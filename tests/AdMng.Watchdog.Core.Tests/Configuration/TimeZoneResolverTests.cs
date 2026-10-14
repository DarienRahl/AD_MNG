using AdMng.Watchdog.Core.Configuration;

namespace AdMng.Watchdog.Core.Tests.Configuration;

public class TimeZoneResolverTests
{
    private static readonly DateTime Winter = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Summer = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Europe/Warsaw")]
    [InlineData("Central European Standard Time")]
    [InlineData("  Europe/Warsaw  ")]
    public void ResolvesWarsaw_FromTheIanaOrTheWindowsId(string id)
    {
        Assert.True(TimeZoneResolver.TryResolve(id, out var zone));
        AssertWarsawRules(zone);
        AssertWarsawRules(TimeZoneResolver.Resolve(id));
    }

    [Fact]
    public void BuiltInMap_ResolvesIanaIds_OnAnNlsHostThatKnowsOnlyWindowsIds()
    {
        var lookup = OnlyKnows(
            ("Central European Standard Time", "Europe/Warsaw"),
            ("W. Europe Standard Time", "Europe/Berlin"),
            ("GMT Standard Time", "Europe/London"),
            ("UTC", "Etc/UTC"));

        Assert.True(TimeZoneResolver.TryResolve("Europe/Warsaw", lookup, convertWithRuntime: false, out var warsaw));
        AssertWarsawRules(warsaw);
        Assert.True(TimeZoneResolver.TryResolve("europe/warsaw", lookup, convertWithRuntime: false, out _));
        Assert.True(TimeZoneResolver.TryResolve("Europe/Berlin", lookup, convertWithRuntime: false, out var berlin));
        Assert.Equal(TimeSpan.FromHours(2), berlin.GetUtcOffset(Summer));
        Assert.True(TimeZoneResolver.TryResolve("Europe/London", lookup, convertWithRuntime: false, out var london));
        Assert.Equal(TimeSpan.Zero, london.GetUtcOffset(Winter));
        Assert.Equal(TimeSpan.FromHours(1), london.GetUtcOffset(Summer));
        Assert.True(TimeZoneResolver.TryResolve("Etc/UTC", lookup, convertWithRuntime: false, out var utc));
        Assert.Equal(TimeSpan.Zero, utc.GetUtcOffset(Summer));
    }

    [Fact]
    public void BuiltInMap_ResolvesWindowsIds_OnAHostThatKnowsOnlyIanaIds()
    {
        var lookup = OnlyKnows(("Europe/Warsaw", "Europe/Warsaw"), ("Europe/London", "Europe/London"));

        Assert.True(TimeZoneResolver.TryResolve("Central European Standard Time", lookup, convertWithRuntime: false, out var warsaw));
        AssertWarsawRules(warsaw);
        Assert.True(TimeZoneResolver.TryResolve("gmt standard time", lookup, convertWithRuntime: false, out var london));
        Assert.Equal(TimeSpan.FromHours(1), london.GetUtcOffset(Summer));
    }

    [Fact]
    public void Utc_ResolvesEvenWithoutAnyTimeZoneData()
    {
        Assert.True(TimeZoneResolver.TryResolve("UTC", _ => null, convertWithRuntime: false, out var utc));
        Assert.Same(TimeZoneInfo.Utc, utc);
        Assert.True(TimeZoneResolver.TryResolve("Etc/UTC", _ => null, convertWithRuntime: false, out _));
    }

    [Fact]
    public void MappedIdsAreNotGuessed_WhenTheHostKnowsNeitherForm()
    {
        Assert.False(TimeZoneResolver.TryResolve("Europe/Warsaw", _ => null, convertWithRuntime: false, out var zone));
        Assert.Null(zone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Europa/Łódź")]
    [InlineData("Środkowoeuropejski czas standardowy")]
    [InlineData("../../etc/passwd")]
    [InlineData("Europe/Warsaw\0")]
    public void UnknownOrEmptyIds_AreNotResolved_AndNeverThrow(string? id)
    {
        Assert.False(TimeZoneResolver.TryResolve(id, out var zone));
        Assert.Null(zone);
    }

    [Fact]
    public void Resolve_ThrowsForUnknownAndEmptyIds()
    {
        var error = Assert.Throws<TimeZoneNotFoundException>(() => TimeZoneResolver.Resolve("Europa/Łódź"));
        Assert.Contains("Europa/Łódź", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => TimeZoneResolver.Resolve(" "));
        Assert.Throws<ArgumentNullException>(() => TimeZoneResolver.Resolve(null!));
    }

    private static void AssertWarsawRules(TimeZoneInfo zone)
    {
        Assert.Equal(TimeSpan.FromHours(1), zone.GetUtcOffset(Winter));
        Assert.Equal(TimeSpan.FromHours(2), zone.GetUtcOffset(Summer));
        // 2026 DST change in Poland: last Sunday of March / October.
        Assert.Equal(TimeSpan.FromHours(1), zone.GetUtcOffset(new DateTime(2026, 3, 29, 0, 59, 0, DateTimeKind.Utc)));
        Assert.Equal(TimeSpan.FromHours(2), zone.GetUtcOffset(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(TimeSpan.FromHours(1), zone.GetUtcOffset(new DateTime(2026, 10, 25, 1, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>A system lookup that knows only the given ids; each is backed by the real zone of the second id
    /// (an NLS-mode Windows host knows "Central European Standard Time" but not "Europe/Warsaw").</summary>
    private static Func<string, TimeZoneInfo?> OnlyKnows(params (string Id, string BackingIanaId)[] zones)
    {
        var map = zones.ToDictionary(z => z.Id, z => TimeZoneInfo.FindSystemTimeZoneById(z.BackingIanaId), StringComparer.Ordinal);
        return id => map.GetValueOrDefault(id);
    }
}
