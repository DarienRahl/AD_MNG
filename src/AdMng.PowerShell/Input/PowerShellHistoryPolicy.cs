using System.Management.Automation.Language;

namespace AdMng.PowerShell.Input;

/// <summary>
/// Which lines the window's history may remember (docs/dev/r1-powershell-window.md 4.9; the history itself lives in memory
/// in the view model). A line that carries a plain-text secret must not be recalled with the arrow keys, shown on a shared
/// screen or copied around; the filter follows PSReadLine's idea (a word list), made more exact by the AST and wider than its
/// list. It is a protection against recalling a secret, not a proof that none was typed: a secret given positionally to a
/// command whose name does not say so, one inside a longer text, an argument of a console program (<c>net user</c>) or a name
/// outside the lists below is remembered. Pure and stateless.
/// </summary>
public static class PowerShellHistoryPolicy
{
    /// <summary>A name (of a parameter, a hashtable key, a variable or a property) that contains one of these is a secret's name.</summary>
    private static readonly string[] SecretWords = ["password", "passwd", "passphrase", "pwd", "secret", "token", "apikey", "privatekey"];

    /// <summary>Names that are a secret's name only as the whole name: as parts of a name they are too common (<c>-Pinned</c>, <c>-KeyName</c>).</summary>
    private static readonly string[] SecretNames = ["pw", "pin", "key"];

    /// <summary>
    /// Abbreviations: a name of two letters or more that begins one of these (<c>-Pa</c>, <c>-Pas</c>, <c>-Pass</c>) is a secret's name -
    /// PowerShell binds every unambiguous prefix of a parameter's name to it.
    /// </summary>
    private static readonly string[] AbbreviatedWords = ["password", "passphrase"];

    /// <summary>The verbs of a command named after a secret that take a name, never the secret itself (<c>Get-Secret MySecret</c>).</summary>
    private static readonly string[] NamingVerbs = ["Get", "Find", "Search", "Show"];

    /// <summary>
    /// False for an empty line, for input that is still incomplete, and for a line with a plain-text secret: the
    /// <c>-AsPlainText</c> parameter (also its prefixes of three letters or more); a literal given to a parameter, a hashtable key
    /// (an inline or splatted hashtable) or assigned to a variable, property or index whose name is a secret's name
    /// (<c>Password</c>, <c>Passphrase</c>, <c>Pwd</c>, <c>Secret</c>, <c>Token</c>, <c>ApiKey</c>, <c>PrivateKey</c> anywhere in
    /// the name; <c>Pw</c>, <c>Pin</c>, <c>Key</c> as the whole name; <c>-Pa</c>, <c>-Pas</c>, <c>-Pass</c> and the other
    /// abbreviations of <c>Password</c>/<c>Passphrase</c>); a literal written positionally to <c>ConvertTo-SecureString</c> (or as
    /// its <c>-String</c>) or to a command whose noun is a secret's name (<c>Reset-AdmPassword user 'x'</c>), unless its verb only
    /// names one (<c>Get</c>, <c>Find</c>, <c>Search</c>, <c>Show</c>). A literal is also a value computed from literals only: in
    /// parentheses or a subexpression (<c>('x')</c>, <c>$('x')</c>), joined or cast (<c>'a' + 'b'</c>, <c>-join ('a', 'b')</c>,
    /// <c>[string]'x'</c>), or the value of a chained assignment (<c>$password = $x = 'x'</c>). A variable, a command or
    /// <c>Read-Host -AsSecureString</c> in the value is fine.
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
        if (ast is null || errors.Any(error => error.IncompleteInput))
        {
            return false;
        }

        // Tokens survive a parse error, so the plain-text parameter is found in a broken line as well.
        if (tokens.Any(IsPlainTextParameter))
        {
            return false;
        }

        return !ast.FindAll(IsSensitive, searchNestedScriptBlocks: true).Any();
    }

    private static bool IsPlainTextParameter(Token token) =>
        token.Kind == TokenKind.Parameter && token is ParameterToken { ParameterName.Length: >= 3 } parameter
            && "AsPlainText".StartsWith(parameter.ParameterName, StringComparison.OrdinalIgnoreCase);

    private static bool IsSensitive(Ast node) => node switch
    {
        CommandAst command => IsSensitive(command),
        HashtableAst hashtable => hashtable.KeyValuePairs.Any(pair => pair.Item1 is StringConstantExpressionAst key && IsSecretName(key.Value) && IsLiteral(pair.Item2)),
        AssignmentStatementAst assignment => NameOf(assignment.Left) is { } name && IsSecretName(name) && IsLiteral(assignment.Right),
        _ => false,
    };

    private static bool IsSensitive(CommandAst command)
    {
        var elements = command.CommandElements;
        var name = command.GetCommandName();
        var isSecureStringConversion = string.Equals(name, "ConvertTo-SecureString", StringComparison.OrdinalIgnoreCase);
        var takesSecretPositionally = isSecureStringConversion || IsNamedAfterSecret(name);
        for (var i = 1; i < elements.Count; i++)
        {
            var element = elements[i];
            if (element is CommandParameterAst parameter)
            {
                var argument = parameter.Argument ?? (i + 1 < elements.Count && elements[i + 1] is not CommandParameterAst ? elements[i + 1] : null);
                if (argument is not null && IsLiteral(argument) && IsSecretName(parameter.ParameterName))
                {
                    return true;
                }

                if (argument is not null && IsLiteral(argument) && isSecureStringConversion
                    && "String".StartsWith(parameter.ParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (takesSecretPositionally && IsLiteral(element) && !IsArgumentOfParameter(elements, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsArgumentOfParameter(IReadOnlyList<CommandElementAst> elements, int index) =>
        index > 1 && elements[index - 1] is CommandParameterAst { Argument: null };

    /// <summary>A command <c>Verb-Noun</c> whose noun is a secret's name and whose verb does more than name one.</summary>
    private static bool IsNamedAfterSecret(string? command)
    {
        var dash = command?.IndexOf('-', StringComparison.Ordinal) ?? -1;
        if (command is null || dash <= 0)
        {
            return false;
        }

        var verb = command[..dash];
        return !NamingVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase) && IsSecretName(command[(dash + 1)..]);
    }

    private static bool IsSecretName(string name) =>
        SecretWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase))
        || SecretNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || (name.Length >= 2 && AbbreviatedWords.Any(word => word.StartsWith(name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The name an assignment writes: a variable (without its scope or drive), a property, a constant index, through a type cast.</summary>
    private static string? NameOf(ExpressionAst target) => target switch
    {
        VariableExpressionAst variable => variable.VariablePath.UserPath[(variable.VariablePath.UserPath.LastIndexOf(':') + 1)..],
        MemberExpressionAst { Member: StringConstantExpressionAst member } => member.Value,
        IndexExpressionAst { Index: StringConstantExpressionAst index } => index.Value,
        AttributedExpressionAst attributed => NameOf(attributed.Child),
        _ => null,
    };

    /// <summary>A statement whose value is a literal: a pure expression, or a (chained) assignment of one.</summary>
    private static bool IsLiteral(StatementAst statement) => statement switch
    {
        PipelineAst pipeline => pipeline.GetPureExpression() is { } expression && IsLiteral(expression),
        CommandExpressionAst command => IsLiteral(command.Expression),
        AssignmentStatementAst assignment => IsLiteral(assignment.Right),
        _ => false,
    };

    /// <summary>
    /// A value typed as text: a constant, a string without embedded expressions, an array of these, or a value computed from them alone -
    /// in parentheses, in a subexpression of one statement, through an operator (<c>'a' + 'b'</c>, <c>-join</c>) or a cast.
    /// </summary>
    private static bool IsLiteral(Ast node) => node switch
    {
        ConstantExpressionAst => true,
        ExpandableStringExpressionAst expandable => expandable.NestedExpressions.Count == 0,
        ArrayLiteralAst array => array.Elements.All(IsLiteral),
        ParenExpressionAst paren => IsLiteral(paren.Pipeline),
        SubExpressionAst { SubExpression: { Statements.Count: 1, Traps: null } block } => IsLiteral(block.Statements[0]),
        BinaryExpressionAst binary => IsLiteral(binary.Left) && IsLiteral(binary.Right),
        UnaryExpressionAst unary => IsLiteral(unary.Child),
        ConvertExpressionAst convert => IsLiteral(convert.Child),
        StatementAst statement => IsLiteral(statement),
        _ => false,
    };
}
