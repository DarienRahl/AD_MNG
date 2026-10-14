using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;

namespace AdMng.Watchdog.Service.Hosting.Fallbacks;

/// <summary><see cref="IDefaultConfigurationFactory"/> used only when the Core registration provides none: the record
/// defaults without built-in rules.</summary>
public sealed class FallbackDefaultConfigurationFactory : IDefaultConfigurationFactory
{
    public WatchdogConfiguration Create() => new();
}
