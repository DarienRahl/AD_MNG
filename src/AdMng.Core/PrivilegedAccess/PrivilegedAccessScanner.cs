using System.Runtime.CompilerServices;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Filters;
using AdMng.Core.Ldap;
using AdMng.Core.Model;

namespace AdMng.Core.PrivilegedAccess;

/// <summary>
/// The queries of a privileged access run (README U4, design docs/dev/u4-privileged-access.md section 6), all on the
/// session's DC: the security groups (one paged search, the <see cref="GroupGraph"/>), the domain controllers' names, one
/// DN-only search per filter rule, the grant sources, then one paged search of the accounts of the scope, streamed and
/// evaluated as it arrives. Nothing is written anywhere; the results live as long as the caller keeps them.
/// </summary>
public sealed class PrivilegedAccessScanner
{
    private readonly IDirectorySession _session;
    private readonly PrivilegedAccessOptions _options;
    private readonly IReadOnlyList<IPrivilegeGrantSource> _sources;
    private readonly List<string> _skippedRules = [];

    /// <param name="session">The session (CLAUDE.md rule 4: every read on its DC).</param>
    /// <param name="options">Domain SIDs, levels, marks, rules, peers.</param>
    /// <param name="sources">
    /// More grant sources (U4b, U4c); the catalogue groups and the operator's marks are always collected.
    /// </param>
    public PrivilegedAccessScanner(IDirectorySession session, PrivilegedAccessOptions options, IEnumerable<IPrivilegeGrantSource>? sources = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sources = [new BuiltInGroupGrantSource(), new MarkedPrincipalGrantSource(), .. sources ?? []];
    }

    /// <summary>User and computer accounts (managed service accounts are machine accounts).</summary>
    public static string AccountFilter { get; } = LdapFilter.Or(ObjectKind.User.ToLdapFilter(), ObjectKind.Computer.ToLdapFilter());

    /// <summary>Domain controllers: SERVER_TRUST_ACCOUNT or PARTIAL_SECRETS_ACCOUNT (RODC).</summary>
    public static string DomainControllerFilter { get; } = LdapFilter.And(
        ObjectKind.Computer.ToLdapFilter(),
        LdapFilter.Or(
            LdapFilter.BitAnd(Attr.UserAccountControl, (long)UserAccountControl.ServerTrustAccount),
            LdapFilter.BitAnd(Attr.UserAccountControl, (long)UserAccountControl.PartialSecretsAccount)));

    /// <summary>Names of the rules left out because their filter is not valid (after <see cref="PrepareAsync"/>).</summary>
    public IReadOnlyList<string> SkippedRules => _skippedRules;

    /// <summary>Reads everything the evaluation of the accounts needs.</summary>
    public async Task<PrivilegeEvaluator> PrepareAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var nc = _session.Info.DefaultNamingContext;
        var filterContext = new FilterContext { Now = now, DefaultNamingContext = nc, Schema = _session.Schema };
        _skippedRules.Clear();
        var rules = new List<(PrivilegeConditionRule Rule, FilterPlan Plan)>();
        foreach (var rule in _options.Rules)
        {
            try
            {
                rules.Add((rule, FilterCompiler.Plan(rule.Filter, filterContext)));
            }
            catch (FilterDefinitionException)
            {
                _skippedRules.Add(rule.Name);
            }
        }

        var groupRules = rules.Where(r => r.Rule.Target == PrivilegeRuleTarget.Groups).ToList();
        var accountRules = rules.Where(r => r.Rule.Target == PrivilegeRuleTarget.Accounts).ToList();
        var graph = await GroupGraph.ReadAsync(_session, groupRules.SelectMany(r => r.Plan.Attributes), _options.PageSize, cancellationToken).ConfigureAwait(false);
        var context = new PrivilegeScanContext { Session = _session, Groups = graph, Options = _options, Now = now };
        var grants = new List<PrivilegeGrant>();
        foreach (var source in _sources)
        {
            grants.AddRange(await source.CollectAsync(context, cancellationToken).ConfigureAwait(false));
        }

        foreach (var (rule, plan) in groupRules)
        {
            var candidates = plan.ServerFilter is { } server
                ? await DnKeysAsync(LdapFilter.And(GroupGraph.Filter, server), cancellationToken).ConfigureAwait(false)
                : null;
            foreach (var node in graph.Nodes.Where(n => !n.IsImplicit && (candidates is null || candidates.Contains(GroupGraph.Key(n.Dn)))))
            {
                if (plan.NeedsClientCheck)
                {
                    var self = new PrivilegePathStep(node.Dn, node.Name, PrivilegePathStepKind.Account);
                    var parents = graph.Reach(self, node.Object.GetStrings(Attr.MemberOf), null, includeImplicit: false);
                    if (!FilterEvaluator.Matches(rule.Filter, node.Object, filterContext, dn => graph.FindByDn(dn) is { } g && parents.Contains(g)))
                    {
                        continue;
                    }
                }

                grants.Add(new PrivilegeGrant
                {
                    Source = PrivilegeSource.OperatorRules,
                    Kind = PrivilegeFindingKind.ConditionGroup,
                    Level = PrivilegeLevels.Normalize(rule.Level),
                    Scope = PrivilegeScopeKind.Domain,
                    PrincipalSid = node.Sid,
                    PrincipalDn = node.Dn,
                    Note = rule.Note is { Length: > 0 } note ? $"{rule.Name} – {note}" : rule.Name,
                });
            }
        }

        var matchers = new List<AccountRuleMatcher>();
        foreach (var (rule, plan) in accountRules)
        {
            var candidates = plan.ServerFilter is { } server
                ? await DnKeysAsync(LdapFilter.And(AccountFilter, server), cancellationToken).ConfigureAwait(false)
                : null;
            matchers.Add(new AccountRuleMatcher(rule, plan, candidates));
        }

        var controllers = new List<string>();
        await foreach (var dc in _session.SearchAsync(new DirectorySearch
        {
            BaseDn = nc,
            Scope = SearchScope.Subtree,
            Filter = DomainControllerFilter,
            Attributes = [Attr.DnsHostName, Attr.SamAccountName],
            PageSize = _options.PageSize,
        }, cancellationToken).ConfigureAwait(false))
        {
            if (dc.GetString(Attr.DnsHostName) is { Length: > 0 } host)
            {
                controllers.Add(host);
            }

            if (dc.GetString(Attr.SamAccountName) is { Length: > 0 } sam)
            {
                controllers.Add(sam.TrimEnd('$'));
            }
        }

        return new PrivilegeEvaluator(graph, grants, _options, filterContext, controllers, matchers);
    }

    /// <summary>
    /// Streams the accounts of the scope, evaluated (also those without findings - the peer comparison needs them).
    /// </summary>
    /// <param name="evaluator">From <see cref="PrepareAsync"/>.</param>
    /// <param name="baseDn">Base of the scope.</param>
    /// <param name="scope">Search scope.</param>
    /// <param name="scopeFilter">Filter of the scope (saved view, group members, saved filter), null for none.</param>
    /// <param name="extraAttributes">More attributes to read (client checks of the scope's saved filters), null for none.</param>
    /// <param name="cancellationToken">Stops the search (abandons the paging).</param>
    public async IAsyncEnumerable<AccountPrivileges> ScanAsync(
        PrivilegeEvaluator evaluator,
        string baseDn,
        SearchScope scope,
        string? scopeFilter,
        IReadOnlyList<string>? extraAttributes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDn);
        var search = new DirectorySearch
        {
            BaseDn = baseDn,
            Scope = scope,
            Filter = LdapFilter.And(AccountFilter, scopeFilter),
            Attributes = [.. evaluator.AccountAttributes.Concat(extraAttributes ?? []).Distinct(StringComparer.OrdinalIgnoreCase)],
            PageSize = _options.PageSize,
        };
        await foreach (var obj in _session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            var account = await CompleteMemberOfAsync(obj, cancellationToken).ConfigureAwait(false);
            if (evaluator.Evaluate(account) is { } result)
            {
                yield return result;
            }
        }
    }

    /// <summary>The peer findings of the accounts (users only; computers and service accounts have no peers).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<PrivilegeFinding>> ComparePeers(IEnumerable<AccountPrivileges> accounts, PrivilegeEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(evaluator);
        if (!evaluator.Options.Peers.IsEnabled)
        {
            return new Dictionary<string, IReadOnlyList<PrivilegeFinding>>();
        }

        var subjects = accounts.Where(a => a.Type == PrivilegedAccountType.User).Select(a => a.ToPeerSubject()).ToList();
        return PeerComparison.Compare(subjects, evaluator.Options.Peers, evaluator.GrantedLevel, evaluator.GroupName);
    }

    /// <summary>The key of an account in <see cref="ComparePeers"/>.</summary>
    public static string KeyOf(string distinguishedName) => GroupGraph.Key(distinguishedName);

    /// <summary>memberOf with more than 1500 values comes back ranged: the rest is read (rare; one read per such account).</summary>
    private async Task<DirectoryObject> CompleteMemberOfAsync(DirectoryObject obj, CancellationToken cancellationToken)
    {
        if (!obj.TryGet(Attr.MemberOf, out var values)
            || !values.Name.Contains(";range=", StringComparison.OrdinalIgnoreCase)
            || values.Name.EndsWith("-*", StringComparison.Ordinal))
        {
            return obj;
        }

        var all = new List<object>();
        await foreach (var value in _session.GetAllValuesAsync(obj.DistinguishedName, Attr.MemberOf, cancellationToken).ConfigureAwait(false))
        {
            all.Add(value);
        }

        return obj.With([new AttributeValues(Attr.MemberOf, all)]);
    }

    private async Task<HashSet<string>> DnKeysAsync(string filter, CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var obj in _session.SearchAsync(new DirectorySearch
        {
            BaseDn = _session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = filter,
            Attributes = [],
            PageSize = _options.PageSize,
        }, cancellationToken).ConfigureAwait(false))
        {
            keys.Add(GroupGraph.Key(obj.DistinguishedName));
        }

        return keys;
    }
}
