using System.Text.Json;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Testing.Stores;

/// <summary>
/// In-memory implementation of every store interface of section 6.2 (Core tests, integration tests, Development
/// runs) with the semantics the SQLite store of the service has (section 16): AUTOINCREMENT ids that are never reused,
/// contract types copied through <see cref="WatchdogJson.Options"/> (what the JSON payload columns store), Core model
/// types deep-copied with their dictionaries rebuilt case-insensitively (what <c>BlobCodec</c> does), cascades of the
/// foreign keys, and the transactions of the interface contracts (one lock per call). Nothing is written to disk.
/// <see cref="WatchdogStoreContractTests{TStore}"/> pins the behaviour; the SQLite stores run the same suite.
/// </summary>
/// <remarks>Time ranges are half-open <c>[from, to)</c> (event queries, <see cref="GetRangeAsync"/>) except the
/// correlation windows of <see cref="FindAsync"/>, which are closed <c>[From, To]</c>. Times written by the store itself
/// (watermarks, configuration saves, audit entries, <c>MembershipsChangedAt</c>, <c>BaselineCompletedAt</c>, backups)
/// come from the injected <see cref="TimeProvider"/>.</remarks>
public sealed partial class InMemoryWatchdogStore :
    IDetectionStore, IEventStore, INotificationQueueStore, IOutboxStore, ISecurityEventStore, IExpiryNoticeStore,
    IDigestRunStore, IConfigStore, ISecretStore, IStoreMaintenance
{
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    public InMemoryWatchdogStore(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    private DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    /// <summary>A copy of a contract type as the JSON payload column stores it.</summary>
    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, WatchdogJson.Options);
        return JsonSerializer.Deserialize<T>(json, WatchdogJson.Options)
            ?? throw new InvalidOperationException("A stored payload could not be read back.");
    }

    /// <summary>Normalized DN for comparisons (the <c>*_dn_norm</c> columns); unparsable values compare case-insensitively.</summary>
    private static string NormalizeDn(string? dn)
    {
        if (string.IsNullOrEmpty(dn))
        {
            return string.Empty;
        }

        return DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString().ToUpperInvariant() : dn.Trim().ToUpperInvariant();
    }

    private static SnapshotObject Copy(SnapshotObject snapshot)
    {
        var attributes = new Dictionary<string, SnapshotAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, attribute) in snapshot.Attributes)
        {
            attributes[name] = attribute with { Values = [.. attribute.Values], DisplayValues = [.. attribute.DisplayValues] };
        }

        return snapshot with { ObjectClasses = [.. snapshot.ObjectClasses], Attributes = attributes };
    }

    private static MemberReference Copy(MemberReference member) => member with { };

    private static PrivilegeState Copy(PrivilegeState state) => state with
    {
        Principals = [.. state.Principals.Select(p => p with { Path = [.. p.Path] })],
    };

    private static NotificationTarget Copy(NotificationTarget target) => target with { Recipients = [.. target.Recipients] };

    private static PendingNotification Copy(PendingNotification pending) => pending with { Target = Copy(pending.Target) };

    private static OutboxMessage Copy(OutboxMessage message) => message with
    {
        Recipients = [.. message.Recipients],
        EventIds = [.. message.EventIds],
        Email = message.Email is null
            ? null
            : message.Email with { Headers = new Dictionary<string, string>(message.Email.Headers, StringComparer.OrdinalIgnoreCase) },
        Http = message.Http is null ? null : message.Http with { Body = [.. message.Http.Body] },
    };

    private static SecurityEvent Copy(SecurityEvent securityEvent) => securityEvent with
    {
        Data = new Dictionary<string, string>(securityEvent.Data, StringComparer.Ordinal),
    };

    private static UpcomingExpiry Copy(UpcomingExpiry upcoming) => upcoming with
    {
        Object = RoundTrip(upcoming.Object),
        Tags = [.. upcoming.Tags],
    };

    private static WatchdogConfiguration Copy(WatchdogConfiguration configuration) => RoundTrip(configuration);

    private static ConfigEnvelope Copy(ConfigEnvelope envelope) => envelope with { Configuration = Copy(envelope.Configuration) };
}
