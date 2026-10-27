using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Model;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>
/// The Group Policy operations of a connection window (design note docs/dev/k-computers-and-gpo.md 5.3) as the documents,
/// the tree and the shell start them: ask what is needed (name, GPO, trustee, folder), plan on the session's DC with
/// <see cref="GroupPolicyPlanner"/> (rule 4; SYSVOL opened with the window's credential only while planning) and show the
/// plan in the change plan preview, which executes it (rule 5). Every method returns true when something was written to
/// the directory or SYSVOL; failures are shown to the operator and never thrown. Scoped like its services.
/// </summary>
public sealed class GroupPolicyActions(GroupPolicyServices services)
{
    /// <summary>The services of the window.</summary>
    public GroupPolicyServices Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

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
    /// usunięciem", default on); the preview asks to type the GPO's name (the sentence names the domain).
    /// </summary>
    public async Task<bool> DeleteAsync(GpoInfo gpo)
    {
        ArgumentNullException.ThrowIfNull(gpo);
        var title = Localizer.Format(L.GroupPolicy.TitleDelete, gpo.DisplayName);
        if (gpo.IsDefault)
        {
            await Services.Ui.Dialogs.ShowErrorAsync(title, Localizer[L.GroupPolicy.DefaultGpoRefused]).ConfigureAwait(true);
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
        return await RunAsync(title, SysvolUse.Required, (session, _, token) => Services.Planner.PlanDeleteAsync(session, gpo.Id, token),
            confirmationText: gpo.DisplayName, confirmationMessage: message).ConfigureAwait(true);
    }

    /// <summary>"Usuń pozostały folder": the SYSVOL folder a failed delete left behind.</summary>
    public Task<bool> DeleteOrphanedFolderAsync(Guid gpoId, string name) =>
        RunAsync(Localizer.Format(L.GroupPolicy.TitleDeleteFolder, name), SysvolUse.Required,
            (session, _, _) => Task.FromResult(Services.Planner.PlanDeleteOrphanedFolder(session, gpoId, name)));

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
        if (await PickTrusteeAsync().ConfigureAwait(true) is not { } trustee)
        {
            return false;
        }

        return await RunAsync(Localizer.Format(L.GroupPolicy.TitleSecurityFilter, gpo.DisplayName), SysvolUse.Required,
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
        if (await PickTrusteeAsync().ConfigureAwait(true) is not { } trustee)
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
    /// own connection to the same DC) and shows the preview. Failures are shown, never thrown.
    /// </summary>
    internal async Task<bool> RunAsync(
        string title,
        SysvolUse sysvolUse,
        Func<IDirectorySession, ISysvolWriteConnection?, CancellationToken, Task<ChangePlan>> plan,
        string? confirmationText = null,
        string? confirmationMessage = null)
    {
        var session = Services.Sessions.Current;
        if (session is null)
        {
            await Services.Ui.Dialogs.ShowErrorAsync(title, Localizer[L.Errors.NotConnected]).ConfigureAwait(true);
            return false;
        }

        ChangePlan planned;
        ISysvolWriteConnection? sysvol = null;
        try
        {
            if (sysvolUse == SysvolUse.Required)
            {
                var access = await Services.OpenSysvolAsync(session, Services.Lifetime).ConfigureAwait(true);
                if (access.Connection is null)
                {
                    await Services.Ui.Dialogs.ShowErrorAsync(title, Services.SysvolText(access.Reason ?? SysvolUnavailableReason.Failed, session.Info.Server))
                        .ConfigureAwait(true);
                    return false;
                }

                sysvol = access.Connection;
            }

            planned = await plan(session, sysvol, Services.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            await Services.Ui.Dialogs.ShowErrorAsync(title, GroupPolicyText.Error(Localizer, Services.Errors, ex)).ConfigureAwait(true);
            return false;
        }
        finally
        {
            if (sysvol is not null)
            {
                await sysvol.DisposeAsync().ConfigureAwait(true);
            }
        }

        if (planned.ExecutableCount == 0 && planned.ErrorCount == 0)
        {
            Services.Ui.Notifications.Show(NotificationSeverity.Info, Localizer.Format(L.Changes.NothingToDo, title));
            return false;
        }

        return await Services.PreviewAsync(planned, title, session, confirmationText, confirmationMessage).ConfigureAwait(true);
    }

    /// <summary>The object picker for a trustee (user, group or computer) and its SID; null when cancelled or unreadable.</summary>
    private async Task<(string Sid, string Name)?> PickTrusteeAsync()
    {
        var session = Services.Sessions.Current;
        if (session is null)
        {
            return null;
        }

        var picked = await Services.Ui.Dialogs.PickObjectsAsync(session, [ObjectKind.User, ObjectKind.Group, ObjectKind.Computer], multiSelect: false).ConfigureAwait(true);
        if (picked is not [var dn, ..])
        {
            return null;
        }

        try
        {
            var obj = await session.GetAsync(dn, [Attr.ObjectSid, Attr.Name, Attr.SamAccountName], Services.Lifetime).ConfigureAwait(true);
            if (obj?.ObjectSid is not { } sid)
            {
                await Services.Ui.Dialogs.ShowErrorAsync(Localizer[L.GroupPolicy.TrusteePickerTitle], Localizer[L.GroupPolicy.TrusteeWithoutSid]).ConfigureAwait(true);
                return null;
            }

            return (sid, obj.GetString(Attr.SamAccountName) ?? obj.Name);
        }
        catch (DirectoryException ex)
        {
            await Services.Ui.Dialogs.ShowErrorAsync(Localizer[L.GroupPolicy.TrusteePickerTitle], Services.Errors.Translate(ex)).ConfigureAwait(true);
            return null;
        }
    }
}
