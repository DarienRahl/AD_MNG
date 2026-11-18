using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Security;
using AdMng.Core.Formatting;
using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Tables;

/// <summary>
/// Turns the objects of a pipeline into the plain table <c>Out-AdmGrid</c> and <c>Export-AdmList</c> hand to the window
/// (docs/dev/r1-powershell-window.md 12.4): the columns are the properties of the objects (in the order they were first seen, the
/// <c>DistinguishedName</c> first), every value is the text the grid shows, and a <c>PSObject</c> is dropped as soon as its
/// row is made - no engine object ever reaches the UI thread. Built on the pipeline thread, in memory only.
/// </summary>
internal sealed class AdmTableBuilder
{
    /// <summary>The name of the one column of objects that have no properties of their own (numbers, strings, dates).</summary>
    public const string ValueColumn = "Value";

    /// <summary>The column that makes a row openable in the program.</summary>
    public const string DistinguishedNameColumn = "DistinguishedName";

    private readonly List<string> _columns = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string?[]> _rows = [];

    public int RowCount => _rows.Count;

    /// <summary>Adds a row for <paramref name="item"/>; an array or a list is added element by element.</summary>
    public void Add(PSObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.BaseObject is IEnumerable list and not string and not IDictionary)
        {
            foreach (var element in list)
            {
                AddRow(element is PSObject wrapped ? wrapped : PSObject.AsPSObject(element));
            }

            return;
        }

        AddRow(item);
    }

    /// <summary>The table, or null when no row was added. The distinguished name column, when there is one, is the first.</summary>
    public PowerShellGridTable? Build(string? title)
    {
        if (_rows.Count == 0)
        {
            return null;
        }

        var order = Enumerable.Range(0, _columns.Count).ToList();
        if (_index.TryGetValue(DistinguishedNameColumn, out var dn) && dn != 0)
        {
            order.Remove(dn);
            order.Insert(0, dn);
        }

        var rows = new List<IReadOnlyList<string?>>(_rows.Count);
        foreach (var row in _rows)
        {
            var cells = new string?[order.Count];
            for (var column = 0; column < order.Count; column++)
            {
                var source = order[column];
                cells[column] = source < row.Length ? row[source] : null;
            }

            rows.Add(cells);
        }

        return new PowerShellGridTable(
            [.. order.Select(column => _columns[column])],
            rows,
            title,
            _index.ContainsKey(DistinguishedNameColumn) ? _columns[_index[DistinguishedNameColumn]] : null);
    }

    private void AddRow(PSObject item)
    {
        var cells = new List<(string Name, string? Text)>();
        var value = item.BaseObject;
        if (value is null)
        {
            cells.Add((ValueColumn, null));
        }
        else if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                cells.Add((Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty, CellText.Of(entry.Value)));
            }
        }
        else if (CellText.IsSimple(value))
        {
            cells.Add((ValueColumn, CellText.Of(value)));
        }
        else
        {
            foreach (var property in item.Properties)
            {
                if (!property.IsGettable)
                {
                    continue;
                }

                string? text;
                try
                {
                    text = CellText.Of(property.Value);
                }
                catch (Exception ex) when (ex is GetValueException or InvalidOperationException or NotSupportedException or ArgumentException)
                {
                    // A property that throws when it is read is a cell without a value; the rest of the row stands.
                    text = null;
                }

                cells.Add((property.Name, text));
            }
        }

        // The columns this row introduces are registered first, so the row is allocated once, as wide as the table is now.
        var columns = new int[cells.Count];
        for (var i = 0; i < cells.Count; i++)
        {
            var name = cells[i].Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                columns[i] = -1;
                continue;
            }

            if (!_index.TryGetValue(name, out var column))
            {
                column = _columns.Count;
                _columns.Add(name);
                _index[name] = column;
            }

            columns[i] = column;
        }

        var row = new string?[_columns.Count];
        for (var i = 0; i < cells.Count; i++)
        {
            if (columns[i] >= 0)
            {
                row[columns[i]] = cells[i].Text;
            }
        }

        _rows.Add(row);
    }
}

/// <summary>The text of a value in a cell of the grid and of the exported file.</summary>
internal static class CellText
{
    /// <summary>A date and time as the grid shows it: sorts as text, means the same in every language.</summary>
    public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>True for a value that is shown as one cell named "Value" rather than as its properties.</summary>
    public static bool IsSimple(object value) =>
        value is string or char or bool or DateTime or DateTimeOffset or TimeSpan or Guid or Uri or Enum or SecureString or byte[]
        || value.GetType().IsPrimitive || value is decimal or System.Numerics.BigInteger;

    /// <summary>
    /// The text of <paramref name="value"/>: null for no value; a list is its elements joined by the separator of the program's own
    /// lists; a date and time as <see cref="DateTimeFormat"/>; numbers independent of the language; binary values only as their size;
    /// a secure string never as anything but its name.
    /// </summary>
    public static string? Of(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return null;
            case PSObject wrapped:
                return wrapped.BaseObject is PSCustomObject ? wrapped.ToString() : Of(wrapped.BaseObject);
            case string text:
                return text;
            case SecureString:
                return "(secure string)";
            case bool flag:
                return flag ? "True" : "False";
            case DateTime moment:
                return moment.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
            case DateTimeOffset offset:
                return offset.LocalDateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
            case TimeSpan span:
                return span.ToString("c", CultureInfo.InvariantCulture);
            case Guid guid:
                return guid.ToString("D");
            case Enum or Uri or char:
                return value.ToString();
            case byte[] bytes:
                return $"(binary, {bytes.Length} bytes)";
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            case IDictionary:
                return value.ToString();
            case IEnumerable list:
                return string.Join(AttributeFormatter.ValueSeparator, list.Cast<object?>().Select(Of).Where(part => part is not null));
            default:
                try
                {
                    return value.ToString();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return null;
                }
        }
    }
}
