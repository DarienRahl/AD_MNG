namespace AdMng.Watchdog.Contracts.Api;

public enum DeliveryStage { Resolve, Connect, Tls, Token, Authenticate, Sender, Recipients, Data, HttpRequest, HttpResponse }

/// <summary>Result of one stage of a test send.</summary>
public sealed record DeliveryStageResult
{
    public required DeliveryStage Stage { get; init; }
    public required bool Success { get; init; }
    public string? Detail { get; init; }          // "TLS 1.3 TLS_AES_256_GCM_SHA384", "250 2.0.0 OK", "HTTP 202"
    public int? StatusCode { get; init; }
    public string? EnhancedStatus { get; init; }  // "5.7.139"
    public TimeSpan Duration { get; init; }
}

/// <summary>Hint codes of <see cref="DeliveryTestResult.Hints"/> – the GUI shows a localized remedy for each.</summary>
public static class DeliveryHints
{
    public const string BasicAuthDisabled = "smtp.basicAuthDisabled";         // 535 5.7.139 / 550 5.7.30
    public const string SmtpAuthDisabledForMailbox = "smtp.smtpAuthDisabled"; // Set-CASMailbox -SmtpClientAuthenticationDisabled $false
    public const string DirectSendRejected = "smtp.directSendRejected";       // 550 5.7.68
    public const string CertificateUntrusted = "tls.certificateUntrusted";
    public const string RevocationUnavailable = "tls.revocationUnavailable";
    public const string StartTlsMissing = "smtp.startTlsMissing";
    public const string RetiredTeamsUrl = "teams.retiredUrl";                 // *.webhook.office.com / *.logic.azure.com
    public const string AcceptedNotDelivered = "teams.acceptedNotDelivered";  // always on 202
    public const string TeamsAuthMismatch = "teams.authMismatch";             // SAS vs OAuth error texts
    public const string WebhookGone = "webhook.gone";                         // 410
    public const string SecretRebindRequired = "secret.rebindRequired";       // stored secret bound to another destination
    public const string MessageTooLarge = "smtp.messageTooLarge";             // 5.3.4 / 5.2.3 / bare 552, SIZE smaller than the message (U1c)

    /// <summary>True when a rejection says the message is too large (<see cref="MessageTooLarge"/>): the enhanced code
    /// 5.3.4 (too big for the system) or 5.2.3 (over the administrative limit), or 552 without an enhanced code. 552 with
    /// another one is not – 552 5.2.2 is a full mailbox of one recipient.</summary>
    public static bool IsMessageTooLarge(int status, string? enhancedStatus) =>
        enhancedStatus is "5.3.4" or "5.2.3" || (status == 552 && enhancedStatus is null);
}

/// <summary>Result of a test send (<c>POST /api/v1/test/…</c>).</summary>
public sealed record DeliveryTestResult
{
    public required bool Success { get; init; }
    public IReadOnlyList<DeliveryStageResult> Stages { get; init; } = [];
    public IReadOnlyList<string> Hints { get; init; } = [];
    public TimeSpan Duration { get; init; }
}
