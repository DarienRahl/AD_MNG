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
}
