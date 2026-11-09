using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.Pki;
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
/// container it comes from (and again at a Tier 0 object where it is worth more). Every trustee becomes a grant
/// (<see cref="PrivilegeFindingKind.AclRight"/>, scope = the object) that the evaluator expands to the members with the
/// path. All reads on the session's DC, nothing kept after the run.
/// </summary>
public sealed class DirectoryAclGrantSource : IPrivilegeGrantSource
{
    public PrivilegeSource Source => PrivilegeSource.DirectoryAcl;

    public Task<IReadOnlyList<PrivilegeGrant>> CollectAsync(PrivilegeScanContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new DirectoryAclScan(context).RunAsync(cancellationToken);
    }

    /// <summary>
    /// The typed PKI entry for ESC5 / ESC14 (P1, docs/dev/p-certificate-services.md 2.3): one scan in a PKI mode – the PKI
    /// subtree of the configuration, the CA computer objects of the window's domain (by DN), the Tier 0 accounts, the
    /// class defaults and the extended rights; no OUs, GPOs, RBCD or deep scan – with the same evaluation rules as
    /// <see cref="CollectAsync"/> (one shared PKI pass), returned as typed grants. All reads on the session's DC.
    /// </summary>
    /// <param name="context">The scan context (session, graph, options).</param>
    /// <param name="caComputerDns">The enterprise CA computers (<c>PkiEnrollmentService.ComputerDn</c>).</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    public Task<IReadOnlyList<PkiAclGrant>> CollectPublicKeyAsync(PrivilegeScanContext context, IReadOnlyList<string> caComputerDns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(caComputerDns);
        return new DirectoryAclScan(context).RunPublicKeyAsync(caComputerDns, cancellationToken);
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

    /// <summary>An object of <c>CN=Public Key Services</c> (P1, docs/dev/p-certificate-services.md 2.3): Tier 0.</summary>
    PublicKey,
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

    /// <summary>
    /// mS-DS-CreatorSID: the account that joined a computer through ms-DS-MachineAccountQuota. Domain Admins own such a
    /// computer; the CREATOR OWNER ACEs of the class name the joiner.
    /// </summary>
    public string? Creator { get; init; }

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

    /// <summary>What the target is in the typed PKI output (<c>CollectPublicKeyAsync</c>); null: not part of it.</summary>
    public PkiAclTargetKind? PkiKind { get; set; }

    public DistinguishedName Parsed => _parsed ??= DistinguishedName.TryParse(Dn, out var dn) ? dn : DistinguishedName.Root;

    private DistinguishedName? _parsed;
}

/// <summary>One run of <see cref="DirectoryAclGrantSource"/>.</summary>
internal sealed class DirectoryAclScan
{
    private const string DefaultSecurityDescriptorAttribute = "defaultSecurityDescriptor";
    private const string LdapDisplayNameAttribute = "lDAPDisplayName";
    private const string CreatorSidAttribute = "mS-DS-CreatorSID";

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

    /// <summary>PKI targets on which Cert Publishers of any forest domain are provisioning trustees (P1 decision 6).</summary>
    private readonly HashSet<string> _certPublisherTargets = new(StringComparer.Ordinal);

    /// <summary>The SIDs of the enterprise CA computers the PKI pass found (Tier 0, P1 decision 4).</summary>
    private readonly List<string> _caComputerSids = [];

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

        // The PKI objects of the configuration and the enterprise CAs' computers (P1, docs/dev/p-certificate-services.md 2.3).
        await ReadPublicKeyInfrastructureAsync(null, cancellationToken).ConfigureAwait(false);

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
        _types = await AclObjectTypes.ReadAsync(_session, PageSize, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The PKI mode of <see cref="DirectoryAclGrantSource.CollectPublicKeyAsync"/> (P1 2.3): the PKI pass of
    /// <see cref="RunAsync"/> with the CA computers given by DN, AdminSDHolder and the Tier 0 accounts, the class defaults
    /// and the extended rights – no OUs, GPOs, RBCD or deep scan – evaluated by the same rules, returned as typed grants.
    /// </summary>
    public async Task<IReadOnlyList<PkiAclGrant>> RunPublicKeyAsync(IReadOnlyList<string> caComputerDns, CancellationToken cancellationToken)
    {
        await ReadPublicKeyInfrastructureAsync(caComputerDns, cancellationToken).ConfigureAwait(false);
        await ReadTier0AccountsAsync(cancellationToken).ConfigureAwait(false);
        await ReadDefaultsAsync(cancellationToken).ConfigureAwait(false);
        _types = await AclObjectTypes.ReadAsync(_session, PageSize, cancellationToken).ConfigureAwait(false);
        foreach (var target in _index.Values.OrderBy(t => t.Dn, StringComparer.Ordinal))
        {
            Evaluate(target);
        }

        return [.. _grants.Values
            .Where(g => g.Target.PkiKind is not null)
            .OrderBy(g => g.Target.Dn, StringComparer.Ordinal)
            .ThenBy(g => g.Sid, StringComparer.Ordinal)
            .SelectMany(g => g.Typed())];
    }

    // ---- PKI (P1, docs/dev/p-certificate-services.md 2.3, decisions 4 and 6) ----------------------------------------

    /// <summary>
    /// The PKI pass shared by U4b and ESC5 / ESC14: one paged subtree search of <c>CN=Public Key Services</c> over the PKI
    /// classes – every object a Tier 0 <see cref="AclTargetKind.PublicKey"/> target, owners reported only for containers,
    /// templates, enrollment services, NTAuth and OID objects with a group link – then the enterprise CAs' computers of
    /// the window's domain (by DN when given, else by the services' <c>dNSHostName</c>) as Tier 0 accounts, and the
    /// provisioning trustees of decision 6: Cert Publishers on AIA / CDP / Certification Authorities / NTAuth / KRA / the
    /// enrollment services, each CA computer on its enrollment service, its CDP host container and CRLs, and the CA store
    /// objects holding its certificate. No PKI (Samba) is no error.
    /// </summary>
    private async Task ReadPublicKeyInfrastructureAsync(IReadOnlyList<string>? caComputerDns, CancellationToken cancellationToken)
    {
        if (!DistinguishedName.TryParse(_session.Info.ConfigurationNamingContext, out var configuration) || configuration.IsRoot)
        {
            return;
        }

        var pki = configuration.Child("CN", "Services").Child("CN", "Public Key Services");
        var ntAuth = pki.Child("CN", "NTAuthCertificates");
        var cdp = pki.Child("CN", "CDP");
        DistinguishedName[] certPublisherScopes =
        [
            pki.Child("CN", "AIA"), cdp, pki.Child("CN", "Certification Authorities"), ntAuth, pki.Child("CN", "KRA"), pki.Child("CN", "Enrollment Services"),
        ];
        var services = new List<(AclTarget? Target, string? Host, IReadOnlyList<byte[]> Certificates)>();
        var stores = new List<(AclTarget Target, IReadOnlyList<byte[]> Certificates)>();
        var crlTargets = new List<(AclTarget Target, string HostLabel)>();
        var filter = LdapFilter.Or(PkiClasses.All.Select(c => LdapFilter.Equal(Attr.ObjectClass, c)));
        try
        {
            await foreach (var obj in SearchAsync(pki.ToString(), SearchScope.Subtree, filter,
                [.. SdAttributes, Attr.DnsHostName, PkiAttributes.CaCertificate, PkiAttributes.OidToGroupLink], cancellationToken).ConfigureAwait(false))
            {
                var kind = PublicKeyKindOf(obj, ntAuth);
                var target = Read(obj, AclTargetKind.PublicKey);
                if (kind == PkiAclTargetKind.EnrollmentService)
                {
                    services.Add((target, obj.GetString(Attr.DnsHostName), Certificates(obj)));
                }

                if (target is null)
                {
                    continue;
                }

                target.IsTier0 = true;
                target.Tier0Subtree = true;
                target.PkiKind = kind;
                // Owners: containers (not the KRA agents, which the typed output also calls Container), templates,
                // enrollment services, NTAuth and OID objects with a group link (CN=OID can hold thousands of objects).
                target.ReportOwner = kind is PkiAclTargetKind.Template or PkiAclTargetKind.EnrollmentService or PkiAclTargetKind.NtAuth
                    || (kind == PkiAclTargetKind.Container && string.Equals(target.StructuralClass, PkiClasses.Container, StringComparison.OrdinalIgnoreCase))
                    || (kind == PkiAclTargetKind.Oid && obj.GetString(PkiAttributes.OidToGroupLink) is { Length: > 0 });
                if (certPublisherScopes.Any(s => target.Parsed.Equals(s) || target.Parsed.IsDescendantOf(s)))
                {
                    _certPublisherTargets.Add(GroupGraph.Key(target.Dn));
                }

                if (kind == PkiAclTargetKind.CertificationAuthority)
                {
                    stores.Add((target, Certificates(obj)));
                }

                if (HostLabelBelow(target.Parsed, cdp) is { } label)
                {
                    crlTargets.Add((target, label));
                }
            }
        }
        catch (DirectoryException ex) when (ex.Kind == DirectoryErrorKind.NoSuchObject)
        {
            // No AD CS in this forest (the Samba lab): nothing to read.
            return;
        }

        var computers = await ReadCaComputersAsync(services.Select(s => s.Host).OfType<string>(), caComputerDns, cancellationToken).ConfigureAwait(false);
        foreach (var (service, host, certificates) in services)
        {
            if (host is null)
            {
                continue;
            }

            foreach (var computer in computers.Where(c => c.Matches(host)))
            {
                if (service is not null)
                {
                    Provision(service, computer.Sid);
                }

                foreach (var (store, _) in stores.Where(s => s.Certificates.Any(c => certificates.Any(own => own.AsSpan().SequenceEqual(c)))))
                {
                    Provision(store, computer.Sid);
                }

                foreach (var (crl, _) in crlTargets.Where(c => string.Equals(c.HostLabel, Label(host), StringComparison.OrdinalIgnoreCase)))
                {
                    Provision(crl, computer.Sid);
                }
            }
        }
    }

    /// <summary>
    /// The enterprise CAs' computer objects of the window's domain (2.1): by DN when the caller resolved them, else one
    /// search by the services' <c>dNSHostName</c> (or <c>sAMAccountName</c> = label$). Each is a Tier 0 account target.
    /// </summary>
    private async Task<List<CaComputer>> ReadCaComputersAsync(IEnumerable<string> hosts, IReadOnlyList<string>? caComputerDns, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> attributes = [.. SdAttributes, Attr.DnsHostName, Attr.SamAccountName, Attr.UserAccountControl];
        var nc = DistinguishedName.Parse(_nc);
        var objects = new List<DirectoryObject>();
        if (caComputerDns is not null)
        {
            foreach (var dn in caComputerDns.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // The window's domain only: a child domain's DN lies below the parent's textually (DC=oddzial,DC=contoso,…),
                // so the DC components must be the window's own.
                if (DistinguishedName.TryParse(dn, out var parsed) && parsed.IsDescendantOf(nc) && parsed.DomainComponent.Equals(nc.DomainComponent)
                    && await _session.GetAsync(dn, attributes, cancellationToken).ConfigureAwait(false) is { } obj)
                {
                    objects.Add(obj);
                }
            }
        }
        else
        {
            // Hosts of the window's domain only: srv.oddzial.contoso.test is in a child domain, not in contoso.test.
            var own = hosts
                .Where(h => h.Split('.', 2) is [_, var domain] && string.Equals(domain.TrimEnd('.'), _session.Info.DomainDnsName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (own.Count > 0)
            {
                var filter = LdapFilter.And(ObjectKind.Computer.ToLdapFilter(), LdapFilter.Or(own.SelectMany(h => new[]
                {
                    LdapFilter.Equal(Attr.DnsHostName, h),
                    LdapFilter.Equal(Attr.SamAccountName, Label(h) + "$"),
                })));
                await foreach (var obj in SearchAsync(_nc, SearchScope.Subtree, filter, attributes, cancellationToken).ConfigureAwait(false))
                {
                    objects.Add(obj);
                }
            }
        }

        var computers = new List<CaComputer>();
        foreach (var obj in objects)
        {
            if (obj.ObjectSid is not { } sid)
            {
                continue;
            }

            _caComputerSids.Add(sid);
            computers.Add(new CaComputer(obj.GetString(Attr.DnsHostName), obj.GetString(Attr.SamAccountName)?.TrimEnd('$'), sid));
            if (Read(obj, AclTargetKind.Account) is { } target)
            {
                target.IsTier0 = true;
                target.ReportOwner = true;
                target.IsDomainController = IsDomainController(obj);
                target.PkiKind = PkiAclTargetKind.CaComputer;
            }
        }

        return computers;
    }

    /// <summary>
    /// The Tier 0 accounts of the PKI mode (ESC14): AdminSDHolder (its DACL is stamped on every protected account), the
    /// domain controllers, Administrator, krbtgt and the direct members of every group that is Tier 0 by decision 4 (the
    /// classifier's graph closure: nested members are direct members of a group of the closure).
    /// </summary>
    private async Task ReadTier0AccountsAsync(CancellationToken cancellationToken)
    {
        var nc = DistinguishedName.Parse(_nc);
        var adminSdHolder = nc.Child("CN", "System").Child("CN", "AdminSDHolder").ToString();
        if (await _session.GetAsync(adminSdHolder, SdAttributes, cancellationToken).ConfigureAwait(false) is { } holder
            && Read(holder, AclTargetKind.Container) is { } holderTarget)
        {
            holderTarget.IsTier0 = true;
            holderTarget.ReportOwner = true;
            holderTarget.PkiKind = PkiAclTargetKind.Tier0Account;
        }

        var classifier = new PrincipalClassifier(_options.DomainSid, _options.RootDomainSid, _options, _context.Groups, _caComputerSids);
        var parts = new List<string> { PrivilegedAccessScanner.DomainControllerFilter };
        if (_options.DomainSid is { } domainSid)
        {
            parts.Add(LdapFilter.Equal(Attr.ObjectSid, string.Create(CultureInfo.InvariantCulture, $"{domainSid}-{WellKnownRids.Administrator}")));
            parts.Add(LdapFilter.Equal(Attr.ObjectSid, string.Create(CultureInfo.InvariantCulture, $"{domainSid}-{WellKnownRids.Krbtgt}")));
        }

        foreach (var node in _context.Groups.Nodes.Where(n => !n.IsImplicit && n.Sid is not null).OrderBy(n => n.Dn, StringComparer.OrdinalIgnoreCase))
        {
            if (classifier.Classify(node.Sid!) == PrincipalClass.Tier0)
            {
                parts.Add(LdapFilter.Equal(Attr.MemberOf, node.Dn));
            }
        }

        await foreach (var obj in SearchAsync(_nc, SearchScope.Subtree, LdapFilter.And(PrivilegedAccessScanner.AccountFilter, LdapFilter.Or(parts)),
            [.. SdAttributes, Attr.UserAccountControl], cancellationToken).ConfigureAwait(false))
        {
            if (Read(obj, AclTargetKind.Account) is { } target)
            {
                target.IsTier0 = true;
                target.ReportOwner = true;
                target.IsDomainController = IsDomainController(obj);
                target.PkiKind = PkiAclTargetKind.Tier0Account;
            }
        }
    }

    /// <summary>What a PKI object is in the typed output (the KRA agents and anything else of the subtree: container).</summary>
    private static PkiAclTargetKind PublicKeyKindOf(DirectoryObject obj, DistinguishedName ntAuth)
    {
        var classes = obj.GetStrings(Attr.ObjectClass);
        bool Is(string name) => classes.Contains(name, StringComparer.OrdinalIgnoreCase);
        return Is(PkiClasses.CertificateTemplate) ? PkiAclTargetKind.Template
            : Is(PkiClasses.EnrollmentService) ? PkiAclTargetKind.EnrollmentService
            : Is(PkiClasses.CrlDistributionPoint) ? PkiAclTargetKind.CrlDistributionPoint
            : Is(PkiClasses.EnterpriseOid) ? PkiAclTargetKind.Oid
            : Is(PkiClasses.CertificationAuthority)
                ? (DistinguishedName.TryParse(obj.DistinguishedName, out var dn) && dn.Equals(ntAuth) ? PkiAclTargetKind.NtAuth : PkiAclTargetKind.CertificationAuthority)
            : PkiAclTargetKind.Container;
    }

    /// <summary>The RDN value of the host container below <c>CN=CDP</c> the target is (or is in), else null.</summary>
    private static string? HostLabelBelow(DistinguishedName target, DistinguishedName cdp)
    {
        for (var current = target; current.Parent is { IsRoot: false } parent; current = parent)
        {
            if (parent.Equals(cdp))
            {
                return current.Name;
            }
        }

        return null;
    }

    private static IReadOnlyList<byte[]> Certificates(DirectoryObject obj) =>
        obj.TryGet(PkiAttributes.CaCertificate, out var values) ? [.. values.OfType<byte[]>()] : [];

    /// <summary>The first DNS label of a host name.</summary>
    private static string Label(string host) => host.Split('.', 2)[0];

    private void Provision(AclTarget target, string sid)
    {
        var key = GroupGraph.Key(target.Dn);
        if (!_provisioningTrustees.TryGetValue(key, out var trustees))
        {
            _provisioningTrustees[key] = trustees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        trustees.Add(sid);
    }

    /// <summary>
    /// Decision 6: the forest root's Domain Admins (owners of the default templates and containers; any domain's while the
    /// root is unknown) on every PKI object, Cert Publishers of any forest domain on <see cref="_certPublisherTargets"/>.
    /// </summary>
    private bool IsPublicKeyProvisioningTrustee(string sid, AclTarget target)
    {
        if (PrincipalClassifier.DomainRid(sid) is not { } rid)
        {
            return false;
        }

        var prefix = sid[..sid.LastIndexOf('-')];
        return rid switch
        {
            WellKnownRids.DomainAdmins => _options.RootDomainSid is not { } root || string.Equals(prefix, root, StringComparison.OrdinalIgnoreCase),
            WellKnownRids.CertPublishers => _certPublisherTargets.Contains(GroupGraph.Key(target.Dn)),
            _ => false,
        };
    }

    private static bool IsTemplate(AclTarget target) =>
        target.Kind == AclTargetKind.PublicKey && string.Equals(target.StructuralClass, PkiClasses.CertificateTemplate, StringComparison.OrdinalIgnoreCase);

    /// <summary>A CA computer read by the PKI pass (the names it is matched to its enrollment service by).</summary>
    private sealed record CaComputer(string? DnsHostName, string? SamLabel, string Sid)
    {
        public bool Matches(string host) =>
            string.Equals(DnsHostName, host, StringComparison.OrdinalIgnoreCase) || string.Equals(SamLabel, Label(host), StringComparison.OrdinalIgnoreCase);

        public override string ToString() => nameof(CaComputer);
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
                Creator = obj.GetBytes(CreatorSidAttribute) is { Length: >= 8 } creator ? AdConvert.SidToString(creator) : null,
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
            [.. SdAttributes, Attr.UserAccountControl, Attr.OperatingSystem, Attr.MemberOf, Attr.PrimaryGroupId, Attr.SamAccountName, CreatorSidAttribute],
            cancellationToken)
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

            if (ace.IsInherited ? IsReportedAtSource(target, ace) : IsDefault(target, ace))
            {
                continue;
            }

            var childrenOnly = ChildrenOnly(target, ace);
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

    /// <summary>Inherit-only: for the children only - unless they include the domain controllers' accounts.</summary>
    private bool ChildrenOnly(AclTarget target, SecurityDescriptorAce ace) =>
        ace.IsInheritOnly && !(target.HoldsDomainControllers
            && (ace.InheritedObjectType is not { } inherited || _types.IsClass(inherited, ObjectClasses.Computer)));

    /// <summary>The level the target's grant gets from the ACE (<see cref="PrivilegeLevel.None"/> without a right that matters).</summary>
    private PrivilegeLevel LevelAt(AclTarget target, SecurityDescriptorAce ace)
    {
        var childrenOnly = ChildrenOnly(target, ace);
        return PrivilegeLevels.Max(RightsOf(ace, target).Select(r => LevelOf(r.Right, target, childrenOnly)));
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
                // P1 (2.3): the only extended rights of PKI objects are Enroll / AutoEnroll – enrollment, not control.
                if (target.Kind != AclTargetKind.PublicKey)
                {
                    rights.Add((AclRight.AllExtendedRights, appliesTo));
                }
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
            else if (target.Kind == AclTargetKind.PublicKey && _types.ClassName(property) is { } objectClass
                && string.Equals(objectClass, target.StructuralClass, StringComparison.OrdinalIgnoreCase))
            {
                // P1 (2.3): the object's own class as object type covers every property (the root of AccessCheck's object type list).
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

                // P1 (2.3): explicit certificate mappings (ESC14) on any target – the attribute or its property set (Public-Information).
                if (_types.Covers(property, AclObjectTypes.AltSecurityIdentities))
                {
                    rights.Add((AclRight.WriteAltSecurityIdentities, appliesTo));
                    known = true;
                }

                // P1 (2.3): the ESC4-sensitive template attributes; a GUID nothing can name counts as sensitive (conservative).
                if (!known && IsTemplate(target))
                {
                    var sensitive = PkiAttributes.SensitiveTemplateAttributes.Where(a => _types.Covers(property, a)).ToList();
                    foreach (var attribute in sensitive)
                    {
                        rights.Add((AclRight.WriteTemplateProperty, attribute));
                    }

                    if (sensitive.Count == 0 && string.Equals(_types.Describe(property), property.ToString("D"), StringComparison.Ordinal))
                    {
                        rights.Add((AclRight.WriteTemplateProperty, property.ToString("D")));
                    }
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
            || (_provisioningTrustees.TryGetValue(GroupGraph.Key(target.Dn), out var provisioned) && provisioned.Contains(sid))
            || (target.Kind == AclTargetKind.PublicKey && IsPublicKeyProvisioningTrustee(sid, target)))
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
    /// here - AD puts the creator in - or the joiner of a computer created through the quota): same trustee, type, object
    /// types, no more rights and no wider inheritance.
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
                    || (creator && (string.Equals(target.Owner, ace.Sid, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(target.Creator, ace.Sid, StringComparison.OrdinalIgnoreCase))))
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
    /// and object type with at least these rights (reported - or left out as a default - there). It is reported here as
    /// well when it is worth more here: it applies to this object (not inherit-only) and gives it a higher level than at
    /// the source - member write on a Tier 0 group of CN=Users, full control of OU=Domain Controllers from the domain head,
    /// reset password on a Tier 0 account of the deep scan (not protected by AdminSDHolder, so it inherits).
    /// </summary>
    private bool IsReportedAtSource(AclTarget target, SecurityDescriptorAce ace)
    {
        var parent = target.Parsed.Parent;
        while (parent is { IsRoot: false })
        {
            if (_index.TryGetValue(GroupGraph.Key(parent.ToString()), out var source))
            {
                var mask = DirectoryAccessMask.Normalize(ace.Mask);
                var covering = source.Aces.FirstOrDefault(a => a.IsAllow
                    && a.IsContainerInherit
                    && string.Equals(a.Sid, ace.Sid, StringComparison.OrdinalIgnoreCase)
                    && a.ObjectType == ace.ObjectType
                    && (mask & ~DirectoryAccessMask.Normalize(a.Mask)) == 0);
                return covering is not null
                    && (ace.IsInheritOnly || IsDefaultTrustee(covering.Sid, source) || (!covering.IsInherited && IsDefault(source, covering))
                        || LevelAt(target, ace) <= LevelAt(source, covering));
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

        /// <summary>One typed grant per right and detail (P1 <c>CollectPublicKeyAsync</c>); <see cref="AclTarget.PkiKind"/> is set.</summary>
        public IEnumerable<PkiAclGrant> Typed() => _rights
            .Select(r => (r.Right, r.Detail))
            .Distinct()
            .OrderBy(r => r.Right)
            .ThenBy(r => r.Detail, StringComparer.Ordinal)
            .Select(r => new PkiAclGrant(Target.Dn, Target.PkiKind!.Value, Sid, r.Right, r.Detail));
    }
}
