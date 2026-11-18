using AdMng.Core.PowerShell;

namespace AdMng.Presentation.PowerShell;

/// <summary>
/// A question the running command asks and waits for (<c>Read-Host</c>, a missing mandatory parameter, <c>-Confirm</c>): what the
/// window shows while it is pending. The question is also in the scrollback; the answer is typed into the editor, which takes it
/// as an answer (<see cref="PowerShellInputMode"/>) - never as a command, and never into the history.
/// </summary>
public sealed class PowerShellPromptState
{
    internal PowerShellPromptState(PowerShellPromptItem item)
    {
        Item = item;
    }

    internal PowerShellPromptItem Item { get; }

    public PowerShellPromptKind Kind => Item.Kind;

    public string? Caption => Item.Caption;

    public string? Message => Item.Message;

    /// <summary>What comes before the answer ("Name: ").</summary>
    public string? Label => Item.Label;

    public IReadOnlyList<PowerShellChoice> Choices => Item.Choices;

    /// <summary>The choice an empty answer takes, or -1.</summary>
    public int DefaultChoice => Item.DefaultChoice;

    /// <summary>The kind only: the question and the answer may hold directory values or a password.</summary>
    public override string ToString() => $"PowerShellPromptState({Kind})";
}

/// <summary>
/// The place the window keeps for the pending change plan of stage R2 (docs/dev/r1-powershell-window.md 6): collapsed and empty, so
/// that R2 adds its panel - the number of operations, Preview, Execute, Clear - without changing the layout. R1 has no command
/// that writes, so nothing in it ever makes the area visible.
/// </summary>
public sealed class PowerShellPendingPlanState
{
    /// <summary>The area is shown. Always false in R1.</summary>
    public bool IsVisible => OperationCount > 0;

    /// <summary>The operations waiting in the plan. Always 0 in R1.</summary>
    public int OperationCount { get; }
}
