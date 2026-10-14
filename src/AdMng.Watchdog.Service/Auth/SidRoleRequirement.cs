using AdMng.Watchdog.Contracts.Api;
using Microsoft.AspNetCore.Authorization;

namespace AdMng.Watchdog.Service.Auth;

/// <summary>The caller must hold <see cref="Role"/> (evaluated by <see cref="SidRoleHandler"/>).</summary>
public sealed record SidRoleRequirement(WatchdogRole Role) : IAuthorizationRequirement;
