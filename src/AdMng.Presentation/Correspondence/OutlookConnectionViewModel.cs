using System.Collections.ObjectModel;
using System.Text;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Correspondence;

/// <summary>The state of the window's Outlook connection.</summary>
public enum OutlookState
{
    /// <summary>Not connected yet (nothing tried, or a connection was cancelled).</summary>
    Unknown,

    Connecting,

    Connected,

    /// <summary>No classic Outlook on this computer.</summary>
    NotInstalled,

    /// <summary>The last connection failed (or the session was lost): <see cref="OutlookConnectionViewModel.Failure"/>.</summary>
    Failed,

    /// <summary>Not Windows, or the host has no Outlook client.</summary>
    NotSupported,
}

/// <summary>
/// The Outlook connection of the Korespondencja window (design note 6.4): state, profile accounts, the chosen account and
/// "w imieniu"; the session is created on demand (<see cref="EnsureConnectedAsync"/>: one connection at a time, a new one
/// after the session was lost) and kept for the window's life; "Sprawdź Outlooka" shows the diagnostics (with the journal,
/// Downloads and signatures folders). Failures show the localized code text (<c>Correspondence.Outlook.Code.{code}</c>),
/// never a modal error.
/// </summary>
public sealed partial class OutlookConnectionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task<IOutlookSession?>? _connecting;
    private string _savedAccount = string.Empty;
    private bool _restoringAccount;
    private int _disposed;

    public OutlookConnectionViewModel(DialogServices services, CorrespondenceServices correspondence)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Correspondence = correspondence ?? throw new ArgumentNullException(nameof(correspondence));
        State = Correspondence.Outlook?.Availability switch
        {
            null or OutlookAvailability.NotSupported => OutlookState.NotSupported,
            OutlookAvailability.NotInstalled => OutlookState.NotInstalled,
            _ => OutlookState.Unknown,
        };
        UpdateStateText();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsAvailable))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(CheckCommand))]
    public partial OutlookState State { get; private set; } = OutlookState.Unknown;

    /// <summary>"Outlook: połączono (2 konta)" …</summary>
    [ObservableProperty]
    public partial string StateText { get; private set; } = string.Empty;

    /// <summary>The accounts of the profile.</summary>
    public ObservableCollection<OutlookAccount> Accounts { get; } = [];

    /// <summary>The account to send from (null: the profile's default account).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountAddress), nameof(OwnAddress))]
    public partial OutlookAccount? SelectedAccount { get; set; }

    /// <summary>"W imieniu" (SentOnBehalfOfName), or empty.</summary>
    [ObservableProperty]
    public partial string OnBehalfOf { get; set; } = string.Empty;

    /// <summary>Why the last connection failed or the session was lost (null when connected or never tried).</summary>
    [ObservableProperty]
    public partial OutlookFailure? Failure { get; private set; }

    /// <summary>The localized text of <see cref="Failure"/>, or null.</summary>
    [ObservableProperty]
    public partial string? FailureText { get; private set; }

    /// <summary>"Sprawdź Outlooka" is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial bool IsChecking { get; private set; }

    /// <summary>A session is connected (sending, preview and responses can use Outlook).</summary>
    public bool IsConnected => State == OutlookState.Connected && Session is { IsConnected: true };

    /// <summary>Classic Outlook can be used on this computer (it may still have to be started).</summary>
    public bool IsAvailable => State is not (OutlookState.NotInstalled or OutlookState.NotSupported);

    /// <summary>The SMTP address of the chosen account, or null (the profile's default account).</summary>
    public string? AccountAddress => string.IsNullOrWhiteSpace(SelectedAccount?.SmtpAddress) ? null : SelectedAccount.SmtpAddress;

    /// <summary>The operator's own address (a test to self, the To of BCC messages): the chosen account, else the profile's
    /// current user; null when not connected.</summary>
    public string? OwnAddress => AccountAddress ?? Profile?.CurrentUserAddress;

    /// <summary>The profile of the connected session, or null.</summary>
    public OutlookProfile? Profile => Session?.Profile;

    /// <summary>The connected session, or null.</summary>
    internal IOutlookSession? Session { get; private set; }

    /// <summary>The connection in progress (tests await it), or null.</summary>
    internal Task<IOutlookSession?>? Connecting
    {
        get
        {
            lock (_gate)
            {
                return _connecting;
            }
        }
    }

    private DialogServices Services { get; }

    private CorrespondenceServices Correspondence { get; }

    private ILocalizer Localizer => Services.Localizer;

    /// <summary>
    /// The connected session: connects when there is none or it was lost (state and text updated, one connection at a time);
    /// null when Outlook cannot be used – the state says why. Never throws for an Outlook failure.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting (the connection itself goes on for the other callers).</param>
    public Task<IOutlookSession?> EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Task.FromResult<IOutlookSession?>(null);
        }

        Task<IOutlookSession?> pending;
        TaskCompletionSource<IOutlookSession?>? started = null;
        lock (_gate)
        {
            if (Session is { IsConnected: true } session && State == OutlookState.Connected && _connecting is null or { IsCompleted: true })
            {
                return Task.FromResult<IOutlookSession?>(session);
            }

            if (_connecting is not { IsCompleted: false })
            {
                started = new TaskCompletionSource<IOutlookSession?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _connecting = started.Task;
            }

            pending = _connecting;
        }

        if (started is not null)
        {
            // Started outside the gate: the state changes it makes run the window's handlers.
            _ = CompleteAsync(started);
        }

        return cancellationToken.CanBeCanceled ? pending.WaitAsync(cancellationToken) : pending;
    }

    /// <summary>Connects in the background (the window opened): the state shows the result, nothing is asked.</summary>
    public void ConnectInBackground()
    {
        if (IsAvailable && Correspondence.Outlook is not null)
        {
            _ = EnsureConnectedAsync();
        }
    }

    /// <summary>
    /// A page met a failure while using the session: a lost Outlook (<see cref="OutlookFailureKind.Disconnected"/>,
    /// <see cref="OutlookFailureKind.NotResponding"/>, <see cref="OutlookFailureKind.Elevated"/>) shows in the state, so the
    /// next <see cref="EnsureConnectedAsync"/> connects again. Other failures concern one item and change nothing.
    /// </summary>
    public void ReportFailure(OutlookFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Kind is OutlookFailureKind.Disconnected or OutlookFailureKind.NotResponding or OutlookFailureKind.Elevated)
        {
            _ = Services.Dispatcher.RunAsync(() => SetState(OutlookState.Failed, failure));
        }
    }

    /// <summary>Takes the account and "w imieniu" of the settings (the account is applied after connect).</summary>
    public void LoadFrom(CorrespondenceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _savedAccount = settings.AccountAddress?.Trim() ?? string.Empty;
        OnBehalfOf = settings.OnBehalfOf?.Trim() ?? string.Empty;
        SelectSavedAccount();
    }

    /// <summary>Writes the account and "w imieniu" into the settings (an account of the settings not in this profile is
    /// kept until the operator chooses another).</summary>
    public void SaveTo(CorrespondenceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.AccountAddress = _savedAccount;
        settings.OnBehalfOf = OnBehalfOf?.Trim() ?? string.Empty;
    }

    /// <summary>The texts in the current UI language (the window's language changed).</summary>
    public void RefreshTexts()
    {
        FailureText = Failure is null ? null : CorrespondenceText.FailureText(Localizer, Failure);
        UpdateStateText();
    }

    /// <summary>Ends the session (the window closed); a connection in progress is abandoned and its session released.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task<IOutlookSession?>? pending;
        lock (_gate)
        {
            pending = _connecting;
        }

        if (pending is not null)
        {
            try
            {
                await pending.ConfigureAwait(false);   // a session it made is released by ConnectCoreAsync
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
            {
                // The dispatcher shut down while the state was updated (the app is closing).
            }
        }

        var session = Session;
        Session = null;
        if (session is not null)
        {
            await ReleaseAsync(session).ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }

    public override string ToString() => nameof(OutlookConnectionViewModel);

    /// <summary>
    /// The text of "Sprawdź Outlooka" (MailerWPiA's Diagnose plus the folders of this window): user and computer, Outlook
    /// version, profile, accounts, current mailbox, signatures, the test item, the journal, Downloads and the signatures
    /// folder. The accounts are the operator's own; no recipient appears.
    /// </summary>
    /// <param name="diagnostics">What Outlook answered, or null.</param>
    /// <param name="failure">Why there is no answer (no connection, or the diagnostics failed), or null.</param>
    /// <param name="connected">A session answered the connection (the diagnostics themselves failed).</param>
    internal string DiagnosticsReport(OutlookDiagnostics? diagnostics, OutlookFailure? failure, bool connected = false)
    {
        var l = Localizer;
        var text = new StringBuilder();
        text.AppendLine(l.Format(L.CorrespondenceOutlook.DiagUser, $@"{Environment.UserDomainName}\{Environment.UserName}"));
        text.AppendLine(l.Format(L.CorrespondenceOutlook.DiagComputer, Environment.MachineName,
            (diagnostics?.ProcessIs64Bit ?? Environment.Is64BitProcess) ? 64 : 32));
        text.AppendLine();
        if (diagnostics is null)
        {
            var reason = failure is null ? l[CorrespondenceText.OutlookStateKey(State)] : CorrespondenceText.FailureText(l, failure);
            text.AppendLine(l.Format(connected ? L.CorrespondenceOutlook.DiagFailed : L.CorrespondenceOutlook.DiagNotConnected, reason));
        }
        else
        {
            text.AppendLine(l.Format(L.CorrespondenceOutlook.DiagOutlook, diagnostics.Version, diagnostics.ProfileName));
            text.AppendLine(l.Format(L.CorrespondenceOutlook.DiagAccounts, diagnostics.Accounts.Count));
            foreach (var account in diagnostics.Accounts.Where(a => a is not null))
            {
                text.Append("   • ").AppendLine(AccountText(account));
            }

            if (!string.IsNullOrWhiteSpace(diagnostics.CurrentUserAddress))
            {
                text.AppendLine(l.Format(L.CorrespondenceOutlook.DiagCurrentUser, diagnostics.CurrentUserAddress));
            }
        }

        var signatures = diagnostics?.Signatures is { Count: > 0 } found ? found : SignatureNames();
        text.AppendLine();
        text.AppendLine(l.Format(L.CorrespondenceOutlook.DiagSignatures, signatures.Count));
        if (signatures.Count == 0)
        {
            var folder = Correspondence.Signatures?.Folder ?? l[L.CorrespondenceOutlook.DiagNotFound];
            text.Append("   ").AppendLine(l.Format(L.CorrespondenceOutlook.DiagNoSignatures, folder));
        }

        foreach (var signature in signatures)
        {
            text.Append("   • ").AppendLine(signature);
        }

        if (diagnostics is not null)
        {
            text.AppendLine();
            text.AppendLine(l.Format(L.CorrespondenceOutlook.DiagTestItem, diagnostics.TestItemCreated switch
            {
                true => l[L.CorrespondenceOutlook.DiagTestOk],
                false => l.Format(L.CorrespondenceOutlook.DiagTestFailed, diagnostics.TestItemFailure is { } itemFailure
                    ? CorrespondenceText.FailureText(l, itemFailure)
                    : l[CorrespondenceText.OutlookCodeKey(OutlookFailureCodes.Unknown)]),
                null => l[L.CorrespondenceOutlook.DiagTestSkipped],
            }));
        }

        var journal = Correspondence.Journal;
        text.AppendLine();
        text.AppendLine(l[L.CorrespondenceOutlook.DiagJournal]);
        text.Append("   ").AppendLine(journal.IsLoaded
            ? l.Format(L.CorrespondenceOutlook.DiagJournalEntries, journal.Folder, journal.Count)
            : l.Format(L.CorrespondenceOutlook.DiagJournalNotLoaded, journal.Folder));
        text.AppendLine(l[L.CorrespondenceOutlook.DiagDownloads]);
        text.Append("   ").AppendLine(Correspondence.Platform?.DownloadsFolder ?? l[L.CorrespondenceOutlook.DiagNotFound]);
        text.AppendLine(l[L.CorrespondenceOutlook.DiagSignaturesFolder]);
        text.Append("   ").Append(Correspondence.Signatures?.Folder ?? l[L.CorrespondenceOutlook.DiagNotFound]);
        return text.ToString();
    }

    /// <summary>"Połącz z Outlookiem": connects when not connected (or the session was lost); a connected session stays.</summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync() => await EnsureConnectedAsync().ConfigureAwait(true);

    private bool CanConnect() => State is not (OutlookState.Connecting or OutlookState.NotSupported or OutlookState.NotInstalled);

    /// <summary>"Sprawdź Outlooka": connects when needed, asks Outlook for the diagnostics (with a test item that is
    /// discarded, never sent) and shows the report.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        IsChecking = true;
        try
        {
            var session = await EnsureConnectedAsync().ConfigureAwait(true);
            OutlookDiagnostics? diagnostics = null;
            var failure = Failure;
            if (session is not null)
            {
                try
                {
                    diagnostics = await session.DiagnoseAsync(createTestItem: true, _lifetime.Token).ConfigureAwait(true);
                }
                catch (OutlookException ex)
                {
                    failure = ex.Failure;
                    ReportFailure(ex.Failure);
                }
                catch (ObjectDisposedException)
                {
                    failure = new OutlookFailure(OutlookFailureKind.Disconnected, OutlookFailureCodes.Disconnected, null, null);
                    ReportFailure(failure);
                }
            }

            var title = Localizer[L.CorrespondenceOutlook.CheckTitle];
            var report = DiagnosticsReport(diagnostics, failure, connected: session is not null);
            if (Correspondence.Dialogs is { } dialogs)
            {
                await dialogs.ShowReportAsync(title, report).ConfigureAwait(true);
            }
            else
            {
                await Services.Dialogs.ShowInfoAsync(title, report).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The window closed.
        }
        finally
        {
            IsChecking = false;
        }
    }

    private bool CanCheck() => !IsChecking && State != OutlookState.Connecting;

    /// <summary>Runs one connection and completes <paramref name="connection"/> with its session (null: none).</summary>
    private async Task CompleteAsync(TaskCompletionSource<IOutlookSession?> connection)
    {
        try
        {
            connection.TrySetResult(await ConnectCoreAsync().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            connection.TrySetException(ex);
        }
    }

    private async Task<IOutlookSession?> ConnectCoreAsync()
    {
        var client = Correspondence.Outlook;
        if (client is null)
        {
            await Services.Dispatcher.RunAsync(() => SetState(OutlookState.NotSupported, null)).ConfigureAwait(false);
            return null;
        }

        switch (client.Availability)
        {
            case OutlookAvailability.NotSupported:
                await Services.Dispatcher.RunAsync(() => SetState(OutlookState.NotSupported, Failed(OutlookFailureKind.NotSupported))).ConfigureAwait(false);
                return null;
            case OutlookAvailability.NotInstalled:
                await Services.Dispatcher.RunAsync(() => SetState(OutlookState.NotInstalled, Failed(OutlookFailureKind.NotInstalled))).ConfigureAwait(false);
                return null;
        }

        await Services.Dispatcher.RunAsync(() => SetState(OutlookState.Connecting, null)).ConfigureAwait(false);
        var previous = Session;
        Session = null;
        if (previous is not null)
        {
            await ReleaseAsync(previous).ConfigureAwait(false);
        }

        OutlookConnectResult result;
        try
        {
            result = await client.ConnectAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                await Services.Dispatcher.RunAsync(() => SetState(OutlookState.Unknown, null)).ConfigureAwait(false);
            }

            return null;
        }
        catch (OutlookException ex)
        {
            result = new OutlookConnectResult(null, ex.Failure);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The client classifies its failures; anything else is "unknown" with its HRESULT (design note 4.3) – the
            // message is never kept (it may contain an address).
            result = new OutlookConnectResult(null, new OutlookFailure(OutlookFailureKind.Unknown, OutlookFailureCodes.Unknown, "connect", ex.HResult));
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            if (result.Session is { } late)
            {
                await ReleaseAsync(late).ConfigureAwait(false);
            }

            return null;
        }

        if (result.Session is { } session)
        {
            Session = session;
            await Services.Dispatcher.RunAsync(() =>
            {
                ApplyProfile(session.Profile);
                SetState(OutlookState.Connected, null);
            }).ConfigureAwait(false);
            return session;
        }

        var failure = result.Failure ?? Failed(OutlookFailureKind.Unknown);
        await Services.Dispatcher.RunAsync(() => SetState(failure.Kind switch
        {
            OutlookFailureKind.NotInstalled => OutlookState.NotInstalled,
            OutlookFailureKind.NotSupported => OutlookState.NotSupported,
            OutlookFailureKind.Cancelled => OutlookState.Unknown,
            _ => OutlookState.Failed,
        }, failure)).ConfigureAwait(false);
        return null;
    }

    private static OutlookFailure Failed(OutlookFailureKind kind) => new(kind, OutlookFailureCodes.Of(kind), "connect", null);

    private static async Task ReleaseAsync(IOutlookSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or TimeoutException or OutlookException)
        {
            // Outlook is gone already; nothing is left to release.
        }
    }

    private void ApplyProfile(OutlookProfile profile)
    {
        _restoringAccount = true;
        try
        {
            Accounts.Clear();
            foreach (var account in profile.Accounts ?? [])
            {
                if (account is not null)
                {
                    Accounts.Add(account);
                }
            }
        }
        finally
        {
            _restoringAccount = false;
        }

        SelectSavedAccount();
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(OwnAddress));
    }

    /// <summary>Selects the account of the settings when the profile has it (else the profile's default account).</summary>
    private void SelectSavedAccount()
    {
        if (Accounts.Count == 0)
        {
            return;
        }

        _restoringAccount = true;
        try
        {
            SelectedAccount = _savedAccount.Length == 0
                ? null
                : Accounts.FirstOrDefault(a => string.Equals(a.SmtpAddress, _savedAccount, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _restoringAccount = false;
        }
    }

    partial void OnSelectedAccountChanged(OutlookAccount? value)
    {
        if (!_restoringAccount)
        {
            _savedAccount = value?.SmtpAddress?.Trim() ?? string.Empty;
        }
    }

    private void SetState(OutlookState state, OutlookFailure? failure)
    {
        Failure = failure;
        FailureText = failure is null ? null : CorrespondenceText.FailureText(Localizer, failure);
        State = state;
        UpdateStateText();
        OnPropertyChanged(nameof(IsConnected));
    }

    private void UpdateStateText()
    {
        var l = Localizer;
        StateText = State switch
        {
            OutlookState.Connected when Accounts.Count > 1 =>
                l.Format(PolishFew(Accounts.Count) ? L.CorrespondenceOutlook.ConnectedFew : L.CorrespondenceOutlook.ConnectedMany, Accounts.Count),
            OutlookState.Failed => l.Format(CorrespondenceText.OutlookStateKey(OutlookState.Failed),
                FailureText ?? l[CorrespondenceText.OutlookCodeKey(OutlookFailureCodes.Unknown)]),
            _ => l[CorrespondenceText.OutlookStateKey(State)],
        };
    }

    /// <summary>The Polish "few" form (2–4, 22–24 … but not 12–14): "2 konta", else "5 kont".</summary>
    private static bool PolishFew(int count) => count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14;

    /// <summary>"Display &lt;smtp&gt;" of one of the operator's own accounts (never through <c>ToString</c>, which is for
    /// logs).</summary>
    private static string AccountText(OutlookAccount account) =>
        string.IsNullOrWhiteSpace(account.SmtpAddress) || string.Equals(account.DisplayName, account.SmtpAddress, StringComparison.OrdinalIgnoreCase)
            ? account.DisplayName
            : string.IsNullOrWhiteSpace(account.DisplayName) ? account.SmtpAddress : $"{account.DisplayName} <{account.SmtpAddress}>";

    private IReadOnlyList<string> SignatureNames()
    {
        try
        {
            return Correspondence.Signatures?.List() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
