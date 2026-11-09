using AdMng.Core.GroupPolicy.Management;
using AdMng.Core.GroupPolicy.Search;
using AdMng.Core.Ldap;

namespace AdMng.Core.GroupPolicy.Cleanup;

public static partial class GpoCleanupAnalyzer
{
    /// <summary>
    /// <see cref="GpoCleanupKind.MergeCandidate"/>: read GPOs with settings and the same links (container, enabled,
    /// enforced), security filter and WMI filter; each is proposed into the one with the most counted units, preferring a
    /// GPO with no disabled half (a source's settings would go dormant in the target's disabled half). Default GPOs and
    /// disabled GPOs never take part. A GPO that cannot be merged gets the proposal without
    /// <see cref="GpoCleanupAction.Merge"/> and the notes that say why (<see cref="GpoCleanupNote.PartDisabled"/> also when
    /// the target's disabled half would take some of its settings).
    /// </summary>
    private static void MergeCandidates(AnalysisContext context, List<GpoCleanupProposal> proposals)
    {
        var groups = context.Entries
            .Where(IsMergeTarget)
            .GroupBy(context.ScopeKey, StringComparer.Ordinal)
            .Where(g => g.Count() >= 2);
        foreach (var group in groups)
        {
            var members = group
                .OrderByDescending(e => e.Units.Count(u => u.CountsAsSetting))
                .ThenBy(e => e.Gpo.DisplayName, NameOrder)
                .ThenBy(e => e.Gpo.Id)
                .ToList();
            var target = members.FirstOrDefault(e => e.Gpo.Status == GpoStatus.AllSettingsEnabled) ?? members[0];
            foreach (var source in members.Where(e => e.Gpo.Id != target.Gpo.Id))
            {
                var (notes, blocking) = MergeBlockers(source);
                if (source.Units.Any(u => u.CountsAsSetting && HalfDisabled(target.Gpo.Status, u.Scope)))
                {
                    notes.Add(GpoCleanupNote.PartDisabled);
                }

                IReadOnlyList<GpoCleanupAction> actions = notes.Count == 0 ? [GpoCleanupAction.Merge] : [];
                if (RanksAbove(source, target))
                {
                    notes.Add(GpoCleanupNote.LinkOrderDiffers);
                }

                if (LinkedBetween(context, source, target))
                {
                    notes.Add(GpoCleanupNote.LinkedBetween);
                }

                if (source.Units.Any(u => u.CountsAsSetting && u.RemovePolicy))
                {
                    notes.Add(GpoCleanupNote.RemovePolicy);
                }

                proposals.Add(new GpoCleanupProposal
                {
                    Kind = GpoCleanupKind.MergeCandidate,
                    GpoId = source.Gpo.Id,
                    RelatedGpoIds = [target.Gpo.Id],
                    ContainerDns = LinkContainers(source),
                    Actions = actions,
                    Notes = [.. notes.Distinct().Order()],
                    BlockingUnitIds = blocking,
                });
            }
        }
    }

    /// <summary>
    /// In a container that links both, the source has the higher precedence (a lower link order): after the merge its
    /// settings take the target's position, so a GPO between them may win where the source won before.
    /// </summary>
    private static bool RanksAbove(GpoIndexEntry source, GpoIndexEntry target) =>
        source.Links.Any(s => target.Links.Any(t => DistinguishedName.AreEqual(s.ContainerDn, t.ContainerDn) && s.Order < t.Order));

    /// <summary>
    /// In a container that links both, an enabled link between the two (in either direction) is to a GPO that applies a
    /// setting the source sets, or whose content is unknown (not read, or not in the index): after the merge the source's
    /// settings take the target's position and pass that GPO – a setting it won over may now win over it, or the reverse.
    /// GPOs from other containers never sit between two GPOs with the same link set.
    /// </summary>
    private static bool LinkedBetween(AnalysisContext context, GpoIndexEntry source, GpoIndexEntry target)
    {
        var baseIds = source.Units.Where(u => u.CountsAsSetting).Select(u => u.BaseId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var container in context.Index.Containers)
        {
            if (container.Links.Find(source.Gpo.Id) is not { } s || container.Links.Find(target.Gpo.Id) is not { } t)
            {
                continue;
            }

            var (low, high) = (Math.Min(s.Order, t.Order), Math.Max(s.Order, t.Order));
            foreach (var link in container.Links.Links.Where(l => l.Enabled && l.Order > low && l.Order < high))
            {
                if (context.Find(link.GpoId) is not { } other || other.Status != GpoIndexStatus.Read
                    || other.Units.Any(u => u.CountsAsSetting && baseIds.Contains(u.BaseId) && Applies(other, u)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
