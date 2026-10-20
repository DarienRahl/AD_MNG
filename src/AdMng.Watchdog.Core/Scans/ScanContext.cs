using AdMng.Core.Directory;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>One iteration of the scheduled scans: session, resolved settings, clock and the stored privilege state.</summary>
internal sealed class ScanContext
{
    private readonly HashSet<string> _privileged;

    public ScanContext(IDirectorySession session, DetectionSettings settings, DcSnapshot dc, DateTimeOffset now, PrivilegeState? privileges,
        WatchdogAccountIdentity? watchdogAccount = null)
    {
        Session = session;
        Settings = settings;
        Dc = dc;
        Now = now;
        Privileges = privileges;
        _privileged = privileges?.Principals.Select(p => p.PrincipalKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, settings.TimeZone).DateTime);
        Tagging = new TaggingContext
        {
            IsPrivileged = IsPrivileged,
            IsPrivilegedRoot = s => s.Kind == WatchObjectKind.Group && settings.Privileged.TierOf(s.Sid, s.SamAccountName, s.Dn) is not null,
            Containers = dc.Containers,
            ServiceAccountSubtrees = settings.ServiceAccountSubtrees,
            DisabledAccountsSubtrees = settings.DisabledAccountsSubtrees,
            WatchdogAccount = watchdogAccount,
        };
    }

    public IDirectorySession Session { get; }

    public DetectionSettings Settings { get; }

    public WatchdogConfiguration Configuration => Settings.Configuration;

    public DcSnapshot Dc { get; }

    public DirectoryFacts Facts => Dc.Facts;

    public DateTimeOffset Now { get; }

    /// <summary>The local date of <see cref="Now"/> in the configured time zone.</summary>
    public DateOnly Today { get; }

    public TimeZoneInfo TimeZone => Settings.TimeZone;

    public PrivilegeState? Privileges { get; }

    public TaggingContext Tagging { get; }

    /// <summary>True when the member key is in the stored PrivilegeState.</summary>
    public bool IsPrivileged(string key) => _privileged.Contains(key);
}
