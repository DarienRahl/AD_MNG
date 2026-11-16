using System.Globalization;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Presentation.Watchdog;
using AdMng.Testing;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// What the configuration of the service (the Rules, Channels, Digest and Sources tabs) says in the status line (stage V2b-4,
/// unit 4): its version as the summary, the read, the validation and the save, the states that last - no configuration, the
/// conflict of a save - and the tabs that have it in front. Nothing of a value of the configuration, and never a secret, is in
/// a text or a log copy (CLAUDE.md rule 2).
/// </summary>
public sealed class WatchdogConfigStatusTests
{
    private const string Secret = "Hasło-Źródłowe!2026";

    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task TheSummary_IsTheVersion_AndFollowsTheLanguage()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var session = await LoadAsync(h);

            Assert.Equal(PageStatusKind.Idle, session.Status.Current.Kind);
            Assert.Equal(session.VersionText, session.Status.Current.Message);
            Assert.StartsWith("Version 7 · ", session.Status.Current.Message, StringComparison.Ordinal);

            h.Ad.Localizer.SetCulture(Polish);
            session.Relocalize();
            Assert.StartsWith("Wersja 7 · ", session.Status.Current.Message, StringComparison.Ordinal);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task TheRead_SaysWorkingWithStop_AndStoppedBeforeAnythingIsLoaded_IsTheStateOfTheTabs()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var session = new WatchdogConfigSession(h.Services) { IsAdmin = true };
            var recorder = new StatusRecorder(session.Status);
            h.Client.Hold(nameof(IWatchdogClient.GetConfigAsync));

            var reading = session.LoadAsync();
            Assert.Equal(PageStatusKind.Working, session.Status.Current.Kind);
            Assert.Equal("Reading the configuration of the service…", session.Status.Current.Message);
            Assert.Equal("Stop", session.Status.Current.Action!.Label);
            Assert.True(session.IsBusy);

            // "Stopping…" at once; the read ends as the state of the tabs: nothing is loaded, they are empty.
            session.Status.CancelCommand.Execute(null);
            Assert.Contains((PageStatusKind.Working, "Stopping…"), recorder.Plain);
            await reading;

            Assert.False(session.IsBusy);
            Assert.False(session.IsLoaded);
            var line = session.Status.Current;
            Assert.Equal(PageStatusKind.Attention, line.Kind);
            Assert.False(line.CanClose);
            Assert.Equal(
                "Reading the configuration was stopped: the tabs show the configuration read before, if there was one. Press “Reload” to read it again.",
                line.Message);
            Assert.Equal("Reload", line.Action!.Label);
            Assert.Same(session.ReloadCommand, line.Action.Command);

            // Reload reads it, and the state gives way to the version.
            h.Client.Release(nameof(IWatchdogClient.GetConfigAsync));
            await session.ReloadCommand.ExecuteAsync(null);
            Assert.True(session.IsLoaded);
            Assert.Equal(PageStatusKind.Idle, session.Status.Current.Kind);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task AFailedRead_IsTheStateOfTheTabs_WhileNothingIsLoaded_AndAResultThatCanBeClosedAfterwards()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var session = new WatchdogConfigSession(h.Services) { IsAdmin = true };
            h.Client.FailNext(nameof(IWatchdogClient.GetConfigAsync), new WatchdogClientException(WatchdogClientErrorKind.Forbidden, "no"));

            await session.LoadAsync();

            var line = session.Status.Current;
            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.False(line.CanClose);
            Assert.StartsWith("Could not read the configuration: ", line.Message, StringComparison.Ordinal);
            Assert.EndsWith(" Press Retry to try again. (Forbidden)", line.Message, StringComparison.Ordinal);
            Assert.Same(session.ReloadCommand, line.Action!.Command);
            Assert.Equal(h.Connection.Describe(new WatchdogClientException(WatchdogClientErrorKind.Forbidden, "x")), session.Error);

            await session.ReloadCommand.ExecuteAsync(null);
            Assert.Equal(PageStatusKind.Idle, session.Status.Current.Kind);

            // With a configuration on screen a failed read leaves it as it was: a result that can be closed.
            h.Client.FailNext(nameof(IWatchdogClient.GetConfigAsync), new WatchdogClientException(WatchdogClientErrorKind.ServerError, "boom"));
            await session.ReloadCommand.ExecuteAsync(null);
            line = session.Status.Current;
            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.True(line.CanClose);
            Assert.True(session.IsLoaded);
            line.Action!.Command.Execute(null);
            session.Status.CloseCommand.Execute(null);
            Assert.Equal(PageStatusKind.Idle, session.Status.Current.Kind);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Validate_IsStoppable_AndSaysValidOrWhyNot()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var session = await LoadAsync(h);

            // Valid: done.
            Assert.True(await session.ValidateAsync());
            Assert.Equal(PageStatusKind.Done, session.Status.Current.Kind);
            Assert.Equal("The configuration is valid.", session.Status.Current.Message);

            // Not valid: an attention, the findings are listed by the tab.
            session.Update(c => c with { Smtp = c.Smtp with { Host = "zły host" } });
            h.Client.Validator = c => c.Smtp.Host == "zły host"
                ? [new ConfigIssue { Path = "smtp.host", Code = "invalidHostName", Severity = IssueSeverity.Error, Message = "Invalid host." }]
                : [];
            Assert.False(await session.ValidateAsync());
            Assert.Equal(PageStatusKind.Attention, session.Status.Current.Kind);
            Assert.Equal("The configuration is not valid – correct the findings in the list.", session.Status.Current.Message);
            Assert.Single(session.Issues);

            // Stopped: control is back at once, the findings are as they were.
            h.Client.Hold(nameof(IWatchdogClient.ValidateConfigAsync));
            var validating = session.ValidateAsync();
            Assert.Equal("Validating the configuration…", session.Status.Current.Message);
            session.Status.CancelCommand.Execute(null);
            Assert.Equal("Stopping…", session.Status.Current.Message);
            Assert.False(await validating);
            Assert.Equal(PageStatusKind.Attention, session.Status.Current.Kind);
            Assert.Equal("Validating the configuration was stopped. Press “Validate” to validate again.", session.Status.Current.Message);
            Assert.Same(session.ValidateCommand, session.Status.Current.Action!.Command);
            Assert.False(session.IsBusy);
            h.Client.Release(nameof(IWatchdogClient.ValidateConfigAsync));

            // A failed call: an error that says it and offers Retry.
            h.Client.FailNext(nameof(IWatchdogClient.ValidateConfigAsync), new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "down"));
            Assert.False(await session.ValidateAsync());
            Assert.Equal(PageStatusKind.Error, session.Status.Current.Kind);
            Assert.StartsWith("Could not validate the configuration: ", session.Status.Current.Message, StringComparison.Ordinal);
            Assert.EndsWith("(Unreachable)", session.Status.Current.Message, StringComparison.Ordinal);
            Assert.Equal("Retry", session.Status.Current.Action!.Label);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Save_IsAWriteWithoutStop_AndSaysTheVersionItSaved_NeverTheSecretItSent()
    {
        var log = new ListLogger<PageStatusHolder>();
        using var h = await WatchdogHarness.CreateAsync(statusLog: log);
        await h.Ui.RunAsync(async () =>
        {
            var session = await LoadAsync(h);
            var recorder = new StatusRecorder(session.Status);
            session.Update(c => c with { Smtp = c.Smtp with { Password = new SecretField { IsSet = true, NewValue = Secret } } });
            h.Client.Hold(nameof(IWatchdogClient.SaveConfigAsync));

            var saving = session.SaveAsync();
            Assert.Equal(PageStatusKind.Working, session.Status.Current.Kind);
            Assert.Equal("Saving the configuration…", session.Status.Current.Message);
            Assert.Null(session.Status.Current.Action);
            session.Status.CancelCommand.Execute(null);
            Assert.Equal("Saving the configuration…", session.Status.Current.Message);

            h.Client.Release(nameof(IWatchdogClient.SaveConfigAsync));
            Assert.True(await saving);

            var line = session.Status.Current;
            Assert.Equal(PageStatusKind.Done, line.Kind);
            Assert.Equal("Configuration saved (version 8).", line.Message);
            Assert.True(line.CanClose);

            // Closed: the version again.
            session.Status.CloseCommand.Execute(null);
            Assert.StartsWith("Version 8 · ", session.Status.Current.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(recorder.Plain, said => said.Message.Contains(Secret, StringComparison.Ordinal));
        });
        h.AssertNoUiErrors();
        Assert.DoesNotContain(Secret, log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Hasło", log.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASaveTheServiceRefuses_SaysNothingWasSaved_AndTheFindingsStayInTheList()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var session = await LoadAsync(h);
            session.Update(c => c with { Smtp = c.Smtp with { Host = "zły host" } });
            h.Client.Validator = c => c.Smtp.Host == "zły host"
                ? [new ConfigIssue { Path = "smtp.host", Code = "invalidHostName", Severity = IssueSeverity.Error, Message = "Invalid host." }]
                : [];

            Assert.False(await session.SaveAsync());

            var line = session.Status.Current;
            Assert.Equal(PageStatusKind.Attention, line.Kind);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.ConfigInvalid], line.Message);
            Assert.Single(session.Issues);
            Assert.True(session.IsDirty);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task TheConflictOfASave_IsTheStateOfTheTabs_UntilTheConfigurationIsReadAgain_AndFollowsTheLanguage()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var session = await LoadAsync(h);
            var recorder = new StatusRecorder(session.Status);
            h.Client.StoredConfig = h.Client.StoredConfig with { Version = 9 };
            session.Update(c => c with { Digest = c.Digest with { SendWhenEmpty = !c.Digest.SendWhenEmpty } });

            Assert.False(await session.SaveAsync());

            // The save ends without a word of its own: the state says that someone else saved first, with Reload.
            Assert.True(session.HasConflict);
            var line = session.Status.Current;
            Assert.Equal(PageStatusKind.Attention, line.Kind);
            Assert.False(line.CanClose);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.ErrorVersionConflict], line.Message);
            Assert.Equal("Reload", line.Action!.Label);
            Assert.Same(session.ReloadCommand, line.Action.Command);
            Assert.Equal(
                [PageStatusKind.Working, PageStatusKind.Attention],
                recorder.Plain.Select(s => s.Kind).Distinct());
            Assert.True(session.IsDirty);

            h.Ad.Localizer.SetCulture(Polish);
            session.Relocalize();
            Assert.Equal(h.Ad.Localizer[L.Watchdog.ErrorVersionConflict], session.Status.Current.Message);
            Assert.Equal("Wczytaj ponownie", session.Status.Current.Action!.Label);

            // Reloading says the version again - and so does a further edit: the conflict is no longer news of the working copy.
            await session.ReloadCommand.ExecuteAsync(null);
            Assert.False(session.HasConflict);
            Assert.StartsWith("Wersja 9 · ", session.Status.Current.Message, StringComparison.Ordinal);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task AnotherFailure_OfASave_IsAnErrorWithoutAnAction_BecauseASaveIsNotRepeatedFromALink()
    {
        var log = new ListLogger<PageStatusHolder>();
        using var h = await WatchdogHarness.CreateAsync(statusLog: log);
        await h.Ui.RunAsync(async () =>
        {
            var session = await LoadAsync(h);
            session.Update(c => c with { Digest = c.Digest with { SendWhenEmpty = !c.Digest.SendWhenEmpty } });
            h.Client.FailNext(nameof(IWatchdogClient.SaveConfigAsync), new WatchdogClientException(WatchdogClientErrorKind.Timeout, "slow"));

            Assert.False(await session.SaveAsync());

            var line = session.Status.Current;
            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.StartsWith("The configuration was not saved: ", line.Message, StringComparison.Ordinal);
            Assert.EndsWith("(Timeout)", line.Message, StringComparison.Ordinal);
            Assert.Null(line.Action);
            Assert.True(session.IsDirty);
        });
        h.AssertNoUiErrors();
        Assert.Contains(log.Entries, e => e.Message.Contains(L.WatchdogConfigStatus.SaveFailed, StringComparison.Ordinal) && e.Message.Contains("[Timeout]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryTabOfTheConfiguration_HasItsStatusInFront_AndTheLineOfTheTabSaysIt()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: true);
            tab.Rules.SelectedRule!.IsEnabled = !tab.Rules.SelectedRule.IsEnabled;
            h.Client.Hold(nameof(IWatchdogClient.SaveConfigAsync));
            var saving = tab.Config.SaveAsync();

            // The save is the configuration's operation: it is said on every tab that edits it, not only where it was pressed.
            foreach (var page in new[] { WatchdogSubTab.Rules, WatchdogSubTab.Channels, WatchdogSubTab.Digest, WatchdogSubTab.Sources })
            {
                tab.SelectedSubTab = page;
                Assert.Equal(PageStatusKind.Working, tab.Status.Current.Kind);
                Assert.Equal("Saving the configuration…", tab.Status.Current.Message);
            }

            // The feed has nothing to do with it.
            tab.SelectedSubTab = WatchdogSubTab.Events;
            Assert.Equal(PageStatusKind.Idle, tab.Status.Current.Kind);

            h.Client.Release(nameof(IWatchdogClient.SaveConfigAsync));
            await saving;
            tab.SelectedSubTab = WatchdogSubTab.Digest;
            Assert.Equal(PageStatusKind.Done, tab.Status.Current.Kind);
            Assert.Equal("Configuration saved (version 8).", tab.Status.Current.Message);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task TheSaveOfTheConfiguration_IsAWriteThatCannotBeStopped_AndTheTabSaysSoWhicheverSubTabIsShown()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: true);
            tab.Rules.SelectedRule!.IsEnabled = !tab.Rules.SelectedRule.IsEnabled;
            h.Client.Hold(nameof(IWatchdogClient.SaveConfigAsync));
            var saving = tab.Config.SaveAsync();

            // The feed is shown, the save is on another page: the question of closing the tab still finds it (RunningWork).
            tab.SelectedSubTab = WatchdogSubTab.Events;
            Assert.True(RunningWork.HasRunningWrites(tab));
            Assert.Equal(OperationWrites.Other, tab.Config.Status.Operation!.Writes);
            Assert.False(tab.Config.Status.Operation.CanCancel);

            // It cannot be stopped: the tab stays, and the operator is told it is still saving.
            var closed = await RunningWork.ConfirmCloseAsync(h.Ad.Localizer, h.Ad.Dialogs, tab.Title, RunningWork.HoldersOf(tab));
            Assert.False(closed);
            Assert.Equal($"info:{h.Ad.Localizer.Format(L.CloseWork.Wait, "Saving the configuration…")}", Assert.Single(h.Ad.Dialogs.Shown));
            Assert.StartsWith("info:Saving the configuration…\n\nThis operation cannot be stopped", h.Ad.Dialogs.Shown[0], StringComparison.Ordinal);

            h.Client.Release(nameof(IWatchdogClient.SaveConfigAsync));
            await saving;
            Assert.False(RunningWork.HasRunningWrites(tab));
            Assert.True(await RunningWork.ConfirmCloseAsync(h.Ad.Localizer, h.Ad.Dialogs, tab.Title, RunningWork.HoldersOf(tab)));
        });
        h.AssertNoUiErrors();
    }

    private static async Task<WatchdogConfigSession> LoadAsync(WatchdogHarness h)
    {
        var session = new WatchdogConfigSession(h.Services) { IsAdmin = true };
        await session.LoadAsync();
        return session;
    }
}
