using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Core.Tests.Attribution.Fakes;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class ActorResolverTests
{
    private static readonly DateTimeOffset T = Now.AddMinutes(-1);

    private readonly FakeDetectionStore _detection = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ActorResolver Resolver(params string[] syncAccounts) => new(_detection, new FakeSyncAccounts(syncAccounts));

    [Fact]
    public async Task TheSubject_IsLookedUpInTheSnapshot()
    {
        _detection.AddPrincipal(AdminSid, "adm.jkowalska", "Joanna Kowalska (admin)", "CN=Joanna Kowalska (admin),OU=Admini,DC=contoso,DC=test");

        var resolved = await Resolver().ResolveAsync(Account(4725, T, Admin), Create(WatchEventKind.AccountDisabled), Ct);

        Assert.Equal(AdminSid, resolved.Actor.Sid);
        Assert.Equal("CONTOSO\\adm.jkowalska", resolved.Actor.Account);
        Assert.Equal("Joanna Kowalska (admin)", resolved.Actor.DisplayName);
        Assert.Equal("CN=Joanna Kowalska (admin),OU=Admini,DC=contoso,DC=test", resolved.Actor.Dn);
        Assert.Null(resolved.Reason);
        Assert.Empty(resolved.AddedTags);
    }

    [Fact]
    public async Task UnknownSubject_KeepsTheRecordsAccountName()
    {
        var resolved = await Resolver().ResolveAsync(Account(4725, T, OtherAdmin), Create(WatchEventKind.AccountDisabled), Ct);

        Assert.Equal("CONTOSO\\adm.zzak", resolved.Actor.Account);
        Assert.Null(resolved.Actor.DisplayName);
    }

    [Fact]
    public async Task LocalSystemAndTheDcsOwnAccount_AreSystem()
    {
        var system = await Resolver().ResolveAsync(Account(4738, T, LocalSystem), Create(WatchEventKind.AttributesChanged), Ct);
        var dcAccount = await Resolver().ResolveAsync(
            Account(4738, T, SecurityEventXml.Subject(DomainSid + "-1000", "DC01$", "CONTOSO", "0x3e7")), Create(WatchEventKind.AttributesChanged), Ct);
        var otherDc = Account(4738, T, SecurityEventXml.Subject(DomainSid + "-1001", "DC02$", "CONTOSO", "0x3e7"));

        Assert.Equal(AttributionReasons.System, system.Reason);
        Assert.Equal("System (DC01)", system.Actor.DisplayName);
        Assert.Equal(AttributionReasons.System, dcAccount.Reason);
        Assert.False(ActorResolver.IsSystem(otherDc));
    }

    [Fact]
    public async Task OwnPasswordChange_IsTaggedSelfChange()
    {
        var subject = SecurityEventXml.Subject(UserSid, "lwisniewski", "CONTOSO", "0x91a2");

        var resolved = await Resolver().ResolveAsync(Account(4723, T, subject), Create(WatchEventKind.PasswordChanged), Ct);

        Assert.Equal([EventTags.SelfChange], resolved.AddedTags);
        Assert.Null(resolved.Reason);
    }

    [Fact]
    public async Task AComputerUpdatingItself_IsSelfChange()
    {
        var computerSid = DomainSid + "-3101";
        var e = Create(WatchEventKind.AttributesChanged, "CN=PC-GDAŃSK-01,OU=Komputery,DC=contoso,DC=test", WatchObjectKind.Computer, sid: computerSid);

        var resolved = await Resolver().ResolveAsync(
            Account(4742, T, SecurityEventXml.Subject(computerSid, "PC-GDANSK-01$", "CONTOSO", "0x3e4"), targetSid: computerSid), e, Ct);

        Assert.Equal(AttributionReasons.SelfChange, resolved.Reason);
        Assert.Contains(EventTags.SelfChange, resolved.AddedTags);
    }

    [Fact]
    public async Task ResetByAnotherAccount_IsTaggedPasswordReset()
    {
        var resolved = await Resolver().ResolveAsync(Account(4724, T, Admin), Create(WatchEventKind.PasswordChanged), Ct);

        Assert.Equal([EventTags.PasswordReset], resolved.AddedTags);
    }

    [Fact]
    public async Task SyncAccounts_AreTagged()
    {
        var sync = SecurityEventXml.Subject(SyncSid, "MSOL_4c1a8e7f", "CONTOSO", "0x6612");

        var resolved = await Resolver(SyncSid).ResolveAsync(Account(4738, T, sync), Create(WatchEventKind.AttributesChanged), Ct);

        Assert.Equal([EventTags.SyncAccount], resolved.AddedTags);
    }
}
