using System.Text;

namespace AdMng.Core.GroupPolicy;

/// <summary>
/// A GPO file could not be parsed; the file is shown as unreadable (fail closed per file - Watchdog stage B design 8.4, the
/// privileged access review U4c).
/// </summary>
public sealed class GpoParseException : Exception
{
    public GpoParseException()
    {
    }

    public GpoParseException(string message)
        : base(message)
    {
    }

    public GpoParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Decoding of the text files of a GPO folder, shared by AdMng Watchdog (GPO content) and the privileged access review
/// (U4c, local rights from GPOs). Content is untrusted: a decoding error, a NUL character or an unsupported
/// encoding fails the file (<see cref="GpoParseException"/>); nothing is guessed silently except the documented ANSI
/// fallback of <c>GPT.INI</c>.
/// </summary>
public static class GpoText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding StrictUtf16 = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Files the Windows tools write as UTF-16LE with BOM (<c>GptTmpl.inf</c> – [MS-GPSB] 2.2, <c>scripts.ini</c> /
    /// <c>psscripts.ini</c> – [MS-GPSCR] 2.2.2, <c>GPO.cmt</c>). Accepted: UTF-16LE with BOM; UTF-16LE without BOM (the
    /// second byte of an ASCII first character is 0); UTF-8 with or without BOM (strict). Refused: UTF-16BE, UTF-32,
    /// invalid sequences, an odd UTF-16 length, NUL characters.
    /// </summary>
    public static string DecodeUnicodeText(ReadOnlySpan<byte> bytes) => Decode(bytes, allowAnsi: false);

    /// <summary>
    /// ANSI/UTF-8 files (<c>GPT.INI</c> – [MS-GPOL] 2.2.4, <c>audit.csv</c> – [MS-GPAC] 2.2.1.2): UTF-8 (BOM optional,
    /// strict), UTF-16LE with BOM; with <paramref name="allowAnsi"/> bytes that are not UTF-8 are read as Latin-1 (the
    /// ANSI code page is unknown; only the version digits of GPT.INI matter).
    /// </summary>
    public static string DecodeAnsiOrUtf8(ReadOnlySpan<byte> bytes, bool allowAnsi) => Decode(bytes, allowAnsi);

    private static string Decode(ReadOnlySpan<byte> bytes, bool allowAnsi)
    {
        string text;
        try
        {
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            {
                throw new GpoParseException("UTF-32 is not supported.");
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                throw new GpoParseException("UTF-16BE is not supported.");
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                text = DecodeUtf16(bytes[2..]);
            }
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                text = StrictUtf8.GetString(bytes[3..]);
            }
            else if (bytes.Length >= 2 && bytes[0] != 0 && bytes[1] == 0)
            {
                text = DecodeUtf16(bytes);
            }
            else
            {
                try
                {
                    text = StrictUtf8.GetString(bytes);
                }
                catch (DecoderFallbackException) when (allowAnsi)
                {
                    text = Encoding.Latin1.GetString(bytes);
                }
            }
        }
        catch (DecoderFallbackException e)
        {
            throw new GpoParseException("The file is not valid text in the expected encoding.", e);
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new GpoParseException("The file contains NUL characters (binary or wrong encoding).");
        }

        return text;
    }

    private static string DecodeUtf16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % 2 != 0)
        {
            throw new GpoParseException("UTF-16 content with an odd number of bytes.");
        }

        return StrictUtf16.GetString(bytes);
    }

    /// <summary>The lines of a text (CR LF, LF or CR).</summary>
    public static IEnumerable<string> Lines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}

/// <summary>One line of an INI file below a section.</summary>
public readonly record struct IniLine(string Section, string Key, string? Value, string Raw);

/// <summary>
/// Reader of the INI dialect of GPO files: <c>[Section]</c> headers, <c>key = value</c> or <c>key=value</c> lines (split at
/// the first '='), <c>;</c> comments, blank lines ignored; lines before the first section are ignored. A line without
/// '=' yields <see cref="IniLine.Value"/> null (the raw lines of <c>[Service General Setting]</c> etc.).
/// </summary>
public static class IniReader
{
    public static IEnumerable<IniLine> Read(string text)
    {
        string? section = null;
        foreach (var rawLine in GpoText.Lines(text))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            if (line[0] == '[')
            {
                var close = line.IndexOf(']', StringComparison.Ordinal);
                section = close > 1 ? line[1..close].Trim() : null;
                continue;
            }

            if (section is null)
            {
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            yield return equals < 0
                ? new IniLine(section, line, null, line)
                : new IniLine(section, line[..equals].Trim(), line[(equals + 1)..].Trim(), line);
        }
    }

    /// <summary>Splits a comma-separated INI value honouring double quotes (quotes removed, <c>""</c> inside quotes = <c>"</c>).</summary>
    public static IReadOnlyList<string> SplitCommaList(string value)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"')
            {
                if (quoted && i + 1 < value.Length && value[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == ',' && !quoted)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    /// <summary>A value without one pair of surrounding double quotes.</summary>
    public static string Unquote(string value)
    {
        var text = value.Trim();
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
    }
}
