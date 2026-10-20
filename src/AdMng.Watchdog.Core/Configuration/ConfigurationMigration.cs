using AdMng.Watchdog.Contracts.Configuration;

namespace AdMng.Watchdog.Core.Configuration;

/// <summary>
/// Migration of a stored runtime configuration to <see cref="WatchdogConfiguration.CurrentSchemaVersion"/> on the Linux
/// service (stage B design 7.8): a version 1 document (the Windows service 0.2.0) whose attribution source is a Windows
/// source (DomainControllers, ForwardedEvents) gets <see cref="AttributionSourceKind.OpenWec"/>; the service saves it
/// once with the audit summary <see cref="LinuxSummary"/>.
/// </summary>
public static class ConfigurationMigration
{
    /// <summary>The configuration audit summary of the migration.</summary>
    public const string LinuxSummary = "migrated for Linux";

    /// <summary>The configuration audit summary of <see cref="RaiseEventsDaysToFloor"/>.</summary>
    public const string RetentionFloorSummary = "event retention raised to the floor";

    /// <summary>The account the audit entry names.</summary>
    public const string MigrationAccount = "AdMng Watchdog";

    /// <summary>Upper bound of <c>retention.eventsDays</c> in the validator.</summary>
    private const int MaxEventsDays = 3650;

    /// <summary>The migrated document, or null when <paramref name="stored"/> is current already.</summary>
    public static WatchdogConfiguration? ForLinux(WatchdogConfiguration stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.SchemaVersion >= WatchdogConfiguration.CurrentSchemaVersion)
        {
            return null;
        }

        var attribution = stored.Attribution ?? new AttributionSettings();
        var source = attribution.Source is AttributionSourceKind.DomainControllers or AttributionSourceKind.ForwardedEvents
            ? AttributionSourceKind.OpenWec
            : attribution.Source;
        return stored with
        {
            SchemaVersion = WatchdogConfiguration.CurrentSchemaVersion,
            Attribution = attribution with { Source = source },
        };
    }

    /// <summary>
    /// Stage B design 14.2 (retention floor): a stored document whose <c>retention.eventsDays</c> is below the bootstrap
    /// floor <c>Watchdog:Retention:MinEventsDays</c> – every document saved by 0.2.0 keeps its default of 365 days – would
    /// let the daily maintenance delete events the floor protects, and every later save would be refused with
    /// <c>retentionBelowFloor</c> (AD Manager has no editor for the value). Returns the document with the value raised to
    /// the floor (at most the validator's 3650), or null when it respects the floor (or there is none, 0).
    /// </summary>
    public static WatchdogConfiguration? RaiseEventsDaysToFloor(WatchdogConfiguration stored, int floor)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var retention = stored.Retention ?? new RetentionSettings();
        var target = Math.Min(floor, MaxEventsDays);
        return target > 0 && retention.EventsDays < target
            ? stored with { Retention = retention with { EventsDays = target } }
            : null;
    }
}
