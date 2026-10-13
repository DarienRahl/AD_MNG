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
}

/// <summary>Result of parsing a template.</summary>
/// <param name="IsValid">No syntax errors.</param>
/// <param name="Errors">English diagnostics with positions.</param>
/// <param name="ReferencedAttributes">Attributes the template reads (to request them while planning).</param>
public sealed record TemplateParseResult(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> ReferencedAttributes);

/// <summary>
/// Value templates for batch editing and object creation.
/// </summary>
/// <remarks>
/// Syntax:
/// <list type="bullet">
/// <item><c>{attr}</c> - first value of an attribute, e.g. <c>{givenName}</c>.</item>
/// <item><c>{attr|f1|f2:arg}</c> - pipe functions applied left to right:
/// <c>lower</c>, <c>upper</c>, <c>title</c>, <c>trim</c>, <c>nodiacritics</c> (ą→a, ł→l, ż→z ...),
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
