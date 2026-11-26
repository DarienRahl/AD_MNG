using System.Formats.Asn1;
using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Directory;
using AdMng.Core.Ldap;
using AdMng.Core.Model;
using AdMng.Core.PrivilegedAccess;

namespace AdMng.Core.Pki;

/// <summary>How <see cref="PkiOverviewReader.ReadAsync"/> reads.</summary>
public sealed record PkiReadOptions
{
    /// <summary>The page size of the subtree search.</summary>
    public int PageSize { get; init; } = 1000;

    /// <summary>Look up the computer objects of the enterprise CA hosts in the window's domain (off for the Watchdog later).</summary>
    public bool ResolveCaComputers { get; init; } = true;

    /// <summary>The clock of <see cref="PkiSnapshot.ReadAt"/>.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The options only.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiReadOptions)}(page {PageSize}, CA computers {ResolveCaComputers})");
}

/// <summary>Progress of <see cref="PkiOverviewReader.ReadAsync"/>.</summary>
/// <param name="Objects">PKI objects read so far.</param>
public sealed record PkiReadProgress(int Objects)
{
    /// <summary>The count only.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Objects} objects");
}

/// <summary>
/// Reads the forest PKI (docs/dev/p-certificate-services.md 2.1): one paged subtree search of <c>CN=Public Key
/// Services,CN=Services,&lt;config&gt;</c> over <see cref="PkiClasses.All"/> with <see cref="PkiAttributes.OverviewAttributes"/>,
/// the security descriptors and extended DNs, all on the session's DC (rule 4), plus one search per enterprise CA host
/// of the window's domain for its computer object. Never throws for a missing or denied base: the snapshot then carries
/// <see cref="PkiReadNoticeCode.BaseNotFound"/> / <see cref="PkiReadNoticeCode.BaseAccessDenied"/>.
/// </summary>
public static class PkiOverviewReader
{
    /// <summary>The page size of <see cref="ReadDomainControllersAsync"/> (AD's MaxPageSize).</summary>
    private const int DomainControllerPageSize = 1000;

    /// <summary>Reads the snapshot (cancellable, progress per page).</summary>
    public static async Task<PkiSnapshot> ReadAsync(IDirectoryReader reader, PkiReadOptions options, IProgress<PkiReadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PageSize);
        cancellationToken.ThrowIfCancellationRequested();

        var configurationDn = reader.Info.ConfigurationNamingContext;
        var layout = new PkiLayout(configurationDn);
        var objects = new List<DirectoryObject>();
        try
        {
            await foreach (var obj in reader.SearchAsync(OverviewSearch(layout, options.PageSize), cancellationToken).ConfigureAwait(false))
            {
                objects.Add(obj);
                if (objects.Count % options.PageSize == 0)
                {
                    progress?.Report(new PkiReadProgress(objects.Count));
                }
            }
        }
        catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.NoSuchObject or DirectoryErrorKind.AccessDenied)
        {
            // No AD CS (or the Samba lab), or a base the caller may not read: a visible "not evaluated" state, never a
            // partial snapshot that would read as "nobody can enroll".
            var code = ex.Kind == DirectoryErrorKind.NoSuchObject ? PkiReadNoticeCode.BaseNotFound : PkiReadNoticeCode.BaseAccessDenied;
            return PkiSnapshot.Empty(configurationDn, options.Time.GetUtcNow(), [new PkiReadNotice(code, layout.PublicKeyServices.ToString(), null)]);
        }

        progress?.Report(new PkiReadProgress(objects.Count));
        var build = new SnapshotBuilder(layout);
        foreach (var obj in objects)
        {
            build.Add(obj);
        }

        var cas = build.EnterpriseCas();
        if (options.ResolveCaComputers)
        {
            cas = await ResolveCaComputersAsync(reader, cas, build.Notices, cancellationToken).ConfigureAwait(false);
        }

        return build.Finish(configurationDn, options.Time.GetUtcNow(), cas);
    }

    /// <summary>
    /// Reads the window domain's domain controllers (<c>PrivilegedAccessScanner.DomainControllerFilter</c>;
    /// <see cref="PkiAttributes.DomainControllerAttributes"/>) with the certificates their objects publish.
    /// </summary>
    public static async Task<IReadOnlyList<PkiDomainController>> ReadDomainControllersAsync(IDirectoryReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var controllers = new List<PkiDomainController>();
        await foreach (var obj in reader.SearchAsync(new DirectorySearch
        {
            BaseDn = reader.Info.DefaultNamingContext,
            Scope = SearchScope.Subtree,
            Filter = PrivilegedAccessScanner.DomainControllerFilter,
            Attributes = PkiAttributes.DomainControllerAttributes,
            PageSize = DomainControllerPageSize,
        }, cancellationToken).ConfigureAwait(false))
        {
            var host = obj.GetString(Attr.DnsHostName) is { Length: > 0 } name ? name.TrimEnd('.') : null;
            var isReadOnly = obj.AccountControl?.HasFlag(UserAccountControl.PartialSecretsAccount) == true;
            controllers.Add(new PkiDomainController(obj.DistinguishedName, host, isReadOnly, BinaryValues(obj, PkiAttributes.UserCertificate)));
        }

        return [.. controllers
            .OrderBy(c => c.HostName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Dn, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The one subtree search of 2.1 (the flags of <c>ConfigurationPartitionWatcher.PublicKeyInfrastructureAsync</c>).</summary>
    private static DirectorySearch OverviewSearch(PkiLayout layout, int pageSize) => new()
    {
        BaseDn = layout.PublicKeyServices.ToString(),
        Scope = SearchScope.Subtree,
        Filter = LdapFilter.Or(PkiClasses.All.Select(c => LdapFilter.Equal(Attr.ObjectClass, c))),
        Attributes = PkiAttributes.OverviewAttributes,
        ReadSecurityDescriptor = true,
        ExtendedDn = true,
        PageSize = pageSize,
    };

    /// <summary>
    /// The computer objects of the enterprise CA hosts (decision 4, ESC5, navigation), one search of the window's default
    /// naming context per CA on the session's DC (rule 4). A host whose domain part is the window's domain is looked up by
    /// <c>dNSHostName</c> or <c>sAMAccountName</c> (its first label + <c>$</c>; not found → <see cref="PkiReadNoticeCode.CaComputerUnresolved"/>);
    /// any other host by its exact <c>dNSHostName</c> only – a disjoint DNS namespace still resolves, and a computer of the
    /// window's domain with the same first label is never taken for a CA host of another domain (not found →
    /// <see cref="PkiReadNoticeCode.CaComputerInOtherDomain"/>).
    /// </summary>
    private static async Task<IReadOnlyList<PkiEnrollmentService>> ResolveCaComputersAsync(
        IDirectoryReader reader,
        IReadOnlyList<PkiEnrollmentService> cas,
        NoticeList notices,
        CancellationToken cancellationToken)
    {
        var domain = reader.Info.DomainDnsName.TrimEnd('.');
        var resolved = new List<PkiEnrollmentService>(cas.Count);
        foreach (var ca in cas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ca.HostName is not { Length: > 0 } host)
            {
                notices.Add(PkiReadNoticeCode.CaComputerUnresolved, ca.Dn, ca.ObjectGuid);
                resolved.Add(ca);
                continue;
            }

            var dot = host.IndexOf('.', StringComparison.Ordinal);
            var label = dot < 0 ? host : host[..dot];
            var inWindowDomain = dot < 0 || string.Equals(host[(dot + 1)..], domain, StringComparison.OrdinalIgnoreCase);
            var byName = LdapFilter.Equal(Attr.DnsHostName, host);
            var filter = inWindowDomain
                ? LdapFilter.And(ObjectKind.Computer.ToLdapFilter(), LdapFilter.Or(byName, LdapFilter.Equal(Attr.SamAccountName, SamAccountNameOf(label))))
                : LdapFilter.And(ObjectKind.Computer.ToLdapFilter(), byName);

            var candidates = new List<DirectoryObject>();
            try
            {
                await foreach (var computer in reader.SearchAsync(new DirectorySearch
                {
                    BaseDn = reader.Info.DefaultNamingContext,
                    Scope = SearchScope.Subtree,
                    Filter = filter,
                    Attributes = [Attr.DnsHostName, Attr.SamAccountName, Attr.ObjectSid],
                }, cancellationToken).ConfigureAwait(false))
                {
                    candidates.Add(computer);
                }
            }
            catch (DirectoryException ex) when (ex.Kind is DirectoryErrorKind.AccessDenied or DirectoryErrorKind.NoSuchObject)
            {
                candidates.Clear();
            }

            // dNSHostName is unique; the sAMAccountName fallback only when it is the one match.
            var match = candidates.FirstOrDefault(c => string.Equals(c.GetString(Attr.DnsHostName)?.TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase))
                ?? (candidates.Count == 1 ? candidates[0] : null);
            if (match is null)
            {
                notices.Add(inWindowDomain ? PkiReadNoticeCode.CaComputerUnresolved : PkiReadNoticeCode.CaComputerInOtherDomain, ca.Dn, ca.ObjectGuid);
                resolved.Add(ca);
                continue;
            }

            resolved.Add(ca with { ComputerDn = match.DistinguishedName, ComputerSid = match.ObjectSid });
        }

        return resolved;
    }

    /// <summary>The pre-Windows 2000 name of a host label: its first 15 characters and <c>$</c>.</summary>
    private static string SamAccountNameOf(string label) => (label.Length > 15 ? label[..15] : label) + "$";

    /// <summary>The binary values of a multi-valued attribute (never through <c>GetAllValuesAsync</c>, which decodes UTF-8); AD's one-byte placeholders left out.</summary>
    private static List<byte[]> BinaryValues(DirectoryObject obj, string attribute) =>
        obj.TryGet(attribute, out var values) ? [.. values.OfType<byte[]>().Where(v => !PkiDer.IsPlaceholder(v))] : [];

    /// <summary>The well-known DNs of the PKI subtree (built with <see cref="DistinguishedName.Child"/>, rule 3).</summary>
    private sealed class PkiLayout
    {
        public PkiLayout(string configurationDn)
        {
            PublicKeyServices = DistinguishedName.Parse(configurationDn).Child("CN", "Services").Child("CN", "Public Key Services");
            NtAuth = PublicKeyServices.Child("CN", "NTAuthCertificates");
            EnrollmentServices = PublicKeyServices.Child("CN", "Enrollment Services");
            CertificateTemplates = PublicKeyServices.Child("CN", "Certificate Templates");
            CertificationAuthorities = PublicKeyServices.Child("CN", "Certification Authorities");
            Aia = PublicKeyServices.Child("CN", "AIA");
            Cdp = PublicKeyServices.Child("CN", "CDP");
            Oid = PublicKeyServices.Child("CN", "OID");
            Kra = PublicKeyServices.Child("CN", "KRA");
        }

        public DistinguishedName PublicKeyServices { get; }

        public DistinguishedName NtAuth { get; }

        public DistinguishedName EnrollmentServices { get; }

        public DistinguishedName CertificateTemplates { get; }

        public DistinguishedName CertificationAuthorities { get; }

        public DistinguishedName Aia { get; }

        public DistinguishedName Cdp { get; }

        public DistinguishedName Oid { get; }

        public DistinguishedName Kra { get; }

        /// <summary>The objects a PKI read expects (2.1; a missing one is <see cref="PkiReadNoticeCode.ContainerMissing"/>).</summary>
        public IEnumerable<DistinguishedName> WellKnown => [NtAuth, EnrollmentServices, CertificateTemplates, CertificationAuthorities, Aia, Cdp, Oid, Kra];
    }

    /// <summary>The notices of one read, at most one per code and object.</summary>
    private sealed class NoticeList
    {
        private readonly List<PkiReadNotice> _notices = [];
        private readonly HashSet<(PkiReadNoticeCode, string)> _seen = [];

        public IReadOnlyList<PkiReadNotice> Items => _notices;

        public void Add(PkiReadNoticeCode code, string? dn, Guid? guid)
        {
            if (_seen.Add((code, dn ?? string.Empty)))
            {
                _notices.Add(new PkiReadNotice(code, dn, guid));
            }
        }
    }

    /// <summary>Sorts the objects of the subtree search into the snapshot's lists.</summary>
    private sealed class SnapshotBuilder(PkiLayout layout)
    {
        private readonly HashSet<DistinguishedName> _seen = [];
        private readonly List<DirectoryObject> _enrollmentServices = [];
        private readonly List<PkiCaObject> _roots = [];
        private readonly List<PkiCaObject> _aia = [];
        private readonly List<PkiCrlList> _crls = [];
        private readonly List<PkiKraAgent> _kra = [];
        private readonly List<PkiOidObject> _oids = [];
        private readonly List<CertificateTemplate> _templates = [];
        private PkiCaObject? _ntAuth;

        public NoticeList Notices { get; } = new();

        public void Add(DirectoryObject obj)
        {
            var dn = obj.Dn;
            _seen.Add(dn);
            var classes = obj.GetStrings(Attr.ObjectClass);
            if (Is(classes, PkiClasses.EnrollmentService))
            {
                _enrollmentServices.Add(obj);
            }
            else if (Is(classes, PkiClasses.CertificateTemplate))
            {
                var template = CertificateTemplate.Of(obj);
                if (template.SecurityDescriptor is null)
                {
                    Notices.Add(PkiReadNoticeCode.SecurityDescriptorUnreadable, obj.DistinguishedName, template.ObjectGuid);
                }

                _templates.Add(template);
            }
            else if (Is(classes, PkiClasses.CertificationAuthority))
            {
                AddCaObject(obj, dn);
            }
            else if (Is(classes, PkiClasses.CrlDistributionPoint))
            {
                _crls.Add(new PkiCrlList(
                    obj.DistinguishedName,
                    GuidOf(obj),
                    NameOf(obj),
                    dn.Parent is { IsRoot: false } parent ? parent.Name : null,
                    Crl(obj, PkiAttributes.CertificateRevocationList),
                    Crl(obj, PkiAttributes.DeltaRevocationList),
                    IssuerCaGuid: null)
                {
                    Authority = Crl(obj, PkiAttributes.AuthorityRevocationList),
                });
            }
            else if (Is(classes, PkiClasses.KeyRecoveryAgent))
            {
                _kra.Add(new PkiKraAgent(obj.DistinguishedName, GuidOf(obj), NameOf(obj), Certificates(obj, PkiAttributes.UserCertificate)));
            }
            else if (Is(classes, PkiClasses.EnterpriseOid) && dn != layout.Oid)
            {
                // CN=OID itself is an msPKI-Enterprise-Oid in AD: the container, not an OID object.
                var link = obj.GetString(PkiAttributes.OidToGroupLink);
                _oids.Add(new PkiOidObject(
                    obj.DistinguishedName,
                    GuidOf(obj),
                    obj.GetString(PkiAttributes.TemplateOid) ?? string.Empty,
                    obj.GetString(Attr.DisplayName),
                    obj.GetInt32(PkiAttributes.Flags) ?? 0,
                    link is null ? null : ExtendedDistinguishedName.TryParse(link, out var extended) ? extended.Dn : link));
            }
        }

        public IReadOnlyList<PkiEnrollmentService> EnterpriseCas()
        {
            var ntAuth = (_ntAuth?.Certificates ?? []).Select(c => PkiDer.Sha256Hex(c.Der)).ToHashSet(StringComparer.Ordinal);
            return [.. _enrollmentServices.Select(obj =>
            {
                var certificates = Certificates(obj, PkiAttributes.CaCertificate);
                return new PkiEnrollmentService(
                    obj.DistinguishedName,
                    GuidOf(obj),
                    NameOf(obj),
                    obj.GetString(Attr.DisplayName),
                    obj.GetString(Attr.DnsHostName) is { Length: > 0 } host ? host.TrimEnd('.') : null,
                    ComputerDn: null,
                    ComputerSid: null,
                    [.. certificates.OrderByDescending(c => c.Summary?.NotBefore ?? DateTimeOffset.MinValue)],
                    [.. obj.GetStrings(PkiAttributes.CertificateTemplates).Where(t => t.Length > 0)],
                    [.. obj.GetStrings(PkiAttributes.EnrollmentServers).Select(EnrollmentServerUri).OfType<string>()],
                    certificates.Any(c => ntAuth.Contains(PkiDer.Sha256Hex(c.Der))));
            }).OrderBy(ca => ca.Name, StringComparer.OrdinalIgnoreCase)];
        }

        public PkiSnapshot Finish(string configurationDn, DateTimeOffset readAt, IReadOnlyList<PkiEnrollmentService> cas)
        {
            foreach (var dn in layout.WellKnown.Where(dn => !_seen.Contains(dn)))
            {
                Notices.Add(PkiReadNoticeCode.ContainerMissing, dn.ToString(), null);
            }

            var crls = _crls.Select(list => list with { IssuerCaGuid = IssuerOf(list, cas) })
                .OrderBy(l => l.HostName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var trust = new PkiTrustMaterial(
                Distinct(_roots.SelectMany(r => r.Certificates)),
                Distinct(_aia.SelectMany(a => a.Certificates.Concat(a.CrossCertificates))
                    .Concat(_roots.SelectMany(r => r.CrossCertificates))
                    .Concat(cas.SelectMany(ca => ca.Certificates))),
                Distinct(_ntAuth?.Certificates ?? []));
            return new PkiSnapshot(
                configurationDn,
                readAt,
                cas,
                [.. _roots.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)],
                [.. _aia.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)],
                _ntAuth,
                crls,
                [.. _kra.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)],
                [.. _oids.OrderBy(o => o.DisplayName ?? o.Oid, StringComparer.OrdinalIgnoreCase)],
                [.. _templates.OrderBy(t => t.Cn, StringComparer.OrdinalIgnoreCase)],
                Notices.Items,
                trust);
        }

        /// <summary>
        /// The enterprise CA whose certificate subject and Subject Key Identifier equal the CRL's issuer and Authority Key
        /// Identifier (never the cn, which certutil sanitizes); without an AKI in the CRL, the subject alone when exactly one
        /// CA has it. The first CRL of the list that parses decides (a malformed base does not hide a valid delta or ARL).
        /// </summary>
        private static Guid? IssuerOf(PkiCrlList list, IReadOnlyList<PkiEnrollmentService> cas)
        {
            if ((list.Base?.Summary ?? list.Delta?.Summary ?? list.Authority?.Summary) is not { } crl)
            {
                return null;
            }

            var bySubject = cas.Where(ca => ca.Certificates.Any(c => c.Summary is { } s && string.Equals(s.Subject, crl.Issuer, StringComparison.Ordinal))).ToList();
            if (crl.AuthorityKeyIdentifier is { } aki)
            {
                return bySubject.FirstOrDefault(ca => ca.Certificates.Any(c => c.Summary is { } s
                    && string.Equals(s.Subject, crl.Issuer, StringComparison.Ordinal)
                    && string.Equals(s.SubjectKeyIdentifier, aki, StringComparison.OrdinalIgnoreCase)))?.ObjectGuid;
            }

            return bySubject.Count == 1 ? bySubject[0].ObjectGuid : null;
        }

        private static List<byte[]> Distinct(IEnumerable<PkiCertificate> certificates)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return [.. certificates.Where(c => seen.Add(PkiDer.Sha256Hex(c.Der))).Select(c => c.Der)];
        }

        private void AddCaObject(DirectoryObject obj, DistinguishedName dn)
        {
            PkiCaStore store;
            if (dn == layout.NtAuth)
            {
                store = PkiCaStore.NtAuth;
            }
            else if (dn.Parent == layout.CertificationAuthorities)
            {
                store = PkiCaStore.RootCa;
            }
            else if (dn.Parent == layout.Aia)
            {
                store = PkiCaStore.Aia;
            }
            else
            {
                return;
            }

            // The CRL attributes a certificationAuthority must contain hold a placeholder byte: never read here.
            var ca = new PkiCaObject(
                obj.DistinguishedName,
                GuidOf(obj),
                NameOf(obj),
                store,
                Certificates(obj, PkiAttributes.CaCertificate),
                CrossCertificates(obj));
            switch (store)
            {
                case PkiCaStore.NtAuth:
                    _ntAuth = ca;
                    break;
                case PkiCaStore.RootCa:
                    _roots.Add(ca);
                    break;
                default:
                    _aia.Add(ca);
                    break;
            }
        }

        private List<PkiCertificate> Certificates(DirectoryObject obj, string attribute)
        {
            var certificates = new List<PkiCertificate>();
            foreach (var der in BinaryValues(obj, attribute))
            {
                certificates.Add(Certificate(obj, der));
            }

            return certificates;
        }

        private PkiCertificate Certificate(DirectoryObject obj, byte[] der)
        {
            var summary = CertificateSummary.Of(der);
            if (summary is null)
            {
                Notices.Add(PkiReadNoticeCode.CertificateMalformed, obj.DistinguishedName, obj.ObjectGuid);
            }

            return new PkiCertificate(der, summary);
        }

        /// <summary>
        /// <c>crossCertificatePair</c>: each value is a <c>CertificatePair ::= SEQUENCE { forward [0] Certificate OPTIONAL,
        /// reverse [1] Certificate OPTIONAL }</c> (X.509, RFC 4523; CryptoAPI's <c>X509_CERT_PAIR</c>) – both certificates
        /// become entries – or, as some tools write it, a bare certificate.
        /// </summary>
        private List<PkiCertificate> CrossCertificates(DirectoryObject obj)
        {
            var certificates = new List<PkiCertificate>();
            foreach (var value in BinaryValues(obj, PkiAttributes.CrossCertificatePair))
            {
                var pair = CertificatePair(value);
                if (pair is null)
                {
                    certificates.Add(Certificate(obj, value));
                    continue;
                }

                certificates.AddRange(pair.Select(der => Certificate(obj, der)));
            }

            return certificates;
        }

        /// <summary>The certificates of a CertificatePair value, or null when it is not one.</summary>
        private static List<byte[]>? CertificatePair(byte[] value)
        {
            try
            {
                var outer = new AsnReader(value, AsnEncodingRules.DER);
                var pair = outer.ReadSequence();
                outer.ThrowIfNotEmpty();
                if (!pair.HasData || pair.PeekTag().TagClass != TagClass.ContextSpecific)
                {
                    return null;
                }

                var certificates = new List<byte[]>();
                foreach (var number in new[] { 0, 1 })
                {
                    var tag = new Asn1Tag(TagClass.ContextSpecific, number, isConstructed: true);
                    if (pair.HasData && pair.PeekTag().HasSameClassAndValue(tag))
                    {
                        certificates.Add(pair.ReadSequence(tag).ReadEncodedValue().ToArray());
                    }
                }

                pair.ThrowIfNotEmpty();
                return certificates;
            }
            catch (AsnContentException)
            {
                return null;
            }
        }

        private PkiCrl? Crl(DirectoryObject obj, string attribute)
        {
            if (BinaryValues(obj, attribute) is not [var der, ..])
            {
                return null;
            }

            var summary = CrlSummary.Of(der);
            if (summary is null)
            {
                Notices.Add(PkiReadNoticeCode.CrlMalformed, obj.DistinguishedName, obj.ObjectGuid);
            }

            return new PkiCrl(der, summary);
        }

        /// <summary>
        /// The URI of one <c>msPKI-Enrollment-Servers</c> value (<c>&lt;priority&gt;\n&lt;authentication&gt;\n&lt;renewal
        /// only&gt;\n&lt;URI&gt;\n</c>, MS-CAESO / MS-WCCE), or null.
        /// </summary>
        private static string? EnrollmentServerUri(string value)
        {
            var lines = value.Split('\n').Select(l => l.Trim()).ToList();
            if (lines.Count >= 4 && lines[3].Length > 0)
            {
                return lines[3];
            }

            return lines.FirstOrDefault(l => l.Contains("://", StringComparison.Ordinal));
        }

        private static bool Is(IReadOnlyList<string> classes, string objectClass) =>
            classes.Contains(objectClass, StringComparer.OrdinalIgnoreCase);

        private static Guid GuidOf(DirectoryObject obj) => obj.ObjectGuid ?? Guid.Empty;

        private static string NameOf(DirectoryObject obj) => obj.GetString(Attr.Cn) ?? obj.Dn.Name;
    }
}
