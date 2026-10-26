using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Security;

namespace AdMng.Core.PrivilegedAccess;

/// <summary>
/// Source 3 of the privileged access review (README U4b, design docs/dev/u4-privileged-access.md section 6): rights beyond
/// the defaults in the ACLs of the directory. Reads the security descriptors (owner + DACL) of the domain head, the OUs,
/// CN=Users, CN=Computers, CN=Builtin, CN=Policies and every GPO, AdminSDHolder and the Tier 0 objects (domain
/// controllers, krbtgt, the built-in Administrator, the protected and Tier 0 groups) - users, groups and computers only
/// with <see cref="PrivilegedAccessOptions.DeepAclScan"/> - plus the resource-based delegation of every account. ACEs of the
/// class's <c>defaultSecurityDescriptor</c> (schema) and of the default trustees (SYSTEM, Administrators, Domain Admins,
/// Enterprise Admins, the domain controllers …) are not reported; an inherited ACE is reported once, at the scanned
/// container it comes from. Every trustee becomes a grant (<see cref="PrivilegeFindingKind.AclRight"/>, scope = the object)
/// that the evaluator expands to the members with the path. All reads on the session's DC, nothing kept after the run.
/// </summary>
public sealed class DirectoryAclGrantSource : IPrivilegeGrantSource
{
    public PrivilegeSource Source => PrivilegeSource.DirectoryAcl;

    public Task<IReadOnlyList<PrivilegeGrant>> CollectAsync(PrivilegeScanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new DirectoryAclScan(context).RunAsync(cancellationToken);
    }
}

/// <summary>What an object whose ACL is read is; decides which rights matter and their level.</summary>
internal enum AclTargetKind
{
    DomainHead,
    Container,
    GroupPolicy,
    Account,
    Group,
}

/// <summary>An object whose security descriptor was read (in memory for the run only).</summary>
internal sealed class AclTarget
{
    public required string Dn { get; init; }

    public required AclTargetKind Kind { get; init; }

    /// <summary>The name shown for the scope when it is not the RDN (the display name of a GPO).</summary>
    public string? DisplayName { get; init; }

    /// <summary>objectSid (rights of an object on itself are not reported).</summary>
    public string? Sid { get; init; }

    /// <summary>The most specific objectClass (its defaultSecurityDescriptor are the defaults).</summary>
    public string? StructuralClass { get; init; }

    public string? Owner { get; init; }

    public IReadOnlyList<SecurityDescriptorAce> Aces { get; init; } = [];

    /// <summary>The object itself is Tier 0 (domain head, AdminSDHolder, Tier 0 accounts and groups, GPOs of the DCs).</summary>
    public bool IsTier0 { get; set; }

    /// <summary>Its children are Tier 0 too (OU=Domain Controllers, GPOs): inherit-only ACEs keep the Tier 0 level.</summary>
    public bool Tier0Subtree { get; set; }

    /// <summary>
    /// OU=Domain Controllers is below this container (the domain head): an inheritable ACE that reaches computers reaches
    /// the domain controllers' accounts too (they are not protected by AdminSDHolder).
    /// </summary>
    public bool HoldsDomainControllers { get; set; }

    /// <summary>The level of a control right (full control, reset password, member write …) when not Tier 0.</summary>
    public PrivilegeLevel ControlLevel { get; set; } = PrivilegeLevel.High;

    /// <summary>The object is a server, or the container holds servers (LAPS, resource-based delegation).</summary>
    public bool HasServers { get; set; }

    public bool IsDomainController { get; set; }

    /// <summary>The owner is reported (containers, GPOs, Tier 0 objects; not ordinary leaf objects - creators own them).</summary>
    public bool ReportOwner { get; set; }

    public DistinguishedName Parsed => _parsed ??= DistinguishedName.TryParse(Dn, out var dn) ? dn : DistinguishedName.Root;

    private DistinguishedName? _parsed;
}

/// <summary>One run of <see cref="DirectoryAclGrantSource"/>.</summary>
internal sealed class DirectoryAclScan
{
    private const string DefaultSecurityDescriptorAttribute = "defaultSecurityDescriptor";
    private const string LdapDisplayNameAttribute = "lDAPDisplayName";
    private const string RightsGuidAttribute = "rightsGuid";
    private const string ValidAccessesAttribute = "validAccesses";

    /// <summary>Trustees whose rights are the defaults of every domain (never reported).</summary>
    private static readonly HashSet<string> DefaultTrusteeSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18",     // SYSTEM
        "S-1-5-10",     // PRINCIPAL SELF
        "S-1-3-0",      // CREATOR OWNER
        "S-1-3-1",      // CREATOR GROUP
        "S-1-3-4",      // OWNER RIGHTS
        "S-1-5-9",      // Enterprise Domain Controllers
        "S-1-5-32-544", // Administrators
    };

    /// <summary>Domain RIDs of default trustees: Domain Admins, Domain Controllers, Read-only DCs, Key Admins.</summary>
    private static readonly uint[] DefaultTrusteeDomainRids =
        [WellKnownRids.DomainAdmins, WellKnownRids.DomainControllers, WellKnownRids.ReadOnlyDomainControllers, WellKnownRids.KeyAdmins];

    /// <summary>Forest root RIDs of default trustees: Schema/Enterprise Admins, Enterprise Read-only DCs, Enterprise Key Admins.</summary>
    private static readonly uint[] DefaultTrusteeRootRids =
        [WellKnownRids.SchemaAdmins, WellKnownRids.EnterpriseAdmins, WellKnownRids.EnterpriseReadOnlyDomainControllers, WellKnownRids.EnterpriseKeyAdmins];

    private const string AccountOperatorsSid = "S-1-5-32-548";
    private const string PrintOperatorsSid = "S-1-5-32-550";

    private static readonly string[] SdAttributes = [Attr.NtSecurityDescriptor, Attr.ObjectClass, Attr.Name, Attr.ObjectSid];

    private readonly PrivilegeScanContext _context;
    private readonly IDirectorySession _session;
    private readonly PrivilegedAccessOptions _options;
    private readonly string _nc;
    private readonly Dictionary<string, AclTarget> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Target, string Sid), GrantBuilder> _grants = [];
    private readonly Dictionary<string, IReadOnlyList<SecurityDescriptorAce>> _defaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DistinguishedName> _servers = [];
    private AclObjectTypes _types = new(null, null);

    /// <summary>
    /// Trustees AD (and Samba) give rights on particular objects at provisioning, beyond the class defaults: Account and
    /// Print Operators create/delete children of CN=Users and CN=Computers, Group Policy Creator Owners create GPOs in
    /// CN=Policies.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _provisioningTrustees = new(StringComparer.Ordinal);

    public DirectoryAclScan(PrivilegeScanContext context)
    {
        _context = context;
        _session = context.Session;
        _options = context.Options;
        _nc = _session.Info.DefaultNamingContext;
    }

    private int PageSize => _options.PageSize;

    public async Task<IReadOnlyList<PrivilegeGrant>> RunAsync(CancellationToken cancellationToken)
    {
        var head = await _session.GetAsync(_nc, [.. SdAttributes, Attr.GpLink, .. WellKnownObjects.Attributes], cancellationToken).ConfigureAwait(false);
        if (head is null || Read(head, AclTargetKind.DomainHead) is not { } headTarget)
        {
            return [];
        }

        headTarget.IsTier0 = true;
        headTarget.ReportOwner = true;
        var wellKnown = WellKnown(head);
        var nc = DistinguishedName.Parse(_nc);
        var dcOu = wellKnown.GetValueOrDefault(WellKnownGuids.DomainControllers) ?? nc.Child("OU", "Domain Controllers").ToString();
        var tier0Gpos = LinkedGpoKeys(head.GetString(Attr.GpLink));

        // The containers: CN=Users, CN=Computers (wherever wellKnownObjects points), AdminSDHolder, CN=Policies.
        var system = nc.Child("CN", "System");
        var users = wellKnown.GetValueOrDefault(WellKnownGuids.Users) ?? nc.Child("CN", "Users").ToString();
        var computers = wellKnown.GetValueOrDefault(WellKnownGuids.Computers) ?? nc.Child("CN", "Computers").ToString();
        var adminSdHolderDn = system.Child("CN", "AdminSDHolder").ToString();
        var policies = system.Child("CN", "Policies").ToString();
        foreach (var dn in new[] { users, computers, adminSdHolderDn, policies })
        {
            if (await _session.GetAsync(dn, SdAttributes, cancellationToken).ConfigureAwait(false) is { } obj && Read(obj, AclTargetKind.Container) is { } target)
            {
                target.ReportOwner = true;
            }
        }

        _provisioningTrustees[GroupGraph.Key(users)] = new(StringComparer.OrdinalIgnoreCase) { AccountOperatorsSid, PrintOperatorsSid };
        _provisioningTrustees[GroupGraph.Key(computers)] = new(StringComparer.OrdinalIgnoreCase) { AccountOperatorsSid, PrintOperatorsSid };
        if (_options.DomainSid is { } domain)
        {
            _provisioningTrustees[GroupGraph.Key(policies)] = new(StringComparer.OrdinalIgnoreCase)
            {
                string.Create(CultureInfo.InvariantCulture, $"{domain}-{WellKnownRids.GroupPolicyCreatorOwners}"),
            };
        }

        if (_index.TryGetValue(GroupGraph.Key(adminSdHolderDn), out var adminSdHolder))
        {
            adminSdHolder.IsTier0 = true;
        }

        // Every OU and CN=Builtin (one paged search).
        await foreach (var obj in SearchAsync(_nc, SearchScope.Subtree,
            LdapFilter.Or(ObjectKind.OrganizationalUnit.ToLdapFilter(), ObjectKind.BuiltinDomain.ToLdapFilter()),
            [.. SdAttributes, Attr.GpLink], cancellationToken).ConfigureAwait(false))
        {
            if (Read(obj, AclTargetKind.Container) is not { } target)
            {
                continue;
            }

            target.ReportOwner = true;
            if (DistinguishedName.AreEqual(obj.DistinguishedName, dcOu))
            {
                target.IsTier0 = true;
                target.Tier0Subtree = true;
                tier0Gpos.UnionWith(LinkedGpoKeys(obj.GetString(Attr.GpLink)));
            }
        }

        if (DistinguishedName.TryParse(dcOu, out var dcOuDn))
        {
            foreach (var target in _index.Values.Where(t => t.Kind is AclTargetKind.DomainHead or AclTargetKind.Container))
            {
                target.HoldsDomainControllers = dcOuDn.IsDescendantOf(target.Parsed);
            }
        }

        // The GPOs (one-level search of CN=Policies); those linked to the domain head or the DCs' OU are Tier 0.
        if (_index.ContainsKey(GroupGraph.Key(policies)))
        {
            await foreach (var obj in SearchAsync(policies, SearchScope.OneLevel,
                LdapFilter.Equal(Attr.ObjectClass, AclObjectTypes.GroupPolicyContainer), [.. SdAttributes, Attr.DisplayName], cancellationToken).ConfigureAwait(false))
            {
                if (Read(obj, AclTargetKind.GroupPolicy, obj.GetString(Attr.DisplayName)) is { } target)
                {
                    target.ReportOwner = true;
                    target.Tier0Subtree = true;
                    target.IsTier0 = tier0Gpos.Contains(GroupGraph.Key(obj.DistinguishedName));
                }
            }
        }

        // The Tier 0 objects: domain controllers, Administrator, krbtgt and the protected / Tier 0 groups of the graph.
        await foreach (var obj in SearchAsync(_nc, SearchScope.Subtree, Tier0Filter(), [.. SdAttributes, Attr.UserAccountControl, Attr.OperatingSystem], cancellationToken)
            .ConfigureAwait(false))
        {
            var group = obj.Kind == ObjectKind.Group;
            if (Read(obj, group ? AclTargetKind.Group : AclTargetKind.Account) is { } target)
            {
                target.IsTier0 = true;
                target.ReportOwner = true;
                target.IsDomainController = IsDomainController(obj);
            }
        }

        // What the ACEs mean: the defaults of the classes, the extended rights; servers when LAPS rights are possible.
        await ReadDefaultsAsync(cancellationToken).ConfigureAwait(false);
        _types = new AclObjectTypes(_session.Schema, await ReadControlAccessRightsAsync(cancellationToken).ConfigureAwait(false));
        if (_types.HasLaps)
        {
            await ReadServersAsync(cancellationToken).ConfigureAwait(false);
            foreach (var target in _index.Values.Where(t => t.Kind is AclTargetKind.Container or AclTargetKind.DomainHead))
            {
                target.HasServers = _servers.Any(s => s.IsDescendantOf(target.Parsed));
            }
        }

        foreach (var target in _index.Values.OrderBy(t => t.Dn, StringComparer.Ordinal))
        {
            Evaluate(target);
        }

        if (_options.DeepAclScan)
        {
            await DeepScanAsync(cancellationToken).ConfigureAwait(false);
        }

        await ResourceBasedDelegationAsync(cancellationToken).ConfigureAwait(false);
        return [.. _grants.Values
            .OrderBy(g => g.Target.Dn, StringComparer.Ordinal)
            .ThenBy(g => g.Sid, StringComparer.Ordinal)
            .Select(g => g.Build())];
    }

    // ---- Reads -----------------------------------------------------------------------------------------------------

    private IAsyncEnumerable<DirectoryObject> SearchAsync(string baseDn, SearchScope scope, string filter, IReadOnlyList<string> attributes, CancellationToken cancellationToken) =>
        _session.SearchAsync(new DirectorySearch
        {
            BaseDn = baseDn,
            Scope = scope,
            Filter = filter,
            Attributes = attributes,
            PageSize = PageSize,
            ReadSecurityDescriptor = attributes.Contains(Attr.NtSecurityDescriptor, StringComparer.OrdinalIgnoreCase),
        }, cancellationToken);

    /// <summary>Parses the descriptor of an object and indexes it; null when it has none (no right to read it) or it is malformed.</summary>
    private AclTarget? Read(DirectoryObject obj, AclTargetKind kind, string? displayName = null)
    {
        var key = GroupGraph.Key(obj.DistinguishedName);
        if (_index.ContainsKey(key))
        {
            return null;
        }

        if (Parse(obj, kind, displayName) is not { } target)
        {
            return null;
        }

        _index[key] = target;
        return target;
    }

    private static AclTarget? Parse(DirectoryObject obj, AclTargetKind kind, string? displayName = null) =>
        obj.GetBytes(Attr.NtSecurityDescriptor) is { Length: > 0 } bytes && SecurityDescriptorSummary.TryParse(bytes, out var sd) && sd is not null
            ? new AclTarget
            {
                Dn = obj.DistinguishedName,
                Kind = kind,
                DisplayName = displayName,
                Sid = obj.ObjectSid,
                StructuralClass = obj.GetStrings(Attr.ObjectClass).LastOrDefault(),
                Owner = sd.Owner,
                Aces = sd.Aces,
            }
            : null;

    private static Dictionary<string, string> WellKnown(DirectoryObject head)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in head.GetStrings(WellKnownObjects.WellKnownObjectsAttribute))
        {
            if (WellKnownObjects.TryParseValue(value, out var guid, out var dn))
            {
                result[guid] = dn;
            }
        }

        return result;
    }

    /// <summary>Domain controllers, Administrator, krbtgt and the graph's protected or Tier 0 catalogue groups.</summary>
    private string Tier0Filter()
    {
        var parts = new List<string> { PrivilegedAccessScanner.DomainControllerFilter };
        if (_options.DomainSid is { } domainSid)
        {
            parts.Add(LdapFilter.Equal(Attr.ObjectSid, string.Create(CultureInfo.InvariantCulture, $"{domainSid}-{WellKnownRids.Administrator}")));
            parts.Add(LdapFilter.Equal(Attr.ObjectSid, string.Create(CultureInfo.InvariantCulture, $"{domainSid}-{WellKnownRids.Krbtgt}")));
        }

        foreach (var node in _context.Groups.Nodes.Where(n => !n.IsImplicit && n.Sid is not null))
        {
            if (PrivilegedGroupCatalog.Match(node.Sid, node.SamAccountName, _options.DomainSid, _options.RootDomainSid) is { } definition
                && (definition.IsProtected || _options.LevelOf(definition) == PrivilegeLevel.Tier0))
            {
                parts.Add(LdapFilter.Equal(Attr.ObjectSid, node.Sid!));
            }
        }

        return LdapFilter.Or(parts);
    }

    /// <summary>The defaultSecurityDescriptor of every class read (one search of the schema).</summary>
    private async Task ReadDefaultsAsync(CancellationToken cancellationToken)
    {
        var classes = _index.Values.Select(t => t.StructuralClass).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_options.DeepAclScan)
        {
            classes.UnionWith([ObjectClasses.User, ObjectClasses.InetOrgPerson, ObjectClasses.Computer, ObjectClasses.Group,
                ObjectClasses.ManagedServiceAccount, ObjectClasses.GroupManagedServiceAccount, ObjectClasses.DelegatedManagedServiceAccount]);
        }

        if (classes.Count == 0)
        {
            return;
        }

        var filter = LdapFilter.And(
            LdapFilter.Equal(Attr.ObjectClass, "classSchema"),
            LdapFilter.Or(classes.Order(StringComparer.OrdinalIgnoreCase).Select(c => LdapFilter.Equal(LdapDisplayNameAttribute, c))));
        try
        {
            await foreach (var definition in SearchAsync(_session.Info.SchemaNamingContext, SearchScope.OneLevel, filter,
                [LdapDisplayNameAttribute, DefaultSecurityDescriptorAttribute], cancellationToken).ConfigureAwait(false))
            {
                if (definition.GetString(LdapDisplayNameAttribute) is { Length: > 0 } name)
                {
                    _defaults[name] = Sddl.ParseDacl(definition.GetString(DefaultSecurityDescriptorAttribute), _options.DomainSid, _options.RootDomainSid);
                }
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // No schema to read: only the default trustees are left out.
        }
    }

    /// <summary>CN=Extended-Rights of the configuration (one one-level search): rightsGuid → name.</summary>
    private async Task<IReadOnlyList<(Guid Guid, string Name, int ValidAccesses)>> ReadControlAccessRightsAsync(CancellationToken cancellationToken)
    {
        var rights = new List<(Guid, string, int)>();
        var extendedRights = DistinguishedName.TryParse(_session.Info.ConfigurationNamingContext, out var configuration) && !configuration.IsRoot
            ? configuration.Child("CN", "Extended-Rights").ToString()
            : null;
        if (extendedRights is null)
        {
            return rights;
        }

        try
        {
            await foreach (var right in SearchAsync(extendedRights, SearchScope.OneLevel,
                LdapFilter.Equal(Attr.ObjectClass, "controlAccessRight"), [Attr.Cn, RightsGuidAttribute, ValidAccessesAttribute], cancellationToken).ConfigureAwait(false))
            {
                if (Guid.TryParse(right.GetString(RightsGuidAttribute), out var guid) && (right.GetString(Attr.Cn) ?? right.Name) is { Length: > 0 } name)
                {
                    rights.Add((guid, name, right.GetInt32(ValidAccessesAttribute) ?? 0));
                }
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // Only the fixed GUIDs of the base schema are known.
        }

        return rights;
    }

    /// <summary>Server computers (DN only): LAPS rights over servers are High.</summary>
    private async Task ReadServersAsync(CancellationToken cancellationToken)
    {
        await foreach (var obj in SearchAsync(_nc, SearchScope.Subtree,
            LdapFilter.And(ObjectKind.Computer.ToLdapFilter(), LdapFilter.Contains(Attr.OperatingSystem, "server")), [], cancellationToken).ConfigureAwait(false))
        {
            _servers.Add(obj.Dn);
        }
    }

    /// <summary>Users, groups and computers (one paged search, evaluated as they come, nothing kept).</summary>
    private async Task DeepScanAsync(CancellationToken cancellationToken)
    {
        var filter = LdapFilter.Or(ObjectKind.User.ToLdapFilter(), ObjectKind.Computer.ToLdapFilter(), ObjectKind.Group.ToLdapFilter());
        await foreach (var obj in SearchAsync(_nc, SearchScope.Subtree, filter,
            [.. SdAttributes, Attr.UserAccountControl, Attr.OperatingSystem, Attr.MemberOf, Attr.PrimaryGroupId, Attr.SamAccountName], cancellationToken)
            .ConfigureAwait(false))
        {
            if (_index.ContainsKey(GroupGraph.Key(obj.DistinguishedName)))
            {
                continue; // Tier 0 objects were evaluated already.
            }

            var group = obj.Kind == ObjectKind.Group;
            if (Parse(obj, group ? AclTargetKind.Group : AclTargetKind.Account) is not { } target)
            {
                continue;
            }

            var server = !group && IsServer(obj);
            var level = PrivilegeLevels.Max(PrivilegeLevel.Elevated, PrivilegeLevels.Max(server ? PrivilegeLevel.High : PrivilegeLevel.None, CatalogueLevel(obj, group)));
            target.IsTier0 = level == PrivilegeLevel.Tier0 || IsDomainController(obj);
            target.ReportOwner = target.IsTier0;
            target.ControlLevel = level;
            target.HasServers = server;
            Evaluate(target);
        }
    }

    /// <summary>
    /// msDS-AllowedToActOnBehalfOfOtherIdentity of every account: the principals it lists may impersonate any user to it
    /// (Tier 0 towards a DC, High towards a server).
    /// </summary>
    private async Task ResourceBasedDelegationAsync(CancellationToken cancellationToken)
    {
        await foreach (var obj in SearchAsync(_nc, SearchScope.Subtree, LdapFilter.Present(Attr.AllowedToActOnBehalfOfOtherIdentity),
            [Attr.AllowedToActOnBehalfOfOtherIdentity, Attr.ObjectSid, Attr.ObjectClass, Attr.Name, Attr.UserAccountControl, Attr.OperatingSystem], cancellationToken)
            .ConfigureAwait(false))
        {
            if (obj.GetBytes(Attr.AllowedToActOnBehalfOfOtherIdentity) is not { Length: > 0 } bytes
                || !SecurityDescriptorSummary.TryParse(bytes, out var sd) || sd is null)
            {
                continue;
            }

            var target = new AclTarget
            {
                Dn = obj.DistinguishedName,
                Kind = AclTargetKind.Account,
                Sid = obj.ObjectSid,
                IsDomainController = IsDomainController(obj),
                HasServers = IsServer(obj),
            };
            var level = target.IsDomainController ? PrivilegeLevel.Tier0 : target.HasServers ? PrivilegeLevel.High : PrivilegeLevel.Elevated;
            foreach (var ace in sd.Aces.Where(a => a.IsAllow && !IsDefaultTrustee(a.Sid, target)))
            {
                Add(target, ace.Sid, AclRight.ActOnBehalfOfOtherIdentity, null, level);
            }
        }
    }

    // ---- Evaluation --------------------------------------------------------------------------------------------------

    private void Evaluate(AclTarget target)
    {
        foreach (var ace in target.Aces)
        {
            if (!ace.IsAllow || IsDefaultTrustee(ace.Sid, target))
            {
                continue;
            }

            if (ace.IsInherited ? IsCovered(target, ace) : IsDefault(target, ace))
            {
                continue;
            }

            // Inherit-only: for the children only - unless they include the domain controllers' accounts.
            var childrenOnly = ace.IsInheritOnly && !(target.HoldsDomainControllers
                && (ace.InheritedObjectType is not { } inherited || _types.IsClass(inherited, ObjectClasses.Computer)));
            foreach (var (right, detail) in RightsOf(ace, target))
            {
                Add(target, ace.Sid, right, detail, LevelOf(right, target, childrenOnly));
            }
        }

        if (target.ReportOwner && target.Owner is { } owner && !IsDefaultTrustee(owner, target))
        {
            Add(target, owner, AclRight.Owner, null, LevelOf(AclRight.Owner, target, childrenOnly: false));
        }
    }

    /// <summary>The rights of an allow ACE that matter on the target (mask, object type, inherited object type).</summary>
    private List<(AclRight Right, string? Detail)> RightsOf(SecurityDescriptorAce ace, AclTarget target)
    {
        var rights = new List<(AclRight, string?)>();
        var mask = DirectoryAccessMask.Normalize(ace.Mask);
        var objectType = ace.ObjectType;
        var appliesTo = ace.InheritedObjectType is { } inherited ? _types.ClassName(inherited) ?? _types.Describe(inherited) : null;
        if (objectType is null && (mask & DirectoryAccessMask.FullControl) == DirectoryAccessMask.FullControl)
        {
            rights.Add((AclRight.GenericAll, appliesTo));
            return rights;
        }

        if ((mask & DirectoryAccessMask.WriteDacl) != 0)
        {
            rights.Add((AclRight.WriteDacl, appliesTo));
        }

        if ((mask & DirectoryAccessMask.WriteOwner) != 0)
        {
            rights.Add((AclRight.WriteOwner, appliesTo));
        }

        if ((mask & DirectoryAccessMask.ControlAccess) != 0)
        {
            if (objectType is not { } right)
            {
                rights.Add((AclRight.AllExtendedRights, appliesTo));
            }
            else if (target.Kind == AclTargetKind.DomainHead && _types.IsRight(right, AclObjectTypes.GetChangesAll))
            {
                rights.Add((AclRight.ReplicationGetChangesAll, null));
            }
            else if (target.Kind == AclTargetKind.DomainHead && _types.IsRight(right, AclObjectTypes.GetChanges))
            {
                rights.Add((AclRight.ReplicationGetChanges, null));
            }
            else if (target.Kind == AclTargetKind.DomainHead && _types.IsRight(right, AclObjectTypes.GetChangesInFilteredSet))
            {
                rights.Add((AclRight.ReplicationGetChangesInFilteredSet, null));
            }
            else if (_types.IsRight(right, AclObjectTypes.ForceChangePassword))
            {
                rights.Add((AclRight.ResetPassword, appliesTo));
            }
            else if (_types.LapsAttribute(right) is { } laps)
            {
                rights.Add((AclRight.ReadLapsPassword, laps));
            }
        }

        if ((mask & DirectoryAccessMask.WriteProperty) != 0)
        {
            if (objectType is not { } property)
            {
                rights.Add((AclRight.WriteAllProperties, appliesTo));
            }
            else
            {
                var known = false;
                if (_types.Covers(property, Attr.Member) && ConcernsGroups(ace, target))
                {
                    rights.Add((AclRight.WriteMember, appliesTo));
                    known = true;
                }

                if (_types.Covers(property, Attr.GpLink) && target.Kind is AclTargetKind.DomainHead or AclTargetKind.Container)
                {
                    rights.Add((AclRight.WriteGpLink, appliesTo));
                    known = true;
                }

                if (_types.Covers(property, AclObjectTypes.KeyCredentialLink))
                {
                    rights.Add((AclRight.WriteKeyCredentialLink, appliesTo));
                    known = true;
                }

                if (_types.Covers(property, Attr.AllowedToActOnBehalfOfOtherIdentity))
                {
                    rights.Add((AclRight.WriteAllowedToActOnBehalf, appliesTo));
                    known = true;
                }

                if (!known && target.Kind == AclTargetKind.GroupPolicy)
                {
                    rights.Add((AclRight.WriteGpoProperty, _types.Describe(property)));
                }
            }
        }

        if ((mask & DirectoryAccessMask.Self) != 0 && ConcernsGroups(ace, target)
            && (objectType is not { } validated ? target.Kind == AclTargetKind.Group || IsGroupClass(ace.InheritedObjectType)
                : _types.IsRight(validated, AclObjectTypes.SelfMembership) || _types.Covers(validated, Attr.Member)))
        {
            rights.Add((AclRight.AddSelfToGroup, appliesTo));
        }

        if ((mask & DirectoryAccessMask.CreateChild) != 0)
        {
            rights.Add((AclRight.CreateChild, objectType is { } created ? _types.ClassName(created) ?? _types.Describe(created) : null));
        }

        if ((mask & DirectoryAccessMask.DeleteChild) != 0)
        {
            rights.Add((AclRight.DeleteChild, objectType is { } deleted ? _types.ClassName(deleted) ?? _types.Describe(deleted) : null));
        }

        return rights;
    }

    /// <summary>The ACE reaches groups: the target is a group, or a container passes it on to groups.</summary>
    private bool ConcernsGroups(SecurityDescriptorAce ace, AclTarget target) =>
        target.Kind == AclTargetKind.Group
        || (target.Kind is AclTargetKind.Container or AclTargetKind.DomainHead && ace.IsContainerInherit
            && (ace.InheritedObjectType is null || IsGroupClass(ace.InheritedObjectType)));

    private bool IsGroupClass(Guid? guid) => guid is { } g && _types.IsClass(g, ObjectClasses.Group);

    /// <summary>
    /// The level of a right (design section 6): control of the domain head, AdminSDHolder, a Tier 0 object or a GPO of the
    /// DCs is Tier 0, of a container or GPO High, of a single account or group what that account or group has (at least
    /// Elevated); DCSync Tier 0; create/delete child Elevated; LAPS High over servers, else Elevated.
    /// </summary>
    private static PrivilegeLevel LevelOf(AclRight right, AclTarget target, bool childrenOnly)
    {
        var tier0 = target.IsTier0 && (!childrenOnly || target.Tier0Subtree);
        return right switch
        {
            AclRight.ReplicationGetChangesAll => PrivilegeLevel.Tier0,
            AclRight.ReplicationGetChanges or AclRight.ReplicationGetChangesInFilteredSet => PrivilegeLevel.High,
            AclRight.CreateChild or AclRight.DeleteChild => PrivilegeLevel.Elevated,
            AclRight.ReadLapsPassword => target.HasServers || tier0 ? PrivilegeLevel.High : PrivilegeLevel.Elevated,
            _ => tier0 ? PrivilegeLevel.Tier0 : target.ControlLevel,
        };
    }

    /// <summary>
    /// A default trustee (SYSTEM, Administrators, Domain/Enterprise Admins, the DCs …), the object itself, or a trustee AD
    /// sets up on this object (<see cref="_provisioningTrustees"/>).
    /// </summary>
    private bool IsDefaultTrustee(string sid, AclTarget target)
    {
        if (DefaultTrusteeSids.Contains(sid)
            || (target.Sid is { } own && string.Equals(own, sid, StringComparison.OrdinalIgnoreCase))
            || (_provisioningTrustees.TryGetValue(GroupGraph.Key(target.Dn), out var provisioned) && provisioned.Contains(sid)))
        {
            return true;
        }

        if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) || AdConvert.GetRid(sid) is not { } rid)
        {
            return false;
        }

        var prefix = sid[..sid.LastIndexOf('-')];
        if (_options.DomainSid is { } domainSid && string.Equals(prefix, domainSid, StringComparison.OrdinalIgnoreCase))
        {
            if (DefaultTrusteeDomainRids.Contains(rid))
            {
                return true;
            }
        }

        var root = _options.RootDomainSid;
        return DefaultTrusteeRootRids.Contains(rid)
            && (root is null ? PrivilegedGroupCatalog.IsDomainSid(prefix) : string.Equals(prefix, root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An explicit ACE the class's defaultSecurityDescriptor gives every new object (CREATOR OWNER there is the owner
    /// here: AD puts the creator in): same trustee, type, object types, no more rights and no wider inheritance.
    /// </summary>
    private bool IsDefault(AclTarget target, SecurityDescriptorAce ace)
    {
        if (target.StructuralClass is null || !_defaults.TryGetValue(target.StructuralClass, out var defaults))
        {
            return false;
        }

        var mask = DirectoryAccessMask.Normalize(ace.Mask);
        foreach (var candidate in defaults)
        {
            var creator = candidate.Sid == "S-1-3-0";
            if (candidate.IsAllow != ace.IsAllow
                || !(string.Equals(candidate.Sid, ace.Sid, StringComparison.OrdinalIgnoreCase)
                    || (creator && target.Owner is { } owner && string.Equals(owner, ace.Sid, StringComparison.OrdinalIgnoreCase)))
                || candidate.ObjectType != ace.ObjectType
                || candidate.InheritedObjectType != ace.InheritedObjectType
                || (mask & ~DirectoryAccessMask.Normalize(candidate.Mask)) != 0)
            {
                continue;
            }

            if (creator || ((ace.IsInheritOnly || !candidate.IsInheritOnly) && (!ace.IsContainerInherit || candidate.IsContainerInherit)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// An inherited ACE whose source is scanned: the nearest scanned ancestor holds an inheritable ACE of the same trustee
    /// and object type with at least these rights (reported - or left out as a default - there).
    /// </summary>
    private bool IsCovered(AclTarget target, SecurityDescriptorAce ace)
    {
        var parent = target.Parsed.Parent;
        while (parent is { IsRoot: false })
        {
            if (_index.TryGetValue(GroupGraph.Key(parent.ToString()), out var source))
            {
                var mask = DirectoryAccessMask.Normalize(ace.Mask);
                return source.Aces.Any(a => a.IsAllow
                    && a.IsContainerInherit
                    && string.Equals(a.Sid, ace.Sid, StringComparison.OrdinalIgnoreCase)
                    && a.ObjectType == ace.ObjectType
                    && (mask & ~DirectoryAccessMask.Normalize(a.Mask)) == 0);
            }

            parent = parent.Parent;
        }

        return false;
    }

    private void Add(AclTarget target, string sid, AclRight right, string? detail, PrivilegeLevel level)
    {
        var key = (GroupGraph.Key(target.Dn), sid.ToUpperInvariant());
        if (!_grants.TryGetValue(key, out var builder))
        {
            _grants[key] = builder = new GrantBuilder(target, sid);
        }

        builder.Add(right, detail, level);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    /// <summary>The highest catalogue level the account or group reaches through the graph (the level of controlling it).</summary>
    private PrivilegeLevel CatalogueLevel(DirectoryObject obj, bool isGroup)
    {
        var graph = _context.Groups;
        var self = new PrivilegePathStep(obj.DistinguishedName, obj.Name, PrivilegePathStepKind.Account);
        var primary = !isGroup && _options.DomainSid is { } domainSid && obj.GetInt64(Attr.PrimaryGroupId) is { } rid and > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{domainSid}-{rid}")
            : null;
        var reached = graph.Reach(self, obj.GetStrings(Attr.MemberOf), primary, includeImplicit: false).Groups.ToList();
        if (isGroup && graph.FindByDn(obj.DistinguishedName) is { } node)
        {
            reached.Add(node);
        }

        var level = PrivilegeLevel.None;
        foreach (var group in reached)
        {
            if (PrivilegedGroupCatalog.Match(group.Sid, group.SamAccountName, _options.DomainSid, _options.RootDomainSid) is { } definition)
            {
                level = PrivilegeLevels.Max(level, _options.LevelOf(definition));
            }
        }

        return level;
    }

    private static bool IsDomainController(DirectoryObject obj) =>
        obj.GetInt32(Attr.UserAccountControl) is { } uac
        && ((UserAccountControl)uac & (UserAccountControl.ServerTrustAccount | UserAccountControl.PartialSecretsAccount)) != 0;

    private static bool IsServer(DirectoryObject obj) =>
        IsDomainController(obj) || obj.GetString(Attr.OperatingSystem)?.Contains("server", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The GPOs an enabled link of a gPLink value points to (DN keys).</summary>
    private static HashSet<string> LinkedGpoKeys(string? gpLink)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(gpLink))
        {
            return keys;
        }

        foreach (var part in gpLink.Split(['[', ']'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.LastIndexOf(';');
            var path = separator < 0 ? part : part[..separator];
            var options = separator >= 0 && int.TryParse(part.AsSpan(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : 0;
            if (path.StartsWith("LDAP://", StringComparison.OrdinalIgnoreCase))
            {
                path = path[7..];
            }

            if ((options & 1) == 0 && DistinguishedName.TryParse(path, out var dn) && !dn.IsRoot)
            {
                keys.Add(GroupGraph.Key(dn.ToString()));
            }
        }

        return keys;
    }

    /// <summary>The rights of one trustee on one object, collected into one grant (level = the highest).</summary>
    private sealed class GrantBuilder(AclTarget target, string sid)
    {
        private readonly List<(AclRight Right, string? Detail, PrivilegeLevel Level)> _rights = [];

        public AclTarget Target { get; } = target;

        public string Sid { get; } = sid;

        public void Add(AclRight right, string? detail, PrivilegeLevel level) => _rights.Add((right, detail, level));

        public PrivilegeGrant Build() => new()
        {
            Source = PrivilegeSource.DirectoryAcl,
            Kind = PrivilegeFindingKind.AclRight,
            Level = PrivilegeLevels.Max(_rights.Select(r => r.Level)),
            Scope = PrivilegeScopeKind.Container,
            ScopeDn = Target.Dn,
            ScopeName = Target.DisplayName,
            PrincipalSid = Sid,
            Values = [.. _rights
                .OrderByDescending(r => r.Level)
                .ThenBy(r => r.Right)
                .ThenBy(r => r.Detail, StringComparer.Ordinal)
                .Select(r => AclRights.Format(r.Right, r.Detail))
                .Distinct(StringComparer.Ordinal)],
        };
    }
}
