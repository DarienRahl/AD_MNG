using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Runtime.ExceptionServices;
using AdMng.Directory.IntegrationTests.Infrastructure;

[assembly: AssemblyFixture(typeof(LdapErrorLog))]

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// What libldap itself said when a connection to the lab failed - for the lab job of CI, where a bind now and then ends
/// in "ServerUnavailable (LDAP 81)" although the Samba log shows no refused or dropped connection. The exception the
/// tests see carries the result code only; the library's own words (a TLS error, a refused or reset connection, a
/// timeout) are in <see cref="LdapException.ServerErrorMessage"/> and were lost. With the environment variable
/// <see cref="PathVariable"/> set, every connection-level LDAP error of the test process is appended to that file with
/// its time, so the job can print it next to what Samba logged in the same second. Without the variable nothing is
/// listened to and nothing is written.
/// </summary>
/// <remarks>
/// Only errors about the connection itself are recorded (server unavailable, local error, timeout, connect error):
/// their texts come from the client library and name no directory object. Errors the tests provoke on purpose - a wrong
/// password, a missing object - are not, and neither is an exception a test builds itself (the classification tests
/// throw their own "server down"): only what the LDAP library threw is a fact about a connection.
/// </remarks>
public sealed class LdapErrorLog : IDisposable
{
    /// <summary>The file the errors are appended to (the lab job of CI sets it).</summary>
    public const string PathVariable = "ADMNG_TEST_LDAP_ERROR_LOG";

    private const int ServerUnavailable = 81;
    private const int LocalError = 82;
    private const int Timeout = 85;
    private const int ConnectError = 91;

    private static readonly Lock Gate = new();
    private readonly string? _path;
    private readonly bool _alsoFromTests;

    public LdapErrorLog()
        : this(Environment.GetEnvironmentVariable(PathVariable))
    {
    }

    /// <summary>
    /// The log over a given file (the tests of the log itself; the variable belongs to the whole process), optionally
    /// also of exceptions that were not thrown by the LDAP library.
    /// </summary>
    internal LdapErrorLog(string? path, bool alsoFromTests = false)
    {
        _alsoFromTests = alsoFromTests;
        if (!string.IsNullOrWhiteSpace(path))
        {
            _path = path;
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        }
    }

    public void Dispose() => AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;

    /// <summary>
    /// One line of the file: the time first (UTC, the format the job cuts the second from), then the error. The system
    /// error number is the one of the thread's last failed system call at that moment - a hint, since the runtime may
    /// have made calls of its own after the library's (111 a refused connection, 104 a reset one, 110 a timeout, 4 an
    /// interrupted call, 32 a broken pipe).
    /// </summary>
    internal static string Describe(DateTime utcNow, int thread, LdapException error, int systemError) => string.Create(
        CultureInfo.InvariantCulture,
        $"{utcNow:yyyy-MM-dd HH:mm:ss.ffffff} thread {thread}: LDAP {error.ErrorCode} in {error.TargetSite?.Name ?? "?"} ({OneLine(error.Message)}); libldap: {OneLine(error.ServerErrorMessage)}; errno {systemError}");

    /// <summary>Was it thrown by the LDAP library itself (and not built by a test)?</summary>
    internal static bool IsFromTheLibrary(LdapException error) =>
        error.TargetSite?.DeclaringType?.Assembly == typeof(LdapException).Assembly;

    /// <summary>Is this an error about the connection itself?</summary>
    internal static bool IsAboutTheConnection(LdapException error) =>
        error.ErrorCode is ServerUnavailable or LocalError or Timeout or ConnectError;

    private static string OneLine(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "(nothing)" : text.ReplaceLineEndings(" ").Trim();

    private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (e.Exception is not LdapException error || !IsAboutTheConnection(error) || _path is null
            || !(_alsoFromTests || IsFromTheLibrary(error)))
        {
            return;
        }

        // First of all: every call from here on may change it.
        var systemError = System.Runtime.InteropServices.Marshal.GetLastSystemError();
        try
        {
            var line = Describe(DateTime.UtcNow, Environment.CurrentManagedThreadId, error, systemError);
            lock (Gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A diagnostic that cannot be written must not change what the test sees.
        }
    }
}
