namespace AdMng.Presentation.Readiness;

/// <summary>
/// The last results of the readiness check of one connection window as value-free code/status pairs (design note
/// docs/dev/s-pilot-readiness.md 2 and 3): the problem report ("Pomoc → Zgłoś problem…") lists them. Registered per
/// connection window (scoped); in memory only, never written anywhere.
/// </summary>
public interface IReadinessResults
{
    /// <summary>
    /// The checks of the last run (completed or cancelled) of this connection window: one pair per code
    /// (<see cref="ReadinessCodes"/>) in check order, the status of a per-host check being the worst of its rows (Error,
    /// Warning, not checked, OK). Empty before the first run. Codes and statuses only – no host, account or other value.
    /// </summary>
    IReadOnlyList<(string Code, ReadinessStatus Status)> Last { get; }

    /// <summary>When the last run ended (null before the first one).</summary>
    DateTimeOffset? LastRunAt { get; }

    /// <summary>The depth of the last run (null before the first one).</summary>
    ReadinessDepth? LastDepth { get; }

    /// <summary>The last run was cancelled (its unreached checks are not checked).</summary>
    bool LastCancelled { get; }

    /// <summary>A run ended and <see cref="Last"/> changed (raised on the thread that published it).</summary>
    event EventHandler? Changed;
}

/// <summary>The store behind <see cref="IReadinessResults"/>: the readiness document of the window publishes each finished run.</summary>
public sealed class ReadinessResults : IReadinessResults
{
    private readonly Lock _gate = new();
    private IReadOnlyList<(string Code, ReadinessStatus Status)> _last = [];
    private DateTimeOffset? _at;
    private ReadinessDepth? _depth;
    private bool _cancelled;

    public event EventHandler? Changed;

    public IReadOnlyList<(string Code, ReadinessStatus Status)> Last
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    public DateTimeOffset? LastRunAt
    {
        get
        {
            lock (_gate)
            {
                return _at;
            }
        }
    }

    public ReadinessDepth? LastDepth
    {
        get
        {
            lock (_gate)
            {
                return _depth;
            }
        }
    }

    public bool LastCancelled
    {
        get
        {
            lock (_gate)
            {
                return _cancelled;
            }
        }
    }

    /// <summary>The pairs of a report: one per code in check order, the worst status of its rows.</summary>
    public static IReadOnlyList<(string Code, ReadinessStatus Status)> Summarize(IEnumerable<ReadinessResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var worst = new Dictionary<string, ReadinessStatus>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var result in results)
        {
            if (worst.TryGetValue(result.Code, out var known))
            {
                worst[result.Code] = Rank(result.Status) > Rank(known) ? result.Status : known;
            }
            else
            {
                worst[result.Code] = result.Status;
                order.Add(result.Code);
            }
        }

        return [.. order.Select(code => (code, worst[code]))];
    }

    /// <summary>Error before Warning before not checked before OK.</summary>
    public static int Rank(ReadinessStatus status) => status switch
    {
        ReadinessStatus.Error => 3,
        ReadinessStatus.Warning => 2,
        ReadinessStatus.NotChecked => 1,
        _ => 0,
    };

    /// <summary>Keeps the codes and statuses of <paramref name="report"/> (nothing else) and raises <see cref="Changed"/>.</summary>
    public void Publish(ReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var last = Summarize(report.Results);
        lock (_gate)
        {
            _last = last;
            _at = report.At;
            _depth = report.Depth;
            _cancelled = report.Cancelled;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Counts only.</summary>
    public override string ToString() => $"Readiness results: {Last.Count}";
}
