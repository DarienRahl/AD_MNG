using System.Globalization;
using AdMng.Core.GroupPolicy;

namespace AdMng.Core.Batch;

/// <summary>
/// Data of a SYSVOL step (design note docs/dev/k-computers-and-gpo.md 3.3). File contents and folder descriptors are kept
/// in memory only - for verification, the preview diff and the rollback - and die with the plan; <see cref="ToString"/>
/// prints the path and byte counts, never content (rule 1).
/// </summary>
public sealed record SysvolChange
{
    /// <summary>The GPO (its folder <c>{GUID}</c> on SYSVOL of the session's DC).</summary>
    public required Guid GpoId { get; init; }

    /// <summary>'/'-separated path below the GPO folder (<see cref="ChangeAction.WriteGpoFile"/>, <see cref="ChangeAction.DeleteGpoFile"/>).</summary>
    public string? RelativePath { get; init; }

    /// <summary>The file as read while planning; null when it did not exist (a new file).</summary>
    public byte[]? OldContent { get; init; }

    /// <summary>The content to write (<see cref="ChangeAction.WriteGpoFile"/>).</summary>
    public byte[]? NewContent { get; init; }

    /// <summary>The folder's descriptor read while planning (rollback of a sync; never written back unless rolled back).</summary>
    public byte[]? OldFolderSecurity { get; init; }

    /// <summary>The folder descriptor to write (<see cref="ChangeAction.SetGpoFolderSecurity"/>).</summary>
    public byte[]? NewFolderSecurity { get; init; }

    /// <summary>
    /// <see cref="ChangeAction.DeleteGpoFolder"/> of a rollback: delete only a folder that still holds nothing but
    /// <c>GPT.INI</c> with <see cref="PristineVersion"/>, the <see cref="PristineFiles"/> with the content the plan wrote and
    /// folders - what the plan made, nothing anybody added since.
    /// </summary>
    public bool OnlyIfPristine { get; init; }

    /// <summary>With <see cref="OnlyIfPristine"/>: the files the plan wrote (path → content) that may be in the folder.</summary>
    public IReadOnlyDictionary<string, byte[]>? PristineFiles { get; init; }

    /// <summary>With <see cref="OnlyIfPristine"/>: the version GPT.INI must still have (null: 0).</summary>
    public GpoVersion? PristineVersion { get; init; }

    /// <summary>Paths, sizes and the GUID only.</summary>
    public override string ToString()
    {
        var path = RelativePath ?? SysvolPaths.FolderName(GpoId);
        return OldContent is null && NewContent is null
            ? path
            : string.Create(CultureInfo.InvariantCulture, $"{path} {Size(OldContent)} -> {Size(NewContent)}");
    }

    private static string Size(byte[]? content) => content is null ? "absent" : string.Create(CultureInfo.InvariantCulture, $"{content.Length} B");
}

/// <summary>
/// Old and new version of <see cref="ChangeAction.SetGpoVersion"/>. The transaction compares the container's
/// <c>versionNumber</c> with <see cref="OldDirectory"/> and the <c>Version=</c> of <c>GPT.INI</c> with <see cref="OldGptIni"/>
/// before it changes anything; a difference is a conflict.
/// </summary>
/// <param name="OldDirectory"><c>versionNumber</c> read while planning (null: not set).</param>
/// <param name="OldGptIni"><c>Version</c> of <c>GPT.INI</c> read while planning (null: no file or no line).</param>
/// <param name="New">The version written to both.</param>
public sealed record GpoVersionChange(GpoVersion? OldDirectory, GpoVersion? OldGptIni, GpoVersion New)
{
    /// <summary>The version the GPO had (the higher half of AD and GPT.INI), for the preview.</summary>
    public GpoVersion Old => GroupPolicy.GpoVersion.Max(OldDirectory ?? GroupPolicy.GpoVersion.Zero, OldGptIni ?? GroupPolicy.GpoVersion.Zero);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Old} -> {New}");
}
