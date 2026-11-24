using AdMng.Core.PowerShell;
using AdMng.PowerShell.Commands;
using AdMng.PowerShell.Guards;

namespace AdMng.PowerShell.Tests;

/// <summary>
/// The window's command list is an allow-list (docs/dev/r1-powershell-window.md 4.6): a new session has exactly these commands,
/// on Linux and on Windows. A change of <see cref="CommandGuards"/> must change this list too - that is the point of writing it
/// out here a second time.
/// </summary>
public sealed class SessionCommandsTests
{
    /// <summary>The engine's own cmdlets the window keeps unchanged.</summary>
    private static readonly string[] EngineCmdlets =
    [
        "Add-Member", "Clear-Variable", "Compare-Object", "ConvertFrom-Csv", "ConvertFrom-Json", "ConvertTo-Csv", "ConvertTo-Json",
        "ConvertTo-SecureString",
        "Format-Custom", "Format-Hex", "Format-List", "Format-Table", "Format-Wide", "Get-Alias", "Get-ChildItem", "Get-Command",
        "Get-Content", "Get-Culture", "Get-Date", "Get-ExecutionPolicy", "Get-Help", "Get-Item", "Get-Location", "Get-Member",
        "Get-PSDrive", "Get-Random", "Get-UICulture", "Get-Unique", "Get-Variable", "Group-Object", "Import-Csv", "Join-Path",
        "Join-String", "Measure-Command", "Measure-Object", "New-Alias", "New-Guid", "New-TimeSpan", "New-Variable", "Out-Default",
        "Out-Host", "Out-Null", "Out-String", "Pop-Location", "Push-Location", "Read-Host", "Remove-Variable", "Resolve-Path",
        "Select-Object", "Select-String", "Set-Alias", "Set-Location", "Set-StrictMode", "Set-Variable", "Sort-Object", "Split-Path",
        "Start-Sleep", "Stop-Transcript", "Test-Path", "Where-Object", "Write-Debug", "Write-Error", "Write-Host", "Write-Information",
        "Write-Output", "Write-Progress", "Write-Verbose", "Write-Warning",
    ];

    /// <summary>Engine cmdlets the window replaces by its own command with fewer parameters.</summary>
    private static readonly string[] Replaced = ["ForEach-Object", "New-Object", "Start-Transcript", "Tee-Object"];

    /// <summary>The engine's function the window keeps (Tab completion calls TabExpansion2 by name).</summary>
    private static readonly string[] EngineFunctions = ["TabExpansion2"];

    /// <summary>Engine functions the window replaces by a compiled command of its own (Clear-Host clears the window, audit PS-12).</summary>
    private static readonly string[] WindowCommands = ["Clear-Host"];

    /// <summary>Names that answer with a message instead of "not recognized".</summary>
    private static readonly string[] Refused =
    [
        "Add-Content", "Export-Clixml", "Export-Csv", "Get-Credential", "Import-Module", "Invoke-Command", "Invoke-Expression", "Out-File",
        "Out-GridView", "Set-Content", "Start-Job", "Start-Process",
    ];

    [Fact]
    public async Task A_new_session_has_exactly_the_allowed_commands()
    {
        await using var session = await TestSessions.CreateAsync();

        var cmdlets = await TestSessions.RunAsync(session, "Get-Command -CommandType Cmdlet | ForEach-Object { $_.Name }");
        var functions = await TestSessions.RunAsync(session, "Get-Command -CommandType Function | ForEach-Object { $_.Name }");

        Assert.Equal(Sorted([.. EngineCmdlets, .. Replaced, .. WindowCommands, .. AdmCommands.Names]), Lines(cmdlets));
        Assert.Equal(Sorted([.. EngineFunctions, .. Refused]), Lines(functions));
    }

    [Fact]
    public void The_code_lists_the_same_commands_as_this_test()
    {
        Assert.Equal(Sorted(EngineCmdlets), Sorted([.. CommandGuards.EngineCmdlets]));
        Assert.Equal(Sorted(Replaced), Sorted([.. CommandGuards.Replacements.Select(r => r.Name)]));
        Assert.Equal(Sorted(EngineFunctions), Sorted([.. CommandGuards.EngineFunctions]));
        Assert.Equal(Sorted(WindowCommands), Sorted([.. CommandGuards.WindowCommands.Select(c => c.Name)]));
        Assert.Equal(Sorted(Refused), Sorted([.. CommandGuards.Refusals.Select(r => r.Name)]));
    }

    [Fact]
    public async Task Every_alias_points_at_a_command_of_the_session()
    {
        await using var session = await TestSessions.CreateAsync();

        var aliases = await TestSessions.RunAsync(
            session,
            "Get-Alias | Where-Object { -not (Get-Command -Name $_.Definition -ErrorAction SilentlyContinue) } | ForEach-Object { $_.Name + ' -> ' + $_.Definition }");
        var help = await TestSessions.RunAsync(session, "(Get-Alias help).Definition; (Get-Alias '%').Definition; (Get-Alias ipmo).Definition");

        Assert.Equal(string.Empty, TestSessions.Text(aliases));
        Assert.Equal("Get-Help\nForEach-Object\nImport-Module\n", TestSessions.Text(help));
    }

    /// <summary>
    /// A script file's text would reach the engine past InputGuard (a .ps1 with <c>using module</c>), so none runs: called by name or
    /// path from the command line, from a script block, from a function or from ForEach-Object; the function drive (text assigned to
    /// <c>function:x</c> becomes code) does not exist.
    /// </summary>
    [Theory]
    [InlineData("& 'SCRIPT'")]
    [InlineData(". 'SCRIPT'")]
    [InlineData("& { & 'SCRIPT' }")]
    [InlineData("function Invoke-Probe { . 'SCRIPT' }; Invoke-Probe")]
    [InlineData("1 | ForEach-Object { & 'SCRIPT' }")]
    [InlineData("${function:probe} = \"'the script ran'\"; probe")]
    public async Task Script_files_do_not_run_and_the_function_drive_does_not_exist(string script)
    {
        var folder = Directory.CreateTempSubdirectory("admng-ps-script-");
        try
        {
            var file = Path.Combine(folder.FullName, "probe.ps1");
            await File.WriteAllTextAsync(file, "'the script ran'", TestContext.Current.CancellationToken);
            await using var session = await TestSessions.CreateAsync();

            var items = await TestSessions.RunAsync(session, script.Replace("SCRIPT", file, StringComparison.Ordinal));

            Assert.Equal(string.Empty, TestSessions.Text(items));
            Assert.True(TestSessions.End(items).HadErrors);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A script file reached as a command object (Get-Command inside a script block finds it, and the call operator runs the object
    /// without a lookup): on Windows the engine asks the window's authorization manager, which refuses it. On Linux the engine asks no
    /// authorization manager (the window is not shipped there).
    /// </summary>
    [Fact]
    public async Task A_script_file_reached_as_a_command_object_is_refused_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The engine asks an authorization manager on Windows only.");
        var folder = Directory.CreateTempSubdirectory("admng-ps-script-");
        try
        {
            var file = Path.Combine(folder.FullName, "probe.ps1");
            await File.WriteAllTextAsync(file, "'the script ran'", TestContext.Current.CancellationToken);
            await using var session = await TestSessions.CreateAsync();

            var items = await TestSessions.RunAsync(session, "& { & (Get-Command -Name '" + file + "' -CommandType ExternalScript) }");

            Assert.Equal(string.Empty, TestSessions.Text(items));
            Assert.Contains("Script files", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static string[] Sorted(IEnumerable<string> names) => [.. names.Order(StringComparer.OrdinalIgnoreCase)];

    private static string[] Lines(IEnumerable<PowerShellOutputItem> items) =>
        Sorted(TestSessions.Text(items).Split('\n', StringSplitOptions.RemoveEmptyEntries));
}
