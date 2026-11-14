using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Model;
using AdMng.Presentation.Changes;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>
/// The Group Policy operations of a connection window (design note docs/dev/k-computers-and-gpo.md 5.3) as the documents,
/// the tree and the shell start them: ask what is needed (name, GPO, trustee, folder), plan on the session's DC with
/// <see cref="GroupPolicyPlanner"/> (rule 4; SYSVOL opened with the window's credential only while planning) and show the
/// plan in the change plan preview, which executes it (rule 5). Every method returns true when something was written to
/// the directory or SYSVOL; failures are shown to the operator and never thrown. Scoped like its services.
/// </summary>
/// <remarks>
/// <b>Where an operation speaks</b> (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built"). A document
/// starts its operations through its own copy (<see cref="For"/>): while the plan is being made its status line says
/// "…: preparing the changes…" with Stop, and when nothing comes of it - SYSVOL is not available, the plan could not be
/// made, nothing has to change, the operator stopped it - the line says why, with Retry. The tree and the shell have
/// no status line (<see cref="Status"/> is null): for them a failure stays a dialog and "nothing to change" a toast,
/// as before. Only what is <i>said</i> differs: the plan, the preview and the execution are the same (rule 5).
/// </remarks>
public sealed class GroupPolicyActions(GroupPolicyServices services)
{
    private GroupPolicyActions(GroupPolicyServices services, PageStatusHolder status)
        : this(services)
    {
        Status = status;
    }

    /// <summary>The services of the window.</summary>
    public GroupPolicyServices Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>
    /// The status line these operations speak through - the one of the document that starts them - or null: the
    /// operations of the tree and of the shell, which answer with a dialog or a toast.
    /// </summary>
    public PageStatusHolder? Status { get; }

    /// <summary>The same operations, speaking through the status line of a document (see the remarks of the class).</summary>
    public GroupPolicyActions For(PageStatusHolder status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new GroupPolicyActions(Services, status);
    }

    private ILocalizer Localizer => Services.Localizer;

    // ---- Documents ----------------------------------------------------------------------------------------------------

    /// <summary>Opens the GPO document (optionally on a tab).</summary>
    public void OpenGpo(Guid gpoId, string name, GpoDocumentTab? tab = null) =>
        Services.OpenDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Object) { GpoId = gpoId, Name = name, Tab = tab });

    /// <summary>Opens "Zasady grupy" of a domain head, OU or site.</summary>
    public void OpenScope(string containerDn, string? name = null) =>
        Services.OpenDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Scope) { ContainerDn = containerDn, Name = name });

    /// <summary>Opens "Obiekty zasad grupy".</summary>
    public void OpenObjects() => Services.OpenDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.Objects));

    /// <summary>Opens "Filtry WMI".</summary>
    public void OpenWmiFilters() => Services.OpenDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.WmiFilters));

    /// <summary>"Edytuj szablony administracyjne": the editor of a GPO's administrative templates (K3).</summary>
    public void EditAdministrativeTemplates(Guid gpoId, string name) =>
        Services.OpenDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.AdministrativeTemplates) { GpoId = gpoId, Name = name });

    /// <summary>
    /// "Zapisz zmiany…" of the administrative templates editor: Registry.pol of the changed halves, the extension names
    /// and the versions as one plan through the preview (K3); <paramref name="settingChanges"/> is shown above its rows.
    /// </summary>
    public Task<bool> SaveAdministrativeTemplatesAsync(GpoInfo gpo, RegistryPolicySave? machine, RegistryPolicySave? user,
        IReadOnlyList<GroupPolicySettingChange>? settingChanges = null)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        return RunAsync(Localizer.Format(L.AdministrativeTemplates.PlanTitle, gpo.DisplayName), SysvolUse.Required,
            (session, sysvol, token) => Services.Planner.PlanAdministrativeTemplatesAsync(session, sysvol!, gpo.Id, machine, user, token),
            settingChanges: settingChanges);
    }

    /// <summary>"Edytuj ustawienia zabezpieczeń": the security settings editor of a GPO (K4).</summary>
    public void EditSecuritySettings(Guid gpoId, string name) =>
        Services.OpenDocument(new GroupPolicyDocumentRequest(GroupPolicyDocumentKind.SecuritySettings) { GpoId = gpoId, Name = name });

    /// <summary>
    /// "Zapisz zmiany…" of the security settings editor: the changed files (GptTmpl.inf, audit.csv, Groups.xml), the computer
    /// extension names and version as one plan through the preview (K4); <paramref name="settingChanges"/> is shown above its rows.
    /// </summary>
    public Task<bool> SaveSecuritySettingsAsync(GpoInfo gpo, SecuritySettingsSave save, IReadOnlyList<GroupPolicySettingChange>? settingChanges = null)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        ArgumentNullException.ThrowIfNull(save);
        return RunAsync(Localizer.Format(L.SecuritySettings.PlanTitle, gpo.DisplayName), SysvolUse.Required,
            (session, sysvol, token) => Services.Planner.PlanSecuritySettingsAsync(session, sysvol!, gpo.Id, save, token),
            settingChanges: settingChanges);
    }

    /// <summary>
    /// "Edytuj to ustawienie" (K6, design note 20.3) for the GPO content's settings, the K5 model rows and the diagnostics:
    /// opens the editor that edits <paramref name="focus"/> – the administrative templates (K3) at its category and policy, or
    /// the security settings (K4) at its page and row (<see cref="GroupPolicySettingFocus.Editor"/>). An editor of the GPO
    /// that is already open is activated and points at the setting again. False (nothing opened) when no editor of AD Manager
    /// edits the setting or no shell listens; callers disable the command with <see cref="CanEditSetting"/>.
    /// </summary>
    /// <param name="gpoId">The GPO.</param>
    /// <param name="name">The GPO's name (shown until the editor has loaded).</param>
    /// <param name="focus">The setting: a GPO content key or an ADMX policy.</param>
    public bool EditSetting(Guid gpoId, string? name, GroupPolicySettingFocus focus)
    {
        ArgumentNullException.ThrowIfNull(focus);
        return focus.Editor is { } kind
            && Services.OpenDocument(new GroupPolicyDocumentRequest(kind) { GpoId = gpoId, Name = name, Focus = focus });
    }

    /// <summary>An editor of AD Manager edits the setting ("Edytuj to ustawienie" is offered).</summary>
    public static bool CanEditSetting(GroupPolicySettingFocus? focus) => focus?.Editor is not null;

    // ---- Create, copy, rename, delete ---------------------------------------------------------------------------------

    /// <summary>"Nowe…" / "Utwórz GPO w tej domenie i połącz je tutaj…": the name, then the create plan (and the link).</summary>
    public async Task<bool> NewAsync(string? linkToDn = null, string? linkToName = null)
    {
        using var dialog = new NewGpoViewModel(Services.Ui, NewGpoMode.New, Localizer[L.GroupPolicy.NewGpoDefaultName], linkToName ?? linkToDn);
        if (!await Services.Dialogs.ShowNewGpoAsync(dialog).ConfigureAwait(true) || dialog.Result is not { } name)
        {
            return false;
        }

        var title = Localizer.Format(L.GroupPolicy.TitleNew, name);
        return await RunAsync(title, SysvolUse.Required, (session, _, token) => Services.Planner.PlanCreateAsync(session, name, linkToDn, token)).ConfigureAwait(true);
    }

    /// <summary>"Kopiuj…": the name of the copy and its permissions (default or the source's), then <c>Copy-GPO</c>.</summary>
    public async Task<bool> CopyAsync(GpoInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var dialog = new NewGpoViewModel(Services.Ui, NewGpoMode.Copy, Localizer.Format(L.GroupPolicy.CopyDefaultName, source.DisplayName), null, source.DisplayName);
        if (!await Services.Dialogs.ShowNewGpoAsync(dialog).ConfigureAwait(true) || dialog.Result is not { } name)
        {
            return false;
        }

        var copyPermissions = dialog.CopyPermissions;
        return await RunAsync(Localizer.Format(L.GroupPolicy.TitleCopy, source.DisplayName), SysvolUse.Required,
            (session, sysvol, token) => Services.Planner.PlanCopyAsync(session, sysvol!, source.Id, name, copyPermissions, token)).ConfigureAwait(true);
    }

    /// <summary>"Zmień nazwę".</summary>
    public async Task<bool> RenameAsync(GpoInfo gpo)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        using var dialog = new NewGpoViewModel(Services.Ui, NewGpoMode.Rename, gpo.DisplayName, null, gpo.DisplayName);
        if (!await Services.Dialogs.ShowNewGpoAsync(dialog).ConfigureAwait(true) || dialog.Result is not { } name
            || string.Equals(name, gpo.DisplayName, StringComparison.Ordinal))
        {
            return false;
        }

        return await RunAsync(Localizer.Format(L.GroupPolicy.TitleRename, gpo.DisplayName), SysvolUse.None,
            (session, _, token) => Services.Planner.PlanRenameAsync(session, gpo.Id, name, token)).ConfigureAwait(true);
    }

    /// <summary>
    /// "Usuń…": the default GPOs are refused; the backup window offers a backup first ("Utwórz kopię zapasową przed
    /// usunięciem", default on); the preview asks to type the GPO's name (the sentence names the domain). With
    /// <paramref name="expected"/> (K7, the cleanup advisor's "Usuń GPO…") the plan is guarded by what the analysis saw: a
    /// GPO whose version or links moved since carries the error <see cref="IssueCodes.GpoChangedSinceAnalysis"/> and cannot run.
    /// </summary>
    public async Task<bool> DeleteAsync(GpoInfo gpo, GpoExpectedState? expected = null)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        var title = Localizer.Format(L.GroupPolicy.TitleDelete, gpo.DisplayName);
        if (gpo.IsDefault)
        {
            await RefuseAsync(title, title, Localizer[L.GroupPolicy.DefaultGpoRefused], L.GroupPolicy.DefaultGpoRefused).ConfigureAwait(true);
            return false;
        }

        using (var backup = new GpoBackupViewModel(Services, GpoBackupMode.BeforeDelete, [gpo]))
        {
            if (!await Services.Dialogs.ShowBackupAsync(backup).ConfigureAwait(true))
            {
                return false;
            }
        }

        if (Services.Sessions.Current is not { } current)
        {
            return false;
        }

        var domain = Connections.ConfirmationText.Domain(current);
        var message = Localizer.Format(L.GroupPolicy.ConfirmDelete, gpo.DisplayName, domain) + Environment.NewLine + Environment.NewLine
            + Localizer.Format(L.Delete.TypeToConfirm, gpo.DisplayName);
        return await RunAsync(title, SysvolUse.Required, (session, _, token) => expected is null
                ? Services.Planner.PlanDeleteAsync(session, gpo.Id, token)
                : Services.Planner.PlanDeleteAsync(session, gpo.Id, expected, token),
            confirmationText: gpo.DisplayName, confirmationMessage: message).ConfigureAwait(true);
    }

    /// <summary>"Usuń pozostały folder": the SYSVOL folder a failed delete left behind.</summary>
    public Task<bool> DeleteOrphanedFolderAsync(Guid gpoId, string name) =>
        RunAsync(Localizer.Format(L.GroupPolicy.TitleDeleteFolder, name), SysvolUse.Required,
            (session, _, token) => Services.Planner.PlanDeleteOrphanedFolderAsync(session, gpoId, name, token));

    // ---- Links and inheritance ----------------------------------------------------------------------------------------

    /// <summary>"Połącz istniejący GPO…": the GPO (one; those already linked here are not offered), then <c>New-GPLink</c>.</summary>
    public async Task<bool> LinkExistingAsync(string containerDn, string? containerName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerDn);
        var session = Services.Sessions.Current;
        if (session is null)
        {
            return false;
        }

        IReadOnlyCollection<Guid> linked = [];
        try
        {
            if (await Services.Reader.ReadContainerAsync(session, containerDn, Services.Lifetime).ConfigureAwait(true) is { } container)
            {
                linked = [.. container.Links.Links.Select(l => l.GpoId)];
            }
        }
        catch (DirectoryException)
        {
            // The picker offers every GPO then; the planner refuses nothing it cannot do.
        }

        using var picker = new GpoPickerViewModel(Services.Ui, Services.Reader, Localizer.Format(L.GroupPolicy.LinkPickerTitle, containerName ?? containerDn), multiSelect: false, linked);
        if (!await Services.Dialogs.ShowGpoPickerAsync(picker).ConfigureAwait(true) || picker.Result is not [var gpo, ..])
        {
            return false;
        }

        return await RunAsync(Localizer.Format(L.GroupPolicy.TitleLink, gpo.DisplayName, containerName ?? containerDn), SysvolUse.None,
            (s, _, token) => Services.Planner.PlanLinkAsync(s, containerDn, gpo.Id, token)).ConfigureAwait(true);
    }

    /// <summary>"Usuń połączenie".</summary>
    public Task<bool> UnlinkAsync(string containerDn, Guid gpoId, string gpoName) =>
        RunAsync(Localizer.Format(L.GroupPolicy.TitleUnlink, gpoName), SysvolUse.None,
            (session, _, token) => Services.Planner.PlanUnlinkAsync(session, containerDn, gpoId, token));

    /// <summary>"Wyżej" / "Niżej": the link to another link order.</summary>
    public Task<bool> MoveLinkAsync(string containerDn, Guid gpoId, string gpoName, int order) =>
        RunAsync(Localizer.Format(L.GroupPolicy.TitleReorder, gpoName), SysvolUse.None,
            (session, _, token) => Services.Planner.PlanMoveLinkAsync(session, containerDn, gpoId, order, token));

    /// <summary>"Wymuszone" / "Połączenie włączone".</summary>
    public Task<bool> SetLinkOptionsAsync(string containerDn, Guid gpoId, string gpoName, bool? enforced, bool? enabled) =>
        RunAsync(Localizer.Format(L.GroupPolicy.TitleLinkOptions, gpoName), SysvolUse.None,
            (session, _, token) => Services.Planner.PlanLinkOptionsAsync(session, containerDn, gpoId, enforced, enabled, token));

    /// <summary>"Zablokuj dziedziczenie" on or off.</summary>
    public Task<bool> SetBlockInheritanceAsync(string containerDn, string containerName, bool block) =>
        RunAsync(Localizer.Format(block ? L.GroupPolicy.TitleBlock : L.GroupPolicy.TitleUnblock, containerName), SysvolUse.None,
            (session, _, token) => Services.Planner.PlanBlockInheritanceAsync(session, containerDn, block, token));

    // ---- Status, WMI filter link, versions, folder permissions --------------------------------------------------------

    /// <summary>"Stan obiektu zasad grupy".</summary>
    public Task<bool> SetStatusAsync(GpoInfo gpo, GpoStatus status)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        return RunAsync(Localizer.Format(L.GroupPolicy.TitleStatus, gpo.DisplayName), SysvolUse.None,
            (session, _, token) => Services.Planner.PlanStatusAsync(session, gpo.Id, status, token));
    }

    /// <summary>"Filtrowanie WMI": the filter (null: none).</summary>
    public Task<bool> SetWmiFilterAsync(GpoInfo gpo, WmiFilterInfo? filter)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        return RunAsync(Localizer.Format(L.GroupPolicy.TitleWmiLink, gpo.DisplayName), SysvolUse.None,
            (session, _, token) => Services.Planner.PlanWmiFilterLinkAsync(session, gpo.Id, filter, token));
    }

    /// <summary>"Wyrównaj wersje".</summary>
    public Task<bool> AlignVersionsAsync(GpoInfo gpo)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        return RunAsync(Localizer.Format(L.GroupPolicy.TitleAlign, gpo.DisplayName), SysvolUse.Required,
            (session, sysvol, token) => Services.Planner.PlanAlignVersionsAsync(session, sysvol!, gpo.Id, token));
    }

    /// <summary>"Napraw uprawnienia folderu".</summary>
    public Task<bool> RepairFolderSecurityAsync(GpoInfo gpo)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        return RunAsync(Localizer.Format(L.GroupPolicy.TitleRepair, gpo.DisplayName), SysvolUse.Required,
            (session, sysvol, token) => Services.Planner.PlanRepairFolderSecurityAsync(session, sysvol!, gpo.Id, token));
    }

    // ---- Security filtering and delegation ----------------------------------------------------------------------------

    /// <summary>"Filtrowanie zabezpieczeń → Dodaj…": a user, group or computer that applies the GPO.</summary>
    public async Task<bool> AddSecurityFilterAsync(GpoInfo gpo)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        var title = Localizer.Format(L.GroupPolicy.TitleSecurityFilter, gpo.DisplayName);
        if (await PickTrusteeAsync(operation: title).ConfigureAwait(true) is not { } trustee)
        {
            return false;
        }

        return await RunAsync(title, SysvolUse.Required,
            (session, sysvol, token) => Services.Planner.PlanAddSecurityFilterAsync(session, sysvol, gpo.Id, trustee.Sid, trustee.Name, token)).ConfigureAwait(true);
    }

    /// <summary>"Filtrowanie zabezpieczeń → Usuń" (Authenticated Users keep Read, MS16-072).</summary>
    public Task<bool> RemoveSecurityFilterAsync(GpoInfo gpo, string sid, string? name)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        return RunAsync(Localizer.Format(L.GroupPolicy.TitleSecurityFilter, gpo.DisplayName), SysvolUse.Required,
            (session, sysvol, token) => Services.Planner.PlanRemoveSecurityFilterAsync(session, sysvol, gpo.Id, sid, name, token));
    }

    /// <summary>"Delegowanie → Dodaj…": a trustee with a GPMC level.</summary>
    public async Task<bool> AddDelegationAsync(GpoInfo gpo, GpoPermissionLevel level)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        if (await PickTrusteeAsync(operation: Localizer.Format(L.GroupPolicy.TitleDelegation, gpo.DisplayName)).ConfigureAwait(true) is not { } trustee)
        {
            return false;
        }

        return await SetDelegationAsync(gpo, trustee.Sid, trustee.Name, level).ConfigureAwait(true);
    }

    /// <summary>"Delegowanie → Zmień poziom".</summary>
    public Task<bool> SetDelegationAsync(GpoInfo gpo, string sid, string? name, GpoPermissionLevel level)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        return RunAsync(Localizer.Format(L.GroupPolicy.TitleDelegation, gpo.DisplayName), SysvolUse.Required,
            (session, sysvol, token) => Services.Planner.PlanSetDelegationAsync(session, sysvol, gpo.Id, sid, name, level, token));
    }

    /// <summary>"Delegowanie → Usuń": every explicit ACE of the trustee.</summary>
    public Task<bool> RemoveDelegationAsync(GpoInfo gpo, string sid, string? name)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        return RunAsync(Localizer.Format(L.GroupPolicy.TitleDelegation, gpo.DisplayName), SysvolUse.Required,
            (session, sysvol, token) => Services.Planner.PlanRemoveDelegationAsync(session, sysvol, gpo.Id, sid, name, token));
    }

    // ---- Backup, restore, import, compare -----------------------------------------------------------------------------

    /// <summary>"Kopia zapasowa…" of the GPOs (into a folder the operator picks).</summary>
    public async Task<bool> BackupAsync(IReadOnlyList<GpoInfo> gpos)
    {
        ArgumentNullException.ThrowIfNull(gpos);
        if (gpos.Count == 0)
        {
            return false;
        }

        using var dialog = new GpoBackupViewModel(Services, GpoBackupMode.Backup, gpos);
        return await Services.Dialogs.ShowBackupAsync(dialog).ConfigureAwait(true);
    }

    /// <summary>"Przywróć z kopii zapasowej…".</summary>
    public async Task<bool> RestoreAsync()
    {
        using var dialog = new GpoBackupViewModel(Services, GpoBackupMode.Restore, []);
        await Services.Dialogs.ShowBackupAsync(dialog).ConfigureAwait(true);
        return dialog.ChangedDirectory;
    }

    /// <summary>"Importuj ustawienia…" into a GPO.</summary>
    public async Task<bool> ImportAsync(GpoInfo target)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var dialog = new GpoBackupViewModel(Services, GpoBackupMode.Import, [target]);
        await Services.Dialogs.ShowBackupAsync(dialog).ConfigureAwait(true);
        return dialog.ChangedDirectory;
    }

    /// <summary>"Porównaj" (two GPOs) / "Porównaj z…" (the second chosen in a picker).</summary>
    public async Task CompareAsync(GpoInfo a, GpoInfo? b = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (b is null)
        {
            using var picker = new GpoPickerViewModel(Services.Ui, Services.Reader, Localizer.Format(L.GroupPolicy.ComparePickerTitle, a.DisplayName), multiSelect: false, [a.Id]);
            if (!await Services.Dialogs.ShowGpoPickerAsync(picker).ConfigureAwait(true) || picker.Result is not [var chosen, ..])
            {
                return;
            }

            b = chosen;
        }

        // Modeless: the window disposes the view model when it closes.
        await Services.Dialogs.ShowComparisonAsync(new GpoComparisonViewModel(Services, a, b)).ConfigureAwait(true);
    }

    // ---- WMI filters --------------------------------------------------------------------------------------------------

    /// <summary>"Nowy…" WMI filter.</summary>
    public async Task<bool> NewWmiFilterAsync()
    {
        using var editor = new WmiFilterEditorViewModel(Services.Ui, null);
        if (!await Services.Dialogs.ShowWmiFilterEditorAsync(editor).ConfigureAwait(true) || editor.Result is not { } result)
        {
            return false;
        }

        return await RunAsync(Localizer.Format(L.GroupPolicy.TitleWmiNew, result.Name), SysvolUse.None,
            (session, _, token) => Services.Planner.PlanCreateWmiFilterAsync(session, result.Name, result.Description, result.Queries, token)).ConfigureAwait(true);
    }

    /// <summary>"Edytuj…" a WMI filter.</summary>
    public async Task<bool> EditWmiFilterAsync(WmiFilterInfo filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        using var editor = new WmiFilterEditorViewModel(Services.Ui, filter);
        if (!await Services.Dialogs.ShowWmiFilterEditorAsync(editor).ConfigureAwait(true) || editor.Result is not { } result)
        {
            return false;
        }

        return await RunAsync(Localizer.Format(L.GroupPolicy.TitleWmiEdit, filter.Name), SysvolUse.None,
            (session, _, token) => Services.Planner.PlanEditWmiFilterAsync(session, filter.Dn, result.Name, result.Description, result.Queries, token)).ConfigureAwait(true);
    }

    /// <summary>"Usuń…" a WMI filter (the GPOs that use it lose the link first).</summary>
    public Task<bool> DeleteWmiFilterAsync(WmiFilterInfo filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return RunAsync(Localizer.Format(L.GroupPolicy.TitleWmiDelete, filter.Name), SysvolUse.None,
            (session, _, token) => Services.Planner.PlanDeleteWmiFilterAsync(session, filter.Dn, token));
    }

    // ---- Watchdog -----------------------------------------------------------------------------------------------------

    /// <summary>The Watchdog can show the history of GPOs (a service address is configured for this domain).</summary>
    public bool CanShowHistory => Services.History?.IsAvailable == true;

    /// <summary>"Historia w Watchdogu": the changes of the GPO's container (by its objectGUID).</summary>
    public async Task<bool> ShowHistoryAsync(GpoInfo gpo)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        if (Services.History is not { IsAvailable: true } history || Services.Sessions.Current is not { } session)
        {
            return false;
        }

        try
        {
            var gpc = await session.GetAsync(gpo.Dn, [Attr.ObjectGuid], Services.Lifetime).ConfigureAwait(true);
            return gpc?.ObjectGuid is { } guid && history.Show(guid, gpo.DisplayName);
        }
        catch (DirectoryException ex)
        {
            await Services.Ui.Dialogs.ShowErrorAsync(gpo.DisplayName, Services.Errors.Translate(ex)).ConfigureAwait(true);
            return false;
        }
    }

    // ---- Plumbing -----------------------------------------------------------------------------------------------------

    /// <summary>How an operation uses SYSVOL of the session's DC.</summary>
    internal enum SysvolUse
    {
        /// <summary>LDAP only (links, status, names, WMI filters).</summary>
        None,

        /// <summary>The plan reads or writes SYSVOL: it is refused (with the reason) when the share cannot be opened.</summary>
        Required,
    }

    /// <summary>
    /// Plans on the current session (SYSVOL opened only for planning, closed before the preview: the executor opens its
    /// own connection to the same DC) and shows the preview. Failures are shown, never thrown. K7 (docs/dev/k-computers-and-gpo.md
    /// 21.5): <c>describeChanges</c> is called after planning when <c>settingChanges</c> is null, with the plan and the
    /// session; its rows are shown as "Zmiany ustawień" above the plan's rows. A failure only drops the rows; null returned:
    /// nothing is previewed (the callback has told the operator why, e.g. nothing would be copied).
    /// </summary>
    /// <remarks>
    /// Started by a document (<see cref="Status"/>), making the plan is an operation of that document: its line says
    /// "…: preparing the changes…" with Stop until the preview opens, and why nothing came of it - with Retry, which makes
    /// the plan of the same request again. The preview says its own status in its own window. Stop must hold: a plan that
    /// was made although Stop was pressed is not previewed.
    /// </remarks>
    internal async Task<bool> RunAsync(
        string title,
        SysvolUse sysvolUse,
        Func<IDirectorySession, ISysvolWriteConnection?, CancellationToken, Task<ChangePlan>> plan,
        string? confirmationText = null,
        string? confirmationMessage = null,
        IReadOnlyList<GroupPolicySettingChange>? settingChanges = null,
        Func<ChangePlan, IDirectorySession, CancellationToken, Task<IReadOnlyList<GroupPolicySettingChange>?>>? describeChanges = null)
    {
        var session = Services.Sessions.Current;
        if (session is null)
        {
            await RefuseAsync(title, title, Localizer[L.Errors.NotConnected], L.Errors.NotConnected).ConfigureAwait(true);
            return false;
        }

        using var preparing = Status?.Begin(Localizer.Format(L.GroupPolicyStatus.Preparing, title), new PageStatusLog(L.GroupPolicyStatus.Preparing));
        using var stoppable = preparing is null ? null : CancellationTokenSource.CreateLinkedTokenSource(preparing.Token, Services.Lifetime);
        var cancellation = stoppable?.Token ?? Services.Lifetime;
        var requestedChanges = settingChanges;
        var retry = preparing is null
            ? null
            : new PageStatusAction(
                Localizer[L.Common.Retry],
                new AsyncRelayCommand(() => RunAsync(title, sysvolUse, plan, confirmationText, confirmationMessage, requestedChanges, describeChanges)));

        ChangePlan planned;
        ISysvolWriteConnection? sysvol = null;
        try
        {
            if (sysvolUse == SysvolUse.Required)
            {
                var access = await Services.OpenSysvolAsync(session, cancellation).ConfigureAwait(true);
                if (access.Connection is null)
                {
                    var reason = Services.SysvolText(access.Reason ?? SysvolUnavailableReason.Failed, session.Info.Server);
                    if (preparing is null)
                    {
                        await Services.Ui.Dialogs.ShowErrorAsync(title, reason).ConfigureAwait(true);
                    }
                    else
                    {
                        preparing.CompleteAttention(NotChangedText(title, reason, retryable: true), new PageStatusLog(L.GroupPolicy.SysvolFailedShort), retry);
                    }

                    return false;
                }

                sysvol = access.Connection;
            }

            planned = await plan(session, sysvol, cancellation).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (preparing is not null)
        {
            // The operator's Stop: nothing was changed. Taken off the line by the next thing the document does (it is
            // read again), or the window closed: nothing to say.
            CompleteStopped(preparing, title, retry);
            return false;
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            var reason = GroupPolicyText.Error(Localizer, Services.Errors, ex);
            if (preparing is null)
            {
                await Services.Ui.Dialogs.ShowErrorAsync(title, reason).ConfigureAwait(true);
            }
            else
            {
                var code = ResultCode.Of(ex);
                preparing.CompleteError(NotChangedText(title, reason, retryable: true, code), new PageStatusLog(L.GroupPolicyStatus.NotChanged).WithCode(code), retry);
            }

            return false;
        }
        finally
        {
            if (sysvol is not null)
            {
                await sysvol.DisposeAsync().ConfigureAwait(true);
            }
        }

        if (preparing is { IsCancelling: true })
        {
            // Stop must hold: a planner that finished although Stop was pressed does not lead to the preview.
            CompleteStopped(preparing, title, retry);
            return false;
        }

        if (settingChanges is null && describeChanges is not null)
        {
            try
            {
                settingChanges = await describeChanges(planned, session, cancellation).ConfigureAwait(true);
                if (settingChanges is null)
                {
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                // The window closed while the rows were made, or the operator stopped it: nothing is previewed, and
                // nothing is thrown.
                if (preparing is not null)
                {
                    CompleteStopped(preparing, title, retry);
                }

                return false;
            }
            catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
            {
                // Only the plain-language rows are lost; the plan's own rows are still shown.
                settingChanges = [];
            }
        }

        if (planned.ExecutableCount == 0 && planned.ErrorCount == 0)
        {
            var nothing = Localizer.Format(L.Changes.NothingToDo, title);
            if (preparing is null)
            {
                Services.Ui.Notifications.Show(NotificationSeverity.Info, nothing);
            }
            else
            {
                preparing.CompleteDone(nothing, new PageStatusLog(L.Changes.NothingToDo));
            }

            return false;
        }

        // The plan is made: the line is the document's again while the preview window - which says its own status - is open.
        preparing?.Dispose();
        return await Services.PreviewAsync(planned, title, session, confirmationText, confirmationMessage, settingChanges).ConfigureAwait(true);
    }

    /// <summary>
    /// Says why an operation was not carried out, where the operator started it: in the status line of the document -
    /// an attention that can be closed - or, for the tree and the shell and while the document says how far an
    /// operation of its own is, in a dialog.
    /// </summary>
    /// <param name="dialogTitle">The title of the dialog (the operation, or what was asked for).</param>
    /// <param name="operation">The title of the operation for the line ("Delete GPO “…”"); null: always a dialog.</param>
    /// <param name="reason">Why, a sentence.</param>
    /// <param name="logKey">The resource key of the reason (the copy for the diagnostic log), never the text.</param>
    /// <param name="code">The result code of a failure; with one the line says an error.</param>
    internal async Task RefuseAsync(string dialogTitle, string? operation, string reason, string logKey, ResultCode code = default)
    {
        if (operation is not null && Status is { IsWorking: false } status)
        {
            var text = NotChangedText(operation, reason, retryable: false, code);
            var log = new PageStatusLog(logKey).WithCode(code);
            status.Show(code.IsNone ? PageStatus.Attention(text, log) : PageStatus.Error(text, log));
            return;
        }

        await Services.Ui.Dialogs.ShowErrorAsync(dialogTitle, reason).ConfigureAwait(true);
    }

    /// <summary>"Rename GPO “…” – nothing was changed: why. Press Retry to try again. (code)".</summary>
    private string NotChangedText(string title, string reason, bool retryable, ResultCode code = default) =>
        PageStatusText.Failure(Localizer.Format(L.GroupPolicyStatus.NotChanged, title), reason, retryable ? Localizer[L.Feedback.RetryNext] : null, code);

    /// <summary>
    /// Making the plan ended with a cancellation: when the operator stopped it the line says that nothing was changed,
    /// with Retry; an operation that merely ended (the document is read again, the window closed) says nothing.
    /// </summary>
    private void CompleteStopped(OperationScope preparing, string title, PageStatusAction? retry)
    {
        if (preparing.IsCancelling)
        {
            preparing.CompleteAttention(
                NotChangedText(title, Localizer[L.GroupPolicyStatus.PreparingStopped], retryable: true),
                new PageStatusLog(L.GroupPolicyStatus.PreparingStopped),
                retry);
        }
    }

    /// <summary>
    /// The object picker for a trustee (user, group or computer, or the <paramref name="kinds"/> given) and its SID with its
    /// account name; null when cancelled or unreadable. <paramref name="operation"/> is the title of the operation the
    /// trustee is picked for: with it a document says in its status line why nothing came of it (the short read after the
    /// picker is no operation of its own); without it the reason is a dialog.
    /// </summary>
    internal async Task<(string Sid, string Name)?> PickTrusteeAsync(IReadOnlyList<ObjectKind>? kinds = null, string? operation = null)
    {
        var session = Services.Sessions.Current;
        if (session is null)
        {
            return null;
        }

        var picked = await Services.Ui.Dialogs.PickObjectsAsync(session, kinds ?? [ObjectKind.User, ObjectKind.Group, ObjectKind.Computer], multiSelect: false).ConfigureAwait(true);
        if (picked is not [var dn, ..])
        {
            return null;
        }

        try
        {
            var obj = await session.GetAsync(dn, [Attr.ObjectSid, Attr.Name, Attr.SamAccountName], Services.Lifetime).ConfigureAwait(true);
            if (obj?.ObjectSid is not { } sid)
            {
                await RefuseAsync(Localizer[L.GroupPolicy.TrusteePickerTitle], operation, Localizer[L.GroupPolicy.TrusteeWithoutSid], L.GroupPolicy.TrusteeWithoutSid)
                    .ConfigureAwait(true);
                return null;
            }

            return (sid, obj.GetString(Attr.SamAccountName) ?? obj.Name);
        }
        catch (DirectoryException ex)
        {
            await RefuseAsync(Localizer[L.GroupPolicy.TrusteePickerTitle], operation, Services.Errors.Translate(ex), L.GroupPolicyStatus.NotChanged, ResultCode.Of(ex))
                .ConfigureAwait(true);
            return null;
        }
    }
}
