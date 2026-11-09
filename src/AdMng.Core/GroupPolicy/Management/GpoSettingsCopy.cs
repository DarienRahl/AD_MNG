using System.Globalization;
using AdMng.Core.Batch;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;

namespace AdMng.Core.GroupPolicy.Management;

// K7 contract (docs/dev/k-computers-and-gpo.md 21.5, 21.11) – implementation: W2.

/// <summary>
/// One unit of one GPO chosen for copy or remove. <paramref name="UnitId"/> is the unit's id in that GPO
/// (<c>BaseId</c> or <c>BaseId#n</c>); <paramref name="ItemId"/> (the preference item's <c>uid</c> or
/// <c>&lt;Element&gt;#n</c>) must still match for preference units and units with an occurrence above 1;
/// <paramref name="ValueFingerprint"/> is the value the operator saw (a different fresh value adds a warning).
/// </summary>
public readonly record struct GpoUnitSelection(Guid GpoId, string UnitId, string? ItemId = null, string? ValueFingerprint = null)
{
    /// <summary>For diagnostics: the GPO GUID only (the unit id holds directory values: paths, group names, drive targets).</summary>
    public override string ToString() => SysvolPaths.FolderName(GpoId);
}

/// <summary>What happens when the target already has a unit with the same base id.</summary>
public enum GpoCopyConflictMode
{
    /// <summary>The target's occurrences (with equal item-level targeting) are removed, then the source unit is added.</summary>
    Replace,

    /// <summary>The target keeps its unit.</summary>
    Skip,

    /// <summary>User rights and restricted groups: the union of both principal lists (<see cref="Replace"/> for other kinds).</summary>
    Union,
}

/// <summary>The result for one selected unit.</summary>
public enum GpoUnitOutcomeKind { Added, Replaced, Merged, Unchanged, Skipped, Refused, Removed, NotFound }

/// <summary>The result for one selected unit and the issue that explains it.</summary>
public sealed record GpoUnitOutcome(GpoUnitSelection Unit, GpoUnitOutcomeKind Kind, string? IssueCode = null)
{
    /// <summary>For diagnostics: the kind and the GPO GUID only.</summary>
    public override string ToString() => Kind + " " + Unit;
}

/// <summary>A planned copy, remove or merge: the plan (preview, execution, undo) and the per-unit outcomes.</summary>
public sealed record GpoSettingsPlan(ChangePlan Plan, IReadOnlyList<GpoUnitOutcome> Outcomes)
{
    /// <summary>At least one unit is added, replaced, merged or removed.</summary>
    public bool ChangesAnything => Outcomes.Any(o => o.Kind is GpoUnitOutcomeKind.Added or GpoUnitOutcomeKind.Replaced
        or GpoUnitOutcomeKind.Merged or GpoUnitOutcomeKind.Removed);

    /// <summary>For diagnostics: counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Outcomes.Count} units, changes: {ChangesAnything}");
}

/// <summary>Copy units of one or more source GPOs into one target GPO (one plan for the target).</summary>
public sealed record GpoSettingsCopyRequest
{
    /// <summary>The most source GPOs one copy reads.</summary>
    public const int MaxSources = 10;

    public required Guid TargetId { get; init; }

    public required IReadOnlyList<GpoUnitSelection> Units { get; init; }

    public GpoCopyConflictMode Conflict { get; init; } = GpoCopyConflictMode.Replace;

    /// <summary>Per-unit overrides of <see cref="Conflict"/>, by base id.</summary>
    public IReadOnlyDictionary<string, GpoCopyConflictMode> ConflictByUnit { get; init; } =
        new Dictionary<string, GpoCopyConflictMode>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ADMX set the selections' units were built with (the index's set).</summary>
    public PolicyDefinitionSet? Definitions { get; init; }

    /// <summary>For diagnostics: GUID and counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{SysvolPaths.FolderName(TargetId)} {Units.Count} units {Conflict}");
}

/// <summary>Merge GPO A (source) into GPO B (target): copy, relink, disable A (one plan).</summary>
public sealed record GpoMergeRequest
{
    /// <summary>A: copied into B, its links replaced by B, then set to all settings disabled.</summary>
    public required Guid SourceId { get; init; }

    /// <summary>B.</summary>
    public required Guid TargetId { get; init; }

    /// <summary>Null: derived from the link order (B first everywhere → Skip, A first → Replace, mixed → an error asking for a choice).</summary>
    public GpoCopyConflictMode? Conflict { get; init; }

    public PolicyDefinitionSet? Definitions { get; init; }

    /// <summary>For diagnostics: the GUIDs only.</summary>
    public override string ToString() => SysvolPaths.FolderName(SourceId) + " → " + SysvolPaths.FolderName(TargetId);
}

/// <summary>
/// What the cleanup advisor saw of a GPO: a delete planned with it is refused (<see cref="IssueCodes.GpoChangedSinceAnalysis"/>)
/// when the GPO's version or links moved since.
/// </summary>
/// <param name="DirectoryVersion">The container's <c>versionNumber</c> the index read.</param>
/// <param name="LinkedContainerDns">The containers that linked the GPO (empty: unlinked).</param>
public sealed record GpoExpectedState(GpoVersion? DirectoryVersion, IReadOnlyList<string> LinkedContainerDns)
{
    /// <summary>For diagnostics: the version and the link count only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{DirectoryVersion} ({LinkedContainerDns.Count} links)");
}
