using System.Globalization;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Presentation.Watchdog;
using AdMng.Testing;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using Microsoft.Extensions.Logging;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// What the Digest, Rules and Channels tabs say in the status line (stage V2b-4, unit 4): the preview of the digest and "send
/// now", the preview of a rule and the reset of a built-in one, and the delivery tests with the stages in their rows. The
/// holder of each tab is in front of the configuration's own, so a save can run while a preview is prepared.
/// </summary>
public sealed class WatchdogPagesStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task TheDigestPreview_IsStoppable_ItsResultIsTheContent_AndAFailureCanBeRepeated()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.SelectedSubTab = WatchdogSubTab.Digest;
            var digest = tab.Digest;
            var recorder = new StatusRecorder(digest.Status);

            // A preview that is read: the line says it with Stop, and ends with the summary (the version) - the content is the result.
            await digest.PreviewCommand.ExecuteAsync(null);
            Assert.NotEmpty(digest.PreviewText);
            Assert.Equal(PageStatusKind.Working, recorder.Plain[0].Kind);
            Assert.Equal("Preparing the digest preview…", recorder.Plain[0].Message);
            Assert.Equal(PageStatusKind.Idle, tab.Status.Current.Kind);
            Assert.StartsWith("Version ", tab.Status.Current.Message, StringComparison.Ordinal);
            var kept = digest.PreviewText;

            // Stop: control is back at once, the preview is as it was and the attention offers the button.
            h.Client.Hold(nameof(IWatchdogClient.PreviewDigestAsync));
            var running = digest.PreviewCommand.ExecuteAsync(null);
            Assert.Equal("Stop", tab.Status.Current.Action!.Label);
            tab.Status.Current.Action.Command.Execute(null);
            Assert.Equal("Stopping…", tab.Status.Current.Message);
            await running;
            h.Client.Release(nameof(IWatchdogClient.PreviewDigestAsync));
            Assert.False(digest.IsBusy);
            Assert.Equal(kept, digest.PreviewText);
            Assert.Equal(PageStatusKind.Attention, tab.Status.Current.Kind);
            Assert.Equal("Preparing the preview was stopped: the preview was not changed. Press “Preview” to try again.", tab.Status.Current.Message);
            Assert.Same(digest.PreviewCommand, tab.Status.Current.Action!.Command);

            // A failure: an error with Retry; the old preview stays.
            h.Client.FailNext(nameof(IWatchdogClient.PreviewDigestAsync), new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "down"));
            await digest.PreviewCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Error, tab.Status.Current.Kind);
            Assert.StartsWith("Could not prepare the digest preview: ", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.EndsWith("(Unreachable)", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.Equal("Retry", tab.Status.Current.Action!.Label);
            Assert.Equal(kept, digest.PreviewText);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task SendNow_IsAWriteWithoutStop_AndSaysItWasQueued_InPolishToo()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.SelectedSubTab = WatchdogSubTab.Digest;
            var digest = tab.Digest;
            h.Client.Hold(nameof(IWatchdogClient.SendDigestNowAsync));
            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);

            var sending = digest.SendNowCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Working, tab.Status.Current.Kind);
            Assert.Equal("Kolejkowanie raportu dziennego do wysłania…", tab.Status.Current.Message);
            Assert.Null(tab.Status.Current.Action);

            h.Client.Release(nameof(IWatchdogClient.SendDigestNowAsync));
            await sending;
            Assert.Equal(PageStatusKind.Done, tab.Status.Current.Kind);
            Assert.Equal("Raport dzienny został przekazany do wysłania.", tab.Status.Current.Message);

            // A failure: an error, no link (a send is not repeated from one).
            h.Client.FailNext(nameof(IWatchdogClient.SendDigestNowAsync), new WatchdogClientException(WatchdogClientErrorKind.Forbidden, "no"));
            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await digest.SendNowCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Error, tab.Status.Current.Kind);
            Assert.StartsWith("Nie udało się zakolejkować raportu dziennego: ", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.Null(tab.Status.Current.Action);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task APreviewIsPrepared_WhileTheConfigurationIsSaved_EachSaysItsOwn()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.SelectedSubTab = WatchdogSubTab.Digest;
            tab.Config.Update(c => c with { Digest = c.Digest with { SendWhenEmpty = !c.Digest.SendWhenEmpty } });
            h.Client.Hold(nameof(IWatchdogClient.PreviewDigestAsync));
            var previewing = tab.Digest.PreviewCommand.ExecuteAsync(null);
            Assert.Equal("Preparing the digest preview…", tab.Status.Current.Message);

            // The save has its own holder: it does not end the preview, and the line says what runs last.
            var saved = await tab.Config.SaveAsync();
            Assert.True(saved);
            Assert.True(tab.Digest.IsBusy);
            Assert.True(tab.Digest.Status.IsWorking);
            Assert.Equal(PageStatusKind.Working, tab.Status.Current.Kind);
            Assert.Equal("Preparing the digest preview…", tab.Status.Current.Message);

            h.Client.Release(nameof(IWatchdogClient.PreviewDigestAsync));
            await previewing;
            Assert.Equal(PageStatusKind.Done, tab.Status.Current.Kind);
            Assert.Equal("Configuration saved (version 8).", tab.Status.Current.Message);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task TheRulePreview_IsAnOperationOfTheTab_StoppableWithRetry_AndEndsWhenTheRuleIsNoLongerShown()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.SelectedSubTab = WatchdogSubTab.Rules;
            var rules = tab.Rules;
            var editor = rules.Editor!;

            await editor.PreviewCommand.ExecuteAsync(null);
            Assert.NotEmpty(editor.PreviewText);
            Assert.Equal(PageStatusKind.Idle, tab.Status.Current.Kind);

            // Stop: control is back, the attention offers the button again.
            h.Client.Hold(nameof(IWatchdogClient.PreviewRuleAsync));
            var running = editor.PreviewCommand.ExecuteAsync(null);
            Assert.Equal("Checking what the rule would have matched…", tab.Status.Current.Message);
            rules.Status.CancelCommand.Execute(null);
            Assert.Equal("Stopping…", tab.Status.Current.Message);
            await running;
            h.Client.Release(nameof(IWatchdogClient.PreviewRuleAsync));
            Assert.False(editor.IsPreviewing);
            Assert.Equal(PageStatusKind.Attention, tab.Status.Current.Kind);
            Assert.StartsWith("Checking the rule was stopped: the preview was not changed. Press “", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.Same(editor.PreviewCommand, tab.Status.Current.Action!.Command);

            // A failure: an error with Retry (the old text of the failure is still the property the tests read).
            h.Client.FailNext(nameof(IWatchdogClient.PreviewRuleAsync), new WatchdogClientException(WatchdogClientErrorKind.Timeout, "slow"));
            await editor.PreviewCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Error, tab.Status.Current.Kind);
            Assert.StartsWith("Could not check the rule: ", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.EndsWith("(Timeout)", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.NotNull(editor.PreviewError);

            // Another rule selected while a preview runs: it ends without a word.
            h.Client.Hold(nameof(IWatchdogClient.PreviewRuleAsync));
            running = editor.PreviewCommand.ExecuteAsync(null);
            rules.SelectedRule = rules.Rules[1];
            await running;
            h.Client.Release(nameof(IWatchdogClient.PreviewRuleAsync));
            Assert.False(rules.Status.IsWorking);
            Assert.DoesNotContain("Stopping", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.NotEqual(PageStatusKind.Attention, tab.Status.Current.Kind);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ResettingARule_SaysWhyNothingWasReset_InsteadOfADialog_AndAFailureCanBeRepeated()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.SelectedSubTab = WatchdogSubTab.Rules;
            var rules = tab.Rules;
            rules.SelectedRule = rules.Rules.First(r => r.IsBuiltIn);

            h.Client.Defaults = h.Client.Defaults with { Rules = [] };
            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await rules.ResetRuleCommand.ExecuteAsync(null);
            Assert.Empty(h.Ad.Dialogs.Errors);
            Assert.Equal(PageStatusKind.Attention, tab.Status.Current.Kind);
            Assert.StartsWith("The rule was not reset: ", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.Contains(h.Ad.Localizer[L.Watchdog.RulesNoDefault], tab.Status.Current.Message, StringComparison.Ordinal);

            h.Client.FailNext(nameof(IWatchdogClient.GetDefaultConfigAsync), new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "down"));
            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await rules.ResetRuleCommand.ExecuteAsync(null);
            Assert.Empty(h.Ad.Dialogs.Errors);
            Assert.Equal(PageStatusKind.Error, tab.Status.Current.Kind);
            Assert.StartsWith("The rule was not reset: ", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.EndsWith("(Unreachable)", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.Same(rules.ResetRuleCommand, tab.Status.Current.Action!.Command);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ADeliveryTest_SaysWorkingWithStop_AndHowItEnded_TheStagesStayInTheirRows()
    {
        var log = new ListLogger<PageStatusHolder>();
        using var h = await WatchdogHarness.CreateAsync(statusLog: log);
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.SelectedSubTab = WatchdogSubTab.Channels;
            var channels = tab.Channels;
            channels.Smtp!.From = "Strażnik AD <straznik@contoso.com>";
            channels.TestRecipients = "jan.kowalski@contoso.com";
            var recorder = new StatusRecorder(channels.Status);

            // The test works with Stop, and succeeds: done, with the time it took.
            h.Client.Hold(nameof(IWatchdogClient.SendTestEmailAsync));
            var testing = channels.SendTestEmailCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Working, tab.Status.Current.Kind);
            Assert.Equal("Sending the test…", tab.Status.Current.Message);
            Assert.Equal("Stop", tab.Status.Current.Action!.Label);
            Assert.True(channels.EmailTest.IsRunning);
            h.Client.Release(nameof(IWatchdogClient.SendTestEmailAsync));
            await testing;
            Assert.Equal(PageStatusKind.Done, tab.Status.Current.Kind);
            Assert.Equal("The test succeeded (840 ms).", tab.Status.Current.Message);
            Assert.Equal(7, channels.EmailTest.Stages.Count);

            // A test that ran and failed: an error that points to the stages; the rows keep each stage's result.
            h.Client.EmailTestHandler = _ => new DeliveryTestResult
            {
                Success = false,
                Duration = TimeSpan.FromMilliseconds(1250),
                Stages =
                [
                    new DeliveryStageResult { Stage = DeliveryStage.Connect, Success = true, Detail = "poczta.contoso.com:587", Duration = TimeSpan.FromMilliseconds(40) },
                    new DeliveryStageResult { Stage = DeliveryStage.Authenticate, Success = false, Detail = "denied", StatusCode = 535, Duration = TimeSpan.FromMilliseconds(1210) },
                ],
            };
            await channels.SendTestEmailCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Error, tab.Status.Current.Kind);
            Assert.StartsWith("The test failed (", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.EndsWith(" The stages of the test show where it failed.", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.Contains(channels.EmailTest.Stages, s => !s.Success);
            Assert.Contains(channels.EmailTest.Stages, s => s.Success);
            Assert.Null(tab.Status.Current.Action);

            // Stopped: it is not known whether anything arrived.
            h.Client.EmailTestHandler = null;
            h.Client.Hold(nameof(IWatchdogClient.SendTestEmailAsync));
            testing = channels.SendTestEmailCommand.ExecuteAsync(null);
            channels.EmailTest.CancelCommand.Execute(null);
            Assert.Equal("Stopping…", tab.Status.Current.Message);
            await testing;
            h.Client.Release(nameof(IWatchdogClient.SendTestEmailAsync));
            Assert.Equal(PageStatusKind.Attention, tab.Status.Current.Kind);
            Assert.Equal("The test was stopped: it is not known whether anything reached the destination.", tab.Status.Current.Message);
            Assert.False(channels.EmailTest.IsRunning);

            // Never sent: why, in one sentence, with the findings of the service.
            h.Client.FailNext(
                nameof(IWatchdogClient.SendTestEmailAsync),
                new WatchdogClientException(WatchdogClientErrorKind.Validation, "invalid")
                {
                    Issues = [new ConfigIssue { Path = "smtp.host", Code = "invalidHostName", Severity = IssueSeverity.Error, Message = "Invalid host." }],
                });
            await channels.SendTestEmailCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Attention, tab.Status.Current.Kind);
            Assert.StartsWith("Nothing was sent: ", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Environment.NewLine, tab.Status.Current.Message, StringComparison.Ordinal);

            // The connection failed: an error, no link to send again.
            h.Client.FailNext(nameof(IWatchdogClient.SendTestEmailAsync), new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "down"));
            await channels.SendTestEmailCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Error, tab.Status.Current.Kind);
            Assert.StartsWith("Nothing was sent: ", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.EndsWith("(Unreachable)", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.Null(tab.Status.Current.Action);
            Assert.Contains((PageStatusKind.Working, "Stopping…"), recorder.Plain);
        });
        h.AssertNoUiErrors();

        // Nobody's address and no host in what was written.
        Assert.DoesNotContain("kowalski", log.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("straznik", log.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admng-wd", log.AllText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains(L.Watchdog.TestSucceeded, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATestStartedWhileAnotherRuns_ReplacesIt_AndTheOldOnesButtonIsNoLongerBusy()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.SelectedSubTab = WatchdogSubTab.Channels;
            var channels = tab.Channels;
            channels.Smtp!.From = "Strażnik AD <straznik@contoso.com>";
            channels.TestRecipients = "jan.kowalski@contoso.com";
            var teams = channels.Channels[0];

            h.Client.Hold(nameof(IWatchdogClient.SendTestEmailAsync));
            var email = channels.SendTestEmailCommand.ExecuteAsync(null);
            Assert.True(channels.EmailTest.IsRunning);

            // One holder, one test at a time: the channel's test takes the line and the e-mail test ends without a word.
            var channelTest = teams.TestCommand.ExecuteAsync(null);
            await email;
            await channelTest;
            h.Client.Release(nameof(IWatchdogClient.SendTestEmailAsync));

            Assert.False(channels.EmailTest.IsRunning);
            Assert.False(teams.Test.IsRunning);
            Assert.Equal(PageStatusKind.Done, tab.Status.Current.Kind);
            Assert.DoesNotContain("stopped", tab.Status.Current.Message, StringComparison.Ordinal);
        });
        h.AssertNoUiErrors();
    }
}
