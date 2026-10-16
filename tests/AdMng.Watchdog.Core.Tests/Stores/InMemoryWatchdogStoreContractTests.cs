using AdMng.Watchdog.Testing.Stores;

namespace AdMng.Watchdog.Core.Tests.Stores;

/// <summary>The store contract suite against <see cref="InMemoryWatchdogStore"/>.</summary>
public sealed class InMemoryWatchdogStoreContractTests : WatchdogStoreContractTests<InMemoryWatchdogStore>
{
    protected override Task<InMemoryWatchdogStore> CreateStoreAsync() => Task.FromResult(new InMemoryWatchdogStore(Clock));
}
