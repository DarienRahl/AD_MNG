using System.Collections.ObjectModel;
using System.Net;
using AdMng.Core.Directory;
using AdMng.Presentation.Errors;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Session;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Connection;

/// <summary>
/// Login window: domain (prefilled with the computer's domain), domain controller (automatic or chosen from a
/// discovered list), authentication (current Windows user or explicit credentials), channel security and saved
/// profiles. The password is read from <see cref="PasswordInput"/> only at connect time and never stored.
/// </summary>
public sealed partial class ConnectViewModel : ObservableObject
{
    private readonly IDirectoryConnector _connector;
    private readonly ISessionManager _sessions;
    private readonly ISettingsStore _settings;
    private readonly ILocalizer _localizer;
    private readonly ErrorTranslator _errors;
    private bool _initialized;

    public ConnectViewModel(
        IDirectoryConnector connector,
        ISessionManager sessions,
        ISettingsStore settings,
        ILocalizer localizer,
        ErrorTranslator errors)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
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

    /// <summary>Set by the view (PasswordBox wrapper). Read only inside <see cref="ConnectCommand"/>.</summary>
    public IPasswordInput? PasswordInput { get; set; }

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    public ObservableCollection<DomainControllerInfo> DomainControllers { get; } = [];

    public IReadOnlyList<Option<AuthenticationMode>> AuthenticationModes { get; }

    public IReadOnlyList<Option<TransportSecurity>> SecurityModes { get; }

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

    /// <summary>Localized error with a hint how to fix it.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    /// <summary>Progress text ("Connecting to contoso.test…").</summary>
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

    [RelayCommand(CanExecute = nameof(CanConnect), IncludeCancelCommand = true)]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ErrorText = null;
        var validation = Validate();
        if (validation is not null)
        {
            ErrorText = _localizer[validation];
            return;
        }

        NetworkCredential? credential = null;
        if (RequiresCredentials)
        {
            // Read the password only now; it lives in this local and the credential passed to the connector.
            var password = PasswordInput?.GetPassword() ?? string.Empty;
            if (password.Length == 0)
            {
                ErrorText = _localizer[L.Connect.PasswordRequired];
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

        IsBusy = true;
        StatusText = _localizer.Format(L.Connect.Connecting, options.Server ?? options.Domain ?? string.Empty);
        try
        {
            await _sessions.ConnectAsync(options, credential, cancellationToken).ConfigureAwait(true);
            PasswordInput?.Clear();
            await RememberProfileAsync().ConfigureAwait(true);
            StatusText = null;
            Connected?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            StatusText = _localizer[L.Errors.Cancelled];
        }
        catch (Exception ex)
        {
            StatusText = null;
            ErrorText = _errors.TranslateConnectError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanConnect() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanFindDomainControllers), IncludeCancelCommand = true)]
    private async Task FindDomainControllersAsync(CancellationToken cancellationToken)
    {
        var domain = NullIfEmpty(Domain) ?? _connector.GetComputerDomain();
        if (domain is null)
        {
            ErrorText = _localizer[L.Connect.DomainRequired];
            return;
        }

        ErrorText = null;
        IsBusy = true;
        StatusText = _localizer.Format(L.Connect.FindingDomainControllers, domain);
        try
        {
            var controllers = await Task.Run(() => _connector.FindDomainControllersAsync(domain, cancellationToken), cancellationToken)
                .ConfigureAwait(true);
            DomainControllers.Clear();
            foreach (var dc in controllers.OrderByDescending(d => d.IsInClientSite).ThenBy(d => d.IsReadOnly).ThenBy(d => d.HostName, StringComparer.OrdinalIgnoreCase))
            {
                DomainControllers.Add(dc);
            }

            StatusText = _localizer.Format(L.Connect.DomainControllersFound, DomainControllers.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText = _localizer[L.Errors.Cancelled];
        }
        catch (Exception ex)
        {
            StatusText = null;
            ErrorText = _errors.TranslateConnectError(ex);
        }
        finally
        {
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
        if (value is null)
        {
            return;
        }

        Domain = value.Domain ?? string.Empty;
        Server = value.Server ?? string.Empty;
        Authentication = value.Authentication;
        Security = value.Security is TransportSecurity.None ? TransportSecurity.SignAndSeal : value.Security;
        UserName = value.UserName ?? string.Empty;
        ProfileName = value.Name;
        SaveProfile = false;
        ErrorText = null;
    }

    partial void OnSelectedDomainControllerChanged(DomainControllerInfo? value)
    {
        if (value is not null)
        {
            Server = value.HostName;
        }
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
