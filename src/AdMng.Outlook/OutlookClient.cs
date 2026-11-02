using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AdMng.Core.Correspondence.Outlook;

namespace AdMng.Outlook;

/// <summary>
/// Classic Outlook through COM late binding (design note docs/dev/u5-outlook-correspondence.md 4.1):
/// <see cref="Availability"/> is the cached ProgID lookup (no start, no attach); <see cref="ConnectAsync"/> attaches to the
/// running Outlook or starts it on a new STA worker (3 attempts, 700 ms apart) and loads the profile and its accounts. The
/// constructor never touches COM. Off Windows nothing is called: <see cref="OutlookAvailability.NotSupported"/>.
/// </summary>
/// <remarks>
/// An Outlook the client had to start runs without a window, and Outlook started by automation exits as soon as its last
/// reference is released – <c>Send()</c> only puts an item into the Outbox, so the messages sent last would wait there until
/// someone opens Outlook. The client therefore keeps one reference to an Outlook it started, on a thread of its own, until
/// it is disposed (the app ends), as MailerWPiA kept its application object for the program's life; sessions come and go
/// (a U1 run reconnects after every failed message) without Outlook closing in between. Outlook the user runs is never held.
/// </remarks>
public sealed class OutlookClient : IOutlookClient, IAsyncDisposable, IDisposable
{
    /// <summary>The ProgID of classic Outlook ("new Outlook" does not register it).</summary>
    public const string ProgId = "Outlook.Application";

    /// <summary>Connection attempts (Outlook may be starting or waiting for its profile).</summary>
    public const int Attempts = 3;

    /// <summary>The pause between two connection attempts.</summary>
    public static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(700);

    /// <summary>How long a connection waits for the reference kept (it goes on in the background after that).</summary>
    private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long disposing waits for the reference kept to be released (a hung Outlook must not hang the app).</summary>
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(30);

    private readonly Lazy<OutlookAvailability> _availability = new(Probe);
    private readonly Func<bool> _isElevated;
    private readonly Lock _keepAliveGate = new();
    private StaWorker? _keepAliveWorker;
    private object? _kept;
    private int _disposed;
    private int _staleCleaned;

    /// <param name="time">The clock of the attempts and the PC's time zone (null: the system clock).</param>
    /// <param name="signatures">The Outlook signatures listed by the diagnostics ("Sprawdź Outlooka"), or null.</param>
    public OutlookClient(TimeProvider? time = null, IOutlookSignatureSource? signatures = null)
        : this(null, time, signatures)
    {
    }

    /// <param name="provider">The source of the Outlook application object (tests pass CLR fakes); null: COM.</param>
    /// <param name="time">The clock of the attempts (null: the system clock).</param>
    /// <param name="signatures">The signatures of the diagnostics, or null.</param>
    /// <param name="tempFiles">The temp folders of inline images (null: <c>%TEMP%</c>).</param>
    /// <param name="release">Releases one COM object (null: <see cref="ComRelease.Release"/>; tests record it).</param>
    /// <param name="isElevated">Whether the process runs elevated (null: <see cref="Environment.IsPrivilegedProcess"/>).</param>
    /// <param name="listSeparator">The Windows list separator, Outlook's delimiter of the voting options (null:
    /// <see cref="OutlookVotingOptions.UserListSeparator"/>).</param>
    internal OutlookClient(
        IOutlookApplicationProvider? provider,
        TimeProvider? time = null,
        IOutlookSignatureSource? signatures = null,
        OutlookTempFiles? tempFiles = null,
        Action<object>? release = null,
        Func<bool>? isElevated = null,
        Func<string>? listSeparator = null)
    {
        Provider = provider;
        Time = time ?? TimeProvider.System;
        Signatures = signatures;
        TempFiles = tempFiles ?? new OutlookTempFiles();
        Release = release ?? ComRelease.Release;
        _isElevated = isElevated ?? (() => Environment.IsPrivilegedProcess);
        ListSeparator = listSeparator ?? OutlookVotingOptions.UserListSeparator;
    }

    /// <summary>Whether classic Outlook is registered; with a test provider always <see cref="OutlookAvailability.Installed"/>.</summary>
    public OutlookAvailability Availability => Provider is null ? _availability.Value : OutlookAvailability.Installed;

    /// <summary>The application object source of the tests, or null (COM).</summary>
    internal IOutlookApplicationProvider? Provider { get; }

    internal TimeProvider Time { get; }

    internal IOutlookSignatureSource? Signatures { get; }

    internal OutlookTempFiles TempFiles { get; }

    internal Action<object> Release { get; }

    internal Func<string> ListSeparator { get; }

    /// <summary>
    /// Attaches to the running Outlook or starts it, and reads the profile: up to <see cref="Attempts"/> attempts,
    /// <see cref="AttemptDelay"/> apart (not repeated when Outlook is not installed or the process is elevated). Failures are
    /// returned classified (design note 4.3); off Windows <see cref="OutlookFailureKind.NotSupported"/> without any COM call.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public async Task<OutlookConnectResult> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IOutlookApplicationProvider provider;
        if (Provider is not null)
        {
            provider = Provider;
        }
        else if (!OperatingSystem.IsWindows())
        {
            return new OutlookConnectResult(null, OutlookErrors.Failure(OutlookFailureKind.NotSupported, OutlookSteps.Connect));
        }
        else if (Availability == OutlookAvailability.NotInstalled)
        {
            return new OutlookConnectResult(null, OutlookErrors.Failure(OutlookFailureKind.NotInstalled, OutlookSteps.Connect));
        }
        else
        {
            provider = new ComOutlookApplicationProvider();
        }

        await CleanupStaleOnceAsync().ConfigureAwait(false);
        var worker = new StaWorker();
        var handedOver = false;
        try
        {
            Exception? last = null;
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                try
                {
                    var (application, profile, started) = await worker.RunAsync(() => Open(provider), cancellationToken).ConfigureAwait(false);
                    var session = new OutlookSession(
                        worker, application, profile, new OutlookSessionOptions(TempFiles, Time, Signatures, Release) { ListSeparator = ListSeparator });
                    handedOver = true;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await session.DisposeAsync().ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    if (started)
                    {
                        await KeepAliveAsync(provider).ConfigureAwait(false);
                    }

                    return new OutlookConnectResult(session, null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (OutlookErrors.Classify(ex, OutlookSteps.Connect, _isElevated()).Kind
                        is OutlookFailureKind.NotInstalled or OutlookFailureKind.NotSupported or OutlookFailureKind.Elevated)
                    {
                        break;
                    }
                }

                if (attempt < Attempts)
                {
                    await Task.Delay(AttemptDelay, Time, cancellationToken).ConfigureAwait(false);
                }
            }

            return new OutlookConnectResult(null, OutlookErrors.Classify(last!, OutlookSteps.Connect, _isElevated()));
        }
        finally
        {
            if (!handedOver)
            {
                await worker.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public override string ToString() => nameof(OutlookClient);

    /// <summary>Releases the reference kept to an Outlook the client started (which may then close) and ends its thread.
    /// The sessions are not touched: each is disposed by its owner.</summary>
    public async ValueTask DisposeAsync()
    {
        StaWorker? worker;
        lock (_keepAliveGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            worker = _keepAliveWorker;
            _keepAliveWorker = null;
        }

        if (worker is null)
        {
            return;
        }

        try
        {
            await worker.RunAsync(() => ReleaseKept(), CancellationToken.None).WaitAsync(ReleaseTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            // The thread is gone, or a COM call hangs: the background thread ends with the process.
        }

        await worker.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc cref="DisposeAsync"/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static OutlookAvailability Probe() => OperatingSystem.IsWindows() ? ProbeOnWindows() : OutlookAvailability.NotSupported;

    [SupportedOSPlatform("windows")]
    private static OutlookAvailability ProbeOnWindows() =>
        Type.GetTypeFromProgID(ProgId) is null ? OutlookAvailability.NotInstalled : OutlookAvailability.Installed;

    /// <summary>On the worker: attach (else start) and read the profile; the application is released when that fails.
    /// <c>Started</c>: Outlook had to be started (no running one could be attached to).</summary>
    private (object Application, OutlookProfile Profile, bool Started) Open(IOutlookApplicationProvider provider)
    {
        object application;
        var started = false;
        try
        {
            application = provider.Attach() ?? throw new COMException(null, OutlookErrors.MkEUnavailable);
        }
        catch (Exception)
        {
            // Not running (MK_E_UNAVAILABLE) or not reachable: start it (MailerWPiA).
            application = provider.Start() ?? throw new COMException(null, OutlookErrors.CoEServerExecFailure);
            started = true;
        }

        var scope = new ComScope(Release);
        try
        {
            return (application, OutlookProfileReader.Read(application, scope), started);
        }
        catch (Exception)
        {
            scope.Dispose();
            Release(application);
            throw;
        }
        finally
        {
            scope.Dispose();
        }
    }

    /// <summary>
    /// Keeps an Outlook the client started running: a reference of its own, taken on the keep-alive thread (created on first
    /// use) and replacing the one kept before (Outlook may have been closed and started again since). Best effort and
    /// bounded: without it Outlook closes with the last session, as before.
    /// </summary>
    private async Task KeepAliveAsync(IOutlookApplicationProvider provider)
    {
        StaWorker worker;
        lock (_keepAliveGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            worker = _keepAliveWorker ??= new StaWorker("AdMng Outlook keep-alive");
        }

        try
        {
            await worker.RunAsync(() => Keep(provider), CancellationToken.None).WaitAsync(KeepAliveTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Outlook refused the reference, hangs, or the client is being disposed: the session works all the same.
        }
    }

    /// <summary>On the keep-alive thread: Outlook's application object – starting Outlook returns the running one, Outlook
    /// runs once per user, and it is found even when it is not in the running object table – replaces the one kept.</summary>
    private bool Keep(IOutlookApplicationProvider provider)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        var application = provider.Start() ?? throw new COMException(null, OutlookErrors.CoEServerExecFailure);
        ReleaseKept();
        _kept = application;
        return true;
    }

    /// <summary>On the keep-alive thread: releases the reference kept, if any.</summary>
    private bool ReleaseKept()
    {
        var kept = _kept;
        _kept = null;
        if (kept is not null)
        {
            Release(kept);
        }

        return kept is not null;
    }

    /// <summary>Removes the temp folders of dead processes once per client (design note 4.1); best effort.</summary>
    private async Task CleanupStaleOnceAsync()
    {
        if (Interlocked.Exchange(ref _staleCleaned, 1) != 0)
        {
            return;
        }

        try
        {
            await Task.Run(() => TempFiles.CleanupStale()).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Never a reason not to connect: the next start tries again.
        }
    }
}

/// <summary>
/// The only place that creates COM objects (design note 4.1): attach to the running Outlook (oleaut32 <c>GetActiveObject</c>
/// via <c>CLSIDFromProgID</c>) or start it (<c>Activator.CreateInstance</c>). Tests pass CLR fakes of the object model.
/// </summary>
internal interface IOutlookApplicationProvider
{
    /// <summary>The running Outlook's application object.</summary>
    object Attach();

    /// <summary>A started Outlook's application object.</summary>
    object Start();
}

/// <summary>The COM provider: <c>GetActiveObject</c> of the ProgID's CLSID, else <c>Activator.CreateInstance</c>.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ComOutlookApplicationProvider : IOutlookApplicationProvider
{
    public object Attach()
    {
        Marshal.ThrowExceptionForHR(ComNative.CLSIDFromProgID(OutlookClient.ProgId, out var clsid));
        Marshal.ThrowExceptionForHR(ComNative.GetActiveObject(in clsid, 0, out var unknown));
        if (unknown == 0)
        {
            throw new COMException(null, OutlookErrors.MkEUnavailable);
        }

        try
        {
            return Marshal.GetObjectForIUnknown(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    public object Start()
    {
        var type = Type.GetTypeFromProgID(OutlookClient.ProgId) ?? throw new COMException(null, OutlookErrors.RegDbEClassNotReg);
        return Activator.CreateInstance(type) ?? throw new COMException(null, OutlookErrors.CoEServerExecFailure);
    }

    public override string ToString() => nameof(ComOutlookApplicationProvider);
}
