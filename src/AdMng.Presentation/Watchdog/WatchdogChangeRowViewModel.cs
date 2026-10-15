using AdMng.Core.Schema;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Presentation.Watchdog;

/// <summary>
/// One changed attribute in the event details: display name (from the session schema when connected, else the LDAP
/// name), old → new values, values or flags added and removed; secret values are shown as "wartość ukryta".
/// </summary>
public sealed class WatchdogChangeRowViewModel
{
    private WatchdogChangeRowViewModel(string attribute, string ldapName)
    {
        Attribute = attribute;
        LdapName = ldapName;
    }

    /// <summary>Display name of the attribute ("Dział").</summary>
    public string Attribute { get; }

    /// <summary>LDAP name ("department"), shown as a tool tip.</summary>
    public string LdapName { get; }

    public string OldText { get; private init; } = string.Empty;

    public string NewText { get; private init; } = string.Empty;

    /// <summary>"+DONT_EXPIRE_PASSWORD, −ACCOUNTDISABLE" / "+MSSQLSvc/…" – what was added and removed.</summary>
    public string ChangeText { get; private init; } = string.Empty;

    /// <summary>Secret or confidential: no values were recorded.</summary>
    public bool IsRedacted { get; private init; }

    /// <summary>Values were capped by the service.</summary>
    public bool IsTruncated { get; private init; }

    /// <param name="change">The change as recorded by the service.</param>
    /// <param name="localizer">UI texts.</param>
    /// <param name="schema">Schema of the open AD session (display names), or null.</param>
    /// <param name="useSchema">False for the watchdog's own events (configuration paths are no attributes).</param>
    public static WatchdogChangeRowViewModel Create(AttributeChange change, ILocalizer localizer, ISchemaCatalog? schema, bool useSchema = true)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(localizer);
        var name = useSchema && !string.IsNullOrWhiteSpace(change.Attribute) && !change.Attribute.Contains('[', StringComparison.Ordinal)
            ? localizer.AttributeName(change.Attribute, schema)
            : change.Attribute;
        if (change.Redacted)
        {
            var hidden = localizer[L.Watchdog.ValueHidden];
            return new WatchdogChangeRowViewModel(name, change.Attribute) { OldText = hidden, NewText = hidden, IsRedacted = true };
        }

        var truncated = change.Truncated ? " " + localizer[L.Watchdog.ValueTruncated] : string.Empty;
        IEnumerable<string> changes =
        [
            .. change.FlagsAdded.Select(f => "+" + f),
            .. change.FlagsRemoved.Select(f => "−" + f),
            .. change.Added.Select(v => "+" + v),
            .. change.Removed.Select(v => "−" + v),
        ];
        return new WatchdogChangeRowViewModel(name, change.Attribute)
        {
            OldText = Join(change.OldValues, localizer) + (change.OldValues.Count > 0 ? truncated : string.Empty),
            NewText = Join(change.NewValues, localizer) + (change.NewValues.Count > 0 ? truncated : string.Empty),
            ChangeText = string.Join(", ", changes),
            IsTruncated = change.Truncated,
        };
    }

    private static string Join(IReadOnlyList<string> values, ILocalizer localizer) =>
        values.Count == 0 ? localizer[L.Watchdog.ValueEmpty] : string.Join("; ", values);
}
