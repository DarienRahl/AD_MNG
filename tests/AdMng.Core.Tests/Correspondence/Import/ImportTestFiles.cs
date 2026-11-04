using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AdMng.Core.Tests.Correspondence.Import;

/// <summary>Files for the import tests, built in memory (zip packages of XLSX, DOCX, ODT and ODS; RTF; HTML) – never
/// read from disk and never real data.</summary>
internal static class ImportTestFiles
{
    public const string SpreadsheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public const string RelationshipsNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static readonly Encoding Cp1250 = CodePagesEncodingProvider.Instance.GetEncoding(1250)!;

    /// <summary>A zip package of text entries (UTF-8).</summary>
    public static byte[] Zip(params (string Name, string Content)[] entries) =>
        ZipBytes([.. entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Content)))]);

    public static byte[] ZipBytes(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var writer = entry.Open();
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    /// <summary>A workbook: <paramref name="sheets"/> are (name, sheetData inner XML) in workbook order, written as
    /// <c>xl/worksheets/sheet{n}.xml</c>; <paramref name="sharedStrings"/> is the inner XML of <c>sst</c> (null = no part).</summary>
    public static byte[] Xlsx(string? sharedStrings, params (string Name, string SheetData)[] sheets)
    {
        var entries = new List<(string, string)>
        {
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
        };
        var workbook = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"{SpreadsheetNs}\" xmlns:r=\"{RelationshipsNs}\"><sheets>");
        var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var i = 0; i < sheets.Length; i++)
        {
            var n = i + 1;
            workbook.Append($"<sheet name=\"{sheets[i].Name}\" sheetId=\"{n}\" r:id=\"rId{n}\"/>");
            rels.Append($"<Relationship Id=\"rId{n}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{n}.xml\"/>");
            entries.Add(($"xl/worksheets/sheet{n}.xml", Sheet(sheets[i].SheetData)));
        }

        if (sharedStrings is not null)
        {
            rels.Append("<Relationship Id=\"rIdS\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>");
            entries.Add(("xl/sharedStrings.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><sst xmlns=\"{SpreadsheetNs}\">{sharedStrings}</sst>"));
        }

        entries.Add(("xl/workbook.xml", workbook.Append("</sheets></workbook>").ToString()));
        entries.Add(("xl/_rels/workbook.xml.rels", rels.Append("</Relationships>").ToString()));
        return Zip([.. entries]);
    }

    public static string Sheet(string sheetData) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"{SpreadsheetNs}\"><sheetData>{sheetData}</sheetData></worksheet>";

    /// <summary>A row of inline-string cells at the given references ("A1", "C1", …).</summary>
    public static string InlineRow(int row, params (string Reference, string Text)[] cells) =>
        $"<row r=\"{row}\">" + string.Concat(cells.Select(c => $"<c r=\"{c.Reference}\" t=\"inlineStr\"><is><t>{Escape(c.Text)}</t></is></c>")) + "</row>";

    /// <summary>A document: <paramref name="body"/> is the inner XML of <c>w:body</c> (prefix <c>w</c>, <c>r</c> and
    /// <c>mc</c> declared); <paramref name="relationships"/> the inner XML of the document's relationships.</summary>
    public static byte[] Docx(string body, string? relationships = null)
    {
        var entries = new List<(string, string)>
        {
            ("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{WordNs}\" xmlns:r=\"{RelationshipsNs}\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><w:body>{body}</w:body></w:document>"),
        };
        if (relationships is not null)
        {
            entries.Add(("word/_rels/document.xml.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{relationships}</Relationships>"));
        }

        return Zip([.. entries]);
    }

    /// <summary>A paragraph with one run of text.</summary>
    public static string P(string text) => $"<w:p><w:r><w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r></w:p>";

    /// <summary>A table cell with one paragraph (optionally its properties' inner XML).</summary>
    public static string Tc(string text, string? properties = null) =>
        "<w:tc>" + (properties is null ? string.Empty : $"<w:tcPr>{properties}</w:tcPr>") + P(text) + "</w:tc>";

    public static string Tr(params string[] cells) => "<w:tr>" + string.Concat(cells) + "</w:tr>";

    public static string Tbl(params string[] rows) => "<w:tbl><w:tblPr/>" + string.Concat(rows) + "</w:tbl>";

    /// <summary>An ODF package (<c>mimetype</c> + <c>content.xml</c>) whose body holds <paramref name="body"/> in
    /// <c>office:text</c> or <c>office:spreadsheet</c>.</summary>
    public static byte[] OpenDocument(string body, bool spreadsheet)
    {
        var kind = spreadsheet ? "spreadsheet" : "text";
        var content = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" "
            + "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" "
            + "xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" office:version=\"1.3\">"
            + $"<office:body><office:{kind}>{body}</office:{kind}></office:body></office:document-content>";
        return Zip(("mimetype", $"application/vnd.oasis.opendocument.{kind}"), ("content.xml", content));
    }

    /// <summary>The uncompressed size of the first entry, as declared in both its local header and the central
    /// directory, replaced by <paramref name="size"/> (a forged package).</summary>
    public static byte[] ForgeUncompressedSize(byte[] zip, uint size)
    {
        var bytes = (byte[])zip.Clone();
        for (var i = 0; i + 4 <= bytes.Length; i++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
            if (signature == 0x04034b50)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 22), size);
            }
            else if (signature == 0x02014b50)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), size);
            }
        }

        return bytes;
    }

    public static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
