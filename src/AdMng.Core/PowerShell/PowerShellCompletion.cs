namespace AdMng.Core.PowerShell;

/// <summary>One completion match: what replaces the span, what the list shows and a tool tip.</summary>
public sealed record PowerShellCompletionMatch(string Text, string ListText, PowerShellCompletionKind Kind, string? ToolTip);

/// <summary>The matches for an input and the span of the input they replace.</summary>
public sealed record PowerShellCompletionResult(int ReplacementIndex, int ReplacementLength, IReadOnlyList<PowerShellCompletionMatch> Matches)
{
    public static PowerShellCompletionResult Empty { get; } = new(0, 0, []);
}

/// <summary>A span of the input and what it is, from the parser's tokens.</summary>
public readonly record struct PowerShellSyntaxSpan(int Start, int Length, PowerShellSyntaxKind Kind);
