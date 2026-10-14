using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Detection;

/// <summary>Per-object context of <see cref="EventFactory.Create"/>.</summary>
internal sealed record EventContext
{
    public required DetectionSource Source { get; init; }
    public string? Domain { get; init; }
    public SeverityContext Severity { get; init; } = SeverityContext.Empty;
    public ReplicationMetadata Metadata { get; init; } = ReplicationMetadata.Empty;

    /// <summary>Watermark W the metadata is compared with (null: no meaningful W – every entry counts).</summary>
    public long? Watermark { get; init; }

    /// <summary>whenChanged on the watched DC.</summary>
    public DateTimeOffset? LocalChangeTime { get; init; }

    /// <summary>(invocationId, originating DSA DN) → DC host name.</summary>
    public Func<Guid, string?, string?>? ResolveDcHost { get; init; }
}
