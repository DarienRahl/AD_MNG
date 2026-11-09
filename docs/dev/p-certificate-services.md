# Track P – AD Certificate Services and certificates (design)

Product description (Polish): README, *Ścieżka P – Urząd certyfikacji (AD CS) i certyfikaty*. This note is the
implementation design of stage **P1** (read-only overview of the forest PKI, LDAP only) and is **binding** for the P1
implementers: the public types of 2.11 are the contract, each heading of 2.1–2.6 names its owner, and 2.12 fixes which
files each agent may touch. P2 (safe changes through the change plan) and P3 (work with the CA host) get their own
top-level sections later, the way the K note grew from §14 to §21; do not renumber 2.x after the P1 contract commit (XML
docs and `PkiFindingCodes` will quote these section numbers).

## 1. Decisions (operator, 2026-11)

The operator chose **"P1 – urząd certyfikacji"** on 2026-11-09 (README track P, *Etapy*: P1). P1 is read-only (CLAUDE.md
rule 5: nothing is written), LDAP only (rule 4: everything through the window session's DC). The lead's decisions:

1. **Scope** = README track P, stage P1 (read-only, LDAP). The only traffic outside the window's LDAP session:
   (a) a **TLS handshake probe** of a DC's LDAPS / GC-LDAPS certificate (port 636, and 3269 only for a DC with the GC
   flag) – no LDAP bind, no credential, no byte written, run only on explicit navigation or a button, at most 4 in
   parallel, 5 s timeout each; (b) **HTTP checks of the AIA / CDP / OCSP URLs** read from certificates, run **only** when
   the operator presses *Sprawdź adresy HTTP* – responses kept in memory, a size cap and a timeout, never fetched
   automatically. Chain building never downloads (`DisableCertificateDownloads = true`, `RevocationMode = NoCheck`,
   `TrustMode = CustomRootTrust` with the roots read from AD).
2. **Expiry thresholds** (settings, allowed on disk – no directory data): one setting *Próg ostrzeżenia o wygasaniu
   certyfikatów* (default **30 days**) for end-entity certificates (DC LDAPS, KRA, published certificates) and a separate
   default **180 days** for CA certificates (renewing a CA has to be planned). A CRL *wygasa* when less than
   `max(1 day, 20 % of its validity)` is left. Stored in `AppSettings` like the other UI settings.
3. **CA-enrollment assumption.** Until P3 reads the CA's own security, the ESC review assumes every CA grants *Enroll* to
   *Authenticated Users* (the Windows default) and every affected finding says so. A template **no enterprise CA
   publishes** is reported at a lower level with the note *szablon nie jest wystawiany* – exploitable only once it is
   published.
4. **Principal classes for ESC** (decides a finding's severity, decision shared with 2.3's rights evaluator):
   *Tier 0 / privileged* = `PrivilegedGroupCatalog` Tier 0 members, SYSTEM, Enterprise Domain Controllers (S-1-5-9),
   Domain Controllers, Administrators and the CA's own computer account → **no finding**. *Broad* = Everyone,
   Authenticated Users, Anonymous, Domain Users, Domain Computers, Domain Guests, Guests, BUILTIN\Users, This
   Organization (S-1-5-15), Interactive, Network → **High / Critical**. Any **other** principal → **Medium**
   (*konto lub grupa spoza Tier 0*) with the account count.
5. **"Kto może się zarejestrować / zmienić"** is a **separate pure Core evaluator** (not U4b): AccessCheck semantics over
   the template DACL (deny ACEs honoured, ACE order, inherit-only skipped, GENERIC_ALL normalized), rights *Enroll*,
   *AutoEnroll*, *FullControl*, *WriteDacl*, *WriteOwner*, *WriteProperty* (all properties or named template attributes),
   *Owner*. Trustee level first (immediate), concrete accounts through `GroupGraph` + **one** streamed account pass per
   analysis (counts + first N names, disabled accounts kept apart, cancellable, progress); trustees from other forest
   domains are reported unresolved.
6. **U4b** (`DirectoryAclGrantSource`) additionally reads the PKI containers of the configuration partition (sites still
   skipped) with provisioning trustees (Cert Publishers on AIA / CDP / Certification Authorities / NTAuth, the CA
   computer on its enrollment service and CDP objects) so a default Windows PKI yields no false findings; a **separate
   sample** keeps the existing exact-set tests unchanged.
7. **userPKCS12** goes into `SecretAttributes.WellKnownNames` **and** the attribute editor masks secret values in its
   `ValueText` and editors (README promises masking *wszędzie, także w edytorze atrybutów*).
8. **Shared Core.** Template flag tables, period decoding, certificate / CRL summaries, the `msDS-KeyCredentialLink` and
   `altSecurityIdentities` parsers, the rights evaluator and the ESC analyzer all live in `AdMng.Core/Pki` (no Windows
   types) so the Watchdog can reuse them (P4). The Watchdog's `FlagNames` decoder delegates to the Core flag tables in a
   **separate, behaviour-neutral** commit (its output is unchanged; `tests/AdMng.Watchdog.Core.Tests` and
   `tests/AdMng.Watchdog.Service.Tests` pass unchanged).
9. **UI.** A tree node *Usługi kluczy publicznych* under the domain node on its own base (a `PkiNodeViewModel`, not the
   GPO base; a *Widok* toggle like the GPO nodes); documents on an LDAP-only `PkiDocumentBase` (no SYSVOL, no PDC note);
   a **scoped per-window** `PkiSnapshotService` caching the PKI read and the enrollment expansion (dropped on session
   change, F5 rebuilds). Documents: **przegląd** (urzędy przedsiębiorstwa with the CA chain/validity/templates/CES,
   zaufane urzędy główne i pośrednie, NTAuth matched to enterprise CAs, CDP lists with CRL numbers/dates/counts, KRA, OID
   with group links, a *pkiview*-style health tab, a DC-LDAPS tab), **szablony** (the README columns, decoded flags as
   Polish/English sentences with the technical bit name in a tooltip, periods in days/weeks/years, issuing CAs,
   unpublished templates, expanded enrollment rights, who can modify, compare two templates, export on demand), **przegląd
   bezpieczeństwa AD CS** (K6-diagnostics-style: severity chips, filters, rows with the ESC number, Certipy / Locksmith /
   MDI ids, condition, attack path, affected object, principals with counts – no *Napraw* in P1, only *Otwórz
   szablon/urząd* navigation; export CSV/HTML on demand), **gotowość na silne mapowanie i NTAuth** (accounts with weak
   `altSecurityIdentities`, authentication templates without the SID extension, logon-capable CAs not in NTAuth, DC LDAPS
   certificates without a proper SAN/EKU; a link opening the event-log reader for KDC events 39/40/41/45/21 **if** the
   reader supports a preset query – otherwise listed as P4). Read-only property-sheet tabs *Opublikowane certyfikaty*
   (user, computer, contact), *Mapowania nazw* (user, computer) with KB5014754 strength and *Poświadczenia kluczy* (user,
   computer) with key id, device id, source, created / last-logon, usage – read lazily, no writes. From a CA to its
   computer object and to the K1 remote actions.
10. **Screenshots 36–39** (overview/health, templates with enrollment rights, security review, readiness/a card). The
    user doc `docs/CERTYFIKATY.md` (Polish) is written in the integration step.
11. **Tests.** A fake-directory sample `AddPublicKeySample` with Polish names (urzędy *Kontoso Główny CA*, *Kontoso
    Wystawiający CA*, szablony with diacritics, one deliberately vulnerable template per LDAP ESC and a safe look-alike
    that must **not** be flagged); certificates / CRLs generated in tests with fixed dates around the fake clock
    (2026-10-13); TLS-probe tests against an in-process TLS server on `localhost`; HTTP-fetch tests with a fake handler.
    Samba has no AD CS (lab: only the empty PKI containers + an LDAPS probe of the lab DC).

## 2. P1 – read-only overview

P1 reads the forest PKI once per window into an in-memory snapshot and shows it in four documents, a tree node and three
property-sheet tabs; a pure Core layer decodes the templates, parses the certificates and evaluates both the enrollment
rights (who can get a certificate / change a template) and the ESC findings. The snapshot is held by the scoped
`PkiSnapshotService` and the open documents only; nothing is written (rule 5) and nothing but an operator export touches
disk (rule 1). The four Core waves (C1–C4) produce the snapshot, the decoders, the rights evaluator and the ESC analyzer
against the contract of 2.11; the three Presentation/App waves (W5–W7) build the node, the documents, the property tabs,
the TLS probe implementation and CA-host navigation on the merged Core.

### 2.1 Core – the PKI overview reader and the TLS-probe contract (C1)

**`PkiOverviewReader.ReadAsync(session, options, progress, ct)`** (`src/AdMng.Core/Pki/PkiOverviewReader.cs`) runs one
paged subtree search of `CN=Public Key Services,CN=Services,<config>` (DN built with
`DistinguishedName.Parse(configDn).Child("CN","Services").Child("CN","Public Key Services")`, rule 3), filter
`LdapFilter.Or` over `PkiClasses.All` (`pKIEnrollmentService`, `pKICertificateTemplate`, `certificationAuthority`,
`cRLDistributionPoint`, `msPKI-Enterprise-Oid`, `msPKI-PrivateKeyRecoveryAgent`, `container`), attributes
`PkiAttributes.OverviewAttributes`, `ReadSecurityDescriptor = true`, `ExtendedDn = true`, `PageSize = 1000` – the exact
options `ConfigurationPartitionWatcher.PublicKeyInfrastructureAsync` already uses, so one DC answers the whole forest PKI
(rule 4). It catches `DirectoryException` of kind `NoSuchObject` or `AccessDenied` per container and records an
`PkiReadNotice` (*niedostępne*) instead of throwing, so an unreadable subtree becomes a visible "not evaluated" state (as
K6 does), never a crash. Multi-valued binary attributes (`cACertificate`, `crossCertificatePair`,
`certificateRevocationList`, `deltaRevocationList`, `authorityRevocationList`, `userCertificate` on KRA) are read with
`obj.TryGet(name, out var v) && v.OfType<byte[]>()` (never `GetAllValuesAsync`, which decodes UTF-8).

The result is the immutable **`PkiSnapshot`** record (every nested record has a value-free `ToString`, rule 1): the
enterprise CAs (`PkiEnrollmentService`: host from `dNSHostName`, the CA certificate chain from `cACertificate`, the
published template names from `certificateTemplates`, the CES addresses from `msPKI-Enrollment-Servers`), the trusted
root and intermediate CAs (`CN=Certification Authorities`, `CN=AIA` – `certificationAuthority`: `cACertificate`,
`crossCertificatePair`), the NTAuth store (`CN=NTAuthCertificates`: `cACertificate`, each matched to an enterprise CA),
the CDP lists (`cRLDistributionPoint`: `certificateRevocationList`, `deltaRevocationList`), the KRA agents
(`msPKI-PrivateKeyRecoveryAgent`: `userCertificate`), the OID objects (`msPKI-Enterprise-Oid`: `msPKI-Cert-Template-OID`,
`flags`, `msDS-OIDToGroupLink`) and the raw templates (handed to 2.2). It also carries the health rows (per CA: the CA
certificate, each AIA and CDP location, the base and delta CRL – `Ok` / `Wygasa` / `Wygasła` / `Niedostępna` by the
decision-2 thresholds against `options.Now`) and the read notices. `PkiOverviewReader` is pure except for the LDAP read;
the HTTP checks of AIA/CDP/OCSP are a separate, operator-triggered method (`PkiHealthProbe`, 2.5) that never runs during
the base read.

**TLS probe contract** (`src/AdMng.Core/Pki/Tls/`, no Windows types, implemented by W7 in `AdMng.Directory`):
`IDomainControllerTlsProbe.ProbeAsync(host, port, timeout, ct)` opens TCP to `host:port` (636 / 3269), does the TLS
handshake only – no LDAP bind, no credential, no data sent, the stream closed without a write – and returns
`TlsProbeResult(Host, Port, TlsProbeOutcome, SslProtocols?, IReadOnlyList<byte[]> PresentedChain /* DER, leaf first */,
SslPolicyErrors?)` whose `ToString() => $"{Port}: {Outcome}"` carries no values. `TlsProbeOutcome` is
`Ok | NameNotResolved | PortClosed | HandshakeFailed | Timeout | InvalidHost` (AD DS accepting TCP on 636 with no usable
certificate and resetting mid-handshake is `HandshakeFailed`, reported as *DC bez certyfikatu LDAPS*, not *unreachable*).
`DcCertificateEvaluation.Evaluate(chain, dcFqdn, domainDns, ntAuthRoots)` is pure: SAN/subject carry the DC FQDN and the
domain, EKU Server Authentication (1.3.6.1.5.2.3.5 KDC Authentication for the KDC readiness), validity against
`options.Now`, and "chains to an AD root" with `X509ChainPolicy.TrustMode = CustomRootTrust`, `CustomTrustStore` filled
from the snapshot's roots, `RevocationMode = NoCheck`, `DisableCertificateDownloads = true`. `HostNames.IsValid` /
`Normalize` (`AdMng.Core/Computers/Remote/HostNames.cs`) guard the probe target (IP addresses refused; SNI and name
checks need a name).

### 2.2 Core – certificate templates: model, flag tables and period decoders (C2)

**`CertificateTemplate.Of(DirectoryObject)`** (`src/AdMng.Core/Pki/CertificateTemplate.cs`, pure) builds the template
record from one `pKICertificateTemplate` object: `Cn`, `DisplayName`, `SchemaVersion` (`msPKI-Template-Schema-Version`;
1–4), `Revision` + `MinorRevision` (`revision`, `msPKI-Template-Minor-Revision`), `Oid` (`msPKI-Cert-Template-OID`), the
EKU set (`pKIExtendedKeyUsage`), the application policies (`msPKI-Certificate-Application-Policy`), `KeyUsage`
(`pKIKeyUsage`), the four flag masks (`msPKI-Certificate-Name-Flag`, `msPKI-Enrollment-Flag`, `msPKI-Private-Key-Flag`,
`flags` – read with `GetInt32` cast to `uint`, so the negative `msPKI-*` masks parse), `msPKI-RA-Signature` (required
authorized signatures) with `msPKI-RA-Application-Policies` and `msPKI-RA-Policies`, `msPKI-Certificate-Policy` (issuance
policies), the validity / renewal periods (`pKIExpirationPeriod`, `pKIOverlapPeriod`), `msPKI-Minimal-Key-Size`,
`pKIDefaultCSPs`, `msPKI-Supersede-Templates` and the security descriptor (passed to 2.3).

**`PkiFlags`** (`src/AdMng.Core/Pki/PkiFlags.cs`) holds the four flag tables as `(long Bit, string Name)[]` with **every**
`CT_FLAG_*` constant and the general `flags` bits, plus the EKU OID catalogue. The Watchdog's `FlagNames` delegates here
(decision 8). The tables, with their exact values:

* **`msPKI-Certificate-Name-Flag`**: `0x1` `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT`, `0x8`
  `CT_FLAG_OLD_CERT_SUPPLIES_SUBJECT_AND_ALT_NAME`, `0x10000` `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT_ALT_NAME`, `0x400000`
  `CT_FLAG_SUBJECT_ALT_REQUIRE_DOMAIN_DNS`, `0x800000` `CT_FLAG_SUBJECT_ALT_REQUIRE_SPN`, `0x1000000`
  `CT_FLAG_SUBJECT_ALT_REQUIRE_DIRECTORY_GUID`, `0x2000000` `CT_FLAG_SUBJECT_ALT_REQUIRE_UPN`, `0x4000000`
  `CT_FLAG_SUBJECT_ALT_REQUIRE_EMAIL`, `0x8000000` `CT_FLAG_SUBJECT_ALT_REQUIRE_DNS`, `0x10000000`
  `CT_FLAG_SUBJECT_REQUIRE_DNS_AS_CN`, `0x20000000` `CT_FLAG_SUBJECT_REQUIRE_EMAIL`, `0x40000000`
  `CT_FLAG_SUBJECT_REQUIRE_COMMON_NAME`, `0x80000000` `CT_FLAG_SUBJECT_REQUIRE_DIRECTORY_PATH`.
* **`msPKI-Enrollment-Flag`**: `0x1` `CT_FLAG_INCLUDE_SYMMETRIC_ALGORITHMS`, `0x2` `CT_FLAG_PEND_ALL_REQUESTS`
  (CA-manager approval), `0x4` `CT_FLAG_PUBLISH_TO_KRA_CONTAINER`, `0x8` `CT_FLAG_PUBLISH_TO_DS`, `0x10`
  `CT_FLAG_AUTO_ENROLLMENT_CHECK_USER_DS_CERTIFICATE`, `0x20` `CT_FLAG_AUTO_ENROLLMENT`, `0x40`
  `CT_FLAG_PREVIOUS_APPROVAL_VALIDATE_REENROLLMENT`, `0x100` `CT_FLAG_USER_INTERACTION_REQUIRED`, `0x400`
  `CT_FLAG_REMOVE_INVALID_CERTIFICATE_FROM_PERSONAL_STORE`, `0x800` `CT_FLAG_ALLOW_ENROLL_ON_BEHALF_OF`, `0x1000`
  `CT_FLAG_ADD_OCSP_NOCHECK`, `0x2000` `CT_FLAG_NOREVOCATIONINFOINISSUEDCERTS`, `0x4000`
  `CT_FLAG_INCLUDE_BASIC_CONSTRAINTS_FOR_EE_CERTS`, `0x8000` `CT_FLAG_ALLOW_PREVIOUS_APPROVAL_KEYBASEDRENEWAL_VALIDATE_REENROLLMENT`,
  `0x10000` `CT_FLAG_ISSUANCE_POLICIES_FROM_REQUEST`, `0x20000` `CT_FLAG_SKIP_AUTO_RENEWAL`, `0x40000`
  `CT_FLAG_NO_SECURITY_EXTENSION` (ESC9).
* **`msPKI-Private-Key-Flag`**: `0x1` `CT_FLAG_REQUIRE_PRIVATE_KEY_ARCHIVAL`, `0x10` `CT_FLAG_EXPORTABLE_KEY`, `0x20`
  `CT_FLAG_STRONG_KEY_PROTECTION_REQUIRED`, `0x40` `CT_FLAG_REQUIRE_ALTERNATE_SIGNATURE_ALGORITHM`, `0x80`
  `CT_FLAG_REQUIRE_SAME_KEY_RENEWAL`, `0x100` `CT_FLAG_USE_LEGACY_PROVIDER`, `0x200`
  `CT_FLAG_ATTEST_NONE` region markers, `0x400` `CT_FLAG_ATTEST_REQUIRED`, `0x800` `CT_FLAG_ATTEST_PREFERRED`, `0x1000`
  `CT_FLAG_ATTESTATION_WITHOUT_POLICY`, `0x00FF0000` the EK/attestation client-compatibility nibbles, `0x01000000`
  `CT_FLAG_HELLO_LOGON_KEY`; unknown bits as `0x…`.
* **general `flags`** (keyed by the template class, never by name – `groupPolicyContainer` also uses `flags`, decision 8):
  `0x20` `CT_FLAG_AUTO_ENROLLMENT` legacy, `0x40` `CT_FLAG_MACHINE_TYPE`, `0x80` `CT_FLAG_IS_CA`, `0x200`
  `CT_FLAG_ADD_TEMPLATE_NAME`, `0x800` `CT_FLAG_IS_CROSS_CA`, `0x10000` `CT_FLAG_IS_DEFAULT`, `0x20000`
  `CT_FLAG_IS_MODIFIED`, `0x400` `CT_FLAG_DONOTPERSISTINDB`, `0x4000` `CT_FLAG_ADD_EMAIL`, `0x8000`
  `CT_FLAG_PUBLISH_TO_DS` legacy, `0x2000` `CT_FLAG_EXPORTABLE_KEY` legacy.

**EKU OIDs** (`PkiFlags.KnownEku`): Client Authentication `1.3.6.1.5.5.7.3.2`, Server Authentication
`1.3.6.1.5.5.7.3.1`, Smart-Card Logon `1.3.6.1.4.1.311.20.2.2`, PKINIT Client Authentication `1.3.6.1.5.2.3.4`, KDC
Authentication `1.3.6.1.5.2.3.5`, Certificate Request Agent (Enrollment Agent) `1.3.6.1.4.1.311.20.2.1`, Any Purpose
`2.5.29.37.0`, and an empty EKU set (= Any Purpose for ESC2). The six "client-auth" OIDs (Client Auth, Smart-Card Logon,
PKINIT, Any Purpose, and the SubCA/Any via an empty EKU) drive the ESC1/ESC9/ESC13 "authentication" predicate.

**Periods** (`PkiPeriod.Decode(byte[])`, `src/AdMng.Core/Pki/PkiPeriod.cs`): `pKIExpirationPeriod` and `pKIOverlapPeriod`
are an 8-byte little-endian **negative** 100-ns interval; decode to a `TimeSpan`/`PkiPeriod` and render in days, weeks,
months or years (the UI picks the unit; no new converter in `AdMng.Core/ActiveDirectory` is added for display – the Core
type carries the ticks, the Presentation formats them).

### 2.3 Core – the enrollment-rights / template-modify evaluator and U4b PKI containers (C3)

This is the decision-5 evaluator – a **separate pure Core evaluator**, not U4b (U4b drops default trustees such as DA/EA
and evaluates no denies, so it is wrong for "who can enroll"). `src/AdMng.Core/Pki/TemplateRightsEvaluator.cs`:

**AccessCheck.** `TemplateRightsEvaluator.Evaluate(securityDescriptorBytes, types)` parses with
`SecurityDescriptorSummary.TryParse`, then per ACE that applies to the object (skip `IsInheritOnly`), with
`m = DirectoryAccessMask.Normalize(ace.Mask)`:
* **Enroll** = `(m & ControlAccess) != 0 && (ObjectType is null || IsRight(ot, "Certificate-Enrollment"))`; **AutoEnroll**
  the same with the AutoEnroll GUID. The two extended-right GUIDs are added to `AclObjectTypes` (`Certificate-Enrollment`
  `0e10c968-78fb-11d2-90d4-00c04f79dc55`, `Certificate-AutoEnrollment` `a05b8cc2-17bc-4802-a710-e7c15ab866a2`) and to
  `ExtendedRightIds` (for P2's `ModifySecurity`).
* **FullControl** = `ObjectType is null && (m & FullControl) == FullControl`; **WriteDacl**, **WriteOwner**.
* **WriteProperty** = `(m & WriteProperty) != 0`, with `ObjectType` null (all properties) or a named template attribute
  through `types.Covers` (`msPKI-Certificate-Name-Flag`, `pKIExtendedKeyUsage`, `msPKI-Enrollment-Flag`,
  `msPKI-RA-Signature`, `msPKI-Certificate-Application-Policy`, `msPKI-Certificate-Policy`).
* **Owner** = `summary.Owner`.

The decision follows AccessCheck order (first matching ACE of the token wins), modelled on
`GroupPolicySecurityFilter.Decide` but **without** its raw `ace.Mask & ControlAccess` (it misses a bare GENERIC_ALL bit –
use the normalized mask). Deny ACEs are honoured.

**Trustee level first, accounts on demand.** `TemplateRightsEvaluator.Resolve(templates, session, groups, progress, ct)`:
(1) read `GroupGraph.ReadAsync` once; (2) stream accounts once (`PrivilegedAccessScanner.AccountFilter`, the memberOf
completion made `internal`/shared), building each token with `Reach` plus Everyone / Authenticated Users / This Org / own
SID / primary group / S-1-5-9 for DCs; (3) per template evaluate the decision table and bucket the account DNs per right,
with `PathTo` for the path and `IsDisabled` separated; (4) a trustee of another forest domain that does not resolve is
reported unresolved (`PkiReadNotice`, resource `Pki.Notice.<Code>`). The result (`TemplateRights`: per template, per
right, the trustee SIDs/names with a count, the first N account names, the disabled set apart, the broad/other/Tier-0
class per decision 4) feeds both the templates document (2.5) and the ESC analyzer (2.4). The streamed pass is cancellable
and reports progress (rule 6); the lists stay in memory (rule 1).

**U4b PKI containers** (decision 6; `src/AdMng.Core/PrivilegedAccess/DirectoryAclGrantSource.cs`, the one shared Core file
C3 edits – at the anchor after the GPO block in `RunAsync`): one paged subtree search of
`CN=Public Key Services,CN=Services,<config>` (`DistinguishedName.Child`, rule 3) over `pKICertificateTemplate`,
`pKIEnrollmentService`, `certificationAuthority`, `msPKI-Enterprise-Oid`, `cRLDistributionPoint`, `container` with
`SdAttributes`, each `Read` into a new `AclTargetKind` value (`PublicKey`) with `IsTier0 = true`, `Tier0Subtree = true`,
`ReportOwner = true`; `ReadDefaultsAsync` then picks up the new classes. Provisioning trustees are seeded before
`Evaluate`: Cert Publishers (`{domain}-517`) on AIA / CDP / Certification Authorities / NTAuth, the CA computer SID on its
own enrollment service and CDP objects. ESC4 writes surface as a new `AclRight` (`WriteTemplateProperty` with the attribute
as detail, mirroring the `WriteGpoProperty` branch); its resx entries are added in both languages
(`PrivilegedAccess.AclRight.WriteTemplateProperty`). A separate ACL sample (`AddPublicKeyAclSample`) keeps the exact-set
`DirectoryAclGrantSourceTests` unchanged.

### 2.4 Core – the ESC analyzer, finding codes and the certificate parsers (C4)

**Parsers** (`src/AdMng.Core/Pki/`, pure, value-free `ToString`):
* `CertificateSummary.Of(byte[])` – via `X509CertificateLoader.LoadCertificate` (never `new X509Certificate2(byte[])`,
  SYSLIB0057; the `X509Certificate2` is disposed after the display fields are copied): subject, issuer, serial, SAN DNS /
  UPN / email, NotBefore/NotAfter, EKU OIDs, key algorithm + size, SHA-1 thumbprint and SHA-256. Modelled on
  `WatchdogServerCertificate.From`.
* `CrlSummary.Of(byte[])` – .NET 10 has no CRL reader, so parse with `System.Formats.Asn1.AsnReader`: CRL number
  (extension `2.5.29.20`), delta indicator (`2.5.29.27`), `thisUpdate` / `nextUpdate`, the revoked-entry count. Never the
  individual revoked serials in P1 (rule 1 – export only).
* `KeyCredentialLink.Parse(string)` – `msDS-KeyCredentialLink` is DN-Binary and in `AlwaysBinary`, so the real DC delivers
  UTF-8 bytes of `B:<hexlen>:<hex>:<dn>`; read with `GetStrings`, split with `WellKnownObjects.TryParseValue`, then parse
  the KEYCREDENTIALLINK_BLOB (version `0x0200`, TLV entries: `0x01` KeyID, `0x02` KeyHash, `0x03` KeyMaterial, `0x04`
  KeyUsage, `0x05` KeySource, `0x06` DeviceId, `0x07` CustomKeyInformation, `0x08` KeyApproximateLastLogonTimeStamp, `0x09`
  KeyCreationTime). Exposes key id, device id, source (AD / Entra), created / last-logon, usage – never the key material
  bytes beyond the id.
* `AltSecurityIdentity.Parse(string)` + `.Strength` (KB5014754): **strong** `X509:<I>…<SR>…` (issuer + **reversed**
  serial-number byte order), `X509:<SKI>…`, `X509:<SHA1-PUKEY>…`; **weak** `X509:<I>…<S>…`, `X509:<S>…`,
  `X509:<RFC822>…`; `Kerberos:…` shown separately.

**The analyzer** is split like K6 (reader → pure `Analyze`): `PkiEscReader.ReadAsync(session, snapshot, options, progress,
ct)` collects the `PkiEscSnapshot` (the templates, their rights from 2.3, the OID→group links, the NTAuth set, the DC
cert evaluations when probed, the U4b-style PKI container ACL grants, and `options.CaGrantsAuthenticatedUsersEnroll` =
decision 3) and `PkiEscAnalyzer.Analyze(snapshot)` (pure, `static partial`: `.Esc1.cs`, `.Esc4.cs`, `.Esc9.cs`,
`.Esc13.cs`, `.Esc14.cs`) runs the LDAP-only ESC rules. Each finding is `PkiFinding(Code, Severity, Area, ObjectKind,
ObjectId, Arguments, Fix=null)` with a **value-free `ToString`** (code, severity, area, the object GUID via
`SysvolPaths.FolderName`-style braces – never a name, DN, subject or value); `RelatedObjectIds` names the CAs/OID objects.
`PkiFindingCodes` (`src/AdMng.Core/Pki/PkiFindingCodes.cs`) copies `GpoFindingCodes`: `public const string X = nameof(X)`,
`static IReadOnlyList<PkiFindingDefinition> All` with `(Code, EscNumber, Severity, Area, Group)`, and a definition record
`PkiFindingDefinition(string Code, string Esc, PkiFindingSeverity Severity, PkiFindingArea Area, PkiRuleGroup Group,
string? Locksmith, string? Mdi)` – the README requires the ESC number, a Locksmith id and an MDI assessment per finding;
**MDI ids are not invented** – `Mdi` is `null` ("none") wherever the lead is unsure. `PkiFindingSeverity` =
`Info | Low | Medium | High | Critical`.

**The LDAP-only ESC rules** (severity by decision 4 from the enrollee/writer class; a template no CA publishes drops a
level with the note `NotPublished`, decision 3; a template requiring CA-manager approval `CT_FLAG_PEND_ALL_REQUESTS`, or
requiring an authorized signature `msPKI-RA-Signature ≥ 1`, is not a finding unless the ESC explicitly bypasses it):

| Code | ESC | Condition | Severity |
|---|---|---|---|
| `EnrolleeSuppliesSubjectClientAuth` | ESC1 | `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT` **and** a client-auth EKU (Client Auth / Smart-Card Logon / PKINIT / Any Purpose / empty) **and** enroll by a broad/other principal **and** no manager approval **and** `msPKI-RA-Signature = 0` | broad → Critical, other → Medium |
| `AnyPurposeOrNoEku` | ESC2 | EKU is Any Purpose (`2.5.29.37.0`) **or** empty (SubCA-equivalent) **and** enrollable by a broad/other principal **and** no approval/signatures | broad → Critical, other → Medium |
| `EnrollmentAgentTemplate` | ESC3 | (a) a template whose EKU is Certificate Request Agent and enrollable by broad/other; **or** (b) a template with `msPKI-RA-Signature = 1`, `msPKI-RA-Application-Policies` = Certificate Request Agent **and** schema v1, enrollable by broad/other and with a client-auth EKU | broad → Critical, other → Medium |
| `TemplateWriteable` | ESC4 | a broad/other principal holds `WriteDacl`, `WriteOwner`, `FullControl`, owner, or `WriteProperty` on a sensitive template attribute (name-flag, enrollment-flag, EKU, application policy, RA-signature) | broad → Critical, other → Medium |
| `PkiObjectWriteable` | ESC5 | a broad/other principal holds write/owner on a PKI container, NTAuth, an OID object, an enrollment service or the CA's computer object (from the U4b PKI pass of 2.3) | broad → Critical, other → Medium |
| `NoSecurityExtensionClientAuth` | ESC9 | `CT_FLAG_NO_SECURITY_EXTENSION` set **and** a client-auth EKU **and** enrollable by broad/other (DC-side enforcement enforced in P3; P1 reports the template condition only) | broad → High, other → Medium |
| `IssuancePolicyLinkedToGroup` | ESC13 | the template carries an issuance policy OID (`msPKI-Certificate-Policy`) whose OID object has `msDS-OIDToGroupLink` to a group, **and** a client-auth EKU, **and** enrollable by broad/other (the linked group's privilege read through U4) | High (Critical when the linked group is Tier 0) |
| `WeakExplicitMapping` | ESC14 | an account carries a **weak** explicit `altSecurityIdentities` mapping, **or** a broad/other principal can write `altSecurityIdentities` on a privileged (Tier 0) account (scope: the accounts streamed in 2.3; the write right from the U4/U4b grants) | High (Tier 0 target) / Medium |
| `SchemaV1EnrolleeSuppliesSubject` | ESC15 | schema **v1** template **and** `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT`, enrollable by broad/other (CVE-2024-49019); P1 marks it *zależne od poprawki urzędu z listopada 2024* (Low/Info pending, confirmed in P3) | Medium (pending) |
| `EnrolleeSuppliesSubjectServerAuth` | ESC17 | `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT` **and** a Server Authentication EKU, enrollable by broad/other (per README track P) | broad → High, other → Medium |

Certipy's ESC numbers are the `Esc` field; Locksmith ids are its `ESCx` rule names where they exist (`Esc1`…`Esc15`;
ESC17 has no Locksmith rule → `null`). All ten are LDAP-only; ESC6/7/8/10/11/16 and ESC12 are P3/out-of-scope and not in
`PkiFindingCodes`.

### 2.5 Presentation/App – the PKI node, the snapshot service and the overview, templates and security documents (W5, W6)

**Tree node and snapshot service.** A `PkiNodeViewModel : TreeNodeViewModel` base (own base, not the GPO node base –
decision 9; subclassing `GroupPolicyNodeViewModel` would make the GPO toggle remove it and `TryActivateGroupPolicyNode`
swallow it). `NavigationTreeViewModel` gets a `PkiViewModelFactory? Pki` constructor parameter, a `ShowPkiNodes(bool)`
inserting *Usługi kluczy publicznych* under the domain node, a relocalize loop in `OnCultureChanged`, and a
*Widok → Usługi kluczy publicznych w drzewie* toggle bound to a new `AppSettings.Pki.ShowInTree` (default on). The
documents hang off a new **`PkiDocumentBase : ObservableObject, IShellDocument`** copied from `GroupPolicyDocumentBase`
but LDAP-only: it keeps `SerialCancellation`, the window `Lifetime`, the `SessionChanged` / `CultureChanged`
subscriptions (unsubscribed in `Dispose`) and `Error` / `LoadedText`, and **drops** SYSVOL, the PDC note and the `Changed`
event; `LoadCoreAsync(IDirectorySession, CancellationToken)` is enough. A scoped **`PkiSnapshotService`**
(`src/AdMng.Presentation/Pki/PkiSnapshotService.cs`, one per window, modelled on `GpoSettingsIndexService` but far
simpler) caches the `PkiSnapshot` and the template-rights expansion keyed on the `IDirectorySession` instance **and**
`session.Info.Server`; `ISessionManager.SessionChanged` drops it, F5 rebuilds; the build runs on `Task.Run` with a
`CancellationTokenSource` linked to `GroupPolicyServices`-style `Lifetime`; it is dropped in `Dispose`.

**W5 documents.** *Przegląd PKI* (`PkiOverviewDocumentViewModel`, tab *Usługi kluczy publicznych*) with the sub-views of
decision 9 (enterprise CAs, roots/intermediates, NTAuth with the enterprise-CA match, CDP lists, KRA, OID with group
links) and a *Zdrowie* tab (`pkiview`-style rows OK / Wygasa / Wygasła / Niedostępna, a *Sprawdź adresy HTTP* button that
calls `PkiHealthProbe` on `Task.Run`) and a *Certyfikaty LDAPS* tab (per DC, from `DomainControllerCatalog.ReadAsync`,
each probed with `IDomainControllerTlsProbe` – at most 4 in parallel via a `SemaphoreSlim`, the current DC first, 3269
only for GCs). *Szablony certyfikatów* (`PkiTemplatesDocumentViewModel`) with the README columns, the decoded flags as
Polish/English sentences (technical bit name in the tooltip), the periods in days/weeks/years, the issuing CAs,
unpublished templates, the expanded enrollment rights and *kto może zmienić* from `PkiSnapshotService`, a compare-two and
an export (CSV/HTML to an operator-chosen file through `Gp.Ui.Files` + `ICsvCodec` `protectFormulas: true`).

**W6 documents.** *Przegląd bezpieczeństwa AD CS* (`PkiSecurityReviewDocumentViewModel` + `.Filters.cs` + `.Actions.cs`),
modelled on `GroupPolicyDiagnosticsDocumentViewModel`: `LoadCoreAsync` runs `Task.Run(PkiEscReader.ReadAsync + Analyze)`
with progress, name resolution, then `Show`; severity chips, filters (`SelectedSeverity` / `SelectedEsc` / `SelectedArea`
/ `SearchText`, `ISearchableDocument`), rows with the ESC number, the Certipy/Locksmith/MDI ids, the condition, the attack
path, the affected object and the principals with counts. **No `Napraw` in P1** – the only row action is *Otwórz
szablon/urząd* through the shell (`DocumentRequested`), plus *Kopiuj*, *Eksportuj CSV / HTML* (self-contained HTML page,
CSP `default-src 'none'`, every value through `E()`, a privacy footer). The *gotowość na silne mapowanie i NTAuth* report
(`PkiReadinessDocumentViewModel`, W6) is a **separate document** in the PKI area (not the stage-S readiness check – README
puts PKI readiness rows in P4): status-per-check rows (accounts with weak `altSecurityIdentities`, auth templates without
the SID extension, logon-capable CAs not in NTAuth, DC LDAPS certs without a proper SAN/EKU) with a link that opens the
event-log reader for KDC 39/40/41/45/21 **only if** `EventLogLauncher` supports a preset query – otherwise the row says
*P4*. Texts of the findings are dynamic resx keys in `PkiFindingText` (2.8).

Shell wiring (`src/AdMng.Presentation/Shell/ShellViewModel.Pki.cs` – a new partial, so
`XamlSourceTests.EveryCommandIdUsedInXaml_IsAShellCommand`, which scans `Shell/ShellViewModel*.cs`, sees the `pki.*`
descriptors): `OpenPkiDocument(request)`, a `PkiDocuments` filter, the `pki.overview` / `pki.templates` / `pki.security` /
`pki.readiness` descriptors, `AttachPki` / `DetachPki`, `TryActivatePkiNode`, and `PkiStateCommands` added to
`StateCommands`. `GroupPolicyDocumentKind`-style the requests go through a `PkiDocumentRequest(PkiDocumentKind Kind)`
record with `TemplateCn`, `CaDn`, `FindingCode` init props and a value-free `ToString`.

### 2.6 Presentation/App – the read-only property tabs, the TLS-probe implementation and CA-host navigation (W7)

**Property-sheet tabs** (`src/AdMng.Presentation/Pki/` property pages; each `public sealed partial class … :
PropertyPageViewModel`, read-only – `GetOperations() => []`, both `Compute*` false, `Revert()` empty, rule 5): *Opublikowane
certyfikaty* (`PublishedCertificatesPageViewModel`, user / computer / contact, columns *Wystawiony dla*, *Wystawiony
przez*, przeznaczenie, ważność, szablon, odcisk – from `userCertificate` and `userSMIMECertificate` via
`CertificateSummary`), *Mapowania nazw* (`NameMappingsPageViewModel`, user / computer – `altSecurityIdentities` with the
KB5014754 strength from `AltSecurityIdentity`) and *Poświadczenia kluczy* (`KeyCredentialsPageViewModel`, user / computer –
`msDS-KeyCredentialLink` via `KeyCredentialLink`, key id / device id / source / created / last-logon / usage). They are
added to the `PropertyPageFactory` lists (a new `PageIf(attr, factory)` helper, since `Known<T>` only accepts fields),
after Organization and before `MemberOfPageViewModel` (ADUC order); the exact-tab-order tests in `PropertySheetTests`
(user `:38`, group `:317`, computer `:361`, OU `:381`) are updated. The certificates are large binary values, so they are
read **lazily** (the Attribute-Editor `ActivateAsync` / `LoadDetailsAsync` pattern with the page's own
`SerialCancellation`); `altSecurityIdentities` is small and goes in `ReadAttributes`. No temp `.cer` files – *Kopiuj do
pliku…* is an operator-chosen export only (rule 1). `userPKCS12` is added to `SecretAttributes.WellKnownNames` and the
attribute editor masks secret `ValueText` / `EditableValues` / the value editor with
`SecretAttributes.IsSecret(Attribute, _context.Schema) ? SecretAttributes.Mask : …` (decision 7).

**TLS probe implementation** (`src/AdMng.Directory/Pki/SslStreamTlsProbe.cs`, implements the 2.1 contract; Directory is
cross-platform and already TCP/LDAP-flavoured, and the Watchdog references it for P4 LDAPS-expiry reminders): `TcpClient`
+ `SslStream`, capturing `RawData` of the presented chain in the validation callback (`CertificateRevocationCheckMode =
NoCheck`, no credential sent), the `LdapConnectionFactory.ProbeAsync` timeout pattern; no `OperatingSystem.IsWindows()`
guard (SslStream works on both). Registered App-side (`AddAdMngPkiApp`, called from `AppHost` after `AddAdMngPresentation`)
as `TryAddSingleton<IDomainControllerTlsProbe, SslStreamTlsProbe>()`; added to `HostTests` `SharedApp` and faked in
`UiTestContext` with `FakeDomainControllerTlsProbe`.

**CA-host navigation.** From a CA row to its computer object and the K1 remote actions through a shared
`ComputerLookup.FindByHostAsync` (extracted from the two private copies in `EventLogServices` / `ReadinessChecker`) and
`IComputerDialogs.ShowRemoteActionsAsync` (the `LockoutDocumentViewModel.Pivot` pattern: `CanOpenRemoteActions =>
Services.Ui.Dialogs is IComputerDialogs`). A CA host in another forest domain that does not resolve shows a note, not an
error.

### 2.7 Data handling (rules 1–6)

* **Rule 1.** The `PkiSnapshot` (CAs, templates, certificates, CRLs, OID links), the ESC findings, the template-rights
  expansion and the probed DC certificates live in the scoped `PkiSnapshotService` and the open documents only; dropped on
  `SessionChanged`, disconnect and window close (scope disposal). Nothing in settings (no PKI data; only the two expiry
  thresholds and the node toggle), temp files or logs; diagnostic logs carry counts and durations only – never a subject,
  thumbprint, DN or serial. Every new record's `ToString()` prints kinds, severities, GUIDs and counts, never a value.
  Exports (`.cer`, `.crl`, CSV, HTML) go only to a file the operator picks.
* **Rule 2.** No password or private key is read: `userPKCS12`, `msPKIAccountCredentials`, `msPKIDPAPIMasterKeys` are in
  `SecretAttributes` and never shown, copied or exported; KRA key recovery is out of scope. `KeyCredentialLink` exposes
  only the id/metadata, never the key material.
* **Rule 3.** Every PKI DN is built with `DistinguishedName.Child` (`Child("CN","Services").Child("CN","Public Key
  Services")`); CA names in `CN=CDP` / `CN=AIA` are certutil-sanitized, so match by attribute, not RDN text. Every filter
  is `LdapFilter.Equal/Or/And`; typed host/account text is never concatenated into a filter.
* **Rule 4.** One DC: the snapshot reader, the rights evaluator, U4b and the property tabs read on the window session's DC;
  the configuration partition is forest-replicated, so no other DC is opened. The only traffic that leaves the session is
  the 636/3269 TLS probe and, on the operator's button, the AIA/CDP/OCSP HTTP check (decision 1).
* **Rule 5.** P1 writes nothing: the documents and property tabs are read-only, `GetOperations()` returns `[]`, the ESC
  review offers navigation only (no `Napraw`). P2 adds the change-plan repairs.
* **Rule 6.** The snapshot build, the rights streaming pass and the TLS probes are async, cancellable and report progress;
  at most 4 TLS probes and 4-wide fan-out at a time; long template and finding lists virtualize; the ESC analysis runs on
  `Task.Run`.

### 2.8 Localization

Both `Strings.resx` and `Strings.pl.resx`, one block per owner between comment markers (the exact anchors are in 2.12),
constants in new `L.*.cs` files; dynamic families get an `AllKeys()` helper tested like `GpoFindingText.AllKeys()`. The
prefix may differ from the class name (as `L.EventLogs` → `EvtLog.`). New classes (no `Pki`/`Cert` prefix exists yet):

| File | Class | Prefix | Owner |
|---|---|---|---|
| `L.Pki.cs` | node, overview, health (`Pki.Health.<Status>`), read notes (`Pki.Notice.<Code>`) | `Pki.` | W5 |
| `L.PkiTemplates.cs` | template list, columns, flag sentences (`PkiTpl.Flag.<Attr>.<Bit>`), period units, enrollment rights | `PkiTpl.` | W5 |
| `L.PkiSecurity.cs` | the ESC review document (chips, filters, row labels; finding texts live in `PkiFindingText`) | `PkiEsc.` | W6 |
| `L.Certificates.cs` | the three read-only property tabs, DC LDAPS, mapping strength (`Cert.Mapping.<Strength>`); page titles stay `Pages.*` | `Cert.` | W7 |
| `L.PkiReadiness.cs` | the strong-mapping / NTAuth report | `PkiReady.` | W6 |

**Dynamic key families** (each with `AllKeys()` and a completeness test, modelled on `GpoFindingTextTests`):
* `PkiTpl.Flag.<Attr>.<Bit>` – one entry per flag bit of 2.2 (owner W5, `L.PkiTemplates.cs`); the helper enumerates
  `PkiFlags`' tables. Test: every bit has en + pl.
* `Pki.Health.<PkiHealthStatus>`, `Pki.Notice.<PkiReadNoticeCode>`, `Cert.Mapping.<MappingStrength>` – one entry per enum
  value, enumerated by `Enum.GetValues` (owner W5/W7). Tests enumerate the enum.
* **`PkiFindingText`** (`src/AdMng.Presentation/Pki/PkiFindingText.cs`, owner **C4**, in the P1 contract commit so
  `LocalizationTests` stay green from the start – modelled on `GpoFindingText`): `ArgumentCounts`, `TitleKey` /
  `MessageKey` / `ExplanationKey` / `RecommendationKey` = `PkiFinding.<Code>.Title|Message|Explanation|Recommendation`,
  `Severities`, `AllKeys()`, plus `PkiEsc.Severity.<sev>`, `PkiEsc.Chip.<sev>`, `PkiEsc.Area.<area>`. The block **P1
  contract – AD CS finding texts (2.4)** carries all ten codes' four texts in en + pl in the contract commit (W6 may
  refine the wording). A `PkiFindingTextTests` asserts all four texts exist in both languages, the argument counts match
  and a hard-coded `Assert.Equal(10, …)` on the code count.

Terminology (EN / PL, from the Polish `certtmpl.msc` / `pkiview.msc` / `certsrv.msc` UI), added as a table here and used
across the resx:

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
| health: OK / Expiring / Expired / Unavailable | Stan: OK / Wygasa / Wygasła / Niedostępna |

Polish UI wording as in the consoles' Polish editions; test data with diacritics (decision 11).

### 2.9 Not in P1 (later options)

P2 repairs (`Napraw…`, template-property edits, `ModifySecurity` ACL writes, template copy, NTAuth / `msDS-OIDToGroupLink`
edits, `userCertificate` / `altSecurityIdentities` / `msDS-KeyCredentialLink` edits, bulk actions); P3 CA-host reads and
actions (ESC6/7/8/10/11/16, the database views, approval/revocation/CRL publication); ESC12 (YubiHSM) is out of program
scope. The DC-side enforcement of ESC9 / ESC10 (`StrongCertificateBindingEnforcement`, Schannel
`CertificateMappingMethods`) and the September-2025 patch read are P3/K1. Moving PKI ESC evaluation into the Watchdog
(P4 / track W) needs a user decision and an ADR-0008/0009 amendment (Q24, 2026-10-20). The KDC-event preset of the
readiness report is P4 unless the event-log reader already offers one. An MDI assessment id is filled in only when a real
one is known.

### 2.10 Test plan

**Core (C1, `tests/AdMng.Core.Tests/Pki/`):** `PkiOverviewReader` – the exact search options (classes, SD flags,
extended DN), per-container `NoSuchObject`/`AccessDenied` → a notice not a throw, binary multi-values read as `byte[]`, the
health rows at the decision-2 thresholds against a fixed `Now`; `DcCertificateEvaluation` – SAN/EKU/validity/chain-to-root
with a hand-built chain; `CertificateSummary`/`CrlSummary` on fixed DER and a `CertificateRevocationListBuilder` CRL.
**Core (C2, `tests/AdMng.Core.Tests/Pki/`):** `PkiFlags.Decode` for each of the four tables (every bit, unknown bits as
`0x…`), the EKU catalogue, `PkiPeriod.Decode` on the negative little-endian 100-ns periods (one year, six weeks),
`CertificateTemplate.Of` on a seeded template; a golden test that `FlagNames` (Watchdog) delegating to `PkiFlags` produces
byte-for-byte the same names (run `tests/AdMng.Watchdog.Core.Tests` and `tests/AdMng.Watchdog.Service.Tests` unchanged).
**Core (C3, `tests/AdMng.Core.Tests/Pki/`):** `TemplateRightsEvaluator` – the AccessCheck decision table (deny wins, ACE
order, GENERIC_ALL normalized, inherit-only skipped, Enroll by the extended-right GUID and by GENERIC_ALL), trustee-level
first then streamed accounts with counts / paths / disabled-apart / cancellation / progress, an other-domain trustee
unresolved; the U4b PKI pass on `AddPublicKeyAclSample` (a default Windows PKI → **no** findings thanks to the provisioning
trustees; `DirectoryAclGrantSourceTests`' existing exact-set unchanged because the PKI objects are in the separate sample).
**Core (C4, `tests/AdMng.Core.Tests/Pki/`):** `KeyCredentialLink.Parse` on a hand-built KEYCREDENTIALLINK_BLOB,
`AltSecurityIdentity.Strength` on each strong/weak form (incl. the reversed `<SR>`), `PkiEscAnalyzer.Analyze` – one test
per ESC on **hand-built** `PkiEscSnapshot` records (C4 does not wait for C1-C3's readers): the vulnerable template flagged
at the decision-4 severity **and** its safe look-alike **not** flagged (manager approval, an authorized RA signature, no
broad enrollment, or unpublished → `NotPublished` lower level), the `ToString` value-free (a Polish-values-absent
contract test like `GpoK7ContractTests`).
**Sample (shared, `tests/AdMng.Testing/FakeDirectoryBuilder.PublicKey.cs` + `FakeSchema.PublicKey.cs`):**
`AddPublicKeySample(SampleCompany) → PublicKeySample` seeds (calling `AddWatchdogInfrastructure()` first when the config
NC is empty) `CN=AIA`, `CN=CDP/<host>/<CA>` (`cRLDistributionPoint`), `CN=KRA`, `CN=OID` (`msPKI-Enterprise-Oid` +
`msDS-OIDToGroupLink`), the CA certificates and the NTAuth `cACertificate`, the two enterprise CAs *Kontoso Główny CA* /
*Kontoso Wystawiający CA*, Polish-named templates with diacritics, **one deliberately vulnerable template per LDAP ESC and
a safe look-alike** (e.g. *Użytkownik-VPN-Podatny* with `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT` + client-auth EKU + Domain
Users enroll for ESC1, beside *Użytkownik-VPN-Bezpieczny* with the same EKU but manager approval), and a sample record
exposing the ids by role. `FakeSchema.PublicKey.cs` adds the missing attributes (`msPKI-Private-Key-Flag`,
`msPKI-Template-Schema-Version`, `msPKI-Template-Minor-Revision`, `revision`, `pKIExpirationPeriod`, `pKIOverlapPeriod`,
`msPKI-Minimal-Key-Size`, `msPKI-Cert-Template-OID`, `msPKI-Certificate-Policy`, `msPKI-RA-Policies`,
`msPKI-RA-Application-Policies`, `msPKI-Supersede-Templates`, `pKIDefaultCSPs`, `pKIKeyUsage`, `msPKI-Enrollment-Servers`,
`certificateRevocationList`, `deltaRevocationList`, `authorityRevocationList`, `crossCertificatePair`,
`msDS-OIDToGroupLink`, `userSMIMECertificate`, `userPKCS12`) and the classes `cRLDistributionPoint`,
`msPKI-Enterprise-Oid`, `msPKI-PrivateKeyRecoveryAgent`, wired into `FakeSchema.CreateCore`. A `PkiSamples` helper in
`AdMng.Testing` builds CA + issued certificates (`CertificateRequest.Create(issuer, …)`) and CRLs
(`CertificateRevocationListBuilder`) with **fixed dates around 2026-10-13**, Polish diacritics in the subjects. Enroll /
AutoEnroll GUIDs added to `FakeDirectoryBuilder.Acl.cs` `AclGuids` and `Certificate-Enrollment`/`AutoEnrollment` (validAccesses `0x100`) to its `ControlAccessRights`.
**Presentation (pl-PL, a new `PkiHarness` copied from `GroupPolicyHarness`, `tests/AdMng.Presentation.Tests/Pki/`):** the
snapshot service (one build, reuse, dropped on `SessionChanged`, a reconnect to the same server not reused); the overview /
health / templates documents on `AddPublicKeySample`; the security review (the vulnerable template appears with its ESC
number and severity, the look-alike does not, filters, chips, *Otwórz szablon* through `DocumentRequested`, export); the
readiness report rows; the property tabs via `harness.Factory.CreatePropertySheet(dn)` + `ActivateAsync`
(`PublishedCertificates`, `NameMappings` with KB5014754 strength, `KeyCredentials`); the `userPKCS12` masking in the
attribute editor; the shell palette `pki.*`; `HostTests` that the node factory is scoped; `ProbeTests` of
`SslStreamTlsProbe` on a `TcpListener(IPAddress.Loopback, 0)` + `SslStream.AuthenticateAsServerAsync` (closed port,
accept-then-reset, hang→timeout, expired leaf, wrong SAN – host `"localhost"`); HTTP health with a fake
`HttpMessageHandler`. End-to-end tests that need the merged Core readers are `[Fact(Skip = "P1 wave 1 merge")]` until the
lead merges wave 1.
**App (Windows CI):** the documents in `MainWindow` templates, `SmokeTest.RenderPkiAsync`, UI tests (binding errors, the
node, the tabs via `VisitAllPagesAsync`), screenshots **36** `36-pki-overview.png`, **37** `37-pki-templates.png`, **38**
`38-pki-security.png`, **39** `39-pki-readiness.png` in `tests/AdMng.App.UiTests/ScreenshotTestsPki.cs` (the name starts
`ScreenshotTests` so CI picks it up). Lab: Samba has only empty PKI containers, so the integration project seeds nothing
and only tests the empty-PKI path and an LDAPS probe of the lab DC; AD CS tests are skipped without `ADMNG_TEST_CA_*`.

### 2.11 Contracts (in the code since the P1 contract commit)

The public types that cross agent boundaries are committed by the lead **before wave 1** as compilable stubs with XML doc
comments (bodies `throw new NotImplementedException("P1 Cn: …")`, except trivial pass-throughs and the already-implemented
text/code tables); **the code is authoritative**, this table is the map. An implementer may add members, never rename or
remove these without writing the change into 2.14. The value-free `ToString` rules of 2.7 are implemented in the stubs and
pinned by `PkiContractTests` (Polish values asserted absent).

| File | Types and members | Implemented by |
|---|---|---|
| `src/AdMng.Core/Pki/PkiAttributes.cs` | `static class PkiAttributes` – the PKI attribute-name constants and `OverviewAttributes` / `TemplateAttributes` lists (Core must not depend on `Watchdog.Core`) | done (contract) |
| `src/AdMng.Core/Pki/PkiClasses.cs` | `static class PkiClasses` – `CertificateTemplate`, `EnrollmentService`, `CertificationAuthority`, `CrlDistributionPoint`, `EnterpriseOid`, `KeyRecoveryAgent`; `All` | done (contract) |
| `src/AdMng.Core/Pki/PkiFlags.cs` | `static class PkiFlags` – `NameFlag`, `EnrollmentFlag`, `PrivateKeyFlag`, `GeneralFlag` tables; `KnownEku`; `Decode(table, long)`, `IsClientAuth(ekus)` | C2 |
| `src/AdMng.Core/Pki/PkiPeriod.cs` | `readonly record struct PkiPeriod`; `static PkiPeriod Decode(ReadOnlySpan<byte>)` | C2 |
| `src/AdMng.Core/Pki/CertificateTemplate.cs` | `sealed record CertificateTemplate` (+ `ToString`); `static CertificateTemplate Of(DirectoryObject, AclObjectTypes?)` | C2 |
| `src/AdMng.Core/Pki/CertificateSummary.cs`, `CrlSummary.cs` | `sealed record CertificateSummary` / `CrlSummary` (+ value-free `ToString`); `static … Of(ReadOnlySpan<byte>)` | C4 |
| `src/AdMng.Core/Pki/KeyCredentialLink.cs`, `AltSecurityIdentity.cs` | `sealed record KeyCredentialLink` + `static IReadOnlyList<KeyCredentialLink> Parse(IEnumerable<string>)`; `sealed record AltSecurityIdentity`, `enum MappingStrength`, `static AltSecurityIdentity Parse(string)` | C4 |
| `src/AdMng.Core/Pki/PkiSnapshot.cs` | `sealed record PkiSnapshot` and its nested records (`PkiEnrollmentService`, `PkiCertificateAuthority`, `PkiCrlList`, `PkiKraAgent`, `PkiOidObject`, `PkiHealthRow`, `enum PkiHealthStatus`, `PkiReadNotice`, `enum PkiReadNoticeCode`); all value-free `ToString` | C1 |
| `src/AdMng.Core/Pki/PkiOverviewReader.cs` | `sealed record PkiReadOptions` (`Now`, the two expiry thresholds); `static Task<PkiSnapshot> ReadAsync(IDirectoryReader, PkiReadOptions, IProgress<PkiReadProgress>?, CancellationToken)`; `PkiReadProgress` | C1 |
| `src/AdMng.Core/Pki/Tls/IDomainControllerTlsProbe.cs` | `interface IDomainControllerTlsProbe`, `sealed record TlsProbeResult` (+ `ToString`), `enum TlsProbeOutcome`, `static class DcCertificateEvaluation` (`Evaluate(chain, dcFqdn, domainDns, roots) → DcCertificateInfo`) | C1 (impl W7) |
| `src/AdMng.Core/Pki/TemplateRights.cs` | `sealed record TemplateRights`, `enum TemplateRight`, `enum PrincipalClass` (`Tier0`, `Broad`, `Other`), `sealed record TemplateTrustee` | C3 |
| `src/AdMng.Core/Pki/TemplateRightsEvaluator.cs` | `static IReadOnlyList<(TemplateRight, PrincipalClass)> Evaluate(ReadOnlySpan<byte> sd, AclObjectTypes)`; `static Task<IReadOnlyDictionary<string, TemplateRights>> ResolveAsync(IReadOnlyList<CertificateTemplate>, IDirectorySession, GroupGraph, IProgress<…>?, CancellationToken)` | C3 |
| `src/AdMng.Core/Pki/PkiEscModel.cs` | `enum PkiFindingSeverity`, `enum PkiFindingArea`, `enum PkiRuleGroup`, `sealed record PkiFinding` (+ value-free `ToString`, `RelatedObjectIds`), `sealed record PkiEscSnapshot`, `sealed record PkiEscResult : IReadOnlyList<PkiFinding>` (`NotEvaluated`) | C4 |
| `src/AdMng.Core/Pki/PkiFindingCodes.cs` | `const string` codes (10), `sealed record PkiFindingDefinition(Code, Esc, Severity, Area, Group, Locksmith, Mdi)`, `static IReadOnlyList<PkiFindingDefinition> All`, `Find(code)` | done (contract) |
| `src/AdMng.Core/Pki/PkiEscAnalyzer.cs` | `static PkiEscResult Analyze(PkiEscSnapshot)` (C4 may split into `.Esc*.cs`); `PkiEscReader.ReadAsync` | C4 |
| `src/AdMng.Core/PrivilegedAccess/AclObjectTypes.cs` + `ExtendedRightIds.cs` | `CertificateEnrollment` / `CertificateAutoEnrollment` constants + `Fallbacks` GUIDs; `ExtendedRightIds.CertificateEnrollment` / `…AutoEnrollment` | done (contract) |
| `src/AdMng.Core/PrivilegedAccess/AclRight.cs` + both resx | `AclRight.WriteTemplateProperty` + `PrivilegedAccess.AclRight.WriteTemplateProperty` (en + pl) | done (contract) |
| `src/AdMng.Core/Schema/SecretAttributes.cs` | `"userPKCS12"` in `WellKnownNames` | done (contract) |
| `src/AdMng.Presentation/Pki/PkiFindingText.cs` + both resx (`P1 contract – AD CS finding texts (2.4)` block) | `ArgumentCounts`, `TitleKey`/`MessageKey`/`ExplanationKey`/`RecommendationKey`, `Severities`, `AllKeys`, the 10 codes' texts en + pl | done (W6 may reword) |
| `src/AdMng.Presentation/Pki/PkiDocumentBase.cs` | `abstract partial class PkiDocumentBase : ObservableObject, IShellDocument` (LDAP-only; no SYSVOL/PDC) | W5 |
| `src/AdMng.Presentation/Pki/IPkiServices.cs`, `PkiSnapshotService.cs` | `PkiDocumentKind`, `PkiDocumentRequest`, `interface IPkiSnapshotService` (`Current`, `IsStale`, `IsBuilding`, `GetAsync(refresh, ct)`, `Invalidate`), the scoped service | W5 |
| `src/AdMng.Presentation/Pki/PkiViewModelFactory.cs` | `Create(request)`, `static bool Matches(document, request)` (refuses each kind until its wave adds the case) | W5 |
| `src/AdMng.Presentation/Pki/PkiServiceCollectionExtensions.cs` | `AddAdMngPki()` – `TryAddScoped` service + factory; both in `ConnectionLifetimes` | done (contract) |
| `src/AdMng.Presentation/Navigation/PkiNodes.cs` | `abstract PkiNodeViewModel : TreeNodeViewModel`, the concrete node | W5 |
| `src/AdMng.Presentation/PropertySheets/…` | `PageIf(string attr, Func<PropertyPageViewModel>)` helper; the three page classes live in `src/AdMng.Presentation/Pki/` | W7 |

Still to be added by their owners (not in the contract commit, because they need types only the owner writes): the
concrete document view models, the XAML, `L.Pki.cs` / `L.PkiTemplates.cs` (W5), `L.PkiSecurity.cs` / `L.PkiReadiness.cs`
(W6), `L.Certificates.cs` (W7), `SslStreamTlsProbe` and `FakeDomainControllerTlsProbe` (W7).

### 2.12 Work split and file ownership

**Waves.** Wave 1 (parallel worktrees from the branch with this section's contract commit): **C1** overview reader +
snapshot + TLS-probe contract; **C2** template model + flag/period decoders; **C3** rights evaluator + U4b PKI
containers; **C4** certificate/CRL/KeyCredentialLink/altSecurityIdentities parsers + ESC analyzer + finding texts. Wave 2
(parallel, from the merged wave 1): **W5** node, snapshot service, overview/health/templates documents; **W6** security
review + readiness report; **W7** property tabs, TLS-probe implementation, CA-host navigation. Docs
(`docs/CERTYFIKATY.md`, README/ROADMAP/ARCHITECTURE/PILOTAZ edits, screenshots) are the integration step after wave 2
(lead), from the as-built notes.

**Dependencies.** The lead's **P1 contract commit** (2.11) is in the branch before wave 1 so the four Core agents and the
three Presentation agents compile against the same stubs from the first minute. C4's analyzer tests build `PkiEscSnapshot`
by hand and do not need C1-C3; only the end-to-end reader tests (C3's `TemplateRightsEvaluator.ResolveAsync`, C1's
`PkiOverviewReader`, the W5-W7 documents on the real readers) need the merge – they are written against the stubs and
marked `[Fact(Skip = "P1 wave 1 merge")]` until the lead removes the skip while merging. Wave 2 needs nothing from the
other wave-2 agents: `PkiDocumentKind`, `PkiDocumentRequest`, the factory signatures, the `PageIf` helper and the
`IDomainControllerTlsProbe` contract are in the contract commit.

**New files (exclusive).**

| Owner | Files |
|---|---|
| C1 | `src/AdMng.Core/Pki/{PkiSnapshot,PkiOverviewReader}.cs`; `src/AdMng.Core/Pki/Tls/**`; `tests/AdMng.Core.Tests/Pki/{PkiOverviewReaderTests,DcCertificateEvaluationTests}.cs` |
| C2 | `src/AdMng.Core/Pki/{PkiFlags,PkiPeriod,CertificateTemplate}.cs`; `tests/AdMng.Core.Tests/Pki/{PkiFlagsTests,PkiPeriodTests,CertificateTemplateTests}.cs` |
| C3 | `src/AdMng.Core/Pki/{TemplateRights,TemplateRightsEvaluator}.cs`; `tests/AdMng.Core.Tests/Pki/TemplateRightsEvaluatorTests.cs`; `tests/AdMng.Core.Tests/PrivilegedAccess/DirectoryAclPublicKeyTests.cs` |
| C4 | `src/AdMng.Core/Pki/{CertificateSummary,CrlSummary,KeyCredentialLink,AltSecurityIdentity,PkiEscModel,PkiEscAnalyzer}.cs` (+ `PkiEscAnalyzer.Esc*.cs`); `src/AdMng.Presentation/Pki/PkiFindingText.cs`; `tests/AdMng.Core.Tests/Pki/{CertificateSummaryTests,CrlSummaryTests,KeyCredentialLinkTests,AltSecurityIdentityTests,PkiEscAnalyzerTests}.cs`; `tests/AdMng.Presentation.Tests/Pki/PkiFindingTextTests.cs` |
| W5 | `src/AdMng.Presentation/Pki/{PkiDocumentBase,PkiSnapshotService,PkiViewModelFactory,PkiOverviewDocumentViewModel*,PkiTemplatesDocumentViewModel*}.cs`; `src/AdMng.Presentation/Navigation/PkiNodes.cs`; `src/AdMng.Presentation/Shell/ShellViewModel.Pki.cs`; `src/AdMng.Presentation/Localization/{L.Pki,L.PkiTemplates}.cs`; `src/AdMng.App/Pki/{PkiOverviewView,PkiTemplatesView}.xaml(.cs)`; `tests/AdMng.Presentation.Tests/Pki/{PkiHarness,PkiSnapshotServiceTests,PkiTreeAndShellTests,PkiTemplatesTests}.cs`; `tests/AdMng.App.UiTests/Pki/PkiOverviewUiTests.cs`; `tests/AdMng.App.UiTests/ScreenshotTestsPki.cs` (36, 37) |
| W6 | `src/AdMng.Presentation/Pki/{PkiSecurityReviewDocumentViewModel*,PkiReadinessDocumentViewModel*,PkiFindingRowViewModel}.cs`; `src/AdMng.Presentation/Localization/{L.PkiSecurity,L.PkiReadiness}.cs`; `src/AdMng.App/Pki/{PkiSecurityReviewView,PkiReadinessView}.xaml(.cs)`; `tests/AdMng.Presentation.Tests/Pki/{PkiSecurityReviewTests,PkiReadinessTests}.cs`; `tests/AdMng.App.UiTests/Pki/PkiSecurityUiTests.cs` (screenshots 38, 39 appended to `ScreenshotTestsPki.cs`, W5-owned, at an anchor) |
| W7 | `src/AdMng.Presentation/Pki/{PublishedCertificatesPageViewModel,NameMappingsPageViewModel,KeyCredentialsPageViewModel}.cs`; `src/AdMng.Presentation/Localization/L.Certificates.cs`; `src/AdMng.Directory/Pki/SslStreamTlsProbe.cs`; `src/AdMng.App/Pki/PkiAppRegistration.cs`; `tests/AdMng.Testing/FakeDomainControllerTlsProbe.cs`; `tests/AdMng.Directory.IntegrationTests/Pki/SslStreamTlsProbeTests.cs`; `tests/AdMng.Presentation.Tests/Pki/CertificateTabsTests.cs` |

Shared: `tests/AdMng.Testing/{FakeDirectoryBuilder.PublicKey,FakeSchema.PublicKey,PkiSamples}.cs` and
`FakeDirectoryBuilder.Acl.cs` (the Enroll/AutoEnroll GUID additions) are written by the **lead in the contract commit**
(C1–C4 and W5–W7 all read the sample), with `AddPublicKeyAclSample` for C3.

**Shared files – changes only at these anchors** (both sides kept when two owners insert at the same anchor; Core waves
before Presentation):

| File | Owner: change at anchor |
|---|---|
| `src/AdMng.Core/PrivilegedAccess/DirectoryAclGrantSource.cs` | C3: the PKI-container subtree pass after the `CN=Policies` GPO block in `RunAsync`; `AclTargetKind.PublicKey`; the provisioning trustees |
| `src/AdMng.Core/PrivilegedAccess/AclObjectTypes.cs`, `ExtendedRightIds.cs`, `AclRight.cs` | done by the contract commit (Enroll/AutoEnroll GUIDs, `WriteTemplateProperty`); C3 may only use them |
| `src/AdMng.Watchdog.Core/Detection/FlagNames.cs` | C2: `TableOf` / the certificate tables delegate to `PkiFlags` (a separate behaviour-neutral commit; Watchdog output unchanged, its tests pass unchanged) |
| `src/AdMng.Presentation/Resources/Strings.resx`, `Strings.pl.resx` | done: block `P1 contract – AD CS finding texts (2.4)` after `<!-- K7 settings search (21.4): end -->`; C3: `PrivilegedAccess.AclRight.WriteTemplateProperty` in the U4b block; W5: block `P1 PKI overview and health (2.1): begin/end` then `P1 certificate templates and enrollment rights (2.2, 2.3): begin/end`; W6: `P1 AD CS security review (2.4): begin/end` then `P1 strong mapping and NTAuth readiness (2.6): begin/end`; W7: `P1 certificates on accounts and DCs (2.5, 2.6): begin/end` (each anchored after the previous block's end marker, in this order) |
| `src/AdMng.Presentation/ServiceCollectionExtensions.cs` | done by the contract commit: `AddAdMngPki()` before the scoped block; W7's `AddAdMngPkiApp` is App-side |
| `src/AdMng.Presentation/Navigation/NavigationTreeViewModel.cs` | W5: the `Pki` constructor parameter, `ShowPkiNodes`, the `OnCultureChanged` relocalize loop, the `OnSettingsChanged` toggle (after the GPO lines) |
| `src/AdMng.Presentation/Settings/AppSettings.cs` + a new `PkiSettings.cs` | done by the contract commit: `Pki` property (`ShowInTree`, `CertificateExpiryWarningDays` = 30, `CaCertificateExpiryWarningDays` = 180) + `Normalize` |
| `src/AdMng.Presentation/PropertySheets/PropertyPageFactory.cs` | W7: the `PageIf` helper and the three tabs in `User(obj)` / `Contact(obj)` / `Computer(obj)` before `MemberOfPageViewModel` |
| `src/AdMng.Presentation/PropertySheets/AttributeEditorPage.cs`, `Fields.cs` | W7: `ValueText` / `EditableValues` / the value editor mask `SecretAttributes.IsSecret` values |
| `src/AdMng.Presentation/Shell/ShellViewModel.cs`, `.Palette.cs`, `.Commands.cs`, `.Documents.cs` | W5/W6: `AttachPki`/`DetachPki` (ctor/Dispose), `if (TryActivatePkiNode(node)) return;` in `OnNodeActivated`, `.. PkiCommandDescriptors(),` in the palette, `.. PkiStateCommands,` in `StateCommands`, an `ActivePki()` arm in `.Documents.cs` (W5's lines first) |
| `src/AdMng.App/AppHost.cs` | W7: `AddAdMngPkiApp()` after `AddAdMngPresentation()` |
| `src/AdMng.App/Views/MainWindow.xaml` | W5/W6: the `pki:`/`pkiv:` xmlns, the `PkiNodeViewModel` tree template, the document content templates, the `pki.*` menu items (W5's lines first) |
| `src/AdMng.App/Views/TreeMenu.cs` | W5: a `case PkiNodeViewModel:` block |
| `src/AdMng.App/Views/PropertiesWindow.xaml` | W7: the three `DataTemplate`s next to the "Stan" template; the `pk:` xmlns |
| `src/AdMng.App/SmokeTest.cs` | W5/W6: `RenderPkiAsync` with the four requests on a disconnected window |
| `src/AdMng.Presentation/Icons/ObjectIcons.cs`, `src/AdMng.App/Controls/Glyphs.cs` | done by the contract commit: `ObjectIcon.Certificate` + its glyph/tone |
| `tests/AdMng.Presentation.Tests/Connections/ConnectionLifetimes.cs` | done: both PKI service types after `PolicyDefinitionsService` |
| `tests/AdMng.App.UiTests/HostTests.cs` | C3: `Assert.Equal(2, …IPrivilegeGrantSource)` unchanged (the PKI pass is inside `DirectoryAclGrantSource`, not a new source); W7: `IDomainControllerTlsProbe` in `SharedApp`; `tests/AdMng.Presentation.Tests/ServiceRegistrationTests.cs` likewise unchanged for the source count |
| `tests/AdMng.App.UiTests/Infrastructure/UiTestContext.cs` | W7: `UseFake<IDomainControllerTlsProbe>` + `AddPublicKeySample` |
| `tests/AdMng.Testing/FakeSchema.cs` | lead (contract commit): `attributes.AddRange(CreatePublicKeyAttributes()); ExtendForPublicKey(classes, schemaNc);` in `CreateCore` |

**Files nobody changes in P1:** `L.cs`, `CLAUDE.md`, `Directory.Packages.props`, `AdMng.slnx`, the Watchdog projects
other than the one `FlagNames.cs` delegation, `AddSampleCompany`, `AddWatchdogInfrastructure`, and the stage-S
`ReadinessChecker` / `ReadinessCodes` (the PKI readiness report is a PKI-area document, not a stage-S area – README puts
stage-S PKI rows in P4). Any other change to a file another owner holds needs the lead's agreement and a line in 2.14; a
merge conflict outside the anchors above means a rule of this section was broken.

### 2.13 Review notes

*(empty – filled after the P1 design review.)*

### 2.14 P1 as built

*(empty – each wave agent appends its as-built note here: `**C1 as built.**` … `**C4 as built.**`, `**W5 as built.**` …
`**W7 as built.**`, `**Integration.**`, `**P1 review fixes**`.)*
