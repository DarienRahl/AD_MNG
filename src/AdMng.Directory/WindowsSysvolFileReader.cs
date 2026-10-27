using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;

namespace AdMng.Directory;

/// <summary>
/// <see cref="ISysvolFileReader"/> of the WPF app (README U4c): reads <c>\\&lt;session DC&gt;\SYSVOL\&lt;domain&gt;\Policies\{GUID}\…</c>
/// - the DC of the session, never another one (CLAUDE.md rule 4) - into memory (rule 1: nothing is written to disk).
/// <list type="bullet">
/// <item>A session of the Windows account (<see cref="AuthenticationMode.CurrentUser"/>) reads with the process identity.</item>
/// <item>A session opened with other credentials gets a logon session of its own for the run: <c>LogonUser</c> with
/// <c>LOGON32_LOGON_NEW_CREDENTIALS</c> (as <c>runas /netonly</c>: the local identity stays, network access uses the
/// session's in-memory credential) and every file is opened while impersonating that token. The operator's logon session
/// is never given the credential, so its existing SMB sessions to the DC cannot conflict (no ERROR_SESSION_CREDENTIAL_CONFLICT),
/// no other process or window of the operator reaches the DC's shares with it, and nothing outlives the run (the token
/// is closed when the connection is disposed). The password goes to the call in a zeroed unmanaged buffer and is never
/// stored or logged (rule 2). <c>NEW_CREDENTIALS</c> does not check the password: opening SYSVOL checks it, so a refused
/// logon, a denied share or an unreachable DC is reported for the whole run.</item>
/// <item>A simple bind with a distinguished name has no logon name for SMB
/// (<see cref="SysvolUnavailableReason.DistinguishedNameLogon"/>); other platforms cannot read SYSVOL this way
/// (<see cref="SysvolUnavailableReason.NotSupported"/>). The tab says so.</item>
/// </list>
/// Opening a file is a blocking call: it runs on the thread pool, never on the UI thread (rule 6); reads are asynchronous
/// and cancellable, and a file larger than the caller's limit is not read.
/// </summary>
public sealed class WindowsSysvolFileReader : ISysvolFileReader
{
    public Task<ISysvolConnection> OpenAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!OperatingSystem.IsWindows())
        {
            throw new SysvolUnavailableException(SysvolUnavailableReason.NotSupported, "SYSVOL is read through Windows file sharing only.");
        }

        if (!SysvolPaths.TryNormalizeHost(session.Info.Server, out var server) || !SysvolPaths.TryNormalizeHost(session.Info.DomainDnsName, out var domain))
        {
            throw new SysvolUnavailableException(SysvolUnavailableReason.Failed, "The DC or domain name cannot be used in a UNC path.");
        }

        return OpenOnWindows(server, domain, SysvolLogon.Of(session), cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static Task<ISysvolConnection> OpenOnWindows(string server, string domain, SysvolLogon logon, CancellationToken cancellationToken) =>
        Task.Run(() => WindowsSysvol.Open(server, domain, logon), cancellationToken);
}

/// <summary>Who reads SYSVOL: the process identity (null user) or the session's credential (in memory only).</summary>
internal sealed class SysvolLogon
{
    private SysvolLogon(string? userName, NetworkCredential? credential)
    {
        UserName = userName;
        Credential = credential;
    }

    public static SysvolLogon CurrentUser { get; } = new(null, null);

    /// <summary><c>DOMAIN\user</c>, <c>user@domain</c> or <c>user</c>; null for the process identity.</summary>
    public string? UserName { get; }

    /// <summary>The session's credential (the password is read only when logging on).</summary>
    public NetworkCredential? Credential { get; }

    /// <summary>
    /// User and domain for <c>LogonUser</c>: <c>DOMAIN\user</c> and a credential with a domain are split, a UPN goes with no
    /// domain (null for the process identity).
    /// </summary>
    public (string User, string? Domain)? LogonParts => Credential is null ? null : SplitForLogon(Credential.UserName, Credential.Domain);

    /// <summary>User and domain for <c>LogonUser</c> from a credential's user name and domain.</summary>
    public static (string User, string? Domain) SplitForLogon(string userName, string? domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        if (!string.IsNullOrEmpty(domain))
        {
            return (userName, domain);
        }

        var backslash = userName.IndexOf('\\', StringComparison.Ordinal);
        return backslash > 0 && backslash < userName.Length - 1 ? (userName[(backslash + 1)..], userName[..backslash]) : (userName, null);
    }

    /// <summary>The logon of a session: its in-memory credential, or the Windows account.</summary>
    /// <exception cref="SysvolUnavailableException">A distinguished name logon, or the session was closed.</exception>
    public static SysvolLogon Of(IDirectorySession session)
    {
        if (session is not LdapDirectorySession ldap || ldap.Pool.Factory.Settings is not { } settings
            || settings.Options.Authentication == AuthenticationMode.CurrentUser)
        {
            return CurrentUser;
        }

        if (settings.Account is { Form: AccountNameForm.DistinguishedName })
        {
            throw new SysvolUnavailableException(SysvolUnavailableReason.DistinguishedNameLogon, "A distinguished name cannot log on to a file share.");
        }

        var credential = settings.Credential
            ?? throw new SysvolUnavailableException(SysvolUnavailableReason.Failed, "The session credential was released.");
        var user = string.IsNullOrEmpty(credential.Domain) ? credential.UserName : credential.Domain + "\\" + credential.UserName;
        return new SysvolLogon(user, credential);
    }

    /// <summary>Never the password.</summary>
    public override string ToString() => UserName ?? "(Windows account)";
}

/// <summary>Win32 errors of the logon and the first access to SYSVOL → why SYSVOL cannot be read (platform-neutral, testable).</summary>
internal static class SysvolErrors
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorBadNetPath = 53;
    private const int ErrorNetNameDeleted = 64;
    private const int ErrorBadNetName = 67;
    private const int ErrorInvalidPassword = 86;
    private const int ErrorNoNetOrBadPath = 1203;
    private const int ErrorNoNetwork = 1222;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorAccountRestriction = 1327;
    private const int ErrorPasswordExpired = 1330;
    private const int ErrorAccountDisabled = 1331;
    private const int ErrorAccountLockedOut = 1909;
    private const int Win32Facility = 7;

    /// <summary>The Win32 error of an exception whose HRESULT was made from one, else 0.</summary>
    internal static int Win32Error(Exception exception) =>
        ((exception.HResult >> 16) & 0x7FF) == Win32Facility ? exception.HResult & 0xFFFF : 0;

    internal static SysvolUnavailableReason ReasonOf(int error) => error switch
    {
        ErrorAccessDenied => SysvolUnavailableReason.AccessDenied,
        ErrorInvalidPassword or ErrorLogonFailure or ErrorAccountRestriction or ErrorPasswordExpired or ErrorAccountDisabled or ErrorAccountLockedOut =>
            SysvolUnavailableReason.LogonFailure,
        ErrorBadNetPath or ErrorNetNameDeleted or ErrorBadNetName or ErrorNoNetOrBadPath or ErrorNoNetwork => SysvolUnavailableReason.Unreachable,
        _ => SysvolUnavailableReason.Failed,
    };
}

/// <summary>
/// The Windows side, shared by the reader and the writer (<see cref="WindowsSysvolFileWriter"/>): a logon session of the
/// run's own for other credentials, and the file reads.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsSysvol
{
    private const int Logon32LogonNewCredentials = 9;
    private const int Logon32ProviderWinNt50 = 3;

    public static ISysvolConnection Open(string server, string domain, SysvolLogon logon)
    {
        var share = Share(server);
        return new Connection(server, domain, share, LogOnForShare(share, domain, logon));
    }

    /// <summary><c>\\&lt;server&gt;\SYSVOL</c>.</summary>
    internal static string Share(string server) => @"\\" + server + @"\SYSVOL";

    /// <summary>
    /// The token of a logon session of the run's own for a session with other credentials (null for the process identity),
    /// checked by a first access to the Policies folder. The caller disposes it.
    /// </summary>
    internal static SafeAccessTokenHandle? LogOnForShare(string share, string domain, SysvolLogon logon)
    {
        if (logon.Credential is not { } credential || logon.LogonParts is not { } parts)
        {
            return null;
        }

        var token = LogOn(parts.User, parts.Domain, credential);
        try
        {
            Probe(share + @"\" + domain + @"\Policies", token);
            return token;
        }
        catch
        {
            token.Dispose();
            throw;
        }
    }

    /// <summary><c>LogonUser(NEW_CREDENTIALS)</c>: a new logon session with the credential for network access only.</summary>
    private static SafeAccessTokenHandle LogOn(string user, string? domain, NetworkCredential credential)
    {
        var password = IntPtr.Zero;
        try
        {
            password = Marshal.SecureStringToGlobalAllocUnicode(credential.SecurePassword);
            if (!LogonUser(user, domain, password, Logon32LogonNewCredentials, Logon32ProviderWinNt50, out var handle))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new SysvolUnavailableException(SysvolErrors.ReasonOf(error), $"LogonUser failed with error {error}.");
            }

            return new SafeAccessTokenHandle(handle);
        }
        finally
        {
            if (password != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(password);
            }
        }
    }

    /// <summary>The first access with the new logon session (it checks the credential): the Policies folder of SYSVOL.</summary>
    private static void Probe(string policies, SafeAccessTokenHandle token)
    {
        try
        {
            WindowsIdentity.RunImpersonated(token, () => _ = File.GetAttributes(policies));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SysvolUnavailableException(SysvolUnavailableReason.AccessDenied, "SYSVOL refused access.", ex);
        }
        catch (IOException ex)
        {
            var reason = ex is FileNotFoundException or DirectoryNotFoundException ? SysvolUnavailableReason.Unreachable : SysvolErrors.ReasonOf(SysvolErrors.Win32Error(ex));
            throw new SysvolUnavailableException(reason, "SYSVOL could not be opened with the session's credentials.", ex);
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUser(string user, string? domain, IntPtr password, int logonType, int logonProvider, out IntPtr token);

    /// <summary>A read connection; <see cref="WindowsSysvolFileWriter"/> derives its write connection from it.</summary>
    internal class Connection(string server, string domain, string share, SafeAccessTokenHandle? token) : ISysvolConnection
    {
        private int _disposed;

        public string Server { get; } = server;

        /// <summary>The logon session of the run (null: the process identity).</summary>
        protected SafeAccessTokenHandle? Token { get; } = token;

        protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>The UNC path of a GPO folder (<paramref name="relativePath"/> null) or of a path below it (validated).</summary>
        protected string GpoPath(Guid gpoId, string? relativePath)
        {
            if (gpoId == Guid.Empty)
            {
                throw new ArgumentException("The GPO has no GUID.", nameof(gpoId));
            }

            var folder = share + @"\" + domain + @"\Policies\" + SysvolPaths.FolderName(gpoId);
            if (relativePath is null)
            {
                return folder;
            }

            if (!SysvolPaths.TryNormalize(relativePath, out var normalized))
            {
                throw new ArgumentException("The path is not a path below a GPO folder.", nameof(relativePath));
            }

            return folder + @"\" + normalized.Replace('/', '\\');
        }

        /// <summary>Runs a blocking file call as the run's logon session (or the process identity).</summary>
        protected T AsLogon<T>(Func<T> call) => Token is null ? call() : WindowsIdentity.RunImpersonated(Token, call);

        public async Task<SysvolFileResult> ReadGpoFileAsync(Guid gpoId, string relativePath, long maxBytes, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
            var path = GpoPath(gpoId, relativePath ?? throw new ArgumentNullException(nameof(relativePath)));
            try
            {
                // CreateFile on a UNC path blocks (SMB round trips): off the calling thread, as the run's own logon session
                // when the session has other credentials (the open handle keeps that SMB session for the reads).
                var stream = await Task.Run(() => AsLogon(() => Open(path)), cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    if (stream.Length > maxBytes)
                    {
                        return SysvolFileResult.TooLarge;
                    }

                    var buffer = new byte[(int)stream.Length];
                    var read = 0;
                    while (read < buffer.Length)
                    {
                        var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
                        if (count == 0)
                        {
                            break;
                        }

                        read += count;
                    }

                    return SysvolFileResult.Of(read == buffer.Length ? buffer : buffer[..read]);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return SysvolFileResult.NotFound;
            }
            catch (UnauthorizedAccessException)
            {
                return SysvolFileResult.AccessDenied;
            }
            catch (IOException)
            {
                return SysvolFileResult.Failed;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Token?.Dispose();
            }

            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        private static FileStream Open(string path) => new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        });
    }
}
