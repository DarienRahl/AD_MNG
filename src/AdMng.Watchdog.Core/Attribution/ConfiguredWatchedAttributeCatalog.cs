using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// Conservative <see cref="IWatchedAttributeCatalog"/> used until the detection side's per-class sets are wired in: the
/// attributes of the SAM field mapping (11.2) and the Tier-0 attributes (12.6) plus
/// <c>Directory.AdditionalWatchedAttributes</c>, minus <c>Directory.IgnoredAttributes</c>. Every one of them is in the
/// watched sets of 9.2, so no value of an unwatched attribute is ever stored; values whose canonical form is a hash or
/// a security-descriptor summary (binary attributes, SDs) are not comparable and never kept.
/// </summary>
public sealed class ConfiguredWatchedAttributeCatalog(IWatchdogConfigProvider configProvider) : IWatchedAttributeCatalog
{
    private static readonly HashSet<string> NotComparable = new(StringComparer.OrdinalIgnoreCase)
    {
        "nTSecurityDescriptor", "msDS-AllowedToActOnBehalfOfOtherIdentity", "msDS-GroupMSAMembership", "msDS-KeyCredentialLink",
        "userCertificate", "cACertificate", "logonHours",
    };

    private static readonly HashSet<string> Core = new(
        SecurityEventCatalog.SamFieldAttributes.Values.Concat(Tier0Catalog.Attributes), StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public bool IsWatched(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        if (NotComparable.Contains(attribute))
        {
            return false;
        }

        var directory = configProvider.Current.Configuration.Directory;
        if ((directory?.IgnoredAttributes ?? []).Contains(attribute, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return Core.Contains(attribute)
            || (directory?.AdditionalWatchedAttributes ?? []).Contains(attribute, StringComparer.OrdinalIgnoreCase);
    }
}
