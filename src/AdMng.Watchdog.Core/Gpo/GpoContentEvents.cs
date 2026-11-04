using AdMng.Core.GroupPolicy.Content;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Detection;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Gpo;

/// <summary>The GPO and the circumstances of a content event.</summary>
public sealed record GpoEventContext
{
    /// <summary>The GPO (groupPolicyContainer) the event is about.</summary>
    public required ObjectRef Gpo { get; init; }

    /// <summary>Domain DNS name.</summary>
    public string? Domain { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>The <c>ChangeSetId</c> of the GroupPolicyChanged event that triggered the read (8.6); empty for a
    /// reconciliation.</summary>
    public Guid ChangeSetId { get; init; }

    public DetectionSource Source { get; init; } = DetectionSource.IncrementalPoll;

    public GpoLinkScope Scope { get; init; } = GpoLinkScope.Unlinked;
}

/// <summary>
/// Builds the events of stage B design 8.6: <c>GroupPolicySettingsChanged</c> (510), <c>GroupPolicyPathRedirected</c>
/// (511) and <c>GroupPolicyContentOutOfBand</c> (512) with their own severity (<see cref="GpoSeverityClassifier"/>), tag
/// <c>gppPassword</c> and the initial attribution (510/511 Pending – inherited from the GroupPolicyChanged event by
/// <see cref="GpoAttributionInheritor"/>; 512 Unknown <c>SYSVOL_NOT_AUDITED</c>, 8.7).
/// </summary>
public static class GpoContentEvents
{
    public static WatchEvent SettingsChanged(GpoEventContext context, GpoSettingsDiff diff)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diff);
        return Create(context, WatchEventKind.GroupPolicySettingsChanged, GpoSeverityClassifier.SettingsChanged(diff, context.Scope),
            diff.Changes, Tags(diff), EventAttribution.Pending);
    }

    public static WatchEvent ContentOutOfBand(GpoEventContext context, GpoSettingsDiff diff)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diff);
        return Create(context, WatchEventKind.GroupPolicyContentOutOfBand, GpoSeverityClassifier.ContentOutOfBand(context.Scope),
            diff.Changes, Tags(diff), GpoAttributionInheritor.OutOfBand(context.DetectedAt));
    }

    /// <summary>511 (Critical): <paramref name="observedPath"/> is shown sanitized and capped like a setting value; the
    /// content was not read from it.</summary>
    public static WatchEvent PathRedirected(GpoEventContext context, string observedPath)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observedPath);
        var (value, kind) = GpoValues.Cap(observedPath);
        var change = new AttributeChange
        {
            Attribute = WatchdogAttributes.GpcFileSysPath,
            ValueKind = kind == GpoSettingKind.Hash ? AttributeValueKind.Hash : AttributeValueKind.Text,
            NewValues = [value],
            NewCount = 1,
        };
        return Create(context, WatchEventKind.GroupPolicyPathRedirected, Severity.Critical, [change], [], EventAttribution.Pending);
    }

    private static IReadOnlyList<string> Tags(GpoSettingsDiff diff) => diff.GppPasswordPresent ? [EventTags.GppPassword] : [];

    private static WatchEvent Create(GpoEventContext context, WatchEventKind kind, Severity severity, IReadOnlyList<AttributeChange> changes,
        IReadOnlyList<string> tags, EventAttribution attribution) => new()
    {
        EventId = Guid.CreateVersion7(context.DetectedAt),
        ChangeSetId = context.ChangeSetId,
        Kind = kind,
        Severity = severity,
        Object = context.Gpo,
        Changes = changes,
        Domain = context.Domain,
        DetectedAt = context.DetectedAt,
        Source = context.Source,
        Attribution = attribution,
        Tags = tags,
    };
}
