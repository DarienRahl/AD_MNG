using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Language;

namespace AdMng.PowerShell.Completion;

/// <summary>
/// Completes <c>-SearchBase</c> from the distinguished names the session already holds: its naming contexts (from the root DSE
/// it read when it connected) and the containers this window's commands returned earlier. Nothing is read from the directory
/// and nothing is guessed; with no such DN there is no completion.
/// </summary>
public sealed class AdmContainerCompleter : IArgumentCompleter
{
    /// <inheritdoc />
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName,
        string parameterName,
        string wordToComplete,
        CommandAst commandAst,
        IDictionary fakeBoundParameters)
    {
        var context = AdmCompletionContext.Current();
        if (context is null)
        {
            return [];
        }

        var info = context.Reader.Info;
        var word = (wordToComplete ?? string.Empty).Trim().Trim('\'', '"');
        return new[] { info.DefaultNamingContext, info.ConfigurationNamingContext, info.SchemaNamingContext }
            .Concat(context.KnownContainers.Snapshot())
            .Where(dn => !string.IsNullOrEmpty(dn))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(dn => dn.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            .Take(AdmAttributeCompleter.Limit)
            .Select(dn => new CompletionResult(AdmCompletionContext.Quote(dn), dn, CompletionResultType.ParameterValue, dn));
    }
}
