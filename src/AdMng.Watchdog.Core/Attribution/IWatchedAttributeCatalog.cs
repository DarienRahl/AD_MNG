namespace AdMng.Watchdog.Core.Attribution;

/// <summary>
/// The effective watched attribute set (section 9.2 plus <c>AdditionalWatchedAttributes</c>, minus
/// <c>IgnoredAttributes</c>) as parse-time reduction needs it (11.1): a 5136 <c>AttributeValue</c> is stored only for a
/// watched, non-secret attribute. The detection side owns the per-class sets (<c>Detection/WatchedAttributes</c>);
/// <see cref="ConfiguredWatchedAttributeCatalog"/> is the conservative fallback.
/// </summary>
public interface IWatchedAttributeCatalog
{
    /// <summary>True when values of <paramref name="attribute"/> are watched (case-insensitive LDAP display name).</summary>
    bool IsWatched(string attribute);
}
