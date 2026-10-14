using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class EventSourceGuardTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 14, 6, 0, 0, TimeSpan.Zero);
    private static readonly HashSet<string> KnownDcs = new(["dc01.contoso.local", "dc02.contoso.local"], StringComparer.OrdinalIgnoreCase);
    private readonly SecurityEventParser _parser = new();

    private bool Accept(string xml) => EventSourceGuard.Accept(_parser.Parse(xml, "collector")!, h => KnownDcs.Contains(h));

    [Fact]
    public void SecurityAuditingRecordsOfKnownDcs_AreAccepted() =>
        Assert.All(SecurityEventFixtures.All, x => Assert.True(Accept(x)));

    [Fact]
    public void RecordsOfOtherComputers_AreRejected() =>
        Assert.False(Accept(SecurityEventXml.Create(4720, 1, At, "WS-KRK-0042.contoso.local", [("TargetSid", "S-1-5-21-1-2-3-500")])));

    [Fact]
    public void RecordsOfAnotherProvider_AreRejected()
    {
        Assert.False(Accept(SecurityEventXml.Create(4720, 1, At, "DC01.contoso.local", [("TargetSid", "S-1-5-21-1-2-3-500")], provider: "Podrobiony-Dostawca")));
        Assert.False(Accept(SecurityEventXml.Create(1102, 1, At, "DC01.contoso.local", [("SubjectUserSid", "S-1-5-18")], provider: SecurityEventXml.SecurityAuditing)));
    }

    [Fact]
    public void EventLogServiceRecords_NeedTheEventlogProvider()
    {
        Assert.True(Accept(SecurityEventXml.LogCleared(2, At, "dc02.CONTOSO.local", "S-1-5-21-1-2-3-1104", "jkowalska", "CONTOSO", "0x1")));
        Assert.True(Accept(SecurityEventXml.EventLogServiceRecord(1104, 3, At, "dc01.contoso.local")));
    }
}
