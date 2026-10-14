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

    /// <summary>The gMSA host group; with the role groups treated as High-tier privileged roots.</summary>
    public string[] WatchdogHostGroupSids { get; init; } = [];

    public DetailLevel ViewerDetailLevel { get; init; } = DetailLevel.Standard;

    public bool AllowTier0Suppression { get; init; }
}
