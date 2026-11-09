using AdMng.Core.Ldap;
using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.Pki;

public static partial class PkiEscAnalyzer
{
    /// <summary>
    /// The rights of ESC5: control of the object (full control, DACL, owner, every property, child objects) or a write of a
    /// named attribute; on a CA computer also the takeover rights of U4 (reset password, read the LAPS password, every
    /// extended right, RBCD, shadow credentials, explicit mappings).
    /// </summary>
    private static readonly AclRight[] Esc5Rights =
    [
        AclRight.GenericAll, AclRight.Owner, AclRight.WriteDacl, AclRight.WriteOwner, AclRight.WriteAllProperties, AclRight.CreateChild,
        AclRight.WriteMember, AclRight.AddSelfToGroup, AclRight.WriteGpLink, AclRight.WriteGpoProperty, AclRight.WriteTemplateProperty,
        AclRight.WriteKeyCredentialLink, AclRight.WriteAllowedToActOnBehalf, AclRight.WriteAltSecurityIdentities,
        AclRight.ResetPassword, AclRight.ReadLapsPassword, AclRight.AllExtendedRights, AclRight.ActOnBehalfOfOtherIdentity,
    ];

    /// <summary>The takeover rights that count only on a CA computer (elsewhere in the PKI subtree they control nothing).</summary>
    private static readonly AclRight[] ComputerOnlyRights = [AclRight.AllExtendedRights];

    /// <summary>
    /// ESC5 (<see cref="PkiFindingCodes.PkiObjectWriteable"/>): a broad or other trustee holds a right of <see cref="Esc5Rights"/>
    /// on a PKI container, NTAuth, an OID object, an enrollment service, a CA / AIA / CDP object or a CA computer
    /// (<see cref="PkiEscSnapshot.AclGrants"/> – the decision-4 safe set and the decision-6 provisioning trustees are already
    /// left out by <c>CollectPublicKeyAsync</c>; templates are ESC4, Tier 0 accounts ESC14). One finding per object; broad →
    /// Critical, other → Medium; approval never protects. A trustee the reader could not classify counts as Other.
    /// </summary>
    private static void Esc5(Run run)
    {
        var grants = run.Snapshot.AclGrants
            .Where(g => g.TargetKind is not (PkiAclTargetKind.Template or PkiAclTargetKind.Tier0Account)
                && Esc5Rights.Contains(g.Right)
                && (g.TargetKind == PkiAclTargetKind.CaComputer || !ComputerOnlyRights.Contains(g.Right))
                && run.ClassOf(g.TrusteeSid) != PrincipalClass.Tier0);
        foreach (var target in grants.GroupBy(g => Key(g.TargetDn)))
        {
            var first = target.First();
            var shown = target
                .DistinctBy(g => (g.TrusteeSid.ToUpperInvariant(), g.Right))
                .OrderBy(g => Array.IndexOf(Esc5Rights, g.Right))
                .ToList();
            var principals = shown.Select(g => run.GrantPrincipal(g.TrusteeSid, g.Right.ToString())).ToList();
            var severity = principals.Aggregate(PkiFindingSeverity.Info,
                (max, p) => Max(max, ClassSeverity(p.Class, PkiFindingSeverity.Critical, PkiFindingSeverity.Medium)));
            var (objectId, name, related) = Describe(run.Snapshot, first);
            run.Add(new PkiFinding(
                PkiFindingCodes.PkiObjectWriteable,
                severity,
                PkiFindingArea.PkiObject,
                objectId,
                [
                    name,
                    Join(principals.Select(p => p.Name ?? p.Sid).Distinct(StringComparer.OrdinalIgnoreCase)),
                    Join(shown.Select(g => g.Right).Distinct().Select(r => r.ToString())),
                ])
            {
                ObjectDn = first.TargetDn,
                Principals = principals,
                RelatedObjectIds = related,
            });
        }
    }

    /// <summary>The comparable key of a DN (parsed, upper case; the text when it does not parse).</summary>
    private static string Key(string dn) => (DistinguishedName.TryParse(dn, out var parsed) ? parsed.ToString() : dn).ToUpperInvariant();

    /// <summary>The object GUID the snapshot knows for the target, its name and the related CAs.</summary>
    private static (Guid? ObjectId, string Name, IReadOnlyList<Guid> Related) Describe(PkiEscSnapshot snapshot, PkiAclGrant grant)
    {
        var key = Key(grant.TargetDn);
        if (snapshot.EnterpriseCas.FirstOrDefault(ca => Key(ca.Dn) == key) is { } service)
        {
            return (service.ObjectGuid, string.IsNullOrWhiteSpace(service.DisplayName) ? service.Name : service.DisplayName, []);
        }

        if (snapshot.Oids.FirstOrDefault(o => Key(o.Dn) == key) is { } oid)
        {
            return (oid.ObjectGuid, string.IsNullOrWhiteSpace(oid.DisplayName) ? LeafName(oid.Dn) : oid.DisplayName, []);
        }

        if (grant.TargetKind == PkiAclTargetKind.CaComputer)
        {
            var cas = snapshot.EnterpriseCas.Where(ca => ca.ComputerDn is { } computer && Key(computer) == key).Select(ca => ca.ObjectGuid).ToList();
            return (null, LeafName(grant.TargetDn), cas);
        }

        return (null, LeafName(grant.TargetDn), []);
    }
}
