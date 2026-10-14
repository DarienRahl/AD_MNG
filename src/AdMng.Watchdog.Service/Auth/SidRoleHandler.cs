using AdMng.Watchdog.Service.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>Grants <see cref="SidRoleRequirement"/> from the bootstrap role SIDs (<see cref="SidRoles"/>); a denied
/// SID fails the whole authorization, whatever other requirements say.</summary>
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

        if (SidRoles.Evaluate(context.User, authorization).Contains(requirement.Role))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
