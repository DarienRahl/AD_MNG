namespace AdMng.Directory.IntegrationTests.Infrastructure;

/// <summary>
/// Tests that measure latency run alone (after the parallel collections), so that the bulk writes of other
/// tests do not distort the numbers - the Samba lab serializes writes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
