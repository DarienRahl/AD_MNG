using System.Management.Automation.Language;
using System.Runtime.ExceptionServices;

namespace AdMng.PowerShell.Input;

/// <summary>
/// The one way the window parses what the operator typed (docs/dev/r1-powershell-window.md 4.6, 4.9): the guard, the history policy,
/// the highlighting, the end-of-input check and completion all go through it, and the engine runs only what passed it. PowerShell's
/// parser guards its own recursion by the stack that is left - so whether it gives up on a line ("ScriptTooComplicated", no tree) depends
/// on the thread and the build: 5 000 nested parentheses gave no tree in a Debug build here and parsed and ran on the Release build of
/// CI. A chain of operators is parsed in a loop, and every walk over its tree afterwards recurses once per operator - the parser's own
/// checks after the parse, the engine's compiler, the history filter. A .NET stack overflow cannot be caught: it ends the process, on a
/// workstation with every window of the program. Measured on Linux (third red team of R1, goal h): <c>$x = 1+1+…</c> with about
/// 16 000 terms ended a parse on a stack of 1 MB (the default of a thread on Windows, the window's UI thread among them), about 9 300
/// terms ended the engine's compiler when the command ran, and <c>$password = 'x'+'x'+…</c> with about 3 000 terms ended the history
/// filter. So the limits do not depend on the stack: a text longer than <see cref="MaxLength"/> is not parsed, a text longer than
/// <see cref="InlineLength"/> is parsed on a thread with a stack of its own (<see cref="LargeStack"/>), and a tree deeper than
/// <see cref="MaxDepth"/> counts as no tree, as does a text the parser gave up on. A text without a tree is refused by the guard, never
/// remembered and not completed - the same text on every machine.
/// </summary>
internal static class InputParser
{
    /// <summary>The longest text the window parses (256 Ki characters); a longer one is refused without being parsed.</summary>
    internal const int MaxLength = 256 * 1024;

    /// <summary>
    /// The deepest syntax tree the window accepts: 500 levels, about 18 times below the depth that ended the engine's compiler. A
    /// parenthesis takes three levels, a script block about five, an operator of a chain one; a command an operator types stays far below.
    /// </summary>
    internal const int MaxDepth = 500;

    /// <summary>A text of up to 4 Ki characters is parsed on the caller's thread: its tree is at most about 2 000 levels deep.</summary>
    internal const int InlineLength = 4 * 1024;

    /// <summary>The stack of the thread that parses a longer text: 64 MB, eight times what a chain of <see cref="MaxLength"/> needs.</summary>
    private const int LargeStack = 64 * 1024 * 1024;

    /// <summary>The tokens, errors and - unless the text is too long or too deep - the syntax tree of <paramref name="input"/>.</summary>
    public static ParsedInput Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        PowerShellEnvironment.Apply();
        if (input.Length > MaxLength)
        {
            return ParsedInput.TooLong;
        }

        if (input.Length <= InlineLength)
        {
            return ParseHere(input);
        }

        ParsedInput? parsed = null;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    parsed = ParseHere(input);
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            },
            LargeStack)
        {
            IsBackground = true,
            Name = "AD Manager PowerShell input parser",
        };

        try
        {
            thread.Start();
        }
        catch (OutOfMemoryException)
        {
            // No thread with that stack could be made: the text is not parsed, as if it were too long.
            return ParsedInput.TooLong;
        }

        thread.Join();
        failure?.Throw();
        return parsed!;
    }

    private static ParsedInput ParseHere(string input)
    {
        var ast = Parser.ParseInput(input, out var tokens, out var errors);
        return new ParsedInput(ast is not null && !IsTooDeep(ast) ? ast : null, tokens, errors);
    }

    /// <summary>
    /// Whether a node lies deeper than <see cref="MaxDepth"/> below the root, nested script blocks included. The search visits a node
    /// before its children and stops at the first one too deep, so it never recurses much further than that. A node's depth is counted
    /// along its parents, up to the first one already counted (normally its own parent), so a node whose parent the search did not visit
    /// is not counted short.
    /// </summary>
    internal static bool IsTooDeep(Ast root)
    {
        var depths = new Dictionary<Ast, int>(ReferenceEqualityComparer.Instance) { [root] = 0 };
        return root.Find(
            node =>
            {
                var depth = 0;
                for (var above = node; !ReferenceEquals(above, root) && above.Parent is { } parent; above = parent)
                {
                    depth++;
                    if (depths.TryGetValue(parent, out var known))
                    {
                        depth += known;
                        break;
                    }
                }

                depths[node] = depth;
                return depth > MaxDepth;
            },
            searchNestedScriptBlocks: true) is not null;
    }
}

/// <summary>What <see cref="InputParser"/> made of a text: <see cref="Ast"/> is null when it is too long, too deep or the parser gave up.</summary>
internal sealed record ParsedInput(ScriptBlockAst? Ast, IReadOnlyList<Token> Tokens, IReadOnlyList<ParseError> Errors)
{
    /// <summary>A text that was not parsed: no tree, no tokens, no errors.</summary>
    public static ParsedInput TooLong { get; } = new(null, [], []);
}
