using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>The configuration shared by Rules, Channels and Digest: dirty tracking, validation, save, conflict, revert.</summary>
public sealed class WatchdogConfigSessionTests
{
    [Fact]
    public async Task Load_GivesTheAdminAnEditableCopy()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var session = await LoadAsync(h);

            Assert.True(session.IsLoaded);
            Assert.False(session.IsRedacted);
            Assert.True(session.CanEdit);
            Assert.False(session.IsDirty);
            Assert.Equal(7, session.Envelope!.Version);
            Assert.Equal(
                h.Ad.Localizer.Format(L.Watchdog.ConfigVersion, 7, h.Ad.Localizer.FormatTime(session.Envelope.UpdatedAt, TimeZoneInfo.Utc), @"CONTOSO\jan.kowalski"),
                session.VersionText);
            Assert.Same(session.Envelope.Configuration, session.Working);
            Assert.False(session.SaveCommand.CanExecute(null));
            Assert.False(session.RevertCommand.CanExecute(null));
            Assert.True(session.ValidateCommand.CanExecute(null));
        });
    }

    [Fact]
    public async Task Dirty_ComparesTheSerializedConfiguration()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var session = await LoadAsync(h);
            var changes = 0;
            session.Changed += (_, _) => changes++;

            session.Update(c => c with { Digest = c.Digest with { SendWhenEmpty = !c.Digest.SendWhenEmpty } });
            Assert.True(session.IsDirty);
            Assert.True(session.SaveCommand.CanExecute(null));

            // A structurally equal copy (new list instances) is not a change.
            session.Update(c => c with { Digest = c.Digest with { SendWhenEmpty = !c.Digest.SendWhenEmpty, Days = [.. c.Digest.Days] } });
            Assert.False(session.IsDirty);
            Assert.Equal(2, changes);
        });
    }

    [Fact]
    public async Task Validate_ShowsTheLocalizedIssues_ErrorsFirst()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.Validator = _ =>
        [
            new ConfigIssue { Path = "smtp", Code = "noTls", Severity = IssueSeverity.Warning, Message = "No TLS." },
            new ConfigIssue { Path = "rules[24].name", Code = "required", Severity = IssueSeverity.Error, Message = "Required." },
            new ConfigIssue { Path = "rules[24].kinds", Code = "brandNewCode", Severity = IssueSeverity.Error, Message = "Something new." },
        ];
        await h.Ui.RunAsync(async () =>
        {
            var session = await LoadAsync(h);
            var changed = 0;
            session.IssuesChanged += (_, _) => changed++;

            Assert.False(await session.ValidateAsync());

            Assert.Equal(1, changed);
            Assert.True(session.HasErrors);
            Assert.Equal(["rules[24].kinds", "rules[24].name", "smtp"], session.Issues.Select(i => i.Path));
            var required = session.Issues.Single(i => i.Code == "required");
            Assert.True(required.IsError);
            Assert.Equal(h.Ad.Localizer[WatchdogDisplay.ValidationKey("required")], required.Text);
            Assert.Equal("rules[24].name: " + required.Text, required.DisplayText);
            Assert.Equal("Something new.", session.Issues.Single(i => i.Code == "brandNewCode").Text);
            Assert.False(session.Issues.Single(i => i.Code == "noTls").IsError);
            Assert.Equal([(required.Issue, required.Text)], session.IssuesFor("rules[24].name"));
            Assert.Null(session.Message);

            h.Client.Validator = null;
            Assert.True(await session.ValidateAsync());
            Assert.Empty(session.Issues);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.ConfigValid], session.Message);
        });
    }

    [Fact]
    public async Task Save_SendsTheWorkingCopyWithItsVersion_AndTakesTheSavedEnvelope()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var session = await LoadAsync(h);
            var reloaded = 0;
            session.Reloaded += (_, _) => reloaded++;
            session.Update(c => c with { Smtp = c.Smtp with { Password = new SecretField { IsSet = true, NewValue = "Nowe-Hasło-2026!" } } });

            Assert.True(await session.SaveAsync());

            var sent = Assert.Single(h.Client.SavedEnvelopes);
            Assert.Equal(7, sent.Version);
            Assert.Equal("Nowe-Hasło-2026!", sent.Configuration.Smtp.Password.NewValue);
            Assert.Equal(8, session.Envelope!.Version);
            Assert.False(session.IsDirty);
            Assert.Equal(1, reloaded);
            Assert.Null(session.Working!.Smtp.Password.NewValue);
            Assert.True(session.Working.Smtp.Password.IsSet);
            Assert.Equal(h.Ad.Localizer.Format(L.Watchdog.ConfigSaved, 8), session.Message);
            Assert.Contains(h.Ad.Notifications.Items, n => n.Severity == NotificationSeverity.Success && n.Message == session.Message);
            Assert.Equal(8, h.Client.Audit[0].Version);
        });
    }

    [Fact]
    public async Task Save_ConflictOfAnotherAdministrator_AsksToReload()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var session = await LoadAsync(h);
            h.Client.StoredConfig = h.Client.StoredConfig with { Version = 9 };
            session.Update(c => c with { Digest = c.Digest with { SendWhenEmpty = !c.Digest.SendWhenEmpty } });

            Assert.False(await session.SaveAsync());

            Assert.True(session.HasConflict);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.ErrorVersionConflict], session.Error);
            Assert.True(session.IsDirty);

            await session.ReloadCommand.ExecuteAsync(null);
            Assert.False(session.HasConflict);
            Assert.Null(session.Error);
            Assert.False(session.IsDirty);
            Assert.Equal(9, session.Envelope!.Version);
        });
    }

    [Fact]
    public async Task Save_RejectedAsInvalid_ShowsTheIssuesOfTheService()
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

            Assert.Equal(h.Ad.Localizer[L.Watchdog.ConfigInvalid], session.Error);
            var issue = Assert.Single(session.Issues);
            Assert.Equal("smtp.host", issue.Path);
            Assert.True(session.IsDirty);
            Assert.Equal(7, session.Envelope!.Version);
        });
    }

    [Fact]
    public async Task Revert_DiscardsTheChanges_AndTypedSecrets()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var session = await LoadAsync(h);
            session.Update(c => c with { Smtp = c.Smtp with { Password = new SecretField { IsSet = true, NewValue = "tajne" } } });
            Assert.True(session.RevertCommand.CanExecute(null));

            session.RevertCommand.Execute(null);

            Assert.False(session.IsDirty);
            Assert.Null(session.Working!.Smtp.Password.NewValue);
            Assert.Empty(h.Client.SavedEnvelopes);
        });
    }

    [Fact]
    public async Task Viewer_GetsARedactedReadOnlyCopy_ThatIsNeverSaved()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.IsAdmin = false;
        await h.Ui.RunAsync(async () =>
        {
            var session = new WatchdogConfigSession(h.Services) { IsAdmin = false };
            await session.LoadAsync();

            Assert.True(session.IsRedacted);
            Assert.False(session.CanEdit);
            Assert.True(session.IsReadOnly);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.RequiresAdmin], session.ReadOnlyReason);
            Assert.Empty(session.Working!.RecipientGroups.SelectMany(g => g.Addresses));
            Assert.All(session.Working.Rules.SelectMany(r => r.Exclusions), e => Assert.StartsWith("redacted-", e.Id, StringComparison.Ordinal));

            session.Update(c => c with { Digest = c.Digest with { SendWhenEmpty = !c.Digest.SendWhenEmpty } });
            Assert.False(session.IsDirty);
            Assert.False(session.SaveCommand.CanExecute(null));
            Assert.False(session.ValidateCommand.CanExecute(null));
            Assert.False(await session.SaveAsync());
            Assert.False(await session.ValidateAsync());
            Assert.Empty(h.Client.SavedEnvelopes);
            Assert.Equal(0, h.Client.Calls.Count(nameof(IWatchdogClient.ValidateConfigAsync)));
        });
    }

    [Fact]
    public async Task LoadFailure_IsReported()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.FailWith(WatchdogClientErrorKind.Forbidden, nameof(IWatchdogClient.GetConfigAsync));
        await h.Ui.RunAsync(async () =>
        {
            var session = new WatchdogConfigSession(h.Services) { IsAdmin = true };
            Exception? failure = null;
            session.Failed += (_, e) => failure = e;
            await session.LoadAsync();

            Assert.False(session.IsLoaded);
            Assert.False(session.IsReadOnly);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.ConnectionForbidden], session.Error);
            Assert.Equal(WatchdogClientErrorKind.Forbidden, Assert.IsType<WatchdogClientException>(failure).Kind);
        });
    }

    private static async Task<WatchdogConfigSession> LoadAsync(WatchdogHarness h)
    {
        var session = new WatchdogConfigSession(h.Services) { IsAdmin = true };
        await session.LoadAsync();
        return session;
    }
}
