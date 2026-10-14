using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Attribution;
using AdMng.Watchdog.Testing.Fixtures.SecurityEvents;
using static AdMng.Watchdog.Core.Tests.Attribution.Records;
using static AdMng.Watchdog.Core.Tests.Rules.TestEvents;

namespace AdMng.Watchdog.Core.Tests.Attribution;

public class AttributionConfirmationTests
{
    private static readonly DateTimeOffset T = Now.AddMinutes(-1);

    private static WatchEvent WithChange(WatchEventKind kind, string attribute, string[] oldValues, string[] newValues) =>
        Create(kind) with { Changes = [new AttributeChange { Attribute = attribute, OldValues = oldValues, NewValues = newValues }] };

    [Fact]
    public void Ds5136_AttributeNameAndValue()
    {
        var department = WithChange(WatchEventKind.AttributesChanged, "department", ["Kadry"], ["Księgowość"]);

        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(Modified(T, Admin, "department", "Księgowość", added: true), department));
        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(Modified(T, Admin, "department", "Kadry", added: false), department));
        Assert.Equal(CandidateFit.Unconfirmed, AttributionConfirmation.Assess(Modified(T, Admin, "department", "Zarząd", added: true), department));
        Assert.Equal(CandidateFit.Contradicts, AttributionConfirmation.Assess(Modified(T, Admin, "title", "Kierownik", added: true), department));
    }

    [Fact]
    public void Ds5136_NameOnly_WhenNoValueWasKept()
    {
        var department = WithChange(WatchEventKind.AttributesChanged, "department", ["Kadry"], ["Księgowość"]);

        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(Modified(T, Admin, "department", null, added: true), department));
        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(Modified(T, Admin, "unicodePwd", AdMng.Core.Schema.SecretAttributes.Mask, added: true),
            WithChange(WatchEventKind.AttributesChanged, "unicodePwd", [], [])));
    }

    [Fact]
    public void Ds5136_UserAccountControlPair_BothConfirm()
    {
        var disabled = WithChange(WatchEventKind.AccountDisabled, "userAccountControl", ["512"], ["514"]);

        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(Modified(T, Admin, "userAccountControl", "512", added: false), disabled));
        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(Modified(T, Admin, "userAccountControl", "514", added: true), disabled));
    }

    [Fact]
    public void Ds5136_Member_ComparesTheMemberDnAndTheOperation()
    {
        var added = Membership(WatchEventKind.GroupMemberAdded, "CN=Księgowość,OU=Grupy,DC=contoso,DC=test");

        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(Modified(T, Admin, "member", UserDn.ToUpperInvariant(), added: true), added));
        Assert.Equal(CandidateFit.Contradicts, AttributionConfirmation.Assess(Modified(T, Admin, "member", "CN=Inny,DC=contoso,DC=test", added: true), added));
        Assert.Equal(CandidateFit.Contradicts, AttributionConfirmation.Assess(Modified(T, Admin, "member", UserDn, added: false), added));
    }

    [Fact]
    public void Sam4738_FieldsAndAcbBits()
    {
        var disabled = WithChange(WatchEventKind.AccountDisabled, "userAccountControl", ["512"], ["514"]);
        var acbDisabled = Account(4738, T, Admin, extra: [("OldUacValue", "0x10"), ("NewUacValue", "0x11")]);
        var acbNoPreauth = Account(4738, T, Admin, extra: [("OldUacValue", "0x10"), ("NewUacValue", "0x10010")]);
        var scriptPath = Account(4738, T, Admin, extra: [("ScriptPath", "logon.bat")]);

        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(acbDisabled, disabled));
        Assert.Equal(CandidateFit.Unconfirmed, AttributionConfirmation.Assess(acbNoPreauth, disabled));
        Assert.Equal(CandidateFit.Contradicts, AttributionConfirmation.Assess(scriptPath, disabled));
        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(scriptPath, Create(WatchEventKind.AttributesChanged, changedAttributes: ["scriptPath"])));
        Assert.Equal(CandidateFit.Unconfirmed, AttributionConfirmation.Assess(Account(4738, T, Admin), Create(WatchEventKind.AttributesChanged, changedAttributes: ["department"])));
    }

    [Fact]
    public void MicrosoftSample4738_ConfirmsTheDontExpirePasswordFlag()
    {
        var sample = Parse(SecurityEventFixtures.UserChanged4738);
        var flags = Create(WatchEventKind.AccountFlagsChanged) with
        {
            Changes = [new AttributeChange { Attribute = "userAccountControl", OldValues = ["546"], NewValues = ["66050"], FlagsAdded = ["DONT_EXPIRE_PASSWORD"], FlagsRemoved = ["PASSWD_NOTREQD"] }],
        };

        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(sample, flags));
    }

    [Fact]
    public void Membership_MemberSidMustMatch()
    {
        var added = Membership(WatchEventKind.GroupMemberAdded, "CN=Księgowość,OU=Grupy,DC=contoso,DC=test") with
        {
            Member = new ObjectRef { Dn = UserDn, Sid = UserSid, Kind = WatchObjectKind.User },
        };
        var record = Parse(SecurityEventXml.Membership(4728, NextId(), T, Dc1, DomainSid + "-1500", "Księgowość", UserSid, UserDn, Admin));
        var other = Parse(SecurityEventXml.Membership(4728, NextId(), T, Dc1, DomainSid + "-1500", "Księgowość", OtherAdminSid, "CN=x,DC=contoso,DC=test", Admin));

        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(record, added));
        Assert.Equal(CandidateFit.Contradicts, AttributionConfirmation.Assess(other, added));
    }

    [Fact]
    public void Move5139_OldAndNewDnMustMatch()
    {
        var moved = Create(WatchEventKind.ObjectMoved, "CN=Łukasz Wiśniewski,OU=Gdańsk,DC=contoso,DC=test") with { PreviousDn = UserDn };
        string Move(string from, string to) => SecurityEventXml.Create(5139, NextId(), T, Dc1,
            [("OldObjectDN", from), ("NewObjectDN", to), ("ObjectGUID", "{" + UserGuid + "}"), ("ObjectClass", "user"), .. Admin]);

        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(Parse(Move(UserDn, moved.Object.Dn)), moved));
        Assert.Equal(CandidateFit.Contradicts, AttributionConfirmation.Assess(Parse(Move(UserDn, "CN=Łukasz Wiśniewski,OU=Archiwum,DC=contoso,DC=test")), moved));
    }

    [Fact]
    public void Rename4781_NewNameMustMatch()
    {
        var renamed = WithChange(WatchEventKind.AccountNameChanged, "sAMAccountName", ["lwisniewski"], ["lukasz.wisniewski"]);

        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(Account(4781, T, Admin, extra: [("OldTargetUserName", "lwisniewski"), ("NewTargetUserName", "lukasz.wisniewski")]), renamed));
        Assert.Equal(CandidateFit.Contradicts, AttributionConfirmation.Assess(Account(4781, T, Admin, extra: [("OldTargetUserName", "lwisniewski"), ("NewTargetUserName", "inny")]), renamed));
    }

    [Theory]
    [InlineData(4720, WatchEventKind.UserCreated)]
    [InlineData(4722, WatchEventKind.AccountEnabled)]
    [InlineData(4725, WatchEventKind.AccountDisabled)]
    [InlineData(4767, WatchEventKind.AccountUnlocked)]
    [InlineData(4724, WatchEventKind.PasswordChanged)]
    [InlineData(4726, WatchEventKind.ObjectDeleted)]
    public void TheKindsOwnEvent_Confirms(int code, WatchEventKind kind) =>
        Assert.Equal(CandidateFit.Confirmed, AttributionConfirmation.Assess(Account(code, T, Admin), Create(kind)));
}
