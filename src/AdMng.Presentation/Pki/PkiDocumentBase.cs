using System.ComponentModel;
using System.Security.Cryptography;
using AdMng.Core.Directory;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Icons;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Pki;

/// <summary>
/// Base of the PKI document tabs (docs/dev/p-certificate-services.md 2.5) – the Group Policy document base made LDAP-only:
/// no SYSVOL, no PDC note, no change event (P1 writes nothing). Loads on the window's current session through
/// <see cref="LoadCoreAsync"/>, reloads after a session change (another DC, a reconnect), rebuilds its texts after a language
/// switch and tells the document about settings changes (<see cref="OnSettingsChanged"/>: the health thresholds). F5
/// (<see cref="RefreshAsync"/>) reloads with <see cref="IsRefresh"/> set, so the document asks the snapshot service for a
/// fresh read. Everything it shows lives in memory only (rule 1).
/// </summary>
/// <remarks>
/// <para><b>What the document says</b> (track V, stage V2b-4, unit 3; docs/dev/v-interface-renewal.md "V2b-4 as built",
/// <i>Areas</i> 5). The four PKI documents say the same things in the same words, through one status line over
/// <see cref="Status"/> - the status bar of the main window says nothing about them (<c>ShellViewModel.StatusBarText</c>):</para>
/// <list type="bullet">
/// <item>the summary is <see cref="LoadedText"/>, with <see cref="SummaryDetail"/> (when and from where it was read) and
/// <see cref="SelectionText"/> joined by " · ";</item>
/// <item>the load is the operation of the document - "Reading “…”…" with Stop (<see cref="ReadingKey"/>); a document reports
/// the stages of its read through <see cref="ReportStage"/>: one message, a new count is not a new message;</item>
/// <item>what lasts for as long as the document shows what it shows is computed in one place
/// (<see cref="UpdateLasting"/>), the most important first: not connected, the load failed (with Retry), the load was
/// stopped (with the document's own refresh action), and then what a kind of document adds
/// (<see cref="DocumentLastingState"/>).</item>
/// </list>
/// <para>A part of a document that says something of its own (a tab with an operation of its own) has a holder and is put in
/// front while it is shown (<see cref="PageStatusHolder.Front"/>). A log copy of any of it is a resource key and counts - never
/// a certificate, a template or a CA name.</para>
/// </remarks>
public abstract partial class PkiDocumentBase : ObservableObject, IShellDocument, IHasPageStatus
{
    private bool _disposed;

    /// <summary>Why the document cannot show its content (the load failed), null when it can.</summary>
    private Problem? _problem;

    /// <summary>The last load was stopped: the document may not show everything until it is read again.</summary>
    private bool _stopped;

    /// <summary>The operation of the running load (null when none runs).</summary>
    private OperationScope? _loadOperation;

    protected PkiDocumentBase(PkiServices pki)
    {
        Pki = pki ?? throw new ArgumentNullException(nameof(pki));
        Status = new PageStatusHolder(Pki.Localizer, Pki.Dispatcher, Pki.Ui.Lists.StatusLog);
        Pki.Sessions.SessionChanged += OnSessionChanged;
        Pki.Localizer.CultureChanged += OnLocalizerCultureChanged;
        Pki.Settings.Changed += OnSettingsStoreChanged;
    }

    /// <summary>The window's PKI services.</summary>
    public PkiServices Pki { get; }

    /// <summary>What the document says in its status line: its summary, its load and the state that lasts (see the remarks of the class).</summary>
    public PageStatusHolder Status { get; }

    public abstract string Title { get; }

    public abstract ObjectIcon Icon { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsLoading { get; protected set; }

    /// <summary>The stage the running read is at, as the status line says it ("Reading “…” – AD CS objects: 1 204…"); empty when nothing runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string ProgressText { get; protected set; } = string.Empty;

    public virtual string StatusText => Error ?? (IsLoading ? Localizer[L.Common.Loading] : LoadedText);

    public virtual string SelectionText => string.Empty;

    /// <summary>A failure of the last load (localized, never a value), null when it succeeded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasError))]
    public partial string? Error { get; protected set; }

    public bool HasError => Error is not null;

    /// <summary>The window is connected.</summary>
    public bool HasSession => Pki.Sessions.Current is not null;

    /// <summary>The status bar text after a successful load (counts only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string LoadedText { get; protected set; } = string.Empty;

    /// <summary>The documents only show what they read: nothing to save.</summary>
    public bool HasUnsavedChanges => false;

    /// <summary>Always true (no edits).</summary>
    public Task<bool> CanCloseAsync() => Task.FromResult(true);

    /// <summary>The cancellation of the running load (a newer load or <see cref="Dispose()"/> cancels it).</summary>
    protected SerialCancellation SerialCancellation { get; } = new();

    /// <summary>The running load was started by F5: <see cref="LoadCoreAsync"/> asks the snapshot service for a fresh read.</summary>
    protected bool IsRefresh { get; private set; }

    protected ILocalizer Localizer => Pki.Localizer;

    protected bool IsDisposed => _disposed;

    /// <summary>
    /// The title of the read as the status line says it, without the dots ("Reading “Public Key Services”"): the key of
    /// the operation, its text and the key of its log copy. A document whose read is a check overrides it.
    /// </summary>
    protected virtual string ReadingKey => L.PkiDocumentStatus.Reading;

    /// <summary>What was tried when the read failed ("Could not read “…”"), without a full stop.</summary>
    protected virtual string FailedKey => L.PkiDocumentStatus.ReadFailed;

    /// <summary>The sentence of a read that was stopped; {0} = <see cref="OperationTitle"/>, {1} = <see cref="RefreshLabel"/>.</summary>
    protected virtual string StoppedKey => L.PkiDocumentStatus.Stopped;

    /// <summary>The text of the document's own button that reads again - the action of a stopped read.</summary>
    protected virtual string RefreshLabel => Localizer[L.Pki.RefreshCommand];

    /// <summary>"Reading “Public Key Services”" - what the read is called on the line and in the sentences about it.</summary>
    protected string OperationTitle => Localizer.Format(ReadingKey, Title);

    /// <summary>
    /// What a kind of document adds to its summary between <see cref="LoadedText"/> and the selection: when and from where
    /// what it shows was read. Empty: nothing; a text equal to <see cref="LoadedText"/> is not said twice. A document raises
    /// <c>PropertyChanged</c> for <c>SummaryDetail</c> when it changes.
    /// </summary>
    protected virtual string SummaryDetail => string.Empty;

    /// <summary>
    /// The operation of the running load, or null: a document whose load has stages reports them through
    /// <see cref="ReportStage"/> - one operation for one piece of work.
    /// </summary>
    protected OperationScope? LoadOperation => _loadOperation is { IsEnded: false } operation ? operation : null;

    /// <summary>F5, and the "Refresh" of a stopped read and the "Retry" of a failed one: reads everything the document shows again (the snapshot included).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public Task RefreshAsync() => LoadAsync(refresh: true);

    /// <summary>Loads (or reloads from the window's cache) the document on the current session; never throws.</summary>
    public Task LoadAsync() => LoadAsync(refresh: false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SerialCancellation.Cancel();
        Status.Operation?.Dispose();
        Pki.Sessions.SessionChanged -= OnSessionChanged;
        Pki.Localizer.CultureChanged -= OnLocalizerCultureChanged;
        Pki.Settings.Changed -= OnSettingsStoreChanged;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Reads what the document shows on <paramref name="session"/> (through <see cref="PkiServices.Snapshot"/>; pass
    /// <see cref="IsRefresh"/> as its <c>refresh</c>). Runs on the UI thread between awaits; throws directory failures, which
    /// the base turns into <see cref="Error"/> and the state of the status line. The operator's Stop cancels the token: the
    /// read ends with <see cref="OperationCanceledException"/> and the base says that it was stopped.
    /// </summary>
    protected abstract Task LoadCoreAsync(IDirectorySession session, CancellationToken cancellationToken);

    /// <summary>Clears what the document shows when the window is not connected.</summary>
    protected virtual void OnNotConnected()
    {
    }

    /// <summary>Re-evaluates the commands after a load or a selection change.</summary>
    protected virtual void RefreshCommands()
    {
    }

    /// <summary>
    /// A load ended without the result it was after (the operator stopped it, or it failed): a document that shows what it
    /// had read so far (the rows that say "reading…") says what is missing now. Not called for a load that was replaced.
    /// </summary>
    protected virtual void OnLoadInterrupted()
    {
    }

    /// <summary>The settings changed (on the UI thread): documents re-evaluate what depends on them (the health thresholds).</summary>
    protected virtual void OnSettingsChanged()
    {
    }

    /// <summary>The UI language changed: texts built while loading are rebuilt by reloading (from the window's cache).</summary>
    protected virtual void OnCultureChanged()
    {
        OnPropertyChanged(nameof(Title));
        LoadAsync().Forget();
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>
    /// Says which stage the read is at ("AD CS objects: 1 204") as one message of the operation - the title of the operation
    /// stays, so a stage or a new count is not a new message - with the part done in the ring when it is known. The log copy is
    /// the key of the operation and the count.
    /// </summary>
    /// <param name="stageKey">The resource key of the stage (<c>PkiDoc.Status.Stage*</c>); {0} = the count, when there is one.</param>
    /// <param name="count">How many objects or accounts were read so far; null for a stage without a count.</param>
    /// <param name="progress">How far it is; indeterminate by default.</param>
    protected void ReportStage(string stageKey, long? count = null, PageProgress progress = default)
    {
        var stage = count is { } n ? Localizer.Format(stageKey, PkiDisplay.Count(Localizer, n)) : Localizer[stageKey];
        var text = Localizer.Format(L.PkiDocumentStatus.Stage, OperationTitle, stage);
        ProgressText = text;
        LoadOperation?.Report(text, progress, count is { } c ? new PageStatusLog(ReadingKey, c) : new PageStatusLog(ReadingKey));
    }

    /// <summary>
    /// Says the state of the document that lasts, the most important first: not connected, the load failed, the load was
    /// stopped, or what this kind of document adds (<see cref="DocumentLastingState"/>). The base calls it whenever one of
    /// its own conditions changes, a document whenever one of its own does; while an operation runs the line keeps "working"
    /// and shows the state afterwards.
    /// </summary>
    protected void UpdateLasting() => Status.SetLasting(LastingState());

    /// <summary>
    /// A state that lasts for this kind of document, behind the ones every document has: an attention or an error that is
    /// true for as long as the document shows what it shows, with at most one action. Null: none. Computed anew at every
    /// <see cref="UpdateLasting"/>, so it follows the language.
    /// </summary>
    protected virtual PageStatus? DocumentLastingState => null;

    /// <summary>
    /// The failures a load turns into <see cref="Error"/> (as <c>GroupPolicyText.IsExpected</c>): directory and session
    /// errors, file and access errors, timeouts, certificate / ASN.1 parsing (<see cref="CryptographicException"/>) and a
    /// resource formatted with the wrong arguments (<see cref="FormatException"/>) – anything else is a bug and escapes.
    /// </summary>
    protected static bool IsExpected(Exception exception) => exception is DirectoryException or SessionEndedException
        or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or TimeoutException
        or CryptographicException or FormatException;

    private async Task LoadAsync(bool refresh)
    {
        if (_disposed)
        {
            return;
        }

        var token = SerialCancellation.Next();
        var session = Pki.Sessions.Current;
        if (session is null)
        {
            // Next() cancelled a running load (the session ended under it): its finally skips the reset, so do it here. The
            // state first, then the operation: the line goes from "working" straight to "not connected".
            _stopped = false;
            _problem = null;
            Error = Localizer[L.Errors.NotConnected];
            OnNotConnected();
            UpdateLasting();
            EndLoadOperation(_loadOperation);
            IsLoading = false;
            IsRefresh = false;
            ProgressText = string.Empty;
            OnPropertyChanged(nameof(HasSession));
            RefreshCommands();
            return;
        }

        // A newer load replaces the running one on the line, as it does in the document.
        var operation = Status.Begin(Localizer.Format(L.PkiDocumentStatus.Working, OperationTitle), new PageStatusLog(ReadingKey));
        _loadOperation = operation;
        IsLoading = true;
        IsRefresh = refresh;
        ProgressText = Localizer.Format(L.PkiDocumentStatus.Working, OperationTitle);
        _stopped = false;
        SetProblem(null);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Pki.Lifetime, operation.Token);
            await LoadCoreAsync(session, linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load, or the window closed: nothing to say. Stopped by the operator - or taken off the
            // line by another operation of the document: the document may not show everything, and says so until it is
            // read again.
            if (!token.IsCancellationRequested && !Pki.Lifetime.IsCancellationRequested)
            {
                _stopped = true;
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (!token.IsCancellationRequested)
            {
                SetProblem(new Problem(Pki.Errors.Translate(ex), ResultCode.Of(ex)));
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
                IsRefresh = false;
                ProgressText = string.Empty;
                if (_stopped || _problem is not null)
                {
                    OnLoadInterrupted();
                }

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

    /// <summary>Why the document cannot show its content, or that it can again (null).</summary>
    private void SetProblem(Problem? problem)
    {
        _problem = problem;
        Error = problem?.Text;
        UpdateLasting();
    }

    private PageStatus? LastingState()
    {
        if (!HasSession)
        {
            return PageStatus.Error(Localizer[L.Errors.NotConnected], new PageStatusLog(L.Errors.NotConnected));
        }

        if (_problem is { } problem)
        {
            return PageStatus.Error(
                PageStatusText.Failure(Localizer.Format(FailedKey, Title), problem.Text, Localizer[L.Feedback.RetryNext], problem.Code),
                new PageStatusLog(FailedKey).WithCode(problem.Code),
                new PageStatusAction(Localizer[L.Common.Retry], RefreshCommand));
        }

        if (_stopped)
        {
            var label = RefreshLabel;
            return PageStatus.Attention(
                Localizer.Format(StoppedKey, OperationTitle, label),
                new PageStatusLog(StoppedKey),
                new PageStatusAction(label, RefreshCommand));
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

    /// <summary>
    /// The summary: what was read, when and from where where the kind of document says so, and the selection, each part that
    /// is there joined by " · ".
    /// </summary>
    private void UpdateIdle()
    {
        var parts = new[] { LoadedText, SummaryDetail, SelectionText }.Where(part => !string.IsNullOrEmpty(part)).Distinct(StringComparer.Ordinal);
        Status.SetIdle(string.Join(" · ", parts));
    }

    /// <summary>The summary of the status line follows <see cref="LoadedText"/>, <see cref="SummaryDetail"/> and <see cref="SelectionText"/>.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(LoadedText) or nameof(SelectionText) or nameof(SummaryDetail))
        {
            UpdateIdle();
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e) => _ = Pki.Dispatcher.RunAsync(() => LoadAsync().Forget());

    private void OnLocalizerCultureChanged(object? sender, EventArgs e) => _ = Pki.Dispatcher.RunAsync(() =>
    {
        if (_disposed)
        {
            return;
        }

        // What lasts is computed from the facts and is said again in the new language at once.
        UpdateLasting();
        UpdateIdle();
        OnCultureChanged();
    });

    private void OnSettingsStoreChanged(object? sender, EventArgs e) => _ = Pki.Dispatcher.RunAsync(() =>
    {
        if (!_disposed)
        {
            OnSettingsChanged();
        }
    });

    /// <summary>Why the document cannot show its content: the sentence on screen and the result code of the failed read.</summary>
    private sealed record Problem(string Text, ResultCode Code)
    {
        /// <summary>The code only - never the text.</summary>
        public override string ToString() => $"{nameof(Problem)}({Code})";
    }
}
