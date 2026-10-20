using System.Collections.Concurrent;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// In-memory cache of role lookups (stage B design 6.2 steps 2 and 5), keyed by the canonical principal
/// (<c>name@REALM</c>, name lower-case): <see cref="PrincipalLookupStatus.Found"/> entries live
/// <c>Authorization.RoleCacheSeconds</c> (default 300), <see cref="PrincipalLookupStatus.NotFound"/> entries
/// <see cref="NegativeLifetime"/>. While LDAP is unavailable an expired entry may still be used for
/// <c>Authorization.RoleCacheMaxStaleSeconds</c> (default 3600) after its expiry. Unavailable results are never stored.
/// The settings apply live (they are read at every store and lookup). SIDs only, held in memory only (CLAUDE.md rule 1).
/// </summary>
public sealed class RoleCache(IOptionsMonitor<ServiceOptions> options, TimeProvider timeProvider)
{
    /// <summary>Lifetime of a negative entry (no or several accounts with the name).</summary>
    public static readonly TimeSpan NegativeLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Entries kept at most; beyond it entries past their stale window are dropped, then the oldest.</summary>
    public const int MaxEntries = 10_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    /// <summary>The entry of <paramref name="key"/> while it is fresh, or – with <paramref name="allowStale"/> – up to
    /// <c>RoleCacheMaxStaleSeconds</c> after its expiry.</summary>
    public bool TryGet(string key, bool allowStale, out PrincipalLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(key);
        lookup = PrincipalLookup.Unavailable;
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        if (now < entry.ExpiresAt || (allowStale && now < entry.ExpiresAt + MaxStale()))
        {
            lookup = entry.Lookup;
            return true;
        }

        return false;
    }

    /// <summary>Stores a <see cref="PrincipalLookupStatus.Found"/> or <see cref="PrincipalLookupStatus.NotFound"/> result.</summary>
    public void Store(string key, PrincipalLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(lookup);
        if (lookup.Status == PrincipalLookupStatus.Unavailable)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var lifetime = lookup.Status == PrincipalLookupStatus.Found
            ? TimeSpan.FromSeconds(Math.Max(0, options.CurrentValue.Authorization.RoleCacheSeconds))
            : NegativeLifetime;
        _entries[key] = new Entry(lookup, now, now + lifetime);
        if (_entries.Count > MaxEntries)
        {
            Trim(now);
        }
    }

    /// <summary>Drops every entry (tests; a changed realm).</summary>
    public void Clear() => _entries.Clear();

    private TimeSpan MaxStale() => TimeSpan.FromSeconds(Math.Max(0, options.CurrentValue.Authorization.RoleCacheMaxStaleSeconds));

    private void Trim(DateTimeOffset now)
    {
        var maxStale = MaxStale();
        foreach (var (key, entry) in _entries)
        {
            if (now >= entry.ExpiresAt + maxStale)
            {
                _entries.TryRemove(key, out _);
            }
        }

        var excess = _entries.Count - MaxEntries;
        if (excess <= 0)
        {
            return;
        }

        foreach (var (key, _) in _entries.OrderBy(e => e.Value.StoredAt).Take(excess).ToList())
        {
            _entries.TryRemove(key, out _);
        }
    }

    private sealed record Entry(PrincipalLookup Lookup, DateTimeOffset StoredAt, DateTimeOffset ExpiresAt);
}
