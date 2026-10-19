using System.Globalization;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>The Watchdog tab: connection states, the https-only address, roles, sub-tabs, badge and rollout banner.</summary>
public sealed class WatchdogDocumentTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task NotConfigured_ShowsTheAddressEditor_WithoutCallingTheService()
    {
        using var h = await WatchdogHarness.CreateAsync(configured: false);
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            tab.IsVisible = true;
            await tab.InitializeAsync();

            Assert.Equal(WatchdogConnectionState.NotConfigured, tab.ConnectionState);
            Assert.True(tab.IsNotConfigured);
            Assert.True(tab.ShowAddressEditor);
            Assert.False(tab.IsConnected);
            Assert.False(tab.HasConnectionProblem);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.ConnectionNotConfigured], tab.ConnectionMessage);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.ConnectionNotConfigured], tab.StatusText);
            Assert.Equal(string.Empty, tab.HostText);
            Assert.False(tab.ConnectCommand.CanExecute(null));
            Assert.DoesNotContain(h.Client.Calls.All, c => c != nameof(IWatchdogClient.Configure));
            Assert.False(h.Launcher.IsAvailable);
        });
        h.AssertNoUiErrors();
    }

    [Theory]
    [InlineData("http://admng-wd.contoso.com:8443")]
    [InlineData("admng-wd.contoso.com")]
    [InlineData("https://jan:haslo@admng-wd.contoso.com")]
    [InlineData("https://admng-wd.contoso.com/?x=1")]
    [InlineData("ftp://admng-wd.contoso.com")]
    public async Task Connect_RefusesAddressesThatAreNotPlainHttps(string address)
    {
        using var h = await WatchdogHarness.CreateAsync(configured: false);
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            await tab.InitializeAsync();
            tab.ServiceUrlInput = address;
            await tab.ConnectCommand.ExecuteAsync(null);

            Assert.Equal(h.Ad.Localizer[L.Watchdog.AddressHttpsOnly], tab.ServiceUrlError);
            Assert.Null(h.Ad.Settings.Current.Watchdog.ServiceUrl);
            Assert.Equal(WatchdogConnectionState.NotConfigured, tab.ConnectionState);
            Assert.Equal(0, h.Client.Calls.Count(nameof(IWatchdogClient.GetMeAsync)));
        });
    }

    [Fact]
    public async Task Connect_StoresTheHttpsAddressOnly_AndConnects()
    {
        using var h = await WatchdogHarness.CreateAsync(configured: false);
        var availability = 0;
        h.Launcher.AvailabilityChanged += (_, _) => availability++;
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            tab.IsVisible = true;
            await tab.InitializeAsync();
            tab.ServiceUrlInput = "  https://ADMNG-WD.contoso.com:8443/  ";
            Assert.True(tab.ConnectCommand.CanExecute(null));
            await tab.ConnectCommand.ExecuteAsync(null);
            await tab.Events.Loading;

            Assert.Null(tab.ServiceUrlError);
            Assert.Equal("https://admng-wd.contoso.com:8443", h.Ad.Settings.Current.Watchdog.ServiceUrl);
            Assert.Equal(WatchdogConnectionState.Connected, tab.ConnectionState);
            Assert.False(tab.ShowAddressEditor);
            Assert.Equal("admng-wd.contoso.com:8443", tab.HostText);
            Assert.Equal(h.Ad.Localizer.Format(L.Watchdog.ConnectionConnected, "admng-wd.contoso.com:8443"), tab.ConnectionMessage);
            Assert.Equal(FakeWatchdogClient.SampleAddress, h.Client.BaseAddress);
            Assert.True(h.Launcher.IsAvailable);
            Assert.Equal(1, availability);

            // The settings hold the address and nothing else of the watchdog.
            var settings = h.Ad.Settings.Current.Watchdog;
            Assert.Null(settings.PinnedCertificateSha256);
            Assert.Empty(settings.FilterPresets);
        });
        h.AssertNoUiErrors();
    }

    [Theory]
    [InlineData(WatchdogClientErrorKind.Unreachable, WatchdogConnectionState.Unreachable)]
    [InlineData(WatchdogClientErrorKind.Timeout, WatchdogConnectionState.Unreachable)]
    [InlineData(WatchdogClientErrorKind.CertificateInvalid, WatchdogConnectionState.CertificateInvalid)]
    [InlineData(WatchdogClientErrorKind.Unauthorized, WatchdogConnectionState.Unauthorized)]
    [InlineData(WatchdogClientErrorKind.Forbidden, WatchdogConnectionState.Forbidden)]
    [InlineData(WatchdogClientErrorKind.ApiVersionMismatch, WatchdogConnectionState.VersionMismatch)]
    [InlineData(WatchdogClientErrorKind.ServerError, WatchdogConnectionState.Error)]
    [InlineData(WatchdogClientErrorKind.NotFound, WatchdogConnectionState.Error)]
    [InlineData(WatchdogClientErrorKind.Validation, WatchdogConnectionState.Error)]
    [InlineData(WatchdogClientErrorKind.VersionConflict, WatchdogConnectionState.Error)]
    [InlineData(WatchdogClientErrorKind.Cancelled, WatchdogConnectionState.Error)]
    [InlineData(WatchdogClientErrorKind.NotConfigured, WatchdogConnectionState.NotConfigured)]
    public async Task ConnectionState_ForEveryClientErrorKind(WatchdogClientErrorKind kind, WatchdogConnectionState expected)
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        h.Client.FailWith(kind, nameof(IWatchdogClient.GetMeAsync));
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            await tab.InitializeAsync();

            Assert.Equal(expected, tab.ConnectionState);
            Assert.Equal(h.Ad.Localizer[WatchdogDisplay.StateKey(expected)], tab.StateText);
            Assert.Equal("admng-wd.contoso.com:8443", tab.HostText);
            Assert.False(tab.IsConnected);
            Assert.Equal(expected is not WatchdogConnectionState.NotConfigured, tab.HasConnectionProblem);
            Assert.False(string.IsNullOrWhiteSpace(tab.ConnectionMessage));
            Assert.Null(tab.Me);
            Assert.False(tab.Config.IsLoaded);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ConnectionMessages_ExplainKerberosAndRoleProblems_InPolish()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            h.Client.FailWith(WatchdogClientErrorKind.Unauthorized, nameof(IWatchdogClient.GetMeAsync));
            await tab.InitializeAsync();
            Assert.Equal("Usługa odrzuciła uwierzytelnienie Kerberos – sprawdź SPN i rekord DNS (docs/WATCHDOG.md).", tab.ConnectionMessage);
            Assert.Equal("Uwierzytelnienie nieudane", tab.StateText);

            h.Client.FailWith(WatchdogClientErrorKind.Forbidden, nameof(IWatchdogClient.GetMeAsync));
            await tab.RefreshAsync();
            Assert.Equal("Brak uprawnień – poproś o dodanie do grupy AD z rolą Watchdog.", tab.ConnectionMessage);

            h.Client.FailWith(WatchdogClientErrorKind.Unreachable, nameof(IWatchdogClient.GetMeAsync));
            await tab.RefreshAsync();
            Assert.Contains("admng-wd.contoso.com:8443", tab.ConnectionMessage, StringComparison.Ordinal);

            h.Client.FailWith(WatchdogClientErrorKind.ServerError, nameof(IWatchdogClient.GetMeAsync));
            await tab.RefreshAsync();
            Assert.Equal("Usługa Watchdog zgłosiła błąd (HTTP 500).", tab.ConnectionMessage);
        });
    }

    [Fact]
    public async Task Banner_ShowsStateAndHostOnce_AndASecondLineOnlyToExplain()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var changed = new List<string?>();
            tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            // Connected: "Połączono admng-wd.contoso.com:8443" – the message repeats it, so no second line.
            Assert.Equal("Połączono", tab.StateText);
            Assert.Equal("admng-wd.contoso.com:8443", tab.HostText);
            Assert.Equal("Połączono z admng-wd.contoso.com:8443", tab.ConnectionMessage);
            Assert.Equal(string.Empty, tab.BannerMessage);

            // A problem: the second line explains it (with the remedy); Retry clears it again.
            var failed = h.CreateDocument();
            failed.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            h.Client.FailWith(WatchdogClientErrorKind.Forbidden, nameof(IWatchdogClient.GetMeAsync));
            await failed.InitializeAsync();
            Assert.Equal("Brak uprawnień", failed.StateText);
            Assert.Equal("Brak uprawnień – poproś o dodanie do grupy AD z rolą Watchdog.", failed.BannerMessage);
            Assert.Contains(nameof(WatchdogDocumentViewModel.BannerMessage), changed);

            h.Client.ClearFailures();
            await failed.RefreshAsync();
            Assert.True(failed.IsConnected);
            Assert.Equal(string.Empty, failed.BannerMessage);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Banner_NotConfigured_ExplainsWhatToDo()
    {
        using var h = await WatchdogHarness.CreateAsync(configured: false);
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            await tab.InitializeAsync();

            Assert.Equal(h.Ad.Localizer[L.Watchdog.ConnectionNotConfigured], tab.BannerMessage);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Retry_AfterAFailure_Connects()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.FailNext(nameof(IWatchdogClient.GetMeAsync), new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "down"));
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            await tab.InitializeAsync();
            Assert.Equal(WatchdogConnectionState.Unreachable, tab.ConnectionState);

            await tab.RefreshNowCommand.ExecuteAsync(null);
            await tab.Config.Loading;

            Assert.Equal(WatchdogConnectionState.Connected, tab.ConnectionState);
            Assert.False(tab.HasConnectionProblem);
            Assert.True(tab.Config.IsLoaded);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Admin_CanEdit_ViewerGetsTheRedactedConfiguration_ReadOnly()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var admin = await h.OpenAsync(visible: false);
            Assert.True(admin.IsAdmin);
            Assert.Equal("Administrator", admin.RoleText);
            Assert.Equal(@"CONTOSO\jan.kowalski · Administrator · detail: full", admin.AccountText);
            Assert.True(admin.Config.CanEdit);
            Assert.False(admin.Config.IsReadOnly);
            Assert.False(admin.Config.IsRedacted);
            Assert.Null(admin.Config.ReadOnlyReason);
            Assert.True(admin.Status.IsAdmin);

            h.Client.IsAdmin = false;
            var viewer = await h.OpenAsync(visible: false);
            Assert.False(viewer.IsAdmin);
            Assert.Equal("Viewer", viewer.RoleText);
            Assert.Equal(@"CONTOSO\anna.nowak · Viewer · detail: standard", viewer.AccountText);
            Assert.True(viewer.Config.IsRedacted);
            Assert.False(viewer.Config.CanEdit);
            Assert.True(viewer.Config.IsReadOnly);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.RequiresAdmin], viewer.Config.ReadOnlyReason);
            Assert.False(viewer.Config.SaveCommand.CanExecute(null));
            Assert.False(viewer.Rules.NewRuleCommand.CanExecute(null));
            Assert.False(viewer.Status.IsAdmin);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ChangeAddress_ShowsTheEditorWithTheCurrentAddress_CancelKeepsTheConnection()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.ChangeAddressCommand.Execute(null);
            Assert.True(tab.IsEditingAddress);
            Assert.True(tab.ShowAddressEditor);
            Assert.Equal(WatchdogHarness.ServiceUrl, tab.ServiceUrlInput);

            tab.ServiceUrlInput = "http://inny-host";
            await tab.ConnectCommand.ExecuteAsync(null);
            Assert.NotNull(tab.ServiceUrlError);
            Assert.Equal(WatchdogHarness.ServiceUrl, h.Ad.Settings.Current.Watchdog.ServiceUrl);

            tab.CancelChangeAddressCommand.Execute(null);
            Assert.False(tab.ShowAddressEditor);
            Assert.Null(tab.ServiceUrlError);
            Assert.True(tab.IsConnected);
        });
    }

    [Fact]
    public async Task SelectedSubTab_IsRememberedInTheSettings_AndStatusLoadsWhenShown()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            Assert.Equal(WatchdogSubTab.Events, tab.SelectedSubTab);
            var statusCalls = h.Client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync));

            tab.SelectedSubTabIndex = (int)WatchdogSubTab.Status;
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetOutboxAsync), 1);
            await tab.Status.DeliveryLog.Loading;

            Assert.Equal(WatchdogSubTab.Status, tab.SelectedSubTab);
            Assert.Equal("Status", h.Ad.Settings.Current.Watchdog.LastSubTab);
            Assert.True(h.Client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync)) > statusCalls);
            Assert.NotEmpty(tab.Status.DeliveryLog.Items);

            tab.SelectedSubTabIndex = 42;
            Assert.Equal(WatchdogSubTab.Events, tab.SelectedSubTab);

            tab.SelectedSubTab = WatchdogSubTab.Rules;
            var next = h.CreateDocument();
            Assert.Equal(WatchdogSubTab.Rules, next.SelectedSubTab);
        });
    }

    [Fact]
    public async Task Badge_ShowsUnacknowledgedHighAndCritical_AndRolloutBanner()
    {
        using var h = await WatchdogHarness.CreateAsync();
        var until = new DateTimeOffset(2026, 10, 21, 7, 0, 0, TimeSpan.Zero);
        h.Client.Status = SampleStatus.Create(until);
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);

            Assert.Equal(5, tab.UnacknowledgedHighOrAbove);
            Assert.True(tab.HasBadge);
            Assert.Equal("5", tab.BadgeText);
            Assert.True(tab.IsRolloutActive);
            Assert.True(tab.ShowRolloutBanner);
            Assert.Equal(until, tab.RolloutUntil);
            Assert.Contains(until.ToString("g", h.Ad.Localizer.Culture), tab.RolloutBannerText, StringComparison.Ordinal);
            Assert.True(tab.Rules.EndRolloutCommand.CanExecute(null));

            h.Client.Status = h.Client.Status with
            {
                Last24Hours = h.Client.Status.Last24Hours with { UnacknowledgedHighOrAbove = 120 },
                RolloutModeUntil = null,
            };
            await tab.Status.LoadAsync();
            Assert.Equal("99+", tab.BadgeText);
            Assert.False(tab.ShowRolloutBanner);
            Assert.Equal(string.Empty, tab.RolloutBannerText);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ShellDocument_TitleIconStatusAndRefresh()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: true);

            Assert.Equal("Watchdog", tab.Title);
            Assert.Equal(ObjectIcon.Watchdog, tab.Icon);
            Assert.False(tab.IsLoading);
            Assert.StartsWith("Events: 30 · unacknowledged: ", tab.StatusText, StringComparison.Ordinal);
            Assert.Equal(string.Empty, tab.SelectionText);

            // F5 on the Events sub-tab reloads the feed.
            var loads = h.Client.EventQueries.Count(q => q.After is null);
            await tab.RefreshAsync();
            Assert.Equal(loads + 1, h.Client.EventQueries.Count(q => q.After is null));

            // F5 on the Rules sub-tab reloads the configuration unless it has unsaved changes.
            tab.SelectedSubTab = WatchdogSubTab.Rules;
            var configLoads = h.Client.Calls.Count(nameof(IWatchdogClient.GetConfigAsync));
            await tab.RefreshAsync();
            Assert.Equal(configLoads + 1, h.Client.Calls.Count(nameof(IWatchdogClient.GetConfigAsync)));
            tab.Rules.SelectedRule!.IsEnabled = !tab.Rules.SelectedRule.IsEnabled;
            Assert.True(tab.Config.IsDirty);
            await tab.RefreshAsync();
            Assert.Equal(configLoads + 1, h.Client.Calls.Count(nameof(IWatchdogClient.GetConfigAsync)));
            Assert.True(tab.Config.IsDirty);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task PollFailures_AreShownInline_AndAuthenticationFailuresChangeTheState()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);

            // The connection read the status once, the poller's first round reads it again.
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), 2);

            // The next status refresh fails: inline error, the tab stays connected.
            h.Client.FailWith(WatchdogClientErrorKind.Unreachable, nameof(IWatchdogClient.GetStatusAsync));
            var calls = h.Client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync));
            h.Time.Advance(WatchdogPoller.HiddenStatusInterval);
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), calls + 1);
            await WatchdogTestWait.UntilAsync(h, () => tab.LiveError is not null);
            Assert.True(tab.IsConnected);
            Assert.Contains("admng-wd.contoso.com:8443", tab.LiveError, StringComparison.Ordinal);

            // It recovers after the back-off.
            h.Client.ClearFailures();
            calls = h.Client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync));
            h.Time.Advance(WatchdogPoller.MinBackoff);
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), calls + 1);
            await WatchdogTestWait.UntilAsync(h, () => tab.LiveError is null);
            Assert.True(tab.IsConnected);

            // A revoked role turns the banner into "no access".
            h.Client.FailWith(WatchdogClientErrorKind.Forbidden, nameof(IWatchdogClient.GetStatusAsync));
            calls = h.Client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync));
            h.Time.Advance(WatchdogPoller.HiddenStatusInterval);
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), calls + 1);
            await WatchdogTestWait.UntilAsync(h, () => tab.ConnectionState == WatchdogConnectionState.Forbidden);
            Assert.True(tab.HasConnectionProblem);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LanguageSwitch_RelocalizesTheTab()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: true);
            Assert.Equal("Connected", tab.StateText);
            var row = tab.Events.Items.Single(r => r.Id == SampleEvents.LockoutId);
            Assert.Equal("Account locked out", row.KindText);

            h.Ad.Localizer.SetCulture(Polish);
            await h.Ui.IdleAsync();

            Assert.Equal("Połączono", tab.StateText);
            Assert.Equal("Połączono z admng-wd.contoso.com:8443", tab.ConnectionMessage);
            Assert.Equal("Administrator", tab.RoleText);
            Assert.Equal("Zablokowano konto", tab.Events.Items.Single(r => r.Id == SampleEvents.LockoutId).KindText);
            Assert.StartsWith("Zdarzeń: 30", tab.StatusText, StringComparison.Ordinal);
            Assert.Equal("poniedziałek", tab.Digest.WeeklySectionsDay.Text);
            Assert.Equal("Blokady kont", tab.Rules.Rules[12].Name);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.OutboxAllStatuses], tab.Status.DeliveryLog.Status.Text);
            Assert.False(tab.Config.IsDirty);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Dispose_StopsPolling()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: true);
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 2);
            tab.Dispose();
            tab.Dispose();
            var calls = h.Client.Calls.All.Count;
            h.Time.Advance(TimeSpan.FromMinutes(10));
            await h.Ui.IdleAsync();
            Assert.Equal(calls, h.Client.Calls.All.Count);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task SupersededConnect_NeverShowsAnErrorState()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = h.CreateDocument();
            var states = new List<WatchdogConnectionState>();
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WatchdogDocumentViewModel.ConnectionState))
                {
                    states.Add(tab.ConnectionState);
                }
            };
            h.Client.Hold(nameof(IWatchdogClient.GetMeAsync));

            var first = tab.InitializeAsync();
            var second = tab.InitializeAsync();
            await first;
            h.Client.Release(nameof(IWatchdogClient.GetMeAsync));
            await second;
            await tab.Config.Loading;

            Assert.Equal(WatchdogConnectionState.Connected, tab.ConnectionState);
            Assert.DoesNotContain(WatchdogConnectionState.Error, states);
            Assert.False(tab.HasConnectionProblem);
        });
        h.AssertNoUiErrors();
    }

    [Theory]
    [InlineData(WatchdogClientErrorKind.Timeout)]
    [InlineData(WatchdogClientErrorKind.Unreachable)]
    public async Task OneFailedRequest_WhileLiveUpdatesWork_StaysInline_AndKeepsTheTab(WatchdogClientErrorKind kind)
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.Config.Update(c => c with
            {
                Digest = c.Digest with { SendWhenEmpty = !c.Digest.SendWhenEmpty },
                Smtp = c.Smtp with { Password = new() { NewValue = "Hasło-Źródłowe!2026" } },
            });

            // A save that times out (or hits a network blip) while the poller still reaches the service.
            h.Client.FailNext(nameof(IWatchdogClient.SaveConfigAsync), new WatchdogClientException(kind, "slow"));
            Assert.False(await tab.Config.SaveAsync());
            Assert.NotNull(tab.Config.Error);
            Assert.Equal(WatchdogConnectionState.Connected, tab.ConnectionState);
            Assert.True(tab.IsConnected);

            // A slow text search of the feed: the error bar of the feed, not a hidden tab.
            h.Client.FailNext(nameof(IWatchdogClient.GetEventsAsync), new WatchdogClientException(kind, "slow"));
            await tab.Events.LoadAsync();
            Assert.NotNull(tab.Events.Error);
            Assert.True(tab.IsConnected);

            // The unsaved edits and the typed password are still there for the next save.
            Assert.True(tab.Config.IsDirty);
            Assert.Equal("Hasło-Źródłowe!2026", tab.Config.Working!.Smtp.Password.NewValue);
            Assert.True(await tab.Config.SaveAsync());
            Assert.Equal("Hasło-Źródłowe!2026", h.Client.SavedEnvelopes[^1].Configuration.Smtp.Password.NewValue);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Reconnect_KeepsUnsavedConfiguration_OfTheSameService()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.Config.Update(c => c with
            {
                Digest = c.Digest with { SendWhenEmpty = !c.Digest.SendWhenEmpty },
                Smtp = c.Smtp with { Password = new() { NewValue = "Zażółć-Gęślą-2026" } },
            });
            var loads = h.Client.Calls.Count(nameof(IWatchdogClient.GetConfigAsync));

            // Retry / F5 after a connection problem reconnects: the operator's work survives.
            await tab.InitializeAsync();
            Assert.True(tab.IsConnected);
            Assert.Equal(loads, h.Client.Calls.Count(nameof(IWatchdogClient.GetConfigAsync)));
            Assert.True(tab.Config.IsDirty);
            Assert.Equal("Zażółć-Gęślą-2026", tab.Config.Working!.Smtp.Password.NewValue);

            // Another service: its own configuration is loaded (edits never go to a different service).
            tab.ChangeAddressCommand.Execute(null);
            tab.ServiceUrlInput = "https://admng-wd2.contoso.com:8443";
            await tab.ConnectCommand.ExecuteAsync(null);
            await tab.Config.Loading;
            Assert.Equal(loads + 1, h.Client.Calls.Count(nameof(IWatchdogClient.GetConfigAsync)));
            Assert.False(tab.Config.IsDirty);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task FailedConnectToAnotherService_IsNotRestored_ByItsStatus()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.Config.Update(c => c with { Digest = c.Digest with { SendWhenEmpty = !c.Digest.SendWhenEmpty } });
            var firstAddress = h.Client.BaseAddress;

            // "Zmień adres" to another service: the caller is read, its catalog times out.
            h.Client.FailNext(nameof(IWatchdogClient.GetCatalogAsync), new WatchdogClientException(WatchdogClientErrorKind.Timeout, "slow"));
            tab.ChangeAddressCommand.Execute(null);
            tab.ServiceUrlInput = "https://admng-wd2.contoso.com:8443";
            await tab.ConnectCommand.ExecuteAsync(null);
            Assert.Equal(WatchdogConnectionState.Unreachable, tab.ConnectionState);
            Assert.NotEqual(firstAddress, h.Client.BaseAddress);

            // The poller (still running) reads the status of the new service: the tab must not come back "connected"
            // with the caller, configuration and feed of the first service (a save would PUT it to the new one).
            var calls = h.Client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync));
            h.Time.Advance(WatchdogPoller.HiddenStatusInterval);
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), calls + 1);
            await h.Ui.IdleAsync();
            await tab.Status.LoadAsync();
            Assert.Equal(WatchdogConnectionState.Unreachable, tab.ConnectionState);
            Assert.False(tab.IsConnected);

            // Retry connects to the new service properly (its own configuration).
            await tab.RefreshAsync();
            await tab.Config.Loading;
            Assert.True(tab.IsConnected);
            Assert.False(tab.Config.IsDirty);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ConnectionProblemOfACall_ClearsWithTheNextSuccessfulStatus()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), 2);
            h.Client.FailNext(nameof(IWatchdogClient.GetEventsAsync), new WatchdogClientException(WatchdogClientErrorKind.Forbidden, "403") { StatusCode = 403 });
            await tab.Events.LoadAsync();
            Assert.Equal(WatchdogConnectionState.Forbidden, tab.ConnectionState);

            // The status (Status sub-tab or the poller) answers again: the tab is usable again without Retry.
            await tab.Status.LoadAsync();
            Assert.Equal(WatchdogConnectionState.Connected, tab.ConnectionState);
            Assert.False(tab.HasConnectionProblem);

            h.Client.FailNext(nameof(IWatchdogClient.GetEventsAsync), new WatchdogClientException(WatchdogClientErrorKind.Forbidden, "403") { StatusCode = 403 });
            await tab.Events.LoadAsync();
            Assert.Equal(WatchdogConnectionState.Forbidden, tab.ConnectionState);
            var calls = h.Client.Calls.Count(nameof(IWatchdogClient.GetStatusAsync));
            h.Time.Advance(WatchdogPoller.HiddenStatusInterval);
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetStatusAsync), calls + 1);
            await WatchdogTestWait.UntilAsync(h, () => tab.IsConnected, "the poller's status");
        });
        h.AssertNoUiErrors();
    }
}
