namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// Detection inputs from the bootstrap file (never editable through the API): the AD groups of the watchdog's own
/// roles and host (<c>Authorization.ViewerSids</c>, <c>AdminSids</c>, <c>WatchdogHostGroupSids</c>), which the
/// PrivilegeEvaluator treats as High-tier privileged roots (section 10.8 step 1). The service registers its instance;
/// the default has none.
/// </summary>
public sealed record DetectionHostOptions
{
    public IReadOnlyList<string> RoleGroupSids { get; init; } = [];

    /// <summary>The API resolves its callers' roles from <c>tokenGroups</c> (Linux, stage B design 6.2 step 3): the
    /// permission self-check then also probes that attribute on a non-protected user
    /// (<see cref="PermissionSelfCheck.TokenGroups"/>).</summary>
    public bool ProbeTokenGroups { get; init; }
}
