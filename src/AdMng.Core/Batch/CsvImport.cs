using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Model;

namespace AdMng.Core.Batch;

/// <summary>What to do with empty CSV cells.</summary>
public enum EmptyCellBehavior
{
    /// <summary>Leave the attribute unchanged (default, safest).</summary>
    Ignore,

    /// <summary>Clear the attribute.</summary>
    Clear,
}

/// <summary>Mapping of a CSV file onto existing directory objects (bulk modify from a spreadsheet).</summary>
public sealed record CsvImportMapping
{
    /// <summary>CSV column identifying the object.</summary>
    public required string KeyColumn { get; init; }

    /// <summary>Attribute the key is matched against: sAMAccountName, userPrincipalName, mail, employeeID,
    /// distinguishedName or objectGUID.</summary>
    public required string KeyAttribute { get; init; }

    /// <summary>CSV column -> LDAP attribute. Unmapped columns are ignored.</summary>
    public required IReadOnlyDictionary<string, string> Columns { get; init; }

    public EmptyCellBehavior EmptyCells { get; init; } = EmptyCellBehavior.Ignore;

    /// <summary>Where to look the keys up (default: domain root).</summary>
    public string? SearchBaseDn { get; init; }

    /// <summary>Restrict lookups to one object kind (recommended: avoids matching a contact instead of a user).</summary>
    public ObjectKind? Kind { get; init; }

    /// <summary>Multi-valued cells are split on this separator (e.g. proxyAddresses "SMTP:a@x|smtp:b@x").</summary>
    public string MultiValueSeparator { get; init; } = "|";
}

/// <summary>Plans a bulk modification from a CSV table.</summary>
public interface ICsvImportPlanner
{
    /// <summary>
    /// Resolves every row to exactly one object (batched OR-filter lookups), reads the current values of the
    /// mapped attributes and builds a <see cref="ChangePlan"/> (rows not found or ambiguous become objects with
    /// <see cref="IssueCodes.NotFound"/> / <see cref="IssueCodes.Ambiguous"/> errors; <see cref="ObjectPlan.SourceRow"/> is set).
    /// </summary>
    Task<ChangePlan> PlanAsync(
        IDirectorySession session,
        CsvTable table,
        CsvImportMapping mapping,
        PlanningOptions? options,
        IProgress<int>? progress,
        CancellationToken cancellationToken);
}
