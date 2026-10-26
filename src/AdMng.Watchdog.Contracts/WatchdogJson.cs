using System.Text.Json;
using System.Text.Json.Serialization;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Contracts;

/// <summary>The JSON options of the watchdog API (server and client).</summary>
public static class WatchdogJson
{
    /// <summary>Web defaults (camelCase), enums as strings, TimeSpan "c", TimeOnly "HH:mm[:ss]", DateOnly "yyyy-MM-dd".</summary>
    /// <remarks>
    /// A read-only copy of <c>WatchdogJsonContext.Default.Options</c> whose resolver keeps record defaults for properties
    /// missing from the JSON (<see cref="InitDefaultsPreservingResolver"/>). Deserialize only through these options (or
    /// their <see cref="JsonSerializerOptions.TypeInfoResolver"/>, e.g. for ASP.NET Core's JSON options) – never through
    /// <c>WatchdogJsonContext.Default</c> or its type infos directly, which reset missing init-only properties to null/0.
    /// </remarks>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(WatchdogJsonContext.Default.Options)
        {
            TypeInfoResolver = new InitDefaultsPreservingResolver(WatchdogJsonContext.Default),
        };
        options.MakeReadOnly();
        return options;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, WriteIndented = false)]
[JsonSerializable(typeof(WatchEvent))]  [JsonSerializable(typeof(EventPage))]  [JsonSerializable(typeof(EventDetails))]
[JsonSerializable(typeof(StatusDto))]   [JsonSerializable(typeof(MeDto))]      [JsonSerializable(typeof(ConfigEnvelope))]
[JsonSerializable(typeof(WatchdogConfiguration))] [JsonSerializable(typeof(ConfigValidationResult))]
[JsonSerializable(typeof(TestEmailRequest))] [JsonSerializable(typeof(TestChannelRequest))] [JsonSerializable(typeof(DeliveryTestResult))]
[JsonSerializable(typeof(RulePreviewRequest))] [JsonSerializable(typeof(RulePreviewResult))] [JsonSerializable(typeof(DigestPreviewDto))]
[JsonSerializable(typeof(OutboxPage))] [JsonSerializable(typeof(ConfigAuditPage))] [JsonSerializable(typeof(AcknowledgeRequest))]
[JsonSerializable(typeof(BulkAcknowledgeRequest))]
[JsonSerializable(typeof(UserMessageRequest))] [JsonSerializable(typeof(UserMessageResult))] [JsonSerializable(typeof(UserMessagingStatusDto))]
[JsonSerializable(typeof(IReadOnlyList<EventKindInfo>))]
public sealed partial class WatchdogJsonContext : JsonSerializerContext;
