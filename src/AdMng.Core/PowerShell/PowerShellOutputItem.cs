using System.Security;

namespace AdMng.Core.PowerShell;

/// <summary>
/// One thing a run hands to the window, in order (docs/dev/r1-powershell-window.md 4.4). The window renders text,
/// answers prompts and ends its operation at <see cref="PowerShellRunEndItem"/>. Nothing here is ever written to a file.
/// </summary>
public abstract record PowerShellOutputItem;

/// <summary>
/// Text the host was asked to write; <see cref="Text"/> includes its line ends and no "WARNING:"-like prefix. It may hold
/// directory values, so <c>ToString()</c> shows only the stream and the length (CLAUDE.md rule 1: nothing of it in a log).
/// </summary>
public sealed record PowerShellTextItem(
    PowerShellStream Stream,
    string Text,
    ConsoleColor? Foreground = null,
    ConsoleColor? Background = null) : PowerShellOutputItem
{
    public override string ToString() => $"PowerShellTextItem({Stream}, {Text.Length} characters)";
}

/// <summary><c>Write-Progress</c>; <c>ToString()</c> shows no activity or status text (they may hold directory values).</summary>
public sealed record PowerShellProgressItem(
    int ActivityId,
    string Activity,
    string Status,
    int PercentComplete,
    bool Completed) : PowerShellOutputItem
{
    public override string ToString() => $"PowerShellProgressItem({ActivityId}, {PercentComplete} %, completed {Completed})";
}

/// <summary>
/// In object mode only: an object the pipeline emitted, unformatted. <see cref="Value"/> is the engine's object (a
/// <c>PSObject</c>); the contract does not name the type so that Core stays free of PowerShell. <c>ToString()</c> shows
/// the type only (the object may be a directory object).
/// </summary>
public sealed record PowerShellObjectItem(object? Value) : PowerShellOutputItem
{
    public override string ToString() => $"PowerShellObjectItem({Value?.GetType().Name ?? "null"})";
}

/// <summary>
/// Always the last item of a run; <see cref="HadErrors"/> is true when any error was written (also a non-terminating one) - a stop of
/// the operator is not an error. <see cref="ExitCode"/> is set when the script ended with <c>exit</c>: the run ends there and the window
/// stays open.
/// </summary>
public sealed record PowerShellRunEndItem(PowerShellRunState State, bool HadErrors, int? ExitCode = null) : PowerShellOutputItem;

/// <summary>The command cleared the window (<c>Clear-Host</c>, <c>cls</c>): what came before goes, like Ctrl+L; what follows is shown.</summary>
public sealed record PowerShellClearItem : PowerShellOutputItem
{
    public static PowerShellClearItem Instance { get; } = new();

    public override string ToString() => nameof(PowerShellClearItem);
}

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
