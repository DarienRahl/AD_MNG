using System.Globalization;
using System.Text;

namespace AdMng.Presentation.Infrastructure;

/// <summary>
/// File helpers for operator-initiated CSV exports and imports (the only directory data that ever reaches the disk,
/// and only because the operator explicitly chose a file - CLAUDE.md rule 1).
/// </summary>
public static class CsvFiles
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static CsvFiles()
    {
        // ANSI code pages (Windows-1250 for Polish Excel) are not available on .NET without the provider.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Delimiter Excel uses for the culture (';' for Polish, ',' for English) - never a space. Pass the regional
    /// culture (<see cref="Services.IRegionalSettings.Culture"/>), not the UI language.
    /// </summary>
    public static char ListSeparator(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var separator = culture.TextInfo.ListSeparator;
        return string.IsNullOrEmpty(separator) || separator[0] is ' ' ? ';' : separator[0];
    }

    /// <summary>Writes a file on a background thread as UTF-8 with BOM (Excel then detects the encoding).</summary>
    public static Task WriteAsync(string path, Action<TextWriter> write, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        return Task.Run(
            () =>
            {
                using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                write(writer);
            },
            cancellationToken);
    }

    /// <summary>
    /// Reads a text file chosen by the operator. UTF-8 (with or without BOM) and UTF-16 with BOM are detected;
    /// anything else is decoded with <paramref name="ansiCodePage"/> - the system's ANSI code page
    /// (<see cref="Services.IRegionalSettings.AnsiCodePage"/>, Windows-1250 on Polish Windows), which is what Excel
    /// writes for "CSV (comma delimited)" whatever the UI language of this app.
    /// </summary>
    public static async Task<string> ReadTextAsync(string path, int ansiCodePage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Decode(bytes, ansiCodePage);
    }

    /// <summary>Decodes file content (see <see cref="ReadTextAsync"/>).</summary>
    public static string Decode(byte[] bytes, int ansiCodePage)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes is [0xFF, 0xFE, ..])
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            var codePage = ansiCodePage is > 0 and not 65001 ? ansiCodePage : 1252;
            return Encoding.GetEncoding(codePage).GetString(bytes);
        }
    }
}
