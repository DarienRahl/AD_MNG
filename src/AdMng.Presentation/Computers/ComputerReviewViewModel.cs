using System.Collections;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Computers;

/// <summary>
/// "Przegląd komputerów" (K1 design note 4.5): one paged query over the computer accounts of a scope (domain, OU, saved
/// view, group members – as the account review), streamed and evaluated on the client, tiles per report (the releases
/// report one tile per release), the list of the selected tile with its own columns and a details pane. Owners and
/// creators are resolved after the stream; the exact last logon (every DC) and the site (DNS → subnet) on demand. Every
/// change goes through the change pipeline; results exist only in memory, the parameters, reports and scope are
/// remembered in the settings.
/// </summary>
public sealed partial class ComputerReviewViewModel : WindowViewModelBase
{
    /// <summary>Above this many rows "Ustal lokację" for the whole list asks first.</summary>
    public const int SiteConfirmationThreshold = 500;

    /// <summary>"Dokładne ostatnie logowanie" reads at most this many computers.</summary>
    public const int MaxExactLastLogon = 500;

    private const int FirstBatchSize = 50;
    private const int CreatorBatchSize = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(150);

    private readonly ViewLibrary _views;
    private readonly ICsvCodec _csv;
    private readonly DomainControllerReader _domainControllers;
    private readonly WindowsReleaseCatalog _catalog;
    private readonly ComputerActions _actions;
    private readonly IHostAddressResolver? _resolver;
    private readonly SerialCancellation _runs = new();
    private readonly SerialCancellation _reads = new();
    private readonly Debouncer _refreshDebouncer;
    private readonly Dictionary<string, ComputerReviewRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingRefresh = new(StringComparer.Ordinal);

    // Servers whose owner is still to be resolved and that no other report holds (they may be servers without owner).
    private readonly Dictionary<string, ComputerReviewFacts> _ownerPending = new(StringComparer.Ordinal);
    private RunContext? _run;
    private Task _currentRun = Task.CompletedTask;
    private Task _refreshing = Task.CompletedTask;
    private int _generation;
    private int _readCount;
    private IList? _selectedItems;
    private string _sortColumn = ComputerReviewColumns.Name;
    private bool _sortDescending;

    public ComputerReviewViewModel(
        DialogServices services,
        ViewLibrary views,
        ICsvCodec csv,
        DomainControllerReader domainControllers,
        WindowsReleaseCatalog catalog,
        ComputerActions actions,
        IHostAddressResolver? resolver = null)
        : base(services)
    {
        _views = views ?? throw new ArgumentNullException(nameof(views));
        _csv = csv ?? throw new ArgumentNullException(nameof(csv));
        _domainControllers = domainControllers ?? throw new ArgumentNullException(nameof(domainControllers));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _resolver = resolver;
        _refreshDebouncer = new Debouncer(services.Time, TimeSpan.FromMilliseconds(300));
        var settings = services.Settings.Current.Computers;
        ReportOptions = [.. Enum.GetValues<ComputerReport>().Select(r => new ComputerReportOptionViewModel(r) { IsEnabled = !settings.DisabledReports.Contains(r) })];
        foreach (var option in ReportOptions)
        {
            option.PropertyChanged += (_, _) => RunCommand.NotifyCanExecuteChanged();
        }

        ScopeKinds = CreateScopeKinds();
        ScopeKind = ScopeKinds[0];
        SavedViews = CreateSavedViews();
        LoadParameters(settings.Parameters);
        LoadScope(settings.Scope);
        RefreshTexts();
        Services.Changes.Changed += OnDirectoryChanged;
        _views.Changed += OnViewsChanged;
    }

    public override string WindowTitle => Session is { } session
        ? Localizer.Format(L.ComputerReview.WindowTitleDomain, session.Info.DomainDnsName)
        : Localizer[L.ComputerReview.WindowTitle];

    // ---- Scope ---------------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial IReadOnlyList<Option<AccountReviewScopeKind>> ScopeKinds { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeText), nameof(IsViewScope), nameof(NeedsScopeTarget))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(BrowseScopeCommand))]
    public partial Option<AccountReviewScopeKind> ScopeKind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeText))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? ScopeDn { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Option<string>> SavedViews { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial Option<string>? SavedView { get; set; }

    public bool IsViewScope => ScopeKind.Value == AccountReviewScopeKind.SavedView;

    public bool NeedsScopeTarget => ScopeKind.Value is AccountReviewScopeKind.OrganizationalUnit or AccountReviewScopeKind.GroupMembers;

    public string ScopeText => ScopeKind.Value switch
    {
        AccountReviewScopeKind.Domain => Session?.Info.DomainDnsName ?? string.Empty,
        AccountReviewScopeKind.OrganizationalUnit or AccountReviewScopeKind.GroupMembers => ScopeDn is { } dn
            ? AddressResolver.ToDisplayPath(dn)
            : Localizer[L.ComputerReview.ScopeNotChosen],
        _ => string.Empty,
    };

    // ---- Parameters and reports ----------------------------------------------------------------------------------

    /// <summary>"Nieaktywne od N dni" (also the creation grace of "never logged on").</summary>
    [ObservableProperty]
    public partial int InactiveDays { get; set; }

    /// <summary>"Koniec wsparcia w ciągu N dni".</summary>
    [ObservableProperty]
    public partial int EndingSoonDays { get; set; }

    public IReadOnlyList<ComputerReportOptionViewModel> ReportOptions { get; }

    /// <summary>"Katalog wersji z dnia 27.10.2026".</summary>
    public string CatalogText => Localizer.Format(L.WindowsRelease.CatalogAsOf, WindowsReleaseCatalog.ValidAsOf.ToString("d", Localizer.Culture));

    /// <summary>The catalogue is older than <see cref="WindowsReleaseCatalog.StaleAfterMonths"/> months (a warning is shown).</summary>
    public bool IsCatalogStale =>
        WindowsReleaseCatalog.ValidAsOf.AddMonths(WindowsReleaseCatalog.StaleAfterMonths) < ComputerInventory.LocalDate(Services.Time.GetUtcNow(), Services.Time.LocalTimeZone);

    public string CatalogStaleText => Localizer.Format(L.WindowsRelease.CatalogStale, WindowsReleaseCatalog.ValidAsOf.ToString("d", Localizer.Culture));

    // ---- Run -----------------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(IsBusy))]
    public partial bool IsRunning { get; private set; }

    /// <summary>Owners, creators, the exact last logon or the sites are being read.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial bool IsReading { get; private set; }

    public bool IsBusy => IsRunning || IsReading;

    [ObservableProperty]
    public partial bool HasRun { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? AsOf { get; private set; }

    public BulkObservableCollection<ComputerReportTileViewModel> Tiles { get; } = [];

    [ObservableProperty]
    public partial ComputerReportTileViewModel? SelectedTile { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ComputerReviewColumnViewModel> Columns { get; private set; } = [];

    public BulkObservableCollection<ComputerReviewRowViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial ComputerReviewRowViewModel? SelectedItem { get; set; }

    /// <summary>The list's multi-selection (set by the view).</summary>
    public IList? SelectedItems
    {
        get => _selectedItems;
        set
        {
            _selectedItems = value;
            NotifySelectionChanged();
        }
    }

    public IReadOnlyList<ComputerReviewRowViewModel> Selection =>
        _selectedItems is { Count: > 0 } items ? [.. items.OfType<ComputerReviewRowViewModel>()]
        : SelectedItem is { } row ? [row]
        : [];

    [ObservableProperty]
    public partial string SelectionText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ListHint { get; private set; } = string.Empty;

    /// <summary>The facts of the selected computer (details pane).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ComputerDetailViewModel> Details { get; private set; } = [];

    public bool IsEmpty => HasRun && !IsRunning && SelectedTile is not null && Items.Count == 0;

    public Task Running => _currentRun;

    public Task Refreshing => _refreshing;

    /// <summary>The site columns are shown (sites were resolved in this run).</summary>
    public bool HasSites => _run?.Topology is not null;

    /// <summary>Remote actions, ping and the tools are offered (the host registered them).</summary>
    public bool CanUseRemote => Services.Dialogs is IComputerDialogs;

    public bool CanUseTools => _actions.CanLaunchTools;

    public bool CanResolveSites => _resolver is not null;

    // ---- Commands ------------------------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunAsync() => _currentRun = RunCoreAsync();

    private bool CanRun() => !IsRunning && !IsSessionEnded && Session is not null && ReportOptions.Any(o => o.IsEnabled) && ScopeKind.Value switch
    {
        AccountReviewScopeKind.OrganizationalUnit or AccountReviewScopeKind.GroupMembers => ScopeDn is not null,
        AccountReviewScopeKind.SavedView => SavedView is not null,
        _ => true,
    };

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _runs.Cancel();
        _reads.Cancel();
    }

    private bool CanStop() => IsRunning || IsReading;

    [RelayCommand(CanExecute = nameof(CanBrowseScope))]
    private async Task BrowseScopeAsync()
    {
        if (Session is not { } session)
        {
            return;
        }

        if (ScopeKind.Value == AccountReviewScopeKind.OrganizationalUnit)
        {
            if (await Services.Dialogs.PickContainerAsync(session, ScopeDn).ConfigureAwait(true) is { } container)
            {
                ScopeDn = container;
            }
        }
        else if (ScopeKind.Value == AccountReviewScopeKind.GroupMembers)
        {
            var picked = await Services.Dialogs.PickObjectsAsync(session, [ObjectKind.Group], multiSelect: false).ConfigureAwait(true);
            if (picked.Count > 0)
            {
                ScopeDn = picked[0];
            }
        }
    }

    private bool CanBrowseScope() => NeedsScopeTarget && !IsSessionEnded;

    [RelayCommand]
    private void Sort(string? column)
    {
        if (string.IsNullOrEmpty(column))
        {
            return;
        }

        _sortDescending = column == _sortColumn && !_sortDescending;
        _sortColumn = column;
        RebuildItems();
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync(ComputerReviewRowViewModel? row)
    {
        row ??= SelectedItem;
        return row is null || IsSessionEnded ? Task.CompletedTask : Services.Dialogs.ShowPropertiesAsync(row.Dn);
    }

    private bool CanOpen(ComputerReviewRowViewModel? row) => !IsSessionEnded;

    [RelayCommand(CanExecute = nameof(HasRows))]
    private Task BatchEditAsync()
    {
        var rows = Targets();
        return rows.Count == 0 || Session is null
            ? Task.CompletedTask
            : Services.Dialogs.ShowBatchEditorAsync(new BatchEditorRequest(Dns(rows)) { Title = Localizer[L.Commands.BatchEdit] });
    }

    /// <summary>The list of the selected tile (all rows, in the shown order) to a CSV file the operator chooses.</summary>
    [RelayCommand(CanExecute = nameof(HasItems))]
    private async Task ExportCsvAsync()
    {
        if (SelectedTile is not { } tile || Items.Count == 0)
        {
            return;
        }

        var title = Localizer[L.ComputerReview.ExportCsv];
        var path = await Services.Files.SaveFileAsync(
            title,
            ListExportSnapshot.SuggestFileName(tile.Title, Services.Time.GetLocalNow()),
            [new FileFilter(Localizer[L.Files.CsvFilter], "*.csv")]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        var columns = Columns.Select(c => c.Id).Append(ComputerReviewColumns.DistinguishedName).ToList();
        var headers = columns.Select(c => Localizer[ComputerReviewFormatter.ColumnKey(c)]).ToList();
        var rows = Items.Select(r => (IReadOnlyList<string>)[.. columns.Select(c => r[c])]).ToList();
        var delimiter = CsvFiles.ListSeparator(Services.Regional.Culture);
        try
        {
            await CsvFiles.WriteAsync(path, writer => _csv.Write(writer, headers, rows, delimiter, protectFormulas: true)).ConfigureAwait(true);
            Services.Notifications.Show(NotificationSeverity.Success, Localizer.Format(L.Files.Exported, rows.Count, Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
        }
    }

    /// <summary>"Dokładne ostatnie logowanie": lastLogon of the selection (or the list, at most 500) on every DC.</summary>
    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task ReadExactLastLogonAsync()
    {
        var rows = Targets().Take(MaxExactLastLogon).ToList();
        if (rows.Count == 0 || Session is not { } session || _run is not { } run)
        {
            return;
        }

        var token = _reads.Next();
        IsReading = true;
        StatusText = Localizer.Format(L.ComputerReview.ReadingDomainControllers, rows.Count);
        try
        {
            var reads = await Task.Run(() => _domainControllers.ReadAsync(session, Dns(rows), [Attr.LastLogon], token), token).ConfigureAwait(true);
            if (!ReferenceEquals(run, _run))
            {
                return;
            }

            var answered = reads.Where(r => r.Succeeded).ToList();
            var active = 0;
            foreach (var target in rows)
            {
                // A computer deleted or moved during the read stays gone; one re-read meanwhile keeps its new facts.
                if (!_rows.TryGetValue(DnKey.For(target.Dn), out var row))
                {
                    continue;
                }

                var best = answered
                    .Select(r => (r.Server, Time: r.Find(row.Dn)?.GetFileTime(Attr.LastLogon)))
                    .Where(v => v.Time is not null)
                    .OrderByDescending(v => v.Time)
                    .FirstOrDefault();
                var before = row.Reports.Contains(ComputerReport.Inactive) || row.Reports.Contains(ComputerReport.NeverLoggedOn);
                var updated = row.Facts with
                {
                    ExactLastLogon = best.Time,
                    ExactLastLogonServer = best.Time is null ? null : best.Server,
                    HasExactLastLogon = true,
                };
                Place(run, updated with { Status = run.Evaluate(updated) });
                var after = _rows.TryGetValue(DnKey.For(row.Dn), out var current)
                    && (current.Reports.Contains(ComputerReport.Inactive) || current.Reports.Contains(ComputerReport.NeverLoggedOn));
                if (before && !after)
                {
                    active++;
                }
            }

            UpdateTiles(complete: true);
            RebuildItems();
            var failed = reads.Where(r => !r.Succeeded).ToList();
            var message = Localizer.Format(L.ComputerReview.ExactDone, answered.Count, reads.Count, active);
            if (failed.Count > 0)
            {
                message += Environment.NewLine + string.Join(Environment.NewLine, failed.Select(f => $"{f.Server}: {f.Error}"));
            }

            Services.Notifications.Show(failed.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success, message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopped by the operator or a new run.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Services.Dialogs.ShowErrorAsync(Localizer[L.ComputerReview.ExactLastLogon], Errors.Translate(ex)).ConfigureAwait(true);
        }
        finally
        {
            IsReading = false;
            UpdateStatus();
        }
    }

    /// <summary>
    /// "Ustal lokację": the DNS name of each computer of the selection (or the list, confirmed above 500) through the
    /// workstation's resolver, the address against the subnets of the forest (longest prefix); a DC's site is its server
    /// object's. Results in memory only.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanResolve))]
    private async Task ResolveSitesAsync()
    {
        var rows = Targets();
        if (rows.Count == 0 || Session is not { } session || _run is not { } run || _resolver is not { } resolver)
        {
            return;
        }

        if (Selection.Count == 0 && rows.Count > SiteConfirmationThreshold
            && !await Services.Dialogs.ConfirmAsync(Localizer[L.ComputerReview.ResolveSites], Localizer.Format(L.ComputerReview.ResolveSitesConfirm, rows.Count), destructive: false).ConfigureAwait(true))
        {
            return;
        }

        var token = _reads.Next();
        IsReading = true;
        StatusText = Localizer.Format(L.ComputerReview.ResolvingSites, rows.Count);
        try
        {
            run.Topology ??= await Task.Run(() => ComputerSites.ReadTopologyAsync(session, token), token).ConfigureAwait(true);
            var topology = run.Topology;
            var resolved = await Task.Run(() => ComputerSites.ResolveAsync(resolver, topology, rows.Select(r => r.Facts.Facts).ToList(), token), token).ConfigureAwait(true);
            if (!ReferenceEquals(run, _run))
            {
                return;
            }

            foreach (var (key, site) in resolved)
            {
                if (_rows.TryGetValue(key, out var row))
                {
                    row.Update(row.Facts with { Site = site }, row.Reports);
                }
            }

            Columns = CreateColumns();
            OnPropertyChanged(nameof(HasSites));
            UpdateDetails();
            if (topology.Topology.Subnets.Invalid.Count > 0 && !run.InvalidSubnetsReported)
            {
                run.InvalidSubnetsReported = true;
                Services.Notifications.Show(NotificationSeverity.Warning, Localizer.Format(L.ComputerReview.InvalidSubnets, string.Join(", ", topology.Topology.Subnets.Invalid)));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopped.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Services.Dialogs.ShowErrorAsync(Localizer[L.ComputerReview.ResolveSites], Errors.Translate(ex)).ConfigureAwait(true);
        }
        finally
        {
            IsReading = false;
            UpdateStatus();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectionForChange))]
    private async Task DisableAsync()
    {
        var rows = Selection;
        if (rows.Count == 0 || Session is null)
        {
            return;
        }

        var title = Localizer[L.Commands.Disable];
        if (!await Services.Dialogs.ConfirmAsync(title, ConfirmText(L.ComputerReview.DisableConfirmOne, L.ComputerReview.DisableConfirmMany, rows), destructive: true).ConfigureAwait(true))
        {
            return;
        }

        await Services.Coordinator.ApplyAsync(new ChangeRequest(Dns(rows), [new SetAccountEnabledOperation(false)], title)).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasSelectionForChange))]
    private async Task MoveAsync()
    {
        var rows = Selection;
        if (rows.Count == 0 || Session is not { } session)
        {
            return;
        }

        var target = await Services.Dialogs.PickContainerAsync(session, rows[0].Facts.Facts.ParentDn).ConfigureAwait(true);
        if (target is null)
        {
            return;
        }

        await Services.Coordinator.ApplyAsync(new ChangeRequest(Dns(rows), [new MoveOperation(target)], Localizer[L.Commands.Move])).ConfigureAwait(true);
    }

    /// <summary>"Usuń…": the domain typed; computers with child objects (BitLocker recovery) are deleted with their subtree.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectionForChange))]
    private async Task DeleteAsync()
    {
        var rows = Selection;
        if (rows.Count == 0 || Session is not { } session)
        {
            return;
        }

        var title = Localizer[L.Commands.Delete];
        bool withChildren;
        try
        {
            withChildren = await Task.Run(() => AnyHasChildrenAsync(session, Dns(rows))).ConfigureAwait(true);
        }
        catch (DirectoryException ex)
        {
            await Services.Dialogs.ShowErrorAsync(title, Errors.Translate(ex)).ConfigureAwait(true);
            return;
        }

        var domain = ConfirmationDomain;
        var message = ConfirmText(L.ComputerReview.DeleteConfirmOne, L.ComputerReview.DeleteConfirmMany, rows);
        if (withChildren)
        {
            message += Environment.NewLine + Environment.NewLine + Localizer[L.ComputerReview.DeleteChildrenWarning];
        }

        message += Environment.NewLine + Localizer.Format(L.Computers.TypeDomainToConfirm, domain);
        if (await Services.Dialogs.ConfirmTypedAsync(title, message, domain).ConfigureAwait(true))
        {
            await Services.Coordinator.ApplyAsync(new ChangeRequest(Dns(rows), [new DeleteOperation(IncludeSubtree: withChildren)], title)).ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectionForChange))]
    private Task ResetAccountAsync() =>
        Session is { } session ? _actions.ResetAccountsAsync(session, Dns(Selection)) : Task.CompletedTask;

    /// <summary>"Ustaw właściciela…": managedBy from the object picker (users, groups, contacts).</summary>
    [RelayCommand(CanExecute = nameof(HasSelectionForChange))]
    private async Task SetOwnerAsync()
    {
        var rows = Selection;
        if (rows.Count == 0 || Session is not { } session)
        {
            return;
        }

        var picked = await Services.Dialogs.PickObjectsAsync(session, [ObjectKind.User, ObjectKind.Group, ObjectKind.Contact], multiSelect: false).ConfigureAwait(true);
        if (picked.Count == 0)
        {
            return;
        }

        await Services.Coordinator.ApplyAsync(new ChangeRequest(Dns(rows), [new SetValuesOperation(Attr.ManagedBy, [picked[0]])], Localizer[L.ComputerReview.SetOwner])).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private Task RemoteActionsAsync() =>
        Services.Dialogs is IComputerDialogs dialogs && SelectedItem is { } row ? dialogs.ShowRemoteActionsAsync(row.Dn) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private Task RemoteDesktopAsync() =>
        Session is { } session && SelectedItem is { } row ? _actions.LaunchAsync(session, LocalTool.RemoteDesktop, row.Dn) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private Task PingAsync() =>
        Session is { } session && SelectedItem is { } row ? _actions.PingAsync(session, row.Dn) : Task.CompletedTask;

    [RelayCommand]
    private void Close() => RequestClose(true);

    private bool HasRows() => Items.Count > 0 && !IsSessionEnded;

    private bool HasItems() => Items.Count > 0;

    private bool CanRead() => !IsRunning && !IsReading && !IsSessionEnded && Items.Count > 0;

    private bool CanResolve() => CanRead() && _resolver is not null;

    private bool HasSelectionForChange() => Selection.Count > 0 && !IsSessionEnded && !IsRunning;

    private bool HasSingleSelection() => Selection.Count == 1 && !IsSessionEnded;

    /// <summary>Called by the view when the list's selection changed.</summary>
    public void NotifySelectionChanged()
    {
        var count = Selection.Count;
        SelectionText = count > 1 ? Localizer.Format(L.ComputerReview.Selected, count) : string.Empty;
        UpdateDetails();
        RefreshCommandStates();
    }

    // ---- Lifetime --------------------------------------------------------------------------------------------------

    public override Task InitializeAsync()
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ScopeText));
        RunCommand.NotifyCanExecuteChanged();
        return Task.CompletedTask;
    }

    protected override void OnSessionEnded()
    {
        _runs.Cancel();
        _reads.Cancel();
        _refreshDebouncer.Cancel();
        RunCommand.NotifyCanExecuteChanged();
        BrowseScopeCommand.NotifyCanExecuteChanged();
        RefreshCommandStates();
        OnPropertyChanged(nameof(WindowTitle));
    }

    protected override void OnCultureChanged()
    {
        base.OnCultureChanged();
        var kind = ScopeKind.Value;
        ScopeKinds = CreateScopeKinds();
        ScopeKind = ScopeKinds.First(k => k.Value == kind);
        RefreshTexts();
        if (_run is { } run)
        {
            run.Formatter = new ComputerReviewFormatter(Localizer, run.TimeZone);
            foreach (var row in _rows.Values)
            {
                row.UseFormatter(run.Formatter);
            }
        }

        OnPropertyChanged(nameof(CatalogText));
        OnPropertyChanged(nameof(CatalogStaleText));
        UpdateTiles(complete: !IsRunning);
        UpdateStatus();
        NotifySelectionChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runs.Cancel();
            _reads.Cancel();
            _refreshDebouncer.Cancel();
            Services.Changes.Changed -= OnDirectoryChanged;
            _views.Changed -= OnViewsChanged;
        }
    }

    partial void OnScopeKindChanged(Option<AccountReviewScopeKind> oldValue, Option<AccountReviewScopeKind> newValue)
    {
        if (oldValue is not null && oldValue.Value != newValue.Value)
        {
            ScopeDn = null;
        }
    }

    partial void OnSelectedItemChanged(ComputerReviewRowViewModel? value) => NotifySelectionChanged();

    partial void OnSelectedTileChanged(ComputerReportTileViewModel? value)
    {
        foreach (var tile in Tiles)
        {
            tile.IsSelected = ReferenceEquals(tile, value);
        }

        Columns = CreateColumns();
        ListHint = value is null ? string.Empty : Localizer[ComputerReviewFormatter.ReportHintKey(value.Report)];
        RebuildItems();
    }

    // ---- Run -------------------------------------------------------------------------------------------------------

    private async Task RunCoreAsync()
    {
        var token = _runs.Next();
        _reads.Cancel();
        var generation = ++_generation;
        _rows.Clear();
        _pendingRefresh.Clear();
        _ownerPending.Clear();
        _readCount = 0;
        Items.ReplaceAll([]);
        Error = null;
        var session = Session;
        if (session is null)
        {
            Error = NoSessionMessage;
            return;
        }

        var reports = ReportOptions.Where(o => o.IsEnabled).Select(o => o.Report).ToHashSet();
        var parameters = CurrentParameters();
        LoadParameters(parameters);
        await RememberAsync(parameters).ConfigureAwait(true);
        var now = Services.Time.GetUtcNow();
        var zone = Services.Time.LocalTimeZone;
        var previous = SelectedTile is { } selected ? (selected.Report, selected.Release) : ((ComputerReport, string?)?)null;
        Tiles.ReplaceAll(reports.Where(r => r != ComputerReport.Releases).Order().Select(r => new ComputerReportTileViewModel(r)));
        RefreshTexts();
        HasRun = true;
        AsOf = now;
        IsRunning = true;
        var stopped = false;
        try
        {
            var settings = Services.Settings.Current.Computers;
            var wellKnown = await Services.Sessions.GetWellKnownObjectsAsync(token).ConfigureAwait(true);
            var nc = session.Info.DefaultNamingContext;
            var placement = new PlacementRules(
                wellKnown.Find(WellKnownContainer.Computers) ?? Core.Ldap.DistinguishedName.Parse(nc).Child("CN", "Computers").ToString(),
                wellKnown.Find(WellKnownContainer.DomainControllers) ?? Core.Ldap.DistinguishedName.Parse(nc).Child("OU", "Domain Controllers").ToString(),
                settings.PlacementRules.ToDictionary(
                    r => r.Type,
                    r => (IReadOnlyList<string>)[.. r.Containers.Select(c => PlacementRules.Resolve(c, nc)).OfType<string>()]));
            var options = new ComputerInventoryOptions
            {
                Catalog = _catalog,
                EndingSoonDays = parameters.EndingSoonDays,
                Overrides = settings.CatalogOverrides(),
                Placement = placement,
            };
            var target = await Task.Run(() => ResolveScopeAsync(session, now, token), token).ConfigureAwait(true);
            var run = new RunContext(session, now, zone, parameters, reports, options, new ComputerReviewFormatter(Localizer, zone));
            _run = run;
            OnPropertyChanged(nameof(HasSites));
            Columns = CreateColumns();
            var search = new DirectorySearch
            {
                BaseDn = target.BaseDn,
                Scope = target.Scope,
                Filter = ComputerReports.Filter(reports, target.Filter, parameters, now),
                Attributes = ComputerFacts.Attributes(session.Schema),
                PageSize = Services.Settings.Current.PageSize,
            };
            await StreamAsync(run, search, generation, token).ConfigureAwait(true);
            if (generation == _generation)
            {
                List<ComputerReviewFacts> candidates = [.. _rows.Values.Select(r => r.Facts), .. _ownerPending.Values];
                _ownerPending.Clear();
                var resolved = await Task.Run(() => ResolveOwnersAndCreatorsAsync(run, candidates, token), token).ConfigureAwait(true);
                if (generation == _generation)
                {
                    foreach (var facts in resolved)
                    {
                        Place(run, facts);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            stopped = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation == _generation)
            {
                Error = ex is ScopeException scope ? scope.Message : Errors.Translate(ex);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsRunning = false;
                UpdateTiles(complete: !stopped);
                SelectedTile = (previous is { } p ? Tiles.FirstOrDefault(t => t.Report == p.Item1 && t.Release == p.Item2) : null) ?? Tiles.FirstOrDefault();
                RebuildItems();
                UpdateStatus(stopped);
            }
        }
    }

    private async Task<ReviewScope> ResolveScopeAsync(IDirectorySession session, DateTimeOffset now, CancellationToken token)
    {
        var nc = session.Info.DefaultNamingContext;
        switch (ScopeKind.Value)
        {
            case AccountReviewScopeKind.OrganizationalUnit:
                return new ReviewScope(ScopeDn ?? throw new ScopeException(Localizer[L.ComputerReview.ScopeNotChosen]), SearchScope.Subtree, null);
            case AccountReviewScopeKind.SavedView:
                var view = (SavedView is { } option ? _views.FindView(option.Value) : null) ?? throw new ScopeException(Localizer[L.ComputerReview.ViewNotFound]);
                var filter = QuickFilters.Combine(LdapFilter.Wrap(view.Filter), view.QuickFilters ?? [], now, Services.Settings.Current.InactiveDays, null);
                return new ReviewScope(view.ResolveBaseDn(nc), view.Scope, filter);
            case AccountReviewScopeKind.GroupMembers:
                var groupDn = ScopeDn ?? throw new ScopeException(Localizer[L.ComputerReview.ScopeNotChosen]);
                var group = await session.GetAsync(groupDn, [Attr.ObjectSid], token).ConfigureAwait(false)
                    ?? throw new ScopeException(Localizer.Format(L.ComputerReview.GroupNotFound, AddressResolver.ToDisplayPath(groupDn)));
                return new ReviewScope(nc, SearchScope.Subtree, AccountReviewQuery.GroupMembersFilter(group.DistinguishedName, AdConvert.GetRid(group.ObjectSid)));
            default:
                return new ReviewScope(nc, SearchScope.Subtree, null);
        }
    }

    private Task StreamAsync(RunContext run, DirectorySearch search, int generation, CancellationToken token)
    {
        var time = Services.Time;
        return Task.Run(
            async () =>
            {
                var buffer = new List<ComputerReviewFacts>(FirstBatchSize);
                var read = 0;
                var lastFlush = time.GetTimestamp();
                try
                {
                    await foreach (var obj in run.Session.SearchAsync(search, token).WithCancellation(token).ConfigureAwait(false))
                    {
                        read++;
                        buffer.Add(run.Create(obj));
                        if (buffer.Count >= FirstBatchSize || time.GetElapsedTime(lastFlush) >= FlushInterval)
                        {
                            await FlushAsync(run, buffer, read, generation).ConfigureAwait(false);
                            buffer = new List<ComputerReviewFacts>(FirstBatchSize);
                            lastFlush = time.GetTimestamp();
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    await FlushAsync(run, buffer, read, generation).ConfigureAwait(false);
                    throw;
                }

                await FlushAsync(run, buffer, read, generation).ConfigureAwait(false);
            },
            token);
    }

    private Task FlushAsync(RunContext run, List<ComputerReviewFacts> batch, int read, int generation) => Services.Dispatcher.InvokeAsync(() =>
    {
        if (generation != _generation || IsDisposed)
        {
            return;
        }

        _readCount = read;
        foreach (var facts in batch)
        {
            Place(run, facts);
        }

        UpdateTiles(complete: false);
        RebuildItems();
        UpdateStatus();
    });

    /// <summary>The owners (managedBy: missing, present, disabled or deleted) and the creators' names of the computers.</summary>
    private static async Task<List<ComputerReviewFacts>> ResolveOwnersAndCreatorsAsync(RunContext run, List<ComputerReviewFacts> rows, CancellationToken token)
    {
        var owners = await DirectoryLookup.ReadManyAsync(
            run.Session, rows.Select(r => r.Facts.ManagedBy).OfType<string>(), [Attr.ObjectClass, Attr.UserAccountControl, Attr.Name], token).ConfigureAwait(false);
        var sids = rows.Select(r => r.Facts.CreatorSidValue).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var creators = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in sids.Chunk(CreatorBatchSize))
        {
            var search = new DirectorySearch
            {
                BaseDn = run.Session.Info.DefaultNamingContext,
                Scope = SearchScope.Subtree,
                Filter = LdapFilter.Or(chunk.Select(s => LdapFilter.Equal(Attr.ObjectSid, s))),
                Attributes = [Attr.ObjectSid, Attr.Name, Attr.SamAccountName],
            };
            await foreach (var principal in run.Session.SearchAsync(search, token).ConfigureAwait(false))
            {
                if (principal.ObjectSid is { } sid)
                {
                    creators[sid] = principal.GetString(Attr.SamAccountName) is { Length: > 0 } login
                        ? $"{run.Session.Info.DomainNetBiosName ?? run.Session.Info.DomainDnsName}\\{login}"
                        : principal.Name;
                }
            }
        }

        return [.. rows.Select(r => r with
        {
            Owner = r.Facts.ManagedBy is not { } owner ? OwnerState.Missing
                : owners.TryGetValue(DnKey.For(owner), out var found) ? (found.IsDisabled ? OwnerState.Disabled : OwnerState.Present)
                : IsInDomainOf(run.Session, owner) ? OwnerState.Deleted
                : OwnerState.Present,
            CreatorName = r.Facts.CreatorSidValue is { } creator && creators.TryGetValue(creator, out var name) ? name : null,
        })];
    }

    /// <summary>
    /// Whether <paramref name="distinguishedName"/> belongs to the session's own domain, the only one the owner search reads.
    /// An owner in another domain of the forest (also a child domain, whose DN ends with this one's) is not checked, so it is
    /// never reported as deleted.
    /// </summary>
    private static bool IsInDomainOf(IDirectorySession session, string distinguishedName) =>
        !DistinguishedName.TryParse(distinguishedName, out var dn)
        || !DistinguishedName.TryParse(session.Info.DefaultNamingContext, out var domain)
        || dn.DomainComponent.Equals(domain);

    /// <summary>Adds, updates or removes the row of an evaluated computer.</summary>
    private void Place(RunContext run, ComputerReviewFacts facts)
    {
        var key = DnKey.For(facts.DistinguishedName);
        if (_rows.TryGetValue(key, out var existing))
        {
            // What was resolved later survives a re-read of the computer.
            facts = facts with
            {
                Owner = facts.Owner != OwnerState.Unknown || !string.Equals(existing.Facts.Facts.ManagedBy, facts.Facts.ManagedBy, StringComparison.OrdinalIgnoreCase)
                    ? facts.Owner
                    : existing.Facts.Owner,
                CreatorName = facts.CreatorName ?? existing.Facts.CreatorName,
                Site = facts.Site ?? existing.Facts.Site,
            };
            if (!facts.HasExactLastLogon && existing.Facts.HasExactLastLogon)
            {
                facts = facts with
                {
                    HasExactLastLogon = true,
                    ExactLastLogon = existing.Facts.ExactLastLogon,
                    ExactLastLogonServer = existing.Facts.ExactLastLogonServer,
                };
                facts = facts with { Status = run.Evaluate(facts) };
            }
        }

        var reports = run.Reports.Where(r => ComputerReports.Matches(r, facts.Facts, facts.Status, facts.Owner, run.Parameters)).ToHashSet();
        if (reports.Count == 0 && existing is null && facts.Owner == OwnerState.Unknown
            && run.Reports.Contains(ComputerReport.ServersWithoutOwner) && facts.Facts.IsEnabled && facts.Facts.Type == ComputerType.Server)
        {
            _ownerPending[key] = facts;
            return;
        }

        _ownerPending.Remove(key);
        if (reports.Count == 0)
        {
            if (existing is not null)
            {
                _rows.Remove(key);
                Items.Remove(existing);
            }

            return;
        }

        if (existing is not null)
        {
            existing.Update(facts, reports);
            return;
        }

        _rows[key] = new ComputerReviewRowViewModel(facts, reports, run.Formatter);
    }

    // ---- Refresh after changes ---------------------------------------------------------------------------------

    private void OnDirectoryChanged(object? sender, DirectoryChangesEventArgs e) => _ = Services.Dispatcher.RunAsync(() =>
    {
        if (IsDisposed || _run is null)
        {
            return;
        }

        var changed = false;
        foreach (var change in e.Changes)
        {
            var key = DnKey.For(change.DistinguishedName);
            if (!_rows.TryGetValue(key, out var row))
            {
                continue;
            }

            if (change.Kind == DirectoryChangeKind.Deleted)
            {
                _rows.Remove(key);
                Items.Remove(row);
                changed = true;
            }
            else if (change.Kind == DirectoryChangeKind.Moved && change.NewDistinguishedName is { } moved)
            {
                _rows.Remove(key);
                Items.Remove(row);
                _pendingRefresh.Add(moved);
                changed = true;
            }
            else
            {
                _pendingRefresh.Add(row.Dn);
            }
        }

        if (changed)
        {
            UpdateTiles(complete: !IsRunning);
        }

        if (_pendingRefresh.Count > 0)
        {
            _refreshing = _refreshDebouncer.RunAsync(RefreshPendingAsync);
        }
    });

    private async Task RefreshPendingAsync(CancellationToken token)
    {
        if (_run is not { } run || Session is not { } session || !ReferenceEquals(session, run.Session) || IsRunning)
        {
            _pendingRefresh.Clear();
            return;
        }

        var dns = _pendingRefresh.ToList();
        _pendingRefresh.Clear();
        try
        {
            var found = await Task.Run(() => DirectoryLookup.ReadManyAsync(session, dns, ComputerFacts.Attributes(session.Schema), token), token).ConfigureAwait(true);
            var evaluated = found.Values.Select(run.Create).ToList();
            var resolved = await Task.Run(() => ResolveOwnersAndCreatorsAsync(run, evaluated, token), token).ConfigureAwait(true);
            if (!ReferenceEquals(run, _run))
            {
                return;
            }

            foreach (var dn in dns.Where(d => !found.ContainsKey(DnKey.For(d))))
            {
                if (_rows.Remove(DnKey.For(dn), out var gone))
                {
                    Items.Remove(gone);
                }
            }

            foreach (var facts in resolved)
            {
                Place(run, facts);
            }

            UpdateTiles(complete: true);
            RebuildItems();
            UpdateStatus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded or closed.
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException)
        {
            Services.Notifications.Show(NotificationSeverity.Warning, Errors.Translate(ex));
        }
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private static async Task<bool> AnyHasChildrenAsync(IDirectorySession session, IReadOnlyList<string> dns)
    {
        foreach (var dn in dns)
        {
            var search = new DirectorySearch { BaseDn = dn, Scope = SearchScope.OneLevel, SizeLimit = 1, PageSize = 1 };
            await foreach (var _ in session.SearchAsync(search, CancellationToken.None).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private string ConfirmText(string oneKey, string manyKey, IReadOnlyList<ComputerReviewRowViewModel> rows) =>
        rows.Count == 1 ? Localizer.Format(oneKey, rows[0].Name, ConfirmationDomain) : Localizer.Format(manyKey, rows.Count, ConfirmationDomain);

    private IReadOnlyList<ComputerReviewRowViewModel> Targets() => Selection is { Count: > 0 } selection ? selection : [.. Items];

    private static IReadOnlyList<string> Dns(IEnumerable<ComputerReviewRowViewModel> rows) => [.. rows.Select(r => r.Dn)];

    private ComputerReportParameters CurrentParameters() => new ComputerReportParameters
    {
        InactiveDays = InactiveDays,
        EndingSoonDays = EndingSoonDays,
    }.Normalized();

    private void LoadParameters(ComputerReportParameters parameters)
    {
        var normalized = parameters.Normalized();
        InactiveDays = normalized.InactiveDays;
        EndingSoonDays = normalized.EndingSoonDays;
    }

    private AccountReviewScope CurrentScope()
    {
        var nc = Session?.Info.DefaultNamingContext;
        return new AccountReviewScope
        {
            Kind = ScopeKind.Value,
            Dn = ScopeDn is { } dn && NeedsScopeTarget ? (nc is null ? dn : Views.SavedView.ToPortableDn(dn, nc)) : null,
            ViewId = IsViewScope ? SavedView?.Value : null,
        };
    }

    private void LoadScope(AccountReviewScope scope)
    {
        ScopeKind = ScopeKinds.FirstOrDefault(k => k.Value == scope.Kind) ?? ScopeKinds[0];
        var nc = Session?.Info.DefaultNamingContext;
        ScopeDn = NeedsScopeTarget && scope.Dn is { } dn ? FromPortable(dn, nc) : null;
        SavedView = IsViewScope ? SavedViews.FirstOrDefault(v => v.Value == scope.ViewId) : null;
    }

    private static string? FromPortable(string dn, string? nc)
    {
        var placeholder = dn.Contains(Views.SavedView.DomainPlaceholder, StringComparison.OrdinalIgnoreCase);
        if (nc is null)
        {
            return placeholder ? null : dn;
        }

        var resolved = placeholder ? dn.Replace(Views.SavedView.DomainPlaceholder, nc, StringComparison.OrdinalIgnoreCase) : dn;
        return Core.Ldap.DistinguishedName.TryParse(resolved, out _) ? resolved : null;
    }

    private Task RememberAsync(ComputerReportParameters parameters)
    {
        var disabled = ReportOptions.Where(o => !o.IsEnabled).Select(o => o.Report).ToList();
        var scope = CurrentScope();
        return Services.Settings.UpdateAsync(s =>
        {
            s.Computers.Parameters = parameters;
            s.Computers.DisabledReports = disabled;
            s.Computers.Scope = scope;
        });
    }

    private void OnViewsChanged(object? sender, EventArgs e) => _ = Services.Dispatcher.RunAsync(() =>
    {
        var selected = SavedView?.Value;
        SavedViews = CreateSavedViews();
        SavedView = SavedViews.FirstOrDefault(v => v.Value == selected);
    });

    private List<Option<AccountReviewScopeKind>> CreateScopeKinds() =>
        [.. Enum.GetValues<AccountReviewScopeKind>().Select(k => new Option<AccountReviewScopeKind>(k, Localizer[$"AccountReview.Scope.{k}"]))];

    private List<Option<string>> CreateSavedViews() => [.. _views.SavedViews.Select(v => new Option<string>(v.Id, v.Name))];

    private IReadOnlyList<ComputerReviewColumnViewModel> CreateColumns() => SelectedTile is { } tile
        ? [.. ComputerReviewColumns.For(tile.Report, HasSites).Select(c => new ComputerReviewColumnViewModel(c, Localizer[ComputerReviewFormatter.ColumnKey(c)]))]
        : [];

    private void RefreshTexts()
    {
        foreach (var option in ReportOptions)
        {
            option.Title = Localizer[ComputerReviewFormatter.ReportKey(option.Report)];
        }

        foreach (var tile in Tiles)
        {
            tile.Title = tile.Release ?? Localizer[ComputerReviewFormatter.ReportKey(tile.Report)];
            tile.AreaTitle = Localizer[ComputerReviewFormatter.AreaKey(tile.Area)];
            tile.Hint = Localizer[ComputerReviewFormatter.ReportHintKey(tile.Report)];
        }

        if (SelectedTile is { } selected)
        {
            Columns = CreateColumns();
            ListHint = Localizer[ComputerReviewFormatter.ReportHintKey(selected.Report)];
        }

        OnPropertyChanged(nameof(ScopeText));
    }

    /// <summary>The counts of the tiles; the releases report gets one tile per release group (most computers first).</summary>
    private void UpdateTiles(bool complete)
    {
        if (_run is { } run && run.Reports.Contains(ComputerReport.Releases))
        {
            var groups = _rows.Values.Where(r => r.Reports.Contains(ComputerReport.Releases))
                .GroupBy(r => r.ReleaseGroup, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Create(Localizer.Culture, ignoreCase: true))
                .Select(g => g.Key)
                .ToList();
            var existing = Tiles.Where(t => t.Report == ComputerReport.Releases).ToList();
            foreach (var stale in existing.Where(t => !groups.Contains(t.Release!, StringComparer.Ordinal)).ToList())
            {
                Tiles.Remove(stale);
                if (ReferenceEquals(SelectedTile, stale))
                {
                    SelectedTile = null;
                }
            }

            foreach (var group in groups.Where(g => !existing.Any(t => string.Equals(t.Release, g, StringComparison.Ordinal))))
            {
                Tiles.Add(new ComputerReportTileViewModel(ComputerReport.Releases, group)
                {
                    Title = group,
                    AreaTitle = Localizer[ComputerReviewFormatter.AreaKey(ComputerReportArea.System)],
                    Hint = Localizer[ComputerReviewFormatter.ReportHintKey(ComputerReport.Releases)],
                });
            }
        }

        foreach (var tile in Tiles)
        {
            tile.SetCount(_rows.Values.Count(tile.Contains), complete, Localizer.Culture);
        }
    }

    private void RebuildItems()
    {
        var selected = Selection.Select(r => r.Dn).ToHashSet(StringComparer.Ordinal);
        if (SelectedTile is not { } tile)
        {
            Items.ReplaceAll([]);
        }
        else
        {
            var compare = Localizer.Culture.CompareInfo;
            var rows = _rows.Values.Where(tile.Contains).ToList();
            var column = _sortColumn;
            var keys = rows.ToDictionary(r => r, r => r.SortKey(column));
            rows.Sort((a, b) =>
            {
                var x = keys[a];
                var y = keys[b];
                var result = x is string sx && y is string sy
                    ? compare.Compare(sx, sy, CompareOptions.IgnoreCase)
                    : Comparer<object>.Default.Compare(x, y);
                if (_sortDescending)
                {
                    result = -result;
                }

                return result != 0 ? result : compare.Compare(a.Name, b.Name, CompareOptions.IgnoreCase);
            });
            Items.ReplaceAll(rows);
        }

        SelectedItem = Items.FirstOrDefault(r => selected.Contains(r.Dn));
        OnPropertyChanged(nameof(IsEmpty));
        NotifySelectionChanged();
    }

    private void RefreshCommandStates()
    {
        BatchEditCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        ReadExactLastLogonCommand.NotifyCanExecuteChanged();
        ResolveSitesCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        MoveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ResetAccountCommand.NotifyCanExecuteChanged();
        SetOwnerCommand.NotifyCanExecuteChanged();
        RemoteActionsCommand.NotifyCanExecuteChanged();
        RemoteDesktopCommand.NotifyCanExecuteChanged();
        PingCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsReadingChanged(bool value) => RefreshCommandStates();

    partial void OnIsRunningChanged(bool value) => RefreshCommandStates();

    private void UpdateStatus(bool stopped = false)
    {
        if (!HasRun || AsOf is not { } asOf)
        {
            StatusText = string.Empty;
            return;
        }

        var time = TimeZoneInfo.ConvertTime(asOf, Services.Time.LocalTimeZone).ToString("t", Localizer.Culture);
        var key = IsRunning ? L.ComputerReview.StatusRunning : stopped ? L.ComputerReview.StatusStopped : L.ComputerReview.Status;
        StatusText = Localizer.Format(key, time, _readCount.ToString("N0", Localizer.Culture), _rows.Count.ToString("N0", Localizer.Culture));
    }

    /// <summary>The details pane: the facts, the findings and the catalogue row of the selected computer.</summary>
    private void UpdateDetails()
    {
        if (Selection is not [var row] || _run is not { } run)
        {
            Details = [];
            return;
        }

        var formatter = run.Formatter;
        var r = row.Facts;
        var f = r.Facts;
        var details = new List<ComputerDetailViewModel>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                details.Add(new ComputerDetailViewModel(Localizer[key], value));
            }
        }

        Add(L.ComputerReview.DetailName, f.Name);
        Add(L.ComputerReview.DetailDnsName, f.DnsHostName);
        Add(L.ComputerReview.DetailType, Localizer[ComputerReviewFormatter.TypeKey(f.Type)]);
        Add(L.ComputerReview.DetailEnabled, Localizer[f.IsEnabled ? L.Common.Yes : L.Common.No]);
        Add(L.ComputerReview.DetailSystem, formatter.System(f));
        Add(L.ComputerReview.DetailRelease, r.Status.Release.Release?.Release);
        Add(L.ComputerReview.DetailSupport, formatter.Support(r.Status.Release));
        Add(L.ComputerReview.DetailLastLogon, formatter.Text(ComputerReviewColumns.LastLogon, r));
        Add(L.ComputerReview.DetailExactLastLogon, formatter.Text(ComputerReviewColumns.ExactLastLogon, r));
        Add(L.ComputerReview.DetailCreated, formatter.Moment(f.Created));
        Add(L.ComputerReview.DetailAccountRenewed, formatter.Moment(f.PasswordLastSet));
        Add(L.ComputerReview.DetailOwner, formatter.Text(ComputerReviewColumns.Owner, r));
        Add(L.ComputerReview.DetailCreator, formatter.Text(ComputerReviewColumns.Creator, r));
        Add(L.ComputerReview.DetailLaps, f.LapsKind is null ? Localizer[L.ComputerReview.LapsNone] : formatter.Text(ComputerReviewColumns.Laps, r));
        Add(L.ComputerReview.DetailSite, formatter.Site(r.Site));
        Add(L.ComputerReview.DetailAddress, r.Site?.Address?.ToString());
        Add(L.ComputerReview.DetailLocation, f.Location);
        Add(L.ComputerReview.DetailDescription, f.Description);
        Add(L.ComputerReview.DetailContainer, formatter.Text(ComputerReviewColumns.Container, r));
        Add(L.ComputerReview.DetailPlacement, formatter.Text(ComputerReviewColumns.Placement, r));
        Add(L.ComputerReview.DetailFindings, string.Join(", ", row.Reports.Order().Select(x => Localizer[ComputerReviewFormatter.ReportKey(x)])));
        Add(L.ComputerReview.DetailCatalog, CatalogText);
        Details = details;
    }

    /// <summary>Everything a run's evaluation needs (also used to re-evaluate rows after changes).</summary>
    private sealed class RunContext(
        IDirectorySession session,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        ComputerReportParameters parameters,
        IReadOnlySet<ComputerReport> reports,
        ComputerInventoryOptions options,
        ComputerReviewFormatter formatter)
    {
        public IDirectorySession Session { get; } = session;

        public DateTimeOffset Now { get; } = now;

        public TimeZoneInfo TimeZone { get; } = timeZone;

        public ComputerReportParameters Parameters { get; } = parameters;

        public IReadOnlySet<ComputerReport> Reports { get; } = reports;

        public ComputerInventoryOptions Options { get; } = options;

        public ComputerReviewFormatter Formatter { get; set; } = formatter;

        /// <summary>Sites, subnets and DCs, read on the first site resolution of the run.</summary>
        public SiteData? Topology { get; set; }

        /// <summary>The invalid subnets were reported once.</summary>
        public bool InvalidSubnetsReported { get; set; }

        public ComputerReviewFacts Create(DirectoryObject obj)
        {
            var facts = ComputerFacts.From(obj);
            return new ComputerReviewFacts
            {
                Facts = facts,
                Status = ComputerInventory.Evaluate(facts, Now, TimeZone, Options),
                Owner = facts.ManagedBy is null ? OwnerState.Missing : OwnerState.Unknown,
            };
        }

        public ComputerStatus Evaluate(ComputerReviewFacts row) =>
            ComputerInventory.Evaluate(row.Facts, Now, TimeZone, Options, row.ExactLastLogon, row.HasExactLastLogon);
    }

    private sealed record ReviewScope(string BaseDn, SearchScope Scope, string? Filter);

    private sealed class ScopeException(string message) : Exception(message);
}
