using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Readiness;

/// <summary>
/// "Gotowość środowiska" (stage S, design note docs/dev/s-pilot-readiness.md 2): one tab per connection window
/// (<i>Narzędzia → Sprawdź gotowość środowiska…</i>, <i>Pomoc</i>, palette <c>tools.readiness</c>). "Szybkie sprawdzenie"
/// (the connection, the directory, this computer and the presence of Outlook; it runs when the tab opens) and "Pełne
/// sprawdzenie" (also SYSVOL, the event log of every DC, the test computer the operator types or picks, the Watchdog and
/// the Outlook profile) with progress and "Anuluj"; the results per area with their status, explanation, remedy and
/// documentation chapter; export to HTML or CSV into a file the operator picks (it names hosts – the document and the export
/// say so). Read-only (nothing is written anywhere – rule 5 is not involved), on the window's DC (rule 4), results in
/// memory only (rule 1); each finished run is published as code/status pairs to <see cref="IReadinessResults"/>.
/// </summary>
public sealed partial class ReadinessDocumentViewModel : ObservableObject, IShellDocument
{
    private readonly ReadinessServices _services;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _running;
    private ReadinessReport? _report;
    private string? _pickedDn;
    private string? _pickedName;
    private bool _disposed;

    public ReadinessDocumentViewModel(ReadinessServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        LastRunText = Localizer[L.Readiness.NeverRun];
        _services.Ui.Sessions.SessionChanged += OnSessionChanged;
        Localizer.CultureChanged += OnCultureChanged;
    }

    public string Title => Localizer[L.Readiness.Title];

    public ObjectIcon Icon => ObjectIcon.DomainController;

    /// <summary>A check runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(QuickCheckCommand), nameof(FullCheckCommand), nameof(CancelCommand), nameof(PickTestComputerCommand),
        nameof(ExportHtmlCommand), nameof(ExportCsvCommand), nameof(CopyCommand), nameof(CheckAgainCommand))]
    public partial bool IsLoading { get; private set; }

    /// <summary>The progress while a check runs, else the summary of the last one.</summary>
    public string StatusText => IsLoading ? ProgressText
        : SummaryText.Length > 0 ? SummaryText
        : HasSession ? LastRunText
        : Localizer[L.Errors.NotConnected];

    public string SelectionText => string.Empty;

    /// <summary>"Sprawdzanie: Dzienniki zdarzeń (4 z 8)".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string ProgressText { get; private set; } = string.Empty;

    /// <summary>The share of the areas done, in percent.</summary>
    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    /// <summary>"Ostatnie sprawdzenie: pełne, 3.11.2026 14:32, na dc01.contoso.test" (or "Jeszcze nie sprawdzono…").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string LastRunText { get; private set; } = string.Empty;

    /// <summary>"OK: 20 · Ostrzeżenia: 3 · Błędy: 1 · Nie sprawdzono: 9" of the last run (empty before the first one).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string SummaryText { get; private set; } = string.Empty;

    /// <summary>The areas of the last run with their rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyCanExecuteChangedFor(nameof(ExportHtmlCommand), nameof(ExportCsvCommand), nameof(CopyCommand))]
    public partial IReadOnlyList<ReadinessGroupViewModel> Groups { get; private set; } = [];

    /// <summary>A run is shown.</summary>
    public bool HasResult => Groups.Count > 0;

    /// <summary>The last run was cancelled (its unreached checks are not checked).</summary>
    [ObservableProperty]
    public partial bool WasCancelled { get; private set; }

    /// <summary>The window's connection changed after the shown run ("sprawdź ponownie").</summary>
    [ObservableProperty]
    public partial bool IsStale { get; private set; }

    /// <summary>The depth of the shown run (null before the first one).</summary>
    [ObservableProperty]
    public partial ReadinessDepth? LastDepth { get; private set; }

    /// <summary>The test computer of the full check: a name or DNS name typed, or the name of the computer picked from AD.</summary>
    [ObservableProperty]
    public partial string TestComputerText { get; set; } = string.Empty;

    /// <summary>The window is connected (the checks need its session).</summary>
    public bool HasSession => _services.Ui.Sessions.Current is not null;

    /// <summary>The finished run of this tab (in memory only), null before the first one.</summary>
    public ReadinessReport? Report => _report;

    /// <summary>The run in progress (tests await it), null when none runs.</summary>
    internal Task? Running { get; private set; }

    private ILocalizer Localizer => _services.Localizer;

    /// <summary>F5: the depth of the shown run again (quick before the first one).</summary>
    public Task RefreshAsync() => CheckAsync(LastDepth ?? ReadinessDepth.Quick);

    /// <summary>"Szybkie sprawdzenie".</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task QuickCheckAsync() => CheckAsync(ReadinessDepth.Quick);

    /// <summary>"Pełne sprawdzenie".</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task FullCheckAsync() => CheckAsync(ReadinessDepth.Full);

    /// <summary>"Sprawdź ponownie" of the stale bar: the depth of the shown run.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAgainAsync() => CheckAsync(LastDepth ?? ReadinessDepth.Quick);

    /// <summary>"Anuluj": stops the run; the checks it did not reach are shown as not checked.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel() => _running?.Cancel();

    private bool CanCheck() => !IsLoading && !_disposed && HasSession;

    private bool CanCancel() => IsLoading && _running is not null;

    /// <summary>"Wybierz…": a computer picked from AD becomes the test computer (its DN is used, its name shown).</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task PickTestComputerAsync()
    {
        if (_services.Ui.Sessions.Current is not { } session)
        {
            return;
        }

        var picked = await _services.Ui.Dialogs.PickObjectsAsync(session, [ObjectKind.Computer], multiSelect: false).ConfigureAwait(true);
        if (picked.Count == 0 || !DistinguishedName.TryParse(picked[0], out var dn) || dn.IsRoot)
        {
            return;
        }

        _pickedDn = picked[0];
        _pickedName = dn.Name;
        TestComputerText = dn.Name;
    }

    /// <summary>Runs a check of <paramref name="depth"/> on the window's session (nothing when one runs or the window is not connected).</summary>
    public Task CheckAsync(ReadinessDepth depth)
    {
        if (IsLoading || _disposed || _services.Ui.Sessions.Current is not { } session)
        {
            return Task.CompletedTask;
        }

        Running = RunAsync(session, depth);
        return Running;
    }

    private async Task RunAsync(IDirectorySession session, ReadinessDepth depth)
    {
        using var running = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _services.Lifetime);
        _running = running;
        IsLoading = true;
        ProgressValue = 0;
        var areas = Enum.GetValues<ReadinessArea>().Count(a => depth == ReadinessDepth.Full || ReadinessCodes.IsQuick(a));
        ProgressText = Localizer.Format(L.Readiness.Progress, Localizer[ReadinessText.AreaKey(ReadinessArea.Connection)], 1, areas);
        var request = new ReadinessRequest(depth, TestComputer());
        var progress = _services.Ui.Dispatcher.CreateProgress<ReadinessProgress>(p =>
        {
            if (ReferenceEquals(_running, running))
            {
                ProgressText = Localizer.Format(L.Readiness.Progress, Localizer[ReadinessText.AreaKey(p.Area)], p.Done + 1, p.Total);
                ProgressValue = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            }
        });
        try
        {
            var checker = new ReadinessChecker(_services);
            var report = await Task.Run(() => checker.RunAsync(session, request, progress, running.Token), CancellationToken.None).ConfigureAwait(true);
            if (!_disposed)
            {
                Show(report);

                // A run the change of the window's connection stopped shows the old connection's results: they are stale.
                IsStale = !ReferenceEquals(_services.Ui.Sessions.Current, session);
                _services.Results.Publish(report);
            }
        }
        finally
        {
            _running = null;
            ProgressText = string.Empty;
            ProgressValue = 0;
            IsLoading = false;
        }
    }

    /// <summary>The test computer of a full check: the picked DN while the text still shows its name, else the text.</summary>
    private string? TestComputer()
    {
        var text = TestComputerText.Trim();
        return _pickedDn is not null && string.Equals(text, _pickedName, StringComparison.Ordinal) ? _pickedDn : text.Length == 0 ? null : text;
    }

    private void Show(ReadinessReport report)
    {
        _report = report;
        LastDepth = report.Depth;
        IsStale = false;
        WasCancelled = report.Cancelled;
        Rebuild();
    }

    /// <summary>The groups and the texts of the shown run in the current language (the rows keep the texts they were checked with).</summary>
    private void Rebuild()
    {
        if (_report is not { } report)
        {
            LastRunText = Localizer[L.Readiness.NeverRun];
            return;
        }

        Groups =
        [
            .. report.Results.GroupBy(r => r.Area).OrderBy(g => g.Key)
                .Select(g => new ReadinessGroupViewModel(g.Key, [.. g.Select(r => new ReadinessRowViewModel(r, Localizer))], Localizer)),
        ];
        LastRunText = Localizer.Format(L.Readiness.LastRun, DepthText(report.Depth), report.At.ToString("g", Localizer.Culture), report.Server);
        SummaryText = Summary(Localizer, report.Results.Select(r => r.Status), all: true);
    }

    private string DepthText(ReadinessDepth depth) => Localizer[depth == ReadinessDepth.Full ? L.Readiness.DepthFull : L.Readiness.DepthQuick];

    /// <summary>"OK: 20 · Ostrzeżenia: 3 · Błędy: 1 · Nie sprawdzono: 9"; without <paramref name="all"/> only the statuses that occur.</summary>
    public static string Summary(ILocalizer localizer, IEnumerable<ReadinessStatus> statuses, bool all = false)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(statuses);
        var counts = statuses.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        if (all)
        {
            return localizer.Format(L.Readiness.Summary, counts.GetValueOrDefault(ReadinessStatus.Ok), counts.GetValueOrDefault(ReadinessStatus.Warning),
                counts.GetValueOrDefault(ReadinessStatus.Error), counts.GetValueOrDefault(ReadinessStatus.NotChecked));
        }

        var parts = new List<string>();
        foreach (var (status, key) in new[]
        {
            (ReadinessStatus.Error, L.Readiness.CountError), (ReadinessStatus.Warning, L.Readiness.CountWarning),
            (ReadinessStatus.Ok, L.Readiness.CountOk), (ReadinessStatus.NotChecked, L.Readiness.CountNotChecked),
        })
        {
            if (counts.GetValueOrDefault(status) is > 0 and var count)
            {
                parts.Add(localizer.Format(key, count));
            }
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"Kopiuj wyniki": every row as a line of text, by area.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private void Copy()
    {
        if (_services.Ui.Clipboard.SetText(ResultsText()))
        {
            _services.Ui.Notifications.Show(NotificationSeverity.Info, Localizer[L.Readiness.Copied]);
        }
    }

    /// <summary>The shown run as text (the copy).</summary>
    public string ResultsText()
    {
        var lines = new List<string> { Title, LastRunText, SummaryText };
        foreach (var group in Groups)
        {
            lines.Add(string.Empty);
            lines.Add($"{group.Title} – {group.StatusText}");
            lines.AddRange(group.Rows.Select(r => "  " + r.ToText()));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private bool CanExport() => !IsLoading && Groups.Count > 0;

    private void OnSessionChanged(object? sender, EventArgs e) => _ = _services.Ui.Dispatcher.RunAsync(() =>
    {
        if (_disposed)
        {
            return;
        }

        // A run on the old session is stopped; the shown results belong to the old connection.
        _running?.Cancel();
        IsStale = _report is not null;
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(StatusText));
        QuickCheckCommand.NotifyCanExecuteChanged();
        FullCheckCommand.NotifyCanExecuteChanged();
        CheckAgainCommand.NotifyCanExecuteChanged();
        PickTestComputerCommand.NotifyCanExecuteChanged();
    });

    private void OnCultureChanged(object? sender, EventArgs e) => _ = _services.Ui.Dispatcher.RunAsync(() =>
    {
        if (!_disposed)
        {
            OnPropertyChanged(nameof(Title));
            Rebuild();
        }
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.Ui.Sessions.SessionChanged -= OnSessionChanged;
        Localizer.CultureChanged -= OnCultureChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _report = null;
    }

    public override string ToString() => nameof(ReadinessDocumentViewModel);
}
