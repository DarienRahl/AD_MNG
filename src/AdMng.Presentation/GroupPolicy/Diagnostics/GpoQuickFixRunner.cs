using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Diagnostics;

/// <summary>
/// "Napraw…" of a finding (K6, design note 20.2): every <see cref="GpoQuickFix"/> record maps to one planner call of
/// <see cref="GroupPolicyPlanner"/> whose plan goes through the window's change plan preview and executes there (CLAUDE.md
/// rule 5) - on the window's current session and, for the SYSVOL repairs, SYSVOL of the same DC opened only while planning
/// (<see cref="GroupPolicyActions"/>, rule 4). Scoped like the actions of its connection window.
/// </summary>
public sealed class GpoQuickFixRunner(GroupPolicyActions actions)
{
    private GroupPolicyActions Actions { get; } = actions ?? throw new ArgumentNullException(nameof(actions));

    private GroupPolicyPlanner Planner => Actions.Services.Planner;

    private ILocalizer Localizer => Actions.Services.Localizer;

    /// <summary>
    /// Plans the repair, shows it in the preview and executes it there. True when the directory or SYSVOL changed; failures
    /// are shown to the operator, never thrown.
    /// </summary>
    /// <param name="fix">The repair of a finding.</param>
    /// <param name="subject">
    /// What the change plan is named after: the GPO's name, or the WMI filter's name for <see cref="GpoQuickFix.DeleteWmiFilter"/>.
    /// </param>
    /// <param name="nameOf">The display name of a SID (the trustee of <see cref="GpoQuickFix.GrantRead"/> and <see cref="GpoQuickFix.RemoveTrustee"/>).</param>
    public Task<bool> RunAsync(GpoQuickFix fix, string subject, Func<string, string>? nameOf = null)
    {
        ArgumentNullException.ThrowIfNull(fix);
        ArgumentNullException.ThrowIfNull(subject);
        var name = nameOf ?? (sid => TrusteeNames.WellKnown(Localizer, sid) ?? sid);
        var (title, sysvol, plan) = Map(fix, subject, name);
        return Actions.RunAsync(title, sysvol, plan);
    }

    /// <summary>The change plan title, the SYSVOL use and the planner call of a repair.</summary>
    internal (string Title, GroupPolicyActions.SysvolUse Sysvol, Func<IDirectorySession, ISysvolWriteConnection?, CancellationToken, Task<ChangePlan>> Plan)
        Map(GpoQuickFix fix, string subject, Func<string, string> nameOf)
    {
        const GroupPolicyActions.SysvolUse Ldap = GroupPolicyActions.SysvolUse.None;
        const GroupPolicyActions.SysvolUse Sysvol = GroupPolicyActions.SysvolUse.Required;
        return fix switch
        {
            GpoQuickFix.Unlink unlink => (Title(L.GroupPolicy.TitleUnlink, subject), Ldap,
                (session, _, token) => Planner.PlanUnlinkAsync(session, unlink.ContainerDn, unlink.GpoId, token)),
            GpoQuickFix.EnableLink enable => (Title(L.GroupPolicy.TitleLinkOptions, subject), Ldap,
                (session, _, token) => Planner.PlanLinkOptionsAsync(session, enable.ContainerDn, enable.GpoId, enforced: null, enabled: true, token)),
            GpoQuickFix.RemoveDuplicateLinks duplicates => (Title(L.GpoDiagnostics.TitleRemoveDuplicateLinks, subject), Ldap,
                (session, _, token) => Planner.PlanRemoveDuplicateLinksAsync(session, duplicates.ContainerDn, duplicates.GpoId, token)),
            GpoQuickFix.SetStatus status => (Title(L.GroupPolicy.TitleStatus, subject), Ldap,
                (session, _, token) => Planner.PlanStatusAsync(session, status.GpoId, status.Status, token)),
            GpoQuickFix.GrantRead grant => (Title(L.GroupPolicy.TitleDelegation, subject), Sysvol,
                (session, connection, token) => Planner.PlanSetDelegationAsync(session, connection, grant.GpoId, grant.Sid, nameOf(grant.Sid), GpoPermissionLevel.Read, token)),
            GpoQuickFix.ClearWmiFilter clear => (Title(L.GroupPolicy.TitleWmiLink, subject), Ldap,
                (session, _, token) => Planner.PlanWmiFilterLinkAsync(session, clear.GpoId, null, token)),
            GpoQuickFix.DeleteWmiFilter delete => (Title(L.GroupPolicy.TitleWmiDelete, subject), Ldap,
                (session, _, token) => Planner.PlanDeleteWmiFilterAsync(session, delete.Dn, token)),
            GpoQuickFix.AlignVersions align => (Title(L.GroupPolicy.TitleAlign, subject), Sysvol,
                (session, connection, token) => Planner.PlanAlignVersionsAsync(session, connection!, align.GpoId, token)),
            GpoQuickFix.RepairFolderSecurity repair => (Title(L.GroupPolicy.TitleRepair, subject), Sysvol,
                (session, connection, token) => Planner.PlanRepairFolderSecurityAsync(session, connection!, repair.GpoId, token)),
            GpoQuickFix.RemoveTrustee remove => (Title(L.GroupPolicy.TitleDelegation, subject), Sysvol,
                (session, connection, token) => Planner.PlanRemoveDelegationAsync(session, connection, remove.GpoId, remove.Sid, nameOf(remove.Sid), token)),
            GpoQuickFix.RepairExtensionNames extensions => (Title(L.GpoDiagnostics.TitleRepairExtensions, subject), Sysvol,
                (session, connection, token) => Planner.PlanRepairExtensionNamesAsync(session, connection!, extensions.GpoId, token)),
            _ => throw new ArgumentOutOfRangeException(nameof(fix), "Unknown repair."),
        };
    }

    /// <summary>
    /// What <see cref="RunAsync"/> names the plan after: the WMI filter's name for <see cref="GpoQuickFix.DeleteWmiFilter"/>
    /// (the finding's first argument), else the GPO's name.
    /// </summary>
    public static string SubjectOf(GpoFindingRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Finding.Fix is GpoQuickFix.DeleteWmiFilter && row.Finding.Arguments.Count > 0 ? row.Finding.Arguments[0] : row.GpoText;
    }

    private string Title(string key, string subject) => Localizer.Format(key, subject);
}
