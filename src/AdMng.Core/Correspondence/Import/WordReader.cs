using System.Globalization;
using System.Text;
using System.Xml;

namespace AdMng.Core.Correspondence.Import;

/// <summary>
/// The text and tables of a DOCX/DOTX/DOCM document (<c>word/document.xml</c>, design note 3.1): <c>w:p</c> → line,
/// <c>w:tab</c> → tab, <c>w:br</c>/<c>w:cr</c> → line; every top-level <c>w:tbl</c> is a table whose cells keep their
/// columns (<c>w:gridSpan</c> adds empty cells, a <c>w:vMerge</c> continuation repeats the cell above); nested tables are
/// flattened into their cell. A <c>mailto:</c> hyperlink whose text does not show the address gets it appended. Deleted
/// text, field codes and the fallback copies of alternate content are skipped.
/// </summary>
internal static class WordReader
{
    private const string DocumentPath = "word/document.xml";
    private const string RelationshipsPath = "word/_rels/document.xml.rels";
    private const int MaxSpan = 64;

    public static DocumentParts Read(SafeZip zip)
    {
        var links = Hyperlinks(zip);
        using var reader = zip.OpenXml(DocumentPath) ?? throw new InvalidDataException("The document has no word/document.xml.");
        return Read(reader, links);
    }

    private static DocumentParts Read(XmlReader reader, Dictionary<string, string> links)
    {
        var parts = new DocumentParts();
        var text = new StringBuilder();
        var budget = new SpreadsheetLimits.Budget();
        var tableDepth = 0;
        List<IReadOnlyList<string>>? rows = null;
        List<string>? row = null;
        IReadOnlyList<string>? previousRow = null;
        StringBuilder? cell = null;
        var span = 1;
        var continuesMerge = false;
        var runDepth = -1;
        var textDepth = -1;
        var hyperlinks = new Stack<(int Depth, string? Target, StringBuilder Builder, int Start)>();

        StringBuilder Current() => cell ?? text;

        var advanced = false;
        while (advanced || reader.Read())
        {
            advanced = false;
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                if (textDepth >= 0)
                {
                    Current().Append(reader.Value);
                }

                continue;
            }

            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (!IsWord(reader.NamespaceURI))
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "t" when reader.Depth == textDepth:
                        textDepth = -1;
                        break;
                    case "r" when reader.Depth == runDepth:
                        runDepth = -1;
                        break;
                    case "p":
                        Current().Append('\n');
                        break;
                    case "hyperlink" when hyperlinks.Count > 0 && hyperlinks.Peek().Depth == reader.Depth:
                        var link = hyperlinks.Pop();
                        DocumentParts.AppendLinkAddress(link.Builder, link.Start, link.Target);
                        break;
                    case "tc" when tableDepth == 1 && row is not null && cell is not null:
                        var value = cell.ToString().Trim();
                        var column = row.Count;
                        if (value.Length == 0 && continuesMerge && previousRow is not null && column < previousRow.Count)
                        {
                            value = previousRow[column];
                        }

                        if (value.Length > 0)
                        {
                            budget.Cell();
                        }

                        row.Add(value);
                        for (var i = 1; i < span && row.Count < SpreadsheetLimits.MaxColumns; i++)
                        {
                            row.Add(string.Empty);
                        }

                        cell = null;
                        break;
                    case "tc" when tableDepth > 1:
                        Current().Append(' ');
                        break;
                    case "tr" when tableDepth == 1 && rows is not null && row is not null:
                        if (row.Any(c => c.Length > 0))
                        {
                            rows.Add(row);
                        }

                        previousRow = row;
                        row = null;
                        break;
                    case "tr" when tableDepth > 1:
                        Current().Append('\n');
                        break;
                    case "tbl":
                        tableDepth--;
                        if (tableDepth == 0 && rows is not null)
                        {
                            parts.AddTable(rows);
                            rows = null;
                            previousRow = null;
                        }

                        break;
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "Fallback" && !IsWord(reader.NamespaceURI))
            {
                reader.Skip();   // markup compatibility: the Choice holds the same content
                advanced = !reader.EOF;
                continue;
            }

            if (!IsWord(reader.NamespaceURI))
            {
                continue;
            }

            var empty = reader.IsEmptyElement;
            switch (reader.LocalName)
            {
                case "r":
                    runDepth = empty ? -1 : reader.Depth;
                    break;
                case "t" when !empty:
                    textDepth = reader.Depth;
                    break;
                case "tab" when runDepth >= 0:
                    Current().Append(cell is null ? '\t' : ' ');
                    break;
                case "br" or "cr" when runDepth >= 0:
                    Current().Append('\n');
                    break;
                case "noBreakHyphen" when runDepth >= 0:
                    Current().Append('-');
                    break;
                case "p" when empty:
                    Current().Append('\n');
                    break;
                case "hyperlink" when !empty:
                    var id = RelationshipId(reader);
                    var builder = Current();
                    hyperlinks.Push((reader.Depth, id is not null && links.TryGetValue(id, out var target) ? target : null, builder, builder.Length));
                    break;
                case "tbl" when !empty:
                    if (tableDepth == 0)
                    {
                        parts.AddText(text);
                        rows = [];
                        previousRow = null;
                    }

                    tableDepth++;
                    break;
                case "tr" when tableDepth == 1 && !empty:
                    row = [];
                    break;
                case "tc" when tableDepth == 1 && row is not null:
                    cell = new StringBuilder();
                    span = 1;
                    continuesMerge = false;
                    if (empty)
                    {
                        row.Add(string.Empty);
                        cell = null;
                    }

                    break;
                case "gridSpan" when tableDepth == 1 && cell is not null:
                    span = int.TryParse(Attribute(reader, "val"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 1, MaxSpan) : 1;
                    break;
                case "vMerge" when tableDepth == 1 && cell is not null:
                    continuesMerge = Attribute(reader, "val") is null or "continue";
                    break;
            }
        }

        parts.AddText(text);
        return parts;
    }

    private static bool IsWord(string ns) =>
        ns.EndsWith("/wordprocessingml/2006/main", StringComparison.Ordinal) || ns.EndsWith("/ooxml/wordprocessingml/main", StringComparison.Ordinal);

    private static string? Attribute(XmlReader reader, string localName)
    {
        if (!reader.MoveToFirstAttribute())
        {
            return null;
        }

        string? value = null;
        do
        {
            if (reader.LocalName == localName)
            {
                value = reader.Value;
                break;
            }
        }
        while (reader.MoveToNextAttribute());

        reader.MoveToElement();
        return value;
    }

    private static string? RelationshipId(XmlReader reader)
    {
        if (!reader.MoveToFirstAttribute())
        {
            return null;
        }

        string? value = null;
        do
        {
            if (reader.LocalName == "id" && reader.NamespaceURI.Contains("relationships", StringComparison.Ordinal))
            {
                value = reader.Value;
                break;
            }
        }
        while (reader.MoveToNextAttribute());

        reader.MoveToElement();
        return value;
    }

    /// <summary>Relationship id → target of the document's external links.</summary>
    private static Dictionary<string, string> Hyperlinks(SafeZip zip)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = zip.OpenXml(RelationshipsPath);
        if (reader is null)
        {
            return result;
        }

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship"
                && reader.GetAttribute("Id") is { Length: > 0 } id && reader.GetAttribute("Target") is { Length: > 0 } target)
            {
                result[id] = target;
            }
        }

        return result;
    }
}
