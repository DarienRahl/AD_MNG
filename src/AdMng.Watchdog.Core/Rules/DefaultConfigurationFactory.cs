using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Core.Rules;

/// <summary>
/// The default runtime configuration (section 8.1): record defaults for every section, the recipient groups
/// <c>it-admins</c> / <c>helpdesk</c> without addresses, no channels, SMTP disabled and the built-in rules of 12.4.
/// The service saves it as version 1 on first start; <c>GET /config/defaults</c> returns it for "reset to default".
/// </summary>
public sealed class DefaultConfigurationFactory : IDefaultConfigurationFactory
{
    /// <inheritdoc/>
    public WatchdogConfiguration Create() => new()
    {
        RecipientGroups =
        [
            new RecipientGroup { Id = DefaultRules.ItAdminsGroup, Name = "Administratorzy IT", DetailLevel = DetailLevel.Full },
            new RecipientGroup { Id = DefaultRules.HelpdeskGroup, Name = "Helpdesk", DetailLevel = DetailLevel.Standard },
        ],
        Rules = DefaultRules.Create(),
    };

    /// <summary>
    /// A new channel with the type's defaults (section 8.1 examples): Teams → detail level Standard with default
    /// <see cref="TeamsChannelOptions"/>; webhook → detail level Minimal with default <see cref="WebhookChannelOptions"/>.
    /// The URL and secrets are empty (entered as <see cref="SecretField.NewValue"/> by the admin).
    /// </summary>
    public static ChannelSettings CreateChannel(string id, ChannelType type, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return type switch
        {
            ChannelType.Teams => new ChannelSettings
            {
                Id = id,
                Type = type,
                Name = name,
                DetailLevel = DetailLevel.Standard,
                Teams = new TeamsChannelOptions(),
            },
            ChannelType.Webhook => new ChannelSettings
            {
                Id = id,
                Type = type,
                Name = name,
                DetailLevel = DetailLevel.Minimal,
                Webhook = new WebhookChannelOptions(),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown channel type."),
        };
    }
}
