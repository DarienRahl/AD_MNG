using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Service.Storage;

/// <summary>
/// Payload blobs = Deflate(UTF-8 JSON) (spec 16.1). Contracts types go through <see cref="WatchdogJson.Options"/>, Core
/// model types through <see cref="WatchdogStorageJsonContext"/>. System.Text.Json drops the
/// <c>StringComparer.OrdinalIgnoreCase</c> initializer of dictionaries on deserialization, so every dictionary is rebuilt
/// with its intended comparer after reading (snapshot attributes and e-mail headers case-insensitive, Security-event
/// data ordinal).
/// </summary>
internal static class BlobCodec
{
    /// <summary>Serializes a Contracts type (<see cref="WatchdogJson.Options"/>) and deflates it.</summary>
    public static byte[] EncodeContract<T>(T value) => Deflate(JsonSerializer.SerializeToUtf8Bytes(value, WatchdogJson.Options));

    /// <summary>Inflates and deserializes a Contracts type.</summary>
    public static T DecodeContract<T>(byte[] blob) =>
        JsonSerializer.Deserialize<T>(Inflate(blob), WatchdogJson.Options)
        ?? throw new InvalidDataException("A stored payload is empty.");

    /// <summary>Serializes a storage type and deflates it.</summary>
    public static byte[] Encode<T>(T value, JsonTypeInfo<T> typeInfo) => Deflate(JsonSerializer.SerializeToUtf8Bytes(value, typeInfo));

    /// <summary>Inflates and deserializes a storage type.</summary>
    public static T Decode<T>(byte[] blob, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(Inflate(blob), typeInfo) ?? throw new InvalidDataException("A stored payload is empty.");

    public static byte[] EncodeSnapshot(SnapshotObject snapshot) => Encode(
        new StoredSnapshotPayload
        {
            ObjectClasses = [.. snapshot.ObjectClasses],
            Attributes = new Dictionary<string, SnapshotAttribute>(snapshot.Attributes, StringComparer.OrdinalIgnoreCase),
        },
        WatchdogStorageJsonContext.Default.StoredSnapshotPayload);

    /// <summary>The snapshot payload with a case-insensitive attribute dictionary.</summary>
    public static StoredSnapshotPayload DecodeSnapshot(byte[] blob)
    {
        var payload = Decode(blob, WatchdogStorageJsonContext.Default.StoredSnapshotPayload);
        var attributes = new Dictionary<string, SnapshotAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, attribute) in payload.Attributes ?? [])
        {
            attributes[name] = attribute with
            {
                Values = attribute.Values ?? [],
                DisplayValues = attribute.DisplayValues ?? [],
            };
        }

        return payload with { ObjectClasses = payload.ObjectClasses ?? [], Attributes = attributes };
    }

    public static byte[] EncodeSecurityEvent(SecurityEvent securityEvent) =>
        Encode(securityEvent, WatchdogStorageJsonContext.Default.SecurityEvent);

    /// <summary>The Security event with an ordinal <see cref="SecurityEvent.Data"/> dictionary.</summary>
    public static SecurityEvent DecodeSecurityEvent(byte[] blob)
    {
        var securityEvent = Decode(blob, WatchdogStorageJsonContext.Default.SecurityEvent);
        return securityEvent with
        {
            Data = new Dictionary<string, string>(securityEvent.Data ?? new Dictionary<string, string>(), StringComparer.Ordinal),
        };
    }

    public static byte[] EncodeOutbox(RenderedEmail? email, RenderedHttpMessage? http) =>
        Encode(new StoredOutboxPayload { Email = email, Http = http }, WatchdogStorageJsonContext.Default.StoredOutboxPayload);

    /// <summary>The rendered message with case-insensitive e-mail headers.</summary>
    public static StoredOutboxPayload DecodeOutbox(byte[] blob)
    {
        var payload = Decode(blob, WatchdogStorageJsonContext.Default.StoredOutboxPayload);
        return payload.Email is { } email
            ? payload with
            {
                Email = email with
                {
                    Headers = new Dictionary<string, string>(email.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                },
            }
            : payload;
    }

    public static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    public static byte[] Inflate(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        using var input = new MemoryStream(blob);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
}
