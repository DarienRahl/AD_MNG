using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
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
/// current session (reloads after "Zmień kontroler…", a reconnect or an executed GPO operation), shows the warning bar
/// ("SYSVOL niedostępny: …" with the reason - SYSVOL writes are then disabled, LDAP reads still shown) and the PDC note
/// with "Zmień kontroler…" (the window never switches DC by itself, rule 4). Everything it shows lives in memory only.
/// </summary>
public abstract partial class GroupPolicyDocumentBase : ObservableObject, IShellDocument
{
    private readonly SerialCancellation _load = new();
    private bool _disposed;

    protected GroupPolicyDocumentBase(GroupPolicyServices services, GroupPolicyActions actions)
    {
        Gp = services ?? throw new ArgumentNullException(nameof(services));
        Actions = actions ?? throw new ArgumentNullException(nameof(actions));
        Gp.Changed += OnGroupPolicyChanged;
        Gp.Sessions.SessionChanged += OnSessionChanged;
        Gp.Localizer.CultureChanged += OnLocalizerCultureChanged;
    }

    /// <summary>The window's Group Policy services.</summary>
    public GroupPolicyServices Gp { get; }

    /// <summary>The operations (create, link, delete …).</summary>
    public GroupPolicyActions Actions { get; }

    public abstract string Title { get; }

    public abstract ObjectIcon Icon { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsLoading { get; protected set; }

    public virtual string StatusText => Error ?? (IsLoading ? Localizer[L.Common.Loading] : LoadedText);

    public virtual string SelectionText => string.Empty;

    /// <summary>A failure of the last load (localized), null when it succeeded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasError))]
    public partial string? Error { get; protected set; }

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

    /// <summary>F5: reloads everything the document shows.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public Task RefreshAsync() => LoadAsync();

    /// <summary>Loads (or reloads) the document on the current session; never throws.</summary>
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
            Error = Localizer[L.Errors.NotConnected];
            SysvolWarning = null;
            PdcNote = null;
            OnNotConnected();
            return;
        }

        IsLoading = true;
        Error = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Gp.Lifetime);
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
            // Superseded by a newer load, or the window closed.
        }
        catch (Exception ex) when (GroupPolicyText.IsExpected(ex))
        {
            if (!token.IsCancellationRequested)
            {
                Error = GroupPolicyText.Error(Localizer, Gp.Errors, ex);
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsSysvolWritable));
                OnPropertyChanged(nameof(HasSession));
                RefreshCommands();
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

    /// <summary>Clears what the document shows when the window is not connected.</summary>
    protected virtual void OnNotConnected()
    {
    }

    /// <summary>Re-evaluates the commands after a load or a selection change.</summary>
    protected virtual void RefreshCommands()
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

    private void OnGroupPolicyChanged(object? sender, EventArgs e) => _ = Gp.Dispatcher.RunAsync(() => LoadAsync().Forget());

    private void OnSessionChanged(object? sender, EventArgs e) => _ = Gp.Dispatcher.RunAsync(() => LoadAsync().Forget());

    private void OnLocalizerCultureChanged(object? sender, EventArgs e) => _ = Gp.Dispatcher.RunAsync(OnCultureChanged);
}
