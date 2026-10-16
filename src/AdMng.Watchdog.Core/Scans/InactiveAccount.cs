using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Scans;

/// <summary>One row of the inactive accounts digest section (section 10.17).</summary>
/// <param name="Object">The account.</param>
/// <param name="LastLogon">lastLogonTimestamp (replicated, up to 14 days behind); null = never logged on.</param>
/// <param name="Container">The parent container DN (the OU shown in the digest).</param>
public sealed record InactiveAccount(ObjectRef Object, DateTimeOffset? LastLogon, string? Container);
