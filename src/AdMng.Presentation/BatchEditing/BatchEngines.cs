using AdMng.Core.Batch;
using AdMng.Core.Csv;
using AdMng.Core.Templates;

namespace AdMng.Presentation.BatchEditing;

/// <summary>
/// The Core engines used by the batch editor, CSV import and password reset (parameter object, singleton).
/// The default AdMng.Core implementations are registered by <see cref="ServiceCollectionExtensions.AddAdMngPresentation"/>
/// unless the host registered its own.
/// </summary>
public sealed class BatchEngines(
    IChangePlanner planner,
    ICsvImportPlanner csvPlanner,
    IRollbackPlanner rollback,
    IPowerShellExporter powerShell,
    ICsvCodec csv,
    ITemplateEngine templates,
    BatchReportExporter reports)
{
    public IChangePlanner Planner { get; } = planner ?? throw new ArgumentNullException(nameof(planner));

    public ICsvImportPlanner CsvPlanner { get; } = csvPlanner ?? throw new ArgumentNullException(nameof(csvPlanner));

    public IRollbackPlanner Rollback { get; } = rollback ?? throw new ArgumentNullException(nameof(rollback));

    public IPowerShellExporter PowerShell { get; } = powerShell ?? throw new ArgumentNullException(nameof(powerShell));

    public ICsvCodec Csv { get; } = csv ?? throw new ArgumentNullException(nameof(csv));

    public ITemplateEngine Templates { get; } = templates ?? throw new ArgumentNullException(nameof(templates));

    public BatchReportExporter Reports { get; } = reports ?? throw new ArgumentNullException(nameof(reports));
}
