using System.Text;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Batch;
using AdMng.Core.Batch.Creation;
using AdMng.Core.Csv;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Schema;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.BulkCreate;

/// <summary>The filters of the check grid (<c>BulkCheck.Filter.&lt;filter&gt;</c>, docs/dev/phase3-bulk-create.md 2.7).</summary>
public enum BulkCheckFilter
{
    /// <summary><em>Wszystkie</em>.</summary>
    All,

    /// <summary><em>OK</em>.</summary>
    Ok,

    /// <summary><em>Ostrzeżenia</em>.</summary>
    Warnings,

    /// <summary><em>Błędy</em>.</summary>
    Errors,

    /// <summary><em>Możliwe duplikaty</em> (<c>PossibleExistingAccount</c>, <c>DuplicateSourceRow</c>).</summary>
    PossibleDuplicates,

    /// <summary><em>Wykluczone</em>.</summary>
    Excluded,

    /// <summary><em>Utworzone</em>.</summary>
    Created,
}

/// <summary>
/// A column of the check grid (2.7): the App generates the WPF <c>DataGrid</c> columns in code-behind from these descriptors
/// (no WPF types in Presentation) and binds the cells by index.
/// </summary>
/// <param name="Key">The attribute's LDAP name, or a fixed key of the grid (<c>#number</c>, <c>#manager</c>, <c>#groups</c>, <c>#notes</c>).</param>
/// <param name="Header">The localized header.</param>
/// <param name="IsEditable">The cells can be edited (an edit is stored and recomputed).</param>
/// <param name="IsMasked">The cells show <c>••••••••</c>.</param>
public sealed record CheckColumnDescriptor(string Key, string Header, bool IsEditable, bool IsMasked)
{
    /// <summary>Frozen on the left (<em>Nr</em>, <em>Imię</em>, <em>Nazwisko</em>).</summary>
    public bool IsFrozen { get; init; }

    /// <summary>The flags only – the key may name a column of the sheet.</summary>
    public override string ToString() => $"Check column (editable={IsEditable}, masked={IsMasked}, frozen={IsFrozen})";
}

/// <summary>
/// Step 4 <em>Sprawdzenie</em> of the bulk creation wizard (docs/dev/phase3-bulk-create.md 2.7, owner B4): computes on
/// entering the step (cancellable, progress), the virtualized check grid with dynamic editable columns updated in place,
/// edits recomputed debounced, exclusions, filters, <em>Następny błąd</em> (F8), <em>Kopiuj wiersze z błędami</em>, the
/// summary, the scope note and the run issues, <em>Sprawdź ponownie</em>, <em>Numeruj od nowa</em>, and the read-only
/// global-catalog session of a multi-domain forest (opened in the background, awaited by the computations, closed on
/// <see cref="Dispose"/>, on a session change and in <see cref="OnSessionEnded"/>).
/// </summary>
/// <remarks>
/// Every computation runs on the thread pool (rule 6); the grid is filled with <c>ReplaceAll</c> on the first fill and on a
/// filter change and updated in place afterwards (rows keyed by their number, the open cell editor keeps its text). Rows,
/// cells, edits and the lookups live only in memory and are dropped on <see cref="Dispose"/> (rule 1).
/// </remarks>
public sealed partial class BulkCheckViewModel : ObservableObject, IDisposable
{
    /// <summary>The key of the <em>Nr</em> column.</summary>
    public const string NumberKey = "#number";

    /// <summary>The key of the <em>Uwagi</em> column.</summary>
    public const string NotesKey = "#notes";

    /// <summary>The key of the <em>Menedżer</em> column (the RDN of the resolved manager; edits the manager text).</summary>
    public const string ManagerKey = "#manager";

    /// <summary>The key of the <em>Grupy</em> column (the number of groups; edits the row's group text).</summary>
    public const string GroupsKey = "#groups";

    /// <summary>How long a computation waits for the global-catalog session before it checks in the domain only.</summary>
    internal static readonly TimeSpan GlobalCatalogWait = TimeSpan.FromSeconds(20);

    private readonly SerialCancellation _computing = new();
    private readonly Debouncer _recompute;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<int, CheckRowViewModel> _rows = [];
    private readonly List<CheckRowViewModel> _ordered = [];
    private readonly Dictionary<int, Dictionary<string, string>> _edits = [];
    private readonly HashSet<int> _excluded = [];
    private Task? _globalCatalog;
    private IDirectorySession? _globalCatalogSession;
    private ReadOnlyDirectoryReader? _globalCatalogReader;
    private IDirectorySession? _initializedFor;
    private string? _counterSignature;
    private int _running;
    private bool _subscribed;
    private bool _sessionEnded;
    private bool _disposed;

    /// <param name="services">The dialog services of the window.</param>
    /// <param name="planner">The planner (<see cref="IBulkCreatePlanner.ComputeAsync"/>).</param>
    /// <param name="input">The wizard's current input (<see cref="BulkCreateViewModel.BuildInput"/>; null while a step is invalid).</param>
    /// <param name="session">The window's session, null once it ended.</param>
    /// <param name="connector">The connector of the GC session (2.2); null = scope <see cref="UniquenessScope.Domain"/> in a multi-domain forest.</param>
    public BulkCheckViewModel(
        DialogServices services,
        IBulkCreatePlanner planner,
        Func<BulkCreateInput?> input,
        Func<IDirectorySession?> session,
        IDirectoryConnector? connector)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Planner = planner ?? throw new ArgumentNullException(nameof(planner));
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Connector = connector;
        _recompute = new Debouncer(services.Time, TimeSpan.FromMilliseconds(300));
        var localizer = services.Localizer;
        Filters = [.. Enum.GetValues<BulkCheckFilter>().Select(f => new Option<BulkCheckFilter>(f, localizer[BulkCreateTextKeys.FilterKey(f)]))];
    }

    /// <summary>The computation, an edit, an exclusion or the busy state changed (the wizard re-evaluates <c>IsReadyToPlan</c>).</summary>
    public event EventHandler? Changed;

    internal DialogServices Services { get; }

    internal IBulkCreatePlanner Planner { get; }

    /// <summary>The wizard's input (the host's <c>BuildInput</c>; tests replace it to drive the check without steps 1–3).</summary>
    internal Func<BulkCreateInput?> Input { get; set; }

    internal Func<IDirectorySession?> Session { get; }

    internal IDirectoryConnector? Connector { get; }

    /// <summary>The first data row in Excel (step 1's <em>Pierwszy wiersz danych w Excelu</em>); null = 2 with a header, 1 without.</summary>
    internal Func<int>? ExcelRowOffset { get; set; }

    /// <summary>The debounce of the recompute after an edit or an exclusion (300 ms; tests set zero).</summary>
    internal TimeSpan EditDelay
    {
        get => _recompute.Delay;
        set => _recompute.Delay = value;
    }

    /// <summary>
    /// Incremented by <see cref="OnInputReset"/> (table replaced, kind changed): row numbers of an earlier table no longer
    /// name the same rows, so the wizard forgets which rows it created (BulkCreateViewModel.Plan.cs).
    /// </summary>
    internal int ResetGeneration { get; private set; }

    /// <summary>The lookup cache of the window (a new one when the GC session arrives; dropped when the session ends).</summary>
    public BulkCreateLookups Lookups { get; private set; } = new();

    /// <summary>The last computation (null before the first).</summary>
    [ObservableProperty]
    public partial BulkCreateComputation? Computation { get; private set; }

    /// <summary>A computation runs (progress bar, <em>Zatrzymaj</em>).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsComputing { get; private set; }

    /// <summary>Rows computed so far / to compute.</summary>
    [ObservableProperty]
    public partial int ComputedRows { get; private set; }

    [ObservableProperty]
    public partial int TotalRows { get; private set; }

    /// <summary>The input changed since the shown computation (an earlier step or the container; <em>Sprawdź ponownie</em>).</summary>
    [ObservableProperty]
    public partial bool IsStale { get; private set; }

    /// <summary>The visible rows of the grid (filtered; updated in place on a recompute).</summary>
    public BulkObservableCollection<CheckRowViewModel> Rows { get; } = [];

    /// <summary>The columns of the grid (the App rebuilds the <c>DataGrid</c> columns when this changes).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CheckColumnDescriptor> Columns { get; private set; } = [];

    public IReadOnlyList<Option<BulkCheckFilter>> Filters { get; }

    [ObservableProperty]
    public partial BulkCheckFilter Filter { get; set; } = BulkCheckFilter.All;

    /// <summary>The selected row of the grid (<em>Następny błąd</em> moves it; the view scrolls it into view).</summary>
    [ObservableProperty]
    public partial CheckRowViewModel? SelectedRow { get; set; }

    /// <summary><em>Wiersze: 120 – OK: 108, z ostrzeżeniami: 6, z błędami: 4, wykluczone: 2</em>.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary><em>Unikalność UPN i adresów: …</em> (<c>BulkCheck.Scope.&lt;scope&gt;</c>).</summary>
    [ObservableProperty]
    public partial string? ScopeNote { get; private set; }

    /// <summary>The localized run issues shown above the grid.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> RunIssues { get; private set; } = [];

    /// <summary>The localized error of the last computation.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The cells edited in the grid: row number → attribute → value.</summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> Edits { get; private set; } =
        new Dictionary<int, IReadOnlyDictionary<string, string>>();

    /// <summary>The rows the operator excluded.</summary>
    public IReadOnlySet<int> ExcludedRows { get; private set; } = new HashSet<int>();

    /// <summary>The counter numbers of the last computation (the pins of the next one).</summary>
    public IReadOnlyDictionary<int, int> PinnedNumbers { get; private set; } = new Dictionary<int, int>();

    /// <summary>An edit or an exclusion waits for its recompute (the plan step computes first).</summary>
    internal bool HasPendingRecompute { get; private set; }

    /// <summary>The global-catalog session is open (a multi-domain forest whose DC is a ready GC).</summary>
    internal bool HasGlobalCatalog => _globalCatalogReader is not null;

    /// <summary>Computes every row (on entering the step; cancellable through <c>ComputeCancelCommand</c>).</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private Task ComputeAsync(CancellationToken cancellationToken) => RunComputeAsync(cancellationToken);

    /// <summary><em>Zatrzymaj</em>: stops whatever computation runs (entering the step, a recompute after an edit).</summary>
    [RelayCommand(CanExecute = nameof(IsComputing))]
    private void Stop()
    {
        _recompute.Cancel();
        _computing.Cancel();
        HasPendingRecompute = false;
    }

    /// <summary><em>Sprawdź ponownie</em>: forgets the lookups and computes.</summary>
    [RelayCommand]
    private Task RecheckAsync()
    {
        Lookups.Refresh();
        return RunComputeAsync(CancellationToken.None);
    }

    /// <summary><em>Numeruj od nowa</em>: clears the pins and computes.</summary>
    [RelayCommand]
    private Task RenumberAsync()
    {
        PinnedNumbers = new Dictionary<int, int>();
        return RunComputeAsync(CancellationToken.None);
    }

    /// <summary><em>Następny błąd</em> (F8).</summary>
    [RelayCommand]
    private void NextError()
    {
        if (!_ordered.Any(r => r.Status == BulkRowStatus.Error))
        {
            Services.Notifications.Show(NotificationSeverity.Info, Services.Localizer[L.BulkCreateCheck.NoErrorRows]);
            return;
        }

        if (!Rows.Any(r => r.Status == BulkRowStatus.Error))
        {
            Filter = BulkCheckFilter.All;
        }

        var start = SelectedRow is { } selected ? Rows.IndexOf(selected) : -1;
        for (var step = 1; step <= Rows.Count; step++)
        {
            var row = Rows[(start + step + Rows.Count) % Rows.Count];
            if (row.Status == BulkRowStatus.Error)
            {
                SelectedRow = row;
                return;
            }
        }
    }

    /// <summary><em>Kopiuj wiersze z błędami</em>: the source cells of the error rows and <em>Uwagi</em> as TSV.</summary>
    [RelayCommand]
    private void CopyErrorRows()
    {
        var localizer = Services.Localizer;
        if (Computation is not { } computation || !_ordered.Any(r => r.Status == BulkRowStatus.Error))
        {
            Services.Notifications.Show(NotificationSeverity.Info, localizer[L.BulkCreateCheck.NoErrorRows]);
            return;
        }

        // The sheet's own cells (what HR sent), so the rows can go back for correction; password-like columns as the mask.
        var table = computation.Input.Table;
        var masked = table.Columns.Select(c => c.Header.Length > 0 && ColumnSynonyms.IsPasswordLike(c.Header)).ToList();
        var text = new StringBuilder();
        AppendTsv(text, [localizer[L.BulkCreateCheck.ColumnNumber], .. table.Columns.Select(c => c.Name), localizer[L.BulkCreateCheck.ColumnNotes]]);
        var count = 0;
        for (var index = 0; index < table.Rows.Count; index++)
        {
            var source = table.Rows[index];
            if (!_rows.TryGetValue(source.Number, out var row) || row.Status != BulkRowStatus.Error)
            {
                continue;
            }

            var cells = table.Columns.Select(c => masked[c.Index] ? CreatedAccountViewModel.Mask : source.Cell(c.Index));
            AppendTsv(text, [ExcelRowOf(source.Number, table).ToString(localizer.Culture), .. cells, row.Notes]);
            count++;
        }

        ClipboardCopy.Text(Services.Clipboard, Services.Notifications, text.ToString(), localizer.Format(L.BulkCreateCheck.ErrorRowsCopied, count));
    }

    /// <summary><em>Przywróć wartość wyliczoną</em>: forgets the operator's edit of the cell and computes again.</summary>
    [RelayCommand]
    private void RevertCell(CheckCellViewModel? cell)
    {
        if (cell?.EditKey is not { } key || !_edits.TryGetValue(cell.RowNumber, out var row) || !row.Remove(key))
        {
            return;
        }

        if (row.Count == 0)
        {
            _edits.Remove(cell.RowNumber);
        }

        PublishEdits();
        ScheduleRecompute();
    }

    /// <summary>
    /// Reads the forest and, in a multi-domain forest with a ready GC, connects the GC session in the background (2.2); the
    /// computations await it. Subscribes to the session changes (the GC session is closed when the window's session goes).
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!_subscribed)
        {
            Services.Sessions.SessionChanged += OnSessionChanged;
            _subscribed = true;
        }

        if (Session() is not { } session || ReferenceEquals(session, _initializedFor) || _sessionEnded)
        {
            return;
        }

        _initializedFor = session;
        if (Connector is not { } connector || session.Info.IsGlobalCatalogReady != true)
        {
            return;
        }

        ForestInfo forest;
        try
        {
            var token = _lifetime.Token;
            forest = await Task.Run(() => ForestScope.ReadAsync(session, token), token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is DirectoryException or OperationCanceledException or ObjectDisposedException)
        {
            // The planner reads the forest again and reports the scope it could check.
            return;
        }

        if (forest.DomainCount > 1 && !_disposed && !_sessionEnded && ReferenceEquals(Session(), session))
        {
            _globalCatalog = ConnectGlobalCatalogAsync(session, connector, _lifetime.Token);
        }
    }

    /// <summary>The step is entered: computes when nothing was computed yet or the input changed since.</summary>
    public Task OnEnteredAsync() =>
        _disposed || (Computation is not null && !IsStale && !HasPendingRecompute) ? Task.CompletedTask : RunComputeAsync(CancellationToken.None);

    /// <summary>An earlier step or the container changed: the computation is stale (computed again on entering the step).</summary>
    public void OnInputChanged()
    {
        if (_disposed)
        {
            return;
        }

        if (Computation is not null)
        {
            IsStale = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The table was replaced (header choice, <em>Wklej i zastąp</em>, <em>Wyczyść</em>, a new file) or the kind changed:
    /// drops the edits, pins, exclusions and created marks and the computation.
    /// </summary>
    public void OnInputReset()
    {
        if (_disposed)
        {
            return;
        }

        _recompute.Cancel();
        _computing.Cancel();
        ResetGeneration++;

        // The cache may predate accounts this window created: the rows of the new table are checked against them.
        Lookups.Refresh();
        _edits.Clear();
        _excluded.Clear();
        PublishEdits();
        ExcludedRows = new HashSet<int>();
        PinnedNumbers = new Dictionary<int, int>();
        _counterSignature = null;
        HasPendingRecompute = false;
        ClearGrid();
        Computation = null;
        IsStale = false;
        Error = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The window's session ended: cancels a running computation, closes the GC session, drops the lookups.</summary>
    public void OnSessionEnded()
    {
        if (_disposed)
        {
            return;
        }

        _sessionEnded = true;
        _recompute.Cancel();
        _computing.Cancel();
        HasPendingRecompute = false;
        CloseGlobalCatalog();
        Lookups = new BulkCreateLookups();
    }

    /// <summary>
    /// Shows a finished computation without computing (the plan step's statuses, the smoke test and screenshots of a window
    /// without a directory): the grid is updated in place, the pins are its numbers.
    /// </summary>
    public void Show(BulkCreateComputation computation)
    {
        ArgumentNullException.ThrowIfNull(computation);
        if (_disposed)
        {
            return;
        }

        Apply(computation);
    }

    /// <summary>
    /// Waits for the global-catalog session that is being connected (at most <see cref="GlobalCatalogWait"/>); after that the
    /// computation checks in the domain only and recomputes once the session arrives.
    /// </summary>
    internal async Task WaitForGlobalCatalogAsync(CancellationToken cancellationToken)
    {
        if (_globalCatalog is not { IsCompleted: false } connecting)
        {
            return;
        }

        try
        {
            await connecting.WaitAsync(GlobalCatalogWait, Services.Time, cancellationToken).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            // Checked in the domain only for now; the session recomputes the grid when it arrives.
        }
    }

    /// <summary>The plan step: a pending recompute or a stale grid is computed now, so the frozen values are the latest ones.</summary>
    internal async Task EnsureCurrentAsync(CancellationToken cancellationToken)
    {
        if (Computation is null || IsStale || HasPendingRecompute || IsComputing)
        {
            _recompute.Cancel();
            await RunComputeAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>The rows an execution created: shown as created at once, then computed again (their values stay reserved).</summary>
    internal void MarkCreated(IEnumerable<int> rows)
    {
        var localizer = Services.Localizer;
        foreach (var number in rows)
        {
            if (_rows.TryGetValue(number, out var row))
            {
                row.Status = BulkRowStatus.Created;
                row.StatusText = localizer[BulkCreateTextKeys.StatusKey(BulkRowStatus.Created)];
                row.SetIncluded(true);
                foreach (var cell in row.Cells)
                {
                    cell.IsEditable = false;
                }
            }
        }

        if (Computation is not null)
        {
            IsStale = true;
        }
    }

    /// <summary>
    /// After an execution or an undo: the grid is computed again in the background (debounced), so it is current when the
    /// operator goes back to <em>Sprawdzenie</em> (created rows need no lookups).
    /// </summary>
    internal void RecomputeSoon()
    {
        if (Computation is not null && !_sessionEnded)
        {
            ScheduleRecompute();
        }
    }

    /// <summary>The undo deleted these rows: they are no longer created and are computed again with the next computation.</summary>
    internal void OnUndone(IReadOnlyCollection<int> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var number in rows)
        {
            if (_rows.TryGetValue(number, out var row) && row.Status == BulkRowStatus.Created)
            {
                foreach (var cell in row.Cells)
                {
                    cell.IsEditable = cell.EditKey is not null;
                }
            }
        }

        if (Computation is not null && rows.Count > 0)
        {
            IsStale = true;
        }
    }

    /// <summary>The Excel row of a wizard row (<em>Nr</em>: number + the first data row − 1).</summary>
    internal int ExcelRow(int number) => Computation?.Input.Table is { } table
        ? ExcelRowOf(number, table)
        : number + (ExcelRowOffset?.Invoke() ?? 2) - 1;

    /// <summary>The plan's computation (values frozen): rows whose value was taken since the check show the new status and reasons.</summary>
    internal void ApplyStatuses(BulkCreateComputation planned)
    {
        ArgumentNullException.ThrowIfNull(planned);
        var schema = Session()?.Schema;
        foreach (var row in planned.Rows)
        {
            if (_rows.TryGetValue(row.Number, out var vm) && row.Status is BulkRowStatus.Error && vm.Status != BulkRowStatus.Error)
            {
                vm.Status = row.Status;
                vm.StatusText = Services.Localizer[BulkCreateTextKeys.StatusKey(row.Status)];
                vm.Notes = NotesOf(row.Issues, schema);
            }
        }

        UpdateSummary();
    }

    private async Task RunComputeAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        var localizer = Services.Localizer;
        var session = Session();
        if (session is null || _sessionEnded)
        {
            Error = localizer[_sessionEnded ? L.WindowSession.Ended : L.Errors.NotConnected];
            return;
        }

        var token = _computing.Next();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken, _lifetime.Token);
        HasPendingRecompute = false;
        _running++;
        IsComputing = true;
        Error = null;
        try
        {
            await WaitForGlobalCatalogAsync(linked.Token).ConfigureAwait(true);
            if (Input() is not { } input)
            {
                Error = localizer[L.BulkCreateCheck.InputIncomplete];
                return;
            }

            // Pins are kept while the counter rules stay the same (2.4: a new prefix, start or step numbers from scratch).
            var signature = CounterSignature(input);
            if (_counterSignature is not null && !string.Equals(signature, _counterSignature, StringComparison.Ordinal) && input.PinnedNumbers.Count > 0)
            {
                PinnedNumbers = new Dictionary<int, int>();
                input = input with { PinnedNumbers = PinnedNumbers };
            }

            ComputedRows = 0;
            TotalRows = input.Table.Rows.Count;
            var progress = Services.Dispatcher.CreateProgress<int>(n =>
            {
                if (_computing.IsCurrent(token))
                {
                    ComputedRows = n;
                }
            });
            var lookups = Lookups;
            var planner = Planner;
            var computation = await Task.Run(() => planner.ComputeAsync(session, input, lookups, progress, linked.Token), linked.Token).ConfigureAwait(true);
            if (!_computing.IsCurrent(token) || _disposed)
            {
                return;
            }

            _counterSignature = signature;
            Apply(computation);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Stopped, superseded by a newer computation, or the window closed.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_computing.IsCurrent(token))
            {
                Error = Services.Errors.Translate(ex);
            }
        }
        finally
        {
            _running--;
            IsComputing = _running > 0;
        }
    }

    /// <summary>The counter rules and the counter mode: when they change, the pins of the last computation no longer apply.</summary>
    private static string CounterSignature(BulkCreateInput input)
    {
        var counters = input.Rules.Attributes
            .Where(r => r.Pattern.Contains("{$nr", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Attribute.ToUpperInvariant() + "=" + r.Pattern)
            .Order(StringComparer.Ordinal);
        return $"{input.Rules.Kind}|{input.Rules.CounterMode}|{input.Rules.ReuseRecycleBinNumbers}|{string.Join("|", counters)}";
    }

    private void ScheduleRecompute()
    {
        if (_disposed || Session() is null)
        {
            return;
        }

        // A computation still running was started before this edit or exclusion: its rows must not be shown over them.
        _computing.Cancel();
        HasPendingRecompute = true;
        Changed?.Invoke(this, EventArgs.Empty);
        _recompute.RunAsync(RunComputeAsync).Forget();
    }

    // ------------------------------------------------------------------ the grid

    private void Apply(BulkCreateComputation computation)
    {
        var schema = Session()?.Schema;
        var columns = BuildColumns(computation, schema);
        var rebuild = !SameColumns(columns, Columns);
        if (rebuild)
        {
            Columns = columns;
        }

        var seen = new HashSet<int>();
        var added = false;
        _ordered.Clear();
        var table = computation.Input.Table;
        var sources = new Dictionary<int, TableRow>(table.Rows.Count);
        foreach (var source in table.Rows)
        {
            sources.TryAdd(source.Number, source);
        }

        foreach (var row in computation.Rows)
        {
            if (!_rows.TryGetValue(row.Number, out var vm))
            {
                vm = new CheckRowViewModel(row.Number) { IncludedChanged = OnIncludedChanged };
                _rows[row.Number] = vm;
                added = true;
            }

            if (rebuild || vm.Cells.Count != columns.Count)
            {
                vm.Cells = [.. columns.Select(c => NewCell(row.Number, c))];
            }

            UpdateRow(vm, row, computation, sources.GetValueOrDefault(row.Number), schema);
            seen.Add(row.Number);
            _ordered.Add(vm);
        }

        var removed = _rows.Keys.Where(n => !seen.Contains(n)).ToList();
        foreach (var number in removed)
        {
            _rows.Remove(number);
        }

        Computation = computation;
        PinnedNumbers = computation.Numbers;
        IsStale = false;
        ScopeNote = Services.Localizer.Format(L.BulkCreateCheck.ScopeNote, ScopeText(computation));
        RunIssues = [.. computation.Issues
            .OrderByDescending(i => i.Severity)
            .Select(i => Services.Localizer.IssueText(i, schema))
            .Distinct(StringComparer.Ordinal)];
        UpdateSummary();
        if (rebuild || added || removed.Count > 0 || Rows.Count == 0)
        {
            ApplyFilter();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string ScopeText(BulkCreateComputation computation) => computation.Scope == UniquenessScope.Forest
        ? Services.Localizer.Format(BulkCreateTextKeys.ScopeKey(computation.Scope), computation.Server)
        : Services.Localizer[BulkCreateTextKeys.ScopeKey(computation.Scope)];

    private List<CheckColumnDescriptor> BuildColumns(BulkCreateComputation computation, ISchemaCatalog? schema)
    {
        var localizer = Services.Localizer;
        var kind = computation.Input.Rules.Kind;
        string[] frozenNames = kind is ObjectKind.User or ObjectKind.InetOrgPerson or ObjectKind.Contact
            ? [Attr.GivenName, Attr.Sn]
            : [Attr.Cn];
        var attributes = computation.Attributes;
        var columns = new List<CheckColumnDescriptor>
        {
            new(NumberKey, localizer[L.BulkCreateCheck.ColumnNumber], IsEditable: false, IsMasked: false) { IsFrozen = true },
        };
        foreach (var name in frozenNames)
        {
            columns.Add(new CheckColumnDescriptor(name, localizer.AttributeName(name, schema), IsEditable: true, IsMasked: false) { IsFrozen = true });
        }

        columns.Add(new CheckColumnDescriptor(NotesKey, localizer[L.BulkCreateCheck.ColumnNotes], IsEditable: false, IsMasked: false));
        foreach (var attribute in attributes)
        {
            if (frozenNames.Contains(attribute, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var secret = SecretAttributes.IsSecret(attribute, schema);
            columns.Add(new CheckColumnDescriptor(attribute, localizer.AttributeName(attribute, schema), IsEditable: !secret, IsMasked: secret));
        }

        var input = computation.Input;
        if (input.Mappings.Any(m => m.Kind == ColumnTargetKind.Manager) || computation.Rows.Any(r => r.ManagerDn is not null || r.ManagerCandidates.Count > 0))
        {
            columns.Add(new CheckColumnDescriptor(ManagerKey, localizer[L.BulkCreateCheck.ColumnManager], IsEditable: true, IsMasked: false));
        }

        if (input.Mappings.Any(m => m.Kind == ColumnTargetKind.Groups) || input.Options.GroupDns.Count > 0 || computation.Rows.Any(r => r.GroupDns.Count > 0))
        {
            columns.Add(new CheckColumnDescriptor(GroupsKey, localizer[L.BulkCreateCheck.ColumnGroups], IsEditable: true, IsMasked: false));
        }

        return columns;
    }

    private static bool SameColumns(IReadOnlyList<CheckColumnDescriptor> a, IReadOnlyList<CheckColumnDescriptor> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);

    private CheckCellViewModel NewCell(int row, CheckColumnDescriptor column)
    {
        var key = column.Key switch
        {
            NumberKey or NotesKey => null,
            ManagerKey => BulkCreatePlanner.ManagerEditKey,
            GroupsKey => BulkCreatePlanner.GroupsEditKey,
            _ => column.IsEditable ? column.Key : null,
        };
        return new CheckCellViewModel(column.Key) { EditKey = key, RowNumber = row, Edited = OnCellEdited };
    }

    private void UpdateRow(CheckRowViewModel vm, BulkCreateRow row, BulkCreateComputation computation, TableRow? source, ISchemaCatalog? schema)
    {
        var localizer = Services.Localizer;
        var input = computation.Input;
        vm.ExcelRow = ExcelRowOf(row.Number, input.Table);
        vm.Status = row.Status;
        vm.StatusText = localizer[BulkCreateTextKeys.StatusKey(row.Status)];
        vm.Notes = NotesOf(row.Issues, schema);
        vm.IsPossibleDuplicate = row.Issues.Any(i => i.Code is BulkCreateIssueCodes.PossibleExistingAccount or BulkCreateIssueCodes.DuplicateSourceRow);
        vm.SetIncluded(row.Status != BulkRowStatus.Excluded);
        var editable = row.Status != BulkRowStatus.Created;
        var edits = input.Edits.TryGetValue(row.Number, out var rowEdits) ? rowEdits : null;
        var values = new Dictionary<string, BulkValue>(row.Values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var value in row.Values)
        {
            values.TryAdd(value.Attribute, value);
        }

        // The cells are in column order (Apply builds them from Columns), so the descriptor is found by index: a grid of
        // 256 columns and 5 000 rows is updated without a search per cell.
        var columns = Columns;
        for (var index = 0; index < vm.Cells.Count; index++)
        {
            var cell = vm.Cells[index];
            var column = index < columns.Count && columns[index].Key == cell.Key ? columns[index] : columns.FirstOrDefault(c => c.Key == cell.Key);
            switch (cell.Key)
            {
                case NumberKey:
                    var number = vm.ExcelRow.ToString(localizer.Culture);
                    cell.Update(number, number, null, false, false, null, false);
                    break;
                case NotesKey:
                    cell.Update(vm.Notes, vm.Notes, null, false, false, vm.Notes.Length > 0 ? vm.Notes : null, false);
                    break;
                case ManagerKey:
                    UpdateManager(cell, row, input, source, edits, editable);
                    break;
                case GroupsKey:
                    UpdateGroups(cell, row, input, source, edits, editable);
                    break;
                default:
                    UpdateValue(cell, values.GetValueOrDefault(cell.Key), column, editable);
                    break;
            }
        }
    }

    private void UpdateValue(CheckCellViewModel cell, BulkValue? value, CheckColumnDescriptor? column, bool editable)
    {
        var text = value is null ? string.Empty : string.Join(" | ", value.Values);
        var masked = column?.IsMasked == true;
        var shown = masked && text.Length > 0 ? CreatedAccountViewModel.Mask : text;
        var adjusted = value is { } v && (v.Adjusted || v.Sanitized || v.Truncated);
        cell.Update(shown, masked ? string.Empty : text, value?.Source, value?.Source == BulkValueSource.Edited, adjusted,
            value is null ? null : CellToolTip(value), editable && column?.IsEditable == true);
    }

    private string CellToolTip(BulkValue value)
    {
        var localizer = Services.Localizer;
        var parts = new List<string> { localizer.Format(L.BulkCreateCheck.CellSource, localizer[BulkCreateTextKeys.SourceKey(value.Source)]) };
        if (value.Adjusted)
        {
            parts.Add(localizer[L.BulkCreateCheck.CellAdjusted]);
        }

        if (value.Sanitized)
        {
            parts.Add(localizer[L.BulkCreateCheck.CellSanitized]);
        }

        if (value.Truncated)
        {
            parts.Add(localizer[L.BulkCreateCheck.CellTruncated]);
        }

        return string.Join(Environment.NewLine, parts);
    }

    private void UpdateManager(
        CheckCellViewModel cell,
        BulkCreateRow row,
        BulkCreateInput input,
        TableRow? source,
        IReadOnlyDictionary<string, string>? edits,
        bool editable)
    {
        var edited = edits is not null && edits.TryGetValue(BulkCreatePlanner.ManagerEditKey, out var editedText) ? editedText : null;
        var sourceText = edited ?? SourceText(input, source, ColumnTargetKind.Manager);
        var shown = row.ManagerDn is { } dn ? RdnOf(dn) : sourceText;
        cell.Update(shown, sourceText, edited is not null ? BulkValueSource.Edited : sourceText.Length > 0 ? BulkValueSource.Column : null,
            edited is not null, false, row.ManagerDn, editable);
        cell.SetCandidates([.. row.ManagerCandidates.Select(c => new Option<string>(c, CandidateText(c)))]);
    }

    private void UpdateGroups(
        CheckCellViewModel cell,
        BulkCreateRow row,
        BulkCreateInput input,
        TableRow? source,
        IReadOnlyDictionary<string, string>? edits,
        bool editable)
    {
        var edited = edits is not null && edits.TryGetValue(BulkCreatePlanner.GroupsEditKey, out var editedText) ? editedText : null;
        var sourceText = edited ?? SourceText(input, source, ColumnTargetKind.Groups);
        var count = row.GroupDns.Count.ToString(Services.Localizer.Culture);
        var names = row.GroupDns.Count == 0 ? null : string.Join(Environment.NewLine, row.GroupDns.Select(RdnOf));
        cell.Update(count, sourceText, edited is not null ? BulkValueSource.Edited : null, edited is not null, false, names, editable);
    }

    private static string SourceText(BulkCreateInput input, TableRow? source, ColumnTargetKind target)
    {
        if (source is null)
        {
            return string.Empty;
        }

        var mapping = input.Mappings.FirstOrDefault(m => m.Kind == target);
        return mapping is null ? string.Empty : source.Cell(mapping.Column);
    }

    /// <summary>The candidate's name and its container, so that two people of the same name can be told apart.</summary>
    private static string CandidateText(string dn)
    {
        if (!DistinguishedName.TryParse(dn, out var parsed) || parsed.IsRoot)
        {
            return dn;
        }

        var parent = parsed.Parent;
        return parent is null || parent.IsRoot ? parsed.Name : $"{parsed.Name} ({parent.Name})";
    }

    private static string RdnOf(string dn) => DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot ? parsed.Name : dn;

    private string NotesOf(IReadOnlyList<ValidationIssue> issues, ISchemaCatalog? schema) => string.Join(" ", issues
        .OrderByDescending(i => i.Severity)
        .Select(i => Services.Localizer.IssueText(i, schema))
        .Distinct(StringComparer.Ordinal));

    private int ExcelRowOf(int number, InputTable table)
    {
        var offset = ExcelRowOffset?.Invoke() ?? (table.HasHeader ? (table.HeaderLine ?? 1) + 1 : 1);
        return number + offset - 1;
    }

    private void UpdateSummary()
    {
        var localizer = Services.Localizer;
        int Count(BulkRowStatus status) => _ordered.Count(r => r.Status == status);
        var created = Count(BulkRowStatus.Created);
        Summary = created > 0
            ? localizer.Format(L.BulkCreateCheck.SummaryWithCreated, _ordered.Count, Count(BulkRowStatus.Ok), Count(BulkRowStatus.Warning), Count(BulkRowStatus.Error),
                Count(BulkRowStatus.Excluded), created)
            : localizer.Format(L.BulkCreateCheck.Summary, _ordered.Count, Count(BulkRowStatus.Ok), Count(BulkRowStatus.Warning), Count(BulkRowStatus.Error),
                Count(BulkRowStatus.Excluded));
    }

    private void ApplyFilter()
    {
        Rows.ReplaceAll(_ordered.Where(Matches));
        if (SelectedRow is { } selected && !Rows.Contains(selected))
        {
            SelectedRow = null;
        }
    }

    private bool Matches(CheckRowViewModel row) => Filter switch
    {
        BulkCheckFilter.Ok => row.Status == BulkRowStatus.Ok,
        BulkCheckFilter.Warnings => row.Status == BulkRowStatus.Warning,
        BulkCheckFilter.Errors => row.Status == BulkRowStatus.Error,
        BulkCheckFilter.PossibleDuplicates => row.IsPossibleDuplicate,
        BulkCheckFilter.Excluded => row.Status == BulkRowStatus.Excluded,
        BulkCheckFilter.Created => row.Status == BulkRowStatus.Created,
        _ => true,
    };

    partial void OnFilterChanged(BulkCheckFilter value) => ApplyFilter();

    private void ClearGrid()
    {
        _rows.Clear();
        _ordered.Clear();
        Rows.ReplaceAll([]);
        Columns = [];
        SelectedRow = null;
        Summary = string.Empty;
        ScopeNote = null;
        RunIssues = [];
    }

    // ------------------------------------------------------------------ edits and exclusions

    private void OnCellEdited(CheckCellViewModel cell, string text)
    {
        if (_disposed || cell.EditKey is not { } key)
        {
            return;
        }

        if (string.Equals(text, cell.ComputedEditText, StringComparison.Ordinal))
        {
            return;
        }

        if (!_edits.TryGetValue(cell.RowNumber, out var row))
        {
            row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _edits[cell.RowNumber] = row;
        }

        row[key] = text;
        if (cell.Key is not (ManagerKey or GroupsKey))
        {
            cell.Value = text;
        }

        cell.IsEdited = true;
        PublishEdits();
        ScheduleRecompute();
    }

    private void OnIncludedChanged(CheckRowViewModel row, bool included)
    {
        if (_disposed || row.Status == BulkRowStatus.Created)
        {
            return;
        }

        if (included ? !_excluded.Remove(row.Number) : !_excluded.Add(row.Number))
        {
            return;
        }

        ExcludedRows = new HashSet<int>(_excluded);
        if (!included)
        {
            row.Status = BulkRowStatus.Excluded;
            row.StatusText = Services.Localizer[BulkCreateTextKeys.StatusKey(BulkRowStatus.Excluded)];
        }

        UpdateSummary();
        ScheduleRecompute();
    }

    private void PublishEdits() =>
        Edits = _edits.ToDictionary(e => e.Key, e => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(e.Value, StringComparer.OrdinalIgnoreCase));

    private static void AppendTsv(StringBuilder text, IEnumerable<string> cells)
    {
        text.Append(string.Join('\t', cells.Select(c => CsvCodec.FormatField(c, '\t', protectFormulas: true))));
        text.Append("\r\n");
    }

    // ------------------------------------------------------------------ the global-catalog session (2.2)

    private async Task ConnectGlobalCatalogAsync(IDirectorySession session, IDirectoryConnector connector, CancellationToken cancellationToken)
    {
        IDirectorySession catalog;
        try
        {
            catalog = await connector.ConnectGlobalCatalogAsync(session, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No GC session: uniqueness is checked in the domain only (the planner's CheckedInDomainOnly says so).
            return;
        }

        if (_disposed || _sessionEnded || !ReferenceEquals(Session(), session))
        {
            // Too late: the window closed or its session ended meanwhile.
            DisposeQuietly(catalog);
            return;
        }

        _globalCatalogSession = catalog;
        _globalCatalogReader = ReadOnlyDirectoryReader.Of(catalog);
        Lookups = new BulkCreateLookups(_globalCatalogReader);
        if (Computation is { Scope: not UniquenessScope.Forest } && !IsComputing)
        {
            // A computation that did not wait for the session: computed once more with the forest scope.
            RunComputeAsync(CancellationToken.None).Forget();
        }
    }

    private void CloseGlobalCatalog()
    {
        _globalCatalogReader?.Detach();
        _globalCatalogReader = null;
        if (_globalCatalogSession is { } catalog)
        {
            _globalCatalogSession = null;
            DisposeQuietly(catalog);
        }
    }

    private static void DisposeQuietly(IDirectorySession session) => Task.Run(async () =>
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Closing a read-only session that is gone anyway.
        }
    }).Forget();

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        // The window keeps its session; when it is no longer the current one (disconnected, another DC), the GC session goes.
        if (!_disposed && _initializedFor is { } owner && !ReferenceEquals(Services.Sessions.Current, owner))
        {
            Services.Dispatcher.RunAsync(CloseGlobalCatalog).Forget();
        }
    }

    /// <summary>Cancels, closes the GC session, unsubscribes from <c>Sessions.SessionChanged</c>, drops the rows.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _recompute.Cancel();
        _computing.Cancel();
        _lifetime.Cancel();
        if (_subscribed)
        {
            Services.Sessions.SessionChanged -= OnSessionChanged;
            _subscribed = false;
        }

        CloseGlobalCatalog();
        Lookups = new BulkCreateLookups();
        _edits.Clear();
        _excluded.Clear();
        ClearGrid();
        Computation = null;
        Changed = null;
    }

    public override string ToString() => $"Bulk create check: {Rows.Count} rows, computing={IsComputing}, {Filter}";

    partial void OnComputationChanged(BulkCreateComputation? value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnIsComputingChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);
}
