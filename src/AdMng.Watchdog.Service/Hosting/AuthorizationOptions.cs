using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Bootstrap <c>Watchdog:Authorization</c>: roles by AD group SID (spec 8.4, 17.3).</summary>
public sealed record AuthorizationOptions
{
    public string[] ViewerSids { get; init; } = [];

    /// <summary>Admin implies Viewer.</summary>
    public string[] AdminSids { get; init; } = [];

    /// <summary>User or group SIDs denied before any grant (emergency revocation).</summary>
    public string[] DeniedSids { get; init; } = [];

    /// <summary>The gMSA host group; with the role groups treated as High-tier privileged roots. Obsolete on Linux
    /// (ignored with a start-up warning – stage B design 6.2).</summary>
    public string[] WatchdogHostGroupSids { get; init; } = [];

    /// <summary>Linux role resolution: lifetime of a positive cache entry (principal → objectSid + tokenGroups) in seconds
    /// (stage B design 6.2 step 2; negative entries live 60 s).</summary>
    public int RoleCacheSeconds { get; init; } = 300;

    /// <summary>Linux role resolution: how long after its expiry a cached entry may still be used while LDAP is unavailable
    /// (stage B design 6.2 step 5); without one the request gets 503 <c>role-lookup-unavailable</c>.</summary>
    public int RoleCacheMaxStaleSeconds { get; init; } = 3600;

    public DetailLevel ViewerDetailLevel { get; init; } = DetailLevel.Standard;

    public bool AllowTier0Suppression { get; init; }
}
