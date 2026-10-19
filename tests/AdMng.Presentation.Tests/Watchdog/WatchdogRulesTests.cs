using AdMng.Presentation.Localization;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>
/// The Rules sub-tab: rule rows, enable toggle, new / duplicate / delete / reset, exclusion suggestions, rollout mode with
/// "would send N", health warnings, and the Viewer's redacted read-only view.
/// </summary>
public sealed class WatchdogRulesTests
{
    private const int LockoutsIndex = 12;
    private const int HealthIndex = 23;
    private const int CustomIndex = 24;

    [Fact]
    public async Task Rows_SummarizeEveryRule()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var rules = tab.Rules;
            var localizer = h.Ad.Localizer;

            Assert.Equal(25, rules.Rules.Count);
            Assert.Same(rules.Rules[0], rules.SelectedRule);
            Assert.NotNull(rules.Editor);

            var lockouts = rules.Rules[LockoutsIndex];
            Assert.Equal("lockouts", lockouts.Id);
            Assert.Equal("Blokady kont", lockouts.Name);
            Assert.Equal(localizer[L.Watchdog.RulesBuiltIn], lockouts.BadgeText);
            Assert.Equal(localizer.KindName(WatchEventKind.AccountLockedOut), lockouts.KindsText);
            Assert.Equal(localizer.Format(L.Watchdog.RulesImmediate, "helpdesk") + " · " + localizer[L.Watchdog.RulesDigest], lockouts.DeliveryText);
            Assert.Equal(localizer.Format(L.Watchdog.RulesExclusionsCount, 1), lockouts.ExclusionsText);
            Assert.True(lockouts.IsEnabled);
            Assert.True(lockouts.CanToggle);
            Assert.True(lockouts.IsBuiltIn);

            var health = rules.Rules[HealthIndex];
            Assert.Equal(localizer[L.Watchdog.RulesLocked], health.BadgeText);
            Assert.False(health.CanToggle);
            Assert.Contains(localizer[L.Watchdog.RulesAllChannels], health.DeliveryText, StringComparison.Ordinal);

            var custom = rules.Rules[CustomIndex];
            Assert.Equal(string.Empty, custom.BadgeText);
            Assert.Equal(localizer.Format(L.Watchdog.RulesKindsCount, 4, localizer.KindName(WatchEventKind.UserCreated)), custom.KindsText);
            Assert.Equal(localizer.Format(L.Watchdog.RulesImmediate, "helpdesk, teams-it") + " · " + localizer[L.Watchdog.RulesDigest], custom.DeliveryText);
            Assert.Equal(string.Empty, custom.ExclusionsText);

            var security = rules.Rules[1];
            Assert.Equal(localizer.Format(L.Watchdog.FilterMinSeverity, localizer.SeverityName(Severity.High)), security.SeverityText);
            Assert.Equal(localizer[L.Watchdog.RulesDigest], rules.Rules[2].DeliveryText);
            Assert.Equal(
                localizer.Format(L.Watchdog.RulesSeverityOverride, localizer.SeverityName(Severity.Medium)),
                rules.Rules[17].SeverityText);

            // Health warning about the rule (actor conditions that cannot work without attribution).
            Assert.StartsWith(localizer.HealthText("attribution.actorConditionsUnreliable"), security.WarningText, StringComparison.Ordinal);
            Assert.EndsWith("(KeyCredentialChanged)", security.WarningText, StringComparison.Ordinal);
            Assert.Equal(string.Empty, lockouts.WarningText);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Toggle_EnablesAndDisablesTheRuleInTheWorkingCopy()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var rules = tab.Rules;
            rules.SelectedRule = rules.Rules[LockoutsIndex];

            rules.Rules[LockoutsIndex].IsEnabled = false;

            Assert.True(tab.Config.IsDirty);
            Assert.False(tab.Config.Working!.Rules[LockoutsIndex].Enabled);
            Assert.False(rules.Editor!.IsEnabled);

            rules.Rules[LockoutsIndex].IsEnabled = true;
            Assert.False(tab.Config.IsDirty);
        });
    }

    [Fact]
    public async Task NewDuplicateDelete_ChangeTheRuleList()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var rules = tab.Rules;
            var localizer = h.Ad.Localizer;

            rules.NewRuleCommand.Execute(null);
            Assert.Equal(26, rules.Rules.Count);
            var created = rules.SelectedRule!;
            Assert.Equal("nowa-regula", created.Id);
            Assert.Equal(localizer[L.Watchdog.RulesNewName], created.Name);
            Assert.False(created.Rule.Delivery.Immediate);
            Assert.True(created.Rule.Delivery.IncludeInDigest);
            Assert.Equal(25, rules.Editor!.Index);
            Assert.True(rules.DeleteRuleCommand.CanExecute(null));
            Assert.False(rules.ResetRuleCommand.CanExecute(null));

            rules.NewRuleCommand.Execute(null);
            Assert.Equal("nowa-regula-2", rules.SelectedRule!.Id);

            rules.SelectedRule = rules.Rules[1];
            Assert.False(rules.DeleteRuleCommand.CanExecute(null));
            rules.DuplicateRuleCommand.Execute(null);
            var copy = rules.SelectedRule!;
            Assert.Equal("tier0-security-kopia", copy.Id);
            Assert.Equal(localizer.Format(L.Watchdog.RulesCopyName, "Atrybuty krytyczne dla bezpieczeństwa"), copy.Name);
            Assert.False(copy.IsBuiltIn);
            Assert.False(copy.IsLocked);
            Assert.Single(copy.Rule.Exclusions);

            // Delete asks first; "no" keeps the rule.
            h.Ad.Dialogs.ConfirmAnswers.Enqueue(false);
            await rules.DeleteRuleCommand.ExecuteAsync(null);
            Assert.Equal(28, rules.Rules.Count);

            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await rules.DeleteRuleCommand.ExecuteAsync(null);
            Assert.Equal(27, rules.Rules.Count);
            Assert.DoesNotContain(tab.Config.Working!.Rules, r => r.Id == "tier0-security-kopia");
            Assert.Equal("nowa-regula-2", rules.SelectedRule!.Id);
            Assert.Contains(h.Ad.Dialogs.Shown, s => s.Contains("tier0-security", StringComparison.Ordinal) || s.Contains(copy.Name, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task ResetToDefault_ReplacesABuiltInRuleWithItsShippedVersion()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var rules = tab.Rules;
            rules.SelectedRule = rules.Rules[LockoutsIndex];
            Assert.True(rules.ResetRuleCommand.CanExecute(null));

            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await rules.ResetRuleCommand.ExecuteAsync(null);

            var reset = tab.Config.Working!.Rules[LockoutsIndex];
            Assert.Equal("lockouts", reset.Id);
            Assert.Empty(reset.Exclusions);
            Assert.True(tab.Config.IsDirty);
            Assert.Same(rules.Rules[LockoutsIndex], rules.SelectedRule);
            Assert.Equal(string.Empty, rules.SelectedRule!.ExclusionsText);

            // A built-in rule the service no longer ships.
            h.Client.Defaults = h.Client.Defaults with { Rules = [] };
            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await rules.ResetRuleCommand.ExecuteAsync(null);
            Assert.Contains(h.Ad.Dialogs.Errors, e => e.Message == h.Ad.Localizer[L.Watchdog.RulesNoDefault]);
        });
    }

    [Fact]
    public async Task Suggestion_AddsAPrefilledExclusionToItsRule()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var rules = tab.Rules;
            var localizer = h.Ad.Localizer;

            Assert.Equal(2, rules.Suggestions.Count);
            var suggestion = rules.Suggestions[0];
            Assert.Equal(
                localizer.Format(L.Watchdog.SuggestionText, localizer.SuggestionText("entraConnectSync"), @"CONTOSO\MSOL_4f2a9c1b7e30", "tier0-security"),
                suggestion.Text);

            rules.AddSuggestedExclusionCommand.Execute(suggestion);

            Assert.Equal("tier0-security", rules.SelectedRule!.Id);
            var editor = rules.Editor!;
            var exclusion = editor.SelectedExclusion!;
            Assert.Equal(2, editor.Exclusions.Count);
            Assert.Equal(SamplePrincipals.DomainSid + "-1181", exclusion.Actor);
            Assert.Equal(@"CONTOSO\MSOL_4f2a9c1b7e30", exclusion.ActorName);
            Assert.Equal(localizer.SuggestionText("entraConnectSync"), exclusion.Reason);
            Assert.Equal(@"CONTOSO\jan.kowalski", exclusion.CreatedBy);
            Assert.Equal(h.Time.GetUtcNow(), exclusion.CreatedAt);
            Assert.True(exclusion.IsValid);

            var saved = tab.Config.Working!.Rules[1].Exclusions;
            Assert.Equal(2, saved.Count);
            Assert.Equal(SamplePrincipals.DomainSid + "-1181", saved[1].Actor);
            Assert.Equal(localizer.Format(L.Watchdog.RulesExclusionsCount, 2), rules.SelectedRule.ExclusionsText);
        });
    }

    [Fact]
    public async Task Rollout_ShowsWouldSendCounts_AndCanBeEndedByAnAdmin()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.Status = SampleStatus.Create(new DateTimeOffset(2026, 10, 21, 7, 0, 0, TimeSpan.Zero));
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var rules = tab.Rules;
            await rules.LoadRolloutCountsAsync();

            Assert.True(rules.IsRolloutActive);
            var preview = h.Client.RulePreviews.First(p => p.Rule.Id == "lockouts");
            Assert.Equal(3, preview.Days);
            Assert.Equal(rules.Rules.Count(r => r.Rule.Enabled), h.Client.RulePreviews.Select(p => p.Rule.Id).Distinct().Count());
            var lockouts = rules.Rules[LockoutsIndex];
            Assert.Matches(@"^would send \d+ messages$", lockouts.RolloutText);
            Assert.Equal(string.Empty, rules.Rules[2].RolloutText);

            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await rules.EndRolloutCommand.ExecuteAsync(null);

            var sent = Assert.Single(h.Client.SavedEnvelopes);
            Assert.True(sent.Configuration.General.RolloutEnded);
            Assert.False(rules.IsRolloutActive);
            Assert.All(rules.Rules, r => Assert.Equal(string.Empty, r.RolloutText));
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task EndingRollout_SavesOnlyTheRolloutFlag_AndKeepsOtherUnsavedEditsUnsaved()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.Status = SampleStatus.Create(new DateTimeOffset(2026, 10, 21, 7, 0, 0, TimeSpan.Zero));
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var rules = tab.Rules;
            var version = tab.Config.Envelope!.Version;

            // An experiment the Admin has not saved: the lockout rule switched off, the sender renamed.
            rules.Rules[LockoutsIndex].IsEnabled = false;
            tab.Config.Update(c => c with { Smtp = c.Smtp with { From = "Strażnik AD <straznik@contoso.com>" } });
            Assert.True(tab.Config.IsDirty);

            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await rules.EndRolloutCommand.ExecuteAsync(null);

            // Only the rollout flag reached the service – on top of the configuration as last saved.
            var sent = Assert.Single(h.Client.SavedEnvelopes);
            Assert.Equal(version, sent.Version);
            Assert.True(sent.Configuration.General.RolloutEnded);
            Assert.True(sent.Configuration.Rules[LockoutsIndex].Enabled);
            Assert.NotEqual("Strażnik AD <straznik@contoso.com>", sent.Configuration.Smtp.From);
            Assert.False(rules.IsRolloutActive);

            // The experiment is still there, unsaved, on top of the new version (which also ended rollout).
            Assert.Equal(version + 1, tab.Config.Envelope!.Version);
            Assert.True(tab.Config.IsDirty);
            Assert.False(tab.Config.Working!.Rules[LockoutsIndex].Enabled);
            Assert.Equal("Strażnik AD <straznik@contoso.com>", tab.Config.Working.Smtp.From);
            Assert.True(tab.Config.Working.General.RolloutEnded);
            Assert.False(rules.Rules[LockoutsIndex].IsEnabled);

            // Saving it later is a normal save of the new version (no conflict with the rollout save).
            Assert.True(await tab.Config.SaveAsync());
            Assert.Equal(2, h.Client.SavedEnvelopes.Count);
            Assert.Equal(version + 1, h.Client.SavedEnvelopes[1].Version);
            Assert.False(h.Client.SavedEnvelopes[1].Configuration.Rules[LockoutsIndex].Enabled);
            Assert.False(tab.Config.IsDirty);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task EndingRollout_WithoutOtherEdits_LeavesTheConfigurationClean()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.Status = SampleStatus.Create(new DateTimeOffset(2026, 10, 21, 7, 0, 0, TimeSpan.Zero));
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);

            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await tab.Rules.EndRolloutCommand.ExecuteAsync(null);

            Assert.True(Assert.Single(h.Client.SavedEnvelopes).Configuration.General.RolloutEnded);
            Assert.False(tab.Config.IsDirty);
            Assert.True(tab.Config.Working!.General.RolloutEnded);
        });
        h.AssertNoUiErrors();
    }

    [Fact]
    public async Task Rollout_WouldSendCounts_OnlyForAdmins()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.IsAdmin = false;
        h.Client.Status = SampleStatus.Create(new DateTimeOffset(2026, 10, 21, 7, 0, 0, TimeSpan.Zero));
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            await tab.Rules.LoadRolloutCountsAsync();

            Assert.Empty(h.Client.RulePreviews);
            Assert.True(tab.Rules.IsRolloutActive);
            Assert.False(tab.Rules.EndRolloutCommand.CanExecute(null));
        });
    }

    [Fact]
    public async Task Viewer_SeesTheRedactedRulesReadOnly_WithExclusionCounts()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.IsAdmin = false;
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var rules = tab.Rules;

            Assert.All(rules.Rules, r => Assert.False(r.CanEdit));
            Assert.All(rules.Rules, r => Assert.False(r.CanToggle));
            Assert.False(rules.NewRuleCommand.CanExecute(null));
            Assert.False(rules.DuplicateRuleCommand.CanExecute(null));
            Assert.False(rules.AddSuggestedExclusionCommand.CanExecute(rules.Suggestions[0]));

            rules.SelectedRule = rules.Rules[LockoutsIndex];
            Assert.Equal(h.Ad.Localizer.Format(L.Watchdog.RulesExclusionsCount, 1), rules.SelectedRule.ExclusionsText);
            var editor = rules.Editor!;
            Assert.False(editor.CanEdit);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.RequiresAdmin], editor.ReadOnlyReason);
            var exclusion = Assert.Single(editor.Exclusions);
            Assert.True(exclusion.IsRedacted);
            Assert.False(exclusion.CanEdit);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.ExclusionRedacted], exclusion.Summary);
            Assert.False(editor.RemoveExclusionCommand.CanExecute(null));
            Assert.False(editor.PreviewCommand.CanExecute(null));

            // Edits of a Viewer never reach the configuration.
            editor.Name = "Zmiana przeglądającego";
            Assert.False(tab.Config.IsDirty);
        });
    }

    [Fact]
    public async Task ValidationIssues_MarkTheRuleRows()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.Validator = _ => [new ConfigIssue { Path = $"rules[{CustomIndex}].delivery.channelIds[0]", Code = "unknownReference", Severity = IssueSeverity.Error, Message = "Unknown." }];
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var rules = tab.Rules;
            rules.SelectedRule = rules.Rules[CustomIndex];

            await tab.Config.ValidateAsync();

            Assert.True(rules.Rules[CustomIndex].HasIssues);
            Assert.False(rules.Rules[0].HasIssues);
            Assert.Equal(h.Ad.Localizer[WatchdogDisplay.ValidationKey("unknownReference")], rules.Editor!.Errors["delivery.channelIds"]);
            Assert.Equal(rules.Editor.Errors["delivery"], rules.Editor.Errors["delivery.channelIds[0]"]);
            Assert.Null(rules.Editor.Errors["name"]);
            Assert.True(rules.Editor.Errors.HasErrors);
        });
    }

    [Fact]
    public async Task Rebuild_AfterSave_KeepsTheSelectedRule()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var tab = await h.OpenAsync(visible: false);
            var rules = tab.Rules;
            rules.SelectedRule = rules.Rules[CustomIndex];
            rules.Editor!.Name = "Oddział Kraków – wszystkie zmiany";
            Assert.Equal("Oddział Kraków – wszystkie zmiany", rules.Rules[CustomIndex].Name);

            Assert.True(await tab.Config.SaveAsync());

            Assert.Equal("oddzial-krakow", rules.SelectedRule!.Id);
            Assert.Equal("Oddział Kraków – wszystkie zmiany", rules.SelectedRule.Name);
            Assert.Equal(CustomIndex, rules.Editor!.Index);
            Assert.Equal(1, h.Client.Calls.Count(nameof(IWatchdogClient.SaveConfigAsync)));
        });
    }
}
