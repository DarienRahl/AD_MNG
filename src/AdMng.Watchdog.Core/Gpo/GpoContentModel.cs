using AdMng.Core.GroupPolicy.Content;

namespace AdMng.Watchdog.Core.Gpo;

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
