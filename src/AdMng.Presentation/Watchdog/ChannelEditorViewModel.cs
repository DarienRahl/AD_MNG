using System.Globalization;
using System.Security.Cryptography;
using AdMng.Presentation.Infrastructure;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Services;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// A Teams (Power Automate Workflows) or generic webhook channel: name, id, enabled, the write-only URL (only its hint is
/// shown), detail level, Teams authentication (SAS or Entra client credentials with tenant, client, certificate or secret
/// and one of the cloud's documented scopes), webhook signing secret ("Generuj": <c>whsec_</c> + 32 random bytes created
/// here and shown once for copying into the receiver), previous secret for rotation, GitHub signature, allowed hosts,
/// timeout, heartbeat and rate limits, and a test send with the unsaved settings.
/// </summary>
public sealed partial class ChannelEditorViewModel : ObservableObject
{
    /// <summary>Random bytes of a generated signing secret (Standard Webhooks).</summary>
    public const int GeneratedSecretBytes = 32;

    private readonly WatchdogServices _services;
    private readonly WatchdogConfigSession _session;
    private readonly ChannelSettings _original;
    private bool _loading;

    public ChannelEditorViewModel(WatchdogServices services, WatchdogConfigSession session, int index)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Index = index;
        _original = session.Working?.Channels[index] ?? throw new InvalidOperationException("The configuration is not loaded.");
        var localizer = services.Localizer;
        DetailLevels = [.. Enum.GetValues<DetailLevel>().Select(d => new Option<DetailLevel>(d, localizer[WatchdogDisplay.DetailLevelKey(d)]))];
        AuthModes = [.. Enum.GetValues<TeamsAuthMode>().Select(m => new Option<TeamsAuthMode>(m, localizer[WatchdogDisplay.TeamsAuthModeKey(m)]))];
        Clouds = [.. Enum.GetValues<EntraCloud>().Select(c => new Option<EntraCloud>(c, localizer[WatchdogDisplay.EntraCloudKey(c)]))];
        var teams = _original.Teams ?? new TeamsChannelOptions();
        var webhook = _original.Webhook ?? new WebhookChannelOptions();
        Url = new WatchdogSecretFieldViewModel(_original.Url, localizer, Commit);
        ClientSecret = new WatchdogSecretFieldViewModel(teams.ClientSecret, localizer, Commit);
        SigningSecret = new WatchdogSecretFieldViewModel(webhook.SigningSecret, localizer, Commit);
        PreviousSigningSecret = new WatchdogSecretFieldViewModel(webhook.PreviousSigningSecret, localizer, Commit);
        Test = new DeliveryTestViewModel(services);
        Test.SecretRebindRequired += OnSecretRebindRequired;
        _loading = true;
        Id = _original.Id;
        Name = _original.Name;
        Enabled = _original.Enabled;
        DetailLevel = DetailLevels.First(d => d.Value == _original.DetailLevel);
        AuthMode = AuthModes.First(m => m.Value == teams.AuthMode);
        Cloud = Clouds.First(c => c.Value == teams.Cloud);
        Scopes = EntraCloudInfo.TeamsScopes(teams.Cloud);
        Scope = teams.Scope;
        TenantId = teams.TenantId ?? string.Empty;
        TeamsClientId = teams.ClientId ?? string.Empty;
        CertificateThumbprint = teams.CertificateThumbprint ?? string.Empty;
        SendGitHubSignature = webhook.SendGitHubSignature;
        AllowedHosts = string.Join(Environment.NewLine, webhook.AllowedHosts);
        TimeoutSeconds = ((int)webhook.Timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        IncludeDigest = webhook.IncludeDigest;
        HeartbeatMinutes = webhook.HeartbeatInterval is { } heartbeat ? ((int)heartbeat.TotalMinutes).ToString(CultureInfo.InvariantCulture) : string.Empty;
        PerSecond = _original.RateLimit.PerSecond.ToString(CultureInfo.InvariantCulture);
        PerMinute = _original.RateLimit.PerMinute.ToString(CultureInfo.InvariantCulture);
        PerHour = _original.RateLimit.PerHour.ToString(CultureInfo.InvariantCulture);
        _loading = false;
        RefreshErrors();
    }

    /// <summary>The channel was edited (the channel list follows).</summary>
    public event EventHandler? Changed;

    /// <summary>Position in <c>configuration.channels</c>.</summary>
    public int Index { get; }

    public ChannelType Type => _original.Type;

    public bool IsTeams => Type == ChannelType.Teams;

    public bool IsWebhook => Type == ChannelType.Webhook;

    public string TypeText => _services.Localizer.ChannelTypeName(WatchdogDisplay.ToDeliveryType(Type));

    public bool CanEdit => _session.CanEdit;

    public WatchdogFieldErrors Errors { get; } = new();

    public IReadOnlyList<Option<DetailLevel>> DetailLevels { get; }

    public IReadOnlyList<Option<TeamsAuthMode>> AuthModes { get; }

    public IReadOnlyList<Option<EntraCloud>> Clouds { get; }

    [ObservableProperty]
    public partial string Id { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    public partial bool Enabled { get; set; }

    /// <summary>"Teams – Administratorzy IT (Teams)" for the channel list.</summary>
    public string DisplayText => Enabled ? $"{Name} ({TypeText})" : $"{Name} ({TypeText}, {_services.Localizer[L.Watchdog.ChannelDisabled]})";

    /// <summary>The trigger / endpoint URL (a secret: the <c>sig</c> parameter of a Teams URL is a bearer credential).</summary>
    public WatchdogSecretFieldViewModel Url { get; }

    [ObservableProperty]
    public partial Option<DetailLevel> DetailLevel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEntraAuth))]
    public partial Option<TeamsAuthMode> AuthMode { get; set; }

    public bool IsEntraAuth => IsTeams && AuthMode.Value == TeamsAuthMode.EntraClientCredentials;

    [ObservableProperty]
    public partial Option<EntraCloud> Cloud { get; set; }

    /// <summary>The documented Power Automate scopes of <see cref="Cloud"/> (no free text).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Scopes { get; private set; }

    [ObservableProperty]
    public partial string Scope { get; set; }

    [ObservableProperty]
    public partial string TenantId { get; set; }

    [ObservableProperty]
    public partial string TeamsClientId { get; set; }

    [ObservableProperty]
    public partial string CertificateThumbprint { get; set; }

    public WatchdogSecretFieldViewModel ClientSecret { get; }

    public WatchdogSecretFieldViewModel SigningSecret { get; }

    public WatchdogSecretFieldViewModel PreviousSigningSecret { get; }

    /// <summary>The secret generated by "Generuj", shown once for copying into the receiver (gone after save).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGeneratedSecret))]
    [NotifyCanExecuteChangedFor(nameof(CopyGeneratedSecretCommand))]
    public partial string? GeneratedSecret { get; private set; }

    public bool HasGeneratedSecret => GeneratedSecret is not null;

    [ObservableProperty]
    public partial bool SendGitHubSignature { get; set; }

    /// <summary>Hosts the webhook may post to, one per line (empty = any https host).</summary>
    [ObservableProperty]
    public partial string AllowedHosts { get; set; }

    [ObservableProperty]
    public partial string TimeoutSeconds { get; set; }

    [ObservableProperty]
    public partial bool IncludeDigest { get; set; }

    /// <summary>Minutes between "watchdog.heartbeat" posts (≥ 5); empty = off.</summary>
    [ObservableProperty]
    public partial string HeartbeatMinutes { get; set; }

    [ObservableProperty]
    public partial string PerSecond { get; set; }

    [ObservableProperty]
    public partial string PerMinute { get; set; }

    [ObservableProperty]
    public partial string PerHour { get; set; }

    [ObservableProperty]
    public partial string? NumbersError { get; private set; }

    /// <summary>The test send of this channel.</summary>
    public DeliveryTestViewModel Test { get; }

    /// <summary>The channel built from the fields (unsaved; used by save and the test).</summary>
    public ChannelSettings Build()
    {
        var teams = _original.Teams ?? new TeamsChannelOptions();
        var webhook = _original.Webhook ?? new WebhookChannelOptions();
        return _original with
        {
            Id = Id.Trim(),
            Name = Name.Trim(),
            Enabled = Enabled,
            Url = Url.ToField(),
            DetailLevel = DetailLevel.Value,
            Teams = IsTeams
                ? teams with
                {
                    AuthMode = AuthMode.Value,
                    Cloud = Cloud.Value,
                    TenantId = Blank(TenantId),
                    ClientId = Blank(TeamsClientId),
                    CertificateThumbprint = Blank(CertificateThumbprint),
                    ClientSecret = ClientSecret.ToField(),
                    Scope = Scope,
                }
                : _original.Teams,
            Webhook = IsWebhook
                ? webhook with
                {
                    SigningSecret = SigningSecret.ToField(),
                    PreviousSigningSecret = PreviousSigningSecret.ToField(),
                    SendGitHubSignature = SendGitHubSignature,
                    AllowedHosts = [.. AllowedHosts.Split([',', ';', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                    Timeout = TimeSpan.FromSeconds(Number(TimeoutSeconds, (int)webhook.Timeout.TotalSeconds)),
                    IncludeDigest = IncludeDigest,
                    HeartbeatInterval = int.TryParse(HeartbeatMinutes.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ? TimeSpan.FromMinutes(minutes) : null,
                }
                : _original.Webhook,
            RateLimit = new RateLimitSettings
            {
                PerSecond = Number(PerSecond, _original.RateLimit.PerSecond),
                PerMinute = Number(PerMinute, _original.RateLimit.PerMinute),
                PerHour = Number(PerHour, _original.RateLimit.PerHour),
            },
        };
    }

    /// <summary>Shows the service's issues of this channel next to its fields.</summary>
    public void RefreshErrors()
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"channels[{Index}].");
        Errors.Set(_session.IssuesFor(prefix), prefix);
        Url.Error = Errors["url"];
        ClientSecret.Error = Errors["teams.clientSecret"];
        SigningSecret.Error = Errors["webhook.signingSecret"];
        PreviousSigningSecret.Error = Errors["webhook.previousSigningSecret"];
    }

    /// <summary>"Generuj": a new <c>whsec_</c> secret (32 random bytes), set as the new signing secret and shown once.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void GenerateSecret()
    {
        var secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(GeneratedSecretBytes));
        GeneratedSecret = secret;
        SigningSecret.SetNewValue(secret);
    }

    /// <summary>Copies the generated secret (clipboard history excluded, like passwords).</summary>
    [RelayCommand(CanExecute = nameof(HasGeneratedSecret))]
    private void CopyGeneratedSecret()
    {
        if (GeneratedSecret is { } secret && _services.Ui.Clipboard.SetSecret(secret))
        {
            _services.Ui.Notifications.Show(NotificationSeverity.Info, _services.Localizer[L.Fields.Copied]);
        }
    }

    /// <summary>Test send with the unsaved settings of this channel.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private Task TestAsync()
    {
        var channel = Build();
        return Test.RunAsync(token => _services.Client.SendTestChannelAsync(channel.Id, new TestChannelRequest { Settings = channel }, token));
    }

    private bool CanTest() => _session.IsAdmin && !_session.IsRedacted;

    partial void OnIdChanged(string value) => Commit();

    partial void OnNameChanged(string value) => Commit();

    partial void OnEnabledChanged(bool value) => Commit();

    partial void OnDetailLevelChanged(Option<DetailLevel> value) => Commit();

    partial void OnAuthModeChanged(Option<TeamsAuthMode> value) => Commit();

    partial void OnCloudChanged(Option<EntraCloud> value)
    {
        Scopes = EntraCloudInfo.TeamsScopes(value.Value);
        if (!_loading && !Scopes.Contains(Scope, StringComparer.Ordinal))
        {
            Scope = Scopes[0];
        }

        Commit();
    }

    partial void OnScopeChanged(string value) => Commit();

    partial void OnTenantIdChanged(string value) => Commit();

    partial void OnTeamsClientIdChanged(string value) => Commit();

    partial void OnCertificateThumbprintChanged(string value) => Commit();

    partial void OnSendGitHubSignatureChanged(bool value) => Commit();

    partial void OnAllowedHostsChanged(string value) => Commit();

    partial void OnTimeoutSecondsChanged(string value) => Commit();

    partial void OnIncludeDigestChanged(bool value) => Commit();

    partial void OnHeartbeatMinutesChanged(string value) => Commit();

    partial void OnPerSecondChanged(string value) => Commit();

    partial void OnPerMinuteChanged(string value) => Commit();

    partial void OnPerHourChanged(string value) => Commit();

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static int Number(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void OnSecretRebindRequired(object? sender, string path)
    {
        var message = _services.Localizer[L.Watchdog.TestRebindRequired];
        var secret = path.EndsWith("clientSecret", StringComparison.OrdinalIgnoreCase) ? ClientSecret
            : path.EndsWith("previousSigningSecret", StringComparison.OrdinalIgnoreCase) ? PreviousSigningSecret
            : path.EndsWith("signingSecret", StringComparison.OrdinalIgnoreCase) ? SigningSecret
            : Url;
        secret.Error = message;
        secret.RequestFocus();
    }

    private void Commit()
    {
        if (_loading || !CanEdit)
        {
            return;
        }

        var numbers = new[] { TimeoutSeconds, PerSecond, PerMinute, PerHour }.Concat(HeartbeatMinutes.Trim().Length > 0 ? [HeartbeatMinutes] : []);
        NumbersError = numbers.All(t => int.TryParse(t.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            ? null
            : _services.Localizer[WatchdogDisplay.ValidationKey("range")];
        var channel = Build();
        var index = Index;
        _session.Update(c => c with { Channels = [.. c.Channels.Select((ch, i) => i == index ? channel : ch)] });
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
