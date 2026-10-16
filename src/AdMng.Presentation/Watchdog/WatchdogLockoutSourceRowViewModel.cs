using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Presentation.Watchdog;

/// <summary>"Źródło błędnych haseł": one source of failed logons before a lockout, with its count.</summary>
public sealed class WatchdogLockoutSourceRowViewModel(LockoutSource source)
{
    public string Address { get; } = source?.IpAddress ?? string.Empty;

    /// <summary>Client-supplied name (never authoritative).</summary>
    public string Workstation { get; } = source?.Workstation ?? string.Empty;

    public int Count { get; } = source?.Count ?? 0;

    /// <summary>4740 / 4771 / 4776 / 4625.</summary>
    public int EventCode { get; } = source?.EventCode ?? 0;
}
