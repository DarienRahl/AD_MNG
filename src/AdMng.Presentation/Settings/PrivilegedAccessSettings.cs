using AdMng.Core.PrivilegedAccess;

namespace AdMng.Presentation.Settings;

/// <summary>The operator's level of a catalogue group (<see cref="PrivilegedGroupDefinition.Id"/>).</summary>
public sealed record PrivilegedGroupLevelSetting(string GroupId, PrivilegeLevel Level);

/// <summary>
/// A group or account marked as privileged: its SID and/or portable DN (<c>{domain}</c>), the level and a note - nothing
/// else of the directory (CLAUDE.md rule 1).
/// </summary>
public sealed record PrivilegedMarkSetting
{
    public string? Sid { get; init; }

    /// <summary>The DN in the portable form (<see cref="Views.SavedView.ToPortableDn"/>).</summary>
    public string? Dn { get; init; }

    public PrivilegeLevel Level { get; init; } = PrivilegeLevel.High;

    public string? Note { get; init; }
}

/// <summary>A saved filter used as "mark as privileged with level X" for accounts or groups.</summary>
public sealed record PrivilegedRuleSetting
{
    /// <summary>Id of the <see cref="Filters.SavedFilter"/>.</summary>
    public string FilterId { get; init; } = string.Empty;

    public PrivilegeRuleTarget Target { get; init; } = PrivilegeRuleTarget.Accounts;

    public PrivilegeLevel Level { get; init; } = PrivilegeLevel.Elevated;

    public string? Note { get; init; }
}

/// <summary>
/// Settings of the "Uprawnienia" tab of the account review (README U4): level overrides of the built-in groups, marked
/// groups/accounts, filter rules, the peer comparison and which account types are reviewed. Never results.
/// </summary>
public sealed class PrivilegedAccessSettings
{
    public const int MaxMarks = 500;
    public const int MaxRules = 100;

    public List<PrivilegedGroupLevelSetting> LevelOverrides { get; set; } = [];

    public List<PrivilegedMarkSetting> Marks { get; set; } = [];

    public List<PrivilegedRuleSetting> Rules { get; set; } = [];

    public PeerOptions Peers { get; set; } = new();

    public bool IncludeComputers { get; set; } = true;

    public bool IncludeServiceAccounts { get; set; } = true;

    /// <summary>U4b: the ACL delegations also read every user, group and computer (slower; off by default).</summary>
    public bool DeepAclScan { get; set; }

    /// <summary>The level overrides as the Core options take them.</summary>
    public IReadOnlyDictionary<string, PrivilegeLevel> OverrideMap() =>
        LevelOverrides.ToDictionary(o => o.GroupId, o => o.Level, StringComparer.OrdinalIgnoreCase);

    /// <summary>Repairs values of a hand-edited or older settings file.</summary>
    public void Normalize()
    {
        LevelOverrides = [.. (LevelOverrides ?? [])
            .Where(o => o is not null && PrivilegedGroupCatalog.Find(o.GroupId) is not null)
            .Select(o => o with { Level = PrivilegeLevels.Normalize(o.Level) })
            .DistinctBy(o => o.GroupId, StringComparer.OrdinalIgnoreCase)];
        Marks = [.. (Marks ?? [])
            .Where(m => m is not null && (!string.IsNullOrWhiteSpace(m.Sid) || !string.IsNullOrWhiteSpace(m.Dn)))
            .Select(m => m with { Level = PrivilegeLevels.Normalize(m.Level) })
            .Take(MaxMarks)];
        Rules = [.. (Rules ?? [])
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.FilterId))
            .Select(r => r with { Level = PrivilegeLevels.Normalize(r.Level), Target = Enum.IsDefined(r.Target) ? r.Target : PrivilegeRuleTarget.Accounts })
            .Take(MaxRules)];
        Peers = (Peers ?? new()).Normalized();
    }
}
