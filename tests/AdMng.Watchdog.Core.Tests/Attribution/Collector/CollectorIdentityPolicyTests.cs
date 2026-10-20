using System.Net;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Model;
using AdMng.Watchdog.Testing.Fixtures.OpenWec;

namespace AdMng.Watchdog.Core.Tests.Attribution.Collector;

/// <summary>The identity checks of stage B design 7.6: subscription, client = writable DC of the domain (and its client
/// directory), address, computer name, channel and provider.</summary>
public sealed class CollectorIdentityPolicyTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly CollectorIdentityPolicy Policy = new(WecRawJson.SubscriptionUuid, "contoso.test.",
        [
            new CollectorDomainController("DC01.contoso.test", "DC01$", [IPAddress.Parse("10.0.1.10"), IPAddress.Parse("fd00::10")]),
            new CollectorDomainController("dc02.contoso.test", "OLDNAME$", [IPAddress.Parse("10.0.1.11")]),   // a renamed DC keeps its account
        ],
        [IPAddress.Parse("192.0.2.50")]);

    private static WecRecord Record(string client = "DC01$@CONTOSO.TEST", string ip = "10.0.1.10", Guid? uuid = null) =>
        new(ip, At, client, client, uuid ?? WecRawJson.SubscriptionUuid, "admng-dc-security", "<Event/>");

    [Fact]
    public void ADcRecord_Matches()
    {
        Assert.Equal(CollectorRejection.None, Policy.MatchClient(Record(), "DC01@CONTOSO.TEST", out var dc));
        Assert.Equal("dc01.contoso.test", dc!.HostName);
        Assert.Equal("CONTOSO.TEST", Policy.Realm);
    }

    [Theory]
    [InlineData("dc01$@contoso.test", "dc01@contoso.test", CollectorRejection.None)]          // case-insensitive
    [InlineData("OLDNAME$@CONTOSO.TEST", "OLDNAME@CONTOSO.TEST", CollectorRejection.None)]
    [InlineData("SRV01$@CONTOSO.TEST", "SRV01@CONTOSO.TEST", CollectorRejection.Client)]       // a member server
    [InlineData("RODC01$@CONTOSO.TEST", "RODC01@CONTOSO.TEST", CollectorRejection.Client)]     // not a writable DC
    [InlineData("DC01$@FABRIKAM.TEST", "DC01@FABRIKAM.TEST", CollectorRejection.Client)]       // a DC of another domain
    [InlineData("DC01$@CONTOSO.TEST", "DC02@CONTOSO.TEST", CollectorRejection.Client)]         // in another client's directory
    [InlineData("", "DC01@CONTOSO.TEST", CollectorRejection.Client)]
    public void TheClient_MustBeAWritableDcInItsOwnDirectory(string client, string directory, CollectorRejection expected) =>
        Assert.Equal(expected, Policy.MatchClient(Record(client, client.StartsWith("OLDNAME", StringComparison.Ordinal) ? "10.0.1.11" : "10.0.1.10"),
            directory, out _));

    [Fact]
    public void AnotherSubscription_IsRefusedFirst() =>
        Assert.Equal(CollectorRejection.Subscription, Policy.MatchClient(Record(uuid: Guid.NewGuid()), "DC01@CONTOSO.TEST", out _));

    [Theory]
    [InlineData("10.0.1.10", CollectorRejection.None)]
    [InlineData("::ffff:10.0.1.10", CollectorRejection.None)]
    [InlineData("fd00::10", CollectorRejection.None)]
    [InlineData("192.0.2.50", CollectorRejection.None)]        // Watchdog:Collector:AdditionalSourceAddresses (NAT)
    [InlineData("10.0.1.11", CollectorRejection.Address)]      // the other DC's address
    [InlineData("10.9.9.9", CollectorRejection.Address)]
    [InlineData("", CollectorRejection.Address)]
    public void TheAddress_MustBeTheDcsOrAnAdditionalOne(string ip, CollectorRejection expected) =>
        Assert.Equal(expected, Policy.MatchClient(Record(ip: ip), "DC01@CONTOSO.TEST", out _));

    [Fact]
    public void ComputerChannelAndProvider_AreCheckedOnTheParsedRecord()
    {
        var dc = Policy.DomainControllers.First(d => d.HostName == "dc01.contoso.test");
        ParsedSecurityRecord Parsed(string computer = "DC01.contoso.test", string channel = "Security",
            string provider = "Microsoft-Windows-Security-Auditing", int code = 4724) =>
            new(provider, new SecurityEvent { Source = dc.HostName, Computer = computer, RecordId = 1, EventCode = code, TimeCreated = At }, channel);

        Assert.Equal(CollectorRejection.None, CollectorIdentityPolicy.CheckRecord(Parsed(), dc));
        Assert.Equal(CollectorRejection.None, CollectorIdentityPolicy.CheckRecord(Parsed(computer: "dc01.CONTOSO.test."), dc));
        Assert.Equal(CollectorRejection.Computer, CollectorIdentityPolicy.CheckRecord(Parsed(computer: "DC02.contoso.test"), dc));
        Assert.Equal(CollectorRejection.Channel, CollectorIdentityPolicy.CheckRecord(Parsed(channel: "ForwardedEvents"), dc));
        Assert.Equal(CollectorRejection.Channel, CollectorIdentityPolicy.CheckRecord(Parsed(provider: "Fake-Provider"), dc));
        Assert.Equal(CollectorRejection.None, CollectorIdentityPolicy.CheckRecord(Parsed(provider: "Microsoft-Windows-Eventlog", code: 1102), dc));
    }

    [Theory]
    [InlineData("DC01$@CONTOSO.TEST", "DC01@CONTOSO.TEST")]
    [InlineData("HTTP/x y@R", "HTTPxy@R")]
    [InlineData("dc_01-a.b$@r", "dc_01-a.b@r")]
    public void ClientDirectoryName_IsOpenWecsSanitizedName(string principal, string expected) =>
        Assert.Equal(expected, CollectorIdentityPolicy.ClientDirectoryName(principal));
}
