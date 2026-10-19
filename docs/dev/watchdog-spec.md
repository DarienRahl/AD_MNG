# AdMng Watchdog – implementation specification (v1)

Status: binding for the scaffold step and the implementation agents (W-core – split into W-detect, W-rules and W-notify, see 25.5 – W-service, W-gui, W-testenv).
Revision 2 (after the operations, security and implementability reviews – see "Critique resolution" at the end).
Date: 2026-10-14. Product docs: `docs/WATCHDOG.md` (Polish), decision record: `docs/adr/0008-watchdog-dane-w-osobnym-folderze.md`.
Language rules from `CLAUDE.md` apply: code/comments/commits in English, product docs in Polish, every user-visible
string through resx (en + pl).

This document is self-contained: an agent must be able to implement its part from it without the research notes.
Where it says **MUST** it is a requirement; **SHOULD** is a strong default an agent may deviate from only with a
reason in its final report.

---

## 0. Key decisions (summary)

| # | Decision | Why |
|---|---|---|
| D1 | A separate Windows service **AdMng Watchdog** (`AdMng.Watchdog.exe`, .NET 10, ASP.NET Core + Worker) runs 24/7 under a **gMSA** with read-only AD rights. The WPF app gets a **Watchdog document tab** that talks to the service over **HTTPS + Kerberos (Negotiate)**. | User decision. Detection needs a continuously running process and history; the WPF app must stay storage-free. |
| D2 | **Detection = uSNChanged polling** pinned to one DC (PDC emulator by default) with an **objectGUID-keyed snapshot of watched attributes only**, baseline on first run (no events), watermark stored **per DC** (dsServiceName + invocationId), full resync only for a never-seen DC / invocationId change / USN rollback (failback to a known DC continues incrementally), daily reconciliation. Change notifications are only an optional wake-up. DirSync is not used in v1. | Works for a read-only account, verified on the Samba lab; DirSync hides deletions from non-admins and its cookies are DC-bound. |
| D3 | **DN-valued attributes are read with the Extended DN control** (`<GUID=…>;<SID=…>;dn`) and compared by GUID. | Moves/renames of a referenced object rewrite DN values without bumping the referencing object's USN – plain DN comparison would produce false "manager changed"/"member changed" events. |
| D4 | **Attribution** (who / from where) is a separate, asynchronous enrichment from DC **Security logs** (4720…4781, 4741-4743, group events, 5136-5141, 4624 for the source computer). LDAP is the source of truth for *what* changed; replication metadata tells *which DC* and *when* to look. Attribution may arrive later; events are updated in place; notifications wait a short grace period. | Netwrix-style "System" mis-attribution must never happen; the actor is shown as "nieustalony" with a reason code instead of a guess. |
| D5 | Storage: one **SQLite** database (WAL, `secure_delete`, `temp_store = MEMORY`) in the watchdog's **own data folder** (`%ProgramData%\AdMng\Watchdog\data` by default; protected DACL SYSTEM / Administrators / gMSA, **enforced at start** – the service refuses to run with a wider ACL). The bootstrap file lives in a separate `config\` folder the gMSA can only read. Secrets (SMTP password, OAuth client secret, Teams/webhook URLs, webhook signing keys) are encrypted with **ASP.NET Core Data Protection, keys protected with DPAPI-NG to the gMSA SID** and **bound to their destination** (host, account, tenant) – changing the destination without re-entering the secret is rejected. | ADR-0008 relaxes ADR-0003 only for this folder and only for the service host project. |
| D6 | Channels per rule: **immediate e-mail (aggregated)**, **daily digest e-mail**, **Microsoft Teams (Power Automate Workflows webhook, Adaptive Card 1.4)**, **generic webhook (Standard Webhooks v1 HMAC)**. No Windows Event Log channel. Office 365 Connector URLs (`*.webhook.office.com`) were retired on 2026-05-22 and are rejected. | User decision + research. |
| D7 | E-mail via **MailKitLite 4.18.1** (never `System.Net.Mail`, TLS 1.2/1.3 only); modes: anonymous relay (STARTTLS required by default), Basic (TLS only), **Exchange Online OAuth2 client credentials (MSAL 4.90.1, XOAUTH2, fixed Entra cloud authority/scope, XOAUTH2 only to allow-listed hosts)**. Graph sendMail and Integrated Windows auth are phase 2. | Exchange Online SMTP AUTH Basic is disabled by default from end of December 2026. |
| D8 | API host: **Kestrel + Negotiate, Kerberos only, HTTP/1.1, TLS 1.2/1.3, certificate from LocalMachine\My**; SPN `HTTP/<dedicated A record>` on the gMSA. **HTTP.sys** is a supported alternative selected in `appsettings.json` (Windows only; scheme Kerberos, not Negotiate). A server-agnostic middleware rejects every non-Kerberos identity. Authorization by **AD group SIDs** from the Windows token (roles Viewer, Admin, plus an emergency deny list), configured locally in a bootstrap file the service account cannot write (never editable through the API). | One stack for Windows service, console mode and Linux tests; real Negotiate can be tested against Samba. .NET 10 has no channel binding on Kestrel, so NTLM is rejected everywhere. |
| D9 | Live updates in the GUI by **cursor polling with optional long-poll** (`GET /api/v1/events?after=N&wait=25`). No SignalR. SSE is phase 2. | Simple, restart-safe, testable. |
| D10 | Everything is split **one feature per file**: pure logic in `AdMng.Watchdog.Core` (net10.0, unit-tested on Linux with `FakeDirectorySession` and XML fixtures), infrastructure in `AdMng.Watchdog.Service`, DTOs/config in `AdMng.Watchdog.Contracts`, HTTP client in `AdMng.Watchdog.Client`, view models in `AdMng.Presentation/Watchdog`, XAML in `AdMng.App/Views/Watchdog`. | User request + parallel implementation. |
| D11 | **Noise discipline by default**: a 7-day **rollout mode** after the baseline (only tier0, bursts and health notify immediately; everything else goes to the digest with "would have sent N" counters), creation **settle window** (multi-step account creation = one event), attribution-dependent severities (a privileged user's own password change is not an alert), per-object repeat suppression (lockouts), deletion and privilege rules that ignore system churn (BitLocker children, DNS, SDProp). | Ops review: an alerting tool that pages for routine activity gets disabled – together with its real detections. |
| D12 | **The monitor protects itself**: changes of the watchdog configuration raise `WatchdogConfigurationChanged` (delivered with the *previous* recipients), Tier-0 detections cannot be suppressed through the API without a local bootstrap flag, the watchdog role groups are treated as privileged, `/healthz` and an optional webhook heartbeat let external monitoring notice an outage. The watchdog host and gMSA are **Tier 0** assets. | Security review: an API admin (ordinary AD group) or a Tier-1 compromise must not be able to blind Tier-0 alerting silently. |

### 0.1 Repository conventions every agent applies

- `TreatWarningsAsErrors`, nullable, `EnforceCodeStyleInBuild` (Directory.Build.props) – 0 warnings; Windows-only APIs
  only behind `[SupportedOSPlatform("windows")]` + `OperatingSystem.IsWindows()` (CA1416).
- Central package versions only (`Directory.Packages.props`), no versions in csproj.
- File-scoped namespaces, `_camelCase` fields, braces always, one type per file (tiny enums next to their record are
  allowed where section 3 says so), XML docs on non-obvious public APIs.
- LDAP filters only through `AdMng.Core.Ldap.LdapFilter`, DNs only through `DistinguishedName`; never concatenate
  values.
- No directory values in logs, exception messages or `ToString()`; no secrets anywhere outside `ISecretStore`.
  Exceptions are logged **only** through `SafeExceptionLog` (Core, `Diagnostics/SafeExceptionLog.cs`): exception type,
  HResult, `ResultCode`/Win32 error/`SocketErrorCode` and the stack trace – never `Message`, `ToString()` or `Data`
  (`DirectoryOperationException`, `FormatException` (.NET 8+ includes the input), `XmlException` and MailKit
  `SmtpCommandException` messages carry directory values or addresses). Enforced by `LogHygieneTests` (22.2).
- Projects with `Sdk.Web` or a `FrameworkReference` to `Microsoft.AspNetCore.App` never reference packages the
  shared framework already supplies (`Microsoft.Extensions.*`, `System.Diagnostics.EventLog`, …) – .NET 10 package
  pruning turns that into `NU1510` (an error here).
- Every user-visible string through resx with en + pl entries (GUI: `AdMng.Presentation/Resources/Strings*.resx`;
  notifications: `AdMng.Watchdog.Core/Resources/NotificationStrings*.resx`).
- Async everywhere with `CancellationToken`; the GUI never blocks; `TimeProvider` instead of `DateTime.Now`.
- Tests: xUnit v3 `Assert`, fakes (no mocking libraries, no FluentAssertions), Polish test data with diacritics.

---

## 1. Goals and non-goals

### 1.1 Goals (v1)

1. Detect and record, for one AD domain:
   - new user / computer / group / OU / gMSA / contact objects, deletions, restores, moves, renames;
   - account parameter changes (watched attributes, old → new), enable/disable, lockout/unlock, password set/reset,
     `userAccountControl` flag changes, account name changes, expiration changes;
   - group membership changes (direct) and **effective privileged access** changes (transitive, incl. primary group);
   - Tier-0 changes: adminCount, sIDHistory, delegation, SPN on users, msDS-KeyCredentialLink, primaryGroupID,
     dMSA migration links (both sides), explicit certificate mappings (`altSecurityIdentities`), gMSA password
     readers, security descriptor of critical objects, trusts, DCs added/removed, FSMO role moves, certificate
     templates / enrollment services / NTAuth, domain and forest policy (password/lockout, PSOs, Recycle Bin,
     dSHeuristics, tombstoneLifetime), GPO links, schema changes, Security log cleared, audit policy changed, DSRM
     password;
   - time-based conditions: accounts expiring in N days (default 14/7/1, counted in calendar days to the last valid
     day, moved to the previous business day), passwords of privileged/service accounts expiring, mass lockouts, mass
     deletions, optional password-spraying detection from failed-logon events;
   - watchdog health (DC unreachable, resync, permission gaps, attribution degraded, event-log gaps, delivery failures,
     storage, outdated runtime) and changes of the watchdog's own configuration.
2. Attribute each change to an actor (account, SID) and source (workstation, IP) when DC auditing allows it, with an
   explicit confidence level and a reason code when it does not.
3. Notify per configurable rules over e-mail (immediate, aggregated), daily digest, Teams and generic webhooks, with
   throttling, quiet hours (Polish business calendar), exclusions, follow-ups when attribution arrives late.
4. Offer a **Watchdog** document tab in AD_MNG: event feed with filters and details, rule editor with preview,
   channel/SMTP configuration with test sends, digest preview, health/status, delivery log.
5. Be deployable by a Windows admin following `docs/WATCHDOG.md`; be fully testable on Linux (unit + service tests +
   Samba integration) and on Windows CI (UI tests, published service smoke test).

### 1.2 Non-goals (v1)

- Blocking or undoing changes (read-only service; no "auto undo").
- Entra ID / Microsoft 365 auditing, GPO content diffs (only `versionNumber`, links, flags), file-server auditing.
- Multiple domains in one service instance (one instance per domain; documented).
- Windows Event Log as a notification channel (explicitly declined by the user). The service writes only its own
  diagnostic events (without directory values) to the Application log.
- DirSync-based detection, msDS-ReplValueMetaData per-value timing, honeytoken rules, planned-change correlation with
  AD_MNG change plans, Microsoft Graph `sendMail`, Integrated Windows SMTP auth, SSE live stream, Samba JSON audit
  attribution source, a web UI beyond the read-only HTML event page, time-bound (PAM TTL) membership expiry timers
  (expiry is found by the nightly reconciliation), per-request `tokenGroups` re-check of API callers (the bootstrap
  deny list covers emergencies), NuGet lock files – all **phase 2** (hooks are reserved where noted).
- The WPF app never persists watchdog data (events, configs) locally – it only keeps the service URL in its settings.

---

## 2. Architecture

```
                    ┌──────────────── Windows server (domain member), gMSA CONTOSO\svc-admngwd$ ───────────────┐
                    │ AdMng.Watchdog.exe  (Windows service "AdMngWatchdog"; console mode for dev/lab)          │
                    │                                                                                          │
  DC (PDC) ◄─LDAP──►│ ChangeDetectionEngine ──► DetectionCommit ──┐                                            │
  389 sign+seal     │   RootDSE poll, uSNChanged pages,           │      SQLite  <DataDir>\watchdog.db (WAL)   │
                    │   Extended DN, Show Deleted, metadata       ├────► snapshots, events, outbox, cursors,   │
  all DCs ◄─RPC────►│ AttributionEngine (Security log reader) ────┤      config, secrets(DP), notices          │
  (Event Log        │ ScheduledScanJob (expiry, lockout, self-check)                                           │
   Readers)         │ RuleEvaluationJob ─► NotificationDispatchJob ─► OutboxSenderJob ──► SMTP / Teams / webhook│
                    │ DigestJob, RetentionJob                                                                  │
                    │ ASP.NET Core API (Kestrel/HTTP.sys, HTTPS, Negotiate/Kerberos, roles by AD group SID)    │
                    └──────────────────────────────▲───────────────────────────────────────────────────────────┘
                                                   │ HTTPS 8443, Kerberos (HTTP/admng-wd.contoso.com on the gMSA)
                    ┌──────────────────────────────┴───────────────────────────────┐
                    │ AD_MNG (WPF) – document tab "Watchdog" (IWatchdogClient)      │
                    │ events feed · rules · channels/SMTP · digest · status         │
                    └──────────────────────────────────────────────────────────────┘
```

Dependency direction (MUST):

```
AdMng.Watchdog.Contracts  (no project references)
AdMng.Watchdog.Core       → AdMng.Core, AdMng.Watchdog.Contracts
AdMng.Watchdog.Service    → AdMng.Watchdog.Core, AdMng.Directory, AdMng.Watchdog.Contracts
AdMng.Watchdog.Client     → AdMng.Watchdog.Contracts
AdMng.Presentation        → (existing) + AdMng.Watchdog.Contracts, AdMng.Watchdog.Client
AdMng.App                 → (existing) + AdMng.Watchdog.Client (registration only)
```

Processing pipeline inside the service (each step is an `IWatchdogJob` in Core, hosted by a generic `JobHost` in the
Service; steps communicate through the store and `IWatchdogSignals`):

```
ChangeDetectionEngine / ScheduledScanJob / AttributionEngine(event-log-only kinds) / HealthRegistry
        │ events (rules_state = New)
        ▼
RuleEvaluationJob: RuleEngine.Evaluate → event severity + matched rules → PendingNotification rows (hold for attribution / quiet hours / aggregation)
        │
        ▼
NotificationDispatchJob: due buckets → formatters (e-mail / Teams card / webhook JSON) → OutboxMessage rows
        │
        ▼
OutboxSenderJob: lease due → INotificationTransport (MailKit / HttpClient) → Sent | retry with backoff | Dead + channel health
DigestJob (daily at configured local time) → digest OutboxMessages
AttributionEngine: ingest Security events → correlate pending events → update attribution → signal (follow-ups, releases holds)
RetentionJob (daily): purge by retention settings, VACUUM INTO backup
```

---

## 3. Repository layout – every file

Notation: `[S]` created by the scaffold step (section 25.1) with final content; `[S*]` created by the scaffold as a
compilable shell (stub) that the owning agent replaces; owner in brackets for everything else. "One file = one type"
unless a file explicitly lists several tiny related types (enums next to their record).

### 3.1 `src/AdMng.Watchdog.Contracts` (net10.0, no references) – owner after scaffold: see 25.3

```
AdMng.Watchdog.Contracts.csproj                    [S]
WatchdogRoutes.cs                                  [S] route constants, API version, header names
WatchdogJson.cs                                    [S] JsonSerializerOptions factory + WatchdogJsonContext (source-gen)
Events/Severity.cs                                 [S]
Events/WatchEventKind.cs                           [S]
Events/WatchObjectKind.cs                          [S]
Events/EventCategory.cs                            [S]
Events/DetectionSource.cs                          [S]
Events/EventTags.cs                                [S] string constants for tags
Events/WatchEvent.cs                               [S]
Events/ObjectRef.cs                                [S] ObjectRef + PrincipalRef
Events/AttributeChange.cs                          [S] AttributeChange + AttributeValueKind
Events/Attribution.cs                              [S] Attribution, AttributionStatus, AttributionConfidence, EvidenceRef
Events/AttributionReasons.cs                       [S] reason code constants
Events/ExpiryInfo.cs                               [S] ExpiryInfo + ExpiryKind
Events/LockoutInfo.cs                              [S] LockoutInfo + LockoutSource
Events/BurstInfo.cs                                [S]
Events/EventKindCatalog.cs                         [S] EventKindInfo + static catalog (defaults table of section 7)
Api/EventQuery.cs                                  [S] + ToQueryString()
Api/EventPage.cs                                   [S]
Api/EventDetails.cs                                [S] EventDetails + EventDeliveryDto
Api/AcknowledgeRequest.cs                          [S] AcknowledgeRequest + BulkAcknowledgeRequest
Api/MeDto.cs                                       [S] MeDto + WatchdogRole
Api/StatusDto.cs                                   [S] StatusDto, DirectoryStatusDto, AttributionStatusDto, DcLogStatusDto,
                                                       StorageStatusDto, EventCountsDto, ExclusionSuggestionDto
Api/HealthCheckDto.cs                              [S] HealthCheckDto + HealthState
Api/ChannelHealthDto.cs                            [S]
Api/ConfigEnvelope.cs                              [S]
Api/ConfigValidationResult.cs                      [S] ConfigValidationResult, ConfigIssue, IssueSeverity
Api/TestRequests.cs                                [S] TestEmailRequest, TestChannelRequest
Api/DeliveryTestResult.cs                          [S] DeliveryTestResult, DeliveryStageResult, DeliveryStage, DeliveryHints
Api/RulePreview.cs                                 [S] RulePreviewRequest, RulePreviewResult, DailyCount
Api/DigestPreviewDto.cs                            [S]
Api/OutboxDtos.cs                                  [S] OutboxQuery, OutboxPage, OutboxItemDto, OutboxStatus, DeliveryKind,
                                                       DeliveryChannelType
Api/ConfigAuditDtos.cs                             [S] ConfigAuditPage, ConfigAuditEntry
Api/ApiProblem.cs                                  [S] problem type URIs / error codes shared by server and client
Configuration/WatchdogConfiguration.cs             [S]
Configuration/GeneralSettings.cs                   [S]
Configuration/DirectorySettings.cs                 [S] + MembershipTracking enum
Configuration/AttributionSettings.cs               [S] + AttributionSourceKind enum
Configuration/ScanSettings.cs                      [S] ScanSettings, AccountExpiryScan, PasswordExpiryScan(+Scope enum),
                                                       InactiveAccountsScan, BurstSettings
Configuration/PrivilegedGroupSettings.cs           [S]
Configuration/BusinessHoursSettings.cs             [S]
Configuration/NotificationSettings.cs              [S]
Configuration/SmtpSettings.cs                      [S] SmtpSettings, SmtpSecurity, SmtpAuthentication, SmtpOAuthSettings,
                                                       CertificateValidationMode, EntraCloud
Configuration/ChannelSettings.cs                   [S] ChannelSettings, ChannelType, DetailLevel, TeamsChannelOptions,
                                                       TeamsAuthMode, WebhookChannelOptions, RateLimitSettings
Configuration/RecipientGroup.cs                    [S]
Configuration/DigestSettings.cs                    [S]
Configuration/RuleSettings.cs                      [S] RuleSettings, RuleScope, RuleDelivery, DeliveryHours, GroupFilter,
                                                       ActorFilter, RuleExclusion
Configuration/RetentionSettings.cs                 [S]
Configuration/SecretField.cs                       [S]
Configuration/EntraCloudInfo.cs                    [S] per-cloud authority host, SMTP scope, allowed Power Automate scopes
Configuration/SecretNames.cs                       [S] canonical secret names ("smtp.password", "channel.{id}.url", …)
```

### 3.2 `src/AdMng.Watchdog.Core` (net10.0) – owner W-core

```
AdMng.Watchdog.Core.csproj                                   [S]
WatchdogCoreServiceCollectionExtensions.cs                   [S*] AddWatchdogCore(): registers every Core implementation
Abstractions/IDetectionStore.cs                              [S]
Abstractions/IEventStore.cs                                  [S]
Abstractions/INotificationQueueStore.cs                      [S]
Abstractions/IOutboxStore.cs                                 [S]
Abstractions/ISecurityEventStore.cs                          [S]
Abstractions/IExpiryNoticeStore.cs                           [S]
Abstractions/IDigestRunStore.cs                              [S]
Abstractions/IConfigStore.cs                                 [S] + ConfigVersionConflictException
Abstractions/ISecretStore.cs                                 [S]
Abstractions/IStoreMaintenance.cs                            [S]
Abstractions/IDirectorySessionProvider.cs                    [S] + DirectoryConnectionState
Abstractions/ISecurityEventSource.cs                         [S] + SecurityEventQuery, SecurityLogInfo
Abstractions/INotificationTransport.cs                       [S]
Abstractions/IWatchdogJob.cs                                 [S] + JobResult
Abstractions/IWatchdogSignals.cs                             [S] + WatchdogSignal, ISignalSubscription
Abstractions/IWatchdogConfigProvider.cs                      [S]
Abstractions/IHealthRegistry.cs                              [S]
Abstractions/IConfigurationValidator.cs                      [S] + ConfigValidationContext
Abstractions/IDefaultConfigurationFactory.cs                 [S]
Abstractions/IRulePreviewService.cs                          [S]
Abstractions/IDigestService.cs                               [S] preview + send-now
Abstractions/IEventHtmlRenderer.cs                           [S]
Abstractions/IDeliveryTester.cs                              [S]
Model/DcState.cs                                             [S] + DcWatermark
Model/SnapshotObject.cs                                      [S] SnapshotObject + SnapshotAttribute
Model/MemberReference.cs                                     [S]
Model/PrivilegeState.cs                                      [S] PrivilegeState + PrivilegedPrincipal
Model/DetectionCommit.cs                                     [S] + DnRewrite
Model/DetectionCycleResult.cs                                [S] + DetectionMode
Model/SecurityEvent.cs                                       [S]
Model/SecurityEventCriteria.cs                               [S]
Model/EventLogCursor.cs                                      [S]
Model/EventEvaluation.cs                                     [S] + AttributionUpdate
Model/PendingNotification.cs                                 [S] + NotificationTarget
Model/OutboxMessage.cs                                       [S] + RenderedEmail, RenderedHttpMessage
Model/DeliveryResult.cs                                      [S] + DeliveryOutcome
Model/ExpiryNotice.cs                                        [S] + UpcomingExpiry
Model/SecretValue.cs                                         [S] SecretValue, StoredSecret, SecretChange
Model/PurgeResult.cs                                         [S]
Diagnostics/SafeExceptionLog.cs                              [S] the only way to log an exception (0.1)
Configuration/SecretBindings.cs                              destination fingerprint per secret (section 18)
Configuration/TimeZoneResolver.cs                            IANA/Windows id → TimeZoneInfo incl. built-in map (NLS hosts)
Configuration/ConfigChangeAuditor.cs                         old vs new config → WatchdogConfigurationChanged (severity, summary)
Configuration/ConfigRedactor.cs                              Viewer view of the configuration (17.2)
Configuration/ConfiguredSecret.cs                            one secret field + expected binding (SecretBindings.Enumerate)
Configuration/ConfigChangeAudit.cs                           ConfigChangeAuditor.Compare result (severity, items, summary)
Configuration/ConfigChangeItem.cs                            ConfigChangeItem + ConfigChangeKind
Detection/ChangeDetectionEngine.cs                           [S*] IWatchdogJob: one poll cycle (section 10)
Detection/DetectionModeSelector.cs                           decides Baseline / Incremental / Resync / Reconciliation
Detection/DcStateReader.cs                                   RootDSE + invocationId + PDC lookup
Detection/DomainControllerDirectory.cs                       nTDSDSA/server enumeration, invocationId → host map
Detection/WatchdogAttributes.cs                              attribute-name constants not in AdMng.Core.Attr
Detection/WatchedAttributes.cs                               per-class watched attribute sets (section 9)
Detection/NoiseAttributes.cs                                 central noise list + computer-noise list
Detection/DetectionFilters.cs                                LDAP filters (built with LdapFilter only)
Detection/ValueCanonicalizer.cs                              LDAP values → canonical strings (DN by GUID, SID, FILETIME, hash)
Detection/SnapshotBuilder.cs                                 DirectoryObject → SnapshotObject
Detection/ObjectDiffer.cs                                    snapshot vs snapshot → RawObjectDiff
Detection/RawObjectDiff.cs                                   diff model (internal)
Detection/ChangeClassifier.cs                                RawObjectDiff → WatchEvent[] (kinds of section 7)
Detection/UacFlagClassifier.cs                               userAccountControl bit diff → kinds/flags/severity
Detection/EventTagger.cs                                     tags: privileged, serviceAccount, defaultContainer, …
Detection/WellKnownContainers.cs                             wellKnownObjects of the domain head (users/computers, redirected?)
Detection/LockoutPolicyResolver.cs                           effective lockoutDuration (domain or PSO via msDS-ResultantPSO)
Detection/CreationSettler.cs                                 settle window: deferred Created events with the final state
Detection/RestoredMembershipReader.cs                        memberOf of a restored object → member lists (Recycle Bin)
Detection/SyncAccountDetector.cs                             Entra Connect / sync accounts → exclusion suggestions
Detection/SeverityResolver.cs                                catalog default + tag bumps + explicit overrides (section 12.2)
Detection/MembershipReader.cs                                member values (Extended DN, ranged) → MemberReference[]
Detection/MembershipDiffer.cs                                old/new member sets → member events
Detection/PrivilegeEvaluator.cs                              transitive privileged set + diff → PrivilegeGranted/Revoked
Detection/DeletedObjectsScanner.cs                           Show Deleted pass
Detection/Reconciler.cs                                      full scan diff incl. disappeared GUIDs
Detection/DnRewriter.cs                                      descendant DN rewrite on container move/rename
Detection/ConfigurationPartitionWatcher.cs                   nTDSDSA added/removed, FSMO holders, Directory Service object,
                                                             Recycle Bin feature, PKI objects, schema increments (10.11)
Detection/SecurityDescriptorSummary.cs                       managed SD parser: owner, ACE list, SHA-256
Detection/ReplicationMetadataXmlParser.cs                    msDS-ReplAttributeMetaData (Windows) parser
Detection/ReplicationMetadataDecoder.cs                      replPropertyMetaData binary decoder
Detection/AttrTypResolver.cs                                 ATTRTYP → lDAPDisplayName (prefix table, msDS-IntId)
Detection/ReplicationMetadata.cs                             AttributeMetadata record + lookup
Detection/ChangeNotificationWaker.cs                         optional WatchAsync wake-up
Detection/PermissionSelfCheck.cs                             read-permission probes (section 10.12)
Scans/ScheduledScanJob.cs                                    [S*] IWatchdogJob running the scanners below on their intervals
Scans/AccountExpiryScanner.cs
Scans/PasswordExpiryScanner.cs
Scans/ExpiryThresholds.cs                                    threshold math + dedup key
Scans/LockoutMonitor.cs                                      currently-locked set, lockout expiry
Scans/BurstDetector.cs                                       mass lockout + mass deletion
Scans/PasswordSprayDetector.cs                               optional: failed-logon events per source IP (10.15)
Scans/InactiveAccountsReport.cs                              weekly digest section (no events)
Attribution/AttributionEngine.cs                             [S*] IWatchdogJob: ingest + correlate + finalize
Attribution/SecurityEventParser.cs                           event XML → SecurityEvent
Attribution/InsertionStringDecoder.cs                        %%1792…, %%14674…, %%20xx
Attribution/SamAccountControlDecoder.cs                      Old/NewUacValue (ACB bits)
Attribution/SecurityEventCatalog.cs                          event IDs per WatchEventKind, ingestion set
Attribution/SecurityEventQueryBuilder.cs                     SecurityEventQuery → Windows QueryList XML
Attribution/AttributionCorrelator.cs                         candidate selection + scoring
Attribution/AttributionScorer.cs                             score table
Attribution/LogonSourceResolver.cs                           SubjectLogonId → 4624 → workstation/IP (bounded lookups)
Attribution/LockoutSourceResolver.cs                         4771/4776/4625 around a lockout → source IPs/workstations
Attribution/EventSourceGuard.cs                              provider + known-DC checks on every parsed record
Attribution/OriginatingDcResolver.cs                         metadata → DC host + time window
Attribution/EventLogGapDetector.cs                           cursor regressions, 1102/1104/1100, overwritten logs
Attribution/EventLogOnlyDetector.cs                          1102/4719/4794 → WatchEvents
Rules/RuleEvaluationJob.cs                                   [S*] IWatchdogJob
Rules/RuleEngine.cs                                          event × rules → matches
Rules/RuleMatcher.cs                                         one rule vs one event
Rules/ScopeMatcher.cs
Rules/ExclusionMatcher.cs
Rules/DefaultRules.cs                                        built-in rules (section 12.4)
Rules/DefaultConfigurationFactory.cs                         implements IDefaultConfigurationFactory
Rules/ConfigurationValidator.cs                              implements IConfigurationValidator (section 8.3)
Rules/RulePreviewService.cs                                  implements IRulePreviewService
Rules/WatchdogConfigProvider.cs                              implements IWatchdogConfigProvider (cached, ConfigChanged)
Rules/ConfigTokens.cs                                        {domain}, {domainSid}, {rootDomainSid} expansion
Rules/RolloutMode.cs                                         rollout-mode window (12.5)
Rules/Tier0Catalog.cs                                        Tier-0 kinds/attributes that cannot be suppressed (8.3)
Notifications/NotificationPlanner.cs                         matches → PendingNotification rows (holds, quiet hours)
Notifications/AggregationPolicy.cs                           bucket keys, flush delays, max items
Notifications/ThrottlePolicy.cs                              per rule × channel hourly cap
Notifications/BusinessCalendar.cs                            business hours check (time zone)
Notifications/PolishHolidays.cs                              PL statutory holidays incl. Easter-based and 24 Dec
Notifications/NotificationDispatchJob.cs                     [S*] IWatchdogJob: due buckets → render → outbox
Notifications/OutboxSenderJob.cs                             [S*] IWatchdogJob: lease → transport → result
Notifications/RetryPolicy.cs                                 per channel type schedules (section 15)
Notifications/FollowUpPlanner.cs                             attribution-resolved follow-ups
Notifications/DigestBuilder.cs                               DigestModel from events + scans
Notifications/DigestModel.cs
Notifications/DigestJob.cs                                   [S*] IWatchdogJob + IDigestService
Notifications/Schedule.cs                                    next occurrence math (local time, DST)
Notifications/HealthRegistry.cs                              implements IHealthRegistry
Notifications/WatchdogSignals.cs                             implements IWatchdogSignals (per-subscription latches)
Notifications/HeartbeatJob.cs                                IWatchdogJob: watchdog.heartbeat to channels with HeartbeatInterval
Formatting/NotificationText.cs                               resx access with explicit culture
Formatting/UntrustedText.cs                                  bidi/zero-width/control escapes + URL defanging of AD strings
Formatting/AttributeDisplay.cs                               attribute display names + value rendering (FILETIME, UAC, …)
Formatting/DetailLevelFilter.cs                              Minimal/Standard/Full + secret masking
Formatting/HtmlWriter.cs                                     tiny HTML builder, encodes everything
Formatting/EmailSubjectBuilder.cs
Formatting/EmailAlertFormatter.cs
Formatting/EmailDigestFormatter.cs
Formatting/EventHtmlRenderer.cs                              implements IEventHtmlRenderer (HTML event page)
Formatting/AdaptiveCards/AdaptiveCardBuilder.cs              typed builder (AC 1.4 subset)
Formatting/AdaptiveCards/AdaptiveElements.cs                 element records (TextBlock, RichTextBlock, TextRun, …)
Formatting/AdaptiveCards/TeamsAlertCardFormatter.cs
Formatting/AdaptiveCards/TeamsDigestCardFormatter.cs
Formatting/AdaptiveCards/CardBudget.cs                       24 KiB truncation
Formatting/Webhooks/WebhookPayloadFormatter.cs
Formatting/Webhooks/WebhookEventTypes.cs
Formatting/Webhooks/StandardWebhookSigner.cs
Formatting/Webhooks/TeamsUrlValidator.cs
Formatting/Webhooks/TeamsUrlValidationResult.cs              TeamsUrlValidationResult + TeamsUrlProblem
Retention/RetentionJob.cs                                    [S*] IWatchdogJob
Resources/NotificationStrings.resx                           en (neutral)
Resources/NotificationStrings.pl.resx                        pl
```

### 3.3 `src/AdMng.Watchdog.Service` (Microsoft.NET.Sdk.Web, net10.0, AssemblyName `AdMng.Watchdog`) – owner W-service

```
AdMng.Watchdog.Service.csproj                       [S]
Program.cs                                          [S*] top-level: WatchdogHost.Build(args).Run / --smoke-test / --check-config / --health
appsettings.json                                    [S*] bootstrap defaults (section 8.4)
appsettings.Development.json                        [S*]
Properties/launchSettings.json                      lab/dev profiles
Hosting/WatchdogHost.cs                             [S*] composition root: Build(string[] args, Action<WebApplicationBuilder>? configure)
Hosting/ServiceOptions.cs                           bootstrap options record bound from "Watchdog" section
Hosting/DataDirectory.cs                            create with protected DACL, verify ACL allow-list (Windows), paths
Hosting/BootstrapFileGuard.cs                       refuses to start when the service identity can write the bootstrap file
Hosting/KestrelSetup.cs                             HTTPS endpoint, HTTP/1.1, TLS 1.2/1.3, certificate selector
Hosting/HttpSysSetup.cs                             [SupportedOSPlatform("windows")] alternative server (scheme Kerberos)
Hosting/CertificateSelector.cs                      LocalMachine\My lookup by thumbprint/subject + key-access self-check
Hosting/JobHost.cs                                  BackgroundService running one IWatchdogJob (signal subscription, backoff)
Hosting/IProcessTerminator.cs                       Exit(code) abstraction (tests replace it) + EnvironmentProcessTerminator
Hosting/FatalErrorHandler.cs                        IProcessTerminator.Exit(1) path for SCM recovery
Hosting/SmokeTest.cs                                --smoke-test: temp data dir, loopback, /healthz, exit code
Hosting/HealthCommand.cs                            --health: GET /healthz on the configured URL, exit code 0/1
Hosting/StartupChecks.cs                            migrations, data dir ACL, bootstrap guard, cert, secrets, runtime version
Hosting/LoggingSetup.cs                             console + EventLog (source "AdMng Watchdog"), redacting provider wrapper
Hosting/RedactingLoggerProvider.cs                  drops exception objects/messages of framework categories (0.1)
Hosting/ApiExceptionHandler.cs                      IExceptionHandler → ProblemDetails, logs via SafeExceptionLog
Hosting/EventLogIds.cs                              stable Application-log event ids (19.8)
Auth/AuthenticationSetup.cs                         Negotiate | HttpSys | Development registration
Auth/KerberosOnlyEvents.cs                          NegotiateEvents: 401 on failure + SPN hint (Kestrel)
Auth/KerberosOnlyMiddleware.cs                      rejects every non-Kerberos identity (Kestrel and HTTP.sys)
Auth/RemoteAddressFilter.cs                         AllowedRemoteSubnets / MonitoringSubnets on Connection.RemoteIpAddress only
Auth/DevelopmentAuthenticationHandler.cs            loopback-only, Development environment only, never as a Windows service
Auth/SidRoleRequirement.cs                          requirement + handler (GroupSid/PrimaryGroupSid, DeniedSids first)
Auth/WatchdogPolicies.cs                            Viewer/Admin policies, fallback policy
Auth/ClientHeaderFilter.cs                          endpoint filter requiring X-AdMng-Client on state changes
Auth/CallerIdentity.cs                              HttpContext → PrincipalRef (for audit/ack)
Api/ApiSetup.cs                                     maps all endpoint groups, ProblemDetails, API version header
Api/StatusEndpoints.cs                              /me, /status, /catalog, /healthz
Api/EventEndpoints.cs                               /events, /events/{id}, /events/{id}/ack, /events/ack (bulk), long-poll
Api/EventQueryBinder.cs                             query string → EventQuery
Api/ConfigEndpoints.cs                              GET (role-dependent redaction)/PUT config, defaults, validate, audit
Api/ConfigSecretsApplier.cs                         SecretField.NewValue/Clear → SecretChange list (bindings); strip before save
Api/ConfigAuditSummarizer.cs                        human summary of a config change (no secret values)
Api/ConfigChangeNotifier.cs                         WatchdogConfigurationChanged evaluated with the previous configuration
Api/TestEndpoints.cs                                /test/email, /test/channels/{id}
Api/RuleEndpoints.cs                                /rules/preview
Api/DigestEndpoints.cs                              /digest/preview, /digest/send
Api/OutboxEndpoints.cs                              /outbox, /outbox/{id}/retry
Api/EventPageEndpoint.cs                            GET /events/{eventId} HTML (Viewer)
Storage/SqliteDatabase.cs                           connection factory, PRAGMAs, single-writer gate
Storage/MigrationRunner.cs                          user_version migrations, VACUUM INTO before upgrade
Storage/Migrations/0001_initial.sql                 embedded resource (section 16)
Storage/BlobCodec.cs                                deflate + JSON (WatchdogStorageJsonContext), case-insensitive dictionaries
Storage/WatchdogStorageJsonContext.cs               source-gen context for stored Core.Model payloads
Storage/SqliteDetectionStore.cs                     IDetectionStore
Storage/SqliteEventStore.cs                         IEventStore
Storage/SqliteNotificationQueueStore.cs             INotificationQueueStore
Storage/SqliteOutboxStore.cs                        IOutboxStore
Storage/SqliteSecurityEventStore.cs                 ISecurityEventStore
Storage/SqliteExpiryNoticeStore.cs                  IExpiryNoticeStore
Storage/SqliteDigestRunStore.cs                     IDigestRunStore
Storage/SqliteConfigStore.cs                        IConfigStore
Storage/SqliteSecretStore.cs                        ISecretStore (Data Protection)
Storage/SqliteStoreMaintenance.cs                   IStoreMaintenance (purge, backup, status)
Directory/ServiceDirectorySessionProvider.cs        IDirectorySessionProvider over LdapDirectoryConnector (PDC selection)
Directory/DirectoryConnectionOptionsFactory.cs      ServiceOptions → ConnectionOptions (+lab simple bind from env)
Attribution/WindowsSecurityEventSource.cs           [SupportedOSPlatform("windows")] EventLogSession/EventLogReader
Attribution/NullSecurityEventSource.cs              non-Windows / disabled
Notifications/SmtpEmailTransport.cs                 INotificationTransport (MailKit)
Notifications/MimeMessageFactory.cs                 RenderedEmail → MimeMessage (headers, Message-ID, In-Reply-To)
Notifications/SmtpErrorClassifier.cs                MailKit exceptions → DeliveryResult + hint codes
Notifications/SmtpOAuthTokenProvider.cs             MSAL confidential client (cert or secret), singleton cache
Notifications/SmtpCertificateValidation.cs          System / pinned SHA-256 / dev accept-any
Notifications/TeamsWorkflowTransport.cs             INotificationTransport (HttpClient)
Notifications/TeamsTokenProvider.cs                 MSAL for "Specific users in my tenant" triggers
Notifications/WebhookTransport.cs                   INotificationTransport (Standard Webhooks headers per attempt)
Notifications/HttpDeliveryClassifier.cs             HTTP status → DeliveryResult (Teams + webhook tables)
Notifications/ChannelRateLimiters.cs                TokenBucketRateLimiter per channel (manual replenishment on TimeProvider)
Notifications/DestinationGuard.cs                   secret binding + OAuth host allow-list check before any credential is used
Notifications/DeliveryTester.cs                     IDeliveryTester (stage-by-stage test sends)
Secrets/DataProtectionSetup.cs                      key ring in <data>\keys, DPAPI-NG on Windows
Deploy/Install-AdMngWatchdog.ps1                    install script (section 19.6), copied to publish output
Deploy/Uninstall-AdMngWatchdog.ps1
```

### 3.4 `src/AdMng.Watchdog.Client` (net10.0) – owner W-service

```
AdMng.Watchdog.Client.csproj                        [S]
IWatchdogClient.cs                                  [S]
WatchdogClientOptions.cs                            [S]
WatchdogClientException.cs                          [S] + WatchdogClientErrorKind
WatchdogHttpClient.cs                               [S*] implementation (SocketsHttpHandler, Negotiate, long-poll)
WatchdogHttpHandlerFactory.cs                       handler with credentials bound to the exact origin, cert validation, HTTP/1.1
```

### 3.5 `src/AdMng.Presentation` additions – owner W-gui

```
Shell/IShellDocument.cs                             common contract of document tabs (ObjectListViewModel implements it)
Shell/ShellViewModel*.cs                            (modified) Documents: ObservableCollection<IShellDocument>, tools.watchdog
Watchdog/WatchdogDocumentViewModel.cs               the tab: connection state, sub-tabs, role, commands
Watchdog/WatchdogConnectionService.cs               applies AppSettings.Watchdog to IWatchdogClient, maps errors
Watchdog/WatchdogPoller.cs                          long-poll loop while the tab is visible; /status every 60 s while hidden
Watchdog/WatchdogEventsViewModel.cs                 feed + filters + paging + bulk acknowledge
Watchdog/WatchdogEventRowViewModel.cs
Watchdog/WatchdogEventFilterViewModel.cs            incl. named presets (AppSettings, filters only)
Watchdog/WatchdogObjectHistoryLauncher.cs           "Historia zmian (Watchdog)" from objects in the main window
Watchdog/WatchdogEventDetailsViewModel.cs
Watchdog/WatchdogConfigSession.cs                   loaded ConfigEnvelope, dirty tracking, save/reload/conflict
Watchdog/WatchdogRulesViewModel.cs
Watchdog/WatchdogRuleEditorViewModel.cs
Watchdog/WatchdogExclusionViewModel.cs
Watchdog/WatchdogChannelsViewModel.cs
Watchdog/SmtpSettingsViewModel.cs
Watchdog/ChannelEditorViewModel.cs
Watchdog/RecipientGroupsViewModel.cs
Watchdog/DeliveryTestViewModel.cs
Watchdog/WatchdogDigestViewModel.cs
Watchdog/WatchdogStatusViewModel.cs
Watchdog/WatchdogDeliveryLogViewModel.cs
Watchdog/WatchdogDisplay.cs                         localized names of kinds, severities, reasons, health codes
Localization/L.Watchdog.cs                          key constants (prefix "Watchdog.")
Resources/Strings.resx, Strings.pl.resx             (modified) all Watchdog.* keys
Settings/AppSettings.cs                             (modified) WatchdogClientSettings { ServiceUrl, … }
ServiceCollectionExtensions.cs                      (modified) registrations
```

### 3.6 `src/AdMng.App` additions – owner W-gui

```
Views/Watchdog/WatchdogView.xaml(.cs)               document content: sub-tab strip + connection banner
Views/Watchdog/WatchdogEventsView.xaml(.cs)
Views/Watchdog/WatchdogRulesView.xaml(.cs)          list + inline editor pane (no new window)
Views/Watchdog/WatchdogChannelsView.xaml(.cs)
Views/Watchdog/WatchdogDigestView.xaml(.cs)
Views/Watchdog/WatchdogStatusView.xaml(.cs)
Views/MainWindow.xaml(.cs)                          (modified) tab header + content templates per document type, menu item
AppHost.cs                                          (modified) IWatchdogClient registration
SmokeTest.cs                                        (modified) opens the Watchdog tab (not configured + fake data)
```

### 3.7 Tests and tools

```
tests/AdMng.Watchdog.Core.Tests/…                   [S] csproj + 1 sanity test; then W-core (section 22.1)
tests/AdMng.Watchdog.Service.Tests/…                [S] csproj + 1 sanity test; then W-service (22.2)
tests/AdMng.Watchdog.IntegrationTests/…             [S] csproj + 1 skipped-without-lab test; then W-testenv (22.4)
tests/AdMng.Watchdog.Testing/…                      [S] csproj + compilable shells with final names/signatures:
                                                        Stores/WatchdogStoreContractTests.cs (abstract, 3–5 starter facts),
                                                        Stores/InMemoryWatchdogStore.cs, Http/HttpSink.cs (public API only);
                                                        then owned per 25.3 (W-core: store fakes, fixtures,
                                                        FakeSecurityEventSource; W-service: SMTP/webhook sinks, certificates)
tests/AdMng.Testing/FakeDirectorySession*.cs,       (modified, W-core) watchdog emulation (section 4.3), incl. edits of the
  FakeSchema.cs, FakeDirectoryBuilder.cs              main FakeDirectorySession.cs (metadata hook) and FakeSchema additions
tests/AdMng.Presentation.Tests/Watchdog/…           W-gui
tests/AdMng.App.UiTests/WatchdogViewTests.cs        W-gui (+ ScreenshotTests additions)
tools/testlab/watchdog/…                            W-testenv (section 23)
.github/workflows/ci.yml                            W-testenv (section 24)
```

---

## 4. Changes to existing code

### 4.1 `AdMng.Core` (scaffold, then frozen)

1. `Directory/Search.cs` – add to `DirectorySearch`:

```csharp
/// <summary>
/// Extended DN control (1.2.840.113556.1.4.529, string form): values of DN-syntax attributes are returned as
/// <c>&lt;GUID=…&gt;;&lt;SID=…&gt;;dn</c> (SID only for security principals). Entry DNs stay plain.
/// Parse values with <see cref="Ldap.ExtendedDistinguishedName.TryParse"/>.
/// </summary>
public bool ExtendedDn { get; init; }
```

2. New `Directory/AttributeReadOptions.cs`:

```csharp
namespace AdMng.Core.Directory;

/// <summary>Options of <see cref="IDirectorySession.GetAllValuesAsync(string,string,AttributeReadOptions,CancellationToken)"/>.</summary>
public sealed record AttributeReadOptions
{
    /// <summary>Return DN values in extended form (see <see cref="DirectorySearch.ExtendedDn"/>).</summary>
    public bool ExtendedDn { get; init; }

    /// <summary>Read a deleted object (Show Deleted control).</summary>
    public bool IncludeDeleted { get; init; }

    public static AttributeReadOptions Default { get; } = new();
}
```

3. `Directory/IDirectorySession.cs` – add a default interface method (existing implementations keep compiling):

```csharp
/// <summary>Ranged retrieval with options. The default implementation supports only <see cref="AttributeReadOptions.Default"/>.</summary>
IAsyncEnumerable<string> GetAllValuesAsync(string distinguishedName, string attribute, AttributeReadOptions options, CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(options);
    return options.ExtendedDn || options.IncludeDeleted
        ? throw new NotSupportedException("This session does not support extended DN / deleted-object value reads.")
        : GetAllValuesAsync(distinguishedName, attribute, cancellationToken);
}
```

4. New `Ldap/ExtendedDistinguishedName.cs` (pure, unit-tested by the scaffold):

```csharp
namespace AdMng.Core.Ldap;

/// <summary>A value returned with the Extended DN control: &lt;GUID=x&gt;;&lt;SID=y&gt;;dn (GUID/SID optional).</summary>
public readonly record struct ExtendedDistinguishedName(Guid? Guid, string? Sid, string Dn)
{
    /// <summary>Parses the string form (flag 1). A plain DN parses with Guid/Sid = null. Never throws.</summary>
    public static bool TryParse(string? value, out ExtendedDistinguishedName result);

    /// <summary>Formats the string form (used by FakeDirectorySession).</summary>
    public override string ToString();
}
```

Rules: components are `<GUID=…>` (GUID "D" format, also accept 32-hex) and `<SID=…>` (S-1-… text, also accept hex);
order GUID, SID; separator `;`; the remainder after the last `>;` is the DN (a `;` inside a DN value is RFC 4514-escaped).
For DN-Binary syntax values (`B:<len>:<hex>:<dn>`) the parser is not used (see 9.4).

5. `Schema/SecretAttributes.cs` – add to `WellKnownNames` the remaining MS-DRSR secret attributes whose schema entries
   are not marked confidential (lab: `searchFlags` 0): `currentValue`, `priorValue` (LSA secret objects incl. the
   DPAPI backup keys), `msDS-ExecuteScriptPassword`, plus `msSFU30Password` (SFU schemas). New test
   `SecretAttributesTests.CoversDrsrSecretSet` asserts every MS-DRSR secret attribute (`unicodePwd`, `dBCSPwd`,
   `ntPwdHistory`, `lmPwdHistory`, `supplementalCredentials`, `priorValue`, `currentValue`, `trustAuthOutgoing`,
   `trustAuthIncoming`, `initialAuthOutgoing`, `initialAuthIncoming`, `msDS-ExecuteScriptPassword`) is covered.

### 4.2 `AdMng.Directory` (W-service)

- `LdapDirectorySession.BuildSearchRequest`: when `search.ExtendedDn` add `new ExtendedDNControl(ExtendedDNFlag.StandardString)`.
  When AD returns the entry DN itself in extended form, strip it (`ExtendedDistinguishedName.TryParse(..).Dn`) before
  building `DirectoryObject`.
- Implement the new `GetAllValuesAsync(dn, attribute, AttributeReadOptions, ct)` overload (extended DN + Show Deleted on
  every ranged request).
- New integration test `tests/AdMng.Directory.IntegrationTests/Integration/ExtendedDnTests.cs` (throwaway OU in the
  existing `LabFixture`): group with a user member and a manager link, values parse to the objects' GUIDs/SIDs; ranged
  read with extended DN on a 1600-member group (create contacts in the test OU).
- No other change; `RootDSE` reads use the existing `GetAsync(string.Empty, attrs)` (verified by `WriteTests`).

### 4.3 `tests/AdMng.Testing` – FakeDirectorySession watchdog emulation (W-core)

Make `FakeDirectorySession` `partial` and add `FakeDirectorySession.Watchdog.cs` (+ tests in
`tests/AdMng.Core.Tests/Testing/FakeDirectoryWatchdogTests.cs`). Required behaviour (mirrors the Samba lab findings).
**Every watchdog behaviour is opt-in**: a fake built by existing tests (`FakeDirectoryBuilder.AddSampleCompany()`)
behaves exactly as today – no DC objects, no lockout expiry, no new constructed values – so `AdMng.Presentation.Tests`,
`AdMng.App.UiTests` and the screenshots are unaffected.

| Feature | Behaviour |
|---|---|
| RootDSE | `GetAsync("", attrs)` returns `highestCommittedUSN` (= current USN counter), `dsServiceName`, `serverName`, `dnsHostName` (= `Info.Server`), `isSynchronized` (`IsSynchronized` property, default TRUE), `currentTime` (time provider), `defaultNamingContext`, `configurationNamingContext`, `schemaNamingContext`, `rootDomainNamingContext`, `supportedControl`. |
| Naming-context boundaries | `Info.ConfigurationNamingContext` and `Info.SchemaNamingContext` are separate NCs although their DNs are lexically below the domain (as in AD): a subtree / one-level search whose base is outside such an NC never returns entries inside it (AD and Samba return only a referral, which the real session ignores). |
| DC objects | **Explicit only** – no seeding in the constructor (the domain root does not exist yet then). `SeedDomainController(string hostName, Guid invocationId, bool isPdc, bool isReadOnly = false, string? domainNc = null)` creates the configuration containers when missing, `CN=<HOST>,CN=Servers,CN=Default-First-Site-Name,CN=Sites,<config>` (objectClass server, dNSHostName) and child `CN=NTDS Settings` (objectClass nTDSDSA, objectCategory nTDSDSA or nTDSDSARO, invocationId binary, objectGUID, `msDS-HasDomainNCs` = `domainNc ?? defaultNC`); `isPdc` writes `fSMORoleOwner` on the domain head. `FakeDirectoryBuilder.AddWatchdogInfrastructure()` (called after the domain root exists) seeds `Info.Server` as PDC, `wellKnownObjects` on the domain head, the FSMO holder objects (`CN=RID Manager$,CN=System`, `CN=Infrastructure`, `CN=Partitions,<config>`, schema head) and `CN=Directory Service,CN=Windows NT,CN=Services,<config>`. |
| Restore / rollback | `SimulateRestore()` gives the current DC a new invocationId. `OverrideHighestCommittedUsn(long?)` returns a lower value (USN rollback). `SwitchServer(string host)` changes `Info.Server`/dsServiceName (failover / failback; each seeded DC keeps its own USN offset so failback to a known DC can be tested). |
| USN semantics | Every write `Touch`es only the written object; a `member` change bumps **only the group** (not the member, memberOf is computed); deleting a member removes it from groups **without** bumping the groups; a Recycle Bin restore (`RestoreDeletedAsync` with `RecycleBinEnabled`) re-activates the object's former link values **without** bumping the groups; ModifyDN bumps only the moved object (descendants get new DNs, same USNs); tombstoning bumps the deleted object. |
| Noise | `SimulateSuccessfulLogon(dn)` writes `lastLogonTimestamp` and bumps USN, and – when a lockout has expired (`LockoutDuration` set and `now ≥ lockoutTime + LockoutDuration`) – also writes `lockoutTime = 0` (DC behaviour, Samba `authsam_logon_success_accounting`); `SimulateBadPassword(dn)` changes `badPwdCount`/`badPasswordTime` without bumping USN; `SimulateLockout(dn)` writes `lockoutTime` (bumps USN). `LockoutDuration` property: **default null = today's semantics** (lockoutTime > 0 means locked, never expires); when set, `msDS-User-Account-Control-Computed` returns 0x10 only while `now < lockoutTime + LockoutDuration` – after that `lockoutTime` stays but the computed bit clears. |
| Constructed | Only when `EmulateConstructedPasswordExpiry = true`: `msDS-UserPasswordExpiryTimeComputed` = pwdLastSet + `MaxPasswordAge` (property, default 42 days); 0 when pwdLastSet = 0; 0x7FFFFFFFFFFFFFFF for DONT_EXPIRE_PASSWORD and computers. `msDS-ResultantPSO` returns the PSO DN set with `SetResultantPso(dn, psoDn)`. |
| Metadata | Per object and attribute keep `(version, originatingTime, invocationId, usnOriginating, usnLocal)`; updated on every write of that attribute (also `unicodePwd`, `pwdLastSet` on reset). Implemented through a new hook in the main file: `Touch(Entry entry, IEnumerable<string> changedAttributes)` – every write path (ApplyModify, Add, Move, Tombstone, Restore, ApplyPassword, SetValues) passes the attributes it changed (these are edits of `FakeDirectorySession.cs`, owned by W-core). Returned as Windows-style `msDS-ReplAttributeMetaData` XML values (format in 10.10) when requested. `member` (linked) has no entry (LVR semantics). `replPropertyMetaData` is not emulated (binary decoder is tested with lab fixtures). |
| Extended DN | `DirectorySearch.ExtendedDn` and `GetAllValuesAsync(…, new AttributeReadOptions { ExtendedDn = true })` return DN-syntax values as `<GUID=…>;<SID=…>;dn`. |
| Read restrictions | `HideAttributes(string dn, params string[] attributes)`: those attributes are neither returned nor matched by filters for that object (simulates a hardened DACL – e.g. hiding `uSNChanged` makes the object vanish from `(uSNChanged>=x)`). `DenyDeletedObjects = true`: Show Deleted searches under CN=Deleted Objects return nothing (read-only account without LCRP). |
| Schema (`FakeSchema.cs`) | Add the attributes the watchdog diffs, with correct syntax (the fake rejects unknown attributes with LDAP error 17 and only knows DN values from the schema): `msDS-AllowedToDelegateTo` (Unicode, multi), `msDS-AllowedToActOnBehalfOfOtherIdentity` (SecurityDescriptor), `msDS-KeyCredentialLink` (DN-Binary, multi), `userCertificate` (Octet, multi), `altSecurityIdentities` (Unicode, multi), `logonHours` (Octet), `employeeType`, `mS-DS-CreatorSID` (SID), `gPOptions`, `lockoutDuration`, `lockOutObservationWindow`, `maxPwdAge`, `minPwdAge`, `minPwdLength`, `pwdHistoryLength`, `pwdProperties`, `lockoutThreshold`, `ms-DS-MachineAccountQuota`, `msDS-Behavior-Version`, `versionNumber`, `flags`, `gPCFileSysPath`, `trustDirection`, `trustType`, `trustAttributes`, `flatName`, `trustPartner`, the `msDS-PSO*`/`msDS-Password*`/`msDS-Lockout*`/`msDS-MinimumPassword*`/`msDS-MaximumPasswordAge` attributes, `msDS-PSOAppliesTo` (DN), `msDS-ResultantPSO` (DN, constructed), `invocationId` (Octet), `fSMORoleOwner` (DN), `msDS-HasDomainNCs` (DN), `wellKnownObjects` (DN-Binary), `msDS-UserPasswordExpiryTimeComputed` (constructed), `msDS-GroupMSAMembership` (SecurityDescriptor), `msDS-ManagedAccountPrecededByLink` (DN), `msDS-DelegatedMSAState`, `msDS-SupersededManagedAccountLink` (DN), `msDS-SupersededServiceAccountState`, `msDS-EnabledFeature` (DN), `dSHeuristics`, `tombstoneLifetime`, `msPKI-Certificate-Name-Flag`, `msPKI-Enrollment-Flag`, `pKIExtendedKeyUsage`, `msPKI-Certificate-Application-Policy`, `msPKI-RA-Signature`, `certificateTemplates`, `cACertificate`; classes `server`, `nTDSDSA`, `attributeSchema`, `classSchema`, `groupPolicyContainer`, `trustedDomain`, `msDS-PasswordSettings`, `msDS-ManagedServiceAccount`, `msDS-GroupManagedServiceAccount`, `msDS-DelegatedManagedServiceAccount`, `rIDManager`, `infrastructureUpdate`, `crossRefContainer`, `nTDSService`, `pKICertificateTemplate`, `pKIEnrollmentService`, `certificationAuthority`, `msFVE-RecoveryInformation`, `dnsNode` (the last two for the deletion-noise tests). |

### 4.4 `CLAUDE.md` amendment (scaffold)

Replace rule 1 with:

```
1. **No local directory data in the app.** AD_MNG (App, Presentation, Core, Directory) never writes directory content
   (objects, attribute values, search results, schema dumps, snapshots) to disk, registry, temp files or logs. Everything
   is read live from the DC and kept in memory only while displayed. Allowed on disk: settings (UI layout, language,
   connection profiles without passwords, saved views = names + DNs + LDAP filters, the Watchdog service URL),
   diagnostic logs WITHOUT directory values, and files the operator explicitly exports.
   **Only exception:** the AdMng Watchdog service host (`src/AdMng.Watchdog.Service` and the store implementations it
   hosts) stores snapshots of watched attributes, events, normalized DC log records and its outbox in its own data
   folder, under the conditions of ADR-0008 (never secrets, retention, enforced ACL, encrypted channel secrets,
   diagnostic logs still value-free). `AdMng.Watchdog.Client`, `.Contracts`, `.Core` (outside the service host),
   Presentation and App stay under the strict rule – the WPF process never persists watchdog data.
```

Replace rule 2 with:

```
2. **Never persist or log passwords.** `PlannedChange.Password`, `ResetPasswordOperation.FixedPassword` and
   credentials stay in memory; override `ToString()` on anything that carries them. **Only exception:** AdMng Watchdog
   channel credentials (SMTP password, OAuth client secrets, Teams/webhook URLs, webhook signing keys) are persisted
   solely through `ISecretStore` (ASP.NET Core Data Protection, keys protected with DPAPI-NG to the service gMSA),
   bound to their destination host; they are write-only through the API and never appear in the config JSON, logs,
   exceptions or `ToString()`.
```

Add to `docs/adr/0003-brak-lokalnych-danych.md` the status line "przyjęta; częściowo zmieniona przez ADR-0008 (tylko
usługa AdMng Watchdog)" (done together with this spec revision).

Add the new projects to the layout table and these commands:

```bash
dotnet test tests/AdMng.Watchdog.Core.Tests
dotnet test tests/AdMng.Watchdog.Service.Tests
# lab: Samba + Mailpit (tools/testlab/watchdog/README.md)
dotnet test tests/AdMng.Watchdog.IntegrationTests
dotnet run --project src/AdMng.Watchdog.Service --launch-profile Lab
```

---

## 5. Contracts (`AdMng.Watchdog.Contracts`) – exact code

Namespace `AdMng.Watchdog.Contracts` (sub-namespaces `.Events`, `.Api`, `.Configuration`). All records are `sealed`,
immutable (`init`), JSON via `WatchdogJson`. Collections default to empty (`[]`), never null. XML docs on members
whose meaning is not obvious. Anything that may carry a secret overrides `ToString()`.

### 5.1 Routes and JSON

```csharp
namespace AdMng.Watchdog.Contracts;

public static class WatchdogRoutes
{
    public const int ApiVersion = 1;
    public const string ApiVersionHeader = "X-AdMng-Watchdog-Api";   // sent by server on every response, by client on every request
    public const string ClientHeader = "X-AdMng-Client";             // CSRF guard, required on POST/PUT/DELETE, value "1"
    public const string Prefix = "/api/v1";
    public const string Me = Prefix + "/me";                         // GET  Viewer
    public const string Status = Prefix + "/status";                 // GET  Viewer
    public const string Catalog = Prefix + "/catalog";               // GET  Viewer  -> EventKindInfo[]
    public const string Events = Prefix + "/events";                 // GET  Viewer  (EventQuery) -> EventPage
    public const string EventById = Prefix + "/events/{id:long}";    // GET  Viewer  -> EventDetails
    public const string EventAcknowledge = Prefix + "/events/{id:long}/ack"; // POST Viewer -> 204
    public const string EventsAcknowledge = Prefix + "/events/ack";  // POST Viewer (BulkAcknowledgeRequest, ≤ 500 ids) -> 204
    public const string Config = Prefix + "/config";                 // GET Viewer (redacted unless Admin) -> ConfigEnvelope; PUT Admin (If-Match: "version")
    public const string ConfigDefaults = Prefix + "/config/defaults";// GET  Viewer  -> WatchdogConfiguration
    public const string ConfigValidate = Prefix + "/config/validate";// POST Admin   -> ConfigValidationResult
    public const string ConfigAudit = Prefix + "/config/audit";      // GET  Admin   -> ConfigAuditPage
    public const string TestEmail = Prefix + "/test/email";          // POST Admin   -> DeliveryTestResult
    public const string TestChannel = Prefix + "/test/channels/{channelId}"; // POST Admin -> DeliveryTestResult
    public const string RulePreview = Prefix + "/rules/preview";     // POST Admin   -> RulePreviewResult
    public const string DigestPreview = Prefix + "/digest/preview";  // GET  Viewer  -> DigestPreviewDto
    public const string DigestSend = Prefix + "/digest/send";        // POST Admin   -> 202
    public const string Outbox = Prefix + "/outbox";                 // GET  Admin   (OutboxQuery) -> OutboxPage
    public const string OutboxRetry = Prefix + "/outbox/{id:long}/retry"; // POST Admin -> 204
    public const string EventPage = "/events/{eventId:guid}";        // GET  Viewer  -> text/html (links in notifications)
    public const string Health = "/healthz";                         // GET  anonymous: loopback, own addresses, MonitoringSubnets -> "ok"/503

    public static string EventByIdPath(long id) => $"{Prefix}/events/{id}";
    public static string EventAcknowledgePath(long id) => $"{Prefix}/events/{id}/ack";
    public static string TestChannelPath(string channelId) => $"{Prefix}/test/channels/{Uri.EscapeDataString(channelId)}";
    public static string OutboxRetryPath(long id) => $"{Prefix}/outbox/{id}/retry";
    public static string EventPagePath(Guid eventId) => $"/events/{eventId:D}";
}
```

```csharp
namespace AdMng.Watchdog.Contracts;

public static class WatchdogJson
{
    /// <summary>Web defaults (camelCase), enums as strings, TimeSpan "c", TimeOnly "HH:mm[:ss]", DateOnly "yyyy-MM-dd".</summary>
    public static JsonSerializerOptions Options { get; }   // = new(WatchdogJsonContext.Default.Options)
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, WriteIndented = false)]
[JsonSerializable(typeof(WatchEvent))]  [JsonSerializable(typeof(EventPage))]  [JsonSerializable(typeof(EventDetails))]
[JsonSerializable(typeof(StatusDto))]   [JsonSerializable(typeof(MeDto))]      [JsonSerializable(typeof(ConfigEnvelope))]
[JsonSerializable(typeof(WatchdogConfiguration))] [JsonSerializable(typeof(ConfigValidationResult))]
[JsonSerializable(typeof(TestEmailRequest))] [JsonSerializable(typeof(TestChannelRequest))] [JsonSerializable(typeof(DeliveryTestResult))]
[JsonSerializable(typeof(RulePreviewRequest))] [JsonSerializable(typeof(RulePreviewResult))] [JsonSerializable(typeof(DigestPreviewDto))]
[JsonSerializable(typeof(OutboxPage))] [JsonSerializable(typeof(ConfigAuditPage))] [JsonSerializable(typeof(AcknowledgeRequest))]
[JsonSerializable(typeof(BulkAcknowledgeRequest))]
[JsonSerializable(typeof(IReadOnlyList<EventKindInfo>))]
public sealed partial class WatchdogJsonContext : JsonSerializerContext;
```

### 5.2 Event model

```csharp
namespace AdMng.Watchdog.Contracts.Events;

public enum Severity { Info = 0, Low = 1, Medium = 2, High = 3, Critical = 4 }

public enum WatchObjectKind
{
    Unknown = 0, User = 1, Computer = 2, Group = 3, OrganizationalUnit = 4, Container = 5, Contact = 6,
    ServiceAccount = 7,      // msDS-ManagedServiceAccount, msDS-GroupManagedServiceAccount, msDS-DelegatedManagedServiceAccount
    Domain = 8, GroupPolicy = 9, Trust = 10, DomainController = 11, PasswordSettings = 12, Schema = 13, Watchdog = 14,
    PublicKeyInfrastructure = 15,   // certificate templates, enrollment services, CAs, NTAuth (Configuration NC)
    Configuration = 16,             // other forest singletons: Partitions, Directory Service, FSMO holder objects
}

public enum EventCategory { Lifecycle, Account, Membership, Security, Domain, Expiry, AuditLog, Health }

public enum DetectionSource
{
    IncrementalPoll = 1, Reconciliation = 2, DeletedObjects = 3, LockoutMonitor = 4, ExpiryScan = 5,
    BurstDetector = 6, SecurityLog = 7, Health = 8, Test = 9,
}

/// <summary>Stored as int – values are stable, never renumber. Defaults: section 7 / EventKindCatalog.</summary>
public enum WatchEventKind
{
    UserCreated = 100, ComputerCreated = 101, GroupCreated = 102, OrganizationalUnitCreated = 103, ContactCreated = 104,
    ServiceAccountCreated = 105, ObjectDeleted = 110, ObjectRestored = 111, ObjectMoved = 112, ObjectRenamed = 113,
    ObjectAppeared = 114, ObjectDisappeared = 115,
    AccountDisabled = 200, AccountEnabled = 201, AccountLockedOut = 202, AccountUnlocked = 203, LockoutExpired = 204,
    PasswordChanged = 205, PasswordMustChangeSet = 206, AccountFlagsChanged = 207, AccountExpirationChanged = 208,
    AccountNameChanged = 209, AttributesChanged = 210,
    PasswordAgeReset = 211,          // pwdLastSet moved forward without a unicodePwd write (0 → -1 trick)
    GroupMemberAdded = 300, GroupMemberRemoved = 301, GroupTypeChanged = 302, PrivilegeGranted = 303, PrivilegeRevoked = 304,
    AdminCountSet = 400, SidHistoryChanged = 401, DelegationChanged = 402, ServicePrincipalNameAdded = 403,
    KeyCredentialChanged = 404, PrimaryGroupChanged = 405, SecurityDescriptorChanged = 406, ComputerNameSpoofing = 407,
    ManagedAccountLinkChanged = 408,      // dMSA migration links on either side (BadSuccessor)
    CertificateMappingChanged = 409,      // altSecurityIdentities (explicit certificate mapping, ESC14)
    ManagedPasswordReadersChanged = 410,  // msDS-GroupMSAMembership (who may read a gMSA/dMSA password)
    DomainPolicyChanged = 500, PasswordSettingsChanged = 501, GroupPolicyLinkChanged = 502, GroupPolicyChanged = 503,
    TrustChanged = 504, DomainControllerAdded = 505, SchemaChanged = 506,
    DomainControllerRemoved = 507, FsmoRoleChanged = 508, CertificateTemplateChanged = 509,
    AccountExpiring = 600, AccountExpired = 601, PasswordExpiring = 602, MassLockout = 603, MassDeletion = 604,
    PasswordSprayingSuspected = 605,      // optional detector on failed-logon events (10.15), not recorded by default
    SecurityLogCleared = 700, AuditPolicyChanged = 701, DsrmPasswordChanged = 702,
    HealthDirectoryUnavailable = 900, HealthResync = 901, HealthPermissionGap = 902, HealthAttributionDegraded = 903,
    HealthEventLogGap = 904, HealthDeliveryFailing = 905, HealthStorage = 906, HealthBaselineCompleted = 907,
    WatchdogConfigurationChanged = 908,   // runtime configuration saved through the API (17.2)
    Test = 999,
}

public static class EventTags
{
    public const string Privileged = "privileged";             // principal (or a member) in the current PrivilegeState only
    public const string AdminCountOrphan = "adminCountOrphan"; // adminCount=1 but not in PrivilegeState (digest hygiene, no bump)
    public const string ServiceAccount = "serviceAccount";     // MSA/gMSA/dMSA, user with SPN, or under ServiceAccountSubtrees
    public const string DefaultContainer = "defaultContainer"; // created/moved into the (possibly redirected) default users/computers container
    public const string UnexpectedContainer = "unexpectedContainer"; // default container is redirected, yet created in literal CN=Users / CN=Computers
    public const string CreatedByNonAdmin = "createdByNonAdmin"; // computer with mS-DS-CreatorSID (MachineAccountQuota)
    public const string Server = "server";                     // computer whose operatingSystem contains "Server"
    public const string DomainController = "domainController";
    public const string OtherDomain = "otherDomain";           // DC of another domain of the forest
    public const string Reconciliation = "reconciliation";     // detected by a full resync / reconciliation
    public const string Restored = "restored";                 // privileges/memberships that came back with a restored object
    public const string LongDisabled = "longDisabled";         // enabled after being disabled (or unused) ≥ 30 days
    public const string DisabledOu = "disabledOu";             // object is/was under Directory.DisabledAccountsSubtrees
    public const string Krbtgt = "krbtgt";
    public const string BuiltInAdministrator = "builtinAdministrator"; // RID 500
    public const string SelfChange = "selfChange";             // actor == target (computer self-updates, own password change)
    public const string PasswordReset = "passwordReset";       // password set by another account (4724) – added by attribution
    public const string SyncAccount = "syncAccount";           // actor is a detected Entra Connect / sync account
    public const string OutOfScope = "outOfScope";             // moved into/out of Directory.ExcludedSubtrees
}

public sealed record ObjectRef
{
    public Guid? ObjectGuid { get; init; }
    public required string Dn { get; init; }
    public string? Name { get; init; }               // RDN value
    public string? DisplayName { get; init; }
    public string? SamAccountName { get; init; }
    public string? Sid { get; init; }
    public WatchObjectKind Kind { get; init; }
}

public sealed record PrincipalRef
{
    public string? Sid { get; init; }
    public string? Account { get; init; }            // DOMAIN\name
    public string? DisplayName { get; init; }
    public string? Dn { get; init; }
}

public enum AttributeValueKind { Text, Number, Boolean, FileTime, GeneralizedTime, Dn, Sid, Guid, Flags, Hash, Binary }

public sealed record AttributeChange
{
    public required string Attribute { get; init; }
    public AttributeValueKind ValueKind { get; init; }
    /// <summary>Canonical values (section 9.4); DN values are display DNs. Empty when Redacted.</summary>
    public IReadOnlyList<string> OldValues { get; init; } = [];
    public IReadOnlyList<string> NewValues { get; init; } = [];
    /// <summary>Multi-valued attributes: values added/removed (computed by the differ).</summary>
    public IReadOnlyList<string> Added { get; init; } = [];
    public IReadOnlyList<string> Removed { get; init; } = [];
    /// <summary>userAccountControl / groupType / gPOptions flag names set and cleared.</summary>
    public IReadOnlyList<string> FlagsAdded { get; init; } = [];
    public IReadOnlyList<string> FlagsRemoved { get; init; } = [];
    /// <summary>Secret or confidential attribute: values are never stored or sent.</summary>
    public bool Redacted { get; init; }
    /// <summary>Values were capped (Directory.MaxValuesPerAttribute / 200-character cap).</summary>
    public bool Truncated { get; init; }
    public int OldCount { get; init; }
    public int NewCount { get; init; }
}

public enum AttributionStatus { NotApplicable = 0, Pending = 1, Resolved = 2, Ambiguous = 3, Unknown = 4 }
public enum AttributionConfidence { None = 0, Low = 1, Medium = 2, High = 3 }

public sealed record EvidenceRef
{
    public required string Dc { get; init; }
    public string Log { get; init; } = "Security";
    public required int EventCode { get; init; }
    public required long RecordId { get; init; }
    public required DateTimeOffset TimeCreated { get; init; }
}

public sealed record Attribution
{
    public AttributionStatus Status { get; init; } = AttributionStatus.Pending;
    public AttributionConfidence Confidence { get; init; }
    /// <summary>One of <see cref="AttributionReasons"/> when not Resolved (or Resolved with caveats).</summary>
    public string? Reason { get; init; }
    public PrincipalRef? Actor { get; init; }
    /// <summary>Client-supplied or DNS-derived name – never authoritative (shown with its origin, never used in rule
    /// matching or exclusions).</summary>
    public string? Workstation { get; init; }
    /// <summary>Primary source when known (from 4624/4771/4776).</summary>
    public string? IpAddress { get; init; }
    /// <summary>"4624", "4740", "4771", "4776", "dns", "local" – how Workstation/IpAddress were obtained.</summary>
    public string? SourceMethod { get; init; }
    /// <summary>Confidence of the source: capped at Low for SourceMethod dns / 4740 / NTLM WorkstationName; never
    /// higher than <see cref="Confidence"/>.</summary>
    public AttributionConfidence SourceConfidence { get; init; }
    /// <summary>Why the source is missing: SOURCE_LOGON_NOT_FOUND, SOURCE_NOT_COLLECTED, DC_UNREACHABLE …</summary>
    public string? SourceReason { get; init; }
    public int? LogonType { get; init; }
    public IReadOnlyList<EvidenceRef> Evidence { get; init; } = [];
    public IReadOnlyList<PrincipalRef> Candidates { get; init; } = [];   // Ambiguous
    public DateTimeOffset? ResolvedAt { get; init; }

    public static Attribution Pending { get; } = new();
    public static Attribution NotApplicable { get; } = new() { Status = AttributionStatus.NotApplicable };
}

public static class AttributionReasons
{
    public const string Pending = "PENDING";
    public const string SourceDisabled = "SOURCE_DISABLED";          // attribution off / non-Windows host
    public const string AuditPolicyMissing = "AUDIT_POLICY_MISSING"; // no events of that family from that DC in 7 days
    public const string SaclMissing = "SACL_MISSING";                // non-SAM attribute, never saw a 5136
    public const string DcUnreachable = "DC_UNREACHABLE";            // RPC / access denied
    public const string LogOverwritten = "LOG_OVERWRITTEN";          // oldest record newer than the change
    public const string Ambiguous = "AMBIGUOUS";
    public const string NoMatch = "NO_MATCH";                        // window searched, nothing matched
    public const string System = "SYSTEM";                           // actor is the DC itself (S-1-5-18 / DC$)
    public const string SelfChange = "SELF_CHANGE";                  // computer changed its own object
    // source-level reasons (Attribution.SourceReason)
    public const string SourceLogonNotFound = "SOURCE_LOGON_NOT_FOUND"; // 4624 not found within LogonLookback / overwritten
    public const string SourceNotCollected = "SOURCE_NOT_COLLECTED";   // ForwardedEvents mode without 4624/4771/4776 in the subscription
}

public enum ExpiryKind { Account, Password }
/// <summary>DaysLeft = calendar days (configured time zone) from today to the displayed last valid day
/// (<c>(local(expires) − 1 tick).Date</c>).</summary>
public sealed record ExpiryInfo(ExpiryKind Kind, DateTimeOffset ExpiresAt, DateOnly LastValidDay, int DaysLeft, int ThresholdDays);

/// <summary>Where the bad passwords came from (4740 caller, 4771 client address, 4776 workstation, 4625).</summary>
public sealed record LockoutSource(string? IpAddress, string? Workstation, int Count, int EventCode);

public sealed record LockoutInfo
{
    public required DateTimeOffset LockedAt { get; init; }
    public DateTimeOffset? LockedUntil { get; init; }   // null = until an admin unlocks; effective duration incl. PSO
    public string? CallerComputer { get; init; }        // from 4740 (client-supplied; may be empty or a front-end server)
    /// <summary>Aggregated failed-logon sources in [LockedAt − lockOutObservationWindow, LockedAt] (≤ 10, by count).</summary>
    public IReadOnlyList<LockoutSource> Sources { get; init; } = [];
    /// <summary>Lockouts of the same account since local midnight, including this one (digest roll-up, repeat suppression).</summary>
    public int LockoutsToday { get; init; } = 1;
}

public sealed record BurstInfo
{
    public required int Count { get; init; }
    public required TimeSpan Window { get; init; }
    public IReadOnlyList<ObjectRef> Objects { get; init; } = [];  // capped at 50
}

public sealed record WatchEvent
{
    /// <summary>Store cursor (autoincrement); 0 until stored.</summary>
    public long Id { get; init; }
    /// <summary>Stable id (Guid v7): webhook-id, e-mail Message-ID, HTML link.</summary>
    public required Guid EventId { get; init; }
    /// <summary>Events produced from one object diff (create + enable + password set) share this id.</summary>
    public Guid ChangeSetId { get; init; }
    public required WatchEventKind Kind { get; init; }
    public Severity Severity { get; init; }
    public required ObjectRef Object { get; init; }
    public string? PreviousDn { get; init; }                 // moves/renames, deletions (last live DN)
    public IReadOnlyList<AttributeChange> Changes { get; init; } = [];
    public ObjectRef? Member { get; init; }                  // GroupMember*: the member; Privilege*: the privileged group
    public IReadOnlyList<ObjectRef> Via { get; init; } = []; // Privilege*: nesting path principal → … → privileged group
    public ExpiryInfo? Expiry { get; init; }
    public LockoutInfo? Lockout { get; init; }
    public BurstInfo? Burst { get; init; }
    public string? Domain { get; init; }                     // DNS name
    public required DateTimeOffset DetectedAt { get; init; }
    public DateTimeOffset? OriginatingTime { get; init; }    // replication metadata (or Security event time)
    public string? OriginatingDc { get; init; }              // DNS host name
    /// <summary>whenChanged on the watched DC (arrival time there) – correlation fallback when metadata is missing (LVR member changes).</summary>
    public DateTimeOffset? LocalChangeTime { get; init; }
    public required DetectionSource Source { get; init; }
    public Attribution Attribution { get; init; } = Attribution.Pending;
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> MatchedRules { get; init; } = [];
    public DateTimeOffset? AcknowledgedAt { get; init; }
    public string? AcknowledgedBy { get; init; }
    public string? AcknowledgeComment { get; init; }
}
```

```csharp
public sealed record EventKindInfo
{
    public required WatchEventKind Kind { get; init; }
    public required EventCategory Category { get; init; }
    public required Severity DefaultSeverity { get; init; }
    /// <summary>False: the kind is not recorded unless enabled in Directory.EnabledOptionalKinds.</summary>
    public required bool RecordedByDefault { get; init; }
    public IReadOnlyList<WatchObjectKind> ObjectKinds { get; init; } = [];
    public bool NeedsSecurityLog { get; init; }       // event-log-only kinds
    public bool IsHealth { get; init; }
}

public static class EventKindCatalog
{
    public static IReadOnlyList<EventKindInfo> All { get; }
    public static EventKindInfo Get(WatchEventKind kind);
    /// <summary>Default severity of ObjectDeleted etc. depends on the object kind (section 7).</summary>
    public static Severity DefaultSeverity(WatchEventKind kind, WatchObjectKind objectKind);
}
```

### 5.3 API DTOs

```csharp
namespace AdMng.Watchdog.Contracts.Api;

public sealed record EventQuery
{
    public long? After { get; init; }          // ascending ids > After (live polling)
    public long? Before { get; init; }         // descending ids < Before (paging back); ignored when After is set
    public int Limit { get; init; } = 100;     // 1..500
    public IReadOnlyList<WatchEventKind> Kinds { get; init; } = [];
    public IReadOnlyList<EventCategory> Categories { get; init; } = [];
    public IReadOnlyList<WatchObjectKind> ObjectKinds { get; init; } = [];
    public Severity? MinSeverity { get; init; }
    /// <summary>Case-insensitive contains on name, sAMAccountName, DN, actor, workstation and IP address.</summary>
    public string? Text { get; init; }
    /// <summary>Object DN (or PreviousDn) equal to or below this DN (helpdesk per branch).</summary>
    public string? Subtree { get; init; }
    /// <summary>Event must carry ALL of these tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<AttributionStatus> AttributionStatuses { get; init; } = [];
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? RuleId { get; init; }
    public Guid? ObjectGuid { get; init; }
    public string? ActorSid { get; init; }
    public bool UnacknowledgedOnly { get; init; }
    public int WaitSeconds { get; init; }      // 0..30, only with After: long-poll until a newer event exists

    /// <summary>"after=1&amp;kind=UserCreated&amp;kind=…" – repeated keys for lists, ISO 8601 for times.</summary>
    public string ToQueryString();
}

public sealed record EventPage
{
    public IReadOnlyList<WatchEvent> Items { get; init; } = [];
    public bool HasMore { get; init; }
    public long LatestId { get; init; }        // highest id in the store (polling cursor even when filtered)
}

public enum DeliveryChannelType { Email, Teams, Webhook }
public enum DeliveryKind { Alert, Digest, FollowUp, Test, Health }
public enum OutboxStatus { Pending, Sending, Sent, Retrying, Dead, Cancelled }

public sealed record EventDeliveryDto
{
    public required long OutboxId { get; init; }
    public required DeliveryChannelType ChannelType { get; init; }
    public required string ChannelId { get; init; }
    public string? ChannelName { get; init; }
    public required DeliveryKind Kind { get; init; }
    public required OutboxStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public string? LastErrorCode { get; init; }
}

public sealed record EventDetails
{
    public required WatchEvent Event { get; init; }
    public IReadOnlyList<WatchEvent> RelatedEvents { get; init; } = [];   // same ChangeSetId
    public IReadOnlyList<EventDeliveryDto> Deliveries { get; init; } = [];
}

public sealed record AcknowledgeRequest
{
    public string? Comment { get; init; }      // ≤ 500 chars
}

public sealed record BulkAcknowledgeRequest
{
    public IReadOnlyList<long> Ids { get; init; } = [];   // 1..500
    public string? Comment { get; init; }                  // ≤ 500 chars
}

public enum WatchdogRole { Viewer, Admin }

public sealed record MeDto
{
    public required string Account { get; init; }
    public string? Sid { get; init; }
    public required string AuthenticationType { get; init; }   // always "Kerberos" (other identities are rejected)
    public IReadOnlyList<WatchdogRole> Roles { get; init; } = [];
    /// <summary>Detail level applied to API responses for this caller (Admin: Full; Viewer: Authorization.ViewerDetailLevel).</summary>
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Full;
}

public enum HealthState { Ok, Warning, Error, Unknown, Disabled }

public sealed record HealthCheckDto
{
    /// <summary>Stable code, e.g. "directory.connection", "permission.uSNChanged", "permission.deletedObjects",
    /// "eventlog.dc01.contoso.com", "smtp", "storage.free" – the GUI localizes "Watchdog.Health.{Code-prefix}".</summary>
    public required string Code { get; init; }
    public required HealthState State { get; init; }
    public string? Detail { get; init; }       // English diagnostic text without directory values
    public IReadOnlyList<string> Args { get; init; } = [];   // numbers/host names for localized messages
    public DateTimeOffset CheckedAt { get; init; }
}

public sealed record ChannelHealthDto
{
    public required string ChannelId { get; init; }          // "email" for SMTP
    public required DeliveryChannelType ChannelType { get; init; }
    public required HealthState State { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public string? LastErrorCode { get; init; }
    public int ConsecutiveFailures { get; init; }
    public int Pending { get; init; }
}

public sealed record DirectoryStatusDto
{
    public required HealthState State { get; init; }
    public string? DcHostName { get; init; }
    public bool IsPdc { get; init; }
    public Guid? InvocationId { get; init; }
    public long HighestUsn { get; init; }
    public DateTimeOffset? LastPollAt { get; init; }
    public DateTimeOffset? LastChangeAt { get; init; }
    public DateTimeOffset? LastFullSyncAt { get; init; }
    public DateTimeOffset? BaselineCompletedAt { get; init; }
    public long SnapshotObjects { get; init; }
}

public sealed record DcLogStatusDto
{
    public required string Host { get; init; }
    public required HealthState State { get; init; }
    public DateTimeOffset? LastReadAt { get; init; }
    public long? LastRecordId { get; init; }
    public double? Coverage24h { get; init; }   // share of attributable LDAP-detected changes on this DC attributed in the last 24 h
    public string? ErrorCode { get; init; }
    public int SourceLookups24h { get; init; }  // on-demand 4624/4771/4776 queries against this DC
    public TimeSpan? SourceLookupAverage { get; init; }
    public int SourceLookupTimeouts24h { get; init; }
}

public sealed record AttributionStatusDto
{
    public bool Enabled { get; init; }
    public string Source { get; init; } = "DomainControllers";
    public IReadOnlyList<DcLogStatusDto> DomainControllers { get; init; } = [];
}

public sealed record StorageStatusDto
{
    public required string DataDirectory { get; init; }
    public long DatabaseBytes { get; init; }
    public long FreeBytes { get; init; }
    public long EventCount { get; init; }
    public DateTimeOffset? OldestEventAt { get; init; }
    public DateTimeOffset? LastBackupAt { get; init; }
}

public sealed record EventCountsDto
{
    public int Total { get; init; }
    public IReadOnlyDictionary<Severity, int> BySeverity { get; init; } = new Dictionary<Severity, int>();
    public int Unacknowledged { get; init; }
    public int UnacknowledgedHighOrAbove { get; init; }   // tab-header badge
}

/// <summary>A suggested (not applied) rule exclusion, e.g. a detected Entra Connect sync account for KeyCredentialChanged.
/// The GUI offers "Dodaj wyjątek", which goes through the normal PUT /config.</summary>
public sealed record ExclusionSuggestionDto
{
    public required string RuleId { get; init; }
    public required string ActorSid { get; init; }
    public string? ActorAccount { get; init; }
    public required string ReasonCode { get; init; }   // "entraConnectSync", "computerCleanupAccount" …
}

public sealed record StatusDto
{
    public required string ServiceVersion { get; init; }
    public int ApiVersion { get; init; } = WatchdogRoutes.ApiVersion;
    public required string InstanceName { get; init; }
    public string? Domain { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset Now { get; init; }
    public required DirectoryStatusDto Directory { get; init; }
    public required AttributionStatusDto Attribution { get; init; }
    public IReadOnlyList<HealthCheckDto> Checks { get; init; } = [];
    public IReadOnlyList<ChannelHealthDto> Channels { get; init; } = [];
    public required StorageStatusDto Storage { get; init; }
    public required EventCountsDto Last24Hours { get; init; }
    public DateTimeOffset? NextDigestAt { get; init; }
    /// <summary>End of rollout mode (12.5); null when not active.</summary>
    public DateTimeOffset? RolloutModeUntil { get; init; }
    public IReadOnlyList<ExclusionSuggestionDto> Suggestions { get; init; } = [];
}

public sealed record ConfigEnvelope
{
    public required long Version { get; init; }               // optimistic concurrency; also sent as ETag
    public required WatchdogConfiguration Configuration { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
    /// <summary>True for Viewer responses: exclusions, recipient addresses, SMTP identity and channel URL hints removed
    /// (ConfigRedactor). A redacted envelope can never be PUT.</summary>
    public bool IsRedacted { get; init; }
}

public enum IssueSeverity { Error, Warning }

public sealed record ConfigIssue
{
    public required string Path { get; init; }       // JSON path, e.g. "rules[3].delivery.channelIds[0]"
    public required string Code { get; init; }       // stable code (section 8.3) – GUI localizes "Watchdog.Validation.{Code}"
    public required IssueSeverity Severity { get; init; }
    public required string Message { get; init; }    // English fallback
    public IReadOnlyList<string> Args { get; init; } = [];
}

public sealed record ConfigValidationResult
{
    public IReadOnlyList<ConfigIssue> Issues { get; init; } = [];
    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);
}

public sealed record TestEmailRequest
{
    public IReadOnlyList<string> Recipients { get; init; } = [];
    /// <summary>Unsaved settings to test. Secrets come from SecretField.NewValue; a stored secret is used only when the
    /// unsaved settings have the same destination binding as the stored one (section 18) – otherwise the request is
    /// rejected (400, code secretRebindRequired) before anything is sent. Null = saved settings.</summary>
    public SmtpSettings? Settings { get; init; }
    public override string ToString() => $"TestEmailRequest({Recipients.Count} recipients)";
}

public sealed record TestChannelRequest
{
    /// <summary>Unsaved channel (same binding rule as TestEmailRequest); null = saved channel {channelId}.</summary>
    public ChannelSettings? Settings { get; init; }
    public override string ToString() => "TestChannelRequest";
}

public enum DeliveryStage { Resolve, Connect, Tls, Token, Authenticate, Sender, Recipients, Data, HttpRequest, HttpResponse }

public sealed record DeliveryStageResult
{
    public required DeliveryStage Stage { get; init; }
    public required bool Success { get; init; }
    public string? Detail { get; init; }          // "TLS 1.3 TLS_AES_256_GCM_SHA384", "250 2.0.0 OK", "HTTP 202"
    public int? StatusCode { get; init; }
    public string? EnhancedStatus { get; init; }  // "5.7.139"
    public TimeSpan Duration { get; init; }
}

public static class DeliveryHints
{
    public const string BasicAuthDisabled = "smtp.basicAuthDisabled";         // 535 5.7.139 / 550 5.7.30
    public const string SmtpAuthDisabledForMailbox = "smtp.smtpAuthDisabled"; // Set-CASMailbox -SmtpClientAuthenticationDisabled $false
    public const string DirectSendRejected = "smtp.directSendRejected";       // 550 5.7.68
    public const string CertificateUntrusted = "tls.certificateUntrusted";
    public const string RevocationUnavailable = "tls.revocationUnavailable";
    public const string StartTlsMissing = "smtp.startTlsMissing";
    public const string RetiredTeamsUrl = "teams.retiredUrl";                 // *.webhook.office.com / *.logic.azure.com
    public const string AcceptedNotDelivered = "teams.acceptedNotDelivered";  // always on 202
    public const string TeamsAuthMismatch = "teams.authMismatch";             // SAS vs OAuth error texts
    public const string WebhookGone = "webhook.gone";                         // 410
    public const string SecretRebindRequired = "secret.rebindRequired";       // stored secret bound to another destination
}

public sealed record DeliveryTestResult
{
    public required bool Success { get; init; }
    public IReadOnlyList<DeliveryStageResult> Stages { get; init; } = [];
    public IReadOnlyList<string> Hints { get; init; } = [];
    public TimeSpan Duration { get; init; }
}

public sealed record RulePreviewRequest
{
    public required RuleSettings Rule { get; init; }
    public int Days { get; init; } = 7;           // 1..90
}

public sealed record DailyCount(DateOnly Day, int Events, int Messages);

public sealed record RulePreviewResult
{
    public int MatchedEvents { get; init; }
    public int EstimatedImmediateMessages { get; init; }   // after aggregation + throttle simulation
    public IReadOnlyList<DailyCount> PerDay { get; init; } = [];
    public IReadOnlyList<WatchEvent> Samples { get; init; } = [];    // ≤ 20, newest first
    public bool ScanLimitReached { get; init; }                      // > 100 000 events scanned
}

public sealed record DigestPreviewDto
{
    public required DateTimeOffset PeriodStart { get; init; }
    public required DateTimeOffset PeriodEnd { get; init; }
    public required string Subject { get; init; }
    public required string Text { get; init; }    // plain-text alternative (shown in the GUI)
    public required string Html { get; init; }
    public int EventCount { get; init; }
    public DateTimeOffset? NextScheduledAt { get; init; }
    public DateTimeOffset? LastSentAt { get; init; }
}

public sealed record OutboxQuery
{
    public OutboxStatus? Status { get; init; }
    public string? ChannelId { get; init; }
    public long? Before { get; init; }
    public int Limit { get; init; } = 100;
    public string ToQueryString();
}

public sealed record OutboxItemDto
{
    public required long Id { get; init; }
    public required string MessageId { get; init; }
    public required DeliveryChannelType ChannelType { get; init; }
    public required string ChannelId { get; init; }
    public string? ChannelName { get; init; }
    public required DeliveryKind Kind { get; init; }
    public required OutboxStatus Status { get; init; }
    public int Attempts { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public string? LastErrorCode { get; init; }
    public string? LastError { get; init; }      // never contains message content or directory values
    public string? Subject { get; init; }        // e-mail subject / card title (directory data; shown only in the GUI)
    public int EventCount { get; init; }
    public int RecipientCount { get; init; }
}

public sealed record OutboxPage
{
    public IReadOnlyList<OutboxItemDto> Items { get; init; } = [];
    public bool HasMore { get; init; }
}

public sealed record ConfigAuditEntry
{
    public required long Id { get; init; }
    public required DateTimeOffset At { get; init; }
    public string? BySid { get; init; }
    public required string ByAccount { get; init; }
    public required string Summary { get; init; }   // "rules: 2 changed (new-accounts, lockouts); channels: teams-it URL replaced"
    public long Version { get; init; }
}

public sealed record ConfigAuditPage
{
    public IReadOnlyList<ConfigAuditEntry> Items { get; init; } = [];
    public bool HasMore { get; init; }
}

/// <summary>ProblemDetails "type" values (RFC 9457) used by the API; the client maps them to WatchdogClientErrorKind.</summary>
public static class ApiProblem
{
    public const string Validation = "https://admng/problems/validation";          // 400, extensions["issues"] = ConfigIssue[]
    public const string VersionConflict = "https://admng/problems/version-conflict"; // 412
    public const string ClientHeaderMissing = "https://admng/problems/client-header"; // 400
    public const string NotFound = "https://admng/problems/not-found";             // 404
    public const string Unavailable = "https://admng/problems/unavailable";         // 503 (store/directory down)
}
```

### 5.4 Configuration model

```csharp
namespace AdMng.Watchdog.Contracts.Configuration;

public sealed record WatchdogConfiguration
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public GeneralSettings General { get; init; } = new();
    public DirectorySettings Directory { get; init; } = new();
    public AttributionSettings Attribution { get; init; } = new();
    public ScanSettings Scans { get; init; } = new();
    public PrivilegedGroupSettings PrivilegedGroups { get; init; } = new();
    public BusinessHoursSettings BusinessHours { get; init; } = new();
    public NotificationSettings Notifications { get; init; } = new();
    public SmtpSettings Smtp { get; init; } = new();
    public IReadOnlyList<ChannelSettings> Channels { get; init; } = [];
    public IReadOnlyList<RecipientGroup> RecipientGroups { get; init; } = [];
    public DigestSettings Digest { get; init; } = new();
    public IReadOnlyList<RuleSettings> Rules { get; init; } = [];
    public RetentionSettings Retention { get; init; } = new();
}

public sealed record GeneralSettings
{
    public string InstanceName { get; init; } = "AD Watchdog";
    public string Language { get; init; } = "pl";                 // notifications: "pl" | "en"
    /// <summary>IANA or Windows id, resolved by TimeZoneResolver (works on NLS-mode hosts such as Windows Server 2019).</summary>
    public string TimeZone { get; init; } = "Europe/Warsaw";
    public string? PublicBaseUrl { get; init; }                   // https://admng-wd.contoso.com:8443 – links; null = no links
    /// <summary>Rollout mode length after the baseline (12.5); 0 = off.</summary>
    public int RolloutDays { get; init; } = 7;
    /// <summary>Set by an admin to end rollout mode early.</summary>
    public bool RolloutEnded { get; init; }
}

public enum MembershipTracking { AllGroups, PrivilegedAndWatchListOnly }

public sealed record DirectorySettings
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);        // RootDSE check; 5 s .. 5 min
    public TimeOnly FullReconciliationTime { get; init; } = new(2, 30);            // local time, daily
    public long UsnOverlap { get; init; }                                          // re-read USNs [W+1-overlap ..]
    public bool TrackDeletedObjects { get; init; } = true;
    public MembershipTracking MembershipTracking { get; init; } = MembershipTracking.AllGroups;
    public bool WatchConfigurationPartition { get; init; } = true;                 // new DCs (nTDSDSA)
    public bool WatchSchemaPartition { get; init; } = true;
    public IReadOnlyList<string> WakeUpContainers { get; init; } = [];             // change-notification wake-up (≤ 4)
    public IReadOnlyList<string> ExcludedSubtrees { get; init; } = [];             // never snapshotted, no events
    public IReadOnlyList<string> CriticalObjects { get; init; } =
        ["{domain}", "CN=AdminSDHolder,CN=System,{domain}", "OU=Domain Controllers,{domain}"];  // SD tracked
    public IReadOnlyList<string> ServiceAccountSubtrees { get; init; } = [];       // tag serviceAccount
    public IReadOnlyList<string> DisabledAccountsSubtrees { get; init; } = [];     // tag disabledOu
    public IReadOnlyList<string> AdditionalWatchedAttributes { get; init; } = [];  // appended to user/computer sets
    public IReadOnlyList<string> IgnoredAttributes { get; init; } = [];            // removed from watched sets
    public IReadOnlyList<WatchEventKind> EnabledOptionalKinds { get; init; } = []; // kinds with RecordedByDefault=false
    public IReadOnlyList<WatchEventKind> DisabledKinds { get; init; } = [];        // never recorded (health and Tier-0 kinds rejected, 8.3)
    public bool TrackComputerPasswordChanges { get; init; }                        // machine pwdLastSet rotation
    public bool TrackManagedPasswordRollover { get; init; }                        // gMSA/MSA/dMSA automatic password rollover
    public bool TrackComputerOsChanges { get; init; }                              // operatingSystem*, dNSHostName by SELF
    public int MaxValuesPerAttribute { get; init; } = 100;                         // stored values per side of a change
    public TimeSpan FailoverAfter { get; init; } = TimeSpan.FromMinutes(10);       // pinned DC down → another DC
    /// <summary>Changes to an object younger than this (whenCreated) are folded into its Created event (10.6.3).</summary>
    public TimeSpan CreationSettleWindow { get; init; } = TimeSpan.FromMinutes(2);
}

public enum AttributionSourceKind { DomainControllers, ForwardedEvents, None }

public sealed record AttributionSettings
{
    public bool Enabled { get; init; } = true;
    public AttributionSourceKind Source { get; init; } = AttributionSourceKind.DomainControllers;
    public string? Collector { get; init; }                                  // WEF collector host for ForwardedEvents
    /// <summary>Empty = discover writable DCs of this domain; entries must be DCs known to DomainControllerDirectory.</summary>
    public IReadOnlyList<string> DomainControllers { get; init; } = [];
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MatchWindow { get; init; } = TimeSpan.FromSeconds(3);    // ± around originating time
    public TimeSpan FallbackWindow { get; init; } = TimeSpan.FromMinutes(2); // ± when no metadata
    public TimeSpan ReplicationLatencyBound { get; init; } = TimeSpan.FromMinutes(15);
    public IReadOnlyList<TimeSpan> PendingRetries { get; init; } =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];
    public bool LookupLogonSource { get; init; } = true;                     // 4624 by SubjectLogonId
    public bool LookupLockoutSources { get; init; } = true;                  // 4771/4776/4625 around lockouts
    /// <summary>Oldest 4624 considered (From bound of the backwards search; also capped by the log's oldest record).</summary>
    public TimeSpan LogonLookback { get; init; } = TimeSpan.FromHours(12);
    /// <summary>Timeout of one on-demand source query (4624/4771/4776); at most one such query per DC at a time.</summary>
    public TimeSpan SourceQueryTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Ingestion budget per source and poll; exceeding it raises HealthEventLogGap ("flooding").</summary>
    public int MaxRecordsPerPoll { get; init; } = 50_000;
    public bool ResolveReverseDns { get; init; } = true;
    public bool SuppressComputerSelfChanges { get; init; } = true;
}

public sealed record AccountExpiryScan
{
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<int> ThresholdsDays { get; init; } = [14, 7, 1];
    public bool IncludeDisabled { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);
    /// <summary>A threshold whose day falls on a non-business day fires on the previous business day (10.14).</summary>
    public bool FireOnBusinessDays { get; init; } = true;
}

public enum PasswordExpiryScope { None, PrivilegedAndService, AllUsers }

public sealed record PasswordExpiryScan
{
    public PasswordExpiryScope Scope { get; init; } = PasswordExpiryScope.PrivilegedAndService;
    public IReadOnlyList<int> ThresholdsDays { get; init; } = [14, 7, 1];
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);
}

public sealed record InactiveAccountsScan
{
    public bool Enabled { get; init; } = true;
    public int Days { get; init; } = 90;              // ≥ 30 (lastLogonTimestamp lag)
    public int PrivilegedDays { get; init; } = 180;
    public int MinAgeDays { get; init; } = 30;        // ignore accounts younger than this
    public DayOfWeek ReportDay { get; init; } = DayOfWeek.Monday;
}

public sealed record BurstSettings
{
    public int Threshold { get; init; }
    public TimeSpan Window { get; init; }
    /// <summary>MassLockout only: effective threshold = max(Threshold, ceil(percent × enabled users / 100)).</summary>
    public double PercentOfEnabledUsers { get; init; }
}

public sealed record PasswordSprayDetection
{
    /// <summary>Off by default: needs Kerberos Authentication Service / Credential Validation failure auditing (docs 4.5).</summary>
    public bool Enabled { get; init; }
    public int DistinctAccounts { get; init; } = 20;               // per source IP / workstation
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record ScanSettings
{
    public AccountExpiryScan AccountExpiry { get; init; } = new();
    public PasswordExpiryScan PasswordExpiry { get; init; } = new();
    public TimeSpan LockoutRecheckInterval { get; init; } = TimeSpan.FromMinutes(5);
    public InactiveAccountsScan InactiveAccounts { get; init; } = new();
    public BurstSettings MassLockout { get; init; } =
        new() { Threshold = 5, Window = TimeSpan.FromMinutes(10), PercentOfEnabledUsers = 0.5 };
    /// <summary>Counts deletions of users, groups, OUs and service accounts.</summary>
    public BurstSettings MassDeletion { get; init; } = new() { Threshold = 10, Window = TimeSpan.FromMinutes(10) };
    /// <summary>Counts deletions of computers and contacts (stale-computer clean-ups are routine).</summary>
    public BurstSettings MassComputerDeletion { get; init; } = new() { Threshold = 50, Window = TimeSpan.FromMinutes(10) };
    public PasswordSprayDetection PasswordSpray { get; init; } = new();
    public TimeSpan PermissionSelfCheckInterval { get; init; } = TimeSpan.FromHours(24);
}

public sealed record PrivilegedGroupSettings
{
    /// <summary>Tier "Critical". Tokens {domainSid} / {rootDomainSid} are expanded at runtime.</summary>
    public IReadOnlyList<string> CriticalSids { get; init; } =
        ["S-1-5-32-544", "{domainSid}-512", "{domainSid}-516", "{rootDomainSid}-518", "{rootDomainSid}-519",
         "{domainSid}-526", "{rootDomainSid}-527"];
    public IReadOnlyList<string> HighSids { get; init; } =
        ["S-1-5-32-548", "S-1-5-32-549", "S-1-5-32-550", "S-1-5-32-551", "S-1-5-32-552",
         "{domainSid}-520", "{domainSid}-517", "{domainSid}-521"];
    /// <summary>Groups without a well-known RID, matched by sAMAccountName (case-insensitive).</summary>
    public IReadOnlyList<string> HighSamAccountNames { get; init; } = ["DnsAdmins"];
    public IReadOnlyList<string> AdditionalCriticalDns { get; init; } = [];
    public IReadOnlyList<string> AdditionalHighDns { get; init; } = [];
    /// <summary>Removal from this group raises High (Protected Users).</summary>
    public IReadOnlyList<string> ProtectRemovalSids { get; init; } = ["{domainSid}-525"];
    /// <summary>Ordinary groups whose membership changes notify immediately (GroupFilter.WatchList).</summary>
    public IReadOnlyList<string> WatchListDns { get; init; } = [];
}

public sealed record BusinessHoursSettings
{
    public IReadOnlyList<DayOfWeek> Days { get; init; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    public TimeOnly Start { get; init; } = new(7, 0);
    public TimeOnly End { get; init; } = new(17, 0);
    public string HolidayCalendar { get; init; } = "PL";          // "PL" | "None"
    public IReadOnlyList<DateOnly> ExtraHolidays { get; init; } = [];
}

public sealed record NotificationSettings
{
    public string SubjectPrefix { get; init; } = "[AD Watchdog]";
    public TimeSpan AttributionGrace { get; init; } = TimeSpan.FromSeconds(90);    // hold < High waiting for the actor
    public TimeSpan AggregationWindow { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan HighSeverityFlushDelay { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxItemsPerMessage { get; init; } = 50;
    public int MaxMessagesPerRulePerHour { get; init; } = 10;                      // Critical exempt
    public bool FollowUpWhenAttributed { get; init; } = true;                      // High/Critical sent with actor pending
    public int MaxValueLength { get; init; } = 200;
}

public enum SmtpSecurity { None, StartTls, StartTlsWhenAvailable, SslOnConnect }
public enum SmtpAuthentication { None, Basic, OAuth2ClientCredentials }
public enum CertificateValidationMode { System, PinnedSha256, AcceptAnyDevelopmentOnly }

/// <summary>Entra cloud – fixes the token authority host and the SMTP scope (no free-text authority/scope: an
/// attacker-chosen authority would receive the client assertion or secret).
/// Public: login.microsoftonline.com, https://outlook.office365.com/.default;
/// UsGovernment: login.microsoftonline.us, https://outlook.office365.us/.default;
/// China: login.partner.microsoftonline.cn, https://partner.outlook.cn/.default.</summary>
public enum EntraCloud { Public, UsGovernment, China }

public static class EntraCloudInfo
{
    public static string Authority(EntraCloud cloud, string tenantId);      // https://<host>/<tenant GUID>/v2.0
    public static string SmtpScope(EntraCloud cloud);
    /// <summary>Accepted Teams trigger scopes; Public: "https://service.flow.microsoft.com//.default",
    /// "https://service.flow.microsoft.com/.default"; UsGovernment: the gov.service.flow / high.service.flow variants;
    /// China: "https://service.powerautomate.cn//.default" and the single-slash form.</summary>
    public static IReadOnlyList<string> TeamsScopes(EntraCloud cloud);
}

public sealed record SmtpOAuthSettings
{
    public EntraCloud Cloud { get; init; } = EntraCloud.Public;
    public string? TenantId { get; init; }                // GUID
    public string? ClientId { get; init; }                // GUID
    public string? CertificateThumbprint { get; init; }   // LocalMachine\My, private key readable by the gMSA (preferred)
    public SecretField ClientSecret { get; init; } = new();
}

public sealed record SmtpSettings
{
    public bool Enabled { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; } = 587;
    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;   // also for relays on port 25
    public SmtpAuthentication Authentication { get; init; } = SmtpAuthentication.None;
    public string? UserName { get; init; }                // Basic: login; OAuth2: sender mailbox (XOAUTH2 user=)
    public SecretField Password { get; init; } = new();
    public SmtpOAuthSettings OAuth { get; init; } = new();
    public string? From { get; init; }                    // "AD Watchdog <watchdog@contoso.com>"
    public string? ReplyTo { get; init; }
    public string? LocalDomain { get; init; }             // EHLO + Message-ID domain (hostname syntax); null = machine FQDN
    public CertificateValidationMode CertificateValidation { get; init; } = CertificateValidationMode.System;
    public string? PinnedSha256 { get; init; }            // hex SHA-256 of the server certificate
    public bool CheckRevocation { get; init; } = true;
    public int MaxMessagesPerMinute { get; init; } = 20;
    public int MaxRecipientsPerMessage { get; init; } = 50;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
}

public enum ChannelType { Teams, Webhook }
public enum DetailLevel { Minimal, Standard, Full }
public enum TeamsAuthMode { Sas, EntraClientCredentials }

public sealed record RateLimitSettings
{
    public int PerSecond { get; init; } = 1;
    public int PerMinute { get; init; } = 20;
    public int PerHour { get; init; } = 100;
}

public sealed record TeamsChannelOptions
{
    public TeamsAuthMode AuthMode { get; init; } = TeamsAuthMode.Sas;
    public EntraCloud Cloud { get; init; } = EntraCloud.Public;
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? CertificateThumbprint { get; init; }
    public SecretField ClientSecret { get; init; } = new();
    /// <summary>Only the documented Power Automate scopes of the cloud are accepted (8.3 invalidScope); Public:
    /// "https://service.flow.microsoft.com//.default" (default) or "https://service.flow.microsoft.com/.default".</summary>
    public string Scope { get; init; } = "https://service.flow.microsoft.com//.default";
    public string CardVersion { get; init; } = "1.4";
}

public sealed record WebhookChannelOptions
{
    public SecretField SigningSecret { get; init; } = new();          // "whsec_" + base64(32 random bytes)
    public SecretField PreviousSigningSecret { get; init; } = new();  // rotation: both signatures sent until cleared
    public bool SendGitHubSignature { get; init; }                    // X-Hub-Signature-256
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];    // empty = any https host
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool IncludeDigest { get; init; } = true;                  // watchdog.digest events
    /// <summary>watchdog.heartbeat every interval (≥ 5 min) so a SIEM can alert when it stops; null = off.</summary>
    public TimeSpan? HeartbeatInterval { get; init; }
}

public sealed record ChannelSettings
{
    public required string Id { get; init; }                 // [a-z0-9-]{1,40}, unique; "email" reserved
    public required ChannelType Type { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public SecretField Url { get; init; } = new();           // Hint = scheme://host[:port]/… (never a path segment)
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Standard;   // Teams Standard, webhook Minimal (defaults factory)
    public TeamsChannelOptions? Teams { get; init; }
    public WebhookChannelOptions? Webhook { get; init; }
    public RateLimitSettings RateLimit { get; init; } = new();
}

public sealed record RecipientGroup
{
    public required string Id { get; init; }                 // [a-z0-9-]{1,40}
    public required string Name { get; init; }
    public IReadOnlyList<string> Addresses { get; init; } = [];
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Full;
}

public sealed record DigestSettings
{
    public bool Enabled { get; init; } = true;
    public TimeOnly Time { get; init; } = new(7, 30);
    public IReadOnlyList<DayOfWeek> Days { get; init; } = Enum.GetValues<DayOfWeek>();
    public IReadOnlyList<string> RecipientGroupIds { get; init; } = ["it-admins"];
    public IReadOnlyList<string> ChannelIds { get; init; } = [];
    public bool SendWhenEmpty { get; init; } = true;            // heartbeat
    public int MaxRowsPerSection { get; init; } = 50;
    public DayOfWeek WeeklySectionsDay { get; init; } = DayOfWeek.Monday;
    public int UpcomingExpiryDays { get; init; } = 14;
    /// <summary>The first business day after non-business days (weekend, holidays) covers the whole period since the
    /// last business-day digest; the digests of non-business days are still sent (heartbeat).</summary>
    public bool CoverNonBusinessDays { get; init; } = true;
}

public enum DeliveryHours { Default, Always, BusinessHours }   // Default: Always for ≥ High, else BusinessHours
public enum GroupFilter { Any, Privileged, WatchList, Explicit }

public sealed record RuleScope
{
    public IReadOnlyList<string> IncludeSubtrees { get; init; } = [];   // empty = whole domain
    public IReadOnlyList<string> ExcludeSubtrees { get; init; } = [];
}

public sealed record ActorFilter
{
    public IReadOnlyList<string> ExcludeActors { get; init; } = [];     // SIDs only (the GUI resolves names to SIDs)
    public bool ExcludeSelfChanges { get; init; }
}

public sealed record RuleDelivery
{
    public bool Immediate { get; init; }
    public IReadOnlyList<string> RecipientGroupIds { get; init; } = [];
    public IReadOnlyList<string> ChannelIds { get; init; } = [];
    /// <summary>Also every enabled Teams/webhook channel (the locked health rule ships with true).</summary>
    public bool AllEnabledChannels { get; init; }
    public bool IncludeInDigest { get; init; } = true;
    public DeliveryHours Hours { get; init; } = DeliveryHours.Default;
    public TimeSpan? AggregationWindow { get; init; }
    public int? MaxMessagesPerHour { get; init; }
    /// <summary>Per object: after an immediate notification about an object, further events of this rule for the same
    /// object within this period go to the digest only (lockouts: 1 h).</summary>
    public TimeSpan? RepeatSuppression { get; init; }
    /// <summary>Expiry kinds: also e-mail the object's manager (manager → mail, detail level Minimal). Off by default.</summary>
    public bool NotifyManager { get; init; }
}

public sealed record RuleExclusion
{
    public required string Id { get; init; }
    public Guid? ObjectGuid { get; init; }
    public string? SubtreeDn { get; init; }
    public string? Actor { get; init; }            // SID only
    public string? Attribute { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public required string CreatedBy { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record RuleSettings
{
    public required string Id { get; init; }                         // [a-z0-9-]{1,60}
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public bool BuiltIn { get; init; }
    public bool Locked { get; init; }                                // health rule: cannot be disabled/deleted
    public IReadOnlyList<WatchEventKind> Kinds { get; init; } = [];  // empty = any
    public IReadOnlyList<WatchObjectKind> ObjectKinds { get; init; } = [];
    public RuleScope Scope { get; init; } = new();
    public IReadOnlyList<string> Attributes { get; init; } = [];     // AttributesChanged etc.: any of these changed
    public IReadOnlyList<string> RequiredTags { get; init; } = [];   // event must carry ALL
    public IReadOnlyList<string> AnyTags { get; init; } = [];        // event must carry ANY (when non-empty)
    public GroupFilter Groups { get; init; } = GroupFilter.Any;      // membership kinds: which groups
    public IReadOnlyList<string> GroupDns { get; init; } = [];       // GroupFilter.Explicit
    public Severity? MinSeverity { get; init; }
    public Severity? Severity { get; init; }                         // override for this rule's notifications
    public ActorFilter Actors { get; init; } = new();
    public RuleDelivery Delivery { get; init; } = new();
    public IReadOnlyList<RuleExclusion> Exclusions { get; init; } = [];
}

public sealed record RetentionSettings
{
    public int EventsDays { get; init; } = 365;
    public int SecurityEventsDays { get; init; } = 30;
    public int OutboxSentDays { get; init; } = 30;
    public int OutboxDeadDays { get; init; } = 90;
    public int DeletedSnapshotDays { get; init; } = 180;
    public int ConfigAuditDays { get; init; } = 1095;
    public int BackupsToKeep { get; init; } = 7;
    /// <summary>Pre-migration backups older than this are deleted (the newest one is always kept).</summary>
    public int PreMigrationBackupDays { get; init; } = 30;
    /// <summary>Size cap of watchdog.db; security_events are purged oldest-first when reached, HealthStorage at 80 %.</summary>
    public int MaxDatabaseMb { get; init; } = 10_240;
    public TimeOnly MaintenanceTime { get; init; } = new(3, 15);
}

/// <summary>A write-only secret in the configuration document. GET returns IsSet/Hint only.</summary>
public sealed record SecretField
{
    public bool IsSet { get; init; }
    public string? Hint { get; init; }
    /// <summary>PUT only: new value. Never returned, never stored in the configuration document.</summary>
    public string? NewValue { get; init; }
    /// <summary>PUT only: remove the stored secret.</summary>
    public bool Clear { get; init; }
    public override string ToString() => IsSet ? "SecretField(set)" : "SecretField(empty)";
}

public static class SecretNames
{
    public const string SmtpPassword = "smtp.password";
    public const string SmtpOAuthClientSecret = "smtp.oauth.clientSecret";
    public static string ChannelUrl(string channelId) => $"channel.{channelId}.url";
    public static string ChannelClientSecret(string channelId) => $"channel.{channelId}.clientSecret";
    public static string WebhookSigningSecret(string channelId) => $"channel.{channelId}.signingSecret";
    public static string WebhookPreviousSigningSecret(string channelId) => $"channel.{channelId}.previousSigningSecret";
}
```

Records with collection members compare by reference – the GUI's dirty tracking MUST compare serialized JSON, not
`Equals`.

### 5.5 Client (`AdMng.Watchdog.Client`)

```csharp
namespace AdMng.Watchdog.Client;

public sealed record WatchdogClientOptions
{
    public required Uri BaseAddress { get; init; }             // https://admng-wd.contoso.com:8443/
    /// <summary>Null = process identity (CredentialCache.DefaultCredentials). Kept in memory only.</summary>
    public NetworkCredential? Credential { get; init; }
    /// <summary>Optional SHA-256 pin of the server certificate (in addition to normal validation failing).</summary>
    public string? PinnedCertificateSha256 { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Tests/Development only: allow http://localhost or 127.0.0.1.</summary>
    public bool AllowInsecureLoopback { get; init; }
    public override string ToString() => $"WatchdogClientOptions({BaseAddress})";
}

public enum WatchdogClientErrorKind
{
    NotConfigured, Unreachable, Timeout, CertificateInvalid, Unauthorized, Forbidden, NotFound,
    Validation, VersionConflict, ApiVersionMismatch, ServerError, Cancelled,
}

public sealed class WatchdogClientException : Exception
{
    public WatchdogClientException(WatchdogClientErrorKind kind, string message, Exception? inner = null);
    public WatchdogClientErrorKind Kind { get; }
    public int? StatusCode { get; init; }
    public IReadOnlyList<ConfigIssue> Issues { get; init; } = [];
}

public interface IWatchdogClient : IDisposable
{
    bool IsConfigured { get; }
    Uri? BaseAddress { get; }
    /// <summary>Replaces the connection (disposes the previous handler). Null = not configured.</summary>
    void Configure(WatchdogClientOptions? options);
    Task<MeDto> GetMeAsync(CancellationToken cancellationToken);
    Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<EventKindInfo>> GetCatalogAsync(CancellationToken cancellationToken);
    Task<EventPage> GetEventsAsync(EventQuery query, CancellationToken cancellationToken);
    Task<EventDetails> GetEventAsync(long id, CancellationToken cancellationToken);
    Task AcknowledgeAsync(long id, AcknowledgeRequest request, CancellationToken cancellationToken);
    Task AcknowledgeManyAsync(BulkAcknowledgeRequest request, CancellationToken cancellationToken);
    Task<ConfigEnvelope> GetConfigAsync(CancellationToken cancellationToken);
    Task<WatchdogConfiguration> GetDefaultConfigAsync(CancellationToken cancellationToken);
    /// <summary>PUT with If-Match: "Version". Returns the saved envelope (new version, secrets as IsSet/Hint).</summary>
    Task<ConfigEnvelope> SaveConfigAsync(ConfigEnvelope envelope, CancellationToken cancellationToken);
    Task<ConfigValidationResult> ValidateConfigAsync(WatchdogConfiguration configuration, CancellationToken cancellationToken);
    Task<ConfigAuditPage> GetConfigAuditAsync(long? before, int limit, CancellationToken cancellationToken);
    Task<DeliveryTestResult> SendTestEmailAsync(TestEmailRequest request, CancellationToken cancellationToken);
    Task<DeliveryTestResult> SendTestChannelAsync(string channelId, TestChannelRequest request, CancellationToken cancellationToken);
    Task<RulePreviewResult> PreviewRuleAsync(RulePreviewRequest request, CancellationToken cancellationToken);
    Task<DigestPreviewDto> PreviewDigestAsync(CancellationToken cancellationToken);
    Task SendDigestNowAsync(CancellationToken cancellationToken);
    Task<OutboxPage> GetOutboxAsync(OutboxQuery query, CancellationToken cancellationToken);
    Task RetryOutboxAsync(long id, CancellationToken cancellationToken);
}
```

`WatchdogHttpClient` (W-service): constructor `WatchdogHttpClient(TimeProvider timeProvider, ILogger<WatchdogHttpClient> logger)`
(both resolvable in the App's DI; the Client csproj references `Microsoft.Extensions.Logging.Abstractions`).
`Configure` rejects a non-`https` `BaseAddress` (`ArgumentException`; only `http://localhost`/`127.0.0.1` when the
caller passes `AllowInsecureLoopback`, used by Development tests). Credentials are never offered to other origins:
`SocketsHttpHandler { Credentials = new CredentialCache { { origin(BaseAddress), "Negotiate", options.Credential ??
CredentialCache.DefaultNetworkCredentials } }, PreAuthenticate = false, AllowAutoRedirect = false,
PooledConnectionLifetime = 5 min }` (`.NET` has no intranet-zone logic – `DefaultCredentials` alone would answer any
host's Negotiate/NTLM challenge); the GUI connection banner always shows the host it talks to. HTTP/1.1 only
(`DefaultRequestVersion = HttpVersion.Version11`, `VersionPolicy = RequestVersionExact`); `HttpClient.Timeout =
InfiniteTimeSpan` and per-call `CancellationTokenSource` (RequestTimeout, or `WaitSeconds + 15 s` for long-poll);
sends `X-AdMng-Watchdog-Api: 1` and `X-AdMng-Client: 1`; maps status codes / problem types to
`WatchdogClientErrorKind` (401 Unauthorized, 403 Forbidden, 404 NotFound, 412 VersionConflict, 400 + Validation type →
Validation with Issues, `HttpRequestException` with `SocketError` → Unreachable, `AuthenticationException` →
CertificateInvalid, `TaskCanceledException` without caller cancellation → Timeout, server API version ≠ 1 →
ApiVersionMismatch). Logs only method, path and status (never bodies, never query values).

---

## 6. Core abstractions and model (`AdMng.Watchdog.Core`) – exact code (scaffold, frozen)

Namespaces: `AdMng.Watchdog.Core.Abstractions`, `AdMng.Watchdog.Core.Model`. Every async method takes a
`CancellationToken` last. Stores are implemented by W-service (SQLite) and by W-core's `InMemoryWatchdogStore`
(tests); one class may implement several interfaces. All store writes are transactional per call.

### 6.1 Model

```csharp
namespace AdMng.Watchdog.Core.Model;

public sealed record DcState
{
    public required string Domain { get; init; }              // DNS name, store key
    public required string DcHostName { get; init; }
    public required string DsServiceName { get; init; }       // DN of the NTDS Settings object
    public required Guid InvocationId { get; init; }
    public required long HighestUsn { get; init; }            // watermark W (all changes ≤ W processed)
    public DateTimeOffset? LastPollAt { get; init; }
    public DateTimeOffset? LastChangeAt { get; init; }
    public DateTimeOffset? LastFullSyncAt { get; init; }
    public DateTimeOffset? BaselineCompletedAt { get; init; }
    /// <summary>Set by the store on every commit with MembershipRelevant = true (PrivilegeEvaluator crash safety).</summary>
    public DateTimeOffset? MembershipsChangedAt { get; init; }
}

/// <summary>Last processed USN per DC identity (failback without a full resync, 10.3). The store upserts it for the
/// DC of every committed NewState.</summary>
public sealed record DcWatermark(string DsServiceName, Guid InvocationId, long HighestUsn, DateTimeOffset UpdatedAt);

public sealed record SnapshotAttribute
{
    public required IReadOnlyList<string> Values { get; init; }       // canonical (section 9.4), comparison form
    public IReadOnlyList<string> DisplayValues { get; init; } = [];   // DN values: display DN (same order); empty = Values
    public AttributeValueKind Kind { get; init; }
    public int? Version { get; init; }                                // replication metadata dwVersion when known
    public DateTimeOffset? ChangedAt { get; init; }                   // originating time, else detection time
}

public sealed record SnapshotObject
{
    public required Guid ObjectGuid { get; init; }
    public required string Dn { get; init; }
    public required WatchObjectKind Kind { get; init; }
    public IReadOnlyList<string> ObjectClasses { get; init; } = [];
    public string? Sid { get; init; }
    public string? SamAccountName { get; init; }
    public string? Name { get; init; }
    public string? DisplayName { get; init; }
    public long UsnCreated { get; init; }
    public long UsnChanged { get; init; }
    public DateTimeOffset? WhenCreated { get; init; }
    /// <summary>Only rows marked deleted by the DeletedObjectsScanner / reconciliation tombstone lookup. A later live
    /// appearance of such a GUID is ObjectRestored; out-of-scope or disappeared objects are removed instead (Removals).</summary>
    public bool IsDeleted { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    /// <summary>Settle window of a new object (10.6.3): the Created event is emitted when this time has passed.</summary>
    public DateTimeOffset? CreationPendingUntil { get; init; }
    public IReadOnlyDictionary<string, SnapshotAttribute> Attributes { get; init; } =
        new Dictionary<string, SnapshotAttribute>(StringComparer.OrdinalIgnoreCase);

    public ObjectRef ToRef();
}

public sealed record MemberReference
{
    /// <summary>Stable key: GUID "N" when known, else "sid:S-1-…", else "dn:&lt;normalized dn&gt;".</summary>
    public required string Key { get; init; }
    public Guid? ObjectGuid { get; init; }
    public string? Sid { get; init; }
    public required string Dn { get; init; }
}

public enum PrivilegeTier { High = 3, Critical = 4 }

public sealed record PrivilegedPrincipal
{
    public required string PrincipalKey { get; init; }           // MemberReference.Key
    public required Guid GroupGuid { get; init; }                // privileged group reached
    public required PrivilegeTier Tier { get; init; }
    public IReadOnlyList<Guid> Path { get; init; } = [];        // intermediate groups (direct = empty)
    public bool ViaPrimaryGroup { get; init; }
}

public sealed record PrivilegeState
{
    public IReadOnlyList<PrivilegedPrincipal> Principals { get; init; } = [];
    public DateTimeOffset ComputedAt { get; init; }
}

public sealed record DnRewrite(string OldSuffix, string NewSuffix);   // descendants of a moved/renamed container

public sealed record DetectionCommit
{
    /// <summary>Null = DC state unchanged (every chunk commit). Only the final commit of a cycle writes it – a chunk
    /// never writes a new DC identity with an old watermark (crash mid-Resync must lead to another Resync).</summary>
    public DcState? NewState { get; init; }
    public IReadOnlyList<SnapshotObject> Upserts { get; init; } = [];
    /// <summary>Full replacement of the member list of these groups.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<MemberReference>> Memberships { get; init; } =
        new Dictionary<Guid, IReadOnlyList<MemberReference>>();
    /// <summary>Objects now deleted (tombstone seen): marked deleted (kept for RetentionSettings.DeletedSnapshotDays)
    /// and removed from every member list.</summary>
    public IReadOnlyList<(Guid ObjectGuid, DateTimeOffset DeletedAt)> Deletions { get; init; } = [];
    /// <summary>Hard removal of snapshot rows (moved out of scope, disappeared without tombstone): the row and, for a
    /// group, its own member rows. Other groups' member lists are NOT touched (the object may still be a member).</summary>
    public IReadOnlyList<Guid> Removals { get; init; } = [];
    public IReadOnlyList<DnRewrite> DnRewrites { get; init; } = [];
    public IReadOnlyList<WatchEvent> Events { get; init; } = [];       // Id = 0
    public PrivilegeState? Privileges { get; init; }                   // null = unchanged
    /// <summary>The commit contains membership replacements, deletions/removals of principals or primaryGroupID
    /// changes – the store sets DcState.MembershipsChangedAt.</summary>
    public bool MembershipRelevant { get; init; }
    public bool IsBaseline { get; init; }                              // marks BaselineCompletedAt
}

public enum DetectionMode { NoChange, Baseline, Incremental, Failback, Resync, Reconciliation }

public sealed record DetectionCycleResult
{
    public required DetectionMode Mode { get; init; }
    public int ObjectsExamined { get; init; }
    public int EventsCreated { get; init; }
    public long HighestUsn { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];    // codes, no directory values
}

/// <summary>A parsed Security-log record (values of secret attributes already masked).</summary>
public sealed record SecurityEvent
{
    public required string Source { get; init; }               // DC FQDN or collector (cursor key)
    public required string Computer { get; init; }             // System/Computer – the DC that logged it
    public required long RecordId { get; init; }
    public required int EventCode { get; init; }
    public int Version { get; init; }
    public required DateTimeOffset TimeCreated { get; init; }  // UTC
    /// <summary>EventData by Data/@Name; "-" and empty values omitted; %% codes decoded where documented.</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string? Get(string name);
    public string? SubjectSid => Get("SubjectUserSid");
    public string? SubjectAccount { get; }        // SubjectDomainName\SubjectUserName
    public string? SubjectLogonId { get; }        // normalized "0x…" lower-case
    public string? TargetSid => Get("TargetSid");
    public Guid? ObjectGuid { get; }              // 51xx ObjectGUID "{…}"
    public Guid? OpCorrelationId { get; }
}

public sealed record SecurityEventCriteria
{
    public IReadOnlyList<int> EventCodes { get; init; } = [];
    public string? Computer { get; init; }                 // null = any DC
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public string? TargetSid { get; init; }
    public Guid? ObjectGuid { get; init; }
    public string? MemberSid { get; init; }
    public string? TargetUserName { get; init; }
    public string? ObjectDn { get; init; }
    public int Limit { get; init; } = 200;
}

public sealed record EventLogCursor
{
    public required string Source { get; init; }
    public long LastRecordId { get; init; }
    public string? BookmarkXml { get; init; }
    public long? OldestRecordSeen { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record EventEvaluation
{
    public required long EventId { get; init; }
    public required Severity Severity { get; init; }
    public IReadOnlyList<string> MatchedRules { get; init; } = [];
}

/// <summary>What the AttributionEngine writes back into a stored event.</summary>
public sealed record AttributionUpdate
{
    public required Attribution Attribution { get; init; }
    public IReadOnlyList<string> AddedTags { get; init; } = [];     // selfChange, passwordReset, syncAccount
    /// <summary>Re-resolved severity of attribution-dependent kinds (12.2); null = unchanged.</summary>
    public Severity? Severity { get; init; }
    /// <summary>Lockout sources filled by LockoutSourceResolver; null = unchanged.</summary>
    public LockoutInfo? Lockout { get; init; }
    /// <summary>Null when the attribution is final.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }
}

public sealed record NotificationTarget
{
    public required DeliveryChannelType ChannelType { get; init; }
    public required string ChannelId { get; init; }                // "email" for SMTP
    public IReadOnlyList<string> Recipients { get; init; } = [];   // e-mail addresses (sorted, distinct)
    public DetailLevel DetailLevel { get; init; }
    public string Key { get; }                                     // channelId + "|" + hash(recipients)
}

public sealed record PendingNotification
{
    public long Id { get; init; }
    public required long EventId { get; init; }
    public required string RuleId { get; init; }
    public required NotificationTarget Target { get; init; }
    public required Severity Severity { get; init; }               // rule override or event severity
    public required string BucketKey { get; init; }                // AggregationPolicy
    public required DateTimeOffset DueAt { get; init; }            // earliest flush time of its bucket
    public DateTimeOffset? HoldForAttributionUntil { get; init; }
    public bool IsFollowUp { get; init; }
    public string? ReplyToMessageId { get; init; }                 // follow-ups: original Message-ID
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record RenderedEmail
{
    public required string Subject { get; init; }
    public required string HtmlBody { get; init; }
    public required string TextBody { get; init; }
    public string? InReplyTo { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record RenderedHttpMessage
{
    public required byte[] Body { get; init; }              // exact bytes to send (signature covers them)
    public string ContentType { get; init; } = "application/json; charset=utf-8";
    public string? WebhookEventType { get; init; }
    public string? Title { get; init; }                     // for the delivery log
}

public sealed record OutboxMessage
{
    public long Id { get; init; }
    public required string MessageId { get; init; }        // Guid v7 "N"; webhook-id / Message-ID local part
    public required DeliveryChannelType ChannelType { get; init; }
    public required string ChannelId { get; init; }
    /// <summary>NotificationTarget.Key of the pending rows it was rendered from (follow-ups reuse the target).</summary>
    public string? TargetKey { get; init; }
    /// <summary>At least one event was rendered with attribution Pending (FollowUpPlanner input).</summary>
    public bool SentWithPendingAttribution { get; init; }
    public required DeliveryKind Kind { get; init; }
    public IReadOnlyList<string> Recipients { get; init; } = [];
    public RenderedEmail? Email { get; init; }
    public RenderedHttpMessage? Http { get; init; }
    public string? RuleId { get; init; }
    public IReadOnlyList<long> EventIds { get; init; } = [];
    public Severity Severity { get; init; }
    public int Attempts { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset NextAttemptAt { get; init; }
    public override string ToString() => $"OutboxMessage({Id}, {ChannelType}, {ChannelId}, {Kind})";
}

/// <summary>Deferred = not attempted (rate limiter); the outbox does not count an attempt.</summary>
public enum DeliveryOutcome { Delivered, Deferred, Transient, Permanent, ChannelBroken, AuthenticationFailed }

public sealed record DeliveryResult
{
    public required DeliveryOutcome Outcome { get; init; }
    public string? ErrorCode { get; init; }         // "smtp.451", "http.429", "socket", "timeout", "auth" …
    public string? Detail { get; init; }            // no message content, no directory values
    public TimeSpan? RetryAfter { get; init; }
    public int? StatusCode { get; init; }
    public static DeliveryResult Delivered { get; } = new() { Outcome = DeliveryOutcome.Delivered };
}

public sealed record ExpiryNotice(Guid ObjectGuid, ExpiryKind Kind, long ExpiresAtFileTime, int ThresholdDays, DateTimeOffset NotifiedAt);

public sealed record UpcomingExpiry
{
    public required ObjectRef Object { get; init; }
    public required ExpiryKind Kind { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>A secret in memory only. ToString never reveals it.</summary>
public sealed class SecretValue
{
    public SecretValue(string value);
    public string Reveal();
    public override string ToString() => "SecretValue(***)";
}

/// <summary>A stored secret with the destination binding it was entered for (SecretBindings, section 18).</summary>
public sealed record StoredSecret(SecretValue Value, string Binding)
{
    public override string ToString() => $"StoredSecret(binding {Binding})";
}

/// <summary>One secret write applied in the same transaction as a configuration save. Value null = remove.</summary>
public sealed record SecretChange(string Name, SecretValue? Value, string? Binding)
{
    public override string ToString() => $"SecretChange({Name}, {(Value is null ? "remove" : "set")})";
}

public sealed record PurgeResult(int Events, int SecurityEvents, int OutboxMessages, int Snapshots, int Notices, int AuditEntries);
```

### 6.2 Abstractions

```csharp
namespace AdMng.Watchdog.Core.Abstractions;

public interface IDetectionStore
{
    Task<DcState?> GetDcStateAsync(string domain, CancellationToken cancellationToken);
    /// <summary>Last watermark processed on that DC identity, or null when this (dsServiceName, invocationId) was never polled.</summary>
    Task<DcWatermark?> GetWatermarkAsync(string dsServiceName, Guid invocationId, CancellationToken cancellationToken);
    /// <summary>Live snapshots whose CreationPendingUntil ≤ dueBefore (settle window over).</summary>
    Task<IReadOnlyList<SnapshotObject>> GetPendingCreationsAsync(DateTimeOffset dueBefore, CancellationToken cancellationToken);
    /// <summary>Live snapshot rows of one kind (DomainController, PublicKeyInfrastructure, Configuration – small sets).</summary>
    Task<IReadOnlyList<SnapshotObject>> GetByKindAsync(WatchObjectKind kind, CancellationToken cancellationToken);
    /// <summary>Snapshots by GUID, including rows marked deleted (restore detection). Missing GUIDs are absent.</summary>
    Task<IReadOnlyDictionary<Guid, SnapshotObject>> GetSnapshotsAsync(IReadOnlyCollection<Guid> objectGuids, CancellationToken cancellationToken);
    Task<SnapshotObject?> FindByDnAsync(string distinguishedName, CancellationToken cancellationToken);
    Task<SnapshotObject?> FindBySidAsync(string sid, CancellationToken cancellationToken);
    /// <summary>Live objects whose DN ends with ",{ancestorDn}" (case-insensitive, normalized).</summary>
    IAsyncEnumerable<SnapshotObject> GetDescendantsAsync(string ancestorDn, CancellationToken cancellationToken);
    IAsyncEnumerable<Guid> GetLiveObjectGuidsAsync(CancellationToken cancellationToken);
    Task<long> CountLiveObjectsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MemberReference>> GetMembersAsync(Guid groupGuid, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> GetGroupsContainingAsync(string memberKey, CancellationToken cancellationToken);
    /// <summary>All group → member edges (privilege evaluation). Mid-size domains: ≤ a few 100 k edges.</summary>
    Task<IReadOnlyList<(Guid GroupGuid, MemberReference Member)>> GetAllMembershipsAsync(CancellationToken cancellationToken);
    Task<PrivilegeState?> GetPrivilegeStateAsync(CancellationToken cancellationToken);
    /// <summary>Live users/computers/service accounts whose primaryGroupID RID is not 513, 514 or 515 (indexed column).</summary>
    Task<IReadOnlyList<(string MemberKey, uint PrimaryGroupRid)>> GetNonDefaultPrimaryGroupsAsync(CancellationToken cancellationToken);
    /// <summary>Applies one cycle atomically and returns the stored events (ids assigned, ascending).</summary>
    Task<IReadOnlyList<WatchEvent>> CommitAsync(DetectionCommit commit, CancellationToken cancellationToken);
}

public interface IEventStore
{
    Task<long> AppendAsync(WatchEvent watchEvent, CancellationToken cancellationToken);   // scans, health, event-log-only
    Task<EventPage> QueryAsync(EventQuery query, CancellationToken cancellationToken);
    /// <summary>Completes when an event with Id &gt; afterId exists or the timeout elapses (long-poll).</summary>
    Task WaitForNewerAsync(long afterId, TimeSpan timeout, CancellationToken cancellationToken);
    Task<WatchEvent?> GetAsync(long id, CancellationToken cancellationToken);
    Task<WatchEvent?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken);
    Task<IReadOnlyList<WatchEvent>> GetChangeSetAsync(Guid changeSetId, CancellationToken cancellationToken);
    IAsyncEnumerable<WatchEvent> GetRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    /// <summary>Events with attribution Pending whose next attempt time is ≤ dueBefore (new Pending events are due
    /// immediately: the store sets next_attribution_at = detected_at on insert).</summary>
    Task<IReadOnlyList<WatchEvent>> GetPendingAttributionAsync(DateTimeOffset dueBefore, int limit, CancellationToken cancellationToken);
    /// <summary>Stores attribution, added tags, re-resolved severity and lockout sources; updates the search text
    /// (workstation/IP). update.NextAttemptAt = null when the attribution is final.</summary>
    Task UpdateAttributionAsync(long id, AttributionUpdate update, CancellationToken cancellationToken);
    Task<IReadOnlyList<WatchEvent>> GetUnevaluatedAsync(int limit, CancellationToken cancellationToken);
    /// <summary>Atomically: stores severities/matched rules, marks events evaluated, inserts pending notifications
    /// (duplicates by (event, target, follow-up) ignored).</summary>
    Task CommitEvaluationAsync(IReadOnlyList<EventEvaluation> evaluations, IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken);
    Task<bool> AcknowledgeAsync(long id, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken);
    /// <summary>Returns the number of events acknowledged (already acknowledged ones are left unchanged).</summary>
    Task<int> AcknowledgeManyAsync(IReadOnlyList<long> ids, string by, string? comment, DateTimeOffset at, CancellationToken cancellationToken);
    Task<int> CountForObjectAsync(Guid objectGuid, WatchEventKind kind, DateTimeOffset since, CancellationToken cancellationToken);
    Task<EventCountsDto> CountAsync(DateTimeOffset since, CancellationToken cancellationToken);
    Task<long> GetLatestIdAsync(CancellationToken cancellationToken);
}

public interface INotificationQueueStore
{
    Task<IReadOnlyList<PendingNotification>> GetAllAsync(CancellationToken cancellationToken);
    /// <summary>Follow-ups and previous-configuration notifications; duplicates by (event, target, follow-up) ignored.</summary>
    Task AddAsync(IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken);
    /// <summary>Atomically deletes the pending rows and (when not null) enqueues the rendered message linked to their events.</summary>
    Task CompleteAsync(IReadOnlyList<long> pendingIds, OutboxMessage? message, CancellationToken cancellationToken);
    Task DropAsync(IReadOnlyList<long> pendingIds, CancellationToken cancellationToken);
    /// <summary>Moves the rows' DueAt (throttled buckets, 13.3).</summary>
    Task RescheduleAsync(IReadOnlyList<long> pendingIds, DateTimeOffset dueAt, CancellationToken cancellationToken);
}

public interface IOutboxStore
{
    Task<long> EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken);
    /// <summary>Due Pending/Retrying messages, marked Sending with a lease; expired leases become due again (crash
    /// safety). Leasing does not change Attempts.</summary>
    Task<IReadOnlyList<OutboxMessage>> LeaseDueAsync(DateTimeOffset now, int limit, TimeSpan lease, CancellationToken cancellationToken);
    /// <summary>Marks Sent, increments Attempts and sets the payload to NULL (only metadata is kept, 16.3).</summary>
    Task MarkSentAsync(long id, DateTimeOffset at, CancellationToken cancellationToken);
    /// <summary>countAttempt = false for rate-limiter deferrals (no attempt was made).</summary>
    Task MarkRetryAsync(long id, DateTimeOffset nextAttemptAt, string errorCode, string? error, bool countAttempt, CancellationToken cancellationToken);
    /// <summary>Marks Dead and increments Attempts; the payload is kept for a manual retry until OutboxDeadDays.</summary>
    Task MarkDeadAsync(long id, string errorCode, string? error, CancellationToken cancellationToken);
    Task<bool> RequeueAsync(long id, DateTimeOffset at, CancellationToken cancellationToken);    // manual retry of Dead
    Task<OutboxPage> QueryAsync(OutboxQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<EventDeliveryDto>> GetDeliveriesForEventAsync(long eventId, CancellationToken cancellationToken);
    /// <summary>Messages (any status, payload possibly null) that contain this event – FollowUpPlanner input.</summary>
    Task<IReadOnlyList<OutboxMessage>> GetMessagesForEventAsync(long eventId, CancellationToken cancellationToken);
    /// <summary>RuleDelivery.RepeatSuppression: was an immediate (non-digest) message of this rule about this object
    /// enqueued since the given time?</summary>
    Task<bool> WasNotifiedAboutObjectAsync(string ruleId, Guid objectGuid, DateTimeOffset since, CancellationToken cancellationToken);
    Task<int> CountSentAsync(string channelId, string? ruleId, DateTimeOffset since, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, int>> CountPendingByChannelAsync(CancellationToken cancellationToken);
}

public interface ISecurityEventStore
{
    Task<EventLogCursor?> GetCursorAsync(string source, CancellationToken cancellationToken);
    /// <summary>Inserts events (duplicates by (Computer, RecordId) ignored) and moves the cursor – one transaction.</summary>
    Task AppendAsync(IReadOnlyList<SecurityEvent> events, EventLogCursor cursor, CancellationToken cancellationToken);
    Task<IReadOnlyList<SecurityEvent>> FindAsync(SecurityEventCriteria criteria, CancellationToken cancellationToken);
    Task<DateTimeOffset?> GetLastSeenAsync(string computer, int eventCode, CancellationToken cancellationToken);  // coverage
}

public interface IExpiryNoticeStore
{
    /// <summary>False when the notice already exists (dedup).</summary>
    Task<bool> TryRecordAsync(ExpiryNotice notice, CancellationToken cancellationToken);
    Task ReplaceUpcomingAsync(ExpiryKind kind, IReadOnlyList<UpcomingExpiry> upcoming, CancellationToken cancellationToken);
    Task<IReadOnlyList<UpcomingExpiry>> GetUpcomingAsync(CancellationToken cancellationToken);
}

public interface IDigestRunStore
{
    /// <summary>End of the last digest period that was enqueued, or null before the first digest.</summary>
    Task<DateTimeOffset?> GetLastPeriodEndAsync(CancellationToken cancellationToken);
    Task RecordAsync(DateTimeOffset periodStart, DateTimeOffset periodEnd, int eventCount, IReadOnlyList<long> outboxIds, CancellationToken cancellationToken);
}

public sealed class ConfigVersionConflictException(long expected, long actual)
    : Exception($"Configuration version conflict (expected {expected}, actual {actual}).")
{
    public long Expected { get; } = expected;
    public long Actual { get; } = actual;
}

public interface IConfigStore
{
    Task<ConfigEnvelope?> GetAsync(CancellationToken cancellationToken);
    /// <summary>Saves (config already stripped of SecretField.NewValue) when the stored version equals expectedVersion
    /// (0 = first save, no stored row); applies the secret changes and writes the audit entry **in the same transaction**
    /// – a failed save leaves secrets untouched.</summary>
    /// <exception cref="ConfigVersionConflictException"/>
    Task<ConfigEnvelope> SaveAsync(WatchdogConfiguration configuration, long expectedVersion, PrincipalRef by, string summary,
        IReadOnlyList<SecretChange> secretChanges, CancellationToken cancellationToken);
    Task<ConfigAuditPage> GetAuditAsync(long? before, int limit, CancellationToken cancellationToken);
}

public interface ISecretStore
{
    Task<StoredSecret?> GetAsync(string name, CancellationToken cancellationToken);
    /// <summary>Tests and DR only – production writes go through IConfigStore.SaveAsync.</summary>
    Task SetAsync(string name, SecretValue value, string binding, CancellationToken cancellationToken);
    Task RemoveAsync(string name, CancellationToken cancellationToken);
    /// <summary>Stored secret names with their bindings (validation input).</summary>
    Task<IReadOnlyDictionary<string, string>> ListAsync(CancellationToken cancellationToken);
}

public interface IStoreMaintenance
{
    Task<PurgeResult> PurgeAsync(RetentionSettings retention, DateTimeOffset now, CancellationToken cancellationToken);
    Task<string> BackupAsync(CancellationToken cancellationToken);            // VACUUM INTO <data>\backup\…; returns file name
    Task<StorageStatusDto> GetStatusAsync(CancellationToken cancellationToken);
}

public enum DirectoryConnectionState { Disconnected, Connecting, Connected, Failed }

public interface IDirectorySessionProvider
{
    DirectoryConnectionState State { get; }
    string? CurrentServer { get; }
    /// <summary>The live session pinned to the watched DC (connects / reconnects / fails over). Callers never dispose it.
    /// PDC selection is the provider's job: with Directory.PreferPdc it reads fSMORoleOwner of the domain head on
    /// connect and (re)connects to the PDC emulator; Core only reports IsPdc.</summary>
    ValueTask<IDirectorySession> GetSessionAsync(CancellationToken cancellationToken);
    /// <summary>Reports a transport-level failure (ServerUnavailable, Timeout…) so the next call reconnects.</summary>
    void ReportFailure(IDirectorySession session, Exception error);
}

public sealed record SecurityEventQuery
{
    public required string LogName { get; init; }                 // "Security" | "ForwardedEvents"
    public IReadOnlyList<int> EventCodes { get; init; } = [];
    public long? AfterRecordId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    /// <summary>EventData equality filters, e.g. ("TargetLogonId", "0x30dc2").</summary>
    public IReadOnlyList<(string Name, string Value)> DataEquals { get; init; } = [];
    /// <summary>5136 AttributeLDAPDisplayName values to suppress (noise).</summary>
    public IReadOnlyList<string> SuppressAttributes { get; init; } = [];
    public bool NewestFirst { get; init; }                         // ReverseDirection
    public int MaxRecords { get; init; } = 1000;
}

public sealed record SecurityLogInfo(long? OldestRecordNumber, long? RecordCount, bool IsLogFull, long? FileSizeBytes);

public interface ISecurityEventSource
{
    /// <summary>False on non-Windows hosts or when attribution is disabled (NullSecurityEventSource).</summary>
    bool IsAvailable { get; }
    Task<SecurityLogInfo> GetLogInfoAsync(string host, string logName, CancellationToken cancellationToken);
    /// <summary>Raw event XML (EventRecord.ToXml()) in query order. Throws SecurityLogAccessException (Win32 error) on RPC/ACL failures.</summary>
    IAsyncEnumerable<string> ReadAsync(string host, SecurityEventQuery query, CancellationToken cancellationToken);
}

public sealed class SecurityLogAccessException(string host, int? win32Error, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Host { get; } = host;
    public int? Win32Error { get; } = win32Error;     // 5 access denied, 1722 RPC unavailable, …
}

public interface INotificationTransport
{
    DeliveryChannelType ChannelType { get; }
    Task<DeliveryResult> SendAsync(OutboxMessage message, CancellationToken cancellationToken);
}

public interface IDeliveryTester
{
    Task<DeliveryTestResult> TestEmailAsync(TestEmailRequest request, CancellationToken cancellationToken);
    Task<DeliveryTestResult> TestChannelAsync(string channelId, TestChannelRequest request, CancellationToken cancellationToken);
}

public readonly record struct JobResult(TimeSpan? NextRunIn, bool Failed = false, string? FailureCode = null);

public interface IWatchdogJob
{
    string Name { get; }                                         // "detection", "attribution", … (logs, health codes)
    TimeSpan DefaultInterval { get; }
    IReadOnlyList<WatchdogSignal> WakeSignals { get; }           // signals that end the wait early
    /// <summary>One iteration; never throws for expected failures (returns Failed). Unexpected exceptions are logged by
    /// the host and retried with backoff (1 s … 5 min).</summary>
    Task<JobResult> RunOnceAsync(CancellationToken cancellationToken);
}

public enum WatchdogSignal { PollRequested, EventsStored, AttributionUpdated, NotificationsQueued, OutboxEnqueued, ConfigChanged, DigestRequested }

public interface IWatchdogSignals
{
    /// <summary>Sets the latch of every subscription that includes the signal.</summary>
    void Raise(WatchdogSignal signal);
    /// <summary>One subscription per consumer (JobHost subscribes once per job with its WakeSignals). Each subscription
    /// has its own latch, so overlapping consumers never steal each other's wake-ups.</summary>
    ISignalSubscription Subscribe(IReadOnlyList<WatchdogSignal> signals);
}

public interface ISignalSubscription : IDisposable
{
    /// <summary>Completes true when one of the subscribed signals was raised since the previous WaitAsync of this
    /// subscription returned (the latch is reset on return), false on timeout. The timeout runs on the injected
    /// TimeProvider (<c>Task.Delay(timeout, timeProvider, ct)</c>).</summary>
    Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IWatchdogConfigProvider
{
    /// <summary>Effective configuration (defaults merged, tokens NOT expanded). Never null after startup.</summary>
    ConfigEnvelope Current { get; }
    Task<ConfigEnvelope> ReloadAsync(CancellationToken cancellationToken);
    event EventHandler<ConfigEnvelope>? Changed;
}

public interface IHealthRegistry
{
    void Report(HealthCheckDto check);
    void ReportChannel(ChannelHealthDto channel);
    IReadOnlyList<HealthCheckDto> Checks { get; }
    IReadOnlyList<ChannelHealthDto> Channels { get; }
    DirectoryStatusDto Directory { get; }
    void ReportDirectory(DirectoryStatusDto status);
    AttributionStatusDto Attribution { get; }
    void ReportAttribution(AttributionStatusDto status);
}

public sealed record ConfigValidationContext
{
    /// <summary>Stored secret names → binding (built by the Service from ISecretStore.ListAsync).</summary>
    public IReadOnlyDictionary<string, string> StoredSecrets { get; init; } = new Dictionary<string, string>();
    public bool IsDevelopment { get; init; }
    /// <summary>Bootstrap Authorization.AllowTier0Suppression (8.3 tier0Suppression).</summary>
    public bool AllowTier0Suppression { get; init; }
    /// <summary>Bootstrap Notifications.OAuthSmtpHosts (XOAUTH2 destinations).</summary>
    public IReadOnlyList<string> OAuthSmtpHosts { get; init; } = ["smtp.office365.com"];
}

public interface IConfigurationValidator
{
    ConfigValidationResult Validate(WatchdogConfiguration configuration, ConfigValidationContext context);
}

public interface IDefaultConfigurationFactory
{
    WatchdogConfiguration Create();
}

public interface IRulePreviewService
{
    Task<RulePreviewResult> PreviewAsync(RulePreviewRequest request, CancellationToken cancellationToken);
}

public interface IDigestService
{
    Task<DigestPreviewDto> PreviewAsync(CancellationToken cancellationToken);
    /// <summary>Builds and enqueues the digest for the period since the last digest now (does not move the schedule).</summary>
    Task SendNowAsync(CancellationToken cancellationToken);
    DateTimeOffset? NextScheduledAt { get; }
}

public interface IEventHtmlRenderer
{
    /// <summary>Full HTML page of one event (same layout as the alert e-mail), language from configuration.</summary>
    string Render(EventDetails details);
}
```

`AddWatchdogCore(this IServiceCollection services)` (W-core) registers: `TimeProvider.System` (TryAdd), every
`IWatchdogJob` implementation (`ChangeDetectionEngine`, `AttributionEngine`, `ScheduledScanJob`, `RuleEvaluationJob`,
`NotificationDispatchJob`, `OutboxSenderJob`, `DigestJob`, `RetentionJob`, `HeartbeatJob`) as singletons and as `IWatchdogJob`
(`DigestJob` also as `IDigestService`), `IWatchdogSignals`, `IHealthRegistry`, `IWatchdogConfigProvider`,
`IConfigurationValidator`, `IDefaultConfigurationFactory`, `IRulePreviewService`, `IEventHtmlRenderer`, the formatters,
`RuleEngine`, `SecurityEventParser`, `ConfigChangeAuditor`, `ConfigRedactor`, etc. It does NOT register stores,
transports, `IDirectorySessionProvider`, `ISecurityEventSource`, `IDeliveryTester` (Service). Integration and service
tests build engines only through `AddWatchdogCore()` and the interfaces – never through constructors – so they compile
against the scaffold shells before W-core is merged.

---

## 7. Event catalog (defaults)

"Rec." = recorded by default (else only when listed in `Directory.EnabledOptionalKinds`). Severity is the catalog
default before tag bumps and overrides (section 12.2). "Rule" = built-in rule that notifies (section 12.4); "digest" =
only in the daily digest by default. During **rollout mode** (12.5) only tier0-*, bursts and health notify immediately.
Webhook `type` = `WebhookEventTypes.For(kind, objectKind)` (section 14.5).

| Kind | Cat. | Default severity | Rec. | Detected from | Default delivery |
|---|---|---|---|---|---|
| UserCreated | Lifecycle | Low | yes | LDAP: new GUID, uSNCreated > W; emitted after the creation settle window with the final state (10.6.3) | new-accounts: immediate (business hours, aggregated) + digest |
| ComputerCreated | Lifecycle | Info (Medium with tags createdByNonAdmin / unexpectedContainer / server; `defaultContainer` is only a digest flag) | yes | LDAP (settle window) | digest; computers-unusual: immediate |
| GroupCreated | Lifecycle | Info | yes | LDAP (settle window) | digest |
| OrganizationalUnitCreated | Lifecycle | Info | yes | LDAP | digest |
| ContactCreated | Lifecycle | Info | **no** | LDAP | digest |
| ServiceAccountCreated | Lifecycle | Medium (High for msDS-DelegatedManagedServiceAccount) | yes | LDAP (MSA/gMSA/dMSA class) | new-accounts; dMSA also tier0-security |
| ObjectDeleted | Lifecycle | User/Group/ServiceAccount Medium, Computer Low, Contact Info, OU High, GroupPolicy/Trust/PasswordSettings Medium | yes | Show Deleted pass restricted to the watched classes (10.9) or reconciliation | deletions: immediate + digest |
| ObjectRestored | Lifecycle | Medium | yes | GUID known as deleted re-appears live (memberships re-read, 10.9) | deletions |
| ObjectMoved | Lifecycle | Info (High with tag disabledOu when moved **out of** a disabled-accounts subtree) | yes | DN parent changed | digest |
| ObjectRenamed | Lifecycle | Info | yes | RDN changed | digest |
| ObjectAppeared | Lifecycle | Info | yes | GUID new but uSNCreated ≤ W (moved into scope – tag outOfScope – or became readable) | digest |
| ObjectDisappeared | Lifecycle | Low | yes | reconciliation: GUID gone, not found as tombstone | deletions |
| AccountDisabled | Account | Info | yes | UAC bit 0x2 set | account-status digest; privileged-accounts immediate |
| AccountEnabled | Account | Low (High with longDisabled / disabledOu) | yes | UAC bit 0x2 cleared | account-status |
| AccountLockedOut | Account | Low | yes | lockoutTime 0→t or t1→t2>t1; sources from 4740/4771/4776/4625 | lockouts: immediate to helpdesk (business hours, aggregated, 1 h repeat suppression per account) |
| AccountUnlocked | Account | Info | yes | lockoutTime t→0 **before** t + effective lockoutDuration, or matched 4767 (a reset at the first logon after expiry is not an unlock – 10.6) | digest |
| LockoutExpired | Account | Info | **no** | LockoutMonitor: computed lockout bit cleared, lockoutTime unchanged | digest |
| PasswordChanged | Account | Info (attribution-dependent bump, 12.2) | yes | pwdLastSet changed to > 0 together with a unicodePwd write (users; computers only with TrackComputerPasswordChanges; MSA/gMSA/dMSA only with TrackManagedPasswordRollover); tags passwordReset (4724) / selfChange (4723) added by attribution | digest (resets and changes counted separately); privileged-accounts immediate for resets/unattributed |
| PasswordAgeReset | Account | Medium (High for privileged) | yes | pwdLastSet moved forward while the unicodePwd metadata did not change (pwdLastSet 0 → -1 trick; needs metadata) | account-flags: immediate |
| PasswordMustChangeSet | Account | Info | yes | pwdLastSet → 0 | digest |
| AccountFlagsChanged | Account | per flag (section 10.6.1) – Medium for DONT_EXPIRE_PASSWORD, High for PASSWD_NOTREQD / DONT_REQ_PREAUTH / ENCRYPTED_TEXT_PWD_ALLOWED / USE_DES_KEY_ONLY, Low otherwise; also emitted for risky bits of a new account's final state | yes | UAC bits other than 0x2 and the delegation bits | account-flags: immediate (Medium+) |
| AccountExpirationChanged | Account | Info (Medium when cleared to "never") | yes | accountExpires | digest |
| AccountNameChanged | Account | Medium | yes | sAMAccountName / userPrincipalName | account-names: immediate |
| AttributesChanged | Account | Info (Medium for the logon-settings attributes) | yes | any other watched attribute (one event lists all) | digest; logon-settings: immediate for scriptPath, profilePath, homeDirectory, homeDrive, userWorkstations, logonHours |
| GroupMemberAdded | Membership | Info | yes | member set diff (per member) | digest; watchlist-groups immediate |
| GroupMemberRemoved | Membership | Info (High when the group is in ProtectRemovalSids) | yes | member set diff | digest |
| GroupTypeChanged | Membership | Medium | yes | groupType | group-changes digest |
| PrivilegeGranted | Membership | Critical (tier Critical) / High (tier High) | yes | PrivilegeEvaluator diff (transitive incl. primary group; tag restored after a restore) | tier0-privilege: immediate, always |
| PrivilegeRevoked | Membership | Medium | yes | PrivilegeEvaluator diff | tier0-privilege |
| AdminCountSet | Security | High; **Info** when the principal is in PrivilegeState or the actor is SYSTEM (SDProp) – attribution-dependent | yes | adminCount absent/0 → 1 | tier0-security (High only) |
| SidHistoryChanged | Security | Critical | yes | sIDHistory | tier0-security |
| DelegationChanged | Security | High (Critical: TRUSTED_FOR_DELEGATION on a non-DC) | yes | UAC 0x80000/0x1000000, msDS-AllowedToDelegateTo, msDS-AllowedToActOnBehalfOfOtherIdentity | tier0-security |
| ServicePrincipalNameAdded | Security | High | yes | servicePrincipalName value added on a user / inetOrgPerson | tier0-security |
| KeyCredentialChanged | Security | pattern-dependent (10.6.4): computer 0 → 1 value, not DC/privileged → Low; computer that already had a value, DC or privileged → High; user privileged → High; user otherwise Medium, High when the resolved actor is neither the user nor a sync account | yes | msDS-KeyCredentialLink (count + hash of the binary part only) | tier0-security (High+), security-review digest |
| PrimaryGroupChanged | Security | High | yes | primaryGroupID → value not 513/514 (users), 515 (computers), 516/521 (DCs) | tier0-security |
| SecurityDescriptorChanged | Security | Critical | yes | owner/ACE summary of CriticalObjects changed (hash only as pre-check) | tier0-security |
| ComputerNameSpoofing | Security | Critical | yes | computer sAMAccountName without trailing `$` or equal to a DC name + `$`-less | tier0-security |
| ManagedAccountLinkChanged | Security | High (Critical when the linked/superseded account is privileged) | yes | msDS-ManagedAccountPrecededByLink / msDS-DelegatedMSAState on a dMSA, msDS-SupersededManagedAccountLink / msDS-SupersededServiceAccountState on a user or computer | tier0-security |
| CertificateMappingChanged | Security | High (Critical for privileged) | yes | altSecurityIdentities | tier0-security |
| ManagedPasswordReadersChanged | Security | High | yes | msDS-GroupMSAMembership (ACE added/removed summary) | tier0-security |
| DomainPolicyChanged | Domain | High | yes | domain head: lockout/password policy, ms-DS-MachineAccountQuota; forest: Recycle Bin (msDS-EnabledFeature), dSHeuristics, tombstoneLifetime | tier0-domain |
| PasswordSettingsChanged | Domain | High | yes | msDS-PasswordSettings created/changed/deleted, msDS-PSOAppliesTo | tier0-domain |
| GroupPolicyLinkChanged | Domain | High on domain head / Domain Controllers OU, else Medium | yes | gPLink / gPOptions | tier0-domain (High+), ou-gpo digest |
| GroupPolicyChanged | Domain | Info (Medium for Default Domain / Default Domain Controllers Policy) | yes | groupPolicyContainer versionNumber/flags/displayName, created/deleted | ou-gpo digest |
| TrustChanged | Domain | Critical | yes | trustedDomain created/changed/deleted | tier0-domain |
| DomainControllerAdded | Domain | Critical (High with tag otherDomain) | yes | new nTDSDSA in the Configuration NC or SERVER_TRUST_ACCOUNT set on a computer | tier0-domain |
| DomainControllerRemoved | Domain | High (Medium with tag otherDomain) | yes | known nTDSDSA no longer present (DomainControllerDirectory refresh) | tier0-domain |
| FsmoRoleChanged | Domain | High | yes | fSMORoleOwner of the domain head, RID Manager$, Infrastructure, Partitions or the schema head | tier0-domain |
| CertificateTemplateChanged | Domain | High | yes | pKICertificateTemplate (flags, EKUs, RA signatures, ACL), pKIEnrollmentService (published templates), certificationAuthority / NTAuthCertificates (cACertificate) | tier0-domain |
| SchemaChanged | Domain | High | yes | attributeSchema/classSchema created/changed (one event per cycle with counts and ≤ 50 names) | tier0-domain |
| AccountExpiring | Expiry | Info | yes | AccountExpiryScanner thresholds 14/7/1 calendar days (business-day adjusted) | expiry: helpdesk in business hours (aggregated) + digest |
| AccountExpired | Expiry | Info | yes | scanner: accountExpires passed (once) | expiry |
| PasswordExpiring | Expiry | Info (High with privileged/serviceAccount at ≤ 7 days) | yes | PasswordExpiryScanner | expiry digest; privileged-accounts immediate |
| MassLockout | Security | Medium | yes | ≥ max(5, 0.5 % of enabled users) distinct accounts locked within 10 min | bursts (Default hours: business hours for Medium) |
| MassDeletion | Security | Critical (users/groups/OUs/service accounts ≥ 10 in 10 min); High (computers/contacts ≥ 50 in 10 min) | yes | ObjectDeleted burst by object class (by one actor when known) | bursts: immediate |
| PasswordSprayingSuspected | Security | High | **no** | one source IP/workstation with failures for ≥ 20 distinct accounts in 10 min (4771 0x18/0x6, 4776 0xC000006A/0xC0000064 on the PDC; needs failure auditing) | bursts: immediate, always |
| SecurityLogCleared | AuditLog | Critical | yes | 1102 (and 1104 log full → Health) | tier0-eventlog |
| AuditPolicyChanged | AuditLog | Critical | yes | 4719 | tier0-eventlog |
| DsrmPasswordChanged | AuditLog | High | yes | 4794 | tier0-eventlog |
| HealthDirectoryUnavailable | Health | High | always | session provider failures > FailoverAfter | health |
| HealthResync | Health | Medium | always | resync (never-seen DC, invocationId change, USN rollback) | health |
| HealthPermissionGap | Health | High | always | PermissionSelfCheck | health |
| HealthAttributionDegraded | Health | Medium | always | DC log unreadable, coverage < 50 % over 24 h (NotApplicable and lockout-reset events excluded) | health |
| HealthEventLogGap | Health | High | always | cursor regression, overwritten records, 1102/1104, ingestion budget exceeded ("flooding") | health |
| HealthDeliveryFailing | Health | High | always | channel broken / dead letters | health (sent through every other working channel; e-mail falls back to the digest recipients) |
| HealthStorage | Health | High | always | free space < 1 GiB or DB ≥ 80 % of `Retention.MaxDatabaseMb` | health |
| HealthBaselineCompleted | Health | Info | always | first baseline committed | digest |
| WatchdogConfigurationChanged | Health | Medium; High for recipients/channels/SMTP/attribution/exclusion changes; Critical for disabling or weakening tier0-* / health delivery, IgnoredAttributes, DisabledKinds, ExcludedSubtrees or a Tier-0 detection source (ConfigChangeAuditor, 25.6) | always | PUT /config saved | health – evaluated with the **previous** configuration as well (17.2) |
| Test | Health | Info | always | test sends only (never stored) | – |

---

## 8. Configuration

Two layers:

1. **Bootstrap settings** (`appsettings.json` next to the exe, overridable by
   `%ProgramData%\AdMng\Watchdog\config\appsettings.json` and environment variables `ADMNG_WATCHDOG__…`): where the
   data folder is, how the API listens and authenticates, which AD groups have which role, how to reach AD. Editable
   only by local administrators; **never** through the API (otherwise an API admin could grant himself more rights).
   The `config\` folder grants the gMSA **Read** only (the data folder is a sibling, `data\`); `BootstrapFileGuard`
   refuses to start (Event Log error, exit code 6) when the service identity can write the bootstrap file or folder.
2. **Runtime configuration** (`WatchdogConfiguration`): rules, channels, SMTP, digest, schedules. Stored in SQLite
   (`config` table, versioned, audited), edited through the API / GUI. On first start the service saves
   `IDefaultConfigurationFactory.Create()` with `SaveAsync(defaults, expectedVersion: 0, by: <service account>,
   summary: "initial", [])` → version 1. Built-in rules added by a newer service version (8.2) are merged **in memory**
   by `WatchdogConfigProvider` and persisted only with the next admin PUT (no service-initiated config writes).
   After a successful PUT, `ConfigEndpoints` calls `IWatchdogConfigProvider.ReloadAsync` and then raises `ConfigChanged`.

### 8.1 Runtime configuration – default document (JSON as returned by `GET /api/v1/config`)

```json
{
  "version": 1,
  "configuration": {
    "schemaVersion": 1,
    "general": { "instanceName": "AD Watchdog", "language": "pl", "timeZone": "Europe/Warsaw", "publicBaseUrl": null,
                 "rolloutDays": 7, "rolloutEnded": false },
    "directory": {
      "pollInterval": "00:00:15", "fullReconciliationTime": "02:30", "usnOverlap": 0,
      "trackDeletedObjects": true, "membershipTracking": "AllGroups",
      "watchConfigurationPartition": true, "watchSchemaPartition": true,
      "wakeUpContainers": [], "excludedSubtrees": [],
      "criticalObjects": ["{domain}", "CN=AdminSDHolder,CN=System,{domain}", "OU=Domain Controllers,{domain}"],
      "serviceAccountSubtrees": [], "disabledAccountsSubtrees": [],
      "additionalWatchedAttributes": [], "ignoredAttributes": [],
      "enabledOptionalKinds": [], "disabledKinds": [],
      "trackComputerPasswordChanges": false, "trackManagedPasswordRollover": false, "trackComputerOsChanges": false,
      "maxValuesPerAttribute": 100, "failoverAfter": "00:10:00", "creationSettleWindow": "00:02:00"
    },
    "attribution": {
      "enabled": true, "source": "DomainControllers", "collector": null, "domainControllers": [],
      "pollInterval": "00:00:30", "matchWindow": "00:00:03", "fallbackWindow": "00:02:00",
      "replicationLatencyBound": "00:15:00",
      "pendingRetries": ["00:00:30", "00:02:00", "00:10:00", "00:30:00"],
      "lookupLogonSource": true, "lookupLockoutSources": true, "logonLookback": "12:00:00",
      "sourceQueryTimeout": "00:00:10", "maxRecordsPerPoll": 50000,
      "resolveReverseDns": true, "suppressComputerSelfChanges": true
    },
    "scans": {
      "accountExpiry": { "enabled": true, "thresholdsDays": [14, 7, 1], "includeDisabled": false, "interval": "01:00:00",
                         "fireOnBusinessDays": true },
      "passwordExpiry": { "scope": "PrivilegedAndService", "thresholdsDays": [14, 7, 1], "interval": "01:00:00" },
      "lockoutRecheckInterval": "00:05:00",
      "inactiveAccounts": { "enabled": true, "days": 90, "privilegedDays": 180, "minAgeDays": 30, "reportDay": "Monday" },
      "massLockout": { "threshold": 5, "window": "00:10:00", "percentOfEnabledUsers": 0.5 },
      "massDeletion": { "threshold": 10, "window": "00:10:00", "percentOfEnabledUsers": 0 },
      "massComputerDeletion": { "threshold": 50, "window": "00:10:00", "percentOfEnabledUsers": 0 },
      "passwordSpray": { "enabled": false, "distinctAccounts": 20, "window": "00:10:00" },
      "permissionSelfCheckInterval": "1.00:00:00"
    },
    "privilegedGroups": {
      "criticalSids": ["S-1-5-32-544", "{domainSid}-512", "{domainSid}-516", "{rootDomainSid}-518",
                       "{rootDomainSid}-519", "{domainSid}-526", "{rootDomainSid}-527"],
      "highSids": ["S-1-5-32-548", "S-1-5-32-549", "S-1-5-32-550", "S-1-5-32-551", "S-1-5-32-552",
                   "{domainSid}-520", "{domainSid}-517", "{domainSid}-521"],
      "highSamAccountNames": ["DnsAdmins"],
      "additionalCriticalDns": [], "additionalHighDns": [],
      "protectRemovalSids": ["{domainSid}-525"], "watchListDns": []
    },
    "businessHours": {
      "days": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
      "start": "07:00", "end": "17:00", "holidayCalendar": "PL", "extraHolidays": []
    },
    "notifications": {
      "subjectPrefix": "[AD Watchdog]", "attributionGrace": "00:01:30", "aggregationWindow": "00:05:00",
      "highSeverityFlushDelay": "00:00:10", "maxItemsPerMessage": 50, "maxMessagesPerRulePerHour": 10,
      "followUpWhenAttributed": true, "maxValueLength": 200
    },
    "smtp": {
      "enabled": false, "host": null, "port": 587, "security": "StartTls", "authentication": "None",
      "userName": null, "password": { "isSet": false, "hint": null },
      "oAuth": { "cloud": "Public", "tenantId": null, "clientId": null, "certificateThumbprint": null,
                 "clientSecret": { "isSet": false, "hint": null } },
      "from": null, "replyTo": null, "localDomain": null,
      "certificateValidation": "System", "pinnedSha256": null, "checkRevocation": true,
      "maxMessagesPerMinute": 20, "maxRecipientsPerMessage": 50, "timeout": "00:01:00"
    },
    "channels": [],
    "recipientGroups": [
      { "id": "it-admins", "name": "Administratorzy IT", "addresses": [], "detailLevel": "Full" },
      { "id": "helpdesk", "name": "Helpdesk", "addresses": [], "detailLevel": "Standard" }
    ],
    "digest": {
      "enabled": true, "time": "07:30",
      "days": ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"],
      "recipientGroupIds": ["it-admins"], "channelIds": [], "sendWhenEmpty": true,
      "maxRowsPerSection": 50, "weeklySectionsDay": "Monday", "upcomingExpiryDays": 14, "coverNonBusinessDays": true
    },
    "rules": [ "… built-in rules of section 12.4 …" ],
    "retention": {
      "eventsDays": 365, "securityEventsDays": 30, "outboxSentDays": 30, "outboxDeadDays": 90,
      "deletedSnapshotDays": 180, "configAuditDays": 1095, "backupsToKeep": 7, "preMigrationBackupDays": 30,
      "maxDatabaseMb": 10240, "maintenanceTime": "03:15"
    }
  },
  "updatedAt": "2026-10-14T08:00:00Z",
  "updatedBy": "CONTOSO\\svc-admngwd$"
}
```

Example channel entries (what an admin adds in the GUI):

```json
{ "id": "teams-it", "type": "Teams", "name": "Teams – Administratorzy IT", "enabled": true,
  "url": { "isSet": true, "hint": "https://default1a2b….12.environment.api.powerplatform.com/powerautomate/automations/direct/…" },
  "detailLevel": "Standard",
  "teams": { "authMode": "Sas", "cloud": "Public", "tenantId": null, "clientId": null, "certificateThumbprint": null,
             "clientSecret": { "isSet": false }, "scope": "https://service.flow.microsoft.com//.default", "cardVersion": "1.4" },
  "webhook": null, "rateLimit": { "perSecond": 1, "perMinute": 20, "perHour": 100 } }

{ "id": "siem", "type": "Webhook", "name": "SIEM", "enabled": true,
  "url": { "isSet": true, "hint": "https://siem.contoso.com/hooks/…" }, "detailLevel": "Minimal",
  "teams": null,
  "webhook": { "signingSecret": { "isSet": true, "hint": "whsec_…Xw==" }, "previousSigningSecret": { "isSet": false },
               "sendGitHubSignature": false, "allowedHosts": ["siem.contoso.com"], "timeout": "00:00:30", "includeDigest": true,
               "heartbeatInterval": "00:15:00" },
  "rateLimit": { "perSecond": 10, "perMinute": 300, "perHour": 5000 } }
```

Tokens in DNs/SIDs: `{domain}` = default naming context, `{domainSid}` = domain SID, `{rootDomainSid}` = forest root
domain SID (read once per session from the root domain head's `objectSid`; when unreadable, `{rootDomainSid}` groups
are matched by RID 518/519/527 within any domain SID). Expansion: `ConfigTokens` (Core) – runtime only, the stored
document keeps the tokens.

### 8.2 Merge rules

- Missing properties in a stored document take the record defaults (System.Text.Json + `init` defaults).
- Built-in rules: the stored document is authoritative. `DefaultRules` adds built-in rules whose `Id` is missing (new
  version of the service) with `Enabled` as shipped, **in memory only** (persisted with the next admin PUT), and never
  overwrites existing ones. "Reset to default" in the GUI replaces one rule with `GET /config/defaults` content.
- `Locked` rules (`health`) cannot be disabled or deleted (validation error).
- A redacted envelope (`IsRedacted = true`) is never accepted by PUT (400 `redactedConfig`).

### 8.3 Validation (`ConfigurationValidator`, Core) – codes and rules

`Validate(configuration, ConfigValidationContext)`; the Service builds the context (stored secret bindings,
environment, bootstrap flags). Errors (save rejected with 400 + `issues`):

| Code | Rule |
|---|---|
| `range` | `pollInterval` 5 s – 5 min; `attribution.pollInterval` 10 s – 10 min; `matchWindow` 1 – 30 s; `fallbackWindow` 30 s – 30 min; `logonLookback` 1 – 72 h; `sourceQueryTimeout` 2 – 60 s; `maxRecordsPerPoll` 1 000 – 500 000; `creationSettleWindow` 0 – 10 min; `aggregationWindow` 0 – 60 min; `attributionGrace` 0 – 10 min; `maxItemsPerMessage` 1 – 500; `maxMessagesPerRulePerHour` 1 – 1000; `maxValuesPerAttribute` 1 – 1000; thresholds 0 – 365 (distinct); retention days 1 – 3650 (`eventsDays` ≥ 7); `maxDatabaseMb` ≥ 512; `inactiveAccounts.days` ≥ 30; `smtp.port` 1 – 65535; `maxRecipientsPerMessage` 1 – 500; rate limits ≥ 1; `heartbeatInterval` ≥ 5 min; `rolloutDays` 0 – 60; `repeatSuppression` 0 – 24 h |
| `required` | `smtp.host`, `smtp.from` when `smtp.enabled`; channel `url` set (stored or NewValue) when enabled; webhook `signingSecret` set; OAuth (SMTP, Teams): tenantId, clientId (GUIDs) and (certificateThumbprint or clientSecret) |
| `secretRebindRequired` | a stored secret is used by settings whose destination binding (section 18, `SecretBindings`) differs from the binding it was stored with, and no `NewValue` is supplied (e.g. `smtp.host` changed while keeping the stored password) – also enforced for test sends (17.2) |
| `duplicateId` | rule ids, channel ids, recipient group ids, exclusion ids unique (case-insensitive) |
| `invalidId` | ids `^[a-z0-9][a-z0-9-]{0,59}$` (channels/groups ≤ 40); channel id `email` reserved |
| `unknownReference` | rule/digest `recipientGroupIds`, `channelIds` must exist |
| `invalidDn` | every DN parses with `DistinguishedName.TryParse` after token expansion with placeholder values |
| `invalidSid` | SIDs match `^S-1-\d+(-\d+)+$` after token expansion – includes `ActorFilter.ExcludeActors` and `RuleExclusion.Actor` (SIDs only) |
| `invalidAttribute` | `additionalWatchedAttributes` / `ignoredAttributes` / rule `attributes`: `^[A-Za-z][A-Za-z0-9-]{0,63}$` (no `*`, `+`, `1.1`, `;options`), ≤ 20 additional attributes, not secret (`SecretAttributes.IsSecret`), not a noise attribute (9.3). The Service additionally validates `additionalWatchedAttributes` against the live session schema on PUT (exists, not confidential, not constructed, not a back link); without a session the change is rejected (`schemaUnavailable`) |
| `tier0Suppression` | `ignoredAttributes` contains a Tier-0 attribute or `disabledKinds` a Tier-0 kind (`Tier0Catalog`: every attribute/kind of categories Security, Domain, AuditLog, PrivilegeGranted/Revoked), or `excludedSubtrees` contains/equals a `criticalObjects` entry, a `privilegedGroups.additionalCriticalDns`/`additionalHighDns` group, `{domain}`, `OU=Domain Controllers,{domain}`, `CN=System,{domain}`, `CN=Users,{domain}` (Domain/Enterprise/Schema Admins, krbtgt, RID 500) or `CN=Builtin,{domain}` (Administrators and the operator groups) – allowed only when the bootstrap flag `Authorization.AllowTier0Suppression` is true. The service (`ConfigValidationService`, PUT and validate) also rejects a newly added `excludedSubtrees` entry that equals or contains a privileged object of the snapshot – a privileged root group (critical/high SIDs, the watchdog's role and host groups), a group on a path to one, a principal of the current privilege state, krbtgt or the built-in Administrator – so privileged groups and admin accounts moved into tiered OUs stay protected; entries already stored are not re-checked |
| `invalidAddress` | every e-mail address parses (`MailboxAddress.TryParse` semantics: implemented in Core with `System.Net.Mail.MailAddress` parsing rules + no CR/LF); `from` likewise |
| `invalidHostName` | `smtp.localDomain` and `smtp.host` match `^[A-Za-z0-9.-]{1,253}$` (no CR/LF, no spaces); `attribution.collector` and every `attribution.domainControllers` entry likewise and are not an IP literal (`HostNames.IsHostName`) – the service identity authenticates to them with Kerberos only |
| `insecureCredentials` | `smtp.authentication` ≠ None requires `security` StartTls or SslOnConnect |
| `oauthHostNotAllowed` | `smtp.authentication = OAuth2ClientCredentials` and `smtp.host` is not in the bootstrap `Notifications.OAuthSmtpHosts` (default `smtp.office365.com`) |
| `invalidScope` | Teams `scope` not one of the documented Power Automate scopes of the selected cloud |
| `insecureUrl` | channel URLs must be `https://` (Development builds may allow `http://localhost`/`127.0.0.1` when `context.IsDevelopment`) |
| `retiredTeamsUrl` | Teams URL host ends with `.webhook.office.com` or `.logic.azure.com` |
| `teamsUrlFormat` | Teams URL must match `^https://[^/]+\.environment\.api\.powerplatform\.com(:443)?/powerautomate/automations/direct/(.*/)?workflows/` – an error for `EntraClientCredentials` (the bearer token is sent only to Power Platform hosts), a warning for SAS with a custom host |
| `hostNotAllowed` | webhook URL host not in `allowedHosts` (when non-empty) |
| `lockedRule` | a `Locked` rule disabled, deleted or its kinds changed; for the shipped locked rule (`health`) also a weakened delivery: `immediate`, `includeInDigest` or `allEnabledChannels` turned off, `hours` narrower than shipped (Always), no recipient group and no channel left, `repeatSuppression` > 0, `maxMessagesPerHour` below `notifications.maxMessagesPerRulePerHour`, `aggregationWindow` above `notifications.aggregationWindow`, or a `severity` override below Critical (re-targeting and strengthening stay allowed) |
| `acceptAnyNotAllowed` | `certificateValidation = AcceptAnyDevelopmentOnly` outside Development |
| `pinnedHash` | `pinnedSha256` must be 64 hex chars when mode PinnedSha256 |
| `timeZone` | `general.timeZone` resolvable by `TimeZoneResolver` (as given; IANA ↔ Windows conversion; built-in map for NLS hosts: Europe/Warsaw ↔ Central European Standard Time, Europe/Berlin ↔ W. Europe Standard Time, Europe/London ↔ GMT Standard Time, UTC ↔ UTC) |
| `language` | `pl` or `en` |
| `exclusionReason` | every exclusion needs a non-empty reason (≤ 500) and `createdBy` |
| `tooManyWakeUpContainers` | > 4 |
| `secretWithoutChannel` | `SecretField.NewValue` and `Clear` both set |
| `redactedConfig` | PUT of an envelope with `IsRedacted = true` |

Warnings (save allowed):

| Code | Rule |
|---|---|
| `noRecipients` | a rule with `immediate` true whose recipient groups have no addresses and no channels |
| `smtpDisabledButUsed` | rules/digest reference recipient groups while `smtp.enabled` is false |
| `exchangeOnlineBasic` | `authentication = Basic` and host is `smtp.office365.com` / `smtp-legacy.office365.com` (Basic SMTP AUTH is disabled by default from end of December 2026) |
| `noTls` | `security = None` or `StartTlsWhenAvailable` (any port, also 25 – a STARTTLS-stripping attacker could read or alter alerts) |
| `revocationOff` | `checkRevocation = false` |
| `teamsUrlFormat` | non-standard Teams host (SAS mode) |
| `tier0Exclusion` | an exclusion on a tier0-* rule (shown prominently in the GUI and listed in the digest) |
| `inactiveLow` | `inactiveAccounts.days` < 45 |
| `pollFast` | `pollInterval` < 10 s |
| `rolloutOff` | `rolloutDays = 0` on a configuration whose baseline is younger than 7 days |

### 8.4 Bootstrap settings (`appsettings.json`) – `ServiceOptions` (Service)

```json
{
  "Watchdog": {
    "DataDirectory": "%ProgramData%\\AdMng\\Watchdog\\data",
    "AllowInsecureDataDirectoryAcl": false,
    "Authentication": "Negotiate",
    "Hosting": {
      "Server": "Kestrel",
      "Urls": "https://admng-wd.contoso.com:8443",
      "Certificate": { "Thumbprint": null, "Subject": "admng-wd.contoso.com", "Store": "My", "Location": "LocalMachine" },
      "RequireKerberos": true,
      "AllowedRemoteSubnets": [],
      "MonitoringSubnets": []
    },
    "Authorization": {
      "ViewerSids": [ "S-1-5-21-…-1301" ],
      "AdminSids": [ "S-1-5-21-…-1302" ],
      "DeniedSids": [],
      "WatchdogHostGroupSids": [ "S-1-5-21-…-1303" ],
      "ViewerDetailLevel": "Standard",
      "AllowTier0Suppression": false
    },
    "Notifications": { "OAuthSmtpHosts": [ "smtp.office365.com" ] },
    "Directory": {
      "Domain": null,
      "Server": null,
      "PreferPdc": true,
      "Authentication": "CurrentUser",
      "Security": "SignAndSeal",
      "Port": null,
      "LabSimpleBindUser": null,
      "AllowInsecureLab": false
    },
    "Development": { "Sids": [], "Account": "LAB\\developer" }
  },
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" },
               "EventLog": { "SourceName": "AdMng Watchdog", "LogLevel": { "Default": "Warning" } } }
}
```

| Setting | Meaning |
|---|---|
| `DataDirectory` | environment variables expanded; default `%ProgramData%\AdMng\Watchdog\data`. Created on start with a **protected DACL** (SYSTEM F, Administrators F, service identity M, owner Administrators) when missing (also `keys\`, `backup\`). On every start (Windows) the DACL of the folder and of `watchdog.db`, `-wal`, `-shm` is checked against that allow-list for **any** access mask; a deviation is an Event Log error and exit code 5 unless `AllowInsecureDataDirectoryAcl` (local-admin escape hatch, health Warning `storage.acl`) |
| `Authentication` | `Negotiate` (production), `Development` (only when `ASPNETCORE_ENVIRONMENT=Development`, **never** when `WindowsServiceHelpers.IsWindowsService()` and never when any listener is not loopback; identity from `Development`), `Test` is registered only by tests |
| `Hosting.Server` | `Kestrel` (default) or `HttpSys` (Windows only; `netsh http add urlacl/sslcert` required, see docs) |
| `Hosting.Certificate` | Kestrel: `Thumbprint` wins over `Subject`; certificate must have serverAuth EKU and an accessible private key |
| `Hosting.RequireKerberos` | reject every non-Kerberos identity (default true; `KerberosOnlyMiddleware` for both servers; .NET 10 Kestrel has no channel binding) |
| `Hosting.AllowedRemoteSubnets` | optional CIDR allow-list checked on `Connection.RemoteIpAddress` only (never forwarded headers; no reverse proxy / TLS inspection in front of the API – documented) |
| `Hosting.MonitoringSubnets` | CIDRs allowed to call `/healthz` anonymously (in addition to loopback and the machine's own addresses); `/healthz` returns only `ok` / 503 |
| `Authorization.*Sids` | AD group SIDs; Admin implies Viewer; empty lists = nobody (service still runs; GUI shows 403). `DeniedSids` (user or group SIDs) are checked first – emergency revocation without waiting for Kerberos ticket expiry |
| `Authorization.WatchdogHostGroupSids` | the gMSA host group (`PrincipalsAllowedToRetrieveManagedPassword`); together with `ViewerSids`/`AdminSids` these groups are treated as **High-tier privileged roots** by the PrivilegeEvaluator (membership changes alert), not removable through the API |
| `Authorization.ViewerDetailLevel` | detail level of API responses and the HTML event page for non-Admins (default Standard, 14.1) |
| `Authorization.AllowTier0Suppression` | permits the `tier0Suppression` configurations of 8.3 (default false) |
| `Notifications.OAuthSmtpHosts` | hosts that may receive an XOAUTH2 token (default `smtp.office365.com`) |
| `Directory.Domain/Server` | null = computer's domain / PDC emulator (`PreferPdc`) located via `IDirectoryConnector` |
| `Directory.Authentication` | `CurrentUser` (gMSA) in production; lab: `SimpleBind` with `LabSimpleBindUser`, password ONLY from env `ADMNG_WATCHDOG_LDAP_PASSWORD`, and `AllowInsecureLab=true` (maps to `ConnectionOptions.AllowInsecureSimpleBind`, refused outside Development) |

**Reload.** The bootstrap file is added with `reloadOnChange` (when `config\` exists; the gMSA cannot write it, 19.5).
`ServiceOptionsFactory` builds the `IOptionsMonitor<ServiceOptions>` value from the live configuration but pins what the
host consumed when it was built – `DataDirectory`, `AllowInsecureDataDirectoryAcl`, `Authentication`, `Hosting` (except
`AllowedRemoteSubnets`/`MonitoringSubnets`), `Directory`, `Development` – to the start-up values. So `Authorization`
(above all `DeniedSids`: revocation must not wait for a restart), `Notifications` and the two subnet lists apply within
seconds; everything else needs a service restart (documented). `DetectionHostOptions` (role groups as privileged roots)
is read once at start. An edit that cannot be loaded (malformed JSON) keeps the previous settings in effect – the file
provider throws in its change callback, so the options are not rebuilt – and `ConfigurationSetup.ReportReloadFailures`
logs warning 2002 (type and code only), so an emergency `DeniedSids` entry with a syntax error does not fail silently;
at the next start the same file fails with exit code 2 / event 1010. (The provider does clear its data on such a
failure: should another watched source – the `appsettings.json` next to the binary – change before the file is fixed,
the options are rebuilt without the bootstrap settings, i.e. without any role SIDs – everyone gets 403, fail closed.)
An edit that is valid JSON but holds a value the binder cannot convert (an unknown enum name such as
`"ViewerDetailLevel": "Minimalny"`, `"tak"` for a flag) would make every `CurrentValue` read throw – each request,
`/healthz`, the directory and notification jobs – so `ServiceOptionsFactory` keeps the last good options and logs the
same warning 2002 (type only: the binder's message carries the value); the corrected file applies as usual.

---

## 9. Snapshot model

### 9.1 Objects in scope

Default naming context, subtree, excluding `Directory.ExcludedSubtrees`, `CN=Deleted Objects`, `CN=LostAndFound`,
`CN=System` **except** the classes listed below that live there:

| WatchObjectKind | LDAP selection (in the increment filter OR-list) |
|---|---|
| User | `(&(objectCategory=person)(objectClass=user))` – includes inetOrgPerson |
| Computer | `(objectClass=computer)` without MSA classes; DCs get tag `domainController` (UAC 0x2000) |
| ServiceAccount | `(objectClass=msDS-ManagedServiceAccount)`, `(objectClass=msDS-GroupManagedServiceAccount)`, `(objectClass=msDS-DelegatedManagedServiceAccount)` |
| Group | `(objectClass=group)` |
| Contact | `(objectClass=contact)` |
| OrganizationalUnit | `(objectClass=organizationalUnit)` |
| Container | `(objectClass=container)` (DN only, for moves; no attributes) |
| Domain | the NC head `(objectClass=domainDNS)` (base) |
| GroupPolicy | `(objectClass=groupPolicyContainer)` under `CN=Policies,CN=System` |
| Trust | `(objectClass=trustedDomain)` under `CN=System` |
| PasswordSettings | `(objectClass=msDS-PasswordSettings)` under `CN=Password Settings Container,CN=System` (only if readable) |
| Configuration | FSMO holder objects of the domain NC: `CN=RID Manager$,CN=System,{domain}` (rIDManager), `CN=Infrastructure,{domain}` (infrastructureUpdate) – attribute `fSMORoleOwner` only |

Objects outside the default NC are watched by `ConfigurationPartitionWatcher` (10.11), each with the same watermark W
(USNs are per DC across all its naming contexts) and snapshotted (small sets):

| WatchObjectKind | Objects |
|---|---|
| DomainController | `nTDSDSA` objects under `CN=Sites,{config}` (forest-wide; `msDS-HasDomainNCs` tells the domain; objectCategory `nTDSDSARO` = RODC) |
| Configuration | `CN=Partitions,{config}` (`fSMORoleOwner`, `msDS-EnabledFeature`), `CN=Directory Service,CN=Windows NT,CN=Services,{config}` (`dSHeuristics`, `tombstoneLifetime`), the schema NC head (`fSMORoleOwner`) |
| PublicKeyInfrastructure | under `CN=Public Key Services,CN=Services,{config}`: `pKICertificateTemplate`, `pKIEnrollmentService`, `certificationAuthority` (incl. `CN=NTAuthCertificates`) |
| Schema | `attributeSchema`/`classSchema` – increments only, not snapshotted (SchemaChanged) |

The Reconciler (10.9) only considers snapshot rows whose DN is under the default naming context; rows of these kinds
are reconciled by `ConfigurationPartitionWatcher` itself.

### 9.2 Watched attributes per kind (`WatchedAttributes`)

Always read (identity, not diffed as changes): `objectGUID`, `objectSid`, `objectClass`, `uSNCreated`, `uSNChanged`,
`whenCreated`, `whenChanged` (→ `LocalChangeTime`), `name`, `isDeleted`. `msDS-User-Account-Control-Computed` is read for users/computers (lock state) but
not snapshotted.

| Kind | Diffed attributes |
|---|---|
| User (+ ServiceAccount) | `sAMAccountName`, `userPrincipalName`, `displayName`, `givenName`, `sn`, `initials`, `cn`*, `description`, `mail`, `proxyAddresses`, `title`, `department`, `company`, `manager`, `physicalDeliveryOfficeName`, `telephoneNumber`, `mobile`, `employeeID`, `employeeNumber`, `employeeType`, `info`, `userAccountControl`, `accountExpires`, `pwdLastSet`, `lockoutTime`, `primaryGroupID`, `homeDirectory`, `homeDrive`, `scriptPath`, `profilePath`, `logonHours`, `userWorkstations`, `servicePrincipalName`, `msDS-AllowedToDelegateTo`, `msDS-AllowedToActOnBehalfOfOtherIdentity`, `sIDHistory`, `adminCount`, `altSecurityIdentities`, `msDS-KeyCredentialLink`, `msDS-SupportedEncryptionTypes`, `userCertificate`, `msDS-SupersededManagedAccountLink`, `msDS-SupersededServiceAccountState` (the superseded side of a dMSA migration lives on the user/computer); gMSA/dMSA add `msDS-GroupMSAMembership`, `msDS-ManagedAccountPrecededByLink`, `msDS-DelegatedMSAState` |
| Computer | the user set minus personal fields (`givenName`, `sn`, `initials`, `mail`, `proxyAddresses`, `title`, `department`, `company`, `manager`, `physicalDeliveryOfficeName`, `telephoneNumber`, `mobile`, `employee*`, `homeDirectory`, `homeDrive`, `scriptPath`, `profilePath`, `logonHours`, `userWorkstations`) plus `dNSHostName`, `operatingSystem`, `operatingSystemVersion`, `location`, `managedBy`, `mS-DS-CreatorSID` (keeps `msDS-SupersededManagedAccountLink`, `msDS-SupersededServiceAccountState`; RODC attributes are out of v1) |
| Group | `sAMAccountName`, `groupType`, `description`, `info`, `mail`, `managedBy`, `adminCount`, `sIDHistory`, `cn`*, `member` (separate member table) |
| Contact | `displayName`, `givenName`, `sn`, `mail`, `description`, `cn`* |
| OrganizationalUnit | `ou`*, `description`, `gPLink`, `gPOptions`, `managedBy` |
| Domain head | `lockoutThreshold`, `lockoutDuration`, `lockOutObservationWindow`, `maxPwdAge`, `minPwdAge`, `minPwdLength`, `pwdHistoryLength`, `pwdProperties`, `ms-DS-MachineAccountQuota`, `gPLink`, `gPOptions`, `msDS-Behavior-Version`, `fSMORoleOwner` (`wellKnownObjects` is read once per session by `WellKnownContainers`, not diffed) |
| Configuration | `fSMORoleOwner`, `msDS-EnabledFeature`, `dSHeuristics`, `tombstoneLifetime` (per object, 9.1) |
| PublicKeyInfrastructure | templates: `displayName`, `flags`, `msPKI-Certificate-Name-Flag`, `msPKI-Enrollment-Flag`, `msPKI-RA-Signature`, `pKIExtendedKeyUsage`, `msPKI-Certificate-Application-Policy`, `nTSecurityDescriptor`; enrollment services: `certificateTemplates`, `dNSHostName`; CAs / NTAuth: `cACertificate` (hash + thumbprints) |
| GroupPolicy | `displayName`, `versionNumber`, `flags`, `gPCFileSysPath` |
| Trust | `trustDirection`, `trustType`, `trustAttributes`, `flatName`, `trustPartner` |
| PasswordSettings | `msDS-PasswordSettingsPrecedence`, `msDS-MinimumPasswordLength`, `msDS-PasswordHistoryLength`, `msDS-PasswordComplexityEnabled`, `msDS-PasswordReversibleEncryptionEnabled`, `msDS-MinimumPasswordAge`, `msDS-MaximumPasswordAge`, `msDS-LockoutThreshold`, `msDS-LockoutObservationWindow`, `msDS-LockoutDuration`, `msDS-PSOAppliesTo` |
| Critical objects (any kind) | additionally `nTSecurityDescriptor` (read with `ReadSecurityDescriptor`, DACL+owner+group, stored as hash + ACE summary) |

\* RDN attributes are not diffed as attributes – a change is reported as ObjectRenamed from the DN.

`Directory.AdditionalWatchedAttributes` are appended to User/ServiceAccount/Computer sets (e.g. `extensionAttribute1`),
`Directory.IgnoredAttributes` removed. Secret attributes (`SecretAttributes.IsSecret(name, schema)`) can never be
added – validation ignores them and the reader never requests them.

### 9.3 Noise and computer-noise (`NoiseAttributes`)

Never diffed and never requested in the change searches (even if configured): `lastLogon`, `lastLogonTimestamp`,
`lastLogoff`, `logonCount`, `badPwdCount`, `badPasswordTime`, `whenChanged` (read as identity only, 9.2), `uSNChanged`,
`uSNCreated` (identity), `dSCorePropagationData`,
`replPropertyMetaData`, `msDS-ReplAttributeMetaData`, `msDS-ReplValueMetaData`, `modifyTimeStamp`,
`msDS-LastSuccessfulInteractiveLogonTime`, `msDS-LastFailedInteractiveLogonTime`, `msDS-FailedInteractiveLogonCount`,
`msDS-FailedInteractiveLogonCountAtLastSuccessfulLogon`, `msDS-KeyVersionNumber`, `msDS-RevealedUsers`,
`msDS-RevealedList`, `msDS-ManagedPasswordId`, `msDS-ManagedPasswordPreviousId`, `msLAPS-PasswordExpirationTime`,
`ms-Mcs-AdmPwdExpirationTime`, `msTPM-*` (prefix), `dnsRecord`, `dNSTombstoned`, `replUpToDateVector`, `repsFrom`,
`repsTo`, `memberOf` (back link; membership is tracked on groups). Targeted base reads are the only exceptions:
`lastLogonTimestamp` of an account that was just enabled (10.6.2 longDisabled) and `memberOf` of a restored object
(10.9) – neither is stored.

Computer noise (diffed but dropped unless enabled): `pwdLastSet` (unless `TrackComputerPasswordChanges`),
`operatingSystem`, `operatingSystemVersion`, `dNSHostName`, `servicePrincipalName`, `msDS-SupportedEncryptionTypes`,
`userCertificate` (unless `TrackComputerOsChanges`). v1 drops these changes even when another actor made them
(re-emitting after late attribution is phase 2). Always evaluated regardless of these switches: `ComputerNameSpoofing`
(sAMAccountName) and `dNSHostName` equal to another computer's or a DC's name, and every Tier-0 attribute
(`servicePrincipalName` is computer noise only for SPNs whose host part equals the computer's own name).

### 9.4 Canonical values (`ValueCanonicalizer`)

| Syntax | Stored comparison value | Display value |
|---|---|---|
| Unicode/IA5/printable/numeric string | value exactly as returned (no trimming, case preserved) | same |
| Integer / LargeInteger | invariant decimal | FILETIME attributes (`Attr.FileTimeAttributes` + `pwdLastSet`, `accountExpires`, `lockoutTime`) rendered as local date-time by formatters; 0 / 0x7FFFFFFFFFFFFFFF = "nigdy"/"never" |
| Boolean | `TRUE` / `FALSE` | localized |
| GeneralizedTime | ISO 8601 UTC | local date-time |
| DN (`manager`, `managedBy`, `msDS-PSOAppliesTo`, `msDS-ManagedAccountPrecededByLink`, …; note `msDS-AllowedToDelegateTo` is a string list of SPNs) | `guid:<GUID "D">` when the extended DN has a GUID, else `sid:<SID>`, else `dn:<normalized DN>` | the DN from the extended value |
| SID (`objectSid`, `sIDHistory`, `mS-DS-CreatorSID`) | SID string (`AdConvert.SidToString`) | SID + resolved account name when known (snapshot lookup by SID) |
| GUID | "D" format | same |
| `logonHours` | 42 bytes as lower-case hex | summary "pon–pt 7–17" (formatter) |
| Security descriptor (`nTSecurityDescriptor`, `msDS-AllowedToActOnBehalfOfOtherIdentity`, `msDS-GroupMSAMembership`) | side-car summary values `owner:<SID>`, `ace:<type>/<flags>/<mask hex>/<SID>[/<objectType GUID>][/<inheritedObjectType GUID>]` (one per ACE, **sorted and de-duplicated** – the comparison value), plus `sha256:<hex>` of the raw SD as a cheap pre-check only: a different hash with an identical summary set (ACE re-ordering, canonicalization, `Set-Acl` without a semantic change) is **no change** | changes shown as ACE added/removed with trustee name + rights (known extended rights by name: DS-Replication-Get-Changes 1131f6aa-…, -All 1131f6ad-…, User-Force-Change-Password 00299570-…, etc.) |
| DN-Binary (`msDS-KeyCredentialLink`) | `sha256:<hex of the binary part B:<len>:<hex> only>` – the DN part is the owning object's own DN (or `<GUID=…>` with Extended DN) and changes on every rename/move, so it is never hashed | "zmieniono (n → m wpisów)" |
| Octet string (`userCertificate`) | `sha256:<hex>` | count + thumbprints (SHA-1) |
| Other binary | `sha256:<hex>` | "wartość binarna" |

Values longer than `Notifications.MaxValueLength` are stored whole in the snapshot (comparison) but truncated with
"…" in `AttributeChange` (Truncated = true). Multi-valued attributes keep at most `Directory.MaxValuesPerAttribute`
values per side in `AttributeChange` (counts are exact). Membership is never stored as an attribute.

### 9.5 Membership storage

`snapshot_members(group_guid, member_key, member_guid, member_sid, member_dn)` – full list per group, replaced when the
group is re-read. Member keys come from Extended DN values: GUID when present (normal objects), `sid:` for foreign
security principals without GUID in the value (should not happen – FSPs have GUIDs), `dn:` fallback. Members outside
the snapshot (other domains via FSP, objects in excluded subtrees) are stored too; their display name comes from the DN.
`primaryGroupID` membership is not in `member`: the PrivilegeEvaluator adds it virtually (principal's `primaryGroupID`
RID + domain SID → group).

---

## 10. Detection algorithms (W-core)

All LDAP filters are built with `LdapFilter` (`And`, `Or`, `Equal`, `GreaterOrEqual`, `LessOrEqual`, `BitAnd`,
`EqualGuid`, `Not`) – never by string concatenation of values. DNs are composed with `DistinguishedName`
(`Parse`, `Child`, `WithParent`, `IsDescendantOf`, `AreEqual`). All calls are async with the job's
`CancellationToken`; the engine never holds more than one result page plus the current cycle's change set in memory.

### 10.1 Cycle (`ChangeDetectionEngine.RunOnceAsync`)

```
session  = await sessionProvider.GetSessionAsync(ct)        // pinned DC (provider handles PreferPdc), gMSA Kerberos sign+seal
cfg      = configProvider.Current (tokens expanded with the session's domain SID)
dc       = await DcStateReader.ReadAsync(session)           // see 10.2
state    = await store.GetDcStateAsync(domain)
known    = state is null || dc is the stored DC ? null : await store.GetWatermarkAsync(dc.DsServiceName, dc.InvocationId)
mode     = DetectionModeSelector.Select(state, known, dc, now, cfg) // 10.3
switch mode:
  NoChange       → update in-memory LastPollAt; persist LastPollAt at most every 5 min; health Ok
  Baseline       → FullScan(emitEvents: false)            // 10.4
  Resync         → raise HealthResync event; FullScan(emitEvents: true, versionAware: true, tag reconciliation)
  Reconciliation → FullScan(emitEvents: true, versionAware: false, tag reconciliation)
  Incremental    → Increment(W = state.HighestUsn, H = dc.HighestUsn)   // 10.5
  Failback       → Increment(W = known.HighestUsn, H = dc.HighestUsn, versionAware: true)   // known DC, same invocationId
Incremental/Failback only: ConfigurationPartitionWatcher (10.11) and DeletedObjectsScanner (10.9) with the same W'/H
            (after a Resync the old W belongs to another DC's USN space – deletions are then found by the full
            scan's disappeared handling, and DomainControllerDirectory is simply refreshed);
all modes (incl. NoChange): CreationSettler emits due Created events (10.6.3);
            PrivilegeEvaluator when anything membership-relevant changed, on the **first cycle after process start**,
            and whenever PrivilegeState.ComputedAt < DcState.MembershipsChangedAt (crash safety, 10.8);
            final commit (NewState: W := H, and for Baseline/Resync/Failback the new DcHostName/DsServiceName/InvocationId;
            the store also upserts the DcWatermark of that DC), signals.Raise(EventsStored) when events were stored;
            health report (DirectoryStatusDto)
return JobResult(NextRunIn: cfg.Directory.PollInterval); WakeSignals = [PollRequested, ConfigChanged]
```

Errors: `DirectoryException` with `IsTransient` → `sessionProvider.ReportFailure`, `JobResult(Failed, "directory")`,
the host retries with backoff; after `Directory.FailoverAfter` of continuous failures the provider connects to another
DC (located) and reports HealthDirectoryUnavailable; the next cycle sees a different dsServiceName → Failback when that
DC was polled before with the same invocationId, otherwise Resync. `AccessDenied` on the incremental search →
HealthPermissionGap and the cycle aborts without moving W. Exceptions are logged only through `SafeExceptionLog`.

### 10.2 DC state (`DcStateReader`)

1. `GetAsync("", ["highestCommittedUSN", "dsServiceName", "dnsHostName", "isSynchronized", "currentTime", "serverName"])`.
   Read **H before any search** (Microsoft's documented algorithm).
2. `GetAsync(dsServiceName, ["invocationId", "objectGUID"])` → invocationId (16-byte GUID).
3. PDC: `GetAsync(defaultNC, ["fSMORoleOwner", "wellKnownObjects"])` once per session; `IsPdc =
   AreEqual(fSMORoleOwner, dsServiceName)` (reported only – PDC selection is the session provider's job, 6.2);
   `WellKnownContainers` parses `wellKnownObjects` (`B:32:AA312825768811D1ADED00C04FD8D5CD:<dn>` = computers,
   `B:32:A9D1CA15768811D1ADED00C04FD8D5CD:<dn>` = users; lab-verified present) – when the computers container is the
   literal `CN=Computers,{domain}` (no `redircmp`), health Info `directory.computersNotRedirected` (one-time hint).
4. Domain SID: `objectSid` of the NC head; root domain SID from `rootDomainNamingContext` head (best effort).

### 10.3 Mode selection (`DetectionModeSelector`) – first match wins

| Condition | Mode |
|---|---|
| no DcState, or `BaselineCompletedAt` null | Baseline |
| `isSynchronized` = FALSE | skip the cycle (health Warning `directory.notSynchronized`), W unchanged |
| `dsServiceName` ≠ stored (case-insensitive DN compare) and a `DcWatermark` exists for (dsServiceName, invocationId) with `H ≥` its W | **Failback** (incremental from that DC's own W, version-aware – every change since then has a local USN > W on that DC) |
| `dsServiceName` ≠ stored | Resync (reason `dcChanged` – never-seen DC) |
| invocationId ≠ stored | Resync (`invocationIdChanged` – restore / VM-GenerationID rollback) |
| H < stored W | Resync (`usnRollback`) |
| local time ≥ today's `FullReconciliationTime` and `LastFullSyncAt` < that moment | Reconciliation |
| H = W | NoChange |
| otherwise | Incremental |

### 10.4 Full scan (Baseline / Resync / Reconciliation)

```
H  = dc.HighestUsn (already read)
pass 1: containers   base=defaultNC subtree filter=ContainerClasses                 (10.5 rules for containers)
pass 2: everything   base=defaultNC subtree filter=ScopeClasses AND NOT ContainerClasses
        ExtendedDn = true, PageSize = 1000, attributes = WatchedAttributes.RequestSet
        for each page: snapshots = store.GetSnapshotsAsync(page GUIDs); process (10.6); seen.Add(guid)
        commit every page (chunk): Upserts + Memberships + Events (only when emitEvents), NewState = null
        (chunks never write DC identity or W)
memberships: groups' "member" from the entry; ranged values via GetAllValuesAsync(… ExtendedDn) (10.8)
critical objects: GetAsync(dn, ["nTSecurityDescriptor"]) for each expanded CriticalObjects DN
ConfigurationPartitionWatcher.FullSync (10.11): nTDSDSA, Configuration and PKI objects (no events at Baseline)
after pass 2 (not Baseline): for every live snapshot GUID under the default NC not in `seen` → Disappeared handling (10.9)
final commit: PrivilegeState (Baseline: computed, no events), NewState { HighestUsn = H, LastFullSyncAt = now,
              BaselineCompletedAt = now (Baseline) }, IsBaseline; Baseline also appends HealthBaselineCompleted
```

- Baseline emits **no change events** (a monitor must never alert on its first look) and sets no
  `CreationPendingUntil` – the settle window only applies to objects created after the baseline.
- Resync / Failback (`versionAware`): for an attribute whose new metadata version (when known) is **lower** than the
  snapshot's stored `Version`, the DC has not replicated that change yet → keep the snapshot value, no event, warning
  `staleDc` (counted). Equal version → no change. Without metadata → plain value diff.
- Crash safety: a crash mid-scan leaves DC state and W unchanged (chunks carry `NewState = null`); the next cycle
  repeats the same mode; already-updated snapshots produce no diff, so events are not duplicated.

### 10.5 Incremental cycle

```
W' = max(0, W + 1 - Directory.UsnOverlap)
pass 1: containers changed:  (&(uSNChanged>=W')ContainerClasses) – collect all (small), sort by old DN depth ascending,
        process → DnRewrites (10.7)
pass 2: (&(uSNChanged>=W')(ScopeClasses)(!ContainerClasses)) paged 1000, ExtendedDn, same attribute set
        per page: snapshots = store.GetSnapshotsAsync(GUIDs) (includes deleted rows)
                  for each entry: process (10.6) with old.Dn rewritten by this cycle's DnRewrites
                  metadata pass for objects that produced events or are inside their settle window (10.10)
                  chunk commit (NewState = null)
then ConfigurationPartitionWatcher, DeletedObjectsScanner, CreationSettler, PrivilegeEvaluator, BurstDetector inputs
final commit: NewState { HighestUsn = H, LastPollAt = now, LastChangeAt = now if events }
```

`ContainerClasses = (|(objectClass=organizationalUnit)(objectClass=container)(objectClass=domainDNS))`.
`ScopeClasses = (|(objectClass=user)(objectClass=group)(objectClass=contact)(objectClass=organizationalUnit)
(objectClass=container)(objectClass=domainDNS)(objectClass=groupPolicyContainer)(objectClass=trustedDomain)
(objectClass=msDS-PasswordSettings)(objectClass=rIDManager)(objectClass=infrastructureUpdate))` (computers, MSAs and
inetOrgPerson are `objectClass=user`).
Out-of-scope filtering happens client-side (ExcludedSubtrees, CN=System except the listed classes and critical
objects, CN=LostAndFound). The order of returned entries is arbitrary (lab-verified) – never derive W from it.

### 10.6 Per-object processing (`SnapshotBuilder` → `ObjectDiffer` → `ChangeClassifier` → `EventTagger` → `SeverityResolver`)

```
new = SnapshotBuilder.Build(entry, previous: old)      // canonical values (9.4), carries old Version/ChangedAt
if entry is out of scope:
    if old is live and its DN changed → event ObjectMoved (PreviousDn = old.Dn, Dn = new DN, tag "outOfScope")
         (an object that did not move – an ExcludedSubtrees entry was added – is removed silently)
    if old exists → DetectionCommit.Removals += guid; a container also removes, silently, its stored descendants that
         now live below its new DN (10.7)
    continue
if old is null:
    if Baseline → upsert, no event
    elif entry.uSNCreated > W (Resync, where W belongs to another DC: whenCreated > (LastPollAt ?? LastFullSyncAt) − 3 h,
         the margin covers objects created on another DC shortly before the last poll and not yet replicated to the
         old DC, and clock skew) → upsert with CreationPendingUntil = whenCreated + Directory.CreationSettleWindow and
         no event yet (the CreationSettler emits the Created event, 10.6.3); with a zero window the Created event is
         emitted at once
    else → ObjectAppeared (Info; tag outOfScope when the object was moved in: the `name` metadata – a move or rename
         stamps it – has usnLocal > W; without metadata or W, whenChanged > LastPollAt. Never decided from the
         configuration); a container also adopts, silently, the descendants without a row that were not created
         since the last poll (10.7)
elif old.IsDeleted → ObjectRestored (+ diff vs the last live snapshot for context, not as separate events) and
         RestoredMembershipReader (10.9)
elif old.CreationPendingUntil is set → upsert the new state, no events (still settling)
else:
    diff = ObjectDiffer.Diff(old, new)    // DN change + attribute changes (+ membership via 10.8)
    if diff empty → no write (lastLogonTimestamp churn produces this case constantly)
    else events = ChangeClassifier.Classify(diff)
all events of one object share ChangeSetId (Guid v7); Object = new.ToRef(); DetectedAt = now;
LocalChangeTime = entry.whenChanged; Source = mode-dependent; Tags = EventTagger; Severity = SeverityResolver
kinds in Directory.DisabledKinds or optional kinds not enabled are dropped here (health kinds never)
```

`ChangeClassifier` – attribute → kind (an attribute is consumed by the first matching line; leftovers go into one
`AttributesChanged` event):

| Attribute(s) / condition | Kind |
|---|---|
| DN parent changed | ObjectMoved (PreviousDn); also the RDN changed → additionally ObjectRenamed |
| DN RDN changed only | ObjectRenamed |
| `userAccountControl` | `UacFlagClassifier` (10.6.1) |
| `lockoutTime` 0/absent → t>0, or t1 → t2 > t1 | AccountLockedOut (LockoutInfo: LockedAt = t2, LockedUntil = t2 + **effective** lockout duration from `LockoutPolicyResolver` – the user's PSO (`msDS-ResultantPSO` base read → `msDS-LockoutDuration`) when readable, else the domain `lockoutDuration` (absolute value); 0x8000000000000000 / 0 ("until an admin unlocks") → null; `LockoutsToday` = `IEventStore.CountForObjectAsync(guid, AccountLockedOut, local midnight)` + 1) |
| `lockoutTime` t>0 → 0 | `t0` = originating time of the lockoutTime write (metadata, else DetectedAt): **AccountUnlocked** when LockedUntil is null or `t0 < t + effective duration` (an admin/helpdesk unlock); otherwise **no event** – the DC resets `lockoutTime` to 0 at the first successful logon after the lockout expired (Samba `authsam_logon_success_accounting`, Windows likewise), which is not an unlock (LockoutMonitor reports the expiry when `LockoutExpired` is enabled). A 4767 matched later for a "no event" reset is ignored |
| `pwdLastSet` → 0 | PasswordMustChangeSet; additionally **PasswordChanged** (same change set, same computer / ServiceAccount suppression as below) when metadata is available and `unicodePwd` `usnLocal > W` – the usual helpdesk reset with "must change at next logon" writes both in one poll. Without metadata (or without a W of this DC) must-change alone is reported |
| `pwdLastSet` → value > old, `unicodePwd` metadata `usnLocal > W` (or no metadata available) | PasswordChanged (computers: dropped unless TrackComputerPasswordChanges; ServiceAccount kind: dropped unless TrackManagedPasswordRollover) |
| `pwdLastSet` → value > old, metadata available and `unicodePwd` `usnLocal ≤ W` | **PasswordAgeReset** (password age extended without a new password) |
| `accountExpires` | AccountExpirationChanged |
| `sAMAccountName`, `userPrincipalName` | AccountNameChanged; computer whose new sAMAccountName lacks the trailing `$` or equals another DC's name without `$` → ComputerNameSpoofing |
| `primaryGroupID` to a non-default RID (users 513/514, computers 515, DCs 516/521) | PrimaryGroupChanged (always also feeds the PrivilegeEvaluator; commit MembershipRelevant) |
| `sIDHistory` values added/removed | SidHistoryChanged |
| `adminCount` absent/0 → 1 | AdminCountSet (Info when the principal is in PrivilegeState – SDProp follow-up of a PrivilegeGranted; otherwise High, attribution-dependent: SYSTEM actor → Info) (1 → absent/0: AttributesChanged) |
| `servicePrincipalName` added on User/ServiceAccount(user) | ServicePrincipalNameAdded (removals: AttributesChanged) |
| `msDS-KeyCredentialLink` | KeyCredentialChanged (severity by pattern, 10.6.4) |
| `msDS-AllowedToDelegateTo`, `msDS-AllowedToActOnBehalfOfOtherIdentity` | DelegationChanged |
| `msDS-ManagedAccountPrecededByLink`, `msDS-DelegatedMSAState`, `msDS-SupersededManagedAccountLink`, `msDS-SupersededServiceAccountState` | ManagedAccountLinkChanged (Critical when the linked/superseded principal is privileged) |
| `altSecurityIdentities` | CertificateMappingChanged (Critical when privileged) |
| `msDS-GroupMSAMembership` | ManagedPasswordReadersChanged (Changes: ACEs added/removed) |
| `nTSecurityDescriptor` (critical objects) | SecurityDescriptorChanged when the owner/ACE summary set differs (9.4) (Changes: owner + ACEs added/removed) |
| `groupType` | GroupTypeChanged (flags: Security, Global/DomainLocal/Universal) |
| `gPLink`, `gPOptions` (OU, domain head) | GroupPolicyLinkChanged (Changes: linked/unlinked GPO DNs + enforced/disabled flags; GPO display names resolved from snapshot) |
| `fSMORoleOwner` (domain head, RID Manager$, Infrastructure) | FsmoRoleChanged (Changes: role, old → new DC) |
| domain head policy attributes, `ms-DS-MachineAccountQuota` | DomainPolicyChanged |
| PSO attributes | PasswordSettingsChanged |
| groupPolicyContainer attributes | GroupPolicyChanged |
| trustedDomain attributes | TrustChanged |
| `mS-DS-CreatorSID` | (no event; only tag createdByNonAdmin on creation) |
| everything else watched | AttributesChanged (one event, all leftover attributes) |

#### 10.6.1 `UacFlagClassifier`

Decode old/new `userAccountControl` (enum `UserAccountControl` from AdMng.Core). Bit changes:

| Bit | Set → | Cleared → |
|---|---|---|
| 0x0002 ACCOUNTDISABLE | AccountDisabled | AccountEnabled |
| 0x0080000 TRUSTED_FOR_DELEGATION | DelegationChanged (Critical unless the object is a DC: tag domainController, i.e. 0x2000 in the **new** value – also a one-step promotion that sets both bits – → no event) | DelegationChanged (Medium) |
| 0x1000000 TRUSTED_TO_AUTH_FOR_DELEGATION | DelegationChanged (High) | DelegationChanged (Medium) |
| 0x2000 SERVER_TRUST_ACCOUNT | DomainControllerAdded (Critical) | AccountFlagsChanged (High) |
| 0x0020 PASSWD_NOTREQD, 0x400000 DONT_REQ_PREAUTH, 0x0080 ENCRYPTED_TEXT_PWD_ALLOWED, 0x200000 USE_DES_KEY_ONLY | AccountFlagsChanged (High) | AccountFlagsChanged (Low) |
| 0x10000 DONT_EXPIRE_PASSWORD | AccountFlagsChanged (Medium) | AccountFlagsChanged (Low) |
| 0x40000 SMARTCARD_REQUIRED, 0x100000 NOT_DELEGATED | AccountFlagsChanged (Low) | AccountFlagsChanged (High when the object is privileged, else Low) |
| any other bit (0x0001 SCRIPT, 0x0008, 0x0040 PASSWORD_CANT_CHANGE, 0x0200 NORMAL, 0x1000 WORKSTATION, …) | AccountFlagsChanged (Low) | AccountFlagsChanged (Low) |

One `AccountFlagsChanged` event per object diff carries all its flag changes (`FlagsAdded`/`FlagsRemoved` with the
LDAP names `ACCOUNTDISABLE`, `PASSWD_NOTREQD`, … as listed in `UserAccountControl`, upper-snake); its severity is the
maximum of the per-bit severities. Lock state is not a UAC bit (0x10 only appears in the constructed attribute).

#### 10.6.2 `EventTagger`

| Tag | Condition |
|---|---|
| privileged | object key (or, for membership kinds, the member) in the current **PrivilegeState** – nothing else |
| adminCountOrphan | `adminCount` = 1 and not in PrivilegeState (no severity bump; digest hygiene section) |
| serviceAccount | ServiceAccount kind, user with ≥ 1 SPN, or DN under `ServiceAccountSubtrees` |
| defaultContainer | new parent DN equals the **resolved** default users/computers container (`WellKnownContainers`; follows `redirusr`/`redircmp`) – a digest flag, no severity bump |
| unexpectedContainer | the default container is redirected, yet the object was created in the literal `CN=Users,{domain}` / `CN=Computers,{domain}` |
| createdByNonAdmin | computer with `mS-DS-CreatorSID` |
| server | computer `operatingSystem` contains "Server" (ordinal ignore case) |
| domainController | UAC 0x2000 |
| disabledOu | object DN (or PreviousDn for moves) under `DisabledAccountsSubtrees` |
| longDisabled | AccountEnabled and (the snapshot's `userAccountControl.ChangedAt` ≤ now − 30 days **or** a base read of `lastLogonTimestamp` shows it absent or older than now − 44 days (30 + 14 days replication lag) – covers accounts disabled before the baseline) |
| krbtgt / builtinAdministrator | RID 502 / 500 |
| reconciliation | Source = Reconciliation |
| syncAccount, selfChange, passwordReset | added by attribution (11.4) |

#### 10.6.3 Creation settle window (`CreationSettler`)

Tools create accounts in several LDAP steps (New-ADUser → Set-ADAccountPassword → Enable-ADAccount, bulk scripts) that
straddle polls. A new object (uSNCreated > W, not Baseline) is stored with `CreationPendingUntil = whenCreated +
CreationSettleWindow`; diffs inside the window update the snapshot without events. Every cycle (also NoChange)
`store.GetPendingCreationsAsync(now)` returns settled objects; for each, one ChangeSet is emitted:

- the Created event by kind (UserCreated, ComputerCreated, GroupCreated, OrganizationalUnitCreated, ContactCreated,
  ServiceAccountCreated; Domain/Container: none; GroupPolicy → GroupPolicyChanged "created"; Trust → TrustChanged
  "created"; PasswordSettings → PasswordSettingsChanged "created") with Changes = the **final** watched values
  (non-empty attributes; flags decoded); groups: one GroupMemberAdded per member at that time;
- risky bits of the final `userAccountControl` evaluated against an empty baseline: AccountFlagsChanged (PASSWD_NOTREQD,
  DONT_REQ_PREAUTH, ENCRYPTED_TEXT_PWD_ALLOWED, USE_DES_KEY_ONLY → High; DONT_EXPIRE_PASSWORD → Medium; PASSWD_NOTREQD on
  a still-disabled account → Low) and DelegationChanged (TRUSTED_FOR_DELEGATION / TRUSTED_TO_AUTH_FOR_DELEGATION);
- the snapshot row is upserted with `CreationPendingUntil = null`.

Deleted inside the window → the Created event is emitted first (last known state), then ObjectDeleted. Privilege
changes are never deferred (the PrivilegeEvaluator works on group membership).

#### 10.6.4 KeyCredentialChanged severity (`SeverityResolver`)

| Object / pattern | Severity |
|---|---|
| computer, not DC and not privileged, value count 0 → 1 (device self-registration / hybrid join) | Low (digest) |
| computer that already had ≥ 1 value, or a DC, or privileged | High |
| user, privileged | High |
| user, not privileged | Medium; attribution-dependent → High when the resolved actor is neither the user itself nor a `syncAccount` |

`SyncAccountDetector` (baseline and reconciliation) recognizes sync accounts: sAMAccountName `MSOL_*` or `Sync_*`, or a
description containing "Azure Active Directory Connect" / "Microsoft Entra Connect" / "Azure AD Connect". Their SIDs
are kept in memory (tag `syncAccount` on attribution) and offered as `ExclusionSuggestionDto` for `tier0-security`
(never applied automatically – no service-initiated config writes).

### 10.7 Moves and renames (`DnRewriter`)

- Containers (pass 1) are processed in ascending depth of their old DN. For each in-scope container whose DN changed,
  the cycle records (its stored DN at the start of the cycle, its new DN). A DN of the start of the cycle is rewritten
  **once**, onto the new DN of its **deepest** recorded ancestor – never through a chain, so a shift (B→C, then A→B) or
  a swap of names in one poll puts A's children under the new B whatever order the server returned the containers in
  (so a child of a renamed OU is not reported as moved).
- The commit applies the rewrites to all stored descendants (DN suffix replace, case-insensitive on the normalized
  DN) and to `snapshot_members.member_dn`, one after the other: deepest old DN first; when a new DN lies at, below or
  above another rewrite's old DN (shift, swap, a container moved onto the name of a removed one whose child moved away),
  in two phases through unique temporary DNs below `DC=admng-rewrite,DC=invalid` – the result equals the
  deepest-ancestor rule. Descendants keep their USN – AD does
  not bump them.
- Pass 2 runs after the container chunk committed the rewrites, so its stored DNs are already rewritten. The
  PreviousDn of an object that left a container renamed in the same poll is mapped back to the DN of the start of
  the cycle (deepest recorded new DN → its old DN) – unless another object held that new DN at the start of the cycle
  and did not move (deleted, left the scope): then the stored DN is kept (its former children were never rewritten).
- ObjectMoved: parent changed. ObjectRenamed: RDN value changed. Both when both changed.
- A move **out of** scope (`ExcludedSubtrees`) is an ObjectMoved with tag `outOfScope` and a `Removals` entry (hard
  delete of the row; other groups' member lists keep the key). A container moved out takes its subtree along: no
  rewrite is recorded for it, and its stored descendants that a subtree search finds below its new DN get `Removals`
  entries in the same chunk without events (the container's ObjectMoved covers them; AD does not bump them, so no
  later pass would see them); descendants found elsewhere (moved away or deleted in the same poll) keep their rows for
  pass 2 and the deletion pass. When the new location cannot be read (warning `outOfScopeSubtreeUnreadable`), every
  stored descendant is removed; one that moved away in the same poll is then found without a row by pass 2
  (ObjectAppeared) instead of being lost. An object outside the scope whose DN did not change (an `ExcludedSubtrees` entry was
  added) is removed **silently** – the configuration change is audited as WatchdogConfigurationChanged.
- A move **into** scope finds no snapshot → ObjectAppeared with tag `outOfScope` (no Created event, no ObjectRestored –
  only tombstone-marked rows are restores). A container moved in (or found again after an exclusion was removed) adopts
  its subtree in the same cycle: descendants without a row are snapshotted silently (members read as for 10.8),
  instead of staying unknown until the reconciliation reports each of them. Descendants **created since the last poll**
  (`uSNCreated > W`; Resync: the whenCreated rule of 10.6) are not adopted – the cycle's own search (or the next one)
  returns them and they get the creation settle window and their Created event like any new object.

### 10.8 Membership (`MembershipReader`, `MembershipDiffer`, `PrivilegeEvaluator`)

Reading (groups in the increment or full scan):
1. The entry's `member` values (Extended DN) – when the server returned a ranged attribute name
   (`member;range=0-1499`), read all values with
   `session.GetAllValuesAsync(dn, "member", new AttributeReadOptions { ExtendedDn = true }, ct)`.
2. Each value → `ExtendedDistinguishedName.TryParse` → `MemberReference { Key = GUID "N" | "sid:…" | "dn:…" }`.
3. `MembershipTracking.PrivilegedAndWatchListOnly`: store members only for privileged groups (by SID/name/DN),
   watch-list groups and groups that are members of those (one level is enough because nested groups are tracked
   anyway when they are reachable – the evaluator reads unknown nested groups on demand). A group read while it is
   not tracked gets the reserved snapshot marker `$members = untracked` (an empty stored list and "never stored" look
   the same in `snapshot_members`) and its stored list, if any, is **dropped** (replaced by an empty one) – a list kept
   while nobody re-reads it would go stale and the evaluator trusts stored lists. The **first tracked read** of a group
   whose old snapshot carries the marker stores its list **silently** (no GroupMemberAdded for members that were there
   all along; `MembershipRelevant`, so the evaluator reports new privileges). The same applies after switching
   `MembershipTracking` to `AllGroups`. The restore re-insertion (10.9) only touches groups whose list is stored.
   Because a page's member lists are committed together, a group read in the same page as the list that contains it
   (every nested group at the baseline, a group nested in the same poll) is still untracked when read; the evaluator's
   on-demand read of such a group (a member of a stored list without a list of its own) therefore **stores** what it
   read and clears the marker in the final commit – unless the group's `uSNChanged > H` (the next cycle reads and
   diffs it). Its next change is then diffed normally (GroupMemberAdded/Removed) – important for groups nested in a
   watch-list group, where no PrivilegeGranted would stand in for a silent first read.

Diffing: old = `store.GetMembersAsync(groupGuid)`, new = read set, by `Key`. Added → `GroupMemberAdded` (Object =
group, Member = member ref resolved from the snapshot by GUID, else from the DN), removed → `GroupMemberRemoved`.
Removal from a group whose SID is in `ProtectRemovalSids` → severity High. Each event carries the group's
`LocalChangeTime` (no metadata: LVR `member` changes are not in replPropertyMetaData). A commit containing membership
replacements, deletions/removals of principals or primaryGroupID changes sets `MembershipRelevant`.

Deleted members: when the DeletedObjectsScanner reports a deletion, the store removes the key from every group
(`DetectionCommit.Deletions`) – **no** GroupMemberRemoved events (the ObjectDeleted event lists the former groups in
an `AttributeChange { Attribute = "memberOf", OldValues = group DNs }` for context). A removed member that a search of
this domain by objectGUID no longer finds counts as deleted too – except a member whose DN lies in another naming
context (another domain of the forest, also a child domain lexically below this one: a `DC=` RDN below the domain root,
or outside it), which such a search can never see: its removal is a GroupMemberRemoved.

`PrivilegeEvaluator` – runs when the cycle changed memberships, primaryGroupIDs, created/deleted/restored groups or
principals; always at baseline/resync/failback; on the **first cycle after process start**; and whenever the stored
`PrivilegeState.ComputedAt` is older than `DcState.MembershipsChangedAt` (a crash after a chunk commit with membership
replacements and before the final commit would otherwise lose the PrivilegeGranted – the diff is idempotent):
1. Roots: groups whose SID ∈ expanded `CriticalSids` (tier Critical) or `HighSids` / `HighSamAccountNames` /
   `AdditionalHighDns` (High) / `AdditionalCriticalDns` (Critical), plus the watchdog's own role and host groups from the
   bootstrap file (`Authorization.ViewerSids`, `AdminSids`, `WatchdogHostGroupSids` – High, not removable through the
   API); a group in both lists is Critical.
2. Graph = `store.GetAllMembershipsAsync()` overlaid with this cycle's replacements and deletions, plus virtual edges
   from `GetNonDefaultPrimaryGroupsAsync()` (RID → `{domainSid}-RID` → group GUID by SID lookup).
3. BFS from every root through member edges; nested groups are traversed (cycle-safe, visited set); every reached
   member key (users, computers, service accounts, FSPs **and nested groups**) becomes a `PrivilegedPrincipal` with
   the shortest path and the highest tier reached.
4. Diff vs the stored `PrivilegeState` by (PrincipalKey, GroupGuid): new pairs → `PrivilegeGranted` (Object =
   principal, Member = privileged group, Via = path refs, severity = tier), removed pairs → `PrivilegeRevoked`
   (Medium). Pairs whose principal was deleted in this cycle produce no Revoked event – nor pairs whose principal's
   snapshot row is marked deleted, or has no row and is not found live by objectGUID (a cycle repeated after a crash
   between the deletion chunk and the final commit; a repeated reconciliation after ObjectDisappeared). Members outside
   the snapshot that still exist (excluded subtrees, FSPs) are revoked normally, and so are members of another domain
   of the forest – told by the DN of a member list of this cycle (stored, read live or just removed), as for
   GroupMemberRemoved; the revoked principal's ObjectRef takes that DN when there is no row. Pairs that came back with a
   restored object (10.9) get tag `restored` and the ObjectRestored event's ChangeSetId (they inherit its 5138
   attribution).
5. Commit the new state. Domain Controllers (RID 516 via primary group) and krbtgt etc. are part of the baseline state
   and therefore never alert unless they change.

Time-bound (PAM TTL) memberships expire without bumping the group; their expiry is found by the nightly reconciliation
(PrivilegeRevoked, Medium). Reading TTLs is phase 2.

### 10.9 Deletions, restores, disappearance (`DeletedObjectsScanner`, `RestoredMembershipReader`, `Reconciler`)

Precondition: `Directory.TrackDeletedObjects` and the self-check `permission.deletedObjects` is Ok (LCRP on
`CN=Deleted Objects`). Query: base `CN=Deleted Objects,{domain}`, OneLevel, `IncludeDeleted = true`
(`IncludeRecycled = true` when `SessionInfo.RecycleBinEnabled`), filter
`(&(isDeleted=TRUE)(uSNChanged>=W')(uSNChanged<=H)(|(objectClass=user)(objectClass=group)(objectClass=organizationalUnit)(objectClass=contact)(objectClass=groupPolicyContainer)(objectClass=trustedDomain)(objectClass=msDS-PasswordSettings)))`
(tombstones written after H was read belong to the next cycle)
– only the watched classes (`objectClass` survives on tombstones, objectCategory is stripped; computers, MSAs and
inetOrgPerson are `user`). Children removed by tree-delete or system churn (msFVE-RecoveryInformation, msTPM-*,
printQueue, serviceConnectionPoint, msDS-Device in CN=RegisteredDevices, dnsNode in legacy domain-partition DNS zones,
foreignSecurityPrincipal clean-up) therefore never produce events. Attributes `objectGUID`, `objectSid`,
`objectClass`, `sAMAccountName`, `lastKnownParent`, `msDS-LastKnownRDN`, `isRecycled`, `whenChanged`, `uSNChanged`,
`uSNCreated`.

| Tombstone GUID in snapshot | Result |
|---|---|
| live | `ObjectDeleted` (Object = last live snapshot ref, PreviousDn = last live DN, context memberOf), `Deletions` entry |
| live, `CreationPendingUntil` set | the deferred Created event first (10.6.3), then `ObjectDeleted` |
| already deleted (Recycle Bin: deleted → recycled) | nothing |
| absent, Recycle Bin enabled, `isRecycled = TRUE` and `uSNCreated < W'` (a deleted object recycled after its deleted row was purged by retention) | nothing – deleted → recycled is never a new deletion (without the Recycle Bin every tombstone is `isRecycled = TRUE`, so the check applies to Recycle Bin forests only) |
| unknown (created and deleted between two polls; with the Recycle Bin also deleted **and** purged) | `ObjectDeleted` from tombstone data **only when** `lastKnownParent` resolves (through the snapshot, or the parent tombstone's own lastKnownParent chain) to an in-scope location – not under `CN=System` (except the listed classes), `CN=LostAndFound`, `ExcludedSubtrees` or a deleted user/computer; otherwise dropped. DN = the RDN under the resolved parent: `msDS-LastKnownRDN` (set on the Recycle Bin path only), else the tombstone's own RDN cut at the `\0ADEL:<guid>` mangling (tombstones without the Recycle Bin – Samba, Windows without the feature – have no `msDS-LastKnownRDN`, lab-verified). In the same chunk a **deleted marker row** (identity only, no attributes, `IsDeleted`) is upserted, so a repeated cycle, the later recycling and a restore know the object (a restore is ObjectRestored without context changes) |

The original location is always taken from the snapshot (lastKnownParent can point into Deleted Objects when the
parent was deleted too). Restores are detected by the increment (10.6: a tombstone-marked snapshot GUID appears live).

**Restores** (`RestoredMembershipReader`): AD Recycle Bin re-activates the restored object's link values **without
bumping the groups** (same model as member deletion, lab-verified: a member deletion left the group's uSNChanged
unchanged). On ObjectRestored the engine therefore reads the restored object's `memberOf` (Extended DN, ranged; a
targeted read – never stored as an attribute) and re-inserts its key into those groups' member lists in the same
commit (`Memberships` replacements of the affected groups, `MembershipRelevant`), then runs the PrivilegeEvaluator in
that cycle (PrivilegeGranted with tag `restored`, 10.8). Without the Recycle Bin (tombstone reanimation) the link
values are gone and `memberOf` is empty. The Windows link/USN behaviour is confirmed in the Windows lab run (26.4).

Without Deleted-Objects access, deletions are found by the daily reconciliation: a live snapshot GUID under the
default NC not seen in the full scan is looked up with `(objectGUID=<LdapFilter.EqualGuid>)` subtree from `{domain}`
with `IncludeDeleted`: found deleted → ObjectDeleted (source Reconciliation) and a `Deletions` entry; found live at another
DN outside scope → ObjectMoved `outOfScope` and a `Removals` entry; not found, or found live at its old DN but invisible
to the scan → `ObjectDisappeared` (Low; deleted, moved out of read scope, or permissions lost) and a `Removals` entry
(if it reappears later it is ObjectAppeared, never ObjectRestored).

`MassDeletion` (BurstDetector) counts ObjectDeleted events by object class within the window (grouped by actor once
attribution resolved, otherwise overall): users, groups, OUs and service accounts against `Scans.MassDeletion`
(≥ 10 in 10 min → **Critical**); computers and contacts against `Scans.MassComputerDeletion` (≥ 50 in 10 min →
**High** – stale-computer clean-ups are routine). One MassDeletion event per burst (BurstInfo with up to 50 objects);
cooldown = window. The candidates are the ObjectDeleted events **detected** within the window, but they count as a
burst only when the deletions themselves (`LocalChangeTime` = the tombstone's whenChanged, else `OriginatingTime`,
else `DetectedAt`) fall within one window: deletions spread over hours that are found together – the catch-up cycle
after an outage, a Resync, the reconciliation – are no burst; a real burst found late is reported once. After a computer MassDeletion attributed to one actor, that actor is offered as an
`ExclusionSuggestionDto` (`computerCleanupAccount`, rule `bursts`) – applied only by an admin.

### 10.10 Replication metadata (`ReplicationMetadataXmlParser`, `ReplicationMetadataDecoder`, `AttrTypResolver`, `OriginatingDcResolver`)

For every object that produced events in a page (and every object in its settle window or with a pwdLastSet /
lockoutTime change – PasswordAgeReset and unlock-vs-expiry need it), one base read: `GetAsync(dn,
["msDS-ReplAttributeMetaData", "replPropertyMetaData"])`. Prefer the XML (Windows); fall back to the binary blob (Samba, or Windows when the
constructed attribute is unreadable); neither → no metadata (fallback windows in attribution).

XML value (one per attribute; Windows may append `\0` and leave `&` unescaped in DNs – remove NULs and replace
`&(?!(amp|lt|gt|quot|apos|#\d+);)` with `&amp;` before parsing with `XmlReader` (`DtdProcessing.Prohibit`,
`XmlResolver = null`)):

```xml
<DS_REPL_ATTR_META_DATA>
	<pszAttributeName>department</pszAttributeName>
	<dwVersion>2</dwVersion>
	<ftimeLastOriginatingChange>2026-10-14T07:12:01Z</ftimeLastOriginatingChange>
	<uuidLastOriginatingDsaInvocationID>845842f4-6f6d-4e7a-a5cd-4f58276592e5</uuidLastOriginatingDsaInvocationID>
	<usnOriginatingChange>184467</usnOriginatingChange>
	<usnLocalChange>184467</usnLocalChange>
	<pszLastOriginatingDsaDN>CN=NTDS Settings,CN=DC01,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=contoso,DC=com</pszLastOriginatingDsaDN>
</DS_REPL_ATTR_META_DATA>
```

Binary `replPropertyMetaData` (little-endian): header `uint version (=1), uint reserved, uint count, uint reserved`
(16 bytes), then `count` entries of 48 bytes: `uint attrTyp, uint version, long timeChanged (seconds since
1601-01-01 UTC), Guid originatingInvocationId (16 bytes, Guid(byte[]) layout), long usnOriginating, long usnLocal`.
Reject blobs with a wrong length or version ≠ 1 (warning, no metadata).

`AttrTypResolver`: ATTRTYP ↔ OID per MS-DRSR 5.16.4 (`OidFromAttid`/`MakeAttid`) with the default prefix table
below; build `attrTyp → lDAPDisplayName` from the session schema (`AttributeDefinition.AttributeId`) plus one query
`(&(objectClass=attributeSchema)(msDS-IntId=*))` on the schema NC (`msDS-IntId` → name). Unresolvable ATTRTYPs are
ignored. Test vectors: `0x00000014` ↔ 2.5.4.20 (telephoneNumber), `0x00090008` ↔ 1.2.840.113556.1.4.8
(userAccountControl), `0x0009005A` ↔ 1.2.840.113556.1.4.90 (unicodePwd).

Default prefix table (index → OID prefix): 0 `2.5.4`, 1 `2.5.6`, 2 `1.2.840.113556.1.2`, 3 `1.2.840.113556.1.3`,
4 `2.16.840.1.101.2.2.1`, 5 `2.16.840.1.101.2.2.3`, 6 `2.16.840.1.101.2.1.5`, 7 `2.16.840.1.101.2.1.4`, 8 `2.5.5`,
9 `1.2.840.113556.1.4`, 10 `1.2.840.113556.1.5`, 11 `1.2.840.113556.1.4.260`, 12 `1.2.840.113556.1.5.56`,
13 `1.2.840.113556.1.4.262`, 14 `1.2.840.113556.1.5.57`, 15 `1.2.840.113556.1.4.263`, 16 `1.2.840.113556.1.5.58`,
17 `1.2.840.113556.1.5.73`, 18 `1.2.840.113556.1.4.305`, 19 `0.9.2342.19200300.100`, 20 `2.16.840.1.113730.3`,
21 `0.9.2342.19200300.100.1`, 22 `2.16.840.1.113730.3.1`, 23 `1.2.840.113556.1.5.7000`, 24 `2.5.21`, 25 `2.5.18`,
26 `2.5.20`, 27 `1.3.6.1.4.1.1466.101.119`, 28 `2.16.840.1.113730.3.2`, 29 `1.3.6.1.4.1.250.1`,
30 `1.2.840.113549.1.9`, 31 `0.9.2342.19200300.100.4`, 32 `1.2.840.113556.1.6.23`, 33 `1.2.840.113556.1.6.18.1`,
34 `1.2.840.113556.1.6.18.2`, 35 `1.2.840.113556.1.6.13.3`, 36 `1.2.840.113556.1.6.13.4`, 37 `1.3.6.1.1.1.1`,
38 `1.3.6.1.1.1.2` (verify against Samba `source4/dsdb/schema/schema_prefixmap.c` while implementing; the three test
vectors above must pass).

Use of metadata:
- `SnapshotAttribute.Version/ChangedAt` updated from the entries of the diffed attributes.
- Event `OriginatingTime` / `OriginatingDc`: the newest entry among the event's attributes whose `usnLocal > W`
  (Created: the `objectClass` entry; password events: `unicodePwd` or `pwdLastSet`; lockouts: `lockoutTime`).
  `OriginatingDcResolver`: invocationId → DC host via `DomainControllerDirectory` (forest view: all `nTDSDSA` under
  `CN=Sites,{config}` with `invocationId`, parent server `dNSHostName`; cached, refreshed hourly or on a miss, 10.11).
  XML also gives `pszLastOriginatingDsaDN` directly (parent → server → dNSHostName).
- Password changes without a value read: `unicodePwd` entry present with `usnLocal > W` confirms a password write even
  when only metadata is visible.

### 10.11 Configuration, PKI and schema partitions (`ConfigurationPartitionWatcher`, `DomainControllerDirectory`)

`DomainControllerDirectory` keeps two views (refreshed hourly, on a resolution miss and by this watcher):
- **all DCs of the forest** – every `nTDSDSA` under `CN=Sites,{config}` (invocationId → host resolution, 10.10);
- **attribution sources** – writable DCs of this domain only:
  `(&(objectClass=nTDSDSA)(msDS-HasDomainNCs=<defaultNC>))` (attribute lab-verified on the NTDS Settings object), RODCs
  (objectCategory `nTDSDSARO`) excluded – RODCs never originate writes and other domains' DCs refuse Event Log Readers
  of this domain (error 5).

Each incremental / failback cycle (same W'/H), and a full sync at Baseline/Resync/Reconciliation (no events at
Baseline):
- `CN=Sites,{config}` subtree `(&(objectClass=nTDSDSA)(uSNChanged>=W'))` attributes `objectGUID`, `uSNCreated`,
  `invocationId`, `objectCategory`, `msDS-HasDomainNCs` → snapshot rows of kind DomainController; a new GUID →
  `DomainControllerAdded` (Object = server object's DN, dNSHostName; tag `otherDomain` and severity High when
  `msDS-HasDomainNCs` is another domain). After each refresh a previously known nTDSDSA GUID that is no longer
  present → `DomainControllerRemoved` (High; Medium with `otherDomain`) and a `Removals` entry (no tombstone access
  needed).
- FSMO and forest singletons: base reads (`uSNChanged`, watched attributes of 9.2) of `CN=Partitions,{config}`,
  `CN=Directory Service,CN=Windows NT,CN=Services,{config}` and the schema NC head – diffed only when `uSNChanged ≥ W'`:
  `fSMORoleOwner` → FsmoRoleChanged; `msDS-EnabledFeature` (Recycle Bin), `dSHeuristics`, `tombstoneLifetime` →
  DomainPolicyChanged (object kind Configuration). The domain-NC FSMO holders (domain head, RID Manager$,
  Infrastructure) come from the normal increment (9.1).
- PKI (`CN=Public Key Services,CN=Services,{config}` subtree)
  `(&(uSNChanged>=W')(|(objectClass=pKICertificateTemplate)(objectClass=pKIEnrollmentService)(objectClass=certificationAuthority)))`
  with the 9.2 PKI attributes (templates also `nTSecurityDescriptor`, owner+DACL) → snapshot rows of kind
  PublicKeyInfrastructure; created/changed/deleted (GUID gone on the next full sync) → CertificateTemplateChanged
  (Changes: flags decoded – e.g. `CT_FLAG_ENROLLEE_SUPPLIES_SUBJECT`, EKUs added/removed, RA signature count, ACEs
  added/removed with enroll/write rights named; enrollment services: published templates added/removed; CAs/NTAuth:
  certificate thumbprints added/removed).
- Schema NC (when `WatchSchemaPartition`) one-level `(&(uSNChanged>=W')(uSNChanged<=H)(|(objectClass=attributeSchema)(objectClass=classSchema)))`
  attributes `lDAPDisplayName`, `uSNCreated`, `objectClass` → **one** `SchemaChanged` event per cycle (Changes:
  counts created/modified and up to 50 lDAPDisplayNames; Exchange `/PrepareSchema` = one event, not thousands).
  Baseline/Resync: no events. Schema objects are not snapshotted, so the event is emitted in the cycle's **final
  commit** (with W): a cycle that fails after its chunk commits repeats the range without a duplicate, and writes after
  H are reported by the next cycle only. Entries of the `UsnOverlap` re-read (`uSNChanged ≤ W`) were reported by the
  previous cycle and are skipped; "created" means `uSNCreated > W`.
- The incremental nTDSDSA and PKI searches use the same range `(uSNChanged>=W')(uSNChanged<=H)`.

### 10.12 Permission self-check (`PermissionSelfCheck`) – at startup after baseline and every `PermissionSelfCheckInterval`

| Code | Probe | Failure state |
|---|---|---|
| `permission.uSNChanged` | collect the objectGUID sets of `(&(objectCategory=person)(objectClass=user))` and of the same AND `(uSNChanged>=1)` (paged, objectGUID only); for every GUID only in the first set, a base read with `uSNChanged` – a gap is reported only for objects that **still exist** without a readable `uSNChanged` (deletions between the two searches are not gaps) | Error (objects invisible to change tracking; Args: count) |
| `permission.accountRestrictions` | 20 sample users: `userAccountControl`, `pwdLastSet`, `accountExpires` present | Error when missing on any |
| `permission.replicationMetadata` | domain head: `msDS-ReplAttributeMetaData` or `replPropertyMetaData` present | Warning |
| `permission.deletedObjects` | base read of `CN=Deleted Objects,{domain}` with Show Deleted | Warning (deletions via reconciliation only) |
| `permission.domainPolicy` | domain head `lockoutDuration`, `maxPwdAge` present | Warning |
| `permission.passwordSettings` | `CN=Password Settings Container,CN=System,{domain}` readable (PSOs, effective lockout duration) | Warning (domain lockout duration used for everyone) |
| `permission.configurationPartition` | `CN=Sites,{config}` nTDSDSA search returns ≥ 1 | Warning |
| `eventlog.<dc>` | reported by the AttributionEngine (`GetLogInfoAsync`) | Error with Win32 error code |

Results → `IHealthRegistry`; a transition from Ok to Warning/Error appends one `HealthPermissionGap` event
(not repeated while the state stays bad; repeated after recovery and relapse).

### 10.13 Lockout monitor (`LockoutMonitor`) – only when `LockoutExpired` is enabled

Every `LockoutRecheckInterval`: `(&(objectCategory=person)(objectClass=user)(lockoutTime>=1))` with
`msDS-User-Account-Control-Computed`, `lockoutTime`. Keep the previous locked set in memory (first run: no events).
Previously locked (bit 0x10), now not locked, `lockoutTime` unchanged → `LockoutExpired`. The filter cannot use the
constructed bit (lab-verified) – evaluate it client-side. (A later reset of `lockoutTime` to 0 at the next successful
logon produces no event, 10.6.)

### 10.14 Expiry scanners (`AccountExpiryScanner`, `PasswordExpiryScanner`, `ExpiryThresholds`)

Account expiry, every `Scans.AccountExpiry.Interval`:

```
T = max(thresholds); now = clock; today = local date (configured zone)
filter = (&(objectCategory=person)(objectClass=user)
           (accountExpires>=FILETIME(now − 1 day))(accountExpires<=FILETIME(now + (T + 4) days))
           [(!(userAccountControl:1.2.840.113556.1.4.803:=2)) unless IncludeDisabled])
for each user:
  expires = FILETIME(accountExpires)            // 0 and 0x7FFFFFFFFFFFFFFF never match the range
  lastDay = (local(expires) − 1 tick).Date      // ADUC "End of: D" stores the local midnight that starts D+1
  daysLeft = lastDay − today                    // calendar days; 0 = the last valid day is today
  if expires ≤ now: if store.TryRecord(Notice(guid, Account, raw, threshold 0)) → AccountExpired
  else for each threshold th (descending):
       fireDay = lastDay − th; if FireOnBusinessDays: fireDay = last business day ≤ fireDay (BusinessCalendar)
       if today ≥ fireDay and TryRecord(Notice(guid, Account, raw, th))
          → AccountExpiring(ExpiryInfo(Account, expires, lastDay, daysLeft, th)) for the smallest such th only,
            all larger thresholds recorded silently (no burst of stale thresholds after downtime)
ReplaceUpcoming(Account, users with daysLeft ≤ Digest.UpcomingExpiryDays)
```

So the "1 day" notice fires on the day before the last valid day (or the previous business day: an account ending on
Sunday is announced on Friday, before the 07:30 digest of that Friday when the scanner runs hourly from midnight).
Display: "konto wygasa z końcem dnia 21.10.2026". Dedup key = (objectGuid, kind, raw accountExpires, threshold) – a
changed expiration date re-arms all thresholds. Delivery (12.4 `expiry`): helpdesk in business hours (aggregated) +
digest; optionally the account's manager (`RuleDelivery.NotifyManager`, off by default).

Password expiry (`PasswordExpiryScope` ≠ None): prefilter
`(&(objectCategory=person)(objectClass=user)(pwdLastSet>=1)(!(userAccountControl:1.2.840.113556.1.4.803:=2))(!(userAccountControl:1.2.840.113556.1.4.803:=65536)))`
with `msDS-UserPasswordExpiryTimeComputed`; skip 0 (must change) and 0x7FFFFFFFFFFFFFFF (never); scope
PrivilegedAndService keeps objects tagged privileged/serviceAccount (PrivilegeState + ServiceAccountSubtrees +
SPN); thresholds/dedup/business-day rule as above with kind Password (the constructed value is the exact moment, so
`lastDay` = local date of that moment). Severity: Info; High via tag bump at ≤ 7 days for privileged/service.

### 10.15 Bursts (`BurstDetector`, `PasswordSprayDetector`, inside ScheduledScanJob, every minute)

- **Mass lockout** (`BurstDetector`): distinct objects with `AccountLockedOut` detected in the last `MassLockout.Window`
  whose lockout times (`Lockout.LockedAt`) fall within one window (as for MassDeletion, 10.9) ≥
  `max(Threshold, ceil(PercentOfEnabledUsers × enabled users / 100))` (enabled users counted at reconciliation) →
  `MassLockout` (Medium, BurstInfo), cooldown = window (restart-safe: skip when such an event exists in the store within
  the window). This is *not* a password-spraying detector – spraying deliberately stays below the lockout threshold.
- **Password spraying** (`PasswordSprayDetector`, only when `Scans.PasswordSpray.Enabled` – which also makes the kind
  recorded, no `EnabledOptionalKinds` entry needed; needs the failure auditing
  rows of docs 4.5): every minute an on-demand query of the PDC emulator's Security log (failed passwords are re-checked
  there) for 4771 with Status
  0x18 / 0x6 and 4776 with Status 0xC000006A / 0xC0000064 in the last `Window` (bounded like 11.4 #6: timeout, one query
  per DC, `MaxRecords` 20 000); group by IpAddress (4771) / Workstation (4776); a source with failures for ≥
  `DistinctAccounts` distinct TargetUserNames → `PasswordSprayingSuspected` (High, BurstInfo with up to 50 account
  names as ObjectRefs resolved live by sAMAccountName, `Attribution.IpAddress`/`Workstation` = the source); cooldown per
  source = window. Nothing is ingested or stored except the event. A name that resolves to no account (4776
  0xC0000064, 4771 0x6) is **masked** to its first character and length (`L…(9)`, `ObjectRef.Name` only): such names
  are often passwords typed into the user name field, so the raw TargetUserName never reaches the store, a digest
  or a notification (the distinct-name count stays exact).
- Mass deletion: see 10.9.

### 10.16 Change-notification wake-up (`ChangeNotificationWaker`)

Optional (`WakeUpContainers`, ≤ 4 – AD allows 5 per connection): `session.WatchAsync(container, _ =>
signals.Raise(PollRequested))`; re-subscribed when the session or the configuration changes; never a source of
events. The RootDSE check every `PollInterval` stays the reliable trigger.

### 10.17 Inactive accounts report (`InactiveAccountsReport`, weekly digest section only – no events, no storage)

On `InactiveAccounts.ReportDay` the DigestBuilder queries live:
users `(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2))(|(lastLogonTimestamp<=FT(now−Days))(!(lastLogonTimestamp=*)))(whenCreated<=GT(now−MinAgeDays)))`,
computers the same with `(objectClass=computer)`, privileged principals with `PrivilegedDays`. The digest shows counts
and the first `MaxRowsPerSection` rows (name, last logon date, OU).

### 10.18 Budgets (SHOULD)

- Idle cycle (H = W): 2 base reads (RootDSE + NTDS Settings), < 50 ms on a LAN.
- Incremental page of 1000 objects processed < 2 s (Linux CI, FakeDirectorySession).
- Baseline of 50 000 objects < 10 min on Windows; memory < 300 MB working set (chunked commits).
- Lab measurement (Samba): full `(uSNChanged>=1)` scan of 2 585 objects with two attributes took 0.93 s.

---

## 11. Attribution (W-core: parsing/correlation; W-service: Windows reader)

### 11.1 Ingestion (`AttributionEngine`, every `Attribution.PollInterval`, wake on `EventsStored`)

```
if !Attribution.Enabled or !securityEventSource.IsAvailable:
    mark pending events older than 30 s as Unknown(SOURCE_DISABLED) (except NotApplicable kinds); return
sources = Source == ForwardedEvents ? [Collector] (log "ForwardedEvents")
        : Attribution.DomainControllers (config; every entry must be a writable DC of this domain known to
          DomainControllerDirectory – unknown hosts are skipped with health Error eventlog.<host> "notADomainController")
          or the DomainControllerDirectory attribution sources (10.11) (log "Security")
for each source (in parallel, max 4; a source failing with a store error is reported as health Error
                 eventlog.<host> and does not stop or discard the others):
    info = GetLogInfoAsync(host, log)                      → health eventlog.<host> (Ok / Error with Win32 code)
    cursor = store.GetCursorAsync(host)
  Security log of a DC (cursor = the DC's EventRecordID):
    if cursor null: query From = now − ReplicationLatencyBound (do not ingest history)
    else: AfterRecordId = cursor.LastRecordId
          if info.OldestRecordNumber > cursor.LastRecordId + 1 → HealthEventLogGap (records overwritten unread)
    loop: ReadAsync(host, query{ EventCodes = SecurityEventCatalog.IngestionSet, AfterRecordId, SuppressAttributes = noise,
                                 MaxRecords = 1000 }) → parse → EventSourceGuard → reduce + mask (below)
          → AppendAsync(events, newCursor) until < 1000 returned or Attribution.MaxRecordsPerPoll records were read
          after the last (short) page newCursor = max(last record read, newest record of info) – the filtered query
          proves no ingestion-set record exists up to it, so a busy DC whose log wraps between account changes raises no
          false "overwritten" gap and no LOG_OVERWRITTEN
          budget exceeded → HealthEventLogGap (reason "flooding"); the cursor keeps moving (next poll continues)
    newest record id < cursor (log cleared / recreated) → HealthEventLogGap + rebase cursor to the newest record
  ForwardedEvents log of a WEF collector (records keep their source DC's EventRecordID – not comparable across DCs nor
  with the collector's numbering): the cursor is time based – the newest TimeCreated read (capped at now) is kept in
  EventLogCursor.BookmarkXml as <TimeCursor SystemTime="…"/>; every poll reads the ingestion set from that time −
  ReplicationLatencyBound (late WEF deliveries) in one read of at most MaxRecordsPerPoll records, stored in pages of
  1000; the collector's record numbers are never checked for overwrites or regressions. A read cut by
  MaxRecordsPerPoll (HealthEventLogGap "flooding") stores the newest TimeCreated read as the cursor's Floor
  (<TimeCursor SystemTime="…" Floor="…"/>, at least one tick after the read's start): later reads start at
  max(cursor − ReplicationLatencyBound, Floor), so a window holding more records than the budget (a bulk import) cannot
  make every poll re-read its oldest records and stall the cursor. Records read again are
  ignored by the store (computer, record id) and by the detections (stable ids, below).
  1102 / 4719 / 4794 / 1104 / 1100 → EventLogOnlyDetector (11.6) and record gaps, appended to the event store before
    the cursor of their page is committed, under an id derived from the record (RecordEventIds: Guid v7 layout,
    timestamp = TimeCreated, rest = SHA-256 of what|computer|record id|time|event id) and only when that id is not
    stored yet – an interrupted pass re-reads the page and neither loses nor repeats a SecurityLogCleared
  1102 / 1104 (and a cursor regression) mark the DC's log overwritten from the record's time (11.7 LOG_OVERWRITTEN)
then correlate (11.4) every pending event whose next retry is due
report AttributionStatusDto (per DC: last read, last record id, coverage 24 h, source lookups)
```

**Reduction at parse time** (ADR-0008 cond. 2 – only what matching needs; also a flood guard):
- `EventSourceGuard`: a record is accepted only when `Provider/@Name` is `Microsoft-Windows-Security-Auditing`
  (or `Microsoft-Windows-Eventlog` for 1102/1104/1100) and `System/Computer` is a DC known to
  `DomainControllerDirectory` (also in ForwardedEvents mode) – others are dropped and counted (health Warning
  `eventlog.<source>.foreignRecords`).
- 5136–5141 records whose `ObjectDN` is under `ExcludedSubtrees`, or whose `ObjectClass` is not a watched class
  (9.1 kinds incl. nTDSDSA, PKI and schema classes), are dropped.
- Only the indexed keys (16.2) and the `Data` fields of the 11.2 mapping are kept. A 5136 `AttributeValue` is kept only
  when `AttributeLDAPDisplayName` is in the effective watched set **and** not secret (`SecretAttributes.IsSecret`);
  otherwise only the attribute name and `OperationType` are stored. Without a loaded schema every attribute is treated
  as non-watched (no values). SAM-field values of 4720/4738/4741/4742 follow the same rule via the 11.2 field mapping.

`SecurityEventQueryBuilder` produces the Windows structured query (`<QueryList><Query Id="0" Path="Security">
<Select Path="Security">*[System[(EventID=4720 or …) and EventRecordID &gt; 12345]]</Select>…<Suppress Path="Security">
*[System[EventID=5136] and EventData[Data[@Name='AttributeLDAPDisplayName']='dSCorePropagationData']]</Suppress>
</Query></QueryList>`) with **at most 20 EventID expressions per Select** (several Selects OR-ed), time filters as
`TimeCreated[@SystemTime&gt;='…Z']`, data filters as `EventData[Data[@Name='TargetLogonId']='0x30dc2']`; log names,
data names and suppressed attributes are restricted to `[0-9A-Za-z.\-_:{}$ ]`, a data value is one XPath string literal
(single-quoted, double-quoted when it contains an apostrophe – `o'neill`; rejected when it contains both quote
characters or a control character), so `józef.żak` is queried and no value can change the query; everything is
XML-escaped.

`WindowsSecurityEventSource` (W-service, `[SupportedOSPlatform("windows")]`): one `EventLogSession(host, null, null,
null, SessionAuthentication.Kerberos)` per host (process identity = gMSA; never `Default`/Negotiate, which falls back to
NTLM towards whatever host is configured); a host that is not a host name (IP literal, `HostNames.IsHostName`) is
refused before connecting (Win32 123), and `AttributionSourceResolver` skips such a collector or DC with health Error
`invalidHostName`; `EventLogQuery(logName, PathType.LogName, queryXml)
{ Session, ReverseDirection = NewestFirst, TolerateQueryErrors = true }`, `EventLogReader { BatchSize = 256 }`,
`record.ToXml()` only (never `FormatDescription()` or `*DisplayName`), all blocking calls on `Task.Run` with
`session.CancelCurrentOperations()` on cancellation; `EventLogException`/`UnauthorizedAccessException` →
`SecurityLogAccessException` (Win32 1722 RPC unavailable → firewall; 5 → Event Log Readers). `GetLogInfoAsync` uses
`session.GetLogInformation(logName, PathType.LogName)`.

### 11.2 Event catalog (`SecurityEventCatalog`)

Ingestion set: 4720 4722 4723 4724 4725 4726 4738 4740 4767 4781 4765 4766 4794 4741 4742 4743 4727 4728 4729 4730
4731 4732 4733 4734 4735 4737 4754 4755 4756 4757 4758 4764 4744 4745 4746 4747 4748 4749 4750 4751 4752 4753 4759
4760 4761 4762 4763 4719 5136 5137 5138 5139 5141 1102 1104 1100. 4624/4771/4776/4625 are never ingested – only
queried on demand, bounded (11.4 #6, `LockoutSourceResolver`, `PasswordSprayDetector`).

Candidates per watch-event kind (strong key in brackets):

| WatchEventKind | Security events |
|---|---|
| UserCreated | 4720 [TargetSid = objectSid], 5137 [ObjectGUID] |
| ComputerCreated, ServiceAccountCreated | 4741 [TargetSid], 5137 [ObjectGUID] |
| GroupCreated | 4727, 4731, 4754, 4744, 4749, 4759 [TargetSid], 5137 |
| OrganizationalUnitCreated, ContactCreated | 5137 [ObjectGUID] |
| ObjectDeleted | 4726 (user), 4743 (computer), 4730/4734/4758/4748/4753/4763 (groups) [TargetSid]; 5141 [ObjectGUID] |
| ObjectRestored | 5138 [ObjectGUID] |
| ObjectMoved, ObjectRenamed | 5139 [ObjectGUID] (OldObjectDN/NewObjectDN must match) |
| AccountDisabled / AccountEnabled | 4725 / 4722 [TargetSid]; 4738/4742 with the UAC bit; 5136 userAccountControl |
| AccountLockedOut | 4740 [TargetSid] (preferably from the PDC) – evidence only: the actor is NotApplicable (the 4740 subject is the DC); the **source of the bad passwords** comes from `LockoutSourceResolver` (11.4 #6b) |
| AccountUnlocked | 4767 [TargetSid] (the subject is the person who unlocked) |
| PasswordChanged, PasswordAgeReset | 4724 (reset by another account → tag passwordReset), 4723 (change → tag selfChange when the subject is the account itself or ANONYMOUS LOGON S-1-5-7 – an expired password changed at logon; a 4723 by another account gets no selfChange) [TargetSid]; PasswordAgeReset: 4738 PasswordLastSet / 5136 pwdLastSet |
| PasswordMustChangeSet, AccountExpirationChanged, AccountNameChanged(UPN), AttributesChanged (SAM fields) | 4738 / 4742 with the field ≠ '-' [TargetSid]; 5136 [ObjectGUID + AttributeLDAPDisplayName] |
| AccountNameChanged (sAMAccountName) | 4781 [TargetSid] |
| AccountFlagsChanged, DelegationChanged (UAC) | 4738/4742 Old/NewUacValue [TargetSid]; 5136 |
| SidHistoryChanged | 4765/4766, 4738 SidHistory [TargetSid] |
| GroupMemberAdded | 4728/4732/4756/4751/4746/4761 [TargetSid = group SID and MemberSid = member SID]; 5136 member Value Added [ObjectGUID = group] |
| GroupMemberRemoved | 4729/4733/4757/4752/4747/4762 [same]; 5136 member Value Deleted |
| GroupTypeChanged | 4764 [TargetSid] |
| PrivilegeGranted / Revoked | inherits the attribution of the GroupMemberAdded/Removed (or PrimaryGroupChanged, or ObjectRestored for tag `restored`) event that caused it: same ChangeSetId; otherwise the newest membership change on **any edge** of the nesting path (root ← Via… ← principal – the path is listed from the root down, so Via[0] is the group nested directly in the root) or a PrimaryGroupChanged of the principal, detected at most 15 min before the privilege event (`PrivilegeAttributionInheritor.CauseWindow`); an older change is never taken – nesting IT-Ops into Domain Admins today is the cause, not yesterday's addition of Bob to IT-Ops |
| everything else (attribute kinds, GPO, trust, PSO, SD, schema, FSMO, PKI, Configuration) | 5136 / 5137 / 5141 [ObjectGUID] |
| DomainControllerAdded / DomainControllerRemoved | 5137 / 5141 nTDSDSA [ObjectGUID] |
| WatchdogConfigurationChanged | Resolved at creation from the API caller (Kerberos identity) |
| expiry, bursts (except PasswordSprayingSuspected: source only), health, ObjectAppeared/Disappeared, LockoutExpired | NotApplicable |

SAM field mapping (4720/4738/4741/4742 Data name → LDAP attribute): SamAccountName→sAMAccountName,
DisplayName→displayName, UserPrincipalName→userPrincipalName, HomeDirectory→homeDirectory, HomePath→homeDrive,
ScriptPath→scriptPath, ProfilePath→profilePath, UserWorkstations→userWorkstations, PasswordLastSet→pwdLastSet,
AccountExpires→accountExpires, PrimaryGroupId→primaryGroupID, AllowedToDelegateTo→msDS-AllowedToDelegateTo,
OldUacValue/NewUacValue→userAccountControl, SidHistory→sIDHistory, LogonHours→logonHours,
DnsHostName→dNSHostName, ServicePrincipalNames→servicePrincipalName.

### 11.3 Parsing (`SecurityEventParser`, `InsertionStringDecoder`, `SamAccountControlDecoder`)

- Namespace `http://schemas.microsoft.com/win/2004/08/events/event`; read `System/EventID`, `Version`,
  `TimeCreated/@SystemTime` (UTC), `EventRecordID`, `Computer`; `EventData/Data[@Name]` **by name, never by
  position**; for 1102 read `UserData/*/*` child elements as data. Unknown names and higher versions are kept/ignored
  silently. `XmlReader` with `DtdProcessing.Prohibit` and `CheckCharacters = false`: characters invalid in XML 1.0
  (control characters of attacker-written values, raw or as `&#x1;`) are replaced by U+FFFD instead of dropping the
  record – one value must not hide a change from attribution.
- `-` and empty values are omitted. Logon ids (`0x30dc2`) are normalized to lower-case `0x…` and compared
  numerically. SIDs are strings `S-1-…`; GUIDs `{…}` parse with `Guid.Parse`.
- `%%` codes: 1792 `<value changed, but not displayed>`, 1793 `<value not set>`, 1794 `<never>`, 1797 All,
  14674 Value Added, 14675 Value Deleted, 14676 AD DS, 14677 AD LDS, 14678 Yes, 14679 No, 14680 Value Added With
  Expiration Time, 14681 Value Deleted With Expiration Time, 14688 Value Auto Deleted With Expiration Time;
  `OperationType` is stored as `ValueAdded` / `ValueDeleted` / … tokens.
- `UserAccountControl` list `%%2048+b` (bit b cleared) / `%%2080+b` (set) – cross-check only; **decode
  Old/NewUacValue as SAM ACB bits** (`SamAccountControlDecoder`: 0x1 DISABLED, 0x2 HOMDIRREQ, 0x4 PWNOTREQ,
  0x8 TEMPDUP, 0x10 NORMAL, 0x20 MNS, 0x40 DOMTRUST, 0x80 WSTRUST, 0x100 SVRTRUST, 0x200 PWNOEXP, 0x400 AUTOLOCK,
  0x800 ENC_TXT_PWD_ALLOWED, 0x1000 SMARTCARD_REQUIRED, 0x2000 TRUSTED_FOR_DELEGATION, 0x4000 NOT_DELEGATED,
  0x8000 USE_DES_KEY_ONLY, 0x10000 DONT_REQUIRE_PREAUTH, 0x20000 PW_EXPIRED, 0x40000 TRUSTED_TO_AUTHENTICATE_FOR_DELEGATION,
  0x80000 NO_AUTH_DATA_REQD, 0x100000 PARTIAL_SECRETS, 0x200000 USE_AES_KEYS) and map them to the LDAP UAC names for
  comparison with the LDAP diff. (Microsoft's own 4720 prose decodes 0x15 with the LDAP table – wrong; the four
  Microsoft samples verify the ACB interpretation.)
- **4740**: `TargetDomainName` holds the *Caller Computer Name* → exposed as `CallerComputerName` (client-supplied,
  may be empty or a front-end server); Subject is the DC (S-1-5-18).
- `System/Provider/@Name` is kept for `EventSourceGuard` (11.1); records of other providers are dropped.
- Masking: `AttributeValue` of 5136 and every 4738/4720 field whose LDAP attribute is secret
  (`SecretAttributes.IsSecret(attr, schema)`) → `SecretAttributes.Mask` before storing.

### 11.4 Correlation (`AttributionCorrelator`, `AttributionScorer`, `OriginatingDcResolver`)

For each pending watch event (Status Pending, `Attribution` kind not NotApplicable), at `DetectedAt` + each
`PendingRetries` step:

1. **Window and DCs**: with OriginatingTime + OriginatingDc → that DC, `[t − MatchWindow, t + MatchWindow]`; with
   OriginatingTime but unknown DC → all attribution-source DCs, same window; otherwise (LVR membership changes,
   missing metadata) → all attribution-source DCs, `[LocalChangeTime (or DetectedAt) − ReplicationLatencyBound,
   (LocalChangeTime or DetectedAt) + FallbackWindow]`.
2. **Candidates**: `store.FindAsync(criteria)` with the kind's codes and the strong key (TargetSid / ObjectGuid /
   group SID + MemberSid); when none, weak keys (TargetUserName = sAMAccountName, ObjectDN equal via
   `DistinguishedName.AreEqual` to Dn or PreviousDn).
3. **Score** (`AttributionScorer`): strong key +50, weak key +20; attribute confirmed +30 (5136 attribute name and
   value: Value Added = new value / Value Deleted = old value after syntax normalization; 4738 field present; UAC bit
   changed in ACB diff; 4722/4725 for enable/disable; 4781 old/new names; 4728… member SID); originating DC matches
   +10; |Δt| ≤ 2 s +10, ≤ 30 s +5. Candidates below 50 are discarded.
4. **Decision**: best ≥ 60 → Resolved; confidence High (strong key + confirmed attribute), Medium (strong key only),
   Low (weak key). Two different subjects within 5 points → Ambiguous (Candidates listed, no Actor). No candidate → next
   retry; after the last retry → Unknown with a reason (11.7).
5. **Actor**: SubjectUserSid → snapshot `FindBySidAsync` → display name/DN; fallback `SubjectDomainName\SubjectUserName`.
   S-1-5-18 or `<DC>$` → Resolved, Reason SYSTEM (shown "System (DC01)"). Subject SID = object SID (a computer
   updating itself, a user changing their own password via 4723; for a 4723 also ANONYMOUS LOGON) → tag `selfChange`,
   Reason SELF_CHANGE for computers; with `SuppressComputerSelfChanges` the event is kept but every rule behaves as if
   `ExcludeSelfChanges` were set for changes of a computer that touch only computer-noise attributes (9.3; SPN values
   naming the computer itself). 4724 by another account → tag `passwordReset`. Subject SID of a detected sync account
   (10.6.4) → tag `syncAccount`.
6. **Source** (`LogonSourceResolver`), when `LookupLogonSource` and SubjectLogonId ≠ 0x3e7: query on the evidence DC
   for 4624 with `TargetLogonId = <id>`, **From = max(evidence time − LogonLookback, the log's oldest record time)**,
   To = evidence time, NewestFirst, MaxRecords 1; **at most one such query per DC at a time**, per-query timeout
   `SourceQueryTimeout` (cancels via `CancelCurrentOperations`), positive and **negative** cache of (dc, logonId) for
   at most 24 h (no ingestion) – logon ids are unique per boot only, so a found session is reused only for evidence
   between its 4624 and `LogonLookback` later and only while no 1100 (event log service shut down – a reboot) of that
   DC was ingested after the logon; a negative result only for evidence inside the window already searched. Nothing found → `SourceReason = SOURCE_LOGON_NOT_FOUND`; in ForwardedEvents mode the query runs
   against the collector's ForwardedEvents log – when the subscription does not carry 4624, `SOURCE_NOT_COLLECTED`.
   LogonType 3 + IpAddress → IpAddress is the primary source, Workstation = WorkstationName if present else reverse
   DNS (2 s timeout, SourceMethod "dns"); IpAddress `-`, `::1`, `127.0.0.1` → SourceMethod "local" ("lokalnie na DC –
   ADWS/PowerShell/konsola"); LogonType 10 → RDP client address (may be a jump host); LogonType 8 → simple bind.
   `SourceConfidence` ≤ the actor's confidence and **capped at Low** for SourceMethod "dns", "4740" and an NTLM
   WorkstationName (client-supplied); names are shown with their origin ("zgłoszona przez klienta" / "(DNS)") and are
   never used in rule matching or exclusions. Lookup counts/durations/timeouts per DC → `DcLogStatusDto`.
   6b. **Lockout sources** (`LockoutSourceResolver`, AccountLockedOut, when `LookupLockoutSources`): the 4740 caller is
   recorded (`LockoutInfo.CallerComputer`, client-supplied); when it is empty, a DC, or a known server (tag `server` in
   the snapshot – Exchange, ADFS, NPS, VPN front ends), the PDC and the DC that logged the 4740 are queried (same bounds
   as #6) for 4771 Status 0x18, 4776 Status 0xC000006A and 4625 with `TargetUserName = sAMAccountName` in
   `[lockoutTime − lockOutObservationWindow (effective), lockoutTime]`; results are aggregated into
   `LockoutInfo.Sources` (IP / workstation × count, top 10) and written with `AttributionUpdate.Lockout`. The lockout's
   attribution status is NotApplicable (there is no "actor"); the GUI and e-mail label the row "Źródło błędnych haseł".
   While no 4740 is found or a source query did not finish (WEF delivers the 4740 and the 4771/4776 in ~30 s batches;
   the PDC unreachable), the lockout stays Pending and is retried at the `PendingRetries` steps; after the last step it
   is final with what was found.
7. **Operation grouping**: events with the same ChangeSetId resolved from candidates with the same (Computer,
   SubjectLogonId) inside 2 s share the actor; ADUC "new user" = 4720 + 4724 + 4722 + 4738s is one operation.
8. **Attribution-dependent severity** (12.2): for PasswordChanged, AdminCountSet and user KeyCredentialChanged the
   `SeverityResolver` re-resolves the severity with the final attribution and the added tags.
9. **Re-evaluation**: a final attribution of an attribution-dependent kind (#8 – always: "severity unchanged" is relative
   to the stored severity, which carries the rule overrides, so a rise of the event's own severity to an override's
   value reports no change), or one that changes the severity or adds a tag (`passwordReset`, `selfChange`,
   `syncAccount`), can make further rules match (shadow credentials on a user written by another account become High
   and must reach `tier0-security`; custom rules on `RequiredTags [passwordReset]`). The engine matches the event again
   with its final state (`RuleEngine.Evaluate`), plans only the rules that are not yet in the stored `matched_rules`
   (`IRuleMatchPlanner.PlanReevaluationAsync` – rules that already matched are never planned again, their rows may be
   delivered and deleted; targets that already got the final state are left out, 13.2 #1) and stores matched rules ∪
   new rules, the evaluated severity and the new pending rows in one `CommitEvaluationAsync`, **before** the attribution
   update: a pass interrupted in between leaves the event pending and the next pass finds the rules stored. Waiting rows
   of stored rules that no longer match the final state are dropped first – the dispatch job would drop them on release,
   and the store keeps one row per (event, target), so they would otherwise swallow the row of a rule that matches now
   (a custom rule excluding the actor must not silence `tier0-security` for the same recipients). The stored severity is
   the evaluated one, `max(final severity, overrides of the matching rules)` (12.1), also when nothing new matched – a
   re-resolved severity never drops a rule override. `NotificationsQueued` is raised only after the attribution update,
   so a dispatch it wakes renders the new rows with the final actor. An event the rules job has not evaluated yet has no matched rules, so all its
   final matches are planned and the job skips it afterwards. `rules_state` is never reset. The re-evaluation and the
   attribution update of one event and the rules job's read → match → plan → commit run in one in-process section
   (`RuleEngine.EnterEvaluationAsync`): both jobs wake on EventsStored, and without it the job could commit a match
   made with the pending attribution after the final one was stored (stale severity and matched rules, a rule planned
   twice).
10. `UpdateAttributionAsync(id, AttributionUpdate)`; when the attribution became final, the engine calls
   `FollowUpPlanner` directly (13.2 #5) and `INotificationQueueStore.AddAsync`; then `signals.Raise(AttributionUpdated)`.
   The store phase (#9, #10) runs without the stop token, and the follow-ups collected so far are queued (one call per
   pass) also when a later write of the pass fails.

### 11.5 Late enrichment and notifications

- Events are stored and evaluated immediately with `Attribution.Pending`.
- Notifications of severity < High, of attribution-dependent kinds (any severity) and of provisional actor matches are
  held until attribution is final or `Notifications.AttributionGrace` elapsed (then sent with "Wykonawca: ustalanie…" /
  "nieustalony (powód)"); on release the dispatch job re-runs the rule match with the final severity and tags (13.3).
- Other High/Critical are sent at once; when `FollowUpWhenAttributed`, a follow-up notification (same e-mail thread:
  `In-Reply-To`/`References`; Teams: a short card "Uzupełnienie: wykonawca ustalony"; webhook: event type
  `ad.attribution.updated` with the same `data.eventId`) is queued when attribution later resolves.
- The digest and the GUI always show the final attribution.

### 11.6 Event-log-only detections (`EventLogOnlyDetector`)

| Event | WatchEvent | Notes |
|---|---|---|
| 1102 (provider Microsoft-Windows-Eventlog, UserData `LogFileCleared`) | SecurityLogCleared (Critical), Object = DC computer, Actor from SubjectUserSid | also HealthEventLogGap for that DC |
| 1104 log full / 1100 service shutdown | HealthEventLogGap | |
| 4719 audit policy changed | AuditPolicyChanged (Critical) | Changes: SubcategoryGuid + AuditPolicyChanges tokens |
| 4794 DSRM password set | DsrmPasswordChanged (High) | Workstation from data |

Attribution of these events is Resolved at creation (the record itself is the evidence).

### 11.7 Reason codes when unresolved (shown with a one-line remedy in the GUI/e-mail)

| Reason | Condition (checked in this order) | Remedy text (pl) |
|---|---|---|
| SOURCE_DISABLED | attribution disabled or non-Windows host | „Włącz atrybucję (konfiguracja) – usługa musi działać na Windows.” |
| DC_UNREACHABLE | the originating DC's log could not be read during the window | „Sprawdź zaporę (Remote Event Log Management) i członkostwo konta usługi w Event Log Readers.” |
| LOG_OVERWRITTEN | oldest record time of the DC log > window end: records overwritten unread, a cursor regression, or a 1102/1104 of that DC at or after the window start | „Zwiększ rozmiar dziennika Security na DC (≥ 1 GB).” |
| AUDIT_POLICY_MISSING | no event of the kind's family from that DC in 7 days (`GetLastSeenAsync`) | „Włącz zasady inspekcji (docs/WATCHDOG.md, rozdz. 4.5).” |
| SACL_MISSING | the kind is only coverable by 5136/5137/5139/5141 and no such event was seen from that DC in 7 days | „Dodaj SACL dla zmian usług katalogowych (docs/WATCHDOG.md, rozdz. 4.6).” |
| AMBIGUOUS | two candidates within 5 points | „Kilka możliwych wykonawców – szczegóły w dowodach.” |
| NO_MATCH | otherwise | „Nie znaleziono pasującego zdarzenia w oknie czasowym.” |

Source-level reasons (`Attribution.SourceReason`, the actor may still be resolved):

| Reason | Condition | Remedy text (pl) |
|---|---|---|
| SOURCE_LOGON_NOT_FOUND | no 4624 for the subject's logon id within `LogonLookback` (or already overwritten) | „Sesja logowania starsza niż okno wyszukiwania lub dziennik nadpisany – zwiększ dziennik Security.” |
| SOURCE_NOT_COLLECTED | ForwardedEvents mode and 4624/4771/4776 are not in the subscription | „Dodaj 4624/4771/4776 do subskrypcji WEF (docs/WATCHDOG.md, rozdz. 4.4).” |

Coverage (`DcLogStatusDto.Coverage24h`, HealthAttributionDegraded) counts only attributable events – NotApplicable
kinds and lockout resets at logon (10.6) are excluded.

Health check `attribution.actorConditionsUnreliable` (Warning, computed by the AttributionEngine after each
correlation pass): an enabled rule uses actor conditions (`ExcludeActors`, `ExcludeSelfChanges`, actor exclusions) for
kinds that ended `SACL_MISSING` / `AUDIT_POLICY_MISSING` in the last 24 h – such exclusions cannot work (after the grace
period an unresolved actor excludes nothing, 12.1 #8). Shown in Status and next to the rule in the Rules tab (Args:
rule id, kind).

---

## 12. Rules (W-core)

### 12.1 Matching (`RuleMatcher`, `ScopeMatcher`, `ExclusionMatcher`)

A rule matches an event when **all** hold:

1. `Enabled`.
2. `Kinds` empty or contains `event.Kind`; `ObjectKinds` empty or contains `event.Object.Kind`.
3. Scope: `IncludeSubtrees` empty or `Object.Dn` / `PreviousDn` equal to or under one of them; neither under any
   `ExcludeSubtrees` (tokens expanded; `DistinguishedName.IsDescendantOf`/`AreEqual`).
4. `Attributes` empty or `event.Changes` contains one of them (case-insensitive).
5. `RequiredTags` ⊆ `event.Tags`; `AnyTags` empty or intersects.
6. `Groups` (only for GroupMemberAdded/Removed – the group is `Object` – and Privilege* – the group is `Member`):
   Privileged = the group is a privileged root (by SID/name/DN); WatchList = DN in `PrivilegedGroups.WatchListDns`;
   Explicit = DN in `GroupDns`. For other kinds the filter is ignored.
7. `MinSeverity` ≤ event severity (after SeverityResolver).
8. Actors: when attribution is Resolved and the actor's **SID** equals an `ExcludeActors` entry (SIDs only) → no
   match; an entry with a `{domainSid}`/`{rootDomainSid}` token whose value is unknown matches nothing (the
   any-domain relative-id fallback of 8.1 applies to privileged roots only – an exclusion fails closed).
   `ExcludeSelfChanges` and tag `selfChange` → no match – but never for a Tier-0 kind (12.6) written by the object
   itself (a relayed computer writing RBCD, SID history, SPNs or a primary group on itself is the attack): of the Tier-0
   kinds only KeyCredentialChanged of a computer (key rotation) and ServicePrincipalNameAdded of a service account (SPN
   self-registration) may be hidden, and not for a domain controller or a privileged object; such a non-excludable
   self-change does not make the match provisional either. While attribution is Pending and the rule has actor
   conditions, the match is **provisional**: the planner holds the notification until attribution is final or the
   grace period ends (also for High/Critical), then re-checks. After the grace period an unresolved actor excludes
   nothing (the notification is sent). Workstation names / IPs are never matching inputs.
9. No `Exclusions` entry applies. An exclusion applies when every field it sets matches: `ObjectGuid` = object or
   member GUID; `SubtreeDn` contains the object; `Actor` (SID) = actor SID; `Attribute`: the event's changes **without**
   this attribute are empty (the exclusion removes only that attribute from consideration). Expired exclusions
   (`ExpiresAt` < now) are ignored.

DN-based settings compare DN strings, so a renamed or moved OU/group makes them match nothing. Health check
`rules.configuredDns` (Warning, `ConfiguredDnMonitor`, run by the rules job at most every 10 min per configuration):
every configured DN – rule scopes, `GroupDns`, exclusion subtrees, `privilegedGroups.additionalCriticalDns` /
`additionalHighDns` / `watchListDns`, `directory.criticalObjects`, service-account and disabled-account subtrees,
wake-up containers – is resolved against the snapshot; the ones that no longer exist are listed (Args, as configured).
DNs outside the default NC, under `CN=System` or under an excluded subtree, and `CN=Builtin` itself (a builtinDomain
object, not a container – its groups are snapshotted) are not snapshotted and not checked; nothing is reported before
the baseline; the service never rewrites the configuration. The GUI shows the check as „Nieistniejące DN w
konfiguracji” (health codes `rules.configuredDns` and `attribution.collector` are localized).

`RuleEngine.Evaluate(event)` returns all matching rules; `EventEvaluation.Severity = max(event.Severity, every
matched rule's Severity override)`; a rule's own notifications use `rule.Severity ?? event.Severity` (a rule may
deliberately lower its notifications). Health events always match the locked `health` rule.

### 12.2 Severity resolution (`SeverityResolver`)

Order (MUST): **(1)** `base = EventKindCatalog.DefaultSeverity(kind, objectKind)` (ObjectDeleted per object kind;
AccountFlagsChanged / DelegationChanged per bit table 10.6.1; PrivilegeGranted per tier; KeyCredentialChanged per
10.6.4); **(2)** tag bumps – never lowering; **(3)** attribution-dependent re-resolution when the attribution is final
(11.4 #8); **(4)** explicit per-kind overrides – applied last, may lower (krbtgt).

| # | Tag / condition | Kinds | Result |
|---|---|---|---|
| 2 | privileged or serviceAccount | AccountDisabled, AccountEnabled, AccountLockedOut, PasswordMustChangeSet, PasswordAgeReset, AccountFlagsChanged, AccountExpirationChanged, AccountNameChanged, ObjectDeleted, ObjectMoved, ObjectRenamed | ≥ High |
| 2 | privileged or serviceAccount, **and** the changed attributes intersect the security-relevant set `scriptPath`, `profilePath`, `homeDirectory`, `homeDrive`, `userWorkstations`, `logonHours`, `servicePrincipalName`, `msDS-SupportedEncryptionTypes`, `userCertificate` | AttributesChanged | ≥ High (personal/HR fields such as department, title, phone stay Info – nightly HR syncs never page) |
| 2 | unexpectedContainer, createdByNonAdmin, server | *Created | ≥ Medium (`defaultContainer` alone: no bump) |
| 2 | longDisabled or disabledOu | AccountEnabled | High |
| 2 | disabledOu (moved out of) | ObjectMoved | High |
| 2 | privileged or serviceAccount and DaysLeft ≤ 7 | PasswordExpiring, AccountExpiring | High |
| 2 | group SID in ProtectRemovalSids | GroupMemberRemoved | High |
| 2 | adminCountOrphan | any | no bump (digest hygiene only) |
| 2 | reconciliation | any | unchanged |
| 3 | privileged or serviceAccount | PasswordChanged | provisional High; final: High for tag `passwordReset` (4724) or an attribution ending Unknown/Ambiguous; **Info** for `selfChange` (4723 – the user's own change at expiry) |
| 3 | principal not in PrivilegeState | AdminCountSet | provisional High; final: Info when the actor is SYSTEM (SDProp) |
| 3 | user, not privileged | KeyCredentialChanged | provisional Medium; final: High when the actor is resolved and is neither the user nor tag `syncAccount` |
| 4 | krbtgt | PasswordChanged | exactly Medium (expected: two resets ~10 h apart) |

Kinds with step-3 rules (`SeverityResolver.AttributionDependentKinds`) are held by the planner until the attribution is
final or the grace period ends (13.2), and re-matched with the final severity on release (13.3).

### 12.3 Exclusion hygiene

Exclusions carry reason, creator, creation time and optional expiry; they are part of the audited configuration.
Actors are SIDs only. Exclusions on rules whose id starts with `tier0-` produce a validation warning and are listed in
every digest ("Aktywne wyjątki"). Every configuration save raises `WatchdogConfigurationChanged` (17.2).
Suggestions (`ExclusionSuggestionDto`: detected sync accounts for `tier0-security`, computer clean-up accounts for
`bursts`) are shown in the Rules tab and never applied automatically.

### 12.4 Built-in rules (`DefaultRules`) – shipped enabled unless noted

| Id | Name (pl) | Kinds | Other conditions | Delivery |
|---|---|---|---|---|
| `tier0-privilege` | Dostęp uprzywilejowany | PrivilegeGranted, PrivilegeRevoked | – | immediate → it-admins, Always; digest |
| `tier0-security` | Atrybuty krytyczne dla bezpieczeństwa | AdminCountSet, SidHistoryChanged, DelegationChanged, ServicePrincipalNameAdded, KeyCredentialChanged, PrimaryGroupChanged, SecurityDescriptorChanged, ComputerNameSpoofing, ManagedAccountLinkChanged, CertificateMappingChanged, ManagedPasswordReadersChanged, ServiceAccountCreated (dMSA) | MinSeverity High; `ExcludeSelfChanges` = true (hides only computer key rotation and service-account SPN self-registration, 12.1 #8) | immediate → it-admins, Always; digest |
| `security-review` | Zmiany bezpieczeństwa do przeglądu | the tier0-security kinds | – (catches Low/Medium, e.g. device key self-registration) | digest only |
| `tier0-domain` | Domena, zaufania, schemat, PKI, zasady | DomainPolicyChanged, PasswordSettingsChanged, TrustChanged, DomainControllerAdded, DomainControllerRemoved, FsmoRoleChanged, CertificateTemplateChanged, SchemaChanged, GroupPolicyLinkChanged | MinSeverity High | immediate → it-admins, Always; digest |
| `tier0-eventlog` | Dzienniki zabezpieczeń DC | SecurityLogCleared, AuditPolicyChanged, DsrmPasswordChanged | – | immediate → it-admins, Always; digest |
| `bursts` | Zdarzenia masowe | MassLockout, MassDeletion, PasswordSprayingSuspected | – | immediate → it-admins, Default (High/Critical always, MassLockout Medium in business hours); digest |
| `privileged-accounts` | Konta uprzywilejowane i serwisowe | all Account kinds + PasswordExpiring + ObjectDeleted/Moved/Renamed | AnyTags [privileged, serviceAccount], MinSeverity High | immediate → it-admins, Always; digest |
| `new-accounts` | Nowe konta | UserCreated, ServiceAccountCreated | – | immediate → it-admins, Default (business hours for < High), aggregation 5 min; digest |
| `new-computers` | Nowe komputery | ComputerCreated | – | digest only |
| `computers-unusual` | Komputery spoza procesu | ComputerCreated | AnyTags [createdByNonAdmin, unexpectedContainer, server] | immediate → it-admins, Default; digest |
| `deletions` | Usunięcia i przywrócenia | ObjectDeleted, ObjectRestored, ObjectDisappeared | – | immediate → it-admins, Default; digest |
| `account-status` | Włączenia i wyłączenia kont | AccountDisabled, AccountEnabled | – | digest only |
| `lockouts` | Blokady kont | AccountLockedOut | – | immediate → helpdesk, BusinessHours, aggregation 5 min, max 10/h, **RepeatSuppression 1 h** per account; digest (one row per account per day: "zablokowane 5× dziś, ostatnio 10:42") |
| `unlocks` | Odblokowania | AccountUnlocked, LockoutExpired | – | digest only |
| `passwords` | Hasła | PasswordChanged, PasswordMustChangeSet | – | digest only (resets / own changes counted separately) |
| `account-flags` | Flagi konta | AccountFlagsChanged, PasswordAgeReset | MinSeverity Medium | immediate → it-admins, Default; digest |
| `account-names` | Zmiany nazw kont | AccountNameChanged | – | immediate → it-admins, Default; digest |
| `logon-settings` | Skrypty, profile, ograniczenia logowania | AttributesChanged | Attributes [scriptPath, profilePath, homeDirectory, homeDrive, userWorkstations, logonHours], Severity Medium | immediate → it-admins, Default; digest |
| `account-attributes` | Zmiany parametrów kont | AttributesChanged, AccountExpirationChanged, ObjectMoved, ObjectRenamed, ObjectAppeared | – | digest only |
| `group-changes` | Grupy i członkostwo | GroupMemberAdded, GroupMemberRemoved, GroupCreated, GroupTypeChanged | – | digest only |
| `watchlist-groups` | Grupy obserwowane | GroupMemberAdded, GroupMemberRemoved | Groups WatchList, Severity Medium | immediate → it-admins, Default; digest (inactive until WatchListDns is filled) |
| `ou-gpo` | OU i GPO | OrganizationalUnitCreated, GroupPolicyLinkChanged, GroupPolicyChanged | – | digest only |
| `expiry` | Terminy ważności | AccountExpiring, AccountExpired, PasswordExpiring | – | immediate → helpdesk, BusinessHours, aggregation 60 min; digest; `NotifyManager` false |
| `health` | Stan usługi Watchdog | all Health* kinds incl. WatchdogConfigurationChanged | **Locked** | immediate → it-admins **and every enabled Teams/webhook channel** (`AllEnabledChannels`), Always; digest |

`ContactCreated`, `LockoutExpired` and `PasswordSprayingSuspected` are not recorded by default (catalog). `Test` never
matches rules.

### 12.5 Rollout mode (`RolloutMode`)

Active while `!General.RolloutEnded && General.RolloutDays > 0 && now < BaselineCompletedAt + RolloutDays` (no
service-initiated config write – the end time derives from the baseline). While active, the `NotificationPlanner`
creates immediate notifications only for rules whose id starts with `tier0-`, for `bursts` and for `health`; every other
match goes to the digest only, and `RulePreviewService` reports per rule "wysłałaby N wiadomości" since the baseline.
`StatusDto.RolloutModeUntil` drives a banner in the Rules tab ("Tryb wdrożenia do …: powiadomienia natychmiastowe tylko
dla zdarzeń krytycznych – dostosuj reguły i zakończ tryb"); the admin ends it early with `General.RolloutEnded = true`.
The baseline itself emits no events, so the rollout week is the first period with preview data.

### 12.6 Tier-0 catalog (`Tier0Catalog`)

Kinds that `Directory.DisabledKinds` may not contain and attributes that `Directory.IgnoredAttributes` may not contain
without `Authorization.AllowTier0Suppression` (8.3 `tier0Suppression`): every kind of the categories Security, Domain
and AuditLog, plus PrivilegeGranted/PrivilegeRevoked, AccountFlagsChanged and DelegationChanged; the attributes
consumed by them in the ChangeClassifier table (10.6): `userAccountControl`, `member`, `primaryGroupID`, `sIDHistory`,
`adminCount`, `servicePrincipalName`, `msDS-KeyCredentialLink`, `msDS-AllowedToDelegateTo`,
`msDS-AllowedToActOnBehalfOfOtherIdentity`, the dMSA link attributes, `altSecurityIdentities`,
`msDS-GroupMSAMembership`, `nTSecurityDescriptor`, `sAMAccountName`, `dNSHostName` (ComputerNameSpoofing), `gPLink`,
`gPOptions`, the GPO content attributes `versionNumber` and `gPCFileSysPath` (GroupPolicyChanged), `fSMORoleOwner`, the
domain policy, PSO, trust and PKI attributes of 9.2 – every attribute of the 9.2 sets of the Tier-0 object kinds except
`displayName` (a test guards against drift).

---

## 13. Notification pipeline (W-core; transports W-service)

### 13.1 `RuleEvaluationJob` (wake: EventsStored, AttributionUpdated, ConfigChanged; interval 30 s)

```
enter RuleEngine.EnterEvaluationAsync (shared with the AttributionEngine's re-evaluation, 11.4 #9)
events = eventStore.GetUnevaluatedAsync(500)
for each: matches = RuleEngine.Evaluate(e); pendings += NotificationPlanner.Plan(e, matches, now)
eventStore.CommitEvaluationAsync(evaluations, pendings); leave; signals.Raise(NotificationsQueued)
```

Rules matched only after a final attribution (raised severity, added tags) are planned by the AttributionEngine
(11.4 #9).

### 13.2 `NotificationPlanner`

For every match with `Delivery.Immediate`:
0. Rollout mode (12.5) active and the rule is not `tier0-*` / `bursts` / `health` → no immediate notification
   (digest only; counted for the rollout banner).
1. Targets: each recipient group → one `NotificationTarget(Email, "email", group addresses, group DetailLevel)`
   (skipped when SMTP is disabled or the group is empty); each channel id – plus every enabled Teams/webhook channel when
   `AllEnabledChannels` – → `NotificationTarget(Teams|Webhook, id, [], channel DetailLevel)` (skipped when the channel
   is disabled); `NotifyManager` (expiry kinds) → the object's manager (snapshot `manager` → that object's `mail`) as an
   e-mail target with DetailLevel Minimal. Targets are de-duplicated across rules by (Target.Key, EventId) – the first
   rule (config order) owns the pending row. For the rules added by the re-evaluation after attribution (11.4 #9,
   `PlanReevaluationAsync`) a target is also left out when the event already has an alert for it that is not Dead or
   Cancelled, has at least the planned severity and carries the final state (sent with the attribution final, or getting
   the follow-up of #6); a higher severity, or an alert sent with the actor pending and no follow-up to come, is new
   information and the added rule notifies.
2. Delivery hours: `Default` → Always for severity ≥ High, else BusinessHours. BusinessHours and
   `BusinessCalendar.IsBusinessTime(now)` false → **no immediate notification** (the event still goes to the digest).
3. Repeat suppression: `RepeatSuppression` set and `outbox.WasNotifiedAboutObjectAsync(ruleId, objectGuid, now −
   RepeatSuppression)` → no immediate notification (digest only; the next one after the period carries
   `LockoutsToday`).
4. Attribution hold: (severity < High or the kind is attribution-dependent, 12.2) and attribution Pending →
   `HoldForAttributionUntil = DetectedAt + AttributionGrace`; provisional actor matches (12.1 #8) hold for any severity.
5. Aggregation: `BucketKey = ruleId|target.Key` (webhooks: `eventId|target.Key` – one POST per event);
   window = `rule.Delivery.AggregationWindow ?? Notifications.AggregationWindow`, but `HighSeverityFlushDelay` for
   severity ≥ High; `DueAt = now + window`.
6. Follow-ups (`FollowUpPlanner`, called by the AttributionEngine when an attribution becomes final, 11.4 #9): for
   every message from `outbox.GetMessagesForEventAsync(eventId)` with `SentWithPendingAttribution`, severity ≥ High and
   `FollowUpWhenAttributed`, add a pending row for the original `TargetKey` with `IsFollowUp`, `ReplyToMessageId` =
   that message's `MessageId`, DueAt = now (`INotificationQueueStore.AddAsync`, duplicates ignored). The dispatch job
   also plans them right after it stored an alert whose events turned final while it was rendered (13.3), and sends a
   follow-up only after its original was Sent (none for a Dead/Cancelled original).

`WatchdogConfigurationChanged` is planned twice (17.2): synchronously against the **previous** configuration by
`ConfigChangeNotifier` (`RuleEngine.Evaluate` and `NotificationPlanner.Plan` take an explicit configuration argument
for this; the default overloads use `IWatchdogConfigProvider.Current`), then normally against the new one;
`(event, target, follow-up)` uniqueness removes duplicates.

### 13.3 `NotificationDispatchJob` (wake: NotificationsQueued, AttributionUpdated; interval 10 s)

```
pendings = queueStore.GetAllAsync(); group by BucketKey
for each bucket with min(DueAt) ≤ now:
    ready = items whose hold expired or whose event attribution is final
    re-match held items with the event's final state (severity, tags, actor): RuleMatcher for that rule, delivery
        hours and rollout recomputed → drop items that no longer qualify (queueStore.DropAsync)
    other non-follow-up items: delivery hours rechecked → outside them (the aggregation window or throttling ran past
        the end of business hours) dropped – the digest reports them
    follow-up bucket: items whose event already has a FollowUp message for the target → dropped (duplicate); original
        message (ReplyToMessageId) Dead/Cancelled → dropped; Pending/Sending/Retrying → RescheduleAsync(now + 1 min)
        (a follow-up never overtakes its original); Sent or unknown (purged) → rendered
    if ready is empty: continue
    if severity < Critical and ThrottlePolicy.IsExceeded(rule, target, now):   // max(sent, enqueued) in the last hour ≥ cap
        queueStore.RescheduleAsync(bucket ids, now + 15 min) (items keep accumulating); continue
    take up to MaxItemsPerMessage (oldest first); render (14) → OutboxMessage (MessageId = Guid.CreateVersion7().ToString("N"),
        TargetKey, SentWithPendingAttribution = any rendered event still Pending)
    queueStore.CompleteAsync(ids, message)         // items beyond the cap: "+N kolejnych" line, all ids completed
    ThrottlePolicy.RecordEnqueued(rule, channel, now)
    alert with SentWithPendingAttribution: events whose attribution became final since they were read → their
        FollowUpPlanner rows for this message → queueStore.AddAsync (the AttributionEngine planned before the message
        existed; whichever side plans second is ignored by the uniqueness or dropped as a duplicate at flush)
signals.Raise(OutboxEnqueued)
```

The hourly cap counts the larger of the messages **sent** in the last hour (`CountSentAsync`, survives a restart) and
the messages **enqueued** in the last hour (kept in memory by the `ThrottlePolicy` singleton), so a channel that cannot
deliver does not let a burst queue one message per flush that floods the recipients after the recovery.

Rendering choice: e-mail 1 item → alert, > 1 → aggregated alert; Teams 1 → alert card, > 1 → burst card (digest
layout, title "N zdarzeń: <rule>"); webhook → one payload per event; follow-up → follow-up template.

### 13.4 Digest (`DigestJob`, `DigestBuilder`, `Schedule`)

- Schedule: `Schedule.NextOccurrence(Digest.Time, Digest.Days, tz, now)`; DST: a non-existent local time moves to
  the next valid minute, an ambiguous one uses the first occurrence. After a downtime the missed digest is sent at
  start-up when it is less than 12 h late, otherwise skipped; a digest counts as missed only when the last recorded
  period ended more than `MaxEvaluationLag` (10 min) before the scheduled time (a digest whose end was held back for an
  unevaluated event, see Period, was sent – a restart must not send it again). `IDigestRunStore` (`digest_runs`)
  stores the period and the outbox ids. Once the first message of a digest is enqueued, the remaining messages and the
  `digest_runs` row are written without cancellation (a service stop in between would make the start-up catch-up send
  the digest again); when recording fails after an enqueue, the error is logged and the schedule moves on anyway (the
  next digest then starts at the previous end instead of this digest being enqueued every minute).
- Period: from the previous digest's end (first run: now − 24 h) to now – or to just before the oldest event the
  rule evaluation has not processed yet or whose attribution is still pending, when it was detected within the last
  10 min (`DigestBuilder.MaxEvaluationLag`; sections list only rule-matched events, and the final attribution can still
  change severity, tags and matched rules (11.4 #9), so such an event goes to the next digest with its final state
  instead of being counted now and never listed again – e.g. shadow credentials that turn High after the digest would
  miss "Wymaga uwagi"; an older backlog does not hold the period back). With `CoverNonBusinessDays` (default), the
  digest of the **first business day after non-business days** (Monday after a weekend, the day after a holiday)
  covers everything since the end of the last business-day digest's period (e.g. Friday 07:30 → Monday 07:30) in every
  section; the weekend digests are still sent (heartbeat) with their own 24 h periods. The extension applies only while
  the previous digest ended on a non-business day (or there was none): a send-now after the Monday digest, or a
  weekday-only schedule, simply starts where the previous digest ended. The end of the last business-day digest is
  the one the job remembers (e.g. a late Friday catch-up at 10:00), after a restart the scheduled time of that day.
- Preview (`IDigestService.PreviewAsync`, served to Viewers): "Aktywne wyjątki" shows only the number of active
  exceptions (`Digest.Fact.ActiveExceptionsHidden`) – exclusions are Admin configuration (17.2) and the API passes only
  the caller's detail level; the sent digest lists them for its recipients.
- Content (`DigestModel`): header (instance, domain, period, health line: DC, last poll, attribution coverage,
  log gaps, broken channels, rollout mode); counts (critical/high, new users, new computers, deleted, disabled/enabled,
  lockouts, password resets (tag passwordReset), own password changes, password age resets, privileged changes,
  GPO/domain changes); **"Wymaga uwagi"** = all events ≥ High with acknowledged/unacknowledged state; sections (only
  events matched by a rule with `IncludeInDigest`, plus health): Nowe konta · Nowe komputery (grouped by parent OU,
  unusual ones flagged, `defaultContainer` flag) · Usunięte / przywrócone · Wyłączone / włączone · Przeniesione /
  zmiany nazw · Dostęp uprzywilejowany · Członkostwo w grupach · Zmiany parametrów kont (grouped per object: attribute
  old → new, who, when) · Blokady (**one row per account and day**: "zablokowane 5× dziś, ostatnio 10:42", top bad
  password sources) · Hasła (resets / own changes / age resets counted separately; privileged listed) · Domena, zasady,
  GPO, PKI · Zmiany bezpieczeństwa do przeglądu (security-review) · Nadchodzące terminy (`GetUpcomingAsync`, ≤
  UpcomingExpiryDays) · on `WeeklySectionsDay`: Nieaktywne konta (10.17) and hygiene counters (live LDAP counts:
  PASSWD_NOTREQD, DONT_EXPIRE_PASSWORD, unconstrained delegation on non-DCs, `adminCountOrphan` (adminCount=1 not in
  PrivilegeState), krbtgt and RID-500 password age > 180 days) · Aktywne wyjątki · Zmiany konfiguracji Watchdog.
- Rows per section ≤ `MaxRowsPerSection`, then "… i N kolejnych – pełna lista w zakładce Watchdog".
- Recipients: `Digest.RecipientGroupIds` (e-mail, full digest) and `Digest.ChannelIds` (Teams: summary card within the
  24 KiB budget; webhook: `watchdog.digest` payload with counts + event ids when `IncludeDigest`).
- `SendWhenEmpty` → "Brak zmian w okresie" digest (heartbeat; keeps Teams flows from the 90-day inactivity suspension).

### 13.5 `BusinessCalendar` and `PolishHolidays`

`IsBusinessTime(now)`: local time (configured zone, `TimeZoneResolver`) on a configured day, `Start ≤ time < End`, not
a holiday. `IsBusinessDay(date)` and `PreviousBusinessDay(date)` serve the expiry scanner and the digest period.
`PolishHolidays.Get(year)`: 1 Jan, 6 Jan, Easter Sunday, Easter Monday, 1 May, 3 May, Pentecost (Easter + 49),
Corpus Christi (Easter + 60), 15 Aug, 1 Nov, 11 Nov, 24 Dec (statutory since 2025), 25 Dec, 26 Dec; Easter by the
anonymous Gregorian algorithm (tests: 2026-04-05, 2027-03-28). `ExtraHolidays` added.

### 13.6 `HeartbeatJob`

For every enabled webhook channel with `HeartbeatInterval`: every interval a `watchdog.heartbeat` payload (instance,
service version, directory state, last poll, pending outbox count – no directory values) is enqueued as
`DeliveryKind.Health`. A SIEM alerts when it stops arriving. Teams uses the daily digest as heartbeat. A heartbeat is
never retried past its interval – the next one supersedes it (15.1, `heartbeat.superseded`).

---

## 14. Formatters (W-core) – outputs

### 14.1 Common rules

- Text from `Resources/NotificationStrings(.pl).resx` through `NotificationText` with an explicit `CultureInfo`
  (never the thread culture). Keys: `Kind.{Kind}.Title`, `Kind.{Kind}.TitlePlural`, `Kind.{Kind}.Sentence`
  (format with placeholders), `Severity.{Severity}`, `Attr.{ldapName}` (display names of every watched attribute, pl
  like the Polish ADUC), `Reason.{code}` + `Reason.{code}.Remedy`, `Flag.{UAC_NAME}`, `Digest.*`, `Email.*`, `Card.*`.
  A resx test asserts both cultures contain every key generated from the enums/catalogs.
- Polish sentences avoid grammatical gender by using the passive voice ("Konto … zostało dodane do grupy …").
- Times: configured time zone; pl `dd.MM.yyyy HH:mm:ss`, en `yyyy-MM-dd HH:mm:ss`, followed by the UTC offset; the
  e-mail body also shows UTC.
- `DetailLevelFilter`: **Minimal** – attribute names only (no values), no workstation/IP, no Via path DNs (names
  only); **Standard** – values except personal-data attributes (`telephoneNumber`, `mobile`, `employeeID`,
  `employeeNumber`, `employeeType`, `info`, `physicalDeliveryOfficeName`, `homeDirectory`, `logonHours`) shown as
  "zmieniono"; **Full** – all non-secret values. Secret/confidential (`SecretAttributes.IsSecret`) and `Redacted`
  changes are always "zmieniono (wartość ukryta)" / "changed (value hidden)". Values longer than `MaxValueLength`
  end with "…"; multi-valued: added/removed counts + first 3 values. The same filter is applied to API responses and
  the HTML event page for non-Admin callers (`Authorization.ViewerDetailLevel`, default Standard).
- All directory strings are untrusted. **Before** any formatter runs, `UntrustedText` (called by
  `DetailLevelFilter`) (a) replaces bidi controls (U+202A–202E, U+2066–2069, U+200E/200F, U+061C), zero-width and
  other Unicode Cf/Cc characters (except TAB in multi-line values) with visible escapes such as `<U+202E>`, and (b)
  defangs URL-like tokens – `scheme://…` becomes `hxxps://…`/`hxxp://…`/`fxp://…` with the dots of its host as `[.]`,
  and `www.` tokens become `www[.]` – so Outlook/OWA/Teams never auto-link attacker-controlled text (description, info,
  object/group names in Via paths). Plain host names, UPNs and e-mail addresses stay readable. Then: HTML-encoded in
  e-mail; placed only in `TextRun` (plain text) in Adaptive Cards; JSON-escaped in webhooks; CR/LF and control
  characters stripped from subjects and headers.
- Source computer: the IP address is shown first; names carry their origin ("WS-123 (zgłoszona przez klienta)",
  "(DNS)") and the source confidence; for AccountLockedOut the row is "Źródło błędnych haseł" (sources with counts)
  and there is no "Kto" row.

### 14.2 E-mail subjects (`EmailSubjectBuilder`)

| Case | pl | en |
|---|---|---|
| single | `[AD Watchdog][KRYTYCZNE] Dodano do grupy uprzywilejowanej: Jan Kowalski (jan.kowalski) → Domain Admins – CONTOSO\adm.nowak` | `[AD Watchdog][CRITICAL] Privileged access granted: Jan Kowalski (jan.kowalski) → Domain Admins – CONTOSO\adm.nowak` |
| single, actor pending | `[AD Watchdog][NISKI] Nowe konto: Zofia Żak (zofia.zak) – wykonawca: ustalanie…` | `[AD Watchdog][LOW] New account: Zofia Żak (zofia.zak) – actor: pending…` |
| aggregated | `[AD Watchdog][NISKI] Nowe konta: 25 (OU=Sprzedaż) – CONTOSO\adm.nowak` | `[AD Watchdog][LOW] New accounts: 25 (OU=Sprzedaż) – CONTOSO\adm.nowak` |
| follow-up | `Re: <original subject>` | same |
| digest | `[AD Watchdog] Raport dzienny contoso.com – 14.10.2026: 71 zdarzeń (2 wymagają uwagi)` | `[AD Watchdog] Daily report contoso.com – 2026-10-14: 71 events (2 need attention)` |
| test | `[AD Watchdog] Wiadomość testowa` | `[AD Watchdog] Test message` |

Prefix = `Notifications.SubjectPrefix`; severity words pl: INFO, NISKI, ŚREDNI, WYSOKI, KRYTYCZNE; the object part is
cut so that the subject stays ≤ 200 characters; the key facts come first (mobile).

### 14.3 E-mail body (`EmailAlertFormatter`, `EmailDigestFormatter`, `HtmlWriter`)

MIME (built by the Service's `MimeMessageFactory`): `multipart/alternative` (text/plain + text/html, UTF-8; MimeKit
chooses 8bit/base64), headers `Message-ID: <{MessageId}@{LocalDomain}>`, `Auto-Submitted: auto-generated`,
`X-Auto-Response-Suppress: All`, `X-AdMng-Severity`, `X-AdMng-Rule`, `X-AdMng-Event-Id` (first 20 event GUIDs),
`In-Reply-To`/`References` for follow-ups. HTML: inline CSS only, no images, no scripts, max width 720 px, table
layout for Outlook.

Single alert (pl) – structure:

```
┌ banner (Critical #b00020 / High #d83b01 / Medium #ca5010 / Low #0f6cbd / Info #616161, white text)
│ KRYTYCZNE · Dostęp uprzywilejowany
└
Konto Jan Kowalski (jan.kowalski) uzyskało dostęp do grupy Domain Admins
(członkostwo pośrednie: GG_Admins_Serwery → Domain Admins).

 Co        Dodano do grupy uprzywilejowanej (reguła: Dostęp uprzywilejowany)
 Obiekt    Jan Kowalski (jan.kowalski) · Firma › Pracownicy › IT
 Kto       CONTOSO\adm.nowak (Anna Nowak) · pewność: wysoka
 Skąd      10.0.12.34 · WS-ADM-01 (DNS) · logowanie sieciowe (4624) · pewność: niska
 Kiedy     14.10.2026 14:32:05 (UTC+02:00) · 12:32:05 UTC
 Gdzie     DC01.contoso.com (zdarzenie 4728, rekord 918273)

 Zmiany                      Przed                Po
 department                  Sprzedaż             Księgowość
 userAccountControl          512 (NORMAL)         514 (+ACCOUNTDISABLE)
 unicodePwd                  zmieniono (wartość ukryta)

 Otwórz w przeglądarce: https://admng-wd.contoso.com:8443/events/0192f3a4-…   (only with PublicBaseUrl)
 W AD_MNG: Narzędzia → Watchdog → zdarzenie nr 18342

 ── Reguła tier0-privilege · zdarzenie 18342 · usługa admng-wd.contoso.com · wiadomość automatyczna, nie odpowiadaj
```

Unresolved actor row: `Kto  nieustalony – Nie znaleziono pasującego zdarzenia w oknie czasowym (NO_MATCH)` plus the
remedy line; pending: `ustalanie… (uzupełnienie zostanie wysłane)`.
Aggregated alert: one-sentence summary ("25 nowych kont w OU=Sprzedaż, wykonawca CONTOSO\adm.nowak"), then a table
(Czas · Obiekt · Zmiana · Kto · Skąd), max `MaxItemsPerMessage` rows, "+N kolejnych".
English uses the same layout with the en resx: banner "CRITICAL · Privileged access", sentence "The account Jan
Kowalski (jan.kowalski) was granted membership of Domain Admins (nested: GG_Admins_Serwery → Domain Admins).", labels
What / Object / Who / From / When / Where, "Changes · Before · After", "changed (value hidden)", "Who: unknown –
<reason>", "pending… (a follow-up will be sent)", footer "Rule … · event … · service … · automated message, do not
reply".
Text alternative: the same content as aligned "Label: value" lines, ≤ 100 characters per line.

### 14.4 Teams (Power Automate Workflows) – `TeamsAlertCardFormatter`, `TeamsDigestCardFormatter`, `CardBudget`

- Body = `{"type":"message","attachments":[{"contentType":"application/vnd.microsoft.card.adaptive","contentUrl":null,"content":<card>}]}`,
  **exactly one attachment per POST**, card `"version": "1.4"` (setting `CardVersion`), `"msteams":{"width":"Full"}`.
- Element subset (AC 1.4): Container (style `attention` for Critical/High, `warning` Medium, `accent` Low, `default`
  Info), ColumnSet/Column, TextBlock (watchdog-generated text only), FactSet (watchdog-generated only: counts, rule
  names, times), RichTextBlock/TextRun (**every AD-sourced string** – TextRun is plain text by schema, TextBlock and
  Fact values render Markdown links), Action.ToggleVisibility, Action.OpenUrl (only with PublicBaseUrl). No Table, no
  `style: heading`, no Action.Submit.
- Show at most 6 change rows inline, the rest in a hidden container toggled by "Pokaż wszystkie zmiany".
- Serialize with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` (HTTP body, never embedded in HTML) and enforce
  **24 576 bytes** (`Encoding.UTF8.GetByteCount`) after serialization: drop the last row of the largest section until
  it fits, always keep the header, counts and "Pokazano X z Y".
- `fallbackText`: one line "AD Watchdog: <title> <object> – <actor>".

Alert card (pl, abbreviated):

```json
{"type":"message","attachments":[{"contentType":"application/vnd.microsoft.card.adaptive","contentUrl":null,"content":{
 "$schema":"http://adaptivecards.io/schemas/adaptive-card.json","type":"AdaptiveCard","version":"1.4","msteams":{"width":"Full"},
 "fallbackText":"AD Watchdog: Zmiana parametrów konta Łukasz Wiśniewski (lwisniewski) – CONTOSO\\adm.jkowalska",
 "body":[
  {"type":"Container","style":"warning","bleed":true,"items":[
    {"type":"TextBlock","text":"Zmiana parametrów konta","size":"Large","weight":"Bolder","wrap":true},
    {"type":"RichTextBlock","inlines":[{"type":"TextRun","text":"Łukasz Wiśniewski (lwisniewski)","weight":"Bolder"}]},
    {"type":"TextBlock","text":"contoso.com · reguła: Zmiany parametrów kont · 14.10.2026 09:12:03 (UTC+02:00)","isSubtle":true,"spacing":"None","wrap":true}]},
  {"type":"ColumnSet","spacing":"Medium","columns":[
    {"type":"Column","width":1,"items":[{"type":"TextBlock","text":"Kto","weight":"Bolder"}]},
    {"type":"Column","width":3,"items":[{"type":"RichTextBlock","inlines":[{"type":"TextRun","text":"CONTOSO\\adm.jkowalska (Jadwiga Kowalska)"}]}]}]},
  {"type":"ColumnSet","spacing":"Small","columns":[
    {"type":"Column","width":1,"items":[{"type":"TextBlock","text":"Skąd","weight":"Bolder"}]},
    {"type":"Column","width":3,"items":[{"type":"RichTextBlock","inlines":[{"type":"TextRun","text":"10.0.12.34 · WS-ADM-01 (DNS) · zdarzenie 4738 na DC01"}]}]}]},
  {"type":"TextBlock","text":"Zmiany (2)","weight":"Bolder","spacing":"Large","separator":true},
  {"type":"ColumnSet","spacing":"Small","columns":[
    {"type":"Column","width":2,"items":[{"type":"TextBlock","text":"Dział","wrap":true}]},
    {"type":"Column","width":3,"items":[{"type":"RichTextBlock","inlines":[{"type":"TextRun","text":"Sprzedaż","strikethrough":true,"isSubtle":true}]}]},
    {"type":"Column","width":3,"items":[{"type":"RichTextBlock","inlines":[{"type":"TextRun","text":"Księgowość"}]}]}]}],
 "actions":[{"type":"Action.OpenUrl","title":"Otwórz w AD Watchdog","url":"https://admng-wd.contoso.com:8443/events/0192f3a4-5b6c-7d8e-9f00-112233445566"}]}}]}
```

Digest card: header container (style `emphasis`), FactSet with counts, per section up to 10 rows (ColumnSet: time ·
object TextRun · change TextRun · actor TextRun), "… i N kolejnych", "Pokazano X z Y zdarzeń", OpenUrl to the
service when configured.

### 14.5 Generic webhook – `WebhookPayloadFormatter`, `WebhookEventTypes`, `StandardWebhookSigner`

Event type = `ad.<object>.<action>` (lower snake): object ∈ user, computer, group, ou, contact, service_account,
domain, gpo, trust, dc, pso, schema, pki, forest; action from the kind (`created`, `deleted`, `restored`, `moved`,
`renamed`, `appeared`, `disappeared`, `disabled`, `enabled`, `locked_out`, `unlocked`, `lockout_expired`,
`password_changed`, `password_age_reset`, `password_must_change`, `flags_changed`, `expiration_changed`,
`name_changed`, `attributes_changed`, `member_added`, `member_removed`, `type_changed`, `admincount_set`,
`sidhistory_changed`, `delegation_changed`, `spn_added`, `keycredential_changed`, `primarygroup_changed`,
`acl_changed`, `name_spoofing`, `managed_account_link_changed`, `certificate_mapping_changed`,
`password_readers_changed`, `policy_changed`, `link_changed`, `added` / `removed` (dc), `fsmo_changed`,
`template_changed`, `expiring`, `expired`, `password_expiring`); Privilege* → `ad.privilege.granted|revoked`; bursts →
`ad.burst.mass_lockout|mass_deletion|password_spraying`; event-log kinds →
`ad.dc.security_log_cleared|audit_policy_changed|dsrm_password_changed`; health → `watchdog.health.<snake kind without
Health>`; WatchdogConfigurationChanged → `watchdog.configuration_changed`; heartbeat → `watchdog.heartbeat`; digest →
`watchdog.digest`; test → `watchdog.test`; follow-up → `ad.attribution.updated`.

Body (UTF-8, compact, signed bytes = sent bytes):

```json
{"type":"ad.user.attributes_changed","timestamp":"2026-10-14T07:12:01Z","data":{
 "schemaVersion":1,"eventId":"0192f3a4-5b6c-7d8e-9f00-112233445566","id":18342,"kind":"AttributesChanged",
 "severity":"info","domain":"contoso.com","detailLevel":"Standard",
 "rules":[{"id":"account-attributes","name":"Zmiany parametrów kont"}],
 "object":{"objectGuid":"6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7","dn":"CN=Łukasz Wiśniewski,OU=IT,OU=Pracownicy,OU=Firma,DC=contoso,DC=com",
           "kind":"User","sAMAccountName":"lwisniewski","sid":"S-1-5-21-…-1107","displayName":"Łukasz Wiśniewski"},
 "previousDn":null,
 "changes":[{"attribute":"department","before":["Sprzedaż"],"after":["Księgowość"]},
            {"attribute":"userAccountControl","before":["512"],"after":["514"],"flagsAdded":["ACCOUNTDISABLE"],"flagsRemoved":[]},
            {"attribute":"unicodePwd","redacted":true}],
 "member":null,
 "actor":{"status":"Resolved","confidence":"High","account":"CONTOSO\\adm.jkowalska","sid":"S-1-5-21-…-1104",
          "workstation":"WS-ADM-01","ipAddress":"10.0.12.34","reason":null,
          "evidence":[{"dc":"dc01.contoso.com","log":"Security","eventCode":4738,"recordId":918273,"timeCreated":"2026-10-14T07:12:01.534Z"}]},
 "detection":{"source":"IncrementalPoll","detectedAt":"2026-10-14T07:12:31Z","originatingTime":"2026-10-14T07:12:01Z","originatingDc":"dc01.contoso.com"},
 "links":{"event":"https://admng-wd.contoso.com:8443/events/0192f3a4-5b6c-7d8e-9f00-112233445566"}}}
```

`timestamp` = event detection time (UTC, seconds). Minimal detail level: `changes` items keep `attribute` only,
`actor` keeps status/account/sid, no `workstation`/`ipAddress`, `links` kept. `before`/`after` arrays respect the
value caps of 14.1. Health events: `object` = `{ "kind": "Watchdog" }` + `health: { code, state, args }`.

Headers (set by `WebhookTransport` **per attempt**):

```
Content-Type: application/json; charset=utf-8
User-Agent: AdMng-Watchdog/<version>
webhook-id: <OutboxMessage.MessageId>          (stable across retries; idempotency key; never contains '.')
webhook-timestamp: <unix seconds of this attempt>
webhook-signature: v1,<base64 HMAC-SHA256(key, id + "." + timestamp + "." + body)> [space v1,<previous key sig>]
X-Hub-Signature-256: sha256=<hex HMAC-SHA256(key, body)>   (only with SendGitHubSignature)
```

Key = base64-decoded part after `whsec_` (24–64 bytes). Generated secrets: `"whsec_" + Base64(RandomNumberGenerator
32 bytes)`. Test vectors (MUST be unit tests):
- Standard Webhooks spec: key `whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw`, id `msg_p5jXN8AQM9LWM0D4loKWxJek`,
  timestamp `1614265330`, body `{"test": 2432232314}` → `v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=`
  (verified with Python `hmac`).
- GitHub style: secret `It's a Secret to Everybody`, body `Hello, World!` →
  `sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17` (verified).
`HttpClient` for webhooks/Teams: `AllowAutoRedirect = false`, https only (Development: loopback http allowed),
timeout from settings (default 30 s), `AllowedHosts` enforced at send time too.

### 14.6 Event HTML page (`EventHtmlRenderer`)

`GET /events/{eventId}` returns a complete HTML document (`<meta charset="utf-8">`) with the single-alert e-mail body
plus related events and evidence (non-Admin callers: `Authorization.ViewerDetailLevel`; access logged with the event
GUID), response headers `Content-Security-Policy: default-src 'none'; style-src
'unsafe-inline'`, `X-Content-Type-Options: nosniff`, `Cache-Control: no-store`.

---

## 15. Outbox, retries, rate limits, channel health

### 15.1 `OutboxSenderJob` (W-core; wake OutboxEnqueued; interval 5 s)

```
msgs = outbox.LeaseDueAsync(now, limit 20, lease 5 min)       // crash: expired leases come back
for each msg (sequential per channel, parallel across channels, max 4):
    result = transports[msg.ChannelType].SendAsync(msg, ct)    // transport handles rate limit + secrets + auth
    Delivered            → MarkSent (payload set to NULL); channel health success (ConsecutiveFailures = 0)
    Deferred (rate limit)→ MarkRetry(now + RetryAfter, countAttempt: false)
    Transient            → next = RetryPolicy.Next(type, attempts + 1, result.RetryAfter); null → MarkDead + HealthDeliveryFailing
                           else MarkRetry(next, countAttempt: true)
    Permanent            → MarkDead (message-specific, e.g. 552 too large, 5.1.1 unknown recipient); health Warning
    ChannelBroken /
    AuthenticationFailed → MarkRetry(now + 1 h, countAttempt: false) until 24 h old, then MarkDead; channel health Error;
                           HealthDeliveryFailing once per transition into Error (sent through other working channels)
```

- Partial delivery: a transport puts `partial.` before the code of a transient failure after some recipients already got
  the message (e-mail: a recipient rejected temporarily while the others accepted, a later recipient chunk failed). Its
  exhausted retries are message-specific – Dead with channel health Warning, no `HealthDeliveryFailing` (one mailbox
  answering `452 4.2.2` or `450 4.1.1` for days must not report the working e-mail channel as failing with every alert).
- Broken-channel retries do not count as attempts: a Teams flow switched off for ten hours must not leave the message
  with an exhausted transient schedule, so the first 429/503/421 after the recovery is retried after the first step;
  the 24 h age limit ends a broken period.
- Withdrawn channel: before sending (and when the transport answers `channel.disabled` / `channel.unknown` /
  `smtp.disabled`), a message whose Teams/webhook channel was disabled or removed, or an e-mail while `Smtp.Enabled` is
  false, is marked Dead with that code – no channel health failure, no `HealthDeliveryFailing`, nothing delivered to a
  re-enabled channel later (an Admin can still retry it manually until `OutboxDeadDays`).
- Heartbeats (`DeliveryKind.Health`, 13.6) are not sent or retried once the next heartbeat of their channel is due
  (`CreatedAt + max(HeartbeatInterval, 1 min)`): such a message is marked Dead with `heartbeat.superseded` instead of a
  retry (a stale backlog would arrive with old timestamps and use the channel's rate limit); the failure itself still
  counts for the channel health as usual.
- Every run first lets an expired channel Error decay (15.8), also when nothing is due.

### 15.2 `RetryPolicy` (±20 % jitter; `Retry-After` honored up to 1 h when longer than the step)

Constructor `RetryPolicy(Random random)` – production passes `Random.Shared`, tests a seeded or zero-jitter source.

| Channel | Delays after attempt 1, 2, … | Give up |
|---|---|---|
| E-mail | 30 s, 2 min, 10 min, 30 min, 1 h, 2 h, 4 h, 6 h, 8 h | after ~22 h → Dead |
| Teams | 5 s, 30 s, 2 min, 10 min, 30 min, 1 h, 2 h, 4 h, 8 h | after ~16 h → Dead |
| Webhook (Standard Webhooks) | 5 s, 5 min, 30 min, 2 h, 5 h, 10 h, 14 h, 20 h, 24 h | Dead |

### 15.3 HTTP classification (`HttpDeliveryClassifier`, W-service)

| Response | Teams (Workflows) | Generic webhook |
|---|---|---|
| 200, 202 (Teams), any 2xx (webhook) | Delivered (202 = accepted by the flow, not proof of posting – health shows "przyjęte") | Delivered |
| 3xx | ChannelBroken (redirects never followed) | Permanent |
| 400 | ChannelBroken (payload/flow error) | Permanent |
| 401 / 403 | ChannelBroken; hint `teams.authMismatch` when the body contains "Shared Access scheme" or "OAuth authorization scheme" | AuthenticationFailed |
| 404 | ChannelBroken (flow deleted / URL wrong) | ChannelBroken |
| 408, 429, 502, 503, 504, other 5xx | Transient (Retry-After) | Transient |
| 410 | ChannelBroken | ChannelBroken (disable endpoint, hint `webhook.gone`) |
| body/headers contain `WorkflowTriggerIsNotEnabled` | ChannelBroken (flow turned off) | – |
| socket error, TLS error, timeout | Transient (TLS validation failure: ChannelBroken + hint) | Transient |

### 15.4 SMTP classification (`SmtpErrorClassifier`, W-service, MailKit types confined to it and the transport)

| Error | Result |
|---|---|
| `SmtpCommandException` 4xx (incl. 421, 451, 452) | Transient |
| `SmtpCommandException` 5xx on RCPT for some recipients | send to the others; Permanent for the message only when all failed (a chunk whose recipients were all rejected does not stop the other chunks); warning log 2203 with the count |
| `SmtpCommandException` 4xx on RCPT for some recipients (greylisting, 452 too many recipients) | the others get it; Transient with code `partial.smtp.4xx…` (15.1) – the retry goes only to the recipients that did not get the message yet; warning log 2202 with the count |
| 552 / `SenderNotAccepted` + ExceededStorageAllocation | Permanent (message too large – digest splits by MaxRowsPerSection) |
| 535 5.7.139, 550 5.7.30 | AuthenticationFailed + hint `smtp.basicAuthDisabled` / `smtp.smtpAuthDisabled` |
| 550 5.7.68 | ChannelBroken + hint `smtp.directSendRejected` |
| `AuthenticationException` (SASL), MSAL `MsalServiceException` | AuthenticationFailed |
| `SslHandshakeException` | ChannelBroken + hint `tls.certificateUntrusted` / `tls.revocationUnavailable` |
| `ProtocolException`, `IOException`, `SocketException`, timeout | Transient |
| `ServiceNotAuthenticatedException` | AuthenticationFailed |

### 15.5 Rate limits (`ChannelRateLimiters`, W-service)

`System.Threading.RateLimiting.TokenBucketRateLimiter` (shared framework): e-mail `MaxMessagesPerMinute` (default 20,
under Exchange Online's 30/min) – one token per SMTP message, i.e. per recipient chunk (a denied token after some
chunks were sent returns `Deferred`; the retry does not resend them); per Teams/webhook channel three chained
limiters PerSecond/PerMinute/PerHour (Teams default 1/20/100 – inside the Teams connector's 100 calls/60 s). A denied lease returns `Deferred` with
`RetryAfter` = the limiter's retry hint (no attempt counted). Limiters use `AutoReplenishment = false`;
`ChannelRateLimiters` calls `TryReplenish()` from a `TimeProvider` timer (deterministic under `FakeTimeProvider`).

### 15.6 SMTP transport details (`SmtpEmailTransport`, `MimeMessageFactory`, `SmtpOAuthTokenProvider`, `SmtpCertificateValidation`)

- MailKitLite `SmtpClient` per send batch (connections are not pooled across ticks; a batch of due messages for the
  same profile reuses one connection), `LocalDomain` = `Smtp.LocalDomain ?? machine FQDN`, `Timeout` from settings,
  `CheckCertificateRevocation` from settings, `ServerCertificateValidationCallback` per mode (System: accept only
  `SslPolicyErrors.None`; PinnedSha256: SHA-256 of the leaf equals the pin; AcceptAnyDevelopmentOnly: only in
  Development), `SslProtocols = Tls12 | Tls13`. `SecureSocketOptions` = mapping of `SmtpSecurity` (**never `Auto`**).
- `DestinationGuard` runs before any credential is touched: the stored secret's binding must equal
  `SecretBindings.For…(effective settings)` (section 18) – otherwise `AuthenticationFailed` with hint
  `secret.rebindRequired` and nothing is sent; XOAUTH2 only when `Host` ∈ bootstrap `Notifications.OAuthSmtpHosts`.
- Auth: None; Basic → `AuthenticateAsync(new NetworkCredential(user, secret))`, only after TLS (validation
  `insecureCredentials`); OAuth2ClientCredentials → MSAL `ConfidentialClientApplicationBuilder.Create(clientId)
  .WithAuthority(EntraCloudInfo.Authority(cloud, tenantId)).WithCertificate(cert from LocalMachine\My by thumbprint)`
  (or `.WithClientSecret(secret)`), singleton per (cloud, tenant, client) to reuse the token cache,
  `AcquireTokenForClient([EntraCloudInfo.SmtpScope(cloud)])` → `new SaslMechanismOAuth2(UserName /*sender mailbox*/,
  token)`. Authority and scope are fixed per cloud (`EntraCloud`) – never free text.
- Each recipient group gets its own message (one pending target per group); its addresses go in `To:`, split into
  messages of ≤ `MaxRecipientsPerMessage` recipients – sorted first, so the same recipient set always gives the same
  chunks: chunk n has `Message-ID: <{MessageId}.{n}@…>` (the first `<{MessageId}@…>`) and chunk n of a follow-up (sent
  to the original's recipients) replies to chunk n of the original (`In-Reply-To`/`References`).
- Recipient progress (`SmtpRecipientProgress`, in memory in the singleton transport): the addresses that accepted an
  outbox e-mail are remembered until the message is complete (at most 14 days), so a retry after a temporary RCPT
  rejection, a failed later chunk, a timeout or a rate-limit deferral goes only to the recipients that did not get it.
  After a service restart a retry reaches everybody again (a duplicate, never a loss). Addresses are never logged.
- MailKit `ProtocolLogger` is never enabled (it would log DATA = directory values).

### 15.7 Teams transport (`TeamsWorkflowTransport`, `TeamsTokenProvider`)

POST the stored body to the URL secret; `AuthMode.Sas` → no Authorization header (sending one breaks SAS triggers);
`EntraClientCredentials` → `Authorization: Bearer <MSAL token for Scope>` (certificate preferred, authority fixed by
`Cloud`) – **only** when the URL host matches `*.environment.api.powerplatform.com` and the client secret's binding
matches (`DestinationGuard`), otherwise `ChannelBroken` + hint `secret.rebindRequired`. Log only `scheme://host/path`
(never the query – `sig` is a credential); keep the .NET HttpClientFactory URI redaction on (never set
`System.Net.Http.DisableUriRedaction`). Webhook transport: `DestinationGuard` checks the signing secrets' binding (URL
host) the same way.

### 15.8 Channel health

`IHealthRegistry.ReportChannel` after every attempt: Ok (last success), Warning (transient failures ≥ 3 in a row),
Error (ChannelBroken / AuthenticationFailed / Dead in the last **two hours** – longer than the hourly broken-channel
retry, so a channel that stays broken neither flaps nor is reported again). Without another error-level failure the
Error decays to Warning (until the next success) – also without any traffic, `ChannelHealthTracker.Refresh` runs with
every outbox run – and the next error-level failure enters Error again and is reported again (`HealthDeliveryFailing`).
Shown in the GUI status and the digest header.
`DeliveryTester` (test buttons) runs the same transport code path with stage reporting (`DeliveryTestResult`) and never
writes to the outbox; Teams test card says "Wiadomość testowa AD Watchdog" and the result always includes hint
`teams.acceptedNotDelivered`.

---

## 16. SQLite storage (W-service)

### 16.1 Database rules

- One file `<DataDirectory>\watchdog.db`, Microsoft.Data.Sqlite 10.0.13 (SQLitePCLRaw 2.1.13, e_sqlite3 3.53.x). On
  creation, before the first table: `PRAGMA auto_vacuum = INCREMENTAL; PRAGMA journal_mode = WAL;` (WAL persists).
  Every connection: `PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000;
  PRAGMA secure_delete = ON; PRAGMA temp_store = MEMORY;` – e_sqlite3 is built without SECURE_DELETE and with
  TEMP_STORE=1 (lab-verified), so without these purged rows would linger in free pages and sorts / transient indices /
  `VACUUM INTO` would spill directory data into temp files in the gMSA profile's `%TEMP%` (outside the data folder).
- Pooled connections (default); **one writer at a time** (`SqliteDatabase.WriteAsync` behind a `SemaphoreSlim(1)`);
  readers use their own short-lived connections; never keep a reader open across an `await` that yields to other
  work (materialize pages first). Never `Cache=Shared`. Use the synchronous ADO.NET APIs inside `Task.Run`-free code
  paths (Microsoft.Data.Sqlite async methods run synchronously anyway).
- Times are `INTEGER` Unix milliseconds UTC; GUIDs `BLOB(16)` (`Guid.ToByteArray()`); enums `INTEGER` (stable
  values); payloads `BLOB` = Deflate(JSON) (`BlobCodec`): Contracts types via `WatchdogJsonContext`, Core.Model types
  (SnapshotObject/SnapshotAttribute, PrivilegeState, NotificationTarget, RenderedEmail/RenderedHttpMessage,
  SecurityEvent, UpcomingExpiry) via the Service's `Storage/WatchdogStorageJsonContext` (Contracts cannot reference
  Core). System.Text.Json drops the `StringComparer.OrdinalIgnoreCase` initializer on deserialization (verified with a
  source-generated context), so `BlobCodec` rebuilds every dictionary (`SnapshotObject.Attributes`, `SecurityEvent.Data`
  – Ordinal, `RenderedEmail.Headers`) with its intended comparer after reading; a store contract test looks up a
  snapshot attribute with different casing.
- Migrations: embedded `Storage/Migrations/NNNN_name.sql`, each applied in one transaction followed by
  `PRAGMA user_version = NNNN`; before applying any migration to an existing non-empty database:
  `VACUUM INTO '<data>\backup\pre-migration-<old>-<timestamp>.db'` (rotated by retention: the newest is kept, others
  deleted after `Retention.PreMigrationBackupDays`). A database with `user_version` newer than the build → refuse to
  start (Event Log error, exit code 3).
- `SqliteConnection.ClearAllPools()` before moving/deleting files (backups, tests).
- The database stays on a local NTFS volume (WAL needs shared memory on the same host) – checked at start
  (`DriveInfo.DriveType == Fixed`, warning otherwise).

### 16.2 Schema `0001_initial.sql`

```sql
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);                -- installation id, last backup …

CREATE TABLE config (
  id INTEGER PRIMARY KEY CHECK (id = 1), version INTEGER NOT NULL, json TEXT NOT NULL,   -- never contains secret values
  updated_at INTEGER NOT NULL, updated_by TEXT NOT NULL);
CREATE TABLE config_audit (
  id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, by_sid TEXT, by_account TEXT NOT NULL,
  version INTEGER NOT NULL, summary TEXT NOT NULL);
CREATE TABLE secrets (name TEXT PRIMARY KEY, protected BLOB NOT NULL,
  binding TEXT NOT NULL,                           -- destination fingerprint (section 18), not secret
  updated_at INTEGER NOT NULL);

CREATE TABLE dc_state (
  domain TEXT PRIMARY KEY, dc_host TEXT NOT NULL, ds_service_name TEXT NOT NULL, invocation_id BLOB NOT NULL,
  highest_usn INTEGER NOT NULL, last_poll_at INTEGER, last_change_at INTEGER, last_full_sync_at INTEGER,
  baseline_completed_at INTEGER, memberships_changed_at INTEGER);
CREATE TABLE dc_watermarks (                       -- failback without resync (10.3)
  ds_service_name_norm TEXT NOT NULL, invocation_id BLOB NOT NULL, highest_usn INTEGER NOT NULL,
  updated_at INTEGER NOT NULL, PRIMARY KEY (ds_service_name_norm, invocation_id)) WITHOUT ROWID;

CREATE TABLE snapshot_objects (
  object_guid BLOB PRIMARY KEY, dn TEXT NOT NULL, dn_norm TEXT NOT NULL, kind INTEGER NOT NULL,
  sid TEXT, sam TEXT, name TEXT, display_name TEXT, primary_group_rid INTEGER,
  usn_created INTEGER NOT NULL, usn_changed INTEGER NOT NULL, when_created INTEGER,
  is_deleted INTEGER NOT NULL DEFAULT 0, deleted_at INTEGER, creation_pending_until INTEGER,
  attributes BLOB NOT NULL, updated_at INTEGER NOT NULL);
CREATE INDEX ix_snapshot_pending ON snapshot_objects(creation_pending_until) WHERE creation_pending_until IS NOT NULL;
CREATE INDEX ix_snapshot_kind ON snapshot_objects(kind) WHERE kind IN (11, 15, 16);   -- DC, PKI, Configuration rows
CREATE INDEX ix_snapshot_sid ON snapshot_objects(sid) WHERE sid IS NOT NULL;
CREATE INDEX ix_snapshot_dn ON snapshot_objects(dn_norm);
CREATE INDEX ix_snapshot_pgr ON snapshot_objects(primary_group_rid)
  WHERE primary_group_rid IS NOT NULL AND primary_group_rid NOT IN (513, 514, 515);
CREATE INDEX ix_snapshot_deleted ON snapshot_objects(deleted_at) WHERE is_deleted = 1;

CREATE TABLE snapshot_members (
  group_guid BLOB NOT NULL, member_key TEXT NOT NULL, member_guid BLOB, member_sid TEXT, member_dn TEXT NOT NULL,
  PRIMARY KEY (group_guid, member_key)) WITHOUT ROWID;
CREATE INDEX ix_members_key ON snapshot_members(member_key);

CREATE TABLE privilege_state (id INTEGER PRIMARY KEY CHECK (id = 1), state BLOB NOT NULL, computed_at INTEGER NOT NULL);

CREATE TABLE events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,            -- AUTOINCREMENT: cursors never reuse ids after retention deletes
  event_id BLOB NOT NULL UNIQUE, change_set_id BLOB,
  kind INTEGER NOT NULL, severity INTEGER NOT NULL, object_kind INTEGER NOT NULL,
  object_guid BLOB, object_dn TEXT NOT NULL, object_dn_norm TEXT NOT NULL, previous_dn_norm TEXT,   -- EventQuery.Subtree
  search_text TEXT NOT NULL,                       -- lower-case name|sam|dn|actor|workstation|ip (updated with attribution)
  tags TEXT NOT NULL DEFAULT '',                   -- "|privileged|serviceAccount|" for EventQuery.Tags
  detected_at INTEGER NOT NULL, originating_at INTEGER, originating_dc TEXT, source INTEGER NOT NULL,
  actor_status INTEGER NOT NULL, actor_sid TEXT, next_attribution_at INTEGER,
  rules_state INTEGER NOT NULL DEFAULT 0, matched_rules TEXT,
  acked_at INTEGER, acked_by TEXT, ack_comment TEXT,
  payload BLOB NOT NULL);                          -- full WatchEvent (changes, attribution …)
CREATE INDEX ix_events_detected ON events(detected_at);
CREATE INDEX ix_events_kind ON events(kind, detected_at);
CREATE INDEX ix_events_object ON events(object_guid, detected_at);
CREATE INDEX ix_events_actor ON events(actor_sid, detected_at) WHERE actor_sid IS NOT NULL;
CREATE INDEX ix_events_changeset ON events(change_set_id) WHERE change_set_id IS NOT NULL;
CREATE INDEX ix_events_attr_due ON events(next_attribution_at) WHERE actor_status = 1;
CREATE INDEX ix_events_unevaluated ON events(id) WHERE rules_state = 0;

CREATE TABLE pending_notifications (
  id INTEGER PRIMARY KEY AUTOINCREMENT, event_id INTEGER NOT NULL REFERENCES events(id) ON DELETE CASCADE,
  rule_id TEXT NOT NULL, target BLOB NOT NULL, target_key TEXT NOT NULL, severity INTEGER NOT NULL,
  bucket_key TEXT NOT NULL, due_at INTEGER NOT NULL, hold_until INTEGER,
  is_follow_up INTEGER NOT NULL DEFAULT 0, reply_to TEXT, created_at INTEGER NOT NULL,
  UNIQUE (event_id, target_key, is_follow_up));
CREATE INDEX ix_pending_bucket ON pending_notifications(bucket_key, due_at);

CREATE TABLE outbox (
  id INTEGER PRIMARY KEY AUTOINCREMENT, message_id TEXT NOT NULL UNIQUE,
  channel_type INTEGER NOT NULL, channel_id TEXT NOT NULL, kind INTEGER NOT NULL, rule_id TEXT, severity INTEGER NOT NULL,
  status INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL,
  next_attempt_at INTEGER NOT NULL, lease_until INTEGER, sent_at INTEGER,
  last_error_code TEXT, last_error TEXT, subject TEXT, recipient_count INTEGER NOT NULL DEFAULT 0,
  recipients TEXT, target_key TEXT, sent_with_pending INTEGER NOT NULL DEFAULT 0,
  payload BLOB);                                   -- NULL after successful send (only metadata kept, 16.3)
CREATE INDEX ix_outbox_due ON outbox(status, next_attempt_at);
CREATE INDEX ix_outbox_sent ON outbox(channel_id, rule_id, sent_at);
CREATE TABLE outbox_events (
  outbox_id INTEGER NOT NULL REFERENCES outbox(id) ON DELETE CASCADE,
  event_id INTEGER NOT NULL REFERENCES events(id) ON DELETE CASCADE,
  PRIMARY KEY (outbox_id, event_id)) WITHOUT ROWID;
CREATE INDEX ix_outbox_events_event ON outbox_events(event_id);

CREATE TABLE security_events (
  id INTEGER PRIMARY KEY, computer TEXT NOT NULL, record_id INTEGER NOT NULL, source TEXT NOT NULL,
  event_code INTEGER NOT NULL, time_created INTEGER NOT NULL,
  target_sid TEXT, object_guid BLOB, member_sid TEXT, target_user TEXT, object_dn_norm TEXT,
  subject_sid TEXT, subject_logon_id TEXT, data BLOB NOT NULL,
  UNIQUE (computer, record_id));
CREATE INDEX ix_sec_target ON security_events(target_sid, time_created) WHERE target_sid IS NOT NULL;
CREATE INDEX ix_sec_object ON security_events(object_guid, time_created) WHERE object_guid IS NOT NULL;
CREATE INDEX ix_sec_code ON security_events(event_code, computer, time_created);
CREATE INDEX ix_sec_time ON security_events(time_created);
CREATE TABLE eventlog_cursors (
  source TEXT PRIMARY KEY, last_record_id INTEGER NOT NULL, bookmark_xml TEXT, oldest_seen INTEGER, updated_at INTEGER NOT NULL);

CREATE TABLE expiry_notices (
  object_guid BLOB NOT NULL, kind INTEGER NOT NULL, expires_raw INTEGER NOT NULL, threshold_days INTEGER NOT NULL,
  notified_at INTEGER NOT NULL, PRIMARY KEY (object_guid, kind, expires_raw, threshold_days)) WITHOUT ROWID;
CREATE TABLE upcoming_expiries (
  kind INTEGER NOT NULL, object_guid BLOB NOT NULL, expires_at INTEGER NOT NULL, payload BLOB NOT NULL,
  PRIMARY KEY (kind, object_guid));
CREATE TABLE digest_runs (
  id INTEGER PRIMARY KEY AUTOINCREMENT, period_start INTEGER NOT NULL, period_end INTEGER NOT NULL,
  created_at INTEGER NOT NULL, event_count INTEGER NOT NULL, outbox_ids TEXT NOT NULL);
```

### 16.3 Retention and maintenance (`RetentionJob` → `IStoreMaintenance`) – daily at `Retention.MaintenanceTime`

Delete in batches of 5 000 rows per transaction (writer gate released between batches):
events older than `EventsDays` (cascades pending notifications and outbox links), `security_events` older than
`SecurityEventsDays`, outbox Sent older than `OutboxSentDays` and Dead/Cancelled older than `OutboxDeadDays` (a Sent
message has no payload anyway – `MarkSentAsync` sets it to NULL, so rendered PII is kept only while a message is
Pending/Retrying/Dead), deleted snapshot rows older than `DeletedSnapshotDays` (+ their member rows), expiry notices
whose expiry passed more than 30 days ago, `config_audit` older than `ConfigAuditDays`, `digest_runs` older than 365
days. Size cap: when the database exceeds `MaxDatabaseMb`, `security_events` are purged oldest-first (then outbox
Dead) until it is below 90 % – also checked hourly, not only at maintenance time; `HealthStorage` at ≥ 80 %. Then
`PRAGMA incremental_vacuum(2048)` in steps (the writer gate is released between the steps, so deliveries are not held
up by a large freelist), `PRAGMA wal_checkpoint(TRUNCATE); PRAGMA optimize;` (with `secure_delete = ON` the freed
pages and the truncated WAL no longer contain purged values), then `VACUUM INTO
'<data>\backup\watchdog-yyyyMMdd-HHmm.db'` – on its own connection **outside** the single-writer gate (in WAL mode a
read transaction on a consistent snapshot; only the `LastBackupAt` update takes the gate, so lease/sent/retry and
detection writes go on while a multi-GB database is copied) – and delete backups beyond `BackupsToKeep`. The health
checks `storage.size` (database file + WAL against the cap: Warning from 80 %, Error above it) and `storage.free`
(Error below 1 GiB) have one definition (`StorageHealth`), reported by the retention job and the hourly
`StorageGuardJob` from the same `GetStatusAsync`; pre-migration backups: keep the
newest, delete the rest after `PreMigrationBackupDays`. A backup is only useful together with `<data>\keys\` and the
same gMSA (DPAPI-NG) – documented; backups carry the folder ACL and should be encrypted by the backup system
(BitLocker on the data volume recommended).

Size guide (measured with Polish data): event row ≈ 0.6 KB incl. indexes (1 000 events/day ≈ 230 MB/year);
snapshot ≈ 0.6 KB/object with the deflated attribute blob (50 000 objects ≈ 30 MB). `HealthStorage` fires when the
volume has < 1 GiB free or the database reaches 80 % of `MaxDatabaseMb`.

---

## 17. HTTP API (W-service)

### 17.1 Conventions

- Minimal APIs, JSON via `WatchdogJsonContext` (source-gen), ProblemDetails (RFC 9457) for every error with `type`
  from `ApiProblem`; validation errors include `extensions.issues` (`ConfigIssue[]`). Unhandled exceptions go through
  `ApiExceptionHandler` (`IExceptionHandler`): ProblemDetails 500 without exception text, logged via `SafeExceptionLog`.
- Every response carries `X-AdMng-Watchdog-Api: 1`. Requests with a different `X-AdMng-Watchdog-Api` value → 400.
- POST/PUT require `X-AdMng-Client: 1` (filter `ClientHeaderFilter`) and `Content-Type: application/json`
  (form posts → 415) – CSRF protection against intranet browsers that send Windows credentials automatically. No
  CORS policy is configured. State never changes on GET.
- Logs: method, route template, status, duration, caller account (no query values, no bodies). Access to single
  events (`/api/v1/events/{id}`, `/events/{eventId}`) additionally logs the numeric id / event GUID with the caller
  (accountability for who viewed an employee's history – RODO); never a directory value.
- Limits: request body ≤ 1 MB; `limit` ≤ 500; long-poll ≤ 30 s; per-caller concurrency 4 (rate limiter).
- TLS: Kestrel `HttpsConnectionAdapterOptions.SslProtocols = Tls12 | Tls13` (Windows Server 2019/2022 still enable
  TLS 1.0/1.1 server-side by default); HTTP.sys follows the OS settings – documented hardening.
- No reverse proxy or TLS-inspecting device in front of the API (documented): remote-address checks use
  `Connection.RemoteIpAddress` only and forwarded headers are never honoured.

### 17.2 Endpoints

| Method | Route | Role | Request | Response | Notes |
|---|---|---|---|---|---|
| GET | `/healthz` | anonymous: loopback, the machine's own addresses, `Hosting.MonitoringSubnets` | – | `200 text/plain "ok"` / 503 | liveness for monitoring (SCOM/Zabbix/PRTG); no data; other callers 403 |
| GET | `/api/v1/me` | Viewer | – | `MeDto` | roles from SID claims, detail level |
| GET | `/api/v1/status` | Viewer | – | `StatusDto` | health registry + storage + counts + rollout + suggestions |
| GET | `/api/v1/catalog` | Viewer | – | `EventKindInfo[]` | |
| GET | `/api/v1/events` | Viewer | `EventQuery` (query string) | `EventPage` | `after` → ascending ids > after (long-poll with `wait` 1–30 s via `IEventStore.WaitForNewerAsync`, ended at once by `ApplicationStopping` – a stop never waits for open GUI tabs); otherwise descending (`before` for older pages); non-Admins get `ViewerDetailLevel`; for Minimal callers `text` does not match workstation and IP (`IViewerEventSearch`, SQL `wd_search_visible`), else hit/miss would reveal the hidden source; page and `LatestId` come from one read snapshot (`SqliteDatabase.ReadSnapshot`) |
| GET | `/api/v1/events/{id}` | Viewer | – | `EventDetails` | 404 when purged; detail level as above; access logged |
| POST | `/api/v1/events/{id}/ack` | Viewer | `AcknowledgeRequest` | 204 | idempotent; stores caller account |
| POST | `/api/v1/events/ack` | Viewer | `BulkAcknowledgeRequest` (1–500 ids) | 204 | bulk acknowledge (lockout-heavy feeds) |
| GET | `/api/v1/config` | Viewer | – | `ConfigEnvelope` + `ETag: "<version>"` | Admin: full (secrets as `IsSet`/`Hint` only); Viewer: **redacted** by `ConfigRedactor` (`IsRedacted = true`: exclusions and actor exclusions (`ExcludeActors`) removed – only their count per rule –, recipient addresses and webhook `AllowedHosts` removed, SMTP host/user/from/replyTo/localDomain removed, channel URL hints and OAuth ids removed) |
| PUT | `/api/v1/config` | Admin | `ConfigEnvelope` + `If-Match: "<version>"` | `ConfigEnvelope` (new version) | algorithm below; 412 on version conflict; 400 with issues |
| GET | `/api/v1/config/defaults` | Viewer | – | `WatchdogConfiguration` | for "reset rule to default" |
| POST | `/api/v1/config/validate` | Admin | `WatchdogConfiguration` | `ConfigValidationResult` | no side effects |
| GET | `/api/v1/config/audit` | Admin | `before`, `limit` | `ConfigAuditPage` | |
| POST | `/api/v1/test/email` | Admin | `TestEmailRequest` | `DeliveryTestResult` | ≤ 60 s; never queued; stored secrets only with a matching binding (else 400 `secretRebindRequired`, nothing sent) |
| POST | `/api/v1/test/channels/{channelId}` | Admin | `TestChannelRequest` | `DeliveryTestResult` | ≤ 45 s; same binding rule |
| POST | `/api/v1/rules/preview` | Admin | `RulePreviewRequest` | `RulePreviewResult` | scans stored events of the last N days (≤ 100 000) |
| GET | `/api/v1/digest/preview` | Viewer | – | `DigestPreviewDto` | builds without sending (Viewer: ViewerDetailLevel); active exceptions only as a number (13.4) |
| POST | `/api/v1/digest/send` | Admin | – | 202 | `IDigestService.SendNowAsync` |
| GET | `/api/v1/outbox` | Admin | `OutboxQuery` | `OutboxPage` | delivery log |
| POST | `/api/v1/outbox/{id}/retry` | Admin | – | 204 / 404 / 409 (not Dead) | requeue now |
| GET | `/events/{eventId}` | Viewer | – | `text/html` | links in notifications (14.6); detail level and access log as `/api/v1/events/{id}` |

`PUT /config` algorithm (`ConfigEndpoints` + `ConfigSecretsApplier` + `ConfigAuditSummarizer` + `ConfigChangeNotifier`):
1. Parse; reject a redacted envelope (`redactedConfig`).
2. Validate with `ConfigValidationContext` (stored secret names + bindings from `ISecretStore.ListAsync`, environment,
   bootstrap flags) and, for `additionalWatchedAttributes`, the live session schema (8.3 `invalidAttribute`).
   Then `ExclusionProvenance` sets `CreatedBy`/`CreatedAt` of every new or changed exclusion to the caller and the
   service clock and keeps the stored values of unchanged ones (same rule id, exclusion id and content) – the client's
   values are ignored (12.3 shows them in every digest).
3. `ConfigSecretsApplier` builds `SecretChange`s: `Clear` → remove; `NewValue` → set with the binding of the **new**
   settings (`SecretBindings`, section 18) and a `Hint` (URLs → `scheme://host[:port]/…` – `SecretHints`: never a path
   segment, query or user info, because services such as webhook.site or ntfy.sh carry the credential in the first
   segment; the host stays because the validator reads the declared host from the hint; webhook secrets →
   `whsec_…` + last 4 chars, passwords → null); a kept secret keeps its stored hint in that safe form; a removed
   channel removes its secrets; strip `NewValue`/`Clear`, set `IsSet`. `SqliteConfigStore` stores hints only in the
   safe form and repairs a document written by an earlier version in place on its first read (same version, no audit
   entry). Known limits: a service whose host itself is the credential (e.g. `*.m.pipedream.net`) still shows it in the
   hint – masking the host needs `SecretBindings.DeclaredUrlHost`/`ChannelValidator` to stop reading hints (Core); a
   retired connector URL recognised only by its path (`outlook.office.com/webhook/…`) is no longer caught by the
   stored-hint check of 8.3 (it cannot be entered – `NewValue` is checked in full – and the Teams transport refuses it
   at send time with `retiredTeamsUrl`).
4. `IConfigStore.SaveAsync(config, expectedVersion, by, summary, secretChanges)` – config row, secrets and the
   `config_audit` entry in **one SQLite transaction** (412 on conflict leaves secrets untouched).
5. `ConfigChangeAuditor` (Core) diffs the **effective** forms (`WatchdogConfigProvider.Effective`) of old and new – GET
   returns the effective document, so disabling a built-in rule the stored document lacks is audited as disabled, not
   added – → `WatchdogConfigurationChanged` (Health; severity per section 7,
   Changes: changed sections/rules, never secret values; attribution Resolved = caller). `ConfigChangeNotifier`
   appends it and plans its notifications **synchronously with the previous configuration** (so the old recipients
   learn that they were removed), then leaves it unevaluated so `RuleEvaluationJob` also plans it with the new one.
6. `IWatchdogConfigProvider.ReloadAsync`, `signals.Raise(ConfigChanged)`, response = saved envelope.

### 17.3 Authentication and authorization (`Auth/*`)

- `Watchdog:Authentication = Negotiate` (default): `AddAuthentication(NegotiateDefaults.AuthenticationScheme)
  .AddNegotiate(o => { o.PersistKerberosCredentials = false; o.Events = KerberosOnlyEvents; })` –
  `PersistKerberosCredentials` stays **false** (upstream: must not be used when connections may be shared by
  different users, e.g. behind a pooling proxy); the API is low-volume, the extra handshake per request is irrelevant.
  `KerberosOnlyEvents.OnAuthenticated`: when `Hosting.RequireKerberos` and `AuthenticationType` ≠ `"Kerberos"` →
  `Fail("NTLM is not accepted")`. `OnAuthenticationFailed`: respond 401, `HandleResponse()`, log a hint ("check SPN
  HTTP/<host> on the service account and that clients use the A record") – never a 500.
- `KerberosOnlyMiddleware` (after `UseAuthentication`, for **both** servers): an authenticated identity that was not
  authenticated by Kerberos → 401 + log 2500 (Development/Test schemes exempt). This is the enforcement point for
  HTTP.sys, where `NegotiateEvents` do not run. Kestrel reports `AuthenticationType` `Kerberos`/`NTLM` directly;
  HTTP.sys reports every SPNEGO logon as `Negotiate`, so `ILogonPackageResolver` (`WindowsLogonPackageResolver`)
  reads the real package from the caller's logon token – the token source (`Kerberos` / `NtLmSsp`), else the logon
  session (`LsaGetLogonSessionData`) – and an undeterminable package is refused (fail closed).
- Kestrel endpoint `HttpProtocols.Http1` (Negotiate does not work on HTTP/2).
- `HttpSys` server: Windows only, `HttpSysOptions.Authentication.Schemes = AuthenticationSchemes.Negotiate` always, plus
  the AppContext switch `Microsoft.AspNetCore.Server.HttpSys.EnableCBTHardening` = true (the scheme `Kerberos` sends
  `WWW-Authenticate: Kerberos`, which neither browsers nor `SocketsHttpHandler` answer – every call would get 401);
  `RequireKerberos` is enforced by `KerberosOnlyMiddleware` as above. `AllowAnonymous = true` (for `/healthz`), scheme
  `HttpSysDefaults.AuthenticationScheme`; same authorization.
- Authorization: requirement `SidRoleRequirement(role)`; handler **first** denies when any user/group SID claim is in
  `Authorization.DeniedSids` (emergency revocation – Kerberos tickets keep group SIDs for up to 10 h), then grants when
  any `ClaimTypes.GroupSid` **or** `ClaimTypes.PrimaryGroupSid` claim value is in the configured SIDs (Admin SIDs also
  satisfy Viewer). Policies `Viewer`, `Admin`; `FallbackPolicy` = Viewer (every endpoint requires at least Viewer
  unless explicitly anonymous). Group changes take effect when the user gets a new Kerberos ticket (documented).
- `Development` scheme (only `ASPNETCORE_ENVIRONMENT=Development`, never when running as a Windows service, never when
  any listener is non-loopback – checked at startup – and only for loopback remote addresses): identity
  `Development.Account`, group SIDs `Development.Sids`.
- Tests register a `Test` scheme (`X-Test-Account`, `X-Test-Sids` headers) via `ConfigureTestServices`
  (the Negotiate handler throws `NotSupportedException` under TestServer – it needs `IConnectionItemsFeature`), so the
  Negotiate handler is registered only when `Watchdog:Authentication = Negotiate`.
- `RemoteAddressFilter`: `AllowedRemoteSubnets` (when non-empty) → 403 for other remote addresses (defense in depth);
  `/healthz` → loopback, own addresses and `MonitoringSubnets` only. The filter runs after `UseRouting` and applies the
  health rule to whatever routing matched to the health endpoint (`/healthz/`, `/HEALTHZ` too), with the path without
  trailing slashes as the fallback.
- Linux: the Negotiate handler emits no SID claims (only role names via `EnableLdap`), so on Linux the real-Kerberos
  test proves **authentication only** (expects 403 with `AuthenticationType` "Kerberos", 22.4).

---

## 18. Secrets (W-service)

- ASP.NET Core Data Protection: `AddDataProtection().SetApplicationName("AdMng.Watchdog")
  .PersistKeysToFileSystem(<data>\keys)`; on Windows **`.ProtectKeysWithDpapiNG()`** (rule `SID={gMSA SID}` – the KDS
  infrastructure the gMSA already needs; keys survive a move to another host running the same gMSA); on Linux
  (lab/dev only) keys unprotected with a startup warning, refused when the environment is Production.
  Tests: `UseEphemeralDataProtectionProvider()`.
- `SqliteSecretStore`: `IDataProtector` purpose `"AdMng.Watchdog.Secrets.v1"`; table `secrets(name, protected,
  binding)`; values only ever in `SecretValue` (ToString masked); secrets are re-protected on every edit; production
  writes only through `IConfigStore.SaveAsync` (one transaction with the config and its audit entry).
- **Destination binding** (`SecretBindings`, Core): every secret is stored with a canonical, non-secret fingerprint
  of where it may be sent; `DestinationGuard` (Service) compares it with the effective settings before any use, and
  the validator rejects settings that would reuse a secret elsewhere (`secretRebindRequired`, 8.3) – an API Admin
  (an ordinary AD group, not trusted with secret values) cannot redirect a stored credential to his own host:

  | Secret | Binding (lower-case, `|`-separated) |
  |---|---|
  | `smtp.password` | `smtp|<host>|<port>|<userName>|basic[|<tls>]` |
  | `smtp.oauth.clientSecret` | `smtp-oauth|<cloud>|<tenantId>|<clientId>|<host>[|<tls>]` (host must also be in `Notifications.OAuthSmtpHosts`) |
  | `channel.{id}.url` | `channel|<id>|<type>` (the URL is its own destination; Teams OAuth additionally requires a Power Platform host) |
  | `channel.{id}.clientSecret` | `teams-oauth|<cloud>|<tenantId>|<clientId>|<scope>|<url host>` |
  | `channel.{id}.signingSecret`, `…previousSigningSecret` | `webhook|<id>|<url host>` |

  `<tls>` is the TLS trust of the SMTP server: empty for `certificateValidation = System` with `checkRevocation`
  (the default – bindings stored before the component existed stay valid; a credential stored earlier with a pinned
  certificate or with revocation checks off must be entered once more after the update), otherwise `tls-<mode>` (`tls-pinnedsha256:<pin>`
  with the lower-case pin) followed by `revocation-off` when revocation checks are off. An Admin who pins his own
  certificate therefore cannot get the stored password or the XOAUTH2 token released to it.
  Changing any bound field requires entering the secret again (`NewValue`); a test send with unsaved settings uses a
  stored secret only when the binding matches. Tests: host changed without `NewValue` → 400 and the SMTP sink sees no
  `AUTH`; OAuth settings with a foreign host → no token request.
- API never returns secret values; `SecretField.Hint` only (redacted entirely for Viewers). Request DTOs carrying
  secrets override `ToString()`. Request/response body logging is never enabled.
- Teams/webhook URLs are secrets (the `sig` query parameter is a bearer credential); logs print
  `scheme://host/path` only.
- OAuth certificates are referenced by thumbprint (LocalMachine\My, private key ACL for the gMSA) – preferred over
  client secrets. Authority and scopes are fixed per `EntraCloud`.
- DR: deleting/recreating the gMSA makes stored secrets unreadable → the service starts, marks affected channels
  `secret.unreadable` (health Error) and the admin re-enters them in the GUI. Documented.
- CLAUDE.md rule 2 carries the corresponding exception (4.4).

---

## 19. Hosting (W-service)

### 19.1 Program flow (`Program.cs`, `WatchdogHost`)

```
args: [--console] [--smoke-test] [--check-config] [--health] [--environment <name>] [--contentRoot <dir>]
options = new WebApplicationOptions { Args = args, ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null }
builder = WebApplication.CreateBuilder(options)
builder.Services.AddWindowsService(o => o.ServiceName = "AdMngWatchdog")  // no-op on Linux / console
builder.Configuration: appsettings.json, %ProgramData%\AdMng\Watchdog\config\appsettings.json (optional, watched – 8.4), the file
    named by env ADMNG_WATCHDOG_CONFIG (optional, lab/dev, refused when running as a Windows service), env
    ADMNG_WATCHDOG__* (section "Watchdog")
LoggingSetup: console (only when interactive), EventLog (Windows, SourceName "AdMng Watchdog", Warning+ plus the
    Information entries 1000, 1006 and the access log 2600/2601 – `LoggingSetup.EventLogFilter`), both wrapped
    by RedactingLoggerProvider; no file logs; HostOptions.BackgroundServiceExceptionBehavior = Ignore (JobHost catches
    everything and logs via SafeExceptionLog); AddExceptionHandler<ApiExceptionHandler>()
DataDirectory.Ensure(); DataProtectionSetup; AuthenticationSetup; KestrelSetup | HttpSysSetup
services.AddWatchdogCore(); stores (singletons sharing SqliteDatabase); transports; IDeliveryTester;
IDirectorySessionProvider (ServiceDirectorySessionProvider over LdapDirectoryConnector); ISecurityEventSource
(Windows → WindowsSecurityEventSource, else NullSecurityEventSource); IProcessTerminator; one JobHost per IWatchdogJob
app = builder.Build()
StartupChecks (before Run – .NET 10 runs BackgroundService.ExecuteAsync fully in the background, so nothing may serve
    an un-migrated DB): BootstrapFileGuard (exit 6), data-directory ACL (exit 5), migrations (exit 3 for a newer DB),
    plain http only on loopback listeners (both servers; exit 4), certificate key access (only for Kestrel with HTTPS;
    exit 4), Development-auth guard (refused as a Windows service
    or with a non-loopback listener; exit 7), secrets readable, runtime version (`Environment.Version` < 10.0.13 →
    Event Log warning + health Warning `runtime.outdated`) → Event Log hints
ApiSetup.Map(app); app.Run()
```

- A host that cannot be built at all (malformed bootstrap JSON, an invalid setting value, an unusable data folder –
  any exception from `Build` except OOM) is reported by `StartupFailureLog` before any logging provider exists: stderr
  and, on Windows, Application-log event 1010 written directly with source "AdMng Watchdog" (type and code only, no
  values, a hint to `--check-config`); exit code 2 – never an unhandled crash.
- `--smoke-test` (CI): temporary data directory, loopback HTTP on an ephemeral port, `Development` auth,
  directory and attribution disabled, runs migrations, GETs `/healthz` and `/api/v1/status`, renders a sample alert
  with every formatter, exits 0/1. `--check-config`: validates bootstrap + stored config, prints issues, exits.
  `--health`: reads the bootstrap `Hosting.Urls`, GETs `/healthz` (system certificate validation, 10 s timeout) and
  exits 0 (ok) / 1 – for monitoring scripts and the install script.
- `JobHost`: `BackgroundService` per job; subscribes once to the job's `WakeSignals` (`IWatchdogSignals.Subscribe`);
  loop `RunOnceAsync` → `subscription.WaitAsync(NextRunIn ?? DefaultInterval)`; unexpected exception →
  `SafeExceptionLog`, exponential backoff 1 s → 5 min; repeated fatal errors in the detection job for > 1 h →
  `FatalErrorHandler` → `IProcessTerminator.Exit(1)` so SCM recovery restarts the service (a clean stop never
  triggers recovery). Tests replace `IProcessTerminator`. A `Failed` result logs warning 2300 when the failure starts
  or its code changes, then at most hourly while it lasts (the condition itself is in the health registry), and
  Information 2301 on recovery – admitted to the Application log (19.8), otherwise a recovered job and a still failing
  one would look the same there.
- `TimeProvider.System` everywhere; tests swap `FakeTimeProvider`.
- Crash dumps: the install script calls `WerAddExcludedApplication` for `AdMng.Watchdog.exe` (WER dumps could contain
  decrypted secrets and directory values); the service never enables `DOTNET_DbgEnableMiniDump`.

### 19.2 Why Kestrel by default (HTTP.sys optional)

| | Kestrel + Negotiate (default) | HTTP.sys (option) |
|---|---|---|
| Kerberos | user mode, SPN `HTTP/<A record>` **on the gMSA** | kernel mode, machine account – no SPN work (HOST/ covers HTTP/<server FQDN>) |
| NTLM | rejected (`KerberosOnlyEvents` + `KerberosOnlyMiddleware`) | scheme Negotiate with `EnableCBTHardening`; NTLM rejected by `KerberosOnlyMiddleware` from the logon token's package (`RequireKerberos`) |
| TLS cert | selected in app config; key ACL for the gMSA; hot selection by thumbprint/subject | `netsh http add sslcert` per hash – rebind on every renewal |
| URL ACL | none | `netsh http add urlacl url=https://+:8443/ user=CONTOSO\svc-admngwd$` |
| Channel binding (EPA) | none on .NET 10 → NTLM rejected | opt-in `Microsoft.AspNetCore.Server.HttpSys.EnableCBTHardening` (10.0.1+, off by default) |
| Tests | same stack on Linux (TestServer, real sockets, real Kerberos against Samba) | Windows only (CI: an NTLM-only client gets 401) |

Kestrel wins because the same server runs in the Windows service, in console mode and in Linux CI. Choose HTTP.sys
when the AD team refuses an SPN on the gMSA.

### 19.3 Kestrel and certificate (`KestrelSetup`, `CertificateSelector`)

`ListenAnyIP(port, lo => { lo.Protocols = HttpProtocols.Http1; lo.UseHttps(h => { h.SslProtocols = SslProtocols.Tls12 |
SslProtocols.Tls13; h.ServerCertificateSelector = (_, _) => CertificateSelector.Current; }); })` from `Hosting.Urls`.
Selector: LocalMachine\My by `Thumbprint`, else by `Subject` (newest valid NotAfter with serverAuth EKU and private
key); reloaded every 10 min (renewals). Startup self-check signs test data with the private key; failure → Event Log
error "grant the service account Read on the private key (certlm.msc → Manage Private Keys)" and exit code 4.
The reload applies the same signing check before it switches to a newly found certificate (an AD CS renewal gives the
new key the default ACL): when it fails, the previous certificate stays in use while it is valid, health
`certificate.key` Warning (Error when no valid alternative remains – the unusable one is then served, so fixing the key
ACL takes effect at the next handshake) and event 1007 once per thumbprint and state (Warning while the previous
certificate serves, Error once handshakes fail); `certificate.key` returns to Ok when the key
becomes usable. Every reload also re-evaluates `certificate.expiry` (Warning < 30 days, event 1007 once per thumbprint).
A failure to read the store keeps the current certificate.

### 19.4 Windows service registration

Service name `AdMngWatchdog`, display name "AdMng Watchdog", start `delayed-auto`, account `CONTOSO\svc-admngwd$`
(gMSA, no password), `sc.exe sidtype AdMngWatchdog unrestricted` (enables `NT SERVICE\AdMngWatchdog` in ACLs),
`sc.exe failure AdMngWatchdog reset= 86400 actions= restart/60000/restart/60000/restart/300000`,
`sc.exe failureflag AdMngWatchdog 1`, explicit "Log on as a service" grant (`LsaAddAccountRights`
SeServiceLogonRight – the install script does it; a domain GPO defining this right replaces local assignments, so in
GPO-managed environments add the gMSA to that GPO).

### 19.5 Folders (`DataDirectory`, `BootstrapFileGuard`)

```
%ProgramData%\AdMng\                     SYSTEM F, Administrators F, Users RX + gMSA RX (this folder only), inheritance off
  Watchdog\                             SYSTEM F, Administrators F, gMSA RX (this folder only), inheritance off
    config\appsettings.json              SYSTEM F, Administrators F, gMSA R – the service refuses to start if it can write here
    data\                                SYSTEM F, Administrators F, gMSA M (protected DACL, owner Administrators)
      watchdog.db, -wal, -shm, keys\, backup\
```

The install script sets the ACLs with SIDs (`Set-AdMngProtectedAcl`: owner Administrators recursively, then a new
protected `DirectorySecurity` that replaces every ACE – `(OI)(CI)` Full for SYSTEM and Administrators, `(OI)(CI)` Modify
for the gMSA on `data\`, Read on `config\`; 19.6) – SIDs avoid localized names such as "Administratorzy". When the
service itself creates `data\` (e.g. a changed `DataDirectory`), it applies the same protected DACL. On every start
(Windows) the DACLs of `data\`, `watchdog.db`, `-wal`, `-shm`, `keys\` and `backup\` are compared with the allow-list
{SYSTEM, Administrators, service identity} for **any** access mask, and the owner must be SYSTEM or Administrators: a deviation → Event Log
error 1003 and exit code 5, unless the bootstrap flag `AllowInsecureDataDirectoryAcl` (then health Warning
`storage.acl`). Backups made by the service land in `data\backup\` with the same ACL; BitLocker on the volume is
recommended (docs).

### 19.6 Install script (`Deploy/Install-AdMngWatchdog.ps1`, PowerShell 5.1+/7, elevated)

Parameters: `-ServiceAccount CONTOSO\svc-admngwd$`, `-InstallPath "C:\Program Files\AdMng\Watchdog"`,
`-DataDirectory`, `-Url https://admng-wd.contoso.com:8443`, `-CertificateThumbprint`, `-ViewerGroupSid`,
`-AdminGroupSid`, `-HostGroupSid`, `-AllowedSubnets`, `-MonitoringSubnets`, `-TrustExistingConfig` (see below). Steps: `Test-ADServiceAccount` (when the
AD module exists) → copy files → create `config\` and `data\` with the ACLs of 19.5 → write
`config\appsettings.json` (role, host-group and monitoring settings) → event source
(`[System.Diagnostics.EventLog]::CreateEventSource('AdMng Watchdog','Application')`; `New-EventLog` does not exist in
PowerShell 7) → grant SeServiceLogonRight → private key read ACL for the gMSA → `WerAddExcludedApplication` →
`sc.exe create … obj= "<gMSA>"` (no password) + failure/failureflag/sidtype → firewall rule `New-NetFirewallRule -Name
AdMngWatchdog-HTTPS -Direction Inbound -Protocol TCP -LocalPort <port> -Profile Domain -RemoteAddress <admin and
monitoring subnets>` → `setspn -Q HTTP/<host>` must return only the gMSA (warning otherwise) → start →
`AdMng.Watchdog.exe --health` (uses the configured FQDN; the machine's own address counts as local). Idempotent;
`-WhatIf` supported. `Uninstall-AdMngWatchdog.ps1` stops/deletes the service and the firewall rule and keeps `data\`
unless `-RemoveData`.

Details (helper module `Deploy/AdMngWatchdog.Install.psm1`, shipped with the scripts):
- **Pure ASCII** (`.ps1`, `.psm1`; `DeployScriptTests`): Windows PowerShell 5.1 reads a script without BOM in the ANSI
  code page, where the UTF-8 bytes of "–" contain a typographic quote that ends a double-quoted string early. On
  Windows CI the scripts are parsed by `powershell.exe`.
- **Update = merge**: an existing `config\appsettings.json` is read and only the settings given as parameters
  (`$PSBoundParameters`; `-Url` and `-AdminGroupSid` always) are written; defaults (`Server=Kestrel`,
  `RequireKerberos=true`, empty lists, `Certificate.Subject=<host>` when no thumbprint exists) fill only missing values;
  everything else – `DeniedSids`, `ViewerDetailLevel`, `Notifications`, `Directory`, `Server=HttpSys`, certificate
  store – is kept. A `Certificate.Subject` equal to the host of the stored `Urls` (derived by the script,
  `Test-AdMngSubjectFollowsUrl`) follows a new `-Url`; a subject set by hand stays. The changed settings are printed.
  The file is written as a new file (UTF-8 without BOM) owned by Administrators.
- **Trust before merge**: Users may create folders in `%ProgramData%`, so a local user could create
  `AdMng\Watchdog\config\appsettings.json` before the first installation and plant settings the merge would take over
  (`ViewerSids` when `-ViewerGroupSid` is omitted, `DataDirectory` – which the script then takes ownership of recursively –,
  `ViewerDetailLevel`, `AllowTier0Suppression`, …). Before the file is read, `Get-AdMngUntrustedWriters` lists every
  owner other than SYSTEM, Administrators, TrustedInstaller and the administrator running the script, and every Allow
  ACE (not inherit-only) of another account that can change the file (write, delete, `WRITE_DAC`, `WRITE_OWNER`,
  generic all/write) or replace it in `config\`, `Watchdog\` or `AdMng\` (delete child, delete, `WRITE_DAC`,
  `WRITE_OWNER`, generic all) – the right to create files, which `%ProgramData%` passes on to Users, cannot replace an
  existing file and is ignored. Any entry stops the script before the first change and names the accounts; the
  administrator reviews the file and deletes it or re-runs with `-TrustExistingConfig` (then a warning lists them).
- **ACLs**: `%ProgramData%\AdMng`, `Watchdog\`, `config\` and the data folder get owner Administrators (recursively,
  `icacls /setowner /T`) and a protected DACL that replaces every other ACE (`Set-Acl` with a new
  `DirectorySecurity`); everything inside `config\` and the data folder is reset to the inherited ACEs
  (`icacls <folder>\* /reset /T`, `-ResetChildren`) – a new folder DACL leaves the explicit ACEs of existing children in
  place. So a local user who created the folders, the bootstrap file or e.g. `data\backup` / `data\keys` before the
  installation keeps neither ownership nor an ACE.
- **Private key**: the gMSA gets Read on the key of the certificate the service will select – by thumbprint, else by
  the selector's subject rule (serverAuth, valid, private key, newest NotAfter) – also without
  `-CertificateThumbprint`; no matching certificate stops the script. Skipped for `Server=HttpSys` (netsh sslcert).

### 19.7 Publishing

Framework-dependent `win-x64` (`dotnet publish src/AdMng.Watchdog.Service -c Release -r win-x64 --self-contained false`)
so Microsoft Update servicing patches the ASP.NET Core runtime (prerequisite: **ASP.NET Core Runtime ≥ 10.0.13** x64 on
the server – 10.0.13 fixes several ASP.NET Core CVEs; the service warns when the installed runtime is older). The
app-local packages (Negotiate, Data.Sqlite/SQLitePCLRaw, MailKitLite, MSAL) are patched only by a new build – the
release checklist bumps them. Output includes `Deploy\*.ps1`. CI artifact name `AdMng-Watchdog-win-x64`.

### 19.8 Application log event ids (`EventLogIds`, source "AdMng Watchdog") – for SCOM / Zabbix / SIEM

| Id | Level | Meaning (message without directory values) |
|---|---|---|
| 1000 | Information | service started (version, instance) |
| 1001 | Error | directory unavailable (> FailoverAfter) / Warning: failover to another DC |
| 1002 | Error | delivery failing (channel id, error code) |
| 1003 | Error | storage problem (free space, size cap, data directory ACL) |
| 1004 | Warning | permission gap (self-check code) |
| 1005 | Warning | attribution degraded / event-log gap (DC host, code) |
| 1006 | Information | configuration changed (version, caller account) |
| 1007 | Warning | runtime outdated / certificate expiring within 30 days / renewed certificate whose key the service account cannot use (the previous one still serves); Error: no usable certificate key remains, TLS handshakes fail (19.3) |
| 1010 | Error | fatal start-up check failed (exit code in the message) |
| 2002 | Warning | an edit of the bootstrap file could not be loaded (malformed JSON) or holds a value that cannot be bound; the previous settings stay in effect (8.4) |
| 2300 / 2301 | Warning / Information | a job's iterations fail (job, failure code, failures in a row – when the failure starts or its code changes, then at most hourly) / the job recovered (job, number of failed iterations) – 19.1 |
| 2600 / 2601 | Information | single event viewed through `/api/v1/events/{id}` / `/events/{eventId}` (numeric id / event GUID, caller account) – the RODO access log of 17.1 |

As a Windows service the Application log is the only log sink, so its filter (`LoggingSetup.EventLogFilter`) admits
Warning and above plus the Information entries of this table (categories `StartupChecks`, `ConfigChangeNotifier`,
`EventAccessLog`, `JobHost`); other Information diagnostics (e.g. the per-request line 2700) stay console-only. The log is shared
with every application on the server, so `ThrottlingLoggerProvider` writes a Warning/Error whose category, event id and
text equal one written within the last 15 minutes only once, the next one carrying the number of suppressed repeats
(a DC outage would otherwise repeat the same per-cycle warnings thousands of times a day); distinct entries, Critical,
Information and warning 2002 always pass (2002 answers an administrator's edit – a second malformed edit within the
window must not leave only the entry of the first attempt, which predates it).

Docs recommend forwarding these and the SCM events 7036/7040 for `AdMngWatchdog` to the SIEM, plus external
monitoring of `/healthz` from `MonitoringSubnets` and, optionally, the webhook heartbeat (13.6).

---

## 20. GUI – the Watchdog tab (W-gui)

### 20.1 Shell integration

- New `Shell/IShellDocument.cs`:

```csharp
public interface IShellDocument : INotifyPropertyChanged, IDisposable
{
    string Title { get; }
    ObjectIcon Icon { get; }
    bool IsLoading { get; }
    string StatusText { get; }
    string SelectionText { get; }
    /// <summary>F5 on this tab.</summary>
    Task RefreshAsync();
    /// <summary>Unsaved changes that closing the tab would discard (default false).</summary>
    bool HasUnsavedChanges => false;
    /// <summary>Asks before discarding them; true when the tab may close (default true).</summary>
    Task<bool> CanCloseAsync() => Task.FromResult(true);
}
```

  `ObjectListViewModel` implements it (existing members; `RefreshAsync` = its refresh). `ShellViewModel.Documents`
  becomes `ObservableCollection<IShellDocument>`, new `[ObservableProperty] IShellDocument? SelectedTab` bound to
  the TabControl; `SelectedDocument` becomes `ObjectListViewModel? SelectedDocument => SelectedTab as
  ObjectListViewModel` (setter assigns `SelectedTab`) so existing list commands keep working and disable themselves on
  the Watchdog tab. Iterations over `Documents` that need lists use `.OfType<ObjectListViewModel>()`.
- Session changes (`InitializeCoreAsync`, disconnect) dispose and remove **only list documents**; the Watchdog tab
  stays (it does not depend on the LDAP session).
- Command `tools.watchdog` – `L.Commands.Watchdog` ("Watchdog" / "Watchdog – monitor zmian"), category Tools,
  shortcut **Ctrl+Shift+A**, available without an AD session; opens the single Watchdog tab or selects it if open.
  Menu **Narzędzia → Watchdog** (after "Kosz AD"), listed in the command palette (Ctrl+K). F5 on the tab calls
  `RefreshAsync`; Ctrl+W closes it. Closing a tab (Ctrl+W, its close button) or the main window with unsaved Watchdog
  configuration edits (typed secrets included, `HasUnsavedChanges` = `Config.IsDirty`) asks first
  (`Watchdog.Config.Discard`, like the property sheets and the batch editor); the window selects each such tab and
  stays open when the operator keeps the changes (`ShellViewModel.CanCloseDocumentsAsync`).
- `MainWindow.xaml`: tab header and content templates per document type (implicit `DataTemplate`s for
  `ObjectListViewModel` and `WatchdogDocumentViewModel`); a new `ObjectIcon.Watchdog` value with its glyph; the tab
  header shows a badge with `UnacknowledgedHighOrAbove` (refreshed by the poller's `/status` call every 60 s even while
  the tab is hidden).
- **"Historia zmian (Watchdog)"** (`WatchdogObjectHistoryLauncher`): command in the object context menu, the details
  pane and the property sheet (enabled when a service URL is configured and the object has a GUID); opens/selects the
  Watchdog tab, switches to Events and sets the filter `ObjectGuid` (helpdesk's most frequent question: "what happened
  to this account?").
- `AppSettings` gains `WatchdogClientSettings Watchdog { ServiceUrl (string?), PinnedCertificateSha256 (string?),
  LastSubTab (string?), FilterPresets (list of { Name, serialized EventQuery without After/Before/Wait }) }` – a URL,
  UI layout and named filters only (like saved views), never watchdog data (CLAUDE.md rule 1).
- DI (`ServiceCollectionExtensions` + `AppHost`): `IWatchdogClient` → `WatchdogHttpClient` (singleton),
  `WatchdogConnectionService`, `Func<WatchdogDocumentViewModel>` factory.

### 20.2 View models (`AdMng.Presentation/Watchdog`, CommunityToolkit.Mvvm, no WPF types, UI never blocks)

| VM | Responsibilities |
|---|---|
| `WatchdogDocumentViewModel` | `ConnectionState` {NotConfigured, Connecting, Connected, Unauthorized, Forbidden, Unreachable, CertificateInvalid, VersionMismatch, Error}; `Me` (roles → `IsAdmin`, detail level); `ServiceUrl` editor when not configured ("Adres usługi Watchdog", e.g. `https://admng-wd.contoso.com:8443`; only `https://` accepted); the connection banner always shows the host the tab talks to; `ConnectCommand`, `ChangeAddressCommand`, `RefreshCommand`; sub-tabs Events / Rules / Channels / Digest / Status (`SelectedSubTab`, remembered in settings); `IsVisible` (set by the view) switches polling mode; `ShowObjectHistory(Guid)` (used by the launcher); rollout banner from `StatusDto.RolloutModeUntil`; connection failures of single calls: Unauthorized / Forbidden / CertificateInvalid / API version change the state, a timeout or network failure of one request stays inline in its sub-tab unless the poller is failing too (backing off); the next successful poll or status restores Connected – only while the client still talks to the address of the last successful connection (after a failed connection to another address the caller, catalog, configuration and feed shown are the previous service's, so only Retry connects); Retry/F5 reconnects without reloading the configuration while it has unsaved Admin edits for the same service address |
| `WatchdogConnectionService` | builds `WatchdogClientOptions` from settings (process identity; `runas /netonly` works), `Configure` (rejects non-https), maps `WatchdogClientException.Kind` to state + localized message |
| `WatchdogPoller` | while visible: `GetEventsAsync(after = newestId, wait = 25)` loop, exponential backoff on errors (2 s → 60 s), status refresh every 30 s; while hidden (tab open in the background): only `/status` every 60 s for the header badge; the filter of a round is an immutable `EventQuery` snapshot taken on the UI thread by the last full load of the feed (never the filter view model's collections); stops on dispose |
| `WatchdogEventsViewModel` | `Items` (BulkObservableCollection of rows), filters (`WatchdogEventFilterViewModel`: min severity, categories, kinds grouped by category, object kinds, **subtree/OU** (container picker when an AD session is open), **tags** (privileged, serviceAccount, …), **attribution status** ("pokaż nieustalone"), actor (picker resolving a SID via the AD session, else SID text), text incl. workstation/IP debounced 300 ms, from/to, unacknowledged only; named presets saved in settings), `LoadOlderCommand` (`before`), new events prepended when they match the filter (one collection notification per poll page; dropped only while a full reload runs, never while an older page loads), the live feed keeps at most 2000 rows (`MaxLiveItems`, or what the operator loaded with "Wczytaj starsze"; older rows are dropped from memory and `HasMore` offers them again; nothing is trimmed while "Wczytaj starsze" runs – its page continues below the oldest row), every status round of the visible tab re-reads up to 20 rows whose attribution is still Pending (`GetEventAsync`) and updates them and the details pane, and – with a filter on what the service fills in after it stored an event (min severity, tags, attribution status, actor, rule, text) – re-reads the newest 50 matching events detected in the last 2 h and inserts those that match only since (the live long-poll skipped them: their ids are behind the cursor) at their place in id order, "N nowych" badge while scrolled away, multi-select + `AcknowledgeSelectedCommand` (`AcknowledgeManyAsync`, comment), `ExportCsvCommand` (operator-initiated export via `IFileDialogService` + `ICsvCodec`, formula-injection safe – the only way watchdog data reaches the operator's disk) |
| `WatchdogEventRowViewModel` | local time, severity, kind title, object name, canonical path, actor text ("ustalanie…", "nieustalony"), source (IP first, name with origin), lockout count ("5× dziś"), acknowledged flag |
| `WatchdogEventDetailsViewModel` | loads `EventDetails`; changes table (attribute display name from the session schema when connected, else LDAP name; old → new; flags added/removed; "wartość ukryta"), attribution (actor, source IP / workstation with origin and source confidence, method, confidence, reason + remedy, evidence list; lockouts: "Źródło błędnych haseł" with counts), related events, deliveries; `AcknowledgeCommand` (comment), `OpenInAdMngCommand` (resolves the **current** DN by an `(objectGUID=…)` search through `LdapFilter.EqualGuid` – the stored DN may be stale after a move/rename; a deleted object opens the Recycle Bin window preselected on that GUID (`IDialogService.ShowRecycleBinAsync(Guid? select)`); requires an AD session to the same domain), `UnlockAccountCommand` (AccountLockedOut, AD session to the same domain open, object still locked: plans the unlock through `IChangePlanner`/`IBatchExecutor` with the normal preview – CLAUDE.md rule 5), `CopyDetailsCommand` |
| `WatchdogConfigSession` | holds the loaded `ConfigEnvelope`, working copy, dirty flag (compares serialized JSON), `ValidateAsync`, `SaveAsync` (412 → "Konfiguracja zmieniona przez innego administratora – wczytaj ponownie"), `SaveChangeAsync(change)` (saves one change applied to the configuration as last saved; unsaved edits and typed secrets are not sent and stay unsaved on top of the new version), `RevertCommand`; shared by Rules/Channels/Digest |
| `WatchdogRulesViewModel` | rule rows (enabled toggle, name, built-in/locked badge, kinds summary, severity override, delivery summary, "wysłałaby N" during rollout); New / Duplicate / Delete (not built-in) / Reset to default (built-in, from `GetDefaultConfigAsync`) / Preview; exclusion suggestions from `StatusDto.Suggestions` ("Dodaj wyjątek" → editor prefilled, saved through the normal PUT); rollout banner with "Zakończ tryb wdrożenia" (Admin, saves `General.RolloutEnded` alone through `SaveChangeAsync` – the confirmation names only that, other unsaved edits stay unsaved); for Viewers the redacted configuration (exclusions shown as a count) |
| `WatchdogRuleEditorViewModel` | inline pane editing every `RuleSettings` field: kinds picker (catalog grouped by category), object kinds, scope DNs (container picker via `IDialogService` when an AD session is open, else text validated with `DistinguishedName.TryParse`), attributes, tags, group filter + DNs, actor exclusions, min severity / severity override, delivery (immediate, recipient groups, channels, digest, hours, aggregation, max/h); preview result (per-day counts, samples) |
| `WatchdogExclusionViewModel` | add/edit exclusions: object (picker or GUID), subtree, actor (picker resolving to a **SID** through the AD session, else SID text – names are never stored), attribute, reason (required), expiry; `CreatedBy` = `Me.Account` |
| `WatchdogChannelsViewModel` | SMTP (`SmtpSettingsViewModel`), recipient groups (`RecipientGroupsViewModel`), Teams/webhook channels (`ChannelEditorViewModel`), test buttons (`DeliveryTestViewModel`) |
| `SmtpSettingsViewModel` | all `SmtpSettings` fields; password and OAuth client secret as write-only fields (state "ustawione"/"nieustawione", typing sets `NewValue`, "Wyczyść" sets `Clear`; values never re-displayed, cleared from memory after save) |
| `ChannelEditorViewModel` | name/id, type, enabled, URL (write-only, hint shown), detail level, Teams auth mode + OAuth fields, webhook signing secret ("Generuj" creates `whsec_` + 32 random bytes client-side and shows it once for copying into the receiver), previous secret (rotation), GitHub signature, allowed hosts, timeout, rate limits |
| `DeliveryTestViewModel` | runs `SendTestEmailAsync` / `SendTestChannelAsync` with the unsaved settings; shows stages (✓/✗, detail, status codes, duration) and localized hints (`Watchdog.Hint.*`); a `secretRebindRequired` issue focuses the secret field ("adres docelowy się zmienił – wpisz hasło/sekret ponownie") |
| `WatchdogDigestViewModel` | digest settings; `PreviewCommand` shows Subject + text alternative in a read-only text box (never written to disk); `SendNowCommand` (Admin) |
| `WatchdogStatusViewModel` | cards: service, directory, attribution per DC, health checks (localized code, state, remedy), channels, storage, last 24 h counts; delivery log (`WatchdogDeliveryLogViewModel`, Admin: filter by status/channel, Retry for Dead); config audit (Admin) |
| `WatchdogDisplay` | localized names: kinds, categories, severities, object kinds, reasons + remedies, health codes, hints, validation codes, delivery stages, outbox statuses (key families below) |

Viewer role: everything read-only (editors disabled with tooltip "Wymaga roli administratora Watchdog"); Admin:
editing, tests, digest send, outbox retry. All service calls are async with cancellation; long operations show
progress in the tab; errors are shown inline (never modal for polling errors). A request cancelled because a newer one superseded it (or the operator cancelled it) is never an error – the client reports it as `WatchdogClientException(Cancelled)`, view models treat any failure after their token was cancelled as superseded.

### 20.3 Views (`AdMng.App/Views/Watchdog`)

`WatchdogView` (connection banner + sub-tab strip + content), `WatchdogEventsView` (filter bar, virtualized
`DataGrid` with severity badge column, details pane on the right/bottom like the main window's details pane),
`WatchdogRulesView` (list + inline editor), `WatchdogChannelsView`, `WatchdogDigestView`, `WatchdogStatusView`.
Fluent theme resources only (severity colours mapped to theme brushes: Critical/High
`SystemFillColorCriticalBrush`, Medium `SystemFillColorCautionBrush`, Low `AccentFillColorDefaultBrush`, Info
`TextFillColorSecondaryBrush`; Critical is told apart from High by a 2 px ring in `TextFillColorPrimaryBrush` – dark on
light, light on dark, the window text colour in high contrast – and keeps its semibold critical-coloured severity text),
automation names on every input, keyboard navigation, no binding errors (UI test listener).
Layout (checked by UI tests at the screenshot size 1440 × 900): the connection banner shows "state host" once and a
second line (`BannerMessage`) only to explain a problem or the missing address; the main window's object details pane
(and its column) is hidden while the Watchdog tab is selected (`ShellViewModel.IsDetailsPaneShown`; the operator's
choice `IsDetailsPaneVisible` is kept for the object lists); the feed and its details pane (400 px, min 300) are
separated by a `GridSplitter`, and the event title column fills the width the feed has left right of severity and
time (`GridViewFill`, 230–380 px; a column the operator resized keeps its width); the saved-filter list
(`Filter.HasPresets`) is shown only when presets exist, with the placeholder "Zapisane filtry"; the details button is
"Pokaż w AD Manager" (the product name; "AD_MNG" lost its underscore as an access key); the SMTP card keeps its value
column at least 250 px wide.

### 20.4 Localization keys (both `Strings.resx` and `Strings.pl.resx`)

Fixed keys under `Watchdog.*` (constants in `L.Watchdog`) for titles, labels, buttons, states and messages, e.g.
`Watchdog.Title` ("Watchdog"), `Watchdog.Tab.Events` ("Zdarzenia"), `Watchdog.Tab.Rules` ("Reguły"),
`Watchdog.Tab.Channels` ("Kanały i SMTP"), `Watchdog.Tab.Digest` ("Raport dzienny"), `Watchdog.Tab.Status` ("Stan"),
`Watchdog.Connection.NotConfigured`, `Watchdog.Connection.Unauthorized` ("Usługa odrzuciła uwierzytelnienie
Kerberos – sprawdź SPN i rekord DNS (docs/WATCHDOG.md)"), `Watchdog.Connection.Forbidden` ("Brak uprawnień – poproś
o dodanie do grupy AD z rolą Watchdog"), `Commands.Watchdog`.
Dynamic families (tested for completeness against the enums/constants like `DisplayNames`):
`Watchdog.Kind.{WatchEventKind}`, `Watchdog.Category.{EventCategory}`, `Watchdog.Severity.{Severity}`,
`Watchdog.ObjectKind.{WatchObjectKind}`, `Watchdog.Reason.{code}` + `.Remedy`, `Watchdog.Health.{code prefix}` + `.Remedy`
(every health code the service and the engine report – e.g. `certificate.expiry`, `storage.size`, `rules.planner`,
`attribution.collector` – resolves to a prefix with its own text; a Presentation test scans their sources),
`Watchdog.Hint.{hint}`, `Watchdog.Validation.{code}`, `Watchdog.Stage.{DeliveryStage}`,
`Watchdog.Outbox.{OutboxStatus}`, `Watchdog.State.{ConnectionState}`, `Watchdog.DetailLevel.{DetailLevel}`,
`Watchdog.DeliveryHours.{DeliveryHours}`, `Watchdog.SmtpSecurity.{…}`, `Watchdog.SmtpAuthentication.{…}`,
`Watchdog.TeamsAuthMode.{…}`, `Watchdog.GroupFilter.{…}`, `Watchdog.EntraCloud.{…}`, `Watchdog.Tag.{tag}`,
`Watchdog.AttributionStatus.{…}`, `Watchdog.Suggestion.{reasonCode}`, `Watchdog.SourceOrigin.{method}`; fixed keys for
the rollout banner, object history command (`Commands.WatchdogHistory` "Historia zmian (Watchdog)"), unlock and bulk
acknowledge.

### 20.5 Smoke test, UI tests, screenshots

- `SmokeTest` opens the Watchdog tab in the NotConfigured state (published app, real DI).
- `tests/AdMng.App.UiTests/WatchdogViewTests.cs`: renders every sub-tab with `FakeWatchdogClient` + Polish sample data
  off-screen, asserts no binding errors, keyboard focus order, Viewer (redacted config) vs Admin enabling, rollout
  banner, tab badge.
- `ScreenshotTests`: add `watchdog-events.png` ("Watchdog – zdarzenia"), `watchdog-rules.png` ("Watchdog – reguły"),
  `watchdog-channels.png` ("Watchdog – kanały i SMTP"), `watchdog-status.png` ("Watchdog – stan usługi") to the
  manifest, so every CI run publishes them to the `screenshots` branch (the user asked for pictures per release).

---

## 21. Packages (`Directory.Packages.props`, scaffold)

Verified on NuGet on 2026-10-14. **All Microsoft 10.0.x packages – the existing ones (System.DirectoryServices.Protocols,
Microsoft.Extensions.Hosting/Logging.Abstractions/DependencyInjection(.Abstractions)) and the new ones – move to
10.0.13** (security release 2026-10-13, 13 ASP.NET Core CVEs) in one scaffold commit. Microsoft.AspNetCore.Authentication.Negotiate
10.0.12 now carries advisory GHSA-vrrp-6v32-2p8g, so with warnings-as-errors its restore fails (`NU1903`); a Web project
referencing a 10.0.12 library line was also verified to build cleanly with 10.0.13 (no NU1605). All 10.0.13 versions
exist on NuGet (checked). `global.json` stays (`10.0.100`, `rollForward: latestFeature` – SDK 10.0.112 locally,
≥ 10.0.113 in CI). Scaffold check: `dotnet restore AdMng.slnx --force-evaluate` shows no NU19xx/NU1510.

```xml
<PackageVersion Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.13" />
<PackageVersion Include="Microsoft.AspNetCore.Authentication.Negotiate" Version="10.0.13" />
<PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.13" />
<PackageVersion Include="MailKitLite" Version="4.18.1" />
<PackageVersion Include="Microsoft.Identity.Client" Version="4.90.1" />
<PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.13" />
<PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.11.0" />
<PackageVersion Include="SmtpServer" Version="11.1.0" />
<PackageVersion Include="xunit.v3.extensibility.core" Version="3.2.2" />
<PackageVersion Include="xunit.v3.assert" Version="3.2.2" />
```

| Package | Used by | Why |
|---|---|---|
| Microsoft.Extensions.Hosting.WindowsServices | Service | `AddWindowsService` |
| Microsoft.AspNetCore.Authentication.Negotiate | Service | Kerberos on Kestrel |
| Microsoft.Data.Sqlite | Service | storage (SQLitePCLRaw 2.1.13) |
| MailKitLite 4.18.1 | Service | SMTP incl. XOAUTH2, STARTTLS; ≥ 4.16 fixes CVE-2026-41319 (MimeKitLite 4.18.1 ≥ 4.15.1 fixes CVE-2026-30227); no BouncyCastle (no S/MIME needed). MailKit 5.0 (unreleased) retires the Lite package and changes APIs – MailKit types stay inside `SmtpEmailTransport`, `MimeMessageFactory`, `SmtpErrorClassifier`, `SmtpCertificateValidation` |
| Microsoft.Identity.Client 4.90.1 | Service | OAuth2 client credentials (Exchange Online SMTP, Teams OAuth trigger) |
| Microsoft.AspNetCore.Mvc.Testing | Service.Tests, IntegrationTests | `WebApplicationFactory<Program>` (+ `UseKestrel(0)`) |
| Microsoft.Extensions.TimeProvider.Testing | Core.Tests, Service.Tests | `FakeTimeProvider` with timers (scheduling, holds, retries) |
| SmtpServer 11.1.0 | Watchdog.Testing | in-process SMTP sink (plain/STARTTLS/implicit TLS, AUTH PLAIN/LOGIN, SIZE) |
| xunit.v3.extensibility.core / xunit.v3.assert 3.2.2 | Watchdog.Testing | the abstract `WatchdogStoreContractTests` ([Fact]s in a library; same version line as `xunit.v3` 3.2.2 – the 4.x line is not adopted here) |

Not allowed: `System.Net.Mail.SmtpClient`, netDumbster (its MimeKit 4.7.1 fails the build with NU1902),
Microsoft.Graph SDK (55 MB; Graph sendMail is phase 2 with plain HttpClient), any mocking library, FluentAssertions.
Shared-framework features used without packages: Kestrel, Data Protection, `System.Threading.RateLimiting`,
`System.Net.ServerSentEvents` (phase 2), `IHttpClientFactory`, **`System.Diagnostics.EventLog`** (EventLogSession /
EventLogReader and the EventLog logger ship in `Microsoft.AspNetCore.App`; a direct PackageReference in a Web project
fails with `NU1510`). Rule: projects with `Sdk.Web` or `FrameworkReference Microsoft.AspNetCore.App` (Service,
Watchdog.Testing) reference no `Microsoft.Extensions.*` / `System.*` package the framework supplies; the plain
net10.0 Client and Core reference `Microsoft.Extensions.Logging.Abstractions` (Client, Core) and
`Microsoft.Extensions.DependencyInjection.Abstractions` (Core).

Supply chain (scaffold): `nuget.config` at the repo root with `<clear/>`, nuget.org only and
`<packageSourceMapping>` (`*` → nuget.org); `Directory.Build.props` sets `<NuGetAudit>true</NuGetAudit>`,
`<NuGetAuditMode>all</NuGetAuditMode>` (transitive packages too; with warnings-as-errors any NU190x fails the build).
NuGet lock files (`RestorePackagesWithLockFile` + `--locked-mode`) are deferred (phase 2, repo-wide workflow change).

---

## 22. Test plan

Conventions: xUnit v3 `Assert`, fakes over mocks, Polish data with diacritics, `TestContext.Current.CancellationToken`,
no `Thread.Sleep` (use `FakeTimeProvider` / signals), 0 warnings.

### 22.1 `tests/AdMng.Watchdog.Core.Tests` (W-core)

| Area | Tests (one file per feature) |
|---|---|
| Contracts | JSON round-trip of `WatchEvent`, `WatchdogConfiguration` (defaults survive missing properties), `SecretField.ToString` never prints NewValue, `EventQuery.ToQueryString` encoding |
| Fake directory | `FakeDirectoryWatchdogTests` (in AdMng.Core.Tests/Testing): RootDSE, USN semantics (member change bumps only the group; rename/move bumps only the object; Recycle Bin restore re-activates links without a group bump), NC boundaries (a domain subtree search never returns CN=Sites objects), explicit DC seeding + failback USN offsets, metadata XML via the `Touch(entry, changedAttributes)` hook, extended DN, lockout computed bit expiry and lockoutTime reset at logon after expiry (opt-in `LockoutDuration`), default fake unchanged (no DC objects, lockoutTime>0 = locked), HideAttributes, invocationId switch, new FakeSchema attributes accepted by ModifyAsync |
| Detection | `DetectionModeSelectorTests` (every row of 10.3 incl. Failback to a known DC vs Resync for a never-seen DC), `BaselineTests` (no events, W = H read before the scan, chunked commits with `NewState = null`, HealthBaselineCompleted), `IncrementalTests` (sample company: create user/computer/group/OU; modify; noise only → no event and no write; `UsnOverlap` idempotency; crash between chunks → no duplicates; crash mid-Resync → Resync again, never Incremental from a foreign W), `CreationSettlerTests` (New-ADUser → password → enable inside the window = one UserCreated with the final state; risky final bits → AccountFlagsChanged High; PASSWD_NOTREQD on a disabled account Low; deleted inside the window → Created then Deleted), `ClassifierTests` (every row of the 10.6 table incl. unlock before expiry → AccountUnlocked, lockoutTime reset after expiry → no event, PSO lockout duration, PasswordAgeReset vs PasswordChanged by metadata, gMSA rollover dropped by default, dMSA links on both sides, altSecurityIdentities, msDS-GroupMSAMembership, FsmoRoleChanged), `KeyCredentialSeverityTests` (10.6.4 matrix; rename of a user with msDS-KeyCredentialLink → ObjectRenamed only), `UacFlagClassifierTests` (bit table), `EventTaggerTests` (privileged only from PrivilegeState, adminCountOrphan, redirected default containers via wellKnownObjects, longDisabled via lastLogonTimestamp), `MoveRenameTests` (OU rename does not report children; child moved + parent renamed in one cycle; move out of scope → Removals; move back in → ObjectAppeared, never ObjectRestored), `MembershipTests` (add/remove, ranged > 1500 values, deleted member silently removed, rename of member is not a change), `PrivilegeEvaluatorTests` (direct, nested 3 levels, cycle, primary group 512, revoke, tiers, baseline silent, watchdog role/host groups are High roots, crash after a membership chunk → PrivilegeGranted exactly once after restart), `DeletedObjectsTests` (live → deleted, unknown tombstone in scope, recycled, restore with memberships re-inserted and PrivilegeGranted tag restored, computer with 3 msFVE children deleted → 1 event, dnsNode / msDS-Device churn → 0 events, unknown tombstone whose parent is a deleted user → dropped), `ReconcilerTests` (disappeared → Removals, moved out of scope, resync version-aware stale DC, config/PKI rows never reconciled as disappeared), `ConfigurationPartitionTests` (DC added/removed, other-domain DC High, RODC excluded from attribution sources, FSMO moves, Recycle Bin / dSHeuristics, PKI template flag/EKU/ACL change, Exchange-style schema burst → one SchemaChanged), `PermissionSelfCheckTests` (HideAttributes → gap; a deletion between the two searches is not a gap; DenyDeletedObjects; PSO container unreadable), `SecurityDescriptorSummaryTests` (owner + ACE diff, DS-Replication-Get-Changes named, ACE re-ordering / identical rewrite → no event), `ValueCanonicalizerTests` (DN-Binary hashes the binary part only), `ReplicationMetadataXmlParserTests` (NULs, unescaped `&`), `ReplicationMetadataDecoderTests` (captured lab blob fixture, wrong length), `AttrTypResolverTests` (three vectors + msDS-IntId) |
| Scans | `AccountExpiryScannerTests` (thresholds 14/7/1 in calendar days to the last valid day, '1 day' fires the day before, Sunday expiry announced on Friday, holiday stretch 24–26 Dec, dedup, downtime catch-up records larger thresholds, changed expiry re-arms, expired once, ADUC end-of-day display), `PasswordExpiryScannerTests` (scope, 0 and never), `LockoutMonitorTests`, `BurstDetectorTests` (mass lockout with the percentage threshold, mass deletion by class: 10 users Critical, 10 computers nothing, 50 computers High; cooldown), `PasswordSprayDetectorTests` (fixture 4771/4776 sets: 20 accounts from one IP → event; 20 lockouts from different IPs → none; disabled by default) |
| Attribution | `SecurityEventParserTests` (all fixtures, by-name parsing, unknown fields, `-`, %% decoding, 4740 caller mapping, 1102 UserData, masking), `IngestionReductionTests` (5136 value kept only for watched non-secret attributes, no values without a schema, excluded-subtree / unwatched-class records dropped, budget → HealthEventLogGap flooding), `EventSourceGuardTests` (foreign provider / unknown Computer dropped, also in ForwardedEvents mode), `SamAccountControlDecoderTests` (the four Microsoft samples: 0x0→0x15 = Disabled+PwdNotRequired+Normal; 0x15→0x211; 0x80→0x2080; 0x80), `SecurityEventQueryBuilderTests` (≤ 20 IDs per Select, escaping, suppress), `AttributionCorrelatorTests` (strong vs weak keys, attribute confirmation, ambiguous, SYSTEM, self change, 4723 → selfChange / 4724 → passwordReset, sync account tag, attribution-dependent severity re-resolution, operation grouping, retries then each reason code), `LogonSourceResolverTests` (types 3/8/10, loopback, From bound = LogonLookback, timeout, one query per DC, negative cache, SOURCE_LOGON_NOT_FOUND, source confidence caps), `LockoutSourceResolverTests` (empty caller → 4771/4776 sources with counts; caller = DC/server → sources queried), `EventLogGapDetectorTests` |
| Rules | `RuleMatcherTests` (each condition; actor exclusions by SID only), `ExclusionMatcherTests` (attribute exclusion semantics, expiry), `SeverityResolverTests` (order base → bumps → attribution → overrides; HR-attribute change of a privileged user stays Info; privileged own password change → Info, reset → High; krbtgt exactly Medium; AdminCountSet after PrivilegeGranted → Info), `DefaultRulesTests` (ids unique, health locked with AllEnabledChannels, every kind with RecordedByDefault is covered by at least digest), `ConfigurationValidatorTests` (every code of 8.3 incl. secretRebindRequired, tier0Suppression, invalidAttribute, invalidScope, oauthHostNotAllowed, invalidHostName, both time-zone id forms), `ConfigChangeAuditorTests` (severity per change class, no secret values in Changes), `ConfigRedactorTests`, `RolloutModeTests`, `RulePreviewServiceTests`, `ConfigTokensTests`, `TimeZoneResolverTests` (IANA, Windows id, built-in map when ICU conversion is unavailable) |
| Notifications | `NotificationPlannerTests` (targets, AllEnabledChannels, NotifyManager, business hours, rollout mode, repeat suppression, holds incl. attribution-dependent kinds, provisional actor matches, previous-configuration planning of WatchdogConfigurationChanged), `AggregationTests`, `ThrottleTests` (RescheduleAsync), `FollowUpPlannerTests` (via GetMessagesForEventAsync + SentWithPendingAttribution), `DispatchJobTests` (FakeTimeProvider; re-match with final severity drops a privileged self password change), `OutboxSenderJobTests` (fake transport returning each `DeliveryOutcome`; Deferred counts no attempt; RetryPolicy schedules with a seeded `Random`), `SignalsTests` (two subscriptions on one signal both wake; no lost wake-ups), `HeartbeatJobTests`, `DigestBuilderTests` (sections, caps, weekly sections, empty heartbeat, Monday after a weekend covers Friday 07:30 → Monday 07:30, lockouts rolled up per account and day, password resets vs own changes), `ScheduleTests` (DST spring/fall in Europe/Warsaw), `PolishHolidaysTests` (2026, 2027, 24 Dec from 2025) |
| Formatting | `EmailSubjectBuilderTests` (pl/en table 14.2, CR/LF stripping, 200 chars), `UntrustedTextTests` (U+202E, U+2066, zero-width → visible escapes; `https://evil.example/x` → `hxxps://evil[.]example/x`; `www.` defanged; UPNs and host names unchanged), `EmailAlertFormatterTests` (HTML-encoded hostile values like `<script>`, `[Kliknij](https://evil)`, `‮` and a URL in description and in a nested group name of a Via path; secret masked; detail levels; source row IP first with name origin; lockout source row), `TeamsCardFormatterTests` (only AC 1.4 element types; AD strings only in TextRun; defanged URLs; ≤ 24 576 bytes with 500 Polish events; one attachment), `WebhookPayloadFormatterTests` (schema, Minimal level strips values), `StandardWebhookSignerTests` (both vectors of 14.5, rotation = two signatures), `TeamsUrlValidatorTests`, `NotificationStringsTests` (every generated key in en and pl) |

### 22.2 `tests/AdMng.Watchdog.Service.Tests` (W-service)

- `WatchdogFactory : WebApplicationFactory<Program>`: temp data dir per instance (`ClearAllPools` + delete on dispose),
  `Watchdog:Authentication=Test` + `TestAuthenticationHandler` (`X-Test-Account`, `X-Test-Sids`), ephemeral Data
  Protection, `FakeTimeProvider`, `FakeDirectorySession` behind a test `IDirectorySessionProvider`,
  `FakeSecurityEventSource`, Core engines replaced by fakes where an API test only needs a canned answer.
- API: authz matrix (anonymous 401, unknown group 403, Viewer GET ok / PUT 403, Admin PUT ok, PrimaryGroupSid
  grants, DeniedSids override Admin), CSRF header required (400) and form posts (415), API version header, `/healthz`
  (loopback and MonitoringSubnets allowed, other remote 403), events paging (`after`/`before`/all filters incl.
  subtree, tags, attribution status, text on workstation/IP), long-poll returns when an event is appended and after
  `wait`, event details + deliveries, ack and bulk ack, Viewer responses at `ViewerDetailLevel`, config GET: Admin full /
  Viewer redacted (`IsRedacted`), PUT of a redacted envelope → 400, PUT stores secrets / PUT with `Clear` / 412 conflict
  leaves secrets unchanged / 400 issues / audit summary never contains secret values, **secret rebinding**: host
  changed without NewValue → 400 `secretRebindRequired` (PUT and test send; the SMTP sink sees no AUTH; OAuth with a
  foreign host → no token request), WatchdogConfigurationChanged planned for the previous recipients, rule preview,
  digest preview, outbox list + retry, HTML event page headers, event access logged with id only.
- **`LogHygieneTests`**: capture all log output (console + a test EventLog sink, exceptions formatted with
  `ToString()`) while running the service scenario plus fault injections – LDAP NoSuchObject on a Polish DN, malformed
  5136 XML, SMTP 550 on a recipient, an `int.Parse` failure on an AD value, an unhandled exception in an endpoint and
  in a job – and assert that no snapshot/event string value, no recipient address and no secret appears.
- Storage: `SqliteStoreContractTests` (subclass of W-core's abstract contract suite, incl. the case-insensitive
  attribute lookup after a round trip), migrations (fresh, upgrade with pre-migration backup, newer version refused),
  retention batches + backup rotation (pre-migration backups too), size cap purge, AUTOINCREMENT cursor after purge,
  outbox payload NULL after send, `secure_delete`: after purge + checkpoint the db and WAL bytes no longer contain a
  purged Polish value, `temp_store = MEMORY` set on every connection, config + secrets + audit in one transaction,
  concurrency (single writer, readers during writes).
- Hosting: `DataDirectoryTests` (Windows: folder created with the protected DACL; a parent with Users:RX → refuses to
  start with exit code 5 via `IProcessTerminator`), `BootstrapFileGuardTests` (Windows), `DevelopmentAuthGuardTests`
  (non-loopback listener → refused), `RuntimeVersionCheckTests`, `KerberosOnlyMiddlewareTests` (non-Kerberos identity
  → 401), TLS 1.1 handshake to the API fails (real socket), HTTP.sys NTLM-only client → 401 (Windows CI).
- Transports: `SmtpEmailTransportTests` against `SmtpSink` (plain relay, STARTTLS with self-signed certificate:
  System → ChannelBroken, PinnedSha256 → Delivered; implicit TLS 465; AUTH PLAIN only after TLS; Polish subject
  RFC 2047; Message-ID domain; In-Reply-To) and `ScriptedSmtpServer` (421/451/452 Transient, 535 5.7.139 Auth +
  hint, 550 5.7.68 hint, 552 Permanent, XOAUTH2 payload `user=…^Aauth=Bearer …^A^A`, no 8BITMIME → base64);
  `TeamsWorkflowTransportTests` / `WebhookTransportTests` against `HttpSink` (202, 400, 401 with SAS text, 404, 410,
  429 + Retry-After, 500, timeout, redirect not followed, URL query never logged, signature verified by the sink,
  webhook-id stable across retries, timestamp per attempt).
- `DeliveryTesterTests` (stages), `ConfigSecretsApplierTests` (bindings), `DestinationGuardTests`,
  `KerberosOnlyEventsTests` (NTLM identity rejected), `SidRoleRequirementTests`, `CertificateSelectorTests` (in-memory
  store abstraction), `JobHostTests` (backoff, wake via subscriptions, fatal path through a fake `IProcessTerminator`),
  `ChannelRateLimitersTests` (manual replenishment on `FakeTimeProvider`), SMTP/HTTP transports reject TLS < 1.2.
- Client: `WatchdogHttpClientTests` against the real server (`UseKestrel(0)` + `StartServer()`): every method,
  error-kind mapping (stop server → Unreachable, 401/403/404/412/400), long-poll timeout handling, headers sent,
  non-https base address rejected, credentials offered only to the configured origin.
- Windows-only (skipped elsewhere): `WindowsSecurityEventSourceTests` (`ADMNG_WINDOWS_EVENTLOG_TESTS=1`, reads 4624
  records from the local Security log of the CI runner with a structured query; parser round-trip of real XML),
  `DpapiNgSecretStoreTests`.

### 22.3 `tests/AdMng.Presentation.Tests/Watchdog` (W-gui)

`FakeWatchdogClient` (in `tests/AdMng.Watchdog.Testing/Client`) scripted per test: connection states for each
`WatchdogClientErrorKind`, roles, events feed (paging, polling prepend with filters, badge), details, ack, rules
editing (dirty tracking via JSON, validation issues mapped to fields, 412 conflict), secret fields (NewValue/Clear,
never re-shown), webhook secret generation format, delivery test rendering, digest preview, status cards, delivery log
retry, shell integration (command opens/selects the single tab, survives AD reconnect, F5/Ctrl+W), object history
launcher (filter by objectGUID), OpenInAdMng (current DN via objectGUID search; deleted object → Recycle Bin window
preselected), unlock through the change planner (preview, no direct write), filters (subtree, tags, attribution
status, presets saved in settings without event data), bulk acknowledge, hidden-tab `/status` polling and badge,
rollout banner + end command, exclusion suggestions, Viewer with a redacted configuration, https-only service URL,
localization completeness for every `Watchdog.*` family, language switch.

### 22.4 `tests/AdMng.Watchdog.IntegrationTests` (W-testenv) – Samba lab, skipped without `ADMNG_TEST_LDAP_*`

Fixture: Administrator session creates `OU=WatchdogIT-<run id>` (throwaway; tree-deleted at the end; leftovers of
aborted runs removed first), a read-only session as `odczyt.test` (`ADMNG_TEST_LDAP_READONLY_*`), the real engines
resolved from `services.AddWatchdogCore()` (interfaces only, never constructors) + `InMemoryWatchdogStore` +
`LdapDirectoryConnector`, `CreationSettleWindow = 0` unless a test exercises the window. The engine watches the whole lab
domain (the baseline over ~3 000 objects is part of the fixture and must finish in < 60 s); assertions only look at
events whose object is under the test OU (other lab users may change the shared lab concurrently).
Scenarios (each: perform LDAP change with the admin session → run one cycle with the read-only session → assert):
create user → UserCreated (+ initial values); modify department/title → AttributesChanged old → new; disable →
AccountDisabled; 5 bad binds (simple bind with wrong password) → AccountLockedOut with LockedUntil; admin unlock →
AccountUnlocked; bad bind only → no event; successful bind → no event (lastLogonTimestamp noise); accountExpires in
5 days → AccountExpiring(7); add to group → GroupMemberAdded; nested privileged group (a test group listed in
`AdditionalCriticalDns`) → PrivilegeGranted with Via; rename the OU → children not reported; move user → ObjectMoved;
1600-contact group → ranged extended-DN membership; delete user → with the admin session ObjectDeleted (Deleted
Objects readable), with odczyt.test → reconciliation ObjectDisappeared + `permission.deletedObjects` Warning;
hardened DACL object (binary SD written in the test OU) → `permission.uSNChanged` Error; binary replPropertyMetaData
decoded and originating DC = the lab DC; computer created → ComputerCreated; multi-step creation (add disabled →
set password → enable) with a 2-minute settle window on a fake clock → one UserCreated with the final state;
`pwdLastSet` 0 → -1 on a test user → PasswordAgeReset (unicodePwd metadata unchanged); deleting a test computer that
has a child object → one ObjectDeleted; `msDS-HasDomainNCs` DC discovery returns the lab DC; `wellKnownObjects` parsed.
End-to-end (`ServiceEndToEndTests`): in-process `WebApplicationFactory<Program>` with real LDAP (simple bind, lab
only), SMTP → Mailpit (`ADMNG_TEST_SMTP_*`, assert via `GET /api/v1/messages`: Polish subject, HTML encoded values,
no secret values) and `HttpSink` for a webhook channel (signature valid); config PUT via API; status endpoint shows
directory Ok, attribution `SOURCE_DISABLED` (Linux). Optional `KerberosNegotiateTests` (`ADMNG_TEST_KERBEROS=1`) with
`tools/testlab/watchdog/krb5-lab.sh` – **authentication only** on Linux (the Negotiate handler emits no SID claims
there): `/api/v1/me` returns 403 (not 401) and a test-only endpoint reports `AuthenticationType` "Kerberos"; an
NTLM-only client gets 401. The SID-role path is covered by the Windows CI tests.

### 22.5 Fixtures

`tests/AdMng.Watchdog.Testing/Fixtures/SecurityEvents/*.xml` (W-core): Microsoft samples 4720, 4738, 4740, 5136
(Appendix A) plus 4741, 4742, 4732, 4781, 4767, 4624 (copy from the Microsoft auditing docs' sample XML, `- ` tree
prefixes removed) and synthetic: 5136 Value Deleted/Value Added pair sharing an OpCorrelationID, 4624 v2 network logon
(LogonType 3, Kerberos, IpAddress `::ffff:10.0.0.12`, TargetLogonId 0x30dc2), 1102 with UserData, 4728 with MemberSid,
5139 move, 4723 and 4724 for the same target, 4767, 4771 (Status 0x18, Client Address) and 4776 (Status 0xC000006A,
Workstation) sets for lockout-source and spraying tests (one IP × 20 accounts; 20 IPs × 1 account), a record with a
foreign provider name, a 5138 restore. `Fixtures/ReplMetadata/lab-user.bin` (base64 text of a lab `replPropertyMetaData` blob, captured by W-core
with ldapsearch as odczyt.test from a throwaway object) and `windows-xml.txt` (XML values incl. a `&` DN).

---

## 23. Test environment (`tools/testlab/watchdog`, W-testenv)

| File | Purpose |
|---|---|
| `README.md` (Polish) | how to run the watchdog against the Samba lab, Mailpit UI (http://127.0.0.1:8025), webhook sink, scenario script, Kerberos option, cleanup |
| `start-mailpit.sh` / `stop-mailpit.sh` | download pinned Mailpit **v1.31.5** linux-amd64 into `~/.cache/admng/mailpit/` (SHA-256 pinned in the script; downloaded into its own empty directory), run `mailpit --smtp 127.0.0.1:1025 --listen 127.0.0.1:8025 --smtp-auth-accept-any --smtp-tls-cert sans:localhost --smtp-tls-key sans:localhost --enable-chaos --disable-version-check` in the background (PID file); `--require-starttls` option |
| `webhook-sink.py` | Python 3 stdlib HTTP server on 127.0.0.1:8091: verifies Standard Webhooks signatures (secret from env `SINK_SECRET`), checks timestamp tolerance 5 min, prints type/id/object, scriptable responses (`SINK_STATUS=202\|429\|500`, `SINK_RETRY_AFTER`), Teams mode (accepts the `message` envelope, prints card title) |
| `appsettings.Lab.json` | bootstrap for the lab: `Authentication=Development`, `Hosting.Urls=http://127.0.0.1:5089` (HTTP allowed only in Development on loopback), `Directory.Server=127.0.0.1`, `Authentication=SimpleBind`, `Security=None`, `AllowInsecureLab=true`, `LabSimpleBindUser=odczyt.test@contoso.test`, `DataDirectory=~/.local/share/admng-watchdog-lab` |
| `run-watchdog-lab.sh` | starts Mailpit + sink, exports `ASPNETCORE_ENVIRONMENT=Development`, `ADMNG_WATCHDOG_LDAP_PASSWORD` (from `ADMNG_TEST_LDAP_READONLY_PASSWORD`, never written to files), runs `dotnet run --project src/AdMng.Watchdog.Service --launch-profile Lab`; `--reset` deletes the lab data folder |
| `configure-lab.sh` | curl calls against the Development API: SMTP → Mailpit, webhook channel → sink, recipient group `it-admins` = `admin@contoso.test`, digest time = now + 2 min |
| `lab-scenario.sh` | as Administrator: creates `OU=WatchdogDemo`, performs the scenario of 22.4 with ldapadd/ldapmodify/ldapdelete (Polish names: Zofia Żak, Łukasz Wiśniewski), bad binds for a lockout; `--cleanup` tree-deletes the OU |
| `krb5-lab.sh` | optional real Kerberos: throwaway OU with service user + SPN `HTTP/localhost` + AES keys, keytab via `/usr/bin/python3.12 /usr/bin/samba-tool domain exportkeytab`, private `krb5.conf` with `kdc = 127.0.0.1`, prints `KRB5_CONFIG`/`KRB5_KTNAME`; `--cleanup` |

The launch profile `Lab` lives in `src/AdMng.Watchdog.Service/Properties/launchSettings.json` (W-service) and sets
`ADMNG_WATCHDOG_CONFIG=../../tools/testlab/watchdog/appsettings.Lab.json` (the host adds the JSON file named by this
variable after the other configuration files, section 19.1). The lab is shared: scripts touch only their own throwaway OUs.
`tools/testlab/README.md` gets a short section linking to `watchdog/README.md`.

---

## 24. CI (`.github/workflows/ci.yml`, W-testenv)

- **windows** job (existing): `dotnet test AdMng.slnx` now includes the watchdog test projects (env
  `ADMNG_UI_TESTS=1`, new `ADMNG_WINDOWS_EVENTLOG_TESTS=1`); after the app publish add:
  `dotnet publish src/AdMng.Watchdog.Service/AdMng.Watchdog.Service.csproj -c Release -r win-x64 --self-contained false -o publish-watchdog`,
  `publish-watchdog/AdMng.Watchdog.exe --smoke-test` (fail on non-zero), upload artifact **`AdMng-Watchdog-win-x64`**
  (14 days). Screenshot step unchanged (picks up the new watchdog PNGs).
- **SDK**: `actions/setup-dotnet` installs the latest 10.0.x SDK (≥ 10.0.113, runtime 10.0.13) – `global.json`
  (`10.0.100`, `latestFeature`) allows it; restore runs with the repo `nuget.config` (source mapping, audit mode all).
- **integration** job (ubuntu, Samba lab): add **one step per project** – `dotnet test tests/AdMng.Watchdog.Core.Tests -c Release`,
  `dotnet test tests/AdMng.Watchdog.Service.Tests -c Release` (VSTest-mode `dotnet test` accepts a single project or
  solution; `MSB1008` otherwise) – Linux coverage of the non-Windows code paths, then
  `tools/testlab/watchdog/start-mailpit.sh`, then `dotnet test tests/AdMng.Watchdog.IntegrationTests -c Release` with `ADMNG_TEST_LDAP_*`,
  `ADMNG_TEST_LDAP_READONLY_USER=odczyt.test@contoso.test`, `ADMNG_TEST_LDAP_READONLY_PASSWORD=Passw0rd!Lab2026`,
  `ADMNG_TEST_SMTP_HOST=127.0.0.1`, `ADMNG_TEST_SMTP_PORT=1025`, `ADMNG_TEST_MAILPIT_API=http://127.0.0.1:8025`;
  `stop-mailpit.sh` in an `if: always()` step. Timeout raised to 40 min.
- No secrets are needed (lab passwords are public test values).

---

## 25. Work split

### 25.1 Scaffold step S0 (one agent, sequential, before the split; ends with a green build and a commit)

1. `Directory.Packages.props`: the entries of section 21, **and every existing Microsoft 10.0.x package bumped to
   10.0.13** in the same commit; `nuget.config` (source mapping) and `NuGetAudit`/`NuGetAuditMode=all` in
   `Directory.Build.props` (section 21). Check: `dotnet restore AdMng.slnx --force-evaluate` shows no NU19xx / NU1510.
2. Create the projects with csproj files and references (section 2), add them to `AdMng.slnx`:
   `src/AdMng.Watchdog.Contracts`, `src/AdMng.Watchdog.Core` (refs AdMng.Core, Contracts; packages
   Logging.Abstractions, DependencyInjection.Abstractions; `InternalsVisibleTo` Core.Tests; resx files with one
   key each), `src/AdMng.Watchdog.Service` (Sdk.Web, AssemblyName `AdMng.Watchdog`, refs Core, Directory, Contracts;
   packages of 21 – **no** System.Diagnostics.EventLog / Microsoft.Extensions.* references (NU1510);
   `InternalsVisibleTo` Service.Tests, IntegrationTests), `src/AdMng.Watchdog.Client` (ref Contracts; package
   Microsoft.Extensions.Logging.Abstractions),
   `tests/AdMng.Watchdog.Core.Tests` (refs Core, Contracts, Watchdog.Testing, AdMng.Testing; TimeProvider.Testing),
   `tests/AdMng.Watchdog.Service.Tests` (refs Service, Client, Watchdog.Testing, AdMng.Testing; Mvc.Testing,
   TimeProvider.Testing), `tests/AdMng.Watchdog.IntegrationTests` (refs Service, Core, Directory,
   Watchdog.Testing, AdMng.Testing; Mvc.Testing), `tests/AdMng.Watchdog.Testing` (library, `IsTestProject=false`;
   refs Core, Client, Contracts, AdMng.Testing; `FrameworkReference Microsoft.AspNetCore.App`; SmtpServer,
   xunit.v3.extensibility.core, xunit.v3.assert, Microsoft.Extensions.TimeProvider.Testing – nothing the framework
   supplies). `AdMng.Presentation` → Contracts + Client; `AdMng.App` → Client; `AdMng.Presentation.Tests` and
   `AdMng.App.UiTests` → Watchdog.Testing.
3. AdMng.Core changes of 4.1 (+ `ExtendedDistinguishedNameTests` and `SecretAttributesTests.CoversDrsrSecretSet` in
   AdMng.Core.Tests).
4. All Contracts files of 3.1 with the exact code of section 5 (incl. `EventKindCatalog` filled from section 7,
   `EntraCloudInfo`, `EventQuery.ToQueryString`, `OutboxQuery.ToQueryString`).
5. Core `Abstractions/*`, `Model/*` and `Diagnostics/SafeExceptionLog.cs` of section 6 (exact code); `[S*]` stubs:
   each job class implements `IWatchdogJob` with `throw new NotImplementedException()`; `AddWatchdogCore()` registers
   nothing yet.
6. Client: `IWatchdogClient`, `WatchdogClientOptions`, `WatchdogClientException`; `WatchdogHttpClient` stub with the
   constructor of 5.5 (methods throw `NotImplementedException`, `IsConfigured` false).
7. Service: `Program.cs` + `WatchdogHost.Build` minimal (maps `/healthz`, `--smoke-test` returns 0), `appsettings.json`.
8. Compilable test-library shells with final names and signatures (so parallel agents code against real types):
   `tests/AdMng.Watchdog.Testing/Stores/WatchdogStoreContractTests.cs` (abstract, `protected abstract
   Task<TStore> CreateStoreAsync()`, 3–5 starter facts), `Stores/InMemoryWatchdogStore.cs` (namespace
   `AdMng.Watchdog.Testing.Stores`, implements every store interface of 6.2, `ctor(TimeProvider)`, members throw
   `NotImplementedException`), `Http/HttpSink.cs` (public API: `StartAsync`, `Uri BaseAddress`, `Requests`,
   `Script(Func<HttpSinkRequest, HttpSinkResponse>)`, `DisposeAsync`).
9. One sanity test per new test project (e.g. Contracts JSON round-trip; `/healthz` via WebApplicationFactory;
   integration project's test skips without env).
10. `CLAUDE.md` amendment of rules 1 and 2 (4.4); `docs/WATCHDOG.md`, `docs/adr/0008-…` and the ADR-0003 status line
    are already written.
11. `dotnet build AdMng.slnx` (0 warnings) and `dotnet test` of all non-UI projects green (one project per
    `dotnet test` call) → commit "Watchdog scaffold: projects, contracts, abstractions".

### 25.2 Agents (parallel git worktrees from the scaffold commit)

| Agent | Delivers | Definition of done |
|---|---|---|
| **W-core** | Everything in `src/AdMng.Watchdog.Core` (sections 9–15 logic, formatters, resx pl+en, `AddWatchdogCore`), FakeDirectorySession watchdog emulation incl. FakeSchema additions and the metadata hook (4.3), `InMemoryWatchdogStore` + `WatchdogStoreContractTests` (replacing the shells), `FakeSecurityEventSource`, security-event and metadata fixtures, all of 22.1 | Core.Tests + AdMng.Core.Tests green on Linux, 0 warnings; **`AdMng.Presentation.Tests` green and `AdMng.App.UiTests` builds** (the shared fake must not change existing behaviour); every kind of section 7 detectable through FakeDirectorySession or fixtures |
| **W-service** | `src/AdMng.Watchdog.Service` (all files of 3.3), `WatchdogHttpClient` + handler factory, `AdMng.Directory` extended-DN support (4.2) + its integration test, SMTP/HTTP sinks (replacing the `HttpSink` shell) and test certificates in Watchdog.Testing, launch profile `Lab`, install scripts, all of 22.2 | Service.Tests green on Linux (Windows-only tests skipped), `--smoke-test` exits 0 on Linux, `ExtendedDnTests` green against the lab |
| **W-gui** | Presentation (3.5), App (3.6), localization pl+en, `FakeWatchdogClient` + sample data in Watchdog.Testing/Client, 22.3, UI tests + screenshots (20.5) | Presentation.Tests green on Linux; App and UiTests compile on Linux (`EnableWindowsTargeting`); no new binding errors by static review |
| **W-testenv** | `tools/testlab/watchdog/*`, `tests/AdMng.Watchdog.IntegrationTests/*` (22.4), CI (24), `tools/testlab/README.md` section, `docs/ARCHITECTURE.md` + `README.md` watchdog sections, later edits of `docs/WATCHDOG.md` | scripts run against the local lab (Mailpit, sink, scenario with cleanup); integration tests compile against the scaffold shells (engines only via `AddWatchdogCore()` and interfaces); they pass after W-core + W-service are merged (final run in the integration pass) |

### 25.3 File ownership (strict – an agent edits only its files)

| Path | Owner |
|---|---|
| `AdMng.slnx`, `Directory.Packages.props`, `Directory.Build.props`, `global.json`, `nuget.config`, `CLAUDE.md` | scaffold only (later: lead) |
| `src/AdMng.Core/**` | scaffold only (frozen) |
| `src/AdMng.Directory/**`, `tests/AdMng.Directory.IntegrationTests/Integration/ExtendedDnTests.cs` | W-service |
| `src/AdMng.Watchdog.Contracts/Events/**`, `Configuration/**` | frozen; W-core may make **additive** changes (new optional members with defaults) and must list them in its report |
| `src/AdMng.Watchdog.Contracts/Api/**`, `WatchdogRoutes.cs`, `WatchdogJson.cs` | frozen; W-service may make additive changes (listed in its report) |
| `src/AdMng.Watchdog.Core/Abstractions/**`, `Model/**`, `Diagnostics/SafeExceptionLog.cs` | frozen for everyone; a needed change is reported, not made (W-core may add **new** interfaces/records in new files) |
| `src/AdMng.Watchdog.Core/**` (rest), `Resources/NotificationStrings*.resx` | W-core |
| `tests/AdMng.Testing/**` (incl. edits of `FakeDirectorySession.cs`, `FakeSchema.cs`, `FakeDirectoryBuilder.cs`), `tests/AdMng.Core.Tests/Testing/FakeDirectoryWatchdogTests.cs` | W-core |
| `tests/AdMng.Watchdog.Core.Tests/**` | W-core |
| `tests/AdMng.Watchdog.Testing/Stores/**`, `Fixtures/**`, `Attribution/**` (FakeSecurityEventSource), `Scenarios/**` | W-core |
| `tests/AdMng.Watchdog.Testing/Smtp/**`, `Http/**`, `Certificates/**`, `Net/**` | W-service |
| `tests/AdMng.Watchdog.Testing/Client/**` | W-gui |
| `src/AdMng.Watchdog.Service/**`, `src/AdMng.Watchdog.Client/**` (except the three scaffold contract files) | W-service |
| `tests/AdMng.Watchdog.Service.Tests/**` | W-service |
| `src/AdMng.Presentation/**`, `src/AdMng.App/**`, `tests/AdMng.Presentation.Tests/**`, `tests/AdMng.App.UiTests/**` | W-gui |
| `tools/**`, `.github/**`, `tests/AdMng.Watchdog.IntegrationTests/**`, `docs/**`, `README.md` | W-testenv |

Rules: no agent edits csproj files it does not own (test projects' csproj belong to the test project's owner; source
csproj files are scaffold-owned – a missing reference is reported). New packages are not added by agents (report
instead). Each agent's final report lists: files changed outside its ownership (should be none), contract additions,
open TODOs, test results.

### 25.4 Merge order and integration pass

1. W-core → 2. W-service (rebase on W-core if both touched Contracts additively) → 3. W-testenv → 4. W-gui.
Integration pass (lead or a fresh agent): full `dotnet build AdMng.slnx`, all Linux tests, Samba integration tests
with Mailpit, `run-watchdog-lab.sh` + `lab-scenario.sh` end to end (e-mail visible in Mailpit, webhook verified by the
sink), Windows CI (UI tests, smoke tests, screenshots, HTTP.sys NTLM rejection, data-directory ACL enforcement),
`LogHygieneTests` green, adversarial review against CLAUDE.md rules (no directory data outside the data folder, no
secrets in logs/ToString, filters/DNs escaped, UI never blocks, 0 warnings).

### 25.5 Lead amendment (2026-10-14): W-core is split into three agents

W-core as one agent is too large. It is replaced by three parallel agents with the ownership below; everything
else in 25.1–25.4 stays. "W-core" anywhere in this spec means the union of the three.

| Agent | Owns (create/modify) | Delivers |
|---|---|---|
| **W-detect** | `src/AdMng.Watchdog.Core/Detection/**`, `Scans/**`, `Registration/DetectionRegistration.cs`; `tests/AdMng.Testing/**` (FakeDirectorySession/FakeSchema/FakeDirectoryBuilder watchdog emulation, 4.3), `tests/AdMng.Core.Tests/Testing/FakeDirectoryWatchdogTests.cs`; `tests/AdMng.Watchdog.Testing/Scenarios/**` and metadata fixtures under `Fixtures/Metadata/**`; `tests/AdMng.Watchdog.Core.Tests/Detection/**`, `Scans/**` | sections 9, 10 (incl. 10.13–10.17 scanners), `ScheduledScanJob`, `ChangeDetectionEngine` |
| **W-rules** | `src/AdMng.Watchdog.Core/Attribution/**`, `Rules/**`, `Configuration/**`, `Registration/RulesRegistration.cs`; `tests/AdMng.Watchdog.Testing/Attribution/**` (FakeSecurityEventSource) and `Fixtures/SecurityEvents/**`; `tests/AdMng.Watchdog.Core.Tests/Attribution/**`, `Rules/**`, `Configuration/**` | sections 8, 11, 12, 18 (Core parts: SecretBindings, ConfigChangeAuditor, ConfigRedactor, TimeZoneResolver) |
| **W-notify** | `src/AdMng.Watchdog.Core/Notifications/**`, `Formatting/**`, `Retention/**`, `Resources/**`, `Registration/NotificationRegistration.cs`; `tests/AdMng.Watchdog.Testing/Stores/**` (InMemoryWatchdogStore + WatchdogStoreContractTests); `tests/AdMng.Watchdog.Core.Tests/Notifications/**`, `Formatting/**`, `Retention/**`, `Stores/**` | sections 13, 14, 15.1–15.2, 16.3 (job side), the in-memory store |

Scaffold additions for the split: `WatchdogCoreServiceCollectionExtensions.AddWatchdogCore()` calls
`DetectionRegistration.Add(services)`, `RulesRegistration.Add(services)` and `NotificationRegistration.Add(services)`
(three files under `src/AdMng.Watchdog.Core/Registration/`, each an empty `internal static` method in the scaffold),
so no agent edits another agent's registrations. Shared services used across the three (e.g. `IWatchdogSignals`,
`IHealthRegistry`, `IWatchdogConfigProvider`) are consumed only through the frozen abstractions; the implementing
agent registers them. A class one agent needs from another's folder is never re-implemented: the agent codes against
the abstraction (or a scaffold stub) and lists the dependency in its report. Tests that need another area's behaviour
use fakes in their own test folder.

### 25.6 Shared Core classes implemented before the split

The six Core classes below are used across agent boundaries (W-service uses all of them; W-rules owns
`Configuration/`, W-notify owns `Formatting/`), so they were implemented completely – final public API, XML docs and
unit tests (`tests/AdMng.Watchdog.Core.Tests/Configuration/**`, `…/Formatting/Webhooks/**`) – in one sequential step
after the scaffold and before the parallel agents start. **Ownership after the split stays as in 25.3/25.5**
(`Configuration/**` → W-rules, `Formatting/**` → W-notify), but the owners **must keep the public API below stable**
(additive changes only: new optional parameters with defaults, new members, new overloads); W-service and the other
agents code against exactly these signatures. `RulesRegistration.Add` registers `ConfigRedactor` and
`ConfigChangeAuditor` as singletons (`TryAdd`); the other classes are static.

Namespaces: `AdMng.Watchdog.Core.Configuration` and `AdMng.Watchdog.Core.Formatting.Webhooks`.

`Configuration/SecretBindings.cs`, `Configuration/ConfiguredSecret.cs` – destination bindings of section 18
(lower-case, `|`-separated, `%`/`|`/control characters percent-escaped, GUIDs in "D" form, hosts – the SMTP host as
typed, URL hosts and explicit `urlHost` arguments alike – in IDNA form without a trailing dot, a non-ASCII host that is not
valid IDNA kept as written so it never equals a real host's binding; never a secret value – URLs contribute their host
only):

```csharp
public static class SecretBindings
{
    public static string ForSmtpPassword(SmtpSettings smtp);                         // smtp|<host>|<port>|<userName>|basic
    public static string ForSmtpOAuthClientSecret(SmtpSettings smtp);                // smtp-oauth|<cloud>|<tenantId>|<clientId>|<host>
    public static string ForChannelUrl(ChannelSettings channel);                     // channel|<id>|<type>
    public static string ForTeamsClientSecret(ChannelSettings channel, string? urlHost);    // teams-oauth|<cloud>|<tenantId>|<clientId>|<scope>|<url host>
    public static string ForWebhookSigningSecret(ChannelSettings channel, string? urlHost); // webhook|<id>|<url host> (also previousSigningSecret)
    public static string? UrlHost(string? url);                                      // host of a URL or URL hint; null when not absolute
    public static string? DeclaredUrlHost(ChannelSettings channel);                  // Url.NewValue host, else Url.Hint host (validator view)
    public static IReadOnlyList<ConfiguredSecret> Enumerate(WatchdogConfiguration configuration, Func<ChannelSettings, string?>? urlHost = null);
    public static bool RequiresRebind(ConfiguredSecret secret, IReadOnlyDictionary<string, string> storedBindings); // 8.3 secretRebindRequired
}

public sealed record ConfiguredSecret
{
    public required string Name { get; init; }      // SecretNames value (ISecretStore key)
    public required string Path { get; init; }      // "smtp.password", "smtp.oAuth.clientSecret", "channels[2].url", "channels[2].teams.clientSecret", "channels[2].webhook.signingSecret", "channels[2].webhook.previousSigningSecret"
    public required SecretField Field { get; init; }
    public required string Binding { get; init; }   // expected binding under these settings
    public string? ChannelId { get; init; }
    public bool InUse { get; init; }                // Basic / OAuth2ClientCredentials / every channel URL / Teams EntraClientCredentials / webhook signing secrets; Enabled flags ignored
}
```

A null `urlHost` yields an empty host component that never matches a stored binding (fail closed). The validator uses
the default resolver (`DeclaredUrlHost`); `ConfigSecretsApplier`, `DestinationGuard` and the test-send check pass a
resolver that returns the host of the new value or of the **decrypted stored URL**. `RequiresRebind` = in use, a secret
of that name is stored, no `NewValue`/`Clear`, and the stored binding differs.

`Configuration/TimeZoneResolver.cs` – IANA or Windows id → `TimeZoneInfo` (as given → runtime IANA↔Windows conversion →
built-in map Europe/Warsaw ↔ Central European Standard Time, Europe/Berlin ↔ W. Europe Standard Time, Europe/London ↔ GMT
Standard Time, UTC/Etc/UTC ↔ UTC → `TimeZoneInfo.Utc` for "UTC"/"Etc/UTC"; trimmed; control characters never resolve;
never throws from `TryResolve`):

```csharp
public static class TimeZoneResolver
{
    public static bool TryResolve(string? id, [NotNullWhen(true)] out TimeZoneInfo? timeZone);
    public static TimeZoneInfo Resolve(string id);   // ArgumentException (empty) / TimeZoneNotFoundException
}
```

`Configuration/ConfigRedactor.cs` – Viewer view of `GET /api/v1/config` (17.2): `IsRedacted = true`; recipient
addresses, SMTP host/user/from/replyTo/localDomain, OAuth tenant/client ids and certificate thumbprints (SMTP and
Teams) removed; every `SecretField` reduced to `{ IsSet }` (all hints removed); each exclusion replaced by a placeholder
(`Id = "redacted-<n>"`, empty reason/creator, `CreatedAt = default`, nothing else) so `rule.Exclusions.Count` is the
count; each `ActorFilter.ExcludeActors` SID replaced by the placeholder `redacted-<n>` (only the count remains – actor
exclusions reveal monitoring blind spots like exclusions); webhook `AllowedHosts` removed (like recipients they reveal
where alerts go). Everything else (incl. `PublicBaseUrl`, `ExcludeSelfChanges`) is returned unchanged; the input is not
modified. (Lead decision 2026-10-14: actor filters and allowed hosts redacted as well.)

```csharp
public sealed class ConfigRedactor
{
    public const string RedactedExclusionIdPrefix = "redacted-";
    public ConfigEnvelope Redact(ConfigEnvelope envelope);
    public WatchdogConfiguration Redact(WatchdogConfiguration configuration);
}
```

`Configuration/ConfigChangeAuditor.cs`, `ConfigChangeAudit.cs`, `ConfigChangeItem.cs` – old vs new configuration →
`WatchdogConfigurationChanged` data (17.2 step 5). JSON-tree diff of every serialized property (additive contract
changes are covered automatically); rules, channels, recipient groups matched by id (`rules[tier0-security].enabled`);
exclusions only as counts per rule (`rules[lockouts].exclusions +1 -0 ~1`); lists as case-insensitive counts
(`recipientGroups[it-admins].addresses +0 -1`); secrets as `set` / `replaced` (NewValue) / `cleared` – pass the new
configuration **as received**, before `ConfigSecretsApplier` strips NewValue/Clear. A nested list stored as null
counts as an empty list. No values, secrets, addresses or exclusion contents ever appear. Severity = max over the
items, at least Medium: **Critical** for disabling or weakening
a `tier0-*`/`health` rule (removed, disabled, kinds/object kinds/scope/attributes/tags narrowed, group filter,
MinSeverity raised, Severity override other than Critical, actor exclusions or exclusions added/changed, immediate /
digest / all-channels off, recipient groups or channels removed, hours weakened, aggregation window or repeat
suppression set/increased, hourly cap set/lowered), the global defaults of those (`notifications.aggregationWindow` /
`highSeverityFlushDelay` increased, `notifications.maxMessagesPerRulePerHour` lowered), `locked` cleared on any rule,
additions to `directory.ignoredAttributes` / `disabledKinds` / `excludedSubtrees`, removals from
`directory.criticalObjects` and the privileged-group lists (except `watchListDns`), a Tier-0 detection source switched
off (configuration/schema partition watching, `directory.trackDeletedObjects` – deleted trusts/PSOs and MassDeletion –,
`attribution.enabled` or `attribution.source` = None – the AuditLog kinds of 11.6 –, `scans.passwordSpray.enabled`) or
a burst detector made less sensitive (`scans.massLockout` / `massDeletion` / `massComputerDeletion` threshold or
percentage raised or window shortened, `scans.passwordSpray.distinctAccounts` raised or window shortened),
`smtp.enabled` off, a channel or recipient group used by a `tier0-*`/`health` rule (incl. `AllEnabledChannels`)
removed/disabled or emptied of addresses, a whole section removed; **High** for any other change of recipients (groups,
rule and digest references, `notifyManager`, a whole rule added/removed that sends somewhere or carries exclusions),
channels, SMTP, attribution, exclusions, `digest.enabled`; **Medium** otherwise.

```csharp
public sealed class ConfigChangeAuditor
{
    public const int MaxSummaryItems = 40;
    public ConfigChangeAuditor(TimeProvider timeProvider);
    public ConfigChangeAudit Compare(WatchdogConfiguration previous, WatchdogConfiguration current);
    /// Kind WatchdogConfigurationChanged, Source Health, Object { Kind = Watchdog, Dn = "" }, Id 0, EventId = ChangeSetId = Guid v7,
    /// attribution Resolved/High with changedBy as actor (IpAddress + SourceMethod "local" when given),
    /// Changes: one AttributeChange per item { Attribute = Path, ValueKind = Text, NewValues = [Description] }.
    public WatchEvent CreateEvent(ConfigChangeAudit audit, PrincipalRef changedBy, string? domain = null, string? sourceIpAddress = null);
}

public sealed record ConfigChangeAudit
{
    public required Severity Severity { get; init; }
    public IReadOnlyList<ConfigChangeItem> Items { get; init; } = [];   // most severe first, then document order
    public required string Summary { get; init; }                      // "path description; …; (+N more)" or "no changes" – for IConfigStore.SaveAsync(summary)
    public bool HasChanges { get; }
}

public enum ConfigChangeKind { Added, Removed, Modified, SecretSet, SecretReplaced, SecretCleared }

public sealed record ConfigChangeItem
{
    public required string Path { get; init; }
    public required ConfigChangeKind Kind { get; init; }
    public required Severity Severity { get; init; }
    public bool IsCollection { get; init; }
    public int AddedCount { get; init; }
    public int RemovedCount { get; init; }
    public int ModifiedCount { get; init; }
    public string Description { get; }   // added | removed | changed | set | replaced | cleared | "+2 -1" | "+1 -0 ~1" | reordered
}
```

`Formatting/Webhooks/TeamsUrlValidator.cs`, `TeamsUrlValidationResult.cs` – Teams channel URL check (8.3, 15.7). Standard:
`https://<env>.environment.api.powerplatform.com[:443]/powerautomate/automations/direct/[…/]workflows/…` (incl.
`/direct/cu/NN/workflows/`, any length). Order: missing → `required`; not an absolute URL / file URL → `teamsUrlFormat`
(Malformed); `*.webhook.office.com`, `*.logic.azure.com`, `outlook.office(365).com/webhook…` → `retiredTeamsUrl` +
hint `teams.retiredUrl` (also over http, any case, also with a trailing dot); non-http(s) scheme → `insecureUrl`; user info → Malformed; http →
`insecureUrl` unless loopback with `allowInsecureLoopback`; non-standard host/port/path → `teamsUrlFormat` (Warning for
SAS, Error for EntraClientCredentials). The URL never appears in a result.

```csharp
public static class TeamsUrlValidator
{
    public const string PowerPlatformHostSuffix = ".environment.api.powerplatform.com";
    public static TeamsUrlValidationResult Validate(string? url, TeamsAuthMode authMode, bool allowInsecureLoopback = false);
    public static bool IsPowerPlatformHost(string? host);   // host only (ASCII letters/digits/'-'/'.', pass Uri.IdnHost; anything else, incl. a trailing dot, is false); bearer-token gate = Validate(url, EntraClientCredentials).IsValid
}

public enum TeamsUrlProblem { None, Missing, Malformed, Insecure, Retired, NonStandard }

public sealed record TeamsUrlValidationResult
{
    public required TeamsUrlProblem Problem { get; init; }
    public string? Code { get; init; }               // null | "required" | "teamsUrlFormat" | "insecureUrl" | "retiredTeamsUrl"
    public IssueSeverity? Severity { get; init; }    // null when valid
    public string? Message { get; init; }            // English fallback (ConfigIssue.Message)
    public string? Hint { get; init; }               // DeliveryHints.RetiredTeamsUrl for retired URLs
    public bool IsPowerPlatformHost { get; init; }   // https, port 443, *.environment.api.powerplatform.com
    public bool IsValid { get; }                     // Problem == None
    public bool IsAccepted { get; }                  // Severity != Error
}
```

`Formatting/Webhooks/StandardWebhookSigner.cs` – Standard Webhooks v1 (14.5). Both test vectors of 14.5 are unit tests
(`StandardWebhookSignerTests`). Key = base64 part after `whsec_` (24–64 bytes, padding optional); the GitHub signature
uses the UTF-8 bytes of the whole secret text; message ids must be non-empty and contain no '.'; secrets never appear in
exceptions; key buffers are zeroed.

```csharp
public static class StandardWebhookSigner
{
    public const string SecretPrefix = "whsec_";
    public const int MinKeyBytes = 24, MaxKeyBytes = 64, GeneratedKeyBytes = 32;   // (declared as separate constants)
    public const string IdHeader = "webhook-id", TimestampHeader = "webhook-timestamp", SignatureHeader = "webhook-signature",
        GitHubSignatureHeader = "X-Hub-Signature-256";                              // (declared as separate constants)
    public static string GenerateSecret();                                        // "whsec_" + Base64(32 random bytes)
    public static bool IsValidSecret(string? secret);
    public static bool TryParseKey(string? secret, [NotNullWhen(true)] out byte[]? key);
    public static string Sign(SecretValue secret, string messageId, long timestamp, ReadOnlySpan<byte> body);              // "v1,<base64>"
    public static string CreateSignatureHeader(SecretValue secret, SecretValue? previousSecret, string messageId, long timestamp, ReadOnlySpan<byte> body); // "v1,<a>[ v1,<b>]"
    public static string SignGitHub(SecretValue secret, ReadOnlySpan<byte> body);                                          // "sha256=<hex>"
    public static bool Verify(string? signatureHeader, SecretValue secret, string messageId, long timestamp, ReadOnlySpan<byte> body); // any v1 entry, constant time
}
```

---

## 26. Open questions and risks

1. **Mail platform** of the customer (Exchange Online / on-prem Exchange / relay) – decides the default SMTP mode;
   Exchange Online Basic SMTP AUTH is disabled by default from end of December 2026 (OAuth2 supported in v1).
   Unverified: whether XOAUTH2 needs `Set-CASMailbox -SmtpClientAuthenticationDisabled $false` when the tenant
   default is disabled (documented as a prerequisite anyway).
2. **SPN/DNS**: the AD team must accept `HTTP/<A record>` on the gMSA and an A record (not CNAME); otherwise use
   HTTP.sys mode.
3. **Read permissions**: hardened domains (Pre-Windows 2000 Compatible Access emptied) need the GenericRead delegation;
   `CN=Deleted Objects` LCRP needs `dsacls /takeownership` – customer approval required; without it deletions are
   reported by the daily reconciliation as "zniknął".
4. **Windows-only facts not verified on a Windows DC** (Samba lab only): `msDS-ReplAttributeMetaData` readability for a
   non-admin, what 4624 shows for changes via ADWS/PowerShell (IP `-`?), whether 4740 is also logged on non-PDC DCs,
   whether `lastLogonTimestamp` produces 5136 noise with the MDI-style SACL, record ids after a log clear, default
   Security channel ACL for Event Log Readers, `lockoutTime` reset to 0 at the first logon after expiry (Samba source
   confirms it; assumed identical on Windows), Recycle Bin restore re-activating links without a group USN bump,
   4771/4776 visibility on the PDC for lockout sources and spraying, 5136/5137 coverage of the extended SACL script
   (domain head, GPC, PSO, trusts, AdminSDHolder, inetOrgPerson, dMSA), whether the token source / logon session of an HTTP.sys
   Kerberos logon reads `Kerberos` for the gMSA (fail closed otherwise). Plan: a Windows Server 2022/2025 lab run before release; record real events as fixtures.
5. **Teams**: the Adaptive Card version accepted by Workflows (1.4 assumed), rendering of ToggleVisibility/`msteams.width`,
   tenant policy on SAS triggers, OAuth trigger availability, flow owner licensing (90-day suspension → weekly digest
   heartbeat). Needs a live tenant check.
6. **Replication latency**: changes on DCs in other sites arrive after inter-site replication (default 180 min);
   alerts are timely only for the polled DC's site. Option for later: one watchdog per site or polling several DCs.
7. **Privacy (RODO)**: notifications copy employee data to mail/Teams/SIEM (Power Automate keeps run history 30 days);
   the default detail levels (e-mail Full, Teams Standard, webhook Minimal) and retention (365 days) need the DPO's
   approval; document the processing.
8. **Planned-change correlation** with AD_MNG batch operations (reduces noise of bulk edits) – phase 2; needs a design
   for authenticating the app's announcements.
9. **Runtime servicing**: the service needs ASP.NET Core Runtime ≥ 10.0.13 on the server (startup warning otherwise);
   app-local packages are patched only by new builds – the release checklist re-checks NuGet advisories (NuGetAudit).
10. **Multi-domain forests**: one service per domain in v1 (attribution sources are this domain's writable DCs; DCs of
    other domains appear only as DomainControllerAdded/Removed with tag otherDomain).
11. **Windows Server 2019 hosts** run .NET in NLS mode (no inbox ICU): IANA time-zone ids are resolved through the
    built-in map of `TimeZoneResolver`; verify on a 2019 host (or recommend Server 2022+).
12. **Tier-0 operation of the watchdog host** (docs): local administrators of the server and anyone able to run code
    as the gMSA can read DC logs, decrypt channel secrets and silence alerting – the customer must administer the host
    as Tier 0 (PAW, no Tier-1 agents running as SYSTEM). Organisational, not enforceable by the code.
13. **WEF vs direct DC reads**: direct reads (default) need Event Log Readers on all DCs (read access to every DC log);
    the hardened alternative (WEF subscription limited to the ingestion set, docs 4.4) needs a collector and loses
    on-demand 4624/4771/4776 lookups unless those IDs are forwarded. Customer decision per environment.

---

## Appendix A – Security event fixtures (Microsoft samples, `- ` tree prefixes removed, well-formed)

4720:

```xml
<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><Provider Name="Microsoft-Windows-Security-Auditing" Guid="{54849625-5478-4994-A5BA-3E3B0328C30D}" /><EventID>4720</EventID><Version>0</Version><Level>0</Level><Task>13824</Task><Opcode>0</Opcode><Keywords>0x8020000000000000</Keywords><TimeCreated SystemTime="2015-08-20T16:22:02.759912000Z" /><EventRecordID>175408</EventRecordID><Correlation /><Execution ProcessID="520" ThreadID="1508" /><Channel>Security</Channel><Computer>DC01.contoso.local</Computer><Security /></System><EventData><Data Name="TargetUserName">ksmith</Data><Data Name="TargetDomainName">CONTOSO</Data><Data Name="TargetSid">S-1-5-21-3457937927-2839227994-823803824-6609</Data><Data Name="SubjectUserSid">S-1-5-21-3457937927-2839227994-823803824-1104</Data><Data Name="SubjectUserName">dadmin</Data><Data Name="SubjectDomainName">CONTOSO</Data><Data Name="SubjectLogonId">0x30dc2</Data><Data Name="PrivilegeList">-</Data><Data Name="SamAccountName">ksmith</Data><Data Name="DisplayName">Ken Smith</Data><Data Name="UserPrincipalName">ksmith@contoso.local</Data><Data Name="HomeDirectory">-</Data><Data Name="HomePath">-</Data><Data Name="ScriptPath">-</Data><Data Name="ProfilePath">-</Data><Data Name="UserWorkstations">-</Data><Data Name="PasswordLastSet">%%1794</Data><Data Name="AccountExpires">%%1794</Data><Data Name="PrimaryGroupId">513</Data><Data Name="AllowedToDelegateTo">-</Data><Data Name="OldUacValue">0x0</Data><Data Name="NewUacValue">0x15</Data><Data Name="UserAccountControl">%%2080 %%2082 %%2084</Data><Data Name="UserParameters">-</Data><Data Name="SidHistory">-</Data><Data Name="LogonHours">%%1793</Data></EventData></Event>
```

4738:

```xml
<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><Provider Name="Microsoft-Windows-Security-Auditing" Guid="{54849625-5478-4994-A5BA-3E3B0328C30D}" /><EventID>4738</EventID><Version>0</Version><Level>0</Level><Task>13824</Task><Opcode>0</Opcode><Keywords>0x8020000000000000</Keywords><TimeCreated SystemTime="2015-08-20T16:22:02.792454100Z" /><EventRecordID>175413</EventRecordID><Correlation /><Execution ProcessID="520" ThreadID="1508" /><Channel>Security</Channel><Computer>DC01.contoso.local</Computer><Security /></System><EventData><Data Name="Dummy">-</Data><Data Name="TargetUserName">ksmith</Data><Data Name="TargetDomainName">CONTOSO</Data><Data Name="TargetSid">S-1-5-21-3457937927-2839227994-823803824-6609</Data><Data Name="SubjectUserSid">S-1-5-21-3457937927-2839227994-823803824-1104</Data><Data Name="SubjectUserName">dadmin</Data><Data Name="SubjectDomainName">CONTOSO</Data><Data Name="SubjectLogonId">0x30dc2</Data><Data Name="PrivilegeList">-</Data><Data Name="SamAccountName">-</Data><Data Name="DisplayName">-</Data><Data Name="UserPrincipalName">-</Data><Data Name="HomeDirectory">-</Data><Data Name="HomePath">-</Data><Data Name="ScriptPath">-</Data><Data Name="ProfilePath">-</Data><Data Name="UserWorkstations">-</Data><Data Name="PasswordLastSet">-</Data><Data Name="AccountExpires">-</Data><Data Name="PrimaryGroupId">-</Data><Data Name="AllowedToDelegateTo">-</Data><Data Name="OldUacValue">0x15</Data><Data Name="NewUacValue">0x211</Data><Data Name="UserAccountControl">%%2050 %%2089</Data><Data Name="UserParameters">-</Data><Data Name="SidHistory">-</Data><Data Name="LogonHours">-</Data></EventData></Event>
```

4740 (TargetDomainName = Caller Computer Name):

```xml
<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><Provider Name="Microsoft-Windows-Security-Auditing" Guid="{54849625-5478-4994-A5BA-3E3B0328C30D}" /><EventID>4740</EventID><Version>0</Version><Level>0</Level><Task>13824</Task><Opcode>0</Opcode><Keywords>0x8020000000000000</Keywords><TimeCreated SystemTime="2015-08-21T22:06:08.576887500Z" /><EventRecordID>175703</EventRecordID><Correlation /><Execution ProcessID="520" ThreadID="1112" /><Channel>Security</Channel><Computer>DC01.contoso.local</Computer><Security /></System><EventData><Data Name="TargetUserName">Auditor</Data><Data Name="TargetDomainName">WIN81</Data><Data Name="TargetSid">S-1-5-21-3457937927-2839227994-823803824-2104</Data><Data Name="SubjectUserSid">S-1-5-18</Data><Data Name="SubjectUserName">DC01$</Data><Data Name="SubjectDomainName">CONTOSO</Data><Data Name="SubjectLogonId">0x3e7</Data></EventData></Event>
```

5136:

```xml
<Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event"><System><Provider Name="Microsoft-Windows-Security-Auditing" Guid="{54849625-5478-4994-A5BA-3E3B0328C30D}" /><EventID>5136</EventID><Version>0</Version><Level>0</Level><Task>14081</Task><Opcode>0</Opcode><Keywords>0x8020000000000000</Keywords><TimeCreated SystemTime="2015-08-28T17:36:04.129472600Z" /><EventRecordID>410204</EventRecordID><Correlation /><Execution ProcessID="516" ThreadID="4020" /><Channel>Security</Channel><Computer>DC01.contoso.local</Computer><Security /></System><EventData><Data Name="OpCorrelationID">{02647639-8626-43CE-AFE6-7AA1AD657739}</Data><Data Name="AppCorrelationID">-</Data><Data Name="SubjectUserSid">S-1-5-21-3457937927-2839227994-823803824-1104</Data><Data Name="SubjectUserName">dadmin</Data><Data Name="SubjectDomainName">CONTOSO</Data><Data Name="SubjectLogonId">0x32004</Data><Data Name="DSName">contoso.local</Data><Data Name="DSType">%%14676</Data><Data Name="ObjectDN">CN=Sergey,CN=Builtin,DC=contoso,DC=local</Data><Data Name="ObjectGUID">{4FE80A66-5F93-4F73-B215-68678058E613}</Data><Data Name="ObjectClass">user</Data><Data Name="AttributeLDAPDisplayName">userAccountControl</Data><Data Name="AttributeSyntaxOID">2.5.5.9</Data><Data Name="AttributeValue">512</Data><Data Name="OperationType">%%14675</Data></EventData></Event>
```

## Appendix B – Lab facts the tests rely on (Samba 4.19, CONTOSO.TEST, verified 2026-10-14)

- RootDSE as `odczyt.test`: `highestCommittedUSN`, `dsServiceName` (`CN=NTDS Settings,CN=VM,CN=Servers,CN=Default-First-Site-Name,CN=Sites,CN=Configuration,DC=contoso,DC=test`), `dnsHostName vm.contoso.test`, `isSynchronized TRUE`, `currentTime`.
- Domain head: `lockoutThreshold 5`, `lockoutDuration -18000000000` (30 min), `maxPwdAge -36288000000000` (42 days), `fSMORoleOwner` = the lab DC.
- A failed bind changes badPwdCount/badPasswordTime but not uSNChanged; the 5th sets lockoutTime (bumps USN); the first successful bind writes lastLogonTimestamp (bumps USN).
- Adding/removing members bumps only the group; renaming/moving/deleting a member rewrites/removes the value without bumping the group.
- `msDS-ReplAttributeMetaData`/`msDS-ReplValueMetaData` are empty on Samba; binary `replPropertyMetaData` is readable by `odczyt.test`.
- `odczyt.test` cannot see tombstones (`Access check failed on CN=Deleted Objects`); Administrator can (Show Deleted).
- Samba returns all 1600 values on a plain read (no MaxValRange enforcement) – the code must still handle `member;range=…` (Windows truncates at 1500).
- `(msDS-User-Account-Control-Computed:1.2.840.113556.1.4.803:=16)` matches nothing – evaluate the bit client-side.
- The lab has tombstones from earlier experiments (≈ 1 600, 180-day lifetime): tests must not assume an empty `CN=Deleted Objects`.
- Writing an SDDL string into `nTSecurityDescriptor` is silently ignored by Samba – write binary SDs in tests.

---

## Critique resolution (revision 2, 2026-10-14)

Three reviews were applied: **A** – AD operations / helpdesk lead (27 issues), **B** – security and privacy (25),
**C** – .NET implementability with four parallel agents (25). 77 issues: 65 accepted as proposed, 12 accepted with
a modification or partially (marked ◐, reason given); none rejected outright. `docs/WATCHDOG.md`, ADR-0008 and the
ADR-0003 status line were updated in the same revision.

### A. AD operations engineer / helpdesk lead

| # | Sev. | Issue | Resolution | Where |
|---|---|---|---|---|
| A1 | blocker | Tombstone query turns every deleted object (BitLocker children, DNS, devices, FSPs) into ObjectDeleted; MassDeletion pages for routine computer clean-ups | ◐ Accepted: tombstone filter restricted to the watched classes; unknown tombstones reported only when `lastKnownParent` resolves into scope; MassDeletion split by class (users/groups/OUs/MSAs ≥ 10 → Critical; computers/contacts ≥ 50 → High); clean-up actor offered as exclusion suggestion; tests for msFVE children / dnsNode / msDS-Device. Not done: "children as a count on the parent's event" (needs a second tombstone query per deletion; the class filter already removes the noise) | 10.9, 5.4, 7, 12.3, 22.1 |
| A2 | major | `lockoutTime` t→0 is also the DC's reset at the first logon after expiry – not an unlock | Accepted: AccountUnlocked only when the write precedes t + effective duration (or a 4767 matches); otherwise no event; excluded from coverage; fake emulates the reset; docs §9 corrected | 10.6, 4.3, 11.7, docs 9 |
| A3 | major | Lockout source relies on 4740 caller only | Accepted: `LockoutSourceResolver` (4771 0x18, 4776 0xC000006A, 4625 on PDC + 4740 DC, bounded), `LockoutInfo.Sources`, actor NotApplicable, row "Źródło błędnych haseł", failure-audit rows in docs 4.5, per-account daily roll-up and 1 h repeat suppression | 5.2, 11.4 #6b, 12.4, 13.2, 13.4, docs 4.5 |
| A4 | major | "Password spraying" = 5 lockouts is wrong and noisy | Accepted: kind 603 renamed **MassLockout** (Medium, threshold max(5, 0.5 % of enabled users)); new optional **PasswordSprayingSuspected** (605) detector on 4771/4776 per source IP (≥ 20 accounts / 10 min), off by default; webhook types split | 5.2, 5.4, 7, 10.15 |
| A5 | major | Privileged-tag defaults page for routine activity (adminCount orphans, gMSA rollover, own password change, krbtgt precedence) | Accepted: `privileged` only from PrivilegeState, new `adminCountOrphan` tag (digest only); gMSA rollover not recorded unless `TrackManagedPasswordRollover`; AttributesChanged bump only for security-relevant attributes; PasswordChanged severity attribution-dependent (reset/unknown High, own change Info); explicit precedence base → bumps → attribution → overrides | 10.6, 10.6.2, 12.2, 5.4 |
| A6 | major | KeyCredentialChanged pages for hybrid-join self-registration and sync writes | ◐ Accepted: pattern-dependent severity matrix (10.6.4), sync accounts detected and offered as suggested exclusions, `security-review` digest rule for Low/Medium. The "validator warning when actor conditions meet SACL_MISSING" became a **health check** instead (the validator is pure and has no health state) | 10.6.4, 12.4, 12.3, docs 4.6 |
| A7 | major | dMSA links (both sides), altSecurityIdentities, msDS-GroupMSAMembership, dMSA creation under-classified | Accepted: superseded attributes on users/computers; new kinds ManagedAccountLinkChanged (408), CertificateMappingChanged (409), ManagedPasswordReadersChanged (410); dMSA creation High; all in tier0-security | 5.2, 7, 9.2, 10.6, 12.4 |
| A8 | major | Recycle Bin restore brings memberships back without group bumps | Accepted: `RestoredMembershipReader` reads memberOf on ObjectRestored, re-inserts keys, runs the evaluator in that cycle (PrivilegeGranted tag `restored`, inherits the 5138 attribution); fake emulates it; Windows confirmation in 26.4 | 10.8, 10.9, 4.3, 26 |
| A9 | major | Forest-wide DC discovery polls other domains' DCs and RODCs | Accepted: attribution sources = writable DCs with `msDS-HasDomainNCs` = this NC; RODCs excluded; other-domain DCs only for Added/Removed with tag `otherDomain` (High/Medium) | 10.11, 11.1, 7 |
| A10 | major | Unbounded backwards 4624 search loads busy DCs | Accepted: `LogonLookback` 12 h (capped by the oldest record), `SourceQueryTimeout` 10 s, one query per DC, negative cache 24 h, `SOURCE_LOGON_NOT_FOUND`, lookup metrics per DC in Status | 5.3, 5.4, 11.4 #6, 11.7 |
| A11 | major | `defaultContainer` = literal CN=Computers marks every SMB join as unusual | Accepted: containers resolved from `wellKnownObjects`; `defaultContainer` is a digest flag only; new `unexpectedContainer` tag drives the bump; one-time hint when CN=Computers is not redirected | 10.2, 10.6.2, 12.2, 12.4 |
| A12 | major | No path from an object to its history; stale DN in "Pokaż w AD_MNG"; no unlock action | Accepted: "Historia zmian (Watchdog)" command (filter by objectGUID); OpenInAdMng resolves the current DN by objectGUID search, deleted → Recycle Bin window preselected; "Odblokuj konto" through IChangePlanner/IBatchExecutor | 20.1, 20.2, 3.5 |
| A13 | major | Nobody notices the watchdog is down (/healthz loopback-only, wrong docs command) | Accepted: /healthz for loopback, own addresses and `MonitoringSubnets`; `--health` CLI; stable Application-log event ids; health rule to all enabled channels (`AllEnabledChannels`); docs command fixed | 17.2, 19.1, 19.8, 12.4, 5.4, docs 10 |
| A14 | minor | Missing triage filters, bulk ack, presets, hidden-tab indicator | Accepted: EventQuery Subtree/Tags/AttributionStatuses/Categories, text incl. workstation/IP, `POST /events/ack`, presets in AppSettings (filters only), `/status` every 60 s while hidden + tab badge | 5.1, 5.3, 16.2, 20.1, 20.2 |
| A15 | minor | Watermark per domain forces a resync on failback | Accepted: `DcWatermark` per (dsServiceName, invocationId); new mode **Failback** (incremental, version-aware) | 6.1, 10.1, 10.3, 16.2 |
| A16 | minor | longDisabled blind for 30 days after deployment | Accepted: additionally a base read of `lastLogonTimestamp` at enable time (absent or > 44 days) | 10.6.2, 9.3 |
| A17 | minor | AdminCountSet duplicates PrivilegeGranted at a random time | Accepted: Info when the principal is in PrivilegeState or the actor is SYSTEM (attribution-dependent) | 7, 10.6, 12.2 |
| A18 | minor | One PasswordChanged kind for reset / own change / age-reset trick | Accepted: tags `passwordReset` / `selfChange` from attribution; new kind **PasswordAgeReset** (211) from metadata; digest counts separated | 5.2, 7, 10.6, 11.2, 13.4 |
| A19 | minor | Expiry "1 day" fires on the last day; weekend expiries missed; only it-admins get them | ◐ Accepted: calendar days to the displayed last valid day, business-day shift (`FireOnBusinessDays`), `expiry` rule to helpdesk in business hours, optional `NotifyManager`. Not done: a separate weekly expiry summary (the digest section + threshold mails cover it) | 5.2, 5.4, 10.14, 12.4, 13.2 |
| A20 | minor | LockedUntil ignores PSOs | Accepted: `LockoutPolicyResolver` (msDS-ResultantPSO → msDS-LockoutDuration, fallback domain), self-check `permission.passwordSettings`, docs grant for the PSO container | 10.6, 10.12, docs 4.3 |
| A21 | minor | Multi-step account creation produces several events | Accepted: creation settle window (default 2 min, `CreationPendingUntil`), Created event with the final state, risky final UAC bits evaluated against an empty baseline | 10.6.3, 5.4, 6.1, 16.2 |
| A22 | minor | Raw SD hash alerts on semantically identical rewrites | Accepted: sorted owner/ACE summary set is the comparison value, hash only a pre-check | 9.4, 10.6 |
| A23 | minor | Missing DC removal, FSMO, PKI, forest settings; schema bursts; PAM TTL | ◐ Accepted: DomainControllerRemoved (507), FsmoRoleChanged (508), CertificateTemplateChanged (509), Recycle Bin / dSHeuristics / tombstoneLifetime as DomainPolicyChanged, one SchemaChanged per cycle. **Rejected:** PAM TTL expiry timers – need the LINK_TTL control in AdMng.Directory, cannot be tested on Samba, PAM is rare in the target segment and a late *revocation* is low-risk; nightly reconciliation reports it (phase 2) | 5.2, 7, 9.1, 9.2, 10.11, 1.2 |
| A24 | minor | Docs: 4794 subcategory wrong; dsacls/SACL miss inetOrgPerson, dMSA, domain head, GPC, PSO, trusts, AdminSDHolder | Accepted (class GUIDs re-verified on the lab schema; dMSA added only when present in the schema) | docs 4.3, 4.5, 4.6 |
| A25 | minor | WEF supported but undocumented; 4624 not reachable in WEF mode | Accepted: docs 4.4 variant B (subscription incl. optional 4624/4771/4776); lookups run against ForwardedEvents; `SOURCE_NOT_COLLECTED` | docs 4.4, 11.4, 11.7 |
| A26 | minor | Monday digest does not cover the weekend | ◐ Accepted and generalized: `CoverNonBusinessDays` – the first business day after weekends/holidays covers the whole period in **all** sections (not only "Wymaga uwagi"); weekend digests remain as heartbeat | 5.4, 13.4, docs 5.5 |
| A27 | minor | No preview data and maximum noise in the first week | ◐ Accepted: rollout mode (7 days after baseline; only tier0-*, bursts, health immediate; "wysłałaby N" counters; banner). Modification: it **ends automatically** after `RolloutDays` (admin can end it earlier) – a forgotten mode must never silence alerts indefinitely | 12.5, 5.3, 5.4, 13.2, 20.2 |

### B. Security and privacy

| # | Sev. | Issue | Resolution | Where |
|---|---|---|---|---|
| B1 | blocker | Stored secrets not bound to destination – an API Admin can redirect the SMTP password / OAuth token / Teams bearer to his host | Accepted: `SecretBindings` stored with every secret; `secretRebindRequired` on PUT and test sends; `DestinationGuard` before any credential use; fixed Entra authority/scope per `EntraCloud`; XOAUTH2 only to bootstrap `OAuthSmtpHosts`; Teams bearer only to Power Platform hosts; tests | 5.3, 5.4, 6.1, 6.2, 8.3, 15.6, 15.7, 18 |
| B2 | major | HTTP.sys mode accepts NTLM | Accepted: HTTP.sys scheme `Kerberos` when `RequireKerberos` (Negotiate only with EnableCBTHardening); server-agnostic `KerberosOnlyMiddleware`; Windows CI test with an NTLM-only client | 17.3, 19.2, 22.2 |
| B3 | major | Data folder ACL only warned about | Accepted: protected DACL on creation, allow-list check for any access mask on every start, exit code 5 unless local escape hatch | 8.4, 19.5, 22.2, ADR-0008 cond. 5 |
| B4 | major | "No directory values in logs" has no mechanism or test | Accepted: `SafeExceptionLog`, `ApiExceptionHandler`, `BackgroundServiceExceptionBehavior = Ignore`, `RedactingLoggerProvider`, `LogHygieneTests` with fault injection | 0.1, 3.2, 3.3, 17.1, 19.1, 22.2 |
| B5 | major | Watchdog Admin can silently disable detection | ◐ Accepted: `WatchdogConfigurationChanged` (908) planned with the previous configuration too; `tier0Suppression` validation unless bootstrap `AllowTier0Suppression`; actor exclusions SIDs only; watchdog role/host groups are High privileged roots from the bootstrap SIDs. Modified: routine rule edits are **Medium** (High/Critical for recipient, channel, SMTP, attribution, exclusion and Tier-0 changes) so that every save does not page at night; the role groups come from bootstrap SIDs instead of the installer editing `AdditionalHighDns` (runtime config stays admin-owned) | 5.2, 7, 8.3, 8.4, 10.8, 12.1, 12.6, 17.2 |
| B6 | major | Event-log ingestion stores too much and is unbounded | Accepted: parse-time reduction (values only for watched non-secret attributes, none without schema, excluded/unwatched records dropped), `Retention.MaxDatabaseMb` with oldest-first purge, `MaxRecordsPerPoll` budget → HealthEventLogGap "flooding" | 11.1, 5.4, 16.3 |
| B7 | major | Packages pinned to 10.0.12 despite CVEs; no supply-chain controls | ◐ Accepted: all Microsoft packages → 10.0.13 in the scaffold, runtime ≥ 10.0.13 required + startup warning, `nuget.config` with source mapping, `NuGetAuditMode=all`. Deferred: NuGet lock files (repo-wide workflow change with merge friction for four parallel worktrees; audit + source mapping cover the main risk) | 21, 19.1, 19.7, 24, 1.2 |
| B8 | major | `AdditionalWatchedAttributes` unvalidated (`*`, confidential, …) | Accepted: `invalidAttribute` (syntax, ≤ 20, not secret/noise) + live-schema check on PUT, rejected without a session; reader re-filters | 8.3, 17.2, ADR-0008 cond. 2 |
| B9 | major | Retention leaves data (pre-migration backups, no secure_delete, outbox payloads) | Accepted: pre-migration backup rotation, `secure_delete = ON`, payload NULL after send, byte-level test | 16.1, 16.2, 16.3, 6.2 |
| B10 | major | Threat model ignores tampering by host admins; outages unnoticed | ◐ Accepted: host and gMSA classified **Tier 0** (docs, ADR), `/healthz` for monitoring subnets, SCM 7036/7040 forwarding documented, Application-log ids, webhook heartbeat. Modified: the heartbeat is **opt-in per webhook channel** (`HeartbeatInterval`, e.g. 15 min) instead of on by default – a default 15-minute message would spam Teams and is useful only for a SIEM | 13.6, 17.2, 19.8, 26.12, docs 3, 8 |
| B11 | major | Exception scope covers Client/Contracts (WPF process); ADR-0003 not marked | Accepted: scope = `src/AdMng.Watchdog.Service` (host) only; ADR-0003 status line added | 4.4, ADR-0008, ADR-0003 |
| B12 | major | CLAUDE.md rule 2 not amended | Accepted: rule 2 exception text in 4.4 | 4.4, 18 |
| B13 | minor | Secrets written before the versioned save | Accepted: config row, secrets and audit entry in one transaction (`IConfigStore.SaveAsync(…, secretChanges)`) | 6.2, 17.2 |
| B14 | minor | Bootstrap file writable by the gMSA; Development auth too easy to enable | Accepted: `config\` folder read-only for the gMSA (start refused otherwise, exit 6); Development auth refused as a Windows service or with non-loopback listeners (exit 7); `ADMNG_WATCHDOG_CONFIG` ignored as a service | 8, 8.4, 19.1, 19.5 |
| B15 | minor | SQLite temp files, WER dumps, unencrypted DB | Accepted: `temp_store = MEMORY`, `WerAddExcludedApplication`, BitLocker/encrypted backups recommended | 16.1, 19.1, 19.6, ADR-0008 |
| B16 | minor | Bidi/zero-width/URL content in AD strings | ◐ Accepted: `UntrustedText` escapes Cf/Cc and bidi characters and defangs URL-like tokens (`scheme://`, `www.`). Not done: defanging every dotted name – it would mangle UPNs, mail addresses and DNS host names that operators need to read | 14.1, 22.1 |
| B17 | minor | Client-/DNS-supplied source names presented as fact | Accepted: IP first, names labelled with origin, `SourceConfidence` capped at Low, never used in matching/exclusions | 5.2, 11.4, 14.1, 14.3 |
| B18 | minor | Event sources not authenticated | Accepted: configured DCs must be known DCs; `EventSourceGuard` checks provider and Computer (also WEF) | 11.1, 11.3 |
| B19 | minor | Event Log Readers grants all logs; firewall unscoped | ◐ Accepted: firewall rules scoped to the watchdog IP; WEF documented as the hardened alternative. Not made the default: WEF needs a collector and GPO subscriptions and loses on-demand 4624/4771/4776 unless forwarded; direct reads remain the out-of-box default (customer decision, 26.13) | docs 4.4, 26 |
| B20 | minor | PersistKerberosCredentials = true; client offers credentials anywhere; http allowed | Accepted: `PersistKerberosCredentials = false`, no reverse proxy, remote checks on `Connection.RemoteIpAddress`, https-only client, credentials bound to the exact origin | 5.5, 17.1, 17.3 |
| B21 | minor | TLS versions not pinned | Accepted: Tls12 \| Tls13 for Kestrel and MailKit; TLS 1.1 handshake test | 15.6, 17.1, 19.3, 22.2 |
| B22 | minor | Opportunistic STARTTLS on relays, unvalidated LocalDomain, sender mailbox with password | Accepted: StartTls default also on port 25, `noTls` warning on any port, `invalidHostName`, docs: shared mailbox with sign-in blocked | 5.4, 8.3, docs 5.2 |
| B23 | minor | Viewer sees full detail, config and exclusions; no access accountability | Accepted: `ViewerDetailLevel` (Standard) for API and HTML page; redacted config for Viewers (`IsRedacted`); event-id access logging | 5.3, 8.4, 14.1, 14.6, 17.1, 17.2 |
| B24 | minor | Role revocation waits for ticket expiry | ◐ Accepted: bootstrap `DeniedSids`. Deferred: per-request `tokenGroups` re-check (adds an LDAP lookup per caller; DeniedSids covers emergencies) – phase 2 | 8.4, 17.3, 1.2 |
| B25 | minor | currentValue/priorValue/msDS-ExecuteScriptPassword missing from SecretAttributes | Accepted: added in the scaffold's AdMng.Core change (+ msSFU30Password) with a coverage test | 4.1 #5, 25.1 |

### C. Implementability (.NET, four parallel agents)

| # | Sev. | Issue | Resolution | Where |
|---|---|---|---|---|
| C1 | blocker | Negotiate 10.0.12 fails restore (NU1903); NU1605 rationale wrong | Accepted: all Microsoft packages → 10.0.13 (versions verified on NuGet), sentence removed, restore check in the scaffold | 21, 25.1 |
| C2 | blocker | `System.Diagnostics.EventLog` in a Web project → NU1510; Client lacks Logging.Abstractions | Accepted: package removed (shared framework, verified in the AspNetCore ref pack), rule for Sdk.Web / FrameworkReference projects, Client references Logging.Abstractions | 0.1, 21, 25.1, 5.5 |
| C3 | major | `dotnet test` with two projects fails (MSB1008) | Accepted: one step per project | 24, 25.1 |
| C4 | major | Fake DC seeding impossible in the constructor; config NC leaks into domain searches | Accepted: explicit `SeedDomainController` / `AddWatchdogInfrastructure()`, NC boundaries in searches, test | 4.3, 22.1 |
| C5 | major | FakeSchema lacks most watched attributes/classes; metadata needs a write-path hook | Accepted: FakeSchema list in 4.3; `Touch(entry, changedAttributes)` hook in the main file, owned by W-core | 4.3, 3.7, 25.3 |
| C6 | major | Shared-fake semantic changes break W-gui's tests | Accepted: every watchdog behaviour opt-in (`LockoutDuration` null by default, explicit seeding); W-core DoD includes Presentation.Tests green and UiTests build | 4.3, 25.2 |
| C7 | major | `IWatchdogSignals.WaitAsync` has no caller identity | Accepted: `Subscribe` → `ISignalSubscription` with per-subscription latch, TimeProvider timeouts | 6.2, 19.1, 22.1 |
| C8 | major | No hard removal in DetectionCommit → moved-back objects become ObjectRestored | Accepted: `Removals`; only tombstone-marked rows restore; moved-in = ObjectAppeared | 6.1, 10.6, 10.7, 10.9 |
| C9 | major | Follow-ups not buildable from frozen abstractions | Accepted: `GetMessagesForEventAsync`, `OutboxMessage.TargetKey` / `SentWithPendingAttribution`, AttributionEngine calls FollowUpPlanner directly | 6.1, 6.2, 11.4 #9, 13.2 |
| C10 | major | WatchdogJsonContext cannot serialize Core.Model; comparer lost on deserialization | Accepted: `WatchdogStorageJsonContext` in the Service, BlobCodec rebuilds dictionaries, contract test | 3.3, 16.1, 22.2 |
| C11 | major | KeyCredentialLink hash includes the owner DN → false alerts on rename/move | Accepted: hash the binary part only; rename test | 9.4, 22.1 |
| C12 | major | Crash after a membership chunk loses PrivilegeGranted | Accepted: evaluator on the first cycle after start and when `ComputedAt < MembershipsChangedAt` (set via `MembershipRelevant`) | 6.1, 10.1, 10.8, 22.1 |
| C13 | major | Agents code against APIs that do not exist yet | Accepted: scaffold shells for WatchdogStoreContractTests, InMemoryWatchdogStore, HttpSink; tests build engines only via AddWatchdogCore + interfaces | 3.7, 6.2, 22.4, 25.1, 25.2 |
| C14 | major | CLAUDE.md rule 2 conflicts with persisted secrets | Accepted (same as B12) | 4.4 |
| C15 | major | IANA time zone fails on NLS hosts (Server 2019) | Accepted: `TimeZoneResolver` with conversion + built-in map; validator tests for both id forms; Server 2022 recommended in docs | 5.4, 8.3, 13.5, 26.11, docs 3 |
| C16 | minor | whenChanged both "never requested" and used | Accepted: identity attribute, never diffed | 9.2, 9.3 |
| C17 | minor | Chunk commits could write a new DC identity with an old W | Accepted: `NewState` nullable, chunks carry null; crash-mid-Resync test | 6.1, 10.4, 10.5, 22.1 |
| C18 | minor | No reschedule method; attempt counting undefined | Accepted: `RescheduleAsync`, `MarkRetryAsync(countAttempt)`, `DeliveryOutcome.Deferred`, leasing never counts | 6.1, 6.2, 13.3, 15.1, 15.5 |
| C19 | minor | Validator lacks environment information | Accepted: `ConfigValidationContext` | 6.2, 8.3 |
| C20 | minor | Core cannot request a specific DC | Accepted: PDC selection inside `ServiceDirectorySessionProvider`; Core only reports IsPdc | 6.2, 10.2 |
| C21 | minor | Jitter, rate-limiter timers and Environment.Exit block deterministic tests | Accepted: `RetryPolicy(Random)`, manual replenishment on TimeProvider, `IProcessTerminator`, certificate check only for Kestrel HTTPS | 15.2, 15.5, 19.1, 3.3 |
| C22 | minor | Linux Kerberos test cannot prove SID roles | Accepted: scoped to authentication (403 + AuthenticationType Kerberos) | 17.3, 22.4 |
| C23 | minor | Permission self-check counts race with deletions | Accepted: GUID-set comparison with base reads of the difference | 10.12, 22.1 |
| C24 | minor | outbox_events.event_id has no FK | Accepted: `REFERENCES events(id) ON DELETE CASCADE` | 16.2 |
| C25 | minor | Config reload, first save version and built-in merge unspecified | Accepted: ReloadAsync then ConfigChanged; first save with expectedVersion 0; merge in memory, persisted on the next PUT | 8, 8.2, 6.2 |

### Questions for the user

None block the implementation. Two customer decisions are documented with safe defaults and can be confirmed later:
direct DC log reads (default) vs a WEF collector (26.13), and the existing mail-platform question (26.1).
