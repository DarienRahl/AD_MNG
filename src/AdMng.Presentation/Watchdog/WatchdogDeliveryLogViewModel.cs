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
    private readonly SerialCancellation _loads = new();
    private bool _initialized;

    public WatchdogDeliveryLogViewModel(WatchdogServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
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

        Error = null;
        try
        {
            await _services.Client.RetryOutboxAsync(row.Id, CancellationToken.None).ConfigureAwait(true);
            row.Update(row.Item with { Status = OutboxStatus.Pending, NextAttemptAt = _services.Time.GetUtcNow(), LastError = null, LastErrorCode = null });
            RetryCommand.NotifyCanExecuteChanged();
            _services.Ui.Notifications.Show(Services.NotificationSeverity.Success, _services.Localizer[L.Watchdog.OutboxRetried]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = _services.Connection.Describe(ex);
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
        var token = _loads.Next();
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
            var page = await _services.Client.GetOutboxAsync(query, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
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
        catch (Exception ex) when (ex is not OutOfMemoryException && token.IsCancellationRequested)
        {
            // Superseded (OperationCanceledException or a WatchdogClientException of kind Cancelled): never an error.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = _services.Connection.Describe(ex);
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
