using AdMng.Core.Computers.Remote;

namespace AdMng.Remote.Tests;

public class RemoteErrorsTests
{
    [Theory]
    [InlineData(0x80338126u, RemoteErrorKind.Unreachable)]
    [InlineData(1722u, RemoteErrorKind.Unreachable)]
    [InlineData(0x800706BAu, RemoteErrorKind.Unreachable)]
    [InlineData(12029u, RemoteErrorKind.Unreachable)]
    [InlineData(0x80338012u, RemoteErrorKind.WinRmNotAvailable)]
    [InlineData(0x800704C9u, RemoteErrorKind.WinRmNotAvailable)]
    [InlineData(0x80090322u, RemoteErrorKind.AuthenticationFailed)]
    [InlineData(0x80090324u, RemoteErrorKind.AuthenticationFailed)]
    [InlineData(0x8009030Eu, RemoteErrorKind.AuthenticationFailed)]
    [InlineData(0x803380E4u, RemoteErrorKind.AuthenticationFailed)]
    [InlineData(1326u, RemoteErrorKind.AuthenticationFailed)]
    [InlineData(0x80070005u, RemoteErrorKind.AccessDenied)]
    [InlineData(5u, RemoteErrorKind.AccessDenied)]
    [InlineData(0x80338029u, RemoteErrorKind.Timeout)]
    [InlineData(0x800705B4u, RemoteErrorKind.Timeout)]
    [InlineData(12002u, RemoteErrorKind.Timeout)]
    public void Classify_ByTheErrorInstanceCode(uint code, RemoteErrorKind expected) =>
        Assert.Equal(expected, RemoteErrors.Classify(1, code));

    [Theory]
    [InlineData(2, RemoteErrorKind.AccessDenied)]
    [InlineData(3, RemoteErrorKind.NotSupported)]
    [InlineData(5, RemoteErrorKind.NotSupported)]
    [InlineData(7, RemoteErrorKind.NotSupported)]
    [InlineData(17, RemoteErrorKind.NotSupported)]
    [InlineData(22, RemoteErrorKind.Timeout)]
    [InlineData(28, RemoteErrorKind.Unreachable)]
    [InlineData(1, RemoteErrorKind.Failed)]
    public void Classify_ByTheMiResultWhenTheCodeSaysNothing(int native, RemoteErrorKind expected)
    {
        Assert.Equal(expected, RemoteErrors.Classify(native, null));
        Assert.Equal(expected, RemoteErrors.Classify(native, 0x80041001u));
    }

    [Theory]
    [InlineData(0x80070005u, 5u)]
    [InlineData(0x800706BAu, 1722u)]
    [InlineData(0x80338126u, 0x80338126u)]
    [InlineData(1460u, 1460u)]
    public void Normalize_UnwrapsWin32Hresults(uint code, uint expected) => Assert.Equal(expected, RemoteErrors.Normalize(code));

    [Fact]
    public void FromReturnValue_KeepsTheCode_AndNoText()
    {
        var denied = RemoteErrors.FromReturnValue(2);
        var failed = RemoteErrors.FromReturnValue(1190);

        Assert.Equal(RemoteErrorKind.AccessDenied, denied.Kind);
        Assert.Equal(RemoteErrorKind.Failed, failed.Kind);
        Assert.Equal(1190, failed.ErrorCode);
        Assert.Equal("The remote operation failed.", failed.Message);
    }
}
