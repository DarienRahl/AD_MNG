using System.Collections.ObjectModel;
using AdMng.Core.Correspondence.Outlook;
using AdMng.Core.Messaging;
using AdMng.Presentation.Correspondence;
using AdMng.Presentation.Dialogs;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Presentation.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.UserMessages;

/// <summary>
/// The "Wiadomości e-mail" tab of the settings window (README U1): sender, reply-to, CC, test address, default language,
/// rate limit, daily budget, confirmation threshold, allowed link domains, transport and the direct SMTP connection, with
/// "Testuj połączenie" and "Wyślij wiadomość testową do mnie" on the values typed (saved or not). The SMTP password is read
/// from the window's box only for a test and then kept in the session memory – never in the settings. The transport
/// Outlook (U5) adds the account of the Outlook profile ("Pobierz konta z Outlooka" connects to Outlook to list them) and
/// "w imieniu nadawcy".
/// </summary>
public sealed partial class UserMessageSettingsViewModel : ObservableObject
{
    private readonly DialogServices _services;
    private readonly UserMessageServices _messages;

    public UserMessageSettingsViewModel(DialogServices services, UserMessageServices messages)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        var l = services.Localizer;
        Languages = [new("pl", l[L.UserMessages.LanguagePolish]), new("en", l[L.UserMessages.LanguageEnglish])];
        TransportChoices = [.. Enum.GetValues<UserMessageTransportChoice>().Select(c => new Option<UserMessageTransportChoice>(c, l[UserMessageText.TransportKey(c)]))];
        TlsModes = [.. Enum.GetValues<SmtpTlsMode>().Select(m => new Option<SmtpTlsMode>(m, l[UserMessageText.TlsKey(m)]))];
        CertificateChecks = [.. Enum.GetValues<SmtpCertificateCheck>().Select(c => new Option<SmtpCertificateCheck>(c, l[UserMessageText.CertificateKey(c)]))];
        Authentications = [.. Enum.GetValues<SmtpAuthentication>().Select(a => new Option<SmtpAuthentication>(a, l[UserMessageText.AuthKey(a)]))];

        var current = services.Settings.Current.UserMessages;
        FromAddress = current.FromAddress ?? string.Empty;
        FromName = current.FromName ?? string.Empty;
        ReplyTo = current.ReplyTo ?? string.Empty;
        Cc = current.Cc ?? string.Empty;
        TestAddress = current.TestAddress ?? string.Empty;
        Language = Languages.First(o => o.Value == current.DefaultLanguage);
        MessagesPerMinute = current.MessagesPerMinute;
        RecipientsPerDay = current.RecipientsPerDay;
        ConfirmationThreshold = current.ConfirmationThreshold;
        AllowedDomainsText = string.Join(Environment.NewLine, current.AllowedLinkDomains);
        Transport = TransportChoices.First(o => o.Value == current.Transport);
        OutlookAccount = current.OutlookAccount ?? string.Empty;
        OutlookOnBehalfOfSender = current.OutlookOnBehalfOfSender;
        if (OutlookAccount.Length > 0)
        {
            OutlookAccounts.Add(OutlookAccount);
        }

        var smtp = current.Smtp;
        SmtpHost = smtp.Host ?? string.Empty;
        SmtpPort = smtp.Port;
        Tls = TlsModes.First(o => o.Value == smtp.Tls);
        InsecureTlsConfirmed = smtp.InsecureTlsConfirmed;
        Certificate = CertificateChecks.First(o => o.Value == smtp.Certificate);
        PinnedSha256 = smtp.PinnedSha256 ?? string.Empty;
        Authentication = Authentications.First(o => o.Value == smtp.Authentication);
        SmtpUserName = smtp.UserName ?? string.Empty;
        LocalDomain = smtp.LocalDomain ?? string.Empty;
        Salutations = new SalutationSettingsViewModel(l, current.Salutations ?? new());
        Salutations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SalutationSettingsViewModel.Error))
            {
                OnPropertyChanged(nameof(Error));
            }
        };
    }

    /// <summary>The "Zwroty grzecznościowe" section (U1d).</summary>
    public SalutationSettingsViewModel Salutations { get; }

    public IReadOnlyList<Option<string>> Languages { get; }

    public IReadOnlyList<Option<UserMessageTransportChoice>> TransportChoices { get; }

    public IReadOnlyList<Option<SmtpTlsMode>> TlsModes { get; }

    public IReadOnlyList<Option<SmtpCertificateCheck>> CertificateChecks { get; }

    public IReadOnlyList<Option<SmtpAuthentication>> Authentications { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string FromAddress { get; set; }

    [ObservableProperty]
    public partial string FromName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string ReplyTo { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string Cc { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyCanExecuteChangedFor(nameof(SendTestMessageCommand))]
    public partial string TestAddress { get; set; }

    [ObservableProperty]
    public partial Option<string> Language { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial int MessagesPerMinute { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial int RecipientsPerDay { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial int ConfirmationThreshold { get; set; }

    /// <summary>Allowed link domains, one per line (commas and spaces separate too).</summary>
    [ObservableProperty]
    public partial string AllowedDomainsText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutlookTransport))]
    public partial Option<UserMessageTransportChoice> Transport { get; set; }

    /// <summary>The transport Outlook is chosen (the account box and "w imieniu nadawcy" are shown).</summary>
    public bool IsOutlookTransport => Transport?.Value == UserMessageTransportChoice.Outlook;

    /// <summary>The SMTP address of the Outlook profile account to send from; empty: the profile's default account.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string OutlookAccount { get; set; }

    /// <summary>Put the sender address (<see cref="FromAddress"/>) into "w imieniu" (SentOnBehalfOfName).</summary>
    [ObservableProperty]
    public partial bool OutlookOnBehalfOfSender { get; set; }

    /// <summary>The SMTP addresses of the Outlook profile's accounts (after "Pobierz konta z Outlooka"; the saved account
    /// before).</summary>
    public ObservableCollection<string> OutlookAccounts { get; } = [];

    /// <summary>"Pobierz konta z Outlooka" is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadOutlookAccountsCommand))]
    public partial bool IsLoadingOutlookAccounts { get; private set; }

    /// <summary>The outcome of the last "Pobierz konta z Outlooka", or null.</summary>
    [ObservableProperty]
    public partial string? OutlookAccountsText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand), nameof(SendTestMessageCommand))]
    public partial string SmtpHost { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial int SmtpPort { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInsecureTls), nameof(Error))]
    public partial Option<SmtpTlsMode> Tls { get; set; }

    /// <summary>STARTTLS when offered / no TLS: messages (and with a relay possibly more) travel unencrypted.</summary>
    public bool IsInsecureTls => Tls.Value is SmtpTlsMode.None or SmtpTlsMode.StartTlsWhenAvailable;

    /// <summary>The operator confirmed the warning of an insecure TLS mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial bool InsecureTlsConfirmed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinned), nameof(Error))]
    public partial Option<SmtpCertificateCheck> Certificate { get; set; }

    public bool IsPinned => Certificate.Value == SmtpCertificateCheck.PinnedSha256;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string PinnedSha256 { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesUserName), nameof(Error))]
    public partial Option<SmtpAuthentication> Authentication { get; set; }

    public bool UsesUserName => Authentication.Value == SmtpAuthentication.Basic;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string SmtpUserName { get; set; }

    [ObservableProperty]
    public partial string LocalDomain { get; set; }

    /// <summary>The settings window's SMTP password box (read only for a test; kept in the session memory afterwards).</summary>
    public IPasswordInput? SmtpPasswordInput { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand), nameof(SendTestMessageCommand))]
    public partial bool IsTesting { get; private set; }

    /// <summary>Outcome of the last connection test or test message.</summary>
    [ObservableProperty]
    public partial string? TestResult { get; private set; }

    [ObservableProperty]
    public partial bool TestSucceeded { get; private set; }

    /// <summary>The SHA-256 of the certificate the server presented in the last test (offered for pinning).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PinSeenCertificateCommand))]
    public partial string? SeenCertificate { get; private set; }

    /// <summary>Localized validation error of the tab, or null.</summary>
    public string? Error
    {
        get
        {
            var l = _services.Localizer;
            string? Address(string label, string value) =>
                value.Trim().Length > 0 && !UserMessageRules.TryParseAddress(value, out _) ? l.Format(L.UserMessages.InvalidAddress, l[label]) : null;
            string? Range(string label, int value, int min, int max) =>
                value < min || value > max ? l.Format(L.Settings.OutOfRange, l[label], min, max) : null;
            return Address(L.UserMessages.FromAddress, FromAddress)
                ?? Address(L.UserMessages.ReplyToLabel, ReplyTo)
                ?? Address(L.UserMessages.CcLabel, Cc)
                ?? Address(L.UserMessages.TestAddress, TestAddress)
                ?? Address(L.UserMessagesOutlook.Account, OutlookAccount)
                ?? Range(L.UserMessages.MessagesPerMinute, MessagesPerMinute, 1, 600)
                ?? Range(L.UserMessages.RecipientsPerDay, RecipientsPerDay, 1, 1_000_000)
                ?? Range(L.UserMessages.MessageThreshold, ConfirmationThreshold, 1, 100_000)
                ?? Range(L.UserMessages.SmtpPort, SmtpPort, 1, 65535)
                ?? (IsInsecureTls && !InsecureTlsConfirmed ? l[L.UserMessages.InsecureNotConfirmed] : null)
                ?? (IsPinned && DirectSmtpOptions.NormalizePin(PinnedSha256) is null ? l[L.UserMessages.InvalidPin] : null)
                ?? (UsesUserName && SmtpHost.Trim().Length > 0 && SmtpUserName.Trim().Length == 0 ? l[L.UserMessages.UserNameRequired] : null)
                ?? Salutations.Error;
        }
    }

    /// <summary>Writes the values into the settings (called by Save of the settings window).</summary>
    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var target = settings.UserMessages;
        target.FromAddress = Clean(FromAddress);
        target.FromName = Clean(FromName);
        target.ReplyTo = Clean(ReplyTo);
        target.Cc = Clean(Cc);
        target.TestAddress = Clean(TestAddress);
        target.DefaultLanguage = Language.Value;
        target.MessagesPerMinute = MessagesPerMinute;
        target.RecipientsPerDay = RecipientsPerDay;
        target.ConfirmationThreshold = ConfirmationThreshold;
        target.AllowedLinkDomains = [.. Domains()];
        target.Transport = Transport.Value;
        target.OutlookAccount = Clean(OutlookAccount);
        target.OutlookOnBehalfOfSender = OutlookOnBehalfOfSender;
        target.Smtp = Connection();
        Salutations.Apply(target);
        target.Normalize();
    }

    /// <summary>"Testuj połączenie": connect, TLS and authentication with the typed values (nothing is sent).</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        if (CreateTransport() is not IDirectSmtpTransport transport)
        {
            return;
        }

        IsTesting = true;
        TestResult = _services.Localizer[L.UserMessages.Testing];
        try
        {
            var result = await Task.Run(() => transport.TestConnectionAsync(CancellationToken.None)).ConfigureAwait(true);
            SeenCertificate = result.CertificateSha256;
            TestSucceeded = result.Success;
            TestResult = Describe(result);
            if (result.Failure?.Outcome == UserMessageDeliveryOutcome.AuthenticationFailed)
            {
                _messages.Transports.Credentials.Clear();
            }
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>"Wyślij wiadomość testową do mnie": a short sample message to the test address over the typed connection.</summary>
    [RelayCommand(CanExecute = nameof(CanSendTestMessage))]
    private async Task SendTestMessageAsync()
    {
        if (!UserMessageRules.TryParseAddress(TestAddress, out var address) || CreateTransport() is not { } transport)
        {
            return;
        }

        var l = _services.Localizer;
        var language = MessageLanguages.FromCode(Language.Value) ?? MessageLanguage.Polish;
        var domain = _services.Sessions.Current?.Info.DomainDnsName;
        var from = UserMessageRules.TryParseAddress(FromAddress, out var fromAddress)
            ? fromAddress
            : string.IsNullOrWhiteSpace(domain) ? address : "noreply@" + domain.ToLowerInvariant();
        var footer = BuiltInMessageTemplates.Footer(language);
        var body = l[L.UserMessages.SampleBody];
        var message = new OutgoingUserMessage
        {
            Id = Guid.NewGuid(),
            From = new MessageAddress(from, Clean(FromName) ?? BuiltInMessageTemplates.SenderName(language)),
            To = [new MessageAddress(address)],
            ReplyTo = UserMessageRules.TryParseAddress(ReplyTo, out var reply) ? new MessageAddress(reply) : null,
            Subject = l[L.UserMessages.SampleSubject],
            TextBody = UserMessageRules.TextDocument(body, footer),
            HtmlBody = UserMessageRules.HtmlDocument(UserMessageRules.TextToHtml(body), footer, language),
            Language = language,
        };
        var planned = new PlannedUserMessage
        {
            Index = 0,
            DistinguishedName = string.Empty,
            Name = address,
            Status = PlannedMessageStatus.Ready,
            Message = message,
        };
        IsTesting = true;
        TestResult = l[L.UserMessages.TestSending];
        try
        {
            _messages.RateLimiter.MessagesPerMinute = MessagesPerMinute is >= 1 and <= 600 ? MessagesPerMinute : MessageRateLimiter.DefaultMessagesPerMinute;
            var results = await Task.Run(() => _messages.Dispatcher.SendAsync(transport, [planned], _messages.RateLimiter, _messages.DispatchOptions, null, CancellationToken.None)).ConfigureAwait(true);
            TestSucceeded = results[0].Status == UserMessageSendStatus.Accepted;
            TestResult = TestSucceeded
                ? l.Format(L.UserMessages.TestSent, address)
                : l.Format(L.UserMessages.TestFailed, UserMessageText.ResultDetail(l, results[0]));
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>
    /// "Pobierz konta z Outlooka" (U5): connects to classic Outlook (attaches to the running one or starts it), lists the
    /// SMTP addresses of the profile's accounts and ends the session. Only with Outlook installed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadOutlookAccounts))]
    private async Task LoadOutlookAccountsAsync()
    {
        if (_messages.Transports.Outlook is not { } outlook)
        {
            return;
        }

        var l = _services.Localizer;
        IsLoadingOutlookAccounts = true;
        OutlookAccountsText = l[L.UserMessagesOutlook.LoadingAccounts];
        try
        {
            OutlookConnectResult result;
            try
            {
                result = await outlook.ConnectAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (OutlookException ex)
            {
                result = new OutlookConnectResult(null, ex.Failure);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A client that breaks its contract: "unknown" with its HRESULT, never the message (it may hold an address).
                result = new OutlookConnectResult(null, new OutlookFailure(OutlookFailureKind.Unknown, OutlookFailureCodes.Unknown, "connect", ex.HResult));
            }

            if (result.Session is not { } session)
            {
                var failure = result.Failure ?? new OutlookFailure(OutlookFailureKind.Unknown, OutlookFailureCodes.Unknown, null, null);
                OutlookAccountsText = l.Format(L.UserMessagesOutlook.AccountsFailed, CorrespondenceText.FailureText(l, failure));
                return;
            }

            await using (session.ConfigureAwait(true))
            {
                var addresses = session.Profile.Accounts
                    .Select(a => a?.SmtpAddress?.Trim())
                    .Where(a => !string.IsNullOrEmpty(a))
                    .Select(a => a!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var chosen = OutlookAccount;
                OutlookAccounts.Clear();
                foreach (var address in addresses)
                {
                    OutlookAccounts.Add(address);
                }

                if (chosen.Trim().Length > 0 && !addresses.Contains(chosen.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    OutlookAccounts.Add(chosen.Trim());   // kept: it may be an account of another profile
                }

                OutlookAccount = chosen;
                OutlookAccountsText = addresses.Count == 0
                    ? l[L.UserMessagesOutlook.NoAccounts]
                    : l.Format(L.UserMessagesOutlook.AccountsLoaded, session.Profile.ProfileName, string.Join(", ", addresses));
            }
        }
        finally
        {
            IsLoadingOutlookAccounts = false;
        }
    }

    private bool CanLoadOutlookAccounts() =>
        !IsLoadingOutlookAccounts && _messages.Transports.Outlook?.Availability == OutlookAvailability.Installed;

    /// <summary>Copies the certificate seen in the last test into the pin (and switches to pinning).</summary>
    [RelayCommand(CanExecute = nameof(HasSeenCertificate))]
    private void PinSeenCertificate()
    {
        PinnedSha256 = SeenCertificate ?? string.Empty;
        Certificate = CertificateChecks.First(c => c.Value == SmtpCertificateCheck.PinnedSha256);
    }

    private bool HasSeenCertificate() => SeenCertificate is not null;

    private bool CanTest() => !IsTesting && SmtpHost.Trim().Length > 0;

    private bool CanSendTestMessage() => CanTest() && UserMessageRules.TryParseAddress(TestAddress, out _);

    /// <summary>The direct SMTP transport of the typed values (the password from the box goes to the session memory).</summary>
    private IUserMessageTransport? CreateTransport()
    {
        if (Error is { } error)
        {
            TestSucceeded = false;
            TestResult = error;
            return null;
        }

        var password = UsesUserName ? SmtpPasswordInput?.GetPassword() : null;
        var choice = _messages.Transports.Smtp(Connection(), password);
        if (!string.IsNullOrEmpty(password))
        {
            SmtpPasswordInput?.Clear();
        }

        if (choice.Transport is null || choice.NeedsPassword)
        {
            TestSucceeded = false;
            TestResult = choice.NeedsPassword
                ? _services.Localizer[L.UserMessages.PasswordRequired]
                : _services.Localizer[UserMessageText.ProblemKey(choice.Problem ?? UserMessageTransportChoiceResult.SmtpNotConfigured)];
            return null;
        }

        return choice.Transport;
    }

    private string Describe(SmtpConnectionTest result)
    {
        var l = _services.Localizer;
        var lines = new List<string>();
        if (result.Success)
        {
            lines.Add(l.Format(
                L.UserMessages.TestConnectionOk,
                result.IsEncrypted ? l.Format(L.UserMessages.Encrypted, result.TlsProtocol ?? string.Empty) : l[L.UserMessages.NotEncrypted],
                result.AuthenticatedWith ?? l[UserMessageText.AuthKey(SmtpAuthentication.None)]));
        }
        else
        {
            lines.Add(l.Format(L.UserMessages.TestConnectionFailed, l[UserMessageText.StageKey(result.Stage)], UserMessageText.Delivery(l, result.Failure!)));
        }

        if (result.AuthenticationMechanisms.Count > 0)
        {
            lines.Add(l.Format(L.UserMessages.Mechanisms, string.Join(", ", result.AuthenticationMechanisms)));
        }

        if (result.CertificateSha256 is { } sha)
        {
            lines.Add(l.Format(L.UserMessages.CertificateSeen, sha));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private SmtpConnectionSettings Connection() => new()
    {
        Host = Clean(SmtpHost),
        Port = SmtpPort,
        Tls = Tls.Value,
        InsecureTlsConfirmed = IsInsecureTls && InsecureTlsConfirmed,
        Certificate = Certificate.Value,
        PinnedSha256 = Clean(PinnedSha256),
        Authentication = Authentication.Value,
        UserName = Clean(SmtpUserName),
        LocalDomain = Clean(LocalDomain),
    };

    private IEnumerable<string> Domains() =>
        (AllowedDomainsText ?? string.Empty)
            .Split(['\r', '\n', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(UserMessageRules.NormalizeDomain)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.Ordinal);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
