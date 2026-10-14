using System.Text.Json;
using AdMng.Core.ActiveDirectory;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>The inline rule editor: every RuleSettings field, local checks, pickers, exclusions and the preview.</summary>
public sealed class WatchdogRuleEditorTests
{
    private const int CustomIndex = 24;

    [Fact]
    public async Task EveryRule_RoundTripsThroughTheEditorUnchanged()
    {
        using var h = await WatchdogHarness.CreateAsync(zone: TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));
        await h.Ui.RunAsync(async () =>
        {
            var session = new WatchdogConfigSession(h.Services) { IsAdmin = true };
            await session.LoadAsync();
            for (var i = 0; i < session.Working!.Rules.Count; i++)
            {
                var editor = new WatchdogRuleEditorViewModel(h.Services, session, i);
                Assert.Equal(Json(session.Working.Rules[i]), Json(editor.Build()));
            }

            Assert.False(session.IsDirty);
        });
    }

    [Fact]
    public async Task Load_ShowsEveryField()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var editor = await EditorAsync(h, CustomIndex);

            Assert.Equal("oddzial-krakow", editor.Id);
            Assert.Equal("Oddział Kraków – zmiany kont", editor.Name);
            Assert.StartsWith("Zmiany kont i grup", editor.Description, StringComparison.Ordinal);
            Assert.True(editor.IsEnabled);
            Assert.True(editor.CanEdit);
            Assert.True(editor.CanEditId);
            Assert.True(editor.CanEditKinds);
            Assert.True(editor.CanDisable);
            Assert.Equal(
                new[] { WatchEventKind.UserCreated, WatchEventKind.AccountDisabled, WatchEventKind.GroupMemberAdded, WatchEventKind.GroupMemberRemoved }.Order(),
                editor.KindGroups.SelectMany(g => g.Options).Where(o => o.IsChecked).Select(o => o.Value).Order());
            Assert.Equal([WatchObjectKind.User, WatchObjectKind.Group], editor.ObjectKinds.Where(o => o.IsChecked).Select(o => o.Value));
            Assert.Equal("OU=Kraków,OU=Firma,DC=contoso,DC=test", editor.IncludeSubtrees);
            Assert.True(editor.Immediate);
            Assert.Equal(["helpdesk"], editor.RecipientGroups.Where(g => g.IsChecked).Select(g => g.Value));
            Assert.Equal(["Administratorzy IT", "Helpdesk"], editor.RecipientGroups.Select(g => g.Text));
            Assert.Equal(["teams-it"], editor.Channels.Where(c => c.IsChecked).Select(c => c.Value));
            Assert.Equal(DeliveryHours.BusinessHours, editor.Hours.Value);
            Assert.Null(editor.MinSeverity.Value);
            Assert.Null(editor.SeverityOverride.Value);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.RuleNoSeverity], editor.MinSeverity.Text);
            Assert.Equal(string.Empty, editor.AggregationMinutes);
            Assert.Empty(editor.Exclusions);
            Assert.Null(editor.ScopeError);
            Assert.Null(editor.ActorsError);
            Assert.Null(editor.NumbersError);

            var lockouts = await EditorAsync(h, 12);
            Assert.Equal("5", lockouts.AggregationMinutes);
            Assert.Equal("10", lockouts.MaxMessagesPerHour);
            Assert.Equal("60", lockouts.RepeatSuppressionMinutes);
            Assert.False(lockouts.CanEditId);
        });
    }

    [Fact]
    public async Task Edits_ReachTheWorkingConfiguration()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var session = new WatchdogConfigSession(h.Services) { IsAdmin = true };
            await session.LoadAsync();
            var editor = new WatchdogRuleEditorViewModel(h.Services, session, CustomIndex);
            var edited = new List<RuleSettings>();
            editor.RuleChanged += (_, r) => edited.Add(r);

            editor.Name = "  Oddział Kraków  ";
            editor.Description = " ";
            editor.KindGroups.SelectMany(g => g.Options).Single(o => o.Value == WatchEventKind.AccountLockedOut).IsChecked = true;
            editor.KindGroups.SelectMany(g => g.Options).Single(o => o.Value == WatchEventKind.UserCreated).IsChecked = false;
            editor.ObjectKinds.Single(o => o.Value == WatchObjectKind.Computer).IsChecked = true;
            editor.ExcludeSubtrees = "OU=Kraków-Serwis,OU=Firma,DC=contoso,DC=test\r\n\r\n";
            editor.Attributes = "department, title;telephoneNumber";
            editor.RequiredTags.Single(t => t.Value == EventTags.Privileged).IsChecked = true;
            editor.AnyTags.Single(t => t.Value == EventTags.Server).IsChecked = true;
            editor.Groups = editor.GroupFilters.Single(g => g.Value == GroupFilter.Explicit);
            editor.GroupDns = "CN=GG_Krakow,OU=Grupy,OU=Firma,DC=contoso,DC=test";
            editor.ExcludeActors = SamplePrincipals.HrSync.Sid!;
            editor.ExcludeSelfChanges = true;
            editor.MinSeverity = editor.Severities.Single(s => s.Value == Severity.Low);
            editor.SeverityOverride = editor.Severities.Single(s => s.Value == Severity.High);
            editor.RecipientGroups.Single(g => g.Value == "it-admins").IsChecked = true;
            editor.Channels.Single(c => c.Value == "teams-it").IsChecked = false;
            editor.AllEnabledChannels = true;
            editor.IncludeInDigest = false;
            editor.Hours = editor.HoursOptions.Single(o => o.Value == DeliveryHours.Always);
            editor.AggregationMinutes = "15";
            editor.MaxMessagesPerHour = "20";
            editor.RepeatSuppressionMinutes = "120";
            editor.NotifyManager = true;
            editor.IsEnabled = false;

            var rule = session.Working!.Rules[CustomIndex];
            Assert.Equal("Oddział Kraków", rule.Name);
            Assert.Null(rule.Description);
            Assert.False(rule.Enabled);
            Assert.Equal([WatchEventKind.AccountDisabled, WatchEventKind.GroupMemberAdded, WatchEventKind.GroupMemberRemoved, WatchEventKind.AccountLockedOut], rule.Kinds);
            Assert.Equal([WatchObjectKind.User, WatchObjectKind.Group, WatchObjectKind.Computer], rule.ObjectKinds);
            Assert.Equal(["OU=Kraków-Serwis,OU=Firma,DC=contoso,DC=test"], rule.Scope.ExcludeSubtrees);
            Assert.Equal(["department", "title", "telephoneNumber"], rule.Attributes);
            Assert.Equal([EventTags.Privileged], rule.RequiredTags);
            Assert.Equal([EventTags.Server], rule.AnyTags);
            Assert.Equal(GroupFilter.Explicit, rule.Groups);
            Assert.Equal(["CN=GG_Krakow,OU=Grupy,OU=Firma,DC=contoso,DC=test"], rule.GroupDns);
            Assert.Equal([SamplePrincipals.HrSync.Sid!], rule.Actors.ExcludeActors);
            Assert.True(rule.Actors.ExcludeSelfChanges);
            Assert.Equal(Severity.Low, rule.MinSeverity);
            Assert.Equal(Severity.High, rule.Severity);
            Assert.Equal(["helpdesk", "it-admins"], rule.Delivery.RecipientGroupIds);
            Assert.Empty(rule.Delivery.ChannelIds);
            Assert.True(rule.Delivery.AllEnabledChannels);
            Assert.False(rule.Delivery.IncludeInDigest);
            Assert.Equal(DeliveryHours.Always, rule.Delivery.Hours);
            Assert.Equal(TimeSpan.FromMinutes(15), rule.Delivery.AggregationWindow);
            Assert.Equal(20, rule.Delivery.MaxMessagesPerHour);
            Assert.Equal(TimeSpan.FromHours(2), rule.Delivery.RepeatSuppression);
            Assert.True(rule.Delivery.NotifyManager);
            Assert.True(session.IsDirty);
            Assert.Same(rule, edited[^1]);
            Assert.Null(editor.ScopeError);
            Assert.Null(editor.ActorsError);
            Assert.Null(editor.NumbersError);
        });
    }

    [Fact]
    public async Task LocalChecks_FlagInvalidDnsSidsAndNumbers()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var editor = await EditorAsync(h, CustomIndex);
            var localizer = h.Ad.Localizer;

            editor.IncludeSubtrees = "OU=Kraków,{domain}\nto nie jest DN";
            Assert.Equal(localizer[WatchdogDisplay.ValidationKey("invalidDn")] + " to nie jest DN", editor.ScopeError);

            editor.ExcludeActors = "S-1-5-21-1-2-3-500\njan.kowalski";
            Assert.Equal(localizer[WatchdogDisplay.ValidationKey("invalidSid")] + " jan.kowalski", editor.ActorsError);

            editor.AggregationMinutes = "pięć";
            Assert.Equal(localizer[WatchdogDisplay.ValidationKey("range")], editor.NumbersError);
            Assert.Null(editor.Build().Delivery.AggregationWindow);

            editor.IncludeSubtrees = string.Empty;
            editor.ExcludeActors = string.Empty;
            editor.AggregationMinutes = string.Empty;
            Assert.Null(editor.ScopeError);
            Assert.Null(editor.ActorsError);
            Assert.Null(editor.NumbersError);
        });
    }

    [Fact]
    public async Task Pickers_AddContainersAndActorSidsFromTheAdSession()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var editor = await EditorAsync(h, CustomIndex);
            Assert.True(editor.CanPick);

            h.Ad.Dialogs.PickedContainers.Enqueue(h.Ad.Company.SalesOu);
            await editor.BrowseIncludeSubtreeCommand.ExecuteAsync(null);
            Assert.Equal(["OU=Kraków,OU=Firma,DC=contoso,DC=test", h.Ad.Company.SalesOu], WatchdogDn.Lines(editor.IncludeSubtrees));

            h.Ad.Dialogs.PickedContainers.Enqueue(h.Ad.Company.ComputersOu);
            await editor.BrowseExcludeSubtreeCommand.ExecuteAsync(null);
            Assert.Equal(h.Ad.Company.ComputersOu, editor.ExcludeSubtrees);

            h.Ad.Dialogs.PickedObjects.Enqueue([h.Ad.Company.JanKowalski, h.Ad.Company.GroupIt]);
            await editor.PickExcludedActorCommand.ExecuteAsync(null);
            Assert.Equal(
                [h.Ad.Value(h.Ad.Company.JanKowalski, Attr.ObjectSid)!, h.Ad.Value(h.Ad.Company.GroupIt, Attr.ObjectSid)!],
                WatchdogDn.Lines(editor.ExcludeActors));
            Assert.DoesNotContain("Kowalski", editor.ExcludeActors, StringComparison.Ordinal);

            // The same SID is not added twice.
            h.Ad.Dialogs.PickedObjects.Enqueue([h.Ad.Company.JanKowalski]);
            await editor.PickExcludedActorCommand.ExecuteAsync(null);
            Assert.Equal(2, WatchdogDn.Lines(editor.ExcludeActors).Count);
        });
    }

    [Fact]
    public async Task LockedHealthRule_KeepsItsKinds_AndCannotBeDisabled()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var session = new WatchdogConfigSession(h.Services) { IsAdmin = true };
            await session.LoadAsync();
            var health = session.Working!.Rules[23];
            var editor = new WatchdogRuleEditorViewModel(h.Services, session, 23);

            Assert.False(editor.CanEditKinds);
            Assert.False(editor.CanDisable);
            editor.KindGroups.SelectMany(g => g.Options).First(o => o.IsChecked).IsChecked = false;
            Assert.Equal(health.Kinds, session.Working.Rules[23].Kinds);
        });
    }

    [Fact]
    public async Task Preview_ShowsWhatTheUnsavedRuleWouldHaveMatched()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var editor = await EditorAsync(h, 12);
            var localizer = h.Ad.Localizer;
            editor.Name = "Blokady – podgląd";
            Assert.Equal([1, 7, 14, 30, 90], editor.PreviewDayOptions);
            editor.PreviewDays = 30;

            await editor.PreviewCommand.ExecuteAsync(null);

            var request = Assert.Single(h.Client.RulePreviews);
            Assert.Equal(30, request.Days);
            Assert.Equal("Blokady – podgląd", request.Rule.Name);
            Assert.Equal(localizer.Format(L.Watchdog.RulePreviewSummary, 2, 2), editor.PreviewText);
            Assert.Equal(2, editor.PreviewDaysList.Count);
            Assert.Equal(localizer.Format(L.Watchdog.RulePreviewDay, new DateOnly(2026, 10, 13).ToString("d", localizer.Culture), 1, 1), editor.PreviewDaysList[0]);
            Assert.Equal([30L, 11L], editor.PreviewSamples.Select(s => s.Id));
            Assert.Null(editor.PreviewError);
            Assert.False(editor.IsPreviewing);

            h.Client.FailNext(nameof(IWatchdogClient.PreviewRuleAsync), new WatchdogClientException(WatchdogClientErrorKind.ServerError, "x") { StatusCode = 500 });
            await editor.PreviewCommand.ExecuteAsync(null);
            Assert.Equal(localizer.Format(L.Watchdog.ErrorServerStatus, 500), editor.PreviewError);
        });
    }

    [Fact]
    public async Task Exclusions_AreAddedEditedAndRemoved_WithReasonAndSidChecks()
    {
        using var h = await WatchdogHarness.CreateAsync(zone: TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));
        await h.Ui.RunAsync(async () =>
        {
            var session = new WatchdogConfigSession(h.Services) { IsAdmin = true };
            await session.LoadAsync();
            var editor = new WatchdogRuleEditorViewModel(h.Services, session, 12) { Account = @"CONTOSO\jan.kowalski" };
            var localizer = h.Ad.Localizer;

            var kiosk = Assert.Single(editor.Exclusions);
            Assert.Equal("Konto kiosku w recepcji – blokady zgłasza ochrona.", kiosk.Summary);
            Assert.Equal(new DateTime(2027, 1, 1), kiosk.ExpiresAt);
            Assert.Equal(localizer.Format(L.Watchdog.ExclusionCreated, localizer.FormatTime(new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero), h.Services.TimeZone), @"CONTOSO\anna.nowak"), kiosk.CreatedText);

            editor.NewExclusionCommand.Execute(null);
            var added = editor.SelectedExclusion!;
            Assert.Equal("wyjatek-1", added.Id);
            Assert.Equal(@"CONTOSO\jan.kowalski", added.CreatedBy);
            Assert.Equal(h.Time.GetUtcNow(), added.CreatedAt);
            Assert.Equal(localizer[WatchdogDisplay.ValidationKey("exclusionReason")], added.ReasonError);
            Assert.False(added.IsValid);
            Assert.Equal(localizer.Format(L.Watchdog.RulesExclusionsCount, 2), editor.ExclusionsText);

            added.Reason = "Konto testowe działu IT";
            added.ObjectGuidText = "nie-guid";
            Assert.Equal(localizer[L.Watchdog.ExclusionInvalidGuid], added.ObjectError);
            added.Actor = "anna.nowak";
            Assert.Equal(localizer[WatchdogDisplay.ValidationKey("invalidSid")], added.ActorError);
            added.SubtreeDn = "zły DN";
            Assert.Equal(localizer[WatchdogDisplay.ValidationKey("invalidDn")], added.SubtreeError);

            // Picked through the AD session: the GUID and the SID are stored, names are shown only.
            h.Ad.Dialogs.PickedObjects.Enqueue([h.Ad.Company.ZanetaSzymanska]);
            await added.PickObjectCommand.ExecuteAsync(null);
            h.Ad.Dialogs.PickedObjects.Enqueue([h.Ad.Company.JanKowalski]);
            await added.PickActorCommand.ExecuteAsync(null);
            h.Ad.Dialogs.PickedContainers.Enqueue(h.Ad.Company.SalesOu);
            await added.BrowseSubtreeCommand.ExecuteAsync(null);
            added.Attribute = " lockoutTime ";
            added.ExpiresAt = new DateTime(2026, 11, 30);

            Assert.True(added.IsValid);
            Assert.Equal("Żaneta Szymańska", added.ObjectName);
            Assert.Equal("Jan Kowalski", added.ActorName);
            var stored = session.Working!.Rules[12].Exclusions[1];
            var zaneta = await h.Ad.Session.GetAsync(h.Ad.Company.ZanetaSzymanska, [Attr.ObjectGuid], TestContext.Current.CancellationToken);
            Assert.Equal(zaneta!.ObjectGuid, stored.ObjectGuid);
            Assert.Equal(h.Ad.Value(h.Ad.Company.JanKowalski, Attr.ObjectSid), stored.Actor);
            Assert.Equal(h.Ad.Company.SalesOu, stored.SubtreeDn);
            Assert.Equal("lockoutTime", stored.Attribute);
            Assert.Equal("Konto testowe działu IT", stored.Reason);
            Assert.Equal(new DateTimeOffset(2026, 11, 30, 23, 0, 0, TimeSpan.Zero).AddTicks(-1), stored.ExpiresAt);
            Assert.Equal(kiosk.ToExclusion().ExpiresAt, session.Working.Rules[12].Exclusions[0].ExpiresAt);

            Assert.True(editor.RemoveExclusionCommand.CanExecute(null));
            editor.RemoveExclusionCommand.Execute(null);
            Assert.Single(session.Working.Rules[12].Exclusions);
            Assert.Same(kiosk, editor.SelectedExclusion);
        });
    }

    [Fact]
    public async Task MissingReferences_StayVisible()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.StoredConfig = h.Client.StoredConfig with
        {
            Configuration = h.Client.StoredConfig.Configuration with
            {
                Rules = [.. h.Client.StoredConfig.Configuration.Rules.Select((r, i) => i == CustomIndex
                    ? r with { Delivery = r.Delivery with { RecipientGroupIds = ["helpdesk", "usunieta-grupa"] } }
                    : r)],
            },
        };
        await h.Ui.RunAsync(async () =>
        {
            var editor = await EditorAsync(h, CustomIndex);
            var missing = editor.RecipientGroups.Single(g => g.Value == "usunieta-grupa");
            Assert.True(missing.IsChecked);
            Assert.Equal("usunieta-grupa", missing.Text);
        });
    }

    private static async Task<WatchdogRuleEditorViewModel> EditorAsync(WatchdogHarness h, int index)
    {
        var session = new WatchdogConfigSession(h.Services) { IsAdmin = true };
        await session.LoadAsync();
        return new WatchdogRuleEditorViewModel(h.Services, session, index) { Account = @"CONTOSO\jan.kowalski" };
    }

    private static string Json(RuleSettings rule) => JsonSerializer.Serialize(rule, WatchdogJson.Options);
}
