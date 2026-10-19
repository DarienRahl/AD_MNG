# Phase 1 – Logon Hours (design note)

Track "logonhours" of roadmap phase 1 ("Godziny logowania"): ADUC's *Logon Hours…* dialog on the user Account tab, the
`logonHours` codec and a batch operation. Binding for this track; product docs (Polish) live in `README.md`.

## 1. The attribute

`logonHours` (octet string, single-valued, user class and its subclasses) holds **21 bytes = 168 bits, one bit per hour
of the week in UTC**. Bit 0 (least significant bit) of byte 0 is Sunday 00:00–01:00 UTC, bit 7 of byte 0 is Sunday
07:00–08:00, bit 0 of byte 1 is Sunday 08:00–09:00 … bit 7 of byte 20 is Saturday 23:00–24:00 UTC. A set bit permits
logon in that hour. This is the layout Windows (`USER_INFO_11.usri11_logon_hours`, `NetpRotateLogonHours`) and the
Watchdog's `LogonHoursSummary` use.

| Stored value | Meaning |
|---|---|
| attribute absent | no restriction (all hours permitted) |
| 21 × `0xFF` | all hours permitted – **what ADUC writes** when the operator permits everything |
| 21 × `0x00` | no hours: the account can never log on (ADUC writes this, without a warning) |
| any other length | invalid (shown as such, replaced on the next edit) |

AD_MNG follows ADUC: it never clears the attribute, "all permitted" is written as 21 × `0xFF`. Absent and all ones are
**the same schedule**, so opening the dialog and pressing OK on an account without the attribute is a no-op (no write).

## 2. Local time and DST (decision)

ADUC shows the grid in the operator machine's local time by rotating the bitmap by the **current** UTC offset (bias of
`GetTimeZoneInformation` including the daylight bias, divided by 60 – `NetpRotateLogonHours`). The whole week is
rotated by one constant offset; the DC enforces the stored bits in UTC.

AD_MNG does the same:

- offset = `TimeProvider.LocalTimeZone.GetUtcOffset(now)` at the moment the dialog (or the field summary) is built,
  **truncated toward zero to whole hours** (`+05:30` → 5, `-03:30` → -3, like the integer division of the bias);
- local hour-of-week `L = day * 24 + hour` (Sunday = 0); UTC hour-of-week `U = (L - offset) mod 168`.

Consequence (same as ADUC, documented for operators): in Poland a schedule entered in winter (CET, UTC+1) as Mon–Fri
08–16 is stored as 07–15 UTC and is shown – and enforced – as 09–17 in summer (CEST, UTC+2). The dialog states the
offset it uses ("czas lokalny tego komputera, UTC+02:00") and that the DC applies the hours in UTC. Tests pin the
behaviour for Europe/Warsaw on both sides of both switches in 2026 (29 March 01:00 UTC → +2, 25 October 01:00 UTC → +1).

## 3. Core (`src/AdMng.Core`)

- `ActiveDirectory/LogonHours.cs` – immutable `LogonHours` (168 UTC bits, value equality): `All`, `None`,
  `FromBytes`/`TryFromBytes` (exactly 21 bytes), `FromAttribute(byte[]?)` (absent → `All`, invalid → null),
  `TryParseHex`/`ToHex` (42 hex characters, the pipeline and Watchdog form), `ToBytes`, `IsPermittedUtc`,
  `IsAllPermitted`/`IsNonePermitted`, `ToLocalGrid(offset)`/`FromLocalGrid(grid, offset)`, `OffsetHours(zone, instant)`.
  `LogonHoursGrid` – mutable 7 × 24 local-time grid indexed by `[DayOfWeek, hour]` (the editing buffer).
- **Binary values in the change pipeline.** The pipeline is string based (`AttributeChange` values, audit, rollback,
  CSV report) and rejected binary attributes (`BinaryNotSupported`). `Batch/PipelineValues.cs` defines the one
  exception: attributes in `PipelineValues.HexEncodedAttributes` (only `logonHours`) travel as lowercase hex strings.
  `Read` (directory object → pipeline strings), `Normalize` (operator/plan value → canonical hex) and `ToWire`
  (pipeline string → `byte[]` for the session) are used by `ObjectPlanDraft` (original values), `PlanValidation`
  (instead of `BinaryNotSupported`: the value must be 21 bytes of hex), `BatchExecutor` (modify/create values and the
  read-back confirmation after an ambiguous retry) and `PowerShellExporter` (`[byte[]](0x..,…)` literals). Every other
  binary attribute is still `BinaryNotSupported`.
- `SetLogonHoursOperation(LogonHours Hours)` – user and inetOrgPerson accounts only (others: `NotApplicable` warning,
  like the password operations); no change when the current effective schedule equals `Hours` (absent ≡ all ones);
  an invalid current value is replaced. Compare-and-swap, audit, rollback (old hex ↔ new hex) and the PowerShell
  export work unchanged on the hex values.

## 4. Presentation (`src/AdMng.Presentation`)

- `Dialogs/LogonHoursViewModel` – the dialog (no session, pure editing): rows = days in the UI culture's week order
  (Polish: Monday first, as the Polish ADUC; English: Sunday first), 24 hour columns, cells with `IsPermitted`,
  `IsSelected`, `IsFocused` and a localized automation name ("Poniedziałek 08:00–09:00, logowanie dozwolone").
  Rectangular selection (anchor + focus) set by click/drag (`BeginSelection`/`ExtendSelection`, Shift+click extends
  from the anchor), row header (whole day), column header (one hour on every day), corner (everything). Keyboard:
  arrows move the focused cell, Shift+arrows extend, Home/End jump to the first/last hour, Space flips the selection to
  the opposite of the focused cell, Ctrl+A selects all. `Permit`/`Deny` commands and the radio state
  `IsSelectionPermitted`/`IsSelectionDenied` (both false for a mixed selection), selection text
  ("Poniedziałek – Piątek, 08:00–16:00"), summary, "no hours" warning, time-zone note, `Result` (UTC `LogonHours`),
  `HasChanges`. Read-only mode: everything visible, the commands disabled.
- `IDialogService.ShowLogonHoursAsync(LogonHoursViewModel)` – modal, true when the operator pressed OK.
- `PropertySheets/LogonHoursFieldViewModel` – on the Account tab after the pre-Windows 2000 logon name (ADUC order):
  label, summary and a "Godziny logowania…" button. The button always opens the dialog; the dialog is read-only when
  the schema/`allowedAttributesEffective` do not allow writing `logonHours` (the field shows the lock and the reason).
  OK with changes makes the field dirty; Apply sends `SetLogonHoursOperation` through the sheet's normal change plan
  (validation, conflict check, audit, activity, rollback) – never a direct write. The sheet's "changed on server" check
  compares the pipeline (hex) form.
- `BatchEditing`: `OperationType.LogonHours` with `LogonHoursEditorViewModel` ("Ustaw godziny logowania"): summary +
  "Godziny logowania…" button opening the same dialog; builds `SetLogonHoursOperation`. The preview shows the
  before/after schedules as summaries instead of hex; an absent value of an existing account (before a first
  restriction, after its undo) is shown as "Wszystkie godziny" - an empty cell would read like "no hours".
- `LogonHoursText` – summary in local time with localized short day names, days with identical hours grouped
  ("pon–pt 8–16; ndz 9–13", "Wszystkie godziny", "Brak dozwolonych godzin"), week order, hour and offset labels. The
  Polish short day names match the Watchdog's (Sunday is "ndz", never "nie").
- Localization: `Labels.LogonHours*`, `LogonHours.*`, `Operation.LogonHours`, `OperationHint.LogonHours` (en + pl).

## 5. App (`src/AdMng.App`)

- `Views/LogonHoursWindow.xaml(.cs)` – modal, `IDialogResultWindow`. Grid of 7 × 24 cells (ItemsControl of rows,
  `UniformGrid` of cells), row and column header buttons and the corner "Wszystkie" button (bound through a
  `BindingProxy`; not focusable, and a click hands the keyboard focus to the grid - otherwise arrows would stop working
  and Space would click the header again), radio buttons *Logowanie dozwolone* / *Logowanie zabronione* with the
  legend swatches (permitted = accent fill, denied = plain cell), selection text (a live region announced after every
  change), summary, "no hours" warning, time-zone note, keyboard hint, OK/Cancel. Mouse: press on a cell starts a
  selection, moving with the button pressed extends it (mouse capture on the grid; the cell is computed from the
  position, clamped to the grid), Shift+click extends. Keyboard on the focusable grid as in 4. Every automation name is
  null-safe. The window sizes to its content, so every text line is capped at 720 px (a long summary of a fragmented
  schedule would otherwise widen the window past the screen). On the Account tab and in the batch editor the
  "Godziny logowania…" button carries the summary as its automation help text (the summary itself is not focusable).
- `AppHost`: `AddWindow<LogonHoursViewModel, LogonHoursWindow>()`; `DialogService.ShowLogonHoursAsync`; `SmokeTest`
  renders the dialog; templates for the field (`PropertiesWindow.xaml`) and the batch editor (`BatchEditorWindow.xaml`).

## 6. Edge cases

- Attribute absent / all ones / all zeros / wrong length (summary "Nieprawidłowa wartość", OK writes a valid value).
- Non-user objects (computers inherit `logonHours` but ADUC does not offer it): no field; the batch operation skips them.
- Schema without `logonHours` (never in AD, possible in tests): no field.
- Half-hour time zones (truncation), offsets across DST switches, wrap-around of the rotation across Saturday/Sunday.
- Concurrent change by another admin: the compare-and-swap of the executor reports the conflict (bytes compared).
- Read-only (no permission): dialog read-only, nothing staged.

## 7. Tests

- Core: codec (bit order, hex, absent/all/none/invalid, rotation, round trip for every offset −12…+14, Warsaw DST
  switches), planner (no-op, absent ≡ all ones, invalid current, non-user skipped, validation of hex values for
  `SetValuesOperation`), executor (bytes written, compare-and-swap conflict, rollback restores the old bytes),
  PowerShell export literal.
- Presentation: dialog view model (selection by drag/header/corner/keyboard, permit/deny, radio state, read-only,
  result in UTC, Polish order Monday first), field (summary, dirty, operations, read-only), property sheet end to end
  (Apply writes 21 bytes through the pipeline, audit entry, no write for an unchanged dialog), batch editor operation
  (preview summaries, execution, rollback), localization keys.
- Integration (Samba lab): plan → execute → read back 21 bytes → rollback through the real LDAP session.
- UI (Windows CI): dialog renders and edits by keyboard and mouse handlers without binding errors, the property sheet
  field opens the dialog and stages the change, the batch editor template; screenshot "Godziny logowania".
