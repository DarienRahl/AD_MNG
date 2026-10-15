using AdMng.Core.Directory;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>
/// State of one detection cycle (section 10.1): session, resolved settings, mode, watermarks, the container rewrites,
/// what was seen/deleted/restored and the chunk being built. Chunks are committed to the store with
/// <c>NewState = null</c>; the final commit moves W.
/// </summary>
internal sealed class DetectionCycle
{
    private int _committedRewrites;

    public DetectionCycle(IDirectorySession session, DetectionSettings settings, DcSnapshot dc, DcState? state, ModeDecision decision, DateTimeOffset now)
    {
        Session = session;
        Settings = settings;
        Scope = new DetectionScope(settings);
        Dc = dc;
        State = state;
        Decision = decision;
        Now = now;
        var overlap = Math.Max(0, settings.Directory.UsnOverlap);
        SearchFrom = Math.Max(0, decision.Watermark + 1 - overlap);
    }

    public IDirectorySession Session { get; }

    public DetectionSettings Settings { get; }

    public DetectionScope Scope { get; }

    public DcSnapshot Dc { get; }

    public DcState? State { get; }

    public ModeDecision Decision { get; }

    public DetectionMode Mode => Decision.Mode;

    public DateTimeOffset Now { get; }

    /// <summary>W' = max(0, W + 1 − UsnOverlap): first USN of the incremental searches.</summary>
    public long SearchFrom { get; }

    /// <summary>The W replication metadata is compared with; null when W belongs to another DC (Resync, Baseline).</summary>
    public long? MetadataWatermark => Mode is DetectionMode.Resync or DetectionMode.Baseline ? null : Decision.Watermark;

    /// <summary>Baseline emits no change events (section 10.4).</summary>
    public bool EmitEvents => Mode != DetectionMode.Baseline;

    /// <summary>Resync / Failback: a lower metadata version than stored means the DC has not replicated the change yet.</summary>
    public bool VersionAware => Mode is DetectionMode.Resync or DetectionMode.Failback;

    public bool IsFullScan => Mode is DetectionMode.Baseline or DetectionMode.Resync or DetectionMode.Reconciliation;

    public DetectionSource Source => Mode is DetectionMode.Resync or DetectionMode.Reconciliation ? DetectionSource.Reconciliation : DetectionSource.IncrementalPoll;

    public DnRewriter Rewriter { get; } = new();

    /// <summary>Stored privilege state at the start of the cycle (tag privileged, PrivilegeEvaluator diff).</summary>
    public PrivilegeState? StoredPrivileges { get; set; }

    /// <summary>objectGUIDs of tombstones seen in this cycle (their removal from groups is not an event).</summary>
    public HashSet<Guid> DeletedGuids { get; } = [];

    /// <summary>Member keys of principals deleted in this cycle (no PrivilegeRevoked for them).</summary>
    public HashSet<string> DeletedPrincipalKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Member key of a restored object → change set of its ObjectRestored event (PrivilegeGranted tag restored).</summary>
    public Dictionary<string, Guid> RestoredChangeSets { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>GUIDs returned by the full scan (reconciliation of the rest).</summary>
    public HashSet<Guid> Seen { get; } = [];

    /// <summary>
    /// GUIDs already handled with their container in this cycle – descendants removed with a container that left the
    /// scope, or adopted with one that entered it (10.7) – which a later pass skips.
    /// </summary>
    public HashSet<Guid> HandledWithContainer { get; } = [];

    public List<string> Warnings { get; } = [];

    /// <summary>
    /// Drafts without a snapshot row (SchemaChanged) emitted only into the final commit, together with W – a cycle that
    /// fails before it is repeated over the same USN range without a duplicate.
    /// </summary>
    public List<(EventDraft Draft, ObjectRef Object)> FinalDrafts { get; } = [];

    public bool MembershipRelevant { get; set; }

    /// <summary>Anything membership- or privilege-relevant happened (PrivilegeEvaluator trigger).</summary>
    public bool PrivilegeRelevant { get; set; }

    public int ObjectsExamined { get; set; }

    public List<WatchEvent> StoredEvents { get; } = [];

    public CommitChunk Chunk { get; private set; } = new();

    private HashSet<string> PrivilegedKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the member key is in the stored PrivilegeState.</summary>
    public bool IsPrivileged(string key) => PrivilegedKeys.Contains(key);

    /// <summary>Sets the stored privilege state of the cycle.</summary>
    public void UsePrivileges(PrivilegeState? state)
    {
        StoredPrivileges = state;
        PrivilegedKeys = state?.Principals.Select(p => p.PrincipalKey).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Commits the current chunk (when not empty) and starts a new one.</summary>
    public async Task CommitChunkAsync(IDetectionStore store, CancellationToken cancellationToken)
    {
        var recorded = Rewriter.Count;
        if (Chunk.IsEmpty && recorded == _committedRewrites)
        {
            return;
        }

        var commit = Chunk.ToCommit(Rewriter.StoreRewrites(_committedRewrites));
        MembershipRelevant |= commit.MembershipRelevant;
        var stored = await store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        StoredEvents.AddRange(stored);
        _committedRewrites = recorded;
        Chunk = new CommitChunk();
    }

    /// <summary>The store rewrites of the container rewrites not committed yet (for the final commit).</summary>
    public IReadOnlyList<DnRewrite> PendingRewrites => Rewriter.StoreRewrites(_committedRewrites);

    /// <summary>Marks every rewrite as committed (after the final commit).</summary>
    public void MarkRewritesCommitted() => _committedRewrites = Rewriter.Count;
}
