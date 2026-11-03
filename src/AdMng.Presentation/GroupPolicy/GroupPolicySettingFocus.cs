using AdMng.Core.GroupPolicy.AdministrativeTemplates;

namespace AdMng.Presentation.GroupPolicy;

/// <summary>
/// "Edytuj to ustawienie" (K6, docs/dev/k-computers-and-gpo.md 20.3): the setting an editor opens at. Either a key of the
/// normalized GPO settings (<see cref="Core.GroupPolicy.Content.GpoSetting.Key"/>, e.g.
/// <c>Computer/Security/Privilege Rights/SeDebugPrivilege</c>, <c>Computer/Audit/{0cce9215-…}</c>,
/// <c>Computer/Preferences/Groups/{uid}/…</c> or <c>Computer/Registry/HKLM\Software\Policies\…!NoAutoUpdate</c>) or an ADMX
/// policy (<see cref="PolicyId"/>, <c>namespace:name</c>, with the half it is set in). Keys only: no directory value.
/// </summary>
/// <param name="SettingKey">A setting key of the GPO content parsers (null with <paramref name="PolicyId"/>).</param>
/// <param name="PolicyId">An ADMX policy (<c>Contoso.Policies.Aktualizacje:KonfiguracjaAktualizacji</c>).</param>
/// <param name="PolicyClass">The half of <paramref name="PolicyId"/> (null: computer for a policy of both halves).</param>
public sealed record GroupPolicySettingFocus(string? SettingKey = null, string? PolicyId = null, PolicyClass? PolicyClass = null)
{
    private const string ComputerRegistry = "Computer/Registry/";
    private const string UserRegistry = "User/Registry/";
    private const string ComputerSecurity = "Computer/Security/";
    private const string ComputerAudit = "Computer/Audit/";
    private const string ComputerGroups = "Computer/Preferences/Groups/";

    /// <summary>The sections of the security template the security settings editor (K4) shows and edits.</summary>
    private static readonly HashSet<string> SecuritySections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Privilege Rights", "System Access", "Kerberos Policy", "Registry Values", "Event Audit", "Group Membership", "Services",
    };

    /// <summary>Properties only a local user item of Groups.xml has (K4 edits the local groups, not the users).</summary>
    private static readonly HashSet<string> LocalUserProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "userName", "fullName", "cpassword", "changeLogon", "noChange", "neverExpires", "acctDisabled", "subAuthority", "expires",
    };

    /// <summary>The setting of a GPO content key.</summary>
    public static GroupPolicySettingFocus ForSetting(string settingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingKey);
        return new GroupPolicySettingFocus(SettingKey: settingKey);
    }

    /// <summary>An ADMX policy in one half of the GPO.</summary>
    public static GroupPolicySettingFocus ForPolicy(PolicyClass scope, string policyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyId);
        return new GroupPolicySettingFocus(PolicyId: policyId, PolicyClass: scope);
    }

    /// <summary>
    /// The editor that edits the setting: the administrative templates (K3) for an ADMX policy and a Registry.pol value of
    /// either half; the security settings (K4) for the template sections it shows (<c>Computer/Security/&lt;section&gt;/*</c>:
    /// rights, account policies, Kerberos, security options, legacy audit, Restricted Groups, services), the advanced audit
    /// policy (<c>Computer/Audit/*</c>) and the computer's GPP local groups (<c>Computer/Preferences/Groups/*</c>, not a
    /// property only a local user has); null for anything else (registry keys and file security, event logs, other template
    /// sections, scripts, other preferences, user GPP groups: no editor of AD Manager writes them).
    /// </summary>
    public GroupPolicyDocumentKind? Editor =>
        PolicyId is { Length: > 0 } ? GroupPolicyDocumentKind.AdministrativeTemplates
        : SettingKey is not { Length: > 0 } key ? null
        : Starts(key, ComputerRegistry) || Starts(key, UserRegistry) ? GroupPolicyDocumentKind.AdministrativeTemplates
        : IsSecuritySection(key) || Starts(key, ComputerAudit) || IsLocalGroupItem(key) ? GroupPolicyDocumentKind.SecuritySettings
        : null;

    /// <summary>
    /// A Registry.pol value of the key (<c>Computer/Registry/HKLM\Software\…!Name</c>): the half, the key without its hive and
    /// the value name (null: the key itself, a deletion of all its values, or names that are hidden). False for other keys.
    /// </summary>
    public bool TryGetRegistryValue(out PolicyClass scope, out string registryKey, out string? valueName)
    {
        scope = Core.GroupPolicy.AdministrativeTemplates.PolicyClass.Machine;
        registryKey = string.Empty;
        valueName = null;
        string rest;
        if (SettingKey is { } key && Starts(key, ComputerRegistry))
        {
            rest = key[ComputerRegistry.Length..];
        }
        else if (SettingKey is { } userKey && Starts(userKey, UserRegistry))
        {
            scope = Core.GroupPolicy.AdministrativeTemplates.PolicyClass.User;
            rest = userKey[UserRegistry.Length..];
        }
        else
        {
            return false;
        }

        var bang = rest.LastIndexOf('!');
        var path = bang < 0 ? rest : rest[..bang];
        var name = bang < 0 ? null : rest[(bang + 1)..];
        var slash = path.IndexOf('\\', StringComparison.Ordinal);
        registryKey = slash < 0 ? string.Empty : path[(slash + 1)..];
        valueName = name switch
        {
            null or "*" => null,
            "(Default)" => string.Empty,
            _ when name.StartsWith("**", StringComparison.Ordinal) => null,
            _ => name,
        };
        return registryKey.Length > 0;
    }

    /// <summary>The part of a K4 key after its prefix (<c>Computer/Security/</c>, <c>Computer/Audit/</c>, <c>Computer/Preferences/Groups/</c>).</summary>
    internal (string Area, string Path)? SecurityPart()
    {
        if (SettingKey is not { } key)
        {
            return null;
        }

        foreach (var prefix in new[] { ComputerSecurity, ComputerAudit, ComputerGroups })
        {
            if (Starts(key, prefix))
            {
                return (prefix, key[prefix.Length..]);
            }
        }

        return null;
    }

    internal static bool IsSecurityArea(string area) => area == ComputerSecurity;

    internal static bool IsAuditArea(string area) => area == ComputerAudit;

    private static bool Starts(string key, string prefix) => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>Computer/Security/&lt;section&gt;/…</c> of a section K4 edits.</summary>
    private static bool IsSecuritySection(string key)
    {
        if (!Starts(key, ComputerSecurity))
        {
            return false;
        }

        var rest = key[ComputerSecurity.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && SecuritySections.Contains(rest[..slash]);
    }

    /// <summary>
    /// <c>Computer/Preferences/Groups/&lt;uid&gt;…</c> that may be a local group: the key alone does not tell a group from a
    /// local user (the presenter passes no focus for a user item), only a property no group has does.
    /// </summary>
    private static bool IsLocalGroupItem(string key) =>
        Starts(key, ComputerGroups)
        && !(Core.GroupPolicy.Content.GpoSettingKey.Parse(key) is { Property: { } property } && LocalUserProperties.Contains(property));
}

/// <summary>An editor that "Edytuj to ustawienie" can point at a setting (K3, K4).</summary>
public interface IGroupPolicySettingEditor
{
    /// <summary>The GPO the editor edits.</summary>
    Guid GpoId { get; }

    /// <summary>
    /// Selects the setting (page, category, row) – at once when the editor has loaded, else when its load ends. False when
    /// the loaded editor does not know the setting (the operator is told).
    /// </summary>
    bool FocusSetting(GroupPolicySettingFocus focus);
}
