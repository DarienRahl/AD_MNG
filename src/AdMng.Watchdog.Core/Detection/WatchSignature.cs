using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Records in each snapshot which attributes it was built with when that differs from the kind's defaults
/// (<c>+attribute</c> watched in addition, <c>-attribute</c> not watched), under the reserved key
/// <see cref="Key"/>. When an operator starts watching an attribute (AdditionalWatchedAttributes, un-ignoring, adding a
/// critical object), the first read of an object fills the attribute silently instead of reporting every existing
/// value as "added" – otherwise the next reconciliation would raise an event for every object of the domain.
/// </summary>
internal static class WatchSignature
{
    /// <summary>Reserved snapshot attribute key (not an LDAP name: LDAP names never start with '$').</summary>
    public const string Key = "$watch";

    private const string Version = "v1";

    /// <summary>The signature values of an effective watched set.</summary>
    public static SnapshotAttribute Create(WatchObjectKind kind, IReadOnlySet<string> watched)
    {
        var defaults = new HashSet<string>(WatchedAttributes.Defaults(kind), StringComparer.OrdinalIgnoreCase);
        var values = new List<string> { Version };
        values.AddRange(watched.Where(a => !defaults.Contains(a)).Select(a => "+" + a).Order(StringComparer.OrdinalIgnoreCase));
        values.AddRange(defaults.Where(a => !watched.Contains(a)).Select(a => "-" + a).Order(StringComparer.OrdinalIgnoreCase));
        return new SnapshotAttribute { Values = values, Kind = AttributeValueKind.Text };
    }

    /// <summary>
    /// True when <paramref name="previous"/> was built with <paramref name="attribute"/> watched – an absent value
    /// then really means "no value". Snapshots without a signature were built with the defaults.
    /// </summary>
    public static bool WasWatched(SnapshotObject previous, string attribute)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (!previous.Attributes.TryGetValue(Key, out var signature))
        {
            return WatchedAttributes.Defaults(previous.Kind).Contains(attribute, StringComparer.OrdinalIgnoreCase);
        }

        if (signature.Values.Contains("+" + attribute, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return !signature.Values.Contains("-" + attribute, StringComparer.OrdinalIgnoreCase)
            && WatchedAttributes.Defaults(previous.Kind).Contains(attribute, StringComparer.OrdinalIgnoreCase);
    }
}
