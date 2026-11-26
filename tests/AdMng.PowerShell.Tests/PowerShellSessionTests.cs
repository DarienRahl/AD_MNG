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

    /// <summary>
    /// Audit PS-07: a script that floods a consumer that fell behind fills the channel; a stop then dropped the end item (posted
    /// through the path that drops an item once the run is stopped) and the enumeration ended with text.
    /// </summary>
    [Fact]
    public async Task A_flood_stopped_while_the_consumer_is_behind_still_ends_with_the_end_item()
    {
        await using var session = await TestSessions.CreateAsync();
        var items = new List<PowerShellOutputItem>();
        await using var run = session.RunAsync("while ($true) { 'x' }", TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await run.MoveNextAsync());
        items.Add(run.Current);

        // The consumer stays away until the producer waits for room in the full channel.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (session.BufferedItemCount < PowerShellSession.ChannelCapacity && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Equal(PowerShellSession.ChannelCapacity, session.BufferedItemCount);
        await session.StopAsync();
        while (await run.MoveNextAsync())
        {
            items.Add(run.Current);
        }

        var end = Assert.IsType<PowerShellRunEndItem>(items[^1]);
        Assert.Equal(PowerShellRunState.Stopped, end.State);
        Assert.Single(items.OfType<PowerShellRunEndItem>());
        Assert.False(session.IsRunning);
    }

    /// <summary>
    /// Audit PS-09: the channel was bounded by its number of items only, so a flood of large strings held 4096 of them - 790 MB with
    /// strings of 100,000 characters - while the window was behind. The text it holds is bounded by its size too.
    /// </summary>
    [Fact]
    public async Task A_flood_of_large_strings_holds_at_most_the_channels_size_of_text_while_the_consumer_is_behind()
    {
        await using var session = await TestSessions.CreateAsync();
        await using var run = session.RunAsync("$big = 'y' * 100000; while ($true) { $big }", TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await run.MoveNextAsync());

        // The consumer stays away; the producer runs until it waits for room (the held text stops growing).
        var held = -1L;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            var now = session.BufferedCharacters;
            if (now == held && now > 0)
            {
                break;
            }

            held = now;
        }

        Assert.InRange(session.BufferedCharacters, PowerShellSession.ChannelCharacterCapacity - 100_001, PowerShellSession.ChannelCharacterCapacity);
        Assert.True(session.BufferedItemCount < 20, $"{session.BufferedItemCount} items held");

        await session.StopAsync();
        var last = run.Current;
        while (await run.MoveNextAsync())
        {
            last = run.Current;
        }

        Assert.Equal(PowerShellRunState.Stopped, Assert.IsType<PowerShellRunEndItem>(last).State);
        Assert.Equal(0, session.BufferedCharacters);
    }

    [Fact]
    public async Task One_text_larger_than_the_channels_size_of_text_still_arrives_whole()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, $"'z' * {PowerShellSession.ChannelCharacterCapacity + 10}; 'after'");

        Assert.Equal(new string('z', PowerShellSession.ChannelCharacterCapacity + 10) + "\nafter\n", TestSessions.Text(items));
        Assert.Equal(PowerShellRunState.Completed, TestSessions.End(items).State);
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

    /// <summary>
    /// A command stuck in .NET code that ignores Stop is left after the grace period: the run ends as
    /// <see cref="PowerShellRunState.Abandoned"/> and the session is idle at once, in a new runspace without the old one's variables,
    /// functions and language mode changes of the script. Before the second red team of R1 the session stayed busy for good.
    /// </summary>
    [Fact]
    public async Task A_stop_that_the_pipeline_does_not_obey_leaves_the_run_after_the_grace_period_and_the_next_command_runs_in_a_new_runspace()
    {
        var time = new FakeTimeProvider();
        TestCommands.Release.Reset();
        var context = new FakeWindowContext();
        await using var session = await TestSessions.CreateAsync(context, time: time, extra: TestCommands.All);
        await TestSessions.RunAsync(session, "$before = 'kept'; function Get-Before { 'kept' }");
        Assert.Equal("False\nkept\n", TestSessions.Text(await TestSessions.RunAsync(session, "$null -eq $before; Get-Before")));
        var oldHost = TestSessions.Text(await TestSessions.RunAsync(session, "$Host.InstanceId"));
        var items = new List<PowerShellOutputItem>();
        try
        {
            await foreach (var item in session.RunAsync("Block-AdmTest; 'after'", TestContext.Current.CancellationToken))
            {
                items.Add(item);
                if (item is PowerShellTextItem { Text: "blocking\n" })
                {
                    var stopping = session.StopAsync();
                    await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
                    Assert.False(stopping.IsCompleted);
                    Assert.True(session.IsRunning);
                    time.Advance(TimeSpan.FromSeconds(5));
                    await stopping;
                    Assert.False(session.IsRunning);
                }
            }

            Assert.Equal(new PowerShellRunEndItem(PowerShellRunState.Abandoned, HadErrors: false), TestSessions.End(items));
            Assert.DoesNotContain("after", TestSessions.Text(items), StringComparison.Ordinal);
            Assert.Equal(PowerShellLanguageMode.Constrained, session.LanguageMode);

            // The new runspace has its own host and the window's commands still find their window.
            var next = await TestSessions.RunAsync(session, "$null -eq $before; Test-AdmContext");
            Assert.Equal("True\n" + context.Session.Info.DomainDnsName + "\nFalse\n", TestSessions.Text(next));
            Assert.Equal(new PowerShellRunEndItem(PowerShellRunState.Completed, HadErrors: false), TestSessions.End(next));
            Assert.Contains("Get-Before", TestSessions.Text(await TestSessions.RunAsync(session, "Get-Before"), PowerShellStream.Error), StringComparison.Ordinal);
            Assert.NotEqual(oldHost, TestSessions.Text(await TestSessions.RunAsync(session, "$Host.InstanceId")));
        }
        finally
        {
            TestCommands.Release.Set();
        }

        // The left thread returns: its pipeline ends in the old runspace, and the session goes on.
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        Assert.Equal("next\n", TestSessions.Text(await TestSessions.RunAsync(session, "'next'")));
    }

    /// <summary>
    /// Audit PS-10: a completion stuck in .NET code that its stop cannot interrupt (a completer of the operator's that calls such code)
    /// held the session's gate for good. After its time limit and the grace period the session leaves it like a left run and goes on
    /// in a new runspace.
    /// </summary>
    [Fact]
    public async Task A_completion_that_its_stop_cannot_end_is_left_after_the_grace_period_and_the_next_command_runs_in_a_new_runspace()
    {
        var time = new FakeTimeProvider();
        TestCommands.Release.Reset();
        await using var session = await TestSessions.CreateAsync(time: time, extra: TestCommands.All);
        await TestSessions.RunAsync(session, "$before = 'kept'; function global:TabExpansion2 { Block-AdmTest }");
        try
        {
            var completing = session.CompleteAsync("Get-Ad", 6, TestContext.Current.CancellationToken).AsTask();
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.False(completing.IsCompleted);

            time.Advance(PowerShellSessionOptions.DefaultCompletionTimeLimit);
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.False(completing.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(5));

            var result = await completing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Empty(result.Matches);
            var next = await TestSessions.RunAsync(session, "$null -eq $before; 'next'");
            Assert.Equal("True\nnext\n", TestSessions.Text(next));
            var again = await session.CompleteAsync("Get-Ad", 6, TestContext.Current.CancellationToken);
            Assert.Contains(again.Matches, match => match.Text == "Get-AdmVersion");
        }
        finally
        {
            TestCommands.Release.Set();
        }
    }

    /// <summary>
    /// Every command of the window runs on the runspace's own thread, and it is a background thread: a left run's thread that never
    /// returns must not keep the program's process alive (<c>RegexLimitTests</c> proves the exit in a child process). Also in a runspace
    /// the session made after it left a run.
    /// </summary>
    [Fact]
    public async Task The_commands_of_the_window_run_on_one_background_thread_also_after_a_left_run()
    {
        var time = new FakeTimeProvider();
        TestCommands.Release.Reset();
        await using var session = await TestSessions.CreateAsync(time: time, extra: TestCommands.All);
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken);
        const string Thread = "[System.Threading.Thread]::CurrentThread.IsBackground; [System.Threading.Thread]::CurrentThread.ManagedThreadId";
        var first = TestSessions.Text(await TestSessions.RunAsync(session, Thread));
        var second = TestSessions.Text(await TestSessions.RunAsync(session, Thread));
        try
        {
            await LeaveABlockedRunAsync(session, time);
        }
        finally
        {
            TestCommands.Release.Set();
        }

        var after = TestSessions.Text(await TestSessions.RunAsync(session, Thread));
        Assert.StartsWith("True\n", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
        Assert.StartsWith("True\n", after, StringComparison.Ordinal);
        Assert.NotEqual(first, after);
    }

    /// <summary>The new runspace keeps the language mode the operator chose: the trusted mode stays on, the window's toggle still tells the truth.</summary>
    [Fact]
    public async Task A_left_run_of_the_trusted_mode_goes_on_in_a_trusted_runspace()
    {
        var time = new FakeTimeProvider();
        TestCommands.Release.Reset();
        await using var session = await TestSessions.CreateAsync(time: time, extra: TestCommands.All);
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(PowerShellRunState.Abandoned, TestSessions.End(await LeaveABlockedRunAsync(session, time)).State);
        }
        finally
        {
            TestCommands.Release.Set();
        }

        Assert.Equal(PowerShellLanguageMode.Full, session.LanguageMode);
        Assert.Equal("FullLanguage\n", TestSessions.Text(await TestSessions.RunAsync(session, "$ExecutionContext.SessionState.LanguageMode")));
    }

    /// <summary>A lockdown enforced since the trusted mode was turned on wins: the new runspace is constrained, and the session says so.</summary>
    [Fact]
    public async Task A_left_run_of_the_trusted_mode_goes_on_constrained_when_a_lockdown_is_now_enforced()
    {
        var time = new FakeTimeProvider();
        var probe = new FakeLockdownProbe();
        TestCommands.Release.Reset();
        await using var session = await TestSessions.CreateAsync(probe: probe, time: time, extra: TestCommands.All);
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken);
        probe.State = PowerShellLockdownState.Enforce;
        try
        {
            Assert.Equal(PowerShellRunState.Abandoned, TestSessions.End(await LeaveABlockedRunAsync(session, time)).State);
        }
        finally
        {
            TestCommands.Release.Set();
        }

        Assert.Equal(PowerShellLanguageMode.Constrained, session.LanguageMode);
        Assert.Equal(PowerShellTrustedModeBlock.SystemLockdown, session.TrustedModeBlock);
        Assert.Equal("ConstrainedLanguage\n", TestSessions.Text(await TestSessions.RunAsync(session, "$ExecutionContext.SessionState.LanguageMode")));
    }

    /// <summary>Closing the window during a command that ignores Stop: dispose gives up after the grace period, makes no new runspace and returns.</summary>
    [Fact]
    public async Task Disposing_the_session_during_a_run_that_ignores_stop_leaves_the_run_and_returns_after_the_grace_period()
    {
        var time = new FakeTimeProvider();
        TestCommands.Release.Reset();
        var session = await TestSessions.CreateAsync(time: time, extra: TestCommands.All);
        try
        {
            await using var run = session.RunAsync("Block-AdmTest", TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            Assert.True(await run.MoveNextAsync());

            var disposing = session.DisposeAsync().AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
            Assert.False(disposing.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(5));
            await disposing.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            var last = run.Current;
            while (await run.MoveNextAsync())
            {
                last = run.Current;
            }

            Assert.Equal(PowerShellRunState.Abandoned, Assert.IsType<PowerShellRunEndItem>(last).State);
            Assert.False(session.IsRunning);
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            {
                await foreach (var unused in session.RunAsync("1", TestContext.Current.CancellationToken))
                {
                }
            });
        }
        finally
        {
            TestCommands.Release.Set();
        }
    }

    /// <summary>Runs <c>Block-AdmTest</c>, presses Stop and lets the grace period pass; the items of the run.</summary>
    private static async Task<List<PowerShellOutputItem>> LeaveABlockedRunAsync(PowerShellSession session, FakeTimeProvider time)
    {
        var items = new List<PowerShellOutputItem>();
        await foreach (var item in session.RunAsync("Block-AdmTest", TestContext.Current.CancellationToken))
        {
            items.Add(item);
            if (item is PowerShellTextItem { Text: "blocking\n" })
            {
                var stopping = session.StopAsync();
                time.Advance(TimeSpan.FromSeconds(5));
                await stopping.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            }
        }

        return items;
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
