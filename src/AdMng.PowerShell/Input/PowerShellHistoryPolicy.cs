using System.Management.Automation.Language;

namespace AdMng.PowerShell.Input;

/// <summary>
/// Which lines the window's history may remember (docs/dev/r1-powershell-window.md 4.9; the history itself lives in memory
/// in the view model). A line that carries a plain-text secret must not be recalled with the arrow keys, shown on a shared
/// screen or copied around; the filter is the one PSReadLine applies, made more exact by the AST. Pure and stateless.
/// </summary>
public static class PowerShellHistoryPolicy
{
    /// <summary>A parameter whose name contains one of these, followed by a literal, makes the line sensitive.</summary>
    private static readonly string[] SecretWords = ["password", "passwd", "secret", "token", "apikey"];

    /// <summary>
    /// False for an empty line, for input that is still incomplete, and for a line with a plain-text password: the
    /// <c>-AsPlainText</c> parameter (also its prefixes of three letters or more), a <c>ConvertTo-SecureString</c> with a
    /// literal argument, or a named parameter with <c>Password</c> (or another secret word) in its name whose argument is a
    /// literal. A variable, a subexpression or <c>Read-Host -AsSecureString</c> as the argument is fine.
    /// </summary>
    public static bool ShouldRemember(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        PowerShellEnvironment.Apply();
        var ast = Parser.ParseInput(line, out var tokens, out var errors);
        if (errors.Any(error => error.IncompleteInput))
        {
            return false;
        }

        // Tokens survive a parse error, so the plain-text parameter is found in a broken line as well.
        if (tokens.Any(IsPlainTextParameter))
        {
            return false;
        }

        foreach (var command in ast.FindAll(node => node is CommandAst, searchNestedScriptBlocks: true).Cast<CommandAst>())
        {
            if (IsSensitive(command))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlainTextParameter(Token token) =>
        token.Kind == TokenKind.Parameter && token is ParameterToken { ParameterName.Length: >= 3 } parameter
            && "AsPlainText".StartsWith(parameter.ParameterName, StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitive(CommandAst command)
    {
        var elements = command.CommandElements;
        var isSecureStringConversion = string.Equals(command.GetCommandName(), "ConvertTo-SecureString", StringComparison.OrdinalIgnoreCase);
        for (var i = 1; i < elements.Count; i++)
        {
            var element = elements[i];
            if (element is CommandParameterAst parameter)
            {
                var argument = parameter.Argument ?? (i + 1 < elements.Count && elements[i + 1] is not CommandParameterAst ? elements[i + 1] : null);
                if (argument is not null && IsLiteral(argument) && HasSecretWord(parameter.ParameterName))
                {
                    return true;
                }

                if (argument is not null && IsLiteral(argument) && isSecureStringConversion
                    && "String".StartsWith(parameter.ParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (isSecureStringConversion && IsLiteral(element) && !IsArgumentOfParameter(elements, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsArgumentOfParameter(IReadOnlyList<CommandElementAst> elements, int index) =>
        index > 1 && elements[index - 1] is CommandParameterAst { Argument: null };

    private static bool HasSecretWord(string parameterName) =>
        SecretWords.Any(word => parameterName.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool IsLiteral(Ast node) => node switch
    {
        ConstantExpressionAst => true,
        ExpandableStringExpressionAst expandable => expandable.NestedExpressions.Count == 0,
        _ => false,
    };
}
