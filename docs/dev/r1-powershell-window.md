# Track R, stage R1 – the PowerShell window: window and reads (design)

Product description: README, *Track R – PowerShell window* (Polish: *Ścieżka R – Okno PowerShell*). The decisions and their
reasons: [ADR-0010](../en/adr/0010-powershell-window-embedded-engine.md) (Polish: `docs/adr/0010-okno-powershell-osadzony-silnik.md`).
This note is the implementation design of stage **R1** (the window next to the connection window, AD Manager's read commands,
results as objects, `Out-AdmGrid`, *Open in the program*, export, the PowerShell policies from GPOs and the language mode).
**R2** (write commands through the change plan) and **R3** (RSAT modules) are not built here, but nothing in R1 may make them
harder: the window has a place for a pending plan, and no write command exists. Where this note and the section "R1 as built"
disagree, "as built" wins.

R1 is built in five parts (the workflow's units), all five done: **1** decisions on paper and the engine, **2** the read commands,
**3** the window's view model, the line editor's model and the policy model, **4** the WPF window, registration, UI tests and
the Windows decisions (single file, publish layout), **5** documentation for the operator and the closing checks (the readiness item
was optional and is not built). Section 8 says which part was planned to build which file; **section 12.0 is the whole of R1 as built, in one text**.

## 1. Decisions at a glance

The owner's decisions D1–D9 (the full text and the reasons are in ADR-0010):

| | Decision |
|---|---|
| D1 engine | Embedded PowerShell 7 (`Microsoft.PowerShell.SDK` 7.6.x LTS; built on 7.6.10, `net10.0`; 7.7 targets .NET 11) in the new project `src/AdMng.PowerShell`, used by `AdMng.App` only, never in the Watchdog package. Size (+19…54 MB) and a release with each security update accepted. If the single-file `AdMng.exe` cannot run the engine, its files are published in a folder beside `AdMng.exe` (Windows CI decides in part 4). |
| D2 runspace | One runspace per window, created and disposed with the window's DI scope; a custom `PSHost` / `PSHostUserInterface`; an allow-list of commands (4.6: no jobs, parallel runspaces, remote sessions, `Invoke-Expression`, `Start-Process` or file writers; no job outlives its command); no profiles, no PSReadLine, module auto-loading off, no script files (execution policy `Restricted` and, on Windows, the window's own authorization manager), `Import-Module` and `using module` only for a list of modules that is empty in R1. |
| D3 language mode | ConstrainedLanguage by default; a per-window switch *Trusted mode (FullLanguage)* after a confirmation, shown in the title and in a lasting note, never remembered; with a system lockdown the window stays in ConstrainedLanguage and the switch is disabled with the reason. A protection against mistakes, not a security boundary. |
| D4 names | Prefix `Adm`: `Get-AdmObject`, `Get-AdmUser`, `Get-AdmGroup`, `Get-AdmComputer`, `Get-AdmGroupMember` (`-Recursive`), `Search-AdmObject`, `Out-AdmGrid`, `Open-AdmObject`, `Export-AdmList` (+ `Get-AdmVersion`). Shortcut **Ctrl+Shift+P** (grep of `KeyBinding`, `InputBindings`, the command descriptors of `ShellViewModel.Palette.cs` and the docs found no use of it); menu *Tools → PowerShell…*; a command palette entry. |
| D5 line editor | Own WPF control, no new dependency; completion through `CommandCompletion.CompleteInput` plus parameter completers (attribute names from the session's in-memory schema, DNs from what the session knows – no directory read per keystroke); highlighting from `Parser.ParseInput` tokens; Shift+Enter inserts a line, Enter runs a complete command, Ctrl+C / Stop cancels; history in the window's memory only (Up/Down, Ctrl+R); a line with a plain-text password is not remembered; no file system completion. |
| D6 output | Ordinary PowerShell objects; the window renders them with the engine's own formatting as text with clickable DNs; errors, warnings and verbose messages in the window's colours with the status line's glyphs; `Out-AdmGrid` sends the objects to the program's object list grid; nothing is written unless the operator exports. |
| D7 policies | When the window opens it reads the PowerShell policies from the registry (PowerShell Core, and Windows PowerShell for a policy whose own `UseWindowsPowerShellPolicySetting` is on: transcription, script block logging, module logging, execution policy) and the lockdown state; a lasting note (the status line's Attention) before the first command says what is logged where; the program does not try to get around any policy; the registry reader is behind an interface. |
| D8 nothing on disk | Memory only; the engine's environment is set before its first use (section 4.2); a "no files" test; in ConstrainedLanguage no script reaches the window's session or credentials (4.3; in the trusted mode .NET reflection can); `LdapFilter` and `DistinguishedName`; one DC; async with the pipeline's token. |
| D9 writes | None. A test asserts that the registered AD Manager commands are exactly the read commands (section 4.8). |

## 2. Rules (CLAUDE.md) applied

1. **No local directory data.** Window content, results and history are memory only. Nothing of the window goes to settings
   in R1 (code snippets are R3, and only code typed by the operator). The engine's own files are prevented, not tolerated
   (section 4.2): no telemetry identifier, no module analysis cache, no transcript of the program's own making, no PSReadLine
   history, no `$PROFILE`. The cmdlets and the host never write a file; `Export-AdmList` writes only the path the operator named
   or chose in *Save As*, and `Start-Transcript` only to the file named with `-Path`; no other command writes a file (4.6).
2. **No passwords.** The window's credentials stay in the session object; the commands get a `ReadOnlyDirectoryReader` that
   cannot be cast back to the session (so neither its write methods nor the connector's credential reuse), find it in the
   engine's window registry, which no script in ConstrainedLanguage can read (4.3), and R1 creates no `PSCredential`. A prompt for a credential (`Get-Credential`) is refused in R1. A line with a plain-text password does not
   reach the history (section 4.9). `ToString()` of every type that carries a secret is value-free.
3. **Escape everything.** Every filter the commands build goes through `AdMng.Core.Ldap.LdapFilter`, every DN through
   `DistinguishedName` (`-Name`, `-Attribute`, `-Value`, `-SearchBase`, `-Scope`); a raw `-LdapFilter` parameter is passed
   through the same parser check the search box uses, not concatenated into anything.
4. **One DC per session.** The commands read only from the window's session; there is no `-Server` and no `-Credential`.
   The window's directory session is the only network path of R1's commands. (`Invoke-WebRequest` or a .NET socket in a
   script is the operator's own business, like running `pwsh.exe`; it is not a directory read.)
5. **Every write goes through the change plan.** No write exists in R1 (D9). The context object has no writer, and the
   window keeps a place for the pending plan that R2 fills (section 7).
6. **The UI never blocks.** The pipeline runs on its own thread; a command that reads the directory waits for its async read
   on that thread (never on the UI thread) and passes the pipeline's cancellation token; `StopProcessing` cancels it; results
   stream to the window as they arrive and the window shows them in batches.

## 3. Projects and layering

```
AdMng.Core            contracts the window shares with the engine: AdMng.Core.PowerShell (IPowerShellSession,
                      output items, completion, syntax spans, language mode, the grid table) – no PowerShell types
AdMng.PowerShell      the engine (net10.0, Microsoft.PowerShell.SDK): PowerShellSession, the host, the commands, guards,
                      history policy, syntax, completion, lockdown probe – references AdMng.Core only
AdMng.Presentation    PowerShellWindowViewModel and its parts (no WPF types, no reference to the engine): talks to the
                      engine only through the Core contracts and IPowerShellSessionFactory
AdMng.App             the window, the line editor control, the factory that creates the engine session for a connection
                      scope, the Windows registry reader of the policies, DI registration – the only project that
                      references AdMng.PowerShell
```

Why the contracts are in Core and not in the engine: the established pattern (`AdMng.Remote`, `AdMng.EventLogs`) is that
`AdMng.Presentation` never references an engine project, so view models are tested on Linux against fakes of the contracts.
The engine is also tested on Linux, and `AdMng.Presentation.Tests` may reference it (a test project may) to run a view model
against the real engine in a few tests. The engine project is not referenced by any Watchdog project, so the package never
reaches the Watchdog build output; a check of the Watchdog publish layout stays in the CI job that publishes it.

## 4. The engine (`src/AdMng.PowerShell`)

### 4.1 `PowerShellSession` – one per window

`PowerShellSession : IPowerShellSession` owns one runspace, one host and one context. It is created by the factory in the
connection's DI scope (`IPowerShellSessionFactory`, App; part 3 defines the contract, part 4 implements it) and disposed by the
scope. `PowerShellSession.CreateAsync(options)` builds it on a pool thread: the first runspace of a process costs about
0.4 s, the second about 10 ms (measured, section 12.1), so the window shows "Starting PowerShell…" only once.

- **Initial session state:** `InitialSessionState.CreateDefault()` (not `Create()`: it brings the formatting and type data, the
  providers and the built-in variables) reduced to the window's allow-list of commands (4.6), then
  - `LanguageMode = ConstrainedLanguage` (always the start mode, also without a lockdown);
  - `ExecutionPolicy = Restricted` **on Windows only** (`InitialSessionState.ExecutionPolicy` throws
    `PlatformNotSupportedException` on Linux and macOS – there is no execution policy there); script files are refused by
    `ScriptFileGuard` regardless (4.6), on Linux in a weaker form (the window is Windows only);
  - `ThrowOnRunspaceOpenError = true`; `ThreadOptions = ReuseThread` and, on Windows, the apartment `STA` (one dedicated
    thread per runspace, as the console host has, so COM-backed cmdlets work);
  - the variable `PSModuleAutoLoadingPreference = None` (module auto-loading off; section 4.2 shows what it prevents);
  - the window's authorization manager (`WindowAuthorizationManager`, 4.6: no script file runs on Windows);
  - the commands of AD Manager (`AdmCommands.Entries`, 4.5) and the allow-list with its replacements and refusals (4.6).
- **Set-up after `Open`:** the lookup hook that drops a script file (`ScriptFileGuard.DropScriptFiles` as the engine's
  `PostCommandLookupAction`), then `WindowSetupCommand` runs once, invoked through its `CmdletInfo` with the session briefly in
  FullLanguage: it makes the bridge of the replacement commands (4.6) and hands over the session's job repository; the session is
  constrained again before anything of the operator runs, and the window's entry (context and bridge) goes into `WindowRegistry`.
- **One run at a time.** `RunAsync` on a busy session throws `InvalidOperationException` (when the enumeration starts); the
  window's command is disabled while a run is active. Completion returns nothing and the language switch throws while a run
  is active; a completion in flight delays the start of a run by its own duration (one gate), which is bounded: a completion
  is stopped after `CompletionTimeLimit` (5 s), on `StopAsync` and when its token is cancelled, and one that its stop cannot end
  is left after the grace period like a run (a new runspace; audit PS-10).
- **Stop.** `StopAsync()` (the Stop button) and the cancellation token of `RunAsync` do the same: cancel the sink's token
  (wakes a producer waiting for room in the channel and a prompt waiting for an answer), `PowerShell.BeginStop` (the engine's
  stop – a `StopProcessing` call on every running command, which cancels the command's `StopToken`), and start the
  **grace period** (`StopGracePeriod`, 5 s). A run that ends ends with `PowerShellRunEndItem(Stopped)`. The engine's stop
  (`PowerShell.Stop`) waits until the pipeline stopped, so it runs on a background thread of the window (`BeginStop` would wait
  on a foreground thread of the engine's own).
- **A run that Stop cannot end is left.** A pipeline stuck in .NET code cannot be interrupted. When the grace period passes and
  the run has not ended, the session **leaves** it: its runspace leaves `WindowRegistry`, its host is detached (nothing it still
  writes reaches the window), its jobs are ended, and a **new runspace** is made the way the first was (a new host, the same
  initial state, set-up and registry entry, the session's language mode – constrained again when a lockdown is now enforced). The
  enumeration ends with `PowerShellRunEndItem(Abandoned)` once the session is idle, so the next command runs at once – without
  the variables, functions and modules of the old runspace; the window says so in its scrollback and on its status line (6).
  The left thread runs on until it returns – its pipeline then stops in the old runspace, which is disposed – or until the
  process ends. The runspace's pipeline thread is made a background thread by the set-up (the engine makes it a foreground
  thread, observed), so a thread that never returns does not keep the program's process alive after its last window closed
  (`RegexLimitTests` proves it in a child process). ConstrainedLanguage reaches such code too: a match of a regular expression
  (`-match`, `-replace`, `-split`, `switch -regex`, `Select-String`, `[regex]`) is .NET code that Stop does not interrupt,
  and one pattern that backtracks held the runspace and a processor core until the process ended (red team of R1, goal h). The
  time limit of 4.2 ends every match of a regular expression without a limit of its own after 2 s – shorter than the grace
  period, so Stop ends the command itself. A `[regex]` made with a limit of its own is **not** bound by it, and
  ConstrainedLanguage allows that (`[regex]::new('^(a+)+$', 'None', [timespan]::FromHours(1))`, `[regex]::InfiniteMatchTimeout`,
  the static methods with a time limit): such a match, and the trusted mode's .NET code, is what the leaving is for (second red
  team, 12.8). Its thread keeps a processor core busy until the match ends – an hour, or never – or the program closes.
- **Turning the trusted mode off makes a new runspace** (audit PS-02, 12.10): `SetLanguageModeAsync(Constrained)` on a trusted
  session does not set the language mode of the same runspace back - a function, an alias, a script block in a variable or a member
  added to `$Host` defined in FullLanguage keeps running in FullLanguage - but makes a new runspace the way the first was (in
  ConstrainedLanguage, on a pool thread, under the session's gate: no command and no completion meanwhile) and asks the lockdown
  again; the trusted runspace leaves `WindowRegistry`, its jobs are ended and it is closed on a pool thread. Every runspace has a
  host and a raw user interface of its own (the width is the session's), so nothing attached to the old ones is reachable. When the
  new runspace cannot be made, the session has none: the switch and every later command throw
  `PowerShellSessionUnavailableException` (Core; the cause is its inner exception), the same as after a left run whose new runspace
  failed. Turning the trusted mode on keeps the runspace and its variables (the engine itself drops the functions defined in
  ConstrainedLanguage when the mode becomes FullLanguage).
- **Jobs.** No job outlives its command: every job left in the session's job repository is removed and ended (on a pool thread,
  not waited for) when the command ends, on `StopAsync` and on dispose; the end that removes a job ends it, so no job is stopped
  twice. No command of the window creates a job; .NET code of the trusted mode can (`ForEach-Object -Parallel -AsJob` through the
  engine's cmdlet object). PowerShell 7.6 hands the children of such a parallel job (`PSTaskJob`) to its pool in a work item of its
  own, which opens a runspace for each child that gets a place - at any moment until it has handed out the last child, also when an
  earlier child ends and frees its place - and stopping or disposing the job closes the runspaces of its pool, also one still
  opening, which then fails on that work item and ends the process (12.12). So the session (`ParallelJobs.PrepareEndAsync`) first
  takes away the pool's free place and gives its stop signal - the two events the work item waits on - so that the children waiting
  for a place do not start, waits until the work item has closed the pool (it does so after its last child; a child it is opening
  at that moment still starts, and so does the next one if a running child of a full pool frees its place in the moment the work
  item takes to leave its loop), and only then stops and disposes the job: every open has ended before the stop. These are private
  members of the engine (`PSTaskPool._addAvailable`, `_stopAll`, `IsOpen`; an `EscapeTests` test fails when an update of the engine
  changes them, and without them the session waits until every child has started, so a throttled job runs on until its last child
  has a place). A parallel job that something else stopped first (trusted-mode code's own `StopJob`) is neither stopped again nor
  disposed. No child is stopped by itself. So a job ends with its command as soon as the engine has opened the runspace it was
  opening, and its children that waited for a place do not start. A thread or a runspace that such code makes for itself is not a
  job, and nothing in the window ends it (12.6 decision 6). A script block such code leaves as the callback of a timer or thread
  and that fires after its window closed finds no runspace and throws; the process's handler of unhandled exceptions (4.2) counts
  exactly that error as handled, so it ends the callback, not the program (12.8). The transcript of
  `Start-Transcript -Path` ends with `Stop-Transcript` or when the runspace closes (its end lines are written then).
- **`DisposeAsync`:** stops the running pipeline, waits until the session is idle (the run ended, or after the grace period the
  session left it – a disposed session makes no new runspace), ends the jobs, removes the window's registry entry, disposes the
  runspace (a left one is disposed when its thread returns), is safe to call twice, and makes later `RunAsync` calls throw
  `ObjectDisposedException`.

### 4.2 The engine's environment (D8; observed on Linux)

`PowerShellEnvironment.Apply()` runs once per process, before the first runspace and before any other use of the engine
(every public entry point calls it; idempotent and thread-safe). It sets, overriding what the user's environment says,
because a variable of the user must not weaken a privacy setting:

| Variable | What was observed with Microsoft.PowerShell.SDK 7.6.10 on Linux, with a clean `HOME`/`XDG_*`/`TMPDIR` |
|---|---|
| `POWERSHELL_TELEMETRY_OPTOUT=1` | Without it the first **command** (opening the runspace is not enough) writes `telemetry.uuid` (`$XDG_CACHE_HOME/powershell`; on Windows in the user's local application data). With it no file is written. The test of 9.1 has the control. |
| `POWERSHELL_DIAGNOSTICS_OPTOUT=1` | The name is in the engine (the diagnostics channel of `Enter-PSHostProcess`). A plain runspace creates no `PSHost.*` pipe on Linux, so the proof is the Windows run of the pipe test (no `\\.\pipe\PSHost.*`). |
| `POWERSHELL_UPDATECHECK=Off` | No update notification (a console host feature; set so that nothing ever looks for one). |
| `PSModuleAnalysisCachePath` = a file "inside" the null device (`/dev/null/ModuleAnalysisCache`, `NUL\ModuleAnalysisCache`) | See below. |

**The module analysis cache** is the only file the engine writes by itself that is not telemetry. It is
`ModuleAnalysisCache-<hash>` in the cache directory, written by a timer about 12 s after module discovery (measured: it
appeared 14 s after the process started, discovery having run at about 1 s; there is no flush at exit, so a short-lived
process never shows it). A probe on Linux with a fake module on `PSModulePath` and a clean cache directory gave:

| Setting | Discovery that ran | File after 40 s |
|---|---|---|
| auto-loading on (the default) | `Get-Command Get-Fake*`, completion of `Get-` | `ModuleAnalysisCache-67BE2BE9` written |
| `PSModuleAutoLoadingPreference = None` | the same, plus `Get-Command -Module`, `Get-Command` | **no file** (completion still lists 49 commands) |
| `None`, then `Get-Module -ListAvailable` | explicit module listing | file written (auto-loading off alone is not enough) |
| `PSModuleAnalysisCachePath=/dev/null`, `Get-Module -ListAvailable` | explicit module listing | **no file**, no error |
| `PSModuleAnalysisCachePath=/dev/null/ModuleAnalysisCache` (what the window sets; the opt-in test) | discovery, `Get-Module -ListAvailable`, completion | **no file**, no error |
| `PSModuleAnalysisCachePath=/proc/nonexistent/cache`, `Get-Module -ListAvailable` | explicit module listing | **no file**, no error (a failed write is swallowed) |

So the window turns auto-loading off **and** points the cache at a file inside the null device. The path is not the bare
device name on purpose: `/dev/null` as such worked on Linux, but a bare `NUL` on Windows (a path whose folder part is empty)
was not observed, whereas "a folder that cannot be created" is the failure the engine was seen to swallow (the
`/proc/nonexistent` row) and the same on both systems. The probe is repeatable as the opt-in test
`NoFilesTests.The_module_analysis_cache_is_never_written_but_would_be_with_auto_loading_on` (`ADMNG_POWERSHELL_SLOW_TESTS=1`,
about 40 s; it passed here with the path above). `NUL\ModuleAnalysisCache` on Windows is the one part not verified: it is a
**relative** path there (a folder `NUL` below the process's current folder, which the Win32 path rules turn into the device - so
creating it should fail, and the engine swallows the failure: `AnalysisCacheData.Serialize`, decompiled 7.6.10, catches every
exception of the write) and nobody has run it; the opt-in test runs in the Windows CI job since the audit (its window child works in
the watched folder, and the snapshot of the user's cache folder covers the engine's default place) and passed there from the CI of
151aff46 on (about 42 s). In
the window nothing makes the engine write the cache in the first place: auto-loading is off and `Get-Module` is not one of its
commands (4.6), so the path is the second line of defence.

**The time limit of regular expressions.** Not an environment variable but a setting of the .NET runtime:
`PowerShellEnvironment.LimitRegularExpressions()` sets `REGEX_DEFAULT_MATCH_TIMEOUT` (an `AppContext` value) to
`RegexMatchTimeout`, 2 s, so every regular expression of the process made without a limit of its own fails a match that takes
longer with `RegexMatchTimeoutException` – an error of its statement; the script goes on and the session stays usable.
Observed with .NET 10.0: the runtime reads the value once, when the first regular expression of the process is made (a later
call changes nothing), and the same value given as text in the runtime configuration (`RuntimeHostConfigurationOption`) makes
every regular expression throw a `TypeInitializationException`. So `Apply()` cannot set it; the program's module initializer
(`src/AdMng.App/ProcessStart.cs`, run before `Main`) calls it before any other code of the program, and the engine's tests do
the same (`tests/AdMng.PowerShell.Tests/ProcessStart.cs`). `HostTests` checks that the program's module sets it; the smoke test
checks in the published `AdMng.exe` that it is in effect (`RegularExpressionsLimited`) and that a script catches the error of a
match that backtracks. The limit applies to the whole process: the program's own patterns do not backtrack and run on short
values or on messages, where a match of 2 s would need hundreds of megabytes; a regular expression made with its own limit keeps
it (batch editing gives the operator's pattern 1 s; the Watchdog service, another process, gives its patterns 1 s) – and so does
one a script makes with a limit of its own, which only the leaving of a run (4.1) ends in the window. Measured
here: `'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!' -match '^(a+)+$'` did not end in 20 s without the limit; with it each of the seven ways
of the language to a regular expression ended after 2.0 s, in both language modes (`RegexLimitTests` keeps the seven in the
default mode and `-match` in the trusted mode).

**The handler of unhandled exceptions.** A script block that .NET code calls as a delegate on a thread without a runspace throws
`PSInvalidOperationException` with the error id `ScriptBlockDelegateInvokedFromWrongThread` ("There is no Runspace available to
run scripts in this thread"); on a timer or pool thread nothing handles it, and an unhandled exception ends a .NET process
(observed: exit 134 on Linux) – in the program every connection window with it. The trusted mode can leave such a callback
behind (a `[System.Threading.Timer]` whose callback is a script block, firing after its window closed; ConstrainedLanguage
refuses the timer, which is not a core type). `PowerShellEnvironment.ContainLeftoverScriptBlocks()` sets the process's handler of
unhandled exceptions (`ExceptionHandling.SetUnhandledExceptionHandler`, .NET 9 and later): exactly that error counts as handled
– the callback ends, its thread goes on – and every other exception ends the process as before (the predicate is
`IsLeftoverScriptBlockCall`). .NET allows one handler per process (a second call throws; the method then does nothing, and
`LeftoverScriptBlocksContained` stays false), so the program's module initializer sets it next to the regular expression limit,
and the engine's tests do the same. `EscapeTests` proves it in child processes (the probe's `leftover-callback`, which waits until
the handler has counted the callback - `ContainedLeftoverScriptBlockCalls` - and its control, which still crashes; neither waits a
time of its own for the timer, 12.14), `HostTests` checks that the program's module sets it, and the smoke test leaves such a timer
behind in the published `AdMng.exe`.

**Script block logging (decision D-B, audit PS-04).** The program's `powershell.config.json` has no `ScriptBlockLogging`
section: it is not the program's to decide. Until D-B it set `EnableScriptBlockLogging: false`, and what that did is in the
engine's code (`System.Management.Automation` 7.6.10, `engine/runtime/CompiledScriptBlock.cs`):

- `PerformSecurityChecks` runs when a script block is compiled, and only for a `ScriptBlockAst`: it returns at once for a function
  definition's body ("Checks are only needed at the top level"). The braces of `if`, `foreach`, `while` and the like are a
  `StatementBlockAst`, part of the block around them and never checked on their own; a script block literal `{ }` is a
  `ScriptBlockAst`, checked when it is compiled and logged on its own when it starts. It also skips AMSI and the check below for
  a block from a `.psd1` file whose content is a safe hashtable (a line typed in the window has no file). Otherwise it runs AMSI
  and then sets `HasSuspiciousContent` when `ScriptBlock.CheckSuspiciousContent(scriptBlockAst)` finds anything. That check has
  two parts:
  - `SuspiciousContentChecker.Match` over the text, against 119 words (`LookupHash`) that it compares by a 32-bit hash of the
    lower-cased word. It hashes only the ASCII letters `a-z`, `A-Z` and `-`; any other character - a space, a quote, a
    digit, a Polish letter such as `ł` - is skipped and ends the current run. While it reads, it tests the words that end at the
    current letter inside the current run; at the end of the text it tests every length up to 29 of the letters read last,
    across what was skipped. So a word inside a run is found (`Get-AdmUser jmarshall` - `Marshal`), so is a word the last letters
    spell across other characters (`Get-AdmUser 'grace mit'`, `Get-AdmUser emłit` - `Emit`), a word of the list with a digit
    (`FromBase64String`, `ToBase64String`, `kernel32`, `WriteInt32`, `SHA1Managed`) is never found as it is written, and since
    only the hash is compared, a text that holds no word of the list can match too (`Get-AdmUser mc-earraytype` has the hash of
    `MakeArrayType`). No rule about the letters of a line tells for sure that the engine will not mark it.
  - The parser's marks: `scriptBlockAst.HasSuspiciousContent`, set on every parse in the default mode (`Parser.cs`:
    `PerformPostParseChecks`, `SemanticChecks.CheckAst`) by `SemanticChecks.MarkAstParentsAsSuspicious` on the AST and all its
    parents: `CheckMemberAccess` (for `VisitMemberExpression` and `VisitInvokeMemberExpression`) when the member is not a
    `ConstantExpressionAst` - `$u.$a`, `$u.('mail')`, `$u."$n"`, `$u.$m()`, so an operator's
    `foreach ($a in 'mail', 'department') { $u.$a }` - and when a static member (`::`) is taken of something that is not a
    `TypeExpressionAst` - `$t::Now` -, `VisitTypeExpression` and `VisitConvertExpression` for the type `System.Type` itself
    (`[type]`, `[type]'System.String'`). `$u.mail`, `$u.'mail'` and `[datetime]::Now` are not marked.

  A word inside a run of letters and a mark of the parser mark the block around a nested block as well (the run is in its text
  too, and the parser marks every parent), but the end of a nested block is not the end of the text around it, so the end of the
  text can mark a nested block alone: with nothing set,
  `$global:sb = { Get-AdmUser 'grace mit' }; $null = & $global:sb; Get-AdmUser jan` is skipped and its nested block is logged,
  while `if ($true) { Get-AdmUser 'grace mit' }; Get-AdmUser jan` is skipped as a whole;
- `LogScriptBlockStart` (when a script block starts: `CompiledScriptBlock.cs` for an invoked block, `ScriptCommandProcessor.cs`
  for a script the window runs and `& $block`) calls `LogScriptBlockCreation(scriptBlock, force: HasSuspiciousContent)` unless
  the block is already `SkipLogging`;
- `LogScriptBlockCreation` returns when the block `HasLogged`; otherwise `if (force || logSetting?.EnableScriptBlockLogging ==
  true) { if (logSetting?.EnableScriptBlockLogging == false || scriptBlock.ScriptBlockData.IsProductCode) { SkipLogging = true;
  return; } … WriteScriptBlockToLog(…) … HasLogged = true; } else { SkipLogging = true; }`. `logSetting` is
  `Utils.GetPolicySetting<ScriptBlockLogging>(Utils.SystemWideThenCurrentUserConfig)`, read once per process
  (`s_sbLoggingSettingCache`): on Windows the registry of `HKLM`, then `HKCU`, then the all-users file, then the user's
  (`Utils.GetPolicySetting`), null when none sets it;
- `WriteScriptBlockToLog` writes a marked block with `PSEtwLog.LogOperationalWarning` and any other with
  `LogOperationalVerbose`, both `PSEventId.ScriptBlock_Compile_Detail` - event 4104 of the PowerShell 7 provider
  (*PowerShellCore/Operational* on Windows, where the provider is registered: the MSI of PowerShell 7 runs
  `RegisterManifest.ps1` while its property `REGISTER_MANIFEST` is 1, the default - the check box *Register Windows Event Logging
  Manifest*, `assets/wix/Product.wxs`; the zip ships the script in `$PSHOME` without running it, and the MSIX package declares no
  event provider, `assets/AppxManifest.xml`; the script, run elevated, registers it for any of them. AD Manager registers nothing).

So when nothing sets the policy, the engine writes a script block it marked to its log on its own, as a warning, and no other;
`true` writes every script block; `false` - a Group Policy set to *Disabled*, a file - writes none, not even the marked ones. The
program's old file was such a file: in the window not even a block with `Add-Type` reached the event log as a 4104 unless a Group
Policy turned the logging on. `HasLogged` belongs to the compiled block, and `ScriptBlock.Create` (`[scriptblock]::Create`) reuses
a cached compiled block for a text it compiled before (`TryGetCachedScriptBlock`), so through it a text still in that cache is
not logged again (the cache holds at most 1024 texts and is then cleared, and never takes a text with `using module`, `using
assembly` or a dynamic keyword); the window runs each line through `PowerShell.AddScript`, which parses it anew (`AutomationEngine.ParseScriptBlock`, no
cache), so a marked line typed again is logged again (12.13). Without the section, Group Policy decides, then a user's own
`powershell.config.json` (which can now turn the logging on, or off), and with neither the engine's default.

What the logs get from the window differs from what they get from a `pwsh.exe` in the points the guide lists (chapter 8); the
reasons in the code:

- a `pwsh.exe` reads the `powershell.config.json` of its own `$PSHOME` (e.g. `C:\Program Files\PowerShell\7`) as its all-users
  file, the window the program's file (`Utils.DefaultPowerShellAppBase`, `PSConfiguration.cs`), so a section there - also
  `ProtectedEventLogging`, read from the all-users file only (`Utils.SystemWideOnlyConfig`) - applies to `pwsh.exe` and not to
  the window; the file a PowerShell 7 install writes there has no logging setting;
- the program's file turns off the transcription and the module logging a user's file turns on (an all-users setting wins over
  the user's, see below); a `pwsh.exe`, whose all-users file as installed sets neither, follows the user's file;
- module logging (event 4103): a command logs its pipeline details when the module or the snap-in it comes from has
  `LogPipelineExecutionDetails` on (`MshCommandRuntime.InitShouldLogPipelineExecutionDetail`; `Add-Type` always), which the
  policy sets on the modules and snap-ins its `ModuleNames` match (`ModuleCmdletBase`,
  `PSSnapInReader.SetSnapInLoggingInformation`). AD Manager's commands and the window's own `ForEach-Object`, `New-Object`,
  `Tee-Object`, `Start-Transcript` and `Clear-Host` have neither, so they never log by themselves; a pipeline writes its 4103 when
  any of its commands logs (`PipelineProcessor._needToLog`), and then it names its other commands too -
  `Get-AdmUser … | Select-Object Name` writes `CommandInvocation(Get-AdmUser)` beside `Select-Object`'s parameters; a parameter
  binding is written only for a command that logs itself or while the host is transcribing. Every line typed in the window ends in
  `Out-Default` of the snap-in `Microsoft.PowerShell.Core`, so with `ModuleNames` `*` every line writes a 4103 with what reaches
  `Out-Default` (all seen on Linux, where the engine writes these events to syslog, 12.13);
- an event that names its host names the window's host (`WindowHost.Name`, *AD Manager PowerShell*) and the program's command
  line; 40961 (`PSEtwLog.LogConsoleStartup`, which only `UnmanagedPSEntry.Start` of the console host calls) and 40962
  (`ConsoleHost.cs`) come from the console host, which the program does not run, and 53504
  (`NamedPipeIPC_ServerListenerStarted`) from `RemoteSessionNamedPipeServer`, whose static constructor starts the pipe only when
  `POWERSHELL_DIAGNOSTICS_OPTOUT` is not true - the program sets it to 1 (the table above).

`ScriptBlockLoggingTests` shows the script block logging with the real engine in child processes (the probe's
`script-block-logging:<all-users file>|<user's file>` mode sets the all-users file as `pwsh -SettingsFile` does,
`PowerShellConfig.SetSystemConfigFilePath`, the user's file by `PowerShellConfig.perUserConfigFile`, compiles each of twelve lines
with `[scriptblock]::Create`, runs it once and reads the internal `HasLogged` / `SkipLogging`): an empty file and the program's
file both give `setting=null`, the eight marked lines (`$properties = …`, `$u.$a` in a `foreach`, `$u.('mail')`, `$u."$n"`,
`$t::Now`, `[type]`, `Get-AdmUser 'grace mit'`, `Get-AdmUser jmarshall`) logged and the four others (`$attributes = …`;
`$u.mail; $u.'mail'; [datetime]::Now`; `$x = [Convert]::FromBase64String('eA==')`; `Get-AdmUser jkowalski`) skipped; a file
with the old section gives `False` and skips all twelve; the program's file with a user's file that says `true` gives `True` and
logs all twelve. On Linux, which has no Group Policy, the setting of the first two must be `null`; on Windows the test is skipped
when the machine's own policy sets it. The test reads internal members on purpose: an engine update that changes them fails it,
and this paragraph is read again. The window's note says what the policies record (D7) and makes no sentence of the engine's
default (12.13).

**Other process-level facts** found while proving it, relevant to later parts:

- `$PSHOME` is the folder of `System.Management.Automation.dll`: `runtimes/<rid>/lib/net10.0` of a framework-dependent
  output, not the application folder. The four manifests (`Microsoft.PowerShell.Host|Management|Security|Utility`) are in its
  `Modules` folder; the default session state loads none of them as modules (its cmdlets are bound directly).
- A `powershell.config.json` with `PowerShellPolicies` (`Transcription`, `ScriptBlockLogging`) in the **user's**
  configuration folder makes the embedded engine write transcripts that contain the commands **and their output**; one in
  `$PSHOME` (the all-users scope) **wins over the user's** (verified on Linux: user on / all-users off = no transcript; user
  off / all-users on = a transcript). So the README's plan stands – a `powershell.config.json` in the engine's folder turns
  off what a user's file turned on (since D-B the transcription and the module logging, not script block logging: see above) –
  and the policy reader of D7 must read these files too, not only the registry (on Windows the registry GPO comes first; the
  engine's `Utils.GetPolicySetting` reads the JSON policies after the registry there too).
- `.NET`'s own diagnostic sockets (`dotnet-diagnostic-*`, `clr-debug-pipe-*` in the temp folder while the process runs)
  belong to the runtime, not to PowerShell; they are gone when the process ends and are outside this track.
- ConstrainedLanguage in 7.6.10 refuses more than the documentation suggests: `[pscustomobject]@{…}` ("Only core types are
  supported in this language mode"), any method call on a non-core type (`$PSCmdlet.ShouldProcess()`, `[System.IO.File]::`),
  while `New-Object PSObject -Property @{…}`, `Select-Object` with calculated properties, `Format-*`, `Out-String` and the
  pipeline cmdlets work. The guide (part 5) says so.

### 4.3 The host

`WindowHost : PSHost` (internal), `WindowHostUserInterface : PSHostUserInterface`, `WindowHostRawUserInterface :
PSHostRawUserInterface` (minimal: buffer and window size from the session's width, colours, no cursor, no key reading).

- All output goes through the user interface into the **output channel** of the run, as items (4.4): `Write`, `WriteLine`
  → text of the output stream (with the colours `Write-Host` gave; `Write-Information` arrives the same way);
  `WriteErrorLine`, `WriteWarningLine`, `WriteVerboseLine`, `WriteDebugLine` → text items of those streams (without the
  `WARNING:`-like prefix; the window decides how to show a stream); `WriteProgress` → a progress item. Nothing goes to a
  file or a console.
- **Prompts.** `ReadLine`, `ReadLineAsSecureString`, `Prompt` (a missing mandatory parameter: one prompt per field, the
  answer converted to the parameter's type; a parameter that takes an array is asked for element by element, `Identity[0]`,
  `Identity[1]`, ..., until an empty answer, as the console host asks - an answer is never split at commas, so a distinguished
  name stays one element, audit PS-05; the prompt text is the field's name, as in the console host, not its label with the
  accelerator), `PromptForChoice` (confirmations: `-Confirm`, `ShouldContinue`) post a **prompt
  item** into the same stream and block the pipeline thread until the consumer answers it; Stop cancels the wait, and a
  cancelled prompt stops the pipeline (`PipelineStoppedException`, like Ctrl+C at a console prompt). `PromptForCredential`
  throws `PSNotSupportedException` with a clear message (R1 creates no `PSCredential`). `EnterNestedPrompt` is not supported.
- **`exit` and `Clear-Host`** (audit PS-12, 12.10). `SetShouldExit` (the engine calls it for `exit`, also inside a function or a
  script block) keeps the exit code, which the session puts into the end item; the run ends there and the process does not (until
  the audit it raised an event nobody subscribed to, and `exit` ended the run without a word). `SetBufferContents` with the
  rectangle -1, -1, -1, -1 - the whole buffer, what `Clear-Host` asks for - posts a clear item; any other rectangle has nothing to
  fill.
- `Name` is "AD Manager PowerShell", `Version` the program's version, `CurrentCulture` and `CurrentUICulture` follow the
  `Culture` option (the window's UI language); `SupportsVirtualTerminal` is false (no ANSI sequences: the window colours
  text itself).
- **How a command reaches the window's services:** `WindowRegistry`, a table inside the engine keyed by the runspace
  (`Runspace.DefaultRunspace` on the pipeline thread); `AdmCmdlet.ContextOrNull` and the completers' `AdmCompletionContext` read
  it. The host carries nothing of the window: its `PrivateData` is empty (until the audit of R1 it held the context in an object
  without a public member, and a `ForEach-Object -Parallel` runspace - FullLanguage whatever the window's mode - reached the
  window's `IDirectorySession` through `$using:Host` and wrote around the change plan). No variable, parameter or member of the
  session hands the context to a script, and a script in ConstrainedLanguage cannot read a static member of a type that is not a
  core type, nor run code in another language mode (4.6). In FullLanguage reflection can reach anything in the process, this
  table included - the boundary ADR-0010 states.

### 4.4 Output items and streaming

`IPowerShellSession.RunAsync(script, cancellationToken)` returns `IAsyncEnumerable<PowerShellOutputItem>` (Core):

| Item | When |
|---|---|
| `PowerShellTextItem(Stream, Text, Foreground?, Background?)` | Anything the host was asked to write; `Text` includes its line ends (`\n`); `Stream` is Output, Error, Warning, Verbose or Debug. A formatted error record arrives as one or two text items of the Error stream. |
| `PowerShellProgressItem(ActivityId, Activity, Status, PercentComplete, Completed)` | `Write-Progress`. |
| `PowerShellPromptItem` | A prompt or confirmation (4.3): `Kind` Text, Secret or Choice, `Caption`, `Message`, `Label`, `Choices`, `DefaultChoice`; answered with `Respond`, `RespondSecret`, `RespondChoice` or `Cancel`; its `ToString()` shows no question and no answer. |
| `PowerShellObjectItem(Value)` | Only in **object mode** (`PowerShellSession.RunObjectsAsync`, not on the Core interface): each object the pipeline emits, unformatted (a `PSObject`). For tests of the commands and for programmatic callers. |
| `PowerShellClearItem` | `Clear-Host` / `cls` (4.3): the window clears its output there, like Ctrl+L, in the order of the other items. |
| `PowerShellRunEndItem(State, HadErrors, ExitCode?)` | Always the last item - kept on the run and yielded after the channel's last item, never posted into the channel, where a stopped flood dropped it (audit PS-07; the view model no longer makes one up when it is missing): `Completed`, `Stopped`, `Failed` (a terminating error, whose text is the item before it) or `Abandoned` (4.1); whether any error was written - for a stopped run only an error the command wrote, not the stop itself; and the code of an `exit` that ended the script. |

The window uses the **formatted mode**: the script is run as one pipeline followed by `Out-Default`, with the error stream
merged into it, the way the console host runs a command. Between the two stands the window's compiled pass-through
`Write-AdmWindowOutput` (`WindowOutputCommand`, not in the session state), and the merge is the script's own
(`MergeMyResults`), not "the unclaimed results" of `Out-Default` (audit PS-06): the engine starts a console program that is last
in its pipeline with the process's own standard output when the command downstream is `Out-Default` - in AdMng.exe on Windows a
hidden console, so `whoami` showed nothing - and dropped what such a program writes to its error output. Now both are captured:
the output as text, the error output as plain text in its place (as PowerShell 7 shows a captured error output; it does not make
the run one with errors). Proven on Linux with `/bin/echo` and `/bin/sh -c '... 1>&2'`; the same tests use `cmd.exe /c` on
Windows, where the engine still allocates its hidden console (`ConsoleVisibility.AllocateHiddenConsole`) but no longer hands it
the output. The engine's own formatting therefore decides tables and lists (a
header once, column widths from `OutputWidth`), and the text reaches the host as above; the objects are real objects up to
`Out-Default`, so `Where-Object`, `Select-Object` and `Out-AdmGrid` in the pipeline work on them. The script runs in the
global scope (variables stay between commands, like in a console). The channel is bounded by its number of items (4096) and
by the size of the text it holds (1,048,576 characters, 2 MB; audit PS-09 - by the count alone, 4096 strings of 100,000
characters held 790 MB while the window was behind): a producer that outruns the window waits, so `while ($true) { 1 }`
cannot eat the memory before Stop, one text larger than the bound still passes when the channel is empty, and the session
never buffers a whole result. The session is idle again **before** the consumer sees the end item.

### 4.5 AD Manager's commands

`AdmCommands.Catalog` is the one catalog (name → type); `AdmCommands.Entries()` creates the `SessionStateCmdletEntry` list
for the initial session state. A command class is a plain `PSCmdlet` derived from `AdmCmdlet` (public, so that R3 and the
tests can derive from it):

```csharp
public abstract class AdmCmdlet : PSCmdlet
{
    protected IAdmWindowContext? ContextOrNull { get; }         // from the window registry of the runspace (4.3)
    protected IAdmWindowContext RequireContext();                // throws a clear error outside a window
    protected CancellationToken StopToken { get; }               // cancelled by StopProcessing
    protected T Wait<T>(Task<T> task);                           // blocks the pipeline thread (never the UI thread); a stop becomes PipelineStoppedException
    protected void WriteAll<T>(IAsyncEnumerable<T> source, Func<T, object?> map);   // pulls on the pipeline thread, writes as it goes
}
```

`IAdmWindowContext` is what a command may use: `IDirectoryReader Reader` and `string ApplicationVersion`; part 2 adds the
window services (`OpenObject(dn)`, the grid, a save-path chooser) and R2 the pending plan. The program builds the context as
`new AdmWindowContext(session, version)`, which wraps the session in a `ReadOnlyDirectoryReader` (searches, base reads, ranged
values, `Info`, `Schema` – no write method, and it cannot be cast back to the session, so no path to the connector's
credential; CLAUDE.md rules 2, 4, 5). Commands are tested without WPF and without a directory: a `PowerShellSession` is
created over a `FakeDirectorySession` and the command is run as a script, which is the path the window uses. **Part 1 ships
one command, `Get-AdmVersion`** (kept: the program's and the engine's version, the language mode, the domain, the DC and the
account of the window's session – a useful command and the end-to-end proof of the mechanism).

**How to add a command (parts 2 and R2):** a class with `[Cmdlet]` under `Commands/`, derived from `AdmCmdlet`; an entry in
`AdmCommands.Catalog`; the expected list in `CommandCatalogTests`; a test that runs it through a `PowerShellSession` over a
fake directory (`TestSessions.CreateAsync(new FakeWindowContext(session))` and `RunObjectsAsync` for the objects, `RunAsync`
for what the window shows).

### 4.6 The allow-list and the guards (D2)

Rebuilt after the audit of R1 (PS-01, PS-03, ps-commands-5): until then the session was `CreateDefault()` with 233 cmdlets minus
`Start-Job` and `Import-Module`, and a window that said ConstrainedLanguage ran `ForEach-Object -Parallel` in FullLanguage, reached the
directory session through the host and wrote around the change plan; `-AsJob` jobs outlived Stop and the window; `using module` loaded
modules; `Out-File`, `Export-Csv`, `Set-Content`, `>` and `Start-Transcript` without a path wrote files. What the window has now:

- **One list** - `Guards/CommandGuards.cs` - and `CommandGuards.Apply` removes every command entry of `CreateDefault()` that is not
  on it. Kept unchanged: 68 engine cmdlets (`EngineCmdlets`: objects and the pipeline, formatting, data in memory, files and
  locations read only, variables and aliases, `Get-Command` / `Get-Help`, values, messages and `Read-Host`, `Start-Sleep`,
  `Measure-Command`, `Set-StrictMode`, `Get-ExecutionPolicy`, `Stop-Transcript`) and one engine function (`TabExpansion2`, which
  completion calls by name). Added: four **replacements** (below), the window's own compiled `Clear-Host` in place of the engine's
  function (`WindowCommands`; it asks the host to clear its whole buffer, 4.3 - the engine's function did nothing in the window on
  Windows and wrote terminal codes to the process's console elsewhere, audit PS-12; `cls` stays its alias), twelve **refusals** - functions that write
  an error of the command's own name with the id `AdmCommandNotAvailable` (`Start-Job`, `Import-Module`, `Invoke-Command`,
  `Invoke-Expression`, `Get-Credential`, `Start-Process`, `Out-GridView`, `Export-Csv`, `Export-Clixml`, `Out-File`, `Set-Content`,
  `Add-Content`) - and the alias `help` → `Get-Help` (the engine's `help` function pipes into a pager). Of the engine's aliases only
  those that point at a kept, replaced or refused name stay. The application entry stays (console programs run as their own
  process with the operator's identity); the script entry goes; the `Function` provider goes (a string assigned to `function:x`
  becomes code). `SessionCommandsTests` lists a new session's cmdlets and functions and compares them exactly with a copy of the
  list in the test; the smoke test of the published `AdMng.exe` compares the count.
- **Replacements** (`Guards/ReplacementCommands.cs`, internal): `ForEach-Object` without `-Parallel`, `-AsJob`, `-ThrottleLimit`,
  `-TimeoutSeconds` and `-UseNewRunspace`; `Tee-Object` into a variable only; `New-Object` without `-ComObject`; `Start-Transcript`
  only with `-Path` / `-LiteralPath` (without one it writes the refusal; `-OutputDirectory` is not a parameter). Each declares only
  the allowed parameters and runs the engine's cmdlet - which is in the session under no name - through a steppable pipeline built
  from the window's **bridge** (`param($command, $parameters) & $command @parameters`, made by `WindowSetupCommand` while the session
  is briefly FullLanguage; a steppable pipeline built from a constrained script block refuses to evaluate the command object). The
  cmdlet therefore runs in the **caller's** language mode and scope. Two designs that were tried and rejected: a FullLanguage proxy
  function in the global session state (`$count++` in the script block was lost, `$wrappedCmd` - the real cmdlet with `-Parallel`
  - was readable from the script block's scope, 8x slower) and the same in a dynamic module (semantics right, but the proxy
  switched the context to FullLanguage, so `ForEach-Object -MemberName` called any .NET method from ConstrainedLanguage:
  `Get-Item / | % -MemberName GetFiles` worked). The compiled replacement keeps `$count++`, scopes, `return` and `continue` as the
  engine's cmdlet does; `1..100000 | ForEach-Object { $n++ }` takes about 0.22 s against 0.13 s for the engine's cmdlet (Release
  build on Linux, warm; the first run of a session about 0.6 s).
- **`InputGuard`** refuses from the parsed text of a command, before anything of it runs (an error and a `Failed` end item, like a
  parse error), what reaches the engine without a command name: `using module` and `using assembly` (the engine imports through
  `ImportModuleCommand` directly; a binary module runs .NET code even in ConstrainedLanguage), the background operator `&` (it starts
  the engine's `StartJobCommand` directly), and redirection to a file (`>`, `>>`, `2>`, `*>`: the engine's `Out-File`; `> $null`
  and `2>&1` stay). Nested script blocks are searched. A typed `ForEach-Object -Parallel` (also `%`, `foreach`, `-AsJob` and the
  other parallel parameters, abbreviated as far as the engine accepts) gets a message saying why; the protection is that the
  replacement has no such parameter (a splatted one fails in parameter binding). Text cannot become code past this check: there
  is no `Invoke-Expression` (in ConstrainedLanguage it would stay constrained, but it compiles text this check never saw), no
  `function:` drive, and `[scriptblock]'text'` is refused in ConstrainedLanguage. A text the check cannot walk safely is refused
  as a whole (`TooComplexMessage`, 12.9): `Input/InputParser.cs` is the one parse of what was typed (the guard, the history policy,
  highlighting, the end-of-input check, completion); a text longer than 256 Ki characters is not parsed, one longer than 4 Ki is
  parsed on a thread with 64 MB of stack, and a syntax tree deeper than 500 levels - or none, when the parser gave up - counts as no
  tree. The walks over a tree recurse once per level and a .NET stack overflow ends the process: a chain of about 9 300 operators
  ended the engine's compiler.
- **`ScriptFileGuard`**: no script file runs. The session lists no script, so one called by name or path from the command line is
  not found; the engine's `PostCommandLookupAction` (set from C#; a constrained script cannot change it) drops a script file that a
  lookup from inside a script block or a function found; on Windows the engine asks the window's `WindowAuthorizationManager`
  before it runs any script file or loads a script module, and it refuses every `.ps1`, `.psm1` and `.cdxml` whatever the execution
  policy says (the policy stays `Restricted`). On Linux the engine asks no authorization manager, so there a script file obtained as
  a command object (`Get-Command` inside a script block, then `&`) still runs; the window is not shipped on Linux.
- **Jobs** end with their command (4.1).

`CommandGuards.AllowedModules` is the list `Import-Module` may load: **empty** in R1 (R3 fills it, replaces the `Import-Module`
refusal by a command that checks the list, and has to decide what `InputGuard` does with `using module` of an allowed module; track C
adds the Microsoft 365 ones). The list is a protection against mistakes, like the language mode - not a sandbox: in the trusted mode
.NET code reaches the engine's cmdlet objects (`[CmdletInfo]::new(...)`) and anything else in the process.

### 4.7 Language mode (D3)

- `PowerShellSession.LanguageMode` is `Constrained` until `SetLanguageModeAsync(Full)` succeeds; `TrustedModeBlock` says
  whether the window's switch may be turned on (`None` or `SystemLockdown`; the window localizes the reason). The probe
  (`IPowerShellLockdownProbe`, injected; the real one wraps `SystemPolicy.GetSystemLockdownPolicy()`) is asked at creation and
  again on every switch to the trusted mode: under `Enforce` the session stays `Constrained`, the switch throws and
  `TrustedModeBlock` is `SystemLockdown`; `Audit` does not block.
- Switching on sets `Runspace.SessionStateProxy.LanguageMode`; switching off makes a new runspace (4.1); both are refused while a
  run is active. The mode never leaves the session object: nothing persists it, and a new window starts in ConstrainedLanguage (a
  test creates a session after a trusted one).
- ConstrainedLanguage holds for every command of the window: no command runs code in another runspace or language mode
  (`LanguageModeTests` tries each way - `-Parallel` typed, as `%` and splatted, `[powershell]`, `[runspacefactory]`, `InvokeScript`,
  `[scriptblock]::Create` and the cast, `Invoke-Expression`, a `function:` assignment, `Add-Type`, jobs). The trusted mode changes
  the language mode only; it adds no command. Nothing defined while it was on runs in FullLanguage after it was turned off - the
  runspace is new (audit PS-02, `TrustedModeOffTests`). What its .NET code changed outside the runspace stays until the program
  closes, in every window: a thread or timer it started (4.1), and a member it added to a process-wide .NET object - `Add-Member`
  on the type object `[string]` or on the culture of `Get-Culture` - which a constrained window can still call, in FullLanguage
  (measured; no runspace can take it back).
- The confirmation dialog, the title suffix and the lasting note are the window's (parts 3 and 4); the engine only says what is
  possible.

### 4.8 The command list test (D9)

`CommandCatalogTests` compares `AdmCommands.Names` with the expected list; a reflection test finds every public type of the
engine assembly with a `[Cmdlet]` attribute and fails if one is not in the catalog, does not derive from `AdmCmdlet` or has a
write verb (`Set`, `New`, `Remove`, `Move`, `Rename`, `Add`, `Enable`, `Disable`, `Unlock`, `Reset`, `Clear`, `Restore`,
`Update`, `Write`, `Invoke`, `Grant`, `Revoke`, `Copy`, `Import`, … – the full list is in the test); a run-time test lists
`Get-Command -Name *-Adm*` in a live session and compares it with the catalog, and checks that there is no `AD:` drive and no
`*-AD*` command. R2 changes the list and that test together. The rest of the session's commands are pinned by
`SessionCommandsTests` (4.6): every cmdlet and function of a new session, compared exactly with the list written out in the test.

### 4.9 History, syntax and completion

- `PowerShellHistoryPolicy.ShouldRemember(line)` is a pure function over the parser's tokens and AST. A line is **not**
  remembered if it is empty, still incomplete, too long or too deep to parse (`InputParser`, 4.6), has the `-AsPlainText`
  parameter (also a prefix of three letters or more; of `ConvertTo-SecureString` every prefix, `-As` and `-A`, which bind to it
  there while the secret comes through the pipeline), casts a literal to SecureString (`[securestring]'x'`) or assigns one to a
  target typed so (`[securestring]$x = 'x'`), or gives a literal (a constant, a string without embedded expressions, an array of these; also `-Password:'x'` and a bare word;
  also a value computed from literals alone – in parentheses or a subexpression `('x')`, `$('x')`, through an operator
  `'a' + 'b'`, `-join ('a', 'b')`, a cast `[string]'x'`, or the value of a chained assignment `$password = $x = 'x'`)
  to a **secret's name**: a named parameter, a key of a hashtable (inline or splatted, `@{ Password = 'x' }`), or the target of
  an assignment (a variable without its scope or drive, `$env:ADMIN_PASSWORD`, a property `$cred.Password`, a constant index
  `$h['ApiKey']`, through a cast `[string]$token`); of an interpolated key, property or index (`"Pass$x"`) each literal part
  counts as its name. A secret's name contains `Password`, `Passwd`, `Passphrase`, `Pwd`,
  `Secret`, `Token`, `ApiKey` or `PrivateKey`, is `Pw`, `Pin` or `Key` as a whole, or abbreviates `Password`/`Passphrase` with
  two letters or more (`-Pa`, `-Pas`, `-Pass` – PowerShell binds every unambiguous prefix of a parameter's name). A literal written positionally is a secret for `ConvertTo-SecureString` (or as its
  `-String`) and for a command whose noun is a secret's name (`Reset-AdmPassword user 'x'`, `Set-Secret n 'x'`) unless its verb
  only names one (`Get`, `Find`, `Search`, `Show`). A variable, a command or `Read-Host -AsSecureString` in the value is
  fine; a line with a syntax error is still checked from its tokens. The words began as PSReadLine's filter; the red team of R1
  (goal g) found that the first list kept the splatted hashtable - the usual way to pass credentials - `-Passphrase`, `-Pwd`,
  `-Pass`, `-Key` and the positional `Reset-AdmPassword user 'x'`, and the audit (PS-13) the assignments; the second red team
  (12.8) the literal in parentheses or put together from literals, `-Pa`/`-Pas` and the chained assignment, and the third (12.9)
  `ConvertTo-SecureString -As`, the interpolated key `"Pass$x"` and `[securestring]$x = 'x'`; all are refused now
  (`HistoryPolicyTests`), while `-PassThru`, attribute names such as `pwdLastSet` given as values, `Get-Secret MySecret` and
  `Reset-AdmPassword -Identity x` stay remembered. After the audit's engine unit (12.11, the rest of PS-13) a literal is also a
  variable the same line set to one (`$p = 'x'; Set-Thing -Password $p`), the second argument of `New-Object PSCredential` or
  `[pscredential]::new` is a secret, and console programs (a name with `.exe` and the like, or without a dash) have rules of
  their own: the third word of `net user` and the word after the share of `net use` (not `*`), a switch `/name:value` or
  `/name value` whose name is a secret's name or `p`, `pd`, `po`, `pw`, `rp`, `pass`, and `-p`/`-P` (also those short names)
  of `psexec`, `sqlcmd` and a few others or of any `.exe` - only there, since `-p` of a cmdlet is usually `-Path`. What is still
  remembered: a secret given positionally to a command whose name does not say so (`Connect-Thing 'x'`), one inside a longer
  text, a switch of a console program outside these names and a name outside the lists – the guide says not to type a password
  into a command at all. The window's history lives in `AdMng.Presentation` (memory only) and asks this function through
  `IPowerShellSession.ShouldRemember`. `PowerShellHistoryPolicy.HideSecrets(line)` (`IPowerShellSession.HideSecrets`) gives the
  line as the scrollback echoes it: each secret the rules found - the literal, the literal a variable was set to, the value after
  a console program's switch, the literals of a command with `-AsPlainText` and those piped into it - replaced by `***`; the
  command itself runs as typed.
- `PowerShellSyntax.Tokenize(input)` returns spans (`Start`, `Length`, `Kind`: command, parameter, variable, string, number,
  operator, comment, keyword, type, member) from the parser's tokens (`InputParser`), in order, never empty, with whitespace,
  brackets and bare arguments left out (kind `Other` is not listed); a text too long to parse has none. `IsComplete(input)` is
  `true` unless a parse error is of the "incomplete input" kind (an open quote, bracket, script block or here-string, a trailing
  pipe) – it decides Enter versus a new line; other syntax errors do not count (such a line runs and fails), nor does a text too
  long or too deep to parse (Enter runs it and the guard says why it is refused). Both are pure and need no runspace, so the editor
  may call them on every keystroke.
- `CompleteAsync(input, cursor)` calls `CommandCompletion.CompleteInput` on the window's idle runspace and returns the
  replacement span and the matches (text, list text, kind, tool tip); a text too long or too deep to parse gets none (the engine's
  completion would parse it again on a pool thread). `CompleteInput` runs the session's `TabExpansion2` and the argument
  completers - code the operator can replace - so the completion has a time limit (5 s), Stop and its token stop it, one stuck in
  .NET code is left after the grace period (the session goes on in a new runspace), and one that fails or is stopped offers
  nothing (audit PS-10: a looping `TabExpansion2` held the session's gate until the window closed). File system completion is removed in R1: matches of
  kind `ProviderItem` and `ProviderContainer` are dropped (a test shows they would otherwise be offered for `Get-ChildItem /`).
  Parameter completers for AD Manager's commands (part 2) are `IArgumentCompleter`s on the parameters: they read
  `Context.Reader.Schema` (attribute names) and the DNs the session already knows, never the directory.

### 4.10 How results reach the window and the grid

```
pipeline thread (the runspace's own)                         UI thread
Get-AdmUser jan | Select-Object Name, mail | Out-Default  ->  host.UI.WriteLine(...) -> OutputSink -> channel (4096) -> RunAsync's items -> view model -> output document
Get-AdmUser jan | Out-AdmGrid                              ->  OutAdmGridCommand collects PSObjects (EndProcessing)
                                                               -> converts them on the pipeline thread to plain data
                                                                  (PowerShellGridTable: column names + rows of simple values,
                                                                  anything else as its string form)
                                                               -> IAdmWindowContext window service ShowGrid(table, title)  -> view model -> the window opens the
                                                                  program's object list grid with that table as its source
```

- **Text** reaches the window through the host (4.3, 4.4): the pipeline thread posts items into the bounded channel, the view model
  reads them in batches on the UI thread (it never touches a `PSObject`), and a flood is held back by the channel, not by the UI.
- **The grid** is not fed from the output items: `Out-AdmGrid` is a command inside the pipeline, so it sees the real objects
  (`Where-Object`, `Select-Object` and `Sort-Object` before it work). It turns them into plain data **on the pipeline thread**
  (no `PSObject` crosses to the UI thread; values the grid cannot show as such become their string form, `DistinguishedName`
  values stay recognisable so *Open in the program* works in the grid too) and calls the window service. The service is part 2's
  interface in the engine (`IAdmWindowServices`: `OpenObject(dn)`, `ShowGrid(table, title)`, `ChooseSavePath(...)`), carried by the
  context like the reader; part 3's view model implements what is UI-independent and part 4's App implements the rest (opening the
  object list grid, the *Save As* dialog). The grid's own sorting, filters, copying and CSV export (with formula protection) then
  work as for any list; nothing is written unless the operator exports (rule 1).
- **Cancellation** flows the other way: Stop (the button, Ctrl+C, a closed window, the token of `RunAsync`) → `RequestStop` →
  `PowerShell.BeginStop` → the engine calls `StopProcessing` on the running commands → `AdmCmdlet.StopToken` is cancelled → the
  directory read (a token of `IDirectoryReader`) is cancelled at the LDAP request (rule 6) → the pipeline ends → the end item.

## 5. The read commands (part 2)

The table is the plan; what was built, with the parameters it has, is section 12.2 (it wins: `-SizeLimit` became `-ResultSetSize` and
`-ResultPageSize`, there is no `-Raw` and no `.Attributes`, `-Scope` is an alias of `-SearchScope`).

| Command | Parameters (besides the common `-Properties`, `-SizeLimit`, `-Raw`) | What it does |
|---|---|---|
| `Get-AdmObject` | `-Identity` (DN, `objectGUID`, SID, sAMAccountName, name), `-LdapFilter`, `-SearchBase`, `-Scope` | One object, or a search with a raw filter checked by the filter parser. |
| `Get-AdmUser` / `Get-AdmGroup` / `Get-AdmComputer` | `-Identity`, `-Name`, `-Filter` (wildcards), common `-SearchBase`, `-Scope` | The class-specific search with the program's attribute set for that class. |
| `Get-AdmGroupMember` | `-Identity`, `-Recursive` | Direct members (ranged retrieval beyond 1500) or the nested closure with a cycle guard. |
| `Search-AdmObject` | `-Name`, `-Attribute`, `-Value`, `-SearchBase`, `-Scope`, `-ObjectClass` | The filter is built from the parameters through `LdapFilter`; a value such as `Łukasz (Kraków)*` is escaped (RFC 4515) and `*` is a wildcard only where the parameter says so. |
| `Open-AdmObject` | `-Identity` (DN or `objectGUID`) | Asks the window to open the object's properties. |
| `Out-AdmGrid` | `-InputObject`, `-Title` | Collects the objects, turns them into a table (columns from their properties) and hands it to the window, which shows it in the program's object list. |
| `Export-AdmList` | `-InputObject`, `-Path`, `-Delimiter` | CSV with formula protection as in the list export; only to the path given or the one chosen in *Save As*; never to a default path. |

Common behaviour: results stream as the server pages arrive (paged results control, 1000 per page), the read is cancelled by
Stop (the token reaches the LDAP request), values are decoded by the same code the program's windows use (GUIDs, SIDs, dates,
`userAccountControl` flags as names) so the comparison with the list view is exact, and the secret attributes the Attribute
Editor masks (`SecretAttributes`) are masked. Each command returns a typed object (`AdmUser`, `AdmGroup`, …) whose properties
are the program's names with the raw attribute values available through `.Attributes`. No command has `-Server` or
`-Credential`.

## 6. Presentation (part 3)

This is the plan; what was built is section 12.3 and it wins (the policy types are in Presentation, not Core; the raw reads are
`IPowerShellPolicySource`, not `IPowerShellPolicyRegistry`; the real engine is not used in the Presentation tests).

`PowerShellWindowViewModel` (one per window, created by the connection's `IDialogViewModelFactory`, tested on Linux against a fake
`IPowerShellSession`):

- the **status line** (`PageStatusHolder`, `docs/dev/v2-status-controls.md`): the summary is "domain · DC · account · mode"; a
  running command is an `OperationScope` (`Status.Begin`) with the Stop of the line (the window's own Stop button is the
  action button's cancel); the PowerShell policy note is a **lasting Attention** (`SetLasting`) until the first command; the
  trusted mode is a lasting Attention while it is on. `writes:` stays `None` – R2 sets it where the plan executes. A run the
  engine left (`Abandoned`, 4.1, since 12.8) ends as an Attention ("stopped after … but did not end; the window goes on in a new
  session") with a line in the scrollback that says the variables, functions and modules of earlier commands are gone; when the
  new runspace is constrained because a lockdown came meanwhile, the line of the trusted mode's end follows and the title, the
  summary and the switch change with it. Turning the trusted mode off is a state of its own while the session makes its new
  runspace (`IsSwitchingMode`, 12.10): a Working line that cannot be stopped, Run, Tab and the switch disabled, the completion
  list and a running highlight dropped first; afterwards a line says that the session is new and the variables, functions and
  aliases are gone, and the lockdown is read from the session again. What keeps a window from running is kept as its cause - no
  engine, the exception of a failed start or of a session that lost its runspace - and the texts of the lasting state and of the
  switch's reason are made when they are read (they follow the language; the reason goes through `ErrorTranslator`); the switch
  has a reason in every state it is disabled in.
- `PowerShellInputModel`: the buffer, the caret, the history (a list in memory with Up/Down and Ctrl+R incremental search),
  the completion state (matches, selection, the span to replace) and the "Enter runs or inserts a line" decision
  (`IsComplete`).
- `PowerShellOutputDocument`: the scrollback as blocks (the echoed command, text runs with a stream and a colour, a prompt,
  a run summary) with a bounded size (the oldest blocks are dropped; there is no file; a line holds at most `MaxLineLength` =
  8,000 characters and a longer text goes on in a new line - audit W-04: an open line grew without a limit and every piece of it
  copied all its runs, joined its whole text and raised two changes, so 40,000 pieces of `Write-Host -NoNewline '.'` took 14 s
  and the view rebuilt every inline per piece; a growing line now keeps a list and a `StringBuilder`, a piece of the same colours
  joins the last run, and the line tells its change once per batch - 100,000 pieces take well under 3 s), and the DN recognition: a
  `DistinguishedName` is recognised in a text run with `DistinguishedName.TryParse` over candidates found by a regular
  expression anchored on `CN=`/`OU=`/`DC=`, and becomes a link (`OpenObjectCommand`). A name the formatting may have wrapped is
  no link (audit W-06; `Format-List` breaks a value at the width at any character, and the first line then often ends in a valid
  but cut name, `...,DC=contoso,DC=te` or `...,DC=contoso,`, while the next starts with the rest): one that ends a line as long
  as the formatting writes (`Width` - 1; the view model gives the document the session's width at the start of a run), one
  followed by a comma at the end of the line, and one that starts an indented line. The formats cannot keep a list value in one
  line (the engine's list writer wraps it; a table cuts with `...`), so the guide points to `Select-Object -ExpandProperty
  DistinguishedName`, whose strings are written unwrapped.
- **A place for the pending plan** (R2): a `PendingPlanState` property of the view model with its own area in the window
  (collapsed, empty) so that R2 adds the panel without changing the layout.
- the policy note: `PowerShellPolicies` from `PowerShellPolicyReader` over an `IPowerShellPolicySource` (the raw reads; the Windows implementation is in `AdMng.App`, the fake in the tests) and
  `PowerShellPolicyNotes.For(policies)` (section 12.3 has the keys, the order and the text);
- the command descriptor `tools.powershell` (`Ctrl+Shift+P`, category Tools) with the palette entry, and the window title
  "PowerShell – domain – DC – account" (plus "– Trusted mode" while on).
- strings in `Strings.resx` / `Strings.pl.resx` with `L.PowerShell.*` constants, following the wording rules of
  `v-interface-renewal.md` 4.5.

## 7. The window (part 4)

This is the plan; what was built is section 12.4 and it wins (the editor is a `UserControl` over a `TextBox` with a coloured layer, not a
`Control` with a template of its own; the engine's files are not published in a folder - the single file runs it; `Out-AdmGrid` opens a
grid window of its own instead of the main window's object list).

- `src/AdMng.App/PowerShell/PowerShellWindow.xaml(.cs)` – registered with `services.AddWindow<PowerShellWindowViewModel,
  PowerShellWindow>()` in `AppHost`, opened from `Services/DialogService.cs` (`ShowPowerShellAsync`), added to `SmokeTest` and a
  UI test. The window has the page header, the status line, the output area, the pending-plan area (collapsed), and the line
  editor with the Stop button.
- `PowerShellEditor` (a `Control` of our own, no new package): a text surface with the caret, selection, multi-line input,
  highlight runs drawn from the tokens, the completion popup (a `Popup` with a list), Up/Down history, Ctrl+R search, Ctrl+C
  (copy when there is a selection, otherwise Stop). Every `StaticResource` key it uses is proven by a script (the lesson of
  the Windows CI: a wrong key compiles and fails at run time); no frozen brush or pen built from a theme brush.
- The shortcut `Ctrl+Shift+P` and the menu entry *Tools → PowerShell…*; the descriptor is in `ShellViewModel.Palette.cs`
  (the command palette and the key binding come from the same descriptor).
- `PowerShellSessionFactory` (App) creates the engine session for the connection scope with the real lockdown probe and the
  window services; `WindowsPowerShellPolicyRegistry` reads the registry; both are registered scoped or as singletons that
  hold no connection.
- The Windows decisions (D1): the published single-file `AdMng.exe --smoke-test` creates a session and runs a script. If it
  fails, the build publishes the engine's files in a folder beside `AdMng.exe` (`PublishSingleFile` with the engine's
  assemblies and `Modules` excluded from the bundle) and the smoke test runs again; the ADR's consequence is updated with the
  result. The `powershell.config.json` that turns off the transcription and the module logging a user's file enabled is placed
  next to `System.Management.Automation.dll` by the publish step (script block logging it leaves alone, 4.2).
- Every consumer of the engine (the App, the tests) imports `src/AdMng.PowerShell/PowerShellEngineConsumer.props`, which
  excludes the non-OSI `sni.dll` packages (ADR-0010, "Package licenses").

## 8. File and class plan

This is the plan of the parts; the files that exist are in the records of 12.1-12.4 and the totals in 12.0 (the App's editor is `PowerShellEditor.xaml` and the Windows policy reader `WindowsPowerShellPolicySource`, for instance).

| Part | Project | Files (new unless marked) |
|---|---|---|
| 1 | docs | `docs/adr/0010-…`, `docs/en/adr/0010-…`, this note, `docs/dev/README.md`, the README pair (Track R "Decided"), `CLAUDE.md` layout rows |
| 1 | `AdMng.Core` | `PowerShell/PowerShellEnums.cs`, `PowerShellOutputItem.cs` (items), `PowerShellCompletion.cs`, `IPowerShellSession.cs` |
| 1 | `AdMng.PowerShell` | `AdMng.PowerShell.csproj`, `PowerShellEngineConsumer.props`, `PowerShellEnvironment.cs`, `PowerShellSession.cs`, `PowerShellSessionOptions.cs`, `IPowerShellLockdownProbe.cs`, `ErrorText.cs`, `Hosting/OutputSink.cs`, `WindowHost.cs`, `WindowHostUserInterface.cs`, `WindowHostRawUserInterface.cs`, `Commands/AdmCmdlet.cs`, `IAdmWindowContext.cs`, `AdmWindowContext.cs`, `AdmCommands.cs`, `GetAdmVersionCommand.cs`, `Guards/CommandGuards.cs`, `Input/PowerShellHistoryPolicy.cs`, `Input/PowerShellSyntax.cs`; `Directory.Packages.props`, `AdMng.slnx` (changed) |
| 1 | `tests/AdMng.PowerShell.Tests` | `PowerShellSessionTests`, `LanguageModeTests`, `HostRoutingTests`, `GuardTests`, `EnvironmentTests`, `HistoryPolicyTests`, `SyntaxTests`, `CompletionTests`, `CommandCatalogTests`, `PackageLicenseTests`, `NoFilesTests` (+ `ChildProcess`, `TempFolder`), `TestSupport`, `TestCommands` |
| 1 | `tests/AdMng.PowerShell.Probe` | `Program.cs` – the child process of the file tests (a console program, referenced by the tests, which start it) |
| 2 | `AdMng.PowerShell` | `Commands/Get*Command.cs`, `SearchAdmObjectCommand.cs`, `OpenAdmObjectCommand.cs`, `OutAdmGridCommand.cs`, `ExportAdmListCommand.cs`, `Directory/AdmFilterBuilder.cs`, `AdmObjectProjector.cs`, `AdmAttributeCompleter.cs`, `AdmDnCompleter.cs`, `Commands/IAdmWindowServices.cs` |
| 3 | `AdMng.Core` | `PowerShell/PowerShellGridTable.cs` (the plain table of `Out-AdmGrid` / `Export-AdmList`) |
| 3 | `AdMng.Presentation` | `PowerShell/PowerShellWindowViewModel.cs` (+ `.Run.cs`, `.Mode.cs`), `PowerShellInputModel.cs`, `PowerShellInputHistory.cs`, `PowerShellOutputDocument.cs`, `PowerShellDnLinkFinder.cs`, `PowerShellPromptState.cs` (+ the pending plan state), `PowerShellPolicies.cs`, `IPowerShellPolicyReader.cs` (+ `IPowerShellPolicySource`, `PowerShellPolicyKeys`), `PowerShellPolicyReader.cs`, `PowerShellPolicyNote.cs`, `IPowerShellSessionFactory.cs`, `IPowerShellWindowActions.cs`, `PowerShellServices.cs`, `PowerShellServiceCollectionExtensions.cs`, `Localization/L.PowerShell.cs`, `Dialogs/DialogViewModelFactory.cs` (`CreatePowerShell`), `ServiceCollectionExtensions.cs`, `Resources/Strings*.resx` (block "R1 PowerShell window") |
| 4 | `AdMng.App` | `PowerShell/PowerShellWindow.xaml(.cs)`, `PowerShellEditor.cs`, `PowerShellSessionFactory.cs`, `WindowsPowerShellPolicyRegistry.cs`, `AppHost.cs`, `Services/DialogService.cs`, `SmokeTest.cs` (changed), `AdMng.App.csproj` (reference), `tests/AdMng.App.UiTests/PowerShell*` |
| 5 | docs | `docs/POWERSHELL.md` + `docs/en/POWERSHELL.md` (the operator's guide: commands, the window, the policies, the trusted mode, what is on disk), `docs/releases/NEXT.md`, the readiness item (if small), the closing checks |

## 9. Tests per layer

### 9.1 `AdMng.PowerShell.Tests` (Linux and Windows)

- **`PowerShellSessionTests`** – a script runs and its output streams as text ending with the end item; variables stay between
  runs; one table header from the engine's formatting; object mode; a terminating error is `Failed`, a non-terminating one
  `Completed` with errors; the next run can start at once; a second run while one is active is refused; a spinning script is
  stopped (`StopAsync`, and the cancellation token, no sleep); a command that waits on `StopToken` ends at Stop; a run whose
  pipeline does not obey Stop is left after the grace period (a `FakeTimeProvider`, no real wait): it ends `Abandoned`, the
  next command runs at once in a new runspace with its own host and the window's commands, without the old variables and
  functions, in the trusted mode when it was on and constrained when a lockdown came meanwhile; the window's commands run on
  one background thread, also after a left run; dispose stops a run and throws on later runs, and during a run that ignores
  Stop it returns after the grace period; two sessions share no runspace; `OutputWidth` decides the wrapping.
- **`LanguageModeTests`** – ConstrainedLanguage by default and the language-mode error for a .NET call; no way of the window runs
  code in another language mode (4.7); the program's commands work in it; the switch to FullLanguage and a command typed after
  the switch back; refused under a fake lockdown (at the start and when it appears later); `Audit` does not block; refused while
  a run is active; a new session starts constrained after a trusted one.
- **`TrustedModeOffTests`** – turning the trusted mode off (PS-02, 12.10): eight ways to call what the trusted mode defined (a
  function, an alias, a script block in a variable, a member of `$Host` and of `$Host.UI.RawUI`, a command object) ran FullLanguage
  before and fail after it; the new runspace has the window's commands, width and a new host; turning on keeps the variables; the
  lockdown is asked again; refused while a command runs; the trusted runspace is closed; the new runspace failing and a dispose
  meanwhile.
- **`SessionEndingTests`** – `exit` (with and without a code, in a function, in a script block) ends the run with the code in its
  end item; `Clear-Host` and `cls` give a clear item in the order of the output, also in the trusted mode; a stopped run counts only
  an error the command wrote (PS-12, 12.10).
- **`HostRoutingTests`** – each stream arrives as text of its own stream; `Write-Host` colours without duplicates; progress;
  `Read-Host` (text and secret), a missing mandatory parameter, `-Confirm` (both answers) as prompt items with the answer
  going back; a cancelled prompt and a stopped run at a prompt end the run; `Get-Credential` is refused; no nested prompt; the
  host's name and culture.
- **`GuardTests`** – `Start-Job` and `Import-Module` are refused with the message and the script goes on; every way to a refused
  command meets the refusal (the name, the alias, a string, the command object of `Get-Command`, the module-qualified name, the
  background operator, `using module`); the refused names are functions; no profile and no PSReadLine; auto-loading off; the
  execution policy `Restricted` (Windows only).
- **`SessionCommandsTests`** – the exact command list of a new session (4.6), the same list in the code, every alias points at a
  command of the session, no script file runs (from the command line, a script block, a function, ForEach-Object) and there is no
  `function:` drive; on Windows a script file reached as a command object is refused by the authorization manager.
- **`EscapeTests`** – the probe scripts of the audit of R1, in ConstrainedLanguage and in the trusted mode: the `-Parallel`
  reflection chain to the directory session (the object survives, no file is written), the `-AsJob` variant, a job the trusted mode
  starts through the engine's cmdlet object ends with its command and when the window closes, `using module` of a script and of a
  binary module and `using assembly`, the background operator, every file writer and redirection, `Start-Transcript` with and
  without a path, the commands that run code elsewhere or load it, `$Host.PrivateData` and the audit's reflection chain, that
  `ForEach-Object -MemberName` and `New-Object` keep the language mode, that the red team's trusted-mode chains (12.7) are
  refused in the default mode, and that a script block the trusted mode left behind does not end the process when it fires after
  its window closed (child processes: the probe's `leftover-callback`, and its control without the program's set-up, which crashes
  and gets none of the runtime's crash dump settings - the probe's `environment` shows what a child got) while only that error is
  contained; a parallel job ended while the engine still opens its child's runspace (child process: the probe's `parallel-job-open`,
  which holds the runspace at that point, 12.12), a job of several children while the engine opens a later child's runspace and when
  a child ends by itself at the moment the job is stopped (the probe's `parallel-job-children`, its session clock moved on by an hour
  every 20 ms), a job of several children and a throttled one in the test process (the children that waited never start), and that
  the engine still has the members the session ends such a job with; the third red team's line nested too deeply for the parser is
  refused in both modes and the window runs the next command, and chains of operators too deep to walk - also the deepest that fit
  below the length limit - are refused without ending the process (child processes: the probe's `deep-input:<case>`, from a thread
  with 1 MB of stack, 12.9).
- **`RegexLimitTests`** – the process limits every regular expression without a limit of its own (4.2); a match that backtracks for
  hours ends as an error of its statement after 2 s, for each way of the language (`-match`, `-replace`, `-split`,
  `switch -regex`, `Select-String`, `[regex]::IsMatch`, `[regex]::new`), and the next command runs; a script catches the error;
  Stop during such matches ends the command itself (a fake clock that nobody advances: the grace period never passes); a
  `[regex]` with a limit of its own (`[timespan]::FromHours(1)` through `[regex]::new` and `[regex]::Matches`,
  `[regex]::InfiniteMatchTimeout`) outlasts Stop and is left, and the next command runs in a new runspace; a child process whose
  window left a match that never ends still exits (`left-run` of the probe program).
- **`InputGuardTests`** – what `InputGuard` refuses and what it must not (the call operator, strings, `&&`, `> $null`, `2>&1`), a
  line the parser gives up on, and the limits of `InputParser` on both sides.
- **`ScriptBlockLoggingTests`** – what the engine logs of twelve lines with the all-users file and the user's file it is given
  (D-B, 4.2; child processes: the probe's `script-block-logging:<all-users file>|<user's file>`): with an empty file and with the
  program's file the eight lines the engine marks (a word of its list inside a run of letters - `$properties`, the account name
  `jmarshall` -, the last letters of the text - `Get-AdmUser 'grace mit'` -, and the parser's marks: a member by a variable, an
  expression or an expandable string, a static member through a variable, `[type]`) are logged and the four others skipped
  (no word, members by constant names, a word of the list with a digit, `Get-AdmUser jkowalski`), with a file that sets the
  switch off none is, with the program's file and a user's file that says on all are. On Linux, which has no Group Policy, the
  setting must be `null`; only on Windows is the test skipped when the machine sets script block logging itself.
  `EngineBoundaryTests` pins the program's file (no `ScriptBlockLogging` section; transcription, module logging and the
  compatibility layer off).
- **`EnvironmentTests`** – the variables of 4.2 are set and visible to a script; no `PSHost.*` pipe exists (Windows
  `\\.\pipe\`, Unix the temp folder).
- **`NoFilesTests`** – the **"no files" test** and the measurement: the probe program (`AdMng.PowerShell.Probe`, a small console
  app the tests start as a child process, so that the environment is set before the runtime starts) with `HOME`,
  `LOCALAPPDATA`, `APPDATA`, `USERPROFILE`, `TEMP`, `TMP`, `TMPDIR` and `XDG_*` pointing at an empty folder and telemetry
  switched **on** in its environment runs two sessions, a script touching the formatter, module discovery, completion, syntax and
  the history policy, and the folder holds no file afterwards; the child reports the folders of its engine and the test asserts
  that it watches each (on Windows also a snapshot of the user's own folders of the engine; 12.1, "What could not be verified on
  Linux"); a **positive control** (an ordinary runspace of the SDK, both systems) leaves a file of the probe in the engine's cache
  folder and, where the engine sends telemetry, `telemetry.uuid` (12.14), and the test must see them; the opt-in **module analysis cache proof** (`ADMNG_POWERSHELL_SLOW_TESTS=1`,
  about 40 s; set in the Windows CI job since the audit). The probe is not an xunit run on purpose (the first version started this
  test assembly as the child and took 18.6 s on the Windows runner). Its `Get-Module -ListAvailable` fails at once in the window,
  whose command list does not have it; the first version ran it in an ordinary runspace and left it out on Windows, where a runner
  with hundreds of modules took 30 s to minutes.
- **`HistoryPolicyTests`**, **`SyntaxTests`**, **`CompletionTests`** – section 4.9 (the matrices are in the tests); the
  completion test has a mutation check (it fails when the file system matches are not dropped).
- **`CommandCatalogTests`** – section 4.8, and the context mechanism: `Get-AdmVersion` reports the fake session's domain, DC and
  account, follows the trusted mode, answers without a window, and a command that needs the window says so; the reader a command
  gets is not the session; two sessions give each command its own services; `$Host.PrivateData` is empty and no variable of the
  session holds the context, the reader or the session; every command class is either in the catalog or one of the internal
  replacements and the set-up command.
- **`PackageLicenseTests`** – the output has no `sni.dll` and its deps file names none.

### 9.2 Part 2 – the commands

Each command over `FakeDirectorySession` (the sample company, 1000-object variants): same objects and values as the list view
for 1000 objects, the escaping test `Łukasz (Kraków)*` (also `\`, `*`, `(`, `)`, NUL), nested membership with a cycle,
100,000 streamed results with a cancellation in the middle and no buffering of the whole set, the secret attributes masked, no
call outside the one session, `-Server` and `-Credential` do not exist.

### 9.3 Part 3 – `AdMng.Presentation.Tests` (Linux)

The view model against a fake session (and the real engine in a few tests): the status line during a run and on Stop, the
lasting policy note, the trusted-mode lasting note, turning the trusted mode off (`PowerShellWindowTrustedModeTests`: the
switching state, its failure, the lockdown of the new runspace), the reason of the switch in every state, history rules, completion state, Enter versus a new line, DN links, the
bounded scrollback, the policy reader over a fake registry (every combination of the two hives, the Windows PowerShell switch
and the two config files), `ConnectionLifetimes` (the new services are scoped).

### 9.4 Part 4 – Windows

`AdMng.App.UiTests`: the window opens from the menu and the shortcut and binds without errors (the binding-error listener), a
command runs and its text appears, Stop ends a long command, the policy note appears before the first command, the trusted
mode needs a confirmation, the window closes without a question when it is idle and with one while a write runs (none in R1);
the smoke test of the published `AdMng.exe` runs a script in the engine.

## 10. Completion criteria of R1

The criteria of the README that concern reads, plus the ones this design adds:

1. The read commands return the same objects and values as the program's windows (a comparison for 1000 lab objects) and go
   only to the window's DC; 100,000 results do not block the window, and the read can be cancelled at any moment.
2. A value with a parenthesis, an asterisk, a backslash or Polish characters (`Łukasz (Kraków)*`) in the `Search-AdmObject`
   parameters gives a correctly escaped filter (RFC 4515) – without extra results.
3. After the window is closed there is no command history, transcript or result on the workstation's disk (when no GPO
   turns on transcription, script block logging or module logging and the user's own `powershell.config.json` does not turn on
   script block logging; the engine's own warnings 4104 for script blocks it finds suspicious are in the event log where the
   PowerShell 7 event provider is registered, 4.2) – the **"no files" test** passes; with transcription turned on by a GPO, the
   window warns before the first command; the password does not appear in the history.
4. When a WDAC policy applies, the window works in ConstrainedLanguage mode and AD Manager's commands work; without it
   ConstrainedLanguage is the default and the trusted mode needs a confirmation and is shown.
5. No write command exists, no `AD:` drive and no RSAT: the **command list test** passes.
6. No PSReadLine, no profile, an allow-list of commands without jobs, parallel runspaces or file writers (4.6), `Import-Module`
   and `using module` limited to the (empty) list, no script file runs, telemetry and the diagnostics channel off, no module
   analysis cache.
7. The window opens from *Tools → PowerShell…*, the command palette and **Ctrl+Shift+P**, belongs to its connection, shows the
   domain, the DC and the account, and its runspace is disposed with the connection's scope.
8. The published single-file `AdMng.exe` runs the engine (or the fallback layout does), proven by the smoke test in Windows CI.

The criteria about writes and RSAT in the README (the change plan, translated RSAT commands, `Get-ADUser` without `-Server`,
`AD:` writes) are criteria of R2 and R3.

## 11. Open points

What was open when the design was written and what became of it (the state at the end of R1, with the owner's questions, is in 12.0):

- Whether the engine reads `PowerShellPolicies` from `powershell.config.json` on Windows next to the registry policies, and where `$PSHOME` is in the single-file layout: `$PSHOME` is the program folder (the smoke test in Windows CI
  shows it); which policies the engine takes, and from where, was settled after the audit from its decompiled code (W-02: per policy and per hive, the switch in each policy's own key, the JSON policies after every hive - `PowerShellPolicyReader` mirrors it); a comparison with what the engine records under a real GPO is **still to be made** on such a machine.
- `NUL\ModuleAnalysisCache` as the analysis cache path on Windows: the opt-in proof was run on Linux only at first; the Windows CI job runs it since the audit, and it passed there from the CI of 151aff46 on.
- The stage S readiness item for the policies and the language mode: **not built**; the reader and the note are ready to be reused.
- The two R3 questions of the README ("To be agreed with the team"): the list of translated RSAT commands and the window's credentials for the `ActiveDirectory` module - **open**.
- New at the end of R1: the README promises that the window rejects `Start-Transcript` without `-Path`; nothing in R1 did. Built after
  the audit of R1 (4.6).

## 12. R1 as built

Section 12.0 reads the whole of R1 as one text. Sections 12.1 to 12.4 are the records the four parts wrote when they were done (the source comments cite them by number), and 12.5 records part 5. Where a
record says "not yet" or ends with a question, 12.0 says what became of it. 12.6 records the repair of the engine's security composition after the audit of R1, 12.7, 12.8 and 12.9 what three red teams found after it, 12.10, 12.11 and 12.13 the fixes of further findings of the audit, and 12.12 the fix of a parallel job that ended the process in CI; where they differ from 12.0-12.5, they win. The blocks win over the design above.

### 12.0 R1 in one text

R1 is built. **Nobody has seen the window on a screen.** Sections 12.1 to 12.4 are the records the four parts wrote when they were
done (the source comments cite them by number); this block is the whole in one text: what exists, how it works, the decisions that
bind R2 and R3, the counts, what could not be verified and what is open. Where a record below says "not yet" or ends with a
question, this block says what became of it.

**What exists.** *Tools > PowerShell...*, **Ctrl+Shift+P** and the command palette entry `tools.powershell` open one PowerShell window
per connection window (a singleton per scope, key `tools:powershell`). It runs an embedded PowerShell 7.6.10
(`Microsoft.PowerShell.SDK`; 7.7 targets .NET 11) in one runspace of its own, in ConstrainedLanguage, with ten commands of AD Manager, an
allow-list of the engine's commands (4.6, 12.6) and our own line editor. The ten commands are all reads or window actions: `Get-AdmVersion`, `Get-AdmObject`, `Get-AdmUser`, `Get-AdmGroup`,
`Get-AdmComputer`, `Get-AdmGroupMember` (`-Recursive`, `-ExcludeGroups`), `Search-AdmObject`, `Out-AdmGrid`, `Open-AdmObject`,
`Export-AdmList`. The window has the status line with Stop, the notes that last (what Group Policy records, the trusted mode, the cut
scrollback), the output (a virtualised list with distinguished names as links), the empty place of the pending plan, the editor
(colours from the parser's tokens, a completion list, history in memory, Ctrl+R) with *Run* / *Clear*, and the *Trusted mode
(FullLanguage)* switch behind a confirmation. `Out-AdmGrid` opens a grid window of its own. The window reads the PowerShell policies of the
workstation when it opens (registry and the two `powershell.config.json` files) and says before the first command what they record.
The operator's guide is `docs/POWERSHELL.md` / `docs/en/POWERSHELL.md`; the window's first lines name it.

**The pieces and where they live** (files / lines added from the base of R1, `b02d5a03^`, to the end of part 4, `75b100af`; recounted by a script that leaves out the two
test-stabilisation commits of the orchestrator):

| Project | What it holds | Files / lines |
|---|---|---|
| `src/AdMng.PowerShell` (net10.0, App only) | the engine: `PowerShellSession`, `PowerShellEnvironment`, the host, the output channel, language mode, guards, history policy, syntax, completion; the ten commands, the reading code (`Reading/`), completers, default formats, MAML help, `AdmTableBuilder` | 46 / 5,429 |
| `src/AdMng.Core/PowerShell` | the contracts the window shares with the engine (`IPowerShellSession`, output items, completion, syntax spans, `PowerShellGridTable`); `LdapFilter.Pattern` and `EqualBinary` in `Core/Ldap` | 5 / 345 (+41) |
| `src/AdMng.Presentation/PowerShell` | the window's view model, editor model, history, scrollback, DN link finder, the policy model and its note, the grid's view model, the window actions, the scoped services | 18 / 3,299 |
| `src/AdMng.App/PowerShell` | `PowerShellWindow`, `PowerShellEditor`, `PowerShellGridWindow`, `PowerShellRuns`, `StateCheckBox`, `PowerShellSessionFactory`, `WindowsPowerShellPolicySource` | 11 / 1,619 |
| the rest of the program | registrations (`AppHost`), `SmokeTest.PowerShell.cs`, `powershell.config.json`, the menu and palette entry, `Ad.Font.Mono`, `IDialogService` members, strings (`L.PowerShell.cs`, both resx), the dialog factory | 3 new + 15 changed / +1,019 |
| tests | `AdMng.PowerShell.Tests` (24 files, 3,685 lines), `AdMng.PowerShell.Probe` (the child process of the "no files" test, 2 / 176), `AdMng.Presentation.Tests` (22 / 4,186), `AdMng.Core.Tests` (2 / 102), `AdMng.Directory.IntegrationTests` (lab, 2 / 213), `AdMng.App.UiTests` (8 / 779) | 60 / 9,141 |

All of parts 1 to 4: 171 files in 28 commits (26 "R1:" commits and 2 test-stabilisation commits of the orchestrator), 135 new and 36 changed, +22,471 / -62 lines. Of these 6 files
(+1,412 lines) are documentation - the ADR pair, this note, the dev index, `multi-window.md`, the release draft - and 6 more are the README pair, `CLAUDE.md`, `Directory.Packages.props`, `AdMng.slnx` and the CI
workflow (+166 / -60). Part 5 added the guide pair, the status of Track R in the README pair, the roadmap and the goals, the measured sizes in the ADR pair and the release draft, the guide's name in the window's first
lines (one string changed in each language, one test) and this block.

**How a command runs.** The editor model asks the engine (through `IPowerShellSession`, which names no PowerShell type) to tokenize,
complete, decide whether the input is complete and whether a line may enter the history. *Enter* on a complete input makes the view
model begin an operation on the status line (`OperationWrites.Other`) and call `RunAsync(script)`. On the session's pipeline thread the script
runs as `script | Out-Default`, error stream merged, in the global scope of the window's runspace; AD Manager's commands read through the
`ReadOnlyDirectoryReader` of the window's session (async, one object at a time, the pipeline's `StopToken`), the host turns everything written
into items of a bounded channel (4096), `PumpAsync` hands them to the UI thread in batches, and `PowerShellOutputDocument` keeps the last 20,000 lines
and finds the distinguished names. `Out-AdmGrid`, `Open-AdmObject` and `Export-AdmList` call back into the window through `IAdmWindowServices` (an adapter over
`PowerShellWindowActions`) on the pipeline thread and wait there. Stop (button, Ctrl+C, closing the window, the token) goes the other way:
`PowerShell.Stop` -> `StopProcessing` -> the stop token -> the LDAP request, with a 5 s grace period after which the session leaves the command and goes on in a new runspace. Section 4.10
has the picture.

**Decisions that bind R2 and R3** (each is detailed in the part that made it):

1. *Contracts in Core, the engine App-only* (12.1 decision 1): `AdMng.Presentation` never references an engine project, the Watchdog package never has one
   (`EngineBoundaryTests` in source, a CI step on the publish output), and every consumer of the engine imports `PowerShellEngineConsumer.props` (the non-OSI `sni` packages).
2. *The window context reaches a command through the window registry of its runspace* (12.6; until the audit through the host's `PrivateData`, 12.1 decision 2) and holds a
   `ReadOnlyDirectoryReader` (12.1 decision 3). A command cannot write or reach the connector's credential. **R2's writes must come to a command the same way - a separate object of the context
   that adds operations to the window's pending plan - never the session.**
3. *The catalog is the one list* (`AdmCommands.Catalog`); `CommandCatalogTests` pins the ten names and allows the verbs Get, Search, Out, Open and Export, and the same test checks that
   there is no `AD:` drive and no `*-AD*` command. R2 changes the list and the verb test together; R3 flips the `AD:` assertion. `PowerShellRulesTests` pins that the window's presentation code names no
   write API and that only two files write (the grid's export and the window actions' export).
4. *The commands do not leave the pipeline thread* (`AdmCmdlet.Wait` / `WriteAll`): any R2 command that waits for the window (a confirmation) does it the same way and passes the stop token.
5. *The session is an allow-list* (4.6, 12.6): `CommandGuards` is the one list of its commands, pinned by `SessionCommandsTests`; refusals are functions over cmdlets that are not in the
   session; what bypasses a command name (`using`, `&`, `>`) is refused from the text; no script file runs; jobs end with their command. `CommandGuards.AllowedModules` is **empty** and R3
   fills it, replaces the `Import-Module` refusal by a command that checks it and decides what `InputGuard` does with `using module` of an allowed module. `Get-Credential` is refused and R1
   creates no `PSCredential`: R3's credential for the `ActiveDirectory` module is a decision the README still lists. A new command of R2 or R3 goes into the catalog or the allow-list, and
   the list in the test changes with it.
6. *The formatted mode is text* (12.1 decision 4): the window shows the engine's formatting, so it has lines, not objects; the objects are real up to `Out-Default`. A count of objects needs a
   field on `PowerShellRunEndItem` (open point below).
7. *Read semantics R3 maps to* (12.2): identities are classified, the type of the command is part of the filter, values are typed by the program's own formatter, secrets are masked as the Attribute
   Editor does and `Search-AdmObject` refuses to compare them, nested membership is the program's own query and **includes the nested groups** (`-ExcludeGroups` is what the AD module's `-Recursive`
   maps to), every filter is built by `AdmFilterBuilder` from `LdapFilter` (rule 3).
8. *Status line, close question and notes* (12.3): a command is an operation with `OperationWrites.Other` (R2 sets `writes:` where the plan executes); the policy and trusted-mode notes are
   one method (`UpdateLasting`) and a command's result covers them until it is closed; the policy note is in four places (status line, layout, first lines, the log copy as a mask).
9. *The reserved place of the plan* is `PowerShellPendingPlanState` (`PendingPlan.IsVisible`, a collapsed `Border` named `PendingPlanArea` in `PowerShellWindow.xaml`): R2 fills it without changing the layout.
10. *The editor is a `TextBox` that draws no text over a `TextBlock` that draws it in colours* (12.4 decision 2); newlines are `\r\n` in the box and `\n` in the model (`PowerShellEditorText`).
11. *`Out-AdmGrid` is a window of its own* (12.4 decision 8), built from the object list's column, sort and density pieces; it is not `ObjectListViewModel`.
12. *Published layout* (12.4 decision 1, ADR-0010): the single-file `AdMng.exe` runs the engine (no folder fallback was needed); `powershell.config.json` and `AdMng.PowerShell.dll-Help.xml` sit beside it
    (`ExcludeFromSingleFile`); the engine falls back to the program folder as `$PSHOME`; the help file is found through `AppContext.BaseDirectory`.
13. *Nothing on disk* (12.1, 4.2): the engine's environment is set before its first use and a probe program shows that no file appears where its engine keeps files (on Linux observed; on Windows
    not yet seen to pass; there the engine's AppLocker check writes and deletes two test files in `%TEMP%`); the only writes of the window are operator-requested exports.
14. *A test seam:* `PowerShellSessionOptions.ExtraCommands` (internal) registers test-only commands outside the catalog.

**Counts** (recounted by a script that reads the catalog, the git diff of the tests and both resx files; results of a local run on Linux):

| What | Number |
|---|---|
| Commands in the catalog | 10: 6 `Get-` (`Get-AdmVersion` among them), `Search-`, `Out-`, `Open-`, `Export-`; no write verb |
| Strings | 92 keys in each of `Strings.resx` and `Strings.pl.resx`: 75 `PowerShell.*`, 16 `PowerShellGrid.*`, 1 `Commands.PowerShell`; 91 constants in `L.PowerShell.cs` |
| Engine tests (`AdMng.PowerShell.Tests`, Linux and Windows) | 239 at the end of R1: 237 passed, 2 skipped by design on each platform (Linux: the Windows-only execution-policy test and the opt-in 40 s cache proof; Windows: the Unix-only positive control and the same opt-in proof). 373 after the repair of 12.6: on Linux 370 passed and 3 skipped (the two above and the Windows-only authorization-manager test). 426 after 12.7 (+11 `RegexLimitTests`, +41 `HistoryPolicyTests` cases, +1 `EscapeTests`): on Linux 423 passed, the same 3 skipped. 461 after 12.8 (+25 `HistoryPolicyTests` cases, +4 `RegexLimitTests`, +4 `PowerShellSessionTests`, +2 `EscapeTests`): on Linux 458 passed, the same 3 skipped. 497 after 12.9 (+21 `HistoryPolicyTests` cases, +11 `InputGuardTests`, +3 `EscapeTests`, +1 `SyntaxTests`): on Linux 494 passed, the same 3 skipped. 523 after 12.10 (+16 `TrustedModeOffTests`, +10 `SessionEndingTests`). 611 after 12.11 (+45 `HistoryPolicyTests` cases, +8 `HostRoutingTests`, +7 `CommandTextTests`, +6 `PowerShellSessionTests`, +3 `CompletionTests`, the rest in the read and window command tests): on Linux 608 passed, the same 3 skipped. 617 after 12.12 (+6 `EscapeTests`): on Linux 614 passed, the same 3 skipped. 618 after 12.13 (+1 `ScriptBlockLoggingTests`; the test of the shipped file in `EngineBoundaryTests` changed, not added): on Linux 615 passed, the same 3 skipped |
| Presentation tests (Linux) | 253 in `AdMng.Presentation.Tests.PowerShell` (one of them is part 5's test that the window's first lines name the guide), +1 in `ShellViewModelTests`; the project has 3,920. 256 after 12.8 (+3 `PowerShellWindowRunTests`: a run the engine left); the project has 3,952 |
| Core tests (Linux) | 8 of `PowerShellGridTableTests`, 10 cases of `LdapFilter.Pattern` / `EqualBinary`; the project has 5,544 |
| Lab tests against Samba | 5 (`PowerShellReadLabTests`), skipped without `ADMNG_TEST_LDAP_*`, run by the CI lab job |
| UI tests (Windows only) | 25 test methods (+6 data rows): 20 of the window, 5 of the editor's parts; the lists of `XamlSourceTests`, `HostTests`, `DesignTokenTests` |
| Published app | `SmokeTest.PowerShell.cs`: a runspace, `1 + 1`, `Get-AdmVersion`, the ten commands, `Import-Module` refused, the help of a command, since 12.6 the number of the session's commands against the allow-list and the window's `ForEach-Object`, and since 12.7 the time limit of regular expressions (in effect, and a backtracking match caught); both windows rendered in both languages |
| Sizes (`-r win-x64 --self-contained -p:PublishSingleFile=true`, MB = 10^6 bytes) | `AdMng.exe` 143.5 -> 181.9 MB (+38.4 MB; CI prints 173.5, which is MiB); publish folder 164.7 -> 204.3 MB (+39.6 MB); CI artifact 68.8 -> 82.4 MB (+13.6 MB); the base is the commit of 0.16.0 |

**Checks at the end of R1.** `dotnet build AdMng.slnx -c Release`: 0 warnings, 0 errors. `dotnet test` of `AdMng.PowerShell.Tests`, `AdMng.Presentation.Tests` and `AdMng.Core.Tests`: all pass (numbers above).
`tools/release/release.sh check`, `python3 tools/icon/build-icon-set.py --check` and `python3 tools/docs/check_docs.py`: OK. The last Windows CI run before part 5 (run 43927187726, head `75b100af`) was green on every job, including
the published-app smoke test that starts the engine and the two CI steps that check the published layout (no `sni*.dll`, the config and help files present) and the Watchdog package (no engine).
*After the audit (W-12, DLR-3):* the smoke test asserted neither `$PSHOME` nor which config file is in effect, never ran the policy reads, and `release.yml` had none of the two layout checks while
its zip takes only the top-level files of the publish folder (no `runtimes\` sub folders) - a layout no test ran. Now the smoke test asserts that `$PSHOME` is the folder the program reads the
engine's configuration from (`PowerShellEnvironment.EngineHome`) and, in the single file, the folder of `AdMng.exe` with its `powershell.config.json`; it reads the policies once with
`WindowsPowerShellPolicySource` (must not throw) and, over a user's file simulated in memory that turns the three logs on, the program's file must decide each of them unless a Group Policy
does (since D-B the transcription and the module logging, and never script block logging, 12.13). `release.yml` runs the layout check after the publish, smoke-tests the **unpacked zip** (with the layout check of the files that ship) and checks the `admng-watchdog` .deb for the engine;
the CI's Linux package job runs that .deb check too. `IPowerShellWindowActions` is in the per-connection list of `HostTests`. The release workflow was not run (release 0.16.0 is on hold).

**What could not be verified, and why it should still be right.**

- *Nobody has seen the window.* The coloured layer under the transparent box (alignment with the caret), the selection and caret in every theme, High Contrast (the colours collapse to the text colour), the
  completion popup near the edge of the screen, 125-200 % scaling, the narrow widths of the grid window, the width of the output in characters with a scroll bar, a screen reader (the completion list is not announced).
  The UI tests drive the real window over the real engine and a fake directory and found one binding error; a person has not looked.
- *Policies under a real GPO.* The registry keys and the rules come from the engine's decompiled code (`Utils.GetPolicySetting`, audit W-02) and the documented templates, and the reader's rules are tested on Linux;
  the Windows source is tested against a throwaway key of the current user (`WindowsPowerShellPolicySourceTests`, Windows CI). Whether the note matches what the engine records under a real GPO has not been seen.
- *WDAC / AppLocker.* The lockdown probe is `SystemPolicy.GetSystemLockdownPolicy()` and its mapping is tested with a fake; the engine under a real lockdown has not been run.
- *`PSModuleAnalysisCachePath` = `NUL\ModuleAnalysisCache` on Windows* (a relative path there, 4.2). The opt-in 40 s proof (`ADMNG_POWERSHELL_SLOW_TESTS=1`) was run on Linux only at first; the Windows CI job sets it since the audit,
  and it passed there from the CI of 151aff46 on; auto-loading is off, which stops the cache being written by discovery, and the path is the second line of defence.
- *The "no files" test on Windows.* Its first Windows run (CI of 5cfead9c) failed its positive control, which showed that the test had not watched where the child's known-folder answers point; the
  test now asks the child's engine for its folders and asserts that it watches each (12.1, "What could not be verified on Linux"); the window's test passed on Windows from the CI of 151aff46 on, and its positive control expected a file that the Windows engine does not write on the runner (12.14).
- *A real Windows Server AD.* The commands ran over `FakeDirectorySession` and the Samba lab; the lab has no Windows-only behaviour (a hidden attribute, ranged retrieval differences, the DC's computed `msDS-User-Account-Control-Computed`).
- *Running `AdMng.exe` as a person would.* The CI starts it with `--smoke-test` and the UI tests; the first runspace took 523-791 ms there and the second 9-12 ms.

**Deviations from the owner's brief** (the owner delegated the decisions D1-D9; these are the places where the build differs from the text, with the reason):

1. *No object items in the formatted mode and no "N objects"* - the window shows the engine's formatted text (the console model); `PowerShellRunEndItem` carries `HadErrors`, not a count (12.1, 12.3).
2. *"All MIT" became "all OSI-approved"* - four `sni` packages (Microsoft .NET Library licence) are excluded with a test; a script in the trusted mode that uses `System.Data.SqlClient` finds no `sni.dll` (ADR-0010).
3. *The size*: `AdMng.exe` grew by +38.4 MB uncompressed (the README's 19 / 54 MB were estimates; an empty program measured +17.0 / +48.4 MB, part 1).
4. *`Out-AdmGrid` opens a window of its own*, not the program's object list (D6; 12.4 decision 8).
5. *The policy types are in Presentation*, not Core (12.3 decision 1); the raw reader interface is `IPowerShellPolicySource`.
6. *A weaker script-file guard on Linux* (12.6): a script file called by name or path is not found there either, but the engine asks no authorization manager on Linux, so one obtained
   as a command object from `Get-Command` inside a script block runs (the window is Windows only).
7. *`Get-AdmGroupMember -Recursive` includes the nested groups* (the program's *All (nested)* list) instead of the leaves `Get-ADGroupMember -Recursive` returns; `-ExcludeGroups` gives that.
8. *The operator-readable secrets are shown* (the clear-text LAPS password, a BitLocker recovery password): "as the Attribute Editor" was read literally; the guide and the policy note warn.
9. *The editor is a `UserControl`* with a `TextBox` for editing (12.4 deviation 2), and the monospace font is a new design token `Ad.Font.Mono`.
10. *Extra commands and parameters beyond D4:* `Get-AdmVersion`, `-ExcludeGroups`, `-ObjectClass`, `-Match`, the identity forms `<GUID=...>` and `<SID=...>`.
11. *Parts of the README's "Security and data" are promises of later stages*: the window rejecting `Start-Transcript` without `-Path` was not built in R1; it was built after the audit (12.6).

**Open for R2** (writes through the change plan):

- the write commands (`Set-AdmObject`, `New-AdmUser`, ... the names the README reserves) as commands of the catalog that add operations to the window's pending plan, with the object of the context that carries them (decision 2);
- the panel in `PendingPlanArea` (count, *Preview...*, *Execute*, *Clear*) and `Show-AdmPlan`, `Clear-AdmPlan`, `Invoke-AdmPlan` (the last only opens the preview); execution through `IChangePlanner` / `IBatchExecutor` with audit, report, retry and rollback, and `writes:` set on the status line;
- passwords in write commands only as `SecureString` or generated by the program; a warning before `-AsPlainText` (the history already refuses such a line);
- copying an operation from the history of equivalent PowerShell commands into the window;
- `CommandCatalogTests` (names, verbs), `PowerShellRulesTests` (no write API in the presentation code) and `EngineBoundaryTests` change together with the commands.

**Open for R3** (RSAT modules):

- `CommandGuards.AllowedModules` and the `Import-Module` command; the `ActiveDirectory` and `GroupPolicy` modules pointed at the window's DC (`$PSDefaultParameterValues`), the read-only `AD:` drive, the translated write commands and the refusals;
- the two README questions: which RSAT write commands to translate, and the window's credential for the `ActiveDirectory` module (a `PSCredential` in a script's reach reveals the password in every language mode);
- ADWS exists only on Windows Server (the Samba lab has none), the `GroupPolicy` module and the Windows PowerShell compatibility layer (`powershell.config.json` already sets `DisableImplicitWinCompat`);
- snippets saved in the settings (only code typed by the operator); track C reuses the same engine.

**Open in R1 itself** (small, none blocks R2):

- the stage S readiness item for the PowerShell policies and the language mode: not built; `PowerShellPolicyReader` over `WindowsPowerShellPolicySource` and `PowerShellPolicyNotes` are ready to be reused, and it is a new row with its strings and a test;
- "N objects" in the status line (an engine count), `LockedOut` with the domain's lockout duration, members by primary group in `Get-AdmGroupMember`;
- the completion list is not announced to a screen reader; `Out-AdmGrid` has no `-PassThru` / `-Wait`; whether the grid should become a tab of the main window;
- the Windows checks listed above (policies under a real GPO, WDAC, the `NUL` cache path);

**Questions that remain for the owner.**

1. Should a person look at the window on Windows (light, dark, High Contrast, 125-200 % scaling, a screen reader) before it is announced in a release? The release draft says nobody has.
2. Is an all-users `powershell.config.json` beside `AdMng.exe` acceptable to the operators' security teams? It turns off the transcription and the module logging that a user's file turned on; a computer policy still wins and the window says so. Script block logging it leaves to Windows and Group Policy (D-B, 12.13).
3. Should the behaviour under a real WDAC / AppLocker and a real recording GPO get a pilot on a managed machine before R2 adds writes?
4. Should `sni.dll` be offered in some OSI-compatible way for trusted-mode scripts that need SqlClient, or is the documented limit enough?
5. Should the operator-readable secrets (LAPS, BitLocker) be masked in the window too (one line in `AdmValueConverter`; the Attribute Editor would then differ), or is the warning enough?
6. `Start-Transcript` without `-Path` is refused since 12.6, and with `-Path` it writes the named file (the README: "transcription only to an explicitly specified file"), while every
   other file writer except `Export-AdmList` is gone from the window. Should transcription to a named file stay, or be refused like the others?
7. The flaky connector timeout and DNS tests of the Windows runner (fixed twice in the CI history by widening bounds) may need a shared helper.

### 12.1 Part 1 – decisions and the engine

**What exists.** The decisions D1–D9 on paper (ADR-0010 in Polish and English, this note, the README pair's "Decided" section,
the index, the `CLAUDE.md` layout rows) and the engine: `src/AdMng.PowerShell` (`PowerShellSession` with the custom host, the
output channel and the stop logic; `PowerShellEnvironment`; the lockdown probe; `AdmCmdlet`, `AdmWindowContext`, `AdmCommands`,
`Get-AdmVersion`; `CommandGuards`; `PowerShellHistoryPolicy`; `PowerShellSyntax`; completion), the contracts in
`src/AdMng.Core/PowerShell` (`IPowerShellSession`, the output items, completion, syntax spans, the enums), the package
(`Microsoft.PowerShell.SDK` 7.6.10 in `Directory.Packages.props`, with the four excluded `sni` packages and
`PowerShellEngineConsumer.props`) and `tests/AdMng.PowerShell.Tests` (128 tests: 126 run, 2 skipped by design – the Windows
execution policy and the opt-in 40 s cache proof) with the child-process program `tests/AdMng.PowerShell.Probe`. At that time nothing was visible to the operator (`AdMng.App` did not reference the
engine until part 4, and there was no release note); 12.0 has the whole.

**Decisions of part 1 that the later parts follow** (each with its reason):

1. *Contracts in Core, the engine is App-only.* `AdMng.Presentation` never references an engine project (the `AdMng.Remote`
   pattern), so the view model is tested against fakes of `IPowerShellSession` and, in a few tests, the real engine. The Core
   types name no PowerShell type (`PowerShellObjectItem.Value` is an `object`).
2. *(Replaced after the audit of R1 by the window registry, 12.6.)* *The window context reaches a command through the host's `PrivateData`* – `PSCmdlet.Host` is the engine's wrapper, not our
   host, so `Host as WindowHost` is null (found by running `Get-AdmVersion`); the wrapper does hand out `PrivateData`. The
   object has no public member (a test asserts `Get-Member` shows none). No static registry, no variable.
3. *The context holds a `ReadOnlyDirectoryReader`* (built by `AdmWindowContext`, so the engine decides it, not the caller):
   there is no write method and no cast back to the session.
4. *The formatted mode is `script | Out-Default` with the error stream merged into it*, like the console host; text arrives
   through the host's `Write*` methods. The information stream needs no item of its own (`Write-Information` and `Write-Host`
   both arrive as Output text; overriding `WriteInformation` duplicated every `Write-Host`).
5. *Object mode* (`RunObjectsAsync`) is on the session class, not on the Core interface: it serves the tests of part 2 and
   programmatic callers; the window never needs it.
6. *Prompts are items of the same stream*, answered through the item; a stop or a `Cancel` ends the pipeline like Ctrl+C.
   `Get-Credential` is refused in R1.
7. *(Extended after the audit of R1 to the allow-list of 4.6 and 12.6; the refusals keep this form.)* *Guards are functions that write a non-terminating error with the command's name* (`Start-Job: …`), and the cmdlet is
   removed from the session state so that the module-qualified name cannot bypass it. A `throw` printed the guard's own source
   line; `-ErrorAction Stop` printed "The running command stopped because …"; `Write-Error -CategoryActivity <name>` printed
   exactly `Start-Job: <message>`.
8. *`InitialSessionState.ExecutionPolicy` only on Windows* (it throws `PlatformNotSupportedException` elsewhere).
9. *`PSModuleAutoLoadingPreference = None` and `PSModuleAnalysisCachePath` = a file inside the null device*, both needed (4.2).
10. *A bounded output channel (4096) with a grace period on Stop*, so that neither a flood nor a command stuck in .NET code can
    freeze the window.
11. *Every consumer of the engine imports `PowerShellEngineConsumer.props`* – excluding the `sni` packages in the engine's own
    project does not flow to the consumers (tested: `sni.dll` was in the consumer's output until the consumer excluded it
    too); a single-file win-x64 publish of a consumer has no `sni.dll`.

**Findings with the real engine** (Linux, Microsoft.PowerShell.SDK 7.6.10, .NET 10.0.115):

| Finding | Where it matters |
|---|---|
| The first runspace takes about 0.43–0.45 s, the second about 10–14 ms (child process, three runs: 454/429/423 ms and 14/11/10 ms); managed memory after the first is about 1.8 MB, the working set of the process grows by about 52 MB (assemblies and JIT; 14 MB when the engine's assemblies were already loaded). | Part 4: "Starting PowerShell…" is shown once; opening a second window is cheap. |
| A single-file win-x64 self-contained publish (cross-published on Linux, not run): an empty app is 37.6 MB compressed / 73.5 MB plain, the same with the engine is 54.6 MB / 122.0 MB – **+17.0 MB / +48.4 MB** (the README says about 19 / 54). The publish folder is **not** single: next to the exe go `pwrshplugin.dll`, `PowerShell.Core.Instrumentation.dll`, `getfilesiginforedist.dll`, `microsoft.management.infrastructure.native.unmanaged.dll`, `build.manifest(.sig)` and `runtimes/{win,unix}/lib/net10.0/Modules/*` (the module manifests and format files). | Part 4 decides D1: whether the engine finds `$PSHOME/Modules` there. |
| Telemetry writes `telemetry.uuid` at the first command, not at open; opt-out env stops it. | 4.2 |
| User `powershell.config.json` can switch transcription on; the all-users file in `$PSHOME` wins. | D7 reader; part 4 publish layout. |
| `[pscustomobject]@{}` and any method call on a non-core type fail in ConstrainedLanguage in 7.6.10. | The guide of part 5. |
| `Get-Process`, `Get-ChildItem`, `Format-*`, `Out-String`, `Select-Object` calculated properties, `New-Object PSObject -Property` work in ConstrainedLanguage. | The guide. |

**Checks run.** Linux: `dotnet build AdMng.slnx -c Release` – 0 warnings, 0 errors; `dotnet test tests/AdMng.PowerShell.Tests` –
126 passed, 2 skipped (128); the opt-in cache proof passed (41 s: the control's cache file appeared, the window's session wrote
none); a mutation check (the file-completion filter removed) made the completion test fail; `release.sh check`,
`build-icon-set.py --check` and `check_docs.py` – OK. **The first Windows CI run** of the engine tests (128 passed, 2 skipped,
2 failed) found two mistakes of the tests, not of the engine: the pipe test listed `\\.\pipe\PSHost.*` of every process on
the runner (the `pwsh` that runs the CI step has one) – it now counts only the pipes with its own process id – and the
PSReadLine test counted the modules *available* on disk (the runner has PSReadLine installed: listing it is fine, loading it is
what is refused). The same run showed that the "no files" test started the test assembly as its child process and took 18.6 s (the slowest test of
the whole run); with the small `AdMng.PowerShell.Probe` program it still took 29.5 s in one run and timed out at 120 s in another
run of the same commit: the cause is `Get-Module -ListAvailable`, which on a Windows runner analyses the hundreds of modules of
the image (and writes to folders the test cannot watch anyway), so the probe skips that listing on Windows. The engine tests
themselves (session, host, language mode, guards, completion, catalog, `sni`, the "no files" child) passed on Windows at their
first run.
The result of the last Windows run is recorded in the report of the part.

**What could not be verified on Linux, and why it should still be right.**

- *Windows behaviour of the engine setup:* `PSModuleAnalysisCachePath=NUL\ModuleAnalysisCache` (the engine swallows an unwritable path on Linux; the
  proof test runs in the Windows CI job since the audit, `ADMNG_POWERSHELL_SLOW_TESTS=1`, and passed there from the CI of 151aff46 on), `InitialSessionState.ExecutionPolicy =
  Restricted` and whether it is the process environment variable `PSExecutionPolicyPreference` (a Windows-only test asserts
  `Get-ExecutionPolicy` is `Restricted`), the STA apartment with `ReuseThread`, the absence of `\\.\pipe\PSHost.*` (the Linux run
  cannot show the pipe exists in the first place – a plain runspace creates none there; the Windows run is the proof), and the
  real `SystemPolicy` probe under WDAC/AppLocker (the engine's own behaviour under lockdown is the README's "to be confirmed on
  a machine with WDAC").
- *The "no files" test on Windows* only watched what an environment variable moves (`TEMP`, `TMP`); the engine's files there go
  to known folders (`%LOCALAPPDATA%`), which a variable does not redirect, and its positive control was skipped - so the Windows
  run passed whatever the engine did (audit TI-1). *After the audit:* the child reports where its engine keeps files of its own -
  the engine's `Platform.CacheDirectory` (`telemetry.uuid`, the module analysis cache, startup data) and `Platform.ConfigDirectory`
  (the user's `powershell.config.json`, profiles, modules), read from the engine itself, on Windows also Windows PowerShell's
  `%LOCALAPPDATA%\Microsoft\Windows\PowerShell` (its module analysis cache), `%APPDATA%\Microsoft\Windows\PowerShell`
  (PSReadLine) and `Documents\WindowsPowerShell`, found as the engine finds a folder (`DoNotVerify`), and the temporary folder
  (`tests/AdMng.PowerShell.Probe/EngineFolders.cs`) - and the test asserts that it watches each of them: below the child's folder,
  or, on Windows, in a snapshot (length, time written) of the user's own folders of the same kinds taken before and after the run,
  which must show no new or changed file; the temporary folder must be the child's. A `FileSystemWatcher` on the child's folder
  catches a file made and deleted during the run, and on Windows the only ones allowed are the two
  `__PSScriptPolicyTest_<random>.ps1` / `.psm1` of the engine's AppLocker check (`SystemPolicy.GetAppLockerPolicy`, decompiled
  7.6.10: written with a comment line and deleted at once, once per process; audit W-11). The positive control is the same on both
  systems: an ordinary runspace of the SDK, telemetry on, whose first command writes the engine's `telemetry.uuid` into the engine's
  cache folder when the engine sends telemetry (on Windows only when Windows' diagnostic data setting allows it too, 12.14), where the
  probe also writes a file of its own; what was written must be seen where the test watches (`telemetry.uuid` only when it is new
  there), and the test removes what the control made in a real folder. The first Windows run of the control (CI of
  5cfead9c) failed: it wrote its file into the folder `[Environment]::GetFolderPath('LocalApplicationData')` named in the child,
  and the snapshot of the user's real folder did not see it - so the child's variables do move at least that answer of the
  known-folder API (probably through `USERPROFILE`, or .NET's fallback to `LOCALAPPDATA` when the folder is missing; not looked
  into further), and the test now assumes neither and asks the engine. The collection runs alone. On Linux every folder the child
  reports is below its folder; the watcher sees only the runtime's diagnostic socket and debug pipes, gone by the end, and what
  stays is one empty folder, `powershell` (the engine's cache folder, made at its start - a folder, no file). On Windows the
  window's test, with the snapshot of the user's folders, passed from the CI of 151aff46 on (the test prints the child's folders);
  the control failed there only on `telemetry.uuid` (12.14).
- *Whether the engine reads `PowerShellPolicies` from `powershell.config.json` on Windows* next to the registry; the D7 reader
  will read both, so the note is right either way.
- *The single-file layout:* where `$PSHOME` points and whether the four modules are found (part 4) - settled in 12.4 decision 1: the single file runs the engine and `$PSHOME` is the program folder.

**Deviations from the task text.** (1) No `Object` item in the formatted mode: results are shown as the engine's formatted
text (the console model); an `Object` item exists in object mode only (decision 5). (2) The end item carries `HadErrors`
instead of an error count. (3) The 85-package license check found four packages (`sni`) that are not OSI approved – excluded,
with a test, instead of "all MIT" (ADR-0010 "Package licenses"). (4) The README's 19 / 54 MB measured as +17 / +48 MB.
(5) `AdMng.Core/PowerShell` (the contracts) is new Core code, not named in the task. (6) The tests start a probe program of their own (`tests/AdMng.PowerShell.Probe`, one more project in the solution); a child
process is the only way to set `HOME` and the like before the runtime starts.

**Open questions for the next parts, and what became of them.** Where the grid takes its columns from: the grid is a window of its own built from the object list's column, sort and density pieces (12.4 decision 8). How the window shows a
stream's text: lines with a glyph column for errors, warnings and notes and the theme's colours (12.4 decision 6). Whether `Out-AdmGrid` posts a table through the context or calls a window service: a window service,
`IAdmWindowServices`, adapted over `PowerShellWindowActions` (12.4 decisions 9 and 10). Whether the stage S readiness item fits: not built (12.0).

### 12.2 Part 2 – the read commands

**What exists.** `Get-AdmObject`, `Get-AdmUser`, `Get-AdmGroup`, `Get-AdmComputer`, `Get-AdmGroupMember` and `Search-AdmObject`
in `src/AdMng.PowerShell/Commands/` (plain `PSCmdlet`s over `AdmReadCmdlet` / `AdmSearchCmdlet`, registered in `AdmCommands.Catalog`),
the reading code in `Reading/` (`AdmIdentity`, `AdmFilterBuilder`, `AdmProjection`, `AdmObjectProjector`, `AdmValueConverter`,
`AdmGroupMembers`, `AdmSearchPlan`, `AdmErrors`), the completers in `Completion/`, the default display in `Formats/AdmFormats.cs`, the
help in `Help/AdMng.PowerShell.dll-Help.xml`, and in Core `LdapFilter.Pattern` and `LdapFilter.EqualBinary`. `Out-AdmGrid`,
`Open-AdmObject` and `Export-AdmList` are part 4 (they need the window). No command writes (D9): the catalog test lists exactly
`Get-AdmVersion` and these six, its verb check allows only `Get`, `Search`, `Out`, `Open` and `Export`.

**Parameters** (all read through the window's reader only; there is no `-Server`, no `-Credential`, no `-ComputerName`):

| Command | Parameter sets and parameters (besides the common ones below) |
|---|---|
| `Get-AdmObject` | `Identity`: `-Identity` (DN, `objectGUID`, `<GUID=…>`, SID, `<SID=…>`, `sAMAccountName`, user principal name; from the pipeline by value or by the property `DistinguishedName`). `LdapFilter`: `-LdapFilter` (a ready filter; syntax-checked, a wrong one is a terminating error before any read). |
| `Get-AdmUser` | `Identity` as above, or `Filter`: `-Name -SamAccountName -UserPrincipalName -GivenName -Surname -DisplayName -Mail (EmailAddress) -Department -Title -Company -Description` (a `*` is the wildcard, everything else literal), `-Enabled <bool>`, `-LdapFilter` (added); all of them are ANDed. |
| `Get-AdmGroup` | `Identity` or `Filter`: `-Name -SamAccountName -Description`, `-GroupScope DomainLocal\|Global\|Universal`, `-GroupCategory Security\|Distribution`, `-LdapFilter`. |
| `Get-AdmComputer` | `Identity` (the account name with or without the `$`) or `Filter`: `-Name -SamAccountName -DnsHostName -OperatingSystem -Location -Description`, `-Enabled`, `-LdapFilter`. |
| `Get-AdmGroupMember` | `-Identity` (a group), `-Recursive`, `-ExcludeGroups`; no `-SearchBase`. |
| `Search-AdmObject` | `Name`: `-Name`; `Attribute`: `-Attribute`, `-Value` (both arrays: any attribute may match any value); `Filter`: `-Filter`; `-Match Exact\|StartsWith\|Contains\|EndsWith`, `-ObjectClass`. |
| common | `-Properties`, `-ResultPageSize` (1–10000, default 500), `-ResultSetSize` (0 = all); with a base: `-SearchBase`, `-SearchScope Base\|OneLevel\|Subtree` (alias `-Scope`; `Get-AdmGroupMember` has no base). |

**Decisions of part 2 that the later parts follow** (each with its reason):

1. *Every filter is built by `AdmFilterBuilder` from `LdapFilter`* (rule 3). Two primitives were missing and went into Core:
   `LdapFilter.Pattern(attribute, pattern)` (the star is the only wildcard, a run of stars is one, everything else is escaped) and
   `LdapFilter.EqualBinary` (a SID). The text parameters of `Get-AdmUser/Group/Computer` use `Pattern` (`Jan*` is what an
   administrator types); `Search-AdmObject` never does: its `-Value`/`-Name` are literal, `-Match` says how they are compared, so
   `Łukasz (Kraków)*\` finds exactly that object (the tests assert the text of the filter sent: `(name=Łukasz \28Kraków\29\2a\5c)`, the
   results on the fake and on Samba, and a mutation check: without escaping 10 of 20 tests fail). A `-LdapFilter` is wrapped and
   checked with `LdapFilter.Validate` (the check of the Find window and the saved views); its error is a terminating error of the command
   (`AdmInvalidLdapFilter`), so it is an ordinary error record the script can catch.
2. *An identity is classified, not guessed* (`AdmIdentity`): `<GUID=…>` / any GUID text → `objectGUID` (`EqualGuid`); `S-1-…` → `objectSid`
   (`EqualBinary`); a text with `=` that parses as a DN → read **where it is** (base scope; `-SearchBase` does not apply, as in the AD
   module); anything else → `sAMAccountName` (and the user principal name for users and objects; the name with `$` for computers). The type
   of the command is part of the filter, with the program's own indexed filters (`ObjectKind.ToLdapFilter`): `Get-AdmUser` is
   `sAMAccountType=805306368`, so unlike `Get-ADUser` it does not return computers. An identity that finds nothing (or finds an object of
   another type) is a non-terminating error `AdmObjectNotFound` (category ObjectNotFound) whose message holds the identity: `Cannot find a user
   with identity 'CN=…': it does not exist, or this account cannot see it.`
3. *Typed values by the rules of the program's formatter* (`AdmValueConverter`; the comparison test below keeps them equal):

   | Attribute | Property value |
   |---|---|
   | Boolean syntax | `bool` |
   | Integer, Enumeration; `userAccountControl`, `msDS-User-Account-Control-Computed`, `groupType` | `int` (also `userAccountControl` as a number: scripts compare bits) |
   | LargeInteger | `long` |
   | FILETIME attributes (`Attr.FileTimeAttributes`) | `DateTime` in local time (`TimeZoneInfo.Local`, what the program shows); `0` and `long.MaxValue` ("never") are `$null`; a text that is no number stays text |
   | GeneralizedTime / UTC time (also `whenCreated`, `whenChanged` without a schema entry) | `DateTime` in local time; year 1601 is `$null` |
   | `objectGUID`, any 16-byte attribute whose name ends in `guid` | `Guid` |
   | `objectSid`, `sIDHistory`, `tokenGroups…`, syntax SID | text `S-1-5-…` |
   | DN syntax, text syntaxes | `string` |
   | other binary of a known binary syntax (`logonHours`, `thumbnailPhoto`, certificates) | `byte[]` |
   | bytes of an attribute the schema does not know | text when they are UTF-8 text, otherwise **base64** (the only place base64 is used) |
   | multi-valued (schema: not single-valued; without a schema entry: more than one value) | an array, typed when every element has one type (`string[]`, `int[]`, `Guid[]`, `DateTime[]`), else `object[]`; none = an empty array |
   | single-valued without value | `$null` |
   | ranged (`member;range=0-1499`) | completed with ranged retrieval first |

   Beside the attributes every object carries `DistinguishedName`, `Name` (the `name` attribute, or the RDN) and `ObjectKind`
   (`ObjectKind` as text), accounts carry `Enabled`, `LockedOut` (users only; the DC's own `msDS-User-Account-Control-Computed` flag when it was
   returned, else `lockoutTime` > 0 – the same rule as `DirectoryObject.IsLockedOut`, which does not know the domain's lockout
   duration the list view applies) and `UserAccountControlFlags` (the ADSI names of the formatter's table), groups carry `GroupScope`,
   `GroupCategory` and `GroupTypeFlags`. These names are not LDAP attribute names, so they never collide with an attribute (property names
   are case-insensitive: `Name` and `name`, `DistinguishedName` and `distinguishedName` are one property).
4. *Type names by what the object is* (`ObjectKinds`): `AdMng.User` (also inetOrgPerson), `AdMng.Computer`, `AdMng.Group`, otherwise
   `AdMng.Object`; each object also has the base name `AdMng.Object`. There is **no `.Attributes` property** (the plan of section 5 had one): the
   typed values are the attributes, and a second copy of every value would double the memory of a 100,000-object result.
5. *Which attributes are asked for:* the defaults of the type (`AdmProjection.DefaultsOf`: the program's name, account and class
   attributes, a handful) plus `-Properties`; a name is canonicalised to the schema's spelling and one the schema does not know ends the
   command before any read (`AdmInvalidProperty`; with an empty schema nothing is checked); `*` asks for everything the account may read. A default
   attribute is a property only when the object has a value; one named in `-Properties` is always a property (`$null` or an empty array).
   `DistinguishedName` in `-Properties` is accepted and not requested.
6. *Secrets exactly as the Attribute Editor treats them* (`AttributeRowViewModel.IsMasked`): `SecretAttributes.IsNeverShown` (key
   material – `userPKCS12`, the roaming credentials, the BitLocker key package, managed and encrypted passwords, `unicodePwd`, binary secrets,
   `supplementalCredentials`) → the property is `SecretAttributes.Mask` as one string, whatever the number of values, and `$null` when there is no value (the editor says
   "not set"); the **operator-readable** secrets (the clear-text LAPS password, the BitLocker recovery password, a confidential text
   attribute) are shown, because the editor shows them (it copies them through the secret clipboard, which a script has no equivalent
   of). `Search-AdmObject` refuses to compare any secret attribute (`AdmSecretAttribute`): a search by the value of a LAPS password would be
   an oracle the program does not offer elsewhere.
7. *A mandatory attribute that did not come back is a permission error, once* (`AdmAttributeHidden`, category PermissionDenied): the DC returns an
   attribute the account may not read as if it were empty, and the schema (`GetMandatoryAttributes` of the object's classes) says which empty attributes are
   impossible – only the attributes the command asked for are checked (the defaults and `-Properties`, not what `*` happens to return), and the message
   names the attribute and the first object and says it is shown once. An attribute that is merely not set is no error. A read the DC refuses
   (`AccessDenied`) is `Access denied reading '<target>': the account of this window may not read it.`; an unreachable DC, a missing search
   base and the other directory errors are non-terminating error records with the target in the message (`AdmErrors.FromDirectory`).
8. *Nested membership is the program's own query, not a second engine.* The Members page's "All (nested)" list and the comparison window
   send `(memberOf:1.2.840.113556.1.4.1941:=<group>)` from the domain's default naming context; so does `-Recursive` (`AdmGroupMembers.NestedSearch`,
   with `Not(group filter)` appended for `-ExcludeGroups`). The DC evaluates the nesting and ends a cycle; the lab test makes a cycle on Samba.
   Unlike `Get-ADGroupMember -Recursive` the result **includes the nested groups** (what the program's list shows), so `-ExcludeGroups` is what R3
   maps the AD module's `-Recursive` to. The direct members are the values of `member` (`GetAllValuesAsync` completes ranges beyond 1500)
   looked up in batches of 100 DNs with one OR search each (the program's `DirectoryLookup`); members that cannot be found are counted in one
   warning with three DNs as examples. Members by primary group are not members here (as in the program's Members tab).
9. *Streaming, stop and no buffering* (CLAUDE.md rule 6): a command pulls the async search one object at a time **on the pipeline thread**
   (`AdmCmdlet.WriteAll`) with the pipeline's stop token, so a downstream `Select-Object -First 50` stops the read after 50 objects (a test with a reader that
   makes 100,000 objects lazily: 50 results, at most 60 made), the output channel of the session is the only buffer (4096 items: after 100 results
   taken and Stop, fewer than 4,710 of 100,000 were made, and the token that reached the directory read was cancelled) and Stop ends the read at the
   directory. A stop between two identities is a `PipelineStoppedException`, not an error text.
10. *The default display is data, registered in memory* (`AdmFormats`: `ExtendedTypeDefinition`s through `SessionStateFormatEntry`, no `.ps1xml`,
    no file): a table per type name – user `Name sAMAccountName Enabled DistinguishedName`, computer `Name Enabled operatingSystem DistinguishedName`,
    group `Name GroupScope GroupCategory DistinguishedName`, object `ObjectKind Name DistinguishedName` – with the other columns of a fixed width, so that
    the DN gets the rest of the line and is the **last** column (the window makes DNs links). At the default width of 120 a usual DN (up to about 70
    characters) is whole; a longer one is **cut with `…`** by the engine's table (a window that is wider, or `Format-List`, shows it whole). The DN finder of
    part 3 must not take the text before a `…` for a DN (the prefix of a DN is often another valid DN). The engine formats the first object's first
    type name as a table and every object whose first type name differs as a list block, so every object carries its command's type name first
    (audit ps-commands-2: `Get-AdmGroupMember -Recursive` was a table of users and an 11-line list per group, a managed service account broke the
    table of `Get-AdmComputer`): the commands of mixed kinds (`Get-AdmObject`, `Get-AdmGroupMember`, `Search-AdmObject`) put `AdMng.Object` first
    and the kind's name behind it, `Get-AdmUser`/`-Group`/`-Computer` their own (a gMSA from `Get-AdmComputer` is `AdMng.Computer`, its
    `ObjectKind` says what it is).
11. *The host removes the ANSI sequences of the engine's formatting* (a defect of part 1, found by reading the output of these commands): PowerShell 7.2+
    decorates table headers, list labels and errors with `ESC[32;1m…` and leaves it to the host; `WindowHostUserInterface` now passes every text through
    `PSHostUserInterface.GetOutputString(text, supportsVirtualTerminal: false)`. Without it the window would have shown the escape characters as
    boxes. (`$PSStyle.OutputRendering = PlainText` was tried first and does not remove them.)
12. *Completion reads memory only.* `-Properties` and `-Attribute` complete the schema's LDAP names (not the confidential ones, 200 at most, tool tip: the
    syntax); `-SearchBase` completes the naming contexts of the root DSE and the containers this window's commands have returned (`AdmKnownContainers`, in the window context;
    bounded to 256, memory only, the newest first; the session exposes no cache of the tree, so nothing else is known) and quotes a DN that needs it. A completer finds its window with a
    nested `Microsoft.PowerShell.Utility\Get-Variable Host` of the current runspace (module-qualified, so a function of the operator named `Get-Variable` cannot
    stand in for it – a test defines one; `SessionStateProxy` cannot be used there: "A pipeline is already running"); a test asserts the fake session saw no call
    during completion.
13. *Help is MAML beside the assembly.* `SessionStateCmdletEntry` takes the **absolute path** of `AdMng.PowerShell.dll-Help.xml` (a relative name is not
    searched; the project copies the file to the output with `Link=` so it is beside the dll, not in `Help/`); with the file `Get-Help` shows synopsis, syntax of every
    parameter set, a description of every parameter and one example. `CommandHelpTests` fails when a command lacks a synopsis, a description or an
    example, when a parameter has no description or when the help lists a parameter the command does not have. The XML is the source (it was generated once from
    the commands' metadata and written by hand after that). In a **single-file** `AdMng.exe` `Assembly.Location` is empty, the file is not found and `Get-Help` falls back to the
    engine's generated syntax without descriptions and examples – part 4 decides whether the publish layout puts the file beside `AdMng.exe` (then
    `HelpFilePath` must look in `AppContext.BaseDirectory`) - it does, and it does (12.4 decision 1).
14. *The engine project references no new package and the lab tests reference the engine:* `tests/AdMng.Directory.IntegrationTests` got a project reference
    to `AdMng.PowerShell` and the `PowerShellEngineConsumer.props` import (the lab job restores the PowerShell SDK too).

**Tests** (all on Linux): `AdMng.PowerShell.Tests` – `Reading/GetAdmCommandsTests` (identities of every kind, filters, properties, errors, pipeline, language mode),
`GroupMemberAndSearchTests` (direct, nested, cycle, ranged, a hostile-value table), `ValueComparisonTests` (**1000 generated objects, every attribute
compared with `AttributeFormatter` / `SecretAttributes`**: more than 15,000 values; it also runs in another time zone – a mutation to UTC dates fails it),
`StreamingAndSessionTests` (lazy 100,000-object reader, stop, one session, no write, no `-Server`/`-Credential`), `ErrorsDisplayAndCompletionTests`, and
`CommandHelpTests`; `AdMng.Core.Tests` – `LdapFilter.Pattern`/`EqualBinary`; `AdMng.Directory.IntegrationTests/Integration/PowerShellReadLabTests` against the
Samba lab (the administrator with typed values, a filter compared with a session search, an escaped value `Łukasz (Kraków)*\` among decoys, nested groups with a cycle, a missing object
and a missing base). Mutation checks done: unescaped filter values (10 of 20 fail), a command that buffers the result set (3 of 8 fail), dates in UTC (1 of 6 fails
in another time zone).

**What could not be verified on Linux, and why it should still be right.** The Windows run of the new tests is the last push's CI run (recorded in the report of the
part). Things only Windows can show: the table of a long DN (`…` is the engine's own ellipsis, the same on both systems); `TimeZoneInfo.Local` conversions (the same
call as the program's formatter); the help file when the engine runs from a published folder (above); and the completer's nested pipeline in the STA runspace of Windows
(the engine's `ThreadOptions = ReuseThread` is the same on both systems; the completion test runs in the Windows job).

**Open questions for the next parts, and what became of them.** (1) Part 3: the DN link finder and the `…` of a cut table (decision 10) - settled in 12.3 decision 12. (2) Part 4: where the help file goes in the
published layout (decision 13) - beside `AdMng.exe` (12.4 decision 1); whether `Out-AdmGrid` takes its columns from the objects' properties (`DistinguishedName` is the first one on every object, the attribute
properties follow in request order) - it does (12.4 decision 9). (3) Whether `LockedOut` should use the domain's lockout duration like the list (it needs a read of the domain head; the
DC's computed flag is used when it is returned) - still open (12.0). (4) `Get-AdmGroupMember` does not list the members whose primary group it is (as the Members tab does not) - kept, open for the owner (12.0).

### 12.3 Part 3 - the window's view model, the line editor's model and the policies

**What exists.** Everything of the window that is not WPF, in `src/AdMng.Presentation/PowerShell/` (namespace
`AdMng.Presentation.PowerShell`), tested on Linux with a fake engine session (220 tests in `tests/AdMng.Presentation.Tests/PowerShell`, 8 more of the grid table in `tests/AdMng.Core.Tests/PowerShell`):

| Piece | What it is |
|---|---|
| `PowerShellWindowViewModel` | One per window: the title, the scrollback (`Output`), the editor (`Input`), the history (`History`), `Status` (the status line), `RunCommand` / `SubmitCommand` / `StopCommand` / `ClearCommand` / `OpenObjectCommand` / `ToggleTrustedModeCommand`, the language mode, the question of a running command (`PendingPrompt`), the notes about the policies, `PendingPlan` (the empty place of R2), `SetOutputWidth`. A `WindowViewModelBase` (session pinning, the close question, `Dispose`). |
| `PowerShellInputModel` + `PowerShellInputHistory` | The editor's state: text, caret, highlight spans, completion list and selection, Enter / Shift+Enter, Up / Down (lines of a multi-line input, then the history, then the draft), Ctrl+R, the answer modes. |
| `PowerShellOutputDocument` | The scrollback: lines of runs (colours, links), partial lines, the cap, the count of lines of output. |
| `PowerShellDnLinkFinder` | The distinguished names of a text. |
| `PowerShellPolicies`, `IPowerShellPolicyReader`, `IPowerShellPolicySource`, `PowerShellPolicyReader`, `PowerShellPolicyNotes` | The policies (D7): the record, the raw reads the Windows host implements, the rules that turn them into the record, the note. |
| `IPowerShellSessionFactory`, `PowerShellSessionRequest` | How the window gets its engine session (App implements). |
| `IPowerShellWindowActions` (+ `AdMng.Core.PowerShell.PowerShellGridTable`) | What `Out-AdmGrid`, `Open-AdmObject` and `Export-AdmList` call back into (App implements). |
| `PowerShellServices`, `AddAdMngPowerShell()`, `IDialogViewModelFactory.CreatePowerShell()` | The scoped services of a window and its creation. |

**What part 4 implements and binds** (the whole list; nothing else of the window is missing):

1. *Register* `IPowerShellSessionFactory` (creates the engine's `PowerShellSession` from a `PowerShellSessionRequest`: the directory session
   becomes `new AdmWindowContext(request.Directory, request.ApplicationVersion)`, `Culture` and `OutputWidth` go to the options, the real lockdown probe is the default),
   `IPowerShellPolicySource` (the registry and the files, below) and `IPowerShellWindowActions` (scoped: it needs the window's dialogs and
   the grid). `AddAdMngPowerShell()` (called by `AddAdMngPresentation`) builds `PowerShellServices` from whatever is registered, so a host that registers
   nothing still works - the window says "This installation has no PowerShell engine."
2. *Open the window:* add `IDialogService.ShowPowerShellAsync()` (and the fakes in both test projects) that calls `IDialogViewModelFactory.CreatePowerShell()`, hosts the
   result as every `IWindowViewModel` (the window calls `InitializeAsync()` after it is shown and `CanCloseAsync()` before it closes),
   `services.AddWindow<PowerShellWindowViewModel, PowerShellWindow>()`, `SmokeTest`, the command descriptor `tools.powershell` (Ctrl+Shift+P)
   and the menu entry. The window is **not** opened by anything in this part: no `IDialogService` member exists yet.
3. *The engine adapter of the three commands:* the engine's own interface of window services (the plan of 4.10, `IAdmWindowServices`, part 4 defines it in `AdMng.PowerShell` next
   to `IAdmWindowContext`) is an adapter over `IPowerShellWindowActions`; `Out-AdmGrid` builds a `PowerShellGridTable` from the objects (columns from
   their properties - `DistinguishedName` first, then the attributes in request order - cells as the text the window shows, `null` for no value, the
   column named `DistinguishedName` as `distinguishedNameColumn`) and calls `ShowInGridAsync`; the host turns the table into the rows of the program's
   object list. `Open-AdmObject` calls `OpenObjectAsync` (a DN, or an `objectGUID` the host resolves through the window's session; `NotFound` is an error
   record of the command). `Export-AdmList` calls `ExportListAsync` (a path, or `null` for *Save As*; the host writes CSV through `ICsvCodec.Write(..., protectFormulas: true)` and
   `CsvFiles.WriteAsync`, delimiter `CsvFiles.ListSeparator(regional culture)` when none is given; it returns the path or `null` when the operator cancelled).
   All three are called **on the pipeline thread** with the pipeline's token and waited for there.
4. *The view:* the `c:StatusLine` named `PageStatus` bound to `Status.Current` as the V2 guide says (the file goes on `PagesWithAStatusLine`); the action button
   (`Command="{Binding RunCommand}"`, `CancelCommand="{Binding StopCommand}"`, `IsRunning="{Binding IsRunning}"`, `IsCancelling="{Binding Status.IsCancelling}"`);
   `Output.Lines` as the scrollback (`Kind` chooses the colour and the glyph: `Error`, `Warning`, `PolicyNote` use the status line's glyphs; `Runs` are the pieces, a run with
   `IsLink` is a link that executes `OpenObjectCommand` with its `DistinguishedName`, tool tip `L.PowerShell.OpenLinkTip`; `DroppedNote` is shown above the list while it is not null; follow the end
   of the list while the operator has not scrolled up); the editor (below); the switch of the trusted mode (`IsChecked` one way from `IsTrustedMode`, `Command="{Binding ToggleTrustedModeCommand}"`,
   `TrustedModeDisabledReason` as its tool tip, label `L.PowerShell.TrustedSwitch`); the empty `PendingPlan` area (`IsVisible`); `SetOutputWidth(characters)` when the output area is resized.
   The strings that only the view needs (the labels of the buttons, the placeholder, automation names) are part 4's, in the same block of the resx files.
5. *The editor control* forwards keys to `PowerShellInputModel` and draws it: edits -> `SetText(text, caret)`; Enter -> `SubmitCommand`; Shift+Enter -> `Input.InsertNewLine()`; Tab -> `await Input.CompleteAsync()`;
   Shift+Tab -> `Input.CompletePrevious()`; Esc -> `CloseCompletion()` or `CancelSearch()`; Up / Down -> `MoveUp()` / `MoveDown()` (a key they refuse goes on to the control); Ctrl+R -> `BeginSearch()`,
   then the characters typed go to `SetSearchQuery(...)` while `IsSearching`; Ctrl+C -> copy with a selection, else `StopCommand`; Ctrl+L -> `ClearCommand`. It draws `Spans` (kinds of `PowerShellSyntaxKind`),
   dots while `IsSecret`, `InputPrompt` before the text, the list of `Completions` with `SelectedCompletionIndex`, and `SearchQuery` / `SearchMatch` / `SearchFailed` while `IsSearching`.
6. *The Windows source of the policies* (`IPowerShellPolicySource`) - the keys, exactly (`PowerShellPolicyKeys` has them as constants; `HKLM` is `PowerShellPolicyScope.Computer`, `HKCU` is `User`):

| What | Where | Value |
|---|---|---|
| What | The policy's own PowerShell 7 key (`HKLM`, then `HKCU`) | Read instead while its `UseWindowsPowerShellPolicySetting` is non-zero | Values |
|---|---|---|---|
| Execution policy | `Software\Policies\Microsoft\PowerShellCore` | `Software\Policies\Microsoft\Windows\PowerShell` | `EnableScripts` `REG_DWORD` 0/1 (0 = scripts off), `ExecutionPolicy` `REG_SZ` |
| Transcription | `…\PowerShellCore\Transcription` | `…\Windows\PowerShell\Transcription` | `EnableTranscripting` 0/1, `EnableInvocationHeader` 0/1, `OutputDirectory` `REG_SZ` |
| Script block logging | `…\PowerShellCore\ScriptBlockLogging` | `…\Windows\PowerShell\ScriptBlockLogging` | `EnableScriptBlockLogging` 0/1, `EnableScriptBlockInvocationLogging` 0/1; set nowhere (the program's file has no section since D-B), the engine still logs the script blocks it finds suspicious and the reader says `NotSet` (4.2) |
| Module logging | `…\PowerShellCore\ModuleLogging` | `…\Windows\PowerShell\ModuleLogging` | `EnableModuleLogging` 0/1; `ModuleNames`: a `REG_MULTI_SZ` / `REG_SZ` value of that name, else the value **names** of the sub key `ModuleNames` |
| The config files | `ReadConfigFile(Computer)`: `powershell.config.json` in the folder of `System.Management.Automation.dll` (the engine's all-users file, the one the publish places: `Transcription` and `ModuleLogging` off, no `ScriptBlockLogging` since D-B); `ReadConfigFile(User)`: `<Documents>\PowerShell\powershell.config.json` | – | the text, or `null`; the reader converts `PowerShellPolicies` as the engine's Newtonsoft.Json does (sections `ScriptExecution`, `Transcription`, `ScriptBlockLogging`, `ModuleLogging` with the value names above, and the three the window does not show) |
| The lockdown | `GetLockdownState()` | – | `SystemPolicy.GetSystemLockdownPolicy()` mapped to `PowerShellLockdownState` (the engine's `SystemPolicyLockdownProbe` already does it) |

   The switch is a value **of each policy's own key in each hive** - the execution policy's is in the root key, transcription's in `PowerShellCore\Transcription` - not one switch of all of them
   (audit W-02: the first reader read it once, from the root key of either hive, and swapped every policy of both hives). The rules, from the decompiled `Utils.GetPolicySettingFromGPOImpl` and
   `TrySetPolicySettingsFromRegistryKey` of `System.Management.Automation.dll` 7.6.10 (`PowerShellPolicyReader`, tested by `PowerShellPolicyEngineRulesTests` and `PowerShellPolicyReaderTests`):
   per policy and per hive in the order `HKLM`, `HKCU`; a non-zero `UseWindowsPowerShellPolicySetting` (any `REG_DWORD`; another type makes the engine's cast fail, so the window says it
   could not tell) reads the Windows PowerShell key of that policy instead, and an absent Windows PowerShell key sets nothing in that hive; a switch value counts only as the `REG_DWORD` 0 or 1, a text as a
   `REG_SZ` / `REG_EXPAND_SZ` (shown unexpanded); the **first hive whose key sets any value of the policy decides the whole policy**; only then the all-users file, then the user's - the first file
   that has the section decides it (also an empty one), and a file whose `PowerShellPolicies` the engine cannot convert as a whole sets nothing. The execution policy is the engine's machine policy
   (`HKLM`, then the all-users file) before its user policy (`HKCU`, then the user's file), and a place that sets neither `EnableScripts` 0 nor 1 with a name defines none. The engine reads each policy
   once per process (its caches are static), the window when it opens. The source (`IPowerShellPolicySource`) returns raw values - `null` for an absent key or value - and throws
   `PowerShellPolicyUnreadableException` for a real failure; which type is a setting is the reader's rule.

**Decisions of part 3 that the later parts follow** (each with its reason):

1. *The policy types and the reader are in Presentation, not Core.* The plan put a snapshot in Core; nothing outside the window uses it (the engine learns the lockdown from its own probe), so
   Core would only carry types for the view model. `IPowerShellPolicyReader` is what the window depends on (a fake in the tests); `PowerShellPolicyReader` over `IPowerShellPolicySource` holds every rule, so the
   Windows part is only the raw reads.
2. *The order the reader takes* is the engine's (corrected after the audit, W-02; the rules are under the table above): per policy, `HKLM` then `HKCU` of the policy's own key, the Windows
   PowerShell key of that policy instead while its switch is on, the first hive that sets any value of the policy deciding the whole policy; then the all-users config file, then the user's. A policy
   set off decides too (that is how a policy, or the file AD Manager ships next to the engine for the transcription and the module logging, turns off what a user's file turned on). The first reader modelled the switch as one value of the
   root key that swapped every policy of both hives, and took the hives value by value; its tests pinned that model, so they passed. The decompiled engine settled it; a run under a real GPO
   (pilot) is still to compare the note with what the engine records.
3. *The note is plain and says where* (D7): one sentence per recording policy - transcription (where the files go, "Documents folder of your account" when no directory is named), script block logging (the PowerShell Core event log), module logging (the modules) - each with its origin
   ("computer policy", "user policy of Windows PowerShell", "your powershell.config.json"…), then "AD Manager does not change these policies and does not get around them." Execution policy and a lockdown record nothing and make no note (the lockdown is the reason of the disabled switch and a line of the scrollback).
   Script block logging that nothing sets makes no note either, although the engine then logs the script blocks it finds suspicious (4.2): that is the engine's own default, not a policy of the workstation, and the guide and the README say it (12.13).
   The log copy is the key and a mask of what is on (1 transcription, 2 script block logging, 4 module logging) - never the directory or the modules.
4. *What lasts, and what covers it* (`UpdateLasting`, one method): the end of the session (error), a start that failed (error), then **notes**: the policy note, "the policies could not be read" (an attention - not knowing is said), the trusted mode; when more than one is true they are one attention, one after the other, with the
   log key `PowerShell.Status.Notes` and their number. A running command and its result cover the lasting state, as everywhere in V2, and it is back when the result is closed - which is what "a lasting note before the first command" means. The policy note is also the first lines of the scrollback (`PolicyNote`), where nothing covers it.
5. *A command is an operation with `OperationWrites.Other`.* In R1 no command writes, but what a window runs is whatever the operator typed (in the trusted mode, anything), so closing the window while it runs asks "Stop the running operation and close the window?" and waits for the end - the existing `RunningWork` question, nothing of its own.
6. *`Status.Show` while a command runs ends the command* (a holder shows one thing; `Show` replaces the operation). The switch of the language mode, which may be refused by the engine, therefore says its refusal in the scrollback always and on the line only when no command runs (a test found it).
7. *No count of objects.* The status line says what it knows: lines of output ("12 lines of output in the window, 0.8 s"; Polish plural forms), not the "N objects" of the plan, because the formatted mode (part 1, decision 4) hands the window text, not objects. A count needs the engine (an `Out-Default` that counts, or the commands reporting what they wrote);
   the contract `PowerShellRunEndItem` has no such field. Open question 1.
8. *The stream is read off the UI thread and handed over in steps* (`PumpAsync`): the first item after a quiet moment goes at once (a line appears as it is written); after a step, what follows is gathered for at least 40 ms (`HandOverInterval`, measured from the end of the step) or until 2,000 items, and each step is awaited on the UI thread - a command that writes faster than the window shows is held back (the engine's bounded channel does the rest). The steps go at `UiDispatchPriority.Background` (WPF `DispatcherPriority.Background`, below input and rendering). *After the audit (W-03, by reasoning, not run):* the first pump handed a step over whenever the next item was not ready, at the dispatcher's normal priority, so a command that writes without a pause (`while ($true) { 'x' }`) always had a step queued above the keyboard and the mouse and Stop could not be pressed, and a trickle of one line per millisecond made one hand-over per line (302 for 300 lines; now at most one per interval - `PowerShellWindowRunTests`). The view queues one scroll at a time and looks for the last line when it runs (W-05). `PowerShellWindowUiTests.AFloodOfOutput_LeavesTheKeyboardFree_AndCtrlCOfInputPriorityIsTakenAtOnce` floods the real window over the real dispatcher and posts Ctrl+C at input priority; **a person still has to try it on a real screen** (click Stop during `while ($true) { 'x' }`).
9. *The scrollback is bounded:* 20,000 lines, dropped in steps (it may hold up to 22,000 before it drops the oldest down to 20,000, so a window fed line by line does not copy its list for every line); `DroppedLines` and `DroppedNote` say how many. A batch of one line is an ordinary `Add`, more than one a `Reset` (the `BulkObservableCollection` convention).
   The lines of the window's own (welcome, the note, the answers) are not counted as output.
10. *The questions of a command* are answered in the editor, in `Answer` or `Secret` mode (no highlight, no completion, no history, always "run" on Enter): text, secret, a choice (the letter, the text, `?` for the help messages, an empty answer for the default, an unknown answer asks again). After the audit (W-08) a secret is never text of the model (`SetText` is ignored in the `Secret` mode): Enter in the password box hands `SecurePassword.Copy()` to `AnswerSecretCommand`, which gives it to the command read-only, and the box is cleared on every change of mode; until then each key put the whole secret into `Input.Text` as a string and the box kept it for the life of the window. W-09: the text typed ahead as the next command when a question comes is kept (`CommandDraft`) and comes back with the command mode, and the first empty answer to a choice asked while there was such text asks again (the Enter was most likely meant for the command; it took the default, Yes of a `-Confirm`); the command line follows the model on every change of mode. The answer is never a command and never in the history. `PendingPrompt` is set **last** (a test saw the question pending before its lines were there). W-07: the empty editor says `EditorHint` - the command hint, or `PowerShell.Editor.AnswerHint` while `Input.IsAnswering` (before the audit the command hint stayed and the answer hint was used nowhere) - and the line style names an error, a warning or the policy note "Error: <text>" (`PowerShell.Kind.*`, also unused before), so the kind is heard, not only seen.
    Stop cancels a pending question.
11. *Highlighting, completion and history are the engine's* (`Tokenize`, `CompleteAsync`, `IsInputComplete`, `ShouldRemember` through `IPowerShellSession`); the model decides only the keys and the state. A completion result for text that changed meanwhile is dropped; one match is put in, several open the list (first selected, Tab cycles, Enter takes), a second Tab supersedes the first request.
    The history keeps 500 lines, no consecutive repeats, and never a line the engine refuses.
12. *The finder of distinguished names* recognises a name by its shape - leading components of the usual types and the trailing `DC=` components - and checks it with `DistinguishedName.TryParse`. It ignores a name followed by the engine's ellipsis, or by a comma that goes straight on (a cut table cell: the beginning of a name is often another valid name), a value with an
    unescaped `=`, text longer than 8,000 characters, and anything it cannot read in 200 ms.
13. *`PowerShellGridTable` is in Core* (the engine builds it, the window shows it): cells are text (`null` = no value), the column names are unique ignoring case, rows have one cell per column, the DN column is named; its `ToString()` shows only the counts. `IPowerShellWindowActions` is in Presentation (the engine reaches it through part 4's adapter).
14. *`PowerShellSessionRequest` carries the window's `IDirectorySession`* - the factory builds the read-only context from it (D8, rule 4); the request's `ToString()` names nothing.

**Tests.** `AdMng.Presentation.Tests` (Linux, 220 new): `PowerShellPolicyReaderTests` (each hive, the switch of each policy, the Windows PowerShell keys, both config files, precedence, off beats on, comments and case in the file, a file that is not usable, the lockdown; after the audit with `PowerShellPolicyEngineRulesTests`, the engine's rules and the auditor's scenarios A-D),
`PowerShellPolicyNoteTests` (each setting, several, both languages, the log copy), `PowerShellWindowRunTests` (the echo, streaming and the status sequence with and without Stop, in both languages, a result of 100,000 lines, a line shown as soon as it is written, errors, a failed command, progress,
the close question with No and Yes, disposal during a run, the questions of a command, the history rules, Enter, the width), `PowerShellWindowStatusTests` (title, summary, starting, a failed or missing engine, a disconnected window, the end of the session, every combination of the notes, the trusted mode with its
confirmation, a lockdown from the session or the policies, a refusal by the engine), `PowerShellInputModelTests`, `PowerShellInputHistoryTests`, `PowerShellOutputDocumentTests`, `PowerShellDnLinkFinderTests`, `PowerShellWindowActionsTests` (the contract through a fake, the actions of a host that gave none; the table itself is `AdMng.Core.Tests` `PowerShellGridTableTests`),
`PowerShellRegistrationTests` (scoped, built from the host's registrations, a window from the factory), `PowerShellRulesTests` (Presentation does not reference the engine; the window's code names no file write, registry, settings or write API). `ConnectionLifetimes` has `PowerShellServices`.
Mutation checks done: the close question without `OperationWrites.Other` (1 test fails), the computer and user policy layers swapped (1 fails), the ellipsis rule of the finder removed (4 fail).
The tests use a fake `IPowerShellSession` whose "pipeline" has a thread of its own and a channel, like the engine, so a command that blocks on a question does not block the reader; there is no real-engine test in `AdMng.Presentation.Tests` (it would add the PowerShell SDK to the test project; the engine has its own tests, and the window talks to it through `IPowerShellSession`).

**What could not be verified on Linux, and why it should still be right.** How the view draws any of it (nobody has looked; part 4 writes the view against the properties above); the Windows source of the policies (the keys come from the engine's metadata and the documented templates, the rules are tested here); whether the engine's `Write-Progress`, `Read-Host` and `-Confirm`
arrive as the items the fake streams (part 1 tests them against the real host); the timing of the UI-thread hand-over under the real `Dispatcher` (the logic is the same as the list loads', and the Windows UI test of part 4 holds a command and watches the line).

**Open questions for the next parts, and what became of them.**
1. *"N objects" in the status line* needs a count from the engine (`PowerShellRunEndItem` has none); the line says lines of output meanwhile. A proposal: the engine counts what reaches `Out-Default`, or the commands report what they wrote through the context.
2. *`UseWindowsPowerShellPolicySetting`:* answered from the decompiled engine after the audit (W-02): a value of each policy's own key in each hive, and the Windows PowerShell key of that policy replaces the PowerShell 7 one of the same hive. A comparison under a real GPO is a pilot item.
3. *The default actions in Presentation:* `IPowerShellWindowActions` is only a contract; the logic that does not need WPF - the GUID lookup of `Open-AdmObject`, *Save As* and the CSV file of `Export-AdmList` - could be a Presentation class with a Linux test (it needs one new member, "show a table in the grid", from the host); part 4 decides - decided: `PowerShellWindowActions` is a Presentation class (12.4 decision 10).
4. *Where the policy note lives:* as the lasting state of the status line it is covered by every command's result until the result is closed (V2 behaviour). If the owner prefers it always visible, it is `PolicyNoteText` - a note (`Ad.Note.Attention`) in the window's layout costs the view one line, and the view model needs nothing - part 4 built that note in the layout (12.4 decision 6).
5. *The stage S readiness item* can reuse `PowerShellPolicyReader` and `PowerShellPolicyNotes` as they are.

### 12.4 Part 4 - the window, the grid, the engine in the program

**What exists.** In `AdMng.App` (`src/AdMng.App/PowerShell/`): `PowerShellWindow` (+ `PowerShellWindowViewModel` of part 3), `PowerShellEditor`
(a `UserControl`; the text conversion in `PowerShellEditorText`), `PowerShellRuns` (the pieces of a line as `Run`s and links),
`StateCheckBox` (the trusted-mode switch), `PowerShellGridWindow` (the grid of `Out-AdmGrid`), `PowerShellSessionFactory` (the engine session of
a window) and `WindowsPowerShellPolicySource` (the registry and the two config files); `powershell.config.json` beside the program;
`SmokeTest.PowerShell.cs`; the registrations in `AppHost`; *Tools > PowerShell...* in the main window; `IDialogService.ShowPowerShellAsync` and
`ShowPowerShellGridAsync` (+ `ToolWindows.ShowPowerShell`, key `tools:powershell`, one window per connection window). In `AdMng.Presentation`:
`PowerShellGridViewModel` (+ `PowerShellGridRow`), `PowerShellWindowActions`, the descriptor `tools.powershell` (`Ctrl+Shift+P`), `CopyLines` and
`CopyDistinguishedName` of the window, `PowerShellInputModel.AcceptCompletionAt` and the off-thread highlighting of long text, the strings of both languages (`L.PowerShell*`, `L.PowerShellGrid`, `L.Commands.PowerShell`). In `AdMng.PowerShell`: `Out-AdmGrid`, `Open-AdmObject`,
`Export-AdmList` (+ MAML help), `IAdmWindowServices` on the context, `AdmTableBuilder`, `PowerShellEnvironment.EngineHome`. The token `Ad.Font.Mono`
(Tokens.xaml, `DesignTokenTests`). A CI step checks the published layout and that the Watchdog output has no engine.

**Decisions of part 4 that the later parts follow** (each with its reason):

1. *The single-file `AdMng.exe` runs the engine; the fallback layout is not needed.* The step "Smoke test published app" of Windows CI passed
   with the engine check of `SmokeTest.PowerShell.cs` (a runspace, `1 + 1`, `Get-AdmVersion`, ConstrainedLanguage, execution policy `Restricted`, the
   ten commands, `Import-Module` refused, the help of a command): `$PSHOME` was `...\publish`, the first runspace 523 ms, the second 9 ms. Without a
   location of the bundled assembly the engine takes the folder of the program (also seen on Linux in a single-file publish). Two things
   had to change for it: `Assembly.Location` (empty in a single file; the analyser IL3000 fails the single-file build) is not used for the help
   file any more - it is looked up in `AppContext.BaseDirectory` - and `PowerShellEnvironment.EngineHome` reads it under a pragma that says why.
   Sizes (re-measured in part 5 with one method, 12.0): `AdMng.exe` 143.5 MB -> 181.9 MB (+38.4 MB, uncompressed single file), the publish folder 164.7 MB -> 204.3 MB, the CI artifact 68.8 MB -> 82.4 MB.
2. *The editor is a `TextBox` that draws no text and a `TextBlock` under it that draws the text in colours* (the header of `PowerShellEditor.xaml`).
   The box edits (selection, undo, clipboard, input methods, a screen reader reads it); its `Foreground` is transparent and its template is
   the bare text host. The layer is built from the model's `Spans` as `Run`s with `SetResourceReference` to the theme's text brushes (command, keyword and type
   `AccentTextFillColorPrimaryBrush`, variable `SystemFillColorSuccessBrush`, string `SystemFillColorCautionBrush`, number `SystemFillColorAttentionBrush`, parameter,
   operator, comment and member `TextFillColorSecondaryBrush`, the rest `TextFillColorPrimaryBrush`: only keys of the closed set, so the colours follow light,
   dark and High Contrast, where they collapse to the text colour and the structure is still readable). It is placed where the box puts its first character
   (`GetRectFromCharacterIndex(0)`, asked after the layout and again when the box scrolls or resizes), not by a metric of the template, so a one pixel difference of
   the text host's internals cannot show. *Why not a text surface of our own:* it would need the caret, selection, clipboard, input methods and a UI Automation
   text pattern again; the cost of this design is the alignment of two layers, which nobody has seen (below).
3. *Newlines.* The box breaks lines with `\r\n`, the model with `\n`. The box is the source of the text while the operator edits (`TextChanged` -> `SetText(ToModel(text), ToModelIndex(...))`),
   the model while it changes the text itself (history, completion, the end of a command: `ApplyText` writes it into the box and puts the caret by `ToBoxIndex`); a flag
   keeps the two from echoing. `PowerShellEditorText` is pure and has tests.
4. *Keys* (`PowerShellEditor.HandleKey(key, modifiers)`, tested without a keyboard): Enter = `SubmitCommand` (the view model's: question, completion, search, run or new line);
   Shift+Enter inserts a line in the box; Tab / Shift+Tab / Ctrl+Space complete; Esc closes the list or the search; Up / Down are the model's (lines, then history); Ctrl+R searches
   (typed characters go to the search while it is on, Backspace removes one); Ctrl+L clears; Ctrl+C with no selection is Stop (with one it is the box's copy). In the output list Ctrl+C copies
   the selected lines (nothing selected: Stop), Enter opens the first distinguished name of the selected line, Esc returns to the editor. A secret answer is a `PasswordBox` (a screen
   reader says "password"), not the box with dots.
5. *Highlighting and completion are off the UI thread.* Completion was already (`CompleteAsync`, one request at a time, a changed text drops the answer); the model now
   highlights a text of up to 2,000 characters at once - a command line is a few hundred and the parser takes well under a millisecond - and a longer one (up to 20,000) on a pool
   thread, shown plain until the spans arrive, the next keystroke cancels it (`HighlightTask`, tested with a held tokenizer). The list of completions is a `Popup` under the caret
   that takes no focus; a click takes a match (`AcceptCompletionAt`); it closes when the box loses the keyboard.
6. *The window.* Top to bottom: the status line, three notes that last (what the policies record - `PolicyNoteText`; the trusted mode; the cut scrollback), the output (a virtualised `ListBox`
   of lines, extended selection, a context menu with copy and select all, following the end until the operator scrolls up), the area of the plan R2 will fill (a collapsed `Border`), the editor, and
   *Run* (the action button: its Stop is the command's) / *Clear* / the switch. A line is a glyph column (errors, warnings and the policy note have their glyph - never the colour alone),
   the pieces of the line as `Run`s (the colours of `Write-Host` are mapped to the theme's status brushes; white, grey and black keep the text's own) and a distinguished name as a `Hyperlink`
   (click: open; context menu: open, copy the name; no tab stop - Enter on the line does it). The width the engine formats for is the width of the output in characters (`SetOutputWidth`).
   The policy note is therefore in four places: the status line (lasting, covered by a command's result as everywhere in V2), the layout note, the first lines of the scrollback and - as a
   fact - the log copy; the owner asked for the layout note, part 3 built the rest.
7. *The trusted-mode switch is a `CheckBox` that never toggles by itself* (`StateCheckBox`): a click runs the command, the box says what the view model says, so a refused or declined change leaves it
   as it was. Disabled, its tool tip is `TrustedModeDisabledReason`.
8. *`Out-AdmGrid` opens a window of its own, not the object list of the main window.* The object list (`ObjectListViewModel`) is a view of a directory location - a container, a search, a saved view, a Find
   query - which it reads, refreshes, watches for changes and virtualises by itself, and whose cells it formats a second time with the attribute formatter; the rows of a pipeline are none of
   that, and feeding them in would be a fifth kind of location in a 2,000-line class that nobody can run here. `PowerShellGridViewModel` instead uses the list's own parts: `ColumnViewModel` (header,
   sort glyph, width), `b:DynamicColumns` (the `GridView` from the columns, a click on a header sorts), `b:Density`, the row height and the footer. It adds a filter (case and diacritics folded, over all columns),
   numeric-aware sorting (empty cells last), copy (tab separated, formulas protected as the list copies them), *Copy DN*, CSV export of the rows as shown (formulas protected, the regional separator,
   UTF-8 with a BOM, only to the file the operator chose), and *Open in the program* (Enter or a double click; at most 10 windows; only rows that have a distinguished name). Every call opens a new window
   (like `Out-GridView`); it belongs to its connection window and ends with its session ("session ended" on the line, rows stay, nothing can be opened).
9. *The three commands.* `Out-AdmGrid` and `Export-AdmList` turn each object into a row as it arrives (`AdmTableBuilder`: columns = the properties in order of first appearance, the
   `DistinguishedName` first; plain values are one column `Value`; cell text: dates `yyyy-MM-dd HH:mm:ss` in local time, numbers and booleans independent of the language, lists joined by `; ` as the program's
   lists do, binary as `(binary, N bytes)`, a `SecureString` never as anything but its name, no value is `null`). `Open-AdmObject` resolves the identity itself - a DN, `objectGUID`, SID, account name - through the
   window's reader (`AdmIdentity`, `AdmSearchPlan`: escaped filters, one DC, only `name` is read), so the host opens a distinguished name and never reads the directory; a missing object is an error that names it and no window
   opens. `Export-AdmList` takes `-Path` literally (brackets are no wildcard), refuses a path outside the file system, an existing file without `-Force` and a delimiter that is not one visible character; without
   `-Path` it asks in *Save As*, and when nobody chose a file it writes nothing and says so (a warning). The host writes the file (`PowerShellWindowActions`, `CsvFiles.WriteAsync`; the one place besides the grid that
   writes - `PowerShellRulesTests` pins it).
10. *`PowerShellWindowActions` is a Presentation class* (the open question 3 of part 3): the actions need no WPF, so they are tested on Linux; the host registers it scoped (`AppHost`) because it needs the connection's dialogs
    and files. The factory adapts it to the engine's `IAdmWindowServices`.
11. *The policies of the workstation* are read by `WindowsPowerShellPolicySource` through the keys of `PowerShellPolicyKeys`: only reads (`RegistryKey.OpenSubKey(writable: false)`), an absent key or value is "not set", a key that cannot be read is an
    exception that the window says it could not tell (never "nothing is recorded"), strings are not expanded. After the audit (W-01): the values are read with `GetValue(name, null, DoNotExpandEnvironmentNames)` and
    returned as the registry holds them - the first source asked `GetValueKind` first, which throws `IOException` for an absent value, so every workstation with a PowerShell 7 policy key would have
    shown "could not be read"; the CI runner has no such key and the UI tests replace the source, so no test ran it. The roots and the two files are injectable, and
    `WindowsPowerShellPolicySourceTests` (UI test project, Windows CI) writes a throwaway `HKCU\Software\AdMng.Tests-<guid>` (a key without the value, other types, `REG_EXPAND_SZ`, the module names, scenario C of W-02)
    and deletes it. A failure is `PowerShellPolicyUnreadableException` (the key or the file), which the error translator says in the window's language. The all-users file is in `PowerShellEnvironment.EngineHome` (`$PSHOME`), the user's in `Documents\PowerShell`.
    `powershell.config.json` ships beside the program (`CopyToPublishDirectory`): it turns off the transcription and the module logging that a user's file could switch on, and the Windows PowerShell compatibility layer (until D-B also script block logging, 12.13).
12. *Shortcut and menu.* `tools.powershell`, `Ctrl+Shift+P`, category Tools; the menu entry is in the Tools menu before the session log; a test asserts that no other descriptor uses the shortcut (and `Key="P"` is in no XAML).

**Criteria of section 10 and where each is shown.** (1) the read commands: part 2 tests; the commands that need the window: `WindowCommandsTests` (Linux), `PowerShellWindowUiTests` (Windows). (2) the escaping: part 2, and `Open_AdmObject_escapes...`.
(3) no file, no history of passwords, the note before the first command: part 1 "no files" test, `APlainTextPassword_NeverEntersTheHistory`, `TheNoteAboutTheRecordingPolicies_IsSaidBeforeTheFirstCommand...`. (4) the language mode: `TheTrustedMode_AsksFirst...`, `AnEnforcedLockdown_...`.
(5) no write command: `CommandCatalogTests` (ten commands). (6) the environment: part 1; `powershell.config.json`: `EngineBoundaryTests`. (7) the window opens from the menu, the palette and `Ctrl+Shift+P`, belongs to its connection, shows domain, DC and account, and its
runspace is disposed with the scope: `PowerShell_IsOfferedWhileConnected...` (Linux), `TheWindow_OpensFromTheConnection...` and `AnIdleWindow_ClosesWithoutAQuestion...` (Windows). (8) the single file: the CI step of the smoke test.

**Tests.** `AdMng.PowerShell.Tests`: `WindowCommandsTests` (24: grid table, cell text, identities, the 10-window limit, escaping, export path rules, stop through the call into the window, Constrained), `EngineBoundaryTests`, the catalog and help tests
for the three commands. `AdMng.Presentation.Tests`: `PowerShellGridViewModelTests` (17), `PowerShellWindowActionsHostTests` (7), `PowerShellWindowCopyTests`, the highlighting and `AcceptCompletionAt` tests of the input model, the
shell test of the command, the rule that only two files write. `AdMng.App.UiTests` (Windows): `PowerShellWindowUiTests` (the window over the real engine and the fake directory: open, run, the editor's colours, completion, Enter and Shift+Enter, history, Stop of a held command, the
error glyph, a distinguished name as a link, `Open-AdmObject`, `Out-AdmGrid`, `Export-AdmList`, the policy note, a lockdown, the trusted mode, closing), `PowerShellEditorPartsTests`, the lists of `XamlSourceTests`, `HostTests`, `DesignTokenTests`; the smoke test
renders both windows in both languages and starts the engine in the published file.

**What could not be verified here, and why it should still be right.** *Nobody has seen any of the window on a screen.* In particular: the alignment of the coloured layer and the caret of the box (placed from `GetRectFromCharacterIndex(0)`; if the box
internals disagree by a pixel the caret is a pixel off the text, nothing more); that a transparent `Foreground` keeps the caret and the selection visible in every theme (the caret brush is set, the selection is the system's); the look in High Contrast (the colours collapse to the text colour);
the popup of completions near the screen edge; how a screen reader reads the window (the list of completions is not announced - the box keeps the focus); the grid window's columns at narrow widths; the width of the output in characters for a window with a scroll bar. The Windows behaviour of
policies under a real GPO and of the engine under WDAC / AppLocker (the keys come from the engine's metadata and the templates; the rules are tested). `PowerShellRuns` finds the commands of a link by the inheritance of an attached property through the list - a Windows UI test clicks a link, but
a right-click menu is only checked for its entries.

**Deviations from the task text.** (1) `Out-AdmGrid` shows a window of its own, not "the program's object list grid" (decision 8). (2) The editor is a `UserControl`, not a `Control` with a style in `Themes/Controls.xaml` (that file's test wants every control with a `StyleKey` named in its header; a
`UserControl` has none, and the template is in its own XAML). (3) The monospace font is a new token `Ad.Font.Mono` (the token test lists the tokens on purpose). (4) `Open-AdmObject` resolves the identity in the engine (the host gets a DN) instead of the host resolving a GUID. (5) The status line
has no "N objects" (unit 3). (6) The policy note is in four places (decision 6). (7) The Windows PowerShell policy keys and JSON policies on Windows are still the reader's best reading of the engine (unit 3).

**Open questions.** (1) The completion list is not announced to a screen reader (no UI Automation event is raised for the selected match). (2) Should the grid of `Out-AdmGrid` become a tab of the main window? A window per call is `Out-GridView`'s model, a tab would need `IShellDocument`. (3) Should `Out-AdmGrid` accept `-PassThru`
and `-Wait` like `Out-GridView`? (4) `LockedOut` ignores the domain's lockout duration (part 2) - the grid shows what the command returned. (5) The stage S readiness item for the policies and the lockdown is not built (the reader is ready: `PowerShellPolicyReader` over `WindowsPowerShellPolicySource`); the readiness
check's shape (a list of facts of this computer, each with a state and a way to fix it) would make it a small addition, but it is a new row, its strings and a test of its own, and nobody asked for it in this part - left for part 5 or the owner.

### 12.5 Part 5 - the guide, the status, the recount

**What exists.** The operator's guide as a Polish / English pair (`docs/POWERSHELL.md`, `docs/en/POWERSHELL.md`: twelve chapters - the window and how to open it, the commands with an example each, escaping, results and the
grid, exporting, history, the language modes and the trusted mode, what Group Policy logging does, what it does not do yet, keyboard, troubleshooting, limits and the pilot; the transcripts in it are real runs of the commands over the
test directory); the guide's name in the window's first lines (`PowerShell.Welcome.Help`, both languages), because the other windows name their guide in plain text where they need to (the readiness check and some
error messages: `docs/en/COMPUTERS.md`) and there is no other link mechanism before the page header of track V3; the Features entry, the roadmap row and the stage line of Track R in the README pair (heading `Track R - PowerShell window 🟡 (R1: 🟡)`
with its anchors), `ROADMAP` and `GOALS` in both languages, the pilot scenarios PW1-PW10 (`PILOT.md` / `PILOTAZ.md` 3.7), the English-name list of `CLAUDE.md` (`POWERSHELL.md` = `POWERSHELL.md`), the release draft (`docs/releases/NEXT.md`, one set for R1 with the measured sizes) and
this block of the design note (12.0 as the one text, the open questions of 12.1-12.4 closed or carried).

**Decisions of part 5.**

1. *The guide links nowhere but names its document* in the window (decision above); the guide itself links the design note and the ADR.
2. *Sizes are measured again, with one method*: the same `dotnet publish` as CI for the commit of 0.16.0 (`76a4de49`) and for the head of R1 - `AdMng.exe` 143.5 -> 181.9 MB; part 4's 157.5 -> 194.8 for the folder look like MiB under the name MB (this measurement gives 157.1 -> 194.9 MiB), and
   the ADR pair now says which unit is which. CI prints MiB under the name "MB" (`1MB` of PowerShell), so its 173.5 is the 181.9 here.
3. *The guide says what the code does, not what the README promises*: `Start-Transcript`, `Export-Csv` and `Out-File` work in the window and write where they are pointed (checked by running them); the README's sentence that the window rejects
   `Start-Transcript` without `-Path` belongs to a later stage and is listed in 12.0 as open. *(Since 12.6 the code keeps the README's promise and the guide says so.)*
4. *The recount is a script* that reads the catalog, the git diff of the tests (declared `[Fact]`/`[Theory]`, `[InlineData]` rows) and both resx files, and leaves out the two test-stabilisation commits that are not R1; the numbers of 12.0 come from it and from
   `dotnet test` of the three projects.

**What could not be verified.** The guide's Polish text was written by the engineer of this part and read for diacritics and terms against `Strings.pl.resx`, not by a native editor; the "real session" in it comes from the test directory
(`contoso.test`, the sample company), not from a domain.

### 12.6 After the audit of R1 - the engine's security composition

The audit of the R1 commits (PS-01, PS-03, PS-08, ps-commands-5, the job part of PS-12, DLR-2) ran probe scripts against the engine and
found the composition wrong: the session was `InitialSessionState.CreateDefault()` minus `Start-Job` and `Import-Module`. In a window that
said ConstrainedLanguage, `1 | ForEach-Object -Parallel { ... }` ran in FullLanguage (the engine makes the parallel runspaces itself, with
the system's lockdown policy only), took the host through `$using:Host`, reflected from its `PrivateData` to the context, the reader's
`_inner` field and the real `IDirectorySession`, and deleted an object around the change plan; with `-AsJob` the job outlived Stop and
the session; `using module` loaded a module past the `Import-Module` guard; the background operator started the engine's Start-Job;
`Start-Transcript` without a path, `Out-File`, `Export-Csv`, `Set-Content` and `>` wrote files. What was built (4.1, 4.3, 4.6):

- `CommandGuards` - the one list: 68 engine cmdlets, two engine functions, four replacements, twelve refusals, the `help` alias,
  the aliases of these, no script entry, no `Function` provider. `SessionCommandsTests` pins it against a live session.
- The replacements run the engine's cmdlet through a FullLanguage bridge made at set-up (`WindowSetupCommand`), so the cmdlet runs
  in the caller's language mode and scope.
- `InputGuard`: `using module` / `using assembly`, the background operator and redirection to a file are refused from the text
  before anything runs; a typed `ForEach-Object -Parallel` gets a message.
- `ScriptFileGuard`: no script file is found by name or path at any depth; on Windows `WindowAuthorizationManager` refuses every
  script file whatever the execution policy.
- `WindowRegistry`: the context is found by the runspace; the host's `PrivateData` is empty.
- Jobs left in the session's repository are stopped when their command ends, on Stop and on dispose.
- Tests: `EscapeTests` (the audit's probe scripts, in both modes), `SessionCommandsTests`, `InputGuardTests`, the rewritten
  `LanguageModeTests` (every way to another language mode) and `GuardTests` (every way to a refused command), a stop test of a
  script that keeps writing. Run against the code before the change (`f429fbe9`), 41 of the 82 cases of `EscapeTests` and
  `LanguageModeTests` fail (`using module` of a binary module, written by the test, ran .NET code in ConstrainedLanguage - worse
  than the audit's script module); `SessionCommandsTests` and `InputGuardTests` do not compile there. The engine tests also run in
  the Linux CI job now (DLR-2).
- A second probe of the rebuilt session tried the paths that need no command of the session: the runspace object and an advanced
  function's `$PSCmdlet` to set the language mode, the provider intrinsics of `$ExecutionContext` that create and write files,
  `NewScriptBlock`, a method object's `Invoke()`, reflection from the window's `ForEach-Object` to the engine's cmdlet it wraps,
  `[type]::GetType` of the registry, variables left by the bridge in a script block's scope, and the module-qualified names of the
  engine's `ForEach-Object`, `Export-Csv`, `Out-File`, `Set-Content`, `Copy-Item`. All were refused; they are tests of
  `EscapeTests` now (the bridge and module-qualified cases fail against `f429fbe9`, the constrained-language ones were closed by the
  language mode already and are kept as guards).

**Decisions taken here** (the reasons are in 4.6):

1. *The list comes from what the README and the guide promise and what a read-only console needs*: reading, shaping and showing
   objects, reading files (a list of names from `Get-Content` or `Import-Csv`), variables, help, messages. `ConvertTo-SecureString` is
   kept (the guide's history rules name it; a test needs a secure string); `Get-Module`, `Get-Process`, the clipboard cmdlets,
   `Invoke-WebRequest` and the history cmdlets are not on the list - nothing of R1 needs them.
2. *`Invoke-Expression` is dropped.* In ConstrainedLanguage it stays constrained, so it is no language escape by itself, but it
   compiles text that `InputGuard` never saw (`using module`, `&`, `>`); nothing a read console does needs it.
3. *`Start-Transcript -Path` stays* - the README's "transcription only to an explicitly specified file" - while `Start-Transcript`
   without a path is refused; every other file writer is gone. Question 6 of 12.0 asks the owner whether the named-file transcript
   should stay.
4. *Console programs stay* (the application entry): they run as their own process with the operator's identity, never with the
   window's credentials - like `pwsh.exe`, which ADR-0010 already names. Their output still bypasses the window (audit PS-06, a
   separate change).
5. *Jobs end with their command* rather than only at Stop or dispose: no command of the window creates one, and the window cannot
   show a job's output (no `Receive-Job`), so a job left behind would be invisible work.
6. *Trusted mode*: the same list; the trusted mode adds no command. Its .NET code can still reach anything in the process,
   including the window's session and credentials, and can start threads or runspaces of its own that nothing in the window stops
   (only jobs in the session's repository are ended). Code defined while it was on kept FullLanguage after the switch back (PS-02;
   since 12.10 the switch back makes a new runspace).

**What could not be verified here.** Nothing ran on Windows: the authorization manager (the engine asks it on Windows only;
`SessionCommandsTests.A_script_file_reached_as_a_command_object_is_refused_on_windows` and the smoke test of the published file are the
first checks, in Windows CI), the allow-list on a Windows session (the 68 cmdlet names and the four replaced ones exist in the Windows build of the engine -
checked by listing the `[Cmdlet]` attributes of its five assemblies; the live test compares them in Windows CI), the replacements in the single-file `AdMng.exe` (the smoke test runs `ForEach-Object`), and the set-up
under a WDAC/AppLocker lockdown (it switches the session to FullLanguage for one internal command before the operator's first command;
the engine has no lockdown check on that switch, but nobody has run it under a lockdown). One run of the audit's 62-command probe battery
showed a later command with no output after a stopped prompt; three repeats and the isolated sequence did not reproduce it.

### 12.7 After the red team of R1 - a regular expression, the history, the trusted mode

A red team ran scripts against the rebuilt session (12.6) with eight goals: run arbitrary code (a), reach the window's directory
session (b), write around the change plan (c), outlive the window (d), write a file (e), pass a different language mode (f), keep a
plain-text secret in the history (g), and hold the window (h). Goals a-f found no way in ConstrainedLanguage. What it found, and what
became of it:

- **(h) A regular expression held the window.** `'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!' -match '^(a+)+$'` - default mode, no .NET call -
  backtracked for hours; Stop gave up after the grace period, the session stayed busy for good, and disposing the session left the
  thread running a core until the process ended (`-replace` the same). Fixed by the process-wide time limit of 4.2 (2 s, set by the
  program's module initializer): every way of the language to a regular expression ends with an error after 2 s, Stop ends the
  command itself. `RegexLimitTests` in its first form - the seven ways in both modes, and Stop - failed in all 15 cases before (each
  run reached the test's 20 s stop and the grace period); it keeps the seven ways in the default mode and `-match` in the trusted
  mode (each case takes 2 s), with the limit itself and a script that catches the error: 11 tests. Left: a
  `[regex]` made with a longer limit of its own (allowed in ConstrainedLanguage) and the trusted mode's .NET code can still hold the
  thread (4.1); the window did not say so yet (open point in 12.0 then; the session leaves such a run since 12.8).
- **(g) Plain-text secrets in the history.** `ShouldRemember` kept `$h = @{ Password = 'x' }; Set-Thing @h`, `-Passphrase`, `-pwd`,
  `-pass`, `-Key` with a literal and `Reset-AdmPassword user 'x'`; the audit (PS-13) had added the assignments. Fixed in 4.9
  (hashtable keys, assignment targets, more names, abbreviations, positional literals to a command named after a secret);
  `HistoryPolicyTests`: 24 lines failed before, 17 ordinary lines near the new rules stay remembered. Left, and said in the guide:
  a positional secret to a command whose name does not say so, a console program's argument, a name outside the lists.
- **(a-c, e) in the trusted mode.** `[type]::GetType('AdMng.PowerShell.Hosting.WindowRegistry, AdMng.PowerShell')`, its `Current()`, the
  context's reader and the reader's `_inner` field reached the `FakeDirectorySession`, deleted the Guest object without a change plan
  and wrote a file - what ADR-0010 decision 3 says the trusted mode allows (a protection against mistakes, not a boundary). No change;
  `EscapeTests.The_red_teams_trusted_mode_chains_are_refused_in_the_default_mode` runs the same chain in the default mode (refused, the
  object survives, no file; it fails when the test's session is switched to the trusted mode), and `LanguageModeTests` keeps a new
  session constrained after a trusted one.
- **(d) in the trusted mode.** A runspace the script made for itself (`[runspacefactory]::CreateRunspace()`, `BeginInvoke`) wrote a
  file 2.5 s later, after the run, Stop and dispose - decision 6 of 12.6 (only jobs in the session's repository are ended). No change;
  the same regression test runs it in the default mode (refused, no file after the delayed write would have happened).


### 12.8 After the second red team of R1

A second red team ran the same eight goals against the session of 12.7. Goals a-c, e and f found no way in ConstrainedLanguage
again. What it found, and what became of it:

- **(g) Plain-text secrets in the history, past the rules of 12.7.** `ShouldRemember` kept `$password = ('x')`,
  `Set-Thing -Password ('x')` (also in double parentheses), `New-Thing -ApiKey ('x')`, `$password = 'Sum' + 'mer'`,
  `Set-Thing -Pas 'x'` and `-Pa 'x'` (PowerShell binds both to `-Password`), and the chained `$password = $x = 'x'`. Fixed in 4.9:
  a value computed from literals alone (parentheses, a subexpression of one statement, an operator, a cast, a chained assignment)
  is a literal, and the abbreviations of `Password`/`Passphrase` count from two letters. `HistoryPolicyTests`: 16 of the 17 new
  lines were remembered before (the 17th, `$x = $password = 'x'`, was already caught through the inner assignment and stays as a
  guard); 8 ordinary lines near the new rules (a parenthesized user name, a sum of numbers, `-Path ('C:\dane')`, `-P 'x'`, a value
  joined with a variable) stay remembered.
- **(h) A regular expression with a time limit of its own held the window past Stop.** In the default mode,
  `[regex]::new('^(a+)+$', 'None', [timespan]::FromHours(1)).IsMatch('aaaa…a!')` (36 a's) - and the same through
  `[regex]::Matches(…, [timespan]::FromHours(1))` and `[regex]::InfiniteMatchTimeout` - ignored the 2 s limit of 12.7, which binds
  only a regular expression without a limit of its own: after Stop and the grace period the session stayed busy for good ("A
  command is already running in this window") and the thread kept a core busy for an hour, or forever. Nothing in the language
  mode can refuse a .NET argument, so the session now leaves such a run (4.1): the run ends `Abandoned`, the next command runs at
  once in a new runspace, and the window says that the variables and functions of the old one are gone (6). Found while fixing
  it: the engine's pipeline thread and the thread of `BeginStop` are foreground threads, so a left thread that never returns kept
  the process alive after `Main` returned (observed in a child process); the set-up makes the pipeline thread a background thread
  and the window stops a pipeline on a background thread of its own. `RegexLimitTests`: the red team's three scripts (with 26 a's,
  about 5 s of backtracking, so that the test run does not keep a core busy for an hour) failed before ("the window is still busy
  with the match it gave up on"), the process-exit test failed before both thread changes and with either one alone (the child
  did not end in 60 s); `PowerShellSessionTests` keeps the left run (the old variables and functions gone, the window's commands
  working, a new host), the trusted mode kept and given up under a new lockdown, dispose during such a run, and the background
  thread; `PowerShellWindowRunTests` the attention, the line in the scrollback in both languages and the trusted mode ended under
  a lockdown (each failed before the view model knew the new state). Left: the thread of a left run still uses its core until it
  returns, and trusted-mode .NET code that never returns holds whatever it holds.
- **(d) in the trusted mode: a script block left behind ended the process.**
  `$global:tmr = [System.Threading.Timer]::new([System.Threading.TimerCallback]{ [System.IO.File]::WriteAllText('C:\x.txt','x') }, $null, 1500, -1)`
  fired 1.5 s after the window was disposed; the script block found no runspace on the timer thread and threw
  `ScriptBlockDelegateInvokedFromWrongThread` unhandled, which ended the whole process (exit 134 on Linux) - on a workstation every
  connection window with it. That the timer outlives the window is the trusted mode (12.6 decision 6), and a runspace the script
  makes for itself still runs and writes; the crash is not. The program now sets the process's handler of unhandled exceptions in its
  module initializer (4.2, `PowerShellEnvironment.ContainLeftoverScriptBlocks`): exactly that error counts as handled, every other
  still ends the process. `EscapeTests` runs the red team's script in two child processes: the control, without the program's set-up,
  still crashes; the one set up as the program lives on and nothing was written - it failed before the handler (the child crashed
  with the same error). The smoke test leaves such a timer behind in the published program and `HostTests` checks the program's
  module; ConstrainedLanguage refuses the timer itself.

### 12.9 After the third red team of R1

A third red team ran the eight goals against the session of 12.8. What it found, what was found while reproducing it, and what
became of it:

- **(h) A line nested too deeply for the parser left the window busy for good.** `$x = ` followed by 5 000 `(`, `1` and 5 000 `)`
  (both language modes): from about 4 500 nested parentheses `Parser.ParseInput` gives up (`ScriptTooComplicated`, measured on Linux
  in Debug and Release) and returns no syntax tree. `InputGuard.Refusal` dereferenced it and threw a `NullReferenceException` after
  the session had counted itself as running; the flag was reset only on a refusal or a failed start, so every later command was
  refused with "A command is already running in this window", and Stop could not help (no pipeline had started).
  `ShouldRemember`, which the view model asks before the command runs, threw the same way. Now a text without a tree is refused
  (`TooComplexMessage`, 4.6) and not remembered, and the session resets the flag when the check itself throws.
- **Found while reproducing it: a chain of operators too deep to walk ended the whole program.** The parser guards its own recursion
  but parses a chain of operators in a loop, and every walk over the tree afterwards recurses once per operator - the parser's own
  checks after the parse, the engine's compiler, the history filter; a .NET stack overflow cannot be caught and ends the process, on
  a workstation with every connection window. Measured on Linux in child processes from a thread with 1 MB of stack (the default of
  a thread on Windows, the window's UI thread among them), in the default mode: `$x = 1+1+…` with 12 000 terms passed every check
  and ended the engine's compiler when it ran (about 9 300 terms are enough), with 40 000 terms the end-of-input check of Enter ended
  the process already (about 16 000 on 1 MB), and `$password = 'x'+'x'+…` with 4 000 terms the history filter (about 3 000). The
  red team had not looked further than the parentheses. `Input/InputParser.cs` is now the one parse of what was typed (4.6, 4.9): a
  text longer than 256 Ki characters is not parsed, one longer than 4 Ki is parsed on a thread with 64 MB of stack, and a tree deeper
  than 500 levels counts as no tree - about 18 times below the depth that ended the compiler; a parenthesis takes three levels, a
  script block about five, an operator of a chain one, so a command an operator types stays far below. A text without a tree is
  refused with a message that says why, not remembered and not completed; it is highlighted from its tokens unless it is too long,
  and Enter runs it so that the message is shown.
- **(g) Three ways of a plain-text secret into the history.** `'P@ssw0rd!' | ConvertTo-SecureString -As -Force` (also `-A`: both
  bind to `-AsPlainText` there, and the secret comes through the pipeline; the token rule counted a prefix from three letters),
  `$h = @{ "Pass$x" = 'P@ssw0rd!' }` (only a constant key was looked at) and `[securestring]$x = 'P@ssw0rd!'` (the variable's name
  decided alone). Fixed in 4.9: of `ConvertTo-SecureString` every prefix of `-AsPlainText` counts (also module-qualified; elsewhere
  `-As` and `-A` stay ordinary parameters), the literal parts of an interpolated key, property or index count as its name, and a
  literal cast to SecureString or assigned to a target typed so is a secret whatever the name.
- **Tests.** `EscapeTests`: the red team's line in both modes (refused, the next command runs, nothing remembered, highlighting and
  completion do not throw - it threw before); the three chains in child processes (the probe's `deep-input:<case>`) ended the process
  before with exit 134 and "Stack overflow", now each is refused and the window runs the next command, as for the deepest chains,
  unary operators, member accesses and parentheses that fit below the length limit; the same lines 400 terms long still run.
  `InputGuardTests`, `HistoryPolicyTests` and `SyntaxTests` pin the limits on both sides; `HistoryPolicyTests` adds the red team's
  11 lines and neighbours (remembered before) and 8 ordinary lines near the new rules that stay remembered.
- **Left.** The limits were measured on Linux; Windows runs the same tests in CI, also the child processes with a 1 MB thread. When no
  thread with 64 MB of stack can be made, a long text is treated as too long (refused, not remembered). In the trusted mode
  `[scriptblock]::Create` still parses any text at run time - FullLanguage, decision 6 of 12.6; ConstrainedLanguage refuses it.

### 12.10 After the audit of R1 - the trusted mode off, the state of the window, exit and cls

Audit findings PS-02 (high), W-10 and the session's part of PS-12, each reproduced before the change (the engine runs on Linux):

- **PS-02: the trusted mode did not end when it was turned off.** The switch back set the language mode of the same runspace; a
  function, an alias, a script block in a variable and a script method added to `$Host` or `$Host.UI.RawUI` in the trusted mode
  went on running in FullLanguage while the window said ConstrainedLanguage (`TrustedModeOffTests` printed "FullLanguage" for each
  of its eight calls). Now the session makes a new runspace (4.1) - the same session object, so the window makes no second request
  to the factory: the directory, the culture, the width and the actions are the session's by construction, the window's lambdas of
  the history and the editor stay valid, and there is one code path for "a new runspace" with the left run of 12.8. Every runspace
  got its own raw user interface for it (a member added to the shared one was reachable from the new runspace). Measured and left,
  because no runspace can take it back: a member the trusted mode adds to a process-wide .NET object - `[string] | Add-Member
  -MemberType ScriptMethod …`, the same on the culture of `Get-Culture` - stays callable in FullLanguage, from this window and from a
  new one, until the program closes; it is the trusted mode's .NET code (ADR-0010 decision 3) and the guide says so.
- **The window while the new runspace is made** (the auditor's six points): `IsSwitchingMode` - the Working line "Turning the trusted
  mode off: starting a new PowerShell session…" without Stop; Run, Tab (the editor's `canComplete` gate) and the switch disabled with
  the reason; the completion list and a running highlight dropped before (the text is highlighted again after); the history and the
  scrollback stay, the scrollback says that the session is new and the variables, functions and aliases are gone - and the
  confirmation of the trusted mode says so before it is turned on (turning off asks nothing: it is the safe direction); the lockdown
  is read from the session again (a new lockdown disables the switch and is said in the scrollback). When the new runspace cannot
  be made (`PowerShellSessionUnavailableException`), the window is not left trusted: it closes the session and says "PowerShell could
  not be started" as its lasting state and in the scrollback; a command after which the session has no runspace (a left run whose
  new runspace failed) ends the same way instead of failing on every command.
- **W-10: the cause of a window that cannot run is state.** `_noEngine` and `_startFailure` (the exception) replace the text of the
  start; the lasting state and `TrustedModeDisabledReason` are made when they are read - in the language of then, the reason through
  `ErrorTranslator`. A build without the engine says "This installation has no PowerShell engine. Use the standard package of AD
  Manager, which includes the engine." (no "close the window and open it again"). The switch has a reason in every state it is
  disabled in - the session ended or none, no engine, could not be started, being turned off, not ready yet, a command runs, a
  lockdown - and `CanToggleTrustedMode` is exactly "no reason". A refused switch says why in the window's words, the run failure
  "The command could not be started" goes through the translator.
- **PS-12, the session's part.** `exit` ends the run and the window says "The command ended with exit (exit code n). The window stays
  open…": closing the window would take the scrollback and the history with a script's `exit`; the dead `ExitRequested` event is
  gone. `Clear-Host` / `cls` is a compiled command of the window (4.6) and clears the output like Ctrl+L, in the order of the output
  (`PowerShellClearItem`); the view no longer reads the last item of a list cleared meanwhile. A stopped run's `HadErrors` counts
  only an error the command wrote, not the stop. Not changed here: `Out-Default` resolved by name, and the rest of W-05.
- **Tests.** `TrustedModeOffTests` (16) and `SessionEndingTests` (10) in the engine; `PowerShellWindowTrustedModeTests` (9), the
  reason of the switch in six states, the start failure and the build without the engine before and after a switch to Polish, the
  clear and the exit line in the window's tests. Not seen on Windows: the switching state and the clear in the real window (the UI
  tests run there; the smoke test counts the window's own `Clear-Host`).

### 12.11 After the audit of R1 - the defects of the engine, the window and the commands

The remaining findings of the engine (PS-05, PS-06, PS-07, PS-09, PS-10, PS-13), of the window (W-04, W-06, W-08, W-09), of the
commands (ps-commands-1 to -4 and -6 to -10) and TI-3, each reproduced before the change with the test that now pins it (the engine
runs on Linux; the WPF part only compiles here):

- **Engine.** A mandatory parameter that takes an array is asked for element by element, so a DN is one identity (PS-05, 4.3). The
  pipeline is `script | Write-AdmWindowOutput | Out-Default` with the script's own error merge, so a console program at the end of a
  command writes into the window, its error output too (PS-06, 4.4; proven with `/bin/echo` and `/bin/sh`, the same tests use
  `cmd.exe /c` on Windows). The end item is kept on the run and yielded after the channel's last item (PS-07, 4.4); the channel is
  bounded by 1,048,576 characters of text besides its 4096 items (PS-09, 4.4). A completion is stopped after 5 s, on Stop and by its
  token, and left like a run when it cannot be stopped (PS-10, 4.1, 4.9). The history filter also refuses a literal that reaches a
  secret through a variable of the line, a credential built from a literal and the passwords of console programs, and the echo of
  a command masks every such literal with `***` (PS-13, 4.9; `IPowerShellSession.HideSecrets`).
- **Window.** A growing line keeps a list and a `StringBuilder`, joins pieces of one colour, changes once per batch and ends at
  8,000 characters (W-04, 6). A DN the formatting may have wrapped is no link: at the end of a full-width line, before a comma at the
  end of a line, at the start of an indented line (W-06, 6; the document gets the session's width). A secret is never text of the
  editor's model: the password box hands a `SecureString` to `AnswerSecretCommand` and is cleared on every change of mode (W-08). A
  question keeps the command typed ahead and gives it back; the first empty answer to a choice asked over such text asks again
  (W-09). The view model no longer makes up an end item (PS-07).
- **Commands.** A failed read by identity is one error, not also "not found" (ps-commands-1). Every object carries its command's type
  name first, so mixed results are one table (ps-commands-2, 12.2 decision 10). A cell shows three levels of lists (ps-commands-3: an
  array that contains itself ended the program), and a null element is an empty row (-6). The "shown once" error is shown once per
  command (-7). The messages of the commands are a resx pair of the engine (`Resources/CommandStrings(.pl).resx`, `AdmText`, the
  culture of the session's host); the help, the engine's messages and the window's refusals of the session stay English (-8). The
  converter and the member lookup use the program's rules (`AttributeValueRules`, `DistinguishedNameBatch`,
  `DistinguishedName.KeyOf`) instead of copies (-9). Export-AdmList checks its path, `-Force` and `-Delimiter` before the pipeline is
  read (-10). Columns with secrets - the clear-text LAPS password, a BitLocker recovery password, what the schema marks confidential -
  leave the window by Export-AdmList and the grid's export only after the list export's question, and the grid's copy leaves them
  out (-4). A stopped export of the grid ends its write between two rows, deletes the part written and says so (TI-3; the one
  `File.Delete` the window's rule test allows).
- **Not seen on Windows.** The capture of a console program's output in AdMng.exe (the engine still allocates its hidden console),
  the password box and the type-ahead in the real window (a UI test covers them on Windows CI), the Polish satellite of the engine's
  resources in the single-file program.

### 12.12 A parallel job ended while its runspace opened (CI run 45315307313)

- **What happened.** In the lab job of CI run 45315307313 (commit `c0e4fab0`) the step "PowerShell engine tests (Linux)" passed
  every test (458 passed, 3 skipped) and still failed: xUnit reported a "catastrophic failure", a `NullReferenceException` without a
  test name. Under CPU load it came back in 2 of 49 runs of the suite and in about 9 % of the runs of the two job tests of
  `EscapeTests`, always with one stack, which the detailed console logger shows: `InitialSessionState.BindRunspace` ←
  `LocalRunspace.DoOpenHelper` ← `RunspaceBase.Open` ← `PSTaskPool.GetRunspace` ← `PSTaskPool.Add` ← the work item of
  `PSTaskJob.Start` on a pool thread. Not the commits of 12.8: the same race and stack were measured at `3418c59d` before them; the
  code came with the job ending of 12.6 (`c4ed6e3c`), and the long regular-expression tests of 12.8, which keep cores busy beside
  `EscapeTests`, made it likelier.
- **The mechanism.** `PSTaskJob.Start` hands the children of a `ForEach-Object -Parallel -AsJob` job to its pool in a thread-pool
  work item that catches nothing; for each child `GetRunspace` adds a new runspace to the pool's list and opens it, and
  `LocalRunspace.DoOpenHelper` marks the runspace Opened - which lets a waiting `Close` go on - before it binds the initial state
  (`BindRunspace`). The session ended the job at once when its command ended and on dispose: `StopJob` → `PSTaskPool.StopAll` →
  `DisposeRunspaces` closed that runspace, the close took its engine away, and `BindRunspace` dereferenced it. Unhandled on a pool
  thread this ends the process (exit 134 on Linux) - in the program every connection window; in the test process xUnit's handler
  reported it, held the thread until the run ended and exited, and `dotnet test` failed although no test had. The handler of 4.2
  counts only a left script block as handled, and rightly not this. The work item opens a runspace whenever a child gets a place:
  `PSTaskPool.Add` waits in `WaitHandle.WaitAny` for a free place (`_addAvailable`) or the stop signal (`_stopAll`) - a free place
  wins when both are set - and `GetRunspace` does not look whether the pool is still open; a child that ends frees its place at any
  moment. So nothing but the end of the work item's loop orders `StopAll` and `Dispose` against an open, and `Dispose` also
  disposes the two events `Add` waits on.
- **The fix (4.1).** The session ends a parallel job only once the engine's work item is done with its pool
  (`ParallelJobs.PrepareEndAsync`): it resets `_addAvailable` and sets `_stopAll`, so that the work item gives no further child a
  place and waits no more; it waits until the work item has closed the pool - `IsOpen`, which only the work item's last step and a
  stop clear; the work item has no event of its own, so the session looks every 1 ms, doubling to 100 ms - and only then stops and
  disposes the job. A child the work item is opening at that moment still starts and is stopped with the job; the children that
  waited for a place do not start, so a throttled job ends with its command too. One exception, harmless: a running child of a
  full pool that ends in the moment the work item takes to leave its loop sets `_addAvailable` again (`HandleTaskStateChanged`),
  and the work item opens the next child's runspace - before it closes the pool, so the stop does not meet that open either, and
  the child is stopped with the job. After `IsOpen` is cleared the work item only runs `CheckForComplete`, whose call of the pool's
  event catches everything. `StopJob` marks the job Stopping before it closes the pool, so a pool closed while the job still runs
  was closed by the work item; a job that something else stopped first (trusted-mode code's own `StopJob`) is neither stopped
  again nor disposed - its `Dispose` would dispose the events the work item may still wait on. These are private members of
  Microsoft.PowerShell.SDK 7.6.10 (`PSTaskJob._taskPool`, `PSTaskPool._addAvailable`, `_stopAll`, `IsOpen`):
  `The_engine_has_what_the_window_needs_to_end_a_parallel_job` fails when an update of the engine changes them, and without them
  the session waits until every child has started (a child leaves NotStarted only once its runspace is open). No child is stopped
  by itself: a first version stopped each child as soon as it ran (`BeginStop`), to free its place under the throttle limit; under
  load it left a throttled job stuck once in 40 rounds (a child stopped while the pool's thread still started it ended Stopped
  without a reason, and the next child never started). The end of the session that removes a job from the repository is the one
  that ends it, so the end of a command and a dispose at the same time no longer stop one job twice.
- **The first repair only waited.** `b89a279a` stopped the job once none of its children was NotStarted or, while some ran and
  others waited, once the running children had stayed unchanged for 10 s of the session's clock. A verifier forced its window 3 of
  3 times: a child that ended between the end of that wait and `StopAll`'s stop signal let the work item open the next child's
  runspace under the stop, with the stack of the CI run. The wait also held the later children of a job without a throttle limit,
  whose runspaces the work item opens one after another while the first child runs, and a `Dispose` between `Add`'s look at
  `IsOpen` and its `WaitAny` met disposed events. The fix above leaves no such window: the job is stopped and disposed only after
  the work item has left its loop.
- **Tests.** `EscapeTests.A_job_whose_runspace_is_still_opening_is_ended_without_ending_the_process` makes the race certain: in a
  child process set up as the program (the probe's `parallel-job-open`), a listener on the engine's `RunspaceInit` trace source
  (`ParallelRunspaceGate`) holds the pool's thread at "runspace opened successfully" - Opened, its state not bound yet - while the
  window ends the job, when its command ends and when the window closes while its command waits; the probe notes the job's state,
  lets the runspace open and notes the end. Before the fix it failed every time: the job was Stopping or Stopped while its runspace
  opened, and the child crashed with exit 134 and the stack of the CI run. Now the job stays Running until its child runs, then
  ends without writing, and the process survives. `A_job_of_several_children_is_ended_without_a_runspace_opening_under_its_stop`
  does the same for three children (the probe's `parallel-job-children`, with a session clock moved on by an hour every 20 ms, so
  that whatever the session waits for on its own clock has passed): in `later-child` the gate, which can now skip opens, holds the
  second child's runspace when the command ends; in `child-ends-at-stop`, with a throttle limit of 1, the first child ends by itself
  inside the job's Stopping event - `StopJob` raises it before it stops the pool - with the gate ready for the second child's
  runspace. Against the first repair and against the code before it, 3 of 3 runs each failed: the job was Stopping while the second
  runspace opened, the stop met an open runspace, and the child crashed with the stack of the CI run. Now the job stays Running while
  the runspace opens, the stop meets a closed pool, the children that waited stay NotStarted, nothing is written, and the process
  survives. The probe reads what the stop met only once the Stopping handler has decided it: the engine marks the job Stopped on
  the first child's thread as soon as that child ends in a closed pool (`HandleTaskPoolComplete`), while the stopping thread may
  still be between two looks - read at the job's end, the probe said "none" in 3 of 19 suite runs and 5 of 61 runs of the probe
  alone, although the stop had met a closed pool; waiting for the decision, 120 of 120. A failure of the test shows the whole
  report.
  `A_job_of_several_children_ends_with_its_command` and
  `A_throttled_job_ends_with_its_command_and_its_waiting_children_never_start` run in the test process (the children that waited
  never start; the first child sleeps for 30 s, so none frees its place), and
  `The_engine_has_what_the_window_needs_to_end_a_parallel_job` pins the members. The job scripts of these tests, of the probe and
  of the audit's two job tests stop on their first error (below).
- **Stress.** Under CPU load (two busy loops beside the runs and another build and test run on the 4 cores; load average 10-50),
  with the CI's crash dump settings: before any repair the audit's two job tests ended in xUnit's catastrophic failure in 3 of 80
  runs (at `7dd82299`) and 4 of 60 (a verifier), the suite in 2 of 49 (at `c0e4fab0`, while reproducing it) and 1 of 20 (at
  `76eb8d16`; 0 in 20 more), each run of the suite leaving the dump of the deliberate crash. The first repair (`b89a279a`) went
  through 58 runs of the suite without a catastrophic failure - its window needed the forced order above. With the repair here
  the suite ran 25 times through `dotnet test`, as the CI step runs it, and 10 times through the test executable (load 13-45):
  no catastrophic failure, exit code 0 every time, no dump and no crash report; the probe of several children ran 120 times alone.
  The version with `BeginStop` above ran the suite 30 times without a catastrophic failure, but its own throttled test failed in
  2 of them.
- **The deliberate crash left a dump in every lab run.** The control child of 12.8 (`leftover-callback-control`) crashes on purpose
  and inherited the CI's `DOTNET_DbgEnableMiniDump`, `DOTNET_DbgMiniDumpType`, `DOTNET_DbgMiniDumpName` and
  `DOTNET_EnableCrashReport`, so every lab run left a 25 MB dump and a crash report of `AdMng.PowerShell.Probe.dll` for the step
  "Crash reports", beside - and hiding - a crash that is not meant. `ChildProcess.RunAsync(…, crashDumps: false)` starts that child
  without them (with the `COMPlus_` names and `EnableCrashReportOnly` too); every other child keeps them, so a crash that is not meant
  still leaves its report. `A_child_that_is_meant_to_crash_gets_no_crash_dump_or_crash_report_settings` checks what a child got (the
  probe's `environment`) against a simulated CI environment, and that a child not meant to crash keeps it.
- **Left.** A child stuck in .NET code that ignores Stop holds the thread that stops the job, and its pipeline's foreground thread
  keeps the process alive until it returns, as before; a runspace open that never ended would keep the session looking at the pool
  every 100 ms and the job running. A child that the stop meets while the engine still loads the module of its first command (here
  `Start-Sleep`) gets a CommandNotFoundException - a statement-terminating error - and the engine runs its next statement before
  the stop ends the script: the engine's Stop for any script (11 of 60 rounds of three children stopped at once wrote their file; with
  `$ErrorActionPreference = 'Stop'` in the job script, as the tests have it now, none of 100). The race is the engine's (`PSTaskPool`
  orders neither `StopAll` nor `Dispose` against `GetRunspace`, and the work item catches nothing) and can be reported upstream; the
  session's way round it rests on private members, which a test checks against the pinned version. Trusted-mode code that stops
  such a job itself (`$job.StopJob()`) at that moment still ends the process - FullLanguage, decision 6 of 12.6 - and the session
  leaves a job stopped that way as it is.

### 12.13 After the audit of R1 - script block logging left to Windows and Group Policy (D-B)

Audit finding PS-04 (medium): the program's `powershell.config.json` set `EnableScriptBlockLogging: false`, so a security team
saw no script block (4104) of what was typed in the window unless a Group Policy turned script block logging on - and, as the
engine's code shows (4.2), not even the script blocks the engine logs on its own when nothing sets the policy. The owner's
decision D-B: AD Manager does not touch script block logging; Group Policy decides, then the user's own file. The transcription
and the module logging stay off in the program's file (a transcript is directory data on disk, rule 1).

- **The file.** No `ScriptBlockLogging` section. `EngineBoundaryTests` pins it (no section of that name in any case, transcription
  and module logging still off); `ScriptBlockLoggingTests` shows what the engine then does (4.2). Both failed on the old file.
- **The reader.** Unchanged - it already mirrored the engine. `PowerShellPolicyEngineRulesTests` now reads the shipped file from
  the repository instead of a copy: alone it decides the transcription and the module logging and leaves script block logging
  `NotSet`; a user's file then turns script block logging on (the note says "your powershell.config.json") but not the other two; a
  Group Policy that turns it off wins over the user's file. The smoke test expects the program's file never to decide script block
  logging.
- **The note.** No sentence for the engine's default: the note says what the **policies** of the workstation record (D7), with
  facts the window reads; which lines the engine logs on its own when no policy is set is the engine's choice, not a setting of
  the workstation, and they reach an event log only where the PowerShell 7 event provider is registered, which the window does
  not read - a sentence would have to say "may", show on nearly every workstation, and dull the note that must stand out when a
  policy writes directory data to disk. Nothing the window says claims that nothing is recorded: without a recording policy it
  shows no note at all. The guide (chapter 8), the README and the ADR say that the engine logs some lines on its own.
- **Not seen on Windows.** The warning 4104 of a suspicious block in *PowerShellCore/Operational* on a workstation with PowerShell
  7 installed (the child-process test runs on Windows CI too, where it is skipped if the machine sets the policy itself).
- **Four verification rounds** (against the engine's code, a replica of `Match` and the real engine) found, one after another, that
  the docs' description of what the engine marks was wrong or incomplete: the parser's marks (`$u.$a`…) beside the word list; the
  words with a digit, which are never found as written (`FromBase64String`); the last letters of a text read across other
  characters (`'grace mit'`); a word anywhere inside a run, also inside an account name (`jmarshall`); a nested block marked on its
  own. Along the way `ScriptBlockLoggingTests` grew to twelve lines, asserts on Linux instead of skipping, and gives the program's
  file a user's file that says on; the docs learned where the event provider comes from (the MSI by default, not the zip
  or MSIX) and the differences from `pwsh.exe` (the file of its `$PSHOME`, a user's file that turns on transcription or module
  logging, the console-host and pipe events).
- **After the fourth verification.** It found more corners: `Match` hashes ASCII letters only (`Get-AdmUser emłit` ends in
  `Emit`) and compares 32-bit hashes, so a text without any listed word can be marked (`Get-AdmUser mc-earraytype` -
  `MakeArrayType`); the braces of `if` and `foreach` are a `StatementBlockAst`, checked only as part of the block around them;
  the window's `AddScript` path has no cache of compiled blocks, so a marked line typed again is logged again (4.2 said the
  opposite); module logging does not log the window's own commands as commands of a module. The decision: the user docs (the
  README pair, chapter 8 of the guide pair, the ADR pair, the release notes and PW12 of both pilot docs) no longer describe the
  engine's algorithm - each precise sentence had turned out wrong somewhere. They say what is robust: the program does not set
  script block logging, Group Policy and then the user's own file decide, and with it on every command is logged; without a
  policy the engine still logs, as a warning 4104, the lines its built-in check finds suspicious - its own choice, which can
  include ordinary lines - so every line is treated as possibly logged and no secret is typed in plain text; the events reach a
  log only where the provider is registered; the known differences from `pwsh.exe` are listed once, in chapter 8 of the guide.
  PW12 tests only what is robust: without a policy `Write-Output 'Add-Type'` is a warning 4104, with the Group Policy on every
  line is logged. 4.2 keeps the technical detail, each sentence checked against the code and the real engine: a scratch program
  ran the test's twelve lines and the new examples (`emłit`, `mc-earraytype`, the nested `if` and `$global:sb` blocks, a marked
  line twice through `AddScript` - logged twice, the compiled blocks not shared - and twice through `[scriptblock]::Create` -
  one shared block, logged once), and the window's real `PowerShellSession` with a listener on `/dev/log`, which receives the
  engine's events on Linux: with nothing set, `Write-Output 'Add-Type'` run twice gave two warnings 4104 and
  `Get-AdmUser jkowalski` none; with the setting on (and `"LogLevel": "Verbose"`, so that verbose events reach syslog) every line
  gave a 4104 (a verbose one, and a warning for a line the engine marks, as without the setting); with module logging for `*`, the 4103 events of 4.2 (the outer pipeline with `Out-Default` for every line,
  `CommandInvocation(Get-AdmUser)` without parameters beside `Select-Object`, nothing of the window's `ForEach-Object` itself).
  The comments of `ScriptBlockLoggingTests` and of the probe say the same.

### 12.14 The Windows CI of the audit fixes - tests that counted on the runner

The fixes of the audit were pushed without waiting for the Windows CI job; its runs (and one of the Linux lab job) failed in tests
that counted on how the runner behaves. What failed, and what became of it:

- **A fixed wait let the control of the leftover script block survive.** The control child of 12.8 (`leftover-callback-control`)
  set the timer of 1.5 s, disposed its session, waited a fixed 4 s, wrote "survived" and returned 0. A runner that stalls a process
  for 8-20 s under load lets both come due at once: on Linux (CI of f219a3e7) the child wrote "survived" before the crash, on Windows
  (CI of ea9c4c5d and 7cc6191b) it printed the crash and still exited 0 - the process was already ending. Reproduced here by
  freezing the child (`SIGSTOP`) 1-2 s after its start for 8 s: six of six runs wrote "survived" and exited 0 without the crash;
  not frozen, it crashes about 2.4 s after its start. The contained child had the same flaw, unseen: it passed whether or not its
  callback had run (frozen the same way it reported "survived" with nothing contained). Now neither child waits a time of its own:
  the control waits up to a minute for its crash and only then reports that it survived; the contained child waits until its
  handler has counted the callback (`PowerShellEnvironment.ContainedLeftoverScriptBlockCalls`, internal, counted by the handler of
  4.2) and reports the count, which the test expects to be 1. The probe also holds the timer, so that a collection of the closed
  window's variables cannot cancel it before it fires. Frozen the same way, the control crashed and the contained child reported
  `contained=1` in every run.
- **The positive control of the "no files" test expected a file the Windows engine does not write.** On the Windows runner (CI of
  ea9c4c5d, 151aff46, 7e13cff6 and 7cc6191b) the probe's own file was seen in the engine's cache folder, but `telemetry.uuid` was
  not, and the test failed. The Windows build of the engine (decompiled 7.6.10, the static constructor of
  `Microsoft.PowerShell.Telemetry.ApplicationInsightsTelemetry`) sends telemetry - and only then writes the identifier - when, besides
  the opt-out variable, Windows' diagnostic data setting allows it (`WindowsDataCollectionSetting.CanCollectDiagnostics`, the WinRT
  `PlatformDiagnosticsAndUsageDataSettings`), which is off on the runner; the Linux build has no such check. The control now reports
  the engine's `ApplicationInsightsTelemetry.CanSendTelemetry`; the test expects `telemetry.uuid` only when it is true (on Linux it
  must be, with telemetry on in the child's environment) and, when it is false, that no `telemetry.uuid` appeared; the probe's own
  file shows on both systems that the test watches the engine's cache folder. Here the test failed before the probe reported the
  setting (the report did not say) and passes with it (`CanSendTelemetry` true on Linux).

### 12.15 0.16.1 - the lab tests of the read commands among the engine's tests

`PowerShellReadLabTests` (the five lab tests of 12.2, unchanged in what they assert) moved from
`tests/AdMng.Directory.IntegrationTests/Integration` to `tests/AdMng.PowerShell.Tests/Lab` (audit note
note-directory-it-powershell-ref): the tests of the directory library carried the whole engine for this one class. They no longer
reference `AdMng.PowerShell` or import `PowerShellEngineConsumer.props`; the engine's tests reference `AdMng.Directory` for the
real session instead. The tests are skipped without `ADMNG_TEST_LDAP_SERVER`. The CI lab job sets the lab variables for the whole
job, so its step "PowerShell engine tests (Linux)" runs them; the Windows job has no lab and skips them.

- They have a class fixture of their own, `PowerShellLabFixture`: this run's OU `OU=AdMngPowerShell-<UTC time>-<random>`, with
  the run-OU and cleanup code of the directory tests (`LabRuns`, `LabCleanup`) as linked files, as
  `AdMng.Watchdog.IntegrationTests` has them.
- The namespace `AdMng.Directory` (from the reference and from the linked files) hides `System.IO.Directory` inside
  `AdMng.PowerShell.Tests`, so the engine's tests write `System.IO.Directory`, as the directory and Watchdog lab tests do.
- `EngineBoundaryTests` expects the engine project to be referenced by `AdMng.App`, `AdMng.PowerShell.Probe` and
  `AdMng.PowerShell.Tests` only; it failed while the directory tests still referenced it.
- Against the Samba lab here the five passed and the fixture removed its run OU (13 objects).
