using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Service.Api;
using AdMng.Watchdog.Service.Tests.Infrastructure;

namespace AdMng.Watchdog.Service.Tests.Api;

/// <summary>Detail levels of API responses for non-Admins (spec 14.1).</summary>
public sealed class ViewerEventFilterTests
{
    private static readonly AttributeChange Department = new() { Attribute = "department", OldValues = ["Kadry"], NewValues = ["Księgowość"] };
    private static readonly AttributeChange Mobile = new() { Attribute = "mobile", NewValues = ["+48 601 234 567"], NewCount = 1 };
    private static readonly AttributeChange Flags = new() { Attribute = "userAccountControl", FlagsAdded = ["ACCOUNTDISABLE"], OldValues = ["512"], NewValues = ["514"] };
    private static readonly AttributeChange Laps = new() { Attribute = "msLAPS-Password", NewValues = ["P@ss"] };

    [Fact]
    public void Full_KeepsValues_ButNeverSecrets()
    {
        var filtered = ViewerEventFilter.Apply(Event(), DetailLevel.Full);

        Assert.Equal(["Księgowość"], filtered.Changes[0].NewValues);
        Assert.Equal(["+48 601 234 567"], filtered.Changes[1].NewValues);
        Assert.True(filtered.Changes[3].Redacted);
        Assert.Empty(filtered.Changes[3].NewValues);
    }

    [Fact]
    public void Standard_HidesPersonalData()
    {
        var filtered = ViewerEventFilter.Apply(Event(), DetailLevel.Standard);

        Assert.Equal(["Księgowość"], filtered.Changes[0].NewValues);
        Assert.True(filtered.Changes[1].Redacted);
        Assert.Empty(filtered.Changes[1].NewValues);
        Assert.Equal(1, filtered.Changes[1].NewCount);
        Assert.Equal(["514"], filtered.Changes[2].NewValues);
        Assert.Equal("WS-ŁÓDŹ-12", filtered.Attribution.Workstation);
    }

    [Fact]
    public void Minimal_KeepsNamesOnly()
    {
        var watchEvent = Event() with
        {
            Via = [new ObjectRef { Dn = "CN=Kadry Admins,OU=Grupy,DC=contoso,DC=test", Name = "Kadry Admins" }],
            Lockout = new LockoutInfo { LockedAt = TestStore.Start, CallerComputer = "WS-01", Sources = [new LockoutSource("10.0.0.7", "WS-01", 5, 4771)] },
        };

        var filtered = ViewerEventFilter.Apply(watchEvent, DetailLevel.Minimal);

        Assert.All(filtered.Changes, c => Assert.True(c.Redacted && c.NewValues.Count == 0 && c.FlagsAdded.Count == 0));
        Assert.Equal(["department", "mobile", "userAccountControl", "msLAPS-Password"], filtered.Changes.Select(c => c.Attribute));
        Assert.Null(filtered.Attribution.Workstation);
        Assert.Null(filtered.Attribution.IpAddress);
        Assert.Equal("Kadry Admins", Assert.Single(filtered.Via).Name);
        Assert.Equal(string.Empty, filtered.Via[0].Dn);
        Assert.Null(filtered.Lockout!.CallerComputer);
        Assert.Equal(5, Assert.Single(filtered.Lockout.Sources).Count);
        Assert.Null(filtered.Lockout.Sources[0].IpAddress);
    }

    [Fact]
    public void Details_FilterTheRelatedEventsToo()
    {
        var details = new EventDetails { Event = Event(), RelatedEvents = [Event()] };

        var filtered = ViewerEventFilter.Apply(details, DetailLevel.Standard);

        Assert.True(filtered.RelatedEvents[0].Changes[1].Redacted);
    }

    private static WatchEvent Event() =>
        TestData.Event(WatchEventKind.AttributesChanged, attribution: TestData.Resolved(), changes: [Department, Mobile, Flags, Laps]);
}
