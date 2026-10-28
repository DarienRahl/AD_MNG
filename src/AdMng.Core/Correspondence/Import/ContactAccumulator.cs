using System.Collections.ObjectModel;
using AdMng.Core.Correspondence.Fields;

namespace AdMng.Core.Correspondence.Import;

/// <summary>Collects contacts in order: de-duplicated by address (a later name fills an empty one), columns in order of
/// first appearance (distinct by <see cref="FieldKey"/>), invalid and duplicate counts.</summary>
internal sealed class ContactAccumulator
{
    private static readonly IReadOnlyDictionary<string, string> NoFields = ReadOnlyDictionary<string, string>.Empty;

    private readonly List<(string Email, string Name, IReadOnlyDictionary<string, string> Fields)> _contacts = [];
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string> _columns = [];
    private readonly HashSet<string> _columnKeys = new(StringComparer.Ordinal);

    public int Count => _contacts.Count;

    public int InvalidAddresses { get; set; }

    public int Duplicates { get; private set; }

    /// <summary>Adds a contact; <paramref name="email"/> is normalized and valid. False for a repeated address.</summary>
    public bool Add(string email, string name, IReadOnlyDictionary<string, string>? fields)
    {
        if (_index.TryGetValue(email, out var existing))
        {
            Duplicates++;
            var (_, oldName, oldFields) = _contacts[existing];
            if (oldName.Length == 0 && name.Length > 0)
            {
                _contacts[existing] = (email, name, oldFields);
            }

            return false;
        }

        _index[email] = _contacts.Count;
        _contacts.Add((email, name, fields ?? NoFields));
        return true;
    }

    public void AddColumns(IEnumerable<string> columns)
    {
        foreach (var column in columns)
        {
            if (_columnKeys.Add(FieldKey.Of(column)))
            {
                _columns.Add(column);
            }
        }
    }

    /// <summary>Adds the contacts, columns and counts of a part (a table or text of the same file).</summary>
    public void Merge(ContactImportResult part)
    {
        AddColumns(part.Columns);
        InvalidAddresses += part.InvalidAddresses;
        Duplicates += part.Duplicates;
        foreach (var contact in part.Contacts)
        {
            Add(contact.Email, contact.Name, contact.Fields);
        }
    }

    public ContactImportResult Result(ContactSourceFormat format) => new(
        [.. _contacts.Select(c => new ImportedContact(c.Email, c.Name, c.Fields))],
        [.. _columns],
        InvalidAddresses,
        Duplicates,
        format);
}
