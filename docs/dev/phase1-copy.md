# Phase 1 – "Kopiowanie wartości" (copy track)

Design note for the README roadmap item *Faza 1 → Kopiowanie wartości* (the agreed spec). English, for developers.

## Goals

Copying a value from AD Manager must take one click or one shortcut – no selecting text in a property field, no
dialog. Everything copied comes from the live directory read the window already shows (or a fresh read through the
session's pinned DC); nothing is written to disk (CLAUDE.md rule 1) and nothing in this feature writes to the directory.

## Behaviour

### 1. Copy buttons next to values

* **Details pane** (main window): every attribute value line (`SummaryField` with `CopyText`) gets a small copy button
  (`Ad.CopyButton`), so do the header name, the canonical path and the DN. Status lines (account state, password
  expiry) and the multi-selection summary (counts per type) are not copyable. New lines for users: *Domain logon*
  (`NETBIOS\sAMAccountName`, when the NetBIOS name of the session is known; also for groups), *Mobile*, *Employee ID*,
  *Employee number* and *Manager* (the manager's name – the RDN value of the `manager` DN). Contacts also get *Mobile*.
* All values in the pane are **selectable with the mouse** (`Ad.SelectableText`: a read-only, borderless `TextBox`
  that looks like a `TextBlock`, not a tab stop – keyboard users use the copy buttons, which are tab stops with an
  automation name "Copy: <label>").
* **Property pages:** copy buttons on the text fields e-mail, display name, sAMAccountName (plus a `NETBIOS\` button
  left of the box, like ADUC's pre-Windows 2000 prefix, that copies `NETBIOS\login`), all phone numbers, title,
  department, company and office; on the UPN field (`prefix@suffix` as it will be written) and the Manager field (the
  manager's name). The Object page already had copy buttons (canonical name, GUID, SID, DN...). Employee ID / number
  have no field on ADUC's pages; they are copied from the details pane or the Attribute Editor.
* Confirmation: a short non-modal toast **"Skopiowano: <label>" / "Copied: <label>"** (Info, auto-dismissed, read by
  screen readers – the toast host is a polite live region) and the button's glyph turns into a check mark for 1.5 s
  (`CopyFeedback` behaviour, view only). When the clipboard is held by another process, `IClipboardService.SetText`
  now returns false: no "Copied" toast, only the existing warning.

### 2. Ctrl+C on the object list

* `object.copyRows` (Ctrl+C, list context menu, Action menu, palette): the selected rows, **in display order**, with
  the **visible columns** (same headers as the list) as tab-separated text, one row per line. No header row. The cells
  are formatted off the UI thread (`FormatUncached`, like the CSV export); only the clipboard is written on it.
* `object.copyRowsWithHeaders` (context menu "Kopiuj wiersze z nagłówkami", Action menu, palette; no shortcut –
  Ctrl+Shift+C stays "copy DN" as planned in RESEARCH.md): the same with the column headers as the first line.
* Cells: tabs and line breaks inside a value become spaces; values starting with `=`, `+`, `-` or `@` get an
  apostrophe (formula injection protection, like the CSV export) unless they are plain numbers or phone numbers
  (`+48 22 123 45 67`, `-5`), which cannot run anything and would otherwise get a stray apostrophe in Teams or a
  ticket.
* Columns with confidential values (`SecretAttributes.IsSecret` with the session schema: passwords, LAPS, BitLocker,
  attributes the schema marks confidential) are **left out**; the toast names them. If only such columns are visible,
  nothing is copied (warning).
* Ctrl+C used to copy the DNs of the selection – that is now Ctrl+Shift+C ("Kopiuj jako ▸ Nazwa wyróżniająca").

### 3. "Kopiuj jako…" (Copy as)

* New command category **"Kopiuj jako" / "Copy as"** (`CommandCategories.CopyAs`); the palette shows e.g.
  "Adres e-mail · Kopiuj jako · Ctrl+Shift+M" and finds it by "kopiuj mail". The category is bound to the document area
  like the Object category (shortcuts act on the list selection only).
* Descriptors (all `ShellViewModel.CopyAsCommand` with a `CopyFormat` parameter, except DN):

  | Id | Title (pl) | Value | Joined with |
  |---|---|---|---|
  | `copyAs.mail` (Ctrl+Shift+M) | Adres e-mail | `mail` | `"; "` (Outlook "To" field), duplicates removed |
  | `copyAs.upn` | Nazwa UPN | `userPrincipalName` | new line |
  | `copyAs.login` | Nazwa logowania (sAMAccountName) | `sAMAccountName` | new line |
  | `copyAs.domainLogin` | DOMENA\login | `NETBIOS\sAMAccountName` (disabled without NetBIOS name) | new line |
  | `copyAs.name` | Nazwa | RDN value (`name`) | new line |
  | `copyAs.displayName` | Nazwa wyświetlana | `displayName` | new line |
  | `object.copyDn` (Ctrl+Shift+C) | Nazwa wyróżniająca (DN) | DN (existing command, moved into the category) | new line |
  | `copyAs.path` | Ścieżka kanoniczna | `contoso.test/Firma/IT/Jan Kowalski` (from the DN) | new line |
  | `copyAs.guid` | GUID (objectGUID) | `objectGUID` as `D` format | new line |
  | `copyAs.sid` | SID (objectSid) | `objectSid` string | new line |

* Works for one or many selected objects. Name, path and DN come from the rows; the other formats read the selected
  objects **fresh** from the session's DC in batches (`DirectoryLookup.ReadManyAsync`, 100 DNs per OR filter, off the
  UI thread, cancellable) – list rows only hold the attributes of the visible columns.
* Objects without the value (no mail, deleted meanwhile, a computer without UPN...) are **skipped and counted**:
  "Skopiowano adresy e-mail: 5. Pominięto obiekty bez adresu e-mail: 2." (warning severity when something was
  skipped). Nothing found → warning, clipboard untouched. A directory error → error dialog.
* Shown in the list context menu and the Action menu as the submenu **"Kopiuj jako"**, in the toolbar as a copy button
  that opens the same menu, and in the palette (Ctrl+K).

### 4. Copy from list tabs

* **E-mail addresses** tab (proxyAddresses): "Kopiuj adres" button, context menu and Ctrl+C copy the selected address
  **without** its `smtp:`/`SMTP:` (or other type) prefix; works on read-only fields too.
* **Member Of / Members**: context menu and Ctrl+C on the direct list: name(s) (Ctrl+C), DN(s), e-mail address(es) of
  the selection. The Members page also has "Kopiuj adresy e-mail" copying the addresses of **all** members in the list
  (the filtered list, pending removals excluded) as a `"; "` list, skipping members without mail (groups without mail,
  computers, unresolved DNs) with the skipped count in the toast.
* **Session log**: Ctrl+C / context menu "Kopiuj wpis" copy the selected entry as labelled lines (time, operator, DC,
  object + DN, action, attribute, before → after, result, error). Copy DN moves to Ctrl+Shift+C. Secret values are
  never in the log (the executor records them as null).
* **Watchdog event details**: `CopyDetails` now goes through the same helper (toast "Skopiowano: Szczegóły
  zdarzenia", no toast when the clipboard is busy) and its button gets the check-mark feedback.

### 5. Confidential values

* None of the copy formats above is a secret attribute; the shared helpers still refuse an attribute that
  `SecretAttributes.IsSecret(attribute, schema)` reports (defence in depth, e.g. a schema that marks `employeeID`
  confidential).
* TSV rows leave secret columns out (see 2). The details pane never shows secret attributes.
* The only way to copy a secret stays the existing explicit reveal: the Attribute Editor's "Copy" on a secret row uses
  `IClipboardService.SetSecret` (excluded from clipboard history / cloud clipboard, cleared after 60 s); generated
  passwords keep their own copy path.

## Edge cases

* Empty selection: commands disabled (palette hides them). Session ended: copy-as disabled, rows/DN copy still work
  (they only use what the list shows).
* Selection order vs display order: rows and values are emitted in the list's current order (sorted / filtered).
* Multi-valued or long values: values are joined by the formatter (`; `); TSV replaces line breaks.
* Language switch: labels come from the localizer at copy time; the details pane is rebuilt on culture change.
* Clipboard busy (RDP redirection, clipboard managers): `SetText` returns false → warning toast only.

## Files

* `src/AdMng.Presentation/Services/PlatformServices.cs` – `IClipboardService.SetText` returns `bool`.
* `src/AdMng.Presentation/Infrastructure/ClipboardCopy.cs` – copy + confirmation toast helpers.
* `src/AdMng.Presentation/Shell/ObjectCopyFormats.cs` – `CopyFormat`, attributes, value extraction, separators.
* `src/AdMng.Presentation/Shell/ShellViewModel.Copy.cs` – copy-as, rows (TSV), details pane copy commands.
* `src/AdMng.Presentation/Lists/ListClipboardText.cs` – TSV formatting of rows and visible columns.
* `src/AdMng.Presentation/Shell/ObjectSummaryViewModel.cs` – copyable fields, name/path/DN fields, new user lines.
* `src/AdMng.Presentation/PropertySheets/*` – field copy commands (text, UPN, manager, proxy address, membership).
* `src/AdMng.Presentation/Dialogs/SessionLogViewModel.cs` – copy entry.
* `src/AdMng.Presentation/Localization/L.Copy.cs` + `Strings*.resx` – texts (en + pl).
* Small edits: `ShellViewModel.Palette.cs` (descriptors next to `object.copyDn`), `ShellViewModel.Commands.cs`
  (state refresh), `ShellViewModel.cs` (NetBIOS name for the summary), `MainWindow.xaml(.cs)` (details pane template,
  Action menu, toolbar button, key bindings of the new category), `ObjectListView.xaml(.cs)` (context menu, Ctrl+C),
  `PropertiesWindow.xaml`, `SessionLogWindow.xaml`, `WatchdogEventDetailsView.xaml`, `Themes/Controls.xaml`
  (`Ad.SelectableText`, `Ad.CopyButton`), `Behaviors/CopyFeedback.cs`, `Services/ClipboardService.cs`.

## Tests

* Presentation (B2Harness: real engines + `FakeDirectorySession` with the Polish sample company): copy-as for each
  format, multi-selection with skipped objects and the `"; "` mail list, display order, NetBIOS login, rows TSV with
  and without headers, secret column left out, formula protection vs phone numbers, busy clipboard, details pane
  fields and copy, property field copies (mail, UPN, NETBIOS\login, manager), proxy address without prefix, members'
  mail list, session log entry, Watchdog copy consistency, palette entries and shortcuts, localization keys.
* UI (Windows CI): details pane copy buttons and selectable values without binding errors, "Kopiuj jako" submenu
  wired to the descriptors, Ctrl+C on the list, property page copy buttons, session log Ctrl+C.
