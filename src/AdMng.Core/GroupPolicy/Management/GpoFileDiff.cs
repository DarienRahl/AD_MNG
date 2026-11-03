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
public sealed record GpoDiffLine(GpoDiffLineKind Kind, string Text)
{
    /// <summary>The setting of a <see cref="GpoFileDiffKind.Settings"/> line (the old one of a removed line, the new one
    /// otherwise; a masked setting carries no value); null for text lines.</summary>
    public GpoSetting? Setting { get; init; }
}

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
            var before = ReadSettings(file, oldContent);
            var after = ReadSettings(file, newContent);
            if (before is not null && after is not null)
            {
                return Build(GpoFileDiffKind.Settings, [.. before.Select(s => new Item(Line(s), s))], [.. after.Select(s => new Item(Line(s), s))]);
            }
        }

        // A preferences file the parser could not read, or any file naming a GPP password, is never shown as raw text: masked
        // values are never displayed.
        if (file.Role == GpoFileRole.Preferences || MayHoldGppPassword(oldContent) || MayHoldGppPassword(newContent))
        {
            return new GpoFileDiffResult(GpoFileDiffKind.Binary, []);
        }

        var extension = Path.GetExtension(relativePath);
        if (TextExtensions.Contains(extension) || file.Role is GpoFileRole.ScriptFile or GpoFileRole.GptIni)
        {
            var before = Text(oldContent);
            var after = Text(newContent);
            if (before is not null && after is not null)
            {
                return Build(GpoFileDiffKind.Text, [.. before.Select(l => new Item(l, null))], [.. after.Select(l => new Item(l, null))]);
            }
        }

        return new GpoFileDiffResult(GpoFileDiffKind.Binary, []);
    }

    /// <summary>The settings of a file as lines <c>key = value</c> (masked values as ***), null when it cannot be parsed.</summary>
    public static IReadOnlyList<string>? Settings(GpoFile file, byte[]? content) => ReadSettings(file, content)?.Select(Line).ToList();

    /// <summary>The settings of a file (missing file: none) sorted by key, null when it cannot be parsed.</summary>
    public static IReadOnlyList<GpoSetting>? ReadSettings(GpoFile file, byte[]? content)
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

        return [.. sink.Settings.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)];
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

    /// <summary>The bytes name <c>cpassword</c> in an 8-bit or UTF-16 encoding (case-insensitive).</summary>
    private static bool MayHoldGppPassword(byte[]? content) =>
        content is not null
        && (System.Text.Encoding.Latin1.GetString(content).Contains("cpassword", StringComparison.OrdinalIgnoreCase)
            || System.Text.Encoding.Unicode.GetString(content).Contains("cpassword", StringComparison.OrdinalIgnoreCase)
            || System.Text.Encoding.BigEndianUnicode.GetString(content).Contains("cpassword", StringComparison.OrdinalIgnoreCase));

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

    /// <summary>A line and the setting it shows (null for text lines).</summary>
    private readonly record struct Item(string Text, GpoSetting? Setting);

    private static GpoFileDiffResult Build(GpoFileDiffKind kind, IReadOnlyList<Item> before, IReadOnlyList<Item> after)
    {
        var truncated = before.Count > MaxLines || after.Count > MaxLines;
        var a = before.Take(MaxLines).ToArray();
        var b = after.Take(MaxLines).ToArray();
        return new GpoFileDiffResult(kind, Lcs(a, b)) { Truncated = truncated };
    }

    /// <summary>A line diff by the longest common subsequence (common prefix and suffix trimmed first); unchanged lines carry
    /// the new side's setting.</summary>
    private static List<GpoDiffLine> Lcs(Item[] a, Item[] b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix].Text == b[prefix].Text)
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)].Text == b[^(suffix + 1)].Text)
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
                table[i, j] = x[i].Text == y[j].Text ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var lines = new List<GpoDiffLine>(a.Length + b.Length);
        lines.AddRange(b[..prefix].Select(l => DiffLine(GpoDiffLineKind.Unchanged, l)));
        int p = 0, q = 0;
        while (p < x.Length && q < y.Length)
        {
            if (x[p].Text == y[q].Text)
            {
                lines.Add(DiffLine(GpoDiffLineKind.Unchanged, y[q]));
                p++;
                q++;
            }
            else if (table[p + 1, q] >= table[p, q + 1])
            {
                lines.Add(DiffLine(GpoDiffLineKind.Removed, x[p++]));
            }
            else
            {
                lines.Add(DiffLine(GpoDiffLineKind.Added, y[q++]));
            }
        }

        lines.AddRange(x[p..].Select(l => DiffLine(GpoDiffLineKind.Removed, l)));
        lines.AddRange(y[q..].Select(l => DiffLine(GpoDiffLineKind.Added, l)));
        lines.AddRange(b[(b.Length - suffix)..].Select(l => DiffLine(GpoDiffLineKind.Unchanged, l)));
        return lines;
    }

    private static GpoDiffLine DiffLine(GpoDiffLineKind kind, Item item) => new(kind, item.Text) { Setting = item.Setting };
}
