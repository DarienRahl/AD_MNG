# Track P – AD Certificate Services and certificates (design)

Product description (Polish): README, *Ścieżka P – Urząd certyfikacji (AD CS) i certyfikaty*. This note is the
implementation design of stage **P1** (read-only overview of the forest PKI, LDAP only) and is **binding** for the P1
implementers: the public types of 2.11 are the contract, each heading of 2.1–2.6 names its owner, and 2.12 fixes which
files each agent may touch. Later stages (README track P, *Etapy*, operator decision 2026-11-09): P2 certificate
issuance (requests, CSR, enrollment from templates, renewal), P3 safe changes through the change plan (*Napraw…*), P4
work with the CA host, P5 links (Watchdog, E, K, U, S); each gets its own top-level section later, the way the K note
grew from §14 to §21; do not renumber 2.x after the P1 contract commit (XML docs and `PkiFindingCodes` will quote these
section numbers). Revised after the P1 design review (2.13).

## 1. Decisions (operator, 2026-11)

The operator chose **"P1 – urząd certyfikacji"** on 2026-11-09 (README track P, *Etapy*: P1). P1 is read-only (CLAUDE.md
rule 5: nothing is written), LDAP only (rule 4: everything through the window session's DC). The lead's decisions:

1. **Scope** = README track P, stage P1 (read-only, LDAP). The only traffic outside the window's LDAP session:
   (a) a **TLS handshake probe** of a DC's LDAPS / GC-LDAPS certificate (port 636, and 3269 only for a DC with the GC
   flag) – no LDAP bind, no client certificate, no application data (only the TLS handshake records), run only on
   explicit navigation or a button, at most 4 in parallel, 5 s timeout each; (b) **HTTP checks of the AIA / CDP / OCSP
   URLs** read from certificates, run **only** when the operator presses *Sprawdź adresy HTTP* – `http`/`https` only
   (never `file:`, UNC or an `ldap://host` URL), no credentials sent to the target, at most 3 redirects (each target
   checked again), 15 s per URL including the body, 20 MB cap, responses parsed in memory and kept only in the window's
   snapshot service, never fetched automatically. Chain building never downloads (`DisableCertificateDownloads = true`,
   `RevocationMode = NoCheck`, `TrustMode = CustomRootTrust` with the roots read from AD) – also inside the probe's
   `SslStream` and the HTTPS client (`CertificateChainPolicy`), so neither can reach an AIA URL on its own.
2. **Expiry thresholds** (settings, allowed on disk – no directory data): one setting *Próg ostrzeżenia o wygasaniu
   certyfikatów* (default **30 days**) for end-entity certificates (DC LDAPS, KRA, published certificates) and a separate
   default **180 days** for CA certificates (renewing a CA has to be planned). A CRL *wygasa* when less than
   `max(1 day, 20 % of (nextUpdate − thisUpdate))` is left; a CRL without `nextUpdate` is *nieznany*, never *wygasła*.
   Stored in `AppSettings.Pki` like the other UI settings and editable in the Settings window. Health states are computed
   **when shown** from the snapshot, the current settings and the clock – never cached with the snapshot.
3. **CA-enrollment assumption.** Until P4 reads the CA's own security, the ESC review assumes every CA grants *Enroll* to
   *Authenticated Users* (the Windows default) and every enrollment-based finding says so (`AssumesCaEnroll`). A template
   **no enterprise CA publishes** is reported one level lower with the note *szablon nie jest wystawiany* (`NotPublished`)
   – exploitable only once it is published. A default, patched CA adds the SID extension (`szOID_NTDS_CA_SECURITY_EXT`)
   to certificates whose subject is built from AD, so a default forest raises neither ESC9 (template flag clear) nor ESC16
   (CA `DisableExtensionList` – P4); the review says that a default build is not vulnerable.
4. **Principal classes for ESC** (decide a finding's severity; one `PrincipalClassifier` shared by 2.3 and 2.4). SIDs of
   the form `S-1-5-21-<domain>-<RID>` are classified **by RID for any domain of the forest**, not only the window's:
   * *Tier 0 / infrastructure* → **no finding**: `PrivilegedGroupCatalog` Tier 0 groups and every account or group that
     reaches one through `GroupGraph`, SYSTEM (S-1-5-18), Enterprise Domain Controllers (S-1-5-9), PRINCIPAL SELF
     (S-1-5-10), CREATOR OWNER (S-1-3-0), Administrators (S-1-5-32-544), the RIDs 500 (Administrator), 512 (Domain
     Admins), 516 (Domain Controllers), 517 (Cert Publishers), 518 (Schema Admins), 519 (Enterprise Admins), 521 (RODCs),
     498 (Enterprise RODCs), 526 (Key Admins), 527 (Enterprise Key Admins), and the CA's own computer account (resolved in
     2.1). This is Locksmith's `SafeUsers` set plus the catalogue, so a default Windows PKI yields no ESC noise.
   * *Broad* → **High / Critical**: Everyone (S-1-1-0), Authenticated Users (S-1-5-11), Anonymous (S-1-5-7), This
     Organization (S-1-5-15), Interactive (S-1-5-4), Network (S-1-5-2), BUILTIN\Users (S-1-5-32-545), Guests
     (S-1-5-32-546) and the RIDs 513 (Domain Users), 514 (Domain Guests), 515 (Domain Computers) of any domain.
   * Any **other** principal → **Medium** (*konto lub grupa spoza Tier 0*) with the account count; a trustee of another
     domain that the window cannot expand is *Other* with the count *nieznana* (`TemplateTrustee.IsResolved = false`).
5. **"Kto może się zarejestrować / zmienić"** is a **separate pure Core evaluator** (not U4b): AccessCheck semantics over
   the template DACL – each right decided by the first ACE in DACL order that applies to the token and covers that right
   (deny ACEs honoured), inherit-only ACEs and ACEs whose `InheritedObjectType` is not `pKICertificateTemplate` skipped,
   GENERIC_* normalized – for the rights *Enroll*, *AutoEnroll*, *FullControl*, *WriteDacl*, *WriteOwner*,
   *WriteProperty* (all properties, a sensitive template attribute, or another attribute) and *Owner*. Trustee level
   first (immediate), concrete accounts through `GroupGraph` + **one** streamed account pass per window (counts + the first
   20 names, disabled accounts kept apart, cancellable, progress, decisions memoised per distinct token), which also
   collects the accounts' `altSecurityIdentities` for ESC14 and the readiness report; trustees from other forest domains
   are reported unresolved. A template whose security descriptor was not returned is *nieoceniony*, never "safe".
6. **U4b** (`DirectoryAclGrantSource`) additionally reads the PKI containers of the configuration partition (sites still
   skipped). Provisioning trustees on PKI objects: the forest root's Domain Admins (`<root>-512`, owners of the default
   templates and containers), Cert Publishers (`-517` of **any** forest domain) on AIA / CDP / Certification Authorities /
   NTAuth / KRA and the enrollment services, the CA computer on its enrollment service and its CDP / AIA objects – so a
   default Windows PKI yields no false findings. Owners are reported only for containers, templates, enrollment services,
   NTAuth and OID objects that carry `msDS-OIDToGroupLink`. A typed entry point (`CollectPublicKeyAsync`) serves ESC5 and
   ESC14 (writes of `altSecurityIdentities` on Tier 0 accounts, a new `AclRight.WriteAltSecurityIdentities`). A **separate
   sample** keeps the existing exact-set tests unchanged; UI tests seed the PKI sample only where they need it.
7. **userPKCS12** goes into `SecretAttributes.WellKnownNames`, and a new `SecretAttributes.IsNeverShown` (key material:
   `userPKCS12`, the roaming credentials, BitLocker key packages, managed and encrypted passwords – every secret that is
   binary or in that list) is **masked everywhere, also in the attribute editor**: `ValueText` shows the mask, the row is
   read-only (no value editor, nothing staged) and cannot be copied (README promises masking *wszędzie, także w edytorze
   atrybutów* for these). Operator-readable secrets (LAPS clear-text password, BitLocker recovery password, confidential
   text attributes) keep today's display and secure copy – changing that is an operator question (2.13). The Watchdog
   consequence (it can never watch `userPKCS12`) is stated and tested.
8. **Shared Core.** Template flag tables, period decoding, certificate / CRL summaries, the health rules, the
   `msDS-KeyCredentialLink` and `altSecurityIdentities` parsers, the rights evaluator and the ESC analyzer all live in
   `AdMng.Core/Pki` (no Windows types) so the Watchdog can reuse them (P5). The Watchdog's `FlagNames` takes the names of
   its two certificate tables from `PkiFlags` in a **separate, behaviour-neutral** commit: `PkiFlags.Select` keeps
   exactly today's bits in today's order (no `SUBJECT_ALT_REQUIRE_SPN`, none of `0x2000`–`0x40000`; unknown bits still
   `0x…`; `TableOf` unchanged), pinned by a golden test committed **before** it. `userPKCS12` becoming secret is the only
   other Watchdog-visible change.
9. **UI.** A tree node *Usługi kluczy publicznych* under the domain node on its own base (`PkiNodeViewModel`, not the GPO
   base; a *Widok* toggle like the GPO nodes; inserted after the GPO nodes); documents on an LDAP-only `PkiDocumentBase`
   (no SYSVOL, no PDC note); a **scoped per-window** `PkiSnapshotService` caching the PKI read, the enrollment expansion,
   the DC certificate probes and the HTTP results (dropped on session change, F5 rebuilds). Documents: **przegląd**
   (urzędy przedsiębiorstwa with the CA chain/validity/templates/CES, zaufane urzędy główne i pośrednie, NTAuth matched to
   enterprise CAs, CDP lists with CRL numbers/dates/counts, KRA, OID with group links, a *pkiview*-style health tab, a
   DC-LDAPS tab), **szablony** (the README columns, decoded flags as Polish/English sentences with the technical bit name
   in a tooltip, periods in days/weeks/years, issuing CAs, unpublished templates, expanded enrollment rights with the
   account count and navigation to the accounts and groups, who can modify, a compare-two pane, export on demand),
   **przegląd bezpieczeństwa AD CS** (K6-diagnostics-style: severity chips, filters, rows with the ESC number, Certipy /
   Locksmith / MDI ids, condition, attack path, affected object, principals with counts – no *Napraw* in P1, only
   *Otwórz szablon/urząd* navigation; export CSV/HTML on demand), **gotowość na silne mapowanie i NTAuth** (accounts with
   weak `altSecurityIdentities`, authentication templates without the SID extension, logon-capable CAs not in NTAuth, DC
   LDAPS certificates without a proper SAN/EKU, KDC certificates published on the DC objects). `EventLogLauncher` has no
   preset query, so the KDC events 39/40/41/45/21 row says *P5* and no track-E file changes. Read-only property-sheet tabs
   *Opublikowane certyfikaty* (user, computer, contact), *Mapowania nazw* (user, computer) with KB5014754 strength and
   *Poświadczenia kluczy* (user, computer) with key id, device id, source, created / last-logon, usage – read lazily, no
   writes. From a CA to its computer object and to the K1 remote actions (by the computer DN resolved in 2.1).
10. **Screenshots 36–39**: 36 overview/health, 37 templates with enrollment rights, 38 security review, 39 readiness
    report. The user doc `docs/CERTYFIKATY.md` (Polish) is written in the integration step; it explains every difference
    from `certipy find -vulnerable` and Locksmith (README completion criterion; 2.4 lists them).
11. **Tests.** A fake-directory sample `AddPublicKeySample` with Polish names (urzędy *Kontoso Główny CA*, *Kontoso
    Wystawiający CA*, szablony with diacritics, one deliberately vulnerable template per LDAP ESC and a safe look-alike
    that must **not** be flagged); certificates / CRLs generated in tests with fixed dates around the fake clock
    (2026-10-13); TLS-probe tests against an in-process TLS server on `localhost` (server certificate PFX-round-tripped so
    SChannel accepts it on Windows CI); HTTP-check tests with a fake handler. Samba has no AD CS (lab: only the empty PKI
    containers + an LDAPS probe of the lab DC at `localhost`).

## 2. P1 – read-only overview

P1 reads the forest PKI once per window into an in-memory snapshot and shows it in four documents, a tree node and three
property-sheet tabs; a pure Core layer decodes the templates, parses the certificates, evaluates the health rules (at
display time) and evaluates both the enrollment rights (who can get a certificate / change a template) and the ESC
findings. The snapshot, the enrollment expansion, the probe and HTTP results are held by the scoped `PkiSnapshotService`
and the open documents only; nothing is written (rule 5) and nothing but an operator export touches disk (rule 1). The
four Core waves (C1–C4) produce the snapshot and parsers, the template decoders, the rights evaluator and the ESC analyzer
against the contract of 2.11; the three Presentation/App waves (W5–W7) build the node, the documents, the property tabs,
the attribute-editor mask and the TLS probe implementation on the merged Core.

### 2.1 Core – the PKI overview reader, the certificate/CRL parsers and the TLS-probe contract (C1)

**`PkiOverviewReader.ReadAsync(IDirectoryReader reader, PkiReadOptions options, IProgress<PkiReadProgress>? progress,
CancellationToken ct)`** (`src/AdMng.Core/Pki/PkiOverviewReader.cs`, pure except for the LDAP reads) runs one paged
subtree search of `CN=Public Key Services,CN=Services,<config>` (DN built with
`DistinguishedName.Parse(configDn).Child("CN","Services").Child("CN","Public Key Services")`, rule 3), filter
`LdapFilter.Or` of `objectClass=` each of `PkiClasses.All` (`pKIEnrollmentService`, `pKICertificateTemplate`,
`certificationAuthority`, `cRLDistributionPoint`, `msPKI-Enterprise-Oid`, `msPKI-PrivateKeyRecoveryAgent`,
`container`), attributes `PkiAttributes.OverviewAttributes`, `ReadSecurityDescriptor = true`, `ExtendedDn = true`,
`PageSize = options.PageSize` (1000) – the search flags `ConfigurationPartitionWatcher.PublicKeyInfrastructureAsync`
uses (the watcher searches only three of these classes), so one DC answers the whole forest PKI (rule 4). Multi-valued
binary attributes (`cACertificate`, `crossCertificatePair`, `certificateRevocationList`, `deltaRevocationList`,
`authorityRevocationList`, `userCertificate` on KRA) are read with `obj.TryGet(name, out var v) && v.OfType<byte[]>()`
(never `GetAllValuesAsync`, which decodes UTF-8).

**Read notices** (`PkiReadNotice(PkiReadNoticeCode Code, string? ObjectDn, Guid? ObjectGuid)`, the code set is fixed by
the contract commit): one subtree search cannot fail per container, and AD silently omits objects the caller may not
list, so the reader reports what it can observe – the base search failing with `NoSuchObject` → `BaseNotFound` (an empty
snapshot: no AD CS, the Samba lab) or `AccessDenied` → `BaseAccessDenied` (as K6, a visible "not evaluated" state, never a
crash); after the search every expected well-known object that did not come back (`CN=NTAuthCertificates`,
`CN=Enrollment Services`, `CN=Certificate Templates`, `CN=Certification Authorities`, `CN=AIA`, `CN=CDP`, `CN=OID`,
`CN=KRA`) → `ContainerMissing` (*niewidoczny – brak obiektu albo brak prawa odczytu*); a template returned without
`nTSecurityDescriptor` (no READ_CONTROL) → `SecurityDescriptorUnreadable` – its rights are *nieocenione* in 2.3 and it is
never treated as "nobody can enroll"; malformed DER → `CertificateMalformed` / `CrlMalformed` (the row stays, its summary
is null); the CA computer lookups below → `CaComputerUnresolved` / `CaComputerInOtherDomain`; 2.3 adds
`TrusteeUnresolved` to the same enum (already in the contract).

**`PkiSnapshot`** (immutable record; every record of `AdMng.Core.Pki` has a hand-written value-free `ToString`, 2.7):
`ConfigurationDn`, `ReadAt`; the enterprise CAs (`PkiEnrollmentService`: `Dn`, `ObjectGuid`, `Name` (cn), `DisplayName`,
`HostName` from `dNSHostName`, `ComputerDn` / `ComputerSid` (resolved below, null when unresolved), `Certificates` from
`cACertificate` – renewals included, newest first –, `PublishedTemplates` (the `certificateTemplates` cn values),
`EnrollmentServers` (the CES URIs of `msPKI-Enrollment-Servers`), `InNtAuth`); the trusted roots
(`CN=Certification Authorities`) and the AIA objects (`CN=AIA` – intermediates and cross certificates) as `PkiCaObject`
(`Dn`, `ObjectGuid`, `Name`, `Store`, `Certificates`, `CrossCertificates`); the NTAuth store (`CN=NTAuthCertificates`:
`cACertificate`, `PkiCaObject` with `Store = NtAuth`); the CDP lists (`PkiCrlList`: `Dn`, `ObjectGuid`, `Name`,
`HostName` (the parent container), `Base` / `Delta` as `PkiCrl(byte[] Der, CrlSummary? Summary)`, `IssuerCaGuid` – the
enterprise CA whose certificate subject and Subject Key Identifier equal the CRL issuer and Authority Key Identifier;
never matched by RDN text, CA names in `CN=CDP` / `CN=AIA` are certutil-sanitized); the KRA agents (`PkiKraAgent`:
`userCertificate`); the OID objects (`PkiOidObject`: `Dn`, `ObjectGuid`, `Oid` = `msPKI-Cert-Template-OID`,
`DisplayName`, `Flags`, `GroupLinkDn` = `msDS-OIDToGroupLink`); the templates (`CertificateTemplate.Of`, 2.2); the
notices; and `TrustMaterial` (`PkiTrustMaterial(Roots, Intermediates, NtAuth)` – raw DER lists for chain building).
Every certificate is a `PkiCertificate(byte[] Der, CertificateSummary? Summary)`. `InNtAuth` is true when any NTAuth
certificate has the SHA-256 of any of the CA's certificates. Health rows are **not** part of the snapshot (2.4,
`PkiHealth.Evaluate`, computed at display time).

**CA computer resolution** (decision 4, ESC5, navigation): for each `pKIEnrollmentService` whose `dNSHostName` lies in the
window's domain (`SessionInfo.DomainDnsName` suffix) the reader runs one search of the default naming context,
`LdapFilter.And(ObjectKind.Computer.ToLdapFilter(), LdapFilter.Or(LdapFilter.Equal("dNSHostName", host),
LdapFilter.Equal("sAMAccountName", label + "$")))` (rule 3; on the session's DC), and stores `ComputerDn` / `ComputerSid`;
no match → `CaComputerUnresolved`; a host of another domain → `CaComputerInOtherDomain` (its SID stays unknown: the CA
computer is then *Other*, not Tier 0, and the finding says so). `PkiReadOptions.ResolveCaComputers` (default true) turns
it off for the Watchdog later.

**Domain controllers.** `PkiOverviewReader.ReadDomainControllersAsync(IDirectoryReader reader, CancellationToken ct)`
reads the window domain's DCs (`PrivilegedAccessScanner.DomainControllerFilter`; `dNSHostName`, `objectSid`,
`userAccountControl`, `userCertificate`) into `PkiDomainController(Dn, HostName, IsReadOnly, Certificates)` – the
published certificates feed the KDC indicator below; the GC flag comes from `DomainControllerCatalog.ReadAsync` (W5).

**Parsers** (`src/AdMng.Core/Pki/`, pure, null on malformed input):
* `CertificateSummary.Of(ReadOnlySpan<byte> der)` – via `X509CertificateLoader.LoadCertificate` (never
  `new X509Certificate2(byte[])`, SYSLIB0057; the `X509Certificate2` is disposed after the fields are copied): `Subject`,
  `Issuer`, `SerialNumber`, `NotBefore` / `NotAfter`, SAN DNS / UPN / email / URI (incl. the
  `tag:microsoft.com,2022-09-14:sid:` URL), `Ekus`, `ApplicationPolicies` (`1.3.6.1.4.1.311.21.10`), key algorithm and
  size, signature algorithm, `Thumbprint` (SHA-1) and `Sha256`, `SubjectKeyIdentifier`, `AuthorityKeyIdentifier`,
  `IsCa` + path length, `AiaCaIssuers`, `AiaOcsp`, `CrlDistributionPoints` (URLs), `TemplateName` (v1 template extension
  `1.3.6.1.4.1.311.20.2`), `TemplateOid` + major/minor version (`1.3.6.1.4.1.311.21.7`), `SidExtension`
  (`1.3.6.1.4.1.311.25.2`), `IsSelfSigned`. Modelled on `WatchdogServerCertificate.From`.
* `CrlSummary.Of(ReadOnlySpan<byte> der)` – .NET 10 has no CRL reader, so parse with `System.Formats.Asn1.AsnReader`:
  `Issuer`, `AuthorityKeyIdentifier`, `CrlNumber` (non-critical extension `2.5.29.20`), `IsDelta` + `BaseCrlNumber`
  (Delta CRL Indicator `2.5.29.27`), `ThisUpdate`, `NextUpdate` (nullable), `RevokedCount`. Never the individual revoked
  serials in P1 (rule 1 – export only). `authorityRevocationList` is parsed the same way.

**TLS probe contract** (`src/AdMng.Core/Pki/Tls/`, no Windows types, implemented by W7 in `AdMng.Directory`, 2.6):
`IDomainControllerTlsProbe.ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)` opens TCP to
`host:port` (636 / 3269), does the TLS handshake only – no LDAP bind, no client certificate, no application data – and
returns `TlsProbeResult(Host, Port, TlsProbeOutcome Outcome, SslProtocols? Protocol, IReadOnlyList<byte[]> PresentedChain
/* DER, leaf first, as presented */, SslPolicyErrors? PolicyErrors)` whose `ToString() => $"{Port}: {Outcome}"`.
`TlsProbeOutcome` = `Ok | NameNotResolved | PortClosed | HandshakeFailed | Timeout | InvalidHost` (AD DS accepting TCP on
636 with no usable certificate and resetting mid-handshake is `HandshakeFailed`, reported as *DC bez certyfikatu LDAPS*,
not *unreachable*). `HostNames.IsValid` / `Normalize` (`AdMng.Core/Computers/Remote/HostNames.cs`) guard the target (IP
addresses refused; SNI and the name check need a name).

**`DcCertificateEvaluation.Evaluate(IReadOnlyList<byte[]> presentedChain, string dcFqdn, string domainDns,
PkiTrustMaterial trust, DateTimeOffset now) → DcCertificateInfo`** is pure and judges the **LDAPS** certificate only:
`NameMatchesDc` (a SAN DNS entry, else the subject CN, equals the DC FQDN), `NameMatchesDomain` (the domain DNS name –
needed for `ldaps://<domain>`; a warning, not an error), `HasServerAuth` (EKU Server Authentication `1.3.6.1.5.5.7.3.1`,
or no EKU), `IsTimeValid` + `DaysLeft` against `now`, `ChainsToAdRoot` (`X509Chain` with `TrustMode = CustomRootTrust`,
`CustomTrustStore` = `trust.Roots`, `ExtraStore` = the presented intermediates + `trust.Intermediates`, `RevocationMode =
NoCheck`, `DisableCertificateDownloads = true`, `VerificationTime = now.UtcDateTime` – tests on the 2026-10-13 fake clock
do not depend on the real one), `IssuerInNtAuth` (separately, against `trust.NtAuth`) and the chain status names.
`DcTlsCheck(Host, Port, Outcome, DcCertificateInfo? Certificate)` is the row W5 and W6 show.

**KDC indicator.** The LDAPS certificate is not necessarily the KDC's, so KDC readiness is **not** derived from the probe.
`KdcCertificateEvaluation.Evaluate(IReadOnlyList<byte[]> publishedCertificates, string dcFqdn, PkiTrustMaterial trust,
DateTimeOffset now) → KdcCertificateInfo(KdcReadinessState State, CertificateSummary? Best)` looks at the certificates
the DC object publishes in `userCertificate`: `Ready` (time-valid, EKU KDC Authentication `1.3.6.1.5.2.3.5` – or the
Smart-Card Logon + Server Authentication pair of the legacy *Domain Controller* template – and the issuer's certificate in
NTAuth), `NoKdcEku`, `IssuerNotInNtAuth`, `Expired`, or `NoPublishedCertificate` (publishing is optional: *nieznany*,
not a failure). It is labelled *z certyfikatów opublikowanych w AD*; the authoritative check of the KDC's own store is
P4/K1.

### 2.2 Core – certificate templates: model, flag tables and period decoders (C2)

**`CertificateTemplate.Of(DirectoryObject obj)`** (`src/AdMng.Core/Pki/CertificateTemplate.cs`, pure) builds the
template record from one `pKICertificateTemplate` object. The contract commit implements the identity members (`Dn`,
`ObjectGuid`, `Cn`, `DisplayName`, `SecurityDescriptor`) so C1 can test its reader in wave 1; C2 fills the rest:
`SchemaVersion` (`msPKI-Template-Schema-Version`, 1–4, **absent = 1**), `Revision` + `MinorRevision` (`revision`,
`msPKI-Template-Minor-Revision`), `Oid` (`msPKI-Cert-Template-OID`), `Ekus` (`pKIExtendedKeyUsage`),
`ApplicationPolicies` (`msPKI-Certificate-Application-Policy`), `EffectiveEku` (= `ApplicationPolicies` when
`SchemaVersion ≥ 2` and it is non-empty – the CA issues v2+ templates from it –, else `Ekus`), `KeyUsage`
(`pKIKeyUsage`), the four flag masks `NameFlag`, `EnrollmentFlag`, `PrivateKeyFlag`, `GeneralFlag`
(`msPKI-Certificate-Name-Flag`, `msPKI-Enrollment-Flag`, `msPKI-Private-Key-Flag`, `flags` – read with `GetInt32` cast to
`uint`, so the negative `msPKI-*` masks parse; absent = 0), `RaSignatures` (`msPKI-RA-Signature`, **absent = 0**),
`RaApplicationPolicies` and `CryptoSettings` (`msPKI-RA-Application-Policies`: on schema-4 templates a backquote-delimited
list of `name`type`value` triples – the `msPKI-RA-Application-Policies` triples are OIDs, `msPKI-Asymmetric-Algorithm`,
`msPKI-Hash-Algorithm`, `msPKI-Key-Usage`, `msPKI-Symmetric-Algorithm` … go to `CryptoSettings`; older templates hold
plain OIDs), `RaIssuancePolicies` (`msPKI-RA-Policies`), `IssuancePolicies` (`msPKI-Certificate-Policy`), `Validity` /
`RenewalOverlap` (`pKIExpirationPeriod`, `pKIOverlapPeriod`), `MinimalKeySize` (`msPKI-Minimal-Key-Size`),
`CryptoProviders` (`pKIDefaultCSPs`), `SupersededTemplates` (`msPKI-Supersede-Templates`) and `SecurityDescriptor` (raw
bytes, null when not returned – 2.1). Derived: `EnrolleeSuppliesSubject` (name flag `0x1`), `RequiresManagerApproval`
(enrollment flag `0x2`), `NoSecurityExtension` (enrollment flag `0x80000`), `RequiresAuthorizedSignature`
(`RaSignatures ≥ 1`).

**`PkiFlags`** (`src/AdMng.Core/Pki/PkiFlags.cs`) holds the four tables as `IReadOnlyList<PkiFlagEntry>`
(`PkiFlagEntry(long Mask, string Name, bool IsReserved)`), keyed by `PkiFlagAttribute` (`NameFlag`, `EnrollmentFlag`,
`PrivateKeyFlag`, `GeneralFlag` – the general `flags` table is chosen by the template class, never by attribute name:
`groupPolicyContainer` also has `flags`), the multi-bit fields `PrivateKeyFields` (`PkiFlagField(long Mask, int Shift,
string Name)`), `Decode(PkiFlagAttribute attribute, long value) → PkiDecodedFlags(Set, UnknownBits, Fields)` (unknown bits
as `0x…`, fields as numbers – *wersja klienta: n*), `Select(PkiFlagAttribute attribute, IReadOnlyList<long> bits)` (the
Watchdog subset, decision 8), and the EKU catalogue with the predicates below. Values checked against Certipy
`certipy/lib/constants.py` (commit 3ae3442; `TemplateFlag`, `EnrollmentFlag`, `PrivateKeyFlag`, `CertificateNameFlag`,
which cite MS-CRTD 2.4, 2.26, 2.27, 2.28) and the Watchdog's `FlagNames`:

* **`msPKI-Certificate-Name-Flag`** (MS-CRTD 2.28): `0x1` `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT`, `0x2` `CT_FLAG_ADD_EMAIL`,
  `0x4` `CT_FLAG_ADD_OBJ_GUID`, `0x8` `CT_FLAG_OLD_CERT_SUPPLIES_SUBJECT_AND_ALT_NAME`, `0x100`
  `CT_FLAG_ADD_DIRECTORY_PATH`, `0x10000` `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT_ALT_NAME`, `0x400000`
  `CT_FLAG_SUBJECT_ALT_REQUIRE_DOMAIN_DNS`, `0x800000` `CT_FLAG_SUBJECT_ALT_REQUIRE_SPN`, `0x1000000`
  `CT_FLAG_SUBJECT_ALT_REQUIRE_DIRECTORY_GUID`, `0x2000000` `CT_FLAG_SUBJECT_ALT_REQUIRE_UPN`, `0x4000000`
  `CT_FLAG_SUBJECT_ALT_REQUIRE_EMAIL`, `0x8000000` `CT_FLAG_SUBJECT_ALT_REQUIRE_DNS`, `0x10000000`
  `CT_FLAG_SUBJECT_REQUIRE_DNS_AS_CN`, `0x20000000` `CT_FLAG_SUBJECT_REQUIRE_EMAIL`, `0x40000000`
  `CT_FLAG_SUBJECT_REQUIRE_COMMON_NAME`, `0x80000000` `CT_FLAG_SUBJECT_REQUIRE_DIRECTORY_PATH`.
* **`msPKI-Enrollment-Flag`** (MS-CRTD 2.26): `0x1` `CT_FLAG_INCLUDE_SYMMETRIC_ALGORITHMS`, `0x2`
  `CT_FLAG_PEND_ALL_REQUESTS` (CA-manager approval), `0x4` `CT_FLAG_PUBLISH_TO_KRA_CONTAINER`, `0x8`
  `CT_FLAG_PUBLISH_TO_DS`, `0x10` `CT_FLAG_AUTO_ENROLLMENT_CHECK_USER_DS_CERTIFICATE`, `0x20` `CT_FLAG_AUTO_ENROLLMENT`,
  `0x40` `CT_FLAG_PREVIOUS_APPROVAL_VALIDATE_REENROLLMENT`, `0x80` `CT_FLAG_DOMAIN_AUTHENTICATION_NOT_REQUIRED`, `0x100`
  `CT_FLAG_USER_INTERACTION_REQUIRED`, `0x200` `CT_FLAG_ADD_TEMPLATE_NAME`, `0x400`
  `CT_FLAG_REMOVE_INVALID_CERTIFICATE_FROM_PERSONAL_STORE`, `0x800` `CT_FLAG_ALLOW_ENROLL_ON_BEHALF_OF`, `0x1000`
  `CT_FLAG_ADD_OCSP_NOCHECK`, `0x2000` `CT_FLAG_ENABLE_KEY_REUSE_ON_NT_TOKEN_KEYSET_STORAGE_FULL`, `0x4000`
  `CT_FLAG_NOREVOCATIONINFOINISSUEDCERTS`, `0x8000` `CT_FLAG_INCLUDE_BASIC_CONSTRAINTS_FOR_EE_CERTS`, `0x10000`
  `CT_FLAG_ALLOW_PREVIOUS_APPROVAL_KEYBASEDRENEWAL_VALIDATE_REENROLLMENT`, `0x20000`
  `CT_FLAG_ISSUANCE_POLICIES_FROM_REQUEST`, `0x40000` `CT_FLAG_SKIP_AUTO_RENEWAL`, `0x80000`
  `CT_FLAG_NO_SECURITY_EXTENSION` (ESC9; Locksmith `Find-ESC9` tests `-band 0x80000`, the Watchdog uses `0x80000`). `0x80`
  and `0x200` are taken from Certipy's `EnrollmentFlag`; C2 checks them against MS-CRTD 2.26 and drops a bit the spec does
  not list (it then decodes as `0x…`).
* **`msPKI-Private-Key-Flag`** (MS-CRTD 2.27): `0x1` `CT_FLAG_REQUIRE_PRIVATE_KEY_ARCHIVAL`, `0x10`
  `CT_FLAG_EXPORTABLE_KEY`, `0x20` `CT_FLAG_STRONG_KEY_PROTECTION_REQUIRED`, `0x40`
  `CT_FLAG_REQUIRE_ALTERNATE_SIGNATURE_ALGORITHM`, `0x80` `CT_FLAG_REQUIRE_SAME_KEY_RENEWAL`, `0x100`
  `CT_FLAG_USE_LEGACY_PROVIDER`, `0x200` `CT_FLAG_EK_TRUST_ON_USE`, `0x400` `CT_FLAG_EK_VALIDATE_CERT`, `0x800`
  `CT_FLAG_EK_VALIDATE_KEY`, `0x1000` `CT_FLAG_ATTEST_PREFERRED`, `0x2000` `CT_FLAG_ATTEST_REQUIRED`, `0x4000`
  `CT_FLAG_ATTESTATION_WITHOUT_POLICY`, `0x200000` `CT_FLAG_HELLO_LOGON_KEY`. `CT_FLAG_ATTEST_NONE` is `0` (no bit: the
  sentence *bez zaświadczania klucza* when neither `0x1000` nor `0x2000` is set). Two **fields**, decoded as numbers, not
  flags: `0x000F0000` (shift 16) the minimum CA version and `0x0F000000` (shift 24) the minimum client version
  (`CTPRIVATEKEY_FLAG_SERVERVERSION_MASK` / `CLIENTVERSION_MASK`).
* **general `flags`** (MS-CRTD 2.4): `0x2` `CT_FLAG_ADD_EMAIL`, `0x8` `CT_FLAG_PUBLISH_TO_DS` and `0x10`
  `CT_FLAG_EXPORTABLE_KEY` are reserved and ignored by every protocol (`IsReserved`, shown as *przestarzała, ignorowana*),
  `0x20` `CT_FLAG_AUTO_ENROLLMENT`, `0x40` `CT_FLAG_MACHINE_TYPE`, `0x80` `CT_FLAG_IS_CA`, `0x200`
  `CT_FLAG_ADD_TEMPLATE_NAME`, `0x800` `CT_FLAG_IS_CROSS_CA`, `0x1000` `CT_FLAG_DONOTPERSISTINDB` (the old `0x400` was a
  spec error corrected in 2022), `0x10000` `CT_FLAG_IS_DEFAULT`, `0x20000` `CT_FLAG_IS_MODIFIED`.

**EKU OIDs** (`PkiFlags.KnownEku`): Client Authentication `1.3.6.1.5.5.7.3.2`, Server Authentication
`1.3.6.1.5.5.7.3.1`, Smart-Card Logon `1.3.6.1.4.1.311.20.2.2`, PKINIT Client Authentication `1.3.6.1.5.2.3.4`, KDC
Authentication `1.3.6.1.5.2.3.5`, Certificate Request Agent (Enrollment Agent) `1.3.6.1.4.1.311.20.2.1`, Any Purpose
`2.5.29.37.0`. **Predicates** (each takes one OID set; an **empty set means Any Purpose**, as Certipy):
`IsAnyPurpose` (Any Purpose or empty), `IsClientAuth` (Client Auth, Smart-Card Logon, PKINIT or `IsAnyPurpose`),
`IsServerAuth` (Server Auth or `IsAnyPurpose`), `IsEnrollmentAgent` (Certificate Request Agent or `IsAnyPurpose`). The ESC
rules evaluate a predicate on `EffectiveEku` **and** on `Ekus` and fire when either is true: Certipy and Locksmith read
only `pKIExtendedKeyUsage`, the CA issues v2+ templates from the application policies, so evaluating both finds
everything both tools find; a finding where the two sets disagree carries `EkuSourcesDiffer`.

**Periods** (`PkiPeriod.Decode(ReadOnlySpan<byte> value) → PkiPeriod?`, `src/AdMng.Core/Pki/PkiPeriod.cs`):
`pKIExpirationPeriod` and `pKIOverlapPeriod` are an 8-byte little-endian **negative** 100-ns interval; null when the
value is not 8 bytes. `PkiPeriod` carries the ticks and `Duration`; the Presentation renders it in the largest unit that
divides it exactly (years, weeks, days, hours – as `certtmpl.msc`); no new converter in `AdMng.Core/ActiveDirectory`.

### 2.3 Core – the enrollment-rights / template-modify evaluator and U4b PKI containers (C3)

This is the decision-5 evaluator – a **separate pure Core evaluator**, not U4b (U4b drops default trustees such as DA/EA
and evaluates no denies, so it is wrong for "who can enroll"). `src/AdMng.Core/Pki/TemplateRightsEvaluator.cs`:

**ACL decoding.** `TemplateRightsEvaluator.Evaluate(ReadOnlySpan<byte> securityDescriptor, AclObjectTypes types) →
TemplateAcl(string? Owner, IReadOnlyList<TemplateAce> Aces, bool IsEvaluated)` parses with
`SecurityDescriptorSummary.TryParse` (unparseable → `IsEvaluated = false`), skips `IsInheritOnly` ACEs and ACEs whose
`InheritedObjectType` is set and is not the `pKICertificateTemplate` class (`types.IsClass`), and expands each remaining
ACE, in DACL order, into one `TemplateAce(string Sid, TemplateRight Right, bool IsDeny, string? Attribute)` per atomic
right it grants, with `m = DirectoryAccessMask.Normalize(ace.Mask)`:
* **Enroll** = `(m & ControlAccess) != 0 && (ObjectType is null || types.IsRight(ot, "Certificate-Enrollment"))`;
  **AutoEnroll** the same with `Certificate-AutoEnrollment`. The extended-right GUIDs are in `AclObjectTypes`
  (`Certificate-Enrollment` `0e10c968-78fb-11d2-90d4-00c04f79dc55`, `Certificate-AutoEnrollment`
  `a05b8cc2-17bc-4802-a710-e7c15ab866a2` – the GUIDs Locksmith's `SafeObjectTypes` uses) and in `ExtendedRightIds`
  (for P3's `ModifySecurity`).
* **FullControl** = `ObjectType is null && (m & FullControl) == FullControl` – it also yields Enroll, AutoEnroll,
  WriteDacl, WriteOwner and WriteAllProperties; **WriteDacl**, **WriteOwner** from the mask bits.
* **WriteAllProperties** = `(m & WriteProperty) != 0 && ObjectType is null` (GENERIC_WRITE normalizes into it).
* **WriteSensitiveProperty** = WriteProperty on an attribute (or property set) that `types.Covers` one of
  `PkiAttributes.SensitiveTemplateAttributes` – the **one** list used by 2.3, 2.4 and U4b: `msPKI-Certificate-Name-Flag`,
  `msPKI-Enrollment-Flag`, `pKIExtendedKeyUsage`, `msPKI-Certificate-Application-Policy`, `msPKI-RA-Signature`,
  `msPKI-RA-Application-Policies`, `msPKI-RA-Policies`, `msPKI-Certificate-Policy`, `msPKI-Template-Schema-Version`
  (`Attribute` = the name). An object-type GUID neither the schema, the extended rights nor `AclObjectTypes.Fallbacks`
  can name counts as sensitive with `Attribute` = the GUID (conservative: no ESC4 false negative); C3 adds the
  schemaIDGUIDs of the nine attributes and of `altSecurityIdentities` to `Fallbacks`, each checked against the published
  MS-ADA attribute page and pinned by a test.
* **WriteOtherProperty** = WriteProperty on any other named attribute (Locksmith flags it; 2.4 reports it Low).
* **Owner** = `summary.Owner` (the owner's implicit READ_CONTROL | WRITE_DAC; an `OWNER RIGHTS` S-1-3-4 ACE replaces
  them, as in Windows).

**Decision.** `TemplateRightsEvaluator.Decide(IReadOnlySet<string> tokenSids, TemplateAcl acl, TemplateRight right) →
bool`: AccessCheck decides **each right on its own** – the first ACE in DACL order whose SID is in the token and whose
`Right` is that right decides it (deny → false, allow → true); no such ACE → false. Modelled on
`GroupPolicySecurityFilter.Decide` but **without** its raw `ace.Mask & ControlAccess` (it misses a bare GENERIC_ALL bit –
the normalized mask is used).

**Classes.** `PrincipalClassifier(string? domainSid, string? rootDomainSid, PrivilegedAccessOptions options, GroupGraph?
groups, IReadOnlyCollection<string> caComputerSids)` with `Classify(string sid) → PrincipalClass` (`Tier0`, `Broad`,
`Other`) implements decision 4 exactly (RID rules for any `S-1-5-21-*` prefix; catalogue and graph reach through
`PrivilegedGroupCatalog.Match` + `options.LevelOf`). Without a graph (`groups` null – the immediate trustee view before
the expansion has read it) it uses the SID and RID rules only, and trustee names stay empty until the graph is there.
Shared by 2.3, 2.4 and the ESC5/ESC14 trustees.

**Trustee level first, accounts once.** `TemplateRightsEvaluator.TrusteeLevel(IReadOnlyList<CertificateTemplate>
templates, AclObjectTypes types, PrincipalClassifier classifier) → IReadOnlyList<TemplateRights>` is synchronous and
immediate: per template, per right, the trustees that hold it – a trustee holds a right when `Decide({trustee SID},
acl, right)` is true, so a deny naming the trustee is honoured – (`TemplateTrustee(Sid, Name, Class, Right,
AccountCount = -1 until expanded, …)`), with a template whose descriptor is missing or unparseable marked
`IsEvaluated = false`. The ESC rules use this trustee level (as Certipy and Locksmith, per SID); the account-level counts
of the expansion are what the documents show.
`TemplateRightsEvaluator.ExpandAsync(IDirectorySession session, IReadOnlyList<CertificateTemplate> templates,
PkiExpansionContext context, IProgress<PkiExpansionProgress>? progress, CancellationToken ct) → PkiEnrollmentExpansion`
then (1) uses the `GroupGraph` the context carries (the caller reads it once with `GroupGraph.ReadAsync`), (2) streams the
accounts **once** (`PrivilegedAccessScanner.AccountFilter`; `objectSid`, `primaryGroupID`, `memberOf`,
`userAccountControl`, `sAMAccountName`, `name`, `altSecurityIdentities`; ranged `memberOf` completed with
`PrivilegedAccessScanner.CompleteMemberOfAsync`, made `internal static` by the contract commit), building each token with
`GroupGraph.Reach(…, includeImplicit: true)` plus the account SID, the primary group, Everyone / Authenticated Users / This
Organization and S-1-5-9 for DCs; (3) **memoises** the decisions per distinct key = the token ∩ the SIDs any template ACE
names (100 000 accounts collapse to a few hundred keys); (4) keeps per template and right only the counts (enabled /
disabled apart), the highest class and the **first 20** accounts (`PkiAccountRef(Dn, Name, IsDisabled)`) – the path
(`GroupGraph.PathTo`) is computed by the UI only for a name it shows; (5) keeps every account that carries
`altSecurityIdentities` as `PkiAccountFact(Dn, Name, Sid, IsDisabled, IsTier0, Mappings)` (parsed with 2.4's
`AltSecurityIdentity.Parse`) for ESC14 and the readiness report; (6) reports a trustee of another forest domain that the
graph does not hold as unresolved (`TemplateTrustee.IsResolved = false`, notice `TrusteeUnresolved`, resource
`Pki.Notice.TrusteeUnresolved`). The result `PkiEnrollmentExpansion(Templates /* by cn, OrdinalIgnoreCase */,
MappedAccounts, AccountsScanned, Groups, Options, Notices)` feeds the templates document (2.5), the ESC reader (2.4) and
the readiness report; it is cancellable, reports progress every 500 accounts (rule 6) and lives in memory only (rule 1).
`AclObjectTypes.ReadAsync(IDirectoryReader reader, int pageSize, CancellationToken ct)` (stub in the contract, body by
C3 – the `CN=Extended-Rights` read `DirectoryAclScan` already does, moved) builds the `types` for all callers.

**U4b PKI containers** (decision 6; `src/AdMng.Core/PrivilegedAccess/DirectoryAclGrantSource.cs`, the shared Core file C3
edits – at the anchor after the GPO block in `RunAsync`): one paged subtree search of
`CN=Public Key Services,CN=Services,<config>` (`DistinguishedName.Child`, rule 3) over `PkiClasses.All` with
`SdAttributes`, each `Read` into a new `AclTargetKind.PublicKey` with `IsTier0 = true`, `Tier0Subtree = true`, and
`ReportOwner = true` only for containers, templates, enrollment services, the NTAuth object and OID objects with
`msDS-OIDToGroupLink` (`CN=OID` can hold thousands of objects); `ReadDefaultsAsync` then picks up the new classes. For
`PublicKey` targets `IsDefaultTrustee` also accepts the provisioning trustees of decision 6 (`<RootDomainSid>-512`, `-517`
of any domain SID, the CA computer SIDs on their own enrollment service and CDP / AIA objects; Enterprise Admins are
already default by `DefaultTrusteeRootRids` when `RootDomainSid` is known). On templates, WriteProperty on a
`PkiAttributes.SensitiveTemplateAttributes` attribute surfaces as `AclRight.WriteTemplateProperty` with the attribute as
detail (mirroring the `WriteGpoProperty` branch). New in `RightsOf` for every target: WriteProperty covering
`altSecurityIdentities` → `AclRight.WriteAltSecurityIdentities` (a deliberate U4 output change with its own tests; both
`AclRight` values and their resx entries are in the contract commit).

**Typed entry for ESC5 / ESC14.** `DirectoryAclGrantSource.CollectPublicKeyAsync(PrivilegeScanContext context,
IReadOnlyList<string> caComputerDns, CancellationToken ct) → Task<IReadOnlyList<PkiAclGrant>>` (public; stub in the
contract commit) runs one `DirectoryAclScan` in a PKI mode – the PKI subtree, the CA computer objects (`GetAsync` by DN;
only those of the window's domain), the Tier 0 accounts (`Tier0Filter`), the class defaults and the extended rights; no
OUs, GPOs, RBCD or deep scan – with the same evaluation rules and returns typed `PkiAclGrant(TargetDn, PkiAclTargetKind
TargetKind, TrusteeSid, AclRight Right, string? Detail)` (`PkiAclTargetKind` = `Container | NtAuth | Oid |
EnrollmentService | CertificationAuthority | CrlDistributionPoint | Template | CaComputer | Tier0Account`) instead of
formatted `PrivilegeGrant` values. `RunAsync` and `CollectPublicKeyAsync` share the PKI pass (an internal method of
`DirectoryAclScan`), so U4b and ESC5 cannot drift. `IPrivilegeGrantSource` count unchanged (`HostTests`
`Assert.Equal(2, …)` stays). A separate ACL sample (`AddPublicKeyAclSample`) keeps the exact-set
`DirectoryAclGrantSourceTests` unchanged.

### 2.4 Core – the ESC analyzer, finding codes, health rules and the account parsers (C4)

**Parsers** (`src/AdMng.Core/Pki/`, pure, value-free `ToString`):
* `KeyCredentialLink.Parse(IEnumerable<string> values) → IReadOnlyList<KeyCredentialLink>` – `msDS-KeyCredentialLink` is
  DN-Binary and in `AlwaysBinary`, so the real DC delivers UTF-8 bytes of `B:<hexlen>:<hex>:<dn>`; the caller reads it
  with `GetStrings`, the parser splits each value with `WellKnownObjects.TryParseValue` (`AdMng.Core.Directory`) and
  parses the KEYCREDENTIALLINK_BLOB (version `0x0200`, TLV entries: `0x01` KeyID, `0x02` KeyHash, `0x03` KeyMaterial,
  `0x04` KeyUsage, `0x05` KeySource, `0x06` DeviceId, `0x07` CustomKeyInformation, `0x08`
  KeyApproximateLastLogonTimeStamp, `0x09` KeyCreationTime). Exposes `KeyId`, `DeviceId`, `Source`
  (`KeyCredentialSource` AD / Entra ID), `Usage` (`KeyCredentialUsage`), `Created`, `LastLogon`, `IsMalformed` – never
  the key material bytes beyond the id.
* `AltSecurityIdentity.Parse(string value)` with `Kind` (`AltSecurityIdentityKind`) and `Strength` (`MappingStrength`,
  KB5014754): **strong** `X509IssuerSerial` = `X509:<I>…<SR>…` (issuer **and** serial number in **reversed** byte order;
  `<SR>` without `<I>` is not a defined mapping → `Invalid`), `X509Ski` = `X509:<SKI>…`, `X509Sha1PublicKey` =
  `X509:<SHA1-PUKEY>…`; **weak** `X509IssuerSubject` = `X509:<I>…<S>…`, `X509SubjectOnly` = `X509:<S>…`, `X509Rfc822` =
  `X509:<RFC822>…`; `Kerberos:…` is `NotCertificate` (shown separately); anything else `Invalid`.

**Health rules.** `PkiHealth.Evaluate(PkiSnapshot snapshot, PkiHealthThresholds thresholds, DateTimeOffset now,
IReadOnlyList<PkiHttpCheckResult>? http) → IReadOnlyList<PkiHealthRow>` (`src/AdMng.Core/Pki/PkiHealth.cs`, pure, called
by the Presentation at display time – decision 2): per enterprise CA a row for its certificate (CA threshold), each AIA and
CDP URL of that certificate (the locations of its issuer's certificate and CRL, as `pkiview.msc`) and its own base and
delta CRL from `CN=CDP` (matched by `IssuerCaGuid`); rows for KRA and NTAuth certificates (end-entity / CA thresholds).
`PkiHealthStatus` = `Ok | Expiring | Expired | Unavailable | Unknown`: a certificate is `Expired` after `NotAfter`,
`Expiring` within the threshold, `Unknown` before `NotBefore`; a CRL is `Expired` after `NextUpdate`, `Expiring` when less
than `max(1 day, 20 % × (NextUpdate − ThisUpdate))` is left, `Unknown` without `NextUpdate`; a missing or unparseable
object is `Unavailable`. URLs: `ldap:///…` (no host) is resolved against the snapshot by its DN part
(`DistinguishedName.TryParse`) – found → the object's state, not found → `Unavailable`; `ldap://host/…`, `file:` and UNC
→ `Unknown` (*nie sprawdzono*); `http(s)` → `Unknown` until an HTTP result for it is passed. `PkiHealth.HttpUrls(snapshot)
→ IReadOnlyList<PkiUrl>` lists the `http`/`https` URLs (AIA CA Issuers, CDP, delta CDP, OCSP) for the probe (2.5).
`PkiHealthRow(Guid? CaGuid, PkiHealthItem Item, int Index, PkiHealthStatus Status, DateTimeOffset? ValidUntil, string?
Url)`.

**The analyzer** is split like K6 (reader → pure `Analyze`):
`PkiEscReader.ReadAsync(PrivilegeScanContext context, PkiSnapshot snapshot, PkiEnrollmentExpansion expansion,
PkiEscReadOptions options, IProgress<PkiEscProgress>? progress, CancellationToken ct) → PkiEscSnapshot` takes the
precomputed expansion (no second account pass), calls `DirectoryAclGrantSource.CollectPublicKeyAsync` with the snapshot's
CA computer DNs, classifies every grant trustee with `PrincipalClassifier`, resolves each OID group link against
`context.Groups` (`PkiLinkedGroup(Dn, Name, PrivilegeLevel Level)` – a link to an object that is not a group of the graph
gives no ESC13 finding and a `TrusteeUnresolved` notice), and sets `CaGrantsAuthenticatedUsersEnroll` from
`options` (decision 3, default true). `PkiEscSnapshot(Templates, Rights, EnterpriseCas, Oids, LinkedGroups, AclGrants,
TrusteeClasses, MappedAccounts, CaGrantsAuthenticatedUsersEnroll)`. `PkiEscAnalyzer.Analyze(PkiEscSnapshot snapshot) →
PkiEscResult` (pure, `static partial`: `.Esc1.cs` … `.Esc17.cs`) runs the LDAP-only rules.

Each finding is `PkiFinding(string Code, PkiFindingSeverity Severity, PkiFindingArea Area, Guid? ObjectId,
IReadOnlyList<string> Arguments)` with init members `ObjectDn`, `Principals` (`IReadOnlyList<PkiPrincipal>` – the
accounts and groups with their counts, README: `PkiPrincipal(string Sid, string? Name, PrincipalClass Class, int
AccountCount, bool IsResolved, string? Right)`, built from a `TemplateTrustee` or a `PkiAclGrant` trustee), `Notes` (`[Flags] PkiFindingNotes`: `NotPublished`, `AssumesCaEnroll`,
`PendingCaPatch`, `ApprovalRequired`, `EkuSourcesDiffer`, `UnknownAttribute`, `TargetOnly`) and `RelatedObjectIds` (the
CAs, OID objects, target templates). Its `ToString` is value-free (code, severity, area, the object GUID in braces –
never a name, DN, subject or argument). `PkiFindingCodes` (`src/AdMng.Core/Pki/PkiFindingCodes.cs`) copies
`GpoFindingCodes`: `public const string X = nameof(X)`, `static IReadOnlyList<PkiFindingDefinition> All`, `Find(code)`,
and `PkiFindingDefinition(string Code, string Esc, PkiFindingSeverity Severity /* the highest it can get */,
PkiFindingArea Area, PkiRuleGroup Group, PkiApprovalEffect Approval, string? Locksmith, string? Mdi)`. `Locksmith` is the
`Technique` string of the Locksmith rule (commit 8b555bd: `Find-ESC1/2/3C1/3C2/4/5/9/13/15/17.ps1`); `Mdi` stays `null`
until a real MDI assessment id is confirmed (never invented). `PkiFindingSeverity` = `Info | Low | Medium | High |
Critical`.

**Common rules.** *Enrollable* = an effective **Enroll** (2.3; FullControl and all extended rights included – AutoEnroll
alone does not enroll) held by a *Broad* or *Other* trustee; the severity follows the highest such class. A template no
enterprise CA publishes drops one level with `NotPublished` (decision 3; never below Info). Every enrollment-based finding
carries `AssumesCaEnroll`. **Approval** = `RequiresManagerApproval` or `RequiresAuthorizedSignature`; its effect is the
`Approval` column: *suppresses* (no finding – Certipy and Locksmith agree), *lowers* (one level lower + `ApprovalRequired`
– Certipy 3ae3442 suppresses these, Locksmith reports them, so the review reports them at a lower level) or *never*
(the attacker rewrites the template or the account, approval does not protect).

| Code | ESC (Certipy) | Locksmith | Condition | Approval | Severity |
|---|---|---|---|---|---|
| `EnrolleeSuppliesSubjectClientAuth` | ESC1 | `ESC1` | `EnrolleeSuppliesSubject` **and** `IsClientAuth` **and** enrollable | suppresses | broad → Critical, other → Medium |
| `AnyPurposeOrNoEku` | ESC2 | `ESC2` | `IsAnyPurpose` (Any Purpose `2.5.29.37.0` or no EKU – a template is not a SubCA because its EKU set is empty) **and** enrollable | suppresses | broad → Critical, other → Medium |
| `EnrollmentAgentTemplate` | ESC3 | `ESC3` (C1) | `IsEnrollmentAgent` **and** enrollable; the targets (`RelatedObjectIds`) are the `EnrollmentAgentTargetTemplate` templates and the schema-v1 `IsClientAuth` templates enrollable by anyone (Certipy's *ESC3 Target Template*) | suppresses | broad → Critical, other → Medium |
| `EnrollmentAgentTargetTemplate` | ESC3 | `ESC3` (C2) | `SchemaVersion ≥ 2` **and** `RaSignatures ≥ 1` **and** `RaApplicationPolicies` contains Certificate Request Agent **and** `IsClientAuth` **and** enrollable; a schema-v1 target is never a finding of its own (it would flag the default *User* template) | manager approval suppresses (the signature is the condition) | High with a broad `EnrollmentAgentTemplate` finding, Medium with an other one, else Low + `TargetOnly` |
| `TemplateWriteable` | ESC4 | `ESC4` | a broad/other trustee holds FullControl, WriteDacl, WriteOwner, WriteAllProperties, Owner or WriteSensitiveProperty; WriteOtherProperty → Low (Certipy flags only all-properties writes, Locksmith every write but Enroll/AutoEnroll – the graded model finds both); an unknown attribute GUID → the full level + `UnknownAttribute` | never | broad → Critical, other → Medium |
| `PkiObjectWriteable` | ESC5 | `ESC5` | a broad/other trustee holds GenericAll, WriteDacl, WriteOwner, WriteAllProperties, Owner, CreateChild or a write of a named attribute on a PKI container, NTAuth, an OID object, an enrollment service, a CA / AIA / CDP object or the CA's computer object (`PkiAclGrant`, templates excluded – they are ESC4), with the decision-4 safe set and the decision-6 provisioning trustees | never | broad → Critical, other → Medium |
| `NoSecurityExtensionClientAuth` | ESC9 | `ESC9` | `NoSecurityExtension` (`0x80000`) **and** `IsClientAuth` **and** enrollable (the DC-side enforcement is P4/K1; P1 reports the template) | lowers | broad → High, other → Medium |
| `IssuancePolicyLinkedToGroup` | ESC13 | `ESC13` | a value of `msPKI-Certificate-Policy` **equals** the `msPKI-Cert-Template-OID` of an `msPKI-Enterprise-Oid` object (join by the OID string, never by DN) whose `msDS-OIDToGroupLink` resolves to a group of the graph (normally an empty universal group – its membership comes from the certificate; members the graph shows are listed too), **and** `IsClientAuth` **and** enrollable | lowers | linked group Tier 0 → Critical, else High |
| `WeakExplicitMapping` | ESC14 | – | (a) a Tier 0 account carries a weak `altSecurityIdentities` mapping (`PkiAccountFact`) → High; weak mappings on other accounts → one aggregated Low finding with the count (the readiness report lists them); (b) a broad/other trustee holds `WriteAltSecurityIdentities` or `WriteAllProperties` on a Tier 0 account (`PkiAclGrant.TargetKind = Tier0Account`; full control of a Tier 0 object is already U4) → broad Critical, other Medium. Full enforcement (Sept. 2025 update) rejects weak mappings – the DC state is P4/K1 | never | as given |
| `SchemaV1EnrolleeSuppliesSubject` | ESC15 | `ESC15/EKUwu` | `SchemaVersion = 1` **and** `EnrolleeSuppliesSubject` **and** enrollable, no EKU condition (CVE-2024-49019; Locksmith flags every enrollable v1 template, Certipy only with ESS – the difference is documented), note `PendingCaPatch` (*zależne od poprawki urzędu z listopada 2024*, confirmed in P4) | lowers | Medium |
| `EnrolleeSuppliesSubjectServerAuth` | ESC17 | `ESC17` | `EnrolleeSuppliesSubject` **and** `IsServerAuth` **and** enrollable (Certipy `server_authentication` includes Any Purpose; Locksmith only `1.3.6.1.5.5.7.3.1`) | suppresses | broad → High, other → Medium |

Certipy also reports ESC1 and ESC2 (and ESC3, ESC17) together on an enrollee-supplies-subject Any-Purpose template; the
review does the same and groups the rows by template. All eleven codes are LDAP-only; ESC6/7/8/10/11/16 are P4 and ESC12
is out of scope – none is in `PkiFindingCodes`.

### 2.5 Presentation/App – the PKI node, snapshot service, settings and the overview, templates, security and readiness documents (W5, W6)

**Tree node** (W5). A `PkiNodeViewModel : TreeNodeViewModel` base (own base, not the GPO node base – decision 9;
subclassing `GroupPolicyNodeViewModel` would make the GPO toggle remove it and `TryActivateGroupPolicyNode` swallow it)
and the concrete `PublicKeyServicesNodeViewModel`. `NavigationTreeViewModel` gets a `PkiViewModelFactory? pki`
constructor parameter, `ShowPkiNodes(bool)` inserting *Usługi kluczy publicznych* under the domain node **at the index
after the last `GroupPolicyNodeViewModel` child** (0 when the GPO nodes are hidden; `GroupPolicyTreeAndShellTests`
asserts `Children[0]` is the GPO objects node), called in `InitializeAsync` after `ShowGroupPolicyNodes` and in
`OnSettingsChanged`, a relocalize loop in `OnCultureChanged`, and a *Widok → Usługi kluczy publicznych w drzewie* toggle
bound to `AppSettings.Pki.ShowInTree` (default on – every existing tree screenshot changes once; the integration step
regenerates them).

**Services** (contract commit, complete). `PkiServices(DialogServices ui, ICsvCodec csv, IPkiSnapshotService snapshot,
IDomainControllerTlsProbe? tlsProbe, IPkiHealthProbe? healthProbe)` (scoped) carries `Ui`, `Csv`, `Snapshot`,
`TlsProbe`, `HealthProbe`, the shortcuts `Localizer` / `Dispatcher` / `Sessions` / `Settings` / `Errors` / `Time`,
`Lifetime` (`Ui.Connection?.Lifetime`), `event EventHandler<PkiDocumentRequest>? DocumentRequested` +
`RequestDocument(request)`, and the CA-host navigation `CanOpenRemoteActions => Ui.Dialogs is IComputerDialogs`,
`OpenComputerAsync(string computerDn)` (`Ui.Dialogs.ShowPropertiesAsync`) and `OpenRemoteActionsAsync(string computerDn)`
(`IComputerDialogs.ShowRemoteActionsAsync`) – both by the `ComputerDn` of 2.1, so no computer lookup is needed; a CA host
in another domain shows a note, not an error. The probes come from `sp.GetService<…>()` (null → the LDAPS tab and the
HTTP button say *niedostępne*), the way `EventLogServices` takes `IEventLogConnector`; `PkiSnapshotService` gets them the
same way (`new PkiSnapshotService(sp.GetRequiredService<DialogServices>(), sp.GetService<IDomainControllerTlsProbe>(),
sp.GetService<IPkiHealthProbe>())`), so `PkiServices` → `IPkiSnapshotService` has no cycle. **`PkiDocumentBase :
ObservableObject, IShellDocument`** (contract commit, complete – copied from `GroupPolicyDocumentBase` but LDAP-only):
`protected PkiDocumentBase(PkiServices pki)`, `Pki`, `SerialCancellation`, `LoadCoreAsync(IDirectorySession,
CancellationToken)`, `Error`, `LoadedText`, the `SessionChanged` / `CultureChanged` / `Settings.Changed` subscriptions
(virtual `OnSettingsChanged`, all unsubscribed in `Dispose`); **drops** SYSVOL, the PDC note and the `Changed` event.

**Snapshot service** (W5). `PkiSnapshotService : IPkiSnapshotService, IDisposable`
(`src/AdMng.Presentation/Pki/PkiSnapshotService.cs`, scoped, one per window, modelled on `GpoSettingsIndexService`)
caches, keyed on the `IDirectorySession` instance **and** `session.Info.Server`: the `PkiSnapshot` (`GetAsync(refresh,
progress, ct)`), the enrollment expansion (`GetExpansionAsync(refresh, progress, ct)` – reads `GroupGraph` once, then
`TemplateRightsEvaluator.ExpandAsync`; started lazily by the templates, security and readiness documents **after** the
trustee-level view is on screen), the domain controllers (`GetDomainControllersAsync(refresh, ct)` –
`PkiOverviewReader.ReadDomainControllersAsync`, LDAP only, for the KDC indicator), the DC certificate checks
(`GetDcCertificatesAsync(refresh, ct)` – DCs from `DomainControllerCatalog.ReadAsync` joined with those, each probed with
`IDomainControllerTlsProbe` at most 4 in parallel via a `SemaphoreSlim`, the current DC first, 3269 only for GCs; only on
explicit navigation or button – decision 1) and the HTTP results (`CheckHttpAsync(progress, ct)` on the operator's button
→ `HttpResults`). One build per item is shared by all callers; a caller's token ends only that caller's wait (as
`GpoSettingsIndexService`), the build runs on `Task.Run` under the service's own `CancellationTokenSource` linked to
`PkiServices.Lifetime`; `ISessionManager.SessionChanged` and `Invalidate()` (F5) cancel and drop everything; `Changed`
fires after each replacement; `Dispose` unsubscribes `SessionChanged` and cancels. `ReadAt` replaces the old `IsStale`.

**HTTP checks** (W5). `PkiHealthProbe : IPkiHealthProbe` (`src/AdMng.Presentation/Pki/PkiHealthProbe.cs`, singleton,
modelled on `PolicyTemplatePackageLoader` with a `Func<HttpMessageHandler>?` for tests): `CheckAsync(IReadOnlyList<PkiUrl>
urls, IProgress<int>? progress, CancellationToken ct) → IReadOnlyList<PkiHttpCheckResult>`. Only absolute `http`/`https`
URLs are contacted; anything else is `NotChecked` without a connection (`file:`, UNC – would send NTLM to an
attacker-chosen host –, `ldap://host` – another DC, rule 4). Default handler: `SocketsHttpHandler { AllowAutoRedirect =
false, Credentials = null, DefaultProxyCredentials = CredentialCache.DefaultCredentials /* proxy only */,
ConnectTimeout = 5 s, SslOptions.CertificateChainPolicy = { DisableCertificateDownloads = true, RevocationMode = NoCheck }
}`; the probe follows at most 3 redirects itself, applying the same scheme rule to each target; 15 s per URL including
the body; the body read to at most 20 MB (`TooLarge`); 4 URLs in parallel. AIA bodies parse as a DER or PEM certificate
(`CertificateSummary`), CDP bodies as a CRL (`CrlSummary`), anything else `InvalidContent`; **OCSP is reachability only**
(any HTTP response = `Ok` with its status code; no OCSP request is built in P1). `PkiHttpOutcome` = `Ok | NotFound |
HttpError | Timeout | TooLarge | InvalidContent | TlsError | NameNotResolved | ConnectFailed | NotChecked`. Bodies are
dropped after parsing; the results live in the snapshot service until F5.

**Settings** (W5). `AppSettings.Pki` (`PkiSettings`: `ShowInTree` = true, `CertificateExpiryWarningDays` = 30,
`CaCertificateExpiryWarningDays` = 180, `Normalize` clamps to 1–3650; contract commit) gets a `PkiSettingsViewModel`
(`src/AdMng.Presentation/Pki/PkiSettingsViewModel.cs`, the K1 `ComputerSettingsViewModel` pattern) wired into
`SettingsViewModel` (constructor, property, `Error` chain, `Apply`) and a section *Certyfikaty* at the end of the
*Ogólne* tab of `SettingsWindow.xaml`.

**W5 documents.** *Przegląd PKI* (`PkiOverviewDocumentViewModel`, tab *Usługi kluczy publicznych*) with the sub-views of
decision 9 (enterprise CAs with *Otwórz komputer* / *Zdalne akcje* by `ComputerDn`, roots/intermediates, NTAuth with the
enterprise-CA match, CDP lists, KRA, OID with group links), a *Zdrowie* tab (`PkiHealth.Evaluate` over the snapshot with
the current thresholds and `Pki.Time` – re-evaluated on activation, F5 and `OnSettingsChanged`; a *Sprawdź adresy HTTP*
button → `Snapshot.CheckHttpAsync`) and a *Certyfikaty LDAPS* tab (`Snapshot.GetDcCertificatesAsync` on the tab's first
activation or its button; `DcCertificateEvaluation` per row). *Szablony certyfikatów* (`PkiTemplatesDocumentViewModel`)
with the README columns, the decoded flags as Polish/English sentences (technical bit name in the tooltip), the periods,
the issuing CAs, unpublished templates, the trustee-level rights at once and the expanded counts when
`GetExpansionAsync` completes; account names open the property sheet (`Ui.Dialogs.ShowPropertiesAsync`), a group trustee
opens the group (its *Członkowie* tab – all members, not only the first 20), a broad trustee shows the count only; *kto
może zmienić* from the same expansion; a **compare pane** inside the document (two templates side by side, differing
rows marked; no new window); export CSV/HTML to an operator-chosen file through `Pki.Ui.Files` + `Pki.Csv`
(`protectFormulas: true`).

**W6 documents.** *Przegląd bezpieczeństwa AD CS* (`PkiSecurityReviewDocumentViewModel` + `.Filters.cs` + `.Actions.cs`),
modelled on `GroupPolicyDiagnosticsDocumentViewModel`: `LoadCoreAsync` gets the snapshot and the expansion from the
service, builds the `PrivilegeScanContext` from the session and `expansion.Groups` / `expansion.Options`, runs
`Task.Run(PkiEscReader.ReadAsync + PkiEscAnalyzer.Analyze)` with progress, resolves names, then `Show`; severity chips,
filters (`SelectedSeverity` / `SelectedEsc` / `SelectedArea` / `SearchText`, `ISearchableDocument`), rows
(`PkiFindingRowViewModel`) grouped by object with the ESC number, the Certipy / Locksmith / MDI ids, the condition, the
attack path, the affected object, the principals with counts and the notes. **No `Napraw` in P1** – the row actions are
*Otwórz szablon/urząd* through `Pki.RequestDocument` (`PkiDocumentRequest` with `TemplateCn` / `CaDn`), *Kopiuj*,
*Eksportuj CSV / HTML* (self-contained HTML page, CSP `default-src 'none'`, every value through `E()`, a privacy footer).
*Gotowość na silne mapowanie i NTAuth* (`PkiReadinessDocumentViewModel`) is a **separate document** in the PKI area (not
the stage-S readiness check – README puts stage-S PKI rows in P5) with status rows (`Ok` / *ostrzeżenie* / *problem* /
*nie sprawdzono*): accounts with weak `altSecurityIdentities` (`expansion.MappedAccounts`, Tier 0 first), authentication
templates without the SID extension (published `IsClientAuth` templates with `NoSecurityExtension`, and those with
`EnrolleeSuppliesSubject`, whose certificates carry no SID and need a strong explicit mapping), logon-capable enterprise
CAs not in NTAuth (publishing an `IsClientAuth` template, `InNtAuth = false`), DC LDAPS certificates without a proper
SAN/EKU (`GetDcCertificatesAsync` on the document's own button, or the cached results), KDC certificates
(`KdcCertificateEvaluation` over the certificates `GetDomainControllersAsync` returns), and a KDC events 39/40/41/45/21
row that says *P5*.
Finding texts are the dynamic resx keys of `PkiFindingText` (2.8).

**Shell wiring.** The contract commit pre-wires the call sites to stubs in `src/AdMng.Presentation/Shell/ShellViewModel.Pki.cs`
(a new partial, so `XamlSourceTests.EveryCommandIdUsedInXaml_IsAShellCommand`, which scans `Shell/ShellViewModel*.cs`,
sees the `pki.*` descriptors): `AttachPki()` / `DetachPki()` (constructor / `Dispose`), `if (TryActivatePkiNode(node))
return;` in `OnNodeActivated`, `.. PkiCommandDescriptors(),` in the palette, `.. PkiStateCommands,` in `StateCommands`, an
`ActivePki()` arm in `.Documents.cs`. W5 fills the partial: `OpenPkiDocument(request)`, the `PkiDocuments` filter, all
four descriptors `pki.overview` / `pki.templates` / `pki.security` / `pki.readiness` (kind-generic – they need no W6
type), `AttachPki` subscribing `PkiServices.DocumentRequested`. Requests are `PkiDocumentRequest(PkiDocumentKind Kind)`
(`Overview | Templates | SecurityReview | Readiness`) with `TemplateCn`, `CaDn`, `FindingCode` init props and a value-free
`ToString`. After the contract commit only W5 touches the shell files.

### 2.6 Presentation/App – the read-only property tabs, the attribute-editor mask and the TLS-probe implementation (W7)

**Property-sheet tabs** (`src/AdMng.Presentation/Pki/` property pages; each `public sealed partial class … :
PropertyPageViewModel`, read-only – `GetOperations() => []`, both `Compute*` false, `Revert()` empty, rule 5):
*Opublikowane certyfikaty* (`PublishedCertificatesPageViewModel`, user / computer / contact, columns *Wystawiony dla*,
*Wystawiony przez*, przeznaczenie, ważność, szablon (`CertificateSummary.TemplateName` / `TemplateOid` resolved against
the snapshot's templates when loaded), odcisk – from `userCertificate` and `userSMIMECertificate`), *Mapowania nazw*
(`NameMappingsPageViewModel`, user / computer – `altSecurityIdentities` with the KB5014754 strength from
`AltSecurityIdentity`) and *Poświadczenia kluczy* (`KeyCredentialsPageViewModel`, user / computer –
`msDS-KeyCredentialLink` via `KeyCredentialLink`, key id / device id / source / created / last-logon / usage). They are
added by a private `PageIf(string attribute, Func<PropertyPageViewModel> create)` helper in `PropertyPageFactory` (added
only when `context.IsKnown(attribute)`; the lists filter nulls out – `Known<T>` only accepts fields): `User(obj)` and
`Contact(obj)` after `OrganizationPage()` and before `MemberOfPageViewModel` (ADUC order; in the order *Opublikowane
certyfikaty*, *Mapowania nazw*, *Poświadczenia kluczy*; contact: *Opublikowane certyfikaty* only), `Computer()` after
`ComputerStatusPageViewModel` and before `MemberOfPageViewModel`. Page titles are
`L.Certificates.PublishedCertificatesPage` / `NameMappingsPage` / `KeyCredentialsPage` (as K1's
`L.Computers.StatusPage`; `L.Pages` lives in `L.cs`, which nobody changes). The tab-order tests in
`tests/AdMng.Presentation.Tests/Dialogs/PropertySheetTests.cs` change for user (`:38`) and computer (`:361`); a contact
tab-order test is added; group (`:317`) and OU (`:381`) stay. The certificates and key credentials are read **lazily**
(the Attribute-Editor `ActivateAsync` / `LoadDetailsAsync` pattern with the page's own `SerialCancellation`), parsed on
`Task.Run`; `altSecurityIdentities` is small and goes in `ReadAttributes`. No temp `.cer` files – *Kopiuj do pliku…* is an
operator-chosen export only (rule 1).

**Attribute-editor mask** (decision 7; `src/AdMng.Presentation/PropertySheets/AttributeEditorPage.cs`).
`SecretAttributes.IsNeverShown(attribute, schema)` (contract) = `IsSecret` **and** (the schema gives the attribute a binary
syntax, or it is one of `userPKCS12`, `msPKIAccountCredentials`, `msPKIDPAPIMasterKeys`, `msFVE-KeyPackage`,
`msDS-ManagedPassword`, `supplementalCredentials`, `msLAPS-EncryptedPassword`, `msLAPS-EncryptedPasswordHistory`,
`msLAPS-EncryptedDSRMPassword`, `msLAPS-EncryptedDSRMPasswordHistory`). For a row where it is true – `ValueText` is `SecretAttributes.Mask`, the row is read-only
(`ReadOnlyReason` = *wartość poufna*; `Edit` cannot execute, `CloseEditor` never stages anything for it, the binary value
editor never opens) and `CopyValue` cannot execute. Rows of other secret attributes (LAPS, BitLocker recovery password,
confidential text) are unchanged: shown, copied through `ClipboardCopy.Secret`. `Fields.cs` is untouched (standard pages
never show secrets).

**TLS probe implementation** (`src/AdMng.Directory/Pki/SslStreamTlsProbe.cs`, implements 2.1; Directory is cross-platform
and already TCP/LDAP-flavoured, and the Watchdog service references it for P5 LDAPS-expiry reminders): `HostNames.IsValid`
(else `InvalidHost`; ports other than 636 / 3269 refused), `TcpClient` + `SslStream` with the `LdapConnectionFactory.ProbeAsync`
timeout pattern (a linked CTS; the probe's own timeout → `Timeout`, the caller's cancellation propagates).
`AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, ClientCertificates = [] /* empty */,
LocalCertificateSelectionCallback = (…) => null! /* AD LDAPS sends a CertificateRequest; no certificate is offered */,
CertificateRevocationCheckMode = NoCheck, CertificateChainPolicy = new X509ChainPolicy { DisableCertificateDownloads =
true, RevocationMode = X509RevocationMode.NoCheck } /* the platform chain build cannot fetch AIA */,
RemoteCertificateValidationCallback = capture })`. The callback copies the leaf from `certificate` and the presented
intermediates from `chain.ChainPolicy.ExtraStore` (not `ChainElements`, which is the locally built chain), records
`sslPolicyErrors` and **returns `true`** (returning false would make every presented certificate `HandshakeFailed`; the
stream is closed right after, no application data is ever written). An `AuthenticationException`, an `IOException` or a
`SocketException` (connection reset – which one depends on the OS) during the handshake → `HandshakeFailed`; refused
connect → `PortClosed`; host not found → `NameNotResolved`. No `OperatingSystem.IsWindows()` guard. Registered App-side by
`AddAdMngPkiApp()` (`src/AdMng.App/Pki/PkiAppRegistration.cs`, called from `AppHost` after `AddAdMngPresentation()`) as
`services.AddSingleton<IDomainControllerTlsProbe, SslStreamTlsProbe>()` – a plain `AddSingleton`, since Presentation
registers no fallback (it consumes the probe through `GetService`).

### 2.7 Data handling (rules 1–6)

* **Rule 1.** The `PkiSnapshot` (CAs, templates, certificates, CRLs, OID links), the enrollment expansion (counts, the
  first 20 names, the mapped accounts), the ESC findings, the probed DC certificates and the HTTP results live in the
  scoped `PkiSnapshotService` and the open documents only; dropped on `SessionChanged`, disconnect and window close
  (scope disposal). Nothing in settings (no PKI data; only the two expiry thresholds and the node toggle), temp files or
  logs; diagnostic logs carry counts and durations only – never a subject, thumbprint, DN, URL or serial. Every record of
  `AdMng.Core.Pki` and `AdMng.Presentation.Pki` declares its own `ToString` printing kinds, severities, GUIDs and counts,
  never a value (`PkiContractTests`, 2.10); exception messages and log templates in PKI code carry no values. Exports
  (`.cer`, `.crl`, CSV, HTML) go only to a file the operator picks.
* **Rule 2.** No password or private key is read for display: `userPKCS12`, `msPKIAccountCredentials`,
  `msPKIDPAPIMasterKeys` are in `SecretAttributes`, `IsNeverShown` masks them in the attribute editor, and they are never
  copied or exported; KRA key recovery is out of scope. `KeyCredentialLink` exposes only the id/metadata, never the key
  material.
* **Rule 3.** Every PKI DN is built with `DistinguishedName.Child` (`Child("CN","Services").Child("CN","Public Key
  Services")`); CA names in `CN=CDP` / `CN=AIA` are certutil-sanitized, so objects are matched by attribute (certificate
  hash, subject + key identifier, the OID string), not RDN text. Every filter is `LdapFilter.Equal/Or/And`; host names
  from `dNSHostName` are never concatenated into a filter.
* **Rule 4.** One DC: the snapshot reader, the CA computer lookup, the rights evaluator, U4b and the property tabs read on
  the window session's DC; the configuration partition is forest-replicated, so no other DC is opened. The only traffic
  that leaves the session is the 636/3269 TLS probe and, on the operator's button, the AIA/CDP/OCSP HTTP check (decision
  1); `ldap://host` URLs are never followed.
* **Rule 5.** P1 writes nothing: the documents and property tabs are read-only, `GetOperations()` returns `[]`, the ESC
  review offers navigation only (no `Napraw`), the masked editor rows stage nothing. P3 adds the change-plan repairs.
* **Rule 6.** The snapshot build, the account pass, the ESC read and the TLS / HTTP checks are async, cancellable and
  report progress; at most 4 TLS probes and 4 HTTP checks at a time; long template and finding lists virtualize; the
  ESC analysis and the property-tab parsing run on `Task.Run`.

### 2.8 Localization

Both `Strings.resx` and `Strings.pl.resx`, one block per owner between comment markers (2.12); constants in new nested
classes of the partial `L` (`EveryKeyOfL_ExistsInBothLanguages` only finds constants of nested types of `L`); dynamic
families get an `AllKeys()` helper tested like `GpoFindingText.AllKeys()`. No format string has more than 8 placeholders
(`FormatPlaceholders_MatchBetweenLanguages` formats every value with 8 arguments; `PkiFindingText.ArgumentCounts` ≤ 8,
asserted). The prefix may differ from the class name (as `L.EventLogs` → `EvtLog.`):

| File | Class | Prefix | Contents | Owner |
|---|---|---|---|---|
| `L.Pki.cs` | `L.Pki` | `Pki.` | node, overview, health items (`Pki.HealthItem.<PkiHealthItem>`), read notes (`Pki.Notice.<PkiReadNoticeCode>`), HTTP outcomes (`Pki.Http.<PkiHttpOutcome>`), DC LDAPS tab, settings section | W5 |
| `L.PkiTemplates.cs` | `L.PkiTemplates` | `PkiTpl.` | template list, columns, flag sentences, period units, enrollment rights, compare pane | W5 |
| `L.PkiSecurity.cs` | `L.PkiSecurity` | `PkiEsc.` | the ESC review document (filters, row labels; finding texts live in `PkiFindingText`) | W6 |
| `L.PkiReadiness.cs` | `L.PkiReadiness` | `PkiReady.` | the strong-mapping / NTAuth report | W6 |
| `L.Certificates.cs` | `L.Certificates` | `Cert.` | the three property tabs and their page titles, key sources / usages (`Cert.KeySource.<KeyCredentialSource>`, `Cert.KeyUsage.<KeyCredentialUsage>`), mapping kinds (`Cert.MappingKind.<AltSecurityIdentityKind>`), the masked-row reason | W7 |

**Dynamic key families** (each with `AllKeys()` and a completeness test, modelled on `GpoFindingTextTests`):
* `PkiTpl.Flag.<PkiFlagAttribute>.<Name>` – one entry per **bit** of 2.2 (reserved bits included), and
  `PkiTpl.Field.<Name>` – one per multi-bit field (keyed by the field, never per bit); the helper enumerates `PkiFlags`
  (owner W5). Test: every entry has en + pl.
* Enum families used by more than one wave live in the contract class **`PkiTextKeys`**
  (`src/AdMng.Presentation/Pki/PkiTextKeys.cs`) and the contract resx block: `Pki.Class.<PrincipalClass>`,
  `PkiTpl.Right.<TemplateRight>`, `Cert.Mapping.<MappingStrength>`, `Pki.Tls.<TlsProbeOutcome>`,
  `Pki.Kdc.<KdcReadinessState>`, `Pki.Health.<PkiHealthStatus>`, `PkiEsc.Note.<PkiFindingNotes>` (one per flag),
  `PkiEsc.Severity.<PkiFindingSeverity>`, `PkiEsc.Chip.<PkiFindingSeverity>`, `PkiEsc.Area.<PkiFindingArea>`. The
  single-wave families (`Pki.HealthItem`, `Pki.Notice`, `Pki.Http`, `Cert.KeySource`, `Cert.KeyUsage`, `Cert.MappingKind`)
  are owned by the class of the table above. Tests enumerate the enums.
* **`PkiFindingText`** (`src/AdMng.Presentation/Pki/PkiFindingText.cs`, written by the lead in the contract commit so
  `LocalizationTests` stay green from the start – modelled on `GpoFindingText`): `ArgumentCounts`, `TitleKey` /
  `MessageKey` / `ExplanationKey` / `RecommendationKey` = `PkiFinding.<Code>.Title|Message|Explanation|Recommendation`,
  `Severities`, `AllKeys()`. The block **P1 contract – AD CS finding texts and shared names (2.4, 2.8)** carries all
  eleven codes' four texts and the `PkiTextKeys` families in en + pl. C4 may change `ArgumentCounts` together with the
  texts of that block in wave 1; W6 may only reword. `PkiFindingTextTests` (contract) asserts all four texts exist in both
  languages, the argument counts match and are ≤ 8, and a hard-coded `Assert.Equal(11, PkiFindingCodes.All.Count)`.

Terminology (EN / PL, from the Polish `certtmpl.msc` / `pkiview.msc` / `certsrv.msc` UI), used across the resx:

| English | Polish |
|---|---|
| Public Key Services | Usługi kluczy publicznych |
| Certificate Templates | Szablony certyfikatów |
| Enrollment Services | Usługi rejestracji |
| Certification Authorities | Urzędy certyfikacji |
| Enterprise PKI (pkiview) | PKI przedsiębiorstwa |
| NTAuth store | Magazyn NTAuth |
| Enroll | Rejestruj |
| Autoenroll | Autorejestracja |
| Supply in the request | Dostarcz w żądaniu |
| Build from this Active Directory information | Kompiluj na podstawie tych informacji usługi Active Directory |
| CA manager approval | Zatwierdzenie menedżera certyfikatów |
| Issuance Requirements | Wymagania wystawiania |
| Application Policies | Zasady aplikacji |
| Superseded Templates | Zastępowane szablony |
| Key Recovery Agent | Agent odzyskiwania kluczy |
| Name Mappings | Mapowania nazw |
| Published Certificates | Opublikowane certyfikaty |
| Key Credentials | Poświadczenia kluczy |
| health: OK / Expiring / Expired / Unavailable / Unknown | Stan: OK / Wygasa / Wygasła / Niedostępna / Nieznany |

Polish UI wording as in the consoles' Polish editions; test data with diacritics (decision 11).

### 2.9 Not in P1 (later options)

P2 certificate issuance (requests and CSR, enrollment from templates over the CA, renewal, export on request – README
*Wystawianie certyfikatów*); P3 repairs (`Napraw…`, template-property edits, `ModifySecurity` ACL writes, template copy,
NTAuth / `msDS-OIDToGroupLink` edits, `userCertificate` / `altSecurityIdentities` / `msDS-KeyCredentialLink` edits, bulk
actions); P4 CA-host reads and actions (ESC6/7/8/10/11/16, the CA's own enrollment security replacing the decision-3
assumption, the database views, approval/revocation/CRL publication, the KDC's own certificate store); ESC12 (YubiHSM)
is out of program scope. The DC-side enforcement of ESC9 / ESC10 / ESC14 (`StrongCertificateBindingEnforcement`,
Schannel `CertificateMappingMethods`) and the September-2025 patch read are P4/K1. Moving PKI ESC evaluation into the
Watchdog (P5 / track W) needs a user decision and an ADR-0008/0009 amendment (Q24, 2026-10-20). P5: the KDC-event
analysis of the readiness report (needs a preset query in the event-log reader, track E), the Watchdog history on
*Poświadczenia kluczy* (README: *historia z Watchdoga, gdy jest podłączony*), the stage-S PKI rows. OCSP requests
(beyond reachability) and PKCS#7 AIA bundles are later options. An MDI assessment id is filled in only when a real one
is known.

### 2.10 Test plan

**Core (C1, `tests/AdMng.Core.Tests/Pki/`):** `PkiOverviewReader` – the exact search options (classes, SD flag, extended
DN), base `NoSuchObject` / `AccessDenied` → a notice not a throw, a missing well-known container → `ContainerMissing`, a
template without descriptor → `SecurityDescriptorUnreadable`, binary multi-values read as `byte[]`, the NTAuth and CDP
matches, the CA computer resolution (match, unresolved, other domain); runs in wave 1 because the contract implements the
identity part of `CertificateTemplate.Of`; `ReadDomainControllersAsync`; `CertificateSummary` / `CrlSummary` on
`PkiSamples` DER and a `CertificateRevocationListBuilder` CRL (extensions, AIA/CDP/OCSP URLs, SKI/AKI, SID extension,
delta indicator, missing `nextUpdate`); `DcCertificateEvaluation` and `KdcCertificateEvaluation` with hand-built chains
at the fixed `now`.
**Core (C2, `tests/AdMng.Core.Tests/Pki/`):** `PkiFlags.Decode` for each table (every bit, reserved bits, unknown bits
as `0x…`, the two private-key fields as numbers; the vector `0x80000 → CT_FLAG_NO_SECURITY_EXTENSION`), the EKU
predicates (empty set = Any Purpose; `EffectiveEku` vs `Ekus`), `PkiPeriod.Decode` (one year, six weeks, a wrong length →
null), `CertificateTemplate.Of` on a seeded template (absent `msPKI-RA-Signature` = 0, absent schema version = 1, the
schema-4 backquote `msPKI-RA-Application-Policies`). **Watchdog** (`tests/AdMng.Watchdog.Core.Tests/Detection/
FlagNamesCertificateGoldenTests.cs`, C2, committed **before** the delegation commit): pins today's `FlagNames.Decode`
output for each of the 32 single bits and two mixed values of `msPKI-Certificate-Name-Flag` and `msPKI-Enrollment-Flag`,
and that `TableOf` knows no other certificate attribute; both Watchdog test projects then pass unchanged.
**Core (C3, `tests/AdMng.Core.Tests/Pki/`):** `TemplateRightsEvaluator` – the per-right decision table (deny first wins,
allow then deny, ACE order per right, GENERIC_ALL normalized, inherit-only and foreign `InheritedObjectType` skipped,
Enroll by the extended-right GUID and by GENERIC_ALL, an unknown attribute GUID counted sensitive, OWNER RIGHTS),
`PrincipalClassifier` (every decision-4 SID, `CHILD\Domain Users` broad, root `-512` Tier 0, the CA computer), the
trustee level then the expansion (counts, first 20, disabled apart, memoisation, mapped accounts, cancellation,
progress, other-domain trustee unresolved, 100 000 generated accounts within a time budget); `AclObjectTypes.ReadAsync`;
the U4b PKI pass on `AddPublicKeyAclSample` (a default Windows PKI → **no** findings, also from a child-domain window;
owners only where 2.3 says; `WriteTemplateProperty`; `WriteAltSecurityIdentities` on an account) and
`CollectPublicKeyAsync` (typed grants on containers, NTAuth, OID, enrollment service, CA computer, Tier 0 account) in
`tests/AdMng.Core.Tests/PrivilegedAccess/DirectoryAclPublicKeyTests.cs`; the existing `DirectoryAclGrantSourceTests`
unchanged.
**Core (C4, `tests/AdMng.Core.Tests/Pki/`):** `KeyCredentialLink.Parse` on a hand-built KEYCREDENTIALLINK_BLOB (and a
malformed one), `AltSecurityIdentity` on each strong/weak form (incl. the reversed `<SR>` with `<I>`, a bare `<SR>` →
`Invalid`, `Kerberos:`), `PkiHealth.Evaluate` at each threshold edge (CA 180 d, end entity 30 d, CRL `max(1 d, 20 %)`, no
`nextUpdate`, `ldap:///` found / not found, `ldap://host`, http with and without results) with hand-built
`CertificateSummary` / `CrlSummary` records, `PkiEscAnalyzer.Analyze` – one test per code on **hand-built**
`PkiEscSnapshot` records (C4 does not wait for C1–C3's readers): the vulnerable template flagged at the decision-4
severity **and** its safe look-alikes **not** flagged (Tier 0 enrollee only; for *suppresses* rules manager approval and
an authorized signature; schema-v1 client-auth template not an ESC3 target finding), the *lowers* rules one level lower
with `ApprovalRequired`, `NotPublished` one level lower, `AssumesCaEnroll` on every enrollment finding,
`EkuSourcesDiffer`, ESC1+ESC2 together on an Any-Purpose ESS template, the default-forest snapshot → no findings.
**Contract (`tests/AdMng.Core.Tests/Pki/PkiContractTests.cs` and `tests/AdMng.Presentation.Tests/Pki/PkiContractTests.cs`,
lead):** reflection over every record and record struct of `AdMng.Core.Pki` and `AdMng.Presentation.Pki` – `ToString`
is declared by the type itself and is not the compiler-generated one (no `PrintMembers`) – then a populated instance of
each (Polish sample values from `PkiSamples`) asserted not to contain any of them (like `GpoK7ContractTests`); new records
an implementer adds are covered automatically.
**Sample (shared, `tests/AdMng.Testing/FakeDirectoryBuilder.PublicKey.cs` + `FakeSchema.PublicKey.cs`):**
`AddPublicKeySample(SampleCompany) → PublicKeySample` seeds (calling `AddWatchdogInfrastructure()` first when the config
NC is empty) `CN=AIA`, `CN=CDP/<host>/<CA>` (`cRLDistributionPoint`), `CN=KRA`, `CN=OID` (`msPKI-Enterprise-Oid` +
`msDS-OIDToGroupLink`), the CA certificates and the NTAuth `cACertificate`, the two enterprise CAs *Kontoso Główny CA* /
*Kontoso Wystawiający CA* (with CA computer objects in the sample domain), Polish-named templates with diacritics,
**one deliberately vulnerable template per code and a safe look-alike** (e.g. *Użytkownik-VPN-Podatny* with
`CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT` + client-auth EKU + Domain Users enroll for ESC1, beside *Użytkownik-VPN-Bezpieczny*
with the same EKU but manager approval), default-PKI ACLs (root Domain Admins, Enterprise Admins, Cert Publishers, the
CA computer) that must yield no finding, a few accounts with strong and weak `altSecurityIdentities`, DC `userCertificate`
values, and a sample record exposing the ids by role. `FakeSchema.PublicKey.cs` (`ExtendForPublicKey`, the
`ExtendForGroupPolicy` pattern) extends `MayContain` of the existing `pKICertificateTemplate`, `pKIEnrollmentService`
and `certificationAuthority` classes of `FakeSchema.Watchdog.cs` and of `user` / `computer` / `contact`
(`userSMIMECertificate`, `userPKCS12`, `altSecurityIdentities`, `msDS-KeyCredentialLink`), adds the missing attributes
(`msPKI-Private-Key-Flag`, `msPKI-Template-Schema-Version`, `msPKI-Template-Minor-Revision`, `revision`,
`pKIExpirationPeriod`, `pKIOverlapPeriod`, `msPKI-Minimal-Key-Size`, `msPKI-Cert-Template-OID`, `msPKI-Certificate-Policy`,
`msPKI-RA-Policies`, `msPKI-RA-Application-Policies`, `msPKI-Supersede-Templates`, `pKIDefaultCSPs`, `pKIKeyUsage`,
`msPKI-Enrollment-Servers`, `certificateRevocationList`, `deltaRevocationList`, `authorityRevocationList`,
`crossCertificatePair`, `msDS-OIDToGroupLink`, `userSMIMECertificate`, `userPKCS12`) – binary ones with `OctetString`
syntax, every one with its schemaIDGUID – and the classes `cRLDistributionPoint`, `msPKI-Enterprise-Oid`,
`msPKI-PrivateKeyRecoveryAgent` with `PossibleSuperiors`, wired into `FakeSchema.CreateCore`. `PkiSamples`
(`tests/AdMng.Testing/Pki/PkiSamples.cs`) builds CA + issued certificates (`CertificateRequest.Create(issuer, …)`, AIA /
CDP / OCSP / SID / template extensions) and CRLs (`CertificateRevocationListBuilder`) with **fixed dates around
2026-10-13** and Polish diacritics in the subjects, and `ServerCertificate(…)` returning a TLS server certificate
PFX-round-tripped with `X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null,
X509KeyStorageFlags.Exportable)` (as `tests/AdMng.Watchdog.Testing/Certificates/TestCertificates.cs`; SChannel rejects an
ephemeral key). Enroll / AutoEnroll GUIDs added to `FakeDirectoryBuilder.Acl.cs` `AclGuids` and
`Certificate-Enrollment` / `Certificate-AutoEnrollment` (validAccesses `0x100`) to its `ControlAccessRights`.
**Presentation (pl-PL, `PkiHarness` copied from `GroupPolicyHarness`, `tests/AdMng.Presentation.Tests/Pki/`):** the
snapshot service (one build per item, reuse, a caller's cancellation ends only its wait, dropped on `SessionChanged`, a
reconnect to the same server not reused, `Dispose` unsubscribes); the HTTP probe with a fake `HttpMessageHandler`
(schemes refused without a request, redirects re-checked and capped at 3, 20 MB cap, timeout including the body, no
credentials, AIA/CDP parsing, OCSP reachability); the settings section; the overview / health (re-evaluated after a
threshold change and a clock move) / LDAPS tab (fake probe) / templates documents on `AddPublicKeySample` (compare pane,
navigation to accounts and groups, export); the security review (the vulnerable template appears with its ESC number
and severity, the look-alike does not, filters, chips, *Otwórz szablon* through `DocumentRequested`, export); the
readiness rows; the property tabs via `harness.Factory.CreatePropertySheet(dn)` + `ActivateAsync`
(`PublishedCertificates`, `NameMappings` with KB5014754 strength, `KeyCredentials`); the attribute-editor mask
(`userPKCS12` row shows the mask, cannot be edited or copied, no `SetValuesOperation` carrying the mask can be produced;
a LAPS row unchanged); the shell palette `pki.*`; the tree index after the GPO nodes; `ConnectionLifetimes`. End-to-end
tests that need the merged Core readers are `[Fact(Skip = "P1 wave 1 merge")]` until the lead merges wave 1.
**Directory (`tests/AdMng.Directory.IntegrationTests/Pki/SslStreamTlsProbeTests.cs`, W7; runs on Linux and Windows CI):**
`SslStreamTlsProbe` on a `TcpListener(IPAddress.Loopback, 0)` + `SslStream.AuthenticateAsServerAsync` with
`PkiSamples.ServerCertificate` (closed port, accept-then-reset, hang → timeout, expired leaf, wrong SAN, a presented
intermediate captured from `ExtraStore`, an IP host refused – host `"localhost"`); lab: a probe of `localhost:636` when
`ADMNG_TEST_LDAP_*` is set (`127.0.0.1` would be refused by `HostNames.IsValid`; the Samba certificate is self-signed, so
`Ok` with `ChainsToAdRoot = false`).
**Watchdog (contract commit, `tests/AdMng.Watchdog.Core.Tests/Rules/SecretUserPkcs12Tests.cs`):** a configuration
watching `userPKCS12` fails validation with `ConfigValidationCodes.InvalidAttribute`, and `WatchedAttributes` drops it.
**App (Windows CI):** the documents in `MainWindow` templates, `SmokeTest.RenderPkiAsync` (W5) and
`RenderPkiSecurityAsync` (W6), UI tests (binding errors, the node, the settings section, the tabs via
`VisitAllPagesAsync` and `tests/AdMng.App.UiTests/Pki/CertificateTabsUiTests.cs` rendering the three `PropertiesWindow`
templates), `HostTests` (`IDomainControllerTlsProbe` → `SslStreamTlsProbe` in `SharedApp`; `Assert.Same` of
`IPkiSnapshotService` and `PkiSnapshotService` in one window scope, like the K7 index check). UI tests that need PKI data
call `context.Directory.AddPublicKeySample(context.Company)` themselves – `UiTestContext` does **not** seed it for every
test (it would change the U4 and GPO tests and screenshots). Screenshots **36** `36-pki-overview.png`, **37**
`37-pki-templates.png` in `tests/AdMng.App.UiTests/ScreenshotTestsPki.cs` (W5) and **38** `38-pki-security.png`,
**39** `39-pki-readiness.png` in `tests/AdMng.App.UiTests/ScreenshotTestsPkiSecurity.cs` (W6) – both in namespace
`AdMng.App.UiTests` with class names starting `ScreenshotTests` (CI filters
`FullyQualifiedName~AdMng.App.UiTests.ScreenshotTests`). Lab: Samba has only empty PKI containers, so the integration
project seeds nothing and only tests the empty-PKI path and the LDAPS probe; AD CS tests are skipped without
`ADMNG_TEST_CA_*`.

### 2.11 Contracts (in the code since the P1 contract commit)

The public types that cross agent boundaries are committed by the lead **before wave 1** as compilable stubs with XML doc
comments (bodies `throw new NotImplementedException("P1 Cn: …")`), except the trivial pass-throughs and the items marked
*done* or *complete*; **the code is authoritative**, this table is the map, and each member has exactly the signature
given in 2.1–2.6. An implementer may add members and records, never rename or remove these without writing the change
into 2.14. The value-free `ToString` of every record is in the stubs and pinned by `PkiContractTests`.

| File | Types and members | Implemented by |
|---|---|---|
| `src/AdMng.Core/Pki/PkiAttributes.cs` | `static class PkiAttributes` – the attribute-name constants; `OverviewAttributes`, `TemplateAttributes`, `SensitiveTemplateAttributes` (the nine of 2.3), `AccountAttributes`, `DomainControllerAttributes` (Core must not depend on `Watchdog.Core`) | done |
| `src/AdMng.Core/Pki/PkiClasses.cs` | `static class PkiClasses` – `CertificateTemplate`, `EnrollmentService`, `CertificationAuthority`, `CrlDistributionPoint`, `EnterpriseOid`, `KeyRecoveryAgent`, `Container`; `All` (the seven) | done |
| `src/AdMng.Core/Pki/PkiFlags.cs` | `enum PkiFlagAttribute`, `readonly record struct PkiFlagEntry(Mask, Name, IsReserved)`, `PkiFlagField(Mask, Shift, Name)`, `PkiDecodedFlags(Set, UnknownBits, Fields)`; `static class PkiFlags` – `Table(attribute)`, `PrivateKeyFields`, `KnownEku`, `Decode(PkiFlagAttribute, long)`, `Select(PkiFlagAttribute, IReadOnlyList<long>)`, `IsAnyPurpose` / `IsClientAuth` / `IsServerAuth` / `IsEnrollmentAgent(IReadOnlyCollection<string>)` | C2 |
| `src/AdMng.Core/Pki/PkiPeriod.cs` | `readonly record struct PkiPeriod(long Ticks)` + `Duration`; `static PkiPeriod? Decode(ReadOnlySpan<byte>)` | C2 |
| `src/AdMng.Core/Pki/CertificateTemplate.cs` | `sealed record CertificateTemplate` (members of 2.2); `static CertificateTemplate Of(DirectoryObject)` – identity members done | C2 |
| `src/AdMng.Core/Pki/CertificateSummary.cs`, `CrlSummary.cs` | `sealed record CertificateSummary` / `CrlSummary` (members of 2.1); `static … ? Of(ReadOnlySpan<byte>)` | C1 |
| `src/AdMng.Core/Pki/PkiSnapshot.cs` | `PkiSnapshot`, `PkiCertificate`, `PkiCrl`, `PkiCaObject` + `enum PkiCaStore` (`RootCa`, `Aia`, `NtAuth`), `PkiEnrollmentService`, `PkiCrlList`, `PkiKraAgent`, `PkiOidObject`, `PkiTrustMaterial`, `PkiDomainController`, `PkiReadNotice`, `enum PkiReadNoticeCode` (`BaseNotFound`, `BaseAccessDenied`, `ContainerMissing`, `SecurityDescriptorUnreadable`, `CertificateMalformed`, `CrlMalformed`, `CaComputerUnresolved`, `CaComputerInOtherDomain`, `TrusteeUnresolved` – fixed) | C1 |
| `src/AdMng.Core/Pki/PkiOverviewReader.cs` | `sealed record PkiReadOptions` (`PageSize` = 1000, `ResolveCaComputers` = true, `Time`); `PkiReadProgress(int Objects)`; `static Task<PkiSnapshot> ReadAsync(IDirectoryReader, PkiReadOptions, IProgress<PkiReadProgress>?, CancellationToken)`; `static Task<IReadOnlyList<PkiDomainController>> ReadDomainControllersAsync(IDirectoryReader, CancellationToken)` | C1 |
| `src/AdMng.Core/Pki/Tls/IDomainControllerTlsProbe.cs` | `interface IDomainControllerTlsProbe` (`ProbeAsync(string, int, TimeSpan, CancellationToken)`), `TlsProbeResult`, `enum TlsProbeOutcome`, `DcCertificateInfo`, `DcTlsCheck`, `static class DcCertificateEvaluation` (`Evaluate(IReadOnlyList<byte[]>, string dcFqdn, string domainDns, PkiTrustMaterial, DateTimeOffset now)`), `KdcCertificateInfo`, `enum KdcReadinessState`, `static class KdcCertificateEvaluation` (`Evaluate(IReadOnlyList<byte[]>, string dcFqdn, PkiTrustMaterial, DateTimeOffset now)`) | C1 (probe impl W7) |
| `src/AdMng.Core/Pki/PkiHttpCheck.cs` | `PkiUrl(string Url, PkiUrlKind Kind)`, `enum PkiUrlKind` (`AiaCaIssuers`, `Cdp`, `DeltaCdp`, `Ocsp`), `enum PkiHttpOutcome` (2.5), `PkiHttpCheckResult(Url, Kind, Outcome, int? StatusCode, CertificateSummary? Certificate, CrlSummary? Crl)` | done |
| `src/AdMng.Core/Pki/PkiHealth.cs` | `PkiHealthThresholds(int EndEntityWarningDays, int CaWarningDays)`, `enum PkiHealthStatus`, `enum PkiHealthItem` (`CaCertificate`, `AiaLocation`, `CdpLocation`, `BaseCrl`, `DeltaCrl`, `KraCertificate`, `NtAuthCertificate`), `PkiHealthRow`; `static class PkiHealth` – `Evaluate(PkiSnapshot, PkiHealthThresholds, DateTimeOffset now, IReadOnlyList<PkiHttpCheckResult>?)`, `HttpUrls(PkiSnapshot)` | C4 |
| `src/AdMng.Core/Pki/TemplateRights.cs` | `enum TemplateRight` (`Enroll`, `AutoEnroll`, `FullControl`, `WriteDacl`, `WriteOwner`, `WriteAllProperties`, `WriteSensitiveProperty`, `WriteOtherProperty`, `Owner`), `enum PrincipalClass` (`Tier0`, `Broad`, `Other`), `TemplateAce`, `TemplateAcl`, `TemplateTrustee(Sid, Name, Class, Right, AccountCount, DisabledCount, FirstAccounts, IsResolved, Attribute)`, `TemplateRightTotal(Right, HighestClass, AccountCount, DisabledCount, FirstAccounts)`, `TemplateRights(TemplateCn, TemplateGuid, IsEvaluated, Trustees, Totals)`, `PkiAccountRef`, `PkiAccountFact`, `PkiEnrollmentExpansion`, `PkiExpansionContext(Groups, Types, Classifier, Options, FirstAccounts = 20)`, `PkiExpansionProgress(int Accounts)`, `sealed class PrincipalClassifier` | C3 |
| `src/AdMng.Core/Pki/TemplateRightsEvaluator.cs` | `static TemplateAcl Evaluate(ReadOnlySpan<byte>, AclObjectTypes)`; `static bool Decide(IReadOnlySet<string>, TemplateAcl, TemplateRight)`; `static IReadOnlyList<TemplateRights> TrusteeLevel(IReadOnlyList<CertificateTemplate>, AclObjectTypes, PrincipalClassifier)`; `static Task<PkiEnrollmentExpansion> ExpandAsync(IDirectorySession, IReadOnlyList<CertificateTemplate>, PkiExpansionContext, IProgress<PkiExpansionProgress>?, CancellationToken)` | C3 |
| `src/AdMng.Core/Pki/PkiAclGrant.cs` | `enum PkiAclTargetKind`, `PkiAclGrant(TargetDn, TargetKind, TrusteeSid, Right, Detail)` | done |
| `src/AdMng.Core/Pki/KeyCredentialLink.cs`, `AltSecurityIdentity.cs` | `sealed record KeyCredentialLink`, `enum KeyCredentialSource`, `enum KeyCredentialUsage`, `static IReadOnlyList<KeyCredentialLink> Parse(IEnumerable<string>)`; `sealed record AltSecurityIdentity`, `enum AltSecurityIdentityKind`, `enum MappingStrength` (`Strong`, `Weak`, `NotCertificate`, `Invalid`), `static AltSecurityIdentity Parse(string)` | C4 |
| `src/AdMng.Core/Pki/PkiEscModel.cs` | `enum PkiFindingSeverity`, `enum PkiFindingArea` (`Template`, `CertificationAuthority`, `PkiObject`, `Account`), `enum PkiRuleGroup` (`Enrollment`, `AccessControl`, `Mapping`), `[Flags] enum PkiFindingNotes`, `enum PkiApprovalEffect` (`Suppresses`, `Lowers`, `Never`), `sealed record PkiFinding` (2.4), `PkiPrincipal`, `PkiLinkedGroup`, `sealed record PkiEscSnapshot`, `PkiEscReadOptions(bool CaGrantsAuthenticatedUsersEnroll = true)`, `PkiEscProgress`, `sealed record PkiEscResult : IReadOnlyList<PkiFinding>` (`IsEvaluated`, `Notices`, `NotEvaluated`) | C4 |
| `src/AdMng.Core/Pki/PkiFindingCodes.cs` | the 11 `const string` codes, `sealed record PkiFindingDefinition(Code, Esc, Severity, Area, Group, Approval, Locksmith, Mdi)`, `All`, `Find(code)` | done |
| `src/AdMng.Core/Pki/PkiEscAnalyzer.cs`, `PkiEscReader.cs` | `static PkiEscResult PkiEscAnalyzer.Analyze(PkiEscSnapshot)` (C4 may split into `.Esc*.cs`); `static Task<PkiEscSnapshot> PkiEscReader.ReadAsync(PrivilegeScanContext, PkiSnapshot, PkiEnrollmentExpansion, PkiEscReadOptions, IProgress<PkiEscProgress>?, CancellationToken)` | C4 |
| `src/AdMng.Core/PrivilegedAccess/AclObjectTypes.cs` | `CertificateEnrollment` / `CertificateAutoEnrollment` / `AltSecurityIdentities` constants + the two extended-right GUIDs in `Fallbacks` (done); `static Task<AclObjectTypes> ReadAsync(IDirectoryReader, int pageSize, CancellationToken)` (stub) | done / C3 |
| `src/AdMng.Core/Security/ExtendedRightIds.cs` | `CertificateEnrollment`, `CertificateAutoEnrollment` | done |
| `src/AdMng.Core/PrivilegedAccess/AclRight.cs` + both resx | `AclRight.WriteTemplateProperty`, `AclRight.WriteAltSecurityIdentities` + `PrivilegedAccess.AclRight.*` (en + pl) | done |
| `src/AdMng.Core/PrivilegedAccess/DirectoryAclGrantSource.cs` | `public Task<IReadOnlyList<PkiAclGrant>> CollectPublicKeyAsync(PrivilegeScanContext, IReadOnlyList<string> caComputerDns, CancellationToken)` (stub) | C3 |
| `src/AdMng.Core/PrivilegedAccess/PrivilegedAccessScanner.cs` | `CompleteMemberOfAsync` made `internal static (IDirectoryReader, DirectoryObject, CancellationToken)` (behaviour-neutral) | done |
| `src/AdMng.Core/Schema/SecretAttributes.cs` | `"userPKCS12"` in `WellKnownNames`; `static bool IsNeverShown(string attribute, ISchemaCatalog? schema)` | done |
| `src/AdMng.Presentation/Pki/IPkiSnapshotService.cs` | `enum PkiDocumentKind`, `sealed record PkiDocumentRequest`; `interface IPkiSnapshotService` – `Current`, `ReadAt`, `IsBuilding`, `event Changed`, `GetAsync(bool refresh, IProgress<PkiReadProgress>?, CancellationToken)`, `GetExpansionAsync(bool refresh, IProgress<PkiExpansionProgress>?, CancellationToken)`, `GetDomainControllersAsync(bool refresh, CancellationToken)`, `GetDcCertificatesAsync(bool refresh, CancellationToken)`, `CheckHttpAsync(IProgress<int>?, CancellationToken)`, `HttpResults`, `Invalidate()` | done (impl W5) |
| `src/AdMng.Presentation/Pki/IPkiHealthProbe.cs` | `interface IPkiHealthProbe` (`CheckAsync`) | done (impl W5) |
| `src/AdMng.Presentation/Pki/PkiServices.cs`, `PkiDocumentBase.cs` | 2.5 | complete (lead) |
| `src/AdMng.Presentation/Pki/PkiSnapshotService.cs`, `PkiHealthProbe.cs` | stubs | W5 |
| `src/AdMng.Presentation/Pki/PkiViewModelFactory.cs` | `Create(PkiDocumentRequest)`, `static bool Matches(IShellDocument, PkiDocumentRequest)`, `Pki` (the `PkiServices` the node and the tree use); calls the partial below | W5 |
| `src/AdMng.Presentation/Pki/PkiViewModelFactory.Security.cs` | `CreateSecurity(request)`, `CreateReadiness(request)`, `MatchesSecurity(document, request)` (stubs) | W6 |
| `src/AdMng.Presentation/Pki/PkiServiceCollectionExtensions.cs` | `AddAdMngPki()` – `TryAddScoped(sp => new PkiSnapshotService(…))` (2.5) + `TryAddScoped<IPkiSnapshotService>(sp => sp.GetRequiredService<PkiSnapshotService>())`, `TryAddScoped<PkiServices>` (probes through `GetService`), `TryAddScoped<PkiViewModelFactory>`, `TryAddSingleton<IPkiHealthProbe, PkiHealthProbe>` | done |
| `src/AdMng.Presentation/Pki/PkiFindingText.cs`, `PkiTextKeys.cs` + the contract resx block | 2.8 | done (C4: counts + texts; W6: wording) |
| `src/AdMng.Presentation/Shell/ShellViewModel.Pki.cs` | the stub members of 2.5 and their call sites | W5 |
| `src/AdMng.Presentation/Settings/PkiSettings.cs` + `AppSettings.Pki` | 2.5 | done |
| `src/AdMng.Presentation/Icons/ObjectIcons.cs`, `src/AdMng.App/Controls/Glyphs.cs` | `ObjectIcon.Certificate` + its glyph/tone | done |
| `src/AdMng.App/SmokeTest.cs` → `static partial` + `SmokeTest.Pki.cs` / `SmokeTest.PkiSecurity.cs` | `RenderPkiAsync` (stub, W5), `RenderPkiSecurityAsync` (stub, W6), both called from `RenderWindowsAsync` | W5 / W6 |

### 2.12 Work split and file ownership

**Waves.** Wave 1 (parallel worktrees from the branch with the contract commit): **C1** overview reader, snapshot,
certificate/CRL parsers, CA computer resolution, TLS-probe contract, DC/KDC certificate evaluation; **C2** template model
+ flag/period decoders + the `FlagNames` golden test and delegation; **C3** rights evaluator, classifier, account pass,
U4b PKI containers + `CollectPublicKeyAsync`; **C4** account parsers, health rules, ESC reader + analyzer. Wave 2
(parallel, from the merged wave 1): **W5** node, snapshot service, HTTP probe, settings section, overview/health/
templates documents, shell; **W6** security review + readiness report; **W7** property tabs, attribute-editor mask, TLS
probe implementation. Docs (`docs/CERTYFIKATY.md`, README/ROADMAP/ARCHITECTURE/PILOTAZ edits, the CLAUDE.md layout row of
`AdMng.Directory` and the rule-1/4 notes on the probe and the HTTP check, regenerated screenshots) are the integration
step after wave 2 (lead), from the as-built notes.

**The contract commit (lead)** holds everything marked *done* / *complete* in 2.11 and every stub, plus: the shared test
files below, `tests/AdMng.Presentation.Tests/Pki/PkiHarness.cs` (with a `DirectPkiSnapshotService` test double that runs
the Core readers without caching, so W6/W7 tests do not need W5's service), `PkiFindingTextTests.cs`, both
`PkiContractTests.cs`, `SecretUserPkcs12Tests.cs`, `tests/AdMng.Testing/Pki/{FakeDomainControllerTlsProbe,
FakePkiHealthProbe}.cs`, `UseFake<IDomainControllerTlsProbe>` and `UseFake<IPkiHealthProbe>` in `UiTestContext`, the
`ConnectionLifetimes` entries (`PkiServices`, `PkiSnapshotService`, `IPkiSnapshotService`, `PkiViewModelFactory`), the
shell call sites, two comment anchors in `MainWindow.xaml` (`<!-- P1 PKI (W5) -->`, `<!-- P1 PKI security (W6) -->`), the
`SmokeTest` calls, and **all six resx marker pairs** in both files, in this order after
`<!-- K7 settings search (21.4): end -->`: `P1 contract – AD CS finding texts and shared names (2.4, 2.8)` (filled),
`P1 PKI overview, health and settings (2.1, 2.5)` (W5), `P1 certificate templates and enrollment rights (2.2, 2.3, 2.5)`
(W5), `P1 AD CS security review (2.4, 2.5)` (W6), `P1 strong mapping and NTAuth readiness (2.5)` (W6), `P1 certificates on
accounts (2.6)` (W7) – each agent inserts only between its own markers. C4's analyzer tests build `PkiEscSnapshot` by
hand; only the end-to-end reader tests (C3's `ExpandAsync` on the sample, the W5–W7 documents on the real readers) need
the merge – written against the stubs and marked `[Fact(Skip = "P1 wave 1 merge")]` until the lead removes the skip.

**New files (exclusive).** A file the contract commit created as a stub belongs to the named agent from then on (marked
*bodies*); the agent keeps its public signatures.

| Owner | Files |
|---|---|
| C1 | `src/AdMng.Core/Pki/{PkiSnapshot,PkiOverviewReader,CertificateSummary,CrlSummary}.cs` (bodies); `src/AdMng.Core/Pki/Tls/**` (bodies); `tests/AdMng.Core.Tests/Pki/{PkiOverviewReaderTests,CertificateSummaryTests,CrlSummaryTests,DcCertificateEvaluationTests,KdcCertificateEvaluationTests}.cs` |
| C2 | `src/AdMng.Core/Pki/{PkiFlags,PkiPeriod,CertificateTemplate}.cs` (bodies); `tests/AdMng.Core.Tests/Pki/{PkiFlagsTests,PkiPeriodTests,CertificateTemplateTests}.cs`; `tests/AdMng.Watchdog.Core.Tests/Detection/FlagNamesCertificateGoldenTests.cs` |
| C3 | `src/AdMng.Core/Pki/{TemplateRights,TemplateRightsEvaluator}.cs` (bodies); `tests/AdMng.Core.Tests/Pki/{TemplateRightsEvaluatorTests,PrincipalClassifierTests,TemplateExpansionTests}.cs`; `tests/AdMng.Core.Tests/PrivilegedAccess/DirectoryAclPublicKeyTests.cs` |
| C4 | `src/AdMng.Core/Pki/{KeyCredentialLink,AltSecurityIdentity,PkiHealth,PkiEscModel,PkiEscAnalyzer,PkiEscReader}.cs` (bodies; + `PkiEscAnalyzer.Esc*.cs`); `tests/AdMng.Core.Tests/Pki/{KeyCredentialLinkTests,AltSecurityIdentityTests,PkiHealthTests,PkiEscAnalyzerTests}.cs` |
| W5 | `src/AdMng.Presentation/Pki/{PkiSnapshotService,PkiHealthProbe,PkiViewModelFactory}.cs` (bodies); `src/AdMng.Presentation/Pki/{PkiSettingsViewModel,PkiOverviewDocumentViewModel*,PkiTemplatesDocumentViewModel*}.cs`; `src/AdMng.Presentation/Navigation/PkiNodes.cs`; `src/AdMng.Presentation/Shell/ShellViewModel.Pki.cs` (body); `src/AdMng.Presentation/Localization/{L.Pki,L.PkiTemplates}.cs`; `src/AdMng.App/Pki/{PkiOverviewView,PkiTemplatesView}.xaml(.cs)`; `src/AdMng.App/SmokeTest.Pki.cs` (body); `tests/AdMng.Presentation.Tests/Pki/{PkiSnapshotServiceTests,PkiHealthProbeTests,PkiSettingsTests,PkiTreeAndShellTests,PkiOverviewTests,PkiTemplatesTests}.cs`; `tests/AdMng.App.UiTests/Pki/PkiOverviewUiTests.cs`; `tests/AdMng.App.UiTests/ScreenshotTestsPki.cs` (36, 37) |
| W6 | `src/AdMng.Presentation/Pki/PkiViewModelFactory.Security.cs` (body); `src/AdMng.Presentation/Pki/{PkiSecurityReviewDocumentViewModel*,PkiReadinessDocumentViewModel*,PkiFindingRowViewModel}.cs`; `src/AdMng.Presentation/Localization/{L.PkiSecurity,L.PkiReadiness}.cs`; `src/AdMng.App/Pki/{PkiSecurityReviewView,PkiReadinessView}.xaml(.cs)`; `src/AdMng.App/SmokeTest.PkiSecurity.cs` (body); `tests/AdMng.Presentation.Tests/Pki/{PkiSecurityReviewTests,PkiReadinessTests}.cs`; `tests/AdMng.App.UiTests/Pki/PkiSecurityUiTests.cs`; `tests/AdMng.App.UiTests/ScreenshotTestsPkiSecurity.cs` (38, 39) |
| W7 | `src/AdMng.Presentation/Pki/{PublishedCertificatesPageViewModel,NameMappingsPageViewModel,KeyCredentialsPageViewModel}.cs`; `src/AdMng.Presentation/Localization/L.Certificates.cs`; `src/AdMng.Directory/Pki/SslStreamTlsProbe.cs`; `src/AdMng.App/Pki/PkiAppRegistration.cs`; `tests/AdMng.Directory.IntegrationTests/Pki/SslStreamTlsProbeTests.cs`; `tests/AdMng.Presentation.Tests/Pki/{CertificateTabsTests,AttributeEditorMaskTests}.cs`; `tests/AdMng.App.UiTests/Pki/CertificateTabsUiTests.cs` |

Shared test files written by the **lead in the contract commit** (C1–C4 and W5–W7 all read them):
`tests/AdMng.Testing/{FakeDirectoryBuilder.PublicKey,FakeSchema.PublicKey}.cs` (`AddPublicKeySample`,
`AddPublicKeyAclSample`), `tests/AdMng.Testing/Pki/PkiSamples.cs`, and the Enroll/AutoEnroll additions to
`FakeDirectoryBuilder.Acl.cs`.

**Shared files – changes only at these anchors** (both sides kept when two owners insert at the same anchor; Core waves
before Presentation):

| File | Owner: change at anchor |
|---|---|
| `src/AdMng.Core/PrivilegedAccess/DirectoryAclGrantSource.cs` | C3: the PKI pass after the `CN=Policies` GPO block in `RunAsync`; `AclTargetKind.PublicKey`; the provisioning trustees in `IsDefaultTrustee`; the `WriteTemplateProperty` / `WriteAltSecurityIdentities` branches in `RightsOf`; the body of `CollectPublicKeyAsync` |
| `src/AdMng.Core/PrivilegedAccess/AclObjectTypes.cs` | C3: the template-attribute and `altSecurityIdentities` GUIDs in `Fallbacks`; the body of `ReadAsync` |
| `src/AdMng.Core/Security/ExtendedRightIds.cs`, `AclRight.cs`, `SecretAttributes.cs`, `PrivilegedAccessScanner.cs` | done by the contract commit; agents only use them |
| `src/AdMng.Watchdog.Core/Detection/FlagNames.cs` | C2: the two certificate tables become `PkiFlags.Select(…, <today's bits in today's order>)` – a separate commit after the golden test; nothing else in the file |
| `src/AdMng.Presentation/Resources/Strings.resx`, `Strings.pl.resx` | each owner only between its own marker pair (above); C4 may edit the contract block's finding texts and `ArgumentCounts`-bound placeholders |
| `src/AdMng.Presentation/ServiceCollectionExtensions.cs` | done by the contract commit: `AddAdMngPki()` before the scoped block |
| `src/AdMng.Presentation/Navigation/NavigationTreeViewModel.cs` | W5: the `pki` constructor parameter, `ShowPkiNodes` (index after the GPO nodes), `ShowPkiNodes(Settings.Current.Pki.ShowInTree)` after the GPO line in `InitializeAsync`, the `OnCultureChanged` relocalize loop, the `OnSettingsChanged` toggle (after the GPO lines) |
| `src/AdMng.Presentation/Settings/SettingsViewModel.cs`, `src/AdMng.App/Views/SettingsWindow.xaml` | W5: the `Pki` sub view model after the `Computers` block (constructor, property, `Error` chain, `Apply`); the *Certyfikaty* section at the end of the *Ogólne* tab |
| `src/AdMng.Presentation/PropertySheets/PropertyPageFactory.cs` | W7: the `PageIf` helper and the tabs in `User(obj)` / `Contact(obj)` / `Computer()` (2.6) |
| `src/AdMng.Presentation/PropertySheets/AttributeEditorPage.cs` | W7: the `IsNeverShown` mask of 2.6 (`ValueText`, `ReadOnlyReason`, `Edit` / `CopyValue` can-execute, `CloseEditor`) |
| `tests/AdMng.Presentation.Tests/Dialogs/PropertySheetTests.cs` | W7: the user (`:38`) and computer (`:361`) orders; a new contact order test |
| `src/AdMng.Presentation/Shell/ShellViewModel.cs`, `.Palette.cs`, `.Commands.cs`, `.Documents.cs` | done by the contract commit (call sites); nobody else |
| `src/AdMng.App/AppHost.cs` | W7: `AddAdMngPkiApp()` after `AddAdMngPresentation()` |
| `src/AdMng.App/Views/MainWindow.xaml` | W5: the `pki:`/`pkiv:` xmlns, the `PkiNodeViewModel` tree template, its document templates and the `pki.*` menu items at `<!-- P1 PKI (W5) -->`; W6: its document templates at `<!-- P1 PKI security (W6) -->` |
| `src/AdMng.App/Views/TreeMenu.cs` | W5: a `case PkiNodeViewModel:` block |
| `src/AdMng.App/Views/PropertiesWindow.xaml` | W7: the three `DataTemplate`s next to the "Stan" template; the `pk:` xmlns |
| `tests/AdMng.App.UiTests/HostTests.cs` | W7: `IDomainControllerTlsProbe` in `SharedApp`; W5: the `Assert.Same(IPkiSnapshotService, PkiSnapshotService)` check; `Assert.Equal(2, …IPrivilegeGrantSource)` unchanged (also in `ServiceRegistrationTests`) |
| `tests/AdMng.App.UiTests/Infrastructure/UiTestContext.cs` | done by the contract commit (the two `UseFake` lines); no sample is seeded globally |
| `tests/AdMng.Testing/FakeSchema.cs` | lead (contract commit): `attributes.AddRange(CreatePublicKeyAttributes()); ExtendForPublicKey(classes, schemaNc);` in `CreateCore` |

**Files nobody changes in P1:** `L.cs`, `Directory.Packages.props`, `AdMng.slnx`, the Watchdog projects other than the
`FlagNames.cs` delegation and the two new Watchdog test files, `AddSampleCompany`, `AddWatchdogInfrastructure`,
`Fields.cs`, `EventLogServices.cs` / `EventLogLauncher.cs` (track E), the stage-S `ReadinessChecker` / `ReadinessCodes`
(the PKI readiness report is a PKI-area document – README puts stage-S PKI rows in P5), and `CLAUDE.md` during the waves
(the lead updates it in the integration step). Any other change to a file another owner holds needs the lead's agreement
and a line in 2.14; a merge conflict outside the anchors above means a rule of this section was broken.

### 2.13 Review notes

Design review of the first draft (three reviewers: `adcs`, `architecture`, `workplan`), 2026-11-09. Every finding was
checked against the code and against Certipy (commit 3ae3442) and Locksmith (commit 8b555bd); findings taken in full are
fixed in 1–2.12 without a note. Taken in part or rejected:

* adcs-6 (approval gates only ESC1/2/3/17) – in part: Certipy 3ae3442 checks ESC9, ESC13, ESC15 and ESC17 inside its
  `if not requires_approval` block too, only Locksmith reports ESC9/13/15 regardless; the table now has an *Approval*
  column – *suppresses* for ESC1/2/3/17, *lowers* (one level + `ApprovalRequired`) for ESC9/13/15, *never* for 4/5/14.
* adcs-7, adcs-13 – no separate change; both resolved by the *Approval* column (adcs-6).
* adcs-9 (report an empty-EKU template as ESC2 only) – rejected: Certipy's `client_authentication` includes Any Purpose,
  so it reports ESC1 and ESC2 on such a template, and README's completion criterion requires finding everything Certipy
  finds; the rows are grouped by template instead, Any Purpose / no EKU stay in the predicates.
* adcs-11 (safe SIDs) – taken, with a correction: `-498`, `-521`, `-526`, `-527` are already U4b default trustees by RID;
  only Cert Publishers (`-517`) and the root's Domain Admins in child windows were missing.
* adcs-12 (WriteProperty model) – in part: a graded model instead of Certipy's all-properties-only rule (Certipy flags
  only all-properties writes, Locksmith every write but Enroll/AutoEnroll – the graded model finds both); the attribute
  list is unified in `PkiAttributes.SensitiveTemplateAttributes`.
* adcs-17 (standalone `X509:<SR>`) – in part: the draft had no standalone `<SR>` form; the rule "`<SR>` without `<I>` →
  `Invalid`" is added.
* adcs-19 (CRL extension OIDs) – no change: confirmation only (`2.5.29.20`, `2.5.29.27` correct).
* architecture-1 (`FlagNames` keeps its own arrays) – in part: correct tables and the golden test taken; decision 8 keeps
  the delegation, made neutral by `PkiFlags.Select` with today's bits in today's order.
* architecture-2, workplan-31 (masking) – in part: masking is limited to key material and binary secrets
  (`SecretAttributes.IsNeverShown`), which needs no raw-value formatter; LAPS / BitLocker recovery / confidential text
  keep today's display and secure copy – whether to mask them too is an operator question (below).
* architecture-12, workplan-24 (KDC readiness) – in part: the evaluation is labelled LDAPS-only and gets `now`, roots,
  intermediates and NTAuth separately; KDC readiness is shown as an indicator from the DCs' published `userCertificate`
  (workplan-24) instead of moving it wholly to P4/K1 (architecture-12); the KDC's own store stays P4/K1.
* architecture-16, workplan-32 (CA-host navigation) – in part: no `ComputerLookup` and no `PkiHostNavigator`; C1 resolves
  the CA computer DN/SID (workplan-18) and W5 navigates by DN through `PkiServices`, so no track-E or stage-S file changes.
* workplan-8 (freeze the `PkiDocumentBase` surface) – in part: the lead writes `PkiServices` and `PkiDocumentBase`
  complete in the contract commit, not as stubs W5 fills, so W6's documents run in W6's worktree.
* workplan-11 (`CreateAsync(publicKey: true)`) – in part: architecture-7's form taken – tests that need PKI data call
  `AddPublicKeySample` themselves, as the GPO tests do with `AddGroupPolicyManagementSample`.
* workplan-13 (C1 cannot test its reader) – in part: the certificate/CRL parsers move to C1, the contract implements the
  identity part of `CertificateTemplate.Of`, health becomes C4's `PkiHealth.Evaluate`.
* workplan-17 (`CollectPublicKeyAsync` returning `PrivilegeGrant`) – in part: it returns typed `PkiAclGrant` values
  (architecture-8), since `PrivilegeGrant.Values` are formatted strings.
* workplan-21 (`EffectiveEku` only) – in part: `EffectiveEku` is defined, but the rules evaluate it **and**
  `pKIExtendedKeyUsage` (Certipy and Locksmith read only the latter), marking disagreements `EkuSourcesDiffer`.
* Author's open questions, settled: ESC17 = Certipy's definition (ESS + server-auth incl. Any Purpose; Locksmith only the
  Server Auth OID); Locksmith ids are the `Technique` strings (ESC14 has none; ESC16/17 exist but ESC16 is P4), MDI ids
  stay null; ESC14 covers weak mappings and `altSecurityIdentities` writes, no issuer cross-check in P1; the TLS probe
  stays in `AdMng.Directory`; the PKI pass stays inside `DirectoryAclGrantSource` (source count 2).
* Operator question (open): should the attribute editor also mask the LAPS clear-text password, the BitLocker recovery
  password and confidential text attributes (today shown and copied securely; P1 keeps that)?

### 2.14 P1 as built

*(empty – each wave agent appends its as-built note here: `**C1 as built.**` … `**C4 as built.**`, `**W5 as built.**` …
`**W7 as built.**`, `**Integration.**`, `**P1 review fixes**`.)*

**Contract commit.** Everything of 2.11/2.12 is in the code; deviations and additions (none renames or removes a member
of 2.1–2.6):
* *Shapes.* `CertificateSummary` and `CrlSummary` are records of init properties with defaults (hand-built test
  instances set only what they need); `CertificateSummary.SanSid` added; CRL numbers are `BigInteger?`. `PkiCrlList.Base`
  / `Delta` are one `PkiCrl?` each (the first value). The object GUIDs of the snapshot records are `Guid`;
  `PkiDomainController.HostName` is nullable and its `Certificates` raw DER (what `KdcCertificateEvaluation` takes).
  `TemplateRightTotal.HighestClass` is `PrincipalClass?` (null: nobody). `PkiDecodedFlags.Fields` holds
  `PkiFlagFieldValue` records (bindable). `PkiEscProgress(PkiEscStage Stage, int Done)` with a new `PkiEscStage`;
  `PkiEscSnapshot.Notices` (init); `PkiEscResult.Notices` / `NotEvaluated` are `PkiReadNotice` / `PkiRuleGroup` lists.
  `PkiFindingNotes` has `None = 0` (`PkiTextKeys.NoteFlags` leaves it out). Records cross agent boundaries positionally:
  **add members as init properties, never as new positional parameters** (C4 builds `PkiSnapshot` / `PkiEscSnapshot` by hand).
* *Additions.* `PkiSnapshot.Empty`, `PkiTrustMaterial.Empty`, `PkiHealthThresholds.Default`, `PkiFlags` EKU constants
  (`KnownEku` maps OID → name), `PkiSettings.Thresholds()`, `IPkiSnapshotService.Expansion` and `DcCertificates` (the
  cached results; the readiness report shows them without probing), `PkiDocumentBase.IsRefresh` (F5 → pass it as
  `refresh`), `PkiFindingText.Message/Severity/Area`, `PkiTextKeys.Notes`. Implemented already (pure): the derived members
  of `CertificateTemplate` and its identity part of `Of`; `PrincipalClassifier`'s constructor (properties).
  `ArgumentCounts`: 3 per code, 4 for `IssuancePolicyLinkedToGroup` ({3} the group); the ESC14 message is generic – C4
  rewords it per variant.
* *Stubs that never throw* (the window scope, the app start-up and the existing tests run them): `PkiSnapshotService`
  (nothing cached, `Invalidate`/`Dispose` no-ops), `AttachPki`/`DetachPki`, `TryActivatePkiNode` (false),
  `PkiCommandDescriptors`/`PkiStateCommands` (empty), the two `SmokeTest` renders. `ActivePki()` (a switch with a
  `PkiDocumentBase` arm W5 extends) and `PkiDocuments` are implemented in `ShellViewModel.Pki.cs`.
* *Shared files beyond 2.12.* `MainWindow.xaml` also gets the `pki:`/`pkiv:` xmlns and one tab-header template for
  `PkiDocumentBase` (W5 and W6 would both add them); `pkiv:` needs a type in `AdMng.App.Pki`, so
  `src/AdMng.App/Pki/PkiAppRegistration.cs` exists as a stub (W7 writes its body) and `AppHost` already calls
  `AddAdMngPkiApp()` – W7 no longer touches `AppHost`. `ConnectionLifetimes.Shared` lists `IPkiHealthProbe`. The
  `AclRight` texts sit with the other `PrivilegedAccess.AclRight.*` entries. `AddAclDefinitions` is idempotent (the PKI
  samples call it). `FakePkiHealthProbe` lives in `tests/AdMng.Testing/Pki` but is compiled by the Presentation and UI
  test projects (linked; `AdMng.Testing` references Core only and the Watchdog tests use it). New shared helper
  `tests/AdMng.Testing/Pki/PkiRecordSamples.cs` (the reflection checks of both `PkiContractTests`).
* *Samples.* `PkiSamples.Now` = 2026-10-13 12:00 UTC (`ManualTimeProvider`'s default). The fake schema gives the new
  attributes `Def`'s deterministic schemaIDGUIDs (samples read them from the session schema, as U4 does) and the six PKI
  classes their real ones (`AclGuids.PkiCertificateTemplate` …); C3 still adds the real attribute GUIDs to `Fallbacks`.
  `msDS-OIDToGroupLink` is a plain DN attribute (no back link). `PublicKeyTemplates` names every template by role
  (defaults, `Vulnerable`, `Safe`, the publishing CA); the root CA (not in NTAuth) publishes `SubCA` and
  `Logowanie-Kartą`, the issuing CA everything else but `Użytkownik-VPN-Niewystawiany`. `AddPublicKeySample` and
  `AddPublicKeyAclSample` (default PKI + one delegation per target kind) are alternatives – call one per builder.
* *Review fixes.* `CertificateSummary.FreshestCrl` and `CrlSummary.FreshestCrl` (init, `2.5.29.46`; C1 parses them)
  give `PkiUrlKind.DeltaCdp` its URLs, with a new `PkiHealthItem.DeltaCrlLocation` after `CdpLocation` (W5's
  `Pki.HealthItem.*` texts cover it); the sample's issuing base CRL carries the Freshest CRL
  `PkiSamples.IssuingDeltaCrlHttpUrl`, as AD CS writes it for *Include in CRLs* (`PkiCertificateOptions.FreshestCrl` puts
  one into a certificate). `PkiApprovalEffect.ManagerApprovalSuppresses` for `EnrollmentAgentTargetTemplate` (only manager
  approval suppresses ESC3 condition 2; the signature is the condition). List arguments are joined with
  `PkiFindingCodes.ListSeparator`; the rights of ESC4 / ESC5 and the mapping kinds or rights of ESC14 are enum names
  (`TemplateRight`, `AclRight`, `AltSecurityIdentityKind`) that `PkiFindingText.Message` shows through
  `PkiFindingText.Argument` (`PkiTpl.Right.*`, `PrivilegedAccess.AclRight.*`, `Cert.MappingKind.*`). `Pki.Notice.*` and
  `Cert.MappingKind.*` moved from W5 / W7 into `PkiTextKeys` (`NoticeKey`, `MappingKindKey`) and the contract block, since
  W6 shows both – they leave 2.8's single-wave list. `PkiDocumentBase` resets `IsLoading` / `IsRefresh` when a load finds
  no session (a session that ended under a running load left the spinner on) and turns the failures of its new
  `protected static IsExpected` (now also `UnauthorizedAccessException`, `TimeoutException`, `CryptographicException`,
  `FormatException`) into `Error`. `ActivePki()` moved to `ShellViewModel.Pki.cs` (`.Documents.cs` is back to its pre-P1
  state). The 2.12 rows for `AppHost` and `MainWindow.xaml` are superseded: W7 leaves `AppHost` alone, W5 adds no
  xmlns and puts the `PkiNodeViewModel` tree template at `<!-- P1 PKI tree (W5): … -->` (`Window.Resources`), the
  *Widok* toggle at `<!-- P1 PKI view toggle (W5) -->`, the `pki.*` items at `<!-- P1 PKI menu (W5): … -->`
  (*Narzędzia*) and its document templates at `<!-- P1 PKI (W5) -->`. Samples: `DcLdapsDer`, `DcKdcDer`, `KraDer` and
  `UserCertificateDer` (per argument set) are created once per process, each call a copy; `GG_Administratorzy_PKI` is a
  member of Enterprise Admins (a universal group cannot nest in the global Domain Admins); the KRA certificate and
  Zofia's end on 2027-02-01, inside the issuing CA's validity; the CES entry of `msPKI-Enrollment-Servers` has
  authentication 2 (Kerberos). Polish `PrivilegedAccess.AclRight.WriteTemplateProperty` = *Zapis właściwości szablonu
  certyfikatu istotnej dla bezpieczeństwa*.

**C1 as built.** No public member renamed or removed; every addition is an init member.
* *Additions.* `CrlSummary.Version` (1 when the TBS `version` field is absent, 2 for v2 – AD CS writes v2; a hand-built
  instance defaults to 2). `PkiCrlList.Authority` (`authorityRevocationList` of the CDP object – `certutil -dspublish`
  writes a root CA's CRL there as well; parsed like `Base`, a malformed one is `CrlMalformed`, used for `IssuerCaGuid` when
  neither base nor delta parses). Internal only: `PkiDer` and `GeneralNameLists` in `CertificateSummary.cs` (the
  `System.Formats.Asn1` readers both parsers, the reader and the TLS checks share) and `Tls/CertificateChecks.cs` (names,
  chain, NTAuth match).
* *AD facts the note did not state (the reader handles them).* `CN=OID` is itself an `msPKI-Enterprise-Oid` object in AD:
  the reader treats it as the container, never as an OID object. A `certificationAuthority` object must contain
  `cACertificate`, `certificateRevocationList` and `authorityRevocationList`, so an empty store (and every AIA / root /
  NTAuth object's CRL attributes) holds a one-byte `0x00` placeholder: such values (and empty ones) are skipped silently
  everywhere, and CRLs are read only from `cRLDistributionPoint` objects. `crossCertificatePair` values are X.509
  `CertificatePair ::= SEQUENCE { forward [0] EXPLICIT Certificate OPTIONAL, reverse [1] EXPLICIT Certificate OPTIONAL }`
  (CryptoAPI `X509_CERT_PAIR`): both certificates become `CrossCertificates` entries (a bare certificate value is accepted
  too) and go into `TrustMaterial.Intermediates`.
* *Reader.* Notices are one per code and object; `BaseNotFound` / `BaseAccessDenied` carry the `CN=Public Key Services` DN
  (no GUID), `ContainerMissing` the well-known DN; any other error of the base search (server unavailable, busy …)
  propagates – a transient outage is never shown as *brak AD CS*. `TrustMaterial` lists are deduplicated by SHA-256;
  `Intermediates` = AIA `cACertificate` + every cross certificate + the enterprise CA certificates. `IssuerCaGuid`: CRL
  issuer = CA certificate subject (both formatted by `X500DistinguishedName.Name`, so text equality is an encoding-level
  match) and CRL AKI = certificate SKI; a CRL without AKI matches by subject only when exactly one CA has it; the first
  of base, delta and ARL that parses decides (a malformed base does not hide the delta's CA). Order: CAs, stores and KRA
  by name, CDP by host then name, OIDs by display name, templates by cn (all `OrdinalIgnoreCase`), DCs by host name.
  Progress every `PageSize` objects and once at the end. `ReadDomainControllersAsync` pages by 1000.
* *CA computer lookup (deviation from 2.1).* A host whose domain part (after the first label) is the window's domain is
  looked up by `(|(dNSHostName=host)(sAMAccountName=label$))` (label cut to 15 characters); the `dNSHostName` match wins and
  the `sAMAccountName` fallback is taken only when it is the one match – not found → `CaComputerUnresolved`. Any **other**
  host is looked up too, but by its exact `dNSHostName` only: a disjoint DNS namespace (host suffix ≠ AD domain) still
  resolves, and a CA `srv-ca02.child.contoso.test` is never mistaken for the window domain's `SRV-CA02$` (the label
  fallback would make an unrelated computer Tier 0) – not found → `CaComputerInOtherDomain`. A CA without `dNSHostName`,
  or a lookup refused with `AccessDenied` / `NoSuchObject`, is `CaComputerUnresolved`.
* *Parsers.* `Subject` / `Issuer` = `X500DistinguishedName.Name`. `KeyAlgorithm` / `SignatureAlgorithm` are fixed
  certutil-style names for the common OIDs (`RSA`, `ECC`, `DSA`, `Ed25519`; `sha256RSA`, `RSASSA-PSS`, `sha256ECDSA` …), the
  same on Linux and Windows, else the friendly name or the OID. `IsSelfSigned` verifies the signature with the own key
  (RSA PKCS #1 MD5/SHA-1/SHA-2, RSA-PSS with salt = hash length, ECDSA, DSA), so a CA renewal link certificate (same name,
  signed by the old key) is **not** self-signed; where that cannot be decided it falls back to names equal and AKI absent
  or = SKI. Every extension is parsed on its own – a malformed one leaves only its members empty and the summary is still
  returned (null only when the certificate itself does not load). SAN IA5 values with non-ASCII bytes are read as UTF-8;
  the UPN other name in any usual string type; the SID extension's value as the OCTET STRING AD CS writes (or a string).
  `CrlSummary.Of` tries DER, then BER (non-minimal lengths some CAs write); a version other than v1/v2, trailing data or a
  truncated value → null; the CRL signature is not verified (the issuer is not known there).
* *TLS evaluations.* Names follow RFC 6125: SAN DNS entries, the subject CN only without them, a wildcard only as the whole
  left-most label of a name with at least two more labels (no partial `dc*`), case-insensitive, trailing dot ignored;
  `NameMatchesDomain` uses the same rule. `DaysLeft` = floor of the days to `NotAfter`. `ChainsToAdRoot`: the chain ends
  at a certificate of `trust.Roots` and no element reports a problem **except the leaf's own** `NotTimeValid` /
  `NotTimeNested` (that is `IsTimeValid`; an expired intermediate does break the chain); NTAuth certificates are in the
  extra store too. `ChainStatus` = the distinct `X509ChainStatusFlags` names of the build (incl. the leaf's
  `NotTimeValid`). `IssuerInNtAuth` (DC and KDC) is matched directly, not through the chain: an NTAuth certificate whose
  subject is the leaf's issuer (as encoded), whose SKI is the leaf's AKI (when both carry one: a third-party CA
  certificate without SKI is decided by the signature) and whose key verifies the leaf's signature – so it holds also
  when the chain does not build, and a certificate with the issuer's name but another key does not count.
  An empty chain or an unparsable leaf gives `Leaf = null` and every flag false.
* *KDC (correction to 2.1).* The Smart-Card Logon + Server Authentication pair is the v2 *Domain Controller
  Authentication* template's EKU set; the v1 *Domain Controller* template has only Client + Server Authentication and is
  reported `NoKdcEku` (a KDC can still use it for PKINIT without strict KDC validation – the wording should say *brak EKU
  KDC*, not *nie działa*). No EKU / Any Purpose is `NoKdcEku` as well. `Expired` also covers *not yet valid*. Unparsable
  and placeholder values are ignored (only those → `NoPublishedCertificate`). `Best` prefers a certificate naming the DC,
  then the latest `NotAfter` (for `NoKdcEku` over all published certificates).
* *Tests* (`tests/AdMng.Core.Tests/Pki/`): `CertificateSummaryTests` (17), `CrlSummaryTests` (15), `PkiOverviewReaderTests`
  (25, incl. `RecordingReader`, which drops `nTSecurityDescriptor` from a search without the SD flag as a DC does),
  `DcCertificateEvaluationTests` (24), `KdcCertificateEvaluationTests` (9). No wave-1 end-to-end test needed a skip.

**C2 as built.** `PkiFlags`, `PkiPeriod` and `CertificateTemplate.Of` are complete; no member of 2.1–2.6 renamed or removed.
* *Flag tables* (`PkiFlags.Table`, ascending bit order): 16 name, 18 enrollment, 13 private-key and 11 general bits – the
  2.2 lists, with one correction from the check 2.2 asked for: `0x80` `CT_FLAG_DOMAIN_AUTHENTICATION_NOT_REQUIRED` and
  `0x200` `CT_FLAG_ADD_TEMPLATE_NAME` are not in MS-CRTD 2.26 (Certipy's `EnrollmentFlag` took them from the SDK's
  `X509EnrollmentFlags`, `EnrollFlagDomainAuthenticationNotRequired` / `EnrollFlagAddTemplateName` – note their odd
  spelling there), so they are dropped and decode as the unknown bits `0x80` / `0x200`; adding them later is one table line
  and two `PkiTpl.Flag.*` texts. Only the general `0x2` / `0x8` / `0x10` are `IsReserved`. Not re-checked against the
  live spec (learn.microsoft.com was not reachable from the wave session), so kept as 2.2 lists them: the name-flag bits
  `0x2` `CT_FLAG_ADD_EMAIL`, `0x4` `CT_FLAG_ADD_OBJ_GUID`, `0x100` `CT_FLAG_ADD_DIRECTORY_PATH` (Certipy; the v1
  general-flag positions) and `0x800000` `CT_FLAG_SUBJECT_ALT_REQUIRE_SPN` (also the SDK's
  `SubjectAlternativeNameRequireSPN`). The SDK's private-key `0x100000` (`PrivateKeyHelloKspKey`, neither in 2.27 nor in
  Certipy) decodes as unknown.
* *Fields and decoding.* `PkiFlags.MinimumCaVersion` (`0x000F0000`, shift 16) and `MinimumClientVersion` (`0x0F000000`,
  shift 24) are the two `PrivateKeyFields`; new `PkiFlags.Fields(attribute)` (empty but for the private-key flag).
  `Decode` uses the low 32 bits only (the negative `Integer` AD returns and its `uint` decode alike), always lists both
  fields – also when 0 (schema 1: the UI may hide them) – and field bits never count as unknown. Additions:
  `PkiDecodedFlags.UnknownBitNames` (each unknown bit as `0x…`, ascending) and `PkiFlags.BitName(long)` (the `FlagNames`
  format); `Select` throws `ArgumentException` for a bit outside the table (a programming error). `KnownEku` also names
  18 purposes of the default templates (Code Signing, Secure Email, the three IPsec, Time Stamping, OCSP Signing, IKE
  intermediate, Trust List Signing, EFS, File Recovery, Key Recovery, Document / Lifetime Signing, Private Key Archival,
  Key Recovery Agent, DS Email Replication, Remote Desktop Authentication – the English names Windows shows). The
  predicates trim each OID and compare ordinally.
* *Periods.* `PkiPeriod.Decode` also returns null for a positive stored value (an absolute time, not an interval) and for
  `long.MinValue`; 0 decodes to a zero period. **Correction of 2.2:** the display units include **months** – new
  `PkiPeriodUnit` (`Hours`, `Days`, `Weeks`, `Months`, `Years`) and `PkiPeriod.Unit` / `Count`, the largest unit that
  divides the length exactly in the order years (365 days), months (30 days), weeks, days, hours: `certtmpl.msc` offers
  months and Certipy's `span_to_str` uses the same units (the default 1 year is `00 40 39 87 2E E1 FE FF` = 365 days,
  6 weeks `00 80 A6 0A FF DE FF FF`); `Unit` is null when no whole number of hours fits (the UI then shows `Duration`).
* *`CertificateTemplate.Of`* reads every member of 2.2. Multi-valued strings are trimmed, empty and repeated (ordinal)
  values dropped, server order kept; a schema version below 1 or unparseable is 1, other malformed numbers read as absent
  (0 / null), a period that is not 8 bytes is null. `CryptoProviders` holds the provider **names in priority order** (the
  `<n>,` prefix of `pKIDefaultCSPs` removed – LDAP keeps no value order; values without a number after the numbered ones).
  `msPKI-RA-Application-Policies` is parsed by content, not by schema version (a value with a backquote is the triple
  list): every `msPKI-RA-Application-Policies` triple – and every comma-separated OID inside one – is collected, every other
  name goes to `CryptoSettings` (first value wins, case-insensitive keys), an incomplete trailing triple is ignored; the
  parser is public as `CertificateTemplate.ParseRaApplicationPolicies(IEnumerable<string>)`. New computed members (no new
  init property): `Mask(PkiFlagAttribute)`, `DecodeFlags(PkiFlagAttribute)`, `MinimumCaVersion`, `MinimumClientVersion`.
* *Watchdog.* `FlagNamesCertificateGoldenTests` (82 cases: 2 × 32 single bits, the mixed values, `Diff`, bits above 32, no
  other template attribute is a flag attribute) was committed before the delegation; `FlagNames` builds its two certificate
  tables with `PkiFlags.Select` and today's bit lists (plus `using AdMng.Core.Pki;`), `TableOf` unchanged; both Watchdog
  test projects pass unchanged.
* *Tests.* `PkiFlagsTests`, `PkiPeriodTests`, `CertificateTemplateTests` (sample templates and hand-built objects) and one
  `[Fact(Skip = "P1 wave 1 merge")]` end-to-end test on C1's `PkiOverviewReader.ReadAsync`
  (`CertificateTemplateTests.TheOverviewReader_FillsEveryTemplateMember`).

**C3 as built.** Bodies of `TemplateRights.cs` / `TemplateRightsEvaluator.cs`, `AclObjectTypes.ReadAsync` + `Fallbacks`,
and the PKI pass of `DirectoryAclGrantSource.cs` at the 2.12 anchors (plus the internal `AclTarget.PkiKind` and the
private PKI methods of `DirectoryAclScan`; `RunAsync` now calls `AclObjectTypes.ReadAsync` – the moved Extended-Rights
read, its private copy removed, behaviour-neutral). No public member renamed or removed.
* *Additions (init only).* `TemplateAcl.HasOwnerRights`; `PkiEnrollmentExpansion.Classifier` (the pass's classifier with
  the Tier 0 accounts it found – C4's reader classifies the grant trustees with it, `expansion.Classifier ?? new
  PrincipalClassifier(…)`, so a user named directly in an ACE who is a Domain Admin is Tier 0) and
  `PkiEnrollmentExpansion.DistinctTokens`; `PrincipalClassifier.Tier0Accounts`.
* *GUIDs.* The schemaIDGUIDs of the nine sensitive attributes, of `altSecurityIdentities` (`00fbf30c-91fe-11d1-aebc-
  0000f80367c1`) and of the class `pKICertificateTemplate` (`e5209ca2-3bba-11d2-90cc-00c04fd91ab1`) are in `Fallbacks`,
  checked against the Windows Server schema LDF (MS-ADA1/2, MS-ADSC) and pinned by `TemplateRightsEvaluatorTests`.
  **Correction:** `altSecurityIdentities` is in the **Public-Information** property set (`attributeSecurityGUID`
  `e48d0154-bcf8-11d1-8702-00c04fb96050`), so a write of that set writes it: the set and its member are in the fallbacks,
  and `WriteAltSecurityIdentities` is also reported for it (ESC14). None of the nine template attributes is in a set.
  The template class must be a fallback: without a readable schema every ACE inherited for templates would be skipped.
* *Corrections to the evaluator rules of 2.3 (AccessCheck as the DC does it).* (1) An object type equal to the template
  class covers the whole object (the root of the object type list): write property → `WriteAllProperties`, control access →
  Enroll + AutoEnroll; U4b likewise treats a write with the object's own class as `WriteAllProperties` on PKI targets.
  (2) WRITE_DAC / WRITE_OWNER of an object ACE: an allow counts whatever the object type (as U4b), a deny only without
  one – conservative both ways. (3) `FullControl` is one ACE with the full mask and no object type; a deny removing any
  part of it denies it; a full-control trustee is listed under FullControl, Enroll, AutoEnroll, WriteDacl, WriteOwner and
  WriteAllProperties, not under the attribute rights (they name attributes). (4) `WriteSensitiveProperty` /
  `WriteOtherProperty` are decided **per attribute** (the first ACE covering that attribute – that right on it, or a write
  of all properties – decides), so a deny on one attribute does not hide a write of another; one trustee row per attribute.
  A write-property ACE whose object type is a class or an extended right reaches no property. (5) An `InheritedObjectType`
  nothing can name is kept (conservative), a known other class skipped. (6) Callback (conditional) deny ACEs are not
  honoured (their condition cannot be evaluated), callback allows count. (7) A descriptor without a DACL
  (`SE_DACL_PRESENT` clear or no offset – not returned, or a NULL DACL) is not evaluated. (8) OWNER RIGHTS: an effective
  `S-1-3-4` ACE sets `HasOwnerRights` (the `Owner` right is then not held) and a token with the owner SID holds `S-1-3-4`;
  its rows go to the owner; CREATOR OWNER / GROUP (and the server forms) and PRINCIPAL SELF are never trustees.
* *Tokens.* Also NETWORK (`S-1-5-2`): the CA (enrollment) and the DC (template writes) see a network logon, so an ACE for
  Network is held by every account. `S-1-5-9` only for writable DCs (`SERVER_TRUST_ACCOUNT`) – an RODC's token carries
  Enterprise Read-only DCs (498) instead. The primary group is the account's own domain prefix + `primaryGroupID`.
  `sIDHistory` is not read (the `AccountAttributes` list is unchanged).
* *Trustee level and expansion.* Rows per right in enum order, trustees in DACL order, the owner last; `Name` = the graph
  node's name, after the pass also an account's. `IsResolved = false` for a SID of another domain (prefix ≠ the window's
  domain SID), counts -1; one `TrusteeUnresolved` notice per template, only when such a trustee is not Tier 0 (from a child
  window the root's Domain/Enterprise Admins are on every template – their count never changes a finding). Totals: one per
  `TemplateRight` for an evaluated template (none for an unevaluated one); `HighestClass` = the most dangerous class among
  that right's rows with an account or unresolved. `FirstAccounts`: in stream order, a disabled account gives way to a
  later enabled one, enabled listed first (trustee rows too). Progress every 500 accounts and once at the end. Measured:
  100 000 generated accounts in about 1 s (test budget 30 s).
* *Classifier.* The Tier 0 groups of the graph are the closure downward from the groups Tier 0 by the SID / RID rules or
  the catalogue (`options.LevelOf`; the fixed SIDs and RIDs of decision 4 cannot be lowered), computed once by the
  classifier itself – `GroupGraph.GrantedLevel` is not used, its cache is keyed to the U4 evaluator's level function.
  Broad wins over the graph (Domain Users nested in a Tier 0 group stays Broad). Accounts are Tier 0 when any SID of their
  token is (account RID 500, a CA computer, a Tier 0 group, `S-1-5-9`).
* *U4b PKI pass.* After the GPO block, before the Tier 0 objects (a CA on a DC is read as a CA computer). The CA computers
  of the window's domain – by the services' `dNSHostName` (or `sAMAccountName` = label$) in `RunAsync`, by DN in
  `CollectPublicKeyAsync` – are Tier 0 account targets, so U4b now also reports delegations on the CA computer objects.
  **Correction:** "in the window's domain" is the exact domain part of the host (`srv-ca03.oddzial.contoso.test` is a child
  domain's, not `contoso.test`'s) and, for a DN, the window's own DC components – 2.1's "`DomainDnsName` suffix" would put
  a child domain's CA in the parent's window (C1: use the exact match too). Provisioning trustees: the root's Domain Admins
  (any domain's 512 while the root is unknown) on every PKI object; Cert Publishers (517 of any domain) on AIA, CDP,
  Certification Authorities, NTAuth, KRA, Enrollment Services and below; each CA computer on its enrollment service, the
  CDP host container named by its host label and the CRLs below it, and the `certificationAuthority` objects (AIA,
  Certification Authorities – not NTAuth) holding one of its `cACertificate` values (byte-equal). Control access without
  an object type is not reported on PKI objects (the only extended rights there are Enroll / AutoEnroll – enrollment is
  the evaluator's business, not control). Owners: `container` objects, templates, enrollment services, NTAuth, OID objects
  with a group link; KRA agents are `PkiAclTargetKind.Container` in the typed output (no kind of their own) but their
  owner is not reported. Unknown attribute GUIDs on a template → `WriteTemplateProperty` with the GUID as detail.
* *`CollectPublicKeyAsync`.* PKI pass with the given CA computers + AdminSDHolder as `Tier0Account` (SDProp stamps its DACL
  on every protected account) + one search of the Tier 0 accounts: DCs, Administrator, krbtgt and the direct `memberOf`
  members of every graph group the classifier calls Tier 0 (the closure, so nested members are included) – wider than
  `Tier0Filter`, whose groups are no ESC14 targets. Output ordered by DN, SID, right, detail.
* *Limits.* From a child-domain window the CA computers (root domain) stay unresolved (2.1), so their own ACEs on the CA
  objects are reported (U4b) and are *Other* for C4 – the known limitation of 2.1. The Samba-lab `AclDelegationTests` was
  not run here (no lab); Samba's PKI containers carry only class defaults and inherited configuration ACEs (default
  trustees), so it should not change.
* *Tests.* `tests/AdMng.Core.Tests/Pki/{TemplateRightsEvaluatorTests,PrincipalClassifierTests,TemplateExpansionTests}.cs`,
  `tests/AdMng.Core.Tests/PrivilegedAccess/DirectoryAclPublicKeyTests.cs`; `DirectoryAclGrantSourceTests` unchanged. The
  trustee level of `AddPublicKeySample` runs now (identity part of `CertificateTemplate.Of`); the expansion of the sample
  (`TheExpansionOfTheSample_…`) needs C4's `AltSecurityIdentity.Parse` and is `[Fact(Skip = "P1 wave 1 merge")]`.
* *C3 review.* `CN=OID` is itself an `msPKI-Enterprise-Oid` object in AD (the sample seeds a `container`): it is typed
  `PkiAclTargetKind.Container` and its owner is reported (its owner controls every OID object – ESC5/ESC13); the other
  OID objects keep `Oid` and the group-link rule. The Tier 0 accounts of `CollectPublicKeyAsync` also include the
  accounts whose `primaryGroupID` is a Tier 0 group of the window's domain (primary group membership is not in
  `memberOf`).

**C4 as built.** The parsers, the health rules, the ESC reader and analyzer of 2.4; no public member renamed or removed.
* *Additions* (init members): `AltSecurityIdentity.Issuer` / `Subject` (as stored, reversed RDN order), `SerialNumber`
  (the `<SR>` serial in the certificate's byte order, upper-case hex as `CertificateSummary.SerialNumber` – W7/W6 match a
  mapping to a published certificate with it) and `Identifier` (SKI / SHA1-PUKEY hex, RFC 822 address, Kerberos
  principal); `KeyCredentialLink.KeyMaterialLength` (the only thing exposed about the key), `IsKeyHashValid` (KeyHash =
  SHA-256 of the entries after it; null without a KeyHash) and `OwnerDn` (the DN part of the DN-Binary value);
  `PkiEscSnapshot.NotEvaluated` (the rule groups whose data could not be read) and `TrusteeNames` (SID → name of the grant
  trustees). Internal: `PkiEscAnalyzer.Lower`, `PkiEscAnalyzer.Eku` (the four EKU predicates of 2.2 – `PkiFlags.Is*` are C2
  stubs in wave 1, so the analyzer carries the same four lines; `PkiEscAnalyzerTests.TheEkuPredicates_AreThoseOfPkiFlags`,
  skipped until the merge, pins that they agree; the lead may then call `PkiFlags` directly), `PkiHealth.IsHttp` /
  `CertificateState` / `CrlState`.
* *`KeyCredentialLink`.* Only version `0x0200` parses (MS-ADTS 2.2.20; another version, a truncated entry, a DeviceId that
  is not 16 bytes, a time that is not 8 bytes or an empty KeyID → `IsMalformed`, one row per value). KeyID is Base64 (as
  DSInternals shows version-2 ids); times are FILETIME for `KEY_SOURCE_AD` and `DateTime.ToBinary` for `KEY_SOURCE_AZURE`
  (as DSInternals reads version 2); KeyUsage as a single byte (a legacy string usage → `Unknown`); entry 0x07
  CustomKeyInformation and unknown identifiers are skipped.
* *`AltSecurityIdentity`.* `X509:`, `Kerberos:` and the tags match case-insensitively; a backslash-escaped `<` stays in a
  value; `<SR>` and `<SKI>` must be even-length hex, `<SHA1-PUKEY>` exactly 40 hex digits (a hash of another length can
  never match: `Invalid`); tags in another order (`<S>…<I>…`), three parts, an issuer alone and empty parts are `Invalid`.
* *Health – refinements of 2.4.* A certificate is `Expiring` when **at most** the threshold is left (30 d exactly is
  expiring), a CRL when **less than** `max(1 d, 20 %)` is left (exactly the margin is OK); a CRL before `ThisUpdate` is
  `Unknown`. The CA row is the newest `cACertificate` only (renewals keep their URLs in `HttpUrls`). An `ldap:///` location
  outside `CN=Public Key Services` is `Unknown` (the snapshot never read it – *not found* would be a false alarm); inside it,
  a missing object is `Unavailable`, and so is an object that holds no certificate of the issuer (subject = issuer DN, SKI =
  AKI when both are present) or no CRL of the certificate's authority key; the CRL attribute of the URL
  (`certificateRevocationList` / `deltaRevocationList`) picks base or delta; URLs are percent-decoded (AD CS writes `%20`).
  An HTTP result counts only for the same URL **and** kind and is checked the same way (a delta CRL at a CDP URL, a base
  CRL at a delta URL or another CA's certificate → `Unavailable`; `NotChecked` → `Unknown`; every failure outcome →
  `Unavailable`). A CA without a `CN=CDP` list gets one unavailable base row; a delta row exists when a delta was read or
  the base CRL names a Freshest CRL, and each Freshest CRL URL of the CA's own base CRL (where AD CS writes the delta
  locations; `HttpUrls` lists them) gets a `DeltaCrlLocation` row after that list's rows, numbered on from the
  certificate's own Freshest CRL rows and checked against the base CRL's authority key; a missing NTAuth object is one unavailable row only when the forest has an
  enterprise CA (an empty snapshot has no rows). `Index` counts from 0 per item (per CA for CA rows). OCSP gets no row (as
  pkiview); `HttpUrls` lists it for the probe.
* *Analyzer – rules as built.* `EkuSourcesDiffer` = the rule's predicate holds on exactly one of `EffectiveEku` /
  `pKIExtendedKeyUsage` (sets that differ only in OIDs the rule ignores carry no note). The adjustments run approval first,
  then `NotPublished`, never below Info; `NotPublished` applies to every template finding, ESC4 too (a rewritten template
  still has to be published). ESC3: the agent's `RelatedObjectIds` are its CAs and the **published** targets (condition 2,
  and schema 1 + client authentication + enrollable + no manager approval); the target's severity looks only at agent
  findings on **published** templates. ESC4: one finding per template; a trustee's FullControl hides the WriteDacl /
  WriteOwner / WriteAllProperties it implies; the severity is the highest of its (trustee, right) pairs. ESC5 counts
  GenericAll, Owner, WriteDacl, WriteOwner, WriteAllProperties, CreateChild, every named-attribute write of `AclRight`
  (member, gPLink, GPO property, template property, key credentials, RBCD, altSecurityIdentities), ResetPassword,
  ReadLapsPassword, ActOnBehalfOfOtherIdentity and – on a CA computer only – AllExtendedRights; DeleteChild and the
  replication rights are not ESC5; one finding per object (DNs compared parsed), the enrollment service / OID object
  named and identified from the snapshot, a CA computer related to its CAs. ESC13 and ESC15 take the table's severity
  whatever the enrolling class (the group's rights / the CA patch decide). ESC14 arguments: Tier 0 mapping – name, weak
  kinds, empty; aggregated – the account count, the weak kinds found, empty (`ObjectDn` null); write – name, rights,
  principals. The account-count argument of the enrollment codes is the sum of the enrolling principals' counts capped by
  the template's Enroll total, `N+` when a principal's count is unknown (another domain), `?` when none is known. A grant
  trustee the reader did not classify counts as Other (no false negative). A template without evaluated rights gives no
  finding and a `SecurityDescriptorUnreadable` notice (added when the snapshot had none); `BaseAccessDenied` makes the
  result not evaluated with Enrollment and AccessControl in `NotEvaluated` (the weak mappings of the account pass are still
  reported). Findings: severity descending, then catalogue order, then the first argument, then the DN.
* *Reader.* `CollectPublicKeyAsync` is skipped for a snapshot with `BaseNotFound` / `BaseAccessDenied`; a
  `DirectoryException` of it puts AccessControl into `NotEvaluated` (ESC5 and the ESC14 writes skipped; ESC4 still runs on
  the template rights). Grant trustees are classified with one `PrincipalClassifier` (context graph and options, the
  snapshot's CA computer SIDs) and named from the graph, then by one `objectSid` OR search per 50 SIDs of the default NC
  (foreign security principals stay unnamed); an other-domain trustee without a name adds `TrusteeUnresolved` (the grant's
  target DN). A linked group's level is `GroupGraph.GrantedLevel` over the catalogue levels, Tier 0 also when the
  classifier says so. The rights are `expansion.Templates` by cn in snapshot order. The snapshot notices the review keeps:
  base not found / denied, descriptor unreadable, CA computer unresolved / in another domain, trustee unresolved.
* *Texts.* Resx unchanged: the ESC14 message stays generic (one key per code, three variants) – W6 can tell the variants by
  `ObjectDn == null` (aggregated) and `Principals.Count > 0` (write) if it wants per-variant wording in its own block.
* *Checked, no change.* KB5014754 forms and the reversed `<SR>`; MS-ADTS 2.2.20 entry identifiers 0x01–0x09, KeyUsage
  0x01/0x07/0x08 (+ DSInternals' 0x00/0x02/0x03/0x04/0x09) and KeySource 0x00/0x01; the EKU OIDs and predicates; the ESC
  conditions against Certipy 3ae3442 / Locksmith 8b555bd as the table states. For `docs/CERTYFIKATY.md`: Locksmith's
  broader ESC15 (every enrollable schema-1 template) reflects that on an unpatched CA the requester of any schema-1 template
  can also inject Certificate Request Agent (EKUwu → ESC3); the review keeps Certipy's ESS condition so the default *User*
  template stays quiet. `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT_ALT_NAME` (0x10000) alone is not treated as ESC1 (as Certipy
  and Locksmith) – an open question for P3/P4 against MS-WCCE.
* *Tests.* `AltSecurityIdentityTests`, `KeyCredentialLinkTests`, `PkiHealthTests`, `PkiEscAnalyzerTests` (hand-built
  snapshots; the reader on a snapshot without PKI and its cancellation). Skipped until the merge: `PkiHealthTests.
  TheSample_ReadByTheOverviewReader_IsHealthyWhereItShouldBe` (C1's reader and parsers – note the sample's issuing delta
  CRL is *Expiring* at the fake clock: 12 h of a 1-day margin), `PkiEscAnalyzerTests.TheEkuPredicates_AreThoseOfPkiFlags`
  (C2) and `TheSample_ReadEndToEnd_FlagsTheVulnerableTemplates_AndNothingElse` (C1–C3: overview, `AclObjectTypes.ReadAsync`,
  the classifier, `ExpandAsync`, `CollectPublicKeyAsync`).

**W5 as built.** The node, the snapshot service, the HTTP check, the settings section, the overview and templates
documents, the shell partial and the App views of 2.5; no public member of 2.11 renamed or removed.
* *Snapshot service.* One `Slot` per item (snapshot, expansion, DCs, DC checks, HTTP results); a build is shared by its
  callers, each caller's progress sink is fanned out, the last waiter that cancels cancels the build, `refresh` joins a
  running build. The expansion, the DC checks and the HTTP results are *derived*: a refreshed snapshot drops them and
  cancels their builds, and a derived build stores its value only when its snapshot is still the current one. The key is
  the session instance and its server (a reconnect to the same server is a new instance). `ReadAsync` gets the window clock
  and the settings' page size; the expansion builds its options like the U4 review (domain and root SID, the
  `AccountReview` level overrides), the CA computers' SIDs Tier 0. `Targets` (internal): the configuration partition's DCs
  (GC flag) plus the domain's DC objects it does not list, each host once, the current DC first, then by name; 3269 only
  for a GC. Without a probe `GetDcCertificatesAsync` and `CheckHttpAsync` return empty lists.
* *HTTP check.* `PkiHealthProbe` as 2.5; also no cookies, no pre-authentication, no decompression, no user info in a URL
  (`http://user:pass@…` is `NotChecked`), `Timeout` / `MaxBodyBytes` are init properties for the tests. A declared
  `Content-Length` over the cap is `TooLarge` before the body is read. AIA bodies: DER, PEM, else a PKCS #7 bundle (DER or
  PEM `PKCS7`) unwrapped in the probe (C1's note) – its first CA certificate; CDP bodies: DER or PEM CRL. More than 3
  redirects is `HttpError` with the redirect's status.
* *Node and shell.* `PkiNodeViewModel` (abstract), `PublicKeyServicesNodeViewModel` (opens the overview) with the static
  children `PkiDocumentNodeViewModel` (templates, security review, readiness). Five descriptors: the four `pki.*` and
  `view.pkiNodes` (the *Widok* toggle, `ShellViewModel.IsPkiNodesShown`, `TogglePkiNodesCommand`). **Addition:**
  `IPkiRequestTarget.Apply(PkiDocumentRequest)` (in `PkiViewModelFactory.cs`): an open document a request names is pointed
  at its `TemplateCn` / `CaDn` (W6 can implement it for `FindingCode`); a new document gets the request in its constructor.
  Selecting the security or readiness node needs W6's factory bodies (they still throw in this worktree).
* *Overview.* Tabs `PkiOverviewTab` (`SelectedTabIndex` for the view). "Re-evaluated on activation" is the *Zdrowie* tab
  being selected (`IShellDocument` has no activation hook), plus F5 and a settings change; the CA, trust-store, CRL and KRA
  rows get their state the same way (`PkiDisplay`, the rules of `PkiHealth`'s internal `CertificateState` / `CrlState`
  repeated – the lead may make those public), the LDAPS verdicts use the end-entity threshold (`DaysLeft`) at display time.
  The CA path is walked over the snapshot's CA certificates (subject = issuer, SKI = AKI when both are there; nothing
  downloaded). The NTAuth warning names the published templates whose `EffectiveEku` is client authentication. The LDAPS
  tab probes on its first activation (also when it was opened during the read), on its button and on F5; without a probe
  it lists the DCs with the KDC column only. When another PKI document refreshes the window's cache the open documents
  reload from it (`Changed`).
* *Templates.* The trustee level is computed in the document: `AclObjectTypes.ReadAsync`, a `PrincipalClassifier` without
  a graph (SID / RID rules), `TemplateRightsEvaluator.TrusteeLevel`, the trustees named by one `objectSid` OR search per 50
  SIDs (well-known names from `TrusteeNames.WellKnown`) – which also gives a group's DN for *Otwórz*; then the counting runs
  inside the load (the rows are on screen, the status says *Liczenie kont…*) and the expansion's rights replace the trustee
  level (classes with the graph, counts, first accounts). A failed count leaves the trustee level and says why. A group
  opens its property sheet (`IDialogService.ShowPropertiesAsync` has no page argument, so not directly on *Członkowie*).
  Rights shown: *Kto może się zarejestrować* = Enroll / AutoEnroll, *Kto może zmienić* = the other seven; totals of the
  modify rights nobody holds are left out. Periods use `PkiTpl.Unit.<unit>.One|Few|Many` (Polish plural forms). The
  compare pane takes the two rows selected in the list (`SetSelection`, from the view) or the selected one and a template
  picked in the pane. CSV / HTML export the shown (filtered) rows; *Kopiuj szczegóły* copies the selected template.
* *Strings.* `L.Pki` (with the W5 dynamic families `Pki.HealthItem.*`, `Pki.Http.*` and the new `Pki.UrlKind.*`) and
  `L.PkiTemplates` (`PkiTpl.Flag.*`, `PkiTpl.FlagGroup.*`, `PkiTpl.Field.*`, `PkiTpl.Unit.*`), both with `AllDynamicKeys()`,
  only between the two W5 marker pairs.
* *Shared files beyond the anchors.* `PkiHarness.CreateShell` gives the tree `pki: Factory` and the contract test
  `TheShell_IsUnchanged_UntilW5FillsThePartial` became `TheShell_ListsThePkiDescriptors_AndOpensNothingByItself` (both
  announced by the contract commit). `HostTests` at the W5 entry also checks that the window's `PkiServices.Snapshot` and
  the tree's `Pki` are the window's.
* *Tests.* `tests/AdMng.Presentation.Tests/Pki/{PkiSnapshotServiceTests,PkiHealthProbeTests,PkiSettingsTests,
  PkiTreeAndShellTests,PkiOverviewTests,PkiTemplatesTests}.cs` (70 tests, on the real cache and on `DirectPkiSnapshotService`);
  `tests/AdMng.App.UiTests/Pki/PkiOverviewUiTests.cs` (node, menus, every overview tab, templates with the compare pane, the
  settings section) and `ScreenshotTestsPki.cs` (36 health tab, 37 templates with `Użytkownik-VPN-Podatny`) – Windows CI only.
* *For the integration step.* `docs/CERTYFIKATY.md`: the HTTP check only on *Sprawdź adresy HTTP*, the LDAPS probe on the
  tab's first activation / its button / F5, the thresholds in *Ustawienia → Ogólne → Certyfikaty*, the tree toggle, the
  compare pane, the exports' privacy footer. Every tree screenshot changes (the node is on by default).
* *W5 review fixes.* The compare pane rebuilds its picker from the current rows when a reload (F5, a language switch, a
  newer snapshot) replaced them – the picker's selection was a row it no longer listed; the shown template's trustees say
  *liczenie…* from the start of the account pass (the template a request selected was built before it) and stop saying it
  after a failed count. Screenshots 36/37 use the language of `ADMNG_SCREENSHOTS_LANGUAGE` and the sample's clock
  (`PkiSecurityUiSupport.Configure`, as 38/39), so the CRLs of the sample are not shown expired. Texts: the English overview
  summary and the Polish *dni od wygaśnięcia* avoid wrong plural forms, *Notes from the read*, the broad-group note.

**W6 as built.** The security review and the readiness report of 2.5 with their views, texts, smoke render and tests; no
public member of 2.1–2.6 renamed or removed; outside the W6 files only the two `DataTemplate`s under
`<!-- P1 PKI security (W6) -->` in `MainWindow.xaml` (anchor kept) and the two W6 resx blocks.
* *Additions (Presentation).* `PkiFindingRowViewModel` + `PkiFindingHeadings` (the row and the headings of *Kopiuj* / HTML),
  `PkiSeverityChipViewModel` (in `.Filters.cs`), internal `PkiFindingRows` (in `.Rows.cs`: texts, grouping, navigation
  targets); `PkiSecurityReviewDocumentViewModel` with `ShowCode(code)`, `Result`, `TotalCount`, `HasResult`, `ListNote`,
  `OpenObjectText`, `DetailsText(row)`, `CancelCommand`, `ClearFiltersCommand`, `FocusSearch` (`ISearchableDocument`).
  `PkiReadinessStatus` (`Ok`, `Warning`, `Problem`, `NotChecked` – the dynamic family `PkiReady.Status.<status>`,
  `PkiReadinessDocumentViewModel.AllKeys()`, tested), `PkiReadinessArea`, `PkiReadinessRowViewModel`,
  `PkiReadinessGroupViewModel` (in `.Rows.cs`), internal `PkiReadinessRows` (in `.Builder.cs`, pure);
  `PkiReadinessDocumentViewModel` with `CheckLdapsCommand`, `IsCheckingLdaps`, `IsBusy`, `RowsShown` = 100, `ReportText()`,
  `CopyCommand`, `ExportCsvCommand`, `Summary` / `Rank` / `StatusKey`. Key added beyond the first draft: `PkiEsc.NoPki`.
* *Review.* `LoadCoreAsync` (every load: opening, F5 with `IsRefresh`, a session change) takes the snapshot and the
  expansion from the service, then `Task.Run(PkiEscReader.ReadAsync + PkiEscAnalyzer.Analyze)` with the default
  `PkiEscReadOptions` (decision 3); progress per stage (`PkiEsc.Stage*`); *Anuluj* keeps the rows shown (status
  *Sprawdzanie anulowano.*); a language switch rebuilds the texts from the shown result without reading. Rows are grouped
  by `GroupText` (*Obszar: obiekt*): groups by their most severe finding, then by name; inside a group severity, catalogue
  order, analyzer order. Principals named by SID only are given the localized well-known names (the K4
  `SecuritySettingsText` lists + *Ta organizacja*); a count `?` reads *nieznana liczba*; ESC14 has its three variants in
  the W6 block (`PkiEsc.Esc14.*`: Tier 0 mapping, aggregated – object *Konta spoza Tier 0 (N)* –, write). Filters: the
  chips mirror the severity box; ESC = the distinct `PkiFindingDefinition.Esc` values (so `FindingCode` /
  `ShowCode(EnrollmentAgentTargetTemplate)` shows every ESC3 row); template = the finding's own **or a related** template
  (ESC3 agents list their targets, e.g. *Użytkownik*); the search uses the **invariant** collation with `IgnoreNonSpace`
  (the Polish one keeps ś apart from s; K6 uses `CurrentCulture`). Navigation: *Otwórz szablon* →
  `Pki.RequestDocument(Templates { TemplateCn })`; *Otwórz urząd* → `Overview { CaDn }` (the finding's own CA, the CA of an
  ESC5 CA computer, else the first related CA); *Otwórz konto / komputer / obiekt* → `Ui.Dialogs.ShowPropertiesAsync`
  (ESC14 accounts, ESC5 objects and CA computers; an ESC5 finding on an enrollment service opens the urząd instead).
  Notes bar: the scope note always; one line per notice code with up to five object names; *Nie oceniono: …* for
  `NotEvaluated` (C4: ESC4 is still evaluated without AccessControl – the group text names ESC5 and the ESC14 writes only).
  `ListNote`: no match / *Nie można ocenić PKI* (not evaluated, nothing to show) / *W tym lesie nie ma AD CS*
  (`BaseNotFound`) / none found. Exports of the **filtered** rows: CSV (`protectFormulas`, columns severity, ESC, area,
  object, title, condition, attack path, what to do, principals, notes, related, Certipy, Locksmith, MDI, `Code` – the last
  unlocalized as in K6), HTML with CSP `default-src 'none'; style-src 'unsafe-inline'`, every value through `E()`, the
  filter line and the privacy footer.
* *Readiness.* Snapshot rows first, then the DCs (`GetDomainControllersAsync`), then the expansion; a failure of either
  later read is a *nie sprawdzono* row with the translated error (a snapshot failure is `Error`). Rules: a weak mapping on
  an enabled Tier 0 account → problem, on another account → warning, only invalid values → warning, `NotCertificate`
  (Kerberos) ignored, the strong-only accounts one OK row; published templates naming client authentication in
  `EffectiveEku` or `pKIExtendedKeyUsage` (an empty EKU is ESC2's business): `NoSecurityExtension` → problem,
  `EnrolleeSuppliesSubject` → warning, the rest one OK row; every enterprise CA: in NTAuth OK, not in NTAuth and publishing
  a logon template problem (with the templates), else OK; LDAPS: name / server-auth / validity problems, expiry within the
  end-entity threshold, no AD-root chain or no domain name warnings, 636 closed or handshake failed problem, **3269 closed
  only a warning**, timeout / unresolved warning; KDC (`KdcCertificateEvaluation`, C1's *brak EKU KDC* wording for
  `NoKdcEku` → warning), `Ready` within the threshold → warning, issuer outside NTAuth / expired → problem, no published
  certificate → *nie sprawdzono*, RODCs noted; the KDC events row is *nie sprawdzono* with *P5*. The thresholds and the
  clock apply when shown (`OnSettingsChanged` rebuilds). *Sprawdź certyfikaty LDAPS* passes `refresh: true` once checks are
  shown (a reload shows the service's cached checks without probing); a reload of another session or F5 cancels a running
  probe. An area shows 100 rows and *… i jeszcze N* (copy and CSV hold all); no HTML export (not asked for in 2.5).
* *App.* `PkiSecurityReviewView` (K6 layout; severity styles of its own keyed on `PkiFindingSeverity`; list grouped through
  `gp:GpoSettingsUi.GroupBy`/`GroupedItemsSource` with `IsVirtualizingWhenGrouping`; `gp:GroupPolicyUi.IsDocumentSearch`,
  `KeepsSelectionInView`, `gp:PaneSplitter` reused read-only) and `PkiReadinessView` (stage-S layout). `SmokeTest.PkiSecurity`
  opens both tabs through `ShellViewModel.OpenPkiDocument` in the disconnected window and checks they use the window's
  `PkiServices` and wait for a connection.
* *Tests.* `PkiSecurityReviewTests` (12; with `HookedPkiSnapshotService`, a decorator of `DirectPkiSnapshotService` that
  cancels, replaces the snapshot, fails the expansion or answers the DC checks) and `PkiReadinessTests` (12) on the real
  Core readers. `PkiSecurityUiTests` (2) and `ScreenshotTestsPkiSecurity` (38, 39) run the app on
  `ManualTimeProvider(PkiSamples.Now)` (UTC as the local zone) with their own `FakeDomainControllerTlsProbe`
  (`PkiSecurityUiSupport`); they – and the smoke render – need W5's `OpenPkiDocument` and `PkiSnapshotService` (the
  contract stubs throw), so they first run after the wave-2 merge on Windows CI.
* *For W5 / the merge.* When `OpenPkiDocument` selects an open review for a request with `FindingCode`, it should call
  `review.ShowCode(request.FindingCode)` (`Matches` keeps one review per window); the review and the readiness report
  read only through `IPkiSnapshotService` (`GetAsync`, `GetExpansionAsync`, `GetDomainControllersAsync`,
  `GetDcCertificatesAsync`, `DcCertificates`).

**W7 as built.** The three tabs, the attribute-editor mask and the TLS probe of 2.6; no member of 2.1–2.6 renamed or
removed, every addition is new W7 code.
* *Property tabs.* `PageIf` plus a `CertificatePages(bool mappings)` helper in `PropertyPageFactory` (each tab only when
  the schema defines its attribute – `userCertificate`, `altSecurityIdentities`, `msDS-KeyCredentialLink`); user and
  inetOrgPerson after *Organizacja*, computer after *Stan*, contact *Opublikowane certyfikaty* only, all before *Członek
  grupy* (tab-order tests for user, computer and the new contact test). Public row types:
  `PublishedCertificateRowViewModel`, `CertificateDetailViewModel`, `NameMappingRowViewModel`,
  `KeyCredentialRowViewModel`; the enum `CertificateValidity` (`Valid | Expiring | Expired | NotYetValid`, family
  `Cert.Validity.*` in `L.Certificates.AllKeys()`, *Wygasa* = at most `PkiSettings.CertificateExpiryWarningDays` left,
  as C4's health rule, at `Services.Time`). Every row's `ToString` prints states only.
* *Opublikowane certyfikaty.* Read lazily (`ActivateAsync`, re-read by the sheet's refresh once activated, own
  *Odśwież*), ranged values completed, parsed on `Task.Run` (internal `PublishedCertificates` + record
  `PublishedCertificate`): `userCertificate` first, then `userSMIMECertificate`; a PKCS #7 `SignedData` value (Outlook's
  S/MIME bundle) is unwrapped to its end-entity certificates (all of them when it holds only CA certificates); the
  one-byte `0x00` placeholder is no row; any other unreadable value is a row with the attribute only. Columns
  *Wystawiony dla* (CN, else the SAN UPN / e-mail), *Wystawiony przez*, przeznaczenie (`PkiFlags.KnownEku` names, else
  the OID; no EKU = *Wszystkie (brak EKU)*), ważny do, stan, szablon, odcisk (SHA-1), atrybut; the details pane lists
  subject, issuer, serial, validity, the EKUs with their OIDs, the template, SAN UPN / e-mail / DNS / SID URL, the SID
  extension, key algorithm and size, signature algorithm, SHA-1 and SHA-256. **Deviation:** the template name is not
  taken from the snapshot – the sheet's `FieldContext` has no `PkiServices` and the snapshot service is W5's (a full PKI
  read for one tab); one one-level search of `CN=Certificate Templates` on the window's DC (`msPKI-Cert-Template-OID` /
  `cn` OR filter, size limit 100; any `DirectoryException` → the name or OID the certificate carries) resolves the
  display name. *Kopiuj do pliku…* writes the DER to the `.cer` the operator picks (`Files.SaveFileAsync`; I/O errors as
  a dialog, success as a toast); *Kopiuj odcisk palca*.
* *Mapowania nazw.* `altSecurityIdentities` in `ReadAttributes` (the sheet's read; a refresh replaces the lists):
  `X509Mappings` (every value but `Kerberos:`, invalid ones included, in server order) with kind, strength, issuer and
  subject / serial (*Numer seryjny …*) / identifier, and `KerberosNames` apart; warnings with the weak count (KB5014754
  full enforcement) and the invalid count; one selection across both lists, *Kopiuj* copies the value as stored.
* *Poświadczenia kluczy.* Read lazily like the published certificates (`KeyCredentialLink.Parse` on `Task.Run`): key id
  (Base64), device id, source, usage, created, approximate last logon, remarks (*Nieczytelny wpis*, a KeyHash mismatch);
  a static Shadow Credentials note; *Kopiuj identyfikator klucza / urządzenia*. Never the key material.
* *Attribute-editor mask.* `AttributeRowViewModel.IsMasked` (= `SecretAttributes.IsNeverShown`): `ValueText` is the mask
  (*&lt;nie ustawiono&gt;* when empty), `EditableValues` is empty, `ReadOnlyReason` = `Cert.MaskedReadOnly` (*Wartość
  poufna (materiał klucza)…*, before the binary reason); `Edit` / `CopyValue` cannot execute (`HasSelection` became
  `CanEditOrCopy`), and `CloseEditor`, the restaging after a reload, `GetOperations` and `ComputeHasChanges` skip masked
  rows. No XAML change (the lock glyph shows the reason).
* *TLS probe.* As 2.6, plus: the constants `LdapsPort` / `GlobalCatalogLdapsPort` / `DefaultTimeout` (5 s; the caller
  passes the timeout, `Timeout.InfiniteTimeSpan` allowed, a non-positive one throws `ArgumentOutOfRangeException`); an
  internal `ConnectPort` (the tests' TLS server listens on an ephemeral port while the 636 / 3269 check stays);
  `ConnectOutcome`: `HostNotFound` / `NoData` / `TryAgain` → `NameNotResolved`, `TimedOut` / `HostUnreachable` /
  `NetworkUnreachable` / `HostDown` / `NetworkDown` → `Timeout`, any other connect error → `PortClosed`; a handshake
  that completes without a captured certificate, and `AuthenticationException` / `IOException` / `SocketException`
  (resets) → `HandshakeFailed`; an exception after the probe's own deadline fired (some platforms close the socket under
  the read) → `Timeout`. `AddAdMngPkiApp()` registers it (`AppHost` untouched – the contract commit already calls it).
* *App.* The three `PropertiesWindow` templates after the *Stan* template (`pk:` xmlns), named lists for the UI tests;
  `HostTests.SharedApp` lists `IDomainControllerTlsProbe`; `CertificateTabsUiTests` (both languages, PKI sample seeded
  by the test, the masked `userPKCS12` row, the registration). Not run on Linux – every `{l:Tr}` key of the window was
  checked against both resx files and every binding path against the view models by script.
* *Tests.* `CertificateTabsTests` (16), `AttributeEditorMaskTests` (7), the three tab-order tests in
  `PropertySheetTests`, `SslStreamTlsProbeTests` (28 + the lab probe of `localhost:636`, skipped without
  `ADMNG_TEST_LDAP_*`).
* *For the integration step.* The user and computer sheets have three more tabs, so the README property-sheet pictures
  (`ScreenshotTests.ShootUserPropertiesAsync`) change – check that the tab strip still fits. `docs/CERTYFIKATY.md`
  should describe the tabs, the mask and the probe; the Watchdog history on *Poświadczenia kluczy* stays P5. The
  operator question of 2.13 (mask LAPS / BitLocker recovery / confidential text too) is still open.
* *W7 review fixes.* The probe's handshake policy also sets `TrustMode = CustomRootTrust` with an empty custom store
  (decision 1; 2.6 left it out): the platform chain build never consults the system store, so Windows' automatic root
  update cannot fetch a root or the CTL for an untrusted (e.g. self-signed) DC certificate – `PolicyErrors` (informational,
  read by nobody) now always carries `RemoteCertificateChainErrors`; `Options` and `Capture` are internal for a test.
  *Wystawiony dla* of a certificate with an empty subject (the Domain Controller / Kerberos / Workstation Authentication
  templates put the host only in the SAN) falls back to the first SAN DNS name after the UPN and e-mail – it was an empty
  cell; *Kopiuj do pliku…* suggests the thumbprint as the file name when the certificate names nobody (it was the
  hard-coded Polish `certyfikat.cer`).
