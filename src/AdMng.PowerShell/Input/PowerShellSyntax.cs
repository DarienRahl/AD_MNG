using System.Management.Automation.Language;
using AdMng.Core.PowerShell;

namespace AdMng.PowerShell.Input;

/// <summary>
/// The parser's view of what the operator typed (docs/dev/r1-powershell-window.md 4.9): highlight spans and whether the
/// input is complete. Pure and stateless - it needs no runspace, so the editor can call it on every keystroke.
/// </summary>
public static class PowerShellSyntax
{
    /// <summary>
    /// Spans of the input with their kind, in order, from the parser's tokens (<see cref="InputParser"/>). Whitespace and brackets are
    /// not listed; a text too long to parse has none.
    /// </summary>
    public static IReadOnlyList<PowerShellSyntaxSpan> Tokenize(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tokens = InputParser.Parse(input).Tokens;
        var spans = new List<PowerShellSyntaxSpan>(tokens.Count);
        foreach (var token in tokens)
        {
            var kind = KindOf(token);
            var length = token.Extent.EndOffset - token.Extent.StartOffset;
            if (kind != PowerShellSyntaxKind.Other && length > 0)
            {
                spans.Add(new PowerShellSyntaxSpan(token.Extent.StartOffset, length, kind));
            }
        }

        return spans;
    }

    /// <summary>
    /// False when the parser stops at the end of the input because something is still open: a quote, a bracket, a script
    /// block, a here-string, a trailing pipe. Enter then inserts a line instead of running. Syntax errors elsewhere do not
    /// count: such a line is complete and fails when it runs; so is a text too long or too deep to parse, which the guard refuses.
    /// </summary>
    public static bool IsComplete(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return !InputParser.Parse(input).Errors.Any(error => error.IncompleteInput);
    }

    private static PowerShellSyntaxKind KindOf(Token token)
    {
        var flags = token.TokenFlags;
        if (flags.HasFlag(TokenFlags.CommandName))
        {
            return PowerShellSyntaxKind.Command;
        }

        if (flags.HasFlag(TokenFlags.Keyword))
        {
            return PowerShellSyntaxKind.Keyword;
        }

        if (flags.HasFlag(TokenFlags.TypeName))
        {
            return PowerShellSyntaxKind.Type;
        }

        if (flags.HasFlag(TokenFlags.MemberName))
        {
            return PowerShellSyntaxKind.Member;
        }

        if (flags.HasFlag(TokenFlags.BinaryOperator) || flags.HasFlag(TokenFlags.UnaryOperator) || flags.HasFlag(TokenFlags.AssignmentOperator))
        {
            return PowerShellSyntaxKind.Operator;
        }

        return token.Kind switch
        {
            TokenKind.Parameter => PowerShellSyntaxKind.Parameter,
            TokenKind.Variable or TokenKind.SplattedVariable => PowerShellSyntaxKind.Variable,
            TokenKind.StringLiteral or TokenKind.StringExpandable or TokenKind.HereStringLiteral or TokenKind.HereStringExpandable => PowerShellSyntaxKind.String,
            TokenKind.Number => PowerShellSyntaxKind.Number,
            TokenKind.Comment => PowerShellSyntaxKind.Comment,
            _ => PowerShellSyntaxKind.Other,
        };
    }
}
