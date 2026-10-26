# U2 – Account review ("Przegląd stanu kont") – design note

Roadmap item U2 (README, "Ścieżka U"). Read-only review of user accounts with one paged query, client side checks on
constructed values, lists per check and hand-over to the existing write paths. Everything stays in memory (CLAUDE.md
rule 1); every write goes through the change plan (rule 5); one window = one session = one DC (rule 4) – the only
other sessions are the short-lived, read-only ones of "exact last logon / lock state on all DCs".

## 1. Pieces

| Piece | Project | Purpose |
|---|---|---|
| `AccountStatus` | Core (`ActiveDirectory/`) | Pure evaluation of one user at `now` in a time zone: lock, password expiry (FGPP via `msDS-UserPasswordExpiryTimeComputed`), account expiry ("end of day"), last logon, password age, UAC hygiene flags. Reused by the Account tab, later by U1 template fields and U3 reminders. |
| `AccountCheck`, `AccountReviewQuery` | Presentation (`AccountReview/`) | The checks, their server pre-filters (built from the phase-1 chips `QuickFilters` where one exists) and client predicates. |
| `AccountReviewScope` | Presentation | Domain, OU with subtree, saved view (base, scope, filter and chips of the view), group members (nested). |
| `DomainControllerReader` | Presentation | "On all DCs": reads attributes of a few hundred DNs on every DC of the domain through separate sessions opened with `IDirectoryConnector.ConnectToServerAsync` (each pinned to its DC, closed right after). |
| `AccountReviewViewModel` + `AccountReviewWindow` | Presentation / App | The window: scope, parameters, check set, *Sprawdź*, tiles, list, actions. |
| `AccountStatusFieldViewModel` | Presentation (property sheets) | Summary on the Account tab, with "check on all DCs". |
| `IUserMessageLauncher` | Presentation | U1 hook: no implementation is registered, so every *Wyślij wiadomość…* stays hidden until U1 adds one. |

## 2. Query

One paged subtree search (page size from the settings) over **user accounts only** –
`(sAMAccountType=805306368)` (no computers, gMSA, trusts, contacts) – AND the scope filter AND the OR of the pre-filters
of the enabled checks. Attributes: `name displayName sAMAccountName userPrincipalName objectClass userAccountControl
msDS-User-Account-Control-Computed msDS-UserPasswordExpiryTimeComputed lockoutTime pwdLastSet accountExpires
lastLogonTimestamp whenCreated mail proxyAddresses manager memberOf adminCount isCriticalSystemObject showInAddressBook
description` (+ `msExchHideFromAddressLists` when the schema has it). The constructed attributes are valid in a subtree
search in AD (only their use in a filter is not); a DC that does not return them falls back as described in 3.

Results are streamed (flush every 150 ms or one page) and evaluated on the UI thread in batches; rows that match no
enabled check are dropped right away. "Przerwij" cancels the search (abandons the paging); the window shows what was
evaluated so far and says the review was stopped. "Stan na godz." = the reference time `now` of the run.

Extra queries, only when their check is enabled:

* **Privileged** – the protected groups by SID (`S-1-5-32-544/548/549/550/551/552`, domain RIDs 512, 516, 517, 518, 519,
  520, 521, 526, 527) in one search, then one DN-only search of the users in scope with
  `(|(adminCount=1)(memberOf:1.2.840.113556.1.4.1941:=<group>)…)` – nested membership on the server.
* **Manager disabled** – after the stream, managers that were not in the results are read in batches of 100
  (`(|(distinguishedName=…)…)`, `userAccountControl` only).

Scope filters: domain – base = default NC; OU – base = OU, subtree; saved view – its base (with `{domain}`), scope,
filter and chips (`QuickFilters.Combine`); group – base = default NC,
`(|(memberOf:1.2.840.113556.1.4.1941:=<group>)(primaryGroupID=<RID of the group>))` (members whose primary group is a
nested member are not found – as `Get-ADGroupMember -Recursive`).

## 3. Checks

`E` = enabled `(!(userAccountControl:1.2.840.113556.1.4.803:=2))`, `D` = disabled
`(userAccountControl:1.2.840.113556.1.4.803:=2)`, FT = FILETIME, GT = GeneralizedTime, `now` = reference time,
"days left" = calendar days in the operator's time zone between today and the last valid day. All problem checks look
at enabled accounts only; "Disabled" and the leftovers look at disabled ones.

| Check | Server pre-filter | Client side |
|---|---|---|
| Locked now | E, `(lockoutTime>=1)` (chip *Zablokowane*) | bit 0x10 of `msDS-User-Account-Control-Computed`; when the DC does not return it, `lockoutTime + lockoutDuration` of the domain policy (`lockoutTime` stays set after the lockout expired) |
| Password expired | E, `(pwdLastSet>=1)`, not `DONT_EXPIRE_PASSWORD` (65536) | `msDS-UserPasswordExpiryTimeComputed` ≤ now, or bit 0x800000 (`PASSWORD_EXPIRED`) of the computed UAC |
| Password expires in ≤ N days | as above | now < expiry and days left ≤ N; last day = local date of the expiry moment |
| Account expired | E, `(accountExpires>=1)(accountExpires<=FT(now))` (chip *Konto wygasło*) | instant ≤ now; `0` and `2^63−1` = never |
| Account expires in ≤ N days | E, `(accountExpires>=FT(now))(accountExpires<=FT(now+N+1 d))` | days left ≤ N; last day = local date of `accountExpires − 1 tick` (ADUC "End of: D" stores the midnight that starts D+1) |
| Disabled | D (chip *Wyłączone*) | – |
| Must change password | E, `(pwdLastSet=0)` (chip), not 65536 | – |
| Inactive > N days | E, `(lastLogonTimestamp<=FT(now−N))` | same; exact `lastLogon` on demand (5) |
| Never logged on | E, `(!(lastLogonTimestamp=*))(whenCreated<=GT(now−N))` | `lastLogonTimestamp` missing or 0 |
| Password never expires | E, bit 65536 (chip) | – |
| Password not required | E, bit 32 (`PASSWD_NOTREQD`) | – |
| Reversible encryption | E, bit 128 (`ENCRYPTED_TEXT_PWD_ALLOWED`) | – |
| Password older than N days | E, `(pwdLastSet>=1)(pwdLastSet<=FT(now−N))` | – |
| No e-mail | E, `(!(mail=*))` | and no primary `SMTP:` in `proxyAddresses` (the U1 recipient rule) |
| No manager | E, `(!(manager=*))` | – |
| Manager disabled | E, `(manager=*)` | manager's UAC bit 2 (read as in 2) |
| Disabled, still in groups | D, `(memberOf=*)` | not `isCriticalSystemObject` (krbtgt, Guest); the primary group is not in `memberOf` |
| Disabled, visible in GAL | D, `(showInAddressBook=*)` and – when the schema has it – `(!(msExchHideFromAddressLists=TRUE))` | – |
| Privileged with a problem | OR of the pre-filters of the problem checks | in the privileged set (2) and one of: locked, password expired, account expired, must change, inactive, never logged on, never expires, not required, reversible, old password, disabled still in groups |

Password state order (as the details pane): `DONT_EXPIRE_PASSWORD` → never; `pwdLastSet = 0` → must change;
computed `2^63−1` → never (policy or PSO without maximum age); computed `0` → must change; computed value → that
instant (FGPP included, the DC computes it); computed attribute missing → `pwdLastSet + maxPwdAge` of the domain marked
as an estimate (FGPP not known); nothing known → unknown (no check matches).

Parameters (days, remembered in the settings with the enabled checks and the scope): password expiring 14, account
expiring 30, inactive / never logged on 90 (shared, the `whenCreated` grace of "never" uses it too), password age 365.
The settings JSON uses neutral names (`ExpiryWarningDays`, `RotationDays`) because no persisted name may contain
"password" (settings test).

## 4. UI

*Narzędzia → Przegląd kont…* and the command palette (`tools.accountReview`), one window per application (an open one is
activated). Top: scope (kind + *Wybierz…*), check set (saved sets, *Zapisz zestaw…*, *Usuń*), parameters with a checkbox
per check, *Sprawdź* / *Przerwij*. Middle: tiles grouped by area with the count; a tile selects its list. Bottom: the
list with the columns of the check (name, login, check columns, location), sortable, multi-select; status line with
"stan na godz.", number of accounts read and of findings.

Actions (selection, or the whole list when nothing is selected; all through `IChangeCoordinator` or the batch editor):

| Where | Action |
|---|---|
| every list | *Właściwości* (Enter / double click), *Edycja zbiorcza…*, *Eksportuj CSV…* (file chosen by the operator, regional separator, formula protection, DN column), *Wyślij wiadomość…* (hidden, U1 hook) |
| Locked | *Odblokuj* (`UnlockAccountOperation`) |
| Password expired / expiring, hygiene | *Wymuś zmianę hasła* (`RequirePasswordChangeOperation(true)`) |
| Account expired / expiring | *Przedłuż ważność…* – batch editor with `SetAccountExpirationOperation` (end of the day in 90 days, editable) |
| Inactive / never logged on | *Wyłącz* (`SetAccountEnabledOperation(false)`), *Dokładne ostatnie logowanie (wszystkie DC)* |
| Never expires / not required / reversible | *Wyłącz flagę…* – batch editor with `SetAccountFlagOperation(flag, false)` |
| Disabled, still in groups | *Usuń z grup…* – batch editor preview with `RemoveFromGroupsOperation(union of the groups)` |
| Disabled, visible in GAL | *Ukryj w GAL…* – batch editor with `msExchHideFromAddressLists = TRUE` (only with the Exchange schema) |

Large or risky changes follow the usual threshold (batch preview and typed confirmation). After every change published by
the pipeline (also from the batch editor or other windows) the affected rows of the review are read again and
re-evaluated (debounced), so tiles and lists stay current without a new run.

Saved check sets ("Poranny przegląd helpdesku") hold the name, the enabled checks, the parameters and the scope (kind,
portable DN with `{domain}` or the saved view id) – no directory data.

Account tab of the property sheet: section *Stan konta* – state (locked since …), password (expires on … – in N days /
expired / never expires / must change; "estimate" without the constructed attribute), account (end of … – in N days /
expired / never), last logon (`lastLogonTimestamp`, approximate), *Sprawdź na wszystkich DC* (lock state per DC and the
exact last logon with its DC) and the hidden *Wyślij wiadomość…*.

## 5. Exact values from all DCs

`DomainControllerCatalog.ReadAsync` lists the DCs of the domain (configuration partition, RODCs included – they keep
their own `lastLogon`). The current DC is read through the window's session; every other DC through its own session from
`ConnectToServerAsync(current, host)` (same credential in memory, nothing typed again), at most 4 in parallel, each
closed right after its read. `lastLogon` is not replicated, so the exact value is the maximum over the DCs that
answered (and `lastLogonTimestamp`). A DC that fails is reported; then the maximum is a lower bound: rows whose exact value
is newer than the threshold are removed from *Inactive* / *Never logged on* (they are certainly active), the others stay.
Without a connector (or with a single DC) only the current DC is read.

## 6. Edge cases

* Session ended or DC changed: the window keeps the results (read-only) and refuses actions and runs (WindowViewModelBase).
* Run again / scope change: the previous run is cancelled; generation counter drops late batches.
* `memberOf` with more than 1500 values: only the presence matters; the count is shown as "1500+".
* Times: FILETIME/GeneralizedTime in UTC, days and dates in the operator's local time zone (`TimeProvider.LocalTimeZone`).
* Unknown attributes (no Exchange schema): never used in a filter (AD evaluates an unknown attribute as Undefined).
* Samba: returns both constructed attributes; FGPP through `msDS-ResultantPSO`/PSO objects as AD.

## 7. Tests

* Core: `AccountStatusTests` – fake clock, Europe/Warsaw: password expiry from the computed value (FGPP shorter than the
  domain), never expires (flag, computed `2^63−1`), must change, expired bit, estimate fallback, account "end of day"
  across DST, never expiring, last logon/never, password age, lock with and without the computed bit.
* Presentation: `AccountReviewQueryTests` (filters, escaping, scope filters, schema guards) and
  `AccountReviewViewModelTests` over `FakeDirectorySession` + `AddSampleCompany` (Polish names): tiles and lists per
  check, FGPP, scopes (OU, saved view, nested group), streaming and stop, unlock through the change plan and row refresh,
  batch editor hand-over, CSV export, saved check sets and remembered parameters, exact last logon from several DCs,
  session ended, U1 hook hidden; Account tab summary.
* App: UI test (Tools menu → window → run → tile → list), smoke test entry, screenshot `17-account-review.png`.
* Lab (Samba): own OU with users in every state (locked, expired, expiring, never logged on, FGPP via a PSO), the review
  against the real DC, clean-up.
