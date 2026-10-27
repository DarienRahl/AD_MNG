using AdMng.Core.Messaging;
using AdMng.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.UserMessages;

/// <summary>Stage of the message window.</summary>
public enum UserMessageStage
{
    Compose,
    Preview,
    Sending,
    Report,
}

/// <summary>Filter of the preview list.</summary>
public enum UserMessagePreviewFilter
{
    All,
    Ready,
    Warnings,
    Errors,
    Skipped,
    Excluded,

    /// <summary>Messages whose salutation is guessed or a fallback (U1d).</summary>
    Guessed,
}

/// <summary>An account to message (before the plan: its DN and RDN only).</summary>
public sealed class UserMessageRecipientViewModel(string distinguishedName)
{
    public string DistinguishedName { get; } = distinguishedName;

    public string Name { get; } = Core.Ldap.DistinguishedName.TryParse(distinguishedName, out var dn) ? dn.Name : distinguishedName;

    public string Location { get; } = Core.Ldap.DistinguishedName.TryParse(distinguishedName, out var parsed) && parsed.Parent is { } parent
        ? Navigation.AddressResolver.ToDisplayPath(parent.ToString())
        : string.Empty;
}

/// <summary>A value typed by the operator for <c>{$input:name}</c>.</summary>
public sealed partial class UserMessageInputViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;
}

/// <summary>
/// One planned message in the preview and, after sending, its line of the report. Holds the rendered message only in
/// memory while the window is open.
/// </summary>
public sealed partial class UserMessageItemViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly TimeZoneInfo _timeZone;

    public UserMessageItemViewModel(PlannedUserMessage planned, ILocalizer localizer, TimeZoneInfo timeZone)
    {
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        Planned = planned ?? throw new ArgumentNullException(nameof(planned));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        IsIncluded = planned.CanSend;
        Issues = [.. planned.Issues.Select(i => UserMessageText.Issue(localizer, i))];
    }

    public PlannedUserMessage Planned { get; }

    public string Name => Planned.Name;

    public string Login => Planned.Login ?? string.Empty;

    public PlannedMessageStatus Status => Planned.Status;

    public string StatusText => _localizer[UserMessageText.StatusKey(Planned.Status)];

    /// <summary>The message can be sent (ready or with warnings): only those can be included.</summary>
    public bool CanInclude => Planned.CanSend;

    /// <summary>Included in the send (the operator unticks to exclude).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExcluded))]
    public partial bool IsIncluded { get; set; }

    public bool IsExcluded => CanInclude && !IsIncluded;

    public string LanguageText => MessageLanguages.Code(Planned.Language).ToUpperInvariant();

    public string ToText => string.Join("; ", Planned.Message?.To.Select(a => a.ToString()) ?? []);

    public string CcText => string.Join("; ", Planned.Message?.Cc.Select(a => a.ToString()) ?? []);

    public string ReplyToText => Planned.Message?.ReplyTo?.ToString() ?? string.Empty;

    public string FromText => Planned.Message?.From.ToString() ?? string.Empty;

    public string Subject => Planned.Message?.Subject ?? string.Empty;

    public string TextBody => Planned.Message?.TextBody ?? string.Empty;

    public string HtmlBody => Planned.Message?.HtmlBody ?? string.Empty;

    /// <summary>Localized findings of the validation.</summary>
    public IReadOnlyList<string> Issues { get; }

    public string IssuesText => string.Join(" ", Issues);

    // ---- Report ----------------------------------------------------------------------------------------------------

    /// <summary>The result of the last send of this message, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReportStatus), nameof(ReportText), nameof(ReportDetail), nameof(ReportTime), nameof(CanRetry))]
    public partial UserMessageSendResult? Result { get; set; }

    public UserMessageReportEntry ReportEntry => new(Planned, Result, IsExcluded);

    public UserMessageReportStatus ReportStatus => ReportEntry.Status;

    public string ReportText => _localizer[UserMessageText.ReportStatusKey(ReportStatus)];

    /// <summary>SMTP code and hint of a failure, the reason of "not sent", the CC rejections of an accepted message.</summary>
    public string ReportDetail => UserMessageText.ResultDetail(_localizer, Result) is { Length: > 0 } detail
        ? detail
        : ReportStatus is UserMessageReportStatus.Invalid or UserMessageReportStatus.Skipped ? IssuesText : string.Empty;

    public string ReportTime => Result?.CompletedAt is { } at ? TimeZoneInfo.ConvertTime(at, _timeZone).ToString("T", _localizer.Culture) : string.Empty;

    public bool CanRetry => ReportEntry.CanRetry;
}
