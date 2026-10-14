using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Notifications;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// Everything a formatter needs besides the events: language, time zone, detail level and the presentation settings of
/// the configuration (subject prefix, instance name, public base URL, value cap).
/// </summary>
public sealed class NotificationContext
{
    private NotificationContext(WatchdogConfiguration configuration, DetailLevel detailLevel)
    {
        Configuration = configuration;
        DetailLevel = detailLevel;
        Text = NotificationText.For(configuration.General?.Language);
        Time = new TimeFormatter(BusinessCalendar.ResolveTimeZone(configuration), Text.IsPolish);
        Attributes = new AttributeDisplay(Text, Time);
        Objects = new ObjectDisplay(Text, Time);
    }

    public WatchdogConfiguration Configuration { get; }

    public DetailLevel DetailLevel { get; }

    public NotificationText Text { get; }

    public TimeFormatter Time { get; }

    public AttributeDisplay Attributes { get; }

    public ObjectDisplay Objects { get; }

    /// <summary><c>Notifications.SubjectPrefix</c> (default "[AD Watchdog]").</summary>
    public string SubjectPrefix => UntrustedText.ForHeader(Configuration.Notifications?.SubjectPrefix ?? "[AD Watchdog]");

    /// <summary><c>General.InstanceName</c>.</summary>
    public string InstanceName => UntrustedText.Sanitize(Configuration.General?.InstanceName ?? "AD Watchdog");

    /// <summary><c>Notifications.MaxValueLength</c>.</summary>
    public int MaxValueLength => Configuration.Notifications?.MaxValueLength ?? 200;

    /// <summary>The service shown in footers: the host of <c>PublicBaseUrl</c>, else the instance name.</summary>
    public string ServiceName => PublicBaseUri?.Host ?? InstanceName;

    /// <summary><c>General.PublicBaseUrl</c> when it is an absolute https (or http) URL; null = no links.</summary>
    public Uri? PublicBaseUri =>
        Uri.TryCreate(Configuration.General?.PublicBaseUrl?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    /// <summary>The context of <paramref name="configuration"/> at <paramref name="detailLevel"/>.</summary>
    public static NotificationContext Create(WatchdogConfiguration configuration, DetailLevel detailLevel)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new NotificationContext(configuration, detailLevel);
    }

    /// <summary>The HTML event page link of an event (<c>/events/{eventId}</c> under <c>PublicBaseUrl</c>), or null.</summary>
    public string? EventLink(Guid eventId) =>
        PublicBaseUri is { } baseUri ? new Uri(baseUri, WatchdogRoutes.EventPagePath(eventId)).AbsoluteUri : null;

    /// <summary>The service root link, or null.</summary>
    public string? ServiceLink => PublicBaseUri?.AbsoluteUri;
}
