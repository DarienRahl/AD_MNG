using System.Globalization;
using System.Text.Json;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>"Raport dzienny": settings mapping, preview kept in memory, "Wyślij teraz" for Admins.</summary>
public sealed class WatchdogDigestTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task Load_ShowsTheSettings_WithoutChangingThem()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var (session, digest) = await OpenAsync(h);

            Assert.True(digest.Enabled);
            Assert.Equal("07:30", digest.Time);
            Assert.Equal(["poniedziałek", "wtorek", "środa", "czwartek", "piątek", "sobota", "niedziela"], digest.Days.Select(d => d.Text));
            Assert.All(digest.Days, d => Assert.True(d.IsChecked));
            Assert.Equal([("it-admins", true), ("helpdesk", false)], digest.RecipientGroups.Select(g => (g.Value, g.IsChecked)));
            Assert.Equal([("teams-it", true), ("siem", false)], digest.Channels.Select(c => (c.Value, c.IsChecked)));
            Assert.True(digest.SendWhenEmpty);
            Assert.Equal("50", digest.MaxRowsPerSection);
            Assert.Equal("poniedziałek", digest.WeeklySectionsDay.Text);
            Assert.Equal("14", digest.UpcomingExpiryDays);
            Assert.True(digest.CoverNonBusinessDays);
            Assert.True(digest.CanEdit);
            Assert.Equal(Json(session.Envelope!.Configuration.Digest), Json(digest.Build()));
            Assert.False(session.IsDirty);
        });
    }

    [Fact]
    public async Task Edits_ReachTheWorkingConfiguration()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (session, digest) = await OpenAsync(h);

            digest.Time = "6:05";
            digest.Days.Single(d => d.Value == DayOfWeek.Saturday).IsChecked = false;
            digest.Days.Single(d => d.Value == DayOfWeek.Sunday).IsChecked = false;
            digest.RecipientGroups.Single(g => g.Value == "helpdesk").IsChecked = true;
            digest.Channels.Single(c => c.Value == "teams-it").IsChecked = false;
            digest.SendWhenEmpty = false;
            digest.MaxRowsPerSection = "20";
            digest.WeeklySectionsDay = digest.Weekdays.Single(d => d.Value == DayOfWeek.Friday);
            digest.UpcomingExpiryDays = "30";
            digest.CoverNonBusinessDays = false;
            digest.Enabled = false;

            var stored = session.Working!.Digest;
            Assert.Equal(new TimeOnly(6, 5), stored.Time);
            Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], stored.Days);
            Assert.Equal(["it-admins", "helpdesk"], stored.RecipientGroupIds);
            Assert.Empty(stored.ChannelIds);
            Assert.False(stored.SendWhenEmpty);
            Assert.Equal(20, stored.MaxRowsPerSection);
            Assert.Equal(DayOfWeek.Friday, stored.WeeklySectionsDay);
            Assert.Equal(30, stored.UpcomingExpiryDays);
            Assert.False(stored.CoverNonBusinessDays);
            Assert.False(stored.Enabled);
            Assert.Null(digest.TimeError);
            Assert.True(session.IsDirty);
        });
    }

    [Fact]
    public async Task InvalidTime_IsReported_AndTheStoredTimeIsKept()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var (session, digest) = await OpenAsync(h);

            digest.Time = "25:99";

            Assert.Equal("Podaj godzinę w formacie GG:mm (np. 07:30).", digest.TimeError);
            Assert.Equal(new TimeOnly(7, 30), session.Working!.Digest.Time);

            digest.Time = "07:30";
            Assert.Null(digest.TimeError);
            Assert.False(session.IsDirty);
        });
    }

    [Fact]
    public async Task Revert_ShowsTheStoredSettingsAgain()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (session, digest) = await OpenAsync(h);
            digest.Time = "18:00";
            digest.RecipientGroups.Single(g => g.Value == "it-admins").IsChecked = false;

            session.RevertCommand.Execute(null);

            Assert.Equal("07:30", digest.Time);
            Assert.True(digest.RecipientGroups.Single(g => g.Value == "it-admins").IsChecked);
            Assert.False(session.IsDirty);
        });
    }

    [Fact]
    public async Task Preview_ShowsSubjectTextAndPeriod_InMemoryOnly()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish, zone: TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"));
        await h.Ui.RunAsync(async () =>
        {
            var (_, digest) = await OpenAsync(h);
            var localizer = h.Ad.Localizer;
            var zone = h.Services.TimeZone;
            var sample = SampleStatus.Digest();

            await digest.PreviewCommand.ExecuteAsync(null);

            Assert.Equal("[AD Watchdog] Raport dzienny 14.10.2026 – 27 zdarzeń (2 krytyczne)", digest.PreviewSubject);
            Assert.Equal(sample.Text, digest.PreviewText);
            Assert.Contains("Żaneta Szymańska", digest.PreviewText, StringComparison.Ordinal);
            Assert.Equal(
                localizer.Format(
                    L.Watchdog.DigestInfo,
                    localizer.FormatTime(sample.PeriodStart, zone),
                    localizer.FormatTime(sample.PeriodEnd, zone),
                    27,
                    localizer.FormatTime(sample.NextScheduledAt, zone),
                    localizer.FormatTime(sample.LastSentAt, zone)),
                digest.PreviewInfo);
            Assert.StartsWith("Okres 13.10.2026", digest.PreviewInfo, StringComparison.Ordinal);
            Assert.False(digest.IsBusy);

            // The preview is never offered for saving and nothing is written.
            Assert.Null(h.Ad.Files.LastDefaultFileName);
            Assert.DoesNotContain(typeof(WatchdogDigestViewModel).GetProperties(), p => p.PropertyType == typeof(System.Windows.Input.ICommand) && p.Name.Contains("Save", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Preview_WithoutTheNextAndLastSending_ShowsDashes()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.DigestPreview = SampleStatus.Digest() with { NextScheduledAt = null, LastSentAt = null };
        await h.Ui.RunAsync(async () =>
        {
            var (_, digest) = await OpenAsync(h);
            await digest.PreviewCommand.ExecuteAsync(null);
            Assert.EndsWith("—", digest.PreviewInfo, StringComparison.Ordinal);

            h.Client.FailNext(nameof(IWatchdogClient.PreviewDigestAsync), new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "down"));
            await digest.PreviewCommand.ExecuteAsync(null);
            Assert.Equal(h.Connection.Describe(new WatchdogClientException(WatchdogClientErrorKind.Unreachable, "down")), digest.Error);
        });
    }

    [Fact]
    public async Task SendNow_AsksFirst_AndIsForAdminsOnly()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var (_, digest) = await OpenAsync(h);
            Assert.True(digest.SendNowCommand.CanExecute(null));

            h.Ad.Dialogs.ConfirmAnswers.Enqueue(false);
            await digest.SendNowCommand.ExecuteAsync(null);
            Assert.Equal(0, h.Client.DigestSends);

            h.Ad.Dialogs.ConfirmAnswers.Enqueue(true);
            await digest.SendNowCommand.ExecuteAsync(null);
            Assert.Equal(1, h.Client.DigestSends);
            Assert.Equal("Raport dzienny został przekazany do wysłania.", digest.Message);
            Assert.Contains(h.Ad.Notifications.Items, n => n.Severity == NotificationSeverity.Success && n.Message == digest.Message);
        });
    }

    [Fact]
    public async Task Viewer_CanPreview_ButNotEditOrSend()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.IsAdmin = false;
        await h.Ui.RunAsync(async () =>
        {
            var session = new WatchdogConfigSession(h.Services) { IsAdmin = false };
            await session.LoadAsync();
            using var digest = new WatchdogDigestViewModel(h.Services, session);

            Assert.False(digest.CanEdit);
            Assert.False(digest.SendNowCommand.CanExecute(null));
            Assert.True(digest.PreviewCommand.CanExecute(null));
            digest.Time = "12:00";
            Assert.False(session.IsDirty);

            await digest.PreviewCommand.ExecuteAsync(null);
            Assert.NotEmpty(digest.PreviewText);
        });
    }

    [Fact]
    public async Task LanguageSwitch_IsNotAnEdit_EvenWhenTheComboBoxClearsItsSelection()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var (session, digest) = await OpenAsync(h);
            digest.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WatchdogDigestViewModel.Weekdays))
                {
                    // What a WPF ComboBox does when its items are replaced and the selected one is gone.
                    digest.WeeklySectionsDay = null!;
                }
            };

            h.Ad.Localizer.SetCulture(Polish);
            digest.Relocalize();

            Assert.Equal("poniedziałek", digest.WeeklySectionsDay.Text);
            Assert.Equal("poniedziałek", digest.Days[0].Text);
            Assert.False(session.IsDirty);
        });
    }

    private static async Task<(WatchdogConfigSession Session, WatchdogDigestViewModel Digest)> OpenAsync(WatchdogHarness h)
    {
        var session = new WatchdogConfigSession(h.Services) { IsAdmin = true };
        await session.LoadAsync();
        return (session, new WatchdogDigestViewModel(h.Services, session));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, WatchdogJson.Options);
}
