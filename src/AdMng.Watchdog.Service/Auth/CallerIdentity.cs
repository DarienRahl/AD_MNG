using System.Security.Claims;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>The API caller as the audit trail, acknowledgements and detail levels see it.</summary>
public static class CallerIdentity
{
    /// <summary>DOMAIN\name of the caller (never empty for an authenticated request).</summary>
    public static string Account(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return string.IsNullOrWhiteSpace(user.Identity?.Name) ? "unknown" : user.Identity.Name;
    }

    /// <summary>The user SID claim, when present.</summary>
    public static string? Sid(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.FindFirst(ClaimTypes.PrimarySid)?.Value ?? user.FindFirst(ClaimTypes.Sid)?.Value;
    }

    /// <summary>The caller as <see cref="PrincipalRef"/> (config audit, WatchdogConfigurationChanged actor).</summary>
    public static PrincipalRef Principal(ClaimsPrincipal user) => new() { Sid = Sid(user), Account = Account(user) };

    /// <summary>The detail level of API responses for the caller: Full for Admins, <c>ViewerDetailLevel</c> otherwise.</summary>
    public static DetailLevel DetailLevel(ClaimsPrincipal user, ServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SidRoles.Evaluate(user, options.Authorization).Contains(WatchdogRole.Admin)
            ? Contracts.Configuration.DetailLevel.Full
            : options.Authorization.ViewerDetailLevel;
    }

    /// <summary>True when the caller holds the Admin role.</summary>
    public static bool IsAdmin(ClaimsPrincipal user, ServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SidRoles.Evaluate(user, options.Authorization).Contains(WatchdogRole.Admin);
    }
}
