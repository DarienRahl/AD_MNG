using AdMng.Directory.IntegrationTests.Infrastructure;

namespace AdMng.Directory.IntegrationTests.Integration;

/// <summary>
/// Audit finding DLR-4 (the exit code 134 of the Watchdog lab tests): closing a session while S.DS.P builds a search
/// response must not abort the process. S.DS.P's <c>LdapConnection.ConstructEntry</c> frees an attribute name twice when
/// the connection is disposed under it (dotnet/runtime#35189), so the pool never disposes a connection while a request runs
/// on it. Each case runs the probe <c>AdMng.Directory.DisposeProbe</c> as a child process (an abort ends the child, not this
/// run), which closes the session at many moments of a large search - the allocations of S.DS.P's entry loop tell it when.
/// </summary>
public sealed class CloseWhileRequestRunsTests
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(150);

    [Theory]
    [InlineData("dispose-during-search")]
    [InlineData("cancel-then-dispose")]
    [InlineData("cancel-connect")]
    public async Task ClosingASession_WhileARequestBuildsItsResponse_LeavesTheProcessRunning(string mode)
    {
        LabSettings.RequireLab();

        var result = await ProbeProcess.RunAsync(mode, iterations: 20, ProbeTimeout);

        Assert.True(
            result.ExitCode == 0 && result.Output.Contains("survived 20 iterations", StringComparison.Ordinal),
            $"The probe ({mode}) ended with exit code {result.ExitCode} (134 = SIGABRT, 139 = SIGSEGV, -1 = hung and killed, "
            + $"2 = a request ran on a disposed connection):{Environment.NewLine}{result.Output}");
    }

    /// <summary>
    /// Positive control: the same trigger on S.DS.P alone - a connection disposed while its search response is built - kills
    /// the process. Without it a probe that never reached the entry loop (a lab with few objects, another timing) would let
    /// the cases above pass while proving nothing.
    /// </summary>
    [Fact]
    public async Task DisposingAnSdsConnection_WhileItBuildsTheResponse_AbortsTheProcess()
    {
        LabSettings.RequireLab();
        Assert.SkipUnless(OperatingSystem.IsLinux(), "glibc detects the double free at once; other heaps need not.");

        var result = await ProbeProcess.RunAsync("raw", iterations: 60, ProbeTimeout, crashDumps: false);

        Assert.True(
            result.ExitCode != 0 && !result.Output.Contains("survived", StringComparison.Ordinal),
            $"S.DS.P survived 60 disposals during the entry loop (exit code {result.ExitCode}) - the probe does not reach the "
            + $"moment it is meant to test:{Environment.NewLine}{result.Output}");
    }
}
