using System.Collections.ObjectModel;
using System.Globalization;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// The "Stan" sub-tab: cards for the service, the watched DC, attribution per DC, the health checks (localized code,
/// state, remedy), channels, storage and the counts of the last 24 hours; for Admins the delivery log (filter, retry of
/// dead deliveries) and the configuration audit.
/// </summary>
public sealed partial class WatchdogStatusViewModel : ObservableObject
{
    public const int AuditPageSize = 50;

    private readonly WatchdogServices _services;

    public WatchdogStatusViewModel(WatchdogServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        DeliveryLog = new WatchdogDeliveryLogViewModel(services);
    }

    /// <summary>A service call failed.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>A fresh status was read here (the tab updates its badge and rollout banner).</summary>
    public event EventHandler<StatusDto>? StatusLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial StatusDto? Status { get; private set; }

    public bool HasStatus => Status is not null;

    /// <summary>The caller has the Admin role: delivery log and configuration audit are shown.</summary>
    [ObservableProperty]
    public partial bool IsAdmin { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    // ---- Service -----------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string ServiceText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StartedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RolloutText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string NextDigestText { get; private set; } = string.Empty;

    // ---- Directory ---------------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial HealthState DirectoryState { get; private set; }

    [ObservableProperty]
    public partial string DirectoryStateText { get; private set; } = string.Empty;

    /// <summary>"dc01.contoso.test (PDC)".</summary>
    [ObservableProperty]
    public partial string DcText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string UsnText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LastPollText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LastChangeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LastFullSyncText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string BaselineText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SnapshotText { get; private set; } = string.Empty;

    // ---- Attribution, checks, channels -------------------------------------------------------------------------

    [ObservableProperty]
    public partial string AttributionText { get; private set; } = string.Empty;

    public ObservableCollection<WatchdogDcLogRowViewModel> DomainControllers { get; } = [];

    public ObservableCollection<WatchdogHealthCheckRowViewModel> Checks { get; } = [];

    public ObservableCollection<WatchdogChannelHealthRowViewModel> Channels { get; } = [];

    // ---- Storage and counts ------------------------------------------------------------------------------------

    [ObservableProperty]
    public partial string DataDirectory { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StorageText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string BackupText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CountsText { get; private set; } = string.Empty;

    public ObservableCollection<WatchdogSeverityCountViewModel> Counts { get; } = [];

    // ---- Admin -------------------------------------------------------------------------------------------------

    public WatchdogDeliveryLogViewModel DeliveryLog { get; }

    public ObservableCollection<WatchdogConfigAuditRowViewModel> Audit { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreAuditCommand))]
    public partial bool AuditHasMore { get; private set; }

    [ObservableProperty]
    public partial string? AuditError { get; private set; }

    /// <summary>Shows a status read elsewhere (poller, tab initialization).</summary>
    public void ApplyStatus(StatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Status = status;
        Fill(status);
    }

    /// <summary>Reads the status and, for Admins, the delivery log and the configuration audit. Never throws.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            var status = await _services.Client.GetStatusAsync(CancellationToken.None).ConfigureAwait(true);
            ApplyStatus(status);
            StatusLoaded?.Invoke(this, status);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = _services.Connection.Describe(ex);
            Failed?.Invoke(this, ex);
        }
        finally
        {
            IsLoading = false;
        }

        if (IsAdmin)
        {
            await Task.WhenAll(DeliveryLog.LoadAsync(), LoadAuditAsync(null)).ConfigureAwait(true);
        }
    }

    /// <summary>Texts after a language switch.</summary>
    public void Relocalize()
    {
        if (Status is { } status)
        {
            Fill(status);
        }

        DeliveryLog.Relocalize();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync();

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(AuditHasMore))]
    private Task LoadMoreAuditAsync() => LoadAuditAsync(Audit.Count == 0 ? null : Audit.Min(a => a.Id));

    private async Task LoadAuditAsync(long? before)
    {
        AuditError = null;
        try
        {
            var page = await _services.Client.GetConfigAuditAsync(before, AuditPageSize, CancellationToken.None).ConfigureAwait(true);
            if (before is null)
            {
                Audit.Clear();
            }

            foreach (var entry in page.Items)
            {
                Audit.Add(new WatchdogConfigAuditRowViewModel(entry, _services.Localizer, _services.TimeZone));
            }

            AuditHasMore = page.HasMore;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AuditError = _services.Connection.Describe(ex);
        }
    }

    private void Fill(StatusDto status)
    {
        var localizer = _services.Localizer;
        var zone = _services.TimeZone;
        var culture = localizer.Culture;
        ServiceText = localizer.Format(L.Watchdog.StatusService, status.InstanceName, status.ServiceVersion, status.ApiVersion, status.Domain ?? string.Empty);
        StartedText = localizer.FormatTimeWithSeconds(status.StartedAt, zone);
        RolloutText = status.RolloutModeUntil is { } until ? localizer.Format(L.Watchdog.StatusRollout, localizer.FormatTime(until, zone)) : string.Empty;
        NextDigestText = localizer.FormatTime(status.NextDigestAt, zone);

        var directory = status.Directory;
        DirectoryState = directory.State;
        DirectoryStateText = localizer.HealthStateName(directory.State);
        DcText = directory.DcHostName is null ? string.Empty : directory.IsPdc ? localizer.Format(L.Watchdog.StatusPdc, directory.DcHostName) : directory.DcHostName;
        UsnText = directory.HighestUsn.ToString("N0", culture);
        LastPollText = localizer.FormatTimeWithSeconds(directory.LastPollAt, zone);
        LastChangeText = localizer.FormatTimeWithSeconds(directory.LastChangeAt, zone);
        LastFullSyncText = localizer.FormatTime(directory.LastFullSyncAt, zone);
        BaselineText = localizer.FormatTime(directory.BaselineCompletedAt, zone);
        SnapshotText = directory.SnapshotObjects.ToString("N0", culture);

        AttributionText = status.Attribution.Enabled
            ? localizer.Format(L.Watchdog.StatusAttributionEnabled, status.Attribution.Source)
            : localizer[L.Watchdog.StatusAttributionDisabled];
        Replace(DomainControllers, status.Attribution.DomainControllers.Select(d => new WatchdogDcLogRowViewModel(d, localizer, zone)));
        Replace(Checks, status.Checks.OrderByDescending(c => Rank(c.State)).ThenBy(c => c.Code, StringComparer.Ordinal).Select(c => new WatchdogHealthCheckRowViewModel(c, localizer, zone)));
        Replace(Channels, status.Channels.Select(c => new WatchdogChannelHealthRowViewModel(c, localizer, zone)));

        var storage = status.Storage;
        DataDirectory = storage.DataDirectory;
        StorageText = localizer.Format(L.Watchdog.StatusStorage, FormatBytes(storage.DatabaseBytes, culture), FormatBytes(storage.FreeBytes, culture), storage.EventCount.ToString("N0", culture), localizer.FormatTime(storage.OldestEventAt, zone));
        BackupText = localizer.FormatTime(storage.LastBackupAt, zone);

        var counts = status.Last24Hours;
        CountsText = localizer.Format(L.Watchdog.StatusCounts, counts.Total, counts.Unacknowledged, counts.UnacknowledgedHighOrAbove);
        Replace(Counts, Enum.GetValues<Severity>().Reverse().Select(s => new WatchdogSeverityCountViewModel(s, localizer.SeverityName(s), counts.BySeverity.TryGetValue(s, out var n) ? n : 0)));
    }

    /// <summary>"46,0 MB".</summary>
    public static string FormatBytes(long bytes, CultureInfo culture)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes.ToString(culture)} B" : $"{value.ToString("0.0", culture)} {units[unit]}";
    }

    private static int Rank(HealthState state) => state switch
    {
        HealthState.Error => 4,
        HealthState.Warning => 3,
        HealthState.Unknown => 2,
        HealthState.Ok => 1,
        _ => 0,
    };

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
