**English** · [Polski](../ARCHITECTURE.md)

# AD Manager architecture

This document describes how the application is built and the principles that shape it. Decisions with their rationale: [`docs/adr`](adr/).

## Assumptions

| Requirement | How we meet it |
|---|---|
| Look and habits like `dsa.msc` | Domain/OU tree on the left, object list on the right, property pages with the same names and order, shortcuts F5 / Del / F2 / Alt+Enter / Ctrl+F |
| Modern standards | WPF .NET 10 + Fluent theme (Windows 11, light/dark), MVVM, DI, modeless windows, details panel, command palette (Ctrl+K), address bar with DN, PL/EN switchable on the fly, accessibility (UI Automation, keyboard, high contrast) |
| Smoother than the snap-in | Asynchronous LDAP, results streamed in pages, fetching only the visible columns, no 2000-object limit, search as you type (ANR), list virtualization, refreshing only changed objects |
| No local data | Every query goes to the DC; data lives in memory only while it is displayed ([ADR-0003](adr/0003-no-local-data.md)) |
| Different permission levels | Sign-in with the current Windows account or other credentials; the UI disables fields and actions the operator is not allowed to use ([ADR-0006](adr/0006-permission-aware-ui.md)) |
| Bulk editing | One change pipeline: plan → diff preview → validation → execution → report → rollback ([ADR-0004](adr/0004-one-change-pipeline.md)) |
| Site-specific AD divisions | Configurable "Views": pinned OUs, saved LDAP queries, ready-made quick filters; export/import of views for the team |

## Layers

```
┌───────────────────────────────────────────────────────────────────────────┐
│ AdMng.App (net10.0-windows, WPF)                                          │
│  XAML views, Fluent theme, Generic Host + DI, Windows services (windows,  │
│  clipboard, files), smoke test --smoke-test                               │
├───────────────────────────────────────────────────────────────────────────┤
│ AdMng.Presentation (net10.0) – no WPF types, testable on any OS           │
│  View models: sign-in, shell, tree, lists, search, views,                 │
│  property pages, bulk editor, CSV import, session log;                    │
│  localization (resx en/pl), settings (JSON without passwords)             │
├──────────────────────────────────────┬────────────────────────────────────┤
│ AdMng.Directory (net10.0)            │ AdMng.Core (net10.0)               │
│  System.DirectoryServices.Protocols  │  DN (RFC 4514), filters (RFC 4515),│
│  LdapDirectoryConnector/Session,     │  AD flags and conversions, object  │
│  connection pool to 1 DC, schema,    │  and schema model, directory       │
│  paging, VLV, ranged                 │  session contracts, change engine  │
│  retrieval, effective permissions,   │  (planner, executor, rollback),    │
│  writes, password reset, AD Recycle  │  value templates, CSV, PowerShell  │
│  Bin, change notifications           │  export, session log               │
└──────────────────────────────────────┴────────────────────────────────────┘
```

Dependencies point only downward. `AdMng.Core` does not depend on any LDAP library, so all domain logic is unit-tested
with `FakeDirectorySession` (an in-memory directory, used only in tests).

**Messages to users (U1, U1c).** `AdMng.Mail` (net10.0, MailKit) sends messages directly over SMTP and builds the MIME
body (`UserMessageMimeBody`: `multipart/alternative` of text and HTML, and with embedded images
`multipart/related` with `Content-ID`) – the same class is used by the Watchdog service (`AdMng.Watchdog.Service` references
`AdMng.Mail`), so both sending paths produce the same message structure. `AdMng.Core` uses **AngleSharp**
(an HTML5 parser, fully managed, runs on Linux) in the message HTML sanitizer (`Messaging/Html`), shared by AD
Manager and the service validator; CSS (the parser and writing rules into `style`) is our own small piece of code. `AdMng.App` is the only one
to use **WebView2** (`Microsoft.Web.WebView2`, Evergreen runtime) for the formatted message preview – without scripts,
navigation or network, with content and images from memory, and without the WebView2 runtime it shows the HTML source
([docs/dev/u1c-u1d-rich-html-and-salutations.md](../dev/u1c-u1d-rich-html-and-salutations.md) chapters 4 and 10). Since U5
the third way of sending is the operator's classic Outlook (`OutlookUserMessageTransport` in `AdMng.Outlook`, chapter
[Correspondence (U5)](#correspondence-u5)).

## Directory session

- `IDirectoryConnector.ConnectAsync` locates a DC (DsGetDcName with site awareness, or a manually specified DC),
  binds (Negotiate/Kerberos with signing and encryption, or LDAPS), reads RootDSE, the operator's identity,
  the NetBIOS name, the presence of the AD Recycle Bin, the Exchange schema and LAPS, and the schema (attributes, classes, display names from
  display specifiers 415/409) – in parallel, target < 1 s.
- `IDirectorySession` is pinned to one DC ([ADR-0005](adr/0005-session-pinned-to-dc.md)) and has a pool of several
  connections. All calls are asynchronous with `CancellationToken`.
- "Change Domain Controller..." does not switch the existing session: `IDirectoryConnector.ConnectToServerAsync` opens a NEW
  session on the chosen DC with the options and credential of the current one (the password stays in memory of the `AdMng.Directory` layer),
  `ISessionManager.SwitchDomainControllerAsync` validates it (same domain, different DC) and only then replaces
  the current one and closes the previous one. An error or cancellation leaves the current session unchanged. Windows opened in the previous session
  stay with it (they neither read nor write) and state that they belong to the previous DC; the main window's tabs
  reload from the new DC, and the session log records the change. The DC list combines a read from the directory of the current session
  (`server`/`nTDSDSA` in `CN=Sites`, `fSMORoleOwner`) with the DsGetDcName locator (DNS SRV, client site)
  ([dev/phase1-changedc.md](../dev/phase1-changedc.md)).
- Well-known containers (`wellKnownObjects`/`otherWellKnownObjects` of the domain head) are read by
  `ISessionManager.GetWellKnownObjectsAsync` once per session and kept in memory (`WellKnownObjectMap`); a new session – also
  after a DC change – reads them again. An unreadable attribute yields an empty map: no markers and no redirection, container names
  are not guessed.
- Search (`SearchAsync`) returns an `IAsyncEnumerable` – the first page reaches the screen before the server returns
  further ones. For huge containers VLV (`GetVirtualListPageAsync`) is available – a scrolling window sorted
  on the server side.
- Large multivalued attributes (group members > 1500) are read through ranged retrieval.
- Values are decoded according to the schema (text vs binary: GUID, SID, security descriptor, photo).
- LDAP errors and AD extended codes (e.g. `0000052D` – password policy, `data 775` – account locked) are mapped to
  `DirectoryErrorKind` and understandable PL/EN messages.

### Multiple connections: one main window per connection

Several domains (or the same domain with a different account) work simultaneously in one process, each in a separate main
window ([dev/multi-window.md](../dev/multi-window.md)):

- **A DI scope per window.** `ConnectionWindowManager` (App, singleton) creates an `AsyncServiceScope` for each connection window.
  The scope holds: `ISessionManager` (one session; a bind that finishes after the window is closed
  is closed immediately), `ConnectionContext` (id, profile, color, identity, `Lifetime`), the change bus, the audit
  log, the executor, the activity center, the coordinator, `DialogServices`, the shell (`ShellViewModel`), the services of the Watchdog
  tab, and on the WPF side `ConnectionNavigator`, `WindowService`, `DialogService`, `ToolWindows`, notifications,
  the clipboard and file windows. Shared (singletons): settings, views, saved filters (`FilterLibrary`), localization,
  theme, connector, engines, the connection to the Watchdog service, the SMTP password store and the daily message limit,
  `ConnectionRegistry`, as well as the account review permission sources (delegations in ACLs, local permissions from GPOs) and the
  SYSVOL reader – stateless: each run receives the session of the window that started it and reads only through it (SYSVOL from its
  controller and with its credential). Window services are never obtained from the root provider (`ValidateScopes`; the
  registration tests check the lifetime of each one on the list).
- **Child windows** are created from the window's scope, so they use its session, dialogs and clipboard; each has the
  `ConnectionChrome.ConnectionId` property attached, an owner from the same connection, a domain suffix in the title, and
  a bar and header in the connection's color (a snapshot of the identity from the moment of opening). Looking up open windows ("one per
  connection") sees only windows of that connection; dragging objects from another connection is rejected.
- **Writes only in the plan's session.** `ChangeCoordinator.ApplyAsync` executes a plan in the session in which it was planned; if the
  session has changed (Disconnect, reconnect), nothing is written. Confirmations name the domain of the session that
  will execute the operation.
- **Closing a window:** the end of `Lifetime`, closed dialogs and notifications of the window (remaining background work will show nothing),
  cancelling its operations and closing the session off the UI thread (with a timeout), then disposing the scope;
  the last window ends the program, and `App.OnExit` closes the sessions of the windows that are still left.
- **Settings** are shared; `JsonSettingsStore` changes a copy (copy-on-write) under a lock and saves snapshots with a
  generation number, so windows do not overwrite each other's changes and a reader never sees a list being modified.
- **Comparing objects across connections** is read-only. The comparison window (scope of window A) receives from
  `ConnectionContext.Pin()` of window B (`IConnectionHandle` from `ConnectionRegistry`) a `ConnectionReadContext`: the reader,
  formatting, schema, identity and policy of **one** session B. The reader is an `IDirectoryReader`
  (`IDirectorySession : IDirectoryReader`) behind a `ReadOnlyDirectoryReader` that cannot be cast back to the session,
  limited to 2 concurrent requests; the context ends when session B changes or window B is closed (reads throw
  `ObjectDisposedException`, side B is marked as ended). Side A reads the same way through the pinned session
  of window A (rule 4: each side through its own window's controller, no new bind). The pure diff (`ObjectDiffer`,
  `ObjectSnapshot`, `MembershipComparer`, `GroupKey`, `ComparisonAttributes`) is in AdMng.Core, the matching and reading
  (`ObjectMatcher`, `ComparisonReader`) and `ObjectComparisonViewModel` are in Presentation. Results live only in the window's memory;
  CSV is created only on the operator's request.

## Change pipeline (single and bulk editing)

```
 selection / query result / CSV
            │
            ▼
   BatchOperation[]  ──►  IChangePlanner.PlanAsync ──► ChangePlan
   (set, clear,            • fresh read from the DC        ObjectPlan[]
    add/remove values,     • templates {givenName|lower}   PlannedChange[] (old → new)
    find/replace, flags,   • skipping changes with no      ValidationIssue[]
    unlock, password,        effect
    move, rename,          • schema, permissions,
    groups, delete)          uniqueness, protected objects
                                   │
                                   ▼
                        diff preview + confirmation
                                   │
                                   ▼
                     IBatchExecutor.ExecuteAsync ──► BatchResult ──► CSV / PowerShell export
                     • bounded parallelism               │
                     • optimistic concurrency            ├──► session log (IAuditLog, no passwords)
                     • retrying transient errors         │
                     • cancellation                      └──► IRollbackPlanner ──► rollback plan
```

Order of steps on an object: password reset → attribute modification → group membership → rename →
move (deletion is always separate). Changing a single value in a properties window uses the same pipeline.
Values in a plan are text; binary attributes are not edited in bulk – the exception is `logonHours` (the "Logon
hours" window), which in the plan, the log and the rollback is a hexadecimal string, and reaches the DC as 21 bytes
(`PipelineValues`).

**SYSVOL in the change pipeline (rule 5 also covers GPO files).** Since K2 a plan may contain SYSVOL steps alongside LDAP steps:
`CreateGpoFolder`, `WriteGpoFile`, `DeleteGpoFile`, `SyncGpoFolderSecurity`, `SetGpoFolderSecurity`, `SetGpoVersion`,
`DeleteGpoFolder` (`PlannedChange.Sysvol`, `.GpoVersion`); a GPO plan is sequential (`ChangePlan.Sequential`) and carries
semantic PowerShell equivalents (`ObjectPlan.PowerShell`: `New-GPO`, `Set-GPLink`, `Restore-GPO` …). The executor
opens one SYSVOL connection per run – on the session's controller, with the window's credentials (`ISysvolFileWriter`, rules 2
and 4) – and writes the files of one GPO in a `GpoTransaction`: temporary files next to the target ones, verification,
replacement, finally the version in `GPT.INI` and a compare-and-swap of `versionNumber` in AD; an error or a change of the GPO by someone
else restores the files from memory. Versions only increase (also on rollback), the rollback plan reverses the order of objects.
The audit log records the paths, sizes and SHA-256 of files – never the content. Nothing outside the pipeline writes to SYSVOL.

**Creating multiple accounts (F3) in the change pipeline.** The *Create multiple accounts* wizard is the third mode of the
bulk editor (`BatchSourceMode.Create`), not a separate window. Everything that decides the values is in
`AdMng.Core/Batch/Creation`: `TableTextReader` (clipboard text, its HTML format when needed, CSV or `.xlsx`) yields an
`InputTable`, and `IBulkCreatePlanner` (`BulkCreatePlanner`) only reads in `ComputeAsync` – through the window's session
and, in a forest with several domains, through a read-only session to the global catalog port of **the same** DC
(`ConnectGlobalCatalogAsync`) – and computes the rows: patterns (the `ITemplateEngine` syntax with the tokens `{$nr}`,
`{$column:…}`, `{$suffix}`, `{$mailDomain}`), counters, uniqueness, managers, groups and validation
(`BulkCreateIssueCodes`). `BuildPlan` is pure and yields an ordinary `ChangePlan`: one `ObjectPlan` per row (`SourceRow`,
`PowerShell` = `New-ADUser … -AccountPassword (Read-Host -AsSecureString …)`) with the steps `Create` (the account
disabled, UAC 514), `ResetPassword` (the password only in `PlannedChange.Password`), `Modify` (enabling with a
compare-and-swap of the old UAC value, `pwdLastSet` 0), the manager and `AddMember`; the plan is not sequential, and
`Server` is the DC the values were read from (the executor refuses any other). Before the plan is built, the wizard
computes the rows again with the values of the check frozen (`FrozenValues`), so the plan contains exactly what the grid
showed. Execution goes through `IChangeCoordinator` → `BatchExecutor` (the executor is unchanged; audit, PowerShell
history and change notifications come from the pipeline) in two parts: first one object as a trial (*canary*), and the
rest only when all its steps succeeded – a systematic error (no encryption, no reset right, a rejected password) stops
after one account. The results of all executions of the window are merged (`BulkCreateResults.Merge`); `BuildRetryPlan`
skips the steps that already succeeded (an account is never created twice and never gets a second password). Undo does
not use `IRollbackPlanner`, which knows only the last result: `PlanUndoAsync` builds a plan of `Delete` steps for
everything the window created, after reading each object again by `objectGUID` (an object replaced under the same name is
skipped). The table, the computed rows, plans, results and passwords live only in the window's view models (rules 1 and
2); the naming profiles in the settings are patterns and options.
User description: [CREATING-ACCOUNTS.md](CREATING-ACCOUNTS.md); design:
[dev/phase3-bulk-create.md](../dev/phase3-bulk-create.md).

### Group Policy (K2)

`AdMng.Core/GroupPolicy`: `GroupPolicyReader` (GPOs, links, inheritance, WMI filters, sites, PDC emulator,
GPO folders loaded into memory within limits), `GroupPolicyPlanner` (a plan for every operation – create, copy, rename,
delete, `gPLink`/`gPOptions`, state, WMI filters, security filtering and delegation through the K1 `ModifySecurity`
with GPMC levels, version alignment, folder permissions, restore and import), `GpmcBackupFormat` (backups in the
GPMC layout + `admng.json`, read as untrusted data), `GroupPolicyComparer` and the GPO content parsers shared with
the Watchdog (`GroupPolicy/Content`). `AdMng.Presentation/GroupPolicy`: per-connection-window services and actions (Scoped),
four tabs (`IShellDocument`), tree nodes, window models and the plan preview window (`ChangePlanPreviewViewModel`).
`AdMng.App/GroupPolicy`: WPF views, windows and `WindowsSysvolFileWriter` (`AdMng.Directory`). Backups and comparison export
go only to places chosen by the operator (rule 1). User description:
[GROUP-POLICY.md](GROUP-POLICY.md); specification: [dev/k-computers-and-gpo.md](../dev/k-computers-and-gpo.md).

### Administrative Templates (K3)

`AdMng.Core/GroupPolicy/AdministrativeTemplates`: ADMX and ADML parsers (`AdmxParser`, `AdmlParser`; hardened XML
`GpoXml` – no DTD and no resolver, within limits), the definition set (`PolicyDefinitionSet`, `PolicyDefinitionLoader`: UI
language, the file's `fallbackCulture`, `en-US`; namespace collisions and category loops reported, not fatal), template
sources (`SysvolPolicyDefinitionSource` – the central store on the session's DC, a folder, a package), `RegistryPolicyDocument`
(minimal editing of `Registry.pol` on top of `RegistryPolFile`, PReg v1: unchanged entries stay byte for byte and in their
places) and `PolicyEngine` (setting state from entries – *Enabled*, *Disabled*, *Not configured*,
*Ambiguous* – and the entries of each state according to ADMX rules: `**del.`, `**delvals.`, `**soft.`, list value
prefixes). Saving settings is planned by `GroupPolicyPlanner.PlanAdministrativeTemplatesAsync`: `Modify` of the extension
names (`GpoExtensionNames` – the Registry extension, the *Administrative Templates* tool, template client extensions),
`WriteGpoFile` of `Machine|User/Registry.pol` with the pre-edit content as a precondition (conflict if someone changed the file) and
`SetGpoVersion` of the changed part. Central store update: `PolicyTemplatePackage` (vendor package layouts
reduced to `name.admx` + `culture/name.adml`, package trust), `GroupPolicyPlanner.CompareCentralStoreAsync` and
`PlanCentralStoreUpdate`, the plan actions `WritePolicyDefinitionsFile` and `DeletePolicyDefinitionsFile` executed in one
transaction `PolicyDefinitionsTransaction` (`*.admng-<run>.tmp` files next to the target ones, verification, replacement,
restore on error; the store has no version numbers), `CentralStoreBackup` (a copy into an operator folder) and
`VendorTemplateCatalog` (Edge, Chrome and Firefox addresses, allowed hosts, reading release descriptions with a SHA-256 checksum).
`AdMng.Presentation/GroupPolicy/AdministrativeTemplates`: `PolicyDefinitionsService` (templates of the connection window, Scoped,
with fallback templates of this computer), the editor (`AdministrativeTemplatesEditorViewModel`, option editors),
`PolicyTemplatePackageLoader` (files, folders, HTTPS download exclusively from vendor hosts – also after
redirects – with a size limit and a checksum) and `CentralStoreUpdateViewModel`. `AdMng.App/GroupPolicy`: the editor tab,
the update window and `WindowsPolicyPackageExtractor` (Authenticode signature through `WinVerifyTrust`; MSI/EXE installers
run only with a Microsoft signature – `msiexec /a`, `/extract` in a temporary folder deleted immediately; CAB
unpacked in memory through `cabinet.dll` – `CabinetUnpacker`).
Templates and settings exist only in memory; the central store copy is created only in a folder chosen by the operator
(rule 1), and the audit log records the paths, sizes and SHA-256 of the store files – not their content.

### Group Policy modeling – "what will the computer get" (K5)

`AdMng.Core/GroupPolicy/Modeling`: `GroupPolicyModelReader` reads from the session's DC (rule 4) the GPOs with flags, version,
WMI filter and DACL list (one search with the SD flags control), containers with links
(`GroupPolicyTopology.ReadContainersAsync`), accounts with `tokenGroups` (the primary group also without it, then
`TokenIncomplete`) and GPO folders from SYSVOL (at most four at a time, within the K2 limits). `GroupPolicyModeler` is a pure
function: the link chain with `GroupPolicyInheritance.Explain` (order, enforcement, blocked inheritance, disabled
and repeated links – with the reason for exclusion), the result of each GPO (does not exist, partly disabled, empty, security
filtering, MS16-072, WMI filter according to the operator's assumption), loopback (merge, replace, from the computer's GPO
or a chosen one), settings with the winning and overridden GPOs (cumulative scripts, domain controller account policies only from the domain
GPO, ADMX policies through `PolicyOwnershipIndex` and `PolicyEngine`, preference items with targeting as conditional).
`GroupPolicySecurityFilter` evaluates the DACL entries in order, like `AccessCheck` (the first token entry
that covers the right decides); an account needs *Read* and *Apply group policy*. `AdMng.Presentation/GroupPolicy/Modeling`:
the tab `GroupPolicyModelingDocumentViewModel` (input data, *Recalculate* off the UI thread, rows and texts in
`GroupPolicyModelRows`, CSV export to an operator's file); `AdMng.App/GroupPolicy/GroupPolicyModelingView.xaml`. The model
exists only in memory while the tab is open (rule 1); nothing is written to the directory.

### Security settings (K4)

`AdMng.Core/GroupPolicy/Security`: editable documents `SecurityTemplateDocument` (`GptTmpl.inf`, UTF-16LE with BOM),
`AuditPolicyDocument` (`audit.csv`, the file's encoding and column order) and `GppGroupsDocument` (preferences `Groups.xml`,
hardened XML) – each changes only the rows or elements of the settings being set and preserves the rest of the file
byte for byte; `SecuritySettingsCatalog` (user rights, account policies, security options, audit categories and subcategories
– identifiers only, names in resources). Saving is planned by `GroupPolicyPlanner.PlanSecuritySettingsAsync`:
`Modify` of the computer extension names (GPMC pairs only for the files being written), `WriteGpoFile` of each changed file
with the pre-edit content as a precondition (conflict) and `SetGpoVersion` of the computer part. The tab
`SecuritySettingsEditorViewModel` (`AdMng.Presentation/GroupPolicy/SecuritySettings`) works like the K3 template editor
(reconciliation on refresh, conflict, saving through the plan preview); the files exist only in the tab's memory (rule 1).

### Readable settings, editing convenience and Group Policy diagnostics (K6)

- **Setting keys:** `AdMng.Core/GroupPolicy/Content/GpoSettingKey.Parse` breaks a setting key (the grammar
  of all GPO content parsers) into the area, section, name, registry key and value, the type and identifier of the
  preference item, the script phase and the link to the K4 catalog (an option with a `SecurityValueUnit` unit, an audit
  subcategory); `GpoSettingValue.TryParseRegistry` reads registry values. Matching `Registry.pol` to ADMX policies from
  the K5 model is public as `AdministrativeTemplatePolicies.Configured`. Comparison rows (`GpoComparisonRow.A/B`)
  and file diff rows (`GpoDiffLine.Setting`) carry the settings they were built from.
- **Presenter:** `AdMng.Presentation/GroupPolicy/Settings/GpoSettingPresenter` describes a setting in GPMC language
  (`GpoSettingText`: group path, name, value with units, elements, explanation, technical key, the *Edit this setting*
  target), merges preference items and the values of one ADMX policy into a single row and replaces SIDs with
  names (one `TrusteeNames.ResolveAsync` per view). It is used by the *Settings* tab (with the HTML report
  `GpoSettingsReport`), the K5 model, the comparison, the plan preview and the GPO change rows in the Watchdog tab; secrets stay
  masked on every path.
- **Editing convenience:** `ISaveableDocument` / `ISearchableDocument` (`Shell/DocumentCapabilities.cs`) give Ctrl+S and
  Ctrl+F to a tab that supports them; `GroupPolicySettingFocus` and `GroupPolicyActions.EditSetting` open K3 or
  K4 on a setting (the editor tab of that GPO is reused); `ChangePlanPreviewRequest.SettingChanges` adds a
  *Setting changes* section to the preview.
- **Diagnostics:** `AdMng.Core/GroupPolicy/Diagnostics`: `GroupPolicyDiagnosticsReader` reads a snapshot (quick: LDAP and
  `GPT.INI`; full: also SYSVOL folders, object counts, SIDs and container models) from the session's DC (rule 4), at most
  four SYSVOL reads at a time; `GroupPolicyDiagnostics.Analyze` is a pure rule function (`GpoFindingCodes`, 51 codes,
  severity, area, rule group; `GpoFinding.ToString` without directory values). Fixes (`GpoQuickFix`) correspond to
  one `GroupPolicyPlanner` method (including the new `PlanRepairExtensionNamesAsync`, `PlanRemoveDuplicateLinksAsync`) and
  go through the plan preview (rule 5, `GpoQuickFixRunner`). The tab `GroupPolicyDiagnosticsDocumentViewModel` (one per
  connection window) and the GPO *Status* tab (`GroupPolicyObjectDocumentViewModel.Health.cs`) show the results; snapshots and
  findings exist only in memory, CSV/HTML export goes to an operator's file (rule 1).

### Settings search, copying settings and GPO cleanup (K7)

- **Settings index:** `AdMng.Core/GroupPolicy/Search`: `GpoSettingsIndexBuilder` reads from the session's DC (rule 4) the
  GPOs, links, `GPT.INI` versions and the folders of all GPOs, streamed through `GpoFolderReader` (`ContentOnly` mode, at
  most four SYSVOL reads at a time, limits per file, per GPO and per run; the bytes of one GPO are parsed and discarded
  right away), resolves account names in one pass with `GpoPrincipalResolver` (`LdapFilter` filters, 50 names per query)
  and groups the settings into **units** (`GpoSettingUnits.Of`): what GPMC shows as one row, with an identifier shared by
  all GPOs (an ADMX policy by its identifier, a preference item by its natural identity – drive letter, group name, path –
  not by its `uid`), a value fingerprint, comparability (hidden, conditional, cumulative) and the reason why copying is
  refused. An incremental refresh reads only the GPOs whose version changed.
- **Index service per window:** `GpoSettingsIndexService` (`AdMng.Presentation/GroupPolicy/Search`, Scoped – one per
  connection window) owns every run: its own SYSVOL connection and its own token tied to the lifetime of the GPO
  services, one run at a time with at most one run queued, a view bound to the session instance and the server, a
  staleness marker after GPO changes, abandonment when the session changes. The search
  (`GpoSettingsSearchDocumentViewModel`, matching by `GpoSettingsMatcher` in the background) and the cleanup
  (`GpoCleanupDocumentViewModel`) read the same view; `GpoIndexTexts` computes the GPMC texts once per view.
- **Writing through the change plan:** `GroupPolicyPlanner.PlanCopySettingsAsync`, `PlanRemoveSettingsAsync`,
  `PlanMergeAsync`, `PlanUnlinkAllAsync` and `PlanDeleteAsync` with an expected state read the source and the target
  **again** at planning time (never values from the index), and the internal `GpoSettingsWriter` changes `Registry.pol`,
  `GptTmpl.inf`, `audit.csv` and the preference files (`GppPreferenceDocument`) with minimal edits. The result is one
  `ObjectPlan` per target GPO (extension names, `WriteGpoFile`, a single version increment – one `GpoTransaction`), for a
  merge also the `gPLink` of the containers and the state of the merged GPO; preview, audit and rollback as for any plan
  (rule 5). The `GpoSettingsActions` actions (`AdMng.Presentation/GroupPolicy/Copy`) pass on the index's ADMX definition
  set and build the *Setting changes* section (`GpoSettingChangesBuilder`).
- **Cleanup advisor:** `GpoCleanupAnalyzer.Analyze(index, options)` (`AdMng.Core/GroupPolicy/Cleanup`) is a pure
  function of the index records (values are compared by `GpoSettingUnits.SameValue`, the scope by
  `GroupPolicyInheritance`); proposals and their actions are only data, and every action goes through the planner and
  the preview. The index, results and proposals exist only in the window's memory, export goes to an operator's file
  (rule 1). Description:
  [GROUP-POLICY.md, chapter 12](GROUP-POLICY.md); specification:
  [dev/k-computers-and-gpo.md](../dev/k-computers-and-gpo.md), chapter 21.

### Event logs (track E)

- `AdMng.Core/EventLogs`: the `EventLogRecord` model, the `EventLogQuery` query, the `EventLogCoverage` coverage, the contracts
  `IEventLogConnector`/`IEventLogReader` and the `EventLogAccessException` errors; `EventXmlParser` (event XML → record,
  hardened XML), `EventQueryXml` (a structured query with a safe `XPathLiteral`, the operator's XPath control –
  rule 3), `EventCodes` (NTSTATUS codes, Kerberos, encryption and logon types – keys, texts in resources),
  `EventLogMatcher`; `InsertionStringDecoder`, `XPathLiteral` and `EventHostNames` moved from the Watchdog and now shared.
  `Analysis`: `LockoutInvestigation`, `FailedLogonAnalysis` and `AuditEvidence` – query plans and pure
  analysis functions.
- `AdMng.EventLogs` (application only, Windows at run time – like `AdMng.Remote`): `WindowsEventLogConnector` –
  `EventLogSession` (MS-EVEN6/RPC) with Kerberos and the window's credential (`SessionLogons`, password only in memory),
  a separate session per operation, reading in batches on a separate thread, cancellation through `CancelCurrentOperations`, coverage from
  `GetLogInformation`/`EventLogConfiguration`, `.evtx` files, error classification.
- `AdMng.Presentation/EventLogs`: `EventLogHostRunner` (at most 4 computers at a time, a limit of 50,000 events in memory),
  the tabs *Event logs*, *Find the lockout source* (account state on each DC through `DomainControllerReader` – rule 4,
  `lockoutTime` replication metadata, PSOs, the source computer through the K1 remote actions) and *Failed logons*; saved
  queries in settings (without values from the logs). Events and results exist only in memory; CSV/HTML export goes to an
  operator's file with a note about personal data (rule 1). Description: [EVENT-LOGS.md](EVENT-LOGS.md),
  design: [dev/e-event-logs.md](../dev/e-event-logs.md).

## User interface

- **Main window:** menu and toolbar (like ADUC), address bar (DN or a `contoso.test/Firma/IT` path),
  global search (Ctrl+K / Ctrl+E), a tree with a "Views" section, tabs with lists, a details panel, a status bar
  (DC, identity – with privileged accounts highlighted, object count, query time, background operations).
- **Lists:** streamed, virtualized, columns chosen from schema attributes, a quick text filter,
  chip filters executed on the server side (locked, disabled, inactive N days, …), sorting, CSV export.
  A container that exceeds 5000 objects switches to a virtual list (`VirtualRowList`): VLV sorted on the server, pages
  read while scrolling, a bounded page cache, search-as-you-type through the VLV
  `seek` value, refreshing visible pages after changes ([dev/phase1-navigation.md](../dev/phase1-navigation.md)).
- **Tree:** well-known containers recognized by GUID from `wellKnownObjects`/`otherWellKnownObjects` of the domain head
  (a marker and a role description); "New" commands without a container context create objects in the default Users/Computers container.
- **Properties:** modeless windows, pages as in ADUC + the Attribute Editor; fields unavailable to the operator are read-only.
  "Logon hours..." (the Account page and a bulk editor operation) is a 7 × 24 grid in the operator's local time:
  `LogonHours` (Core) converts 168 UTC bits to the local grid with the current time zone offset, like ADUC
  ([dev/phase1-logonhours.md](../dev/phase1-logonhours.md)).
- **Copying:** "Copy" buttons in the details panel and on property pages, Ctrl+C of list rows (visible
  columns, tabs, formatted off the UI thread), "Copy as..." for a selection in list order – formats other
  than name, DN and path read objects live from the session's DC in batches (`DirectoryLookup.ReadManyAsync`), the read is
  cancelled when the session changes. Only `IClipboardService` writes to the clipboard; nothing reaches the disk
  ([dev/phase1-copy.md](../dev/phase1-copy.md)).
- **Bulk editor:** a wizard operations → preview → execution → results → rollback; CSV import with column mapping.
- **Localization:** `Strings.resx` (en) and `Strings.pl.resx` resources; attribute names from AD display specifiers (as in the
  Polish ADUC), language switching without a restart.

## Computers (K1)

Design: [dev/k-computers-and-gpo.md](../dev/k-computers-and-gpo.md), chapters 3.1 and 4; administrator description:
[COMPUTERS.md](COMPUTERS.md).

- **Core/Computers** – pure logic without LDAP: `ComputerFacts` (facts from one search result), `ComputerClassifier`
  (workstation / server / DC / RODC from `userAccountControl`, the primary group and `operatingSystem`), `WindowsReleaseCatalog`
  (build + release class → end-of-support date, overrides from settings), `ComputerInventory.Evaluate` (computer state
  for reports), `ComputerReports` (LDAP filters of reports through `LdapFilter`), `PlacementRules`, `SubnetMap` and
  `SiteTopology` (sites and subnets from the session DC's configuration), `ComputerJoinRights` (the four join-right ACEs).
- **Permission writes (K1.1)** – the `ChangeAction.ModifySecurity` action in the change pipeline: `AclChange` is an ACE delta that the
  executor applies to a freshly read DACL (`DaclEditor`) and writes with the SD flags control `DACL_SECURITY_INFORMATION`
  (`ModifyOptions.SecurityDescriptorFlags`) – the owner, group and SACL are not written. Rollback is the inverse delta,
  the PowerShell export is `Get-Acl`/`Set-Acl` on the `AD:` drive pinned to the plan's DC. The step result
  (`ChangeResult.AppliedAcl`) is the part of the delta that actually changed the DACL – the audit log and the
  rollback use it, so an ACE added in the meantime by someone else is neither attributed to the plan nor removed. K2 also uses this action
  (security filtering, GPO delegation); a GPO copy with permissions and the restore of a deleted GPO
  use an "exact" delta (`AclChange.ReplacesExplicit`), which also removes explicit ACEs that are not in the source.
- **Lifecycle** – preparing an account in the "New → Computer" wizard (a create step + `ModifySecurity` in one plan),
  `ResetComputerAccountOperation` (random 120 characters, only in the plan's memory) and `PlanValidation` rules for DC accounts.
- **AdMng.Remote** (a separate project, `net10.0`, Windows calls guarded) – `CimRemoteComputerClient`
  (`Microsoft.Management.Infrastructure`: CIM over WS-Management, Kerberos only, the window's credentials from
  `SessionLogons` in AdMng.Directory as a `SecureString` disposed with the CIM session), `WindowsLocalToolLauncher` (mstsc without
  credentials; `compmgmt.msc`/`eventvwr` through `CreateProcessWithLogonW(LOGON_NETCREDENTIALS_ONLY)`), DNS and ping. Only
  `AdMng.App` references it, so the CIM package does not reach the Watchdog service on Linux.
- **Presentation/Computers** – `ComputerReviewViewModel` (a stream as in the account review, tiles, report columns,
  on-demand reading from all DCs and sites), `RemoteComputerViewModel` and `RestartComputerViewModel`, `ComputerActions`
  (remote target, tools, ping, account reset), the "Status" property page and the "Computers" settings page.
  `ComputerViewModelFactory` and `ComputerActions` are **Scoped** (connection window); the CIM client, tools, ping and DNS
  resolver are stateless singletons – each call receives the session (credential) of the window that asks.
- **Data** – review results, addresses, sites and remote action results exist only in the window's memory; the session log records
  remote actions without the restart message text, and diagnostic logs contain no computer names.

## Correspondence (U5)

Design: [dev/u5-outlook-correspondence.md](../dev/u5-outlook-correspondence.md); user description:
[CORRESPONDENCE.md](CORRESPONDENCE.md). The *Correspondence* window is the *Korespondencja WPiA* program (bulk sending through
classic Outlook) moved into AD Manager, with recipients from AD and queries to mailbox owners. It writes nothing
to the directory (rule 5 does not apply – a message is not an AD change); all reads go through the connection window's
session (rule 4).

```text
AdMng.App/Correspondence ── WPF window, RichMessageEditor + FlowDocumentHtml editor, DPAPI, Downloads folder, 127.0.0.1 preview
        │
AdMng.Presentation/Correspondence ── CorrespondenceViewModel + pages, journal, templates, Outlook connection
        │                                    │
AdMng.Core/Correspondence                 AdMng.Outlook ── COM (dynamic) on an STA thread ──► classic Outlook (outlook.exe)
  Import, Fields, Composition,              (App only; never in the Watchdog package)
  Journal, Mailboxes, Outlook (contracts)
```

- **AdMng.Outlook** (a separate project, `net10.0`, runs only on Windows, compiles everywhere – like `AdMng.Remote`):
  classic Outlook through **COM late binding** (`dynamic`, no PIA libraries), each call on a single STA thread of the
  session (`StaWorker`) with a COM message filter (retrying on "Outlook busy" for up to 60 s). `OutlookClient` attaches to a
  running Outlook or starts it (3 attempts); `OutlookSession` sends or saves a draft
  (`OutlookMailBuilder` – the order of steps as in Korespondencja WPiA: account, *on behalf of*, recipients with resolution,
  subject, HTML, receipt, deferred delivery, voting, named properties, `cid:` images as hidden
  attachments, attachments, save, send), builds the preview in Outlook, diagnoses the environment and reads votes from *Sent
  Items* (`OutlookCampaigns`, named properties `AdMngCampaign`/`AdMngKeys`). `OutlookErrors` turns the HRESULT and
  the step into an `outlook.*` code – the exception text is never kept (it may contain addresses). Images for Outlook go
  to a private temporary folder deleted right after the message is saved (`OutlookTempFiles`). The Outlook signatures
  (`OutlookSignatureFiles`) and the U1 transport `OutlookUserMessageTransport` are here too. Only
  `AdMng.App` references it.
- **Core/Correspondence** – pure logic: `Import` (addresses, extraction from text and tables, readers of CSV, XLSX, DOCX,
  ODT/ODS, RTF and HTML with limits and zip bomb protection), `Fields` (`{{Field}}`/`{{Field|text}}` fields, repairing fields
  split by markup, the recipient's field set), `Composition` (the message document, a renderer with `{{list}}`, the Outlook
  signature parser, a mail compliance check, grouping), `Journal` (HMAC-SHA-256 hashes and the journal file format),
  `Mailboxes` (mailbox kinds from `msExchRecipientTypeDetails`, the owner's first and last name, a person index with folding of
  Polish characters, matching with levels and delegations) and the Outlook contracts (`IOutlookClient`,
  `IOutlookSession`, `OutlookMessage`, `OutlookFailureCodes`). Fields from AD are `UserMessageFields` expressions (U1, U1d,
  the new `{$created}`).
- **Presentation/Correspondence** – `CorrespondenceViewModel` (the window, `WindowViewModelBase`) and pages:
  `OutlookConnectionViewModel`, `MailboxOwnersPageViewModel`, `RecipientsPageViewModel`, `MessagePageViewModel`,
  `PreviewPageViewModel`, `SendPageViewModel`, `ResponsesPageViewModel`. Application singletons: `CorrespondenceJournal`
  (the journal file with a `journal.lock` lock across windows and processes), `CorrespondenceTemplateLibrary` (built-in
  and operator templates, export/import, the last draft; images in the shared `MessageImageStore`) and
  `LoopbackPreviewServer` (a browser preview from memory on `127.0.0.1` under a random token, 15 minutes).
  **Scoped** (connection window): `CorrespondenceServices`, `CorrespondenceViewModelFactory`, dialogs and the launcher
  (`ICorrespondenceDialogs`, `ICorrespondenceLauncher` in the App `DialogService`). Shell commands `tools.correspondence`,
  `object.correspondence`, `object.mailboxOwners`. Settings: `CorrespondenceSettings` in `settings.json` (options,
  templates, the last draft, field definitions, mailbox options – names, LDAP filters and portable DNs, never values from the directory).
- **App/Correspondence** – `CorrespondenceWindow` (tabs, grids with field columns), `RichMessageEditor` (a WPF
  `RichTextBox`, pasting through the U1c pipeline) with the `FlowDocumentHtml` converter (document ↔ restricted, sanitized HTML),
  `DpapiKeyProtector` (the journal key, DPAPI of the current user), `WindowsCorrespondencePlatform` (the *Downloads* folder),
  `LoopbackBrowserPreview` and the `AddAdMngCorrespondenceApp()` registration (the Outlook client and the U1 transport as singletons).
  The smoke test and UI tests replace Outlook with a fake – they never start it.
- **Data** (rule 1) – the recipient list, values from AD, the preview, results, mailbox matches and responses exist only in
  the window's memory. The only exception is the "already sent" journal (`correspondence/journal.txt` next to the settings): address hashes
  (HMAC-SHA-256 with the user's key protected by DPAPI), time and subject, no addresses
  ([ADR-0003](adr/0003-no-local-data.md), amendment). The session log gets one entry per send without
  addresses and subject.
- **Tests** – `AdMng.Core.Tests/Correspondence` (file readers, fields, composition, journal, mailbox matching on the
  `AddMailboxOwnerSample` sample), `AdMng.Outlook.Tests` (a fake Outlook object model in the CLR, Linux and Windows),
  `AdMng.Presentation.Tests/Correspondence` (with `FakeOutlookClient` from `AdMng.Testing`), UI tests and screenshots 26–28 on
  Windows. Real Outlook – only the pilot.

## Security

- Sign-in and reset passwords exist only in memory; they do not reach settings, logs or `ToString()`.
- Kerberos with signing and encryption by default (compatible with LDAP signing enforcement in Windows Server 2025);
  a simple bind only over TLS or in the lab with explicit consent.
- Filters and DNs are built exclusively through `LdapFilter` / `DistinguishedName` (RFC 4515/4514 escaping).
- CSV export is protected against Excel formula injection; exporting passwords requires a separate confirmation.
- Clipboard: copy commands omit confidential values (passwords, LAPS, BitLocker, confidential attributes per schema – columns
  containing them are skipped on Ctrl+C); a secret reaches the clipboard only after being explicitly revealed in the Attribute Editor, with
  exclusion from the clipboard history and clearing after 60 s. Rows copied to Excel have the same formula protection as
  CSV export.
- Warnings for protected objects (adminCount=1, critical system objects, built-in accounts) and confirmation
  by typing the number of objects for large/risky operations.
- The application is not a security boundary – permissions are decided by ACLs in AD; the UI only reflects them.

## AdMng Watchdog – the monitoring service

AD_MNG is a console without local data, so detecting changes in the directory (who, what, when, from where) runs in a separate
process: the **AdMng Watchdog** service (ASP.NET Core + Worker, 24/7). Since version 0.3.0 it runs only on Linux –
the systemd unit `admng-watchdog.service` on a Debian 12/13 server outside the domain, with the computer account `ADMNG-WD$`
(keytab, password changed every 30 days) and the OpenWEC event collector; the Windows service with a gMSA was release 0.2.0
([ADR-0009](adr/0009-watchdog-on-linux.md)). The **Watchdog** tab in AD_MNG is its client. Features:
[WATCHDOG.md](WATCHDOG.md), deployment: [WATCHDOG-LINUX.md](WATCHDOG-LINUX.md); binding implementation specifications:
[dev/watchdog-spec.md](../dev/watchdog-spec.md) and [dev/watchdog-linux-design.md](../dev/watchdog-linux-design.md).

```text
Debian 12/13 server outside the domain (Tier 0), computer account CONTOSO\ADMNG-WD$ with a keytab
├─ admng-watchdog.service (AdMng.Watchdog, account admng-watchdog, systemd sandbox)
│   ├─ ChangeDetectionEngine ──► DC (PDC): LDAP 389, SASL GSSAPI signing + encryption – RootDSE, uSNChanged,
│   │    Extended DN, replication metadata, Deleted Objects, Configuration and Schema partitions
│   ├─ GpoContentJob ──► DC (PDC): SMB 3 (smbclient, Kerberos, signing) – GPO files from SYSVOL
│   ├─ WecFileIngestionJob ◄── /var/lib/openwec/out/<DC>/security.jsonl (OpenWEC files)
│   ├─ AttributionEngine (records 47xx, 5136…, 4624/4625/4771/4776 from the store), ScheduledScanJob, ServiceAccountMonitor
│   ├─ RuleEvaluationJob → NotificationDispatchJob → OutboxSenderJob ──► SMTP (MailKit) / Teams / webhook
│   ├─ DigestJob, HeartbeatJob, RetentionJob, StorageGuardJob
│   ├─ SQLite (WAL) /var/lib/admng-watchdog/watchdog.db: events, snapshots, DC records, queue, secrets (DP)
│   └─ HTTPS API 8443 (Kestrel), Kerberos (Negotiate, keytab), roles from tokenGroups by AD group SID
├─ openwecd.service (OpenWEC, account openwec) ◄── WEF from every DC: HTTP 5985 + Kerberos
└─ admng-watchdog-keytab.timer (password change), admng-wec-rotate.timer (OpenWEC file rotation)
                      ▲ HTTPS 8443, Kerberos
   AD_MNG – Watchdog tab (IWatchdogClient)
```

| Project | Role |
|---|---|
| `AdMng.Watchdog.Contracts` | event model, API DTOs, configuration, routes, JSON (no dependencies) |
| `AdMng.Watchdog.Core` | all the logic (detection, snapshots, attribution, rules, notification planning, email/Teams/webhook formatters, daily report) – tested on Linux with `FakeDirectorySession` and recorded events; registration `AddWatchdogCore()` |
| `AdMng.Watchdog.Service` | host: API, authorization (roles from `tokenGroups`), SQLite store, Data Protection (key in a systemd credential), transports (MailKit, `HttpClient`; the MIME body of user messages from `AdMng.Mail`), reading OpenWEC files, the SYSVOL reader (smbclient), service account self-check, job hosts; to be removed after 0.3.0 – Windows hosting, HTTP.sys, DPAPI-NG, the RPC log reader, installation scripts |
| `AdMng.Watchdog.Keytab` | the `admng-watchdog-keytab` tool: changing the `ADMNG-WD$` account password and the keytab (MIT libkrb5) |
| `packaging/`, `tools/packaging` | Debian packages `admng-watchdog` and `admng-openwec`: systemd units, maintainer scripts, building and install test |
| `AdMng.Watchdog.Client` | HTTPS/Kerberos API client (`IWatchdogClient`) used by AD_MNG |
| `AdMng.Presentation/Watchdog`, `AdMng.App/Views/Watchdog` | view models and views of the Watchdog tab |

Dependency direction: Contracts ← Core ← Service; Contracts ← Client ← Presentation/App. `AdMng.Watchdog.Core` uses
`AdMng.Core` (DN, filters, directory session), the service uses `AdMng.Directory` (LDAP).

Principles that distinguish the service from the rest of the solution:

- **Data only in the service folder** ([ADR-0008](adr/0008-watchdog-data-in-separate-folder.md), partly amends
  ADR-0003; on Linux per [ADR-0009](adr/0009-watchdog-on-linux.md)): snapshots of watched attributes and GPO
  settings, events (3 years), DC log records (logons 48 h) and the send queue live in one SQLite database
  (`secure_delete`, retention, `VACUUM INTO` copies) in `/var/lib/admng-watchdog` (0700, checked at start – the service
  does not start with broader permissions); raw events from the DCs live in OpenWEC files only until they are read. AD_MNG,
  Presentation, Core and the client write nothing from the service.
- **Channel secrets** (SMTP password, OAuth secret, Teams/webhook addresses, signing keys) only in `ISecretStore` –
  ASP.NET Core Data Protection with keys encrypted by the key from a systemd credential (TPM2 + host key or host
  key) – and **bound to the destination**: changing the
  server or address without re-entering the secret is rejected. The API never returns them.
- **Detection** polls `uSNChanged` of one DC (the PDC by default) with a snapshot keyed by `objectGUID`; DN attributes
  are read with the Extended DN control (comparison by GUID – renaming a member is not a group change); the progress marker
  (watermark) is remembered per DC, a full resynchronization only for a new DC, a DC restore or a USN rollback;
  the daily reconciliation detects objects that have "disappeared".
- **Attribution** (who and from where) is a separate, asynchronous enrichment from the Security logs of the controllers, which the
  controllers forward through Windows Event Forwarding to OpenWEC; replication metadata points to the DC and time. When the actor cannot be determined, the event shows "undetermined" with a reason code –
  never a guess.
- **Noise discipline:** deployment mode for the first 7 days, a settling window when creating accounts (a multi-step
  creation = one event), severity depending on the actor, suppression of repeats, aggregation, working hours per the
  Polish holiday calendar.
- **API:** HTTPS (TLS 1.2/1.3), Kerberos only (NTLM rejected – Kestrel in .NET 10 has no channel binding),
  Viewer/Admin roles by the SIDs of groups from a local bootstrap file that the service account cannot change (the service
  reads membership itself from `tokenGroups`); CSRF protection
  with the `X-AdMng-Client` header; configuration changes are themselves an event (Tier 0 cannot be silenced through the API).
- **Diagnostic logs without directory values** – exceptions only through `SafeExceptionLog` (type, codes, stack –
  never `Message`), a redaction filter on the log providers.

## Environment readiness and problem reporting (stage S)

- **Environment readiness** (`AdMng.Presentation.Readiness`): a shell document per connection window. `ReadinessChecker`
  checks the areas one after another through the window's existing services (session, controller directory, SYSVOL, remote actions, event
  logs, Watchdog, Outlook) and `ILocalComputerInfo` from App; each result has a fixed code (`ReadinessCodes`), a state,
  an explanation, a hint and a manual chapter. Read-only: the write right to `Policies` is evaluated by
  `Core.Readiness.FolderWriteAccess` from the folder's DACL and the account's `tokenGroups`, without attempting a write. Reading event
  logs goes to every DC, LDAP stays with the window's controller (rule 4).
- **Results** go to the window's `IReadinessResults` (scoped) as code–state pairs without names – the problem report reads them.
- **Report a problem** (`AdMng.Presentation.Diagnostics`): `DiagnosticLoggerProvider` writes to `DiagnosticLogBuffer`
  (singleton, 200 entries in memory) only the level, category, event identifier, the message **template** and the exception type +
  HResult – never formatted arguments, exception messages or scopes, so no directory value
  can get into it (rule 1). `ProblemReportViewModel` assembles from it, from session facts (the kind of sign-in and channel protection,
  without names) and from the readiness results a text that the operator copies or saves.

## Tests and quality

| Level | Where | How |
|---|---|---|
| Unit | `AdMng.Core.Tests`, `AdMng.Presentation.Tests` | xUnit v3, `FakeDirectorySession`; view model tests on a single UI thread (`UiThreadAttribute`), as in WPF; time limits as logical bounds and linear growth of work (`LinearWork`), not wall-clock time |
| Integration | `AdMng.Directory.IntegrationTests` | Samba AD DC from `tools/testlab` (locally and in CI on Ubuntu) |
| Outlook (U5) | `AdMng.Outlook.Tests` | a fake Outlook object model in the CLR: order of message-building steps, `cid:` images, error classification, the STA thread, temporary files, votes, the U1 transport (Linux and Windows); real Outlook – only the pilot |
| UI smoke | Windows CI | `AdMng.exe --smoke-test` creates all views with test data |
| Watchdog – engine | `AdMng.Watchdog.Core.Tests` | `FakeDirectorySession` with USN, metadata and controller emulation; recorded 47xx/5136 events; a fake clock |
| Watchdog – service | `AdMng.Watchdog.Service.Tests` | an in-process host (`WebApplicationFactory`), SQLite in a temporary folder, SMTP/HTTP sinks, log hygiene tests, reading OpenWEC files with a failure at every step, parsing the smbclient listing, roles from `tokenGroups`; Windows-only tests in Windows CI |
| Watchdog – lab | `AdMng.Watchdog.IntegrationTests` | real engines from `AddWatchdogCore()` against a Samba AD DC (read-only account), the scenarios from 22.4 of the specification in a disposable OU, the service end-to-end with Mailpit and a signed webhook, a computer account with a keytab (password change, GSSAPI, Negotiate with roles), OpenWEC, GPO content from SYSVOL |
| Watchdog – packages | CI Ubuntu (`openwec-package`, `linux-package`) | building `admng-openwec` (Debian 12 and 13, GPL-3.0 sources) and `admng-watchdog`, an install test in Debian 12 and 13 containers (accounts, modes, credential, `systemd-analyze verify` and `security`, `--smoke-test`, start with TLS, removal), lintian |
| Watchdog – tools | `tools/testlab/watchdog` | the service in lab mode, Mailpit, a webhook receiver, the `lab-scenario.sh --verify` scenario (also in CI) |
| Watchdog – smoke | CI (Windows and the package install test) | `--smoke-test` of the published service (migrations, `/healthz`, `/api/v1/status`, all formatters); in the package as the `admng-watchdog` account |
| Static | build | warnings = errors, code style analysis |
