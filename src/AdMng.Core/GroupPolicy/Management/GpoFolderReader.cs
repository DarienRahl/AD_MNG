using System.Globalization;
using AdMng.Core.GroupPolicy.Diagnostics;

namespace AdMng.Core.GroupPolicy.Management;

// K7 contract (docs/dev/k-computers-and-gpo.md 21.1, 21.11) – implementation: W1.

/// <summary>How <see cref="GpoFolderReader"/> reads a GPO folder.</summary>
public enum GpoFolderReadMode
{
    /// <summary>Every file within the <see cref="GpoTransaction"/> limits, as <see cref="GroupPolicyReader.ReadFolderAsync"/> (diagnostics).</summary>
    Full,

    /// <summary>
    /// One recursive listing, then only the files a parser reads (GptTmpl.inf, audit.csv, Registry.pol, preferences,
    /// scripts.ini, psscripts.ini, comments) within the budgets of <see cref="GpoFolderReadOptions"/> (the settings index).
    /// </summary>
    ContentOnly,
}

/// <summary>Mode, throttle and budgets of a <see cref="GpoFolderReader"/> run.</summary>
public sealed record GpoFolderReadOptions
{
    public GpoFolderReadMode Mode { get; init; } = GpoFolderReadMode.Full;

    /// <summary>SYSVOL requests at a time across the whole run (one global gate, nested reads included).</summary>
    public int Parallelism { get; init; } = 4;

    /// <summary><see cref="GpoFolderReadMode.ContentOnly"/>: a larger content file is not read and listed in <see cref="GpoFolderReadResult.NotRead"/>.</summary>
    public long MaxFileBytes { get; init; } = GpoTransaction.MaxFileBytes;

    /// <summary><see cref="GpoFolderReadMode.ContentOnly"/>: once a GPO's read bytes reach this, its remaining files go to <see cref="GpoFolderReadResult.NotRead"/>.</summary>
    public long MaxGpoBytes { get; init; } = GpoTransaction.MaxPlanBytes;

    /// <summary><see cref="GpoFolderReadMode.ContentOnly"/>: once the run has read this much, GPOs not started yet are <see cref="GpoFolderReadResult.Skipped"/>.</summary>
    public long MaxTotalBytes { get; init; } = long.MaxValue;

    /// <summary>Full read, as the K6 diagnostics read today.</summary>
    public static GpoFolderReadOptions Default { get; } = new();

    /// <summary>The settings index: content only, 16 MiB per file, 64 MiB per GPO, 1 GiB per run.</summary>
    public static GpoFolderReadOptions Settings { get; } = new()
    {
        Mode = GpoFolderReadMode.ContentOnly,
        MaxFileBytes = 16L << 20,
        MaxGpoBytes = 64L << 20,
        MaxTotalBytes = 1L << 30,
    };
}

/// <summary>One GPO folder to read; <paramref name="WithSecurity"/> also reads the folder's DACL (diagnostics, GPOs in scope).</summary>
public readonly record struct GpoFolderRequest(Guid GpoId, bool WithSecurity = false);

/// <summary>Folders finished so far.</summary>
public readonly record struct GpoFolderReadProgress(int Done, int Total);

/// <summary>
/// The outcome of reading one GPO folder (in memory only, CLAUDE.md rule 1). Per-folder failures are statuses; only
/// cancellation stops a run.
/// </summary>
public sealed record GpoFolderReadResult
{
    public required Guid GpoId { get; init; }

    /// <summary><see cref="GpoFolderReadStatus.Read"/> also when <see cref="NotRead"/> is not empty (a budget kept files out).</summary>
    public required GpoFolderReadStatus Status { get; init; }

    /// <summary>The files read (<see cref="GpoFolderReadStatus.Read"/> only).</summary>
    public GpoFolderContent? Content { get; init; }

    /// <summary>Content files a budget kept out (relative paths).</summary>
    public IReadOnlyList<string> NotRead { get; init; } = [];

    /// <summary>
    /// <see cref="GpoFolderReadMode.ContentOnly"/>: inventory files below <c>Machine/</c> or <c>User/</c> no parser reads
    /// (folder redirection, software installation …) – they may hold settings, so such a GPO is never "empty".
    /// </summary>
    public IReadOnlyList<string> OtherFiles { get; init; } = [];

    /// <summary>The run budget was used up before this GPO started: nothing was read.</summary>
    public bool Skipped { get; init; }

    /// <summary>The folder's security descriptor (DACL only) when asked for and readable.</summary>
    public byte[]? Security { get; init; }

    /// <summary>The folder ACL was asked for (<see cref="GpoFolderRequest.WithSecurity"/>).</summary>
    public bool SecurityRead { get; init; }

    /// <summary>The diagnostics view of this result (<see cref="GpoDiagnosticsSnapshot.Folders"/>, unchanged by K7).</summary>
    public GpoFolderState ToState() => throw new NotImplementedException("K7 W1: GpoFolderReadResult.ToState");

    /// <summary>For diagnostics: GUID, status and counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{SysvolPaths.FolderName(GpoId)} {Status} ({Content?.Files.Count ?? 0} files, {NotRead.Count} not read, {OtherFiles.Count} other)");
}

/// <summary>
/// Reads many GPO folders of the session's DC (rule 4) through one global gate of <see cref="GpoFolderReadOptions.Parallelism"/>
/// SYSVOL requests – the extracted K6 diagnostics read (docs/dev/k-computers-and-gpo.md 21.1).
/// </summary>
public static class GpoFolderReader
{
    /// <summary>
    /// Reads every folder and returns all results at once (diagnostics with <see cref="GpoFolderReadMode.Full"/>, tests).
    /// The index never uses this overload: it would hold the raw bytes of every GPO at once.
    /// </summary>
    public static Task<IReadOnlyDictionary<Guid, GpoFolderReadResult>> ReadAsync(
        ISysvolWriteConnection sysvol, IReadOnlyList<GpoFolderRequest> gpos, GpoFolderReadOptions options,
        IProgress<GpoFolderReadProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotImplementedException("K7 W1: GpoFolderReader.ReadAsync");

    /// <summary>
    /// Streaming read: <paramref name="onRead"/> runs for each GPO as soon as its folder is read – at most
    /// <see cref="GpoFolderReadOptions.Parallelism"/> callbacks at a time, each awaited before its worker takes the next GPO –
    /// and the caller parses and drops the bytes inside it, so at most that many folders are in memory at once.
    /// </summary>
    public static Task ReadAsync(
        ISysvolWriteConnection sysvol, IReadOnlyList<GpoFolderRequest> gpos, GpoFolderReadOptions options,
        Func<GpoFolderReadResult, CancellationToken, ValueTask> onRead,
        IProgress<GpoFolderReadProgress>? progress, CancellationToken cancellationToken) =>
        throw new NotImplementedException("K7 W1: GpoFolderReader.ReadAsync (streaming)");
}

/// <summary>
/// <see cref="GroupPolicyReader.ReadSettings"/>: the normalized settings of a folder and the files whose settings did not
/// fit into one <see cref="Content.GpoSettingSink"/> (their settings are incomplete).
/// </summary>
public sealed record GpoSettingsRead(IReadOnlyList<Content.GpoSetting> Settings, IReadOnlyList<string> OverflowedFiles)
{
    /// <summary>No file overflowed.</summary>
    public bool Complete => OverflowedFiles.Count == 0;

    /// <summary>For diagnostics: counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Settings.Count} settings, {OverflowedFiles.Count} overflowed");
}
