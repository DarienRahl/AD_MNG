# K – Computers and Group Policy (GPO) – design note

Roadmap track K (README, "Ścieżka K – Komputery i zasady grupy (GPO)"). This note is **binding** for the K1 and K2
implementers, who build in parallel from it (section 11 splits the files between them), and it is the architecture
sketch for K3–K5. Status: K1 and K2 built and merged (`feature/k12`, October 2026; sections 14–16 say how they
were built and what is still open); K3–K5 design only.

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
  the delta is re-applied on the fresh DACL, so concurrent unrelated ACL changes are kept. Audit and rollback use the
  part of the delta that changed the DACL at execution (`ChangeResult.AppliedAcl`): an ACE another administrator added
  meanwhile is neither claimed nor removed. Rollback = the inverse of that (added ↔ removed). PowerShell: `$acl = Get-Acl -Path "AD:$dn"`, one `ActiveDirectoryAccessRule` per ACE with
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
  group 516/521 as a second signal), `operatingSystem` containing "Server" (or "Azure Stack HCI" / "Azure Local") →
  server, a Windows client string or build → workstation, no `operatingSystem` → unknown (prestaged and never joined, a
  cluster name object, or a non-Windows system that set nothing).
  Mapping to U4c's `ComputerRole` for shared code; `ComputerRole` itself is not changed.
* `ComputerFacts`: DN, name, `dNSHostName`, `sAMAccountName`, SID, type, enabled, `operatingSystem`,
  `operatingSystemVersion` (`"10.0 (26100)"` → major, minor, build), `operatingSystemServicePack`,
  `lastLogonTimestamp`, `pwdLastSet`, `whenCreated`, `managedBy`, `mS-DS-CreatorSID`, `description`, `location`,
  parent DN, the LAPS expiry time when the schema has `msLAPS-PasswordExpirationTime` or
  `ms-Mcs-AdmPwdExpirationTime` (presence only – the password is never read).
* `WindowsReleaseCatalog` (built-in table, `ValidAsOf` date) → `WindowsReleaseMatch(Release?, EditionClass,
  EndOfSupport?, SupportState)`. The build decides the release – **not** the product string: early Windows 11 builds
  wrote "Windows 10 …" into `operatingSystem`, so build ≥ 22000 on a client string is Windows 11. The product string
  decides the family (Server) and the edition class (`HomePro`, `EnterpriseEducation`, `Ltsc`, `IotLtsc`, `Server`,
  `AzureLocal`), because end dates differ by edition. Strings without "Windows" that are Windows Server based count as
  Windows: "Hyper-V Server" (class `Server`, the dates of the Server release of its build) and "Azure Stack HCI" /
  "Azure Local" (class `AzureLocal`, its own end of service per version: 20H2 2022-12-13, 21H2 2023-11-14, 22H2
  2025-05-31; 23H2 and 24H2 per solution version, so no date). `SupportState`: `Supported`, `EndingSoon` (≤ N days, default 90), `Ended`,
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
| *Wyłącz…*, *Przenieś…*, *Usuń…*, *Resetuj konto…* | 4.6 (batch preview above the threshold, typed confirmation naming the domain; the reset never opens the batch editor – it would offer the machine passwords for export – so its typed confirmation names the accounts, the first 20 and how many more) |
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
min, default 10; message up to 500 characters, default text from the resources; *zamknij aplikacje bez pytania*; for
everything but a known workstation – server, DC, unknown type such as a cluster name object – a typed confirmation of
the computer name), *Anuluj zaplanowane ponowne uruchomienie*, the tool launchers,
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
| Refresh policy (computer) | `Win32_Process.Create` with the constant `gpupdate.exe /target:computer /force /wait:0`, which returns at once while the Group Policy service keeps processing – so not the process but `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\State\Machine\Extension-List\{00000000-0000-0000-0000-000000000000}` is watched: end time (`EndTimeHi/Lo`) read before the launch, then polled until it is later (≤ 10 min), then status. Not the default `/wait`: gpupdate could block on a restart/logoff prompt in session 0 | result *zakończone o …* / *trwa*; no output captured |
| Refresh policy (users) | one-time scheduled task through `root/Microsoft/Windows/TaskScheduler` `PS_ScheduledTask` (`RegisterByXml`): principal group `S-1-5-4` (INTERACTIVE), action `gpupdate.exe /target:user /force`, registration trigger with a 5 s delay (not a time trigger: the workstation's "now + 5 s" can already be past on a computer whose clock runs ahead, and the task would never start), expires after 10 min and is deleted – what `Invoke-GPUpdate` does | setting `RefreshUserPolicy`, off until the pilot confirms it |
| Restart | `Win32_OperatingSystem.Win32ShutdownTracker(Timeout = delay s, Comment = message, ReasonCode = 0x80000000 (planned, other), Flags = 2 reboot (+4 force))` | the message is shown to the logged-on users; always confirmed |
| Abort restart | `Win32_Process.Create` with the constant `shutdown.exe /a` | its exit code is not observed: the result says *cancellation requested*, not confirmed |

**Rights** (all with the window's account on the target): WinRM itself admits local *Administrators* and *Remote
Management Users*; `root\cimv2` remote access, `GetOwner` of other users' processes, `Win32_Process.Create` and the
shutdown privilege need **local Administrators** (on DCs: Domain Admins). The window says so when it gets
`AccessDenied`. Prerequisite on the computers (K1's Polish doc `docs/KOMPUTERY.md`, with a GPO recipe): service
*Windows Remote Management* automatic, *Zezwalaj na zdalne zarządzanie serwerem przy użyciu usługi WinRM* (listener
filter `*`: it selects the computer's own addresses WinRM listens on, not the clients), firewall rule *Windows Remote
Management (HTTP-In)* with its remote addresses (scope) limited to the admin subnet.

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
  A Windows-account window starts them directly (CreateProcess with `__COMPAT_LAYER=RunAsInvoker`: CreateProcess cannot
  elevate mmc's `highestAvailable` or the auto-elevating eventvwr and fails with 740 for a local administrator). Otherwise `CreateProcessWithLogonW(LOGON_NETCREDENTIALS_ONLY)` (what
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

## 6. K3–K5 (design; K3 as built: section 17)

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

Branch `feature/k1-computers`, October 2026. Everything of section 4 is built; the milestones are the commits
`K1.1` (tag `k1.1-acl`) … `K1.5`. Deviations and additions, so that K2 and the merge know them:

* **Catalogue.** Windows Server 2025 ends **2034-11-14** (the table in 4.1 said 2034-10-10). The dates were set with
  `ValidAsOf` 2026-10-27; the sandbox had no access to the Microsoft Lifecycle pages, so the pilot must compare the
  table once more (the window warns after 9 months anyway). Builds 28000 (26H1) and 25398 (Server 23H2) were added.
* **Shared files beyond 11.2 (all additive, K2 does not touch them):** `Core/Batch/BatchReportExporter.cs` (machine
  passwords of computer objects are never written to a report, K1.3); `Shell/ShellViewModel.Commands.cs`
  (`.. ComputerStateCommands` in the state refresh list); `AdMng.App.csproj` (reference to `AdMng.Remote`);
  `Views/PropertiesWindow.xaml` (the template of the *Stan* page, before the `AttributeEditorPageViewModel` template, and an
  `xmlns:cv`); `Views/MainWindow.xaml` also gets the *Zdalne akcje…* button in the details pane's quick actions (after
  `object.move`, visible for computers) – the entry point of 4.5; tests: `AccountReviewTestSupport.cs`,
  `PropertySheetTests.cs` (the *Stan* page in the computer's page list), `ServiceRegistrationTests.cs`, `SettingsTests.cs`.
  New K1 test helpers: `tests/AdMng.Testing/FakeSecurityDescriptor.cs`, `tests/AdMng.App.UiTests/Computers/**`.
* **Settings.** `ComputerSettingsViewModel` (Presentation/Computers) is the *Komputery* tab: WinRM options, restart
  defaults, the end-of-support horizon (also editable in the review), expected containers of workstations and servers
  (one portable DN per line, validated) and the operator's end-of-support dates. Rules for other types stay as stored.
* **UI tests.** `UiTestContext` replaces the four remote services with the `AdMng.Testing` fakes **after** the test's
  `configure` (a test that registered an instance keeps it). `TestDialogService` (object dialog tests) does not implement
  `IComputerDialogs`; the screenshot test builds its review through `ComputerUiSupport.FactoryWithComputerDialogs` (the real
  `DialogService`) instead of changing that shared helper. Screenshot `22-computers.png`: the end-of-support tile with
  `LOD-PC107` selected; it does not pin the clock (Windows 7 and Server 2012 R2 stay "ended" on any day).
* **Not built (left for later):** the optional *Właściciel obiektu* column of the review (the owner is shown on the
  *Stan* page only); the Windows CI loopback test of `CimRemoteComputerClient` through a Negotiate test hook (the client
  is Kerberos-only and has no such hook – it is covered by the pilot); remote service start/stop (non-goal, question 3).
* **Lab tests** (`ComputerLabTests`, Samba 4.19): inventory of `OU=Firma` equals `ldapsearch` (password on stdin);
  `lastLogon` on the lab DC; a run site `K1T-<run>` with the subnet `10.250.<n>.0/24` (description `K1T …`) resolved by
  `SiteTopology`/`ComputerSites`, both removed in `finally`; prestage plan (create + four ACEs for `helpdesk.test`) read
  back; reset computer account over LDAPS; disable, move, delete through the planner and `DomainControllerAccount` for the
  lab DC. Objects live in `OU=K1T-…` below the run OU.
* **Still to confirm on Windows (pilot, section 12):** CIM with Kerberos and an explicit credential of another account,
  `Win32ShutdownTracker` with message and delay, the user policy refresh task (`RefreshUserPolicy`, off by default),
  `CreateProcessWithLogonW` + `RunAsInvoker` for mmc, KB5020276 reuse with prestaged accounts, the inventory against
  `Get-ADComputer`, the catalogue dates, and every WPF window of K1 (UI tests and the smoke test run on Windows CI only).

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
  shows a note (`GroupPolicy.WmiTestLater`). Still open after the K1 merge (section 16).
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

## 16. K1 and K2 merged

Branch `feature/k12` from `feature/k-design`, October 2026: `git merge --no-ff feature/k1-computers` (K1 review 1–18,
`71ffc5c`), then `git merge --no-ff feature/k2-gpo` (K2 review 1–14, `c753650`). Both branches already shared the K1.1
commit `d46e370` (tag `k1.1-acl`, merged into K2 before K2.4), so the ACL foundation merged once.

**Conflicts and how they were resolved.** The first merge was clean (K1 only). The second had three conflicts; every
other shared file of 11.2 and of the two "beyond 11.2" lists merged additively as planned (`AppHost`, `DialogService`,
`ToolWindows`, `SmokeTest`, `MainWindow.xaml`, `ObjectListView.xaml`, `Glyphs`, `ObjectIcons`, `AppSettings`,
`ServiceCollectionExtensions`, `ShellViewModel.Commands/Palette`, `RollbackPlanner`, `Strings*.resx`, `HostTests`,
`ScreenshotTests`, `ConnectionLifetimes`, README, ARCHITECTURE and this note).

* `Core/Batch/BatchExecutor.Security.cs` and `BatchExecutor.cs` – a real semantic overlap, both sides kept. K1 review 7
  moved "what the step actually changed" to the public `ChangeResult.AppliedAcl` (the union of the ACEs written by every
  attempt, used by the audit and by `RollbackPlanner.Executed`, so an ACE another administrator added meanwhile is
  neither claimed nor removed). K2 review 1 introduced `AclChange.ReplacesExplicit` (copy with permissions, restore of
  a deleted GPO) with the applied delta on the private `StepOutcome.AppliedAcl`. Merged: the executor turns a
  `ReplacesExplicit` delta into the exact delta (`ExactDelta`: the wanted ACEs plus the removal of every explicit ACE
  read that is not one of them) before `DaclEditor.Apply`, a succeeded step reports the applied ACEs (removals of the
  schema default entries included) in `ChangeResult.AppliedAcl`, and a failed `ReplacesExplicit` step audits the exact
  delta it tried (`StepOutcome.TriedAcl`, renamed from K2's `AppliedAcl`; the planned delta lacks the removals). The
  rollback of such a step never runs on its own – the object is created by the same plan, whose rollback deletes it –
  and `AclChange.Inverse()` drops the flag anyway. New tests in `ModifySecurityPipelineTests`
  (`ReplacesExplicit_ReportsTheRemovedAcesInTheResultAndTheAudit_AndKeepsInheritedOnes`,
  `ReplacesExplicit_ThatFails_AuditsTheExactDeltaItTried`).
* `tests/AdMng.App.UiTests/Infrastructure/UiTestContext.cs` – only the class comment: it names both K2's
  `FakeSysvol` writer (`context.Sysvol`) and K1's four remote fakes; the code (writer replacement before `configure`, the
  remote fakes after it) merged cleanly.

**Checked after the merge.** `Strings.resx` and `Strings.pl.resx`: 3762 keys each, the same set, no duplicates. No
palette shortcut or command id is used twice (`gpo.containerPolicies` is both the list toolbar button and a context
menu item by design). `ShellViewModel.Commands.cs` carries both `.. GroupPolicyStateCommands` and
`.. ComputerStateCommands`; `XamlSourceTests` (K2) reads the command ids of every `ShellViewModel*.cs` partial, so it
covers K1's `computer.*` ids. The smoke test renders K1's windows (`RenderComputerWindows`) and K2's documents and
windows (`RenderGroupPolicyAsync`); screenshots 22 (computers) and 23 (Group Policy) keep their numbers.

**Unblocked by the merge, not built yet (follow-ups):**

* *Sprawdź na komputerze…* in the WMI filter editor (5.3): needs a WQL query method on K1's `IRemoteComputerSession`
  (today: operating system, logged-on users, services, policy refresh, restart) and a CIM implementation in
  `AdMng.Remote`; the editor keeps the `GroupPolicy.WmiTestLater` note until then.
* The K5 modelling report (section 6) can now combine K1's computer facts (site, OU) with K2's link and inheritance
  reads; nothing of it exists yet.
* Permission awareness of the Group Policy commands (15) is still open.

**Tests on the merged branch** (Linux, `dotnet build AdMng.slnx`: 0 warnings, 0 errors): `AdMng.Core.Tests` 1209,
`AdMng.Presentation.Tests` 1227, `AdMng.Remote.Tests` 75, `AdMng.Watchdog.Core.Tests` 2279 (+4 skipped),
`AdMng.Watchdog.Service.Tests` 819 (+16 skipped) – all passed. Samba lab (`AdMng.Directory.IntegrationTests`,
`ComputerLabTests` 7, `GroupPolicyManagementLabTests` 5, `SessionLogonsTests` 4): 16 passed; a read-only check
afterwards found no `K1T`/`K2T` objects left on the shared lab.

**Verified on Windows CI after the merge into the U1c/U1d branch** (run 40598786957, b80470d): `AdMng.App.UiTests`
(both tracks' UI tests, `HostTests`, `XamlSourceTests`, screenshots 22 and 23) and `AdMng.exe --smoke-test`. The first
Windows runs found test expectations to correct (titles name the domain, controls hidden through their row) and one
product defect: the container built `WindowsReleaseCatalog` through its `IEnumerable<WindowsRelease>` constructor, an
empty catalogue (fixed with a factory registration, `ComputersRegistrationTests`). Every pilot item of 12 and of 14/15
still stands.

## 17. K3 as built

Branch `claude/active-directory-manager-w9v9i8` after the 0.7.0 release, November 2026. Commits: parser, definition set
and the `Registry.pol` engine (`299b1e5`), central store read and the save plan (`cbc37c0`), the editor's view models
(`bdeeeb1`), its view, entry points, smoke and UI tests (`ce178c8`), the central store update core (`d0b752f`) and its
window (`fa0085f`), then this documentation with the lab tests. Everything of section 6 (K3) is implemented unless
listed below.

**Decisions taken with the operator**

* **Sources of templates: files plus the stable downloads.** Only Edge, Chrome and Firefox are downloaded – they have
  stable, documented addresses: Edge from the release list `edgeupdates.microsoft.com/api/products?view=enterprise`
  (product *Policy*, highest version, the ZIP artifact preferred over the CAB, its published SHA-256 and size checked),
  Chrome from the fixed `dl.google.com/…/policy_templates.zip` (no hash published: the pinned address is the only
  check), Firefox from the latest GitHub release of `mozilla/policy-templates` (asset `policy_templates*.zip`, the
  `digest` checked when GitHub gives one). Windows 11 / Server 2025 (MSI), Microsoft 365 Apps (EXE) and everything else
  (OneDrive, Adobe, Zoom, FSLogix, Citrix …) come from a file or a folder the operator chose – the vendor catalogue of
  section 6 with per-vendor signer pins was not built. `VendorTemplateCatalog` holds the three addresses, the pinned
  hosts (HTTPS only, also checked on the final address after redirects) and the parsing of the release descriptions;
  `PolicyTemplatePackageLoader` does the HTTP (size limits, `User-Agent: ADManager/1.0`).
* **Trust.** `PackageTrust`: *Signed* (Authenticode valid through `WinVerifyTrust`, signer shown), *PinnedDownload*
  (host shown), *Unsigned*, *NotChecked* (the operator's files) – the last two with a warning. The package (at most
  `MaxArchiveBytes`) is first copied into a private subfolder of a temporary folder of the profile (`admng-admx-*\package`,
  a name of its own) and held open there read-only (`FileShare.Read`) while it is verified – through that handle,
  `WINTRUST_FILE_INFO.hFile` – and while it is unpacked: an installer's tool runs on that copy with the private folder as
  working folder, a CAB is read through the handle (no swap after the check, no DLL planted beside the operator's file). An MSI or EXE is run (`msiexec /a … TARGETDIR="…"`,
  `/extract:"…" /quiet`) only when Microsoft signed it (`AuthenticodeVerifier`): `WinVerifyTrust` keeps its state, the
  signer's chain is taken from it (`WTHelperProvDataFromStateData`, `WTHelperGetProvSignerFromChain`) and must pass
  `CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_MICROSOFT_ROOT)` – the product roots (flight root disabled) or the
  application root "Microsoft Root Certificate Authority 2011" –, and the leaf of that chain must be a Microsoft
  publisher certificate (`PackageSigner`: its only `O` RDN exactly `Microsoft Corporation`, compared on the parsed name,
  never on a formatted subject; not a Windows Hardware Compatibility Publisher certificate, whose WHQL usages mark other
  vendors' drivers). The window shows that parsed organization (else the common name, without control or formatting
  characters), also for a signed CAB. A CAB is only unpacked and may be unsigned; its header is read first
  (`CabinetDirectory`) and a CAB that would unpack more than `MaxArchiveBytes`, belongs to a set or names a file that is
  not a plain relative path is refused; a CAB whose certificate claims Microsoft Corporation without Microsoft's chain
  counts as *Unsigned*. The CAB is unpacked in memory through Windows' cabinet library (`CabinetUnpacker`: `cabinet.dll`
  FDI with our own callbacks), read through the same held handle – no tool runs and nothing of it is written; only
  templates and ZIP archives are kept, every name a plain relative path again, every file within the size its entry
  declares (a template within `MaxEntryBytes`) and all of them within `MaxArchiveBytes`, a file of a set refused. Edge's
  CAB holds a ZIP, which is read in memory. (The first build ran `expand -F:*` on the held copy: on Windows CI it ended
  with 0 having written nothing, so an installer whose tool writes nothing is now *ExtractionFailed* as well, never a
  package without templates.) The temporary folder is deleted right after; a downloaded Edge CAB is the only download
  written to disk (temp file, deleted after extraction).
* **Package limits** (`PolicyTemplatePackage`, a crafted archive must not exhaust memory or time): at most
  `MaxEntries` (100 000) entries looked at – a ZIP whose end of central directory (classic or ZIP64) declares more is
  refused before its directory is read –, `MaxEntryBytes` (32 MiB) per file, `MaxTemplates` (5 000) templates and
  `MaxPackageBytes` (512 MiB) for the ADMX/ADML files a package keeps; beyond them an `InvalidDataException` that
  `PolicyTemplatePackage.IsTooLarge` recognizes (the extractor reports *TooLarge*). A ZIP is opened once and its entries
  inflated one at a time under a lock, honouring the cancellation token; an entry that cannot be inflated (BZip2, LZMA,
  PPMd, damaged data) or whose size is not the declared one reads as null and is listed as unreadable. An extracted
  folder is read after the selection, so only the files the package keeps are read.
* **Backup only to the operator's folder** (`CentralStoreBackup`, `PolicyDefinitions-<DC>-<yyyyMMdd-HHmmss>`, the
  folder remembered as `GroupPolicySettings.LastBackupFolder`, shared with GPO backups). The alternative sibling folder
  on SYSVOL was not built: it would replicate to every DC and is not the operator's explicit export (rule 1).

**Deviations and additions**

* **No central store → this computer's templates** (`WindowsLocalPolicyDefinitions`, `%WINDIR%\PolicyDefinitions`), as
  the Microsoft editor does; the editor's banner says which source it uses (`AdmTemplates.SourceCentral|Local|
  LocalWithoutSysvol|None`). The update window's *Szablony tego komputera* creates the store from them; the plan then
  carries the info `IssueCodes.CentralStoreCreated`.
* **Languages**: UI culture, the file's `fallbackCulture`, `en-US`, then any. A neutral package folder (`pl`) goes to
  the store's matching specific folder or `CultureInfo.CreateSpecificCulture` (`pl-PL`); culture folders are recognised
  by shape (ICU accepts nearly any name as a culture).
* **Namespace collisions** (old and new Edge templates under different file names): loading keeps the first file and
  reports the rest; the update skips a colliding file unless *Zastąp stare szablony* deletes the old ADMX with its
  language files (`Removed`). Leftovers of an interrupted update (`*.admng-<run>.tmp`) are deleted (`Leftover`).
* **Central store plan**: one `ObjectPlan` anchored on `CN=Policies,CN=System,<domain>` (no LDAP step; display name =
  the UNC path of the store) with `WritePolicyDefinitionsFile` / `DeletePolicyDefinitionsFile`, executed by
  `PolicyDefinitionsTransaction` (stage every write as `*.admng-<run>.tmp`, check that every target still has the content
  the comparison read – else a conflict and nothing is switched –, switch, delete; compensation from memory on
  failure). No version
  numbers; the rollback writes the old bytes back or deletes the new file. PowerShell: comment lines naming the file
  (`Copy-Item` / `Remove-Item` into the store) – there is no cmdlet. Audit: paths, sizes and SHA-256 only.
* **`Registry.pol` writes** keep every untouched entry byte for byte and in place (`RegistryPolicyDocument`), new keys
  in registry order; the state of a policy is read by weighing evidence (policy value and lists twice, element values
  once) and is *Niejednoznaczne* (`PolicyState.Inconsistent`) when it fits neither enabled nor disabled. Extra entries
  no template describes are listed (*Ustawienia rejestru spoza szablonów*) and can only be deleted.
* **Save plan**: `Modify gPC(Machine|User)ExtensionNames` first (Registry CSE with the half's tool GUID plus the
  `clientExtension` GUIDs of configured policies, pairs sorted as GPMC writes them), then `WriteGpoFile` with the content
  the editor read as the compare-and-swap value (a change by someone else is a conflict, `AdmTemplates.ConflictNote`),
  then `SetGpoVersion` per changed half. A disabled half (`flags`) gives the warnings `GpoComputerPartDisabled` /
  `GpoUserPartDisabled`. PowerShell: `Set-GPRegistryValue` (also `-Disable` for `**del.`) and `Remove-GPRegistryValue`.
* **Entry points**: *Edytuj szablony administracyjne* on the GPO list (toolbar, context menu) and the GPO document
  (header, *Ustawienia* tab); *Zaktualizuj szablony…* on the GPO list and in the editor. No palette command ids were
  added.
* **Lifetimes**: `PolicyDefinitionsService` is per connection (cache per server, culture and source; invalidated after
  an update); `PolicyTemplatePackageLoader`, `ILocalPolicyDefinitions` and the App's `IPolicyPackageExtractor` are
  stateless singletons (`ConnectionLifetimes.Shared`, `HostTests`).

**Pre-release review** (before 0.8.0: five areas, every finding checked by a second reviewer that tried to refute it;
33 distinct findings confirmed, all fixed with tests – commits `2c018a1`, `764b2e9`, `e3e0687` and the integration
after them):

* *Registry.pol engine.* Disabled writes the policy's own `disabledValue` / `disabledList` / `enabledList` after the
  elements' deletions, so an element sharing those values no longer turns them into `**del.` or forgets them (Citrix
  lockdowns, the IE zone templates). Lists take only string values no loaded policy owns (`ownedByAny`, the editor's
  map of every policy's values); an additive list keeps other policies' values and values of other types in its key
  (WER consent). A policy with a value name whose value the file does not mention reads as not configured; element
  deletions count for Disabled only when no element value is set (Edge redirection channels); an enum item whose
  value list deletes the enum's own value reads back (BITS "Custom"); a value set again moves after a later
  `**delvals.` / `**DeleteValues` / ancestor `**DeleteKeys` of another tool.
* *Parsing.* Number boxes default to the schema's `defaultValue` 1; a repeated element id keeps the first definition
  (BIS-F); control characters in keys and value names and NUL in string values make a template unreadable (the
  Registry.pol writer would refuse them later); categories nest at most `MaxCategoryDepth` (32) levels
  (`CategoryTooDeep`, no stack overflow on chains through files).
* *Pipeline.* A cancel during a transaction's switch records, audits and undoes every step the transaction decided
  (also GPO saves – the mechanism is K2's). The central store transaction removes the folders it created when it does
  not commit and the folders its deletes empty, the store included
  (`ISysvolWriteConnection.DeleteEmptyPolicyDefinitionsFolderAsync`): undoing the creation of a store removes it. The
  PowerShell equivalent no longer removes a value it has just set when a policy toggles between Enabled and Disabled.
  The comparison keeps the store's template when the package holds two of one namespace, lists the store's language
  files a replaced template leaves at the old revision (`OutdatedLanguage`, counted as skipped; the window also
  chooses the languages the store already has) and keeps no content of skipped files.
* *Packages.* See *Trust* and *Package limits* above (Microsoft root chain policy and exact organization, a private
  copy held open while verified and unpacked, aggregate limits, a ZIP opened once, unreadable entries as null).
* *Editor and update window.* The selected policy stays editable after a reload; closing a tab or window with
  unsaved edits asks (`GroupPolicyDocumentBase.HasUnsavedChanges` / `CanCloseAsync`); invalid options block the save;
  the editor is read-only while saving and recognizes its own saved bytes after the reload (no false conflict); a
  reload of the templates is not lost to a superseding load; a set built with failed reads is not cached and a load
  started before `Invalidate()` does not refill the cache; template issues give localized reasons; the state radios
  no longer group across tabs. In the update window comparisons are serialized (`IsComparing`, only the latest result
  for the shown package counts, a failed one leaves nothing to update), the backup runs off the UI thread and the
  window cannot be closed while it updates, one ADMX file loads only that template with its ADML from the culture
  folders beside it, skipped package files are listed, a release description that is not JSON or a broken body is a
  failed download, downloads use the operator's credentials for the system proxy and a per-request timeout, and a
  package over the limits is *too large*.

**Not built (follow-ups):** comments of settings (`.cmtx`), the editor's filter options (platform, keywords,
requirements), legacy ADM templates, the vendor catalogue beyond Edge/Chrome/Firefox, a GPO's Registry.pol byte-identity
check against the Microsoft editor (no pilot fixtures yet).

**Tests.** Core: ADMX/ADML parsing (hostile XML, limits), definition set (languages, collisions, loops, sorting),
`Registry.pol` document (round trip, minimal edits, special value names), the policy engine (every element kind, enabled
/ disabled / not configured, inconsistent reads), the save plan, the central store comparison, plan, transaction,
rollback and backup, the vendor release descriptions. Presentation: the editor (tree, search, filters, element editors,
validation, save through the preview, conflict, discard) and the update window (file, folder, this computer's
templates, the three downloads through a fake HTTP handler – pinned hosts, redirects, hash, failures –, languages,
comparison, backup, update through the preview) on the fake directory and `FakeSysvol`. App (Windows CI only): editor
saved through the real preview window, the update window registered and rendered, an update from a folder through the
real preview window, smoke test, screenshots `29-administrative-templates.png` and `30-central-store-update.png`.
Lab (`AdministrativeTemplatesLabTests`, `SambaTool` collection): a GPO's `Registry.pol` files saved through the pipeline over smbclient, read back by our engine and by Samba's own PReg parser (`samba-tool
gpo show`), extension names and versions checked, undone; a central store created or extended (this run's own
namespace), read like the editor reads it, backed up, replaced and undone, the store's folders removed when the lab had
none. Templates in all tests are the synthetic Polish samples (`SampleAdministrativeTemplates`), never vendor files.

**Not verified here:** whether the current Windows ADMX MSI and Microsoft 365 Apps EXE pass the Microsoft root chain
policy (an installer signed through another Microsoft root would be refused – the operator then unpacks it by hand and
loads the folder); the vendor downloads against the real servers (the sandbox's proxy blocks the vendors' hosts:
the formats were taken from the vendors' documentation and tested with prepared responses), MSI/EXE/CAB extraction and
`WinVerifyTrust` (Windows only), the WPF views (CI on Windows), and the pilot items: `Registry.pol` identical to the
Microsoft editor's for the same settings, `gpresult /h` on a client, updates from the current Windows 11 / Server 2025,
Microsoft 365 Apps, Edge, Chrome and Firefox packages.

## 18. K5 – "co dostanie komputer" (design)

Group Policy modelling in the connection window: which GPOs a computer and a user would get, in which order, which
setting wins and why – computed from the directory and SYSVOL of the session's DC (rule 4) without connecting to the
computer, like GPMC's *Group Policy Modeling* (not *Results*: nothing is read from the client). Read only: no plan, no
write; everything stays in memory while the tab is open (rule 1), an export is a file the operator asks for.

**Inputs** (`GroupPolicyModelRequest`, Core `AdMng.Core.GroupPolicy.Modeling`):

* *Komputer*: a computer account (its parent container gives the OU chain; its token the security filtering) or a
  container (a new computer there: OU or `CN=Computers`; token Domain Computers + well-known SIDs), or none.
* *Użytkownik*: a user account or a container (token Domain Users + well-known SIDs), or none. At least one of the two.
* *Lokacja*: a site or none. Default: a DC's server object's site; another computer's site through K1's
  `ComputerSites` (DNS + subnets) when it resolves; else none (site links then do not apply – said in the report).
  The user part uses the computer's site (the site of the logon), none without a computer.
* *Filtry WMI*: assumed true (default; such a GPO is listed as applying *if* its filter is true) or false.
* *Pętla zwrotna* (loopback): from the computer's settings (default), off, merge or replace (a "what if").

**Token** (`ModelingToken`): for an account `tokenGroups` (base read; the DC expands nesting and the primary group) +
its objectSid + Everyone, Authenticated Users, This Organization (+ Enterprise Domain Controllers for a writable DC);
for a container the primary group of a new object (515 / 513) + the well-known SIDs. Shown in the report (group names
resolved for display only).

**GPO list per part** ([MS-GPOL] 3.2.5.1): `GroupPolicyInheritance` gets an explaining variant (`Explain`) that
returns every link of the chain with its outcome instead of dropping it – *LinkDisabled*, *Blocked* (by which
container), applied (order) – and the modeller adds, per applied link in that order: *NotFound* (no GPC), *PartDisabled*
(`flags` bit 2 computer / bit 1 user), *Empty* (that half of `versionNumber` is 0), *SecurityDenied* (no Apply Group
Policy for the token, or a deny), *ComputerCannotRead* (user part: since MS16-072 the computer's token must be allowed
to read the GPO – a deny or no read access excludes it), *WmiFilterFalse* (assumed false) or *Applied* /
*AppliedIfWmi*. Precedence 1 = applied last (wins), as GPMC numbers it. A GPO linked twice counts once, at its winning
link (the other link listed as *Duplicate*).

**Loopback** (user part only, needs a computer): the mode is the winning value of
`HKLM\Software\Policies\Microsoft\Windows\System!UserPolicyMode` among the computer part's applied GPOs (1 merge,
2 replace; the ADMX policy *Configure user Group Policy loopback processing mode*) unless the operator overrides it.
Replace: the user part's chain is the computer's container chain (filtered with the user's token). Merge: the user's
chain, then the computer's (the computer's GPOs applied after – higher precedence; a GPO in both counts at its last
position). The report says which mode was used and why.

**Settings** (per part, from the applied GPOs only, in application order):

* *Szablony administracyjne*: each applied GPO's `Machine|User/Registry.pol` is read by K3's `PolicyEngine` with the
  definitions of the editor (`PolicyDefinitionsService`; central store or this computer's templates) and the shared
  ownership index (`PolicyOwnershipIndex`, moved out of the editor). Per policy the winner is the highest-precedence
  GPO that configures it (state and options as in that GPO – GPMC's reporting rule); the others are *przesłonięte*.
  Values no loaded template describes: per value (`key!name`, deletions included) the last GPO wins.
* Other settings: the normalized `GpoSetting` keys of the Watchdog parsers (`GroupPolicyReader.Settings`) – security
  template sections, advanced audit, GPP, event log … – per key the last GPO wins, with every overridden source kept.
  Exceptions: scripts are cumulative (all of them run, in order: no winner); comments are not settings; unreadable files
  are warnings of their GPO; account policy (`[System Access]` password, lockout, `[Kerberos Policy]`) for a DC comes
  only from GPOs linked to the domain head (the domain's account policy), for other computers from all (local
  accounts) – said in the report. GPP item-level targeting is not evaluated: items stay, marked *zależy od komputera*
  when the parser saw targeting.

**Output** (`GroupPolicyModel`): per part the GPO rows (outcome, precedence, link container, enforced, WMI filter name,
security filter note), the policies (definition, winning setting and GPO, overridden ones) and the other settings
(key, category, winning value and GPO, overridden), warnings (unreadable files, GPOs not readable on SYSVOL, no site,
templates missing). Pure: the engine (`GroupPolicyModeler`) takes the read inputs; the reader
(`GroupPolicyModelReader`) does LDAP (GPCs with versions, flags, WMI links and DACLs; domain head, OUs and sites with
links; the targets with `tokenGroups`; WMI filter names) and SYSVOL (the folders of the GPOs that apply, at most 4 in
parallel, within K2's limits).

**UI** (`GroupPolicyDocumentKind.Modeling`, tab *Co dostanie komputer – <name>*): a bar with the computer, the user,
the site, the WMI and loopback choices and *Przelicz*; a summary line; two tabs *Konfiguracja komputera* and
*Konfiguracja użytkownika*, each with the GPO list (applied first, by precedence; then the excluded ones with the
reason) and the settings grouped by area (administrative templates by category path, security, audit, services, GPP,
scripts, extra registry values) with a details pane *Dlaczego*: the winning GPO, its link (container, enforced,
order), and the overridden GPOs with their values; *Grupy* (the token used). Entry points: *Co dostanie komputer…* on
computers and *Co dostanie użytkownik…* on users (object menu, Akcja, palette `gpo.model`), *Modeluj zasady…* on the
*Zasady grupy* tab of an OU or domain and on the computer property sheet's *Stan* page. Export CSV (GPO lists and
settings) only when the operator asks (`IFileDialogService`).

**Tests.** Core: inheritance explanation (disabled links, block inheritance and enforced links, sites, duplicates),
every exclusion reason, security filtering with allow/deny and the read check of the user part, WMI assumptions,
loopback merge/replace and its source, settings precedence (per key, scripts cumulative, account policy on a DC),
policies through K3's engine (winner, overridden, not configured ignored), extra registry values, warnings.
Presentation: the document on the fake directory (`AddGroupPolicyManagementSample`, FakeSysvol, the sample templates):
inputs, recalculation, details, entry points, export. App (Windows CI): view, entry points, smoke, UI test,
screenshot 31. Lab (Samba): an OU chain with links (disabled, enforced, block inheritance), a GPO with its computer part
disabled and one denied to a group of the computer – the applied set compared with `samba-tool gpo list` for the
computer account (Samba checks read access and link flags, not the Apply right or sites: the scenario keeps to what both
evaluate), and the settings of two GPOs on one key with the expected winner.

**Not in K5:** reading the client (RSoP *Results*, `gpresult`), evaluating WMI filters or item-level targeting on the
computer (later through K1's WinRM), slow links, the local GPO and multiple local GPOs, cross-domain users or
computers (the session's domain only), software installation and folder redirection details (listed per GPO as
extensions without settings).

### 18.1 K5 as built

* **Security filtering in DACL order.** `GroupPolicySecurityFilter.FromDescriptor` keeps the read/apply ACEs in the
  DACL's order (`Aces`); `Applies` and `CanRead` let the first ACE of the token that covers the right decide, as
  AccessCheck does (a deny written after an allow does not take it back). A filter built from the sets only (U4c's
  callers) keeps "a denied SID wins". The modeller requires Read and Apply Group Policy for the account (Read only when
  the filter knows the readers, `KnowsReaders`); the user part also requires the computer's Read (MS16-072). U4c's
  tests now write canonical DACLs (denies first, as GPMC, the K1 DACL editor and Samba keep them).
* **Entry points:** two palette commands, `gpo.model` (*Co dostanie komputer…*) and `gpo.modelUser` (*Co dostanie
  użytkownik…*) – one command cannot carry both labels – in Akcja, the list's context menu and the palette; *Modeluj
  zasady…* on the *Zasady grupy* tab of a domain or OU; *Co dostanie komputer…* on the computer property sheet's *Stan*
  page (`ComputerActions` takes the per-connection `GroupPolicyServices`). One tab per set of targets.
* **Inputs** do not recalculate by themselves (the bar says *kliknij „Przelicz”*); the default site comes from
  `ComputerSites` for a computer account and is not overwritten once the operator picks one. A missing account or
  container and an unavailable SYSVOL are notes, not errors (without SYSVOL the GPO lists are complete, the settings
  empty).
* **Lab** (`GroupPolicyModelLabTests`): the filtered GPO is set up with K2's planner as GPMC does it (Authenticated
  Users removed, a group without the computer added) – the computer can neither read nor apply it. Findings from the
  lab: Samba keeps DACLs canonical (`samba-tool dsacl set` puts a deny first), so a non-canonical DACL cannot be built
  there (the order rule is covered by Core tests); `samba-tool gpo list` (4.19) checks read access with its own token for
  the account and takes every account for a user ("GPOs for user …"), so it skips GPOs with the user configuration
  disabled even for a computer – the test leaves the computer-disabled GPO out of the comparison when Samba says
  "user".
* **Review fixes (before release):** a `Registry.pol` value is hidden from the extra values only when a policy of the
  part's class that is configured in that GPO owns it (a value only another class's or an unconfigured policy describes
  stays an extra value, as in GPMC; a value name may contain `!`); links are GPO DNs, not GUIDs – a link to another
  domain's GPO (a site's, or an OU's) is *OtherDomain* (not read from this domain's SYSVOL, never taken for the local GPO
  with the same GUID such as the Default Domain Policy) and duplicates are counted by DN; a GPO folder that fails in any
  way but cancellation is *Unreadable*; the document keeps the computer's default site until the operator picks one,
  clears the model when a recalculation fails, exports only a model of the current inputs (named after its targets) and
  models inetOrgPerson accounts as users.
* **Deviations from the design:** the GPO row of a GPO that is blocked in the user's chain and applied from the
  computer's chain under loopback merge appears twice in `ModeledPart.Gpos` (blocked, then applied through loopback) –
  deliberate, both facts are true.

## 19. K4 – security settings editor (design)

Scope chosen with the operator (2026-11): local groups, user rights, account policies with security options, audit
(legacy and advanced) and services. Firewall rules, file/registry ACLs (`[File Security]`, `[Registry Keys]`), event
log settings and the user configuration's GPP groups are **not** edited: they are shown read-only in the GPO report
(K2) and kept byte for byte by every K4 save.

**Files and writers** (`AdMng.Core/GroupPolicy/Security`, pure, no I/O):

| File | Document | Kept as read | Written as |
|---|---|---|---|
| `Machine/Microsoft/Windows NT/SecEdit/GptTmpl.inf` | `SecurityTemplateDocument` | every line it does not set: unknown sections and keys, comments, `[File Security]`, `[Registry Keys]`, event log sections | UTF-16LE + BOM, CR LF; `key = value` |
| `Machine/Microsoft/Windows NT/Audit/audit.csv` | `AuditPolicyDocument` | the header (column order), options (`Option:…`), global SACL rows, unknown rows | the file's encoding (UTF-8, BOM kept; new file without BOM), CR LF |
| `Machine/Preferences/Groups/Groups.xml` | `GppGroupsDocument` | `User` items, collections, `Filters` (item-level targeting), common options, unknown attributes and their spelling | UTF-8 + BOM, declaration, no indentation |

*Minimal edit.* Sections of the same name are one section (SetupAPI merges them). A template edit replaces the last
line of its key in all of them and removes every other line of it (which duplicate a client applies is not specified,
so a save leaves one), appends to the last section of that name before its trailing blank lines/comments, or removes
the lines; only a section the edit itself created is removed again when emptied (a section read from the file keeps
its header and comments). Setting the value a key already has changes nothing, so an edit and its undo give the bytes
that were read; a GPP item edited back to its state as read gets its element as read back (no new `changed`).
REG_MULTI_SZ lines with a comma, quote, semicolon (an INF comment when unquoted) or outer spaces are quoted. Keys and values with `=`/line breaks, principals with commas/quotes and GPP
SIDs that are not SIDs are refused (`ArgumentException`) – the file is untrusted on read, the editor's input on write.
Restricted Groups keys are matched by group (SID or name, case-insensitive) and relation, written `*SID__Members` /
`*SID__Memberof`; principals are written `*SID` or as named; a defined empty list is `key =` ("no members", "nobody").
GPP items are addressed by position in `GppGroupsParser`'s order (also inside collections); a changed item gets
`changed` (local time of the editor, `yyyy-MM-dd HH:mm:ss`) and the `image` of its action (0 C, 1 R, 2 U, 3 D) as
GPMC; new items get a new upper-case uid and are appended to the root (applied last).

**Catalog** (`SecuritySettingsCatalog`, ids only, names in the resources): the 45 user rights in GPMC's order;
account policies (`[System Access]` password and lockout, `[Kerberos Policy]`), account options (`EnableAdminAccount`,
`NewAdministratorName` …) and 38 security options in `[Registry Values]` with their kind (number with range, boolean,
choice, text, REG_MULTI_SZ lines); the nine legacy audit categories; the 59 advanced audit subcategories
(`{0CCE92xx-69AE-11D9-BED3-505054503030}`, the Subcategory column is `Audit <English name>`). Settings of a GPO that
are not in the catalog are listed as raw entries (key and value as written) that can be removed or have their raw
value replaced – never dropped.

**Plan** (`GroupPolicyPlanner.PlanSecuritySettingsAsync`, mirrors K3's 6): one `Modify` of `gPCMachineExtensionNames`
first (pairs added only for the written files: Security `{827D319E-…}` + `{803E14A0-…}`; Audit Policy Configuration
`{F3CCC681-B74C-4060-9F26-CD84525DCA2A}` + `{0F3F3735-573D-9804-99E4-AB2A69BA5FD4}`; Local Users and Groups
`{17D89FEC-…}` + `{79F92669-4224-476C-9C5C-6EFB4D87DF4A}` and the preference core entry `{00000000-…}` with the same
tool), then one `WriteGpoFile` per changed file with the bytes the editor read as `OldContent` (conflict otherwise),
then `SetGpoVersion` (computer +1) with the "computer configuration disabled" warning. Extensions are never removed
(GPMC keeps them too; a CSE with an empty file does nothing). PowerShell: the GroupPolicy module has no cmdlet for
these files – the script contains `Get-GPO` with a comment saying the plan writes the files on SYSVOL.

**Editor** (`SecuritySettingsEditorViewModel`, document kind `SecuritySettings`, "Edytuj ustawienia zabezpieczeń" next
to "Edytuj szablony administracyjne" on a GPO): one document per GPO, the three files read on open and F5 with K3's
reconcile rules (edits kept over a reload; a file changed on SYSVOL meanwhile is a conflict note; a file that holds
what this tab last saved rebases the edits), a file that cannot be read makes its pages read-only. Pages:

1. *Grupy lokalne* – Restricted Groups (group, "Członkowie" defined/not, "Członek grup") and GPP local group items
   (action, delete all users/groups, members with add/remove, item-level targeting shown and kept) side by side, with
   the precedence text of U4c: Restricted Groups `Members` replaces the membership at every refresh, GPP changes it in
   item order, the CSE order between the two is not guaranteed – do not configure the same group in both.
2. *Prawa użytkownika* – every right of the catalog with "zdefiniowane" and the holders; unknown rights of the file
   listed after them.
3. *Zasady kont i opcje zabezpieczeń* – grouped as GPMC (password, lockout, Kerberos, accounts, network security …),
   each "nie zdefiniowano" or a value; account policies carry the note that for domain accounts only the GPO linked at
   the domain root applies (fine-grained password policies are separate).
4. *Inspekcja* – the legacy categories and the advanced subcategories, each "nie skonfigurowano" / "bez inspekcji" /
   powodzenie / niepowodzenie / both; a note when both are set and "wymuś ustawienia podkategorii" is not enabled.
5. *Usługi* – service name, start mode (automatic, manual, disabled), the security descriptor kept as read (SDDL shown,
   not edited; new entries have none).

Principals are picked with the object picker (users, groups, computers; the SID is written) or typed as a name
(`KONTOSO\grupa`, written as typed, shown with a warning that it is resolved on each client). Names are resolved for
display with `TrusteeNames`, local groups with `LocalRightsCatalog`. Save = one plan through the change plan preview
(rule 5); nothing of the files is persisted or logged by the app (rule 1).

## 20. K6 – readability, editing comfort and diagnostics (design)

Scope chosen with the operator (2026-11): (A) every place that shows GPO settings speaks the operator's language,
(B) "Sprawdź zasady grupy" finds logical errors and weak security settings, with a fix through the change plan where
one exists, (C) editing GPOs is comfortable (layout, keyboard, "edytuj to ustawienie", a plain-language summary
before saving). Out of scope: searching a setting across all GPOs, copying settings between GPOs, Watchdog
notification texts (they keep the technical key; the Watchdog *tab* uses the presenter).

### 20.1 Readability (A)

**Core – `GpoSettingKey`** (`AdMng.Core/GroupPolicy/Content/GpoSettingKey.cs`, pure, key only – Watchdog rows carry
only the key): `Parse(string key) → GpoSettingKeyInfo { Scope (Computer/User/Gpo), Area, Section, Name, RegistryKey,
ValueName, PreferenceType, ItemId, Property, ScriptPhase, Index, Field, Option (SecurityOptionDefinition?), Subcategory
(AuditSubcategoryDefinition?) }`. Areas: SystemAccess, KerberosPolicy, EventAudit, UserRight, RestrictedGroup,
SecurityRegistryValue, Service, RegistryKeySecurity, FileSecurity, EventLog, SecuritySection, AdvancedAudit,
AuditOption, GlobalSacl, PolicyRegistry, Preference, Script, ScriptConfig, ScriptFile, Comment, UnreadableFile, Other.
`HKLM\…!Name` of `Registry Values` maps to the catalog's `MACHINE\…\Name`. `GpoSettingValue.TryParseRegistry(value,
out type, out data)` reads `REG_DWORD:1` etc. The catalog gains `SecurityValueUnit` per option (days, minutes, hours,
attempts, characters, passwords, seconds). The modeller's ADMX matching (`PolicyIndex`, `RegistryCoverage`,
`PolicyEngine.Read`) becomes public as `AdministrativeTemplatePolicies.Configured(...)`, so the GPO tab and the
comparison name policies the way K5 does. `GpoComparisonRow` and `GpoDiffLine` carry the `GpoSetting`s they came from.

**Presentation – `GpoSettingPresenter`** (`ILocalizer`, SID names, `PolicyDefinitionSet?`): `Describe(GpoSetting)` /
`Describe(key, values, masked)` → `GpoSettingText { GroupPath ("Konfiguracja komputera › Zasady › Ustawienia systemu
Windows › Ustawienia zabezpieczeń › Zasady konta › Zasady haseł", GPMC's tree), Name ("Maksymalny okres ważności
hasła"), Technical (constant or key – tooltip), ValueText ("42 dni", "Administratorzy, KONTOSO\Serwisanci",
"Powodzenie i niepowodzenie", "Włączone"), Elements, Explanation? }`. Names come from the K4 resources (`SecRight.*`,
`SecOption.*`, `SecAudit.*` …), ADMX display names, new resources for areas, units, GPP item types and their main
properties, script phases. SIDs: local groups and well-known principals by name, the rest through one
`TrusteeNames.ResolveAsync` per view (`GpoSettingPresenter.Sids(settings)`). Masked values stay masked.

Consumers: the GPO document's *Ustawienia* tab (grouped like a GPMC report, search, "Edytuj to ustawienie", **Raport
HTML…** to an operator-chosen file – rule 1 allows exports), the K5 model rows, the GPO comparison (and its CSV), the
change plan preview's SYSVOL differences, the Watchdog tab's GPO change rows.

### 20.2 Diagnostics (B)

**Core – `AdMng.Core/GroupPolicy/Diagnostics`.** `GroupPolicyDiagnosticsReader.ReadAsync(session, sysvol, request
{ GpoId? (one GPO or the domain), Depth (Quick: LDAP + GPT.INI; Full: + SYSVOL folders, folder ACLs, object counts,
SID resolution, conflicts) }, progress, ct) → GpoDiagnosticsSnapshot` (only in memory; at most 4 SYSVOL reads at a
time as elsewhere). `GroupPolicyDiagnostics.Analyze(snapshot) → IReadOnlyList<GpoFinding>` is a pure function.
`GpoFinding(Code, Severity {Info, Low, Medium, High, Critical}, Area {Links, Scope, Filtering, Versions, Folder,
Content, Conflict, Security}, GpoId?, ContainerDn?, SettingKey?, Arguments, Fix?)` – `ToString` carries no directory
values. `GpoQuickFix` records map one-to-one to existing planner calls: `Unlink(container, gpo)`, `EnableLink`,
`SetStatus(gpo, status)`, `GrantRead(gpo, sid)` (MS16-072: Authenticated Users *Read* via `PlanSetDelegationAsync`),
`ClearWmiFilter(gpo)`, `DeleteWmiFilter(dn)`, `AlignVersions(gpo)`, `RepairFolderSecurity(gpo)`,
`RemoveTrustee(gpo, sid)`, `RepairExtensionNames(gpo)` (new planner method: adds the CSE/tool pairs of the files the
folder holds and K3/K4 know: Registry.pol, GptTmpl.inf, audit.csv, Groups.xml). The GPO document's *Stan* tab uses the
same engine for its GPO (its checks move into Core).

| Code | Sev. | Rule | Fix |
|---|---|---|---|
| `GpoNotLinked` | Info | no link in this domain | – |
| `GpoLinksAllDisabled` | Low | every link disabled | EnableLink |
| `LinkToMissingGpo` | Medium | gPLink names a GPO that does not exist (base read: no such object) | Unlink |
| `LinkToInaccessibleGpo` | Info | gPLink names a GPO the session cannot read (exists, search did not return it) | – |
| `LinkMalformed` | Low | gPLink entries that cannot be read / other domains | – |
| `DuplicateLink` | Low | the same GPO twice in one gPLink | RemoveDuplicateLinks (keeps the entry that applies) |
| `LinkedAllSettingsDisabled` | Medium | linked, GPO status "all settings disabled" | – |
| `PartDisabledWithSettings` | Medium | user/computer part disabled while its version > 0 | SetStatus |
| `PartEnabledWithoutSettings` | Info | part enabled with version 0 (processing cost) | SetStatus |
| `EmptyGpoLinked` | Low | both versions 0, linked | – |
| `UserSettingsWithoutUsers` | Medium | user settings, every link's subtree has no users and no loopback reaches it | – |
| `ComputerSettingsWithoutComputers` | Medium | computer settings, every link's subtree has no computers | – |
| `AccountPolicyBelowDomain` | Medium | password/lockout/Kerberos policy in a GPO not linked to the domain root | – |
| `LoopbackWithoutComputers` | Low | loopback set in a GPO whose links reach no computers | – |
| `NoApplyRight` | High | nobody has *Apply* – the GPO applies to nobody | – |
| `Ms16072ComputersCannotRead` | High | user settings, computers cannot read the GPO (DC token when every link is in the DC OU) | GrantRead |
| `WmiFilterMissing` | Medium | the GPO links a WMI filter that does not exist | ClearWmiFilter |
| `WmiFilterUnused` | Info | a WMI filter no GPO uses | DeleteWmiFilter |
| `DenyInSecurityFiltering` | Info | deny entries in the GPO's DACL | – |
| `OrphanedTrustee` | Low | an unresolvable domain SID in the GPO's permissions | RemoveTrustee |
| `BroadEditRights` | High | Everyone / Authenticated Users / Domain Users can edit the GPO | – |
| `VersionMismatch` | Medium | AD and GPT.INI versions differ | AlignVersions |
| `SysvolFolderMissing` | High | no GPO folder on SYSVOL | – |
| `FolderSecurityMismatch` | Medium | folder ACL differs from the GPO's permissions | RepairFolderSecurity |
| `ExtensionNamesMissing` | Medium | files present whose client-side extension is not listed (ignored by clients) | RepairExtensionNames |
| `UnreadableFile` | Medium | a settings file that cannot be parsed | – |
| `Leftovers` | Low | `*.admng-*.tmp` left by an interrupted save | – |
| `PathRedirected` | High | `gPCFileSysPath` points outside the domain's SYSVOL | – |
| `RestrictedAndPreferenceSameGroup` | Medium | one local group in Restricted Groups and in GPP | – |
| `RestrictedAdministratorsNoAdmins` | High | Administrators' *Members* defined without Administrator/Domain Admins | – |
| `LegacyAndAdvancedAudit` | Medium | both audit forms, forcing subcategories explicitly off | – |
| `LegacyAndAdvancedAuditByDefault` | Low | both audit forms, forcing subcategories not defined (on by default) | – |
| `PasswordLengthNeedsRelax` | Medium | minimum length > 14 and the winning relax option is not 1 where the length applies | – |
| `TypedPrincipalName` | Info | a principal written as a name, not a SID | – |
| `UnresolvedPrincipal` | Low | a SID in rights/groups that resolves to nothing | – |
| `DenyLogonBroadGroup` | High | a deny-logon right for Everyone/Authenticated Users/Administrators/Domain Users | – |
| `ConflictingSetting` | Info | GPOs reaching the same container set one setting differently (winner and losers named) | – |
| `RedundantSetting` | Info | the same value from several GPOs on one path | – |
| `GppPassword` | Critical | `cpassword` in preferences (MS14-025, readable by every user) | – |
| `DangerousRightBroadGroup` | High | a sensitive right (debug, backup, restore, take ownership, load driver, impersonate, act as OS, …) for a broad group | – |
| `BroadGroupInPrivilegedLocalGroup` | High | Everyone/Authenticated Users/Domain Users made member of Administrators, Remote Desktop Users … | – |
| `LmCompatibilityWeak` | Medium | `LmCompatibilityLevel` < 3 | – |
| `LmHashStored` | Medium | `NoLMHash` = 0 | – |
| `SmbSigningNotRequired` | Medium | SMB server signing not required by the GPO whose value wins on the DCs | – |
| `LdapSigningNotRequired` | Medium | `LDAPServerIntegrity` ≤ 1 from the GPO whose value wins on the DCs | – |
| `AnonymousAccessAllowed` | Medium | anonymous SAM enumeration / name lookup / Everyone includes anonymous | – |
| `ReversibleEncryption` | High | `ClearTextPassword` = 1 | – |
| `GuestEnabled` | Medium | `EnableGuestAccount` = 1 | – |
| `UacDisabled` | Medium | `EnableLUA` = 0 | – |
| `PlainTextSmbPassword` | High | `EnablePlainTextPassword` = 1 | – |
| `WeakDomainPasswordPolicy` | Medium | the winning domain-root value per option: minimum length < 8, complexity off or lockout threshold 0 | – |

Broad groups: Everyone, Authenticated Users, This Organization, BUILTIN\Users, Domain Users, Domain Computers. "Reaches
DCs": linked to the domain root or the Domain Controllers OU (enforced or not blocked). Conflicts come from
`GroupPolicyModeler.Build` per container with links (synthetic computer and user target), `Overridden` with another
value → `ConflictingSetting`, the same value → `RedundantSetting` (one finding per container and setting).

**UI.** Document **Diagnostyka zasad grupy** (domain; from the GPO list toolbar, *Akcja → Zasady grupy*, the palette
`gpo.diagnose`; one tab per domain): *Szybka* / *Pełna* check with progress and cancel, summary chips per severity,
filters (severity, area, GPO, text), list (severity, area, GPO, container/setting, message), details (explanation,
recommendation, "Napraw…" → plan preview, "Otwórz GPO", "Edytuj to ustawienie", "Pokaż w modelu"), export CSV/HTML to an
operator file. The GPO document's *Stan* tab shows the same findings for its GPO (with the existing repair buttons as
fixes). Findings and snapshots live only in memory (rule 1).

### 20.3 Editing comfort (C)

- Headers: title + status trimmed with tooltip in a two-column grid next to the buttons (K3, K4, GPO, K5, scope);
  columns star-sized or auto with `TextTrimming` and tooltips; K4 gets splitters; visible splitters everywhere.
- K3: the configured counter follows edits; switching away from a policy with an invalid value keeps the value and
  warns; a missing central store shows as a warning card; the category column only when the list spans categories;
  registry location shown as `HKLM\…` key and value on two lines.
- K4: search box and "Tylko zdefiniowane" / "Tylko zmienione" on every page.
- Keyboard: **Ctrl+S** saves the active editor (shell descriptor, `ISaveableDocument`), **Ctrl+F** focuses the
  document's own search when it has one (`ISearchableDocument`, else the directory Find), Esc clears it; Enter on a
  setting focuses its editor. Palette: *Edytuj szablony administracyjne*, *Edytuj ustawienia zabezpieczeń*, *Zapisz
  zmiany*, *Sprawdź zasady grupy*.
- **Edytuj to ustawienie**: `GroupPolicyDocumentRequest.Focus` (`GroupPolicySettingFocus`: setting key or ADMX policy
  id + class) opens K3 at the policy or K4 at the page and row (an open editor tab is reused and re-focused); from the
  *Ustawienia* tab, the K5 model rows, findings and the scope view's link menu (*Edytuj…*).
- Saving: the preview shows **Zmiany ustawień** (setting, before, after) in the presenter's language above the
  file rows, filled by the K3 and K4 saves.
- GPO list: a search box (name, GUID, WMI filter, link).

### 20.4 K6 as built

- **Keys (C1).** Preference item attributes keep their `@` in `Property` (`@name`), properties and child paths do not
  (`groupSid`, `Members`, `Task/Settings/Enabled`); `ValueName` is `""` for `(Default)`, `*` for hidden names, null for
  a key without value; `Section` is the template section (`Service General Setting`); audit.csv rows without a GUID map
  to `AdvancedAudit` by name (Policy Target `System` or a known subcategory name), else `GlobalSacl`; truncated keys
  are parsed from their kept part, end with `…` and link to nothing; unreadable-file markers take the scope of their
  folder. `TryParseRegistry` returns false for `DELETE*`/`CREATE_KEY` (the overload with `GpoRegistryAction` reads
  them). The ADMX key index of `AdministrativeTemplatePolicies` is cached per definition set; the modeller uses it.
- **Presenter (P1).** Preference properties of one item, the values of one ADMX policy, one service and one script
  collapse into one row; options whose registry value the parsers mask show "(ustawione – ukryte)" (a masked value no
  option explains hides every option); hashed values show the start of their hash so two can be told apart. Kerberos
  ticket and machine-password ages have no special text for 0. `GroupPolicyText.SettingPath/SettingValue` delegate to
  the presenter. ADMX names arrive after the rows (definitions loaded in the background, failure → registry names and
  a note). K5 rows also offer *Edytuj to ustawienie*.
- **Diagnostics (C2 + review).** 51 codes (the two added in review: `LinkToInaccessibleGpo`,
  `LegacyAndAdvancedAuditByDefault`). Conflicts are compared without ADMX definitions (value by value), one preference
  item = one setting, items with item-level targeting and masked values are not compared, a conflict is reported once
  at the topmost container; models assume loopback off and WMI filters true. Domain SIDs with a RID below 1000 count
  as existing. `NoApplyRight`, `Ms16072ComputersCannotRead`, `PartEnabledWithoutSettings`, `EmptyGpoLinked` fire only
  for GPOs with an enabled link; `PartDisabledWithSettings` only when exactly one part is disabled; "a half has
  settings" uses the parsed folder at Full depth (version > 0 otherwise); GPOs with a site link are skipped by the
  "no users/computers" rules; `RestrictedAndPreferenceSameGroup` checks the Members form within one GPO;
  `BroadEditRights` uses the full broad-group set; `RestrictedAdministratorsNoAdmins` accepts Enterprise Admins; the DC
  signing and domain password rules judge the winning value (precedence, computer part enabled); a file that
  disappears while a folder is read makes the folder "not read", not missing. For one GPO the container and WMI filter
  rules are left out. The duplicate-link repair keeps the entry clients apply (enabled and enforced, then enabled) at
  its place among the other links.
- **UI (P2 + E1).** A change made elsewhere marks the diagnostics results stale instead of re-running a check; the
  *Stan* tab checks when it is shown (also when it was selected during a load) and keeps one line per area above the
  findings; *Wyrównaj wersje* is offered for a missing `GPT.INI` (not for an unparseable one, which the transaction
  refuses). Ctrl+S is the palette entry `document.save` (category *Dokument*); K4 has one search/filter bar over its
  five pages, keeps rows being edited, rows just added and options with invalid typed values listed; K3 keeps invalid
  typed options per policy (they count as unsaved changes and block the save); *Edytuj to ustawienie* is offered only
  for what K3/K4 edit (the seven template sections K4 knows, Groups.xml groups – not local users).

## 21. K7 – settings search, copying settings and the cleanup advisor (design)

Scope chosen with the operator (2026-11, README track K, *K7 – szukanie i porządki w GPO*): (a) **Wyszukiwarka ustawień
GPO** – which GPOs of the domain set a setting, a value or an account, in GPMC's words; (b) **copying settings** from one
or more GPOs into another GPO through the change plan; (c) **Porządki w GPO** – a cleanup advisor (empty, unlinked,
disabled, single-setting GPOs, duplicated settings, merge candidates) whose actions are plans with preview and undo.
Everything is read from the session's DC and its SYSVOL (rule 4), kept in the window's memory only (rule 1) and written
only through `IChangePlanner`-style planners and the preview (rule 5). Out of scope: new diagnostics codes (21.9),
preference *editors* (K10), copying across domains, searching in two languages at once, GPO links of other domains.

The three parts share one per-window, in-memory **settings index** (21.1–21.3): a throttled read of every GPO folder,
the normalized settings grouped into **setting units** with a natural identity that is the same in every GPO, and a
name/SID resolver for accounts. Search (21.4) and the cleanup advisor (21.6) read the index; copy, remove and merge
(21.5) never use index values: they read source and target again at planning time.

### 21.1 Core – the all-GPO folder reader (W1)

**Extraction.** The private `GroupPolicyDiagnosticsReader.ReadFoldersAsync` / `ReadFolderAsync` (throttled
`Parallel.ForEachAsync` over an internal `ThrottledSysvolConnection(sysvol, 4)`, per-folder status, only cancellation
stops the run) become the public static `GpoFolderReader.ReadAsync` in
`src/AdMng.Core/GroupPolicy/Management/GpoFolderReader.cs`. `ThrottledSysvolConnection` stays internal (same assembly).
Diagnostics calls the new reader with `GpoFolderReadOptions.Default` (mode `Full`) and maps each result to its unchanged
`GpoFolderState` (`GpoFolderReadResult.ToState()`), adapting the progress to `GpoDiagnosticsProgress(Folders, done,
total)`: the snapshot, the 51 codes and every diagnostics test stay as they are. `GpoFolderReadStatus` is **not**
extended (its switch users stay exhaustive); the index has its own status (21.3).

**Modes.**

* `Full` – exactly today's `GroupPolicyReader.ReadFolderAsync` per GPO (every file, `GpoTransaction` limits,
  `GpoTooLargeException` → `TooLarge`, `SysvolWriteException` NotFound → `GpoFolderExistsAsync` to tell `NotFound` from
  `Failed`, AccessDenied/LogonFailure → `AccessDenied`, anything else but cancellation → `Failed`), plus the folder ACL
  for the requests that ask for it.
* `ContentOnly` (the index; modelled on the Watchdog's `GpoReadPlanner`, which the app cannot reference) – one
  recursive `ListGpoFolderAsync(gpoId, SysvolListOptions.Default)`; more than `GpoTransaction.MaxEntriesPerGpo` entries
  → `TooLarge`; temp names (`GpoTransaction.IsTempName`) ignored; then `ReadGpoFileAsync` only for files whose
  `GpoFileClassifier.Classify(path)` role is `SecurityTemplate`, `AuditCsv`, `RegistryPol`, `Preferences`, `ScriptsIni`,
  `PsScriptsIni`, `Comment` or `GpoComment`, in `GpoFile.Priority` order. Not read: `GPT.INI` (versions come from
  `GroupPolicyReader.WithSysvolVersionsAsync`), `ScriptFile` (script payloads; their command lines come from
  `scripts.ini`) and `Inventory` files. Inventory files below `Machine/` or `User/` (folder redirection, software
  installation …) are listed in `OtherFiles`: they may hold settings no parser reads (as `GpoFolderView.HasSettings`
  counts them), so a GPO with such a file is never "empty". Budgets: a file above `MaxFileBytes` (16 MiB) is not read
  and listed in `NotRead`; once a GPO's read bytes reach `MaxGpoBytes` (64 MiB) its remaining files go to `NotRead`;
  once the run reaches `MaxTotalBytes` (1 GiB read in total) the GPOs not started yet are `Skipped` (nothing read).
  Bytes are handed to the caller **one GPO at a time** through the streaming overload `ReadAsync(…, onRead, …)`: the
  callback runs for each GPO as soon as its folder is read (at most `Parallelism` callbacks at a time, each awaited
  before that worker takes the next GPO), and the caller parses and drops the bytes inside it. The dictionary overload
  (all results at once) is for diagnostics (`Full`, as today) and tests only; the index never uses it, so the WPF process
  holds at most `Parallelism` folders of raw bytes at a time, never the whole domain.

**Overflow fix.** `GroupPolicyReader.Settings` creates one `GpoSettingSink(path)` per file (capacity
`GpoSettingSink.DefaultCapacity` = 10 000) and never looks at `Overflowed`: a `Registry.pol` with more entries loses
settings silently. New `GroupPolicyReader.ReadSettings(content) → GpoSettingsRead(Settings, OverflowedFiles)`;
`Settings(content)` returns `ReadSettings(content).Settings` (unchanged output for K5/K6). The index marks a GPO with an
overflowed file `Incomplete` (search: "nie wszystkie ustawienia odczytane"; cleanup: no content-based proposal; copy:
units of that GPO refused, 21.5).

**Preference items with their collections.** `GppXmlParser.Items(XDocument)` returns, in the order and with the ids
`Parse` uses today, each item as `GppXmlItem(ItemId, Element, Collections)`, where `Collections` are the item's ancestor
`Collection` elements (outermost first). GPMC lets an operator disable a collection or give it item-level targeting, and
both apply to every child; 21.2 and 21.5 use this. `Parse` iterates `Items` and its output stays byte-for-byte the same
(the Watchdog persists these keys): W1 adds a golden test (uid-less items, items nested in collections, collection
pseudo-items with a `uid`) that pins the keys before and after the refactor, and runs `tests/AdMng.Watchdog.Core.Tests`
and `tests/AdMng.Watchdog.Service.Tests`.

**Directory-side settings for all GPOs at once.** `CountDirectorySettingsAsync(session, gpoId, ct)` costs two searches
per GPO. New overload `GroupPolicyReader.CountDirectorySettingsAsync(session, ct) → IReadOnlyDictionary<Guid, int>`:
one paged subtree search of `CN=Policies,CN=System,<domain>` (attribute `objectClass`, filter `LdapFilter.Present(
Attr.ObjectClass)`), counting per GPO the objects whose parent is the GPO's `CN=Machine` or `CN=User` (DNs parsed with
`DistinguishedName`; same semantics as the per-GPO method). A GPO absent from the map has 0.

### 21.2 Core – setting units (W1)

A **setting unit** is what GPMC shows as one row and what a person means by "the same setting in another GPO". The
normalized keys are too fine (one ADMX policy = several `Registry.pol` values, one preference item = many keys, a
service = two keys) and partly per-GPO (preference items keyed by their `uid` or positional `<Element>#n`, scripts by
index). The presenter's buckets cannot be reused: its `K|` bucket is a counter (`GpoSettingPresenter.Rows.cs`), the `S|`
bucket an ini index, and `GpoSettingText` exposes no id. `GpoSettingUnits.Of(...)`
(`src/AdMng.Core/GroupPolicy/Search/GpoSettingUnits*.cs`, pure) builds the units of one GPO from its normalized settings
and its two parsed `Registry.pol` documents.

**Identity.** `BaseId` is the same in every GPO; ids compare with `StringComparer.OrdinalIgnoreCase` everywhere.
`Id` is unique within one GPO: `BaseId` for the first occurrence, `BaseId + "#" + n` for the n-th (n ≥ 2, file order),
e.g. two items mapping K: in one GPO.

| Kind (`GpoSettingUnitKind`) | Settings (areas of `GpoSettingKey.Parse`) | `BaseId` |
|---|---|---|
| `AdministrativeTemplate` | the `Registry.pol` values a configured policy owns (`AdministrativeTemplatePolicies.Configured`, `ConfiguredPolicies.Covers(key)`); only with a definition set | `P\|Machine\|<ns:name>` / `P\|User\|<ns:name>` (`PolicyDefinition.Id`) |
| `RegistryValue` / `RegistryKey` | `PolicyRegistry` keys not covered by a policy (values incl. `DELETE`/`SOFT:`; key-level `**delvals.`, `CREATE_KEY`, `DELETE_KEY`) | `K\|<setting key>` |
| `SecurityValue` | `SystemAccess`, `KerberosPolicy`, `EventAudit`, `EventLog`, `SecuritySection` (`key = value` lines) | `K\|<setting key>` |
| `UserRight` | `UserRight` | `K\|<setting key>` (`…/Privilege Rights/SeRemoteInteractiveLogonRight`) |
| `RestrictedGroup` | `RestrictedGroup` (`…/Group Membership/<group>/Members` or `/MemberOf`) | `G\|<group SID, else upper-case name>\|Members` / `…\|MemberOf` (group normalized with `GpoPrincipalResolver.KnownSid`, else `GpoUnitContext.SidsByName`) |
| `SecurityRegistryValue` | `SecurityRegistryValue` | `K\|<setting key>` |
| `Service` | `Service` (`…/Services/<name>/StartupMode` + `/Security`) | `O\|Computer/Security/Services/<name>` |
| `ObjectSecurity` | `RegistryKeySecurity`, `FileSecurity` (mode + SDDL of one path) | `O\|Computer/Security/<Registry Keys or File Security>/<path>` |
| `AuditSubcategory` | `AdvancedAudit` | `A\|<catalog GUID>` (`AuditSubcategoryDefinition.Guid`; name-only rows through the catalog; unknown → `K\|<key>`) |
| `AuditOption` | `AuditOption`, `GlobalSacl` | `K\|<setting key>` |
| `Preference` | every key of one item (`GpoSettingKeyInfo.ItemKey`) | `I\|<Computer or User>\|<Type>\|<element>\|<natural identity>` |
| `Script` | `Script` command line + `/Parameters` | `S\|<Computer or User>\|<cmd or ps>\|<phase>\|<command line>\|<parameters>` |
| `ScriptConfiguration`, `ScriptFile`, `Comment`, `UnreadableFile`, `Other` | one key each | `K\|<setting key>` |

Natural identity of a preference item (attributes of `Properties` as the parser emits them, trimmed; `<element>` is the
item's `@element`, e.g. `Drive`, `Group`, `User`, `ImmediateTaskV2`):

| Type | Identity |
|---|---|
| Drives | `letter` (with `useLetter` = 0: `path`) |
| Groups | `Group`: `groupSid`, else `groupName`; `User`: `userName` |
| Registry | `hive` + `key` + `name` |
| Services | `serviceName` |
| Files / Folders / Shortcuts | `targetPath` / `path` / `shortcutPath` |
| Printers | `path` (else `localName`, else `ipAddress`) |
| EnvironmentVariables | `name` + `user` |
| ScheduledTasks | `name` |
| IniFiles | `path` + `section` + `property` |
| NetworkShares | `name` |
| any other type, or an attribute missing | `@name` (else the item id – then the unit is `Ambiguous`) |

`Collection` elements that carry a `uid` (pseudo-items with only `@element`/`@name`) are structure, not units – but their
state is inherited (`GppXmlItem.Collections`, 21.1): an item below a collection with `Filters` is `Conditional`, and an
item below a collection with `disabled="1"`, or with `disabled="1"` itself, has `Disabled = true` (it does not apply: never
a duplicate, never "the value" of a search group, but it still keeps its GPO from being "empty" – deleting dormant content
is not a cleanup the advisor proposes). The
private `PreferenceItem(key)` copies in `GroupPolicyModeler` and `GroupPolicyDiagnostics.Conflicts` are replaced by the
public `GpoSettingUnits.ItemKeyOf(key)`, which returns exactly what they return today (the per-GPO item prefix): K5 and
K6 keep their per-key semantics – using natural identity in the modeller would change their results and is a later
option, not K7.

**Value.** `ValueFingerprint`: for policies `PolicySetting` equality is used instead (`GpoSettingUnits.SameValue`); for
`UserRight` / `RestrictedGroup` the sorted set of normalized principals (SID where known, else the upper-case name as
written); for everything else the sorted lines `<key suffix below the unit>=<canonical value>` of its settings (the item
prefix with its `uid` stripped; `@changed` left out). `Hash` values (> 200 characters) compare by hash – equality only.
`Comparability`: `Masked` (any setting of the unit is `GpoSettingKind.Masked`: `cpassword`, `DefaultPassword`, SNMP
communities, hidden value names), `Conditional` (a preference item with `Filters` – item-level targeting), `Cumulative`
(scripts), `Unreadable` (unreadable-file marker), `Ambiguous` (identity fell back to the item id), `Truncated`
(`GpoSettingKey.IsTruncated`), else `Comparable`. `SameValue(a, b)` is false unless both are `Comparable` and neither is
`Disabled` – masked values are never equal. `FiltersFingerprint` (the canonical `Filters` of the item and of its
collections, empty without targeting) lets the planner tell conditional variants of one preference identity apart (21.5).

**Copyability.** `Refusal` (null = copyable and removable): `Secret` (masked), `Script` (scripts, script config, script
files), `ObjectSecurity`, `AuditOption` (options and global SACL rows), `Comment`, `Unreadable`, `Truncated`,
`Unsupported` (`Other`, `Ambiguous` preference items), `ScopedCollection` (a preference item below a `Collection` with
`Filters` or `disabled="1"`: copying it to the target's root would drop the collection's targeting or disabled state, so
it is refused; items below plain collections are copied to the root). Per-GPO reasons (incomplete read, directory-side
settings) and plan-time reasons (missing extension pair, 21.5) are issues, not unit properties.

**Counting.** `CountsAsSetting` is false for `Comment`, `UnreadableFile` and `ScriptFile` (a script payload is not a
setting; its command line is the `Script` unit, and GPMC leaves orphaned files behind when a script entry is removed).
`BlocksMerge` = (`CountsAsSetting` and `Refusal` is not null) or `Kind == UnreadableFile`: comments and script files never
block a merge, they are simply not carried (21.5).

**Principals.** `Principals` (as written) and `PrincipalSids` (normalized, upper case) of user rights, restricted groups
(group and members), GPP groups (`groupSid`, member `sid=` / `name=`) and item-level targeting `sid=`. Names without a SID
are resolved **before** the units are built, so that a name and a SID of the same account compare equal everywhere:
`GpoSettingUnits.PrincipalNames(settings)` lists the names `KnownSid` cannot map, the caller resolves them in one batch
(`GpoPrincipalResolver.ResolveAsync`) and passes the map as `GpoUnitContext.SidsByName`; `Of` then uses it for the
`RestrictedGroup` `BaseId` (`G|<SID>|Members` for both `CONTOSO\GG_Admins` and `*S-1-5-21-…`), for the user-right and
restricted-group fingerprints and for `PrincipalSids`. The index builder does this in two passes (21.3), the planners for
the source and target in one batch (21.5). Without a map (no LDAP, a test) names stay as written, upper case.

### 21.3 Core – the index and the account resolver (W1); Presentation – the index service (W1)

**`GpoSettingsIndexBuilder.BuildAsync`** (`src/AdMng.Core/GroupPolicy/Search/GpoSettingsIndexBuilder.cs`), all on the
session's DC: (1) `GroupPolicyReader.ReadPoliciesAsync`, `ReadLinkingContainersAsync` (domain head, OUs, **sites**) +
`LinksByGpo`, `ReadOtherDomainsAsync`, the domain SID (`session.GetAsync(domainDn, [Attr.ObjectSid])`), the new
directory-settings count (21.1); (2) `WithSysvolVersionsAsync` (GPT.INI); (3) the streaming
`GpoFolderReader.ReadAsync(ContentOnly, onRead)` for the GPOs that need a read; inside the callback, per GPO,
`ReadSettings`, `RegistryPolicyDocument.Parse` of both halves and `GpoSettingUnits.PrincipalNames`; the bytes are dropped
when the callback returns (no `GpoIndexEntry` keeps a `byte[]`); (4) the collected names through
`GpoPrincipalResolver.ResolveAsync` (one batch for the domain); (5) per entry `AdministrativeTemplatePolicies.Configured`
(with a definition set) and `GpoSettingUnits.Of` with `SidsByName` – pure, no I/O. Progress `GpoIndexProgress(Stage,
Done, Total)`, stages `Directory, Versions, Folders, Principals, Units, Completed`. Without a SYSVOL connection the index
holds directory data only and every entry is `NotRead`. The resolved map is kept (`GpoSettingsIndex.SidsByName`) so that
`WithDefinitions` rebuilds units without LDAP.

**Incremental refresh.** With `request.Previous` (same `Server`, same `Definitions` reference) an entry is reused when
its GPO still exists and `(DirectoryVersion, SysvolVersion)` are equal and its status was `Read` or `Incomplete`; only
the others are read again, deleted GPOs drop out, `GpoInfo`, links and directory-settings counts always come from the
fresh LDAP read. `GpoSettingsIndex.WithDefinitions(set)` re-builds units and configured policies from the kept
`Registry.pol` documents without any I/O (ADMX set loaded later or another culture).

**Status per GPO** (`GpoIndexStatus`): `Read`; `Incomplete` (a budget left content files unread, or a sink overflowed);
`NotFound`, `AccessDenied`, `TooLarge`, `Failed` (from the reader); `NotRead` (no SYSVOL, or the run budget was used up).
GPOs the GPO search does not return (no read access) are not in the index at all; the UI says how many GPOs it saw.

**`GpoPrincipalResolver`** (`src/AdMng.Core/GroupPolicy/Search/GpoPrincipalResolver.cs`): `KnownSid(principal,
domainSid)` without LDAP – SID text with or without `*` (`SecurityTemplate.Principal`, `SecurityTemplate.IsSid`), the
en/pl well-known names, `BUILTIN\…`, local groups and the domain RIDs 500/512/513/515 (wraps the internal
`GpoPrincipals.SidOf`); `ResolveAsync(session, principals, domainSid, ct)` for the rest: `DOMAIN\name` (only when
`new GroupPolicyDomainNames(session.Info.DomainDnsName, session.Info.DomainNetBiosName).IsThisDomain(domain)`, as
`GroupPolicyGrantSource` does – a NetBIOS name that differs from the first DNS label is recognized), UPN (`name@suffix` →
`Attr.UserPrincipalName`), bare names (`Attr.SamAccountName`), 50 names per OR search
(`LdapFilter.And(LdapFilter.Present(Attr.ObjectSid), LdapFilter.Or(… LdapFilter.Equal(…)))`, rule 3), base the domain
NC. Unresolved names come back as `Unresolved` (never guessed). The private resolver of `GroupPolicyGrantSource` (U4c)
is left as it is.

**Presentation – `GpoSettingsIndexService`** (`src/AdMng.Presentation/GroupPolicy/Search/`, **Scoped**, one per
connection window, modelled on `PolicyDefinitionsService`): holds the current `GpoIndexView` (index + SID names +
culture), keyed on the `IDirectorySession` instance (`ReferenceEquals`) **and** `session.Info.Server` – a reconnect to the
same DC with other credentials is a new session, so a view read under another security context is never returned, whatever
the order of the `SessionChanged` handlers; a generation counter so that a build started before `Invalidate` never becomes
current. `GroupPolicyServices.Changed` only marks the view stale (`IsStale`, `StateChanged`) – no automatic rebuild;
`ISessionManager.SessionChanged` (another DC, reconnect, disconnect) drops it (`Invalidate`); both handlers are removed in
`Dispose`.

*The service owns every build* (the documents' connections and tokens never reach it): a build opens its own SYSVOL
connection with `GroupPolicyServices.OpenSysvolAsync(session, …)` and disposes it when the build ends (the unavailability
reason goes into the view), and runs on a `CancellationTokenSource` owned by the service and linked to
`GroupPolicyServices.Lifetime`. That source is cancelled by `Invalidate`, `Dispose`, `CancelBuild()`, or when the **last**
waiting caller has cancelled. A caller's token only ends that caller's wait (`Task.WaitAsync(token)`), so *Anuluj* in the
search tab never cancels the build the cleanup tab still waits for, and closing a tab never leaves the build on a disposed
connection. Progress goes to every caller through the `Progress` property and `StateChanged` (not per call). A request
during a running build: `IfMissing` awaits it; `Incremental` or `Full` queues **exactly one** follow-up build (the stronger
of the queued requests) that starts when the running one ends.

`GetAsync(refresh, ct)`: `IfMissing` returns the current view when it fits (same session, same culture; a different
culture only re-resolves names and definitions – no GPO folder read), `Incremental` and `Full` rebuild. Order inside a
build: definitions first (`Definitions.GetAsync(session, sysvol, Localizer.Culture, reload: false, ct)`, cached per window;
a failure → no set and `DefinitionsFailed`), then the Core build on `Task.Run`, then `TrusteeNames.ResolveAsync` over the
union of the entries' `GpoSettingPresenter.Sids(settings)` and `PrincipalSids`, then `GpoIndexTexts` for the view (also on
`Task.Run`, once per view).

*DI* (done by the K7 contract commit, not by a wave agent): the concrete class `TryAddScoped`, the interface forwarded to
the same instance (`sp => sp.GetRequiredService<GpoSettingsIndexService>()`), the `GroupPolicyViewModelFactory` lambda
passes `sp.GetRequiredService<IGpoSettingsIndexService>()`, both types are in `ConnectionLifetimes`. The factory's fallback
(`new GpoSettingsIndexService(services)` when no instance is passed) is for test harnesses only; the factory disposes it
only when it created it. W1 adds a `HostTests` check that `factory.Index` is the scoped instance.

**`GpoIndexTexts`** (Presentation): one `GpoSettingPresenter` per entry (`ComputerPolicies` / `UserPolicies` from the
entry, `ItemNames = GpoSettingPresenter.ItemNamesOf(entry.Settings)`, the view's SID names and definitions), cached per
view (built once per view on `Task.Run`, never per keystroke); `Describe(entry, unit)` = the single row
`DescribeRows(unit.Settings)` yields (units are built so that the presenter collapses them into one row – a test pins this
per kind).

**Budgets** (500 GPOs × ~200 units ≈ 100 000 records): the Core build of 500 synthetic GPOs on `FakeSysvol` makes at most
4 concurrent SYSVOL calls and keeps no `byte[]` (W1 test); a matcher pass over 100 000 records stays well under 500 ms
(W4 test with a generous `Stopwatch` bound); an empty or one-character query shows only the summary (counts per area, notes),
never the 100 000 rows; at most 2 000 groups are shown, with *Zawęź wyszukiwanie – pokazano 2 000 z N grup*.

### 21.4 Search – "Wyszukiwarka ustawień GPO" (W4)

Document `GpoSettingsSearchDocumentViewModel` (`GroupPolicyDocumentKind.SettingsSearch`, one tab per window,
`GroupPolicyDocumentBase`, `ISearchableDocument`), title *Wyszukiwarka ustawień GPO*. Load = `IGpoSettingsIndexService.
GetAsync(IfMissing)` with progress and *Anuluj* (the previous results stay); a change elsewhere sets the stale bar
(*Zmieniono zasady grupy – Odśwież*), F5 / *Odśwież* = `Incremental`, *Odczytaj wszystko ponownie* = `Full`; the
base's reload on `Changed`, `SessionChanged` and culture is overridden as in diagnostics (a `_requested` flag; culture
change re-renders texts from memory).

**Matching** (`GpoSettingsMatcher`, on `Task.Run`, 250 ms debounce, cancellable): one record per (GPO, unit) with the
presenter's `Name`, `GroupPath`, `ValueText`, `Elements`, `Technical`, the unit's keys and `BaseId`, the principals'
resolved names and SIDs. Field selector *Wszystko / Nazwa / Wartość / Klucz techniczny / Konto*; text compared with
`CompareOptions.IgnoreCase | IgnoreNonSpace` (as `GpoSettingRowViewModel.Matches`). Values of `Masked` units are never
matched (only their names and keys); `Konto` resolves the typed text with `GpoPrincipalResolver` (exact SID) and also
matches resolved names by substring. Facets: scope (computer/user), area (administrative templates, security,
audit, preferences by type, scripts, other), *Tylko różne wartości*.

**Results** grouped by `BaseId`: the group header is the unit's name and path plus *ustawione w N GPO – wartości
zgodne / różne / nieporównywalne* (`SameValue` over the occurrences that are not `Disabled`); rows are GPO name, value,
links text, status marks (conditional, disabled item, masked, incomplete GPO). Empty or one-character query: summary only;
more than 2 000 groups: the first 2 000 and the *Zawęź wyszukiwanie* note (21.3 budgets). Grouped `ListView` with `VirtualizingPanel.IsVirtualizingWhenGrouping`.
Notes bar: GPOs not read (count, names in the tooltip, per `GpoIndexStatus`), ADMX not loaded (registry names only,
policies not searchable by name), masked values not searchable, SYSVOL unavailable, links from other domains not visible.

**Row actions:** *Otwórz GPO* (`Actions.OpenGpo(id, name, GpoDocumentTab.Settings)`), *Edytuj to ustawienie*
(`Actions.EditSetting(id, name, text.Focus)` when `GroupPolicyActions.CanEditSetting`), *Porównaj GPO* (two selected
rows, or one row and a picked GPO: `Actions.CompareAsync`), *Kopiuj do innego GPO…* (W5's `CopySettingsAsync`, 21.5,
for the selected rows; refused units shown disabled with the reason from `GpoUnitRefusalText`, which the K7 contract
commit provides with the `GpoCopy.Refusal.*` texts), *Kopiuj* (text), *Eksportuj CSV / HTML* (the
current results to an operator-chosen file: `ICsvCodec` with `protectFormulas: true`, `CsvFiles`; HTML escaped).

**Entry points:** palette `gpo.searchSettings` (*Wyszukaj ustawienie we wszystkich GPO…*, category Tools), *Akcja →
Zasady grupy*, the tree menu of the GPO root, a button on the GPO list toolbar and *Znajdź we wszystkich GPO* on the
GPO document's *Ustawienia* tab, which opens the document with `GroupPolicyDocumentRequest.SearchText` = the tab's search
text (else the selected row's name); an open search tab receives the text through the shell (`ISearchableDocument.
SearchText`).

### 21.5 Copy, remove and merge – Core planners (W2), Presentation actions (W5)

**One plan for the target.** `GroupPolicyPlanner.PlanCopySettingsAsync` (new partial
`GroupPolicyPlanner.CopySettings.cs`, so the private `Plan`, `RequirePolicyAsync`, `PolicyObject`, `ContainerObject`,
`Modify`, `AddIfChanged`, `GpLinkChange`, `RegistryCommands`, `ExtensionNames` stay usable) produces **one** sequential
`ChangePlan` with **one** `ObjectPlan` for the target GPO: one `Modify` of `gPCMachineExtensionNames` and/or
`gPCUserExtensionNames` (only when they change), then one `WriteGpoFile` per touched file (`OldContent` = the bytes read
now, `null` for a new file; `NewContent` = the edited document), then **one** `VersionChange(gpo.DirectoryVersion,
GptVersionAsync(...), user: any User/ file written, computer: any Machine/ file written)` with the "part disabled"
warnings of K3/K4. `BatchExecutor` runs the content steps as one `GpoTransaction` (compare-and-swap on every file and on
both versions); `RollbackPlanner` gives undo. The existing `PlanAdministrativeTemplatesAsync` and
`PlanSecuritySettingsAsync` are not chained (two `ObjectPlan`s for one GPO would bump twice and make the second version
step stale).

**Fresh reads, never index values.** Sources and target are read at planning time on the session's DC with
`GroupPolicyReader.ReadFolderAsync(sysvol, id, ct)` (the `ISysvolWriteConnection` `GroupPolicyActions.RunAsync` opens;
`GpoTooLargeException` / `SysvolWriteException` → an error the UI shows). The names of every source and the target are
resolved in **one** `GpoPrincipalResolver.ResolveAsync` batch (`GpoSettingUnits.PrincipalNames`), then both sides are
unitized with `GpoSettingUnits.Of(content, new GpoUnitContext(request.Definitions, domainSid) { SidsByName = … })`, so a
restricted group written by name in one GPO and by SID in the other is one `BaseId` and the writer removes the target's
lines for **any** spelling of that group. A requested unit is found by `Id` in the fresh source units; for a preference
unit, or any unit with `Occurrence` > 1, `GpoUnitSelection.ItemId` must match as well (positions shift when another item
is added) – otherwise `NotFound` (`GpoSettingNotFound`); when the fresh `ValueFingerprint` differs from the selection's
(index-time) one the copy goes on with the fresh value and adds the warning `GpoSettingChanged` (the preview shows the
value that will be written). A unit no longer there, a source with an overflowed file (`ReadSettings`) or a folder with
leftovers is an issue, never a guess. Every new byte comes from the source document read now, through the per-kind writer
below. At most `GpoSettingsCopyRequest.MaxSources` (10) source GPOs per plan (more → error `GpoCopyTooManySources`; the
copy window says so), so planning stays a few folder reads.

**Definitions.** Copy, remove and merge get the definition set the units were built with – `IGpoSettingsIndexService.
Current.Index.Definitions` – never a fresh `Definitions.GetAsync` (another culture or the local store would turn a `P|`
unit into `K|` units and give `NotFound`). An ADMX unit without a set is refused (`GpoSettingNoDefinitions`).

**Per kind** (internal `GpoSettingsWriter`, `src/AdMng.Core/GroupPolicy/Management/GpoSettingsWriter.cs`):

| Kind | Copy into the target document | Remove |
|---|---|---|
| `AdministrativeTemplate` | `PolicyEngine.Write(definition.Policy, setting, target, PolicyOwnershipIndex.Build(set).OwnedNames)`; state `Inconsistent` (`Write` throws): forget the target's owned entries of that policy (its `ConfiguredPolicy.Entries` when the target configures it), then insert the source's `ConfiguredPolicy.Entries` in file order, with the key-level ordering rule below | `PolicyEngine.Write(…, PolicySetting.NotConfigured, …)`; `Inconsistent`: remove its `ConfiguredPolicy.Entries` |
| `RegistryValue` / `RegistryKey` | forget the target's entries of that value (or key-level entry), then add the source's raw `RegistryPolEntry` records unchanged (`**del.`, `**soft.`, `**delvals.`, `**DeleteValues`/`**DeleteKeys` forms kept); a key-level delete entry is **inserted before the first target entry of that key and before any value copied for that key in this plan** (Registry.pol is processed in file order – appended at the end it would clear values the target, or this plan, set earlier); a raw entry the target's `ConfiguredPolicies` covers (a configured policy owns that value or list key) is refused with `GpoSettingPolicyOwned` – copying it would silently change the target's policy or make it `Inconsistent` | forget them |
| `SecurityValue`, `SecurityRegistryValue` | `SecurityTemplateDocument.Set(section, key, source.Get(section, key))` (`SetRegistryValue` for `[Registry Values]`) | `Remove(section, key)` / `SetRegistryValue(path, null)` |
| `UserRight` | `SetPrivilegeRight(right, principals)`; Union: union of both lists (de-duplicated by resolved SID, else name) | `SetPrivilegeRight(right, null)` |
| `RestrictedGroup` | remove the target's lines of that group under every spelling (resolved SID), then `SetRestrictedGroup(group, relation, principals)`; Union as above | `SetRestrictedGroup(group, relation, null)` for every spelling |
| `Service` | `SetService(name, source service)` | `SetService(name, null)` |
| `AuditSubcategory` | `AuditPolicyDocument.SetSubcategory(guid, name, value)` | `SetSubcategory(guid, name, null)` |
| `Preference` (any type) | clone the item's `XElement` (located with `GppXmlParser.Items`), new upper-case `uid` in braces, `changed` = the planner's `TimeProvider` local time `yyyy-MM-dd HH:mm:ss`, appended at the end of the target's root (applied last); a missing target file gets the source file's root element name and `clsid`; an item below a plain `Collection` is copied to the root; an item below a collection with `Filters` or `disabled="1"` is refused (`ScopedCollection`, 21.2) | remove the item's element (an emptied collection stays, as in GPMC) |

GptTmpl writes keep every other line (K4's minimal-edit rules); a new GptTmpl / audit.csv starts from
`SecurityTemplateDocument.New()` / `AuditPolicyDocument.New()`. Preferences use a new generic
`GppPreferenceDocument` (Parse, `Items` in parser order, `Append(XElement, DateTime)`, `Remove(itemId)`, `ToBytes()` in
`GppGroupsDocument`'s format: UTF-8 with BOM, declaration, no indentation); `GppGroupsDocument.Add` is not used (it
rebuilds the element and drops item-level targeting and common options).

**Conflicts** (the target already has units with the same `BaseId`): `Replace` (default; the target's occurrences are
removed, then the source unit is added), `Skip` (outcome `Skipped`, info issue `GpoSettingSkipped`), `Union` (user rights
and restricted groups; `Replace` for every other kind). Per-unit overrides in `ConflictByUnit`. Units are applied in
request order; the same `BaseId` requested from two sources is refused for both (`GpoSettingAmbiguous`) – no silent "last
wins". Refinements:

* **Unchanged.** `Replace` where the target has exactly one occurrence and `SameValue(source, target)` is a no-op:
  outcome `Unchanged`, no file write, no new `uid`, no version bump when nothing else changes.
* **Conditional preference variants.** The natural identity deliberately groups several items (GPMC configurations often
  hold several *K:* items with different item-level targeting). `Replace` removes only the target occurrences whose
  `FiltersFingerprint` equals the source's (both empty included) and keeps the others; when a conditional target
  occurrence is kept the outcome is `Added` and the info issue `GpoSettingConditionalKept` names the unit.
* **removePolicy.** Removing or replacing a preference item with `removePolicy="1"` (*Usuń element, gdy nie jest już
  stosowany*) makes clients undo it – drop the mapped drive, delete and recreate the local user or group (new SID, lost
  profile) – before the copy (new `uid`) applies again: the warning `GpoSettingRemovePolicy` names the unit, in copy,
  remove and merge plans alike.

**Refused** (outcome `Refused`, warning issue, the rest of the plan goes on): any `Refusal` of the fresh unit (secrets
and `cpassword` items – MS14-025 –, scripts and script files, `[Registry Keys]` / `[File Security]`, audit options and
the global SACL, comments, unreadable or truncated units, `Ambiguous` items, items of a scoped collection); an ADMX unit
without a definition set; a raw registry entry a target policy owns; a source read incomplete; directory-side settings are
never units (a source with `CountDirectorySettingsAsync` > 0 adds a warning that they are not copied, as K2's copy does
with `IssueCodes.GpoDirectorySettings`).

**Source half disabled.** A unit of a half the source GPO has disabled (`GpoStatus` `UserSettingsDisabled`,
`ComputerSettingsDisabled`, `AllSettingsDisabled`) is dormant there and becomes active in the target: an explicit copy goes
on with the warning `GpoSettingSourcePartDisabled`; a merge leaves such units out (outcome `Skipped`, same code as info).

**Extension names.** Registry.pol: `GpoExtensionNames.Registry` + the administrative templates tool of the half plus
the copied policies' ADMX `clientExtension`s (the logic of `AdministrativeTemplatesEditorViewModel.ClientExtensions`
moves to Core as `PolicyClientExtensions.Of(document, set, scope, ownership)`; the K3 editor keeps its copy in K7);
GptTmpl, audit.csv and machine Groups.xml: the pairs of `GroupPolicyPlanner.KnownExtensionFiles`. Every other preference
file (and user-side Groups.xml): the CSE is `GpmcBackupFormat.TryPreferenceExtension(type)` – **null for a type the table
does not know** (the old private mapping falls back to the Registry CSE, which clients would ignore for that file), and
then the unit is refused (`GpoSettingExtensionUnknown`); the tools are those the **source** lists for that CSE in its
extension names of the half (`GpoExtensionNames.Parse`), each added with `GpoExtensionNames.With` for the CSE and for
`GpoExtensionNames.PreferencesCore` when the source lists the tool there. A source without that CSE: when the target
already lists the CSE for the half the copy goes on, otherwise the unit is refused (`GpoSettingExtensionUnknown`) – the
tool GUIDs are not guessed (the code has them for Local Users and Groups only). Names are never removed (GPMC keeps them
too).

**Account policy.** Copying an account-policy unit into a GPO not linked to the domain head adds a warning with two
variants: `GpoAccountPolicyBelowDomain` for password, lockout and forced-logoff settings (`[System Access]` names in
`GroupPolicyModeler.AccountPolicyNames` – they then apply to the computers' local accounts only) and
`GpoKerberosPolicyBelowDomain` for `[Kerberos Policy]` (no effect at all outside the domain head).

**Remove** (`PlanRemoveSettingsAsync`): the same reads and writers on one GPO, one `ObjectPlan`, one version bump; the
same refusals and the `GpoSettingRemovePolicy` warning. Used by the cleanup advisor (*Usuń duplikat z tego GPO*).

**Unlink everywhere** (`PlanUnlinkAllAsync`): every container of the domain and every site that links the GPO loses the
link, one `gPLink` compare-and-swap per container (the first part of `PlanDeleteAsync`, without the delete); the
`GpoLinkedElsewhere` warning when the forest has other domains.

**Delete with an expected state** (new overload `PlanDeleteAsync(session, gpoId, GpoExpectedState expected, ct)`): the
advisor's *Usuń GPO…* passes the `DirectoryVersion` and linked containers the index saw. When the fresh read differs (the
GPO gained settings or links in another window or from another administrator since the analysis) the plan carries the
error `GpoChangedSinceAnalysis` and cannot run; the UI then refreshes the index incrementally. A delete is the one
cleanup action that cannot be undone (`RollbackPlanner`: `Delete` / `DeleteGpoFolder` are `NotReversible`), so this guard
comes in addition to the backup offer of `GroupPolicyActions.DeleteAsync`.

**Merge A into B** (`PlanMergeAsync`; **the last milestone of W2 and of W5** – if it is not ready the release ships
without it and 21.9 lists it as K7.1): **one** sequential plan –

1. B's `ObjectPlan` = the copy of every unit of A that `CountsAsSetting` (fresh read; units of A's disabled half
   skipped, see above; comments and script files not carried, info issue `GpoMergeNotCarried`);
2. per container linking A (`ReadLinksOfAsync` + `ReadContainerAsync`, as `PlanDeleteAsync`): one `gPLink`
   compare-and-swap that puts B where A was with A's options (`GpLinkValue.Remove(A)` then `Insert(B, options, order)`) or,
   when B is already linked there, only removes A (B's link and options kept – the warning below says when they are
   weaker);
3. A's `ObjectPlan`: `flags` → `GpoStatus.AllSettingsDisabled` (as `PlanStatusAsync`).

*Errors* (on A; the plan cannot run): any unit with `BlocksMerge` (the issue `GpoMergeNotCopyable` gives the count, the
UI lists them); A holds directory-side settings (`CountDirectorySettingsAsync` > 0: `IssueCodes.GpoDirectorySettings` at
severity `Error`); A's fresh folder holds files below `Machine/` or `User/` that no unit covers (inventory files such as
folder redirection or software installation, classified as `GpoFolderReader` lists `OtherFiles`: `GpoMergeUnparsedFiles`
with the count); A or B read incomplete; A is a default GPO (`IssueCodes.DefaultGpo`); A = B; the link order is mixed and
the request names no conflict mode (`GpoMergeOrderMixed`, below).

*Precedence.* From the fresh `gPLink` of every container that links both, A's and B's order (`GpLinkValue`, order 1 =
highest precedence) and options are compared. `GpoMergeRequest.Conflict` = null (default) derives the conflict mode:
`Skip` when B precedes A in every shared container (B's winning values stay), `Replace` when A precedes B everywhere
(A's winning values move into B), and when the order is mixed an explicit mode is required (`GpoMergeOrderMixed`). With no
shared container the mode defaults to `Replace`.

*Warnings*: security filtering differs (`GpoMergeSecurityFilterDiffers`: `GpoInfo.SecurityFilter` `Allowed`/`Denied`
sets); WMI filter differs (`GpoMergeWmiFilterDiffers`: `WmiFilterId`); the link sets differ (`GpoMergeLinksDiffer`, with
the containers only A links – they start getting B's other settings – and the containers only B links – they start
getting A's settings; names only, no values); in a shared container A ranks above B, or A's link is enforced or enabled
where B's is not (`GpoMergeLinkOptionsDiffer`, per container: A's settings move down to B's position or lose
enforcement); other domains in the forest (`GpoLinkedElsewhere`: their links to A are not replaced); B's part disabled
where A has settings (`GpoComputerPartDisabled` / `GpoUserPartDisabled`); `GpoSettingRemovePolicy` for items of A with
`removePolicy="1"` (A is disabled, so clients undo them before B's clones apply). Moving B's link to A's position is not
done (21.9).

Delete of A is offered separately afterwards through the existing `GroupPolicyActions.DeleteAsync` (backup first): a
`Delete` is not reversible and rollback would drop `gPLink` values naming A. If a later step fails after B's transaction
committed, the preview's undo restores B (files, version bump again), the links and A's status – all reversible.

**PowerShell** of these plans: `Get-GPO` with the comment that the plan writes SYSVOL files (the GroupPolicy module has
no cmdlet, as K4), `Set-GPRegistryValue` / `Remove-GPRegistryValue` for Registry.pol changes (`RegistryCommands`),
`New-GPLink` / `Remove-GPLink` for the link steps and `(Get-GPO …).GpoStatus = AllSettingsDisabled` for A.

**Presentation (W5).** Extension methods in `src/AdMng.Presentation/GroupPolicy/Copy/GpoSettingsActions.cs` take
display items (`GpoCopyItem`: selection + source name + name/path/value text + refusal) so that callers (search, cleanup)
need no shared state: `CopySettingsAsync` (target from `GpoPickerViewModel`, single select, sources excluded; then the
*Kopiuj ustawienia* window `GpoCopySettingsViewModel` – items with refusals shown disabled, conflict mode *Zastąp /
Pomiń / Połącz listy*, at most 10 source GPOs), `RemoveSettingsAsync`, `MergeAsync` (same window in merge mode: A, B, the
derived conflict mode or the operator's choice when the order is mixed, the units that block the merge, the
filtering/WMI/links warnings), `UnlinkAllAsync`, `DeleteCheckedAsync` (advisor delete with `GpoExpectedState`; W5 adds
the optional `GpoExpectedState? expected` parameter to `GroupPolicyActions.DeleteAsync`). All plan through
`GroupPolicyActions.RunAsync(title, SysvolUse.Required, …)` with the index's definition set. **Nothing to do:** when the
plan's outcomes hold no `Added`, `Replaced`, `Merged` or `Removed` (every unit refused, skipped or unchanged), the actions
do not show RunAsync's "nothing to do" toast but an error dialog listing the units with their `GpoUnitRefusalText` /
issue texts – the MS14-025 refusal is never swallowed. **Zmiany ustawień:** `RunAsync` gains an optional last parameter
`describeChanges` (called after planning when `settingChanges` is null; a failure only drops the rows);
`GpoSettingChangesBuilder` parses the old and new bytes of every `WriteGpoFile` step (`GroupPolicyReader.ReadSettings`
of a one-file `GpoFolderContent`), groups the changed keys by unit (`GpoSettingUnits.Of` on both sides), describes
before/after with a `GpoSettingPresenter` per side (policies from both Registry.pol versions when a set is loaded, SID
names from one `TrusteeNames.ResolveAsync`), masked values stay masked; `Group` = the `ObjectPlan`'s GPO name ›
area. File rows keep *Pokaż różnice* (`GpoFileDiff`). After a successful copy, remove or merge the search and cleanup
documents run an incremental index refresh (only the touched GPOs are read again).

### 21.6 Cleanup advisor – "Porządki w GPO" (W3 Core, W5 UI)

**Core – `GpoCleanupAnalyzer.Analyze(index, options)`** (`src/AdMng.Core/GroupPolicy/Cleanup/`, pure, from a
`GpoSettingsIndex`): proposals sorted by kind, then GPO name. *Content rules* run only for entries `Read` (never
`Incomplete`, `NotRead` or a failed read – those GPOs are listed in `NotEvaluated`); "a counted unit" is a unit with
`CountsAsSetting` (not `Comment`, `UnreadableFile`, `ScriptFile`). "Mergeable" means: no unit with `BlocksMerge`,
`DirectorySettings` = 0, no `OtherFiles`, not a default GPO, and no disabled half (`GpoStatus`) that holds counted units.

| Kind | Rule | Actions | Notes |
|---|---|---|---|
| `EmptyGpo` | no counted unit (disabled preference items count – dormant content is not "empty"), no unreadable marker, no `OtherFiles`, `DirectorySettings` = 0 – linked or not, versions ignored | Delete, Unlink (when linked) | `Linked`, `DefaultGpo` (no Delete) |
| `UnlinkedGpo` | no link in the domain head, OUs or sites | Delete, Disable | `OtherDomainsNotSearched` when `OtherDomains` is not empty (links from other domains are not visible from this DC) |
| `AllLinksDisabled` | ≥ 1 link, every link disabled | Unlink, Disable, Delete | |
| `GpoDisabled` | `GpoStatus.AllSettingsDisabled` | Delete, Unlink (when linked) | |
| `SingleSettingGpo` | exactly one counted unit, `DirectorySettings` = 0, no `OtherFiles` | CopyTo (*Kopiuj do innego GPO…*), then Disable as a separate action; Merge only into a GPO with the same link set (container, enabled, enforced), proposed when such a GPO exists (`RelatedGpoIds`) | `NonCopyableUnits` when that unit has a refusal (no CopyTo, no Merge) |
| `DuplicateSetting` | see below | RemoveSetting (from the covered GPO) | `RemovePolicy`, `AccountPolicy` |
| `DifferentValues` | one `BaseId`, comparable applying occurrences in ≥ 2 GPOs with ≥ 2 distinct values | – (open, compare) | informational |
| `MergeCandidate` | ≥ 2 mergeable GPOs (`Read`, ≥ 1 counted unit) with the same set of links (container, enabled, enforced), the same security filter (`Allowed`, `Denied`) and the same WMI filter: each is proposed into the one with the most units | Merge | `LinkOrderDiffers`, `RemovePolicy`; a GPO that is not mergeable gets the proposal without Merge and the note `NonCopyableUnits` (`BlockingUnitIds` listed), `DirectorySettings` or `OtherFiles` |

**`DuplicateSetting` in detail** (equal values in two GPOs do not make one of them redundant on their own: user rights and
restricted-group members are last-writer-wins, preference items are ordered and cumulative, and scopes may not overlap):

1. *Applying occurrences only.* An occurrence applies when its GPO is `Read`, has at least one enabled link, its
   `GpoStatus` does not disable the unit's half, and the unit is `Comparable` and not `Disabled`. Unlinked GPOs, GPOs whose
   links are all disabled and disabled halves never take part (they are reported by their own kinds).
2. *Equal value.* At least two applying occurrences with `SameValue`, each GPO with a single occurrence of the `BaseId`.
3. *No other value.* When any applying occurrence of the same `BaseId` in another GPO has a different or non-comparable
   value (masked, conditional, cumulative), no `DuplicateSetting` is proposed for that `BaseId` (removing the
   higher-precedence copy could let the third value win); `DifferentValues` covers it.
4. *Coverage.* `RemoveSetting` from GPO X is offered only when, in every container where X applies (resolved with
   `GroupPolicyInheritance` over the index's containers – block inheritance and enforced links included – for the domain
   head, every OU and every site that links X), the other GPO Y with the equal value also applies. The proposal's `GpoId`
   is X, `RelatedGpoIds` = [Y]. When no occurrence is covered (two GPOs on disjoint OUs, such as the department drive
   GPOs of the sample) there is **no** proposal – the search shows such pairs, and `MergeCandidate` handles consolidation.
5. *Account policy.* Account-policy units (password, lockout, Kerberos) only among GPOs linked to the domain head (note
   `AccountPolicy`); below the domain head they have no domain effect and are left alone.
6. *removePolicy.* A preference item with `removePolicy="1"` gets the note `RemovePolicy` (clients undo the item when it
   disappears from X, then Y's item applies again).

Masked values are never equal; conditional (item-level targeting, also inherited from a collection) and cumulative
(scripts) units never count as duplicates; a policy unit and the raw registry values of another GPO are different units
(with a definition set the same policy is found by policy id in both). Default GPOs never get Delete or Merge as the
source. No new `GpoFindingCodes` (21.9).

**UI – `GpoCleanupDocumentViewModel`** (`GroupPolicyDocumentKind.Cleanup`, one tab per window, palette `gpo.cleanup`
*Porządki w GPO…*, *Akcja → Zasady grupy*, the GPO root's tree menu, a GPO list toolbar button): the same index service
and stale/refresh/cancel behaviour as the search; chips per kind, filters (kind, GPO, text, Ctrl+F), virtualized list,
details pane (explanation, recommendation, the GPOs and the setting in GPMC words through `GpoIndexTexts`, notes) and the
actions: *Usuń GPO…* → `DeleteCheckedAsync` (the index entry's `GpoExpectedState`, then `GroupPolicyActions.DeleteAsync`
with its backup offer + typed confirmation; a GPO changed since the analysis is refused and the index refreshed),
*Odłącz wszędzie…* → `UnlinkAllAsync`, *Wyłącz GPO* → `GroupPolicyActions.SetStatusAsync(gpo, AllSettingsDisabled)`,
*Kopiuj do innego GPO…* → `CopySettingsAsync`, *Usuń duplikat z…* → `RemoveSettingsAsync`, *Scal z…* → `MergeAsync`
(only when the proposal offers Merge), plus *Otwórz GPO*, *Porównaj*, *Pokaż w wyszukiwarce* (opens 21.4 with the
unit's name). Every action is a plan with preview and undo; after it succeeds the document refreshes incrementally and
re-analyzes. Export CSV/HTML to an operator file.

### 21.7 Data handling (rules 1–6)

* **Rule 1.** The index (settings, units, documents, SID names), search records and cleanup proposals live in the
  window's scoped `GpoSettingsIndexService` and the open documents only; dropped on `SessionChanged`, disconnect and
  window close (scope disposal). Nothing in `GroupPolicySettings` (no search history), temp files or logs; diagnostic
  logs carry counts and durations only. `ToString()` of every new type prints kinds, statuses, GUIDs and counts – never
  a unit id (it holds directory values such as paths and group names), key or value. Exports go only to files the
  operator picks.
* **Rule 2.** No password is read or written. `cpassword` items and other masked units are refused for copy, never
  matched by value, shown as "(ustawione – ukryte)"; the planner works on raw bytes but never puts secret-bearing items
  into a new file.
* **Rule 3.** Account lookups through `LdapFilter.Equal`/`Or`/`And`/`Present`; DNs with `DistinguishedName` /
  `GpLinkValue.GpoDn`; typed text is never concatenated into a filter or DN.
* **Rule 4.** One DC: index, planners and executor use the session's DC and its SYSVOL; the index records `Server` and is
  dropped when it changes; the PDC note comes with every preview (`GroupPolicyServices.PreviewAsync`).
* **Rule 5.** Copy, remove, unlink, disable, merge and delete are change plans with preview, validation, audit and undo;
  conflicts with concurrent edits are caught by `GpoTransaction`'s compare-and-swap at execution.
* **Rule 6.** Index builds are async, cancellable, with progress (at most 4 SYSVOL requests at a time, one LDAP search
  per 50 names); matching runs on `Task.Run` with debounce; result lists virtualize (also when grouped).

### 21.8 Localization

Both `Strings.resx` and `Strings.pl.resx`, one block per owner between comment markers (anchors in 21.12), constants in
new `L.*.cs` files; dynamic families get an `AllKeys()` helper tested like `GpoFindingText.AllKeys()`.

* `GpoIndex.*` (W1, `L.GpoIndex.cs`): `GpoIndex.Stage.<GpoIndexStage>`, `GpoIndex.Status.<GpoIndexStatus>`, notes
  (not read, incomplete, no SYSVOL, ADMX not loaded, links of other domains), progress text.
* `Issue.<code>` (**K7 contract commit**, constants and en + pl texts, so that `LocalizationTests` stay green from the
  start; W2 only uses them and may refine the wording) for the 25 new `IssueCodes`: copy/remove – `GpoSettingSecret`,
  `GpoSettingUnsupported`, `GpoSettingNotFound`, `GpoSettingChanged`, `GpoSettingIncomplete`, `GpoSettingAmbiguous`,
  `GpoSettingSkipped`, `GpoSettingExtensionUnknown`, `GpoSettingNoDefinitions`, `GpoSettingPolicyOwned`,
  `GpoSettingConditionalKept`, `GpoSettingRemovePolicy`, `GpoSettingSourcePartDisabled`, `GpoAccountPolicyBelowDomain`,
  `GpoKerberosPolicyBelowDomain`, `GpoCopyTooManySources`; delete – `GpoChangedSinceAnalysis`; merge –
  `GpoMergeNotCopyable`, `GpoMergeNotCarried`, `GpoMergeUnparsedFiles`, `GpoMergeOrderMixed`, `GpoMergeLinksDiffer`,
  `GpoMergeLinkOptionsDiffer`, `GpoMergeSecurityFilterDiffers`, `GpoMergeWmiFilterDiffers` (Arguments documented on each
  constant: GPO names, counts and, where useful, the unit's technical key or container names – never a value).
* `GpoSearch.*` (W4, `L.GpoSearch.cs`): title, palette command, toolbar and tab buttons, fields, facets, group summary
  (`ustawione w {0} GPO – wartości zgodne/różne/nieporównywalne`), notes, row actions, export headers.
* `GpoCopy.*` (W5, `L.GpoCopy.cs`): window, modes, conflict modes, titles of the copy/remove/merge/unlink plans,
  `Zmiany ustawień` group texts, the "nothing copied" dialog. `GpoCopy.Refusal.<GpoUnitRefusal>` (dynamic, with
  `GpoUnitRefusalText.AllKeys()`) comes with the **K7 contract commit** in `L.GpoCopy.cs` and a block
  `K7 copy refusals (21.5)` in both resx files (W4 shows them in the search before W5 exists).
* `GpoCleanup.*` (W5, `L.GpoCleanup.cs`): title, command, `GpoCleanup.Kind.<Kind>.Title|Message|Recommendation`,
  `GpoCleanup.Action.<Action>`, `GpoCleanup.Note.<Note>` (dynamic), chips, filters, export headers.

Polish UI wording as in GPMC's Polish edition and the K6 resources; test data with diacritics.

### 21.9 Not in K7 (later options)

New diagnostics codes (`EmptyGpo` content-based, `LinkDisabled` per link with *Unlink*, `SingleSettingGpo`, with fix
kinds `DeleteGpo`) – the 51-code and 11-fix-kind tests stay as they are; merge (K7.1) if it misses the release (21.5);
moving B's link to A's order and strengthening B's options during a merge (`GpLinkValue.MoveTo`); a K5 before/after model
of the merge preview; natural preference identity in the K5 modeller
and the K6 conflict rules (would report two GPOs mapping K: as a conflict); matching in both languages at once; writers
for scripts, `[Registry Keys]` / `[File Security]`, audit options and comments; preference tool GUIDs for target files
the source does not name; a before/after model of the merged GPO's containers (K5 modeller); links from other domains
of the forest; a "new GPO" choice in the copy target picker.

### 21.10 Test plan

**Core (W1, `tests/AdMng.Core.Tests/GroupPolicy/Search/`):** folder reader – both modes, every status, budgets
(`NotRead`, per GPO, `Skipped` after the run budget), `OtherFiles`, at most 4 concurrent calls (FakeSysvol `Calls` /
`HoldNext`), cancellation; the streaming overload – at most `Parallelism` results with `Content` alive at a time;
diagnostics unchanged (existing tests green, one test that the snapshot equals the pre-K7 read); `ReadSettings` overflow
(a Registry.pol with 10 001 values) and `Settings` unchanged; directory-settings count in one search; `GppXmlParser`
golden test (keys of uid-less items, items in collections and collection pseudo-items identical before and after the
`Items` refactor) plus `Items` returning the collection chain; units – every kind of 21.2, preference identity per type
(Drives K: in two GPOs with different uids → one `BaseId`), occurrences `#2`, collections (a child of a targeted
collection is `Conditional`, a child of a disabled collection is `Disabled`, a scoped child has `ScopedCollection`),
policies with and without definitions, comparability, `SameValue` (masked never equal, disabled never equal, privilege
lists as sets with names and SIDs), `SidsByName` (a user right by name and by SID compare equal; a restricted group by
name and by SID give one `BaseId`), `CountsAsSetting` / `BlocksMerge` (comment and script file neither), refusals;
`ItemKeyOf` equals the old private copies; resolver – SID text, `*SID`, en/pl names, `CONTOSO\x` resolves with a fake
session whose NetBIOS name differs from the first DNS label, `KONTOSO\x` (another domain) comes back `Unresolved`, UPN,
chunks of 50, escaping of `*()\`; index builder – full build on `AddGroupPolicyManagementSample`, incremental reuse (one GPO
bumped → one folder read), deleted GPO, `WithDefinitions` without I/O, no SYSVOL → `NotRead`, 500 synthetic GPOs with ≤ 4
concurrent calls and no `byte[]` kept by any entry, two passes (names resolved before `Of`). W1 also runs
`tests/AdMng.Watchdog.Core.Tests` and `tests/AdMng.Watchdog.Service.Tests` (the parser is shared).
**Core (W2, `tests/AdMng.Core.Tests/GroupPolicy/Copy/`, `GroupPolicyTestBed`):** per kind copy and remove, executed
(`ExecuteOkAsync`) and undone; Replace/Skip/Union; `Unchanged` (identical source: no write, no version bump); one Modify /
one version step per plan (both halves); new target file with root and `clsid`; GPP clone keeps `Filters` and common
options, new uid, `changed` from the fake time; conditional variants kept on Replace (`GpoSettingConditionalKept`);
`removePolicy` warning; scoped-collection item refused, plain-collection item copied to the root; Inconsistent policy
copied by raw entries; a `**delvals.` copied before the target's values of that key (and before values copied in the same
plan); a raw value owned by a target policy refused; restricted group by name in the target and by SID in the source →
one group line after Replace; extension pairs from the source, refused without them, accepted when the target lists the
CSE, an unknown preference type refused (`TryPreferenceExtension` null); refusals (cpassword item, masked registry value,
script, object security, audit option, comment, ambiguous selection, incomplete source, more than 10 sources); stale
`ItemId` → `NotFound`, changed fingerprint → `GpoSettingChanged`; source half disabled → warning on copy;
`GpoKerberosPolicyBelowDomain` vs `GpoAccountPolicyBelowDomain`; `PlanDeleteAsync` with an expected state that moved →
`GpoChangedSinceAnalysis`; concurrent change between plan and execution → `Conflict`, nothing written; merge – one plan,
link replaced in place / B already linked, A disabled, derived conflict mode (B first → Skip, A first → Replace, mixed →
`GpoMergeOrderMixed`), refused with blocking units, with directory settings (`GpoDirectorySettings` as error), with
unparsed inventory files (`GpoMergeUnparsedFiles`); a GPO with `GPO.cmt` and an orphaned script file merges
(`GpoMergeNotCarried`); units of A's disabled half skipped; warnings `GpoMergeLinksDiffer`, `GpoMergeLinkOptionsDiffer`,
filter/WMI; undo restores all.
**Core (W3, `tests/AdMng.Core.Tests/GroupPolicy/Cleanup/`):** every kind and note on `GpoSettingsIndex` instances built **by
hand** in the test (`GpoIndexEntry` + `GpoSettingUnit` records written directly – W3 does not wait for W1's
`GpoSettingUnits.Of`), including the negatives (incomplete GPO, `OtherFiles`, directory settings, masked/conditional/
disabled/script units, account policy at and below the domain head, default GPOs, a duplicate whose other occurrence is in
an unlinked GPO, a duplicate on disjoint OUs → no proposal, a third GPO with another value → no `DuplicateSetting`, a
blocked GPO and a part-disabled GPO → no Merge action). The sample tests (below) merge W1's builder once it exists.
**Sample (W3):** `FakeDirectoryBuilder.AddGroupPolicyCleanupSample(company, sysvol) → GroupPolicyCleanupSample` (new
partial file; `AddGroupPolicyManagementSample` and its counts unchanged; the record exposes the GPO ids by role), GPOs on
the OUs of `AddSampleCompany`:
*Porządki – Pusty GPO* (linked, version computer 2, folder with GPT.INI only); *Porządki – Bez powiązań* (unlinked,
Machine/Registry.pol with a policy of `SampleAdministrativeTemplates`); *Porządki – Szablony* (linked at the domain head,
the same policy and value) and *Porządki – Szablony (oddział)* (linked to one OU, the same policy and value → the
`DuplicateSetting` with RemoveSetting from *oddział*; the unlinked *Bez powiązań* never takes part); *Porządki – Pulpit
zdalny* (only `SeRemoteInteractiveLogonRight`) and *Porządki – Inspekcja* (audit.csv with two subcategories, the same
links, filter and WMI as *Pulpit zdalny* → merge candidate); *Porządki – Dyski (Księgowość)* and *Porządki – Dyski
(Kadry)* (User Drives.xml K: with the same path, different uids, linked to two disjoint OUs → **no** RemoveSetting, the
negative test; *Kadry* also a Machine Groups.xml preference group); *Porządki – Wyłączony* (all settings disabled, one
disabled link).
Extension names set as GPMC writes them (Drives: `[{00000000-0000-0000-0000-000000000000}{2EA1A81B-48E5-45E9-8BB7-A6E3AC170006}][{5794DAFD-BE60-433F-88A2-1A31939AC01F}{2EA1A81B-48E5-45E9-8BB7-A6E3AC170006}]`
in `gPCUserExtensionNames`; to be checked against GPMC output in the lab – the design does not depend on the value). The
management sample's drive GPO lists no Drives pair: copying from it is refused (a test says so).
**Presentation (pl-PL, `GroupPolicyHarness`):** W1 – index service (one build, `IfMissing` reuse, stale on `Changed`,
dropped on `SessionChanged`, a reconnect to the same server never reuses the old view, culture switch without GPO folder
reads (no `ListGpoFolder` / `ReadGpoFile` on GPO paths; ADML of the new culture may be read), generation guard, a caller's
cancel does not cancel a build another caller waits for, the last waiter's cancel does, `Incremental` during a build
queues one follow-up, the build's own SYSVOL connection disposed at its end), `GpoIndexTexts` one row per unit kind,
`HostTests`: `factory.Index` is the scoped instance; W4 – search: "minimalna długość hasła" finds both GPOs with values,
"K:" finds the drive GPOs grouped as one unit, account search by name and SID, masked value not matched, facets, notes,
empty query → summary only, 2 000-group cap, matcher over 100 000 records under a generous `Stopwatch` bound,
open/edit/compare actions, export, shell palette `gpo.searchSettings`, tab entry with `SearchText`; W5 – copy (picker,
window, preview rows *Zmiany ustawień* with the GPO name in `Group`, executed, undo), "all refused" → error dialog with the
reasons (no toast), remove, merge, unlink-all, advisor delete refused after a foreign change, cleanup document on the
cleanup sample (proposals, actions through `ScriptedGroupPolicyDialogs`, refresh after an action), palette `gpo.cleanup`,
an open K4 editor on the target reloads after a copy without a conflict note when it has no edits,
`UnavailableGroupPolicyDialogs` covers the new dialog; localization families complete (`AllKeys`).
**App (Windows CI):** views in `MainWindow` templates, the copy window registered with `AddWindow` (+
`GroupPolicyWindowTests`), `SmokeTest.RenderGroupPolicyAsync` opens both documents and the window, UI tests (binding
errors, header widths, virtualization when grouped, entry points), screenshots **34** `34-gpo-settings-search.png` and
**35** `35-gpo-cleanup.png` in the separate classes `ScreenshotTestsGpoSearch` and `ScreenshotTestsGpoCleanup`, each with
its own `ShootAsync` as `ScreenshotTestsCorrespondence` does (`ScreenshotTests` is not partial and its `ShootAsync` is
private; the CI step picks classes by the `ScreenshotTests` prefix). Lab (optional): copy of a policy, a right and a drive
map between two lab GPOs and `samba-tool gpo` reading the result.

### 21.11 Contracts (in the code since the K7 contract commit)

The public types that cross agent boundaries were committed by the lead **before wave 1** as compilable stubs with XML
doc comments (bodies `throw new NotImplementedException("K7 Wn: …")`, except trivial pass-throughs); **the code is
authoritative**, this list is the map. An implementer may add members, never rename or remove these without writing the
change into the as-built section. `ToString()` rules of 21.7 (already implemented in the stubs and pinned by
`GpoK7ContractTests`).

| File | Types and members | Implements |
|---|---|---|
| `src/AdMng.Core/GroupPolicy/Management/GpoFolderReader.cs` | `GpoFolderReadMode`, `GpoFolderReadOptions` (`Default`, `Settings`), `GpoFolderRequest`, `GpoFolderReadProgress`, `GpoFolderReadResult` (+ `ToState()`), `GpoFolderReader.ReadAsync` (dictionary overload; **streaming overload with `onRead`**), `GpoSettingsRead` | W1 |
| `src/AdMng.Core/GroupPolicy/Management/GroupPolicyReader.cs` | `static GpoSettingsRead ReadSettings(GpoFolderContent)`; `Task<IReadOnlyDictionary<Guid,int>> CountDirectorySettingsAsync(session, ct)` | W1 |
| `src/AdMng.Core/GroupPolicy/Content/GppXmlParser.cs` | `static IEnumerable<GppXmlItem> Items(XDocument)`; `GppXmlItem(ItemId, Element, Collections)` | W1 |
| `src/AdMng.Core/GroupPolicy/Search/GpoSettingUnit.cs` | `GpoSettingUnitKind`, `GpoUnitComparability`, `GpoUnitRefusal` (+ `ScopedCollection`), `GpoSettingUnit` (+ `FiltersFingerprint`, `Disabled`, `RemovePolicy`, `CountsAsSetting` without `ScriptFile`, `BlocksMerge`), `GpoUnitContext` (+ `SidsByName`) | W1 |
| `src/AdMng.Core/GroupPolicy/Search/GpoSettingUnits.cs` | `Of(settings, machineRegistry, userRegistry, context)`, `Of(content, context)`, `PrincipalNames(settings, domainSid)`, `SameValue`, `BaseIdOf`, `ItemKeyOf` | W1 |
| `src/AdMng.Core/GroupPolicy/Search/GpoPrincipalResolver.cs` | `GpoPrincipalKind`, `GpoPrincipal`, `GpoPrincipalResolver` (`NamesPerSearch`, `KnownSid`, `ResolveAsync`) | W1 |
| `src/AdMng.Core/GroupPolicy/Search/GpoSettingsIndex.cs` | `GpoIndexStatus`, `GpoIndexStage` (+ `Units`), `GpoIndexProgress`, `GpoUnitRef`, `GpoIndexEntry`, `GpoSettingsIndex` (+ `SidsByName`; `Find`, `BaseIds`, `Occurrences`, `WithPrincipal`, `WithDefinitions`), `GpoSettingsIndexRequest`, `GpoSettingsIndexBuilder.BuildAsync` | W1 |
| `src/AdMng.Core/GroupPolicy/Management/GpoSettingsCopy.cs` | `GpoUnitSelection(GpoId, UnitId, ItemId?, ValueFingerprint?)`, `GpoCopyConflictMode`, `GpoUnitOutcomeKind` (+ `Unchanged`), `GpoUnitOutcome`, `GpoSettingsPlan` (+ `ChangesAnything`), `GpoSettingsCopyRequest` (+ `MaxSources` = 10), `GpoMergeRequest` (`Conflict` nullable = derived), `GpoExpectedState` | W2 |
| `src/AdMng.Core/GroupPolicy/Management/GroupPolicyPlanner.CopySettings.cs` | `PlanCopySettingsAsync`, `PlanRemoveSettingsAsync(session, sysvol, gpoId, IReadOnlyList<GpoUnitSelection>, definitions, ct)`, `PlanMergeAsync`, `PlanUnlinkAllAsync`, `PlanDeleteAsync(session, gpoId, GpoExpectedState, ct)` | W2 |
| `src/AdMng.Core/GroupPolicy/AdministrativeTemplates/PolicyClientExtensions.cs` | `PolicyClientExtensions.Of` | W2 |
| `src/AdMng.Core/GroupPolicy/Backup/GpmcBackupFormat.cs` | `static Guid? TryPreferenceExtension(string? type)` (implemented: null for unknown types) | done |
| `src/AdMng.Core/Batch/ChangePlan.cs` + both resx (`K7 copy issues (21.5)` block) | the 25 `IssueCodes` of 21.8 with en + pl texts | done (W2 may refine wording) |
| `src/AdMng.Core/GroupPolicy/Cleanup/GpoCleanupModel.cs` | `GpoCleanupKind`, `GpoCleanupAction` (+ `CopyTo`), `GpoCleanupNote` (21.6 set), `GpoCleanupProposal`, `GpoCleanupOptions`, `GpoCleanupResult`, `GpoCleanupAnalyzer.Analyze` | W3 (W3 may split the analyzer into its own files) |
| `src/AdMng.Presentation/GroupPolicy/Search/IGpoSettingsIndexService.cs` | `GpoIndexRefresh`, `GpoIndexView`, `IGpoSettingsIndexService` (`Current`, `IsStale`, `IsBuilding`, `Progress`, `StateChanged`, `GetAsync(refresh, ct)` – **no SYSVOL parameter**, `CancelBuild`, `Invalidate`) | W1 |
| `src/AdMng.Presentation/GroupPolicy/Search/GpoSettingsIndexService.cs`, `GpoIndexTexts.cs` | the scoped service (ctor `(GroupPolicyServices)`, `IDisposable`); `GpoIndexTexts(localizer, view)` (`Describe`, `PrincipalName`) | W1 |
| DI, factory, lifetimes | `TryAddScoped(GpoSettingsIndexService)` + interface forward; the factory lambda passes the interface; `GroupPolicyViewModelFactory(…, IGpoSettingsIndexService? index = null)` + `Index`; both types in `ConnectionLifetimes` | done |
| `src/AdMng.Presentation/GroupPolicy/GroupPolicyServices.cs` | `GroupPolicyDocumentKind.SettingsSearch`, `.Cleanup` (after `Diagnostics`); `GroupPolicyDocumentRequest.SearchText` | done (the factory refuses both kinds until W4/W5 add their `CreateDocument` cases) |
| `src/AdMng.Presentation/GroupPolicy/Search/GpoSettingsSearchEntry.cs` | `Request`, `OpenSettingsSearch(this GroupPolicyActions, string? searchText = null)` (implemented) | done; the document: W4 |
| `src/AdMng.Presentation/GroupPolicy/Cleanup/GpoCleanupEntry.cs` | `Request`, `OpenCleanup(this GroupPolicyActions)` (implemented) | done; the document: W5 |
| `src/AdMng.Presentation/GroupPolicy/Copy/GpoCopyItem.cs`, `GpoSettingsActions.cs` | `GpoCopyItem` (+ `ToString`); `CopySettingsAsync`, `RemoveSettingsAsync`, `MergeAsync`, `UnlinkAllAsync`, `DeleteCheckedAsync(gpo, GpoExpectedState)` (return `false` until W5) | W5 |
| `src/AdMng.Presentation/GroupPolicy/Copy/GpoUnitRefusalText.cs` + both resx (`K7 copy refusals (21.5)` block) | `Key`, `Text`, `AllKeys` (implemented) | done |

Still to be added by their owners (not in the contract commit, because they need types only the owner writes):
`GroupPolicyActions.RunAsync(…, describeChanges = null)` and `DeleteAsync(…, GpoExpectedState? expected = null)` (W5);
`IGroupPolicyDialogs.ShowCopySettingsAsync(GpoCopySettingsViewModel)` (W5); `L.GpoIndex.cs` (W1), `L.GpoSearch.cs`
(W4), `L.GpoCopy.cs` and `L.GpoCleanup.cs` (W5).

### 21.12 Work split and file ownership

**Waves.** Wave 1 (parallel worktrees from the branch with this section): **W1** reader, units, resolver, index and the
Presentation index service; **W2** copy/remove/merge/unlink-all planners; **W3** cleanup analyzer and the cleanup sample.
Wave 2 (parallel, from the merged wave 1): **W4** search document; **W5** copy UI, preview builder and cleanup document.
Docs are written in the integration step after wave 2 (lead), from the as-built notes the agents return.

**Dependencies.** The lead's **K7 contract commit** (21.11) is in the branch before wave 1 starts, so the three wave-1
agents and the two wave-2 agents compile against the same stubs from the first minute and never wait for each other's
first commit. W3's analyzer tests build `GpoSettingsIndex` / `GpoIndexEntry` / `GpoSettingUnit` records by hand and do
not need W1's implementation; only W3's sample tests (end-to-end through `GpoSettingsIndexBuilder`) and W2's end-to-end
tests through `GpoSettingUnits.Of` need W1 – they are written against the stubs and turn green after the wave-1 merge
(each agent marks them `[Fact(Skip = "needs W1 – wave-1 merge")]` until then and the lead removes the skips while merging).
Wave 2 needs nothing from the other wave-2 agent: the enum members, `SearchText`, both entry classes, `GpoCopyItem`, the
`GpoSettingsActions` signatures (returning `false` until W5) and `GpoUnitRefusalText` are in the contract commit.
**Merge** is the last milestone of W2 (planner) and of W5 (merge mode of the copy window, *Scal z…* in the advisor): if
either is late, the lead ships K7 without it (the advisor then never offers `Merge`, 21.9 lists it as K7.1).

**New files (exclusive).**

| Owner | Files |
|---|---|
| W1 | `src/AdMng.Core/GroupPolicy/Management/GpoFolderReader.cs`; `src/AdMng.Core/GroupPolicy/Search/**`; `src/AdMng.Presentation/GroupPolicy/Search/{IGpoSettingsIndexService,GpoSettingsIndexService,GpoIndexTexts}.cs`; `src/AdMng.Presentation/Localization/L.GpoIndex.cs`; `tests/AdMng.Core.Tests/GroupPolicy/Search/**`; `tests/AdMng.Presentation.Tests/GroupPolicy/Search/GpoSettingsIndex*Tests.cs`, `GpoIndexTextsTests.cs` |
| W2 | `src/AdMng.Core/GroupPolicy/Management/{GroupPolicyPlanner.CopySettings,GpoSettingsCopy,GpoSettingsWriter,GppPreferenceDocument}.cs`; `src/AdMng.Core/GroupPolicy/AdministrativeTemplates/PolicyClientExtensions.cs`; `tests/AdMng.Core.Tests/GroupPolicy/Copy/**` |
| W3 | `src/AdMng.Core/GroupPolicy/Cleanup/**`; `tests/AdMng.Testing/FakeDirectoryBuilder.GroupPolicyCleanup.cs` (`AddGroupPolicyCleanupSample`, `GroupPolicyCleanupSample`); `tests/AdMng.Core.Tests/GroupPolicy/Cleanup/**` |
| W4 | `src/AdMng.Presentation/GroupPolicy/Search/{GpoSettingsSearchDocumentViewModel*.cs,GpoSearchRowViewModel,GpoSearchGroupViewModel,GpoSettingsMatcher,GpoSettingsSearchEntry}.cs` (the entry exists – W4 may extend it); `src/AdMng.Presentation/GroupPolicy/{GroupPolicyObjectDocumentViewModel.Search,GroupPolicyObjectsDocumentViewModel.Search}.cs`; `src/AdMng.Presentation/Shell/ShellViewModel.GroupPolicySearch.cs`; `src/AdMng.Presentation/Localization/L.GpoSearch.cs`; `src/AdMng.App/GroupPolicy/GpoSettingsSearchView.xaml(.cs)`; `tests/AdMng.Presentation.Tests/GroupPolicy/Search/GpoSettingsSearch*Tests.cs`; `tests/AdMng.App.UiTests/GroupPolicy/GpoSettingsSearchUiTests.cs`; `tests/AdMng.App.UiTests/ScreenshotTestsGpoSearch.cs` (34) |
| W5 | `src/AdMng.Presentation/GroupPolicy/Copy/**` (the contract files included); `src/AdMng.Presentation/GroupPolicy/Cleanup/**` (`GpoCleanupEntry.cs` included); `src/AdMng.Presentation/GroupPolicy/GroupPolicyObjectsDocumentViewModel.Cleanup.cs`; `src/AdMng.Presentation/Shell/ShellViewModel.GroupPolicyCleanup.cs`; `src/AdMng.Presentation/Localization/{L.GpoCopy,L.GpoCleanup}.cs`; `src/AdMng.App/GroupPolicy/{GpoCleanupView,GpoCopySettingsWindow}.xaml(.cs)`; `tests/AdMng.Presentation.Tests/GroupPolicy/{Copy,Cleanup}/**`; `tests/AdMng.App.UiTests/GroupPolicy/{GpoCleanupUiTests,GpoCopySettingsUiTests}.cs`; `tests/AdMng.App.UiTests/ScreenshotTestsGpoCleanup.cs` (35) |

**Shared files – changes only at these anchors** (both sides kept when two owners insert at the same anchor; W4's
lines before W5's):

| File | Owner: change at anchor |
|---|---|
| `src/AdMng.Core/GroupPolicy/Diagnostics/GroupPolicyDiagnosticsReader.cs` | W1: `ReadFoldersAsync` / `ReadFolderAsync` / `FolderMissingAsync` replaced by a call to `GpoFolderReader` (behaviour unchanged) |
| `src/AdMng.Core/GroupPolicy/Management/GroupPolicyReader.cs` | W1: `ReadSettings` before `Settings` (which delegates); the count overload after `CountDirectorySettingsAsync` |
| `src/AdMng.Core/GroupPolicy/Content/GppXmlParser.cs` | W1: `Items(XDocument)` after `Parse`; `Parse` iterates it |
| `src/AdMng.Core/GroupPolicy/Modeling/GroupPolicyModeler.cs`, `Diagnostics/GroupPolicyDiagnostics.Conflicts.cs` | W1: the private `PreferenceItem` bodies call `GpoSettingUnits.ItemKeyOf` |
| `src/AdMng.Core/Batch/ChangePlan.cs` | done by the contract commit (25 codes after `CentralStoreCreated`); W2 may only reword their XML comments |
| `src/AdMng.Core/GroupPolicy/Management/GroupPolicyPlanner.cs` | W2: none – the expected-state `PlanDeleteAsync` overload lives in the `CopySettings` partial and calls the existing method's private helpers |
| `src/AdMng.Presentation/Resources/Strings.resx`, `Strings.pl.resx` | done: blocks `K7 copy issues (21.5)` and `K7 copy refusals (21.5)` after the `Issue.CentralStoreCreated` entry (W2 may reword inside the first, W5 inside the second); W1: block `K7 settings index (21.3)` after `<!-- K6 Group Policy diagnostics (20.2): end -->`; W4: block `K7 settings search (21.4)` after `<!-- Stage S readiness check (Gotowość środowiska): end -->`; W5: block `K7 copy and cleanup (21.5, 21.6)` after `<!-- K7 copy refusals (21.5): end -->` |
| `src/AdMng.Presentation/GroupPolicy/GroupPolicyServiceCollectionExtensions.cs` | index registration and factory argument done; W5: `ShowCopySettingsAsync` in `UnavailableGroupPolicyDialogs` |
| `src/AdMng.Presentation/GroupPolicy/GroupPolicyViewModelFactory.cs` | `index` parameter and `Index` done; W4/W5: one `CreateDocument` case and one `Matches` arm each after `Diagnostics` (W4's first) |
| `src/AdMng.Presentation/GroupPolicy/GroupPolicyServices.cs` | done (`SettingsSearch`, `Cleanup`, `SearchText`); nobody changes it in K7 |
| `src/AdMng.Presentation/GroupPolicy/GroupPolicyActions.cs` | W5: the `describeChanges` parameter of `RunAsync` and its use before `PreviewAsync`; the optional `GpoExpectedState? expected` of `DeleteAsync` |
| `src/AdMng.Presentation/GroupPolicy/IGroupPolicyDialogs.cs` | W5: `ShowCopySettingsAsync` after `ShowCentralStoreUpdateAsync` |
| `src/AdMng.Presentation/Shell/ShellViewModel.GroupPolicy.cs` | W4: an `else if (request.SearchText …)` branch after the `Focus` branch of `OpenGroupPolicyDocument` |
| `src/AdMng.Presentation/Shell/ShellViewModel.Palette.cs` | W4/W5: `.. GroupPolicySearchCommandDescriptors(),` / `.. GroupPolicyCleanupCommandDescriptors(),` after `.. GroupPolicyDiagnosticsCommandDescriptors(),` |
| `src/AdMng.Presentation/Shell/ShellViewModel.Commands.cs` | W4/W5: their open commands in `StateCommands` after `OpenGroupPolicyDiagnosticsCommand` |
| `src/AdMng.Presentation/Shell/ShellViewModel.Documents.cs` | W4/W5: `ActiveGpo()` arms for the selected row after the `GroupPolicyObjectDocumentViewModel` arm |
| `tests/AdMng.Presentation.Tests/Connections/ConnectionLifetimes.cs` | done (both index types after `PolicyDefinitionsService`) |
| `tests/AdMng.App.UiTests/HostTests.cs` | W1: one check that `GroupPolicyViewModelFactory.Index` is the scoped `IGpoSettingsIndexService` |
| `tests/AdMng.Presentation.Tests/GroupPolicy/ScriptedGroupPolicyDialogs.cs` | W5: `ShowCopySettingsAsync` handler |
| `src/AdMng.App/Views/MainWindow.xaml` | W4/W5: xmlns after the last GPO xmlns; document templates after the `GroupPolicyDiagnosticsDocumentViewModel` template; menu items after `gpo.diagnose` |
| `src/AdMng.App/Views/TreeMenu.cs` | W4/W5: one item each after the `L.GpoDiagnostics.Command` item of the GPO root |
| `src/AdMng.App/GroupPolicy/GroupPolicyObjectsView.xaml` | W4/W5: a toolbar button each after `DiagnoseButton` |
| `src/AdMng.App/GroupPolicy/GroupPolicyObjectView.xaml` | W4: *Znajdź we wszystkich GPO* after `SettingsReportButton` |
| `src/AdMng.App/GroupPolicy/GroupPolicyAppRegistration.cs`, `src/AdMng.App/Services/DialogService.GroupPolicy.cs` | W5: `AddWindow<GpoCopySettingsViewModel, GpoCopySettingsWindow>()` and `ShowCopySettingsAsync` |
| `src/AdMng.App/SmokeTest.cs` | W4/W5: their requests after `new(GroupPolicyDocumentKind.Diagnostics)`; W5: the copy window render in `RenderGroupPolicyAsync` |
| `tests/AdMng.App.UiTests/GroupPolicy/GroupPolicyWindowTests.cs` | W5: the copy window in `AppHost_RegistersTheGroupPolicyWindows_AndTheyRender_Disconnected` |
| `docs/ZASADY-GRUPY.md`, `README.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`, `docs/PILOTAZ.md`, this note, and the code that quotes chapter numbers | integration (lead): new chapter *12. Wyszukiwanie, kopiowanie ustawień i porządki w GPO (K7)* after chapter 11, chapters 12–14 renumbered to 13–15 and every link to them fixed; the readiness text that quotes *rozdział 12 „Kontroler domeny, emulator PDC i SYSVOL”* in `Strings.resx` / `Strings.pl.resx` (Readiness documentation entry) becomes *rozdział 13* together with its assertion in `tests/AdMng.Presentation.Tests/Readiness/ReadinessQuickCheckTests.cs`; README K7 ✅ with *Znane ograniczenia*, its `#14-…` links → `#15-…`; `docs/ROADMAP.md` row *K1–K6* → *K1–K7* and its README anchor (renaming the README heading changes the anchor); `docs/PILOTAZ.md` §3.3 *Zasady grupy (K2–K7)* with K7 checklist items and the fixed `#15-ograniczenia-i-pilotaż` link; ARCHITECTURE paragraph; 21.14 *K7 as built* |

`L.cs`, `CLAUDE.md`, `Directory.Packages.props`, `AdMng.slnx`, the Watchdog projects and
`AddGroupPolicyManagementSample` are not changed by anyone. Any other change to a file another owner holds needs the
lead's agreement and a line in the as-built section; a merge conflict outside the anchors above means a rule of this
section was broken.

### 21.13 Review notes

The design review of 21 (2026-11) was applied in 21.1–21.12. Points taken only in part, with the reason:

* *Disabled preference items "not counted"*: they never count as duplicates or as a search group's value, but they keep
  their GPO from being `EmptyGpo` – deleting dormant content is not a cleanup the advisor should propose.
* *Defer merge to K7.1*: kept in K7 as the lead decided, but as the last, droppable milestone of W2 and W5 (21.12).
* *Move B's link to A's order during a merge*: not done; the precedence-derived conflict mode and the
  `GpoMergeLinkOptionsDiffer` warning make the effect visible, and moving links is listed in 21.9.
* *Refuse merge when a disabled half of A holds units*: those units are skipped (not copied, info issue) instead, so
  the merge keeps A's behaviour; the advisor still offers no Merge for such GPOs (note `PartDisabled`).
* *Presence-only `ScriptFile` units in the content-only read*: not needed – `ScriptFile` units no longer count as settings
  or block a merge, so the index (which does not read script files) and the planner agree.
* *Progress or cancel while planning a copy*: replaced by the 10-source limit (`GpoSettingsCopyRequest.MaxSources`).
* *Explicit-copy of a source's disabled half*: copied with a warning (`GpoSettingSourcePartDisabled`) rather than
  skipped – the operator chose the unit; only the merge skips them.
* *Separate code for directory settings in a merge*: the existing `IssueCodes.GpoDirectorySettings` is reused at severity
  `Error` (one text, already translated).

### 21.14 K7 as built

**W1 as built.** Folder reader, units, resolver, index and index service as in 21.1–21.3, with these additions and
choices (contract signatures unchanged; members added only): `GpoFolderReadResult` of a GPO skipped by the run budget has
`Skipped = true` and `Status = Failed`; the GPO budget is predictive (a file that would cross `MaxGpoBytes` and every later
file go to `NotRead`). The new `CountDirectorySettingsAsync(session, ct)` overload sits after the per-GPO method (the
contract commit had split that method's summary). `GppXmlParser.Canonical` became `internal` and `GppXmlParser.IsCollection`
public. The normalized keys do not show `Collection` elements, so the chains travel as `GpoPreferenceScope` records:
`GpoSettingUnits.PreferenceScopes(content)`, `GpoUnitContext.PreferenceScopes` and `GpoIndexEntry.PreferenceScopes` (kept for
`WithDefinitions`); `Of(settings, …)` without them takes every item as being at its file's root, `Of(content, …)` reads them.
`GpoIndexEntry.HasContent` = `Read` or `Incomplete`. Occurrence numbers follow the order of the settings (key order of
`ReadSettings`), not file order; collection pseudo-items make no unit; a cut (truncated) preference key is a unit of its own
and marks its item `Truncated`; an identity that falls back to the item id is written `(id)`; a base id that would end like
`#n` gets a closing `|`, so `BaseIdOf(Id) == BaseId` always. Policies also get a `ValueFingerprint` (state and element values,
canonical) next to `PolicySetting` equality; `SameValue` also requires the same kind and base id. `PrincipalSids` holds SIDs
only (an unresolved name stays in `Principals`). `GpoPrincipalResolver.ResolveAsync` returns `KONTOSO\Domain Admins` (another
domain) as `Unresolved` although `KnownSid` maps the bare name to this domain's RID 512; `PrincipalNames` therefore also
lists `DOMAIN\name` spellings that `KnownSid` maps to a RID of this domain, and with a `SidsByName` map the units take the
resolver's answer for them (without a map the well-known mapping stands). An incremental index build keeps the
`SidsByName` of names it resolved before (same domain SID). The service's `GetAsync` throws `InvalidOperationException` when
disconnected and `ObjectDisposedException` after disposal; waiters of a build that `Invalidate` or `CancelBuild` stopped get
`OperationCanceledException`. `GpoIndexTexts.For(localizer, view)` returns the one instance per view (the service warms it
on its build thread); `PresenterOf(entry)` and `Warm()` are public, `Describe` is cached per GPO and unit id. Review fixes: a folder the parsers trip
over marks only that GPO `Failed`; in the service an `IfMissing` request never joins a cancelled build (it queues an
`Incremental` follow-up), a GPO change during a build leaves the new view stale, queued builds cancelled before they start
release their lifetime link, and `StateChanged` posts are coalesced.

**W2 as built.** `PlanCopySettingsAsync`, `PlanRemoveSettingsAsync`, `PlanMergeAsync`, `PlanUnlinkAllAsync` and the
guarded `PlanDeleteAsync` live in `GroupPolicyPlanner.CopySettings.cs`; contract signatures unchanged. The setting-unit
layer is reached through the internal `GpoUnitSource` (`ReadSettings`, `PrincipalNames`, `ResolveAsync`, `Of`,
`SameValue`, `KnownSid`; `GroupPolicyPlanner.UnitSource`, default = W1's real functions), so W2's tests run on
hand-built units (`tests/AdMng.Core.Tests/GroupPolicy/Copy/HandUnits.cs`) and four end-to-end tests on W1's units wait
with `Skip = "K7 wave 1 merge"`. The internal `GpoSettingsWriter` addresses a unit through its settings' keys
(`GpoSettingKey.Parse` of `Settings[0]`), `Scope`, `Source` (preference file), `ItemId`, `PreferenceType` and
`PolicyId` – W1's units must fill these. A value named in a shared `**DeleteValues` / `**DeleteKeys` list is copied as
an entry of its own (`**del.x` / a one-name `**DeleteKeys`) and removed by shrinking the list. `GppPreferenceDocument`
numbers items itself exactly as `GppXmlParser.Parse` does (no dependency on `GppXmlParser.Items`) and resolves item ids
against the file as read, so removals do not shift positional ids. Deviations: the target's own incomplete read or
leftovers is an error (`GpoSettingIncomplete`); a merge refuses with `GpoMergeNotCopyable` also for units refused at plan
time (policy-owned raw value, unknown extension pair), not only for `BlocksMerge`; merge errors sit on A's object, copy
issues on the target's object; a `P|` selection without a definition set gives `GpoSettingNoDefinitions` (the units
rebuilt without a set hold no `P|` id); duplicate selections are copied once; `A = B` is `IssueCodes.InvalidValue`.
W2 review fixes: the writer refuses, independently of the unit layer, any unit with a masked setting and any preference
item carrying a non-empty `cpassword` (`GpoSettingSecret`), and an audit value outside 0–3 (`GpoSettingUnsupported`
instead of an exception); `Replace` does not remove a target occurrence that has a `Refusal` (the unit is refused, as
a remove of it would be); `GpoSettingPolicyOwned` names the target GPO; remove de-duplicates selections; in a merge,
units of a half A has disabled are skipped before any refusal check, so they neither block the merge
(`GpoMergeNotCopyable`) nor raise `GpoSettingRemovePolicy` or part-disabled warnings (21.13).

**W3 as built** (cleanup analyzer, cleanup sample). `GpoCleanupAnalyzer` lives in `src/AdMng.Core/GroupPolicy/Cleanup/`
(`GpoCleanupAnalyzer{,.Context,.Settings,.Merge}.cs`; the stub left `GpoCleanupModel.cs`, no contract signature changed).
It reads only the index's records (not `GpoSettingsIndex.Find`/`Occurrences`) and compares values with
`GpoSettingUnits.SameValue`, so it is a pure function of the unit records. Additions to 21.6, all on the safe side:
GPO-level kinds (`UnlinkedGpo`, `AllLinksDisabled`, `GpoDisabled`) are evaluated for every entry, content kinds only for
`Read`; `Disable` is not offered for a GPO already all-disabled; the note `DefaultGpo` accompanies every withheld Delete.
Default GPOs never get `SingleSettingGpo` and never take part in a merge group (neither source nor target); merge groups
and merge targets also skip all-disabled GPOs, GPOs without an enabled link and GPOs without counted units; a
single-setting GPO with an unreadable marker is not proposed; the `SingleSettingGpo` merge target needs the same security
and WMI filter too (like `MergeCandidate`). `DuplicateSetting`: coverage also requires the keeper's security filter to
reach everyone the covered GPO's does (same sets, or Authenticated Users/Everyone with no deny) and its WMI filter to be
none or the same; positions are (domain head/OU × site) pairs resolved with `GroupPolicyInheritance`; GPOs are ranked
(default GPO, more counted units, name) and a GPO is proposed only when a higher-ranked one covers it (`RelatedGpoIds` =
every such keeper), so executing every proposal never removes the last copy; an applying occurrence in an `Incomplete`
GPO, a non-comparable one or a second occurrence in one GPO blocks the base id; for account policy only domain-head GPOs
are candidates but applying occurrences anywhere block; comments and script files never count. `LinkOrderDiffers` = in a
shared container the source has the lower link order. A covered unit with a refusal (audit option, object security) is
reported without `RemoveSetting`, with `NonCopyableUnits`. Sample: `AddGroupPolicyCleanupSample` adds `OU=Kadry` below
`OU=Pracownicy` (the company has no HR OU) and appends links to existing `gPLink` values, so it combines with the
management sample; the two drive items are identical apart from their uids; "Pulpit zdalny"/"Inspekcja" are linked to
`OU=IT`, "Szablony (oddział)" to `OU=Komputery`, "Wyłączony" to `OU=Sprzedaż`. The end-to-end sample tests are skipped
with `Skip = "K7 wave 1 merge"`.

**W3 review** (fixes on the W3 branch, all on the safe side): `MergeCandidate` picks as target the best-ranked GPO with
no disabled half (falls back to the largest), and a source whose counted units the target's disabled half would take
gets no Merge (note `PartDisabled`); `SingleSettingGpo` merge targets must not disable the unit's half; an unknown
security filter (descriptor not readable) is never equal in a merge group and never shows coverage. `DuplicateSetting` is
not proposed for `RegistryKey` units (key-level `**delvals.`/delete entries act in processing order on values of other
units), for a covered GPO in whose positions a GPO of unknown content applies (status other than `Read`/`NotFound`, an
enabled link, not all disabled – it could hold a third value between the two copies), or when the covered GPO has a site
link and the forest has other domains (the site link reaches their objects too). Coverage and filters are cached per
analysis.
