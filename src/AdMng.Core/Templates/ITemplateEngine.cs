namespace AdMng.Core.Templates;

/// <summary>Data available while rendering a template for one object.</summary>
public sealed record TemplateContext
{
    /// <summary>Returns the current values of an attribute of the object (empty when absent).</summary>
    public required Func<string, IReadOnlyList<string>> Attribute { get; init; }

    /// <summary>DN of the object ({$dn}, {$rdn}, {$parent}, {$parentName}).</summary>
    public string? DistinguishedName { get; init; }

    /// <summary>DNS domain ({$domain}).</summary>
    public string? Domain { get; init; }

    /// <summary>1-based position in the batch ({$counter}, {$counter:000}).</summary>
    public int Counter { get; init; } = 1;

    /// <summary>Clock for {$date:format}.</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// Evaluates the extension tokens of the engine (<see cref="TemplateExtensionToken"/>): (name, argument) → value.
    /// Null: extension tokens render empty.
    /// </summary>
    public Func<string, string?, string>? Extension { get; init; }

    /// <summary>
    /// Applied to the final value of every token (after its functions) but never to the literal text, e.g. HTML encoding
    /// of directory values in an HTML template. Null: values are inserted as they are.
    /// </summary>
    public Func<string, string>? Encode { get; init; }

    /// <summary>Called with the source text of a token (e.g. <c>{department}</c>) whose value is empty or white space.</summary>
    public Action<string>? EmptyValue { get; init; }
}

/// <summary>Whether an extension token takes an argument after a colon (<c>{$name:argument}</c>).</summary>
public enum TemplateTokenArgument
{
    /// <summary><c>{$name}</c> only.</summary>
    None,

    /// <summary><c>{$name}</c> or <c>{$name:argument}</c>.</summary>
    Optional,

    /// <summary><c>{$name:argument}</c> only (non-empty argument).</summary>
    Required,
}

/// <summary>
/// A special token added to an engine instance (<see cref="TemplateEngine(IEnumerable{TemplateExtensionToken})"/>),
/// evaluated by <see cref="TemplateContext.Extension"/>. <paramref name="IsDateFormat"/>: the argument is validated as a
/// date format (like <c>{$date:format}</c>).
/// </summary>
public sealed record TemplateExtensionToken(string Name, TemplateTokenArgument Argument, bool IsDateFormat = false)
{
    /// <summary>
    /// The only arguments the token accepts (compared case- and diacritics-insensitively, e.g. <c>dopełniacz</c> =
    /// <c>dopelniacz</c>); another argument is a syntax error listing them. Null: any argument.
    /// </summary>
    public IReadOnlyList<string>? AllowedArguments { get; init; }

    /// <summary>
    /// Validates the argument: an English error text (without the position), or null when it is valid. Null: no check.
    /// </summary>
    public Func<string, string?>? ValidateArgument { get; init; }

    /// <summary>
    /// An empty value is reported to <see cref="TemplateContext.EmptyValue"/> (default). False for tokens whose empty value
    /// is normal, e.g. <c>{$title}</c> of a person without a title.
    /// </summary>
    public bool ReportsEmpty { get; init; } = true;
}

/// <summary>A special or extension token used by a template (name as declared, argument as written).</summary>
public sealed record TemplateTokenUse(string Name, string? Argument);

/// <summary>Result of parsing a template.</summary>
/// <param name="IsValid">No syntax errors.</param>
/// <param name="Errors">English diagnostics with positions.</param>
/// <param name="ReferencedAttributes">Attributes the template reads (to request them while planning).</param>
public sealed record TemplateParseResult(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> ReferencedAttributes)
{
    /// <summary>The special and extension tokens the template uses (in order of appearance, duplicates removed).</summary>
    public IReadOnlyList<TemplateTokenUse> SpecialTokens { get; init; } = [];
}

/// <summary>
/// Value templates for batch editing and object creation.
/// </summary>
/// <remarks>
/// Syntax:
/// <list type="bullet">
/// <item><c>{attr}</c> - first value of an attribute, e.g. <c>{givenName}</c>.</item>
/// <item><c>{attr|f1|f2:arg}</c> - pipe functions applied left to right:
/// <c>lower</c>, <c>upper</c>, <c>title</c>, <c>trim</c>, <c>nodiacritics</c> (ą→a, ł→l, ż→z ...),
/// <c>capitalize</c> (upper-cases the first letter only: <c>prof. dr hab.</c> → <c>Prof. dr hab.</c>),
/// <c>first:N</c>, <c>last:N</c>, <c>replace:old:new</c>, <c>default:text</c>, <c>nospaces</c>,
/// <c>alnum</c> (letters and digits only), <c>sam</c> (valid sAMAccountName: no diacritics, no
/// <c>"/\[]:;|=,+*?&lt;&gt;@</c> or spaces, max 20 chars).</item>
/// <item><c>{$dn}</c>, <c>{$rdn}</c>, <c>{$parent}</c>, <c>{$parentName}</c>, <c>{$domain}</c>,
/// <c>{$counter}</c> / <c>{$counter:000}</c>, <c>{$date:yyyy-MM-dd}</c> - special tokens (functions allowed too).</item>
/// <item><c>{{</c> and <c>}}</c> - literal braces.</item>
/// </list>
/// Example: <c>{givenName|first:1|nodiacritics|lower}{sn|nodiacritics|lower}</c> → <c>jkowalski</c>.
/// </remarks>
public interface ITemplateEngine
{
    TemplateParseResult Parse(string template);

    /// <summary>Renders the template. Throws <see cref="FormatException"/> for invalid templates.</summary>
    string Render(string template, TemplateContext context);

    /// <summary>Removes Polish (and other Latin) diacritics: "Łukasz Żółć" → "Lukasz Zolc".</summary>
    string RemoveDiacritics(string value);
}
