using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>
/// Base of the Group Policy document tabs (design note docs/dev/k-computers-and-gpo.md 5.2): loads on the window's
/// current session (reloads after "Zmień kontroler…", a reconnect or an executed GPO operation), says why SYSVOL is not
/// available ("SYSVOL niedostępny: …" with the reason - SYSVOL writes are then disabled, LDAP reads still shown) and
/// where changes are made when that is not the PDC emulator, with "Zmień kontroler…" (the window never switches DC by
/// itself, rule 4). Everything it shows lives in memory only.
/// </summary>
/// <remarks>
/// <para><b>What the document says</b> (track V, stage V2b-3; docs/dev/v-interface-renewal.md "V2b-3 as built", <i>Group
/// Policy documents</i>). Every document has one status line over <see cref="Status"/>, so the status bar of the main
/// window says nothing about it (<c>ShellViewModel.StatusBarText</c>):</para>
/// <list type="bullet">
/// <item>the summary is <see cref="LoadedText"/>, with <see cref="SummaryDetail"/> (when a kind of document says when
/// its content was read) and <see cref="SelectionText"/> joined by " · ";</item>
/// <item>the load is the operation of the document - "Reading “…”…" with Stop (<see cref="ReadingText"/>; a document
/// reports the steps of its load through <see cref="LoadOperation"/>);</item>
/// <item>what lasts for as long as the document shows what it shows is computed in one place
/// (<see cref="UpdateLasting"/>): not connected, the content cannot be shown (the load failed - with Retry -, or found
/// that the GPO or the container is gone: <see cref="ShowUnavailable"/>), the load was stopped (with Refresh), and then
/// what a kind of document adds (<see cref="DocumentLastingState"/>);</item>
/// <item>an operation the document starts through <see cref="Actions"/> says in the same line that its plan is being
/// made, and why nothing came of it.</item>
/// </list>
/// <para><see cref="SysvolWarning"/> and <see cref="PdcNote"/> are no status: they are true of the content for as long
/// as it is shown and stand above it as notes.</para>
/// </remarks>
public abstract partial class GroupPolicyDocumentBase : ObservableObject, IShellDocument, IHasPageStatus
{
    private readonly SerialCancellation _load = new();
    private bool _disposed;

    /// <summary>Why the document cannot show its content (what <see cref="Error"/> says), null when it can.</summary>
    private Problem? _problem;

    /// <summary>The last load was stopped: the document may not show everything until it is read again.</summary>
    private bool _stopped;

    /// <summary>The operation of the running load (null when none runs).</summary>
    private OperationScope? _loadOperation;

    protected GroupPolicyDocumentBase(GroupPolicyServices services, GroupPolicyActions actions)
    {
        Gp = services ?? throw new ArgumentNullException(nameof(services));
        ArgumentNullException.ThrowIfNull(actions);
        Status = new PageStatusHolder(Gp.Localizer, Gp.Dispatcher, Gp.Ui.Lists.StatusLog);

        // What an operation of this document has to say - its plan is being made, nothing was changed and why - it
        // says in the line of this document (the tree and the shell, which have none, keep the dialog and the toast).
        Actions = actions.For(Status);
        Gp.Changed += OnGroupPolicyChanged;
        Gp.Sessions.SessionChanged += OnSessionChanged;
        Gp.Localizer.CultureChanged += OnLocalizerCultureChanged;
    }

    /// <summary>The window's Group Policy services.</summary>
    public GroupPolicyServices Gp { get; }

    /// <summary>The operations (create, link, delete …), speaking through the status line of this document.</summary>
    public GroupPolicyActions Actions { get; }

    /// <summary>
    /// What the document says in its status line: its summary, its load, the operations it starts and the state that
    /// lasts (see the remarks of the class).
    /// </summary>
    public PageStatusHolder Status { get; }

    public abstract string Title { get; }

    public abstract ObjectIcon Icon { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsLoading { get; protected set; }

    public virtual string StatusText => Error ?? (IsLoading ? Localizer[L.Common.Loading] : LoadedText);

    public virtual string SelectionText => string.Empty;

    /// <summary>
    /// Why the document cannot show its content (localized): not connected, a failure of the last load, or what the
    /// load found (<see cref="ShowUnavailable"/>); null when it can. The status line says it as the state that lasts.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>"SYSVOL niedostępny: …" (null: SYSVOL of the session's DC is writable).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSysvolWarning), nameof(IsSysvolWritable))]
    public partial string? SysvolWarning { get; protected set; }

    public bool HasSysvolWarning => SysvolWarning is not null;

    /// <summary>SYSVOL writes (create, copy, delete, restore, permissions, versions) are possible.</summary>
    public bool IsSysvolWritable => SysvolWarning is null && !IsLoading && HasSession;

    /// <summary>"Zmiany GPO wykonujesz na DC X, a emulatorem PDC jest Y – GPMC domyślnie używa PDC" (null: on the PDC).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPdcNote))]
    public partial string? PdcNote { get; protected set; }

    public bool HasPdcNote => PdcNote is not null;

    /// <summary>The window is connected.</summary>
    public bool HasSession => Gp.Sessions.Current is not null;

    /// <summary>The status bar text after a successful load ("6 obiektów zasad grupy").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string LoadedText { get; protected set; } = string.Empty;

    protected ILocalizer Localizer => Gp.Localizer;

    protected bool IsDisposed => _disposed;

    /// <summary>What the status line says while the document is read: "Reading “Group Policy Objects”…".</summary>
    protected virtual string ReadingText => Localizer.Format(L.GroupPolicyStatus.Reading, Title);

    /// <summary>
    /// The operation of the running load, or null: a document whose load has steps reports them here
    /// (<see cref="OperationScope.Report"/>), so the line says them and there is one operation for one piece of work.
    /// </summary>
    protected OperationScope? LoadOperation => _loadOperation is { IsEnded: false } operation ? operation : null;

    /// <summary>
    /// What a kind of document adds to its summary between <see cref="LoadedText"/> and the selection: when what it shows
    /// was read ("Last check: full, 3.11.2026 14:32"). Empty: nothing; a text equal to <see cref="LoadedText"/> is not
    /// said twice. A document raises <c>PropertyChanged</c> for <c>SummaryDetail</c> when it changes.
    /// </summary>
    protected virtual string SummaryDetail => string.Empty;

    /// <summary>
    /// The tab holds edits that closing it would discard (the shell then asks through <see cref="CanCloseAsync"/>; the
    /// documents that only show what they read have none).
    /// </summary>
    public virtual bool HasUnsavedChanges => false;

    /// <summary>Asks before unsaved edits are discarded (Ctrl+W, the tab's close button, the main window). True when the tab may close.</summary>
    public virtual Task<bool> CanCloseAsync() => Task.FromResult(true);

    /// <summary>
    /// Reloads everything the document shows: the "Refresh" and "Retry" of its status line (the shell's F5 is
    /// <see cref="IShellDocument.RefreshAsync"/>). A document whose read can be stopped or fail overrides it to repeat that
    /// read.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public virtual Task RefreshAsync() => LoadAsync();

    /// <summary>
    /// Loads (or reloads) the document on the current session; never throws. The load is the operation of the document:
    /// the status line says "Reading “…”…" with Stop, and afterwards the summary or why the content is not there.
    /// </summary>
    public async Task LoadAsync()
    {
        if (_disposed)
        {
            return;
        }

        var token = _load.Next();
        var session = Gp.Sessions.Current;
        if (session is null)
        {
            _stopped = false;
            SetProblem(new Problem(Localizer[L.Errors.NotConnected], L.Errors.NotConnected));
            SysvolWarning = null;
            PdcNote = null;
            OnNotConnected();

            // The state first, then the operation: a load that still runs was superseded just now, and the line goes
            // from "working" straight to "not connected". "Busy" ends here too, not when the domain controller answers.
            EndLoadOperation(_loadOperation);
            IsLoading = false;
            OnPropertyChanged(nameof(IsSysvolWritable));
            OnPropertyChanged(nameof(HasSession));
            RefreshCommands();
            return;
        }

        // A newer load replaces the running one on the line, as it does in the document.
        var operation = Status.Begin(ReadingText, new PageStatusLog(L.GroupPolicyStatus.Reading));
        _loadOperation = operation;
        IsLoading = true;
        _stopped = false;
        SetProblem(null);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Gp.Lifetime, operation.Token);
            var access = await Gp.OpenSysvolAsync(session, linked.Token).ConfigureAwait(true);
            try
            {
                SysvolWarning = access.Connection is null ? Gp.SysvolText(access.Reason ?? SysvolUnavailableReason.Failed, session.Info.Server) : null;
                await LoadCoreAsync(session, access.Connection, linked.Token).ConfigureAwait(true);
            }
            finally
            {
                if (access.Connection is { } connection)
                {
                    await connection.DisposeAsync().ConfigureAwait(true);
                }
            }

            PdcNote = await Gp.PdcNoteAsync(session, linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load, or the window closed: nothing to say. Stopped by the operator - or taken off
            // the line by another operation of the document: the document may not show everything, and says so until
            // it is read again.
            if (!token.IsCancellationRequested && !Gp.Lifetime.IsCancellationRequested)
            {
                _stopped = true;
            }
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            if (!token.IsCancellationRequested)
            {
                SetProblem(new Problem(GroupPolicyText.Error(Localizer, Gp.Errors, ex), L.GroupPolicyStatus.ReadFailed, ResultCode.Of(ex), IsFailedRead: true));
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                // The state first, then the operation: the line goes from "working" to what the load left.
                UpdateLasting();
                EndLoadOperation(operation);
                IsLoading = false;
                OnPropertyChanged(nameof(IsSysvolWritable));
                OnPropertyChanged(nameof(HasSession));
                RefreshCommands();
            }
            else
            {
                // Superseded: the newer load owns the document and the line.
                EndLoadOperation(operation);
            }
        }
    }

    /// <summary>"Zmień kontroler…" of the PDC note (the existing Change DC dialog of the window).</summary>
    [RelayCommand]
    private Task ChangeDomainControllerAsync() => Gp.Ui.Dialogs.ShowChangeDomainControllerAsync();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _load.Cancel();
        Status.Operation?.Dispose();
        Gp.Changed -= OnGroupPolicyChanged;
        Gp.Sessions.SessionChanged -= OnSessionChanged;
        Gp.Localizer.CultureChanged -= OnLocalizerCultureChanged;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Reads what the document shows on <paramref name="session"/>; <paramref name="sysvol"/> is the open SYSVOL connection
    /// of the same DC, null when it is unavailable (<see cref="SysvolWarning"/> says why). Runs on the UI thread between
    /// awaits; throws the failures of <see cref="GroupPolicyText.IsExpected"/>.
    /// </summary>
    protected abstract Task LoadCoreAsync(IDirectorySession session, ISysvolWriteConnection? sysvol, CancellationToken cancellationToken);

    /// <summary>
    /// The load found that the document cannot show its content - the GPO or the container no longer exists: the
    /// document says so (<see cref="Error"/>) as the state that lasts until it is read again. Called from
    /// <see cref="LoadCoreAsync"/>, which then returns.
    /// </summary>
    /// <param name="key">The resource key of the sentence; it is also the copy for the diagnostic log.</param>
    /// <param name="action">The one thing the operator can do about it ("Delete the remaining folder"), if any.</param>
    protected void ShowUnavailable(string key, PageStatusAction? action = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        SetProblem(new Problem(Localizer[key], key, Action: action));
    }

    /// <summary>Clears what the document shows when the window is not connected.</summary>
    protected virtual void OnNotConnected()
    {
    }

    /// <summary>Re-evaluates the commands after a load or a selection change.</summary>
    protected virtual void RefreshCommands()
    {
    }

    /// <summary>
    /// <see cref="IsLoading"/> changed (a load started or ended): documents that edit what they show become read-only while
    /// a load runs (<see cref="IsSysvolWritable"/> is false meanwhile) and re-evaluate their commands here.
    /// </summary>
    protected virtual void OnLoadingChanged()
    {
    }

    /// <summary>The UI language changed: texts built while loading are rebuilt by reloading.</summary>
    protected virtual void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Title));
        LoadAsync().Forget();
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>Runs an operation and refreshes the commands (the reload follows <see cref="GroupPolicyServices.Changed"/>).</summary>
    protected async Task RunAsync(Func<Task<bool>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await operation().ConfigureAwait(true);
        RefreshCommands();
    }

    /// <summary>The summary of the status line follows <see cref="LoadedText"/>, <see cref="SummaryDetail"/> and <see cref="SelectionText"/>.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(LoadedText) or nameof(SelectionText) or nameof(SummaryDetail))
        {
            UpdateIdle();
        }
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSysvolWritable));
        OnLoadingChanged();
    }

    // ---- Status line (track V, stage V2b-3) ------------------------------------------------------------------------

    /// <summary>
    /// The summary: what was read, when it was read where the kind of document says so, and the selection ("6 group
    /// policy objects, … · 2 selected"), each part that is there joined by " · ".
    /// </summary>
    private void UpdateIdle()
    {
        var parts = new[] { LoadedText, SummaryDetail, SelectionText }.Where(part => !string.IsNullOrEmpty(part)).Distinct(StringComparer.Ordinal);
        Status.SetIdle(string.Join(" · ", parts));
    }

    /// <summary>Why the document cannot show its content, or that it can again (null).</summary>
    private void SetProblem(Problem? problem)
    {
        _problem = problem;
        Error = problem?.Text;
        UpdateLasting();
    }

    /// <summary>
    /// Says the state of the document that lasts, the most important first: it cannot show its content (not connected,
    /// the load failed, the GPO or the container is gone), its load was stopped, or what this kind of document adds
    /// (<see cref="DocumentLastingState"/>). The base calls it whenever one of its own conditions changes, a document
    /// whenever one of its own does; while an operation runs the line keeps "working" and shows the state afterwards.
    /// </summary>
    protected void UpdateLasting() => Status.SetLasting(LastingState());

    /// <summary>
    /// A state that lasts for this kind of document, behind the ones every document has: "Group Policy changed after
    /// these results were made", for example - an attention or an error that is true for as long as the document shows
    /// what it shows, with at most one action. Null: none. Computed anew at every <see cref="UpdateLasting"/>.
    /// </summary>
    protected virtual PageStatus? DocumentLastingState => null;

    private PageStatus? LastingState()
    {
        if (_problem is { } problem)
        {
            if (!problem.IsFailedRead)
            {
                return PageStatus.Error(problem.Text, new PageStatusLog(problem.LogKey), problem.Action);
            }

            return PageStatus.Error(
                PageStatusText.Failure(Localizer.Format(L.GroupPolicyStatus.ReadFailed, Title), problem.Text, Localizer[L.Feedback.RetryNext], problem.Code),
                new PageStatusLog(problem.LogKey).WithCode(problem.Code),
                new PageStatusAction(Localizer[L.Common.Retry], RefreshCommand));
        }

        if (_stopped)
        {
            return PageStatus.Attention(
                Localizer.Format(L.GroupPolicyStatus.Stopped, Title),
                new PageStatusLog(L.GroupPolicyStatus.Stopped),
                new PageStatusAction(Localizer[L.Commands.Refresh], RefreshCommand));
        }

        return DocumentLastingState;
    }

    /// <summary>Ends the operation of a load without a message: the line shows the state that lasts, or the summary.</summary>
    private void EndLoadOperation(OperationScope? operation)
    {
        if (operation is null)
        {
            return;
        }

        if (ReferenceEquals(_loadOperation, operation))
        {
            _loadOperation = null;
        }

        operation.Dispose();
    }

    private void OnGroupPolicyChanged(object? sender, EventArgs e) => _ = Gp.Dispatcher.RunAsync(() => LoadAsync().Forget());

    private void OnSessionChanged(object? sender, EventArgs e) => _ = Gp.Dispatcher.RunAsync(() => LoadAsync().Forget());

    private void OnLocalizerCultureChanged(object? sender, EventArgs e) => _ = Gp.Dispatcher.RunAsync(OnCultureChanged);

    /// <summary>Why the document cannot show its content.</summary>
    /// <param name="Text">The sentence on screen (<see cref="Error"/>).</param>
    /// <param name="LogKey">The resource key that goes to the diagnostic log instead of the text.</param>
    /// <param name="Code">The result code of a failed read.</param>
    /// <param name="Action">The one thing the operator can do about it, if any.</param>
    /// <param name="IsFailedRead">The load failed: the line says "Could not read “…”: reason" and offers Retry.</param>
    private sealed record Problem(string Text, string LogKey, ResultCode Code = default, PageStatusAction? Action = null, bool IsFailedRead = false)
    {
        /// <summary>The key and the code - never the text.</summary>
        public override string ToString() => $"{LogKey} [{Code}]";
    }
}
