using AdMng.Core.ActiveDirectory;
using AdMng.Core.Computers;
using AdMng.Core.Model;

namespace AdMng.Core.Tests.Computers;

public class ComputerReportsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly ComputerReportParameters Parameters = new();

    [Fact]
    public void Filter_ExcludesManagedServiceAccounts_AndOrsThePreFilters()
    {
        var filter = ComputerReports.Filter([ComputerReport.Disabled, ComputerReport.WithoutOperatingSystem], "(name=WAW*)", Parameters, Now);

        Assert.StartsWith("(&(sAMAccountType=805306369)(!(|(objectClass=msDS-GroupManagedServiceAccount)", filter, StringComparison.Ordinal);
        Assert.Contains("(name=WAW*)", filter, StringComparison.Ordinal);
        Assert.Contains("(|(userAccountControl:1.2.840.113556.1.4.803:=2)(!(operatingSystem=*)))", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void Filter_WithAReportWithoutPreFilter_ReadsTheWholeScope()
    {
        var filter = ComputerReports.Filter([ComputerReport.Disabled, ComputerReport.UnexpectedContainer], null, Parameters, Now);

        Assert.DoesNotContain("userAccountControl", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void PreFilters_UseTheCutoff()
    {
        var inactive = ComputerReports.PreFilter(ComputerReport.Inactive, Parameters with { InactiveDays = 30 }, Now)!;
        var never = ComputerReports.PreFilter(ComputerReport.NeverLoggedOn, Parameters with { InactiveDays = 30 }, Now)!;
        var servers = ComputerReports.PreFilter(ComputerReport.ServersWithoutOwner, Parameters, Now)!;

        Assert.Contains($"(lastLogonTimestamp<={AdConvert.ToFileTime(Now.AddDays(-30))})", inactive, StringComparison.Ordinal);
        Assert.Contains("(!(lastLogonTimestamp=*))", never, StringComparison.Ordinal);
        Assert.Contains("(whenCreated<=20260927090000.0Z)", never, StringComparison.Ordinal);
        Assert.Contains("(operatingSystem=*Server*)", servers, StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_ServersWithoutOwner_OnlyWithAMissingOrDisabledOwner()
    {
        var server = Facts("SRV-PLIK01", "Windows Server 2022 Standard", "10.0 (20348)");
        var status = Status(server);

        Assert.True(ComputerReports.Matches(ComputerReport.ServersWithoutOwner, server, status, OwnerState.Missing, Parameters));
        Assert.True(ComputerReports.Matches(ComputerReport.ServersWithoutOwner, server, status, OwnerState.Disabled, Parameters));
        Assert.True(ComputerReports.Matches(ComputerReport.ServersWithoutOwner, server, status, OwnerState.Deleted, Parameters));
        Assert.False(ComputerReports.Matches(ComputerReport.ServersWithoutOwner, server, status, OwnerState.Present, Parameters));
        Assert.False(ComputerReports.Matches(ComputerReport.ServersWithoutOwner, server, status, OwnerState.Unknown, Parameters));
    }

    [Fact]
    public void Matches_EndOfSupport_SkipsDisabledComputers()
    {
        var enabled = Facts("WAW-PC010", "Windows 10 Pro", "10.0 (19045)");
        var disabled = Facts("WAW-PC011", "Windows 10 Pro", "10.0 (19045)", UserAccountControl.WorkstationTrustAccount | UserAccountControl.AccountDisable);

        Assert.True(ComputerReports.Matches(ComputerReport.EndOfSupport, enabled, Status(enabled), OwnerState.Unknown, Parameters));
        Assert.False(ComputerReports.Matches(ComputerReport.EndOfSupport, disabled, Status(disabled), OwnerState.Unknown, Parameters));
        Assert.True(ComputerReports.Matches(ComputerReport.Disabled, disabled, Status(disabled), OwnerState.Unknown, Parameters));
    }

    [Fact]
    public void ReleaseGroup_NamesTheRelease_TheBuildOrTheSystem()
    {
        var known = Facts("A", "Windows 11 Pro", "10.0 (26200)");
        var unknown = Facts("B", "Windows 11 Pro", "10.0 (26400)");
        var samba = Facts("C", "Samba", "4.19.5");

        Assert.Equal("Windows 11 25H2", ComputerReports.ReleaseGroup(known, Status(known)));
        Assert.Equal("Windows 11 Pro (26400)", ComputerReports.ReleaseGroup(unknown, Status(unknown)));
        Assert.Equal("Samba", ComputerReports.ReleaseGroup(samba, Status(samba)));
        Assert.Equal(ComputerReportArea.Order, ComputerReports.AreaOf(ComputerReport.ServersWithoutOwner));
    }

    private static ComputerStatus Status(ComputerFacts facts) => ComputerInventory.Evaluate(facts, Now, TimeZoneInfo.Utc, new ComputerInventoryOptions());

    private static ComputerFacts Facts(string name, string os, string version, UserAccountControl uac = UserAccountControl.WorkstationTrustAccount) =>
        ComputerFacts.From(new DirectoryObject($"CN={name},OU=Serwery,DC=contoso,DC=test",
        [
            new AttributeValues(Attr.ObjectClass, ["top", "computer"]),
            new AttributeValues(Attr.UserAccountControl, [((int)uac).ToString(System.Globalization.CultureInfo.InvariantCulture)]),
            new AttributeValues(Attr.OperatingSystem, [os]),
            new AttributeValues(Attr.OperatingSystemVersion, [version]),
        ]));
}
