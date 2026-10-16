namespace AdMng.Directory.IntegrationTests.Unit;

public sealed class VlvContextTests
{
    [Fact]
    public void RoundTrip_KeepsConnectionAndServerContext()
    {
        byte[] server = [0x10, 0x20, 0x30];

        var encoded = VlvContext.Encode(poolId: 4242, connectionId: 7, server);

        Assert.True(VlvContext.TryDecode(encoded, 4242, out var connection, out var decoded));
        Assert.Equal(7, connection);
        Assert.Equal(server, decoded);
    }

    [Fact]
    public void ContextOfAnotherPool_IsRejected()
    {
        var encoded = VlvContext.Encode(poolId: 1, connectionId: 7, [0x01]);

        Assert.False(VlvContext.TryDecode(encoded, 2, out _, out var server));
        Assert.Empty(server);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0xA7, 1, 0, 0, 0, 7, 0, 0, 0 })]
    public void ForeignOrEmptyValues_AreRejected(byte[]? value)
    {
        Assert.False(VlvContext.TryDecode(value, 1, out _, out _));
    }
}
