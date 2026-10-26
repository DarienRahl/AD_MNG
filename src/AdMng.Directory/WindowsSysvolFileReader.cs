using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;

namespace AdMng.Directory;

/// <summary>
/// <see cref="ISysvolFileReader"/> of the WPF app (README U4c): reads <c>\\&lt;session DC&gt;\SYSVOL\&lt;domain&gt;\Policies\{GUID}\…</c>
/// - the DC of the session, never another one (CLAUDE.md rule 4) - into memory (rule 1: nothing is written to disk).
/// <list type="bullet">
/// <item>A session of the Windows account (<see cref="AuthenticationMode.CurrentUser"/>) reads with the process identity.</item>
/// <item>A session opened with other credentials connects to <c>\\&lt;DC&gt;\SYSVOL</c> with the session's in-memory
/// credential (<c>WNetAddConnection2</c>, deviceless, <c>CONNECT_TEMPORARY</c> - never remembered in the profile) for the
/// duration of the read and disconnects afterwards (<c>WNetCancelConnection2</c>); the password goes to the call in a
/// zeroed unmanaged buffer and is never stored or logged (rule 2). Connections are counted per share, so two windows of
/// the same account share one; Windows allows one set of credentials per server and logon session, so another account's
/// connection to the same DC is reported as <see cref="SysvolUnavailableReason.CredentialConflict"/>.</item>
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

    /// <summary>The session's credential (the password is read only when connecting).</summary>
    public NetworkCredential? Credential { get; }

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

/// <summary>The Windows side: WNet connections (counted per share) and file reads.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsSysvol
{
    private const int ResourceTypeDisk = 1;
    private const int ConnectTemporary = 4;
    private const int NoError = 0;
    private const int ErrorAccessDenied = 5;
    private const int ErrorBadNetPath = 53;
    private const int ErrorNetNameDeleted = 64;
    private const int ErrorBadNetName = 67;
    private const int ErrorInvalidPassword = 86;
    private const int ErrorNoNetOrBadPath = 1203;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorNoNetwork = 1222;
    private const int ErrorLogonFailure = 1326;
    private const int ErrorAccountRestriction = 1327;
    private const int ErrorPasswordExpired = 1330;
    private const int ErrorAccountDisabled = 1331;
    private const int ErrorAccountLockedOut = 1909;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, (int Count, string User)> Connections = new(StringComparer.OrdinalIgnoreCase);

    public static ISysvolConnection Open(string server, string domain, SysvolLogon logon)
    {
        var share = @"\\" + server + @"\SYSVOL";
        if (logon.UserName is { } user && logon.Credential is { } credential)
        {
            Acquire(share, user, credential);
            return new Connection(server, domain, share, connected: true);
        }

        return new Connection(server, domain, share, connected: false);
    }

    private static void Acquire(string share, string user, NetworkCredential credential)
    {
        lock (Gate)
        {
            if (Connections.TryGetValue(share, out var existing))
            {
                if (!string.Equals(existing.User, user, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SysvolUnavailableException(SysvolUnavailableReason.CredentialConflict, "Another window reads this SYSVOL with other credentials.");
                }

                Connections[share] = (existing.Count + 1, existing.User);
                return;
            }

            var remote = Marshal.StringToHGlobalUni(share);
            var userName = Marshal.StringToHGlobalUni(user);
            var password = IntPtr.Zero;
            try
            {
                password = Marshal.SecureStringToGlobalAllocUnicode(credential.SecurePassword);
                var resource = new NetResource { Type = ResourceTypeDisk, RemoteName = remote };
                var result = WNetAddConnection2(ref resource, password, userName, ConnectTemporary);
                if (result != NoError)
                {
                    throw new SysvolUnavailableException(ReasonOf(result), $"WNetAddConnection2 failed with error {result}.");
                }

                Connections[share] = (1, user);
            }
            finally
            {
                if (password != IntPtr.Zero)
                {
                    Marshal.ZeroFreeGlobalAllocUnicode(password);
                }

                Marshal.FreeHGlobal(userName);
                Marshal.FreeHGlobal(remote);
            }
        }
    }

    private static void Release(string share)
    {
        lock (Gate)
        {
            if (!Connections.TryGetValue(share, out var existing))
            {
                return;
            }

            if (existing.Count > 1)
            {
                Connections[share] = (existing.Count - 1, existing.User);
                return;
            }

            Connections.Remove(share);
            _ = WNetCancelConnection2(share, 0, force: true);
        }
    }

    private static SysvolUnavailableReason ReasonOf(int error) => error switch
    {
        ErrorSessionCredentialConflict => SysvolUnavailableReason.CredentialConflict,
        ErrorAccessDenied => SysvolUnavailableReason.AccessDenied,
        ErrorInvalidPassword or ErrorLogonFailure or ErrorAccountRestriction or ErrorPasswordExpired or ErrorAccountDisabled or ErrorAccountLockedOut =>
            SysvolUnavailableReason.LogonFailure,
        ErrorBadNetPath or ErrorNetNameDeleted or ErrorBadNetName or ErrorNoNetOrBadPath or ErrorNoNetwork => SysvolUnavailableReason.Unreachable,
        _ => SysvolUnavailableReason.Failed,
    };

    [LibraryImport("mpr.dll", EntryPoint = "WNetAddConnection2W")]
    private static partial int WNetAddConnection2(ref NetResource resource, IntPtr password, IntPtr userName, int flags);

    [LibraryImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);

    [StructLayout(LayoutKind.Sequential)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public IntPtr LocalName;
        public IntPtr RemoteName;
        public IntPtr Comment;
        public IntPtr Provider;
    }

    private sealed class Connection(string server, string domain, string share, bool connected) : ISysvolConnection
    {
        private int _disposed;

        public string Server { get; } = server;

        public async Task<SysvolFileResult> ReadGpoFileAsync(Guid gpoId, string relativePath, long maxBytes, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!SysvolPaths.TryNormalize(relativePath, out var normalized) || gpoId == Guid.Empty)
            {
                throw new ArgumentException("The path is not a path below a GPO folder.", nameof(relativePath));
            }

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
            var path = share + @"\" + domain + @"\Policies\" + SysvolPaths.FolderName(gpoId) + @"\" + normalized.Replace('/', '\\');
            try
            {
                // CreateFile on a UNC path blocks (SMB round trips): off the calling thread.
                var stream = await Task.Run(
                    () => new FileStream(path, new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.ReadWrite | FileShare.Delete,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                        BufferSize = 0,
                    }),
                    cancellationToken).ConfigureAwait(false);
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
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && connected)
            {
                return new ValueTask(Task.Run(() => Release(share)));
            }

            return ValueTask.CompletedTask;
        }
    }
}
