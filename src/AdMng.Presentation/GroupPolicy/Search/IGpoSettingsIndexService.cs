using System.Globalization;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Search;

namespace AdMng.Presentation.GroupPolicy.Search;

// K7 contract (docs/dev/k-computers-and-gpo.md 21.3, 21.11) – implementation: W1.

/// <summary>What <see cref="IGpoSettingsIndexService.GetAsync"/> does with the current view.</summary>
public enum GpoIndexRefresh
{
    /// <summary>The current view when it fits (same session, same culture), otherwise a build.</summary>
    IfMissing,

    /// <summary>Re-read only the GPOs whose versions changed (F5, after a copy/remove/merge).</summary>
    Incremental,

    /// <summary>Read every GPO again ("Odczytaj wszystko ponownie").</summary>
    Full,
}

/// <summary>
/// The index of one connection window as the documents show it: the Core index, the SID names and the culture its texts
/// were made for (in memory only, CLAUDE.md rule 1).
/// </summary>
public sealed record GpoIndexView(GpoSettingsIndex Index, IReadOnlyDictionary<string, string> SidNames, CultureInfo Culture)
{
    /// <summary>Why SYSVOL could not be opened (null: read).</summary>
    public SysvolUnavailableReason? SysvolReason { get; init; }

    /// <summary>The ADMX templates could not be loaded (registry names only).</summary>
    public bool DefinitionsFailed { get; init; }

    /// <summary>For diagnostics: counts only.</summary>
    public override string ToString() => Index + " " + Culture.Name;
}

/// <summary>
/// The per-window settings index (scoped). The service owns every build: its own SYSVOL connection and its own
/// cancellation (linked to the window's lifetime); a caller's token only ends that caller's wait. A build is cancelled by
/// <see cref="Invalidate"/>, <see cref="CancelBuild"/>, disposal, or when its last waiting caller cancels. An
/// <see cref="GpoIndexRefresh.Incremental"/> or <see cref="GpoIndexRefresh.Full"/> request during a build queues exactly
/// one follow-up build.
/// </summary>
public interface IGpoSettingsIndexService
{
    GpoIndexView? Current { get; }

    /// <summary>A GPO operation ran in this window since <see cref="Current"/> was built (no automatic rebuild).</summary>
    bool IsStale { get; }

    bool IsBuilding { get; }

    /// <summary>The running build's progress (null when idle); changes raise <see cref="StateChanged"/>.</summary>
    GpoIndexProgress? Progress { get; }

    /// <summary>Built, stale, progress, invalidated (raised on the UI dispatcher).</summary>
    event EventHandler? StateChanged;

    Task<GpoIndexView> GetAsync(GpoIndexRefresh refresh, CancellationToken cancellationToken);

    /// <summary>Cancels the running build for every waiter ("Anuluj" of the last document that waits).</summary>
    void CancelBuild();

    /// <summary>Drops the view and cancels a running build (another DC, reconnect, disconnect).</summary>
    void Invalidate();
}
