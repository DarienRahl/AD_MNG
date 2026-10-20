namespace AdMng.Watchdog.Contracts.Configuration;

/// <summary>
/// The runtime configuration document (section 8): stored versioned by the service, edited through the API / GUI.
/// Missing properties take the record defaults. Records with collection members compare by reference – compare
/// serialized JSON (WatchdogJson) for dirty tracking, never <c>Equals</c>.
/// </summary>
public sealed record WatchdogConfiguration
{
    /// <summary>2 since stage B (Linux): version 1 documents are migrated once – DomainControllers / ForwardedEvents
    /// attribution sources become <see cref="AttributionSourceKind.OpenWec"/> (stage B design 7.8).</summary>
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public GeneralSettings General { get; init; } = new();
    public DirectorySettings Directory { get; init; } = new();
    public AttributionSettings Attribution { get; init; } = new();
    public ScanSettings Scans { get; init; } = new();
    public PrivilegedGroupSettings PrivilegedGroups { get; init; } = new();
    public BusinessHoursSettings BusinessHours { get; init; } = new();
    public NotificationSettings Notifications { get; init; } = new();
    public SmtpSettings Smtp { get; init; } = new();
    public IReadOnlyList<ChannelSettings> Channels { get; init; } = [];
    public IReadOnlyList<RecipientGroup> RecipientGroups { get; init; } = [];
    public DigestSettings Digest { get; init; } = new();
    public IReadOnlyList<RuleSettings> Rules { get; init; } = [];
    public RetentionSettings Retention { get; init; } = new();
}
