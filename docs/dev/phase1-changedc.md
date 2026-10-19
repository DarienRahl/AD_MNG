# Phase 1 – "Change Domain Controller" during a session (track `changedc`)

Design note for the roadmap item *„Zmień kontroler domeny” w trakcie sesji* (README, phase 1). Until now a session
stays on the DC it was opened on; changing it meant Disconnect → connect window → connect again (re-typing the
password). This note describes the in-session switch.

## Behaviour

* **Entry points** – one shell command `session.changeDc` ("Change Domain Controller..." / „Zmień kontroler
  domeny...”), enabled while connected: *File* menu (next to *Disconnect*), the context menu of the domain root in
  the navigation tree (as in ADUC), and the command palette.
* **Dialog** (modal, `ChangeDomainControllerWindow`):
  * header: domain and the current DC;
  * list of the domain's DCs: name, site, "your site", roles (current, PDC emulator, global catalog, read-only DC).
    Sources, merged by host name (case-insensitive):
    1. the directory, read through the current session (authoritative): `server` objects and their
       `CN=NTDS Settings` (`nTDSDSA`) under `CN=Sites,<configuration NC>` – site = the server's site container,
       GC = `options` bit 1, RODC = `objectCategory` `NTDS-DSA-RO`, domain membership = `msDS-HasDomainNCs`;
       PDC emulator = `fSMORoleOwner` of the domain head;
    2. the existing locator (`IDirectoryConnector.FindDomainControllersAsync`, DsGetDcName + DNS SRV on Windows):
       whether a DC is in the client's site, and the client's site name; DCs that only DNS knows are listed too.
       The non-Windows fallback entry (the domain name itself) is not a DC and is skipped.

    Both reads are best effort and run off the UI thread; if both fail the dialog says so and a name can still be
    typed. The current DC is always listed and preselected (marked "current").
  * **"This domain controller"** – a list entry or a free-text host name/address (validated: no spaces, URL schemes,
    `/`, or `host:port`).
  * **"Any writable domain controller in my site"** – resolves to the current DC if it already qualifies (nothing to
    do), otherwise the first writable in-site DC in locator order. Disabled with a reason when the client's site is
    unknown (no locator, e.g. Linux) or no writable DC of the site is known.
  * **"Remember in the connection profile «X»"** checkbox, shown only when the current connection came from a saved
    profile (`LastProfileName`, matching domain, authentication and channel security). Off by default; when checked
    the profile's `Server` becomes the chosen DC (or empty = automatic for "any writable DC in my site"). Never
    stores a password (profiles cannot hold one).
  * warning line for a read-only DC (writes are refused by an RODC) and when the selection is the current DC.
* **Switch** (`ISessionManager.SwitchDomainControllerAsync`), respecting *one DC per session* (ADR-0005):
  1. if background operations run, the operator confirms first (they are cancelled only once the new DC answered);
  2. `IDirectoryConnector.ConnectToServerAsync(current, server)` opens a **new** session on the chosen DC with the
     current session's `ConnectionOptions` (authentication, channel security, port, user name, timeouts) and its
     in-memory credential. The credential never leaves `AdMng.Directory`: the new session gets its own copy of the
     connection settings (the old session's disposal clears only its own reference). Bind, RootDSE, schema,
     identity – the normal connect path – run off the UI thread, cancellable;
  3. verification: the new session must serve the same domain (`defaultNamingContext`) and be another DC than the
     current one (an alias/IP of the current DC is reported as "already connected");
  4. running operations are cancelled (`ActivityCenterViewModel.CancelRunningAsync`, like Disconnect);
  5. the new session becomes current (same slot in `Sessions`), `SessionChanged` is raised, the old session is
     closed.

  Any failure (unreachable DC, bind refused, other domain, cancellation, the session changed meanwhile) closes the
  new session and keeps the old one current and open; the dialog shows a localized error (with the connect hints of
  `ErrorTranslator.TranslateConnectError`).
* **After the switch**
  * the shell recognises a DC switch (previous and new session: same domain NC and bound identity) and, instead of
    the full re-initialisation used after a reconnect, keeps the open tabs: the tree is rebuilt and every list tab
    reloads its location from the new DC; the header/status bar shows the new DC; the details pane is refreshed;
  * windows opened in the previous session stay pinned to it (existing `WindowViewModelBase` behaviour: no reads,
    no writes); their "session ended" text now says they belong to the previous DC `X` and that the session works
    with `Y` – reopen to continue;
  * the session log (`IAuditLog`) records the switch – action `ChangeDomainController`, old → new DC, operator,
    success or the error (never a password) – shown localized in the session log window;
  * a toast confirms the new DC; the profile is updated only when the checkbox was ticked.
* **Cancellation / no UI blocking** – while connecting, Change is disabled and Cancel (or Esc) cancels the switch and
  keeps the dialog open; closing the window cancels too. Discovery and the switch run on the thread pool.

## Edge cases

| Case | Result |
|---|---|
| Opened while disconnected / session ended while open | "not connected" / "session ended" error, Change disabled |
| Typed name = current DC (FQDN or first label) | Change disabled, hint "already the current DC" |
| Alias or IP of the current DC | connects, detects the same DC, closes the new session, "already connected" |
| DC of another domain | new session closed, error names both domains |
| Unreachable DC / wrong port / bind refused | old session intact, localized error + hint |
| RODC chosen | allowed (browsing), warning that changes are refused |
| Background operations running | confirmation; cancelled only after the new DC was verified |
| Locator and directory both unavailable (the locator's fallback is only the domain name) | message, free text still possible |
| Profile save fails (I/O) | switch stays done, warning toast |
| Disconnect while switching | the new session is closed, nothing replaced; the dialog says the session ended |

## Files

* Core: `Directory/IDirectorySession.cs` – `IDirectoryConnector.ConnectToServerAsync` (default: not supported).
* Directory: `ConnectionSettings.ForServer`, `LdapDirectoryConnector.ConnectToServerAsync`.
* Presentation: `Connection/DomainControllerCatalog.cs` (directory read + merge), `Connection/DomainControllerItem.cs`,
  `Connection/DomainControllerSwitch.cs` (result, exceptions, session log action), `Session/SessionManager.cs`
  (`SwitchDomainControllerAsync`), `Dialogs/ChangeDomainControllerViewModel.cs`, factory method,
  `Dialogs/IWindowViewModel.cs` (previous-DC message), `Dialogs/SessionLogViewModel.cs` (action text),
  `Services/IDialogService.cs`, `Shell/ShellViewModel.ChangeDc.cs` (command, tab-preserving reload), palette entry,
  `Localization/L.ChangeDc.cs`, resx (en + pl).
* App: `Views/ChangeDomainControllerWindow.xaml(.cs)`, `AppHost` registration, `DialogService`, `SmokeTest`,
  File menu item, tree context menu (`TreeMenu`).
* Testing: `FakeDomainControllerSession` – a session of another DC over the same in-memory directory.

## Tests

* Directory integration (Samba lab): switching to another name of the lab DC keeps options and identity and survives
  the disposal of the source session; LDAPS variant; unreachable DC keeps the source session working; closed source
  session refused; `ForServer` validation (unit).
* Presentation: catalog (sites, GC, RODC, PDC, other-domain DCs excluded, merge with locator, current DC always
  listed); session manager (switch, other domain, same DC, failure, cancellation, race with disconnect, before-switch
  hook); dialog view model (list, free text validation, any-writable-in-site, profile update on request only, audit
  entry, activities confirmation, errors, cancellation); shell (tabs kept and reloaded, header, tree, palette command,
  disabled when disconnected); pinned windows show the previous-DC message; session log action is localized;
  localization keys in en + pl.
* UI (Windows CI): the window renders connected and disconnected in en/pl without binding errors, the switch from the
  File menu updates the status bar, smoke test renders the dialog.
