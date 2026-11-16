using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace AdMng.PowerShell.Completion;

/// <summary>
/// Completes the attribute names of <c>-Properties</c> from the schema the session already holds in memory (never a read per
/// keystroke). The names are the LDAP names; the tool tip is the attribute's syntax and whether it holds several values.
/// </summary>
public sealed class AdmAttributeCompleter : IArgumentCompleter
{
    /// <summary>How many names one completion offers at most.</summary>
    internal const int Limit = 200;

    /// <inheritdoc />
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName,
        string parameterName,
        string wordToComplete,
        CommandAst commandAst,
        IDictionary fakeBoundParameters)
    {
        var schema = AdmCompletionContext.Current()?.Reader.Schema;
        if (schema is null)
        {
            return [];
        }

        var word = (wordToComplete ?? string.Empty).Trim().Trim('\'', '"');
        return schema.Attributes
            .Where(a => a.LdapName.StartsWith(word, StringComparison.OrdinalIgnoreCase) && !a.IsConfidential)
            .OrderBy(a => a.LdapName, StringComparer.OrdinalIgnoreCase)
            .Take(Limit)
            .Select(a => new CompletionResult(
                AdmCompletionContext.Quote(a.LdapName),
                a.LdapName,
                CompletionResultType.ParameterValue,
                $"{a.Syntax}{(a.IsSingleValued ? string.Empty : ", multi-valued")}"));
    }
}
