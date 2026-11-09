using AdMng.Core.Directory;

namespace AdMng.Core.Batch.Creation;

/// <summary>The domains of the forest (2.4): their count decides the uniqueness scope, their DNS names the suffix checks of 2.2.</summary>
/// <param name="DomainCount">The number of domain crossRefs (<c>systemFlags</c> bit 2).</param>
/// <param name="DomainDnsNames">Their <c>dnsRoot</c> values.</param>
public sealed record ForestInfo(int DomainCount, IReadOnlyList<string> DomainDnsNames)
{
    public override string ToString() => $"Forest: {DomainCount} domains";
}

/// <summary>Reads the forest's domains (docs/dev/phase3-bulk-create.md 2.4, owner B2).</summary>
public static class ForestScope
{
    /// <summary>
    /// One one-level search of <c>CN=Partitions,&lt;configuration&gt;</c> for crossRefs with <c>systemFlags</c> bit 2,
    /// returning <c>dnsRoot</c> and <c>nCName</c>.
    /// </summary>
    public static Task<ForestInfo> ReadAsync(IDirectoryReader reader, CancellationToken cancellationToken) =>
        throw new NotImplementedException("F3 B2: ForestScope.ReadAsync (2.4).");
}
