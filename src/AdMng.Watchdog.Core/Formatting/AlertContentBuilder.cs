using System.Globalization;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// Builds the <see cref="AlertContent"/> of one event (section 14.3): masks it for the detail level and makes every
/// directory string safe (<see cref="DetailLevelFilter.ForNotification"/>) first, then composes the sentence, the facts
/// (Co, Obiekt, Kto, Skąd, Kiedy, Gdzie – lockouts show "Źródło błędnych haseł" and no "Kto"), the changes table
/// (multi-valued: first 3 added/removed values and "+N kolejnych"), links and footer.
/// </summary>
public static class AlertContentBuilder
{
    /// <summary>Values shown per side of a multi-valued change.</summary>
    public const int MaxValuesShown = 3;

    /// <summary>Builds the content of <paramref name="watchEvent"/> (raw, as stored).</summary>
    /// <param name="watchEvent">The event.</param>
    /// <param name="rule">The rule the notification belongs to (null for the HTML page or a digest).</param>
    /// <param name="context">Language, zone, detail level, configuration.</param>
    /// <param name="followUpExpected">The actor is pending and a follow-up will be sent.</param>
    public static AlertContent Build(WatchEvent watchEvent, RuleSettings? rule, NotificationContext context, bool followUpExpected = false)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        ArgumentNullException.ThrowIfNull(context);
        var e = DetailLevelFilter.ForNotification(watchEvent, context.DetailLevel, context.MaxValueLength);
        var text = context.Text;
        var objects = context.Objects;
        var title = text.KindTitle(e.Kind);
        var ruleName = objects.RuleName(rule);
        var isLockout = e.Kind == WatchEventKind.AccountLockedOut;
        var showActor = !isLockout && e.Attribution.Status != AttributionStatus.NotApplicable;
        var actor = showActor ? objects.Actor(e.Attribution, followUpExpected) : null;
        var when = context.Time.LocalAndUtc(e.OriginatingTime ?? e.DetectedAt);

        var facts = new List<AlertFact>
        {
            new(text.Get("Label.What"), ruleName.Length == 0 ? title : text.Format("Value.WhatWithRule", title, ruleName)),
        };
        if (e.Object.Kind != WatchObjectKind.Watchdog || !string.IsNullOrEmpty(e.Object.Name))
        {
            facts.Add(new(text.Get("Label.Object"), objects.NameWithPath(e.Object)));
        }

        AddRelatedFacts(e, context, facts);
        if (actor is not null)
        {
            facts.Add(new(text.Get("Label.Who"), actor));
        }

        if (isLockout)
        {
            if (objects.LockoutSources(e.Lockout) is { } sources)
            {
                facts.Add(new(text.Get("Label.BadPasswordSources"), sources));
            }

            if (e.Lockout is { } lockout)
            {
                facts.Add(new(text.Get("Label.Lockout"), LockoutText(lockout, context)));
            }
        }
        else if (objects.Source(e.Attribution) is { } source)
        {
            facts.Add(new(text.Get("Label.From"), source));
        }

        facts.Add(new(text.Get("Label.When"), when));
        if (objects.Where(e) is { } where)
        {
            facts.Add(new(text.Get("Label.Where"), where));
        }

        var eventNumber = e.Id.ToString(CultureInfo.InvariantCulture);
        return new AlertContent
        {
            Event = e,
            Severity = e.Severity,
            SeverityWord = text.SeverityWord(e.Severity),
            Category = ruleName.Length == 0 ? title : ruleName,
            Title = title,
            ObjectName = ObjectName(e, context),
            Sentence = Sentence(e, context),
            Via = e.Via.Count > 0 && e.Kind is WatchEventKind.PrivilegeGranted or WatchEventKind.PrivilegeRevoked
                ? text.Format("Email.Via", objects.ViaPath(e))
                : null,
            Facts = facts,
            Changes = ChangeRows(e, context),
            Remedy = Remedy(e, context),
            EventLink = context.EventLink(e.EventId),
            AdMngHint = text.Format("Email.InAdMng", eventNumber),
            Footer = rule is null
                ? text.Format("Email.FooterNoRule", eventNumber, context.ServiceName)
                : text.Format("Email.Footer", rule.Id, eventNumber, context.ServiceName),
            ActorShort = objects.ActorShort(e.Attribution),
            Actor = actor,
            When = context.Time.Local(e.OriginatingTime ?? e.DetectedAt),
        };
    }

    /// <summary>The sentence of a sanitized event (<c>Kind.{Kind}.Sentence</c> with its arguments).</summary>
    public static string Sentence(WatchEvent e, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(context);
        var objects = context.Objects;
        var related = e.Kind switch
        {
            WatchEventKind.ObjectMoved => ObjectDisplay.ParentPath(e.PreviousDn),
            WatchEventKind.ObjectRenamed => DistinguishedName.TryParse(e.PreviousDn, out var previous) && !previous.IsRoot ? previous.Name : e.PreviousDn ?? string.Empty,
            WatchEventKind.PasswordSprayingSuspected => e.Attribution.IpAddress ?? e.Attribution.Workstation ?? string.Empty,
            WatchEventKind.HealthDeliveryFailing => ChangeValue(e, "channel") ?? string.Empty,
            _ => objects.Name(e.Member),
        };
        var extra = e.Kind == WatchEventKind.ObjectMoved
            ? ObjectDisplay.ParentPath(e.Object.Dn)
            : e.Expiry is { } expiry
                ? expiry.Kind == ExpiryKind.Account ? context.Time.Date(expiry.LastValidDay) : context.Time.Local(expiry.ExpiresAt)
                : string.Empty;
        var count = e.Expiry?.DaysLeft ?? e.Burst?.Count ?? e.Lockout?.LockoutsToday ?? 0;
        var minutes = e.Burst is { } burst ? (int)Math.Round(burst.Window.TotalMinutes) : 0;
        return context.Text.Format("Kind." + e.Kind + ".Sentence", ObjectName(e, context), related, extra, count, minutes);
    }

    /// <summary>The object as named in headers: the object, the DC of event-log kinds, the instance for health events.</summary>
    public static string ObjectName(WatchEvent e, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(context);
        var name = context.Objects.Name(e.Object);
        return name.Length == 0 && e.Object.Kind == WatchObjectKind.Watchdog ? context.InstanceName : name;
    }

    /// <summary>The changes table of a sanitized event.</summary>
    public static IReadOnlyList<AlertChangeRow> ChangeRows(WatchEvent e, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(context);
        var rows = new List<AlertChangeRow>(e.Changes.Count);
        foreach (var change in e.Changes)
        {
            rows.Add(ChangeRow(change, context));
        }

        return rows;
    }

    /// <summary>One row of the changes table.</summary>
    public static AlertChangeRow ChangeRow(AttributeChange change, NotificationContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(context);
        var text = context.Text;
        var display = context.Attributes;
        var name = display.Name(change.Attribute);
        switch (DetailLevelFilter.Visibility(change, context.DetailLevel))
        {
            case ValueVisibility.Secret:
                return new AlertChangeRow(name, null, text.Get("Value.ChangedHidden"));
            case ValueVisibility.NameOnly or ValueVisibility.Hidden:
                return new AlertChangeRow(name, null, text.Get("Value.Changed"));
        }

        if (change.ValueKind is AttributeValueKind.Hash or AttributeValueKind.Binary)
        {
            return new AlertChangeRow(name, null, text.Get("Value.Changed") + " (" + display.EntryCounts(change) + ")");
        }

        if (change.Added.Count > 0 || change.Removed.Count > 0)
        {
            var removed = Capped(change.Removed.Select(v => "−" + display.Value(change, v)).ToList(), context);
            var added = Capped(change.Added.Select(v => "+" + display.Value(change, v)).ToList(), context);
            return new AlertChangeRow(name, removed.Length == 0 ? "—" : removed, added.Length == 0 ? "—" : added);
        }

        var before = Join(display.Before(change), change.OldCount, context);
        var after = Join(display.After(change), change.NewCount, context);
        return new AlertChangeRow(name, before.Length == 0 ? "—" : before, after.Length == 0 ? "—" : after);
    }

    private static void AddRelatedFacts(WatchEvent e, NotificationContext context, List<AlertFact> facts)
    {
        var text = context.Text;
        var objects = context.Objects;
        switch (e.Kind)
        {
            case WatchEventKind.GroupMemberAdded or WatchEventKind.GroupMemberRemoved when e.Member is not null:
                facts.Add(new(text.Get("Label.Member"), objects.NameWithPath(e.Member)));
                break;
            case WatchEventKind.PrivilegeGranted or WatchEventKind.PrivilegeRevoked when e.Member is not null:
                facts.Add(new(text.Get("Label.Group"), objects.Name(e.Member)));
                if (e.Via.Count > 0)
                {
                    facts.Add(new(text.Get("Label.Via"), objects.ViaPath(e)));
                }

                break;
            case WatchEventKind.ObjectMoved or WatchEventKind.ObjectRenamed or WatchEventKind.ObjectDeleted when !string.IsNullOrEmpty(e.PreviousDn):
                facts.Add(new(text.Get("Label.PreviousDn"), e.PreviousDn));
                break;
        }

        if (e.Expiry is { } expiry)
        {
            facts.Add(new(text.Get("Label.Expiry"), objects.Expiry(expiry)));
        }

        if (e.Burst is { } burst)
        {
            facts.Add(new(text.Get("Label.Burst"), objects.Burst(burst)));
            if (burst.Objects.Count > 0)
            {
                facts.Add(new(text.Get("Label.Objects"), Capped(burst.Objects.Select(objects.Name).ToList(), context, max: 10, separator: ", ")));
            }
        }
    }

    private static string LockoutText(LockoutInfo lockout, NotificationContext context)
    {
        var text = context.Text;
        var until = lockout.LockedUntil is { } lockedUntil ? context.Time.Local(lockedUntil) : text.Get("Value.UntilUnlocked");
        return text.Format("Value.LockedUntil", until) + " · " + text.Format("Value.LockoutsToday", lockout.LockoutsToday, context.Time.LocalTime(lockout.LockedAt));
    }

    private static string? Remedy(WatchEvent e, NotificationContext context)
    {
        var a = e.Attribution;
        var codes = new List<string>();
        if (a.Status is AttributionStatus.Unknown or AttributionStatus.Ambiguous && (a.Reason ?? AttributionReasons.NoMatch) is { } reason)
        {
            codes.Add(reason);
        }

        if (a.SourceReason is { } sourceReason && e.Kind != WatchEventKind.AccountLockedOut)
        {
            codes.Add(sourceReason);
        }

        var remedies = codes.Select(context.Objects.Remedy).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return remedies.Count == 0 ? null : string.Join(" ", remedies);
    }

    private static string Join(IReadOnlyList<string> values, int count, NotificationContext context)
    {
        var shown = values.Take(MaxValuesShown).ToList();
        var hidden = Math.Max(count, values.Count) - shown.Count;
        var joined = string.Join("; ", shown);
        return hidden > 0 && shown.Count > 0 ? joined + " " + context.Text.Format("Value.More", hidden) : joined;
    }

    private static string Capped(List<string> values, NotificationContext context, int max = MaxValuesShown, string separator = "; ")
    {
        if (values.Count <= max)
        {
            return string.Join(separator, values);
        }

        return string.Join(separator, values.Take(max)) + " " + context.Text.Format("Value.More", values.Count - max);
    }

    private static string? ChangeValue(WatchEvent e, string attribute) =>
        e.Changes.FirstOrDefault(c => string.Equals(c.Attribute, attribute, StringComparison.OrdinalIgnoreCase))?.NewValues.FirstOrDefault();
}
