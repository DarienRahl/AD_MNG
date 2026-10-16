using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Model;

public class SecurityEventTests
{
    private static SecurityEvent Event(params (string Name, string Value)[] data) => new()
    {
        Source = "dc01.contoso.test",
        Computer = "DC01.contoso.test",
        RecordId = 410204,
        EventCode = 5136,
        TimeCreated = new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero),
        Data = data.ToDictionary(d => d.Name, d => d.Value, StringComparer.Ordinal),
    };

    [Fact]
    public void Subject_IsReadFromEventData()
    {
        var record = Event(
            ("SubjectUserSid", "S-1-5-21-1004336348-1177238915-682003330-1104"),
            ("SubjectUserName", "łukasz.wiśniewski"),
            ("SubjectDomainName", "CONTOSO"),
            ("SubjectLogonId", "0x00030DC2"),
            ("TargetSid", "S-1-5-21-1004336348-1177238915-682003330-6609"),
            ("ObjectGUID", "{4FE80A66-5F93-4F73-B215-68678058E613}"),
            ("OpCorrelationID", "{02647639-8626-43CE-AFE6-7AA1AD657739}"));

        Assert.Equal("S-1-5-21-1004336348-1177238915-682003330-1104", record.SubjectSid);
        Assert.Equal(@"CONTOSO\łukasz.wiśniewski", record.SubjectAccount);
        Assert.Equal("0x30dc2", record.SubjectLogonId);
        Assert.Equal("S-1-5-21-1004336348-1177238915-682003330-6609", record.TargetSid);
        Assert.Equal(Guid.Parse("4fe80a66-5f93-4f73-b215-68678058e613"), record.ObjectGuid);
        Assert.Equal(Guid.Parse("02647639-8626-43ce-afe6-7aa1ad657739"), record.OpCorrelationId);
        Assert.Equal("CONTOSO", record.Get("SubjectDomainName"));
        Assert.Null(record.Get("subjectdomainname"));
    }

    [Fact]
    public void MissingOrMalformedValues_AreNull()
    {
        var record = Event(("SubjectLogonId", "brak"), ("ObjectGUID", "nie-guid"), ("SubjectDomainName", "CONTOSO"));

        Assert.Null(record.SubjectSid);
        Assert.Null(record.SubjectAccount);
        Assert.Null(record.SubjectLogonId);
        Assert.Null(record.ObjectGuid);
        Assert.Null(record.OpCorrelationId);
    }

    [Fact]
    public void SubjectAccount_WithoutDomain_IsTheUserName()
    {
        Assert.Equal("DC01$", Event(("SubjectUserName", "DC01$")).SubjectAccount);
    }
}
