using System.Globalization;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.GroupPolicy.Modeling;

/// <summary>The part of Group Policy a model is about.</summary>
public enum ModelingPart
{
    /// <summary>Computer configuration (<c>Machine/</c>).</summary>
    Computer,

    /// <summary>User configuration (<c>User/</c>).</summary>
    User,
}

/// <summary>What the model assumes for GPOs with a WMI filter (it is never evaluated).</summary>
public enum WmiFilterAssumption
{
    /// <summary>The filters are true: such a GPO is listed as applying if its filter is true (GPMC's default).</summary>
    AssumeTrue,

    /// <summary>The filters are false: such a GPO does not apply.</summary>
    AssumeFalse,
}

/// <summary>The loopback mode the operator chooses for the user part.</summary>
public enum LoopbackChoice
{
    /// <summary>The mode the computer's GPOs set (<c>UserPolicyMode</c>), none when they do not set it.</summary>
    FromSettings,

    /// <summary>No loopback processing.</summary>
    Off,

    /// <summary>Merge: the user's GPOs, then the user configuration of the computer's GPOs.</summary>
    Merge,

    /// <summary>Replace: only the user configuration of the computer's GPOs.</summary>
    Replace,
}

/// <summary>The loopback mode a model used.</summary>
public enum LoopbackMode
{
    None,
    Merge,
    Replace,
}

/// <summary>Where the loopback mode of a model came from.</summary>
public enum LoopbackSource
{
    /// <summary>Not applicable: the model has no computer or no user.</summary>
    NotApplicable,

    /// <summary>The computer's GPOs (the winning <c>UserPolicyMode</c>).</summary>
    Settings,

    /// <summary>The computer's GPOs do not configure loopback.</summary>
    NotConfigured,

    /// <summary>The operator's choice.</summary>
    Operator,
}

/// <summary>The choices of a model besides its targets.</summary>
public sealed record ModelingOptions
{
    public WmiFilterAssumption WmiFilters { get; init; } = WmiFilterAssumption.AssumeTrue;

    public LoopbackChoice Loopback { get; init; } = LoopbackChoice.FromSettings;
}

/// <summary>
/// A computer or user the model is computed for: an account (its parent container gives the OU chain) or a container
/// (a new account there). The token holds the SIDs the security filtering of GPOs is checked against.
/// </summary>
public sealed record ModelingTarget
{
    /// <summary>The container whose chain of OUs applies (the account's parent, or the chosen container).</summary>
    public required string ContainerDn { get; init; }

    /// <summary>The account; null for a new account in <see cref="ContainerDn"/>.</summary>
    public string? AccountDn { get; init; }

    /// <summary>The SIDs of the token (the account, its groups through <c>tokenGroups</c>, the well-known SIDs).</summary>
    public required IReadOnlySet<string> Token { get; init; }

    /// <summary>The token was read without nested groups (<c>tokenGroups</c> not readable): the filtering may differ.</summary>
    public bool TokenIncomplete { get; init; }

    /// <summary>The computer is a domain controller (account policy comes from GPOs linked to the domain only).</summary>
    public bool IsDomainController { get; init; }
}

/// <summary>A GPO as the model needs it (its group policy container).</summary>
public sealed record ModelingGpo
{
    /// <summary><c>flags</c> bit: the user configuration is disabled.</summary>
    public const int UserSettingsDisabledFlag = 1;

    /// <summary><c>flags</c> bit: the computer configuration is disabled.</summary>
    public const int ComputerSettingsDisabledFlag = 2;

    public required Guid Id { get; init; }

    /// <summary>displayName, else the GUID in braces.</summary>
    public required string DisplayName { get; init; }

    public int Flags { get; init; }

    /// <summary><c>versionNumber</c> of the container; null when unknown (a GPO is then not taken as empty).</summary>
    public GpoVersion? Version { get; init; }

    /// <summary>A WMI filter is linked (<c>gPCWQLFilter</c>).</summary>
    public bool HasWmiFilter { get; init; }

    /// <summary>The linked WMI filter's name (null: none, or not found).</summary>
    public string? WmiFilterName { get; init; }

    /// <summary>The security filtering; null when the descriptor was not read (the GPO then applies).</summary>
    public GroupPolicySecurityFilter? SecurityFilter { get; init; }

    /// <summary>The configuration of <paramref name="part"/> is disabled.</summary>
    public bool IsDisabled(ModelingPart part) =>
        (Flags & (part == ModelingPart.Computer ? ComputerSettingsDisabledFlag : UserSettingsDisabledFlag)) != 0;

    /// <summary>The configuration of <paramref name="part"/> was never written (its half of the version is 0).</summary>
    public bool IsEmpty(ModelingPart part) => Version is { } version && (part == ModelingPart.Computer ? version.Computer : version.User) == 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"GPO {Id:B}");
}

/// <summary>What one GPO's folder holds for the model (read from SYSVOL of the session's DC).</summary>
public sealed record ModelingGpoContent
{
    /// <summary>The normalized settings of every content file (<see cref="Management.GroupPolicyReader.Settings"/>).</summary>
    public IReadOnlyList<GpoSetting> Settings { get; init; } = [];

    /// <summary><c>Machine/Registry.pol</c>, null when the GPO has none (or it cannot be parsed – then a marker is in <see cref="Settings"/>).</summary>
    public RegistryPolicyDocument? MachineRegistry { get; init; }

    /// <summary><c>User/Registry.pol</c>.</summary>
    public RegistryPolicyDocument? UserRegistry { get; init; }

    /// <summary>The folder could not be read from SYSVOL (missing, access denied, too large).</summary>
    public bool Unreadable { get; init; }

    public static ModelingGpoContent Empty { get; } = new();

    /// <summary><c>Registry.pol</c> of <paramref name="part"/>.</summary>
    public RegistryPolicyDocument? Registry(ModelingPart part) => part == ModelingPart.Computer ? MachineRegistry : UserRegistry;
}

/// <summary>Everything the modeller reads, except the GPOs' folders.</summary>
public sealed record GroupPolicyModelInput
{
    /// <summary>The domain head (the top of every OU chain).</summary>
    public required string DomainDn { get; init; }

    /// <summary>The domain head, the OUs and the sites with their links and block inheritance.</summary>
    public required IReadOnlyList<GroupPolicySom> Containers { get; init; }

    /// <summary>The GPOs of the domain by GUID.</summary>
    public required IReadOnlyDictionary<Guid, ModelingGpo> Gpos { get; init; }

    /// <summary>The computer (null: a model of the user configuration only).</summary>
    public ModelingTarget? Computer { get; init; }

    /// <summary>The user (null: a model of the computer configuration only).</summary>
    public ModelingTarget? User { get; init; }

    /// <summary>The computer's site; null when unknown (site links then do not apply).</summary>
    public string? SiteDn { get; init; }

    public ModelingOptions Options { get; init; } = new();

    /// <summary>The ADMX templates the policies are named by (empty: every Registry.pol value is an extra value).</summary>
    public PolicyDefinitionSet Definitions { get; init; } = PolicyDefinitionSet.Empty;
}

/// <summary>Why a GPO of the chain applies or not.</summary>
public enum ModeledGpoOutcome
{
    /// <summary>Applies.</summary>
    Applied,

    /// <summary>Applies if its WMI filter is true (assumed true).</summary>
    AppliedIfWmiFilter,

    /// <summary>The link is disabled.</summary>
    LinkDisabled,

    /// <summary>A container below blocks inheritance and the link is not enforced.</summary>
    Blocked,

    /// <summary>Linked again with a higher precedence: counted there.</summary>
    Duplicate,

    /// <summary>The link names no group policy container of this domain.</summary>
    NotFound,

    /// <summary>The link names a GPO of another domain of the forest (e.g. linked to a site): not modelled.</summary>
    OtherDomain,

    /// <summary>The GPO's configuration of this part is disabled.</summary>
    PartDisabled,

    /// <summary>The configuration of this part was never written (version 0).</summary>
    Empty,

    /// <summary>Security filtering: the token has no Apply Group Policy, or it is denied.</summary>
    SecurityDenied,

    /// <summary>User part: the computer may not read the GPO (needed since MS16-072).</summary>
    ComputerCannotRead,

    /// <summary>The WMI filter is assumed false.</summary>
    WmiFilterFalse,
}

/// <summary>A GPO of a part's chain with its outcome.</summary>
public sealed record ModeledGpo
{
    public required Guid GpoId { get; init; }

    /// <summary>The GPO's name (its GUID in braces when it does not exist).</summary>
    public required string Name { get; init; }

    /// <summary>The site, domain head or OU of the link.</summary>
    public required GroupPolicySom Container { get; init; }

    public required GroupPolicyLink Link { get; init; }

    public required ModeledGpoOutcome Outcome { get; init; }

    /// <summary>1 = applied last (wins), as GPMC numbers it; null when the GPO does not apply.</summary>
    public int? Precedence { get; init; }

    /// <summary>The WMI filter's name (null: none or unknown).</summary>
    public string? WmiFilterName { get; init; }

    /// <summary><see cref="ModeledGpoOutcome.Blocked"/>: the container that blocks inheritance.</summary>
    public string? BlockedBy { get; init; }

    /// <summary>User part with loopback: the link is in the computer's chain.</summary>
    public bool FromLoopback { get; init; }

    public bool Applies => Outcome is ModeledGpoOutcome.Applied or ModeledGpoOutcome.AppliedIfWmiFilter;
}

/// <summary>A GPO a setting comes from.</summary>
/// <param name="GpoId">The GPO.</param>
/// <param name="GpoName">Its name.</param>
/// <param name="Precedence">Its precedence in the part (1 wins).</param>
public sealed record ModeledSource(Guid GpoId, string GpoName, int Precedence);

/// <summary>An administrative template policy as one GPO configures it.</summary>
public sealed record ModeledPolicySource(ModeledSource Source, PolicySetting Setting);

/// <summary>An administrative template policy configured by the applied GPOs: the winner and the overridden GPOs.</summary>
public sealed record ModeledPolicy(PolicyDefinition Definition, ModeledPolicySource Winner, IReadOnlyList<ModeledPolicySource> Overridden);

/// <summary>A setting as one GPO has it.</summary>
/// <param name="Source">The GPO.</param>
/// <param name="Setting">Its setting.</param>
/// <param name="Conditional">A preference item with item-level targeting (it applies only where its filters match).</param>
public sealed record ModeledSettingSource(ModeledSource Source, GpoSetting Setting, bool Conditional = false);

/// <summary>
/// A setting (normalized key) of the applied GPOs: the winner (highest precedence) and the overridden GPOs; for a
/// cumulative setting (scripts) every source applies and <see cref="Overridden"/> lists the others that also run.
/// </summary>
public sealed record ModeledSetting(string Key, GpoSettingCategory Category, ModeledSettingSource Winner, IReadOnlyList<ModeledSettingSource> Overridden, bool Cumulative = false);

/// <summary>What a model could not know or chose to leave out.</summary>
public enum ModelingWarningKind
{
    /// <summary>No site: site links were not taken into account.</summary>
    NoSite,

    /// <summary>A GPO's folder could not be read from SYSVOL: its settings are missing.</summary>
    ContentUnreadable,

    /// <summary>A content file of a GPO could not be parsed (<see cref="ModelingWarning.Detail"/>: its path).</summary>
    FileUnreadable,

    /// <summary>No ADMX templates: the Registry.pol values are listed as values.</summary>
    NoDefinitions,

    /// <summary>A DC: the account policy of a GPO linked below the domain head does not apply to domain accounts.</summary>
    AccountPolicyBelowDomain,

    /// <summary>The token lacks nested groups (<c>tokenGroups</c> not readable).</summary>
    TokenIncomplete,

    /// <summary>User part without a computer: the computer's read access (MS16-072) and loopback are not checked.</summary>
    NoComputer,
}

/// <summary>A warning of a part.</summary>
public sealed record ModelingWarning(ModelingWarningKind Kind, Guid? GpoId = null, string? GpoName = null, string? Detail = null);

/// <summary>One part of a model.</summary>
public sealed record ModeledPart
{
    public required ModelingPart Part { get; init; }

    /// <summary>Every GPO of the chain: those that apply (by precedence, 1 first), then the others in chain order.</summary>
    public required IReadOnlyList<ModeledGpo> Gpos { get; init; }

    /// <summary>The configured administrative template policies (by category path and name).</summary>
    public required IReadOnlyList<ModeledPolicy> Policies { get; init; }

    /// <summary>The other settings, extra registry values included (by key).</summary>
    public required IReadOnlyList<ModeledSetting> Settings { get; init; }

    public required IReadOnlyList<ModelingWarning> Warnings { get; init; }

    /// <summary>The GPOs that apply, highest precedence first.</summary>
    public IEnumerable<ModeledGpo> Applied => Gpos.Where(g => g.Applies).OrderBy(g => g.Precedence);
}

/// <summary>A Group Policy model: what a computer and a user would get.</summary>
public sealed record GroupPolicyModel
{
    /// <summary>The computer configuration (null when the model has no computer).</summary>
    public ModeledPart? Computer { get; init; }

    /// <summary>The user configuration (null when the model has no user).</summary>
    public ModeledPart? User { get; init; }

    public LoopbackMode Loopback { get; init; }

    public LoopbackSource LoopbackSource { get; init; }

    /// <summary>The computer GPO whose <c>UserPolicyMode</c> decided the loopback mode (<see cref="LoopbackSource.Settings"/>).</summary>
    public ModeledSource? LoopbackSetBy { get; init; }
}
