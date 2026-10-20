using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Gpo;

/// <summary>The read limits of stage B design 8.3 (runtime <c>Directory.GpoContent</c> and the fixed constants).</summary>
public sealed record GpoReadLimits
{
    /// <summary>Script files above this size are listed with their size only (8.4: "≤ 1 MiB … content hash only").</summary>
    public const long ScriptHashBytes = 1024 * 1024;

    /// <summary>Entries (files and folders) per GPO.</summary>
    public int MaxEntries { get; init; } = 2000;

    public long MaxFileBytes { get; init; } = 5L * 1024 * 1024;

    public long MaxTotalBytes { get; init; } = 50L * 1024 * 1024;

    public int MaxDepth { get; init; } = GpoContentSettings.MaxDepth;

    public int MaxSettings { get; init; } = GpoContentSettings.MaxSettings;

    public static GpoReadLimits Default { get; } = new();

    public static GpoReadLimits From(GpoContentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new GpoReadLimits
        {
            MaxEntries = Math.Max(1, settings.MaxFiles),
            MaxFileBytes = Math.Max(1, settings.MaxFileMb) * 1024L * 1024,
            MaxTotalBytes = Math.Max(1, settings.MaxTotalMb) * 1024L * 1024,
        };
    }
}

/// <summary>Names of the exceeded limits (health Args of <c>gpo.&lt;guid&gt;.tooLarge</c>) and of listing problems.</summary>
public static class GpoReadProblems
{
    public const string MaxEntries = "maxFiles";
    public const string MaxDepth = "maxDepth";
    public const string MaxFileSize = "maxFileSize";
    public const string MaxTotalSize = "maxTotalSize";
    public const string MaxSettings = "maxSettings";
    /// <summary>The listing reported a path that is not a plain relative path (traversal, control characters …).</summary>
    public const string InvalidPath = "invalidPath";
}

/// <summary>What to read of one GPO folder.</summary>
public sealed record GpoReadPlan
{
    /// <summary>Validated entries, sorted, unique (case-insensitive), at most <see cref="GpoReadLimits.MaxEntries"/>.</summary>
    public IReadOnlyList<GpoInventoryEntry> Inventory { get; init; } = [];

    /// <summary>Content files to read, in priority order, within the size limits.</summary>
    public IReadOnlyList<GpoFile> Read { get; init; } = [];

    /// <summary>Content files a limit kept from being read (they get the "could not be read" marker).</summary>
    public IReadOnlyList<GpoFile> NotRead { get; init; } = [];

    /// <summary>Script files above <see cref="GpoReadLimits.ScriptHashBytes"/>: size only.</summary>
    public IReadOnlyList<GpoInventoryEntry> LargeScripts { get; init; } = [];

    /// <summary><see cref="GpoReadProblems"/> values, each once.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public long BytesToRead { get; init; }
}

/// <summary>
/// Turns a SYSVOL listing into a read plan (stage B design 8.3/8.4): validates every path
/// (<see cref="GpoRelativePath"/>), drops entries deeper than 12 folders, keeps at most <c>MaxFiles</c> entries, chooses the
/// content files by role and reads them in priority order (GPT.INI, security template, audit, Registry.pol, scripts,
/// preferences, script files, comments) while each file is ≤ <c>MaxFileMb</c> and the total ≤ <c>MaxTotalMb</c>.
/// </summary>
public static class GpoReadPlanner
{
    public static GpoReadPlan Plan(SysvolListing listing, GpoReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(listing);
        limits ??= GpoReadLimits.Default;
        var problems = new List<string>();
        if (listing.RejectedEntries > 0)
        {
            problems.Add(GpoReadProblems.InvalidPath);
        }

        var inventory = Inventory(listing, limits, problems);
        var files = inventory.Where(e => !e.IsDirectory)
            .Select(e => (Entry: e, File: GpoFileClassifier.Classify(e.Path)))
            .Where(f => f.File.IsContent)
            .OrderBy(f => f.File.Priority)
            .ThenBy(f => f.Entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var read = new List<GpoFile>();
        var notRead = new List<GpoFile>();
        var largeScripts = new List<GpoInventoryEntry>();
        long total = 0;
        foreach (var (entry, file) in files)
        {
            if (file.Role == GpoFileRole.ScriptFile && entry.Size > GpoReadLimits.ScriptHashBytes)
            {
                largeScripts.Add(entry);
            }
            else if (entry.Size > limits.MaxFileBytes)
            {
                notRead.Add(file);
                Add(problems, GpoReadProblems.MaxFileSize);
            }
            else if (total + entry.Size > limits.MaxTotalBytes)
            {
                notRead.Add(file);
                Add(problems, GpoReadProblems.MaxTotalSize);
            }
            else
            {
                read.Add(file);
                total += entry.Size;
            }
        }

        return new GpoReadPlan
        {
            Inventory = inventory,
            Read = read,
            NotRead = notRead,
            LargeScripts = largeScripts,
            Problems = problems,
            BytesToRead = total,
        };
    }

    /// <summary>The validated, sorted inventory of a listing (also used by the daily reconciliation, 8.1).</summary>
    public static IReadOnlyList<GpoInventoryEntry> Inventory(SysvolListing listing, GpoReadLimits? limits = null) =>
        Inventory(listing, limits ?? GpoReadLimits.Default, []);

    private static List<GpoInventoryEntry> Inventory(SysvolListing listing, GpoReadLimits limits, List<string> problems)
    {
        var entries = new List<GpoInventoryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in listing.Entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal))
        {
            if (!GpoRelativePath.TryNormalize(entry.RelativePath, out var path) || entry.Size < 0)
            {
                Add(problems, GpoReadProblems.InvalidPath);
                continue;
            }

            if (GpoRelativePath.Depth(path) > limits.MaxDepth)
            {
                Add(problems, GpoReadProblems.MaxDepth);
                continue;
            }

            if (!seen.Add(path))
            {
                continue; // the same name in another case (case-sensitive server file system): the first one wins
            }

            if (entries.Count >= limits.MaxEntries)
            {
                Add(problems, GpoReadProblems.MaxEntries);
                break;
            }

            entries.Add(new GpoInventoryEntry
            {
                Path = path,
                IsDirectory = entry.IsDirectory,
                Size = entry.IsDirectory ? 0 : entry.Size,
                ModifiedAt = TruncateToSeconds(entry.ModifiedAt),
            });
        }

        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return entries;
    }

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static void Add(List<string> problems, string problem)
    {
        if (!problems.Contains(problem))
        {
            problems.Add(problem);
        }
    }
}

/// <summary>Daily reconciliation (stage B design 8.1): compares the stored inventory with a fresh listing.</summary>
public static class GpoInventoryComparer
{
    /// <summary>True when an entry was added or removed, a file's size changed or its modification time changed (second
    /// precision). Folder modification times are ignored – they follow their files.</summary>
    public static bool Differs(IReadOnlyList<GpoInventoryEntry> stored, IReadOnlyList<GpoInventoryEntry> current)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(current);
        if (stored.Count != current.Count)
        {
            return true;
        }

        var byPath = stored.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in current)
        {
            if (!byPath.TryGetValue(entry.Path, out var old) || old.IsDirectory != entry.IsDirectory)
            {
                return true;
            }

            if (!entry.IsDirectory && (old.Size != entry.Size || Seconds(old.ModifiedAt) != Seconds(entry.ModifiedAt)))
            {
                return true;
            }
        }

        return false;
    }

    private static long Seconds(DateTimeOffset value) => value.ToUnixTimeSeconds();
}
