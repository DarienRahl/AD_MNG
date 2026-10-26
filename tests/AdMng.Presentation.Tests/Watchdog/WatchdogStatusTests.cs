using System.Collections.Specialized;
using System.Globalization;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Watchdog;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Testing.Client;

namespace AdMng.Presentation.Tests.Watchdog;

/// <summary>"Stan": service, DC, attribution, checks, channels, storage, counts; delivery log and audit for Admins.</summary>
public sealed class WatchdogStatusTests
{
    private static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    [Fact]
    public async Task Load_FillsEveryCard()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.Status = SampleStatus.Create(rolloutUntil: new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services) { IsAdmin = true };
            var localizer = h.Ad.Localizer;
            var zone = h.Services.TimeZone;
            StatusDto? loaded = null;
            status.StatusLoaded += (_, s) => loaded = s;

            await status.LoadAsync();

            Assert.Equal("AD Watchdog – Contoso", loaded?.InstanceName);
            Assert.Same(loaded, status.Status);
            Assert.True(status.HasStatus);
            Assert.Null(status.Error);
            Assert.Equal(localizer.Format(L.Watchdog.StatusService, "AD Watchdog – Contoso", "1.0.0", WatchdogRoutes.ApiVersion, "contoso.test"), status.ServiceText);
            Assert.Equal(localizer.Format(L.Watchdog.StatusRollout, localizer.FormatTime(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero), zone)), status.RolloutText);
            Assert.Equal(HealthState.Ok, status.DirectoryState);
            Assert.Equal(localizer.Format(L.Watchdog.StatusPdc, "dc01.contoso.test"), status.DcText);
            Assert.Equal("1,204,877", status.UsnText);
            Assert.Equal("4,812", status.SnapshotText);
            Assert.Equal(localizer.FormatTimeWithSeconds(SampleEvents.Now.AddSeconds(-9), zone), status.LastPollText);

            Assert.Equal(["dc01.contoso.test", "dc02.contoso.test"], status.DomainControllers.Select(d => d.Host));
            var dc02 = status.DomainControllers[1];
            Assert.Equal("41%", dc02.CoverageText);
            Assert.Equal(localizer.Format(L.Watchdog.StatusLookups, 38, "950 ms", 6), dc02.LookupsText);
            Assert.Equal(localizer.Format(L.Watchdog.StatusWin32Error, "1722"), dc02.ErrorText);
            Assert.Equal("2201004", dc02.RecordText);

            // Problems first, then by code.
            Assert.Equal(
                ["eventlog.dc02.contoso.test", "attribution.actorConditionsUnreliable", "permission.deletedObjects", "directory.connection", "permission.uSNChanged", "storage.free"],
                status.Checks.Select(c => c.Code));
            Assert.Equal("RPC server unavailable. · dc02.contoso.test, 1722", status.Checks[0].Detail);
            Assert.Equal(string.Empty, status.Checks[3].Remedy);

            Assert.Equal(["email", "teams-it", "siem"], status.Channels.Select(c => c.ChannelId));
            Assert.Equal(localizer.Format(L.Watchdog.StatusChannelDetail, 4, 3), status.Channels[1].DetailText);
            Assert.Equal("http.401", status.Channels[1].ErrorCode);

            Assert.Equal(@"C:\ProgramData\AdMng\Watchdog\data", status.DataDirectory);
            Assert.Equal(localizer.Format(L.Watchdog.StatusStorage, "46.0 MB", "182.0 GB", "18,420", localizer.FormatTime(SampleEvents.Now.AddDays(-3), zone)), status.StorageText);
            Assert.Equal(localizer.Format(L.Watchdog.StatusCounts, 27, 24, 5), status.CountsText);
            Assert.Equal([Severity.Critical, Severity.High, Severity.Medium, Severity.Low, Severity.Info], status.Counts.Select(c => c.Severity));
            Assert.Equal([2, 4, 3, 6, 12], status.Counts.Select(c => c.Count));

            Assert.Equal(10, status.DeliveryLog.Items.Count);
            Assert.Equal([7L, 6L, 5L, 4L], status.Audit.Select(a => a.Id));
            Assert.False(status.AuditHasMore);
        });
    }

    [Fact]
    public async Task Checks_AreLocalizedWithTheirRemedies()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services) { IsAdmin = true };
            await status.LoadAsync();

            var eventLog = status.Checks.Single(c => c.Code == "eventlog.dc02.contoso.test");
            Assert.Equal("Dziennik zabezpieczeń dc02.contoso.test", eventLog.Title);
            Assert.Equal("Sprawdź zaporę (Remote Event Log Management) i członkostwo w Event Log Readers.", eventLog.Remedy);
            Assert.Equal(h.Ad.Localizer.HealthStateName(HealthState.Error), eventLog.StateText);
            var deleted = status.Checks.Single(c => c.Code == "permission.deletedObjects");
            Assert.Equal("Odczyt kontenera Deleted Objects", deleted.Title);
            Assert.StartsWith("Usunięcia są wykrywane tylko przez nocne uzgadnianie", deleted.Remedy, StringComparison.Ordinal);
            Assert.Equal("46,0 MB", WatchdogStatusViewModel.FormatBytes(48_234_496, Polish));
            Assert.Equal("512 B", WatchdogStatusViewModel.FormatBytes(512, Polish));
            Assert.Equal("182,0 GB", WatchdogStatusViewModel.FormatBytes(195_421_446_144, Polish));
        });
    }

    [Fact]
    public async Task Viewer_GetsNoDeliveryLogOrAudit()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.IsAdmin = false;
        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services) { IsAdmin = false };
            await status.LoadAsync();

            Assert.True(status.HasStatus);
            Assert.Empty(status.DeliveryLog.Items);
            Assert.Empty(status.Audit);
            Assert.Equal(0, h.Client.Calls.Count(nameof(IWatchdogClient.GetOutboxAsync)));
            Assert.Equal(0, h.Client.Calls.Count(nameof(IWatchdogClient.GetConfigAuditAsync)));
        });
    }

    [Fact]
    public async Task LoadFailure_IsReported()
    {
        using var h = await WatchdogHarness.CreateAsync();
        h.Client.FailWith(WatchdogClientErrorKind.Unauthorized, nameof(IWatchdogClient.GetStatusAsync));
        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services) { IsAdmin = true };
            Exception? failure = null;
            status.Failed += (_, e) => failure = e;

            await status.LoadAsync();

            Assert.False(status.HasStatus);
            Assert.Equal(h.Ad.Localizer[L.Watchdog.ConnectionUnauthorized], status.Error);
            Assert.IsType<WatchdogClientException>(failure);
            Assert.False(status.IsLoading);
            Assert.True(status.RefreshCommand.CanExecute(null));
        });
    }

    [Fact]
    public async Task Audit_IsPaged()
    {
        using var h = await WatchdogHarness.CreateAsync();
        for (var id = 8; id < 68; id++)
        {
            h.Client.Audit.Insert(0, new ConfigAuditEntry
            {
                Id = id,
                At = SampleEvents.Now.AddMinutes(id - 70),
                ByAccount = @"CONTOSO\jan.kowalski",
                Summary = "digest.time changed",
                Version = id,
            });
        }

        await h.Ui.RunAsync(async () =>
        {
            var status = new WatchdogStatusViewModel(h.Services) { IsAdmin = true };
            await status.LoadAsync();

            Assert.Equal(WatchdogStatusViewModel.AuditPageSize, status.Audit.Count);
            Assert.Equal(67, status.Audit[0].Id);
            Assert.Equal(@"CONTOSO\jan.kowalski", status.Audit[0].Account);
            Assert.Equal("67", status.Audit[0].VersionText);
            Assert.True(status.AuditHasMore);

            await status.LoadMoreAuditCommand.ExecuteAsync(null);

            Assert.Equal(64, status.Audit.Count);
            Assert.Equal(4, status.Audit[^1].Id);
            Assert.False(status.AuditHasMore);
            Assert.False(status.LoadMoreAuditCommand.CanExecute(null));
        });
    }

    [Fact]
    public async Task DeliveryLog_FiltersByStatusAndChannel()
    {
        using var h = await WatchdogHarness.CreateAsync(culture: Polish);
        await h.Ui.RunAsync(async () =>
        {
            var log = new WatchdogDeliveryLogViewModel(h.Services);
            await log.LoadAsync();
            Assert.Equal(h.Ad.Localizer[L.Watchdog.OutboxAllStatuses], log.Status.Text);
            Assert.Equal(10, log.Items.Count);
            var dead = log.Items.Single(r => r.Id == 117);
            Assert.Equal("Teams – Administratorzy IT", dead.ChannelText);
            Assert.Equal("8", dead.AttemptsText);
            Assert.Equal("http.401 – HTTP 401 Unauthorized (SAS signature invalid)", dead.ErrorText);
            Assert.Equal("Dostęp uprzywilejowany: Łukasz Wiśniewski", dead.Subject);
            Assert.Equal(h.Ad.Localizer.ChannelTypeName(DeliveryChannelType.Email), log.Items.Single(r => r.Id == 119).ChannelText);
            Assert.Equal(
                h.Ad.Localizer.Format(L.Watchdog.OutboxNextAttempt, h.Ad.Localizer.FormatTimeWithSeconds(SampleEvents.Now.AddMinutes(3), h.Services.TimeZone)),
                log.Items.Single(r => r.Id == 120).TimeText);

            log.Status = log.Statuses.Single(s => s.Value == OutboxStatus.Dead);
            await log.Loading;
            Assert.Equal([117L, 114L], log.Items.Select(r => r.Id));

            log.ChannelId = " teams-it ";
            await log.Loading;
            Assert.Equal([117L], log.Items.Select(r => r.Id));
            var query = h.Client.OutboxQueries[^1];
            Assert.Equal(OutboxStatus.Dead, query.Status);
            Assert.Equal("teams-it", query.ChannelId);
            Assert.Null(query.Before);
            Assert.Equal(WatchdogDeliveryLogViewModel.PageSize, query.Limit);
        });
    }

    [Fact]
    public async Task DeliveryLog_LanguageSwitch_KeepsTheFilterWithoutReloading()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var log = new WatchdogDeliveryLogViewModel(h.Services);
            await log.LoadAsync();
            log.Status = log.Statuses.Single(s => s.Value == OutboxStatus.Dead);
            await log.Loading;
            log.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WatchdogDeliveryLogViewModel.Statuses))
                {
                    log.Status = null!;
                }
            };
            var queries = h.Client.OutboxQueries.Count;

            h.Ad.Localizer.SetCulture(Polish);
            log.Relocalize();

            Assert.Equal(OutboxStatus.Dead, log.Status.Value);
            Assert.Equal(h.Ad.Localizer.OutboxStatusName(OutboxStatus.Dead), log.Status.Text);
            Assert.Equal(h.Ad.Localizer.OutboxStatusName(OutboxStatus.Dead), log.Items[0].StatusText);
            Assert.Equal(queries, h.Client.OutboxQueries.Count);
            Assert.Null(log.Error);

            log.Status = log.Statuses[0];
            await log.Loading;
            Assert.Equal(queries + 1, h.Client.OutboxQueries.Count);
        });
    }

    [Fact]
    public async Task DeliveryLog_LoadsOlderPages()
    {
        using var h = await WatchdogHarness.CreateAsync();
        var template = h.Client.Outbox.Single(o => o.Id == 119);
        for (var id = 121; id <= 230; id++)
        {
            h.Client.Outbox.Insert(0, template with { Id = id, CreatedAt = SampleEvents.Now.AddSeconds(id - 300) });
        }

        await h.Ui.RunAsync(async () =>
        {
            var log = new WatchdogDeliveryLogViewModel(h.Services);
            await log.LoadAsync();
            Assert.Equal(100, log.Items.Count);
            Assert.True(log.HasMore);
            var selected = log.Items[90];
            log.SelectedItem = selected;
            var changes = new List<NotifyCollectionChangedEventArgs>();
            log.Items.CollectionChanged += (_, e) => changes.Add(e);

            await log.LoadMoreCommand.ExecuteAsync(null);

            // Appended row by row below the loaded ones (a Reset would scroll the list back to the top).
            Assert.All(changes, c => Assert.Equal(NotifyCollectionChangedAction.Add, c.Action));
            Assert.Equal(Enumerable.Range(100, 20), changes.Select(c => c.NewStartingIndex));
            Assert.Same(selected, log.SelectedItem);
            Assert.Equal(120, log.Items.Count);
            Assert.Equal(111, log.Items[^1].Id);
            Assert.Equal(131, h.Client.OutboxQueries[^1].Before);
            Assert.False(log.HasMore);
        });
    }

    [Fact]
    public async Task DeliveryLog_RetriesDeadDeliveriesOnly()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var log = new WatchdogDeliveryLogViewModel(h.Services);
            await log.LoadAsync();
            var sent = log.Items.Single(r => r.Id == 119);
            var dead = log.Items.Single(r => r.Id == 114);
            Assert.False(log.RetryCommand.CanExecute(sent));
            Assert.True(log.RetryCommand.CanExecute(dead));

            log.SelectedItem = dead;
            await log.RetryCommand.ExecuteAsync(null);

            Assert.Equal(OutboxStatus.Pending, dead.Status);
            Assert.False(dead.IsDead);
            Assert.Equal(string.Empty, dead.ErrorText);
            Assert.Equal(OutboxStatus.Pending, h.Client.Outbox.Single(o => o.Id == 114).Status);
            Assert.False(log.RetryCommand.CanExecute(null));
            Assert.Contains(h.Ad.Notifications.Items, n => n.Severity == NotificationSeverity.Success && n.Message == h.Ad.Localizer[L.Watchdog.OutboxRetried]);

            // Retried elsewhere in the meantime: the service refuses, the error is shown.
            var other = log.Items.Single(r => r.Id == 117);
            h.Client.Outbox[h.Client.Outbox.FindIndex(o => o.Id == 117)] = other.Item with { Status = OutboxStatus.Pending };
            await log.RetryCommand.ExecuteAsync(other);
            Assert.NotNull(log.Error);
        });
    }

    [Fact]
    public async Task DeliveryLog_SupersededLoad_ShowsNoError()
    {
        using var h = await WatchdogHarness.CreateAsync();
        await h.Ui.RunAsync(async () =>
        {
            var log = new WatchdogDeliveryLogViewModel(h.Services);
            h.Client.Hold(nameof(IWatchdogClient.GetOutboxAsync));
            var first = log.LoadAsync();
            var second = log.LoadAsync();
            await first;
            h.Client.Release(nameof(IWatchdogClient.GetOutboxAsync));
            await second;

            Assert.Null(log.Error);
            Assert.Equal(10, log.Items.Count);
            Assert.False(log.IsLoading);
        });
        h.AssertNoUiErrors();
    }
}
