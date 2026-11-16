using AdMng.Core.PowerShell;
using AdMng.PowerShell.Guards;

namespace AdMng.PowerShell.Tests;

/// <summary>D2: no profile, no PSReadLine, no jobs, Import-Module limited to a list that is empty in R1, execution policy.</summary>
public sealed class GuardTests
{
    [Fact]
    public async Task Start_Job_is_refused_with_a_clear_message_naming_the_command_and_the_script_goes_on()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "Start-Job -ScriptBlock { 1 } -Name x; 'after'");

        var error = TestSessions.Text(items, PowerShellStream.Error);
        Assert.Contains("Start-Job", error, StringComparison.Ordinal);
        Assert.Contains("Background jobs are not available", error, StringComparison.Ordinal);
        Assert.Equal("after\n", TestSessions.Text(items));
        Assert.True(TestSessions.End(items).HadErrors);
    }

    [Theory]
    [InlineData("Import-Module Microsoft.PowerShell.Utility")]
    [InlineData("Import-Module -Name Foo -Force -Global")]
    [InlineData("ipmo Bar")]
    public async Task Import_Module_of_any_module_is_refused_while_the_list_is_empty(string script)
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, script);

        var error = TestSessions.Text(items, PowerShellStream.Error);
        Assert.Contains("Import-Module is limited to a list of modules", error, StringComparison.Ordinal);
        Assert.Empty(CommandGuards.AllowedModules);
        Assert.True(TestSessions.End(items).HadErrors);
    }

    [Fact]
    public async Task The_guards_cannot_be_bypassed_by_the_module_qualified_name()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "Microsoft.PowerShell.Core\\Import-Module Foo; Microsoft.PowerShell.Core\\Start-Job { 1 }");

        Assert.Equal(2, CountWord(TestSessions.Text(items, PowerShellStream.Error), "is not recognized"));
        Assert.True(TestSessions.End(items).HadErrors);
    }

    [Fact]
    public async Task The_guarded_names_are_functions_of_the_session_and_every_refusal_is_listed()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "Get-Command Start-Job, Import-Module | ForEach-Object { $_.Name + ':' + $_.CommandType }");

        Assert.Equal("Import-Module:Function\nStart-Job:Function\n", TestSessions.Text(items).Replace("Start-Job:Function\nImport-Module:Function\n", "Import-Module:Function\nStart-Job:Function\n", StringComparison.Ordinal));
        Assert.Equal(["Start-Job", "Import-Module"], CommandGuards.Refusals.Select(r => r.Name).ToArray());
    }

    [Fact]
    public async Task No_profile_is_loaded_and_psreadline_is_not_there()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(
            session,
            "\"[$PROFILE]\"; (Get-Module -Name PSReadLine | Measure-Object).Count; (Get-Module -ListAvailable -Name PSReadLine | Measure-Object).Count; (Get-Command Get-PSReadLineOption -ErrorAction SilentlyContinue | Measure-Object).Count");

        Assert.Equal("[]\n0\n0\n0\n", TestSessions.Text(items));
    }

    [Fact]
    public async Task Module_auto_loading_is_off_in_the_runspace()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "$PSModuleAutoLoadingPreference");

        Assert.Equal("None\n", TestSessions.Text(items));
    }

    [Fact]
    public async Task The_execution_policy_is_restricted_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The execution policy exists on Windows only.");
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "Get-ExecutionPolicy");

        Assert.Equal("Restricted\n", TestSessions.Text(items));
    }

    private static int CountWord(string text, string word)
    {
        var count = 0;
        for (var index = text.IndexOf(word, StringComparison.Ordinal); index >= 0; index = text.IndexOf(word, index + word.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
