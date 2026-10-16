using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Abstractions;

/// <summary>The shipped default configuration incl. the built-in rules (sections 8.1 and 12.4).</summary>
public interface IDefaultConfigurationFactory
{
    WatchdogConfiguration Create();
}
