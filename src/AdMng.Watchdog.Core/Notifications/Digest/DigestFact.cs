namespace AdMng.Watchdog.Core.Notifications.Digest;

/// <summary>A counter of a section (passwords, inactive accounts, hygiene); the label is the resx key
/// <c>Digest.Fact.{Key}</c>. A null value means "unknown" (e.g. the account was not found).</summary>
public sealed record DigestFact(string Key, long? Value);
