using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>One enabled account without a logon for the configured number of days (10.17): name, last logon date
/// (null = never) and DN (the OU is shown).</summary>
public sealed record InactiveAccount(ObjectRef Account, DateTimeOffset? LastLogon, bool Privileged);
