using AdMng.Core.GroupPolicy;

namespace AdMng.Core.Tests.GroupPolicy;

public class GpoVersionTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0x00030004, 3, 4)]
    [InlineData(0x00010000, 1, 0)]
    [InlineData(-65536, 0xFFFF, 0)]
    [InlineData(-1, 0xFFFF, 0xFFFF)]
    public void FromInt32_SplitsUserHighAndComputerLow(int raw, int user, int computer)
    {
        var version = GpoVersion.FromInt32(raw);

        Assert.Equal(user, version.User);
        Assert.Equal(computer, version.Computer);
        Assert.Equal(raw, version.ToInt32());
    }

    [Fact]
    public void Bump_RaisesOnlyTheNamedHalves()
    {
        var version = new GpoVersion(2, 7);

        Assert.Equal(new GpoVersion(2, 8), version.Bump(user: false, computer: true));
        Assert.Equal(new GpoVersion(3, 7), version.Bump(user: true, computer: false));
        Assert.Equal(new GpoVersion(3, 8), version.Bump(user: true, computer: true));
        Assert.Equal(version, version.Bump(user: false, computer: false));
    }

    [Fact]
    public void Bump_AtMaximum_ThrowsInsteadOfWrapping()
    {
        var version = new GpoVersion(GpoVersion.MaxHalf, 1);

        Assert.Throws<GpoVersionOverflowException>(() => version.Bump(user: true, computer: false));
        Assert.Equal(new GpoVersion(GpoVersion.MaxHalf, 2), version.Bump(user: false, computer: true));
    }

    [Fact]
    public void Max_TakesTheHigherHalfOfEach()
    {
        Assert.Equal(new GpoVersion(5, 9), GpoVersion.Max(new GpoVersion(5, 1), new GpoVersion(2, 9)));
        Assert.True(new GpoVersion(1, 9).IsBehind(new GpoVersion(2, 0)));
        Assert.False(new GpoVersion(2, 9).IsBehind(new GpoVersion(2, 9)));
    }
}
