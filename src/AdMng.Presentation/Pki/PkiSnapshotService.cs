using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Pki;
using AdMng.Core.Pki.Tls;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;

namespace AdMng.Presentation.Pki;

/// <summary>
/// The per-window PKI cache (docs/dev/p-certificate-services.md 2.5, W5; modelled on <c>GpoSettingsIndexService</c>):
/// caches, keyed on the session instance and its server, the snapshot, the enrollment expansion, the domain controllers,
/// the DC certificate checks (at most 4 probes in parallel, the current DC first, 3269 only for GCs) and the HTTP results.
/// One build per item shared by all callers, run on <c>Task.Run</c> under the service's own cancellation linked to the
/// window's lifetime; <c>SessionChanged</c> and <see cref="Invalidate"/> cancel and drop everything; <see cref="Changed"/>
/// fires after each replacement; <see cref="Dispose"/> unsubscribes and cancels.
/// </summary>
/// <remarks>
/// A caller's token ends only that caller's wait; the build is cancelled when its last waiting caller cancels (as
/// <c>GpoSettingsIndexService</c>). <c>refresh</c> starts a build unless one runs (then it joins it). The expansion, the DC
/// checks and the HTTP results are derived from a snapshot: a refreshed snapshot drops them (and cancels their builds over
/// the old one), and a derived build stores its result only when its snapshot is still the current one. Everything stays in
/// memory (rule 1); every read goes to the window session's DC (rule 4) – the only other traffic is the TLS probe of the DCs
/// and the HTTP check on the operator's button (decision 1).
/// </remarks>
public sealed class PkiSnapshotService : IPkiSnapshotService, IDisposable
{
    /// <summary>The probe's own timeout (decision 1).</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>At most this many TLS probes at a time (decision 1, rule 6).</summary>
    public const int MaxParallelProbes = 4;

    /// <summary>LDAPS.</summary>
    public const int LdapsPort = 636;

    /// <summary>LDAPS of the global catalog (only for a DC with the GC flag).</summary>
    public const int GlobalCatalogLdapsPort = 3269;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts;
    private readonly Slot<PkiSnapshot> _snapshot = new(derived: false);
    private readonly Slot<PkiEnrollmentExpansion> _expansion = new(derived: true);
    private readonly Slot<IReadOnlyList<PkiDomainController>> _controllers = new(derived: false);
    private readonly Slot<IReadOnlyList<DcTlsCheck>> _dcCertificates = new(derived: true);
    private readonly Slot<IReadOnlyList<PkiHttpCheckResult>> _http = new(derived: true);
    private IDirectorySession? _session;
    private string? _server;
    private long _generation;
    private bool _disposed;

    /// <param name="ui">The window's UI services (session manager, dispatcher, clock, lifetime).</param>
    /// <param name="tlsProbe">The DC LDAPS probe (null: <see cref="GetDcCertificatesAsync"/> reports it unavailable).</param>
    /// <param name="healthProbe">The HTTP check (null: <see cref="CheckHttpAsync"/> reports it unavailable).</param>
    public PkiSnapshotService(DialogServices ui, IDomainControllerTlsProbe? tlsProbe, IPkiHealthProbe? healthProbe)
    {
        Ui = ui ?? throw new ArgumentNullException(nameof(ui));
        TlsProbe = tlsProbe;
        HealthProbe = healthProbe;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ui.Connection?.Lifetime ?? CancellationToken.None);
        Ui.Sessions.SessionChanged += OnSessionChanged;
    }

    /// <summary>The window's UI services.</summary>
    public DialogServices Ui { get; }

    /// <summary>The DC LDAPS probe, or null.</summary>
    public IDomainControllerTlsProbe? TlsProbe { get; }

    /// <summary>The HTTP check, or null.</summary>
    public IPkiHealthProbe? HealthProbe { get; }

    public PkiSnapshot? Current => Read(_snapshot);

    public DateTimeOffset? ReadAt => Current?.ReadAt;

    public bool IsBuilding
    {
        get
        {
            lock (_gate)
            {
                return Slots.Any(s => s.IsRunning);
            }
        }
    }

    public PkiEnrollmentExpansion? Expansion => Read(_expansion);

    public IReadOnlyList<DcTlsCheck>? DcCertificates => Read(_dcCertificates);

    public IReadOnlyList<PkiHttpCheckResult>? HttpResults => Read(_http);

    public event EventHandler? Changed;

    private ISlot[] Slots => [_snapshot, _expansion, _controllers, _dcCertificates, _http];

    public Task<PkiSnapshot> GetAsync(bool refresh, IProgress<PkiReadProgress>? progress, CancellationToken cancellationToken) =>
        GetOrBuildAsync(_snapshot, refresh, progress, ReadSnapshotAsync, cancellationToken);

    public Task<PkiEnrollmentExpansion> GetExpansionAsync(bool refresh, IProgress<PkiExpansionProgress>? progress, CancellationToken cancellationToken) =>
        GetOrBuildAsync(_expansion, refresh, progress, ExpandAsync, cancellationToken);

    public Task<IReadOnlyList<PkiDomainController>> GetDomainControllersAsync(bool refresh, CancellationToken cancellationToken) =>
        GetOrBuildAsync(_controllers, refresh, null, (session, _, token) => PkiOverviewReader.ReadDomainControllersAsync(session, token), cancellationToken);

    public Task<IReadOnlyList<DcTlsCheck>> GetDcCertificatesAsync(bool refresh, CancellationToken cancellationToken) =>
        GetOrBuildAsync(_dcCertificates, refresh, null, ProbeDomainControllersAsync, cancellationToken);

    /// <summary>Always a new check (the operator pressed the button), unless one runs: then its results.</summary>
    public Task<IReadOnlyList<PkiHttpCheckResult>> CheckHttpAsync(IProgress<int>? progress, CancellationToken cancellationToken) =>
        GetOrBuildAsync(_http, refresh: true, progress, CheckHttpCoreAsync, cancellationToken);

    public void Invalidate()
    {
        List<IBuild> cancel;
        lock (_gate)
        {
            cancel = DropLocked();
            _session = null;
            _server = null;
        }

        foreach (var build in cancel)
        {
            build.Cancel();
        }

        RaiseChanged();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Ui.Sessions.SessionChanged -= OnSessionChanged;
        Invalidate();
        Changed = null;
        try
        {
            _cts.Cancel();
        }
        finally
        {
            _cts.Dispose();
        }
    }

    /// <summary>For diagnostics: what is cached (never a value).</summary>
    public override string ToString()
    {
        lock (_gate)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{nameof(PkiSnapshotService)}(snapshot {_snapshot.HasValue}, expansion {_expansion.HasValue}, DCs {_controllers.HasValue}, LDAPS {_dcCertificates.HasValue}, HTTP {_http.HasValue}, building {Slots.Any(s => s.IsRunning)})");
        }
    }

    // ---- The items ---------------------------------------------------------------------------------------------------

    private Task<PkiSnapshot> ReadSnapshotAsync(IDirectorySession session, Build<PkiSnapshot> build, CancellationToken cancellationToken) =>
        PkiOverviewReader.ReadAsync(
            session,
            new PkiReadOptions { Time = Ui.Time, PageSize = Math.Clamp(Ui.Settings.Current.PageSize, 1, 5000) },
            new BuildProgress<PkiReadProgress>(build),
            cancellationToken);

    /// <summary>The group graph once, the object types, the classifier (CA computers Tier 0), then the one account pass.</summary>
    private async Task<PkiEnrollmentExpansion> ExpandAsync(IDirectorySession session, Build<PkiEnrollmentExpansion> build, CancellationToken cancellationToken)
    {
        var snapshot = await BasisAsync(build, cancellationToken).ConfigureAwait(false);
        var info = session.Info;
        var head = await session.GetAsync(info.DefaultNamingContext, [Attr.ObjectSid], cancellationToken).ConfigureAwait(false);
        var domainSid = head?.ObjectSid;
        var rootSid = await RootDomainSidAsync(session, domainSid, cancellationToken).ConfigureAwait(false);
        var settings = Ui.Settings.Current;
        var options = new PrivilegedAccessOptions
        {
            DomainSid = domainSid,
            RootDomainSid = rootSid,
            LevelOverrides = settings.AccountReview.Privileges.OverrideMap(),
            PageSize = Math.Clamp(settings.PageSize, 1, 5000),
        };
        var groups = await GroupGraph.ReadAsync(session, null, options.PageSize, cancellationToken).ConfigureAwait(false);
        var types = await AclObjectTypes.ReadAsync(session, options.PageSize, cancellationToken).ConfigureAwait(false);
        var classifier = new PrincipalClassifier(domainSid, rootSid, options, groups, [.. snapshot.EnterpriseCas.Select(c => c.ComputerSid).OfType<string>()]);
        return await TemplateRightsEvaluator.ExpandAsync(
            session,
            snapshot.Templates,
            new PkiExpansionContext(groups, types, classifier, options),
            new BuildProgress<PkiExpansionProgress>(build),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The DCs of the configuration partition (GC flag) joined with the domain's DC objects, the current DC first, each probed
    /// on 636 (and 3269 for a GC), at most <see cref="MaxParallelProbes"/> at a time; a presented chain is judged against the
    /// snapshot's trust material at the window's clock.
    /// </summary>
    private async Task<IReadOnlyList<DcTlsCheck>> ProbeDomainControllersAsync(IDirectorySession session, Build<IReadOnlyList<DcTlsCheck>> build, CancellationToken cancellationToken)
    {
        var snapshot = await BasisAsync(build, cancellationToken).ConfigureAwait(false);
        if (TlsProbe is not { } probe)
        {
            return [];
        }

        var published = await GetDomainControllersAsync(false, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DirectoryDomainController> catalog;
        try
        {
            catalog = await DomainControllerCatalog.ReadAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.AccessDenied or DirectoryErrorKind.NoSuchObject)
        {
            catalog = [];
        }

        var targets = Targets(session.Info.Server, catalog, published);
        var domain = session.Info.DomainDnsName;
        var now = Ui.Time.GetUtcNow();
        using var gate = new SemaphoreSlim(MaxParallelProbes);
        var checks = targets.Select(async target =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await probe.ProbeAsync(target.Host, target.Port, ProbeTimeout, cancellationToken).ConfigureAwait(false);
                var certificate = result.Outcome == TlsProbeOutcome.Ok
                    ? DcCertificateEvaluation.Evaluate(result.PresentedChain, target.Host, domain, snapshot.TrustMaterial, now)
                    : null;
                return new DcTlsCheck(target.Host, target.Port, result.Outcome, certificate);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        return await Task.WhenAll(checks).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PkiHttpCheckResult>> CheckHttpCoreAsync(IDirectorySession session, Build<IReadOnlyList<PkiHttpCheckResult>> build, CancellationToken cancellationToken)
    {
        var snapshot = await BasisAsync(build, cancellationToken).ConfigureAwait(false);
        return HealthProbe is { } probe
            ? await probe.CheckAsync(PkiHealth.HttpUrls(snapshot), new BuildProgress<int>(build), cancellationToken).ConfigureAwait(false)
            : [];
    }

    /// <summary>
    /// The hosts to probe: the configuration partition's DCs (with their GC flag) and the DC objects of the domain it does not
    /// list (636 only), each once; the window's DC first, then by name. Ports 636, and 3269 for a GC.
    /// </summary>
    internal static IReadOnlyList<(string Host, int Port)> Targets(string? currentServer, IReadOnlyList<DirectoryDomainController> catalog, IReadOnlyList<PkiDomainController> published)
    {
        var hosts = new List<(string Host, bool Gc)>();
        foreach (var dc in catalog)
        {
            if (!string.IsNullOrWhiteSpace(dc.HostName) && !hosts.Any(h => DomainControllerCatalog.IsSameHost(h.Host, dc.HostName)))
            {
                hosts.Add((dc.HostName, dc.IsGlobalCatalog));
            }
        }

        foreach (var dc in published)
        {
            if (dc.HostName is { Length: > 0 } host && !hosts.Any(h => DomainControllerCatalog.IsSameHost(h.Host, host)))
            {
                hosts.Add((host, false));
            }
        }

        return
        [
            .. hosts
                .OrderByDescending(h => DomainControllerCatalog.IsSameHost(h.Host, currentServer))
                .ThenBy(h => h.Host, StringComparer.OrdinalIgnoreCase)
                .SelectMany(h => h.Gc ? new[] { (h.Host, LdapsPort), (h.Host, GlobalCatalogLdapsPort) } : [(h.Host, LdapsPort)]),
        ];
    }

    /// <summary>The forest root's domain SID: the window's own in a single-domain forest, else read from the root's head (null when not readable).</summary>
    private static async Task<string?> RootDomainSidAsync(IDirectorySession session, string? domainSid, CancellationToken cancellationToken)
    {
        var root = session.Info.RootDomainNamingContext;
        if (root is null || DistinguishedName.AreEqual(root, session.Info.DefaultNamingContext))
        {
            return domainSid;
        }

        try
        {
            return (await session.GetAsync(root, [Attr.ObjectSid], cancellationToken).ConfigureAwait(false))?.ObjectSid;
        }
        catch (DirectoryException ex) when (!ex.IsTransient)
        {
            return null;
        }
    }

    /// <summary>The snapshot a derived build works on (the cached one, or the running build's); recorded as the build's basis.</summary>
    private async Task<PkiSnapshot> BasisAsync<T>(Build<T> build, CancellationToken cancellationToken)
        where T : class
    {
        var snapshot = await GetAsync(false, null, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            build.Basis = snapshot;
        }

        return snapshot;
    }

    // ---- The shared builds -------------------------------------------------------------------------------------------

    private T? Read<T>(Slot<T> slot)
        where T : class
    {
        lock (_gate)
        {
            return slot.Value;
        }
    }

    private async Task<T> GetOrBuildAsync<T>(
        Slot<T> slot,
        bool refresh,
        object? progress,
        Func<IDirectorySession, Build<T>, CancellationToken, Task<T>> run,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        Build<T> build;
        var start = false;
        List<IBuild> dropped;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = Ui.Sessions.Current ?? throw new SessionEndedException();
            dropped = EnsureKeyLocked(session);
            if (!refresh && slot.Value is { } cached && dropped.Count == 0)
            {
                return cached;
            }

            if (slot.Running is { IsCancelled: false } running)
            {
                build = running;
            }
            else
            {
                build = new Build<T>(session, _generation, _cts.Token);
                slot.Running = build;
                start = true;
            }

            build.Waiters++;
            build.AddProgress(progress);
        }

        foreach (var old in dropped)
        {
            old.Cancel();
        }

        if (start)
        {
            _ = RunAsync(slot, build, run);
        }

        if (start || dropped.Count > 0)
        {
            RaiseChanged();
        }

        var left = false;
        try
        {
            return await build.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !build.Completion.Task.IsCompleted)
        {
            left = true;
            Leave(slot, build, progress, cancelled: true);
            throw;
        }
        finally
        {
            if (!left)
            {
                Leave(slot, build, progress, cancelled: false);
            }
        }
    }

    private async Task RunAsync<T>(Slot<T> slot, Build<T> build, Func<IDirectorySession, Build<T>, CancellationToken, Task<T>> run)
        where T : class
    {
        T? value = null;
        Exception? failure = null;
        try
        {
            value = await Task.Run(() => run(build.Session, build, build.Token), build.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        var stored = false;
        List<IBuild> cancel = [];
        lock (_gate)
        {
            if (ReferenceEquals(slot.Running, build))
            {
                slot.Running = null;
            }

            if (value is not null && build.Generation == _generation && !build.Token.IsCancellationRequested && !_disposed
                && ReferenceEquals(_session, build.Session) && ReferenceEquals(Ui.Sessions.Current, build.Session)
                && (!slot.Derived || ReferenceEquals(build.Basis, _snapshot.Value)))
            {
                var replaced = slot.Value is not null;
                slot.Value = value;
                slot.Basis = build.Basis;
                stored = true;
                if (ReferenceEquals(slot, _snapshot) && replaced)
                {
                    cancel = DropDerivedLocked((PkiSnapshot)(object)value);
                }
            }
        }

        foreach (var other in cancel)
        {
            other.Cancel();
        }

        if (stored)
        {
            build.Completion.TrySetResult(value!);
        }
        else if (failure is not null and not OperationCanceledException)
        {
            build.Completion.TrySetException(failure);
        }
        else
        {
            build.Completion.TrySetCanceled();
        }

        build.Dispose();
        RaiseChanged();
    }

    /// <summary>A waiter is done: the last one that cancelled cancels the build.</summary>
    private void Leave<T>(Slot<T> slot, Build<T> build, object? progress, bool cancelled)
        where T : class
    {
        var cancel = false;
        lock (_gate)
        {
            build.Waiters--;
            build.RemoveProgress(progress);
            if (cancelled && build.Waiters == 0 && !build.Completion.Task.IsCompleted)
            {
                if (ReferenceEquals(slot.Running, build))
                {
                    slot.Running = null;
                }

                cancel = true;
            }
        }

        if (cancel)
        {
            build.Cancel();
            RaiseChanged();
        }
    }

    /// <summary>
    /// The cache belongs to <paramref name="session"/> (instance and server): otherwise everything is dropped (a reconnect
    /// to the same server is another instance and is never reused). Returns the builds to cancel.
    /// </summary>
    private List<IBuild> EnsureKeyLocked(IDirectorySession session)
    {
        if (ReferenceEquals(_session, session) && string.Equals(_server, session.Info.Server, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var dropped = _session is null && !Slots.Any(s => s.HasValue || s.IsRunning) ? [] : DropLocked();
        _session = session;
        _server = session.Info.Server;
        return dropped;
    }

    /// <summary>Drops every value and detaches every running build (the caller cancels them outside the lock).</summary>
    private List<IBuild> DropLocked()
    {
        _generation++;
        var cancel = new List<IBuild>();
        foreach (var slot in Slots)
        {
            if (slot.Detach() is { } running)
            {
                cancel.Add(running);
            }
        }

        return cancel;
    }

    /// <summary>A new snapshot replaced the old one: the items derived from another snapshot are dropped, their builds cancelled.</summary>
    private List<IBuild> DropDerivedLocked(PkiSnapshot current)
    {
        var cancel = new List<IBuild>();
        foreach (var slot in Slots.Where(s => s.Derived))
        {
            if (slot.HasValue && !ReferenceEquals(slot.BasisObject, current))
            {
                slot.ClearValue();
            }

            if (slot.RunningBasis is { } basis && !ReferenceEquals(basis, current) && slot.Detach() is { } running)
            {
                cancel.Add(running);
            }
        }

        return cancel;
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Invalidate();

    /// <summary>Raises <see cref="Changed"/> on the UI dispatcher.</summary>
    private void RaiseChanged()
    {
        if (Changed is null)
        {
            return;
        }

        _ = Ui.Dispatcher.RunAsync(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private interface ISlot
    {
        bool Derived { get; }

        bool HasValue { get; }

        bool IsRunning { get; }

        object? BasisObject { get; }

        object? RunningBasis { get; }

        void ClearValue();

        /// <summary>Drops the value and returns the running build (no longer the slot's).</summary>
        IBuild? Detach();
    }

    private interface IBuild
    {
        void Cancel();

        /// <summary>Hands a progress value to the waiters that wait with a sink of its type.</summary>
        void Report<TProgress>(TProgress value);
    }

    /// <summary>One cached item: its value (and the snapshot it was derived from) and its running build.</summary>
    private sealed class Slot<T>(bool derived) : ISlot
        where T : class
    {
        public bool Derived { get; } = derived;

        public T? Value { get; set; }

        public PkiSnapshot? Basis { get; set; }

        public Build<T>? Running { get; set; }

        public bool HasValue => Value is not null;

        public bool IsRunning => Running is not null;

        public object? BasisObject => Basis;

        public object? RunningBasis => Running?.Basis;

        public void ClearValue()
        {
            Value = null;
            Basis = null;
        }

        public IBuild? Detach()
        {
            ClearValue();
            var running = Running;
            Running = null;
            return running;
        }

        public override string ToString() => $"{typeof(T).Name}: {(HasValue ? "cached" : "empty")}{(IsRunning ? ", building" : string.Empty)}";
    }

    /// <summary>One build with its waiters, their progress sinks and its own cancellation (linked to the service's).</summary>
    private sealed class Build<T>(IDirectorySession session, long generation, CancellationToken service) : IBuild, IDisposable
        where T : class
    {
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(service);
        private readonly List<object> _progress = [];
        private int _disposed;
        private bool _cancelled;

        public IDirectorySession Session { get; } = session;

        public long Generation { get; } = generation;

        /// <summary>The snapshot a derived build works on (set once it has it).</summary>
        public PkiSnapshot? Basis { get; set; }

        public int Waiters { get; set; }

        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token => _cts.Token;

        /// <summary><see cref="Cancel"/> ran, or the service's cancellation ended: joining it would only end in a cancellation.</summary>
        public bool IsCancelled => Volatile.Read(ref _cancelled) || service.IsCancellationRequested;

        public void AddProgress(object? progress)
        {
            if (progress is null)
            {
                return;
            }

            lock (_progress)
            {
                _progress.Add(progress);
            }
        }

        public void RemoveProgress(object? progress)
        {
            if (progress is null)
            {
                return;
            }

            lock (_progress)
            {
                _progress.Remove(progress);
            }
        }

        public void Report<TProgress>(TProgress value)
        {
            object[] sinks;
            lock (_progress)
            {
                sinks = [.. _progress];
            }

            foreach (var sink in sinks)
            {
                (sink as IProgress<TProgress>)?.Report(value);
            }
        }

        public void Cancel()
        {
            Volatile.Write(ref _cancelled, true);
            if (Volatile.Read(ref _disposed) == 0)
            {
                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Finished meanwhile.
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cts.Dispose();
            }
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{typeof(T).Name} #{Generation}, {Waiters} waiting");
    }

    /// <summary>The Core reader's progress fanned out to the build's waiters.</summary>
    private sealed class BuildProgress<TProgress>(IBuild build) : IProgress<TProgress>
    {
        public void Report(TProgress value) => build.Report(value);
    }
}
