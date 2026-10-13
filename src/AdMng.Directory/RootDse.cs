using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;

namespace AdMng.Directory;

/// <summary>The RootDSE facts needed to set up a session.</summary>
internal sealed record RootDse
{
    /// <summary>OID of the "Who am I?" extended operation (RFC 4532).</summary>
    public const string WhoAmIOid = "1.3.6.1.4.1.4203.1.11.3";

    public const string SupportedExtension = "supportedExtension";

    /// <summary>Attributes requested from the RootDSE.</summary>
    public static IReadOnlyList<string> Attributes { get; } =
    [
        Attr.DefaultNamingContext,
        Attr.ConfigurationNamingContext,
        Attr.SchemaNamingContext,
        Attr.RootDomainNamingContext,
        Attr.DnsHostNameRootDse,
        Attr.SupportedControl,
        SupportedExtension,
        Attr.DomainFunctionality,
        Attr.ForestFunctionality,
        Attr.IsGlobalCatalogReady,
    ];

    public required string DefaultNamingContext { get; init; }

    public required string ConfigurationNamingContext { get; init; }

    public required string SchemaNamingContext { get; init; }

    public string? RootDomainNamingContext { get; init; }

    public string? DnsHostName { get; init; }

    public IReadOnlySet<string> SupportedControls { get; init; } = new HashSet<string>();

    public IReadOnlySet<string> SupportedExtensions { get; init; } = new HashSet<string>();

    public int DomainFunctionality { get; init; }

    public int ForestFunctionality { get; init; }

    public bool IsGlobalCatalogReady { get; init; }

    /// <summary>Parses the RootDSE entry.</summary>
    /// <exception cref="DirectoryException">The server is not an Active Directory domain controller.</exception>
    public static RootDse Parse(DirectoryObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var defaultNc = entry.GetString(Attr.DefaultNamingContext);
        var configNc = entry.GetString(Attr.ConfigurationNamingContext);
        var schemaNc = entry.GetString(Attr.SchemaNamingContext);
        if (string.IsNullOrEmpty(defaultNc) || string.IsNullOrEmpty(configNc) || string.IsNullOrEmpty(schemaNc))
        {
            throw new DirectoryException(
                DirectoryErrorKind.UnwillingToPerform,
                "The server did not return the Active Directory naming contexts; it is not a domain controller.");
        }

        return new RootDse
        {
            DefaultNamingContext = defaultNc,
            ConfigurationNamingContext = configNc,
            SchemaNamingContext = schemaNc,
            RootDomainNamingContext = entry.GetString(Attr.RootDomainNamingContext),
            DnsHostName = entry.GetString(Attr.DnsHostNameRootDse),
            SupportedControls = new HashSet<string>(entry.GetStrings(Attr.SupportedControl), StringComparer.Ordinal),
            SupportedExtensions = new HashSet<string>(entry.GetStrings(SupportedExtension), StringComparer.Ordinal),
            DomainFunctionality = entry.GetInt32(Attr.DomainFunctionality) ?? 0,
            ForestFunctionality = entry.GetInt32(Attr.ForestFunctionality) ?? 0,
            IsGlobalCatalogReady = entry.GetBoolean(Attr.IsGlobalCatalogReady) ?? false,
        };
    }
}
