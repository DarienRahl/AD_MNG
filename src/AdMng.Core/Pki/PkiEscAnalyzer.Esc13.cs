using AdMng.Core.Ldap;
using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.Pki;

public static partial class PkiEscAnalyzer
{
    /// <summary>
    /// ESC13 (<see cref="PkiFindingCodes.IssuancePolicyLinkedToGroup"/>): a value of <c>msPKI-Certificate-Policy</c> equals the
    /// <c>msPKI-Cert-Template-OID</c> of an OID object (joined by the OID string, never by DN) whose <c>msDS-OIDToGroupLink</c>
    /// resolved to a group of the graph (<see cref="PkiEscSnapshot.LinkedGroups"/>), client authentication, enrollable; approval
    /// lowers it. Critical when a linked group is Tier 0, else High – whoever enrolls gets the group's rights, whatever their
    /// own class. The fourth argument is the linked groups; the OID objects are related.
    /// </summary>
    private static void Esc13(Run run)
    {
        var groups = run.Snapshot.LinkedGroups;
        var links = new Dictionary<string, List<(PkiOidObject Oid, PkiLinkedGroup Group)>>(StringComparer.Ordinal);
        foreach (var oid in run.Snapshot.Oids.Where(o => o.GroupLinkDn is not null && !string.IsNullOrWhiteSpace(o.Oid)))
        {
            var group = groups.FirstOrDefault(g => DistinguishedName.AreEqual(g.Dn, oid.GroupLinkDn));
            if (group is null)
            {
                continue;
            }

            if (!links.TryGetValue(oid.Oid.Trim(), out var list))
            {
                links[oid.Oid.Trim()] = list = [];
            }

            list.Add((oid, group));
        }

        if (links.Count == 0)
        {
            return;
        }

        foreach (var facts in run.Evaluated.Where(t => t.IsEnrollable))
        {
            var linked = facts.Template.IssuancePolicies
                .SelectMany(policy => links.TryGetValue(policy.Trim(), out var list) ? list : [])
                .ToList();
            if (linked.Count == 0)
            {
                continue;
            }

            var (holds, differs) = OnEitherEku(facts.Template, PkiFlags.IsClientAuth);
            if (!holds)
            {
                continue;
            }

            var severity = linked.Any(l => l.Group.Level == PrivilegeLevel.Tier0) ? PkiFindingSeverity.Critical : PkiFindingSeverity.High;
            var names = linked.Select(l => l.Group.Name ?? LeafName(l.Group.Dn)).Distinct(StringComparer.OrdinalIgnoreCase);
            run.AddEnrollment(facts, PkiFindingCodes.IssuancePolicyLinkedToGroup, severity,
                differs ? PkiFindingNotes.EkuSourcesDiffer : PkiFindingNotes.None,
                linked.Select(l => l.Oid.ObjectGuid),
                [Join(names)]);
        }
    }
}
