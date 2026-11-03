using System.Collections.ObjectModel;
using AdMng.Core.Directory;
using AdMng.Core.EventLogs;
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
public abstract partial class EventLogDocumentBase : ObservableObject, IShellDocument
{
    private CancellationTokenSource? _running;
    private bool _cancelRequested;
    private bool _disposed;

    protected EventLogDocumentBase(EventLogServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Services.Ui.Sessions.SessionChanged += OnSessionChanged;
        Services.Localizer.CultureChanged += OnLocalizerCultureChanged;
        NotSupportedText = Services.NotSupportedReason(Session);
    }

    public EventLogServices Services { get; }

    public abstract string Title { get; }

    public abstract ObjectIcon Icon { get; }

    /// <summary>The window's session (rule 4: directory reads go to its DC).</summary>
    public IDirectorySession? Session => Services.Ui.Sessions.Current;

    public bool HasSession => Session is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsLoading { get; protected set; }

    /// <summary>The progress of the running read ("Odczyt: 2 z 3 kontrolerów, 1 204 zdarzenia").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string ProgressText { get; protected set; } = string.Empty;

    /// <summary>The summary of the last run (or what to do first).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string LoadedText { get; protected set; } = string.Empty;

    /// <summary>A failure of the last run (localized), null when it worked.</summary>
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

    /// <summary>"Anuluj" was pressed during the running run.</summary>
    protected bool CancelRequested => _cancelRequested;

    /// <summary>F5: runs again.</summary>
    public abstract Task RefreshAsync();

    /// <summary>"Anuluj": stops every source of the running read (what was read stays).</summary>
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running?.Cancel();
        Services.Ui.Sessions.SessionChanged -= OnSessionChanged;
        Services.Localizer.CultureChanged -= OnLocalizerCultureChanged;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Runs <paramref name="work"/> as the document's one run: a running one is cancelled first; while it runs
    /// <see cref="IsLoading"/> is true and "Anuluj" stops it. Failures of the directory are shown as <see cref="Error"/>.
    /// </summary>
    protected async Task RunExclusiveAsync(Func<IDirectorySession, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_disposed)
        {
            return;
        }

        NotSupportedText = Services.NotSupportedReason(Session);
        if (Session is not { } session)
        {
            Error = Localizer[L.Errors.NotConnected];
            return;
        }

        if (IsNotSupported)
        {
            return;
        }

        _running?.Cancel();
        using var running = CancellationTokenSource.CreateLinkedTokenSource(Services.Ui.Connection?.Lifetime ?? CancellationToken.None);
        _running = running;
        _cancelRequested = false;
        IsLoading = true;
        Error = null;
        RefreshCommands();
        try
        {
            await work(session, running.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
            if (_cancelRequested && ReferenceEquals(_running, running))
            {
                LoadedText = Localizer[L.EventLogs.Cancelled];
            }
        }
        catch (Exception ex) when (ex is DirectoryException or InvalidOperationException or ObjectDisposedException or EventLogAccessException)
        {
            if (ReferenceEquals(_running, running))
            {
                Error = ex is EventLogAccessException access ? EventLogText.AccessError(Localizer, access.Error, access.NativeErrorCode) : Services.Ui.Errors.Translate(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_running, running))
            {
                _running = null;
                IsLoading = false;
                ProgressText = string.Empty;
                RefreshCommands();
            }
        }
    }

    /// <summary>Replaces the source rows with rows for <paramref name="hosts"/> (host, kind text) before a run.</summary>
    protected void ResetHosts(IEnumerable<(string Host, string KindText)> hosts)
    {
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

    /// <summary>"Odczyt: 2 z 3 źródeł, 1 204 zdarzenia" from the source rows.</summary>
    protected string ReadingProgress() => Localizer.Format(L.EventLogs.StatusReading,
        Hosts.Count(h => h.State is EventLogHostState.Done or EventLogHostState.Failed or EventLogHostState.Cancelled),
        Hosts.Count,
        Hosts.Sum(h => h.Records).ToString("N0", Localizer.Culture));

    /// <summary>Re-evaluates the commands (a run started or ended, the selection changed).</summary>
    protected virtual void RefreshCommands() => CancelCommand.NotifyCanExecuteChanged();

    /// <summary>The window's session changed (reconnect, "Zmień kontroler"): the support state is evaluated again.</summary>
    protected virtual void OnSessionChangedCore()
    {
    }

    /// <summary>The UI language changed: texts built while showing are rebuilt.</summary>
    protected virtual void OnCultureChanged() => OnPropertyChanged(nameof(Title));

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
}
