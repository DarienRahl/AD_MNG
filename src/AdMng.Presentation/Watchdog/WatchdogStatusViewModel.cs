using System.Collections.ObjectModel;
using System.Globalization;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Events;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// The "Stan" sub-tab: cards for the service, the watched DC, attribution per DC, the health checks (localized code,
/// state, remedy), channels, storage and the counts of the last 24 hours, the stage B sections „Konto usługi”, „Kolektor
/// WEF”, „Zawartość GPO” and „Host” (design 18 item 2; "not reported" while the service has no data for them); for Admins
/// the delivery log (filter, retry of dead deliveries) and the configuration audit.
/// </summary>
/// <remarks>
/// What the tab says (track V, stage V2b-4, unit 4): <see cref="PageHolder"/> is the one holder of the tab - the read of the
/// status, then (Admin) of the delivery log and of the configuration audit, one operation after another, each stoppable; the
/// summary says when the status shown was received. A status read that failed while none is shown is the state of the tab
/// that lasts; one that failed with a status on screen is a result that can be closed. The log copies are keys, counts and a
/// result code - never a delivery, a channel, an address or an account.
/// </remarks>
public sealed partial class WatchdogStatusViewModel : ObservableObject
{
    public const int AuditPageSize = 50;

    private readonly WatchdogServices _services;
    private DateTimeOffset? _receivedAt;
    private Problem? _problem;

    // The refreshes begun so far: one that a newer refresh overtook reads no further (LoadAsync).
    private int _refreshes;

    public WatchdogStatusViewModel(WatchdogServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        PageHolder = services.CreateStatus();
        DeliveryLog = new WatchdogDeliveryLogViewModel(services, PageHolder);
        ResetSections();
    }

    /// <summary>What the tab says in the status line: the reads of the status, the delivery log and the audit, and their results.</summary>
    public PageStatusHolder PageHolder { get; }

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

    // ---- Stage B sections (design 18 item 2) --------------------------------------------------------------------

    /// <summary>„Konto usługi”.</summary>
    [ObservableProperty]
    public partial WatchdogStatusSectionViewModel ServiceAccountSection { get; private set; }

    /// <summary>„Kolektor WEF”.</summary>
    [ObservableProperty]
    public partial WatchdogStatusSectionViewModel CollectorSection { get; private set; }

    /// <summary>„Zawartość GPO”.</summary>
    [ObservableProperty]
    public partial WatchdogStatusSectionViewModel GpoContentSection { get; private set; }

    /// <summary>„Host”.</summary>
    [ObservableProperty]
    public partial WatchdogStatusSectionViewModel HostSection { get; private set; }

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
        _receivedAt = _services.Time.GetUtcNow();
        _problem = null;
        UpdateIdle();
        UpdateLasting();
    }

    /// <summary>The tab was closed: what still runs ends without a word.</summary>
    public void End() => PageHolder.Operation?.Dispose();

    /// <summary>Reads the status and, for Admins, the delivery log and the configuration audit. Never throws.</summary>
    public async Task LoadAsync()
    {
        var localizer = _services.Localizer;
        var run = ++_refreshes;
        using (var scope = PageHolder.Begin(localizer[L.WatchdogPageStatus.Reading], new PageStatusLog(L.WatchdogPageStatus.Reading)))
        {
            IsLoading = true;
            Error = null;
            try
            {
                var status = await _services.Client.GetStatusAsync(scope.Token).ConfigureAwait(true);
                if (scope.IsEnded)
                {
                    // Replaced by a newer read, or the tab was closed: the newer one owns the tab.
                    return;
                }

                ApplyStatus(status);
                StatusLoaded?.Invoke(this, status);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && scope.IsCancelling)
            {
                // The operator's Stop: the tab may show an older status (or none), and the lists are not read.
                var refresh = localizer[L.Commands.Refresh];
                if (Status is null)
                {
                    _problem = new Problem(string.Empty, default, Stopped: true);
                    UpdateLasting();
                }
                else
                {
                    scope.CompleteAttention(
                        localizer.Format(L.WatchdogPageStatus.Stopped, refresh),
                        new PageStatusLog(L.WatchdogPageStatus.Stopped),
                        new PageStatusAction(refresh, RefreshCommand));
                }

                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && scope.IsEnded)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Error = _services.Connection.Describe(ex);
                if (Status is null)
                {
                    // Nothing is shown: the failure is the explanation of the empty cards, until a status arrives.
                    _problem = new Problem(Error, ResultCode.Of(ex));
                    UpdateLasting();
                }
                else
                {
                    scope.CompleteError(
                        PageStatusText.Failure(localizer[L.WatchdogPageStatus.ReadFailed], Error, localizer[L.Feedback.RetryNext], ResultCode.Of(ex)),
                        new PageStatusLog(L.WatchdogPageStatus.ReadFailed).WithCode(ResultCode.Of(ex)),
                        new PageStatusAction(localizer[L.Common.Retry], RefreshCommand));
                }

                Failed?.Invoke(this, ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        // The delivery log and the audit follow the status, one after another on the holder of the tab. A read that failed
        // still reads them (the service may answer these); one that was stopped does not (handled above). A refresh that a
        // newer one overtook reads no further: its next read would take the line - and the token - of the newer one.
        var failure = FailureOnTheLine();
        if (IsAdmin && run == _refreshes)
        {
            await DeliveryLog.LoadAsync().ConfigureAwait(true);
            failure ??= FailureOnTheLine();
            if (run == _refreshes)
            {
                await LoadAuditAsync(null).ConfigureAwait(true);
            }
        }

        // The three reads share the line of the tab and each takes it from the one before, so a failure of the first or the
        // second was on the line for milliseconds and the refresh ended as the summary "Status read <an old time>". The
        // refresh ends with the first thing that went wrong, and its Retry.
        if (failure is not null && run == _refreshes && !PageHolder.IsWorking && !ReferenceEquals(PageHolder.Current, failure))
        {
            PageHolder.Show(failure);
        }
    }

    /// <summary>The failure a read of this refresh left on the line as its result (not the state of the tab that lasts), or null.</summary>
    private PageStatus? FailureOnTheLine() =>
        PageHolder.Current is { Kind: PageStatusKind.Error, IsWorking: false } said
            && !(PageHolder.Lasting is { } lasting && lasting.HasSameContentAs(said))
            ? said
            : null;

    /// <summary>Texts after a language switch.</summary>
    public void Relocalize()
    {
        if (Status is { } status)
        {
            Fill(status);
        }
        else
        {
            ResetSections();
        }

        DeliveryLog.Relocalize();
        UpdateIdle();
        UpdateLasting();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync();

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(AuditHasMore))]
    private Task LoadMoreAuditAsync() => LoadAuditAsync(Audit.Count == 0 ? null : Audit.Min(a => a.Id));

    private async Task LoadAuditAsync(long? before)
    {
        var localizer = _services.Localizer;
        using var scope = PageHolder.Begin(localizer[L.WatchdogPageStatus.AuditReading], new PageStatusLog(L.WatchdogPageStatus.AuditReading));
        AuditError = null;
        try
        {
            var page = await _services.Client.GetConfigAuditAsync(before, AuditPageSize, scope.Token).ConfigureAwait(true);
            if (scope.IsEnded)
            {
                return;
            }

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
        catch (Exception ex) when (ex is not OutOfMemoryException && scope.IsCancelling)
        {
            var label = AuditActionLabel(before);
            scope.CompleteAttention(
                localizer.Format(L.WatchdogPageStatus.AuditStopped, label),
                new PageStatusLog(L.WatchdogPageStatus.AuditStopped),
                new PageStatusAction(label, before is null ? RefreshCommand : LoadMoreAuditCommand));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && scope.IsEnded)
        {
            // Replaced or closed: nothing to say.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AuditError = _services.Connection.Describe(ex);
            scope.CompleteError(
                PageStatusText.Failure(localizer[L.WatchdogPageStatus.AuditFailed], AuditError, localizer[L.Feedback.RetryNext], ResultCode.Of(ex)),
                new PageStatusLog(L.WatchdogPageStatus.AuditFailed).WithCode(ResultCode.Of(ex)),
                new PageStatusAction(localizer[L.Common.Retry], before is null ? RefreshCommand : LoadMoreAuditCommand));
        }
    }

    private string AuditActionLabel(long? before) =>
        _services.Localizer[before is null ? L.Commands.Refresh : L.WatchdogUi.LoadOlder];

    /// <summary>The summary: when the status shown was received.</summary>
    private void UpdateIdle() =>
        PageHolder.SetIdle(_receivedAt is { } at
            ? _services.Localizer.Format(L.WatchdogPageStatus.Summary, _services.Localizer.FormatTimeWithSeconds(at, _services.TimeZone))
            : string.Empty);

    /// <summary>
    /// Says the state of the tab that lasts: no status could be read (with Retry) or its read was stopped (with Refresh) while
    /// none is shown. Computed anew whenever a condition changes, so it follows the language.
    /// </summary>
    private void UpdateLasting()
    {
        var localizer = _services.Localizer;
        if (_problem is null)
        {
            PageHolder.SetLasting(null);
        }
        else if (_problem.Stopped)
        {
            var refresh = localizer[L.Commands.Refresh];
            PageHolder.SetLasting(PageStatus.Attention(
                localizer.Format(L.WatchdogPageStatus.Stopped, refresh),
                new PageStatusLog(L.WatchdogPageStatus.Stopped),
                new PageStatusAction(refresh, RefreshCommand)));
        }
        else
        {
            PageHolder.SetLasting(PageStatus.Error(
                PageStatusText.Failure(localizer[L.WatchdogPageStatus.ReadFailed], _problem.Text, localizer[L.Feedback.RetryNext], _problem.Code),
                new PageStatusLog(L.WatchdogPageStatus.ReadFailed).WithCode(_problem.Code),
                new PageStatusAction(localizer[L.Common.Retry], RefreshCommand)));
        }
    }

    /// <summary>Why the status could not be read: the sentence on screen and the result code - or that the read was stopped.</summary>
    private sealed record Problem(string Text, ResultCode Code, bool Stopped = false)
    {
        /// <summary>The code only - never the text.</summary>
        public override string ToString() => $"{nameof(Problem)}({Code})";
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
            ? localizer.Format(L.Watchdog.StatusAttributionEnabled, localizer.AttributionSourceName(status.Attribution.Source))
            : localizer[L.Watchdog.StatusAttributionDisabled];
        Replace(DomainControllers, status.Attribution.DomainControllers.Select(d => new WatchdogDcLogRowViewModel(d, localizer, zone)));
        Replace(Checks, status.Checks.OrderByDescending(c => Rank(c.State)).ThenBy(c => c.Code, StringComparer.Ordinal).Select(c => new WatchdogHealthCheckRowViewModel(c, localizer, zone)));
        Replace(Channels, status.Channels.Select(c => new WatchdogChannelHealthRowViewModel(c, localizer, zone)));

        var storage = status.Storage;
        DataDirectory = storage.DataDirectory;
        StorageText = localizer.Format(L.Watchdog.StatusStorage, FormatBytes(storage.DatabaseBytes, culture), FormatBytes(storage.FreeBytes, culture), storage.EventCount.ToString("N0", culture), localizer.FormatTime(storage.OldestEventAt, zone));
        BackupText = localizer.FormatTime(storage.LastBackupAt, zone);

        ServiceAccountSection = WatchdogStatusSectionViewModel.ServiceAccount(status, localizer, zone);
        CollectorSection = WatchdogStatusSectionViewModel.Collector(status, localizer, zone);
        GpoContentSection = WatchdogStatusSectionViewModel.GpoContent(status, localizer, zone);
        HostSection = WatchdogStatusSectionViewModel.Host(status, localizer, zone);

        var counts = status.Last24Hours;
        CountsText = localizer.Format(L.Watchdog.StatusCounts, counts.Total, counts.Unacknowledged, counts.UnacknowledgedHighOrAbove);
        Replace(Counts, Enum.GetValues<Severity>().Reverse().Select(s => new WatchdogSeverityCountViewModel(s, localizer.SeverityName(s), counts.BySeverity.TryGetValue(s, out var n) ? n : 0)));
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(ServiceAccountSection), nameof(CollectorSection), nameof(GpoContentSection), nameof(HostSection))]
    private void ResetSections()
    {
        var localizer = _services.Localizer;
        ServiceAccountSection = WatchdogStatusSectionViewModel.Empty(localizer[L.WatchdogLinux.CardServiceAccount]);
        CollectorSection = WatchdogStatusSectionViewModel.Empty(localizer[L.WatchdogLinux.CardCollector]);
        GpoContentSection = WatchdogStatusSectionViewModel.Empty(localizer[L.WatchdogLinux.CardGpoContent]);
        HostSection = WatchdogStatusSectionViewModel.Empty(localizer[L.WatchdogLinux.CardHost]);
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
