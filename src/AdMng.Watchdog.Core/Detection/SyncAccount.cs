namespace AdMng.Watchdog.Core.Detection;

/// <summary>A detected Entra Connect / directory synchronization account.</summary>
public sealed record SyncAccount(string Sid, string? SamAccountName, string Dn);
