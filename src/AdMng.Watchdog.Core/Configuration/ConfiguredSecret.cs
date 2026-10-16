using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Configuration;

/// <summary>One secret field of a configuration document with the destination binding it must have under these
/// settings (<see cref="SecretBindings.Enumerate"/>).</summary>
public sealed record ConfiguredSecret
{
    /// <summary>Canonical secret name (<see cref="SecretNames"/>) – the <c>ISecretStore</c> key.</summary>
    public required string Name { get; init; }

    /// <summary>JSON path of the field in the configuration document (<c>ConfigIssue.Path</c> style), e.g.
    /// <c>smtp.password</c>, <c>smtp.oAuth.clientSecret</c>, <c>channels[2].url</c>,
    /// <c>channels[2].teams.clientSecret</c>, <c>channels[2].webhook.signingSecret</c>.</summary>
    public required string Path { get; init; }

    /// <summary>The field as it appears in the document (may carry <c>NewValue</c>/<c>Clear</c> on a PUT).</summary>
    public required SecretField Field { get; init; }

    /// <summary>The binding the stored secret must have for these settings (section 18). Never contains a secret.</summary>
    public required string Binding { get; init; }

    /// <summary>Id of the channel the secret belongs to; null for the SMTP secrets.</summary>
    public string? ChannelId { get; init; }

    /// <summary>The settings consume this secret: SMTP password with <c>Basic</c>, SMTP client secret with
    /// <c>OAuth2ClientCredentials</c>, every channel URL, the Teams client secret with <c>EntraClientCredentials</c>,
    /// the signing secrets of webhook channels. <c>Enabled</c> flags are deliberately ignored (a rebind is required as
    /// soon as the destination changes, not only when the channel is switched on again).</summary>
    public bool InUse { get; init; }

    public override string ToString() => $"ConfiguredSecret({Name}, {Path})";
}
