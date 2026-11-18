using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Tests;

/// <summary>D5: completion through the engine in the window's runspace; no file system completion in R1.</summary>
public sealed class CompletionTests
{
    [Fact]
    public async Task Get_Ad_completes_to_the_adm_commands()
    {
        await using var session = await TestSessions.CreateAsync();

        var result = await session.CompleteAsync("Get-Ad", 6, TestContext.Current.CancellationToken);

        Assert.Contains(result.Matches, m => m.Text == "Get-AdmVersion" && m.Kind == PowerShellCompletionKind.Command);
        Assert.Equal(0, result.ReplacementIndex);
        Assert.Equal(6, result.ReplacementLength);
    }

    [Fact]
    public async Task A_parameter_is_completed_after_a_dash()
    {
        await using var session = await TestSessions.CreateAsync();

        var result = await session.CompleteAsync("Get-AdmVersion -Verb", 20, TestContext.Current.CancellationToken);

        Assert.Contains(result.Matches, m => m.Text == "-Verbose" && m.Kind == PowerShellCompletionKind.Parameter);
    }

    [Fact]
    public async Task A_variable_is_completed()
    {
        await using var session = await TestSessions.CreateAsync();

        var result = await session.CompleteAsync("$PSVer", 6, TestContext.Current.CancellationToken);

        Assert.Contains(result.Matches, m => m.Text == "$PSVersionTable" && m.Kind == PowerShellCompletionKind.Variable);
    }

    [Fact]
    public async Task Variables_the_operator_defined_are_completed()
    {
        await using var session = await TestSessions.CreateAsync();
        await TestSessions.RunAsync(session, "$myOwnVariable = 1");

        var result = await session.CompleteAsync("$myOwn", 6, TestContext.Current.CancellationToken);

        Assert.Contains(result.Matches, m => m.Text == "$myOwnVariable");
    }

    [Theory]
    [InlineData("Get-ChildItem /", 15)]
    [InlineData("Get-Content ./", 14)]
    [InlineData("Set-Location ~/", 15)]
    public async Task Files_and_folders_are_never_offered(string input, int cursor)
    {
        await using var session = await TestSessions.CreateAsync();

        var result = await session.CompleteAsync(input, cursor, TestContext.Current.CancellationToken);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task Nothing_is_completed_while_a_command_runs()
    {
        await using var session = await TestSessions.CreateAsync();
        await using var run = session.RunAsync("Read-Host 'x'", TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await run.MoveNextAsync());

        var result = await session.CompleteAsync("Get-Ad", 6, TestContext.Current.CancellationToken);

        Assert.Empty(result.Matches);
        Assert.Same(PowerShellCompletionResult.Empty, result);
        Assert.IsType<PowerShellPromptItem>(run.Current).Respond("done");
        while (await run.MoveNextAsync())
        {
        }
    }

    [Fact]
    public async Task Completion_leaves_the_runspace_ready_for_the_next_command()
    {
        await using var session = await TestSessions.CreateAsync();

        await session.CompleteAsync("Get-Ad", 6, TestContext.Current.CancellationToken);
        var items = await TestSessions.RunAsync(session, "'ok'");

        Assert.Equal("ok\n", TestSessions.Text(items));
    }

    [Fact]
    public async Task A_cursor_outside_the_input_is_clamped()
    {
        await using var session = await TestSessions.CreateAsync();

        var result = await session.CompleteAsync("Get-Ad", 99, TestContext.Current.CancellationToken);

        Assert.Contains(result.Matches, m => m.Text == "Get-AdmVersion");
    }
}
