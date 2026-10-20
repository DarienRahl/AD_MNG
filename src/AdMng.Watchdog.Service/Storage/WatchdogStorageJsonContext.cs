using System.Text.Json;
using System.Text.Json.Serialization;
using AdMng.Watchdog.Core.Gpo;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// Source-generated JSON metadata for the Core.Model payloads the store keeps in blobs (Contracts cannot reference
/// Core, so <c>WatchdogJsonContext</c> does not know them). Computed get-only members (<c>SecurityEvent.SubjectSid</c>,
/// <c>NotificationTarget.Key</c> …) are not written. Dictionaries lose their comparer on deserialization –
/// <see cref="BlobCodec"/> rebuilds them.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(StoredSnapshotPayload))]
[JsonSerializable(typeof(StoredOutboxPayload))]
[JsonSerializable(typeof(PrivilegeState))]
[JsonSerializable(typeof(NotificationTarget))]
[JsonSerializable(typeof(SecurityEvent))]
[JsonSerializable(typeof(UpcomingExpiry))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<long>))]
[JsonSerializable(typeof(List<GpoInventoryEntry>))]
[JsonSerializable(typeof(List<GpoSetting>))]
internal sealed partial class WatchdogStorageJsonContext : JsonSerializerContext;
