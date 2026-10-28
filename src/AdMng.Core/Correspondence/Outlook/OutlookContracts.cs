using System.Collections.ObjectModel;
using AdMng.Core.Correspondence.Composition;
using AdMng.Core.Messaging.Images;

namespace AdMng.Core.Correspondence.Outlook;

/// <summary>
/// Classic Outlook on this computer (U5, design note <c>docs/dev/u5-outlook-correspondence.md</c> 3.9 and 4). Application
/// singleton; the constructor never touches COM.
/// </summary>
public interface IOutlookClient
{
    /// <summary>Whether classic Outlook is registered (ProgID "Outlook.Application") – no start, no attach. Off Windows: NotSupported.</summary>
    OutlookAvailability Availability { get; }

    /// <summary>Attaches to the running Outlook or starts it (3 attempts, 700 ms apart), loads profile and accounts.</summary>
    Task<OutlookConnectResult> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>Whether classic Outlook can be used on this computer.</summary>
public enum OutlookAvailability
{
    NotSupported,
    NotInstalled,
    Installed,
}

/// <summary>A connected Outlook: owns one STA thread; dispose releases every COM object.</summary>
public interface IOutlookSession : IAsyncDisposable
{
    OutlookProfile Profile { get; }

    bool IsConnected { get; }

    /// <summary>Builds the item (design note 4.2) and sends it or saves it in "Wersje robocze". Failures are returned.</summary>
    Task<OutlookItemResult> SendAsync(OutlookMessage message, OutlookSendMode mode, CancellationToken cancellationToken);

    /// <summary>Builds, saves, reads and deletes the draft: the message as Outlook renders it.</summary>
    Task<OutlookRenderedMessage> RenderAsync(OutlookMessage message, CancellationToken cancellationToken);

    /// <summary>"Sprawdź Outlooka": version, profile, accounts, signatures, bitness, optionally a test item.</summary>
    Task<OutlookDiagnostics> DiagnoseAsync(bool createTestItem, CancellationToken cancellationToken);

    /// <summary>The send runs (campaign ids) found in "Elementy wysłane" since <paramref name="since"/>.</summary>
    Task<IReadOnlyList<OutlookCampaign>> FindCampaignsAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>The voting responses of one campaign.</summary>
    Task<IReadOnlyList<OutlookVoteResponse>> ReadResponsesAsync(string campaignId, CancellationToken cancellationToken);
}

/// <summary>An account of the Outlook profile.</summary>
public sealed record OutlookAccount(string DisplayName, string SmtpAddress)
{
    /// <summary>"Display &lt;smtp&gt;", or the display name alone when it is the address (or the address is unknown).</summary>
    public override string ToString() =>
        string.IsNullOrWhiteSpace(SmtpAddress) || string.Equals(DisplayName, SmtpAddress, StringComparison.OrdinalIgnoreCase)
            ? DisplayName
            : string.IsNullOrWhiteSpace(DisplayName) ? SmtpAddress : $"{DisplayName} <{SmtpAddress}>";
}

/// <summary>The Outlook profile of a session: version, profile name, accounts and the current user's address.</summary>
public sealed record OutlookProfile(string Version, string ProfileName, IReadOnlyList<OutlookAccount> Accounts, string? CurrentUserAddress)
{
    public override string ToString() => nameof(OutlookProfile);
}

/// <summary>Classified Outlook failures (design note 4.3).</summary>
public enum OutlookFailureKind
{
    NotSupported,
    NotInstalled,
    Elevated,
    Busy,
    NotResponding,
    SecurityDenied,
    Disconnected,
    RecipientUnresolved,
    AttachmentMissing,
    InvalidAccount,
    ItemTooLarge,
    Cancelled,
    Unknown,
}

/// <summary>
/// A classified failure. Exception messages are never kept (they may contain addresses); only the HRESULT and the step.
/// </summary>
/// <param name="Kind">The classification.</param>
/// <param name="Code">"outlook.&lt;kind&gt;" camel case, e.g. <c>outlook.notInstalled</c> (the table of design note 4.3).</param>
/// <param name="Step">A stable step id (design note 4.2), never a value; null outside building an item.</param>
/// <param name="HResult">The HRESULT, kept for the report.</param>
public sealed record OutlookFailure(OutlookFailureKind Kind, string Code, string? Step, int? HResult)
{
    /// <summary>The run cannot continue: NotInstalled, Elevated, SecurityDenied, Disconnected, NotSupported.</summary>
    public bool StopsRun => Kind is OutlookFailureKind.NotInstalled or OutlookFailureKind.Elevated or OutlookFailureKind.SecurityDenied
        or OutlookFailureKind.Disconnected or OutlookFailureKind.NotSupported;

    /// <summary>A later attempt may succeed: Busy, NotResponding.</summary>
    public bool IsTransient => Kind is OutlookFailureKind.Busy or OutlookFailureKind.NotResponding;
}

/// <summary>A connected session, or why there is none.</summary>
public sealed record OutlookConnectResult(IOutlookSession? Session, OutlookFailure? Failure);

/// <summary>A file attached to every message of the run (Korespondencja only).</summary>
public sealed record OutlookAttachment(string Path, string? DisplayName = null)
{
    public override string ToString() => nameof(OutlookAttachment);
}

/// <summary>The importance of an item (Outlook's <c>OlImportance</c>).</summary>
public enum OutlookImportance
{
    Low = 0,
    Normal = 1,
    High = 2,
}

/// <summary>Send the item, or only save it in "Wersje robocze".</summary>
public enum OutlookSendMode
{
    Send,
    Draft,
}

/// <summary>One message to build in Outlook (design note 4.2).</summary>
public sealed record OutlookMessage
{
    public required IReadOnlyList<string> To { get; init; }

    public IReadOnlyList<string> Cc { get; init; } = [];

    public IReadOnlyList<string> Bcc { get; init; } = [];

    public IReadOnlyList<string> ReplyTo { get; init; } = [];

    public required string Subject { get; init; }

    /// <summary>The whole document, <c>cid:</c> images.</summary>
    public required string HtmlBody { get; init; }

    public IReadOnlyList<MessageImage> InlineImages { get; init; } = [];

    public IReadOnlyList<OutlookAttachment> Attachments { get; init; } = [];

    /// <summary>The SMTP address of a profile account; null = the default account.</summary>
    public string? SendUsingAccount { get; init; }

    /// <summary>"W imieniu" (SentOnBehalfOfName), or null.</summary>
    public string? SentOnBehalfOf { get; init; }

    public bool ReadReceipt { get; init; }

    public DateTimeOffset? DeferredDelivery { get; init; }

    /// <summary>"Tak;Nie" – semicolon separated, or null.</summary>
    public string? VotingOptions { get; init; }

    public OutlookImportance Importance { get; init; } = OutlookImportance.Normal;

    /// <summary>Stored as a named property for the "Odpowiedzi" tab.</summary>
    public string? CampaignId { get; init; }

    /// <summary>Keys of this message, e.g. the mailbox addresses it asks about.</summary>
    public IReadOnlyList<string> CampaignKeys { get; init; } = [];

    /// <summary>U1: Auto-Submitted, X-AdMng-Message (best effort).</summary>
    public IReadOnlyDictionary<string, string> InternetHeaders { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    public override string ToString() => nameof(OutlookMessage);
}

/// <summary>The outcome of one item.</summary>
/// <param name="Succeeded">Sent (or saved as a draft in <see cref="OutlookSendMode.Draft"/>).</param>
/// <param name="SavedAsDraft">The item is in "Wersje robocze" (draft mode, or a failure after <c>Save()</c>).</param>
/// <param name="Failure">Why it failed, or null.</param>
public sealed record OutlookItemResult(bool Succeeded, bool SavedAsDraft, OutlookFailure? Failure)
{
    public static OutlookItemResult Sent { get; } = new(true, false, null);

    public static OutlookItemResult Draft { get; } = new(true, true, null);
}

/// <summary>A message as Outlook renders it ("Podgląd z Outlooka"): the HTML with <c>cid:</c> images read into memory.</summary>
public sealed record OutlookRenderedMessage(string Html, IReadOnlyDictionary<string, OutlookRenderedImage> Images, int Attachments)
{
    public override string ToString() => $"{nameof(OutlookRenderedMessage)} ({Images.Count} images)";
}

/// <summary>An inline image of <see cref="OutlookRenderedMessage"/>.</summary>
public sealed record OutlookRenderedImage(string ContentId, string MediaType, byte[] Data)
{
    public override string ToString() => $"{nameof(OutlookRenderedImage)}({ContentId}, {MediaType}, {Data.Length} B)";
}

/// <summary>"Sprawdź Outlooka".</summary>
public sealed record OutlookDiagnostics(string Version, string ProfileName, IReadOnlyList<OutlookAccount> Accounts,
    string? CurrentUserAddress, IReadOnlyList<string> Signatures, bool ProcessIs64Bit, bool? TestItemCreated, OutlookFailure? TestItemFailure)
{
    public override string ToString() => nameof(OutlookDiagnostics);
}

/// <summary>One send run found in "Elementy wysłane".</summary>
public sealed record OutlookCampaign(string Id, string Subject, DateTimeOffset FirstSent, DateTimeOffset LastSent, int Messages)
{
    public override string ToString() => $"{nameof(OutlookCampaign)} ({Messages})";
}

/// <summary>The voting response of one recipient of a campaign.</summary>
public sealed record OutlookVoteResponse(string Recipient, string? Response, DateTimeOffset? RespondedAt, DateTimeOffset SentAt,
    string Subject, IReadOnlyList<string> Keys)
{
    public override string ToString() => nameof(OutlookVoteResponse);
}

/// <summary>Outlook signatures: <c>%APPDATA%\Microsoft\Signatures</c> (file system, no COM).</summary>
public interface IOutlookSignatureSource
{
    /// <summary>The signatures folder, or null when there is none (not Windows).</summary>
    string? Folder { get; }

    /// <summary>The names (file name without .htm/.html), ordinal order.</summary>
    IReadOnlyList<string> List();

    /// <summary>Reads and parses one signature (<see cref="OutlookSignatureParser"/>); null when it does not exist.</summary>
    Task<SignatureParseResult?> ReadAsync(string name, int ansiCodePage, CancellationToken cancellationToken);
}

/// <summary>
/// A classified Outlook failure of an operation that has no result slot for it (<see cref="IOutlookSession.RenderAsync"/>,
/// <see cref="IOutlookSession.DiagnoseAsync"/>, <see cref="IOutlookSession.FindCampaignsAsync"/>,
/// <see cref="IOutlookSession.ReadResponsesAsync"/>). Like <see cref="OutlookFailure"/> it never carries the message of the
/// original exception (it may contain addresses) and has no inner exception: only the kind, the code, the step and the
/// HRESULT.
/// </summary>
public sealed class OutlookException : Exception
{
    public OutlookException(OutlookFailure failure)
        : base($"Outlook failed ({failure?.Code}, step {failure?.Step ?? "-"}).")
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
        if (failure.HResult is { } hresult)
        {
            HResult = hresult;
        }
    }

    /// <summary>The classified failure.</summary>
    public OutlookFailure Failure { get; }
}
