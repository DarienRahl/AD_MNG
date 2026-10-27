using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Planning;
using AdMng.Core.Directory;
using AdMng.Core.Security;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>Security filtering and delegation of a GPO (design note 5.3, K2.4 on K1's <see cref="ChangeAction.ModifySecurity"/>).</summary>
public sealed partial class GroupPolicyPlanner
{
    /// <summary>
    /// Adds a trustee to the security filtering (<c>Set-GPPermission -PermissionLevel GpoApply</c>): Read + Apply Group
    /// Policy, container inherit, then the folder DACL follows the container's.
    /// </summary>
    public Task<ChangePlan> PlanAddSecurityFilterAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, Guid gpoId, string trusteeSid, string? trusteeName,
        CancellationToken cancellationToken) =>
        PlanPermissionAsync(session, sysvol, gpoId, trusteeSid, trusteeName, cancellationToken, "Security filtering",
            (descriptor, sid) => new AclChange(GpoPermissionLevels.AcesFor(sid, GpoPermissionLevel.Apply, trusteeName), []),
            [PowerShellParameter.Literal("PermissionLevel", "GpoApply")]);

    /// <summary>
    /// Removes a trustee from the security filtering: its Apply ACEs and its plain Read ACEs - except for Authenticated Users,
    /// which keep (or get) Read as GPMC does since MS16-072. The preview warns when no computer can read the GPO afterwards.
    /// </summary>
    public Task<ChangePlan> PlanRemoveSecurityFilterAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, Guid gpoId, string trusteeSid, string? trusteeName,
        CancellationToken cancellationToken) =>
        PlanPermissionAsync(session, sysvol, gpoId, trusteeSid, trusteeName, cancellationToken, "Security filtering",
            (descriptor, sid) =>
            {
                var aces = GpoPermissionLevels.ExplicitAcesOf(descriptor, sid);
                var apply = aces.Where(a => a.Qualifier == AceQualifier.Allow && (a.Mask & GpoPermissionLevels.ControlAccess) != 0
                    && (a.ObjectType is null || a.ObjectType == GroupPolicySecurityFilter.ApplyGroupPolicyRight)).ToList();
                var authenticatedUsers = string.Equals(sid, GpoPermissionLevels.AuthenticatedUsersSid, StringComparison.OrdinalIgnoreCase);
                var reads = authenticatedUsers ? [] : aces.Where(a => a.Qualifier == AceQualifier.Allow && a.ObjectType is null && a.Mask == GpoPermissionLevels.ReadMask).ToList();
                var added = authenticatedUsers ? GpoPermissionLevels.AcesFor(sid, GpoPermissionLevel.Read, trusteeName) : [];
                return new AclChange(added, [.. apply, .. reads]);
            },
            [PowerShellParameter.Literal("PermissionLevel", "None"), PowerShellParameter.Switch("Replace")]);

    /// <summary>
    /// Sets the delegation level of a trustee (<c>Set-GPPermission -PermissionLevel GpoRead|GpoEdit|GpoEditDeleteModifySecurity
    /// -Replace</c>): its explicit ACEs are replaced by those of the level.
    /// </summary>
    public Task<ChangePlan> PlanSetDelegationAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, Guid gpoId, string trusteeSid, string? trusteeName,
        GpoPermissionLevel level, CancellationToken cancellationToken)
    {
        if (level == GpoPermissionLevel.Custom)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "A custom level cannot be set.");
        }

        return PlanPermissionAsync(session, sysvol, gpoId, trusteeSid, trusteeName, cancellationToken, "Delegation",
            (descriptor, sid) => new AclChange(GpoPermissionLevels.AcesFor(sid, level, trusteeName), GpoPermissionLevels.ExplicitAcesOf(descriptor, sid)),
            [PowerShellParameter.Literal("PermissionLevel", level switch
            {
                GpoPermissionLevel.Read => "GpoRead",
                GpoPermissionLevel.Apply => "GpoApply",
                GpoPermissionLevel.Edit => "GpoEdit",
                _ => "GpoEditDeleteModifySecurity",
            }), PowerShellParameter.Switch("Replace")]);
    }

    /// <summary>Removes every explicit ACE of a trustee (custom ones included).</summary>
    public Task<ChangePlan> PlanRemoveDelegationAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, Guid gpoId, string trusteeSid, string? trusteeName,
        CancellationToken cancellationToken) =>
        PlanPermissionAsync(session, sysvol, gpoId, trusteeSid, trusteeName, cancellationToken, "Delegation",
            (descriptor, sid) => new AclChange([], GpoPermissionLevels.ExplicitAcesOf(descriptor, sid)),
            [PowerShellParameter.Literal("PermissionLevel", "None"), PowerShellParameter.Switch("Replace")]);

    private async Task<ChangePlan> PlanPermissionAsync(
        IDirectorySession session,
        ISysvolWriteConnection? sysvol,
        Guid gpoId,
        string trusteeSid,
        string? trusteeName,
        CancellationToken cancellationToken,
        string title,
        Func<byte[]?, string, AclChange> delta,
        IReadOnlyList<PowerShellParameter> level)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!AceSpec.IsValidSid(trusteeSid))
        {
            throw new ArgumentException("The trustee is not a valid SID.", nameof(trusteeSid));
        }

        var gpo = await RequirePolicyAsync(session, gpoId, cancellationToken).ConfigureAwait(false);
        var wanted = delta(gpo.SecurityDescriptor, trusteeSid);
        var steps = new List<PlannedChange>();
        if (await AclPlanning.PlanAsync(session, gpo.Dn, wanted, cancellationToken).ConfigureAwait(false) is { } security)
        {
            var issues = security.Issues.ToList();
            if (gpo.SecurityDescriptor is { } descriptor && security.Acl is { } applied && !security.HasErrors)
            {
                var after = DaclEditor.Apply(descriptor, applied).Descriptor;
                var domainComputers = await DomainComputersSidAsync(session, cancellationToken).ConfigureAwait(false);
                if (!GpoPermissionLevels.ComputersCanRead(GpoPermissionLevels.Read(after), domainComputers))
                {
                    issues.Add(new ValidationIssue(IssueSeverity.Warning, IssueCodes.Ms16072Read,
                        "Neither Authenticated Users nor Domain Computers can read the GPO (MS16-072)."));
                }
            }

            steps.Add(security with { Issues = issues });
            var oldFolder = sysvol is null ? null : await sysvol.GetGpoFolderSecurityAsync(gpoId, cancellationToken).ConfigureAwait(false);
            steps.Add(new PlannedChange
            {
                Action = ChangeAction.SyncGpoFolderSecurity,
                Sysvol = new SysvolChange { GpoId = gpoId, OldFolderSecurity = oldFolder },
            });
        }

        var target = trusteeName is { Length: > 0 } name ? name : trusteeSid;
        var command = new PowerShellCommand("Set-GPPermission",
        [
            GuidParameter(gpoId),
            PowerShellParameter.Literal("TargetName", target),
            PowerShellParameter.Literal("TargetType", "Group"),
            .. level,
            Domain(session),
            PowerShellParameter.Server,
        ])
        {
            Comment = "TargetType: Group, User or Computer - as the trustee is.",
        };
        return Plan(session, [PolicyObject(gpo, steps, PowerShellEquivalent.Of(command))], $"{title}: {gpo.DisplayName}");
    }

    /// <summary>Domain Computers of the session's domain (the domain SID + 515), null when the domain SID cannot be read.</summary>
    private static async Task<string?> DomainComputersSidAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        var head = await session.GetAsync(session.Info.DefaultNamingContext, [Attr.ObjectSid], cancellationToken).ConfigureAwait(false);
        return head?.ObjectSid is { } sid ? sid + "-515" : null;
    }
}
