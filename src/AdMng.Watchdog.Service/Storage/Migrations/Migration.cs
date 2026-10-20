namespace AdMng.Watchdog.Service.Storage.Migrations;

/// <summary>One schema migration: applied in one transaction followed by <c>PRAGMA user_version = Version</c>.</summary>
/// <param name="Version">The <c>user_version</c> after the migration (1, 2, …).</param>
/// <param name="Name">"NNNN_name".</param>
/// <param name="Sql">The script (several statements).</param>
internal sealed record Migration(int Version, string Name, string Sql)
{
    /// <summary>Every migration of this build, ascending.</summary>
    public static IReadOnlyList<Migration> All { get; } =
    [
        new(Migration0001Initial.Version, Migration0001Initial.Name, Migration0001Initial.Sql),
        new(Migration0002OwnSeverity.Version, Migration0002OwnSeverity.Name, Migration0002OwnSeverity.Sql),
        new(Migration0003OutboxEventState.Version, Migration0003OutboxEventState.Name, Migration0003OutboxEventState.Sql),
        new(Migration0004SecurityEventsV2.Version, Migration0004SecurityEventsV2.Name, Migration0004SecurityEventsV2.Sql),
        new(Migration0005GpoContent.Version, Migration0005GpoContent.Name, Migration0005GpoContent.Sql),
    ];

    /// <summary>The newest schema version this build knows.</summary>
    public static int LatestVersion => All[^1].Version;

    public override string ToString() => $"Migration({Name})";
}
