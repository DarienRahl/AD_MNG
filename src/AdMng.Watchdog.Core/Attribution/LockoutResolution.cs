using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>Result of <see cref="LockoutSourceResolver"/>: the lockout with caller and bad-password sources, and the
/// 4740 evidence.</summary>
public sealed record LockoutResolution(LockoutInfo Lockout, IReadOnlyList<EvidenceRef> Evidence);
