using AdMng.Core.GroupPolicy.Search;
using AdMng.Core.Ldap;

namespace AdMng.Core.GroupPolicy.Cleanup;

public static partial class GpoCleanupAnalyzer
{
    /// <summary>
    /// <see cref="GpoCleanupKind.MergeCandidate"/>: read GPOs with settings and the same links (container, enabled,
    /// enforced), security filter and WMI filter; each is proposed into the one with the most counted units. Default GPOs
    /// and disabled GPOs never take part. A GPO that cannot be merged gets the proposal without
    /// <see cref="GpoCleanupAction.Merge"/> and the notes that say why.
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
            var target = members[0];
            foreach (var source in members.Skip(1))
            {
                var (notes, blocking) = MergeBlockers(source);
                IReadOnlyList<GpoCleanupAction> actions = notes.Count == 0 ? [GpoCleanupAction.Merge] : [];
                if (RanksAbove(source, target))
                {
                    notes.Add(GpoCleanupNote.LinkOrderDiffers);
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
}
