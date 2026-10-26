namespace AdMng.Core.PrivilegedAccess;

/// <summary>
/// Where a finding comes from (README U4: the four sources plus the operator's own marks and the peer comparison). U4a
/// produces <see cref="Groups"/>, <see cref="OperatorRules"/>, <see cref="AccountSettings"/> and <see cref="Peers"/>;
/// U4b adds <see cref="DirectoryAcl"/> and U4c <see cref="GroupPolicy"/> through <see cref="IPrivilegeGrantSource"/>.
/// </summary>
public enum PrivilegeSource
{
    /// <summary>Nested membership of a built-in or well-known privileged group (primary group included).</summary>
    Groups,

    /// <summary>Groups and accounts the operator marked as privileged, by SID/DN or by a saved filter.</summary>
    OperatorRules,

    /// <summary>Risky settings of the account itself: adminCount, SPN, delegation, SID history, pre-authentication.</summary>
    AccountSettings,

    /// <summary>Groups none or few of the account's peers have (same OU, department or title).</summary>
    Peers,

    /// <summary>U4b: rights beyond the defaults in the ACLs of containers, Tier 0 objects and GPOs.</summary>
    DirectoryAcl,

    /// <summary>U4c: local groups and user rights on computers from GPOs (Restricted Groups, GPP, User Rights Assignment).</summary>
    GroupPolicy,
}

/// <summary>What a finding is about; the UI formats a text per kind (resource keys <c>PrivilegedAccess.Kind.{kind}</c>).</summary>
public enum PrivilegeFindingKind
{
    /// <summary>Member (nested, or through the primary group) of a group of <see cref="PrivilegedGroupCatalog"/>.</summary>
    BuiltInGroup,

    /// <summary>Member of a group the operator marked as privileged (SID/DN in the settings).</summary>
    MarkedGroup,

    /// <summary>The account itself was marked as privileged by the operator.</summary>
    MarkedAccount,

    /// <summary>Member of a group that matches a "mark as privileged" filter rule.</summary>
    ConditionGroup,

    /// <summary>The account matches a "mark as privileged" filter rule.</summary>
    ConditionAccount,

    /// <summary>A well-known Tier 0 account (the built-in Administrator, krbtgt).</summary>
    WellKnownAccount,

    /// <summary>adminCount = 1 with a current membership of a protected group (SDProp manages the ACL).</summary>
    AdminCount,

    /// <summary>adminCount = 1 without a protected membership any more: inheritance stays off (orphaned adminCount).</summary>
    OrphanedAdminCount,

    /// <summary>A user account with a servicePrincipalName (Kerberoasting).</summary>
    Kerberoastable,

    /// <summary>TRUSTED_FOR_DELEGATION on an account that is not a DC: any TGT sent to it can be reused.</summary>
    UnconstrainedDelegation,

    /// <summary>msDS-AllowedToDelegateTo without protocol transition.</summary>
    ConstrainedDelegation,

    /// <summary>msDS-AllowedToDelegateTo with TRUSTED_TO_AUTH_FOR_DELEGATION (any user, without their password).</summary>
    ProtocolTransition,

    /// <summary>msDS-AllowedToActOnBehalfOfOtherIdentity is set: other accounts may impersonate users towards this one.</summary>
    ResourceBasedDelegation,

    /// <summary>sIDHistory has values (privileged SIDs of the catalogue raise the level).</summary>
    SidHistory,

    /// <summary>DONT_REQUIRE_PREAUTH (AS-REP roasting).</summary>
    PreauthNotRequired,

    /// <summary>Holds groups none or few of its peers hold.</summary>
    PeerOutlier,

    /// <summary>U4b: an ACL right on a container, Tier 0 object or GPO (granted to the account or a group of it).</summary>
    AclRight,

    /// <summary>U4c: a local group or user right on computers from a GPO.</summary>
    LocalRight,
}

/// <summary>How a step of a path was reached.</summary>
public enum PrivilegePathStepKind
{
    /// <summary>The account the path starts with.</summary>
    Account,

    /// <summary>A group reached through memberOf (the account or the previous group is listed in its member).</summary>
    Group,

    /// <summary>The account's primary group (primaryGroupID; not in member/memberOf).</summary>
    PrimaryGroup,

    /// <summary>Everyone / Authenticated Users nested into a group (foreign security principal): every account has it.</summary>
    ImplicitGroup,
}

/// <summary>One step of the "where it comes from" path (konto → grupa A → grupa B → Account Operators).</summary>
/// <param name="Dn">The DN of the account or group (for implicit groups the DN of the foreign security principal).</param>
/// <param name="Name">The name to show (RDN value).</param>
/// <param name="Kind">How the step was reached.</param>
public sealed record PrivilegePathStep(string Dn, string Name, PrivilegePathStepKind Kind);

/// <summary>One group a peer outlier holds and how many of its peers hold it too.</summary>
public sealed record PeerOutlierGroup(string Dn, string Name, int Holders, PrivilegeLevel GrantedLevel);

/// <summary>The peer comparison behind a <see cref="PrivilegeFindingKind.PeerOutlier"/> finding.</summary>
/// <param name="Dimension">Which peers: same OU, department or title.</param>
/// <param name="PeerSet">The OU DN, the department or the title.</param>
/// <param name="PeerCount">Peers compared with (the account itself not counted).</param>
/// <param name="Groups">The unusual groups.</param>
public sealed record PeerOutlierDetails(PeerDimension Dimension, string PeerSet, int PeerCount, IReadOnlyList<PeerOutlierGroup> Groups);

/// <summary>
/// One right of an account beyond a plain domain user: source, kind, level, the path that explains it and where it
/// applies. Lives only in memory (CLAUDE.md rule 1).
/// </summary>
public sealed record PrivilegeFinding
{
    public required PrivilegeSource Source { get; init; }

    public required PrivilegeFindingKind Kind { get; init; }

    public required PrivilegeLevel Level { get; init; }

    /// <summary>Where the right applies.</summary>
    public required PrivilegeScopeKind Scope { get; init; }

    /// <summary>The object the scope names (OU of an ACL delegation, linked OU of a GPO, peers' OU); null for domain/forest.</summary>
    public string? ScopeDn { get; init; }

    /// <summary>The group (or account) that carries the right: the privileged group, the marked group, the account itself.</summary>
    public string? SubjectDn { get; init; }

    /// <summary>The name of <see cref="SubjectDn"/>.</summary>
    public string? SubjectName { get; init; }

    /// <summary>Id of the <see cref="PrivilegedGroupCatalog"/> entry of a <see cref="PrivilegeFindingKind.BuiltInGroup"/>.</summary>
    public string? CatalogId { get; init; }

    /// <summary>account → … → <see cref="SubjectDn"/>; only the account for findings about the account itself.</summary>
    public IReadOnlyList<PrivilegePathStep> Path { get; init; } = [];

    /// <summary>Values that explain the finding: SPNs, delegation targets, SIDs of the history.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>The operator's note of a mark, or the name of the filter rule.</summary>
    public string? Note { get; init; }

    /// <summary>The peer comparison of a <see cref="PrivilegeFindingKind.PeerOutlier"/>.</summary>
    public PeerOutlierDetails? Peer { get; init; }

    /// <summary>
    /// The group the account itself is a member of on the path (removing the account from it cuts this path); null when
    /// the path starts with the primary group or an implicit group, or the finding is not about a membership.
    /// </summary>
    public string? DirectGroupDn => Path.Count >= 2 && Path[1].Kind == PrivilegePathStepKind.Group ? Path[1].Dn : null;

    /// <summary>The path goes through the account's primary group.</summary>
    public bool IsThroughPrimaryGroup => Path.Count >= 2 && Path[1].Kind == PrivilegePathStepKind.PrimaryGroup;
}

/// <summary>Kind of account in the privileged access review.</summary>
public enum PrivilegedAccountType
{
    User,
    Computer,
    DomainController,
    ServiceAccount,
}
