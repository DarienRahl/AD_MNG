using System.Xml.Linq;

namespace AdMng.Core.GroupPolicy.Content;

/// <summary>
/// Group Policy Preferences files <c>Machine|User/Preferences/&lt;Type&gt;/&lt;Type&gt;.xml</c> ([MS-GPPREF] 2.2.1 and the
/// per-extension schemas 2.2.1.1 ff.). Every element with a <c>Properties</c> child or a <c>uid</c> attribute is an item –
/// also inside <c>Collection</c> elements – identified by its <c>uid</c> (display by <c>name</c>). Keys:
/// <c>&lt;Scope&gt;/Preferences/&lt;Type&gt;/&lt;uid&gt;/…</c>:
/// <list type="bullet">
/// <item><c>&lt;uid&gt;@element</c> (element name, e.g. <c>Group</c>, <c>ImmediateTaskV2</c>) and the item attributes
/// <c>&lt;uid&gt;@name</c>, <c>@disabled</c>, <c>@removePolicy</c>, <c>@userContext</c>, <c>@bypassErrors</c>, <c>@desc</c>
/// (<c>clsid</c>, <c>image</c>, <c>status</c>, <c>changed</c> are presentation/metadata and skipped);</item>
/// <item>every <c>Properties</c> attribute as <c>…/&lt;attribute&gt;</c>; a password-like attribute – <c>cpassword</c>
/// (MS14-025) – is masked: only its presence, plus the item's <c>&lt;uid&gt;@changed</c> time so that a new password stays
/// visible as an edit;</item>
/// <item>child elements of <c>Properties</c> flattened as <c>…/Path/To/Element</c> (text) and <c>…/Path@attribute</c>; a
/// container of same-named leaf elements (<c>Members</c>, or two or more equal names) is one sorted list;</item>
/// <item><c>Filters</c> as one sorted list of canonical filter elements.</item>
/// </list>
/// Categories: Groups → GPP groups/local users, ScheduledTasks → scheduled tasks, Services → services, Registry → by the
/// key path, Files/Folders/Shortcuts → system/startup folders when a path points there, otherwise preferences.
/// </summary>
public static class GppXmlParser
{
    private static readonly string[] ItemAttributes = ["name", "disabled", "removePolicy", "userContext", "bypassErrors", "desc"];

    public static void Parse(ReadOnlySpan<byte> content, GpoScope scope, string type, GpoSettingSink sink)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(sink);
        var document = GpoXml.Load(content);
        var prefix = (scope == GpoScope.User ? "User" : "Computer") + "/Preferences/" + type + "/";
        var index = 0;
        foreach (var item in document.Root!.Descendants().Where(IsItem))
        {
            index++;
            var uid = GpoXml.Attribute(item, "uid");
            var id = string.IsNullOrWhiteSpace(uid) ? item.Name.LocalName + "#" + GpoValues.Number(index) : uid.Trim();
            Item(sink, prefix + id + "/", type, item);
        }
    }

    private static bool IsItem(XElement element) =>
        GpoXml.Child(element, "Properties") is not null || GpoXml.Attribute(element, "uid") is not null;

    private static void Item(GpoSettingSink sink, string prefix, string type, XElement item)
    {
        var properties = GpoXml.Child(item, "Properties");
        var category = Category(type, properties);
        var registry = properties is not null && type.Equals("Registry", StringComparison.OrdinalIgnoreCase);
        // SNMP community names etc.: the value names themselves are secret (GpoSecretSettings "*").
        var hiddenNames = registry && GpoSecretSettings.HidesValueNames(RegistryPath(properties!));
        var registrySecret = registry && IsSecretRegistryItem(properties!);
        var itemKey = prefix[..^1] + "@";
        sink.Text(itemKey + "element", item.Name.LocalName, category);
        foreach (var name in ItemAttributes)
        {
            if (GpoXml.Attribute(item, name) is { } value)
            {
                if (hiddenNames && name == "name")
                {
                    sink.Masked(itemKey + name, category);
                }
                else
                {
                    sink.Text(itemKey + name, value, category);
                }
            }
        }

        var hasSecret = false;
        if (properties is not null)
        {
            foreach (var attribute in properties.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                var name = attribute.Name.LocalName;
                if (GpoSecretSettings.IsSecretName(name)
                    || (registrySecret && name.Equals("value", StringComparison.OrdinalIgnoreCase))
                    || (hiddenNames && name.Equals("name", StringComparison.OrdinalIgnoreCase)))
                {
                    if (attribute.Value.Length > 0)
                    {
                        sink.Masked(prefix + name, category);
                        hasSecret = true;
                    }
                }
                else
                {
                    sink.Text(prefix + name, attribute.Value, category);
                }
            }

            hasSecret |= Flatten(sink, prefix, properties, string.Empty, category);
        }

        if (hasSecret && GpoXml.Attribute(item, "changed") is { } changed)
        {
            sink.Text(itemKey + "changed", changed, category);
        }

        if (GpoXml.Child(item, "Filters") is { } filters)
        {
            sink.List(prefix + "Filters", filters.Elements().Select(Canonical), category);
        }
    }

    /// <summary>Flattens the children of <paramref name="element"/>; true when a masked value was found.</summary>
    private static bool Flatten(GpoSettingSink sink, string prefix, XElement element, string path, GpoSettingCategory category)
    {
        var hasSecret = false;
        var children = element.Elements().ToList();
        var counts = children.GroupBy(c => c.Name.LocalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in children)
        {
            var name = child.Name.LocalName;
            seen[name] = seen.GetValueOrDefault(name) + 1;
            var childPath = (path.Length == 0 ? string.Empty : path + "/") + name
                            + (counts[name] > 1 ? "[" + GpoValues.Number(seen[name]) + "]" : string.Empty);
            if (IsListContainer(child))
            {
                sink.List(prefix + childPath, child.Elements().Select(Canonical), category);
                continue;
            }

            foreach (var attribute in child.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                var key = prefix + childPath + "@" + attribute.Name.LocalName;
                if (GpoSecretSettings.IsSecretName(attribute.Name.LocalName))
                {
                    if (attribute.Value.Length > 0)
                    {
                        sink.Masked(key, category);
                        hasSecret = true;
                    }
                }
                else
                {
                    sink.Text(key, attribute.Value, category);
                }
            }

            if (!child.HasElements)
            {
                var text = child.Value;
                if (GpoSecretSettings.IsSecretName(name))
                {
                    if (text.Length > 0)
                    {
                        sink.Masked(prefix + childPath, category);
                        hasSecret = true;
                    }
                }
                else if (text.Trim().Length > 0)
                {
                    sink.Text(prefix + childPath, text, category);
                }
            }
            else
            {
                hasSecret |= Flatten(sink, prefix, child, childPath, category);
            }
        }

        return hasSecret;
    }

    /// <summary><c>Members</c>, or a container of two or more same-named leaf elements.</summary>
    private static bool IsListContainer(XElement element)
    {
        var children = element.Elements().ToList();
        if (children.Count == 0 || children.Any(c => c.HasElements)
            || children.Select(c => c.Name.LocalName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            return false;
        }

        return element.Name.LocalName.Equals("Members", StringComparison.OrdinalIgnoreCase) || children.Count > 1;
    }

    /// <summary>One element as text: <c>Name a=1 b=2 [children]</c> (attributes sorted, secrets as <c>***</c>).</summary>
    private static string Canonical(XElement element)
    {
        var parts = new List<string> { element.Name.LocalName };
        parts.AddRange(element.Attributes()
            .Where(a => !a.IsNamespaceDeclaration)
            .OrderBy(a => a.Name.LocalName, StringComparer.OrdinalIgnoreCase)
            .Select(a => a.Name.LocalName + "=" + (GpoSecretSettings.IsSecretName(a.Name.LocalName) && a.Value.Length > 0 ? "***" : a.Value)));
        if (element.HasElements)
        {
            parts.Add("[" + string.Join("; ", element.Elements().Select(Canonical)) + "]");
        }
        else if (element.Value.Trim().Length > 0 && !GpoSecretSettings.IsSecretName(element.Name.LocalName))
        {
            parts.Add(element.Value.Trim());
        }

        return string.Join(" ", parts);
    }

    private static GpoSettingCategory Category(string type, XElement? properties)
    {
        switch (type.ToUpperInvariant())
        {
            case "GROUPS":
                return GpoSettingCategory.PreferenceGroups;
            case "SCHEDULEDTASKS":
                return GpoSettingCategory.PreferenceScheduledTasks;
            case "SERVICES":
                return GpoSettingCategory.Services;
            case "REGISTRY":
                return properties is null ? GpoSettingCategory.Registry : GpoSettingCategories.ForRegistryKey(RegistryPath(properties));
            case "FILES":
            case "FOLDERS":
            case "SHORTCUTS":
                return properties is not null && properties.Attributes().Any(a => GpoSettingCategories.TargetsSystemOrStartupFolder(a.Value))
                    ? GpoSettingCategory.PreferenceSystemFiles
                    : GpoSettingCategory.Preferences;
            default:
                return GpoSettingCategory.Preferences;
        }
    }

    private static string RegistryPath(XElement properties) =>
        RegistryFormat.FromHiveName(GpoXml.Attribute(properties, "hive")) + "\\" + (GpoXml.Attribute(properties, "key") ?? string.Empty).Trim('\\');

    private static bool IsSecretRegistryItem(XElement properties) =>
        GpoSecretSettings.IsSecretRegistryValue(RegistryPath(properties), GpoXml.Attribute(properties, "name"));
}
