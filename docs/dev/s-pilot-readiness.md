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

(Filled in when the stage is done.)
