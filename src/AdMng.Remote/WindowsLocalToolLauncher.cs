using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AdMng.Core.Computers.Remote;
using AdMng.Core.Directory;
using AdMng.Directory;

namespace AdMng.Remote;

/// <summary>
/// Remote Desktop, Computer Management and Event Viewer for a computer (K1 design note 4.8).
/// <list type="bullet">
/// <item><c>mstsc.exe /v:host</c> never gets a credential (no cmdkey, nothing in Credential Manager – rule 2): mstsc uses
/// the operator's saved credentials or asks.</item>
/// <item>The MMC tools start directly in a Windows-account window, with <c>__COMPAT_LAYER=RunAsInvoker</c> (CreateProcess
/// cannot elevate mmc or the auto-elevating eventvwr). Otherwise <c>CreateProcessWithLogonW</c> with
/// <c>LOGON_NETCREDENTIALS_ONLY</c> (what <c>runas /netonly</c> does): the local identity stays, network access uses the
/// window's credential; the password goes in a zeroed unmanaged buffer; the child gets <c>__COMPAT_LAYER=RunAsInvoker</c>.
/// The credential then lives in the LSA logon session of that tool until the operator closes it – never on disk.</item>
/// </list>
/// </summary>
public sealed class WindowsLocalToolLauncher : ILocalToolLauncher
{
    public bool UsesWindowCredential(LocalTool tool, IDirectorySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return tool != LocalTool.RemoteDesktop && session.Info.Authentication != AuthenticationMode.CurrentUser;
    }

    public Task LaunchAsync(LocalTool tool, string host, IDirectorySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!OperatingSystem.IsWindows())
        {
            // The command line is still validated first, so an invalid host is reported the same way everywhere.
            _ = RemoteCommands.Tool(tool, host, string.Empty);
            throw new RemoteActionException(RemoteErrorKind.NotSupported);
        }

        return LaunchOnWindows(tool, host, session, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private Task LaunchOnWindows(LocalTool tool, string host, IDirectorySession session, CancellationToken cancellationToken)
    {
        var (fileName, arguments) = RemoteCommands.Tool(tool, host, Environment.SystemDirectory);
        if (!UsesWindowCredential(tool, session))
        {
            return Task.Run(() => StartDirectly(tool, fileName, arguments), cancellationToken);
        }

        var logon = SessionLogons.Of(session);
        return Task.Run(
            () =>
            {
                using (logon)
                {
                    if (logon.IsWindowsAccount)
                    {
                        StartDirectly(tool, fileName, arguments);
                    }
                    else
                    {
                        NetOnlyProcess.Start(fileName, arguments, logon);
                    }
                }
            },
            cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    private static void StartDirectly(LocalTool tool, string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(RemoteCommands.DirectStart(tool, fileName, arguments));
        }
        catch (Win32Exception ex)
        {
            throw new RemoteActionException(RemoteErrorKind.Failed, ex) { ErrorCode = ex.NativeErrorCode };
        }
    }
}

/// <summary>CreateProcessWithLogonW(LOGON_NETCREDENTIALS_ONLY) – the password only in a zeroed unmanaged buffer.</summary>
[SupportedOSPlatform("windows")]
internal static partial class NetOnlyProcess
{
    private const int LogonNetCredentialsOnly = 2;
    private const int CreateUnicodeEnvironment = 0x00000400;

    public static void Start(string fileName, string arguments, SessionLogon logon)
    {
        var password = IntPtr.Zero;
        var environment = IntPtr.Zero;
        try
        {
            password = Marshal.SecureStringToGlobalAllocUnicode(logon.Password!);
            environment = Marshal.StringToHGlobalUni(RemoteCommands.EnvironmentBlock(Environment.GetEnvironmentVariables()));
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcessWithLogonW(
                logon.UserName!,
                logon.Domain,
                password,
                LogonNetCredentialsOnly,
                fileName,
                RemoteCommands.CommandLine(fileName, arguments),
                CreateUnicodeEnvironment,
                environment,
                Environment.SystemDirectory,
                ref startup,
                out var info))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new RemoteActionException(error is 1326 or 1311 ? RemoteErrorKind.AuthenticationFailed : error == 5 ? RemoteErrorKind.AccessDenied : RemoteErrorKind.Failed)
                {
                    ErrorCode = error,
                };
            }

            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
        }
        finally
        {
            if (password != IntPtr.Zero)
            {
                Marshal.ZeroFreeGlobalAllocUnicode(password);
            }

            if (environment != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environment);
            }
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessWithLogonW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessWithLogonW(
        string userName,
        string? domain,
        IntPtr password,
        int logonFlags,
        string applicationName,
        string commandLine,
        int creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
