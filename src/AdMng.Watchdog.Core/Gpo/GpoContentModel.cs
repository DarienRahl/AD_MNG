namespace AdMng.Watchdog.Core.Gpo;

/// <summary>How a setting value is kept (stage B design 8.5).</summary>
public enum GpoSettingKind
{
    /// <summary>Canonical text (≤ 200 characters; list elements ≤ 200 each).</summary>
    Text = 0,
    /// <summary><c>sha256:&lt;hex&gt;</c> of a longer value, or the content hash of a script file.</summary>
    Hash = 1,
    /// <summary>Secret (GPP <c>cpassword</c>, password-like registry values, <see cref="GpoSecretSettings"/>): never
    /// stored, not even hashed – only its presence; <see cref="GpoSetting.Value"/> is empty.</summary>
    Masked = 2,
    /// <summary>Marker of a content file that could not be read or parsed (key <c>File/&lt;path&gt;</c>, empty value): shown as
    /// „nie udało się odczytać” (<c>Value.Unreadable</c>); the file's previous settings are carried over.</summary>
    Unreadable = 3,
}

/// <summary>Setting categories; <see cref="GpoSettingCategories.IsSensitive"/> drives the severity of 8.6. Stable numbers
/// (stored).</summary>
public enum GpoSettingCategory
{
    Other = 0,
    /// <summary><c>[System Access]</c>, <c>[Kerberos Policy]</c>: account, lockout and Kerberos policy.</summary>
    AccountPolicy = 1,
    /// <summary><c>[Event Audit]</c>, <c>audit.csv</c>.</summary>
    AuditPolicy = 2,
    PrivilegeRights = 3,
    /// <summary><c>[Group Membership]</c> (Restricted Groups).</summary>
    GroupMembership = 4,
    /// <summary>Services of the security template and of GPP.</summary>
    Services = 5,
    /// <summary><c>[Registry Keys]</c>, <c>[File Security]</c> (SDDL).</summary>
    FileRegistrySecurity = 6,
    /// <summary>Registry values outside the sensitive paths.</summary>
    Registry = 7,
    /// <summary>Registry values under the sensitive paths of 8.6 (LSA, Winlogon, Run keys, …).</summary>
    SecurityRegistry = 8,
    /// <summary>scripts.ini, psscripts.ini and script files.</summary>
    Scripts = 9,
    /// <summary>GPP Groups.xml (local groups and local users).</summary>
    PreferenceGroups = 10,
    /// <summary>GPP ScheduledTasks.xml (incl. immediate tasks).</summary>
    PreferenceScheduledTasks = 11,
    /// <summary>GPP Files/Folders/Shortcuts targeting the system folder or startup folders.</summary>
    PreferenceSystemFiles = 12,
    /// <summary>Other preferences.</summary>
    Preferences = 13,
    /// <summary><c>[System Log]</c>, <c>[Security Log]</c>, <c>[Application Log]</c>.</summary>
    EventLog = 14,
    /// <summary>Policy comments (comment.cmtx, GPO.cmt).</summary>
    Comments = 15,
}

/// <summary>One setting of a GPO (stage B design 8.5).</summary>
public sealed record GpoSetting
{
    /// <summary>Separator of the elements of a list value (canonical values never contain a line feed).</summary>
    public const char ListSeparator = '\n';

    /// <summary>Stable, English, scope-prefixed key, e.g. <c>Computer/Security/Privilege Rights/SeDebugPrivilege</c>.
    /// Compared case-insensitively.</summary>
    public required string Key { get; init; }

    /// <summary>Canonical value; for <see cref="IsList"/> the sorted elements joined by <see cref="ListSeparator"/>; empty
    /// for <see cref="GpoSettingKind.Masked"/> and <see cref="GpoSettingKind.Unreadable"/>.</summary>
    public string Value { get; init; } = string.Empty;

    public GpoSettingKind Kind { get; init; }

    public GpoSettingCategory Category { get; init; }

    /// <summary>The value is an unordered list (SIDs of a user right, members of a group): the differ reports the
    /// elements added and removed.</summary>
    public bool IsList { get; init; }

    /// <summary>The file (relative path) the setting came from – lets a later read carry the setting over while that
    /// file is unreadable.</summary>
    public string? Source { get; init; }

    /// <summary>The elements of a list value (the value itself otherwise).</summary>
    public IReadOnlyList<string> Elements() =>
        IsList ? (Value.Length == 0 ? [] : Value.Split(ListSeparator)) : [Value];
}

/// <summary>One entry of the stored inventory (every file and folder of the GPO folder).</summary>
public sealed record GpoInventoryEntry
{
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
}

/// <summary>Result of a read (column <c>state</c> of 8.8).</summary>
public enum GpoContentState
{
    /// <summary>Every content file was read and parsed.</summary>
    Complete = 0,
    /// <summary>A limit was exceeded (<c>gpo.&lt;guid&gt;.tooLarge</c>) or a file could not be parsed
    /// (<c>gpo.&lt;guid&gt;.parseError</c>); unread files keep their previous settings.</summary>
    Partial = 1,
}

/// <summary>The latest content of one GPO (stage B design 8.5, stored by the service – ADR-0008/0009).</summary>
public sealed record GpoContentSnapshot
{
    public required Guid GpoGuid { get; init; }

    /// <summary>DC whose SYSVOL was read.</summary>
    public required string Dc { get; init; }

    /// <summary><c>versionNumber</c> of the GPC when the read was triggered.</summary>
    public int? GpcVersion { get; init; }

    /// <summary><c>Version</c> of <c>GPT.INI</c> as read (null when missing or unreadable).</summary>
    public int? GptVersion { get; init; }

    public required DateTimeOffset ReadAt { get; init; }

    public GpoContentState State { get; init; }

    public IReadOnlyList<GpoInventoryEntry> Inventory { get; init; } = [];

    /// <summary>Settings sorted by key (ordinal, case-insensitive), keys unique.</summary>
    public IReadOnlyList<GpoSetting> Settings { get; init; } = [];
}

/// <summary>
/// GPC <c>versionNumber</c> / <c>GPT.INI</c> <c>Version</c>: computer (machine) version in the low 16 bits, user version in
/// the high 16 bits ([MS-GPOL] 2.2.4).
/// </summary>
public readonly record struct GpoVersion(int Raw)
{
    public int Computer => Raw & 0xFFFF;

    public int User => (Raw >> 16) & 0xFFFF;

    /// <summary>True when either half of this version is behind <paramref name="other"/> (SYSVOL lag, 8.7).</summary>
    public bool IsBehind(GpoVersion other) => Computer < other.Computer || User < other.User;
}
