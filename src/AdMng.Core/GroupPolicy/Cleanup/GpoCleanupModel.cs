using System.Globalization;
using AdMng.Core.GroupPolicy.Search;

namespace AdMng.Core.GroupPolicy.Cleanup;

// K7 contract (docs/dev/k-computers-and-gpo.md 21.6, 21.11) – implementation: W3.

/// <summary>What a cleanup proposal is about.</summary>
public enum GpoCleanupKind
{
    EmptyGpo,
    UnlinkedGpo,
    AllLinksDisabled,
    GpoDisabled,
    SingleSettingGpo,
    DuplicateSetting,
    DifferentValues,
    MergeCandidate,
}

/// <summary>An action a proposal offers (each one a plan with preview and undo).</summary>
public enum GpoCleanupAction
{
    /// <summary>Delete the GPO (guarded by <c>GpoExpectedState</c>, backup offered first).</summary>
    Delete,

    /// <summary>Remove every link of the GPO in this domain.</summary>
    Unlink,

    /// <summary>Set the GPO to all settings disabled (reversible).</summary>
    Disable,

    /// <summary>Remove the duplicated unit from the covered GPO.</summary>
    RemoveSetting,

    /// <summary>Copy the GPO's settings into another GPO (SingleSettingGpo).</summary>
    CopyTo,

    /// <summary>Merge the GPO into <see cref="GpoCleanupProposal.RelatedGpoIds"/>[0].</summary>
    Merge,
}

/// <summary>Remarks on a proposal (each with its own text).</summary>
public enum GpoCleanupNote
{
    Linked,
    DefaultGpo,

    /// <summary>The forest has other domains: their links to this GPO are not visible from this DC.</summary>
    OtherDomainsNotSearched,

    LinkOrderDiffers,

    /// <summary>A unit cannot be copied (<see cref="GpoCleanupProposal.BlockingUnitIds"/>): no CopyTo/Merge.</summary>
    NonCopyableUnits,

    /// <summary>The GPO holds settings kept in the directory (CN=Machine/CN=User): no Merge.</summary>
    DirectorySettings,

    /// <summary>The GPO holds files no parser reads: no Merge.</summary>
    OtherFiles,

    /// <summary>A disabled half of the GPO holds settings, or the merge target's disabled half would take some: no Merge.</summary>
    PartDisabled,

    /// <summary>Account policy: only GPOs linked to the domain head are compared.</summary>
    AccountPolicy,

    /// <summary>The preference item has removePolicy="1": clients undo it when it disappears from the GPO.</summary>
    RemovePolicy,
}

/// <summary>One proposal of the cleanup advisor (in memory only; no directory values in <see cref="ToString"/>).</summary>
public sealed record GpoCleanupProposal
{
    public required GpoCleanupKind Kind { get; init; }

    /// <summary>The GPO the proposal is about (duplicate: the covered GPO to remove from; merge: A, the source).</summary>
    public required Guid GpoId { get; init; }

    /// <summary>Duplicates: the GPO that keeps the setting; merge: [B]; SingleSettingGpo: GPOs with the same link set.</summary>
    public IReadOnlyList<Guid> RelatedGpoIds { get; init; } = [];

    /// <summary>DuplicateSetting, DifferentValues, SingleSettingGpo: the unit's base id.</summary>
    public string? UnitBaseId { get; init; }

    /// <summary>The links the action touches.</summary>
    public IReadOnlyList<string> ContainerDns { get; init; } = [];

    public IReadOnlyList<GpoCleanupAction> Actions { get; init; } = [];

    public IReadOnlyList<GpoCleanupNote> Notes { get; init; } = [];

    /// <summary>Units of <see cref="GpoId"/> with <see cref="GpoSettingUnit.BlocksMerge"/>.</summary>
    public IReadOnlyList<string> BlockingUnitIds { get; init; } = [];

    /// <summary>For diagnostics: the kind and the GUID only.</summary>
    public override string ToString() => Kind + " " + SysvolPaths.FolderName(GpoId);
}

/// <summary>Options of <see cref="GpoCleanupAnalyzer.Analyze"/>.</summary>
public sealed record GpoCleanupOptions
{
    public static GpoCleanupOptions Default { get; } = new();

    public bool IncludeDifferentValues { get; init; } = true;
}

/// <summary>The proposals and the GPOs whose content could not be evaluated (not read completely).</summary>
public sealed record GpoCleanupResult(IReadOnlyList<GpoCleanupProposal> Proposals, IReadOnlyList<Guid> NotEvaluated)
{
    /// <summary>For diagnostics: counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Proposals.Count} proposals, {NotEvaluated.Count} not evaluated");
}
