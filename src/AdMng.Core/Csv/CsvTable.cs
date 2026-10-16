namespace AdMng.Core.Csv;

/// <summary>An in-memory CSV table (header row + data rows).</summary>
public sealed record CsvTable(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows, char Delimiter)
{
    /// <summary>Index of a header (case-insensitive), or -1.</summary>
    public int IndexOf(string header)
    {
        for (var i = 0; i < Headers.Count; i++)
        {
            if (string.Equals(Headers[i], header, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Cell value or empty string when the row is shorter than the header.</summary>
    public string Cell(int row, int column)
    {
        var r = Rows[row];
        return column >= 0 && column < r.Count ? r[column] : string.Empty;
    }
}

/// <summary>RFC 4180 CSV reading and writing tuned for Excel (Polish Excel uses ';' as delimiter).</summary>
public interface ICsvCodec
{
    /// <summary>
    /// Reads a CSV document. When <paramref name="delimiter"/> is null it is detected from the header line
    /// (';', ',', tab). Handles quoted fields with embedded delimiters, quotes and new lines, and a UTF-8 BOM.
    /// </summary>
    CsvTable Read(TextReader reader, char? delimiter = null);

    /// <summary>
    /// Writes a CSV document (quotes when needed). When <paramref name="protectFormulas"/> is true, cells starting with
    /// '=', '+', '-', '@', tab or CR are prefixed with an apostrophe to prevent CSV/formula injection in Excel.
    /// </summary>
    void Write(TextWriter writer, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, char delimiter = ';', bool protectFormulas = true);
}
