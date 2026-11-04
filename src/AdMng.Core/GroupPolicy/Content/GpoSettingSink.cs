namespace AdMng.Core.GroupPolicy.Content;

/// <summary>
/// Collects the settings of one file for a parser: canonicalizes values and keys, applies the 200-character cap and the
/// masking, and stops at its capacity (<see cref="Overflowed"/>; the builder then treats the file as too large).
/// </summary>
public sealed class GpoSettingSink
{
    /// <summary>Default capacity: the most settings one GPO may hold (10 000, the Watchdog's limit as well).</summary>
    public const int DefaultCapacity = 10_000;

    private readonly List<GpoSetting> _settings = [];
    private readonly int _capacity;

    public GpoSettingSink(string source, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        _capacity = capacity;
    }

    /// <summary>The file the settings come from (relative path).</summary>
    public string Source { get; }

    public IReadOnlyList<GpoSetting> Settings => _settings;

    /// <summary>More settings were offered than the capacity allowed; the surplus was dropped.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>A text setting (capped; hashed when longer than 200 characters).</summary>
    public void Text(string key, string? value, GpoSettingCategory category)
    {
        var (text, kind) = GpoValues.Cap(value);
        Add(new GpoSetting { Key = GpoValues.Key(key), Value = text, Kind = kind, Category = category, Source = Source });
    }

    /// <summary>A value that is already a hash (<c>sha256:…</c>, script file content).</summary>
    public void Hash(string key, string hash, GpoSettingCategory category) =>
        Add(new GpoSetting { Key = GpoValues.Key(key), Value = hash, Kind = GpoSettingKind.Hash, Category = category, Source = Source });

    /// <summary>A secret: only its presence is kept.</summary>
    public void Masked(string key, GpoSettingCategory category) =>
        Add(new GpoSetting { Key = GpoValues.Key(key), Kind = GpoSettingKind.Masked, Category = category, Source = Source });

    /// <summary>
    /// An unordered list (user right SIDs, group members, filters): elements sanitized, de-duplicated (case-insensitive),
    /// sorted; an element longer than 200 characters becomes its hash; more than
    /// <see cref="GpoValues.MaxListElements"/> elements – one hash of the whole list (<see cref="GpoSettingKind.Hash"/>).
    /// </summary>
    public void List(string key, IEnumerable<string> elements, GpoSettingCategory category)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var items = elements
            .Select(e => GpoValues.Cap(e).Value)
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e, StringComparer.Ordinal)
            .ToList();
        var joined = string.Join(GpoSetting.ListSeparator, items);
        var setting = items.Count <= GpoValues.MaxListElements
            ? new GpoSetting { Key = GpoValues.Key(key), Value = joined, Kind = GpoSettingKind.Text, IsList = true, Category = category, Source = Source }
            : new GpoSetting { Key = GpoValues.Key(key), Value = GpoValues.Hash(joined), Kind = GpoSettingKind.Hash, Category = category, Source = Source };
        Add(setting);
    }

    private void Add(GpoSetting setting)
    {
        if (_settings.Count >= _capacity)
        {
            Overflowed = true;
            return;
        }

        _settings.Add(setting);
    }
}
