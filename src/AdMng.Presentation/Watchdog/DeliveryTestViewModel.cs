using System.Collections.ObjectModel;
using AdMng.Presentation.Feedback;
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
/// <remarks>
/// The test is an operation of the channels tab (track V, stage V2b-4, unit 4): it says "working" with Stop in the status
/// line of the holder it is given (a test started while another one runs replaces it, as a second test of one editor always
/// did), and how it ended - done, failed with the stages in the rows below, not sent and why, or stopped - in the same line.
/// The rows keep the result of each stage; the hints stay notes of the card. The test only sends what the service would
/// send for a test; how it is made, validated and audited did not change (CLAUDE.md rule 5), and no secret is ever said.
/// </remarks>
public sealed partial class DeliveryTestViewModel : ObservableObject
{
    private readonly WatchdogServices _services;
    private readonly PageStatusHolder _status;
    private readonly SerialCancellation _runs = new();
    private CancellationToken _latest;
    private OperationScope? _scope;

    /// <param name="services">The tab's services.</param>
    /// <param name="status">The holder the test says its status in (the channels tab's); a standalone test makes its own.</param>
    public DeliveryTestViewModel(WatchdogServices services, PageStatusHolder? status = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _status = status ?? services.CreateStatus();
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
        var localizer = _services.Localizer;
        var token = _runs.Next();
        _latest = token;
        using var scope = _status.Begin(localizer[L.Watchdog.TestRunning], new PageStatusLog(L.Watchdog.TestRunning));
        _scope = scope;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, scope.Token);
        IsRunning = true;
        Error = null;
        Success = null;
        SummaryText = localizer[L.Watchdog.TestRunning];
        Stages.Clear();
        Hints.Clear();
        try
        {
            var result = await send(linked.Token).ConfigureAwait(true);
            linked.Token.ThrowIfCancellationRequested();
            Show(result);
            Say(scope, result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && linked.IsCancellationRequested)
        {
            // The operator's Stop, a newer test (the client reports the cancelled request as an OperationCanceledException
            // or as a WatchdogClientException of kind Cancelled) or the end of the tab: not a failed test. A newer test owns
            // the texts.
            if (_latest == token)
            {
                SummaryText = localizer[L.Errors.Cancelled];
                if (scope.IsCancelling)
                {
                    scope.CompleteAttention(localizer[L.WatchdogChannelsStatus.TestStopped], new PageStatusLog(L.WatchdogChannelsStatus.TestStopped));
                }
            }
        }
        catch (WatchdogClientException ex) when (ex.Kind == WatchdogClientErrorKind.Validation)
        {
            Success = false;
            SummaryText = localizer[L.Watchdog.TestNotSent];
            var rebind = ex.Issues.FirstOrDefault(i => i.Code == "secretRebindRequired");
            if (rebind is not null)
            {
                Error = localizer[L.Watchdog.TestRebindRequired];
                Hints.Add(localizer.HintText(DeliveryHints.SecretRebindRequired));
                SecretRebindRequired?.Invoke(this, rebind.Path);
            }
            else
            {
                Error = string.Join(Environment.NewLine, ex.Issues.Select(i => new WatchdogIssueViewModel(i, localizer).DisplayText));
            }

            // Nothing was sent, and why: what the operator has to put right (the line is one flowing text).
            scope.CompleteAttention(
                PageStatusText.Failure(localizer[L.Watchdog.TestNotSent], Error.ReplaceLineEndings(" "), null, ResultCode.Of(ex)),
                new PageStatusLog(L.Watchdog.TestNotSent).WithCode(ResultCode.Of(ex)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Success = false;
            SummaryText = localizer[L.Watchdog.TestNotSent];
            Error = _services.Connection.Describe(ex);
            scope.CompleteError(
                PageStatusText.Failure(localizer[L.Watchdog.TestNotSent], Error, null, ResultCode.Of(ex)),
                new PageStatusLog(L.Watchdog.TestNotSent).WithCode(ResultCode.Of(ex)));
        }
        finally
        {
            if (_latest == token)
            {
                IsRunning = false;
            }

            if (ReferenceEquals(_scope, scope))
            {
                _scope = null;
            }
        }
    }

    /// <summary>The test is over (the tab was closed): what still runs ends without a word.</summary>
    public void End() => _scope?.Dispose();

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        // The line answers "Stopping…" first, then the call is cancelled.
        _scope?.Cancel();
        _runs.Cancel();
    }

    /// <summary>How a test that ran ended: done, or failed with the stages in the rows.</summary>
    private void Say(OperationScope scope, DeliveryTestResult result)
    {
        var localizer = _services.Localizer;
        var duration = WatchdogDisplay.FormatDuration(result.Duration, localizer.Culture);
        if (result.Success)
        {
            scope.CompleteDone(localizer.Format(L.Watchdog.TestSucceeded, duration), new PageStatusLog(L.Watchdog.TestSucceeded));
        }
        else
        {
            scope.CompleteError(
                PageStatusText.Failure(localizer.Format(L.Watchdog.TestFailed, duration), null, localizer[L.WatchdogChannelsStatus.TestFailedNext]),
                new PageStatusLog(L.Watchdog.TestFailed, result.Stages.Count));
        }
    }

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
