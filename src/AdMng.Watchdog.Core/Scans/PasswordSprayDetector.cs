using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Detection;
using AdMng.Watchdog.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>
/// Optional password-spray detector (section 10.15, only with <c>Scans.PasswordSpray.Enabled</c>): every minute one
/// bounded query of the PDC emulator's Security log for 4771 (0x18 / 0x6) and 4776 (0xC000006A / 0xC0000064) in the
/// window; a source IP (4771) or workstation (4776) with failures for at least <c>DistinctAccounts</c> accounts →
/// PasswordSprayingSuspected (High, up to 50 accounts, the source in the attribution). Cooldown per source = window,
/// restart-safe through the stored events. Nothing but the event is stored.
/// </summary>
internal sealed class PasswordSprayDetector
{
    public const int MaxRecords = 20_000;
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);

    private const int NamesPerSearch = 25;

    private readonly ISecurityEventSource? _source;
    private readonly IEventStore _events;
    private readonly ScanEventWriter _writer;
    private readonly DomainControllerDirectory _domainControllers;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public PasswordSprayDetector(ISecurityEventSource? source, IEventStore events, ScanEventWriter writer,
        DomainControllerDirectory domainControllers, TimeProvider timeProvider, ILogger logger)
    {
        _source = source;
        _events = events;
        _writer = writer;
        _domainControllers = domainControllers;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RunAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var settings = context.Configuration.Scans.PasswordSpray;
        if (!settings.Enabled || _source is not { IsAvailable: true } source || settings.DistinctAccounts <= 0)
        {
            return;
        }

        var failures = await ReadFailuresAsync(source, context, settings.Window, cancellationToken).ConfigureAwait(false);
        var suspects = failures
            .Where(f => f.SourceKey is not null)
            .GroupBy(f => f.SourceKey!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Source: g.First(), Accounts: g.Select(f => f.TargetUserName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList()))
            .Where(s => s.Accounts.Count >= settings.DistinctAccounts)
            .ToList();
        if (suspects.Count == 0)
        {
            return;
        }

        var cooling = await RecentSourcesAsync(context.Now - settings.Window, context.Now, cancellationToken).ConfigureAwait(false);
        foreach (var (failure, accounts) in suspects.Where(s => !cooling.Contains(s.Source.SourceKey!)))
        {
            var objects = await ResolveAsync(context, accounts.Take(BurstDetector.MaxObjects).ToList(), cancellationToken).ConfigureAwait(false);
            var draft = new EventDraft
            {
                Kind = WatchEventKind.PasswordSprayingSuspected,
                Burst = new BurstInfo { Count = accounts.Count, Window = settings.Window, Objects = objects },
                Attribution = EventAttribution.NotApplicable with
                {
                    IpAddress = failure.IpAddress,
                    Workstation = failure.Workstation,
                    SourceMethod = failure.EventCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    SourceConfidence = failure.EventCode == 4771 ? AttributionConfidence.Medium : AttributionConfidence.Low,
                },
            };
            var domain = new ObjectRef { Dn = context.Facts.DomainDn, Name = context.Facts.DomainDnsName, Kind = WatchObjectKind.Domain };
            await _writer.AppendAsync(context, draft, domain, DetectionSource.BurstDetector, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<FailedLogon>> ReadFailuresAsync(ISecurityEventSource source, ScanContext context, TimeSpan window, CancellationToken cancellationToken)
    {
        var host = _domainControllers.PdcHostName ?? context.Dc.DnsHostName;
        var query = new SecurityEventQuery
        {
            LogName = "Security",
            EventCodes = [4771, 4776],
            From = context.Now - window,
            To = context.Now,
            MaxRecords = MaxRecords,
        };
        var failures = new List<FailedLogon>();
        using var timeout = new CancellationTokenSource(QueryTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await foreach (var xml in source.ReadAsync(host, query, linked.Token).ConfigureAwait(false))
            {
                if (FailedLogonEventReader.TryRead(xml, out var failure) && failure!.IsBadPassword)
                {
                    failures.Add(failure);
                }
            }
        }
        catch (SecurityLogAccessException ex)
        {
            SafeExceptionLog.LogWarning(_logger, ex, "password spray query");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Bounded query: evaluate what arrived within the timeout.
        }

        return failures;
    }

    /// <summary>Sources of PasswordSprayingSuspected events already stored in the window (cooldown).</summary>
    private async Task<HashSet<string>> RecentSourcesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var watchEvent in _events.GetRangeAsync(from, to, cancellationToken).ConfigureAwait(false))
        {
            if (watchEvent.Kind != WatchEventKind.PasswordSprayingSuspected)
            {
                continue;
            }

            if (watchEvent.Attribution.IpAddress is { } ip)
            {
                sources.Add("ip:" + ip);
            }

            if (watchEvent.Attribution.Workstation is { } workstation)
            {
                sources.Add("ws:" + workstation.ToUpperInvariant());
            }
        }

        return sources;
    }

    /// <summary>
    /// Accounts by sAMAccountName (live search). A name that resolves to no account is masked
    /// (<see cref="MaskUnknownName"/>): failures for unknown accounts (4776 0xC0000064, 4771 0x6) are often a password
    /// typed into the user name field, and the raw name must never reach the store or a notification.
    /// </summary>
    private static async Task<IReadOnlyList<ObjectRef>> ResolveAsync(ScanContext context, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, ObjectRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in names.Chunk(NamesPerSearch))
        {
            var filter = LdapFilter.And(DetectionFilters.Users, LdapFilter.Or(chunk.Select(n => LdapFilter.Equal(Attr.SamAccountName, n))));
            await foreach (var entry in ScanQueries.SearchAsync(context, filter, [], cancellationToken).ConfigureAwait(false))
            {
                if (entry.GetString(Attr.SamAccountName) is { } sam)
                {
                    found[sam] = ScanEventWriter.RefOf(entry);
                }
            }
        }

        return [.. names.Select(n => found.TryGetValue(n, out var reference) ? reference : new ObjectRef { Dn = string.Empty, Name = MaskUnknownName(n) })];
    }

    /// <summary>An unresolved TargetUserName as its first character (text element) and length, e.g. <c>L…(9)</c>.</summary>
    public static string MaskUnknownName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            return "…(0)";
        }

        var first = System.Globalization.StringInfo.GetNextTextElementLength(name);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name[..first]}…({name.Length})");
    }
}
