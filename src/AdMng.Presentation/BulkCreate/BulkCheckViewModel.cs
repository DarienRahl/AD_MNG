using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
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
/// Contract stub: the option lists are built and the members the wizard host calls (<see cref="InitializeAsync"/>,
/// <see cref="OnEnteredAsync"/>, <see cref="OnInputChanged"/>, <see cref="OnInputReset"/>, <see cref="OnSessionEnded"/>,
/// <see cref="Dispose"/>) do nothing; the commands throw until B4 writes them.
/// </remarks>
public sealed partial class BulkCheckViewModel : ObservableObject, IDisposable
{
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
        var localizer = services.Localizer;
        Filters = [.. Enum.GetValues<BulkCheckFilter>().Select(f => new Option<BulkCheckFilter>(f, localizer[BulkCreateTextKeys.FilterKey(f)]))];
    }

    /// <summary>The computation, an edit, an exclusion or the busy state changed (the wizard re-evaluates <c>IsReadyToPlan</c>).</summary>
    public event EventHandler? Changed;

    internal DialogServices Services { get; }

    internal IBulkCreatePlanner Planner { get; }

    internal Func<BulkCreateInput?> Input { get; }

    internal Func<IDirectorySession?> Session { get; }

    internal IDirectoryConnector? Connector { get; }

    /// <summary>The lookup cache of the window (a new one when the GC session arrives; dropped when the session ends).</summary>
    public BulkCreateLookups Lookups { get; private set; } = new();

    /// <summary>The last computation (null before the first).</summary>
    [ObservableProperty]
    public partial BulkCreateComputation? Computation { get; private set; }

    /// <summary>A computation runs (progress bar, <em>Zatrzymaj</em>).</summary>
    [ObservableProperty]
    public partial bool IsComputing { get; private set; }

    /// <summary>Rows computed so far / to compute.</summary>
    [ObservableProperty]
    public partial int ComputedRows { get; private set; }

    [ObservableProperty]
    public partial int TotalRows { get; private set; }

    /// <summary>The visible rows of the grid (filtered; updated in place on a recompute).</summary>
    public BulkObservableCollection<CheckRowViewModel> Rows { get; } = [];

    /// <summary>The columns of the grid (the App rebuilds the <c>DataGrid</c> columns when this changes).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CheckColumnDescriptor> Columns { get; private set; } = [];

    public IReadOnlyList<Option<BulkCheckFilter>> Filters { get; }

    [ObservableProperty]
    public partial BulkCheckFilter Filter { get; set; } = BulkCheckFilter.All;

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

    /// <summary>Computes every row (on entering the step; cancellable through <c>ComputeCancelCommand</c>).</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private Task ComputeAsync(CancellationToken cancellationToken) => throw new NotImplementedException("F3 B4: BulkCheckViewModel.Compute (2.7).");

    /// <summary><em>Sprawdź ponownie</em>: forgets the lookups and computes.</summary>
    [RelayCommand]
    private Task RecheckAsync() => throw new NotImplementedException("F3 B4: BulkCheckViewModel.Recheck (2.7).");

    /// <summary><em>Numeruj od nowa</em>: clears the pins and computes.</summary>
    [RelayCommand]
    private Task RenumberAsync() => throw new NotImplementedException("F3 B4: BulkCheckViewModel.Renumber (2.7).");

    /// <summary><em>Następny błąd</em> (F8).</summary>
    [RelayCommand]
    private void NextError() => throw new NotImplementedException("F3 B4: BulkCheckViewModel.NextError (2.7).");

    /// <summary><em>Kopiuj wiersze z błędami</em>: the source cells of the error rows and <em>Uwagi</em> as TSV.</summary>
    [RelayCommand]
    private void CopyErrorRows() => throw new NotImplementedException("F3 B4: BulkCheckViewModel.CopyErrorRows (2.7).");

    /// <summary>Reads the forest and, in a multi-domain forest with a ready GC, connects the GC session in the background. Contract: nothing.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>The step is entered: computes. Contract: nothing.</summary>
    public Task OnEnteredAsync() => Task.CompletedTask;

    /// <summary>An earlier step or the container changed: the computation is stale. Contract: nothing.</summary>
    public void OnInputChanged()
    {
    }

    /// <summary>
    /// The table was replaced (header choice, <em>Wklej i zastąp</em>, <em>Wyczyść</em>, a new file) or the kind changed:
    /// drops the edits, pins, exclusions and created marks and the computation. Contract: nothing.
    /// </summary>
    public void OnInputReset()
    {
    }

    /// <summary>The window's session ended: cancels a running computation, closes the GC session, drops the lookups. Contract: nothing.</summary>
    public void OnSessionEnded()
    {
    }

    /// <summary>Cancels, closes the GC session, unsubscribes from <c>Sessions.SessionChanged</c>, drops the rows. Contract: nothing.</summary>
    public void Dispose()
    {
    }

    public override string ToString() => $"Bulk create check: {Rows.Count} rows, computing={IsComputing}, {Filter}";

    partial void OnComputationChanged(BulkCreateComputation? value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnIsComputingChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);
}
