using AdMng.Watchdog.Core.Model;
using Xunit;

namespace AdMng.Watchdog.Testing.Stores;

// ISecurityEventStore
public abstract partial class WatchdogStoreContractTests<TStore>
{
    private const string Dc01 = "dc01.contoso.test";
    private const string Dc02 = "dc02.contoso.test";
    private const string TargetSid = "S-1-5-21-1004336348-1177238915-682003330-1106";

    [Fact]
    public Task SecurityEvents_AppendIgnoresDuplicates_AndMovesTheCursor() => WithStoreAsync(async store =>
    {
        var first = SecurityRecord(Dc01, 100, 4738, Now);
        await store.AppendAsync([first, SecurityRecord(Dc01, 101, 4724, Now)], Cursor(Dc01, 101), Ct);

        await store.AppendAsync([first with { EventCode = 4720 }, SecurityRecord(Dc02, 100, 4738, Now)], Cursor(Dc01, 150), Ct);

        var all = await store.FindAsync(new SecurityEventCriteria { From = Now.AddDays(-1), To = Now.AddDays(1) }, Ct);
        Assert.Equal(3, all.Count);
        Assert.Equal(4738, all.Single(e => e.Computer == Dc01 && e.RecordId == 100).EventCode);
        var cursor = await store.GetCursorAsync(Dc01, Ct);
        Assert.NotNull(cursor);
        Assert.Equal(150, cursor.LastRecordId);
        Assert.Equal("<BookmarkList><Bookmark Channel='Security' RecordId='150' IsCurrent='true'/></BookmarkList>", cursor.BookmarkXml);
        Assert.Equal(90, cursor.OldestRecordSeen);
        Assert.Equal(Now, cursor.UpdatedAt);
        Assert.Null(await store.GetCursorAsync(Dc02, Ct));
    });

    [Fact]
    public Task SecurityEvents_RoundTripTheirData() => WithStoreAsync(async store =>
    {
        var record = SecurityRecord(Dc01, 918273, 5136, Now.AddMilliseconds(534)) with
        {
            Source = "collector.contoso.test",
            Version = 1,
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SubjectUserSid"] = "S-1-5-21-1004336348-1177238915-682003330-1104",
                ["SubjectUserName"] = "adm.łukasz",
                ["SubjectDomainName"] = "CONTOSO",
                ["SubjectLogonId"] = "0x30DC2",
                ["ObjectDN"] = "CN=Zofia Żak,OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test",
                ["ObjectGUID"] = "{9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f}",
                ["AttributeLDAPDisplayName"] = "department",
                ["AttributeValue"] = "Księgowość",
            },
        };
        await store.AppendAsync([record], Cursor("collector.contoso.test", 918273), Ct);

        var loaded = Assert.Single(await store.FindAsync(new SecurityEventCriteria { From = Now, To = Now.AddSeconds(1) }, Ct));

        Assert.Equal("collector.contoso.test", loaded.Source);
        Assert.Equal(Dc01, loaded.Computer);
        Assert.Equal(918273, loaded.RecordId);
        Assert.Equal(5136, loaded.EventCode);
        Assert.Equal(1, loaded.Version);
        Assert.Equal(Now.AddMilliseconds(534), loaded.TimeCreated);
        Assert.Equal(record.Data.OrderBy(d => d.Key, StringComparer.Ordinal), loaded.Data.OrderBy(d => d.Key, StringComparer.Ordinal));
        Assert.Equal("0x30dc2", loaded.SubjectLogonId);
        Assert.Equal(@"CONTOSO\adm.łukasz", loaded.SubjectAccount);
        Assert.Equal(Guid.Parse("9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f"), loaded.ObjectGuid);
    });

    [Fact]
    public Task SecurityEvents_FindByCodesComputerAndClosedWindow_InTimeOrder() => WithStoreAsync(async store =>
    {
        await store.AppendAsync(
            [
                SecurityRecord(Dc01, 1, 4738, Now.AddSeconds(3)),
                SecurityRecord(Dc01, 2, 4738, Now.AddSeconds(-3)),
                SecurityRecord(Dc01, 3, 4738, Now.AddSeconds(4)),
                SecurityRecord(Dc01, 4, 4724, Now),
                SecurityRecord(Dc02, 5, 4738, Now),
            ],
            Cursor(Dc01, 5),
            Ct);

        var window = new SecurityEventCriteria { EventCodes = [4738], Computer = "DC01.contoso.test", From = Now.AddSeconds(-3), To = Now.AddSeconds(3) };

        Assert.Equal([2L, 1L], (await store.FindAsync(window, Ct)).Select(e => e.RecordId));
        Assert.Equal([2L, 5L, 1L], (await store.FindAsync(window with { Computer = null }, Ct)).Select(e => e.RecordId));
        Assert.Equal([2L, 4L, 5L, 1L], (await store.FindAsync(window with { Computer = null, EventCodes = [] }, Ct)).Select(e => e.RecordId));
        Assert.Equal([2L], (await store.FindAsync(window with { Limit = 1 }, Ct)).Select(e => e.RecordId));
    });

    [Fact]
    public Task SecurityEvents_FindByStrongAndWeakKeys() => WithStoreAsync(async store =>
    {
        var objectGuid = Guid.Parse("9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f");
        await store.AppendAsync(
            [
                SecurityRecord(Dc01, 1, 4738, Now, ("TargetSid", TargetSid), ("TargetUserName", "zofia.zak")),
                SecurityRecord(Dc01, 2, 5136, Now, ("ObjectGUID", "{" + objectGuid + "}"), ("ObjectDN", "CN=Zofia Żak,OU=Księgowość,OU=Pracownicy,DC=contoso,DC=test")),
                SecurityRecord(Dc01, 3, 4728, Now, ("TargetSid", "S-1-5-21-1004336348-1177238915-682003330-512"), ("MemberSid", TargetSid)),
                SecurityRecord(Dc01, 4, 4738, Now, ("TargetSid", "S-1-5-21-1004336348-1177238915-682003330-1999"), ("TargetUserName", "jan.kowalski")),
            ],
            Cursor(Dc01, 4),
            Ct);
        var any = new SecurityEventCriteria { From = Now.AddMinutes(-1), To = Now.AddMinutes(1) };

        Assert.Equal([1L], (await store.FindAsync(any with { TargetSid = TargetSid.ToLowerInvariant() }, Ct)).Select(e => e.RecordId));
        Assert.Equal([2L], (await store.FindAsync(any with { ObjectGuid = objectGuid }, Ct)).Select(e => e.RecordId));
        Assert.Equal([3L], (await store.FindAsync(any with { MemberSid = TargetSid }, Ct)).Select(e => e.RecordId));
        Assert.Equal([1L], (await store.FindAsync(any with { TargetUserName = "ZOFIA.ZAK" }, Ct)).Select(e => e.RecordId));
        Assert.Equal([2L], (await store.FindAsync(any with { ObjectDn = "cn=zofia żak,ou=księgowość,ou=pracownicy,dc=contoso,dc=test" }, Ct)).Select(e => e.RecordId));
        Assert.Empty(await store.FindAsync(any with { ObjectGuid = Guid.NewGuid() }, Ct));
    });

    [Fact]
    public Task SecurityEvents_LastSeen_IsPerComputerAndEventCode() => WithStoreAsync(async store =>
    {
        await store.AppendAsync(
            [
                SecurityRecord(Dc01, 1, 5136, Now.AddDays(-3)),
                SecurityRecord(Dc01, 2, 5136, Now.AddDays(-1)),
                SecurityRecord(Dc02, 3, 5136, Now),
                SecurityRecord(Dc01, 4, 4738, Now),
            ],
            Cursor(Dc01, 4),
            Ct);

        Assert.Equal(Now.AddDays(-1), await store.GetLastSeenAsync(Dc01, 5136, Ct));
        Assert.Equal(Now, await store.GetLastSeenAsync(Dc02, 5136, Ct));
        Assert.Null(await store.GetLastSeenAsync(Dc02, 4738, Ct));
    });

    [Fact]
    public Task SecurityEvents_AreUniquePerComputerRecordAndTime() => WithStoreAsync(async store =>
    {
        // Migration 0004: a cleared log restarts its record numbers – the same number at another time is another record;
        // OpenWEC delivering a record twice is ignored.
        var original = SecurityRecord(Dc01, 7, 4724, Now);
        await store.AppendAsync([original, original, SecurityRecord(Dc01, 7, 4725, Now.AddHours(2))], Cursor("wec:DC01@CONTOSO.TEST", 1), Ct);
        await store.AppendAsync([original with { EventCode = 4720 }], Cursor("wec:DC01@CONTOSO.TEST", 2), Ct);

        var all = await store.FindAsync(new SecurityEventCriteria { From = Now.AddDays(-1), To = Now.AddDays(1) }, Ct);

        Assert.Equal([4724, 4725], all.Select(e => e.EventCode));
    });

    [Fact]
    public Task SecurityEvents_FindTheLogonSessionByItsNormalizedLogonId() => WithStoreAsync(async store =>
    {
        await store.AppendAsync(
        [
            SecurityRecord(Dc01, 1, 4624, Now.AddMinutes(-30), ("TargetLogonId", "0x0005A3F1"), ("TargetUserName", "adm.łukasz"), ("LogonType", "3")),
            SecurityRecord(Dc02, 2, 4624, Now.AddMinutes(-20), ("TargetLogonId", "0x5a3f1"), ("TargetUserName", "inny"), ("LogonType", "3")),
            SecurityRecord(Dc01, 3, 4625, Now.AddMinutes(-10), ("TargetUserName", "adm.łukasz"), ("Status", "0xc000006d")),
            SecurityRecord(Dc01, 4, 4724, Now.AddMinutes(-5), ("SubjectLogonId", "0x5a3f1"), ("TargetUserName", "zofia")),
        ], Cursor("wec:DC01@CONTOSO.TEST", 4), Ct);

        var logon = Assert.Single(await store.FindAsync(new SecurityEventCriteria
        {
            EventCodes = [4624], Computer = Dc01, LogonId = "0x5A3F1", From = Now.AddHours(-1), To = Now,
        }, Ct));
        var failures = await store.FindAsync(new SecurityEventCriteria
        {
            EventCodes = [4625, 4771, 4776], TargetUserName = "ADM.ŁUKASZ", From = Now.AddHours(-1), To = Now,
        }, Ct);

        Assert.Equal(1, logon.RecordId);
        Assert.Equal(3, Assert.Single(failures).RecordId);
    });

    private static SecurityEvent SecurityRecord(string computer, long recordId, int eventCode, DateTimeOffset time, params (string Name, string Value)[] data) => new()
    {
        Source = computer,
        Computer = computer,
        RecordId = recordId,
        EventCode = eventCode,
        TimeCreated = time,
        Data = data.ToDictionary(d => d.Name, d => d.Value, StringComparer.Ordinal),
    };

    private static EventLogCursor Cursor(string source, long lastRecordId) => new()
    {
        Source = source,
        LastRecordId = lastRecordId,
        BookmarkXml = $"<BookmarkList><Bookmark Channel='Security' RecordId='{lastRecordId}' IsCurrent='true'/></BookmarkList>",
        OldestRecordSeen = 90,
        UpdatedAt = Now,
    };
}
