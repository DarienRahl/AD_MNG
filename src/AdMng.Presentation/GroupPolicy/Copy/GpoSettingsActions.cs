using AdMng.Core.GroupPolicy.Management;

namespace AdMng.Presentation.GroupPolicy.Copy;

/// <summary>
/// K7 contract (docs/dev/k-computers-and-gpo.md 21.5, 21.11) – implementation: W5. Copy, remove, merge, unlink-all and
/// the advisor's guarded delete, each a plan through <c>GroupPolicyActions.RunAsync</c> with preview and undo. Until W5
/// completes them every method returns false (nothing done).
/// </summary>
public static class GpoSettingsActions
{
    /// <summary>Copies the items into a GPO (picked when <paramref name="target"/> is null; at most 10 source GPOs).</summary>
    public static Task<bool> CopySettingsAsync(this GroupPolicyActions actions, IReadOnlyList<GpoCopyItem> items, GpoInfo? target = null) =>
        Task.FromResult(false);

    /// <summary>Removes the items from one GPO.</summary>
    public static Task<bool> RemoveSettingsAsync(this GroupPolicyActions actions, GpoInfo gpo, IReadOnlyList<GpoCopyItem> items) =>
        Task.FromResult(false);

    /// <summary>Merges <paramref name="source"/> into <paramref name="target"/> (one plan: copy, relink, disable the source).</summary>
    public static Task<bool> MergeAsync(this GroupPolicyActions actions, GpoInfo source, GpoInfo target, IReadOnlyList<GpoCopyItem> sourceItems) =>
        Task.FromResult(false);

    /// <summary>Removes every link of the GPO in this domain.</summary>
    public static Task<bool> UnlinkAllAsync(this GroupPolicyActions actions, GpoInfo gpo) =>
        Task.FromResult(false);

    /// <summary>The advisor's delete: refused when the GPO changed since the analysis, then <c>GroupPolicyActions.DeleteAsync</c> (backup offer).</summary>
    public static Task<bool> DeleteCheckedAsync(this GroupPolicyActions actions, GpoInfo gpo, GpoExpectedState expected) =>
        Task.FromResult(false);
}
