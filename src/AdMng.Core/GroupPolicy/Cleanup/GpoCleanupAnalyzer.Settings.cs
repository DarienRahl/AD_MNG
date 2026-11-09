using System.Globalization;
using AdMng.Core.GroupPolicy.Modeling;
using AdMng.Core.GroupPolicy.Search;

namespace AdMng.Core.GroupPolicy.Cleanup;

public static partial class GpoCleanupAnalyzer
{
    private const string SystemAccessKey = "K|Computer/Security/System Access/";
    private const string KerberosKey = "K|Computer/Security/Kerberos Policy/";

    /// <summary>
    /// <see cref="GpoCleanupKind.DuplicateSetting"/> and <see cref="GpoCleanupKind.DifferentValues"/> per
    /// <see cref="GpoSettingUnit.BaseId"/> (21.6 "DuplicateSetting in detail").
    /// </summary>
    private static void SettingRules(AnalysisContext context, GpoCleanupOptions options, List<GpoCleanupProposal> proposals)
    {
        var occurrences = new Dictionary<string, List<Occurrence>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in context.Entries)
        {
            foreach (var unit in entry.Units)
            {
                // Comments and script payloads are not settings: never duplicates, never "different values".
                if (!unit.CountsAsSetting || string.IsNullOrEmpty(unit.BaseId) || !Applies(entry, unit))
                {
                    continue;
                }

                if (!occurrences.TryGetValue(unit.BaseId, out var list))
                {
                    occurrences[unit.BaseId] = list = [];
                }

                list.Add(new Occurrence(entry, unit));
            }
        }

        foreach (var (baseId, applying) in occurrences.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (applying.Select(o => o.Entry.Gpo.Id).Distinct().Take(2).Count() < 2)
            {
                continue;
            }

            var accountPolicy = IsAccountPolicy(baseId);

            // Account policy: only GPOs linked to the domain head take part (below it they have no domain effect).
            var participants = applying.Where(o => o.Entry.Status == GpoIndexStatus.Read && o.Unit.Comparability == GpoUnitComparability.Comparable
                && (!accountPolicy || context.LinkedToDomainHead(o.Entry))).ToList();
            var classes = ValueClasses(participants);
            if (options.IncludeDifferentValues)
            {
                DifferentValues(context, baseId, accountPolicy, participants, classes, proposals);
            }

            Duplicates(context, baseId, accountPolicy, applying, participants, classes, proposals);
        }
    }

    /// <summary>
    /// The occurrence can reach clients: its GPO has an enabled link, its half is not disabled by the GPO's status and
    /// the unit itself is not disabled (preference item or collection with <c>disabled="1"</c>).
    /// </summary>
    private static bool Applies(GpoIndexEntry entry, GpoSettingUnit unit) =>
        entry.Links.Any(l => l.Enabled) && !HalfDisabled(entry.Gpo.Status, unit.Scope) && !unit.Disabled;

    /// <summary>Comparable applying occurrences with ≥ 2 distinct values in ≥ 2 GPOs (informational).</summary>
    private static void DifferentValues(AnalysisContext context, string baseId, bool accountPolicy, List<Occurrence> participants,
        List<List<Occurrence>> classes, List<GpoCleanupProposal> proposals)
    {
        if (classes.Count < 2)
        {
            return;
        }

        var gpos = participants.Select(o => o.Entry).DistinctBy(e => e.Gpo.Id).OrderBy(e => e.Gpo.DisplayName, NameOrder).ThenBy(e => e.Gpo.Id).ToList();
        if (gpos.Count < 2)
        {
            return;
        }

        proposals.Add(new GpoCleanupProposal
        {
            Kind = GpoCleanupKind.DifferentValues,
            GpoId = gpos[0].Gpo.Id,
            RelatedGpoIds = [.. gpos.Skip(1).Select(e => e.Gpo.Id)],
            UnitBaseId = baseId,
            Notes = accountPolicy ? [GpoCleanupNote.AccountPolicy] : [],
        });
    }

    /// <summary>
    /// Equal values in GPOs where one covers the other: RemoveSetting from the covered GPO. Nothing is proposed for a base
    /// id with another applying value, a value that cannot be compared, an incompletely read GPO that applies it, or a GPO
    /// that holds it twice. Each GPO is ranked (default GPO, more settings, name) and only proposed for removal when a
    /// higher-ranked GPO covers it – so mutually covering GPOs never both lose the setting, and executing every proposal
    /// keeps each container's value (coverage is transitive, the highest-ranked keeper is never removed).
    /// </summary>
    private static void Duplicates(AnalysisContext context, string baseId, bool accountPolicy, List<Occurrence> applying,
        List<Occurrence> participants, List<List<Occurrence>> classes, List<GpoCleanupProposal> proposals)
    {
        // No other value: every applying occurrence (any GPO, also below the domain head for account policy) must be
        // read completely, comparable, alone in its GPO and of one value.
        if (applying.Any(o => o.Entry.Status != GpoIndexStatus.Read || o.Unit.Comparability != GpoUnitComparability.Comparable)
            || applying.GroupBy(o => o.Entry.Gpo.Id).Any(g => g.Count() > 1)
            || ValueClasses(applying).Count != 1
            || classes.Count != 1)
        {
            return;
        }

        var ranked = participants
            .OrderByDescending(o => o.Entry.Gpo.IsDefault)
            .ThenByDescending(o => o.Entry.Units.Count(u => u.CountsAsSetting))
            .ThenBy(o => o.Entry.Gpo.DisplayName, NameOrder)
            .ThenBy(o => o.Entry.Gpo.Id)
            .ToList();
        for (var i = 1; i < ranked.Count; i++)
        {
            var covered = ranked[i];
            var keepers = ranked.Take(i).Where(k => context.Covers(k.Entry, covered.Entry)).Select(k => k.Entry.Gpo.Id).ToList();
            if (keepers.Count == 0)
            {
                continue;
            }

            var notes = new List<GpoCleanupNote>();
            IReadOnlyList<GpoCleanupAction> actions = [GpoCleanupAction.RemoveSetting];
            IReadOnlyList<string> blocking = [];
            if (covered.Unit.Refusal is not null)
            {
                notes.Add(GpoCleanupNote.NonCopyableUnits);
                actions = [];
                blocking = [covered.Unit.Id];
            }

            if (accountPolicy)
            {
                notes.Add(GpoCleanupNote.AccountPolicy);
            }

            if (covered.Unit.RemovePolicy)
            {
                notes.Add(GpoCleanupNote.RemovePolicy);
            }

            proposals.Add(new GpoCleanupProposal
            {
                Kind = GpoCleanupKind.DuplicateSetting,
                GpoId = covered.Entry.Gpo.Id,
                RelatedGpoIds = keepers,
                UnitBaseId = baseId,
                Actions = actions,
                Notes = [.. notes.Order()],
                BlockingUnitIds = blocking,
            });
        }
    }

    /// <summary>The occurrences grouped by value (<see cref="SameValue"/>).</summary>
    private static List<List<Occurrence>> ValueClasses(IEnumerable<Occurrence> occurrences)
    {
        var classes = new List<List<Occurrence>>();
        foreach (var occurrence in occurrences)
        {
            if (classes.FirstOrDefault(c => SameValue(c[0].Unit, occurrence.Unit)) is { } match)
            {
                match.Add(occurrence);
            }
            else
            {
                classes.Add([occurrence]);
            }
        }

        return classes;
    }

    /// <summary>
    /// Same setting, same value – the rule of <see cref="GpoSettingUnits.SameValue"/> (21.2): false unless both are
    /// comparable and neither is disabled; policies compare by <see cref="AdministrativeTemplates.PolicySetting"/>
    /// equality, the others by <see cref="GpoSettingUnit.ValueFingerprint"/>. Kept here so the analyzer is a pure function
    /// of the unit records (it does not depend on how the units were built).
    /// </summary>
    private static bool SameValue(GpoSettingUnit a, GpoSettingUnit b)
    {
        if (a.Comparability != GpoUnitComparability.Comparable || b.Comparability != GpoUnitComparability.Comparable || a.Disabled || b.Disabled)
        {
            return false;
        }

        if (a.Policy is not null || b.Policy is not null)
        {
            return a.Policy is not null && a.Policy.Equals(b.Policy);
        }

        return string.Equals(a.ValueFingerprint, b.ValueFingerprint, StringComparison.Ordinal);
    }

    /// <summary>A password, lockout or Kerberos policy unit (domain effect only from GPOs linked to the domain head).</summary>
    private static bool IsAccountPolicy(string baseId) =>
        baseId.StartsWith(KerberosKey, StringComparison.OrdinalIgnoreCase)
        || (baseId.StartsWith(SystemAccessKey, StringComparison.OrdinalIgnoreCase)
            && GroupPolicyModeler.AccountPolicyNames.Contains(baseId[SystemAccessKey.Length..]));

    private static StringComparer NameOrder { get; } = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);

    private readonly record struct Occurrence(GpoIndexEntry Entry, GpoSettingUnit Unit);
}
