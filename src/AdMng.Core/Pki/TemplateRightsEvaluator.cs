using System.Buffers.Binary;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Model;
using AdMng.Core.PrivilegedAccess;
using AdMng.Core.Security;

namespace AdMng.Core.Pki;

/// <summary>
/// "Kto może się zarejestrować / zmienić" (decision 5, docs/dev/p-certificate-services.md 2.3) – a separate pure Core
/// evaluator, not U4b: AccessCheck semantics over a template DACL, each right decided by the first ACE in DACL order that
/// applies to the token and covers that right (deny ACEs honoured), then one streamed account pass per window.
/// </summary>
public static class TemplateRightsEvaluator
{
    /// <summary>OWNER RIGHTS: its ACEs replace the owner's implicit READ_CONTROL | WRITE_DAC and apply to the owner.</summary>
    internal const string OwnerRightsSid = "S-1-3-4";

    private const string EveryoneSid = "S-1-1-0";
    private const string AuthenticatedUsersSid = "S-1-5-11";
    private const string ThisOrganizationSid = "S-1-5-15";
    private const string NetworkSid = "S-1-5-2";
    private const string EnterpriseDomainControllersSid = "S-1-5-9";
    private const ushort DaclPresent = 0x0004;

    /// <summary>
    /// Placeholders no token of an access check on a template contains (CREATOR OWNER / GROUP and their server forms,
    /// PRINCIPAL SELF): never listed as trustees.
    /// </summary>
    private static readonly HashSet<string> NeverInToken = new(StringComparer.OrdinalIgnoreCase) { "S-1-3-0", "S-1-3-1", "S-1-3-2", "S-1-3-3", "S-1-5-10" };

    private static readonly TemplateRight[] AllRights = Enum.GetValues<TemplateRight>();

    private static readonly TemplateAcl NotEvaluated = new(null, [], false);

    /// <summary>
    /// Decodes a template's security descriptor into atomic rights in DACL order (inherit-only ACEs and ACEs inherited for
    /// another class skipped, the mask normalized); unparseable → <see cref="TemplateAcl.IsEvaluated"/> false.
    /// </summary>
    /// <remarks>
    /// Object types are honoured as the DC's access check does: an object type equal to the template class covers the whole
    /// object; a write on an attribute or property set covering one of <see cref="PkiAttributes.SensitiveTemplateAttributes"/>
    /// is <see cref="TemplateRight.WriteSensitiveProperty"/> (one entry per covered attribute), on another named attribute
    /// or property set <see cref="TemplateRight.WriteOtherProperty"/>, on a GUID nothing can name sensitive (with the GUID
    /// as the attribute – no ESC4 false negative). Allow ACEs count their WRITE_DAC / WRITE_OWNER bits whatever the object
    /// type, deny ACEs only without one (an object-typed deny does not remove them – conservative both ways). A deny ACE
    /// that removes any part of full control denies <see cref="TemplateRight.FullControl"/>. A descriptor without a DACL
    /// (not read, or a NULL DACL) is not evaluated, never "nobody".
    /// </remarks>
    public static TemplateAcl Evaluate(ReadOnlySpan<byte> securityDescriptor, AclObjectTypes types)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (securityDescriptor.Length < 20
            || (BinaryPrimitives.ReadUInt16LittleEndian(securityDescriptor[2..]) & DaclPresent) == 0
            || BinaryPrimitives.ReadInt32LittleEndian(securityDescriptor[16..]) == 0
            || !SecurityDescriptorSummary.TryParse(securityDescriptor, out var summary)
            || summary is null)
        {
            return NotEvaluated;
        }

        var aces = new List<TemplateAce>();
        var ownerRights = false;
        foreach (var ace in summary.Aces)
        {
            var deny = ace.IsDeny;
            if ((!ace.IsAllow && !deny) || ace.IsInheritOnly || !AppliesToTemplates(ace, types))
            {
                continue;
            }

            if (deny && ace.Type is 0x0A or 0x0C)
            {
                // A callback (conditional) deny ACE: its condition cannot be evaluated here – it may not apply.
                continue;
            }

            ownerRights |= string.Equals(ace.Sid, OwnerRightsSid, StringComparison.OrdinalIgnoreCase);
            Expand(ace, deny, types, aces);
        }

        return new TemplateAcl(summary.Owner, aces, true) { HasOwnerRights = ownerRights };
    }

    /// <summary>
    /// AccessCheck for one right: the first ACE in DACL order whose SID is in <paramref name="tokenSids"/> and whose right is
    /// <paramref name="right"/> decides (deny → false, allow → true); no such ACE → false.
    /// </summary>
    /// <remarks>
    /// A token that holds the owner SID also holds OWNER RIGHTS (S-1-3-4). <see cref="TemplateRight.Owner"/> is held by the
    /// owner unless <see cref="TemplateAcl.HasOwnerRights"/>. The two attribute rights are decided per attribute: the token
    /// holds <see cref="TemplateRight.WriteSensitiveProperty"/> when an allow of that right names an attribute whose first
    /// covering ACE (that right on that attribute, or a write of all properties) is an allow.
    /// </remarks>
    public static bool Decide(IReadOnlySet<string> tokenSids, TemplateAcl acl, TemplateRight right)
    {
        ArgumentNullException.ThrowIfNull(tokenSids);
        ArgumentNullException.ThrowIfNull(acl);
        if (!acl.IsEvaluated)
        {
            return false;
        }

        var token = new Token(tokenSids, acl);
        switch (right)
        {
            case TemplateRight.Owner:
                return token.IsOwner && !acl.HasOwnerRights;
            case TemplateRight.WriteSensitiveProperty or TemplateRight.WriteOtherProperty:
                foreach (var ace in acl.Aces)
                {
                    if (!ace.IsDeny && ace.Right == right && ace.Attribute is { } attribute && token.Contains(ace.Sid) && CanWrite(token, acl, attribute))
                    {
                        return true;
                    }
                }

                return false;
            default:
                foreach (var ace in acl.Aces)
                {
                    if (ace.Right == right && token.Contains(ace.Sid))
                    {
                        return !ace.IsDeny;
                    }
                }

                return false;
        }
    }

    /// <summary>The trustee level, immediate and synchronous: per template and right the trustees that hold it (counts -1).</summary>
    public static IReadOnlyList<TemplateRights> TrusteeLevel(IReadOnlyList<CertificateTemplate> templates, AclObjectTypes types, PrincipalClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(classifier);
        var result = new List<TemplateRights>(templates.Count);
        foreach (var template in templates)
        {
            var acl = AclOf(template, types);
            IReadOnlyList<TemplateTrustee> trustees = acl.IsEvaluated
                ? [.. Rows(acl).Select(r => new TemplateTrustee(
                    r.Sid, classifier.NameOf(r.Sid), classifier.Classify(r.Sid), r.Right, -1, -1, [], !classifier.IsOtherDomain(r.Sid), r.Attribute))]
                : [];
            result.Add(new TemplateRights(template.Cn, template.ObjectGuid, acl.IsEvaluated, trustees, []));
        }

        return result;
    }

    /// <summary>
    /// The one account pass (<c>PrivilegedAccessScanner.AccountFilter</c>, <see cref="PkiAttributes.AccountAttributes"/>):
    /// tokens through the graph, decisions memoised per distinct key, counts + the first accounts per right, the mapped
    /// accounts; cancellable, progress every 500 accounts.
    /// </summary>
    /// <remarks>
    /// A token is the account SID, its primary group, every group <see cref="GroupGraph.Reach"/> finds (implicit ones
    /// included), Everyone, Authenticated Users, This Organization and NETWORK (the CA and the DC see a network logon),
    /// plus Enterprise Domain Controllers for a writable DC. The key of the memo is the token ∩ the SIDs the ACEs name (the
    /// owners included). Trustees of another domain keep the count -1 (<see cref="TemplateTrustee.IsResolved"/> false);
    /// one <see cref="PkiReadNoticeCode.TrusteeUnresolved"/> notice per template where such a trustee is not Tier 0. All
    /// reads go to <paramref name="session"/> (rule 4); nothing is kept beyond the result (rule 1).
    /// </remarks>
    public static async Task<PkiEnrollmentExpansion> ExpandAsync(
        IDirectorySession session,
        IReadOnlyList<CertificateTemplate> templates,
        PkiExpansionContext context,
        IProgress<PkiExpansionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(context);
        var graph = context.Groups;
        var given = context.Classifier;
        var classifier = ReferenceEquals(given.Groups, graph)
            ? given
            : new PrincipalClassifier(given.DomainSid, given.RootDomainSid, given.Options, graph, given.CaComputerSids) { Tier0Accounts = given.Tier0Accounts };
        var limit = Math.Max(0, context.FirstAccounts);

        // The templates: decoded once, their trustee rows, the SIDs their ACEs name.
        var plans = new List<TemplatePlan>(templates.Count);
        var named = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var namedList = new List<string>();
        void Name(string sid)
        {
            if (named.TryAdd(sid, namedList.Count))
            {
                namedList.Add(sid);
            }
        }

        foreach (var template in templates)
        {
            var acl = AclOf(template, context.Types);
            var rows = acl.IsEvaluated ? Rows(acl) : [];
            plans.Add(new TemplatePlan(template, acl, rows));
            foreach (var ace in acl.Aces)
            {
                Name(ace.Sid);
            }

            if (acl.Owner is { } owner)
            {
                Name(owner);
            }
        }

        var totals = new Slot[plans.Count * AllRights.Length];
        var rowSlots = new List<Slot[]>(plans.Count);
        for (var i = 0; i < totals.Length; i++)
        {
            totals[i] = new Slot(limit);
        }

        foreach (var plan in plans)
        {
            rowSlots.Add([.. plan.Rows.Select(_ => new Slot(limit))]);
        }

        // The one account pass.
        var memo = new Dictionary<int[], KeyEntry>(KeyComparer.Instance);
        var classes = new Dictionary<string, PrincipalClass>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tier0Accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapped = new List<PkiAccountFact>();
        var scanned = 0;
        var token = new List<string>(32);
        var key = new List<int>(16);
        var search = new DirectorySearch
        {
            BaseDn = session.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = PrivilegedAccessScanner.AccountFilter,
            Attributes = PkiAttributes.AccountAttributes,
            PageSize = context.Options.PageSize,
        };
        await foreach (var found in session.SearchAsync(search, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = await PrivilegedAccessScanner.CompleteMemberOfAsync(session, found, cancellationToken).ConfigureAwait(false);
            scanned++;
            if (account.ObjectSid is { } sid)
            {
                var uac = (UserAccountControl)(account.GetInt32(Attr.UserAccountControl) ?? 0);
                var disabled = (uac & UserAccountControl.AccountDisable) != 0;
                BuildToken(account, sid, (uac & UserAccountControl.ServerTrustAccount) != 0, graph, token);
                var isTier0 = classifier.Classify(sid) == PrincipalClass.Tier0;
                key.Clear();
                for (var i = 0; i < token.Count; i++)
                {
                    var member = token[i];
                    if (named.TryGetValue(member, out var index))
                    {
                        key.Add(index);
                    }

                    // token[0] is the account itself (classified above); the groups repeat across accounts, so they are cached.
                    if (i > 0)
                    {
                        if (!classes.TryGetValue(member, out var memberClass))
                        {
                            classes[member] = memberClass = classifier.Classify(member);
                        }

                        isTier0 |= memberClass == PrincipalClass.Tier0;
                    }
                }

                if (isTier0)
                {
                    tier0Accounts.Add(sid);
                }

                if (named.ContainsKey(sid))
                {
                    names.TryAdd(sid, account.Name);
                }

                key.Sort();
                var keyArray = key.Distinct().ToArray();
                if (!memo.TryGetValue(keyArray, out var entry))
                {
                    memo[keyArray] = entry = Entry(keyArray, namedList, plans, totals, rowSlots);
                }

                entry.Add(new PkiAccountRef(account.DistinguishedName, account.Name, disabled));
                if (account.GetStrings(PkiAttributes.AltSecurityIdentities) is { Count: > 0 } values)
                {
                    mapped.Add(new PkiAccountFact(account.DistinguishedName, account.Name, sid, disabled, isTier0, [.. values.Select(AltSecurityIdentity.Parse)]));
                }
            }

            if (scanned % 500 == 0)
            {
                progress?.Report(new PkiExpansionProgress(scanned));
            }
        }

        if (scanned % 500 != 0)
        {
            progress?.Report(new PkiExpansionProgress(scanned));
        }

        foreach (var entry in memo.Values)
        {
            entry.Distribute();
        }

        // The result: trustees with their counts, the totals, the notices.
        var enriched = new PrincipalClassifier(classifier.DomainSid, classifier.RootDomainSid, classifier.Options, graph, classifier.CaComputerSids)
        {
            Tier0Accounts = [.. tier0Accounts.Concat(classifier.Tier0Accounts).Distinct(StringComparer.OrdinalIgnoreCase)],
        };
        var result = new Dictionary<string, TemplateRights>(StringComparer.OrdinalIgnoreCase);
        var notices = new List<PkiReadNotice>();
        for (var p = 0; p < plans.Count; p++)
        {
            var plan = plans[p];
            if (!plan.Acl.IsEvaluated)
            {
                result.TryAdd(plan.Template.Cn, new TemplateRights(plan.Template.Cn, plan.Template.ObjectGuid, false, [], []));
                continue;
            }

            var trustees = new List<TemplateTrustee>(plan.Rows.Count);
            for (var r = 0; r < plan.Rows.Count; r++)
            {
                var row = plan.Rows[r];
                var slot = rowSlots[p][r];
                var resolved = !enriched.IsOtherDomain(row.Sid);
                trustees.Add(new TemplateTrustee(
                    row.Sid,
                    enriched.NameOf(row.Sid) ?? names.GetValueOrDefault(row.Sid),
                    enriched.Classify(row.Sid),
                    row.Right,
                    resolved ? slot.Enabled : -1,
                    resolved ? slot.Disabled : -1,
                    resolved ? slot.First : [],
                    resolved,
                    row.Attribute));
            }

            var rightTotals = new List<TemplateRightTotal>(AllRights.Length);
            foreach (var right in AllRights)
            {
                var slot = totals[(p * AllRights.Length) + (int)right];
                var highest = trustees
                    .Where(t => t.Right == right && (!t.IsResolved || t.AccountCount + t.DisabledCount > 0))
                    .Select(t => (PrincipalClass?)t.Class)
                    .MaxBy(Danger);
                rightTotals.Add(new TemplateRightTotal(right, highest, slot.Enabled, slot.Disabled, slot.First));
            }

            if (trustees.Any(t => !t.IsResolved && t.Class != PrincipalClass.Tier0))
            {
                notices.Add(new PkiReadNotice(PkiReadNoticeCode.TrusteeUnresolved, plan.Template.Dn, plan.Template.ObjectGuid));
            }

            result.TryAdd(plan.Template.Cn, new TemplateRights(plan.Template.Cn, plan.Template.ObjectGuid, true, trustees, rightTotals));
        }

        return new PkiEnrollmentExpansion(result, mapped, scanned, graph, context.Options, notices)
        {
            Classifier = enriched,
            DistinctTokens = memo.Count,
        };
    }

    // ---- Decoding ------------------------------------------------------------------------------------------------------

    private static TemplateAcl AclOf(CertificateTemplate template, AclObjectTypes types) =>
        template.SecurityDescriptor is { Length: > 0 } descriptor ? Evaluate(descriptor, types) : NotEvaluated;

    /// <summary>
    /// The ACE reaches a template: no inherited object type, the template class, or a GUID nothing can name (kept –
    /// conservative); a known other class (or attribute, right) is skipped.
    /// </summary>
    private static bool AppliesToTemplates(SecurityDescriptorAce ace, AclObjectTypes types) =>
        ace.InheritedObjectType is not { } inherited || types.IsClass(inherited, PkiClasses.CertificateTemplate) || IsUnknown(types, inherited);

    private static bool IsUnknown(AclObjectTypes types, Guid guid) => string.Equals(types.Describe(guid), guid.ToString("D"), StringComparison.Ordinal);

    /// <summary>One TemplateAce per atomic right the ACE grants or denies.</summary>
    private static void Expand(SecurityDescriptorAce ace, bool deny, AclObjectTypes types, List<TemplateAce> aces)
    {
        var mask = DirectoryAccessMask.Normalize(ace.Mask);
        var objectType = ace.ObjectType;
        var whole = objectType is not { } typed || types.IsClass(typed, PkiClasses.CertificateTemplate);
        var standard = whole || !deny;
        var start = aces.Count;
        void Add(TemplateRight right, string? attribute = null) => aces.Add(new TemplateAce(ace.Sid, right, deny, attribute));

        if (!deny && whole && (mask & DirectoryAccessMask.FullControl) == DirectoryAccessMask.FullControl)
        {
            Add(TemplateRight.FullControl);
        }

        if ((mask & DirectoryAccessMask.ControlAccess) != 0)
        {
            if (whole)
            {
                Add(TemplateRight.Enroll);
                Add(TemplateRight.AutoEnroll);
            }
            else if (types.IsRight(objectType!.Value, AclObjectTypes.CertificateEnrollment))
            {
                Add(TemplateRight.Enroll);
            }
            else if (types.IsRight(objectType.Value, AclObjectTypes.CertificateAutoEnrollment))
            {
                Add(TemplateRight.AutoEnroll);
            }
        }

        if (standard && (mask & DirectoryAccessMask.WriteDacl) != 0)
        {
            Add(TemplateRight.WriteDacl);
        }

        if (standard && (mask & DirectoryAccessMask.WriteOwner) != 0)
        {
            Add(TemplateRight.WriteOwner);
        }

        if ((mask & DirectoryAccessMask.WriteProperty) != 0)
        {
            if (whole)
            {
                Add(TemplateRight.WriteAllProperties);
            }
            else
            {
                var property = objectType!.Value;
                var sensitive = PkiAttributes.SensitiveTemplateAttributes.Where(a => types.Covers(property, a)).ToList();
                foreach (var attribute in sensitive)
                {
                    Add(TemplateRight.WriteSensitiveProperty, attribute);
                }

                if (sensitive.Count == 0 && IsUnknown(types, property))
                {
                    Add(TemplateRight.WriteSensitiveProperty, property.ToString("D"));
                }
                else if (sensitive.Count == 0 && (types.AttributeName(property) is not null || (types.ClassName(property) is null && types.RightName(property) is null)))
                {
                    // Another attribute or property set (a class or extended-right GUID reaches no property of a template).
                    Add(TemplateRight.WriteOtherProperty, types.Describe(property));
                }
            }
        }

        if (deny && (aces.Count > start || (whole && (mask & DirectoryAccessMask.FullControl) != 0)))
        {
            aces.Insert(start, new TemplateAce(ace.Sid, TemplateRight.FullControl, true, null));
        }
    }

    /// <summary>The token can write the attribute: its first covering ACE (that attribute, or all properties) is an allow.</summary>
    private static bool CanWrite(Token token, TemplateAcl acl, string attribute)
    {
        foreach (var ace in acl.Aces)
        {
            if (token.Contains(ace.Sid)
                && (ace.Right == TemplateRight.WriteAllProperties
                    || (ace.Right is TemplateRight.WriteSensitiveProperty or TemplateRight.WriteOtherProperty
                        && string.Equals(ace.Attribute, attribute, StringComparison.OrdinalIgnoreCase))))
            {
                return !ace.IsDeny;
            }
        }

        return false;
    }

    /// <summary>
    /// The trustee rows of one ACL: per right (enum order) the trustees (DACL order, the owner last) that hold it alone –
    /// one row per attribute for the two attribute rights. OWNER RIGHTS is the owner's; placeholders are left out.
    /// </summary>
    private static List<TrusteeRow> Rows(TemplateAcl acl)
    {
        var sids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sid in acl.Aces.Select(a => a.Sid).Append(acl.Owner).OfType<string>())
        {
            if (!NeverInToken.Contains(sid) && !string.Equals(sid, OwnerRightsSid, StringComparison.OrdinalIgnoreCase) && seen.Add(sid))
            {
                sids.Add(sid);
            }
        }

        var rows = new List<TrusteeRow>();
        foreach (var right in AllRights)
        {
            foreach (var sid in sids)
            {
                var alone = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sid };
                if (right is TemplateRight.WriteSensitiveProperty or TemplateRight.WriteOtherProperty)
                {
                    var token = new Token(alone, acl);
                    foreach (var attribute in acl.Aces
                        .Where(a => !a.IsDeny && a.Right == right && a.Attribute is not null && token.Contains(a.Sid))
                        .Select(a => a.Attribute!)
                        .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (CanWrite(token, acl, attribute))
                        {
                            rows.Add(new TrusteeRow(sid, right, attribute));
                        }
                    }
                }
                else if (Decide(alone, acl, right))
                {
                    rows.Add(new TrusteeRow(sid, right, null));
                }
            }
        }

        return rows;
    }

    /// <summary>A row holds for a token: the trustee is in it and the token holds the row's right (for its attribute).</summary>
    private static bool Holds(IReadOnlySet<string> tokenSids, TemplateAcl acl, TrusteeRow row)
    {
        var token = new Token(tokenSids, acl);
        if (!token.Contains(row.Sid))
        {
            return false;
        }

        return row.Attribute is { } attribute ? CanWrite(token, acl, attribute) : Decide(tokenSids, acl, row.Right);
    }

    // ---- The account pass ---------------------------------------------------------------------------------------------

    private static void BuildToken(DirectoryObject account, string sid, bool isWritableDc, GroupGraph graph, List<string> token)
    {
        token.Clear();
        token.Add(sid);
        var separator = sid.LastIndexOf('-');
        var primary = separator > 0 && account.GetInt64(Attr.PrimaryGroupId) is { } rid and > 0
            ? string.Concat(sid.AsSpan(0, separator), "-", rid.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;
        if (primary is not null)
        {
            token.Add(primary);
        }

        var step = new PrivilegePathStep(account.DistinguishedName, account.Name, PrivilegePathStepKind.Account);
        foreach (var group in graph.Reach(step, account.GetStrings(Attr.MemberOf), primary, includeImplicit: true).Groups)
        {
            if (group.Sid is { } groupSid)
            {
                token.Add(groupSid);
            }
        }

        token.Add(EveryoneSid);
        token.Add(AuthenticatedUsersSid);
        token.Add(ThisOrganizationSid);
        token.Add(NetworkSid);
        if (isWritableDc)
        {
            token.Add(EnterpriseDomainControllersSid);
        }
    }

    /// <summary>The decisions of one memo key: which totals and trustee rows its accounts count towards.</summary>
    private static KeyEntry Entry(int[] key, List<string> namedList, List<TemplatePlan> plans, Slot[] totals, List<Slot[]> rowSlots)
    {
        var sids = new HashSet<string>(key.Select(i => namedList[i]), StringComparer.OrdinalIgnoreCase);
        var slots = new List<Slot>();
        for (var p = 0; p < plans.Count; p++)
        {
            var plan = plans[p];
            if (!plan.Acl.IsEvaluated)
            {
                continue;
            }

            foreach (var right in AllRights)
            {
                if (Decide(sids, plan.Acl, right))
                {
                    slots.Add(totals[(p * AllRights.Length) + (int)right]);
                }
            }

            for (var r = 0; r < plan.Rows.Count; r++)
            {
                if (Holds(sids, plan.Acl, plan.Rows[r]))
                {
                    slots.Add(rowSlots[p][r]);
                }
            }
        }

        return new KeyEntry([.. slots]);
    }

    /// <summary>Broad above Other above Tier 0 (null: nobody).</summary>
    private static int Danger(PrincipalClass? value) => value switch
    {
        PrincipalClass.Broad => 3,
        PrincipalClass.Other => 2,
        PrincipalClass.Tier0 => 1,
        _ => 0,
    };

    /// <summary>A token as AccessCheck sees it: the owner SID also brings OWNER RIGHTS.</summary>
    private readonly struct Token(IReadOnlySet<string> sids, TemplateAcl acl)
    {
        public bool IsOwner { get; } = acl.Owner is { } owner && sids.Contains(owner);

        public bool Contains(string sid) =>
            sids.Contains(sid) || (IsOwner && string.Equals(sid, OwnerRightsSid, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record TemplatePlan(CertificateTemplate Template, TemplateAcl Acl, List<TrusteeRow> Rows)
    {
        /// <summary>The template GUID and the row count only.</summary>
        public override string ToString() => $"{nameof(TemplatePlan)} {Template.ObjectGuid:B} ({Rows.Count} rows)";
    }

    private sealed record TrusteeRow(string Sid, TemplateRight Right, string? Attribute)
    {
        /// <summary>The right only (never the SID or attribute).</summary>
        public override string ToString() => $"{nameof(TrusteeRow)}({Right})";
    }

    /// <summary>The count and the first accounts of one total or trustee row (disabled accounts give way to enabled ones).</summary>
    private sealed class Slot(int limit)
    {
        private readonly List<PkiAccountRef> _first = [];
        private int _disabledInList;

        public int Enabled { get; set; }

        public int Disabled { get; set; }

        public bool IsSaturated => _first.Count >= limit && _disabledInList == 0;

        public IReadOnlyList<PkiAccountRef> First => [.. _first.Where(a => !a.IsDisabled), .. _first.Where(a => a.IsDisabled)];

        public void Offer(PkiAccountRef account)
        {
            if (_first.Count < limit)
            {
                _first.Add(account);
                _disabledInList += account.IsDisabled ? 1 : 0;
            }
            else if (!account.IsDisabled && _disabledInList > 0)
            {
                _first.RemoveAt(_first.FindLastIndex(a => a.IsDisabled));
                _first.Add(account);
                _disabledInList--;
            }
        }
    }

    /// <summary>The accounts of one memo key: counted here, distributed to the slots once at the end.</summary>
    private sealed class KeyEntry(Slot[] slots)
    {
        private int _enabled;
        private int _disabled;
        private bool _saturated = slots.Length == 0;

        public void Add(PkiAccountRef account)
        {
            if (account.IsDisabled)
            {
                _disabled++;
            }
            else
            {
                _enabled++;
            }

            if (_saturated)
            {
                return;
            }

            var saturated = true;
            foreach (var slot in slots)
            {
                slot.Offer(account);
                saturated &= slot.IsSaturated;
            }

            _saturated = saturated;
        }

        public void Distribute()
        {
            foreach (var slot in slots)
            {
                slot.Enabled += _enabled;
                slot.Disabled += _disabled;
            }
        }
    }

    /// <summary>Structural equality of sorted index arrays.</summary>
    private sealed class KeyComparer : IEqualityComparer<int[]>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals(int[]? x, int[]? y) => ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(int[] obj)
        {
            var hash = new HashCode();
            foreach (var value in obj)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
