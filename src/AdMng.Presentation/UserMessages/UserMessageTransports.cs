using System.Net;
using AdMng.Core.Messaging;
using AdMng.Presentation.Settings;
using AdMng.Watchdog.Client;

namespace AdMng.Presentation.UserMessages;

/// <summary>
/// The SMTP password of the direct mode for the application session: in memory only, per host and user name, never in
/// the settings, a log or <see cref="ToString"/> (CLAUDE.md rule 2). Cleared with <see cref="Clear"/> or when the
/// application exits. Thread-safe.
/// </summary>
public sealed class SmtpCredentialCache
{
    private readonly Lock _gate = new();
    private (string Host, string User, NetworkCredential Credential)? _entry;

    /// <summary>The cached credential of <paramref name="host"/> + <paramref name="userName"/>, or null.</summary>
    public NetworkCredential? Find(string? host, string? userName)
    {
        lock (_gate)
        {
            return _entry is { } entry
                && string.Equals(entry.Host, host?.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.User, userName?.Trim(), StringComparison.OrdinalIgnoreCase)
                ? entry.Credential
                : null;
        }
    }

    /// <summary>Keeps the password for the session (replaces any other one).</summary>
    public NetworkCredential Remember(string host, string userName, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);
        var credential = new NetworkCredential(userName.Trim(), password);
        lock (_gate)
        {
            _entry = (host.Trim(), userName.Trim(), credential);
        }

        return credential;
    }

    /// <summary>Forgets the password (e.g. after the server rejected it).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entry = null;
        }
    }

    public override string ToString() => nameof(SmtpCredentialCache);
}

/// <summary>
/// Sending through the AdMng Watchdog service – **stub of part U1b** (design note section 7). The service does not offer
/// messages to users yet: <see cref="GetStatusAsync"/> reports "not configured" without a service address and "not
/// supported" otherwise, <see cref="ConnectAsync"/> throws. U1b replaces the body: the status from a new client call
/// (service capability, allowed for Watchdog Admins and Viewers) and the send through <c>POST /api/v1/user-messages</c>.
/// </summary>
public sealed class WatchdogUserMessageTransport(IWatchdogClient? client = null) : IUserMessageTransport
{
    public UserMessageTransportKind Kind => UserMessageTransportKind.Watchdog;

    public string Description => "AdMng Watchdog";

    /// <summary>A Watchdog service address is configured.</summary>
    public bool IsConfigured => client?.IsConfigured == true;

    public Task<UserMessageTransportStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new UserMessageTransportStatus(
            false,
            IsConfigured ? UserMessageTransportStatus.NotSupported : UserMessageTransportStatus.NotConfigured));

    public Task<IUserMessageConnection> ConnectAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Sending messages to users through AdMng Watchdog arrives with U1b.");
}

/// <summary>The transport chosen for a send, or why there is none.</summary>
/// <param name="Transport">The transport, null when none can be used.</param>
/// <param name="Problem">Localization key suffix of <c>UserMessages.Transport.Problem.{Problem}</c> when there is none.</param>
/// <param name="NeedsPassword">Direct SMTP with user name + password and no password in the session yet.</param>
public sealed record UserMessageTransportChoiceResult(IUserMessageTransport? Transport, string? Problem = null, bool NeedsPassword = false)
{
    public const string SmtpNotConfigured = "SmtpNotConfigured";
    public const string SmtpUnavailable = "SmtpUnavailable";
    public const string WatchdogNotConfigured = "WatchdogNotConfigured";
    public const string WatchdogNotSupported = "WatchdogNotSupported";
}

/// <summary>
/// Chooses the transport of a send (design note section 7): the setting automatic (default) / direct SMTP / Watchdog.
/// Automatic uses the Watchdog when its connection is configured and the service reports the feature available, else
/// direct SMTP from the settings with the session's password.
/// </summary>
public sealed class UserMessageTransportSelector(
    ISettingsStore settings,
    SmtpCredentialCache credentials,
    WatchdogUserMessageTransport watchdog,
    IDirectSmtpTransportFactory? smtp = null)
{
    public SmtpCredentialCache Credentials { get; } = credentials ?? throw new ArgumentNullException(nameof(credentials));

    public async Task<UserMessageTransportChoiceResult> SelectAsync(CancellationToken cancellationToken)
    {
        var options = settings.Current.UserMessages;
        if (options.Transport != UserMessageTransportChoice.DirectSmtp)
        {
            var status = watchdog.IsConfigured
                ? await watchdog.GetStatusAsync(cancellationToken).ConfigureAwait(true)
                : new UserMessageTransportStatus(false, UserMessageTransportStatus.NotConfigured);
            if (status.IsAvailable)
            {
                return new UserMessageTransportChoiceResult(watchdog);
            }

            if (options.Transport == UserMessageTransportChoice.Watchdog)
            {
                return new UserMessageTransportChoiceResult(null, status.Reason == UserMessageTransportStatus.NotConfigured
                    ? UserMessageTransportChoiceResult.WatchdogNotConfigured
                    : UserMessageTransportChoiceResult.WatchdogNotSupported);
            }
        }

        return Smtp(options.Smtp);
    }

    /// <summary>The direct SMTP transport of <paramref name="connection"/> (the current settings or unsaved values).</summary>
    public UserMessageTransportChoiceResult Smtp(SmtpConnectionSettings connection, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (smtp is null)
        {
            return new UserMessageTransportChoiceResult(null, UserMessageTransportChoiceResult.SmtpUnavailable);
        }

        if (!connection.IsConfigured)
        {
            return new UserMessageTransportChoiceResult(null, UserMessageTransportChoiceResult.SmtpNotConfigured);
        }

        var options = connection.ToOptions();
        NetworkCredential? credential = null;
        if (options.Authentication == SmtpAuthentication.Basic)
        {
            credential = !string.IsNullOrEmpty(password) && !string.IsNullOrWhiteSpace(options.UserName)
                ? Credentials.Remember(options.Host, options.UserName, password)
                : Credentials.Find(options.Host, options.UserName);
        }

        return new UserMessageTransportChoiceResult(
            smtp.Create(options, credential),
            NeedsPassword: options.Authentication == SmtpAuthentication.Basic && credential is null);
    }
}
