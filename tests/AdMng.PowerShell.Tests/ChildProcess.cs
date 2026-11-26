using System.Diagnostics;

namespace AdMng.PowerShell.Tests;

/// <summary>
/// Runs the probe program (<c>AdMng.PowerShell.Probe</c>, copied beside this assembly) in a child process whose environment
/// the test controls - the only way to set <c>HOME</c>, <c>TMPDIR</c> and the like before the runtime and the engine start
/// (docs/dev/r1-powershell-window.md 9.1). The probe is a small console program, not an xunit run: starting this very test
/// assembly as the child took 18.6 s on the Windows runner.
/// </summary>
internal static class ChildProcess
{
    /// <summary>
    /// The runtime's settings for a mini dump and a crash report of a process that crashes. The CI sets them for its lab job (a test
    /// process that dies without a managed stack leaves both for the "Crash reports" step), and a child inherits them: a child that is
    /// meant to crash starts without them (<c>crashDumps: false</c>), or every run leaves a dump and a crash report of the probe, which
    /// hide a crash that is not meant. A child that is not meant to crash keeps them.
    /// </summary>
    public static IReadOnlyList<string> CrashDumpVariables { get; } =
        [.. from prefix in new[] { "DOTNET_", "COMPlus_" }
            from name in new[] { "DbgEnableMiniDump", "DbgMiniDumpType", "DbgMiniDumpName", "EnableCrashReport", "EnableCrashReportOnly" }
            select prefix + name];

    public static async Task<ChildResult> RunAsync(
        string mode,
        string home,
        string report,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null,
        TimeSpan? timeout = null,
        bool crashDumps = true)
    {
        var probe = Path.Combine(AppContext.BaseDirectory, "AdMng.PowerShell.Probe.dll");
        Assert.True(File.Exists(probe), "The probe program is not beside the test assembly: " + probe);
        var start = new ProcessStartInfo(HostPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = home,
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(report);

        // Everything a program writes its files under points at the folder the test watches.
        foreach (var name in new[] { "HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_STATE_HOME", "TMPDIR", "TEMP", "TMP", "LOCALAPPDATA", "APPDATA", "USERPROFILE" })
        {
            start.Environment[name] = home;
        }

        // The user's own settings are what the engine must override: telemetry is "on" in the child's environment.
        start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "0";
        start.Environment["POWERSHELL_DIAGNOSTICS_OPTOUT"] = "0";
        start.Environment.Remove("POWERSHELL_UPDATECHECK");
        start.Environment.Remove("PSModuleAnalysisCachePath");
        start.Environment.Remove("PSModulePath");
        foreach (var (name, value) in extraEnvironment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        if (!crashDumps)
        {
            foreach (var name in CrashDumpVariables)
            {
                start.Environment.Remove(name);
            }
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The child process did not start.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(120));
        var output = process.StandardOutput.ReadToEndAsync(cts.Token);
        var error = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new ChildResult(process.ExitCode, await output + await error, File.Exists(report) ? await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken) : string.Empty);
    }

    /// <summary>The <c>dotnet</c> host that runs this test process (or the one on the path).</summary>
    private static string HostPath()
    {
        var current = Environment.ProcessPath;
        if (current is not null && string.Equals(Path.GetFileNameWithoutExtension(current), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        return Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    }
}

/// <summary>What a child process did: its exit code, what it printed and what it reported.</summary>
internal sealed record ChildResult(int ExitCode, string Output, string Report);
