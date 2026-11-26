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

    /// <summary>
    /// Every way to reach a refused command meets the refusal or finds nothing: the name, the alias, the module-qualified name (the
    /// cmdlet is not in the session), the command object of Get-Command, a call by a string, and the two ways that never look up a
    /// name at all - the background operator (it starts the engine's Start-Job directly) and <c>using module</c> (it imports directly).
    /// </summary>
    [Theory]
    [InlineData("Start-Job { 1 }", "Background jobs are not available")]
    [InlineData("sajb { 1 }", "Background jobs are not available")]
    [InlineData("& 'Start-Job' { 1 }", "Background jobs are not available")]
    [InlineData("& (Get-Command Start-Job) { 1 }", "Background jobs are not available")]
    [InlineData("Microsoft.PowerShell.Core\\Start-Job { 1 }", "is not recognized")]
    [InlineData("Get-Date &", "background operator")]
    [InlineData("Import-Module Foo", "Import-Module is limited to a list of modules")]
    [InlineData("ipmo Foo", "Import-Module is limited to a list of modules")]
    [InlineData("& (Get-Command Import-Module) Foo", "Import-Module is limited to a list of modules")]
    [InlineData("Microsoft.PowerShell.Core\\Import-Module Foo", "is not recognized")]
    [InlineData("using module Foo", "using module")]
    public async Task Every_way_to_a_refused_command_meets_the_refusal(string script, string expected)
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, script);

        Assert.Contains(expected, TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.True(TestSessions.End(items).HadErrors);
    }

    [Fact]
    public async Task The_refused_names_are_functions_of_the_session_and_every_refusal_is_listed()
    {
        await using var session = await TestSessions.CreateAsync();
        var names = string.Join(", ", CommandGuards.Refusals.Select(r => r.Name));

        var items = await TestSessions.RunAsync(session, "Get-Command " + names + " | ForEach-Object { $_.Name + ':' + $_.CommandType }");

        Assert.Equal(
            CommandGuards.Refusals.Select(r => r.Name + ":Function").Order(StringComparer.Ordinal).ToArray(),
            TestSessions.Text(items).Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(CommandGuards.Refusals, r => r.Name == "Start-Job");
        Assert.Contains(CommandGuards.Refusals, r => r.Name == "Import-Module");
    }

    [Fact]
    public async Task No_profile_is_loaded_and_psreadline_is_not_loaded_or_loadable()
    {
        await using var session = await TestSessions.CreateAsync();

        // PSReadLine may be INSTALLED on the machine (the Windows runner has it): nothing loads it.
        var items = await TestSessions.RunAsync(
            session,
            "\"[$PROFILE]\"; (Get-Command -Name Get-PSReadLineOption, Set-PSReadLineOption -ErrorAction SilentlyContinue | Measure-Object).Count; Import-Module PSReadLine -ErrorAction SilentlyContinue; (Get-Command -Name Get-PSReadLineOption, Set-PSReadLineOption -ErrorAction SilentlyContinue | Measure-Object).Count");

        Assert.Equal("[]\n0\n0\n", TestSessions.Text(items));
        Assert.Contains("Import-Module is limited", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
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
}
