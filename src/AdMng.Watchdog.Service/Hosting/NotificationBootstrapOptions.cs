namespace AdMng.Watchdog.Service.Hosting;

/// <summary>Bootstrap <c>Watchdog:Notifications</c> (spec 8.4).</summary>
public sealed record NotificationBootstrapOptions
{
    /// <summary>The SMTP host that ships as the default XOAUTH2 destination.</summary>
    public const string DefaultOAuthSmtpHost = "smtp.office365.com";

    /// <summary>Hosts that may receive an XOAUTH2 token; empty = <see cref="DefaultOAuthSmtpHost"/>.</summary>
    public string[] OAuthSmtpHosts { get; init; } = [];

    public IReadOnlyList<string> EffectiveOAuthSmtpHosts => OAuthSmtpHosts.Length == 0 ? [DefaultOAuthSmtpHost] : OAuthSmtpHosts;
}
