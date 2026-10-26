using AdMng.Core.ActiveDirectory;

namespace AdMng.Core.Tests.ActiveDirectory;

public class LogonHoursTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    private static readonly DayOfWeek[] Workdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// <summary>Monday–Friday, <paramref name="from"/>:00 to <paramref name="to"/>:00 in the grid's time.</summary>
    private static LogonHoursGrid OfficeHours(int from, int to) =>
        LogonHoursGrid.Create((day, hour) => Workdays.Contains(day) && hour >= from && hour < to);

    private static byte[] Bytes(params (int Index, byte Value)[] set)
    {
        var bytes = new byte[LogonHours.ByteLength];
        foreach (var (index, value) in set)
        {
            bytes[index] = value;
        }

        return bytes;
    }

    // ------------------------------------------------------------------ bit layout

    [Fact]
    public void BitZeroOfTheFirstByte_IsSundayMidnightUtc_AndBitsRunThroughTheWeek()
    {
        var sundayMidnight = LogonHours.FromBytes(Bytes((0, 0x01)));
        var sundaySeven = LogonHours.FromBytes(Bytes((0, 0x80)));
        var sundayEight = LogonHours.FromBytes(Bytes((1, 0x01)));
        var mondayMidnight = LogonHours.FromBytes(Bytes((3, 0x01)));
        var saturdayLastHour = LogonHours.FromBytes(Bytes((20, 0x80)));

        Assert.True(sundayMidnight.IsPermittedUtc(DayOfWeek.Sunday, 0));
        Assert.Equal(1, sundayMidnight.PermittedCount);
        Assert.True(sundaySeven.IsPermittedUtc(DayOfWeek.Sunday, 7));
        Assert.True(sundayEight.IsPermittedUtc(DayOfWeek.Sunday, 8));
        Assert.True(mondayMidnight.IsPermittedUtc(DayOfWeek.Monday, 0));
        Assert.True(mondayMidnight.IsPermittedUtc(24));
        Assert.True(saturdayLastHour.IsPermittedUtc(DayOfWeek.Saturday, 23));
        Assert.True(saturdayLastHour.IsPermittedUtc(LogonHours.HoursPerWeek - 1));
        Assert.False(saturdayLastHour.IsPermittedUtc(DayOfWeek.Sunday, 0));
    }

    [Fact]
    public void Bytes_And_Hex_RoundTrip()
    {
        var bytes = Bytes((0, 0x01), (4, 0xF0), (5, 0xFF), (20, 0x80));
        var hours = LogonHours.FromBytes(bytes);

        Assert.Equal(bytes, hours.ToBytes());
        Assert.Equal("01000000f0ff000000000000000000000000000080", hours.ToHex());
        Assert.Equal(hours.ToHex(), hours.ToString());
        Assert.True(LogonHours.TryParseHex(hours.ToHex(), out var parsed));
        Assert.Equal(hours, parsed);

        // ADUC's attribute editor shows upper case bytes separated by spaces.
        Assert.True(LogonHours.TryParseHex("01 00 00 00 F0 FF 00 00 00 00 00 00 00 00 00 00 00 00 00 00 80", out var spaced));
        Assert.Equal(hours, spaced);
        Assert.NotSame(bytes, hours.ToBytes());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ff")]
    [InlineData("ffffffffffffffffffffffffffffffffffffffffff00")]
    [InlineData("gfffffffffffffffffffffffffffffffffffffffff")]
    [InlineData("0x00ffffffffffffffffffffffffffffffffffffffff")]
    public void TryParseHex_RejectsAnythingButTwentyOneBytes(string? text)
    {
        Assert.False(LogonHours.TryParseHex(text, out var hours));
        Assert.Null(hours);
    }

    [Fact]
    public void FromBytes_RequiresExactlyTwentyOneBytes()
    {
        Assert.Throws<ArgumentException>(() => LogonHours.FromBytes(new byte[20]));
        Assert.False(LogonHours.TryFromBytes(new byte[22], out _));
        Assert.True(LogonHours.TryFromBytes(new byte[21], out var none));
        Assert.True(none.IsNonePermitted);
    }

    [Fact]
    public void AbsentAttribute_MeansAllHours_LikeAllOnes()
    {
        var allOnes = LogonHours.FromBytes(Enumerable.Repeat((byte)0xFF, LogonHours.ByteLength).ToArray());

        Assert.Equal(LogonHours.All, LogonHours.FromAttribute(null));
        Assert.Equal(LogonHours.All, LogonHours.FromAttribute([]));
        Assert.Equal(LogonHours.All, allOnes);
        Assert.True(allOnes.IsAllPermitted);
        Assert.Equal(LogonHours.All.GetHashCode(), allOnes.GetHashCode());
        Assert.Equal(new string('f', 42), LogonHours.All.ToHex());
        Assert.Equal(new string('0', 42), LogonHours.None.ToHex());
        Assert.True(LogonHours.None.IsNonePermitted);
        Assert.Equal(0, LogonHours.None.PermittedCount);
        Assert.Equal(LogonHours.HoursPerWeek, LogonHours.All.PermittedCount);
        Assert.Null(LogonHours.FromAttribute(new byte[7]));
        Assert.NotEqual(LogonHours.All, LogonHours.None);
    }

    // ------------------------------------------------------------------ local time

    [Fact]
    public void LocalGrid_IsShiftedByTheOffset_WarsawWinter()
    {
        // Monday–Friday 08:00–16:00 in Warsaw in winter (CET, UTC+1) = 07:00–15:00 UTC.
        var hours = LogonHours.FromLocalGrid(OfficeHours(8, 16), offsetHours: 1);

        Assert.Equal(40, hours.PermittedCount);
        Assert.False(hours.IsPermittedUtc(DayOfWeek.Monday, 6));
        Assert.True(hours.IsPermittedUtc(DayOfWeek.Monday, 7));
        Assert.True(hours.IsPermittedUtc(DayOfWeek.Friday, 14));
        Assert.False(hours.IsPermittedUtc(DayOfWeek.Friday, 15));

        var local = hours.ToLocalGrid(offsetHours: 1);
        Assert.True(local[DayOfWeek.Monday, 8]);
        Assert.True(local[DayOfWeek.Friday, 15]);
        Assert.False(local[DayOfWeek.Friday, 16]);
        Assert.False(local[DayOfWeek.Saturday, 10]);
    }

    [Fact]
    public void Rotation_WrapsAroundTheWeek()
    {
        // Local Sunday 00:00 at UTC+2 is Saturday 22:00 UTC; local Saturday 23:00 at UTC-5 is Sunday 04:00 UTC.
        var east = LogonHours.FromLocalGrid(LogonHoursGrid.Create((day, hour) => day == DayOfWeek.Sunday && hour == 0), offsetHours: 2);
        var west = LogonHours.FromLocalGrid(LogonHoursGrid.Create((day, hour) => day == DayOfWeek.Saturday && hour == 23), offsetHours: -5);

        Assert.True(east.IsPermittedUtc(DayOfWeek.Saturday, 22));
        Assert.Equal(1, east.PermittedCount);
        Assert.True(west.IsPermittedUtc(DayOfWeek.Sunday, 4));
        Assert.Equal(1, west.PermittedCount);
        Assert.True(east.ToLocalGrid(2)[DayOfWeek.Sunday, 0]);
        Assert.True(west.ToLocalGrid(-5)[DayOfWeek.Saturday, 23]);
    }

    [Fact]
    public void LocalGrid_RoundTripsForEveryOffset()
    {
        var random = new Random(168);
        var bytes = new byte[LogonHours.ByteLength];
        random.NextBytes(bytes);
        var hours = LogonHours.FromBytes(bytes);

        for (var offset = -12; offset <= 14; offset++)
        {
            var grid = hours.ToLocalGrid(offset);
            Assert.Equal(hours.PermittedCount, grid.PermittedCount);
            Assert.Equal(hours, LogonHours.FromLocalGrid(grid, offset));
        }

        Assert.Equal(LogonHours.All, LogonHours.FromLocalGrid(new LogonHoursGrid(permitted: true), 3));
        Assert.Equal(LogonHours.None, LogonHours.FromLocalGrid(new LogonHoursGrid(), -7));
    }

    [Fact]
    public void Grid_IsAnEditableCopy()
    {
        var grid = OfficeHours(8, 16);
        var copy = grid.Clone();
        copy[DayOfWeek.Saturday, 9] = true;

        Assert.False(grid[DayOfWeek.Saturday, 9]);
        Assert.True(copy[DayOfWeek.Saturday, 9]);
        Assert.Equal(41, copy.PermittedCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid[DayOfWeek.Monday, 24]);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid[(DayOfWeek)7, 0]);
    }

    [Theory]
    [InlineData("2026-01-15T12:00:00Z", 1)]
    [InlineData("2026-07-15T12:00:00Z", 2)]
    [InlineData("2026-03-29T00:59:59Z", 1)] // last second of CET
    [InlineData("2026-03-29T01:00:00Z", 2)] // 02:00 CET -> 03:00 CEST
    [InlineData("2026-10-25T00:59:59Z", 2)] // last second of CEST
    [InlineData("2026-10-25T01:00:00Z", 1)] // 03:00 CEST -> 02:00 CET
    public void Offset_IsTheCurrentOffsetOfTheZone_LikeAduc_AcrossBothWarsawDstSwitches(string instant, int expected)
    {
        Assert.Equal(expected, LogonHours.OffsetHours(Warsaw, DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Offset_IsTruncatedToWholeHours()
    {
        var india = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var newfoundland = TimeZoneInfo.FindSystemTimeZoneById("America/St_Johns");
        var winter = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(5, LogonHours.OffsetHours(india, winter));
        Assert.Equal(-3, LogonHours.OffsetHours(newfoundland, winter));
        Assert.Equal(0, LogonHours.OffsetHours(TimeZoneInfo.Utc, winter));
    }

    [Fact]
    public void ScheduleEnteredInWinter_IsShownOneHourLaterInSummer_BecauseTheDcAppliesUtc()
    {
        var winter = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);
        var summer = new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero);
        var stored = LogonHours.FromLocalGrid(OfficeHours(8, 16), LogonHours.OffsetHours(Warsaw, winter));

        var shownInSummer = stored.ToLocalGrid(LogonHours.OffsetHours(Warsaw, summer));
        var shownInWinter = stored.ToLocalGrid(LogonHours.OffsetHours(Warsaw, winter));

        Assert.False(shownInSummer[DayOfWeek.Monday, 8]);
        Assert.True(shownInSummer[DayOfWeek.Monday, 9]);
        Assert.True(shownInSummer[DayOfWeek.Monday, 16]);
        Assert.False(shownInSummer[DayOfWeek.Monday, 17]);
        Assert.True(shownInWinter[DayOfWeek.Monday, 8]);
        Assert.False(shownInWinter[DayOfWeek.Monday, 16]);
    }
}
