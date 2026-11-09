using System.Security.Cryptography;
using AdMng.Core.Directory;
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
public abstract partial class PkiDocumentBase : ObservableObject, IShellDocument
{
    private bool _disposed;

    protected PkiDocumentBase(PkiServices pki)
    {
        Pki = pki ?? throw new ArgumentNullException(nameof(pki));
        Pki.Sessions.SessionChanged += OnSessionChanged;
        Pki.Localizer.CultureChanged += OnLocalizerCultureChanged;
        Pki.Settings.Changed += OnSettingsStoreChanged;
    }

    /// <summary>The window's PKI services.</summary>
    public PkiServices Pki { get; }

    public abstract string Title { get; }

    public abstract ObjectIcon Icon { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsLoading { get; protected set; }

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

    /// <summary>F5: reads everything the document shows again (the snapshot included).</summary>
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
        Pki.Sessions.SessionChanged -= OnSessionChanged;
        Pki.Localizer.CultureChanged -= OnLocalizerCultureChanged;
        Pki.Settings.Changed -= OnSettingsStoreChanged;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Reads what the document shows on <paramref name="session"/> (through <see cref="PkiServices.Snapshot"/>; pass
    /// <see cref="IsRefresh"/> as its <c>refresh</c>). Runs on the UI thread between awaits; throws directory failures, which
    /// the base turns into <see cref="Error"/>.
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
            // Next() cancelled a running load (the session ended under it): its finally skips the reset, so do it here.
            IsLoading = false;
            IsRefresh = false;
            Error = Localizer[L.Errors.NotConnected];
            OnNotConnected();
            OnPropertyChanged(nameof(HasSession));
            RefreshCommands();
            return;
        }

        IsLoading = true;
        IsRefresh = refresh;
        Error = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Pki.Lifetime);
            await LoadCoreAsync(session, linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load, or the window closed.
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (!token.IsCancellationRequested)
            {
                Error = Pki.Errors.Translate(ex);
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
                IsRefresh = false;
                OnPropertyChanged(nameof(HasSession));
                RefreshCommands();
            }
        }
    }

    /// <summary>
    /// The failures a load turns into <see cref="Error"/> (as <c>GroupPolicyText.IsExpected</c>): directory and session
    /// errors, file and access errors, timeouts, certificate / ASN.1 parsing (<see cref="CryptographicException"/>) and a
    /// resource formatted with the wrong arguments (<see cref="FormatException"/>) – anything else is a bug and escapes.
    /// </summary>
    protected static bool IsExpected(Exception exception) => exception is DirectoryException or SessionEndedException
        or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or TimeoutException
        or CryptographicException or FormatException;

    private void OnSessionChanged(object? sender, EventArgs e) => _ = Pki.Dispatcher.RunAsync(() => LoadAsync().Forget());

    private void OnLocalizerCultureChanged(object? sender, EventArgs e) => _ = Pki.Dispatcher.RunAsync(OnCultureChanged);

    private void OnSettingsStoreChanged(object? sender, EventArgs e) => _ = Pki.Dispatcher.RunAsync(() =>
    {
        if (!_disposed)
        {
            OnSettingsChanged();
        }
    });
}
