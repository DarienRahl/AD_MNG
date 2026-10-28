using System.Collections.Concurrent;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Messaging;

namespace AdMng.Testing;

/// <summary>
/// In-memory <see cref="IOutlookClient"/> for Presentation and UI tests (design note docs/dev/u5-outlook-correspondence.md
/// 4.5): <see cref="Availability"/>, a scripted <see cref="ConnectFailure"/>, the <see cref="Profile"/> of new sessions and
/// everything a <see cref="FakeOutlookSession"/> answers: scripted results per recipient address (a queue per address, then
/// <see cref="DefaultResult"/>), the record of every item with its mode, <see cref="RenderResult"/>,
/// <see cref="DiagnoseResult"/>, <see cref="Campaigns"/>, <see cref="Responses"/> and a <see cref="BeforeSend"/> hook.
/// Never starts or attaches to Outlook. Thread-safe.
/// </summary>
public sealed class FakeOutlookClient : IOutlookClient
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<OutlookItemResult>> _scripted = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<FakeOutlookSession> _sessions = new();
    private int _connections;

    /// <summary>The sample profile: two accounts of Jan Kowalski's Outlook (Polish names, contoso.test).</summary>
    public static OutlookProfile SampleProfile { get; } = new(
        "16.0.0.17928",
        "Outlook",
        [
            new OutlookAccount("Jan Kowalski", "jan.kowalski@contoso.test"),
            new OutlookAccount("Sekretariat – Łódź", "sekretariat.lodz@contoso.test"),
        ],
        "jan.kowalski@contoso.test");

    public OutlookAvailability Availability { get; set; } = OutlookAvailability.Installed;

    /// <summary>Returned by <see cref="ConnectAsync"/> instead of a session when set.</summary>
    public OutlookFailure? ConnectFailure { get; set; }

    /// <summary>The profile of the sessions connected from now on.</summary>
    public OutlookProfile Profile { get; set; } = SampleProfile;

    /// <summary>The result of items without a scripted answer; null: sent (<see cref="OutlookSendMode.Send"/>) or saved as a
    /// draft (<see cref="OutlookSendMode.Draft"/>).</summary>
    public OutlookItemResult? DefaultResult { get; set; }

    /// <summary>What <see cref="FakeOutlookSession.RenderAsync"/> answers; null: the message itself (its HTML and images).</summary>
    public OutlookRenderedMessage? RenderResult { get; set; }

    /// <summary>Thrown by <see cref="FakeOutlookSession.RenderAsync"/> as an <see cref="OutlookException"/> when set.</summary>
    public OutlookFailure? RenderFailure { get; set; }

    /// <summary>What <see cref="FakeOutlookSession.DiagnoseAsync"/> answers; null: built from the session's profile.</summary>
    public OutlookDiagnostics? DiagnoseResult { get; set; }

    /// <summary>The signature names of the default diagnostics.</summary>
    public IReadOnlyList<string> Signatures { get; set; } = ["Jan Kowalski – podpis"];

    /// <summary>The campaigns of "Elementy wysłane" (filtered by the date of the request).</summary>
    public List<OutlookCampaign> Campaigns { get; } = [];

    /// <summary>The responses per campaign id.</summary>
    public Dictionary<string, List<OutlookVoteResponse>> Responses { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Thrown by the campaign and response reads as an <see cref="OutlookException"/> when set.</summary>
    public OutlookFailure? ReadFailure { get; set; }

    /// <summary>Called before every item is "built" (e.g. to cancel a run in the middle or to check the message).</summary>
    public Action<OutlookMessage, OutlookSendMode>? BeforeSend { get; set; }

    /// <summary>Every item sent or saved (also the failed ones), in order.</summary>
    public ConcurrentQueue<FakeOutlookItem> Items { get; } = new();

    /// <summary>Every message rendered, in order.</summary>
    public ConcurrentQueue<OutlookMessage> Rendered { get; } = new();

    /// <summary>The sessions connected so far, in order.</summary>
    public IReadOnlyList<FakeOutlookSession> Sessions => [.. _sessions];

    /// <summary>The number of <see cref="ConnectAsync"/> calls (also the failed ones).</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>The messages of the items that succeeded in <paramref name="mode"/>.</summary>
    public IReadOnlyList<OutlookMessage> Succeeded(OutlookSendMode mode = OutlookSendMode.Send) =>
        [.. Items.Where(item => item.Mode == mode && item.Result.Succeeded).Select(item => item.Message)];

    /// <summary>Queues results for the next items whose first recipient (To, then Cc, then Bcc) is <paramref name="address"/>.</summary>
    public void Script(string address, params OutlookItemResult[] results)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var queue = _scripted.GetOrAdd(address.Trim(), _ => new ConcurrentQueue<OutlookItemResult>());
        foreach (var result in results)
        {
            queue.Enqueue(result);
        }
    }

    /// <summary>A failed item: the classified failure of <paramref name="kind"/> (code as the engine writes it).</summary>
    public static OutlookItemResult Failed(OutlookFailureKind kind, string? step = "send", bool savedAsDraft = false, int? hresult = null) =>
        new(false, savedAsDraft, Failure(kind, step, hresult));

    /// <summary>A failure of <paramref name="kind"/> with the code of the engine (design note 4.3).</summary>
    public static OutlookFailure Failure(OutlookFailureKind kind, string? step = null, int? hresult = null) => new(kind, Code(kind), step, hresult);

    /// <summary>The code of a kind: <c>outlook.&lt;kind&gt;</c> (design note 4.3).</summary>
    public static string Code(OutlookFailureKind kind) => kind switch
    {
        OutlookFailureKind.NotSupported => "outlook.notSupported",
        OutlookFailureKind.NotInstalled => "outlook.notInstalled",
        OutlookFailureKind.Elevated => "outlook.elevated",
        OutlookFailureKind.Busy => "outlook.busy",
        OutlookFailureKind.NotResponding => "outlook.notResponding",
        OutlookFailureKind.SecurityDenied => "outlook.denied",
        OutlookFailureKind.Disconnected => "outlook.disconnected",
        OutlookFailureKind.RecipientUnresolved => "outlook.recipient",
        OutlookFailureKind.AttachmentMissing => "outlook.attachment",
        OutlookFailureKind.InvalidAccount => "outlook.account",
        OutlookFailureKind.ItemTooLarge => "outlook.tooLarge",
        OutlookFailureKind.Cancelled => "outlook.cancelled",
        _ => "outlook.error",
    };

    public Task<OutlookConnectResult> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _connections);
        if (Availability == OutlookAvailability.NotSupported)
        {
            return Task.FromResult(new OutlookConnectResult(null, Failure(OutlookFailureKind.NotSupported, "connect")));
        }

        if (Availability == OutlookAvailability.NotInstalled)
        {
            return Task.FromResult(new OutlookConnectResult(null, Failure(OutlookFailureKind.NotInstalled, "connect")));
        }

        if (ConnectFailure is { } failure)
        {
            return Task.FromResult(new OutlookConnectResult(null, failure));
        }

        var session = new FakeOutlookSession(this, Profile);
        _sessions.Enqueue(session);
        return Task.FromResult(new OutlookConnectResult(session, null));
    }

    public override string ToString() => nameof(FakeOutlookClient);

    internal OutlookItemResult Answer(OutlookMessage message, OutlookSendMode mode)
    {
        BeforeSend?.Invoke(message, mode);
        var result = Scripted(message) ?? DefaultResult ?? (mode == OutlookSendMode.Draft ? OutlookItemResult.Draft : OutlookItemResult.Sent);
        Items.Enqueue(new FakeOutlookItem(message, mode, result));
        return result;
    }

    private OutlookItemResult? Scripted(OutlookMessage message)
    {
        foreach (var address in message.To.Concat(message.Cc).Concat(message.Bcc))
        {
            if (!string.IsNullOrWhiteSpace(address) && _scripted.TryGetValue(address.Trim(), out var queue))
            {
                return queue.TryDequeue(out var result) ? result : null;
            }
        }

        return null;
    }
}

/// <summary>A session of <see cref="FakeOutlookClient"/>: answers from the client's script and records into it.</summary>
public sealed class FakeOutlookSession : IOutlookSession
{
    private readonly FakeOutlookClient _owner;
    private int _disposed;
    private volatile bool _connected = true;

    internal FakeOutlookSession(FakeOutlookClient owner, OutlookProfile profile)
    {
        _owner = owner;
        Profile = profile;
    }

    public OutlookProfile Profile { get; }

    /// <summary>True until disposed or set to false (a lost Outlook).</summary>
    public bool IsConnected
    {
        get => !IsDisposed && _connected;
        set => _connected = value;
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Task<OutlookItemResult> SendAsync(OutlookMessage message, OutlookSendMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return Task.FromResult(_owner.Answer(message, mode));
    }

    public Task<OutlookRenderedMessage> RenderAsync(OutlookMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _owner.Rendered.Enqueue(message);
        if (_owner.RenderFailure is { } failure)
        {
            throw new OutlookException(failure);
        }

        return Task.FromResult(_owner.RenderResult ?? new OutlookRenderedMessage(
            message.HtmlBody,
            message.InlineImages
                .GroupBy(image => image.ContentId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => new OutlookRenderedImage(group.Key, group.First().MediaType, group.First().Data), StringComparer.OrdinalIgnoreCase),
            message.InlineImages.Count + message.Attachments.Count));
    }

    public Task<OutlookDiagnostics> DiagnoseAsync(bool createTestItem, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return Task.FromResult(_owner.DiagnoseResult ?? new OutlookDiagnostics(
            Profile.Version, Profile.ProfileName, Profile.Accounts, Profile.CurrentUserAddress, _owner.Signatures, Environment.Is64BitProcess,
            createTestItem ? true : null, null));
    }

    public Task<IReadOnlyList<OutlookCampaign>> FindCampaignsAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_owner.ReadFailure is { } failure)
        {
            throw new OutlookException(failure);
        }

        return Task.FromResult<IReadOnlyList<OutlookCampaign>>(
            [.. _owner.Campaigns.Where(campaign => campaign.LastSent >= since).OrderByDescending(campaign => campaign.LastSent)]);
    }

    public Task<IReadOnlyList<OutlookVoteResponse>> ReadResponsesAsync(string campaignId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_owner.ReadFailure is { } failure)
        {
            throw new OutlookException(failure);
        }

        return Task.FromResult<IReadOnlyList<OutlookVoteResponse>>(
            _owner.Responses.TryGetValue(campaignId, out var responses) ? [.. responses] : []);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    public override string ToString() => nameof(FakeOutlookSession);
}

/// <summary>One item handed to <see cref="FakeOutlookSession.SendAsync"/>: the message, the mode and the answer.</summary>
public sealed record FakeOutlookItem(OutlookMessage Message, OutlookSendMode Mode, OutlookItemResult Result)
{
    public override string ToString() => $"{nameof(FakeOutlookItem)} {Mode} {(Result.Succeeded ? "ok" : Result.Failure?.Code)}";
}

/// <summary>
/// <see cref="IOutlookUserMessageTransportFactory"/> for Presentation tests: records the account and the on-behalf choice and
/// returns <see cref="Transport"/> (a <see cref="FakeUserMessageTransport"/> of kind Outlook).
/// </summary>
public sealed class FakeOutlookUserMessageTransportFactory : IOutlookUserMessageTransportFactory
{
    /// <summary>The transport every <see cref="Create"/> returns.</summary>
    public FakeUserMessageTransport Transport { get; } = new(UserMessageTransportKind.Outlook, "Outlook (default account)");

    /// <summary>The arguments of every <see cref="Create"/>, in order.</summary>
    public ConcurrentQueue<(string? Account, bool OnBehalfOfSender)> Created { get; } = new();

    public IUserMessageTransport Create(string? account, bool onBehalfOfSender)
    {
        Created.Enqueue((account, onBehalfOfSender));
        Transport.Description = $"Outlook ({(string.IsNullOrWhiteSpace(account) ? "default account" : account.Trim())})";
        return Transport;
    }

    public override string ToString() => nameof(FakeOutlookUserMessageTransportFactory);
}
