using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Testing.Pki;

namespace AdMng.Testing;

/// <summary>The template cn values of <see cref="FakeDirectoryBuilder.AddPublicKeySample"/> by role.</summary>
public static class PublicKeyTemplates
{
    // Windows defaults with their default ACLs: no finding.
    public const string User = "User";
    public const string Machine = "Machine";
    public const string DomainController = "DomainController";
    public const string KerberosAuthentication = "KerberosAuthentication";
    public const string WebServer = "WebServer";
    public const string SubCa = "SubCA";

    /// <summary>Smart-card logon, Domain Admins only; published only by the root CA, which is not in NTAuth (readiness).</summary>
    public const string SmartCardLogon = "Logowanie-Kartą";

    /// <summary>ESC1: enrollee supplies the subject, client authentication, Domain Users enroll.</summary>
    public const string Esc1 = "Użytkownik-VPN-Podatny";

    /// <summary>ESC1 look-alike: the same with CA manager approval.</summary>
    public const string Esc1Safe = "Użytkownik-VPN-Bezpieczny";

    /// <summary>ESC1 on a template no enterprise CA publishes (one level lower, NotPublished).</summary>
    public const string Esc1NotPublished = "Użytkownik-VPN-Niewystawiany";

    /// <summary>ESC2: Any Purpose, enrollable by GG_IT (other); it is an enrollment agent too (ESC3).</summary>
    public const string Esc2 = "Dowolny-Cel-Podatny";

    /// <summary>ESC2 look-alike: Any Purpose, Domain Admins only.</summary>
    public const string Esc2Safe = "Dowolny-Cel-Bezpieczny";

    /// <summary>ESC3 (condition 1): Certificate Request Agent, Domain Users enroll.</summary>
    public const string Esc3 = "Agent-Rejestracji-Podatny";

    /// <summary>ESC3 look-alike: Certificate Request Agent with CA manager approval.</summary>
    public const string Esc3Safe = "Agent-Rejestracji-Bezpieczny";

    /// <summary>ESC3 (condition 2): schema 2, one agent signature with Certificate Request Agent, client authentication, Domain Users enroll.</summary>
    public const string Esc3Target = "Logowanie-W-Imieniu-Podatne";

    /// <summary>ESC3 target look-alike: schema 4 (backquote <c>msPKI-RA-Application-Policies</c>) with CA manager approval.</summary>
    public const string Esc3TargetSafe = "Logowanie-W-Imieniu-Bezpieczne";

    /// <summary>ESC4: Authenticated Users may write <c>msPKI-Certificate-Name-Flag</c>.</summary>
    public const string Esc4 = "Szablon-Zapisywalny-Podatny";

    /// <summary>ESC4 look-alike: only Enterprise Admins (Tier 0) may write <c>msPKI-Certificate-Name-Flag</c>.</summary>
    public const string Esc4Safe = "Szablon-Zapisywalny-Bezpieczny";

    /// <summary>ESC9: no security extension (0x80000), client authentication, Domain Users enroll.</summary>
    public const string Esc9 = "Bez-Rozszerzenia-SID-Podatny";

    /// <summary>ESC9 look-alike: the same with the flag clear.</summary>
    public const string Esc9Safe = "Bez-Rozszerzenia-SID-Bezpieczny";

    /// <summary>ESC13: the issuance policy <c>Polityka wysokiej pewności</c> linked to a Tier 0 group, client authentication, GG_IT enrolls.</summary>
    public const string Esc13 = "Wysoka-Pewność-Podatny";

    /// <summary>ESC13 look-alike: the issuance policy <c>Polityka średniej pewności</c> without a group link.</summary>
    public const string Esc13Safe = "Wysoka-Pewność-Bezpieczny";

    /// <summary>ESC15: schema 1, enrollee supplies the subject, secure e-mail only, Domain Users enroll.</summary>
    public const string Esc15 = "Stary-Szablon-V1-Podatny";

    /// <summary>ESC15 look-alike: the same as schema 2.</summary>
    public const string Esc15Safe = "Stary-Szablon-V1-Bezpieczny";

    /// <summary>ESC17: enrollee supplies the subject, server authentication, Domain Computers enroll.</summary>
    public const string Esc17 = "Serwer-WWW-Podatny";

    /// <summary>ESC17 look-alike: the same, Domain Admins only.</summary>
    public const string Esc17Safe = "Serwer-WWW-Bezpieczny";

    /// <summary>The Windows default templates (no finding).</summary>
    public static IReadOnlyList<string> Defaults { get; } = [User, Machine, DomainController, KerberosAuthentication, WebServer, SubCa, SmartCardLogon];

    /// <summary>One deliberately vulnerable template per LDAP ESC (and the unpublished ESC1).</summary>
    public static IReadOnlyList<string> Vulnerable { get; } = [Esc1, Esc1NotPublished, Esc2, Esc3, Esc3Target, Esc4, Esc9, Esc13, Esc15, Esc17];

    /// <summary>The safe look-alikes (must not be flagged).</summary>
    public static IReadOnlyList<string> Safe { get; } = [Esc1Safe, Esc2Safe, Esc3Safe, Esc3TargetSafe, Esc4Safe, Esc9Safe, Esc13Safe, Esc15Safe, Esc17Safe];

    /// <summary>Every template of the sample.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Defaults, .. Vulnerable, .. Safe];

    /// <summary>The templates of the root CA (the issuing CA publishes every other one except <see cref="Esc1NotPublished"/>).</summary>
    public static IReadOnlyList<string> PublishedByRoot { get; } = [SubCa, SmartCardLogon];

    /// <summary>The templates of the issuing CA.</summary>
    public static IReadOnlyList<string> PublishedByIssuing { get; } =
        [.. All.Where(t => t != Esc1NotPublished && !PublishedByRoot.Contains(t, StringComparer.Ordinal))];
}

/// <summary>
/// AD CS (P1, docs/dev/p-certificate-services.md 2.10): the forest PKI of the sample company (<see cref="AddPublicKeySample"/>)
/// and the ACL sample of the U4b PKI pass (<see cref="AddPublicKeyAclSample"/>). Synthetic data only (certificates from
/// <see cref="PkiSamples"/>); call one of the two per builder.
/// </summary>
public sealed partial class FakeDirectoryBuilder
{
    /// <summary>
    /// Simplified defaultSecurityDescriptor of the PKI classes (Domain Admins and SYSTEM full control, Authenticated Users
    /// read) – what U4b's class defaults read for the PKI pass (2.3).
    /// </summary>
    public static IReadOnlyDictionary<string, string> PublicKeyDefaultSecurityDescriptors { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["pKICertificateTemplate"] = "D:(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;DA)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;SY)(A;;RPLCLORC;;;AU)",
        ["pKIEnrollmentService"] = "D:(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;DA)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;SY)(A;;RPLCLORC;;;AU)",
        ["certificationAuthority"] = "D:(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;DA)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;SY)(A;;RPLCLORC;;;AU)",
        ["cRLDistributionPoint"] = "D:(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;DA)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;SY)(A;;RPLCLORC;;;AU)",
        ["msPKI-Enterprise-Oid"] = "D:(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;DA)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;SY)(A;;RPLCLORC;;;AU)",
        ["msPKI-PrivateKeyRecoveryAgent"] = "D:(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;DA)(A;;RPWPCRCCDCLCLORCWOWDSDDTSW;;;SY)(A;;RPLCLORC;;;AU)",
    };

    /// <summary>
    /// Seeds the forest PKI of the sample company (2.10; seeds <see cref="AddWatchdogInfrastructure"/> first when the
    /// configuration is empty, and the class defaults and extended rights of <see cref="AddAclDefinitions"/>):
    /// <list type="bullet">
    /// <item>the enterprise CAs <i>Kontoso Główny CA</i> (SRV-CA01) and <i>Kontoso Wystawiający CA</i> (SRV-CA02) with their
    /// computer objects in <c>OU=Serwery PKI</c> (members of Cert Publishers) and CES on the issuing CA;</item>
    /// <item><c>CN=Certification Authorities</c> (the root), <c>CN=AIA</c> (both, certutil-sanitized names), NTAuth (the
    /// issuing CA only – the root CA publishes a logon template and is not in NTAuth), <c>CN=CDP/SRV-CA0n</c> (the root's
    /// base CRL, the issuing CA's base and delta CRL), <c>CN=KRA</c> (one agent) and <c>CN=OID</c> (the issuance policies
    /// <i>Polityka wysokiej pewności</i>, linked to the Tier 0 group GG_Administratorzy_PKI, and <i>Polityka średniej
    /// pewności</i>, not linked);</item>
    /// <item>the templates of <see cref="PublicKeyTemplates"/>: the Windows defaults, one deliberately vulnerable template per
    /// LDAP ESC and a safe look-alike, with binary security descriptors;</item>
    /// <item>default-PKI ACLs on every PKI object (root Domain Admins, Enterprise Admins, Cert Publishers, the CA computer)
    /// that yield no finding, plus ESC5 (GG_IT may write NTAuth) and ESC14 (GG_IT may write altSecurityIdentities of
    /// Administrator; a look-alike on Łukasz Wiśniewski, not Tier 0);</item>
    /// <item>mappings: Administrator weak (issuer + subject, Tier 0), Łukasz Wiśniewski weak (RFC 822), Jan Kowalski strong
    /// (issuer + serial), Paweł Lewandowski Kerberos; published certificates (Jan valid, Grażyna Dąbrowska expired, DC01 the
    /// KDC certificate, the contact Zofia Żurawska), key credentials (Jan, KRK-LT001) and a <c>userPKCS12</c> blob on Jan.</item>
    /// </list>
    /// </summary>
    public PublicKeySample AddPublicKeySample(SampleCompany company)
    {
        ArgumentNullException.ThrowIfNull(company);
        var pki = SeedPublicKeyInfrastructure(company);
        var da = $"{DomainSid}-{WellKnownRids.DomainAdmins}";
        var ea = pki.EnterpriseAdminsSid;
        var domainUsers = $"{DomainSid}-{WellKnownRids.DomainUsers}";
        var domainComputers = $"{DomainSid}-{WellKnownRids.DomainComputers}";
        var it = SidOf(company.GroupIt);
        var nameFlagGuid = Session.Schema.FindAttribute("msPKI-Certificate-Name-Flag")!.SchemaIdGuid;
        var altSecurityGuid = Session.Schema.FindAttribute("altSecurityIdentities")!.SchemaIdGuid;

        // ESC13: the issuance policies of CN=OID; the high-assurance one is linked to a Tier 0 group.
        // A universal group can only nest in a universal or domain local group: Enterprise Admins (universal) makes it Tier 0.
        var linkedGroup = AddGroup("GG_Administratorzy_PKI", company.GroupsOu, GroupScope.Universal);
        AddMembers(pki.EnterpriseAdmins, linkedGroup);
        var highOid = PkiSamples.ForestOid + ".500";
        var mediumOid = PkiSamples.ForestOid + ".501";
        var highAssurance = Seed(Dn("CN", "500.8D5E1F0A2B3C4D5E6F708192A3B4C5D6", pki.OidContainer), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "msPKI-Enterprise-Oid" },
            [Attr.DisplayName] = "Polityka wysokiej pewności",
            ["flags"] = 2,
            ["msPKI-Cert-Template-OID"] = highOid,
            ["msDS-OIDToGroupLink"] = linkedGroup,
            [Attr.NtSecurityDescriptor] = PkiObjectSecurity(da, ea, null),
        }, null);
        var mediumAssurance = Seed(Dn("CN", "501.1A2B3C4D5E6F708192A3B4C5D6E7F801", pki.OidContainer), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "msPKI-Enterprise-Oid" },
            [Attr.DisplayName] = "Polityka średniej pewności",
            ["flags"] = 2,
            ["msPKI-Cert-Template-OID"] = mediumOid,
            [Attr.NtSecurityDescriptor] = PkiObjectSecurity(da, ea, null),
        }, null);

        var templates = new Dictionary<string, string>(pki.Templates, StringComparer.Ordinal);
        var number = 30;
        void Add(string cn, int schema, int nameFlag, int enrollmentFlag, string[] ekus, FakeAce[] aces, IReadOnlyDictionary<string, object?>? more = null)
        {
            var attributes = TemplateAttributes(cn, schema, nameFlag, enrollmentFlag, ekus, ++number, machine: false);
            foreach (var (key, value) in more ?? new Dictionary<string, object?>())
            {
                attributes[key] = value;
            }

            attributes[Attr.NtSecurityDescriptor] = TemplateSecurity(da, ea, aces);
            templates[cn] = AddCertificateTemplate(cn, attributes);
        }

        const int Ess = 0x1;
        const int AdSubject = unchecked((int)0x82000000);
        const int Approval = 0x2;
        string[] clientAuth = [PkiSamples.ClientAuthentication];
        FakeAce[] usersEnroll = [Enroll(domainUsers)];
        FakeAce[] adminsOnly = [];

        Add(PublicKeyTemplates.Esc1, 2, Ess, 0, clientAuth, usersEnroll);
        Add(PublicKeyTemplates.Esc1Safe, 2, Ess, Approval, clientAuth, usersEnroll);
        Add(PublicKeyTemplates.Esc1NotPublished, 2, Ess, 0, clientAuth, usersEnroll);
        Add(PublicKeyTemplates.Esc2, 2, AdSubject, 0, [PkiSamples.AnyPurpose], [Enroll(it)]);
        Add(PublicKeyTemplates.Esc2Safe, 2, AdSubject, 0, [PkiSamples.AnyPurpose], adminsOnly);
        Add(PublicKeyTemplates.Esc3, 2, AdSubject, 0, [PkiSamples.CertificateRequestAgent], usersEnroll);
        Add(PublicKeyTemplates.Esc3Safe, 2, AdSubject, Approval, [PkiSamples.CertificateRequestAgent], usersEnroll);
        Add(PublicKeyTemplates.Esc3Target, 2, AdSubject, 0, [PkiSamples.ClientAuthentication, PkiSamples.SmartCardLogon], usersEnroll,
            new Dictionary<string, object?> { ["msPKI-RA-Signature"] = 1, ["msPKI-RA-Application-Policies"] = PkiSamples.CertificateRequestAgent });
        Add(PublicKeyTemplates.Esc3TargetSafe, 4, AdSubject, Approval, [PkiSamples.ClientAuthentication, PkiSamples.SmartCardLogon], usersEnroll,
            new Dictionary<string, object?>
            {
                ["msPKI-RA-Signature"] = 1,
                ["msPKI-RA-Application-Policies"] =
                    "msPKI-RA-Application-Policies`PZPWSTR`" + PkiSamples.CertificateRequestAgent
                    + "`msPKI-Asymmetric-Algorithm`PZPWSTR`RSA`msPKI-Hash-Algorithm`PZPWSTR`SHA256`msPKI-Key-Usage`DWORD`16777215"
                    + "`msPKI-Symmetric-Algorithm`PZPWSTR`3DES`msPKI-Symmetric-Key-Length`DWORD`168`",
            });
        Add(PublicKeyTemplates.Esc4, 2, AdSubject, 0, clientAuth, [new(FakeAce.AccessAllowedObject, 0, 0x20, "S-1-5-11", nameFlagGuid)]);
        Add(PublicKeyTemplates.Esc4Safe, 2, AdSubject, 0, clientAuth, [new(FakeAce.AccessAllowedObject, 0, 0x20, ea, nameFlagGuid)]);
        Add(PublicKeyTemplates.Esc9, 2, AdSubject, 0x80000, clientAuth, usersEnroll);
        Add(PublicKeyTemplates.Esc9Safe, 2, AdSubject, 0, clientAuth, usersEnroll);
        Add(PublicKeyTemplates.Esc13, 2, AdSubject, 0, clientAuth, [Enroll(it)], new Dictionary<string, object?> { ["msPKI-Certificate-Policy"] = highOid });
        Add(PublicKeyTemplates.Esc13Safe, 2, AdSubject, 0, clientAuth, [Enroll(it)], new Dictionary<string, object?> { ["msPKI-Certificate-Policy"] = mediumOid });
        Add(PublicKeyTemplates.Esc15, 1, Ess, 0, [PkiSamples.SecureEmail], usersEnroll);
        Add(PublicKeyTemplates.Esc15Safe, 2, Ess, 0, [PkiSamples.SecureEmail], usersEnroll);
        Add(PublicKeyTemplates.Esc17, 2, Ess, 0, [PkiSamples.ServerAuthentication], [Enroll(domainComputers)]);
        Add(PublicKeyTemplates.Esc17Safe, 2, Ess, 0, [PkiSamples.ServerAuthentication], adminsOnly);

        // ESC5: GG_IT may write NTAuth (other: Medium); Cert Publishers write CN=AIA as AD sets it up (no finding).
        Session.SetValues(NtAuthCertificatesDn, Attr.NtSecurityDescriptor,
            PkiObjectSecurity(da, ea, pki.CertPublishersSid, new FakeAce(FakeAce.AccessAllowed, 0, 0x20, it)));

        // ESC14: weak and strong explicit mappings; GG_IT may write altSecurityIdentities of Administrator (Tier 0) and of
        // Łukasz Wiśniewski (not Tier 0: no finding).
        // The issuer and subject in the reversed RDN order of altSecurityIdentities; <SR> is the serial in reversed byte
        // order (Jan's published certificate has the serial 0x6300).
        var issuer = "C=PL,O=" + PkiSamples.Organization + ",CN=" + PkiSamples.IssuingCaName;
        Session.SetValues(AdministratorDn, PkiAltSecurityIdentities, $"X509:<I>{issuer}<S>DC=test,DC=contoso,CN=Users,CN=Administrator");
        Session.SetValues(company.LukaszWisniewski, PkiAltSecurityIdentities, "X509:<RFC822>lukasz.wisniewski@contoso.test");
        Session.SetValues(company.JanKowalski, PkiAltSecurityIdentities, $"X509:<I>{issuer}<SR>0063");
        Session.SetValues(company.PawelLewandowski, PkiAltSecurityIdentities, "Kerberos:pawel.lewandowski@CONTOSO.TEST");
        FakeAce[] accountDefaults = [FakeAce.FullControl(da), FakeAce.FullControl("S-1-5-18"), FakeAce.FullControl("S-1-5-32-544"), new(FakeAce.AccessAllowed, 0, 0x20094, "S-1-5-11")];
        SetSecurityDescriptor(AdministratorDn, da, [.. accountDefaults, new(FakeAce.AccessAllowedObject, 0, 0x20, it, altSecurityGuid)]);
        SetSecurityDescriptor(company.LukaszWisniewski, da, [.. accountDefaults, new(FakeAce.AccessAllowedObject, 0, 0x20, it, altSecurityGuid)]);

        // Published certificates, key credentials and key material (W7's tabs, the readiness report).
        var janSid = SidOf(company.JanKowalski);
        Session.SetValues(company.JanKowalski, PkiUserCertificate,
            PkiSamples.UserCertificateDer("Jan Kowalski", "jan.kowalski@contoso.test", janSid, 0x6300, new(2025, 10, 30, 8, 0, 0, TimeSpan.Zero), new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero)));
        Session.SetValues(company.GrazynaDabrowska, PkiUserCertificate,
            PkiSamples.UserCertificateDer("Grażyna Dąbrowska", "grazyna.dabrowska@contoso.test", SidOf(company.GrazynaDabrowska), 0x6301,
                new(2025, 9, 1, 8, 0, 0, TimeSpan.Zero), new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero)));
        Session.SetValues(company.JanKowalski, "userPKCS12", new byte[] { 0x30, 0x80, 0x02, 0x01, 0x03, 0x00, 0x00 });
        Session.SetValues(company.JanKowalski, "msDS-KeyCredentialLink",
            PkiSamples.KeyCredentialLinkValue(company.JanKowalski, [0x4A, 0x61, 0x6E, 0x2D, 0x4B, 0x6C, 0x75, 0x63, 0x7A], new Guid("6d0f7c2e-8a41-4b4b-9c3e-1f2a3b4c5d6e"),
                new(2026, 5, 4, 7, 15, 0, TimeSpan.Zero), new(2026, 10, 12, 6, 50, 0, TimeSpan.Zero)));
        Session.SetValues(company.Laptop, "msDS-KeyCredentialLink",
            PkiSamples.KeyCredentialLinkValue(company.Laptop, [0x4B, 0x52, 0x4B, 0x2D, 0x4C, 0x54], new Guid("2b9e4f61-0c7d-4e8a-b1f2-3a4b5c6d7e8f"),
                new(2026, 4, 20, 9, 0, 0, TimeSpan.Zero)));
        Session.SetValues(DomainControllerDn, PkiUserCertificate, PkiSamples.DcKdcDer());
        var contact = AddContact("Zofia", "Żurawska", company.SalesOu, new Dictionary<string, object?>
        {
            [Attr.Mail] = "zofia.zurawska@partner.example",
            [PkiUserCertificate] = PkiSamples.IssueDer(new PkiCertificateOptions
            {
                CommonName = "Zofia Żurawska",
                SerialNumber = 0x6302,
                NotBefore = new(2026, 2, 1, 8, 0, 0, TimeSpan.Zero),
                NotAfter = new(2027, 2, 1, 8, 0, 0, TimeSpan.Zero),
                Ekus = [PkiSamples.SecureEmail],
                Email = "zofia.zurawska@partner.example",
                Issuer = PkiSamples.IssuingCa,
            }),
        });

        return pki with
        {
            Templates = templates,
            HighAssuranceOid = highAssurance,
            MediumAssuranceOid = mediumAssurance,
            LinkedGroup = linkedGroup,
            WeakTier0Account = AdministratorDn,
            WeakAccount = company.LukaszWisniewski,
            StrongAccount = company.JanKowalski,
            KerberosMappedAccount = company.PawelLewandowski,
            ExpiredCertificateAccount = company.GrazynaDabrowska,
            Contact = contact,
        };
    }

    /// <summary>
    /// The ACL sample of the U4b PKI pass and <c>CollectPublicKeyAsync</c> (2.3, 2.10): the default PKI of
    /// <see cref="AddPublicKeySample"/> (CAs, containers, stores, CDP, KRA, the Windows default templates with their default
    /// ACLs – no finding, also from a child-domain window) plus one delegation per target kind: GG_Sprzedaz may write
    /// <c>CN=Public Key Services</c> (container) and owns <c>CN=OID</c>'s policy object, GG_IT may write NTAuth, GG_Ksiegowosc
    /// has full control of the issuing CA's enrollment service, Authenticated Users may write <c>msPKI-Enrollment-Flag</c> of
    /// <see cref="PublicKeyTemplates.User"/> (<c>WriteTemplateProperty</c>), GG_IT has full control of SRV-CA02 (the CA
    /// computer) and may write <c>altSecurityIdentities</c> of Administrator (<c>WriteAltSecurityIdentities</c>).
    /// </summary>
    public PublicKeyAclSample AddPublicKeyAclSample(SampleCompany company)
    {
        ArgumentNullException.ThrowIfNull(company);
        var pki = SeedPublicKeyInfrastructure(company);
        var da = $"{DomainSid}-{WellKnownRids.DomainAdmins}";
        var ea = pki.EnterpriseAdminsSid;
        var it = SidOf(company.GroupIt);
        var sales = SidOf(company.GroupSales);
        var accounting = SidOf(company.GroupAccounting);
        var enrollmentFlagGuid = Session.Schema.FindAttribute("msPKI-Enrollment-Flag")!.SchemaIdGuid;
        var altSecurityGuid = Session.Schema.FindAttribute("altSecurityIdentities")!.SchemaIdGuid;

        Session.SetValues(PublicKeyServicesDn, Attr.NtSecurityDescriptor, PkiObjectSecurity(da, ea, null, new FakeAce(FakeAce.AccessAllowed, 0x02, 0x20, sales)));
        Session.SetValues(NtAuthCertificatesDn, Attr.NtSecurityDescriptor, PkiObjectSecurity(da, ea, pki.CertPublishersSid, new FakeAce(FakeAce.AccessAllowed, 0, 0x20, it)));
        var policy = Seed(Dn("CN", "600.0F1E2D3C4B5A69788796A5B4C3D2E1F0", pki.OidContainer), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "msPKI-Enterprise-Oid" },
            [Attr.DisplayName] = "Polityka działu sprzedaży",
            ["flags"] = 2,
            ["msPKI-Cert-Template-OID"] = PkiSamples.ForestOid + ".600",
            ["msDS-OIDToGroupLink"] = company.GroupSales,
            [Attr.NtSecurityDescriptor] = FakeSecurityDescriptor.Build(sales, sales, FakeAce.FullControl(da), FakeAce.FullControl(ea), FakeAce.FullControl("S-1-5-18")),
        }, null);
        Session.SetValues(pki.IssuingCa, Attr.NtSecurityDescriptor,
            PkiObjectSecurity(da, ea, pki.CertPublishersSid, FakeAce.FullControl(SidOf(pki.IssuingCaComputer)), FakeAce.FullControl(accounting)));
        var user = pki.Templates[PublicKeyTemplates.User];
        Session.SetValues(user, Attr.NtSecurityDescriptor, TemplateSecurity(da, ea,
            [Enroll($"{DomainSid}-{WellKnownRids.DomainUsers}"), new(FakeAce.AccessAllowedObject, 0, 0x20, "S-1-5-11", enrollmentFlagGuid)]));
        FakeAce[] computerDefaults = [FakeAce.FullControl(da), FakeAce.FullControl("S-1-5-18"), FakeAce.FullControl("S-1-5-32-544"), new(FakeAce.AccessAllowed, 0, 0x20094, "S-1-5-11")];
        SetSecurityDescriptor(pki.IssuingCaComputer, da, [.. computerDefaults, FakeAce.FullControl(it)]);
        SetSecurityDescriptor(AdministratorDn, da, [.. computerDefaults, new(FakeAce.AccessAllowedObject, 0, 0x20, it, altSecurityGuid)]);
        return new PublicKeyAclSample(pki, PublicKeyServicesDn, NtAuthCertificatesDn, policy, pki.IssuingCa, user, pki.IssuingCaComputer, AdministratorDn);
    }

    private const string PkiUserCertificate = "userCertificate";
    private const string PkiAltSecurityIdentities = "altSecurityIdentities";

    /// <summary>The default PKI shared by both samples.</summary>
    private PublicKeySample SeedPublicKeyInfrastructure(SampleCompany company)
    {
        if (PdcNtdsSettingsDn is null)
        {
            AddWatchdogInfrastructure();
        }

        AddAclDefinitions();
        SeedClassDefaults(PublicKeyDefaultSecurityDescriptors);
        var da = $"{DomainSid}-{WellKnownRids.DomainAdmins}";
        var eaSid = $"{DomainSid}-{WellKnownRids.EnterpriseAdmins}";
        var cpSid = $"{DomainSid}-{WellKnownRids.CertPublishers}";
        var enterpriseAdmins = EnsureGroup("Enterprise Admins", GroupScope.Universal, eaSid);
        var certPublishers = EnsureGroup("Cert Publishers", GroupScope.DomainLocal, cpSid);

        // The CA hosts (members of Cert Publishers, as the CA setup makes them).
        var servers = AddOu("Serwery PKI", company.CompanyOu);
        var server = new Dictionary<string, object?> { [Attr.OperatingSystem] = "Windows Server 2025 Standard", [Attr.OperatingSystemVersion] = "10.0 (26100)" };
        var rootComputer = AddComputer(PkiSamples.RootCaComputer, servers, server);
        var issuingComputer = AddComputer(PkiSamples.IssuingCaComputer, servers, server);
        AddMembers(certPublishers, rootComputer, issuingComputer);
        var rootComputerSid = SidOf(rootComputer);
        var issuingComputerSid = SidOf(issuingComputer);

        // Containers (AddWatchdogInfrastructure seeds Public Key Services, Certificate Templates, Enrollment Services,
        // Certification Authorities and NTAuthCertificates).
        var aia = SeedConfigContainer("AIA", PublicKeyServicesDn);
        var cdp = SeedConfigContainer("CDP", PublicKeyServicesDn);
        var kra = SeedConfigContainer("KRA", PublicKeyServicesDn);
        var oid = SeedConfigContainer("OID", PublicKeyServicesDn);
        var rootCdpHost = SeedConfigContainer(PkiSamples.RootCaComputer, cdp);
        var issuingCdpHost = SeedConfigContainer(PkiSamples.IssuingCaComputer, cdp);
        foreach (var container in new[] { PublicKeyServicesDn, CertificateTemplatesDn, EnrollmentServicesDn, oid })
        {
            Session.SetValues(container, Attr.NtSecurityDescriptor, PkiObjectSecurity(da, eaSid, null));
        }

        foreach (var container in new[] { CertificationAuthoritiesDn, aia, cdp, kra })
        {
            Session.SetValues(container, Attr.NtSecurityDescriptor, PkiObjectSecurity(da, eaSid, cpSid));
        }

        Session.SetValues(rootCdpHost, Attr.NtSecurityDescriptor, PkiObjectSecurity(da, eaSid, cpSid, FakeAce.FullControl(rootComputerSid)));
        Session.SetValues(issuingCdpHost, Attr.NtSecurityDescriptor, PkiObjectSecurity(da, eaSid, cpSid, FakeAce.FullControl(issuingComputerSid)));

        // CA stores: the root in Certification Authorities, both in AIA, the issuing CA in NTAuth.
        var rootStore = Seed(Dn("CN", PkiSamples.RootCaName, CertificationAuthoritiesDn), CaObject(PkiSamples.RootCaDer, PkiObjectSecurity(da, eaSid, cpSid)), null);
        var rootAia = Seed(Dn("CN", PkiSamples.Sanitized(PkiSamples.RootCaName), aia), CaObject(PkiSamples.RootCaDer,
            PkiObjectSecurity(da, eaSid, cpSid, FakeAce.FullControl(rootComputerSid))), null);
        var issuingAia = Seed(Dn("CN", PkiSamples.Sanitized(PkiSamples.IssuingCaName), aia), CaObject(PkiSamples.IssuingCaDer,
            PkiObjectSecurity(da, eaSid, cpSid, FakeAce.FullControl(issuingComputerSid))), null);
        Session.SetValues(NtAuthCertificatesDn, PkiAttributeCaCertificate, PkiSamples.IssuingCaDer);
        Session.SetValues(NtAuthCertificatesDn, Attr.NtSecurityDescriptor, PkiObjectSecurity(da, eaSid, cpSid));

        // CDP: the root's base CRL, the issuing CA's base and delta CRL (owned by the CA computers, as AD CS creates them).
        var rootCrl = Seed(Dn("CN", PkiSamples.Sanitized(PkiSamples.RootCaName), rootCdpHost), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "cRLDistributionPoint" },
            ["certificateRevocationList"] = PkiSamples.RootCrlDer(),
            [Attr.NtSecurityDescriptor] = FakeSecurityDescriptor.Build(rootComputerSid, rootComputerSid,
                FakeAce.FullControl(da), FakeAce.FullControl(eaSid), FakeAce.FullControl("S-1-5-18"), FakeAce.FullControl(cpSid),
                FakeAce.FullControl(rootComputerSid), new(FakeAce.AccessAllowed, 0, 0x20094, "S-1-5-11")),
        }, null);
        var issuingCrl = Seed(Dn("CN", PkiSamples.Sanitized(PkiSamples.IssuingCaName), issuingCdpHost), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "cRLDistributionPoint" },
            ["certificateRevocationList"] = PkiSamples.IssuingCrlDer(),
            ["deltaRevocationList"] = PkiSamples.IssuingDeltaCrlDer(),
            [Attr.NtSecurityDescriptor] = FakeSecurityDescriptor.Build(issuingComputerSid, issuingComputerSid,
                FakeAce.FullControl(da), FakeAce.FullControl(eaSid), FakeAce.FullControl("S-1-5-18"), FakeAce.FullControl(cpSid),
                FakeAce.FullControl(issuingComputerSid), new(FakeAce.AccessAllowed, 0, 0x20094, "S-1-5-11")),
        }, null);

        // KRA.
        var kraAgent = Seed(Dn("CN", "Agent odzyskiwania kluczy Kontoso", kra), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "msPKI-PrivateKeyRecoveryAgent" },
            [PkiUserCertificate] = PkiSamples.KraDer(),
            [Attr.NtSecurityDescriptor] = PkiObjectSecurity(da, eaSid, cpSid),
        }, null);

        // Templates: the Windows defaults with their default ACLs.
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        var domainUsers = $"{DomainSid}-{WellKnownRids.DomainUsers}";
        var domainComputers = $"{DomainSid}-{WellKnownRids.DomainComputers}";
        var dcs = $"{DomainSid}-{WellKnownRids.DomainControllers}";
        void Default(string cn, int schema, int nameFlag, int enrollmentFlag, string[] ekus, int number, bool machine, string? display, FakeAce[] aces)
        {
            var attributes = TemplateAttributes(cn, schema, nameFlag, enrollmentFlag, ekus, number, machine);
            attributes[Attr.DisplayName] = display ?? cn;
            attributes["flags"] = machine ? 0x10260 : 0x1023A;
            attributes[Attr.NtSecurityDescriptor] = TemplateSecurity(da, eaSid, aces);
            templates[cn] = AddCertificateTemplate(cn, attributes);
        }

        Default(PublicKeyTemplates.User, 1, unchecked((int)0xA6000000), 0x29, [PkiSamples.EncryptingFileSystem, PkiSamples.SecureEmail, PkiSamples.ClientAuthentication], 1, false,
            "Użytkownik", [Enroll(domainUsers)]);
        Default(PublicKeyTemplates.Machine, 1, 0x18000000, 0, [PkiSamples.ClientAuthentication, PkiSamples.ServerAuthentication], 2, true,
            "Komputer", [Enroll(domainComputers)]);
        Default(PublicKeyTemplates.DomainController, 1, 0x19000000, 0x31, [PkiSamples.ClientAuthentication, PkiSamples.ServerAuthentication], 3, true,
            "Kontroler domeny", [Enroll(dcs), Enroll("S-1-5-9")]);
        Default(PublicKeyTemplates.KerberosAuthentication, 2, 0x08400000, 0x28,
            [PkiSamples.ClientAuthentication, PkiSamples.ServerAuthentication, PkiSamples.SmartCardLogon, PkiSamples.KdcAuthentication], 20, true,
            "Uwierzytelnianie Kerberos", [Enroll(dcs), AutoEnroll(dcs), Enroll("S-1-5-9"), AutoEnroll("S-1-5-9")]);
        Default(PublicKeyTemplates.WebServer, 1, 0x1, 0, [PkiSamples.ServerAuthentication], 4, true, "Serwer sieci Web", []);
        Default(PublicKeyTemplates.SubCa, 1, 0x1, 0, [], 5, true, "Podrzędny urząd certyfikacji", []);
        Default(PublicKeyTemplates.SmartCardLogon, 2, unchecked((int)0x82000000), 0, [PkiSamples.ClientAuthentication, PkiSamples.SmartCardLogon], 6, false,
            "Logowanie kartą inteligentną", []);

        // Enterprise CAs (pKIEnrollmentService): published templates, the CA computer's full control, CES on the issuing CA.
        var rootCa = Seed(Dn("CN", PkiSamples.RootCaName, EnrollmentServicesDn), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "pKIEnrollmentService" },
            [Attr.DisplayName] = PkiSamples.RootCaName,
            [Attr.DnsHostName] = PkiSamples.RootCaHost,
            [PkiAttributeCaCertificate] = PkiSamples.RootCaDer,
            ["certificateTemplates"] = PublicKeyTemplates.PublishedByRoot.ToArray(),
            [Attr.NtSecurityDescriptor] = PkiObjectSecurity(da, eaSid, cpSid, FakeAce.FullControl(rootComputerSid)),
        }, null);
        var issuingCa = Seed(Dn("CN", PkiSamples.IssuingCaName, EnrollmentServicesDn), new Dictionary<string, object?>
        {
            [Attr.ObjectClass] = new[] { "top", "pKIEnrollmentService" },
            [Attr.DisplayName] = PkiSamples.IssuingCaName,
            [Attr.DnsHostName] = PkiSamples.IssuingCaHost,
            [PkiAttributeCaCertificate] = PkiSamples.IssuingCaDer,
            ["certificateTemplates"] = PublicKeyTemplates.PublishedByIssuing.ToArray(),
            // <priority>\n<authentication: X509EnrollmentAuthFlags, 2 = Kerberos>\n<renewal only>\n<URI>
            ["msPKI-Enrollment-Servers"] = "1\n2\n0\nhttps://pki.contoso.test/Kontoso%20Wystawiaj!0105cy%20CA_CES_Kerberos/service.svc/CES\n",
            [Attr.NtSecurityDescriptor] = PkiObjectSecurity(da, eaSid, cpSid, FakeAce.FullControl(issuingComputerSid)),
        }, null);

        return new PublicKeySample(
            PublicKeyServicesDn, rootCa, issuingCa, rootComputer, issuingComputer, NtAuthCertificatesDn, rootStore, rootAia, issuingAia,
            rootCrl, issuingCrl, kraAgent, aia, cdp, kra, oid, certPublishers, enterpriseAdmins, cpSid, eaSid, templates);
    }

    private const string PkiAttributeCaCertificate = "cACertificate";

    /// <summary>An existing group of CN=Users by name, or a new one with the SID.</summary>
    private string EnsureGroup(string name, GroupScope scope, string sid)
    {
        var dn = Dn("CN", name, UsersDn);
        return Session.Exists(dn) ? dn : AddGroup(name, UsersDn, scope, attributes: new Dictionary<string, object?>
        {
            [Attr.ObjectSid] = sid,
            [Attr.IsCriticalSystemObject] = true,
        });
    }

    /// <summary>The attributes of a template (schema 1: no schema version attribute).</summary>
    private static Dictionary<string, object?> TemplateAttributes(string cn, int schema, int nameFlag, int enrollmentFlag, string[] ekus, int number, bool machine)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [Attr.DisplayName] = cn,
            ["flags"] = machine ? 0x20260 : 0x20220,
            ["revision"] = schema == 1 ? 4 : 100,
            ["msPKI-Template-Minor-Revision"] = schema == 1 ? null : 3,
            ["msPKI-Template-Schema-Version"] = schema == 1 ? null : schema,
            ["msPKI-Cert-Template-OID"] = string.Create(CultureInfo.InvariantCulture, $"{PkiSamples.ForestOid}.1.{number}"),
            ["msPKI-Certificate-Name-Flag"] = nameFlag,
            ["msPKI-Enrollment-Flag"] = enrollmentFlag,
            ["msPKI-Private-Key-Flag"] = schema == 1 ? 0x10 : 0x01010000,
            ["msPKI-RA-Signature"] = schema == 1 ? null : 0,
            ["msPKI-Minimal-Key-Size"] = 2048,
            ["pKIExtendedKeyUsage"] = ekus.Length > 0 ? ekus : null,
            ["msPKI-Certificate-Application-Policy"] = schema >= 2 && ekus.Length > 0 ? ekus : null,
            ["pKIKeyUsage"] = new byte[] { 0xA0, 0x00 },
            ["pKIExpirationPeriod"] = Period(TimeSpan.FromDays(365)),
            ["pKIOverlapPeriod"] = Period(TimeSpan.FromDays(42)),
            ["pKIDefaultCSPs"] = schema == 1 ? new[] { "1,Microsoft Enhanced Cryptographic Provider v1.0" } : null,
        };
        return attributes;
    }

    /// <summary>pKIExpirationPeriod / pKIOverlapPeriod: the negative 100-ns interval, 8 bytes little-endian.</summary>
    private static byte[] Period(TimeSpan length)
    {
        var bytes = BitConverter.GetBytes(-length.Ticks);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    /// <summary>A template's descriptor: owner and full control for the root's Domain Admins, Enterprise Admins full control, Authenticated Users read.</summary>
    private static byte[] TemplateSecurity(string da, string ea, IEnumerable<FakeAce> extra) => FakeSecurityDescriptor.Build(da, da,
        [FakeAce.FullControl(da), FakeAce.FullControl(ea), new(FakeAce.AccessAllowed, 0, 0x20094, "S-1-5-11"), .. extra]);

    /// <summary>A PKI object's descriptor: Domain Admins (owner), Enterprise Admins and SYSTEM full control, Authenticated Users read, Cert Publishers full control where AD sets it up.</summary>
    private static byte[] PkiObjectSecurity(string da, string ea, string? certPublishers, params FakeAce[] extra) => FakeSecurityDescriptor.Build(da, da,
    [
        FakeAce.FullControl(da), FakeAce.FullControl(ea), FakeAce.FullControl("S-1-5-18"), new(FakeAce.AccessAllowed, 0, 0x20094, "S-1-5-11"),
        .. certPublishers is null ? Array.Empty<FakeAce>() : [FakeAce.FullControl(certPublishers)],
        .. extra,
    ]);

    private static Dictionary<string, object?> CaObject(byte[] certificate, byte[] securityDescriptor) => new()
    {
        [Attr.ObjectClass] = new[] { "top", "certificationAuthority" },
        [PkiAttributeCaCertificate] = certificate,
        [Attr.NtSecurityDescriptor] = securityDescriptor,
    };

    private static FakeAce Enroll(string sid) => FakeAce.ExtendedRight(sid, AclGuids.CertificateEnrollment);

    private static FakeAce AutoEnroll(string sid) => FakeAce.ExtendedRight(sid, AclGuids.CertificateAutoEnrollment);
}

/// <summary>
/// The objects of <see cref="FakeDirectoryBuilder.AddPublicKeySample"/> by role (DNs; the GUIDs come from
/// <c>Session.Snapshot(dn).ObjectGuid</c>). <see cref="Templates"/> maps the cn values of <see cref="PublicKeyTemplates"/>
/// to the template DNs.
/// </summary>
public sealed record PublicKeySample(
    string PublicKeyServices,
    string RootCa,
    string IssuingCa,
    string RootCaComputer,
    string IssuingCaComputer,
    string NtAuth,
    string RootStore,
    string RootAia,
    string IssuingAia,
    string RootCdp,
    string IssuingCdp,
    string KraAgent,
    string AiaContainer,
    string CdpContainer,
    string KraContainer,
    string OidContainer,
    string CertPublishers,
    string EnterpriseAdmins,
    string CertPublishersSid,
    string EnterpriseAdminsSid,
    IReadOnlyDictionary<string, string> Templates)
{
    /// <summary><i>Polityka wysokiej pewności</i> (linked to <see cref="LinkedGroup"/>; null in the ACL sample).</summary>
    public string? HighAssuranceOid { get; init; }

    /// <summary><i>Polityka średniej pewności</i> (no group link).</summary>
    public string? MediumAssuranceOid { get; init; }

    /// <summary>GG_Administratorzy_PKI (universal, member of Enterprise Admins: Tier 0).</summary>
    public string? LinkedGroup { get; init; }

    /// <summary>Administrator: a weak issuer + subject mapping on a Tier 0 account.</summary>
    public string? WeakTier0Account { get; init; }

    /// <summary>Łukasz Wiśniewski: a weak RFC 822 mapping.</summary>
    public string? WeakAccount { get; init; }

    /// <summary>Jan Kowalski: a strong issuer + serial mapping, a valid published certificate, key credentials, userPKCS12.</summary>
    public string? StrongAccount { get; init; }

    /// <summary>Paweł Lewandowski: a Kerberos mapping (not a certificate mapping).</summary>
    public string? KerberosMappedAccount { get; init; }

    /// <summary>Grażyna Dąbrowska: an expired published certificate.</summary>
    public string? ExpiredCertificateAccount { get; init; }

    /// <summary>The contact Zofia Żurawska with a published certificate.</summary>
    public string? Contact { get; init; }

    /// <summary>The DN of a template by its cn.</summary>
    public string Template(string cn) => Templates[cn];
}

/// <summary>The objects of <see cref="FakeDirectoryBuilder.AddPublicKeyAclSample"/> with a delegation beyond the defaults.</summary>
public sealed record PublicKeyAclSample(
    PublicKeySample Pki,
    string PublicKeyServices,
    string NtAuth,
    string SalesPolicyOid,
    string IssuingCa,
    string UserTemplate,
    string IssuingCaComputer,
    string Administrator);
