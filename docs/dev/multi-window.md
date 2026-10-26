# Multi-window connections and object comparison – design note

The operator asked for several domains open at the same time, each with its own credentials ("Dodaj też możliwość
otworzenia innej domeny jednocześnie z innymi creds"). This note is binding for the track `feature/multi-window`:

* **one process, one main window per connection.** Each window has its own session (domain, DC, credential in memory),
  tree, lists, documents, property windows, tools, activity centre and session log;
* **shared:** settings, connection profiles, saved views, localization and theme;
* **comparison:** an object of one window compared with the matching object of another open connection. It is
  read-only and shows a side-by-side diff of attributes and group membership.

CLAUDE.md rules 1–6 apply unchanged. Rule 4 (one DC per session) holds per window: a window never reads through
another window's DC, except for the explicit, read-only comparison, which reads each side through that side's own
pinned session (section 8.2).

## 1. Decisions at a glance

| Topic | Decision |
|---|---|
| Composition | One `AsyncServiceScope` per connection window, created by the app-wide `ConnectionWindowManager`. Everything that touches a session, the change bus, the audit log, activities, dialogs, toasts or window ownership is **Scoped**. Stateless engines and settings stay singletons. |
| Window flow | *Plik → Nowe połączenie w nowym oknie…* (`Ctrl+Shift+N`, palette `session.newWindow`) opens an **unowned** connect window in a new scope. If it is cancelled, the scope is disposed. If it connects, the main window of that scope opens. The application exits when no main window and no pending connect window remain. |
| Identity | The title starts with the domain: `contoso.test – CONTOSO\jan.kowalski – <app title>`. A 4 px colour strip and a badge show the connection colour. The colour comes from the profile, or is derived from the domain name. The strip and a header line appear in every child window and message dialog of the connection. Destructive confirmations name the domain in the sentence. |
| Shared state | `ISettingsStore` (one `settings.json`), profiles, saved views (portable `{domain}`), `ILocalizer`, theme, Watchdog service address, SMTP settings, templates and the user-message daily budget. Recent and pinned containers are filtered per window by naming context. |
| Watchdog | One service for the whole application (unchanged). The tab exists per window. When the service's domain differs from the window's, the tab says so and object history is hidden in that window. |
| Comparison | `ObjectComparisonViewModel` / `CompareObjectsWindow`, opened from the selected user, group, computer or contact. The target connection comes from `ConnectionRegistry`. Matching is by rule (automatic, sAMAccountName, UPN prefix, mail, name, explicit DN or search). The window has an attributes tab (volatile and identity attributes hidden by default) and a membership tab (direct, primary, nested; groups by name, well-known groups by RID or SID). Each side uses its own formatter and schema. CSV is written only when the operator asks. The other side is reachable only through a read-only `IDirectoryReader`. |
| Out of scope | Per-window UI threads, owner-only modal dialogs, copying or synchronizing between connections, foreign security principals, several Watchdog services, restoring the set of windows at start (section 12). |

## 2. Composition: one DI scope per connection window

### 2.1 Shape

```
root provider (singletons)
├─ ConnectionWindowManager      (App, singleton: IConnectionWindows, IAppErrorPresenter, IAsyncDisposable)
├─ ConnectionRegistry           (Presentation, singleton: open connections, colour assignment)
├─ ISettingsStore, ViewLibrary, ILocalizer, IDirectoryConnector, IWatchdogClient, engines …
│
├─ scope #1  ── ConnectionNavigator ─ ConnectWindow ─ MainWindow ─ child windows
│              ConnectionContext · ISessionManager · IDirectoryChangeNotifier · IAuditLog · IBatchExecutor
│              ActivityCenterViewModel · IChangeCoordinator · DialogService · WindowService · ToolWindows …
└─ scope #2  ── (the same set again, for the second connection)
```

* **`ConnectionWindowManager`** (`src/AdMng.App/Connections/ConnectionWindowManager.cs`, singleton).
  * Holds the list of `ConnectionWindowEntry(Guid Id, AsyncServiceScope Scope, ConnectionNavigator Navigator)`.
  * Creates scopes through `IServiceScopeFactory`.
  * Implements `IConnectionWindows` (Presentation: `OpenNewConnection()`, `Activate(Guid connectionId)`) and
    `IAppErrorPresenter` (2.7).
  * Shuts the application down after the last window (3.5) and disposes leftover scopes on exit.
* **`ConnectionNavigator`** (`src/AdMng.App/Connections/ConnectionNavigator.cs`, scoped) replaces the singleton
  `AppNavigator`. It does today's job for its own scope only:
  * the connect window;
  * the main window on success;
  * reconnect after *Rozłącz* (connect window owned by its main window);
  * `MainWindow` and `ConnectWindow` properties for owner resolution.

  It is the only code that resolves `MainWindow` (transient, resolved exactly once per scope) and `ConnectWindow`.
  Nothing else resolves `MainWindow` or reads `Application.Current.MainWindow` for ownership.
* **`ConnectionContext`** (`src/AdMng.Presentation/Connections/ConnectionContext.cs`, scoped, `IDisposable`).
  * `Id` (`Guid`).
  * `ProfileName` and `PreferredColor`: what the connect dialog used. Set by `ConnectViewModel` before
    `ConnectAsync`.
  * `Identity` (`ConnectionIdentity?`, null while disconnected), plus `Label`, `TitleSuffix` and a `Changed` event.
  * It subscribes to its `ISessionManager.SessionChanged`, rebuilds `Identity` from `Session.Info`, and
    registers/updates itself in `ConnectionRegistry`. `Dispose` unregisters it.
  * As an `IConnectionHandle`, it is the read-only view other windows get (8.2).
* **`ConnectionRegistry`** (`src/AdMng.Presentation/Connections/ConnectionRegistry.cs`, singleton).
  * `IReadOnlyList<IConnectionHandle> Connections` (connected windows, in opening order) and a `ConnectionsChanged`
    event.
  * `AssignColor` (4.3).
  * Lives in Presentation, so scopes and the comparison can be tested on Linux without WPF.

### 2.2 Lifetimes

**Per connection (Scoped).** Presentation, `src/AdMng.Presentation/ServiceCollectionExtensions.cs`, changes
`TryAddSingleton` to `TryAddScoped` for:

* `ISessionManager`/`SessionManager`, `IDirectoryChangeNotifier`/`DirectoryChangeNotifier`,
  `IAuditLog`/`InMemoryAuditLog`, `IBatchExecutor`/`BatchExecutor`, `ActivityCenterViewModel`,
  `IChangeCoordinator`/`ChangeCoordinator`, `ObjectListServices`, `PowerShellHistory`, `DialogServices`,
  `IDialogViewModelFactory`/`DialogViewModelFactory`, `UserMessages.UserMessageServices`, `WatchdogServices`,
  `WatchdogObjectHistoryLauncher`, `Func<WatchdogDocumentViewModel>`;
* the new `ConnectionContext`;
* `ShellViewModel`, which becomes **Scoped** (today it is transient), so that a window has exactly one shell that
  `ToolWindows` and the navigator can reach.

**Transients resolved inside the scope.** `ConnectViewModel`, `NavigationTreeViewModel`, `CommandPaletteViewModel`,
`SavedViewEditorViewModel`, `SessionLogViewModel`, `PowerShellHistoryViewModel`, `RecycleBinViewModel`,
`SettingsViewModel`, `CsvImportViewModel`, and the `ObjectListViewModel` factory. Shell tabs are still created by
`ObjectListServices.CreateList()` (not resolved), so a closed tab is not kept alive by the scope until the window
closes.

**Per connection (Scoped), App side.** In `src/AdMng.App/AppHost.cs`:

* `IWindowService`/`WindowService`;
* `DialogService` with its forwards `IDialogService`, `IUserMessageLauncher` and the new `IObjectComparisonLauncher`
  (all `AddScoped(sp => sp.GetRequiredService<DialogService>())`);
* `ToolWindows`, `ConnectionNavigator`, `NotificationService` + `INotificationService`;
* `IClipboardService`/`ClipboardService`: it needs the scoped toast service, and secret auto-clear stays correct per
  scope because it only clears its own secret;
* `IFileDialogService`/`FileDialogService`: the owner is this connection's window;
* `ConnectWindow` and `MainWindow` stay transient but are resolved only from the scope.

`AppHost` drops its own `AddSingleton<IAuditLog, InMemoryAuditLog>()` and `AddSingleton<IBatchExecutor,
BatchExecutor>()`, because the Presentation registrations, now scoped, apply.

**App-wide (Singleton).**

* Engines: `TimeProvider`, `ITemplateEngine`, `ICsvCodec`, `PasswordGenerator`, `IChangePlanner`,
  `IRollbackPlanner`, `IPowerShellExporter`, `ICsvImportPlanner`, `BatchReportExporter`, `AuditLogExporter`,
  `BatchEngines`.
* Directory and localization: `IDirectoryConnector` (stateless session factory), `IAttributeFormatterFactory`,
  `ILocalizer` (+ static `TranslationSource.Localizer`), `IRegionalSettings`, `ErrorTranslator`.
* Settings: `ISettingsStore`, `ViewLibrary`.
* Watchdog: `IWatchdogClient`, `WatchdogConnectionService`.
* User messages: `SmtpCredentialCache`, `WatchdogUserMessageTransport`, `UserMessageTransportSelector`,
  `UserMessageTemplateLibrary`, the new `MessageRateLimiter` (extracted from `UserMessageServices`: the daily budget
  stays application-wide) and `IDirectSmtpTransportFactory`.
* App services: `WpfUiDispatcher`/`IUiDispatcher` (one UI thread), `IAppPaths`, `IAppShutdown`, `ThemeService`,
  `AppearanceService`, `GlobalExceptionHandler`.
* Windows and connections: all `WindowRegistration` records, `ConnectionWindowManager`, `ConnectionRegistry`.

### 2.3 How `ValidateScopes` catches mistakes, and what it does not catch

`AppHost.Build` already uses `ValidateOnBuild = true` and `ValidateScopes = true`.
`tests/AdMng.Presentation.Tests/ServiceRegistrationTests.cs` does the same for `AddAdMngPresentation`.

* **Caught at build.** A constructor-activated singleton that depends, directly or transitively, on a scoped
  service fails with "Cannot consume scoped service … from singleton …". So forgetting to make `DialogServices`,
  `ToolWindows` or `UserMessageServices` scoped breaks `AppHost.Build`: the smoke test and every UI test on
  Windows. On Linux it breaks `ServiceRegistrationTests` for the Presentation part.
* **Caught at first resolution.** A factory lambda (`sp => sp.GetRequiredService<X>()`) is opaque to build
  validation. With `ValidateScopes`, a scoped service resolved from the root provider throws "Cannot resolve scoped
  service … from root provider", also inside a singleton factory, whose `sp` is the root. This covers:
  * the `IDialogService`/`IUserMessageLauncher` forwards;
  * `GlobalExceptionHandler`'s `() => sp.GetRequiredService<IDialogService>()`, which today is caught and only
    logged, so the dialog would silently disappear (2.7);
  * `Func<WatchdogDocumentViewModel>`;
  * the test factories `ObjectDialogTestSupport.CreateContextAsync` and `WatchdogUiSupport.UseClock`.

  Only a test that resolves them finds these. Therefore:
  * **`HostTests.EveryRegistrationResolvesInItsLifetime`** (UI tests, Windows): builds the real host and walks
    the `IServiceCollection`. Singletons resolve from root, scoped and transient services from a fresh scope.
    Windows are skipped, because they need a view model; they are covered by the smoke test. Two scopes must give
    different `ISessionManager`, `IDirectoryChangeNotifier`, `IAuditLog`, `ActivityCenterViewModel`,
    `IDialogService` and `NotificationService` instances, and the same `ISettingsStore`, `ViewLibrary`,
    `MessageRateLimiter` and `ConnectionRegistry`.
  * **`ServiceRegistrationTests`** gets the same walk for Presentation (with the test doubles of the UI
    services), plus `Assert.Throws<InvalidOperationException>` for `ISessionManager` from root.
* **Not caught.** A scoped service subscribing to a singleton event without unsubscribing keeps a closed window
  alive. 2.6 lists them. `ConnectionScopeTests.ClosedScopeIsCollected` creates a scope, connects, opens a shell,
  disposes the scope, forces GC and asserts that `WeakReference`s to `ShellViewModel`, `ActivityCenterViewModel`,
  `WatchdogObjectHistoryLauncher` and `ConnectionContext` are dead.

### 2.4 Child windows resolve from their owner's scope

* **Window creation.** `WindowService` is scoped, so its `IServiceProvider` is the scope.
  `ActivatorUtilities.CreateInstance(_services, …)` therefore builds every window with that connection's
  `IDialogService`, `IClipboardService` and so on. `DialogService`, `ToolWindows` and the view-model factory are
  in the same scope, so a property sheet, the batch editor or a tool opened from window B is B's, all the way down.
* **Attached `ConnectionChrome.ConnectionId`.** It is set on every window the scoped `WindowService` prepares, and
  on the connect and main window by `ConnectionNavigator`. `WindowService` then filters by it:
  * `GetOwner()`: this connection's active window, else its main window, else its connect window, else `null`
    (center screen). Never another connection's window, so a confirmation for domain B is never centred on A.
  * `Show(vm, key)`: the owner is the connection's main window, not `Application.Current.MainWindow`. Closing A
    therefore never closes B's batch editor.
  * `FindOpen(key)` / `FindOpen(predicate)` search only windows with the same `ConnectionId`. The "one per
    application" keys in `ToolWindows` (`tools:session-log`, `tools:powershell-history`, `tools:recycle-bin`,
    `tools:account-review`) become "one per connection" without changing the strings. `DialogService.FindPropertySheet`
    finds only this connection's sheet, even when another window shows the same DN.
* **`ToolWindows`.**
  * `HasUnsavedGeneratedPasswords` scans only this connection's `BatchEditorWindow`s.
  * `ShowSettings()` uses `ConnectionNavigator.MainWindow.Shell`.
* **Drag and drop.** `ObjectListView` adds the format `"AdMng.ConnectionId"` (the GUID as a string) next to
  `"AdMng.DistinguishedNames"`. `MainWindow.OnTreeDragOver`/`OnTreeDrop` accept the drop only when the id equals
  their own. A foreign drop shows the "no" cursor and the status text *Nie można przenosić obiektów między
  połączeniami*. Without this, DNs of domain A dropped on B's tree would be moved in B, which is a real move when the
  DNs coincide.

### 2.5 Session manager per window

`SessionManager` becomes a single-connection manager:

* `ConnectAsync` closes the previous current session, off the UI thread, after `SessionChanged`, instead of keeping
  it in `Sessions` until exit.
* `Sessions` returns at most one entry. Its doc comment changes from "a later phase" to "at most one; one manager per
  connection window".
* `WindowViewModelBase.Session` keeps its meaning. Inside a scope, "the current session" is "this window's
  session", so the roughly 100 existing `Sessions.Current` call sites stay correct without edits.

### 2.6 Event subscriptions that must end with the scope

| Subscriber (scoped) | Singleton source | Change |
|---|---|---|
| `ActivityCenterViewModel` | `ILocalizer.CultureChanged` (lambda) | becomes `IDisposable`, named handler, unsubscribes |
| `WatchdogObjectHistoryLauncher` | `WatchdogConnectionService.AddressChanged` (lambda) | becomes `IDisposable`, unsubscribes |
| `ShellViewModel`, `NavigationTreeViewModel`, `ObjectListViewModel`, `WindowViewModelBase` | `ISettingsStore.Changed`, `ViewLibrary.Changed`, `ILocalizer.CultureChanged` | already unsubscribe in `Dispose`. The shell is disposed by `MainWindow.OnClosed` **and** by the scope, so the `_disposed` guards must hold (test) |
| `ConnectionContext` | `ConnectionRegistry` (registration, not an event) | unregisters in `Dispose` |

### 2.7 Errors outside a connection

`GlobalExceptionHandler` stays a singleton. It receives `IAppErrorPresenter` (implemented by
`ConnectionWindowManager`) instead of the root-resolved `IDialogService`. The presenter shows the error through the
`IDialogService` of the connection that owns the active window (via `ConnectionChrome.ConnectionId`), else that of
the most recently activated connection. With no connection window at all, it shows an unowned `MessageDialogWindow`
built with a scope-less clipboard helper. The navigator's error path uses its own scope's `IDialogService`.

## 3. Window lifecycle

### 3.1 New connection window

* **Entry points.**
  * *Plik → Nowe połączenie w nowym oknie…* (`Commands.NewConnectionWindow`, en "New connection in a new
    window…"), right below *Połącz…*;
  * `Ctrl+Shift+N` (free today);
  * the palette entry `session.newWindow` (category Session);
  * the connection badge's drop-down (4.4).

  All of them call `IConnectionWindows.OpenNewConnection()`. The command is enabled while disconnected too.
* **Shell wiring.** `ShellViewModel` gets two optional trailing constructor parameters, `IConnectionWindows?` and
  `ConnectionContext?`. Like `historyLauncher` and `messages`, this keeps the shell construction in `TestHarness`
  unchanged. `IDialogService` gets no new member, so its three test doubles (`ScriptedDialogService` in the UI and
  Presentation tests, `TestDialogService`) stay as they are.
* **App start.** `App.Start` calls `ConnectionWindowManager.OpenNewConnection()` instead of
  `AppNavigator.ShowConnect()`.

### 3.2 The connect dialog of a new window

* **Placement.** The window is unowned and centred on the screen. If it were owned by the window that opened it,
  closing that window would kill it. Its title is *Nowe połączenie – <app title>* while other windows are open.
* **Profile preselection.** It preselects `LastProfileName` as today. When that profile is already open in another
  window, an information line says so ("Ten profil jest już otwarty w oknie …", with the colour dot of that window).
  It does not block: the same domain with another account is a supported case.
* **Colour.** New field *Kolor okna*: *Automatyczny (z nazwy domeny)* or one of the palette colours (4.3). It is
  saved into the profile together with the other profile fields when the operator saves or remembers the profile.
* **Credentials.** The password is typed per window. A new window never reuses another window's credential
  (rule 2). The `PasswordBox` is cleared after the connection, as today.
* **Cancel.** Closing the dialog without a connection makes the navigator report "cancelled". The manager disposes
  the scope, and shuts the application down only if no other entry exists (3.5).

### 3.3 Inside a window: Disconnect, reconnect, Change DC

The existing flow stays and only affects its own scope:

* *Rozłącz* cancels **this window's** activities, closes its session and shows its connect window (owned by its
  main window).
* Reconnecting may target another domain. The identity then changes everywhere (title, strip, badge, registry
  `ConnectionsChanged`), and child windows of the previous session are marked ended by the existing
  `WindowViewModelBase` logic.
* *Zmień kontroler domeny* keeps the identity colour (same domain and account). The badge tooltip shows the new DC.
* `ChangeDomainControllerViewModel.FindProfile` uses `ConnectionContext.ProfileName` instead of the global
  `LastProfileName`, so "remember DC in profile" can never overwrite the profile of another window.

### 3.4 Closing one window

1. **`MainWindow.OnClosing`.** It asks, for this connection only:
   * running activities: `Shell.CloseWindowWhileRunning`, "Operacje w tle w oknie {domain} nadal trwają…";
   * unsaved generated passwords (`ToolWindows` of this scope);
   * unsaved documents.

   *Plik → Zamknij okno* (`Alt+F4`, `Ui.CloseWindow`) replaces the single *Zakończ*.
2. **`OnClosed`.** WPF closes the owned child windows. The navigator then calls this scope's
   `ActivityCenterViewModel.CancelRunningAsync()`, so **only this window's** batches and searches are cancelled.
3. **Close the session.** The manager closes the session off the UI thread with a bound
   (`Task.Run(() => sessions.DisposeAsync()).WaitAsync(5 s)`; a hanging unbind is abandoned and logged by type only).
   It then awaits `scope.DisposeAsync()` on the UI thread. `SessionManager.DisposeAsync` is idempotent, so the
   scope's second call is a no-op and never blocks the UI (rule 6).
4. **Unregister.** `ConnectionContext.Dispose` unregisters it. Comparison windows of other connections that used it
   see `Closed` (8.2).

### 3.5 Last window and *Zakończ*

* **Exit condition.** The manager calls `IAppShutdown.Shutdown()` when its entry list becomes empty: no main window
  and no pending connect window.
* ***Plik → Zakończ program*** (`Ui.ExitAll`). It closes every connection window in turn, starting with the
  others. Each window asks its own questions with its own domain in the text. The first *Nie* stops the sequence.
* **`App.OnExit`.** It awaits `ConnectionWindowManager.DisposeAsync()` first, before the existing host stop. That
  disposes leftover scopes (Windows logoff, crash path) concurrently, each bounded to 5 s, 10 s in total. Disposing
  the root no longer reaches scoped session managers.

### 3.6 Modal dialogs across windows (known limitation)

WPF `Window.ShowDialog` disables **every** window of the UI thread, not only its owner. A confirmation, *Resetuj
hasło*, *Nowy obiekt*, a picker or the settings in window A therefore briefly blocks input in window B. Background
work in B continues.

A dialog that B raises from a background continuation while A's dialog is open is owned by B, carries B's header
(4.4), and nests (A's result returns after B's dialog closes). That is acceptable for short dialogs.

Owner-only ("window-modal") dialogs and per-window UI threads are non-goals (section 12). All windows share the one
`WpfUiDispatcher`.

### 3.7 Background work

Background work is per window by construction:

* the scoped activity centre, coordinator and executor;
* the scoped change bus, so a write in A never refreshes or removes rows in B. Two windows on the same domain see
  each other's changes only through LDAP live updates (`WatchAsync`) or *Odśwież*, like two ADUC consoles;
* the session log and PowerShell history, which are per window.

Each window costs about 4 pooled LDAP connections, one live-update subscription per open container, and the
Watchdog polling described in section 6. There is no limit on the number of windows.

## 4. Identity

### 4.1 Data

`ConnectionIdentity` (`src/AdMng.Presentation/Connections/ConnectionIdentity.cs`, record):

* `DomainDnsName`, `DomainNetBiosName`, `DefaultNamingContext`, `BoundIdentity`, `Server`, `ProfileName`;
* `Color` (`ConnectionColor`) and `ColorSource` (`Profile` / `Automatic`);
* `Ordinal`: 1, or 2, 3… when an identical domain + account is already open;
* `Label`, e.g. `contoso.test · CONTOSO\jan.kowalski`, plus ` (2)` for a second identical window.

### 4.2 Title

`Shell.WindowTitleConnection` is "{0} – {1} – {2}": domain, account (with the ordinal), app title. Example:
`contoso.test – CONTOSO\jan.kowalski – Użytkownicy i komputery usługi Active Directory`. Domain first, so Alt+Tab
and taskbar thumbnails show it even when truncated.

Child windows get `TitleSuffix` (" – contoso.test"). `ConnectionChrome.Apply` in the scoped `WindowService.Prepare`
replaces the window's own `Title` binding by a `MultiBinding` of that binding and `ConnectionContext.TitleSuffix`, so
the 19 window XAML files do not change.

### 4.3 Colour

* **Palette.** `ConnectionColor` has eight values: `Blue, Teal, Green, Purple, Magenta, Indigo, Brown, Slate` (pl:
  niebieski, morski, zielony, fioletowy, purpurowy, indygo, brązowy, grafitowy).
  * Amber and red are deliberately absent. Amber is the existing Tier 0 banner (`IdentityBannerStyle`,
    `IsPrivilegedIdentity`), which stays as it is. Red means destructive or error.
  * `src/AdMng.App/Themes/ConnectionColors.xaml` defines a strip brush and a badge background and foreground per
    colour for the light and dark themes. Badge text contrast is at least 4.5:1, checked by a UI test that computes
    the contrast ratio.
* **Source.**
  1. `ConnectionProfile.Color` (`ConnectionColor?`; null = automatic), also selectable in the connect dialog for a
     connection without a saved profile;
  2. automatic: `palette[Fnv1a32(domainDnsName.ToLowerInvariant()) % 8]`. It is stable across runs and machines
     (never `string.GetHashCode`, which is randomized per process), so operators learn that "contoso is blue".
* **Collisions** (`ConnectionRegistry.AssignColor`).
  * An automatic colour that an open window **of another identity** (domain + account) already shows moves to the
    next free palette entry.
  * Open windows keep their colour. A closed window frees its colour.
  * Profile colours are never changed. Two profiles with the same chosen colour are the operator's choice; the domain
    text still differs.
* **Not colour alone.** Colour is never the only cue. The domain text is always next to it (title, badge, dialog
  header), which also covers colour-blind operators.

### 4.4 Strip, badge, dialogs, taskbar

* **Main window.**
  * A 4 px strip in the connection colour across the top, above the menu.
  * A badge (`src/AdMng.App/Controls/ConnectionBadge.xaml`) at the right end of the menu bar: a colour pill with the
    domain and account; the tooltip shows the DC, channel security and profile.
  * Clicking the badge opens the list of open connection windows (colour dot, domain, account, DC; the current one
    checked) plus *Nowe połączenie w nowym oknie…*. Choosing an entry calls `IConnectionWindows.Activate`.
  * The disconnected state shows a neutral grey strip and *Nie połączono*.
* **Child windows.** `ConnectionChrome.Apply` wraps the window content in a `DockPanel` with the same strip and a
  one-line header (domain · account in the connection colour).
  * The settings window gets no chrome, because settings are shared. Its title is *Ustawienia (wspólne dla
    wszystkich okien)*.
  * The PowerShell history gets the chrome, because it is the history of this connection.
* **Message dialogs.** `MessageDialogViewModel` gets `ConnectionLabel` and `ConnectionColor`, which the scoped
  `DialogService` fills for every information, error and confirmation dialog. The dialog shows the strip and the
  line *Domena: contoso.test (CONTOSO\jan.kowalski)* above the message. This covers all 31 `ConfirmAsync` /
  `ConfirmTypedAsync` call sites without touching them.
* **Taskbar.** `TaskbarItemInfo.Overlay` shows a dot in the connection colour and `Description` the label. This is
  best effort: Windows shows the overlay depending on taskbar grouping. The title is the reliable cue.

### 4.5 Domain named in destructive confirmations

On top of the header, the sentence itself names the domain. These resources get a `{domain}` argument (en + pl):

| Resource | Sentence |
|---|---|
| `Delete.ConfirmOne`, `Delete.ConfirmMany` | "Usunąć … z domeny contoso.test?" |
| `Delete.TypeToConfirm` | unchanged; the subtree warning names the domain |
| move | the confirmation in `ShellViewModel.Commands.cs:163` |
| `Batch.ConfirmExecute` | the typed confirmation of the batch editor, also used by the coordinator above the threshold |
| reset password | `ResetPasswordViewModel.cs:230`, typed |
| restore from the Recycle Bin | `RecycleBinViewModel.cs:459` |
| account review action | `AccountReviewViewModel.cs:367` |
| message send | the confirmation in `UserMessageComposerViewModel` ("do N odbiorców z domeny …") |
| disconnect and close window while running | `Shell.DisconnectWhileRunning`, `Shell.CloseWindowWhileRunning` |

The expected texts of typed confirmations stay as they are (count or name). The domain is read from
`DialogServices.Connection` (`ConnectionContext`), which `DialogServices` gets as a new constructor parameter.
`TestHarness` creates one per harness.

## 5. What stays shared

### 5.1 Shared state

| Shared | Note |
|---|---|
| `ISettingsStore` / `settings.json` | Language, theme, page size, threshold, parallelism, details pane, Advanced Features, live updates, column layouts and the Watchdog sub-tab apply to every window. Each shell reacts to `ISettingsStore.Changed`: the reload that `ToggleAdvancedFeaturesAsync` does for its own shell today moves into the shell's `Changed` handler, so tree and lists reload in **every** window. |
| Connection profiles | One list; editing a profile in one window's connect dialog is seen by the next connect dialog. New field `Color`. |
| Saved views (`ViewLibrary`) | Portable (`{domain}`), so they show in every window and resolve against that window's naming context. |
| Pinned containers | Stored as absolute DNs. A window shows only pins under its own `DefaultNamingContext` (`DistinguishedName.IsDescendantOf` or equal); the others stay in the settings and are not deleted. |
| Recent containers | `AppSettings.MaxRecentContainers` goes from 10 to 30. Each window shows its 10 newest entries under its own naming context, so domain A's history is not pushed out by B. |
| Localization, theme | `TranslationSource.Localizer` and `AppearanceService` are app-wide; a language switch re-renders all windows. |
| Watchdog address and certificate pin, SMTP settings, templates, daily message budget | App-wide (6). |

`LastProfileName` keeps its meaning ("preselect in the next connect dialog"). The profile **of a window** is
`ConnectionContext.ProfileName` (3.3).

### 5.2 Concurrent settings writes

* **Today.** All windows share one UI thread. `JsonSettingsStore.UpdateAsync` applies the change synchronously,
  normalizes, serializes a snapshot and then writes under `_writeGate`. A later snapshot always contains every
  earlier change.
* **Hardening.** With several windows two writes can overlap, so two changes are added:
  1. **Generation.** Every `UpdateAsync` takes `++_generation` with its snapshot. `WriteAtomicAsync` skips a snapshot
     whose generation is lower than the last written one, so an older snapshot can never overwrite a newer file.
  2. **Lock.** The in-memory part (change, `Normalize`, serialize) runs under a `Lock`, so a call from a thread-pool
     continuation cannot interleave with a UI-thread call. `Changed` is still raised outside the lock on the
     caller's thread.
* **Test.** `SettingsTests.ConcurrentUpdatesFromTwoWindowsKeepBothChanges` uses two scopes, interleaved updates and a
  slow fake file system; the file ends with both changes and the highest generation.
* **Two processes.** Starting `AdMng.exe` twice still gives two independent processes with last-writer-wins on the
  file, as today. The supported way is the new window command; single-instance forwarding is a non-goal.

## 6. The Watchdog tab per window

* **One service.** There is still one configured service: one `WatchdogConnectionService`, one `IWatchdogClient`,
  Kerberos as the Windows user and not the LDAP credential. Each window has its own tab view model (scoped
  `WatchdogServices`, `Func<WatchdogDocumentViewModel>`), its own sub-tab state and its own polling.
* **Domain check.** `WatchdogStatusViewModel` already reads `StatusDto.Domain`. When it differs from the window's
  `DomainDnsName` (case-insensitive):
  * the tab shows an information bar: *Usługa Watchdog monitoruje domenę fabrikam.test; to okno jest połączone z
    contoso.test*. The events stay readable, because the feed is still useful;
  * `WatchdogObjectHistoryLauncher.IsAvailable` becomes false in that window, so *Historia zmian (Watchdog)* is hidden
    for its objects, whose DNs would not match;
  * background badge polling stops in that window. Only matching windows poll in the background; the visible tab
    always polls.

  An unknown domain (older service, status not read yet) behaves as today.
* **Launcher.** It is scoped, so `Requested` reaches only its own shell and an object-history request never opens
  the tab in another window.
* **Event details actions.** `WatchdogEventDetailsViewModel.SessionForEvent` already refuses a session of another
  domain. Actions such as unlock go through **this window's** coordinator.

## 7. Command palette, menus and recent items

* **Palette.** It is per window (transient in the scope); its commands act on its own shell. New descriptors in
  `ShellViewModel.Palette.cs`:
  * `session.newWindow`: *Nowe połączenie w nowym oknie*, `Ctrl+Shift+N`, category Session.
  * `window.activate:<id>`: generated per other open connection, *Przejdź do okna: fabrikam.test ·
    FABRIKAM\anna.zielinska*, rebuilt on `ConnectionRegistry.ConnectionsChanged`.
  * `object.compareWithConnection`: *Porównaj z obiektem w innym połączeniu…*, category Object, no shortcut. Its
    `DisabledReason` says *Zaznacz jeden obiekt: użytkownika, grupę, komputer lub kontakt* or *Otwórz drugie
    połączenie (Ctrl+Shift+N)*.
* **Menus.** *Plik*: *Połącz…*, **Nowe połączenie w nowym oknie…**, *Zmień kontroler domeny…*, *Rozłącz*, …,
  **Zamknij okno**, **Zakończ program**. The object context menu of the list and the details pane gets *Porównaj z
  obiektem w innym połączeniu…* for one user, group, computer or contact.
* **Recent items.** The address bar drop-down and the tree's recent list show the window's filtered recent
  containers (5.1).

## 8. Comparison of objects between two connections

### 8.1 UI

* **`CompareObjectsWindow`** (`src/AdMng.App/Views/CompareObjectsWindow.xaml`; view model
  `ObjectComparisonViewModel`).
  * Modeless and owned by the source main window, with the source connection's chrome.
  * Opened through the scoped `DialogService` as `IObjectComparisonLauncher.CompareAsync(string dn)`.
  * Registered with `AddWindow<ObjectComparisonViewModel, CompareObjectsWindow>()`.
  * Several comparison windows may be open.
  * Title: *Porównanie: Łukasz Wiśniewski – contoso.test ↔ fabrikam.test*.
* **Top panel.**
  * *Połączenie*: a combo of the **other** open connections (colour dot, domain, account). When only one other
    connection is open it is preselected.
  * *Dopasuj według*: *Automatycznie*, *sAMAccountName*, *Prefiks UPN*, *Adres e-mail*, *Nazwa (cn)* or *Wskazany
    obiekt*.
  * For *Wskazany obiekt*, a text box takes a DN or a search text. Search results (ANR + object kind, max 50) are
    listed with the target's colour.
  * *Porównaj* / *Przerwij* and *Odśwież*.
* **Status line.** It shows the result of matching: *Znaleziono: CN=…*, *Nie znaleziono*, or *Znaleziono N obiektów
  – wybierz jeden* with a candidate list (name, path, sAMAccountName, mail).
* **Header.** Two columns, A (source) and B (target). Each has its identity strip and badge in its own colour and an
  `ObjectSummaryViewModel` (icon, name, type, DN, account state) built with that side's formatter, schema and policy.
  When both objects have the same `objectGUID`, a banner says *To ten sam obiekt widziany przez dwa konta – różnice
  wynikają z uprawnień do odczytu*.
* **Tab *Atrybuty*.** Columns: attribute (localized display name, LDAP name in the tooltip), value A, value B, state.
  * States: *Takie same*, *Różne*, *Tylko w A*, *Tylko w B*, *Brak w schemacie A/B*. A difference row has an accent
    background **and** a glyph, never colour alone.
  * Toggles: *Tylko różnice* (on by default); *Pokaż atrybuty zmienne i identyfikujące* (off by default, 8.6);
    *Porównuj adresy bez domeny* (on by default, 8.5); a filter box.
  * Multi-valued cells show up to 20 values with *… i N więcej*. The details pane below the grid lists all values of
    the selected row, marked *tylko A* / *tylko B* / *wspólne*.
* **Tab *Członkostwo*.** Columns: group (name on each side), A (*bezpośrednio* / *grupa podstawowa* /
  *zagnieżdżone*), B (the same), state. Toggles: *Tylko różnice*, *Pokaż zagnieżdżone* (on).
* **Tab *Członkowie*** (group objects only). Direct members compared by principal key (8.7); nested members on demand
  (*Pokaż członków zagnieżdżonych*).
* **Other commands.**
  * `Ctrl+C` copies the selected rows as text (secrets masked), as other lists do.
  * *Eksportuj CSV…* (8.10).
  * There is no "copy to the other side", no apply, and no edit.

### 8.2 The other connection: handle, lifetime, rule 4

* **`IConnectionHandle`** (implemented by `ConnectionContext`, listed by `ConnectionRegistry`): `Id`, `Identity`,
  `IDirectoryReader? Reader`, `IAttributeFormatter Formatter`, `Task<DomainPolicy> GetDomainPolicyAsync(ct)`,
  `event SessionChanged`, `event Closed`. It never exposes the other window's coordinator, dialogs, notifier or
  `ISessionManager`.
* **`IDirectoryReader`** (new, `src/AdMng.Core/Directory/IDirectoryReader.cs`): `Info`, `Schema`, `SearchAsync`,
  `GetAsync`, `GetAllValuesAsync`.
  * `IDirectorySession : IDirectoryReader`, so implementers do not change.
  * The handle returns `ReadOnlyDirectoryReader.Of(session)`, an adapter class that forwards only reads. A cast back
    to `IDirectorySession` is impossible.
  * The source side is read through the same adapter over the window's own pinned session. The whole comparison code
    has no write method in reach.
* **Rule 4.** Each side is read through that connection's current pinned session. The comparison never opens a
  session or binds; credentials are never touched.
* **Lifetime.** The view model pins the target's reader when a comparison starts, like `WindowViewModelBase` pins
  its own session.
  * On the target's `SessionChanged` (DC switch, disconnect, reconnect) or `Closed`, or on `ObjectDisposedException`
    / `SessionEndedException` during a read, side B is marked ended: *Połączenie B zostało zamknięte* or
    *…zmieniło kontroler domeny – Odśwież*. Its results stay visible and read-only.
  * *Odśwież* re-reads through the target's new session while it is connected.
  * When the source window's session ends, the existing `IsSessionEnded` behaviour applies.
* **Concurrency.** Sessions are thread-safe (connection pool), so B's window and A's comparison may read B at the
  same time.

### 8.3 Matching

All filters are built with `LdapFilter` (rule 3). The search is a subtree search under the **target's**
`DefaultNamingContext`, AND the object kind filter (`ObjectKind.ToLdapFilter()` of the source), size limit 51.
Requested attributes: `name distinguishedName objectClass sAMAccountName userPrincipalName mail displayName`.

| Rule | Filter | User | Group | Computer | Contact |
|---|---|---|---|---|---|
| sAMAccountName | `LdapFilter.Equal(Attr.SamAccountName, v)` | ✓ (1) | ✓ (1) | ✓ (1) | – |
| UPN prefix | `LdapFilter.StartsWith(Attr.UserPrincipalName, prefix + "@")`, prefix = the source UPN before `@` | ✓ (2) | – | – | – |
| mail | `LdapFilter.Equal(Attr.Mail, v)` | ✓ (3) | ✓ (2) | – | ✓ (1) |
| name (cn) | `LdapFilter.Equal(Attr.Name, v)` (the RDN value) | ✓ (4) | ✓ (3) | ✓ (2) | ✓ (2) |
| explicit | the DN typed (`DistinguishedName.TryParse`, must be under the target NC, read with `GetAsync`), or `LdapFilter.Anr(text)` AND kind | ✓ | ✓ | ✓ | ✓ |

* **Automatic.** Tries the rules in the numbered order, skipping those without a source value. It stops at the first
  rule with **exactly one** result. With only ambiguous results, it shows the candidates of the first ambiguous rule.
  With no result, it shows *Nie znaleziono* plus the rules that were tried.
* **Results.** 0 = not found; 1 = compare; 2–50 = candidate list; 51 = *więcej niż 50 – zawęź wyszukiwanie*.
* **Remembering.** The chosen rule per object kind and the three toggles are remembered in
  `AppSettings.Comparison` (`ComparisonSettings`: names of rules and switches only, no values; rule 1).

### 8.4 Reading one side

`ComparisonReader.ReadAsync(IDirectoryReader reader, string dn, ct)` (`src/AdMng.Presentation/Comparison/`) returns
an in-memory `ObjectSnapshot`:

* the DN, object classes, kind, and raw values (string or `byte[]`) per attribute;
* the side's `DefaultNamingContext`, `DomainDnsName` and schema.

How it reads:

* `GetAsync(dn, ["*", canonicalName, msDS-User-Account-Control-Computed, msDS-UserPasswordExpiryTimeComputed,
  msDS-PrincipalName])`, as the attribute editor does.
* Then ranged completion of truncated multi-valued attributes. `AttributeEditorPageViewModel.CompleteRangedAsync` is
  moved to a shared `Infrastructure/RangedValues.CompleteAsync`, and the attribute editor uses it too.
* `objectGUID` and `objectSid` are always read, also when hidden, for the same-object banner and the primary group.

### 8.5 Attribute comparison rules

The pure diff is `ObjectDiffer.Compare(ObjectSnapshot a, ObjectSnapshot b, ComparisonOptions options)`
(`src/AdMng.Core/Comparison/`). It returns `AttributeComparison` rows: name, state, values A and B, per-value marks
(only A / only B / both), and flags `IsSecret`, `IsVolatile`, `IsTruncated`.

| Case | Rule |
|---|---|
| Attribute set | union of the attributes present on either side, minus those shown on other tabs (`memberOf`, and `member` for groups) |
| Schema | absent from one side's schema → *Brak w schemacie A/B* (e.g. Exchange attributes) |
| Multi-valued | compared as sets (AD does not keep value order) |
| Strings | ordinal comparison; a case-only difference is a difference, marked *tylko wielkość liter* |
| DN syntax (DN, DN-Binary, DN-String: `manager`, `managedBy`, `directReports`, `secretary`, …) | normalized with `SavedView.ToPortableDn(value, sideNc)`, so `CN=Jan Kowalski,OU=IT,{domain}` matches across domains; compared with `DistinguishedName.AreEqual`; the display shows the canonical path of each side |
| `userPrincipalName`, `mail`, `proxyAddresses`, `targetAddress` | with *Porównuj adresy bez domeny* only the part before `@` (and the `smtp:` / `SMTP:` prefix) is compared; a suffix-only difference is shown as *Takie same (inna domena)* |
| Times (GeneralizedTime, FILETIME `Int64`) | compared as instants; the display uses each side's formatter |
| Binary | byte equality; the display is that of the formatter (size, or the formatted value for SIDs and GUIDs) |
| Secrets (`SecretAttributes.IsSecret(name, sideSchema)`, evaluated with **each** side's schema) | values are never compared or shown, only *ustawiony / brak*, masked with `SecretAttributes.Mask`; also in CSV and clipboard |
| Missing values | an attribute the identity cannot read is not returned by LDAP and looks like an empty one; the state text is *brak wartości (lub brak prawa odczytu)*, and the same-object banner (8.1) explains it |

### 8.6 Hidden by default (volatile and identity attributes)

`ComparisonAttributes.Volatile` (`src/AdMng.Core/Comparison/ComparisonAttributes.cs`) is shown only with *Pokaż
atrybuty zmienne i identyfikujące*. It is a port of `NoiseAttributes.Never` (Watchdog.Core is not referenced) plus the
identity attributes:

* **Identity:** `objectGUID objectSid sIDHistory distinguishedName canonicalName msDS-PrincipalName objectCategory
  instanceType mS-DS-ConsistencyGuid`.
* **Times and counters:** `whenCreated whenChanged uSNCreated uSNChanged modifyTimeStamp createTimeStamp pwdLastSet
  lastLogon lastLogonTimestamp lastLogoff logonCount badPwdCount badPasswordTime lockoutTime`.
* **Replication:** `dSCorePropagationData replPropertyMetaData msDS-ReplAttributeMetaData msDS-ReplValueMetaData`.
* **`msDS-*` logon and key counters:** `msDS-LastSuccessfulInteractiveLogonTime msDS-LastFailedInteractiveLogonTime
  msDS-FailedInteractiveLogonCount msDS-FailedInteractiveLogonCountAtLastSuccessfulLogon msDS-KeyVersionNumber
  msDS-RevealedUsers msDS-RevealedList msDS-ManagedPasswordId msDS-ManagedPasswordPreviousId`.
* **Computers:** `msLAPS-PasswordExpirationTime ms-Mcs-AdmPwdExpirationTime`.
* **Prefix rule:** every `msTPM-*` attribute.

The constructed `msDS-User-Account-Control-Computed` and `msDS-UserPasswordExpiryTimeComputed` stay visible: they
answer "is the account locked or expired on both sides". A unit test keeps the list a superset of
`NoiseAttributes.Never` except `memberOf`, which has its own tab.

### 8.7 Membership

Per side, `ComparisonReader.ReadMembershipAsync`:

* **Direct.** `memberOf` (ranged), with group names, `sAMAccountName`, `objectSid` and `groupType` read in batches
  of 100 through `DirectoryLookup.ReadManyAsync`.
* **Primary.** `primaryGroupID` resolved to the group whose `objectSid` = domain SID + RID (domain SID from
  `DomainPolicy.DomainSid`).
* **Nested.**
  * One paged search under the side's default NC:
    `LdapFilter.And(ObjectKind.Group.ToLdapFilter(), LdapFilter.InChain(Attr.Member, dn))`, the same filter as
    `MemberOfPageViewModel.NestedFilter`.
  * Plus the same search for the primary group's DN, because AD does not follow the primary group in `member`.
  * Groups found only here are *zagnieżdżone*.

Matching groups across sides uses `GroupKey` (`src/AdMng.Core/Comparison/GroupKey.cs`):

* a builtin group (`S-1-5-32-*`) → its SID;
* a domain group with RID < 1000 (Domain Admins 512, Domain Users 513, …, Protected Users 525, …) → `rid:<n>`.
  This way *Domain Users* on an English DC equals *Użytkownicy domeny* on a Polish one;
* any other group → `sAMAccountName` (case-insensitive invariant), else `name`.

A row shows both names when they differ. Its state is one of *w obu*, *w obu, inaczej* (direct on one side, nested
on the other), *tylko A* or *tylko B*.

Groups outside the side's domain NC are not listed: groups of other domains of the forest, and memberships through
foreign security principals (non-goal). A footnote under the tab says so.

The *Członkowie* tab for groups uses the principal key `sAMAccountName` (users, computers, groups; well-known groups
by `GroupKey`), else `mail`, else `name` (contacts). FSP members are counted (*N obcych podmiotów pominięto*) but not
compared.

### 8.8 Formatting per side

* Side A uses its own `ISessionManager.Formatter` (in scope). Side B uses `IConnectionHandle.Formatter`: the
  `LocalizedAttributeFormatter` that B's `SessionManager` built for **B's** schema.
* Display names come from `ILocalizer.AttributeName(attr, schemaOfThatSide)`, or A's schema when both have the
  attribute.
* Each side's policy feeds its `ObjectSummaryViewModel`.
* The view model never uses `DialogServices.Sessions` for side B. A test checks this with an attribute whose syntax
  differs between the two fake schemas.

### 8.9 Performance

* **Run order.**
  * Matching and the two object reads run in parallel (`Task.WhenAll`) off the UI thread. The attribute grid
    renders when both are in; the goal on a LAN is under 1 s for an object with 200 attributes.
  * Direct membership follows. Nested membership runs last, in the background, with a progress ring on the tab. It
    can be slow in large domains.
* **Cancellation.** Every read is cancellable: *Przerwij*, closing the window, *Odśwież* (`SerialCancellation`). The
  per-request timeout is the session's `RequestTimeout`.
* **Large values.** Multi-valued attributes with more than 1 000 values (e.g. `member` of a large group) are diffed
  as sets in memory. The cell shows the counts (*wspólne 4 980, tylko A 12, tylko B 3*) and the details pane lists
  the differences. Membership lists are capped at 5 000 groups per side, with a "list truncated" note.
* **Scale.** No cache between runs: everything is read again on *Odśwież* and dropped when the window closes.

### 8.10 CSV export on demand

* **When.** Only on *Eksportuj CSV…*. The operator chooses the file through the scoped `IFileDialogService` (owner =
  this window). Nothing is written otherwise, and cancelling the dialog writes nothing.
* **Format.** Same as `SessionLogViewModel.ExportAsync`: `ICsvCodec`, `CsvFiles.ListSeparator(Regional.Culture)`,
  formula protection, `CsvFiles.WriteAsync`.
* **Columns.** `Sekcja; Atrybut; Nazwa LDAP; Stan; <A label>; <B label>`. The value column headers are the
  connection labels, e.g. *contoso.test (CONTOSO\jan.kowalski)*.
* **Content.** The first data row is always the DN of each object. Then come the rows that the current filters show,
  on both tabs. Multi-values are joined with a new line inside the cell, and secrets are masked.
* **File name.** The suggestion is `porownanie-<name>-<domainA>-<domainB>.csv`.

### 8.11 Read-only guarantee

* The comparison code reads only through `IDirectoryReader` (8.2).
* `ObjectComparisonViewModel` never calls `Services.Coordinator`, the batch editor or any write path, and no command
  in the window writes.
* `ObjectComparisonViewModelTests.ComparisonNeverWrites` asserts that both `FakeDirectorySession` operation logs
  contain no modify, add, delete, move, password or restore operation after a full run, refresh and export.
* Rule 5 is not involved because there is nothing to plan.

## 9. Data handling

* **Rule 1 (nothing on disk).**
  * Open windows, identities, the registry, comparison snapshots and results live in memory only and are released
    when the window or scope closes.
  * New persisted values are settings only: `ConnectionProfile.Color`, `AppSettings.Comparison` (rule names and
    switches) and `MaxRecentContainers` 30. The settings type-graph test still passes: no member name contains
    "password".
  * No list of open connections is stored.
  * Diagnostic logs carry the connection GUID, event kind and counts. They carry no domain names, DNs or values.
  * CSV is written only by the operator's explicit export.
* **Rule 2 (credentials only in memory).**
  * Each credential stays inside its own session (`AdMng.Directory`) and the connect window's `PasswordBox`, which
    is cleared after connecting.
  * `ConnectionContext`, `ConnectionIdentity`, `IConnectionHandle`, `ConnectionRegistry`, `ConnectionWindowEntry` and
    `ObjectSnapshot` hold no credential.
  * Their `ToString()` overrides print only the type and the connection id or attribute count, never values.
  * No window can obtain another window's credential: the handle exposes a reader, not the session, so
    `ConnectToServerAsync(current, …)` cannot be reached with another connection's session.
  * Closing a window disposes its session, and with it the in-memory credential reference.
* **Rule 3.** Every filter and DN in matching and membership goes through `LdapFilter` / `DistinguishedName`. Tests use
  values with `*()\` and Polish diacritics.
* **Rule 4.** One DC per session per window. The comparison reads each side through that side's pinned session and
  opens none.
* **Rule 5.** Every write of a window goes through that window's scoped coordinator, executor and audit log. The
  comparison writes nothing.
* **Rule 6.** All reads are async and cancellable. Scope disposal never blocks the UI thread (3.4).

## 10. Test plan

### 10.1 Core (`tests/AdMng.Core.Tests/Comparison/`)

* `ObjectDifferTests`:
  * set semantics and ordinal or case-only differences;
  * DN normalization across `DC=contoso,DC=test` and `DC=fabrikam,DC=test`;
  * address suffix toggle, instants, binary, secrets compared by presence only (each side's schema);
  * absent from schema, only A or only B, volatile flags.
* `GroupKeyTests`: builtin SIDs, RID < 1000 (*Domain Users* = *Użytkownicy domeny*), `sAMAccountName` fallback,
  `name` fallback.
* `ComparisonAttributesTests`: the superset of `NoiseAttributes.Never` (the list is copied in the test), `msTPM-*`.
* `ReadOnlyDirectoryReaderTests`: forwards reads; the adapter is not an `IDirectorySession`.

### 10.2 Presentation (Linux)

* **`ServiceRegistrationTests`** (rewritten):
  * the walk of 2.3 and two scopes with distinct per-connection instances and shared singletons;
  * root resolution of a scoped service throws;
  * `WatchdogObjectHistoryLauncher` is the same instance as `PropertySheetViewModel.HistoryLauncher` **within one
    scope**.
* **`Connections/ConnectionScopeTests`.** Two scopes: one connected to `new FakeDirectoryBuilder().AddSampleCompany()`
  (contoso.test), one to `new FakeDirectoryBuilder("fabrikam.test")` with the sample company and changes (Łukasz
  Wiśniewski: title *Starszy programista*, city *Wrocław*, not in `GG_IT`, in `GG_Sprzedaz`).
  * Disconnect in A leaves B connected.
  * A write through A's coordinator lands only in A's fake.
  * A's `DirectoryChange` never reaches B's lists.
  * `CancelRunningAsync` in A leaves B's running activity untouched.
  * Session logs and PowerShell histories are separate.
  * The history launcher request in A does not open B's Watchdog tab.
  * The registry lists both, and `ConnectionsChanged` fires on connect, DC switch and dispose.
  * Scope disposal closes only its own session.
  * `ClosedScopeIsCollected` (2.3).
* **`Connections/ConnectionIdentityTests`.**
  * Golden automatic colours for `contoso.test` and `fabrikam.test`.
  * Collision moves only the newer automatic colour; a profile colour wins; a closed window frees its colour.
  * Ordinal for an identical domain + account.
  * Title and label in en and pl (`contoso.test – CONTOSO\jan.kowalski – …`).
  * `ToString` has no account or values.
* **Settings.**
  * `ConcurrentUpdatesFromTwoWindowsKeepBothChanges` and the generation skip (5.2).
  * `ConnectionProfile.Color` round trip.
  * Pinned and recent containers filtered per naming context, and recent kept at 30.
  * Advanced Features toggled in A reloads B's tree.
  * `ChangeDomainControllerViewModel` remembers the DC in **its** window's profile.
* **Confirmations.** Delete, move, batch, reset password, restore, account review action and message send in A put
  `contoso.test` in the message (`ScriptedDialogService` records it), en + pl.
* **Comparison.**
  * `ObjectMatcherTests`: per rule and kind; automatic order; UPN prefix; 0 / 1 / many / over 50 results; explicit DN
    outside the target NC refused; escaping of `Łukasz (test)*\` in every rule.
  * `ComparisonReaderTests`: ranged completion; primary group; nested through `InChain` (`DL_Wszyscy` via `GG_IT`);
    RID matching with a fabrikam fake whose group 513 is named *Użytkownicy domeny*.
  * `ObjectComparisonViewModelTests`:
    * rows and states for Łukasz Wiśniewski, contoso.test against fabrikam.test;
    * toggles, the same-object banner (two scopes on the **same** fake through two wrappers);
    * side B uses B's formatter and schema;
    * B disconnects or switches DC mid-read: side B ended, *Odśwież* re-reads;
    * B's scope disposed: `Closed`;
    * cancellation on close;
    * CSV export (separator, formula protection, masks, labels in headers);
    * a cancelled save dialog writes nothing;
    * `ComparisonNeverWrites`.
* **Localization.** The existing en/pl completeness and placeholder tests cover all new keys (`Connections.*`,
  `Comparison.*`, `Commands.NewConnectionWindow`, `Commands.CompareWithConnection`, `Shell.WindowTitleConnection`,
  `Ui.CloseWindow`, `Ui.ExitAll`, colour names).

### 10.3 UI tests (`tests/AdMng.App.UiTests`, Windows CI)

* **Infrastructure.**
  * `UiTestContext` creates a default connection scope. `context.Services` becomes that scope's provider (it also
    resolves singletons), so the roughly 100 existing resolutions keep working. `context.Host.Services` stays the
    root. `CreateConnectionAsync(FakeDirectorySession)` serves multi-window tests.
  * `FakeDirectoryConnector` takes a `Func<ConnectionOptions, IDirectorySession>` that maps `options.Domain` to a
    fake. Every connect returns a non-owning wrapper, so closing window A never disposes the fake that window B or
    the next test uses.
  * `ObjectDialogTestSupport` (`DialogService`, `TestDialogService`) and `WatchdogUiSupport.UseClock`
    (`WatchdogServices`) register **scoped**.
* **`MultiWindowTests`.**
  * `Ctrl+Shift+N` opens an unowned `ConnectWindow`; connecting to fabrikam.test opens a second `MainWindow`.
  * Titles, strip and badge colours differ.
  * A property sheet and the session log opened in B are owned by B, and the session log of A is a different
    window.
  * A confirmation raised in B is owned by B and shows *Domena: fabrikam.test*.
  * Toasts appear only in their own window.
  * A drag from A's list onto B's tree is refused.
  * Closing A leaves B working and does not call `RecordingShutdown`; closing B, the last window, does.
  * A cancelled new-window connect dialog with another window open does not shut down.
  * *Zakończ program* closes both, asking per window.
  * An unhandled exception with two windows shows the error dialog on the active connection (not silently logged).
* **`CompareObjectsWindowTests`.** Context menu in A → window. B preselected; automatic match; rows with a
  difference; toggles; membership tab; CSV through the fake file dialog; closing B marks side B ended.
* **`HostTests.EveryRegistrationResolvesInItsLifetime`** (2.3). Badge contrast check (4.3).

### 10.4 Smoke test (`src/AdMng.App/SmokeTest.cs`)

The smoke test resolves through a connection scope instead of root. It renders:

* two main windows with different identity strips (contoso.test, fabrikam.test fakes);
* the connect window of a new connection;
* `CompareObjectsWindow` with a finished comparison.

It disposes the scopes with the bounds of 3.4. The published `AdMng.exe --smoke-test` runs it on Windows CI as today.

### 10.5 Lab integration test (`tests/AdMng.Directory.IntegrationTests/Integration/MultiConnectionTests.cs`)

* **Scope.** Engine level, with no view models. It uses two `SessionManager`s with their `ConnectionContext`s in one
  `ConnectionRegistry`, over the real `LdapDirectoryConnector`.
* **Accounts.**
  * Session A: `LabSettings.Tls(LabSettings.AdminUser)`, i.e. `Administrator@contoso.test` / `P@ssw0rd!2026`.
  * Session B: `LabSettings.Tls(LabSettings.ReaderUser)`, i.e. `odczyt.test@contoso.test` / `Passw0rd!Lab2026`.
  * Both are overridable by the existing `ADMNG_TEST_LDAP_*` variables, and the test is skipped without the lab.
* **Test data.** Session A creates, in the run OU (`LabSettings.TestOu`):
  * the users *Zażółć Gęślą* and *Jaźń Żółtowska* (different `department`; `description` only on the first);
  * the groups *GG_Porównanie_Wewn* ∈ *GG_Porównanie_Zewn*;
  * memberships: the first user in the inner group, the second user directly in the outer group.

  Cleanup is the existing run-OU cleanup.

| Test | Asserts |
|---|---|
| `TwoCredentialsTwoIndependentSessions` | `BoundIdentity` A = `CONTOSO\Administrator`, B = `CONTOSO\odczyt.test`; both read the run OU; disposing A leaves B searching; the registry lists both, then one |
| `SameObjectThroughTwoIdentities` | automatic match of the first user from A in B (sAMAccountName) → same DN and `objectGUID` (same-object flag); with volatile hidden, every difference is *Tylko w A* (read rights), never *Różne*; membership: inner group direct and outer group nested on both sides |
| `DifferentObjectsByExplicitDn` | the first user (A) against the second user (B, explicit DN): `department` *Różne*, `description` *Tylko w A*; membership: inner group *tylko A*, outer group *w obu, inaczej* (nested in A, direct in B) |
| `ComparisonUsesTheSessionsPinnedDc` | `Info.Server` of both readers equals their session's DC (rule 4) |

### 10.6 Screenshots (`tests/AdMng.App.UiTests/ScreenshotTests.cs`)

* `20-two-connections.png`.
  * Two main windows rendered off-screen at 1100×700 and composed side by side by `ScreenshotWriter`.
  * Left: contoso.test (Blue, `CONTOSO\jan.kowalski`), OU *IT* open.
  * Right: fabrikam.test (Green, `FABRIKAM\anna.zielinska`), OU *Sprzedaż* open.
* `21-compare-objects.png`. The comparison of *Łukasz Wiśniewski*, contoso.test ↔ fabrikam.test, on the *Atrybuty*
  tab with *Tylko różnice* on (title, city), with the membership difference visible in the tab badge.

Both are published to the `screenshots` branch like the others and referenced from README in stage 2.

## 11. Staged implementation plan

Every step ends with `dotnet build AdMng.slnx` at 0 warnings and the affected test projects green (twice for
Presentation). UI tests and the smoke test run on Windows CI.

**Stage 1 – scopes, windows, identity**

1. **Presentation scopes.**
   * Lifetimes (2.2); `SessionManager` single-connection (2.5).
   * `ConnectionContext`, `ConnectionIdentity`, `ConnectionColor` (+ `ConnectionColors.Automatic`),
     `ConnectionRegistry`, `IConnectionHandle` (without `Reader` until stage 2), `IConnectionWindows`.
   * `MessageRateLimiter` extracted; `ActivityCenterViewModel` and `WatchdogObjectHistoryLauncher` made
     `IDisposable`.
   * `ShellViewModel` scoped, with title, settings-change reload, `session.newWindow` and `window.activate:<id>`.
   * `ChangeDomainControllerViewModel` profile from the context.
   * Pinned and recent filtering; `JsonSettingsStore` generation and lock; `ConnectionProfile.Color` plus the
     connect-dialog colour.
   * Domain in confirmations (4.5) with resources en + pl.
   * Watchdog domain check (6).
   * Tests of 10.2 (without comparison).
2. **App windows.**
   * `AddAdMngApp` lifetimes; `ConnectionWindowManager`, `ConnectionNavigator` (`AppNavigator` removed).
   * Scoped `WindowService`, `DialogService`, `ToolWindows`, `FileDialogService`, `ClipboardService`,
     `NotificationService`.
   * `ConnectionChrome`, `ConnectionBadge`, `ConnectionColors.xaml`, the `MessageDialogViewModel` header, the taskbar
     overlay.
   * `IAppErrorPresenter`; drag-and-drop guard; *Zamknij okno* / *Zakończ program*; `App.Start` / `App.OnExit`.
   * Smoke test; UI test infrastructure, `MultiWindowTests`, `HostTests`; screenshot 20.
3. **Docs for stage 1.**
   * `docs/ARCHITECTURE.md` (Polish): composition with a scope per window.
   * `CLAUDE.md`: new windows resolve from the connection scope, and per-connection services are never resolved from
     root.
   * README feature line *Wiele połączeń jednocześnie*.

**Stage 2 – comparison and docs**

4. **Core.** `IDirectoryReader` (+ `IDirectorySession : IDirectoryReader`), `ReadOnlyDirectoryReader`,
   `Comparison/` (`ObjectSnapshot`, `ObjectDiffer`, `ComparisonOptions`, `ComparisonAttributes`, `GroupKey`,
   `MembershipComparer`) and the tests of 10.1.
5. **Presentation.**
   * `Infrastructure/RangedValues` (extracted).
   * `Comparison/` (`ObjectMatchRule`, `ObjectMatcher`, `ComparisonReader`, `ObjectComparisonViewModel`,
     `AttributeComparisonRowViewModel`, `MembershipComparisonRowViewModel`).
   * `ComparisonSettings`, `IObjectComparisonLauncher`, the `object.compareWithConnection` command, handle `Reader`.
   * Tests of 10.2 (comparison).
6. **App.** `CompareObjectsWindow`, the launcher in `DialogService`, context menus, smoke test, UI tests, screenshot 21.
7. **Lab.** `MultiConnectionTests` (10.5) in the existing lab CI job.
8. **Docs (Polish).**
   * README: features *Wiele połączeń jednocześnie* and *Porównanie obiektów między połączeniami*, screenshots 20/21,
     the phase 4 status of "Wiele domen … porównanie dwóch obiektów".
   * `docs/ROADMAP.md`.
   * ADR-0010 *Okno na połączenie – zakres DI na okno* (next free number).
   * Release notes with the next release.

## 12. Non-goals

* **Threading and dialogs.** Separate UI threads per window. Owner-only (window-modal) dialogs: WPF `ShowDialog`
  stays thread-modal (3.6).
* **Window model.** Several domains in **one** window or tree (an ADUC console with several domains), or a forest-wide
  view through the global catalog.
* **Writes between connections.** Copying, moving or synchronizing objects or attribute values between connections;
  "apply B's value to A"; dragging between windows. The comparison is read-only.
* **Membership scope.** Foreign security principals, memberships across domains or forests, trusts.
* **Comparison scope.** Comparing two objects of the **same** connection: the engine allows it, but the picker lists
  only other connections. Comparing more than two objects, or whole OUs.
* **Credentials.** Reusing a credential in another window. Every window authenticates by itself.
* **Settings.** Per-window or per-domain settings (language, theme, Advanced Features, column layouts).
* **Process and session restore.** Single-instance forwarding of a second `AdMng.exe` start. Restoring the set of
  open windows at the next start.
* **Watchdog.** Several Watchdog services (one per domain).
* **Change notifications across windows.** Propagating one window's pipeline changes to another window of the same
  domain; LDAP live updates cover the visible container.

## 13. Known limitations and risks

* **Modal dialogs.** Thread-modal dialogs block all windows for their duration (3.6).
* **Same-domain windows.** Two windows on the same domain do not see each other's pipeline changes instantly (3.7).
* **Read rights.** "Not readable" and "empty" cannot be told apart in general. The same-object banner and the state
  text say so (8.5).
* **Nested membership cost.** Nested membership through `LDAP_MATCHING_RULE_IN_CHAIN` can take seconds in large
  domains. It runs last, in the background and cancellable (8.9).
* **Watchdog per window.** The Watchdog tab in a window of another domain shows the service's domain, with a warning
  (6).
* **Validation coverage.** App-level DI lifetime mistakes surface only on Windows CI, in UI tests and the smoke test,
  because `AppHost.Build` needs WPF. `ServiceRegistrationTests` covers the Presentation part on Linux (2.3).
