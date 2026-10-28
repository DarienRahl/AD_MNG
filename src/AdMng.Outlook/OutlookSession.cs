using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook;

/// <summary>
/// A connected classic Outlook (design note docs/dev/u5-outlook-correspondence.md 4.1): holds the late-bound application
/// object; every method marshals to the session's STA worker (one thread per session, COM message filter); disposing
/// releases every COM object and ends the worker. Created only by <see cref="OutlookClient.ConnectAsync"/>.
/// </summary>
public sealed class OutlookSession : IOutlookSession
{
    private const string Track = "U5 T3 outlook";

    /// <param name="application">The Outlook application object (a COM object, or a CLR fake in tests).</param>
    /// <param name="profile">The profile and accounts read while connecting.</param>
    internal OutlookSession(object application, OutlookProfile profile)
    {
        Application = application ?? throw new ArgumentNullException(nameof(application));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public OutlookProfile Profile { get; }

    public bool IsConnected => throw new NotImplementedException(Track);

    /// <summary>The late-bound application object (used only on the worker thread).</summary>
    internal object Application { get; }

    public Task<OutlookItemResult> SendAsync(OutlookMessage message, OutlookSendMode mode, CancellationToken cancellationToken) =>
        throw new NotImplementedException(Track);

    public Task<OutlookRenderedMessage> RenderAsync(OutlookMessage message, CancellationToken cancellationToken) =>
        throw new NotImplementedException(Track);

    public Task<OutlookDiagnostics> DiagnoseAsync(bool createTestItem, CancellationToken cancellationToken) =>
        throw new NotImplementedException(Track);

    public Task<IReadOnlyList<OutlookCampaign>> FindCampaignsAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        throw new NotImplementedException(Track);

    public Task<IReadOnlyList<OutlookVoteResponse>> ReadResponsesAsync(string campaignId, CancellationToken cancellationToken) =>
        throw new NotImplementedException(Track);

    public ValueTask DisposeAsync() => throw new NotImplementedException(Track);

    public override string ToString() => nameof(OutlookSession);
}
