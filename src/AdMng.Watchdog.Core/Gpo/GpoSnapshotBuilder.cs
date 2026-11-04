using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Watchdog.Core.Gpo;

/// <summary>Input of <see cref="GpoSnapshotBuilder.Build"/>.</summary>
public sealed record GpoSnapshotInput
{
    public required Guid GpoGuid { get; init; }
    public required string Dc { get; init; }
    public int? GpcVersion { get; init; }
    public required DateTimeOffset ReadAt { get; init; }
    public required GpoReadPlan Plan { get; init; }

    /// <summary>The files returned by <see cref="Abstractions.ISysvolReader.ReadAsync"/> for <see cref="GpoReadPlan.Read"/>.</summary>
    public IReadOnlyList<SysvolFile> Files { get; init; } = [];

    /// <summary>The stored snapshot: settings of files that cannot be read now are carried over from it.</summary>
    public GpoContentSnapshot? Previous { get; init; }

    public GpoReadLimits Limits { get; init; } = GpoReadLimits.Default;
}

/// <summary>Result of one read: the snapshot to store and what went wrong (health <c>gpo.&lt;guid&gt;.*</c>).</summary>
public sealed record GpoReadResult
{
    public required GpoContentSnapshot Snapshot { get; init; }

    /// <summary>Content files that could not be read or parsed (<c>gpo.&lt;guid&gt;.parseError</c>).</summary>
    public IReadOnlyList<string> UnreadableFiles { get; init; } = [];

    /// <summary>Content files a limit kept from being read (<c>gpo.&lt;guid&gt;.tooLarge</c>).</summary>
    public IReadOnlyList<string> NotReadFiles { get; init; } = [];

    /// <summary><see cref="GpoReadProblems"/> values (limits and invalid listing paths).</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public bool TooLarge => Problems.Any(p => p != GpoReadProblems.InvalidPath);

    public bool HasParseErrors => UnreadableFiles.Count > 0 || Problems.Contains(GpoReadProblems.InvalidPath);
}

/// <summary>
/// Parses the files of a read plan into a <see cref="GpoContentSnapshot"/> (stage B design 8.4/8.5). Every parser fails
/// closed per file: a file that cannot be read or parsed, exceeds a limit or would exceed the 10 000 settings contributes
/// no settings of its own but a marker <c>File/&lt;path&gt;</c> (<see cref="GpoSettingKind.Unreadable"/>), and its previous
/// settings are carried over – a transient failure is neither "all removed" nor, later, "all added". Duplicate keys: the
/// later setting wins (files in plan order, entries in file order). Pure; nothing touches the disk.
/// </summary>
public static class GpoSnapshotBuilder
{
    /// <summary>Prefix of the unreadable-file markers.</summary>
    public const string FileMarkerPrefix = "File/";

    public static GpoReadResult Build(GpoSnapshotInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var plan = input.Plan;
        var limits = input.Limits;
        var contents = input.Files
            .GroupBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var settings = new Dictionary<string, GpoSetting>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<GpoFile>();
        var notRead = new List<GpoFile>(plan.NotRead);
        var problems = new List<string>(plan.Problems);
        int? gptVersion = null;
        var budget = limits.MaxSettings;

        foreach (var file in plan.Read)
        {
            if (!contents.TryGetValue(file.Path, out var read) || read.Content is null)
            {
                if (read?.Error == SysvolErrorKind.TooLarge)
                {
                    notRead.Add(file);
                    AddProblem(problems, GpoReadProblems.MaxFileSize);
                }
                else
                {
                    unreadable.Add(file);
                }

                continue;
            }

            if (read.Content.LongLength > limits.MaxFileBytes)
            {
                notRead.Add(file);
                AddProblem(problems, GpoReadProblems.MaxFileSize);
                continue;
            }

            var sink = new GpoSettingSink(file.Path, Math.Max(0, budget));
            try
            {
                if (file.Role == GpoFileRole.GptIni)
                {
                    gptVersion = GptIniParser.Parse(read.Content).Version;
                }
                else
                {
                    GpoContentParser.Parse(file, read.Content, sink);
                }
            }
            catch (GpoParseException)
            {
                unreadable.Add(file);
                continue;
            }

            if (sink.Overflowed)
            {
                notRead.Add(file);
                AddProblem(problems, GpoReadProblems.MaxSettings);
                continue;
            }

            foreach (var setting in sink.Settings)
            {
                settings[setting.Key] = setting;
            }

            budget -= sink.Settings.Count;
        }

        foreach (var script in plan.LargeScripts)
        {
            var file = GpoFileClassifier.Classify(script.Path);
            settings[ScriptKey(file)] = new GpoSetting
            {
                Key = ScriptKey(file),
                Value = "size:" + GpoValues.Number(script.Size),
                Category = GpoSettingCategory.Scripts,
                Source = script.Path,
            };
        }

        var failed = unreadable.Concat(notRead).ToList();
        foreach (var file in failed)
        {
            settings[FileMarkerPrefix + file.Path] = new GpoSetting
            {
                Key = FileMarkerPrefix + file.Path,
                Kind = GpoSettingKind.Unreadable,
                Category = file.Category,
                Source = file.Path,
            };
        }

        if (input.Previous is { } previous && failed.Count > 0)
        {
            var failedPaths = failed.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var setting in previous.Settings)
            {
                if (setting.Source is not null && failedPaths.Contains(setting.Source) && setting.Kind != GpoSettingKind.Unreadable)
                {
                    settings.TryAdd(setting.Key, setting);
                }
            }
        }

        var snapshot = new GpoContentSnapshot
        {
            GpoGuid = input.GpoGuid,
            Dc = input.Dc,
            GpcVersion = input.GpcVersion,
            GptVersion = gptVersion,
            ReadAt = input.ReadAt,
            State = failed.Count > 0 || problems.Count > 0 ? GpoContentState.Partial : GpoContentState.Complete,
            Inventory = plan.Inventory,
            Settings = settings.Values.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Key, StringComparer.Ordinal).ToList(),
        };
        return new GpoReadResult
        {
            Snapshot = snapshot,
            UnreadableFiles = unreadable.Select(f => f.Path).ToList(),
            NotReadFiles = notRead.Select(f => f.Path).ToList(),
            Problems = problems,
        };
    }

    /// <summary>Key of a script file: <c>Computer/Scripts/Files/&lt;path below Scripts/&gt;</c>.</summary>
    public static string ScriptKey(GpoFile file) => GpoContentParser.ScriptKey(file);

    private static void AddProblem(List<string> problems, string problem)
    {
        if (!problems.Contains(problem))
        {
            problems.Add(problem);
        }
    }
}
