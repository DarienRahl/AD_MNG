using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.GroupPolicy.Search;

/// <summary>
/// The settings index of one connection window (K7, docs/dev/k-computers-and-gpo.md 21.3): scoped, registered with
/// <see cref="IGpoSettingsIndexService"/> forwarded to the same instance. It keeps the current <see cref="GpoIndexView"/>
/// in memory only (CLAUDE.md rule 1), keyed on the session instance and its server, and owns every build: its own SYSVOL
/// connection (opened and disposed by the build) and its own cancellation, linked to the window's lifetime. A caller's
/// token only ends that caller's wait; the build is cancelled by <see cref="Invalidate"/>, <see cref="CancelBuild"/>,
/// <see cref="Dispose"/> or when the last waiting caller cancels. <see cref="GroupPolicyServices.Changed"/> marks the view
/// stale (no automatic rebuild); <see cref="AdMng.Presentation.Session.ISessionManager.SessionChanged"/> drops it. Both handlers are removed
/// in <see cref="Dispose"/>.
/// </summary>
public sealed class GpoSettingsIndexService : IGpoSettingsIndexService, IDisposable
{
    private readonly GroupPolicyServices _services;
    private readonly Lock _gate = new();
    private GpoIndexView? _view;
    private IDirectorySession? _viewSession;
    private string? _viewServer;
    private bool _stale;
    private long _generation;
    private Build? _running;
    private Build? _queued;
    private GpoIndexProgress? _progress;
    private bool _disposed;
    private int _statePosted;

    public GpoSettingsIndexService(GroupPolicyServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _services.Changed += OnGroupPolicyChanged;
        _services.Sessions.SessionChanged += OnSessionChanged;
    }

    public GpoIndexView? Current
    {
        get
        {
            lock (_gate)
            {
                return _view;
            }
        }
    }

    public bool IsStale
    {
        get
        {
            lock (_gate)
            {
                return _view is not null && _stale;
            }
        }
    }

    public bool IsBuilding
    {
        get
        {
            lock (_gate)
            {
                return _running is not null;
            }
        }
    }

    public GpoIndexProgress? Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    public event EventHandler? StateChanged;

    /// <summary>
    /// <see cref="GpoIndexRefresh.IfMissing"/>: the current view when it was built for the current session and the window's
    /// culture; for another culture a rebuild of the names and definitions only (no GPO folder is read); otherwise a build –
    /// or the running build. <see cref="GpoIndexRefresh.Incremental"/> / <see cref="GpoIndexRefresh.Full"/>: a build; during a
    /// running build exactly one follow-up is queued (the stronger request wins).
    /// </summary>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    /// <exception cref="ObjectDisposedException">The window closed.</exception>
    public async Task<GpoIndexView> GetAsync(GpoIndexRefresh refresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = _services.Sessions.Current ?? throw new InvalidOperationException("Not connected.");
        var culture = _services.Localizer.Culture;
        Build build;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var fits = _view is not null && Fits(session);
            if (refresh == GpoIndexRefresh.IfMissing && fits && Equals(_view!.Culture, culture))
            {
                return _view;
            }

            if (_running is { IsCancelled: false } running && ReferenceEquals(running.Session, session) && refresh == GpoIndexRefresh.IfMissing)
            {
                build = running;
            }
            else if (_running is not null)
            {
                // A follow-up of the running build (or of a cancelled one still winding down, or of another session's):
                // exactly one, the stronger request. IfMissing becomes Incremental – it reuses the view only when it fits.
                var kind = refresh == GpoIndexRefresh.IfMissing ? GpoIndexRefresh.Incremental : refresh;
                _queued ??= new Build(kind, session, _services.Lifetime);
                if (kind == GpoIndexRefresh.Full)
                {
                    _queued.Kind = GpoIndexRefresh.Full;
                }

                _queued.Session = session;
                build = _queued;
            }
            else
            {
                var kind = refresh switch
                {
                    GpoIndexRefresh.IfMissing when fits => BuildKind.Culture,
                    GpoIndexRefresh.Incremental when fits => BuildKind.Incremental,
                    _ => BuildKind.Full,
                };
                build = new Build(kind == BuildKind.Full ? GpoIndexRefresh.Full : GpoIndexRefresh.Incremental, session, _services.Lifetime) { Mode = kind };
                StartLocked(build);
            }

            build.Waiters++;
        }

        RaiseStateChanged();
        var left = false;
        try
        {
            return await build.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !build.Completion.Task.IsCompleted)
        {
            left = true;
            Leave(build, cancelled: true);
            throw;
        }
        finally
        {
            if (!left)
            {
                Leave(build, cancelled: false);
            }
        }
    }

    /// <summary>Cancels the running build (and a queued follow-up) for every waiter.</summary>
    public void CancelBuild()
    {
        Build? running, queued;
        lock (_gate)
        {
            (running, queued) = (_running, _queued);
            _queued = null;
        }

        queued?.Cancel();
        running?.Cancel();
        RaiseStateChanged();
    }

    /// <summary>Drops the view and cancels a running build (another DC, reconnect, disconnect); a build started before never becomes current.</summary>
    public void Invalidate()
    {
        Build? running, queued;
        lock (_gate)
        {
            _generation++;
            _view = null;
            _viewSession = null;
            _viewServer = null;
            _stale = false;
            (running, queued) = (_running, _queued);
            _queued = null;
        }

        queued?.Cancel();
        running?.Cancel();
        RaiseStateChanged();
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

        _services.Changed -= OnGroupPolicyChanged;
        _services.Sessions.SessionChanged -= OnSessionChanged;
        Invalidate();
        StateChanged = null;
    }

    /// <summary>For diagnostics: the state only.</summary>
    public override string ToString() => $"view: {Current is not null}, building: {IsBuilding}, stale: {IsStale}";

    // ---- Builds ------------------------------------------------------------------------------------------------------

    private bool Fits(IDirectorySession session) =>
        ReferenceEquals(_viewSession, session) && string.Equals(_viewServer, session.Info.Server, StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts <paramref name="build"/> as the running build (under the gate).</summary>
    private void StartLocked(Build build)
    {
        build.Generation = ++_generation;
        build.Previous = _view is not null && Fits(build.Session) ? _view : null;
        _running = build;
        _progress = new GpoIndexProgress(GpoIndexStage.Directory);
        _ = RunAsync(build);
    }

    private async Task RunAsync(Build build)
    {
        GpoIndexView? view = null;
        Exception? failure = null;
        try
        {
            view = await Task.Run(() => BuildViewAsync(build), build.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        var current = false;
        lock (_gate)
        {
            if (ReferenceEquals(_running, build))
            {
                _running = null;
                _progress = null;
            }

            if (view is not null && build.Generation == _generation && !build.Token.IsCancellationRequested && !_disposed
                && ReferenceEquals(_services.Sessions.Current, build.Session))
            {
                _view = view;
                _viewSession = build.Session;
                _viewServer = build.Session.Info.Server;
                _stale = false;
                current = true;
            }

            if (_queued is { } queued && !_disposed && _running is null)
            {
                _queued = null;
                if (queued.Waiters > 0 && ReferenceEquals(_services.Sessions.Current, queued.Session))
                {
                    queued.Mode = queued.Kind == GpoIndexRefresh.Full ? BuildKind.Full : BuildKind.Incremental;
                    StartLocked(queued);
                }
                else
                {
                    queued.Cancel();
                }
            }
        }

        if (current)
        {
            build.Completion.TrySetResult(view!);
        }
        else if (failure is not null && failure is not OperationCanceledException)
        {
            build.Completion.TrySetException(failure);
        }
        else
        {
            build.Completion.TrySetCanceled();
        }

        build.Dispose();
        RaiseStateChanged();
    }

    /// <summary>
    /// One build: the build's own SYSVOL connection, the window's definitions (cached per window), the Core index (or, for a
    /// culture change, the previous index with the new definitions – no GPO folder read), the SID names, then the texts.
    /// </summary>
    private async Task<GpoIndexView> BuildViewAsync(Build build)
    {
        var token = build.Token;
        var session = build.Session;
        var localizer = _services.Localizer;
        var culture = localizer.Culture;
        var access = await _services.OpenSysvolAsync(session, token).ConfigureAwait(false);
        await using var sysvol = access.Connection;

        PolicyDefinitionSet? definitions = null;
        var definitionsFailed = false;
        if (_services.Definitions is { } service)
        {
            try
            {
                definitions = (await service.GetAsync(session, sysvol, culture, reload: false, token).ConfigureAwait(false)).Set;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                definitionsFailed = true;
            }
        }

        GpoSettingsIndex index;
        var previous = build.Previous;
        if (build.Mode == BuildKind.Culture && previous is not null)
        {
            index = ReferenceEquals(previous.Index.Definitions, definitions) ? previous.Index : previous.Index.WithDefinitions(definitions);
        }
        else
        {
            var request = new GpoSettingsIndexRequest
            {
                Definitions = definitions,
                Previous = build.Mode == BuildKind.Incremental ? previous?.Index : null,
                Generation = build.Generation,
            };
            index = await GpoSettingsIndexBuilder.BuildAsync(session, sysvol, request, new BuildProgress(this, build), token).ConfigureAwait(false);
        }

        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in index.Entries)
        {
            sids.UnionWith(GpoSettingPresenter.Sids(entry.Settings));
            foreach (var unit in entry.Units)
            {
                sids.UnionWith(unit.PrincipalSids);
            }
        }

        var names = await TrusteeNames.ResolveAsync(session, sids, localizer, token).ConfigureAwait(false);
        var view = new GpoIndexView(index, names, culture) { SysvolReason = access.Reason, DefinitionsFailed = definitionsFailed };
        GpoIndexTexts.For(localizer, view).Warm();
        return view;
    }

    private void Report(Build build, GpoIndexProgress value)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_running, build))
            {
                return;
            }

            _progress = value;
        }

        RaiseStateChanged();
    }

    /// <summary>A waiter is done: the last one that cancelled cancels the build (or drops the queued follow-up).</summary>
    private void Leave(Build build, bool cancelled)
    {
        var cancel = false;
        lock (_gate)
        {
            build.Waiters--;
            if (cancelled && build.Waiters == 0 && !build.Completion.Task.IsCompleted)
            {
                if (ReferenceEquals(_queued, build))
                {
                    _queued = null;
                }

                cancel = true;
            }
        }

        if (cancel)
        {
            build.Cancel();
            RaiseStateChanged();
        }
    }

    private void OnGroupPolicyChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_view is null || _stale)
            {
                return;
            }

            _stale = true;
        }

        RaiseStateChanged();
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Invalidate();

    /// <summary>
    /// Raises <see cref="StateChanged"/> on the UI dispatcher, coalesced: while one notification waits for the dispatcher no
    /// other is posted (a build reports per folder and per GPO; the handlers read the current state anyway).
    /// </summary>
    private void RaiseStateChanged()
    {
        if (StateChanged is null || Interlocked.Exchange(ref _statePosted, 1) == 1)
        {
            return;
        }

        _ = _services.Dispatcher.RunAsync(() =>
        {
            Volatile.Write(ref _statePosted, 0);
            StateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private enum BuildKind
    {
        Full,
        Incremental,
        Culture,
    }

    /// <summary>One build (running or queued) with its waiters and its own cancellation.</summary>
    private sealed class Build(GpoIndexRefresh kind, IDirectorySession session, CancellationToken lifetime) : IDisposable
    {
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        private int _disposed;
        private bool _cancelled;

        public GpoIndexRefresh Kind { get; set; } = kind;

        public BuildKind Mode { get; set; } = kind == GpoIndexRefresh.Full ? BuildKind.Full : BuildKind.Incremental;

        public IDirectorySession Session { get; set; } = session;

        public GpoIndexView? Previous { get; set; }

        public long Generation { get; set; }

        public int Waiters { get; set; }

        public TaskCompletionSource<GpoIndexView> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token => _cts.Token;

        /// <summary><see cref="Cancel"/> ran, or the window's lifetime ended: joining this build would only end in a cancellation.</summary>
        public bool IsCancelled => Volatile.Read(ref _cancelled) || lifetime.IsCancellationRequested;

        /// <summary>
        /// Stops the build; a queued (never started) build completes as cancelled at once and releases its link to the
        /// window's lifetime (it never reaches <c>RunAsync</c>, which disposes started builds).
        /// </summary>
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

            if (Generation == 0)
            {
                Completion.TrySetCanceled();
                Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cts.Dispose();
            }
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind} {Mode} #{Generation}, {Waiters} waiting");
    }

    /// <summary>The Core build's progress as the service's <see cref="Progress"/>.</summary>
    private sealed class BuildProgress(GpoSettingsIndexService owner, Build build) : IProgress<GpoIndexProgress>
    {
        public void Report(GpoIndexProgress value) => owner.Report(build, value);
    }
}
