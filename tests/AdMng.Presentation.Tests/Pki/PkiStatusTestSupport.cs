using System.Globalization;
using AdMng.Core.Csv;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Pki;

namespace AdMng.Presentation.Tests.Pki;

/// <summary>
/// The window's PKI services over the fake directory with a cache whose calls the test holds, fails or lets report (stage
/// V2b-4, unit 3): a slow domain controller is a call that waits for the test, never a delay. The probes are the fakes of
/// <see cref="PkiHarness"/>.
/// </summary>
internal sealed class PkiStatusHarness
{
    private PkiStatusHarness(PkiHarness harness)
    {
        H = harness;
        Cache = new ScriptedPkiSnapshotService(harness.Snapshot);
        Services = new PkiServices(harness.B2.Services, new CsvCodec(), Cache, harness.TlsProbe, harness.HealthProbe);
    }

    public PkiHarness H { get; }

    public ScriptedPkiSnapshotService Cache { get; }

    public PkiServices Services { get; }

    public static async Task<PkiStatusHarness> CreateAsync(CultureInfo? culture = null) => new(await PkiHarness.CreateAsync(culture));

    public PkiOverviewDocumentViewModel Overview() => new(Services);

    public PkiTemplatesDocumentViewModel Templates() => new(Services);

    public PkiReadinessDocumentViewModel Readiness() => new(Services);

    public PkiSecurityReviewDocumentViewModel Review() => new(Services);
}

/// <summary>A call the test holds: it says that it got there, and goes on when the test lets it (or when it is cancelled).</summary>
internal sealed class PkiHold
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the call is being held.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Lets the held call go on.</summary>
    public void Release() => _release.TrySetResult();

    /// <summary>
    /// Called by the held call: says that it is here and waits for <see cref="Release"/>. It does not hear the token while it
    /// waits - the call that is on its way to the domain controller has not returned when the operator presses Stop, and
    /// "Stopping…" must be on the page until it does; once released it ends with the token, as the real service does.
    /// </summary>
    public async Task HoldAsync(CancellationToken cancellationToken)
    {
        _reached.TrySetResult();
        await _release.Task.WaitAsync(HoldLimit);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>How long a held call waits for the test at most (a failing test must not keep a thread for good).</summary>
    public static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(60);
}

/// <summary>
/// <see cref="IPkiSnapshotService"/> over the harness's cache (<see cref="DirectPkiSnapshotService"/>) with hooks: each call
/// first runs the hook the test set for it (hold it, report a progress, throw) and then the real read.
/// </summary>
internal sealed class ScriptedPkiSnapshotService(DirectPkiSnapshotService inner) : IPkiSnapshotService
{
    public Func<IProgress<PkiReadProgress>?, CancellationToken, Task>? OnSnapshot { get; set; }

    public Func<IProgress<PkiExpansionProgress>?, CancellationToken, Task>? OnExpansion { get; set; }

    public Func<CancellationToken, Task>? OnDomainControllers { get; set; }

    public Func<CancellationToken, Task>? OnDcCertificates { get; set; }

    public Func<IProgress<int>?, CancellationToken, Task>? OnHttp { get; set; }

    public int Reads { get; private set; }

    public PkiSnapshot? Current => inner.Current;

    public DateTimeOffset? ReadAt => inner.ReadAt;

    public bool IsBuilding => inner.IsBuilding;

    public PkiEnrollmentExpansion? Expansion => inner.Expansion;

    public IReadOnlyList<DcTlsCheck>? DcCertificates => inner.DcCertificates;

    public IReadOnlyList<PkiHttpCheckResult>? HttpResults => inner.HttpResults;

    public event EventHandler? Changed
    {
        add => inner.Changed += value;
        remove => inner.Changed -= value;
    }

    public async Task<PkiSnapshot> GetAsync(bool refresh, IProgress<PkiReadProgress>? progress, CancellationToken cancellationToken)
    {
        Reads++;
        if (OnSnapshot is { } hook)
        {
            await hook(progress, cancellationToken);
        }

        return await inner.GetAsync(refresh, progress, cancellationToken);
    }

    public async Task<PkiEnrollmentExpansion> GetExpansionAsync(bool refresh, IProgress<PkiExpansionProgress>? progress, CancellationToken cancellationToken)
    {
        if (OnExpansion is { } hook)
        {
            await hook(progress, cancellationToken);
        }

        return await inner.GetExpansionAsync(refresh, progress, cancellationToken);
    }

    public async Task<IReadOnlyList<PkiDomainController>> GetDomainControllersAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (OnDomainControllers is { } hook)
        {
            await hook(cancellationToken);
        }

        return await inner.GetDomainControllersAsync(refresh, cancellationToken);
    }

    public async Task<IReadOnlyList<DcTlsCheck>> GetDcCertificatesAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (OnDcCertificates is { } hook)
        {
            await hook(cancellationToken);
        }

        return await inner.GetDcCertificatesAsync(refresh, cancellationToken);
    }

    public async Task<IReadOnlyList<PkiHttpCheckResult>> CheckHttpAsync(IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (OnHttp is { } hook)
        {
            await hook(progress, cancellationToken);
        }

        return await inner.CheckHttpAsync(progress, cancellationToken);
    }

    public void Invalidate() => inner.Invalidate();
}

/// <summary>What a holder says, with the copy for the diagnostic log of each message (key, counts, result code).</summary>
internal sealed class LoggedStatus
{
    private readonly List<string> _logs = [];

    public LoggedStatus(PageStatusHolder status)
    {
        ArgumentNullException.ThrowIfNull(status);
        status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                lock (_logs)
                {
                    _logs.Add(status.Current.Log.ToString());
                }
            }
        };
    }

    /// <summary>The log copies of everything said so far.</summary>
    public IReadOnlyList<string> Logs
    {
        get
        {
            lock (_logs)
            {
                return [.. _logs];
            }
        }
    }
}
