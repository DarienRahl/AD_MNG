using AdMng.Watchdog.Service.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>Grants <see cref="SidRoleRequirement"/> from the bootstrap role SIDs (<see cref="SidRoles"/>); a denied
/// SID fails the whole authorization, whatever other requirements say. A Linux caller whose SIDs could not be looked up
/// (<see cref="KerberosSidClaimsTransformation.UnavailableSource"/>) fails with <see cref="RoleLookupUnavailableReason"/>
/// (answered with 503, never a role).</summary>
public sealed class SidRoleHandler(IOptionsMonitor<ServiceOptions> options) : AuthorizationHandler<SidRoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SidRoleRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);
        var authorization = options.CurrentValue.Authorization;
        if (SidRoles.IsDenied(context.User, authorization))
        {
            context.Fail(new AuthorizationFailureReason(this, "A SID of the caller is in Authorization.DeniedSids."));
            return Task.CompletedTask;
        }

        if (context.User.HasClaim(KerberosSidClaimsTransformation.SidSourceClaim, KerberosSidClaimsTransformation.UnavailableSource))
        {
            context.Fail(new RoleLookupUnavailableReason(this));
            return Task.CompletedTask;
        }

        if (SidRoles.Evaluate(context.User, authorization).Contains(requirement.Role))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>The caller's SIDs could not be resolved (LDAP unavailable, nothing usable cached – stage B design 6.2 step 5).</summary>
public sealed class RoleLookupUnavailableReason(IAuthorizationHandler handler)
    : AuthorizationFailureReason(handler, "The caller's groups could not be looked up in the directory.");
