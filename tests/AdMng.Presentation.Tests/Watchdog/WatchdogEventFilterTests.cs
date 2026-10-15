using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>Feed filters: every condition, debounced text, pickers through the AD session, presets in the settings.</summary>
public sealed class WatchdogEventFilterTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");

    [Fact]
    public async Task ToQuery_MapsEveryCondition_DatesAsWholeLocalDays()
    {
        using var h = await WatchdogHarness.CreateAsync(zone: Warsaw);
        await h.Ui.RunAsync(() =>
        {
            var filter = new WatchdogEventFilterViewModel(h.Services);
            var changes = 0;
            filter.Changed += (_, _) => changes++;

            filter.MinSeverity = filter.Severities.Single(s => s.Value == Severity.Medium);
            filter.Categories.Single(c => c.Value == EventCategory.Security).IsChecked = true;
            filter.KindGroups.SelectMany(g => g.Options).Single(o => o.Value == WatchEventKind.AccountLockedOut).IsChecked = true;
            filter.ObjectKinds.Single(o => o.Value == WatchObjectKind.User).IsChecked = true;
            filter.Tags.Single(t => t.Value == EventTags.Privileged).IsChecked = true;
            filter.AttributionStatuses.Single(a => a.Value == AttributionStatus.Pending).IsChecked = true;
            filter.UnresolvedOnly = true;
            filter.Subtree = SamplePrincipals.SalesOu;
            filter.ActorText = SamplePrincipals.JanKowalski.Sid!;
            filter.From = new DateTime(2026, 10, 1);
            filter.To = new DateTime(2026, 10, 14);
            filter.UnacknowledgedOnly = true;
            filter.RuleId = "lockouts";

            var query = filter.ToQuery();
            Assert.Equal(Severity.Medium, query.MinSeverity);
            Assert.Equal([EventCategory.Security], query.Categories);
            Assert.Equal([WatchEventKind.AccountLockedOut], query.Kinds);
            Assert.Equal([WatchObjectKind.User], query.ObjectKinds);
            Assert.Equal([EventTags.Privileged], query.Tags);
            Assert.Equal([AttributionStatus.Pending, AttributionStatus.Unknown, AttributionStatus.Ambiguous], query.AttributionStatuses);
            Assert.Equal(SamplePrincipals.SalesOu, query.Subtree);
            Assert.Equal(SamplePrincipals.JanKowalski.Sid, query.ActorSid);
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2)), query.From);
            Assert.Equal(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.FromHours(2)).AddTicks(-1), query.To);
            Assert.True(query.UnacknowledgedOnly);
            Assert.Equal("lockouts", query.RuleId);
            Assert.Null(query.After);
            Assert.Null(query.Before);
            Assert.Equal(0, query.WaitSeconds);
            Assert.Equal(13, filter.ActiveCount);
            Assert.True(filter.HasActiveFilter);
            Assert.Equal(13, changes);
            Assert.Equal("contoso.test/Firma/Pracownicy/Sprzedaż", filter.SubtreeText);

            changes = 0;
            filter.ClearCommand.Execute(null);
            Assert.Equal(1, changes);
            Assert.Equal(0, filter.ActiveCount);
            Assert.Equal(new EventQuery().ToQueryString(), filter.ToQuery().ToQueryString());
            Assert.Equal(h.Ad.Localizer[L.Watchdog.FilterAnyLocation], filter.SubtreeText);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Text_IsDebounced300Milliseconds()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            tab.IsVisible = true;
            await tab.Events.Loading;
            var loads = h.Client.EventQueries.Count(q => q.After is null);

            tab.Events.Filter.Text = "Ż";
            tab.Events.Filter.Text = "Żaneta";
            h.Time.Advance(WatchdogEventFilterViewModel.TextDelay - TimeSpan.FromMilliseconds(1));
            await h.Ui.IdleAsync();
            Assert.Equal(loads, h.Client.EventQueries.Count(q => q.After is null));

            h.Time.Advance(TimeSpan.FromMilliseconds(1));
            await WatchdogTestWait.UntilAsync(h, () => h.Client.EventQueries.Count(q => q.After is null) == loads + 1, "one reload");
            await tab.Events.Loading;

            Assert.Equal("Żaneta", h.Client.EventQueries.Last(q => q.After is null).Text);
            Assert.All(tab.Events.Items, r => Assert.Equal("Żaneta Szymańska", r.ObjectName));
            Assert.Equal(2, tab.Events.Items.Count);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Actor_AcceptsSidText_RejectsNames_AndThePickerResolvesTheSid()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var filter = new WatchdogEventFilterViewModel(h.Services);
            Assert.True(filter.CanPick);

            filter.ActorText = "jan.kowalski";
            Assert.Equal(h.Ad.Localizer[L.Watchdog.FilterActorSidOnly], filter.ActorError);
            Assert.Null(filter.ActorSid);

            filter.ActorText = "S-1-5-21-1004336348-1177238915-682003330-1105";
            Assert.Null(filter.ActorError);
            Assert.Equal("S-1-5-21-1004336348-1177238915-682003330-1105", filter.ActorSid);

            filter.ClearActorCommand.Execute(null);
            Assert.Null(filter.ActorSid);
            Assert.Equal(string.Empty, filter.ActorText);

            // Picked in AD: the SID is read through the session; the name is shown, never sent or saved.
            h.Ad.Dialogs.PickedObjects.Enqueue([h.Ad.Company.ZanetaSzymanska]);
            await filter.PickActorCommand.ExecuteAsync(null);
            var sid = h.Ad.Session.GetValues(h.Ad.Company.ZanetaSzymanska, Attr.ObjectSid).Single();
            Assert.Equal(sid, filter.ActorSid);
            Assert.Equal("Żaneta Szymańska", filter.ActorText);
            Assert.Equal(sid, filter.ToQuery().ActorSid);
            Assert.DoesNotContain("Szyma", WatchdogQueryText.ToPreset(filter.ToQuery()), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Subtree_IsPickedThroughTheAdSession()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var filter = new WatchdogEventFilterViewModel(h.Services);
            h.Ad.Dialogs.PickedContainers.Enqueue(h.Ad.Company.SalesOu);
            await filter.BrowseSubtreeCommand.ExecuteAsync(null);

            Assert.Equal(h.Ad.Company.SalesOu, filter.Subtree);
            Assert.True(filter.ClearSubtreeCommand.CanExecute(null));
            filter.ClearSubtreeCommand.Execute(null);
            Assert.Null(filter.Subtree);
        });
    }

    [Fact]
    public async Task Pickers_NeedAnAdSession()
    {
        using var h = await WatchdogHarness.CreateAsync(adSession: false);
        await h.Ui.RunAsync(async () =>
        {
            var filter = new WatchdogEventFilterViewModel(h.Services);
            var raised = new List<string?>();
            filter.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            Assert.False(filter.CanPick);

            h.Ad.Dialogs.PickedContainers.Enqueue(h.Ad.Company.SalesOu);
            await filter.BrowseSubtreeCommand.ExecuteAsync(null);
            Assert.Null(filter.Subtree);

            await h.Ad.Sessions.ConnectAsync(new Core.Directory.ConnectionOptions { Domain = "contoso.test" }, null, TestContext.Current.CancellationToken);
            filter.NotifySessionChanged();
            Assert.True(filter.CanPick);
            Assert.Contains(nameof(WatchdogEventFilterViewModel.CanPick), raised);
        });
    }

    [Fact]
    public async Task ShowObject_ReplacesTheOtherConditions()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(() =>
        {
            var filter = new WatchdogEventFilterViewModel(h.Services);
            filter.UnacknowledgedOnly = true;
            filter.Text = "x";
            var changes = 0;
            filter.Changed += (_, _) => changes++;

            var guid = SamplePrincipals.ZanetaSzymanska.ObjectGuid!.Value;
            filter.ShowObject(guid, "Żaneta Szymańska");

            Assert.Equal(1, changes);
            Assert.True(filter.HasObject);
            Assert.Equal("Żaneta Szymańska", filter.ObjectText);
            Assert.Equal(guid, filter.ToQuery().ObjectGuid);
            Assert.False(filter.UnacknowledgedOnly);
            Assert.Null(filter.ToQuery().Text);
            Assert.Equal(1, filter.ActiveCount);

            filter.ShowObject(guid, null);
            Assert.Equal(guid.ToString("D"), filter.ObjectText);

            filter.ClearObjectCommand.Execute(null);
            Assert.False(filter.HasObject);
            Assert.Equal(string.Empty, filter.ObjectText);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Presets_SaveOnlyTheFilterInTheSettings_AndApplyIt()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync();
            var filter = tab.Events.Filter;
            filter.MinSeverity = filter.Severities.Single(s => s.Value == Severity.High);
            filter.Tags.Single(t => t.Value == EventTags.Privileged).IsChecked = true;
            filter.UnresolvedOnly = true;
            filter.Subtree = SamplePrincipals.ItOu;
            await tab.Events.Loading;

            Assert.False(filter.SavePresetCommand.CanExecute(null));
            filter.PresetName = "  Uprzywilejowane – nieustalone  ";
            await filter.SavePresetCommand.ExecuteAsync(null);

            var preset = Assert.Single(h.Ad.Settings.Current.Watchdog.FilterPresets);
            Assert.Equal("Uprzywilejowane – nieustalone", preset.Name);
            Assert.Equal(
                "minSeverity=High&subtree=" + Uri.EscapeDataString(SamplePrincipals.ItOu) + "&tag=privileged&attributionStatus=Unknown&attributionStatus=Ambiguous",
                preset.Query);
            Assert.DoesNotContain("after=", preset.Query, StringComparison.Ordinal);
            Assert.DoesNotContain("wait=", preset.Query, StringComparison.Ordinal);
            Assert.Equal(string.Empty, filter.PresetName);
            Assert.Single(filter.Presets);

            // Saving under the same name replaces the preset.
            filter.PresetName = "UPRZYWILEJOWANE – NIEUSTALONE";
            await filter.SavePresetCommand.ExecuteAsync(null);
            Assert.Single(h.Ad.Settings.Current.Watchdog.FilterPresets);

            // Applying a preset sets every condition and reloads once.
            filter.ClearCommand.Execute(null);
            await tab.Events.Loading;
            var loads = h.Client.EventQueries.Count(q => q.After is null);
            filter.SelectedPreset = filter.Presets[0];
            await tab.Events.Loading;
            Assert.Equal(loads + 1, h.Client.EventQueries.Count(q => q.After is null));
            Assert.Equal(Severity.High, filter.MinSeverity.Value);
            Assert.True(filter.UnresolvedOnly);
            Assert.Equal(SamplePrincipals.ItOu, filter.Subtree);
            Assert.True(filter.Tags.Single(t => t.Value == EventTags.Privileged).IsChecked);

            await filter.DeletePresetCommand.ExecuteAsync(null);
            Assert.Empty(h.Ad.Settings.Current.Watchdog.FilterPresets);
            Assert.Empty(filter.Presets);
            Assert.Null(filter.SelectedPreset);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Presets_FromHandEditedSettings_AreNormalized()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ad.Settings.UpdateAsync(s =>
        {
            s.Watchdog.FilterPresets.Add(new WatchdogFilterPreset("Blokady", "kind=AccountLockedOut&unknown=1&minSeverity=Bardzo"));
            s.Watchdog.FilterPresets.Add(new WatchdogFilterPreset(" ", "kind=UserCreated"));
            s.Watchdog.FilterPresets.Add(null!);
        }, TestContext.Current.CancellationToken);

        await h.Ui.RunAsync(() =>
        {
            var filter = new WatchdogEventFilterViewModel(h.Services);
            var preset = Assert.Single(filter.Presets);
            filter.SelectedPreset = preset;

            var query = filter.ToQuery();
            Assert.Equal([WatchEventKind.AccountLockedOut], query.Kinds);
            Assert.Null(query.MinSeverity);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("kind=UserCreated&kind=UserCreated&category=Membership&objectKind=Group&minSeverity=Critical&text=Kowalski%20%26%20syn&subtree=OU%3DIT%2CDC%3Dcontoso%2CDC%3Dtest&tag=privileged&attributionStatus=Unknown&from=2026-10-01T00%3A00%3A00.0000000%2B02%3A00&to=2026-10-14T23%3A59%3A59.9999999%2B02%3A00&ruleId=tier0-security&objectGuid=4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8a07&actorSid=S-1-5-21-1-2-3-1105&unacknowledgedOnly=true")]
    public void QueryText_RoundTripsThroughThePresetFormat(string text)
    {
        var parsed = WatchdogQueryText.Parse(text);
        var again = WatchdogQueryText.Parse(WatchdogQueryText.ToPreset(parsed));

        Assert.Equal(WatchdogQueryText.ToPreset(parsed), WatchdogQueryText.ToPreset(again));
        if (text.Length > 0)
        {
            Assert.Equal([WatchEventKind.UserCreated], parsed.Kinds);
            Assert.Equal("Kowalski & syn", parsed.Text);
            Assert.Equal("OU=IT,DC=contoso,DC=test", parsed.Subtree);
            Assert.Equal(Guid.Parse("4a8e1f42-3c51-4f0e-9b7a-2d1c6e5f8a07"), parsed.ObjectGuid);
            Assert.True(parsed.UnacknowledgedOnly);
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2)), parsed.From);
        }
    }

    [Fact]
    public void QueryText_DropsPagingKeys_AndIgnoresInvalidValues()
    {
        var query = new EventQuery { After = 5, Before = 9, WaitSeconds = 25, Limit = 500, MinSeverity = Severity.High };
        Assert.Equal("minSeverity=High", WatchdogQueryText.ToPreset(query));

        var parsed = WatchdogQueryText.Parse("?after=5&before=9&wait=25&limit=7&kind=998&kind=Nonsense&objectGuid=xyz&from=wczoraj&=x&tag=&minSeverity=4");
        Assert.Null(parsed.After);
        Assert.Null(parsed.Before);
        Assert.Equal(0, parsed.WaitSeconds);
        Assert.Equal(100, parsed.Limit);
        Assert.Empty(parsed.Kinds);
        Assert.Null(parsed.ObjectGuid);
        Assert.Null(parsed.From);
        Assert.Empty(parsed.Tags);
        Assert.Equal(Severity.Critical, parsed.MinSeverity);
    }

    [Fact]
    public async Task Catalog_OfTheService_GroupsKindsByCategory_AndKeepsTheSelection()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(() =>
        {
            var filter = new WatchdogEventFilterViewModel(h.Services);
            Assert.DoesNotContain(filter.KindGroups.SelectMany(g => g.Options), o => o.Value == WatchEventKind.Test);
            Assert.Equal(Enum.GetValues<EventCategory>().Length, filter.KindGroups.Count);
            filter.KindGroups.SelectMany(g => g.Options).Single(o => o.Value == WatchEventKind.UserCreated).IsChecked = true;

            filter.SetCatalog([.. EventKindCatalog.All.Where(k => k.Category is EventCategory.Lifecycle or EventCategory.Account)]);

            Assert.Equal(2, filter.KindGroups.Count);
            Assert.Equal(h.Ad.Localizer.CategoryName(EventCategory.Lifecycle), filter.KindGroups[0].Title);
            Assert.Equal([WatchEventKind.UserCreated], filter.ToQuery().Kinds);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Connect_TakesTheCatalogOfTheService()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.Catalog = [.. EventKindCatalog.All.Where(k => k.Category == EventCategory.Health)];
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            Assert.Single(tab.Events.Filter.KindGroups);
            Assert.Equal(1, h.Client.Calls.Count(nameof(IWatchdogClient.GetCatalogAsync)));
        });
    }
}
