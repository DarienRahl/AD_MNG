# Stage S – stabilization and pilot readiness (design)

Chosen by the operator (2026-11) before further features: make the tests reliable, give the pilot an in-app
readiness check and a value-free problem report, and one pilot checklist for everything that waits for a pilot on
Windows Server AD (K1–K6, E, Watchdog, Korespondencja). Code signing stays for later (the operator has no certificate
yet). Rules 1–6 apply unchanged.

## 1. Test reliability

Seen in one day: four tests that failed only sometimes – the Watchdog startup exit code (host cross-talk of
WebApplicationFactory, fixed with `HostBuildLock`), the Korespondencja test send (debounced preview refresh) and the
GPO scope inheritance list (flag set before the list), both fixed by waiting for the real completion; and, reported by
agents, `ShellViewModelTests.DetailsPane_ReadsTheSelectedObjectLive_AfterTheSelectionSettles` (2 of 5 full runs),
`CopyFieldTests.CopyCommands_OfOtherWindows_AreNotConfirmed_WhenTheClipboardIsBusy`,
`BatchEditorTests.RetryFailed_RunsOnlyTheStepsThatFailedOrDidNotRun`,
`HtmlNestingTests.AStyleSheetOverTheRuleBudget_IsDropped_NotInlined`.

Common cause: view model tests run without a UI synchronization context, so the continuations of `ConfigureAwait(true)`
run on thread-pool threads concurrently with the test, which can observe a view model between two assignments that
WPF would run as one step on the UI thread. Work:

- Surface flakes: repeated full runs of `AdMng.Presentation.Tests` and `AdMng.Core.Tests` (and the Watchdog test
  projects) under CPU load; list every test that ever fails.
- Root-cause each one; fix the test (wait for the real completion: `IsLoading`, the awaited task, a refresh task)
  or the product when the race is real in WPF too. Never skip, disable or retry a test.
- Systemic: decide with evidence whether the view model harnesses (`B2Harness`, `GroupPolicyHarness`,
  `EventLogHarness`, …) should run on a single-threaded UI context like `tests/AdMng.Presentation.Tests/Watchdog/
  TestUiThread.cs`, so tests see the same ordering as WPF; and fix patterns such as `WaitUntilAsync(() => A)` followed
  by assertions on `B` that the same load sets later.
- Time-budget tests (`HtmlNestingTests`) must not depend on machine speed.

## 2. Readiness check (in the app)

Document **Gotowość środowiska** (*Narzędzia → Sprawdź gotowość środowiska…*, *Pomoc*, palette `tools.readiness`; one
tab per connection window): **Szybkie** (LDAP and this computer only) and **Pełne** (also SYSVOL, every DC's event log,
a chosen test computer, the Watchdog). Each check: OK / warning / error / not checked, an explanation, the remedy and
a link to the product doc chapter. Read-only – nothing is written anywhere (no SYSVOL write probe; write permission is
judged from the ACL).

| Area | Checks |
|---|---|
| Connection | logon type (Kerberos vs simple bind – remote features need a Windows logon), signing and sealing or LDAPS, clock skew to the DC (> 5 min fails Kerberos), the DC is the PDC emulator (GPO edits), RODC, global catalog |
| Directory | domain and forest functional level, AD Recycle Bin enabled, password settings objects readable, Windows LAPS schema present (later features) |
| SYSVOL and GPO (K2–K6) | SYSVOL reachable with the window's logon, write permission on `Policies` from its ACL, central store present (files, languages), default GPOs present, DFSR (not FRS) for SYSVOL |
| Remote actions (K1) | for a test computer the operator picks: name resolves, WinRM/CIM session opens with Kerberos |
| Event logs (E) | every DC: RPC session opens, `Security` readable (Event Log Readers / channel access), coverage (how far back), audit evidence probes for lockout and failed-logon events; the test computer's `System` log |
| Watchdog | URL configured, reachable, Kerberos authentication, role, health summary |
| Korespondencja (U5) | classic Outlook present and its profile (existing Outlook diagnostics) |
| This computer | Windows build, WebView2 runtime (previews) |

Results only in memory; export to an operator-chosen HTML/CSV file (it names hosts – the export says so).

## 3. Problem report (value-free)

*Pomoc → Zgłoś problem…* builds a text the operator copies or saves: version, OS build, culture, logon type and
transport flags (Kerberos, signing, sealing, LDAPS – no server or account names), enabled features, the last results
of the readiness check as codes, and the last entries of an **in-memory diagnostic buffer**: an `ILoggerProvider`
keeping the last N entries as level, category, event id, the message **template** (never the formatted arguments) and
exception type + HResult – so no directory value can enter it (rule 1). Nothing is written to disk unless the operator
saves the report.

## 4. Pilot checklist

`docs/PILOTAZ.md` (Polish): the pilot environment (Windows Server 2025 and 2022 DCs, Windows 11 admin station,
Debian 13 for the Watchdog), preparation (readiness check), scenarios per area with expected results and the tool to
compare with (GPMC, `gpresult /h`, `secedit`, LockoutStatus, EventCombMT, Event Viewer, classic Outlook), how to report
(problem report), and a results table. It links, not repeats, the "limitations and pilot" chapters of the product docs.

## 5. Small debt

The internal `AnalysisCodes` (event log analyses) is unified with the public `EventCodes` catalog (one classification
of NT status and Kerberos codes).

## 6. As built

Built in 0.13.0 by three parallel branches, merged in this order: problem report and codes, readiness check, test
reliability. The problem report reads the readiness check's `IReadinessResults` (code and status pairs plus one "last run"
line); the pilot checklist is `docs/PILOTAZ.md`.

**As built – problem report and codes.** *Pomoc → Zgłoś problem…* (palette `help.reportProblem`, category Tools) opens
`ProblemReportWindow` / `ProblemReportViewModel` (`AdMng.Presentation.Diagnostics`), one per connection window
(`ToolWindows.ProblemReportKey`; reopening activates it and refreshes the text). The read-only text (labels localized,
values invariant) lists: version (`AppVersion.Display`), OS description and architecture, .NET runtime and process
architecture, UI and format culture; the logon type and channel of the window's current session from `SessionInfo`
only (`Authentication`, `Security` → signing / sealing / LDAPS / StartTLS, SASL SSF where reported, global catalog –
never server, domain, account or naming context); classic Outlook installed (`IOutlookClient.Availability`, no start),
WebView2 runtime available (`IWebView2Runtime`, implemented by `WebView2Host` without creating the environment),
Watchdog configured yes/no (never the address); the readiness results as `Code: Status` from `IReadinessResults.Last`
(`AdMng.Presentation.Readiness`, with `LastRunAt`, `LastDepth` and `LastCancelled` as one "last run" line), and the diagnostic buffer.
Buttons Refresh, Copy, *Zapisz jako…* (UTF-8 `.txt` to an operator-chosen file – the only way it reaches a disk), Close.
`DiagnosticLogBuffer` is an application-wide singleton ring buffer (200 entries) fed by `DiagnosticLoggerProvider`,
registered in `AppHost.Build` next to `AddDebug`: per log call it keeps time, level, category, event id and name, the
`{OriginalFormat}` template (only that pair of the state is read; the formatter is never called, unstructured states keep
no template, scopes are ignored) and, per exception and up to three inner ones, the type name and HResult – never a
message, a stack trace or an argument value. Tests log DNs, account names and passwords as arguments, inside exception
messages and in scopes and prove that none reaches the buffer or the report. Codes (section 5): the internal
`AnalysisCodes` keeps only the event IDs and the record predicates; the classification is `EventCode.FailureKind` of the
public catalog (the category with logon hours, workstation, logon type, authentication firewall and KDC policy told
apart; trust failures and other restrictions → `Other`), the code of a record `EventCodes.ResultOf` / `EventCode.Display`,
and the address and computer normalization `EventCodes.NormalizeIpAddress`, `NormalizeWorkstation`, `IsLocalAddress` and
the new `IsUnspecifiedAddress`. Deliberate differences for records Windows does not write that way: Kerberos `0x1`
(catalog category "account expired") now counts as `AccountExpired` instead of `Other`; a 4625 without a parsable
`Status` uses its sub-status; status texts of digits only parse as decimal; a workstation name with a trailing dot
compares without it.

### As built – readiness check

*Gotowość środowiska* is a shell document (`ReadinessDocumentViewModel`, view `AdMng.App/Readiness/ReadinessView`), one
tab per connection window, opened by *Narzędzia → Sprawdź gotowość środowiska…*, the same entry in *Pomoc* and the
palette entry `tools.readiness`; a new tab of a connected window runs the quick check. `ReadinessChecker` runs the areas in
order through the existing services (`ReadinessServices`, scoped): the session's facts, RootDSE `currentTime`,
`DomainControllerCatalog` and `GroupPolicyReader.ReadPdcEmulatorAsync` (PDC, RODC, GC), `msDS-Behavior-Version` of the
domain head and `CN=Partitions`, `msDS-EnabledFeature`, the PSO container, `msLAPS-Password`/`ms-Mcs-AdmPwd` in the schema;
`GroupPolicyServices.OpenSysvolAsync` with the new read-only `ISysvolWriteConnection.GetPoliciesFolderSecurityAsync`
(default `null`; Windows reads the DACL of `Policies`) judged for the account's `tokenGroups` by
`Core.Readiness.FolderWriteAccess` (never by writing), `ListPolicyDefinitionsAsync` (ADMX count, ADML languages), both
default GPOs in AD and `GPT.INI`, `Core.Readiness.SysvolReplication` (`msDFSR-Flags` of `CN=DFSR-GlobalSettings`, the DFSR
group `Domain System Volume` and the FRS replica set – "unknown" without them, e.g. Samba); the test computer (typed or
picked) through `IHostAddressResolver` and `IRemoteComputerClient` (OS read); every DC's log through `EventLogServices`
(open, Security coverage, `AuditEvidence.Probes` over the covered part of the last week for the lockout and failed-logon
requirements, `EventLogText` errors and remedies) and the test computer's `System` log; `WatchdogConnectionService`
(`/me`, `/status`); `IOutlookClient` (availability; the full check connects and reads `DiagnoseAsync` without a test
item); `ILocalComputerInfo` (App: registry and `WebView2Host.ProbeRuntimeVersion`) against `WindowsReleaseCatalog`. Every
check has a stable code (`ReadinessCodes`, e.g. `Connection.ClockSkew`), a status (OK, Ostrzeżenie, Błąd, Nie sprawdzono),
an explanation, a remedy and the product documentation chapter; a failed check is its own error, cancel marks the unreached
checks "not checked". Results stay in memory; *Kopiuj wyniki* goes to the clipboard and the CSV/HTML exports to an
operator-chosen file – the tab, the HTML report and the confirmation say that the export names hosts. Each finished run is published to `IReadinessResults` (scoped): `Last` = one value-free
`(Code, Status)` pair per code (worst status of a per-host check), `LastRunAt`, `LastDepth`, `LastCancelled`, `Changed` –
for the problem report. Tests: `tests/AdMng.Core.Tests/Readiness`, `tests/AdMng.Presentation.Tests/Readiness`, UI test
`ReadinessUiTests` and the smoke test.

**As built – test reliability.** Flakes were surfaced with 55 full runs of `AdMng.Presentation.Tests` and 46 of
`AdMng.Core.Tests` (plain, two processes at once, with busy loops, pinned to one or two cores); the Watchdog.Core,
EventLogs and Remote tests never failed. Every test that failed, its cause and its fix:
`ShellViewModelTests.DetailsPane_ReadsTheSelectedObjectLive_AfterTheSelectionSettles` – the shell's privilege check (a
fire-and-forget base read at connect) could land after the test had counted all reads (reproduced by delaying it); the
in-memory session records the DN of each read and the test counts the selected object's.
`CopyFieldTests.CopyCommands_OfOtherWindows_AreNotConfirmed_WhenTheClipboardIsBusy`,
`BatchEditorTests.RetryFailed_RunsOnlyTheStepsThatFailedOrDidNotRun`,
`SecuritySettingsEditorTests.AFileChangedBehindTheEdits_KeepsThem_WithAConflictNote_AndDiscardShowsTheNewFile` and
`EventLogEntryPointTests.Shell_RegistersThePaletteCommands_AndOpensEachDocumentOnce` – the test read a list while the
view model's own background work replaced it ("Collection was modified"; the batch editor's group lookup replaced its
item while the preview built the operations) or interleaved with it (the reload after a refused save brought the
conflict note back after "Discard"); the tests wait for the real completion (the sheet's reload, the resolved groups,
the save's reload) and the polling helpers count a read during a change as "not yet".
`SettingsTests.AReader_NeverSeesAListBeingChanged` – the reader task could start only after the writer had finished; the
writer waits for it. `WatchdogLateMatchTests.NoFilterOnLateFields_NoExtraReads` – the clock moved before the poller's
next long-poll had set its deadline; tests move it only while the poller waits in a long-poll
(`FakeWatchdogClient.WaitingLongPolls`). `HtmlNestingTests.AStyleSheetOverTheRuleBudget_IsDropped_NotInlined` (21–26 s)
and `SecurityTemplateTests.LargeTemplate_ManyKeysAndDuplicates_IsParsedInLinearTime_LaterDuplicateStillWins` (10.5 s) –
wall-clock budgets: the style-rule budget is checked at its logical boundary (2 000 rules × 1 000 elements are inlined,
one element more is dropped), the deep-nesting case by the text estimate that runs before any parse, and the parse and
the event log analyses (also `FailedLogonAnalysisTests`, `LockoutInvestigationTests`) assert linear growth of the
thread's CPU time against an input eight times smaller (`LinearWork`: 6.5× to 14× measured, bound 32×, a quadratic probe
66×). Systemic: the Presentation tests run on a single-threaded UI synchronization context (`UiThreadAttribute`,
assembly-wide), so after a test's first await that yields, the test and the view models' `ConfigureAwait(true)`
continuations take turns on one thread as in WPF. Apart from four SYSVOL holds (below) no test changed for it; the
original versions of five flaky tests (GPO scope inheritance, security settings discard, batch editor retry, copy
commands, correspondence test send), three processes at once with busy loops, failed in 11 of 360 runs without it and in
none of 360 with it, and the suite takes the same time. It does not cover work on pool threads (`Task.Run`, the inline
test dispatcher called from there) or a test's code before its first yielding await, and a blocking wait in a test
double that view model code reaches on the UI thread blocks the test: SYSVOL holds use `FakeSysvol.HoldNext` (one
blocking hold had stalled a test for 30 s); directory doubles are reached through `Task.Run`. After the fixes every
fixed test ran 50 times (half of them with two processes at once) and the full suites ran 14 times each (six plain, four
with busy loops, four as two processes at once) plus once through `dotnet test`, all green. Not changed: the HTML
sanitizer merges adjacent identical inline elements in quadratic time (34 000 `<i>` siblings take about 2.5 s, outside
the style budget).
