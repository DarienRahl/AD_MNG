using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>Result of <see cref="LockoutSourceResolver"/>: the lockout with caller and bad-password sources, and the
/// 4740 evidence.</summary>
/// <param name="Lockout">The lockout with caller and sources.</param>
/// <param name="Evidence">The 4740 record, when found.</param>
/// <param name="Complete">False when the 4740 was not (yet) found or a source query did not finish (timeout, DC
/// unreachable) – the lockout is retried while retries remain (11.4 #6b).</param>
public sealed record LockoutResolution(LockoutInfo Lockout, IReadOnlyList<EvidenceRef> Evidence, bool Complete = true);
