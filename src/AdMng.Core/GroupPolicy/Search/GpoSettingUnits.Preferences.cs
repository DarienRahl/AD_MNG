using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Core.GroupPolicy.Search;

/// <summary>Preference items: the natural identity, the collection state and the principals of 21.2.</summary>
public static partial class GpoSettingUnits
{
    /// <summary>
    /// The <c>Properties</c> attributes that name a preference item the same way in every GPO, by type (21.2). Alternatives
    /// are separated by <c>|</c> (the first present wins); parts of one identity by <c>+</c>.
    /// </summary>
    private static readonly Dictionary<string, string[]> IdentityByType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Registry"] = ["hive+key+name"],
        ["Services"] = ["serviceName"],
        ["Files"] = ["targetPath"],
        ["Folders"] = ["path"],
        ["Shortcuts"] = ["shortcutPath"],
        ["Printers"] = ["path", "localName", "ipAddress"],
        ["EnvironmentVariables"] = ["name+user"],
        ["ScheduledTasks"] = ["name"],
        ["IniFiles"] = ["path+section+property"],
        ["NetworkShares"] = ["name"],
    };

    /// <summary>One preference item; null for a <c>Collection</c> pseudo-item (structure, not a setting).</summary>
    private static GpoSettingUnit? PreferenceUnit(Group group, GpoUnitContext context, IReadOnlySet<string> truncatedItems)
    {
        var (first, info) = group.Members[0];
        var itemKey = info.ItemKey!;
        var attributes = new Dictionary<string, GpoSetting>(StringComparer.OrdinalIgnoreCase);
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        GpoSetting? filters = null;
        foreach (var (setting, member) in group.Members)
        {
            if (member.Property is not { } property)
            {
                continue;
            }

            if (property[0] == '@')
            {
                attributes.TryAdd(property, setting);
            }
            else if (property.Equals("Filters", StringComparison.OrdinalIgnoreCase))
            {
                filters = setting;
            }
            else if (!property.Contains('/', StringComparison.Ordinal) && setting.Kind is GpoSettingKind.Text or GpoSettingKind.Hash)
            {
                properties.TryAdd(property, setting.Value.Trim());
            }
        }

        var element = attributes.TryGetValue("@element", out var elementSetting) ? elementSetting.Value.Trim() : string.Empty;
        if (element.Equals("Collection", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var type = info.PreferenceType ?? string.Empty;
        var name = attributes.TryGetValue("@name", out var nameSetting) && nameSetting.Kind is GpoSettingKind.Text or GpoSettingKind.Hash ? nameSetting.Value.Trim() : null;
        var identity = Identity(type, element, properties) ?? name;
        var ambiguous = identity is null;
        identity ??= "(" + info.ItemId + ")";

        var scope = context.PreferenceScopes is { } scopes && scopes.TryGetValue(itemKey, out var found) ? found : null;
        var ownDisabled = attributes.TryGetValue("@disabled", out var disabledSetting) && disabledSetting.Value.Trim() == "1";
        var removePolicy = attributes.TryGetValue("@removePolicy", out var removeSetting) && removeSetting.Value.Trim() == "1";
        var ownFilters = filters is { Kind: GpoSettingKind.Text or GpoSettingKind.Hash, Value.Length: > 0 } ? filters.Value : null;
        var collectionFilters = scope?.CollectionFilters ?? [];
        var filterLines = collectionFilters.Select(f => "collection:" + f.Replace('\n', '\u001e'));
        if (ownFilters is not null)
        {
            filterLines = filterLines.Append("item:" + ownFilters.Replace(GpoSetting.ListSeparator, '\u001e'));
        }

        var settings = group.Settings;
        var comparability = ComparabilityOf(settings, GpoUnitComparability.Comparable);
        if (comparability == GpoUnitComparability.Comparable && truncatedItems.Contains(itemKey))
        {
            comparability = GpoUnitComparability.Truncated; // a key of the item was cut: its value is not complete
        }
        else if (comparability == GpoUnitComparability.Comparable && ambiguous)
        {
            comparability = GpoUnitComparability.Ambiguous;
        }
        else if (comparability == GpoUnitComparability.Comparable && (ownFilters is not null || collectionFilters.Count > 0))
        {
            comparability = GpoUnitComparability.Conditional;
        }

        var (principals, sids) = PreferencePrincipals(type, properties, group, collectionFilters, context);
        return new GpoSettingUnit
        {
            Id = string.Empty,
            BaseId = "I|" + Part(info.Scope) + "|" + type + "|" + element + "|" + identity,
            Kind = GpoSettingUnitKind.Preference,
            Scope = info.Scope,
            Area = GpoSettingArea.Preference,
            Source = first.Source,
            PreferenceType = type,
            ItemId = info.ItemId,
            Settings = settings,
            ValueFingerprint = Fingerprint(settings.Where(s => !IsChanged(s.Key, itemKey)), s => s.Key.Length > itemKey.Length ? s.Key[itemKey.Length..] : string.Empty),
            FiltersFingerprint = string.Join(LineSeparator, filterLines),
            Comparability = comparability,
            Disabled = ownDisabled || scope?.CollectionDisabled == true,
            RemovePolicy = removePolicy,
            Refusal = scope?.Scoped == true ? GpoUnitRefusal.ScopedCollection : null,
            Principals = principals,
            PrincipalSids = sids,
        };
    }

    /// <summary>The natural identity of an item by its type (21.2), null when the type has none or an attribute is missing.</summary>
    private static string? Identity(string type, string element, IReadOnlyDictionary<string, string> properties)
    {
        if (type.Equals("Drives", StringComparison.OrdinalIgnoreCase))
        {
            return properties.TryGetValue("useLetter", out var useLetter) && useLetter == "0"
                ? properties.GetValueOrDefault("path")
                : properties.GetValueOrDefault("letter");
        }

        if (type.Equals("Groups", StringComparison.OrdinalIgnoreCase))
        {
            if (element.Equals("User", StringComparison.OrdinalIgnoreCase))
            {
                return properties.GetValueOrDefault("userName");
            }

            return properties.TryGetValue("groupSid", out var sid) && sid.Length > 0 ? sid.ToUpperInvariant() : properties.GetValueOrDefault("groupName");
        }

        if (!IdentityByType.TryGetValue(type, out var alternatives))
        {
            return null;
        }

        foreach (var alternative in alternatives)
        {
            var parts = alternative.Split('+');
            if (parts.All(properties.ContainsKey))
            {
                return string.Join("|", parts.Select(p => properties[p]));
            }
        }

        return null;
    }

    /// <summary>GPP groups (<c>groupSid</c>/<c>groupName</c>, members' <c>sid=</c>/<c>name=</c>) and the targeting <c>sid=</c> of the item and its collections.</summary>
    private static (IReadOnlyList<string> Principals, IReadOnlyList<string> Sids) PreferencePrincipals(
        string type, IReadOnlyDictionary<string, string> properties, Group group, IReadOnlyList<string> collectionFilters, GpoUnitContext context)
    {
        var written = new List<string>();
        var sids = new List<string>();
        void Add(string? principal, string? sid)
        {
            var text = Written(principal);
            if (text.Length > 0)
            {
                written.Add(text);
            }

            var known = Written(sid) is { Length: > 0 } s ? s.ToUpperInvariant() : text.Length > 0 ? SidOf(text, context) : null;
            if (known is not null)
            {
                sids.Add(known);
            }
        }

        if (type.Equals("Groups", StringComparison.OrdinalIgnoreCase))
        {
            var groupSid = properties.GetValueOrDefault("groupSid");
            if (!string.IsNullOrEmpty(groupSid))
            {
                Add(groupSid, groupSid);
            }
            else if (properties.GetValueOrDefault("groupName") is { Length: > 0 } groupName)
            {
                Add(groupName, null);
            }

            foreach (var (setting, member) in group.Members)
            {
                if (member.Property is { } property && IsMembers(property) && setting.Kind == GpoSettingKind.Text)
                {
                    foreach (var element in setting.Elements())
                    {
                        var parsed = CanonicalAttributes(element);
                        var sid = parsed.GetValueOrDefault("sid");
                        Add(parsed.GetValueOrDefault("name") is { Length: > 0 } name ? name : sid, sid);
                    }
                }
            }
        }

        var filterTexts = group.Members.Where(m => string.Equals(m.Info.Property, "Filters", StringComparison.OrdinalIgnoreCase) && m.Setting.Kind == GpoSettingKind.Text)
            .Select(m => m.Setting.Value)
            .Concat(collectionFilters);
        foreach (var text in filterTexts)
        {
            foreach (System.Text.RegularExpressions.Match match in FilterSid().Matches(text))
            {
                Add(match.Groups[1].Value, match.Groups[1].Value);
            }
        }

        return (written.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), sids.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>The item's <c>@changed</c> (metadata that moves with every save; left out of the value).</summary>
    private static bool IsChanged(string key, string itemKey) =>
        key.Length == itemKey.Length + "@changed".Length && key.EndsWith("@changed", StringComparison.OrdinalIgnoreCase);
}
