using System.Globalization;
using System.Text;
using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.GroupPolicy;

/// <summary>
/// Reading and rewriting the <c>Version=</c> line of <c>GPT.INI</c> ([MS-GPOL] 2.2.4) for the GPO transaction (design note
/// docs/dev/k-computers-and-gpo.md 3.3/3.4). A rewrite changes nothing but that one line: every other line, the line
/// endings, the BOM and the encoding (ANSI, UTF-8 or UTF-16LE) are kept byte for byte. Pure, in memory.
/// </summary>
public static class GptIni
{
    /// <summary>Relative path of the file below the GPO folder.</summary>
    public const string RelativePath = "GPT.INI";

    /// <summary>What <c>samba-tool gpo create</c> (and the transaction for a new folder) writes: <c>[General]\r\nVersion=0\r\n</c>.</summary>
    public static byte[] Initial => "[General]\r\nVersion=0\r\n"u8.ToArray();

    /// <summary>The <c>Version</c> of <c>[General]</c>, or null when the file has none.</summary>
    /// <exception cref="GpoParseException">The file cannot be decoded or the version is not a number.</exception>
    public static int? ReadVersion(ReadOnlySpan<byte> content) => GptIniParser.Parse(content).Version;

    /// <summary>
    /// The file with <c>Version=<paramref name="version"/></c> in <c>[General]</c>: the existing line is replaced (its key
    /// spelling kept), a missing line is added right after the <c>[General]</c> header, a missing section is added at the
    /// start.
    /// </summary>
    public static byte[] WithVersion(ReadOnlySpan<byte> content, int version)
    {
        var value = version.ToString(CultureInfo.InvariantCulture);
        var (text, encoding, preamble) = Decode(content);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) || !text.Contains('\n') ? "\r\n" : "\n";
        var lines = SplitKeepingEndings(text);
        var section = string.Empty;
        var generalHeader = -1;
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var (body, ending) = lines[i];
            var trimmed = body.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                if (section.Equals("General", StringComparison.OrdinalIgnoreCase) && generalHeader < 0)
                {
                    generalHeader = i;
                }

                continue;
            }

            var equals = body.IndexOf('=', StringComparison.Ordinal);
            if (!replaced && equals > 0 && section.Equals("General", StringComparison.OrdinalIgnoreCase)
                && body[..equals].Trim().Equals("Version", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = (body[..(equals + 1)] + value, ending);
                replaced = true;
            }
        }

        if (!replaced)
        {
            if (generalHeader >= 0)
            {
                if (lines[generalHeader].Ending.Length == 0)
                {
                    lines[generalHeader] = (lines[generalHeader].Body, newLine);
                }

                lines.Insert(generalHeader + 1, ("Version=" + value, newLine));
            }
            else
            {
                lines.Insert(0, ("[General]", newLine));
                lines.Insert(1, ("Version=" + value, newLine));
            }
        }

        var result = new StringBuilder(text.Length + 16);
        foreach (var (body, ending) in lines)
        {
            result.Append(body).Append(ending);
        }

        return [.. preamble, .. encoding.GetBytes(result.ToString())];
    }

    /// <summary>
    /// The text and how to write it back: UTF-16LE and UTF-8 by their BOM, anything else byte for byte (Latin-1 maps every
    /// byte to one character and back, so ANSI text of any code page survives unchanged).
    /// </summary>
    private static (string Text, Encoding Encoding, byte[] Preamble) Decode(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return (Encoding.Unicode.GetString(content[2..]), Encoding.Unicode, [0xFF, 0xFE]);
        }

        if (content.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return (Encoding.Latin1.GetString(content[3..]), Encoding.Latin1, [0xEF, 0xBB, 0xBF]);
        }

        return (Encoding.Latin1.GetString(content), Encoding.Latin1, []);
    }

    private static List<(string Body, string Ending)> SplitKeepingEndings(string text)
    {
        var lines = new List<(string, string)>();
        var start = 0;
        while (start < text.Length)
        {
            var lf = text.IndexOf('\n', start);
            if (lf < 0)
            {
                lines.Add((text[start..], string.Empty));
                break;
            }

            var crlf = lf > start && text[lf - 1] == '\r';
            lines.Add((text[start..(crlf ? lf - 1 : lf)], crlf ? "\r\n" : "\n"));
            start = lf + 1;
        }

        return lines;
    }
}
