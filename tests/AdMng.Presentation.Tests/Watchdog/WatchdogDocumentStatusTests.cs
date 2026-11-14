using System.Globalization;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Tests.GroupPolicy;
using AdMng.Presentation.Watchdog;
using AdMng.Testing;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// What the Watchdog tab says in its one status line (track V, stage V2b-4, unit 4): the summary of the page that is shown,
/// the connection attempt, what lasts while the connection does not work, interrupted live updates, and the pages that wait
/// in their holders until they are shown. Every log copy is a key, counts and a result code - never the host, an account, an
/// event or a value of the configuration.
/// </summary>
public sealed class WatchdogDocumentStatusTests
{
    private const string Host = "admng-wd.contoso.com:8443";

    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task AtRest_TheLineSaysTheSummaryOfThePageThatIsShown_InBothLanguages()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: true);

            // The feed: how many events, how many are unacknowledged, as of when - and the selection joins it.
            Assert.Equal(PageStatusKind.Idle, tab.Status.Current.Kind);
            Assert.StartsWith("Events: 30 · unacknowledged: 28 · as of ", tab.Status.Current.Message, StringComparison.Ordinal);
            tab.Events.SelectedItems = tab.Events.Items.Take(3).ToList();
            Assert.EndsWith(" · 3 selected", tab.Status.Current.Message, StringComparison.Ordinal);
            tab.Events.SelectedItems = null;

            // The pages of the configuration: its version, who saved it and when.
            foreach (var page in new[] { WatchdogSubTab.Rules, WatchdogSubTab.Channels, WatchdogSubTab.Digest, WatchdogSubTab.Sources })
            {
                tab.SelectedSubTab = page;
                Assert.Equal(PageStatusKind.Idle, tab.Status.Current.Kind);
                Assert.StartsWith("Version ", tab.Status.Current.Message, StringComparison.Ordinal);
                Assert.Equal(tab.Config.VersionText, tab.Status.Current.Message);
            }

            // The status of the service: when it was received.
            tab.SelectedSubTab = WatchdogSubTab.Status;
            await tab.ServiceStatus.LoadAsync();
            Assert.StartsWith("Status read ", tab.Status.Current.Message, StringComparison.Ordinal);

            // A language switch says all of it again in the new language, at once.
            tab.SelectedSubTab = WatchdogSubTab.Events;
            h.Ad.Localizer.SetCulture(Polish);
            await h.Ui.IdleAsync();
            Assert.StartsWith("Zdarzeń: 30 · niepotwierdzonych: 28 · stan na ", tab.Status.Current.Message, StringComparison.Ordinal);
            tab.SelectedSubTab = WatchdogSubTab.Rules;
            Assert.StartsWith("Wersja ", tab.Status.Current.Message, StringComparison.Ordinal);
            tab.SelectedSubTab = WatchdogSubTab.Status;
            Assert.StartsWith("Stan odczytano ", tab.Status.Current.Message, StringComparison.Ordinal);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task TheConnectionAttempt_IsTheOperationOfTheTab_WithoutStop_AndTheLineGoesStraightToThePage()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            tab.IsVisible = true;
            var recorder = new StatusRecorder(tab.Status);
            h.Client.Hold(nameof(IWatchdogClient.GetMeAsync));
            h.Client.Hold(nameof(IWatchdogClient.GetEventsAsync));

            var connecting = tab.InitializeAsync();
            Assert.Equal(PageStatusKind.Working, tab.Status.Current.Kind);
            Assert.Equal($"Connecting to {Host}…", tab.Status.Current.Message);
            Assert.Null(tab.Status.Current.Action);
            Assert.True(tab.Status.IsWorking);

            // The connection is made: the feed is read, and that is what the line says now (with Stop).
            h.Client.Release(nameof(IWatchdogClient.GetMeAsync));
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 1);
            Assert.Equal(PageStatusKind.Working, tab.Status.Current.Kind);
            Assert.Equal("Reading the events…", tab.Status.Current.Message);
            Assert.Equal("Stop", tab.Status.Current.Action!.Label);
            Assert.False(tab.Status.IsWorking);

            h.Client.Release(nameof(IWatchdogClient.GetEventsAsync));
            await connecting;

            // "Connecting…", the read of the feed, its summary - the summary of the tab itself (empty) is never between them.
            Assert.Equal(
                [
                    (PageStatusKind.Working, $"Connecting to {Host}…"),
                    (PageStatusKind.Working, "Reading the events…"),
                ],
                recorder.Plain.Take(2).ToArray());
            Assert.DoesNotContain(recorder.Plain, said => said.Kind == PageStatusKind.Idle && said.Message.Length == 0);
            Assert.DoesNotContain(recorder.Plain, said => said.Kind is PageStatusKind.Error or PageStatusKind.Attention);
            Assert.Equal(PageStatusKind.Idle, tab.Status.Current.Kind);
            Assert.StartsWith("Events: 30", tab.Status.Current.Message, StringComparison.Ordinal);
        });
        h.AssertNoUiErrors();
    }

    [Theory]
    [InlineData(WatchdogClientErrorKind.Unreachable, "Unreachable")]
    [InlineData(WatchdogClientErrorKind.Timeout, "Timeout")]
    [InlineData(WatchdogClientErrorKind.Unauthorized, "Unauthorized")]
    [InlineData(WatchdogClientErrorKind.Forbidden, "Forbidden")]
    [InlineData(WatchdogClientErrorKind.ApiVersionMismatch, "ApiVersionMismatch")]
    public async Task AConnectionProblem_IsALastingError_WithRetry_ThatCannotBeClosed(WatchdogClientErrorKind kind, string code)
    {
        var log = new ListLogger<PageStatusHolder>();
        using var h = await WatchdogHarness.CreateAsync(statusLog: log);
        h.Client.FailWith(kind);
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            tab.IsVisible = true;
            await tab.InitializeAsync();

            var line = tab.Status.Current;
            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.False(line.CanClose);
            Assert.StartsWith($"Could not connect to {Host}: ", line.Message, StringComparison.Ordinal);
            Assert.Contains(tab.ConnectionMessage, line.Message, StringComparison.Ordinal);
            Assert.EndsWith($" Press Retry to try again. ({code})", line.Message, StringComparison.Ordinal);
            Assert.Equal("Retry", line.Action!.Label);
            Assert.Same(tab.RefreshNowCommand, line.Action.Command);

            // Nothing a page says matters while the connection does not work, and the sub-tabs are not shown.
            Assert.False(tab.IsConnected);
            tab.SelectedSubTab = WatchdogSubTab.Rules;
            Assert.Equal(PageStatusKind.Error, tab.Status.Current.Kind);

            // Retry connects again, and the error is gone.
            h.Client.ClearFailures();
            await tab.RefreshNowCommand.ExecuteAsync(null);
            await tab.Connecting;
            await tab.Config.Loading;
            Assert.True(tab.IsConnected);
            Assert.NotEqual(PageStatusKind.Error, tab.Status.Current.Kind);
        });
        h.AssertNoUiErrors();

        // The log copy: the key and the code - not the host, which the text on screen names.
        var written = log.Entries.Single(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        Assert.Contains(L.WatchdogDocStatus.ConnectFailed, written.Message, StringComparison.Ordinal);
        Assert.Contains($"[{code}]", written.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("admng-wd", log.AllText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUntrustedCertificate_IsALastingError_WhoseOneActionIsToCheckTheCertificate()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.ServerCertificate = SampleStatus.Certificate;
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            tab.IsVisible = true;
            await tab.InitializeAsync();

            // The operator declined the question once; the state stays and says how to be asked again.
            var line = tab.Status.Current;
            Assert.Equal(PageStatusKind.Error, line.Kind);
            Assert.False(line.CanClose);
            Assert.Equal(tab.ConnectionMessage, line.Message.Split(": ", 2)[1].Split(" Press ")[0]);
            Assert.Contains("Press “Check certificate…” to see the certificate the service presented.", line.Message, StringComparison.Ordinal);
            Assert.Equal("Check certificate…", line.Action!.Label);
            Assert.Same(tab.ReviewCertificateCommand, line.Action.Command);
            Assert.EndsWith("(CertificateUntrusted)", line.Message, StringComparison.Ordinal);

            h.Ad.Dialogs.CertificateTrustHandler = _ => true;
            await tab.ReviewCertificateCommand.ExecuteAsync(null);
            await tab.Events.Loading;

            Assert.True(tab.IsConnected);
            Assert.NotEqual(PageStatusKind.Error, tab.Status.Current.Kind);
            Assert.Null(tab.Status.Current.Action);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task NoAddress_IsALastingAttention_WithoutAnAction_TheAddressEditorIsTheWayOut()
    {
        using var h = await WatchdogHarness.CreateAsync(configured: false);
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            await tab.InitializeAsync();

            var line = tab.Status.Current;
            Assert.Equal(PageStatusKind.Attention, line.Kind);
            Assert.False(line.CanClose);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.ConnectionNotConfigured], line.Message);
            Assert.Null(line.Action);
            Assert.True(tab.ShowAddressEditor);

            // Connecting to the address that is typed takes the state away.
            tab.ServiceUrlInput = WatchdogHarness.ServiceUrl;
            await tab.ConnectCommand.ExecuteAsync(null);
            await tab.Events.Loading;
            Assert.NotEqual(PageStatusKind.Attention, tab.Status.Current.Kind);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task InterruptedLiveUpdates_AreALastingAttention_UntilTheyRecover_AndFollowTheLanguage()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), 2);

            h.Client.FailWith(WatchdogClientErrorKind.Unreachable, nameof(IWatchdogClient.GetStatusAsync));
            var calls = h.Client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync));
            h.Time.Advance(WatchdogPoller.HiddenStatusInterval);
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), calls + 1);
            await WatchdogTestWait.UntilAsync(h, () => tab.LiveError is not null);

            // The tab stays usable: an attention that cannot be closed, no action - the tab retries by itself.
            var line = tab.Status.Current;
            Assert.Equal(PageStatusKind.Attention, line.Kind);
            Assert.False(line.CanClose);
            Assert.Equal(tab.LiveError, line.Message);
            Assert.Contains("Retrying…", line.Message, StringComparison.Ordinal);
            Assert.Null(line.Action);
            Assert.True(tab.IsConnected);

            h.Ad.Localizer.SetCulture(Polish);
            await h.Ui.IdleAsync();
            Assert.StartsWith("Aktualizacje na żywo", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.Equal(PageStatusKind.Attention, tab.Status.Current.Kind);

            h.Client.ClearFailures();
            calls = h.Client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync));

            // (A language switch reloads the feed and wakes the poller, which fails once more: its back-off has grown.)
            h.Time.Advance(WatchdogPoller.MaxBackoff);
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), calls + 1);
            await WatchdogTestWait.UntilAsync(h, () => tab.LiveError is null);
            Assert.Equal(PageStatusKind.Idle, tab.Status.Current.Kind);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task TheConnectionProblem_IsSaidInTheLanguageOfTheMoment()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.FailWith(WatchdogClientErrorKind.Forbidden);
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            await tab.InitializeAsync();
            Assert.StartsWith($"Could not connect to {Host}: ", tab.Status.Current.Message, StringComparison.Ordinal);

            h.Ad.Localizer.SetCulture(Polish);
            await h.Ui.IdleAsync();
            Assert.StartsWith($"Nie udało się połączyć z {Host}: ", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.Contains("Wybierz Ponów, aby spróbować ponownie.", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.Equal("Ponów", tab.Status.Current.Action!.Label);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task WhatAPageSays_WaitsInItsHolder_UntilThePageIsShown()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: true);

            // The feed fails while the Rules tab is shown: the line says what the Rules tab says, not the feed's error.
            tab.SelectedSubTab = WatchdogSubTab.Rules;
            h.Client.FailNext(nameof(IWatchdogClient.GetEventsAsync), new WatchdogClientException(WatchdogClientErrorKind.ServerError, "boom"));
            await tab.Events.LoadAsync();
            Assert.NotNull(tab.Events.Error);
            Assert.Equal(PageStatusKind.Idle, tab.Status.Current.Kind);
            Assert.StartsWith("Version ", tab.Status.Current.Message, StringComparison.Ordinal);

            // It is said when the feed is shown again - the one reason the empty feed has.
            tab.SelectedSubTab = WatchdogSubTab.Events;
            Assert.Equal(PageStatusKind.Error, tab.Status.Current.Kind);
            Assert.StartsWith("Could not read the events: ", tab.Status.Current.Message, StringComparison.Ordinal);
            Assert.Equal("Retry", tab.Status.Current.Action!.Label);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ClosingTheTab_EndsWhatStillRuns_WithoutAWord()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: true);
            h.Client.Hold(nameof(IWatchdogClient.GetConfigAsync));
            var reading = tab.Config.LoadAsync();
            Assert.True(tab.Config.Status.IsWorking);

            tab.Dispose();
            h.Client.Release(nameof(IWatchdogClient.GetConfigAsync));
            await reading;

            Assert.False(tab.Config.Status.IsWorking);
            Assert.False(tab.Config.IsBusy);
        });
        h.AssertNoUiErrors();
    }
}
