**English** · [Polski](../RESEARCH.md)

# Research Report – AD_MNG

A modern successor to the "Active Directory Users and Computers" console (ADUC, `dsa.msc`) in WPF/.NET 10.
We read data exclusively live from the domain controller over LDAP and keep no local copy of the directory. The most
important requirement is bulk editing.

| | |
|---|---|
| Date | October 13, 2026 |
| Basis | 7 research areas (ADUC; newer Microsoft tools; competing products; LDAP through S.DS.P; UI; security; bulk editing) and an architectural synthesis |
| Related documents | [`ARCHITECTURE.md`](ARCHITECTURE.md), architecture decisions in [`adr/`](adr/) |
| Client decisions | WPF/.NET 10 technology; PL + EN UI; priority: AD Recycle Bin and Exchange/mail attributes; configurable views for OU divisions |

**Conventions.** Names of attributes, classes, controls, OIDs, APIs and ADUC UI elements are left in the original
(English). The note **(to be verified in the lab)** marks a fact the researchers did not confirm in a primary source: it
was given from memory, came from a search-result snippet, or the sources contradicted each other. Before we base an
implementation on it, it has to be checked on a Windows Server domain controller (and, for commercial products, on a
trial version). Unconfirmed licensing and legal matters carry a shorter note, "to be verified".

**Limitations of the study.** From the sandbox it was not possible to open learn.microsoft.com or most vendor sites.
Microsoft documentation was therefore read from the MicrosoftDocs source repositories on GitHub (the same text as on
Learn), S.DS.P behavior from the dotnet/runtime code (branch `release/10.0`), and information about commercial
products from search-engine snippets. Toward the end of the work the search budget ran out. In the synthesis, two
disputed points were checked directly in source code: the WKGUID identifiers (the `ntdsapi.h` header from mingw-w64)
and the behavior of `LdapConnection.Abort()` in .NET 10.

**Sections:** [1 Summary](#1-summary) · [2 ADUC](#2-aduc-dsamsc--feature-inventory) · [3 Pain points](#3-aduc-pain-points-and-how-we-address-them) · [4 Microsoft tools](#4-adac-windows-admin-center-entra-admin-center-powershell-ad) · [5 Competition](#5-competing-and-open-source-products) · [6 LDAP](#6-ldap-techniques-for-live-fluidity) · [7 UI](#7-user-interface) · [8 Security](#8-security-and-credentials) · [9 Bulk editing](#9-bulk-editing--best-practices) · [10 Risks](#10-risks-and-how-we-mitigate-them) · [11 Questions](#11-open-questions-for-the-team) · [12 Sources](#12-sources)

---

## 1. Summary

1. ADUC (`dsadmin.dll` in MMC 3.0) is still the primary tool for working with AD in Windows Server 2025 and RSAT for
   Windows 11, although Microsoft has practically not developed it since 2008 R2; ADAC did not displace it, and the AD
   extension in Windows Admin Center has had "early preview" status since 2019.
2. ADUC is a lazily loaded domain tree, a list with a limit of 2000 objects and modal property sheets whose fields
   mostly map 1:1 to LDAP attributes – full parity is achievable with pure LDAP through
   `System.DirectoryServices.Protocols` (S.DS.P), without RSAT and ADWS.
3. The exceptions – "User cannot change password", "Protect object from accidental deletion", "Manager can update
   membership list" (ACE entries), the RDS tabs (binary `userParameters`) and country (`c`/`co`/`countryCode`) – require
   separate encoders tested against values written by ADUC.
4. The ADUC pain points (freezing on large OUs, the 2000 limit, no search-as-you-type, manual F5, editing many objects
   without preview and rollback, no credential switching) stem from the MMC/ADSI architecture, not from AD.
5. Responsiveness without a local copy comes from: a cancelable wrapper over S.DS.P based on `TaskCompletionSource`
   (not `FromAsync` – after `Abort()` the callback never arrives), paging with a small first page, VLV above about 2000
   objects, ANR with a 250 ms delay, ranged retrieval/ASQ for large groups, and constructed attributes.
6. A prototype on Samba 4.19 confirmed the order of magnitude (bind 16 ms, first page of 100 objects 65 ms, 428 objects
   174 ms, 20 parallel queries on one connection, working VLV and ASQ); this has to be repeated on Windows Server 2025
   with Kerberos and encryption.
7. ManageEngine ADManager Plus, Adaxes and Active Roles rely on an intermediary server, a service account and their own
   database (ManageEngine copies AD to PostgreSQL every 10 minutes), which breaks the "no local data" requirement and
   creates a new Tier-0 asset.
8. None of the products examined has a full pipeline (selection → diff preview → concurrency control → execution with a
   per-object report → rollback), so alongside the "zero infrastructure" model this is the main product differentiator.
9. All writes, including single ones, go through one change-plan engine: an atomic `ModifyRequest` per object, one
   pinned writable DC, CAS to detect conflicts, `PermissiveModify` for membership, adaptive throttling of DC load and an
   in-memory-only rollback log.
10. We connect with Kerberos with signing and encryption on 389, always to the DC's FQDN (LDAPS optional; simple bind and
    silent fallback to NTLM forbidden), which meets the default requirements of new WS2025 forests.
11. Other credentials work like `runas /netonly` inside the process (`LogonUserW` with `LOGON32_LOGON_NEW_CREDENTIALS`,
    bind in `RunImpersonated`, WhoAmI check), because `NetworkCredential` in S.DS.P keeps a plaintext copy of the
    password.
12. A permission-aware UI (`allowedAttributesEffective` and related) shows the help desk what it can change, but it is
    only a hint – the security boundary remains the ACLs in AD.
13. WPF on .NET 10 with the built-in Fluent theme is the best stack (dense data controls, mature UI Automation),
    provided the experimental `ThemeMode` (WPF0001) is isolated, compact styles are used and `DataGrid` bugs are worked
    around.
14. The client's priorities have a solid basis: restoring from the AD Recycle Bin "parent before child" removes the main
    ADAC limitation, and `mail`/`proxyAddresses` need uniqueness checks in the GC, which AD itself does not enforce.
15. Still open: the scale and topology of the environment, the presence of Exchange and the state of the AD Recycle Bin,
    the boundary of "local data", and the confirmation thresholds in bulk operations.

---

## 2. ADUC (dsa.msc) – feature inventory

### 2.1 State of the tool in 2026

- ADUC is `dsadmin.dll` in MMC 3.0. In Windows 11 it is delivered by the Feature on Demand
  `Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0`. Installation requires a local administrator, does not work in
  Windows Home, and native ARM64 support appeared only in the preview update of February 2026.
- The latest changes are minor: the Windows LAPS tab, a separately packaged BitLocker extension, Name Mappings in the
  strong `X509IssuerSerialNumber` format by default (since February 2024), and refusal to set a name-derived computer
  password on a WS2025 DC.
- The archived AskDS blog calls ADUC obsolete in favor of ADAC but gives no removal date. According to the Windows
  Admin Center FAQ, it does not replace RSAT for AD.
- ADUC reads everything live. Apart from saved-query definitions in the console file (`%APPDATA%\Microsoft\MMC\DSA` or
  a custom `.msc`) it stores no data, so feature parity can be reconciled with the "no local data" rule.

### 2.2 The tree and well-known containers (WKGUID)

The root of the tree is "Active Directory Users and Computers [*FQDN of the bound DC*]", and beneath it are "Saved
Queries" and the domain node (`domainDNS`). We do not resolve well-known containers by name (they can be moved or
redirected) but through `<WKGUID=hex,DC=…>` or the `wellKnownObjects` / `otherWellKnownObjects` attributes of the
domain naming-context head; e.g. `<WKGUID=a9d1ca15768811d1aded00c04fd8d5cd,DC=contoso,DC=test>` points to the Users
container regardless of its name.

| Node | Class | WKGUID | Notes |
|---|---|---|---|
| Builtin | `builtinDomain` | — | built-in domain local groups: Administrators, Account/Server/Print/Backup Operators, Remote Desktop Users, Remote Management Users |
| Computers | `container` | `aa312825768811d1aded00c04fd8d5cd` | **is not an OU** – a GPO cannot be linked; redirected with `redircmp` |
| Domain Controllers | `organizationalUnit` | `a361b2ffffd211d1aa4b00c04fd7d83a` | with the Default Domain Controllers Policy |
| ForeignSecurityPrincipals | `container` | `22b70c67d56e4efb91e9300fca3dc1aa` | `foreignSecurityPrincipal` objects named by SID; the "Readable Name" column is resolved by LSA |
| Managed Service Accounts | `container` | `1EB93889E40C45DF9F0C64D23BBB6237` (in `otherWellKnownObjects`) | MSA, gMSA, in WS2025 also dMSA; ADUC has no wizard for these accounts; GUID confirmed by search, absent from the header |
| Users | `container` | `a9d1ca15768811d1aded00c04fd8d5cd` | redirected with `redirusr` |
| System | `container` | `ab1d30f3768811d1aded00c04fd8d5cd` | advanced view |
| LostAndFound | `lostAndFound` | `ab8153b7768811d1aded00c04fd8d5cd` | advanced view |
| Infrastructure | `infrastructureUpdate` | `2fbac1870ade11d297c400c04fd8d5cd` | advanced view |
| Deleted Objects | `container` | `18e2ea80684f11d2b9aa00c04f79f805` | AD Recycle Bin (6.13) |
| Program Data / Microsoft Program Data | `container` | `09460c08ae1e4a4ea0f64aee7daa1e5a` / `f4be92a4c777485e878e9421d53087db` | advanced view |
| NTDS Quotas | `msDS-QuotaContainer` | `6227f0af1fc2410d8e3bb10615bb5b0f` | advanced view |
| Keys | `container` | `683A24E2E8164BD3AF86AC3C2CF3F981` (in `otherWellKnownObjects`) | (to be verified in the lab) |
| Organization OUs | `organizationalUnit` | — | the client's structure |

The GUIDs from `wellKnownObjects` were verified against the `ntdsapi.h` header (mingw-w64); the values for MSA and Keys
are read live.

### 2.3 Advanced view

**View › Advanced Features** shows objects with `showInAdvancedViewOnly=TRUE`, adds the Object, Security and Attribute
Editor tabs, the Name Mappings menu item and the "Protect object from accidental deletion" field.

| Container | Contents |
|---|---|
| LostAndFound (`lostAndFound`) | orphaned objects; the `lastKnownParent` column is useful |
| Program Data | `CN=Microsoft`, application data |
| System | AdminSDHolder, Policies (`groupPolicyContainer`), Password Settings Container (`msDS-PasswordSettings`, FGPP), MicrosoftDNS, RpcServices, WinsockServices, IP Security, DFSR-GlobalSettings, Domain System Volume, File Replication Service, ComPartitions/ComPartitionSets, WMIPolicy, Meetings, RID Manager$, `BCKUPKEY_*` secrets |
| NTDS Quotas (`msDS-QuotaContainer`) | `msDS-QuotaControl` objects (`msDS-QuotaTrustee`, `msDS-QuotaAmount`) |
| TPM Devices (`msTPM-InformationObjectsContainer`) | `msTPM-InformationObject` with `msTPM-OwnerInformation`; a computer points to it through `msTPM-TpmInformationForComputer` (WS2012+ schema) |
| Keys | created by the WS2016 domainprep; full control for Key Admins and Enterprise Key Admins; `msDS-KeyCredential` objects |
| Infrastructure (`infrastructureUpdate`) | `fSMORoleOwner` points to the infrastructure master |

A separate option, **View › Users, Contacts, Groups, and Computers as containers**, shows child objects of leaf
objects: `msFVE-RecoveryInformation` under computers, `printQueue` under print servers and Exchange ActiveSync device
containers under users.

### 2.4 The View menu, filter options and the 2000 limit

- **View**: Large Icons / Small Icons / List / Detail; "as containers"; Advanced Features; Filter Options; Add/Remove
  Columns; Customize. **Filter Options**: all types, selected types (Builtin domain, Computers, Contacts, Groups,
  InetOrgPerson, OUs, Printers, Shared folders, Users…) or a custom filter (a Find-style window with an LDAP tab).
- The same window has **"Maximum number of items displayed per folder" – 2000 by default**. Above the limit ADUC shows
  the first N objects and suggests raising the limit or filtering (Joe Richards described 2000 shown out of 15,048).
  Sorting by column header works on the client side, only on the loaded objects.
- `msDS-Approx-Immed-Subordinates` (WS2003+, base read only, error about 10%) gives an approximate number of children –
  we use it to choose the loading strategy and the "approx. 15k" marker.

### 2.5 Columns and list export

- By default Name, Type, Description. Add/Remove Columns combines built-in items with `extraColumns` from
  `organizationalUnit-Display` / `container-Display` (the set depends on the node class): among others
  First/Last/Display Name, User Logon Name, Pre-Windows 2000 Logon Name, E-Mail Address, Office, Business Phone, Job
  Title, Department, Company, City, State, Country/Region, Managed By, Modified, Created, Last Known Parent. The
  selection is saved in the console file.
- An arbitrary attribute requires editing `extraColumns` forest-wide, and a populated `extraColumns` **replaces** the
  default list for all administrators. In our app `extraColumns` is only a hint for the column catalog (we never write
  it), any schema attribute can be a column, and the column set is a user preference.
- **Action › Export List** saves the visible columns (optionally only the selected rows) to a text file with tabs or
  commas, in ANSI or Unicode.

### 2.6 Find, ANR and saved queries

- **Find**: Users, Contacts, and Groups; Computers; Printers; Shared Folders; Organizational Units; Custom Search;
  Common Queries. The "In" scope: domain, OU or **Entire Directory**, i.e. the Global Catalog (3268), which sees only
  attributes from the partial attribute set (PAS). The Advanced tab: a class-dependent field, a condition (Starts with /
  Ends with / Is exactly / Is not / Present / Not present), a value; Custom Search accepts a raw LDAP filter.
- **Common Queries**: name/description starts with or equals; "Disabled accounts"; "Non expiring passwords"; "Days
  since last logon" (30–180 days, from `lastLogonTimestamp`, which lags by 9–14 days).
- Results open in a separate window. **Known bug:** objects opened from Find results have no Attribute Editor tab or
  other advanced tabs; according to AskDS it will not be fixed because ADUC is obsolete.
- **ANR** (MS-ADTS 3.1.1.3.1.3.4): `(anr=value)` covers attributes with `fANR` (0x4) and an index (`fATTINDEX`, 0x1) in
  `searchFlags`, among others `displayName`, `givenName`, `sn`, `sAMAccountName`, `name`,
  `physicalDeliveryOfficeName`, `proxyAddresses`, `legacyExchangeDN`, `mailNickname`,
  `msDS-AdditionalSamAccountName`, `msDS-Phonetic*` (the exact default set – to be verified in the lab; the app derives
  it from the schema). A value containing a space is split at the first space and checked as `givenName`/`sn` in both
  orders; `anr==value` is an exact match.
- **Saved Queries**: folders, name, description, root, "Include subcontainers", a definition from the Find window;
  executed live, with no result caching; stored in the user's console file and shared via Export/Import Query
  Definition (XML). The wizard combines conditions with AND only. Importing these XML files (NetIQ DRA does this too)
  will ease migration.

### 2.7 Context actions by class

| Object | Actions in the context menu |
|---|---|
| Domain | Delegate Control, Find, Change Domain, Change Domain Controller, Raise domain functional level, Operations Masters, New, All Tasks, View, Refresh, Export List, Properties; with the BitLocker extension also "Find BitLocker recovery password" |
| OU | Delegate Control, Move, Find, New ›, All Tasks, View, Cut, Delete, Rename, Refresh, Export List, Properties |
| User / inetOrgPerson | Copy…, Add to a group…, Name Mappings… (advanced view only), Disable/Enable Account, Reset Password…, Move…, Open Home Page (`wWWHomePage`), Send Mail (`mail`), All Tasks › Resultant Set of Policy (Planning/Logging), Cut, Delete, Rename, Properties |
| Computer | Add to a group…, Disable Account, Reset Account, Move…, Manage (`compmgmt.msc /computer=<dNSHostName>`), All Tasks, Cut, Delete, Properties |
| Group | Add to a group…, Move…, Send Mail, All Tasks, Cut, Delete, Rename, Properties |
| Contact | Add to a group, Move, Send Mail, Cut, Delete, Rename, Properties |
| Several objects | Add to a group, Enable/Disable, Move, Delete, Cut, Send Mail, Properties (multi-object sheet) |

- **Rename** changes the RDN via ModifyDN; the user window also edits `givenName`, `sn`, `displayName`, UPN and
  `sAMAccountName`.
- **Move** is ModifyDN with `newSuperior`, only within a domain. Moving between domains requires ADMT.
- Deleting a container with children uses the Tree Delete control (`1.2.840.113556.1.4.805`).
- Drag and drop is allowed; since WS2008 it shows a warning that the user can turn off.

### 2.8 "New" wizards

The New menu on an OU: Computer, Contact, Group, InetOrgPerson, msDS-KeyCredential, msDS-ResourcePropertyList,
msDS-ShadowPrincipalContainer, msImaging-PSPs, MSMQ Queue Alias, Printer, User, Shared Folder, Organizational Unit.
Rarer classes are handled by a generic wizard that asks for attributes. The list depends on the schema, display
specifiers and `possSuperiors` (written from memory – to be verified in the lab).

| Wizard | Fields and behavior |
|---|---|
| User | Page 1: First name (`givenName`), Initials (`initials`), Last name (`sn`), Full name (`cn`/`name`, also feeds `displayName` according to `createDialog` from `user-Display`, e.g. `%<givenName> %<initials>. %<sn>`; only the tokens `%<givenName>`, `%<initials>`, `%<sn>` are supported), User logon name (UPN prefix + suffix from the domain DNS name and `uPNSuffixes` on `CN=Partitions`), pre-Windows 2000 name (`NETBIOS\sAMAccountName`, filled from the UPN prefix, max 20 characters). Page 2: password ×2, User must change password, User cannot change password, Password never expires, Account is disabled. Page 3: summary. Internally the object is created disabled with `PASSWD_NOTREQD`, then the password is set, and finally UAC = 512 or 514. |
| InetOrgPerson | The same wizard, class `inetOrgPerson`; it can be hidden in the menu (KB311555) |
| Computer | Computer name (`cn`), pre-Windows 2000 name (`sAMAccountName` with `$`, max 15 characters), "The following user or group can join this computer to a domain" (Domain Admins by default; "Change…" gives that principal the ACEs needed to join), the option "Assign this computer account as a pre-Windows 2000 computer" (password from the name, blocked on a WS2025 DC). Default UAC 4128 (0x1000 + 0x20 `PASSWD_NOTREQD`). Formerly a RIS/WDS page (`netbootGUID`). |
| Group | name (`cn`), pre-Windows 2000 name (`sAMAccountName`), scope (Domain local / Global / Universal), type (Security / Distribution) |
| Contact | First, Initials, Last, Full name, Display name |
| Organizational Unit | name + "Protect container from accidental deletion" – checked by default since WS2008 |
| Printer | UNC path of a pre-Windows 2000 printer share → `printQueue` with `uNCName` |
| Shared Folder | name + network path → a `volume` object with `uNCName`; properties Description, Keywords, Managed By |

### 2.9 Copying a user (Copy…)

Copy opens the New User wizard with fields pre-filled. Attributes flagged `fCOPY` (0x10) in `searchFlags` are copied; in
the base schema: `accountExpires`, `assistant`, `codePage`, `company`, `countryCode`, `c`, `co`, `department`,
`division`, `employeeType`, `homeDirectory`, `homeDrive`, `localeID`, `l`, `logonHours`, `logonWorkstation`,
`userWorkstations`, `otherLoginWorkstations`, `manager`, `maxStorage`, `msNPAllowDialin` and the RADIUS/RAS attributes,
`postalAddress`, `postalCode`, `postOfficeBox`, `preferredOU`, `primaryGroupID`, `profilePath`, `scriptPath`,
`showInAddressBook`, `st`, `street`, `userAccountControl`. Group membership is copied by the snap-in's logic. **Not**
copied are `title`, `description`, `physicalDeliveryOfficeName`, telephones and `streetAddress` (the Street field).
Paths ending with the source user's name are replaced with `%username%`. A schema administrator can add `fCOPY` to
other attributes (e.g. `extensionAttribute*`), so we read the set from the live schema.

### 2.10 User property tabs – mapping fields to attributes

| Tab | Field | Attribute(s) | Notes |
|---|---|---|---|
| General | First name / Initials / Last name | `givenName` / `initials` / `sn` | the tab header shows `cn` |
| | Display name | `displayName` | |
| | Description | `description` | multivalued in the schema, but on SAM classes (user, group, computer) only one value |
| | Office | `physicalDeliveryOfficeName` | |
| | Telephone number (Other…) | `telephoneNumber` (`otherTelephone`) | |
| | E-mail | `mail` | |
| | Web page (Other…) | `wWWHomePage` (`url`) | |
| Address | Street / P.O. Box / City / State / ZIP | `streetAddress` / `postOfficeBox` / `l` / `st` / `postalCode` | |
| | Country/region | `c` + `co` + `countryCode` | always all three at once, e.g. `PL` / `Poland` / `616` |
| Account | User logon name | `userPrincipalName` | prefix + suffix from a list |
| | Pre-Windows 2000 | `sAMAccountName` | |
| | Logon Hours | `logonHours` | 21 bytes = 168 bits in UTC from Sunday 00:00 UTC; displayed in local time |
| | Log On To | `userWorkstations` | NetBIOS names separated by commas |
| | Unlock account | `lockoutTime` | write only `0` |
| | Account options | `userAccountControl`, `pwdLastSet`, ACE, `msDS-SupportedEncryptionTypes` | details in the table below |
| | Account expires | `accountExpires` | Never / End of *date* |
| Profile | Profile path / Logon script | `profilePath` / `scriptPath` | |
| | Home folder | `homeDirectory` (+ `homeDrive`) | "Local path" or "Connect *letter* To" |
| Telephones | Home / Pager / Mobile / Fax / IP phone | `homePhone`, `pager`, `mobile`, `facsimileTelephoneNumber`, `ipPhone` + `other*` lists | |
| | Notes | `info` | |
| Organization | Job Title / Department / Company | `title` / `department` / `company` | |
| | Manager | `manager` | DN |
| | Direct reports | `directReports` | back link, read only |
| Member Of | Member of | `memberOf` | back link; editing writes `member` in the group |
| | Primary group | `primaryGroupID` | RID; "Set Primary Group" requires prior membership |
| Dial-in | Network Access Permission | `msNPAllowDialin` | Allow / Deny / none = "Control access through NPS Network Policy" |
| | Caller ID, Callback, Static IP, Routes | `msNPCallingStationID`, `msRADIUSServiceType`, `msRADIUSCallbackNumber`, `msRADIUSFramedIPAddress`, `msRADIUS-FramedIpv6Prefix`, `msRADIUS-FramedInterfaceId`, `msRADIUSFramedRoute` | plus `msRAS-Saved*` copies |
| Environment, Sessions, Remote control, RDS Profile | startup program, device mapping, session limits, remote control, TS profile and directory, RDSH logon denial | binary `userParameters` (TSUserEx format, `tsuserex.dll`) | **not** in `msTS*` attributes |
| COM+ | Partition set | `msCOM-UserPartitionSetLink` | |
| Published Certificates | certificates | `userCertificate` | add from the store or a file, remove, save to a file |
| Object (advanced) | | `canonicalName`, `objectClass`, `whenCreated`, `whenChanged`, `uSNCreated`, `uSNChanged` | plus the "Protect…" field (ACE) |
| Security (advanced) | ACL | `nTSecurityDescriptor` | |
| Attribute Editor (advanced) | all attributes | — | syntax-dependent editors; "only with values" and "only writable" filters |

**Account options (Account tab):**

| Option | Actual write |
|---|---|
| User must change password at next logon | `pwdLastSet = 0` (clearing: `-1`); does not work when 0x10000 is set |
| User cannot change password | two Deny entries for Everyone (S-1-1-0) and SELF (S-1-5-10) on the Change Password right `ab721a53-1e2f-11d0-9819-00aa0040529b`; UAC bit 0x40 is ignored on write and never appears on read |
| Password never expires | UAC 0x10000 |
| Store password using reversible encryption | UAC 0x80 |
| Account is disabled | UAC 0x2 |
| Smart card is required for interactive logon | UAC 0x40000 |
| Account is sensitive and cannot be delegated | UAC 0x100000 |
| Use only Kerberos DES encryption types | UAC 0x200000 |
| This account supports Kerberos AES 128 / 256 | `msDS-SupportedEncryptionTypes` 0x8 / 0x10 |
| Do not require Kerberos preauthentication | UAC 0x400000 |

Pitfalls: `logonHours` is in UTC (a naive display shifts the grid by the time zone, and on a daylight-saving change the
bitmap does not shift); in `accountExpires` "End of 12/31/2026" writes 2027-01-01 00:00 local time in UTC – a source of
off-by-one-day bugs. "Password Replication" from the task list is not a user tab but a tab of RODC computers (2.11).

**Name Mappings** (advanced view) has the "X.509 Certificates" tabs (a `.cer` file, "Use Issuer…"/"Use Subject…") and
"Kerberos Names" (`principal@REALM`); the values go to `altSecurityIdentities`. KB5014754 treats `X509IssuerSubject`,
`X509SubjectOnly` and `X509RFC822` as weak, and `X509IssuerSerialNumber`
(`X509:<I>issuer<SR>reversed-number`), `X509SKI` and `X509SHA1PublicKey` as strong; with full enforcement, weak
mappings do not allow certificate logon.

### 2.11 Computer tabs

| Tab | Fields → attributes |
|---|---|
| General | Computer name (pre-2000) = `sAMAccountName`; DNS name = `dNSHostName`; role from `userAccountControl` (0x1000 workstation/server, 0x2000 `SERVER_TRUST_ACCOUNT` = DC, 0x04000000 `PARTIAL_SECRETS_ACCOUNT` = RODC); `description`. For a DC also the type (GC) and the NTDS Settings button (to be verified in the lab) |
| Operating System | `operatingSystem`, `operatingSystemVersion`, `operatingSystemServicePack` – read only |
| Member Of | `memberOf` + `primaryGroupID` (515 Domain Computers, 516 DC, 521 RODC) |
| Delegation (accounts with an SPN) | "Do not trust"; "Trust for delegation to any service (Kerberos only)" = UAC `TRUSTED_FOR_DELEGATION` 0x80000; "…to specified services only" with "Use Kerberos only" (only `msDS-AllowedToDelegateTo`) or "Use any authentication protocol" (additionally `TRUSTED_TO_AUTH_FOR_DELEGATION` 0x1000000). Resource-based constrained delegation (`msDS-AllowedToActOnBehalfOfOtherIdentity`) **has no interface in ADUC** |
| Location | `location` |
| Managed By | `managedBy` (+ the manager's office, address and telephones, read only) |
| Dial-in | as for a user |
| LAPS (Windows LAPS) | `msLAPS-PasswordExpirationTime` (FILETIME UTC) with "Expire now"; the current password from `msLAPS-Password` (JSON `{n, t, p}`) or the decrypted `msLAPS-EncryptedPassword`. History (`msLAPS-EncryptedPasswordHistory`) and DSRM (`msLAPS-EncryptedDSRMPassword`) only through `Get-LapsADPassword`. Reading requires rights to the confidential attribute and – for encrypted passwords – membership in the group authorized to decrypt (DPAPI-NG on a domain-joined computer). Legacy LAPS (`ms-Mcs-AdmPwd`, `ms-Mcs-AdmPwdExpirationTime`) has no tab |
| BitLocker Recovery | child objects `msFVE-RecoveryInformation` (`msFVE-RecoveryPassword`, `msFVE-RecoveryGuid`, `msFVE-VolumeGuid`); in the domain menu "Find BitLocker recovery password" by the first 8 characters of the Password ID (the filter – to be verified in the lab) |
| Password Replication Policy (RODC only) | Allowed `msDS-RevealOnDemandGroup` and Denied `msDS-NeverRevealGroup` lists; "Advanced" shows `msDS-RevealedList` and `msDS-AuthenticatedToAccountlist` and "Prepopulate Passwords" |
| Object / Security / Attribute Editor | as for a user |

The **Reset Account** action used to set the computer password to the lowercase name (the secure channel is broken until
rejoin). WS2025 DCs refuse a name-derived password (the "Domain controller: Refuse setting default machine account
password" policy, respected by ADUC, ADAC, `net computer` and `dsmod`), so we do not reproduce this action (9.6).

### 2.12 Group tabs

| Tab | Fields → attributes |
|---|---|
| General | `cn`, `sAMAccountName`, `description`, `mail`; scope and type in `groupType`; Notes = `info` (Microsoft's mapping page gives `comment` – to be verified in the lab) |
| Members | `member`; for large groups ranged retrieval (`MaxValRange` 1500 or 5000) |
| Member Of | `memberOf` |
| Managed By | `managedBy` + the field "Manager can update membership list" = Allow WriteProperty ACE on `member` (`bf9679c0-0de6-11d0-a285-00aa003049e2`) for the manager |
| Object / Security / Attribute Editor | as for a user |

`groupType`: 0x2 global, 0x4 domain local, 0x8 universal, 0x1 system, 0x80000000 security – e.g. global security
`-2147483646`, domain local security `-2147483644`, universal security `-2147483640`, distribution `2`/`4`/`8`. Scope
change: Global → Universal only when the group does not belong to another global group; Domain local → Universal only
without domain local members; Universal → Global or Domain local only when membership allows it.

### 2.13 OU, contact and domain

- **OU:** General (`description`, `street`, `l`, `st`, `postalCode`, `c`/`co`/`countryCode`), Managed By
  (`managedBy`), COM+, Object (with the Protect field), Security, Attribute Editor. The Group Policy tab is gone (moved
  to GPMC).
- **Contact:** General, Address, Telephones, Organization, Member Of, Object, Security, Attribute Editor.
- **Domain:** General (NetBIOS name, description, domain and forest functional levels – read only), Managed By, Object,
  Security, Attribute Editor.

### 2.14 Fields that are not simple attributes

| Field in ADUC | Actual write | Consequence for AD_MNG |
|---|---|---|
| User cannot change password | 2 × Deny ACE (Everyone, SELF) on the right `ab721a53-…` | read the DACL with the SD Flags control; do not use UAC 0x40 |
| Protect object from accidental deletion | Deny Everyone `DELETE` (0x10000) + `DELETE_TREE` (0x40) on the object and Deny Everyone `DELETE_CHILD` (0x2) on the parent; SDDL `(D;;SDDT;;;WD)` and `(D;;DC;;;WD)` (SDDL notation – to be verified in the lab) | also blocks a move; an explicit Deny stops even Domain Admins; we read the state from the DACL |
| Manager can update membership list | Allow WriteProperty on `member` for the principal from `managedBy` | edit the group's ACE when the manager changes |
| Environment / Sessions / Remote control / RDS Profile | binary `userParameters` (TSUserEx) | our own format encoder, tabs marked as advanced |
| Country/region | `c` (ISO 3166 alpha-2) + `co` (name) + `countryCode` (ISO numeric) | write only as a triple; `c` alone gives an empty or stale field in ADUC |
| Locked out | state from `msDS-User-Account-Control-Computed` 0x10; unlock with `lockoutTime = 0` | the LOCKOUT bit in UAC is not maintained; `lockoutTime > 0` does not mean "locked" |
| Primary group | `primaryGroupID` (RID from the group's `primaryGroupToken`) | does not appear in `member`/`memberOf`; requires prior membership |

### 2.15 The delegation wizard and domain operations

- **Delegate Control** (driven by `%windir%\inf\delegwiz.inf`; Microsoft published a version with more than 70 tasks):
  choose users or groups, then common tasks or custom ones (scope and permissions). Common tasks on an OU: managing
  accounts, resetting passwords with forced change, reading user information, managing groups and their membership, GPO
  links, RSoP, equivalents for inetOrgPerson; "Join a computer to the domain" at the domain level (the full list – to be
  verified in the lab). The wizard adds inherited ACEs to the OU and **cannot show or undo a delegation** – cleanup is
  done on the Security tab.
- **Change Domain / Change Domain Controller** ("Any writable DC" or a DC from a list: name, site, type, version,
  status); all operations go to one DC, whose name the tree root shows.
- **Operations Masters**: RID, PDC, Infrastructure (`fSMORoleOwner` on `CN=RID Manager$,CN=System`, the domain NC head
  and `CN=Infrastructure`); "Change…" moves the role to the bound DC through the RootDSE writes `becomeRidMaster`,
  `becomePdc`, `becomeInfrastructureMaster`. The schema master is in `schmmgmt.msc`, the domain naming master in
  `domain.msc`.
- **Raise domain functional level** writes `msDS-Behavior-Version` on the NC head through the PDC; the operation is
  irreversible and requires all DCs at the given level (even an unused RODC account of a lower version blocks it). The
  forest level is raised in `domain.msc` (`msDS-Behavior-Version` on `CN=Partitions`).

| `msDS-Behavior-Version` | 0 | 2 | 3 | 4 | 5 | 6 | 7 | 10 |
|---|---|---|---|---|---|---|---|---|
| Level | 2000 | 2003 | 2008 | 2008 R2 | 2012 | 2012 R2 | 2016 (also 2019 and 2022) | 2025 (32k pages, dMSA) |

### 2.16 Display specifiers – the source of ADUC labels

`CN=DisplaySpecifiers,CN=Configuration,<forest>` has a container for each language: `CN=409` (en-US) and `CN=415`
(pl-PL); when a container is missing, ADUC uses `409`. The `<class>-Display` object contains `classDisplayName`,
`attributeDisplayNames` (`attribute,Label` pairs), `extraColumns`, `createDialog`, `treatAsLeaf`, `iconPath`,
`adminContextMenu`, `adminPropertyPages`, `adminMultiselectPropertyPages`, `shellPropertyPages` (we skip the CLSIDs). We
read them live into session memory and never save them (this is forest-wide configuration), so Polish administrators
will see names as in the Polish ADUC. The Samba lab has no `CN=415` ([ADR-0007](adr/0007-samba-lab.md)) – our
own translations are needed as well.

### 2.17 Editing multiple objects in ADUC

Selecting N users and Properties opens a trimmed-down sheet in which each field has an "apply" checkbox; only the
checked fields are written. The set from practice (Microsoft no longer documents it): **General** – Description,
Office, Telephone number, Fax, Web page, E-mail; **Address** – all fields; **Account** – UPN suffix (without the
prefix), Logon hours, Log On To, Unlock, account options, Account expires; **Profile** – paths with `%username%` (also
creates home directories); **Organization** – Title, Department, Company, Manager. Missing: names, `displayName`, the
Telephones tab, Member Of (replaced by "Add to a group"), Dial-in, RDS and the Attribute Editor; extensions can add
pages through `adminMultiselectPropertyPages` (e.g. Exchange). For computers, groups and contacts the sheet is
rudimentary (`description`, `managedBy` – to be verified in the lab). Writing goes object by object: no preview, diff,
per-object report, rollback or CSV.

### 2.18 What ADUC cannot do

The AD Recycle Bin (browsing and restoring – it is in ADAC and `Restore-ADObject`); an FGPP interface; Authentication
Policies and Silos; Dynamic Access Control; creating gMSA and dMSA; RBCD; LAPS password history; effective and nested
membership (`tokenGroups`); the real last logon (`lastLogon` is per DC and not replicated, `lastLogonTimestamp` lags by
9–14 days); password expiration date (`msDS-UserPasswordExpiryTimeComputed`); lockout state on all DCs (formerly
`LockoutStatus.exe`); "who changed what" history (there is only `msDS-ReplAttributeMetaData` /
`repadmin /showobjmeta`); PowerShell command history as in ADAC; global search across multiple domains.

---

## 3. ADUC pain points and how we address them

| # | Pain point | Cause | How we address it |
|---|---|---|---|
| 1 | "Not Responding" on large OUs | synchronous enumeration on the MMC console's UI thread | asynchronous, cancelable LDAP access; pages appended to the list with `Background` priority; the UI thread only binds data; Esc cancels (6.2) |
| 2 | The 2000-object limit and misleading counters (2000 out of 15,048) | a limit in Filter Options, counting only loaded objects | no limit: paging, and above about 2000 objects VLV; approximate count from `msDS-Approx-Immed-Subordinates`; an "as of *time*" marker (6.4–6.5) |
| 3 | Sorting and filtering only on loaded objects | client-side sorting | server-side sorting by an indexed attribute, filters as LDAP; local sorting (`pl-PL`) only for complete, small sets (7.4) |
| 4 | Modal fixed-size windows, a small member list, one window per object, no refresh of an open sheet | the MMC sheet model | non-modal, resizable tabs as tabs or windows; a details panel; a refresh button; detecting a concurrent change through `whenChanged`/`uSNChanged` (7.6, 9.4) |
| 5 | No search-as-you-type; Find in a separate window; no Attribute Editor in Find results ("won't fix" bug); "Check Names" in the object picker | the dsquery interface | a global ANR box (Ctrl+E) with delay and cancellation; results open the full sheet; our own object picker based on ANR (6.8) |
| 6 | "Entire Directory" sees only PAS attributes | GC search | explicit scope choice (container / domain / GC); non-PAS columns grayed out; writes directed to the DC of the object's domain (6.12) |
| 7 | Weak bulk editing: a fixed subset of user fields, no preview, diff, CSV or report | the "Properties for multiple items" sheet | one change-plan engine (section 9) |
| 8 | No rollback, no Recycle Bin, accidental drags | no change log; the Recycle Bin only in ADAC | an in-memory rollback log; a Recycle Bin view with tree restore (6.13); drag confirmation and a check of deletion protection |
| 9 | Manual F5, no notifications | no change subscription | LDAP notifications for the visible container (max 5 per connection), with `highestCommittedUSN` polling as a fallback (6.11) |
| 10 | Working with one DC: lockout state only from that DC | the snap-in's pinning | a DC pinned consciously and visibly; lockout state and last logon from all DCs on demand |
| 11 | No credential switching (`runas /netonly`, a separate MMC) | the console inherits the process identity | connection profiles in the app, `NEW_CREDENTIALS` in the process, several windows with different identities and a colored bar (8.3) |
| 12 | One domain per snap-in | the snap-in model | roots for all domains from `crossRef`, a separate session per domain, search in the GC |
| 13 | Forest-wide columns (`extraColumns`), queries in `.msc` files | the display specifiers and console model | columns and queries as user preferences; XML import from ADUC; configurable views with export for the team (7.6) |
| 14 | Blurry image on high DPI and over RDP, no dark mode, poor accessibility | bitmap scaling in MMC | PerMonitorV2, Fluent light/dark, UI Automation, high contrast, text scaling (7.8) |
| 15 | RSAT dependency (installation as administrator, disappearing after the 24H2 update, ARM64 only since 2026) | distribution as a Windows feature | a standalone .NET 10 app in an MSI; only LDAP ports 389/636 and GC 3268/3269 are needed |
| 16 | Delegation cannot be reviewed or undone | the wizard only adds ACEs | a review of non-default ACEs per OU (adeleg-style) with the ability to roll back – a later phase |
| 17 | No answer to "who changed it and when" | no history | a History tab from replication metadata (when, on which DC, version – no author); optionally event 5136 from the DC (8.8) |

---

## 4. ADAC, Windows Admin Center, Entra admin center, PowerShell AD

### 4.1 Active Directory Administrative Center (dsac.exe)

ADAC introduced most of the "modern" ideas worth keeping, but it works through the PowerShell AD module and the AD Web
Services (ADWS, TCP 9389) service, which is why it is perceived as slower than ADUC.

| We copy (and improve) | We avoid |
|---|---|
| Navigation in two modes: a tree (like ADUC) and a list with user-added nodes (pins: renaming, ordering, nodes from trusted domains) and a history of the last 3 containers per node | Dependence on ADWS: when the service is down or the port is blocked, ADAC and the AD module stop working |
| An address bar accepting a DN – in our app also for the Configuration context when the operator has rights (ADAC sees only domain NCs) | A list limit of 20,000 objects per container (max 100,000 in Manage › Management List Options) combined with client-side filtering – objects above the limit silently disappear |
| Global Search with a criteria wizard and "Convert to LDAP"; saved queries store criteria, sorting and columns | `MaxGroupOrMemberEntries` = 5000 in ADWS: `Get-ADGroupMember` returns an error for larger groups, and changing the limit requires editing a configuration file on every DC |
| The Attribute Editor reachable from search results (the "Extensions" section) | Other credentials only through `runas /user:… dsac` |
| The Windows PowerShell History panel: by default only data-changing commands, "Show All" for `Get-*`, "Start Task"/"End Task" grouping commands under a note, search and copy | The AD Recycle Bin without restoring a subtree in one move: first the parents, a refresh, then the children level by level |
| The AD Recycle Bin view (Restore, Restore To, Locate Parent; filters "When deleted", "Last known parent", name, type, department, UPN) | The Recycle Bin only for the domain partition (Configuration and DNS require `Restore-ADObject`) |
| The FGPP view (Password Settings Container, "View Resultant Password Settings") | |
| Editing multiple objects with an "apply" checkbox for each field | |

The Dynamic Access Control and Authentication Policies/Silos nodes are niche; we plan them late and initially read
only. For diagnosing ADAC itself, `dsac.exe.config` with `DsacLogLevel` is useful (at the Info level the log shows which
DCs it connected to and which commands it ran).

### 4.2 Windows Admin Center – the Active Directory extension

The extension appeared in WAC Preview 1903 (2019) and according to Microsoft Learn is still "early preview" (no updates
in 2024–2026). It works only after connecting to a DC, through the WAC gateway. It offers creating users and groups,
search, a details panel, bulk Disable/Enable/Remove, password reset, basic properties and Kerberos delegation to a
single machine; group members are changed one at a time (early versions returned at most 10 results per type). WAC 2410
moved to .NET 8 and broke older extensions. **We copy** the details panel next to the grid and the "Manage as" model
(the current identity by default, optionally other credentials). **We avoid** the gateway (when the domain
administrators' credentials pass through it, it becomes a Tier-0 asset), membership "one at a time" and treating WAC as
a platform.

### 4.3 Microsoft Entra admin center

| We copy | We avoid |
|---|---|
| Column selection by dragging headers; any attribute as a column (e.g. `employeeId`, extension attributes, `manager`) | The limit of 60 users in bulk editing and a fixed list of about 12 fields (the list – to be verified in the lab) |
| "Copy link to current view" – in our app a view definition as JSON or an app link (fits the client's configurable views) | Asynchronous jobs with a 1-hour limit (large jobs end in an error) |
| Scrolling with incremental loading instead of a "Load more" button | "Not real-time" counters refreshed every few hours |
| Separate tabs for direct and all (transitive) members, each with its own filter; both counts side by side | The default "contains" mode – in AD it means `(attr=*x*)` without an index, expensive on a DC |
| A downloadable CSV template with headers in the style `Name [displayName] Required`, file validation before upload, a results page with success and error counts, per-row results for download | |
| Log filters: category, activity, date range, target, initiator; downloading the data is itself audited | |

### 4.4 The ActiveDirectory PowerShell module

The app does not run PowerShell – it generates commands **equivalent** to the LDAP operation actually performed,
labeled as "equivalent", not "executed".

- `Get-AD*`: `-Identity` (DN, GUID, SID, `sAMAccountName`), `-Filter`, `-LDAPFilter`, `-SearchBase`, `-SearchScope`,
  `-ResultPageSize` (256 by default), `-Properties`, `-Server`, `-Credential`. Filter operators: `-band` →
  `:1.2.840.113556.1.4.803:=`, `-bor` → `.804`, `-recursivematch` → `.1941`; the only wildcard is `*`.
- `Set-ADUser -Add/-Remove/-Replace` (hashtables with LDAP names) and `-Clear` execute in the order Remove, Add,
  Replace, Clear; an empty value in `-Add`/`-Replace` is an error (empty CSV cells → `-Clear`); `-Instance` writes only
  the differences. `Move-ADObject -TargetServer` between domains requires the RID master in both domains and does not
  work against an RODC; an OU protected from deletion cannot be moved.
- Generated scripts: `-Server <pinned DC>`, `-Identity <objectGUID>`, `-WhatIf`, never passwords
  (`Read-Host -AsSecureString`), and for large groups `(Get-ADGroup -Properties member).member` instead of
  `Get-ADGroupMember` (the ADWS limit). Mapping of operations to commands – 9.12.

### 4.5 Search-ADAccount-style quick filters

`Search-ADAccount` has the switches `-AccountDisabled`, `-AccountExpired`, `-AccountExpiring`, `-AccountInactive`,
`-LockedOut`, `-PasswordExpired`, `-PasswordNeverExpires`. We map them as filter "chips" executed server-side (FT =
FILETIME, hundreds of nanoseconds since 1601-01-01 UTC):

| Chip | LDAP filter | Notes |
|---|---|---|
| Disabled | `(userAccountControl:1.2.840.113556.1.4.803:=2)` | |
| Password never expires | `(userAccountControl:1.2.840.113556.1.4.803:=65536)` | |
| Account expired | `(&(accountExpires>=1)(accountExpires<=FT_now))` | excludes `0` and `9223372036854775807` ("never") |
| Expires within N days | `(&(accountExpires>=FT_now)(accountExpires<=FT_now+N))` | |
| Inactive N days | `(\|(lastLogonTimestamp<=FT_now-N)(!(lastLogonTimestamp=*)))` | 90 days by default; a hint about the 9–14 day lag; for computers also `pwdLastSet` |
| Locked out | `(lockoutTime>=1)` on the server + bit 0x10 in `msDS-User-Account-Control-Computed` on the client | `lockoutTime` stays after the lockout expires (MS-ADTS 3.1.1.4.5.17) |
| Password expired | bit 0x800000 in `msDS-User-Account-Control-Computed` or `msDS-UserPasswordExpiryTimeComputed` < now | accounts for FGPP; the bits are not in the stored `userAccountControl` |
| Must change password | `(pwdLastSet=0)` | |

Constructed attributes cannot be used in a filter, so the "locked out" and "password expired" chips pre-filter on the
server and check only the current page on the client. The default inactivity window of `Search-ADAccount` is not
documented (an external source gives 15 days – to be verified in the lab), so in generated code we always specify
`-TimeSpan` explicitly.

### 4.6 Change history without a local database

The closest cache-free equivalent of the Entra log is the constructed attributes `msDS-ReplAttributeMetaData`
(single-valued attributes) and `msDS-ReplValueMetaData` (link values, e.g. adding and removing a group member): the last
change, its time, the `invocationId` of the source DC, the version and the USN. **They do not say who made the
change** – that requires events from the Security log on the DC (e.g. 4738, 5136), which is a separate, heavier feature
(8.8).

---

## 5. Competing and open-source products

Data on commercial products comes from search-engine snippets of vendor documentation (the sites were unreachable).
Details the researchers marked as uncertain carry the note (to be verified in the lab) – in this case on a trial version
of the product.

### 5.1 Comparison table

| Product | Model | Local database / AD copy | Bulk editing | Preview before write | Rollback | Delegation |
|---|---|---|---|---|---|---|
| ADUC (reference point) | MMC client (ADSI) | no (only query definitions in `.msc`) | multi-object sheet, fixed subset of fields | no | no | AD ACLs |
| ADAC | WPF client → ADWS/PowerShell | no | multi-object sheet with "apply" checkboxes | no | AD Recycle Bin (without subtrees) | AD ACLs |
| ManageEngine ADManager Plus | web app + Windows service, writes with a service account | **yes**: PostgreSQL, sync every 10 min, full about once a month | CSV with a match key, templates, actions from reports, automations | list of matched objects with attribute selection | none native; attribute rollback in RecoveryManager Plus (another database) | in the product (technician roles per domain/OU), multi-step approvals |
| Softerra Adaxes | intermediary service (.NET Remoting), web UI, REST | **yes**: configuration in AD LDS, log in SQLite (30 days by default) | Basket from various containers, "Multiple Selection Properties" (one type only), value references | no data | none native | Security Roles in the product, writes with the service account, approvals |
| One Identity Active Roles | intermediary service + web | **yes**: configuration database; Managed Units exist only in the product | batch operations in the web UI (default limit 1500 objects) | no data | only "Undo deprovisioning" | Access Templates on objects and Managed Units; workflow with escalation |
| Hyena (SystemTools) | desktop client | no | Attribute Manager (Replace/Append/Delete), Active Editor (sheet), Active Task (keyed import) | row validation, before and after state in Active Editor | none documented | AD ACLs |
| Softerra LDAP Administrator | desktop LDAP client | no | Multiple Modifications wizard (`%attr%`), LDAP-SQL, LDIF import | no data | none | AD ACLs |
| Sysinternals AD Explorer | desktop client | **yes**: `.dat` snapshots = a full copy of the directory | none | — | — (snapshot comparison) | AD ACLs |
| ADModify.NET (archived, Microsoft PSS) | desktop client + CLI | XML log with old and new values | any attribute, dynamic values | no data | **yes**, from the XML log | AD ACLs |
| UnifiedDirectoryManager | WPF client, .NET 10 | no (favorites as DN references) | search results, pasted list (≤ 100 entries), CSV, scenarios | "every write confirmed with a diff" | no documented bulk rollback | AD ACLs |
| BLAZAM | web app (Blazor) | no data | no data | no data | no data | "Access Levels" per attribute and action, inherited down OUs (Deny wins), impersonation for testing |
| OpenADUC (beta) | web app (Vue 3, Fastify, ldapts) | **yes**: PostgreSQL with deltas and periodic full sync | planned | — | — | re-entering the AD password before a write (60 min), append-only log |
| **AD_MNG** | WPF client, zero infrastructure | **no** – only session memory | one change-plan engine: selection, query, pasted list, CSV, sheet, templates, wizards | per-object diff with filters, validation and a permission check | in-memory log + a compensating plan with CAS conditions | AD ACLs + a permission-aware UI |

### 5.2 Key observations about the products

- **ManageEngine ADManager Plus:** modification from CSV with matching by `sAMAccountName`, `employeeID`, UPN, DN,
  `objectGUID`, `mail`, `cn` or `displayName`; the key must not be changed in the same run; LDAP headers, UTF-8,
  TRUE/FALSE; since 6.3 appending or overwriting multivalued values and a choice of whether an empty cell clears.
  The vendor lists among the causes of errors encoding and replication latency. Templates with mandatory, hidden and
  "silently active" fields, a name-format wizard, 200+ reports with actions, automations. Delegation "works only in the
  product". Annual pricing from USD 595 (1 domain, 2 technicians) to USD 17,995 (500 technicians). Assigning templates
  to OUs – to be verified in the lab.
- **Softerra Adaxes:** .NET Framework 4.8 + IIS; Property Patterns with references (`%firstname:lower%%lastname:lower,2%`,
  `%adm-RandomString,12%`, `%adm-ParentDN%`), Business Rules, Custom Commands, approvals (since 2025.1 the approver can
  correct the request), Business Units. Licensing per enabled account (100 accounts: USD 1600 + USD 480/year). Users on
  the vendor's forum ask for ADModify-style rollback.
- **One Identity Active Roles:** Managed Units, Access Templates, PGV rules (known bug: a missing attribute removes the
  text following it), workflow with escalation, time-limited membership, dynamic groups; "Update object attributes" ends
  in an error when nothing changes; version 8.2 (October 2024) added federation and MFA.
- **Hyena:** Attribute Manager (Replace/Append/Delete), Active Editor (a sheet from results without re-reading – the
  documentation recommends refreshing; keyed import with validation and marking of changed cells), Active Task (a task
  file run from the command line). USD 150–329 per administrator.
- **Softerra LDAP Administrator** (USD 250): Multiple Modifications with `%uid%`, LDAP-SQL, LDIF import with updating of
  existing objects and "parent before child" ordering; in 2026 import/export tasks in the scheduler.
- **ADModify.NET:** an XML log with the old and new value allowed undoing everything except "Remove Exchange
  Attributes" and "Import Mailbox Rights"; the project disappeared along with CodePlex (the syntax of dynamic values –
  to be verified in the lab).
- **Sysinternals AD Explorer:** the `.dat` snapshot is a full copy of the directory, which offensive tools
  (ADExplorerSnapshot.py, about 1.1k stars) convert into BloodHound data – a strong argument for the "no local data"
  rule.
- **Others:** Cjwdev AD Tidy (rollback only in the paid version, updates ceased around 2016; an "AD Bulk Users" product
  was not confirmed); Netwrix and Lepide (change history, but with collectors and their own store); Specops Secure
  Service Desk (caller verification before a reset, an optional ban on manually typed passwords).
- **Open source:** *UnifiedDirectoryManager* (WPF, .NET 10, active in October 2026) is the closest equivalent:
  templates, "Copy user", saved queries, bulk editing of results, a pasted list, CSV with formula neutralization,
  scenarios, favorites grayed out when the target disappears, LDAPS or Kerberos with signing. *OpenADUC* keeps the whole
  AD in PostgreSQL. *BLAZAM* has the best delegation model among open-source projects. *ADMC* (Qt6) clones ADUC and
  GPMC. *myADMonitor* keeps changes in memory only. *adeleg*, *ADACLScanner* and *PingCastle* are models for ACL and
  stale-object reports; *Tulpep Object Picker* wraps DsObjectPicker; *lithnet access-manager* is a UX model for
  LAPS/BitLocker access; *LDAP Admin*, *Apache Directory Studio* (the Batch Operation wizard) and *LDAP Account Manager*
  (validation → LDIF preview → write) are general LDAP clients.

### 5.3 Template syntaxes – comparison

| Tool | Syntax | Takeaways |
|---|---|---|
| Adaxes | `%firstname%`, `%lastname:lower,2%`, `%adm-RandomString,12%`, `%adm-ParentDN%` | case and length modifiers |
| Active Roles (PGV) | `%<givenName>`, `%<sn>`, masks, properties of the parent OU | a missing attribute "eats" the following text – an explicit fallback is needed |
| ManageEngine | a name-format wizard saved as `%givenName%`, conditional formats | live preview |
| Softerra | `%uid%` in Multiple Modifications | per-object values |
| ADUC | `%username%` in profile/home directory paths when editing multiple objects | backward compatibility |
| AD_MNG | `{attr\|fn…}`, `{csv:Kolumna}`; functions including `lower`, `first(n)`, `translit`, `default(x)`, `counter(w)` | Polish transliteration, explicit fallback values, uniqueness counters, preview (9.10) |

### 5.4 Conclusions and AD_MNG differentiators

- **Zero infrastructure:** no service, service account or database; live operations with the operator's credentials, so
  the delegation boundary is the native AD ACLs and no new Tier-0 asset is created.
- **A full bulk pipeline with rollback** (fresh read, diff, concurrency control, per-object report, compensating plan)
  that none of the products examined has.
- **A permission-aware UI straight from AD** – a "help desk view" and an "administrator view" without local roles.
- **Live change history from AD** (replication metadata + optionally event 5136), without a collector or database.
- **Polish realities** (transliteration of ł, CSV in cp1250 with a semicolon, scripts with a BOM, labels from `CN=415`)
  and **parity with ADUC** together with keyboard habits.
- **Trade-off:** without a server there is no enforced approval process – later, signed plan files ("four-eyes").
  Roles and hidden fields in the UI are a convenience, not a safeguard – this has to be communicated clearly.

---

## 6. LDAP techniques for "live fluidity"

### 6.1 Why S.DS.P

ADSI/`DirectoryEntry` (the basis of ADUC) has a client-side property cache (`RefreshCache`/`CommitChanges`), which
conflicts with the "no cache" rule, and is COM-based; `System.DirectoryServices.AccountManagement` adds network round
trips and handles several classes; ADWS (port 9389) is a slower SOAP service, not present on every DC, with a limit of
5000 members. S.DS.P is a thin layer over `wldap32`: every control and session option, request multiplexing, a 10.0.x
NuGet package, and it also works on Linux (tests with Samba in CI) –
[ADR-0002](adr/0002-system-directoryservices-protocols.md). WS2025 and Windows 11 24H2 have LDAP client performance
counters that can be used to demonstrate the advantage over ADUC.

### 6.2 Asynchrony: why `TaskCompletionSource` and not `FromAsync`

S.DS.P in .NET 10 has only the APM-style API (`BeginSendRequest`, `EndSendRequest`, `Abort`, `GetPartialResults`);
neither `release/10.0` nor `main` (.NET 11) has a `Task`-based version. The .NET 6–10 code shows that:

1. On first use `BeginSendRequest` **synchronously** performs `ldap_connect` (limit = `Timeout` in whole seconds,
   default 30 s) and – with `AutoBind` – a blocking `ldap_bind_s`; a re-bind also happens after a change of `AuthType`
   or `Credential`. The first request must therefore not be sent from the UI thread.
2. Completion is detected by an `ldap_result` loop with a `Task.Delay` doubled from 1 to 100 ms (overhead from a few
   to about 100 ms, without blocking a thread); after the request time limit expires an `LdapException` 85 is raised
   and `ldap_abandon` is sent. Synchronous `SendRequest` truncates the time limit to whole seconds.
3. Partial results mode (notifications) is served by **one global thread** with `Thread.Sleep(250)`;
   `TimeSpan.MaxValue` and `Timeout.InfiniteTimeSpan` give an immediate `LDAP_TIMEOUT`.
4. **After `Abort(ar)` the callback is never invoked** (the `_abortCalled` flag; checked in `LdapConnection.cs` from
   `release/10.0`, around lines 354 and 424), so `Task.Factory.FromAsync` would never complete after cancellation.
   `Abort` also throws `ArgumentException` if `EndSendRequest` has already run. This settled the dispute between the
   researchers (the UI researcher proposed `FromAsync`, the S.DS.P researcher `TaskCompletionSource`):

```csharp
var tcs = new TaskCompletionSource<DirectoryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
var ar = conn.BeginSendRequest(req, timeout, PartialResultProcessing.NoPartialResultSupport,
    a => { try { tcs.TrySetResult(conn.EndSendRequest(a)); } catch (Exception e) { tcs.TrySetException(e); } }, null);
using var reg = ct.Register(() =>
{
    if (tcs.TrySetCanceled(ct))
        try { conn.Abort(ar); } catch (ArgumentException) { } catch (ObjectDisposedException) { }
});
return await tcs.Task.ConfigureAwait(false);
```

- The UI thread only binds data; the first connect/bind goes through `Task.Run`; no `.Result` or `.Wait()` anywhere;
  `ConfigureAwait(false)` in the service layers. Always a bounded request limit (30–120 s) and
  `SearchRequest.TimeLimit` (the DC cuts it to `MaxQueryDuration` = 120 s) – after `Abort` the internal loop runs until
  an error or until the limit expires.
- Every view has its own `CancellationTokenSource`, cancelled on navigation, refresh, a change of sort order, filter or
  identity; search-as-you-type cancels on every character; Esc cancels the foreground operation.
- `Abort` sends an abandon but does not undo a modification the server has already performed – an interrupted write has
  the **Unknown** state and a verification read resolves it (9.5).

### 6.3 Connections and their configuration

`wldap32` calls are thread-safe and one session multiplexes many requests; the S.DS.P class itself, however, does not
guarantee instance safety – `Bind()` and changes to `SessionOptions`, `Credential`, `AuthType` must not run in
parallel with requests. Server state (page cookies, VLV `ContextId`, notification registrations) is bound to the
connection.

| "Lane" per pair (identity, domain) | Connections | Purpose |
|---|---|---|
| Interactive | 1, about 6 parallel requests, priority queue | navigation > visible page > property page > prefetching |
| Batch | 1–2, behind a rate limiter | writes only |
| Notification | 1, max. 5 registrations | change notifications |
| GlobalCatalog | 1, port 3268 (3269 with TLS) | forest-wide search, read only, never write |

Settings: `LdapDirectoryIdentifier(fqdn, 389, fullyQualifiedDnsHostName: true, connectionless: false)` (sets
`LDAP_OPT_AREC_EXCLUSIVE`, so the SPN `ldap/<fqdn>` gives Kerberos); `ProtocolVersion = 3`; `ReferralChasing = None`
(on Windows the default is effectively `All` – chasing DomainDnsZones, ForestDnsZones and Configuration with new binds
causes multi-second stalls); `Signing = Sealing = true` before `Bind()`; `AutoBind = false`; `TcpKeepAlive`; `Timeout`
15–30 s. The built-in control classes have `IsCritical = true` by default; before using a control we check
`supportedControl` in the RootDSE.

### 6.4 Paging

- Paged Results (RFC 2696, `1.2.840.113556.1.4.319`): the default `PageSize` in S.DS.P is 512 – we set it explicitly;
  the loop continues until an empty `Cookie`; the DC cuts the page to `MaxPageSize` (1000, hard limit 20 000). Without
  the control a result above the limit ends with `sizeLimitExceeded` (4), and the partial entries are in the
  exception's `SearchResponse`. When abandoning a search we send `PageSize = 0` with the cookie to release server
  state; `DomainScopeControl` (`.1339`) turns off continuation references on every page.
- AD uses one index per search; sorting with paging forces the DC to sort the whole result before the first page, and
  paging is not combined with VLV.
- For fluidity: first page of 100–200 objects, subsequent ones 500–1000; always an explicit attribute list (never `*`);
  one `CollectionChanged` event per page with `DispatcherPriority.Background`.

### 6.5 VLV and server-side sorting

- VLV (`2.16.840.1.113730.3.4.9`, response `.10`) requires a sort control (`1.2.840.113556.1.4.473`, response
  `.474`); AD supports **one key** and does not sort by constructed attributes or by `distinguishedName`.
  `VlvRequestControl(before, after, offset)` counts the offset from 1, the variant with `target` implements "jump to
  'Kow…'"; we send back the `ContextId` and `EstimateCount`; `ContentCount` is an estimate; error 76 =
  `virtualListViewError`.
- Without a VLV index (`searchFlags` 0x40, WS2008+) or a per-container index (0x2) the DC builds a temporary table
  limited by `MaxTempTableSize` (10 000, hard limit 100 000), which on large containers is slow or ends in an error
  (exact code – to be verified in the lab). ADUC does not use VLV – this is what is supposed to give a noticeable
  advantage on large OUs.
- The collation order is defined by the server. Collation rule OIDs for languages exist (approx.
  `1.2.840.113556.1.4.1461`–`…15xx`), but Polish was not confirmed (to be verified in the lab, together with the order
  of names with ą/ł/ż).

| Situation | Strategy |
|---|---|
| Expanding a tree node | OneLevel with paging, container classes only, minimal attributes |
| Container list, `msDS-Approx-Immed-Subordinates` ≤ approx. 2000 | paging, the whole set in the view's memory, local sorting with a `pl-PL` comparer |
| Container list > approx. 2000 | VLV + one indexed key; on error 76/53/11 or a temporary table fall back to paging, a "narrow the filter" banner, sorting only by indexed columns |
| Quick search | `(anr=…)`, `SizeLimit` 50 |
| Advanced search | paged Subtree, streamed |
| Property page | base read + constructed attributes; ranged retrieval when the attribute name contains `;range=` |
| Writes | Batch lane only |

### 6.6 LDAP policy limits on the DC

The policies are in `queryPolicy` objects (attribute `lDAPAdminLimits`) under
`CN=Query-Policies,CN=Directory Service,CN=Windows NT,CN=Services,CN=Configuration` and can be read at run time; their
names are listed in `supportedLDAPPolicies` in the RootDSE.

| Policy | Default | Hard limit (WS2008+) | Meaning for AD_MNG |
|---|---|---|---|
| `MaxPageSize` | 1000 | 20 000 | page size ≤ 1000 |
| `MaxValRange` | 1500 (min. 30) | 5000 | ranged retrieval for `member` |
| `MaxTempTableSize` | 10 000 | 100 000 | sorting/VLV limit without an index |
| `MaxQueryDuration` | 120 s | 1200 s | `TimeLimit` ≤ 120 s |
| `MaxResultSetSize` | 262 144 B | — | total page/VLV state on the server |
| `MaxResultSetsPerConn` | 10 (`MinResultSets` 3) | — | max. approx. 8 open pagings per connection |
| `MaxNotificationPerConn` | 5 | — | LRU of notification registrations |
| `MaxConnIdleTime` | 900 s | — | the server closes idle connections; notification registrations are lost |
| `MaxConnections` | 5000 per DC | — | |
| `MaxReceiveBuffer` | 10 485 760 B | 20 971 520 B | limits the number of `member` values in one write |
| `MaxPoolThreads` | 4 per CPU | — | do not flood the DC with writes |
| `InitRecvTimeout` | 120 s | — | |
| `MaxActiveQueries` | 20 | not enforced since 2003 | |

### 6.7 Ranged retrieval and ASQ

- **Ranged retrieval:** base read `member;range=0-*`; the DC returns e.g. `member;range=0-1499`, so we iterate over
  `AttributeNames` and ask for `member;range=1500-*` until the name ends with `-*`. Without this, `member` is
  **silently truncated** to 1500 values (max. 5000). ADSI recommends requests for at most 1000; there is a "no-error"
  control `.1948`.
- **ASQ** (`AsqRequestControl("member")`, `1.2.840.113556.1.4.1504`): a base search on a group returns the member
  objects with a filter and selected attributes (no N+1 queries); one source attribute of DN type; VLV and sorting can
  be layered on top. ASQ with paging – to be verified in the lab.
- `member` and ASQ skip members through the primary group – separately `(primaryGroupID=<RID from primaryGroupToken>)`.
  On a DC that is not a GC, `memberOf` does not see universal groups from other domains.

### 6.8 Filters, ANR and alternative DN forms

- Bitwise rules accept only decimal numbers: `BIT_AND` `1.2.840.113556.1.4.803`, `BIT_OR` `.804`, e.g. disabled
  `(userAccountControl:1.2.840.113556.1.4.803:=2)`, security groups
  `(groupType:1.2.840.113556.1.4.803:=2147483648)`. `IN_CHAIN` `1.2.840.113556.1.4.1941` (e.g.
  `(memberOf:1.2.840.113556.1.4.1941:=<group DN>)`) gives transitive membership, but is expensive with a large
  fan-out.
- **ANR** is prefix-based and indexed; we avoid searching in the middle of a word (`*kow*`) unless the attribute has a
  tuple index (0x20). Search-as-you-type: delay of 250–300 ms, min. 2 characters, `SizeLimit` approx. 50, minimal
  attribute list, `Abort` of the previous request, discarding late results by sequence number, `sizeLimitExceeded`
  treated as success.
- User input is escaped according to RFC 4515 (`\2a`, `\28`, `\29`, `\5c`, `\00`) – otherwise LDAP filter injection is
  possible.
- AD accepts `<GUID=…>`, `<SID=…>` and `<WKGUID=…,DC=…>` as a DN (resistant to rename and move); adding
  `<SID=S-1-5-…>` to `member` automatically creates a `foreignSecurityPrincipal`.

### 6.9 Controls – OID table

The OIDs of controls that have a class in S.DS.P were checked in the dotnet/runtime code; the others come from
Microsoft documentation and must be confirmed in `supportedControl`.

| Control / rule / operation | OID | S.DS.P class | Use in AD_MNG |
|---|---|---|---|
| Paged Results | `1.2.840.113556.1.4.319` | `PageResultRequestControl` | lists, search |
| Server-side sort (request / response) | `1.2.840.113556.1.4.473` / `.474` | `SortRequestControl` | column sorting, required by VLV |
| VLV (request / response) | `2.16.840.1.113730.3.4.9` / `.10` | `VlvRequestControl` | large containers, jump to letter |
| ASQ | `1.2.840.113556.1.4.1504` | `AsqRequestControl` | group members with attributes |
| Domain Scope | `1.2.840.113556.1.4.1339` | `DomainScopeControl` | no continuation references |
| Search Options (DomainScope=1, PhantomRoot=2) | `1.2.840.113556.1.4.1340` | `SearchOptionsControl` | forest-wide search through the GC |
| Show Deleted | `1.2.840.113556.1.4.417` | `ShowDeletedControl` | AD Recycle Bin |
| Show Recycled / Show Deactivated Link | `1.2.840.113556.1.4.2064` / `.2065` | generic `DirectoryControl` | AD Recycle Bin, deactivated links |
| Tree Delete | `1.2.840.113556.1.4.805` | `TreeDeleteControl` | deletion with children (behavior on very large subtrees – to be verified in the lab) |
| SD Flags | `1.2.840.113556.1.4.801` | `SecurityDescriptorFlagControl` | reading the DACL without SACL rights (0x7), writing the DACL only (0x4) |
| Permissive Modify | `1.2.840.113556.1.4.1413` | `PermissiveModifyControl` | idempotent adding/removing of values |
| Lazy Commit | `1.2.840.113556.1.4.619` | `LazyCommitControl` | optional for very large imports |
| Extended DN | `1.2.840.113556.1.4.529` | `ExtendedDNControl` | `<GUID=…>;<SID=…>;DN` in results |
| Verify Name | `1.2.840.113556.1.4.1338` | `VerifyNameControl` | validation of DNs from other domains through the GC |
| Cross Domain Move | `1.2.840.113556.1.4.521` | `CrossDomainMoveControl` | out of scope for v1 |
| Quota | `1.2.840.113556.1.4.1852` | `QuotaControl` | — |
| Policy Hints | `1.2.840.113556.1.4.2239` (obsolete `.2066`) | generic, value `BerConverter.Encode("{i}", 1)` | password history on administrative reset |
| Change Notification | `1.2.840.113556.1.4.528` | `DirectoryNotificationControl` | live refresh |
| DirSync | `1.2.840.113556.1.4.841` | `DirSyncRequestControl` | **rejected** (6.11) |
| Range retrieval no-error | `1.2.840.113556.1.4.1948` | generic | ranged retrieval |
| Stats | `1.2.840.113556.1.4.970` | generic | query plan diagnostics (development builds) |
| BIT_AND / BIT_OR / IN_CHAIN | `1.2.840.113556.1.4.803` / `.804` / `.1941` | rules in a filter | state filters, nesting |
| WhoAmI (extended operation, RFC 4532) | `1.3.6.1.4.1.4203.1.11.3` | `ExtendedRequest` | identity verification after bind |
| StartTLS (extended operation) | `1.3.6.1.4.1.1466.20037` | `StartTransportLayerSecurity` | TLS mode on 389 |

### 6.10 Constructed attributes

They are not returned for `*` – they have to be requested by name; some are expensive for the DC and returned or
meaningful only in a base read; none can be used in a filter (`inappropriateMatching`).

| Attribute | OID | What it gives | Notes |
|---|---|---|---|
| `allowedAttributesEffective` | `1.2.840.113556.1.4.914` | attributes the bound principal can write | field locking; without extended rights, deletion and moving |
| `allowedChildClassesEffective` | `1.2.840.113556.1.4.912` | classes it can create in the container | New menu |
| `sDRightsEffective` | `1.2.840.113556.1.4.1304` | bits 0x1 owner, 0x4 DACL, 0x8 SACL | Security tab |
| `msDS-User-Account-Control-Computed` | `1.2.840.113556.1.4.1460` | 0x10 LOCKOUT, 0x800000 PASSWORD_EXPIRED, 0x4000000 PARTIAL_SECRETS, 0x8000000 USE_AES_KEYS | lockout and password expiry state |
| `msDS-UserPasswordExpiryTimeComputed` | `1.2.840.113556.1.4.1996` | password expiry date (FILETIME) | the only reliable one with FGPP |
| `msDS-ResultantPSO` | — | effective FGPP policy | password generator, policy preview |
| `msDS-PrincipalName` | `1.2.840.113556.1.4.1865` | `DOMAIN\name` | |
| `canonicalName` | `1.2.840.113556.1.4.916` | canonical form | address bar |
| `msDS-Approx-Immed-Subordinates` | `1.2.840.113556.1.4.1669` | approximate number of children (±10%) | expander, choosing paging/VLV |
| `primaryGroupToken` | — | group RID | members through the primary group |
| `tokenGroups` | `1.2.840.113556.1.4.1301` | SIDs of transitive membership | base read only, requires a GC, expensive; variants `tokenGroupsNoGCAcceptable`, `tokenGroupsGlobalAndUniversal` |
| `msDS-ReplAttributeMetaData` / `msDS-ReplValueMetaData` | — | last change of an attribute / link value | History tab |

We obtain the identity of the bound principal with the WhoAmI operation. Alternatively `tokenGroups` from the RootDSE
(to be verified in the lab) or a base read of the operator's own object.

### 6.11 Change notifications and their limits

- `LDAP_SERVER_NOTIFICATION_OID` `1.2.840.113556.1.4.528` (`DirectoryNotificationControl`): an asynchronous search
  with the filter **exactly** `(objectClass=*)` (any other filter gives `unwillingToPerform`), Base scope (an open
  page) or OneLevel (a visible container); Subtree is expensive.
- **Max. 5 registrations per connection** (`MaxNotificationPerConn`) – we maintain an LRU of registrations on a
  separate connection. Notifications are local to the DC (changes from other DCs arrive after replication); renaming
  or moving an **ancestor** does not generate them; after `MaxConnIdleTime` (900 s) or a DC restart the registrations
  are lost silently – this has to be detected, re-registered, and the state read anew.
- Registration first, then reading the initial state; objects are identified by `objectGUID`; deletions arrive with
  `isDeleted=TRUE` and a new DN. In S.DS.P: `ReturnPartialResultsAndNotifyCallback`, limit `TimeSpan.FromDays(7)`,
  `GetPartialResults` in the callback; delay of approx. 250 ms × the number of partial operations in the process.
- **DirSync** (`1.2.840.113556.1.4.841`) requires Subtree scope from the NC root, the DS-Replication-Get-Changes right
  (or the ObjectSecurity flag) and at first returns everything; cookies are sometimes rejected between DCs
  (`0000203D`) – rejected.
- **Fallback:** every 10–30 s a cheap read of `highestCommittedUSN` from the RootDSE, and after a change a query
  `(uSNChanged>=N)` in the visible container (`uSNChanged` is local to the DC, so it requires pinning).

### 6.12 DC pinning, locator, GC and multiple domains

- The DC is chosen by `DsGetDcName` (netapi32, `LibraryImport`, the result is released with `NetApiBufferFree`) with
  the flags `DS_DIRECTORY_SERVICE_REQUIRED` 0x10, `DS_WRITABLE_REQUIRED` 0x1000 (no RODC), `DS_IS_DNS_NAME` 0x20000,
  `DS_RETURN_DNS_NAME` 0x40000000, optionally `DS_TRY_NEXTCLOSEST_SITE` 0x40000, `DS_PDC_REQUIRED` 0x80,
  `DS_GC_SERVER_REQUIRED` 0x40, `DS_DIRECTORY_SERVICE_6/8_REQUIRED`; `DS_FORCE_REDISCOVERY` 0x1 only after a failure.
  Netlogon caches results and takes sites into account, so we do not compose SRV records by hand.
- **A session pinned to a DC** ([ADR-0005](adr/0005-session-pinned-to-dc.md)) gives read-your-own-writes, a stable
  `uSNChanged`, working notifications and reliable uniqueness checks; no silent switching, with "Change domain
  controller" available. Password reset and unlock can be directed to the PDC or a DC from the user's site – the window
  shows which DC performed the operation.
- The **GC** (3268/3269) is used only for reading PAS attributes; forest-wide search with PhantomRoot or the base `""`;
  writes always go to the DC of the object's domain (mapping the DN suffix to a domain). **Forest domains:**
  `(&(objectClass=crossRef)(systemFlags:1.2.840.113556.1.4.803:=2))` under `CN=Partitions` (`nCName`, `dnsRoot`,
  `nETBIOSName`), each with its own pinned session; a `Referral` result is directed explicitly to the right session,
  we never chase referrals. WS2025 improves the NetBIOS→DNS mapping in the locator and does not use mailslots.

### 6.13 AD Recycle Bin – mechanics (client priority)

- **Requirements:** forest level 2008 R2+; enabling it (`Enable-ADOptionalFeature … -Scope ForestOrConfigurationSet`)
  is **irreversible** and enlarges NTDS.DIT on every DC; the documentation says Enterprise Admins in one place and
  Domain Admins in another. Only objects deleted after it was enabled can be restored.
- **Detection:** `msDS-EnabledOptionalFeatures` with the feature GUID `766ddcd8-acd0-445e-f3b9-a7f9b6744f2a` (to be
  verified in the lab). The deletion window shows "restorable for N more days" (`msDS-deletedObjectLifetime`,
  `tombstoneLifetime`) or "NOT restorable – the tombstone loses most attributes and group links".
- **Browsing:** base `<WKGUID=18e2ea80684f11d2b9aa00c04f79f805,DC=…>`, Show Deleted `.417` (and Show Recycled
  `.2064`), `(isDeleted=TRUE)` with paging, server filters by `lastKnownParent`, `whenChanged` (approximates the
  deletion time), `msDS-LastKnownRDN`, name, type, UPN.
- **Restore** (MS-ADTS 3.1.1.5.3.7): a single modification with Show Deleted that **removes** `isDeleted` (setting it
  to FALSE does not work) and replaces `distinguishedName`; requires the Reanimate-Tombstones right (changing the ACL
  of a deleted object – also `WRITE_DAC` and `WRITE_OWNER`). "Restore To" = a different parent.
- **Improvement over ADAC:** an OU tree is restored in one operation in "parent before child" order determined from
  `lastKnownParent`. Objects past `msDS-deletedObjectLifetime` are unrecoverable; Configuration and DNS zones require
  separate handling.

### 6.14 RootDSE, schema and display specifiers

- **RootDSE** (reading `""` with an explicit attribute list): naming contexts (`defaultNamingContext`,
  `configurationNamingContext`, `schemaNamingContext`, `rootDomainNamingContext`, `namingContexts`), `dnsHostName`,
  `serverName`, `dsServiceName`, `supportedControl`, `supportedCapabilities`, `supportedLDAPPolicies`,
  `supportedSASLMechanisms`, functional levels (as in 2.15), `isGlobalCatalogReady`, `isSynchronized`,
  `highestCommittedUSN`, `currentTime`, `subschemaSubentry`.
- **Schema** (once per session, paged OneLevel): `attributeSchema` (`lDAPDisplayName`, `attributeSyntax`,
  `oMSyntax`, `isSingleValued`, `rangeLower`/`rangeUpper`, `systemOnly`, `searchFlags`, `systemFlags` – 0x4 marks a
  constructed attribute, `linkID` – even is a forward link, odd a back link, `isMemberOfPartialAttributeSet`,
  `schemaIDGUID`) and `classSchema` (`mustContain`/`mayContain` with auxiliary classes, `possSuperiors`, `rDNAttID`).
- `searchFlags` bits: 0x1 index, 0x2 per-container index, 0x4 ANR, 0x10 copied, 0x20 tuple index, 0x40 VLV index, 0x80
  confidential. They determine the editors, validation, sortable columns, the ANR set, "Copy user" and availability in
  the GC. The schema, display specifiers and the `rightsGuid`/`schemaIDGUID` maps are kept only in the session's memory.

### 6.15 Prototype measurements

The S.DS.P prototype was run on the lab [`tools/testlab`](../../tools/testlab/README.md): Samba 4.19 AD DC, a client on
Linux, a connection within one machine, lab binding (the production application does not use it).

| Bind | First page (100 objects) | 428 objects | 20 parallel queries on 1 connection | VLV + sorting | ASQ | `allowedAttributesEffective` |
|---|---|---|---|---|---|---|
| 16 ms | 65 ms | 174 ms | works | works | works | returned |

This is the order of magnitude of client and protocol overhead, not the behavior of a Windows DC on a LAN with Kerberos
and encryption. On Windows Server 2025 the measurements have to be repeated (to be verified in the lab) against the
Phase 0 goals: first page < 300 ms on a LAN for an OU with 100k objects, no UI frame with work > 16 ms, window visible
< 1 s, Esc cancels the query by sending an abandon.

### 6.16 Error mapping

S.DS.P throws `LdapException` (client and transport: 49, 81, 82 – often SSPI/SPN, 85, 91; `ServerErrorMessage` with
bind diagnostics) or `DirectoryOperationException` (`ResultCode`, `ErrorMessage`, `MatchedDN`, `Referral`). The AD
`ErrorMessage` starts with an 8-digit Win32 code; we take the message from `new Win32Exception(code).Message`
(localized by the system), add a hint, and show the raw DSID text under "Details". Some of the mappings were given by
the researchers from memory (to be verified in the lab).

| LDAP code | Win32 code / subcode | Meaning and message |
|---|---|---|
| 8 strongerAuthRequired | `00002028` | no signing/sealing or TLS |
| 10 referral | `0000202B` | write to an RODC or a wrong NC – redirect to a writable DC |
| 13 / 53 | `0000001F` | the operation requires an encrypted connection |
| 16 noSuchAttribute | | CAS conflict – someone changed the value |
| 19 / 53 | `0000052D` | password policy (length, complexity, history, minimum age) |
| 19 / 68 | `000021C7` / `000021C8` | SPN / UPN not unique in the forest (WS2012 R2+) |
| 20, 68 | `00000524`, `00002071` | value or object already exists |
| 32 noSuchObject | `0000208D` | "does not exist or no permissions" (List Object mode) |
| 50 insufficientAccessRights | `00000005`, `00002098` | which right, on which object and DC; "copy details for escalation" |
| 53 / 66 | `0000055E` / `00002015` | removal from the primary group / the object has children (Tree Delete needed) |
| 11, 51, 52, 81 | | overload or unavailability – retry with a back-off |
| 49 invalidCredentials | `data` subcodes | 525 no such user, 52e wrong password, 530 logon hours, 531 workstation, 532 password expired, 533 account disabled, 701 account expired, 773 must change password, 775 locked out |

---

## 7. User interface

### 7.1 WPF Fluent in .NET 10 – status and known bugs

- Fluent appeared in .NET 9: `Application.ThemeMode` / `Window.ThemeMode` (`None` = Aero2, `Light`, `Dark`, `System`)
  or manually including `Fluent.xaml`. `ThemeMode` also controls the Mica background and the dark title bar; the
  accent comes from `SystemColors` (`AccentColor`, Light1–3/Dark1–3) and we use it exclusively through
  `DynamicResource`.
- **Experimental status:** `ThemeMode` and `Application.ThemeMode` carry `[Experimental("WPF0001")]` also on `main`
  (.NET 11 RC1), with a note that the API may disappear; the code requires `NoWarn`/`#pragma` (setting it in XAML
  reportedly does not trigger the diagnostic). With the application's `ThemeMode` other than `None` a single window
  cannot opt out. We keep the whole API behind `IThemeService` (the only place with WPF0001 disabled), and pin the SDK
  in `global.json` ([ADR-0001](adr/0001-wpf-net10-fluent.md)).
- .NET 10 added styles for DatePicker, GridSplitter, GridView, GroupBox, Label and TextBox, among others, and fixed
  crashes in high-contrast mode; 51 style files cover all the controls an ADUC clone needs.
- **Open bugs:** epic #10393, #10445 (DataGrid style), #10911 (ListView/GridView), #10962 (DataGrid loses the scroll
  position after a reload), #10986 (DataGrid header loses its style with `HorizontalContentAlignment`), #9562,
  #8550 (opt-out mechanism), #8991 (Win11 tracker).
- **Missing:** NavigationView, InfoBar, ContentDialog, AutoSuggestBox, BreadcrumbBar, NumberBox, ProgressRing,
  ToggleSwitch, TabView, TeachingTip, Snackbar, CommandBarFlyout. We build a small `AdMng.Controls` set (command
  palette, BreadcrumbBar, InfoBar/toast, closable tabs, ProgressRing, compact styles), using MIT code from WPF UI where
  needed.
- Fluent sizes are "touch-oriented" – compact styles (row of 24–28 px) `BasedOn` Fluent are needed. Segoe Fluent Icons
  exist only in Windows 11 (Segoe MDL2 Assets as a fallback in Windows 10). We do not use the **lepoco/wpfui** library
  (4.3.0, approx. 419 open issues, a single release maintainer) as a theme, because it has a parallel theming system.

### 7.2 Stack comparison

| Stack | Pros | Cons | Verdict |
|---|---|---|---|
| **WPF .NET 10 + built-in Fluent** | mature, dense data controls with virtualization; best UIA/Narrator; VS designer; CommunityToolkit.Mvvm; easy MSI | WPF0001; DataGrid bugs; some controls missing; no Native AOT or trimming; no automatic text scaling | **chosen** |
| WPF + the WPF UI library as a theme | fills in the missing controls | a second theming system, a single maintainer | selected MIT controls only |
| WinUI 3 / Windows App SDK 2.x (2.5.1 stable, IX 2026) | the newest Fluent, `x:Bind`, text scaling | no stable DataGrid (TableView only in 2.5.4-experimental from 30.09.2026); no XAML designer; heavier environment; less dense | rejected |
| Avalonia 12 | fast rendering, mature Fluent | the best grid (TreeDataGrid) has been paid (Accelerate) since X 2025, the free DataGrid only with fixes; UIA accessibility on Windows less mature (to be verified in the lab); cross-platform support unnecessary | rejected |
| Web application with a gateway (WAC style) | no installation, excellent browser grids | the gateway becomes a Tier-0 asset, Kerberos delegation or credential forwarding needed, extra latency | rejected |

Plan B: a commercial grid (DevExpress GridControl with `InfiniteAsyncSource`/`PagedAsyncSource`, Telerik, Syncfusion),
decided after a performance test in Phase 0 (100k objects, 60 keystrokes/s over RDP, first page < 300 ms).

### 7.3 UI virtualization

- One recipe for lists and the tree: `IsVirtualizing=True`, `VirtualizationMode=Recycling`, `ScrollUnit=Item` for lists
  with a fixed row height (`Pixel` for the tree), `CacheLength="1,1"` in pages, `CanContentScroll=True`; in the bulk
  editor's DataGrid additionally `EnableColumnVirtualization=True` (off by default). The main list is a ListView +
  GridView (fewer layers per cell than a DataGrid).
- **Virtualization is silently disabled by:** a list inside a ScrollViewer, a vertical StackPanel or a Grid row with
  Auto height; `CanContentScroll=False`; a non-virtualizing panel; grouping without `IsVirtualizingWhenGrouping`; an
  `IEnumerable` source instead of `IList`; `Auto`/`SizeToCells` columns; `Items` instead of `ItemsSource`. A UI test
  compares the number of realized containers with the number of visible rows.
- Lightweight rows: ready-made texts, `Mode=OneTime`, no converters on the critical path, frozen shared icons, fixed or
  star widths; with 100k+ rows `IsDeferredScrollingEnabled` or delaying page fetches while the scrollbar is dragged.

### 7.4 Data virtualization, sorting and selection

- `VirtualDirectoryList` (a non-generic `IList` + `INotifyCollectionChanged`) based on VLV: `Count` from
  `ContentCount`, the indexer returns a placeholder row ("Loading") and requests a page of 100–250 rows, and when it
  arrives fills in the placeholders. An LRU of 5–10 pages in RAM only, cleared on navigation, F5, a change of filter,
  sort order and identity – this is a view buffer, not a copy of the directory.
- An `ICollectionView` with sorting or a filter over a virtualized list enumerates everything (every page = an LDAP
  query) and sorts partial data – forbidden. Clicking a header: `e.Handled = true` and a new query with a sort key,
  only for indexed columns. Small, complete sets: `ListCollectionView.CustomSort` with a `pl-PL` comparer, a separate
  view per control, `DeferRefresh()`, no "live shaping"; pages are appended in bulk, not item by item.
- The selection is a `HashSet<objectGUID>`, not references to rows. Ctrl+A on 50k rows means a **query selection**
  ("All 12,345 objects matching the filter in OU=Pracownicy" – a Gmail-style banner), which the bulk engine streams
  again at execution time.

### 7.5 The tree

The tree contains containers only (`domainDNS`, `organizationalUnit`, `container`, `builtinDomain`; in the advanced
view also `lostAndFound`, `msDS-QuotaContainer`, etc.). A `HierarchicalDataTemplate` over a node model with a single
placeholder child to show the arrow; `IsExpanded` bound two-way. The first expansion = a OneLevel query for containers
with `(!(showInAdvancedViewOnly=TRUE))` when the advanced view is off; collapsing cancels loading. With virtualization
`ContainerFromItem` returns `null` for unrealized nodes, so navigating to a DN (address bar, search) expands the
ancestors one by one in the view models, waits for their children and uses `BringIndexIntoViewPublic`. F5 on a node
refreshes only its children and preserves expansions by DN.

### 7.6 UX patterns

| Pattern | Description | Inspiration |
|---|---|---|
| Three panes | tree / list / a collapsible details pane with key attributes, state markers and quick actions (reset password, unlock, enable/disable, add to group, move, copy DN) | ADUC, WAC, Entra |
| Modeless property pages | tabs or windows, several at once, OK/Cancel/Apply, refresh, saving only the differences | ADUC (page parity) |
| Command palette (Ctrl+K) | fuzzy matching in the command registry (id, EN/PL title, keywords, shortcut, context, `CanExecute`); prefixes `>` commands, `@` objects (ANR), `/` go to OU | VS Code |
| Global search (Ctrl+E or `/`) | ANR with a delay, results grouped by type; Enter – details pane, Ctrl+Enter – new tab, Alt+Enter – properties | ADAC Global Search |
| Address bar (Ctrl+L) | canonical path (`contoso.test › Firma › Pracownicy › IT`), a list of children at each segment; edit mode accepts a DN, `canonicalName` or `<GUID=…>` | ADAC, Explorer |
| Filter chips | quick filters combined with AND, always executed as an LDAP filter on the server (4.5) | Entra |
| Configurable views (client requirement) | "workspaces" for OU divisions (e.g. Pracownicy, Komputery, Kontrahenci, Wyłączone): pinned OUs (objectGUID + DN, a warning when the target disappears), saved queries, column sets, chips, templates assigned to OUs, versions for roles (helpdesk/administrator); JSON export/import and a link to the view; saved only as configuration | ADAC (nodes), Entra ("Copy link to current view") |
| Tabs (Ctrl+T/W/Tab) | a container, search or query in a tab, each with its own back/forward history | browsers |
| Activity center (Ctrl+Shift+A) | a queue of writes and bulk tasks: Queued/Running/Succeeded/Partial/Failed/Cancelled, progress, per-object results, "Retry failed", cancel, "Copy report"; a marker in the status bar | Entra "Bulk operation results" |
| In-app notifications | InfoBar/toasts in the bottom right corner, history for the session only, announced through UIA; **no DNs or account names in system notifications** (the Action Center writes them to disk) | |
| Identity bar | principal, DC, encryption mode, tier color, Protected Users, time until the session expires | WAC "Manage as" |
| "As of" | every list shows the read time and count ("428 objects · 0.17 s · as of 14:35:02"), F5 reads again | |
| PowerShell history | a docked pane with equivalent commands, in memory only, no secrets | ADAC |
| Object comparison | two objects side by side, attribute by attribute | — |

### 7.7 Keyboard shortcuts

| ADUC/MMC-compatible | Additional |
|---|---|
| F5 refresh; Del delete (with confirmation and a protection check); F2 rename; Alt+Enter properties; Enter open; Shift+F10 / menu key; Tab/F6 switch panes; Backspace one level up; Alt+←/→ back/forward; Ctrl+A; `*`, `+`, `-` in the tree; typing letters = jump in the list (VLV) | Ctrl+K palette; Ctrl+E or `/` global search; Ctrl+F search in the container; Ctrl+Shift+F advanced search; Ctrl+L address bar; Ctrl+T/W/Tab tabs; Ctrl+C name, Ctrl+Shift+C DN; Ctrl+Shift+A activity center; Esc cancel; Ctrl+Plus/Minus/0 zoom; F1 help |

All shortcuts come from a single command registry, which also feeds the menus, the palette and the tooltips; every
action has a keyboard path and an access key (`_` in the label). Changing the assignments – in a later phase.

### 7.8 Accessibility

`AutomationProperties.Name` for icon-only elements, graphic cells and the search box; `LabeledBy`, `HelpText`,
`AcceleratorKey`; live regions (`LiveSetting=Polite`) for messages such as "Loaded 250 of approx. 12,000"; rows with a
readable name, placeholders read "Loading". Only `DynamicResource` brushes, font icons inheriting `Foreground` (high
contrast), status always with an icon and text. WPF does not apply the "Text size" setting by itself – we read
`UISettings.TextScaleFactor` (TFM `net10.0-windows10.0.19041.0`) and offer zoom; reduced motion when animations are
turned off and in RDP (`SystemParameters.IsRemoteSession`); a `PerMonitorV2` manifest. Tests: Narrator, NVDA,
Accessibility Insights, FlaUI; target WCAG 2.2 AA / EN 301 549 (contrast 4.5:1).

### 7.9 PL/EN localization

- `.resx`: English as the neutral language, Polish as a satellite; `SatelliteResourceLanguages=en;pl`; switching the
  language at run time through a binding source with an indexer. `FrameworkElement.LanguageProperty.OverrideMetadata(...)`
  with the current culture – otherwise bindings format dates and numbers like en-US.
- Three plural forms (1 użytkownik / 2–4 użytkownicy / 5+ użytkowników) are handled by a helper, not by
  `string.Format`; Polish labels are approx. 30% longer and the layout has to withstand that.
- Class and attribute labels from `CN=415` (as in the Polish ADUC), with a fallback to `CN=409` and to our own
  resources (Samba has no `CN=415`). Local sorting with a `pl-PL` comparer; the server's collation order with Polish
  characters – to be verified in the lab.

### 7.10 Presentation structure, diagnostics and distribution

- **MVVM:** CommunityToolkit.Mvvm 8.4.2+ (`[ObservableProperty]` on partial properties,
  `[RelayCommand(IncludeCancelCommand = true)]`, `ObservableValidator`, `WeakReferenceMessenger` as a "read again"
  signal, not a cache); view models in `net10.0` without WPF types; Generic Host; handling of unhandled exceptions; the
  window visible before connecting to the DC.
- **Diagnostics:** `[LoggerMessage]`, EventSource/EventLog; no DNs, values, filters, search text or credentials in
  persistent logs; details to ETW only; optional redaction (`Microsoft.Extensions.Compliance.Redaction`).
- **Environment:** .NET 10 LTS (support until 14.11.2028; .NET 8 and 9 end it on 10.11.2026). WPF does not support
  trimming or Native AOT – ReadyToRun speeds up startup.
- **Distribution:** a signed MSI installed per machine into Program Files (WiX; v6 license terms – to be verified),
  deployed through Intune, ConfigMgr or GPO. Single-file with `IncludeNativeLibrariesForSelfExtract` extracts native
  libraries to `%TEMP%`, which AppLocker/WDAC blocks on PAW workstations – a portable version only as an add-on. MSIX is
  inconvenient in combination with `runas /netonly`.
- **Signing:** Authenticode with an RFC 3161 timestamp, a certificate from an internal AD CS (publisher rules in
  AppLocker/WDAC) or Azure Trusted Signing, signing in CI only (the validity of public certificates is expected to drop
  to approx. 460 days – to be verified). A manifest of `asInvoker`, no auto-update from the internet and no telemetry.

---

## 8. Security and credentials

### 8.1 Windows Server 2025 – default settings the client must meet

- **LDAP signing** is required by default in new deployments (the "Domain controller: LDAP server signing requirements
  enforcement" policy takes precedence); upgraded forests keep their settings, but with no policy at all WS2025
  requires signing. WS2025 and Windows 11 24H2+ clients encrypt LDAP by default.
- **Channel binding** (`LdapEnforceChannelBinding`: 0 Never / 1 When supported / 2 Always): in new WS2025 "When
  supported", in 2022 and older "Never"; applies only to SASL binds over TLS.
- **Confidential attributes** (`searchFlags` 0x80, e.g. LAPS and BitLocker) only over an encrypted connection; LDAPS
  with TLS 1.3.
- The KDC does not issue TGTs with RC4 (accounts need AES keys), remote password changes through SAM-RPC are blocked
  (`unicodePwd` remains), setting the default computer account password is blocked, Credential Guard is enabled by
  default, and SMB signing is required for outbound connections. Microsoft recommends disabling NTLM wherever possible.
- **Timeline:** ADV190023 (2019) → the CBT policy and events 3039–3041 (March 2020) → CBT auditing by default (March
  2023) → events 3074/3075 (August/October 2023) → WS2025 defaults (November 2024). According to KB4520412 updates
  will not change the settings of existing DCs; reports of enforcement phases in 2026 are unconfirmed. Events:
  2886–2889 (signing), 3039–3041 and 3074/3075 (CBT).

### 8.2 Binding matrix

| Variant | Protection | Decision |
|---|---|---|
| 389 + SASL Negotiate restricted to Kerberos (`AuthType.Kerberos`) + `Signing` and `Sealing` | confidentiality, integrity, server identity, no PKI | **default** |
| 636 LDAPS (GC 3269) or StartTLS on 389 + SASL Kerberos; CBT added by the Windows client | the strongest configuration according to Microsoft (TLS + Kerberos + enforced CBT) | option; **without** SASL signing/sealing over TLS – AD rejects such a bind ("Cannot bind using sign/seal on a connection on which TLS or SSL is in effect"; text to be verified in the lab) |
| `AuthType.Negotiate` (allows NTLM) | as above, but silent NTLM is possible | disabled; only when an administrative policy allows it |
| Simple bind (`AuthType.Basic`), anonymous | on 389 no protection; with enforced signing error 8 strongerAuthRequired; over TLS the password reaches the server without CBT | **forbidden** (banned API analyzer) |

In S.DS.P, `AuthType.Kerberos` is still `LDAP_AUTH_NEGOTIATE`, but with `packageList = Kerberos`, which blocks falling
back to NTLM (required for Protected Users). We always connect to the controller's FQDN – an IP, a short name or a CNAME
without an SPN gives NTLM. LDAPS certificate: EKU Server Authentication (`1.3.6.1.5.5.7.3.1`), FQDN in CN or SAN (with
serverless binding also the domain name), a trusted chain; the NTDS service store takes precedence. The
`VerifyServerCertificate` callback takes over full responsibility for verification – either we do not set it, or we
build an `X509Chain` in it with revocation, EKU and name matching; never `(c, cert) => true`. The "Connection
security" panel shows `SslInformation`, the thumbprint and the certificate's validity.

### 8.3 Alternative credentials

| Method | Where the password is | Assessment |
|---|---|---|
| `NetworkCredential` passed to `LdapConnection` | S.DS.P keeps a copy (`_directoryCredential`) for the whole lifetime of the connection so it can retry the bind; the `Password` getter turns a `SecureString` into a plain `string` | rejected (`SecureString` on .NET Core does not encrypt – DE0001) |
| **`LogonUserW(LOGON32_LOGON_NEW_CREDENTIALS = 9, LOGON32_PROVIDER_WINNT50 = 3)` + `WindowsIdentity.RunImpersonated(token, () => conn.Bind())` with `Credential = null`** | in LSA (in LSAIso with Credential Guard); the process holds only a token handle, and the plaintext password exists in it for microseconds | **chosen** – "runas /netonly" in-process |
| Relaunch through `CreateProcessWithLogonW(LOGON_NETCREDENTIALS_ONLY)` | as above, but the identity is inherited by the whole process and child tools | fallback, should the in-process variant fail the Phase 0 test |
| External `runas /netonly` / "Run as a different user" | outside the application | works (the process's current identity) |
| `SMARTCARD_REQUIRED` accounts / smart card | cannot be supplied as a `NetworkCredential` | "Run as" or LDAPS with a client certificate and SASL EXTERNAL – later |

- Password from `PasswordBox.SecurePassword` → `Marshal.SecureStringToGlobalAllocUnicode` → `LogonUserW` →
  `ZeroFreeGlobalAllocUnicode`. A name in UPN format requires `lpszDomain = NULL`; it does not work with gMSA; it does
  not require `SeTcbPrivilege`; it is logged locally as event 4624 of logon type 9, which EDR systems observe.
- `NEW_CREDENTIALS` **does not verify the password**, so a bind and WhoAmI (`1.3.6.1.4.1.4203.1.11.3`) follow
  immediately, comparing the returned `u:DOMAIN\name`. `AutoBind = false`, `ReferralChasing = None` and our own
  reconnect (impersonation, bind, WhoAmI) – otherwise a re-bind on a thread without impersonation silently runs as the
  process identity. Reconnecting after an idle disconnect (900 s) does not require the password.
- The Phase 0 test checks that the DC sees Kerberos and not NTLM (4624 on the DC), that a reconnect does not ask for
  the password, and that a wrong password surfaces at bind.

**We do not use Windows Credential Manager.** A `CRED_TYPE_DOMAIN_PASSWORD` entry makes every process of the session
(Explorer, browser, Office) authenticate to the indicated hosts as the administrator; `CRED_TYPE_GENERIC` is protected
only by the user's DPAPI, so in practice any code of that user can read it; storing higher-tier credentials on a
lower-tier workstation breaks the tiered model. We remember only the user name, the domain and the preferred DC (a
possible exception: an ADMX policy, Tier-2 only, `CRED_PERSIST_SESSION`).

### 8.4 Protected Users and the session

Members of Protected Users (RID 525, DFL 2012 R2+): no NTLM, no DES and RC4 in pre-authentication, no delegation, a
TGT of at most 240 minutes without renewal, no cached credentials on the device. We therefore bind only with Kerberos
to the FQDN; when the operator's `tokenGroups` contains the SID `-525`, the session lasts at most 4 h with a countdown
in the identity bar; we translate `KDC_ERR_ETYPE_NOSUPP` as "the account has no AES keys – reset the password".
Independently of this, the idle lock (default 15 min, ADMX) and the maximum session time release the token and the
connections.

### 8.5 Tiered model and PAW

- Tier 0 is the identity plane (DCs, AD CS, AD FS, Entra Connect, their administrators, Backup/Server Operators and
  management systems), Tier 1 – servers and applications, Tier 2 – workstations and helpdesk. "Trust starts at the
  first physical keyboard"; every tier needs a PAW of the same tier; goal: fewer than 5 people with Domain Admin
  rights; the group manager (`managedBy`) always from the same tier.
- A tool used by a Tier-0 administrator itself becomes a Tier-0 interface: Authenticode signature, Program Files, no
  auto-update from the internet, plugins from writable locations or telemetry, compatibility with WDAC/AppLocker,
  Credential Guard and LSA protection.
- After the bind we classify the operator (well-known RIDs in `tokenGroups`) and the computer (the DN from
  `GetComputerObjectName` in the PAW OU or membership in the PAW group). Outside a PAW, "do not enter Tier-0
  credentials here" appears in front of the password field; a Tier-0 account outside a PAW means a red banner and a log
  entry or a block if the policy says so. Denials of authentication silos (Microsoft-Windows-Authentication events 101,
  105, 106, 305, 306; silos require FAST) are mapped to readable messages.

### 8.6 Permission-aware UI and its limitations

- When opening a property page or a context menu we send **one** base search with the displayed attributes plus
  `allowedAttributesEffective`, `allowedChildClassesEffective`, `sDRightsEffective` and
  `msDS-User-Account-Control-Computed`. A field outside `allowedAttributesEffective` is read-only with a padlock and an
  explanation; the New menu shows the classes from `allowedChildClassesEffective`
  ([ADR-0006](adr/0006-permission-aware-ui.md)). In lists and Subtree searches we do not request these attributes
  (CPU cost on the DC) – only when an object is selected and in batch validation.
- **Limitations:** they do not cover extended rights (Reset Password, Send As), validated writes, deletion, moving or
  reading. For these, AuthZ: `nTSecurityDescriptor` with SD Flags 0x7 and the operator's `tokenGroups` →
  `AuthzInitializeResourceManager(AUTHZ_RM_FLAG_NO_AUDIT)` → `AuthzInitializeContextFromSid(AUTHZ_SKIP_TOKEN_GROUPS)` →
  `AuthzAddSidsToContext` → `AuthzAccessCheck` with an `OBJECT_TYPE_LIST` (level 0: class, level 1: the GUID of the
  right or attribute) and the target's `PrincipalSelfSid`. Rules: deletion = `DELETE` 0x10000 or `DELETE_CHILD` 0x2 on
  the parent; tree = `DELETE_TREE` 0x40; creation = `CREATE_CHILD` 0x1; move = deletion at the source, `CREATE_CHILD` at
  the target and writing the name.
- The result is a **hint**, not a block: AuthZ does not see claims, conditional ACEs and special cases, and the
  authority is the DC. The optimistic path always works – we try and map error 50 to a specific message.
- `adminCount=1`: SDProp on the PDC every 60 min (`AdminSDProtectFrequency`, 60–7200 s) overwrites the ACLs of
  protected objects with the template from `CN=AdminSDHolder,CN=System` and turns off inheritance, so a helpdesk
  delegation on an OU does not cover them, and manual ACL changes disappear. We show a marker with an explanation
  before the operation is attempted and detect orphaned `adminCount` (the flag remaining after removal from a group
  and the effect of character 16 of `dSHeuristics` – to be verified in the lab).

| Right / set | GUID | Note |
|---|---|---|
| Reset Password (User-Force-Change-Password) | `00299570-246d-11d0-a768-00aa006e0529` | user, computer, inetOrgPerson, MSA |
| Change Password | `ab721a53-1e2f-11d0-9819-00aa0040529b` | "User cannot change password" = Deny on this right |
| Self-Membership / attribute `member` | `bf9679c0-0de6-11d0-a285-00aa003049e2` | validated write |
| Validated-DNS-Host-Name / Validated-SPN | `72e39547-7b18-11d1-adef-00c04fd8d5cd` / `f3a64788-5306-11d1-a9c5-0000f80367c1` | (to be verified in the lab) |
| Personal-Information | `77b5b886-944a-11d1-aebd-0000f80367c1` | phone numbers, addresses, but also `userCert`, `msDS-SupportedEncryptionTypes` |
| Public-Information | `e48d0154-bcf8-11d1-8702-00c04fb96050` | among others `title`, `department`, `company`, `manager`, but also **SPN, `altSecurityIdentities`, `msDS-AllowedToDelegateTo`, UPN** |
| General-Information | `59ba2f42-79a2-11d0-9020-00c04fc2d3cf` | `sAMAccountName`, `displayName`, `sIDHistory`… |
| User-Account-Restrictions | `4c164200-20c0-11d0-a768-00aa006e0529` | `userAccountControl`, `pwdLastSet`, `accountExpires`, since 2012 also RBCD |
| Membership | `bc0ac240-79a9-11d0-9020-00c04fc2d4cf` | `member`, `memberOf` |

**Risks of a typical helpdesk delegation** that the interface shows: the right to write `userAccountControl` allows
setting `DONT_REQ_PREAUTH` 0x400000 (AS-REP roasting), `PASSWD_NOTREQD` 0x20 and `DONT_EXPIRE_PASSWD` 0x10000 (the
delegation bits additionally require `SeEnableDelegationPrivilege`); delegating whole property sets exposes SPNs
(Kerberoasting), `altSecurityIdentities` (takeover through certificate mapping) and RBCD. In helpdesk mode we limit
bulk UAC changes to 0x2, and writes of these attributes and adding to groups nested in protected groups are
accompanied by a warning.

### 8.7 Tier-0 classification and privilege elevation

An object is Tier-0 when: `adminCount=1`; its RID or transitive membership hits 500, 502, 512, 516, 518, 519, 521,
526, 527, S-1-5-32-544/548/549/550/551/552 or DnsAdmins/GPCO; it has `SERVER_TRUST_ACCOUNT` 0x2000 or
`primaryGroupID` 516/521; `isCriticalSystemObject=TRUE`; it lies in the configured list of Tier-0 OUs (Entra Connect,
AD CS, AD FS accounts and servers); it is the domain head, AdminSDHolder, the Domain Controllers OU or the GPO
container. We also respect `systemFlags` (`FLAG_DISALLOW_DELETE` 0x80000000, `FLAG_DOMAIN_DISALLOW_RENAME` 0x08000000,
`FLAG_DOMAIN_DISALLOW_MOVE` 0x04000000 – to be verified in the lab). Non-Tier-0 sessions see such objects as read-only;
a change requires "Elevate privileges": a separate, short-lived connection with Tier-0 credentials (optionally on a
PAW only) and giving a reason. We block moving Tier-0 objects to non-Tier-0 OUs and adding members to groups nested in
protected groups. In the interface and documentation it is made clear: **this is protection against mistakes, not a
security boundary.**

### 8.8 Auditing

| Side | Source | Content |
|---|---|---|
| DC (authoritative) | 5136 (object change: principal SID and Logon ID, DN/GUID/class, attribute, "Value Deleted"/"Value Added" with the same Correlation ID), 5137 creation, 5138 restore, 5139 move, 5141 deletion (also of a tree), 4662 (extended right, confidential read; requires a SACL) | requires "Audit Directory Service Changes" and a SACL; the MDI recipe: an Everyone/Success audit entry at the domain root for descendant user, group, computer, gMSA, MSA objects |
| DC – accounts | 4720/4722/4723/4724/4725/4726/4738/4740/4767/4781 (users), 4741–4743 (computers), 4728/4729/4732/4733/4756/4757 (membership), 4624 type 3 (source workstation) | |
| Operator | an Event Log channel registered by the MSI (ETW manifest; an ordinary user cannot create a source): who, when, DC, objectGUID, operation, attribute names, result, correlation ID | **values only when policy allows; secrets never**; collected by WEF/SIEM |
| Operator (option) | JSONL in `%LOCALAPPDATA%` with a SHA-256 chain, owner-only ACL, retention per policy, full values/hash/none mode | **gray area** – disabled pending a client decision |

Correlation: an entry in the application log (DC, objectGUID, time) corresponds to event 5136 on that DC; its Logon ID
points to a type 3 event 4624 with the source workstation. Format precedent: Microsoft's AD Tier Model scripts (blocks
of 100 IDs, a START / COMPLETE / ERROR pattern, `key=value` fields, redaction of keys containing Password, Secret,
Token, Key, Credential). An optional audit readiness test reads the policies and the presence of a SACL (a SACL only
with `SeSecurityPrivilege`).

### 8.9 The "no local data" policy

Decision: [ADR-0003](adr/0003-no-local-data.md). Every query goes to the DC, and data lives in memory only as
long as it is displayed.

| Category | Examples | Rule |
|---|---|---|
| **Never on disk** | search results, object attributes, member lists, schema and display specifiers, VLV page buffers, rollback logs, generated passwords, LAPS/BitLocker values, credentials; DNs and values in diagnostic logs and system notifications | session memory only (a per-session DI scope, cleared on logoff, identity change and lock) |
| **Allowed as configuration** | UI preferences and column sets; connection profiles (user, domain, preferred DC – no passwords); saved views and queries (names, DNs of pinned OUs, LDAP filters, columns, sorting); templates, CSV mapping profiles, recipes; ADMX policies | JSON in `%APPDATA%`; a "no saving" mode for the cautious |
| **Gray area – client decision** | automatic history of recently visited containers (MRU); an operator log with before/after values; exports of CSV, plan, rollback log, PS1, LDIF; crash dumps | off by default or only on the operator's explicit command, with confirmation and an audit event |
| **Enforcement** | no file API in the Directory and Batch layers (banned API rule); export only through `ExportService`; secret buffers excluded from dumps, no full dumps; secrets excluded from clipboard history; log redaction | **a CI test with canaries:** a scenario on canary objects, then a search of `%APPDATA%`, `%LOCALAPPDATA%`, `%TEMP%` and the Event Log |
| **Rejected approaches** | DirSync (full synchronization from the NC root), AD Explorer-style snapshots, the ADSI property cache, any service or database (the ManageEngine/Adaxes/OpenADUC pattern) | — |

The gray area exists because a rollback log, an export or a report file with attribute values **are** local directory
data, yet are sometimes operationally needed. By default everything stays in memory, an export is an explicit operator
action (optionally encrypted with DPAPI `CurrentUser`), and we never automatically write a log, a cache or a snapshot.

### 8.10 Secret hygiene, GDPR and safeguards in code

- **Secrets in the interface:** only `PasswordBox.SecurePassword` and `Clear()` after use; a copied password with
  clipboard formats that exclude history, cloud and monitors (`ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory=0`, `CanUploadToCloudClipboard=0` – to be verified in the lab) and cleared after
  30–60 s; the password reveal window optionally with `WDA_EXCLUDEFROMCAPTURE`; secret buffers excluded from dumps
  (`WerRegisterExcludedMemoryBlock`); `unicodePwd` in a pinned array zeroed with `CryptographicOperations.ZeroMemory`.
- **GDPR:** exporting data from AD is processing of personal data, and the operator log is data about operators. By
  default only the visible columns, sensitive columns with an extra confirmation, never secrets (LAPS, BitLocker,
  passwords), an export event without data (who, when, filter, columns, row count, target type), a warning when saving
  to a cloud-synchronized, network or removable drive, a large copy of the grid to the clipboard counts as an export,
  optional ADMX switches (prohibition, row limit, required reason). Articles 5(1)(c), 5(1)(e), 25, 30, 32 and 33 are
  given from memory – to be confirmed with the DPO.
- **Code and build:** `BannedApiAnalyzers` (a ban on `AuthType.Basic`/`Anonymous`, `PasswordBox.Password`, the
  `NetworkCredential.Password` getter, `VerifyServerCertificate` outside the trusted validator); CA5392 on P/Invoke to
  `wldap32`, `authz`, `advapi32`, `credui`; an `asInvoker` manifest; tests on a WS2025 DC with enforced signing and
  `LdapEnforceChannelBinding=2`. A bind with a `NULL` credential under a UAC-restricted token may block writes if the
  application runs on the DC itself.

---

## 9. Bulk editing – best practices

### 9.1 Pipeline and plan model

Every entry point – selection, multi-object sheet, grid, CSV, template, wizard, menu action – creates a **change plan**
for a single engine ([ADR-0004](adr/0004-one-change-pipeline.md)). Editing a single object uses it with an
automatically approved preview, so validation, log and rollback work everywhere. Stages: **Collect → Resolve
identities → Plan → Validate live → Preview → Confirm → Execute → Verify → Report → (Roll back)**.

- `BatchJob`: operator identity (from WhoAmI), domain, pinned DC (`dnsHostName`, `dsServiceName`, `invocationId`),
  source, options (concurrency, `PermissiveModify`, error thresholds, dry-run mode). `ObjectPlan`: `objectGUID`, DN and
  `uSNChanged` from planning time, class, reference to the source (e.g. a CSV row), steps, state (Pending, Validated,
  Conflict, Applying, Applied, PartiallyApplied, Failed, Skipped, Unknown). `AttributeChange`: attribute, operation
  (Add/Delete/Replace/Clear), old and new values, syntax, cardinality, origin, issues (Blocker/Warning/Info).
- Steps in a fixed order: create → password → modify (**one `ModifyRequest` per object** – atomic according to RFC
  4511) → membership (write on the group) → rename → move → delete. Objects are addressed through `<GUID=…>`, so a move
  in the same batch does not break the following steps. Changes with no effect (equal according to the attribute's
  matching rule) and objects without changes disappear from the plan.
- AD has no multi-object transactions (RFC 5805 is not supported): a batch is N independent atomic operations with a
  per-object result.

### 9.2 Selection sources and editing methods

- **Sources:** multiple selection, selection by query (7.4), a saved query or a report result ("act on the results"), a
  basket with objects from different OUs (the Adaxes pattern), a pasted list resolved by `sAMAccountName`, UPN, `mail`,
  `employeeID`, DN or GUID with the status of each line (0 or > 1 matches flagged), a CSV with a key column.
- **Multi-object sheet:** parity with ADUC, but with three field states (unchanged / set / clear), a "mixed values"
  marker and any schema attribute for any class; tokens `%username%` and `{sAMAccountName}`.
- **Grid:** `DataGrid` with column virtualization and "dirty" edits (changes highlighted, errors in red with a
  tooltip), pasting TSV from Excel, Ctrl+D fill down, Ctrl+Enter apply to the selection, Delete = mark for clearing,
  Ctrl+Z. **Committing a cell writes nothing to AD** – writing happens only after the preview.

| Operation | LDAP implementation | Notes |
|---|---|---|
| Set / replace | Replace | idempotent |
| Clear | Replace with no values | idempotent; ignored when the attribute is absent |
| Add / remove a value | Add / Delete with `PermissiveModify` | `member`, `proxyAddresses`, `otherTelephone`, `servicePrincipalName`, `url` |
| Find and replace | Replace computed in the plan | text or regex with `RegexOptions.NonBacktracking` or a time limit, off the UI thread (ReDoS); number of hits before applying |
| Value map / expression with tokens | Replace | e.g. `title` → `department`; templates – 9.10 |
| UAC flags | read-modify-write | in helpdesk mode only 0x2 |
| Enable / disable / unlock | UAC 0x2 / `lockoutTime = 0` | |
| Account expiration | `accountExpires` | "end of day D" = (D+1) 00:00 local → UTC; "never" = `0x7FFFFFFFFFFFFFFF` (or 0) |
| Country / manager | `c` + `co` + `countryCode` together / `manager` (DN from the picker) | `directReports` is read-only |
| Move / rename | ModifyDN | protection-from-deletion check; computers are not renamed (that is done on the machine) |
| Membership | Add/Delete `member` on the group | 9.5 |
| Protection from deletion | DACL edit (object + parent) | explicit, logged option "remove protection → act → restore" |
| Marker in the description | Replace `description` | e.g. "Disabled 2026-10-13 by …: inactive 90 days" |
| Delete | Delete, Tree Delete for objects with children | typed confirmation, AD Recycle Bin status |

### 9.3 Validation

- **Schema** (from the session cache): syntax → type (2.5.5.12 Unicode, .9 Int32, .16 Int64/FILETIME, .1 DN, .10
  octets, .8 Boolean, .11 GeneralizedTime); `isSingleValued` (`description` is single-valued on SAM classes);
  `rangeLower`/`rangeUpper`; rejecting `systemOnly`, back links (we write the forward link) and constructed attributes;
  the attribute must be allowed for the object's classes.
- **Domain rules:** `sAMAccountName` max. 20 characters, without `" / \ [ ] : ; | = , + * ? < >` and a trailing dot,
  unique in the domain; UPN suffix from the forest's domains or `uPNSuffixes`; UPN unique in the forest (GC + batch);
  DNs in `manager`, `managedBy`, `member` must resolve (`VerifyNameControl` for other domains); consistent
  `c`/`co`/`countryCode`; `accountExpires` in the future (warning).
- **Permissions:** `allowedAttributesEffective` per plan object, `allowedChildClassesEffective` for target OUs,
  `sDRightsEffective` for DACL changes; extended rights and deletion through AuthZ (8.6) or "checked at execution".
- **Risk flags:** Tier-0, `adminCount=1`, `isCriticalSystemObject`, protection from deletion, objects with children,
  built-in groups.

| Attribute | Syntax, cardinality | Limit | Notes |
|---|---|---|---|
| `department`, `company` | 2.5.5.12, single | 64 | `fCOPY` |
| `title` / `physicalDeliveryOfficeName` | single | 128 | no `fCOPY`; Office is in ANR and indexed |
| `description` | multiple in the schema, single on SAM | 1024 | two values = error |
| `telephoneNumber`, `mobile`, `facsimileTelephoneNumber`, `ipPhone` / `otherTelephone` | single / multiple | 64 | |
| `employeeID` / `employeeNumber` | single | 16 / 512 | **no index and no uniqueness guarantee** |
| `employeeType`, `division` | single | 256 | `fCOPY` |
| `manager` / `managedBy` | DN, single | — | `linkID` 42 / 72; `manager` with `fCOPY` |
| `mail` / `proxyAddresses` | single / multiple | 256 / 1123 | index; `proxyAddresses` in ANR, case-insensitive comparison; uniqueness not enforced |
| `userPrincipalName` | single | 1024 | index, unique in the forest (WS2012 R2+) |
| `sAMAccountName` / `cn` | single / RDN | 20 in practice / 64 | forbidden characters / only through ModifyDN |
| `givenName`, `sn` / `initials` / `displayName` | single | 64 / 6 / 256 | |
| `info` / `wWWHomePage` / `location` | single | 1024 / 2048 / 1024 | |
| `c` / `co` / `countryCode` | single | 3 / 128 / int | `fCOPY`, always together |
| `extensionAttribute1–15` / `msDS-cloudExtensionAttribute1–20` | single | — | Exchange schema (detected) / WS2012+ |

The values come from the 2008 R2 schema dump; at run time we read them from the client's schema, which may have been
extended.

### 9.4 Optimistic concurrency

- `uSNChanged` read on the pinned DC during planning (it is local to a DC, so the comparison makes sense only thanks to
  pinning). Before execution we read `uSNChanged` and the planned attributes again; if the USN changed we compare only
  the planned attributes (otherwise false conflicts from e.g. `lastLogonTimestamp`). A changed attribute marks a
  **Conflict** with a choice: skip, overwrite, re-plan.
- **CAS** for single-valued attributes: Delete(old) + Add(new) in one `ModifyRequest` – atomic, and if someone changed
  the value, the DC answers with error 16. **Never together with `PermissiveModify`**, which suppresses this error. For
  attributes managed by SAM (e.g. `userAccountControl`) it is safer to re-read + Replace (to be verified in the lab).
- Idempotence: Replace and Clear are idempotent, Add/Delete of values – with `PermissiveModify`; a repeated create that
  fails with error 68 ends with a read and comparison (matching object = Applied, otherwise Conflict).

### 9.5 Execution, DC load and membership

- **One writable DC** for reading, validation, writing and verification ("applied on DC01, the other DCs will receive
  the changes through replication"). No silent switching: when the DC is unavailable, the job pauses and asks the
  operator, and after the DC is changed the pending objects are validated again.
- **Concurrency:** steps of one object sequentially, objects in parallel – 4 by default (range 1–16), adaptive (AIMD:
  halve on busy or p95 > 500 ms, +1 on stable success); token bucket of about 20 writes/s; chunks of 50–100 objects
  with a pause and resume; limits in ADMX; for thousands of objects a "throttled / off-hours" mode. `LazyCommit` only
  as an option, off by default.
- **Retries** only for 51, 52, 81, 85, 91 (waiting also for 11 and 3): max. 3 attempts with delays of 0.5/1.5/4 s and
  random jitter. No retries for: 16, 17, 19, 20, 21, 32, 34, 50, 53, 64–68; **access denials are never retried**.
  Automatic pause at more than 10% errors or 20 errors in a row.
- **Cancellation:** new objects are not sent, requests in flight complete; those interrupted after sending have the
  state **Unknown** and a read decides them. Optionally every write is verified by a read ("Applied – verified"). Every
  write increments the USN and replicates; GC attributes go to the whole forest, and `member` replicates per value.
- **Membership:** we write the forward link `member` on the group, multiple values in one request (about 500, within
  `MaxReceiveBuffer`) with `PermissiveModify`; on error we split the batch into halves to find the faulty DN; writes to
  one group are serialized. "Replace membership" = ranged retrieval, difference, incremental Add/Delete – **never a
  bulk Replace of `member`**. "Copy memberships A → B" skips the primary group. Scope rules are checked before the
  write: global – members only from the same domain; domain local – accounts and global/universal groups from the
  forest and trusts; universal – no domain-local groups. Foreign principals through `<SID=…>`; a group in another
  domain = a write to that domain's pinned DC.

### 9.6 Passwords, accounts and object creation

- **Password reset:** Replace `unicodePwd` = the password in quotes in UTF-16LE as `byte[]`; requires 128-bit
  encryption (sealing or TLS – signing alone is not enough) and the Reset Password right. The password, `pwdLastSet = 0`
  and `lockoutTime = 0` go in **one** `ModifyRequest`; Policy Hints (`.2239`) enforces password history. A change by the
  user themselves = Delete of the old + Add of the new password (Change Password right, minimum password age).
  `pwdLastSet = 0` does not work with `DONT_EXPIRE_PASSWD` – we warn like ADUC. A policy violation returns 19 with
  `0000052D`; the message lists the domain rules or the PSO.
- **Password generator** compliant with the domain policy or `msDS-ResultantPSO`; the password is shown once, never in
  logs or scripts; for helpdesk optionally only generated passwords and a checklist for verifying the caller (the
  Specops pattern).
- **New user:** (1) Add with `sAMAccountName` (always explicit, otherwise AD assigns a random name), UPN, names and
  `userAccountControl = 514`; (2) `unicodePwd`; (3) UAC 512 and `pwdLastSet = 0`; (4) memberships; (5) optionally steps
  outside LDAP (home directory). The per-step state allows resuming a partial creation or deleting the object by GUID.
  Without a password and an explicit UAC the account has 546 (`PASSWD_NOTREQD`), and 512 before a compliant password
  gives 53 with `0000052D`. The KB says that `unicodePwd` can only be modified; whether an Add with a password works
  over an encrypted connection – to be verified in the lab.
- **New computer:** `sAMAccountName = NAME$` (15 + `$`), UAC 4096 (not 4128 as in ADUC), an ACE for the principal
  allowed to join, the account reuse restrictions from KB5020276. We do not reproduce the "pre-Windows 2000" option and
  the "Reset Account" action (WS2025 blocks a password derived from the name) – instead we explain that the machine
  has to be joined again.

### 9.7 Preview, confirmation and report

- **Preview** in two views: "by object" (attribute, old → new value, step icons) and "by change" (e.g. "`department`:
  IT → Dział IT (120 objects)", "Move → OU=Wyłączone (37)"); filters: changed, unchanged, blockers, warnings,
  conflicts, no permissions; counters and estimated time. Blockers hold back only the affected objects – "apply the
  valid subset" with an explicit count.
- **Typed confirmation** (the number of objects or the word DELETE) for deleting, disabling, password resets, moving
  more than N objects and changes in privileged groups; the threshold is configurable (reference point: Active Roles
  limits an operation to 1500 objects by default); dry-run mode and "stop after N errors".
- **During:** progress, live states, pause/cancel, throughput and error counter, export at any time. **After:** the
  result per object with the LDAP code, Win32 code and a Polish message; "Retry only failed"; "Create rollback plan";
  export as CSV/JSON/HTML/PS1/LDIF. State colors always with an icon and text.

### 9.8 Rollback

| Change | Reversibility |
|---|---|
| Replacing / clearing an attribute | return to the old value, or clearing if the attribute did not exist before |
| Adding / removing a value, membership | the inverse operation |
| Move / rename | return to the old parent / RDN (as long as the object was not moved in the meantime) |
| Enabling / disabling, expiration | restoring the previous state |
| Creation | deletion by GUID |
| Deletion | **conditionally** – only through the AD Recycle Bin (6.13) |
| Password reset | **irreversible** – marked in red in the preview |

The rollback plan is generated from the old values stored at execution and goes through the same preview and
validation with the CAS condition: we roll back only where the current value still equals the one we wrote; otherwise
a Conflict for the operator to decide. The log lives only in the process memory; export (LDIF/JSON, optionally DPAPI)
is an explicit operator action with a warning – the ADModify pattern adapted to the "no local data" rule.

### 9.9 CSV: import and export

- **Encoding and separator:** BOM → strict UTF-8 → Windows-1250 (`CodePagesEncodingProvider`); the separator `;`, `,`
  or Tab detected automatically. Polish Excel saves CSV with a **semicolon** and often in **cp1250** – an importer that
  is "UTF-8 with a comma only" will destroy "Łukasz Żółć".
- **Headers** mapped by LDAP name, then by the English ADUC label, then by the Polish one; mapping profiles as
  configuration; a template with headers `Label [ldapName]` and validation before sending (the Entra/LAM pattern).
- **Key:** `objectGUID` (best), `sAMAccountName`, UPN, DN (fragile after a move), `employeeID` or `mail` (no
  uniqueness guarantee, `employeeID` without an index – warning and narrowing the base). We do not change the key
  attribute in the same run. Keys are resolved in batches of OR filters of 50–100 values with escaping; 0 or > 1
  matches are reported per row.
- **Cells:** empty = ignore, `<clear>` = clear (we never clear silently); multi-value separator `|`; trimming spaces;
  dates `dd.MM.yyyy` or ISO; TRUE/FALSE.
- **Export:** UTF-8 with BOM, `;` or `,`, all fields in quotes; formula neutralization (OWASP) – an apostrophe before
  cells starting with `=`, `+`, `-`, `@`, Tab, CR, LF and the full-width `＝＋－＠`. The prefix also hits numbers like
  "+48 …", so we strip it on re-import; Excel may reactivate formulas after re-saving – we warn. Results: ObjectGuid,
  DN, `sAMAccountName`, step, attribute, old, new, state, LDAP code, Win32 code, message, DC, time, number of
  attempts; also JSON and LDIF.

### 9.10 Templates, Polish transliteration and uniqueness

Syntax: `{attribute}`, `{attribute|fn|fn(arg)}`, `{csv:Column}`; functions `lower`, `upper`, `title` (pl-PL), `trim`,
`collapse`, `ascii`/`translit`, `first(n)`, `left(n)`, `right(n)`, `substr(i,n)`, `replace(a,b)`,
`regex(pattern,replacement)`, `nospace`, `nohyphen`, `default(x)`, `truncate(n)`, `date(format)`, `counter(width)`.

```text
sAMAccountName = {givenName|first(1)|translit|lower}{sn|translit|lower|nospace}
userPrincipalName = {givenName|translit|lower}.{sn|translit|lower}@firma.pl
displayName = {sn} {givenName}
homeDirectory = \\fs01\home$\{sAMAccountName}
```

| Character | ą | ć | ę | ł | ń | ó | ś | ź | ż | Ą | Ć | Ę | Ł | Ń | Ó | Ś | Ź | Ż |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Transliteration | a | c | e | l | n | o | s | z | z | A | C | E | L | N | O | S | Z | Z |

- **Ł and ł have no Unicode decomposition** – we checked that NFD with removal of combining marks turns "ąćęłńóśźż"
  into "acełnoszz". Therefore an explicit table first, then NFD as a fallback for other languages plus a table of
  letters without decomposition (đ, ø, æ, œ, ß → ss, ı). We transliterate only `sAMAccountName`, UPN and addresses;
  `displayName` and `givenName` keep the Polish characters. Configurable: double-barreled surnames (Nowak-Kowalska:
  keep, without the hyphen, first part), apostrophes, spaces; an empty token has an explicit fallback value (we avoid
  the Active Roles bug).
- **Uniqueness:** patterns in turn (jkowalski, jan.kowalski, jkowalski2…); the base is **truncated before adding the
  counter** (otherwise with the 20-character limit the counter would be truncated and collisions would return); the
  candidate is checked on the pinned DC, in the GC (UPN, `mail`/`proxyAddresses`) and among the names reserved in the
  batch. UPN uniqueness is enforced only by WS2012 R2+ DCs, and the GC has replication latency, so on error 68, 19 or
  0x21C8 during execution we generate the next variant (if the rule allows) and note the difference from the preview.

### 9.11 Exchange and mail attributes (client priority)

- **Scope:** `mail`, `mailNickname`, `proxyAddresses`, `targetAddress` (contacts), `extensionAttribute1–15` (we detect
  the presence of the Exchange schema live), `msDS-cloudExtensionAttribute1–20` (WS2012+). `legacyExchangeDN`,
  `proxyAddresses` and `mailNickname` are in the ANR set, so searching by address works right away.
- **`proxyAddresses`:** exactly one primary `SMTP:` address, the rest `smtp:`; case-insensitive comparison, so changing
  a primary into a secondary requires Delete + Add in one request; adding and removing addresses goes through
  `PermissiveModify`.
- **Uniqueness:** AD does not enforce it, but duplicates break Exchange and Entra Connect – we check every new value in
  the GC (`(|(mail=x)(proxyAddresses=smtp:x))`) and within the batch.
- **Boundaries:** `msExch*` is read-only by default; creating mailboxes and changes that need Exchange logic stay in the
  Exchange tools; we do not carry over the `adminMultiselectPropertyPages` pages from Exchange extensions. In a hybrid
  setup, sync anchors (e.g. `mS-DS-ConsistencyGuid`) have to be kept in mind – hybrid support is planned for later.
- **Scenarios:** a surname change (`sn`, `displayName`, `cn` through ModifyDN, UPN, `mail`, a new primary address in
  `proxyAddresses` keeping the old one as secondary), a mail domain migration (adding `smtp:` addresses for a whole
  OU), filling `extensionAttribute*` from an HR CSV. Attributes with `fCOPY` (e.g. some `extensionAttribute*`) are
  copied by "Copy user".

### 9.12 Wizards, scenarios and equivalent scripts

- **Inactive accounts and computers:** `lastLogonTimestamp` is updated only when it is older than
  `msDS-LogonTimeSyncInterval` (14 days minus a random value of up to 5 days), so the threshold is at least 14 days
  plus a margin (typically 60/90/180); in addition `(!(lastLogonTimestamp=*))` with an old `whenCreated`, and
  `pwdLastSet` (computers change their password about every 30 days). Exclusions: DCs
  (`(!(userAccountControl:1.2.840.113556.1.4.803:=8192))`), cluster CNOs/VCOs (SPN with `MSClusterVirtualServer`),
  `isCriticalSystemObject`, operator exceptions. Flow: preview → disable → marker in the description → move to the
  "Wyłączone" OU; deletion later with Tree Delete (computers have children, a plain delete ends with 66).
- **Other wizards:** surname change (9.11), UPN suffix migration (validation against `uPNSuffixes`, uniqueness in the
  GC, a warning about the impact on Entra Connect), copying memberships A → B. **Scenarios** (e.g. employee departure:
  disable, remove from groups, move, clear the manager, marker) saved as configuration.
- **Equivalent PowerShell:** `-Server $dc` and `-Identity '<objectGUID>'` in every command; Modify → `Set-ADObject` /
  `Set-ADUser -Replace/-Add/-Remove/-Clear`; `Move-ADObject -TargetPath`; `Rename-ADObject`;
  `Add-/Remove-ADGroupMember -Confirm:$false`; `Enable-/Disable-ADAccount`; `Unlock-ADAccount`;
  `Set-ADAccountPassword -Reset -NewPassword (Read-Host -AsSecureString)`; `New-ADUser -Instance`;
  `Remove-ADObject -Recursive`; `-WhatIf` through `$WhatIfPreference`; `try/catch` per object with the results
  recorded. The file in **UTF-8 with BOM** – without a BOM Windows PowerShell 5.1 reads the script as cp1250 and
  corrupts "Łódź".
- **LDIF** (RFC 2849, `changetype: modify`) for `ldifde -i -f plan.ldf -s <dc>`. Signed plan files (export by
  operator A, import, re-validation and execution by operator B) will later provide the "four-eyes principle" without a
  server.

---

## 10. Risks and how we mitigate them

| # | Risk | Mitigation |
|---|---|---|
| R1 | Fluent in WPF is experimental (WPF0001), has open DataGrid/ListView bugs, "touch" sizes | `IThemeService`, SDK pinned in `global.json`, compact `BasedOn` styles, ListView+GridView for the main list, a commercial grid as plan B decided in Phase 0 |
| R2 | Large containers will not reach the "faster than ADUC" goal: sorting without an index, `MaxTempTableSize`, a single sort key | test on 100k objects in Phase 0; strategy by `msDS-Approx-Immed-Subordinates`; sorting only by indexed columns; automatic fallback to paging with a banner; measurements and a Stats control |
| R3 | S.DS.P pitfalls: synchronous connect/bind in `BeginSendRequest`, no callback after `Abort`, 250 ms thread, timeout overflow, truncation to seconds | own executor (6.2) with a unit test for every pitfall; first bind through `Task.Run`; `FromDays(7)` for notifications; zero synchronous `SendRequest` on UI paths |
| R4 | Silent identity change: rebinding on a thread without impersonation runs as the process identity | `AutoBind=false`, `ReferralChasing=None`, own reconnect with WhoAmI, identity bar, a test checking the SID from WhoAmI before every write batch |
| R5 | Different domain hardening: signing, CBT=2, blocked NTLM, Protected Users, connecting by IP = NTLM, confidential attributes only encrypted | Kerberos with sign+seal by default, FQDN only, no sign/seal over TLS, a test matrix for WS2022/WS2025 and CBT 0/1/2, messages for errors 8, 13 and bind subcodes |
| R6 | Mass mistakes (mass disabling, deleting, clearing membership) | mandatory preview, typed confirmation, dry-run mode, Tier-0/`adminCount`/protection warnings, Recycle Bin awareness, a ban on Replace of `member`, automatic pause, in-memory rollback, helpdesk mode limits |
| R7 | No multi-object transactions, replication latency, DC failure – partial or "invisible" results | atomic per-object writes, one DC per batch, change detection through `uSNChanged` + CAS, the Unknown state verified by a read, pause instead of switching, re-validation after a DC change |
| R8 | Directory data leaking to disk through logs, dumps, system notifications, clipboard history, MRU, exports | the rules table (8.9), redaction, generic system notifications, clipboard exclusions, a CI test with canaries, customer decisions in the gray area, ADMX switches |
| R9 | DC load from constructed attributes and bulk writes | constructed attributes only in base reads or throttled validation; 4 concurrent writes by default and about 20/s with AIMD; merging writes to groups; telemetry with thresholds |
| R10 | Wrong permission hints: no extended rights in `allowedAttributesEffective`, `adminCount=1`, SDProp | a hint instead of a block, AuthZ for extended rights, the optimistic path always works, an AdminSDHolder marker with an explanation, tests on real delegated accounts |
| R11 | Wrong encoding of old formats: `logonHours` (UTC/DST), `accountExpires` (end of day), `c`/`co`/`countryCode`, `userParameters`, `groupType`, the "cannot change password" ACE | encoders in `AdMng.Core` with tests on values written by ADUC in the lab; `userParameters` late and only if it is used |
| R12 | Polish realities: ł outside NFD, CSV in cp1250 with a semicolon, PS 5.1 without a BOM, three plural forms, server sort order | an explicit transliteration table, encoding and separator detection, a BOM in all scripts, a plural helper, a sort order test with Polish surnames |
| R13 | Scope creep from the ADUC "long tail" (Dial-in, RDS, COM+, printers, the delegation wizard) | priorities by actual usage, the Attribute Editor as a universal fallback, a parity list agreed with the team before full editing |
| R14 | Hardened PAW workstations: AppLocker/WDAC, single-file unpacking libraries to `%TEMP%` | a signed per-machine MSI into Program Files, publisher rules on an internal certificate, no self-extraction, a test on a real PAW image |
| R15 | Notification limits: 5 per connection, lost after idle or a DC restart, local to the DC | LRU of registrations on a separate connection, detection and re-registration, fallback USN polling, a visible time of the last refresh |
| R16 | Unverified details from the research | a list to check in the lab at the start of the work (all places marked "to be verified in the lab", among others ASQ with paging, `unicodePwd` in Add, `tokenGroups` in RootDSE, Tree Delete on large subtrees, `info` vs `comment`, the ANR set, the OID of Polish sorting, the BitLocker filter by ID) |
| R17 | Dependence on libraries with a single maintainer (lepoco/wpfui) | no dependency; a few MIT controls copied with attribution into `AdMng.Controls` |
| R18 | The Samba lab differs from Windows AD (no display specifiers 415, no Exchange schema, different behavior of some controls) | integration tests on Samba in CI, and before release mandatory verification on Windows Server 2022/2025 ([ADR-0007](adr/0007-samba-lab.md)) |
| R19 | Writing mail attributes bypassing Exchange creates inconsistencies or duplicate addresses | `msExch*` read-only, uniqueness checks in the GC and in the batch, one `SMTP:` address, mailbox provisioning in the Exchange tools, questions about the Exchange topology (chapter 11) |
| R20 | AD Recycle Bin disabled in the customer's forest – deletions are practically irreversible | detecting the Recycle Bin state, stronger warnings in the delete window, no automatic enabling (enabling only as an explicit, elevated operation in a late phase) |
| R21 | Views and pins with DNs break after an OU reorganization | storing `objectGUID` next to the DN, marking targets that have disappeared, re-resolving by GUID |

---

## 11. Open questions for the team

Matters already settled are omitted: the technology (WPF/.NET 10), the UI language (PL + EN), the priority of the AD
Recycle Bin and Exchange attributes, and the need for configurable views.

**Environment**

1. Topology: how many domains, which trusts, functional levels, DC versions (2016–2025), are there RODCs, how many
   sites?
2. Scale: how many users, computers and groups; how big are the largest OUs and the largest group? (decides the
   priority of VLV and large-group handling)
3. Views: will you provide the OU tree (e.g. Pracownicy / Komputery / Serwery / Kontrahenci / Wyłączone)? Which views
   to predefine and for which roles? Share them centrally (e.g. from a network share), or is export/import enough?
4. Exchange: on-premises, hybrid or Exchange Online with Entra Connect? Is Exchange the source of truth for
   `mail`/`proxyAddresses`? What are `extensionAttribute1–15` used for? Does the schema have custom extensions?
5. AD Recycle Bin: is it already enabled, what is `msDS-deletedObjectLifetime` and who should have the restore right?
6. Is there a Windows Server lab or a copy of the production AD for testing (Samba does not reproduce everything)?

**Security**

7. The "no local data" boundary: are the following acceptable: (a) an automatic history of recently visited OUs with
   DNs, (b) an operator log with values before and after (a file, only the event log/SIEM, or metadata only), (c) CSV
   exports commissioned by the operator, (d) export of the rollback log, (e) crash dumps?
8. Does LDAPS work on all DCs? Are LDAP signing and channel binding enforced? Is NTLM restricted?
9. Is there a tier model or PAW, and where should the Tier-0 definitions be read from? Are administrators in Protected
   Users, in silos, or do they use smart-card-only accounts?
10. How do operators log on today (`adm-*` accounts with `runas`, a jump server)? Are two identities (helpdesk +
    administrator) needed at the same time in one instance?
11. What rights does helpdesk have (reset, unlock, enable, which attributes, which groups)? Is a "view only" mode
    needed?
12. May helpdesk export to CSV? Should a reset require verifying the caller, or only generated passwords?
13. Is directory change auditing (5136) running on the DCs, and is there a SIEM that can serve as the audit trail?

**Bulk editing**

14. Which bulk operations are the most frequent and on how many objects (typically and at most)?
15. Confirmation thresholds: deletion, disabling, password reset, modifications above N objects? Does anything require a
    second person (the four-eyes principle)?
16. CSV sources: an export from the HR system – format, encoding, key (`employeeID`?) and is the key really unique?
17. Naming conventions: the `sAMAccountName` pattern, UPN suffixes, the e-mail address format, Polish characters,
    double-barreled surnames, duplicates (numbers?); do the rules differ between OUs?
18. When creating accounts, should home directories or share ACLs also be created (SMB, outside LDAP)?
19. Which mail attributes do you change in bulk (aliases after a domain change, the primary address after a surname
    change, `extensionAttribute*` from HR)?

**Scope**

20. Which ADUC tabs are used: Dial-in, RDS (`userParameters`), COM+, Name Mappings, Published Certificates, printers,
    shared folders?
21. LAPS: Windows LAPS or the legacy one, with password encryption or without? Are BitLocker recovery keys in AD?
22. Should the tool cover the other ADAC areas (FGPP, authentication policies and silos, DAC)?
23. Is an "equivalent PowerShell" view needed (learning, change review, schedules)?
24. Attribute labels: Polish ones from display specifiers (as in the Polish ADUC), LDAP names, or both at once?

**Deployment**

25. Distribution (Intune, ConfigMgr, GPO)? AppLocker/WDAC? A code-signing certificate from the internal AD CS? Support
    for Windows 10 or ARM64?
26. Who develops and maintains the tool, what is the deadline for the first useful version (read + helpdesk) and is it
    to be internal only or open source?
27. Who will take part in the pilot (proposal: about 5 helpdesk people for 2 weeks in parallel with ADUC)?

---

## 12. Sources

Most Microsoft Learn pages were read from their sources in the MicrosoftDocs repositories (the same text); below we
give the addresses in the form in which the researchers cited them. Vendor data comes from search engine excerpts.

**ADUC – UI mapping, tabs, wizards**

- [User Object UI Mapping](https://learn.microsoft.com/en-us/windows/win32/ad/user-object-user-interface-mapping) · [Group Object UI Mapping](https://learn.microsoft.com/en-ie/Windows/Win32/ad/group-object-user-interface-mapping) · [Computer UI Mapping](https://msdn.microsoft.com/en-us/ms675736) · [Mappings for the ADUC snap-in](https://learn.microsoft.com/en-us/windows/win32/ad/mappings-for-the-active-directory-users-and-computers-snap-in) · [Modifying User Cannot Change Password](https://learn.microsoft.com/en-us/windows/win32/adsi/modifying-user-cannot-change-password-ldap-provider) · [ADS_GROUP_TYPE_ENUM](https://learn.microsoft.com/en-us/windows/win32/api/iads/ne-iads-ads_group_type_enum) · [ManagedBy and the ACE](https://learn.microsoft.com/en-us/archive/blogs/dsadsi/issues-while-setting-a-manageby-properties-of-a-group-object-in-active-directory)
- [KB250455 (createDialog)](https://support.microsoft.com/kb/250455/) · [Changing display names](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/change-display-names-ad-users) · [Hiding inetOrgPerson](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/hide-display-inetorgperson-object-class) · [Copy user (cc771231)](https://technet.microsoft.com/en-us/library/cc771231.aspx) · [ee198800](https://technet.microsoft.com/en-us/library/ee198800.aspx) · [KB305144 (userAccountControl)](https://support.microsoft.com/kb/305144) · [KB5014754 (certificate mappings)](https://support.microsoft.com/kb/5014754) · [AskDS – Name Mappings](https://techcommunity.microsoft.com/blog/askds/-/4451386)
- [RODC PRP (dd145483)](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2008-r2-and-2008/dd145483(v=ws.11)) · [Kerberos delegation for gMSA](https://learn.microsoft.com/en-us/windows-server/security/group-managed-service-accounts/configure-kerberos-delegation-group-managed-service-accounts) · [Pre-Windows 2000 computers](https://thehacker.recipes/ad/movement/builtins/pre-windows-2000-computers)
- Editing multiple objects: [AD Cookbook ch06s05](https://www.oreilly.com/library/view/active-directory-cookbook/0596004648/ch06s05.html) · [Windows Server 2008 ch11s06](https://oreilly.com/library/view/windows-server-2008/9780735624375/ch11s06.html) · [rebeladmin](https://www.rebeladmin.com/?p=6905)

**ADUC – containers, views, search, operations**

- [cc773295](https://technet.microsoft.com/en-us/library/cc773295(v=ws.10)) · [Binding with WKGUID](https://learn.microsoft.com/en-us/windows/desktop/ad/binding-to-well-known-objects-using-wkguid) · [ntdsapi.h (mingw-w64, GUID verification)](https://raw.githubusercontent.com/mingw-w64/mingw-w64/master/mingw-w64-headers/include/ntdsapi.h) · [no-such-object (hybrid)](https://learn.microsoft.com/ar-sa/troubleshoot/azure/entra/entra-id/ad-dmn-services/azure-ad-hybrid-sync-no-such-object-on-server) · [gg456494](https://technet.microsoft.com/en-us/library/gg456494.aspx) · [Domain-wide updates](https://learn.microsoft.com/en-in/WINDOWS-SERVER/identity/ad-ds/deploy/domain-wide-updates) · [jj635854](https://technet.microsoft.com/en-us/jj635854)
- [Filter Options (cc773054)](https://technet.microsoft.com/en-us/library/cc773054(v=ws.10)) · [Q&A 920235](https://learn.microsoft.com/en-us/answers/a/920235) · [joeware – the 2000 limit](https://blog.joeware.net/?p=2124) · [extraColumns](https://www.undocumented-features.com/?p=95) · [Modifying existing UIs](https://learn.microsoft.com/sv-se/windows/win32/ad/modifying-existing-user-interfaces) · [Export List (cc758335)](https://technet.microsoft.com/en-us/library/cc758335(v=ws.10))
- [Find (cc778937)](https://technet.microsoft.com/en-us/library/cc778937(v=ws.10)) · [Redmond – Searching AD](https://redmondmag.com/articles/2003/07/01/searching-active-directory.aspx) · [MS-ADTS ANR](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/1a9177f4-0272-4ab8-aa22-3c3eafd39e4b) · [Saved Queries (cc783268)](https://technet.microsoft.com/library/cc783268(v=ws.10)) · [cc757566](https://technet.microsoft.com/library/cc757566(v=ws.10)) · [woshub – saved queries](https://woshub.com/using-ldap-saved-queries-in-active-directory/) · [Petri – saved queries](https://petri.com/saved_queries_in_windows_2003_dsa/) · [cc775386](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2003/cc775386(v=ws.10)) · [NetIQ DRA – importing queries](https://www.netiq.com/documentation/directory-and-resource-administrator-92/drauserguide/data/t43avmuy8d8l.html)
- [Protection from deletion (cc739350)](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2003/cc739350(v=ws.10)) · [Petri](https://petri.com/secure-active-directory-objects-windows-server-2008r2/) · [AdminDroid](https://blog.admindroid.com/how-to-delete-protection-enabled-organizational-units-in-active-directory/) · [Delegate Control (cc772784)](https://technet.microsoft.com/en-us/library/cc772784(v=ws.10)) · [AdminDroid – delegation wizard](https://blog.admindroid.com/apply-least-privilege-in-active-directory-with-delegation-wizard/) · [Functional levels](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/active-directory-functional-levels) · [Raise levels](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/plan/raise-domain-forest-functional-levels) · [KB322692](https://support.microsoft.com/kb/322692)
- Pain points and gaps: [ADAC vs ADUC (jj574144)](https://technet.microsoft.com/library/jj574144.aspx) · [Petri – ADAC](https://petri.com/active-directory-administrative-center/) · [Q&A 476055](https://learn.microsoft.com/en-us/answers/questions/476055/aduc-disabled-users-query) · [One Identity – large OUs](https://support.oneidentity.com/kb/4333860/what-are-the-known-limitations-when-dealing-with-organizational-units-or-groups-with-many-thousands-of-objects-) · [Find bug without advanced tabs](https://learn.microsoft.com/en-us/archive/blogs/389thoughts/workaround-for-the-aduc-search-bug-with-advanced-tabs-missing) · [RSAT on ARM64](https://www.elevenforum.com/t/rsat-capabilities-arrive-on-arm-based-windows-11-pcs.45196/latest) · [windows-active-directory.com](https://www.windows-active-directory.com/active-directory-users-and-computers.html)

**ADAC, AD Recycle Bin, FGPP, WAC, Entra, PowerShell**

- ADAC: [dd560631](https://technet.microsoft.com/en-us/library/dd560631) · [Navigation pane](https://learn.microsoft.com/windows-server/security/group-managed-service-accounts/customize-the-active-directory-administrative-center-navigation-pane) · [ITPro Today](https://itprotoday.com/windows-8/using-active-directory-administrative-center-in-windows-server-2008-r2) · [Global Search (dd861328)](https://technet.microsoft.com/en-us/library/dd861328.aspx) · [Petri – Global Search](https://petri.com/use-global-search-in-active-directory-administrative-center-adac/) · [Q&A 562406](https://learn.microsoft.com/en-us/answers/questions/562406/why-we-are-not-able-to-see-attibute-editor-in-user) · [Level 200](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/get-started/adac/advanced-ad-ds-management-using-active-directory-administrative-center--level-200-) ([source](https://github.com/MicrosoftDocs/windowsserverdocs/blob/main/WindowsServerDocs/identity/ad-ds/get-started/adac/Advanced-AD-DS-Management-Using-Active-Directory-Administrative-Center--Level-200-.md)) · [ADWS (ff755968)](https://technet.microsoft.com/en-us/library/ff755968(v=technet.10)) · [Large groups](https://activedirectorypro.com/get-members-of-large-ad-groups) · [PowerShell History](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/get-started/adac/use-active-directory-administrative-center-powershell-history) · [TechTarget](https://www.techtarget.com/searchWindowsServer/tip/Improve-automation-skills-with-the-AD-Administrative-Center)
- AD Recycle Bin and FGPP: [AD Recycle Bin](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/get-started/adac/active-directory-recycle-bin) · [MS-ADTS undelete](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/4302a2eb-55c8-426c-b310-6791c6ae8307) · [Retrieving deleted objects](https://learn.microsoft.com/en-us/windows/win32/ad/retrieving-deleted-objects) · [FGPP](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/get-started/adac/fine-grained-password-policies) · [msDS-UserPasswordExpiryTimeComputed](https://www.manageengine.com/products/ad-manager/ad-schema-attributes/ms-ds-user-password-expiry-time-computed.html) · [DAC](https://learn.microsoft.com/en-us/windows-server/identity/solution-guides/deploy-a-central-access-policy--demonstration-steps-) · [Authentication policies and silos](https://learn.microsoft.com/en-us/windows-server/security/credentials-protection-and-management/authentication-policies-and-authentication-policy-silos)
- WAC: [Manage servers](https://learn.microsoft.com/en-US/windows-server/manage/windows-admin-center/use/manage-servers) · [Overview](https://learn.microsoft.com/windows-server/manage/windows-admin-center/overview) · [Preview 1903](https://blogs.windows.com/windows-insider/2019/03/26/announcing-windows-admin-center-preview-1903/) · [Neowin](https://www.neowin.net/amp/microsoft-releases-windows-admin-center-preview-1903---heres-whats-new/)
- Entra: [Users search](https://learn.microsoft.com/en-us/entra/identity/users/users-search-enhanced) · [Group members](https://learn.microsoft.com/en-us/entra/identity/users/groups-members-owners-search) · [Devices](https://learn.microsoft.com/en-us/entra/identity/devices/manage-device-identities) · [Bulk add](https://learn.microsoft.com/en-us/entra/identity/users/users-bulk-add) · [Bulk delete](https://learn.microsoft.com/en-us/entra/identity/users/users-bulk-delete) · [Bulk download](https://learn.microsoft.com/en-us/entra/identity/users/users-bulk-download) · [Bulk operations](https://learn.microsoft.com/en-us/entra/fundamentals/bulk-operations) · [Limitations](https://learn.microsoft.com/en-us/entra/fundamentals/bulk-operations-service-limitations)
- PowerShell and change history: [Get-ADUser](https://learn.microsoft.com/en-us/powershell/module/activedirectory/get-aduser) · [Set-ADUser](https://learn.microsoft.com/en-us/powershell/module/activedirectory/set-aduser) · [New-ADUser](https://learn.microsoft.com/en-us/powershell/module/activedirectory/new-aduser) · [Move-ADObject](https://learn.microsoft.com/en-us/powershell/module/activedirectory/move-adobject) · [Search-ADAccount](https://learn.microsoft.com/en-us/powershell/module/activedirectory/search-adaccount) ([source](https://github.com/MicrosoftDocs/windows-powershell-docs/blob/main/docset/winserver2025-ps/ActiveDirectory/Search-ADAccount.md)) · [MS-ADTS lockout](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/b80798ae-1f8c-4d30-9d80-a1f3281e96e2) · [ANR (TechNet Wiki)](https://learn.microsoft.com/en-us/archive/technet-wiki/22653.active-directory-ambiguous-name-resolution) · [cc220851](https://msdn.microsoft.com/en-us/library/cc220851) · [Get-ADReplicationAttributeMetadata](https://technet.microsoft.com/en-us/library/hh852209(v=wps.620)) · [SpecterOps – replication metadata](https://specterops.io/blog/2017/09/06/hunting-with-active-directory-replication-metadata/) · [DS_REPL_ATTR_META_DATA](https://learn.microsoft.com/en-us/windows/win32/api/ntdsapi/ns-ntdsapi-ds_repl_attr_meta_data) · [about_Character_Encoding (5.1)](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_character_encoding?view=powershell-5.1)

**Competing products and open source**

- ManageEngine: [sync frequency](https://manageengine.com/products/ad-manager/admanager-kb/how-often-admanager-plus-sync-with-active-directory.html) · [data synchronization](https://www.manageengine.com/products/ad-manager/admanager-kb/active-directory-and-admanager-plus-data-synchronization.html) · [PostgreSQL](https://www.manageengine.com/products/ad-manager/admanager-kb/how-to-update-admanager-plus-built-in-postgresql-database.html) · [CSV modify](https://manageengine.com/products/ad-manager/help/user_management/active-directory-modify-users-csv.html) · [CSV errors](https://www.manageengine.com/au/products/ad-manager/admanager-kb/csv-bulk-modification-failure-reasons.html) · [manager from CSV](https://www.manageengine.com/products/ad-manager/admanager-kb/fix-csv-manager-update-failure.html)
- Adaxes: [Installation guide](https://www.adaxes.com/help/InstallationGuide) · [Technical overview](https://passwords.devfactory.com/AdaxesSelfService/Help/GettingStartedWithAdaxes.TechnicalOverview.html) · [Full name template](https://adaxes.com/help/ChangeTemplateForAutoGeneratingUsersFullName/) · [UPN](https://www.adaxes.com/questions/726/userprincipalname-attribute?show=4872) · Active Roles: [Admin guide 7.6 – 26](https://support.oneidentity.com/technical-documents/active-roles/7.6/administration-guide/26) · [7.6.3 – 5](https://support.oneidentity.com/technical-documents/active-roles/7.6.3/administration-guide/5) · [doc2114472](https://support.oneidentity.com/technical-documents/doc2114472) · [KB PGV](https://support.oneidentity.com/kb/4334147/text-entry-in-a-property-generation-and-validation-policy-following-a-missing-attribute-is-removed)
- Softerra LDAP Administrator: [2025 ch08s08](https://www.ldapadministrator.com/resources/english/help/la2025/ch08s08.html) · [2011.1](https://www.ldapadministrator.com/resources/english/2011.1/whatsnew.html) · [2012.1](https://ldapadministrator.com/resources/english/2012.1/whatsnew.html) · Hyena: [attribute management](https://www.systemtools.com/HyenaHelp/about_attribute_management.htm) · [attributemgt](https://systemtools.com/HyenaHelp/attributemgt.htm) · [Active Editor](https://www.systemtools.com/HyenaHelp/active_editor.htm) · ADModify: [dwinter](https://learn.microsoft.com/en-us/archive/blogs/dwinter/admodify-net-released) · [Exchange blog](https://techcommunity.microsoft.com/blog/exchange/admodify-net-is-here/610119) · [computerperformance](https://www.computerperformance.co.uk/utilities/admodify/)
- Other: [Cjwdev](https://cjwdev.com/Software.html) · [AD Tidy](http://www.cjwdev.co.uk/Software/ADTidy/Info.html) · [albusbit](https://albusbit.com/blog/cjwdev-alternatives/) · [AD Explorer](https://learn.microsoft.com/en-us/sysinternals/downloads/adexplorer) · [Sysinternals 2020](https://www.neowin.net/news/sysinternals-suite-20201104/) · [Snapshots](https://documentation.help/AD-Explorer/Snapshots.htm) · [ADExplorerSnapshot](https://github.com/c3c/ADExplorerSnapshot) · [Netwrix](https://www.netwrix.com/active_directory_change_control.html) · [Netwrix restore](https://docs.netwrix.com/docs/auditor/10_7/tools/objectrestoread) · [Lepide](https://lepide.com/lepideauditor/active-directory-auditing.html) · [Specops datasheet](https://specopssoft.com/our-resources/secure-service-desk-datasheet/) · [Specops admin](https://specopssoft.com/support/secure-service-desk/administration/authentication-web/)
- Open source: [UnifiedDirectoryManager](https://github.com/Armadillon44/UnifiedDirectoryManager) · [OpenADUC](https://github.com/OpenADUC/openaduc) · [ADMC](https://github.com/altlinux/admc) · [BLAZAM](https://github.com/Blazam-App/BLAZAM) · [LDAP Admin (Wikipedia)](https://en.wikipedia.org/wiki/LDAP_Admin) · [LDAP Admin (SourceForge)](https://www.sourceforge.net/projects/ldapadmin/) · [Apache Directory Studio](https://directory.apache.org/studio/)

**LDAP and System.DirectoryServices.Protocols**

- .NET code (`release/10.0`): [ref API](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/ref/System.DirectoryServices.Protocols.cs) · [LdapConnection.cs (Abort)](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/ldap/LdapConnection.cs) · [LdapConnection.Windows.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/ldap/LdapConnection.Windows.cs) · [DirectoryConnection.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/common/DirectoryConnection.cs) · [LdapPartialResultsProcessor.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/ldap/LdapPartialResultsProcessor.cs) · [LdapAsyncResult.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/ldap/LdapAsyncResult.cs) · [LdapSessionOptions.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/ldap/LdapSessionOptions.cs) · [LdapSessionOptions.Windows.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/ldap/LdapSessionOptions.Windows.cs) · [DirectoryControl.cs](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.DirectoryServices.Protocols/src/System/DirectoryServices/Protocols/common/DirectoryControl.cs) · [ref API (main)](https://github.com/dotnet/runtime/blob/main/src/libraries/System.DirectoryServices.Protocols/ref/System.DirectoryServices.Protocols.cs) · [NetworkCredential.cs](https://raw.githubusercontent.com/dotnet/runtime/main/src/libraries/System.Net.Primitives/src/System/Net/NetworkCredential.cs) · [NuGet](https://www.nuget.org/packages/system.directoryservices.protocols) · [BeginSendRequest](https://learn.microsoft.com/dotnet/api/system.directoryservices.protocols.ldapconnection.beginsendrequest)
- Policies and limits: [ntdsutil – LDAP policy](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/view-set-ldap-policy-using-ntdsutil) · [MS-ADTS – LDAP policies](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-adts/3f0137a1-63df-400c-bf97-e1040f055a99) · [500 values in a response](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/domain-controller-returns-500-values-ldap-response) · [ldap_search_ext](https://learn.microsoft.com/en-us/windows/win32/api/winldap/nf-winldap-ldap_search_ext) · [LDAP considerations](https://learn.microsoft.com/en-us/windows-server/administration/performance-tuning/role/active-directory-server/ldap-considerations) · [Referrals](https://learn.microsoft.com/en-us/windows/win32/ad/referrals)
- Paging, VLV, ranges, ASQ, filters: [Sorting](https://learn.microsoft.com/en-us/windows/win32/adsi/sorting) · [Search using VLV](https://learn.microsoft.com/en-us/windows/win32/adsi/how-to-search-using-vlv) · [LDAPSortKey](https://learn.microsoft.com/en-us/windows/win32/api/winldap/ns-winldap-ldapsortkeya) · [draft VLV-09](https://datatracker.ietf.org/doc/html/draft-ietf-ldapext-ldapv3-vlv-09) · [RFC 2891](https://www.rfc-editor.org/rfc/rfc2891) · [Range retrieval](https://learn.microsoft.com/en-us/windows/win32/adsi/attribute-range-retrieval) · [ASQ](https://learn.microsoft.com/en-us/windows/win32/adsi/performing-an-attribute-scoped-query) · [ASQ OID](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ldap/ldap-server-asq-oid) · [Search filter syntax](https://learn.microsoft.com/en-us/windows/win32/adsi/search-filter-syntax) · [RFC 4515](https://www.rfc-editor.org/rfc/rfc4515) · [searchFlags](https://learn.microsoft.com/en-us/windows/win32/adschema/a-searchflags)
- Constructed attributes, notifications, RootDSE, locator: [allowedAttributesEffective](https://learn.microsoft.com/en-us/windows/win32/adschema/a-allowedattributeseffective) · [allowedChildClassesEffective](https://learn.microsoft.com/en-us/windows/win32/adschema/a-allowedchildclasseseffective) · [sDRightsEffective](https://learn.microsoft.com/en-us/windows/win32/adschema/a-sdrightseffective) · [msDS-User-Account-Control-Computed](https://learn.microsoft.com/en-us/windows/win32/adschema/a-msds-user-account-control-computed) · [dirteam – can I update](https://dirteam.com/tomek/2006/12/13/how-to-check-if-i-can-update-attribute_3f00_/) · [Change notifications](https://learn.microsoft.com/en-us/windows/win32/ad/change-notifications-in-active-directory-domain-services) · [Notification OID](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ldap/ldap-server-notification-oid) · [DirSync](https://learn.microsoft.com/en-us/windows/win32/ad/polling-for-changes-using-the-dirsync-control) · [uSNChanged polling](https://learn.microsoft.com/en-us/windows/win32/ad/polling-for-changes-using-usnchanged) · [RootDSE](https://learn.microsoft.com/en-us/windows/win32/adschema/rootdse) · [Serverless binding](https://learn.microsoft.com/en-us/windows/win32/ad/serverless-binding-and-rootdse) · [Display specifiers](https://learn.microsoft.com/windows/win32/ad/display-specifiers) · [DsGetDcName](https://learn.microsoft.com/en-us/windows/win32/api/dsgetdc/nf-dsgetdc-dsgetdcnamew) · [LDAP client performance counters](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/ldap-client-performance-counters)
- Accounts, passwords, groups, errors: [Changing a password through LDAP](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/change-windows-active-directory-user-password) · [unicodePwd](https://learn.microsoft.com/en-us/windows/win32/adschema/a-unicodepwd) · [Setting user passwords](https://learn.microsoft.com/en-us/windows/win32/adsi/setting-user-passwords-for-ldap-providers) · [userAccountControl](https://learn.microsoft.com/en-us/windows/win32/adschema/a-useraccountcontrol) · [pwdLastSet](https://learn.microsoft.com/en-us/windows/win32/adschema/a-pwdlastset) · [User must change password](https://learn.microsoft.com/en-us/windows/win32/adsi/user-must-change-password-at-next-logon) · [accountExpires](https://learn.microsoft.com/en-us/windows/win32/adschema/a-accountexpires) · [groupType](https://learn.microsoft.com/en-us/windows/win32/adschema/a-grouptype) · [primaryGroupID](https://learn.microsoft.com/en-us/windows/win32/adschema/a-primarygroupid) · [adminCount](https://learn.microsoft.com/en-us/windows/win32/adschema/a-admincount) · [Creating a user](https://learn.microsoft.com/en-us/windows/win32/ad/creating-a-user) · [Moving objects](https://learn.microsoft.com/en-us/windows/win32/ad/moving-objects) · [Naming properties](https://learn.microsoft.com/en-us/windows/win32/ad/naming-properties) · [description](https://learn.microsoft.com/en-us/windows/win32/adschema/a-description) · [sAMAccountName](https://learn.microsoft.com/en-us/windows/win32/adschema/a-samaccountname) · [SPN/UPN uniqueness](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/component-updates/spn-and-upn-uniqueness) · [lastLogonTimestamp](https://learn.microsoft.com/en-us/windows/win32/adschema/a-lastlogontimestamp) · [lastLogonTimestamp in ConfigMgr](https://learn.microsoft.com/en-us/troubleshoot/mem/configmgr/discovery/lastlogontimestamp-not-accurate) · [Dell – LDAP codes](https://www.dell.com/support/kbdoc/en-ph/000165267/networker-ad-ldap-integration-failing-with-common-ldap-error-codes-ldap-error-code-49) · [Lenovo – bind errors](https://helpdesk.lenovosoftware.com/portal/en/kb/articles/common-microsoft-ad-ldap-bind-errors-22-8-2017) · [RFC 4511 §4.6](https://www.rfc-editor.org/rfc/rfc4511#section-4.6) · [AD 2008 R2 schema (Samba)](https://github.com/samba-team/samba/blob/master/source4/setup/ad-schema/MS-AD_Schema_2K8_R2_Attributes.txt) · [ldb.h (Samba)](https://github.com/samba-team/samba/blob/master/lib/ldb/include/ldb.h)

**Security**

- WS2025 and LDAP: [What's new in WS2025](https://learn.microsoft.com/en-us/windows-server/get-started/whats-new-windows-server-2025) · [LDAP signing](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/ldap-signing) · [LDAP channel binding](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/ldap-channel-binding) · [Enable LDAP signing](https://learn.microsoft.com/en-us/troubleshoot/windows-server/active-directory/enable-ldap-signing-in-windows-server) · [LDAPS with a third-party CA](https://raw.githubusercontent.com/MicrosoftDocs/SupportArticles-docs/main/support/windows-server/active-directory/enable-ldap-over-ssl-3rd-certification-authority.md) · [VERIFYSERVERCERT](https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/winldap/nc-winldap-verifyservercert.md) · [ldap_bind_s](https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/winldap/nf-winldap-ldap_bind_s.md)
- Credentials and the tier model: [LogonUserW](https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/winbase/nf-winbase-logonuserw.md) · [CreateProcessWithLogonW](https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/winbase/nf-winbase-createprocesswithlogonw.md) · [WindowsIdentity](https://raw.githubusercontent.com/dotnet/dotnet-api-docs/main/xml/System.Security.Principal/WindowsIdentity.xml) · [DE0001 SecureString](https://raw.githubusercontent.com/dotnet/platform-compat/master/docs/DE0001.md) · [CREDENTIALW](https://raw.githubusercontent.com/MicrosoftDocs/sdk-api/docs/sdk-api-src/content/wincred/ns-wincred-credentialw.md) · [Tier model](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/identity/ad-ds/tier-model.md) · [AD Tier Model – best practices](https://raw.githubusercontent.com/microsoft/ActiveDirectoryTierModel/main/docs/best-practices.md) · [Protected Users](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/security/credentials-protection-and-management/protected-users-security-group.md) · [Protected Accounts](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/identity/ad-ds/manage/How-to-Configure-Protected-Accounts.md) · [Authentication silos (source)](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/security/credentials-protection-and-management/authentication-policies-and-authentication-policy-silos.md)
- Rights and delegation: [Control access rights](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/AD/control-access-rights.md) · [User-Force-Change-Password](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/ADSchema/r-user-force-change-password.md) · [User-Change-Password](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/ADSchema/r-user-change-password.md) · [Checking a control access right](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/AD/checking-a-control-access-right-in-an-objectampaposs-acl.md) · [nTSecurityDescriptor](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/ADSchema/a-ntsecuritydescriptor.md) · [Delegation wizard](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/identity/ad-ds/manage/delegation-control-wizard.md) · [Public-Information](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/ADSchema/r-public-information.md) · [lockoutTime](https://raw.githubusercontent.com/MicrosoftDocs/win32/docs/desktop-src/ADSchema/a-lockouttime.md) · [Protected Accounts and Groups](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/identity/ad-ds/plan/security-best-practices/Appendix-C--Protected-Accounts-and-Groups-in-Active-Directory.md)
- Auditing and logs: [Audit policy recommendations](https://raw.githubusercontent.com/MicrosoftDocs/windowsserverdocs/main/WindowsServerDocs/identity/ad-ds/plan/security-best-practices/Audit-Policy-Recommendations.md) · [MDI – event collection](https://raw.githubusercontent.com/MicrosoftDocs/ATADocs/master/ATPDocs/deploy/configure-windows-event-collection.md) · [Tier Model – logging](https://raw.githubusercontent.com/microsoft/ActiveDirectoryTierModel/main/docs/tiermodel-logging.md) · [Tier Model – event ID schema](https://raw.githubusercontent.com/microsoft/ActiveDirectoryTierModel/main/docs/event-id-schema.md) · [OWASP CSV Injection](https://owasp.org/www-community/attacks/CSV_Injection) · [LoggerMessage](https://learn.microsoft.com/dotnet/core/extensions/logger-message-generator) · [Data redaction](https://learn.microsoft.com/dotnet/core/extensions/data-redaction)

**UI and distribution**

- WPF/Fluent and alternatives: [What's new WPF .NET 10](https://raw.githubusercontent.com/dotnet/docs-desktop/main/dotnet-desktop-guide/wpf/whats-new/net100.md) · [.NET 9](https://learn.microsoft.com/dotnet/desktop/wpf/whats-new/net90) · [ThemeMode.cs](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/ThemeMode.cs) · [#10393](https://github.com/dotnet/wpf/issues/10393) · [#10445](https://github.com/dotnet/wpf/issues/10445) · [#10911](https://github.com/dotnet/wpf/issues/10911) · [WPF UI – releases](https://github.com/lepoco/wpfui/releases) · [WPF UI – NuGet](https://www.nuget.org/packages/WPF-UI) · [WPF UI – controls](https://github.com/lepoco/wpfui/tree/main/src/Wpf.Ui/Controls) · [WinUI – releases](https://github.com/microsoft/microsoft-ui-xaml/releases) · [Windows App SDK 2.0](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0) · [Release channels](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-channels) · [Avalonia – discussion 18388](https://github.com/AvaloniaUI/Avalonia/discussions/18388) · [Avalonia – blog](https://avaloniaui.net/blog/building-a-sustainable-future-for-avalonia) · [TreeDataGrid](https://avaloniaui.net/tree-data-grid)
- Performance, accessibility, localization, MVVM: [TreeView performance](https://raw.githubusercontent.com/dotnet/docs-desktop/main/dotnet-desktop-guide/wpf/controls/how-to-improve-the-performance-of-a-treeview.md) · [ListBox scrolling](https://raw.githubusercontent.com/dotnet/docs-desktop/main/dotnet-desktop-guide/wpf/controls/how-to-improve-the-scrolling-performance-of-a-listbox.md) · [Optimizing controls](https://learn.microsoft.com/dotnet/desktop/wpf/advanced/optimizing-performance-controls) · [DataGrid](https://raw.githubusercontent.com/dotnet/docs-desktop/main/dotnet-desktop-guide/wpf/controls/datagrid.md) · [DataGrid – keyboard](https://learn.microsoft.com/dotnet/desktop/wpf/controls/default-keyboard-and-mouse-behavior-in-the-datagrid-control) · [TreeView with indeterminate depth](https://learn.microsoft.com/dotnet/desktop/wpf/controls/how-to-bind-a-treeview-to-data-that-has-an-indeterminable-depth) · [EnableCollectionSynchronization](https://learn.microsoft.com/dotnet/api/system.windows.data.bindingoperations.enablecollectionsynchronization) · [CustomSort](https://learn.microsoft.com/dotnet/api/system.windows.data.listcollectionview.customsort) · [PerMonitorDPI](https://github.com/microsoft/WPF-Samples/tree/main/PerMonitorDPI) · [UI Automation](https://learn.microsoft.com/dotnet/framework/ui-automation/ui-automation-overview) · [LiveSetting](https://learn.microsoft.com/dotnet/api/system.windows.automation.automationproperties.livesetting) · [WPF globalization](https://learn.microsoft.com/dotnet/desktop/wpf/advanced/wpf-globalization-and-localization-overview) · [CommunityToolkit](https://github.com/CommunityToolkit/dotnet/releases) · [ObservableProperty](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/generators/observableproperty) · [Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host)
- Distribution: [.NET releases](https://github.com/dotnet/core/blob/main/releases.md) · [Single-file](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview) ([source](https://raw.githubusercontent.com/dotnet/docs/main/docs/core/deploying/single-file/overview.md)) · [MSIX](https://learn.microsoft.com/windows/msix/overview) · [SignTool](https://learn.microsoft.com/windows/win32/seccrypto/signtool) · [Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/overview) · [LAPS – technical reference](https://learn.microsoft.com/en-us/windows-server/identity/laps/laps-technical-reference)
