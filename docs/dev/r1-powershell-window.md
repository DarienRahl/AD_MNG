# Track R, stage R1 – the PowerShell window: window and reads (design)

Product description: README, *Track R – PowerShell window* (Polish: *Ścieżka R – Okno PowerShell*). The decisions and their
reasons: [ADR-0010](../en/adr/0010-powershell-window-embedded-engine.md) (Polish: `docs/adr/0010-okno-powershell-osadzony-silnik.md`).
This note is the implementation design of stage **R1** (the window next to the connection window, AD Manager's read commands,
results as objects, `Out-AdmGrid`, *Open in the program*, export, the PowerShell policies from GPOs and the language mode).
**R2** (write commands through the change plan) and **R3** (RSAT modules) are not built here, but nothing in R1 may make them
harder: the window has a place for a pending plan, and no write command exists. Where this note and the section "R1 as built"
disagree, "as built" wins.

R1 is built in five parts (the workflow's units): **1** decisions on paper and the engine, **2** the read commands,
**3** the window's view model, the line editor's model and the policy model, **4** the WPF window, registration, UI tests and
the Windows decisions (single file, publish layout), **5** documentation for the operator, the readiness item and the closing
checks. Section 8 says which part builds which file.

## 1. Decisions at a glance

The owner's decisions D1–D9 (the full text and the reasons are in ADR-0010):

| | Decision |
|---|---|
| D1 engine | Embedded PowerShell 7 (`Microsoft.PowerShell.SDK` 7.6.x LTS; built on 7.6.10, `net10.0`; 7.7 targets .NET 11) in the new project `src/AdMng.PowerShell`, used by `AdMng.App` only, never in the Watchdog package. Size (+19…54 MB) and a release with each security update accepted. If the single-file `AdMng.exe` cannot run the engine, its files are published in a folder beside `AdMng.exe` (Windows CI decides in part 4). |
| D2 runspace | One runspace per window, created and disposed with the window's DI scope; a custom `PSHost` / `PSHostUserInterface`; no profiles, no PSReadLine, no `Start-Job`, module auto-loading off, execution policy `Restricted`, `Import-Module` only for a list of modules that is empty in R1. |
| D3 language mode | ConstrainedLanguage by default; a per-window switch *Trusted mode (FullLanguage)* after a confirmation, shown in the title and in a lasting note, never remembered; with a system lockdown the window stays in ConstrainedLanguage and the switch is disabled with the reason. A protection against mistakes, not a security boundary. |
| D4 names | Prefix `Adm`: `Get-AdmObject`, `Get-AdmUser`, `Get-AdmGroup`, `Get-AdmComputer`, `Get-AdmGroupMember` (`-Recursive`), `Search-AdmObject`, `Out-AdmGrid`, `Open-AdmObject`, `Export-AdmList` (+ `Get-AdmVersion`). Shortcut **Ctrl+Shift+P** (grep of `KeyBinding`, `InputBindings`, the command descriptors of `ShellViewModel.Palette.cs` and the docs found no use of it); menu *Tools → PowerShell…*; a command palette entry. |
| D5 line editor | Own WPF control, no new dependency; completion through `CommandCompletion.CompleteInput` plus parameter completers (attribute names from the session's in-memory schema, DNs from what the session knows – no directory read per keystroke); highlighting from `Parser.ParseInput` tokens; Shift+Enter inserts a line, Enter runs a complete command, Ctrl+C / Stop cancels; history in the window's memory only (Up/Down, Ctrl+R); a line with a plain-text password is not remembered; no file system completion. |
| D6 output | Ordinary PowerShell objects; the window renders them with the engine's own formatting as text with clickable DNs; errors, warnings and verbose messages in the window's colours with the status line's glyphs; `Out-AdmGrid` sends the objects to the program's object list grid; nothing is written unless the operator exports. |
| D7 policies | When the window opens it reads the PowerShell policies from the registry (PowerShell Core, and Windows PowerShell when `UseWindowsPowerShellPolicySetting` is on: transcription, script block logging, module logging, execution policy) and the lockdown state; a lasting note (the status line's Attention) before the first command says what is logged where; the program does not try to get around any policy; the registry reader is behind an interface. |
| D8 nothing on disk | Memory only; the engine's environment is set before its first use (section 4.2); a "no files" test; the window's credentials are never visible to a script; `LdapFilter` and `DistinguishedName`; one DC; async with the pipeline's token. |
| D9 writes | None. A test asserts that the registered AD Manager commands are exactly the read commands (section 4.8). |

## 2. Rules (CLAUDE.md) applied

1. **No local directory data.** Window content, results and history are memory only. Nothing of the window goes to settings
   in R1 (code snippets are R3, and only code typed by the operator). The engine's own files are prevented, not tolerated
   (section 4.2): no telemetry identifier, no module analysis cache, no transcript of the program's own making, no PSReadLine
   history, no `$PROFILE`. The cmdlets and the host never write a file; `Export-AdmList` writes only the path the operator named
   or chose in *Save As*.
2. **No passwords.** The window's credentials stay in the session object; the commands get a `ReadOnlyDirectoryReader` that
   cannot be cast back to the session (so neither its write methods nor the connector's credential reuse), and R1 creates no
   `PSCredential`. A prompt for a credential (`Get-Credential`) is refused in R1. A line with a plain-text password does not
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
                      output items, completion, syntax spans, language mode, policy snapshot) – no PowerShell types
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

- **Initial session state:** `InitialSessionState.CreateDefault()` (not `Create()`: the formatting, `Out-Default` and the
  utility cmdlets are needed), then
  - `LanguageMode = ConstrainedLanguage` (always the start mode, also without a lockdown);
  - `ExecutionPolicy = Restricted` **on Windows only** (`InitialSessionState.ExecutionPolicy` throws
    `PlatformNotSupportedException` on Linux and macOS – there is no execution policy there, so no `.ps1` guard exists on
    those systems; the window is Windows only);
  - `ThrowOnRunspaceOpenError = true`; `ThreadOptions = ReuseThread` and, on Windows, the apartment `STA` (one dedicated
    thread per runspace, as the console host has, so COM-backed cmdlets work);
  - the variable `PSModuleAutoLoadingPreference = None` (module auto-loading off; section 4.2 shows what it prevents);
  - the commands of AD Manager (`AdmCommands.Entries`, 4.5) and the command guards (4.6).
- **One run at a time.** `RunAsync` on a busy session throws `InvalidOperationException` (when the enumeration starts); the
  window's command is disabled while a run is active. Completion returns nothing and the language switch throws while a run
  is active; a completion in flight delays the start of a run by its own duration (one gate).
- **Stop.** `StopAsync()` (the Stop button) and the cancellation token of `RunAsync` do the same: cancel the sink's token
  (wakes a producer waiting for room in the channel and a prompt waiting for an answer), `PowerShell.BeginStop` (the engine's
  stop – a `StopProcessing` call on every running command, which cancels the command's `StopToken`), and start the
  **grace period** (`StopGracePeriod`, 5 s). A run that ends ends with `PowerShellRunEndItem(Stopped)`. A pipeline stuck in
  .NET code cannot be interrupted: after the grace period the enumeration ends anyway with a synthetic
  `PowerShellRunEndItem(Stopped)`, `IsRunning` stays true until the thread returns and a new run is refused meanwhile.
- **`DisposeAsync`:** stops the running pipeline, waits for it (at most the grace period), closes and disposes the runspace
  (only closes it when the pipeline is abandoned), is safe to call twice, and makes later `RunAsync` calls throw
  `ObjectDisposedException`.

### 4.2 The engine's environment (D8, proven)

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
about 40 s; it passed here with the path above). `NUL\ModuleAnalysisCache` on Windows is the one part not verified: if the engine
did not swallow its error, the process could fail 12 s after an operator lists the modules – part 4's Windows run is where
this is looked at (the opt-in test passes or fails there the same way).

**Other process-level facts** found while proving it, relevant to later parts:

- `$PSHOME` is the folder of `System.Management.Automation.dll`: `runtimes/<rid>/lib/net10.0` of a framework-dependent
  output, not the application folder. The four manifests (`Microsoft.PowerShell.Host|Management|Security|Utility`) are in its
  `Modules` folder; the default session state loads none of them as modules (its cmdlets are bound directly).
- A `powershell.config.json` with `PowerShellPolicies` (`Transcription`, `ScriptBlockLogging`) in the **user's**
  configuration folder makes the embedded engine write transcripts that contain the commands **and their output**; one in
  `$PSHOME` (the all-users scope) **wins over the user's** (verified on Linux: user on / all-users off = no transcript; user
  off / all-users on = a transcript). So the README's plan stands – a `powershell.config.json` in the engine's folder turns
  off what a user's file turned on – and the policy reader of D7 must read these files too, not only the registry (on Windows
  the registry GPO comes first; whether the engine also reads the JSON policies on Windows is unverified).
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
  answer converted to the parameter's type), `PromptForChoice` (confirmations: `-Confirm`, `ShouldContinue`) post a **prompt
  item** into the same stream and block the pipeline thread until the consumer answers it; Stop cancels the wait, and a
  cancelled prompt stops the pipeline (`PipelineStoppedException`, like Ctrl+C at a console prompt). `PromptForCredential`
  throws `PSNotSupportedException` with a clear message (R1 creates no `PSCredential`). `EnterNestedPrompt` is not supported,
  `SetShouldExit` raises an event the window may use and does not end the process.
- `Name` is "AD Manager PowerShell", `Version` the program's version, `CurrentCulture` and `CurrentUICulture` follow the
  `Culture` option (the window's UI language); `SupportsVirtualTerminal` is false (no ANSI sequences: the window colours
  text itself).
- **How a command reaches the window's services** (without a global): `PSCmdlet.Host` is the engine's wrapper of the host,
  not the host, but the wrapper hands out the host's `PrivateData`. `WindowHost.PrivateData` is an object of an internal type
  with **no public member** that holds the `IAdmWindowContext`; `AdmCmdlet.ContextOrNull` reads it. A script sees
  `$Host.PrivateData` (the type name, nothing to read – a test asserts that `Get-Member` finds no member), no variable and no
  parameter carries the context, so a script in ConstrainedLanguage cannot reach it. In FullLanguage reflection can, which is
  the boundary ADR-0010 states.

### 4.4 Output items and streaming

`IPowerShellSession.RunAsync(script, cancellationToken)` returns `IAsyncEnumerable<PowerShellOutputItem>` (Core):

| Item | When |
|---|---|
| `PowerShellTextItem(Stream, Text, Foreground?, Background?)` | Anything the host was asked to write; `Text` includes its line ends (`\n`); `Stream` is Output, Error, Warning, Verbose or Debug. A formatted error record arrives as one or two text items of the Error stream. |
| `PowerShellProgressItem(ActivityId, Activity, Status, PercentComplete, Completed)` | `Write-Progress`. |
| `PowerShellPromptItem` | A prompt or confirmation (4.3): `Kind` Text, Secret or Choice, `Caption`, `Message`, `Label`, `Choices`, `DefaultChoice`; answered with `Respond`, `RespondSecret`, `RespondChoice` or `Cancel`; its `ToString()` shows no question and no answer. |
| `PowerShellObjectItem(Value)` | Only in **object mode** (`PowerShellSession.RunObjectsAsync`, not on the Core interface): each object the pipeline emits, unformatted (a `PSObject`). For tests of the commands and for programmatic callers. |
| `PowerShellRunEndItem(State, HadErrors)` | Always the last item: `Completed`, `Stopped` or `Failed` (a terminating error, whose text is the item before it), and whether any error was written. |

The window uses the **formatted mode**: the script is run as one pipeline followed by `Out-Default`, with the error stream
merged into it, the way the console host runs a command. The engine's own formatting therefore decides tables and lists (a
header once, column widths from `OutputWidth`), and the text reaches the host as above; the objects are real objects up to
`Out-Default`, so `Where-Object`, `Select-Object` and `Out-AdmGrid` in the pipeline work on them. The script runs in the
global scope (variables stay between commands, like in a console). The channel is bounded (4096 items; a producer that
outruns the window waits, so `while ($true) { 1 }` cannot eat the memory before Stop) and the session never buffers a whole
result. The session is idle again **before** the consumer sees the end item.

### 4.5 AD Manager's commands

`AdmCommands.Catalog` is the one catalog (name → type); `AdmCommands.Entries()` creates the `SessionStateCmdletEntry` list
for the initial session state. A command class is a plain `PSCmdlet` derived from `AdmCmdlet` (public, so that R3 and the
tests can derive from it):

```csharp
public abstract class AdmCmdlet : PSCmdlet
{
    protected IAdmWindowContext? ContextOrNull { get; }         // from the host's private data (4.3)
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

### 4.6 The command guards (D2)

`CommandGuards.Apply` removes `Start-Job` and `Import-Module` from the initial session state and registers, under the same
names, functions that write an error naming the command (`Start-Job: Background jobs are not available in the AD Manager
PowerShell window: …`, `Import-Module: Import-Module is limited to a list of modules …`) with the error id
`AdmCommandNotAvailable`. A function has no parameters of its own, so whatever the operator typed binds to it and the message
is what is shown; a call through an alias (`ipmo`) or with the module name (`Microsoft.PowerShell.Core\Import-Module`, which
is not recognized because the cmdlet is gone) meets the refusal too. Like any failed command it does not stop the script
unless `$ErrorActionPreference` says so. `CommandGuards.AllowedModules` is the list `Import-Module` may load: **empty** in R1
(R3 fills it with the RSAT modules and replaces the function by a command that checks the list; track C adds the Microsoft 365
ones). A guard is a protection against mistakes, like the language mode – not a sandbox. `.ps1` files are refused by the
`Restricted` execution policy on Windows; there is no guard on Linux (no execution policy exists there).

### 4.7 Language mode (D3)

- `PowerShellSession.LanguageMode` is `Constrained` until `SetLanguageModeAsync(Full)` succeeds; `TrustedModeBlock` says
  whether the window's switch may be turned on (`None` or `SystemLockdown`; the window localizes the reason). The probe
  (`IPowerShellLockdownProbe`, injected; the real one wraps `SystemPolicy.GetSystemLockdownPolicy()`) is asked at creation and
  again on every switch to the trusted mode: under `Enforce` the session stays `Constrained`, the switch throws and
  `TrustedModeBlock` is `SystemLockdown`; `Audit` does not block.
- Switching sets `Runspace.SessionStateProxy.LanguageMode`; it is refused while a run is active. The mode never leaves the
  session object: nothing persists it, and a new window starts in ConstrainedLanguage (a test creates a session after a
  trusted one).
- The confirmation dialog, the title suffix and the lasting note are the window's (parts 3 and 4); the engine only says what is
  possible.

### 4.8 The command list test (D9)

`CommandCatalogTests` compares `AdmCommands.Names` with the expected list; a reflection test finds every public type of the
engine assembly with a `[Cmdlet]` attribute and fails if one is not in the catalog, does not derive from `AdmCmdlet` or has a
write verb (`Set`, `New`, `Remove`, `Move`, `Rename`, `Add`, `Enable`, `Disable`, `Unlock`, `Reset`, `Clear`, `Restore`,
`Update`, `Write`, `Invoke`, `Grant`, `Revoke`, `Copy`, `Import`, … – the full list is in the test); a run-time test lists
`Get-Command -Name *-Adm*` in a live session and compares it with the catalog, and checks that there is no `AD:` drive and no
`*-AD*` command. R2 changes the list and that test together.

### 4.9 History, syntax and completion

- `PowerShellHistoryPolicy.ShouldRemember(line)` is a pure function over the parser's tokens and AST. A line is **not**
  remembered if it is empty, still incomplete, has the `-AsPlainText` parameter (also a prefix of three letters or more), a
  `ConvertTo-SecureString` with a literal argument (positional or `-String`), or a named parameter with `Password`, `Passwd`,
  `Secret`, `Token` or `ApiKey` in its name whose argument is a literal (a constant, or a string without embedded
  expressions; also `-Password:'x'` and a bare word). A variable, a subexpression or `Read-Host -AsSecureString` as the
  argument is fine; a line with a syntax error is still checked from its tokens. (README and ADR name `-AsPlainText`,
  `ConvertTo-SecureString '…'` and *Password*; the other secret words are the filter PSReadLine applies.) The window's history
  lives in `AdMng.Presentation` (memory only) and asks this function through `IPowerShellSession.ShouldRemember`.
- `PowerShellSyntax.Tokenize(input)` returns spans (`Start`, `Length`, `Kind`: command, parameter, variable, string, number,
  operator, comment, keyword, type, member) from `Parser.ParseInput`'s tokens, in order, never empty, with whitespace,
  brackets and bare arguments left out (kind `Other` is not listed). `IsComplete(input)` is `true` unless a parse error is of
  the "incomplete input" kind (an open quote, bracket, script block or here-string, a trailing pipe) – it decides Enter versus a
  new line; other syntax errors do not count (such a line runs and fails). Both are pure and need no runspace, so the editor
  may call them on every keystroke.
- `CompleteAsync(input, cursor)` calls `CommandCompletion.CompleteInput` on the window's idle runspace and returns the
  replacement span and the matches (text, list text, kind, tool tip). File system completion is removed in R1: matches of
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

`PowerShellWindowViewModel` (scoped, one per connection scope, tested on Linux against a fake `IPowerShellSession` and, in a
few tests, the real engine):

- the **status line** (`PageStatusHolder`, `docs/dev/v2-status-controls.md`): the summary is "domain · DC · account · mode"; a
  running command is an `OperationScope` (`Status.Begin`) with the Stop of the line (the window's own Stop button is the
  action button's cancel); the PowerShell policy note is a **lasting Attention** (`SetLasting`) until the first command; the
  trusted mode is a lasting Attention while it is on. `writes:` stays `None` – R2 sets it where the plan executes.
- `PowerShellInputModel`: the buffer, the caret, the history (a list in memory with Up/Down and Ctrl+R incremental search),
  the completion state (matches, selection, the span to replace) and the "Enter runs or inserts a line" decision
  (`IsComplete`).
- `PowerShellOutputDocument`: the scrollback as blocks (the echoed command, text runs with a stream and a colour, a prompt,
  a run summary) with a bounded size (the oldest blocks are dropped; there is no file), and the DN recognition: a
  `DistinguishedName` is recognised in a text run with `DistinguishedName.TryParse` over candidates found by a regular
  expression anchored on `CN=`/`OU=`/`DC=`, and becomes a link (`OpenObjectCommand`).
- **A place for the pending plan** (R2): a `PendingPlanState` property of the view model with its own area in the window
  (collapsed, empty) so that R2 adds the panel without changing the layout.
- the policy note: `PowerShellPolicySnapshot` (Core) from `PowerShellPolicyReader`, which takes an `IPowerShellPolicyRegistry`
  (reads a value of a hive/key/name, and the two `powershell.config.json` files) – the Windows implementation is in `AdMng.App`,
  the fake in the tests; the snapshot says transcription (on/off, directory), script block logging, module logging, the
  execution policy and the lockdown state, and `PowerShellPolicyNote.For(snapshot)` is the text.
- the command descriptor `tools.powershell` (`Ctrl+Shift+P`, category Tools) with the palette entry, and the window title
  "PowerShell – domain – DC – account" (plus "– Trusted mode" while on).
- strings in `Strings.resx` / `Strings.pl.resx` with `L.PowerShell.*` constants, following the wording rules of
  `v-interface-renewal.md` 4.5.

## 7. The window (part 4)

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
  result. The `powershell.config.json` that turns off what a user's file enabled is placed next to
  `System.Management.Automation.dll` by the publish step.
- Every consumer of the engine (the App, the tests) imports `src/AdMng.PowerShell/PowerShellEngineConsumer.props`, which
  excludes the non-OSI `sni.dll` packages (ADR-0010, "Package licenses").

## 8. File and class plan

| Part | Project | Files (new unless marked) |
|---|---|---|
| 1 | docs | `docs/adr/0010-…`, `docs/en/adr/0010-…`, this note, `docs/dev/README.md`, the README pair (Track R "Decided"), `CLAUDE.md` layout rows |
| 1 | `AdMng.Core` | `PowerShell/PowerShellEnums.cs`, `PowerShellOutputItem.cs` (items), `PowerShellCompletion.cs`, `IPowerShellSession.cs` |
| 1 | `AdMng.PowerShell` | `AdMng.PowerShell.csproj`, `PowerShellEngineConsumer.props`, `PowerShellEnvironment.cs`, `PowerShellSession.cs`, `PowerShellSessionOptions.cs`, `IPowerShellLockdownProbe.cs`, `ErrorText.cs`, `Hosting/OutputSink.cs`, `WindowHost.cs`, `WindowHostUserInterface.cs`, `WindowHostRawUserInterface.cs`, `Commands/AdmCmdlet.cs`, `IAdmWindowContext.cs`, `AdmWindowContext.cs`, `AdmCommands.cs`, `GetAdmVersionCommand.cs`, `Guards/CommandGuards.cs`, `Input/PowerShellHistoryPolicy.cs`, `Input/PowerShellSyntax.cs`; `Directory.Packages.props`, `AdMng.slnx` (changed) |
| 1 | `tests/AdMng.PowerShell.Tests` | `PowerShellSessionTests`, `LanguageModeTests`, `HostRoutingTests`, `GuardTests`, `EnvironmentTests`, `HistoryPolicyTests`, `SyntaxTests`, `CompletionTests`, `CommandCatalogTests`, `PackageLicenseTests`, `NoFilesTests` (+ `ChildProcess`, `TempFolder`, the child methods), `TestSupport`, `TestCommands` |
| 2 | `AdMng.PowerShell` | `Commands/Get*Command.cs`, `SearchAdmObjectCommand.cs`, `OpenAdmObjectCommand.cs`, `OutAdmGridCommand.cs`, `ExportAdmListCommand.cs`, `Directory/AdmFilterBuilder.cs`, `AdmObjectProjector.cs`, `AdmAttributeCompleter.cs`, `AdmDnCompleter.cs`, `Commands/IAdmWindowServices.cs` |
| 3 | `AdMng.Core` | `PowerShell/Policies/PowerShellPolicySnapshot.cs`, `PowerShellPolicyReader.cs`, `IPowerShellPolicyRegistry.cs`, `PowerShellPolicyNote.cs`, `IPowerShellSessionFactory.cs` |
| 3 | `AdMng.Presentation` | `PowerShell/PowerShellWindowViewModel.cs`, `PowerShellInputModel.cs`, `PowerShellHistory.cs`, `PowerShellOutputDocument.cs`, `DnLinkFinder.cs`, `Shell/ShellViewModel.Palette.cs` (changed), `Resources/Strings*.resx` + `L.cs` |
| 4 | `AdMng.App` | `PowerShell/PowerShellWindow.xaml(.cs)`, `PowerShellEditor.cs`, `PowerShellSessionFactory.cs`, `WindowsPowerShellPolicyRegistry.cs`, `AppHost.cs`, `Services/DialogService.cs`, `SmokeTest.cs` (changed), `AdMng.App.csproj` (reference), `tests/AdMng.App.UiTests/PowerShell*` |
| 5 | docs | `docs/POWERSHELL.md` + `docs/en/POWERSHELL.md` (the operator's guide: commands, the window, the policies, the trusted mode, what is on disk), `docs/releases/NEXT.md`, the readiness item (if small), the closing checks |

## 9. Tests per layer

### 9.1 `AdMng.PowerShell.Tests` (Linux and Windows)

- **`PowerShellSessionTests`** – a script runs and its output streams as text ending with the end item; variables stay between
  runs; one table header from the engine's formatting; object mode; a terminating error is `Failed`, a non-terminating one
  `Completed` with errors; the next run can start at once; a second run while one is active is refused; a spinning script is
  stopped (`StopAsync`, and the cancellation token, no sleep); a command that waits on `StopToken` ends at Stop; a stop the
  pipeline does not obey ends the enumeration after the grace period (a `FakeTimeProvider`, no real wait); dispose stops a
  run and throws on later runs; two sessions share no runspace; `OutputWidth` decides the wrapping.
- **`LanguageModeTests`** – ConstrainedLanguage by default and the language-mode error for a .NET call; the program's commands
  work in it; the switch to FullLanguage and back; refused under a fake lockdown (at the start and when it appears later);
  `Audit` does not block; refused while a run is active; a new session starts constrained after a trusted one.
- **`HostRoutingTests`** – each stream arrives as text of its own stream; `Write-Host` colours without duplicates; progress;
  `Read-Host` (text and secret), a missing mandatory parameter, `-Confirm` (both answers) as prompt items with the answer
  going back; a cancelled prompt and a stopped run at a prompt end the run; `Get-Credential` is refused; no nested prompt; the
  host's name and culture.
- **`GuardTests`** – `Start-Job` and `Import-Module` (also `ipmo`) are refused with the message and the script goes on; the
  module-qualified names are not recognized; the guarded names are functions; no profile and no PSReadLine; auto-loading off;
  the execution policy `Restricted` (Windows only).
- **`EnvironmentTests`** – the variables of 4.2 are set and visible to a script; no `PSHost.*` pipe exists (Windows
  `\\.\pipe\`, Unix the temp folder).
- **`NoFilesTests`** – the **"no files" test** and the measurement: a child process (this assembly, started with `-method`, so
  the environment is set before the runtime starts) with `HOME`, `LOCALAPPDATA`, `APPDATA`, `USERPROFILE`, `TEMP`, `TMP`,
  `TMPDIR` and `XDG_*` pointing at an empty folder and telemetry switched **on** in its environment runs two sessions, a
  script touching the formatter, module discovery, completion, syntax and the history policy, and the folder holds no
  file afterwards; a **positive control** (an ordinary runspace of the SDK, Unix only) leaves `telemetry.uuid` there, so the
  probe would see it; the opt-in **module analysis cache proof** (`ADMNG_POWERSHELL_SLOW_TESTS=1`, about 40 s).
- **`HistoryPolicyTests`**, **`SyntaxTests`**, **`CompletionTests`** – section 4.9 (the matrices are in the tests); the
  completion test has a mutation check (it fails when the file system matches are not dropped).
- **`CommandCatalogTests`** – section 4.8, and the context mechanism: `Get-AdmVersion` reports the fake session's domain, DC and
  account, follows the trusted mode, answers without a window, and a command that needs the window says so; the reader a command
  gets is not the session; two sessions give each command its own services; a script finds no member on `$Host.PrivateData`.
- **`PackageLicenseTests`** – the output has no `sni.dll` and its deps file names none.

### 9.2 Part 2 – the commands

Each command over `FakeDirectorySession` (the sample company, 1000-object variants): same objects and values as the list view
for 1000 objects, the escaping test `Łukasz (Kraków)*` (also `\`, `*`, `(`, `)`, NUL), nested membership with a cycle,
100,000 streamed results with a cancellation in the middle and no buffering of the whole set, the secret attributes masked, no
call outside the one session, `-Server` and `-Credential` do not exist.

### 9.3 Part 3 – `AdMng.Presentation.Tests` (Linux)

The view model against a fake session (and the real engine in a few tests): the status line during a run and on Stop, the
lasting policy note, the trusted-mode lasting note, history rules, completion state, Enter versus a new line, DN links, the
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
   turns on transcription or script block logging) – the **"no files" test** passes; with transcription turned on by a GPO,
   the window warns before the first command; the password does not appear in the history.
4. When a WDAC policy applies, the window works in ConstrainedLanguage mode and AD Manager's commands work; without it
   ConstrainedLanguage is the default and the trusted mode needs a confirmation and is shown.
5. No write command exists, no `AD:` drive and no RSAT: the **command list test** passes.
6. No PSReadLine, no profile, no `Start-Job`, `Import-Module` limited to the (empty) list, telemetry and the diagnostics channel
   off, no module analysis cache.
7. The window opens from *Tools → PowerShell…*, the command palette and **Ctrl+Shift+P**, belongs to its connection, shows the
   domain, the DC and the account, and its runspace is disposed with the connection's scope.
8. The published single-file `AdMng.exe` runs the engine (or the fallback layout does), proven by the smoke test in Windows CI.

The criteria about writes and RSAT in the README (the change plan, translated RSAT commands, `Get-ADUser` without `-Server`,
`AD:` writes) are criteria of R2 and R3.

## 11. Open points

- Whether `powershell.config.json` `PowerShellPolicies` are read by the engine on Windows next to the registry policies, and
  where `$PSHOME` is in the single-file layout (part 4, Windows).
- `NUL\ModuleAnalysisCache` as the analysis cache path on Windows (part 4, Windows).
- The stage S readiness item for the policies and the language mode: optional, added in part 5 only if the readiness check's
  shape makes it a small addition.
- The two R3 questions of the README ("To be agreed with the team"): the list of translated RSAT commands and the window's
  credentials for the `ActiveDirectory` module.

## 12. R1 as built

Each part adds its block here; the blocks win over the design above.

### 12.1 Part 1 – decisions and the engine

**What exists.** The decisions D1–D9 on paper (ADR-0010 in Polish and English, this note, the README pair's "Decided" section,
the index, the `CLAUDE.md` layout rows) and the engine: `src/AdMng.PowerShell` (`PowerShellSession` with the custom host, the
output channel and the stop logic; `PowerShellEnvironment`; the lockdown probe; `AdmCmdlet`, `AdmWindowContext`, `AdmCommands`,
`Get-AdmVersion`; `CommandGuards`; `PowerShellHistoryPolicy`; `PowerShellSyntax`; completion), the contracts in
`src/AdMng.Core/PowerShell` (`IPowerShellSession`, the output items, completion, syntax spans, the enums), the package
(`Microsoft.PowerShell.SDK` 7.6.10 in `Directory.Packages.props`, with the four excluded `sni` packages and
`PowerShellEngineConsumer.props`) and `tests/AdMng.PowerShell.Tests` (131 tests: 129 run, 2 skipped by design – the Windows
execution policy and the opt-in 40 s cache proof). Nothing is visible to the operator yet: `AdMng.App` does not reference the
engine until part 4, and there is no release note.

**Decisions of part 1 that the later parts follow** (each with its reason):

1. *Contracts in Core, the engine is App-only.* `AdMng.Presentation` never references an engine project (the `AdMng.Remote`
   pattern), so the view model is tested against fakes of `IPowerShellSession` and, in a few tests, the real engine. The Core
   types name no PowerShell type (`PowerShellObjectItem.Value` is an `object`).
2. *The window context reaches a command through the host's `PrivateData`* – `PSCmdlet.Host` is the engine's wrapper, not our
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
7. *Guards are functions that write a non-terminating error with the command's name* (`Start-Job: …`), and the cmdlet is
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

**Checks run** (Linux): `dotnet build AdMng.slnx -c Release` – 0 warnings, 0 errors; `dotnet test tests/AdMng.PowerShell.Tests`
– 129 passed, 2 skipped; the opt-in cache proof passed (41 s: the control's cache file appeared, the window's session wrote
none); a mutation check (the file-completion filter removed) made the completion test fail; `release.sh check`,
`build-icon-set.py --check` and `check_docs.py` – OK. The Windows CI run is recorded in the report of the part.

**What could not be verified on Linux, and why it should still be right.**

- *Windows behaviour of the engine setup:* `PSModuleAnalysisCachePath=NUL\ModuleAnalysisCache` (the engine swallows an unwritable path on Linux; the
  proof test runs on Windows only if someone sets `ADMNG_POWERSHELL_SLOW_TESTS=1`), `InitialSessionState.ExecutionPolicy =
  Restricted` and whether it is the process environment variable `PSExecutionPolicyPreference` (a Windows-only test asserts
  `Get-ExecutionPolicy` is `Restricted`), the STA apartment with `ReuseThread`, the absence of `\\.\pipe\PSHost.*` (the Linux run
  cannot show the pipe exists in the first place – a plain runspace creates none there; the Windows run is the proof), and the
  real `SystemPolicy` probe under WDAC/AppLocker (the engine's own behaviour under lockdown is the README's "to be confirmed on
  a machine with WDAC").
- *The "no files" test on Windows* only watches what an environment variable moves (`TEMP`, `TMP`); the engine's files there go
  to known folders (`%LOCALAPPDATA%`), which a variable does not redirect – the Windows run is weaker than the Linux run, and
  the settings of 4.2 (tested in `EnvironmentTests`) are what carries the proof.
- *Whether the engine reads `PowerShellPolicies` from `powershell.config.json` on Windows* next to the registry; the D7 reader
  will read both, so the note is right either way.
- *The single-file layout:* where `$PSHOME` points and whether the four modules are found (part 4).

**Deviations from the task text.** (1) No `Object` item in the formatted mode: results are shown as the engine's formatted
text (the console model); an `Object` item exists in object mode only (decision 5). (2) The end item carries `HadErrors`
instead of an error count. (3) The 85-package license check found four packages (`sni`) that are not OSI approved – excluded,
with a test, instead of "all MIT" (ADR-0010 "Package licenses"). (4) The README's 19 / 54 MB measured as +17 / +48 MB.
(5) `AdMng.Core/PowerShell` (the contracts) is new Core code, not named in the task. (6) The tests start a child process of
their own assembly; this is the only way to set `HOME` and the like before the runtime starts.

**Open questions for the next parts.** Where the grid takes its columns from (part 2 reads `ObjectListViewModel` and its
columns model); how the window shows a stream's text (part 3: the glyphs and colours of the status line apply to the window's
own messages, the output area has its own colours); whether `Out-AdmGrid` posts a table through the context or calls a window
service (decide in part 2 together with `Open-AdmObject` and `Export-AdmList`); whether the stage S readiness item fits.
