using System.Collections.Frozen;
using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Attribution;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// The attribution side's <see cref="IWatchedAttributeCatalog"/> (section 11.1 reduction) over the per-kind sets of
/// <see cref="WatchedAttributes"/>: an attribute is watched when it is in the effective set of any object kind
/// (<c>AdditionalWatchedAttributes</c> added, <c>IgnoredAttributes</c>, secret and noise attributes removed) or is a
/// group's <c>member</c>. Values
/// whose snapshot form is a hash or a security-descriptor summary (binary attributes, SDs, logon hours) are not
/// comparable with a 5136 <c>AttributeValue</c> and are never kept. The union is computed once per configuration
/// version.
/// </summary>
public sealed class WatchedAttributeCatalog : IWatchedAttributeCatalog
{
    /// <summary>Watched attributes whose 5136 value is never stored (not comparable with the snapshot form).</summary>
    public static IReadOnlySet<string> NotComparable { get; } = new[]
    {
        Attr.NtSecurityDescriptor, WatchdogAttributes.AllowedToActOnBehalfOfOtherIdentity, WatchdogAttributes.GroupMsaMembership,
        WatchdogAttributes.KeyCredentialLink, WatchdogAttributes.UserCertificate, WatchdogAttributes.CaCertificate, Attr.LogonHours,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly IWatchdogConfigProvider _configProvider;
    private readonly Lock _gate = new();
    private (DirectorySettings Settings, FrozenSet<string> Watched)? _cached;

    public WatchedAttributeCatalog(IWatchdogConfigProvider configProvider)
    {
        ArgumentNullException.ThrowIfNull(configProvider);
        _configProvider = configProvider;
    }

    /// <inheritdoc/>
    public bool IsWatched(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return !NotComparable.Contains(attribute) && Current().Contains(attribute);
    }

    private FrozenSet<string> Current()
    {
        var settings = _configProvider.Current.Configuration.Directory ?? new DirectorySettings();
        lock (_gate)
        {
            if (_cached is { } cached && ReferenceEquals(cached.Settings, settings))
            {
                return cached.Watched;
            }
        }

        // The change searches request identity + member (separate member table, 9.2 Group) + every kind's effective
        // set; the identity attributes are read with every object but never diffed.
        var watched = WatchedAttributes.RequestSet(settings)
            .Where(a => !WatchedAttributes.Identity.Contains(a, StringComparer.OrdinalIgnoreCase))
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            _cached = (settings, watched);
        }

        return watched;
    }
}
