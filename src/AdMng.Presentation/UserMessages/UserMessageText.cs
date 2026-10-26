using AdMng.Core.Messaging;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Watchdog;

namespace AdMng.Presentation.UserMessages;

/// <summary>
/// Localized texts of messages to users. Key families: <c>UserMessages.Issue.{code}</c> (<see cref="MessageIssueCodes"/>,
/// argument {0}), <c>UserMessages.Status.{PlannedMessageStatus}</c>, <c>UserMessages.Report.{UserMessageReportStatus}</c>,
/// <c>UserMessages.NotSent.{UserMessageNotSentReason}</c>, <c>UserMessages.Condition.{MessageConditionKind}</c>,
/// <c>UserMessages.Mode.{MessageRecipientMode}</c>, <c>UserMessages.Filter.{UserMessagePreviewFilter}</c>,
/// <c>UserMessages.Tls.{SmtpTlsMode}</c>, <c>UserMessages.Certificate.{SmtpCertificateCheck}</c>,
/// <c>UserMessages.Auth.{SmtpAuthentication}</c>, <c>UserMessages.Transport.{UserMessageTransportChoice}</c>,
/// <c>UserMessages.Problem.{UserMessageTransportChoiceResult constant}</c>, <c>UserMessages.Code.{code}</c> (the transport's
/// own codes), <c>UserMessages.Stage.{SmtpTestStage}</c>.
/// </summary>
public static class UserMessageText
{
    /// <summary>Codes of the transports with their own text (others are shown as they are, with the hint).</summary>
    public static IReadOnlyList<string> KnownCodes { get; } =
        ["auth", "auth.noCredential", "auth.unencrypted", "auth.mechanismNotOffered", "config.host", "config.pin", "address", "tls", "timeout", "io", "smtp.notSupported"];

    public static string IssueKey(string code) => "UserMessages.Issue." + code;

    public static string StatusKey(PlannedMessageStatus status) => "UserMessages.Status." + status;

    public static string ReportStatusKey(UserMessageReportStatus status) => "UserMessages.Report." + status;

    public static string NotSentKey(UserMessageNotSentReason reason) => "UserMessages.NotSent." + reason;

    public static string ConditionKey(MessageConditionKind kind) => "UserMessages.Condition." + kind;

    public static string ModeKey(MessageRecipientMode mode) => "UserMessages.Mode." + mode;

    public static string FilterKey(UserMessagePreviewFilter filter) => "UserMessages.Filter." + filter;

    public static string TlsKey(Core.Messaging.SmtpTlsMode mode) => "UserMessages.Tls." + mode;

    public static string CertificateKey(SmtpCertificateCheck check) => "UserMessages.Certificate." + check;

    public static string AuthKey(SmtpAuthentication authentication) => "UserMessages.Auth." + authentication;

    public static string TransportKey(Settings.UserMessageTransportChoice choice) => "UserMessages.Transport." + choice;

    public static string ProblemKey(string problem) => "UserMessages.Problem." + problem;

    public static string CodeKey(string code) => "UserMessages.Code." + code;

    public static string StageKey(SmtpTestStage stage) => "UserMessages.Stage." + stage;

    /// <summary>Every key of the dynamic families (localization test).</summary>
    public static IEnumerable<string> AllDynamicKeys() =>
        MessageIssueCodes.All.Select(IssueKey)
            .Concat(Enum.GetValues<PlannedMessageStatus>().Select(StatusKey))
            .Concat(Enum.GetValues<UserMessageReportStatus>().Select(ReportStatusKey))
            .Concat(Enum.GetValues<UserMessageNotSentReason>().Where(r => r != UserMessageNotSentReason.None).Select(NotSentKey))
            .Concat(Enum.GetValues<MessageConditionKind>().Select(ConditionKey))
            .Concat(Enum.GetValues<MessageRecipientMode>().Select(ModeKey))
            .Concat(Enum.GetValues<UserMessagePreviewFilter>().Select(FilterKey))
            .Concat(Enum.GetValues<Core.Messaging.SmtpTlsMode>().Select(TlsKey))
            .Concat(Enum.GetValues<SmtpCertificateCheck>().Select(CertificateKey))
            .Concat(Enum.GetValues<SmtpAuthentication>().Select(AuthKey))
            .Concat(Enum.GetValues<Settings.UserMessageTransportChoice>().Select(TransportKey))
            .Concat(new[]
            {
                UserMessageTransportChoiceResult.SmtpNotConfigured, UserMessageTransportChoiceResult.SmtpUnavailable,
                UserMessageTransportChoiceResult.WatchdogNotConfigured, UserMessageTransportChoiceResult.WatchdogNotSupported,
            }.Select(ProblemKey))
            .Concat(KnownCodes.Select(CodeKey))
            .Concat(Enum.GetValues<SmtpTestStage>().Select(StageKey));

    /// <summary>"Brak adresu e-mail (mail ani proxyAddresses)." …</summary>
    public static string Issue(ILocalizer localizer, MessageIssue issue)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(issue);
        var argument = issue.Code == MessageIssueCodes.ConditionNotMet && Enum.TryParse<MessageConditionKind>(issue.Argument, out var kind)
            ? localizer[ConditionKey(kind)]
            : issue.Argument ?? string.Empty;
        return localizer.Format(IssueKey(issue.Code), argument);
    }

    /// <summary>A delivery: "smtp.550.5.1.1 – Odbiorca nie istnieje …" (code, own text or hint).</summary>
    public static string Delivery(ILocalizer localizer, UserMessageDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(delivery);
        var parts = new List<string> { delivery.Code };
        if (KnownCodes.Contains(delivery.Code))
        {
            parts.Add(localizer[CodeKey(delivery.Code)]);
        }

        if (delivery.Hint is { Length: > 0 } hint)
        {
            parts.Add(localizer.HintText(hint));
        }

        return string.Join(" – ", parts);
    }

    /// <summary>What the report shows next to the status of a result.</summary>
    public static string ResultDetail(ILocalizer localizer, UserMessageSendResult? result)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (result is null)
        {
            return string.Empty;
        }

        if (result.Status == UserMessageSendStatus.NotSent)
        {
            return result.NotSentReason == UserMessageNotSentReason.None ? string.Empty : localizer[NotSentKey(result.NotSentReason)];
        }

        if (result.Delivery is not { } delivery)
        {
            return string.Empty;
        }

        if (delivery.IsAccepted)
        {
            return delivery.RejectedCopies.Count > 0
                ? localizer.Format(L.UserMessages.CopiesRejected, string.Join(", ", delivery.RejectedCopies))
                : string.Empty;
        }

        return Delivery(localizer, delivery);
    }
}
