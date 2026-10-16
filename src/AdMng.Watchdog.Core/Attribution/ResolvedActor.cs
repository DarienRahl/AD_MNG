using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Attribution;

/// <summary>The actor of a resolved attribution (<see cref="ActorResolver"/>).</summary>
/// <param name="Actor">Subject SID, account and – when in the snapshot – display name and DN.</param>
/// <param name="Reason"><see cref="AttributionReasons.System"/> / <see cref="AttributionReasons.SelfChange"/> or null.</param>
/// <param name="AddedTags"><c>selfChange</c>, <c>passwordReset</c>, <c>syncAccount</c>.</param>
public sealed record ResolvedActor(PrincipalRef Actor, string? Reason, IReadOnlyList<string> AddedTags);
