using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting;

namespace AdMng.Watchdog.Core.Tests.Formatting;

public sealed class DetailLevelFilterTests
{
    [Fact]
    public void Full_KeepsEveryNonSecretValue_AndRedactsSecrets()
    {
        var filtered = DetailLevelFilter.Apply(FormattingSamples.AttributesChanged(), DetailLevel.Full);

        Assert.Equal(["Księgowość"], Change(filtered, "department").NewValues);
        Assert.Equal(["+48 600 100 201"], Change(filtered, "mobile").NewValues);
        var password = Change(filtered, "unicodePwd");
        Assert.True(password.Redacted);
        Assert.Empty(password.NewValues);
        Assert.Equal("WS-ADM-01", filtered.Attribution.Workstation);
    }

    [Fact]
    public void Standard_HidesPersonalData_KeepingCounts()
    {
        var filtered = DetailLevelFilter.Apply(FormattingSamples.AttributesChanged(), DetailLevel.Standard);

        var mobile = Change(filtered, "mobile");
        Assert.Empty(mobile.OldValues);
        Assert.Empty(mobile.NewValues);
        Assert.Equal(1, mobile.NewCount);
        Assert.Equal(ValueVisibility.Hidden, DetailLevelFilter.Visibility(mobile, DetailLevel.Standard));
        Assert.Equal(["Księgowość"], Change(filtered, "department").NewValues);
        Assert.Equal(["ACCOUNTDISABLE"], Change(filtered, "userAccountControl").FlagsAdded);
    }

    [Fact]
    public void Minimal_KeepsAttributeNamesOnly_NoSourceNoViaDns()
    {
        var granted = DetailLevelFilter.Apply(FormattingSamples.PrivilegeGranted(), DetailLevel.Minimal);
        var changed = DetailLevelFilter.Apply(FormattingSamples.AttributesChanged(), DetailLevel.Minimal);
        var locked = DetailLevelFilter.Apply(FormattingSamples.LockedOut(), DetailLevel.Minimal);

        Assert.All(changed.Changes, c => Assert.Empty(c.NewValues));
        Assert.All(changed.Changes, c => Assert.Empty(c.FlagsAdded));
        Assert.Equal(5, changed.Changes.Count);
        Assert.Null(granted.Attribution.Workstation);
        Assert.Null(granted.Attribution.IpAddress);
        Assert.Equal(@"CONTOSO\adm.nowak", granted.Attribution.Actor?.Account);
        Assert.Equal(["GG_Admins_Serwery", "Domain Admins"], granted.Via.Select(v => v.Dn));
        Assert.Empty(locked.Lockout!.Sources);
    }

    [Fact]
    public void SecretAttributes_AreHiddenEvenWhenNotMarkedRedacted()
    {
        var leaked = new AttributeChange { Attribute = "ms-Mcs-AdmPwd", NewValues = ["Tajne!Hasło1"], NewCount = 1 };
        var e = FormattingSamples.AttributesChanged() with { Changes = [leaked] };

        var filtered = DetailLevelFilter.Apply(e, DetailLevel.Full);

        Assert.True(filtered.Changes[0].Redacted);
        Assert.Empty(filtered.Changes[0].NewValues);
        Assert.Equal(ValueVisibility.Secret, DetailLevelFilter.Visibility(leaked, DetailLevel.Full));
    }

    [Fact]
    public void LongValues_EndWithAnEllipsis()
    {
        var e = FormattingSamples.AttributesChanged() with
        {
            Changes = [new AttributeChange { Attribute = "description", NewValues = [new string('ż', 300)], NewCount = 1 }],
        };

        var value = Assert.Single(DetailLevelFilter.Apply(e, DetailLevel.Full, maxValueLength: 50).Changes[0].NewValues);

        Assert.Equal(50, value.Length);
        Assert.EndsWith("…", value, StringComparison.Ordinal);
    }

    [Fact]
    public void ForNotification_SanitizesEveryDirectoryString()
    {
        var hostile = FormattingSamples.PrivilegeGranted() with
        {
            Object = FormattingSamples.Jan with { DisplayName = "Jan" + FormattingSamples.Rlo + "Kowalski" },
            Via = [new ObjectRef { Dn = "CN=www.zly.example,OU=Grupy,DC=contoso,DC=com", Name = "https://evil.example/grupa" }],
            AcknowledgeComment = "patrz https://evil.example",
        };

        var safe = DetailLevelFilter.ForNotification(hostile, DetailLevel.Full);

        Assert.Equal("Jan<U+202E>Kowalski", safe.Object.DisplayName);
        Assert.Equal("hxxps://evil[.]example/grupa", safe.Via[0].Name);
        Assert.Contains("www[.]zly", safe.Via[0].Dn, StringComparison.Ordinal);
        Assert.Equal("patrz hxxps://evil[.]example", safe.AcknowledgeComment);
    }

    [Fact]
    public void EventDetails_AreFilteredWithTheirRelatedEvents()
    {
        var details = new AdMng.Watchdog.Contracts.Api.EventDetails
        {
            Event = FormattingSamples.AttributesChanged(),
            RelatedEvents = [FormattingSamples.AttributesChanged()],
        };

        var filtered = DetailLevelFilter.Apply(details, DetailLevel.Standard);

        Assert.Empty(Change(filtered.Event, "mobile").NewValues);
        Assert.Empty(Change(filtered.RelatedEvents[0], "mobile").NewValues);
    }

    private static AttributeChange Change(WatchEvent e, string attribute) => e.Changes.Single(c => c.Attribute == attribute);
}
