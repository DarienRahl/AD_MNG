using System.DirectoryServices.Protocols;

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// The record of connection-level LDAP errors the lab job prints (<see cref="LdapErrorLog"/>): what is written, what is
/// not, and that nothing is listened to without a file. These tests need no lab.
/// </summary>
public sealed class LdapErrorLogTests
{
    [Fact]
    public void AConnectionError_IsWrittenWithItsTimeAndTheLibrarysWords_AndAnErrorOfTheDirectoryIsNot()
    {
        var path = Path.Combine(Path.GetTempPath(), "admng-ldap-errors-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using (new LdapErrorLog(path, alsoFromTests: true))
            {
                Throw(new LdapException(81, "The LDAP server is unavailable.", "TLS: the handshake\nwas broken off"));

                // A wrong password and a missing object are what tests provoke on purpose: not recorded.
                Throw(new LdapException(49, "The supplied credential is invalid.", "80090308: LdapErr: DSID-0C09044E"));
                Throw(new LdapException(32, "The object does not exist."));
                Throw(new InvalidOperationException("not an LDAP error"));

                Throw(new LdapException(85, "The operation was aborted because the client side timeout limit was exceeded."));
            }

            // After the log is disposed nothing is listened to any more.
            Throw(new LdapException(81, "The LDAP server is unavailable.", "later"));

            // Other tests of this process run side by side and may fail to connect as well: only this thread's lines count.
            var mine = $" thread {Environment.CurrentManagedThreadId}: ";
            var lines = File.ReadAllLines(path).Where(line => line.Contains(mine, StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, lines.Length);
            Assert.Contains("LDAP 81 in Throw (The LDAP server is unavailable.); libldap: TLS: the handshake was broken off; errno ", lines[0], StringComparison.Ordinal);
            Assert.EndsWith($"; test {TestContext.Current.Test!.TestDisplayName}", lines[0], StringComparison.Ordinal);
            Assert.Contains("LDAP 85 in Throw (The operation was aborted because the client side timeout limit was exceeded.); libldap: (nothing); errno ", lines[1], StringComparison.Ordinal);

            // The line starts with the UTC time in the form the job cuts the second from ("2026-11-12 23:43:11").
            Assert.True(
                DateTime.TryParseExact(lines[0][..19], "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out _),
                lines[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnExceptionATestBuiltItself_IsNotAFactAboutAConnection()
    {
        var path = Path.Combine(Path.GetTempPath(), "admng-ldap-errors-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            // The log as the job runs it: only what the LDAP library itself threw.
            using (new LdapErrorLog(path))
            {
                var built = new LdapException(81, "The LDAP server is unavailable.", "built by a test");
                Throw(built);
                Assert.False(LdapErrorLog.IsFromTheLibrary(built));
            }

            var mine = $" thread {Environment.CurrentManagedThreadId}: ";
            Assert.DoesNotContain(
                File.Exists(path) ? File.ReadAllLines(path) : [],
                line => line.Contains(mine, StringComparison.Ordinal) && line.Contains("built by a test", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WithoutAFile_NothingIsListenedTo()
    {
        using var log = new LdapErrorLog(null);

        // Nothing to assert on a file: the log must simply not get in the way of the exception.
        Action failingBind = () => throw new LdapException(81, "The LDAP server is unavailable.");
        var seen = Assert.Throws<LdapException>(failingBind);
        Assert.Equal(81, seen.ErrorCode);
    }

    [Theory]
    [InlineData(81, true)]
    [InlineData(82, true)]
    [InlineData(85, true)]
    [InlineData(91, true)]
    [InlineData(49, false)]
    [InlineData(32, false)]
    [InlineData(50, false)]
    public void OnlyErrorsAboutTheConnectionItself_AreRecorded(int code, bool recorded) =>
        Assert.Equal(recorded, LdapErrorLog.IsAboutTheConnection(new LdapException(code, "text")));

    private static void Throw(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception ex) when (ReferenceEquals(ex, exception))
        {
            // The first-chance notification is what the log listens to; the exception itself is not of interest here.
        }
    }
}
