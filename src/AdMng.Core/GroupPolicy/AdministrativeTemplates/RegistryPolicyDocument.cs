using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>What Registry.pol leaves for one value once its entries are applied in order.</summary>
public enum RegistryValueStateKind
{
    /// <summary>No entry names the value.</summary>
    Missing,

    /// <summary>The value is set (<see cref="RegistryValueState.Entry"/>).</summary>
    Set,

    /// <summary>The value is deleted (<c>**del.</c>, <c>**delvals.</c>, <c>**DeleteValues</c>, <c>**DeleteKeys</c>).</summary>
    Deleted,
}

/// <summary>The net state of one value.</summary>
/// <param name="Kind">Missing, set or deleted.</param>
/// <param name="Entry">The entry that sets it (its value name may carry <c>**soft.</c>).</param>
public sealed record RegistryValueState(RegistryValueStateKind Kind, RegistryPolEntry? Entry = null)
{
    public static RegistryValueState Missing { get; } = new(RegistryValueStateKind.Missing);

    public static RegistryValueState Deleted { get; } = new(RegistryValueStateKind.Deleted);

    public bool IsSet => Kind == RegistryValueStateKind.Set;

    public bool IsDeleted => Kind == RegistryValueStateKind.Deleted;
}

/// <summary>
/// The net effect of a Registry.pol on each key and value ([MS-GPREG] 2.2.1.1–2.2.1.2: the client applies the entries in
/// order, later entries win): built once per document version and read by <see cref="PolicyEngine"/>.
/// </summary>
public sealed class RegistryPolicyView
{
    private readonly Dictionary<string, KeyState> _keys = new(StringComparer.OrdinalIgnoreCase);

    internal RegistryPolicyView(IEnumerable<RegistryPolEntry> entries)
    {
        foreach (var entry in entries)
        {
            var key = RegistryPolicyDocument.NormalizeKey(entry.Key);
            var name = entry.ValueName;
            if (name.StartsWith(RegistryPolicyDocument.DeletePrefix, StringComparison.OrdinalIgnoreCase))
            {
                State(key).Delete(name[RegistryPolicyDocument.DeletePrefix.Length..]);
            }
            else if (name.Equals(RegistryPolicyDocument.DeleteAllValues, StringComparison.OrdinalIgnoreCase))
            {
                State(key).DeleteAll();
            }
            else if (name.Equals(RegistryPolicyDocument.DeleteValuesList, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in SemicolonList(entry.Data))
                {
                    State(key).Delete(item);
                }
            }
            else if (name.Equals(RegistryPolicyDocument.DeleteKeysList, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var subkey in SemicolonList(entry.Data))
                {
                    var prefix = key.Length == 0 ? subkey.Trim('\\') : key + "\\" + subkey.Trim('\\');
                    foreach (var (path, state) in _keys)
                    {
                        if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                        {
                            state.DeleteAll();
                        }
                    }

                    State(prefix).DeleteAll();
                }
            }
            else if (name.StartsWith(RegistryPolicyDocument.SoftPrefix, StringComparison.OrdinalIgnoreCase))
            {
                State(key).Set(name[RegistryPolicyDocument.SoftPrefix.Length..], entry);
            }
            else if (name.StartsWith("**", StringComparison.Ordinal) || (name.Length == 0 && entry.Type == RegistryFormat.RegNone && entry.Data.Length == 0))
            {
                // **SecureKey and other directives, key creation: no value.
            }
            else
            {
                State(key).Set(name, entry);
            }
        }
    }

    /// <summary>The net state of a value.</summary>
    public RegistryValueState Get(string key, string valueName)
    {
        ArgumentNullException.ThrowIfNull(valueName);
        return _keys.TryGetValue(RegistryPolicyDocument.NormalizeKey(key), out var state) ? state.Get(valueName) : RegistryValueState.Missing;
    }

    /// <summary>The values set below <paramref name="key"/> in the order they are first set (after the last delete of each).</summary>
    public IReadOnlyList<RegistryPolEntry> ValuesOf(string key) =>
        _keys.TryGetValue(RegistryPolicyDocument.NormalizeKey(key), out var state) ? state.Values() : [];

    /// <summary>A <c>**delvals.</c> (or <c>**DeleteKeys</c>) deletes the key's values on the client.</summary>
    public bool ClearsValues(string key) =>
        _keys.TryGetValue(RegistryPolicyDocument.NormalizeKey(key), out var state) && state.ClearsAll;

    private KeyState State(string key)
    {
        if (!_keys.TryGetValue(key, out var state))
        {
            state = new KeyState();
            _keys[key] = state;
        }

        return state;
    }

    private static IEnumerable<string> SemicolonList(byte[] data) =>
        RegistryPolFile.ReadString(data).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed class KeyState
    {
        private readonly List<string> _order = [];
        private readonly Dictionary<string, RegistryPolEntry> _set = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);

        public bool ClearsAll { get; private set; }

        public void Set(string name, RegistryPolEntry entry)
        {
            if (!_set.ContainsKey(name))
            {
                _order.Add(name);
            }

            _set[name] = entry;
            _deleted.Remove(name);
        }

        public void Delete(string name)
        {
            if (_set.Remove(name))
            {
                _order.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            }

            _deleted.Add(name);
        }

        public void DeleteAll()
        {
            foreach (var name in _order)
            {
                _deleted.Add(name);
            }

            _order.Clear();
            _set.Clear();
            ClearsAll = true;
        }

        public RegistryValueState Get(string name) =>
            _set.TryGetValue(name, out var entry) ? new RegistryValueState(RegistryValueStateKind.Set, entry)
            : _deleted.Contains(name) || ClearsAll ? RegistryValueState.Deleted
            : RegistryValueState.Missing;

        public IReadOnlyList<RegistryPolEntry> Values() => [.. _order.Select(n => _set[n])];
    }
}

/// <summary>
/// An editable Registry.pol (K3): its entries in file order, changed the way the Microsoft editor changes its in-memory
/// registry copy, keeping every value in its place: a value set again stays where it is, a deleted value's <c>**del.</c>
/// entry takes the value's place (and the value set again takes the deletion's), a new value goes after the last entry of
/// its key, a new key goes where its path sorts among the keys (keys compared per segment without case, as the registry
/// enumerates them), <c>**delvals.</c> leads its key. Entries the editor does not touch stay as they are, byte for byte.
/// Not thread-safe.
/// </summary>
public sealed class RegistryPolicyDocument
{
    /// <summary>Deletes one value.</summary>
    public const string DeletePrefix = "**del.";

    /// <summary>Deletes every value of the key.</summary>
    public const string DeleteAllValues = "**delvals.";

    /// <summary>Sets a value only when the client does not have it.</summary>
    public const string SoftPrefix = "**soft.";

    /// <summary>Deletes the values named in the data (<c>a;b</c>).</summary>
    public const string DeleteValuesList = "**DeleteValues";

    /// <summary>Deletes the subkeys named in the data (<c>a;b</c>).</summary>
    public const string DeleteKeysList = "**DeleteKeys";

    private readonly List<RegistryPolEntry> _entries;
    private RegistryPolicyView? _view;

    public RegistryPolicyDocument(IEnumerable<RegistryPolEntry>? entries = null)
    {
        _entries = entries is null ? [] : [.. entries];
    }

    /// <summary>The document of a file; null or empty content: no entries.</summary>
    /// <exception cref="GpoParseException">The file is malformed.</exception>
    public static RegistryPolicyDocument Parse(byte[]? content) => new(content is null ? [] : RegistryPolFile.Read(content));

    public IReadOnlyList<RegistryPolEntry> Entries => _entries;

    /// <summary>The net effect of the entries (cached until the next change).</summary>
    public RegistryPolicyView View => _view ??= new RegistryPolicyView(_entries);

    /// <summary>The file content (header and entries).</summary>
    public byte[] ToBytes() => RegistryPolFile.Write(_entries);

    public RegistryPolicyDocument Clone() => new(_entries);

    /// <summary>A key compared and stored without surrounding separators.</summary>
    public static string NormalizeKey(string? key) => (key ?? string.Empty).Trim().Trim('\\');

    /// <summary>
    /// Sets a value (a <c>**soft.</c> one with <paramref name="soft"/>): in the place of its entry, or of its deletion or other
    /// form, which it replaces; otherwise after the key's entries.
    /// </summary>
    public void Set(string key, string valueName, uint type, byte[] data, bool soft = false)
    {
        ArgumentNullException.ThrowIfNull(valueName);
        ArgumentNullException.ThrowIfNull(data);
        Put(NormalizeKey(key), soft ? SoftPrefix + valueName : valueName, [DeletePrefix + valueName, soft ? valueName : SoftPrefix + valueName], type, data);
    }

    /// <summary>Deletes a value on the client: a <c>**del.</c> entry in the place of the entry that set it (or after the key's entries).</summary>
    public void MarkDeleted(string key, string valueName)
    {
        ArgumentNullException.ThrowIfNull(valueName);
        Put(NormalizeKey(key), DeletePrefix + valueName, [valueName, SoftPrefix + valueName], RegistryFormat.RegSz, RegistryPolEntry.DeletionData);
    }

    /// <summary>Removes every entry about a value (set, soft or deleted): the policy no longer says anything about it.</summary>
    public void Forget(string key, string valueName)
    {
        ArgumentNullException.ThrowIfNull(valueName);
        RemoveNames(NormalizeKey(key), valueName, SoftPrefix + valueName, DeletePrefix + valueName);
    }

    /// <summary>
    /// Removes every entry of a key (values, deletions, <c>**delvals.</c>) except the values named in <paramref name="keep"/>
    /// (other values of the same policy in that key).
    /// </summary>
    public void ForgetKey(string key, IReadOnlyCollection<string>? keep = null)
    {
        var normalized = NormalizeKey(key);
        var removed = _entries.RemoveAll(e => SameKey(e.Key, normalized) && (keep is null || !Keeps(keep, e.ValueName)));
        if (removed > 0)
        {
            _view = null;
        }
    }

    /// <summary>
    /// Deletes every value of the key on the client: a <c>**delvals.</c> entry at the head of the key's entries (the values
    /// the policy sets follow it).
    /// </summary>
    public void ClearValues(string key)
    {
        var normalized = NormalizeKey(key);
        RemoveNames(normalized, DeleteAllValues);
        var entry = new RegistryPolEntry(normalized, DeleteAllValues, RegistryFormat.RegSz, RegistryPolEntry.DeletionData);
        var first = _entries.FindIndex(e => SameKey(e.Key, normalized));
        _entries.Insert(first >= 0 ? first : InsertionIndexForNewKey(normalized), entry);
        _view = null;
    }

    /// <summary>Removes one entry exactly (an extra registry setting the operator deletes).</summary>
    public bool Remove(RegistryPolEntry entry)
    {
        var index = _entries.IndexOf(entry);
        if (index < 0)
        {
            index = _entries.FindIndex(e => SameKey(e.Key, entry.Key) && e.ValueName.Equals(entry.ValueName, StringComparison.OrdinalIgnoreCase) && e.SameData(entry));
        }

        if (index < 0)
        {
            return false;
        }

        _entries.RemoveAt(index);
        _view = null;
        return true;
    }

    /// <summary>Path order of registry keys: per segment, without case (as the registry enumerates subkeys).</summary>
    public static int CompareKeys(string a, string b)
    {
        var left = NormalizeKey(a).Split('\\');
        var right = NormalizeKey(b).Split('\\');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var compared = string.Compare(left[i], right[i], StringComparison.OrdinalIgnoreCase);
            if (compared != 0)
            {
                return compared;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool SameKey(string entryKey, string normalized) => NormalizeKey(entryKey).Equals(normalized, StringComparison.OrdinalIgnoreCase);

    private static bool Keeps(IReadOnlyCollection<string> keep, string valueName)
    {
        var name = valueName.StartsWith(DeletePrefix, StringComparison.OrdinalIgnoreCase) ? valueName[DeletePrefix.Length..]
            : valueName.StartsWith(SoftPrefix, StringComparison.OrdinalIgnoreCase) ? valueName[SoftPrefix.Length..]
            : valueName;
        return keep.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes the entry of a raw value name: over its existing entry, else over the first entry of a name it replaces
    /// (<paramref name="replaces"/>: the deletion or the other form of the same value), else after the key's entries (a new
    /// key where it sorts). The other entries of these names are removed; the stored spelling of a replaced entry's key is
    /// kept.
    /// </summary>
    private void Put(string key, string rawName, string[] replaces, uint type, byte[] data)
    {
        bool Named(RegistryPolEntry e, string name) => SameKey(e.Key, key) && e.ValueName.Equals(name, StringComparison.OrdinalIgnoreCase);
        var index = _entries.FindIndex(e => Named(e, rawName));
        if (index < 0)
        {
            index = _entries.FindIndex(e => replaces.Any(r => Named(e, r)));
        }

        if (index >= 0)
        {
            var old = _entries[index];
            _entries[index] = new RegistryPolEntry(old.Key, old.ValueName.Equals(rawName, StringComparison.OrdinalIgnoreCase) ? old.ValueName : rawName, type, data);
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                if (i != index && (Named(_entries[i], rawName) || replaces.Any(r => Named(_entries[i], r))))
                {
                    _entries.RemoveAt(i);
                }
            }
        }
        else
        {
            var last = _entries.FindLastIndex(e => SameKey(e.Key, key));
            _entries.Insert(last >= 0 ? last + 1 : InsertionIndexForNewKey(key), new RegistryPolEntry(key, rawName, type, data));
        }

        _view = null;
    }

    private void RemoveNames(string key, params string[] rawNames)
    {
        if (_entries.RemoveAll(e => SameKey(e.Key, key) && rawNames.Contains(e.ValueName, StringComparer.OrdinalIgnoreCase)) > 0)
        {
            _view = null;
        }
    }

    /// <summary>Before the first entry whose key sorts after <paramref name="key"/> (the end when none does).</summary>
    private int InsertionIndexForNewKey(string key)
    {
        var index = _entries.FindIndex(e => CompareKeys(e.Key, key) > 0);
        return index >= 0 ? index : _entries.Count;
    }
}
