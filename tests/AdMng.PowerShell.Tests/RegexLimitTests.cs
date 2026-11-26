using AdMng.Core.PowerShell;
using Microsoft.Extensions.Time.Testing;

namespace AdMng.PowerShell.Tests;

/// <summary>
/// A regular expression the operator types cannot hold the window (red team of R1, goal h). <c>-match</c>, <c>-replace</c>,
/// <c>-split</c>, <c>switch -regex</c>, <c>Select-String</c> and <c>[regex]</c> - all of them allowed in ConstrainedLanguage - share
/// .NET's regular expression engine, and Stop does not interrupt a match: '^(a+)+$' against thirty a's and a '!' backtracks for
/// hours, Stop gave up after its grace period, the session stayed busy for good ("A command is already running in this window")
/// and the thread kept a processor core busy until the program ended. The process-wide time limit of a match (set at the start
/// of the process, as the program does it) turns such a match into an error of the command.
/// </summary>
public sealed class RegexLimitTests
{
    private const string Backtracking = "'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!'";

    /// <summary>Every way of the language to a regular expression, each with the pattern that backtracks.</summary>
    private static readonly string[] Matches =
    [
        Backtracking + " -match '^(a+)+$'",
        Backtracking + " -replace '^(a+)+$', 'b'",
        Backtracking + " -split '^(a+)+$'",
        "switch -regex (" + Backtracking + ") { '^(a+)+$' { 'matched' } }",
        Backtracking + " | Select-String -Pattern '^(a+)+$'",
        "[regex]::IsMatch(" + Backtracking + ", '^(a+)+$')",
        "[regex]::new('^(a+)+$').IsMatch(" + Backtracking + ")",
    ];

    /// <summary>Each way in the window's default mode; the trusted mode uses the same engine, so one way there (each match takes 2 s).</summary>
    public static TheoryData<string, PowerShellLanguageMode> MatchesInBothModes
    {
        get
        {
            var data = new TheoryData<string, PowerShellLanguageMode>();
            foreach (var match in Matches)
            {
                data.Add(match, PowerShellLanguageMode.Constrained);
            }

            data.Add(Matches[0], PowerShellLanguageMode.Full);
            return data;
        }
    }

    /// <summary>The precondition of the tests below, set the way the program sets it (<see cref="ProcessStart"/>).</summary>
    [Fact]
    public void The_process_limits_every_regular_expression_without_a_limit_of_its_own()
    {
        Assert.True(PowerShellEnvironment.RegularExpressionsLimited, "The test process did not set the limit before its first regular expression.");
        Assert.Equal(TimeSpan.FromSeconds(2), PowerShellEnvironment.RegexMatchTimeout);
        Assert.Equal(PowerShellEnvironment.RegexMatchTimeout, new System.Text.RegularExpressions.Regex("a").MatchTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), new System.Text.RegularExpressions.Regex("a", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(30)).MatchTimeout);

        // Shorter than the grace period of Stop, so that Stop ends the match and not only the window's wait for it.
        Assert.True(PowerShellEnvironment.RegexMatchTimeout < new PowerShellSessionOptions().StopGracePeriod);

        // Idempotent.
        PowerShellEnvironment.LimitRegularExpressions();
        Assert.True(PowerShellEnvironment.RegularExpressionsLimited);
    }

    [Theory]
    [MemberData(nameof(MatchesInBothModes))]
    public async Task A_match_that_backtracks_for_hours_ends_as_an_error_of_its_statement_and_the_window_runs_the_next_command(string match, PowerShellLanguageMode mode)
    {
        Assert.True(PowerShellEnvironment.RegularExpressionsLimited);
        await using var session = await TestSessions.CreateAsync();
        if (mode == PowerShellLanguageMode.Full)
        {
            await session.SetLanguageModeAsync(mode, TestContext.Current.CancellationToken);
        }

        // Without the limit the match does not end: the test stops it after 20 s, and the end is the one of a stop given up on.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var items = new List<PowerShellOutputItem>();
        await foreach (var item in session.RunAsync(match + "; 'after'", limit.Token))
        {
            items.Add(item);
        }

        var end = TestSessions.End(items);
        Assert.True(end.State == PowerShellRunState.Completed, $"{end.State} after {clock.Elapsed.TotalSeconds:0.0} s: {TestSessions.Dump(items)}");
        Assert.True(end.HadErrors);
        Assert.Contains("timed out", TestSessions.Text(items, PowerShellStream.Error), StringComparison.Ordinal);
        Assert.EndsWith("after\n", TestSessions.Text(items), StringComparison.Ordinal);
        Assert.False(session.IsRunning);

        var next = await TestSessions.RunAsync(session, "'next'");
        Assert.Equal("next\n", TestSessions.Text(next));
    }

    /// <summary>The error is an ordinary one: a script catches it (the smoke test of the published program relies on this).</summary>
    [Fact]
    public async Task A_script_catches_the_error_of_a_match_that_took_too_long()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "try { $null = " + Backtracking + " -match '^(a+)+$'; 'matched' } catch { 'limited' }");

        Assert.Equal("limited\n", TestSessions.Text(items));
        Assert.Equal(new PowerShellRunEndItem(PowerShellRunState.Completed, HadErrors: false), TestSessions.End(items));
    }

    /// <summary>
    /// Stop during such matches ends the command itself - not the window's wait for it: the grace period of this session never passes
    /// (a fake clock that nobody advances), so <see cref="IPowerShellSession.StopAsync"/> returns only when the command really ended.
    /// </summary>
    [Fact]
    public async Task Stop_during_backtracking_matches_ends_the_command_within_the_grace_period()
    {
        var time = new FakeTimeProvider();
        await using var session = await TestSessions.CreateAsync(time: time);
        var items = new List<PowerShellOutputItem>();
        var stopReturned = true;

        await foreach (var item in session.RunAsync("'started'; while ($true) { $null = " + Backtracking + " -match '^(a+)+$' }", TestContext.Current.CancellationToken))
        {
            items.Add(item);
            if (item is PowerShellTextItem { Text: "started\n" })
            {
                var stopping = session.StopAsync();
                stopReturned = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)) == stopping;
                if (!stopReturned)
                {
                    // Let the window give up, so that the test ends and says why.
                    time.Advance(TimeSpan.FromSeconds(5));
                    await stopping;
                }
            }
        }

        Assert.True(stopReturned, "Stop waited for the whole grace period: the match was not interrupted. " + TestSessions.Dump(items));
        Assert.Equal(PowerShellRunState.Stopped, TestSessions.End(items).State);
        Assert.False(session.IsRunning);
        Assert.Equal("next\n", TestSessions.Text(await TestSessions.RunAsync(session, "'next'")));
    }

    /// <summary>
    /// The second red team of R1 (goal h): a <c>[regex]</c> made with a time limit of its own - <c>[timespan]::FromHours(1)</c> or
    /// <c>[regex]::InfiniteMatchTimeout</c>, all of it allowed in ConstrainedLanguage - is not bound by the process's limit, so the
    /// match backtracks past Stop. The red team's text had 36 a's (hours); 26 a's backtrack for about 5 s here, long enough to outlast
    /// the second this test gives Stop, short enough not to keep a core of the test run busy for long.
    /// </summary>
    public static TheoryData<string> MatchesWithTheirOwnLimit { get; } = new(
        "[regex]::new('^(a+)+$', 'None', [timespan]::FromHours(1)).IsMatch(" + LongBacktracking + ")",
        "[regex]::Matches(" + LongBacktracking + ", '^(a+)+$', [System.Text.RegularExpressions.RegexOptions]::None, [timespan]::FromHours(1)).Count",
        "[regex]::new('^(a+)+$', [System.Text.RegularExpressions.RegexOptions]::None, [regex]::InfiniteMatchTimeout).IsMatch(" + LongBacktracking + ")");

    private const string LongBacktracking = "'aaaaaaaaaaaaaaaaaaaaaaaaaa!'";

    /// <summary>
    /// Stop cannot end such a match, and before the fix the window stayed busy for good ("A command is already running in this window").
    /// After the grace period the session leaves the run - it ends as <see cref="PowerShellRunState.Abandoned"/> - and the next command
    /// runs at once in a new runspace, without what the old one held.
    /// </summary>
    [Theory]
    [MemberData(nameof(MatchesWithTheirOwnLimit))]
    public async Task Stop_of_a_match_with_a_longer_limit_of_its_own_leaves_it_and_the_window_runs_the_next_command(string match)
    {
        var time = new FakeTimeProvider();
        await using var session = await TestSessions.CreateAsync(time: time);
        await TestSessions.RunAsync(session, "$before = 'kept'");
        var items = new List<PowerShellOutputItem>();
        var stopEndedTheMatch = false;

        await foreach (var item in session.RunAsync("'started'; " + match + "; 'after'", TestContext.Current.CancellationToken))
        {
            items.Add(item);
            if (item is PowerShellTextItem { Text: "started\n" })
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
                var stopping = session.StopAsync();
                stopEndedTheMatch = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)) == stopping;

                // The grace period passes.
                time.Advance(TimeSpan.FromSeconds(5));
                await stopping.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            }
        }

        Assert.False(stopEndedTheMatch, "Stop ended the match: the case does not show what it is about. " + TestSessions.Dump(items));
        Assert.False(session.IsRunning, "The window is still busy with the match it gave up on. " + TestSessions.Dump(items));
        var next = await TestSessions.RunAsync(session, "41 + 1; $null -eq $before");
        Assert.Equal("42\nTrue\n", TestSessions.Text(next));
        Assert.Equal(new PowerShellRunEndItem(PowerShellRunState.Completed, HadErrors: false), TestSessions.End(next));

        Assert.Equal(PowerShellRunState.Abandoned, TestSessions.End(items).State);
        Assert.DoesNotContain("after", TestSessions.Text(items), StringComparison.Ordinal);
    }

    /// <summary>
    /// The left thread of such a match never returns ([regex]::InfiniteMatchTimeout), and the engine's pipeline thread is a foreground
    /// thread: the program must still end when its last window closed. A child process runs the match, Stop, the end of the window and
    /// the end of <c>Main</c>; it has to exit on its own.
    /// </summary>
    [Fact]
    public async Task A_process_whose_window_left_a_match_that_never_ends_still_exits()
    {
        using var home = new TempFolder();

        var result = await ChildProcess.RunAsync("left-run", home.Home, home.ReportPath, timeout: TimeSpan.FromSeconds(60));

        Assert.True(result.ExitCode == 0, "The child failed:\n" + result.Output);
        Assert.Contains("end=Abandoned\ndone", result.Report, StringComparison.Ordinal);
    }
}
