namespace AdMng.Core.GroupPolicy.Content;

/// <summary>Registry names shared by the security template and Registry.pol parsers.</summary>
public static class RegistryFormat
{
    public const uint RegNone = 0, RegSz = 1, RegExpandSz = 2, RegBinary = 3, RegDword = 4, RegDwordBigEndian = 5,
        RegLink = 6, RegMultiSz = 7, RegQword = 11;

    /// <summary><c>REG_DWORD</c> etc.; unknown types as <c>REG_TYPE_&lt;n&gt;</c>.</summary>
    public static string TypeName(uint type) => type switch
    {
        RegNone => "REG_NONE",
        RegSz => "REG_SZ",
        RegExpandSz => "REG_EXPAND_SZ",
        RegBinary => "REG_BINARY",
        RegDword => "REG_DWORD",
        RegDwordBigEndian => "REG_DWORD_BIG_ENDIAN",
        RegLink => "REG_LINK",
        RegMultiSz => "REG_MULTI_SZ",
        RegQword => "REG_QWORD",
        _ => "REG_TYPE_" + GpoValues.Number(type),
    };

    /// <summary>
    /// A security-template registry path (<c>MACHINE\System\…</c>, [MS-GPSB] 2.2.5) with the usual hive abbreviation
    /// (<c>HKLM\System\…</c>). Unknown roots stay as written.
    /// </summary>
    public static string FromSecEditPath(string path)
    {
        var separator = path.IndexOf('\\', StringComparison.Ordinal);
        var root = separator < 0 ? path : path[..separator];
        var rest = separator < 0 ? string.Empty : path[separator..];
        var hive = root.ToUpperInvariant() switch
        {
            "MACHINE" => "HKLM",
            "USER" => "HKU",
            "CLASSES_ROOT" => "HKCR",
            "CURRENT_USER" => "HKCU",
            _ => root,
        };
        return hive + rest;
    }

    /// <summary>GPP hive names (<c>HKEY_LOCAL_MACHINE</c>) abbreviated.</summary>
    public static string FromHiveName(string? hive) => (hive ?? string.Empty).ToUpperInvariant() switch
    {
        "HKEY_LOCAL_MACHINE" => "HKLM",
        "HKEY_CURRENT_USER" => "HKCU",
        "HKEY_USERS" => "HKU",
        "HKEY_CLASSES_ROOT" => "HKCR",
        "HKEY_CURRENT_CONFIG" => "HKCC",
        var other => other,
    };
}

/// <summary>Names of the audit setting values shared by <c>[Event Audit]</c> and <c>audit.csv</c> (0–3).</summary>
public static class AuditValues
{
    public static string Name(string raw) => raw.Trim() switch
    {
        "0" => "No Auditing",
        "1" => "Success",
        "2" => "Failure",
        "3" => "Success and Failure",
        var other => other,
    };
}
