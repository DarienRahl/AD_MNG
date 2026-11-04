using System.Net.Sockets;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Core.EventLogs.Analysis;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Computers;
using AdMng.Presentation.EventLogs;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Readiness;

/// <summary>
/// Remote actions (K1) on the test computer – its name resolves, a CIM session opens with Kerberos and reads the OS – and
/// the event logs (E): per DC the RPC session, the Security log, how far back it reaches and the audit evidence probes of the
/// lockout and failed-logon analyses; the test computer's System log. Everything through the existing K1 and E services.
/// </summary>
public sealed partial class ReadinessChecker
{
    /// <summary>The window the audit probes look back at most (the analyses' lockout investigations reach a week).</summary>
    public static readonly TimeSpan AuditWindow = TimeSpan.FromDays(7);

    /// <summary>A Security log that reaches less far back than this is reported.</summary>
    public static readonly TimeSpan RecommendedCoverage = TimeSpan.FromDays(7);

    private const string SecurityChannel = "Security";
    private const string SystemChannel = "System";

    private static readonly string[] PerDomainControllerCodes =
    [
        ReadinessCodes.EventLogsSession, ReadinessCodes.EventLogsSecurity, ReadinessCodes.EventLogsCoverage,
        ReadinessCodes.EventLogsAuditLockout, ReadinessCodes.EventLogsAuditFailedLogons,
    ];

    // ---- Remote actions ---------------------------------------------------------------------------------------------------------

    private async Task RemoteActionsAsync(RunContext context, List<ReadinessResult> rows, CancellationToken cancellationToken)
    {
        var test = await TestComputerAsync(context, cancellationToken).ConfigureAwait(false);
        if (test.Target is not { } target)
        {
            rows.Add(Row(ReadinessCodes.RemoteNameResolves, test.Status, test.Reason ?? string.Empty, test.Remedy));
            rows.Add(test.Status == ReadinessStatus.NotChecked
                ? NotChecked(ReadinessCodes.RemoteCimSession, test.Reason ?? string.Empty, test.Remedy)
                : NotChecked(ReadinessCodes.RemoteCimSession, Localizer[L.Readiness.TargetSkipped]));
            return;
        }

        var host = target.DnsHostName;
        await AddAsync(rows, ReadinessCodes.RemoteNameResolves, () => NameResolvesAsync(host, cancellationToken), host).ConfigureAwait(false);
        await AddAsync(rows, ReadinessCodes.RemoteCimSession, () => CimSessionAsync(context.Session, target, cancellationToken), host).ConfigureAwait(false);
    }

    private async Task<ReadinessResult> NameResolvesAsync(string host, CancellationToken cancellationToken)
    {
        if (_services.Resolver is not { } resolver)
        {
            return NotChecked(ReadinessCodes.RemoteNameResolves, Localizer[L.Readiness.NotAvailable], host: host);
        }

        IReadOnlyList<System.Net.IPAddress> addresses;
        try
        {
            addresses = await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or RemoteActionException or ArgumentException)
        {
            addresses = [];
        }

        return addresses.Count > 0
            ? Ok(ReadinessCodes.RemoteNameResolves, Localizer.Format(L.Readiness.NameResolves, host, string.Join(", ", addresses.Take(3))), host)
            : Row(ReadinessCodes.RemoteNameResolves, ReadinessStatus.Error, Localizer.Format(L.Readiness.NameNotResolved, host), Localizer[L.Readiness.RemedyNameNotResolved], host);
    }

    /// <summary>A CIM session with the window's credential (Kerberos) that reads the operating system, as "Zdalne akcje" does.</summary>
    private async Task<ReadinessResult> CimSessionAsync(IDirectorySession session, RemoteTarget target, CancellationToken cancellationToken)
    {
        var host = target.DnsHostName;
        if (_services.Remote is not { } remote)
        {
            return NotChecked(ReadinessCodes.RemoteCimSession, Localizer[L.Readiness.NotAvailable], host: host);
        }

        try
        {
            var remoteSession = await remote.ConnectAsync(target, session, _services.Ui.Settings.Current.Computers.RemoteOptions(), cancellationToken).ConfigureAwait(false);
            try
            {
                var os = await remoteSession.GetOperatingSystemAsync(cancellationToken).ConfigureAwait(false);
                var system = string.Join(" ", new[] { os.Caption, os.FullBuild }.Where(s => !string.IsNullOrWhiteSpace(s)));
                return Ok(ReadinessCodes.RemoteCimSession, Localizer.Format(L.Readiness.CimOk, host, system.Length == 0 ? "Windows" : system), host);
            }
            finally
            {
                await remoteSession.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (RemoteActionException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Row(ReadinessCodes.RemoteCimSession, ReadinessStatus.Error, Localizer[RemoteText.ErrorKey(ex.Kind)], Localizer[RemoteText.HintKey(ex.Kind)], host,
                ex.Kind == RemoteErrorKind.WinRmNotAvailable ? L.Readiness.DocComputersWinRm : L.Readiness.DocComputersRemote);
        }
    }

    /// <summary>The test computer of the request (once per run): a DN from the picker or a typed name, its DNS name from AD.</summary>
    private async Task<TestComputer> TestComputerAsync(RunContext context, CancellationToken cancellationToken)
    {
        if (context.TestComputer is { } known)
        {
            return known;
        }

        var text = context.Request.TestComputer?.Trim();
        TestComputer result;
        if (string.IsNullOrEmpty(text))
        {
            result = new TestComputer(null, Localizer[L.Readiness.NotCheckedNoTestComputer], Localizer[L.Readiness.RemedyChooseTestComputer], ReadinessStatus.NotChecked);
        }
        else if (await FindComputerAsync(context.Session, text, cancellationToken).ConfigureAwait(false) is not { } computer)
        {
            result = new TestComputer(null, Localizer.Format(L.Readiness.TargetNotFound, text), Localizer[L.Readiness.RemedyTargetNotFound], ReadinessStatus.Error);
        }
        else
        {
            var facts = ComputerFacts.From(computer);
            result = HostNames.IsValid(facts.DnsHostName)
                ? new TestComputer(new RemoteTarget(facts.DistinguishedName, facts.Name, facts.DnsHostName!, facts.Type), null, null, ReadinessStatus.Ok)
                : new TestComputer(null, Localizer.Format(L.Readiness.TargetNoDns, facts.Name), Localizer[L.Readiness.RemedyTargetNoDns], ReadinessStatus.Error);
        }

        context.TestComputer = result;
        return result;
    }

    /// <summary>The computer object of a DN, or of a name (dNSHostName, sAMAccountName or cn); null when there is none.</summary>
    private static async Task<DirectoryObject?> FindComputerAsync(IDirectorySession session, string text, CancellationToken cancellationToken)
    {
        var attributes = ComputerFacts.Attributes(session.Schema);
        if (text.Contains('=', StringComparison.Ordinal) && DistinguishedName.TryParse(text, out var dn) && !dn.IsRoot)
        {
            try
            {
                var byDn = await session.GetAsync(dn.ToString(), attributes, cancellationToken).ConfigureAwait(false);
                return byDn?.Kind == ObjectKind.Computer ? byDn : null;
            }
            catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.NoSuchObject or DirectoryErrorKind.InvalidSyntax)
            {
                return null;
            }
        }

        var name = text.TrimEnd('.').TrimEnd('$');
        if (name.Length is 0 or > 255)
        {
            return null;
        }

        var shortName = name.Split('.')[0];
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = LdapFilter.And([
                LdapFilter.Equal(Attr.ObjectClass, "computer"),
                LdapFilter.Or([LdapFilter.Equal(Attr.DnsHostName, name), LdapFilter.Equal(Attr.SamAccountName, shortName + "$"), LdapFilter.Equal(Attr.Cn, shortName)]),
            ]),
            Attributes = attributes,
            SizeLimit = 2,
        };
        DirectoryObject? first = null;
        await foreach (var found in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(found.GetString(Attr.DnsHostName)?.TrimEnd('.'), name, StringComparison.OrdinalIgnoreCase))
            {
                return found;
            }

            first ??= found;
        }

        return first;
    }

    // ---- Event logs ---------------------------------------------------------------------------------------------------------------

    private async Task EventLogsAsync(RunContext context, List<ReadinessResult> rows, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var unavailable = _services.EventLogs is not { } events
            ? Localizer[L.Readiness.NotAvailable]
            : events.NotSupportedReason(session);
        if (unavailable is not null || _services.EventLogs?.Connector is not { } connector)
        {
            var remedy = session.Info.Authentication == AuthenticationMode.SimpleBind ? Localizer[L.Readiness.RemedyWindowsLogon] : null;
            rows.AddRange(ReadinessCodes.Of(ReadinessArea.EventLogs).Select(code => NotChecked(code, unavailable ?? Localizer[L.Readiness.NotAvailable], remedy)));
            return;
        }

        var controllers = await _services.EventLogs.ReadDomainControllersAsync(session, cancellationToken).ConfigureAwait(false);
        foreach (var controller in controllers)
        {
            var dcRows = new List<ReadinessResult>();
            try
            {
                await DomainControllerLogAsync(connector, session, controller.Host, dcRows, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                dcRows.AddRange(PerDomainControllerCodes.Where(code => dcRows.All(r => r.Code != code)).Select(code => Failed(code, ex, controller.Host)));
            }

            rows.AddRange(dcRows);
        }

        await AddAsync(rows, ReadinessCodes.EventLogsTestComputer, () => TestComputerLogAsync(context, connector, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>One DC: open its log, read the Security coverage, probe the audit events of the analyses.</summary>
    private async Task DomainControllerLogAsync(IEventLogConnector connector, IDirectorySession session, string host, List<ReadinessResult> rows, CancellationToken cancellationToken)
    {
        IEventLogReader reader;
        try
        {
            reader = await connector.OpenComputerAsync(host, session, cancellationToken).ConfigureAwait(false);
        }
        catch (EventLogAccessException ex)
        {
            rows.Add(AccessError(ReadinessCodes.EventLogsSession, ex, host));
            rows.AddRange(PerDomainControllerCodes.Skip(1).Select(code => NotChecked(code, Localizer.Format(L.Readiness.EventLogNotOpened, host), host: host)));
            return;
        }

        try
        {
            rows.Add(Ok(ReadinessCodes.EventLogsSession, Localizer.Format(L.Readiness.EventLogSessionOk, host), host));
            EventLogCoverage coverage;
            try
            {
                coverage = await reader.GetCoverageAsync(SecurityChannel, cancellationToken).ConfigureAwait(false);
            }
            catch (EventLogAccessException ex)
            {
                rows.Add(AccessError(ReadinessCodes.EventLogsSecurity, ex, host));
                rows.AddRange(PerDomainControllerCodes.Skip(2).Select(code => NotChecked(code, Localizer.Format(L.Readiness.SecurityNotRead, host), host: host)));
                return;
            }

            rows.Add(coverage.IsEnabled
                ? Ok(ReadinessCodes.EventLogsSecurity, Localizer.Format(L.Readiness.SecurityReadable, host), host)
                : Row(ReadinessCodes.EventLogsSecurity, ReadinessStatus.Error, Localizer.Format(L.Readiness.SecurityDisabled, host), Localizer[L.Readiness.RemedyCoverage], host,
                    L.Readiness.DocWatchdogAudit));
            rows.Add(Coverage(coverage, host));
            await AuditAsync(reader, coverage, host, rows, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The row of a log that could not be opened or read: the access error, its Windows code and its remedy (EventLogText).</summary>
    private ReadinessResult AccessError(string code, EventLogAccessException exception, string host)
    {
        var text = EventLogText.AccessErrorShort(Localizer, exception.Error);
        if (exception.NativeErrorCode is { } native)
        {
            text = Localizer.Format(L.EventLogs.WithErrorCode, text, Number(native));
        }

        return Row(code, ReadinessStatus.Error, text, Localizer[EventLogText.RemedyKey(exception.Error)], host);
    }

    /// <summary>How far back the Security log reaches ("sięga 3 dni wstecz"), its size and mode; short or stopped logs are reported.</summary>
    private ReadinessResult Coverage(EventLogCoverage coverage, string host)
    {
        var now = _services.Time.GetUtcNow();
        if (coverage.Oldest is not { } oldest)
        {
            return NotChecked(ReadinessCodes.EventLogsCoverage, Localizer.Format(L.Readiness.CoverageUnknown, host), host: host);
        }

        var reach = now - oldest;
        var text = Localizer.Format(L.Readiness.CoverageReach, host, EventLogText.Duration(Localizer, reach), EventLogText.Moment(Localizer, oldest, _services.Time.LocalTimeZone));
        if (coverage.FileSizeBytes is { } size && coverage.MaximumSizeBytes is { } maximum)
        {
            text += " " + Localizer.Format(L.Readiness.CoverageSize, EventLogText.Megabytes(Localizer, size), EventLogText.Megabytes(Localizer, maximum),
                EventLogText.Mode(Localizer, coverage.Mode));
        }

        var remedy = Localizer[L.Readiness.RemedyCoverage];
        if (coverage.StoppedBefore(now))
        {
            return Row(ReadinessCodes.EventLogsCoverage, ReadinessStatus.Error, text + " " + Localizer[L.Readiness.CoverageStopped], remedy, host);
        }

        if (reach >= RecommendedCoverage)
        {
            return Ok(ReadinessCodes.EventLogsCoverage, text, host);
        }

        var shortText = text + " " + Localizer.Format(L.Readiness.CoverageShort, Number((long)RecommendedCoverage.TotalDays));
        return Row(ReadinessCodes.EventLogsCoverage, reach >= TimeSpan.FromDays(1) ? ReadinessStatus.Warning : ReadinessStatus.Error, shortText, remedy, host);
    }

    /// <summary>
    /// The audit evidence of the lockout and failed-logon analyses on one DC: one unfiltered probe per required event
    /// (<see cref="AuditEvidence.Probes"/>, at most one record each) over the part of the last week the log covers.
    /// </summary>
    private async Task AuditAsync(IEventLogReader reader, EventLogCoverage coverage, string host, List<ReadinessResult> rows, CancellationToken cancellationToken)
    {
        var now = _services.Time.GetUtcNow();
        var from = coverage.Oldest is { } oldest && oldest > now - AuditWindow && oldest <= now ? oldest : now - AuditWindow;
        var requirements = AuditEvidence.LockoutRequirements.Concat(AuditEvidence.FailedLogonRequirements).Distinct().ToList();
        var records = new List<EventLogRecord>();
        var reads = new List<AuditRead>();
        foreach (var probe in AuditEvidence.Probes(requirements, [], [coverage], from, now, [host]))
        {
            var complete = true;
            foreach (var query in probe.Queries)
            {
                try
                {
                    await foreach (var record in reader.ReadAsync(query, cancellationToken).ConfigureAwait(false))
                    {
                        records.Add(record);
                        break;
                    }
                }
                catch (EventLogAccessException)
                {
                    complete = false;
                    break;
                }
            }

            if (complete)
            {
                reads.Add(probe.Completed);
            }
        }

        rows.Add(Audit(ReadinessCodes.EventLogsAuditLockout, AuditEvidence.LockoutRequirements, records, coverage, from, now, reads, host));
        rows.Add(Audit(ReadinessCodes.EventLogsAuditFailedLogons, AuditEvidence.FailedLogonRequirements, records, coverage, from, now, reads, host));
    }

    private ReadinessResult Audit(string code, IReadOnlyList<AuditRequirement> requirements, List<EventLogRecord> records, EventLogCoverage coverage,
        DateTimeOffset from, DateTimeOffset to, List<AuditRead> reads, string host)
    {
        var entries = AuditEvidence.Evaluate(requirements, records, [coverage], from, to, reads, [host]);
        if (entries.All(e => e.State == AuditEvidenceState.Seen))
        {
            return Ok(code, Localizer.Format(L.Readiness.AuditSeen, host, string.Join(", ", requirements.Select(r => Number(r.EventId)))), host);
        }

        var missing = entries.Where(e => e.State == AuditEvidenceState.NotSeenWhileCovered).Select(e => RequirementText(e.Requirement)).ToList();
        return missing.Count > 0
            ? Row(code, ReadinessStatus.Warning, Localizer.Format(L.Readiness.AuditMissing, host, string.Join(", ", missing), EventLogText.Duration(Localizer, to - from)),
                Localizer[L.Readiness.RemedyAudit], host)
            : NotChecked(code, Localizer.Format(L.Readiness.AuditUnknown, host), host: host);
    }

    /// <summary>"4740 (Zarządzanie kontami użytkowników, Inspekcja sukcesów)".</summary>
    private string RequirementText(AuditRequirement requirement) => Localizer.Format(L.Readiness.AuditRequirement,
        Number(requirement.EventId), Localizer[SecuritySettingsText.AuditSubcategoryKey(requirement.Subcategory.Id)], EventLogText.Outcome(Localizer, requirement.Outcome));

    /// <summary>The System log of the test computer (opened like the reader opens a computer).</summary>
    private async Task<ReadinessResult> TestComputerLogAsync(RunContext context, IEventLogConnector connector, CancellationToken cancellationToken)
    {
        var test = await TestComputerAsync(context, cancellationToken).ConfigureAwait(false);
        if (test.Target is not { } target)
        {
            return test.Status == ReadinessStatus.NotChecked
                ? NotChecked(ReadinessCodes.EventLogsTestComputer, test.Reason ?? string.Empty, test.Remedy)
                : NotChecked(ReadinessCodes.EventLogsTestComputer, Localizer[L.Readiness.TargetSkipped]);
        }

        var host = target.DnsHostName;
        IEventLogReader reader;
        try
        {
            reader = await connector.OpenComputerAsync(host, context.Session, cancellationToken).ConfigureAwait(false);
        }
        catch (EventLogAccessException ex)
        {
            return AccessError(ReadinessCodes.EventLogsTestComputer, ex, host);
        }

        try
        {
            var coverage = await reader.GetCoverageAsync(SystemChannel, cancellationToken).ConfigureAwait(false);
            if (!coverage.IsEnabled)
            {
                return Row(ReadinessCodes.EventLogsTestComputer, ReadinessStatus.Warning, Localizer.Format(L.Readiness.TestComputerLogDisabled, host), host: host);
            }

            return coverage.Oldest is { } oldest
                ? Ok(ReadinessCodes.EventLogsTestComputer, Localizer.Format(L.Readiness.TestComputerLogOk, host, EventLogText.Duration(Localizer, _services.Time.GetUtcNow() - oldest)), host)
                : Ok(ReadinessCodes.EventLogsTestComputer, Localizer.Format(L.Readiness.TestComputerLogReadable, host), host);
        }
        catch (EventLogAccessException ex)
        {
            return AccessError(ReadinessCodes.EventLogsTestComputer, ex, host);
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }
}
