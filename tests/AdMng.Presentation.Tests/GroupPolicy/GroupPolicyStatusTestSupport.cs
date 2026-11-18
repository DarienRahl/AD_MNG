using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.GroupPolicy.Search;
using AdMng.Testing;

namespace AdMng.Presentation.Tests.GroupPolicy;

/// <summary>One thing a status line said: its kind, its text and - while an operation runs - how far it is.</summary>
internal readonly record struct Said(PageStatusKind Kind, string Message, double? Fraction = null);

/// <summary>
/// Every change of <see cref="PageStatusHolder.Current"/> from the moment it is created (what the line shows, in order):
/// the sequence of a view model's status is asserted as a whole, so a state that lasts only a moment - "Stopping…" of a
/// stop that is answered at once - is observed and not raced for.
/// </summary>
internal sealed class StatusRecorder
{
    private readonly List<Said> _seen = [];

    public StatusRecorder(PageStatusHolder status)
    {
        ArgumentNullException.ThrowIfNull(status);
        status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageStatusHolder.Current))
            {
                lock (_seen)
                {
                    _seen.Add(new Said(status.Current.Kind, status.Current.Message, status.Current.Progress?.Fraction));
                }
            }
        };
    }

    /// <summary>What was said so far.</summary>
    public IReadOnlyList<Said> Seen
    {
        get
        {
            lock (_seen)
            {
                return [.. _seen];
            }
        }
    }

    /// <summary>The kinds and texts only, a status that was said again with the same text counted once.</summary>
    public IReadOnlyList<(PageStatusKind Kind, string Message)> Plain
    {
        get
        {
            var plain = new List<(PageStatusKind Kind, string Message)>();
            foreach (var said in Seen)
            {
                if (plain.Count == 0 || plain[^1] != (said.Kind, said.Message))
                {
                    plain.Add((said.Kind, said.Message));
                }
            }

            return plain;
        }
    }
}

/// <summary>
/// A slow domain controller: the first call of the fake session waits for the test (a call on a pool thread - the reads
/// of the documents are - or on the caller's: use it where the first call is not made by the thread of the test).
/// Disposing releases it, so a failing test does not keep a thread for good.
/// </summary>
internal sealed class HeldDirectoryCall : IDisposable
{
    /// <summary>How long a held call waits for the test at most.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private readonly FakeDirectorySession _session;
    private readonly ManualResetEventSlim _release = new();
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed = 1;

    public HeldDirectoryCall(FakeDirectorySession session, Func<FakeOperation, bool>? matches = null)
    {
        _session = session;
        session.BeforeOperation = operation =>
        {
            if ((matches is null || matches(operation)) && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _reached.TrySetResult();
                _release.Wait(Limit);
            }
        };
    }

    /// <summary>Completes when the call is being held.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Lets the held call go on; later calls are not held.</summary>
    public void Release()
    {
        Volatile.Write(ref _armed, 0);
        _release.Set();
    }

    public void Dispose()
    {
        Release();
        _session.BeforeOperation = null;
    }
}

/// <summary>
/// The settings index of a window with a read the test holds: the next <see cref="IGpoSettingsIndexService.GetAsync"/>
/// waits (cancellable, like the real service waits for its build) until <see cref="ReadGate.Release"/>, and the progress
/// it shows is the one the test reports - so the stages of a read are deterministic. Everything else is the real service.
/// </summary>
internal sealed class ScriptedIndexService : IGpoSettingsIndexService
{
    private readonly IGpoSettingsIndexService _inner;
    private ReadGate? _gate;
    private GpoIndexProgress? _progress;

    public ScriptedIndexService(IGpoSettingsIndexService inner)
    {
        _inner = inner;
        inner.StateChanged += (_, e) => StateChanged?.Invoke(this, e);
    }

    public GpoIndexView? Current => _inner.Current;

    public bool IsStale => _inner.IsStale;

    public bool IsBuilding => _inner.IsBuilding;

    public GpoIndexProgress? Progress => _progress ?? _inner.Progress;

    public event EventHandler? StateChanged;

    /// <summary>The next read waits for the test.</summary>
    public ReadGate HoldNextRead()
    {
        var gate = new ReadGate(this);
        _gate = gate;
        return gate;
    }

    public async Task<GpoIndexView> GetAsync(GpoIndexRefresh refresh, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _gate, null) is { } gate)
        {
            gate.Enter(refresh);
            try
            {
                await gate.Released.WaitAsync(HeldDirectoryCall.Limit, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _progress = null;
            }
        }

        return await _inner.GetAsync(refresh, cancellationToken).ConfigureAwait(false);
    }

    public void CancelBuild() => _inner.CancelBuild();

    public void Invalidate() => _inner.Invalidate();

    internal void Report(GpoIndexProgress progress)
    {
        _progress = progress;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One held read.</summary>
    public sealed class ReadGate(ScriptedIndexService owner)
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the document asked for the read.</summary>
        public Task Reached => _reached.Task;

        /// <summary>What the document asked for.</summary>
        public GpoIndexRefresh? Requested { get; private set; }

        internal Task Released => _released.Task;

        /// <summary>The build reports its progress (the document shows it).</summary>
        public void Report(GpoIndexProgress progress) => owner.Report(progress);

        /// <summary>Lets the read go on.</summary>
        public void Release() => _released.TrySetResult();

        internal void Enter(GpoIndexRefresh refresh)
        {
            Requested = refresh;
            _reached.TrySetResult();
        }
    }
}
