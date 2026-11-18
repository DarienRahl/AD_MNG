using System.Globalization;
using AdMng.Core.Ldap;

namespace AdMng.Core.Pki;

/// <summary>The expiry thresholds of the health rules (decision 2; from the settings, never directory data).</summary>
/// <param name="EndEntityWarningDays">End-entity certificates (DC LDAPS, KRA, published): default 30.</param>
/// <param name="CaWarningDays">CA certificates: default 180.</param>
public sealed record PkiHealthThresholds(int EndEntityWarningDays, int CaWarningDays)
{
    /// <summary>30 and 180 days.</summary>
    public static PkiHealthThresholds Default { get; } = new(30, 180);

    /// <summary>The two numbers (settings, not directory data).</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{nameof(PkiHealthThresholds)}({EndEntityWarningDays} d, CA {CaWarningDays} d)");
}

/// <summary>The state of a health row (<i>Stan: OK / Wygasa / Wygasła / Niedostępna / Nieznany</i>).</summary>
public enum PkiHealthStatus
{
    Ok,
    Expiring,
    Expired,
    Unavailable,
    Unknown,
}

/// <summary>What a health row is about (<c>Pki.HealthItem.&lt;PkiHealthItem&gt;</c>).</summary>
public enum PkiHealthItem
{
    CaCertificate,
    AiaLocation,
    CdpLocation,

    /// <summary>
    /// A delta CRL location (Freshest CRL: <see cref="CrlSummary.FreshestCrl"/>, <see cref="CertificateSummary.FreshestCrl"/>;
    /// <i>DeltaCRL Location</i> of pkiview).
    /// </summary>
    DeltaCrlLocation,

    BaseCrl,
    DeltaCrl,
    KraCertificate,
    NtAuthCertificate,
}

/// <summary>One row of the <i>pkiview</i>-style health tab.</summary>
/// <param name="CaGuid">The enterprise CA the row belongs to (null for KRA and NTAuth rows).</param>
/// <param name="Item">What it is about.</param>
/// <param name="Index">The position among rows of the same item (the n-th AIA URL …), counted from 0 (per CA for the CA rows).</param>
/// <param name="Status">Its state at evaluation time.</param>
/// <param name="ValidUntil"><c>NotAfter</c> / <c>NextUpdate</c>, null when unknown.</param>
/// <param name="Url">The URL of a location row.</param>
public sealed record PkiHealthRow(Guid? CaGuid, PkiHealthItem Item, int Index, PkiHealthStatus Status, DateTimeOffset? ValidUntil, string? Url)
{
    /// <summary>Item, index, status and the CA GUID only (never the URL).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Item}[{Index}] {Status}{(CaGuid is { } ca ? " " + ca.ToString("B") : string.Empty)}");
}

/// <summary>
/// The health rules (docs/dev/p-certificate-services.md 2.4): pure, called by the Presentation at display time with the
/// current thresholds and clock – never cached with the snapshot.
/// </summary>
/// <remarks>
/// A certificate is <see cref="PkiHealthStatus.Expired"/> after <c>NotAfter</c>, <see cref="PkiHealthStatus.Expiring"/> when
/// at most the threshold is left, <see cref="PkiHealthStatus.Unknown"/> before <c>NotBefore</c>; a CRL is expired after
/// <c>NextUpdate</c>, expiring when less than <c>max(1 day, 20 % × (NextUpdate − ThisUpdate))</c> is left, unknown without
/// <c>NextUpdate</c> (or before <c>ThisUpdate</c>); a missing or unparseable object is <see cref="PkiHealthStatus.Unavailable"/>.
/// Location URLs: <c>ldap:///…</c> is resolved against the snapshot by its DN – inside <c>CN=Public Key Services</c> the
/// object's state (unavailable when it is missing or holds no certificate / CRL of the right issuer), outside it unknown (the
/// snapshot did not read it); <c>ldap://host/…</c>, <c>file:</c>, UNC paths and anything else are unknown (<i>nie
/// sprawdzono</i>); <c>http(s)</c> is unknown until an HTTP result for the same URL and kind is passed, then the fetched
/// certificate's / CRL's state (unavailable for a failed fetch or content of another issuer).
/// </remarks>
public static class PkiHealth
{
    private static readonly TimeSpan MinimumCrlMargin = TimeSpan.FromDays(1);

    /// <summary>The rows of every enterprise CA (certificate, AIA, CDP and delta CRL URLs, base and delta CRL) and of the KRA and NTAuth certificates.</summary>
    /// <remarks>
    /// Per enterprise CA, in this order: its current certificate (the newest of <c>cACertificate</c>, CA threshold; none or
    /// unparseable: unavailable), the AIA CA Issuers, CDP and Freshest CRL URLs of that certificate (where its issuer's
    /// certificate, base CRL and delta CRL are, as <c>pkiview.msc</c>), then the base and delta CRL of every <c>CN=CDP</c> list
    /// matched to the CA by <see cref="PkiCrlList.IssuerCaGuid"/> (no list: one unavailable base row; a missing delta is a row
    /// only when the base CRL names a Freshest CRL location), each followed by the delta CRL locations its base CRL names (the
    /// Freshest CRL of the CA's own base CRL, where AD CS writes them). Then every KRA certificate (end-entity threshold) and every
    /// NTAuth certificate (CA threshold; a missing NTAuth object is one unavailable row when the forest has an enterprise CA).
    /// </remarks>
    public static IReadOnlyList<PkiHealthRow> Evaluate(PkiSnapshot snapshot, PkiHealthThresholds thresholds, DateTimeOffset now, IReadOnlyList<PkiHttpCheckResult>? http)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(thresholds);
        var context = new Context(snapshot, thresholds, now, http ?? []);
        var rows = new List<PkiHealthRow>();
        foreach (var ca in snapshot.EnterpriseCas)
        {
            context.AddCaRows(ca, rows);
        }

        var kraIndex = 0;
        foreach (var agent in snapshot.KraAgents)
        {
            if (agent.Certificates.Count == 0)
            {
                rows.Add(new PkiHealthRow(null, PkiHealthItem.KraCertificate, kraIndex++, PkiHealthStatus.Unavailable, null, null));
                continue;
            }

            foreach (var certificate in agent.Certificates)
            {
                var (status, until) = context.CertificateState(certificate.Summary, isCa: false);
                rows.Add(new PkiHealthRow(null, PkiHealthItem.KraCertificate, kraIndex++, status, until, null));
            }
        }

        if (snapshot.NtAuth is { } ntAuth)
        {
            var index = 0;
            foreach (var certificate in ntAuth.Certificates)
            {
                var (status, until) = context.CertificateState(certificate.Summary, isCa: true);
                rows.Add(new PkiHealthRow(null, PkiHealthItem.NtAuthCertificate, index++, status, until, null));
            }
        }
        else if (snapshot.EnterpriseCas.Count > 0)
        {
            rows.Add(new PkiHealthRow(null, PkiHealthItem.NtAuthCertificate, 0, PkiHealthStatus.Unavailable, null, null));
        }

        return rows;
    }

    /// <summary>
    /// The <c>http</c>/<c>https</c> URLs (AIA CA Issuers, CDP, OCSP of the snapshot's certificates; delta CDP = the Freshest
    /// CRL of the certificates and of the base CRLs) for the HTTP check.
    /// </summary>
    /// <remarks>
    /// The certificates of the enterprise CAs (renewals included), the root and AIA stores (cross certificates included), the
    /// NTAuth store and the KRA agents, then the base CRLs of <c>CN=CDP</c>; each absolute <c>http</c>/<c>https</c> URL once per
    /// kind, in that order. Nothing else (<c>ldap:</c>, <c>file:</c>, UNC) is ever listed.
    /// </remarks>
    public static IReadOnlyList<PkiUrl> HttpUrls(PkiSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var seen = new HashSet<(string, PkiUrlKind)>();
        var urls = new List<PkiUrl>();

        void Add(IEnumerable<string> values, PkiUrlKind kind)
        {
            foreach (var url in values)
            {
                if (IsHttp(url) && seen.Add((url, kind)))
                {
                    urls.Add(new PkiUrl(url, kind));
                }
            }
        }

        void AddCertificate(PkiCertificate certificate)
        {
            if (certificate.Summary is not { } summary)
            {
                return;
            }

            Add(summary.AiaCaIssuers, PkiUrlKind.AiaCaIssuers);
            Add(summary.CrlDistributionPoints, PkiUrlKind.Cdp);
            Add(summary.FreshestCrl, PkiUrlKind.DeltaCdp);
            Add(summary.AiaOcsp, PkiUrlKind.Ocsp);
        }

        foreach (var certificate in snapshot.EnterpriseCas.SelectMany(ca => ca.Certificates))
        {
            AddCertificate(certificate);
        }

        foreach (var store in Stores(snapshot))
        {
            foreach (var certificate in store.Certificates.Concat(store.CrossCertificates))
            {
                AddCertificate(certificate);
            }
        }

        foreach (var certificate in snapshot.KraAgents.SelectMany(a => a.Certificates))
        {
            AddCertificate(certificate);
        }

        foreach (var list in snapshot.CrlLists)
        {
            if (list.Base?.Summary is { } crl)
            {
                Add(crl.FreshestCrl, PkiUrlKind.DeltaCdp);
            }
        }

        return urls;
    }

    /// <summary>An absolute <c>http</c>/<c>https</c> URL (never <c>file:</c>, UNC or <c>ldap:</c>).</summary>
    internal static bool IsHttp(string? url) =>
        url is not null
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>The state of one certificate by its validity and the threshold in days; a null summary is unavailable.</summary>
    internal static (PkiHealthStatus Status, DateTimeOffset? ValidUntil) CertificateState(CertificateSummary? summary, int warningDays, DateTimeOffset now)
    {
        if (summary is null)
        {
            return (PkiHealthStatus.Unavailable, null);
        }

        if (now > summary.NotAfter)
        {
            return (PkiHealthStatus.Expired, summary.NotAfter);
        }

        if (now < summary.NotBefore)
        {
            return (PkiHealthStatus.Unknown, summary.NotAfter);
        }

        var threshold = TimeSpan.FromDays(Math.Max(0, warningDays));
        return (summary.NotAfter - now <= threshold ? PkiHealthStatus.Expiring : PkiHealthStatus.Ok, summary.NotAfter);
    }

    /// <summary>The state of one CRL (decision 2): a null summary is unavailable; no <c>NextUpdate</c> is unknown, never expired.</summary>
    internal static (PkiHealthStatus Status, DateTimeOffset? ValidUntil) CrlState(CrlSummary? summary, DateTimeOffset now)
    {
        if (summary is null)
        {
            return (PkiHealthStatus.Unavailable, null);
        }

        if (summary.NextUpdate is not { } next)
        {
            return (PkiHealthStatus.Unknown, null);
        }

        if (now > next)
        {
            return (PkiHealthStatus.Expired, next);
        }

        if (now < summary.ThisUpdate)
        {
            return (PkiHealthStatus.Unknown, next);
        }

        var margin = TimeSpan.FromTicks(Math.Max(MinimumCrlMargin.Ticks, (next - summary.ThisUpdate).Ticks / 5));
        return (next - now < margin ? PkiHealthStatus.Expiring : PkiHealthStatus.Ok, next);
    }

    private static IEnumerable<PkiCaObject> Stores(PkiSnapshot snapshot) =>
        snapshot.NtAuth is { } ntAuth ? [.. snapshot.RootCas, .. snapshot.AiaObjects, ntAuth] : [.. snapshot.RootCas, .. snapshot.AiaObjects];

    /// <summary>
    /// <paramref name="candidate"/> is the issuer of <paramref name="subject"/>: its subject is the issuer DN, and its Subject Key
    /// Identifier is the Authority Key Identifier when both are present.
    /// </summary>
    private static bool IsIssuerOf(CertificateSummary candidate, CertificateSummary subject) =>
        string.Equals(candidate.Subject, subject.Issuer, StringComparison.OrdinalIgnoreCase)
        && (candidate.SubjectKeyIdentifier is null || subject.AuthorityKeyIdentifier is null
            || string.Equals(candidate.SubjectKeyIdentifier, subject.AuthorityKeyIdentifier, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A CRL a location may hold: its Authority Key Identifier is the expected one (the certificate's for its CDP, the base CRL's
    /// for the CA's own delta locations) when both are present.
    /// </summary>
    private static bool IsCrlOfIssuer(CrlSummary crl, string? authorityKey) =>
        crl.AuthorityKeyIdentifier is null || authorityKey is null
        || string.Equals(crl.AuthorityKeyIdentifier, authorityKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>What <see cref="Evaluate"/> needs while it walks the CAs.</summary>
    private sealed class Context(PkiSnapshot snapshot, PkiHealthThresholds thresholds, DateTimeOffset now, IReadOnlyList<PkiHttpCheckResult> http)
    {
        private readonly DistinguishedName? _publicKeyServices =
            DistinguishedName.TryParse(snapshot.ConfigurationDn, out var configuration) && !configuration.IsRoot
                ? configuration.Child("CN", "Services").Child("CN", "Public Key Services")
                : null;

        public (PkiHealthStatus Status, DateTimeOffset? ValidUntil) CertificateState(CertificateSummary? summary, bool isCa) =>
            PkiHealth.CertificateState(summary, isCa ? thresholds.CaWarningDays : thresholds.EndEntityWarningDays, now);

        public void AddCaRows(PkiEnrollmentService ca, List<PkiHealthRow> rows)
        {
            var current = ca.Certificates.Count > 0 ? ca.Certificates[0] : null;
            var (status, until) = CertificateState(current?.Summary, isCa: true);
            rows.Add(new PkiHealthRow(ca.ObjectGuid, PkiHealthItem.CaCertificate, 0, status, until, null));
            var deltaLocations = 0;
            if (current?.Summary is { } summary)
            {
                AddLocations(ca.ObjectGuid, PkiHealthItem.AiaLocation, summary.AiaCaIssuers, summary, summary.AuthorityKeyIdentifier, 0, rows);
                AddLocations(ca.ObjectGuid, PkiHealthItem.CdpLocation, summary.CrlDistributionPoints, summary, summary.AuthorityKeyIdentifier, 0, rows);
                deltaLocations = AddLocations(ca.ObjectGuid, PkiHealthItem.DeltaCrlLocation, summary.FreshestCrl, summary, summary.AuthorityKeyIdentifier, 0, rows);
            }

            var lists = snapshot.CrlLists.Where(l => l.IssuerCaGuid == ca.ObjectGuid).ToList();
            if (lists.Count == 0)
            {
                rows.Add(new PkiHealthRow(ca.ObjectGuid, PkiHealthItem.BaseCrl, 0, PkiHealthStatus.Unavailable, null, null));
                return;
            }

            var baseIndex = 0;
            var deltaIndex = 0;
            var ownDeltaUrls = new HashSet<string>(StringComparer.Ordinal);
            foreach (var list in lists)
            {
                var (baseStatus, baseUntil) = CrlState(list.Base?.Summary, now);
                rows.Add(new PkiHealthRow(ca.ObjectGuid, PkiHealthItem.BaseCrl, baseIndex++, baseStatus, baseUntil, null));
                if (list.Delta is not null || list.Base?.Summary?.FreshestCrl.Count > 0)
                {
                    var (deltaStatus, deltaUntil) = CrlState(list.Delta?.Summary, now);
                    rows.Add(new PkiHealthRow(ca.ObjectGuid, PkiHealthItem.DeltaCrl, deltaIndex++, deltaStatus, deltaUntil, null));
                }

                // Where the CA's own delta CRL is published: the Freshest CRL of its base CRL (AD CS writes it there for the
                // CDP URLs marked "Include in CRLs" – the CA certificate itself rarely carries one), checked against the
                // base CRL's authority key. HttpUrls lists these URLs for the probe, so their results need a row.
                if (list.Base?.Summary is { } baseCrl)
                {
                    deltaLocations = AddLocations(ca.ObjectGuid, PkiHealthItem.DeltaCrlLocation, [.. baseCrl.FreshestCrl.Where(ownDeltaUrls.Add)], null,
                        baseCrl.AuthorityKeyIdentifier, deltaLocations, rows);
                }
            }
        }

        /// <summary>
        /// One row per URL from <paramref name="index"/> on (returns the next index): <paramref name="certificate"/> is the
        /// certificate whose issuer an AIA location must hold (null for a CRL's own locations), <paramref name="authorityKey"/> the
        /// Authority Key Identifier a CRL found at a CDP / delta location must carry (null: any).
        /// </summary>
        private int AddLocations(Guid ca, PkiHealthItem item, IReadOnlyList<string> urls, CertificateSummary? certificate, string? authorityKey, int index, List<PkiHealthRow> rows)
        {
            foreach (var url in urls)
            {
                var (status, until) = LocationState(item, url, certificate, authorityKey);
                rows.Add(new PkiHealthRow(ca, item, index++, status, until, url));
            }

            return index;
        }

        private (PkiHealthStatus Status, DateTimeOffset? ValidUntil) LocationState(PkiHealthItem item, string url, CertificateSummary? certificate, string? authorityKey)
        {
            if (IsHttp(url))
            {
                return HttpState(item, url, certificate, authorityKey);
            }

            return TryParseLdapUrl(url, out var dn, out var attribute) ? LdapState(item, dn, attribute, certificate, authorityKey) : (PkiHealthStatus.Unknown, null);
        }

        private (PkiHealthStatus Status, DateTimeOffset? ValidUntil) HttpState(PkiHealthItem item, string url, CertificateSummary? certificate, string? authorityKey)
        {
            var kind = item switch
            {
                PkiHealthItem.AiaLocation => PkiUrlKind.AiaCaIssuers,
                PkiHealthItem.CdpLocation => PkiUrlKind.Cdp,
                _ => PkiUrlKind.DeltaCdp,
            };
            var result = http.FirstOrDefault(r => r.Kind == kind && string.Equals(r.Url, url, StringComparison.Ordinal));
            return result?.Outcome switch
            {
                null or PkiHttpOutcome.NotChecked => (PkiHealthStatus.Unknown, null),
                PkiHttpOutcome.Ok when kind == PkiUrlKind.AiaCaIssuers =>
                    result.Certificate is { } issuer && certificate is not null && IsIssuerOf(issuer, certificate)
                        ? CertificateState(issuer, isCa: true)
                        : (PkiHealthStatus.Unavailable, null),
                PkiHttpOutcome.Ok =>
                    result.Crl is { } crl && IsCrlOfIssuer(crl, authorityKey) && crl.IsDelta == (kind == PkiUrlKind.DeltaCdp)
                        ? CrlState(crl, now)
                        : (PkiHealthStatus.Unavailable, null),
                _ => (PkiHealthStatus.Unavailable, null),
            };
        }

        private (PkiHealthStatus Status, DateTimeOffset? ValidUntil) LdapState(PkiHealthItem item, DistinguishedName dn, string? attribute, CertificateSummary? certificate, string? authorityKey)
        {
            if (_publicKeyServices is null || !dn.IsDescendantOf(_publicKeyServices))
            {
                // Outside the subtree the snapshot reads: not checked, never "missing".
                return (PkiHealthStatus.Unknown, null);
            }

            if (item == PkiHealthItem.AiaLocation)
            {
                var store = Stores(snapshot).FirstOrDefault(o => DistinguishedName.TryParse(o.Dn, out var storeDn) && storeDn.Equals(dn));
                var issuer = store?.Certificates.Concat(store.CrossCertificates)
                    .Select(c => c.Summary)
                    .OfType<CertificateSummary>()
                    .Where(s => certificate is not null && IsIssuerOf(s, certificate))
                    .OrderByDescending(s => s.NotAfter)
                    .FirstOrDefault();
                return issuer is null ? (PkiHealthStatus.Unavailable, null) : CertificateState(issuer, isCa: true);
            }

            var list = snapshot.CrlLists.FirstOrDefault(l => DistinguishedName.TryParse(l.Dn, out var listDn) && listDn.Equals(dn));
            if (list is null)
            {
                return (PkiHealthStatus.Unavailable, null);
            }

            var delta = attribute is not null
                ? string.Equals(attribute, PkiAttributes.DeltaRevocationList, StringComparison.OrdinalIgnoreCase)
                : item == PkiHealthItem.DeltaCrlLocation;
            var crl = (delta ? list.Delta : list.Base)?.Summary;
            return crl is not null && IsCrlOfIssuer(crl, authorityKey) ? CrlState(crl, now) : (PkiHealthStatus.Unavailable, null);
        }

        /// <summary>
        /// The DN and first attribute of an <c>ldap:///dn?attribute?…</c> URL without a host (percent-decoded, as AD CS writes
        /// spaces as <c>%20</c>); false for <c>ldap://host/…</c>, any other scheme and an unparseable DN.
        /// </summary>
        private static bool TryParseLdapUrl(string url, out DistinguishedName dn, out string? attribute)
        {
            dn = DistinguishedName.Root;
            attribute = null;
            const string Prefix = "ldap:///";
            if (!url.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rest = url[Prefix.Length..];
            var query = rest.IndexOf('?', StringComparison.Ordinal);
            var dnPart = query < 0 ? rest : rest[..query];
            if (query >= 0)
            {
                var attributes = rest[(query + 1)..];
                var end = attributes.IndexOf('?', StringComparison.Ordinal);
                var first = (end < 0 ? attributes : attributes[..end]).Split(',')[0].Trim();
                attribute = first.Length == 0 ? null : Uri.UnescapeDataString(first);
            }

            return DistinguishedName.TryParse(Uri.UnescapeDataString(dnPart), out dn) && !dn.IsRoot;
        }
    }
}
