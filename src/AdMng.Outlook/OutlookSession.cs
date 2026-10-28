using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook;

/// <summary>
/// A connected classic Outlook (design note docs/dev/u5-outlook-correspondence.md 4.1): holds the late-bound application
/// object; every method marshals to the session's STA worker (one thread per session, COM message filter); disposing
/// releases every COM object and ends the worker. Created only by <see cref="OutlookClient.ConnectAsync"/>.
/// </summary>
/// <remarks>
/// Calls are serialized on the worker in the order they were made. A call cancelled while it waits for the worker throws
/// <see cref="OperationCanceledException"/>; once an item is being built it is completed (a half-built item would be left
/// in "Wersje robocze"). Failures of <see cref="SendAsync"/> are returned; the other methods throw
/// <see cref="OutlookException"/>. A <see cref="OutlookFailureKind.Disconnected"/> failure ends <see cref="IsConnected"/>.
/// </remarks>
public sealed class OutlookSession : IOutlookSession
{
    private readonly StaWorker _worker;
    private readonly OutlookMailBuilder _builder;
    private readonly OutlookCampaigns _campaigns;
    private readonly IOutlookSignatureSource? _signatures;
    private readonly Action<object> _release;
    private int _disposed;
    private volatile bool _disconnected;

    /// <param name="worker">The worker thread that created <paramref name="application"/> (owned by the session).</param>
    /// <param name="application">The Outlook application object (a COM object, or a CLR fake in tests).</param>
    /// <param name="profile">The profile and accounts read while connecting.</param>
    /// <param name="options">Temp files, clock, signatures and the releaser of COM objects.</param>
    internal OutlookSession(StaWorker worker, object application, OutlookProfile profile, OutlookSessionOptions options)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        Application = application ?? throw new ArgumentNullException(nameof(application));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        ArgumentNullException.ThrowIfNull(options);
        _release = options.Release;
        _signatures = options.Signatures;
        _builder = new OutlookMailBuilder(application, options.TempFiles, options.Time.LocalTimeZone, options.Release);
        _campaigns = new OutlookCampaigns(application, options.Time.LocalTimeZone, options.Release);
    }

    public OutlookProfile Profile { get; }

    /// <summary>Not disposed and Outlook was not found disconnected.</summary>
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && !_disconnected;

    /// <summary>The late-bound application object (used only on the worker thread).</summary>
    internal object Application { get; }

    /// <summary>The worker thread of this session (tests).</summary>
    internal StaWorker Worker => _worker;

    /// <summary>Builds the item (design note 4.2) and sends it or saves it in "Wersje robocze"; failures are returned.</summary>
    /// <exception cref="ArgumentException">The message has no recipient.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the item was started.</exception>
    /// <exception cref="ObjectDisposedException">The session was disposed.</exception>
    public async Task<OutlookItemResult> SendAsync(OutlookMessage message, OutlookSendMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!HasRecipient(message))
        {
            throw new ArgumentException("The message has no recipient.", nameof(message));
        }

        ThrowIfDisposed();
        var result = await _worker.RunAsync(() => _builder.Send(message, mode), cancellationToken).ConfigureAwait(false);
        Observe(result.Failure);
        return result;
    }

    /// <summary>Builds and saves the item, reads it back as Outlook renders it and deletes the draft.</summary>
    /// <exception cref="OutlookException">A step failed (classified).</exception>
    public Task<OutlookRenderedMessage> RenderAsync(OutlookMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        return Observed(_worker.RunAsync(() => _builder.Render(message), cancellationToken));
    }

    /// <summary>
    /// "Sprawdź Outlooka" (MailerWPiA <c>Diagnose</c>): version, profile, accounts and current user read again, the signature
    /// names (when a signature source is configured), the bitness of the process and optionally a test item that is created
    /// and discarded without sending.
    /// </summary>
    /// <exception cref="OutlookException">Outlook cannot be reached (classified, step diagnose).</exception>
    public Task<OutlookDiagnostics> DiagnoseAsync(bool createTestItem, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var signatures = ReadSignatureNames();
        return Observed(_worker.RunAsync(
            () =>
            {
                OutlookProfile profile;
                var scope = new ComScope(_release);
                try
                {
                    profile = OutlookProfileReader.Read(Application, scope);
                }
                catch (Exception ex)
                {
                    throw new OutlookException(OutlookErrors.Classify(ex, OutlookSteps.Diagnose, isElevated: false));
                }
                finally
                {
                    scope.Dispose();
                }

                var testFailure = createTestItem ? _builder.TestItem() : null;
                return new OutlookDiagnostics(
                    profile.Version,
                    profile.ProfileName,
                    profile.Accounts,
                    profile.CurrentUserAddress,
                    signatures,
                    Environment.Is64BitProcess,
                    createTestItem ? testFailure is null : null,
                    testFailure);
            },
            cancellationToken));
    }

    /// <summary>The campaigns (send runs) of "Elementy wysłane" since <paramref name="since"/>, newest first.</summary>
    /// <exception cref="OutlookException">The search failed (classified).</exception>
    public Task<IReadOnlyList<OutlookCampaign>> FindCampaignsAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return Observed(_worker.RunAsync(() => _campaigns.Find(since), cancellationToken));
    }

    /// <summary>The voting responses of one campaign.</summary>
    /// <exception cref="ArgumentException">Not a campaign id (1–64 letters, digits or '-').</exception>
    /// <exception cref="OutlookException">The search failed (classified).</exception>
    public Task<IReadOnlyList<OutlookVoteResponse>> ReadResponsesAsync(string campaignId, CancellationToken cancellationToken)
    {
        if (!OutlookMailBuilder.IsCampaignId(campaignId))
        {
            throw new ArgumentException("Not a campaign id.", nameof(campaignId));
        }

        ThrowIfDisposed();
        return Observed(_worker.RunAsync(() => _campaigns.Responses(campaignId, Profile.CurrentUserAddress), cancellationToken));
    }

    /// <summary>Lets the calls already queued finish, releases the application object and ends the worker thread. Outlook
    /// itself keeps running.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _worker.RunAsync(() => _release(Application), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The worker is already gone.
        }

        await _worker.DisposeAsync().ConfigureAwait(false);
    }

    public override string ToString() => nameof(OutlookSession);

    /// <summary><see cref="System.Runtime.InteropServices.Marshal.ReleaseComObject"/> only when <paramref name="value"/> is a
    /// COM object (the fakes of the tests are CLR objects).</summary>
    internal static void Release(object? value) => ComRelease.Release(value);

    private static bool HasRecipient(OutlookMessage message) =>
        message.To.Concat(message.Cc).Concat(message.Bcc).Any(address => !string.IsNullOrWhiteSpace(address));

    private IReadOnlyList<string> ReadSignatureNames()
    {
        if (_signatures is null)
        {
            return [];
        }

        try
        {
            return _signatures.List();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task<T> Observed<T>(Task<T> operation)
    {
        try
        {
            return await operation.ConfigureAwait(false);
        }
        catch (OutlookException ex)
        {
            Observe(ex.Failure);
            throw;
        }
    }

    private void Observe(OutlookFailure? failure)
    {
        if (failure?.Kind == OutlookFailureKind.Disconnected)
        {
            _disconnected = true;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

/// <summary>What a session needs besides the application object.</summary>
/// <param name="TempFiles">The temp folders of inline images.</param>
/// <param name="Time">The clock and the PC's time zone (deferred delivery, dates read back).</param>
/// <param name="Signatures">The signature source of the diagnostics, or null.</param>
/// <param name="Release">Releases one COM object (<see cref="ComRelease.Release"/>; tests record it).</param>
internal sealed record OutlookSessionOptions(OutlookTempFiles TempFiles, TimeProvider Time, IOutlookSignatureSource? Signatures, Action<object> Release)
{
    public override string ToString() => nameof(OutlookSessionOptions);
}
