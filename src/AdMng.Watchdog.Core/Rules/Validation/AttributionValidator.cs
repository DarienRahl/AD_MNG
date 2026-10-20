using System.Text.RegularExpressions;
using AdMng.Watchdog.Core.Attribution;

namespace AdMng.Watchdog.Core.Rules.Validation;

/// <summary>
/// Section <c>attribution</c>: <c>invalidHostName</c> for <c>collector</c> and every <c>domainControllers</c> entry –
/// <c>^[A-Za-z0-9.-]{1,253}$</c> and not an IP literal (<see cref="HostNames.IsHostName"/>). The service identity (Event
/// Log Readers on every DC) authenticates to these hosts with Kerberos, which needs a host name (11.1). <c>source</c> must be
/// one the host supports (<c>sourceUnsupportedOnHost</c>, stage B design 7.8: OpenWec or None on Linux).
/// </summary>
internal static partial class AttributionValidator
{
    public static void Validate(ValidationRun run)
    {
        var attribution = run.Configuration.Attribution;
        var supported = run.Context.SupportedAttributionSources;
        if (supported.Count > 0 && !supported.Contains(attribution.Source))
        {
            run.Error("attribution.source", ConfigValidationCodes.SourceUnsupportedOnHost,
                "The Linux service takes the DC Security events from OpenWEC only (source OpenWec or None).",
                attribution.Source.ToString());
        }

        if (attribution.Collector is { Length: > 0 } collector)
        {
            HostName(run, "attribution.collector", collector);
        }

        for (var i = 0; i < attribution.DomainControllers.Count; i++)
        {
            if (attribution.DomainControllers[i] is { } host)
            {
                HostName(run, ValidationRun.Item("attribution.domainControllers", i), host);
            }
        }
    }

    private static void HostName(ValidationRun run, string path, string value)
    {
        if (!HostNameSyntax().IsMatch(value) || !HostNames.IsHostName(HostNames.Normalize(value)))
        {
            run.Error(path, ConfigValidationCodes.InvalidHostName,
                "Domain controllers and the event collector are given by DNS host name (letters, digits, '.' and '-'; no IP address).");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9.-]{1,253}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HostNameSyntax();
}
