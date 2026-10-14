using System.Globalization;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// The e-mail channel (<see cref="SmtpSettings"/>): server, port, TLS mode, authentication (Basic or Exchange Online OAuth2
/// client credentials), sender, certificate checks and limits. The password and the OAuth client secret are write-only
/// fields (<see cref="WatchdogSecretFieldViewModel"/>). Every edit goes to the working configuration; the service's issues
/// are shown per field (<c>Errors[host]</c>, <c>Errors[oAuth.tenantId]</c>…).
/// </summary>
public sealed partial class SmtpSettingsViewModel : ObservableObject
{
    public const string Prefix = "smtp.";

    private readonly WatchdogServices _services;
    private readonly WatchdogConfigSession _session;
    private readonly SmtpSettings _original;
    private bool _loading;

    public SmtpSettingsViewModel(WatchdogServices services, WatchdogConfigSession session)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _original = session.Working?.Smtp ?? new SmtpSettings();
        var localizer = services.Localizer;
        SecurityOptions = [.. Enum.GetValues<SmtpSecurity>().Select(s => new Option<SmtpSecurity>(s, localizer[WatchdogDisplay.SmtpSecurityKey(s)]))];
        AuthenticationOptions = [.. Enum.GetValues<SmtpAuthentication>().Select(a => new Option<SmtpAuthentication>(a, localizer[WatchdogDisplay.SmtpAuthenticationKey(a)]))];
        CloudOptions = [.. Enum.GetValues<EntraCloud>().Select(c => new Option<EntraCloud>(c, localizer[WatchdogDisplay.EntraCloudKey(c)]))];
        CertificateOptions = [.. Enum.GetValues<CertificateValidationMode>().Select(m => new Option<CertificateValidationMode>(m, localizer[WatchdogDisplay.CertificateValidationKey(m)]))];
        Password = new WatchdogSecretFieldViewModel(_original.Password, localizer, Commit);
        ClientSecret = new WatchdogSecretFieldViewModel(_original.OAuth.ClientSecret, localizer, Commit);
        _loading = true;
        Enabled = _original.Enabled;
        Host = _original.Host ?? string.Empty;
        Port = _original.Port.ToString(CultureInfo.InvariantCulture);
        Security = SecurityOptions.First(o => o.Value == _original.Security);
        Authentication = AuthenticationOptions.First(o => o.Value == _original.Authentication);
        UserName = _original.UserName ?? string.Empty;
        Cloud = CloudOptions.First(o => o.Value == _original.OAuth.Cloud);
        TenantId = _original.OAuth.TenantId ?? string.Empty;
        ClientId = _original.OAuth.ClientId ?? string.Empty;
        CertificateThumbprint = _original.OAuth.CertificateThumbprint ?? string.Empty;
        From = _original.From ?? string.Empty;
        ReplyTo = _original.ReplyTo ?? string.Empty;
        LocalDomain = _original.LocalDomain ?? string.Empty;
        CertificateValidation = CertificateOptions.First(o => o.Value == _original.CertificateValidation);
        PinnedSha256 = _original.PinnedSha256 ?? string.Empty;
        CheckRevocation = _original.CheckRevocation;
        MaxMessagesPerMinute = _original.MaxMessagesPerMinute.ToString(CultureInfo.InvariantCulture);
        MaxRecipientsPerMessage = _original.MaxRecipientsPerMessage.ToString(CultureInfo.InvariantCulture);
        TimeoutSeconds = ((int)_original.Timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        _loading = false;
        RefreshErrors();
    }

    public bool CanEdit => _session.CanEdit;

    public WatchdogFieldErrors Errors { get; } = new();

    public IReadOnlyList<Option<SmtpSecurity>> SecurityOptions { get; }

    public IReadOnlyList<Option<SmtpAuthentication>> AuthenticationOptions { get; }

    public IReadOnlyList<Option<EntraCloud>> CloudOptions { get; }

    public IReadOnlyList<Option<CertificateValidationMode>> CertificateOptions { get; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial string Host { get; set; }

    [ObservableProperty]
    public partial string Port { get; set; }

    [ObservableProperty]
    public partial Option<SmtpSecurity> Security { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBasic), nameof(IsOAuth), nameof(UsesUserName))]
    public partial Option<SmtpAuthentication> Authentication { get; set; }

    public bool IsBasic => Authentication.Value == SmtpAuthentication.Basic;

    public bool IsOAuth => Authentication.Value == SmtpAuthentication.OAuth2ClientCredentials;

    /// <summary>Basic: the login; OAuth2: the sender mailbox.</summary>
    public bool UsesUserName => Authentication.Value != SmtpAuthentication.None;

    [ObservableProperty]
    public partial string UserName { get; set; }

    public WatchdogSecretFieldViewModel Password { get; }

    [ObservableProperty]
    public partial Option<EntraCloud> Cloud { get; set; }

    [ObservableProperty]
    public partial string TenantId { get; set; }

    [ObservableProperty]
    public partial string ClientId { get; set; }

    [ObservableProperty]
    public partial string CertificateThumbprint { get; set; }

    public WatchdogSecretFieldViewModel ClientSecret { get; }

    [ObservableProperty]
    public partial string From { get; set; }

    [ObservableProperty]
    public partial string ReplyTo { get; set; }

    [ObservableProperty]
    public partial string LocalDomain { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    public partial Option<CertificateValidationMode> CertificateValidation { get; set; }

    public bool IsPinned => CertificateValidation.Value == CertificateValidationMode.PinnedSha256;

    [ObservableProperty]
    public partial string PinnedSha256 { get; set; }

    [ObservableProperty]
    public partial bool CheckRevocation { get; set; }

    [ObservableProperty]
    public partial string MaxMessagesPerMinute { get; set; }

    [ObservableProperty]
    public partial string MaxRecipientsPerMessage { get; set; }

    [ObservableProperty]
    public partial string TimeoutSeconds { get; set; }

    /// <summary>A number field holds no number (shown next to the limits).</summary>
    [ObservableProperty]
    public partial string? NumbersError { get; private set; }

    /// <summary>The settings built from the fields (unsaved; used by save and the test e-mail).</summary>
    public SmtpSettings Build() => _original with
    {
        Enabled = Enabled,
        Host = Blank(Host),
        Port = Number(Port, _original.Port),
        Security = Security.Value,
        Authentication = Authentication.Value,
        UserName = Blank(UserName),
        Password = Password.ToField(),
        OAuth = _original.OAuth with
        {
            Cloud = Cloud.Value,
            TenantId = Blank(TenantId),
            ClientId = Blank(ClientId),
            CertificateThumbprint = Blank(CertificateThumbprint),
            ClientSecret = ClientSecret.ToField(),
        },
        From = Blank(From),
        ReplyTo = Blank(ReplyTo),
        LocalDomain = Blank(LocalDomain),
        CertificateValidation = CertificateValidation.Value,
        PinnedSha256 = Blank(PinnedSha256),
        CheckRevocation = CheckRevocation,
        MaxMessagesPerMinute = Number(MaxMessagesPerMinute, _original.MaxMessagesPerMinute),
        MaxRecipientsPerMessage = Number(MaxRecipientsPerMessage, _original.MaxRecipientsPerMessage),
        Timeout = TimeSpan.FromSeconds(Number(TimeoutSeconds, (int)_original.Timeout.TotalSeconds)),
    };

    /// <summary>Shows the service's issues of the "smtp." section next to the fields.</summary>
    public void RefreshErrors()
    {
        Errors.Set(_session.IssuesFor(Prefix), Prefix);
        Password.Error = Errors["password"];
        ClientSecret.Error = Errors["oAuth.clientSecret"];
    }

    /// <summary>Focuses the secret field named by an issue path ("smtp.password" / "smtp.oAuth.clientSecret").</summary>
    public void FocusSecret(string path)
    {
        if (path.EndsWith("clientSecret", StringComparison.OrdinalIgnoreCase))
        {
            ClientSecret.Error = _services.Localizer[L.Watchdog.TestRebindRequired];
            ClientSecret.RequestFocus();
        }
        else
        {
            Password.Error = _services.Localizer[L.Watchdog.TestRebindRequired];
            Password.RequestFocus();
        }
    }

    partial void OnEnabledChanged(bool value) => Commit();

    partial void OnHostChanged(string value) => Commit();

    partial void OnPortChanged(string value) => Commit();

    partial void OnSecurityChanged(Option<SmtpSecurity> value) => Commit();

    partial void OnAuthenticationChanged(Option<SmtpAuthentication> value) => Commit();

    partial void OnUserNameChanged(string value) => Commit();

    partial void OnCloudChanged(Option<EntraCloud> value) => Commit();

    partial void OnTenantIdChanged(string value) => Commit();

    partial void OnClientIdChanged(string value) => Commit();

    partial void OnCertificateThumbprintChanged(string value) => Commit();

    partial void OnFromChanged(string value) => Commit();

    partial void OnReplyToChanged(string value) => Commit();

    partial void OnLocalDomainChanged(string value) => Commit();

    partial void OnCertificateValidationChanged(Option<CertificateValidationMode> value) => Commit();

    partial void OnPinnedSha256Changed(string value) => Commit();

    partial void OnCheckRevocationChanged(bool value) => Commit();

    partial void OnMaxMessagesPerMinuteChanged(string value) => Commit();

    partial void OnMaxRecipientsPerMessageChanged(string value) => Commit();

    partial void OnTimeoutSecondsChanged(string value) => Commit();

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static int Number(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool IsNumber(string text) => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private void Commit()
    {
        if (_loading || !CanEdit)
        {
            return;
        }

        NumbersError = new[] { Port, MaxMessagesPerMinute, MaxRecipientsPerMessage, TimeoutSeconds }.All(IsNumber)
            ? null
            : _services.Localizer[WatchdogDisplay.ValidationKey("range")];
        var smtp = Build();
        _session.Update(c => c with { Smtp = smtp });
    }
}
