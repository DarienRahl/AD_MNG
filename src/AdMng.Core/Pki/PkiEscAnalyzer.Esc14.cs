using System.Globalization;
using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.Pki;

public static partial class PkiEscAnalyzer
{
    /// <summary>The writes of ESC14 (b) on a Tier 0 account (full control of a Tier 0 object is already U4).</summary>
    private static readonly AclRight[] Esc14Rights = [AclRight.WriteAltSecurityIdentities, AclRight.WriteAllProperties];

    /// <summary>
    /// ESC14 (<see cref="PkiFindingCodes.WeakExplicitMapping"/>), never lowered by approval:
    /// (a) a Tier 0 account with a weak <c>altSecurityIdentities</c> mapping (issuer + subject, subject only, RFC 822) → High,
    /// one finding per account – arguments: the account's name, the weak mapping kinds (<see cref="AltSecurityIdentityKind"/>
    /// names), an empty principal list; weak mappings on other accounts → one aggregated Low finding – arguments: the number
    /// of accounts, the weak kinds found, empty (the readiness report lists them);
    /// (b) a broad or other trustee may write <c>altSecurityIdentities</c> (or every property) of a Tier 0 account
    /// (<see cref="PkiAclTargetKind.Tier0Account"/> grants) → broad Critical, other Medium, one finding per account – arguments:
    /// the account's name, the rights (<see cref="AclRight"/> names), the principals. Part (b) needs the PKI ACL pass and is
    /// skipped when <see cref="PkiRuleGroup.AccessControl"/> was not read.
    /// </summary>
    private static void Esc14(Run run)
    {
        var accounts = run.Snapshot.MappedAccounts;
        foreach (var account in accounts.Where(a => a.IsTier0))
        {
            var weak = WeakKinds(account);
            if (weak.Count > 0)
            {
                run.Add(new PkiFinding(PkiFindingCodes.WeakExplicitMapping, PkiFindingSeverity.High, PkiFindingArea.Account, null,
                    [account.Name, Join(weak.Select(k => k.ToString())), string.Empty])
                {
                    ObjectDn = account.Dn,
                });
            }
        }

        var others = accounts.Where(a => !a.IsTier0).Select(a => (Account: a, Weak: WeakKinds(a))).Where(a => a.Weak.Count > 0).ToList();
        if (others.Count > 0)
        {
            var kinds = others.SelectMany(a => a.Weak).Distinct().Order();
            run.Add(new PkiFinding(PkiFindingCodes.WeakExplicitMapping, PkiFindingSeverity.Low, PkiFindingArea.Account, null,
                [others.Count.ToString(CultureInfo.InvariantCulture), Join(kinds.Select(k => k.ToString())), string.Empty]));
        }

        if (run.Skips(PkiRuleGroup.AccessControl))
        {
            return;
        }

        var writes = run.Snapshot.AclGrants
            .Where(g => g.TargetKind == PkiAclTargetKind.Tier0Account && Esc14Rights.Contains(g.Right) && run.ClassOf(g.TrusteeSid) != PrincipalClass.Tier0);
        foreach (var target in writes.GroupBy(g => Key(g.TargetDn)))
        {
            var first = target.First();
            var shown = target.DistinctBy(g => (g.TrusteeSid.ToUpperInvariant(), g.Right)).OrderBy(g => Array.IndexOf(Esc14Rights, g.Right)).ToList();
            var principals = shown.Select(g => run.GrantPrincipal(g.TrusteeSid, g.Right.ToString())).ToList();
            var severity = principals.Aggregate(PkiFindingSeverity.Info,
                (max, p) => Max(max, ClassSeverity(p.Class, PkiFindingSeverity.Critical, PkiFindingSeverity.Medium)));
            var name = accounts.FirstOrDefault(a => Key(a.Dn) == target.Key)?.Name ?? LeafName(first.TargetDn);
            run.Add(new PkiFinding(PkiFindingCodes.WeakExplicitMapping, severity, PkiFindingArea.Account, null,
                [
                    name,
                    Join(shown.Select(g => g.Right).Distinct().Select(r => r.ToString())),
                    Join(principals.Select(p => p.Name ?? p.Sid).Distinct(StringComparer.OrdinalIgnoreCase)),
                ])
            {
                ObjectDn = first.TargetDn,
                Principals = principals,
            });
        }
    }

    /// <summary>The weak mapping kinds of an account, in enum order.</summary>
    private static List<AltSecurityIdentityKind> WeakKinds(PkiAccountFact account) =>
        [.. account.Mappings.Where(m => m.Strength == MappingStrength.Weak).Select(m => m.Kind).Distinct().Order()];
}
