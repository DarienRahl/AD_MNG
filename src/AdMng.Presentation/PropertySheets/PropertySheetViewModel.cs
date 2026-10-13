using System.Collections.ObjectModel;
using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.PropertySheets;

/// <summary>A planner finding that is not attached to a single field (e.g. "protected by AdminSDHolder").</summary>
public sealed record SheetIssueViewModel(IssueSeverity Severity, string Text)
{
    public bool IsError => Severity == IssueSeverity.Error;
}

/// <summary>
/// ADUC-style property sheet of one object (modeless window). Pages depend on the object kind (see
/// <see cref="PropertyPageFactory"/>). Values are read live from the session's DC together with the operator's
/// effective permissions; Apply turns the pending changes of all pages into one <see cref="BatchRequest"/> for
/// the object, plans it (findings are shown on the fields), executes it through <see cref="IChangeCoordinator"/>
/// (audit, activity, list refresh) and reloads the object. Nothing is written outside the change pipeline.
/// </summary>
public sealed partial class PropertySheetViewModel : WindowViewModelBase
{
    /// <summary>
    /// Attributes whose planned "old" values legitimately differ from what the sheet showed: userAccountControl
    /// options are merged bit by bit into the current value, pwdLastSet / lockoutTime have write-only semantics.
    /// </summary>
    private static readonly HashSet<string> MergedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        Attr.UserAccountControl, Attr.PwdLastSet, Attr.LockoutTime,
    };

    private readonly IChangePlanner _planner;
    private readonly SerialCancellation _loads = new();
    private FieldContext? _context;
    private bool _applying;

    /// <summary>The object as the pages show it (values the operator based the edits on), and the attributes read.</summary>
    private DirectoryObject? _loaded;
    private HashSet<string> _loadedAttributes = new(StringComparer.OrdinalIgnoreCase);

    public PropertySheetViewModel(DialogServices services, IChangePlanner planner, string distinguishedName)
        : base(services)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        ArgumentException.ThrowIfNullOrWhiteSpace(distinguishedName);
        DistinguishedName = distinguishedName;
        Name = Core.Ldap.DistinguishedName.TryParse(distinguishedName, out var dn) && !dn.IsRoot ? dn.Name : distinguishedName;
        Services.Changes.Changed += OnDirectoryChanged;
    }

    /// <summary>Raised after changes of this object were applied (the host may refresh other views).</summary>
    public event EventHandler<string>? ObjectChanged;

    /// <summary>DN of the object (follows renames/moves made elsewhere in the application).</summary>
    public string DistinguishedName { get; private set; }

    /// <summary>"Jan Kowalski Properties" / "Właściwości: Jan Kowalski".</summary>
    public override string WindowTitle => Localizer.Format(L.PropertySheet.WindowTitle, Name);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial string Name { get; private set; }

    [ObservableProperty]
    public partial ObjectKind Kind { get; private set; }

    [ObservableProperty]
    public partial ObjectIcon Icon { get; private set; } = ObjectIcon.Unknown;

    /// <summary>Localized type ("User", "Security Group - Global").</summary>
    [ObservableProperty]
    public partial string TypeText { get; private set; } = string.Empty;

    /// <summary>Folder of the object ("contoso.test/Firma/IT").</summary>
    [ObservableProperty]
    public partial string Path { get; private set; } = string.Empty;

    /// <summary>Tabs in ADUC order.</summary>
    public ObservableCollection<PropertyPageViewModel> Pages { get; } = [];

    /// <summary>The visible tab (two-way); selecting a lazy tab (Attribute Editor) loads it.</summary>
    [ObservableProperty]
    public partial PropertyPageViewModel? SelectedPage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(OkCommand), nameof(RefreshCommand))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Localized load error (object deleted, access denied, DC unreachable).</summary>
    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(OkCommand), nameof(CancelCommand), nameof(RefreshCommand))]
    public partial bool IsApplying { get; private set; }

    /// <summary>Localized summary why Apply failed or was blocked (details on the fields and in <see cref="Issues"/>).</summary>
    [ObservableProperty]
    public partial string? ApplyError { get; private set; }

    /// <summary>Planner findings that are not shown on a field.</summary>
    public ObservableCollection<SheetIssueViewModel> Issues { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool HasChanges { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool HasErrors { get; private set; }

    /// <summary>The object was changed elsewhere in this application while this sheet had pending changes.</summary>
    [ObservableProperty]
    public partial bool IsOutdated { get; private set; }

    /// <summary>The object no longer exists.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool IsDeleted { get; private set; }

    public override Task InitializeAsync() => LoadAsync();

    /// <summary>Reads the object and fills all pages (discarding pending changes). Never throws.</summary>
    public Task LoadAsync() => LoadCoreAsync(keepMessages: false);

    /// <remarks>The window stays open while changes are being applied (their outcome is shown on the sheet).</remarks>
    public override async Task<bool> CanCloseAsync() =>
        !IsApplying
        && (!HasChanges || await Services.Dialogs.ConfirmAsync(WindowTitle, Localizer[L.PropertySheet.DiscardChanges], destructive: true).ConfigureAwait(true));

    /// <summary>Applies the pending changes. Returns true when everything was written (or there was nothing to write).</summary>
    public async Task<bool> ApplyChangesAsync()
    {
        if (!HasChanges)
        {
            return true;
        }

        ApplyError = null;
        Issues.Clear();
        if (HasErrors)
        {
            ApplyError = Localizer[L.PropertySheet.FixErrors];
            return false;
        }

        var session = Services.Sessions.Current;
        if (session is null)
        {
            ApplyError = Localizer[L.Errors.NotConnected];
            return false;
        }

        // Attribute Editor changes come last so that they win over the same attribute edited on another tab.
        var operations = Pages.Where(p => p is not AttributeEditorPageViewModel)
            .Concat(Pages.OfType<AttributeEditorPageViewModel>())
            .SelectMany(p => p.GetOperations())
            .ToList();
        if (operations.Count == 0)
        {
            await LoadCoreAsync(keepMessages: false).ConfigureAwait(true);
            return true;
        }

        foreach (var page in Pages)
        {
            page.ClearIssues();
        }

        IsApplying = true;
        try
        {
            var title = Localizer.Format(L.PropertySheet.ApplyTitle, Name);
            var request = new BatchRequest([DistinguishedName], operations) { Title = title };
            var plan = await Task.Run(() => _planner.PlanAsync(session, request, new PlanningOptions { MaxDegreeOfParallelism = 1 }, null, CancellationToken.None))
                .ConfigureAwait(true);
            var objectPlan = plan.Objects[0];
            ShowIssues(objectPlan, session);
            if (objectPlan.HasErrors)
            {
                ApplyError = Localizer[L.PropertySheet.ApplyBlocked];
                return false;
            }

            if (objectPlan.IsNoOp)
            {
                await LoadCoreAsync(keepMessages: true).ConfigureAwait(true);
                return true;
            }

            // The planner diffs against fresh values, so a value changed on the DC after this window was opened
            // (by another administrator or tool) would be overwritten silently - ask first.
            var changedOnServer = ChangedOnServer(objectPlan);
            if (changedOnServer.Count > 0)
            {
                IsOutdated = true;
                var names = string.Join(", ", changedOnServer.Select(a => Localizer.AttributeName(a, session.Schema)));
                if (!await Services.Dialogs.ConfirmAsync(WindowTitle, Localizer.Format(L.PropertySheet.ChangedOnServer, names), destructive: true)
                        .ConfigureAwait(true))
                {
                    ApplyError = Localizer[L.PropertySheet.Outdated];
                    return false;
                }
            }

            BatchResult result;
            _applying = true;
            try
            {
                result = await Services.Coordinator.ExecutePlanAsync(plan, title).ConfigureAwait(true);
            }
            finally
            {
                _applying = false;
            }

            var objectResult = result.Objects[0];
            if (objectResult.Status == ObjectResultStatus.Succeeded)
            {
                DistinguishedName = objectResult.FinalDn;
                await LoadCoreAsync(keepMessages: true).ConfigureAwait(true);
                ObjectChanged?.Invoke(this, DistinguishedName);
                return true;
            }

            // Keep the operator's values so the failed part can be corrected and applied again (succeeded steps
            // become no-ops then, because the next plan compares with fresh values).
            ApplyError = objectResult.Status == ObjectResultStatus.Cancelled ? Localizer[L.Errors.Cancelled] : DescribeFailure(objectResult);
            if (objectResult.Status == ObjectResultStatus.PartiallySucceeded)
            {
                ObjectChanged?.Invoke(this, objectResult.FinalDn);
            }

            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ApplyError = Errors.Translate(ex);
            return false;
        }
        finally
        {
            IsApplying = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task ApplyAsync() => ApplyChangesAsync();

    private bool CanApply() => HasChanges && !HasErrors && !IsApplying && !IsLoading && !IsDeleted;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private async Task OkAsync()
    {
        if (!HasChanges || await ApplyChangesAsync().ConfigureAwait(true))
        {
            RequestClose(true);
        }
    }

    private bool CanOk() => !IsApplying && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (await CanCloseAsync().ConfigureAwait(true))
        {
            RequestClose(false);
        }
    }

    private bool CanCancel() => !IsApplying;

    /// <summary>Re-reads the object (asks before discarding pending changes).</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (HasChanges && !await Services.Dialogs.ConfirmAsync(WindowTitle, Localizer[L.PropertySheet.DiscardChanges], destructive: true).ConfigureAwait(true))
        {
            return;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    private bool CanRefresh() => !IsApplying && !IsLoading;

    partial void OnSelectedPageChanged(PropertyPageViewModel? value) => value?.ActivateAsync().Forget();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _loads.Cancel();
            Services.Changes.Changed -= OnDirectoryChanged;
            foreach (var page in Pages)
            {
                page.StateChanged -= OnPageStateChanged;
            }
        }
    }

    private async Task LoadCoreAsync(bool keepMessages)
    {
        var token = _loads.Next();
        var session = Services.Sessions.Current;
        if (session is null)
        {
            LoadError = Localizer[L.Errors.NotConnected];
            return;
        }

        IsLoading = true;
        LoadError = null;
        if (!keepMessages)
        {
            ApplyError = null;
            Issues.Clear();
        }

        try
        {
            if (_context is null || !ReferenceEquals(_context.Session, session))
            {
                _context = new FieldContext(Services, session);
                ReplacePages([]);
            }

            var context = _context;
            var dn = DistinguishedName;
            var attributes = AttributesToRead(Pages);
            var obj = await Task.Run(() => session.GetAsync(dn, attributes, token), token).ConfigureAwait(true);
            if (obj is not null && (Pages.Count == 0 || obj.Kind != Kind))
            {
                // First load (or the object changed its class): build the pages, then read what they need.
                var pages = PropertyPageFactory.Create(context, obj);
                var needed = AttributesToRead(pages);
                if (needed.Except(attributes, StringComparer.OrdinalIgnoreCase).Any())
                {
                    obj = await Task.Run(() => session.GetAsync(dn, needed, token), token).ConfigureAwait(true);
                    attributes = needed;
                }

                ReplacePages(pages);
            }

            if (obj is null)
            {
                IsDeleted = true;
                LoadError = Localizer[L.PropertySheet.NotFound];
                return;
            }

            context.Permissions = await ReadPermissionsAsync(session, obj.DistinguishedName, token).ConfigureAwait(true);
            context.Mandatory = new HashSet<string>(
                session.Schema.GetMandatoryAttributes(obj.GetStrings(Attr.ObjectClass)), StringComparer.OrdinalIgnoreCase);
            token.ThrowIfCancellationRequested();

            _loaded = obj;
            _loadedAttributes = new HashSet<string>(attributes, StringComparer.OrdinalIgnoreCase);
            DistinguishedName = obj.DistinguishedName;
            Name = obj.Name;
            Kind = obj.Kind;
            Icon = ObjectIcons.For(obj);
            TypeText = Localizer.TypeName(obj);
            Path = Core.Ldap.DistinguishedName.TryParse(obj.DistinguishedName, out var parsed) && parsed.Parent is { IsRoot: false } parent
                ? AddressResolver.ToDisplayPath(parent.ToString())
                : string.Empty;
            IsDeleted = false;
            IsOutdated = false;
            foreach (var page in Pages)
            {
                page.Load(obj);
            }

            SelectedPage ??= Pages.FirstOrDefault();
            foreach (var page in Pages)
            {
                await page.LoadDetailsAsync(obj, token).ConfigureAwait(true);
            }

            UpdateState();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a newer load or the window closed.
        }
        catch (DirectoryException ex)
        {
            LoadError = Errors.Translate(ex);
        }
        finally
        {
            if (_loads.IsCurrent(token))
            {
                IsLoading = false;
            }
        }
    }

    private static string[] AttributesToRead(IEnumerable<PropertyPageViewModel> pages) =>
        [.. PropertyPageFactory.BaseAttributes.Concat(pages.SelectMany(p => p.ReadAttributes)).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static async Task<EffectivePermissions> ReadPermissionsAsync(IDirectorySession session, string dn, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => session.GetEffectivePermissionsAsync(dn, cancellationToken), cancellationToken).ConfigureAwait(true);
        }
        catch (DirectoryException)
        {
            // Unknown permissions: fields stay editable and the DC decides on Apply.
            return EffectivePermissions.Unknown;
        }
    }

    private void ReplacePages(IReadOnlyList<PropertyPageViewModel> pages)
    {
        var selectedKey = SelectedPage?.Key;
        foreach (var page in Pages)
        {
            page.StateChanged -= OnPageStateChanged;
        }

        Pages.Clear();
        foreach (var page in pages)
        {
            page.StateChanged += OnPageStateChanged;
            Pages.Add(page);
        }

        SelectedPage = Pages.FirstOrDefault(p => p.Key == selectedKey) ?? Pages.FirstOrDefault();
    }

    private void OnPageStateChanged(object? sender, EventArgs e) => UpdateState();

    private void UpdateState()
    {
        HasChanges = Pages.Any(p => p.HasChanges);
        HasErrors = Pages.Any(p => p.HasErrors);
    }

    private void ShowIssues(ObjectPlan objectPlan, IDirectorySession session)
    {
        var issues = objectPlan.Issues
            .Concat(objectPlan.Changes.SelectMany(c => c.Issues))
            .Where(i => i.Severity != IssueSeverity.Info)
            .Distinct();
        foreach (var issue in issues)
        {
            // Errors are shown on the fields (they block Apply); warnings do not block and must survive the reload
            // after a successful Apply, so they are listed on the sheet.
            var text = Errors.TranslateIssue(issue, session.Schema);
            var shown = false;
            if (issue.Severity == IssueSeverity.Error)
            {
                foreach (var page in Pages)
                {
                    shown |= page.ShowIssue(issue, text);
                }
            }

            if (!shown)
            {
                Issues.Add(new SheetIssueViewModel(issue.Severity, text));
            }
        }

        // Membership steps report the group in TargetDn; name it so the operator knows which group failed.
        foreach (var step in objectPlan.Changes.Where(c => c.Action is ChangeAction.AddMember or ChangeAction.RemoveMember && c.HasErrors))
        {
            var group = Core.Ldap.DistinguishedName.TryParse(step.TargetDn, out var groupDn) && !groupDn.IsRoot ? groupDn.Name : step.TargetDn;
            Issues.Add(new SheetIssueViewModel(IssueSeverity.Error, Localizer.Format(L.PropertySheet.MembershipFailed, group)));
        }
    }

    /// <summary>
    /// Attributes the plan replaces whose current value on the DC differs from the value the operator saw (the
    /// sheet's read, or the Attribute Editor's read for values staged there).
    /// </summary>
    private List<string> ChangedOnServer(ObjectPlan plan)
    {
        var editor = Pages.OfType<AttributeEditorPageViewModel>().FirstOrDefault();
        var changed = new List<string>();
        foreach (var change in plan.Changes.Where(c => c.Action == ChangeAction.Modify).SelectMany(c => c.Attributes))
        {
            if (change.Kind is not (AttributeChangeKind.Replace or AttributeChangeKind.Clear)
                || MergedAttributes.Contains(change.Attribute)
                || changed.Contains(change.Attribute, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var shown = editor?.StagedBaseline(change.Attribute) ?? LoadedValues(change.Attribute);
            if (shown is not null && !SameValues(shown, change.OldValues))
            {
                changed.Add(change.Attribute);
            }
        }

        return changed;
    }

    /// <summary>Values of the sheet's read, or null when the attribute was not read (or only partially - ranged).</summary>
    private IReadOnlyList<string>? LoadedValues(string attribute)
    {
        if (_loaded is null || !_loadedAttributes.Contains(attribute))
        {
            return null;
        }

        if (!_loaded.TryGet(attribute, out var values))
        {
            return [];
        }

        return values.Name.Contains(";range=", StringComparison.OrdinalIgnoreCase) ? null : values.Strings;
    }

    private static bool SameValues(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private string DescribeFailure(ObjectResult result)
    {
        var sb = new StringBuilder(Localizer[L.PropertySheet.ApplyFailed]);
        foreach (var failed in result.Changes.Where(c => !c.Succeeded))
        {
            sb.AppendLine();
            sb.Append("• ").Append(Errors.TranslateFailure(failed.ErrorKind, failed.ErrorMessage));
        }

        return sb.ToString();
    }

    private void OnDirectoryChanged(object? sender, DirectoryChangesEventArgs e)
    {
        if (_applying)
        {
            // Our own Apply; the sheet reloads by itself.
            return;
        }

        var relevant = e.Changes.Where(c => Core.Ldap.DistinguishedName.AreEqual(c.DistinguishedName, DistinguishedName)).ToList();
        if (relevant.Count == 0)
        {
            return;
        }

        _ = Services.Dispatcher.RunAsync(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            foreach (var change in relevant)
            {
                switch (change.Kind)
                {
                    case DirectoryChangeKind.Deleted:
                        IsDeleted = true;
                        return;
                    case DirectoryChangeKind.Moved when change.NewDistinguishedName is { } newDn:
                        DistinguishedName = newDn;
                        break;
                }
            }

            if (HasChanges)
            {
                IsOutdated = true;
            }
            else
            {
                LoadAsync().Forget();
            }
        });
    }
}
