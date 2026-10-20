using System.Diagnostics;
using System.Runtime.Versioning;

namespace AdMng.Watchdog.Service.Hosting;

/// <summary>
/// Reports a host that could not even be built (spec 19.1, 19.8) – before any logging provider exists: stderr, and on
/// Windows Application-log event 1010 written directly with source "AdMng Watchdog" (created by the install script), so
/// a service stuck in an SCM restart loop says why. The message carries only the exception type and code, never a value.
/// </summary>
public static class StartupFailureLog
{
    /// <summary>The message for <paramref name="errorCode"/> (<c>SafeExceptionLog.ErrorCode</c>).</summary>
    public static string Message(string errorCode) =>
        $"AdMng Watchdog: the service could not be configured (exit code {StartupExitCodes.InvalidSettings}): {errorCode}. "
        + (OperatingSystem.IsLinux()
            ? $"Check {ConfigurationSetup.LinuxBootstrapFilePath} (JSON syntax, setting names and values) and the data folder; "
              + "sudo admng-watchdog --check-config lists the issues."
            : @"Check %ProgramData%\AdMng\Watchdog\config\appsettings.json (JSON syntax, setting names and values) and the data folder; "
              + "AdMng.Watchdog.exe --check-config lists the issues.");

    public static void Report(string errorCode)
    {
        var message = Message(errorCode);
        try
        {
            Console.Error.WriteLine(message);
        }
        catch (IOException)
        {
            // No console under the SCM.
        }

        if (OperatingSystem.IsWindows())
        {
            WriteEventLog(message);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteEventLog(string message)
    {
        try
        {
            EventLog.WriteEntry(LoggingSetup.EventLogSource, message, EventLogEntryType.Error, EventLogIds.FatalStartupCheck);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The source is missing (not installed) or the log is not writable: stderr is all there is.
        }
    }
}
