using System.Globalization;
using AdMng.Core.GroupPolicy.Content;
using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;

namespace AdMng.Core.GroupPolicy.Cleanup;

/// <summary>
/// The cleanup advisor (docs/dev/k-computers-and-gpo.md 21.6): pure rules over a <see cref="GpoSettingsIndex"/>, no I/O.
/// GPO-level rules (unlinked, all links disabled, GPO disabled) use the directory data of every entry; content rules
/// (empty, single setting, duplicates, different values, merge candidates) only entries read completely
/// (<see cref="GpoIndexStatus.Read"/>) – the others are listed in <see cref="GpoCleanupResult.NotEvaluated"/>. Every
/// proposal is advice: its actions are plans with preview and undo (rule 5), built by the planners from a fresh read.
/// </summary>
public static partial class GpoCleanupAnalyzer
{
    private static readonly CompareInfo NameComparer = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>The proposals for <paramref name="index"/>, sorted by kind, then GPO name.</summary>
    public static GpoCleanupResult Analyze(GpoSettingsIndex index, GpoCleanupOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        options ??= GpoCleanupOptions.Default;
        var context = new AnalysisContext(index);
        var proposals = new List<GpoCleanupProposal>();
        foreach (var entry in context.Entries)
        {
            GpoRules(context, entry, proposals);
            if (entry.Status == GpoIndexStatus.Read)
            {
                EmptyGpo(entry, proposals);
                SingleSetting(context, entry, proposals);
            }
        }

        SettingRules(context, options, proposals);
        MergeCandidates(context, proposals);

        proposals.Sort((a, b) => Compare(context, a, b));
        var notEvaluated = context.Entries
            .Where(e => e.Status != GpoIndexStatus.Read)
            .OrderBy(e => e.Gpo.DisplayName, NameOrder)
            .ThenBy(e => e.Gpo.Id)
            .Select(e => e.Gpo.Id)
            .ToList();
        return new GpoCleanupResult(proposals, notEvaluated);
    }

    /// <summary>Unlinked, all links disabled, GPO disabled: directory data only, every entry.</summary>
    private static void GpoRules(AnalysisContext context, GpoIndexEntry entry, List<GpoCleanupProposal> proposals)
    {
        var gpo = entry.Gpo;
        var disabled = gpo.Status == GpoStatus.AllSettingsDisabled;
        if (!entry.IsLinked)
        {
            var actions = new List<GpoCleanupAction>();
            AddDelete(gpo, actions);
            if (!disabled)
            {
                actions.Add(GpoCleanupAction.Disable);
            }

            var notes = new List<GpoCleanupNote>();
            AddDefault(gpo, notes);
            if (context.Index.OtherDomains.Count > 0)
            {
                notes.Add(GpoCleanupNote.OtherDomainsNotSearched);
            }

            proposals.Add(Proposal(GpoCleanupKind.UnlinkedGpo, entry, actions, notes));
        }
        else if (entry.Links.All(l => !l.Enabled))
        {
            var actions = new List<GpoCleanupAction> { GpoCleanupAction.Unlink };
            if (!disabled)
            {
                actions.Add(GpoCleanupAction.Disable);
            }

            AddDelete(gpo, actions);
            var notes = new List<GpoCleanupNote>();
            AddDefault(gpo, notes);
            proposals.Add(Proposal(GpoCleanupKind.AllLinksDisabled, entry, actions, notes));
        }

        if (disabled)
        {
            var actions = new List<GpoCleanupAction>();
            AddDelete(gpo, actions);
            if (entry.IsLinked)
            {
                actions.Add(GpoCleanupAction.Unlink);
            }

            var notes = new List<GpoCleanupNote>();
            AddDefault(gpo, notes);
            proposals.Add(Proposal(GpoCleanupKind.GpoDisabled, entry, actions, notes));
        }
    }

    /// <summary>
    /// No counted unit (disabled preference items count), no unreadable marker, no unparsed files, no directory-side
    /// settings – linked or not, versions ignored.
    /// </summary>
    private static void EmptyGpo(GpoIndexEntry entry, List<GpoCleanupProposal> proposals)
    {
        if (entry.Units.Any(u => u.CountsAsSetting) || HasUnreadable(entry) || entry.OtherFiles.Count > 0 || entry.DirectorySettings > 0)
        {
            return;
        }

        var actions = new List<GpoCleanupAction>();
        AddDelete(entry.Gpo, actions);
        if (entry.IsLinked)
        {
            actions.Add(GpoCleanupAction.Unlink);
        }

        var notes = new List<GpoCleanupNote>();
        if (entry.IsLinked)
        {
            notes.Add(GpoCleanupNote.Linked);
        }

        AddDefault(entry.Gpo, notes);
        proposals.Add(Proposal(GpoCleanupKind.EmptyGpo, entry, actions, notes));
    }

    /// <summary>
    /// Exactly one counted unit, no directory-side settings, no unparsed files, no unreadable marker; never a default GPO
    /// (settings are not consolidated into or out of them). CopyTo, then Disable as a separate action; Merge only into a GPO
    /// with the same links and filters.
    /// </summary>
    private static void SingleSetting(AnalysisContext context, GpoIndexEntry entry, List<GpoCleanupProposal> proposals)
    {
        var gpo = entry.Gpo;
        if (gpo.IsDefault || entry.DirectorySettings > 0 || entry.OtherFiles.Count > 0 || HasUnreadable(entry))
        {
            return;
        }

        var counted = entry.Units.Where(u => u.CountsAsSetting).Take(2).ToList();
        if (counted.Count != 1)
        {
            return;
        }

        var unit = counted[0];
        var actions = new List<GpoCleanupAction>();
        var notes = new List<GpoCleanupNote>();
        IReadOnlyList<string> blocking = [];
        IReadOnlyList<Guid> related = [];
        if (unit.Refusal is not null)
        {
            notes.Add(GpoCleanupNote.NonCopyableUnits);
            blocking = [unit.Id];
        }
        else
        {
            actions.Add(GpoCleanupAction.CopyTo);
        }

        if (gpo.Status != GpoStatus.AllSettingsDisabled)
        {
            actions.Add(GpoCleanupAction.Disable);
        }

        if (unit.Refusal is null)
        {
            var blockers = MergeBlockers(entry);
            notes.AddRange(blockers.Notes);
            related = MergeTargets(context, entry, unit);
            if (blockers.Notes.Count == 0 && related.Count > 0)
            {
                actions.Add(GpoCleanupAction.Merge);
            }

            // The same link-order remarks as a merge candidate's, for any of the targets.
            var targets = related.Select(context.Find).OfType<GpoIndexEntry>().ToList();
            if (targets.Any(t => RanksAbove(entry, t)))
            {
                notes.Add(GpoCleanupNote.LinkOrderDiffers);
            }

            if (targets.Any(t => LinkedBetween(context, entry, t)))
            {
                notes.Add(GpoCleanupNote.LinkedBetween);
            }
        }

        if (unit.RemovePolicy)
        {
            notes.Add(GpoCleanupNote.RemovePolicy);
        }

        proposals.Add(Proposal(GpoCleanupKind.SingleSettingGpo, entry, actions, notes) with
        {
            UnitBaseId = unit.BaseId,
            RelatedGpoIds = related,
            BlockingUnitIds = blocking,
        });
    }

    /// <summary>
    /// The GPOs a single-setting GPO can be merged into: same link set, same filters, read, not default, not disabled, and
    /// the half the unit belongs to not disabled (the setting would go dormant there).
    /// </summary>
    private static IReadOnlyList<Guid> MergeTargets(AnalysisContext context, GpoIndexEntry entry, GpoSettingUnit unit)
    {
        if (!entry.Links.Any(l => l.Enabled))
        {
            return [];
        }

        var key = context.ScopeKey(entry);
        return context.Entries
            .Where(e => e.Gpo.Id != entry.Gpo.Id && IsMergeTarget(e) && !HalfDisabled(e.Gpo.Status, unit.Scope) && context.ScopeKey(e) == key)
            .OrderByDescending(e => e.Units.Count(u => u.CountsAsSetting))
            .ThenBy(e => e.Gpo.DisplayName, NameOrder)
            .ThenBy(e => e.Gpo.Id)
            .Select(e => e.Gpo.Id)
            .ToList();
    }

    /// <summary>A GPO that may take part in a merge: read, linked (≥ 1 enabled link), not default, not disabled, with settings.</summary>
    private static bool IsMergeTarget(GpoIndexEntry entry) =>
        entry.Status == GpoIndexStatus.Read && !entry.Gpo.IsDefault && entry.Gpo.Status != GpoStatus.AllSettingsDisabled
        && entry.Links.Any(l => l.Enabled) && entry.Units.Any(u => u.CountsAsSetting);

    /// <summary>
    /// Why a GPO cannot be merged into another (empty: it can): units that block a merge, directory-side settings, unparsed
    /// files, a default GPO, a disabled half that holds counted units.
    /// </summary>
    private static (List<GpoCleanupNote> Notes, IReadOnlyList<string> BlockingUnitIds) MergeBlockers(GpoIndexEntry entry)
    {
        var notes = new List<GpoCleanupNote>();
        IReadOnlyList<string> blocking = [.. entry.Units.Where(u => u.BlocksMerge).Select(u => u.Id)];
        if (blocking.Count > 0)
        {
            notes.Add(GpoCleanupNote.NonCopyableUnits);
        }

        if (entry.DirectorySettings > 0)
        {
            notes.Add(GpoCleanupNote.DirectorySettings);
        }

        if (entry.OtherFiles.Count > 0)
        {
            notes.Add(GpoCleanupNote.OtherFiles);
        }

        if (entry.Units.Any(u => u.CountsAsSetting && HalfDisabled(entry.Gpo.Status, u.Scope)))
        {
            notes.Add(GpoCleanupNote.PartDisabled);
        }

        AddDefault(entry.Gpo, notes);
        return (notes, blocking);
    }

    private static bool HasUnreadable(GpoIndexEntry entry) => entry.Units.Any(u => u.Kind == GpoSettingUnitKind.UnreadableFile);

    /// <summary>The GPO's status keeps clients from processing the half <paramref name="scope"/> belongs to.</summary>
    private static bool HalfDisabled(GpoStatus status, GpoScope scope) => scope switch
    {
        GpoScope.Computer => (status & GpoStatus.ComputerSettingsDisabled) != 0,
        GpoScope.User => (status & GpoStatus.UserSettingsDisabled) != 0,
        _ => false,
    };

    private static void AddDelete(GpoInfo gpo, List<GpoCleanupAction> actions)
    {
        if (!gpo.IsDefault)
        {
            actions.Add(GpoCleanupAction.Delete);
        }
    }

    private static void AddDefault(GpoInfo gpo, List<GpoCleanupNote> notes)
    {
        if (gpo.IsDefault && !notes.Contains(GpoCleanupNote.DefaultGpo))
        {
            notes.Add(GpoCleanupNote.DefaultGpo);
        }
    }

    /// <summary>A proposal about a whole GPO: the containers of its links (the ones Unlink and Delete touch).</summary>
    private static GpoCleanupProposal Proposal(GpoCleanupKind kind, GpoIndexEntry entry, List<GpoCleanupAction> actions, List<GpoCleanupNote> notes) => new()
    {
        Kind = kind,
        GpoId = entry.Gpo.Id,
        ContainerDns = LinkContainers(entry),
        Actions = [.. actions.Distinct()],
        Notes = [.. notes.Distinct().Order()],
    };

    private static IReadOnlyList<string> LinkContainers(GpoIndexEntry entry)
    {
        var result = new List<string>();
        foreach (var link in entry.Links)
        {
            if (!result.Any(dn => Ldap.DistinguishedName.AreEqual(dn, link.ContainerDn)))
            {
                result.Add(link.ContainerDn);
            }
        }

        return result;
    }

    private static int Compare(AnalysisContext context, GpoCleanupProposal a, GpoCleanupProposal b)
    {
        var result = a.Kind.CompareTo(b.Kind);
        if (result == 0)
        {
            result = NameComparer.Compare(context.NameOf(a.GpoId), context.NameOf(b.GpoId), CompareOptions.IgnoreCase);
        }

        if (result == 0)
        {
            result = a.GpoId.CompareTo(b.GpoId);
        }

        if (result == 0)
        {
            result = string.Compare(a.UnitBaseId, b.UnitBaseId, StringComparison.OrdinalIgnoreCase);
        }

        if (result == 0)
        {
            result = (a.RelatedGpoIds.Count > 0 ? a.RelatedGpoIds[0] : Guid.Empty).CompareTo(b.RelatedGpoIds.Count > 0 ? b.RelatedGpoIds[0] : Guid.Empty);
        }

        return result;
    }
}
