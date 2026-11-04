using System.Collections.ObjectModel;
using AdMng.Core.Correspondence.Import;

namespace AdMng.Core.Correspondence.Composition;

/// <summary>Rows to messages (design note 3.4): one unit per row, or one per address with all its rows.</summary>
public static class RecipientGrouping
{
    /// <summary>
    /// The units in first-appearance order; without <paramref name="byAddress"/> one unit per row. Mailbox rows use it with
    /// the owner's address.
    /// </summary>
    /// <remarks>Addresses are compared normalized (<see cref="EmailAddressText.Normalize"/>, so case and a "mailto:" do not
    /// matter); a unit keeps the first row's address as written and the first non-empty name of its rows. Rows without an
    /// address are never grouped (each is a unit of its own). Null fields become an empty row.</remarks>
    public static IReadOnlyList<CorrespondenceUnit> Group(
        IEnumerable<(string Email, string Name, IReadOnlyDictionary<string, string> Fields)> rows, bool byAddress)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var groups = new List<(string Email, string Name, List<IReadOnlyDictionary<string, string>> Rows)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (email, name, fields) in rows)
        {
            var row = fields ?? ReadOnlyDictionary<string, string>.Empty;
            var key = byAddress ? EmailAddressText.Normalize(email) : string.Empty;
            if (key.Length > 0 && index.TryGetValue(key, out var existing))
            {
                var group = groups[existing];
                group.Rows.Add(row);
                if (group.Name.Length == 0 && !string.IsNullOrWhiteSpace(name))
                {
                    groups[existing] = (group.Email, name.Trim(), group.Rows);
                }

                continue;
            }

            if (key.Length > 0)
            {
                index[key] = groups.Count;
            }

            groups.Add((email ?? string.Empty, name?.Trim() ?? string.Empty, [row]));
        }

        return [.. groups.Select(g => new CorrespondenceUnit(g.Email, g.Name, g.Rows.AsReadOnly()))];
    }
}
