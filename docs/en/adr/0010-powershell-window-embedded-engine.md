**English** · [Polski](../../adr/0010-okno-powershell-osadzony-silnik.md)

# ADR-0010: The PowerShell window – an embedded PowerShell 7 engine (track R)

- Status: **Accepted** (2026-11-16). Decisions of the project owner, to whom the team delegated the matters of the "To be
  agreed with the team" section of track R and the names of the commands. They concern stage R1 and bind stages R2 and
  R3; in the README of track R the settled entries moved to the section "Decided"
- Date: 2026-11-16
- Applies to: track R in the [README](../../../README.md#track-r--powershell-window-); rules 1–6 of `CLAUDE.md` stay
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
   4 of R1) and the result goes to the implementation note. The reason: only an in-process engine gives access to the
   window's session and credentials without exposing them, objects handed to the program's grid and our own line editor;
   an external `pwsh.exe` gives none of these (section "Alternatives considered").
2. **One runspace per window (D2).** A runspace is never shared between connection windows; it is created and disposed with
   the window's DI scope (`docs/dev/multi-window.md`). A custom `PSHost` / `PSHostUserInterface` routes text, prompts and
   confirmations into the window. No profiles are loaded (`$PROFILE` is never read), there is no PSReadLine and no
   `Start-Job`, module auto-loading is **off**, the execution policy is `Restricted` (R1 runs no `.ps1` files), and
   `Import-Module` works only for the modules on a list, which is **empty** in R1 (RSAT comes with R3, the Microsoft 365
   modules with track C). The reason: the window should do what the program knows, not what a profile, a module in
   `PSModulePath` or a script on disk brought in.
3. **Language mode (D3): ConstrainedLanguage by default.** AD Manager's commands work in it because they are compiled
   program code. The window's switch "Trusted mode (FullLanguage)" is turned on by the operator after a confirmation that
   says what it allows (any .NET code, writes around the change plan); the mode is shown in the window title and in a
   lasting note for as long as it is on, and it is never remembered between windows or sessions. With a system lockdown
   (WDAC / AppLocker, `SystemPolicy.GetSystemLockdownPolicy`) the window stays in ConstrainedLanguage and the switch is
   disabled with the reason. **This is a protection against mistakes, not a security boundary** (README, "Security and
   data"): in FullLanguage any .NET code bypasses the change plan, and the operator can run `pwsh.exe` anyway.
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
7. **PowerShell policies from GPOs (D7).** When the window opens it reads the PowerShell policies: PowerShell Core
   (`HKLM`/`HKCU\Software\Policies\Microsoft\PowerShellCore`) and Windows PowerShell
   (`Software\Policies\Microsoft\Windows\PowerShell`, when `UseWindowsPowerShellPolicySetting` is on) – transcription
   (`EnableTranscripting`, `OutputDirectory`), script block logging, module logging and `ExecutionPolicy` – and the
   lockdown state. A lasting note (the status line's Attention) before the first command says plainly what is logged where
   ("transcription is on by policy: commands and results are written by Windows to <directory>"). The program does not try
   to get around any policy. The registry reader sits behind an interface (the Windows implementation in `AdMng.App`, a
   fake in tests).
8. **Nothing on disk, passwords, one DC, the UI never blocks (D8).** The window's content, results and history live in
   memory only (rule 1). The engine's environment is set **before** its first use: `POWERSHELL_TELEMETRY_OPTOUT=1`,
   `POWERSHELL_DIAGNOSTICS_OPTOUT=1`, `POWERSHELL_UPDATECHECK=Off`, module auto-loading off and the module analysis cache
   pointed at a null device; a "no files" test runs commands in a process whose `HOME`, `LOCALAPPDATA`, `TEMP` and `XDG_*`
   point at an empty folder and asserts that the folder is still empty. The window's credentials are never visible to a
   script: the commands take the session object that the host injects, and R1 creates no `PSCredential` (rule 2). Filters
   are built with `AdMng.Core.Ldap.LdapFilter` and DNs with `DistinguishedName` (rule 3). The commands read only from the
   window's `IDirectorySession` – one DC, no `-Server` and no `-Credential` (rule 4). Every directory call is asynchronous
   with the pipeline's cancellation token (`StopProcessing` cancels it); results are streamed and paged, and 100,000
   results do not block the window (rule 6).
9. **Writes (D9): none in R1.** There is no `Set-`/`New-`/`Remove-`/`Move-`/`Rename-`/`Add-`/`Enable-`/`Disable-`/`Unlock-`/
   `Reset-Adm*` command, no `AD:` drive and no RSAT; a test asserts that the registered AD Manager command list contains
   only the read commands of item 4 (and `Get-AdmVersion`). The window has a place for the pending plan that R2 adds.

## Consequences

- **Size.** The engine package with its dependencies makes the program about 19 MB larger (single file with compression)
  or about 54 MB (without compression); the first runspace starts in about half a second and takes a few tens of MB of
  memory (the Linux measurement is in the implementation note). This cost was accepted by the owner.
- **A release with every security update.** Each PowerShell fix (roughly once a month) means a new version of
  `Microsoft.PowerShell.SDK` in `Directory.Packages.props` and a new AD Manager release. The NuGet audit (`NuGetAudit`, mode
  `all`, warnings as errors) stops the build on a known advisory in the package anyway. LTS 7.6 is supported until November
  2028; 7.7 requires .NET 11, so a change of the line goes together with a change of the platform.
- **EDR and SOC.** EDR systems may report "a PowerShell engine in a process other than `powershell.exe`" (e.g. a Sigma rule
  for `System.Management.Automation.dll`). The answers are the signed `AdMng.exe` (phase 7, certificate from P2) and
  information for the security team; documentation for them is part of R1. AMSI checks every script block, and script
  block logging and transcription from GPOs cover the window – hence the note before the first command (item 7).
- **The security boundary.** ConstrainedLanguage, the module list, the `Restricted` policy and the missing `Start-Job`
  protect against mistakes, not against a malicious operator; the only boundary is the account's rights in AD. Changes made
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
  this decision; R3 will add the RSAT modules through the module list of item 2. A `powershell.config.json` file in the
  engine's folder (turning off transcription and logging set in the users' files) comes with the publish layout (part 4
  of R1).

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
   the module list (empty in R1) and the `Restricted` execution policy: the window should behave the same on every
   workstation.
6. **Our own command language instead of PowerShell.** It would avoid many issues but would give no object pipeline, no
   `Where-Object`/`Select-Object`/`Group-Object` and none of the scripts administrators already use. Rejected.
7. **A new third-party package for the line editor** (e.g. a code editor control). A new dependency with a license to check
   and maintain, while the needed features (highlighting from tokens, completion, multi-line input) can be built on what
   WPF and the engine already have. Rejected; a package only if a stage proves it unavoidable, with its license in the ADR.
