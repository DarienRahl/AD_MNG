using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using AdMng.Core.Ldap;

namespace AdMng.Core.Templates;

/// <summary>
/// Default <see cref="ITemplateEngine"/>. Thread-safe; parsed templates are cached in memory (the cache holds
/// templates typed by the operator, never directory values).
/// </summary>
/// <remarks>
/// In addition to the syntax documented on <see cref="ITemplateEngine"/>:
/// <list type="bullet">
/// <item>Inside a token a backslash escapes the next character, e.g. <c>{x|replace:\::-}</c> or <c>{x|default:a\|b}</c>.
/// Outside tokens a backslash is a literal character.</item>
/// <item>Whitespace around attribute names and functions is ignored: <c>{ givenName | lower }</c>.</item>
/// <item><c>default:text</c>, <c>{$date:format}</c> and <c>{$counter:format}</c> take the rest of the segment
/// literally, so <c>{$date:HH:mm}</c> works without escaping.</item>
/// <item><c>{$rdn}</c> is the unescaped value of the leaf RDN (<c>Jan Kowalski</c>), <c>{$parentName}</c> the value
/// of the parent's RDN (<c>Księgowość</c>), <c>{$domain}</c> falls back to the DNS name derived from the DN.</item>
/// <item><c>sam</c> removes diacritics, whitespace, control and non-ASCII characters and
/// <c>"/\[]:;|=,+*?&lt;&gt;@</c>, truncates to 20 characters and trims trailing periods.</item>
/// </list>
/// </remarks>
public sealed class TemplateEngine : ITemplateEngine
{
    /// <summary>Maximum length of a sAMAccountName produced by the <c>sam</c> function.</summary>
    public const int MaxSamAccountNameLength = 20;

    private const int MaxCachedTemplates = 512;

    private static readonly string[] KnownFunctions =
        ["lower", "upper", "title", "capitalize", "trim", "nodiacritics", "first", "last", "replace", "default", "nospaces", "alnum", "sam"];

    private static readonly string[] KnownSpecialTokens = ["dn", "rdn", "parent", "parentName", "domain", "counter", "date"];

    private readonly ConcurrentDictionary<string, CompiledTemplate> _cache = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, TemplateExtensionToken> _extensions;

    /// <summary>The engine of the batch editor and object creation (no extension tokens).</summary>
    public TemplateEngine()
        : this([])
    {
    }

    /// <summary>
    /// An engine that also knows <paramref name="extensions"/> (<c>{$name}</c> / <c>{$name:argument}</c>, evaluated by
    /// <see cref="TemplateContext.Extension"/>), e.g. the computed fields of user messages. Names must not collide with the
    /// built-in special tokens.
    /// </summary>
    public TemplateEngine(IEnumerable<TemplateExtensionToken> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var map = new Dictionary<string, TemplateExtensionToken>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in extensions)
        {
            if (KnownSpecialTokens.Contains(extension.Name, StringComparer.OrdinalIgnoreCase) || !map.TryAdd(extension.Name, extension))
            {
                throw new ArgumentException($"The token '${extension.Name}' is defined twice.", nameof(extensions));
            }
        }

        _extensions = map;
    }

    /// <summary>The extension tokens of this instance.</summary>
    public IReadOnlyCollection<TemplateExtensionToken> Extensions => [.. _extensions.Values];

    /// <inheritdoc />
    public TemplateParseResult Parse(string template)
    {
        var compiled = Compile(template);
        return new TemplateParseResult(compiled.Errors.Count == 0, compiled.Errors, compiled.ReferencedAttributes)
        {
            SpecialTokens = compiled.SpecialTokens,
        };
    }

    /// <inheritdoc />
    public string Render(string template, TemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var compiled = Compile(template);
        if (compiled.Errors.Count > 0)
        {
            throw new FormatException("Invalid template: " + string.Join(" ", compiled.Errors));
        }

        var sb = new StringBuilder();
        foreach (var segment in compiled.Segments)
        {
            sb.Append(segment switch
            {
                LiteralSegment literal => literal.Text,
                TokenSegment token => EvaluateToken(token, context),
                _ => throw new InvalidOperationException("Unknown template segment."),
            });
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public string RemoveDiacritics(string value) => Diacritics.Remove(value);

    private CompiledTemplate Compile(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (_cache.TryGetValue(template, out var cached))
        {
            return cached;
        }

        var compiled = TemplateParser.Parse(template, _extensions);
        if (_cache.Count >= MaxCachedTemplates)
        {
            _cache.Clear();
        }

        _cache.TryAdd(template, compiled);
        return compiled;
    }

    private string EvaluateToken(TokenSegment token, TemplateContext context)
    {
        var value = token.Special is { } special
            ? token.IsExtension
                ? context.Extension?.Invoke(special, token.SpecialArgument) ?? string.Empty
                : EvaluateSpecial(special, token.SpecialArgument, context)
            : FirstValue(context.Attribute(token.Attribute!));
        foreach (var function in token.Functions)
        {
            value = Apply(function, value);
        }

        if (token.ReportsEmpty && string.IsNullOrWhiteSpace(value))
        {
            context.EmptyValue?.Invoke(token.Source);
        }

        return context.Encode is { } encode ? encode(value) : value;
    }

    private static string FirstValue(IReadOnlyList<string>? values) => values is { Count: > 0 } ? values[0] ?? string.Empty : string.Empty;

    private static string EvaluateSpecial(string special, string? argument, TemplateContext context)
    {
        var dn = context.DistinguishedName is { } text && DistinguishedName.TryParse(text, out var parsed) ? parsed : null;
        return special switch
        {
            "dn" => context.DistinguishedName ?? string.Empty,
            "rdn" => dn is { IsRoot: false } ? dn.Name : string.Empty,
            "parent" => dn?.Parent?.ToString() ?? string.Empty,
            "parentName" => dn?.Parent?.Name ?? string.Empty,
            "domain" => context.Domain ?? dn?.DnsDomain ?? string.Empty,
            "counter" => context.Counter.ToString(argument ?? string.Empty, CultureInfo.InvariantCulture),
            "date" => context.Now.ToString(argument ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Unknown special token '${special}'."),
        };
    }

    private string Apply(TemplateFunction function, string value) => function.Name switch
    {
        "lower" => value.ToLowerInvariant(),
        "upper" => value.ToUpperInvariant(),
        "title" => ToTitleCase(value),
        "capitalize" => Capitalize(value),
        "trim" => value.Trim(),
        "nodiacritics" => RemoveDiacritics(value),
        "first" => FirstTextElements(value, function.Count),
        "last" => LastTextElements(value, function.Count),
        "replace" => value.Replace(function.Arg1!, function.Arg2 ?? string.Empty, StringComparison.Ordinal),
        "default" => string.IsNullOrWhiteSpace(value) ? function.Arg1 ?? string.Empty : value,
        "nospaces" => string.Concat(value.Where(c => !char.IsWhiteSpace(c))),
        "alnum" => string.Concat(value.Where(char.IsLetterOrDigit)),
        "sam" => ToSamAccountName(value),
        _ => throw new InvalidOperationException($"Unknown function '{function.Name}'."),
    };

    /// <summary>Upper-cases the first letter of every word (words split on whitespace, '-', '\'' and '.'), lower-cases the rest.</summary>
    private static string ToTitleCase(string value)
    {
        var sb = new StringBuilder(value.Length);
        var startOfWord = true;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '\'' or '.')
            {
                sb.Append(c);
                startOfWord = true;
                continue;
            }

            sb.Append(startOfWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            startOfWord = false;
        }

        return sb.ToString();
    }

    /// <summary>Upper-cases the first letter only ("prof. dr hab." → "Prof. dr hab."); the rest stays as it is.</summary>
    private static string Capitalize(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsLetter(value[i]))
            {
                return char.IsUpper(value[i]) ? value : string.Concat(value.AsSpan(0, i), char.ToUpperInvariant(value[i]).ToString(), value.AsSpan(i + 1));
            }
        }

        return value;
    }

    private static string FirstTextElements(string value, int count)
    {
        if (count == 0)
        {
            return string.Empty;
        }

        var info = new StringInfo(value);
        return info.LengthInTextElements <= count ? value : info.SubstringByTextElements(0, count);
    }

    private static string LastTextElements(string value, int count)
    {
        // StringInfo.SubstringByTextElements(length, 0) throws, so 0 is handled explicitly.
        if (count == 0)
        {
            return string.Empty;
        }

        var info = new StringInfo(value);
        var length = info.LengthInTextElements;
        return length <= count ? value : info.SubstringByTextElements(length - count, count);
    }

    private string ToSamAccountName(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in RemoveDiacritics(value))
        {
            if (c > '~' || char.IsWhiteSpace(c) || char.IsControl(c) || IsInvalidSamCharacter(c))
            {
                continue;
            }

            sb.Append(c);
        }

        var result = sb.ToString();
        if (result.Length > MaxSamAccountNameLength)
        {
            result = result[..MaxSamAccountNameLength];
        }

        return result.TrimEnd('.');
    }

    /// <summary>Characters not allowed in a sAMAccountName ("/\[]:;|=,+*?&lt;&gt;@ and double quote).</summary>
    internal static bool IsInvalidSamCharacter(char c) => c is '"' or '/' or '\\' or '[' or ']' or ':' or ';' or '|'
        or '=' or ',' or '+' or '*' or '?' or '<' or '>' or '@';

    private abstract record Segment;

    private sealed record LiteralSegment(string Text) : Segment;

    private sealed record TokenSegment(
        string Source,
        string? Attribute,
        string? Special,
        string? SpecialArgument,
        bool IsExtension,
        IReadOnlyList<TemplateFunction> Functions,
        bool ReportsEmpty = true) : Segment;

    private sealed record TemplateFunction(string Name, int Count = 0, string? Arg1 = null, string? Arg2 = null);

    private sealed record CompiledTemplate(
        IReadOnlyList<Segment> Segments,
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> ReferencedAttributes,
        IReadOnlyList<TemplateTokenUse> SpecialTokens);

    /// <summary>One character of a token with the information whether it was escaped with a backslash.</summary>
    private readonly record struct TokenChar(char Value, bool Escaped, int Position);

    private static class TemplateParser
    {
        public static CompiledTemplate Parse(string template, IReadOnlyDictionary<string, TemplateExtensionToken> extensions)
        {
            var parser = new ParseState(template, extensions);
            var segments = parser.Segments;
            var errors = parser.Errors;
            var literal = new StringBuilder();
            var i = 0;
            while (i < template.Length)
            {
                var c = template[i];
                if (c == '{')
                {
                    if (i + 1 < template.Length && template[i + 1] == '{')
                    {
                        literal.Append('{');
                        i += 2;
                        continue;
                    }

                    if (literal.Length > 0)
                    {
                        segments.Add(new LiteralSegment(literal.ToString()));
                        literal.Clear();
                    }

                    i = ParseToken(parser, i);
                    continue;
                }

                if (c == '}')
                {
                    if (i + 1 < template.Length && template[i + 1] == '}')
                    {
                        literal.Append('}');
                        i += 2;
                        continue;
                    }

                    errors.Add($"Unexpected '}}' at position {i}; use '}}}}' for a literal brace.");
                    i++;
                    continue;
                }

                literal.Append(c);
                i++;
            }

            if (literal.Length > 0)
            {
                segments.Add(new LiteralSegment(literal.ToString()));
            }

            return new CompiledTemplate(segments, errors, parser.Attributes, parser.SpecialTokens);
        }

        /// <summary>Parses the token starting at <paramref name="start"/> (the '{'); returns the index after it.</summary>
        private static int ParseToken(ParseState parser, int start)
        {
            var template = parser.Template;
            var errors = parser.Errors;
            var chars = new List<TokenChar>();
            var i = start + 1;
            while (i < template.Length)
            {
                var c = template[i];
                if (c == '\\')
                {
                    if (i + 1 >= template.Length)
                    {
                        errors.Add($"Dangling escape character at position {i}.");
                        return template.Length;
                    }

                    chars.Add(new TokenChar(template[i + 1], true, i));
                    i += 2;
                    continue;
                }

                if (c == '}')
                {
                    BuildToken(parser, chars, start, template[start..(i + 1)]);
                    return i + 1;
                }

                if (c == '{')
                {
                    errors.Add($"Unexpected '{{' at position {i} inside the token starting at position {start}; escape it as '\\{{'.");
                    return SkipToTokenEnd(template, i);
                }

                chars.Add(new TokenChar(c, false, i));
                i++;
            }

            errors.Add($"Unclosed '{{' at position {start}.");
            return template.Length;
        }

        private static int SkipToTokenEnd(string template, int from)
        {
            var end = template.IndexOf('}', from);
            return end < 0 ? template.Length : end + 1;
        }

        private static void BuildToken(ParseState parser, List<TokenChar> chars, int start, string source)
        {
            var errors = parser.Errors;
            var attributes = parser.Attributes;
            var parts = Split(chars, '|');
            var head = Trim(parts[0]);
            var errorCount = errors.Count;
            if (head.Count == 0)
            {
                errors.Add($"Empty token at position {start}.");
                return;
            }

            string? attribute = null;
            string? special = null;
            string? specialArgument = null;
            var isExtension = false;
            var reportsEmpty = true;
            if (head[0] is { Value: '$', Escaped: false })
            {
                var nameAndArgs = Split(head.Skip(1).ToList(), ':');
                var name = Text(nameAndArgs[0]).Trim();
                special = KnownSpecialTokens.FirstOrDefault(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
                if (nameAndArgs.Count > 1)
                {
                    specialArgument = string.Join(':', nameAndArgs.Skip(1).Select(Text));
                }

                if (special is null && parser.Extensions.TryGetValue(name, out var extension))
                {
                    special = extension.Name;
                    isExtension = true;
                    reportsEmpty = extension.ReportsEmpty;
                    ValidateExtensionArgument(extension, specialArgument, head[0].Position, errors);
                }
                else if (special is null)
                {
                    var known = KnownSpecialTokens.Concat(parser.Extensions.Values.Select(e => e.Name)).Select(s => "$" + s);
                    errors.Add($"Unknown special token '${name}' at position {head[0].Position}. Known tokens: {string.Join(", ", known)}.");
                }
                else if (specialArgument is not null)
                {
                    ValidateSpecialArgument(special, specialArgument, head[0].Position, errors);
                }
            }
            else
            {
                attribute = Text(head);
                if (!IsValidAttributeName(head))
                {
                    errors.Add($"Invalid attribute name '{attribute}' at position {head[0].Position}.");
                }
                else if (!attributes.Contains(attribute, StringComparer.OrdinalIgnoreCase))
                {
                    attributes.Add(attribute);
                }
            }

            var functions = new List<TemplateFunction>();
            for (var p = 1; p < parts.Count; p++)
            {
                var part = Trim(parts[p]);
                var position = part.Count > 0 ? part[0].Position : start;
                if (part.Count == 0)
                {
                    errors.Add($"Empty function name in the token starting at position {start}.");
                    continue;
                }

                if (ParseFunction(part, position, errors) is { } function)
                {
                    functions.Add(function);
                }
            }

            if (errors.Count == errorCount)
            {
                parser.Segments.Add(new TokenSegment(source, attribute, special, specialArgument, isExtension, functions, reportsEmpty));
                if (special is not null && !parser.SpecialTokens.Any(t => string.Equals(t.Name, special, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Argument, specialArgument, StringComparison.Ordinal)))
                {
                    parser.SpecialTokens.Add(new TemplateTokenUse(special, specialArgument));
                }
            }
        }

        private static void ValidateExtensionArgument(TemplateExtensionToken extension, string? argument, int position, List<string> errors)
        {
            switch (extension.Argument)
            {
                case TemplateTokenArgument.None when argument is not null:
                    errors.Add($"Special token '${extension.Name}' at position {position} does not take an argument.");
                    return;
                case TemplateTokenArgument.Required when string.IsNullOrWhiteSpace(argument):
                    errors.Add($"Special token '${extension.Name}' at position {position} requires an argument, e.g. '{{${extension.Name}:name}}'.");
                    return;
            }

            if (argument is not null && extension.AllowedArguments is { } allowed
                && !allowed.Any(a => string.Equals(Diacritics.Remove(a.Trim()), Diacritics.Remove(argument.Trim()), StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Invalid argument '{argument}' for '${extension.Name}' at position {position}. Allowed: {string.Join(", ", allowed)}.");
                return;
            }

            if (argument is not null && extension.ValidateArgument?.Invoke(argument) is { } invalid)
            {
                errors.Add($"Invalid argument '{argument}' for '${extension.Name}' at position {position}: {invalid}");
                return;
            }

            if (extension.IsDateFormat && argument is not null)
            {
                try
                {
                    if (argument.Length == 0)
                    {
                        errors.Add($"Empty date format for '${extension.Name}' at position {position}.");
                        return;
                    }

                    _ = DateTimeOffset.UnixEpoch.ToString(argument, CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    errors.Add($"Invalid format '{argument}' for '${extension.Name}' at position {position}.");
                }
            }
        }

        /// <summary>The mutable state of one parse.</summary>
        private sealed class ParseState(string template, IReadOnlyDictionary<string, TemplateExtensionToken> extensions)
        {
            public string Template { get; } = template;

            public IReadOnlyDictionary<string, TemplateExtensionToken> Extensions { get; } = extensions;

            public List<Segment> Segments { get; } = [];

            public List<string> Errors { get; } = [];

            public List<string> Attributes { get; } = [];

            public List<TemplateTokenUse> SpecialTokens { get; } = [];
        }

        private static TemplateFunction? ParseFunction(List<TokenChar> part, int position, List<string> errors)
        {
            var pieces = Split(part, ':');
            var name = Text(pieces[0]).Trim().ToLowerInvariant();
            var args = pieces.Skip(1).Select(Text).ToList();
            switch (name)
            {
                case "lower" or "upper" or "title" or "capitalize" or "trim" or "nodiacritics" or "nospaces" or "alnum" or "sam":
                    if (args.Count > 0)
                    {
                        errors.Add($"Function '{name}' at position {position} does not take arguments.");
                        return null;
                    }

                    return new TemplateFunction(name);
                case "first" or "last":
                    if (args.Count != 1
                        || !int.TryParse(args[0], NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var count))
                    {
                        errors.Add($"Function '{name}' at position {position} requires one non-negative integer argument, e.g. '{name}:1'.");
                        return null;
                    }

                    return new TemplateFunction(name, Count: count);
                case "replace":
                    if (args.Count != 2)
                    {
                        errors.Add($"Function 'replace' at position {position} requires two arguments, e.g. 'replace:old:new' (escape a literal ':' as '\\:').");
                        return null;
                    }

                    if (args[0].Length == 0)
                    {
                        errors.Add($"Function 'replace' at position {position} requires a non-empty text to find.");
                        return null;
                    }

                    return new TemplateFunction(name, Arg1: args[0], Arg2: args[1]);
                case "default":
                    if (args.Count == 0)
                    {
                        errors.Add($"Function 'default' at position {position} requires an argument, e.g. 'default:text'.");
                        return null;
                    }

                    return new TemplateFunction(name, Arg1: string.Join(':', args));
                default:
                    errors.Add($"Unknown function '{Text(pieces[0]).Trim()}' at position {position}. Known functions: {string.Join(", ", KnownFunctions)}.");
                    return null;
            }
        }

        private static void ValidateSpecialArgument(string special, string argument, int position, List<string> errors)
        {
            try
            {
                switch (special)
                {
                    case "counter":
                        _ = 1.ToString(argument, CultureInfo.InvariantCulture);
                        break;
                    case "date":
                        if (argument.Length == 0)
                        {
                            errors.Add($"Empty date format for '$date' at position {position}.");
                            return;
                        }

                        _ = DateTimeOffset.UnixEpoch.ToString(argument, CultureInfo.InvariantCulture);
                        break;
                    default:
                        errors.Add($"Special token '${special}' at position {position} does not take an argument.");
                        break;
                }
            }
            catch (FormatException)
            {
                errors.Add($"Invalid format '{argument}' for '${special}' at position {position}.");
            }
        }

        private static bool IsValidAttributeName(List<TokenChar> name)
        {
            if (name.Count == 0 || !char.IsAsciiLetter(name[0].Value))
            {
                return false;
            }

            return name.All(c => !c.Escaped && (char.IsAsciiLetterOrDigit(c.Value) || c.Value == '-'));
        }

        private static List<List<TokenChar>> Split(List<TokenChar> chars, char separator)
        {
            var result = new List<List<TokenChar>> { new() };
            foreach (var c in chars)
            {
                if (c.Value == separator && !c.Escaped)
                {
                    result.Add([]);
                }
                else
                {
                    result[^1].Add(c);
                }
            }

            return result;
        }

        private static List<TokenChar> Trim(List<TokenChar> chars)
        {
            var start = 0;
            var end = chars.Count;
            while (start < end && !chars[start].Escaped && char.IsWhiteSpace(chars[start].Value))
            {
                start++;
            }

            while (end > start && !chars[end - 1].Escaped && char.IsWhiteSpace(chars[end - 1].Value))
            {
                end--;
            }

            return chars.GetRange(start, end - start);
        }

        private static string Text(List<TokenChar> chars) => string.Concat(chars.Select(c => c.Value));
    }
}
