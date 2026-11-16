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

`PowerShellSession : IPowerShellSession, IAsyncDisposable` owns one runspace, one host and one context. It is created by the
factory in the connection's DI scope (`IPowerShellSessionFactory.Create`, App) and disposed by the scope. Creation does not
open the runspace on the caller's thread: `CreateAsync` opens it on a pool thread (the first runspace of a process costs about
half a second; the status line says "Starting PowerShell…" meanwhile).

- **Initial session state:** `InitialSessionState.CreateDefault()` (not `Create()`: the formatting, `Out-Default` and the
  utility cmdlets are needed), then
  - `LanguageMode = ConstrainedLanguage` (always the start mode, also without a lockdown);
  - `ExecutionPolicy = Restricted` **on Windows only** (`InitialSessionState.ExecutionPolicy` throws
    `PlatformNotSupportedException` on Linux and macOS; on those systems there is no execution policy and R1 refuses `.ps1`
    by the command guard of section 4.6);
  - `ThrowOnRunspaceOpenError = true`; `ThreadOptions = ReuseThread`, apartment `STA` on Windows (a dedicated thread per
    runspace, as the console host does, so COM-backed cmdlets such as the clipboard work);
  - the variable `PSModuleAutoLoadingPreference = None` (module auto-loading off; section 4.2 shows what it prevents);
  - the commands of AD Manager (`AdmCommands.Entries`, section 4.5) and the command guards (section 4.6).
- **One run at a time.** `RunAsync` on a busy session throws `InvalidOperationException`; the window's command is disabled
  while a run is active. Completion and the language mode switch are refused (completion returns nothing, the switch throws)
  while a run is active.
- **`DisposeAsync`:** stops the running pipeline (`PowerShell.StopAsync`), closes and disposes the runspace, completes the
  output channel, detaches the context (the reader is dropped), and is safe to call twice.

### 4.2 The engine's environment (D8, proven)

`PowerShellEnvironment.Apply()` runs once per process, before the first runspace (a `Lazy` in the session's static
constructor path; idempotent and thread-safe). It sets, overriding what the user's environment says, because a user's
variable must not weaken a privacy setting:

| Variable | Why (what was observed with Microsoft.PowerShell.SDK 7.6.10 on Linux, a clean `HOME`/`XDG_*`/`TMPDIR`) |
|---|---|
| `POWERSHELL_TELEMETRY_OPTOUT=1` | Without it the first runspace writes `telemetry.uuid` (`$XDG_CACHE_HOME/powershell`, on Windows under the user's local application data). With it no file is written. |
| `POWERSHELL_DIAGNOSTICS_OPTOUT=1` | The name is in the engine (the diagnostics channel of `Enter-PSHostProcess`); a plain runspace creates no `PSHost.*` pipe on Linux, so the proof is the Windows test of section 9.1 (no `\\.\pipe\PSHost.*`). |
| `POWERSHELL_UPDATECHECK=Off` | No update notification (a console host feature; set so that nothing ever looks for one). |
| `PSModuleAnalysisCachePath` = the null device (`/dev/null`, `NUL`) | See below. |

**The module analysis cache** is the only file the engine writes by itself that is not telemetry. It is
`ModuleAnalysisCache-<hash>` in the cache directory, written by a timer about 12 s after module discovery (measured: the file
appeared 14 s after the process started, with discovery at about 1 s; there is no flush at exit, so a short-lived process never
shows it). A probe on Linux with a fake module on `PSModulePath` and a clean cache directory gave:

| Setting | Discovery that ran | File after 40 s |
|---|---|---|
| auto-loading on (the default) | `Get-Command Get-Fake*`, completion of `Get-` | `ModuleAnalysisCache-67BE2BE9` written |
| `PSModuleAutoLoadingPreference = None` | the same, plus `Get-Command -Module`, `Get-Command` | **no file** (completion still lists 49 commands) |
| `None`, then `Get-Module -ListAvailable` | explicit module listing | file written (so auto-loading off alone is not enough) |
| `PSModuleAnalysisCachePath=/dev/null`, `Get-Module -ListAvailable` | explicit module listing | **no file**, no error |
| `PSModuleAnalysisCachePath=/proc/nonexistent/cache`, `Get-Module -ListAvailable` | explicit module listing | **no file**, no error (a write that fails is swallowed) |

So the window turns auto-loading off **and** points the cache at the null device. The null device on Windows (`NUL`) is the
one part not verified here (blind spot in "R1 as built"); the engine swallows an unwritable cache path, so the worst case
there is a file in the user's local application data, which part 4's Windows tests look for. A slow, opt-in test repeats the
probe by hand (section 9.1).

**Other process-level facts** found while proving it, relevant to later parts:

- `$PSHOME` is the folder of `System.Management.Automation.dll`: `runtimes/<rid>/lib/net10.0` of a framework-dependent
  output, not the application folder. The four manifests the default session state loads
  (`Microsoft.PowerShell.Host|Management|Security|Utility`) are in its `Modules` folder.
- A `powershell.config.json` with `PowerShellPolicies` (`Transcription`, `ScriptBlockLogging`) in the **user's** configuration
  folder makes the embedded engine write transcripts that contain the commands **and their output**; one in `$PSHOME`
  (the all-users scope) **wins over the user's** (verified on Linux: user on / all-users off = no transcript; user off /
  all-users on = a transcript). So the README's plan stands – a `powershell.config.json` in the engine's folder turns off
  what a user's file turned on – and the policy reader of D7 must read these files too, not only the registry (on Windows the
  registry GPO comes first; whether the engine also reads the JSON policies on Windows is unverified).
- `.NET`'s own diagnostic sockets (`dotnet-diagnostic-*`, `clr-debug-pipe-*` in the temp folder) belong to the runtime, not to
  PowerShell. They are not files the program writes and are outside this track.

### 4.3 The host

`WindowHost : PSHost` (internal), `WindowHostUserInterface : PSHostUserInterface`, `WindowHostRawUserInterface :
PSHostRawUserInterface` (minimal: buffer and window size from the session's width, colours, no cursor, no key reading).

- All output goes through the user interface into the **output channel** of the run, as items (section 4.4): `Write`,
  `WriteLine` → text of the output stream (with the colours `Write-Host` gave); `WriteErrorLine`, `WriteWarningLine`,
  `WriteVerboseLine`, `WriteDebugLine` → text items of those streams; `WriteInformation` / `WriteProgress` → information and
  progress items. The window renders them; nothing goes to a file or a console.
- **Prompts.** `ReadLine`, `ReadLineAsSecureString`, `Prompt`, `PromptForChoice` (confirmations: `-Confirm`, `ShouldContinue`)
  post a **prompt item** into the same stream and block the pipeline thread until the consumer answers it (the item carries
  the answer's task; Stop cancels it). `PromptForCredential` throws `PSNotSupportedException` with a clear message (R1 creates
  no `PSCredential`). `EnterNestedPrompt` is not supported, `SetShouldExit` asks the window to close and does not end the
  process, and `NotifyBeginApplication` / `NotifyEndApplication` do nothing.
- `Name` is "AD Manager PowerShell", `Version` is the program's version, `CurrentCulture` and `CurrentUICulture` follow the
  window's culture (the UI language).
- **How a command reaches the window's services** (without a global): `PSCmdlet.Host` returns the external host, which is our
  `WindowHost`; it holds the `AdmWindowContext` that the session was created with. `AdmCmdlet.Context` reads it from there.
  The context is an internal property of an internal class: a script sees `$Host` (the engine's wrapper) but no variable and no
  parameter carries the context, so a script in ConstrainedLanguage cannot reach it. In FullLanguage reflection can, which is
  the boundary that ADR-0010 states.

### 4.4 Output items and streaming

`IPowerShellSession.RunAsync(script, cancellationToken)` returns `IAsyncEnumerable<PowerShellOutputItem>`:

| Item | When |
|---|---|
| `PowerShellTextItem(Stream, Text, Foreground?, Background?)` | Anything the host UI was asked to write; `Text` includes its line ends; `Stream` is Output, Error, Warning, Verbose, Debug or Information. |
| `PowerShellProgressItem(...)` | `Write-Progress` (activity, status, percent, completed). |
| `PowerShellPromptItem(Kind, Caption, Message, Choices, ...)` + `Answer(...)` | A prompt or confirmation (section 4.3). |
| `PowerShellObjectItem(Value)` | Only in **object mode** (`RunObjectsAsync`): each object the pipeline emits, unformatted. Used by tests and by programmatic callers. |
| `PowerShellRunEndItem(State)` | Always the last item: `Completed`, `Stopped` or `Failed` (and whether errors were written). |

The window uses the **formatted mode**: the script is run as one pipeline followed by `Out-Default`, with the error stream
merged into the output, the way the console host runs a command. The engine's own formatting therefore decides tables and
lists (a header once, column widths from the window's width), and the text reaches the host UI as above; the objects are real
objects up to `Out-Default`, so `Where-Object`, `Select-Object` and `Out-AdmGrid` in the pipeline work on them. The channel is
bounded (back pressure: a producer that outruns the window waits) and the session never buffers a whole result.

### 4.5 AD Manager's commands

`AdmCommands` is the one catalog (name → type); `Entries` creates the `SessionStateCmdletEntry` list for the initial session
state. A command class is a plain `PSCmdlet` derived from `AdmCmdlet`:

```csharp
public abstract class AdmCmdlet : PSCmdlet
{
    protected IAdmWindowContext Context { get; }        // from the host (section 4.3); throws a clear error without a window
    protected CancellationToken StopToken { get; }      // cancelled by StopProcessing
    protected void WriteAll<T>(IAsyncEnumerable<T> source, Func<T, object?> map);   // pulls on the pipeline thread, writes as it goes
    protected T Wait<T>(Task<T> task);                  // blocks the pipeline thread (never the UI thread) with StopToken
}
```

`IAdmWindowContext` is what a command may use: `IDirectoryReader Reader` (a `ReadOnlyDirectoryReader` of the window's session:
searches, base reads, ranged values, `Info`, `Schema` – no write method exists on it), `string ApplicationVersion`, and from
part 2 the window services (`OpenObject(dn)`, a sink for the grid, a save-path chooser). Commands are tested without WPF and
without a directory: a `PowerShellSession` is created over a `FakeDirectorySession` (`tests/AdMng.Testing`) and the command is
run as a script, which is the same path the window uses. **R1 part 1 ships one command, `Get-AdmVersion`** (kept: it is a
useful command – the program's and the engine's version, the language mode, the domain, the DC and the account of the window's
session – and the test that proves the whole mechanism).

### 4.6 The command guards (D2)

The initial session state replaces three commands by guards (a guard is a cmdlet registered under the original name, so
`Get-Command`, completion and error messages show it):

| Command | Guard |
|---|---|
| `Start-Job` (and `Start-ThreadJob`, `Receive-Job` stays) | Throws a `PSNotSupportedException`: "Background jobs are not available in the AD Manager PowerShell window (they need a separate pwsh.exe process). Run the command in the window or use Start-Process explicitly." |
| `Import-Module` | Allows only a module on `AdmModuleList.Allowed` (empty in R1; R3 fills it with the RSAT modules, track C with the Microsoft 365 ones); anything else throws "Import-Module is limited to a list of modules in this window; no module is on it yet." (the message names the module). `using module` and `Import-PSSession` take the same path or are refused. |
| a `.ps1` file as a command (`& ./x.ps1`, `. x.ps1`) | On Windows the `Restricted` execution policy refuses it; on Linux and macOS (tests only) a command-lookup guard does. |

A guard is a protection against mistakes, like the language mode. It does not try to make the window a sandbox.

### 4.7 Language mode (D3)

- `PowerShellSession.LanguageMode` is `Constrained` until `SetLanguageModeAsync(Full)` succeeds; `CanEnableTrustedMode` and
  `TrustedModeBlockedReason` say whether the window's switch may be turned on. The probe (`IPowerShellLockdownProbe`,
  injected; the real one wraps `SystemPolicy.GetSystemLockdownPolicy()`) is asked at creation and again on every switch: under
  `Enforce` the session stays `Constrained`, the switch is refused with the reason, and `Audit` is reported but does not block.
- Switching sets `Runspace.SessionStateProxy.LanguageMode`; it is refused while a run is active. The mode never leaves the
  session object: nothing persists it and a new window starts in ConstrainedLanguage.
- The confirmation dialog, the title suffix and the lasting note are the window's (parts 3 and 4); the engine only says what is
  possible.

### 4.8 The command list test (D9)

`AdmCommands.Names` is compared with the expected list in a test; a reflection test finds every public type of the assembly
with a `[Cmdlet]` attribute and fails if one is not in the catalog or has a write verb (`Set`, `New`, `Remove`, `Move`,
`Rename`, `Add`, `Enable`, `Disable`, `Unlock`, `Reset`, `Clear`, `Restore`, `Update`, `Write`, `Invoke`, `Grant`, `Revoke`);
a run-time test lists `Get-Command -Name *-Adm*` in a live session and compares it with the catalog. R2 changes the list and the
test together.

### 4.9 History, syntax and completion

- `PowerShellHistoryPolicy.ShouldRemember(line)` is a pure function over the parser's tokens and AST: a line is **not**
  remembered if it has the `-AsPlainText` parameter, a `ConvertTo-SecureString` with a literal string argument, or a named
  parameter whose name contains `Password` with a literal value (the exact list is in "R1 as built"); an empty line and a
  line that does not parse as complete input are not remembered either. The window's history lives in `AdMng.Presentation` (memory only) and asks this function through the
  session (`IPowerShellSession.ShouldRemember`).
- `PowerShellSyntax.Tokenize(input)` returns spans (`Start`, `Length`, `Kind`: command, parameter, variable, string, number,
  operator, comment, keyword, type, member, other) from `Parser.ParseInput`; `IsComplete(input)` is `true` unless the parse
  errors are all of the "incomplete input" kind (an open quote, bracket or here-string) – it decides Enter versus a new line.
- `CompleteAsync(input, cursor)` calls `CommandCompletion.CompleteInput` on the window's idle runspace and returns the
  replacement span and matches (text, list text, kind, tool tip). File system completion is removed in R1: matches of kind
  `ProviderItem`, `ProviderContainer` and `Namespace` are dropped, and the editor does not call completion inside a string
  that is a path. Parameter completers for AD Manager's commands (part 2) are `IArgumentCompleter`s on the parameters: they read
  `Context.Reader.Schema` (attribute names) and the DNs the session already knows, never the directory.

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
| 1 | docs | `docs/adr/0010-…`, `docs/en/adr/0010-…`, this note, `docs/dev/README.md`, README pair (Track R "Decided"), `CLAUDE.md` layout rows |
| 1 | `AdMng.Core` | `PowerShell/IPowerShellSession.cs`, `PowerShellOutputItem.cs` (items), `PowerShellCompletion.cs`, `PowerShellSyntaxSpan.cs`, `PowerShellLanguageMode.cs` |
| 1 | `AdMng.PowerShell` | `AdMng.PowerShell.csproj`, `PowerShellEngineConsumer.props`, `PowerShellEnvironment.cs`, `PowerShellSession.cs`, `PowerShellSessionOptions.cs`, `IPowerShellLockdownProbe.cs`, `Hosting/WindowHost.cs`, `WindowHostUserInterface.cs`, `WindowHostRawUserInterface.cs`, `Hosting/OutputSink.cs`, `Commands/AdmCmdlet.cs`, `Commands/IAdmWindowContext.cs`, `Commands/AdmCommands.cs`, `Commands/GetAdmVersionCommand.cs`, `Guards/StartJobGuardCommand.cs`, `Guards/ImportModuleGuardCommand.cs`, `Input/PowerShellHistoryPolicy.cs`, `Input/PowerShellSyntax.cs` |
| 1 | `tests/AdMng.PowerShell.Tests` | `PowerShellSessionTests`, `LanguageModeTests`, `HostRoutingTests`, `GuardTests`, `NoFilesTests` (+ the child-process helper), `EnvironmentTests`, `HistoryPolicyTests`, `SyntaxTests`, `CompletionTests`, `AdmCommandCatalogTests`, `PackageLicenseTests`, `EngineMeasurementTests` |
| 2 | `AdMng.PowerShell` | `Commands/Get*Command.cs`, `SearchAdmObjectCommand.cs`, `OpenAdmObjectCommand.cs`, `OutAdmGridCommand.cs`, `ExportAdmListCommand.cs`, `Directory/AdmFilterBuilder.cs`, `AdmObjectProjector.cs`, `AdmAttributeCompleter.cs`, `AdmDnCompleter.cs`, `Commands/IAdmWindowServices.cs` |
| 3 | `AdMng.Core` | `PowerShell/Policies/PowerShellPolicySnapshot.cs`, `PowerShellPolicyReader.cs`, `IPowerShellPolicyRegistry.cs`, `PowerShellPolicyNote.cs`, `IPowerShellSessionFactory.cs` |
| 3 | `AdMng.Presentation` | `PowerShell/PowerShellWindowViewModel.cs`, `PowerShellInputModel.cs`, `PowerShellHistory.cs`, `PowerShellOutputDocument.cs`, `DnLinkFinder.cs`, `Shell/ShellViewModel.Palette.cs` (changed), `Resources/Strings*.resx` + `L.cs` |
| 4 | `AdMng.App` | `PowerShell/PowerShellWindow.xaml(.cs)`, `PowerShellEditor.cs`, `PowerShellSessionFactory.cs`, `WindowsPowerShellPolicyRegistry.cs`, `AppHost.cs`, `Services/DialogService.cs`, `SmokeTest.cs` (changed), `AdMng.App.csproj` (reference), `tests/AdMng.App.UiTests/PowerShell*` |
| 5 | docs | `docs/POWERSHELL.md` + `docs/en/POWERSHELL.md` (the operator's guide: commands, the window, the policies, the trusted mode, what is on disk), `docs/releases/NEXT.md`, the readiness item (if small), the closing checks |

## 9. Tests per layer

### 9.1 `AdMng.PowerShell.Tests` (Linux and Windows)

- the session: a script runs and streams; a long script is stopped (a loop that checks the token, no real sleep); a busy
  session refuses a second run; dispose stops a run; object mode returns the pipeline's objects.
- language mode: ConstrainedLanguage by default (a .NET method call fails with the language-mode error); the switch works;
  it is refused under a fake lockdown (and at creation under `Enforce` the start mode is unchanged and the reason is set); the
  switch is refused while a run is active.
- no profile and no PSReadLine (`$PROFILE` is not loaded, `Get-Module PSReadLine` is empty), `Start-Job` and `Import-Module` of
  an unlisted module fail with the clear message, execution policy `Restricted` on Windows.
- the host routes streams and prompts: `Write-Host`, `Write-Warning`, `Write-Verbose`, `Write-Error`, `Write-Progress`,
  `Read-Host` and `-Confirm` arrive as items and the answers go back; `Get-Credential` is refused.
- the **"no files" test**: a child process (the test assembly started with a filter, so the environment can be set before the
  runtime starts) runs a script in a runspace with `HOME`, `LOCALAPPDATA`, `APPDATA`, `TEMP`, `TMP`, `TMPDIR` and `XDG_*`
  pointing at an empty folder and the test asserts that the folder holds no file afterwards; a positive control
  (telemetry on) shows the check would see a file.
- the environment: the variables of section 4.2 are set, auto-loading is off in the runspace, no `PSHost.*` pipe exists.
- the **module analysis cache proof** (opt-in, `ADMNG_POWERSHELL_SLOW_TESTS=1`, about 40 s): the probe of section 4.2 as a test.
- history policy: lines with `-AsPlainText`, `ConvertTo-SecureString 'x' -AsPlainText -Force`, `-Password 'x'` and
  `-ClientSecret "x"` are not remembered; ordinary lines are; empty and incomplete lines are not.
- syntax: tokens of a command, a parameter, a variable, a string and a comment; `IsComplete` for open quotes and brackets.
- completion: `Get-Ad` returns the Adm commands; a parameter completion works; no file system matches.
- the command catalog (D9): exactly the expected names, no write verb, the live list equals the catalog.
- the package licenses: the output folder has no `sni.dll`.
- the measurement of the first and the second runspace (time and memory), printed as a diagnostic message.

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
- `NUL` as the analysis cache path on Windows (part 4, Windows).
- The stage S readiness item for the policies and the language mode: optional, added in part 5 only if the readiness check's
  shape makes it a small addition.
- The two R3 questions of the README ("To be agreed with the team"): the list of translated RSAT commands and the window's
  credentials for the `ActiveDirectory` module.

## 12. R1 as built

Each part adds its block here; the blocks win over the design above.

### 12.1 Part 1 – decisions and the engine

(written when the part is closed)
