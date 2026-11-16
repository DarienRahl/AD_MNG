using System.Security;

namespace AdMng.Core.PowerShell;

/// <summary>
/// One thing a run hands to the window, in order (docs/dev/r1-powershell-window.md 4.4). The window renders text,
/// answers prompts and ends its operation at <see cref="PowerShellRunEndItem"/>. Nothing here is ever written to a file.
/// </summary>
public abstract record PowerShellOutputItem;

/// <summary>Text the host was asked to write; <see cref="Text"/> includes its line ends and no "WARNING:"-like prefix.</summary>
public sealed record PowerShellTextItem(
    PowerShellStream Stream,
    string Text,
    ConsoleColor? Foreground = null,
    ConsoleColor? Background = null) : PowerShellOutputItem;

/// <summary><c>Write-Progress</c>.</summary>
public sealed record PowerShellProgressItem(
    int ActivityId,
    string Activity,
    string Status,
    int PercentComplete,
    bool Completed) : PowerShellOutputItem;

/// <summary>
/// In object mode only: an object the pipeline emitted, unformatted. <see cref="Value"/> is the engine's object (a
/// <c>PSObject</c>); the contract does not name the type so that Core stays free of PowerShell.
/// </summary>
public sealed record PowerShellObjectItem(object? Value) : PowerShellOutputItem;

/// <summary>Always the last item of a run; <see cref="HadErrors"/> is true when any error was written (also a non-terminating one).</summary>
public sealed record PowerShellRunEndItem(PowerShellRunState State, bool HadErrors) : PowerShellOutputItem;

/// <summary>One choice of a <see cref="PowerShellPromptKind.Choice"/> prompt; <see cref="Label"/> may carry an '&amp;' accelerator.</summary>
public sealed record PowerShellChoice(string Label, string HelpMessage);

/// <summary>
/// A question the pipeline waits on: the consumer of the run's items answers it (<see cref="Respond"/>,
/// <see cref="RespondSecret"/>, <see cref="RespondChoice"/>) or <see cref="Cancel"/>s it, and the pipeline goes on. A stop
/// cancels it too. The answer of a secret prompt lives in memory only; <c>ToString()</c> shows no answer.
/// </summary>
public sealed record PowerShellPromptItem : PowerShellOutputItem
{
    private readonly TaskCompletionSource<object?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PowerShellPromptItem(
        PowerShellPromptKind kind,
        string? caption,
        string? message,
        string? label,
        IReadOnlyList<PowerShellChoice>? choices = null,
        int defaultChoice = -1)
    {
        Kind = kind;
        Caption = caption;
        Message = message;
        Label = label;
        Choices = choices ?? [];
        DefaultChoice = defaultChoice;
    }

    public PowerShellPromptKind Kind { get; }

    public string? Caption { get; }

    public string? Message { get; }

    /// <summary>The prompt text of a text or secret prompt (what comes before the cursor).</summary>
    public string? Label { get; }

    public IReadOnlyList<PowerShellChoice> Choices { get; }

    /// <summary>The preselected choice, or -1.</summary>
    public int DefaultChoice { get; }

    /// <summary>True once answered or cancelled.</summary>
    public bool IsAnswered => _answer.Task.IsCompleted;

    public void Respond(string? text) => _answer.TrySetResult(text ?? string.Empty);

    public void RespondSecret(SecureString? value) => _answer.TrySetResult(value);

    public void RespondChoice(int index) => _answer.TrySetResult(index);

    /// <summary>The operator declined to answer (the pipeline is stopped).</summary>
    public void Cancel() => _answer.TrySetResult(null);

    /// <summary>Waits for the answer on the pipeline thread (never the UI thread); null when cancelled.</summary>
    public object? Wait(CancellationToken cancellationToken)
    {
        try
        {
            return _answer.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The kind only: neither the question's text nor the answer.</summary>
    public override string ToString() => $"PowerShellPromptItem({Kind})";
}
