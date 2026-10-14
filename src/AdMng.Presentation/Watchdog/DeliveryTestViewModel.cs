using System.Collections.ObjectModel;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Client;
using AdMng.Watchdog.Contracts.Api;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// Runs a test send (<c>SendTestEmailAsync</c> / <c>SendTestChannelAsync</c>) with the unsaved settings and shows its
/// stages (✓/✗, detail, status codes, duration) and the localized hints (<c>Watchdog.Hint.*</c>). A
/// <c>secretRebindRequired</c> answer (the destination changed while the stored secret was kept) is reported through
/// <see cref="SecretRebindRequired"/> so the editor focuses the secret field – nothing was sent.
/// </summary>
public sealed partial class DeliveryTestViewModel : ObservableObject
{
    private readonly WatchdogServices _services;
    private readonly SerialCancellation _runs = new();

    public DeliveryTestViewModel(WatchdogServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>The service refused the test because a stored secret must be entered again (issue path).</summary>
    public event EventHandler<string>? SecretRebindRequired;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>Null before the first test.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial bool? Success { get; private set; }

    public bool HasResult => Success is not null;

    /// <summary>"Test zakończony powodzeniem (1,2 s)" / "Test nie powiódł się".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    /// <summary>Why the test could not run (connection, validation).</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    public ObservableCollection<DeliveryStageRowViewModel> Stages { get; } = [];

    public ObservableCollection<string> Hints { get; } = [];

    /// <summary>Runs one test; a running one is cancelled. Never throws.</summary>
    public async Task RunAsync(Func<CancellationToken, Task<DeliveryTestResult>> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        var token = _runs.Next();
        IsRunning = true;
        Error = null;
        Success = null;
        SummaryText = _services.Localizer[L.Watchdog.TestRunning];
        Stages.Clear();
        Hints.Clear();
        try
        {
            var result = await send(token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            Show(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SummaryText = _services.Localizer[L.Errors.Cancelled];
        }
        catch (WatchdogClientException ex) when (ex.Kind == WatchdogClientErrorKind.Validation)
        {
            Success = false;
            SummaryText = _services.Localizer[L.Watchdog.TestNotSent];
            var rebind = ex.Issues.FirstOrDefault(i => i.Code == "secretRebindRequired");
            if (rebind is not null)
            {
                Error = _services.Localizer[L.Watchdog.TestRebindRequired];
                Hints.Add(_services.Localizer.HintText(DeliveryHints.SecretRebindRequired));
                SecretRebindRequired?.Invoke(this, rebind.Path);
            }
            else
            {
                Error = string.Join(Environment.NewLine, ex.Issues.Select(i => new WatchdogIssueViewModel(i, _services.Localizer).DisplayText));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Success = false;
            SummaryText = _services.Localizer[L.Watchdog.TestNotSent];
            Error = _services.Connection.Describe(ex);
        }
        finally
        {
            if (_runs.IsCurrent(token) || token.IsCancellationRequested)
            {
                IsRunning = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _runs.Cancel();

    private void Show(DeliveryTestResult result)
    {
        var localizer = _services.Localizer;
        Success = result.Success;
        var duration = WatchdogDisplay.FormatDuration(result.Duration, localizer.Culture);
        SummaryText = localizer.Format(result.Success ? L.Watchdog.TestSucceeded : L.Watchdog.TestFailed, duration);
        foreach (var stage in result.Stages)
        {
            Stages.Add(new DeliveryStageRowViewModel(stage, localizer));
        }

        foreach (var hint in result.Hints)
        {
            Hints.Add(localizer.HintText(hint));
        }
    }
}
