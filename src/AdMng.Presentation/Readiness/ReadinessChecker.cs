using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Presentation.Connection;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Readiness;

/// <summary>
/// Runs the readiness check (design note docs/dev/s-pilot-readiness.md 2) on the window's session: the areas in the order
/// of <see cref="ReadinessArea"/>, each check through the existing services of its feature, read-only (nothing is written
/// to AD, SYSVOL, a computer or the disk – rule 1; rule 5 is not involved), on the session's DC (rule 4), cancellable
/// (rule 6). A quick check runs the connection, the directory, this computer and the presence of Outlook; the other rows are
/// "not checked". A failure of one check is that check's error; only cancellation ends a run early – the rows it did not
/// reach are "not checked". Call it off the UI thread; the results are returned in memory only.
/// </summary>
public sealed partial class ReadinessChecker(ReadinessServices services)
{
    private readonly ReadinessServices _services = services ?? throw new ArgumentNullException(nameof(services));

    private ILocalizer Localizer => _services.Localizer;

    /// <summary>Checks <paramref name="request"/> on <paramref name="session"/>; never throws for a check that fails.</summary>
    /// <exception cref="OperationCanceledException">Never: a cancelled run returns with <see cref="ReadinessReport.Cancelled"/>.</exception>
    public async Task<ReadinessReport> RunAsync(IDirectorySession session, ReadinessRequest request, IProgress<ReadinessProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var context = new RunContext(session, request);
        var results = new List<ReadinessResult>();
        var areas = Enum.GetValues<ReadinessArea>();
        var total = areas.Count(a => request.Depth == ReadinessDepth.Full || ReadinessCodes.IsQuick(a));
        var done = 0;
        var cancelled = false;
        foreach (var area in areas)
        {
            if (request.Depth == ReadinessDepth.Quick && !ReadinessCodes.IsQuick(area))
            {
                results.AddRange(ReadinessCodes.Of(area).Select(code => NotChecked(code, Localizer[L.Readiness.NotCheckedQuick])));
                continue;
            }

            if (cancelled || cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                results.AddRange(ReadinessCodes.Of(area).Select(code => NotChecked(code, Localizer[L.Readiness.NotCheckedCancelled])));
                continue;
            }

            progress?.Report(new ReadinessProgress(area, done, total));
            var rows = new List<ReadinessResult>();
            try
            {
                await RunAreaAsync(area, context, rows, cancellationToken).ConfigureAwait(false);
                results.AddRange(rows);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The rows of a cancelled area are dropped: the area is "not checked" as a whole.
                cancelled = true;
                results.AddRange(ReadinessCodes.Of(area).Select(code => NotChecked(code, Localizer[L.Readiness.NotCheckedCancelled])));
            }

            done++;
        }

        return new ReadinessReport(request.Depth, results, _services.Time.GetLocalNow(), cancelled, session.Info.Server);
    }

    private Task RunAreaAsync(ReadinessArea area, RunContext context, List<ReadinessResult> rows, CancellationToken cancellationToken) => area switch
    {
        ReadinessArea.Connection => ConnectionAsync(context, rows, cancellationToken),
        ReadinessArea.Directory => DirectoryAsync(context, rows, cancellationToken),
        ReadinessArea.Sysvol => SysvolAsync(context, rows, cancellationToken),
        ReadinessArea.RemoteActions => RemoteActionsAsync(context, rows, cancellationToken),
        ReadinessArea.EventLogs => EventLogsAsync(context, rows, cancellationToken),
        ReadinessArea.Watchdog => WatchdogAsync(context, rows, cancellationToken),
        ReadinessArea.Correspondence => CorrespondenceAsync(context, rows, cancellationToken),
        _ => ThisComputerAsync(rows),
    };

    // ---- Rows ------------------------------------------------------------------------------------------------------------------

    /// <summary>A row of <paramref name="code"/> with its title and documentation chapter (or <paramref name="documentation"/>).</summary>
    private ReadinessResult Row(string code, ReadinessStatus status, string explanation, string? remedy = null, string? host = null, string? documentation = null) =>
        new(code, status, Localizer[ReadinessText.TitleKey(code)], explanation)
        {
            Host = host,
            Remedy = remedy ?? string.Empty,
            Documentation = Localizer[documentation ?? ReadinessText.DocumentationKey(code)],
        };

    private ReadinessResult Ok(string code, string explanation, string? host = null) => Row(code, ReadinessStatus.Ok, explanation, host: host);

    private ReadinessResult NotChecked(string code, string explanation, string? remedy = null, string? host = null) =>
        Row(code, ReadinessStatus.NotChecked, explanation, remedy, host);

    /// <summary>The error row of a check that failed unexpectedly (the translated failure, never a stack or a raw value).</summary>
    private ReadinessResult Failed(string code, Exception exception, string? host = null) =>
        Row(code, ReadinessStatus.Error, Localizer.Format(L.Readiness.CheckFailed, _services.Ui.Errors.Translate(exception)), host: host);

    /// <summary>Adds the row of one check; a failure of the check becomes its error row (cancellation propagates).</summary>
    private async Task AddAsync(List<ReadinessResult> rows, string code, Func<Task<ReadinessResult>> check, string? host = null)
    {
        try
        {
            rows.Add(await check().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            rows.Add(Failed(code, ex, host));
        }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// What one run reads once and several areas use: the domain controllers of the configuration partition and the test
    /// computer of the remote checks. In memory for the run only.
    /// </summary>
    private sealed class RunContext(IDirectorySession session, ReadinessRequest request)
    {
        public IDirectorySession Session { get; } = session;

        public ReadinessRequest Request { get; } = request;

        /// <summary>The DCs of the domain (null: not read yet; empty: none or not readable).</summary>
        public IReadOnlyList<DirectoryDomainController>? Controllers { get; set; }

        /// <summary>The test computer, once resolved (null: not resolved yet).</summary>
        public TestComputer? TestComputer { get; set; }
    }

    /// <summary>
    /// The test computer of a full check: its remote target, or why there is none (no name given, not found, no DNS name).
    /// <see cref="Reason"/> is the explanation of the row of the first remote check; <see cref="Remedy"/> its remedy.
    /// </summary>
    private sealed record TestComputer(Core.Computers.Remote.RemoteTarget? Target, string? Reason, string? Remedy, ReadinessStatus Status)
    {
        public override string ToString() => Status.ToString();
    }
}
