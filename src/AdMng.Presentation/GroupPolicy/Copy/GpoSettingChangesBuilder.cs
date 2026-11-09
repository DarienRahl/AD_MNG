using AdMng.Core.Batch;
using AdMng.Core.Directory;
using AdMng.Core.GroupPolicy;
using AdMng.Core.GroupPolicy.AdministrativeTemplates;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Presentation.Changes;
using AdMng.Presentation.GroupPolicy.Settings;
using AdMng.Presentation.Localization;

namespace AdMng.Presentation.GroupPolicy.Copy;

/// <summary>
/// "Zmiany ustawień" of a K7 plan (docs/dev/k-computers-and-gpo.md 21.5): the old and new bytes of every
/// <see cref="ChangeAction.WriteGpoFile"/> step are parsed (<see cref="GroupPolicyReader.ReadSettings"/> of a folder made of
/// the written files only), grouped into setting units on both sides (<see cref="GpoSettingUnits.Of(GpoFolderContent, GpoUnitContext)"/>)
/// and each added, changed or removed unit is described before and after in GPMC's words by a
/// <see cref="GpoSettingPresenter"/> per side (policies from both Registry.pol versions when a definition set is loaded,
/// SID names from one <see cref="TrusteeNames.ResolveAsync"/> batch); masked values stay masked. The group of a row is the
/// GPO's name › the setting's place. Built in memory for the preview only (rule 1).
/// </summary>
public static class GpoSettingChangesBuilder
{
    /// <summary>The rows of <paramref name="plan"/> (none when no GPO file is written).</summary>
    public static async Task<IReadOnlyList<GroupPolicySettingChange>> BuildAsync(ChangePlan plan, IDirectorySession? session, ILocalizer localizer,
        PolicyDefinitionSet? definitions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(localizer);
        var sides = Sides(plan);
        if (sides.Count == 0)
        {
            return [];
        }

        var sids = sides.SelectMany(s => GpoSettingPresenter.Sids(s.BeforeSettings).Concat(GpoSettingPresenter.Sids(s.AfterSettings)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        IReadOnlyDictionary<string, string> names = sids.Count == 0 || session is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : await TrusteeNames.ResolveAsync(session, sids, localizer, cancellationToken).ConfigureAwait(true);
        var rows = new List<GroupPolicySettingChange>();
        foreach (var side in sides)
        {
            rows.AddRange(Describe(side, names, localizer, definitions));
        }

        return rows;
    }

    /// <summary>The rows of one GPO's written files (pure: no directory reads; tests and the builder above).</summary>
    internal static IReadOnlyList<GroupPolicySettingChange> Describe(GpoSides side, IReadOnlyDictionary<string, string> names, ILocalizer localizer,
        PolicyDefinitionSet? definitions)
    {
        var context = new GpoUnitContext(definitions, null);
        var before = Units(side.Before, context);
        var after = Units(side.After, context);
        var beforePresenter = Presenter(side.Before, side.BeforeSettings, names, localizer, definitions);
        var afterPresenter = Presenter(side.After, side.AfterSettings, names, localizer, definitions);
        var notConfigured = localizer[L.GpoCopy.NotConfigured];
        var rows = new List<GroupPolicySettingChange>();
        var ids = before.Keys.Concat(after.Keys.Where(k => !before.ContainsKey(k))).ToList();
        foreach (var id in ids)
        {
            before.TryGetValue(id, out var old);
            after.TryGetValue(id, out var now);
            if (old is not null && now is not null && string.Equals(old.ValueFingerprint, now.ValueFingerprint, StringComparison.Ordinal)
                && old.Settings.Count == now.Settings.Count)
            {
                continue;
            }

            var oldText = old is null ? null : Text(beforePresenter, old);
            var newText = now is null ? null : Text(afterPresenter, now);
            var shown = newText ?? oldText!;
            rows.Add(new GroupPolicySettingChange(
                shown.Name,
                oldText?.ValueText ?? notConfigured,
                newText?.ValueText ?? notConfigured,
                side.GpoName + GpoSettingText.Separator + shown.GroupPath));
        }

        return rows;
    }

    /// <summary>The written files of each GPO of the plan, before and after.</summary>
    internal static IReadOnlyList<GpoSides> Sides(ChangePlan plan)
    {
        var result = new List<GpoSides>();
        foreach (var objectPlan in plan.Objects)
        {
            var writes = objectPlan.Changes
                .Where(c => c.Action is ChangeAction.WriteGpoFile or ChangeAction.DeleteGpoFile && c.Sysvol is { RelativePath: not null })
                .Select(c => c.Sysvol!)
                .ToList();
            if (writes.Count == 0)
            {
                continue;
            }

            foreach (var gpo in writes.GroupBy(w => w.GpoId))
            {
                var before = Folder(gpo.Where(w => w.OldContent is not null).Select(w => (w.RelativePath!, w.OldContent!)));
                var after = Folder(gpo.Where(w => w.NewContent is not null).Select(w => (w.RelativePath!, w.NewContent!)));
                result.Add(new GpoSides(objectPlan.DisplayName, before, after, Settings(before), Settings(after)));
            }
        }

        return result;
    }

    private static GpoFolderContent Folder(IEnumerable<(string Path, byte[] Content)> files)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, content) in files)
        {
            map[path] = content;
        }

        return new GpoFolderContent([.. map.Select(f => new SysvolEntry(f.Key, false, f.Value.Length, DateTimeOffset.MinValue))], map);
    }

    private static IReadOnlyList<GpoSetting> Settings(GpoFolderContent content) => GroupPolicyReader.ReadSettings(content).Settings;

    private static Dictionary<string, GpoSettingUnit> Units(GpoFolderContent content, GpoUnitContext context)
    {
        var result = new Dictionary<string, GpoSettingUnit>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in GpoSettingUnits.Of(content, context).Where(u => u.Kind != GpoSettingUnitKind.UnreadableFile))
        {
            result.TryAdd(unit.Id, unit);
        }

        return result;
    }

    private static GpoSettingPresenter Presenter(GpoFolderContent content, IReadOnlyList<GpoSetting> settings, IReadOnlyDictionary<string, string> names,
        ILocalizer localizer, PolicyDefinitionSet? definitions)
    {
        if (definitions is null)
        {
            return new GpoSettingPresenter(localizer, names) { ItemNames = GpoSettingPresenter.ItemNamesOf(settings) };
        }

        var (computer, user) = GpoSettingPresenter.Policies(content, definitions);
        return new GpoSettingPresenter(localizer, names, definitions)
        {
            ComputerPolicies = computer,
            UserPolicies = user,
            ItemNames = GpoSettingPresenter.ItemNamesOf(settings),
        };
    }

    private static GpoSettingText Text(GpoSettingPresenter presenter, GpoSettingUnit unit) => presenter.DescribeRows(unit.Settings) is [var first, ..]
        ? first
        : new GpoSettingText
        {
            Key = unit.Id,
            Scope = unit.Scope,
            Kind = unit.Area,
            Configuration = string.Empty,
            Name = unit.Id,
            Technical = unit.Id,
            ValueText = string.Empty,
        };

    /// <summary>One GPO's written files before and after (in memory only; <see cref="ToString"/> prints counts).</summary>
    internal sealed record GpoSides(string GpoName, GpoFolderContent Before, GpoFolderContent After, IReadOnlyList<GpoSetting> BeforeSettings,
        IReadOnlyList<GpoSetting> AfterSettings)
    {
        public override string ToString() => $"{Before.Files.Count} → {After.Files.Count} files";
    }
}
