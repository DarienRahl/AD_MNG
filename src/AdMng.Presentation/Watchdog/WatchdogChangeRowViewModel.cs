using AdMng.Core.Schema;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Presentation.Localization;
using AdMng.Watchdog.Contracts.Events;

namespace AdMng.Presentation.Watchdog;

/// <summary>How the changes of one event are read (what kind of names its rows carry).</summary>
/// <param name="UseSchema">Rows are directory attributes (display names from the session schema); false for the
/// watchdog's own events (configuration paths) and GPO settings.</param>
/// <param name="GpoSettings">Rows are GPO settings of SYSVOL (GroupPolicySettingsChanged, GroupPolicyContentOutOfBand):
/// the key is shown as a readable path (stage B design 8.5, 18 item 3).</param>
/// <param name="AuditSettings">The event carries the tag <c>auditSettings</c> (design 9.4): the value-less
/// nTSecurityDescriptor row reads <c>Value.AuditSettingsChanged</c>.</param>
public sealed record WatchdogChangeContext(bool UseSchema = true, bool GpoSettings = false, bool AuditSettings = false)
{
    /// <summary>The context of <paramref name="e"/>'s changes.</summary>
    public static WatchdogChangeContext Of(WatchEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var gpo = e.Kind is WatchEventKind.GroupPolicySettingsChanged or WatchEventKind.GroupPolicyContentOutOfBand;
        return new WatchdogChangeContext(
            UseSchema: e.Object.Kind != WatchObjectKind.Watchdog && !gpo,
            GpoSettings: gpo,
            AuditSettings: e.Kind == WatchEventKind.SecurityDescriptorChanged && e.Tags.Contains(EventTags.AuditSettings, StringComparer.Ordinal));
    }
}

/// <summary>
/// One changed attribute in the event details: display name (from the session schema when connected, else the LDAP
/// name), old → new values, values or flags added and removed; secret values are shown as "wartość ukryta". GPO settings
/// speak the operator's language (K6, <see cref="GpoSettingPresenter"/>): GPMC's path and the setting's name ("Konfiguracja
/// komputera › … › Przypisywanie praw użytkownika › Debugowanie programów"), readable values ("Administratorzy",
/// "1 (REG_DWORD)"), the key as tool tip; an unreadable file reads „nie udało się odczytać” and the summary row of a long
/// diff "nie pokazano N z M zmienionych ustawień".
/// </summary>
public sealed class WatchdogChangeRowViewModel
{
    /// <summary>Separator of the readable path of a GPO setting key.</summary>
    public const string PathSeparator = " › ";

    /// <summary>Attribute of the summary row of a truncated GPO diff (Core <c>GpoSettingsDiffer.SummaryAttribute</c>).</summary>
    public const string GpoSummaryAttribute = "gpo.moreChanges";

    /// <summary>Value of an unreadable GPO file's marker row (Core <c>GpoSettingsDiffer.UnreadableValue</c>).</summary>
    public const string GpoUnreadableValue = "unreadable";

    private WatchdogChangeRowViewModel(string attribute, string ldapName)
    {
        Attribute = attribute;
        LdapName = ldapName;
    }

    /// <summary>Display name of the attribute ("Dział"), or the readable path of a GPO setting.</summary>
    public string Attribute { get; }

    /// <summary>LDAP name ("department") or the GPO setting key, shown as a tool tip.</summary>
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
    public static WatchdogChangeRowViewModel Create(AttributeChange change, ILocalizer localizer, ISchemaCatalog? schema, bool useSchema = true) =>
        Create(change, localizer, schema, new WatchdogChangeContext(UseSchema: useSchema));

    /// <param name="change">The change as recorded by the service.</param>
    /// <param name="localizer">UI texts.</param>
    /// <param name="schema">Schema of the open AD session (display names), or null.</param>
    /// <param name="context">How the event's rows are read (<see cref="WatchdogChangeContext.Of"/>).</param>
    public static WatchdogChangeRowViewModel Create(AttributeChange change, ILocalizer localizer, ISchemaCatalog? schema, WatchdogChangeContext context) =>
        Create(change, localizer, schema, context, null);

    /// <param name="change">The change as recorded by the service.</param>
    /// <param name="localizer">UI texts.</param>
    /// <param name="schema">Schema of the open AD session (display names), or null.</param>
    /// <param name="context">How the event's rows are read (<see cref="WatchdogChangeContext.Of"/>).</param>
    /// <param name="presenter">GPO settings in the operator's language (SID and item names of the event); null: one without them.</param>
    public static WatchdogChangeRowViewModel Create(AttributeChange change, ILocalizer localizer, ISchemaCatalog? schema, WatchdogChangeContext context,
        GpoSettingPresenter? presenter)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(context);
        if (context.GpoSettings && string.Equals(change.Attribute, GpoSummaryAttribute, StringComparison.Ordinal))
        {
            return new WatchdogChangeRowViewModel(localizer[L.WatchdogLinux.ValueGpoMoreChangesName], change.Attribute)
            {
                NewText = localizer.Format(L.WatchdogLinux.ValueGpoMoreChanges, change.OldCount, change.NewCount),
                IsTruncated = true,
            };
        }

        if (context.GpoSettings && !string.IsNullOrEmpty(change.Attribute))
        {
            return GpoSetting(change, localizer, presenter ?? new GpoSettingPresenter(localizer));
        }

        var name = context.GpoSettings
            ? GpoSettingPath(change.Attribute)
            : context.UseSchema && !string.IsNullOrWhiteSpace(change.Attribute) && !change.Attribute.Contains('[', StringComparison.Ordinal)
                ? localizer.AttributeName(change.Attribute, schema)
                : change.Attribute;
        if (change.Redacted)
        {
            var hidden = localizer[L.Watchdog.ValueHidden];
            return new WatchdogChangeRowViewModel(name, change.Attribute) { OldText = hidden, NewText = hidden, IsRedacted = true };
        }

        if (context.AuditSettings && IsAuditSettingsRow(change))
        {
            return new WatchdogChangeRowViewModel(name, change.Attribute) { NewText = localizer[L.WatchdogLinux.ValueAuditSettingsChanged] };
        }

        var unreadableFile = context.GpoSettings && change.Attribute.StartsWith("File/", StringComparison.OrdinalIgnoreCase);
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
            OldText = Join(change.OldValues, localizer, unreadableFile) + (change.OldValues.Count > 0 ? truncated : string.Empty),
            NewText = Join(change.NewValues, localizer, unreadableFile) + (change.NewValues.Count > 0 ? truncated : string.Empty),
            ChangeText = string.Join(", ", changes),
            IsTruncated = change.Truncated,
        };
    }

    /// <summary>A GPO setting's row: GPMC's path and name, the values and the list changes in the operator's language.</summary>
    private static WatchdogChangeRowViewModel GpoSetting(AttributeChange change, ILocalizer localizer, GpoSettingPresenter presenter)
    {
        var key = change.Attribute;
        var text = presenter.Describe(key, change.NewValues.Count > 0 ? change.NewValues : change.OldValues, change.Redacted);
        var name = text.GroupPath + PathSeparator + text.Name;
        if (change.Redacted)
        {
            var hidden = localizer[L.Watchdog.ValueHidden];
            return new WatchdogChangeRowViewModel(name, key) { OldText = hidden, NewText = hidden, IsRedacted = true };
        }

        var unreadableFile = key.StartsWith("File/", StringComparison.OrdinalIgnoreCase);
        var truncated = change.Truncated ? " " + localizer[L.Watchdog.ValueTruncated] : string.Empty;
        string Values(IReadOnlyList<string> values) => values.Count == 0 ? localizer[L.Watchdog.ValueEmpty]
            : unreadableFile ? Join(values, localizer, unreadableFile: true)
            : presenter.Describe(key, values, masked: false).ValueText;
        IEnumerable<string> changes =
        [
            .. change.Added.Select(v => "+" + presenter.DescribeElement(key, v, signed: false)),
            .. change.Removed.Select(v => "−" + presenter.DescribeElement(key, v, signed: false)),
        ];
        return new WatchdogChangeRowViewModel(name, key)
        {
            OldText = Values(change.OldValues) + (change.OldValues.Count > 0 ? truncated : string.Empty),
            NewText = Values(change.NewValues) + (change.NewValues.Count > 0 ? truncated : string.Empty),
            ChangeText = string.Join(", ", changes),
            IsTruncated = change.Truncated,
        };
    }

    /// <summary>
    /// A GPO setting key as a readable path: its '/' segments and the value name after the last '!' of a registry key
    /// ("Computer/Registry/HKLM\Software\Policies\X!Enabled" → "Computer › Registry › HKLM\Software\Policies\X › Enabled").
    /// </summary>
    public static string GpoSettingPath(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0 && segments[^1].LastIndexOf('!') is var bang and > 0 && bang < segments[^1].Length - 1)
        {
            var last = segments[^1];
            segments[^1] = last[..bang];
            segments.Add(last[(bang + 1)..]);
        }

        return segments.Count == 0 ? key : string.Join(PathSeparator, segments);
    }

    private static bool IsAuditSettingsRow(AttributeChange change) =>
        string.Equals(change.Attribute, "nTSecurityDescriptor", StringComparison.OrdinalIgnoreCase)
        && change.OldValues.Count == 0 && change.NewValues.Count == 0 && change.Added.Count == 0 && change.Removed.Count == 0;

    private static string Join(IReadOnlyList<string> values, ILocalizer localizer, bool unreadableFile) =>
        values.Count == 0
            ? localizer[L.Watchdog.ValueEmpty]
            : string.Join("; ", values.Select(v => unreadableFile && string.Equals(v, GpoUnreadableValue, StringComparison.Ordinal) ? localizer[L.WatchdogLinux.ValueUnreadable] : v));
}
