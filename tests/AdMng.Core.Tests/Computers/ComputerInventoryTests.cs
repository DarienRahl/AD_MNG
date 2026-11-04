using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers;
using AdMng.Core.Model;

namespace AdMng.Core.Tests.Computers;

/// <summary>A fixed clock: Tuesday 27 October 2026, 10:00 in Warsaw.</summary>
public class ComputerInventoryTests
{
    private const string Domain = "DC=contoso,DC=test";
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static readonly ComputerInventoryOptions Options = new()
    {
        Placement = new PlacementRules($"CN=Computers,{Domain}", $"OU=Domain Controllers,{Domain}"),
    };

    private static ComputerFacts Facts(string name, string? os, string? version, string? parent = null, DateTimeOffset? lastLogon = null,
        UserAccountControl uac = UserAccountControl.WorkstationTrustAccount, DateTimeOffset? created = null)
    {
        var dn = $"CN={name},{parent ?? $"OU=Komputery,OU=Firma,{Domain}"}";
        var attributes = new List<AttributeValues>
        {
            new(Attr.ObjectClass, ["top", "computer"]),
            new(Attr.Cn, [name]),
            new(Attr.UserAccountControl, [((int)uac).ToString(System.Globalization.CultureInfo.InvariantCulture)]),
            new(Attr.PrimaryGroupId, [uac.HasFlag(UserAccountControl.ServerTrustAccount) ? "516" : "515"]),
            new(Attr.WhenCreated, [AdConvert.ToGeneralizedTime(created ?? new DateTimeOffset(2024, 3, 1, 8, 0, 0, TimeSpan.Zero))]),
            new(Attr.Description, ["Stanowisko księgowej – Gdańsk"]),
        };
        if (os is not null)
        {
            attributes.Add(new(Attr.OperatingSystem, [os]));
        }

        if (version is not null)
        {
            attributes.Add(new(Attr.OperatingSystemVersion, [version]));
        }

        if (lastLogon is { } logon)
        {
            attributes.Add(new(Attr.LastLogonTimestamp, [AdConvert.ToFileTime(logon).ToString(System.Globalization.CultureInfo.InvariantCulture)]));
        }

        return ComputerFacts.From(new DirectoryObject(dn, attributes));
    }

    [Fact]
    public void Windows11Pro24H2_EndedTwoWeeksAgo()
    {
        var status = ComputerInventory.Evaluate(Facts("WAW-PC001", "Windows 11 Pro", "10.0 (26100)"), Now, Warsaw, Options);

        Assert.Equal(SupportState.Ended, status.Release.State);
        Assert.Equal(-14, status.Release.DaysLeft);
        Assert.Equal("Windows 11 24H2", status.Release.Release!.Release);
    }

    [Fact]
    public void Windows11Enterprise23H2_EndsIn14Days()
    {
        var status = ComputerInventory.Evaluate(Facts("KRK-PC002", "Windows 11 Enterprise", "10.0 (22631)"), Now, Warsaw, Options);

        Assert.Equal(SupportState.EndingSoon, status.Release.State);
        Assert.Equal(14, status.Release.DaysLeft);
    }

    [Fact]
    public void DaysSinceLogon_AreCalendarDaysInTheOperatorsZone()
    {
        // 23:30 UTC on 16 October is 01:30 on 17 October in Warsaw: 10 days ago.
        var facts = Facts("GDA-LT003", "Windows 11 Enterprise", "10.0 (26200)", lastLogon: new DateTimeOffset(2026, 10, 16, 23, 30, 0, TimeSpan.Zero));

        var status = ComputerInventory.Evaluate(facts, Now, Warsaw, Options);

        Assert.Equal(10, status.DaysSinceLogon);
        Assert.False(status.NeverLoggedOn);
    }

    [Fact]
    public void ExactLastLogon_ReplacesTheReplicatedValue_ButNeverMakesItOlder()
    {
        var replicated = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var facts = Facts("WRO-PC004", "Windows 11 Pro", "10.0 (26200)", lastLogon: replicated);

        var newer = ComputerInventory.Evaluate(facts, Now, Warsaw, Options, new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero), hasExactLastLogon: true);
        var older = ComputerInventory.Evaluate(facts, Now, Warsaw, Options, new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero), hasExactLastLogon: true);

        Assert.Equal(1, newer.DaysSinceLogon);
        Assert.Equal(replicated, older.LastLogon);
    }

    [Fact]
    public void NeverLoggedOn_WithoutAnyLogon()
    {
        var status = ComputerInventory.Evaluate(Facts("POZ-PC005", null, null), Now, Warsaw, Options);

        Assert.True(status.NeverLoggedOn);
        Assert.Null(status.DaysSinceLogon);
        Assert.Equal(SupportState.NotWindows, status.Release.State);
        Assert.True(status.DaysSinceCreated > 900);
    }

    [Fact]
    public void Placement_IsEvaluated()
    {
        var status = ComputerInventory.Evaluate(Facts("WAW-PC006", "Windows 11 Pro", "10.0 (26200)", parent: $"CN=Computers,{Domain}"), Now, Warsaw, Options);

        Assert.Equal([PlacementFinding.NotMovedAfterJoin], status.Placement);
    }

    [Fact]
    public void Facts_ReadTheAccount()
    {
        var facts = Facts("DC02", "Windows Server 2025 Datacenter", "10.0 (26100)", parent: $"OU=Domain Controllers,{Domain}",
            uac: UserAccountControl.ServerTrustAccount);

        Assert.Equal(ComputerType.DomainController, facts.Type);
        Assert.Equal("DC02", facts.Name);
        Assert.Equal($"OU=Domain Controllers,{Domain}", facts.ParentDn);
        Assert.Equal(26100, facts.Version!.Build);
        Assert.True(facts.IsEnabled);
        Assert.Equal("Stanowisko księgowej – Gdańsk", facts.Description);
    }
}
