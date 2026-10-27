# K – Computers and Group Policy (GPO) – design note

Roadmap track K (README, "Ścieżka K – Komputery i zasady grupy (GPO)"). This note is **binding** for the K1 and K2
implementers, who build in parallel from it (section 11 splits the files between them), and it is the architecture
sketch for K3–K5. Status: design, October 2026. Nothing here is built yet.

The scope was decided with the user and is not re-opened here:

* **K1 – computers.** Inventory and reports (OS and build, exact last logon from all DCs, site/subnet,
  `mS-DS-CreatorSID`, `managedBy`, type workstation/server/DC; reports: OS past end of support, Windows 11/Server
  builds, inactive N days, servers without owner, computers in unexpected OUs; CSV on demand). Computer account
  lifecycle (prestage with the join right for a chosen user/group, reset computer account, move, disable, delete – all
  through the pipeline). Stale computers: **report only**, no automatic disabling. Remote actions over WinRM/Kerberos
  **with the window's credentials** (logged-on users, `gpupdate /force`, restart with confirmation, delay and user
  message, service states) and launching ping, mstsc, `compmgmt.msc` and `eventvwr` for the computer.
* **K2 – GPO in the main tree.** A *Obiekty zasad grupy* node under the domain (like GPMC); a *Zasady grupy* view for
  the domain, OUs and sites (linked GPOs, order, enforced, link enabled, block inheritance, inherited GPOs); create,
  copy, rename, delete GPO; link/unlink, reorder, enforce; disable the user/computer part; security filtering; WMI
  filters; delegation of GPO editing; backup and restore (operator-chosen folder); compare two GPOs.
* **Later stages, design only:** K3 ADMX editor with central store updates, K4 security settings, K5 "what will this
  computer get" modelling report (section 6).

Rules of CLAUDE.md that shape the design: no directory data on disk – GPO backups only to a folder the operator picks
(1); credentials only in memory, also for WinRM and launched tools (2); every filter and DN through `LdapFilter` /
`DistinguishedName` (3); one DC per session – **SYSVOL is always the share of the session's DC** (4); every write
through the change plan – **this track extends the pipeline to SYSVOL file operations** with preview, audit and
rollback (5); async and cancellable everywhere (6); per-connection services are Scoped (`docs/dev/multi-window.md`).

## 1. Decisions at a glance

| Topic | Decision | Section |
|---|---|---|
| Computer inventory UI | *Przegląd komputerów* window (one per connection window), built like *Przegląd kont* (U2): scope, reports, tiles, list, details, actions, CSV on demand | 4.5 |
| End of support | Built-in `WindowsReleaseCatalog` (build + edition → dates) with operator overrides in the settings; the report names the catalogue's date | 4.1 |
| Site of a member computer | Resolved on demand: DNS of `dNSHostName` → longest prefix among the subnet objects of the forest; nothing stored | 4.4 |
| Join right on prestaged accounts | The ACE set ADUC writes, through a new pipeline action `ModifySecurity` (ACE delta on the DACL) | 3.1, 4.6 |
| Reset computer account | `ResetPassword` with a random 120-character password that nobody sees; no "pre-Windows 2000" option | 4.6 |
| Remote actions library | **CIM over WS-Management** (`Microsoft.Management.Infrastructure` 3.0.0), Kerberos only, explicit credential from the window; not the PowerShell SDK | 4.7 |
| Launched tools | ping in-app; `mstsc /v:` without credentials; `compmgmt.msc` / `eventvwr` with the window's credential through `CreateProcessWithLogonW(LOGON_NETCREDENTIALS_ONLY)` | 4.8 |
| SYSVOL writes | `ISysvolFileWriter` next to the reader; Windows implementation with the window's credential (the U4c logon); Linux `smbclient` implementation for lab tests only | 3.2 |
| Atomicity | Per GPO: stage temp files → verify → rename over the targets → `GPT.INI` → `versionNumber` (compare-and-swap = commit point); a failure up to the commit puts the old files back from memory | 3.3 |
| GPO versions | Never decrease; only content changes bump them; rollback bumps again | 3.4 |
| Pipeline | New `ChangeAction`s for SYSVOL, `ChangePlan.Sequential`, semantic PowerShell equivalents per object (`New-GPO`, `New-GPLink` …) | 3.3 |
| GPO view | Shell document tabs (`IShellDocument`) for the GPO list, one GPO and the *Zasady grupy* of a container; tree nodes *Obiekty zasad grupy*, *Filtry WMI*, *Lokacje* | 5.2 |
| Folder permissions | Kept consistent with the GPO's AD permissions (the mapping Samba's `samba-tool gpo` uses, which mirrors GPMC) | 5.4 |
| Backup format | **GPMC-compatible** (`manifest.xml`, `{id}\Backup.xml`, `bkupInfo.xml`, `DomainSysvol\GPO\…`) plus an `admng.json` sidecar; GPMC backups (and Microsoft security baselines) can be restored/imported | 5.5 |
| Parallel work | K1 owns the ACL action, K2 owns the SYSVOL write path; shared files get additive changes at fixed anchors | 11 |

## 2. Architecture

```
AdMng.Core
  Security/      AclChange, DaclEditor, SecurityDescriptorWriter, ExtendedRightIds            (K1, used by K2)
  Batch/         ChangeAction + payloads, executor/rollback/PowerShell for the new actions      (K1: ACL, K2: SYSVOL)
  Computers/     ComputerFacts, ComputerClassifier, WindowsReleaseCatalog, ComputerInventory,  (K1)
                 PlacementRules, SubnetMap, SiteTopology, ComputerJoinRights
  Computers/Remote/  IRemoteComputerClient, ILocalToolLauncher, IHostAddressResolver, IPingService, models (K1)
  GroupPolicy/   (U4c reader, topology, inheritance, links)                                      (existing)
                 ISysvolFileWriter, GptIni, GpoVersion, GpLinkValue                                (K2)
  GroupPolicy/Management/  GroupPolicyReader, GroupPolicyPlanner, GpoTransaction, GpoFolderAcl,
                 GpoPermissionLevels, WmiFilters                                                  (K2)
  GroupPolicy/Content/     settings parsers moved from AdMng.Watchdog.Core (shared with the Watchdog)  (K2)
  GroupPolicy/Backup/      GPMC backup reader/writer, sidecar                                     (K2)
  GroupPolicy/Comparison/  GroupPolicyComparer                                                    (K2)
AdMng.Directory   SessionLogons (public logon of a session, K1); SD-flag modify (K1);
                  WindowsSysvolFileWriter (K2)
AdMng.Remote      (new, net10.0, Windows code paths) CimRemoteComputerClient, WindowsLocalToolLauncher,
                  DnsHostAddressResolver, IcmpPingService                                          (K1)
AdMng.Presentation  Computers/ (K1), GroupPolicy/ + Navigation nodes (K2), Changes/ChangePlanPreview (K2)
AdMng.App         Computers/ windows (K1), GroupPolicy/ documents and windows (K2)
```

`AdMng.Remote` is a separate project so that the Windows-only CIM package never reaches `AdMng.Directory`, which the
Linux Watchdog service references (`AdMng.Watchdog.Service` → `AdMng.Directory`). It targets `net10.0` (compiles on
Linux), guards every Windows call with `OperatingSystem.IsWindows()` / `[SupportedOSPlatform("windows")]` and is
referenced only by `AdMng.App` and its own test project.

## 3. Shared foundations

### 3.1 Security descriptor writes (`ModifySecurity`, K1 milestone K1.1)

Needed by K1 (join right on a prestaged computer) and K2 (security filtering, delegation). Today the pipeline writes
only string values and the session cannot send the SD flags control on a modify.

* **Core/Security.** `AceSpec` (type allow/deny, flags, mask, trustee SID, object type, inherited object type – the
  writable subset of the existing `SecurityDescriptorAce`), `AclChange(IReadOnlyList<AceSpec> Added,
  IReadOnlyList<AceSpec> Removed)`, `DaclEditor.Apply(byte[] descriptor, AclChange change)` → new self-relative
  descriptor holding only the DACL (control `SE_DACL_PRESENT`, the protected bit kept as read), and
  `SecurityDescriptorWriter` (serialization; round-trip tested with `SecurityDescriptorSummary`). Apply is a delta on
  the **current** DACL: an added ACE already present (same type, flags without `INHERITED`, mask ⊇, SID, object types)
  is a no-op, a removed ACE that is absent is a no-op; inherited ACEs are never touched; the result is put in
  canonical order (explicit deny, explicit allow – object ACEs inside their group in their original order – then
  inherited ACEs unchanged). `ExtendedRightIds`: Reset Password `00299570-246d-11d0-a768-00aa006e0529`, Validated
  write to DNS host name `72e39547-7b18-11d1-adef-00c04fd8d5cd`, Validated write to SPN
  `f3a64788-5306-11d1-a9c5-0000f80367c1`, Account Restrictions property set `4c164200-20c0-11d0-a768-00aa006e0529`,
  Apply Group Policy `edacfd8f-ffb3-11d1-b41d-00a0c968f939`.
* **Pipeline.** `ChangeAction.ModifySecurity` with payload `PlannedChange.Acl` (`AclChange`) and, for the preview,
  the plan-time DACL (`AclChange.PlannedFrom` – summary values, never written back). The executor reads
  `nTSecurityDescriptor` with the SD flags control `DACL_SECURITY_INFORMATION` (4), applies the delta and writes the
  result with the same control (`ModifyOptions.SecurityDescriptorFlags`, new; `LdapDirectorySession` adds the
  `SecurityDescriptorFlagControl`; `FakeDirectorySession` honours it). There is no compare-and-swap for descriptors:
  the delta is re-applied on the fresh DACL, so concurrent unrelated ACL changes are kept. Rollback = the inverse delta
  (added ↔ removed). PowerShell: `$acl = Get-Acl -Path "AD:$dn"`, one `ActiveDirectoryAccessRule` per ACE with
  `AddAccessRule` / `RemoveAccessRuleSpecific`, `Set-Acl` – with `-Server` through `New-PSDrive` pinned to the plan's DC
  (the `AD:` drive of the ActiveDirectory module). The preview shows one row per ACE: trustee name, right names
  (`SecurityDescriptorSummary.KnownRightName`, the U4b right texts), *dodane* / *usunięte*.
* **Not covered:** owner and SACL writes (owner changes need `WRITE_OWNER` and usually `SeRestorePrivilege`; SACLs need
  `SeSecurityPrivilege`).

### 3.2 SYSVOL write infrastructure (K2 milestone K2.1)

Contract next to the reader, in `AdMng.Core/GroupPolicy/ISysvolFileWriter.cs`:

```csharp
public interface ISysvolFileWriter
{
    /// SYSVOL of the session's DC (rule 4) opened for reading and writing; dispose to release the logon.
    Task<ISysvolWriteConnection> OpenForWriteAsync(IDirectorySession session, CancellationToken cancellationToken);
}

public interface ISysvolWriteConnection : ISysvolConnection          // ReadGpoFileAsync + Server
{
    Task<IReadOnlyList<SysvolEntry>> ListGpoFolderAsync(Guid gpoId, SysvolListOptions options, CancellationToken ct);
    Task<bool> GpoFolderExistsAsync(Guid gpoId, CancellationToken ct);
    Task CreateGpoFolderAsync(Guid gpoId, CancellationToken ct);                  // fails when it exists
    Task WriteTempFileAsync(Guid gpoId, string tempRelativePath, ReadOnlyMemory<byte> content, CancellationToken ct);
    Task ReplaceAsync(Guid gpoId, string tempRelativePath, string targetRelativePath, CancellationToken ct); // rename, replace
    Task DeleteFileAsync(Guid gpoId, string relativePath, CancellationToken ct);
    Task DeleteEmptyFolderAsync(Guid gpoId, string relativeFolder, CancellationToken ct);
    Task DeleteGpoFolderAsync(Guid gpoId, CancellationToken ct);                  // recursive, the whole {GUID} folder
    Task<byte[]?> GetGpoFolderSecurityAsync(Guid gpoId, CancellationToken ct);    // self-relative SD, DACL only
    Task SetGpoFolderSecurityAsync(Guid gpoId, byte[] descriptor, CancellationToken ct); // protected DACL, propagated
}
```

`SysvolEntry(string RelativePath, bool IsFolder, long Length, DateTimeOffset LastWrite)`. Every path goes through
`SysvolPaths.TryNormalize`; the GPO folder name is `SysvolPaths.FolderName(gpoId)`; the host and domain through
`SysvolPaths.TryNormalizeHost` – as the reader. Failures throw `SysvolWriteException(SysvolWriteError, message)` with
`AccessDenied`, `NotFound`, `AlreadyExists`, `Unreachable`, `LogonFailure`, `DiskFull`, `Failed`; the message never
contains a path or content (rule 1: diagnostic logs are value-free).

**Windows implementation** – `WindowsSysvolFileWriter` (AdMng.Directory, beside `WindowsSysvolFileReader`): the same
logon as the reader (process identity for a Windows-account session; otherwise a `LogonUser(NEW_CREDENTIALS)` token of
the run's own, the password in a zeroed unmanaged buffer, every file call inside `WindowsIdentity.RunImpersonated`, the
token closed with the connection). The `WindowsSysvol` logon helper becomes shared by reader and writer (K2 refactors it
inside AdMng.Directory). Temp files are written with `FileMode.CreateNew`, `FileShare.None`, flushed
(`FileStream.Flush(true)`) and closed before `File.Move(temp, target, overwrite: true)` – SMB2 `SET_INFO
FileRenameInformation` with `ReplaceIfExists`, atomic on the DC's NTFS. Folder security through
`DirectoryInfo.SetAccessControl` with a `DirectorySecurity` built from the binary form (protected DACL, propagated to
the children by the system). Off the UI thread, cancellable between files; a blocking open runs on the thread pool.

**Linux implementation – tests only** – `SmbClientSysvolFileWriter` in
`tests/AdMng.Directory.IntegrationTests/Infrastructure` (beside `SmbClientSysvolFileReader`):
`smbclient //<session DC>/SYSVOL -I <lab address> -U … -m SMB3` with `PASSWD` in the environment (the lab's documented
password), commands `mkdir`, `put /dev/stdin "<temp>"` (content on stdin – verified on the lab: no local temp file),
`rename "<temp>" "<target>" -f` (verified on the lab: replaces the existing file), `del`, `rmdir`, `deltree`,
`recurse ON; ls` (listing), and `smbcacls --sddl` / `smbcacls -S` for the folder DACL. It never ships; the WPF app has
no Linux SYSVOL path (rule 2 forbids putting the window's password on a command line or in a credentials file).

**Temp names and leftovers.** `<target>.admng-<runId:N>.tmp` in the target's folder (same folder ⇒ same volume ⇒ atomic
rename). A crash can leave temp files: the next write to that GPO lists the folder, shows them in the preview
(*pozostałości poprzedniego zapisu*) and deletes only names matching `*.admng-*.tmp`. The Watchdog's daily
reconciliation would report such a leftover as `GroupPolicyContentOutOfBand` – which is correct.

**Limits** (constants of `GpoTransaction`, enforced at plan time): ≤ 2 000 entries per GPO folder, ≤ 32 MiB per file,
≤ 128 MiB of in-memory copies per plan. A GPO beyond them is refused for copy, backup, restore and import with the
reason (*GPO jest zbyt duże do bezpiecznej kopii w pamięci*); link, status and permission changes still work.

### 3.3 Change pipeline extensions (K2 owns everything here except `ModifySecurity`)

**New actions** (`ChangeAction`, K2):

| Action | Payload | Executed as |
|---|---|---|
| `CreateGpoFolder` | `Sysvol.GpoId` | create `{GUID}`, `Machine`, `User` and `GPT.INI` (`[General]\r\nVersion=0\r\n`, exactly what `samba-tool gpo create` writes – lab) |
| `WriteGpoFile` | `Sysvol` (`GpoId`, `RelativePath`, `OldContent` or null, `NewContent`) | staged (temp), verified, switched (3.3 *transaction*) |
| `DeleteGpoFile` | `Sysvol` (`OldContent`) | verified, deleted in the switch phase |
| `SyncGpoFolderSecurity` | `Sysvol` (`OldFolderSecurity` read at plan time) | read the GPC's DACL now, map it (5.4), write the folder DACL |
| `SetGpoFolderSecurity` | `Sysvol` (`NewFolderSecurity`, `OldFolderSecurity`) | write the given folder DACL (used by rollbacks of a sync) |
| `SetGpoVersion` | `GpoVersion` (`Old`, `New` as `GpoVersion` = user/computer halves) | `GPT.INI` `Version=` line, then `versionNumber` compare-and-swap |
| `DeleteGpoFolder` | `Sysvol.GpoId` | recursive delete; not reversible |

`PlannedChange` gets `Sysvol` (`SysvolChange?`) and `GpoVersion` (`GpoVersionChange?`); their `ToString` print the
action, the relative path and byte counts only. `ObjectPlan.DistinguishedName` of every SYSVOL step is the GPC DN – the
GPO is the object, so the session log, the published `DirectoryChange` and the Watchdog all name the GPO.

**Ordering.** `ChangePlan.Sequential` (new, `init`): the executor runs objects one by one in plan order with
`StopOnFirstError`, whatever `BatchOptions` say. Every GPO plan is sequential (the GPC must exist before its child
containers and before a container links it). `RollbackPlanner` emits the objects of a sequential plan in **reverse**
order. Within one object plan the planners put the content transaction (below) before attribute, ACL and folder-ACL
steps of an existing GPO, so a failed transaction leaves the GPO untouched and a later failure is an ordinary partial
failure that *Cofnij* reverts (a new GPO: `Create` and `CreateGpoFolder` come first, then its content).

**The GPO transaction** (`GpoTransaction`, Core/GroupPolicy/Management, called by `BatchExecutor.Sysvol.cs` for the
SYSVOL steps of one object plan):

1. **Stage** – every `WriteGpoFile` is written to its temp name. A failure deletes the temps written so far; nothing
   else changed.
2. **Verify** – every target is read again and compared with the plan (`OldContent` bytes, or "absent"); `GPT.INI`
   `Version` and the GPC `versionNumber` are read and compared with `GpoVersion.Old`. Any difference → step result
   `Conflict` (`BatchExecutor.ConflictMessagePrefix`), temps deleted, nothing changed.
3. **Switch** – renames (temp → target) and deletes, in plan order.
4. **Commit** – `SetGpoVersion`: write `GPT.INI` (stage + rename, only the `Version=` line changes, every other line
   and the encoding are kept), then LDAP modify of `versionNumber` as delete(old)+add(new) – the compare-and-swap is the
   commit point (the same CAS the executor uses for every replace).
5. **Compensate** – a failure in 3 or 4 writes the old contents back from memory (stage + rename), deletes created
   files, re-creates deleted ones and, if `GPT.INI` was already written, writes its old bytes back; the steps are
   reported failed with *zmiany plików cofnięto*. Only when compensation itself fails is the GPO left inconsistent:
   the object is reported with the error *GPO niespójne – użyj „Wyrównaj wersje”* and the GPO document's *Stan* tab
   shows it (5.2).

Why not leave failed SYSVOL writes for the operator's *Cofnij*: files written without a version bump are published by
the **next** legitimate edit of anyone (GPMC bumps the version and clients then read our half-written content). Content
is therefore either committed with its version or put back at once. The window in which a client can read a mix of old
and new files is the time of the renames (milliseconds per file); clients only reprocess a GPO whose version changed,
so outside `gpupdate /force` they never act on it before the commit.

**Plan-time capture.** Planners read every file they will replace or delete (and the folder DACL for
`SyncGpoFolderSecurity`) through the write connection and keep the bytes in the plan – in memory only, bounded (3.2) –
for verification, preview diffs and rollback.

**Rollback** (`RollbackPlanner.Sysvol.cs`, K2): `WriteGpoFile` → write `OldContent` back (or delete a created file);
`DeleteGpoFile` → write `OldContent`; `SyncGpoFolderSecurity` → `SetGpoFolderSecurity` with the plan-time descriptor;
`SetGpoVersion` → `SetGpoVersion` from the executed new value **up** (each half that changed +1, never back to the old
number – clients must reprocess the restored settings); `CreateGpoFolder` → `DeleteGpoFolder` guarded by "the folder
holds only `GPT.INI` with version 0, `Machine`, `User`"; `DeleteGpoFolder` → `NotReversible` (use the backup taken
before the delete, 5.3). The file steps of a rollback run through the same transaction, with their `SetGpoVersion`.

**PowerShell equivalents.** `ObjectPlan.PowerShell` (`PowerShellEquivalent?`, new): when set, the exporter writes its
commands for that object instead of the per-step lines; an empty list means *covered by the command of an earlier
object* (written as a comment). Commands are structured (`PowerShellCommand(string Cmdlet, IReadOnlyList<(string
Name, string? Value)> Parameters)`), rendered with `PowerShellExporter.Quote`, so planners never build script text. The
header adds `#Requires -Modules GroupPolicy` when a plan uses one of its cmdlets. Rollback plans and SYSVOL steps
without a semantic equivalent are rendered as comments (`# SYSVOL: zapis Machine/Registry.pol (1 234 B) – brak
odpowiednika; użyj Restore-GPO / Set-GPRegistryValue`), never with file content. The cmdlets per operation are listed in
5.3.

**Preview.** K2 adds `ChangePlanPreviewViewModel` (Presentation/Changes) + `ChangePlanPreviewWindow` (App): the batch
editor's preview rows (`PreviewModels`) for a ready plan, the PowerShell tab, *Wykonaj* / *Anuluj*, then the result with
*Cofnij*; every GPO operation opens it (no threshold-based direct execution for GPO plans). SYSVOL rows show the path,
size before/after and *Pokaż różnice* (text diff for INI/INF/XML decoded with `GpoText`, "binary" otherwise); the version
row shows `komputer 3 → 4, użytkownik 0 → 0`. K1 adds the `ModifySecurity` rows (3.1).

**Audit.** Every executed step is an `AuditEntry` as today: SYSVOL steps with `Attribute = "sysvol:<relative path>"`,
`OldValue`/`NewValue` = `sha256:<hex> (<n> B)` – never content; `SetGpoVersion` with `versionNumber` old → new.

### 3.4 GPO version numbers

`versionNumber` of the GPC and `Version=` of `GPT.INI`: user version in the high 16 bits, computer version in the low 16
bits (`GpoVersion.FromInt32` / `ToInt32`). Rules:

* Only content changes bump: a plan that changes files below `Machine/` bumps the computer half, below `User/` the user
  half, root files (`GPO.cmt`) neither. Links, `flags`, the WMI filter link, permissions and the display name never
  bump (as GPMC).
* New = max(AD value, `GPT.INI` value) + 1 for each changed half; a half at 65 535 is an error (*wersja GPO osiągnęła
  maksimum*) instead of wrapping.
* A plan whose object writes content without a `SetGpoVersion` step is invalid (planner assertion + validation error
  `GpoVersionMissing`): content without a version change is exactly what the Watchdog reports as out of band.
* Order on the wire: content → `GPT.INI` → `versionNumber`. The Watchdog triggers its content read on the
  `versionNumber` change (`docs/dev/watchdog-linux-design.md` 8.1) and attributes it from the 5136 of our LDAP modify –
  the operator of the window – so a GPO changed in AD Manager is seen "było → jest" as one change.
* The *Stan* tab compares AD and `GPT.INI`: `GPT.INI` behind AD = SYSVOL replication pending on this DC (no action);
  `GPT.INI` ahead of AD = a failed write or AD replication lag → *Wyrównaj wersje* (a `SetGpoVersion` to max + 1 on
  both halves).

## 4. K1 – Computers

### 4.1 Model

* `ComputerType` (Core/Computers): `Workstation`, `Server`, `DomainController`, `ReadOnlyDomainController`, `Unknown`.
  `ComputerClassifier`: UAC `SERVER_TRUST_ACCOUNT` (8192) → DC, `PARTIAL_SECRETS_ACCOUNT` (67108864) → RODC (primary
  group 516/521 as a second signal), `operatingSystem` containing "Server" → server, a Windows client string or build →
  workstation, no `operatingSystem` → unknown (prestaged and never joined, or a non-Windows system that set nothing).
  Mapping to U4c's `ComputerRole` for shared code; `ComputerRole` itself is not changed.
* `ComputerFacts`: DN, name, `dNSHostName`, `sAMAccountName`, SID, type, enabled, `operatingSystem`,
  `operatingSystemVersion` (`"10.0 (26100)"` → major, minor, build), `operatingSystemServicePack`,
  `lastLogonTimestamp`, `pwdLastSet`, `whenCreated`, `managedBy`, `mS-DS-CreatorSID`, `description`, `location`,
  parent DN, the LAPS expiry time when the schema has `msLAPS-PasswordExpirationTime` or
  `ms-Mcs-AdmPwdExpirationTime` (presence only – the password is never read).
* `WindowsReleaseCatalog` (built-in table, `ValidAsOf` date) → `WindowsReleaseMatch(Release?, EditionClass,
  EndOfSupport?, SupportState)`. The build decides the release – **not** the product string: early Windows 11 builds
  wrote "Windows 10 …" into `operatingSystem`, so build ≥ 22000 on a client string is Windows 11. The product string
  decides the family (Server) and the edition class (`HomePro`, `EnterpriseEducation`, `Ltsc`, `IotLtsc`, `Server`),
  because end dates differ by edition. `SupportState`: `Supported`, `EndingSoon` (≤ N days, default 90), `Ended`,
  `Unknown` (build not in the catalogue), `NotWindows`. Operator overrides in the settings (build + edition class →
  date, e.g. bought ESU for Windows 10) win. The window shows *Katalog wersji z dnia …* and a warning when it is older
  than 9 months. Orientation rows (the implementer re-checks every date against Microsoft Lifecycle when filling the
  table):

  | Build | Release | End of support |
  |---|---|---|
  | 19045 | Windows 10 22H2 | 2025-10-14 (ESU: overrides) |
  | 19044 | Windows 10 Enterprise LTSC 2021 | 2027-01-12 (IoT LTSC 2032-01-13) |
  | 17763 | Windows 10 LTSC 2019 / Windows Server 2019 | 2029-01-09 |
  | 14393 | Windows 10 LTSB 2016 / Windows Server 2016 | 2026-10-13 / 2027-01-12 |
  | 22621 | Windows 11 22H2 | Home/Pro 2024-10-08, Ent/Edu 2025-10-14 |
  | 22631 | Windows 11 23H2 | Home/Pro 2025-11-11, Ent/Edu 2026-11-10 |
  | 26100 | Windows 11 24H2 / LTSC 2024 / Windows Server 2025 | Home/Pro 2026-10-13, Ent/Edu 2027-10-12, LTSC 2029-10-09, Server 2034-10-10 |
  | 26200 | Windows 11 25H2 | Home/Pro 2027-10-12, Ent/Edu 2028-10-10 |
  | 20348 | Windows Server 2022 | 2031-10-14 |
  | 9200 / 9600, 7601 and older | Windows 8/8.1, Server 2012/2012 R2, Windows 7, Server 2008 R2 | ended |

* `ComputerInventory.Evaluate(facts, now, timeZone, options)` → `ComputerStatus` (pure, like `AccountStatus`): support
  state, days since the last logon (or never), owner missing / owner disabled, placement findings.
* `PlacementRules` (settings): per type a list of allowed containers (portable DNs with `{domain}`, subtree). Built-in
  rules that need no configuration: a computer in the **default computers container** (`wellKnownObjects`, normally
  `CN=Computers`) is *nieprzeniesiony po dołączeniu*; a DC outside `OU=Domain Controllers` and a non-DC inside it.

### 4.2 Queries

All on the window's session (rule 4) except the exact last logon (U2's short-lived read-only sessions to the other DCs).

| Read | When | Filter / attributes |
|---|---|---|
| Computers | once per run, paged, streamed | `(sAMAccountType=805306369)` AND NOT `msDS-GroupManagedServiceAccount` / `msDS-ManagedServiceAccount` / `msDS-DelegatedManagedServiceAccount` classes AND the scope filter (domain, OU subtree, saved view, group members as U2); attributes of 4.1 (+ `nTSecurityDescriptor` owner only when the *Właściciel obiektu* column is on – SD flags `OWNER`) |
| Creator names | after the stream | `(|(objectSid=S-…)…)` per 50 SIDs, `name sAMAccountName objectClass` |
| Owners (`managedBy`) | after the stream | `(|(distinguishedName=…)…)` per 100, `name objectClass userAccountControl` |
| Sites and subnets | on the first site resolution | `CN=Sites,CN=Configuration,…`: `(objectClass=site)` `name gPLink`, `(objectClass=subnet)` `cn siteObject` (one level of `CN=Subnets`) |
| Default computers container | once per window | the session's `WellKnownObjectMap` (already read by the tree) |
| Exact last logon | on demand, selection ≤ 500 | `DomainControllerReader` (U2) with `lastLogon`, at most 4 DCs in parallel |

Every filter through `LdapFilter`. Streaming, batches every 150 ms or 50 rows, *Przerwij* cancels, generation counter
drops late batches (U2 pattern). 10 000 computers ≈ the U2 account run.

### 4.3 Reports

| Report | Server pre-filter | Client side |
|---|---|---|
| *Systemy po końcu wsparcia* (+ *kończy się w ciągu N dni*) | enabled, `(operatingSystem=*)` | catalogue match `Ended` / `EndingSoon` |
| *Wersje Windows 11 i Server* | `(operatingSystem=*)` | grouping by release (tile per release with counts: *Windows 11 24H2 – 133*), list filtered by the selected release |
| *Nieaktywne od N dni* | enabled, `(lastLogonTimestamp<=FT(now−N))` | same; exact value on demand drops rows that are active on another DC (U2 5) |
| *Nigdy niezalogowane* | enabled, `(!(lastLogonTimestamp=*))(whenCreated<=GT(now−N))` | prestaged accounts nobody joined |
| *Serwery bez właściciela* | enabled, `(operatingSystem=*Server*)` | `managedBy` missing, or the owner is disabled / deleted |
| *Komputery w niewłaściwych OU* | – (scope) | `PlacementRules` |
| *Wyłączone* | disabled | informational (the list feeds *Usuń…* / *Przenieś…*) |
| *Bez systemu operacyjnego* | `(!(operatingSystem=*))` | prestaged / non-Windows |

Parameters (N days, ending-soon days) and the enabled reports are remembered (settings, section 8). **Stale computers
are a report only**: nothing is disabled or moved without the operator selecting rows and confirming a plan.

### 4.4 Site and subnet

`SiteTopology.ReadAsync(session)` → sites and subnets of the forest (configuration partition of the session's DC).
`SubnetMap` parses `cn` (`10.1.0.0/16`, IPv6 too; invalid entries reported once) and matches an address by the longest
prefix. A DC's site is its server object's site (`DomainControllerCatalog`). A member's site is not stored in AD: the
review resolves `dNSHostName` with `IHostAddressResolver` (default `Dns.GetHostAddressesAsync`, 2 s timeout, 16 in
parallel, cancellable) – on demand (*Ustal lokację* for the selection, or for the whole list after a confirmation above
500 rows). Columns *Lokacja*, *Podsieć*, *Adres IP*; texts *brak podsieci*, *nie rozwiązano*. DNS goes to the
workstation's resolver, not to the session's DC: rule 4 is about LDAP and SYSVOL, and the note says so in the column
tooltip. Results only in memory.

### 4.5 UI

**Przegląd komputerów** (`ComputerReviewViewModel` + `ComputerReviewWindow`; *Narzędzia → Przegląd komputerów…*, palette
`tools.computerReview`; one per connection window through `ToolWindows`): scope row (domain / OU / saved view / group,
as U2), report set with parameters, *Sprawdź* (F5) / *Przerwij*, tiles grouped *System* (end of support, releases),
*Aktywność* (inactive, never logged on, disabled), *Porządek* (servers without owner, unexpected OUs, without OS), the
list (name with type icon, DNS name, type, system, release, support state with date, last logon, exact last logon,
site/subnet, creator, owner (`managedBy`), location, description, OU; optional: object owner, LAPS, `pwdLastSet`),
details pane (facts, findings, the catalogue row), status line (*stan na godz.*, rows read). Actions (selection, or the
whole list where it makes sense; every change through `IChangeCoordinator`):

| Action | How |
|---|---|
| *Właściwości*, *Edycja zbiorcza…*, *Eksportuj CSV…* | as U2 (CSV: file chosen by the operator, regional separator, formula protection, DN column) |
| *Dokładne ostatnie logowanie*, *Ustal lokację* | 4.2, 4.4 |
| *Zdalne akcje…*, *Pulpit zdalny*, *Ping* | 4.7, 4.8 |
| *Wyłącz…*, *Przenieś…*, *Usuń…*, *Resetuj konto…* | 4.6 (batch preview above the threshold, typed confirmation naming the domain) |
| *Ustaw właściciela…* | `SetValuesOperation(managedBy, [dn])` from the object picker |

After every published change the affected rows are read again (debounced), as U2.

**Property sheet of a computer** (`PropertyPageFactory.Computer`, K1): *System operacyjny* adds the release and the
support state; *Ogólne* adds the type; a new page *Stan* (last logon approximate + *Sprawdź na wszystkich DC*, site
(*Ustal*), creator, object owner, LAPS present) with buttons *Zdalne akcje…*, *Pulpit zdalny*, *Zarządzanie komputerem*,
*Podgląd zdarzeń*, *Ping*.

**Zdalne akcje** (`RemoteComputerViewModel` + `RemoteComputerWindow`; one per computer per connection window, key
`tools:remote:<DN>`): header (computer, DNS name, *Połączenie: WinRM, Kerberos jako CONTOSO\admin*), *Stan* (ping,
WinRM reachable, OS from CIM with UBR and last boot), *Zalogowani użytkownicy*, *Usługi* (filter *automatyczne,
zatrzymane*), actions *Odśwież zasady grupy*, *Uruchom ponownie…* (dialog `RestartComputerViewModel`: delay 0–1 440
min, default 10; message up to 500 characters, default text from the resources; *zamknij aplikacje bez pytania*; for a
server or DC a typed confirmation of the computer name), *Anuluj zaplanowane ponowne uruchomienie*, the tool launchers,
and the in-memory action log of the window.

**Entry points**: object list context menu and *Akcja* menu, submenu *Komputer* (`computer.remote`,
`computer.remoteDesktop`, `computer.ping`, `computer.manage`, `computer.eventViewer`, `computer.reset`) for selected
computers; command palette; the details pane of a computer shows *Zdalne akcje…*.

### 4.6 Lifecycle

* **Prestage** – the existing *Nowy → Komputer* wizard (`NewObjectViewModel`) gets *Użytkownik lub grupa, która może
  dołączyć ten komputer do domeny* (object picker; default: none = only administrators), *Zarządzany przez*,
  *Lokalizacja*. The plan of the one object: `Create` (as today: `WORKSTATION_TRUST_ACCOUNT`) then `ModifySecurity`
  adding for the chosen SID the ACEs ADUC writes: *Reset Password* (control access), read and write of the *Account
  Restrictions* property set, validated write to DNS host name, validated write to SPN (all allow, no inheritance).
  The ADUC option *Przypisz jako komputer sprzed Windows 2000* (a password derived from the name) is deliberately not
  offered – predictable machine passwords are a known attack path. The wizard's summary explains the domain join
  hardening of KB5020276 (2022–2023): a join that reuses an existing account succeeds when the joiner created it, when
  the joiner is a Domain/Enterprise Admin or built-in Administrator, when the account's owner is a member of those
  groups, or when the owner is trusted by the policy *Domain controller: Allow computer account re-use during domain
  join* (the Polish text uses the Windows Polish name). The owner will be the window's account; when that account is
  not an administrator and the chosen joiner is someone else, the summary says the join fails without that policy
  (pilot: confirm against a Windows Server 2025 DC).
* **Reset computer account** – new `ResetComputerAccountOperation` (Core/Batch/Operations.cs): a `ResetPassword` step
  with a 120-character random password from `PasswordGenerator`, generated at plan time, **never shown, copied,
  exported or written to a report** (`PlannedChange.Password` stays in memory, the preview shows the mask, the
  PowerShell export prompts as for users). Warning in the preview: *komputer straci relację zaufania; dołącz go ponownie
  albo napraw kanał (`Test-ComputerSecureChannel -Repair`)*. Not reversible. DCs are refused (`DomainControllerAccount`
  error).
* **Move, disable, delete** – the existing operations; K1 adds computer validations in `PlanValidation`: disabling,
  deleting or resetting a DC account is an error (*użyj obniżenia roli kontrolera*), moving a DC out of
  `OU=Domain Controllers` a warning; deleting a computer with children (BitLocker recovery information, Hyper-V /
  cluster objects) needs the subtree delete and warns that BitLocker recovery passwords are deleted with it.
* **Stale computers** – report only (4.3); hand-over to *Wyłącz…* / *Przenieś…* / *Usuń…* / *Edycja zbiorcza…*.

### 4.7 Remote actions over WinRM

**Library: `Microsoft.Management.Infrastructure` 3.0.0 (CIM over WS-Management).** Reasons:

1. **Typed, language-independent results.** CIM returns typed instances (`Win32_Service.State`, `Win32_Process`
   owners). The PowerShell route ends in parsing `quser`/`sc`/`gpupdate` text, which Polish Windows localizes.
2. **No script text, no injection surface.** Method parameters are typed (`Win32ShutdownTracker.Comment` carries the
   operator's message verbatim); the PowerShell SDK needs script blocks with parameters done right everywhere.
3. **Footprint and coupling.** One package (the same version PowerShell 7.6 itself depends on) against
   `System.Management.Automation` 7.6.x with ~16 dependencies (Newtonsoft.Json, ApplicationInsights telemetry,
   PowerShell.Native …) whose versions are tied to the .NET runtime (7.6 ↔ net10.0) and patched every few weeks.
4. **Credential and lifecycle in one object.** `CimCredential(PasswordAuthenticationMechanism.Kerberos, domain, user,
   SecureString)` on `WSManSessionOptions`; per-operation `CimOperationOptions.Timeout` and `CancellationToken`; no
   runspace pool or remote shell quota (`MaxShellsPerUser`).
5. **Same prerequisites.** Both use the WinRM service and its HTTP listener (5985, Kerberos message encryption); CIM
   does not even need the PowerShell session configuration (it works where JEA locks PowerShell remoting down).

Trade-offs accepted: process launches return no output (gpupdate completion is observed, below); refreshing the user
policy of logged-on users needs a scheduled task (below, behind a setting until the pilot). README's wording
"PowerShell Remoting" becomes "WinRM (WS-Management, Kerberos)" when K1 lands. `Microsoft.Management.Infrastructure`
4.0.0 on NuGet is an unlisted 2018 package – not used.

`Directory.Packages.props`, new group:

```xml
<ItemGroup Label="AD Manager – computers (K1)">
  <PackageVersion Include="Microsoft.Management.Infrastructure" Version="3.0.0" />
</ItemGroup>
```

**Contract** (Core/Computers/Remote): `IRemoteComputerClient.ConnectAsync(RemoteTarget target, IDirectorySession
session, RemoteOptions options, CancellationToken)` → `IRemoteComputerSession` (`IAsyncDisposable`) with
`GetOperatingSystemAsync`, `GetLoggedOnUsersAsync`, `GetServicesAsync`, `RefreshGroupPolicyAsync(GroupPolicyRefresh
target)`, `ScheduleRestartAsync(TimeSpan delay, string message, bool force)`, `AbortRestartAsync`. Errors:
`RemoteActionException(RemoteErrorKind)` – `Unreachable`, `WinRmNotAvailable`, `AuthenticationFailed` (Kerberos: no
SPN / clock skew / wrong account), `AccessDenied`, `Timeout`, `Cancelled`, `NotSupported` (not Windows, or no DNS
name), `Failed`; each with a Polish/English hint (*włącz WinRM zasadą grupy – docs/KOMPUTERY.md*). Messages never carry
the computer name or remote output into logs.

**Target and credential.** The target is `dNSHostName` from AD (validated as a host name; IP addresses refused – no
Kerberos). The credential is the window's: `SessionLogons.Of(session)` (AdMng.Directory, new public API by K1: the
Windows account, or user/domain plus a `SecureString` copy of the session's in-memory password; a DN simple bind has no
Windows logon → `NotSupported`). A Windows-account window creates the `CimSession` without a credential (process
token, Kerberos). Otherwise `AddDestinationCredentials(new CimCredential(Kerberos, domain, user, password))`; the
`SecureString` is disposed with the `CimSession`, which lives for one action. **Kerberos only** – no Negotiate/NTLM
fallback, so an admin credential is never handed to a host by NTLM. HTTPS (5986) is a setting; default HTTP with
Kerberos encryption.

**Timeouts and cancellation.** Connect/operation timeout 30 s (setting 5–300 s); every operation takes the window's
`CancellationToken` linked with the connection's lifetime (`ConnectionContext.Lifetime`), so closing the window stops
the calls. Nothing runs on the UI thread.

**Actions.**

| Action | CIM | Notes |
|---|---|---|
| System | `Win32_OperatingSystem` (`Caption`, `Version`, `BuildNumber`, `LastBootUpTime`), UBR via `StdRegProv.GetDWORDValue` `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion!UBR` | the patch level AD does not have |
| Logged-on users | `Win32_ComputerSystem.UserName` (console) + `Win32_Process WHERE Name='explorer.exe'` → `GetOwner` (domain, user), `SessionId`, `CreationDate` | console vs other sessions; disconnected RDP sessions still show (CIM has no session state – stated in the UI) |
| Services | `Win32_Service` `Name DisplayName State StartMode DelayedAutoStart StartName` | read only (start/stop is a non-goal of K1) |
| Refresh policy (computer) | `Win32_Process.Create` with the constant `gpupdate.exe /target:computer /force /wait:0`, then poll the process until it exits (≤ 10 min) and read `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\State\Machine\Extension-List\{00000000-0000-0000-0000-000000000000}` (end time, status) | result *zakończone o …* / *trwa*; no output captured |
| Refresh policy (users) | one-time scheduled task through `root/Microsoft/Windows/TaskScheduler` `PS_ScheduledTask` (`RegisterByXml`): principal group `S-1-5-4` (INTERACTIVE), action `gpupdate.exe /target:user /force`, start now + random 0–60 s, deleted after run – what `Invoke-GPUpdate` does | setting `RefreshUserPolicy`, off until the pilot confirms it |
| Restart | `Win32_OperatingSystem.Win32ShutdownTracker(Timeout = delay s, Comment = message, ReasonCode = 0x80000000 (planned, other), Flags = 2 reboot (+4 force))` | the message is shown to the logged-on users; always confirmed |
| Abort restart | `Win32_Process.Create` with the constant `shutdown.exe /a` | – |

**Rights** (all with the window's account on the target): WinRM itself admits local *Administrators* and *Remote
Management Users*; `root\cimv2` remote access, `GetOwner` of other users' processes, `Win32_Process.Create` and the
shutdown privilege need **local Administrators** (on DCs: Domain Admins). The window says so when it gets
`AccessDenied`. Prerequisite on the computers (K1's Polish doc `docs/KOMPUTERY.md`, with a GPO recipe): service
*Windows Remote Management* automatic, *Zezwalaj na zdalne zarządzanie serwerem przy użyciu usługi WinRM* (listener
filter), firewall rule *Windows Remote Management (HTTP-In)* limited to the admin subnet.

**Equivalents and audit.** Each action records an `AuditEntry` (`Action = "Remote.Restart"` etc., DN = the computer,
`Server` = its DNS name, the delay/forced flag as the new value; the message text is not recorded) and a PowerShell
equivalent in `PowerShellHistory` (new `RecordScript(title, server, script, domain)`, K1):
`$s = New-CimSession -ComputerName 'pc01.contoso.test' -Credential (Get-Credential) -Authentication Kerberos` followed
by `Get-CimInstance` / `Invoke-CimMethod -CimSession $s -ClassName Win32_OperatingSystem -MethodName
Win32ShutdownTracker -Arguments @{ Timeout = 600; Comment = '…'; ReasonCode = 2147483648; Flags = 2 }`.

### 4.8 Local tools

`ILocalToolLauncher.LaunchAsync(LocalTool tool, string host, IDirectorySession session)` (AdMng.Remote,
`WindowsLocalToolLauncher`):

* **Ping** – in-app (`IPingService`, `System.Net.NetworkInformation.Ping`, 4 echoes, 2 s each, cancellable); results in
  the remote window / details pane. No credential.
* **Pulpit zdalny** – `mstsc.exe /v:<dns name>`; **no credential is passed** (no `cmdkey`, nothing in Credential
  Manager – rule 2); mstsc uses the operator's saved credentials or asks.
* **Zarządzanie komputerem**, **Podgląd zdarzeń** – `mmc.exe compmgmt.msc /computer:<host>`, `eventvwr.exe <host>`.
  A Windows-account window starts them directly. Otherwise `CreateProcessWithLogonW(LOGON_NETCREDENTIALS_ONLY)` (what
  `runas /netonly` does): the local identity stays, network access uses the window's credential; the password goes in
  a zeroed unmanaged buffer; the child gets `__COMPAT_LAYER=RunAsInvoker` (mmc would otherwise ask for elevation, which
  `CreateProcessWithLogonW` cannot satisfy – remote management needs none). The credential then lives in the LSA logon
  session of that tool until the operator closes it – in memory, never on disk; the confirmation text says so.

## 5. K2 – Group Policy in the main tree

### 5.1 Model and reads

`GroupPolicyReader` (Core/GroupPolicy/Management) – everything on the session's DC:

| Read | Filter / attributes |
|---|---|
| GPOs | one level of `CN=Policies,CN=System,<domain>`, `(objectClass=groupPolicyContainer)`: `cn displayName versionNumber flags gPCFileSysPath gPCFunctionalityVersion gPCMachineExtensionNames gPCUserExtensionNames gPCWQLFilter whenCreated whenChanged nTSecurityDescriptor` (SD flags owner + DACL) |
| Links | domain head base (`gPLink gPOptions`), subtree `(&(objectClass=organizationalUnit)(|(gPLink=*)(gPOptions=*)))`, one level of `CN=Sites` `(objectClass=site)` `gPLink` – U4c's `GroupPolicyTopology` reads without the computer search (K2 adds `includeComputers: false`) |
| Links of one GPO | domain NC subtree `(&(|(objectClass=domainDNS)(objectClass=organizationalUnit))(gPLink=*{GUID}*))` + sites `(gPLink=*{GUID}*)` with the GPO's GUID in braces – substring through `LdapFilter` |
| WMI filters | one level of `CN=SOM,CN=WMIPolicy,CN=System,<domain>` `(objectClass=msWMI-Som)`: `msWMI-ID msWMI-Name msWMI-Parm1 msWMI-Parm2 msWMI-Author msWMI-CreationDate msWMI-ChangeDate` |
| `GPT.INI` versions | SYSVOL of the session's DC, one read per GPO, 4 in parallel, lazily for the list |
| PDC emulator | `fSMORoleOwner` of the domain head (`DomainControllerCatalog`) |

Model records: `GpoInfo` (id, DN, name, `GpoVersion` AD and SYSVOL, `GpoStatus` from `flags` – 0 all enabled, 1 user
disabled, 2 computer disabled, 3 all disabled –, extension names, WMI filter id, owner, DACL summary, created,
changed), `GpoLink` (container DN/kind, order, enforced, enabled), `WmiFilter` (id, name, description, queries
`(namespace, WQL)`, author, dates), `GpoPermission` (trustee SID, `GpoPermissionLevel`: `Read`, `Apply` (security
filtering), `Edit`, `EditDeleteModifySecurity`, `Custom`). `gPLink` writing: `GpLinkValue` (insert/remove/reorder/set
options; entries of other domains and unparseable entries kept byte for byte; new entries written as GPMC/Samba
write them, `[LDAP://cn={GUID},cn=policies,cn=system,<domain DN>;<options>]`; no link left → the attribute is cleared;
a value of a single space is read as empty).

### 5.2 Tree, documents and windows

**Tree** (K2 owns `Navigation/`): under the domain node, before its containers, three virtual nodes (kept by
`ContainerNodeViewModel.MergeChildren`, which leaves non-container nodes at the top): *Obiekty zasad grupy*
(`GroupPolicyObjectsNodeViewModel`, children `GpoNodeViewModel` sorted by name, lazily loaded), *Filtry WMI*
(`WmiFiltersNodeViewModel`) and *Lokacje* (`SitesNodeViewModel`, children `SiteNodeViewModel`). The setting
`GroupPolicy.ShowInTree` (default on; *Widok → Zasady grupy w drzewie*, `view.groupPolicyNodes`) hides them. Context
menus (`TreeMenu`): on the domain and OUs *Zasady grupy*, *Utwórz GPO w tej domenie i połącz je tutaj…*, *Połącz
istniejący GPO…*, *Zablokuj dziedziczenie*; on sites the same without block; on a GPO node *Stan GPO*, *Kopia
zapasowa…*, *Kopiuj*, *Zmień nazwę*, *Usuń*, *Porównaj z…*, *Historia w Watchdogu* (the existing
`WatchdogObjectHistoryLauncher` on the GPC DN).

**Documents** (`IShellDocument`, opened in the window's shell; templates in `MainWindow.xaml`):

* `GroupPolicyObjectsDocumentViewModel` – selecting *Obiekty zasad grupy*: all GPOs (name, status, WMI filter, links,
  versions AD/SYSVOL with a mismatch marker, owner, modified), multi-select; *Nowe…*, *Kopiuj…*, *Zmień nazwę*,
  *Usuń…*, *Kopia zapasowa…* (selection), *Przywróć z kopii zapasowej…*, *Importuj ustawienia…*, *Porównaj* (two
  selected).
* `GroupPolicyObjectDocumentViewModel` – selecting a GPO: tabs *Zakres* (links with container, enforced, enabled;
  *Filtrowanie zabezpieczeń* list with *Dodaj…/Usuń*; *Filtrowanie WMI* combo), *Szczegóły* (domain, owner, created,
  modified, versions user/computer for AD and SYSVOL, GUID, *Stan GPO* combo), *Ustawienia* (read-only list of the
  normalized settings from `GroupPolicy/Content` – "Komputer/Zabezpieczenia/…", "Komputer/Rejestr/…" – the Watchdog's
  keys; K3 replaces it with the ADMX-aware view), *Delegowanie* (trustees with GPMC levels; *Dodaj…*, *Zmień
  poziom*, *Usuń*), *Stan* (AD/SYSVOL consistency: folder present, versions, folder ACL consistent with the GPC DACL,
  extension names vs files present, leftovers of interrupted writes; *Wyrównaj wersje*, *Napraw uprawnienia folderu*).
* `GroupPolicyScopeDocumentViewModel` – the *Zasady grupy* of a domain, OU or site (tree menu, the object list toolbar
  button *Zasady grupy* for container locations, palette `gpo.containerPolicies`): *Połączone obiekty zasad grupy*
  (link order, GPO, enforced, link enabled, GPO status, WMI filter, modified; *Wyżej/Niżej*, *Wymuszone*, *Połączenie
  włączone*, *Usuń połączenie*, *Połącz istniejący…*, *Utwórz i połącz…*), *Zablokuj dziedziczenie* toggle (not for
  sites), *Dziedziczenie zasad grupy* (precedence from U4c's `GroupPolicyInheritance`: site links are shown for sites
  only, as GPMC; enforced and blocked markers; read-only).
* `WmiFiltersDocumentViewModel` – the filters, their linked GPOs, *Nowy…*, *Edytuj…*, *Usuń…*.

**Windows** (each `AddWindow`, opened from `DialogService`, in `SmokeTest` and a UI test): `NewGpoWindow` (name, optional
link target; also the copy dialog with *uprawnienia: domyślne / zachowaj istniejące*), `GpoBackupWindow` (backup:
choose GPOs and folder, comment; restore/import: choose folder, list of backups, target, options), `GpoComparisonWindow`,
`WmiFilterEditorWindow` (name, description, queries; *Sprawdź na komputerze…* runs the query through K1's
`IRemoteComputerClient` once K1 is merged), `ChangePlanPreviewWindow` (3.3).

**Warning bar** in every GPO document: *SYSVOL niedostępny* (the U4c reasons: DN logon, not supported, logon failure,
denied, unreachable) – write actions disabled with the reason, reads from LDAP still shown; *Zmiany GPO wykonujesz na
DC X, a emulatorem PDC jest Y – GPMC domyślnie używa PDC* with *Zmień kontroler…* (the existing Change DC command; the
window never switches DC by itself, rule 4).

**Permission awareness** (ADR-0006): *Nowe GPO* needs `groupPolicyContainer` in `allowedChildClassesEffective` of
`CN=Policies`; link actions need `gPLink` (block: `gPOptions`) in `allowedAttributesEffective` of the container; GPO
edits need write on the GPC; disabled commands carry the reason. SYSVOL rights cannot be read ahead – a refused write
fails the plan before its commit (3.3) and the old state stays.

### 5.3 Operations

Every operation is a planner method of `GroupPolicyPlanner` (Core) returning a sequential `ChangePlan` with semantic
PowerShell equivalents; Presentation previews and executes it (`ChangePlanPreviewWindow` → `IChangeCoordinator.
ExecutePlanAsync(plan, title, session)`).

| Operation | Steps (object: steps) | PowerShell |
|---|---|---|
| Create | GPC: `Create` (`objectClass=groupPolicyContainer`, `displayName`, `gPCFileSysPath=\\<domain>\SysVol\<domain>\Policies\{GUID}`, `gPCFunctionalityVersion=2`, `versionNumber=0`, `flags=0`; the DACL from the schema default), `CreateGpoFolder`, `SyncGpoFolderSecurity`; `CN=Machine`: `Create`; `CN=User`: `Create`; optional container: `Modify gPLink` | `New-GPO -Name … -Domain … -Server $server`; `New-GPLink -Guid … -Target … -LinkEnabled Yes` |
| Copy (within the domain) | as Create with a new GUID and the source's files (`WriteGpoFile` each, `GPT.INI` excluded), extension names and `flags`, the WMI filter link; versions: each half 1 when the source half is non-zero, else 0 (a new GUID has no client history); permissions default, or exactly the source's explicit ACEs (`ModifySecurity` with `AclChange.ReplacesExplicit`: the schema default entries the source lacks are removed, so its security filtering survives) | `Copy-GPO -SourceGuid … -TargetName … [-CopyAcl]` |
| Rename | GPC: `Modify displayName` (CAS); `GPT.INI` untouched (GPMC does not update it) | `Rename-GPO -Guid … -TargetName …` |
| Delete | every container linking it (domain NC and sites): `Modify gPLink` without the link; GPC: `Delete` (subtree: `CN=Machine`, `CN=User`), `DeleteGpoFolder`. The two default GPOs are refused. The dialog offers *Utwórz kopię zapasową przed usunięciem* (default on). A folder delete that fails leaves an orphaned folder, reported with *Usuń pozostały folder* | `Remove-GPO -Guid …` (removes the links of the domain); `Remove-GPLink -Guid … -Target <site DN>` for site links |
| Link / unlink / reorder / enforce / enable | container: one `Modify gPLink` (CAS on the whole value) | `New-GPLink` / `Remove-GPLink` / `Set-GPLink -Order` / `-Enforced Yes|No` / `-LinkEnabled Yes|No` |
| Block inheritance | domain or OU: `Modify gPOptions` (1 or cleared) | `Set-GPInheritance -Target … -IsBlocked Yes|No` |
| GPO status | GPC: `Modify flags` | `(Get-GPO -Guid …).GpoStatus = 'UserSettingsDisabled'` |
| Security filtering add / remove | GPC: `ModifySecurity` (Read + *Apply Group Policy*, container inherit), `SyncGpoFolderSecurity` | `Set-GPPermission -Guid … -TargetName … -TargetType Group|User|Computer -PermissionLevel GpoApply [-Replace]` |
| WMI filter link | GPC: `Modify gPCWQLFilter` = `[<domain DNS>;{FILTER-ID};0]` or cleared | `(Get-GPO -Guid …).WmiFilter = …` |
| WMI filter create / edit / delete | `msWMI-Som`: `Create` / `Modify` / `Delete`; a delete first clears `gPCWQLFilter` of the GPOs that use it | `New-ADObject -Type msWMI-Som …` / `Set-ADObject` / `Remove-ADObject` |
| Delegation | GPC: `ModifySecurity` (level ACEs, container inherit), `SyncGpoFolderSecurity` | `Set-GPPermission -PermissionLevel GpoRead|GpoEdit|GpoEditDeleteModifySecurity` |
| Restore / import | 5.5 | `Restore-GPO -BackupId … -Path …` / `Import-GPO -BackupId … -Path … -TargetGuid …` |

**Security filtering and MS16-072.** Since June 2016 clients read user-side GPOs in the computer's context. Removing
*Authenticated Users* from the security filtering therefore keeps (or adds) *Read* for Authenticated Users – as GPMC
does – and the preview warns (`Ms16072Read`) when neither Authenticated Users nor Domain Computers can read the GPO.

**GPMC permission levels** (`GpoPermissionLevels`; masks pinned by fixtures from GPMC in the pilot, until then): *Read*
= `GenericRead` (0x20094); *Apply* = Read + control access *Apply Group Policy*; *Edit settings* = Read + `WriteProperty`
+ `Self` + `CreateChild` + `DeleteChild` (0x200BF); *Edit settings, delete, modify security* = Edit + `DELETE` +
`WRITE_DAC` + `WRITE_OWNER` (0xF00BF). ACEs are container-inherit (they reach `CN=Machine` / `CN=User`). A trustee
whose ACEs match none of the levels shows as *Niestandardowe* and is only removed as a whole.

**WMI filter queries** (`msWMI-Parm2`): the number of queries, then per query its type, the lengths of namespace and
query, the language, the namespace and the query, each followed by `;` –

```
1;3;10;<length of the query>;WQL;root\CIMv2;SELECT * FROM Win32_OperatingSystem WHERE ProductType = 1;
```

– parsed and written by `WmiFilterQueries` (round-trip tests; fixtures from GPMC-made filters in the pilot). `msWMI-ID`
= `{GUID}`, dates in the `yyyyMMddHHmmss.ffffff-000` form, `msWMI-Author` = the window's account UPN.

### 5.4 Folder permissions

`GpoFolderAcl.FromDirectoryDacl(byte[] gpcDescriptor)` maps the GPC's DACL to the folder's DACL the way Samba's
`samba-tool gpo` does (`dsacl2fsacl`, which mirrors GPMC): the standard rights are kept; *read property + list
contents* → read & execute; *write property* → write data/append/attributes/EA; *create child* → add file/subfolder;
*delete child* → delete child; object ACEs (*Apply Group Policy*) are not file rights; every ACE becomes
object-and-container inherit; the DACL is protected. `SyncGpoFolderSecurity` writes it after every `ModifySecurity` of a
GPO and on create; the *Stan* tab compares the current folder DACL with the mapping (*Napraw uprawnienia folderu*). The
lab test compares the mapping with what `samba-tool gpo create` puts on its own folder (`smbcacls`); the pilot compares
with GPMC's *uprawnienia niespójne* check.

### 5.5 Backup, restore, import

**Format: GPMC-compatible**, because (1) operators already keep GPMC backups and a GPMC replacement must restore them;
(2) a backup must stay restorable with in-box tools (`Restore-GPO`, GPMC) when AD Manager is not at hand; (3) Microsoft's
security baselines (Security Compliance Toolkit) are distributed as GPMC backups, so *Importuj ustawienia* brings them
in for free (and K3/K4 build on it). An own format would win on simplicity only. AD Manager's extras go into a sidecar.

Layout written into the folder the operator picks (rule 1: an explicit export; nothing else written anywhere):

```
<folder>\manifest.xml                    (hidden, as GPMC: one BackupInst per backup in the folder; merged, not replaced)
<folder>\{BackupId}\Backup.xml           GroupPolicyBackupScheme: GroupPolicyCoreSettings (ID, Domain, SecurityDescriptor,
                                         DisplayName, Options, User/MachineVersionNumber, User/MachineExtensionGuids,
                                         WMIFilter), SecurityGroups (trustees of the DACL with SID and names),
                                         GroupPolicyExtension entries with FSObjectDir/FSObjectFile for every entry
<folder>\{BackupId}\bkupInfo.xml         GPOGuid, GPODomain, GPODomainGuid, GPODomainController, BackupTime, ID, Comment,
                                         GPODisplayName
<folder>\{BackupId}\DomainSysvol\GPO\…   Machine\…, User\…, root files except GPT.INI – byte for byte
<folder>\{BackupId}\admng.json           sidecar: AD Manager version, SHA-256 of every file, links of the GPO at backup
                                         time (container DN, order, options), WMI filter definition, flags
```

`gpreport.xml` (GPMC's settings report) is not written; the pilot confirms `Restore-GPO`/`Import-GPO` and GPMC accept a
backup without it (otherwise a minimal one is added). Files are mapped to GroupPolicyExtension entries by the
Watchdog's file classifier (Registry, Security, Scripts, GPP …); the pilot confirms GPMC restores every file of an AD
Manager backup. Until both pilot checks pass the backup window says *zgodna z GPMC – do potwierdzenia w pilotażu*.

**Reading a backup is reading untrusted input**: `XmlReader` with `DtdProcessing.Prohibit` and no resolver; every
`bkp:Location` / file path through `SysvolPaths.TryNormalize` (no `..`, no rooted paths) and confined to
`DomainSysvol\GPO`; the limits of 3.2; SHA-256 from the sidecar checked when present; GPP `cpassword` found → warning
(MS14-025).

* **Backup** (read-only for the directory; no plan): reads the GPC and the folder of each selected GPO into memory,
  writes the layout, merges `manifest.xml`; a failure removes what this backup wrote. Not audited as a change; the
  activity center shows it and the window offers the equivalent `Backup-GPO -Guid … -Path … -Comment …`.
* **Restore** (the GPO of the backup's GUID): existing GPO → plan: `WriteGpoFile`/`DeleteGpoFile` so that the folder
  equals the backup, `Modify` of `displayName`, `flags`, `gPCMachineExtensionNames`, `gPCUserExtensionNames`,
  `gPCWQLFilter` (when the filter exists), `ModifySecurity` to the backup's explicit ACEs, `SyncGpoFolderSecurity`,
  `SetGpoVersion` (+1 on each half from the current maximum). Deleted GPO → the create steps with the backup's GUID,
  content and exactly its explicit ACEs (version = 1 above the higher of the backup's and the deleted GPO's last
  `versionNumber` from Deleted Objects; without the Recycle Bin 1 + 1000 above the backup's, never back for clients); a
  leftover SYSVOL folder with the GUID refuses the plan. Links are not part of a GPMC restore; *Przywróć też połączenia
  (N)* from the sidecar is an option, off by default.
* **Import settings** (into a chosen GPO, as `Import-GPO`): content, extension names and the version bump only – no
  DACL, status or WMI link. Security principals and UNC paths of another domain found in `GptTmpl.inf` / GPP are listed
  as a warning; migration tables are a non-goal of K2.

### 5.6 Compare two GPOs

`GroupPolicyComparer` (Core/GroupPolicy/Comparison): core settings (status, versions, WMI filter, security filtering,
delegation, links) and content – the normalized settings of both GPOs from `GroupPolicy/Content` (the Watchdog's
parsers and `GpoSettingsDiffer`, moved to Core by K2 in one behaviour-neutral commit; the Watchdog uses them from
there), plus a file list (identical / different / only in A / only in B). `GpoComparisonWindow` shows *ustawienie: A |
B* with filters *tylko różnice*; masked values (passwords) are never shown; export to CSV only on demand. Both GPOs of
the window's domain (a GPO of another connection: later, through `IConnectionHandle` as the object comparison).

## 6. K3–K5 (design only)

**K3 – ADMX editor and central store.**

* Reads the central store `\\<session DC>\SYSVOL\<domain>\Policies\PolicyDefinitions` (the SYSVOL reader/writer get a
  second root, `PolicyDefinitions`, with the same path validation); without a central store the editor says so and
  offers to create it. ADMX/ADML parsed with hardened XML; languages `pl-PL` then `en-US` fallback.
* Model: categories, policies (class Machine/User/Both, key, value name, enabled/disabled values and lists, elements
  boolean/decimal/text/multiText/enum/list), *supportedOn*, explain text. Editor tree for computer and user, search,
  *tylko skonfigurowane*, *Włączone / Wyłączone / Nieskonfigurowane* with element editors.
* State from `Registry.pol` (`RegistryPolParser` moved by K2) and a new `RegistryPolWriter` (`PReg` v1, `**del.` /
  `**delvals.` semantics per ADMX rules). A save = one K2 plan: `WriteGpoFile Machine|User/Registry.pol`, `Modify
  gPC(Machine|User)ExtensionNames` with the Registry CSE `{35378EAC-683F-11D2-A89A-00C04FBBCFA2}` and the tool
  extension (`{0F6B957D-509E-11D1-A7CC-0000F87571E3}` machine, `{0F6B957E-…}` user), kept sorted as GPMC writes them,
  `SetGpoVersion` of that half. Acceptance: `Registry.pol` byte-identical to what the Microsoft editor writes for the same
  settings (fixtures from the pilot).
* *Zaktualizuj szablony*: a vendor catalogue (shipped with AD Manager) of package URLs and expected signers – Windows 11
  / Server 2025 (MSI, Microsoft), Edge (CAB/ZIP), Microsoft 365 Apps (EXE), Chrome (ZIP), Firefox (ZIP, GitHub
  releases), OneDrive, Adobe Acrobat/Reader, Zoom, FSLogix, Citrix Workspace, plus *own ADMX from a file*. Authenticode
  check (`WinVerifyTrust`, signer subject pinned per vendor); packages without a signature are accepted only from the
  pinned HTTPS URL and marked *niepodpisany przez producenta*. Vendor packages are not directory data; extraction may
  use a temporary folder of the operator profile, deleted right after. Diff shown before writing (new / changed /
  removed policies and files, namespace collisions such as old and new Edge templates); the previous central store is
  copied to an operator-chosen folder (or, by choice, a sibling `PolicyDefinitions.admng-<date>` on SYSVOL) before a
  plan of `WritePolicyDefinitionsFile` steps (same staging and compensation as 3.3, no version numbers).

**K4 – security settings.** Writers for `GptTmpl.inf` (`[Privilege Rights]`, `[Group Membership]`, `[System Access]`,
`[Event Audit]`, `[Service General Setting]`, `[Registry Values]`; UTF-16LE with BOM as secedit), GPP `Groups.xml`
(items with uid, item-level targeting kept), `audit.csv` (advanced audit) and firewall rules (registry values below
`SOFTWARE\Policies\Microsoft\WindowsFirewall`), all through K2 plans with the CSE/tool GUIDs GPMC writes (Security
`{827D319E-6EAC-11D2-A4EA-00C04F79F83A}` + `{803E14A0-B4FB-11D0-A0D0-00A0C90F574B}`, GPP Groups, Advanced Audit … –
fixtures from the pilot). Shared parsers with U4c (local rights) and the Watchdog. UI: local groups (Restricted Groups
and GPP side by side, with U4c's precedence explanation), user rights, advanced audit, services, firewall.

**K5 – "co dostanie komputer".** Input: a computer (or OU) and optionally a user (user configuration, loopback
merge/replace); site from the subnet (K1) or chosen. Precedence from U4c's `GroupPolicyInheritance`, security
filtering from `GroupPolicySecurityFilter` with the computer's token from the group graph, disabled links/status
excluded with the reason; WMI filters and GPP item-level targeting shown as *zależy od komputera* (optionally evaluated
live through K1's WinRM); the winning setting per key from the normalized settings with *dlaczego* (GPO, link,
enforced, order). In memory; HTML/CSV export on demand. No connection to the computer is required.

## 7. Per-connection lifetimes

| Service | Lifetime | Owner | Why |
|---|---|---|---|
| `IRemoteComputerClient` (`CimRemoteComputerClient`), `ILocalToolLauncher`, `IPingService`, `IHostAddressResolver` | Singleton | K1 | stateless; every call takes the window's session (its credential) |
| `WindowsReleaseCatalog` | Singleton | K1 | static data + settings overrides (shared settings) |
| `ComputerViewModelFactory` | **Scoped** | K1 | creates the review, remote and restart view models with the window's `DialogServices`, coordinator, audit log, history |
| `ISysvolFileWriter` (`WindowsSysvolFileWriter`) | Singleton | K2 | stateless like the reader; connections are per run and per session |
| `GroupPolicyPlanner`, `GroupPolicyReader`, `GpmcBackupReader/Writer`, `GroupPolicyComparer` | Singleton | K2 | pure / take the session per call |
| `GroupPolicyServices` (bundle for documents), `GroupPolicyViewModelFactory` | **Scoped** | K2 | the window's session manager, coordinator, dialogs, shell |
| `IBatchExecutor` | Scoped (unchanged) | – | gets the singleton writer as an optional constructor parameter |
| Window and document view models | created by the scoped factories | K1/K2 | closing a tab or window frees them |

New scoped types are added to `tests/AdMng.Presentation.Tests/Connections/ConnectionLifetimes.cs` (and App types to
the `PerConnectionApp` / `SharedApp` lists of `HostTests`); a scoped service that subscribes to the shared
`ISettingsStore.Changed` unsubscribes in `Dispose`. Remote windows and GPO documents use the window's
`ConnectionContext.Lifetime` for their work.

## 8. Data handling (rules 1 and 2 for every new piece)

* **Inventory, reports, DNS results, remote results, GPO lists, settings, file contents, comparisons** – in memory of
  the window only; CSV/HTML only when the operator chooses a file. Nothing in temp files, caches or logs.
* **Plans** hold in-memory copies of SYSVOL files and folder descriptors for verification and rollback (bounded,
  3.2); they die with the plan/result of the window. The audit log holds hashes and sizes, never content.
* **SYSVOL temp files** exist on the DC's SYSVOL – the directory's own store – only during a write.
* **GPO backups** go only to the folder the operator picks for that backup (rule 1's "files the operator explicitly
  exports"); the last used folder path may be remembered as a UI setting (a path, no content).
* **Settings** (new `ComputerSettings`, `GroupPolicySettings`): report parameters and enabled reports, placement rules
  (portable DNs), catalogue overrides (build, edition class, date), remote options (timeout, HTTPS, port,
  `RefreshUserPolicy`), restart defaults (delay, message text), tree visibility, backup defaults (comment template, last
  folder). No computer names, no GPO names, no results. No persisted name contains "password" (settings test).
* **Credentials**: the window's password reaches WinRM as a `SecureString` copy disposed with the CIM session,
  `CreateProcessWithLogonW` and `LogonUser` in zeroed unmanaged buffers; mstsc gets none; never `cmdkey`, never a command
  line, never an environment variable. The reset computer password exists only in the plan. Every new type that holds a
  credential overrides `ToString()`.
* **Diagnostic logs**: action kinds, counts, durations, error kinds – no computer names, GPO names, paths, users or
  remote output.

## 9. Localization and naming

All texts in `Strings.resx` and `Strings.pl.resx`, both languages, own prefixes: K1 `Computers.*`, `ComputerReview.*`,
`RemoteActions.*`, `WindowsRelease.*`; K2 `GroupPolicy.*`, `Gpo.*`, `GpoBackup.*`, `WmiFilter.*`; dynamic families
`ChangeAction.{new members}`, `Issue.{new codes}` from each owner. Command titles live in the owner's `L.Computers.cs` /
`L.GroupPolicy.cs` (not in `L.cs`). Polish terms follow the Polish GPMC/ADUC where they exist:

| English | Polski |
|---|---|
| Computer review | Przegląd komputerów |
| End of support | Koniec wsparcia |
| Remote actions | Zdalne akcje |
| Logged-on users | Zalogowani użytkownicy |
| Refresh Group Policy | Odśwież zasady grupy |
| Restart… / Cancel scheduled restart | Uruchom ponownie… / Anuluj zaplanowane ponowne uruchomienie |
| Remote Desktop / Computer Management / Event Viewer | Pulpit zdalny / Zarządzanie komputerem / Podgląd zdarzeń |
| Reset Account | Resetuj konto |
| The following user or group can join this computer to a domain | Użytkownik lub grupa, która może dołączyć ten komputer do domeny |
| Group Policy Objects | Obiekty zasad grupy |
| Group Policy (container view) | Zasady grupy |
| Linked Group Policy Objects / Group Policy Inheritance | Połączone obiekty zasad grupy / Dziedziczenie zasad grupy |
| Link Order / Enforced / Link Enabled | Kolejność łączy / Wymuszone / Połączenie włączone |
| Block Inheritance | Zablokuj dziedziczenie |
| GPO Status | Stan obiektu zasad grupy |
| Security Filtering / WMI Filtering / WMI Filters | Filtrowanie zabezpieczeń / Filtrowanie WMI / Filtry WMI |
| Delegation | Delegowanie |
| Back Up / Restore from Backup / Import Settings | Kopia zapasowa / Przywróć z kopii zapasowej / Importuj ustawienia |
| Sites | Lokacje |

## 10. Test plan

Polish data with diacritics throughout (descriptions, locations, GPO names such as *Stacje robocze – Zasady haseł*,
owners *Łukasz Wiśniewski*); computer names stay ASCII (NetBIOS).

**Core (Linux).**
* K1: `WindowsReleaseCatalog` (every row, editions, Windows 11 reported as "Windows 10", LTSC/IoT, server builds,
  unknown build, non-Windows strings, overrides, `ValidAsOf`), `ComputerClassifier` (UAC bits, RODC, primary groups),
  `ComputerInventory` with a fixed clock (Europe/Warsaw; 2026-10-27: Windows 11 Pro 24H2 ended two weeks ago, 23H2
  Enterprise ends in 14 days), `PlacementRules` (default computers container, DC OU, configured rules), `SubnetMap`
  (IPv4/IPv6 longest prefix, invalid `cn`), `DaclEditor`/`SecurityDescriptorWriter` (round trip with the parser,
  idempotent delta, canonical order, inherited ACEs untouched, inverse), `ComputerJoinRights` (the four ACEs), executor
  `ModifySecurity` on the fake (concurrent unrelated ACE kept), rollback and PowerShell of `ModifySecurity`,
  `ResetComputerAccountOperation` (password never in `ToString`, preview, PowerShell, report), computer validations.
* K2: `GptIni` (version halves, other lines and encodings kept, CRLF), `GpoVersion` (bump, max, never down),
  `GpLinkValue` (insert, remove, reorder, options, foreign and malformed entries kept, empty → cleared), every planner
  operation's steps and PowerShell (Polish names with apostrophes and typographic quotes), `GpoTransaction` on
  `FakeSysvol` (stage failure, verify conflict on a file and on `versionNumber`, switch failure → compensation, CAS
  failure → `GPT.INI` restored, leftovers cleaned), sequential execution and reverse rollback, rollback bumps versions,
  `GpoFolderAcl` (fixtures equal to Samba's mapping), `GpoPermissionLevels` (levels ↔ ACEs, custom), MS16-072 rule,
  `WmiFilterQueries` round trip, backup writer/reader round trip, hostile backups (traversal, absolute paths, DTD,
  oversized, missing files, bad hashes), `GroupPolicyComparer`; the moved content parsers keep every Watchdog test
  green unchanged except namespaces.

**AdMng.Remote.Tests (Linux).** CIM property-bag mapping (pure functions over dictionaries), `CimException` →
`RemoteErrorKind`, the constant command lines, logon name split (UPN / `DOMAIN\user`), host validation, the launcher's
argument building (`/v:`, `/computer:`), `NotSupported` off Windows. A Windows CI loopback test against the runner's
WinRM runs only when WinRM is enabled there and uses Negotiate through an internal test hook (the product path is
Kerberos-only); Kerberos is checked in the pilot.

**Presentation (Linux, fake directory).** `FakeDirectoryBuilder.AddComputerInventorySample()` (K1: DCs, RODC, servers
with and without owners, workstations of every catalogue row, prestaged and never joined, computers in `CN=Computers`
and in wrong OUs, sites and subnets, creators) and `AddGroupPolicyManagementSample()` (K2: GPOs with versions and child
containers, links on the domain head, OUs and a site, block inheritance, enforced and disabled links, WMI filters,
security filtering and delegated editors) with a writable `FakeSysvol` (K2; implements reader and writer, records every
call and the DC it went to, can fail any step).
* K1: `ComputerReviewViewModel` (tiles and lists per report, scopes, streaming and stop, exact last logon from several
  DCs, site resolution with `FakeHostAddressResolver`, CSV, actions through the pipeline with re-read rows, settings
  saved without names, session ended), `RemoteComputerViewModel` with `FakeRemoteComputerClient` (each action, typed
  confirmation for servers, delay and message passed verbatim, every `RemoteErrorKind` text in both languages,
  cancellation on window close, audit and PowerShell history entries), the launcher fake (no credential for mstsc),
  prestage in `NewObjectViewModel` (plan: create + ACEs), lifetimes.
* K2: tree nodes (Polish names, merge keeps them, setting hides them), the three documents (links, order, inheritance
  with block/enforced, versions mismatch, *Stan*), every operation from the UI to the preview to execution to the
  refreshed document, typed confirmation naming the domain for delete, backup/restore/import windows on a test-only
  temporary folder, comparison, WMI editor, delegation and security filtering (MS16-072 warning), PDC warning, SYSVOL
  unavailable (each reason) disables writes, lifetimes.

**Lab (Samba 4.19, `tools/testlab`).** The lab is shared: K1 objects under `OU=K1T-<run>` and subnets `10.250.<n>.0/24`
named `K1T-…`; K2 GPOs named `K2T-<run> …` linked only to `OU=K2T-<run>`; everything removed at the end (also on
failure); never reset the lab.
* K1: inventory of the 250 lab computers equals an `ldapsearch` of the same attributes (the Linux stand-in for
  `Get-ADComputer`); exact last logon on the lab DC; subnet → site; prestage with the join right for
  `helpdesk.test` and the ACEs read back; reset computer account; disable, move, delete in the run's OU.
* K2 (collection `SambaTool`, serial): create through the pipeline with `SmbClientSysvolFileWriter`, then `samba-tool gpo
  show` / `listall`, `GPT.INI` via smbclient, folder DACL equal to the DACL `samba-tool gpo create` gives its own GPO;
  link / enforce / reorder / block → `samba-tool gpo getlink` / `getinheritance`; status, WMI filter link, security
  filtering; copy; backup → restore into the same GPO and as a deleted GPO; import; delete removes links, container and
  folder; a `versionNumber` changed behind the plan's back → conflict and no file changed; an interrupted write (the fake
  hook in the writer) → compensation leaves the folder as before. Optional with the Watchdog lab: a GPO changed in AD
  Manager gives one `GroupPolicyChanged` + `GroupPolicySettingsChanged` attributed to the lab administrator.

**UI tests (Windows CI)** – K1: *Narzędzia → Przegląd komputerów* → run on the fake → tile → list → details; remote
window with the fake client (restart dialog with typed confirmation); the prestage field in *Nowy komputer*; the
*Komputer* submenu. K2: tree nodes and their menus, the three documents, `NewGpoWindow`, `GpoBackupWindow`,
`GpoComparisonWindow`, `WmiFilterEditorWindow`, `ChangePlanPreviewWindow` with SYSVOL rows; the UI test host replaces
the writer with `FakeSysvol` and the remote client/launcher with fakes. Every new window in `SmokeTest`.

**Screenshots** (`ScreenshotTests`): `22-computers.png` – *Przegląd komputerów* on the Polish sample with the
end-of-support tile selected; `23-group-policy.png` – the main window with the *Zasady grupy* tab of `OU=Komputery`
(links, an enforced link, inheritance) and the *Obiekty zasad grupy* node expanded.

## 11. File ownership split

Two implementers, two branches from `feature/k-design`: **K1** (`feature/k1-computers`) and **K2**
(`feature/k2-group-policy`). Each owns its new files outright. Shared files get only the changes listed below, at the
named anchors, so that the merges are mechanical; any other change to a shared file needs the other owner's agreement
first (written into the "as built" section of this note).

### 11.1 New files (exclusive)

| K1 | K2 |
|---|---|
| `src/AdMng.Core/Computers/**`, `src/AdMng.Core/Security/{AclChange,DaclEditor,SecurityDescriptorWriter,ExtendedRightIds}.cs`, `src/AdMng.Core/Batch/BatchExecutor.Security.cs` | `src/AdMng.Core/GroupPolicy/{ISysvolFileWriter,GptIni,GpoVersion,GpLinkValue}.cs`, `GroupPolicy/Management/**`, `GroupPolicy/Content/**` (the move), `GroupPolicy/Backup/**`, `GroupPolicy/Comparison/**`, `src/AdMng.Core/Batch/{BatchExecutor.Sysvol,RollbackPlanner.Sysvol,PowerShellEquivalent}.cs` |
| `src/AdMng.Directory/SessionLogons.cs` | `src/AdMng.Directory/WindowsSysvolFileWriter.cs` (+ the shared logon helper refactor inside AdMng.Directory) |
| `src/AdMng.Remote/**`, `tests/AdMng.Remote.Tests/**` | – |
| `src/AdMng.Presentation/Computers/**`, `Settings/ComputerSettings.cs`, `Localization/L.Computers.cs`, `Shell/ShellViewModel.Computers.cs` | `src/AdMng.Presentation/GroupPolicy/**`, `Navigation/GroupPolicyNodes.cs`, `Changes/ChangePlanPreviewViewModel.cs`, `Settings/GroupPolicySettings.cs`, `Localization/L.GroupPolicy.cs`, `Shell/ShellViewModel.GroupPolicy.cs` |
| `src/AdMng.App/Computers/**` (windows, `ComputersAppRegistration.cs`), `Services/DialogService.Computers.cs`, `Services/ToolWindows.Computers.cs` | `src/AdMng.App/GroupPolicy/**` (documents, windows, `GroupPolicyAppRegistration.cs`), `Views/ChangePlanPreviewWindow.xaml(.cs)`, `Services/DialogService.GroupPolicy.cs`, `Services/ToolWindows.GroupPolicy.cs` |
| `tests/AdMng.Testing/{FakeDirectoryBuilder.Computers,FakeRemoteComputerClient,FakeLocalToolLauncher,FakeHostAddressResolver}.cs` | `tests/AdMng.Testing/FakeSysvol.cs` (and `FakeSysvolFileReader.cs`, `FakeDirectoryBuilder.GroupPolicy.cs`, which K2 owns from now on) |
| tests in `Computers/` folders of Core.Tests, Presentation.Tests, App.UiTests; `IntegrationTests/Integration/ComputerLabTests.cs` | tests in `GroupPolicy/` folders; `IntegrationTests/Integration/GroupPolicyManagementLabTests.cs`; `Infrastructure/SambaTools.cs` and `SmbClientSysvolFileWriter` |
| `docs/KOMPUTERY.md` (Polish: reports, lifecycle, remote actions, WinRM by GPO) | `docs/ZASADY-GRUPY.md` (Polish: GPO tree, operations, backups, PDC note) |

Owned existing areas: K2 owns `src/AdMng.Presentation/Navigation/**`, `src/AdMng.App/Views/TreeMenu.cs`,
`src/AdMng.Core/GroupPolicy/**`, the Watchdog files touched by the parser move. K1 owns
`src/AdMng.Presentation/PropertySheets/PropertyPageFactory.cs` (computer pages), `Dialogs/NewObjectViewModel.cs` and
`Views/NewObjectWindow.xaml`, `src/AdMng.Core/Batch/{Operations,ChangePlanner}.cs` and `Batch/Planning/**`,
`tests/AdMng.Testing/FakeDirectorySession.cs`.

### 11.2 Shared files – additive changes at fixed anchors

| File | K1 adds | K2 adds |
|---|---|---|
| `Directory.Packages.props` | the K1 `ItemGroup` (4.7) after the last `ItemGroup` | nothing |
| `AdMng.slnx` | `AdMng.Remote`, `AdMng.Remote.Tests` | nothing |
| `src/AdMng.Core/Batch/ChangePlan.cs` | `ChangeAction.ModifySecurity` after `Restore`; `PlannedChange.Acl` after `DeleteSubtree`; issue codes after `CannotCreateChild` | SYSVOL `ChangeAction` members after `Delete`; `PlannedChange.Sysvol`, `.GpoVersion` after `Password`, and the `ToString` change; `ObjectPlan.PowerShell` after `SourceRow`; `ChangePlan.Sequential` after `Server`; issue codes after `TombstoneReanimation` |
| `src/AdMng.Core/Batch/BatchExecutor.cs` | `partial` keyword (identical on both sides); in `ExecuteStepAsync` one `case ChangeAction.ModifySecurity` after the `Restore` case; its audit values in `Audit` after the `Restore` case | `partial`; in `ExecuteStepAsync` the SYSVOL cases before the `Modify` case; in `Audit` before the `Modify` case; the optional `ISysvolFileWriter?` constructor parameter; `Sequential` in `Run` |
| `src/AdMng.Core/Batch/RollbackPlanner.cs` | the `ModifySecurity` case after the `Restore` case | `partial`; SYSVOL cases before the `Modify` case; reverse order for sequential plans |
| `src/AdMng.Core/Batch/PowerShellExporter.cs` | `ModifySecurity` in `WriteChange` after `Restore` | `ObjectPlan.PowerShell` in `Export`, SYSVOL comments before `Modify` in `WriteChange`, `#Requires -Modules GroupPolicy` in `WriteHeader` |
| `src/AdMng.Core/Directory/Search.cs` | `ModifyOptions.SecurityDescriptorFlags` | nothing |
| `src/AdMng.Directory/LdapDirectorySession.cs` | SD flags control on modify | nothing |
| `src/AdMng.Core/Model/ObjectKind.cs`, `Presentation/Icons/ObjectIcons.cs` + the icon resources of the App | `ObjectIcon.Server`, `ObjectIcon.DomainController` after `ComputerDisabled` | `ObjectKind.GroupPolicyContainer`, `WmiFilter`, `Site` after `BitLockerRecovery`; GPO/WMI/site icons after `Watchdog` |
| `src/AdMng.Presentation/BatchEditing/PreviewModels.cs` | `ModifySecurity` case after the `Restore` case | SYSVOL cases after the `ResetPassword` case (before `default`) |
| `src/AdMng.Presentation/Changes/PowerShellHistory.cs` | `RecordScript(...)` | nothing |
| `src/AdMng.Presentation/ServiceCollectionExtensions.cs` | `services.AddAdMngComputers();` after the `FilterLibrary` line | `services.AddAdMngGroupPolicy();` after the `DirectoryAclGrantSource` line |
| `src/AdMng.Presentation/Settings/AppSettings.cs` | `ComputerSettings Computers` after `AccountReview` | `GroupPolicySettings GroupPolicy` after `Comparison` |
| `src/AdMng.Presentation/Settings/SettingsViewModel.cs` + `src/AdMng.App/Views/SettingsWindow.xaml` | a sub view model `Computers` (as `Messages`) built in the constructor and saved in `SaveAsync`; tab *Komputery* after the *Wiadomości* tab | nothing: K2's few settings are changed where they are used (*Widok → Zasady grupy w drzewie*, the backup window) |
| `src/AdMng.Presentation/Shell/ShellViewModel.Palette.cs` | `.. ComputerCommandDescriptors(),` after the `object.compareWithConnection` descriptor | `.. GroupPolicyCommandDescriptors(),` after `tools.watchdog` |
| `src/AdMng.Presentation/Resources/Strings.resx`, `Strings.pl.resx` | one block right after the last `AccountStatus.*` entry | one block at the end of the file |
| `src/AdMng.App/AppHost.cs` | `services.AddAdMngComputersApp();` after the `AccountReviewWindow` registration | `services.AddAdMngGroupPolicyApp();` after the `ISysvolFileReader` registration |
| `src/AdMng.App/Services/DialogService.cs`, `ToolWindows.cs` | `partial` keyword only (identical) | `partial` keyword only (identical) |
| `src/AdMng.App/Views/MainWindow.xaml` | *Narzędzia*: `tools.computerReview` after `tools.accountReview`; *Akcja*: submenu *Komputer* after `object.resetPassword` | tree templates after the `PlaceholderNodeViewModel` template; document templates after both `WatchdogDocumentViewModel` templates; *Akcja*: submenu *Zasady grupy* after `object.compareWithConnection`; *Widok*: `view.groupPolicyNodes` toggle after `view.details` |
| `src/AdMng.App/Views/ObjectListView.xaml` | context submenu *Komputer* after `object.resetPassword` | `gpo.containerPolicies` after `object.move`; toolbar button *Zasady grupy* |
| `src/AdMng.App/SmokeTest.cs` | `RenderComputerWindows(...)` call before the `RenderObjectDialogs` line + the method at the end of the class | `await RenderGroupPolicyAsync(main)` after the `RenderWatchdogTabAsync` line + the method after `RenderWatchdogTabAsync` |
| `tests/AdMng.App.UiTests/ScreenshotTests.cs` | `22-computers.png` after the `20-two-connections` entry | `23-group-policy.png` after the `21-compare-objects` entry |
| `tests/AdMng.App.UiTests/Infrastructure/UiTestContext.cs` | fake remote client/launcher/resolver at the end of the service set-up | `FakeSysvol` as writer next to the existing SYSVOL reader replacement |
| `tests/AdMng.Presentation.Tests/Connections/ConnectionLifetimes.cs` (+ `PerConnectionApp` / `SharedApp` in `tests/AdMng.App.UiTests/HostTests.cs`) | per-connection after `WatchdogObjectHistoryLauncher`, shared after `ConnectionRegistry` | per-connection after `DialogServices`, shared after `FilterLibrary` |
| `README.md` | the K1 line of *Etapy* and *Znane ograniczenia* entries | the K2 line of *Etapy* and its entries |
| `docs/ARCHITECTURE.md` | a paragraph *Komputery (K1)* | a paragraph *Zasady grupy (K2)* and the rule-5 note (SYSVOL in the pipeline) |
| this note | section 14 *K1 as built* | section 15 *K2 as built* |

`L.cs` is not changed by either. `CLAUDE.md` is not changed by either: the extension of rule 5 to SYSVOL is recorded
here and in `docs/ARCHITECTURE.md`; its wording in CLAUDE.md is the user's decision. Other tracks (U1c, U1d) change
`Strings*.resx`, `README.md` and Watchdog files at the same time; their conflicts are additive as well. K2 makes the
content parser move (Watchdog.Core → `AdMng.Core/GroupPolicy/Content`) its first, isolated, behaviour-neutral commit so
that it can be rebased over concurrent Watchdog work without touching anything else.

### 11.3 Milestones and the one dependency

| K1 | K2 |
|---|---|
| **K1.1** ACL writes (3.1) – tag `k1.1-acl` as soon as it is green | **K2.1** SYSVOL writer, pipeline extensions, transaction, versions, preview window, content parser move – tag `k2.1-sysvol` |
| K1.2 inventory, catalogue, reports, review window, CSV, site/subnet | K2.2 reads, tree nodes, the three documents (read-only) |
| K1.3 lifecycle: prestage (uses K1.1), reset, validations | K2.3 create, copy, rename, delete, links, block, status, WMI filters |
| K1.4 remote actions and tools (`AdMng.Remote`), property sheet page | K2.4 security filtering, delegation, folder ACL sync – **needs `k1.1-acl`** (`git merge k1.1-acl`, never cherry-pick) |
| K1.5 docs, UI tests, smoke, screenshot 22, lab | K2.5 backup/restore/import, compare, docs, UI tests, smoke, screenshot 23, lab |

K1 never needs K2. If `k1.1-acl` is late, K2 does K2.5 before K2.4. Both branches merge `feature/k-design`'s later
corrections (if any) the same way. Whoever finishes second merges the first branch and resolves the additive conflicts
of 11.2 by keeping both sides; any other conflict means a rule of this section was broken.

## 12. Open points

**To confirm in the pilot (Windows Server 2025 DC, Windows 11 24H2/25H2 clients):** GPMC restores an AD Manager backup
and AD Manager restores GPMC backups (with and without `gpreport.xml`); GPMC shows no *uprawnienia niespójne* after
AD Manager's ACL changes; the GPMC permission-level masks and WMI `msWMI-Parm2` fixtures; a GPO created and changed in
AD Manager applies like one made in GPMC (`gpresult /h`); versions and `Registry.pol` (K3) equal GPMC's; the KB5020276
reuse rules for prestaged accounts; `Win32ShutdownTracker` with message and delay as a non-elevated remote admin; the
user policy refresh task (`RefreshUserPolicy`); Kerberos-only CIM with an explicit credential of another account;
`CreateProcessWithLogonW` + `RunAsInvoker` for mmc; inventory equal to `Get-ADComputer`.

**Questions for the user (defaults are used until answered; none blocks K1/K2):**

1. Should the *Zasady grupy* documents offer to switch the window to the PDC emulator automatically when a GPO change
   starts on another DC? Default: a warning with a *Zmień kontroler…* button, no automatic switch.
2. Default restart delay and message text for *Uruchom ponownie…*? Default: 10 minutes, *Komputer zostanie ponownie
   uruchomiony przez dział IT za {minuty} min. Zapisz swoją pracę.*
3. Should remote service start/stop/restart come right after K1 (now read-only)? Default: later.
4. Should backups also be offered to a SYSVOL folder (replicated, visible to other admins) besides a local folder?
   Default: local/UNC folder chosen by the operator only.
5. End-of-support warning horizon? Default: 90 days.

## 13. Non-goals (K1/K2)

Starter GPOs; migration tables and cross-domain copy; GPO links to sites of other forests; Group Policy modelling and
results (K5); editing GPO settings (K3/K4); remote service start/stop, remote software inventory, Wake-on-LAN; LAPS
password reading; automatic disabling or deletion of stale computers; storing any history of remote actions beyond the
window's session log.

## 14. K1 as built

(Filled in by the K1 implementer.)

## 15. K2 as built

Branch `feature/k2-gpo` (not `feature/k2-group-policy`), from `feature/k-design`, with `k1.1-acl` merged (never
cherry-picked) before K2.4. Commits in order: the content parser move (`104f625`), K2.1 in two parts (`87d24ee`,
`bf7958b` – tagged `k2.1-sysvol`), the K1.1 merge, Core (`134ddad`), lab (`e7985d9`), Presentation (`2b4f090`), App
(`7d3438b`), then documentation and the lab/backup follow-ups. Everything of sections 3.2–3.4 and 5 is implemented
unless listed below.

**Deviations and additions**

* **`GpoSettingsDiffer` stayed in the Watchdog.** Its output is the Watchdog event contract (`AttributeChange` rows of
  `AdMng.Watchdog.Contracts`), which `AdMng.Core` cannot reference. The parsers, the setting model, the secret rules and
  the file classifier moved (`AdMng.Core/GroupPolicy/Content`, `GpoContentParser` replaces the per-file switch of
  `GpoSnapshotBuilder`); AD Manager's own diff is `GpoFileDiff` (preview) and `GroupPolicyComparer` (comparison).
  Fallout fixed in the Watchdog tests: `tests/AdMng.Watchdog.Core.Tests/Gpo/SmallParserTests.cs` aliases `GpoVersion`
  (ambiguous between `AdMng.Core.GroupPolicy` and `AdMng.Watchdog.Core.Gpo`).
* **Shared files beyond 11.2** (to be kept when K1 merges): `Shell/ShellViewModel.cs` has three one-line hooks –
  `AttachGroupPolicy()` after the `Tree.PropertyChanged` subscription, `DetachGroupPolicy()` in `Dispose`, and
  `if (TryActivateGroupPolicyNode(node)) { return; }` at the top of `OnNodeActivated`; `Shell/ShellViewModel.Commands.cs`
  has `.. GroupPolicyStateCommands,` right after `ChangeDomainControllerCommand,` (not next to K1's line);
  `NavigationTreeViewModel` (K2-owned) takes an optional `GroupPolicyViewModelFactory`, so no other constructor changed.
  `tests/AdMng.App.UiTests/XamlSourceTests.cs` now reads the palette command ids from every `ShellViewModel*.cs`
  partial (it read only `ShellViewModel.Palette.cs`; this also covers K1's `computer.*` ids).
* **SmokeTest**: `RenderGroupPolicyAsync(main, services)` (the windows need the connection's services), after
  `RenderWatchdogTabAsync`; it renders the four documents disconnected and every GPO window from `GroupPolicySamples`
  (App, sample data only, never written anywhere).
* **SYSVOL unavailable** disables only operations that write SYSVOL (create, copy, delete, delete leftover folder,
  security filtering, delegation, restore, import, align versions, repair folder permissions). Link, unlink, order,
  enforce, enable, block inheritance, GPO status, rename and the WMI filter link and filters change AD only and stay
  enabled. Each reason has its own banner text; reads from LDAP are always shown.
* **Permission awareness (5.2) is not implemented**: commands are enabled by session and SYSVOL state only, not by
  `allowedChildClassesEffective` / `allowedAttributesEffective`. A refused write fails in the preview's results; SYSVOL
  changes are compensated (3.3), LDAP steps already executed are undone with *Cofnij*. Follow-up item.
* **Link existing GPO** uses a single-select picker (one link per plan, GPOs already linked to the target are not
  offered). *Porównaj z…* uses the same picker.
* **Backup** runs as an activity of the activity center with one synthetic `ObjectResult` per GPO (no `BatchResult`,
  not audited, as designed); the window remembers the folder (`GroupPolicySettings.LastBackupFolder`), a comment
  template (`BackupCommentTemplate`, `{0}` = local time) and the *backup before delete* choice. Nothing else of K2 is in
  the settings (`ShowInTree`).
* **GPP passwords**: restore and import plans carry the warning `IssueCodes.GpoGppPassword` (MS14-025) naming the
  preference files that hold a non-empty `cpassword` – paths only, never the value.
* **WMI filter editor**: *Sprawdź na komputerze…* is not there yet – it needs K1's `IRemoteComputerClient`; the editor
  shows a note (`GroupPolicy.WmiTestLater`). To add after the K1 merge.
* **Shell target of the Group Policy commands** (`gpo.containerPolicies`, `gpo.newLinked`, `gpo.linkExisting`,
  `gpo.blockInheritance`): the command parameter (tree menu: a DN, a container or site node, a container row), else a
  single selected OU or domain row of the list, else the current list's container, else the selected tree container;
  only a domain head, an OU or a site qualifies.
* **Tree menus** also have *Otwórz* (GPO node, bold), *Nowe…* / *Przywróć z kopii zapasowej…* / *Odśwież* on *Obiekty
  zasad grupy*, *Nowy…* on *Filtry WMI* and *Odśwież* on *Lokacje*.
* **`GPT.INI` versions of the list** are read when the list loads (4 in parallel), not lazily per visible row.
* **UI test host**: `UiTestContext` replaces `ISysvolFileWriter` with its `FakeSysvol` (`context.Sysvol`); the
  `ISysvolFileReader` replacement (U4c) stays the empty `FakeSysvolFileReader`. The GPO views read SYSVOL through the
  write connection. `HostTests`: `IGroupPolicyDialogs` per connection, `ISysvolFileWriter` shared.
* **Lab**: the interrupted write is tested on the lab too (`SmbClientSysvolFileWriter.FailReplace(n)` fails the n-th
  replace before it reaches the server); the optional Watchdog scenario (one `GroupPolicyChanged` +
  `GroupPolicySettingsChanged` per AD Manager change) was not run.

**Review fixes** (commits *K2 review 1–14*):

* Copy with permissions and restore of a deleted GPO set exactly the source's explicit ACEs
  (`AclChange.ReplacesExplicit`; the executor removes the explicit ACEs it reads that the source lacks and audits the
  applied delta) – the schema default's Authenticated Users Apply no longer undoes security filtering.
* Settings kept as directory objects below `CN=Machine`/`CN=User` (software installation, wireless/wired policies) are
  counted (`GroupPolicyReader.CountDirectorySettingsAsync`, sidecar `directoryObjects`, GPMC `DSObject` entries) and
  warned about (`GpoDirectorySettings`) on copy, backup, restore and import; they are still not carried.
* Rollback drops inverse `gPLink` / `gPCWQLFilter` values that name a GPO or WMI filter the batch deleted.
* Security filtering lists every trustee allowed Apply (`GpoPermission.Applies`), whatever its level; a delegation
  level change keeps the Apply ACE.
* A sequential plan with an error on any object runs nothing (`ChangePlan.IsBlocked`).
* Missing GPO folder = deleted; *Usuń pozostały folder* is refused while the GPO exists, also at execution
  (`SysvolChange.OnlyIfContainerMissing`); restoring a deleted GPO over a leftover folder is refused.
* *Pokaż różnice* never shows an unparseable preferences file or a file naming `cpassword` as text.
* `manifest.xml` is written to a temporary file and moved over the old one, merges serialized in the process.
* Delete warns in a forest with other domains (`GpoLinkedElsewhere`): `gPLink` is not in the GC, their links are found
  only on their DCs (rule 4).
* Dedicated issue codes (`GpoWmiFilterMissing`, `GpoBackupOtherDomain`, `GpoExists`, `GpoFolderExists`,
  `GpoPermissionsReplaced`, `GpoVersionJump`) and `ValidationIssue.Arguments` for the `{0}` placeholders of the
  localized texts.

**Tests.** Core: versions, `GPT.INI`, `gPLink`, folder ACL mapping, transaction and compensation, SYSVOL pipeline
(executor, rollback, PowerShell, audit), reader, every planner operation on the fake directory + `FakeSysvol`, GPMC
backups (layout, round trip, hostile input, restore, import, GPP passwords), comparison. Presentation: documents, every
operation from the command through the preview to the fake and the refreshed document, typed delete confirmation,
backup/restore/import on a test-only temporary folder, comparison, WMI editor, delegation and security filtering
(MS16-072), PDC note, SYSVOL unavailable per reason, tree and shell, settings, lifetimes. App (Windows CI only): every
window registered and rendered, tree nodes and menus, documents in the main window, enforce through the real preview
window, new linked GPO through the dialogs, backup and comparison windows, the GPO picker (exclusions, filter, choice),
preview diff; screenshot
`23-group-policy.png`. Lab (`SambaTool` collection): see 10; all objects `K2T-<run>` in their own OU, removed at the end.
The Watchdog's lab tests of GPO content (`GpoContentLabTests`, through the moved parsers) pass unchanged; on the shared
lab, Watchdog and directory lab tests can fail with client-side LDAP timeouts while another suite holds Samba's single
write transaction, and the Kerberos ones need the DC's DNS name in `/etc/hosts` (tools/testlab/README.md) – a rerun on
a quiet lab is green.

**Not verified here (Windows only):** the WPF windows and views, the UI tests, the smoke test and screenshot 23 (CI on
Windows), `WindowsSysvolFileWriter` against a Windows DC, the folder picker (`OpenFolderDialog`), and every pilot item of
12 (GPMC compatibility of backups in both directions, GPMC permission masks, `msWMI-Parm2` fixtures, `gpresult /h`).
