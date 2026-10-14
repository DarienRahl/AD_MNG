using AdMng.Watchdog.Contracts.Api;
using Microsoft.AspNetCore.Authorization;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>The authorization policies (spec 17.3): <see cref="Viewer"/>, <see cref="Admin"/>; the fallback policy is
/// Viewer, so every endpoint needs at least Viewer unless it is explicitly anonymous (<c>/healthz</c>).</summary>
public static class WatchdogPolicies
{
    public const string Viewer = "Viewer";
    public const string Admin = "Admin";

    public static void Configure(Microsoft.AspNetCore.Authorization.AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var viewer = Build(WatchdogRole.Viewer);
        options.AddPolicy(Viewer, viewer);
        options.AddPolicy(Admin, Build(WatchdogRole.Admin));
        options.FallbackPolicy = viewer;
    }

    private static AuthorizationPolicy Build(WatchdogRole role) =>
        new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new SidRoleRequirement(role)).Build();
}
