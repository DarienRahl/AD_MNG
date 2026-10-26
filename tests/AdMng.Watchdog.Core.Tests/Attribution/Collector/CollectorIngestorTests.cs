using System.Text;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Testing.Fixtures.OpenWec;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution.Collector;

/// <summary>One batch of RawJson lines through <see cref="CollectorIngestor"/> (stage B design 7.5–7.7): identity checks,
/// reduction of logon and failure records, the ingest budget, noise, malformed lines and the detections.</summary>
public sealed class CollectorIngestorTests : IDisposable
{
    private const string Client = "DC01$@CONTOSO.TEST";
    private const string Directory = "DC01@CONTOSO.TEST";
    private static readonly DateTimeOffset T = Now.AddMinutes(-1);
    private static long _id = 9_100_000;

    private readonly AttributionHarness _h = new(collector: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    private static long Id() => Interlocked.Increment(ref _id);

    private static ReadOnlyMemory<byte> Line(string xml, string client = Client, string ip = "10.0.1.10", Guid? uuid = null, DateTimeOffset? received = null) =>
        Encoding.UTF8.GetBytes(WecRawJson.Line(xml, client, ip, received ?? T.AddSeconds(4), uuid).TrimEnd('\n'));

    private async Task<(CollectorBatch Batch, CollectorIngestionContext Context)> PrepareAsync(IEnumerable<ReadOnlyMemory<byte>> lines,
        CollectorLogonBudget? budget = null, string directory = Directory)
    {
        var context = await CollectorReplay.ContextAsync(_h, Ct);
        if (budget is not null)
        {
            context = context with { Budget = budget };
        }

        return (_h.CollectorIngestor.Prepare(directory, [.. lines], context), context);
    }

    [Fact]
    public async Task ALogonRecord_KeepsOnlyTheFieldsOfSection77()
    {
        var (batch, _) = await PrepareAsync([Line(WindowsEventXml.Logon(Id(), T, Dc1, AdminSid, "adm.jkowalska", AdminLogonId, 3, "10.0.5.21", "ADM-WS01"))]);

        var logon = Assert.Single(batch.Records);
        Assert.Equal(Dc1, logon.Source);
        Assert.Equal(
            ["AuthenticationPackageName", "IpAddress", "LogonType", "TargetDomainName", "TargetLogonId", "TargetUserName", "TargetUserSid", "WorkstationName"],
            logon.Data.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(AdminLogonId, logon.TargetLogonId);
        Assert.Equal(TimeSpan.FromSeconds(4), Assert.Single(batch.Lags).Lag);
        Assert.Equal(T, batch.NewestRecordAt);
        Assert.Equal("dc01.contoso.test", batch.Dc!.HostName);
    }

    [Fact]
    public async Task FailureRecords_KeepTheirFields()
    {
        var (batch, _) = await PrepareAsync(
        [
            Line(WindowsEventXml.LogonFailed(Id(), T, Dc1, "łucja.żak", "10.0.7.8", "WS-ŁÓDŹ-02")),
            Line(WindowsEventXml.KerberosPreAuthFailed(Id(), T, Dc1, "łucja.żak", UserSid, "10.0.7.7")),
            Line(WindowsEventXml.CredentialValidation(Id(), T, Dc1, "łucja.żak", "WS-ŁÓDŹ-01")),
        ]);

        Assert.Equal(3, batch.Records.Count);
        Assert.Equal(["IpAddress", "LogonType", "Status", "SubStatus", "TargetDomainName", "TargetUserName", "WorkstationName"],
            batch.Records[0].Data.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["IpAddress", "Status", "TargetSid", "TargetUserName"], batch.Records[1].Data.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["Status", "TargetUserName", "Workstation"], batch.Records[2].Data.Keys.Order(StringComparer.Ordinal));
        Assert.All(batch.Records, r => Assert.Equal("łucja.żak", r.Get("TargetUserName")));
    }

    [Fact]
    public async Task ComputerAnonymousAndOtherLogonTypes_AreDroppedByDefault()
    {
        IEnumerable<ReadOnlyMemory<byte>> Lines() =>
        [
            Line(WindowsEventXml.Logon(Id(), T, Dc1, "S-1-5-21-1-2-3-1001", "WS01$", "0x91", 3, "10.0.3.3", null)),
            Line(WindowsEventXml.Logon(Id(), T, Dc1, "S-1-5-7", "ANONYMOUS LOGON", "0x92", 3, "10.0.3.4", null, "NTLM")),
            Line(WindowsEventXml.Logon(Id(), T, Dc1, AdminSid, "adm.jkowalska", "0x93", 2, null, null)),
        ];

        var (dropped, _) = await PrepareAsync(Lines());
        Assert.Empty(dropped.Records);
        Assert.Equal(3, dropped.Filtered);
        Assert.Equal(3, dropped.Accepted);

        _h.Config.Set(new WatchdogConfiguration { Attribution = new AttributionSettings { DropComputerLogons = false, DropAnonymousLogons = false } });
        var (kept, _) = await PrepareAsync(Lines());
        Assert.Equal(2, kept.Records.Count);   // logon type 2 never: the subscription forwards 3 and 10 only
    }

    [Fact]
    public async Task TheBudget_LimitsLogonRecordsPerDcAndMinuteButNeverChanges()
    {
        var lines = Enumerable.Range(0, 5)
            .Select(i => Line(WindowsEventXml.LogonFailed(Id(), T.AddSeconds(i), Dc1, "konto" + i, "10.0.7.8", null)))
            .Append(Line(WindowsEventXml.Account(4725, Id(), T.AddSeconds(6), Dc1, UserSid, "lwisniewski", WindowsEventXml.Subject(AdminSid, "adm.jkowalska", AdminLogonId))));

        var (batch, context) = await PrepareAsync(lines, new CollectorLogonBudget(3));
        var appended = await _h.CollectorIngestor.DetectAsync(batch, context, Ct);

        Assert.Equal(2, batch.OverBudget);
        Assert.True(batch.Flooded);
        Assert.Equal(4, batch.Records.Count);   // 3 failures + the change
        var flooding = Assert.Single(appended);
        Assert.Equal(WatchEventKind.HealthEventLogGap, flooding.Kind);
        Assert.Equal(EventLogGapDetector.Flooding, flooding.Changes[0].NewValues[0]);
    }

    [Fact]
    public async Task Noise5136_IsDroppedLikeTheDcLogQuerySuppressesIt()
    {
        var subject = WindowsEventXml.Subject("S-1-5-18", "DC01$", "0x3e7");
        var (batch, _) = await PrepareAsync(
        [
            Line(WindowsEventXml.DirectoryModified(Id(), T, Dc1, UserGuid, UserDn, "user", "lastLogonTimestamp", "134055", true, subject)),
            Line(WindowsEventXml.DirectoryModified(Id(), T, Dc1, UserGuid, UserDn, "user", "dSCorePropagationData", "16010101000000.0Z", true, subject)),
            Line(WindowsEventXml.DirectoryModified(Id(), T, Dc1, UserGuid, UserDn, "user", "department", "Kadry", true, subject)),
        ]);

        Assert.Equal(2, batch.Filtered);
        Assert.Equal("department", Assert.Single(batch.Records).Get("AttributeLDAPDisplayName"));
    }

    [Fact]
    public async Task RejectedAndMalformedLines_AreCountedPerCheck()
    {
        var xml = WindowsEventXml.Account(4724, Id(), T, Dc1, UserSid, "lwisniewski", WindowsEventXml.Subject(AdminSid, "adm.jkowalska", AdminLogonId));
        var (batch, _) = await PrepareAsync(
        [
            Line(xml, uuid: Guid.NewGuid()),
            Line(xml, client: "SRV01$@CONTOSO.TEST"),
            Line(xml, ip: "10.6.6.6"),
            Line(xml.Replace(">DC01.contoso.test<", ">DC02.contoso.test<", StringComparison.OrdinalIgnoreCase)),
            Line(xml.Replace("<Channel>Security</Channel>", "<Channel>Application</Channel>", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes("{\"meta\":{\"IpAddress\""),
            Line("<Event><System>"),
            Line(xml),
        ]);

        Assert.Equal(1, batch.Rejected[CollectorRejection.Subscription]);
        Assert.Equal(1, batch.Rejected[CollectorRejection.Client]);
        Assert.Equal(1, batch.Rejected[CollectorRejection.Address]);
        Assert.Equal(1, batch.Rejected[CollectorRejection.Computer]);
        Assert.Equal(1, batch.Rejected[CollectorRejection.Channel]);
        Assert.Equal(2, batch.Malformed);
        Assert.Single(batch.Records);
        Assert.Equal(8, batch.Lines);
    }

    [Fact]
    public async Task APartialLineBeforeAWholeRecord_IsResynchronized()
    {
        var whole = WecRawJson.Line(WindowsEventXml.Account(4724, Id(), T, Dc1, UserSid, "lwisniewski",
            WindowsEventXml.Subject(AdminSid, "adm.jkowalska", AdminLogonId)), Client, "10.0.1.10", T).TrimEnd('\n');

        var (batch, _) = await PrepareAsync([Encoding.UTF8.GetBytes(whole[..90] + whole)]);

        Assert.Equal(1, batch.Resynchronized);
        Assert.Equal(1, batch.Malformed);
        Assert.Single(batch.Records);
    }

    [Fact]
    public async Task Detections_AreAppendedOnceWhenABatchIsReadAgain()
    {
        var lines = new[] { Line(WindowsEventXml.LogCleared(Id(), T, Dc1, AdminSid, "adm.jkowalska", AdminLogonId)) };
        var (first, context) = await PrepareAsync(lines);
        var appended = await _h.CollectorIngestor.DetectAsync(first, context, Ct);
        var (again, _) = await PrepareAsync(lines);
        var repeated = await _h.CollectorIngestor.DetectAsync(again, context, Ct);

        Assert.Contains(appended, e => e.Kind == WatchEventKind.SecurityLogCleared);
        Assert.Empty(repeated);
        Assert.Single(_h.Events.All, e => e.Kind == WatchEventKind.SecurityLogCleared);
        Assert.True(_h.Tracker.WasOverwrittenSince(Dc1, T.AddSeconds(-1)));
    }
}
