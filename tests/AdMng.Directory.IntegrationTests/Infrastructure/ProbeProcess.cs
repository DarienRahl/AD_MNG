using System.Diagnostics;

namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the probe program <c>AdMng.Directory.DisposeProbe</c> (copied beside this assembly) in a child process: a defect
/// that aborts the process (DLR-4, a double free in S.DS.P) must end the child, not the test run. The child inherits the
/// lab variables (<c>ADMNG_TEST_LDAP_*</c>).
/// </summary>
internal static class ProbeProcess
{
    /// <summary>
    /// The runtime's settings for a mini dump and a crash report. The CI sets them for its lab job, and a child inherits them;
    /// a child that is meant to crash (the positive control) starts without them, or it leaves a dump and a crash report that
    /// hide a crash that is not meant (and createdump itself can hang on a corrupted heap).
    /// </summary>
    private static readonly string[] CrashDumpVariables =
        [.. from prefix in new[] { "DOTNET_", "COMPlus_" }
            from name in new[] { "DbgEnableMiniDump", "DbgMiniDumpType", "DbgMiniDumpName", "EnableCrashReport", "EnableCrashReportOnly" }
            select prefix + name];

    /// <summary>Runs the probe; a child that has not ended after <paramref name="timeout"/> is killed (exit code -1).</summary>
    public static async Task<ProbeResult> RunAsync(string mode, int iterations, TimeSpan timeout, bool crashDumps = true)
    {
        var probe = Path.Combine(AppContext.BaseDirectory, "AdMng.Directory.DisposeProbe.dll");
        Assert.True(File.Exists(probe), "The probe program is not beside the test assembly: " + probe);
        var start = new ProcessStartInfo(HostPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(iterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!crashDumps)
        {
            foreach (var name in CrashDumpVariables)
            {
                start.Environment.Remove(name);
            }
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The probe did not start.");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout);
        var hung = false;
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            // A heap corrupted by a double free can leave the process hanging in malloc.
            hung = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        return new ProbeResult(hung ? -1 : process.ExitCode, await output + await error);
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

/// <summary>What the probe did: its exit code (-1 = hung and killed) and what it printed.</summary>
internal sealed record ProbeResult(int ExitCode, string Output);
