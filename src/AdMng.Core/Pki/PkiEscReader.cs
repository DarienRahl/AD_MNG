using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.Pki;

/// <summary>
/// Builds the <see cref="PkiEscSnapshot"/> (2.4): takes the precomputed expansion (no second account pass), calls
/// <c>DirectoryAclGrantSource.CollectPublicKeyAsync</c> with the snapshot's CA computer DNs, classifies every grant
/// trustee and resolves the OID group links against <see cref="PrivilegeScanContext.Groups"/>. All reads on the session's DC.
/// </summary>
public static class PkiEscReader
{
    /// <summary>SIDs per <c>objectSid</c> OR search when grant trustees are named (as the GPO diagnostics).</summary>
    private const int SidChunk = 50;

    /// <summary>The snapshot notices the review shows (the rest belong to the overview).</summary>
    private static readonly PkiReadNoticeCode[] ReviewNotices =
    [
        PkiReadNoticeCode.BaseNotFound, PkiReadNoticeCode.BaseAccessDenied, PkiReadNoticeCode.SecurityDescriptorUnreadable,
        PkiReadNoticeCode.CaComputerUnresolved, PkiReadNoticeCode.CaComputerInOtherDomain, PkiReadNoticeCode.TrusteeUnresolved,
    ];

    /// <summary>Reads what the analyzer needs (cancellable, with progress).</summary>
    /// <remarks>
    /// The PKI ACL pass is skipped for a snapshot without PKI (base not found or denied); a <see cref="DirectoryException"/>
    /// of the pass marks <see cref="PkiRuleGroup.AccessControl"/> not evaluated instead of failing the review. The trustees of
    /// the grants are classified with one <see cref="PrincipalClassifier"/> (the context's graph and options, the CA computer
    /// SIDs of the snapshot) and named from the graph, then by one chunked <c>objectSid</c> search of the default naming
    /// context (foreign security principals stay unnamed: another domain). An OID group link that is not a security group of
    /// the graph gives no linked group and a <see cref="PkiReadNoticeCode.TrusteeUnresolved"/> notice (ESC13 needs the group);
    /// a linked group's level is the highest catalogue level it grants through its nesting, Tier 0 also when the classifier
    /// puts it there.
    /// </remarks>
    public static async Task<PkiEscSnapshot> ReadAsync(
        PrivilegeScanContext context,
        PkiSnapshot snapshot,
        PkiEnrollmentExpansion expansion,
        PkiEscReadOptions options,
        IProgress<PkiEscProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(expansion);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var notices = new List<PkiReadNotice>(snapshot.Notices.Where(n => ReviewNotices.Contains(n.Code)));
        notices.AddRange(expansion.Notices);
        var notEvaluated = new List<PkiRuleGroup>();
        var hasPki = !snapshot.Notices.Any(n => n.Code is PkiReadNoticeCode.BaseNotFound or PkiReadNoticeCode.BaseAccessDenied);

        progress?.Report(new PkiEscProgress(PkiEscStage.AccessControl));
        IReadOnlyList<PkiAclGrant> grants = [];
        if (hasPki)
        {
            var caComputers = snapshot.EnterpriseCas.Select(ca => ca.ComputerDn).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            try
            {
                grants = await new DirectoryAclGrantSource().CollectPublicKeyAsync(context, caComputers, cancellationToken).ConfigureAwait(false);
            }
            catch (DirectoryException) when (!cancellationToken.IsCancellationRequested)
            {
                notEvaluated.Add(PkiRuleGroup.AccessControl);
            }
        }

        progress?.Report(new PkiEscProgress(PkiEscStage.AccessControl, grants.Count));
        progress?.Report(new PkiEscProgress(PkiEscStage.Principals));
        var domainSid = context.Options.DomainSid;
        var rootDomainSid = context.Options.RootDomainSid;
        var classifier = new PrincipalClassifier(domainSid, rootDomainSid, context.Options, context.Groups,
            [.. snapshot.EnterpriseCas.Select(ca => ca.ComputerSid).OfType<string>()]);
        var trustees = grants.Select(g => g.TrusteeSid).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var classes = new Dictionary<string, PrincipalClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var sid in trustees)
        {
            classes[sid] = classifier.Classify(sid);
        }

        var names = await NameAsync(context, trustees, cancellationToken).ConfigureAwait(false);
        foreach (var grant in grants.Where(g => !names.ContainsKey(g.TrusteeSid) && IsOtherDomain(g.TrusteeSid, domainSid)))
        {
            // A trustee of another forest domain the window cannot name: the finding says "nieznana" for its count.
            notices.Add(new PkiReadNotice(PkiReadNoticeCode.TrusteeUnresolved, grant.TargetDn, null));
        }

        progress?.Report(new PkiEscProgress(PkiEscStage.Principals, trustees.Count));

        var linked = new List<PkiLinkedGroup>();
        foreach (var oid in snapshot.Oids.Where(o => !string.IsNullOrWhiteSpace(o.GroupLinkDn)))
        {
            var group = context.Groups.FindByDn(oid.GroupLinkDn);
            if (group is null || group.IsImplicit)
            {
                notices.Add(new PkiReadNotice(PkiReadNoticeCode.TrusteeUnresolved, oid.Dn, oid.ObjectGuid));
                continue;
            }

            if (!linked.Any(l => DistinguishedName.AreEqual(l.Dn, group.Dn)))
            {
                linked.Add(new PkiLinkedGroup(group.Dn, group.Name, LevelOf(context, classifier, group)));
            }
        }

        var rights = snapshot.Templates
            .Select(t => expansion.Templates.TryGetValue(t.Cn, out var r) ? r : null)
            .OfType<TemplateRights>()
            .ToList();
        progress?.Report(new PkiEscProgress(PkiEscStage.Completed, snapshot.Templates.Count));
        return new PkiEscSnapshot(
            snapshot.Templates,
            rights,
            snapshot.EnterpriseCas,
            snapshot.Oids,
            linked,
            grants,
            classes,
            expansion.MappedAccounts,
            options.CaGrantsAuthenticatedUsersEnroll)
        {
            Notices = [.. notices.DistinctBy(n => (n.Code, n.ObjectGuid, n.ObjectDn?.ToUpperInvariant()))],
            NotEvaluated = notEvaluated,
            TrusteeNames = names,
        };
    }

    /// <summary>A SID of a domain (<c>S-1-5-21-…</c>) other than the window's.</summary>
    private static bool IsOtherDomain(string sid, string? domainSid) =>
        sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
        && (domainSid is null || !sid.StartsWith(domainSid + "-", StringComparison.OrdinalIgnoreCase));

    /// <summary>The level membership of <paramref name="group"/> grants (catalogue levels through its nesting; Tier 0 by class).</summary>
    private static PrivilegeLevel LevelOf(PrivilegeScanContext context, PrincipalClassifier classifier, GroupNode group)
    {
        var options = context.Options;
        var granted = context.Groups.GrantedLevel(group, node =>
            PrivilegedGroupCatalog.Match(node.Sid, node.SamAccountName, options.DomainSid, options.RootDomainSid) is { } definition
                ? options.LevelOf(definition)
                : PrivilegeLevel.None);
        return group.Sid is { } sid && classifier.Classify(sid) == PrincipalClass.Tier0 ? PrivilegeLevel.Tier0 : granted;
    }

    /// <summary>The names of the trustees: the graph first, then one chunked <c>objectSid</c> search (rule 3: <see cref="LdapFilter"/>).</summary>
    private static async Task<IReadOnlyDictionary<string, string>> NameAsync(PrivilegeScanContext context, IReadOnlyList<string> sids, CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lookup = new List<string>();
        foreach (var sid in sids)
        {
            if (context.Groups.FindBySid(sid) is { IsImplicit: false } node)
            {
                names[sid] = node.Name;
            }
            else if (sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
            {
                lookup.Add(sid);
            }
        }

        var session = context.Session;
        try
        {
            foreach (var chunk in lookup.Order(StringComparer.OrdinalIgnoreCase).Chunk(SidChunk))
            {
                await foreach (var found in session.SearchAsync(new DirectorySearch
                {
                    BaseDn = session.Info.DefaultNamingContext,
                    Scope = SearchScope.Subtree,
                    Filter = LdapFilter.Or(chunk.Select(sid => LdapFilter.Equal(Attr.ObjectSid, sid))),
                    Attributes = [Attr.ObjectSid, Attr.SamAccountName, Attr.Name, Attr.ObjectClass],
                    PageSize = SidChunk,
                }, cancellationToken).ConfigureAwait(false))
                {
                    if (found.ObjectSid is { } sid && found.Kind != ObjectKind.ForeignSecurityPrincipal)
                    {
                        names.TryAdd(sid, found.Name);
                    }
                }
            }
        }
        catch (DirectoryException) when (!cancellationToken.IsCancellationRequested)
        {
            // Names are a convenience: the SIDs stay (unresolved) and the review goes on.
        }

        return names;
    }
}
