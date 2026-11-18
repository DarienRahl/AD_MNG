using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Tests;

/// <summary>D3: ConstrainedLanguage by default, the trusted-mode switch, the lockdown.</summary>
public sealed class LanguageModeTests
{
    private const string DotNetCall = "[System.IO.File]::Exists('/x')";

    [Fact]
    public async Task The_window_starts_in_constrained_language_and_a_dotnet_method_call_fails_with_the_language_mode_error()
    {
        await using var session = await TestSessions.CreateAsync();

        var mode = await TestSessions.RunAsync(session, "$ExecutionContext.SessionState.LanguageMode");
        var call = await TestSessions.RunAsync(session, DotNetCall);

        Assert.Equal(PowerShellLanguageMode.Constrained, session.LanguageMode);
        Assert.Equal("ConstrainedLanguage\n", TestSessions.Text(mode));
        Assert.Contains("language mode", TestSessions.Text(call, PowerShellStream.Error), StringComparison.OrdinalIgnoreCase);
        Assert.True(TestSessions.End(call).HadErrors);
    }

    [Fact]
    public async Task The_programs_commands_work_in_constrained_language()
    {
        await using var session = await TestSessions.CreateAsync();

        var items = await TestSessions.RunAsync(session, "(Get-AdmVersion).LanguageMode");

        Assert.Equal("Constrained\n", TestSessions.Text(items));
    }

    [Fact]
    public async Task The_trusted_mode_is_on_after_the_switch_and_back_to_constrained_on_request()
    {
        await using var session = await TestSessions.CreateAsync();

        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken);
        var full = await TestSessions.RunAsync(session, DotNetCall + "; (Get-AdmVersion).LanguageMode");
        await session.SetLanguageModeAsync(PowerShellLanguageMode.Constrained, TestContext.Current.CancellationToken);
        var constrained = await TestSessions.RunAsync(session, DotNetCall);

        Assert.Equal("False\nFull\n", TestSessions.Text(full));
        Assert.Equal(PowerShellLanguageMode.Constrained, session.LanguageMode);
        Assert.True(TestSessions.End(constrained).HadErrors);
    }

    [Fact]
    public async Task A_system_lockdown_at_the_start_disables_the_switch_with_the_reason_and_keeps_constrained_language()
    {
        var probe = new FakeLockdownProbe { State = PowerShellLockdownState.Enforce };
        await using var session = await TestSessions.CreateAsync(probe: probe);

        Assert.Equal(PowerShellTrustedModeBlock.SystemLockdown, session.TrustedModeBlock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken));
        Assert.Equal(PowerShellLanguageMode.Constrained, session.LanguageMode);
        var items = await TestSessions.RunAsync(session, "(Get-AdmVersion).LanguageMode");
        Assert.Equal("Constrained\n", TestSessions.Text(items));
    }

    [Fact]
    public async Task A_lockdown_that_appears_later_is_noticed_when_the_switch_is_asked_for()
    {
        var probe = new FakeLockdownProbe();
        await using var session = await TestSessions.CreateAsync(probe: probe);
        Assert.Equal(PowerShellTrustedModeBlock.None, session.TrustedModeBlock);

        probe.State = PowerShellLockdownState.Enforce;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken));

        Assert.Equal(PowerShellTrustedModeBlock.SystemLockdown, session.TrustedModeBlock);
        Assert.Equal(PowerShellLanguageMode.Constrained, session.LanguageMode);
    }

    [Fact]
    public async Task An_audit_only_policy_does_not_block_the_trusted_mode()
    {
        var probe = new FakeLockdownProbe { State = PowerShellLockdownState.Audit };
        await using var session = await TestSessions.CreateAsync(probe: probe);

        await session.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken);

        Assert.Equal(PowerShellTrustedModeBlock.None, session.TrustedModeBlock);
        Assert.Equal(PowerShellLanguageMode.Full, session.LanguageMode);
    }

    [Fact]
    public async Task The_mode_cannot_be_changed_while_a_command_runs()
    {
        await using var session = await TestSessions.CreateAsync();
        await using var run = session.RunAsync("Read-Host 'x'", TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await run.MoveNextAsync());
        var prompt = Assert.IsType<PowerShellPromptItem>(run.Current);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken));

        prompt.Respond("done");
        while (await run.MoveNextAsync())
        {
        }

        Assert.Equal(PowerShellLanguageMode.Constrained, session.LanguageMode);
    }

    [Fact]
    public async Task A_new_session_starts_in_constrained_language_even_after_another_one_was_trusted()
    {
        await using (var first = await TestSessions.CreateAsync())
        {
            await first.SetLanguageModeAsync(PowerShellLanguageMode.Full, TestContext.Current.CancellationToken);
        }

        await using var second = await TestSessions.CreateAsync();

        Assert.Equal(PowerShellLanguageMode.Constrained, second.LanguageMode);
        var items = await TestSessions.RunAsync(second, "$ExecutionContext.SessionState.LanguageMode");
        Assert.Equal("ConstrainedLanguage\n", TestSessions.Text(items));
    }

    [Fact]
    public void The_real_probe_reports_no_lockdown_where_no_policy_exists()
    {
        var state = SystemPolicyLockdownProbe.Instance.Probe();

        Assert.True(Enum.IsDefined(state));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(PowerShellLockdownState.None, state);
        }
    }
}
