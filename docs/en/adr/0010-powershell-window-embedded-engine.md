**English** · [Polski](../../adr/0010-okno-powershell-osadzony-silnik.md)

# ADR-0010: The PowerShell window – an embedded PowerShell 7 engine (track R)

- Status: **Accepted** (2026-11-16). Decisions of the project owner, to whom the team delegated the matters of the "To be
  agreed with the team" section of track R and the names of the commands. They concern stage R1 and bind stages R2 and
  R3; in the README of track R the settled entries moved to the section "Decided"
- Date: 2026-11-16
- Applies to: track R in the [README](../../../README.md#track-r--powershell-window--r1-); rules 1–6 of `CLAUDE.md` stay
  unchanged (the PowerShell window works within them and is no exception to any)
- Implementation note: [`docs/dev/r1-powershell-window.md`](../../dev/r1-powershell-window.md) (binding for stage R1;
  its "R1 as built" part records what was actually built and what could not be verified)

## Context

Track R adds a PowerShell 7 window next to every connection window: ad hoc queries, AD data dumps and scripts without a
separate console, without RSAT and without signing in again. AD Manager's commands (`Get-Adm…`) read through the
window's `IDirectorySession` – the same DC, the same credentials, filters built by `LdapFilter` – and the results are
ordinary PowerShell objects that can be opened in the program's grid. Writes never go straight to AD: stage R2 will add
them to the window's change plan (rule 5), stage R3 will add the RSAT modules.

The README left six matters to be settled "with the team": the package size and the choice of an embedded engine, the
language mode without WDAC/AppLocker policies, whether the window may load any modules and scripts from disk, the list of
translated RSAT commands, the window's credentials for the `ActiveDirectory` module, and the names of the commands and the
keyboard shortcut. The owner settled the first three, the names and the shortcut, and every detail of stage R1 (the
choice of the engine, the rules of the host, the line editor, the PowerShell policies from GPOs, the files on disk). Two
matters concerning R3 – the list of translated RSAT commands and the credentials for the `ActiveDirectory` module – stay
open.

Constraints: AD Manager is a WPF application on .NET 10 (one application, no service and no installed PowerShell
required to work), rules 1–6 apply (above all rule 1 – no local directory data, rule 2 – no stored passwords, rule 4 – one
DC per session and rule 6 – the UI never blocks), and the package with the engine goes to the application only, never to
the Watchdog package.

## Decision

1. **The engine (D1): an embedded PowerShell 7 from the `Microsoft.PowerShell.SDK` package** (the LTS line 7.6.x; built
   on 7.6.10, `net10.0`) in a new project `src/AdMng.PowerShell` (`net10.0`), used by `AdMng.App` only and never shipped
   in the Watchdog package – like `AdMng.Remote`. Version 7.7 targets .NET 11, so we will move to it together with the
   platform upgrade. The package size (+19…54 MB) and an AD Manager release with each PowerShell security update are
   **accepted**. If the engine cannot run in the single-file `AdMng.exe` (the SDK needs its module manifests beside the
   host), the engine's files will be published in a folder beside `AdMng.exe`; the smoke test in Windows CI decides (part
   4 of R1) – **it decided: the engine runs in the single-file `AdMng.exe` and no folder is needed** (section "The publish
   layout"). The reason: only an in-process engine lets AD Manager's commands use the window's own session (the same DC
   and account, no second sign-in), hand objects to the program's grid and work with our own line editor; an external
   `pwsh.exe` gives none of these (section "Alternatives considered"). The price: code that runs in FullLanguage runs in
   AD Manager's process and can reach its memory, the window's session and credentials included (decision 3); only an
   engine out of process with a read bridge would not – the audit of R1 recommends weighing that again before R3 puts a
   credential into the session.
2. **One runspace per window (D2).** A runspace is never shared between connection windows; it is created and disposed
   with the window's DI scope (`docs/dev/multi-window.md`). A custom `PSHost` / `PSHostUserInterface` routes text, prompts
   and confirmations into the window. **The session's commands are an allow-list** – one list in the code
   (`src/AdMng.PowerShell/Guards/CommandGuards.cs`), compared with a live session by a test: AD Manager's commands, the
   engine's cmdlets for objects, formatting, variables, help and reading files, four replacements with fewer parameters
   (`ForEach-Object` without `-Parallel` and `-AsJob`, `Tee-Object` into a variable only, `New-Object` without COM,
   `Start-Transcript` only to a file named with `-Path`) and, for names typed out of habit, refusals with a message
   (`Start-Job`, `Import-Module`, `Invoke-Command`, `Invoke-Expression`, `Get-Credential`, `Start-Process`,
   `Out-GridView`, `Export-Csv`, `Export-Clixml`, `Out-File`, `Set-Content`, `Add-Content`). There are no jobs, parallel
   runspaces, remote sessions, events, debugger, help updates, policy changes or commands that write a file (only
   `Export-AdmList` and `Start-Transcript -Path` write one). What reaches the engine without a command name is refused
   from the text before it runs: `using module` / `using assembly`, the background operator `&` and redirection to a file;
   so is, as a whole, a text too long or nested too deeply to walk safely (a .NET stack overflow in the parser or the
   engine's compiler would end the whole program). Script files do not run: the session lists none, a lookup hook drops one found from a script block, and on Windows the
   window's authorization manager refuses every `.ps1` and `.psm1` whatever the execution policy says (it stays
   `Restricted`; on Linux, where the window is not shipped, the engine asks no authorization manager). No job outlives the
   command that started it: a job left in the session is stopped when the command ends, on Stop and on dispose. No
   profiles are loaded (`$PROFILE` is never read), there is no PSReadLine, module auto-loading is **off**, and
   `Import-Module` and `using module` work only for the modules on a list, which is **empty** in R1 (RSAT comes with R3,
   the Microsoft 365 modules with track C). The reason: the window should do what the program knows, not what a profile, a
   module in `PSModulePath` or a script on disk brought in, and it must not offer a way to run code outside its language
   mode or past its own life – the audit of R1 found `ForEach-Object -Parallel` running FullLanguage in a constrained
   window and `-AsJob` jobs outliving it, when the session was the engine's default minus two names.
3. **Language mode (D3): ConstrainedLanguage by default.** AD Manager's commands work in it because they are compiled
   program code. The window's switch "Trusted mode (FullLanguage)" is turned on by the operator after a confirmation that
   says what it allows (any .NET code, writes around the change plan); the mode is shown in the window title and in a
   lasting note for as long as it is on, and it is never remembered between windows or sessions. With a system lockdown
   (WDAC / AppLocker, `SystemPolicy.GetSystemLockdownPolicy`) the window stays in ConstrainedLanguage and the switch is
   disabled with the reason. In ConstrainedLanguage no command of the window runs code in another runspace or language
   mode. The trusted mode changes the language mode only: it adds no command, and its .NET code runs in AD Manager's process,
   where it can reach anything – the window's directory session and its credentials included. **This is a protection
   against mistakes, not a security boundary** (README, "Security and data"): in FullLanguage any .NET code bypasses the
   change plan, and the operator can run `pwsh.exe` anyway. **Turning the trusted mode off gives the window a new
   runspace** in ConstrainedLanguage (with a new host; the old one is closed): a function, an alias, a script block in a
   variable or a member of `$Host` defined in FullLanguage used to go on running in FullLanguage in a window that said
   ConstrainedLanguage (audit of R1, PS-02). The window's variables go with the old runspace; the question when the mode is
   turned on and the window when it is turned off say so. When the new runspace cannot be made, the window is not left in
   the trusted mode but says that PowerShell could not be started. A thread, runspace or timer that such code leaves
   behind keeps running after the mode is turned off and after the window closed, and a member it added to a .NET type or
   object of the whole process (`Add-Member` on `[string]`) can be called in FullLanguage from a constrained window too –
   until the program closes; a script block such a timer calls after the window closed has no runspace and ends with an
   error that the program counts as handled (the process's handler of unhandled exceptions, set when the program starts),
   so it does not close the program or its other windows.
4. **Names and shortcut (D4): the prefix `Adm`.** R1 builds `Get-AdmObject`, `Get-AdmUser`, `Get-AdmGroup`,
   `Get-AdmComputer`, `Get-AdmGroupMember` (`-Recursive` for nested membership), `Search-AdmObject`, `Out-AdmGrid`,
   `Open-AdmObject` (opens an object's properties in the program by DN or `objectGUID`) and `Export-AdmList` (CSV with
   formula protection as in the list export, only to an explicit path or the *Save As* dialog). The names of the write
   commands in the README stay reserved for R2. The window's shortcut is **Ctrl+Shift+P** (no existing binding uses it);
   the menu entry is *Tools → PowerShell…* and the command palette gets an entry.
5. **The line editor (D5): our own WPF control without a new third-party dependency.** Completion through the engine's
   `CommandCompletion.CompleteInput` in the window's runspace (commands, parameters, variables; paths and files are **not**
   offered in R1) plus parameter completers on AD Manager's commands (attribute names from the session's in-memory schema,
   DNs from what the session already knows – never a new directory read per keystroke); highlighting from the parser tokens
   (`Parser.ParseInput`); multi-line input (Shift+Enter inserts a line, Enter runs a complete command, Ctrl+C and the Stop
   button cancel the pipeline); the command history lives **in the window's memory only** (arrows, Ctrl+R); a line with a
   plain-text password (`-AsPlainText`, `ConvertTo-SecureString '…' -AsPlainText`, a parameter whose name contains
   *Password* with a literal) is not added to the history.
6. **Output (D6).** Results are ordinary PowerShell objects; the window renders them with the PowerShell formatting engine
   (`Out-String`) as text with the DNs recognised and made clickable (*Open in the program*). Errors, warnings and verbose
   messages use the window's colours and the status line's glyphs. `Out-AdmGrid` sends the objects to the program's object
   list grid: the objects become rows, the columns come from their properties, and the grid's sorting, filters, copying and
   CSV export work. Nothing is written anywhere unless the operator exports.
7. **PowerShell policies from GPOs (D7).** When the window opens it reads the PowerShell policies by the engine's own
   rules: PowerShell Core (`HKLM`/`HKCU\Software\Policies\Microsoft\PowerShellCore`) and, for a policy whose own
   `UseWindowsPowerShellPolicySetting` is on, Windows PowerShell (`Software\Policies\Microsoft\Windows\PowerShell`) –
   transcription (`EnableTranscripting`, `OutputDirectory`), script block logging, module logging and `ExecutionPolicy`,
   each from the first hive that sets it, then the `powershell.config.json` files – and the lockdown state. A lasting note (the status line's Attention) before the first command says plainly what is logged where
   ("transcription is on by policy: commands and results are written by Windows to <directory>"). The program does not try
   to get around any policy. The registry reader sits behind an interface (the Windows implementation in `AdMng.App`, a
   fake in tests).
8. **Nothing on disk, passwords, one DC, the UI never blocks (D8).** The window's content, results and history live in
   memory only (rule 1). The engine's environment is set **before** its first use: `POWERSHELL_TELEMETRY_OPTOUT=1`,
   `POWERSHELL_DIAGNOSTICS_OPTOUT=1`, `POWERSHELL_UPDATECHECK=Off`, module auto-loading off and the module analysis cache
   pointed at a null device; a "no files" test runs commands in a process whose `HOME`, `LOCALAPPDATA`, `TEMP` and `XDG_*`
   point at an empty folder and asserts that the folder is still empty – on Linux that covers every folder the engine uses;
   on Windows the engine finds its folders through the known-folder API, which the variables do not move, so there the test
   also compares the user's PowerShell folders before and after the run. On Windows the engine writes two files of its own
   accord that cannot be turned off: its AppLocker check writes `__PSScriptPolicyTest_<random>.ps1` and `.psm1` (a comment
   line, no directory data) into `%TEMP%` and deletes them at once, once per process; the test allows exactly these. In ConstrainedLanguage no script reaches the
   window's session or its credentials: no variable, parameter or member of `$Host` carries them (AD Manager's commands
   find their window in a registry of the engine, keyed by the runspace), and R1 creates no `PSCredential` (rule 2); in
   the trusted mode .NET reflection can (decision 3). Filters are built with `AdMng.Core.Ldap.LdapFilter` and DNs with
   `DistinguishedName` (rule 3). The commands read only from the window's `IDirectorySession` – one DC, no `-Server` and
   no `-Credential` (rule 4). Every directory call is asynchronous with the pipeline's cancellation token
   (`StopProcessing` cancels it); results are streamed and paged, and 100,000 results do not block the window (rule 6). Stop
   does not interrupt a match of a regular expression, and `-match`, `-replace`, `-split`, `switch -regex`, `Select-String`
   and `[regex]` work in ConstrainedLanguage, so before the first regular expression of the process (the program's module
   initializer) the program gives every regular expression without a limit of its own a time limit of 2 s: a pattern that
   backtracks catastrophically ends as an error of its command instead of holding the window and a processor core. A pattern
   a script gives a longer limit of its own (`[regex]::new('…', 'None', [timespan]::FromHours(1))`, allowed in
   ConstrainedLanguage) and the trusted mode's .NET code can still get stuck: a command that has not ended 5 s after Stop is
   left by the session to its thread (a background thread, which does not hold up the closing of the program), and the next
   command runs at once in a new runspace, without the old one's variables and functions, and the window says so.
9. **Writes (D9): none in R1.** There is no `Set-`/`New-`/`Remove-`/`Move-`/`Rename-`/`Add-`/`Enable-`/`Disable-`/`Unlock-`/
   `Reset-Adm*` command, no `AD:` drive and no RSAT; a test asserts that the registered AD Manager command list contains
   only the read commands of item 4 (and `Get-AdmVersion`). The window has a place for the pending plan that R2 adds.

## Consequences

- **Size.** The engine package with its dependencies makes the program about 19 MB larger (single file with compression)
  or about 54 MB (without compression); a measurement of a single-file win-x64 publish made on Linux (so not run) gave
  +17.0 MB and +48.4 MB (empty programs: 37.6 MB and 73.5 MB). The first runspace starts in about 0.4 s and each next one in
  about 10 ms; managed memory grows by about 2 MB and the process working set by a few tens of MB (the engine's assemblies
  being loaded) – the measurements are in the implementation note. This cost was accepted by the owner.
- **A release with every security update.** Each PowerShell fix (roughly once a month) means a new version of
  `Microsoft.PowerShell.SDK` in `Directory.Packages.props` and a new AD Manager release. The NuGet audit (`NuGetAudit`, mode
  `all`, warnings as errors) stops the build on a known advisory in the package anyway. LTS 7.6 is supported until November
  2028; 7.7 requires .NET 11, so a change of the line goes together with a change of the platform.
- **EDR and SOC.** EDR systems may report "a PowerShell engine in a process other than `powershell.exe`" (e.g. a Sigma rule
  for `System.Management.Automation.dll`). The answers are the signed `AdMng.exe` (phase 7, certificate from P2) and
  information for the security team; documentation for them is part of R1. AMSI checks every script block, and script
  block logging and transcription from GPOs cover the window – hence the note before the first command (item 7).
- **The security boundary.** ConstrainedLanguage, the allow-list of commands, the empty module list and the refusal of
  script files protect against mistakes, not against a malicious operator; the only boundary is the account's rights in AD. Changes made
  around the plan have no audit and no rollback in the program, but the Watchdog sees them like any other change in AD.
- **Process state.** The engine's environment and the execution policy are process settings: on Windows
  `ISS.ExecutionPolicy` sets the *Process* scope policy, which according to the PowerShell documentation is stored in the
  `PSExecutionPolicyPreference` environment variable of the AD Manager process (not verified on Windows in the first part
  of R1), so processes started later inherit it; AD Manager starts no PowerShell process. On Linux `ISS.ExecutionPolicy`
  is not supported and is not set (the window is only a test environment there).
- **The engine runs on Linux.** The engine project and its tests run and are tested on Linux and Windows; the WPF window
  only on Windows. As a result all the logic (the host, the language mode, the history, completion, the commands) has unit
  tests outside the UI.
- **Next stages.** R2 will add the write commands through the change plan (the window has a place for it) without changing
  this decision; R3 will add the RSAT modules through the module list of item 2. A `powershell.config.json` file beside
  `AdMng.exe` (turning off transcription and logging set in the users' files) is part of the publish layout (section "The
  publish layout").

## The publish layout

The result of part 4 of R1, checked in Windows CI (the step "Smoke test published app" of the published single-file `AdMng.exe`):

- **The engine runs in the single-file, self-contained `AdMng.exe`.** The smoke test creates a runspace, runs `1 + 1` and
  `Get-AdmVersion`, checks the language mode (ConstrainedLanguage), the execution policy (`Restricted`), the list of the ten
  commands, the refusal of `Import-Module` and the help of a command (since the fixes after the audit of R1 also the number of
  the session's commands against the allow-list and the window's `ForEach-Object`); the first runspace of the process takes about 0.5 s
  (523 ms), the second about 10 ms (9 ms). The `System.Management.Automation` assembly is inside the file; the engine, finding
  no location of it (`Assembly.Location` is empty), takes the folder of the program for `$PSHOME` (observed: `$PSHOME` was the
  folder of `AdMng.exe`). **A folder with the engine's files beside the program is therefore not needed** and is not used; the
  fallback of item 1 stays described in case the engine changes.
- **Only the files .NET does not put into the single file land beside `AdMng.exe`:** the engine's native libraries
  (`pwrshplugin.dll`, `PowerShell.Core.Instrumentation.dll`, `getfilesiginforedist.dll`,
  `microsoft.management.infrastructure.native.unmanaged.dll`), `build.manifest(.sig)`, the module manifests
  (`runtimes/*/lib/net10.0/Modules`; nothing loads them – the default session state binds the commands directly; the release
  zip takes the top-level files of the publish folder only, so it has none of them, and the release workflow runs the smoke test on
  the unpacked zip), the help of
  the commands (`AdMng.PowerShell.dll-Help.xml`, found through `AppContext.BaseDirectory` because `Assembly.Location` is empty in a
  single file) and **`powershell.config.json`**, which turns off transcription, script block logging, module logging and the
  Windows PowerShell 5.1 compatibility layer. The engine reads it from `$PSHOME`, and a setting there wins over the same
  setting in a user's file (checked on Linux in part 1, and by the smoke test of the published single file, which also asserts that
  `$PSHOME` is the folder of `AdMng.exe`); the computer's policies (GPO) take precedence over both, and the window
  says what they record before the first command.
- **Size** (MB = 10^6 bytes; the same publish command as CI, `-r win-x64 --self-contained -p:PublishSingleFile=true`, run on Linux for
  the commit of 0.16.0 and for the head of R1; CI prints the same 173.5 as "MB", which is MiB). `AdMng.exe` (one file, uncompressed):
  143.5 MB without the engine, 181.9 MB with it, that is **+38.4 MB**; the whole publish folder 164.7 MB → 204.3 MB (**+39.6 MB**);
  the CI artifact archive 68.8 MB → 82.4 MB (**+13.6 MB**, measured by CI). The measurement of part 1 (empty programs, +48.4 MB
  uncompressed) was higher, probably because the program already has part of what the engine brings (the .NET libraries are in the file once).
- **The Watchdog package has no engine.** No Watchdog project references `AdMng.PowerShell` or `Microsoft.PowerShell.SDK` (test
  `EngineBoundaryTests`), the scripts of the Debian package do not know it, and CI checks that the publish output of the
  Watchdog has no `System.Management.Automation*.dll`. The publish output of AD Manager has no `sni.dll`.

## Package licenses

The package `Microsoft.PowerShell.SDK` 7.6.10 is licensed under **MIT**. The closure of its dependencies (85 packages
according to `project.assets.json` of the engine project, restored on 2026-11-16) was checked against the requirement of
`CLAUDE.md` (an OSI-approved license compatible with Apache-2.0):

- **MIT:** 79 packages (the `System.Management.Automation` and `Microsoft.PowerShell.*` families, `System.*`,
  `Microsoft.Extensions.*`, `Newtonsoft.Json`, `Humanizer.Core`, `JsonSchema.Net` and others).
- **BSD-2-Clause:** `Markdig.Signed` 0.44.0 (Markdown rendering in `Show-Markdown`) – OSI-approved, compatible.
- **MIT (the corefx license address):** `runtime.native.System.Data.SqlClient.sni` 4.4.0 – a package with no binaries.
- **The Microsoft .NET Library license (not OSI-approved, but with a right of redistribution):** the `runtime.win-x64`,
  `runtime.win-x86` and `runtime.win-arm64.runtime.native.System.Data.SqlClient.sni` 4.4.0 – the native `sni.dll`, needed
  only by `System.Data.SqlClient`, which the SDK package pulls in. No AD Manager command and none of the PowerShell
  commands the window offers uses it. **Decision:** these packages are excluded from the build and publish output
  (`ExcludeAssets="all"` in the `.csproj` of every program that uses the engine; the shared file
  `src/AdMng.PowerShell/PowerShellEngineConsumer.props`), and a test in `AdMng.PowerShell.Tests` asserts that the output has
  no `sni.dll`. Consequence: a script in trusted mode that connects to SQL Server through `System.Data.SqlClient` will not
  find the native library. This departs from the assumption "everything is MIT" and was reported to the owner.
- **A Microsoft license (a file):** `Microsoft.Management.Infrastructure.Runtime.Win` 3.0.0 has been in the program since
  K1 (`AdMng.Remote`); this decision does not change that.

## Alternatives considered

1. **`pwsh.exe` out of process in a terminal (ConPTY), with PSReadLine.** A smaller program, the full comfort of a console and
   no line editor of our own, but no access to the window's session and credentials (a second sign-in would be needed), no
   objects in the program's grid and no control over what goes to the PSReadLine history and to disk. Rejected.
2. **Windows PowerShell 5.1.** It does not run on .NET 10, needs an installed system component and cannot be embedded in a
   .NET 10 process. Rejected.
3. **FullLanguage by default.** Any .NET code, including writes around the change plan, would be available from the first
   command; ConstrainedLanguage with a trusted-mode switch gives the same abilities at the operator's explicit request.
   Rejected.
4. **One runspace for all windows.** The state of one window (variables, `$PSDefaultParameterValues`, the session) would leak
   into another connection, and the runspace could not be disposed with the window. Rejected; one window = one runspace.
5. **Any modules and scripts from disk** (`Import-Module`, `.ps1` files, the user's module folder). Rejected in favour of
   the module list (empty in R1) and the refusal of script files by the window itself (decision 2), whatever the execution
   policy: the window should behave the same on every workstation.
6. **Our own command language instead of PowerShell.** It would avoid many issues but would give no object pipeline, no
   `Where-Object`/`Select-Object`/`Group-Object` and none of the scripts administrators already use. Rejected.
7. **A new third-party package for the line editor** (e.g. a code editor control). A new dependency with a license to check
   and maintain, while the needed features (highlighting from tokens, completion, multi-line input) can be built on what
   WPF and the engine already have. Rejected; a package only if a stage proves it unavoidable, with its license in the ADR.
8. **The engine's default session state with a few names removed** (a deny-list: `CreateDefault()` without `Start-Job` and
   `Import-Module`). What R1 first built. The audit of R1 found the ways out among what such a list does not name –
   `ForEach-Object -Parallel` runs FullLanguage runspaces, `-AsJob` jobs outlive the window, `using module`, the background
   operator and redirection never look a name up, and among its 233 cmdlets are ones that write files, start processes and
   change the session. Rejected in favour of the allow-list of decision 2.
9. **An engine out of process with a read bridge** (the commands of the window call back into AD Manager for reads). The
   only design in which code of the trusted mode cannot reach AD Manager's memory, the window's session and its credentials;
   weeks of work, a new ADR and no objects shared with the program's grid. Not taken for R1 (only the operator turns the
   trusted mode on, after a question); to be weighed again before R3 hands the `ActiveDirectory` module a `PSCredential`
   (decision 1).
