using System.Collections.Concurrent;
using System.Globalization;
using AdMng.Core.GroupPolicy.Content;
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

    /// <summary>The run budget was used up before this GPO started: nothing was read (<see cref="Status"/> is <see cref="GpoFolderReadStatus.Failed"/>).</summary>
    public bool Skipped { get; init; }

    /// <summary>The folder's security descriptor (DACL only) when asked for and readable.</summary>
    public byte[]? Security { get; init; }

    /// <summary>The folder ACL was asked for (<see cref="GpoFolderRequest.WithSecurity"/>).</summary>
    public bool SecurityRead { get; init; }

    /// <summary>The diagnostics view of this result (<see cref="GpoDiagnosticsSnapshot.Folders"/>, unchanged by K7).</summary>
    public GpoFolderState ToState() => Status == GpoFolderReadStatus.Read && Content is { } content
        ? GpoFolderState.Of(content, Security, SecurityRead)
        : new GpoFolderState { Status = Status == GpoFolderReadStatus.Read ? GpoFolderReadStatus.Failed : Status };

    /// <summary>For diagnostics: GUID, status and counts only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{SysvolPaths.FolderName(GpoId)} {Status} ({Content?.Files.Count ?? 0} files, {NotRead.Count} not read, {OtherFiles.Count} other)");
}

/// <summary>
/// Reads many GPO folders of the session's DC (rule 4) through one global gate of <see cref="GpoFolderReadOptions.Parallelism"/>
/// SYSVOL requests – the extracted K6 diagnostics read (docs/dev/k-computers-and-gpo.md 21.1). Per-folder failures are
/// statuses; only cancellation stops a run. Nothing is written anywhere (rule 1).
/// </summary>
public static class GpoFolderReader
{
    /// <summary>
    /// Reads every folder and returns all results at once (diagnostics with <see cref="GpoFolderReadMode.Full"/>, tests).
    /// The index never uses this overload: it would hold the raw bytes of every GPO at once.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, GpoFolderReadResult>> ReadAsync(
        ISysvolWriteConnection sysvol, IReadOnlyList<GpoFolderRequest> gpos, GpoFolderReadOptions options,
        IProgress<GpoFolderReadProgress>? progress, CancellationToken cancellationToken)
    {
        var results = new ConcurrentDictionary<Guid, GpoFolderReadResult>();
        await ReadAsync(sysvol, gpos, options, (result, _) =>
        {
            results[result.GpoId] = result;
            return ValueTask.CompletedTask;
        }, progress, cancellationToken).ConfigureAwait(false);
        return new Dictionary<Guid, GpoFolderReadResult>(results);
    }

    /// <summary>
    /// Streaming read: <paramref name="onRead"/> runs for each GPO as soon as its folder is read – at most
    /// <see cref="GpoFolderReadOptions.Parallelism"/> callbacks at a time, each awaited before its worker takes the next GPO –
    /// and the caller parses and drops the bytes inside it, so at most that many folders are in memory at once. A GPO
    /// requested twice is read once (the first request wins).
    /// </summary>
    public static async Task ReadAsync(
        ISysvolWriteConnection sysvol, IReadOnlyList<GpoFolderRequest> gpos, GpoFolderReadOptions options,
        Func<GpoFolderReadResult, CancellationToken, ValueTask> onRead,
        IProgress<GpoFolderReadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sysvol);
        ArgumentNullException.ThrowIfNull(gpos);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onRead);
        var work = gpos.DistinctBy(g => g.GpoId).ToList();
        var parallelism = Math.Max(1, options.Parallelism);
        var done = 0;
        var reportGate = new Lock();
        long total = 0;
        progress?.Report(new GpoFolderReadProgress(0, work.Count));
        await using var throttled = new ThrottledSysvolConnection(sysvol, parallelism);
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken }, async (request, token) =>
        {
            GpoFolderReadResult result;
            if (options.Mode == GpoFolderReadMode.ContentOnly && Interlocked.Read(ref total) >= options.MaxTotalBytes)
            {
                result = new GpoFolderReadResult { GpoId = request.GpoId, Status = GpoFolderReadStatus.Failed, Skipped = true };
            }
            else
            {
                result = options.Mode == GpoFolderReadMode.ContentOnly
                    ? await ReadContentAsync(throttled, request.GpoId, options, token).ConfigureAwait(false)
                    : await ReadFullAsync(throttled, request, token).ConfigureAwait(false);
                if (result.Content is { } content)
                {
                    Interlocked.Add(ref total, content.TotalBytes);
                }
            }

            await onRead(result, token).ConfigureAwait(false);

            // Counted and reported under one lock: otherwise the reports of two workers can overtake each other and
            // the progress ends below the total.
            lock (reportGate)
            {
                progress?.Report(new GpoFolderReadProgress(++done, work.Count));
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Every file within the <see cref="GpoTransaction"/> limits and, when asked, the folder ACL (the K6 read).</summary>
    private static async Task<GpoFolderReadResult> ReadFullAsync(ISysvolWriteConnection sysvol, GpoFolderRequest request, CancellationToken cancellationToken)
    {
        var gpoId = request.GpoId;
        GpoFolderContent content;
        try
        {
            content = await GroupPolicyReader.ReadFolderAsync(sysvol, gpoId, cancellationToken).ConfigureAwait(false);
        }
        catch (SysvolWriteException ex) when (ex.Error == SysvolWriteError.NotFound)
        {
            // The folder, or a file listed and gone before it was read (a save going on): only the former is missing.
            return await NotFoundAsync(sysvol, gpoId, cancellationToken).ConfigureAwait(false);
        }
        catch (SysvolWriteException ex) when (ex.Error is SysvolWriteError.AccessDenied or SysvolWriteError.LogonFailure)
        {
            return Failure(gpoId, GpoFolderReadStatus.AccessDenied);
        }
        catch (GpoTooLargeException)
        {
            return Failure(gpoId, GpoFolderReadStatus.TooLarge);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Any other failure of one folder (SYSVOL error, a path SYSVOL rejects …) leaves only that folder unread.
            return Failure(gpoId, GpoFolderReadStatus.Failed);
        }

        byte[]? security = null;
        if (request.WithSecurity)
        {
            try
            {
                security = await sysvol.GetGpoFolderSecurityAsync(gpoId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                security = null; // the ACL rule is skipped for this folder
            }
        }

        return new GpoFolderReadResult
        {
            GpoId = gpoId,
            Status = GpoFolderReadStatus.Read,
            Content = content,
            Security = security,
            SecurityRead = request.WithSecurity,
        };
    }

    /// <summary>
    /// One recursive listing, then the files a parser reads, in <see cref="GpoFile.Priority"/> order, one after the other
    /// within the file and GPO budgets (the settings index; modelled on the Watchdog's read planner).
    /// </summary>
    private static async Task<GpoFolderReadResult> ReadContentAsync(ISysvolWriteConnection sysvol, Guid gpoId, GpoFolderReadOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await sysvol.ListGpoFolderAsync(gpoId, SysvolListOptions.Default, cancellationToken).ConfigureAwait(false);
            if (entries.Count > GpoTransaction.MaxEntriesPerGpo)
            {
                return Failure(gpoId, GpoFolderReadStatus.TooLarge);
            }

            var leftovers = new List<string>();
            var other = new List<string>();
            var wanted = new List<(SysvolEntry Entry, GpoFile File)>();
            foreach (var entry in entries.Where(e => !e.IsFolder))
            {
                if (GpoTransaction.IsTempName(entry.RelativePath))
                {
                    leftovers.Add(entry.RelativePath);
                    continue;
                }

                var file = GpoFileClassifier.Classify(entry.RelativePath);
                if (IsContentRole(file.Role))
                {
                    wanted.Add((entry, file));
                }
                else if (file.Role == GpoFileRole.Inventory && file.Scope != GpoScope.Gpo)
                {
                    other.Add(entry.RelativePath);
                }
            }

            var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var notRead = new List<string>();
            long read = 0;
            var budgetUsed = false;
            foreach (var (entry, _) in wanted.OrderBy(w => w.File.Priority).ThenBy(w => w.Entry.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                if (!budgetUsed && entry.Length <= options.MaxFileBytes && read + entry.Length > options.MaxGpoBytes)
                {
                    budgetUsed = true; // the GPO's budget is used up: this file and every later one stay unread
                }

                if (budgetUsed || entry.Length > options.MaxFileBytes)
                {
                    notRead.Add(entry.RelativePath);
                    continue;
                }

                var file = await sysvol.ReadGpoFileAsync(gpoId, entry.RelativePath, options.MaxFileBytes, cancellationToken).ConfigureAwait(false);
                switch (file.Status)
                {
                    case SysvolFileStatus.Read:
                        contents[entry.RelativePath] = file.Content!;
                        read += file.Content!.Length;
                        break;
                    case SysvolFileStatus.TooLarge:
                        notRead.Add(entry.RelativePath); // grew after the listing
                        break;
                    case SysvolFileStatus.AccessDenied:
                        return Failure(gpoId, GpoFolderReadStatus.AccessDenied);
                    case SysvolFileStatus.NotFound:
                        return await NotFoundAsync(sysvol, gpoId, cancellationToken).ConfigureAwait(false);
                    default:
                        return Failure(gpoId, GpoFolderReadStatus.Failed);
                }
            }

            return new GpoFolderReadResult
            {
                GpoId = gpoId,
                Status = GpoFolderReadStatus.Read,
                Content = new GpoFolderContent(entries, contents) { Leftovers = leftovers },
                NotRead = notRead,
                OtherFiles = other,
            };
        }
        catch (SysvolWriteException ex) when (ex.Error == SysvolWriteError.NotFound)
        {
            return await NotFoundAsync(sysvol, gpoId, cancellationToken).ConfigureAwait(false);
        }
        catch (SysvolWriteException ex) when (ex.Error is SysvolWriteError.AccessDenied or SysvolWriteError.LogonFailure)
        {
            return Failure(gpoId, GpoFolderReadStatus.AccessDenied);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Failure(gpoId, GpoFolderReadStatus.Failed);
        }
    }

    /// <summary>The roles a parser of <see cref="GroupPolicyReader.Settings"/> reads (GPT.INI, script payloads and inventory files are not read).</summary>
    private static bool IsContentRole(GpoFileRole role) => role is GpoFileRole.SecurityTemplate or GpoFileRole.AuditCsv or GpoFileRole.RegistryPol
        or GpoFileRole.Preferences or GpoFileRole.ScriptsIni or GpoFileRole.PsScriptsIni or GpoFileRole.Comment or GpoFileRole.GpoComment;

    private static GpoFolderReadResult Failure(Guid gpoId, GpoFolderReadStatus status) => new() { GpoId = gpoId, Status = status };

    /// <summary>
    /// After "not found" while reading a folder: <see cref="GpoFolderReadStatus.NotFound"/> when the GPO's folder itself does
    /// not exist, else (a file gone meanwhile, or the existence cannot be told) <see cref="GpoFolderReadStatus.Failed"/>.
    /// </summary>
    private static async Task<GpoFolderReadResult> NotFoundAsync(ISysvolWriteConnection sysvol, Guid gpoId, CancellationToken cancellationToken)
    {
        bool missing;
        try
        {
            missing = !await sysvol.GpoFolderExistsAsync(gpoId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            missing = false;
        }

        return Failure(gpoId, missing ? GpoFolderReadStatus.NotFound : GpoFolderReadStatus.Failed);
    }
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
