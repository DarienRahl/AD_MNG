using AdMng.Core.Batch.Creation;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.BatchEditing;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.BulkCreate;

/// <summary>The sub-steps of the batch editor's Edit step in create mode (docs/dev/phase3-bulk-create.md 2).</summary>
public enum BulkCreateStep
{
    /// <summary>1 <em>Dane</em>: paste, file, typing.</summary>
    Source,

    /// <summary>2 <em>Kolumny</em>: column → attribute / pattern only / ignore.</summary>
    Columns,

    /// <summary>3 <em>Nazwy i opcje</em>: numbering, patterns, options, profiles.</summary>
    Rules,

    /// <summary>4 <em>Sprawdzenie</em>: the computed rows; the batch editor's Preview follows.</summary>
    Check,
}

/// <summary>
/// The bulk creation wizard <em>Zakładanie wielu kont</em> – the Edit step of the batch editor in create mode
/// (<see cref="BatchSourceMode.Create"/>, docs/dev/phase3-bulk-create.md 2.6; the host is complete in the contract): the
/// kind and the one target container, the four sub-steps with their navigation, the children <see cref="Source"/>,
/// <see cref="Columns"/>, <see cref="Rules"/> (B3) and <see cref="Check"/> (B4), the planner's input (<see cref="BuildInput"/>)
/// and the readiness of the plan. The plan, the accumulated results and the undo of everything this window created are
/// in <c>BulkCreateViewModel.Plan.cs</c> (B4). Everything lives in memory of the window and is dropped on
/// <see cref="Dispose"/> (rule 1).
/// </summary>
public sealed partial class BulkCreateViewModel : ObservableObject, IDisposable
{
    private readonly Func<IDirectorySession?> _session;
    private readonly bool _initialized;
    private bool _revertingKind;
    private bool _disposed;

    /// <param name="services">The dialog services of the batch editor's window.</param>
    /// <param name="engines">The Core engines (<see cref="BatchEngines.BulkCreate"/>).</param>
    /// <param name="request">The kind and the pre-filled container.</param>
    /// <param name="session">The session the window was opened in (null once it ended; rule 4).</param>
    /// <param name="connector">The connector of the read-only global-catalog session (2.2); null = none.</param>
    public BulkCreateViewModel(
        DialogServices services,
        BatchEngines engines,
        BulkCreateRequest request,
        Func<IDirectorySession?> session,
        IDirectoryConnector? connector)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Engines = engines ?? throw new ArgumentNullException(nameof(engines));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Connector = connector;
        var localizer = services.Localizer;
        Kinds = [.. BulkCreateTextKeys.Kinds.Select(k => new Option<ObjectKind>(k, localizer[BulkCreateTextKeys.KindKey(k)]))];
        Kind = Kinds.FirstOrDefault(o => o.Value == request.Kind) ?? Kinds[0];
        ContainerDn = string.IsNullOrWhiteSpace(request.ParentDn) ? null : request.ParentDn;

        Source = new BulkSourceViewModel(services);
        Columns = new BulkColumnsViewModel(services, Source, _session, Kind.Value);
        Check = new BulkCheckViewModel(services, engines.BulkCreate, BuildInput, _session, connector);
        Rules = new BulkRulesViewModel(services, Columns, engines.BulkCreate, _session, () => ContainerDn, () => BuildInputCore(requireRows: false), () => Check.Lookups, Kind.Value);
        Source.Changed += OnSourceChanged;
        Source.TableReplaced += OnTableReplaced;
        Columns.Changed += OnStepChanged;
        Rules.Changed += OnStepChanged;
        Check.Changed += OnCheckChanged;
        localizer.CultureChanged += OnCultureChanged;
        _initialized = true;
    }

    /// <summary>The input or the readiness changed (the batch editor re-evaluates <c>PreviewCommand</c>).</summary>
    public event EventHandler? Changed;

    public DialogServices Services { get; }

    internal BatchEngines Engines { get; }

    public BulkCreateRequest Request { get; }

    internal IDirectoryConnector? Connector { get; }

    /// <summary>The session of the window (null once it ended).</summary>
    internal IDirectorySession? Session => _session();

    // ---- Kind and container -------------------------------------------------------------------------------

    /// <summary><em>Użytkownicy, Kontakty, Grupy, Komputery</em>.</summary>
    public IReadOnlyList<Option<ObjectKind>> Kinds { get; }

    /// <summary>
    /// The kind of the created objects. A change asks first when the naming rules were changed
    /// (<see cref="BulkRulesViewModel.IsModified"/>) and then gives the rules the kind's default profile.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ObjectKind), nameof(Title))]
    public partial Option<ObjectKind> Kind { get; set; }

    /// <summary>The kind as a value.</summary>
    public ObjectKind ObjectKind => Kind.Value;

    /// <summary>The one container every object is created in (decision 1); null until chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContainerName), nameof(HasContainer), nameof(Title))]
    [NotifyCanExecuteChangedFor(nameof(NextStepCommand))]
    public partial string? ContainerDn { get; private set; }

    public bool HasContainer => ContainerDn is not null;

    /// <summary>The container's name (its RDN value), or <em>Nie wybrano kontenera</em>.</summary>
    public string ContainerName => ContainerDn is { } dn && DistinguishedName.TryParse(dn, out var parsed) && parsed.Name.Length > 0
        ? parsed.Name
        : ContainerDn ?? Services.Localizer[L.BulkCreate.NoContainer];

    /// <summary><em>Zakładanie wielu kont</em> (per kind) with the container's name – the batch editor's window title in create mode.</summary>
    public string Title => ContainerDn is null
        ? Services.Localizer[BulkCreateTextKeys.TitleKey(ObjectKind)]
        : Services.Localizer.Format(L.BulkCreate.TitleInContainer, Services.Localizer[BulkCreateTextKeys.TitleKey(ObjectKind)], ContainerName);

    // ---- Steps --------------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceStep), nameof(IsColumnsStep), nameof(IsRulesStep), nameof(IsCheckStep), nameof(CanGoNext), nameof(IsReadyToPlan))]
    [NotifyCanExecuteChangedFor(nameof(NextStepCommand), nameof(PreviousStepCommand))]
    public partial BulkCreateStep Step { get; private set; } = BulkCreateStep.Source;

    public bool IsSourceStep => Step == BulkCreateStep.Source;

    public bool IsColumnsStep => Step == BulkCreateStep.Columns;

    public bool IsRulesStep => Step == BulkCreateStep.Rules;

    public bool IsCheckStep => Step == BulkCreateStep.Check;

    /// <summary>Step 1 <em>Dane</em> (B3).</summary>
    public BulkSourceViewModel Source { get; }

    /// <summary>Step 2 <em>Kolumny</em> (B3).</summary>
    public BulkColumnsViewModel Columns { get; }

    /// <summary>Step 3 <em>Nazwy i opcje</em> (B3).</summary>
    public BulkRulesViewModel Rules { get; }

    /// <summary>Step 4 <em>Sprawdzenie</em> (B4).</summary>
    public BulkCheckViewModel Check { get; }

    /// <summary>
    /// <em>Dalej</em> is possible: <em>Dane</em> → a container and at least one row; <em>Kolumny</em> → a usable mapping;
    /// <em>Nazwy i opcje</em> → valid rules and options; from <em>Sprawdzenie</em> the batch editor's Preview continues.
    /// </summary>
    public bool CanGoNext => !_disposed && Step switch
    {
        BulkCreateStep.Source => ContainerDn is not null && Source.Table.Rows.Count > 0 && !Source.IsBusy,
        BulkCreateStep.Columns => Columns.MappingError is null && Columns.Mappings.Count > 0,
        BulkCreateStep.Rules => Rules.Error is null,
        _ => false,
    };

    /// <summary>The rows the plan would cover at most: rows of the table that are neither excluded nor created (planning progress).</summary>
    public int IncludedRowCount
    {
        get
        {
            var excluded = Check.ExcludedRows;
            var created = CreatedRows;
            return Source.Table.Rows.Count(r => !excluded.Contains(r.Number) && !created.ContainsKey(r.Number));
        }
    }

    /// <summary>
    /// The batch editor's Preview is possible (<c>CanPreview</c> in create mode): the check step is reached, its computation
    /// has no run errors and at least one OK or warning row, nothing is computing, and the rules and options are valid.
    /// An empty shared-password box is not checked here: the plan step says <em>wpisz hasło ponownie</em>.
    /// </summary>
    public bool IsReadyToPlan =>
        !_disposed
        && Step == BulkCreateStep.Check
        && !Check.IsComputing
        && Check.Computation is { HasRunErrors: false } computation
        && computation.Count(BulkRowStatus.Ok) + computation.Count(BulkRowStatus.Warning) > 0
        && (_samplePlan is not null || Rules.Error is null); // a sample (smoke test, screenshot) brings its own plan

    /// <summary>
    /// The planner's input from the four steps and the window's state (edits, exclusions, pins, created rows, released
    /// values); null while a step is invalid (no container, no rows, a mapping or rule problem).
    /// </summary>
    public BulkCreateInput? BuildInput() => BuildInputCore(requireRows: true);

    /// <summary>
    /// The planner's input; with <paramref name="requireRows"/> false also for an empty table – the first free number of
    /// the <em>Numeracja</em> panel needs only the rules, so it is shown before any row is pasted.
    /// </summary>
    private BulkCreateInput? BuildInputCore(bool requireRows)
    {
        if (_disposed || ContainerDn is not { } container || (requireRows && Source.Table.Rows.Count == 0)
            || Columns.MappingError is not null || Rules.Error is not null)
        {
            return null;
        }

        return new BulkCreateInput
        {
            ContainerDn = container,
            Table = Source.Table,
            Mappings = Columns.ToMappings(),
            Rules = Rules.ToRules(),
            Options = Rules.ToOptions(),
            Edits = Check.Edits,
            ExcludedRows = Check.ExcludedRows,
            CreatedRows = CreatedRows,
            PinnedNumbers = Check.PinnedNumbers,
            ReleasedValues = ReleasedValues,
            UpnSuffixes = Rules.UpnSuffixes,
            Countries = Columns.CountryTable,
            MaxParallelLookups = Math.Max(1, Services.Settings.Current.BatchMaxParallelism),
        };
    }

    /// <summary>Loads what the steps need (UPN suffixes and the profile of step 3, the forest and the GC session of step 4).</summary>
    public async Task InitializeAsync()
    {
        await Rules.InitializeAsync().ConfigureAwait(true);
        await Check.InitializeAsync().ConfigureAwait(true);
    }

    /// <summary>The window's session ended: step 4 stops and closes its GC session.</summary>
    public void OnSessionEnded()
    {
        Rules.OnSessionEnded();
        Check.OnSessionEnded();
        NotifyStateChanged();
    }

    /// <summary><em>Zmień…</em>: picks the target container on the window's session.</summary>
    [RelayCommand]
    private async Task PickContainerAsync()
    {
        if (_session() is not { } session)
        {
            return;
        }

        var picked = await Services.Dialogs.PickContainerAsync(session, ContainerDn ?? session.Info.DefaultNamingContext).ConfigureAwait(true);
        if (picked is null || _disposed || DistinguishedName.AreEqual(picked, ContainerDn))
        {
            return;
        }

        ContainerDn = picked;
        Check.OnInputChanged();
        NotifyStateChanged();
    }

    /// <summary><em>Dalej</em>.</summary>
    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextStepAsync()
    {
        Step = Step + 1;
        if (Step == BulkCreateStep.Rules)
        {
            await Rules.OnEnteredAsync().ConfigureAwait(true);
        }
        else if (Step == BulkCreateStep.Check)
        {
            await Check.OnEnteredAsync().ConfigureAwait(true);
        }

        NotifyStateChanged();
    }

    /// <summary><em>Wstecz</em> (within the wizard; the batch editor's own Back returns from the preview).</summary>
    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousStep()
    {
        Step = Step - 1;
        NotifyStateChanged();
    }

    private bool CanGoPrevious() => !_disposed && Step > BulkCreateStep.Source;

    partial void OnKindChanged(Option<ObjectKind> oldValue, Option<ObjectKind> newValue)
    {
        if (!_initialized || _revertingKind || oldValue is null || oldValue.Value == newValue.Value)
        {
            return;
        }

        ApplyKindAsync(oldValue, newValue).Forget();
    }

    private async Task ApplyKindAsync(Option<ObjectKind> oldValue, Option<ObjectKind> newValue)
    {
        if (Rules.IsModified
            && !await Services.Dialogs.ConfirmAsync(Title, Services.Localizer[L.BulkCreate.ConfirmKindChange], destructive: true).ConfigureAwait(true))
        {
            _revertingKind = true;
            try
            {
                Kind = oldValue;
            }
            finally
            {
                _revertingKind = false;
            }

            return;
        }

        if (_disposed || Kind.Value != newValue.Value)
        {
            return;
        }

        Columns.OnKindChanged(newValue.Value);
        Rules.OnKindChanged(newValue.Value);
        Check.OnInputReset();
        NotifyStateChanged();
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        Check.OnInputChanged();
        NotifyStateChanged();
    }

    private void OnTableReplaced(object? sender, EventArgs e)
    {
        Check.OnInputReset();
        NotifyStateChanged();
    }

    private void OnStepChanged(object? sender, EventArgs e)
    {
        Check.OnInputChanged();
        NotifyStateChanged();
    }

    private void OnCheckChanged(object? sender, EventArgs e) => NotifyStateChanged();

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(ContainerName));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>Re-evaluates the navigation and the readiness and tells the batch editor.</summary>
    private void NotifyStateChanged()
    {
        if (_disposed)
        {
            return;
        }

        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsReadyToPlan));
        OnPropertyChanged(nameof(IncludedRowCount));
        NextStepCommand.NotifyCanExecuteChanged();
        PreviousStepCommand.NotifyCanExecuteChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops the table, the computation, the plans, the results and the passwords (window closed; rule 1, rule 2).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Services.Localizer.CultureChanged -= OnCultureChanged;
        Source.Changed -= OnSourceChanged;
        Source.TableReplaced -= OnTableReplaced;
        Columns.Changed -= OnStepChanged;
        Rules.Changed -= OnStepChanged;
        Check.Changed -= OnCheckChanged;
        Check.Dispose();
        Rules.Dispose();
        Columns.Dispose();
        Source.Dispose();
        OnDisposingPlan();
        Changed = null;
    }

    /// <summary>B4 drops the plan state (accumulated results, created objects, passwords) in <c>BulkCreateViewModel.Plan.cs</c>.</summary>
    partial void OnDisposingPlan();

    /// <summary>The kind, the step and the counts – never a value or the container's DN.</summary>
    public override string ToString() => $"Bulk create wizard: {ObjectKind}, {Step}, {Source.Table.Rows.Count} rows, container={(ContainerDn is not null)}";
}
