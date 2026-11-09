using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;

namespace AdMng.Presentation.GroupPolicy.Copy;

/// <summary>
/// K7 contract (docs/dev/k-computers-and-gpo.md 21.5, 21.11): one unit offered for copy, remove or merge, with the texts
/// the copy window shows, so that the search and the cleanup document need no shared state with the copy UI. The texts
/// are directory values: <see cref="ToString"/> prints the GPO GUID and the refusal only.
/// </summary>
public sealed record GpoCopyItem(GpoUnitSelection Unit, string SourceGpoName, string Name, string? GroupPath, string? ValueText,
    GpoUnitRefusal? Refusal)
{
    /// <summary>For diagnostics: the GPO GUID and the refusal only.</summary>
    public override string ToString() => SysvolPaths.FolderName(Unit.GpoId) + (Refusal is { } refusal ? " " + refusal : string.Empty);
}
