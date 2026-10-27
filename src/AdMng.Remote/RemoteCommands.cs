using System.Globalization;
using AdMng.Core.Computers.Remote;

namespace AdMng.Remote;

/// <summary>
/// The constant command lines and documents the remote actions and tools use (K1 design note 4.7, 4.8). Nothing typed by
/// the operator is ever put into a command line: hosts are validated DNS names, the restart message is a typed CIM method
/// parameter.
/// </summary>
internal static class RemoteCommands
{
    /// <summary>HKEY_LOCAL_MACHINE for StdRegProv.</summary>
    public const uint LocalMachine = 0x80000002;

    public const string CimNamespace = "root/cimv2";

    public const string TaskSchedulerNamespace = "root/Microsoft/Windows/TaskScheduler";

    /// <summary>
    /// Win32_Process.Create of the computer policy refresh. gpupdate returns at once (no wait) and the Group Policy state
    /// is polled for a new end time; the process itself ends before the processing does.
    /// </summary>
    public const string GpUpdateComputer = "gpupdate.exe /target:computer /force /wait:0";

    /// <summary>Win32_Process.Create that requests the cancellation of a scheduled restart (its exit code is not observed).</summary>
    public const string AbortShutdown = "shutdown.exe /a";

    public const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public const string MachinePolicyStateKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\State\Machine\Extension-List\{00000000-0000-0000-0000-000000000000}";

    /// <summary>Win32ShutdownTracker ReasonCode: SHTDN_REASON_FLAG_PLANNED | other.</summary>
    public const uint PlannedOtherReason = 0x80000000;

    /// <summary>Win32ShutdownTracker Flags: 2 = reboot, + 4 = forced (applications closed without asking).</summary>
    public static int RestartFlags(bool force) => force ? 6 : 2;

    /// <summary>The restart delay in seconds (0–24 h).</summary>
    public static uint RestartTimeout(TimeSpan delay) =>
        delay < TimeSpan.Zero || delay > TimeSpan.FromDays(1) ? throw new ArgumentOutOfRangeException(nameof(delay)) : (uint)delay.TotalSeconds;

    /// <summary>
    /// The one-time task that refreshes the user policy of every logged-on user (what Invoke-GPUpdate does): principal
    /// group INTERACTIVE (S-1-5-4), <c>gpupdate.exe /target:user /force</c>, started 5 s after its registration (a
    /// registration trigger, so the start does not depend on the clocks of the workstation and the computer agreeing – a
    /// time trigger a few seconds in the computer's past would never fire), deleted once it expired 10 minutes later.
    /// </summary>
    public static string UserPolicyTaskXml(DateTimeOffset now)
    {
        var end = now.ToUniversalTime().AddMinutes(10).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>AD Manager: Group Policy refresh of the logged-on users</Description></RegistrationInfo>
              <Triggers><RegistrationTrigger><EndBoundary>{end}</EndBoundary><Enabled>true</Enabled><Delay>PT5S</Delay></RegistrationTrigger></Triggers>
              <Principals><Principal id="Users"><GroupId>S-1-5-4</GroupId><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings><DeleteExpiredTaskAfter>PT0S</DeleteExpiredTaskAfter><StartWhenAvailable>false</StartWhenAvailable><ExecutionTimeLimit>PT10M</ExecutionTimeLimit><Hidden>true</Hidden><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries></Settings>
              <Actions Context="Users"><Exec><Command>gpupdate.exe</Command><Arguments>/target:user /force</Arguments></Exec></Actions>
            </Task>
            """;
    }

    /// <summary>The task's name: fixed prefix and a random part (never the computer or the account).</summary>
    public static string UserPolicyTaskName(Guid id) => "AdMng-UserPolicyRefresh-" + id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// The program and arguments of a local tool for <paramref name="host"/> (a validated DNS name, so no quoting is
    /// needed): <c>mstsc.exe /v:host</c>, <c>mmc.exe compmgmt.msc /computer:host</c>, <c>eventvwr.exe host</c>.
    /// </summary>
    /// <exception cref="RemoteActionException"><see cref="RemoteErrorKind.NotSupported"/> for an invalid host or an IP address.</exception>
    public static (string FileName, string Arguments) Tool(LocalTool tool, string host, string systemDirectory)
    {
        ArgumentNullException.ThrowIfNull(systemDirectory);
        if (!HostNames.IsValid(host))
        {
            throw new RemoteActionException(RemoteErrorKind.NotSupported);
        }

        var name = HostNames.Normalize(host);
        return tool switch
        {
            LocalTool.RemoteDesktop => (Path.Combine(systemDirectory, "mstsc.exe"), "/v:" + name),
            LocalTool.ComputerManagement => (Path.Combine(systemDirectory, "mmc.exe"), "compmgmt.msc /computer:" + name),
            LocalTool.EventViewer => (Path.Combine(systemDirectory, "eventvwr.exe"), name),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
        };
    }

    /// <summary>
    /// The environment block of a tool started with the window's credential: the current environment plus
    /// <c>__COMPAT_LAYER=RunAsInvoker</c> (mmc would ask for elevation, which CreateProcessWithLogonW cannot satisfy),
    /// sorted, each <c>NAME=value</c> ended by NUL and the block by another NUL.
    /// </summary>
    public static string EnvironmentBlock(System.Collections.IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in environment)
        {
            if (entry.Key is string name && name.Length > 0 && !name.Contains('=', StringComparison.Ordinal) && !name.Contains('\0', StringComparison.Ordinal)
                && entry.Value is string value && !value.Contains('\0', StringComparison.Ordinal))
            {
                variables[name] = value;
            }
        }

        variables["__COMPAT_LAYER"] = "RunAsInvoker";
        return string.Concat(variables.Select(v => $"{v.Key}={v.Value}\0")) + "\0";
    }

    /// <summary>The command line of CreateProcessWithLogonW: the quoted program and its arguments.</summary>
    public static string CommandLine(string fileName, string arguments) => $"\"{fileName}\" {arguments}";
}
