namespace AdMng.Directory.IntegrationTests.Unit;

public sealed class AttributeRangeTests
{
    [Theory]
    [InlineData("member;range=0-1499", "member", 0, 1499)]
    [InlineData("member;Range=1500-2999", "member", 1500, 2999)]
    [InlineData("msDS-Members;range=10-19", "msDS-Members", 10, 19)]
    [InlineData("member;binary;range=0-4", "member", 0, 4)]
    [InlineData("member;range=0-4;x-option", "member", 0, 4)]
    public void FiniteRanges_AreParsed(string description, string name, int start, int end)
    {
        Assert.True(AttributeRange.TryParse(description, out var range));

        Assert.Equal(name, range.AttributeName);
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
        Assert.False(range.IsLast);
    }

    [Fact]
    public void OpenRange_IsTheLastChunk()
    {
        Assert.True(AttributeRange.TryParse("member;range=1500-*", out var range));

        Assert.Equal(1500, range.Start);
        Assert.Null(range.End);
        Assert.True(range.IsLast);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("member")]
    [InlineData(";range=0-1")]
    [InlineData("member;range=")]
    [InlineData("member;range=x-1")]
    [InlineData("member;range=5")]
    [InlineData("member;range=5-a")]
    [InlineData("member;range=-1-5")]
    [InlineData("member;range=10-3")]
    public void InvalidDescriptions_AreRejected(string? description)
    {
        Assert.False(AttributeRange.TryParse(description, out _));
    }

    [Fact]
    public void Requests_AreFormattedPerMsAdts()
    {
        Assert.Equal("member;range=0-*", AttributeRange.Request("member", 0));
        Assert.Equal("member;range=1500-*", AttributeRange.Request("member", 1500));
        Assert.Equal("member;range=500-999", AttributeRange.Request("member", 500, 500));
        Assert.Equal("member;range=0-0", AttributeRange.Request("member", 0, 1));
    }

    [Fact]
    public void InvalidRequests_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AttributeRange.Request("member", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AttributeRange.Request("member", 0, 0));
        Assert.Throws<ArgumentException>(() => AttributeRange.Request(" ", 0));
    }
}
