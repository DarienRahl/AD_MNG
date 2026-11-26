using AdMng.Core.Csv;
using AdMng.Core.Ldap;
using AdMng.Core.PowerShell;
using AdMng.Core.Schema;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Lists;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;

namespace AdMng.Presentation.PowerShell;

/// <summary>
/// What <c>Out-AdmGrid</c>, <c>Open-AdmObject</c> and <c>Export-AdmList</c> do in the program (track R, stage R1): the grid window
/// opens, the properties of an object open, a CSV file is written - all through the services of the connection window the
/// PowerShell window belongs to. Its one state is the count of the grid windows open (at most <see cref="MaxGridWindows"/>); it
/// never reads the directory (the command has read what it opens).
/// </summary>
/// <remarks>
/// <b>Threads.</b> A command calls these on the pipeline thread; every call that shows a window or a dialog is handed to the UI
/// thread here, and the file is written on a pool thread, so neither the window nor the pipeline's own thread blocks the other.
/// <b>Nothing on disk unless the operator says so</b> (CLAUDE.md rule 1): <see cref="ExportListAsync"/> writes the path the
/// command was given or the one chosen in <i>Save As</i> - never a default path and never a temporary file.
/// </remarks>
public sealed class PowerShellWindowActions(DialogServices services, ICsvCodec csv) : IPowerShellWindowActions
{
    /// <summary>
    /// The most grid windows of <c>Out-AdmGrid</c> open at a time (owner answer 12 of the 0.16.1 plan, like the ten property windows
    /// of one <c>Open-AdmObject</c>): a loop that calls the command a thousand times is not a thousand windows. A closed grid frees
    /// its place. One per connection window, like the PowerShell window the actions serve.
    /// </summary>
    public const int MaxGridWindows = 10;

    private readonly DialogServices _services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly ICsvCodec _csv = csv ?? throw new ArgumentNullException(nameof(csv));

    /// <summary>The grid windows shown and not closed yet, and the one being shown (the pipeline thread counts up, the UI thread down).</summary>
    private int _openGrids;

    /// <inheritdoc />
    /// <exception cref="PowerShellGridLimitException"><see cref="MaxGridWindows"/> grid windows are open: no grid is shown.</exception>
    public async Task ShowInGridAsync(PowerShellGridTable table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _openGrids) > MaxGridWindows)
        {
            Interlocked.Decrement(ref _openGrids);
            throw new PowerShellGridLimitException(MaxGridWindows);
        }

        // The place is freed once: when the window closes, or when it could not be shown.
        var freed = 0;
        void Free()
        {
            if (Interlocked.Exchange(ref freed, 1) == 0)
            {
                Interlocked.Decrement(ref _openGrids);
            }
        }

        try
        {
            await _services.Dialogs.ShowPowerShellGridAsync(table, Free).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Free();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PowerShellOpenOutcome> OpenObjectAsync(string identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();

        // The command has resolved a name or an objectGUID through the window's own session and read the object; what reaches
        // here is its distinguished name. Anything else is not something this can open.
        if (!DistinguishedName.TryParse(identity.Trim(), out var dn) || dn.IsRoot)
        {
            return PowerShellOpenOutcome.NotFound;
        }

        await _services.Dialogs.ShowPropertiesAsync(dn.ToString()).ConfigureAwait(false);
        return PowerShellOpenOutcome.Opened;
    }

    /// <inheritdoc />
    public async Task<string?> ExportListAsync(PowerShellGridTable table, string? path, char? delimiter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        cancellationToken.ThrowIfCancellationRequested();

        // Columns with secrets (the clear-text LAPS password, a BitLocker recovery password, what the schema marks confidential) are
        // written only after the question of the program's list export; declined, they are left out (audit ps-commands-4).
        var kept = Enumerable.Range(0, table.Columns.Count).ToList();
        var secret = kept.Where(index => SecretAttributes.IsSecret(table.Columns[index], _services.Sessions.Current?.Schema)).ToList();
        if (secret.Count > 0)
        {
            var message = _services.Localizer.Format(L.Files.ExportSecretColumns, string.Join(", ", secret.Select(index => table.Columns[index])));
            var include = await OnUiThreadAsync(() => _services.Dialogs.ConfirmAsync(_services.Localizer[L.Batch.ExportPasswordsTitle], message, destructive: true)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!include)
            {
                kept.RemoveAll(secret.Contains);
            }
        }

        var target = path;
        if (target is null)
        {
            // No path: the operator chooses the file, or chooses none and nothing is written.
            var title = _services.Localizer[L.Commands.ExportList];
            var name = ListExportSnapshot.SuggestFileName(table.Title ?? _services.Localizer[L.PowerShellGrid.Title], _services.Time.GetLocalNow());
            FileFilter[] filters = [new(_services.Localizer[L.Files.CsvFilter], "*.csv")];
            target = await OnUiThreadAsync(() => _services.Files.SaveFileAsync(title, name, filters)).ConfigureAwait(false);
            if (target is null)
            {
                return null;
            }
        }

        // The separator of the operator's region (';' in Poland) unless the command named one.
        var separator = delimiter ?? CsvFiles.ListSeparator(_services.Regional.Culture);
        IReadOnlyList<string> headers = [.. kept.Select(index => table.Columns[index])];
        var rows = table.Rows.Select(row => (IReadOnlyList<string>)[.. kept.Select(index => index < row.Count ? row[index] ?? string.Empty : string.Empty)]);
        await CsvFiles.WriteAsync(target, writer => _csv.Write(writer, headers, rows, separator, protectFormulas: true), cancellationToken).ConfigureAwait(false);
        return target;
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread (a dialog needs it) and gives its result back to the caller's thread.</summary>
    private Task<T> OnUiThreadAsync<T>(Func<Task<T>> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _services.Dispatcher.RunAsync(async () =>
        {
            try
            {
                completion.SetResult(await work().ConfigureAwait(true));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }
}
