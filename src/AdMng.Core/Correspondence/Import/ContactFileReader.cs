namespace AdMng.Core.Correspondence.Import;

/// <summary>
/// Reads contacts from the bytes of one file (design note 3.1): CSV/TSV/TXT, XLSX/XLSM/XLTX, DOCX/DOTX/DOCM, ODT/ODS, RTF
/// and HTML. Pure: the caller reads the file. Files above <see cref="MaxFileBytes"/> and zip bombs are refused
/// (<see cref="InvalidDataException"/>); XML is read without DTDs or external resolution.
/// </summary>
public static class ContactFileReader
{
    private const string Track = "U5 T1 core";

    /// <summary>The largest file read (50 MiB).</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>The supported extensions (lower case, with the dot).</summary>
    public static IReadOnlyList<string> Extensions { get; } =
        [".csv", ".tsv", ".txt", ".xlsx", ".xlsm", ".xltx", ".docx", ".dotx", ".docm", ".odt", ".ods", ".rtf", ".htm", ".html"];

    /// <summary>True when the extension of <paramref name="fileName"/> is one of <see cref="Extensions"/>.</summary>
    public static bool IsSupported(string fileName) => throw new NotImplementedException(Track);

    /// <summary>Reads one file. Text files: BOM / strict UTF-8 / the ANSI code page (Windows-1250 on Polish Windows).</summary>
    /// <exception cref="InvalidDataException">The file is too large, a zip bomb or not readable as its format.</exception>
    public static ContactImportResult Read(string fileName, ReadOnlySpan<byte> content, int ansiCodePage) =>
        throw new NotImplementedException(Track);
}
