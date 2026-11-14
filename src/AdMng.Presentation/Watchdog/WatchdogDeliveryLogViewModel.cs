using AdMng.Presentation.Feedback;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Api;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// The delivery log of the Status sub-tab (Admin): outbox messages filtered by status and channel, older pages on
/// demand (appended row by row, so the list keeps its scroll position and selection), "Ponów" for dead deliveries (queued
/// again at once).
/// </summary>
public sealed partial class WatchdogDeliveryLogViewModel : ObservableObject
{
    public const int PageSize = 100;

    private readonly WatchdogServices _services;
    private readonly PageStatusHolder _pageStatus;
    private readonly SerialCancellation _loads = new();
    private bool _initialized;

    /// <param name="services">The tab's services.</param>
    /// <param name="pageStatus">
    /// The holder the log says its status in (the status tab's: its reads and the retry of a dead delivery are operations of
    /// that tab, one after another); a standalone log makes its own.
    /// </param>
    public WatchdogDeliveryLogViewModel(WatchdogServices services, PageStatusHolder? pageStatus = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _pageStatus = pageStatus ?? services.CreateStatus();
        Statuses = CreateStatuses();
        Status = Statuses[0];
    }

    public BulkObservableCollection<WatchdogOutboxRowViewModel> Items { get; } = [];

    public IReadOnlyList<Option<OutboxStatus?>> Statuses { get; private set; }

    [ObservableProperty]
    public partial Option<OutboxStatus?> Status { get; set; }

    /// <summary>Channel id filter ("email", "teams-it"…); empty = all.</summary>
    [ObservableProperty]
    public partial string ChannelId { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial WatchdogOutboxRowViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMore { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand), nameof(RefreshCommand), nameof(RetryCommand))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The current load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the newest page with the current filter. Never throws.</summary>
    public Task LoadAsync()
    {
        _initialized = true;
        Loading = LoadCoreAsync(before: null);
        return Loading;
    }

    /// <summary>Texts after a language switch.</summary>
    public void Relocalize()
    {
        var status = Status?.Value;
        var initialized = _initialized;

        // The filter does not change: no reload while the options are replaced.
        _initialized = false;
        Statuses = CreateStatuses();
        OnPropertyChanged(nameof(Statuses));
        Status = Statuses.First(s => s.Value == status);
        _initialized = initialized;
        foreach (var row in Items)
        {
            row.Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAsync();

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync()
    {
        Loading = LoadCoreAsync(Items.Count == 0 ? null : Items.Min(r => r.Id));
        return Loading;
    }

    private bool CanLoadMore() => HasMore && !IsLoading;

    /// <summary>Queues a dead delivery again.</summary>
    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync(WatchdogOutboxRowViewModel? row)
    {
        row ??= SelectedItem;
        if (row is not { IsDead: true })
        {
            return;
        }

        var localizer = _services.Localizer;

        // A write to the service: no Stop. The row says the result (it is pending again); only a failure is said in the line.
        using var scope = _pageStatus.Begin(localizer[L.WatchdogPageStatus.RetryQueuing], new PageStatusLog(L.WatchdogPageStatus.RetryQueuing), canCancel: false);
        Error = null;
        try
        {
            await _services.Client.RetryOutboxAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            row.Update(row.Item with { Status = OutboxStatus.Pending, NextAttemptAt = _services.Time.GetUtcNow(), LastError = null, LastErrorCode = null });
            RetryCommand.NotifyCanExecuteChanged();
            _services.Ui.Notifications.Show(Services.NotificationSeverity.Success, localizer[L.Watchdog.OutboxRetried]);
            scope.Complete(null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = _services.Connection.Describe(ex);
            scope.CompleteError(
                PageStatusText.Failure(localizer[L.WatchdogPageStatus.RetryFailed], Error, null, ResultCode.Of(ex)),
                new PageStatusLog(L.WatchdogPageStatus.RetryFailed).WithCode(ResultCode.Of(ex)));
        }
    }

    private bool CanRetry(WatchdogOutboxRowViewModel? row) => !IsLoading && (row ?? SelectedItem) is { IsDead: true };

    partial void OnStatusChanged(Option<OutboxStatus?> value)
    {
        // A combo box clears its selection (null) while its items are replaced.
        if (_initialized && value is not null)
        {
            LoadAsync().Forget();
        }
    }

    partial void OnChannelIdChanged(string value)
    {
        if (_initialized)
        {
            LoadAsync().Forget();
        }
    }

    private List<Option<OutboxStatus?>> CreateStatuses() =>
    [
        new(null, _services.Localizer[L.Watchdog.OutboxAllStatuses]),
        .. Enum.GetValues<OutboxStatus>().Select(s => new Option<OutboxStatus?>(s, _services.Localizer.OutboxStatusName(s))),
    ];

    private async Task LoadCoreAsync(long? before)
    {
        var localizer = _services.Localizer;
        var token = _loads.Next();
        using var scope = _pageStatus.Begin(localizer[L.WatchdogPageStatus.LogReading], new PageStatusLog(L.WatchdogPageStatus.LogReading));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, scope.Token);
        IsLoading = true;
        Error = null;
        try
        {
            var query = new OutboxQuery
            {
                Status = Status?.Value,
                ChannelId = string.IsNullOrWhiteSpace(ChannelId) ? null : ChannelId.Trim(),
                Before = before,
                Limit = PageSize,
            };
            var page = await _services.Client.GetOutboxAsync(query, linked.Token).ConfigureAwait(true);
            linked.Token.ThrowIfCancellationRequested();
            var rows = page.Items.Select(i => new WatchdogOutboxRowViewModel(i, _services.Localizer, _services.TimeZone));
            if (before is null)
            {
                Items.ReplaceAll(rows);
            }
            else
            {
                // Row by row below the loaded ones: a Reset would scroll the list back to the top.
                foreach (var row in rows)
                {
                    Items.Add(row);
                }
            }

            HasMore = page.HasMore;
            Error = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && scope.IsCancelling && !token.IsCancellationRequested)
        {
            // The operator's Stop: the list is as it was.
            var label = localizer[before is null ? L.Commands.Refresh : L.WatchdogUi.LoadOlder];
            scope.CompleteAttention(
                localizer.Format(L.WatchdogPageStatus.LogStopped, label),
                new PageStatusLog(L.WatchdogPageStatus.LogStopped),
                new PageStatusAction(label, before is null ? RefreshCommand : LoadMoreCommand));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && (token.IsCancellationRequested || scope.IsEnded))
        {
            // Superseded (OperationCanceledException or a WatchdogClientException of kind Cancelled): never an error.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = _services.Connection.Describe(ex);
            scope.CompleteError(
                PageStatusText.Failure(localizer[L.WatchdogPageStatus.LogFailed], Error, localizer[L.Feedback.RetryNext], ResultCode.Of(ex)),
                new PageStatusLog(L.WatchdogPageStatus.LogFailed).WithCode(ResultCode.Of(ex)),
                new PageStatusAction(localizer[L.Common.Retry], before is null ? RefreshCommand : LoadMoreCommand));
        }
        finally
        {
            if (_loads.IsCurrent(token))
            {
                IsLoading = false;
            }
        }
    }
}
