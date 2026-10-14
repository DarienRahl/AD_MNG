using System.Globalization;
using AdMng.Core.Ldap;
using AdMng.Watchdog.Contracts.Configuration;
using AdMng.Watchdog.Contracts.Events;
using EventAttribution = AdMng.Watchdog.Contracts.Events.Attribution;

namespace AdMng.Watchdog.Core.Formatting;

/// <summary>
/// Display strings of objects, actors and sources (section 14.1): "Jan Kowalski (jan.kowalski)", OU paths
/// "Firma › Pracownicy › IT", the actor with its confidence or the unresolved reason, the source with the IP address
/// first and the origin of a computer name ("zgłoszona przez klienta", "DNS"). Inputs are sanitized events.
/// </summary>
public sealed class ObjectDisplay
{
    private readonly NotificationText _text;
    private readonly TimeFormatter _time;

    public ObjectDisplay(NotificationText text, TimeFormatter time)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(time);
        _text = text;
        _time = time;
    }

    /// <summary>"Jan Kowalski (jan.kowalski)", "WS-001", "Domain Admins".</summary>
    public string Name(ObjectRef? reference)
    {
        if (reference is null)
        {
            return string.Empty;
        }

        var sam = reference.SamAccountName?.TrimEnd('$');
        var display = FirstNonEmpty(reference.DisplayName, reference.Name, Leaf(reference.Dn), sam, reference.Dn) ?? string.Empty;
        return !string.IsNullOrEmpty(sam) && !string.Equals(sam, display, StringComparison.OrdinalIgnoreCase)
            ? display + " (" + sam + ")"
            : display;
    }

    /// <summary>"Jan Kowalski (jan.kowalski) · Firma › Pracownicy › IT".</summary>
    public string NameWithPath(ObjectRef? reference)
    {
        var name = Name(reference);
        var path = ParentPath(reference?.Dn);
        return path.Length == 0 ? name : name + " · " + path;
    }

    /// <summary>The OU/container path of the parent of <paramref name="dn"/> from the top: "Firma › Pracownicy › IT".</summary>
    public static string ParentPath(string? dn)
    {
        if (!DistinguishedName.TryParse(dn, out var parsed) || parsed.Parent is not { } parent)
        {
            return string.Empty;
        }

        var names = parent.Rdns.Where(r => !string.Equals(r.Type, "DC", StringComparison.OrdinalIgnoreCase)).Select(r => r.Value).Reverse();
        return string.Join(" › ", names);
    }

    /// <summary>The RDN of the common parent of all objects ("OU=Sprzedaż"), or null when they differ.</summary>
    public static string? CommonParentRdn(IEnumerable<WatchEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        DistinguishedName? common = null;
        foreach (var watchEvent in events)
        {
            if (!DistinguishedName.TryParse(watchEvent.Object.Dn, out var dn) || dn.Parent is not { IsRoot: false } parent)
            {
                return null;
            }

            if (common is null)
            {
                common = parent;
            }
            else if (!common.Equals(parent))
            {
                return null;
            }
        }

        return common?.Leaf.ToString();
    }

    /// <summary>The nesting path of a privilege change: "GG_Admins_Serwery → Domain Admins".</summary>
    public string ViaPath(WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        return string.Join(" → ", watchEvent.Via.Select(Name).Where(n => n.Length > 0));
    }

    /// <summary>The actor row: "CONTOSO\adm.nowak (Anna Nowak) · pewność: wysoka", "System (DC01)", "ustalanie…",
    /// "nieustalony – reason (CODE)".</summary>
    public string Actor(EventAttribution? attribution, bool followUpExpected = false)
    {
        var a = attribution ?? EventAttribution.Pending;
        switch (a.Status)
        {
            case AttributionStatus.NotApplicable:
                return _text.Get("Value.NotApplicable");
            case AttributionStatus.Pending:
                return followUpExpected ? _text.Get("Value.ActorPendingFollowUp") : _text.Get("Value.ActorPending");
            case AttributionStatus.Unknown or AttributionStatus.Ambiguous:
                var code = a.Reason ?? (a.Status == AttributionStatus.Ambiguous ? AttributionReasons.Ambiguous : AttributionReasons.NoMatch);
                var candidates = a.Candidates.Count == 0
                    ? string.Empty
                    : " · " + _text.Get("Label.Candidates") + ": " + string.Join(", ", a.Candidates.Select(Principal));
                return _text.Format("Value.ActorUnknown", Reason(code), code) + candidates;
        }

        if (string.Equals(a.Reason, AttributionReasons.System, StringComparison.Ordinal))
        {
            var dc = a.Evidence.FirstOrDefault()?.Dc;
            return string.IsNullOrEmpty(dc) ? _text.Get("Value.System") : _text.Get("Value.System") + " (" + ShortHost(dc) + ")";
        }

        var actor = a.Actor is null ? _text.Get("Value.Unknown") : Principal(a.Actor);
        return actor + " · " + _text.Format("Value.Confidence", Confidence(a.Confidence));
    }

    /// <summary>The actor in a subject or summary line: account, "System", or null when not resolved.</summary>
    public string? ActorShort(EventAttribution? attribution)
    {
        if (attribution is not { Status: AttributionStatus.Resolved } a)
        {
            return null;
        }

        if (string.Equals(a.Reason, AttributionReasons.System, StringComparison.Ordinal))
        {
            return _text.Get("Value.System");
        }

        return FirstNonEmpty(a.Actor?.Account, a.Actor?.DisplayName, a.Actor?.Sid);
    }

    /// <summary>"CONTOSO\adm.nowak (Anna Nowak)".</summary>
    public string Principal(PrincipalRef principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var account = FirstNonEmpty(principal.Account, principal.Sid, principal.Dn) ?? string.Empty;
        return !string.IsNullOrEmpty(principal.DisplayName) && !string.Equals(principal.DisplayName, account, StringComparison.OrdinalIgnoreCase)
            ? account + " (" + principal.DisplayName + ")"
            : account;
    }

    /// <summary>The source row: "10.0.12.34 · WS-ADM-01 (DNS) · logowanie sieciowe (4624) · pewność: niska", or null when
    /// there is nothing to show.</summary>
    public string? Source(EventAttribution? attribution)
    {
        if (attribution is null || attribution.Status == AttributionStatus.NotApplicable)
        {
            return null;
        }

        var parts = new List<string>();
        if (string.Equals(attribution.SourceMethod, "local", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(_text.Get("Source.Local"));
        }
        else
        {
            if (!string.IsNullOrEmpty(attribution.IpAddress))
            {
                parts.Add(attribution.IpAddress);
            }

            if (!string.IsNullOrEmpty(attribution.Workstation))
            {
                parts.Add(attribution.Workstation + " (" + WorkstationOrigin(attribution.SourceMethod) + ")");
            }

            if (parts.Count > 0 && Method(attribution) is { } method)
            {
                parts.Add(method);
            }
        }

        if (parts.Count > 0)
        {
            parts.Add(_text.Format("Value.Confidence", Confidence(attribution.SourceConfidence)));
            return string.Join(" · ", parts);
        }

        return attribution.SourceReason is { } reason ? _text.Format("Value.SourceUnknown", Reason(reason), reason) : null;
    }

    /// <summary>"10.0.0.12 (WS-01) × 5 · 10.0.0.13 × 2" – the bad-password sources of a lockout.</summary>
    public string? LockoutSources(LockoutInfo? lockout)
    {
        if (lockout is null)
        {
            return null;
        }

        var sources = lockout.Sources
            .OrderByDescending(s => s.Count)
            .Select(s =>
            {
                var where = FirstNonEmpty(s.IpAddress, s.Workstation) ?? "?";
                if (!string.IsNullOrEmpty(s.IpAddress) && !string.IsNullOrEmpty(s.Workstation))
                {
                    where += " (" + s.Workstation + ")";
                }

                return where + " × " + s.Count.ToString(CultureInfo.InvariantCulture);
            })
            .ToList();
        if (!string.IsNullOrEmpty(lockout.CallerComputer))
        {
            sources.Add(_text.Format("Value.LockoutCaller", lockout.CallerComputer));
        }

        return sources.Count == 0 ? null : string.Join(" · ", sources);
    }

    /// <summary>"dc01.contoso.com (zdarzenie 4728, rekord 918273)" – where the change originated, or null.</summary>
    public string? Where(WatchEvent watchEvent)
    {
        ArgumentNullException.ThrowIfNull(watchEvent);
        var evidence = watchEvent.Attribution?.Evidence.FirstOrDefault();
        var dc = FirstNonEmpty(watchEvent.OriginatingDc, evidence?.Dc);
        if (dc is null)
        {
            return null;
        }

        return evidence is null ? dc : dc + " (" + _text.Format("Value.Evidence", evidence.EventCode, evidence.RecordId) + ")";
    }

    /// <summary>The localized name of an unresolved reason code ("Nie znaleziono pasującego zdarzenia …").</summary>
    public string Reason(string code) => _text.Find("Reason." + code) ?? code;

    /// <summary>The one-line remedy of a reason code, or null.</summary>
    public string? Remedy(string code) => _text.Find("Reason." + code + ".Remedy");

    /// <summary>"wysoka", "średnia", "niska", "brak".</summary>
    public string Confidence(AttributionConfidence confidence) => _text.Get("Confidence." + confidence);

    /// <summary>The configured rule name, or the localized built-in name when the built-in rule kept its shipped name.</summary>
    public string RuleName(RuleSettings? rule)
    {
        if (rule is null)
        {
            return string.Empty;
        }

        if (rule.BuiltIn && NotificationText.Polish.Find("Rule." + rule.Id) is { } polish && NotificationText.English.Find("Rule." + rule.Id) is { } english
            && (string.Equals(rule.Name, polish, StringComparison.Ordinal) || string.Equals(rule.Name, english, StringComparison.Ordinal)))
        {
            return _text.Get("Rule." + rule.Id);
        }

        return UntrustedText.Sanitize(rule.Name);
    }

    /// <summary>The expiry row: "konto wygasa z końcem dnia 21.10.2026 (pozostało dni: 7)".</summary>
    public string Expiry(ExpiryInfo expiry)
    {
        ArgumentNullException.ThrowIfNull(expiry);
        var key = expiry.Kind == ExpiryKind.Account ? "Value.AccountExpiresEndOfDay" : "Value.PasswordExpiresOn";
        var when = expiry.Kind == ExpiryKind.Account ? _time.Date(expiry.LastValidDay) : _time.Local(expiry.ExpiresAt);
        return _text.Format(key, when, expiry.DaysLeft);
    }

    /// <summary>The burst row: "liczba: 12 w ciągu 10 min".</summary>
    public string Burst(BurstInfo burst)
    {
        ArgumentNullException.ThrowIfNull(burst);
        return _text.Format("Value.Burst", burst.Count, (int)Math.Round(burst.Window.TotalMinutes));
    }

    private string WorkstationOrigin(string? method) =>
        string.Equals(method, "dns", StringComparison.OrdinalIgnoreCase) ? _text.Get("Source.Dns") : _text.Get("Source.ClientReported");

    private string? Method(EventAttribution attribution)
    {
        var method = attribution.SourceMethod;
        if (string.IsNullOrEmpty(method) || string.Equals(method, "dns", StringComparison.OrdinalIgnoreCase))
        {
            return attribution.LogonType is { } dnsLogon ? LogonType(dnsLogon, "4624") : null;
        }

        return string.Equals(method, "4624", StringComparison.Ordinal) && attribution.LogonType is { } logonType
            ? LogonType(logonType, method)
            : _text.Format("Source.Event", method);
    }

    private string LogonType(int logonType, string eventCode) =>
        (_text.Find("Source.LogonType." + logonType.ToString(CultureInfo.InvariantCulture)) is { } name ? name + " " : string.Empty)
        + "(" + eventCode + ")";

    private static string ShortHost(string host)
    {
        var dot = host.IndexOf('.', StringComparison.Ordinal);
        return (dot > 0 ? host[..dot] : host).ToUpperInvariant();
    }

    private static string? Leaf(string? dn) =>
        DistinguishedName.TryParse(dn, out var parsed) && !parsed.IsRoot ? parsed.Name : null;

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
