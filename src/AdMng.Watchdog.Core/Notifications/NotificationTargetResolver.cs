using AdMng.Core.ActiveDirectory;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Abstractions;
using AdMng.Watchdog.Core.Model;

namespace AdMng.Watchdog.Core.Notifications;

/// <summary>
/// The delivery targets of one rule for one event (section 13.2 #1): one e-mail target per recipient group (skipped
/// when SMTP is disabled or the group has no addresses), one target per enabled Teams/webhook channel of the rule – every
/// enabled channel with <c>AllEnabledChannels</c> – and, for expiry kinds with <c>NotifyManager</c>, the object's manager
/// (snapshot <c>manager</c> → that object's <c>mail</c>, detail level Minimal). <c>HealthDeliveryFailing</c> never goes
/// over the failing channel and also reaches the digest recipients by e-mail.
/// </summary>
public sealed class NotificationTargetResolver
{
    /// <summary>Channel id of the e-mail channel (SMTP).</summary>
    public const string EmailChannelId = "email";

    private static readonly HashSet<WatchEventKind> ExpiryKinds =
        [WatchEventKind.AccountExpiring, WatchEventKind.AccountExpired, WatchEventKind.PasswordExpiring];

    private readonly IDetectionStore _detection;

    public NotificationTargetResolver(IDetectionStore detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        _detection = detection;
    }

    /// <summary>The targets (distinct by key, in configuration order).</summary>
    public async Task<IReadOnlyList<NotificationTarget>> ResolveAsync(WatchEvent watchEvent, RuleSettings rule, WatchdogConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(configuration);
        var delivery = rule.Delivery ?? new RuleDelivery();
        var targets = new List<NotificationTarget>();
        var failingChannel = watchEvent.Kind == WatchEventKind.HealthDeliveryFailing
            ? watchEvent.Changes.FirstOrDefault(c => string.Equals(c.Attribute, "channel", StringComparison.OrdinalIgnoreCase))?.NewValues.FirstOrDefault()
            : null;

        var groupIds = (delivery.RecipientGroupIds ?? []).ToList();
        if (failingChannel is not null)
        {
            groupIds.AddRange(configuration.Digest?.RecipientGroupIds ?? []);
        }

        if (!string.Equals(failingChannel, EmailChannelId, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var groupId in groupIds)
            {
                if (EmailTarget(configuration, groupId) is { } target)
                {
                    targets.Add(target);
                }
            }
        }

        foreach (var channel in Channels(configuration, delivery))
        {
            if (!string.Equals(channel.Id, failingChannel, StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(ChannelTarget(channel));
            }
        }

        if (delivery.NotifyManager && ExpiryKinds.Contains(watchEvent.Kind) && configuration.Smtp?.Enabled == true
            && await ManagerMailAsync(watchEvent, cancellationToken).ConfigureAwait(false) is { } mail)
        {
            targets.Add(new NotificationTarget
            {
                ChannelType = DeliveryChannelType.Email,
                ChannelId = EmailChannelId,
                Recipients = [mail],
                DetailLevel = DetailLevel.Minimal,
            });
        }

        return [.. targets.DistinctBy(t => t.Key)];
    }

    /// <summary>The e-mail target of a recipient group, or null (SMTP off, unknown or empty group).</summary>
    public static NotificationTarget? EmailTarget(WatchdogConfiguration configuration, string groupId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Smtp?.Enabled != true)
        {
            return null;
        }

        var group = (configuration.RecipientGroups ?? []).FirstOrDefault(g => g is not null && string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase));
        var addresses = (group?.Addresses ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return group is null || addresses.Count == 0
            ? null
            : new NotificationTarget { ChannelType = DeliveryChannelType.Email, ChannelId = EmailChannelId, Recipients = addresses, DetailLevel = group.DetailLevel };
    }

    /// <summary>The target of a Teams or webhook channel.</summary>
    public static NotificationTarget ChannelTarget(ChannelSettings channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new NotificationTarget
        {
            ChannelType = channel.Type == ChannelType.Teams ? DeliveryChannelType.Teams : DeliveryChannelType.Webhook,
            ChannelId = channel.Id,
            DetailLevel = channel.DetailLevel,
        };
    }

    /// <summary>The enabled channels of a delivery (its channel ids, plus every enabled channel with AllEnabledChannels).</summary>
    public static IEnumerable<ChannelSettings> Channels(WatchdogConfiguration configuration, RuleDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(delivery);
        var channels = (configuration.Channels ?? []).Where(c => c is not null && c.Enabled).ToList();
        var ids = new HashSet<string>(delivery.ChannelIds ?? [], StringComparer.OrdinalIgnoreCase);
        return channels.Where(c => delivery.AllEnabledChannels || ids.Contains(c.Id));
    }

    private async Task<string?> ManagerMailAsync(WatchEvent watchEvent, CancellationToken cancellationToken)
    {
        if (watchEvent.Object.ObjectGuid is not { } guid)
        {
            return null;
        }

        var snapshots = await _detection.GetSnapshotsAsync([guid], cancellationToken).ConfigureAwait(false);
        if (!snapshots.TryGetValue(guid, out var snapshot) || !snapshot.Attributes.TryGetValue(Attr.Manager, out var manager) || manager.Values.Count == 0)
        {
            return null;
        }

        var reference = manager.Values[0];
        SnapshotObject? managerObject = null;
        if (reference.StartsWith("guid:", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(reference.AsSpan(5), out var managerGuid))
        {
            managerObject = (await _detection.GetSnapshotsAsync([managerGuid], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(managerGuid);
        }
        else if (reference.StartsWith("sid:", StringComparison.OrdinalIgnoreCase))
        {
            managerObject = await _detection.FindBySidAsync(reference[4..], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var dn = reference.StartsWith("dn:", StringComparison.OrdinalIgnoreCase) ? reference[3..] : manager.DisplayValues.FirstOrDefault() ?? reference;
            managerObject = await _detection.FindByDnAsync(dn, cancellationToken).ConfigureAwait(false);
        }

        if (managerObject is null || managerObject.IsDeleted || !managerObject.Attributes.TryGetValue(Attr.Mail, out var mail))
        {
            return null;
        }

        var address = mail.Values.FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(address) || address.Contains('\r') || address.Contains('\n') ? null : address;
    }
}
