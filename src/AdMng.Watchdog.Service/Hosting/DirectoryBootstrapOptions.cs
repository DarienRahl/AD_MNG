using AdMng.Core.Directory;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Bootstrap <c>Watchdog:Directory</c>: how the service reaches AD (spec 8.4).</summary>
public sealed record DirectoryBootstrapOptions
{
    /// <summary>Null = the computer's domain.</summary>
    public string? Domain { get; init; }

    /// <summary>Null = locate a DC (the PDC emulator with <see cref="PreferPdc"/>).</summary>
    public string? Server { get; init; }

    /// <summary>Linux (required there – no DC locator): ordered DC host names (FQDN, never IP literals). The first reachable
    /// one is used; with <see cref="PreferPdc"/> the service reconnects to the PDC emulator when it is listed; failover
    /// after <c>Directory.FailoverAfter</c> to the next (stage B design 5.3).</summary>
    public string[] Servers { get; init; } = [];

    public bool PreferPdc { get; init; } = true;

    public AuthenticationMode Authentication { get; init; } = AuthenticationMode.CurrentUser;

    public TransportSecurity Security { get; init; } = TransportSecurity.SignAndSeal;

    public int? Port { get; init; }

    /// <summary>Lab only: simple-bind user; the password comes ONLY from env <c>ADMNG_WATCHDOG_LDAP_PASSWORD</c>.</summary>
    public string? LabSimpleBindUser { get; init; }

    /// <summary>Lab only (Development): allow simple bind over an unprotected channel.</summary>
    public bool AllowInsecureLab { get; init; }

    /// <summary>Disables directory monitoring (smoke test, API-only development runs).</summary>
    public bool Disabled { get; init; }
}
