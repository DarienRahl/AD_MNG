# U4 – Privileged access ("Uprawnienia") and saved filters – design note

Roadmap item U4 (README, "Ścieżka U"). A second tab of the *Przegląd kont* window lists every account that has **any**
right beyond a plain domain user – "even minimally more than others" – with a level per finding and the path that explains
where it comes from (konto → grupa A → grupa B → Account Operators). Four sources were chosen with the user; **U4a**
implements sources 1 and 2, the peer comparison and the shared model, **U4b** adds ACL delegations (as built: section
6.1) and **U4c** local rights from GPOs (as built: section 6.2); section 6.3 describes the two sources together. All of U4
(U4a–U4c) is built and waits for a pilot on Windows Server AD. The same work brings the filter builder with saved
filters, used by the review, the object lists and the privilege rules.

Rules of CLAUDE.md that shape the design: results only in memory (1), every filter through `LdapFilter` (3), all reads on
the window's DC (4), every action through the change plan (5), streaming and cancellable (6).

## 1. Pieces

| Piece | Project | Purpose |
|---|---|---|
| `PrivilegedGroupCatalog`, `PrivilegeLevel` | Core (`PrivilegedAccess/`) | The privileged groups from Tier 0 to minor, by SID/RID; shared with U2 (protected groups) and Watchdog (forest-root RIDs; a test keeps the Watchdog's default tiers consistent with it). |
| `PrivilegeFinding`, `PrivilegeSource`, `PrivilegeFindingKind`, `PrivilegeScopeKind`, `PrivilegePathStep` | Core | One right of an account: source, kind, level, path, scope, values, note, peer details. |
| `PrivilegeGrant`, `IPrivilegeGrantSource` | Core | A right granted to a principal (group or account by SID/DN); sources return grants, the evaluator expands them to accounts. U4b/U4c only add sources. |
| `GroupGraph`, `MembershipPaths` | Core | Security groups and their memberOf links in memory; nested membership by breadth-first search with the shortest path. |
| `PrivilegeEvaluator`, `AccountPrivileges` | Core | Evaluates one account: grants through membership, grants on the account, filter rules, risky settings (source 2). |
| `PeerComparison`, `PeerOptions` | Core | Groups none or few of an account's peers hold. |
| `PrivilegedAccessScanner` | Core | The queries of a run (section 7), streamed. |
| `FilterDefinition`, `FilterCompiler`, `FilterEvaluator` | Core (`Filters/`) | The filter builder's model, compilation through `LdapFilter`, server/client split, client evaluation. |
| `PrivilegedAccessViewModel` (+ rows, formatter) | Presentation (`AccountReview/`) | The tab: run, tiles, filters, details, actions, settings. |
| `SavedFilter`, `SavedFilterPlan`, `FilterLibrary`, `FilterEditorViewModel`, `SavedFiltersViewModel` | Presentation (`Filters/`) | Saved filters, their combined plan, the library (JSON export/import), the builder and the list window. |
| `PrivilegedAccessSettings` | Presentation (`Settings/`) | Level overrides, marks, rules, peers, account types, the ACL deep scan (U4b). |
| `DirectoryAclGrantSource`, `AclObjectTypes`, `AclRight` / `AclRights` | Core (`PrivilegedAccess/`) | U4b: the ACL delegations source, the meaning of object-type GUIDs (schema, CN=Extended-Rights), the reported rights and their value format. |
| `SecurityDescriptorSummary`, `SecurityDescriptorAce`, `Sddl`, `DirectoryAccessMask` | Core (`Security/`) | Binary descriptor parser (moved from AdMng.Watchdog.Core, which keeps using it), the SDDL reader of `defaultSecurityDescriptor`, access mask bits and the generic mapping. |
| `AccountReviewWindow` (tab), `FilterEditorWindow`, `SavedFiltersWindow`, chips in `ObjectListView` | App | XAML. |
| `GroupPolicyGrantSource`, `GroupPolicyRightsResolver`, `LocalRightsCatalog`, `GroupPolicyGrantDetails` | Core (`PrivilegedAccess/`) | U4c: local rights from GPOs. |
| `GroupPolicyTopology`, `GroupPolicyInheritance`, `GroupPolicyLinks`, `GroupPolicySecurityFilter`, `SecurityTemplate`, `GppGroupsParser`, `GpoText`, `GpoXml`, `ISysvolFileReader`, `SysvolPaths` | Core (`GroupPolicy/`) | U4c: GPOs, links, security filtering, computers, inheritance, the GPO file parsers (shared with Watchdog) and the SYSVOL reader contract. |
| `WindowsSysvolFileReader` | Directory | U4c: SYSVOL of the session's DC on Windows (process identity or the session's in-memory credential). |

## 2. Model

**Levels** (`PrivilegeLevel`): Tier 0 (control of the domain/forest) > wysoki/High (many objects or a path to Tier 0) >
podwyższony/Elevated (servers, risky setting) > drobny/Minor (a small extra right, an unusual group). An account's level is
the highest of its findings; the tab shows the number of accounts per level.

**Sources** (`PrivilegeSource`): `Groups` (catalogue groups), `OperatorRules` (marked groups/accounts and filter rules),
`AccountSettings` (source 2), `Peers`, `DirectoryAcl` (U4b), `GroupPolicy` (U4c). The UI offers the sources U4a evaluates
plus those of registered grant sources.

**Finding** (`PrivilegeFinding`): source, kind, level, scope (`Forest`, `Domain`, `DomainControllers`, `Account`,
`Container` + DN, `Computers` + DN, `Peers`), subject (the group or the account), catalogue id, the path, values (SPNs,
delegation targets, SIDs), the operator's note, peer details. `DirectGroupDn` is the group the account itself is a member
of on the path – the one "Usuń z grupy…" removes; a path through the primary group or an implicit group has none.

**One row per account** (`AccountPrivileges`) with all its findings; the details pane lists them by level.

**Grants** (`PrivilegeGrant`): a right on a principal, by SID (preferred) or DN, with kind, level, scope and values. The
evaluator indexes grants on groups of the graph and grants on accounts; an account gets every grant on itself and on
every group it reaches, each with the shortest path. Catalogue groups and marks are grants already (U4a sources
`BuiltInGroupGrantSource`, `MarkedPrincipalGrantSource`); ACL ACEs (U4b) and GPO memberships/user rights (U4c) become
grants of their trustees.

## 3. Source 1 – groups and "power users"

Recognised **by SID or RID only** (Polish, German… domains localize the names): built-in `S-1-5-32-x`, domain SID + RID,
forest-root RIDs (518, 519, 527, 498) in the root domain – or in any domain while the root domain SID is unknown. Only
groups without a well-known RID are matched by their (never localized) sAMAccountName: DnsAdmins, DnsUpdateProxy and the
Exchange groups.

| Level | Groups (default, the operator may change every one in the settings) |
|---|---|
| Tier 0 | Administrators, Domain Admins, Enterprise Admins, Schema Admins, Domain Controllers, Account/Server/Print/Backup Operators, Key Admins, Enterprise Key Admins, DnsAdmins, Exchange Windows Permissions, Exchange Trusted Subsystem |
| High | Group Policy Creator Owners, Cert Publishers, (Enterprise) Read-only Domain Controllers, Cloneable Domain Controllers, Organization Management |
| Elevated | Replicator, Remote Desktop Users, Remote Management Users, Hyper-V Administrators, Storage Replica Administrators, Incoming Forest Trust Builders |
| Minor | Network Configuration Operators, Performance Monitor/Log Users, Windows Authorization Access Group, Terminal Server License Servers, Distributed COM Users, Cryptographic Operators, Event Log Readers, Certificate Service DCOM Access, RDS Remote Access/Endpoint/Management Servers, Access Control Assistance Operators, DnsUpdateProxy |

AdminSDHolder-protected groups are flagged (`IsProtected`) for the adminCount check.

**Membership** (`GroupGraph`): one paged search of the security groups (`groupType` bit `0x80000000`) and of the foreign
security principals that are members of something; distribution groups are left out (their members never get the SIDs
of the security groups they are nested in). Per account a breadth-first search starts at its memberOf groups, its primary
group (domain SID + `primaryGroupID`) and the implicit principals Everyone and Authenticated Users (when they are nested
into a group, every account has it – a classic misconfiguration); visited nodes stop cycles. memberOf with more than 1500
values is completed by ranged retrieval (the only per-account read, and only then). Primary groups that are themselves
nested are followed too (Domain Users in a privileged group gives every user the finding).

**Operator's groups and accounts**: marks in the settings – SID and portable DN (`{domain}`), level, note, nothing else.
A marked group's members get `MarkedGroup` findings, a marked account `MarkedAccount`.

**Filter rules**: a saved filter used as "mark as privileged with level X" for accounts (`ConditionAccount`) or for groups
(`ConditionGroup`, the members get it through the graph). The server part of the filter runs as one DN-only search per
rule; conditions on constructed attributes are checked on the client (accounts: on the main stream, with the rule's
attributes added to the account query; groups: on the graph's nodes).

## 4. Source 2 – risky settings of the account

| Kind | Condition | Level | Scope |
|---|---|---|---|
| WellKnownAccount | RID 500 (Administrator), 502 (krbtgt) of the domain | Tier 0 | domain |
| AdminCount | `adminCount = 1` and a protected group reached (or a well-known account) | Minor | account |
| OrphanedAdminCount | `adminCount = 1` without a protected group: inheritance stays off; "Wyczyść adminCount…" | Minor | account |
| Kerberoastable | user (not computer/gMSA) with `servicePrincipalName` | Elevated | account |
| UnconstrainedDelegation | `TRUSTED_FOR_DELEGATION` on an account that is not a DC | Tier 0 | domain |
| ConstrainedDelegation | `msDS-AllowedToDelegateTo` | High; Tier 0 when a target host is a DC | domain |
| ProtocolTransition | the same with `TRUSTED_TO_AUTH_FOR_DELEGATION` | High; Tier 0 towards a DC | domain |
| ResourceBasedDelegation | `msDS-AllowedToActOnBehalfOfOtherIdentity` set on the account | Elevated; Tier 0 on a DC | account |
| SidHistory | `sIDHistory` has values | Elevated; High for SIDs of this domain; the catalogue/graph level of a privileged SID | domain |
| PreauthNotRequired | `DONT_REQUIRE_PREAUTH` | Elevated | account |

DC names for the delegation targets come from one small search (SERVER_TRUST_ACCOUNT or PARTIAL_SECRETS_ACCOUNT). Owners of
privileged objects need security descriptors and belong to U4b, as do the principals listed in an RBCD descriptor (they
are the ones that gain the right).

## 5. Peers ("odstępstwa od podobnych kont")

Peers are the users of the run's scope with the same parent OU, department or title (each switchable). A **direct** group
of an account (memberOf or primary group) is unusual when fewer than *N %* of at least *M* peers (the account itself not
counted) hold it – defaults 20 % of at least 5. Direct groups only: nested ones would repeat every unusual group along its
parents. One finding per dimension; a dimension that names only groups another dimension already reported is skipped
(department = OU). Level: Elevated when an unusual group grants Elevated or more (catalogue, marks, rules, nested),
otherwise Minor – this also catches ordinary groups held unusually ("ma 2 grupy, których nie ma nikt inny w Księgowości").
The comparison runs after the stream over all accounts of the scope (also those without findings), in memory.

## 6. Sources 3 and 4 (U4b, U4c) – how they plug in

**U4b – ACL delegations.** A new `IPrivilegeGrantSource` with `Source = DirectoryAcl`:

* Reads `nTSecurityDescriptor` (SD flags control: owner + DACL) of the containers (domain head, OUs, `CN=Users`,
  `CN=Computers`, `CN=Builtin`), the Tier 0 objects (`AdminSDHolder`, the protected groups, `krbtgt`, `OU=Domain Controllers`,
  the DC computer objects, `CN=Policies` and every `groupPolicyContainer`) – one paged search per object class or subtree;
  leaf objects (users, groups) only in an optional deep scan.
* The descriptor parser of `AdMng.Watchdog.Core/Detection/SecurityDescriptorSummary.cs` moves to `AdMng.Core` (Watchdog
  keeps using it from there), so Presentation can parse ACEs.
* Differences from the defaults: the class's `defaultSecurityDescriptor` from the schema plus well-known default trustees
  (SYSTEM, Domain Admins, Enterprise Admins, Administrators, Account Operators where AD grants it, Pre-Windows 2000
  read access…) are ignored; inherited ACEs are reported once at the container they come from.
* Rights → kind `AclRight`, values = right names, scope = `Container` + DN: GenericAll / WriteDacl / WriteOwner (Tier 0 on
  the domain head, AdminSDHolder or a Tier 0 object, else High), DCSync (`DS-Replication-Get-Changes(-All)`, Tier 0), reset
  password and `member` write on OUs (High), create/delete child (Elevated), GPO edit and `gPLink` write (High, Tier 0 on
  a GPO linked to the DCs), LAPS password read (`ms-Mcs-AdmPwd`, `msLAPS-Password`; High on servers, Elevated otherwise),
  `msDS-KeyCredentialLink` / `msDS-AllowedToActOnBehalfOfOtherIdentity` write (High), owner of a Tier 0 object (Tier 0).
* Grants go to the ACE trustee's SID: a group → its members through the graph with the path, an account directly.
  The RBCD descriptor of an account becomes grants of its listed principals ("may impersonate users to X").

**U4c – local rights from GPOs.** A grant source with `Source = GroupPolicy`:

* GPOs from `CN=Policies` (`gPCFileSysPath`), links from `gPLink`/`gPOptions` of the domain head, OUs and sites (enforced,
  block inheritance, disabled links); the computers of each linked OU (subtree) are the scope.
* SYSVOL is read in the WPF process with the operator's Windows identity (UNC path of the DC of the session, rule 4), the
  files only in memory: `GptTmpl.inf` (`[Group Membership]` – Restricted Groups, `[Privilege Rights]`) and GPP
  `Groups.xml`. The parsers of `AdMng.Watchdog.Core/Gpo/Parsers` move to `AdMng.Core` (shared like the catalogue).
* Local groups by SID: Administrators (`S-1-5-32-544`, High on servers, Tier 0 through the Default Domain Controllers
  Policy), Remote Desktop Users (555, Elevated), Power Users (547, Elevated), Backup Operators (551, High), Remote
  Management Users (580, Elevated); sensitive user rights: `SeDebugPrivilege`, `SeBackupPrivilege`, `SeRestorePrivilege`,
  `SeTakeOwnershipPrivilege`, `SeLoadDriverPrivilege`, `SeImpersonatePrivilege`, `SeTcbPrivilege`,
  `SeAssignPrimaryTokenPrivilege`, `SeEnableDelegationPrivilege` (High; Tier 0 on DCs), `SeRemoteInteractiveLogonRight`,
  `SeInteractiveLogonRight` on DCs (Elevated).
* Kind `LocalRight`, scope `Computers` + the linked OU, values = the GPO name and the right; grants to the member SIDs.

Both only add a source class, enum values already exist (`DirectoryAcl`, `GroupPolicy`, `AclRight`, `LocalRight`,
`Container`, `Computers`) with resources in both languages; the tab offers a source as soon as it is registered
(`PrivilegedAccessViewModel` and `PrivilegedAccessScanner` already take `IEnumerable<IPrivilegeGrantSource>`; U4a
passes none – U4b/U4c register theirs in DI and hand them from `DialogViewModelFactory.CreateAccountReview` through
`AccountReviewViewModel` to the tab). As built: section 6.3.

### 6.1 U4b as built

**Reads** (all on the session's DC, descriptors with the SD flags control OWNER | GROUP | DACL – the existing
`DirectorySearch.ReadSecurityDescriptor`; the group SID is not used):

| Read | Objects |
|---|---|
| base | the domain head (also `gPLink`, `wellKnownObjects`), CN=Users and CN=Computers (where `wellKnownObjects` points), `CN=AdminSDHolder,CN=System`, `CN=Policies,CN=System` |
| subtree, paged | `(|(objectCategory=organizationalUnit)(objectCategory=builtinDomain))` – every OU (OU=Domain Controllers flagged Tier 0, its `gPLink` read) and CN=Builtin |
| one level of CN=Policies | `(objectClass=groupPolicyContainer)` with `displayName` |
| subtree, paged | Tier 0: the DC filter of the scanner, `objectSid` = domain-500 / domain-502, the SIDs of the graph's catalogue groups that are protected or Tier 0 |
| schema, one level | `classSchema` of the classes read: `lDAPDisplayName`, `defaultSecurityDescriptor` |
| configuration, one level | `CN=Extended-Rights`: `cn`, `rightsGuid`, `validAccesses` |
| subtree, DN only | server computers (`operatingSystem=*server*`) – only when the schema has a LAPS password attribute |
| subtree, paged | `(msDS-AllowedToActOnBehalfOfOtherIdentity=*)` – the principals of resource-based delegation |
| subtree, paged (deep scan only) | users, computers and groups (also `mS-DS-CreatorSID`), evaluated as they come, nothing kept |

On the Samba lab (400 users, 250 computers) a run with the deep scan takes about 2.5 s.

**Defaults that are not reported.** (1) ACEs of the default trustees: SYSTEM, PRINCIPAL SELF, CREATOR OWNER/GROUP,
OWNER RIGHTS, Enterprise Domain Controllers, Administrators, Domain Admins, Domain Controllers, Read-only DCs, Key Admins,
and in the forest root Enterprise/Schema Admins, Enterprise Read-only DCs, Enterprise Key Admins (any domain while the root
SID is unknown); the object's own SID. (2) Explicit ACEs of the class's `defaultSecurityDescriptor` (read from the schema,
SDDL with the domain/root SID): same trustee (CREATOR OWNER there = the object's owner here, as AD puts the creator in,
or the account in `mS-DS-CreatorSID`: a computer joined through `ms-DS-MachineAccountQuota` is owned by Domain Admins while
the CREATOR OWNER ACEs name the joiner – to be confirmed on Windows Server), type and object types, no more rights, no wider inheritance. (3) What AD and Samba set up beyond the class defaults:
Account and Print Operators on CN=Users and CN=Computers, Group Policy Creator Owners on CN=Policies. On the fresh Samba lab
only its seeded helpdesk delegation (reset password on OU=Pracownicy) is left – also with the deep scan. Read rights are
never reported (only the rights below), so Pre-Windows 2000 access, RAS servers, Cert Publishers' `userCertificate` and the
like never show.

**Inheritance.** An inherited ACE is left out when the nearest read ancestor holds an inheritable (CI) ACE of the same
trustee and object type with at least its rights – it is reported (or left out as a default) there, once. When the source
was not read (an ACE set on CN=System arrives at CN=Policies) it is reported at the object where it was found. It is
reported at the object as well when it applies there (not inherit-only) with a higher level than at its source: Tier 0
objects that AdminSDHolder does not protect inherit (DnsAdmins and the Exchange groups in their containers, OU=Domain
Controllers below the domain head) – member write on the groups of CN=Users is High at CN=Users and Tier 0 at DnsAdmins,
full control of the OUs (inherit-only on the domain head) High there and Tier 0 at OU=Domain Controllers.

**Rights → values and levels** (`AclRight`, value `Right` or `Right:detail`, the detail being the class the ACE is limited
to, the class that may be created or the attribute; the UI shows them in its language). One grant per object and trustee
with every right, level = the highest:

| Right | Level |
|---|---|
| `GenericAll`, `WriteDacl`, `WriteOwner`, `Owner`, `AllExtendedRights`, `WriteAllProperties`, `ResetPassword`, `WriteMember`, `AddSelfToGroup` (Self-Membership), `WriteGpLink`, `WriteGpoProperty`, `WriteKeyCredentialLink`, `WriteAllowedToActOnBehalf` | Tier 0 on the domain head (inherit-only ACEs only when they reach computers: the DCs' accounts inherit them, the protected Tier 0 users and groups do not), AdminSDHolder, a Tier 0 object, OU=Domain Controllers (also inherit-only) and a GPO with an enabled link on the domain head or OU=Domain Controllers; High on other containers and GPOs; on single objects (deep scan) what the object has: at least Elevated, High for a server, the catalogue level the account or group reaches (reset password on a Domain Admin is Tier 0; a delegation inherited from an OU reaches the Tier 0 accounts AdminSDHolder does not protect, e.g. DnsAdmins members, and is reported at them with Tier 0 too) |
| `ReplicationGetChangesAll` (domain head only) | Tier 0 |
| `ReplicationGetChanges`, `ReplicationGetChangesInFilteredSet` (domain head only) | High |
| `CreateChild`, `DeleteChild` | Elevated |
| `ReadLapsPassword` (control access on a LAPS password attribute of the schema) | High when the container holds a server (or the object is one), else Elevated |
| `ActOnBehalfOfOtherIdentity` (listed in the RBCD descriptor of the object) | Tier 0 towards a DC, High towards a server, else Elevated |

Object types come from the session's schema (attribute and class `schemaIDGUID`, property sets by `attributeSecurityGUID`)
and `CN=Extended-Rights` (`rightsGuid`); the fixed GUIDs of the base schema are a fallback, the LAPS attributes are only
known from the schema. `member` write counts through the attribute or its property set (Membership), only where it reaches
groups; `gPLink` write only on the domain head and OUs; any other written attribute of a GPO is `WriteGpoProperty`.

**Model additions.** `PrivilegeGrant.ScopeName` / `PrivilegeFinding.ScopeName` (the GPO's display name); the finding's
title names the object whose permissions hold the right ("Uprawnienie w zabezpieczeniach obiektu Firma", the domain head
by its DNS name). `PrivilegedAccessOptions.DeepAclScan` (setting `DeepAclScan`, off by default, "Głębokie skanowanie"
with the note that it is slower, shown only when the source is registered). The evaluator gives grants on Everyone /
Authenticated Users to every account also when the graph has no node for them (not nested anywhere), with the implicit
group on the path.

**Deviations from the plan above, with the reasons.**

* DCSync: only `DS-Replication-Get-Changes-All` is Tier 0; Get-Changes alone (and In-Filtered-Set) is High – without
  `-All` no secrets replicate (directory sync tools hold it).
* The Tier 0 level covers every takeover right on a Tier 0 object (reset password, member write on a protected group,
  shadow credentials, RBCD write, `gPLink` on the domain head or the DCs' OU), not only GenericAll/WriteDacl/WriteOwner –
  each of them is a path to Tier 0. Inherit-only ACEs on the domain head are High unless they reach computers (the
  domain controllers' accounts are not protected by AdminSDHolder and inherit them).
* Owners: besides Tier 0 objects (Tier 0), owners of containers and GPOs are reported (High: the owner may change the
  DACL); owners of ordinary users, groups and computers are not – AD makes every creator the owner (computers joined by
  users, `ms-DS-MachineAccountQuota`), which would flood the list.
* The deep scan reads computers too (not only users and groups): RBCD and shadow-credential delegations sit on computers;
  the joiner's CREATOR OWNER defaults are not reported (joiner = owner or `mS-DS-CreatorSID`).
* Account/Print Operators on CN=Users and CN=Computers and Group Policy Creator Owners on CN=Policies are defaults although
  no class default holds them (the lab showed them as the only false findings otherwise).
* The levels of RBCD principals were not specified; they follow the target (DC / server / other).

### 6.2 U4c as built

* **Pieces.** `GroupPolicyGrantSource` (Core) reads `GroupPolicyTopology` (LDAP), the files of the GPOs that reach a
  computer through `ISysvolFileReader`, resolves names and hands everything to the pure `GroupPolicyRightsResolver`.
  The parsers moved from `AdMng.Watchdog.Core/Gpo/Parsers` to `AdMng.Core/GroupPolicy`: `GpoText` (strict decoding, INI
  reader) and `GpoXml` (hardened XML) as they were, plus `SecurityTemplate` (Restricted Groups and Privilege Rights as
  structured entries; the Watchdog's `GptTmplParser` takes the principal syntax and the Restricted Groups key split from
  it), `GppGroupsParser` (local group items of Groups.xml) and `GroupPolicyLinks` (gPLink with link order; the Watchdog's
  `GpLinks` and GPC cn parsing delegate to it). The Watchdog's generic snapshot parsers stay in Watchdog.
* **Reads** (all on the session's DC, rule 4): one-level search of `CN=Policies,CN=System` (`cn displayName
  gPCFileSysPath flags gPCMachineExtensionNames gPCWQLFilter nTSecurityDescriptor`, SD flags DACL), a base read of the domain head (`gPLink gPOptions`), one
  subtree search of the OUs with `gPLink` or `gPOptions`, one one-level search of `CN=Sites` for sites with `gPLink` (the
  DCs' server objects only when a site links a GPO), one paged search of the computer accounts (`objectClass
  userAccountControl operatingSystem objectSid primaryGroupID memberOf`; managed service accounts and disabled computers
  left out), then one
  `sAMAccountName` search per 50 names the GPOs use. SYSVOL: `GptTmpl.inf` when the GPO lists the Security extension
  (or lists none), `Groups.xml` when it lists the GPP Local Users and Groups extension (or none); 4 MiB per file, 4
  reads at a time, cancellable. Only GPOs that reach a computer are read.
* **Inheritance** (`GroupPolicyInheritance`, as [MS-GPOL] and GPMC): site, domain, OUs top-down; within a container from
  the highest link order number to link order 1 (the last link of `gPLink`; Samba and GPMC prepend new links); disabled
  links skipped; block inheritance (gPOptions 1, also on the domain head against site links) drops the non-enforced
  links above; enforced links are applied after all others, the deepest container first, so that the enforced link
  highest in the hierarchy wins; a GPO linked twice counts once, at its winning position. GPOs with the computer
  configuration disabled (`flags` 2) and links to GPOs without a container in the domain are left out (the latter
  reported). Computers are grouped by parent container (and the site of a DC); member computers have no site link
  (their site follows the subnet and is not evaluated).
* **Effective settings** per container group: User Rights Assignment per right and Restricted Groups per group
  (Members) and per member (MemberOf) - the highest-precedence GPO wins, as in the security engine; the members of a
  local group are its Members list plus every MemberOf naming it. GPP items in application order: ADD/REMOVE, delete
  all member users / groups (users vs groups by the group graph), Replace and Delete clear what earlier items added;
  items with item-level targeting are kept but conditional - next to, never instead of, an unconditional add of the
  same member (that one keeps its GPO and its computers) - and their removals are not applied. Restricted Groups and
  GPP are evaluated independently (both reported; the CSE order between them is not modelled). On the DCs the "local"
  groups are the domain's groups: a Members line of a privileged domain group of the catalogue (Domain Admins, Group
  Policy Creator Owners …) and a MemberOf line naming one are reported for the DCs the GPO reaches, at the group's
  level, kind `LocalRightKind.DomainGroup` (the tab: the group's catalogue name and description and "the GPO enforces
  this membership: a removal is undone at the next refresh"), so that a *Usuń z grupy…* the DCs revert is explained
  (added after the review); on member computers such lines change nothing. Other domain groups on the left of a
  Members line are a domain membership (in the graph already), not a local right.
* **Principals.** SIDs of the domain; Everyone and Authenticated Users (by SID or by English/Polish name) and Users
  (545, through the graph) - the evaluator now gives grants on Everyone / Authenticated Users to every account even
  when no group of the domain nests them; names `DOMAIN\x`, `x@dns`, `x` of this domain through their SIDs; local groups
  by SID, by the English/Polish names and by the names of the domain's built-in groups. Not repeated: other built-in
  groups (the default holders of the rights, e.g. Administrators in `SeDebugPrivilege`), service SIDs, other domains
  and groups that are Tier 0 in the catalogue (Domain Admins are Tier 0 already). Unknown names are reported.
* **Levels** (`LocalRightsCatalog`): by the roles of the computers the grant reaches - DC (server trust account or
  RODC), server (operatingSystem contains "Server"), workstation; the highest wins. Administrators: Elevated on
  workstations, High on servers; Backup Operators High; Power Users, Remote Desktop Users, Remote Management Users,
  Hyper-V Administrators Elevated; the minor built-in groups Minor; on DCs the catalogue level of the built-in group
  with the operator's override (Administrators, Backup Operators, Account/Server/Print Operators: Tier 0). The sensitive
  user rights of the note are High, Tier 0 on DCs, and so are `SeCreateTokenPrivilege` (any token, as much as
  `SeTcbPrivilege`), `SeManageVolumePrivilege` (raw volume reads, NTDS.dit on a DC) and `SeRelabelPrivilege` (takes
  objects over), added after the review; `SeTrustedCredManAccessPrivilege` (reads saved credentials) is Elevated, High on
  DCs; `SeInteractiveLogonRight` / `SeRemoteInteractiveLogonRight` only on DCs (Elevated). None of these has a default
  holder among the domain's own groups; `SeSecurityPrivilege` is left out (Exchange Servers hold it on the DCs). A right that reaches no reported role is dropped, so a GPO linked to an OU without computers grants
  nothing yet.
* **Findings.** Kind `LocalRight`, source `GroupPolicy`, scope `Computers` + the container of the link, values = the
  GPO name and the right (local group SID or user right constant), and `GroupPolicyGrantDetails` (GPO, link, link
  order, enforced, mechanism, item-level targeting, WMI filter, computers by role). One grant per principal, right,
  mechanism, GPO and link. The tab names local groups as Windows does (`PrivilegedAccess.GroupName.*`) and user rights
  as the security policy editor (`PrivilegedAccess.UserRightName.*`), says what the right gives
  (`PrivilegedAccess.LocalRight.*`), the mechanism, the GPO, the computers, enforced links and conditions.
* **Notices.** `PrivilegeScanContext.Report` / `PrivilegedAccessScanner.Notices`: a source reports what it could not read
  (SYSVOL unavailable with the reason, unreadable or invalid files, unknown names, missing GPOs, gPCFileSysPath pointing
  elsewhere - the files are read from the session DC's SYSVOL by GUID anyway). The tab shows them in a warning bar above
  the list (`PrivilegedAccess.Notice.{code}`, re-formatted on a language change); the run goes on.
* **SYSVOL and credentials** (`WindowsSysvolFileReader`, AdMng.Directory): the UNC path of the session's DC
  (`SessionInfo.Server`, validated by `SysvolPaths`), files read with `FileStream` off the UI thread into a byte array,
  never written anywhere. A session of the Windows account (CurrentUser) reads with the process identity. A session
  opened with other credentials (Negotiate with a typed account, or a simple bind with a UPN or `DOMAIN\user`) gets a
  logon session of the run's own: `LogonUser(LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50)` (as `runas
  /netonly`: local identity unchanged, network access with the session's credential; a UPN goes without a domain,
  `DOMAIN\user` is split), the password copied from `NetworkCredential` into a zeroed unmanaged buffer; every file is
  opened inside `WindowsIdentity.RunImpersonated` with that token (the open handle keeps its SMB session for the reads)
  and the token is closed with the run's connection. The operator's logon session never gets the credential: no
  ERROR_SESSION_CREDENTIAL_CONFLICT with the SMB sessions it already has to its logon DC (GP processing, logon script,
  mapped drives), no other process or window reaching `C$`/`ADMIN$` with the session's (admin) credential, nothing left
  behind after a crash. `NEW_CREDENTIALS` does not check the password, so the reader opens `SYSVOL\<domain>\Policies`
  once at the start: a refused logon (`LogonFailure`), a denied share (`AccessDenied`) or an unreachable DC
  (`Unreachable`) is reported for the run (changed after the review; the first version used `WNetAddConnection2` in the
  operator's logon session). A simple bind with a DN has no SMB logon name (`DistinguishedNameLogon`); other platforms
  get `NotSupported`. Each becomes a notice in the tab ("GPO rights need ... the Windows account"); the other sources
  still run.
* **Deviations from the note.** The scope DN is the container of the link (also a site); the level comes from the
  computers the GPO actually reaches (so the DC OU and the Default Domain Controllers Policy give Tier 0, and a
  domain-level GPO reaching the DCs does too). Administrators on workstations is Elevated (the note names only servers
  and DCs). Restricted Groups precedence, GPP order and computer counts were added to make link order matter.
  WMI filter queries and item-level targeting conditions are not evaluated: findings with a WMI filter or targeting
  are marked conditional. Security filtering is evaluated (added after the review, the note left it to U4b): see
  *Security filtering* below.
* **Security filtering** (`GroupPolicySecurityFilter`): the GPC search reads `nTSecurityDescriptor` with the SD flags
  control (DACL; the parser moved to `AdMng.Core/Security` by U4b) and collects the SIDs allowed and denied the *Apply
  Group Policy* extended right (`edacfd8f-…`, or control access without an object type; inherit-only ACEs ignored). The
  computer search adds `objectSid primaryGroupID memberOf`; a computer's token is its SID, its primary group, its groups
  nested through the group graph, Everyone, Authenticated Users, This Organization and, for a writable DC (primary group
  516), Enterprise Domain Controllers. A computer applies a GPO when an allowed SID is in its token and no denied one
  is; computers of one container that apply the same GPOs are evaluated together, so a GPO linked at the domain head
  but filtered to "Serwery SQL" (or denied to Domain Controllers) counts only the computers it reaches and is not Tier 0
  through the DCs, and a lower-precedence GPO wins where the filtered one does not apply. A descriptor that cannot be
  read counts as applying everywhere (as before). Not modelled: the Read permission, universal groups of other
  domains in the token.
* **Tests.** Core: SecurityTemplate (UTF-16 with BOM, Polish names, both Restricted Groups forms, duplicates, hostile
  encodings), GppGroupsParser (actions, collections, targeting, disabled items, DTD and nesting refused),
  GroupPolicyInheritance (link order, block, enforced, disabled, sites, duplicates), SysvolPaths, and the source end to
  end on the fake DC (`FakeDirectoryBuilder.AddGroupPolicySample`, `FakeSysvolFileReader`). Presentation: the tab in
  Polish with the GPO sample (texts, levels, scope, toggles, notices in both languages, CSV), SYSVOL unavailable, SYSVOL
  of the window session's DC. App: a UI test of the notice bar with the in-memory SYSVOL. Lab (Samba): GPOs from
  `samba-tool gpo create`, links with `setlink` (one enforced) and `setinheritance block`, read over LDAP; GptTmpl.inf
  and Groups.xml written with smbclient and the source end to end through `SmbClientSysvolFileReader` (a Linux test
  reader: `smbclient //<session DC>/SYSVOL -I <lab address> … get … -`, content on stdout, in memory); the Windows
  reader's logon from the lab session (UPN) and a DN bind (refused). Unverified: the Windows reader itself
  (LogonUser with new credentials, impersonated opens, Kerberos to the DC) runs only on Windows and has no automated
  test yet; its logon name split and the Win32 error mapping are tested on any platform.

### 6.3 U4b and U4c together (as built)

The two tracks were built in parallel from U4a and merged; this is how they run side by side.

* **Registration.** `DirectoryAclGrantSource` has no platform dependency and is registered by `AddAdMngPresentation`
  (`TryAddEnumerable`, so a second call does not add it twice). `GroupPolicyGrantSource` needs the Windows SYSVOL reader
  and is registered by the WPF host (`AppHost`: `ISysvolFileReader` → `WindowsSysvolFileReader`, the source as a further
  `IPrivilegeGrantSource`). `DialogViewModelFactory` takes `IEnumerable<IPrivilegeGrantSource>` and hands the same list
  to every `AccountReviewViewModel` → `PrivilegedAccessViewModel` → `PrivilegedAccessScanner`. The tab offers *Delegacje
  w AD* and *Uprawnienia lokalne z GPO* next to the U4a sources (ordered by `PrivilegeSource`), shows the deep scan
  setting only when the ACL source is registered and the notice bar for the notices of any source. The UI test host
  replaces the SYSVOL reader with an empty in-memory one, so no UI test reaches a share; the U4c UI test puts its own.
  With one DI scope per connection window (`docs/dev/multi-window.md`, section 16 for the merge) the sources, the
  SYSVOL reader and `FilterLibrary` stay application-wide singletons - they hold no window state, and every run reads
  through the session of the window that runs it - while the factory, the account review, the saved filters window
  and the filter builder belong to the connection window (its session, dialogs, owner and identity chrome).
* **Shared pieces.** The security descriptor parser moved once to `AdMng.Core/Security` (U4b's move, cherry-picked
  unchanged into U4c, identical in the merge): U4b parses the descriptors of the containers, Tier 0 objects and GPOs with
  it, U4c's `GroupPolicySecurityFilter` reads the *Apply Group Policy* ACEs of the GPO descriptors with it, and the
  Watchdog keeps using it. The GPO parsers live in `AdMng.Core/GroupPolicy` (U4c's move). The evaluator has one list of
  grants on Everyone / Authenticated Users that no group of the graph nests (both tracks had added one – U4b for ACEs,
  U4c for GPO lines); the path step of such a grant is `CN=<SID>,CN=ForeignSecurityPrincipals,<domain>`, built with
  `DistinguishedName` (U4b's form; U4c had used `<SID=…>`), and the tab names it *Wszyscy* / *Użytkownicy
  uwierzytelnieni*. The formatter titles `AclRight` findings by the object whose ACL holds the right and `LocalRight`
  findings by the local group or user right; the values are the rights in the UI language (ACL) or the GPO and the
  right (GPO).
* **Both in one run** (`CombinedGrantSourcesTests`, both samples on one fake DC): an account holds an ACL delegation and
  a local right from a GPO, each with its own path and scope; the GPO source's security filtering evaluates the
  descriptors the ACL sample put on the GPOs (*Apply Group Policy* for Authenticated Users), so the Default Domain
  Controllers Policy still reaches the DC and its `SeDebugPrivilege` stays Tier 0; DCSync is untouched by the GPO source;
  the only notice is the GPP name the domain lacks. Per run both sources read `CN=Policies` (one level, with the
  descriptors): the ACL source for the delegations, the GPO source for the security filtering – two searches of the
  same small container (one entry per GPO), not shared.
* **Lab.** Both lab tests change only their own OU (U4b: `samba-tool dsacl` on the run's OU, no ACE on the shared domain
  head; U4c: GPOs `U4C-…` linked to its OU and deleted at the end) and both run alone in the `SambaTool` collection,
  because both call `samba-tool` commands that write (`dsacl set`, `gpo create` / `setlink`), and such a write made
  concurrent Adds of other test classes fail with LDAP 81 during U4b.
* **Status: built, waiting for a pilot on Windows Server AD.** Everything above was checked on fake DCs and on the Samba
  4.19 lab only. To be confirmed on Windows Server: no false ACL findings on a fresh Windows domain (its default ACLs
  differ from Samba's), Exchange, LAPS (legacy and Windows LAPS), the owner and ACEs of computers joined through
  `ms-DS-MachineAccountQuota`, the deep scan time for 10 000 accounts; the Windows SYSVOL reader (`LogonUser` with new
  credentials, impersonated opens, Kerberos to the DC, the Windows account) – it has never run, it needs Windows;
  agreement with `gpresult /scope computer` / RSoP; a child domain; 10 000 accounts without UI freezes.
* **Left open.** U4b reports nothing when a descriptor cannot be read (the object is skipped); the notice bar U4c added
  could carry that. The two `CN=Policies` reads could be one. U4b's lab test has its own `samba-tool dsacl` runner
  next to U4c's `SambaTools`.

## 7. Queries and performance

All on the session's DC; one window = one session (rule 4).

| Search | When | Filter / attributes |
|---|---|---|
| Group graph | once per run | `(|(&(objectCategory=group)(groupType:…803:=2147483648))(&(objectCategory=foreignSecurityPrincipal)(memberOf=*)))`, attributes `name sAMAccountName objectSid memberOf groupType adminCount objectClass` + group rule attributes; paged |
| Domain controllers | once | `(&(sAMAccountType=805306369)(|(userAccountControl:…803:=8192)(userAccountControl:…803:=67108864)))`, `dNSHostName sAMAccountName` |
| Filter rules | once per rule | the rule's server part AND accounts or security groups, DN only |
| Accounts | once, streamed | `(|(sAMAccountType=805306368)(sAMAccountType=805306369))` AND the scope filter (saved view, group members, saved filters), attributes of `PrivilegeEvaluator.AccountAttributes` (+ client check attributes); paged |
| Ranged memberOf | only for accounts with > 1500 groups | ranged retrieval |
| U4b: security descriptors | once per run (the ACL source) | section 6.1: domain head, CN=Users, CN=Computers, AdminSDHolder, CN=Policies (base), OUs and CN=Builtin, Tier 0 objects, RBCD principals (subtree, paged), GPOs (one level), schema classes and extended rights; with the deep scan users, computers and groups (paged, evaluated as they come) |
| U4c: GPOs, links, computers | once per run (the GPO source) | `CN=Policies` one level (with the DACL for the security filtering), domain head base, OUs with `gPLink`/`gPOptions`, sites with `gPLink` (+ server objects), computers paged; `sAMAccountName` per 50 names; SYSVOL GptTmpl.inf / Groups.xml of the GPOs that reach a computer |

The evaluation is in memory: per account a breadth-first search over its reachable groups (tens of nodes), then the
grants of those nodes. 10 000 accounts × ~30 reachable groups is ~300 000 steps; the peer comparison is linear in accounts ×
direct groups per dimension. Memory: the group graph plus every evaluated account of the scope (needed by the peers) –
tens of MB for 10 000 accounts. `tokenGroups` was considered: it gives the effective SIDs but no path and needs a base
read per account; `LDAP_MATCHING_RULE_IN_CHAIN` per privileged group gives the members but no path either and costs one
search per group – the graph gives both with two searches. The U2 check "privileged with a problem" keeps its in-chain
search (it needs no path).

Streaming: batches every 150 ms or 50 accounts to the UI thread; "Przerwij" cancels the paging; the peer comparison runs
after the stream off the UI thread. After a change through the pipeline the changed accounts are read again (batched OR of
DNs), re-evaluated with the run's graph and grants, and the peers recomputed.

## 8. Filter builder and saved filters

`FilterDefinition`: groups of conditions; every group combines its conditions with I (all) or LUB (any), the groups are
combined with I or LUB; or a raw LDAP filter (validated by `LdapFilter.Validate`). Values are typed text; group DNs are
stored in the portable `{domain}` form.

| Operator | LDAP (always through `LdapFilter`) |
|---|---|
| równa się / różne od | `(a=v)` / `(!(a=v))`; numbers for integer syntaxes, TRUE/FALSE upper case for booleans |
| zawiera / zaczyna się od / kończy się na | `(a=*v*)` / `(a=v*)` / `(a=*v)` |
| istnieje / nie istnieje | `(a=*)` / `(!(a=*))` |
| większe/mniejsze lub równe | `(a>=n)` / `(a<=n)`; dates `yyyy-MM-dd` for GeneralizedTime and FILETIME attributes |
| data starsza / nowsza niż N dni | FILETIME: `(&(a>=1)(a<=FT(now−N)))` / `(&(a>=FT(now−N))(!(a=2^63−1)))`; GeneralizedTime: `(a<=GT)` / `(a>=GT)` |
| flaga ustawiona / nieustawiona | `(a:1.2.840.113556.1.4.803:=mask)` / its negation; decimal or 0x hex |
| członek grupy (także pośrednio) / nie jest członkiem | `(memberOf:1.2.840.113556.1.4.1941:=dn)` / its negation |

The value kind comes from the schema syntax (known names without a schema). **Constructed attributes** (schema
`systemFlags` 0x4, or the known ones) cannot be used in a filter: `FilterCompiler.Plan` gives the server a necessary
condition (in an I group the other conditions; a LUB with a constructed condition cannot narrow down) and the client checks
the whole definition with `FilterEvaluator` (LDAP semantics: any value matches, negations match a missing attribute;
member-of through the group graph where the caller has it, else direct memberOf). Raw filters go to the server only.

Saved filters (`AppSettings.SavedFilters`: id, name, description, definition – text only, like saved views) are managed in
*Zapisane filtry* (Tools menu, command palette, the tab's settings) with the builder (attribute picker from the schema,
group picker, live preview, problems per condition, test count) and JSON export/import for the team (`AdMng.Filters`,
invalid entries skipped, ids never overwritten). Uses:

* (a) the extra filter of *Przegląd kont* (next to the scope; both tabs) and the saved filters of a saved view scope;
* (b) chips on the object lists under the quick filters – ANDed with the location's filter, kept by "Zapisz jako widok"
  (`SavedView.SavedFilters`); with a client check the virtual list (VLV) is not used;
* (c) "mark as privileged with level X" rules of the tab.

Deleting a filter removes it from saved views, the review's scope and the rules.

## 9. UI

The scope row of the window gets *Filtr* (the saved filter). *Sprawdź* (F5) runs the selected tab; the other keeps its
results. Tab *Uprawnienia*:

* *Ustawienia uprawnień* (expander): *Sprawdzaj też* computers/DCs and gMSAs; peers (OU, department, title, M, N %); the
  levels of all catalogue groups (changed ones bold); marked groups/accounts (*Oznacz grupy…*, *Oznacz konta…* with the
  object picker; level, note); rules (saved filter, accounts/groups, level, note; *Dodaj regułę*, *Nowy filtr…*,
  *Zapisane filtry…*). Saved at once.
* Level tiles (Tier 0 / Wysoki / Podwyższony / Drobny with the number of accounts, toggles), the sources with counts
  (toggles), *Tylko nietypowe na tle podobnych kont*.
* The list: name (icon, "(wyłączone)"), logon name, type, level badge, sources, findings, "Nietypowe", location; sortable;
  multi-select. The details pane: every finding with level, title, source, *Skąd* (path), *Zakres*, *Co daje* (what the
  built-in group grants / the peer comparison), values, note.

| Action | How |
|---|---|
| *Usuń z grupy…* | the selected finding's direct group, for the selected accounts that have it on a path: `RemoveFromGroupsOperation`, batch preview; through the primary group a hint instead |
| *Wyczyść adminCount…* | accounts with an orphaned adminCount: `ClearAttributeOperation(adminCount)`, batch preview |
| *Wyłącz konto* | `SetAccountEnabledOperation(false)` (threshold as everywhere) |
| *Właściwości*, *Edycja zbiorcza…*, *Eksportuj CSV…*, *Wyślij wiadomość…* | as in U2; CSV adds the column *Uprawnienia* with every finding and the DN |

The U2 list *Konta uprzywilejowane z problemem* links to the tab ("Wszystkie konta uprzywilejowane: N (zakładka
Uprawnienia) →"); the link runs the tab once if it has not run.

## 10. Data handling

* Results (graph, accounts, findings, peers) exist only in memory of the window and go with it (rule 1). CSV only when the
  operator chooses a file.
* Settings hold only levels, SIDs and portable DNs of marks with notes, filter ids of rules, peer options and saved filters
  (names, conditions, typed values, group DNs) – the same kind of text as saved views. No password-like names (settings
  test).
* Every filter is built with `LdapFilter` (rule 3); raw filters are validated.
* Every change through `IChangeCoordinator` (rule 5).
* U4c: SYSVOL files are read into memory and parsed there; nothing of them (nor of the GPOs) is written to disk or
  logged. A session's credential reaches `LogonUser` only in memory, in a zeroed unmanaged buffer (rule 2); the logon
  token of the run is closed with the run's SYSVOL connection, the operator's own logon session never gets the
  credential.
* U4b: descriptors are parsed in memory; only the findings (rights, trustees, objects) are kept, in the window.

## 11. Edge cases and limitations

* Child domains: memberOf on a non-GC port shows only groups of the domain; Enterprise/Schema Admins of the root domain
  are not seen for accounts of a child domain (they are for the root domain). Foreign principals of trusted domains in
  local groups are not accounts of this domain and are skipped.
* `LDAP_MATCHING_RULE_IN_CHAIN` (filter builder, scope "group members") does not follow primary groups; the graph does.
* A peer set is limited to the scope of the run (an OU scope compares within it).
* Domain controllers are Tier 0 by their primary group and are listed; their own unconstrained delegation is not reported.
* U4b: deny ACEs are not evaluated (an allowed right a deny cancels is still reported); objects of class `container` other
  than CN=Users/CN=Computers (CN=Managed Service Accounts, own containers, CN=System) and the configuration partition
  (sites, PKI) are not read – with the deep scan the ACEs they pass on show at the objects below; GPO edit is judged by
  the ACL of the GPO object in AD, not by the SYSVOL file permissions; links of sites to GPOs do not make them Tier 0;
  rights on Everyone / Authenticated Users give every account the finding; a descriptor that cannot be read is skipped
  without a notice.
* U4c: WMI filter queries and item-level targeting conditions are not evaluated (the findings say "warunkowo"); the
  sites of member computers (they follow the subnet) are not evaluated; the Read permission and universal groups of
  other domains in a computer's token are not modelled for the security filtering; Restricted Groups and GPP are
  evaluated independently (their CSE order is not modelled); Restricted Groups of domain groups count only for
  privileged groups of the catalogue on the DCs; SYSVOL is read only on Windows (elsewhere a notice); local accounts
  and groups of the computers themselves are not seen.
* Samba (lab) accepts every filter of the scanner and the builder (bit filters, in-chain, SID strings, negative and
  2147483648 group types) and lets the administrator set delegation flags, SPNs and adminCount.

## 12. Tests

* Core: catalogue (SID/RID/forest root/never by localized name), filter compiler (every operator, escaping with Polish
  letters, dates, plan with constructed attributes, issues with positions), filter evaluator, peers (thresholds,
  dimensions), scanner end to end on `FakeDirectoryBuilder.AddPrivilegedAccessSample()` (Polish built-in group names,
  nested path, primary group, cycle, distribution group, implicit Authenticated Users, every setting, marks, levels,
  rules, queries); Watchdog consistency with the catalogue.
* Presentation: the tab in Polish (levels, paths, scope, peers text), filters, actions through the pipeline with re-read
  rows, settings (marks, levels, rules, peers, nothing of the results in the JSON), the scope filter for both tabs, the
  link from U2, session ended; the filter editor, library (cascade delete, export/import), window, list chips and saved
  views; dynamic resource families in both languages.
* App: UI tests (tab, windows, chips), smoke test entries, screenshot `19-privileged-access.png`.
* U4b Core: binary descriptors built in the tests on the Polish sample (`AddAclDelegationSample`): every delegation
  type with its level, defaults of the class (CREATOR OWNER = owner) and of the default trustees, inherited copies reported
  once, again at Tier 0 objects where they are worth more (DnsAdmins in CN=Users, OU=Domain Controllers below the domain
  head, a DnsAdmins member in the deep scan), a source that is not read (CN=System → CN=Policies), GPO linked to the domain, RBCD principals, deep scan, the
  remaining rights and levels (owner of krbtgt, `gPLink` write on the DCs' OU and an ordinary OU, RBCD write,
  Self-Membership, all extended rights, a GPO of the DCs with an enabled and one with a disabled link), the
  scanner's paths through groups, Authenticated Users not in the graph, LAPS resolved from the schema, the fixed GUIDs
  without schema and configuration; SDDL reader and access mask.
* U4b Presentation: the tab with the source (titles, rights in Polish, paths, scope, source filter, CSV line), the deep
  scan setting saved and used, no source without registration, DI registration, the `AclRight` resource family; UI test of
  the setting.
* U4b lab (Samba): `samba-tool dsacl` delegates reset password on the users and member write on the groups of the run's OU,
  full control of one user (deep scan only); the source finds them once, at their source, the members get the path, the
  lab's defaults (also the DCs' replication rights on the domain head) stay quiet. Only the run's OU is changed: DCSync, an
  ACE on the shared domain head that other runs would see (and that a killed run would leave behind), is covered by the
  fake DC only. It runs alone (collection
  `SambaTool`): while `samba-tool dsacl set` ran, Adds of other test classes of the same process failed with
  ServerUnavailable (LDAP 81); read-only `samba-tool` calls and runs in another process did not show it (cause not found).
* U4c: see section 6.2 (*Tests*).
* U4b with U4c: `CombinedGrantSourcesTests` (both sources and both samples on one fake DC, section 6.3); the UI test of
  the GPO source checks that the tab offers both registered sources.
* Lab (Samba): nested membership to Account Operators through two groups of the run's OU, a minor built-in group, SPN,
  unconstrained and constrained delegation with protocol transition towards the DC, orphaned adminCount, no
  pre-authentication, rules; every builder operator accepted by the server, the client check of a constructed attribute.
