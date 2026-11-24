using System.Management.Automation.Language;

namespace AdMng.PowerShell.Guards;

/// <summary>
/// What the window refuses from the text of a command before anything of it runs (docs/dev/r1-powershell-window.md 4.6): the three
/// constructs that reach the engine without looking up a command name, so <see cref="CommandGuards"/> cannot see them -
/// <c>using module</c> / <c>using assembly</c> (they import a module or load an assembly directly; a binary module runs .NET code even
/// in ConstrainedLanguage), the background operator <c>&amp;</c> (it starts the engine's Start-Job directly) and redirection to a file
/// (<c>&gt;</c>, <c>&gt;&gt;</c>, <c>2&gt;</c>: the engine's Out-File). <c>&gt; $null</c> and <c>2&gt;&amp;1</c> stay. Nested script
/// blocks are searched too. Text cannot become code past this check: there is no Invoke-Expression and no <c>function:</c> drive, and a
/// string does not convert to a script block in ConstrainedLanguage. Besides, a typed <c>ForEach-Object -Parallel</c> (or <c>-AsJob</c>
/// and the other parallel parameters) gets a message saying why: the window's ForEach-Object has no such parameter, so this is only
/// the explanation, not the protection (a splatted one fails in parameter binding).
/// </summary>
internal static class InputGuard
{
    internal const string UsingMessage =
        "'using module' and 'using assembly' are not available in the AD Manager PowerShell window: it loads no module and no assembly (the list of modules Import-Module may load is empty). Nothing of the command was run.";

    internal const string BackgroundMessage =
        "The background operator '&' at the end of a pipeline is not available in the AD Manager PowerShell window: a background job would keep running after the command ends. Nothing of the command was run.";

    internal const string ParallelMessage =
        "ForEach-Object -Parallel and -AsJob are not available in the AD Manager PowerShell window: their script blocks would run in runspaces of their own, outside the window's language mode, and a job would keep running after the command ends. Use ForEach-Object without them. Nothing of the command was run.";

    internal const string RedirectionMessage =
        "Redirecting output to a file ('>', '>>') is not available in the AD Manager PowerShell window: it writes a file only through Export-AdmList or Start-Transcript -Path ('> $null' still discards output). Nothing of the command was run.";

    internal const string TooComplexMessage =
        "The command is nested too deeply for the PowerShell parser, so the AD Manager PowerShell window cannot check it. Nothing of the command was run.";

    /// <summary>The message refusing <paramref name="script"/>, or null when nothing in it is refused.</summary>
    public static string? Refusal(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var ast = Parser.ParseInput(script, out _, out _);
        if (ast is null)
        {
            // The parser gave up (about 4 500 nested parentheses: "ScriptTooComplicated") and there is nothing to look into.
            return TooComplexMessage;
        }

        if (ast.Find(IsLoad, searchNestedScriptBlocks: true) is not null)
        {
            return UsingMessage;
        }

        if (ast.Find(IsBackground, searchNestedScriptBlocks: true) is not null)
        {
            return BackgroundMessage;
        }

        if (ast.Find(IsFileRedirection, searchNestedScriptBlocks: true) is not null)
        {
            return RedirectionMessage;
        }

        return ast.Find(IsParallelForEach, searchNestedScriptBlocks: true) is not null ? ParallelMessage : null;
    }

    private static bool IsLoad(Ast ast) =>
        ast is UsingStatementAst { UsingStatementKind: UsingStatementKind.Module or UsingStatementKind.Assembly };

    private static bool IsBackground(Ast ast) => ast is PipelineAst { Background: true } or PipelineChainAst { Background: true };

    /// <summary>
    /// <c>ForEach-Object</c> (or <c>%</c>, <c>foreach</c>) with a parameter of the engine's parallel set, also abbreviated as far as the
    /// engine accepts it (<c>-Pa</c>, <c>-As</c>, <c>-Th</c>, <c>-Ti</c>, <c>-Us</c> are unambiguous among ForEach-Object's parameters).
    /// </summary>
    private static bool IsParallelForEach(Ast ast) =>
        ast is CommandAst command
        && command.GetCommandName() is { } name
        && (name.Equals("ForEach-Object", StringComparison.OrdinalIgnoreCase) || name is "%" || name.Equals("foreach", StringComparison.OrdinalIgnoreCase))
        && command.CommandElements.OfType<CommandParameterAst>().Any(parameter => ParallelParameters.Any(p =>
            parameter.ParameterName.Length >= 2
            && p.StartsWith(parameter.ParameterName, StringComparison.OrdinalIgnoreCase)));

    private static readonly string[] ParallelParameters = ["Parallel", "AsJob", "ThrottleLimit", "TimeoutSeconds", "UseNewRunspace"];

    private static bool IsFileRedirection(Ast ast) =>
        ast is FileRedirectionAst redirection
        && !(redirection.Location is VariableExpressionAst variable && variable.VariablePath.IsVariable && string.Equals(variable.VariablePath.UserPath, "null", StringComparison.OrdinalIgnoreCase));
}
