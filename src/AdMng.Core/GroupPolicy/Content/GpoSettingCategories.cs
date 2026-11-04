namespace AdMng.Core.GroupPolicy.Content;

/// <summary>
/// The sensitive categories of stage B design 8.6: privilege rights; group membership / restricted groups; GPP groups
/// and local users; scripts and script files; GPP scheduled tasks; services; audit policy; Kerberos and account policy;
/// registry under the listed paths; file and registry security; GPP files/folders/shortcuts targeting the system folder
/// or startup folders.
/// </summary>
public static class GpoSettingCategories
{
    /// <summary>Registry key paths (consecutive key names, case-insensitive) whose values are sensitive. A trailing '*'
    /// matches a key name prefix (<c>Run</c>, <c>RunOnce</c>, <c>RunOnceEx</c>, …).</summary>
    private static readonly string[][] SensitiveRegistryPaths =
    [
        ["Control", "Lsa"],
        ["Winlogon"],
        ["CurrentVersion", "Run*"],
        ["Policies", "System"],
        ["SecurityProviders", "WDigest"],
        ["Services", "Netlogon", "Parameters"],
        ["Services", "LanmanServer", "Parameters"],
        ["Services", "LanmanWorkstation", "Parameters"],
        ["Windows Defender", "Exclusions"],
        ["WindowsFirewall"],
    ];

    /// <summary>Markers of the system folder and the startup folders in GPP paths (GPP variables and literal paths).</summary>
    private static readonly string[] SystemOrStartupMarkers =
    [
        "%SystemRoot%", "%WinDir%", "%WindowsDir%", "%SystemDir%", "%SystemDrive%\\Windows", ":\\Windows",
        "%StartUpDir%", "%CommonStartUpDir%", "\\Start Menu\\Programs\\Startup", "\\Programs\\StartUp",
    ];

    public static bool IsSensitive(GpoSettingCategory category) => category is
        GpoSettingCategory.AccountPolicy or GpoSettingCategory.AuditPolicy or GpoSettingCategory.PrivilegeRights
        or GpoSettingCategory.GroupMembership or GpoSettingCategory.Services or GpoSettingCategory.FileRegistrySecurity
        or GpoSettingCategory.SecurityRegistry or GpoSettingCategory.Scripts or GpoSettingCategory.PreferenceGroups
        or GpoSettingCategory.PreferenceScheduledTasks or GpoSettingCategory.PreferenceSystemFiles;

    /// <summary><see cref="GpoSettingCategory.SecurityRegistry"/> for a key under a sensitive path, otherwise
    /// <see cref="GpoSettingCategory.Registry"/>. The hive prefix (<c>HKLM\</c>, <c>MACHINE\</c> …) does not matter.</summary>
    public static GpoSettingCategory ForRegistryKey(string keyPath) =>
        IsSensitiveRegistryKey(keyPath) ? GpoSettingCategory.SecurityRegistry : GpoSettingCategory.Registry;

    public static bool IsSensitiveRegistryKey(string keyPath)
    {
        ArgumentNullException.ThrowIfNull(keyPath);
        var names = keyPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pattern in SensitiveRegistryPaths)
        {
            for (var start = 0; start + pattern.Length <= names.Length; start++)
            {
                if (Matches(names, start, pattern))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when a GPP path value points into the system folder or a startup folder.</summary>
    public static bool TargetsSystemOrStartupFolder(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SystemOrStartupMarkers.Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(string[] names, int start, string[] pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            var name = names[start + i];
            var expected = pattern[i];
            var ok = expected.EndsWith('*')
                ? name.StartsWith(expected[..^1], StringComparison.OrdinalIgnoreCase)
                : name.Equals(expected, StringComparison.OrdinalIgnoreCase);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Secret settings (stage B design 8.5): never stored, not even hashed – only their presence. Registry values whose name
/// contains <c>Password</c>, <c>Pwd</c> or <c>Secret</c> (case-insensitive), GPP attributes with such a name (above all
/// <c>cpassword</c>, MS14-025), and the registry values of <see cref="Registry"/>.
/// </summary>
public static class GpoSecretSettings
{
    /// <summary>GPP attribute holding the obfuscated password of MS14-025 (its presence sets tag <c>gppPassword</c>).</summary>
    public const string CPassword = "cpassword";

    /// <summary>(key path suffix, value name) of secret registry values whose name is not password-like; value name
    /// <c>*</c> = every value of the key (the value names themselves are secret then: SNMP community names).</summary>
    public static IReadOnlyList<(string KeySuffix, string ValueName)> Registry { get; } =
    [
        (@"Microsoft\Windows NT\CurrentVersion\Winlogon", "DefaultPassword"),
        (@"Microsoft\Windows NT\CurrentVersion\Winlogon", "AltDefaultPassword"),
        (@"Policies\SNMP\Parameters\ValidCommunities", "*"),
        (@"Services\SNMP\Parameters\ValidCommunities", "*"),
    ];

    /// <summary>True for a password-like name: contains <c>Password</c>, <c>Pwd</c> or <c>Secret</c>.</summary>
    public static bool IsSecretName(string? name) =>
        name is not null
        && (name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("pwd", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the registry value is secret (by its name or by <see cref="Registry"/>).</summary>
    public static bool IsSecretRegistryValue(string keyPath, string? valueName) =>
        IsSecretName(valueName) || HidesValueNames(keyPath)
        || Registry.Any(r => r.ValueName != "*" && EndsWithKey(keyPath, r.KeySuffix)
                             && string.Equals(r.ValueName, valueName, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when every value of the key is secret including its name (the setting key then ends in <c>!*</c>).</summary>
    public static bool HidesValueNames(string keyPath) =>
        Registry.Any(r => r.ValueName == "*" && EndsWithKey(keyPath, r.KeySuffix));

    private static bool EndsWithKey(string keyPath, string suffix)
    {
        var path = keyPath.TrimEnd('\\');
        return path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
               && (path.Length == suffix.Length || path[path.Length - suffix.Length - 1] == '\\');
    }
}
