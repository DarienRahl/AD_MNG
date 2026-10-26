using System.Text.Json;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Security;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Diagnostics;
using AdMng.Watchdog.Core.Notifications;
using AdMng.Watchdog.Service.Directory;
using AdMng.Watchdog.Service.Hosting;
using Microsoft.Extensions.Options;

namespace AdMng.Watchdog.Service.Identity;

/// <summary>Where the monitor finds the keytab and the service's krb5.conf (the unit's <c>KRB5_KTNAME</c> /
/// <c>KRB5_CONFIG</c>; tests point them elsewhere).</summary>
public sealed record ServiceAccountPaths(string Keytab, string? Krb5Config)
{
    /// <summary>From the process environment (the systemd unit sets both).</summary>
    public static ServiceAccountPaths FromEnvironment() => new(
        StartupChecks.KeytabPath(Environment.GetEnvironmentVariable("KRB5_KTNAME")),
        Environment.GetEnvironmentVariable("KRB5_CONFIG") is { Length: > 0 } config ? config.Split(':')[0] : null);
}

/// <summary>
/// The self-checks of the service's own computer account (design 4.7) at start and hourly, over the current session
/// (rule 4): resolves <c>Watchdog:Kerberos:ServicePrincipal</c> by sAMAccountName, reads its flags, enctypes, SPNs,
/// <c>dNSHostName</c>, <c>pwdLastSet</c>, <c>msDS-KeyVersionNumber</c>, <c>tokenGroups</c> and DACL, the keytab (managed
/// reader, no keys kept) and the keytab tool's <c>status.json</c>, and reports the <c>serviceAccount.*</c> health checks,
/// the Status section „Konto usługi” and <c>host.primaryKdcNotPdc</c> (4.8). A check that turns Warning or Error raises
/// a HealthServiceAccount event once. A privileged account found by the first run stops the service with exit code 8
/// unless <c>Watchdog:AllowPrivilegedServiceAccount</c> is set (4.7 item 1). The account's objectGUID and SID are handed
/// to <see cref="WatchdogAccountSource"/>, which connects the detection's <c>watchdog-self</c> rule (9.3) with this monitor:
/// the monitor checks the account's state hourly, the rule reports every change of it as it happens.
/// </summary>
public sealed partial class ServiceAccountMonitor : IWatchdogJob
{
    /// <summary>Builtin groups that make the account privileged (design 4.7 item 1).</summary>
    private static readonly HashSet<string> PrivilegedBuiltinSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-32-544", "S-1-5-32-548", "S-1-5-32-549", "S-1-5-32-550", "S-1-5-32-551", "S-1-5-32-552",
    };

    /// <summary>Domain (and forest root) group RIDs: Domain Admins, DCs, Cert Publishers, Schema/Enterprise Admins, GPCO,
    /// RODCs, Key Admins, Enterprise Key Admins, Enterprise RODCs.</summary>
    private static readonly HashSet<uint> PrivilegedRids = [512, 516, 517, 518, 519, 520, 521, 526, 527, 498];

    private static readonly string[] AccountAttributes =
    [
        Attr.ObjectGuid, Attr.ObjectSid, Attr.UserAccountControl, Attr.SupportedEncryptionTypes, Attr.ServicePrincipalName, Attr.DnsHostName,
        Attr.PwdLastSet, "msDS-KeyVersionNumber",
    ];

    private readonly IDirectorySessionProvider _sessions;
    private readonly IOptionsMonitor<ServiceOptions> _options;
    private readonly IWatchdogConfigProvider _configuration;
    private readonly IHealthRegistry _health;
    private readonly IEventStore _events;
    private readonly IWatchdogSignals _signals;
    private readonly IProcessTerminator _terminator;
    private readonly ServiceAccountPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<ServiceAccountMonitor> _logger;
    private readonly WatchdogAccountSource? _accountSource;
    private readonly Dictionary<string, HealthState> _lastStates = new(StringComparer.Ordinal);
    private bool _firstRunDone;

    public ServiceAccountMonitor(
        IDirectorySessionProvider sessions,
        IOptionsMonitor<ServiceOptions> options,
        IWatchdogConfigProvider configuration,
        IHealthRegistry health,
        IEventStore events,
        IWatchdogSignals signals,
        IProcessTerminator terminator,
        ServiceAccountPaths paths,
        TimeProvider time,
        ILogger<ServiceAccountMonitor> logger,
        WatchdogAccountSource? accountSource = null)
    {
        _sessions = sessions;
        _options = options;
        _configuration = configuration;
        _health = health;
        _events = events;
        _signals = signals;
        _terminator = terminator;
        _paths = paths;
        _time = time;
        _logger = logger;
        _accountSource = accountSource;
    }

    public string Name => "service-account";

    public TimeSpan DefaultInterval => TimeSpan.FromHours(1);

    public IReadOnlyList<WatchdogSignal> WakeSignals => [];

    /// <summary>The latest evaluation (tests).</summary>
    public ServiceAccountEvaluation? Last { get; private set; }

    public async Task<JobResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var principal = _options.CurrentValue.Kerberos.ServicePrincipal;
        if (string.IsNullOrWhiteSpace(principal))
        {
            return new JobResult(null);
        }

        IDirectorySession session;
        try
        {
            session = await _sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException)
        {
            // The detection job reports the connection; retry sooner than hourly.
            return new JobResult(TimeSpan.FromMinutes(5), Failed: true, FailureCode: "directory");
        }

        ServiceAccountFacts facts;
        try
        {
            facts = await ReadFactsAsync(session, principal.Trim(), cancellationToken).ConfigureAwait(false);
            await CheckPrimaryKdcAsync(session, principal.Trim(), cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex)
        {
            if (ex.Kind is DirectoryErrorKind.ServerUnavailable or DirectoryErrorKind.Timeout)
            {
                _sessions.ReportFailure(session, ex);
            }

            SafeExceptionLog.LogWarning(_logger, ex, "service account check", new EventId(EventLogIds.ServiceAccountProblem));
            return new JobResult(TimeSpan.FromMinutes(5), Failed: true, FailureCode: "directory");
        }

        var evaluation = ServiceAccountChecks.Evaluate(facts, _time.GetUtcNow());
        Last = evaluation;
        foreach (var check in evaluation.Checks)
        {
            _health.Report(check);
        }

        _health.ReportServiceAccount(evaluation.Status);
        await RaiseEventsAsync(evaluation.Checks, cancellationToken).ConfigureAwait(false);

        var firstRun = !_firstRunDone;
        _firstRunDone = true;
        if (firstRun && facts.PrivilegedSids.Count > 0 && !_options.CurrentValue.AllowPrivilegedServiceAccount)
        {
            Log.PrivilegedAccount(_logger, facts.PrivilegedSids.Count, StartupExitCodes.PrivilegedServiceAccount);
            _terminator.Exit(StartupExitCodes.PrivilegedServiceAccount);
        }

        return new JobResult(null);
    }

    /// <summary>Reads everything the checks need; file problems become facts, directory errors propagate.</summary>
    internal async Task<ServiceAccountFacts> ReadFactsAsync(IDirectorySession session, string principal, CancellationToken cancellationToken)
    {
        var (keytab, keytabError) = ReadKeytab();
        var (status, statusError) = ReadStatus();
        var name = principal.Contains('@', StringComparison.Ordinal) ? principal[..principal.LastIndexOf('@')] : principal;
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, "computer"), LdapFilter.Equal(Attr.SamAccountName, name)),
            Attributes = AccountAttributes,
            SizeLimit = 2,
        };
        DirectoryObject? account = null;
        await foreach (var found in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            account = found;
            break;
        }

        if (account is null)
        {
            Log.AccountMissing(_logger, principal);
            return new ServiceAccountFacts { Principal = principal, AccountMissing = true, Keytab = keytab, KeytabError = keytabError, ToolStatus = status, ToolStatusError = statusError };
        }

        // The detection's watchdogAccount tag follows the account by GUID once known (design 9.3).
        _accountSource?.Update(account.ObjectGuid, account.ObjectSid);
        var groups = await ReadTokenGroupsAsync(session, account.DistinguishedName, cancellationToken).ConfigureAwait(false);
        var dacl = await ReadDaclAsync(session, account.DistinguishedName, cancellationToken).ConfigureAwait(false);
        var privileged = groups is null ? [] : await PrivilegedSidsAsync(session, groups, account.ObjectSid, cancellationToken).ConfigureAwait(false);
        return new ServiceAccountFacts
        {
            Principal = principal,
            AccountDn = account.DistinguishedName,
            UserAccountControl = account.GetInt32(Attr.UserAccountControl),
            SupportedEncryptionTypes = account.GetInt32(Attr.SupportedEncryptionTypes),
            ServicePrincipalNames = account.GetStrings(Attr.ServicePrincipalName),
            DnsHostName = account.GetString(Attr.DnsHostName),
            PasswordLastSet = account.GetFileTime(Attr.PwdLastSet),
            KeyVersionNumber = account.GetInt32("msDS-KeyVersionNumber"),
            TokenGroups = groups,
            PrivilegedSids = privileged,
            Dacl = dacl,
            Keytab = keytab,
            KeytabError = keytabError,
            ToolStatus = status,
            ToolStatusError = statusError,
        };
    }

    private (IReadOnlyList<KeytabEntry>? Entries, string? Error) ReadKeytab()
    {
        try
        {
            return (KeytabFile.Read(_paths.Keytab), null);
        }
        catch (FileNotFoundException)
        {
            return (null, "missing");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, "accessDenied");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return (null, ex is InvalidDataException ? "malformed" : "unreadable");
        }
    }

    private (KeytabToolStatus? Status, string? Error) ReadStatus()
    {
        try
        {
            return (KeytabToolStatus.Read(_options.CurrentValue.Kerberos.KeytabStatusFile), null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, "accessDenied");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return (null, ex is JsonException ? "malformed" : "unreadable");
        }
    }

    private static async Task<IReadOnlyList<string>?> ReadTokenGroupsAsync(IDirectorySession session, string dn, CancellationToken cancellationToken)
    {
        var entry = await session.GetAsync(dn, [Attr.TokenGroups], cancellationToken).ConfigureAwait(false);
        if (entry is null || !entry.TryGet(Attr.TokenGroups, out var values))
        {
            return null;
        }

        return [.. values.OfType<byte[]>().Where(b => b.Length >= 8).Select(b => AdConvert.SidToString(b))];
    }

    private static async Task<IReadOnlyList<SecurityDescriptorAce>?> ReadDaclAsync(IDirectorySession session, string dn, CancellationToken cancellationToken)
    {
        var entry = await session.GetAsync(dn, [Attr.NtSecurityDescriptor], cancellationToken).ConfigureAwait(false);
        return entry?.GetBytes(Attr.NtSecurityDescriptor) is { } bytes && SecurityDescriptorSummary.TryParse(bytes, out var summary) && summary is not null
            ? summary.Aces
            : null;
    }

    /// <summary>The privileged SIDs among the account's tokenGroups: builtin and domain groups of 4.7 item 1, DnsAdmins
    /// (by name) and the privileged roots of the runtime configuration.</summary>
    private async Task<IReadOnlyList<string>> PrivilegedSidsAsync(
        IDirectorySession session, IReadOnlyList<string> groups, string? accountSid, CancellationToken cancellationToken)
    {
        string? dnsAdmins = null;
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Filter = LdapFilter.And(LdapFilter.Equal(Attr.ObjectClass, "group"), LdapFilter.Equal(Attr.SamAccountName, "DnsAdmins")),
            Attributes = [Attr.ObjectSid],
            SizeLimit = 1,
        };
        await foreach (var group in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            dnsAdmins = group.ObjectSid;
            break;
        }

        var domainSid = accountSid is not null && accountSid.LastIndexOf('-') is > 0 and var dash ? accountSid[..dash] : null;
        var roots = new PrivilegedGroupSet(
            _configuration.Current.Configuration.PrivilegedGroups,
            new DirectoryFacts
            {
                DomainDn = session.Info.DefaultNamingContext,
                DomainDnsName = session.Info.DomainDnsName,
                ConfigurationDn = session.Info.ConfigurationNamingContext,
                SchemaDn = session.Info.SchemaNamingContext,
                DomainSid = domainSid,
            });
        return [.. groups.Where(sid => IsPrivileged(sid, dnsAdmins) || roots.TierOf(sid, null, null) is not null).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal static bool IsPrivileged(string sid, string? dnsAdminsSid)
    {
        if (PrivilegedBuiltinSids.Contains(sid) || string.Equals(sid, dnsAdminsSid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dash = sid.LastIndexOf('-');
        return sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(sid.AsSpan(dash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var rid)
            && PrivilegedRids.Contains(rid);
    }

    /// <summary>Design 4.8: <c>primary_kdc</c> of the service's krb5.conf must name the PDC emulator.</summary>
    private async Task CheckPrimaryKdcAsync(IDirectorySession session, string principal, CancellationToken cancellationToken)
    {
        if (_paths.Krb5Config is not { } path || !File.Exists(path))
        {
            return;
        }

        var realm = principal.Contains('@', StringComparison.Ordinal) ? principal[(principal.LastIndexOf('@') + 1)..] : _options.CurrentValue.Kerberos.Realm;
        string? primary;
        try
        {
            primary = string.IsNullOrWhiteSpace(realm) ? null : Krb5ConfigFile.PrimaryKdc(File.ReadLines(path), realm);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var pdc = await ServiceDirectorySessionProvider.FindPdcAsync(session, cancellationToken).ConfigureAwait(false);
        if (primary is null || pdc is null)
        {
            return;
        }

        var same = string.Equals(primary.TrimEnd('.'), pdc.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
        _health.Report(new HealthCheckDto
        {
            Code = HealthCheckCodes.HostPrimaryKdcNotPdc,
            State = same ? HealthState.Ok : HealthState.Warning,
            Detail = same ? "primary_kdc of krb5.conf is the PDC emulator." : "primary_kdc of krb5.conf is not the PDC emulator (update krb5.conf after a role move).",
            Args = [primary, pdc],
            CheckedAt = _time.GetUtcNow(),
        });
    }

    private async Task RaiseEventsAsync(IReadOnlyList<HealthCheckDto> checks, CancellationToken cancellationToken)
    {
        foreach (var check in checks)
        {
            var previous = _lastStates.TryGetValue(check.Code, out var state) ? state : HealthState.Ok;
            _lastStates[check.Code] = check.State;
            if (check.State is not (HealthState.Warning or HealthState.Error) || check.State <= previous)
            {
                continue;
            }

            Log.CheckFailed(_logger, check.Code, check.State);
            try
            {
                var details = new List<(string, string)> { ("code", check.Code), ("state", check.State.ToString()) };
                if (check.Args.Count > 0)
                {
                    details.Add(("args", string.Join(", ", check.Args)));
                }

                await _events.AppendAsync(HealthEvents.Create(WatchEventKind.HealthServiceAccount, _time.GetUtcNow(), details), cancellationToken).ConfigureAwait(false);
                _signals.Raise(WatchdogSignal.EventsStored);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SafeExceptionLog.LogError(_logger, ex, "health event append");
            }
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = EventLogIds.ServiceAccountProblem, Level = LogLevel.Critical,
            Message = "The service account is a member of {Count} privileged group(s); refusing to run (exit code {ExitCode}). Remove the memberships or set Watchdog:AllowPrivilegedServiceAccount.")]
        public static partial void PrivilegedAccount(ILogger logger, int count, int exitCode);

        [LoggerMessage(EventId = EventLogIds.ServiceAccountProblem, Level = LogLevel.Error, Message = "The service account {Principal} was not found in the directory.")]
        public static partial void AccountMissing(ILogger logger, string principal);

        [LoggerMessage(EventId = EventLogIds.ServiceAccountProblem, Level = LogLevel.Warning, Message = "Service account check {Code}: {State}.")]
        public static partial void CheckFailed(ILogger logger, string code, HealthState state);
    }
}
