using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Rules;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>Builds the <see cref="SecurityEventReduction"/> of one poll: <c>ExcludedSubtrees</c> expanded with the
/// current token values, the watched attribute catalog and the schema of the directory session (none when the session
/// is unavailable – then no attribute value is kept, 11.1).</summary>
public sealed class SecurityEventReductionFactory(IDirectorySessionProvider sessions, IConfigTokenSource tokens, IWatchedAttributeCatalog watchedAttributes)
{
    /// <summary>The reduction for <paramref name="configuration"/>.</summary>
    public async Task<SecurityEventReduction> CreateAsync(WatchdogConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var current = tokens.Current;
        var excluded = (configuration.Directory?.ExcludedSubtrees ?? [])
            .Select(current.ExpandDn)
            .Select(value => DistinguishedName.TryParse(value, out var dn) && !dn.IsRoot ? dn : null)
            .OfType<DistinguishedName>()
            .ToList();

        AdMng.Core.Schema.ISchemaCatalog? schema = null;
        try
        {
            schema = (await sessions.GetSessionAsync(cancellationToken).ConfigureAwait(false)).Schema;
        }
        catch (DirectoryException)
        {
            // no session: values are not kept this poll
        }

        return new SecurityEventReduction { ExcludedSubtrees = excluded, WatchedAttributes = watchedAttributes, Schema = schema };
    }
}
