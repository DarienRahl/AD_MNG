using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Contracts.Api;

/// <summary><c>POST /api/v1/test/email</c>.</summary>
public sealed record TestEmailRequest
{
    public IReadOnlyList<string> Recipients { get; init; } = [];
    /// <summary>Unsaved settings to test. Secrets come from SecretField.NewValue; a stored secret is used only when the
    /// unsaved settings have the same destination binding as the stored one (section 18) – otherwise the request is
    /// rejected (400, code secretRebindRequired) before anything is sent. Null = saved settings.</summary>
    public SmtpSettings? Settings { get; init; }
    public override string ToString() => $"TestEmailRequest({Recipients.Count} recipients)";
}

/// <summary><c>POST /api/v1/test/channels/{channelId}</c>.</summary>
public sealed record TestChannelRequest
{
    /// <summary>Unsaved channel (same binding rule as TestEmailRequest); null = saved channel {channelId}.</summary>
    public ChannelSettings? Settings { get; init; }
    public override string ToString() => "TestChannelRequest";
}
