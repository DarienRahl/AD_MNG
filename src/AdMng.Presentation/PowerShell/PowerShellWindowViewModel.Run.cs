using System.Security;
using AdMng.Core.PowerShell;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.PowerShell;

// Running a command: the operation on the status line, the stream of the engine's items, the questions of the command, Stop.
public sealed partial class PowerShellWindowViewModel
{
    /// <summary>How many items are shown to the window at once: a result of 100,000 objects is shown in steps, never in one freeze.</summary>
    internal const int BatchSize = 500;

    /// <summary>The longest first line of an error that goes into the status line.</summary>
    private const int ErrorReasonLength = 240;

    private OperationScope? _scope;
    private string? _firstError;
    private PowerShellRunEndItem? _end;

    /// <summary>
    /// Enter in the editor: an open completion list or search takes its choice, the answer to a question of the running command is
    /// sent, a complete input is run and an incomplete one (an open quote, a bracket, a trailing pipe) gets a new line.
    /// </summary>
    [RelayCommand]
    private void Submit()
    {
        if (IsDisposed)
        {
            return;
        }

        if (PendingPrompt is not null && !Input.IsCompletionOpen && !Input.IsSearching)
        {
            AnswerPrompt();
            return;
        }

        if (Input.PressEnter() == PowerShellEnterAction.Run && RunCommand.CanExecute(null))
        {
            RunCommand.Execute(null);
        }
    }

    /// <summary>The command of the editor's button: runs the text of the editor.</summary>
    private bool CanRun() => IsReady && !IsRunning && !IsStarting && !Input.IsBlank && !Input.IsAnswering;

    /// <summary>
    /// Runs the text of the editor in the window's runspace. The command is an operation of the status line that can be stopped; its
    /// text goes to the scrollback as it arrives, in steps, and what it did is the result: done, attention (errors, a stop) or
    /// error (the command failed). A command a window runs is asked about when the window is closed meanwhile, whatever it does.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (_session is not { } session || IsRunning || IsDisposed || Input.IsBlank)
        {
            return;
        }

        var script = Input.Text;
        Output.AddCommand(script, PromptText, ContinuationText);
        History.Add(script);
        Input.Clear();
        if (!session.IsRunning)
        {
            session.OutputWidth = _outputWidth;
        }

        _firstError = null;
        _end = null;
        var linesBefore = Output.OutputLinesWritten;
        var started = Services.Time.GetUtcNow();
        IsRunning = true;
        NotifyAvailability();
        using var scope = Status.Begin(
            Localizer[L.PowerShellStatus.Running], new PageStatusLog(L.PowerShellStatus.Running), canCancel: true, writes: OperationWrites.Other);
        _scope = scope;
        try
        {
            await Task.Run(() => PumpAsync(session, script, scope.Token), CancellationToken.None).ConfigureAwait(true);
            if (!IsDisposed)
            {
                Finish(scope, started, linesBefore);
            }
        }
        catch (OperationCanceledException) when (scope.Token.IsCancellationRequested)
        {
            // An engine that ends a stopped run by throwing is a stopped run too.
            if (!IsDisposed)
            {
                Finish(scope, started, linesBefore);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!IsDisposed)
            {
                Output.EndRun();
                scope.CompleteError(
                    PageStatusText.Failure(Localizer[L.PowerShellStatus.CouldNotRun], ex.Message, Localizer[L.PowerShellStatus.FailedNext]),
                    new PageStatusLog(L.PowerShellStatus.CouldNotRun));
            }
        }
        finally
        {
            _scope = null;
            EndPrompt(answered: false);
            IsRunning = false;
            NotifyAvailability();
        }
    }

    /// <summary>Stop (the button, Ctrl+C): asks the running command to end; the line says "Stopping…" until it did. Nothing when no command runs.</summary>
    [RelayCommand]
    private void Stop()
    {
        PendingPrompt?.Item.Cancel();
        _scope?.Cancel();
    }

    /// <summary>Ctrl+L: clears the scrollback. The history of commands stays.</summary>
    [RelayCommand]
    private void Clear() => Output.Clear();

    /// <summary>A distinguished name of the scrollback was clicked: <i>Open in the program</i> (the properties of the object).</summary>
    [RelayCommand]
    private async Task OpenObjectAsync(string? distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName) || IsDisposed || IsSessionEnded)
        {
            return;
        }

        await Services.Dialogs.ShowPropertiesAsync(distinguishedName).ConfigureAwait(true);
    }

    // ---- The stream of the engine -------------------------------------------------------------------------------

    /// <summary>
    /// Reads the engine's items off the UI thread and hands them over in steps: whatever is ready when the next item would have to be
    /// waited for goes at once - so a line of output appears as soon as it was written - and a burst goes in batches of
    /// <see cref="BatchSize"/>. Each step waits until the UI thread has taken it, so a command that writes faster than the window
    /// can show is held back (the engine's own channel is bounded too) instead of piling up.
    /// </summary>
    private async Task PumpAsync(IPowerShellSession session, string script, CancellationToken stop)
    {
        var batch = new List<PowerShellOutputItem>();
        await using var items = session.RunAsync(script, stop).GetAsyncEnumerator(CancellationToken.None);
        while (true)
        {
            var next = items.MoveNextAsync();
            if (!next.IsCompleted && batch.Count > 0)
            {
                await Services.Dispatcher.InvokeAsync(ApplyTo(batch)).ConfigureAwait(false);
                batch = [];
            }

            if (!await next.ConfigureAwait(false))
            {
                break;
            }

            batch.Add(items.Current);
            if (batch.Count >= BatchSize)
            {
                await Services.Dispatcher.InvokeAsync(ApplyTo(batch)).ConfigureAwait(false);
                batch = [];
            }
        }

        if (batch.Count > 0)
        {
            await Services.Dispatcher.InvokeAsync(ApplyTo(batch)).ConfigureAwait(false);
        }
    }

    private Action ApplyTo(List<PowerShellOutputItem> batch) => () => ApplyBatch(batch);

    /// <summary>One step of the stream, on the UI thread: the text goes to the scrollback with one change, the rest is handled in order.</summary>
    private void ApplyBatch(List<PowerShellOutputItem> batch)
    {
        if (IsDisposed)
        {
            return;
        }

        var texts = new List<PowerShellTextItem>();
        foreach (var item in batch)
        {
            if (item is PowerShellTextItem text)
            {
                texts.Add(text);
                if (_firstError is null && text.Stream == PowerShellStream.Error)
                {
                    _firstError = FirstLine(text.Text);
                }

                continue;
            }

            if (texts.Count > 0)
            {
                Output.Add(texts);
                texts.Clear();
            }

            switch (item)
            {
                case PowerShellProgressItem progress:
                    ReportProgress(progress);
                    break;
                case PowerShellPromptItem prompt:
                    ShowPrompt(prompt);
                    break;
                case PowerShellRunEndItem end:
                    _end = end;
                    break;
            }
        }

        if (texts.Count > 0)
        {
            Output.Add(texts);
        }
    }

    private void ReportProgress(PowerShellProgressItem progress)
    {
        if (_scope is not { } scope || PendingPrompt is not null)
        {
            return;
        }

        if (progress.Completed)
        {
            scope.Report(Localizer[L.PowerShellStatus.Running], PageProgress.Indeterminate, new PageStatusLog(L.PowerShellStatus.Running));
            return;
        }

        var text = string.IsNullOrWhiteSpace(progress.Status)
            ? progress.Activity
            : Localizer.Format(L.PowerShellStatus.Progress, progress.Activity, progress.Status);
        scope.Report(
            string.IsNullOrWhiteSpace(text) ? Localizer[L.PowerShellStatus.Running] : text,
            progress.PercentComplete is >= 0 and <= 100 ? PageProgress.Of(progress.PercentComplete / 100.0) : PageProgress.Indeterminate,
            new PageStatusLog(L.PowerShellStatus.Progress));
    }

    /// <summary>How the command ended, as the result of its operation.</summary>
    private void Finish(OperationScope scope, DateTimeOffset started, long linesBefore)
    {
        Output.EndRun();
        var end = _end ?? new PowerShellRunEndItem(scope.Token.IsCancellationRequested ? PowerShellRunState.Stopped : PowerShellRunState.Completed, _firstError is not null);
        var elapsed = Services.Time.GetUtcNow() - started;
        var time = Localizer.Format(L.PowerShellStatus.Seconds, Math.Max(0, elapsed.TotalSeconds).ToString("0.0", Localizer.Culture));
        var lines = Output.OutputLinesWritten - linesBefore;
        var milliseconds = (long)Math.Max(0, elapsed.TotalMilliseconds);
        switch (end.State)
        {
            case PowerShellRunState.Stopped:
                scope.CompleteAttention(Localizer.Format(L.PowerShellStatus.Stopped, time), new PageStatusLog(L.PowerShellStatus.Stopped, milliseconds));
                break;
            case PowerShellRunState.Failed:
                scope.CompleteError(
                    PageStatusText.Failure(Localizer[L.PowerShellStatus.Failed], _firstError, Localizer[L.PowerShellStatus.FailedNext]),
                    new PageStatusLog(L.PowerShellStatus.Failed, lines, milliseconds));
                break;
            default:
                if (end.HadErrors)
                {
                    scope.CompleteAttention(
                        PageStatusText.Failure(Localizer[L.PowerShellStatus.WithErrors], _firstError, Localizer[L.PowerShellStatus.WithErrorsNext]),
                        new PageStatusLog(L.PowerShellStatus.WithErrors, lines, milliseconds));
                }
                else if (lines == 0)
                {
                    scope.CompleteDone(Localizer.Format(L.PowerShellStatus.DoneNoOutput, time), new PageStatusLog(L.PowerShellStatus.DoneNoOutput, milliseconds));
                }
                else
                {
                    scope.CompleteDone(
                        Localizer.Format(L.PowerShellStatus.Done, LinesText((int)Math.Min(lines, int.MaxValue)), time),
                        new PageStatusLog(L.PowerShellStatus.Done, lines, milliseconds));
                }

                break;
        }
    }

    private string LinesText(int count) =>
        PageStatusText.Count(Localizer, count, L.PowerShellStatus.LinesOne, L.PowerShellStatus.LinesFew, L.PowerShellStatus.LinesMany);

    /// <summary>The first line of an error text, shortened: what the status line says of an error (the whole text is in the window).</summary>
    private static string? FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length <= ErrorReasonLength ? trimmed : trimmed[..(ErrorReasonLength - 1)] + "…";
            }
        }

        return null;
    }

    // ---- The questions of the command ---------------------------------------------------------------------------

    /// <summary>The running command asks (<c>Read-Host</c>, a missing mandatory parameter, <c>-Confirm</c>): the question is in the scrollback and the editor takes the answer.</summary>
    private void ShowPrompt(PowerShellPromptItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Caption))
        {
            Output.AddLine(PowerShellLineKind.Prompt, item.Caption!);
        }

        if (!string.IsNullOrWhiteSpace(item.Message))
        {
            Output.AddLine(PowerShellLineKind.Prompt, item.Message!);
        }

        if (item.Kind == PowerShellPromptKind.Choice)
        {
            Output.AddLine(PowerShellLineKind.Prompt, ChoiceMenu(item));
        }

        Input.SetMode(item.Kind == PowerShellPromptKind.Secret ? PowerShellInputMode.Secret : PowerShellInputMode.Answer);
        _scope?.Report(Localizer[L.PowerShellStatus.Waiting], PageProgress.Indeterminate, new PageStatusLog(L.PowerShellStatus.Waiting));

        // Last: whoever sees the question pending finds the scrollback, the editor's mode and the line already as they belong to it.
        PendingPrompt = new PowerShellPromptState(item);
        OnPropertyChanged(nameof(InputPrompt));
    }

    /// <summary>Sends what the editor holds as the answer to the pending question. A choice that is none of the choices asks again.</summary>
    private void AnswerPrompt()
    {
        if (PendingPrompt is not { } prompt)
        {
            return;
        }

        var item = prompt.Item;
        var text = Input.Text.TrimEnd('\r', '\n');
        switch (item.Kind)
        {
            case PowerShellPromptKind.Secret:
                // The echo has no answer in it: a secret is only ever in the SecureString that goes to the command.
                Output.AddLine(PowerShellLineKind.Prompt, item.Label ?? string.Empty);
                item.RespondSecret(ToSecureString(text));
                break;
            case PowerShellPromptKind.Choice:
                if (text == "?")
                {
                    ShowChoiceHelp(item);
                    Input.Clear();
                    return;
                }

                var index = ChoiceIndex(item, text);
                if (index < 0)
                {
                    Output.AddLine(PowerShellLineKind.Prompt, Localizer[L.PowerShell.PromptInvalid]);
                    Input.Clear();
                    return;
                }

                Output.AddLine(PowerShellLineKind.Prompt, "> " + ChoiceText(item.Choices[index].Label));
                item.RespondChoice(index);
                break;
            default:
                Output.AddLine(PowerShellLineKind.Prompt, (item.Label ?? string.Empty) + text);
                item.Respond(text);
                break;
        }

        EndPrompt(answered: true);
    }

    /// <summary>The question is over (answered, or the command ended): the editor takes commands again.</summary>
    private void EndPrompt(bool answered)
    {
        if (PendingPrompt is null)
        {
            return;
        }

        PendingPrompt = null;
        OnPropertyChanged(nameof(InputPrompt));
        Input.SetMode(PowerShellInputMode.Command);
        if (answered)
        {
            _scope?.Report(Localizer[L.PowerShellStatus.Running], PageProgress.Indeterminate, new PageStatusLog(L.PowerShellStatus.Running));
        }
    }

    private string ChoiceMenu(PowerShellPromptItem item)
    {
        var menu = string.Join("  ", item.Choices.Select(choice => $"[{ChoiceAccelerator(choice.Label)}] {ChoiceText(choice.Label)}"));
        menu += $"  [?] {Localizer[L.PowerShell.PromptHelp]}";
        return item.DefaultChoice >= 0 && item.DefaultChoice < item.Choices.Count
            ? $"{menu} {Localizer.Format(L.PowerShell.PromptDefault, ChoiceAccelerator(item.Choices[item.DefaultChoice].Label))}"
            : menu;
    }

    private void ShowChoiceHelp(PowerShellPromptItem item)
    {
        foreach (var choice in item.Choices)
        {
            Output.AddLine(PowerShellLineKind.Prompt, $"{ChoiceAccelerator(choice.Label)} - {choice.HelpMessage}");
        }

        Output.AddLine(PowerShellLineKind.Prompt, $"? - {Localizer[L.PowerShell.PromptHelp]}");
    }

    /// <summary>The index of the choice an answer names: its letter, its text, or - for an empty answer - the default; -1 for none.</summary>
    private static int ChoiceIndex(PowerShellPromptItem item, string answer)
    {
        answer = answer.Trim();
        if (answer.Length == 0)
        {
            return item.DefaultChoice >= 0 && item.DefaultChoice < item.Choices.Count ? item.DefaultChoice : -1;
        }

        for (var index = 0; index < item.Choices.Count; index++)
        {
            var label = item.Choices[index].Label;
            if (string.Equals(ChoiceAccelerator(label), answer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ChoiceText(label), answer, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The letter of a choice: the one after the '&amp;', else the first.</summary>
    internal static string ChoiceAccelerator(string label)
    {
        var amp = label.IndexOf('&', StringComparison.Ordinal);
        return amp >= 0 && amp + 1 < label.Length ? label[amp + 1].ToString() : label.Length > 0 ? label[0].ToString() : string.Empty;
    }

    /// <summary>The label without the '&amp;'.</summary>
    internal static string ChoiceText(string label) => label.Replace("&", string.Empty, StringComparison.Ordinal);

    private static SecureString ToSecureString(string text)
    {
        var secret = new SecureString();
        foreach (var character in text)
        {
            secret.AppendChar(character);
        }

        secret.MakeReadOnly();
        return secret;
    }
}
