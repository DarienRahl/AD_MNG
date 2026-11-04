using System.Globalization;
using System.Text;
using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.GroupPolicy.Security;

/// <summary>
/// The advanced audit policy <c>Machine/Microsoft/Windows NT/Audit/audit.csv</c> ([MS-GPAC] 2.2.1.2) as an editable
/// document (K4): subcategory rows are set, replaced or removed by their GUID; every other row (audit options, global object
/// access SACLs, rows this editor does not understand) and the header are kept as read. Columns are found by their header
/// names, so a row is written in the file's own column order. Written in the file's encoding (UTF-8, with a BOM when it had
/// one; a new file is UTF-8 without BOM as GPMC writes it), CR LF.
/// </summary>
public sealed class AuditPolicyDocument
{
    /// <summary>The file's path in a GPO folder.</summary>
    public const string RelativePath = "Machine/Microsoft/Windows NT/Audit/audit.csv";

    /// <summary>The header GPMC writes.</summary>
    public static readonly IReadOnlyList<string> StandardColumns =
        ["Machine Name", "Policy Target", "Subcategory", "Subcategory GUID", "Inclusion Setting", "Exclusion Setting", "Setting Value"];

    private readonly List<Row> _rows = [];
    private readonly bool _bom;
    private readonly List<string> _columns;

    private AuditPolicyDocument(List<string> columns, bool bom)
    {
        _columns = columns;
        _bom = bom;
    }

    /// <summary>A new file (the standard header).</summary>
    public static AuditPolicyDocument New() => new([.. StandardColumns], bom: false);

    /// <summary>The document of a file; an empty or missing file is a new one.</summary>
    /// <exception cref="GpoParseException">Not valid text, an unterminated quote, or no header with Subcategory and Setting Value.</exception>
    public static AuditPolicyDocument Parse(ReadOnlySpan<byte> content)
    {
        var text = GpoText.DecodeAnsiOrUtf8(content, allowAnsi: false);
        var bom = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF;
        AuditPolicyDocument? document = null;
        foreach (var line in GpoText.Lines(text))
        {
            if (document is null)
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                var header = AuditCsvParser.SplitCsv(line).Select(f => f.Trim()).ToList();
                if (!header.Contains("Setting Value", StringComparer.OrdinalIgnoreCase) || !header.Contains("Subcategory", StringComparer.OrdinalIgnoreCase))
                {
                    throw new GpoParseException("audit.csv has no header with Subcategory and Setting Value.");
                }

                document = new AuditPolicyDocument(header, bom);
                continue;
            }

            document._rows.Add(new Row(line, line.Trim().Length == 0 ? [] : AuditCsvParser.SplitCsv(line)));
        }

        return document ?? new AuditPolicyDocument([.. StandardColumns], bom);
    }

    /// <summary>The subcategory settings: GUID → 0 no auditing, 1 success, 2 failure, 3 both (the last row of a GUID wins).</summary>
    public IReadOnlyDictionary<Guid, int> GetSubcategories()
    {
        var result = new Dictionary<Guid, int>();
        foreach (var row in _rows)
        {
            if (SubcategoryOf(row) is { } guid && int.TryParse(Field(row, "Setting Value"), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                result[guid] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Sets a subcategory (0–3) or removes its rows (null: not configured). <paramref name="name"/> is the English name
    /// written in the Subcategory column (<c>Audit Credential Validation</c>).
    /// </summary>
    public void SetSubcategory(Guid subcategory, string name, int? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (value is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "An audit setting is 0 to 3.");
        }

        var matches = _rows.Select((row, i) => (row, i)).Where(m => SubcategoryOf(m.row) == subcategory).ToList();
        if (value is null)
        {
            foreach (var (_, i) in matches.OrderByDescending(m => m.i))
            {
                _rows.RemoveAt(i);
            }

            return;
        }

        if (matches.Count == 1 && Field(matches[0].row, "Setting Value") == value.Value.ToString(CultureInfo.InvariantCulture))
        {
            return;
        }

        var fields = _columns.Select(column => column.ToUpperInvariant() switch
        {
            "POLICY TARGET" => "System",
            "SUBCATEGORY" => name,
            "SUBCATEGORY GUID" => subcategory.ToString("B", CultureInfo.InvariantCulture),
            "INCLUSION SETTING" => InclusionText(value.Value),
            "SETTING VALUE" => value.Value.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty,
        }).ToList();
        var row = new Row(string.Join(",", fields.Select(Csv)), fields);
        if (matches.Count == 0)
        {
            var at = _rows.Count;
            while (at > 0 && _rows[at - 1].Fields.Count == 0)
            {
                at--;
            }

            _rows.Insert(at, row);
            return;
        }

        _rows[matches[^1].i] = row;
        foreach (var (_, i) in matches.Take(matches.Count - 1).OrderByDescending(m => m.i))
        {
            _rows.RemoveAt(i);
        }
    }

    /// <summary>"No Auditing", "Success", "Failure", "Success and Failure" (the Inclusion Setting column).</summary>
    public static string InclusionText(int value) => AuditValues.Name(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>The file's text (CR LF).</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append(string.Join(",", _columns.Select(Csv))).Append("\r\n");
        foreach (var row in _rows)
        {
            text.Append(row.Raw).Append("\r\n");
        }

        return text.ToString();
    }

    public byte[] ToBytes()
    {
        var bytes = Encoding.UTF8.GetBytes(ToText());
        return _bom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes;
    }

    /// <summary>Never content.</summary>
    public override string ToString() => $"audit.csv ({_rows.Count} rows)";

    private Guid? SubcategoryOf(Row row) => Guid.TryParse(Field(row, "Subcategory GUID"), out var guid) ? guid : null;

    private string Field(Row row, string column)
    {
        var index = _columns.FindIndex(c => c.Equals(column, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index < row.Fields.Count ? row.Fields[index].Trim() : string.Empty;
    }

    private static string Csv(string field) =>
        field.Contains(',', StringComparison.Ordinal) || field.Contains('"', StringComparison.Ordinal)
            ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : field;

    private sealed record Row(string Raw, IReadOnlyList<string> Fields);
}
