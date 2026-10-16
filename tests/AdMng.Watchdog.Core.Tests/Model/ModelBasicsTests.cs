using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Tests.Model;

public class ModelBasicsTests
{
    [Fact]
    public void NotificationTargetKey_IsStable_AndFreeOfAddresses()
    {
        var a = new NotificationTarget
        {
            ChannelType = DeliveryChannelType.Email,
            ChannelId = "email",
            Recipients = ["helpdesk@contoso.test", "Zofia.Zak@contoso.test"],
        };
        var b = a with { Recipients = ["zofia.zak@contoso.test", "helpdesk@contoso.test", "HELPDESK@contoso.test"] };
        var other = a with { Recipients = ["admin@contoso.test"] };

        Assert.Equal(a.Key, b.Key);
        Assert.NotEqual(a.Key, other.Key);
        Assert.StartsWith("email|", a.Key, StringComparison.Ordinal);
        Assert.Matches("^email\\|[0-9a-f]{32}$", a.Key);
        Assert.DoesNotContain("contoso", a.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void Secrets_NeverAppearInToString()
    {
        var value = new SecretValue("Tajne!Hasło#2026");

        Assert.Equal("Tajne!Hasło#2026", value.Reveal());
        Assert.Equal("SecretValue(***)", value.ToString());
        Assert.DoesNotContain("Tajne", new StoredSecret(value, "smtp|smtp.contoso.test|587|watchdog|basic").ToString(), StringComparison.Ordinal);
        Assert.Equal("SecretChange(smtp.password, set)", new SecretChange("smtp.password", value, "smtp|x").ToString());
        Assert.Equal("SecretChange(smtp.password, remove)", new SecretChange("smtp.password", null, null).ToString());
        Assert.Throws<ArgumentNullException>(() => new SecretValue(null!));
    }

    [Fact]
    public void SnapshotToRef_CopiesTheIdentity()
    {
        var snapshot = new SnapshotObject
        {
            ObjectGuid = Guid.Parse("9b0f4e3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f"),
            Dn = "CN=Zofia Żak,OU=Księgowość,DC=contoso,DC=test",
            Kind = WatchObjectKind.User,
            Sid = "S-1-5-21-1-2-3-1106",
            SamAccountName = "zofia.zak",
            Name = "Zofia Żak",
            DisplayName = "Zofia Żak (Księgowość)",
        };

        var reference = snapshot.ToRef();

        Assert.Equal(snapshot.ObjectGuid, reference.ObjectGuid);
        Assert.Equal(snapshot.Dn, reference.Dn);
        Assert.Equal(snapshot.Kind, reference.Kind);
        Assert.Equal(snapshot.Sid, reference.Sid);
        Assert.Equal(snapshot.SamAccountName, reference.SamAccountName);
        Assert.Equal(snapshot.Name, reference.Name);
        Assert.Equal(snapshot.DisplayName, reference.DisplayName);
        Assert.True(snapshot.Attributes is Dictionary<string, SnapshotAttribute> { Comparer: var comparer }
            && comparer.Equals("department", "DEPARTMENT"));
    }
}
