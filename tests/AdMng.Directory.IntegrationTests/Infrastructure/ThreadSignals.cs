using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// Linux: signals delivered to the threads of this test process – the SIGCHLD that the exit of a child process (samba-tool,
/// smbclient, ldapsearch started by other tests) sends to one of them, aimed at every thread so that it also reaches the
/// one that waits in libldap.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class ThreadSignals
{
    /// <summary>SIGCHLD (Linux x64 and arm64).</summary>
    public const int ChildExited = 17;

    /// <summary>
    /// Makes sure the runtime handles SIGCHLD the way it does once this process started a child process (the handler that
    /// reaps it); without a handler the kernel discards SIGCHLD and no wait is interrupted.
    /// </summary>
    public static void EnsureChildProcessHandling()
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/true") { UseShellExecute = false })!;
        process.WaitForExit();
    }

    /// <summary>Sends <paramref name="signal"/> to every thread of this process; returns how many threads got it.</summary>
    public static int SendToEveryThread(int signal)
    {
        var processId = Environment.ProcessId;
        var sent = 0;
        foreach (var task in System.IO.Directory.EnumerateDirectories("/proc/self/task"))
        {
            // A thread that ended meanwhile fails with ESRCH.
            if (int.TryParse(Path.GetFileName(task), out var threadId) && ThreadKill(processId, threadId, signal) == 0)
            {
                sent++;
            }
        }

        return sent;
    }

    [LibraryImport("libc", EntryPoint = "tgkill")]
    private static partial int ThreadKill(int processId, int threadId, int signal);
}
