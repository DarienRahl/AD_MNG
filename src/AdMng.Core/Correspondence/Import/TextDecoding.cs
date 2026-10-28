using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;

namespace AdMng.Core.Correspondence.Import;

/// <summary>Text of imported files: BOM, strict UTF-8, else the ANSI code page (registers <c>CodePagesEncodingProvider</c>).</summary>
public static partial class TextDecoding
{
    /// <summary>The code page used when the given ANSI code page is not usable (Polish Windows).</summary>
    public const int FallbackCodePage = 1250;

    private const int CharsetProbeBytes = 4096;

    static TextDecoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Decodes <paramref name="bytes"/>: BOM first (UTF-8, UTF-16 LE/BE, UTF-32 LE/BE), then strict UTF-8, else
    /// <paramref name="ansiCodePage"/> (Windows-1250 when that code page is not available or is UTF-8 itself).</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, int ansiCodePage)
    {
        if (DecodeWithBom(bytes) is { } withBom)
        {
            return withBom;
        }

        return Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Ansi(ansiCodePage).GetString(bytes);
    }

    /// <summary>
    /// Decodes an HTML file: BOM, else the <c>charset</c> of a <c>&lt;meta&gt;</c> in the first 4 KiB (Outlook writes
    /// <c>windows-1250</c> signatures on Polish Windows), else <see cref="Decode"/>.
    /// </summary>
    internal static string DecodeHtml(ReadOnlySpan<byte> bytes, int ansiCodePage)
    {
        if (DecodeWithBom(bytes) is { } withBom)
        {
            return withBom;
        }

        var probe = Encoding.ASCII.GetString(bytes[..Math.Min(bytes.Length, CharsetProbeBytes)]);
        var match = CharsetPattern().Match(probe);
        if (match.Success && EncodingNamed(match.Groups[1].Value) is { } declared)
        {
            return declared.GetString(bytes);
        }

        return Decode(bytes, ansiCodePage);
    }

    /// <summary>The ANSI encoding for <paramref name="codePage"/> (Windows-1250 when it is not usable).</summary>
    internal static Encoding Ansi(int codePage)
    {
        if (codePage > 0 && codePage != 65001 && codePage != 1200 && codePage != 1201 && codePage != 12000 && codePage != 12001)
        {
            try
            {
                return Encoding.GetEncoding(codePage);
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }

        return Encoding.GetEncoding(FallbackCodePage);
    }

    private static string? DecodeWithBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            return Encoding.UTF32.GetString(bytes[4..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetString(bytes[4..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        return null;
    }

    private static Encoding? EncodingNamed(string name)
    {
        try
        {
            var encoding = Encoding.GetEncoding(name.Trim());

            // A UTF-16/32 declaration in an ASCII-readable file is wrong (the probe could read it): decode by content.
            return encoding is UnicodeEncoding or UTF32Encoding ? null : encoding;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<meta[^>]*?charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CharsetPattern();
}
