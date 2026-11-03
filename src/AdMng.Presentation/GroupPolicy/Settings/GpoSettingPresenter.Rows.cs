using System.Globalization;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Settings;

/// <summary>Scripts, comments, unreadable files and other keys; and the rows of a list view (<see cref="DescribeRows"/>).</summary>
public sealed partial class GpoSettingPresenter
{
    /// <summary>
    /// The rows of a view in the order the settings come: the settings of one administrative template policy collapse into
    /// one policy row (when <see cref="ComputerPolicies"/>/<see cref="UserPolicies"/> are known), the settings of one
    /// preference item into one item row, a service's or an object's start mode and permissions into one row, a script's
    /// command and parameters into one row; every other setting is a row of its own.
    /// </summary>
    public IReadOnlyList<GpoSettingText> DescribeRows(IEnumerable<GpoSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var order = new List<string>();
        var buckets = new Dictionary<string, List<(GpoSetting Setting, GpoSettingKeyInfo Info)>>(StringComparer.OrdinalIgnoreCase);
        var policies = new Dictionary<string, (ConfiguredPolicy Policy, PolicyClass Class)>(StringComparer.OrdinalIgnoreCase);
        var single = 0;
        foreach (var setting in settings)
        {
            var info = GpoSettingKey.Parse(setting.Key);
            string bucket;
            if (setting.Kind != GpoSettingKind.Unreadable && PolicyOf(info) is { } owner)
            {
                bucket = "P|" + owner.Class + "|" + owner.Policy.Definition.Id;
                policies[bucket] = owner;
            }
            else if (info is { Area: GpoSettingArea.Preference, ItemKey: { } item, Truncated: false })
            {
                bucket = "I|" + item;
            }
            else if (info is { Area: GpoSettingArea.Service or GpoSettingArea.RegistryKeySecurity or GpoSettingArea.FileSecurity, Name: { } name, Field: not null, Truncated: false })
            {
                bucket = "O|" + info.Area + "|" + name;
            }
            else if (info is { Area: GpoSettingArea.Script, Index: { } index, Truncated: false })
            {
                bucket = "S|" + info.Scope + "|" + info.PowerShell + "|" + info.ScriptPhase + "|" + index.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                bucket = "K|" + (single++).ToString(CultureInfo.InvariantCulture);
            }

            if (!buckets.TryGetValue(bucket, out var members))
            {
                members = [];
                buckets[bucket] = members;
                order.Add(bucket);
            }

            members.Add((setting, info));
        }

        var rows = new List<GpoSettingText>(order.Count);
        foreach (var bucket in order)
        {
            var members = buckets[bucket];
            rows.Add(bucket[0] switch
            {
                'P' => DescribePolicy(policies[bucket].Policy, policies[bucket].Class, [.. members.Select(m => m.Setting)]),
                'I' => DescribeItem(members),
                'O' when members.Count > 1 => MergeObject(members),
                'S' when members.Count > 1 => MergeScript(members),
                _ => Describe(members[0].Setting),
            });
        }

        return rows;
    }

    /// <summary>
    /// Rows in GPMC's order: the computer, the user, then the GPO itself; "Zasady" before "Preferencje"; GPMC's folders in
    /// its tree order ("Ustawienia systemu Windows" before "Szablony administracyjne", "Zasady haseł" before "Zasady blokady
    /// konta", the extra registry settings last), other folders and the names as the window's culture sorts.
    /// </summary>
    public static IReadOnlyList<GpoSettingText> Ordered(IEnumerable<GpoSettingText> rows, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(localizer);
        var folders = new FolderOrder(localizer);
        var names = StringComparer.Create(localizer.Culture, ignoreCase: true);
        return [.. rows
            .OrderBy(r => r.Scope switch { GpoScope.Computer => 0, GpoScope.User => 1, _ => 2 })
            .ThenBy(r => r.Branch switch { GpoSettingBranch.Policies => 0, GpoSettingBranch.Preferences => 1, _ => 2 })
            .ThenBy(r => r.Groups, folders)
            .ThenBy(r => r.Name, names)
            .ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Folder paths compared segment by segment: GPMC's known folders by their place in its tree, the rest by name.</summary>
    private sealed class FolderOrder : IComparer<IReadOnlyList<string>>
    {
        private readonly Dictionary<string, int> _rank;
        private readonly StringComparer _names;

        public FolderOrder(ILocalizer localizer)
        {
            _names = StringComparer.Create(localizer.Culture, ignoreCase: true);
            _rank = new Dictionary<string, int>(_names);
            void Rank(int rank, params string[] keys)
            {
                foreach (var key in keys)
                {
                    _rank.TryAdd(localizer[key], rank);
                }
            }

            // Policies: Windows settings, then administrative templates; Preferences: Windows, then Control Panel.
            Rank(0, L.GpoSettings.WindowsSettings, L.GpoSettings.ScriptsComputer, L.GpoSettings.ScriptsUser, L.GpoSettings.AccountPolicies,
                L.GpoSettings.AuditPolicy, L.GpoSettings.PhaseStartup, L.GpoSettings.PhaseLogon,
                SecuritySettings.SecuritySettingsText.OptionGroupKey(Core.GroupPolicy.Security.SecurityOptionGroup.PasswordPolicy));
            Rank(1, L.GpoSettings.ControlPanelSettings, L.GroupPolicyModel.AreaTemplates, L.GpoSettings.SecuritySettings, L.GpoSettings.LocalPolicies,
                L.GpoSettings.UserRightsAssignment, L.GpoSettings.PhaseShutdown, L.GpoSettings.PhaseLogoff,
                SecuritySettings.SecuritySettingsText.OptionGroupKey(Core.GroupPolicy.Security.SecurityOptionGroup.AccountLockout));
            Rank(2, L.GpoSettings.EventLog, L.GpoSettings.SecurityOptions, L.GpoSettings.ScriptFiles,
                SecuritySettings.SecuritySettingsText.OptionGroupKey(Core.GroupPolicy.Security.SecurityOptionGroup.Kerberos));
            Rank(3, L.GpoSettings.RestrictedGroups);
            Rank(4, L.GpoSettings.SystemServices);
            Rank(5, L.GpoSettings.Registry);
            Rank(6, L.GpoSettings.FileSystem);
            Rank(7, L.GpoSettings.AdvancedAudit);
            Rank(int.MaxValue, L.GpoSettings.ExtraRegistry, L.GpoSettings.Other, L.GpoSettings.UnreadableFiles);
        }

        public int Compare(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
        {
            x ??= [];
            y ??= [];
            for (var i = 0; i < Math.Min(x.Count, y.Count); i++)
            {
                var rank = Rank(x[i]).CompareTo(Rank(y[i]));
                if (rank != 0)
                {
                    return rank;
                }

                var name = _names.Compare(x[i], y[i]);
                if (name != 0)
                {
                    return name;
                }
            }

            return x.Count.CompareTo(y.Count);
        }

        /// <summary>A known folder's place; unknown folders come after the known ones of their level, by name.</summary>
        private int Rank(string segment) => _rank.TryGetValue(segment, out var rank) ? rank : 100;
    }

    /// <summary>A service, registry key or file: its start mode or inheritance and its permissions in one row.</summary>
    private GpoSettingText MergeObject(IReadOnlyList<(GpoSetting Setting, GpoSettingKeyInfo Info)> members)
    {
        var parts = members.Select(m => (m.Info, Text: Describe(m.Setting))).OrderBy(p => IsSecurityField(p.Info) ? 1 : 0).ToList();
        var elements = new List<string>();
        foreach (var (info, text) in parts)
        {
            var label = IsSecurityField(info) ? _l[L.GpoSettings.Permissions]
                : info.Area == GpoSettingArea.Service ? _l[L.GpoSettings.StartupMode]
                : _l[L.GpoSettings.Inheritance];
            elements.Add(Labeled(label, text.ValueText));
            elements.AddRange(text.Elements);
        }

        var first = parts[0].Text;
        return first with
        {
            Keys = [.. members.Select(m => m.Setting.Key)],
            Name = parts[0].Info.Name ?? first.Name,
            ValueText = string.Join("; ", parts.Select(p => p.Text.ValueText)),
            Elements = elements,
            Explanation = null,
        };
    }

    private static bool IsSecurityField(GpoSettingKeyInfo info) => string.Equals(info.Field, "Security", StringComparison.OrdinalIgnoreCase);

    /// <summary>A script's command line and parameters in one row.</summary>
    private GpoSettingText MergeScript(IReadOnlyList<(GpoSetting Setting, GpoSettingKeyInfo Info)> members)
    {
        var command = members.FirstOrDefault(m => m.Info.Field is null);
        var parameters = members.FirstOrDefault(m => m.Info.Field is not null);
        var commandText = command.Setting is null ? string.Empty : Describe(command.Setting).ValueText;
        var parametersText = parameters.Setting is null ? string.Empty : Describe(parameters.Setting).ValueText;
        var first = Describe((command.Setting ?? members[0].Setting)!);
        return first with
        {
            Keys = [.. members.Select(m => m.Setting.Key)],
            Name = ScriptName(members[0].Info),
            ValueText = parametersText.Length == 0 ? commandText : commandText + " " + parametersText,
            Elements =
            [
                Labeled(_l[L.GpoSettings.ScriptCommand], commandText),
                Labeled(_l[L.GpoSettings.ScriptParameters], parametersText.Length == 0 ? _l[L.GpoSettings.NoParameters] : parametersText),
            ],
        };
    }

    // ---- Scripts ---------------------------------------------------------------------------------------------------------

    private GpoSettingText Script(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var groups = ScriptGroups(info.Scope, PhaseName(info.ScriptPhase));
        var value = Single(values);
        if (info.Index is null)
        {
            return Row(info, GpoSettingBranch.Policies, groups, info.Name ?? info.Key, value);
        }

        var name = ScriptName(info);
        return info.Field is { Length: > 0 }
            ? Row(info, GpoSettingBranch.Policies, groups, Part(name, _l[L.GpoSettings.ScriptParameters]), value.Length == 0 ? _l[L.GpoSettings.NoParameters] : value)
            : Row(info, GpoSettingBranch.Policies, groups, name, value);
    }

    /// <summary>"Skrypt 1", "Skrypt programu PowerShell 2" (the files number them from 0).</summary>
    private string ScriptName(GpoSettingKeyInfo info) =>
        _l.Format(info.PowerShell ? L.GpoSettings.PowerShellScript : L.GpoSettings.Script, ((info.Index ?? 0) + 1).ToString(_l.Culture));

    /// <summary>"Ustawienia systemu Windows › Skrypty (uruchamianie/zamykanie) › Uruchamianie".</summary>
    private string[] ScriptGroups(GpoScope scope, string? below) => below is null
        ? [_l[L.GpoSettings.WindowsSettings], _l[scope == GpoScope.User ? L.GpoSettings.ScriptsUser : L.GpoSettings.ScriptsComputer]]
        : [_l[L.GpoSettings.WindowsSettings], _l[scope == GpoScope.User ? L.GpoSettings.ScriptsUser : L.GpoSettings.ScriptsComputer], below];

    /// <summary>"Uruchamianie", "Zamykanie", "Logowanie", "Wylogowanie" (other sections as written).</summary>
    private string? PhaseName(string? phase) => phase?.ToUpperInvariant() switch
    {
        null => null,
        "STARTUP" => _l[L.GpoSettings.PhaseStartup],
        "SHUTDOWN" => _l[L.GpoSettings.PhaseShutdown],
        "LOGON" => _l[L.GpoSettings.PhaseLogon],
        "LOGOFF" => _l[L.GpoSettings.PhaseLogoff],
        _ => phase,
    };

    private GpoSettingText ScriptConfig(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var key = info.Name ?? info.Key;
        var user = info.Scope == GpoScope.User;
        var phase = key.ToUpperInvariant() switch
        {
            "STARTEXECUTEPSFIRST" => _l[user ? L.GpoSettings.PhaseLogon : L.GpoSettings.PhaseStartup],
            "ENDEXECUTEPSFIRST" => _l[user ? L.GpoSettings.PhaseLogoff : L.GpoSettings.PhaseShutdown],
            _ => null,
        };
        var value = Single(values).Trim();
        var text = value.ToUpperInvariant() switch
        {
            "TRUE" or "1" => YesNo(true),
            "FALSE" or "0" => YesNo(false),
            _ => value,
        };
        return Row(info, GpoSettingBranch.Policies, ScriptGroups(info.Scope, null), phase is null ? key : _l.Format(L.GpoSettings.PowerShellFirst, phase), text);
    }

    /// <summary>A script file below <c>Scripts/</c>: its size; the content is compared by its SHA-256 hash.</summary>
    private GpoSettingText ScriptFile(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var raw = Single(values);
        string? hash = null;
        long? size = null;
        foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(GpoValues.HashPrefix, StringComparison.Ordinal))
            {
                hash = part[GpoValues.HashPrefix.Length..];
            }
            else if (part.StartsWith("size:", StringComparison.Ordinal) && long.TryParse(part[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
            {
                size = bytes;
            }
        }

        return Row(info, GpoSettingBranch.Policies, ScriptGroups(info.Scope, _l[L.GpoSettings.ScriptFiles]), info.Name ?? info.Key,
            size is { } length ? Size(length) : raw, hash is null ? [] : ["SHA-256: " + hash], hash is null ? null : _l[L.GpoSettings.ExplainScriptFile]);
    }

    /// <summary>"512 B", "12,4 KB", "3,1 MB".</summary>
    private string Size(long bytes) => bytes switch
    {
        < 1024 => _l.Format(L.GpoSettings.Bytes, bytes.ToString("N0", _l.Culture)),
        < 1024 * 1024 => _l.Format(L.GpoSettings.Kilobytes, (bytes / 1024d).ToString("0.#", _l.Culture)),
        _ => _l.Format(L.GpoSettings.Megabytes, (bytes / (1024d * 1024d)).ToString("0.#", _l.Culture)),
    };

    // ---- Comments, unreadable files, other ------------------------------------------------------------------------------

    private GpoSettingText Comment(GpoSettingKeyInfo info, IReadOnlyList<string> values)
    {
        var value = Single(values);
        if (info.Scope == GpoScope.Gpo || info.Name is not { Length: > 0 } policyId)
        {
            return Row(info, GpoSettingBranch.None, [], _l[L.GpoSettings.GpoComment], value) with { Focus = null };
        }

        if (Definitions?.Find(policyId) is { } definition)
        {
            return Row(info, GpoSettingBranch.Policies, PolicyGroups(definition), _l.Format(L.GpoSettings.PolicyComment, definition.DisplayName), value,
                focus: GroupPolicySettingFocus.ForPolicy(info.Scope == GpoScope.User ? PolicyClass.User : PolicyClass.Machine, definition.Id));
        }

        return Row(info, GpoSettingBranch.Policies, [_l[L.GroupPolicyModel.AreaTemplates], _l[L.GpoSettings.PolicyComments]],
            _l.Format(L.GpoSettings.PolicyComment, policyId), value);
    }

    private GpoSettingText UnreadableFile(GpoSettingKeyInfo info) =>
        Row(info, GpoSettingBranch.None, [_l[L.GpoSettings.UnreadableFiles]], info.Name ?? info.Key, _l[L.GroupPolicy.UnreadableValue],
            explanation: _l[L.GpoSettings.ExplainUnreadable]) with { Focus = null };

    private GpoSettingText Other(GpoSettingKeyInfo info, IReadOnlyList<string> values, bool isList) =>
        Row(info, GpoSettingBranch.None, [_l[L.GpoSettings.Other]], info.Name ?? info.Key, isList ? string.Join(", ", values) : Single(values),
            isList ? values : null);
}
