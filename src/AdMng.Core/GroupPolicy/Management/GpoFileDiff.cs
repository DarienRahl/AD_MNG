using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.GroupPolicy.Management;

/// <summary>How a file of a GPO is compared.</summary>
public enum GpoFileDiffKind
{
    /// <summary>Settings files (security template, audit.csv, Registry.pol, preferences, scripts.ini, comments): the
    /// normalized settings of the shared parsers, one line per setting - secrets masked, never shown.</summary>
    Settings,

    /// <summary>Other text files: their lines.</summary>
    Text,

    /// <summary>Binary or undecodable content: sizes only.</summary>
    Binary,
}

/// <summary>One line of a diff.</summary>
public enum GpoDiffLineKind
{
    Unchanged,
    Removed,
    Added,
}

/// <summary>One line of <see cref="GpoFileDiffResult"/>.</summary>
public sealed record GpoDiffLine(GpoDiffLineKind Kind, string Text);

/// <summary>The comparison of the old and new content of one GPO file (in memory, for the preview's "Pokaż różnice").</summary>
public sealed record GpoFileDiffResult(GpoFileDiffKind Kind, IReadOnlyList<GpoDiffLine> Lines)
{
    /// <summary>Lines were left out (more than <see cref="GpoFileDiff.MaxLines"/> on a side).</summary>
    public bool Truncated { get; init; }

    public int Added => Lines.Count(l => l.Kind == GpoDiffLineKind.Added);

    public int Removed => Lines.Count(l => l.Kind == GpoDiffLineKind.Removed);
}

/// <summary>
/// Diff of two versions of a GPO file (design note docs/dev/k-computers-and-gpo.md 3.3 preview). Settings files go through
/// the parsers of <see cref="GpoContentParser"/>, so secrets (GPP <c>cpassword</c>, password-like registry values) are
/// masked; other text files are decoded strictly (<see cref="GpoText"/>) and compared by lines; anything else is binary.
/// Pure and in memory.
/// </summary>
public static class GpoFileDiff
{
    /// <summary>Most lines per side that are compared line by line (a longer file shows only its first lines changed).</summary>
    public const int MaxLines = 2000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ini", ".inf", ".xml", ".csv", ".txt", ".cmd", ".bat", ".ps1", ".vbs", ".js", ".adm", ".admx", ".adml", ".cmtx", ".cmt",
    };

    public static GpoFileDiffResult Compare(string relativePath, byte[]? oldContent, byte[]? newContent)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var file = GpoFileClassifier.Classify(relativePath);
        if (file.IsContent && file.Role != GpoFileRole.ScriptFile && file.Role != GpoFileRole.GptIni)
        {
            var before = Settings(file, oldContent);
            var after = Settings(file, newContent);
            if (before is not null && after is not null)
            {
                return Build(GpoFileDiffKind.Settings, before, after);
            }
        }

        var extension = Path.GetExtension(relativePath);
        if (TextExtensions.Contains(extension) || file.Role is GpoFileRole.ScriptFile or GpoFileRole.GptIni)
        {
            var before = Text(oldContent);
            var after = Text(newContent);
            if (before is not null && after is not null)
            {
                return Build(GpoFileDiffKind.Text, before, after);
            }
        }

        return new GpoFileDiffResult(GpoFileDiffKind.Binary, []);
    }

    /// <summary>The settings of a file as lines <c>key = value</c> (masked values as ***), null when it cannot be parsed.</summary>
    public static IReadOnlyList<string>? Settings(GpoFile file, byte[]? content)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (content is null)
        {
            return [];
        }

        var sink = new GpoSettingSink(file.Path);
        try
        {
            GpoContentParser.Parse(file, content, sink);
        }
        catch (GpoParseException)
        {
            return null;
        }

        return [.. sink.Settings.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(Line)];
    }

    /// <summary>One setting as a line; a masked setting never shows its value.</summary>
    public static string Line(GpoSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var value = setting.Kind switch
        {
            GpoSettingKind.Masked => "***",
            _ when setting.IsList => string.Join(", ", setting.Elements()),
            _ => setting.Value,
        };
        return setting.Key + " = " + value;
    }

    private static IReadOnlyList<string>? Text(byte[]? content)
    {
        if (content is null)
        {
            return [];
        }

        try
        {
            return [.. GpoText.Lines(GpoText.DecodeAnsiOrUtf8(content, allowAnsi: true))];
        }
        catch (GpoParseException)
        {
            return null;
        }
    }

    private static GpoFileDiffResult Build(GpoFileDiffKind kind, IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var truncated = before.Count > MaxLines || after.Count > MaxLines;
        var a = before.Take(MaxLines).ToArray();
        var b = after.Take(MaxLines).ToArray();
        return new GpoFileDiffResult(kind, Lcs(a, b)) { Truncated = truncated };
    }

    /// <summary>A line diff by the longest common subsequence (common prefix and suffix trimmed first).</summary>
    private static List<GpoDiffLine> Lcs(string[] a, string[] b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
        {
            suffix++;
        }

        var x = a[prefix..(a.Length - suffix)];
        var y = b[prefix..(b.Length - suffix)];
        var table = new int[x.Length + 1, y.Length + 1];
        for (var i = x.Length - 1; i >= 0; i--)
        {
            for (var j = y.Length - 1; j >= 0; j--)
            {
                table[i, j] = x[i] == y[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var lines = new List<GpoDiffLine>(a.Length + b.Length);
        lines.AddRange(a[..prefix].Select(l => new GpoDiffLine(GpoDiffLineKind.Unchanged, l)));
        int p = 0, q = 0;
        while (p < x.Length && q < y.Length)
        {
            if (x[p] == y[q])
            {
                lines.Add(new GpoDiffLine(GpoDiffLineKind.Unchanged, x[p]));
                p++;
                q++;
            }
            else if (table[p + 1, q] >= table[p, q + 1])
            {
                lines.Add(new GpoDiffLine(GpoDiffLineKind.Removed, x[p++]));
            }
            else
            {
                lines.Add(new GpoDiffLine(GpoDiffLineKind.Added, y[q++]));
            }
        }

        lines.AddRange(x[p..].Select(l => new GpoDiffLine(GpoDiffLineKind.Removed, l)));
        lines.AddRange(y[q..].Select(l => new GpoDiffLine(GpoDiffLineKind.Added, l)));
        lines.AddRange(a[(a.Length - suffix)..].Select(l => new GpoDiffLine(GpoDiffLineKind.Unchanged, l)));
        return lines;
    }
}
