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

    /// <summary>Short switch names of console programs that take a password (<c>/p:</c> of ldp, <c>/pd:</c> of netdom, <c>/rp</c> of schtasks, <c>-p</c> of psexec).</summary>
    private static readonly string[] NativeSecretSwitches = ["p", "pd", "po", "pw", "rp", "pass"];

    /// <summary>Console programs that take a password after a dash switch (<c>psexec -p</c>, <c>sqlcmd -P</c>).</summary>
    private static readonly string[] DashPasswordPrograms = ["psexec", "psexec64", "paexec", "plink", "pscp", "psftp", "sqlcmd", "osql", "bcp", "winrs"];

    private static readonly string[] ProgramExtensions = [".exe", ".com", ".bat", ".cmd"];

    /// <summary>The verbs of a command named after a secret that take a name, never the secret itself (<c>Get-Secret MySecret</c>).</summary>
    private static readonly string[] NamingVerbs = ["Get", "Find", "Search", "Show"];

    /// <summary>
    /// False for an empty line, for input that is still incomplete, for a text too long or nested too deeply to parse safely
    /// (<see cref="InputParser"/>; the guard refuses it), and for a line with a plain-text secret: the
    /// <c>-AsPlainText</c> parameter (also its prefixes of three letters or more, and of ConvertTo-SecureString every prefix: <c>-As</c>,
    /// <c>-A</c>); a literal given to a parameter, a hashtable key (an inline or splatted hashtable) or assigned to a variable, property or
    /// index whose name is a secret's name (of an interpolated key, property or index such as <c>"Pass$x"</c> its literal parts count)
    /// (<c>Password</c>, <c>Passphrase</c>, <c>Pwd</c>, <c>Secret</c>, <c>Token</c>, <c>ApiKey</c>, <c>PrivateKey</c> anywhere in
    /// the name; <c>Pw</c>, <c>Pin</c>, <c>Key</c> as the whole name; <c>-Pa</c>, <c>-Pas</c>, <c>-Pass</c> and the other
    /// abbreviations of <c>Password</c>/<c>Passphrase</c>); a literal written positionally to <c>ConvertTo-SecureString</c> (or as
    /// its <c>-String</c>) or to a command whose noun is a secret's name (<c>Reset-AdmPassword user 'x'</c>), unless its verb only
    /// names one (<c>Get</c>, <c>Find</c>, <c>Search</c>, <c>Show</c>). A literal is also a value computed from literals only: in
    /// parentheses or a subexpression (<c>('x')</c>, <c>$('x')</c>), joined or cast (<c>'a' + 'b'</c>, <c>-join ('a', 'b')</c>,
    /// <c>[string]'x'</c>), or the value of a chained assignment (<c>$password = $x = 'x'</c>). A literal cast to SecureString
    /// (<c>[securestring]'x'</c>) or assigned to a variable typed so (<c>[securestring]$x = 'x'</c>) is a secret whatever the name. A
    /// variable, a command or <c>Read-Host -AsSecureString</c> in the value is fine.
    /// </summary>
    public static bool ShouldRemember(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return Analyze(line).Remember;
    }

    /// <summary>The mask that stands for a secret in <see cref="HideSecrets"/>.</summary>
    public const string Mask = "***";

    /// <summary>
    /// <paramref name="line"/> as the window's scrollback echoes it (audit PS-13): every plain-text secret the rules of
    /// <see cref="ShouldRemember"/> find - the literal, or the value after a console program's switch - is replaced by
    /// <see cref="Mask"/>; the rest is as typed. A line the rules let through, or one whose secret is no literal of the line
    /// (<c>ConvertTo-SecureString $x -AsPlainText</c>), comes back unchanged.
    /// </summary>
    public static string HideSecrets(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var spans = Analyze(line).Spans;
        if (spans.Count == 0)
        {
            return line;
        }

        var merged = new List<(int Start, int End)>();
        foreach (var (start, end) in spans.OrderBy(span => span.Start))
        {
            if (merged.Count > 0 && start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        var text = new System.Text.StringBuilder(line.Length);
        var position = 0;
        foreach (var (start, end) in merged)
        {
            text.Append(line, position, start - position).Append(Mask);
            position = end;
        }

        return text.Append(line, position, line.Length - position).ToString();
    }

    private static Analysis Analyze(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new Analysis(remember: false);
        }

        // A line too long or too deep to walk safely, or one the parser gave up on, is not remembered (InputParser; the guard refuses it).
        var parsed = InputParser.Parse(line);
        if (parsed.Ast is not { } ast || parsed.Errors.Any(error => error.IncompleteInput))
        {
            return new Analysis(remember: false);
        }

        var analysis = new Analysis(remember: true);
        analysis.Collect(ast);

        // Tokens survive a parse error, so the plain-text parameter is found in a broken line as well.
        if (parsed.Tokens.Any(IsPlainTextParameter))
        {
            analysis.Remember = false;
        }

        return analysis;
    }

    /// <summary>
    /// <c>-AsPlainText</c> of any command, abbreviated to three letters or more (<c>-Asp</c>); a shorter prefix only of ConvertTo-SecureString
    /// (<see cref="Analysis.Command"/>), where <c>-As</c> and <c>-A</c> bind to it too but elsewhere are often another parameter.
    /// </summary>
    private static bool IsPlainTextParameter(Token token) =>
        token.Kind == TokenKind.Parameter && token is ParameterToken { ParameterName.Length: >= 3 } parameter
            && IsPlainTextPrefix(parameter.ParameterName);

    private static bool IsPlainTextPrefix(string parameterName) =>
        parameterName.Length > 0 && "AsPlainText".StartsWith(parameterName, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>The name of a switch of a console program that takes a secret: a secret's name, or one of the short ones (<c>/p</c>, <c>/pd</c>, <c>/rp</c>).</summary>
    private static bool IsNativeSecretSwitch(string name) =>
        IsSecretName(name) || NativeSecretSwitches.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a command is a console program: a name that ends in <c>.exe</c> (and the like) or has no dash (cmdlets and functions are
    /// <c>Verb-Noun</c>; an alias without a dash is taken as one too, so only switches that start with <c>/</c> count for it).
    /// </summary>
    private static bool IsConsoleProgram(string? name) =>
        name is not null && (ProgramExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) || !name.Contains('-', StringComparison.Ordinal));

    /// <summary>A console program known to take a password after a dash switch (<c>psexec -p</c>, <c>sqlcmd -P</c>): only these, since <c>-p</c> of a cmdlet is often <c>-Path</c>.</summary>
    private static bool TakesDashPassword(string? name) =>
        name is not null && (ProgramExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            || DashPasswordPrograms.Contains(name, StringComparer.OrdinalIgnoreCase));

    private static string? ProgramName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var file = name[(Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/')) + 1)..];
        foreach (var extension in ProgramExtensions)
        {
            if (file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return file[..^extension.Length];
            }
        }

        return file;
    }

    /// <summary>
    /// Whether a key, a property or an index is a secret's name: a constant (a bare word or a string), or an interpolated string one of
    /// whose literal parts is (<c>"Pass$x"</c>: <c>Pass</c> abbreviates <c>Password</c>; <c>"Key$i"</c>).
    /// </summary>
    private static bool IsSecretKey(Ast? key) => key switch
    {
        StringConstantExpressionAst constant => IsSecretName(constant.Value),
        ExpandableStringExpressionAst expandable => IsSecretName(expandable.Value) || LiteralParts(expandable).Any(IsSecretName),
        _ => false,
    };

    /// <summary>The text of an interpolated string outside its embedded expressions, without the quotes: <c>"Pass$x"</c> gives <c>Pass</c>.</summary>
    private static IEnumerable<string> LiteralParts(ExpandableStringExpressionAst expandable)
    {
        var text = expandable.Extent.Text;
        var start = expandable.Extent.StartOffset;
        var position = 0;
        foreach (var nested in expandable.NestedExpressions.OrderBy(nested => nested.Extent.StartOffset))
        {
            var end = Math.Clamp(nested.Extent.StartOffset - start, position, text.Length);
            yield return Unquoted(text[position..end]);
            position = Math.Clamp(nested.Extent.EndOffset - start, end, text.Length);
        }

        yield return Unquoted(text[position..]);
    }

    private static string Unquoted(string part) => part.Trim('"', '\u201C', '\u201D', '\u201E', '@', ' ', '\t', '\r', '\n');

    /// <summary>A target typed as SecureString: <c>[securestring]$x</c>, also among other attributes.</summary>
    private static bool IsSecureStringTarget(ExpressionAst target) =>
        target is AttributedExpressionAst attributed
        && ((attributed is ConvertExpressionAst convert && IsSecureString(convert.Type.TypeName)) || IsSecureStringTarget(attributed.Child));

    /// <summary><c>[securestring]</c>, <c>[Security.SecureString]</c>, <c>[System.Security.SecureString]</c>.</summary>
    private static bool IsSecureString(ITypeName type) => IsType(type.FullName, "SecureString");

    /// <summary><c>PSCredential</c>, <c>System.Management.Automation.PSCredential</c>.</summary>
    private static bool IsCredential(string? typeName) => typeName is not null && IsType(typeName, "PSCredential");

    private static bool IsType(string fullName, string name) =>
        fullName.Trim('[', ']')[(fullName.Trim('[', ']').LastIndexOf('.') + 1)..].Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>A variable's name without its scope or drive (<c>$script:p</c> is <c>p</c>).</summary>
    private static string NameOf(VariableExpressionAst variable) =>
        variable.VariablePath.UserPath[(variable.VariablePath.UserPath.LastIndexOf(':') + 1)..];

    /// <summary>The variable an assignment writes, through its type casts (<c>[string]$p = 'x'</c>), or null for a property or an index.</summary>
    private static VariableExpressionAst? AssignedVariable(ExpressionAst target) => target switch
    {
        VariableExpressionAst variable => variable,
        AttributedExpressionAst attributed => AssignedVariable(attributed.Child),
        _ => null,
    };

    /// <summary>
    /// What one line carries: whether it may be remembered and where its plain-text secrets are (the spans <see cref="HideSecrets"/>
    /// masks). A literal of the line is also a variable the same line set to a literal (<c>$p = 'x'; Set-Thing -Password $p</c>):
    /// its secret is the literal where it was set.
    /// </summary>
    private sealed class Analysis(bool remember)
    {
        private readonly Dictionary<string, List<ExpressionAst>> _literalVariables = new(StringComparer.OrdinalIgnoreCase);

        public bool Remember { get; set; } = remember;

        public List<(int Start, int End)> Spans { get; } = [];

        public void Collect(Ast ast)
        {
            foreach (var assignment in ast.FindAll(node => node is AssignmentStatementAst, searchNestedScriptBlocks: true).Cast<AssignmentStatementAst>())
            {
                if (AssignedVariable(assignment.Left) is { } variable && LiteralValue(assignment.Right) is { } value)
                {
                    if (!_literalVariables.TryGetValue(NameOf(variable), out var values))
                    {
                        _literalVariables[NameOf(variable)] = values = [];
                    }

                    values.Add(value);
                }
            }

            foreach (var node in ast.FindAll(_ => true, searchNestedScriptBlocks: true))
            {
                Visit(node);
            }
        }

        private void Visit(Ast node)
        {
            switch (node)
            {
                case CommandAst command:
                    Command(command);
                    break;
                case HashtableAst hashtable:
                    foreach (var (key, value) in hashtable.KeyValuePairs)
                    {
                        if (IsSecretKey(key))
                        {
                            SecretIfLiteral(value);
                        }
                    }

                    break;
                case AssignmentStatementAst assignment when IsSecretTarget(assignment.Left) || IsSecureStringTarget(assignment.Left):
                    SecretIfLiteral(assignment.Right);
                    break;
                case ConvertExpressionAst convert when IsSecureString(convert.Type.TypeName):
                    SecretIfLiteral(convert.Child);
                    break;
                case InvokeMemberExpressionAst { Static: true, Expression: TypeExpressionAst type, Member: StringConstantExpressionAst { Value: var member }, Arguments: { Count: >= 2 } arguments }
                    when IsCredential(type.TypeName.FullName) && member.Equals("new", StringComparison.OrdinalIgnoreCase):
                    SecretIfLiteral(arguments[1]);
                    break;
            }
        }

        private void Command(CommandAst command)
        {
            var elements = command.CommandElements;
            var name = command.GetCommandName() is { } qualified ? qualified[(qualified.LastIndexOf('\\') + 1)..] : null;
            var isSecureStringConversion = string.Equals(name, "ConvertTo-SecureString", StringComparison.OrdinalIgnoreCase);
            var takesSecretPositionally = isSecureStringConversion || IsNamedAfterSecret(name);
            var plainText = false;
            for (var i = 1; i < elements.Count; i++)
            {
                var element = elements[i];
                if (element is CommandParameterAst parameter)
                {
                    if (IsPlainTextPrefix(parameter.ParameterName) && (isSecureStringConversion || parameter.ParameterName.Length >= 3))
                    {
                        plainText = true;

                        // The secret may come through the pipeline: ConvertTo-SecureString -As (or -A) is enough.
                        if (isSecureStringConversion)
                        {
                            Remember = false;
                        }
                    }

                    var argument = parameter.Argument ?? (i + 1 < elements.Count && elements[i + 1] is not CommandParameterAst ? elements[i + 1] : null);
                    if (argument is not null
                        && (IsSecretName(parameter.ParameterName)
                            || (isSecureStringConversion && "String".StartsWith(parameter.ParameterName, StringComparison.OrdinalIgnoreCase))
                            || (TakesDashPassword(name) && NativeSecretSwitches.Contains(parameter.ParameterName, StringComparer.OrdinalIgnoreCase))))
                    {
                        SecretIfLiteral(argument);
                    }
                }
                else if (takesSecretPositionally && !IsArgumentOfParameter(elements, i))
                {
                    SecretIfLiteral(element);
                }
            }

            if (plainText)
            {
                // -AsPlainText: the command's own literals and the literals piped into it are the secret.
                PlainTextLiterals(command);
            }

            if (IsCredential(NewObjectType(command, name)))
            {
                CredentialArguments(command);
            }

            if (IsConsoleProgram(name))
            {
                ConsoleProgram(ProgramName(name), elements);
            }
        }

        private void PlainTextLiterals(CommandAst command)
        {
            for (var i = 1; i < command.CommandElements.Count; i++)
            {
                if (command.CommandElements[i] is not CommandParameterAst && !IsArgumentOfParameter(command.CommandElements, i))
                {
                    SecretIfLiteral(command.CommandElements[i]);
                }
            }

            if (command.Parent is PipelineAst pipeline)
            {
                foreach (var element in pipeline.PipelineElements.TakeWhile(element => !ReferenceEquals(element, command)))
                {
                    if (element is CommandExpressionAst expression)
                    {
                        SecretIfLiteral(expression.Expression);
                    }
                }
            }
        }

        /// <summary>The type of <c>New-Object</c>: its <c>-TypeName</c> or its first positional argument.</summary>
        private static string? NewObjectType(CommandAst command, string? name)
        {
            if (!string.Equals(name, "New-Object", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var elements = command.CommandElements;
            for (var i = 1; i < elements.Count; i++)
            {
                if (elements[i] is CommandParameterAst parameter)
                {
                    if ("TypeName".StartsWith(parameter.ParameterName, StringComparison.OrdinalIgnoreCase))
                    {
                        return (parameter.Argument ?? (i + 1 < elements.Count ? elements[i + 1] : null)) is StringConstantExpressionAst type ? type.Value : null;
                    }
                }
                else if (!IsArgumentOfParameter(elements, i))
                {
                    return elements[i] is StringConstantExpressionAst type ? type.Value : null;
                }
            }

            return null;
        }

        /// <summary><c>New-Object PSCredential 'jan', 'x'</c>: the second value of the arguments (positional or <c>-ArgumentList</c>) is the password.</summary>
        private void CredentialArguments(CommandAst command)
        {
            foreach (var element in command.CommandElements.Skip(1))
            {
                var value = element is CommandParameterAst parameter ? parameter.Argument : element as ExpressionAst;
                while (value is ParenExpressionAst { Pipeline: PipelineAst pipeline } && pipeline.GetPureExpression() is { } inner)
                {
                    value = inner;
                }

                if (value is ArrayLiteralAst { Elements.Count: >= 2 } list)
                {
                    SecretIfLiteral(list.Elements[1]);
                }
            }
        }

        /// <summary>
        /// A console program: <c>net user name password</c>, <c>net use \\server\share password</c>, a switch with a secret's name and a
        /// value (<c>/pass:x</c>, <c>/pd:x</c>, <c>-p:x</c>) or followed by one (<c>/rp x</c>).
        /// </summary>
        private void ConsoleProgram(string? program, IReadOnlyList<CommandElementAst> elements)
        {
            var isNet = program is not null && (program.Equals("net", StringComparison.OrdinalIgnoreCase) || program.Equals("net1", StringComparison.OrdinalIgnoreCase));
            var subcommand = isNet && elements.Count > 1 ? ConstantText(elements[1]) : null;
            for (var i = 1; i < elements.Count; i++)
            {
                var text = ConstantText(elements[i]);
                if (text is null)
                {
                    continue;
                }

                if (text.Length > 1 && text[0] == '/')
                {
                    var separator = text.IndexOfAny([':', '=']);
                    var name = separator > 1 ? text[1..separator] : text[1..];
                    if (!IsNativeSecretSwitch(name))
                    {
                        continue;
                    }

                    if (separator > 1 && separator + 1 < text.Length)
                    {
                        // The value inside the word: only it is masked.
                        Spans.Add((elements[i].Extent.StartOffset + (elements[i].Extent.Text.Length - text.Length) + separator + 1, elements[i].Extent.EndOffset));
                        Remember = false;
                    }
                    else if (separator < 0 && i + 1 < elements.Count && ConstantText(elements[i + 1]) is { } next && !next.StartsWith('/'))
                    {
                        SecretIfLiteral(elements[i + 1]);
                    }
                }
                else if (subcommand is not null && IsNetPassword(subcommand, elements, i, text))
                {
                    SecretIfLiteral(elements[i]);
                }
            }
        }

        /// <summary><c>net user name password</c> (the third word) and <c>net use [device] \\server\share password</c> (the word after the share); <c>*</c> asks.</summary>
        private static bool IsNetPassword(string subcommand, IReadOnlyList<CommandElementAst> elements, int index, string text)
        {
            if (text == "*" || text.StartsWith('/'))
            {
                return false;
            }

            if (subcommand.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                return index == 3;
            }

            return subcommand.Equals("use", StringComparison.OrdinalIgnoreCase)
                && index > 2
                && ConstantText(elements[index - 1]) is { } before
                && before.StartsWith(@"\\", StringComparison.Ordinal);
        }

        /// <summary>The text of a bare word or a quoted string without embedded expressions, else null.</summary>
        private static string? ConstantText(CommandElementAst element) => element switch
        {
            StringConstantExpressionAst constant => constant.Value,
            ExpandableStringExpressionAst { NestedExpressions.Count: 0 } expandable => expandable.Value,
            _ => null,
        };

        /// <summary>A value that is a literal of the line: the secret; its span is masked (for a variable, the literal it was set to).</summary>
        private void SecretIfLiteral(Ast? value)
        {
            if (value is null || !IsLiteral(value))
            {
                return;
            }

            Remember = false;
            foreach (var variable in value.FindAll(node => node is VariableExpressionAst, searchNestedScriptBlocks: false).Cast<VariableExpressionAst>())
            {
                if (_literalVariables.TryGetValue(NameOf(variable), out var literals))
                {
                    foreach (var literal in literals)
                    {
                        Spans.Add((literal.Extent.StartOffset, literal.Extent.EndOffset));
                    }
                }
            }

            if (value is not VariableExpressionAst && !(value is CommandExpressionAst { Expression: VariableExpressionAst }))
            {
                Spans.Add((value.Extent.StartOffset, value.Extent.EndOffset));
            }
        }

        /// <summary>The literal value an assignment's right side holds (also of a chained assignment), or null.</summary>
        private ExpressionAst? LiteralValue(StatementAst statement) => statement switch
        {
            PipelineAst pipeline => pipeline.GetPureExpression() is { } expression && IsLiteral(expression) ? expression : null,
            CommandExpressionAst command => IsLiteral(command.Expression) ? command.Expression : null,
            AssignmentStatementAst assignment => LiteralValue(assignment.Right),
            _ => null,
        };

        /// <summary>A statement whose value is a literal: a pure expression, or a (chained) assignment of one.</summary>
        private bool IsLiteral(StatementAst statement) => statement switch
        {
            PipelineAst pipeline => pipeline.GetPureExpression() is { } expression && IsLiteral(expression),
            CommandExpressionAst command => IsLiteral(command.Expression),
            AssignmentStatementAst assignment => IsLiteral(assignment.Right),
            _ => false,
        };

        /// <summary>
        /// A value typed as text: a constant, a string without embedded expressions, an array of these, or a value computed from them alone -
        /// in parentheses, in a subexpression of one statement, through an operator (<c>'a' + 'b'</c>, <c>-join</c>) or a cast - and a
        /// variable this line set to such a value.
        /// </summary>
        private bool IsLiteral(Ast node) => node switch
        {
            ConstantExpressionAst => true,
            ExpandableStringExpressionAst expandable => expandable.NestedExpressions.Count == 0,
            VariableExpressionAst variable => _literalVariables.ContainsKey(NameOf(variable)),
            ArrayLiteralAst array => array.Elements.All(IsLiteral),
            ParenExpressionAst paren => IsLiteral(paren.Pipeline),
            SubExpressionAst { SubExpression: { Statements.Count: 1, Traps: null } block } => IsLiteral(block.Statements[0]),
            BinaryExpressionAst binary => IsLiteral(binary.Left) && IsLiteral(binary.Right),
            UnaryExpressionAst unary => IsLiteral(unary.Child),
            ConvertExpressionAst convert => IsLiteral(convert.Child),
            StatementAst statement => IsLiteral(statement),
            _ => false,
        };

        /// <summary>Whether an assignment writes a secret's name: a variable (without its scope or drive), a property, an index, through a type cast.</summary>
        private static bool IsSecretTarget(ExpressionAst target) => target switch
        {
            VariableExpressionAst variable => IsSecretName(NameOf(variable)),
            MemberExpressionAst member => IsSecretKey(member.Member),
            IndexExpressionAst index => IsSecretKey(index.Index),
            AttributedExpressionAst attributed => IsSecretTarget(attributed.Child),
            _ => false,
        };
    }
}
