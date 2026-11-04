using System.Globalization;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Messaging.Images;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Settings;
using AdMng.Presentation.UserMessages;

namespace AdMng.Presentation.Correspondence;

public static partial class CorrespondenceText
{
    /// <summary><c>Correspondence.SendMode.{mode}</c>.</summary>
    public static string SendModeKey(CorrespondenceSendMode mode) => "Correspondence.SendMode." + mode;

    /// <summary><c>Correspondence.Width.{width}</c>.</summary>
    public static string WidthKey(CorrespondencePreviewWidth width) => "Correspondence.Width." + width;

    /// <summary><c>Correspondence.Lint.{kind}</c> ({0} = the detail of the finding).</summary>
    public static string LintKey(MailLintKind kind) => "Correspondence.Lint." + kind;

    /// <summary><c>Correspondence.Message.SignatureProblem.{code}</c> (<see cref="SignatureProblemCodes"/>, {0} = the detail).</summary>
    public static string SignatureProblemKey(string code) => "Correspondence.Message.SignatureProblem." + code;

    /// <summary>
    /// The localized text of a signature problem (<see cref="SignatureProblemCodes.Format"/>): its code's text with the detail
    /// – the reason of a refused image in the operator's language (a too large one with the Korespondencja limit). An unknown
    /// code is shown as written.
    /// </summary>
    public static string SignatureProblem(ILocalizer localizer, string problem)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(problem);
        var (code, detail) = SignatureProblemCodes.Split(problem);
        if (!SignatureProblemCodes.All.Contains(code, StringComparer.Ordinal))
        {
            return problem;
        }

        var shown = code != SignatureProblemCodes.ImageRejected
            ? detail ?? string.Empty
            : string.Equals(detail, nameof(MessageImageError.TooLarge), StringComparison.Ordinal)
                ? localizer.Format(L.CorrespondenceMessage.ImageTooLargeReason, Size(localizer, CorrespondenceLimits.MaxImageBytes))   // not U1's 1 MB
                : UserMessageText.Reason(localizer, detail);
        return localizer.Format(SignatureProblemKey(code), shown);
    }

    /// <summary>
    /// The localized warning of a compatibility finding: the pixels of <see cref="MailLintKind.TooWide"/>, the host of
    /// <see cref="MailLintKind.LinkTextMismatch"/>, the size of <see cref="MailLintKind.LargeMessage"/> ("21,4 MB").
    /// </summary>
    public static string Lint(ILocalizer localizer, MailLintFinding finding)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(finding);
        var detail = finding.Kind == MailLintKind.LargeMessage && long.TryParse(finding.Detail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
            ? Size(localizer, bytes)
            : finding.Detail ?? string.Empty;
        return localizer.Format(LintKey(finding.Kind), detail);
    }

    /// <summary>A size as the window shows it: "18 KB", "1,4 MB" (in the UI culture).</summary>
    public static string Size(ILocalizer localizer, long bytes)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return MessageImageItemViewModel.Bytes(Math.Max(0, bytes), localizer.Culture);
    }

    /// <summary>Dynamic keys of the message and preview pages (track message).</summary>
    private static partial IEnumerable<string> MessageKeys() =>
    [
        .. Enum.GetValues<CorrespondenceSendMode>().Select(SendModeKey),
        .. Enum.GetValues<CorrespondencePreviewWidth>().Select(WidthKey),
        .. Enum.GetValues<MailLintKind>().Select(LintKey),
        .. SignatureProblemCodes.All.Select(SignatureProblemKey),
    ];
}
