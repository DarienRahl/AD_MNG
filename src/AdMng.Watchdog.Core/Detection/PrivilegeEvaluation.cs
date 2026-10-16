using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>Result of <see cref="PrivilegeEvaluator.EvaluateAsync"/>: the new state and the change drafts.</summary>
internal sealed record PrivilegeEvaluation(PrivilegeState State, IReadOnlyList<EventDraft> Drafts);
