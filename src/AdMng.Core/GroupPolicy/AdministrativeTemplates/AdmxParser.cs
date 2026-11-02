using System.Globalization;
using System.Xml.Linq;

namespace AdMng.Core.GroupPolicy.AdministrativeTemplates;

/// <summary>
/// Parses ADMX files (the "Group Policy ADMX Syntax Reference Guide" schema: <c>policyDefinitions</c> with
/// <c>policyNamespaces</c>, <c>resources</c>, <c>supportedOn</c>, <c>categories</c> and <c>policies</c>). The content is
/// untrusted (SYSVOL, vendor packages): it is loaded through <see cref="GpoXml"/> (no DTD, no resolver, size and depth
/// limits) and any structural error fails the whole file with <see cref="GpoParseException"/> – as the Microsoft editor,
/// which refuses a file it cannot read. Element names are matched without case and without namespace.
/// </summary>
public static class AdmxParser
{
    /// <summary>Most policies of one file (the largest Windows file has about 1 500).</summary>
    public const int MaxPolicies = 20_000;

    /// <summary>Most categories of one file.</summary>
    public const int MaxCategories = 5_000;

    /// <summary>Most elements of one policy.</summary>
    public const int MaxElements = 256;

    /// <summary>Most items of one value list or enum.</summary>
    public const int MaxItems = 4_096;

    /// <summary>Longest name, key or value name accepted.</summary>
    public const int MaxNameLength = 1_024;

    public static AdmxFile Parse(string fileName, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var root = GpoXml.Load(content).Root!;
        if (!Is(root, "policyDefinitions"))
        {
            throw new GpoParseException("The file is not an ADMX file (policyDefinitions).");
        }

        var namespaces = Child(root, "policyNamespaces") ?? throw new GpoParseException("The ADMX file has no policyNamespaces.");
        var target = Child(namespaces, "target") ?? throw new GpoParseException("The ADMX file has no target namespace.");
        var targetPrefix = Required(target, "prefix");
        var targetNamespace = Required(target, "namespace");
        var prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [targetPrefix] = targetNamespace };
        foreach (var use in Children(namespaces, "using"))
        {
            // A using entry may repeat the target prefix (some vendor files do); the target wins.
            prefixes.TryAdd(Required(use, "prefix"), Required(use, "namespace"));
        }

        var resources = Child(root, "resources");
        var file = new AdmxFile
        {
            FileName = fileName,
            TargetPrefix = targetPrefix,
            TargetNamespace = targetNamespace,
            Prefixes = prefixes,
            Revision = GpoXml.Attribute(root, "revision"),
            MinRequiredRevision = resources is null ? null : GpoXml.Attribute(resources, "minRequiredRevision"),
            FallbackCulture = resources is null ? null : GpoXml.Attribute(resources, "fallbackCulture"),
            SupportedOn = SupportedOnDefinitions(root),
            Categories = Categories(root, targetNamespace),
        };
        return file with { Policies = Policies(root, targetNamespace) };
    }

    private static List<AdmxSupportedOnDefinition> SupportedOnDefinitions(XElement root)
    {
        var result = new List<AdmxSupportedOnDefinition>();
        if (Child(root, "supportedOn") is { } supportedOn && Child(supportedOn, "definitions") is { } definitions)
        {
            foreach (var definition in Children(definitions, "definition"))
            {
                result.Add(new AdmxSupportedOnDefinition(Required(definition, "name"), Required(definition, "displayName")));
            }
        }

        return result;
    }

    private static List<AdmxCategory> Categories(XElement root, string ns)
    {
        var result = new List<AdmxCategory>();
        if (Child(root, "categories") is not { } categories)
        {
            return result;
        }

        foreach (var category in Children(categories, "category"))
        {
            if (result.Count >= MaxCategories)
            {
                throw new GpoParseException("The ADMX file has too many categories.");
            }

            result.Add(new AdmxCategory(Required(category, "name"), ns, Required(category, "displayName"), GpoXml.Attribute(category, "explainText"),
                Child(category, "parentCategory") is { } parent ? Required(parent, "ref") : null));
        }

        return result;
    }

    private static List<AdmxPolicy> Policies(XElement root, string ns)
    {
        var result = new List<AdmxPolicy>();
        if (Child(root, "policies") is not { } policies)
        {
            return result;
        }

        foreach (var policy in Children(policies, "policy"))
        {
            if (result.Count >= MaxPolicies)
            {
                throw new GpoParseException("The ADMX file has too many policies.");
            }

            result.Add(Policy(policy, ns));
        }

        return result;
    }

    private static AdmxPolicy Policy(XElement policy, string ns)
    {
        var classText = Required(policy, "class");
        if (!Enum.TryParse<PolicyClass>(classText, ignoreCase: true, out var policyClass) || !Enum.IsDefined(policyClass))
        {
            throw new GpoParseException("A policy has an unknown class.");
        }

        var elements = new List<PolicyElement>();
        if (Child(policy, "elements") is { } container)
        {
            foreach (var element in container.Elements())
            {
                if (elements.Count >= MaxElements)
                {
                    throw new GpoParseException("A policy has too many elements.");
                }

                var parsed = Element(element);
                if (elements.Any(e => string.Equals(e.Id, parsed.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new GpoParseException("A policy has two elements with the same id.");
                }

                elements.Add(parsed);
            }
        }

        return new AdmxPolicy
        {
            Name = Required(policy, "name"),
            Namespace = ns,
            Class = policyClass,
            DisplayName = Required(policy, "displayName"),
            ExplainText = GpoXml.Attribute(policy, "explainText"),
            Presentation = PresentationId(GpoXml.Attribute(policy, "presentation")),
            Key = Key(Required(policy, "key")),
            ValueName = Optional(policy, "valueName"),
            ParentCategory = Child(policy, "parentCategory") is { } parent ? Required(parent, "ref") : null,
            SupportedOn = Child(policy, "supportedOn") is { } supported ? Required(supported, "ref") : null,
            EnabledValue = Child(policy, "enabledValue") is { } enabled ? Value(enabled) : null,
            DisabledValue = Child(policy, "disabledValue") is { } disabled ? Value(disabled) : null,
            EnabledList = Child(policy, "enabledList") is { } enabledList ? List(enabledList) : null,
            DisabledList = Child(policy, "disabledList") is { } disabledList ? List(disabledList) : null,
            Elements = elements,
            ClientExtension = ClientExtension(policy),
        };
    }

    private static PolicyElement Element(XElement element)
    {
        var id = Required(element, "id");
        var key = Optional(element, "key") is { } k ? Key(k) : null;
        var valueName = Optional(element, "valueName");
        var cse = ClientExtension(element);
        switch (element.Name.LocalName.ToUpperInvariant())
        {
            case "BOOLEAN":
                return new BooleanElement
                {
                    Id = id,
                    Key = key,
                    ValueName = RequireValueName(valueName),
                    ClientExtension = cse,
                    TrueValue = Child(element, "trueValue") is { } trueValue ? Value(trueValue) : null,
                    FalseValue = Child(element, "falseValue") is { } falseValue ? Value(falseValue) : null,
                    TrueList = Child(element, "trueList") is { } trueList ? List(trueList) : null,
                    FalseList = Child(element, "falseList") is { } falseList ? List(falseList) : null,
                };
            case "DECIMAL" or "LONGDECIMAL":
                var isLong = element.Name.LocalName.Equals("longDecimal", StringComparison.OrdinalIgnoreCase);
                var limit = isLong ? ulong.MaxValue : uint.MaxValue;
                var min = Number(GpoXml.Attribute(element, "minValue"), isLong) ?? 0;
                var max = Number(GpoXml.Attribute(element, "maxValue"), isLong) ?? Math.Min(DecimalElement.DefaultMaxValue, limit);
                if (min > max)
                {
                    throw new GpoParseException("A number element has minValue above maxValue.");
                }

                return new DecimalElement
                {
                    Id = id,
                    Key = key,
                    ValueName = RequireValueName(valueName),
                    ClientExtension = cse,
                    IsLong = isLong,
                    Required = Bool(element, "required"),
                    MinValue = min,
                    MaxValue = max,
                    StoreAsText = Bool(element, "storeAsText"),
                    Soft = Bool(element, "soft"),
                };
            case "TEXT":
                return new TextElement
                {
                    Id = id,
                    Key = key,
                    ValueName = RequireValueName(valueName),
                    ClientExtension = cse,
                    Required = Bool(element, "required"),
                    MaxLength = Length(element, "maxLength", TextElement.DefaultMaxLength),
                    Expandable = Bool(element, "expandable"),
                    Soft = Bool(element, "soft"),
                };
            case "MULTITEXT":
                return new MultiTextElement
                {
                    Id = id,
                    Key = key,
                    ValueName = RequireValueName(valueName),
                    ClientExtension = cse,
                    Required = Bool(element, "required"),
                    MaxLength = Length(element, "maxLength", TextElement.DefaultMaxLength),
                    MaxStrings = Length(element, "maxStrings", 0),
                    Soft = Bool(element, "soft"),
                };
            case "ENUM":
                var items = new List<EnumItem>();
                foreach (var item in Children(element, "item"))
                {
                    if (items.Count >= MaxItems)
                    {
                        throw new GpoParseException("An enum has too many items.");
                    }

                    items.Add(new EnumItem(Required(item, "displayName"),
                        Child(item, "value") is { } value ? Value(value) : throw new GpoParseException("An enum item has no value."),
                        Child(item, "valueList") is { } valueList ? List(valueList) : null));
                }

                return new EnumElement
                {
                    Id = id,
                    Key = key,
                    ValueName = RequireValueName(valueName),
                    ClientExtension = cse,
                    Required = Bool(element, "required"),
                    Soft = Bool(element, "soft"),
                    Items = items,
                };
            case "LIST":
                return new ListElement
                {
                    Id = id,
                    Key = key,
                    ValueName = null,
                    ClientExtension = cse,
                    ValuePrefix = GpoXml.Attribute(element, "valuePrefix"),
                    Additive = Bool(element, "additive"),
                    Expandable = Bool(element, "expandable"),
                    ExplicitValue = Bool(element, "explicitValue"),
                };
            default:
                throw new GpoParseException("A policy has an unknown element type.");
        }
    }

    /// <summary>The value of <c>enabledValue</c>, <c>trueValue</c>, an item's <c>value</c> … (its single child).</summary>
    private static PolicyValue Value(XElement container)
    {
        var child = container.Elements().FirstOrDefault() ?? throw new GpoParseException("A value is empty.");
        return child.Name.LocalName.ToUpperInvariant() switch
        {
            "DECIMAL" => PolicyValue.Decimal((uint)(Number(Required(child, "value"), isLong: false) ?? 0)),
            "LONGDECIMAL" => PolicyValue.LongDecimal(Number(Required(child, "value"), isLong: true) ?? 0),
            "STRING" => PolicyValue.String(child.Value),
            "DELETE" => PolicyValue.Delete,
            _ => throw new GpoParseException("A value has an unknown type."),
        };
    }

    private static PolicyValueList List(XElement list)
    {
        var items = new List<PolicyValueItem>();
        foreach (var item in Children(list, "item"))
        {
            if (items.Count >= MaxItems)
            {
                throw new GpoParseException("A value list has too many items.");
            }

            items.Add(new PolicyValueItem(Optional(item, "key") is { } key ? Key(key) : null, RequireValueName(Optional(item, "valueName")),
                Child(item, "value") is { } value ? Value(value) : throw new GpoParseException("A value list item has no value.")));
        }

        return new PolicyValueList(Optional(list, "defaultKey") is { } defaultKey ? Key(defaultKey) : null, items);
    }

    /// <summary>
    /// An unsigned number; a negative one within the signed range is taken as its two's complement (some vendor files
    /// write <c>-1</c> for 0xFFFFFFFF).
    /// </summary>
    private static ulong? Number(string? text, bool isLong)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (isLong)
        {
            if (ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var unsignedLong))
            {
                return unsignedLong;
            }

            if (long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var signedLong))
            {
                return unchecked((ulong)signedLong);
            }
        }
        else
        {
            if (uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var unsigned))
            {
                return unsigned;
            }

            if (int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var signed))
            {
                return unchecked((uint)signed);
            }
        }

        throw new GpoParseException("A number is not valid.");
    }

    private static int Length(XElement element, string attribute, int fallback)
    {
        var text = GpoXml.Attribute(element, attribute);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new GpoParseException("A length is not valid.");
    }

    private static bool Bool(XElement element, string attribute) => GpoXml.Attribute(element, attribute)?.Trim().ToUpperInvariant() switch
    {
        null or "" or "FALSE" or "0" => false,
        "TRUE" or "1" => true,
        _ => throw new GpoParseException("A boolean attribute is not valid."),
    };

    private static Guid? ClientExtension(XElement element) => GpoXml.Attribute(element, "clientExtension") is not { Length: > 0 } text
        ? null
        : Guid.TryParse(text.Trim().Trim('{', '}'), out var guid)
            ? guid
            : throw new GpoParseException("A client extension is not a GUID.");

    /// <summary><c>$(presentation.id)</c> → id.</summary>
    private static string? PresentationId(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        const string Prefix = "$(presentation.";
        var trimmed = reference.Trim();
        return trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith(')')
            ? trimmed[Prefix.Length..^1]
            : trimmed;
    }

    /// <summary>A registry key as written (separators trimmed); empty or over-long keys fail the file.</summary>
    private static string Key(string key)
    {
        var trimmed = key.Trim().Trim('\\');
        return trimmed.Length is 0 or > MaxNameLength ? throw new GpoParseException("A registry key is not valid.") : trimmed;
    }

    private static string RequireValueName(string? valueName) =>
        valueName ?? throw new GpoParseException("A value name is missing.");

    private static string Required(XElement element, string attribute)
    {
        var value = GpoXml.Attribute(element, attribute);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new GpoParseException("A required attribute is missing.");
        }

        return value.Length > MaxNameLength ? throw new GpoParseException("An attribute is too long.") : value.Trim();
    }

    /// <summary>An attribute that may be missing (empty kept: an empty value name is the key's default value).</summary>
    private static string? Optional(XElement element, string attribute)
    {
        var value = GpoXml.Attribute(element, attribute);
        return value is { Length: > MaxNameLength } ? throw new GpoParseException("An attribute is too long.") : value;
    }

    internal static bool Is(XElement element, string localName) => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase);

    internal static XElement? Child(XElement element, string localName) => GpoXml.Child(element, localName);

    internal static IEnumerable<XElement> Children(XElement element, string localName) => element.Elements().Where(e => Is(e, localName));
}
