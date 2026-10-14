namespace AdMng.Watchdog.Core.Formatting.Webhooks;

/// <summary>The content of a <c>watchdog.heartbeat</c> payload (section 13.6) – service state only, no directory values.</summary>
/// <param name="At">When the heartbeat was created.</param>
/// <param name="ServiceVersion">Version of the service.</param>
/// <param name="DirectoryState">Health state of the directory connection ("Ok", "Error", …).</param>
/// <param name="LastPollAt">Last detection poll.</param>
/// <param name="PendingOutbox">Messages waiting in the outbox.</param>
public sealed record HeartbeatPayload(DateTimeOffset At, string ServiceVersion, string DirectoryState, DateTimeOffset? LastPollAt, int PendingOutbox);
