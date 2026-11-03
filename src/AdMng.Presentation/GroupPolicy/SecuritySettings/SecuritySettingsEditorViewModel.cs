using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Security;
using AdMng.Core.Model;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy.SecuritySettings;

/// <summary>
/// "Ustawienia zabezpieczeń" of one GPO (K4, design note docs/dev/k-computers-and-gpo.md 19): the computer configuration's
/// local groups (Restricted Groups and GPP local groups), user rights, account policies and security options, audit policy
/// (legacy categories and advanced subcategories) and service start modes, read from GptTmpl.inf, audit.csv and Groups.xml
/// of the session's DC and saved as one plan through the change plan preview (CLAUDE.md rule 5). Edits live in memory until
/// saved, with K3's reload rules: a file changed on SYSVOL meanwhile keeps the edits and the conflict note, a file saved as
/// edited becomes clean, a file holding what this tab saved rebases the later edits. A file that cannot be read makes only its
/// pages read-only; everything is read-only while loading or saving or when SYSVOL is not writable; invalid option values
/// block the save; closing the tab with unsaved edits asks. Nothing of the files is persisted or logged (rule 1).
/// </summary>
public sealed partial class SecuritySettingsEditorViewModel : GroupPolicyDocumentBase
{
    private const string TemplatePath = SecurityTemplate.RelativePath;
    private const string AuditPath = AuditPolicyDocument.RelativePath;
    private const string GroupsPath = GppGroupsDocument.RelativePath;

    private readonly string _initialName;

    /// <summary>What the last save asked to write per file: a reload that finds it on SYSVOL knows the save landed.</summary>
    private readonly Dictionary<string, byte[]> _saved = new(StringComparer.Ordinal);

    /// <summary>Names of domain SIDs (resolved on the session's DC or picked), memory only.</summary>
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    private SecurityFile<SecurityTemplateDocument>? _template;
    private SecurityFile<AuditPolicyDocument>? _audit;
    private SecurityFile<GppGroupsDocument>? _groups;
    private GpoInfo? _gpo;
    private bool _confirmingClose;
    private Dictionary<string, PrincipalChoice> _choicesBySid = new(StringComparer.OrdinalIgnoreCase);

    public SecuritySettingsEditorViewModel(GroupPolicyServices services, GroupPolicyActions actions, Guid gpoId, string? name)
        : base(services, actions)
    {
        GpoId = gpoId;
        _initialName = name ?? GroupPolicyText.Guid(gpoId);
        BuildCatalogRows();
    }

    public Guid GpoId { get; }

    public override string Title => Localizer.Format(L.SecuritySettings.Title, _gpo?.DisplayName ?? _initialName);

    public override ObjectIcon Icon => ObjectIcon.GroupPolicy;

    public override string SelectionText => ChangedCount > 0 ? Localizer.Format(L.SecuritySettings.ChangedCount, ChangedCount) : string.Empty;

    /// <summary>The local groups and well-known principals offered in the group and principal boxes (localized).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PrincipalChoice> PrincipalChoices { get; private set; } = [];

    /// <summary>The built-in local groups (with their GPP names) of the group boxes.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PrincipalChoice> LocalGroupChoices { get; private set; } = [];

    // ---- Notes and state -----------------------------------------------------------------------------------------------

    /// <summary>Files that cannot be read (their pages are read-only), else null.</summary>
    [ObservableProperty]
    public partial string? FileNote { get; private set; }

    /// <summary>The GPO's files changed on SYSVOL since the edits started (a save would be a conflict), else null.</summary>
    [ObservableProperty]
    public partial string? ConflictNote { get; private set; }

    /// <summary>Settings changed and not saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    public partial int ChangedCount { get; private set; }

    /// <summary>A file differs from what was read.</summary>
    public bool HasChanges => (_template?.IsChanged ?? false) || (_audit?.IsChanged ?? false) || (_groups?.IsChanged ?? false);

    /// <summary>"Zapisz zmiany…" is running (plan, preview, execution): the editor is read-only meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanEditTemplate), nameof(CanEditAudit), nameof(CanEditGroups))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    public partial bool IsSaving { get; private set; }

    /// <summary>The GPO can be edited now (connected, SYSVOL writable, loaded, not being saved).</summary>
    public bool CanEdit => IsSysvolWritable && _gpo is not null && !IsSaving;

    /// <summary>GptTmpl.inf can be edited (Restricted Groups, rights, options, legacy audit, services).</summary>
    public bool CanEditTemplate => CanEdit && _template is { Error: null };

    /// <summary>audit.csv can be edited (advanced audit policy).</summary>
    public bool CanEditAudit => CanEdit && _audit is { Error: null };

    /// <summary>Groups.xml can be edited (GPP local groups).</summary>
    public bool CanEditGroups => CanEdit && _groups is { Error: null };

    /// <summary>An option value as edited is not valid (the save is blocked until it is corrected or undone).</summary>
    public bool HasProblems => _optionRows.Any(r => r.HasProblem);

    /// <summary>Closing the tab would discard edits (the shell asks first).</summary>
    public override bool HasUnsavedChanges => HasChanges;

    // ---- Save, discard, close ------------------------------------------------------------------------------------------

    /// <summary>"Zapisz zmiany…": one plan (the changed files, extension names, version) through the preview.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_gpo is not { } gpo)
        {
            return;
        }

        if (_optionRows.FirstOrDefault(r => r.HasProblem) is { } invalid)
        {
            // The document holds the last valid value, not what the row shows: never save something else than shown.
            Gp.Ui.Notifications.Show(NotificationSeverity.Warning, Localizer.Format(L.SecuritySettings.SaveBlocked, invalid.Name));
            return;
        }

        var save = new SecuritySettingsSave { Template = FileSave(_template), Audit = FileSave(_audit), Groups = FileSave(_groups) };
        if (!save.Changed)
        {
            return;
        }

        Remember(TemplatePath, save.Template);
        Remember(AuditPath, save.Audit);
        Remember(GroupsPath, save.Groups);
        IsSaving = true;
        SyncCanEdit();
        try
        {
            await Actions.SaveSecuritySettingsAsync(gpo, save).ConfigureAwait(true);
        }
        finally
        {
            IsSaving = false;
            SyncCanEdit();
        }
    }

    /// <summary>"Odrzuć zmiany": the files as read again (then a reload shows the current files).</summary>
    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private async Task DiscardAsync()
    {
        if (!await Gp.Ui.Dialogs.ConfirmAsync(Localizer[L.SecuritySettings.DiscardTitle], Localizer.Format(L.SecuritySettings.DiscardMessage, ChangedCount),
            destructive: true).ConfigureAwait(true))
        {
            return;
        }

        _template = _template?.Discarded();
        _audit = _audit?.Discarded();
        _groups = _groups?.Discarded();
        ConflictNote = null;
        RebuildAll();
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Closing the tab with unsaved edits asks before they are discarded; never while its save is being planned.</summary>
    public override async Task<bool> CanCloseAsync()
    {
        if (!HasChanges || IsDisposed)
        {
            return true;
        }

        if (_confirmingClose || IsSaving)
        {
            return false;
        }

        _confirmingClose = true;
        try
        {
            return await Gp.Ui.Dialogs.ConfirmAsync(Title, Localizer.Format(L.SecuritySettings.CloseDiscard, ChangedCount), destructive: true).ConfigureAwait(true);
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    private bool CanSave() => HasChanges && CanEdit && !IsLoading && !HasProblems;

    private bool CanDiscard() => HasChanges && !IsSaving;

    private static GpoFileSave? FileSave<T>(SecurityFile<T>? file)
        where T : class =>
        file is { IsChanged: true, Error: null } ? new GpoFileSave { Original = file.Original, Content = file.Content } : null;

    private void Remember(string path, GpoFileSave? save)
    {
        if (save is not null)
        {
            _saved[path] = save.Content;
        }
    }

    // ---- Loading -------------------------------------------------------------------------------------------------------

    protected override async Task LoadCoreAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, CancellationToken cancellationToken)
    {
        var reader = Gp.Reader;
        var gpo = await Task.Run(() => reader.ReadPolicyAsync(session, GpoId, cancellationToken), cancellationToken).ConfigureAwait(true);
        if (gpo is null)
        {
            _gpo = null;
            Error = Localizer[L.GroupPolicy.Missing];
            LoadedText = Error;
            OnPropertyChanged(nameof(Title));
            return;
        }

        (byte[]? Content, string? Error)? template = null, audit = null, groups = null;
        if (sysvol is not null)
        {
            template = await ReadFileAsync(sysvol, TemplatePath, cancellationToken).ConfigureAwait(true);
            audit = await ReadFileAsync(sysvol, AuditPath, cancellationToken).ConfigureAwait(true);
            groups = await ReadFileAsync(sysvol, GroupsPath, cancellationToken).ConfigureAwait(true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _gpo = gpo;
        OnPropertyChanged(nameof(Title));
        _template = Reconcile(_template, TemplatePath, template, c => SecurityTemplateDocument.Parse(c), d => d.ToBytes());
        _audit = Reconcile(_audit, AuditPath, audit, c => AuditPolicyDocument.Parse(c), d => d.ToBytes());
        _groups = Reconcile(_groups, GroupsPath, groups, c => GppGroupsDocument.Parse(c), d => d.ToBytes());
        ConflictNote = (_template?.Conflict ?? false) || (_audit?.Conflict ?? false) || (_groups?.Conflict ?? false)
            ? Localizer.Format(L.SecuritySettings.ConflictNote, session.Info.Server)
            : null;
        FileNote = string.Join(Environment.NewLine, new[] { _template?.Error, _audit?.Error, _groups?.Error }.OfType<string>()) is { Length: > 0 } note ? note : null;

        // Names of the domain's SIDs for display (memory only); a SID that cannot be resolved stays a SID.
        var unknown = Sids().Where(s => NameOfSid(s) is null).ToList();
        if (unknown.Count > 0)
        {
            var names = await TrusteeNames.ResolveAsync(session, unknown, Localizer, cancellationToken).ConfigureAwait(true);
            foreach (var (sid, name) in names)
            {
                _names[sid] = name;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        RebuildAll();
        LoadedText = Localizer.Format(L.SecuritySettings.Loaded, CountDefined());
    }

    protected override void OnNotConnected()
    {
        _gpo = null;
        RebuildAll();
    }

    protected override void RefreshCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        SyncCanEdit();
    }

    protected override void OnCultureChanged()
    {
        BuildCatalogRows();
        RebuildAll();
        base.OnCultureChanged();
    }

    /// <summary>A file of the GPO: its content (null: none) or a localized reason it cannot be read.</summary>
    private async Task<(byte[]? Content, string? Error)> ReadFileAsync(ISysvolConnection sysvol, string path, CancellationToken cancellationToken)
    {
        var result = await sysvol.ReadGpoFileAsync(GpoId, path, GpoTransaction.MaxFileBytes, cancellationToken).ConfigureAwait(true);
        return result.Status switch
        {
            SysvolFileStatus.Read or SysvolFileStatus.NotFound => (result.Content, null),
            SysvolFileStatus.AccessDenied => (null, Localizer[L.AdministrativeTemplates.FileAccessDenied]),
            SysvolFileStatus.TooLarge => (null, Localizer[L.AdministrativeTemplates.FileTooLarge]),
            _ => (null, Localizer[L.AdministrativeTemplates.FileReadFailed]),
        };
    }

    /// <summary>
    /// The file after a read (null: SYSVOL unavailable – nothing to edit): without edits it is the file; with edits it stays
    /// edited unless the file now equals the edits (they were saved); a file that holds what the last save of this tab wrote
    /// rebases the edits made since onto it; any other file that changed under the edits marks a conflict and keeps the old
    /// original (the save's compare-and-swap refuses it).
    /// </summary>
    private SecurityFile<T>? Reconcile<T>(SecurityFile<T>? file, string path, (byte[]? Content, string? Error)? read, Func<byte[]?, T> parse, Func<T, byte[]> serialize)
        where T : class
    {
        if (read is not { } result)
        {
            return file is { IsChanged: true } ? file : null;
        }

        if (result.Error is not null)
        {
            return file is { IsChanged: true } ? file : SecurityFile<T>.Unreadable(path, Localizer.Format(L.SecuritySettings.FileUnreadable, path, result.Error), parse, serialize);
        }

        try
        {
            if (file is { IsChanged: true })
            {
                if (SecurityFile<T>.SameContent(result.Content, file.Content))
                {
                    return SecurityFile<T>.Of(path, result.Content, parse, serialize);
                }

                if (SecurityFile<T>.SameContent(result.Content, file.Original))
                {
                    file.Conflict = false;
                    return file;
                }

                if (_saved.TryGetValue(path, out var saved) && SecurityFile<T>.SameContent(result.Content, saved))
                {
                    // Our save landed, and the editor holds edits made after it (e.g. after a reload that could not read the
                    // file): those edits stay, on top of the saved file.
                    return file.Rebased(result.Content);
                }

                file.Conflict = true;
                return file;
            }

            return SecurityFile<T>.Of(path, result.Content, parse, serialize);
        }
        catch (GpoParseException)
        {
            return file is { IsChanged: true } ? file : SecurityFile<T>.Unreadable(path, Localizer.Format(L.SecuritySettings.FileUnreadable, path, Localizer[L.AdministrativeTemplates.FileMalformed]), parse, serialize);
        }
    }

    // ---- Rows ----------------------------------------------------------------------------------------------------------

    /// <summary>The rows of every page from the documents (rows are reused, so selections and bindings stay).</summary>
    private void RebuildAll()
    {
        RebuildGroups();
        RebuildRights();
        RebuildOptions();
        RebuildAudit();
        RebuildServices();
        SyncCanEdit();
        UpdateChanges();
    }

    /// <summary>After an edit: the pages, the read-only state and the counter.</summary>
    private void AfterEdit() => RebuildAll();

    /// <summary>The rows and commands follow whether each file can be edited now (after a load, around a save).</summary>
    private void SyncCanEdit()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanEditTemplate));
        OnPropertyChanged(nameof(CanEditAudit));
        OnPropertyChanged(nameof(CanEditGroups));
        var template = CanEditTemplate;
        foreach (var row in RestrictedGroups)
        {
            row.CanEdit = template;
        }

        foreach (var row in UserRights)
        {
            row.CanEdit = template;
        }

        foreach (var row in _optionRows)
        {
            row.CanEdit = template;
        }

        foreach (var row in RawEntries)
        {
            row.CanEdit = template;
        }

        foreach (var row in LegacyAudit)
        {
            row.CanEdit = template;
        }

        foreach (var row in _advancedRows)
        {
            row.CanEdit = CanEditAudit;
        }

        foreach (var row in Services)
        {
            row.CanEdit = template;
        }

        foreach (var row in GppGroups)
        {
            row.CanEdit = CanEditGroups;
        }

        RefreshPageCommands();
    }

    /// <summary>The number of changed settings: keys of the template, subcategories, services and GPP items that differ.</summary>
    private void UpdateChanges()
    {
        var count = 0;
        if (_template is { Error: null } template)
        {
            foreach (var section in new[]
            {
                SecurityTemplateDocument.SystemAccess, SecurityTemplateDocument.KerberosPolicy, SecurityTemplateDocument.EventAudit,
                SecurityTemplateDocument.PrivilegeRights, SecurityTemplateDocument.GroupMembership, SecurityTemplateDocument.RegistryValues,
            })
            {
                var before = template.OriginalDocument.Values(section).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
                var after = template.Document.Values(section).ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
                count += before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
                    .Count(k => !SameValue(before.GetValueOrDefault(k), after.GetValueOrDefault(k)));
            }

            var servicesBefore = template.OriginalDocument.GetServices().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            var servicesAfter = template.Document.GetServices().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
            count += servicesBefore.Keys.Union(servicesAfter.Keys, StringComparer.OrdinalIgnoreCase)
                .Count(k => servicesBefore.GetValueOrDefault(k) != servicesAfter.GetValueOrDefault(k));
        }

        if (_audit is { Error: null } audit)
        {
            var before = audit.OriginalDocument.GetSubcategories();
            var after = audit.Document.GetSubcategories();
            count += before.Keys.Union(after.Keys).Count(k => before.GetValueOrDefault(k, -1) != after.GetValueOrDefault(k, -1));
        }

        if (_groups is { Error: null } groups)
        {
            var before = groups.OriginalDocument.Items;
            var after = groups.Document.Items;
            for (var i = 0; i < Math.Max(before.Count, after.Count); i++)
            {
                if (i >= before.Count || i >= after.Count || !SameItem(before[i], after[i]))
                {
                    count++;
                }
            }
        }

        ChangedCount = HasChanges ? Math.Max(count, 1) : 0;
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(HasProblems));
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    private int CountDefined()
    {
        if (_template is not { Error: null } template)
        {
            return 0;
        }

        var count = template.Document.Values(SecurityTemplateDocument.PrivilegeRights).Count
            + RestrictedGroups.Count
            + template.Document.Values(SecurityTemplateDocument.SystemAccess).Count
            + template.Document.Values(SecurityTemplateDocument.KerberosPolicy).Count
            + template.Document.Values(SecurityTemplateDocument.RegistryValues).Count
            + template.Document.Values(SecurityTemplateDocument.EventAudit).Count
            + template.Document.GetServices().Count;
        count += _audit is { Error: null } audit ? audit.Document.GetSubcategories().Count : 0;
        count += _groups is { Error: null } groups ? groups.Document.Items.Count : 0;
        return count;
    }

    private static bool SameValue(string? a, string? b) =>
        a is null ? b is null : b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.Ordinal);

    private static bool SamePrincipals(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);

    private static bool SameItem(GppLocalGroup a, GppLocalGroup b) =>
        string.Equals(a.GroupName, b.GroupName, StringComparison.Ordinal)
        && string.Equals(a.GroupSid, b.GroupSid, StringComparison.OrdinalIgnoreCase)
        && a.Action == b.Action && a.DeleteAllUsers == b.DeleteAllUsers && a.DeleteAllGroups == b.DeleteAllGroups
        && a.Disabled == b.Disabled && a.HasItemLevelTargeting == b.HasItemLevelTargeting
        && a.Members.SequenceEqual(b.Members);

    // ---- Principals ----------------------------------------------------------------------------------------------------

    /// <summary>A principal as shown: a SID by its local, well-known or resolved name; a name as typed with the hint.</summary>
    private SecurityPrincipalViewModel Principal(string value)
    {
        if (SecurityTemplate.IsSid(value))
        {
            var sid = value.ToUpperInvariant();
            return new SecurityPrincipalViewModel(sid, NameOfSid(sid) ?? sid, IsSid: true, sid);
        }

        return new SecurityPrincipalViewModel(value, value, IsSid: false, Localizer[L.SecuritySettings.TypedNameHint]);
    }

    private string? NameOfSid(string sid) =>
        _choicesBySid.TryGetValue(sid, out var choice) ? choice.DisplayName
        : TrusteeNames.WellKnown(Localizer, sid) is { } wellKnown ? wellKnown
        : _names.TryGetValue(sid, out var name) ? name
        : null;

    /// <summary>Every SID the three files name (for the name lookup on load).</summary>
    private IEnumerable<string> Sids()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_template?.Document is { } template)
        {
            foreach (var holders in template.GetPrivilegeRights().Values)
            {
                result.UnionWith(holders.Where(SecurityTemplate.IsSid));
            }

            foreach (var entry in template.GetRestrictedGroups())
            {
                result.UnionWith(entry.Principals.Append(entry.Group).Where(SecurityTemplate.IsSid));
            }
        }

        if (_groups?.Document is { } groups)
        {
            foreach (var item in groups.Items)
            {
                result.UnionWith(item.Members.Select(m => m.Sid).OfType<string>());
                if (item.GroupSid is { } sid)
                {
                    result.Add(sid);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// What a typed principal box holds: a listed local group or well-known principal by its name (its SID), a SID, or the
    /// name as typed (resolved on each client); null (with a warning) when empty or not writable (comma, quote, '=', line break).
    /// </summary>
    private string? ResolveTyped(string? text, bool localGroup, out PrincipalChoice? choice)
    {
        choice = null;
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            Gp.Ui.Notifications.Show(NotificationSeverity.Warning, Localizer[L.SecuritySettings.NameRequired]);
            return null;
        }

        choice = (localGroup ? LocalGroupChoices : PrincipalChoices).FirstOrDefault(c => string.Equals(c.DisplayName, value, StringComparison.CurrentCultureIgnoreCase)
            || string.Equals(c.GppName, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Sid, value, StringComparison.OrdinalIgnoreCase));
        if (choice is null && localGroup && LocalRightsCatalog.LocalGroupSid(value) is { } known && _choicesBySid.TryGetValue(known, out var named))
        {
            choice = named; // "Administrators", "BUILTIN\Administratorzy", "Administrators (built-in)"
        }

        if (choice is not null)
        {
            return choice.Sid;
        }

        var principal = SecurityTemplate.Principal(value);
        if (SecurityTemplate.IsSid(principal))
        {
            return principal;
        }

        if (principal.IndexOfAny([',', '"', '=', '\r', '\n']) >= 0 || principal.Length == 0)
        {
            Gp.Ui.Notifications.Show(NotificationSeverity.Warning, Localizer.Format(L.SecuritySettings.NameInvalid, value));
            return null;
        }

        return principal;
    }

    /// <summary>The object picker (users, groups, computers – or only groups): the SID, its name remembered for display.</summary>
    private async Task<(string Sid, string Name)?> PickAsync(bool groupsOnly)
    {
        var picked = await Actions.PickTrusteeAsync(groupsOnly ? [ObjectKind.Group] : null).ConfigureAwait(true);
        if (picked is not { } trustee)
        {
            return null;
        }

        var session = Gp.Sessions.Current;
        var netBios = session is null ? null : string.IsNullOrWhiteSpace(session.Info.DomainNetBiosName) ? session.Info.DomainDnsName : session.Info.DomainNetBiosName;
        var prefix = trustee.Sid.StartsWith("S-1-5-32-", StringComparison.OrdinalIgnoreCase) ? "BUILTIN" : netBios;
        var name = string.IsNullOrEmpty(prefix) ? trustee.Name : prefix + "\\" + trustee.Name;
        _names[trustee.Sid] = name;
        return (trustee.Sid.ToUpperInvariant(), name);
    }

    /// <summary>A document refused a value (a comma, quote or line break where the file cannot hold one).</summary>
    private void ShowRefused() => Gp.Ui.Notifications.Show(NotificationSeverity.Warning, Localizer[L.SecuritySettings.EditRefused]);

    private void BuildCatalogRows()
    {
        var choices = SecuritySettingsText.Choices(Localizer);
        PrincipalChoices = choices;
        LocalGroupChoices = [.. choices.Where(c => c.IsLocalGroup)];
        _choicesBySid = choices.ToDictionary(c => c.Sid, StringComparer.OrdinalIgnoreCase);
        BuildRightRows();
        BuildOptionRows();
        BuildAuditRows();
        BuildServiceChoices();
        BuildGppChoices();
    }
}
