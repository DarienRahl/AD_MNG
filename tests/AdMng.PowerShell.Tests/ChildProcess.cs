using System.Diagnostics;

namespace AdMng.PowerShell.Tests;

/// <summary>
/// Runs one test method of this very assembly in a child process whose environment the test controls - the only way to set
/// <c>HOME</c>, <c>TMPDIR</c> and the like before the runtime and the engine start (docs/dev/r1-powershell-window.md 9.1).
/// The child methods are ordinary facts that do nothing unless <see cref="ModeVariable"/> names their mode.
/// </summary>
internal static class ChildProcess
{
    public const string ModeVariable = "ADMNG_POWERSHELL_CHILD";
    public const string ReportVariable = "ADMNG_POWERSHELL_CHILD_REPORT";

    /// <summary>The mode this process was started for, or null in an ordinary test run.</summary>
    public static string? Mode => Environment.GetEnvironmentVariable(ModeVariable);

    /// <summary>The file the child writes its findings to (outside the folder the test watches).</summary>
    public static string? ReportPath => Environment.GetEnvironmentVariable(ReportVariable);

    public static async Task<ChildResult> RunAsync(
        string mode,
        string method,
        string home,
        string report,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null,
        TimeSpan? timeout = null)
    {
        var (file, leading) = HostCommand();
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = home,
        };
        foreach (var argument in leading)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("-method");
        start.ArgumentList.Add(method);

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
        start.Environment[ModeVariable] = mode;
        start.Environment[ReportVariable] = report;
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

    /// <summary>How to start this test assembly: through <c>dotnet</c>, or through its own apphost.</summary>
    private static (string File, string[] Leading) HostCommand()
    {
        var assembly = typeof(ChildProcess).Assembly.Location;
        var current = Environment.ProcessPath;
        if (current is not null)
        {
            var name = Path.GetFileNameWithoutExtension(current);
            if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return (current, [assembly]);
            }

            if (string.Equals(name, Path.GetFileNameWithoutExtension(assembly), StringComparison.OrdinalIgnoreCase))
            {
                return (current, []);
            }
        }

        return (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", [assembly]);
    }
}

/// <summary>What a child process did: its exit code, what it printed and what it reported.</summary>
internal sealed record ChildResult(int ExitCode, string Output, string Report);
