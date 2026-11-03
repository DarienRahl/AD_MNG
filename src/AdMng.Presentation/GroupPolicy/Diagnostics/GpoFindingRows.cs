using System.Globalization;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Diagnostics;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.Ldap;
using AdMng.Presentation.GroupPolicy.SecuritySettings;
using AdMng.Presentation.Localization;
using AdMng.Presentation.Navigation;

namespace AdMng.Presentation.GroupPolicy.Diagnostics;

/// <summary>
/// Turns the findings of one check into rows (<see cref="GpoFindingRowViewModel"/>) in the UI language: the message with its
/// arguments made readable (SIDs as names, parts, loopback modes and K4 option names localized), the GPO and container names,
/// the setting described with the K4 names. Built per check and per UI language; holds the snapshot's names in memory only.
/// </summary>
public sealed class GpoFindingRows
{
    private readonly ILocalizer _localizer;
    private readonly GpoDiagnosticsSnapshot _snapshot;
    private readonly IReadOnlyDictionary<string, string> _names;
    private readonly Dictionary<Guid, GpoInfo> _gpos = [];
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<GpoLinkInfo>> _links;
    private readonly Dictionary<string, string> _principals;

    /// <param name="localizer">The UI language.</param>
    /// <param name="snapshot">The check's snapshot (GPO names, links, WMI filters).</param>
    /// <param name="names">Domain SIDs resolved to names (<see cref="ResolveNamesAsync"/>).</param>
    public GpoFindingRows(ILocalizer localizer, GpoDiagnosticsSnapshot snapshot, IReadOnlyDictionary<string, string> names)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _names = names ?? throw new ArgumentNullException(nameof(names));
        foreach (var gpo in snapshot.Gpos)
        {
            _gpos.TryAdd(gpo.Id, gpo);
        }

        _links = GroupPolicyReader.LinksByGpo(snapshot.Containers);
        _principals = LocalPrincipals(localizer);
    }

    /// <summary>The rows of <paramref name="findings"/> in their order.</summary>
    public IReadOnlyList<GpoFindingRowViewModel> Rows(IEnumerable<GpoFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return [.. findings.Select(Row)];
    }

    /// <summary>The row of one finding.</summary>
    public GpoFindingRowViewModel Row(GpoFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var code = finding.Code;
        var title = _localizer[GpoFindingText.TitleKey(code)];
        var message = _localizer.Format(GpoFindingText.MessageKey(code), Arguments(finding));
        var severity = GpoFindingText.Severity(_localizer, finding.Severity);
        var area = GpoFindingText.Area(_localizer, finding.Area);
        var gpoText = finding.GpoId is { } id ? GpoName(id, finding.GpoName) : "–";
        var settingText = finding.SettingKey is { } key ? DescribeSetting(key) : null;
        var containerText = finding.ContainerDn is { } dn ? ContainerText(dn) : null;
        var wmiFilter = WmiFilterText(finding);
        var where = settingText ?? containerText ?? wmiFilter ?? "–";

        var affected = new List<string>();
        if (finding.GpoId is { } gpoId)
        {
            affected.Add(_localizer.Format(L.GpoDiagnostics.AffectedGpo, gpoText + (gpoText.StartsWith('{') ? string.Empty : " " + GroupPolicyText.Guid(gpoId))));
        }

        if (containerText is not null)
        {
            affected.Add(_localizer.Format(L.GpoDiagnostics.AffectedContainer, containerText));
        }

        if (finding.RelatedGpoIds.Count > 0)
        {
            affected.Add(_localizer.Format(L.GpoDiagnostics.AffectedRelated, string.Join(", ", finding.RelatedGpoIds.Select(r => GpoName(r, null)))));
        }

        if (settingText is not null)
        {
            affected.Add(_localizer.Format(L.GpoDiagnostics.AffectedSetting, settingText));
            if (!string.Equals(settingText, finding.SettingKey, StringComparison.Ordinal))
            {
                affected.Add(_localizer.Format(L.GpoDiagnostics.AffectedSettingKey, finding.SettingKey));
            }
        }

        if (wmiFilter is not null)
        {
            affected.Add(_localizer.Format(L.GpoDiagnostics.AffectedWmiFilter, wmiFilter));
        }

        var fixText = finding.Fix is { } fix
            ? _localizer.Format(L.GpoDiagnostics.FixLine, GpoFindingText.Fix(_localizer, fix, NameOf))
            : _localizer[L.GpoDiagnostics.NoFix];
        return new GpoFindingRowViewModel
        {
            Finding = finding,
            SeverityText = severity,
            AreaText = area,
            GpoText = gpoText,
            WhereText = where,
            Title = title,
            Message = message,
            Explanation = _localizer[GpoFindingText.ExplanationKey(code)],
            Recommendation = _localizer[GpoFindingText.RecommendationKey(code)],
            AffectedLines = affected,
            FixText = fixText,
            Focus = finding.SettingKey is { Length: > 0 } settingKey && !GpoSettingKey.IsTruncated(settingKey) ? GroupPolicySettingFocus.ForSetting(settingKey) : null,
            GpoExists = finding.GpoId is { } existing && _gpos.ContainsKey(existing),
            ModelContainerDn = ModelContainer(finding),
            Key = KeyOf(finding),
            SearchText = string.Join('\n', title, message, gpoText, where, area, severity, code),
        };
    }

    /// <summary>The identity of a finding (code, GPO, container, setting, arguments).</summary>
    public static string KeyOf(GpoFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return string.Join('\u001F', finding.Code, finding.GpoId?.ToString() ?? string.Empty, finding.ContainerDn ?? string.Empty,
            finding.SettingKey ?? string.Empty, string.Join('\u001E', finding.Arguments));
    }

    /// <summary>
    /// The domain SIDs of the findings' principals resolved to names on the session's DC (one OR search per 50 SIDs, like
    /// every GPO document); local groups and well-known principals are named without a lookup. Never throws for names.
    /// </summary>
    public static Task<IReadOnlyDictionary<string, string>> ResolveNamesAsync(IDirectorySession session, IEnumerable<GpoFinding> findings, ILocalizer localizer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(localizer);
        var local = LocalPrincipals(localizer);
        var sids = findings.SelectMany(PrincipalSids)
            .Where(s => !local.ContainsKey(s) && TrusteeNames.WellKnown(localizer, s) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return sids.Count == 0
            ? Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            : TrusteeNames.ResolveAsync(session, sids, localizer, cancellationToken);
    }

    /// <summary>The name of a principal as written in a finding (<c>*S-1-5-11</c>, a SID, or a typed name).</summary>
    public string NameOf(string principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var text = principal.Trim().TrimStart('*');
        if (!text.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            return principal.Trim();
        }

        return _principals.TryGetValue(text, out var local) ? local
            : TrusteeNames.WellKnown(_localizer, text) is { } wellKnown ? wellKnown
            : _names.TryGetValue(text, out var name) ? name
            : text.ToUpperInvariant();
    }

    /// <summary>The name of a GPO of the snapshot (its GUID in braces when it does not exist).</summary>
    public string GpoName(Guid id, string? fallback) =>
        _gpos.TryGetValue(id, out var gpo) ? gpo.DisplayName : fallback ?? GroupPolicyText.Guid(id);

    /// <summary>"contoso.test", "contoso.test/Firma/Komputery", or a site's name.</summary>
    public string ContainerText(string dn)
    {
        ArgumentNullException.ThrowIfNull(dn);
        if (DistinguishedName.AreEqual(dn, _snapshot.DomainDn))
        {
            return _snapshot.DomainDnsName;
        }

        if (DistinguishedName.TryParse(dn, out var parsed) && DistinguishedName.TryParse(_snapshot.DomainDn, out var domain) && parsed.IsDescendantOf(domain))
        {
            return AddressResolver.ToDisplayPath(dn);
        }

        return _snapshot.Containers.FirstOrDefault(c => DistinguishedName.AreEqual(c.Dn, dn))?.Name
            ?? (parsed is { IsRoot: false } ? parsed.Name : dn);
    }

    /// <summary>The SIDs of a finding's principals that may need a name (its principal arguments, a restricted group).</summary>
    internal static IEnumerable<string> PrincipalSids(GpoFinding finding)
    {
        IEnumerable<string> candidates = finding.Code switch
        {
            GpoFindingCodes.DenyInSecurityFiltering or GpoFindingCodes.BroadEditRights or GpoFindingCodes.RestrictedAndPreferenceSameGroup
                or GpoFindingCodes.DenyLogonBroadGroup or GpoFindingCodes.DangerousRightBroadGroup => finding.Arguments.Take(1),
            GpoFindingCodes.BroadGroupInPrivilegedLocalGroup => finding.Arguments.Take(2),
            _ => [],
        };
        if (finding.SettingKey is { } key && GpoSettingKey.Parse(key) is { Area: GpoSettingArea.RestrictedGroup, Name: { } group })
        {
            candidates = candidates.Append(group);
        }

        return candidates.Select(c => c.Trim().TrimStart('*')).Where(c => c.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)).Select(c => c.ToUpperInvariant());
    }

    // ---- Message arguments ----------------------------------------------------------------------------------------------

    /// <summary>The finding's arguments made readable, as many as the code's message takes.</summary>
    private object?[] Arguments(GpoFinding finding)
    {
        var args = finding.Arguments;
        string Arg(int index) => index < args.Count ? args[index] : string.Empty;
        IReadOnlyList<string> readable = finding.Code switch
        {
            GpoFindingCodes.PartDisabledWithSettings => [GpoFindingText.Part(_localizer, Arg(0)), Arg(1)],
            GpoFindingCodes.PartEnabledWithoutSettings => [GpoFindingText.Part(_localizer, Arg(0))],
            GpoFindingCodes.LoopbackWithoutComputers => [Arg(0) switch
            {
                "Merge" => _localizer[L.GroupPolicyModel.LoopbackMerge],
                "Replace" => _localizer[L.GroupPolicyModel.LoopbackReplace],
                var other => other,
            }],
            GpoFindingCodes.DenyInSecurityFiltering or GpoFindingCodes.BroadEditRights or GpoFindingCodes.RestrictedAndPreferenceSameGroup
                or GpoFindingCodes.DenyLogonBroadGroup or GpoFindingCodes.DangerousRightBroadGroup => [NameOf(Arg(0))],
            GpoFindingCodes.BroadGroupInPrivilegedLocalGroup => [NameOf(Arg(0)), NameOf(Arg(1))],
            GpoFindingCodes.SysvolFolderMissing => [Arg(0) switch
            {
                "Folder" => _localizer[L.GpoDiagnostics.MissingFolder],
                "GptIni" => _localizer[L.GpoDiagnostics.MissingGptIni],
                var other => other,
            }],
            GpoFindingCodes.ConflictingSetting => [Arg(0), Arg(1), ValueOrItem(Arg(2)), ValueOrItem(Arg(3))],
            GpoFindingCodes.RedundantSetting => [Arg(0), Arg(1), ValueOrItem(Arg(2))],
            GpoFindingCodes.LmCompatibilityWeak => [Choice("LmCompatibilityLevel", Arg(0))],
            GpoFindingCodes.LdapSigningNotRequired => [Choice("LDAPServerIntegrity", Arg(0))],
            GpoFindingCodes.AnonymousAccessAllowed => [OptionName(Arg(0))],
            GpoFindingCodes.WeakDomainPasswordPolicy => [OptionName(Arg(0)), Arg(1)],
            _ => args,
        };

        var count = GpoFindingText.ArgumentCounts.TryGetValue(finding.Code, out var expected) ? expected : readable.Count;
        return [.. Enumerable.Range(0, count).Select(i => (object?)(i < readable.Count ? readable[i] : string.Empty))];
    }

    private string ValueOrItem(string value) => value.Length == 0 ? _localizer[L.GpoDiagnostics.PreferenceItem] : value;

    /// <summary>The K4 name of an option (<c>SecOption.&lt;id&gt;</c>), else the id.</summary>
    private string OptionName(string optionId)
    {
        var key = SecuritySettingsText.OptionKey(optionId);
        return _localizer.Contains(key) ? _localizer[key] : optionId;
    }

    /// <summary>The K4 label of an option's value with the value ("Wysyłaj odpowiedzi LM i NTLM (0)"), else the value.</summary>
    private string Choice(string optionId, string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return value;
        }

        var key = SecuritySettingsText.OptionChoiceKey(optionId, number);
        return _localizer.Contains(key) ? _localizer[key] + " (" + value + ")" : value;
    }

    // ---- Where ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The setting column's text: the name K4 gives the setting where it has one, else the key itself. The one place that
    /// describes a setting key (to be redirected to the GPO setting presenter, design note 20.1).
    /// </summary>
    private string DescribeSetting(string key)
    {
        var info = GpoSettingKey.Parse(key);
        string? Known(string resource) => _localizer.Contains(resource) ? _localizer[resource] : null;
        return info.Area switch
        {
            GpoSettingArea.UserRight when info.Name is { } right => Known(SecuritySettingsText.RightKey(right)) ?? key,
            GpoSettingArea.SystemAccess or GpoSettingArea.KerberosPolicy or GpoSettingArea.SecurityRegistryValue when info.Option is { } option =>
                Known(SecuritySettingsText.OptionKey(option.Id)) ?? key,
            GpoSettingArea.EventAudit when info.Name is { } category => Known(SecuritySettingsText.EventAuditKey(category)) ?? key,
            GpoSettingArea.AdvancedAudit when info.Subcategory is { } subcategory => Known(SecuritySettingsText.AuditSubcategoryKey(subcategory.Id)) ?? key,
            GpoSettingArea.RestrictedGroup when info.Name is { } group => _localizer.Format(L.GpoDiagnostics.SettingRestrictedGroup, NameOf(group)),
            GpoSettingArea.Preference when string.Equals(info.PreferenceType, "Groups", StringComparison.OrdinalIgnoreCase) =>
                _localizer[L.GpoDiagnostics.SettingLocalGroups],
            GpoSettingArea.PolicyRegistry when info.RegistryKey is { } registryKey =>
                info.ValueName is { Length: > 0 } value ? registryKey + "\\" + value : registryKey,
            GpoSettingArea.UnreadableFile when info.Name is { } path => _localizer.Format(L.GpoDiagnostics.SettingFile, path),
            _ => key,
        };
    }

    /// <summary>The WMI filter of a WMI filter finding (name, else id), null for other findings.</summary>
    private string? WmiFilterText(GpoFinding finding) => finding.Code switch
    {
        GpoFindingCodes.WmiFilterUnused when finding.Arguments.Count > 0 => finding.Arguments[0],
        GpoFindingCodes.WmiFilterMissing when finding.Arguments.Count > 0 => finding.Arguments[0],
        _ => null,
    };

    /// <summary>The domain head or OU of "Pokaż w modelu": the finding's container, else the GPO's first enabled link there.</summary>
    private string? ModelContainer(GpoFinding finding)
    {
        if (finding.ContainerDn is { } dn && IsDomainContainer(dn))
        {
            return dn;
        }

        if (finding.GpoId is not { } id || !_links.TryGetValue(id, out var links))
        {
            return null;
        }

        var candidates = links.Where(l => l.Kind != GroupPolicySomKind.Site).ToList();
        return (candidates.FirstOrDefault(l => l.Enabled) ?? candidates.FirstOrDefault())?.ContainerDn;
    }

    private bool IsDomainContainer(string dn) =>
        DistinguishedName.AreEqual(dn, _snapshot.DomainDn)
        || (DistinguishedName.TryParse(dn, out var parsed) && DistinguishedName.TryParse(_snapshot.DomainDn, out var domain) && parsed.IsDescendantOf(domain));

    /// <summary>The localized names of the built-in local groups and well-known principals (K4) by SID.</summary>
    private static Dictionary<string, string> LocalPrincipals(ILocalizer localizer)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in SecuritySettingsText.Choices(localizer))
        {
            map.TryAdd(choice.Sid, choice.DisplayName);
        }

        return map;
    }
}
