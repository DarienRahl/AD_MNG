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
public sealed class RecipientFieldSet
{
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
        string? list = null, int? count = null) => throw new NotImplementedException("U5 T1 core");

    /// <summary>The value of <paramref name="field"/>; null = unknown field.</summary>
    public string? Lookup(string field) => throw new NotImplementedException("U5 T1 core");

    public override string ToString() => nameof(RecipientFieldSet);
}
