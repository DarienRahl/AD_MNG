using AdMng.Core.GroupPolicy.Content;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Gpo;

/// <summary>Result of <see cref="GpoSettingsDiffer.Diff"/>.</summary>
public sealed record GpoSettingsDiff
{
    /// <summary>"Was → is" rows, sensitive categories first, at most <see cref="GpoSettingsDiffer.MaxChanges"/> including the
    /// summary row of a truncated diff.</summary>
    public IReadOnlyList<AttributeChange> Changes { get; init; } = [];

    public int Added { get; init; }
    public int Removed { get; init; }
    public int Modified { get; init; }

    public int Total => Added + Removed + Modified;

    /// <summary>More rows than <see cref="GpoSettingsDiffer.MaxChanges"/>: the last row is the summary.</summary>
    public bool Truncated { get; init; }

    /// <summary>A changed setting (on either side) is in a sensitive category (<see cref="GpoSettingCategories"/>).</summary>
    public bool SensitiveChanged { get; init; }

    public IReadOnlySet<GpoSettingCategory> ChangedCategories { get; init; } = new HashSet<GpoSettingCategory>();

    /// <summary>The new settings carry a GPP <c>cpassword</c> (tag <c>gppPassword</c>, MS14-025).</summary>
    public bool GppPasswordPresent { get; init; }

    public bool IsEmpty => Total == 0;
}

/// <summary>
/// Stage B design 8.6: compares two setting lists and produces <see cref="AttributeChange"/> rows – <c>Attribute</c> = the
/// setting key, <c>OldValues</c>/<c>NewValues</c> one canonical value each (empty on the added/removed side),
/// <c>ValueKind</c> Text or Hash, <c>Redacted</c> for masked settings (only presence: <c>OldCount</c>/<c>NewCount</c> 0 or 1).
/// List settings (<see cref="GpoSetting.IsList"/>) carry their elements in <c>OldValues</c>/<c>NewValues</c> and the
/// differences in <c>Added</c>/<c>Removed</c>. An unreadable file shows as <c>File/&lt;path&gt;</c> with the value
/// <see cref="UnreadableValue"/> („nie udało się odczytać”, <c>Value.Unreadable</c>). At most 200 rows per event: when more
/// settings changed, 199 rows (sensitive first, then by key) and a summary row <see cref="SummaryAttribute"/> with
/// <c>Truncated</c>, <c>OldCount</c> = rows left out and <c>NewCount</c> = all changed settings.
/// </summary>
public static class GpoSettingsDiffer
{
    public const int MaxChanges = 200;

    /// <summary>Canonical value of an unreadable file's marker in a row (formatters show <c>Value.Unreadable</c>).</summary>
    public const string UnreadableValue = "unreadable";

    /// <summary>Attribute of the summary row of a truncated diff.</summary>
    public const string SummaryAttribute = "gpo.moreChanges";

    public static GpoSettingsDiff Diff(IReadOnlyList<GpoSetting> oldSettings, IReadOnlyList<GpoSetting> newSettings)
    {
        ArgumentNullException.ThrowIfNull(oldSettings);
        ArgumentNullException.ThrowIfNull(newSettings);
        var before = Index(oldSettings);
        var after = Index(newSettings);
        var rows = new List<(GpoSettingCategory Category, AttributeChange Change)>();
        int added = 0, removed = 0, modified = 0;
        foreach (var (key, setting) in after)
        {
            if (!before.TryGetValue(key, out var old))
            {
                added++;
                rows.Add((setting.Category, Row(null, setting)));
            }
            else if (!Same(old, setting))
            {
                modified++;
                rows.Add((setting.Category, Row(old, setting)));
            }
        }

        foreach (var (key, setting) in before)
        {
            if (!after.ContainsKey(key))
            {
                removed++;
                rows.Add((setting.Category, Row(setting, null)));
            }
        }

        var ordered = rows
            .OrderBy(r => GpoSettingCategories.IsSensitive(r.Category) ? 0 : 1)
            .ThenBy(r => r.Change.Attribute, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Change.Attribute, StringComparer.Ordinal)
            .ToList();
        var truncated = ordered.Count > MaxChanges;
        var changes = ordered.Take(truncated ? MaxChanges - 1 : MaxChanges).Select(r => r.Change).ToList();
        if (truncated)
        {
            changes.Add(new AttributeChange
            {
                Attribute = SummaryAttribute,
                ValueKind = AttributeValueKind.Number,
                Truncated = true,
                OldCount = ordered.Count - (MaxChanges - 1),
                NewCount = ordered.Count,
            });
        }

        return new GpoSettingsDiff
        {
            Changes = changes,
            Added = added,
            Removed = removed,
            Modified = modified,
            Truncated = truncated,
            SensitiveChanged = rows.Any(r => GpoSettingCategories.IsSensitive(r.Category)),
            ChangedCategories = rows.Select(r => r.Category).ToHashSet(),
            GppPasswordPresent = HasGppPassword(newSettings),
        };
    }

    /// <summary>True when a GPP item carries a <c>cpassword</c> (masked setting <c>…/Preferences/…/cpassword</c>).</summary>
    public static bool HasGppPassword(IEnumerable<GpoSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Any(s => s.Kind == GpoSettingKind.Masked
                                 && s.Key.EndsWith("/" + GpoSecretSettings.CPassword, StringComparison.OrdinalIgnoreCase)
                                 && s.Key.Contains("/Preferences/", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, GpoSetting> Index(IReadOnlyList<GpoSetting> settings)
    {
        var index = new Dictionary<string, GpoSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            index[setting.Key] = setting;
        }

        return index;
    }

    /// <summary>Equal settings: same kind and value (masked: presence only; lists: same elements).</summary>
    private static bool Same(GpoSetting a, GpoSetting b) =>
        a.Kind == b.Kind && a.IsList == b.IsList && (a.Kind == GpoSettingKind.Masked || string.Equals(a.Value, b.Value, StringComparison.Ordinal));

    private static AttributeChange Row(GpoSetting? old, GpoSetting? @new)
    {
        var key = (@new ?? old)!.Key;
        if (old?.Kind == GpoSettingKind.Masked || @new?.Kind == GpoSettingKind.Masked)
        {
            return new AttributeChange
            {
                Attribute = key,
                ValueKind = AttributeValueKind.Text,
                Redacted = true,
                OldCount = old is null ? 0 : 1,
                NewCount = @new is null ? 0 : 1,
            };
        }

        var oldValues = Values(old);
        var newValues = Values(@new);
        var list = (old?.IsList ?? true) && (@new?.IsList ?? true) && (old ?? @new)!.IsList;
        return new AttributeChange
        {
            Attribute = key,
            ValueKind = old?.Kind == GpoSettingKind.Hash || @new?.Kind == GpoSettingKind.Hash ? AttributeValueKind.Hash : AttributeValueKind.Text,
            OldValues = oldValues,
            NewValues = newValues,
            Added = list ? newValues.Except(oldValues, StringComparer.OrdinalIgnoreCase).ToList() : [],
            Removed = list ? oldValues.Except(newValues, StringComparer.OrdinalIgnoreCase).ToList() : [],
            OldCount = oldValues.Count,
            NewCount = newValues.Count,
        };
    }

    private static IReadOnlyList<string> Values(GpoSetting? setting) => setting switch
    {
        null => [],
        { Kind: GpoSettingKind.Unreadable } => [UnreadableValue],
        { IsList: true, Kind: GpoSettingKind.Text } => setting.Elements(),
        _ => [setting.Value],
    };
}
