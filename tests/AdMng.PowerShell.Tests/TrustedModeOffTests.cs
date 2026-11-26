using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Tests;

/// <summary>
/// Turning the trusted mode off (audit PS-02): the session goes on in a NEW runspace in ConstrainedLanguage, so nothing the trusted mode
/// defined - a function, an alias, a script block in a variable, a member it added to the window's host - can run in FullLanguage
/// afterwards. Before the fix the switch only set the language mode of the same runspace, and every function of the trusted period
/// went on running FullLanguage in a window that said ConstrainedLanguage.
/// </summary>
public sealed class TrustedModeOffTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What the trusted mode leaves in its runspace, each printing the language mode it runs in.</summary>
    private const string Leftovers =
        "function global:Get-Leftover { 'function ' + $ExecutionContext.SessionState.LanguageMode }; " +
        "Set-Alias -Name leftover -Value Get-Leftover -Scope Global; " +
        "$global:captured = { 'script block ' + $ExecutionContext.SessionState.LanguageMode }; " +
        "$global:holder = [pscustomobject]@{ Run = { 'member ' + $ExecutionContext.SessionState.LanguageMode } }; " +
        "$Host | Add-Member -MemberType ScriptMethod -Name Leftover -Value { 'host ' + $ExecutionContext.SessionState.LanguageMode } -Force; " +
        "$Host.UI.RawUI | Add-Member -MemberType ScriptMethod -Name Leftover -Value { 'raw ' + $ExecutionContext.SessionState.LanguageMode } -Force; " +
        "'planted'";

    /// <summary>Each way a script of the constrained window could call what the trusted mode left.</summary>
    public static TheoryData<string> WaysToCallALeftover =>
    [
        "Get-Leftover",
        "leftover",
        "& $captured",
        "$captured.Invoke()",
        "& $holder.Run",
        "$Host.Leftover()",
        "$Host.UI.RawUI.Leftover()",
        "& (Get-Command Get-Leftover)",
    ];

    [Theory]
    [MemberData(nameof(WaysToCallALeftover))]
    public async Task Nothing_the_trusted_mode_defined_runs_in_full_language_after_it_was_turned_off(string call)
    {
        await using var session = await TestSessions.CreateAsync();
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, Ct);
        Assert.Equal("planted\n", TestSessions.Text(await TestSessions.RunAsync(session, Leftovers)));
        Assert.Contains("FullLanguage", TestSessions.Text(await TestSessions.RunAsync(session, call)), StringComparison.Ordinal);

        await session.SetLanguageModeAsync(PowerShellLanguageMode.Constrained, Ct);
        var items = await TestSessions.RunAsync(session, call);

        Assert.Equal(PowerShellLanguageMode.Constrained, session.LanguageMode);
        Assert.DoesNotContain("FullLanguage", TestSessions.Dump(items), StringComparison.Ordinal);
        Assert.True(TestSessions.End(items).HadErrors, $"'{call}' ran after the trusted mode was turned off: {TestSessions.Dump(items)}");
    }

    [Fact]
    public async Task After_the_trusted_mode_the_session_is_a_new_constrained_runspace_with_the_windows_commands_and_width()
    {
        await using var session = await TestSessions.CreateAsync();
        session.OutputWidth = 77;
        var hostBefore = TestSessions.Text(await TestSessions.RunAsync(session, "$Host.InstanceId"));
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, Ct);
        await TestSessions.RunAsync(session, "$global:kept = 'trusted'");

        await session.SetLanguageModeAsync(PowerShellLanguageMode.Constrained, Ct);

        Assert.Equal(PowerShellLanguageMode.Constrained, session.LanguageMode);
        Assert.Equal(77, session.OutputWidth);
        var after = await TestSessions.RunAsync(session, "$null -eq $kept; $ExecutionContext.SessionState.LanguageMode; (Get-AdmVersion).LanguageMode; $Host.UI.RawUI.BufferSize.Width");
        Assert.Equal("True\nConstrainedLanguage\nConstrained\n77\n", TestSessions.Text(after));
        Assert.NotEqual(hostBefore, TestSessions.Text(await TestSessions.RunAsync(session, "$Host.InstanceId")));
        Assert.Equal("1.2.3-test\n", TestSessions.Text(await TestSessions.RunAsync(session, "(Get-AdmVersion).ApplicationVersion")));
    }

    /// <summary>
    /// Turning the trusted mode on keeps the runspace and the variables of the constrained mode (only the switch off makes a new one). The
    /// functions defined in ConstrainedLanguage the engine itself drops when the language mode becomes FullLanguage.
    /// </summary>
    [Fact]
    public async Task Turning_the_trusted_mode_on_keeps_the_runspace_and_the_variables_of_the_constrained_mode()
    {
        await using var session = await TestSessions.CreateAsync();
        await TestSessions.RunAsync(session, "$work = 'kept'; function Get-Work { $work }");
        var hostBefore = TestSessions.Text(await TestSessions.RunAsync(session, "$Host.InstanceId"));

        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, Ct);

        Assert.Equal("kept\n", TestSessions.Text(await TestSessions.RunAsync(session, "$work")));
        Assert.Equal(hostBefore, TestSessions.Text(await TestSessions.RunAsync(session, "$Host.InstanceId")));
        Assert.True(TestSessions.End(await TestSessions.RunAsync(session, "Get-Work")).HadErrors);
    }

    [Fact]
    public async Task Turning_the_trusted_mode_off_asks_the_lockdown_again()
    {
        var probe = new FakeLockdownProbe();
        await using var session = await TestSessions.CreateAsync(probe: probe);
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, Ct);
        probe.State = PowerShellLockdownState.Enforce;

        await session.SetLanguageModeAsync(PowerShellLanguageMode.Constrained, Ct);

        Assert.Equal(PowerShellTrustedModeBlock.SystemLockdown, session.TrustedModeBlock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetLanguageModeAsync(PowerShellLanguageMode.Full, Ct));
    }

    [Fact]
    public async Task The_trusted_mode_cannot_be_turned_off_while_a_command_runs_and_stays_on()
    {
        await using var session = await TestSessions.CreateAsync();
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, Ct);
        await using var run = session.RunAsync("Read-Host 'x'", Ct).GetAsyncEnumerator(Ct);
        Assert.True(await run.MoveNextAsync());
        var prompt = Assert.IsType<PowerShellPromptItem>(run.Current);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetLanguageModeAsync(PowerShellLanguageMode.Constrained, Ct));

        prompt.Respond("done");
        while (await run.MoveNextAsync())
        {
        }

        Assert.Equal(PowerShellLanguageMode.Full, session.LanguageMode);
        Assert.Equal("FullLanguage\n", TestSessions.Text(await TestSessions.RunAsync(session, "$ExecutionContext.SessionState.LanguageMode")));
    }

    [Fact]
    public async Task The_trusted_runspace_is_closed_after_the_switch()
    {
        var key = "admng-test-trusted-runspace-" + Guid.NewGuid().ToString("N");
        await using var session = await TestSessions.CreateAsync();
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, Ct);
        await TestSessions.RunAsync(session, $"[System.AppDomain]::CurrentDomain.SetData('{key}', [runspace]::DefaultRunspace)");
        var trusted = Assert.IsAssignableFrom<System.Management.Automation.Runspaces.Runspace>(AppDomain.CurrentDomain.GetData(key));
        AppDomain.CurrentDomain.SetData(key, null);

        await session.SetLanguageModeAsync(PowerShellLanguageMode.Constrained, Ct);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (trusted.RunspaceStateInfo.State != System.Management.Automation.Runspaces.RunspaceState.Closed && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        Assert.Equal(System.Management.Automation.Runspaces.RunspaceState.Closed, trusted.RunspaceStateInfo.State);
    }

    /// <summary>
    /// The new runspace cannot be made: the session is constrained and has no runspace - the trusted one is not kept - and every later
    /// command says why instead of running somewhere.
    /// </summary>
    [Fact]
    public async Task When_the_new_runspace_cannot_be_made_the_session_runs_nothing_and_says_why()
    {
        var cause = new InvalidOperationException("The engine's files are missing.");
        await using var session = await PowerShellSession.CreateAsync(
            new PowerShellSessionOptions
            {
                Context = new FakeWindowContext(),
                LockdownProbe = new FakeLockdownProbe(),
                RunspaceCreating = number =>
                {
                    if (number > 1)
                    {
                        throw cause;
                    }
                },
            },
            Ct);
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, Ct);

        var error = await Assert.ThrowsAsync<PowerShellSessionUnavailableException>(() => session.SetLanguageModeAsync(PowerShellLanguageMode.Constrained, Ct));

        Assert.Same(cause, error.InnerException);
        Assert.Equal(PowerShellLanguageMode.Constrained, session.LanguageMode);
        Assert.False(session.IsRunning);
        var run = await Assert.ThrowsAsync<PowerShellSessionUnavailableException>(() => TestSessions.RunAsync(session, "'still trusted?'"));
        Assert.Same(cause, run.InnerException);
        Assert.False(session.IsRunning);
        Assert.Same(PowerShellCompletionResult.Empty, await session.CompleteAsync("Get-Da", 6, Ct));
        await Assert.ThrowsAsync<PowerShellSessionUnavailableException>(() => session.SetLanguageModeAsync(PowerShellLanguageMode.Full, Ct));
    }

    /// <summary>The window closes while the new runspace is being made: the session ends, and nothing of it runs a command afterwards.</summary>
    [Fact]
    public async Task A_session_disposed_while_the_new_runspace_is_made_ends_without_it()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var session = await PowerShellSession.CreateAsync(
            new PowerShellSessionOptions
            {
                Context = new FakeWindowContext(),
                LockdownProbe = new FakeLockdownProbe(),
                RunspaceCreating = number =>
                {
                    if (number > 1)
                    {
                        entered.Set();
                        release.Wait(TimeSpan.FromSeconds(30));
                    }
                },
            },
            Ct);
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, Ct);

        var switching = session.SetLanguageModeAsync(PowerShellLanguageMode.Constrained, Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), Ct));
        Assert.False(switching.IsCompleted);
        await session.DisposeAsync();
        release.Set();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => switching);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => TestSessions.RunAsync(session, "'after'"));
        Assert.Equal(PowerShellLanguageMode.Constrained, session.LanguageMode);
    }

    [Fact]
    public async Task Turning_constrained_while_constrained_keeps_the_runspace()
    {
        await using var session = await TestSessions.CreateAsync();
        await TestSessions.RunAsync(session, "$work = 'kept'");

        await session.SetLanguageModeAsync(PowerShellLanguageMode.Constrained, Ct);

        Assert.Equal("kept\n", TestSessions.Text(await TestSessions.RunAsync(session, "$work")));
    }
}
