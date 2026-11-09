using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;

namespace AdMng.Core.GroupPolicy.Management;

// K7 contract (docs/dev/k-computers-and-gpo.md 21.5, 21.11) – implementation: W2.
public sealed partial class GroupPolicyPlanner
{
    /// <summary>
    /// Copies units of up to <see cref="GpoSettingsCopyRequest.MaxSources"/> source GPOs into one target GPO: one plan with
    /// one <see cref="ObjectPlan"/> for the target (one extension-name Modify, one WriteGpoFile per touched file built from
    /// bytes read now on the session's DC, one version change). Refused units are issues; the rest goes on.
    /// </summary>
    public Task<GpoSettingsPlan> PlanCopySettingsAsync(IDirectorySession session, ISysvolWriteConnection sysvol,
        GpoSettingsCopyRequest request, CancellationToken cancellationToken) =>
        throw new NotImplementedException("K7 W2: PlanCopySettingsAsync");

    /// <summary>Removes units from one GPO (same reads, writers and refusals as the copy; one version bump).</summary>
    public Task<GpoSettingsPlan> PlanRemoveSettingsAsync(IDirectorySession session, ISysvolWriteConnection sysvol, Guid gpoId,
        IReadOnlyList<GpoUnitSelection> units, PolicyDefinitionSet? definitions, CancellationToken cancellationToken) =>
        throw new NotImplementedException("K7 W2: PlanRemoveSettingsAsync");

    /// <summary>
    /// Merges A into B as one sequential plan: copy A's units into B, replace A's links by B (one gPLink compare-and-swap
    /// per container), set A to all settings disabled. Refused with an error when A cannot be carried completely (21.5).
    /// </summary>
    public Task<GpoSettingsPlan> PlanMergeAsync(IDirectorySession session, ISysvolWriteConnection sysvol,
        GpoMergeRequest request, CancellationToken cancellationToken) =>
        throw new NotImplementedException("K7 W2: PlanMergeAsync");

    /// <summary>Removes every link of the GPO in this domain (domain head, OUs, sites): one gPLink compare-and-swap per container.</summary>
    public Task<ChangePlan> PlanUnlinkAllAsync(IDirectorySession session, Guid gpoId, CancellationToken cancellationToken) =>
        throw new NotImplementedException("K7 W2: PlanUnlinkAllAsync");

    /// <summary>
    /// <see cref="PlanDeleteAsync(IDirectorySession, Guid, CancellationToken)"/> guarded by what the cleanup advisor saw: an
    /// error <see cref="IssueCodes.GpoChangedSinceAnalysis"/> when the GPO's version or links moved since.
    /// </summary>
    public Task<ChangePlan> PlanDeleteAsync(IDirectorySession session, Guid gpoId, GpoExpectedState expected, CancellationToken cancellationToken) =>
        throw new NotImplementedException("K7 W2: PlanDeleteAsync (expected state)");
}
