using System.Xml;

namespace AdMng.Core.Correspondence.Import;

/// <summary>
/// Reads contacts from the bytes of one file (design note 3.1): CSV/TSV/TXT, XLSX/XLSM/XLTX, DOCX/DOTX/DOCM, ODT/ODS, RTF
/// and HTML. Pure: the caller reads the file. Files above <see cref="MaxFileBytes"/>, zip bombs and packages whose cells
/// and paragraphs expand to more than <see cref="MaxFileBytes"/> characters (a shared string or a repeated cell counted
/// each time it appears) are refused (<see cref="InvalidDataException"/>); XML is read without DTDs or external resolution.
/// </summary>
/// <remarks>
/// Text files go through <see cref="ContactExtractor.FromText"/>. Workbooks (XLSX, ODS): every sheet with an e-mail column
/// is a table of its own, their contacts concatenated; when no sheet has one, all cells are free text. Sheets and rows
/// Excel hides are left out of an XLSX (<see cref="ContactImportResult.HiddenSheets"/>,
/// <see cref="ContactImportResult.HiddenRows"/>): the operator never saw them. Documents (DOCX,
/// ODT, HTML): text outside tables through <see cref="ContactExtractor.FromText"/>, every table through
/// <see cref="ContactExtractor.FromRows"/> (HTML: the tables first, then the remaining text). A file with any other
/// extension is read as text. Exception messages never contain the file's content.
/// </remarks>
public static class ContactFileReader
{
    /// <summary>The largest file read (50 MiB).</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>The supported extensions (lower case, with the dot).</summary>
    public static IReadOnlyList<string> Extensions { get; } =
        [".csv", ".tsv", ".txt", ".xlsx", ".xlsm", ".xltx", ".docx", ".dotx", ".docm", ".odt", ".ods", ".rtf", ".htm", ".html"];

    /// <summary>True when the extension of <paramref name="fileName"/> is one of <see cref="Extensions"/>.</summary>
    public static bool IsSupported(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && Extensions.Contains(Extension(fileName), StringComparer.Ordinal);

    /// <summary>Reads one file. Text files: BOM / strict UTF-8 / the ANSI code page (Windows-1250 on Polish Windows).</summary>
    /// <exception cref="InvalidDataException">The file is too large, a zip bomb or not readable as its format.</exception>
    public static ContactImportResult Read(string fileName, ReadOnlySpan<byte> content, int ansiCodePage)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (content.Length > MaxFileBytes)
        {
            throw new InvalidDataException("The file is larger than 50 MiB.");
        }

        var extension = Extension(fileName);
        try
        {
            switch (extension)
            {
                case ".xlsx" or ".xlsm" or ".xltx":
                    using (var zip = SafeZip.Open(content))
                    {
                        var workbook = XlsxReader.ReadSheets(zip);
                        var sheets = workbook.Sheets;
                        return DocumentParts.Sheets(sheets, () => string.Concat(sheets.Select(ContactExtractor.RowsText)), ContactSourceFormat.Excel)
                            with { HiddenSheets = workbook.HiddenSheets, HiddenRows = workbook.HiddenRows };
                    }

                case ".ods":
                    using (var zip = SafeZip.Open(content))
                    {
                        return OpenDocumentReader.Read(zip).SheetContacts(ContactSourceFormat.OpenDocument);
                    }

                case ".odt":
                    using (var zip = SafeZip.Open(content))
                    {
                        return OpenDocumentReader.Read(zip).Contacts(ContactSourceFormat.OpenDocument);
                    }

                case ".docx" or ".dotx" or ".docm":
                    using (var zip = SafeZip.Open(content))
                    {
                        return WordReader.Read(zip).Contacts(ContactSourceFormat.Word);
                    }

                case ".rtf":
                    return ContactExtractor.FromText(RtfReader.Read(content, ansiCodePage)) with { Format = ContactSourceFormat.Rtf };
                case ".htm" or ".html":
                    return HtmlContactReader.Read(TextDecoding.DecodeHtml(content, ansiCodePage));
                default:
                    return ContactExtractor.FromText(TextDecoding.Decode(content, ansiCodePage));
            }
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("The file is not well-formed XML or uses a prohibited feature (DTD).", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidDataException("The package uses a feature that is not supported (compression method).", ex);
        }
    }

    /// <summary>
    /// Every valid address of one file, normalized, de-duplicated, in file order – in any column, cell (a second address of a
    /// cell too), row, sheet (hidden ones included), table or paragraph, not only the e-mail column of a table: the "Nie
    /// wysyłać" list must hold every address of the file, an address left out would get the letter.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is too large, a zip bomb or not readable as its format.</exception>
    public static IReadOnlyList<string> ReadAddresses(string fileName, ReadOnlySpan<byte> content, int ansiCodePage)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (content.Length > MaxFileBytes)
        {
            throw new InvalidDataException("The file is larger than 50 MiB.");
        }

        string text;
        try
        {
            switch (Extension(fileName))
            {
                case ".xlsx" or ".xlsm" or ".xltx":
                    using (var zip = SafeZip.Open(content))
                    {
                        text = string.Concat(XlsxReader.ReadSheets(zip, includeHidden: true).Sheets.Select(ContactExtractor.RowsText));
                    }

                    break;
                case ".ods" or ".odt":
                    using (var zip = SafeZip.Open(content))
                    {
                        text = OpenDocumentReader.Read(zip).AllText();
                    }

                    break;
                case ".docx" or ".dotx" or ".docm":
                    using (var zip = SafeZip.Open(content))
                    {
                        text = WordReader.Read(zip).AllText();
                    }

                    break;
                case ".rtf":
                    text = RtfReader.Read(content, ansiCodePage);
                    break;
                case ".htm" or ".html":
                    text = HtmlContactReader.Text(TextDecoding.DecodeHtml(content, ansiCodePage));
                    break;
                default:
                    text = TextDecoding.Decode(content, ansiCodePage);
                    break;
            }
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("The file is not well-formed XML or uses a prohibited feature (DTD).", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidDataException("The package uses a feature that is not supported (compression method).", ex);
        }

        return EmailAddressText.Extract(text);
    }

    private static string Extension(string fileName)
    {
        var name = fileName.Trim();
        var cut = name.LastIndexOfAny(['/', '\\']);
        if (cut >= 0)
        {
            name = name[(cut + 1)..];
        }

        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[dot..].ToLowerInvariant() : string.Empty;
    }
}
