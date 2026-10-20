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

    /// <summary>The account the audit entry names.</summary>
    public const string MigrationAccount = "AdMng Watchdog";

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
}
