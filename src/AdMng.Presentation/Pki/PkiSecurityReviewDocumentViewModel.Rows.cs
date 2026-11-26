using System.Globalization;
using System.Text.RegularExpressions;
using AdMng.Core.Ldap;
using AdMng.Core.Pki;
using AdMng.Core.PrivilegedAccess;
using AdMng.Presentation.AccountReview;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.Pki;

/// <summary>
/// Builds the rows of <i>Przegląd bezpieczeństwa AD CS</i> from a <see cref="PkiEscResult"/> and the snapshot it was read
/// from (2.5): the localized finding texts with the names the findings carry (a well-known principal the reader could only
/// name by SID gets its localized name), the affected object, the principals with their account counts, the notes, the
/// related CAs and templates, the tool ids and the navigation targets. Rows of one object stay together, the object with the
/// most severe finding first. Pure: nothing is read here.
/// </summary>
internal sealed partial class PkiFindingRows(ILocalizer localizer, PkiSnapshot snapshot)
{
    /// <summary>This Organization: not in the security settings editor's well-known principals.</summary>
    private const string ThisOrganizationSid = "S-1-5-15";

    private readonly ILocalizer _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    private readonly PkiSnapshot _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    private Dictionary<Guid, CertificateTemplate>? _templates;
    private Dictionary<Guid, PkiEnrollmentService>? _cas;
    private Dictionary<Guid, PkiOidObject>? _oids;

    private Dictionary<Guid, CertificateTemplate> Templates => _templates ??= _snapshot.Templates
        .Where(t => t.ObjectGuid != Guid.Empty).DistinctBy(t => t.ObjectGuid).ToDictionary(t => t.ObjectGuid);

    private Dictionary<Guid, PkiEnrollmentService> Cas => _cas ??= _snapshot.EnterpriseCas.DistinctBy(c => c.ObjectGuid).ToDictionary(c => c.ObjectGuid);

    private Dictionary<Guid, PkiOidObject> Oids => _oids ??= _snapshot.Oids.DistinctBy(o => o.ObjectGuid).ToDictionary(o => o.ObjectGuid);

    /// <summary>The headings of the details in the UI language.</summary>
    public PkiFindingHeadings Headings => new()
    {
        Condition = _localizer[L.PkiSecurity.ConditionHeading],
        AttackPath = _localizer[L.PkiSecurity.AttackPathHeading],
        What = _localizer[L.PkiSecurity.WhatHeading],
        Object = _localizer[L.PkiSecurity.ObjectHeading],
        Principals = _localizer[L.PkiSecurity.PrincipalsHeading],
        Notes = _localizer[L.PkiSecurity.NotesHeading],
        Related = _localizer[L.PkiSecurity.RelatedHeading],
    };

    /// <summary>The rows of <paramref name="result"/>: grouped by object (the most severe object first), most severe first inside.</summary>
    public IReadOnlyList<PkiFindingRowViewModel> Rows(PkiEscResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var order = PkiFindingCodes.All.Select((d, i) => (d.Code, i)).ToDictionary(p => p.Code, p => p.i, StringComparer.Ordinal);
        var compare = StringComparer.Create(_localizer.Culture, ignoreCase: true);
        return [.. result.Findings
            .Where(f => order.ContainsKey(f.Code))
            .Select((f, index) => (Row: Row(f), Index: index))
            .GroupBy(r => r.Row.GroupText, StringComparer.Ordinal)
            .OrderByDescending(g => g.Max(r => r.Row.Severity))
            .ThenBy(g => g.Key, compare)
            .SelectMany(g => g
                .OrderByDescending(r => r.Row.Severity)
                .ThenBy(r => order[r.Row.Code])
                .ThenBy(r => r.Index)
                .Select(r => r.Row))];
    }

    /// <summary>One finding as a row.</summary>
    public PkiFindingRowViewModel Row(PkiFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var definition = PkiFindingCodes.Find(finding.Code);
        var severity = PkiFindingText.Severity(_localizer, finding.Severity);
        var area = PkiFindingText.Area(_localizer, finding.Area);
        var objectText = ObjectText(finding);
        var title = _localizer[PkiFindingText.TitleKey(finding.Code)];
        var message = Message(finding);
        var principals = finding.Principals
            .DistinctBy(p => (p.Sid.ToUpperInvariant(), p.Right))
            .Select(PrincipalLine)
            .ToList();
        var notes = PkiTextKeys.Notes(_localizer, finding.Notes);
        var related = Related(finding);
        var ids = _localizer.Format(L.PkiSecurity.Ids, definition.Esc, definition.Locksmith ?? _localizer[L.PkiSecurity.NoId],
            definition.Mdi ?? _localizer[L.PkiSecurity.NoId]);
        var principalsText = PrincipalsText(finding);
        var (templateCn, caDn, openDn, openText) = Targets(finding);
        return new PkiFindingRowViewModel
        {
            Finding = finding,
            Definition = definition,
            SeverityText = severity,
            AreaText = area,
            ObjectText = objectText,
            GroupText = _localizer.Format(L.PkiSecurity.GroupHeader, area, objectText),
            Title = title,
            Message = message,
            Explanation = _localizer[PkiFindingText.ExplanationKey(finding.Code)],
            Recommendation = _localizer[PkiFindingText.RecommendationKey(finding.Code)],
            IdsText = ids,
            PrincipalsText = principalsText,
            PrincipalLines = principals,
            NoteLines = notes,
            RelatedLines = related,
            TemplateCn = templateCn,
            CaDn = caDn,
            OpenDn = openDn,
            OpenObjectText = openText,
            Key = string.Join("\u001F", [finding.Code, finding.ObjectId?.ToString("N") ?? string.Empty, finding.ObjectDn?.ToUpperInvariant() ?? string.Empty, .. finding.Arguments]),
            SearchText = string.Join(" ", [title, message, objectText, principalsText, .. principals, .. notes, .. related, definition.Esc, ids, area, severity, finding.Code]),
        };
    }

    /// <summary>The name of a principal: the one the reader resolved, a well-known principal's localized name, else the SID.</summary>
    public string PrincipalName(PkiPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return string.IsNullOrWhiteSpace(principal.Name) ? WellKnownName(_localizer, principal.Sid) ?? principal.Sid : principal.Name;
    }

    /// <summary>The localized name of a well-known principal (Everyone, Authenticated Users, BUILTIN\Users …), null for another SID.</summary>
    public static string? WellKnownName(ILocalizer localizer, string sid)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (string.IsNullOrWhiteSpace(sid))
        {
            return null;
        }

        if (string.Equals(sid, ThisOrganizationSid, StringComparison.OrdinalIgnoreCase))
        {
            return localizer[L.PkiSecurity.PrincipalThisOrganization];
        }

        foreach (var (id, wellKnown) in SecuritySettingsText.WellKnownPrincipals)
        {
            if (string.Equals(sid, wellKnown, StringComparison.OrdinalIgnoreCase))
            {
                return localizer[SecuritySettingsText.PrincipalKey(id)];
            }
        }

        foreach (var (id, local, _) in SecuritySettingsText.LocalGroups)
        {
            if (string.Equals(sid, local, StringComparison.OrdinalIgnoreCase))
            {
                return localizer[SecuritySettingsText.LocalGroupNameKey(id)];
            }
        }

        return null;
    }

    /// <summary>The name of an object of the snapshot by its GUID (template, enterprise CA, OID object), null when unknown.</summary>
    public string? NameOf(Guid id) =>
        Templates.TryGetValue(id, out var template) ? TemplateName(template)
        : Cas.TryGetValue(id, out var ca) ? CaName(ca)
        : Oids.TryGetValue(id, out var oid) ? oid.DisplayName ?? oid.Oid
        : null;

    /// <summary>The leaf value of a DN (display only – never used to build a DN or filter).</summary>
    public static string LeafName(string? dn) =>
        dn is null ? string.Empty : DistinguishedName.TryParse(dn, out var parsed) && parsed.Name.Length > 0 ? parsed.Name : dn;

    /// <summary>The template's cn of a template finding (for the filter and <i>Otwórz szablon</i>), null otherwise.</summary>
    public CertificateTemplate? TemplateOf(Guid? id) => id is { } guid && Templates.TryGetValue(guid, out var template) ? template : null;

    private static string TemplateName(CertificateTemplate template) => string.IsNullOrWhiteSpace(template.DisplayName) ? template.Cn : template.DisplayName;

    private static string CaName(PkiEnrollmentService ca) => string.IsNullOrWhiteSpace(ca.DisplayName) ? ca.Name : ca.DisplayName;

    /// <summary>The affected object's name.</summary>
    private string ObjectText(PkiFinding finding)
    {
        if (finding.Code == PkiFindingCodes.WeakExplicitMapping && finding.ObjectDn is null)
        {
            return _localizer.Format(L.PkiSecurity.AggregatedAccounts, Argument(finding, 0));
        }

        if (finding.ObjectId is { } id && NameOf(id) is { } name)
        {
            return name;
        }

        return finding.Arguments.Count > 0 && finding.Arguments[0].Length > 0 ? finding.Arguments[0] : LeafName(finding.ObjectDn);
    }

    /// <summary>The finding's message with its arguments shown in the UI language (ESC14: the wording of its variant).</summary>
    private string Message(PkiFinding finding)
    {
        var expected = PkiFindingText.ArgumentCounts.GetValueOrDefault(finding.Code);
        if (finding.Code == PkiFindingCodes.WeakExplicitMapping && finding.Arguments.Count >= 3)
        {
            var kinds = Shown(finding, 1);
            return finding.ObjectDn is null ? _localizer.Format(L.PkiSecurity.Esc14Aggregated, finding.Arguments[0], kinds)
                : finding.Principals.Count > 0 ? _localizer.Format(L.PkiSecurity.Esc14Write, finding.Arguments[0], kinds, Shown(finding, 2))
                : _localizer.Format(L.PkiSecurity.Esc14Tier0Mapping, finding.Arguments[0], kinds);
        }

        var arguments = Enumerable.Range(0, Math.Max(expected, finding.Arguments.Count)).Select(i => (object)Shown(finding, i)).ToArray();
        return _localizer.Format(PkiFindingText.MessageKey(finding.Code), arguments);
    }

    /// <summary>One argument as the message shows it: enum names localized, SIDs named, an unknown count said in words.</summary>
    private string Shown(PkiFinding finding, int index)
    {
        var argument = Argument(finding, index);
        var text = PkiFindingText.Argument(_localizer, finding.Code, index, argument);
        if (index == 2 && text == "?" && PkiFindingCodes.Find(finding.Code).Group == PkiRuleGroup.Enrollment)
        {
            return _localizer[L.PkiSecurity.CountUnknown];
        }

        return NameSids(text);
    }

    private static string Argument(PkiFinding finding, int index) => index < finding.Arguments.Count ? finding.Arguments[index] : string.Empty;

    /// <summary>The items of a list argument that are SIDs, replaced by the names of the well-known principals.</summary>
    private string NameSids(string text)
    {
        if (!text.Contains("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var items = text.Split(PkiFindingCodes.ListSeparator);
        return string.Join(PkiFindingCodes.ListSeparator, items.Select(item =>
            SidPattern().IsMatch(item) ? WellKnownName(_localizer, item) ?? item : item));
    }

    /// <summary>"Użytkownicy domeny (152), GG_IT (4)" for the list column, "–" when the finding names no principal.</summary>
    private string PrincipalsText(PkiFinding finding)
    {
        var principals = finding.Principals.DistinctBy(p => p.Sid.ToUpperInvariant()).ToList();
        if (principals.Count == 0)
        {
            return _localizer[L.PkiSecurity.NoId];
        }

        return string.Join(PkiFindingCodes.ListSeparator, principals.Select(p => p.AccountCount >= 0 && p.IsResolved
            ? PrincipalName(p) + " (" + p.AccountCount.ToString(_localizer.Culture) + ")"
            : PrincipalName(p)));
    }

    /// <summary>"Użytkownicy domeny · Szeroka grupa · kont: 152 · Rejestruj".</summary>
    private string PrincipalLine(PkiPrincipal principal)
    {
        var count = !principal.IsResolved ? _localizer[L.PkiSecurity.AccountCountOtherDomain]
            : principal.AccountCount < 0 ? _localizer[L.PkiSecurity.AccountCountUnknown]
            : _localizer.Format(L.PkiSecurity.AccountCount, principal.AccountCount.ToString("N0", _localizer.Culture));
        var line = _localizer.Format(L.PkiSecurity.PrincipalLine, PrincipalName(principal), _localizer[PkiTextKeys.ClassKey(principal.Class)], count);
        return RightName(principal.Right) is { } right ? _localizer.Format(L.PkiSecurity.PrincipalLineRight, line, right) : line;
    }

    /// <summary>A <see cref="TemplateRight"/> or <see cref="AclRight"/> name in the UI language.</summary>
    private string? RightName(string? right) =>
        right is null ? null
        : Enum.GetNames<TemplateRight>().Contains(right, StringComparer.Ordinal) ? _localizer[PkiTextKeys.RightKey(Enum.Parse<TemplateRight>(right))]
        : Enum.GetNames<AclRight>().Contains(right, StringComparer.Ordinal) ? _localizer[PrivilegedAccessText.AclRightKey(Enum.Parse<AclRight>(right))]
        : right;

    /// <summary>The related CAs, templates and OID objects (one line each, in the finding's order).</summary>
    private List<string> Related(PkiFinding finding)
    {
        var lines = new List<string>();
        foreach (var id in finding.RelatedObjectIds.Distinct())
        {
            if (id == finding.ObjectId)
            {
                continue;
            }

            if (Cas.TryGetValue(id, out var ca))
            {
                lines.Add(_localizer.Format(L.PkiSecurity.RelatedCa, CaName(ca)));
            }
            else if (Templates.TryGetValue(id, out var template))
            {
                lines.Add(_localizer.Format(L.PkiSecurity.RelatedTemplate, TemplateName(template)));
            }
            else if (Oids.TryGetValue(id, out var oid))
            {
                lines.Add(_localizer.Format(L.PkiSecurity.RelatedOid, oid.DisplayName ?? oid.Oid));
            }
        }

        return lines;
    }

    /// <summary>Where the finding's navigation leads: the template, the CA (its own or the first publishing one), the object.</summary>
    private (string? TemplateCn, string? CaDn, string? OpenDn, string OpenText) Targets(PkiFinding finding)
    {
        var template = finding.Area == PkiFindingArea.Template ? TemplateOf(finding.ObjectId) : null;
        var ownCa = _snapshot.EnterpriseCas.FirstOrDefault(c =>
            (finding.ObjectId is { } id && c.ObjectGuid == id) || DistinguishedName.AreEqual(c.Dn, finding.ObjectDn));
        var caComputer = finding.ObjectDn is null ? null
            : _snapshot.EnterpriseCas.FirstOrDefault(c => c.ComputerDn is not null && DistinguishedName.AreEqual(c.ComputerDn, finding.ObjectDn));
        var ca = ownCa ?? caComputer ?? finding.RelatedObjectIds.Select(id => Cas.GetValueOrDefault(id)).FirstOrDefault(c => c is not null);
        var openDn = finding.Area is PkiFindingArea.Account or PkiFindingArea.PkiObject && ownCa is null ? finding.ObjectDn : null;
        var openText = finding.Area == PkiFindingArea.Account ? _localizer[L.PkiSecurity.OpenAccount]
            : caComputer is not null ? _localizer[L.PkiSecurity.OpenComputer]
            : _localizer[L.PkiSecurity.OpenObject];
        return (template?.Cn, ca?.Dn, openDn, openText);
    }

    /// <summary>A SID (<c>S-1-…</c>) as the reader writes an unnamed principal.</summary>
    [GeneratedRegex(@"^S-1-\d+(-\d+)+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SidPattern();

    /// <summary>The names of the notices' objects (templates, CAs and OID objects by GUID, else the DN's leaf), at most five.</summary>
    public string NoticeObjects(IEnumerable<PkiReadNotice> notices)
    {
        ArgumentNullException.ThrowIfNull(notices);
        var names = notices
            .Select(n => (n.ObjectGuid is { } id ? NameOf(id) : null) ?? (n.ObjectDn is null ? null : LeafName(n.ObjectDn)))
            .OfType<string>()
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        const int Shown = 5;
        var text = string.Join(PkiFindingCodes.ListSeparator, names.Take(Shown));
        return names.Count > Shown
            ? text + " " + _localizer.Format(L.PkiSecurity.NoteMoreObjects, (names.Count - Shown).ToString(CultureInfo.CurrentCulture))
            : text;
    }
}
