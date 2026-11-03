using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Modeling;
using AdMng.Core.Ldap;

namespace AdMng.Core.GroupPolicy.Diagnostics;

/// <summary>
/// Conflicting and redundant settings (Full): <see cref="GroupPolicyModeler.Build"/> for a new computer and a new user in
/// every domain head or OU with links (<see cref="GroupPolicyModelReader.ContainerTarget"/>, loopback off, WMI filters
/// assumed true). A setting whose winner overrides a lower-precedence GPO with another value is a conflict, with the same
/// value a redundancy. A preference item (all its properties) counts as one setting. Without ADMX templates the
/// Registry.pol values are compared value by value. A finding is reported at the topmost container where it occurs (the
/// same winner, losers and setting below it are not repeated).
/// </summary>
public static partial class GroupPolicyDiagnostics
{
    private static void ConflictRules(Context c)
    {
        var gpos = c.Snapshot.Gpos.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => ToModelingGpo(g.First(), c.Snapshot.WmiFilters));
        var contents = new Dictionary<Guid, ModelingGpoContent>();
        foreach (var id in c.Snapshot.Folders.Keys)
        {
            contents[id] = c.View(id)?.Modeling ?? new ModelingGpoContent { Unreadable = true };
        }

        var containers = c.Snapshot.Containers.Select(x => x.ToSom()).ToList();
        var targets = c.Snapshot.Containers
            .Where(x => x.Kind != GroupPolicySomKind.Site && x.Links.Links.Count > 0)
            .Select(x => (Container: x, Dn: DistinguishedName.TryParse(x.Dn, out var dn) ? dn : null))
            .Where(x => x.Dn is not null)
            .OrderBy(x => x.Dn!.Rdns.Count)
            .ThenBy(x => x.Container.Dn, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Container)
            .ToList();
        if (c.Snapshot.GpoId is { } one)
        {
            targets = [.. targets.Where(x => c.Inheritance.Resolve(x.Dn, siteDn: null).Any(a => a.GpoId == one))];
        }

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            var model = GroupPolicyModeler.Build(new GroupPolicyModelInput
            {
                DomainDn = c.DomainDn,
                Containers = containers,
                Gpos = gpos,
                Computer = GroupPolicyModelReader.ContainerTarget(target.Dn, ModelingPart.Computer, c.DomainSid),
                User = GroupPolicyModelReader.ContainerTarget(target.Dn, ModelingPart.User, c.DomainSid),
                Options = new ModelingOptions { Loopback = LoopbackChoice.Off },
            }, contents);
            foreach (var part in new[] { model.Computer, model.User })
            {
                if (part is not null)
                {
                    PartConflicts(c, target.Dn, part, reported);
                }
            }
        }
    }

    private static void PartConflicts(Context c, string containerDn, ModeledPart part, HashSet<string> reported)
    {
        // Per setting unit (a key, or a preference item) and winning GPO: the losers and whether any value differs.
        var units = new Dictionary<(string Unit, Guid Winner), UnitPairs>();
        foreach (var setting in part.Settings.Where(s => !s.Cumulative && s.Overridden.Count > 0 && Comparable(s.Winner)))
        {
            var unit = PreferenceItem(setting.Key) ?? setting.Key;
            var id = (unit, setting.Winner.Source.GpoId);
            if (!units.TryGetValue(id, out var pairs))
            {
                pairs = new UnitPairs(setting.Winner.Source, unit == setting.Key ? setting.Winner.Setting.Value : null);
                units[id] = pairs;
            }

            foreach (var loser in setting.Overridden.Where(o => Comparable(o) && o.Source.GpoId != setting.Winner.Source.GpoId))
            {
                pairs.Add(loser.Source, !string.Equals(loser.Setting.Value, setting.Winner.Setting.Value, StringComparison.Ordinal),
                    unit == setting.Key ? loser.Setting.Value : null);
            }
        }

        foreach (var ((unit, winner), pairs) in units)
        {
            var differ = pairs.Losers.Where(l => l.Differs).ToList();
            var same = pairs.Losers.Where(l => !l.Differs).ToList();
            if (differ.Count > 0)
            {
                Report(GpoFindingCodes.ConflictingSetting, differ,
                    [pairs.Winner.GpoName, Names(differ), pairs.WinnerValue ?? string.Empty, string.Join("; ", differ.Select(l => l.Value ?? string.Empty))]);
            }

            if (same.Count > 0)
            {
                Report(GpoFindingCodes.RedundantSetting, same, [pairs.Winner.GpoName, Names(same), pairs.WinnerValue ?? string.Empty]);
            }

            void Report(string code, List<UnitLoser> losers, IReadOnlyList<string> arguments)
            {
                if (c.Snapshot.GpoId is { } one && winner != one && losers.All(l => l.Source.GpoId != one))
                {
                    return;
                }

                var key = string.Join('|', code, unit, winner, string.Join(',', losers.Select(l => l.Source.GpoId).Order()));
                if (reported.Add(key))
                {
                    c.Add(code, winner, containerDn, unit, arguments, related: [.. losers.Select(l => l.Source.GpoId)], gpoName: pairs.Winner.GpoName);
                }
            }
        }

        static string Names(List<UnitLoser> losers) => string.Join(", ", losers.Select(l => l.Source.GpoName));
    }

    /// <summary>A source whose value can be compared: not conditional (item-level targeting), not masked or unreadable.</summary>
    private static bool Comparable(ModeledSettingSource source) =>
        !source.Conditional && source.Setting.Kind is GpoSettingKind.Text or GpoSettingKind.Hash;

    /// <summary>The part of a preference key up to its item (<c>Computer/Preferences/Drives/{uid}</c>); null for other keys.</summary>
    private static string? PreferenceItem(string key)
    {
        var parts = key.Split('/', 5);
        if (parts.Length < 4 || !parts[1].Equals("Preferences", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var item = parts[3];
        var at = item.IndexOf('@', StringComparison.Ordinal);
        return string.Join('/', parts[0], parts[1], parts[2], at < 0 ? item : item[..at]);
    }

    private static ModelingGpo ToModelingGpo(GpoInfo gpo, IReadOnlyList<WmiFilterInfo> filters)
    {
        var hasWmi = !string.IsNullOrWhiteSpace(gpo.WmiFilterLink);
        return new ModelingGpo
        {
            Id = gpo.Id,
            DisplayName = gpo.DisplayName,
            Flags = gpo.Flags,
            Version = gpo.DirectoryVersion,
            HasWmiFilter = hasWmi,
            WmiFilterName = gpo.WmiFilterId is { } id ? filters.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase))?.Name : null,
            SecurityFilter = gpo.SecurityFilter,
        };
    }

    private sealed record UnitLoser(ModeledSource Source, bool Differs, string? Value);

    /// <summary>The losers of one winner for one setting unit, each once (a preference item differs when any property differs).</summary>
    private sealed class UnitPairs(ModeledSource winner, string? winnerValue)
    {
        private readonly List<UnitLoser> _losers = [];

        public ModeledSource Winner { get; } = winner;

        public string? WinnerValue { get; } = winnerValue;

        /// <summary>The losers in precedence order.</summary>
        public IEnumerable<UnitLoser> Losers => _losers.OrderBy(l => l.Source.Precedence);

        public void Add(ModeledSource source, bool differs, string? value)
        {
            var index = _losers.FindIndex(l => l.Source.GpoId == source.GpoId);
            if (index < 0)
            {
                _losers.Add(new UnitLoser(source, differs, value));
            }
            else if (differs && !_losers[index].Differs)
            {
                _losers[index] = _losers[index] with { Differs = true };
            }
        }
    }
}
