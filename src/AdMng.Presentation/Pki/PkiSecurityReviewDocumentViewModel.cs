using AdMng.Core.Directory;
using AdMng.Core.Pki;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Pki;

/// <summary>
/// <i>Przegląd bezpieczeństwa AD CS</i> (P1, docs/dev/p-certificate-services.md 2.4, 2.5; modelled on K6's
/// <c>GroupPolicyDiagnosticsDocumentViewModel</c>): one tab per connection window. The load takes the snapshot and the
/// enrollment expansion from the window's <see cref="IPkiSnapshotService"/>, builds the <see cref="PrivilegeScanContext"/>
/// from the session and the expansion's graph and options, and runs <see cref="PkiEscReader.ReadAsync"/> +
/// <see cref="PkiEscAnalyzer.Analyze"/> off the UI thread with progress and <i>Anuluj</i>. The findings are shown with summary
/// chips per severity, filters (severity, ESC, area, template, text – Ctrl+F), grouped by object, with the ESC number, the
/// Certipy / Locksmith / MDI ids, the condition, the attack path, the principals with their account counts and the notes; the
/// notes bar says what was assumed (decision 3), what could not be read and which rule groups were not evaluated. Read-only
/// (rule 5): no <i>Napraw</i> in P1 – the actions are <i>Otwórz szablon / urząd / konto</i>, <i>Kopiuj</i> and the CSV / HTML
/// exports to a file the operator picks (rule 1). Everything is read on the window's DC (rule 4) and kept in memory only.
/// </summary>
public sealed partial class PkiSecurityReviewDocumentViewModel : PkiDocumentBase, ISearchableDocument, IPkiRequestTarget
{
    private CancellationTokenSource? _running;
    private bool _cancelRequested;
    private ReviewResult? _review;

    /// <summary>The selection to restore after the next check (the key of the row, and its index as a fallback).</summary>
    private (string Key, int Index)? _restoreSelection;

    /// <param name="pki">The window's PKI services.</param>
    /// <param name="request">The request that opened the tab (<see cref="PkiDocumentRequest.FindingCode"/> sets the ESC filter).</param>
    public PkiSecurityReviewDocumentViewModel(PkiServices pki, PkiDocumentRequest? request = null)
        : base(pki)
    {
        SeverityOptions = [];
        EscOptions = [];
        AreaOptions = [];
        TemplateOptions = [];
        Chips = [];
        BuildFilterOptions();
        if (request?.FindingCode is { } code)
        {
            ShowCode(code);
        }

        LoadedText = Localizer[L.PkiSecurity.NeverRun];
        LastRunText = LoadedText;
        ScopeNote = Localizer[L.PkiSecurity.ScopeNote];
    }

    public override string Title => Localizer[L.PkiSecurity.Title];

    /// <summary>An already open review shows the ESC of a request's finding code (a request without one keeps the filters).</summary>
    public void Apply(PkiDocumentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FindingCode is { } code)
        {
            ShowCode(code);
        }
    }

    public override ObjectIcon Icon => ObjectIcon.Certificate;

    /// <summary>The progress while a check runs, else the summary of the last one.</summary>
    public override string StatusText => Error ?? (IsLoading ? ProgressText : LoadedText);

    public override string SelectionText => SelectedRow is { } row ? row.Title : string.Empty;

    // ---- Check state --------------------------------------------------------------------------------------------------------

    /// <summary>"Odczyt kont (1 500)…" while a check runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string ProgressText { get; private set; } = string.Empty;

    /// <summary>"Ostatnie sprawdzenie: 13.10.2026 12:00 na dc01.contoso.test" (or "Jeszcze nie sprawdzono.").</summary>
    [ObservableProperty]
    public partial string LastRunText { get; private set; } = string.Empty;

    /// <summary>What the review covers and assumes (decision 3; ESC6/7/8/10/11/16 are P4) – always shown.</summary>
    public string ScopeNote { get; private set; }

    /// <summary>What the shown check could not read or evaluate (the notices and the rule groups not evaluated).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial IReadOnlyList<string> Notes { get; private set; } = [];

    public bool HasNotes => Notes.Count > 0;

    /// <summary>A check result is shown (the exports and the chips have something to work on).</summary>
    public bool HasResult => _review is not null;

    /// <summary>The number of findings of the shown check (before the filters).</summary>
    public int TotalCount => _review?.Rows.Count ?? 0;

    /// <summary>The shown result (tests and the exports; in memory only), null before the first check.</summary>
    public PkiEscResult? Result => _review?.Result;

    // ---- Loading --------------------------------------------------------------------------------------------------------------

    protected override async Task LoadCoreAsync(IDirectorySession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_review is not null && SelectedRow is { } selected)
        {
            _restoreSelection ??= (selected.Key, IndexOf(Rows, selected));
        }

        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = running;
        _cancelRequested = false;
        RefreshCommands();
        var token = running.Token;
        var refresh = IsRefresh;
        try
        {
            ProgressText = Localizer.Format(L.PkiSecurity.StageSnapshot, 0);
            var snapshot = await Pki.Snapshot.GetAsync(refresh,
                Progress<PkiReadProgress>(running, p => ProgressText = Localizer.Format(L.PkiSecurity.StageSnapshot, p.Objects.ToString("N0", Localizer.Culture))),
                token).ConfigureAwait(true);
            ProgressText = Localizer.Format(L.PkiSecurity.StageAccounts, 0);
            var expansion = await Pki.Snapshot.GetExpansionAsync(refresh,
                Progress<PkiExpansionProgress>(running, p => ProgressText = Localizer.Format(L.PkiSecurity.StageAccounts, p.Accounts.ToString("N0", Localizer.Culture))),
                token).ConfigureAwait(true);
            ProgressText = Localizer[L.PkiSecurity.StageAccessControl];
            var context = new PrivilegeScanContext
            {
                Session = session,
                Groups = expansion.Groups,
                Options = expansion.Options,
                Now = Pki.Time.GetUtcNow(),
            };
            var escProgress = Progress<PkiEscProgress>(running, p => ProgressText = p.Stage switch
            {
                PkiEscStage.AccessControl => Localizer[L.PkiSecurity.StageAccessControl],
                PkiEscStage.Principals => Localizer[L.PkiSecurity.StagePrincipals],
                _ => Localizer[L.PkiSecurity.StageAnalysis],
            });
            var result = await Task.Run(async () =>
            {
                var read = await PkiEscReader.ReadAsync(context, snapshot, expansion, new PkiEscReadOptions(), escProgress, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return PkiEscAnalyzer.Analyze(read);
            }, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            Show(new ReviewResult(snapshot, result, Pki.Time.GetLocalNow(), session.Info.Server, Localizer));
        }
        catch (OperationCanceledException) when (_cancelRequested && !cancellationToken.IsCancellationRequested)
        {
            // "Anuluj": the shown results (if any) stay; the status says the check was cancelled.
            _restoreSelection = null;
            LoadedText = Localizer[L.PkiSecurity.Cancelled];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _restoreSelection = null;
            throw;
        }
        finally
        {
            if (ReferenceEquals(_running, running))
            {
                _running = null;
                ProgressText = string.Empty;
            }

            RefreshCommands();
        }
    }

    /// <summary>A progress that reports on the UI thread only while <paramref name="running"/> is the current check.</summary>
    private IProgress<T> Progress<T>(CancellationTokenSource running, Action<T> report) =>
        Pki.Dispatcher.CreateProgress<T>(p =>
        {
            if (ReferenceEquals(_running, running) && !running.IsCancellationRequested)
            {
                report(p);
            }
        });

    protected override void OnNotConnected()
    {
        _review = null;
        _all = [];
        Notes = [];
        ApplyFilters();
        UpdateSummary();
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(TotalCount));
    }

    /// <summary>The texts are rebuilt from the shown check in the new language (nothing is read again).</summary>
    protected override void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Title));
        if (!HasSession && Error is not null)
        {
            // The base would reload to say "not connected" in the new language; nothing is read here, so say it directly.
            Error = Localizer[L.Errors.NotConnected];
        }

        BuildFilterOptions();
        ScopeNote = Localizer[L.PkiSecurity.ScopeNote];
        OnPropertyChanged(nameof(ScopeNote));
        if (_review is { } review)
        {
            Show(review, keepSelection: true);
        }
        else
        {
            LoadedText = Localizer[L.PkiSecurity.NeverRun];
            LastRunText = LoadedText;
        }
    }

    protected override void RefreshCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OpenTemplateCommand.NotifyCanExecuteChanged();
        OpenCaCommand.NotifyCanExecuteChanged();
        OpenObjectCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"Anuluj": stops the running check (the shown results stay).</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        if (_running is { } running)
        {
            _cancelRequested = true;
            running.Cancel();
        }
    }

    private bool CanCancel() => IsLoading && _running is not null;

    /// <summary>
    /// Shows a check: rows, chips, filters, notes, the last-run line and the status text (also to rebuild the texts after a
    /// language change, <paramref name="keepSelection"/>).
    /// </summary>
    private void Show(ReviewResult review, bool keepSelection = false)
    {
        var restore = keepSelection && SelectedRow is { } selected ? (selected.Key, IndexOf(Rows, selected)) : _restoreSelection;
        _restoreSelection = null;
        review.Rebuild(Localizer);
        _review = review;
        _all = review.Rows;
        LastRunText = Localizer.Format(L.PkiSecurity.LastRun, review.At.ToString("g", Localizer.Culture), review.Server);
        Notes = NotesOf(review.Result, review.Texts);
        BuildTemplateOptions();
        UpdateSummary();
        ApplyFilters(restore);
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(TotalCount));
        RefreshCommands();
    }

    /// <summary>What the check could not read or evaluate, in the operator's words.</summary>
    private List<string> NotesOf(PkiEscResult result, PkiFindingRows rows)
    {
        var notes = new List<string>();
        foreach (var group in result.Notices.GroupBy(n => n.Code).OrderBy(g => g.Key))
        {
            var text = Localizer[PkiTextKeys.NoticeKey(group.Key)];
            var objects = group.Key is PkiReadNoticeCode.BaseNotFound or PkiReadNoticeCode.BaseAccessDenied ? string.Empty : rows.NoticeObjects(group);
            notes.Add(objects.Length > 0 ? Localizer.Format(L.PkiSecurity.NoteWithObjects, text, objects) : text);
        }

        if (result.NotEvaluated.Count > 0)
        {
            notes.Add(Localizer.Format(L.PkiSecurity.NoteNotEvaluated, string.Join("; ", result.NotEvaluated.Select(GroupText))));
        }

        return notes;
    }

    private string GroupText(PkiRuleGroup group) => Localizer[group switch
    {
        PkiRuleGroup.Enrollment => L.PkiSecurity.GroupEnrollment,
        PkiRuleGroup.AccessControl => L.PkiSecurity.GroupAccessControl,
        _ => L.PkiSecurity.GroupMapping,
    }];

    /// <summary>One check as shown: the snapshot and result (in memory only), when and on which DC it ran, and its rows.</summary>
    private sealed class ReviewResult(PkiSnapshot snapshot, PkiEscResult result, DateTimeOffset at, string server, ILocalizer localizer)
    {
        public PkiSnapshot Snapshot { get; } = snapshot;

        public PkiEscResult Result { get; } = result;

        public DateTimeOffset At { get; } = at;

        public string Server { get; } = server;

        /// <summary>The texts the rows were built with (the exports and <i>Kopiuj</i> use its headings).</summary>
        public PkiFindingRows Texts { get; private set; } = new(localizer, snapshot);

        public IReadOnlyList<PkiFindingRowViewModel> Rows { get; private set; } = [];

        /// <summary>The rows in the UI language (again after a language change).</summary>
        public void Rebuild(ILocalizer localizer)
        {
            Texts = new PkiFindingRows(localizer, Snapshot);
            Rows = Texts.Rows(Result);
        }

        /// <summary>For diagnostics: counts only.</summary>
        public override string ToString() => $"{nameof(ReviewResult)}({Result})";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _running?.Cancel();
            _review = null;
            _all = [];
        }

        base.Dispose(disposing);
    }
}
