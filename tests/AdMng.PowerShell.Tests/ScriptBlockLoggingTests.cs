namespace AdMng.PowerShell.Tests;

/// <summary>
/// What the engine writes of the window's commands to its event log (decision D-B, audit PS-04), with the real engine in child
/// processes (the setting is read once per process), each with its own all-users and user's <c>powershell.config.json</c>.
/// <c>System.Management.Automation</c> 7.6.10: <c>PerformSecurityChecks</c> (<c>CompiledScriptBlock.cs</c>) marks a script block
/// when it is compiled if <c>ScriptBlock.CheckSuspiciousContent</c> finds anything, and that check has two parts - its text holds a
/// word of the engine's list (<c>SuspiciousContentChecker.Match</c>: "Properties", "Add-Type", "EncodedCommand", "GetMethod"…,
/// also inside a longer word; <c>Match</c> hashes only letters and '-', so the five words of the list with a digit,
/// "FromBase64String" among them, never match, and it tests every length once more at the end, so the last letters of the text,
/// read without what lies between them, count too - <c>Get-AdmUser 'grace mit'</c> ends in "Emit"), or the parser marked it
/// (<c>Ast.HasSuspiciousContent</c>, set on every parse by <c>SemanticChecks</c>: a member whose name is not a constant -
/// <c>$u.$a</c>, <c>$u.('mail')</c>, <c>$u."$n"</c> -, a static member of something that is not a type literal - <c>$t::Now</c> -,
/// and the type <c>[type]</c> itself). <c>LogScriptBlockStart</c> then logs a marked block with <c>force</c>, and
/// <c>LogScriptBlockCreation</c> writes it unless the setting says <c>EnableScriptBlockLogging == false</c> - as a warning, event
/// 4104 (<c>WriteScriptBlockToLog</c>); any other block is written only when the setting says <c>true</c>. So with nothing set the engine logs the marked lines on its own (an operator's
/// <c>foreach ($a in 'mail', 'department') { $u.$a }</c> among them), a file that sets the switch to false - the program's own
/// file did until D-B - silences even those, a user's own file that sets it to true makes every line logged, and the program's
/// file, which has no section, leaves all of it to Group Policy and the user's file, as in a plain pwsh.
/// </summary>
public sealed class ScriptBlockLoggingTests
{
    /// <summary>The probe's lines the engine marks as suspicious (<c>ScriptBlockLoggingProbe.Lines</c>), in the order of its report.</summary>
    private static readonly string[] Suspicious =
        ["word", "member-by-variable", "member-by-expression", "member-by-expandable-string", "static-member-through-variable", "type-type", "last-letters"];

    /// <summary>
    /// The probe's ordinary lines: no word of the list, members by constant names, a static member of a type literal, a word of the
    /// list with a digit, an account name whose letters end in no word of the list.
    /// </summary>
    private static readonly string[] Ordinary = ["ordinary", "members-by-constant-name", "word-with-digit", "account-name"];

    private const string SwitchedOff =
        """{ "PowerShellPolicies": { "ScriptBlockLogging": { "EnableScriptBlockLogging": false, "EnableScriptBlockInvocationLogging": false } } }""";

    private const string SwitchedOn = """{ "PowerShellPolicies": { "ScriptBlockLogging": { "EnableScriptBlockLogging": true } } }""";

    /// <summary>The report the probe writes when the engine's setting is <paramref name="setting"/>.</summary>
    private static string Report(string setting, string suspicious, string ordinary) =>
        "setting=" + setting + "\n"
        + string.Concat(Suspicious.Select(name => name + "=" + suspicious + "\n"))
        + string.Concat(Ordinary.Select(name => name + "=" + ordinary + "\n"))
        + "done\n";

    [Fact]
    public async Task The_programs_file_leaves_the_engines_own_logging_of_suspicious_script_blocks_to_Group_Policy_and_the_users_file()
    {
        using var none = new TempFolder();
        using var shipped = new TempFolder();
        using var off = new TempFolder();
        using var userOn = new TempFolder();
        var empty = await WriteBesideAsync(none, "powershell.config.json", "{ }");
        var switchedOff = await WriteBesideAsync(off, "powershell.config.json", SwitchedOff);
        var usersFile = await WriteBesideAsync(userOn, "user.powershell.config.json", SwitchedOn);

        // The user's file of the first three is one that does not exist, so that the user's real file (Documents\PowerShell on
        // Windows) never decides here.
        var results = await Task.WhenAll(
            ChildProcess.RunAsync(Mode(empty, Missing(none)), none.Home, none.ReportPath),
            ChildProcess.RunAsync(Mode(EngineBoundaryTests.ShippedConfigFile, Missing(shipped)), shipped.Home, shipped.ReportPath),
            ChildProcess.RunAsync(Mode(switchedOff, Missing(off)), off.Home, off.ReportPath),
            ChildProcess.RunAsync(Mode(EngineBoundaryTests.ShippedConfigFile, usersFile), userOn.Home, userOn.ReportPath));
        foreach (var result in results)
        {
            Assert.True(result.ExitCode == 0, result.Output);
            Assert.EndsWith("done\n", result.Report, StringComparison.Ordinal);
        }

        // A Group Policy of this machine that sets script block logging decides before any file. Only Windows has one: elsewhere the
        // engine reads nothing but the two files the probe names, so the setting must be null there.
        if (OperatingSystem.IsWindows())
        {
            Assert.SkipUnless(results[0].Report.StartsWith("setting=null\n", StringComparison.Ordinal), "This machine sets script block logging itself:\n" + results[0].Report);
        }

        // Nothing sets it: the engine logs the marked script blocks on its own - a word of its list and the parser's marks alike -
        // and no ordinary one.
        Assert.Equal(Report("null", suspicious: "logged", ordinary: "skipped"), results[0].Report);

        // The program's file sets nothing of it: the same as no file.
        Assert.Equal(results[0].Report, results[1].Report);

        // A file that says off: not even the marked ones.
        Assert.Equal(Report("False", suspicious: "skipped", ordinary: "skipped"), results[2].Report);

        // Beside the program's file, a user's own file that says on: every script block.
        Assert.Equal(Report("True", suspicious: "logged", ordinary: "logged"), results[3].Report);
        Assert.Empty(shipped.Files());
    }

    /// <summary>The probe's mode for an all-users file and a user's file (<c>|</c> is in no Windows path).</summary>
    private static string Mode(string allUsersFile, string usersFile) => "script-block-logging:" + allUsersFile + "|" + usersFile;

    private static string Missing(TempFolder folder) => Path.Combine(Path.GetDirectoryName(folder.ReportPath)!, "no-such-folder", "powershell.config.json");

    /// <summary>Writes a file beside the report, outside the watched home folder.</summary>
    private static async Task<string> WriteBesideAsync(TempFolder folder, string name, string content)
    {
        var path = Path.Combine(Path.GetDirectoryName(folder.ReportPath)!, name);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }
}
