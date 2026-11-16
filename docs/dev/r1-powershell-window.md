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
- **`NoFilesTests`** – the **"no files" test** and the measurement: the probe program (`AdMng.PowerShell.Probe`, a small console
  app the tests start as a child process, so that the environment is set before the runtime starts) with `HOME`,
  `LOCALAPPDATA`, `APPDATA`, `USERPROFILE`, `TEMP`, `TMP`, `TMPDIR` and `XDG_*` pointing at an empty folder and telemetry
  switched **on** in its environment runs two sessions, a script touching the formatter, module discovery, completion, syntax and
  the history policy, and the folder holds no file afterwards; a **positive control** (an ordinary runspace of the SDK, Unix
  only) leaves `telemetry.uuid` there, so the probe would see it; the opt-in **module analysis cache proof**
  (`ADMNG_POWERSHELL_SLOW_TESTS=1`, about 40 s). The probe is not an xunit run on purpose (the first version started this test
  assembly as the child and took 18.6 s on the Windows runner) and does not list modules on Windows (`Get-Module -ListAvailable`
  on a runner with hundreds of modules took 30 s to minutes); on Unix it does, with the module path of the machine.
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
  shape makes it a small addition. (Part 3 gives it its facts: `PowerShellPolicyReader` over a source and
  `PowerShellPolicyNotes.For`; the item itself is not built.)
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
`PowerShellEngineConsumer.props`) and `tests/AdMng.PowerShell.Tests` (128 tests: 126 run, 2 skipped by design – the Windows
execution policy and the opt-in 40 s cache proof) with the child-process program `tests/AdMng.PowerShell.Probe`. Nothing is visible to the operator yet: `AdMng.App` does not reference the
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
(5) `AdMng.Core/PowerShell` (the contracts) is new Core code, not named in the task. (6) The tests start a probe program of their own (`tests/AdMng.PowerShell.Probe`, one more project in the solution); a child
process is the only way to set `HOME` and the like before the runtime starts.

**Open questions for the next parts.** Where the grid takes its columns from (part 2 reads `ObjectListViewModel` and its
columns model); how the window shows a stream's text (part 3: the glyphs and colours of the status line apply to the window's
own messages, the output area has its own colours); whether `Out-AdmGrid` posts a table through the context or calls a window
service (decide in part 2 together with `Open-AdmObject` and `Export-AdmList`); whether the stage S readiness item fits.

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
    part 3 must not take the text before a `…` for a DN (the prefix of a DN is often another valid DN).
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
    `HelpFilePath` must look in `AppContext.BaseDirectory`).
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

**Open questions for the next parts.** (1) Part 3: the DN link finder and the `…` of a cut table (decision 10). (2) Part 4: where the help file goes in the
published layout (decision 13); whether `Out-AdmGrid` takes its columns from the objects' properties (`DistinguishedName` is the first one on every object, the attribute
properties follow in request order). (3) Whether `LockedOut` should use the domain's lockout duration like the list (it needs a read of the domain head; the
DC's computed flag is used when it is returned). (4) `Get-AdmGroupMember` does not list the members whose primary group it is (as the Members tab does not).

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
| The switch "Use Windows PowerShell Policy setting" | `HKLM` or `HKCU`, `Software\Policies\Microsoft\PowerShellCore` | `UseWindowsPowerShellPolicySetting` `REG_DWORD`, non-zero = on |
| Execution policy | the root key (`PowerShellCore`, or `Software\Policies\Microsoft\Windows\PowerShell` while the switch is on) | `EnableScripts` `REG_DWORD` (0 = scripts off), `ExecutionPolicy` `REG_SZ` |
| Transcription | `<root>\Transcription` | `EnableTranscripting` `REG_DWORD`, `EnableInvocationHeader` `REG_DWORD`, `OutputDirectory` `REG_SZ` |
| Script block logging | `<root>\ScriptBlockLogging` | `EnableScriptBlockLogging` `REG_DWORD`, `EnableScriptBlockInvocationLogging` `REG_DWORD` |
| Module logging | `<root>\ModuleLogging` | `EnableModuleLogging` `REG_DWORD`; the modules are the value **names** under `<root>\ModuleLogging\ModuleNames` |
| The config files | `ReadConfigFile(Computer)`: `powershell.config.json` in the folder of `System.Management.Automation.dll` (the engine's all-users file, the one the publish places); `ReadConfigFile(User)`: `<Documents>\PowerShell\powershell.config.json` | the text, or `null`; the reader parses `PowerShellPolicies` (`ScriptExecution`, `Transcription`, `ScriptBlockLogging`, `ModuleLogging` with the value names above; booleans or numbers) |
| The lockdown | `GetLockdownState()` | `SystemPolicy.GetSystemLockdownPolicy()` mapped to `PowerShellLockdownState` (the engine's `SystemPolicyLockdownProbe` already does it) |

   The source returns `null` / an empty list for an absent key, absent value or a value of another type, and throws for a real failure (the window then says it could not tell). The names were taken from
   what `System.Management.Automation.dll` 7.6.10 reads (the paths `Software\Policies\Microsoft\PowerShellCore\…` and `…\Windows\PowerShell\…` and the property names of its policy classes were found in its metadata).

**Decisions of part 3 that the later parts follow** (each with its reason):

1. *The policy types and the reader are in Presentation, not Core.* The plan put a snapshot in Core; nothing outside the window uses it (the engine learns the lockdown from its own probe), so
   Core would only carry types for the view model. `IPowerShellPolicyReader` is what the window depends on (a fake in the tests); `PowerShellPolicyReader` over `IPowerShellPolicySource` holds every rule, so the
   Windows part is only the raw reads.
2. *The order the reader takes* (what the engine does, as far as it could be seen): `HKLM` then `HKCU` of the PowerShell 7 key; with the switch on in either hive the Windows PowerShell keys **stand in** for them; then the all-users config
   file, then the user's. For each of the four settings the **first place that sets it decides**, also when it sets it off (that is how a policy, or the file AD Manager ships next to the engine, turns off what a user's file turned on).
   *Not verified:* whether the engine reads the Windows PowerShell keys instead of, or besides, the PowerShell 7 ones while the switch is on, and whether it reads the JSON policies on Windows at all. The reader reports what it finds; the worst case is a note about a
   setting the engine ignores - never a missing note for one it applies.
3. *The note is plain and says where* (D7): one sentence per recording policy - transcription (where the files go, "Documents folder of your account" when no directory is named), script block logging (the PowerShell Core event log), module logging (the modules) - each with its origin
   ("computer policy", "user policy of Windows PowerShell", "your powershell.config.json"…), then "AD Manager does not change these policies and does not get around them." Execution policy and a lockdown record nothing and make no note (the lockdown is the reason of the disabled switch and a line of the scrollback).
   The log copy is the key and a mask of what is on (1 transcription, 2 script block logging, 4 module logging) - never the directory or the modules.
4. *What lasts, and what covers it* (`UpdateLasting`, one method): the end of the session (error), a start that failed (error), then **notes**: the policy note, "the policies could not be read" (an attention - not knowing is said), the trusted mode; when more than one is true they are one attention, one after the other, with the
   log key `PowerShell.Status.Notes` and their number. A running command and its result cover the lasting state, as everywhere in V2, and it is back when the result is closed - which is what "a lasting note before the first command" means. The policy note is also the first lines of the scrollback (`PolicyNote`), where nothing covers it.
5. *A command is an operation with `OperationWrites.Other`.* In R1 no command writes, but what a window runs is whatever the operator typed (in the trusted mode, anything), so closing the window while it runs asks "Stop the running operation and close the window?" and waits for the end - the existing `RunningWork` question, nothing of its own.
6. *`Status.Show` while a command runs ends the command* (a holder shows one thing; `Show` replaces the operation). The switch of the language mode, which may be refused by the engine, therefore says its refusal in the scrollback always and on the line only when no command runs (a test found it).
7. *No count of objects.* The status line says what it knows: lines of output ("12 lines of output in the window, 0.8 s"; Polish plural forms), not the "N objects" of the plan, because the formatted mode (part 1, decision 4) hands the window text, not objects. A count needs the engine (an `Out-Default` that counts, or the commands reporting what they wrote);
   the contract `PowerShellRunEndItem` has no such field. Open question 1.
8. *The stream is read off the UI thread and handed over in steps* (`PumpAsync`): what is ready when the next item would have to be waited for goes at once (a line appears as it is written), a burst goes in batches of 500, and each step is awaited on the UI thread - a command that writes faster than the window shows is held back (the engine's bounded channel does the rest), 100,000 lines are about 200 hand-overs, not 100,000 (a test counts them).
9. *The scrollback is bounded:* 20,000 lines, dropped in steps (it may hold up to 22,000 before it drops the oldest down to 20,000, so a window fed line by line does not copy its list for every line); `DroppedLines` and `DroppedNote` say how many. A batch of one line is an ordinary `Add`, more than one a `Reset` (the `BulkObservableCollection` convention).
   The lines of the window's own (welcome, the note, the answers) are not counted as output.
10. *The questions of a command* are answered in the editor, in `Answer` or `Secret` mode (no highlight, no completion, no history, always "run" on Enter): text, secret (a `SecureString` built from the typed characters; the echo has no answer in it), a choice (the letter, the text, `?` for the help messages, an empty answer for the default, an unknown answer asks again). The answer is never a command and never in the history. `PendingPrompt` is set **last** (a test saw the question pending before its lines were there).
    Stop cancels a pending question.
11. *Highlighting, completion and history are the engine's* (`Tokenize`, `CompleteAsync`, `IsInputComplete`, `ShouldRemember` through `IPowerShellSession`); the model decides only the keys and the state. A completion result for text that changed meanwhile is dropped; one match is put in, several open the list (first selected, Tab cycles, Enter takes), a second Tab supersedes the first request.
    The history keeps 500 lines, no consecutive repeats, and never a line the engine refuses.
12. *The finder of distinguished names* recognises a name by its shape - leading components of the usual types and the trailing `DC=` components - and checks it with `DistinguishedName.TryParse`. It ignores a name followed by the engine's ellipsis, or by a comma that goes straight on (a cut table cell: the beginning of a name is often another valid name), a value with an
    unescaped `=`, text longer than 8,000 characters, and anything it cannot read in 200 ms.
13. *`PowerShellGridTable` is in Core* (the engine builds it, the window shows it): cells are text (`null` = no value), the column names are unique ignoring case, rows have one cell per column, the DN column is named; its `ToString()` shows only the counts. `IPowerShellWindowActions` is in Presentation (the engine reaches it through part 4's adapter).
14. *`PowerShellSessionRequest` carries the window's `IDirectorySession`* - the factory builds the read-only context from it (D8, rule 4); the request's `ToString()` names nothing.

**Tests.** `AdMng.Presentation.Tests` (Linux, 220 new): `PowerShellPolicyReaderTests` (every combination: each hive, the switch in either hive, the Windows PowerShell keys, both config files, precedence, off beats on, comments and case in the file, a file that is not usable, the lockdown),
`PowerShellPolicyNoteTests` (each setting, several, both languages, the log copy), `PowerShellWindowRunTests` (the echo, streaming and the status sequence with and without Stop, in both languages, a result of 100,000 lines, a line shown as soon as it is written, errors, a failed command, progress,
the close question with No and Yes, disposal during a run, the questions of a command, the history rules, Enter, the width), `PowerShellWindowStatusTests` (title, summary, starting, a failed or missing engine, a disconnected window, the end of the session, every combination of the notes, the trusted mode with its
confirmation, a lockdown from the session or the policies, a refusal by the engine), `PowerShellInputModelTests`, `PowerShellInputHistoryTests`, `PowerShellOutputDocumentTests`, `PowerShellDnLinkFinderTests`, `PowerShellWindowActionsTests` (the contract through a fake, the actions of a host that gave none; the table itself is `AdMng.Core.Tests` `PowerShellGridTableTests`),
`PowerShellRegistrationTests` (scoped, built from the host's registrations, a window from the factory), `PowerShellRulesTests` (Presentation does not reference the engine; the window's code names no file write, registry, settings or write API). `ConnectionLifetimes` has `PowerShellServices`.
Mutation checks done: the close question without `OperationWrites.Other` (1 test fails), the computer and user policy layers swapped (1 fails), the ellipsis rule of the finder removed (4 fail).
The tests use a fake `IPowerShellSession` whose "pipeline" has a thread of its own and a channel, like the engine, so a command that blocks on a question does not block the reader; there is no real-engine test in `AdMng.Presentation.Tests` (it would add the PowerShell SDK to the test project; the engine has its own tests, and the window talks to it through `IPowerShellSession`).

**What could not be verified on Linux, and why it should still be right.** How the view draws any of it (nobody has looked; part 4 writes the view against the properties above); the Windows source of the policies (the keys come from the engine's metadata and the documented templates, the rules are tested here); whether the engine's `Write-Progress`, `Read-Host` and `-Confirm`
arrive as the items the fake streams (part 1 tests them against the real host); the timing of the UI-thread hand-over under the real `Dispatcher` (the logic is the same as the list loads', and the Windows UI test of part 4 holds a command and watches the line).

**Open questions for the next parts.**
1. *"N objects" in the status line* needs a count from the engine (`PowerShellRunEndItem` has none); the line says lines of output meanwhile. A proposal: the engine counts what reaches `Out-Default`, or the commands report what they wrote through the context.
2. *`UseWindowsPowerShellPolicySetting`:* where the switch lives (read from the `PowerShellCore` key of either hive here) and whether the Windows PowerShell keys replace or add to the PowerShell 7 ones is to be confirmed on a machine with such a GPO (part 4's Windows run can read what the engine reports for the same keys).
3. *The default actions in Presentation:* `IPowerShellWindowActions` is only a contract; the logic that does not need WPF - the GUID lookup of `Open-AdmObject`, *Save As* and the CSV file of `Export-AdmList` - could be a Presentation class with a Linux test (it needs one new member, "show a table in the grid", from the host); part 4 decides.
4. *Where the policy note lives:* as the lasting state of the status line it is covered by every command's result until the result is closed (V2 behaviour). If the owner prefers it always visible, it is `PolicyNoteText` - a note (`Ad.Note.Attention`) in the window's layout costs the view one line, and the view model needs nothing.
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
   Sizes: `AdMng.exe` 143.7 MB -> 181.9 MB (+38.2 MB, uncompressed single file), the publish folder 157.5 MB -> 194.8 MB, the CI artifact 82.4 MB.
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
11. *The policies of the workstation* are read by `WindowsPowerShellPolicySource` through the keys of `PowerShellPolicyKeys`: only reads (`RegistryKey.OpenSubKey(writable: false)`), an absent key is "not set", a key that cannot be read is an
    exception that the window says it could not tell (never "nothing is recorded"), strings are not expanded. The all-users file is in `PowerShellEnvironment.EngineHome` (`$PSHOME`), the user's in `Documents\PowerShell`.
    `powershell.config.json` ships beside the program (`CopyToPublishDirectory`): it turns off the three logs that a user's file could switch on, and the Windows PowerShell compatibility layer.
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
