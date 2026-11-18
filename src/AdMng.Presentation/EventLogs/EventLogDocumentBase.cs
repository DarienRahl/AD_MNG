using System.Collections.ObjectModel;
using System.ComponentModel;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.EventLogs;

/// <summary>
/// Base of the event log document tabs (track E): the window's session, the "not supported" banner (not Windows, a
/// simple-bind window), one run at a time with "Anuluj" that stops every source, the per-source panel ("Zasięg i dostęp")
/// and the status texts. Everything read lives only in memory while the tab is open (rule 1); closing the tab cancels the run.
/// </summary>
/// <remarks>
/// <para>The three documents say the same things in the same words, through one status line over <see cref="Status"/>
/// (track V, stage V2b-4; design note docs/dev/v-interface-renewal.md "V2b-4 as built"):</para>
/// <list type="bullet">
/// <item>the summary of the last run (<see cref="LoadedText"/>, with the selection joined by " · ");</item>
/// <item>the run as the operation of the tab - what it is doing, the stage it is at and how far it is, with Stop (the button
/// that started it is its Cancel) - and its result: a run that read everything ends without a message (the summary says what
/// was read), one that could not read some sources ends as an attention that names them (why, for each, is in the panel of
/// the sources, a row of its own), a stopped one as an attention with "Refresh", a failed one as an error with "Retry";</item>
/// <item>the states that last, the most important first (<see cref="UpdateLasting"/>): not connected, the window cannot read
/// event logs (<see cref="NotSupportedText"/>), what the run found it cannot show (<see cref="ShowUnavailable"/>).</item>
/// </list>
/// <para>A log copy of any of it is a resource key and counts - never a source, an account or an event.</para>
/// </remarks>
public abstract partial class EventLogDocumentBase : ObservableObject, IShellDocument, IHasPageStatus
{
    private CancellationTokenSource? _running;
    private OperationScope? _operation;
    private RunText? _run;
    private bool _hostsReset;
    private long? _partialEvents;
    private Problem? _problem;
    private bool _disposed;

    protected EventLogDocumentBase(EventLogServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Status = new PageStatusHolder(Services.Localizer, Services.Ui.Dispatcher, Services.Ui.Lists.StatusLog);
        Status.PropertyChanged += OnStatusChanged;
        RefreshCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(
            () => RefreshAsync(), CanRefresh, CommunityToolkit.Mvvm.Input.AsyncRelayCommandOptions.AllowConcurrentExecutions);
        Services.Ui.Sessions.SessionChanged += OnSessionChanged;
        Services.Localizer.CultureChanged += OnLocalizerCultureChanged;
        NotSupportedText = Services.NotSupportedReason(Session);
        UpdateLasting();
    }

    public EventLogServices Services { get; }

    /// <summary>What the tab says in its status line (see the remarks of the class).</summary>
    public PageStatusHolder Status { get; }

    /// <summary>
    /// Reads again: the "Refresh" of a stopped run and the "Retry" of a failed one (the shell's F5 is
    /// <see cref="RefreshAsync"/>).
    /// </summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand RefreshCommand { get; }

    public abstract string Title { get; }

    public abstract ObjectIcon Icon { get; }

    /// <summary>The window's session (rule 4: directory reads go to its DC).</summary>
    public IDirectorySession? Session => Services.Ui.Sessions.Current;

    public bool HasSession => Session is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsLoading { get; protected set; }

    /// <summary>The stage the running read is at, as the status line says it ("… – sources: 2 of 3, events: 1 204…").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string ProgressText { get; protected set; } = string.Empty;

    /// <summary>The summary of the last run (or what to do first).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string LoadedText { get; protected set; } = string.Empty;

    /// <summary>A failure of the last run (localized), null when it worked. The status line says it as an error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasError))]
    public partial string? Error { get; protected set; }

    public bool HasError => Error is not null;

    /// <summary>Why nothing can be read in this window (the banner), null when reading is possible.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotSupported), nameof(IsSupported))]
    public partial string? NotSupportedText { get; private set; }

    public bool IsNotSupported => NotSupportedText is not null;

    public bool IsSupported => NotSupportedText is null;

    public virtual string StatusText => Error ?? (IsLoading ? ProgressText : LoadedText);

    public virtual string SelectionText => string.Empty;

    /// <summary>The state, coverage and access of every source of the last run.</summary>
    public ObservableCollection<EventLogHostRow> Hosts { get; } = [];

    /// <summary>A source failed or reaches too little back (the panel opens itself).</summary>
    public bool HasHostProblems => Hosts.Any(h => h.HasError || h.HasWarning);

    protected ILocalizer Localizer => Services.Localizer;

    protected TimeZoneInfo Zone => Services.Zone;

    protected IUiDispatcher Dispatcher => Services.Ui.Dispatcher;

    protected bool IsDisposed => _disposed;

    /// <summary>The token of the running run (none: not cancelled).</summary>
    protected CancellationToken RunToken => _running?.Token ?? CancellationToken.None;

    /// <summary>"Stop" was pressed during the running run.</summary>
    protected bool CancelRequested => _operation is { IsCancelling: true };

    /// <summary>The operation of the running run, or null: a document reports the stages of its run through it.</summary>
    protected OperationScope? RunOperation => _operation is { IsEnded: false } operation ? operation : null;

    /// <summary>The running run is the one that started with <paramref name="key"/> (the action button of that run follows it).</summary>
    protected bool IsRunning(string key) => Status.IsWorking && _run?.Key == key;

    /// <summary>F5: runs again.</summary>
    public abstract Task RefreshAsync();

    /// <summary>"Zatrzymaj": stops every source of the running read (what was read stays) - the same Stop as the one of the status line.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel() => _operation?.Cancel();

    private bool CanCancel() => IsLoading && _running is not null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running?.Cancel();
        Status.Operation?.Dispose();
        Status.PropertyChanged -= OnStatusChanged;
        Services.Ui.Sessions.SessionChanged -= OnSessionChanged;
        Services.Localizer.CultureChanged -= OnLocalizerCultureChanged;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Runs <paramref name="work"/> as the document's one run: a running one is cancelled first; while it runs
    /// <see cref="IsLoading"/> is true, the status line says <paramref name="text"/> and the stage the work reports
    /// (<see cref="ReportStage"/>, <see cref="ReportSources"/>) with Stop. The work may hand back a result to say (done);
    /// otherwise the line says what the run left (see the remarks of the class) or the summary. Failures of the directory
    /// and of the event logs are shown as <see cref="Error"/> and as an error with Retry.
    /// </summary>
    protected Task RunExclusiveAsync(Func<IDirectorySession, CancellationToken, Task> work, RunText text)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunExclusiveWithResultAsync(
            async (session, token) =>
            {
                await work(session, token).ConfigureAwait(true);
                return null;
            },
            text);
    }

    /// <inheritdoc cref="RunExclusiveAsync(Func{IDirectorySession, CancellationToken, Task}, RunText)"/>
    protected async Task RunExclusiveWithResultAsync(Func<IDirectorySession, CancellationToken, Task<PageStatus?>> work, RunText text)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(text);
        if (_disposed)
        {
            return;
        }

        NotSupportedText = Services.NotSupportedReason(Session);
        if (Session is not { } session)
        {
            // The state of the tab says it (not connected); the sentence stays for what reads it.
            Error = Localizer[L.Errors.NotConnected];
            UpdateLasting();
            return;
        }

        if (IsNotSupported)
        {
            UpdateLasting();
            return;
        }

        // A newer run replaces the running one, on the line as in the tab.
        _running?.Cancel();
        var working = Localizer.Format(L.EventLogsStatus.Working, text.Title(Localizer));
        _run = text;
        var operation = Status.Begin(working, new PageStatusLog(text.Key));
        using var running = CancellationTokenSource.CreateLinkedTokenSource(Services.Ui.Connection?.Lifetime ?? CancellationToken.None, operation.Token);
        _running = running;
        _operation = operation;
        _hostsReset = false;
        _partialEvents = null;
        SetProblem(null);
        IsLoading = true;
        ProgressText = working;
        Error = null;
        RefreshCommands();
        PageStatus? result = null;
        try
        {
            result = await work(session, running.Token).ConfigureAwait(true);
            if (result is null && ReferenceEquals(_running, running) && !operation.IsEnded)
            {
                result = _partialEvents is { } events ? Stopped(text, events) : NotReadStatus(text);
            }
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
            // Replaced by a newer run, the session changed or the window closed: nothing to say. Stopped by the operator:
            // what is shown was not changed, and the line says so.
            if (ReferenceEquals(_running, running) && operation.IsCancelling)
            {
                result = Stopped(text, null);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // What the work is known to throw - the directory, the event log - and anything else (a record that cannot be
            // parsed, an I/O failure): the error of the run, with Retry. Never an exception that leaves the method with the
            // operation open: the line would say "working" for good, with a Stop of a run that no longer exists.
            if (ReferenceEquals(_running, running))
            {
                Error = ex is EventLogAccessException access ? EventLogText.AccessError(Localizer, access.Error, access.NativeErrorCode) : Services.Ui.Errors.Translate(ex);
                var code = ex is EventLogAccessException { NativeErrorCode: { } native } ? ResultCode.Win32(native) : ResultCode.Of(ex);
                result = PageStatus.Error(
                    PageStatusText.Failure(text.FailedTitle(Localizer), Error, Localizer[L.Feedback.RetryNext], code),
                    new PageStatusLog(text.FailedKey).WithCode(code),
                    new PageStatusAction(Localizer[L.Common.Retry], RefreshCommand));
            }
        }
        finally
        {
            if (ReferenceEquals(_running, running))
            {
                // The state first, then the operation: the line goes from "working" to what the run left.
                _running = null;
                IsLoading = false;
                ProgressText = string.Empty;
                UpdateLasting();
                RefreshCommands();
            }

            if (ReferenceEquals(_operation, operation))
            {
                _operation = null;
            }

            // On every way out: an operation that is begun is ended (a replaced one ignores what it is told).
            operation.Complete(result);
            operation.Dispose();
        }
    }

    /// <summary>
    /// The run found that the tab cannot show what it was asked for (the account is not an account, the window is
    /// empty): the tab says so as the state that lasts until the next run, as an error without an action. Called from the
    /// work of a run, which then returns.
    /// </summary>
    /// <param name="key">The resource key of the sentence; it is also the copy for the diagnostic log.</param>
    protected void ShowUnavailable(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Error = Localizer[key];
        SetProblem(new Problem(Error, key));
    }

    /// <summary>
    /// Says that something the operator started in the tab (not a run) failed: an error that can be closed - or, while a run
    /// is on the line, which keeps it, a toast (what finished elsewhere is a toast). Never dropped.
    /// </summary>
    /// <param name="failedKey">The resource key of what was tried, without a full stop.</param>
    /// <param name="exception">What went wrong.</param>
    /// <param name="args">What the sentence names.</param>
    protected void SayFailure(string failedKey, Exception exception, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var code = ResultCode.Of(exception);
        var text = PageStatusText.Failure(Localizer.Format(failedKey, args), Services.Ui.Errors.Translate(exception), null, code);
        if (Status.IsWorking)
        {
            Services.Ui.Notifications.Show(NotificationSeverity.Error, text);
            return;
        }

        Status.Show(PageStatus.Error(text, new PageStatusLog(failedKey).WithCode(code)));
    }

    /// <summary>
    /// The run read only part of what was asked for - the operator stopped it and the runner kept what it had read: the
    /// line says how many events are shown. Called from the work of a run, which then returns.
    /// </summary>
    protected void MarkPartial(long events) => _partialEvents = events;

    /// <summary>Says which stage the run is at ("reading the account") - one message of the operation, not a new one.</summary>
    protected void ReportStage(string stageKey, PageProgress progress = default, long[]? counts = null, params object[] args) =>
        ReportStageText(Localizer.Format(stageKey, args), progress, counts);

    /// <summary>
    /// Says that the run reads the sources (<paramref name="stageKey"/>: "…sources: {0}, events: {1}"): how many of them are
    /// through and how many events were read so far, with the part done in the ring.
    /// </summary>
    protected void ReportSources(string stageKey)
    {
        var done = Hosts.Count(h => h.State is EventLogHostState.Done or EventLogHostState.Failed or EventLogHostState.Cancelled);
        var total = Hosts.Count;
        var events = Hosts.Sum(h => h.Records);
        ReportStageText(
            Localizer.Format(stageKey, PageStatusText.Progress(Localizer, done, total), events.ToString("N0", Localizer.Culture)),
            PageProgress.Of(done, total),
            [done, total, events]);
    }

    private void ReportStageText(string stage, PageProgress progress, long[]? counts)
    {
        if (_run is not { } run)
        {
            return;
        }

        var text = Localizer.Format(L.EventLogsStatus.Stage, run.Title(Localizer), stage);
        ProgressText = text;
        RunOperation?.Report(text, progress, new PageStatusLog(run.Key, counts ?? []));
    }

    private PageStatus Stopped(RunText text, long? events)
    {
        var refresh = Localizer[L.Commands.Refresh];
        var action = new PageStatusAction(refresh, RefreshCommand);
        return events is { } read
            ? PageStatus.Attention(
                Localizer.Format(L.EventLogsStatus.StoppedPartial, text.Title(Localizer), read.ToString("N0", Localizer.Culture), refresh),
                new PageStatusLog(L.EventLogsStatus.StoppedPartial, read),
                action)
            : PageStatus.Attention(
                Localizer.Format(L.EventLogsStatus.Stopped, text.Title(Localizer), refresh),
                new PageStatusLog(L.EventLogsStatus.Stopped),
                action);
    }

    /// <summary>
    /// A run that read the sources and could not read some of them: what was skipped, which, and where the reason of each
    /// is (the panel of the sources, one row each). Null when every source was read, or the run read no sources.
    /// </summary>
    private PageStatus? NotReadStatus(RunText text)
    {
        if (!_hostsReset || Hosts.Where(h => h.State == EventLogHostState.Failed).ToList() is not { Count: > 0 } failed)
        {
            return null;
        }

        const int Named = 3;
        var names = string.Join(", ", failed.Take(Named).Select(h => h.Host)) + (failed.Count > Named ? ", …" : string.Empty);
        return PageStatus.Attention(
            Localizer.Format(
                L.EventLogsStatus.NotRead,
                text.Title(Localizer),
                PageStatusText.Progress(Localizer, failed.Count, Hosts.Count),
                names,
                Localizer[L.EventLogs.HostsHeading]),
            new PageStatusLog(L.EventLogsStatus.NotRead, failed.Count, Hosts.Count));
    }

    /// <summary>Replaces the source rows with rows for <paramref name="hosts"/> (host, kind text) before a run.</summary>
    protected void ResetHosts(IEnumerable<(string Host, string KindText)> hosts)
    {
        _hostsReset = true;
        Hosts.Clear();
        foreach (var (host, kind) in hosts)
        {
            Hosts.Add(new EventLogHostRow(host, kind, Localizer, Zone));
        }

        OnPropertyChanged(nameof(HasHostProblems));
    }

    /// <summary>A progress whose reports update the source rows on the UI thread (<paramref name="from"/>: the window start).</summary>
    protected IProgress<EventLogHostProgress> HostProgress(DateTimeOffset? from, Action? changed = null) => Dispatcher.CreateProgress<EventLogHostProgress>(p =>
    {
        if (_disposed)
        {
            return;
        }

        if (Hosts.FirstOrDefault(h => string.Equals(h.Host, p.Host, StringComparison.OrdinalIgnoreCase)) is { } row)
        {
            row.Update(p, from, Services.Ui.Time.GetUtcNow());
            OnPropertyChanged(nameof(HasHostProblems));
        }

        changed?.Invoke();
    });

    /// <summary>Re-evaluates the commands (a run started or ended, the selection changed).</summary>
    protected virtual void RefreshCommands()
    {
        CancelCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The running run changed: a document with several start buttons raises the flag of each (<see cref="IsRunning"/>).</summary>
    protected virtual void OnRunningChanged()
    {
    }

    /// <summary>The window's session changed (reconnect, "Zmień kontroler"): the support state is evaluated again.</summary>
    protected virtual void OnSessionChangedCore()
    {
    }

    /// <summary>The UI language changed: texts built while showing are rebuilt.</summary>
    protected virtual void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Title));
        UpdateIdle();
        UpdateLasting();
    }

    /// <summary>
    /// A state of the tab that lasts, behind the ones every document has (not connected, not supported, what the run
    /// found it cannot show): an attention or an error that is true for as long as the tab shows what it shows, with at most
    /// one action. Null: none. Computed anew at every <see cref="UpdateLasting"/>.
    /// </summary>
    protected virtual PageStatus? DocumentLastingState => null;

    /// <summary>
    /// Says the state of the tab that lasts, the most important first. Called whenever one of its conditions changes; while
    /// a run is on the line it shows the state afterwards.
    /// </summary>
    protected void UpdateLasting() => Status.SetLasting(LastingState());

    private PageStatus? LastingState()
    {
        if (!HasSession)
        {
            return PageStatus.Error(Localizer[L.Errors.NotConnected], new PageStatusLog(L.Errors.NotConnected));
        }

        if (Services.NotSupportedKey(Session) is { } key)
        {
            return PageStatus.Attention(Localizer[key], new PageStatusLog(key));
        }

        return _problem is { } problem
            ? PageStatus.Error(problem.Text, new PageStatusLog(problem.LogKey))
            : DocumentLastingState;
    }

    private void SetProblem(Problem? problem)
    {
        _problem = problem;
        UpdateLasting();
    }

    /// <summary>The summary: what the last run read, then the selection ("1 204 events from 3 sources · 4625 · dc01 · …").</summary>
    private void UpdateIdle() =>
        Status.SetIdle(string.Join(" · ", new[] { LoadedText, SelectionText }.Where(part => !string.IsNullOrEmpty(part)).Distinct(StringComparer.Ordinal)));

    /// <summary>The summary of the line follows what the last run read and the selection.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(LoadedText) or nameof(SelectionText))
        {
            UpdateIdle();
        }
    }

    private bool CanRefresh() => IsSupported && HasSession && !IsLoading;

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageStatusHolder.IsWorking))
        {
            OnRunningChanged();
        }
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    private void OnSessionChanged(object? sender, EventArgs e) => _ = Dispatcher.RunAsync(() =>
    {
        if (_disposed)
        {
            return;
        }

        _running?.Cancel();
        NotSupportedText = Services.NotSupportedReason(Session);
        OnPropertyChanged(nameof(HasSession));
        OnSessionChangedCore();
        UpdateLasting();
        RefreshCommands();
    });

    private void OnLocalizerCultureChanged(object? sender, EventArgs e) => _ = Dispatcher.RunAsync(() =>
    {
        if (!_disposed)
        {
            NotSupportedText = Services.NotSupportedReason(Session);
            OnCultureChanged();
        }
    });

    partial void OnIsLoadingChanged(bool value) => RefreshCommands();

    /// <summary>
    /// What a run is called on the line: its title ("Reading the event logs"; it is the key of its log copy) and what is said
    /// when it fails ("Could not read the event logs"), each made from a resource key and the arguments the sentence names.
    /// </summary>
    /// <param name="Key">The resource key of the title; the diagnostic log gets the key, never the text.</param>
    /// <param name="FailedKey">The resource key of what was tried, without a full stop.</param>
    /// <param name="Args">What the sentences name (the account, the computer).</param>
    protected sealed record RunText(string Key, string FailedKey, params object[] Args)
    {
        public string Title(ILocalizer localizer) => localizer.Format(Key, Args);

        public string FailedTitle(ILocalizer localizer) => localizer.Format(FailedKey, Args);

        public override string ToString() => Key;
    }

    /// <summary>Why the tab cannot show what it was asked for: the sentence on screen and the key that goes to the log instead.</summary>
    private sealed record Problem(string Text, string LogKey)
    {
        public override string ToString() => LogKey;
    }
}
