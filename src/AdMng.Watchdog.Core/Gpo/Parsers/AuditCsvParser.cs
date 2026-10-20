using System.Text;

namespace AdMng.Watchdog.Core.Gpo.Parsers;

/// <summary>
/// Advanced audit policy <c>Machine/Microsoft/Windows NT/Audit/audit.csv</c> ([MS-GPAC] 2.2.1.2; UTF-8, BOM optional): a
/// header row <c>Machine Name,Policy Target,Subcategory,Subcategory GUID,Inclusion Setting,Exclusion Setting,Setting Value</c>
/// and one row per subcategory (<c>Setting Value</c> 0–3), audit option (<c>Option:CrashOnAuditFail</c> …) or global object
/// access SACL (<c>Policy Target</c> File System / Registry, SDDL value). Keys: <c>Computer/Audit/{subcategory GUID}</c>
/// (GUID lower case in braces) = <c>Success and Failure</c> etc., <c>Computer/Audit/Option/&lt;name&gt;</c>,
/// <c>Computer/Audit/&lt;Policy Target&gt;/&lt;Subcategory&gt;</c> otherwise. Columns are found by their header names; a file
/// without the <c>Setting Value</c> column fails.
/// </summary>
public static class AuditCsvParser
{
    public static void Parse(ReadOnlySpan<byte> content, GpoSettingSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var text = GpoText.DecodeAnsiOrUtf8(content, allowAnsi: false);
        Dictionary<string, int>? columns = null;
        foreach (var line in GpoText.Lines(text))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var fields = SplitCsv(line);
            if (columns is null)
            {
                columns = fields.Select((name, index) => (name: name.Trim(), index))
                    .GroupBy(f => f.name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);
                if (!columns.ContainsKey("Setting Value") || !columns.ContainsKey("Subcategory"))
                {
                    throw new GpoParseException("audit.csv has no header with Subcategory and Setting Value.");
                }

                continue;
            }

            string Field(string name) => columns.TryGetValue(name, out var i) && i < fields.Count ? fields[i].Trim() : string.Empty;
            var subcategory = Field("Subcategory");
            var guidText = Field("Subcategory GUID");
            var value = Field("Setting Value");
            var exclusion = Field("Exclusion Setting");
            if (Guid.TryParse(guidText, out var guid))
            {
                var setting = AuditValues.Name(value) + (exclusion.Length > 0 ? "; exclusion: " + exclusion : string.Empty);
                sink.Text("Computer/Audit/" + guid.ToString("B", System.Globalization.CultureInfo.InvariantCulture), setting, GpoSettingCategory.AuditPolicy);
            }
            else if (subcategory.StartsWith("Option:", StringComparison.OrdinalIgnoreCase))
            {
                sink.Text("Computer/Audit/Option/" + subcategory[7..].Trim(), value, GpoSettingCategory.AuditPolicy);
            }
            else if (subcategory.Length > 0 || Field("Policy Target").Length > 0)
            {
                sink.Text("Computer/Audit/" + Field("Policy Target") + "/" + subcategory, value, GpoSettingCategory.AuditPolicy);
            }
        }
    }

    /// <summary>RFC 4180 fields of one line (quotes, <c>""</c> escapes).</summary>
    internal static IReadOnlyList<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (quoted)
        {
            throw new GpoParseException("audit.csv has an unterminated quoted field.");
        }

        fields.Add(current.ToString());
        return fields;
    }
}
