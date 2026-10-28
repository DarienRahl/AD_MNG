using System.Globalization;
using System.Text;
using System.Xml;

namespace AdMng.Core.Correspondence.Import;

/// <summary>
/// The text and tables of an ODT/ODS document (<c>content.xml</c>, design note 3.1): <c>text:p</c>/<c>text:h</c> → line,
/// <c>text:s</c> → spaces, <c>text:tab</c> → tab, <c>text:line-break</c> → line; every top-level <c>table:table</c> is a
/// table whose cells keep their columns (<c>table:number-columns-repeated</c> capped at 256 columns, repeated rows read
/// once); nested tables are flattened into their cell; annotations and tracked deletions are skipped; a <c>mailto:</c>
/// link whose text does not show the address gets it appended.
/// </summary>
internal static class OpenDocumentReader
{
    private const string ContentPath = "content.xml";
    private const string TextNamespace = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string TableNamespace = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string OfficeNamespace = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private const string XlinkNamespace = "http://www.w3.org/1999/xlink";
    private const string SvgNamespace = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    private const int MaxSpaces = 1000;

    public static DocumentParts Read(SafeZip zip)
    {
        using var reader = zip.OpenXml(ContentPath) ?? throw new InvalidDataException("The document has no content.xml.");
        return Read(reader);
    }

    private static DocumentParts Read(XmlReader reader)
    {
        var parts = new DocumentParts();
        var text = new StringBuilder();
        var budget = new SpreadsheetLimits.Budget();
        var tableDepth = 0;
        List<IReadOnlyList<string>>? rows = null;
        List<string>? row = null;
        var pendingEmpty = 0;
        StringBuilder? cell = null;
        var repeat = 1;
        var paragraphDepth = 0;
        var links = new Stack<(int Depth, string? Target, StringBuilder Builder, int Start)>();

        StringBuilder Current() => cell ?? text;

        var advanced = false;
        while (advanced || reader.Read())
        {
            advanced = false;
            switch (reader.NodeType)
            {
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    if (paragraphDepth > 0)
                    {
                        // White space of the XML is collapsible in ODF (text:s carries the significant one).
                        Current().Append(reader.Value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '));
                    }

                    continue;
                case XmlNodeType.EndElement:
                    break;
                case XmlNodeType.Element:
                    if (IsSkipped(reader))
                    {
                        reader.Skip();
                        advanced = !reader.EOF;
                        continue;
                    }

                    StartElement();
                    continue;
                default:
                    continue;
            }

            // End elements.
            if (reader.NamespaceURI == TextNamespace)
            {
                switch (reader.LocalName)
                {
                    case "p" or "h":
                        paragraphDepth--;
                        Current().Append('\n');
                        break;
                    case "a" when links.Count > 0 && links.Peek().Depth == reader.Depth:
                        var link = links.Pop();
                        DocumentParts.AppendLinkAddress(link.Builder, link.Start, link.Target);
                        break;
                }
            }
            else if (reader.NamespaceURI == TableNamespace)
            {
                switch (reader.LocalName)
                {
                    case "table-cell" or "covered-table-cell" when tableDepth == 1 && row is not null && cell is not null:
                        AddCell(cell.ToString().Trim());
                        cell = null;
                        break;
                    case "table-cell" or "covered-table-cell" when tableDepth > 1:
                        Current().Append(' ');
                        break;
                    case "table-row" when tableDepth == 1 && rows is not null && row is not null:
                        if (row.Count > 0)
                        {
                            rows.Add(row);
                        }

                        row = null;
                        break;
                    case "table-row" when tableDepth > 1:
                        Current().Append('\n');
                        break;
                    case "table":
                        EndTable();
                        break;
                }
            }
        }

        parts.AddText(text);
        return parts;

        void StartElement()
        {
            var empty = reader.IsEmptyElement;
            if (reader.NamespaceURI == TextNamespace)
            {
                switch (reader.LocalName)
                {
                    case "p" or "h":
                        if (empty)
                        {
                            Current().Append('\n');
                        }
                        else
                        {
                            if (paragraphDepth > 0 && Current() is { Length: > 0 } nested && nested[^1] != '\n')
                            {
                                nested.Append('\n');   // a paragraph inside a paragraph (a text box)
                            }

                            paragraphDepth++;
                        }

                        break;
                    case "s" when paragraphDepth > 0:
                        var count = int.TryParse(reader.GetAttribute("c", TextNamespace), NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : 1;
                        Current().Append(' ', Math.Clamp(count, 1, MaxSpaces));
                        break;
                    case "tab" when paragraphDepth > 0:
                        Current().Append(cell is null ? '\t' : ' ');
                        break;
                    case "line-break" when paragraphDepth > 0:
                        Current().Append('\n');
                        break;
                    case "a" when !empty:
                        var builder = Current();
                        links.Push((reader.Depth, reader.GetAttribute("href", XlinkNamespace), builder, builder.Length));
                        break;
                }
            }
            else if (reader.NamespaceURI == TableNamespace)
            {
                switch (reader.LocalName)
                {
                    case "table" when !empty:
                        if (tableDepth == 0)
                        {
                            parts.AddText(text);
                            rows = [];
                        }

                        tableDepth++;
                        break;
                    case "table-row" when tableDepth == 1 && !empty:
                        row = [];
                        pendingEmpty = 0;
                        break;
                    case "table-cell" or "covered-table-cell" when tableDepth == 1 && row is not null:
                        repeat = int.TryParse(reader.GetAttribute("number-columns-repeated", TableNamespace), NumberStyles.None, CultureInfo.InvariantCulture, out var r)
                            ? Math.Clamp(r, 1, SpreadsheetLimits.MaxColumns)
                            : 1;
                        if (empty)
                        {
                            AddCell(string.Empty);
                        }
                        else
                        {
                            cell = new StringBuilder();
                        }

                        break;
                }
            }
        }

        void AddCell(string value)
        {
            if (value.Length == 0)
            {
                pendingEmpty = Math.Min(pendingEmpty + repeat, SpreadsheetLimits.MaxColumns);   // added only when a value follows
                return;
            }

            for (var i = 0; i < pendingEmpty && row!.Count < SpreadsheetLimits.MaxColumns; i++)
            {
                row.Add(string.Empty);
            }

            pendingEmpty = 0;
            for (var i = 0; i < repeat && row!.Count < SpreadsheetLimits.MaxColumns; i++)
            {
                budget.Cell();
                row.Add(value);
            }
        }

        void EndTable()
        {
            tableDepth--;
            if (tableDepth == 0 && rows is not null)
            {
                parts.AddTable(rows);
                rows = null;
            }
        }
    }

    /// <summary>Content that is not part of the document's text: annotations (comments), tracked changes (deleted text),
    /// shape titles and descriptions.</summary>
    private static bool IsSkipped(XmlReader reader) =>
        (reader.NamespaceURI == OfficeNamespace && reader.LocalName == "annotation")
        || (reader.NamespaceURI == TextNamespace && reader.LocalName is "tracked-changes" or "note-citation")
        || (reader.NamespaceURI == SvgNamespace && reader.LocalName is "title" or "desc");
}
