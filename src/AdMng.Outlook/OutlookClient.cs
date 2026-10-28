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
public sealed class OutlookClient : IOutlookClient
{
    /// <summary>The ProgID of classic Outlook ("new Outlook" does not register it).</summary>
    public const string ProgId = "Outlook.Application";

    /// <summary>Connection attempts (Outlook may be starting or waiting for its profile).</summary>
    public const int Attempts = 3;

    /// <summary>The pause between two connection attempts.</summary>
    public static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(700);

    private readonly Lazy<OutlookAvailability> _availability = new(Probe);
    private readonly Func<bool> _isElevated;
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
    internal OutlookClient(
        IOutlookApplicationProvider? provider,
        TimeProvider? time = null,
        IOutlookSignatureSource? signatures = null,
        OutlookTempFiles? tempFiles = null,
        Action<object>? release = null,
        Func<bool>? isElevated = null)
    {
        Provider = provider;
        Time = time ?? TimeProvider.System;
        Signatures = signatures;
        TempFiles = tempFiles ?? new OutlookTempFiles();
        Release = release ?? ComRelease.Release;
        _isElevated = isElevated ?? (() => Environment.IsPrivilegedProcess);
    }

    /// <summary>Whether classic Outlook is registered; with a test provider always <see cref="OutlookAvailability.Installed"/>.</summary>
    public OutlookAvailability Availability => Provider is null ? _availability.Value : OutlookAvailability.Installed;

    /// <summary>The application object source of the tests, or null (COM).</summary>
    internal IOutlookApplicationProvider? Provider { get; }

    internal TimeProvider Time { get; }

    internal IOutlookSignatureSource? Signatures { get; }

    internal OutlookTempFiles TempFiles { get; }

    internal Action<object> Release { get; }

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
                    var (application, profile) = await worker.RunAsync(() => Open(provider), cancellationToken).ConfigureAwait(false);
                    var session = new OutlookSession(worker, application, profile, new OutlookSessionOptions(TempFiles, Time, Signatures, Release));
                    handedOver = true;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await session.DisposeAsync().ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
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

    private static OutlookAvailability Probe() => OperatingSystem.IsWindows() ? ProbeOnWindows() : OutlookAvailability.NotSupported;

    [SupportedOSPlatform("windows")]
    private static OutlookAvailability ProbeOnWindows() =>
        Type.GetTypeFromProgID(ProgId) is null ? OutlookAvailability.NotInstalled : OutlookAvailability.Installed;

    /// <summary>On the worker: attach (else start) and read the profile; the application is released when that fails.</summary>
    private (object Application, OutlookProfile Profile) Open(IOutlookApplicationProvider provider)
    {
        object application;
        try
        {
            application = provider.Attach() ?? throw new COMException(null, OutlookErrors.MkEUnavailable);
        }
        catch (Exception)
        {
            // Not running (MK_E_UNAVAILABLE) or not reachable: start it (MailerWPiA).
            application = provider.Start() ?? throw new COMException(null, OutlookErrors.CoEServerExecFailure);
        }

        var scope = new ComScope(Release);
        try
        {
            return (application, OutlookProfileReader.Read(application, scope));
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

    /// <summary>Removes the temp folders of dead processes once per client (design note 4.1).</summary>
    private Task CleanupStaleOnceAsync() =>
        Interlocked.Exchange(ref _staleCleaned, 1) == 0 ? Task.Run(() => TempFiles.CleanupStale()) : Task.CompletedTask;
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
