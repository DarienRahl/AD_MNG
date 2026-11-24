using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Tests;

/// <summary>
/// How a run ends besides its output (audit PS-12, the session's part): <c>exit</c> ends the run and says so in its end item (it was
/// swallowed - <c>WindowHost.ExitRequested</c> had no subscriber), <c>Clear-Host</c> / <c>cls</c> clears the window (it did nothing; on
/// Linux the engine's function wrote terminal codes to the process's console), and a run the operator stopped is not counted as a run
/// with errors (the engine's HadErrors is true for every stop).
/// </summary>
public sealed class SessionEndingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("'before'; exit 5; 'after'", 5)]
    [InlineData("'before'; exit; 'after'", 0)]
    [InlineData("function Stop-Here { exit 3 }; 'before'; Stop-Here; 'after'", 3)]
    [InlineData("'before'; & { exit 7 }; 'after'", 7)]
    public async Task Exit_ends_the_run_and_its_end_item_has_the_exit_code(string script, int code)
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, script);

        Assert.Equal("before\n", TestSessions.Text(items));
        Assert.Equal(new PowerShellRunEndItem(PowerShellRunState.Completed, HadErrors: false, ExitCode: code), TestSessions.End(items));
        Assert.Equal("next\n", TestSessions.Text(await TestSessions.RunAsync(session, "'next'")));
    }

    [Fact]
    public async Task A_run_without_exit_has_no_exit_code_and_the_next_one_neither()
    {
        await using var session = await TestSessions.CreateAsync();
        await TestSessions.RunAsync(session, "exit 4");

        var items = await TestSessions.RunAsync(session, "'plain'");

        Assert.Null(TestSessions.End(items).ExitCode);
    }

    [Theory]
    [InlineData("'before'; Clear-Host; 'after'")]
    [InlineData("'before'; cls; 'after'")]
    public async Task Clear_Host_clears_the_window_in_the_order_of_the_output(string script)
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, script);

        Assert.Equal(["Output:before\\n", "PowerShellClearItem", "Output:after\\n"], TestSessions.Dump(items).Split(" | ")[..3]);
        Assert.Equal(PowerShellRunState.Completed, TestSessions.End(items).State);
        Assert.False(TestSessions.End(items).HadErrors);
    }

    [Fact]
    public async Task Clear_Host_works_in_the_trusted_mode_too()
    {
        await using var session = await TestSessions.CreateAsync();
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, Ct);

        var items = await TestSessions.RunAsync(session, "Clear-Host");

        Assert.Single(items.OfType<PowerShellClearItem>());
    }

    [Fact]
    public async Task A_run_the_operator_stopped_does_not_count_as_a_run_with_errors()
    {
        await using var session = await TestSessions.CreateAsync();
        var items = new List<PowerShellOutputItem>();

        await foreach (var item in session.RunAsync("'started'; while ($true) { $i++ }", Ct))
        {
            items.Add(item);
            if (item is PowerShellTextItem { Text: "started\n" })
            {
                await session.StopAsync();
            }
        }

        Assert.Equal(new PowerShellRunEndItem(PowerShellRunState.Stopped, HadErrors: false), TestSessions.End(items));
    }

    [Fact]
    public async Task A_stopped_run_that_wrote_an_error_before_still_counts_it()
    {
        await using var session = await TestSessions.CreateAsync();
        var items = new List<PowerShellOutputItem>();

        await foreach (var item in session.RunAsync("Write-Error 'first'; 'started'; while ($true) { $i++ }", Ct))
        {
            items.Add(item);
            if (item is PowerShellTextItem { Text: "started\n" })
            {
                await session.StopAsync();
            }
        }

        Assert.Equal(new PowerShellRunEndItem(PowerShellRunState.Stopped, HadErrors: true), TestSessions.End(items));
    }
}
