using System.Collections.ObjectModel;
using System.Net;
using AdMng.Core.Directory;
using AdMng.Presentation.Connections;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Feedback;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AdMng.Presentation.Connection;

/// <summary>
/// Login window: domain (prefilled with the computer's domain), domain controller (automatic or chosen from a
/// discovered list), authentication (current Windows user or explicit credentials), channel security and saved
/// profiles. The password is read from <see cref="PasswordInput"/> only at connect time and never stored.
/// </summary>
/// <remarks>
/// In a connection window (with its <see cref="ConnectionContext"/>) the dialog also offers the window colour, tells the
/// window which profile and colour it connected with, and says when the chosen profile is already open in another window
/// (information only: the same domain with another account, or a second window of the same account, is supported).
/// Every window types its own password: a credential is never taken over from another window.
/// <para>The window has one status line (<see cref="Status"/>; track V, stage V2b, design note
/// docs/dev/v-interface-renewal.md "V2b-2 as built"): connecting and looking for the domain controllers of the domain
/// are its two operations - one at a time, each command refuses while the other runs - and the line says how they
/// ended: stopped, failed with the reason and what to do, or why nothing was started. A connection that succeeded says
/// nothing here: the window closes. The window has no session, so there is no "session ended" state; and nothing of
/// the credential - not the password, not the user name - is part of a text or of its log copy.</para>
/// </remarks>
public sealed partial class ConnectViewModel : ObservableObject, IHasPageStatus
{
    private readonly IDirectoryConnector _connector;
    private readonly ISessionManager _sessions;
    private readonly ISettingsStore _settings;
    private readonly ILocalizer _localizer;
    private readonly ErrorTranslator _errors;
    private readonly ConnectionContext? _connection;
    private readonly ConnectionRegistry? _registry;
    private bool _initialized;

    /// <remarks>
    /// <c>dispatcher</c>: the UI thread, for the status line; <c>connection</c>: the connection window this dialog
    /// connects (null in hosts without connection windows); <c>registry</c>: the open connection windows (for "this
    /// profile is already open in …"); <c>statusLog</c>: the diagnostic log the status line writes its value-free
    /// copies to (null in hosts without logging).
    /// </remarks>
    public ConnectViewModel(
        IDirectoryConnector connector,
        ISessionManager sessions,
        ISettingsStore settings,
        ILocalizer localizer,
        ErrorTranslator errors,
        IUiDispatcher dispatcher,
        ConnectionContext? connection = null,
        ConnectionRegistry? registry = null,
        ILogger<PageStatusHolder>? statusLog = null)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        _connection = connection;
        _registry = registry;
        Status = new PageStatusHolder(localizer, dispatcher ?? throw new ArgumentNullException(nameof(dispatcher)), statusLog);
        Colors =
        [
            new(null, localizer[L.Connections.ColorAutomatic]),
            .. ConnectionColors.Palette.Select(c => new Option<ConnectionColor?>(c, localizer[DisplayNames.ConnectionColorKey(c)])),
        ];
        AuthenticationModes =
        [
            new(AuthenticationMode.CurrentUser, localizer[DisplayNames.AuthenticationKey(AuthenticationMode.CurrentUser)]),
            new(AuthenticationMode.Credentials, localizer[DisplayNames.AuthenticationKey(AuthenticationMode.Credentials)]),
            new(AuthenticationMode.SimpleBind, localizer[DisplayNames.AuthenticationKey(AuthenticationMode.SimpleBind)]),
        ];
        SecurityModes =
        [
            new(TransportSecurity.SignAndSeal, localizer[DisplayNames.SecurityKey(TransportSecurity.SignAndSeal)]),
            new(TransportSecurity.Tls, localizer[DisplayNames.SecurityKey(TransportSecurity.Tls)]),
            new(TransportSecurity.StartTls, localizer[DisplayNames.SecurityKey(TransportSecurity.StartTls)]),
        ];
    }

    /// <summary>Raised on the UI thread after a successful connect (the app then opens the main window).</summary>
    public event EventHandler? Connected;

    /// <summary>
    /// The status of the window - what its one status line shows: the running connection attempt or search for domain
    /// controllers (with Stop), how it ended, and - at rest, once a search ran - how many domain controllers the
    /// drop-down offers.
    /// </summary>
    public PageStatusHolder Status { get; }

    /// <summary>Set by the view (PasswordBox wrapper). Read only inside <see cref="ConnectCommand"/>.</summary>
    public IPasswordInput? PasswordInput { get; set; }

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    public ObservableCollection<DomainControllerInfo> DomainControllers { get; } = [];

    public IReadOnlyList<Option<AuthenticationMode>> AuthenticationModes { get; }

    public IReadOnlyList<Option<TransportSecurity>> SecurityModes { get; }

    /// <summary>"Kolor okna": automatic (from the domain name) or one of the palette colours; saved with the profile.</summary>
    public IReadOnlyList<Option<ConnectionColor?>> Colors { get; }

    /// <summary>The colour chooser is offered (the dialog belongs to a connection window).</summary>
    public bool CanChooseColor => _connection is not null;

    /// <summary>The chosen window colour; null = automatic.</summary>
    [ObservableProperty]
    public partial ConnectionColor? SelectedColor { get; set; }

    /// <summary>
    /// "Ten profil jest już otwarty w oknie …" when the selected profile is connected in another window (information, not
    /// a block), else null.
    /// </summary>
    [ObservableProperty]
    public partial string? ProfileOpenElsewhereText { get; private set; }

    /// <summary>The colour of the other window that has the selected profile open (the dot next to the hint).</summary>
    [ObservableProperty]
    public partial ConnectionColor? ProfileOpenElsewhereColor { get; private set; }

    /// <summary>Windows identity used with <see cref="AuthenticationMode.CurrentUser"/> ("CONTOSO\jan").</summary>
    public string CurrentWindowsUser => string.IsNullOrEmpty(Environment.UserDomainName)
        ? Environment.UserName
        : $@"{Environment.UserDomainName}\{Environment.UserName}";

    [ObservableProperty]
    public partial ConnectionProfile? SelectedProfile { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(FindDomainControllersCommand))]
    public partial string Domain { get; set; } = string.Empty;

    /// <summary>Specific DC; empty = locate automatically (site aware).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string Server { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DomainControllerInfo? SelectedDomainController { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresCredentials))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial AuthenticationMode Authentication { get; set; } = AuthenticationMode.CurrentUser;

    public bool RequiresCredentials => Authentication != AuthenticationMode.CurrentUser;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string UserName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial TransportSecurity Security { get; set; } = TransportSecurity.SignAndSeal;

    [ObservableProperty]
    public partial bool SaveProfile { get; set; }

    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(FindDomainControllersCommand), nameof(DeleteProfileCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>The connection attempt runs: the Connect button is its Cancel.</summary>
    [ObservableProperty]
    public partial bool IsConnecting { get; private set; }

    /// <summary>The search for domain controllers runs: its button is its Cancel.</summary>
    [ObservableProperty]
    public partial bool IsFindingDomainControllers { get; private set; }

    /// <summary>Localized error with a hint how to fix it (what the status line says, as plain text; kept for callers that read it).</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>Progress text ("Connecting to contoso.test…"; the status line says it, kept for callers that read it).</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>Loads profiles and pre-fills the computer's domain (call once when the window opens).</summary>
    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Profiles.Clear();
        foreach (var profile in _settings.Current.ConnectionProfiles)
        {
            Profiles.Add(profile);
        }

        var last = Profiles.FirstOrDefault(p => string.Equals(p.Name, _settings.Current.LastProfileName, StringComparison.Ordinal));
        if (last is not null)
        {
            SelectedProfile = last;
        }
        else if (string.IsNullOrWhiteSpace(Domain))
        {
            Domain = _connector.GetComputerDomain() ?? string.Empty;
        }
    }

    /// <summary>
    /// Connects with what the window holds. Connecting is the operation of the window: its status line says so with
    /// Stop - the Connect button is its Cancel as well - and says how it ended unless it succeeded (the window closes
    /// then). A connection that was not even tried - a field is missing - is said as "not connected: why".
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect), IncludeCancelCommand = true)]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ErrorText = null;
        var validation = Validate();
        if (validation is not null)
        {
            NotStarted(L.ConnectStatus.NotConnected, validation);
            return;
        }

        NetworkCredential? credential = null;
        if (RequiresCredentials)
        {
            // Read the password only now; it lives in this local and the credential passed to the connector.
            var password = PasswordInput?.GetPassword() ?? string.Empty;
            if (password.Length == 0)
            {
                NotStarted(L.ConnectStatus.NotConnected, L.Connect.PasswordRequired);
                return;
            }

            credential = CreateCredential(UserName.Trim(), password);
        }

        var options = new ConnectionOptions
        {
            Domain = NullIfEmpty(Domain),
            Server = NullIfEmpty(Server),
            Authentication = Authentication,
            Security = Security,
            UserName = RequiresCredentials ? UserName.Trim() : null,
        };

        if (_connection is not null)
        {
            // What the window shows and remembers: the profile it connected with (a saved one, or the profile that is about
            // to be saved) and the colour. Set before the bind, so the identity built on SessionChanged carries them.
            _connection.ProfileName = SaveProfile && !string.IsNullOrWhiteSpace(ProfileName) ? ProfileName.Trim() : SelectedProfile?.Name;
            _connection.PreferredColor = SelectedColor;
        }

        // What the operator connects to, as far as the window knows it: the domain controller, else the domain (the
        // computer's own when the box is empty). Never the account.
        var target = options.Server ?? options.Domain ?? _connector.GetComputerDomain() ?? string.Empty;
        IsBusy = true;
        IsConnecting = true;
        StatusText = _localizer.Format(L.Connect.Connecting, target);

        // The operation of the window: "working" from here on, with Stop. The cancel of the command (the window that
        // closes while it connects) is the same Stop; however this method is left, the line does not stay in "working".
        using var connecting = Status.Begin(StatusText, new PageStatusLog(L.Connect.Connecting));
        using var stop = cancellationToken.Register(connecting.Cancel);
        try
        {
            try
            {
                // Closing the window (its lifetime) ends a bind still running; one that completes anyway is closed by the
                // session manager and never becomes a connection without a window.
                using var lifetime = _connection?.LinkLifetime(connecting.Token);
                await _sessions.ConnectAsync(options, credential, lifetime?.Token ?? connecting.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                StatusText = _localizer[L.Errors.Cancelled];

                // Only a connection the operator stopped says so; the end of the window's lifetime ends it without a word.
                if (connecting.IsCancelling)
                {
                    var again = _localizer[L.Connect.ConnectButton];
                    connecting.CompleteAttention(
                        _localizer.Format(L.ConnectStatus.Stopped, target, again),
                        new PageStatusLog(L.ConnectStatus.Stopped),
                        new PageStatusAction(again, ConnectCommand));
                }

                return;
            }
            catch (Exception ex)
            {
                StatusText = null;
                ErrorText = _errors.TranslateConnectError(ex);

                // What was tried, why it failed, what to do, the code. No Retry in the line: the advice is nearly always
                // to change something in the window or on the account first - and a bind repeated with a wrong password
                // counts towards the lockout of the account. The Connect button is where it was.
                var code = ResultCode.Of(ex);
                connecting.CompleteError(
                    PageStatusText.Failure(_localizer.Format(L.ConnectStatus.Failed, target), _errors.Translate(ex), _errors.ConnectHint(ex), code),
                    new PageStatusLog(L.ConnectStatus.Failed).WithCode(code));
                return;
            }

            // Connected: from here on nothing may turn the success into an error - a failed profile save would
            // otherwise leave an open session behind a "failed" window and the next attempt would open a second one.
            // The window closes: its line says nothing about a result nobody would read.
            PasswordInput?.Clear();
            StatusText = null;
            try
            {
                await RememberProfileAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The profile is not saved; the connection is fine.
            }

            Connected?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsConnecting = false;
            IsBusy = false;
        }
    }

    private bool CanConnect() => !IsBusy;

    /// <summary>
    /// Fills the drop-down of domain controllers. The search is an operation of the window (it and connecting never run
    /// together): the line says it with Stop - its button is its Cancel - and, when it is through, how many domain
    /// controllers the drop-down offers: the summary of the window, because the list itself is the result.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFindDomainControllers), IncludeCancelCommand = true)]
    private async Task FindDomainControllersAsync(CancellationToken cancellationToken)
    {
        var domain = NullIfEmpty(Domain) ?? _connector.GetComputerDomain();
        if (domain is null)
        {
            NotStarted(L.ConnectStatus.NotSearched, L.Connect.DomainRequired);
            return;
        }

        ErrorText = null;
        IsBusy = true;
        IsFindingDomainControllers = true;
        StatusText = _localizer.Format(L.Connect.FindingDomainControllers, domain);
        using var finding = Status.Begin(StatusText, new PageStatusLog(L.Connect.FindingDomainControllers));
        using var stop = cancellationToken.Register(finding.Cancel);
        var token = finding.Token;
        try
        {
            var controllers = await Task.Run(() => _connector.FindDomainControllersAsync(domain, token), token).ConfigureAwait(true);
            DomainControllers.Clear();
            foreach (var dc in controllers.OrderByDescending(d => d.IsInClientSite).ThenBy(d => d.IsReadOnly).ThenBy(d => d.HostName, StringComparer.OrdinalIgnoreCase))
            {
                DomainControllers.Add(dc);
            }

            StatusText = _localizer.Format(L.Connect.DomainControllersFound, DomainControllers.Count);
            Status.SetIdle(StatusText);
        }
        catch (OperationCanceledException)
        {
            StatusText = _localizer[L.Errors.Cancelled];
            if (finding.IsCancelling)
            {
                var again = _localizer[L.Connect.FindDomainControllers];
                finding.CompleteAttention(
                    _localizer.Format(L.ConnectStatus.FindStopped, domain, again),
                    new PageStatusLog(L.ConnectStatus.FindStopped),
                    new PageStatusAction(again, FindDomainControllersCommand));
            }
        }
        catch (Exception ex)
        {
            StatusText = null;
            ErrorText = _errors.TranslateConnectError(ex);

            // The drop-down is as it was: a result that can be closed, with Retry (a read is safe to repeat).
            var code = ResultCode.Of(ex);
            finding.CompleteError(
                PageStatusText.Failure(
                    _localizer.Format(L.ConnectStatus.FindFailed, domain),
                    _errors.Translate(ex),
                    _errors.ConnectHint(ex) ?? _localizer[L.Feedback.RetryNext],
                    code),
                new PageStatusLog(L.ConnectStatus.FindFailed).WithCode(code),
                new PageStatusAction(_localizer[L.Common.Retry], FindDomainControllersCommand));
        }
        finally
        {
            IsFindingDomainControllers = false;
            IsBusy = false;
        }
    }

    private bool CanFindDomainControllers() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        await _settings.UpdateAsync(s =>
        {
            s.ConnectionProfiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.Ordinal));
            if (string.Equals(s.LastProfileName, profile.Name, StringComparison.Ordinal))
            {
                s.LastProfileName = null;
            }
        }).ConfigureAwait(true);
        Profiles.Remove(profile);
        SelectedProfile = null;
    }

    private bool CanDeleteProfile() => !IsBusy && SelectedProfile is not null;

    partial void OnSelectedProfileChanged(ConnectionProfile? value)
    {
        DeleteProfileCommand.NotifyCanExecuteChanged();
        UpdateProfileOpenElsewhere();
        if (value is null)
        {
            return;
        }

        Domain = value.Domain ?? string.Empty;
        Server = value.Server ?? string.Empty;
        Authentication = value.Authentication;
        Security = value.Security is TransportSecurity.None ? TransportSecurity.SignAndSeal : value.Security;
        UserName = value.UserName ?? string.Empty;
        SelectedColor = value.Color;
        ProfileName = value.Name;
        SaveProfile = false;
        ErrorText = null;

        // What the line said was about the connection that was typed before: a result leaves with the operator's next
        // step (design note 6.7). A running operation keeps the line.
        if (Status.Operation is null)
        {
            Status.Clear();
        }
    }

    partial void OnSelectedDomainControllerChanged(DomainControllerInfo? value)
    {
        if (value is not null)
        {
            Server = value.HostName;
        }
    }

    /// <summary>
    /// Nothing was started because the window lacks something: "Not connected: Enter the password." - an attention the
    /// operator can close; the log copy is the key of the reason.
    /// </summary>
    /// <param name="skippedKey">What was skipped, without a full stop.</param>
    /// <param name="reasonKey">Why: the validation text.</param>
    private void NotStarted(string skippedKey, string reasonKey)
    {
        ErrorText = _localizer[reasonKey];
        Status.Show(PageStatus.Attention(PageStatusText.Failure(_localizer[skippedKey], ErrorText), new PageStatusLog(reasonKey)));
    }

    /// <summary>Returns a resource key describing the first validation problem, or null.</summary>
    private string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Domain) && string.IsNullOrWhiteSpace(Server) && _connector.GetComputerDomain() is null)
        {
            return L.Connect.DomainRequired;
        }

        if (RequiresCredentials && string.IsNullOrWhiteSpace(UserName))
        {
            return L.Connect.UserNameRequired;
        }

        if (Authentication == AuthenticationMode.SimpleBind && Security == TransportSecurity.SignAndSeal)
        {
            return L.Connect.SimpleBindRequiresTls;
        }

        if (SaveProfile && string.IsNullOrWhiteSpace(ProfileName))
        {
            return L.Connect.ProfileNameRequired;
        }

        return null;
    }

    private async Task RememberProfileAsync()
    {
        var save = SaveProfile && !string.IsNullOrWhiteSpace(ProfileName);
        var profile = new ConnectionProfile
        {
            Name = ProfileName.Trim(),
            Domain = NullIfEmpty(Domain),
            Server = NullIfEmpty(Server),
            Authentication = Authentication,
            Security = Security,
            UserName = RequiresCredentials ? UserName.Trim() : null,
            Color = SelectedColor,
        };
        await _settings.UpdateAsync(s =>
        {
            if (save)
            {
                s.ConnectionProfiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.Ordinal));
                s.ConnectionProfiles.Add(profile);
                s.LastProfileName = profile.Name;
            }
            else if (SelectedProfile is null)
            {
                s.LastProfileName = null;
            }
            else
            {
                s.LastProfileName = SelectedProfile.Name;
            }
        }).ConfigureAwait(true);

        if (save)
        {
            var existing = Profiles.FirstOrDefault(p => string.Equals(p.Name, profile.Name, StringComparison.Ordinal));
            if (existing is not null)
            {
                Profiles.Remove(existing);
            }

            Profiles.Add(profile);
        }
    }

    /// <summary>The hint that the selected profile is already connected in another connection window.</summary>
    private void UpdateProfileOpenElsewhere()
    {
        var name = SelectedProfile?.Name;
        var other = name is null || _registry is null
            ? null
            : _registry.Connections.FirstOrDefault(c => c.Id != _connection?.Id
                && c.Identity is { } identity && string.Equals(identity.ProfileName, name, StringComparison.Ordinal));
        ProfileOpenElsewhereText = other?.Identity is { } open ? _localizer.Format(L.Connections.ProfileOpenElsewhere, open.Label) : null;
        ProfileOpenElsewhereColor = other?.Identity?.Color;
    }

    /// <summary>Splits DOMAIN\user into user + domain (Negotiate needs them separately); UPNs are kept whole.</summary>
    internal static NetworkCredential CreateCredential(string userName, string password)
    {
        var backslash = userName.IndexOf('\\', StringComparison.Ordinal);
        return backslash > 0
            ? new NetworkCredential(userName[(backslash + 1)..], password, userName[..backslash])
            : new NetworkCredential(userName, password);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
