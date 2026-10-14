using System.Collections.Frozen;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Ldap;
using AdMng.Core.Schema;
using AdMng.Watchdog.Contracts.Api;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// Detail levels and secret masking (section 14.1). <b>Minimal</b>: attribute names only, no workstation/IP (also no
/// lockout sources), Via path as names only; <b>Standard</b>: values except the personal-data attributes
/// (<see cref="PersonalAttributes"/>), shown as "zmieniono"; <b>Full</b>: all non-secret values. Secret/confidential
/// (<see cref="SecretAttributes.IsSecret"/>) and <see cref="AttributeChange.Redacted"/> changes never carry values.
/// Values longer than the configured maximum end with "…". Hidden values are removed and their counts kept.
/// </summary>
/// <remarks><see cref="Apply(WatchEvent, DetailLevel, int)"/> is the masking used for API responses and the HTML event
/// page of non-Admin callers; <see cref="ForNotification"/> additionally passes every directory string through
/// <see cref="UntrustedText"/> before any formatter runs.</remarks>
public static class DetailLevelFilter
{
    private const string Ellipsis = "…";

    /// <summary>Personal-data attributes hidden at the Standard level.</summary>
    public static IReadOnlySet<string> PersonalAttributes { get; } = new[]
    {
        Attr.TelephoneNumber, Attr.Mobile, Attr.EmployeeId, Attr.EmployeeNumber, Attr.EmployeeType, Attr.Info, Attr.Office,
        Attr.HomeDirectory, Attr.LogonHours,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>How the values of <paramref name="change"/> may be shown at <paramref name="level"/>.</summary>
    public static ValueVisibility Visibility(AttributeChange change, DetailLevel level)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Redacted || SecretAttributes.IsSecret(change.Attribute))
        {
            return ValueVisibility.Secret;
        }

        if (level == DetailLevel.Minimal)
        {
            return ValueVisibility.NameOnly;
        }

        if (level == DetailLevel.Standard && PersonalAttributes.Contains(change.Attribute))
        {
            return ValueVisibility.Hidden;
        }

        var noValues = change.OldValues.Count == 0 && change.NewValues.Count == 0 && change.Added.Count == 0 && change.Removed.Count == 0;
        return noValues && (change.OldCount > 0 || change.NewCount > 0) ? ValueVisibility.Hidden : ValueVisibility.Visible;
    }

    /// <summary>The event as a caller with <paramref name="level"/> may see it (no defanging).</summary>
    public static WatchEvent Apply(WatchEvent watchEvent, DetailLevel level, int maxValueLength = 200)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        var attribution = watchEvent.Attribution ?? AdMng.Watchdog.Contracts.Events.Attribution.Pending;
        return watchEvent with
        {
            Changes = [.. watchEvent.Changes.Select(c => FilterChange(c, level, maxValueLength))],
            Attribution = level == DetailLevel.Minimal ? attribution with { Workstation = null, IpAddress = null } : attribution,
            Lockout = watchEvent.Lockout is { } lockout && level == DetailLevel.Minimal
                ? lockout with { CallerComputer = null, Sources = [] }
                : watchEvent.Lockout,
            Via = level == DetailLevel.Minimal ? [.. watchEvent.Via.Select(NameOnly)] : watchEvent.Via,
        };
    }

    /// <summary><see cref="Apply(WatchEvent, DetailLevel, int)"/> for the event and its related events.</summary>
    public static EventDetails Apply(EventDetails details, DetailLevel level, int maxValueLength = 200)
    {
        ArgumentNullException.ThrowIfNull(details);
        return details with
        {
            Event = Apply(details.Event, level, maxValueLength),
            RelatedEvents = [.. details.RelatedEvents.Select(e => Apply(e, level, maxValueLength))],
        };
    }

    /// <summary>The event ready for the notification formatters: masked for <paramref name="level"/> and every
    /// directory string made safe by <see cref="UntrustedText"/>.</summary>
    public static WatchEvent ForNotification(WatchEvent watchEvent, DetailLevel level, int maxValueLength = 200)
    {
        var masked = Apply(watchEvent, level, maxValueLength);
        var attribution = masked.Attribution;
        return masked with
        {
            Object = Safe(masked.Object),
            PreviousDn = Safe(masked.PreviousDn),
            Member = masked.Member is null ? null : Safe(masked.Member),
            Via = [.. masked.Via.Select(Safe)],
            Changes = [.. masked.Changes.Select(SafeChange)],
            Burst = masked.Burst is null ? null : masked.Burst with { Objects = [.. masked.Burst.Objects.Select(Safe)] },
            Lockout = masked.Lockout is null
                ? null
                : masked.Lockout with
                {
                    CallerComputer = Safe(masked.Lockout.CallerComputer),
                    Sources = [.. masked.Lockout.Sources.Select(s => s with { IpAddress = Safe(s.IpAddress), Workstation = Safe(s.Workstation) })],
                },
            Attribution = attribution with
            {
                Actor = attribution.Actor is null ? null : Safe(attribution.Actor),
                Candidates = [.. attribution.Candidates.Select(Safe)],
                Workstation = Safe(attribution.Workstation),
                IpAddress = Safe(attribution.IpAddress),
                Evidence = [.. attribution.Evidence.Select(e => e with { Dc = UntrustedText.Sanitize(e.Dc) })],
            },
            Domain = Safe(masked.Domain),
            OriginatingDc = Safe(masked.OriginatingDc),
            AcknowledgedBy = Safe(masked.AcknowledgedBy),
            AcknowledgeComment = masked.AcknowledgeComment is null ? null : UntrustedText.Sanitize(masked.AcknowledgeComment, multiLine: true),
        };
    }

    private static AttributeChange FilterChange(AttributeChange change, DetailLevel level, int maxValueLength)
    {
        switch (Visibility(change, level))
        {
            case ValueVisibility.Secret:
                return Strip(change) with { Redacted = true };
            case ValueVisibility.NameOnly:
                return Strip(change) with { FlagsAdded = [], FlagsRemoved = [] };
            case ValueVisibility.Hidden:
                return Strip(change);
            default:
                var truncated = change.Truncated;
                return change with
                {
                    OldValues = Cap(change.OldValues, maxValueLength, ref truncated),
                    NewValues = Cap(change.NewValues, maxValueLength, ref truncated),
                    Added = Cap(change.Added, maxValueLength, ref truncated),
                    Removed = Cap(change.Removed, maxValueLength, ref truncated),
                    Truncated = truncated,
                };
        }
    }

    private static AttributeChange Strip(AttributeChange change) => change with
    {
        OldValues = [],
        NewValues = [],
        Added = [],
        Removed = [],
        OldCount = Math.Max(change.OldCount, change.OldValues.Count),
        NewCount = Math.Max(change.NewCount, change.NewValues.Count),
    };

    private static List<string> Cap(IReadOnlyList<string> values, int maxValueLength, ref bool truncated)
    {
        var max = Math.Max(16, maxValueLength);
        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (value.Length > max)
            {
                truncated = true;
                result.Add(value[..(max - 1)] + Ellipsis);
            }
            else
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static ObjectRef NameOnly(ObjectRef reference)
    {
        var name = reference.Name ?? (DistinguishedName.TryParse(reference.Dn, out var dn) && !dn.IsRoot ? dn.Name : reference.Dn);
        return reference with { Dn = name };
    }

    private static AttributeChange SafeChange(AttributeChange change) => change with
    {
        Attribute = UntrustedText.Sanitize(change.Attribute),
        OldValues = [.. change.OldValues.Select(v => UntrustedText.Sanitize(v, multiLine: true))],
        NewValues = [.. change.NewValues.Select(v => UntrustedText.Sanitize(v, multiLine: true))],
        Added = [.. change.Added.Select(v => UntrustedText.Sanitize(v, multiLine: true))],
        Removed = [.. change.Removed.Select(v => UntrustedText.Sanitize(v, multiLine: true))],
        FlagsAdded = [.. change.FlagsAdded.Select(v => UntrustedText.Sanitize(v))],
        FlagsRemoved = [.. change.FlagsRemoved.Select(v => UntrustedText.Sanitize(v))],
    };

    private static ObjectRef Safe(ObjectRef reference) => reference with
    {
        Dn = UntrustedText.Sanitize(reference.Dn),
        Name = Safe(reference.Name),
        DisplayName = Safe(reference.DisplayName),
        SamAccountName = Safe(reference.SamAccountName),
        Sid = Safe(reference.Sid),
    };

    private static PrincipalRef Safe(PrincipalRef principal) => principal with
    {
        Sid = Safe(principal.Sid),
        Account = Safe(principal.Account),
        DisplayName = Safe(principal.DisplayName),
        Dn = Safe(principal.Dn),
    };

    private static string? Safe(string? value) => value is null ? null : UntrustedText.Sanitize(value);
}
