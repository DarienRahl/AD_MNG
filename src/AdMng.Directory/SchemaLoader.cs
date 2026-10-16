using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using Microsoft.Extensions.Logging;

namespace AdMng.Directory;

/// <summary>Streams the results of one search (the session's paged search).</summary>
internal delegate IAsyncEnumerable<DirectoryObject> DirectorySearchHandler(DirectorySearch search, CancellationToken cancellationToken);

/// <summary>
/// Loads attributeSchema / classSchema metadata and the display specifiers into a <see cref="SchemaCatalog"/>.
/// Only schema metadata is read (no directory content) and it is kept in memory for the session lifetime.
/// </summary>
internal static class SchemaLoader
{
    /// <summary>Display specifier containers loaded by default: locale id -> two letter language.</summary>
    public static IReadOnlyDictionary<int, string> DefaultLocales { get; } = new Dictionary<int, string>
    {
        [0x409] = "en",
        [0x415] = "pl",
    };

    /// <summary>
    /// Display specifier objects merged into the per-language map, in order of precedence (first wins).
    /// </summary>
    public static IReadOnlyList<string> DisplaySpecifierObjects { get; } =
    [
        "user-Display",
        "group-Display",
        "computer-Display",
        "contact-Display",
        "organizationalUnit-Display",
        "default-Display",
    ];

    private const string AttributeDisplayNames = "attributeDisplayNames";
    private const int SchemaPageSize = 1000;

    private static readonly string[] AttributeSchemaAttributes =
    [
        "lDAPDisplayName", "cn", "attributeID", "attributeSyntax", "oMSyntax", "isSingleValued", "rangeLower",
        "rangeUpper", "systemOnly", "systemFlags", "searchFlags", "isMemberOfPartialAttributeSet", "linkID",
        "schemaIDGUID", "attributeSecurityGUID",
    ];

    private static readonly string[] ClassSchemaAttributes =
    [
        "lDAPDisplayName", "cn", "governsID", "subClassOf", "objectClassCategory", "auxiliaryClass",
        "systemAuxiliaryClass", "mustContain", "systemMustContain", "mayContain", "systemMayContain", "possSuperiors",
        "systemPossSuperiors", "defaultObjectCategory", "schemaIDGUID",
    ];

    /// <summary>Loads attributes, classes and display names concurrently.</summary>
    /// <exception cref="DirectoryException">Attribute or class definitions could not be read.</exception>
    public static async Task<SchemaCatalog> LoadAsync(
        DirectorySearchHandler search,
        string schemaNamingContext,
        string configurationNamingContext,
        ILogger logger,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaNamingContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationNamingContext);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var started = timeProvider.GetTimestamp();
        var attributesTask = CollectAsync(
            search,
            SchemaSearch(schemaNamingContext, "attributeSchema", AttributeSchemaAttributes),
            ParseAttribute,
            cancellationToken);
        var classesTask = CollectAsync(
            search,
            SchemaSearch(schemaNamingContext, "classSchema", ClassSchemaAttributes),
            ParseClass,
            cancellationToken);
        var displayNamesTask = LoadDisplayNamesOrEmptyAsync();

        await Task.WhenAll(attributesTask, classesTask, displayNamesTask).ConfigureAwait(false);
        var catalog = new SchemaCatalog(attributesTask.Result, classesTask.Result, displayNamesTask.Result);
        Log.SchemaLoaded(
            logger,
            (long)timeProvider.GetElapsedTime(started).TotalMilliseconds,
            catalog.Attributes.Count,
            catalog.Classes.Count,
            displayNamesTask.Result.Count);
        return catalog;

        async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadDisplayNamesOrEmptyAsync()
        {
            try
            {
                return await LoadDisplayNamesAsync(search, configurationNamingContext, DefaultLocales, logger, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DirectoryException ex) when (!ex.IsTransient)
            {
                // Friendly names are optional - the UI falls back to LDAP names.
                Log.ConnectStepFailed(logger, "DisplaySpecifiers", ex.Kind, ex.LdapResultCode);
                return new Dictionary<string, IReadOnlyDictionary<string, string>>();
            }
        }
    }

    /// <summary>
    /// Loads <c>attributeDisplayNames</c> of the display specifier containers. A missing locale container
    /// (e.g. CN=415 on a DC installed without Polish display specifiers) yields no entry for that language.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadDisplayNamesAsync(
        DirectorySearchHandler search,
        string configurationNamingContext,
        IReadOnlyDictionary<int, string> locales,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(locales);
        var config = DistinguishedName.Parse(configurationNamingContext);
        var tasks = locales.Select(async locale =>
        {
            var containerDn = config
                .Child("CN", "DisplaySpecifiers")
                .Child("CN", locale.Key.ToString("X", CultureInfo.InvariantCulture))
                .ToString();
            var request = new DirectorySearch
            {
                BaseDn = containerDn,
                Scope = SearchScope.OneLevel,
                Filter = LdapFilter.Or(DisplaySpecifierObjects.Select(name => LdapFilter.Equal(Attr.Cn, name))),
                Attributes = [Attr.Cn, AttributeDisplayNames],
                PageSize = 100,
            };

            var objects = new List<DirectoryObject>();
            try
            {
                await foreach (var entry in search(request, cancellationToken).ConfigureAwait(false))
                {
                    objects.Add(entry);
                }
            }
            catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
            {
                Log.DisplaySpecifiersMissing(logger, locale.Value);
                return (Language: locale.Value, Names: (IReadOnlyDictionary<string, string>?)null);
            }

            var names = MergeDisplayNames(objects.Select(o => (o.GetString(Attr.Cn) ?? o.Name, o.GetStrings(AttributeDisplayNames))));
            return (Language: locale.Value, Names: names.Count == 0 ? null : names);
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var map = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (language, names) in results)
        {
            if (names is not null)
            {
                map[language] = names;
            }
        }

        return map;
    }

    /// <summary>
    /// Merges <c>attributeDisplayNames</c> values ("ldapName,Display Name") of several display specifier
    /// objects. Objects are applied in <see cref="DisplaySpecifierObjects"/> precedence; the first definition wins.
    /// </summary>
    public static IReadOnlyDictionary<string, string> MergeDisplayNames(IEnumerable<(string ObjectName, IReadOnlyList<string> Values)> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var ordered = objects
            .OrderBy(o => Precedence(o.ObjectName))
            .ThenBy(o => o.ObjectName, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, values) in ordered)
        {
            foreach (var value in values)
            {
                if (TryParseDisplayName(value, out var attribute, out var displayName))
                {
                    result.TryAdd(attribute, displayName);
                }
            }
        }

        return result;
    }

    /// <summary>Parses one <c>attributeDisplayNames</c> value: <c>ldapName,Display Name</c>.</summary>
    public static bool TryParseDisplayName(string? value, out string attribute, out string displayName)
    {
        attribute = string.Empty;
        displayName = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var comma = value.IndexOf(',', StringComparison.Ordinal);
        if (comma <= 0)
        {
            return false;
        }

        attribute = value[..comma].Trim();
        displayName = value[(comma + 1)..].Trim();
        return attribute.Length > 0 && displayName.Length > 0;
    }

    /// <summary>Maps one attributeSchema object. Returns null when it has no lDAPDisplayName.</summary>
    public static AttributeDefinition? ParseAttribute(DirectoryObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var ldapName = entry.GetString("lDAPDisplayName");
        if (string.IsNullOrWhiteSpace(ldapName))
        {
            return null;
        }

        var systemFlags = entry.GetInt32(Attr.SystemFlags) ?? 0;
        var searchFlags = entry.GetInt32("searchFlags") ?? 0;
        return new AttributeDefinition
        {
            LdapName = ldapName,
            Cn = entry.GetString(Attr.Cn),
            AttributeId = entry.GetString("attributeID"),
            Syntax = AttributeDefinition.MapSyntax(entry.GetString("attributeSyntax"), entry.GetInt32("oMSyntax") ?? 0),
            IsSingleValued = entry.GetBoolean("isSingleValued") ?? false,
            RangeLower = entry.GetInt64("rangeLower"),
            RangeUpper = entry.GetInt64("rangeUpper"),
            IsSystemOnly = entry.GetBoolean("systemOnly") ?? false,
            IsConstructed = (systemFlags & 0x4) != 0,
            IsIndexed = (searchFlags & 0x1) != 0,
            IsConfidential = (searchFlags & 0x80) != 0,
            IsInGlobalCatalog = entry.GetBoolean("isMemberOfPartialAttributeSet") ?? false,
            LinkId = entry.GetInt32("linkID"),
            SchemaIdGuid = ReadGuid(entry, "schemaIDGUID") ?? Guid.Empty,
            PropertySetGuid = ReadGuid(entry, "attributeSecurityGUID"),
        };
    }

    /// <summary>Maps one classSchema object. Returns null when it has no lDAPDisplayName.</summary>
    public static ClassDefinition? ParseClass(DirectoryObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var ldapName = entry.GetString("lDAPDisplayName");
        if (string.IsNullOrWhiteSpace(ldapName))
        {
            return null;
        }

        var category = entry.GetInt32("objectClassCategory") ?? 0;
        return new ClassDefinition
        {
            LdapName = ldapName,
            Cn = entry.GetString(Attr.Cn),
            GovernsId = entry.GetString("governsID"),
            SubClassOf = entry.GetString("subClassOf"),
            Category = Enum.IsDefined((ClassCategory)category) ? (ClassCategory)category : ClassCategory.Structural,
            AuxiliaryClasses = Union(entry, "auxiliaryClass", "systemAuxiliaryClass"),
            MustContain = Union(entry, "mustContain", "systemMustContain"),
            MayContain = Union(entry, "mayContain", "systemMayContain"),
            PossibleSuperiors = Union(entry, "possSuperiors", "systemPossSuperiors"),
            DefaultObjectCategory = entry.GetString("defaultObjectCategory"),
            SchemaIdGuid = ReadGuid(entry, "schemaIDGUID") ?? Guid.Empty,
        };
    }

    private static DirectorySearch SchemaSearch(string schemaNamingContext, string objectClass, string[] attributes) => new()
    {
        BaseDn = schemaNamingContext,
        Scope = SearchScope.OneLevel,
        Filter = LdapFilter.Equal(Attr.ObjectClass, objectClass),
        Attributes = attributes,
        PageSize = SchemaPageSize,
    };

    private static async Task<List<T>> CollectAsync<T>(
        DirectorySearchHandler search,
        DirectorySearch request,
        Func<DirectoryObject, T?> parse,
        CancellationToken cancellationToken)
        where T : class
    {
        var result = new List<T>();
        await foreach (var entry in search(request, cancellationToken).ConfigureAwait(false))
        {
            if (parse(entry) is { } item)
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static string[] Union(DirectoryObject entry, string first, string second) =>
        entry.GetStrings(first).Concat(entry.GetStrings(second)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static Guid? ReadGuid(DirectoryObject entry, string attribute) =>
        entry.GetBytes(attribute) is { Length: 16 } bytes ? AdConvert.GuidFromBytes(bytes) : null;

    private static int Precedence(string objectName)
    {
        for (var i = 0; i < DisplaySpecifierObjects.Count; i++)
        {
            if (string.Equals(DisplaySpecifierObjects[i], objectName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return DisplaySpecifierObjects.Count;
    }
}
