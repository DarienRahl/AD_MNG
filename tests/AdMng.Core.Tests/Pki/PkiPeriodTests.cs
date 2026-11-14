using AdMng.Core.Pki;

namespace AdMng.Core.Tests.Pki;

/// <summary>
/// <c>pKIExpirationPeriod</c> / <c>pKIOverlapPeriod</c> (docs/dev/p-certificate-services.md 2.2, 2.10): 8 bytes, little
/// endian, a negative 100-ns interval; shown in the largest exact unit (years of 365 days, months of 30, weeks, days, hours).
/// The byte vectors are the values AD stores for the default templates.
/// </summary>
public sealed class PkiPeriodTests
{
    [Fact]
    public void OneYear_IsTheDefaultValidity()
    {
        var period = PkiPeriod.Decode(Hex("00 40 39 87 2E E1 FE FF"));

        Assert.NotNull(period);
        Assert.Equal(TimeSpan.FromDays(365), period.Value.Duration);
        Assert.Equal(TimeSpan.FromDays(365).Ticks, period.Value.Ticks);
        Assert.Equal(PkiPeriodUnit.Years, period.Value.Unit);
        Assert.Equal(1, period.Value.Count);
    }

    [Fact]
    public void SixWeeks_IsTheDefaultRenewalOverlap()
    {
        var period = PkiPeriod.Decode(Hex("00 80 A6 0A FF DE FF FF"));

        Assert.NotNull(period);
        Assert.Equal(TimeSpan.FromDays(42), period.Value.Duration);
        Assert.Equal(PkiPeriodUnit.Weeks, period.Value.Unit);
        Assert.Equal(6, period.Value.Count);
    }

    [Theory]
    [InlineData("00 80 72 0E 5D C2 FD FF", PkiPeriodUnit.Years, 2)] // Web Server: 2 years
    [InlineData("00 40 1E A4 E8 65 FA FF", PkiPeriodUnit.Years, 5)] // Subordinate CA: 5 years
    [InlineData("00 80 37 AE FF F4 FF FF", PkiPeriodUnit.Weeks, 2)] // 14 days
    [InlineData("00 C0 1B D7 7F FA FF FF", PkiPeriodUnit.Weeks, 1)]
    public void DefaultTemplateValues_PickTheLargestExactUnit(string hex, PkiPeriodUnit unit, long count)
    {
        var period = PkiPeriod.Decode(Hex(hex));

        Assert.NotNull(period);
        Assert.Equal(unit, period.Value.Unit);
        Assert.Equal(count, period.Value.Count);
    }

    [Theory]
    [InlineData(730, PkiPeriodUnit.Years, 2)]
    [InlineData(2190, PkiPeriodUnit.Years, 6)] // also 73 months: years win
    [InlineData(90, PkiPeriodUnit.Months, 3)]
    [InlineData(210, PkiPeriodUnit.Months, 7)] // also 30 weeks: months win
    [InlineData(366, PkiPeriodUnit.Days, 366)]
    [InlineData(10, PkiPeriodUnit.Days, 10)]
    public void Days_PickTheLargestExactUnit(int days, PkiPeriodUnit unit, long count)
    {
        var period = PkiPeriod.Decode(Stored(-TimeSpan.FromDays(days).Ticks));

        Assert.Equal(unit, period!.Value.Unit);
        Assert.Equal(count, period.Value.Count);
    }

    [Fact]
    public void Hours_AndOddLengths()
    {
        var hours = PkiPeriod.Decode(Stored(-TimeSpan.FromHours(36).Ticks))!.Value;
        Assert.Equal(PkiPeriodUnit.Hours, hours.Unit);
        Assert.Equal(36, hours.Count);

        var odd = PkiPeriod.Decode(Stored(-TimeSpan.FromMinutes(90).Ticks))!.Value;
        Assert.Null(odd.Unit);
        Assert.Equal(0, odd.Count);
        Assert.Equal(TimeSpan.FromMinutes(90), odd.Duration);

        var zero = PkiPeriod.Decode(new byte[8])!.Value;
        Assert.Equal(TimeSpan.Zero, zero.Duration);
        Assert.Null(zero.Unit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(16)]
    public void AValueThatIsNotEightBytes_IsNull(int length) =>
        Assert.Null(PkiPeriod.Decode(new byte[length]));

    [Fact]
    public void APositiveOrUnnegatableValue_IsNull()
    {
        Assert.Null(PkiPeriod.Decode(Stored(TimeSpan.FromDays(365).Ticks)));
        Assert.Null(PkiPeriod.Decode(Stored(long.MinValue)));
        Assert.NotNull(PkiPeriod.Decode(Stored(long.MinValue + 1)));
    }

    [Fact]
    public void ToString_CarriesNoValue() =>
        Assert.Equal("PkiPeriod", PkiPeriod.Decode(Hex("00 40 39 87 2E E1 FE FF"))!.Value.ToString());

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    private static byte[] Stored(long value)
    {
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }
}
