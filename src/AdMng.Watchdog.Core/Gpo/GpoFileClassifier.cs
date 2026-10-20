namespace AdMng.Watchdog.Core.Gpo;

/// <summary>What the service does with a file of the GPO folder (stage B design 8.4).</summary>
public enum GpoFileRole
{
    /// <summary>Inventory only (path, size, modification time).</summary>
    Inventory = 0,
    GptIni = 1,
    SecurityTemplate = 2,
    AuditCsv = 3,
    RegistryPol = 4,
    Preferences = 5,
    ScriptsIni = 6,
    PsScriptsIni = 7,
    /// <summary>Other files below <c>Scripts/</c>: content hash only (≤ 1 MiB).</summary>
    ScriptFile = 8,
    /// <summary><c>Machine|User/comment.cmtx</c>.</summary>
    Comment = 9,
    /// <summary><c>GPO.cmt</c>.</summary>
    GpoComment = 10,
}

/// <summary>A classified file of the GPO folder.</summary>
public sealed record GpoFile(string Path, GpoFileRole Role, GpoScope Scope, string? PreferenceType = null)
{
    /// <summary>Order in which content files are read within the size budget (the security-relevant files first).</summary>
    public int Priority => Role switch
    {
        GpoFileRole.GptIni => 0,
        GpoFileRole.SecurityTemplate => 1,
        GpoFileRole.AuditCsv => 2,
        GpoFileRole.RegistryPol => 3,
        GpoFileRole.ScriptsIni or GpoFileRole.PsScriptsIni => 4,
        GpoFileRole.Preferences => 5,
        GpoFileRole.ScriptFile => 6,
        _ => 7,
    };

    /// <summary>Category of the file's settings as a whole – used for the "could not be read" marker. Unreadable content
    /// counts as sensitive wherever the file can hold sensitive settings (fail closed).</summary>
    public GpoSettingCategory Category => Role switch
    {
        GpoFileRole.SecurityTemplate => GpoSettingCategory.PrivilegeRights,
        GpoFileRole.AuditCsv => GpoSettingCategory.AuditPolicy,
        GpoFileRole.RegistryPol => GpoSettingCategory.SecurityRegistry,
        GpoFileRole.ScriptsIni or GpoFileRole.PsScriptsIni or GpoFileRole.ScriptFile => GpoSettingCategory.Scripts,
        GpoFileRole.Comment or GpoFileRole.GpoComment => GpoSettingCategory.Comments,
        GpoFileRole.Preferences => (PreferenceType ?? string.Empty).ToUpperInvariant() switch
        {
            "GROUPS" => GpoSettingCategory.PreferenceGroups,
            "SCHEDULEDTASKS" => GpoSettingCategory.PreferenceScheduledTasks,
            "SERVICES" => GpoSettingCategory.Services,
            "REGISTRY" => GpoSettingCategory.SecurityRegistry,
            "FILES" or "FOLDERS" or "SHORTCUTS" => GpoSettingCategory.PreferenceSystemFiles,
            _ => GpoSettingCategory.Preferences,
        },
        _ => GpoSettingCategory.Other,
    };

    /// <summary>True when the file's content is read (everything but <see cref="GpoFileRole.Inventory"/>).</summary>
    public bool IsContent => Role != GpoFileRole.Inventory;
}

/// <summary>Maps a normalized relative path (<see cref="GpoRelativePath"/>) to its role – case-insensitive like SYSVOL.</summary>
public static class GpoFileClassifier
{
    public static GpoFile Classify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var s = path.Split('/');
        if (s.Length == 1)
        {
            return Is(s[0], "GPT.INI") ? new GpoFile(path, GpoFileRole.GptIni, GpoScope.Gpo)
                : Is(s[0], "GPO.cmt") ? new GpoFile(path, GpoFileRole.GpoComment, GpoScope.Gpo)
                : new GpoFile(path, GpoFileRole.Inventory, GpoScope.Gpo);
        }

        var scope = Is(s[0], "Machine") ? GpoScope.Computer : Is(s[0], "User") ? GpoScope.User : GpoScope.Gpo;
        if (scope == GpoScope.Gpo)
        {
            return new GpoFile(path, GpoFileRole.Inventory, GpoScope.Gpo);
        }

        var role = s switch
        {
            [_, var a, var b, var c, var d] when scope == GpoScope.Computer && Is(a, "Microsoft") && Is(b, "Windows NT") && Is(c, "SecEdit")
                && Is(d, "GptTmpl.inf") => GpoFileRole.SecurityTemplate,
            [_, var a, var b, var c, var d] when scope == GpoScope.Computer && Is(a, "Microsoft") && Is(b, "Windows NT") && Is(c, "Audit")
                && Is(d, "audit.csv") => GpoFileRole.AuditCsv,
            [_, var a] when Is(a, "Registry.pol") => GpoFileRole.RegistryPol,
            [_, var a] when Is(a, "comment.cmtx") => GpoFileRole.Comment,
            [_, var a, var b] when Is(a, "Scripts") && Is(b, "scripts.ini") => GpoFileRole.ScriptsIni,
            [_, var a, var b] when Is(a, "Scripts") && Is(b, "psscripts.ini") => GpoFileRole.PsScriptsIni,
            [_, var a, ..] when Is(a, "Scripts") => GpoFileRole.ScriptFile,
            [_, var a, .., var last] when Is(a, "Preferences") && last.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) => GpoFileRole.Preferences,
            _ => GpoFileRole.Inventory,
        };
        string? type = role == GpoFileRole.Preferences ? (s.Length >= 4 ? s[2] : Path.GetFileNameWithoutExtension(s[^1])) : null;
        return new GpoFile(path, role, scope, type);
    }

    private static bool Is(string segment, string name) => segment.Equals(name, StringComparison.OrdinalIgnoreCase);
}
