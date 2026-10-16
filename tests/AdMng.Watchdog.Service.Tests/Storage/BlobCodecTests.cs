using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Service.Storage;

namespace AdMng.Watchdog.Service.Tests.Storage;

public sealed class BlobCodecTests
{
    [Fact]
    public void SnapshotAttributes_StayCaseInsensitive()
    {
        var snapshot = new SnapshotObject
        {
            ObjectGuid = Guid.NewGuid(),
            Dn = "CN=Zofia Żak,DC=contoso,DC=test",
            Kind = WatchObjectKind.User,
            ObjectClasses = ["top", "user"],
            Attributes = new Dictionary<string, SnapshotAttribute>(StringComparer.OrdinalIgnoreCase)
            {
                ["department"] = new() { Values = ["Księgowość"], Kind = AttributeValueKind.Text },
            },
        };

        var payload = BlobCodec.DecodeSnapshot(BlobCodec.EncodeSnapshot(snapshot));

        Assert.Equal(["top", "user"], payload.ObjectClasses);
        Assert.Equal("Księgowość", Assert.Single(payload.Attributes["DEPARTMENT"].Values));
        Assert.Empty(payload.Attributes["Department"].DisplayValues);
    }

    [Fact]
    public void SecurityEventData_StaysOrdinal()
    {
        var record = new SecurityEvent
        {
            Source = "dc01.contoso.test",
            Computer = "dc01.contoso.test",
            RecordId = 918273,
            EventCode = 4738,
            TimeCreated = DateTimeOffset.UnixEpoch,
            Data = new Dictionary<string, string>(StringComparer.Ordinal) { ["SubjectUserName"] = "adm.łukasz", ["SubjectDomainName"] = "CONTOSO" },
        };

        var loaded = BlobCodec.DecodeSecurityEvent(BlobCodec.EncodeSecurityEvent(record));

        Assert.Equal("adm.łukasz", loaded.Get("SubjectUserName"));
        Assert.Null(loaded.Get("subjectusername"));
        Assert.Equal(@"CONTOSO\adm.łukasz", loaded.SubjectAccount);
    }

    [Fact]
    public void EmailHeaders_StayCaseInsensitive_AndBodiesRoundTrip()
    {
        var email = new RenderedEmail
        {
            Subject = "[AD Watchdog][WYSOKI] Zmiana: Łucja Nowak",
            HtmlBody = "<p>Łucja</p>",
            TextBody = "Łucja",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-AdMng-Rule"] = "tier0-privilege" },
        };
        var http = new RenderedHttpMessage { Body = [1, 2, 3, 0xC5, 0x81], Title = "Zażółć" };

        var payload = BlobCodec.DecodeOutbox(BlobCodec.EncodeOutbox(email, http));

        Assert.Equal("tier0-privilege", payload.Email!.Headers["x-admng-rule"]);
        Assert.Equal(email.Subject, payload.Email.Subject);
        Assert.Equal(http.Body, payload.Http!.Body);
        Assert.Equal("Zażółć", payload.Http.Title);
    }

    [Fact]
    public void ContractPayloads_RoundTripThroughWatchdogJson()
    {
        var watchEvent = new WatchEvent
        {
            EventId = Guid.CreateVersion7(),
            Kind = WatchEventKind.AttributesChanged,
            Object = new ObjectRef { Dn = "CN=Zofia Żak,DC=contoso,DC=test", Kind = WatchObjectKind.User },
            DetectedAt = DateTimeOffset.UnixEpoch,
            Source = DetectionSource.IncrementalPoll,
            Changes = [new AttributeChange { Attribute = "department", OldValues = ["Kadry"], NewValues = ["Księgowość"] }],
        };

        var loaded = BlobCodec.DecodeContract<WatchEvent>(BlobCodec.EncodeContract(watchEvent));

        Assert.Equal(watchEvent.EventId, loaded.EventId);
        Assert.Equal(["Księgowość"], Assert.Single(loaded.Changes).NewValues);
        Assert.Equal(AttributionStatus.Pending, loaded.Attribution.Status);
    }
}
