using System.Management.Automation;
using AdMng.Core.PowerShell;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.PowerShell.Tests;

/// <summary>The session: a script runs and streams, a run is stopped, one run at a time, the end item, dispose.</summary>
public sealed class PowerShellSessionTests
{
    [Fact]
    public async Task A_script_runs_and_its_output_streams_as_text_ending_with_the_end_item()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "'a'; 'b'; 1 + 2");

        Assert.Equal("a\nb\n3\n", TestSessions.Text(items));
        Assert.Equal(new PowerShellRunEndItem(PowerShellRunState.Completed, HadErrors: false), items[^1]);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task Variables_stay_between_runs_like_in_a_console()
    {
        await using var session = await TestSessions.CreateAsync();

        await TestSessions.RunAsync(session, "$answer = 41");
        var items = await TestSessions.RunAsync(session, "$answer + 1");

        Assert.Equal("42\n", TestSessions.Text(items));
    }

    [Fact]
    public async Task The_engines_own_formatting_makes_a_table_with_one_header()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "@(@{ Name = 'a'; Value = 1 }, @{ Name = 'b'; Value = 2 }) | ForEach-Object { New-Object PSObject -Property $_ } | Select-Object Name, Value");
        var text = TestSessions.Text(items);

        Assert.Equal(1, CountOccurrences(text, "Name"));
        Assert.Contains("a", text, StringComparison.Ordinal);
        Assert.Contains("b", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Object_mode_hands_over_the_pipelines_objects_unformatted()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = new List<PowerShellOutputItem>();
        await foreach (var item in session.RunObjectsAsync("1..3 | ForEach-Object { $_ * 2 }", TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        Assert.Equal([2, 4, 6], items.OfType<PowerShellObjectItem>().Select(o => (int)((PSObject)o.Value!).BaseObject).ToArray());
        Assert.Equal(PowerShellRunState.Completed, TestSessions.End(items).State);
    }

    [Fact]
    public async Task A_terminating_error_ends_the_run_as_failed_with_its_message()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "throw 'boom'");

        Assert.Contains("boom", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.Equal(new PowerShellRunEndItem(PowerShellRunState.Failed, HadErrors: true), items[^1]);
    }

    [Fact]
    public async Task A_non_terminating_error_is_text_of_the_error_stream_and_the_run_completes_with_errors()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "Get-NoSuchCommand; 'still here'");

        Assert.Contains("Get-NoSuchCommand", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.Equal("still here\n", TestSessions.Text(items));
        Assert.Equal(new PowerShellRunEndItem(PowerShellRunState.Completed, HadErrors: true), items[^1]);
    }

    [Fact]
    public async Task The_next_run_can_start_as_soon_as_the_end_item_arrived()
    {
        await using var session = await TestSessions.CreateAsync();

        for (var i = 0; i < 3; i++)
        {
            var items = await TestSessions.RunAsync(session, $"'run{i}'");
            Assert.Equal($"run{i}\n", TestSessions.Text(items));
        }
    }

    [Fact]
    public async Task A_second_run_while_one_is_active_is_refused()
    {
        await using var session = await TestSessions.CreateAsync();
        await using var first = session.RunAsync("Read-Host 'x'", TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await first.MoveNextAsync());
        var prompt = Assert.IsType<PowerShellPromptItem>(first.Current);
        Assert.True(session.IsRunning);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var unused in session.RunAsync("1", TestContext.Current.CancellationToken))
            {
                Assert.Fail("The refused run must not produce items.");
            }
        });

        prompt.Respond("done");
        while (await first.MoveNextAsync())
        {
        }

        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task A_running_script_is_stopped_and_ends_as_stopped()
    {
        await using var session = await TestSessions.CreateAsync();
        var items = new List<PowerShellOutputItem>();

        await foreach (var item in session.RunAsync("'started'; while ($true) { $i++ }", TestContext.Current.CancellationToken))
        {
            items.Add(item);
            if (item is PowerShellTextItem { Text: "started\n" })
            {
                await session.StopAsync();
            }
        }

        Assert.Equal(PowerShellRunState.Stopped, TestSessions.End(items).State);
        Assert.False(session.IsRunning);
    }

    /// <summary>
    /// A script that keeps writing (the case Stop exists for) stops too, and nothing it wrote arrives after the end item. The consumer
    /// keeps up here; a consumer that falls behind a flood is the case of audit PS-07.
    /// </summary>
    [Fact]
    public async Task A_running_script_that_keeps_writing_is_stopped_and_ends_as_stopped()
    {
        await using var session = await TestSessions.CreateAsync();
        var items = new List<PowerShellOutputItem>();

        await foreach (var item in session.RunAsync("'started'; while ($true) { 'line'; Start-Sleep -Milliseconds 5 }", TestContext.Current.CancellationToken))
        {
            items.Add(item);
            if (item is PowerShellTextItem { Text: "line\n" } && items.Count == 5)
            {
                await session.StopAsync();
            }
        }

        Assert.Equal(PowerShellRunState.Stopped, TestSessions.End(items).State);
        Assert.IsType<PowerShellRunEndItem>(items[^1]);
        Assert.True(items.Count >= 5);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task Cancelling_the_token_stops_the_pipeline_and_the_enumeration_still_ends_with_the_end_item()
    {
        await using var session = await TestSessions.CreateAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var items = new List<PowerShellOutputItem>();

        await foreach (var item in session.RunAsync("'started'; while ($true) { $i++ }", cts.Token))
        {
            items.Add(item);
            if (item is PowerShellTextItem { Text: "started\n" })
            {
                await cts.CancelAsync();
            }
        }

        Assert.Equal(PowerShellRunState.Stopped, TestSessions.End(items).State);
    }

    [Fact]
    public async Task A_command_that_waits_on_the_stop_token_ends_when_the_run_is_stopped()
    {
        await using var session = await TestSessions.CreateAsync(extra: TestCommands.All);
        var items = new List<PowerShellOutputItem>();

        await foreach (var item in session.RunAsync("Wait-AdmTest", TestContext.Current.CancellationToken))
        {
            items.Add(item);
            if (item is PowerShellTextItem { Text: "waiting\n" })
            {
                await session.StopAsync();
            }
        }

        Assert.Equal(PowerShellRunState.Stopped, TestSessions.End(items).State);
    }

    [Fact]
    public async Task A_stop_that_the_pipeline_does_not_obey_ends_the_enumeration_after_the_grace_period()
    {
        var time = new FakeTimeProvider();
        TestCommands.Release.Reset();
        await using var session = await TestSessions.CreateAsync(time: time, extra: TestCommands.All);
        var items = new List<PowerShellOutputItem>();
        try
        {
            await foreach (var item in session.RunAsync("Block-AdmTest", TestContext.Current.CancellationToken))
            {
                items.Add(item);
                if (item is PowerShellTextItem { Text: "blocking\n" })
                {
                    var stopping = session.StopAsync();
                    Assert.False(stopping.IsCompleted);
                    time.Advance(TimeSpan.FromSeconds(5));
                    await stopping;
                }
            }

            Assert.Equal(PowerShellRunState.Stopped, TestSessions.End(items).State);
            Assert.True(session.IsRunning);
        }
        finally
        {
            TestCommands.Release.Set();
        }
    }

    [Fact]
    public async Task Disposing_the_session_stops_a_running_script()
    {
        var session = await TestSessions.CreateAsync();
        await using var run = session.RunAsync("'started'; while ($true) { $i++ }", TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await run.MoveNextAsync());

        await session.DisposeAsync();
        await session.DisposeAsync();

        var last = run.Current;
        while (await run.MoveNextAsync())
        {
            last = run.Current;
        }

        Assert.Equal(PowerShellRunState.Stopped, Assert.IsType<PowerShellRunEndItem>(last).State);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var unused in session.RunAsync("1", TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task Two_sessions_do_not_share_a_runspace()
    {
        await using var one = await TestSessions.CreateAsync();
        await using var two = await TestSessions.CreateAsync();

        await TestSessions.RunAsync(one, "$mine = 'one'");
        var items = await TestSessions.RunAsync(two, "if (Test-Path variable:mine) { 'shared' } else { 'separate' }");

        Assert.Equal("separate\n", TestSessions.Text(items));
    }

    [Fact]
    public async Task The_output_width_decides_how_the_engine_wraps()
    {
        await using var session = await TestSessions.CreateAsync();
        const string Script = "New-Object PSObject -Property @{ Text = ('x' * 150) } | Format-Table Text -Wrap";

        session.OutputWidth = 40;
        var narrow = LongestLine(TestSessions.Text(await TestSessions.RunAsync(session, Script)));
        session.OutputWidth = 200;
        var wide = LongestLine(TestSessions.Text(await TestSessions.RunAsync(session, Script)));

        Assert.Equal(200, session.OutputWidth);
        Assert.True(narrow <= 40, $"narrow output has a line of {narrow} characters");
        Assert.True(wide >= 150, $"wide output has a line of only {wide} characters");
    }

    private static int LongestLine(string text) => text.Split('\n').Max(line => line.TrimEnd().Length);

    private static int CountOccurrences(string text, string word)
    {
        var count = 0;
        for (var index = text.IndexOf(word, StringComparison.Ordinal); index >= 0; index = text.IndexOf(word, index + word.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
