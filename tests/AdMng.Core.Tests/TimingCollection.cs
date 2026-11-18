namespace AdMng.Core.Tests;

/// <summary>
/// Tests with a wall-clock budget (the 5 000-row bulk creation of docs/dev/phase3-bulk-create.md 2.11) run alone, after
/// the parallel collections: in a full run the other test classes of this assembly share the same cores and made the
/// name-based computation four times slower (2.5 s alone, 9-10 s among them). Complexity checks that need no budget use
/// <see cref="LinearWork"/> instead.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
