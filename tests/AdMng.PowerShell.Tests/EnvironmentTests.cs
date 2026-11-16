namespace AdMng.PowerShell.Tests;

/// <summary>D8: the engine's environment is set before its first use; the diagnostics channel does not exist.</summary>
public sealed class EnvironmentTests
{
    [Fact]
    public async Task A_session_applies_the_engines_environment_and_the_script_sees_it()
    {
        await using var session = await TestSessions.CreateAsync();

        Assert.True(PowerShellEnvironment.IsApplied);
        foreach (var (name, value) in PowerShellEnvironment.Variables)
        {
            Assert.Equal(value, Environment.GetEnvironmentVariable(name));
        }

        var items = await TestSessions.RunAsync(session, "$env:POWERSHELL_TELEMETRY_OPTOUT; $env:POWERSHELL_DIAGNOSTICS_OPTOUT; $env:PSModuleAnalysisCachePath");
        Assert.Equal("1\n1\n" + PowerShellEnvironment.NullDevice + "\n", TestSessions.Text(items));
    }

    [Fact]
    public void The_settings_are_the_ones_the_design_note_names()
    {
        Assert.Equal("1", PowerShellEnvironment.Variables["POWERSHELL_TELEMETRY_OPTOUT"]);
        Assert.Equal("1", PowerShellEnvironment.Variables["POWERSHELL_DIAGNOSTICS_OPTOUT"]);
        Assert.Equal("Off", PowerShellEnvironment.Variables["POWERSHELL_UPDATECHECK"]);
        Assert.Equal(OperatingSystem.IsWindows() ? "NUL" : "/dev/null", PowerShellEnvironment.Variables["PSModuleAnalysisCachePath"]);
        Assert.Equal(4, PowerShellEnvironment.Variables.Count);
    }

    [Fact]
    public async Task No_diagnostics_pipe_of_the_engine_exists_while_a_session_runs()
    {
        await using var session = await TestSessions.CreateAsync();
        await TestSessions.RunAsync(session, "Get-Date");

        // Windows: \\.\pipe\PSHost.<ticks>.<pid>.<domain>.<process>; Unix: CoreFxPipe_PSHost.* in the temp folder.
        var pipes = OperatingSystem.IsWindows()
            ? Directory.GetFiles(@"\\.\pipe\", "PSHost.*")
            : Directory.GetFiles(Path.GetTempPath(), "CoreFxPipe_PSHost*");

        Assert.Empty(pipes);
    }
}
