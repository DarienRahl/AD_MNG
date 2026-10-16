using AdMng.Core.Schema;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using AdMng.Watchdog.Core.Formatting;

namespace AdMng.Watchdog.Service.Api;

/// <summary>
/// The detail level of API responses and the HTML event page for non-Admin callers (spec 14.1, 17.2) – the structured
/// counterpart of the notification <c>DetailLevelFilter</c>: <b>Minimal</b> – attribute names only (values, flags and
/// value lists removed and marked <see cref="AttributeChange.Redacted"/>), no workstation/IP (attribution and lockout
/// sources), Via path DNs removed (names kept); <b>Standard</b> – values except the personal-data attributes, which are
/// redacted; <b>Full</b> – unchanged. Secret attributes are redacted at every level. Unlike the notification masking
/// (<see cref="DetailLevelFilter"/>) hidden values are marked <see cref="AttributeChange.Redacted"/> for the GUI and
/// never truncated.
/// </summary>
public static class ViewerEventFilter
{
    /// <summary>Attributes whose values are personal data (hidden below Full) – the notification side's list
    /// (<see cref="DetailLevelFilter.PersonalAttributes"/>, spec 14.1), so the API, the event page and the notifications
    /// hide the same values.</summary>
    public static IReadOnlySet<string> PersonalDataAttributes => DetailLevelFilter.PersonalAttributes;

    public static WatchEvent Apply(WatchEvent watchEvent, DetailLevel level)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        var changes = watchEvent.Changes.Select(c => Filter(c, level)).ToList();
        if (level != DetailLevel.Minimal)
        {
            return watchEvent with { Changes = changes };
        }

        return watchEvent with
        {
            Changes = changes,
            Via = [.. watchEvent.Via.Select(v => v with { Dn = string.Empty })],
            Attribution = watchEvent.Attribution with { Workstation = null, IpAddress = null },
            Lockout = watchEvent.Lockout is { } lockout
                ? lockout with { CallerComputer = null, Sources = [.. lockout.Sources.Select(s => s with { IpAddress = null, Workstation = null })] }
                : null,
        };
    }

    public static EventDetails Apply(EventDetails details, DetailLevel level)
    {
        ArgumentNullException.ThrowIfNull(details);
        return level == DetailLevel.Full && !HasSecrets(details)
            ? details
            : details with
            {
                Event = Apply(details.Event, level),
                RelatedEvents = [.. details.RelatedEvents.Select(e => Apply(e, level))],
            };
    }

    public static EventPage Apply(EventPage page, DetailLevel level)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page with { Items = [.. page.Items.Select(e => Apply(e, level))] };
    }

    private static AttributeChange Filter(AttributeChange change, DetailLevel level)
    {
        var hide = change.Redacted
                   || SecretAttributes.IsSecret(change.Attribute)
                   || level == DetailLevel.Minimal
                   || (level == DetailLevel.Standard && PersonalDataAttributes.Contains(change.Attribute));
        if (!hide)
        {
            return change;
        }

        return change with
        {
            OldValues = [],
            NewValues = [],
            Added = [],
            Removed = [],
            FlagsAdded = level == DetailLevel.Minimal ? [] : change.FlagsAdded,
            FlagsRemoved = level == DetailLevel.Minimal ? [] : change.FlagsRemoved,
            Redacted = true,
        };
    }

    private static bool HasSecrets(EventDetails details) =>
        details.Event.Changes.Any(c => SecretAttributes.IsSecret(c.Attribute))
        || details.RelatedEvents.Any(e => e.Changes.Any(c => SecretAttributes.IsSecret(c.Attribute)));
}
