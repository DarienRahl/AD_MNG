using System.Globalization;

namespace AdMng.Core.Correspondence.Fields;

/// <summary>An operator-defined field: <paramref name="Value"/> may use <c>{{other fields}}</c> (not other custom fields).</summary>
public sealed record CustomFieldDefinition(string Name, string Value);

/// <summary>
/// Field values of one recipient (or group), design note 3.2: built-ins, custom fields, then columns (<see cref="FieldKey"/>
/// matching). Lookup order: <c>zwrot</c> → salutation (blank → default); <c>nazwa</c>/<c>name</c> → row name, else the
/// salutation; <c>email</c>/<c>mail</c>/<c>adres e-mail</c> → address; <c>lista</c>/<c>liczba</c> → group values (null
/// when not grouped); custom fields (their value merged with the same lookup minus custom fields, text encoding); row
/// columns; a column known to the list but empty in this row → ""; else null (unknown).
/// </summary>
/// <remarks>When there are no group values (<c>list</c>/<c>count</c> null), <c>lista</c>/<c>liczba</c> fall through to
/// custom fields and columns, so a column called "Liczba" still works in ungrouped messages; without such a column they
/// are unknown. The first custom field of a name wins; a custom field with a blank name is ignored. Not thread-safe
/// (custom values are merged on first use and cached).</remarks>
public sealed class RecipientFieldSet
{
    private readonly string _email;
    private readonly string _name;
    private readonly string _salutation;
    private readonly string? _list;
    private readonly int? _count;
    private readonly Dictionary<string, string> _columns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _custom = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _customValues = new(StringComparer.Ordinal);

    /// <param name="email">The address of the message.</param>
    /// <param name="name">The row name (may be empty).</param>
    /// <param name="columns">The row's cells by column name.</param>
    /// <param name="salutation">The window's salutation (blank → <see cref="BuiltInFields.DefaultSalutation"/>).</param>
    /// <param name="customFields">The operator's custom fields.</param>
    /// <param name="knownColumns">Every column of the recipient list (empty in this row → "").</param>
    /// <param name="list">Grouped messages: the rendered list (HTML).</param>
    /// <param name="count">Grouped messages: the number of rows.</param>
    public RecipientFieldSet(string email, string name, IReadOnlyDictionary<string, string> columns, string salutation,
        IReadOnlyList<CustomFieldDefinition> customFields, IReadOnlyCollection<string> knownColumns,
        string? list = null, int? count = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(customFields);
        ArgumentNullException.ThrowIfNull(knownColumns);
        _email = email?.Trim() ?? string.Empty;
        _name = name?.Trim() ?? string.Empty;
        _salutation = string.IsNullOrWhiteSpace(salutation) ? BuiltInFields.DefaultSalutation : salutation.Trim();
        _list = list;
        _count = count;
        foreach (var (column, value) in columns)
        {
            _columns.TryAdd(FieldKey.Of(column), value ?? string.Empty);
        }

        foreach (var column in knownColumns)
        {
            _known.Add(FieldKey.Of(column));
        }

        foreach (var custom in customFields)
        {
            if (custom is not null && FieldKey.Of(custom.Name) is { Length: > 0 } key)
            {
                _custom.TryAdd(key, custom.Value ?? string.Empty);
            }
        }
    }

    /// <summary>The value of <paramref name="field"/>; null = unknown field.</summary>
    public string? Lookup(string field) => Lookup(FieldKey.Of(field), withCustom: true);

    public override string ToString() => nameof(RecipientFieldSet);

    private string? Lookup(string key, bool withCustom)
    {
        if (key.Length == 0)
        {
            return null;
        }

        if (key == BuiltInFields.Salutation)
        {
            return _salutation;
        }

        if (BuiltInFields.NameKeys.Contains(key))
        {
            return _name.Length > 0 ? _name : _salutation;
        }

        if (BuiltInFields.EmailKeys.Contains(key))
        {
            return _email;
        }

        if (key == BuiltInFields.List && _list is not null)
        {
            return _list;
        }

        if (key == BuiltInFields.Count && _count is { } count)
        {
            return count.ToString(CultureInfo.InvariantCulture);
        }

        if (withCustom && _custom.TryGetValue(key, out var template))
        {
            if (!_customValues.TryGetValue(key, out var merged))
            {
                merged = FieldTemplate.Merge(template, f => Lookup(FieldKey.Of(f), withCustom: false), FieldEncoding.Text);
                _customValues[key] = merged;
            }

            return merged;
        }

        if (_columns.TryGetValue(key, out var value))
        {
            return value;
        }

        return _known.Contains(key) ? string.Empty : null;
    }
}
