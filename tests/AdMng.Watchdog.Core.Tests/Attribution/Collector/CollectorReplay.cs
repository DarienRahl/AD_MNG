using System.Net;
using System.Text;
using System.Text.Json;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Core.Tests.Rules;
using AdMng.Watchdog.Testing.Fixtures.OpenWec;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;

namespace AdMng.Watchdog.Core.Tests.Attribution.Collector;

/// <summary>
/// Replays one set of DC Security records through both attribution paths (stage B design 21 B4 acceptance): the live
/// DC-log path (records in the fake DC logs, the engine ingests and queries them over "RPC") and collector mode (the same
/// records as OpenWEC RawJson lines of each DC's client directory → <see cref="CollectorIngestor"/> → the store, the engine
/// correlates and looks up from the store). <see cref="Outcome"/> is what the operator sees: the final attribution and
/// lockout of every pending event and the detections appended.
/// </summary>
internal static class CollectorReplay
{
    public const string Realm = "CONTOSO.TEST";

    /// <summary>The lab DCs as the collector knows them: account, client directory, address.</summary>
    public static readonly IReadOnlyList<CollectorDomainController> DomainControllers =
    [
        new(Dc1, "DC01$", [IPAddress.Parse("10.0.1.10")]),
        new(Dc2, "DC02$", [IPAddress.Parse("10.0.1.11")]),
    ];

    public static string Client(string dc) => dc == Dc1 ? "DC01$@" + Realm : "DC02$@" + Realm;

    public static string Address(string dc) => dc == Dc1 ? "10.0.1.10" : "10.0.1.11";

    public static CollectorIdentityPolicy Identity() => new(WecRawJson.SubscriptionUuid, Realm, DomainControllers, []);

    /// <summary>One record of a DC (its record id is in the XML).</summary>
    public sealed record DcRecord(string Dc, string Xml);

    /// <summary>What both paths must agree on (JSON of the final attribution and lockout per pending event, and the kinds and
    /// attributions of the appended detections).</summary>
    public sealed record Outcome(IReadOnlyList<string> Pending, IReadOnlyList<string> Detections);

    /// <summary>Runs the scenario in one mode.</summary>
    public static async Task<(Outcome Outcome, AttributionHarness Harness)> RunAsync(bool collector, IReadOnlyList<DcRecord> records,
        IReadOnlyList<WatchEvent> pending, CancellationToken cancellationToken, int cycles = 1, TimeSpan? between = null,
        Action<AttributionHarness>? configure = null)
    {
        var h = new AttributionHarness(collector: collector);
        h.DomainControllers.Pdc = Dc2;
        configure?.Invoke(h);
        if (collector)
        {
            await IngestAsync(h, records, cancellationToken);
        }
        else
        {
            foreach (var record in records)
            {
                h.Source.Add(record.Dc, record.Xml);
            }
        }

        var ids = pending.Select(e => h.Events.Add(e).Id).ToList();
        for (var i = 0; i < cycles; i++)
        {
            if (i > 0)
            {
                h.Time.Advance(between ?? TimeSpan.FromSeconds(30));
            }

            var result = await h.Engine.RunOnceAsync(cancellationToken);
            Assert.False(result.Failed);
        }

        var json = WatchdogJson.Options;
        var finals = ids.Select(id => h.Events.Get(id))
            .Select(e => string.Join(" | ", JsonSerializer.Serialize(e.Attribution, json), JsonSerializer.Serialize(e.Lockout, json),
                string.Join(",", e.Tags ?? []), e.Severity))
            .ToList();
        var detections = h.Events.All.Where(e => !ids.Contains(e.Id))
            .OrderBy(e => e.Kind).ThenBy(e => e.Object.Name, StringComparer.Ordinal)
            .Select(e => string.Join(" | ", e.Kind, e.Object.Name, e.OriginatingDc, JsonSerializer.Serialize(e.Attribution, json)))
            .ToList();
        return (new Outcome(finals, detections), h);
    }

    /// <summary>The records as RawJson lines per client directory, through the collector ingestor into the store (with
    /// its cursor), and the detections appended – what the tailer does per batch.</summary>
    public static async Task IngestAsync(AttributionHarness h, IReadOnlyList<DcRecord> records, CancellationToken cancellationToken)
    {
        var context = await ContextAsync(h, cancellationToken);
        foreach (var group in records.GroupBy(r => r.Dc))
        {
            var client = Client(group.Key);
            var lines = group.Select((r, i) => Encoding.UTF8.GetBytes(WecRawJson.Line(r.Xml, client, Address(group.Key),
                    TestEvents.Now.AddSeconds(-60 + i)).TrimEnd('\n')))
                .Select(b => (ReadOnlyMemory<byte>)b)
                .ToList();
            var batch = h.CollectorIngestor.Prepare(WecRawJson.ClientDirectory(client), lines, context);
            Assert.Equal(0, batch.RejectedTotal);
            Assert.Equal(0, batch.Malformed);
            await h.CollectorIngestor.DetectAsync(batch, context, cancellationToken);
            await h.SecurityStore.AppendAsync(batch.Records, new EventLogCursor { Source = "wec:" + WecRawJson.ClientDirectory(client), LastRecordId = lines.Count },
                cancellationToken);
        }
    }

    public static async Task<CollectorIngestionContext> ContextAsync(AttributionHarness h, CancellationToken cancellationToken)
    {
        var configuration = h.Config.Current.Configuration;
        return new CollectorIngestionContext
        {
            Ingestion = new IngestionContext
            {
                Settings = configuration.Attribution,
                Reduction = await h.Reductions.CreateAsync(configuration, cancellationToken),
                IsKnownDomainController = c => HostNames.AreEqual(c, Dc1) || HostNames.AreEqual(c, Dc2),
                Domain = "contoso.test",
            },
            Identity = Identity(),
            Budget = new CollectorLogonBudget(20_000),
        };
    }
}
