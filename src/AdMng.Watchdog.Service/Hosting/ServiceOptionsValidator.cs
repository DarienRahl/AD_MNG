using System.Net;
using System.Text.RegularExpressions;
using AdMng.Core.Directory;
using AdMng.Watchdog.Contracts.Api;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Checks of the bootstrap settings (<c>--check-config</c>): listener URLs, certificate selection for https,
/// SID syntax of every role list, the shipped placeholder SID (stage B design 14.1), CIDR syntax of the subnet lists, the
/// Development scheme only in Development, the lab simple bind only with its user.</summary>
public static partial class ServiceOptionsValidator
{
    /// <summary>The role group SID of the shipped Linux <c>appsettings.json</c> (14.1): <c>--check-config</c> and the start
    /// fail until the administrator enters the real SIDs (appendix A.3 prints them).</summary>
    public const string PlaceholderSid = "S-1-5-21-0-0-0-0";

    /// <summary>The documented message of the placeholder check.</summary>
    public const string PlaceholderSidMessage =
        "Replace the role group SIDs: Watchdog:Authorization still holds the shipped placeholder " + PlaceholderSid + " (see the instructions, appendix A.3).";

    /// <summary>True when an Admin, Viewer or Denied list still holds <see cref="PlaceholderSid"/>.</summary>
    public static bool HasPlaceholderSids(ServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var authorization = options.Authorization ?? new AuthorizationOptions();
        return (authorization.AdminSids ?? []).Concat(authorization.ViewerSids ?? []).Concat(authorization.DeniedSids ?? [])
            .Any(sid => string.Equals(sid?.Trim(), PlaceholderSid, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<ConfigIssue> Validate(ServiceOptions options, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(options);
        var issues = new List<ConfigIssue>();
        IReadOnlyList<ListenerEndpoint> endpoints = [];
        try
        {
            endpoints = ListenerEndpoint.ParseAll(options.Hosting.Urls);
        }
        catch (FormatException)
        {
            issues.Add(Error("Watchdog:Hosting:Urls", "invalidUrl", "Hosting.Urls contains an invalid URL."));
        }

        if (endpoints.Count == 0 && issues.Count == 0)
        {
            issues.Add(Error("Watchdog:Hosting:Urls", "required", "Hosting.Urls lists no listener."));
        }

        if (endpoints.Any(e => !e.Https && !e.IsLoopback))
        {
            issues.Add(Error("Watchdog:Hosting:Urls", "insecureUrl", "Plain http is allowed only on loopback listeners."));
        }

        if (options.Hosting.Server == WatchdogServer.Kestrel && endpoints.Any(e => e.Https)
            && string.IsNullOrWhiteSpace(options.Hosting.Certificate.Path)
            && string.IsNullOrWhiteSpace(options.Hosting.Certificate.Thumbprint) && string.IsNullOrWhiteSpace(options.Hosting.Certificate.Subject))
        {
            issues.Add(Error("Watchdog:Hosting:Certificate", "required", "An https listener needs Hosting.Certificate.Path (PEM, Linux) or Thumbprint or Subject."));
        }

        if (HasPlaceholderSids(options))
        {
            issues.Add(Error("Watchdog:Authorization", "placeholderSid", PlaceholderSidMessage));
        }

        CheckSids(issues, "Watchdog:Authorization:ViewerSids", options.Authorization.ViewerSids);
        CheckSids(issues, "Watchdog:Authorization:AdminSids", options.Authorization.AdminSids);
        CheckSids(issues, "Watchdog:Authorization:DeniedSids", options.Authorization.DeniedSids);
        CheckSids(issues, "Watchdog:Authorization:WatchdogHostGroupSids", options.Authorization.WatchdogHostGroupSids);
        CheckSids(issues, "Watchdog:Development:Sids", options.Development.Sids);
        CheckSubnets(issues, "Watchdog:Hosting:AllowedRemoteSubnets", options.Hosting.AllowedRemoteSubnets);
        CheckSubnets(issues, "Watchdog:Hosting:MonitoringSubnets", options.Hosting.MonitoringSubnets);

        if (options.Authorization.ViewerSids.Length == 0 && options.Authorization.AdminSids.Length == 0)
        {
            issues.Add(Warning("Watchdog:Authorization", "noRoles", "No Viewer or Admin group is configured; every API call will be denied."));
        }

        if (options.Authentication == WatchdogAuthenticationMode.Development
            && !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error("Watchdog:Authentication", "developmentAuthentication", "Development authentication requires the Development environment."));
        }

        if (options.Authentication == WatchdogAuthenticationMode.Test)
        {
            issues.Add(Error("Watchdog:Authentication", "testAuthentication", "Test authentication exists only in the service tests."));
        }

        if (options.Directory.Authentication == AuthenticationMode.SimpleBind && string.IsNullOrWhiteSpace(options.Directory.LabSimpleBindUser))
        {
            issues.Add(Error("Watchdog:Directory:LabSimpleBindUser", "required", "Simple bind (lab only) needs Directory.LabSimpleBindUser."));
        }

        if (options.Directory.AllowInsecureLab && !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Error("Watchdog:Directory:AllowInsecureLab", "labOnly", "Directory.AllowInsecureLab is refused outside the Development environment."));
        }

        CheckDirectoryServers(issues, options.Directory, OperatingSystem.IsLinux());
        return issues;
    }

    /// <summary>Stage B design 5.1/5.3: Linux has no DC locator, so <c>Directory.Servers</c> (or <c>Server</c>) is required
    /// there, and Kerberos needs DC host names (<c>ldap/&lt;fqdn&gt;</c>) – IP literals are refused.</summary>
    internal static void CheckDirectoryServers(List<ConfigIssue> issues, DirectoryBootstrapOptions directory, bool linux)
    {
        if (directory.Disabled)
        {
            return;
        }

        var servers = (directory.Servers ?? []).Append(directory.Server).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
        if (linux && servers.Count == 0)
        {
            issues.Add(Error("Watchdog:Directory:Servers", "required", "Directory.Servers (DC host names) is required on Linux (there is no DC locator)."));
        }

        if (directory.Authentication == AuthenticationMode.CurrentUser && servers.Any(s => System.Net.IPAddress.TryParse(s.Trim('[', ']'), out _)))
        {
            issues.Add(Error("Watchdog:Directory:Servers", "ipLiteral", "Kerberos needs the DC host names (FQDN) in Directory.Servers, not IP addresses."));
        }
    }

    private static void CheckSids(List<ConfigIssue> issues, string path, IReadOnlyList<string> sids)
    {
        for (var i = 0; i < sids.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(sids[i]) || !SidPattern().IsMatch(sids[i].Trim()))
            {
                issues.Add(Error($"{path}:{i}", "invalidSid", "Not a SID in S-1-… form."));
            }
        }
    }

    private static void CheckSubnets(List<ConfigIssue> issues, string path, IReadOnlyList<string> subnets)
    {
        for (var i = 0; i < subnets.Count; i++)
        {
            var text = subnets[i]?.Trim() ?? string.Empty;
            if (!IPNetwork.TryParse(text, out _) && !IPAddress.TryParse(text, out _))
            {
                issues.Add(Error($"{path}:{i}", "invalidSubnet", "Not a CIDR subnet or IP address."));
            }
        }
    }

    private static ConfigIssue Error(string path, string code, string message) =>
        new() { Path = path, Code = code, Severity = IssueSeverity.Error, Message = message };

    private static ConfigIssue Warning(string path, string code, string message) =>
        new() { Path = path, Code = code, Severity = IssueSeverity.Warning, Message = message };

    [GeneratedRegex(@"^S-1-\d+(-\d+)+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SidPattern();
}
