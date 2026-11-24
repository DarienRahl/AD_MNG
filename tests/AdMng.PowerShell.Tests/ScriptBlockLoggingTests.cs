namespace AdMng.PowerShell.Tests;

/// <summary>
/// What the engine writes of the window's commands to its event log when neither Group Policy nor a configuration file sets script
/// block logging (decision D-B, audit PS-04). <c>System.Management.Automation</c> 7.6.10, <c>CompiledScriptBlock.cs</c>: a top-level
/// script block whose text holds a word of the engine's list of suspicious content (<c>SuspiciousContentChecker</c>: "Properties",
/// "Add-Type", "FromBase64String", "GetMethod"…) is marked when it is compiled, <c>LogScriptBlockStart</c> then logs it with
/// <c>force</c>, and <c>LogScriptBlockCreation</c> writes it unless the setting says <c>EnableScriptBlockLogging == false</c> - as a
/// warning, event 4104 (<c>WriteScriptBlockToLog</c>); an ordinary script block is written only when the setting says <c>true</c>.
/// So a file that sets the switch to false - the program's own file did until D-B - silences even the suspicious ones, and a file
/// without the section leaves the engine's default, as in a plain pwsh. In child processes (the setting is read once per process),
/// each with its own all-users file: none of its own, the program's, and one with the old section.
/// </summary>
public sealed class ScriptBlockLoggingTests
{
    [Fact]
    public async Task The_programs_file_leaves_the_engines_own_logging_of_suspicious_script_blocks_which_a_file_that_says_off_switches_off()
    {
        using var none = new TempFolder();
        using var shipped = new TempFolder();
        using var off = new TempFolder();
        var empty = Path.Combine(Path.GetDirectoryName(none.ReportPath)!, "powershell.config.json");
        await File.WriteAllTextAsync(empty, "{ }", TestContext.Current.CancellationToken);
        var switchedOff = Path.Combine(Path.GetDirectoryName(off.ReportPath)!, "powershell.config.json");
        await File.WriteAllTextAsync(
            switchedOff,
            """{ "PowerShellPolicies": { "ScriptBlockLogging": { "EnableScriptBlockLogging": false, "EnableScriptBlockInvocationLogging": false } } }""",
            TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(
            ChildProcess.RunAsync("script-block-logging:" + empty, none.Home, none.ReportPath),
            ChildProcess.RunAsync("script-block-logging:" + EngineBoundaryTests.ShippedConfigFile, shipped.Home, shipped.ReportPath),
            ChildProcess.RunAsync("script-block-logging:" + switchedOff, off.Home, off.ReportPath));
        foreach (var result in results)
        {
            Assert.True(result.ExitCode == 0, result.Output);
            Assert.EndsWith("done\n", result.Report, StringComparison.Ordinal);
        }

        // A Group Policy of this machine (or the user's own file) that sets script block logging decides before any all-users file.
        Assert.SkipUnless(results[0].Report.StartsWith("setting=null\n", StringComparison.Ordinal), "This machine sets script block logging itself:\n" + results[0].Report);

        // Nothing sets it: the engine logs the suspicious script block on its own, not the ordinary one.
        Assert.Equal("setting=null\nsuspicious=logged\nordinary=skipped\ndone\n", results[0].Report);

        // The program's file sets nothing of it: the same as no file.
        Assert.Equal(results[0].Report, results[1].Report);

        // A file that says off: not even the suspicious one.
        Assert.Equal("setting=False\nsuspicious=skipped\nordinary=skipped\ndone\n", results[2].Report);
        Assert.Empty(shipped.Files());
    }
}
