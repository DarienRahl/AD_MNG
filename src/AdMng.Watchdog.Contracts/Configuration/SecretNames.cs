namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>Canonical names of the secrets kept in ISecretStore (section 18).</summary>
public static class SecretNames
{
    public const string SmtpPassword = "smtp.password";
    public const string SmtpOAuthClientSecret = "smtp.oauth.clientSecret";
    public static string ChannelUrl(string channelId) => $"channel.{channelId}.url";
    public static string ChannelClientSecret(string channelId) => $"channel.{channelId}.clientSecret";
    public static string WebhookSigningSecret(string channelId) => $"channel.{channelId}.signingSecret";
    public static string WebhookPreviousSigningSecret(string channelId) => $"channel.{channelId}.previousSigningSecret";
}
