**English** · [Polski](../CERTYFIKATY.md)

# Certificate Services (AD CS) in AD Manager (stage P1)

This document describes stage **P1** of Track P: a **read-only overview of the forest's public key infrastructure**,
read live from the configuration partition over LDAP. AD Manager shows the enterprise certification authorities, the
trusted root and intermediate CAs, the NTAuth store, the CRLs, the key recovery agents, the OIDs and the certificate
templates – with their flags decoded and with **who can really enroll in them**. It assesses the health in the style of
*Enterprise PKI* (`pkiview.msc`), the security against the ESC catalog (Certipy's numbering) and the readiness for
strong certificate mapping (KB5014754), and it adds three property tabs to accounts. The plan of the whole track
(P2–P5): [README, Track P](../../README.md#track-p--certification-authority-ad-cs-and-certificates--p1-); technical
design: [docs/dev/p-certificate-services.md](../dev/p-certificate-services.md).

Rules that apply everywhere:

- **Read-only.** P1 changes nothing in AD – not the templates, not the permissions, not the accounts. The security
  review has no *Fix…* button: repairs through the change plan come in stage P3, issuing certificates in P2 and working
  with the CA itself in P4.
- **LDAP only, one domain controller.** Everything the documents and tabs show comes from the window's domain controller
  (the configuration partition is replicated across the whole forest, so one controller knows the PKI of the whole
  forest). AD Manager **does not connect to the certification authority** – neither over DCOM nor through the registry.
  The only connections outside the LDAP session are the TLS handshake with the LDAPS ports of the domain controllers and
  – only on a button – the check of the HTTP URLs taken from the certificates (chapter 10).
- **Nothing from the directory on disk.** The PKI objects read, the check results and the findings exist only in the
  memory of the connection window. A file is created only when the operator chooses an export and picks the file.
- **Key material – never.** `userPKCS12`, the roaming credentials and other keys are never shown, copied or exported –
  not in the Attribute Editor and not in list columns either (chapter 8).

The examples come from the test domain `contoso.test` with the invented CAs "Kontoso Główny CA" (the root CA) and
"Kontoso Wystawiający CA" (the issuing CA).

## 1. What stage P1 covers

| Document or tab | What it shows | Instead of |
|---|---|---|
| *Public Key Services* (overview) | the enterprise CAs with their certificate, path and templates, the trusted CAs and NTAuth, the CRLs, the key recovery agents, the OIDs, the health, the LDAPS certificates of the domain controllers | `pkiview.msc`, *ADSI Edit*, `certutil` |
| *Certificate Templates* | every template of the forest with decoded flags and periods, the CAs that issue it, who can enroll and who can change the template, a comparison of two templates | `certtmpl.msc` (including the *Security* tab) |
| *AD CS security review* | the ESC1–5, 9, 13, 14, 15 and 17 findings with severity, condition, attack path, accounts and groups | `certipy find -vulnerable`, Locksmith |
| *Strong mapping and NTAuth readiness* | accounts with weak mappings, authentication templates without the SID extension, CAs outside NTAuth, LDAPS and KDC certificates of the domain controllers | manual checks following KB5014754 |
| The *Published Certificates*, *Name Mappings* and *Key Credentials* tabs | the certificates, explicit certificate mappings and Windows Hello / FIDO keys stored on the account | the ADUC tabs, *Attribute Editor* |

Along the way P1 extends the *Privileges* tab of the *Account review* window (U4b): delegations are now also read from
the PKI containers in the configuration partition (sites still are not), with two new rights – *Edit a sensitive
property of the certificate template* and *Write altSecurityIdentities (explicit certificate mappings)*. The default
Windows PKI permissions (the forest root domain's administrators, *Cert Publishers*, the CA computer on its own objects)
are not reported.

**Not in P1:** issuing certificates (P2), repairs, editing templates, permissions, NTAuth, mappings and keys (P3),
reading the configuration, the roles and the database of the CA itself – and with them ESC6, ESC7, ESC8, ESC10, ESC11
and ESC16 (P4), the analysis of the KDC events, Watchdog rules and the key history from the Watchdog (P5). ESC12 is
outside the scope of the program.

## 2. Where to find it

- **Tree:** the **Public Key Services** node below the domain node (after the Group Policy nodes). Selecting the node
  opens the overview, and its three child nodes – *Certificate Templates*, *AD CS security review* and *Strong mapping
  and NTAuth readiness* – open their documents. Context menu: *Open*, and on the root node also the four commands of the
  menu.
- **View → Public Key Services in the tree** shows or hides the node (shown by default; the setting is shared by all
  windows).
- **Tools → Certificate Services (AD CS)**: *Public Key Services…*, *Certificate templates…*, *AD CS security review…*,
  *Strong mapping and NTAuth readiness…*.
- **Command palette** (Ctrl+K):

| Command | Identifier | Group |
|---|---|---|
| *Public Key Services…* | `pki.overview` | *Tools* |
| *Certificate templates…* | `pki.templates` | *Tools* |
| *AD CS security review…* | `pki.security` | *Tools* |
| *Strong mapping and NTAuth readiness…* | `pki.readiness` | *Tools* |
| *Public Key Services in the tree* | `view.pkiNodes` | *View* |

Every document opens as a tab of the connection window, one of each kind; the command for an open tab only selects it.
The documents lead to one another: *Open template* goes to the *Certificate Templates* document with the template
selected, *Open CA* to the overview with the CA selected, and *Open account*, *Open group*, *Open computer* and *Open
object* open the object's property sheet.

**One read per window.** The four documents use the same PKI read of the connection window (and one account count –
chapter 4.2): a second document does not read the directory again. *Refresh*, *Check again*, *Read again* or F5 in any
of them reads everything again, and the other open PKI documents show the new read. *Change Domain Controller...*,
disconnecting and closing the window drop the read from memory.

## 3. The "Public Key Services" overview

The header gives a summary (e.g. *2 enterprise CAs · 26 templates · 1 trusted root CAs · 1 NTAuth certificates*) and
where and when the data was read (*Read from dc01.contoso.test at 10/13/2026 2:00 PM.*). The overview reads with one
paged search of `CN=Public Key Services,CN=Services,CN=Configuration,…` including the security descriptors. *Refresh*
(F5) reads again.

The **What the read observed** panel says what the read could not see – AD silently leaves out the objects the account
cannot read:

| Note | Meaning |
|---|---|
| *The Public Key Services container was not found – no AD CS in this forest* | a forest without AD CS (e.g. the Samba lab); the overview is empty |
| *Access to the Public Key Services container was denied – the PKI was not evaluated* | the window's account cannot read the container; nothing is assessed as "safe" |
| *A PKI object is not visible – it is missing or cannot be read* | a well-known container is missing (`CN=NTAuthCertificates`, `CN=Enrollment Services`, `CN=Certificate Templates`, `CN=Certification Authorities`, `CN=AIA`, `CN=CDP`, `CN=OID`, `CN=KRA`) |
| *The template's security descriptor could not be read – its rights were not evaluated* | no right to read the template's permissions; such a template is never treated as safe |
| *A certificate value is not valid DER* (and the same for a CRL) | the row stays, but without details |
| *The computer object of the CA server was not found* | the CA has no *Open computer* and *Remote actions…* |
| *The CA server is in another domain – its SID is unknown, so it counts as an account outside Tier 0* | a child domain window, the CA in the root domain (chapter 11) |
| *A trustee or a linked group could not be resolved in this window (another domain of the forest)* | a right or a group from another domain of the forest – the account count is unknown |

A transient controller error (server unavailable, busy) is a read error, not the message "no AD CS in this forest".

### 3.1 Enterprise CAs

The `CN=Enrollment Services` objects – columns *Name*, *Host*, *Valid to*, *Status*, *NTAuth* and *Templates*. The
details pane of the selected CA:

- **CA certificate** – subject, issuer, validity, public key, signature algorithm, serial number, thumbprint (SHA-1) and
  SHA-256, subject alternative name, intended purposes, CRL distribution points and authority information access; the
  number of certificates in `cACertificate` (renewals included – the newest one is shown).
- **Certification path** built only from the CA certificates stored in AD (issuer = subject, authority key identifier =
  subject key identifier): *(trusted root CA in AD)* at the end or *(the issuer's certificate is not in AD)*. Nothing is
  downloaded from the network.
- **Certificate templates to issue** (`certificateTemplates`) with the *Open template* button.
- **Certificate enrollment web services (CES)** from `msPKI-Enrollment-Servers`.
- **Open computer** and **Remote actions…** (K1, [COMPUTERS.md](COMPUTERS.md)) for the CA host – by the computer object
  found in the window's domain (by `dNSHostName`). A host from another domain of the forest: *The CA host belongs to
  another domain of the forest: open it from a window of that domain.*

### 3.2 Trusted CAs and NTAuth

The certificates of `CN=Certification Authorities` (*Trusted root CA*), `CN=AIA` (*Intermediate CA (AIA)*, *Cross
certificate (AIA)*) and `CN=NTAuthCertificates` (*NTAuth*) – columns *Store*, *Subject*, *Issuer*, *Valid to*, *Status*,
*Enterprise CA* (matched by the certificate's hash, not by name) and *Thumbprint*; details as in 3.1.

The NTAuth summary: *NTAuth certificates: 1 · enterprise CAs in NTAuth: 1 of 2*. An enterprise CA is in NTAuth when the
store holds any of its certificates. When a CA publishes logon templates (client authentication) but is not in NTAuth,
the overview warns: *Kontoso Główny CA issues logon certificates (Logowanie-Kartą) but is not in NTAuth: smart-card and
certificate logon with its certificates fails.*

### 3.3 CRLs

The lists of `CN=CDP` – columns *CA*, *Host*, *Kind* (*Base CRL*, *Delta CRL* with the number of its base CRL,
*Authority revocation list (ARL)*), *Number*, *Effective date*, *Next update*, *Revoked* and *Status*. A list is
assigned to its CA by issuer and key identifier, never by the container's name (`certutil` changes the CA names in
`CN=CDP` and `CN=AIA`). Revoked certificates are only counted – P1 does not show their serial numbers.

### 3.4 Key recovery agents and OIDs

- **Key recovery agents** (`CN=KRA`): *Agent*, *Subject*, *Issuer*, *Valid to*, *Status*. Key recovery is outside the
  scope of the program.
- **OIDs** (`CN=OID`): *Display name*, *Kind* (*Certificate template*, *Issuance policy*, *Application policy*,
  *Other*), *OID* and *Linked group* (`msDS-OIDToGroupLink`) with the *Open group* button. An issuance policy linked to
  a group puts that group into the Kerberos ticket of whoever logs on with such a certificate – see ESC13 in chapter 5.

### 3.5 Health

The **Health** tab assesses the PKI the way *Enterprise PKI* (`pkiview.msc`) does, for all CAs at once. Columns *CA*,
*Item*, *Status*, *Valid to*, *Location* and *Result* (the result of the HTTP check). Rows for every enterprise CA: *CA
certificate* (the newest one of `cACertificate`), the *AIA location* and *CDP location* of its certificate (where the
certificate and the CRL of its issuer are), its *Base CRL* and *Delta CRL* from `CN=CDP` and the *Delta CRL location*;
in addition the key recovery agent certificates and the NTAuth certificates.

| Status | Certificate | CRL |
|---|---|---|
| *OK* | valid, with more than the threshold left | at least `max(1 day, 20 % of the CRL's validity)` left until the next update |
| *Expiring* | at most as many days left as the threshold: 180 days for CA certificates, 30 days for the others (chapter 9) | less than `max(1 day, 20 %)` left |
| *Expired* | after `NotAfter` | after `nextUpdate` |
| *Unavailable* | object missing, value unreadable, a location without the issuer's certificate or a failed HTTP check | no CRL, a location without this CA's CRL or a failed HTTP check |
| *Unknown* | before `NotBefore` or a location that was not checked | a CRL without `nextUpdate` (never *Expired*) or before its effective date |

Locations:

- `ldap:///CN=…` (no host) – checked against what was read: a found object has its own state, a missing object or an
  object without this CA's certificate or CRL is *Unavailable*; a location outside `CN=Public Key Services` is
  *Unknown*.
- `ldap://host/…`, `file:` and UNC paths – *Unknown* (not checked): the program does not connect to another server.
- `http://` and `https://` – *Unknown* until the operator presses *Check HTTP URLs* (3.6).
- OCSP has no health row (as in `pkiview.msc`) – it is only on the *HTTP URLs* list.

Above the list: a summary (*OK: 9 · expiring: 1 · expired: 0 · unavailable: 1 · unknown: 4*) and the thresholds used.
The states are computed **when shown** from the read, the current thresholds and the clock: every time the tab is
selected, after F5 and after the thresholds change in the settings – without reading the directory again.

### 3.6 Checking the HTTP URLs

The `http` and `https` URLs of the certificates (AIA – CA issuers, CDP – base CRL, delta CRL, OCSP) are checked **only
when the operator presses *Check HTTP URLs*** on the *Health* tab. Nothing is fetched otherwise.

- No credentials, cookies or pre-authentication reach the target server. When the workstation uses a proxy server, the
  requests go through it, and only the proxy gets the credentials of the Windows account.
- At most 3 redirects – each target checked by the same rules; 15 s per URL including the body; a body of up to 20 MB; 4
  URLs at a time.
- `file:`, UNC paths (they would send NTLM to the named host), `ldap://` (another controller) and URLs with a user name
  and password are never contacted: *Not checked (not an http/https URL)*.
- An AIA body must be a certificate (DER, PEM or a PKCS #7 bundle – its first CA certificate counts), a CDP body a CRL;
  for OCSP only reachability counts: every HTTP response is *OK* with its status code (P1 sends no OCSP request).
  Building the HTTPS chain downloads nothing.
- Results: the **HTTP URLs** list with the columns *Kind*, *Location* and *Result* (*OK*, *Not found (404)*, *HTTP
  error*, *No answer within 15 s*, *Larger than 20 MB*, *Not a certificate or CRL*, *The HTTPS handshake failed*, *The
  host name does not resolve*, *The connection failed*, *Not checked (not an http/https URL)*), and the health rows get
  their states. The fetched body is discarded right after it has been read; the results stay in the window's memory
  until F5.

### 3.7 LDAPS certificates of the domain controllers

The **LDAPS certificates** tab checks the certificate every domain controller of the window's domain presents on its
LDAPS port:

- **When:** the first time the tab is opened, when *Check LDAPS certificates* is pressed and – once the tab has been
  opened, while the overview stays open – after F5 (in any PKI document of the window: F5 drops the probe results with
  the read) and after a change of the domain controller. Never in the background.
- **What:** a TCP connection to port 636 of every domain controller (and 3269 on global catalogs) and **only the TLS
  handshake** – no LDAP bind, no client certificate (the controller asks for one – the program offers none) and no
  application data. At most 4 controllers at a time, 5 s each; the window's controller first. The target is the name
  from `dNSHostName`, never an IP address.
- **Columns:** *Domain controller*, *Port* (*636* or *3269 (global catalog)*), *Result* (*LDAPS certificate presented*,
  *Host name not resolved*, *Port closed*, *DC without an LDAPS certificate (the TLS handshake failed)*, *No answer
  within 5 s*, *Not a valid host name*), *Checks*, *Valid to* and *KDC certificate*.
- **Checks** of the presented certificate: the name matches the controller (a DNS entry of the subject alternative name,
  without one the common name; a wildcard only as the whole first label), the domain name in the SAN (a warning only:
  *the domain name is not in the SAN (ldaps://contoso.test fails)*), the Server Authentication EKU (or no EKU), the
  validity (the threshold for certificates, not for CAs), a chain to a root CA published in AD and the issuer in NTAuth.
  The chain is built only from the certificates read from AD – without downloads and without the Windows certificate
  store, so the automatic root certificate update of Windows does not download anything either.
- **KDC certificate** is an indicator from the certificates published in `userCertificate` of the domain controller
  object (not from the LDAPS certificate and not from the KDC's own store – stage P4 reads that): *Ready (from the
  certificates published in AD)*, *No KDC Authentication EKU* (e.g. a certificate of the version 1 *Domain Controller*
  template: PKINIT works, but not with strict KDC validation), *Issuer not in the NTAuth store*, *Expired*, *Unknown –
  no certificate published in AD*.

## 4. Certificate templates

A list of all `CN=Certificate Templates` objects with the columns:

| Column | From |
|---|---|
| *Display name*, *Template name (cn)* | `displayName`, `cn` |
| *Schema*, *Version* | `msPKI-Template-Schema-Version` (1–4; missing = 1), `revision` and `msPKI-Template-Minor-Revision` |
| *Intended purposes* | the application policies of templates of version 2 and later (the CA issues the certificate from them), otherwise `pKIExtendedKeyUsage`; none = *Any purpose (no EKU)* |
| *Validity period*, *Renewal period* | `pKIExpirationPeriod`, `pKIOverlapPeriod` – in the largest unit that divides the period exactly: years (365 days), months (30 days), weeks, days, hours – as in `certtmpl.msc` |
| *Minimum key size, provider* | `msPKI-Minimal-Key-Size`, `pKIDefaultCSPs` (in priority order) |
| *Subject name* | *Supply in the request* or *Build from Active Directory information* |
| *CA manager approval*, *Authorized signatures* | the `CT_FLAG_PEND_ALL_REQUESTS` flag, `msPKI-RA-Signature` (missing = 0) |
| *Publish in AD*, *Superseded templates*, *Issuance policies* | `CT_FLAG_PUBLISH_TO_DS`, `msPKI-Supersede-Templates`, `msPKI-Certificate-Policy` |
| *Issued by* | the enterprise CAs that list the template in `certificateTemplates`; otherwise *not issued by any CA* |
| *Who can enroll* | the summary of 4.2 |

Above the list: the search (Ctrl+F; name, OID, purpose, CA, trustee), the *Only templates no CA issues* box and the
summary *26 templates, 2 not issued by any CA*. The details pane of the selected template has the parts *General*
(template OID, application policies, extended key usage (`pKIExtendedKeyUsage`), policies an authorized signature must
carry, cryptographic providers), *Flags*, *Who can enroll* and *Who can change the template*. *Copy details* copies the
template's description to the clipboard, *Refresh* (F5) reads again.

### 4.1 Flags

Four groups: *Subject name (msPKI-Certificate-Name-Flag)*, *Enrollment (msPKI-Enrollment-Flag)*, *Private key
(msPKI-Private-Key-Flag)* and *General (flags)*. Every bit that is set is a sentence in the UI language, e.g. *The
enrollee supplies the subject name in the request* or *The SID security extension (szOID_NTDS_CA_SECURITY_EXT) is not
added*; the technical name of the bit (`CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT`, `CT_FLAG_NO_SECURITY_EXTENSION` …) is in the
tooltip. General bits that the protocols ignore are marked *(obsolete, ignored)*. Bits the MS-CRTD specification does
not describe are shown as *Unknown bits: 0x…* (this includes `0x80` and `0x200` of the enrollment flag, which Certipy
lists). The private key fields *Minimum CA version* and *Minimum client version* are numbers, not flags.

### 4.2 Who can enroll and who can change a template

The rights are read from the template's DACL **the way Windows evaluates them** (AccessCheck): each right is decided by
the first ACE in the order of the list that applies to the account and covers that right – a deny ACE as well;
inherit-only ACEs and ACEs for other object classes are skipped, generic rights (GENERIC_*) are expanded.

- **Who can enroll:** *Enroll* and *Autoenroll* (also through *Full control*; *Autoenroll* alone does not enroll –
  *Enroll* is needed as well).
- **Who can change the template:** *Full control*, *Modify permissions*, *Modify owner*, *Write all properties*, *Write
  a security-relevant property* (for each of the nine attributes `msPKI-Certificate-Name-Flag`, `msPKI-Enrollment-Flag`,
  `pKIExtendedKeyUsage`, `msPKI-Certificate-Application-Policy`, `msPKI-RA-Signature`, `msPKI-RA-Application-Policies`,
  `msPKI-RA-Policies`, `msPKI-Certificate-Policy`, `msPKI-Template-Schema-Version` separately), *Write another property*
  and *Owner*. Rights nobody holds are left out.

First – at once – the trustees of the ACL are shown (names from AD). Then, in the background, **one pass over the
accounts of the domain** (*Counting accounts…*) expands the group memberships, nested and primary ones included: every
right and trustee gets the number of accounts (*412 accounts (+ 9 disabled)*), the first 20 accounts and an *In total*
row. There is one pass per window – the security review and the readiness report use it too – and it ends when the
window is closed or the controller changes. When counting fails, the trustee view stays with the reason.

Every trustee has a **class**, which decides the assessment in the security review:

| Class | Who |
|---|---|
| *Tier 0 / infrastructure* | the Tier 0 groups of the program's catalog and every account or group that belongs to them (nested too); SYSTEM, ENTERPRISE DOMAIN CONTROLLERS, SELF, CREATOR OWNER, Administrators; in every domain of the forest (by RID): the Administrator account (500), Domain Admins (512), Domain Controllers (516), Cert Publishers (517), Schema Admins (518), Enterprise Admins (519), Read-only Domain Controllers (521), Enterprise Read-only Domain Controllers (498), Key Admins (526), Enterprise Key Admins (527); the CA's computer account |
| *Broad group* | Everyone, Authenticated Users, ANONYMOUS LOGON, This Organization, INTERACTIVE, NETWORK, the built-in Users and Guests groups, and Domain Users (513), Domain Guests (514) and Domain Computers (515) of every domain of the forest |
| *Account or group outside Tier 0* | every other trustee |

The name of an account or group with the *Open* button opens its property sheet (all members of a group are on its
*Members* tab). A broad group shows the count only (*A broad group: practically every account holds it.*). A trustee
from another domain of the forest has *count unknown (another domain)*. A template whose descriptor could not be read
shows *The template's security descriptor could not be read: its rights are not evaluated (never treated as safe).*

### 4.3 Comparison and export

- **Compare…** opens the *Compare templates* pane inside the document: two templates selected in the list (Ctrl+click)
  or the selected one and a second one chosen in the pane (*with*). The rows in which they differ are highlighted;
  *Differences only* hides the others, *Differences: N* gives their number, *Close the comparison* closes the pane.
- **Export to CSV…** and **Export to HTML…** write the shown (filtered) templates to a file the operator picks. The CSV
  is protected against Excel formulas. The HTML report is a self-contained page (no scripts and no resources from the
  network) with the footer: *This report holds template names, OIDs, purposes and account names and counts of the
  directory. Keep it with the same care as the directory itself.*

## 5. AD CS security review

The **AD CS security review** document looks for configurations open to the known attacks on AD CS. When opened it uses
the window's PKI read and account count, reads the permissions of the PKI objects (containers, NTAuth, OID objects,
enrollment services, CA objects, the CA computer objects and Tier 0 accounts), resolves the accounts and groups and then
evaluates the rules – the document shows which stage is running. *Cancel* stops it (the rows shown stay), *Check again*
(F5) reads everything again. The scope is always shown above the list:

> Reads the templates, the PKI objects and the accounts over LDAP (ESC1–5, 9, 13, 14, 15, 17) and changes nothing.
> Assumes every CA grants Enroll to Authenticated Users; the CA's own settings (ESC6, 7, 8, 10, 11, 16) are checked in
> stage P4.

A default Windows PKI raises no finding (*No vulnerable configuration found – a default Windows PKI raises no
finding.*). A default, updated CA adds the SID extension to the certificates whose subject is built from AD, so a
default forest raises neither ESC9 (the template flag is clear) nor ESC16 (P4). In a forest without AD CS the list says
*There is no AD CS in this forest – nothing to review.*

### 5.1 Severities, filters and details

- **Severities:** *Critical*, *High*, *Medium*, *Low*, *Information*; the tiles with counters (*Critical: 2*, *High: 1*
  …) filter by severity.
- **Filters:** *Severity*, *Technique* (the ESC number), *Area* (*Certificate template*, *Certification authority*, *PKI
  object*, *Account*), *Template* (related templates too – an enrollment agent finding is shown with its target
  templates) and the search (*Search the findings (title, template, account, ESC)*, regardless of diacritics); *Clear
  filters*.
- **The list** is grouped by object (*Certificate template: Użytkownik-VPN-Podatny*), the groups by their most severe
  finding first. Columns: *Severity*, *ESC*, *Object*, *Accounts and groups*, *Finding*.
- **Details** of the selected finding: *Condition*, *Attack path*, *What to do*, *Affected object*, *Accounts and
  groups* (with the class, the right and the number of accounts; *another domain – number of accounts not known*),
  *Notes*, *Related objects* and *Identifiers* (*Certipy: ESC1 · Locksmith: ESC1 · MDI: –*).

### 5.2 Checks and finding codes

The enrollment rules (ESC1, 2, 3, 9, 13, 15, 17) concern a template **in which a trustee outside Tier 0 can enroll** –
it holds an effective *Enroll* right (also through *Full control* or all extended rights; *Autoenroll* alone is not
enough). The severity follows the most dangerous class: a broad group gives the highest severity of the rule, another
account or group *Medium*; ESC13 and ESC15 take the severity of the table whatever the class. Tier 0 trustees raise no
finding. The "Approval" column says what CA manager approval or a required authorized signature does: it suppresses the
finding, lowers it by one level (with a note) or does not protect (the attacker changes the template or the account).

| ESC | Finding code | Locksmith | When there is a finding | Severity | Approval |
|---|---|---|---|---|---|
| ESC1 | `EnrolleeSuppliesSubjectClientAuth` | ESC1 | the enrollee supplies the subject (*Supply in the request*) and the certificate allows client authentication (also smart-card logon, PKINIT, any purpose) | broad group: Critical, other: Medium | suppresses |
| ESC2 | `AnyPurposeOrNoEku` | ESC2 | the certificate is valid for any purpose (*Any Purpose* or no EKU) | Critical / Medium | suppresses |
| ESC3 | `EnrollmentAgentTemplate` | ESC3 | the template issues a Certificate Request Agent certificate; the *Related objects* list – besides the CAs that publish it – the templates that accept an agent's signature | Critical / Medium | suppresses |
| ESC3 | `EnrollmentAgentTargetTemplate` | ESC3 | a template of schema version 2 or later requires an enrollment agent's signature and allows client authentication | High with an agent template a broad group can use, Medium with another one, Low without an agent template | only manager approval suppresses |
| ESC4 | `TemplateWriteable` | ESC4 | a trustee outside Tier 0 holds *Full control*, *Modify permissions*, *Modify owner*, *Write all properties* or *Write a security-relevant property*, or owns the template; *Write another property* alone – Low | Critical / Medium | does not protect |
| ESC5 | `PkiObjectWriteable` | ESC5 | a trustee outside Tier 0 can change a PKI container, the NTAuth store, an OID object, an enrollment service, a CA object in `CN=AIA` / `CN=CDP` / `CN=Certification Authorities` or the CA's computer object (full control, permissions, owner, attribute writes, creating child objects, password reset, reading the LAPS password, resource-based constrained delegation …) | Critical / Medium | does not protect |
| ESC9 | `NoSecurityExtensionClientAuth` | ESC9 | a template with `CT_FLAG_NO_SECURITY_EXTENSION` (`0x80000`) allows client authentication | High / Medium | lowers |
| ESC13 | `IssuancePolicyLinkedToGroup` | ESC13 | an issuance policy of the template is linked to a group (`msDS-OIDToGroupLink`) and the certificate allows client authentication | Tier 0 group: Critical, other: High | lowers |
| ESC14 | `WeakExplicitMapping` | – | a Tier 0 account has a weak explicit certificate mapping (High); weak mappings of the other accounts – one aggregated finding (Low); a trustee outside Tier 0 can write `altSecurityIdentities` of a Tier 0 account (Critical / Medium) | up to Critical | does not protect |
| ESC15 | `SchemaV1EnrolleeSuppliesSubject` | ESC15/EKUwu | a template of schema version 1 with an enrollee-supplied subject (CVE-2024-49019) | Medium | lowers |
| ESC17 | `EnrolleeSuppliesSubjectServerAuth` | ESC17 | the enrollee supplies the subject and the certificate allows server authentication (also any purpose) | High / Medium | suppresses |

The finding code is fixed and not translated – it goes into the CSV export (the `Code` column) and lets you compare
results between runs. The *MDI* column (the Microsoft Defender for Identity assessment identifier) stays empty until a
real identifier is confirmed.

### 5.3 Notes

| Note | Meaning |
|---|---|
| *No enterprise CA publishes the template (exploitable once it is published)* | one level lower (never below *Information*) |
| *Assumes the CA grants Enroll to Authenticated Users (the CA's own security is read in stage P4)* | on every enrollment finding: P1 does not read the CA's own security |
| *Depends on the CA's November 2024 update (checked in stage P4)* | ESC15 – the attack does not work on an updated CA |
| *Manager approval or an authorized signature is required – one level lower* | ESC9, ESC13 and ESC15 on a template with approval |
| *pKIExtendedKeyUsage and the application policies differ* | the rule holds on only one of the two EKU sets (5.4) |
| *A write right on an object type nothing names (counted as security-relevant)* | ESC4: an ACE with an object type neither the schema nor the program knows – counted, to be safe, as dangerous |
| *No enrollment agent template others can use* | ESC3: a target template without an available agent template – severity *Low* |

Approval is applied first, then the unpublished template.

### 5.4 Differences from `certipy find -vulnerable` and Locksmith

The rules were compared with Certipy (commit 3ae3442) and Locksmith (commit 8b555bd). The review must find everything
both tools find for the ESCs of the LDAP list; the differences are deliberate:

- **Both EKU sets.** Certipy and Locksmith read only `pKIExtendedKeyUsage`, while the CA issues templates of version 2
  and later from the application policies (`msPKI-Certificate-Application-Policy`). The review checks both sets and
  raises a finding when the rule holds on either; when the result depends on the set, it adds the note
  *pKIExtendedKeyUsage and the application policies differ*.
- **Approval for ESC9, ESC13 and ESC15.** Certipy leaves these findings out when the template requires CA manager
  approval or an authorized signature; Locksmith always reports them. The review reports them one level lower with a
  note. For ESC1, ESC2, ESC3 and ESC17 approval suppresses the finding – as in both tools.
- **Graded ESC4.** Certipy reports only a write of all properties, Locksmith every write right except *Enroll* and
  *Autoenroll*. The review reports both cases: a write of all properties, of a security-relevant property or of an
  object type nothing names at the full severity, a write of another property as *Low*.
- **ESC15** follows Certipy's condition (schema version 1 and a subject from the request). Locksmith reports every
  version 1 template in which someone outside Tier 0 can enroll – on a CA without the update the requester of any such
  template can also add the *Certificate Request Agent* application policy (EKUwu → ESC3). The review keeps Certipy's
  condition so that the default *User* template raises no finding.
- **ESC17** follows Certipy's definition: server authentication or any purpose. Locksmith checks only the Server
  Authentication OID (`1.3.6.1.5.5.7.3.1`).
- **ESC1 and ESC2 together.** A template with a subject from the request and any purpose (or no EKU) raises both
  findings (as well as ESC3 and ESC17) – as in Certipy; the list groups them under one template.
- **ESC3 targets.** A template of schema version 1 with client authentication (e.g. the default *User*) is only a
  related target of the enrollment agent finding ("ESC3 Target Template" in Certipy), never a finding of its own.
- **Safe trustees** are Locksmith's `SafeUsers` set extended with the program's catalog of Tier 0 groups and the
  accounts that belong to them – which is why a default Windows PKI raises no finding.
- **The CA's security.** P1 does not read the *Enroll* right on the CA itself (P4) and assumes the default
  *Authenticated Users*; the note on every enrollment finding says so.
- **Unpublished templates** are reported one level lower with a note – they become dangerous only once published.
- **The `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT_ALT_NAME` flag (`0x10000`) alone** does not raise ESC1 – as in Certipy and
  Locksmith.
- **ESC6, ESC7, ESC8, ESC10, ESC11 and ESC16** need a read of the CA or of the domain controllers – stage P4 checks
  them; ESC12 is outside the scope of the program. The P1 review does not show these findings.
- **Trustees from another domain of the forest** are shown without an account count (*another domain – number of
  accounts not known*), and the account count in the message then has the form `N+`.

### 5.5 Navigation, copying and export

- *Open template* goes to the *Certificate Templates* document, *Open CA* to the overview with the CA selected, and
  *Open account*, *Open computer* and *Open object* open the property sheet (the accounts of ESC14 findings, the objects
  and CA computers of ESC5).
- *Copy* copies the selected finding to the clipboard.
- *Export to CSV…* and *Export to HTML…* write the **filtered** findings to a file the operator picks. The CSV
  (protected against formulas) has the columns severity, ESC, area, object, title, condition, attack path,
  recommendation, accounts and groups, notes, related objects, the Certipy, Locksmith and MDI identifiers and the
  finding code. The HTML report is a self-contained page with the filter line and the footer: *The report contains names
  of your directory objects. Store and share it like any other administrative document.*
- **Nothing is repaired:** *The review changes nothing: make the change in the Certificate Templates console
  (certtmpl.msc) or in the object's permissions. Repairs through the change plan come in stage P3.*

## 6. Strong mapping and NTAuth readiness

A separate PKI document (this is not *Check environment readiness…* of stage S). It answers the question **who will not
be able to log on with a certificate** under full enforcement of strong certificate mapping (KB5014754) and without the
CA in NTAuth. The rows have the status *OK*, *Warning*, *Problem* or *Not checked*, a description and *What to do*;
above the list are the status counters.

| Area | What it checks | Statuses |
|---|---|---|
| *Explicit certificate mappings (altSecurityIdentities)* | the accounts with mappings found by the account count (Tier 0 accounts first) | a weak mapping on an enabled Tier 0 account – *Problem*; on another account – *Warning*; only invalid values – *Warning*; accounts with strong mappings only – one *OK* row; Kerberos names are ignored |
| *Authentication templates and the SID extension* | published templates with client authentication in the application policies or in `pKIExtendedKeyUsage` | `CT_FLAG_NO_SECURITY_EXTENSION` – *Problem* (ESC9); the enrollee supplies the subject – *Warning* (the CA adds no SID, logon needs a strong explicit mapping); the others – one *OK* row |
| *Enterprise CAs and the NTAuth store* | every enterprise CA | in NTAuth – *OK*; outside NTAuth while publishing logon templates – *Problem* (with the list of templates); outside NTAuth without logon templates – *OK* |
| *LDAPS certificates of the domain controllers* | the result of the check of chapter 3.7 | a wrong name, no server authentication, not valid, port 636 closed or a failed handshake – *Problem*; expiring within the threshold, no chain to a CA in AD, no domain name, port 3269 closed, no answer, an unresolved name – *Warning*; not checked – *Not checked* |
| *KDC certificates (published on the domain controller objects)* | the certificates in `userCertificate` of every domain controller | ready – *OK* (expiring within the threshold – *Warning*); no KDC Authentication EKU – *Warning*; issuer outside NTAuth or expired – *Problem*; no published certificate – *Not checked*; read-only domain controllers are marked |
| *KDC events* | the events 39, 40, 41, 45 and 21 of the domain controllers | always *Not checked* – the event analysis comes in stage P5; until then filter the *System* log of every domain controller in the event log reader ([EVENT-LOGS.md](EVENT-LOGS.md)) |

- **Check LDAPS certificates** – the report connects to the domain controllers **only when this button is pressed** (the
  same TLS handshake as in 3.7). Otherwise it shows the result of an earlier check in the overview or *Not checked*.
- *Read again* (F5) reads the PKI objects, the domain controllers and the accounts again.
- The rows have the buttons *Open account*, *Open template* and *Open CA*. Each area shows up to 100 rows (*… and N
  more*); *Copy* and *Export to CSV…* include all of them.
- The thresholds and the clock apply when shown – changing the thresholds in the settings rebuilds the report without
  reading.

## 7. Property tabs on accounts

Three read-only tabs – nothing can be changed on them (editing comes in P3). A tab is shown only when the schema knows
its attribute.

| Tab | User (and `inetOrgPerson`) | Computer | Contact |
|---|---|---|---|
| *Published Certificates* | after *Organization* | after *Status* | yes |
| *Name Mappings* | yes | yes | – |
| *Key Credentials* | yes | yes | – |

The tabs are in the order of the table, before the *Member Of* tab.

### 7.1 Published Certificates

The certificates of `userCertificate`, then of `userSMIMECertificate` (the PKCS #7 bundles Outlook stores are unpacked
to the user's certificates). The tab reads them only once it is opened; *Refresh* reads again.

- Columns: *Issued To* (the common name, without it the UPN, the e-mail address or the first DNS name of the SAN),
  *Issued By*, *Intended Purposes* (the names of known EKUs, otherwise the OID; no EKU = *All (no EKU)*), *Valid To*,
  *Status* (*Valid*, *Expiring (N days left)* – by the threshold for certificates –, *Expired*, *Not yet valid*),
  *Template*, *Thumbprint* (SHA-1) and *Attribute*.
- *Certificate details*: subject, issuer, serial number, valid from and to, intended purposes with their OIDs, template,
  the user principal name, e-mail address and DNS names of the SAN, the SID from the SAN URL (strong mapping) and from
  the security extension, public key, signature algorithm, SHA-1 and SHA-256.
- The template's name comes from the template extension in the certificate; the program reads the display name from
  `CN=Certificate Templates` on the window's controller (one search).
- **Copy to File…** writes the certificate (DER, `.cer`) to a file the operator picks; **Copy thumbprint**.
- A value that is not a certificate has the row *Unreadable value (not a certificate)*.

### 7.2 Name Mappings

The explicit certificate mappings of `altSecurityIdentities` with their strength under KB5014754:

| Form | Kind | Strength |
|---|---|---|
| `X509:<I>…<SR>…` | *Issuer and serial number* (the serial number in reversed byte order) | *Strong* |
| `X509:<SKI>…` | *Subject key identifier* | *Strong* |
| `X509:<SHA1-PUKEY>…` | *SHA1 hash of the public key* | *Strong* |
| `X509:<I>…<S>…` | *Issuer and subject* | *Weak* |
| `X509:<S>…` | *Subject only* | *Weak* |
| `X509:<RFC822>…` | *E-mail address (RFC822)* | *Weak* |
| anything else (e.g. `<SR>` without `<I>`) | *Not a valid mapping* | *Invalid* |

The *X.509 certificates* list has the columns *Mapping*, *Strength (KB5014754)*, *Issuer* and *Subject or identifier*
(for the form with a serial number: *Serial number …* in the certificate's byte order); a separate *Kerberos names* list
shows the `Kerberos:` values. Above the lists the warnings: *Weak mappings: N. Under full enforcement of strong
certificate binding (KB5014754) the domain controller refuses them…* and *Invalid values: N (the KDC ignores them).*
*Copy* copies the selected value as stored. The ADUC *Name Mappings* tab creates weak forms only.

### 7.3 Key Credentials

The key metadata of `msDS-KeyCredentialLink` (Windows Hello for Business, FIDO keys, device keys): *Key ID* (Base64),
*Device ID*, *Source* (*Active Directory*, *Microsoft Entra ID*), *Usage* (e.g. *Windows Hello for Business (NGC)*,
*FIDO security key*), *Created*, *Last logon (approximate)* and *Remarks* (*Unreadable entry*, *The key hash does not
match (the entry was changed)*). The tab never shows the key itself. The warning: *An entry nobody enrolled may be a
Shadow Credentials attack: whoever wrote it can log on as this account.* The buttons *Copy key ID* and *Copy device ID*.
The key history from the AdMng Watchdog service comes in stage P5.

## 8. Key material: the Attribute Editor and lists

These attributes are **never** shown, copied, edited or exported: `userPKCS12`, `msPKIAccountCredentials`,
`msPKIDPAPIMasterKeys`, `msFVE-KeyPackage`, `msDS-ManagedPassword`, `supplementalCredentials`,
`msLAPS-EncryptedPassword`, `msLAPS-EncryptedPasswordHistory`, `msLAPS-EncryptedDSRMPassword`,
`msLAPS-EncryptedDSRMPasswordHistory` and every other secret attribute (known by name or confidential in the schema)
that the schema gives a binary syntax.

- **Attribute Editor:** the value is `••••••••` (or *<not set>*), the row is read-only with a lock and the reason
  *Confidential value (key material): never shown, copied or edited*; the *Edit* and *Copy* buttons are disabled, the
  value editor does not open and no change of the attribute can be staged.
- **Lists:** the column chooser does not offer these attributes, and a column from a saved view shows the mask, which is
  all the CSV export writes; Ctrl+C leaves such a column out, as it does every secret column. In a list cell the mask
  goes by the names above (the column chooser offers no binary attribute anyway).
- **Operator-readable secrets** – the clear-text LAPS password, the BitLocker recovery password and confidential text
  attributes – work as before: masked, revealed deliberately and copied through the secure clipboard. Whether to mask
  them like the keys is a question for the team.
- `userPKCS12` is also a secret attribute for the AdMng Watchdog service: the service rejects a configuration that would
  watch it.

## 9. Settings

*Tools → Settings → General*, the **Certificates** section:

- **Certificate expiry warning (days)** – 30 by default: the LDAPS and KDC certificates of the domain controllers, the
  key recovery agent certificates and the certificates published on accounts.
- **CA certificate expiry warning (days)** – 180 by default (renewing a CA has to be planned).

Both values: 1–3650 days. A CRL has a fixed rule: it is *expiring* when less than 20 % of its validity, at least one
day, is left. The thresholds apply when shown: a change re-evaluates the states in the open documents at once, without
reading the directory. The settings contain no PKI data – only these two thresholds and the tree node toggle (*View*).

## 10. Privacy and network

| Connection | To | When | What is sent |
|---|---|---|---|
| LDAP (Kerberos, signing and encryption) | the window's domain controller | opening a PKI document, F5, opening a property tab | queries for the PKI objects, templates, accounts, groups and domain controllers |
| TLS handshake, ports 636 and 3269 | every domain controller of the window's domain | the *LDAPS certificates* tab (3.7): first opening, the button, F5 (in any PKI document of the window) and a change of the controller once the tab has been opened; the readiness report – only its button | only the TLS handshake records: no LDAP bind, no client certificate, no application data |
| HTTP and HTTPS | the AIA, CDP and OCSP URLs of the certificates in AD | only after *Check HTTP URLs* (3.6) | a GET request without credentials and cookies (the proxy gets the Windows credentials) |
| Remote actions (WinRM) | the CA host | only after *Remote actions…* (K1) | as in [COMPUTERS.md, chapter 4](COMPUTERS.md#4-remote-actions-winrm-kerberos) |
| Certification authority (DCOM, registry) | – | **never in P1** (from P4) | – |

None of these connections fetches anything by itself: building chains (the overview, the TLS probe, HTTPS) uses only the
CA certificates read from AD and reaches neither the AIA URLs nor the Windows certificate store.

**Data:**

- The PKI read, the account count (counts, the first 20 names, the accounts with mappings), the findings, the TLS probe
  results and the HTTP results are **only in the memory of the connection window** – shared by its PKI documents,
  dropped on a change of the domain controller, on disconnecting and on closing the window; F5 reads them again. The
  property tabs keep their values only while the property sheet is open.
- Only the **exports the operator asks for** reach the disk, in the file the operator picks: CSV and HTML of the
  templates and of the security review, CSV of the readiness report, `.cer` from the *Published Certificates* tab. The
  HTML reports have a footer about the directory data.
- The settings hold only the two thresholds and the node toggle. The PKI documents write nothing of their own to the
  diagnostic logs, and the P1 types print (`ToString()`) only kinds and counts – never a subject, thumbprint, DN, URL or
  serial number.
- P1 sends nothing to the AdMng Watchdog service.

## 11. Limitations and pilot

Limitations of this version:

- **No connection to the CA.** The *Enroll* right on the CA itself is assumed (*Authenticated Users*); the state of the
  November 2024 update (ESC15) and the CA configuration (ESC6, ESC7, ESC8, ESC11, ESC16) are unknown – P4. ESC12 is out
  of scope.
- **No read of the domain controllers.** The enforcement of strong mapping on the DCs
  (`StrongCertificateBindingEnforcement`, Schannel `CertificateMappingMethods`, ESC10) and the KDC's certificate store –
  P4/K1; the KDC indicator relies only on the certificates published in AD. The analysis of the KDC events 39, 40, 41,
  45 and 21 – P5.
- **OCSP** – reachability only, no OCSP request; of a PKCS #7 bundle at an AIA URL the first CA certificate counts. The
  health row of a CA concerns the newest certificate of `cACertificate`.
- **A child domain window:** the CA computers of the forest root domain are not resolved (*The CA server is in another
  domain…*), so their rights on the CA objects count as an account outside Tier 0; trustees from other domains of the
  forest have no account count; the TLS probe checks only the window's domain controllers.
- **Nothing is repaired or issued** – P3 (*Fix…*, editing templates, permissions, NTAuth, mappings, keys) and P2
  (issuance). The key history from the Watchdog on the *Key Credentials* tab – P5.
- The bits `0x80` and `0x200` of the enrollment flag are shown as unknown (MS-CRTD does not list them), and the
  `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT_ALT_NAME` flag alone does not raise ESC1 – to be reassessed in P3/P4.
- *Open* on a group opens its property sheet, not its *Members* tab directly. The readiness report has no HTML export
  and shows up to 100 rows per area.
- The WPF windows and the screenshots 36–39 are checked only by the Windows CI.

**To be confirmed in the pilot on Windows Server AD with AD CS** – the fake directory of the tests (CAs, templates with
Polish names, a deliberately vulnerable template for every ESC rule and a safe look-alike) and the Samba lab (no AD CS:
only empty PKI containers and an LDAPS probe of the `localhost` controller) cannot settle this; scenarios and results:
[PILOT.md, chapter 3.5](PILOT.md#35-certificate-services-p1):

- a health state consistent with `pkiview.msc` in a domain with at least two CAs (a root and an issuing CA, base and
  delta CRLs, `ldap:///` and `http://` locations);
- the properties and permissions of every template consistent with `certtmpl.msc` (flag sentences, periods, *Enroll* and
  *Autoenroll* with deny ACEs and nested groups), also on real AD values (negative `msPKI-*` masks,
  `msPKI-RA-Application-Policies` of version 4 templates, `CN=OID` as an `msPKI-Enterprise-Oid` object, the `0x00`
  placeholder values, `crossCertificatePair`, CA names changed by `certutil`);
- on deliberately vulnerable templates the review finds everything `certipy find -vulnerable` and Locksmith find
  (differences as in 5.4), and a default Windows PKI – also from a child domain window – raises no finding and no
  delegation on the *Privileges* tab;
- the TLS probe on Windows controllers: the client certificate request in the SChannel handshake, a controller without
  an LDAPS certificate (*DC without an LDAPS certificate*), port 3269 of a global catalog, an RODC, a chain to the CA
  from AD without internet access;
- the KDC indicator against the certificate the KDC really uses (the *Kerberos Authentication*, *Domain Controller
  Authentication* and version 1 *Domain Controller* templates);
- the HTTP URL check through a proxy server and with real AIA, CDP and OCSP URLs;
- the account tabs against ADUC and `Get-ADUser`: S/MIME certificates stored by Outlook, mappings created by the ADUC
  tab (weak), Windows Hello for Business and Entra ID keys (identifiers and dates);
- the masking of `userPKCS12`, `msPKIAccountCredentials` and `msPKIDPAPIMasterKeys` in the Attribute Editor and in a
  saved view column on a Windows Server schema;
- the duration of the account count in a large domain (tests: 100,000 generated accounts in about a second).
