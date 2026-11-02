using System.Globalization;
using System.Text;
using System.Xml;

namespace AdMng.Core.Correspondence.Import;

/// <summary>
/// The sheets of an XLSX/XLSM/XLTX workbook as rows of cells (design note 3.1): sheet order from <c>xl/workbook.xml</c>
/// and its relationships; shared strings (every <c>&lt;t&gt;</c> of an <c>&lt;si&gt;</c>, rich runs joined, phonetic runs
/// skipped); cells placed by their <c>r</c> reference (gaps stay empty), inline strings, formula strings and numbers as
/// text, booleans <c>TRUE</c>/<c>FALSE</c>. What Excel does not show – sheets in the state <c>hidden</c> or
/// <c>veryHidden</c>, rows marked <c>hidden</c> (hidden by hand, by a filter or a collapsed outline) – is left out and
/// counted, unless the caller asks for it.
/// </summary>
internal static class XlsxReader
{
    private const string WorkbookPath = "xl/workbook.xml";
    private const string WorkbookRelationshipsPath = "xl/_rels/workbook.xml.rels";

    /// <summary>The sheets in workbook order; hidden sheets and hidden rows only with <paramref name="includeHidden"/>.</summary>
    public static XlsxWorkbook ReadSheets(SafeZip zip, bool includeHidden = false)
    {
        var budget = new SpreadsheetLimits.Budget();
        var relationships = Relationships(zip);
        var shared = SharedStrings(zip, relationships, new SpreadsheetLimits.Budget());
        var sheets = new List<IReadOnlyList<IReadOnlyList<string>>>();
        var (paths, hiddenSheets) = SheetPaths(zip, relationships, includeHidden);
        var hiddenRows = 0;
        foreach (var path in paths)
        {
            using var reader = zip.OpenXml(path);
            if (reader is not null)
            {
                sheets.Add(ReadSheet(reader, shared, budget, includeHidden, ref hiddenRows));
            }
        }

        return new XlsxWorkbook(sheets, hiddenSheets, hiddenRows);
    }

    /// <summary>Relationship id → (type, target part name) of the workbook.</summary>
    private static Dictionary<string, (string Type, string Target)> Relationships(SafeZip zip)
    {
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        using var reader = zip.OpenXml(WorkbookRelationshipsPath);
        if (reader is null)
        {
            return result;
        }

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship"
                && reader.GetAttribute("Id") is { Length: > 0 } id && reader.GetAttribute("Target") is { Length: > 0 } target
                && !string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
            {
                result[id] = (reader.GetAttribute("Type") ?? string.Empty, PartName("xl/", target));
            }
        }

        return result;
    }

    /// <summary>The worksheets in workbook order (hidden ones counted, not listed, unless <paramref name="includeHidden"/>);
    /// without a usable workbook every <c>xl/worksheets/*.xml</c> in natural order.</summary>
    private static (List<string> Paths, int Hidden) SheetPaths(SafeZip zip, Dictionary<string, (string Type, string Target)> relationships, bool includeHidden)
    {
        var paths = new List<string>();
        var hidden = 0;
        using (var reader = zip.OpenXml(WorkbookPath))
        {
            if (reader is not null)
            {
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet")
                    {
                        continue;
                    }

                    var id = RelationshipId(reader);
                    if (id is not null && relationships.TryGetValue(id, out var relationship)
                        && relationship.Type.EndsWith("/worksheet", StringComparison.OrdinalIgnoreCase) && !paths.Contains(relationship.Target))
                    {
                        if (!includeHidden && reader.GetAttribute("state") is "hidden" or "veryHidden")
                        {
                            hidden++;
                            continue;
                        }

                        paths.Add(relationship.Target);
                    }
                }
            }
        }

        if (paths.Count > 0 || hidden > 0)
        {
            return (paths, hidden);
        }

        return ([.. zip.Names
            .Where(n => n.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && n.IndexOf('/', "xl/worksheets/".Length) < 0)
            .OrderBy(n => n.Length)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)], 0);
    }

    private static string? RelationshipId(XmlReader reader)
    {
        if (!reader.MoveToFirstAttribute())
        {
            return null;
        }

        string? id = null;
        do
        {
            if (reader.LocalName == "id" && reader.NamespaceURI.Length > 0)
            {
                id = reader.Value;
            }
        }
        while (reader.MoveToNextAttribute());

        reader.MoveToElement();
        return id;
    }

    /// <summary>The shared strings (their characters counted in <paramref name="budget"/>: once each – a cell that references
    /// one counts it again in the sheet's budget).</summary>
    private static List<string> SharedStrings(SafeZip zip, Dictionary<string, (string Type, string Target)> relationships, SpreadsheetLimits.Budget budget)
    {
        var path = relationships.Values.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.OrdinalIgnoreCase)).Target
            ?? "xl/sharedStrings.xml";
        var strings = new List<string>();
        using var reader = zip.OpenXml(path) ?? zip.OpenXml("xl/sharedStrings.xml");
        if (reader is null)
        {
            return strings;
        }

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                if (strings.Count >= SpreadsheetLimits.MaxSharedStrings)
                {
                    throw new InvalidDataException("The workbook has too many shared strings.");
                }

                strings.Add(reader.IsEmptyElement ? string.Empty : RichText(reader, budget));
            }
        }

        return strings;
    }

    /// <summary>The text of the current <c>si</c> / <c>is</c> element: every <c>t</c> outside phonetic runs, joined (its
    /// characters counted in <paramref name="budget"/> while it is read).</summary>
    private static string RichText(XmlReader reader, SpreadsheetLimits.Budget budget)
    {
        var depth = reader.Depth;
        var builder = new StringBuilder();
        var inText = 0;
        var phoneticDepth = -1;
        while (reader.Read() && reader.Depth > depth)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.LocalName == "rPh" && phoneticDepth < 0 && !reader.IsEmptyElement:
                    phoneticDepth = reader.Depth;
                    break;
                case XmlNodeType.Element when reader.LocalName == "t" && phoneticDepth < 0 && !reader.IsEmptyElement:
                    inText = reader.Depth;
                    break;
                case XmlNodeType.EndElement when reader.LocalName == "rPh" && reader.Depth == phoneticDepth:
                    phoneticDepth = -1;
                    break;
                case XmlNodeType.EndElement when reader.LocalName == "t" && reader.Depth == inText:
                    inText = 0;
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace when inText > 0:
                    var value = reader.Value;
                    budget.Characters(value.Length);
                    builder.Append(value);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>The rows of a sheet; rows marked hidden are counted in <paramref name="hiddenRows"/> (when they hold a value)
    /// and left out, unless <paramref name="includeHidden"/>.</summary>
    private static List<IReadOnlyList<string>> ReadSheet(XmlReader reader, List<string> shared, SpreadsheetLimits.Budget budget,
        bool includeHidden, ref int hiddenRows)
    {
        var rows = new List<IReadOnlyList<string>>();
        List<string>? row = null;
        var inRow = false;
        var hiddenRow = false;
        var hiddenCounted = false;
        var nextColumn = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row")
            {
                inRow = false;
                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "row")
            {
                // A row is materialized with its first value: rows Excel writes only for their formatting cost nothing.
                inRow = !reader.IsEmptyElement;
                hiddenRow = !includeHidden && reader.GetAttribute("hidden") is "1" or "true";
                hiddenCounted = false;
                row = null;
                nextColumn = 0;
                continue;
            }

            if (reader.LocalName != "c" || !inRow)
            {
                continue;
            }

            var column = SpreadsheetLimits.ColumnOf(reader.GetAttribute("r"));
            if (column < 0)
            {
                column = nextColumn;
            }

            nextColumn = column == int.MaxValue ? column : column + 1;
            var type = reader.GetAttribute("t");
            var value = reader.IsEmptyElement ? string.Empty : CellValue(reader, type, shared, budget);
            if (value.Length == 0 || column >= SpreadsheetLimits.MaxColumns)
            {
                continue;
            }

            if (hiddenRow)
            {
                // Hidden by hand, filtered out or in a collapsed group: the operator does not see it in Excel.
                if (!hiddenCounted)
                {
                    hiddenRows++;
                    hiddenCounted = true;
                }

                continue;
            }

            budget.Cell();   // its characters were counted by CellValue
            if (row is null)
            {
                row = [];
                rows.Add(row);
            }

            while (row.Count <= column)
            {
                row.Add(string.Empty);
            }

            row[column] = value;
        }

        return rows;
    }

    /// <summary>The text of the current <c>c</c> element, its characters counted in <paramref name="budget"/> (a shared
    /// string once per cell that references it); leaves the reader on its end tag.</summary>
    private static string CellValue(XmlReader reader, string? type, List<string> shared, SpreadsheetLimits.Budget budget)
    {
        var depth = reader.Depth;
        string? value = null;
        string? inline = null;
        reader.Read();
        while (!reader.EOF && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "v")
            {
                value = reader.ReadElementContentAsString();   // moves past </v>
                budget.Characters(value.Length);
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "is")
            {
                inline = reader.IsEmptyElement ? string.Empty : RichText(reader, budget);   // RichText stops on </is>
            }

            reader.Read();
        }

        switch (type)
        {
            case "s":
                if (int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < shared.Count)
                {
                    budget.Characters(shared[index].Length);
                    return shared[index];
                }

                return string.Empty;
            case "inlineStr":
                return inline ?? value ?? string.Empty;
            case "b":
                return value?.Trim() switch
                {
                    "1" => "TRUE",
                    "0" => "FALSE",
                    _ => value ?? string.Empty,
                };
            default:
                return value ?? inline ?? string.Empty;
        }
    }

    /// <summary>A relationship target as a part name: absolute ("/xl/…") or relative to <paramref name="baseFolder"/>,
    /// with "./" and "../" resolved.</summary>
    internal static string PartName(string baseFolder, string target)
    {
        var path = target.Replace('\\', '/');
        var combined = path.StartsWith('/') ? path.TrimStart('/') : baseFolder + path;
        var segments = new List<string>();
        foreach (var segment in combined.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }
}

/// <summary>The sheets of a workbook (<see cref="XlsxReader.ReadSheets"/>).</summary>
/// <param name="Sheets">The rows of cells of each sheet read, in workbook order.</param>
/// <param name="HiddenSheets">Worksheets left out because Excel hides them.</param>
/// <param name="HiddenRows">Rows with a value left out because Excel hides them.</param>
internal sealed record XlsxWorkbook(IReadOnlyList<IReadOnlyList<IReadOnlyList<string>>> Sheets, int HiddenSheets, int HiddenRows)
{
    public override string ToString() => $"{nameof(XlsxWorkbook)} ({Sheets.Count} sheets)";
}
