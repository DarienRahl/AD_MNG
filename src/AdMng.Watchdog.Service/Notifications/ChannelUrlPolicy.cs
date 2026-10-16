using System.Globalization;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Formatting.Webhooks;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Notifications;

/// <summary>
/// Send-time URL checks (spec 14.5, 15.7; the validator checks the same at save time): https only (Development: loopback
/// http), Teams URLs through <see cref="TeamsUrlValidator"/> (retired Office 365 connector hosts are refused), webhook
/// <see cref="WebhookChannelOptions.AllowedHosts"/> enforced.
/// </summary>
public static class ChannelUrlPolicy
{
    /// <summary>The URL to POST to, or the refusal.</summary>
    public static (Uri? Url, ClassifiedDelivery? Refusal) Check(ChannelSettings channel, string url, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IsFile || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return (null, ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, "url.invalid"));
        }

        var insecureAllowed = isDevelopment && uri.IsLoopback;
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && insecureAllowed))
        {
            return (null, ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, "insecureUrl"));
        }

        if (channel.Type == ChannelType.Teams)
        {
            var teams = TeamsUrlValidator.Validate(url, channel.Teams?.AuthMode ?? TeamsAuthMode.Sas, insecureAllowed);
            if (!teams.IsAccepted)
            {
                return (null, ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, teams.Code ?? "teamsUrlFormat", teams.Hint));
            }
        }
        else if (channel.Webhook?.AllowedHosts is { Count: > 0 } allowed && !allowed.Any(h => HostEquals(h, uri.IdnHost)))
        {
            return (null, ClassifiedDelivery.Failure(DeliveryOutcome.ChannelBroken, "hostNotAllowed"));
        }

        return (uri, null);
    }

    private static bool HostEquals(string allowed, string host)
    {
        var normalized = allowed.Trim().TrimEnd('.');
        try
        {
            normalized = new IdnMapping().GetAscii(normalized);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return string.Equals(normalized, host.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }
}
