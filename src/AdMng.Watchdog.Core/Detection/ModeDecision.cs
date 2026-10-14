using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>Result of <see cref="DetectionModeSelector.Select"/>.</summary>
/// <param name="Mode">The cycle mode.</param>
/// <param name="Skip">True when the cycle is skipped (DC not synchronized); W stays unchanged.</param>
/// <param name="Reason">Reason code: <c>dcChanged</c>, <c>invocationIdChanged</c>, <c>usnRollback</c>, <c>notSynchronized</c>,
/// <c>baseline</c>, <c>failback</c>, <c>reconciliation</c>; null for NoChange/Incremental.</param>
/// <param name="Watermark">The W the incremental search starts from (stored W, or the known DC's own W on failback).</param>
public sealed record ModeDecision(DetectionMode Mode, bool Skip, string? Reason, long Watermark);
