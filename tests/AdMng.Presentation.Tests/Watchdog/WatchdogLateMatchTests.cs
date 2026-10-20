using System.Collections.Specialized;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// The live feed with a filter on what the service fills in after it stored an event (severity, tags, attribution status,
/// actor, matched rules, text): events that start to match within the attribution window are merged in completely (paged
/// over the whole window of the loaded range), rows that stop matching leave the list (the selected one is marked until it
/// is deselected), changed rows show their new state – without reloads, duplicates or a blocked UI.
/// </summary>
public sealed class WatchdogLateMatchTests
{
    [Fact]
    public async Task LateMatches_OfABulkOperation_AllAppearInOneStatusRound_InIdOrder()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (feed, resolved) = await OpenFilteredByActorAsync(h);
            var shown = feed.Items.Select(r => r.Id).ToList();

            // A bulk operation of Jan Kowalski: 1200 accounts stored with the actor pending – the long-poll skips them all.
            var added = Enumerable.Range(0, 1200)
                .Select(i => h.Client.AddEvent(SampleEvents.NewUser($"Konto hurtowe {i:D4} Żółć", h.Time.GetUtcNow().AddSeconds(-i))))
                .ToList();
            await WaitForCursorAsync(h, added[^1].Id);
            Assert.DoesNotContain(feed.Items, r => r.Id >= added[0].Id);

            foreach (var watchEvent in added)
            {
                h.Client.UpdateEvent(watchEvent.Id, e => e with { Attribution = resolved });
            }

            var changes = WatchdogEventsTests.Record(feed);
            await NextStatusRoundAsync(h, feed);

            Assert.Equal([.. added.Select(e => e.Id).OrderDescending(), .. shown], feed.Items.Select(r => r.Id));
            Assert.All(feed.Items.Take(added.Count), r => Assert.Equal(@"CONTOSO\jan.kowalski", r.ActorText));

            // One Add per late row, none for the rows shown before (no Reset: the list keeps its place).
            Assert.Equal(added.Count, changes.Count);
            Assert.All(changes, c => Assert.Equal(NotifyCollectionChangedAction.Add, c.Action));

            // The next round finds nothing new: no duplicates, nothing moves.
            await NextStatusRoundAsync(h, feed);
            Assert.Equal([.. added.Select(e => e.Id).OrderDescending(), .. shown], feed.Items.Select(r => r.Id));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LateMatches_BetweenShownRows_AreInsertedRowByRow_TheOperatorsRowsAndSelectionStay()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (feed, resolved) = await OpenFilteredByActorAsync(h);

            // Events of Jan Kowalski (shown at once) between events whose actor is still being determined.
            var late = new List<WatchEvent>();
            var current = new List<WatchEvent>();
            for (var i = 0; i < 4; i++)
            {
                late.Add(h.Client.AddEvent(SampleEvents.NewUser($"Późna Żaneta {i}", h.Time.GetUtcNow())));
                current.Add(h.Client.AddEvent(SampleEvents.NewUser($"Bieżący Łukasz {i}", h.Time.GetUtcNow()) with { Attribution = resolved }));
            }

            await WaitForCursorAsync(h, current[^1].Id);
            await WatchdogTestWait.UntilAsync(h, () => current.All(e => feed.Items.Any(r => r.Id == e.Id)), "the matching events");
            var before = feed.Items.ToList();
            var selected = feed.Items.Single(r => r.Id == current[1].Id);
            feed.SelectedItem = selected;
            feed.SelectedItems = new List<object> { selected };

            // One status round finds all four, each between two shown rows.
            foreach (var watchEvent in late)
            {
                h.Client.UpdateEvent(watchEvent.Id, e => e with { Attribution = resolved });
            }

            var changes = WatchdogEventsTests.Record(feed);
            await NextStatusRoundAsync(h, feed);

            var ids = feed.Items.Select(r => r.Id).ToList();
            Assert.Equal(ids.OrderDescending(), ids);
            Assert.Subset(ids.ToHashSet(), late.Select(e => e.Id).ToHashSet());

            // Inserted one by one at their place (no Reset, no Move); the rows shown before are the same objects.
            Assert.Equal(late.Count, changes.Count);
            Assert.All(changes, c =>
            {
                Assert.Equal(NotifyCollectionChangedAction.Add, c.Action);
                Assert.Same(feed.Items[c.NewStartingIndex], Assert.Single(c.NewItems!.Cast<object>()));
            });
            Assert.Equal(before, feed.Items.Where(r => before.Contains(r)));
            Assert.Same(selected, feed.SelectedItem);
            Assert.Equal([selected], feed.Selection);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LateMatches_BeyondTheLiveCap_KeepTheNewestRows_AndOfferTheRestWithLoadOlder()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (feed, resolved) = await OpenFilteredByActorAsync(h);
            var added = Enumerable.Range(0, WatchdogEventsViewModel.MaxLiveItems + 100)
                .Select(i => h.Client.AddEvent(SampleEvents.NewUser($"Konto {i:D4} Łąkowe", h.Time.GetUtcNow().AddSeconds(-i))))
                .ToList();
            await WaitForCursorAsync(h, added[^1].Id);
            foreach (var watchEvent in added)
            {
                h.Client.UpdateEvent(watchEvent.Id, e => e with { Attribution = resolved });
            }

            await NextStatusRoundAsync(h, feed);

            var ids = feed.Items.Select(r => r.Id).ToList();
            Assert.Equal(WatchdogEventsViewModel.MaxLiveItems, ids.Count);
            Assert.Equal(added.Select(e => e.Id).OrderDescending().Take(WatchdogEventsViewModel.MaxLiveItems), ids);
            Assert.True(feed.HasMore);

            await feed.LoadOlderCommand.ExecuteAsync(null);
            Assert.Equal(ids[^1] - 1, feed.Items[WatchdogEventsViewModel.MaxLiveItems].Id);
            Assert.Equal(feed.Items.Count, feed.Items.Select(r => r.Id).Distinct().Count());
        });
        h.AssertNoUiErrors();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LateMatch_WithinTheServicesConfiguredAttributionRetries_IsFound(bool admin)
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.IsAdmin = admin;
        SetRetries(h, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), TimeSpan.FromHours(6));

        // Detected four hours ago, still waiting for the actor: the service retries until six hours after detection.
        var late = h.Client.AddEvent(SampleEvents.NewUser("Ludwika Późna", SampleEvents.Now.AddHours(-4)));
        await h.Ui.RunAsync(async () =>
        {
            var (feed, resolved) = await OpenFilteredByActorAsync(h);
            Assert.Equal(TimeSpan.FromHours(6) + new AttributionSettings().PollInterval + WatchdogEventsViewModel.LateMatchAllowance, feed.LateMatchWindow);
            Assert.DoesNotContain(feed.Items, r => r.Id == late.Id);

            h.Client.UpdateEvent(late.Id, e => e with { Attribution = resolved });
            await NextStatusRoundAsync(h, feed);

            Assert.Contains(feed.Items, r => r.Id == late.Id);
            Assert.Equal(feed.Items.Select(r => r.Id).OrderDescending(), feed.Items.Select(r => r.Id));
            // The window counts back from the previous round (the feed's last view of the service's state).
            var read = LateMatchQueries(h).Last();
            Assert.Equal(h.Time.GetUtcNow() - WatchdogPoller.VisibleStatusInterval - feed.LateMatchWindow, read.From);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task LateMatchWindow_FollowsTheConfigurationTheServiceRuns()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            Assert.Equal(WatchdogEventsViewModel.DefaultLateMatchWindow, tab.Events.LateMatchWindow);

            // An administrator lengthens the retries (here: another console); the tab reloads the configuration.
            SetRetries(h, TimeSpan.FromMinutes(1), TimeSpan.FromHours(12));
            await tab.Config.LoadAsync();

            Assert.Equal(TimeSpan.FromHours(12) + new AttributionSettings().PollInterval + WatchdogEventsViewModel.LateMatchAllowance, tab.Events.LateMatchWindow);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public void LateMatchWindowFor_CoversTheLastRetry_NeverLessThanTheDefault()
    {
        var defaults = new WatchdogConfiguration();
        Assert.Equal(WatchdogEventsViewModel.DefaultLateMatchWindow, WatchdogEventsViewModel.LateMatchWindowFor(null));
        Assert.Equal(WatchdogEventsViewModel.DefaultLateMatchWindow, WatchdogEventsViewModel.LateMatchWindowFor(defaults));

        var lengthy = defaults with
        {
            Attribution = defaults.Attribution with
            {
                PendingRetries = [TimeSpan.FromHours(20), TimeSpan.FromMinutes(5)],
                PollInterval = TimeSpan.FromMinutes(2),
            },
        };
        Assert.Equal(TimeSpan.FromHours(20) + TimeSpan.FromMinutes(2) + WatchdogEventsViewModel.LateMatchAllowance, WatchdogEventsViewModel.LateMatchWindowFor(lengthy));

        // Attribution off: events are final at once – the default bound (rule evaluation) applies.
        Assert.Equal(WatchdogEventsViewModel.DefaultLateMatchWindow, WatchdogEventsViewModel.LateMatchWindowFor(lengthy with { Attribution = lengthy.Attribution with { Enabled = false } }));
        Assert.Equal(WatchdogEventsViewModel.DefaultLateMatchWindow, WatchdogEventsViewModel.LateMatchWindowFor(lengthy with { Attribution = lengthy.Attribution with { Source = AttributionSourceKind.None } }));
        Assert.Equal(WatchdogEventsViewModel.DefaultLateMatchWindow, WatchdogEventsViewModel.LateMatchWindowFor(lengthy with { Attribution = lengthy.Attribution with { PendingRetries = [] } }));
    }

    [Fact]
    public async Task RepeatedRounds_WithLiveEventsAndRowsLeavingTheFilter_ChangeTheFeedRowByRow_TheSelectedRowStays()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (feed, resolved) = await OpenFilteredByActorAsync(h);

            // Events of Jan Kowalski (shown at once) between events whose actor is still being determined.
            var current = new List<WatchEvent>();
            var late = new List<WatchEvent>();
            for (var i = 0; i < 6; i++)
            {
                current.Add(h.Client.AddEvent(SampleEvents.NewUser($"Bieżąca Łucja {i}", h.Time.GetUtcNow()) with { Attribution = resolved }));
                late.Add(h.Client.AddEvent(SampleEvents.NewUser($"Późny Zdzisław {i}", h.Time.GetUtcNow())));
            }

            await WaitForCursorAsync(h, late[^1].Id);
            await WatchdogTestWait.UntilAsync(h, () => current.All(e => feed.Items.Any(r => r.Id == e.Id)), "the matching events");
            var selected = feed.Items.Single(r => r.Id == current[2].Id);
            feed.SelectedItem = selected;
            feed.SelectedItems = new List<object> { selected };
            var changes = WatchdogEventsTests.Record(feed);

            // Round 1: two late matches, and a live event of Jan Kowalski on top.
            h.Client.UpdateEvent(late[1].Id, e => e with { Attribution = resolved });
            h.Client.UpdateEvent(late[4].Id, e => e with { Attribution = resolved });
            var live = h.Client.AddEvent(SampleEvents.NewUser("Nowa Żaneta", h.Time.GetUtcNow()) with { Attribution = resolved });
            await WaitForCursorAsync(h, live.Id);
            await NextStatusRoundAsync(h, feed);

            // Round 2: two shown events turn out to be Anna Nowak's – the selected one stays (marked), the other leaves – and
            // one more late match.
            var other = resolved with
            {
                Actor = resolved.Actor! with { Sid = resolved.Actor.Sid + "9", Account = @"CONTOSO\anna.nowak", DisplayName = "Anna Nowak" },
            };
            h.Client.UpdateEvent(current[4].Id, e => e with { Attribution = other });
            h.Client.UpdateEvent(current[2].Id, e => e with { Attribution = other });
            h.Client.UpdateEvent(late[0].Id, e => e with { Attribution = resolved });
            await NextStatusRoundAsync(h, feed);

            // Round 3: nothing changed – nothing moves.
            var settled = changes.Count;
            await NextStatusRoundAsync(h, feed);
            Assert.Equal(settled, changes.Count);
            Assert.Same(selected, feed.SelectedItem);
            Assert.Same(selected, feed.Items.Single(r => r.Id == current[2].Id));
            Assert.True(selected.NoLongerMatches);

            // Round 4, after the operator deselected it: the marked row leaves too.
            feed.SelectedItem = null;
            feed.SelectedItems = new List<object>();
            await NextStatusRoundAsync(h, feed);

            Assert.All(changes, c => Assert.True(c.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove, $"{c.Action}"));
            Assert.Equal(
                new[] { live.Id, late[0].Id, late[1].Id, late[4].Id }.Order(),
                changes.Where(c => c.Action == NotifyCollectionChangedAction.Add).Select(c => Assert.IsType<WatchdogEventRowViewModel>(Assert.Single(c.NewItems!.Cast<object>())).Id).Order());
            Assert.Equal(
                [current[4].Id, current[2].Id],
                changes.Where(c => c.Action == NotifyCollectionChangedAction.Remove).Select(c => Assert.IsType<WatchdogEventRowViewModel>(Assert.Single(c.OldItems!.Cast<object>())).Id));
            var ids = feed.Items.Select(r => r.Id).ToList();
            Assert.Equal(ids.OrderDescending(), ids);
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.DoesNotContain(current[2].Id, ids);
            Assert.DoesNotContain(current[4].Id, ids);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task RowsThatNoLongerMatch_LeaveTheList_ChangedRowsShowTheirNewState()
    {
        using var h = await WatchdogHarness.CreateAsync();
        var lowered = h.Client.AddEvent(SampleEvents.NewUser("Bogna Wysocka", SampleEvents.Now.AddMinutes(-3), Severity.High));
        var raised = h.Client.AddEvent(SampleEvents.NewUser("Czesław Wyższy", SampleEvents.Now.AddMinutes(-2), Severity.High));
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            feed.Filter.MinSeverity = feed.Filter.Severities.Single(s => s.Value == Severity.High);
            await feed.Loading;
            await WaitForFilteredPollAsync(h, q => q.MinSeverity == Severity.High);
            var before = feed.Items.Select(r => r.Id).ToList();
            Assert.Contains(lowered.Id, before);
            var raisedRow = feed.Items.Single(r => r.Id == raised.Id);

            // The final attribution re-resolves the severities (e.g. a self-service password change, a reset by an admin).
            h.Client.UpdateEvent(lowered.Id, e => e with { Severity = Severity.Medium });
            h.Client.UpdateEvent(raised.Id, e => e with { Severity = Severity.Critical, Tags = [EventTags.PasswordReset] });
            var changes = WatchdogEventsTests.Record(feed);
            await NextStatusRoundAsync(h, feed);

            Assert.Equal(before.Where(id => id != lowered.Id), feed.Items.Select(r => r.Id));
            var change = Assert.Single(changes);
            Assert.Equal(NotifyCollectionChangedAction.Remove, change.Action);
            Assert.Equal(before.IndexOf(lowered.Id), change.OldStartingIndex);
            Assert.Same(raisedRow, feed.Items.Single(r => r.Id == raised.Id));
            Assert.Equal(Severity.Critical, raisedRow.Severity);
            Assert.Equal(h.Ad.Localizer[WatchdogDisplay.SeverityKey(Severity.Critical)], raisedRow.SeverityText);
            Assert.All(feed.Items, r => Assert.False(r.NoLongerMatches));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task SelectedRowThatNoLongerMatches_IsMarked_AndLeavesOnceDeselected()
    {
        using var h = await WatchdogHarness.CreateAsync();
        var changing = h.Client.AddEvent(SampleEvents.NewUser("Danuta Zmienna", SampleEvents.Now.AddMinutes(-3), Severity.High));
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            feed.Filter.MinSeverity = feed.Filter.Severities.Single(s => s.Value == Severity.High);
            await feed.Loading;
            await WaitForFilteredPollAsync(h, q => q.MinSeverity == Severity.High);
            var row = feed.Items.Single(r => r.Id == changing.Id);
            feed.SelectedItem = row;
            await feed.Details.Loading;
            var before = feed.Items.Select(r => r.Id).ToList();

            h.Client.UpdateEvent(changing.Id, e => e with { Severity = Severity.Low });
            await NextStatusRoundAsync(h, feed);

            // The operator is reading it: it stays (marked) and stays selected.
            Assert.Equal(before, feed.Items.Select(r => r.Id));
            Assert.Same(row, feed.SelectedItem);
            Assert.True(row.NoLongerMatches);
            Assert.Equal(Severity.Low, row.Severity);
            Assert.Equal(Severity.Low, feed.Details.Event?.Severity);

            // Matching again clears the mark.
            h.Client.UpdateEvent(changing.Id, e => e with { Severity = Severity.High });
            await NextStatusRoundAsync(h, feed);
            Assert.False(row.NoLongerMatches);

            h.Client.UpdateEvent(changing.Id, e => e with { Severity = Severity.Low });
            await NextStatusRoundAsync(h, feed);
            Assert.True(row.NoLongerMatches);

            feed.SelectedItem = null;
            await NextStatusRoundAsync(h, feed);
            Assert.Equal(before.Where(id => id != changing.Id), feed.Items.Select(r => r.Id));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task OnlyTheLoadedRangeIsRead_WhenOlderEventsAreNotLoaded()
    {
        using var h = await WatchdogHarness.CreateAsync();
        for (var i = 0; i < 300; i++)
        {
            h.Client.AddEvent(SampleEvents.NewUser($"Konto {i:D3} Wysokie", SampleEvents.Now.AddMinutes(-60).AddSeconds(i), Severity.High));
        }

        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            feed.Filter.MinSeverity = feed.Filter.Severities.Single(s => s.Value == Severity.High);
            await feed.Loading;
            await WaitForFilteredPollAsync(h, q => q.MinSeverity == Severity.High);
            Assert.True(feed.HasMore);
            var before = feed.Items.Select(r => r.Id).ToList();
            var reads = LateMatchQueries(h).Count;

            await NextStatusRoundAsync(h, feed);

            // One page reaches below the oldest loaded row: the rest of the window is left to "Wczytaj starsze".
            Assert.Equal(reads + 1, LateMatchQueries(h).Count);
            Assert.Equal(before, feed.Items.Select(r => r.Id));
            Assert.True(feed.HasMore);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task NoFilterOnLateFields_NoExtraReads()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            feed.Filter.KindGroups.SelectMany(g => g.Options).Single(o => o.Value == WatchEventKind.UserCreated).IsChecked = true;
            await feed.Loading;
            await WaitForFilteredPollAsync(h, q => q.Kinds.Count == 1);

            await NextStatusRoundAsync(h, feed);
            await NextStatusRoundAsync(h, feed);

            Assert.Empty(LateMatchQueries(h));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task AReloadDuringTheMerge_WinsOverTheStaleRead()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (feed, resolved) = await OpenFilteredByActorAsync(h);
            var added = h.Client.AddEvent(SampleEvents.NewUser("Edyta Spóźniona", h.Time.GetUtcNow()));
            await WaitForCursorAsync(h, added.Id);
            h.Client.UpdateEvent(added.Id, e => e with { Attribution = resolved });

            h.Client.HoldEvents(IsLateMatchQuery);
            var reads = LateMatchQueries(h).Count;
            var rounds = feed.CompletedLateRefreshes;
            h.Time.Advance(WatchdogPoller.VisibleStatusInterval);
            await WatchdogTestWait.UntilAsync(h, () => LateMatchQueries(h).Count > reads, "the held late-match read");

            // The operator narrows the filter meanwhile: only Critical events of Jan Kowalski.
            feed.Filter.MinSeverity = feed.Filter.Severities.Single(s => s.Value == Severity.Critical);
            await feed.Loading;
            var reloaded = feed.Items.Select(r => r.Id).ToList();
            Assert.DoesNotContain(added.Id, reloaded);

            h.Client.ReleaseEvents();
            await WatchdogTestWait.UntilAsync(h, () => feed.CompletedLateRefreshes > rounds, "the end of the stale round");
            await h.Ui.IdleAsync();

            Assert.Equal(reloaded, feed.Items.Select(r => r.Id));
            Assert.All(feed.Items, r => Assert.False(r.NoLongerMatches));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ClosingTheTabDuringTheMerge_IsQuiet()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (feed, resolved) = await OpenFilteredByActorAsync(h);
            var added = h.Client.AddEvent(SampleEvents.NewUser("Felicja Zamknięta", h.Time.GetUtcNow()));
            await WaitForCursorAsync(h, added.Id);
            h.Client.UpdateEvent(added.Id, e => e with { Attribution = resolved });
            h.Client.HoldEvents(IsLateMatchQuery);
            var reads = LateMatchQueries(h).Count;
            h.Time.Advance(WatchdogPoller.VisibleStatusInterval);
            await WatchdogTestWait.UntilAsync(h, () => LateMatchQueries(h).Count > reads, "the held late-match read");
            var shown = feed.Items.Select(r => r.Id).ToList();

            feed.Dispose();
            h.Client.ReleaseEvents();
            await h.Ui.IdleAsync();

            Assert.Equal(shown, feed.Items.Select(r => r.Id));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task UnacknowledgedOnly_AcknowledgedElsewhere_StaysShownAsAcknowledged_AcknowledgedLateMatchIsNotInserted()
    {
        using var h = await WatchdogHarness.CreateAsync();
        var acknowledged = h.Client.AddEvent(SampleEvents.NewUser("Grażyna Potwierdzona", SampleEvents.Now.AddMinutes(-3), Severity.High));
        var late = h.Client.AddEvent(SampleEvents.NewUser("Henryk Późny", SampleEvents.Now.AddMinutes(-2)));
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            feed.Filter.Apply(new EventQuery { MinSeverity = Severity.High, UnacknowledgedOnly = true });
            await feed.Loading;
            await WaitForFilteredPollAsync(h, q => q.MinSeverity == Severity.High && q.UnacknowledgedOnly);
            var before = feed.Items.Select(r => r.Id).ToList();
            Assert.Contains(acknowledged.Id, before);

            // Anna Nowak acknowledges one event in her console; another one becomes High after it was acknowledged.
            h.Client.UpdateEvent(acknowledged.Id, e => e with { AcknowledgedAt = h.Time.GetUtcNow(), AcknowledgedBy = @"CONTOSO\anna.nowak" });
            h.Client.UpdateEvent(late.Id, e => e with { Severity = Severity.High, AcknowledgedAt = h.Time.GetUtcNow(), AcknowledgedBy = @"CONTOSO\anna.nowak" });
            await NextStatusRoundAsync(h, feed);

            Assert.Equal(before, feed.Items.Select(r => r.Id));
            Assert.True(feed.Items.Single(r => r.Id == acknowledged.Id).IsAcknowledged);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task AStaleRead_NeverUndoesAnAcknowledgement()
    {
        using var h = await WatchdogHarness.CreateAsync();
        var high = h.Client.AddEvent(SampleEvents.NewUser("Irena Uważna", SampleEvents.Now.AddMinutes(-3), Severity.High));
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            feed.Filter.MinSeverity = feed.Filter.Severities.Single(s => s.Value == Severity.High);
            await feed.Loading;
            await WaitForFilteredPollAsync(h, q => q.MinSeverity == Severity.High);
            feed.SelectedItems = new List<object> { feed.Items.Single(r => r.Id == high.Id) };
            await feed.AcknowledgeSelectedCommand.ExecuteAsync(null);
            Assert.True(feed.Items.Single(r => r.Id == high.Id).IsAcknowledged);

            // A read that started before the acknowledgement reached the service (simulated: the stored event without it).
            h.Client.UpdateEvent(high.Id, e => e with { AcknowledgedAt = null, AcknowledgedBy = null, AcknowledgeComment = null });
            await NextStatusRoundAsync(h, feed);

            Assert.True(feed.Items.Single(r => r.Id == high.Id).IsAcknowledged);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ChangesWhileTheTabWasHidden_LongerThanTheWindow_AreMergedOnceItIsShownAgain()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (tab, resolved) = await OpenTabFilteredByActorAsync(h);
            var feed = tab.Events;
            var leaving = h.Client.AddEvent(SampleEvents.NewUser("Jadwiga Odchodząca", h.Time.GetUtcNow()) with { Attribution = resolved });
            var late = h.Client.AddEvent(SampleEvents.NewUser("Kazimierz Spóźniony", h.Time.GetUtcNow()));
            await WaitForCursorAsync(h, late.Id);
            await WatchdogTestWait.UntilAsync(h, () => feed.Items.Any(r => r.Id == leaving.Id), "the matching event in the feed");
            await NextStatusRoundAsync(h, feed);
            Assert.DoesNotContain(feed.Items, r => r.Id == late.Id);

            // The operator switches to another document for three hours; meanwhile the service attributes both events:
            // one to Jan Kowalski (matches now), the other to Anna Nowak (no longer matches).
            tab.IsVisible = false;
            h.Client.UpdateEvent(late.Id, e => e with { Attribution = resolved });
            h.Client.UpdateEvent(leaving.Id, e => e with { Attribution = resolved with { Actor = SamplePrincipals.AnnaNowak } });
            h.Time.Advance(TimeSpan.FromHours(3));
            await h.Ui.IdleAsync();

            tab.IsVisible = true;
            await NextStatusRoundAsync(h, feed);

            Assert.Contains(feed.Items, r => r.Id == late.Id);
            Assert.DoesNotContain(feed.Items, r => r.Id == leaving.Id);
            Assert.Equal(feed.Items.Select(r => r.Id).OrderDescending(), feed.Items.Select(r => r.Id));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task AfterAServiceOutage_TheCatchUpOnEventsStoredBeforeIt_IsMerged()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (tab, resolved) = await OpenTabFilteredByActorAsync(h);
            var feed = tab.Events;
            var late = h.Client.AddEvent(SampleEvents.NewUser("Lucyna Przedawaryjna", h.Time.GetUtcNow()));
            await WaitForCursorAsync(h, late.Id);
            await NextStatusRoundAsync(h, feed);

            // The service host is down for three hours (an update window), then starts again; the console reconnects.
            h.Client.FailWith(WatchdogClientErrorKind.Unreachable);
            h.Time.Advance(TimeSpan.FromHours(3));
            await h.Ui.IdleAsync();
            h.Client.ClearFailures();
            h.Client.Status = h.Client.Status with { StartedAt = h.Time.GetUtcNow() };
            await NextStatusRoundAsync(h, feed, untilDone: true);
            Assert.DoesNotContain(feed.Items, r => r.Id == late.Id);

            // Two minutes after the start it catches up on the attribution of the events it stored before the outage.
            h.Time.Advance(TimeSpan.FromMinutes(2));
            h.Client.UpdateEvent(late.Id, e => e with { Attribution = resolved });
            await NextStatusRoundAsync(h, feed);

            Assert.Contains(feed.Items, r => r.Id == late.Id);

            // Once the service has run for the whole window, the rounds count back from the previous one again.
            h.Time.Advance(feed.LateMatchWindow);
            await NextStatusRoundAsync(h, feed);
            await NextStatusRoundAsync(h, feed);
            Assert.Equal(h.Time.GetUtcNow() - WatchdogPoller.VisibleStatusInterval - feed.LateMatchWindow, LateMatchQueries(h).Last().From);
            Assert.Single(feed.Items, r => r.Id == late.Id);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task AnEventThatLeavesAndMatchesAgain_ComesBackOnce_RoundAfterRound()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (feed, resolved) = await OpenFilteredByActorAsync(h);
            var flapping = h.Client.AddEvent(SampleEvents.NewUser("Mirosław Zmienny", h.Time.GetUtcNow()) with { Attribution = resolved });
            await WatchdogTestWait.UntilAsync(h, () => feed.Items.Any(r => r.Id == flapping.Id), "the matching event in the feed");
            var other = resolved with { Actor = SamplePrincipals.MarekZajac };

            for (var cycle = 0; cycle < 3; cycle++)
            {
                h.Client.UpdateEvent(flapping.Id, e => e with { Attribution = other });
                await NextStatusRoundAsync(h, feed);
                Assert.DoesNotContain(feed.Items, r => r.Id == flapping.Id);

                h.Client.UpdateEvent(flapping.Id, e => e with { Attribution = resolved });
                await NextStatusRoundAsync(h, feed);
                Assert.Single(feed.Items, r => r.Id == flapping.Id);
                Assert.Equal(feed.Items.Count, feed.Items.Select(r => r.Id).Distinct().Count());
                Assert.Equal(feed.Items.Select(r => r.Id).OrderDescending(), feed.Items.Select(r => r.Id));
            }
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task ABacklogAfterTheTabWasHidden_FromThePollAndTheMergeAtOnce_StaysInIdOrder()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var feed = tab.Events;
            feed.Filter.MinSeverity = feed.Filter.Severities.Single(s => s.Value == Severity.High);
            await feed.Loading;
            await WaitForFilteredPollAsync(h, q => q.MinSeverity == Severity.High);

            // The operator works in another document while a bulk operation stores 1200 High events.
            tab.IsVisible = false;
            await h.Ui.IdleAsync();
            var added = Enumerable.Range(0, 1200)
                .Select(i => h.Client.AddEvent(SampleEvents.NewUser($"Konto {i:D4} Źdźbło", h.Time.GetUtcNow(), Severity.High)))
                .ToList();

            // Shown again 45 s after the last status: the status round is due at once, so the late-match merge (newest
            // first) runs next to the long-poll catching up from its old cursor (oldest first). The merge's second page
            // waits until the poll is done, so the poll's pages arrive between the merge's.
            h.Client.HoldEvents(q => IsLateMatchQuery(q) && q.Before is not null);
            var changes = WatchdogEventsTests.Record(feed);
            h.Time.Advance(TimeSpan.FromSeconds(45));
            tab.IsVisible = true;
            await WaitForCursorAsync(h, added[^1].Id);
            await WatchdogTestWait.UntilAsync(h, () => LateMatchQueries(h).Any(q => q.Before is not null), "the merge's second page");
            await h.Ui.IdleAsync();

            var ids = feed.Items.Select(r => r.Id).ToList();
            Assert.Equal(ids.OrderDescending(), ids);

            var rounds = feed.CompletedLateRefreshes;
            h.Client.ReleaseEvents();
            await WatchdogTestWait.UntilAsync(h, () => feed.CompletedLateRefreshes > rounds, "the end of the merge");
            await h.Ui.IdleAsync();

            ids = [.. feed.Items.Select(r => r.Id)];
            Assert.Equal(ids.OrderDescending(), ids);
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Subset(ids.ToHashSet(), added.Select(e => e.Id).ToHashSet());

            // Poll pages that land between the merge's rows and the merge's pages: row by row, never a Reset.
            Assert.DoesNotContain(changes, c => c.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove));
            Assert.Equal(added.Count, changes.Count(c => c.Action == NotifyCollectionChangedAction.Add));
        });
        h.AssertNoUiErrors();
    }

    private static bool IsLateMatchQuery(EventQuery query) =>
        query.After is null && query.From is { } from && from > SampleEvents.Now.AddDays(-2);

    private static List<EventQuery> LateMatchQueries(WatchdogHarness h) => [.. h.Client.EventQueries.Where(IsLateMatchQuery)];

    private static void SetRetries(WatchdogHarness h, params TimeSpan[] retries)
    {
        var stored = h.Client.StoredConfig;
        var configuration = stored.Configuration;
        h.Client.StoredConfig = stored with
        {
            Configuration = configuration with { Attribution = configuration.Attribution with { PendingRetries = retries } },
        };
    }

    /// <summary>A visible tab filtered on Jan Kowalski as the actor, with the filtered long-poll running.</summary>
    private static async Task<(WatchdogEventsViewModel Feed, Attribution Resolved)> OpenFilteredByActorAsync(WatchdogHarness h)
    {
        var (tab, resolved) = await OpenTabFilteredByActorAsync(h);
        return (tab.Events, resolved);
    }

    private static async Task<(WatchdogDocumentViewModel Tab, Attribution Resolved)> OpenTabFilteredByActorAsync(WatchdogHarness h)
    {
        var tab = await h.OpenAsync();
        var feed = tab.Events;
        var resolved = h.Client.Events.Single(e => e.Id == SampleEvents.PrivilegeGrantedId).Attribution;
        feed.Filter.ActorSid = resolved.Actor!.Sid;
        await feed.Loading;
        Assert.NotEmpty(feed.Items);
        await WaitForFilteredPollAsync(h, q => q.ActorSid is not null);
        return (tab, resolved);
    }

    private static Task WaitForFilteredPollAsync(WatchdogHarness h, Func<EventQuery, bool> match) =>
        WatchdogTestWait.UntilAsync(h, () => h.Client.EventQueries.LastOrDefault(q => q.After is not null) is { } q && match(q), "the filtered long-poll");

    private static Task WaitForCursorAsync(WatchdogHarness h, long id) =>
        WatchdogTestWait.UntilAsync(h, () => h.Client.EventQueries.LastOrDefault(q => q.After is not null) is { After: { } after } && after >= id, "the cursor past the new events");

    /// <summary>
    /// Advances the clock to the next status round of the visible tab and waits until its refresh completed; with
    /// <paramref name="untilDone"/> the clock keeps moving in status intervals until one did (a poller backing off).
    /// </summary>
    private static async Task NextStatusRoundAsync(WatchdogHarness h, WatchdogEventsViewModel feed, bool untilDone = false)
    {
        var rounds = feed.CompletedLateRefreshes;
        h.Time.Advance(WatchdogPoller.VisibleStatusInterval);
        if (untilDone)
        {
            for (var i = 0; i < 10 && feed.CompletedLateRefreshes == rounds; i++)
            {
                await h.Ui.IdleAsync();
                h.Time.Advance(WatchdogPoller.VisibleStatusInterval);
            }
        }

        await WatchdogTestWait.UntilAsync(h, () => feed.CompletedLateRefreshes > rounds, "the next status round");
    }
}
