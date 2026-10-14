using System.Security.Claims;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Service.Hosting;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>
/// Roles from SID claims (spec 17.3): denied when any user or group SID is in <c>Authorization.DeniedSids</c> (checked
/// first – emergency revocation while Kerberos tickets still carry the groups); Admin when a <see cref="ClaimTypes.GroupSid"/>
/// or <see cref="ClaimTypes.PrimaryGroupSid"/> is in <c>AdminSids</c>; Viewer when one is in <c>ViewerSids</c> or the
/// caller is Admin. SIDs compare case-insensitively; empty lists grant nothing.
/// </summary>
public static class SidRoles
{
    /// <summary>The roles of <paramref name="user"/> (empty when denied or not in any role group).</summary>
    public static IReadOnlyList<WatchdogRole> Evaluate(ClaimsPrincipal user, AuthorizationOptions authorization)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(authorization);
        if (user.Identity?.IsAuthenticated != true || IsDenied(user, authorization))
        {
            return [];
        }

        var groups = GroupSids(user).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var admin = authorization.AdminSids.Any(groups.Contains);
        var viewer = admin || authorization.ViewerSids.Any(groups.Contains);
        return admin ? [WatchdogRole.Viewer, WatchdogRole.Admin] : viewer ? [WatchdogRole.Viewer] : [];
    }

    /// <summary>True when any user/group SID claim is in <c>DeniedSids</c>.</summary>
    public static bool IsDenied(ClaimsPrincipal user, AuthorizationOptions authorization)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(authorization);
        if (authorization.DeniedSids.Length == 0)
        {
            return false;
        }

        var denied = authorization.DeniedSids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return user.Claims.Any(c => (c.Type is ClaimTypes.PrimarySid or ClaimTypes.Sid or ClaimTypes.GroupSid or ClaimTypes.PrimaryGroupSid)
                                    && denied.Contains(c.Value));
    }

    private static IEnumerable<string> GroupSids(ClaimsPrincipal user) =>
        user.Claims.Where(c => c.Type is ClaimTypes.GroupSid or ClaimTypes.PrimaryGroupSid).Select(c => c.Value);
}
