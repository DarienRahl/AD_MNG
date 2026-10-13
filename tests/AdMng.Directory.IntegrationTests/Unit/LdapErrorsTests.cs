using System.DirectoryServices.Protocols;
using System.Net.Sockets;
using AdMng.Core.Directory;
using DirectoryException = AdMng.Core.Directory.DirectoryException;

namespace AdMng.Directory.IntegrationTests.Unit;

public sealed class LdapErrorsTests
{
    [Theory]
    [InlineData(81, DirectoryErrorKind.ServerUnavailable)]
    [InlineData(91, DirectoryErrorKind.ServerUnavailable)]
    [InlineData(82, DirectoryErrorKind.ServerUnavailable)]
    [InlineData(85, DirectoryErrorKind.Timeout)]
    [InlineData(87, DirectoryErrorKind.InvalidSyntax)]
    [InlineData(88, DirectoryErrorKind.Cancelled)]
    [InlineData(92, DirectoryErrorKind.UnsupportedControl)]
    [InlineData(8, DirectoryErrorKind.StrongAuthRequired)]
    [InlineData(49, DirectoryErrorKind.InvalidCredentials)]
    [InlineData(50, DirectoryErrorKind.AccessDenied)]
    public void LdapException_ErrorCodes_AreClassified(int code, DirectoryErrorKind expected)
    {
        var error = LdapErrors.Translate(new LdapException(code, "message"), OperationKind.Bind, null);

        Assert.NotNull(error);
        Assert.Equal(expected, error.Kind);
        Assert.Equal(code, error.LdapResultCode);
    }

    [Theory]
    [InlineData("80090308: LdapErr: DSID-0C09050F, comment: AcceptSecurityContext error, data 52e, v4f7c", DirectoryErrorKind.InvalidCredentials, 0x52E)]
    [InlineData("80090308: LdapErr: DSID-0C09050F, comment: AcceptSecurityContext error, data 775, v4f7c", DirectoryErrorKind.AccountRestriction, 0x775)]
    [InlineData("80090308: LdapErr: DSID-0C09050F, comment: AcceptSecurityContext error, data 532, v4f7c", DirectoryErrorKind.AccountRestriction, 0x532)]
    [InlineData("80090308: LdapErr: DSID-0C09050F, comment: AcceptSecurityContext error, data 533, v4f7c", DirectoryErrorKind.AccountRestriction, 0x533)]
    [InlineData("80090308: LdapErr: DSID-0C09050F, comment: AcceptSecurityContext error, data 701, v4f7c", DirectoryErrorKind.AccountRestriction, 0x701)]
    [InlineData("80090308: LdapErr: DSID-0C09050F, comment: AcceptSecurityContext error, data 773, v4f7c", DirectoryErrorKind.AccountRestriction, 0x773)]
    public void BindFailures_UseTheDataCode(string serverMessage, DirectoryErrorKind expected, int win32)
    {
        var error = LdapErrors.Translate(new LdapException(49, "The supplied credential is invalid.", serverMessage), OperationKind.Bind, null);

        Assert.NotNull(error);
        Assert.Equal(expected, error.Kind);
        Assert.Equal(win32, error.Win32Error);
        Assert.Equal(serverMessage, error.ServerMessage);
        Assert.DoesNotContain("DSID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Messages_ContainNoDistinguishedName()
    {
        const string dn = "CN=Jan Kowalski,OU=Księgowość,DC=contoso,DC=com";
        var error = LdapErrors.Translate(new LdapException(81, "down"), OperationKind.Modify, dn);

        Assert.NotNull(error);
        Assert.Equal(dn, error.DistinguishedName);
        Assert.DoesNotContain("Kowalski", error.Message, StringComparison.Ordinal);
        Assert.StartsWith("Modify failed", error.Message, StringComparison.Ordinal);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public void DirectoryOperationException_WithoutResponse_IsUnknown()
    {
        var error = LdapErrors.Translate(new DirectoryOperationException("bind failed"), OperationKind.Bind, null);

        Assert.NotNull(error);
        Assert.Equal(DirectoryErrorKind.Unknown, error.Kind);
        Assert.Equal(0, error.LdapResultCode);
        Assert.Equal("bind failed", error.ServerMessage);
    }

    [Fact]
    public void RequestNotSent_IsUnwrapped()
    {
        var notSent = new RequestNotSentException(new LdapException(81, "down"));

        Assert.True(LdapErrors.WasNotSent(notSent));
        Assert.True(LdapErrors.IsConnectionBroken(notSent));
        Assert.Equal(DirectoryErrorKind.ServerUnavailable, LdapErrors.Translate(notSent, OperationKind.Add, null)!.Kind);
        Assert.False(LdapErrors.WasNotSent(new LdapException(81)));
    }

    [Fact]
    public void ConnectionBroken_OnlyForTransportFailures()
    {
        Assert.True(LdapErrors.IsConnectionBroken(new LdapException(81)));
        Assert.True(LdapErrors.IsConnectionBroken(new LdapException(91)));
        Assert.False(LdapErrors.IsConnectionBroken(new LdapException(85)));
        Assert.False(LdapErrors.IsConnectionBroken(new DirectoryOperationException("x")));
        Assert.False(LdapErrors.IsConnectionBroken(new InvalidOperationException()));
    }

    [Fact]
    public void TimeoutAndSocketErrors_AreTranslated()
    {
        Assert.Equal(DirectoryErrorKind.Timeout, LdapErrors.Translate(new TimeoutException(), OperationKind.Bind, null)!.Kind);
        var socket = LdapErrors.Translate(new SocketException((int)SocketError.ConnectionRefused), OperationKind.Bind, null);
        Assert.Equal(DirectoryErrorKind.ServerUnavailable, socket!.Kind);
        Assert.Contains("ConnectionRefused", socket.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectoryExceptions_PassThrough_AndOtherExceptionsAreNotTranslated()
    {
        var original = new DirectoryException(DirectoryErrorKind.AccessDenied, "denied");

        Assert.Same(original, LdapErrors.Translate(original, OperationKind.Modify, null));
        Assert.Null(LdapErrors.Translate(new ArgumentException("x"), OperationKind.Modify, null));
        Assert.Null(LdapErrors.Translate(new ObjectDisposedException("x"), OperationKind.Modify, null));
    }

    [Fact]
    public void Describe_UsesResultCodeNames()
    {
        Assert.Equal(
            "Modify failed: InsufficientAccessRights (LDAP 50, Win32 0x5).",
            LdapErrors.Describe(OperationKind.Modify, 50, 5, DirectoryErrorKind.AccessDenied));
        Assert.Equal(
            "Search failed: ServerUnavailable (LDAP 81).",
            LdapErrors.Describe(OperationKind.Search, 81, null, DirectoryErrorKind.ServerUnavailable));
    }
}
