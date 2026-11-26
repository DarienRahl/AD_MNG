using System.Management.Automation;
using System.Reflection;
using AdMng.PowerShell.Commands;

namespace AdMng.PowerShell.Tests;

/// <summary>R1 part 2: every command of the window has help (English, MAML) with a synopsis, a description, one example and every parameter described.</summary>
public sealed class CommandHelpTests
{
    public static TheoryData<string> CommandNames { get; } = [.. AdmCommands.Names];

    [Fact]
    public void The_help_file_is_beside_the_assembly_where_the_engine_finds_it()
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(AdmCommands).Assembly.Location)!, AdmCommands.HelpFileName);

        Assert.True(File.Exists(path), path);
    }

    [Theory]
    [MemberData(nameof(CommandNames))]
    public async Task A_command_has_a_synopsis_a_description_and_an_example(string command)
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(
            session,
            $"$h = Get-Help {command} -Full; $h.Synopsis.Trim(); $h.description[0].Text.Trim(); @($h.examples.example).Count; $h.examples.example[0].code.Trim(); $h.examples.example[0].remarks[0].Text.Trim()");

        var lines = TestSessions.Text(items).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.True(lines[0].Length > 20 && lines[0].EndsWith('.'), lines[0]);
        Assert.True(lines[1].Length > 40, lines[1]);
        Assert.Equal("1", lines[2]);
        Assert.StartsWith(command, lines[3], StringComparison.Ordinal);
        Assert.True(lines[4].Length > 10, lines[4]);
    }

    [Theory]
    [MemberData(nameof(CommandNames))]
    public async Task Every_parameter_of_a_command_is_described_and_help_lists_no_other(string command)
    {
        await using var session = await TestSessions.CreateAsync();
        var type = AdmCommands.Catalog.Single(entry => entry.Name == command).Type;
        var parameters = type.GetProperties()
            .Where(p => p.GetCustomAttributes<ParameterAttribute>().Any())
            .Select(p => p.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var items = await TestSessions.RunAsync(
            session,
            $"(Get-Help {command} -Full).parameters.parameter | ForEach-Object {{ $_.name + '|' + $_.description[0].Text.Trim() }}");

        var described = TestSessions.Text(items).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(parameters, described.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.All(described, pair => Assert.True(pair.Value.Length > 15, $"{command} -{pair.Key}: '{pair.Value}'"));
    }

    [Fact]
    public async Task Get_Help_shows_the_syntax_of_every_parameter_set_and_the_example()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "Get-Help Get-AdmUser -Examples | Out-String -Width 200");

        var text = TestSessions.Text(items);
        Assert.Contains("Get-AdmUser -Department", text, StringComparison.Ordinal);
        var syntax = TestSessions.Text(await TestSessions.RunAsync(session, "Get-Help Get-AdmUser | Out-String -Width 300"));
        Assert.Contains("Get-AdmUser [-Identity] <String[]>", syntax, StringComparison.Ordinal);
        Assert.Contains("[-Enabled <Boolean>]", syntax, StringComparison.Ordinal);
    }
}
