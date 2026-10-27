using AdMng.Core.Computers;

namespace AdMng.Core.Tests.Computers;

public class WindowsReleaseCatalogTests
{
    private static readonly DateOnly Today = new(2026, 10, 27);
    private readonly WindowsReleaseCatalog _catalog = new();

    private WindowsReleaseMatch Match(string? os, string? version, IReadOnlyCollection<WindowsReleaseOverride>? overrides = null, int soon = 90) =>
        _catalog.Match(os, version, Today, soon, overrides);

    [Theory]
    [InlineData("Windows 11 Pro", "10.0 (26200)", "Windows 11 25H2", "2027-10-12", SupportState.Supported)]
    [InlineData("Windows 11 Enterprise", "10.0 (26200)", "Windows 11 25H2", "2028-10-10", SupportState.Supported)]
    [InlineData("Windows 11 Pro", "10.0 (26100)", "Windows 11 24H2", "2026-10-13", SupportState.Ended)]
    [InlineData("Windows 11 Education", "10.0 (26100)", "Windows 11 24H2", "2027-10-12", SupportState.Supported)]
    [InlineData("Windows 11 Enterprise", "10.0 (22631)", "Windows 11 23H2", "2026-11-10", SupportState.EndingSoon)]
    [InlineData("Windows 11 Pro", "10.0 (22631)", "Windows 11 23H2", "2025-11-11", SupportState.Ended)]
    [InlineData("Windows 11 Enterprise", "10.0 (22621)", "Windows 11 22H2", "2025-10-14", SupportState.Ended)]
    [InlineData("Windows 11 Pro", "10.0 (22000)", "Windows 11 21H2", "2023-10-10", SupportState.Ended)]
    [InlineData("Windows 10 Pro", "10.0 (19045)", "Windows 10 22H2", "2025-10-14", SupportState.Ended)]
    [InlineData("Windows 10 Enterprise LTSC", "10.0 (19044)", "Windows 10 Enterprise LTSC 2021", "2027-01-12", SupportState.EndingSoon)]
    [InlineData("Windows 10 IoT Enterprise LTSC", "10.0 (19044)", "Windows 10 IoT Enterprise LTSC 2021", "2032-01-13", SupportState.Supported)]
    [InlineData("Windows 10 Enterprise LTSC 2019", "10.0 (17763)", "Windows 10 Enterprise LTSC 2019", "2029-01-09", SupportState.Supported)]
    [InlineData("Windows 10 Enterprise 2016 LTSB", "10.0 (14393)", "Windows 10 Enterprise LTSB 2016", "2026-10-13", SupportState.Ended)]
    [InlineData("Windows 11 Enterprise LTSC", "10.0 (26100)", "Windows 11 Enterprise LTSC 2024", "2029-10-09", SupportState.Supported)]
    [InlineData("Windows 11 IoT Enterprise LTSC", "10.0 (26100)", "Windows 11 IoT Enterprise LTSC 2024", "2034-10-10", SupportState.Supported)]
    [InlineData("Windows Server 2025 Datacenter", "10.0 (26100)", "Windows Server 2025", "2034-11-14", SupportState.Supported)]
    [InlineData("Windows Server 2022 Standard", "10.0 (20348)", "Windows Server 2022", "2031-10-14", SupportState.Supported)]
    [InlineData("Windows Server 2019 Standard", "10.0 (17763)", "Windows Server 2019", "2029-01-09", SupportState.Supported)]
    [InlineData("Windows Server 2016 Standard", "10.0 (14393)", "Windows Server 2016", "2027-01-12", SupportState.EndingSoon)]
    [InlineData("Windows Server 2012 R2 Standard", "6.3 (9600)", "Windows Server 2012 R2", "2023-10-10", SupportState.Ended)]
    [InlineData("Windows 7 Professional", "6.1 (7601)", "Windows 7 SP1", "2020-01-14", SupportState.Ended)]
    [InlineData("Windows 8.1 Pro", "6.3 (9600)", "Windows 8.1", "2023-01-10", SupportState.Ended)]
    public void Match_UsesTheBuildAndTheEditionClass(string os, string version, string release, string end, SupportState state)
    {
        var match = Match(os, version);

        Assert.Equal(release, match.Release?.Release);
        Assert.Equal(DateOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture), match.EndOfSupport);
        Assert.Equal(state, match.State);
    }

    [Fact]
    public void EarlyWindows11_ReportedAsWindows10_IsWindows11ByItsBuild()
    {
        var match = Match("Windows 10 Enterprise", "10.0 (22000)");

        Assert.Equal("Windows 11 21H2", match.Release!.Release);
        Assert.Equal(EditionClass.EnterpriseEducation, match.Edition);
        Assert.Equal(SupportState.Ended, match.State);
    }

    [Theory]
    [InlineData("Windows 11 Pro Education", EditionClass.HomePro)]
    [InlineData("Windows 11 Pro for Workstations", EditionClass.HomePro)]
    [InlineData("Windows 11 Home", EditionClass.HomePro)]
    [InlineData("Windows 11 Education", EditionClass.EnterpriseEducation)]
    [InlineData("Windows 11 Enterprise multi-session", EditionClass.EnterpriseEducation)]
    [InlineData("Windows 10 Enterprise LTSC", EditionClass.Ltsc)]
    [InlineData("Windows 10 IoT Enterprise LTSC", EditionClass.IotLtsc)]
    [InlineData("Windows Server 2025 Datacenter Azure Edition", EditionClass.Server)]
    [InlineData("Windows Storage Server 2016", EditionClass.Server)]
    public void EditionOf_ClassifiesTheProductString(string os, EditionClass expected) =>
        Assert.Equal(expected, WindowsReleaseCatalog.EditionOf(os));

    [Theory]
    [InlineData("Samba")]
    [InlineData("Ubuntu 24.04 LTS")]
    [InlineData("Mac OS X")]
    [InlineData("")]
    [InlineData(null)]
    public void NonWindowsStrings_AreNotWindows(string? os)
    {
        var match = Match(os, "4.19.5-Ubuntu");

        Assert.Equal(SupportState.NotWindows, match.State);
        Assert.Null(match.Release);
        Assert.Null(match.Edition);
    }

    [Fact]
    public void UnknownBuilds_AreUnknown_UnlessAnEndedReleaseFollowsThem()
    {
        Assert.Equal(SupportState.Unknown, Match("Windows 11 Pro", "10.0 (26400)").State);
        Assert.Equal(SupportState.Unknown, Match("Windows 11 Pro", "10.0").State);
        Assert.Equal(SupportState.Unknown, Match("Windows 11 Pro", null).State);

        // Windows 10 1809 Pro and Server 1903 (no rows) are older than releases that have ended.
        Assert.Equal(SupportState.Ended, Match("Windows 10 Pro", "10.0 (17763)").State);
        Assert.Equal(SupportState.Ended, Match("Windows Server Datacenter", "10.0 (18362)").State);
    }

    [Fact]
    public void ReleaseWithoutConfirmedDate_IsKnownButUnknownState()
    {
        var match = Match("Windows 11 Pro", "10.0 (28000)");

        Assert.Equal("Windows 11 26H1", match.Release!.Release);
        Assert.Equal(SupportState.Unknown, match.State);
        Assert.Null(match.EndOfSupport);
    }

    [Fact]
    public void Overrides_Win()
    {
        var esu = new WindowsReleaseOverride(19045, EditionClass.EnterpriseEducation, new DateOnly(2028, 10, 10));

        var match = Match("Windows 10 Enterprise", "10.0 (19045)", [esu]);
        var pro = Match("Windows 10 Pro", "10.0 (19045)", [esu]);

        Assert.Equal(SupportState.Supported, match.State);
        Assert.True(match.IsOverridden);
        Assert.Equal(new DateOnly(2028, 10, 10), match.EndOfSupport);
        Assert.Equal(SupportState.Ended, pro.State);
        Assert.False(pro.IsOverridden);
    }

    [Fact]
    public void EndingSoon_FollowsTheHorizon()
    {
        Assert.Equal(SupportState.EndingSoon, Match("Windows 11 Enterprise", "10.0 (22631)", soon: 14).State);
        Assert.Equal(SupportState.Supported, Match("Windows 11 Enterprise", "10.0 (22631)", soon: 13).State);
        Assert.Equal(14, Match("Windows 11 Enterprise", "10.0 (22631)").DaysLeft);
        Assert.Equal(-14, Match("Windows 11 Pro", "10.0 (26100)").DaysLeft);
    }

    [Fact]
    public void ValidAsOf_IsSet_AndEveryRowHasAName()
    {
        Assert.Equal(new DateOnly(2026, 10, 27), WindowsReleaseCatalog.ValidAsOf);
        Assert.All(_catalog.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Release)));
        Assert.Equal(_catalog.Rows.Count, _catalog.Rows.Select(r => (r.Build, r.Edition)).Distinct().Count());
    }

    [Theory]
    [InlineData("10.0 (26100)", 10, 0, 26100)]
    [InlineData("6.3 (9600)", 6, 3, 9600)]
    [InlineData("10.0.19045", 10, 0, 19045)]
    public void OperatingSystemVersion_Parses(string text, int major, int minor, int build)
    {
        Assert.True(OperatingSystemVersion.TryParse(text, out var version));
        Assert.Equal(new OperatingSystemVersion(major, minor, build), version);
    }

    [Theory]
    [InlineData("10.0 (abc)")]
    [InlineData("Samba")]
    [InlineData("10")]
    public void OperatingSystemVersion_RefusesOtherValues(string text) => Assert.False(OperatingSystemVersion.TryParse(text, out _));

    [Fact]
    public void OperatingSystemVersion_WithoutABuild_HasNone()
    {
        Assert.True(OperatingSystemVersion.TryParse("4.19.5-Ubuntu", out var version));
        Assert.Null(version!.Build);
        Assert.Equal("4.19", version.ToString());
    }
}
