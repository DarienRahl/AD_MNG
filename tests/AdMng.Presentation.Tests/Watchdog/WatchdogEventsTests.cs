using System.Collections;
using System.Text;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>The event feed: paging, live prepend with the "N new" badge, bulk acknowledge, CSV export, inline errors.</summary>
public sealed class WatchdogEventsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Load_ShowsTheNewestEventsFirst_WithTheRowTexts()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            var localizer = h.Ad.Localizer;

            Assert.Equal(30, feed.Items.Count);
            Assert.Equal(Enumerable.Range(1, 30).Reverse().Select(i => (long)i), feed.Items.Select(r => r.Id));
            Assert.False(feed.HasMore);
            Assert.False(feed.IsLoading);
            Assert.False(feed.IsEmpty);
            Assert.Null(feed.Error);
            var first = Assert.Single(h.Client.EventQueries, q => q.After is null);
            Assert.Equal(WatchdogEventsViewModel.PageSize, first.Limit);

            var lockout = feed.Items.Single(r => r.Id == SampleEvents.LockoutId);
            Assert.Equal("Żaneta Szymańska", lockout.ObjectName);
            Assert.Equal("contoso.test/Firma/Pracownicy/Sprzedaż/Żaneta Szymańska", lockout.ObjectPath);
            Assert.Equal(localizer.Format(L.Watchdog.LockoutsToday, 5), lockout.LockoutText);
            Assert.Equal("10.20.4.17 · WRO-LT014 (" + localizer[WatchdogDisplay.SourceOriginKey("4740")] + ")", lockout.SourceText);
            Assert.Equal(Severity.Low, lockout.Severity);
            Assert.Equal(localizer[WatchdogDisplay.SeverityKey(Severity.Low)], lockout.SeverityText);
            Assert.Equal("10/13/2026 12:30:00", lockout.TimeText);

            var privilege = feed.Items.Single(r => r.Id == SampleEvents.PrivilegeGrantedId);
            Assert.Equal(@"CONTOSO\jan.kowalski", privilege.ActorText);
            Assert.Equal(Icons.ObjectIcon.User, privilege.Icon);
            Assert.False(privilege.IsAcknowledged);

            var pending = feed.Items.Single(r => r.Id == SampleEvents.PendingAttributionId);
            Assert.Equal(localizer[L.Watchdog.ActorPending], pending.ActorText);
            Assert.True(pending.IsActorPending);

            var unknown = feed.Items.Single(r => r.Id == SampleEvents.DeletedComputerId);
            Assert.Equal(localizer.Format(L.Watchdog.ActorUnknown, localizer.ReasonText(AttributionReasons.AuditPolicyMissing)), unknown.ActorText);
            Assert.Equal("contoso.test/Firma/Komputery/KRK-LT001", unknown.ObjectPath);

            var ambiguous = feed.Items.Single(r => r.Id == SampleEvents.AmbiguousId);
            Assert.Equal(localizer.Format(L.Watchdog.ActorAmbiguous, @"CONTOSO\jan.kowalski, CONTOSO\adm.kowalski"), ambiguous.ActorText);

            Assert.True(feed.Items.Single(r => r.Id == 5).IsAcknowledged);
            Assert.Equal("Watchdog", feed.Items.Single(r => r.Id == 28).ObjectName);
            Assert.StartsWith("Events: 30 · unacknowledged: 28 · as of ", feed.StatusText, StringComparison.Ordinal);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LoadOlder_PagesBackWithBefore()
    {
        using var h = await WatchdogHarness.CreateAsync();
        for (var i = 0; i < 120; i++)
        {
            h.Client.AddEvent(SampleEvents.NewUser($"Użytkownik {i:D3}", SampleEvents.Now.AddMinutes(-5)));
        }

        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.IsVisible = true;
            await tab.Events.Loading;
            var feed = tab.Events;
            Assert.Equal(100, feed.Items.Count);
            Assert.Equal(150, feed.Items[0].Id);
            Assert.True(feed.HasMore);
            Assert.True(feed.LoadOlderCommand.CanExecute(null));

            await feed.LoadOlderCommand.ExecuteAsync(null);

            Assert.Equal(150, feed.Items.Count);
            Assert.Equal(1, feed.Items[^1].Id);
            Assert.False(feed.HasMore);
            Assert.False(feed.LoadOlderCommand.CanExecute(null));
            Assert.Equal(51, h.Client.EventQueries.Last(q => q.Before is not null).Before);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LiveEvents_ArePrepended_AndCountedWhileScrolledAway()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 2);
            Assert.Equal(30, h.Client.EventQueries[^1].After);

            h.Client.AddEvent(SampleEvents.NewUser("Celina Mróz", h.Time.GetUtcNow()));
            await WatchdogTestWait.UntilAsync(h, () => feed.Items.Count == 31, "the new event");

            Assert.Equal("Celina Mróz", feed.Items[0].ObjectName);
            Assert.False(feed.Items[0].IsNew);
            Assert.Equal(0, feed.NewCount);
            Assert.False(feed.HasNewEvents);

            // Scrolled away from the top: new events are marked and counted ("2 new").
            feed.IsScrolledAway = true;
            h.Client.AddEvent(SampleEvents.NewUser("Dorota Kruk", h.Time.GetUtcNow()));
            await WatchdogTestWait.UntilAsync(h, () => feed.Items.Count == 32, "the second event");
            h.Client.AddEvent(SampleEvents.NewUser("Edward Sowa", h.Time.GetUtcNow()));
            await WatchdogTestWait.UntilAsync(h, () => feed.Items.Count == 33, "the third event");

            Assert.Equal(2, feed.NewCount);
            Assert.True(feed.HasNewEvents);
            Assert.Equal(h.Ad.Localizer.Format(L.Watchdog.EventsNew, 2), feed.NewEventsText);
            Assert.True(feed.Items[0].IsNew);
            Assert.True(feed.Items[1].IsNew);

            var scrolled = 0;
            feed.ScrollToTopRequested += (_, _) => scrolled++;
            feed.ShowNewCommand.Execute(null);
            Assert.Equal(1, scrolled);
            Assert.Equal(0, feed.NewCount);
            Assert.False(feed.IsScrolledAway);
            Assert.DoesNotContain(feed.Items, r => r.IsNew);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LiveEvents_OutsideTheFilter_AreNotShown()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            feed.Filter.MinSeverity = feed.Filter.Severities.Single(s => s.Value == Severity.High);
            await feed.Loading;
            var shown = feed.Items.Count;
            Assert.All(feed.Items, r => Assert.True(r.Severity >= Severity.High));

            await WatchdogTestWait.UntilAsync(h, () => h.Client.EventQueries.LastOrDefault(q => q.After is not null) is { MinSeverity: Severity.High }, "the filtered long-poll");

            h.Client.AddEvent(SampleEvents.NewUser("Filip Niski", h.Time.GetUtcNow(), Severity.Low));
            h.Client.AddEvent(SampleEvents.NewUser("Gerard Wysoki", h.Time.GetUtcNow(), Severity.Critical));
            await WatchdogTestWait.UntilAsync(h, () => feed.Items.Count == shown + 1, "the critical event");

            Assert.Equal("Gerard Wysoki", feed.Items[0].ObjectName);
            Assert.DoesNotContain(feed.Items, r => r.ObjectName == "Filip Niski");
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LiveFeed_FilteredByWhatTheServiceFillsInLater_ShowsAnEventOnceItMatches()
    {
        // The service stores an event with the actor pending and evaluates/attributes it afterwards (same id): with a filter
        // on the actor the live long-poll skips it, so the status round must pick it up once it matches.
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            var resolved = h.Client.Events.Single(e => e.Id == SampleEvents.PrivilegeGrantedId).Attribution;
            feed.Filter.ActorSid = resolved.Actor!.Sid;
            await feed.Loading;
            var shown = feed.Items.Select(r => r.Id).ToList();
            Assert.NotEmpty(shown);
            await WatchdogTestWait.UntilAsync(h, () => h.Client.EventQueries.LastOrDefault(q => q.After is not null) is { ActorSid: not null }, "the filtered long-poll");

            var added = h.Client.AddEvent(SampleEvents.NewUser("Zenon Łęcki", h.Time.GetUtcNow()));
            await WatchdogTestWait.UntilAsync(h, () => h.Client.EventQueries.LastOrDefault(q => q.After is not null) is { After: { } after } && after >= added.Id, "the cursor past the pending event");
            Assert.DoesNotContain(feed.Items, r => r.Id == added.Id);

            h.Client.UpdateEvent(added.Id, e => e with { Attribution = resolved });
            h.Time.Advance(WatchdogPoller.VisibleStatusInterval);
            await WatchdogTestWait.UntilAsync(h, () => feed.Items.Any(r => r.Id == added.Id), "the late match");

            Assert.Equal(added.Id, feed.Items[0].Id);
            Assert.Equal(@"CONTOSO\jan.kowalski", feed.Items[0].ActorText);
            Assert.Equal([added.Id, .. shown], feed.Items.Select(r => r.Id));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task AcknowledgeSelected_SendsTheSelectedUnacknowledgedEvents_WithTheComment()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            Assert.False(feed.AcknowledgeSelectedCommand.CanExecute(null));

            IList selection = new List<object>
            {
                feed.Items.Single(r => r.Id == SampleEvents.PrivilegeGrantedId),
                feed.Items.Single(r => r.Id == SampleEvents.LockoutId),
                feed.Items.Single(r => r.Id == 5),
            };
            feed.SelectedItems = selection;
            Assert.Equal(h.Ad.Localizer.Format(L.Watchdog.EventsSelected, 3), feed.SelectionText);
            Assert.Equal(feed.SelectionText, tab.SelectionText);
            Assert.True(feed.AcknowledgeSelectedCommand.CanExecute(null));

            feed.AcknowledgeComment = "  Sprawdzone z działem kadr – zmiany planowane.  ";
            await feed.AcknowledgeSelectedCommand.ExecuteAsync(null);

            var request = Assert.Single(h.Client.BulkAcknowledgements);
            Assert.Equal([SampleEvents.PrivilegeGrantedId, SampleEvents.LockoutId], request.Ids.Order());
            Assert.Equal("Sprawdzone z działem kadr – zmiany planowane.", request.Comment);
            Assert.Equal(string.Empty, feed.AcknowledgeComment);
            Assert.True(feed.Items.Single(r => r.Id == SampleEvents.LockoutId).IsAcknowledged);
            Assert.Equal(@"CONTOSO\jan.kowalski", feed.Items.Single(r => r.Id == SampleEvents.LockoutId).Event.AcknowledgedBy);
            Assert.False(feed.AcknowledgeSelectedCommand.CanExecute(null));
            Assert.Contains(h.Ad.Notifications.Items, n => n.Severity == NotificationSeverity.Success);
            Assert.StartsWith("Events: 30 · unacknowledged: 26 ·", feed.StatusText, StringComparison.Ordinal);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task AcknowledgeSelected_SplitsIntoRequestsOf500()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var feed = tab.Events;
            var rows = Enumerable.Range(1, 1200)
                .Select(i => new WatchdogEventRowViewModel(SampleEvents.NewUser($"U{i}", SampleEvents.Now) with { Id = 1000 + i }, h.Ad.Localizer, TimeZoneInfo.Utc))
                .ToList<object>();
            feed.SelectedItems = rows;
            await feed.AcknowledgeSelectedCommand.ExecuteAsync(null);

            Assert.Equal([500, 500, 200], h.Client.BulkAcknowledgements.Select(r => r.Ids.Count));
            Assert.Null(h.Client.BulkAcknowledgements[0].Comment);
        });
    }

    [Fact]
    public async Task ExportCsv_WritesTheLoadedEventsOnlyWhereTheOperatorChose_FormulaSafe()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.AddEvent(SampleEvents.NewUser("=HYPERLINK(\"http://zly.example\")", SampleEvents.Now.AddMinutes(-1)));
        var path = Path.Combine(Path.GetTempPath(), $"admng-watchdog-{Guid.NewGuid():N}.csv");
        try
        {
            await h.Ui.RunAsync(async () =>
            {
                var tab = await h.OpenAsync();
                var feed = tab.Events;
                Assert.True(feed.ExportCsvCommand.CanExecute(null));

                // Cancelled save dialog: nothing is written.
                h.Ad.Files.NextSavePath = null;
                await feed.ExportCsvCommand.ExecuteAsync(null);
                Assert.Equal("watchdog-2026-10-14-1030.csv", h.Ad.Files.LastDefaultFileName);
                Assert.False(File.Exists(path));

                h.Ad.Files.NextSavePath = path;
                await feed.ExportCsvCommand.ExecuteAsync(null);
            });

            var text = await File.ReadAllTextAsync(path, Encoding.UTF8, Ct);
            var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(32, lines.Length);
            Assert.StartsWith("Time,Severity,Event,Object,Location,Distinguished name,Actor,Source,Acknowledged", lines[0], StringComparison.Ordinal);
            Assert.Contains(lines, l => l.Contains("Żaneta Szymańska", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("'=HYPERLINK", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Contains(",=HYPERLINK", StringComparison.Ordinal) || l.Contains(",\"=HYPERLINK", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }

        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LoadError_IsShownInline_AndUpdatesTheConnectionBanner()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            h.Client.FailNext(nameof(IWatchdogClient.GetEventsAsync), new WatchdogClientException(WatchdogClientErrorKind.ServerError, "boom") { StatusCode = 503 });
            await tab.Events.LoadAsync();

            Assert.Equal(h.Ad.Localizer.Format(L.Watchdog.ErrorServerStatus, 503), tab.Events.Error);
            Assert.Empty(tab.Events.Items);
            Assert.False(tab.Events.IsEmpty);
            Assert.True(tab.IsConnected);

            h.Client.FailNext(nameof(IWatchdogClient.GetEventsAsync), new WatchdogClientException(WatchdogClientErrorKind.Unauthorized, "401") { StatusCode = 401 });
            await tab.Events.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(WatchdogConnectionState.Unauthorized, tab.ConnectionState);

            await tab.RefreshAsync();
            tab.IsVisible = true;
            await tab.Events.Loading;
            Assert.True(tab.IsConnected);
            Assert.Null(tab.Events.Error);
            Assert.Equal(30, tab.Events.Items.Count);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task EmptyResult_ShowsTheHint()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            tab.Events.Filter.Text = "nie-ma-takiego-obiektu";
            h.Time.Advance(WatchdogEventFilterViewModel.TextDelay);
            await WatchdogTestWait.UntilAsync(h, () => h.Client.EventQueries.Any(q => q.Text == "nie-ma-takiego-obiektu"));
            await tab.Events.Loading;

            Assert.Empty(tab.Events.Items);
            Assert.True(tab.Events.IsEmpty);
            Assert.False(tab.Events.ExportCsvCommand.CanExecute(null));
            Assert.Equal(string.Empty, tab.Events.StatusText);
        });
    }

    [Fact]
    public async Task SupersededLoad_LeavesNoErrorOverTheReloadedFeed()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var feed = tab.Events;
            h.Client.Hold(nameof(IWatchdogClient.GetEventsAsync));

            // Two quick filter changes: the second load cancels the first one, which the client reports as a
            // WatchdogClientException of kind Cancelled (like the real HTTP client).
            var first = feed.LoadAsync();
            var second = feed.LoadAsync();
            await first;
            h.Client.Release(nameof(IWatchdogClient.GetEventsAsync));
            await second;

            Assert.Null(feed.Error);
            Assert.Equal(30, feed.Items.Count);
            Assert.False(feed.IsLoading);
            Assert.True(tab.IsConnected);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LiveEvents_ArrivingWhileOlderEventsLoad_AreNotLost()
    {
        using var h = await WatchdogHarness.CreateAsync();
        for (var i = 0; i < 120; i++)
        {
            h.Client.AddEvent(SampleEvents.NewUser($"Użytkownik {i:D3}", SampleEvents.Now.AddMinutes(-5)));
        }

        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            Assert.True(feed.HasMore);
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 2);

            // "Wczytaj starsze" is on its way while the live long-poll keeps running.
            h.Client.HoldEvents(q => q.Before is not null);
            var older = feed.LoadOlderCommand.ExecuteAsync(null);
            await WatchdogTestWait.UntilAsync(h, () => h.Client.EventQueries.Any(q => q.Before is not null), "the older page request");
            Assert.True(feed.IsLoading);

            // A new event arrives meanwhile: the long-poll answers and the poller's cursor moves past it.
            var added = h.Client.AddEvent(SampleEvents.NewUser("Bożena Wróbel", h.Time.GetUtcNow()));
            await WatchdogTestWait.UntilAsync(h, () => h.Client.EventQueries[^1].After == added.Id, "the next long-poll");
            await h.Ui.IdleAsync();

            h.Client.ReleaseEvents();
            await older;

            Assert.Equal(added.Id, feed.Items[0].Id);
            Assert.Equal("Bożena Wróbel", feed.Items[0].ObjectName);
            Assert.Equal(151, feed.Items.Count);
            Assert.Equal(feed.Items.Count, feed.Items.Select(r => r.Id).Distinct().Count());
            Assert.False(feed.HasMore);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LiveFeed_KeepsTheFilterOfTheLoadedPage_WhileTheFilterListsAreRebuilt()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            feed.Filter.KindGroups.SelectMany(g => g.Options).Single(o => o.Value == WatchEventKind.UserCreated).IsChecked = true;
            await feed.Loading;
            await WatchdogTestWait.UntilAsync(
                h,
                () => h.Client.EventQueries[^1] is { After: not null } q && q.Kinds.SequenceEqual([WatchEventKind.UserCreated]),
                "the filtered long-poll");

            // The kind lists are rebuilt (a reconnect brings the catalog again; here without the selected kind - the
            // state a poll round on another thread could see in the middle of the rebuild). No reload is started.
            feed.Filter.SetCatalog([.. EventKindCatalog.All.Where(k => k.Kind != WatchEventKind.UserCreated)]);
            var disabled = h.Client.AddEvent(SampleEvents.NewUser("Ireneusz Szary", h.Time.GetUtcNow()) with { Kind = WatchEventKind.AccountDisabled });
            var created = h.Client.AddEvent(SampleEvents.NewUser("Halina Kos", h.Time.GetUtcNow()));
            await WatchdogTestWait.UntilAsync(h, () => feed.Items.Any(r => r.Id == created.Id), "the new user");

            Assert.DoesNotContain(feed.Items, r => r.Id == disabled.Id);
            Assert.All(feed.Items, r => Assert.Equal(WatchEventKind.UserCreated, r.Event.Kind));
            Assert.Equal([WatchEventKind.UserCreated], h.Client.EventQueries[^1].Kinds);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LiveFeed_IsCapped_AndEachPageIsInsertedAtOnce()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 2);

            // The tab is in the background while a lockout storm happens; when it is shown again the poller catches up
            // in pages of 500.
            tab.IsVisible = false;
            WatchEvent last = null!;
            for (var i = 0; i < 2100; i++)
            {
                last = h.Client.AddEvent(SampleEvents.NewUser($"Konto {i:D4}", SampleEvents.Now.AddSeconds(i)));
            }

            var notifications = 0;
            feed.Items.CollectionChanged += (_, _) => notifications++;
            tab.IsVisible = true;
            await WatchdogTestWait.UntilAsync(h, () => feed.Items.Count > 0 && feed.Items[0].Id == last.Id, "the catch-up");

            Assert.Equal(WatchdogEventsViewModel.MaxLiveItems, feed.Items.Count);
            Assert.True(feed.HasMore);
            Assert.True(notifications <= 10, $"{notifications} collection notifications for 5 pages");
            Assert.Equal(feed.Items.Select(r => r.Id).OrderDescending(), feed.Items.Select(r => r.Id));

            // The trimmed (oldest) events are still reachable with "Wczytaj starsze".
            await feed.LoadOlderCommand.ExecuteAsync(null);
            Assert.Equal(feed.Items[WatchdogEventsViewModel.MaxLiveItems - 1].Id - 1, feed.Items[WatchdogEventsViewModel.MaxLiveItems].Id);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LiveEvents_AtTheCap_WhileOlderEventsLoad_LeaveNoGapInTheFeed()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            await h.Client.Calls.CallsAsync(nameof(IWatchdogClient.GetEventsAsync), 2);
            tab.IsVisible = false;
            WatchEvent last = null!;
            for (var i = 0; i < 2100; i++)
            {
                last = h.Client.AddEvent(SampleEvents.NewUser($"Konto {i:D4}", SampleEvents.Now.AddSeconds(i)));
            }

            tab.IsVisible = true;
            await WatchdogTestWait.UntilAsync(h, () => feed.Items.Count > 0 && feed.Items[0].Id == last.Id, "the catch-up");
            Assert.Equal(WatchdogEventsViewModel.MaxLiveItems, feed.Items.Count);
            var oldest = feed.Items[^1].Id;

            // "Wczytaj starsze" asks for the events before the oldest row while a live event arrives: the feed is full,
            // but the oldest row must not be trimmed under the running request (the page continues below it).
            h.Client.HoldEvents(q => q.Before is not null);
            var older = feed.LoadOlderCommand.ExecuteAsync(null);
            await WatchdogTestWait.UntilAsync(h, () => h.Client.EventQueries.Any(q => q.Before == oldest), "the older page request");
            var added = h.Client.AddEvent(SampleEvents.NewUser("Józef Łęcki", h.Time.GetUtcNow()));
            await WatchdogTestWait.UntilAsync(h, () => feed.Items[0].Id == added.Id, "the live event");
            h.Client.ReleaseEvents();
            await older;

            var ids = feed.Items.Select(r => r.Id).ToList();
            Assert.Contains(oldest, ids);
            Assert.Equal(Enumerable.Range(0, ids.Count).Select(i => ids[0] - i), ids);
            Assert.Equal(WatchdogEventsViewModel.MaxLiveItems + 1 + WatchdogEventsViewModel.PageSize, ids.Count);

            // The next live page trims the feed back to what it held before (the oldest rows go, "Wczytaj starsze" offers them).
            var next = h.Client.AddEvent(SampleEvents.NewUser("Marianna Śląska", h.Time.GetUtcNow()));
            await WatchdogTestWait.UntilAsync(h, () => feed.Items[0].Id == next.Id, "the next live event");
            Assert.Equal(ids.Count, feed.Items.Count);
            Assert.True(feed.HasMore);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LateAttribution_ReplacesThePendingActor_InTheFeedAndTheDetails()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            var row = feed.Items.Single(r => r.Id == SampleEvents.PendingAttributionId);
            Assert.True(row.IsActorPending);
            feed.SelectedItem = row;
            await feed.Details.Loading;
            Assert.Equal(h.Ad.Localizer.AttributionStatusName(AttributionStatus.Pending), feed.Details.AttributionStatusText);

            // The attribution engine resolves the actor a few seconds later (the stored event is updated, no new id).
            var resolved = h.Client.Events.Single(e => e.Id == SampleEvents.PrivilegeGrantedId).Attribution;
            h.Client.UpdateEvent(SampleEvents.PendingAttributionId, e => e with { Attribution = resolved });

            // The next status round of the visible tab re-reads the rows still waiting for attribution.
            h.Time.Advance(WatchdogPoller.VisibleStatusInterval);
            await WatchdogTestWait.UntilAsync(h, () => !row.IsActorPending, "the late attribution");

            Assert.Equal(@"CONTOSO\jan.kowalski", row.ActorText);
            Assert.Equal(h.Ad.Localizer.AttributionStatusName(AttributionStatus.Resolved), feed.Details.AttributionStatusText);
            Assert.Equal(SampleEvents.PendingAttributionId, feed.Details.Event?.Id);
        });
        h.AssertNoUiErrors();
    }
}
