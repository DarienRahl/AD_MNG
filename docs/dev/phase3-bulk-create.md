# Phase 3 – bulk creation of accounts (design)

Product description (Polish): README, *Faza 3 – Edycja zbiorcza v2 i zakładanie kont* (the bullets *Import CSV
**tworzący** obiekty* and *polityka nazewnictwa … licznikami unikalności*); the Polish user document
`docs/ZAKLADANIE-KONT.md` is written in the integration step (2.13). This note is the implementation design of the first
part of phase 3 – the wizard **Zakładanie wielu kont** – and is **binding** for its implementers: the public types of
2.12 are the contract, each heading of 2.1–2.7 names its owner (Core agents **B1**, **B2** in wave 1, Presentation/App
agents **B3**, **B4** in wave 2), and 2.13 fixes which files each agent may touch. The rest of phase 3 (batch editing v2)
gets its own top-level section later; do not renumber 2.x after the F3 contract commit (XML docs and
`BulkCreateIssueCodes` quote these numbers). Track P1 (AD CS, docs/dev/p-certificate-services.md 2.12) is being
implemented in parallel: F3 touches no PKI file and changes shared files only by the small anchored insertions of 2.13.
The draft was reviewed on 2026-11-09 (2.14); this text is the revised version.

Operator request, 2026-11-09 (verbatim): *„Jeszcze chcę abyś koniecznie teraz dodał dodawanie kont masowe nawet z wiedzie
z Excela jak przekleję maisce ma telefon imię nazwisko i inne adres mailowy np. a mieć konfiguracje w stylu
kd202@costoco.com Kd201.. Itp.. Tes ewneulanie inne dane ale to w kreatorze do wyboru grupowego tworzenia kont do
kontentegi folderu ma to być całkiem uniwersalne”* – create many accounts now, from data pasted from Excel (mobile phone,
first name, last name, other data, e-mail address), with configurable naming such as `kd201@contoso.com`, `kd202…`
(prefix + counter), other data selectable in a wizard, into one chosen container, fully universal.

## 1. Decisions (operator, 2026-11)

1. **Wizard and scope.** *Zakładanie wielu kont* (the title follows the kind: *… kontaktów / grup / komputerów*): a
   palette command, *Akcja → Nowy → Wiele kont…*, the *Nowy* submenu of the OU/container context menu of the tree and of
   the object list, and a *Wiele naraz…* button in the single-object *Nowy …* dialogs (user, contact, group, computer).
   The decision named the palette id `users.bulkCreate`; it is **`new.bulkCreate`** (2.7) because the command sits in the
   existing `new.*` group and also creates contacts, groups and computers. It creates objects into **one** chosen
   container (OU or container; the selected tree node is pre-filled, a picker changes it). Object kinds: **user**
   first-class; **contact, group and computer** through the same engine with kind-specific columns and defaults
   (*całkiem uniwersalne*).
2. **Data sources.** Paste from Excel or any spreadsheet: clipboard Unicode text (TSV, Excel's own quoting rule) first,
   the HTML table format only when it is needed; with or without a header row (detected, also below a title row; the
   operator can override); Excel quirks handled – quoted cells with tabs and line breaks, trailing empty columns and
   rows, BOM, non-breaking and zero-width spaces, error values (`#N/D!`), phone numbers such as `+48 600 100 200` kept as
   text, numbers Excel shows as `4,86E+11` warned (and recovered from the HTML format when it carries the full value). A
   CSV file (`CsvFiles` decoding incl. cp1250 and UTF-16), an `.xlsx` file (the U5 reader), typing and editing directly in
   the grid, and appending more pastes (a repeated paste of the same rows is caught). Columns are auto-mapped by Polish
   and English header synonyms and by content (2.1), and manually to **any** writable attribute of the object class from
   the live schema (incl. `extensionAttribute1–15` and custom attributes), to *Tylko we wzorcach*, or ignored.
3. **Naming rules** are patterns with tokens (2.3), configured in the wizard and saved as named **profiles** (settings:
   patterns and options only, never directory data; optionally the default profile of a target container). Rules exist
   for `sAMAccountName`, `userPrincipalName` (prefix pattern + a suffix chosen from the forest's UPN suffixes and domain
   names), `mail`, the primary SMTP address in `proxyAddresses` (optional), `cn`/RDN, `displayName`, `givenName`/`sn`
   from columns, `description`, and any attribute with a fixed value or pattern. Tokens: column values, functions (first
   N letters, lower/upper, transliteration of Polish letters through `Diacritics`, removal of spaces and disallowed
   characters, truncation – `sAMAccountName` 20, `cn` 64) and the **counter** `{$nr:start[:width[:step]]}` –
   `kd{$nr:201}` gives kd201, kd202 …; the counter takes the next free number: existing objects are checked in AD
   (through the window's session, rule 4) and numbers reserved in the same batch are skipped. Collision strategy for
   name-based patterns – append a number (`jan.kowalski2`) or mark the row as an error – chosen in the profile.
   Uniqueness: `sAMAccountName` in the domain (incl. the implicit UPN `sam@domain`); `userPrincipalName`, `mail` and
   `proxyAddresses` forest-wide through the global catalog when the session's DC is a ready GC, otherwise marked
   *sprawdzone tylko w domenie*; the object name (`name`, the RDN value) within the container.
4. **Account options.** Password per account generated (`PasswordGenerator`, length from the profile, never below the
   domain minimum) or one password typed for all; *Użytkownik musi zmienić hasło przy następnym logowaniu* on by default;
   account enabled (default; needs a password the domain accepts) or disabled; expiration date (for all, or per row from
   a column); groups to add (picker, plus an optional *Grupy* column); manager from a column matched to an existing
   account (by `sAMAccountName`, UPN, `mail`, `displayName` – ambiguous → row warning); fixed attribute values. No
   file-system work: home folders only as the `homeDirectory` / `homeDrive` attributes.
5. **Check grid before anything is written** (*Sprawdzenie*): every row with its computed value per attribute, its status
   (OK / ostrzeżenie / błąd) and the reasons (duplicates in the batch or in AD, possible existing accounts of the same
   person, invalid or too long values, missing mandatory attributes, invalid characters, manager not found …); cells are
   editable, rows can be excluded, the grid filters by status. Then the **standard change plan** (rule 5): one create per
   row plus password, enable, manager and group steps, with the usual preview, validation, PowerShell equivalent
   (`New-ADUser … -AccountPassword (Read-Host -AsSecureString …)` – never the password), audit, partial-failure handling
   and undo (undo deletes the objects this wizard created – over all its executions and retries, each checked by
   `objectGUID` – and says so). 5 000 rows stay responsive (rule 6); the existing typed confirmation applies.
6. **Passwords (rule 2)** live only in memory. The results list the created accounts with their initial passwords
   (masked, revealed per row); copy through the secure clipboard copy; export to a file only on explicit command with the
   existing warning (README: *Hasła wygenerowane przy resecie istnieją tylko w pamięci; eksport wyników z hasłami
   wyłącznie na wyraźne polecenie*); never in audit, logs, PowerShell, `ToString` or profiles. Handing passwords to
   managers through U1 messages is a later option (2.10).
7. **Nothing from the directory or the pasted data is saved to disk** (rule 1); profiles keep only patterns and options;
   the last unsent wizard state is **not** persisted.
8. **Tests**: fake directory with Polish data (diacritics), clipboard parsing tests with hand-written synthetic Excel TSV
   and HTML samples, counter and collision tests, a 5 000-row performance test, Presentation wizard tests, App UI tests
   and screenshots, and a Samba lab test (20 accounts with a counter pattern, attributes verified, undo).

**Design choices of this note within 1–8** (reviewed, 2.14): (a) the wizard is a third source mode of the batch editor
(`BatchSourceMode.Create`), so preview, PowerShell, typed confirmation, execution as an activity, results, the
generated-password close guard and `ToolWindows.HasUnsavedGeneratedPasswords` are reused, while results, retry and undo
of create mode accumulate over all executions of the window (2.7); no new window, `IDialogService` method or
`OperationType` is added; (b) the pattern language is the existing template syntax of the batch editor and the user
messages (`{attribute|function}`, `{$token:argument}`) with four creation tokens, not a second syntax, and without Polish
aliases (the UI inserts fields by their Polish labels, 2.6); (c) counters: name-based profiles fill gaps
(*najniższy wolny numer*), the built-in *Konta numerowane* continues after the highest number (*kolejny po najwyższym*)
and never reuses numbers of objects in the AD Recycle Bin (confirmed by the operator, 2.14); (d) a name held only by an object
in the AD Recycle Bin does not block a name-based value but warns (restoring such an object later needs a rename);
(e) the forest-wide check uses a second, read-only session to the **GC port of the same DC** (2.2), opened only for a
multi-domain forest whose DC is a ready GC; (f) the wave-2 split is by feature (B3: steps 1–3, B4: step 4, plan,
results, entry points), each agent owning its view models and views; (g) computers get a random machine password at
creation that nobody sees (2.5 – the K1 *Nowy komputer* dialog keeps its behaviour); (h) execution starts with one
**canary** object, so a systematic failure (no encryption, no reset right, rejected password) stops after one account
(2.7).

## 2. Bulk creation wizard

The wizard is the *Edit* step of the batch editor in create mode, with four sub-steps; the batch editor's own steps
follow unchanged:

```
 Edycja (create mode)                                                  │ batch editor steps (existing)
 1 Dane ──── 2 Kolumny ──── 3 Nazwy i opcje ──── 4 Sprawdzenie ─────────┼─ Podgląd ─── Wykonywanie ─── Wyniki
 paste,      column →       numbering, patterns, computed values per    │  change plan   canary, then   created accounts with
 CSV, xlsx,  attribute /    collisions, UPN,     row, status, reasons,  │  diff, PS,     the rest,      passwords (masked), copy,
 typing      pattern-only / password, enable,    edit cells, exclude,   │  typed confirm activity,      export, retry, undo
             ignore, case   groups, profiles     filter, duplicates     │                cancel         (all executions)
```

Core does everything that decides values: B1 turns text into an `InputTable` and owns the value rules, the PowerShell
equivalent and the GC session; B2 owns the pattern language and `IBulkCreatePlanner` – `ComputeAsync` (patterns,
counters, uniqueness, references, validation; reads only), `BuildPlan` (the `ChangePlan`, passwords generated),
`BuildRetryPlan`, `ReadCreatedAsync` and `PlanUndoAsync` (2.5). The plans then run through the existing pipeline:
`IChangeCoordinator` → `BatchExecutor` (Create = `AddAsync`, `ConfirmCreateAsync` on retry), audit (Create audited per
attribute, the reset without a value), `PowerShellExporter`, `PreviewBuilder` (Create rows show *before* empty) and the
change bus (`DirectoryChangeKind.Created`). The undo of create mode is a plan of Deletes built by `PlanUndoAsync`, not by
`RollbackPlanner` (which sees only the last result). No change to `BatchExecutor`, `RollbackPlanner`, `ChangePlanner`,
`CsvImportPlanner` or `IssueCodes`. Everything lives in namespace `AdMng.Core.Batch.Creation`
(`src/AdMng.Core/Batch/Creation/`) – it must be in Core because it uses `PlanValidation`, `PlanningContext` and
`DelimitedText` (internal; the CSV path only).

### 2.1 Core – table input: clipboard, HTML, CSV and xlsx, header detection, column mapping and header synonyms (B1)

**Model** (`InputTable.cs`, complete in the contract): `TableColumn(int Index, string Letter, string Header)` with
`Name` = the header, or the Excel letter (`A`, `B` … `AA`) when the table has no header; `TableRow(int Number,
IReadOnlyList<string> Cells)` – `Number` is the wizard's row number (1-based, stable: appended rows continue the
numbering, it is the `ObjectPlan.SourceRow` later); `InputTable(Columns, Rows, HasHeader, TableSource Source, Warnings)`
with `Cell(row, column)` ("" past the end), `FindColumn(string nameOrLetter)` (header first – trimmed, case- and
diacritics-insensitive – then letter), `NextRowNumber`, `HeaderLine` (1-based line of the header in the pasted range,
null without one) and `TitleRows` (the lines above the header, kept only for `Reinterpret`; never mapped). `TableWarning(
TableWarningCode Code, int? Row, int? Column, int Count)` carries positions and counts, never a cell value.

**`TableTextReader`** (`TableTextReader.cs`, pure, B1):

* `ReadClipboard(string? text, byte[]? cfHtml, string? cfHtmlText, HeaderMode header)` – the Unicode text is the
  primary source (Excel, LibreOffice and Google Sheets put the displayed values as tab-separated text with CRLF). It is
  split by B1's own **Excel-TSV splitter** (`SpreadsheetText.SplitTsv`, internal, `SpreadsheetText.cs`), not by
  `DelimitedText` (whose "quoted when any later quote exists" rule lets `"Społem" PSS` lose its quotes and an unbalanced
  quote swallow the following rows): a cell is quoted only when it starts with `"` **and** its closing quote (not
  doubled) is followed by a tab, CR, LF or the end of the text – that is how Excel writes cells with a tab or a line
  break; otherwise every quote is a literal character. CRLF, LF-only and CR line endings and a missing final line break
  are accepted. `NeedsHtml(string? text)` (public, used by the App before it asks the clipboard for HTML, 2.6) is true
  when the text is empty or not tabular (no tab in its first 20 lines, e.g. a single column copied from a web page), or
  a fast scan finds a cell in scientific notation; only then is the HTML format parsed. HTML: `CfHtmlParser.Parse` (raw
  bytes) or `CfHtmlParser.FromText`, then `HtmlTableReader` (internal, `HtmlTableReader.cs`, B1 – `HtmlContactReader` of
  U5 is neither reused nor edited: its private `Rows`/`AppendText` append the `mailto:` address to linked text):
  `HtmlNesting.ParseDocument`, the `<table>` with the most `tr` rows, own `td`/`th` cells only (nested tables skipped),
  `<br>` → CRLF, text only (no link addresses), `colspan` → empty cells + warning `MergedCells`, `rowspan` → warning
  `MergedCells`.
* **Scientific notation.** A cell matching `^[+-]?\d([.,]\d+)?E[+-]?\d+$` (case-insensitive) – Excel's display of a
  12+ digit number in a *General* column – gets warning `ScientificNotation` (row, column). When the HTML format is
  present, has the same shape (rows × columns after trimming) and the same cell carries Excel's `x:num="486001002001"`
  attribute with an integer, the cell takes that value and the warning becomes `RecoveredFromHtml` (info). Leading zeros
  Excel dropped from numbers cannot be detected (user doc says so).
* **Normalisation** (`CellText.Normalize`, every cell): BOM (U+FEFF) and zero-width characters (U+200B–U+200D, U+2060)
  removed; NBSP, U+2007, U+202F → space; runs of spaces → one; trimmed; CR, LF and CRLF inside a cell → CRLF (a
  multi-line `streetAddress` is valid, a line break in a single-line attribute is an error of 2.2). Excel **error
  values** (`CellText.IsSpreadsheetError`: `#N/D!`, `#ARG!`, `#NAZWA?`, `#DZIEL/0!`, `#ADR!`, `#LICZBA!`, `#ZERO!`,
  `#PUSTE!`, `#N/A`, `#VALUE!`, `#REF!`, `#NAME?`, `#DIV/0!`, `#NUM!`, `#NULL!`, `#SPILL!`, `#CALC!`) empty the cell with
  warning `FormulaError` (row, column). `CsvCodec.RemoveFormulaProtection` only for `TableSource.CsvFile` (AD_MNG's own
  exports protect `+48…` with an apostrophe; Excel's clipboard text never has one). Trailing columns that are empty in
  every row (and the header) and trailing empty rows are dropped silently; empty rows in the middle are skipped with one
  `EmptyRowsSkipped` warning (count); rows longer than the header extend the table with header-less columns (warning
  `RaggedRows`); duplicate headers keep both columns (the second named `Header (2)`, warning `DuplicateHeader`).
* **Limits**: `MaxRows = 10_000` (more → truncated, warning `TooManyRows`), `MaxColumns = 256`
  (`SpreadsheetLimits.MaxColumns`, warning `TooManyColumns`), `MaxCellLength = 65_536` (cut, warning `CellTooLong`).
* `ReadDelimited(string text, char? delimiter, HeaderMode header, TableSource source = TableSource.CsvFile)` – CSV and
  `.txt`; `null` delimiter = the `CsvCodec.CandidateDelimiters` character most frequent in the first record (outside
  quotes), tab included (Excel's *Tekst Unicode (\*.txt)* is UTF-16 LE TSV); tab-delimited text uses `SplitTsv`, the
  others `DelimitedText.Parse`. Decoding (UTF-16 BOM / UTF-8 BOM / UTF-8 / the ANSI code page, Polish cp1250) stays in
  Presentation (`CsvFiles.ReadTextAsync`).
* `ReadXlsx(byte[] content, HeaderMode header)` – `XlsxReader.ReadSheets` through `SafeZip` as `ContactFileReader` does
  (zip-bomb limits; content ≤ `ContactFileReader.MaxFileBytes`): the first visible non-empty sheet; other non-empty
  sheets → warning `OtherSheetsIgnored`; hidden rows skipped (warning `HiddenRowsIgnored`, count). Numbers come with the
  workbook's number formats, so a phone stored as a number is not shortened.
* `ReadHtml(string html, HeaderMode header)` – the HTML path alone (tests, a pasted `.html` file).
* `Append(InputTable existing, InputTable added)` – both with headers: columns matched by folded header
  (`ColumnSynonyms.Fold`), unknown headers become new columns; otherwise by position (extra columns appended). Row
  numbers continue at `existing.NextRowNumber`, warnings keep their (renumbered) rows. An empty `existing` returns
  `added`. `CountRepeatedRows(InputTable existing, InputTable added)` – the number of added rows whose normalized cells
  equal a row already loaded (2.6 asks before appending a repeated paste).
* `Reinterpret(InputTable table, HeaderMode header, int? headerLine = null)` – toggling *Pierwszy wiersz zawiera
  nagłówki* or choosing the *Wiersz nagłówków* without re-reading: header → data row, data row (or a title row) →
  header; rows renumbered from 1 (the wizard then drops its edits, pins and results of the check step, 2.6).
* **Header detection** (`HeaderMode.Auto`; `FindHeaderRow(IReadOnlyList<IReadOnlyList<string>> firstRows) → int?`, the
  first five non-empty lines): the header is the line with the most known headers (`ColumnSynonyms.Suggest` ≠ null; the
  reader has no schema, so only the synonym table and the LDAP names of its targets count), none of whose cells looks
  like data (`CellText.LooksLikeEmail`, `LooksLikePhone` – 7+ digits –, an ISO or Polish date, a number); without such a
  line, the first line is a header only when its cells are distinct, non-empty, contain no digit and at least one column
  of the next five lines holds an e-mail address or a phone number. Lines above the found header (a title such as
  *Nowi pracownicy – listopad 2026* and blank lines) become `TitleRows` with warning `TitleRowsSkipped` (count). A
  single line is data unless every cell is a known header. `HeaderMode.FirstRowIsHeader` / `NoHeader` force the choice.

**`CellText`** (B1, pure): `Normalize`, `IsSpreadsheetError`, `IsScientificNotation`, `LooksLikeEmail`,
`LooksLikePhone`, `LooksLikePolishMobile` (9 digits, or `48` / `+48` / `0048` + 9 digits, first two digits 45, 50, 51,
53, 57, 60, 66, 69, 72, 73, 78, 79 or 88), `IsAllCaps` (letters only upper case, 2+ letters), `ApplyCase(string,
TextCase)` (*Jak W Nazwie*: each word and each part of a hyphenated name capitalized with `pl-PL` casing –
*NOWAK-KOWALSKA* → *Nowak-Kowalska*), `SplitValues(string)` (values of a multi-valued attribute: `|` only, as the CSV
import; a line break stays inside the value), `SplitList(string)` (the *Grupy* column: `;`, `|` and line breaks),
`SplitCommas(string)` (the fallback of 2.5 for group cells written with commas), `SplitFullName(string, NameOrder)` →
`(GivenName, Surname)`: titles removed with `TitleParser.WithoutTitles` (*dr hab. Anna Maria Nowak-Kowalska*), then
given-name-first: the last token is the surname, the rest the given names; surname-first: the first token is the
surname; one token → surname only; and `NormalizePhone(string, PhoneFormat)`: `International` strips spaces, dashes,
dots and parentheses, `00` → `+`, a 9-digit number → `+48`, 11 digits starting `48` → `+48`, then formats
`+48 600 100 200` (other countries: `+` and the digits ungrouped); anything with letters or fewer than 7 digits is
returned unchanged (the planner warns, 2.2).

**Column mapping** (`ColumnMapping.cs`, complete in the contract): `ColumnMapping(int Column, ColumnTargetKind Kind,
string? Attribute = null, NameOrder Order = NameOrder.GivenNameFirst, TextCase Case = TextCase.AsIs)`;
`ColumnTargetKind` = `Ignore`, `Attribute` (any writable attribute of the class, 2.2), `PatternOnly` (only through
`{$column:…}`), `FullName` (split into `givenName` / `sn` with `Order`), `Manager` (`manager` of users and contacts,
`managedBy` of groups and computers, 2.5), `Groups` (per-row group membership, 2.5), `Country` (`c`, `co` and
`countryCode` together from a name or code, 2.5) and `ExpiresAt` (per-row `accountExpires` of users, 2.5).
`TextCase` (`AsIs`, `Title`, `Lower`, `Upper`) is the operator's explicit per-column choice, applied after
`Normalize` (like `PhoneFormat`, it is the only way a column value is changed). Two columns may not map the same attribute
(the second becomes `Ignore`, 2.6 says so); `FullName` provides `givenName` / `sn` only where those have no own mapped,
non-empty cell.

**`ColumnSynonyms`** (B1, pure): `Fold(header)` = lower-case invariant, `Diacritics.Remove`, every character that is
not a letter or digit removed (*Tel. kom.* → `telkom`, *Adres e-mail* → `adresemail`); `Suggest(string header,
ObjectKind kind, IReadOnlyCollection<string>? attributeNames = null, IReadOnlyList<string>? samples = null)` matches the
folded header **exactly** against the table below (no substring matching: *komórka organizacyjna* must not become
*komórka*), then against the LDAP display names of the attribute catalogue the caller passes (2.6 adds the localized
display names), then refines by **content** (`samples` = the first 20 non-empty cells): a header of the *telefon*
family (`telefon`, `tel`, `nrtel`, `nrtelefonu`, `numertelefonu`) whose samples are 80 % or more Polish mobile numbers
→ `mobile` with `SuggestionNote.MobileNumbersDetected`; `komorka` → `mobile` only when 80 % of the samples are phone
numbers, otherwise `department`; phone samples written as `48xxxxxxxxx` add `SuggestionNote.PhonesWithoutPlus` (2.6
offers *Format międzynarodowy*). `SuggestFromContent(IReadOnlyList<string> samples, ObjectKind kind)` is used for a
table without a header: 80 % e-mail addresses → `mail`, Polish mobile numbers → `mobile`, other phones →
`telephoneNumber`, else null (`SuggestionNote.ContentBased`). `IsPasswordLike(header)` and `IsPersonalIdentifier(header)`
(below). Kind-dependent entries are marked.

| Target | Polish headers (folded) | English headers (folded) |
|---|---|---|
| `givenName` | imie, imiona, pierwszeimie | firstname, givenname, forename |
| `sn` | nazwisko | lastname, surname, familyname, sn |
| `FullName` (given first) | imieinazwisko, imienazwisko, pracownik, osoba; *nazwa* (users, contacts) | fullname, employeename; *name* (users, contacts) |
| `FullName` (surname first) | nazwiskoiimie, nazwiskoimie | – |
| `initials` / `middleName` | inicjaly, inicjal / drugieimie | initials, middleinitial / middlename |
| `displayName` | nazwawyswietlana | displayname |
| `cn` | nazwaobiektu, nazwagrupy, nazwakomputera, komputer, nazwahosta; *nazwa* (groups, computers) | cn, commonname, groupname, computername, hostname; *name* (groups, computers) |
| `sAMAccountName` | login, nazwauzytkownika, nazwalogowania, konto, loginwindows | samaccountname, username, userid, logon, logonname, account |
| `userPrincipalName` | upn, loginupn | userprincipalname, upn |
| `mail` | email, mail, adresemail, adresmailowy, poczta, pocztaemail, adrespoczty, emailsluzbowy, mailsluzbowy, adresemailsluzbowy | email, mail, emailaddress, workemail |
| `PatternOnly` (private address) | emailprywatny, mailprywatny, prywatnyemail, adresprywatny, emaildomowy | privateemail, personalemail, homeemail |
| `mobile` | telefonkomorkowy, komorkowy, telkom, telefonkom, telkomorkowy, nrkomorki, numerkomorki, nrtelkom, nrtelefonukomorkowego, numertelefonukomorkowego, komorkasluzbowa, gsm; *komorka* (by content, above) | mobile, mobilephone, cellphone, cell, mobilenumber |
| `telephoneNumber` | telefon, tel, nrtel, nrtelefonu, numertelefonu, telefonstacjonarny, telefonsluzbowy, telsluzbowy, telefonbiurowy, nrwewnetrzny, wewnetrzny (the *telefon* family by content, above) | phone, telephone, telephonenumber, officephone, businessphone, workphone, extension |
| `homePhone` / `ipPhone` / `facsimileTelephoneNumber` | telefondomowy / telefonip / faks, fax, nrfaksu | homephone / ipphone / fax, faxnumber |
| `title` | stanowisko, funkcja | title, jobtitle, position |
| `department` | dzial, departament, wydzial, komorkaorganizacyjna; *komorka* (by content, above) | department, dept |
| `company` | firma, spolka, pracodawca | company, employer, organization, organisation |
| `physicalDeliveryOfficeName` | biuro, pokoj, siedziba, miejscepracy; *lokalizacja* (users, contacts) | office, room; *location* (users, contacts) |
| `location` (computers) | *lokalizacja* | *location* |
| `employeeID` / `employeeNumber` | idpracownika, identyfikatorpracownika / numerpracownika, nrpracownika, nrewidencyjny, numerewidencyjny, nrkadrowy | employeeid / employeenumber, personnelnumber, staffnumber |
| `employeeType` | typpracownika, rodzajzatrudnienia | employeetype |
| `Manager` | menedzer, przelozony, bezposredniprzelozony, kierownik, szef | manager, supervisor, reportsto |
| `ExpiresAt` (users) | umowado, datakoncaumowy, datazakonczenia, kontowygasa, wygasa, datawygasniecia | enddate, contractend, expires, expirationdate |
| `Country` | kraj, panstwo, krajregion | country, countryregion |
| `description` / `info` | opis / uwagi, notatki, notatka | description / notes, comment, comments, info |
| `streetAddress` / `l` / `postalCode` / `st` | ulica, adres / miasto, miejscowosc / kodpocztowy, kod / wojewodztwo, region | street, streetaddress, address / city, town, locality / postalcode, zip, zipcode, postcode / state, province |
| `wWWHomePage` | stronawww, www, stronainternetowa | homepage, website, webpage, web |
| `homeDirectory` / `homeDrive` / `scriptPath` / `profilePath` | folderdomowy, katalogdomowy / dyskdomowy / skryptlogowania / sciezkaprofilu | homedirectory, homefolder / homedrive / scriptpath, logonscript / profilepath |
| `Groups` | grupy, grupa, czlonkostwo, grupyad | groups, group, memberof |

**Never mapped automatically:** password-like headers – `IsPasswordLike` matches **substrings** of the folded header
(`haslo`, `hasla`, `password`, `passwd`, `pwd`, `kennwort`) and the exact headers `pin`, `kodpin`, `pass` (false
positives are harmless): the column is forced to `Ignore`, cannot be mapped (neither to an attribute nor to *Tylko we
wzorcach*), is not reachable through `{$column:…}` (2.3), its cells and samples are masked, its source-grid column is
read-only and copies the mask, and it gets warning `PasswordLikeColumn` (passwords from a sheet are not in this stage,
2.10); and personal identifiers (`IsPersonalIdentifier`: pesel, nip, dowod, nrdowodu, dataurodzenia, birthdate, ssn,
iban) – `Ignore` unless the operator maps them explicitly.

### 2.2 Core – value rules, identifier names, the PowerShell equivalent and the global-catalog session (B1)

**`CreationNames`** (`CreationNames.cs`; constants, `ClassChainOf`, `StructuralClassOf`, `MaxSamAccountName` and
`IsInvalidSamCharacter` complete in the contract – copies of `AttributeOption.ClassesOf` and `AccountNames`, which live
in Presentation, pinned equal by the Presentation golden test `CreationNamesParityTests` of the contract; the rest B1):
`MaxUserSamAccountName = 20`, `MaxComputerName = 15` (sAMAccountName `NAME$` ≤ 16), `MaxGroupSamAccountName = 256`,
`MaxCn = 64`, `MaxUpn = 1024`, `MaxMailLocalPart = 64`; `IsValidSamAccountName(value, kind)`, `IsValidUpnPrefix(value)`
(rules of `AccountNames.IsValidUpnPrefix`), `IsValidComputerName(value)` (1–15 characters, letters, digits and `-`, not
only digits, not starting or ending with `-`, **no `.`** – an error as in the K1 dialog; `_` → warning
`ComputerNameNotDnsSafe`); `SanitizeIdentifier(string value, ObjectKind kind, string attribute)` – the character filter
of the template function `sam` **without its 20-character cut** (diacritics transliterated, white space, non-ASCII and
`" / \ [ ] : ; | = , + * ? < > @` removed, trailing dots trimmed; for a UPN or `mail` value that contains `@`,
`Finish` sanitizes the part before the last `@` and lower-cases the domain); and `AppendDisambiguator(string attribute, string value, int number, int maxLength)`: identifiers
(`sAMAccountName`, the UPN prefix, the local part of `mail`) get the digits appended – before the first `@` when there is
one – after cutting the base so the result fits `maxLength` (trailing dots trimmed again); `cn` and `displayName` of
users, contacts and groups get `" (2)"` the same way (`Jan Kowalski (2)`); computers never (2.4).

**`CreationValues`** (`CreationValues.cs`; the contract has pass-through bodies – `Finish` returns the value unchanged,
`Validate` no issue – so B2 is not blocked; B1 replaces them):

* `Finish(ObjectKind kind, string attribute, string raw, CreationFinishOptions options) → FinishedValue(Value,
  Sanitized, Truncated)` is applied to values that come from a **rule** (2.3). Values from a column or an edited cell
  are only `CellText.Normalize`d – operator data is never silently changed – except the operator's explicit `Case` and
  `PhoneFormat` choices and the computer rules below. Finishing is **character-wise** (`SanitizeIdentifier`); length is
  applied afterwards to the whole finished value: `TruncateNames` cuts `sAMAccountName` (20 / 16 with `$` / 256) and
  `cn` (64) – never a counter value –, while a UPN over 1024 characters or a `mail` local part over 64 is the error
  `TooLong` (an address is never cut).
* `Validate(ObjectKind kind, string attribute, IReadOnlyList<string> values, ISchemaCatalog schema) →
  IReadOnlyList<ValidationIssue>` (codes of `IssueCodes` and `BulkCreateIssueCodes`, the attribute set on each).
* `IsBlockedAttribute(string attribute, ObjectKind kind, ISchemaCatalog schema)` – never a rule target nor a mapped
  attribute: `objectClass`, `objectCategory`, `name`, `distinguishedName`, `unicodePwd`, `userPassword`, `pwdLastSet`,
  `userAccountControl`, `accountExpires` (options or the `ExpiresAt` target set it), `groupType`, `lockoutTime`,
  `msDS-SupportedEncryptionTypes`, `sAMAccountType`, `primaryGroupID`, `objectSid`, `sIDHistory`, `objectGUID`,
  `member`, `memberOf`, `nTSecurityDescriptor`, `instanceType`, `adminCount`, `isCriticalSystemObject`,
  `servicePrincipalName`, `msDS-AllowedToDelegateTo`, `msDS-AllowedToActOnBehalfOfOtherIdentity`,
  `altSecurityIdentities`, `msDS-KeyCredentialLink`; `proxyAddresses` (only through `AddPrimarySmtp`: exactly one
  `SMTP:` value equal to `mail`); `c`, `co`, `countryCode` (only through the `Country` target); the Exchange and Skype
  recipient attributes `mailNickname`, `targetAddress`, `legacyExchangeDN`, `homeMDB`, `msExch*` and `msRTCSIP-*`
  (raw writes create half-provisioned recipients; mailboxes are not in this stage, 2.10); every DN-syntax attribute
  (`manager` / `managedBy` only through the `Manager` target); every `IsReadOnly` (system-only, constructed, back-link),
  binary or `SecretAttributes.IsSecret` attribute; and every attribute not allowed for `ClassChainOf(kind)` by the
  schema (a session without schema: only the attribute names of the table below are accepted). `cn` is allowed: it
  becomes the RDN (never written as an attribute of the Create step). `ColumnSynonyms` never suggests a blocked
  attribute.
* `IsPhoneAttribute(attribute)` – `telephoneNumber`, `mobile`, `homePhone`, `ipPhone`, `pager`,
  `facsimileTelephoneNumber`, `otherTelephone`, `otherMobile`, `otherHomePhone`.
* `IsMultiLine(attribute)` – `streetAddress`, `postalAddress`, `info`; every other attribute is single-line.

| Attribute | `Finish` (rule values) | `Validate` (error unless noted) |
|---|---|---|
| `cn` | white space collapsed, trimmed; `TruncateNames`: cut to 64 (never a counter value) | non-empty, ≤ 64; computers: `IsValidComputerName`, upper-cased |
| `sAMAccountName` | `SanitizeIdentifiers`: users and computers – `SanitizeIdentifier`; groups – only the forbidden characters removed (spaces and letters with diacritics are valid in a group name); computers – upper case, `$` appended **only when missing**; `TruncateNames`: cut to the limit (never a counter value) | non-empty; ≤ 20 (users), ≤ 16 with `$` (computers), ≤ 256 (groups); none of the forbidden or control characters; not only dots and spaces; not ending with `.`; computers: exactly one `$`, the last character (`PC01$$` → `TrailingDollar`); users: no trailing `$` (`TrailingDollar` – a hidden, machine-like account); groups: as Windows allows |
| `userPrincipalName` | prefix `SanitizeIdentifier` (no cut), suffix lower-cased; a rendered prefix that already contains `@` is the whole UPN (2.3) | `prefix@suffix`, `IsValidUpnPrefix`, ≤ 1024; a suffix that is neither a domain DNS name of the forest (every crossRef `dnsRoot`) nor one of `uPNSuffixes` → **warning** `UpnSuffixNotInForest` |
| `mail` | local part `SanitizeIdentifier` (no cut), domain lower-cased | `UserMessageRules.TryParseAddress`, local part ≤ 64, ≤ 256; **warning** `MailDomainNotInForest` when the domain is neither a forest domain nor a UPN suffix (2.4) |
| `proxyAddresses` | `SMTP:` + `mail` (option `AddPrimarySmtp`) | derived – blocked as a target |
| phone attributes | `PhoneFormat.International` (also for column values) | **warning** `ScientificNotation` for `4,86E+11`; **warning** `InvalidPhone` when `International` could not format it |
| `homeDrive` / `homeDirectory` | upper-cased / – | `^[A-Z]:$` / `\\server\share…` or `X:\…`, else **warning** `InvalidValue`; the folder is never created (2.6 says so) |
| `description` | – | **single-valued for every kind** (SAM allows one value on users, groups and computers; ADUC parity): several values → `SingleValued` |
| every single-line attribute | – | no CR / LF (`LineBreakInValue`, e.g. Alt+Enter in an *Imię* cell) and no other control character |
| `givenName`, `sn`, `displayName` from a column | – | **warning** `AllCapsName` when `IsAllCaps` and the column's `Case` is `AsIs` (2.6 offers *Jak W Nazwie*) |
| every other attribute | – | schema `RangeUpper` / `RangeLower` → `TooLong` / `TooShort`; several values for a single-valued attribute → `SingleValued`; syntax per value through `PlanValidation.ValidateValue` (made `internal`-visible by the contract, behaviour-neutral) |

**PowerShell equivalent** (`BulkCreatePowerShell.For(ObjectPlan objectPlan) → PowerShellEquivalent?`; the contract
returns null, so the exporter's generic per-step lines are used until B1 lands). The planner sets `ObjectPlan.PowerShell`
to it (2.5). Users: `New-ADUser -Name <cn> -Path <container> -SamAccountName … -UserPrincipalName … -GivenName …
-Surname … -Initials … -DisplayName … -Description … -EmailAddress … -Title … -Department … -Company … -Office …
-OfficePhone … -MobilePhone … -EmployeeID … -EmployeeNumber … -City … -StreetAddress … -PostalCode … -State … -Country
<c> -HomePage … -HomeDirectory … -HomeDrive … -ScriptPath … -ProfilePath … -AccountExpirationDate … -OtherAttributes
@{…}` (every other Create attribute, `proxyAddresses`, `co` and `countryCode` included; `objectClass` and
`userAccountControl` never), `-AccountPassword (Read-Host -AsSecureString -Prompt 'Initial password for <cn>')` when
the object has a `ResetPassword` step, `-Enabled $true` when its `Modify` step enables the account,
`-ChangePasswordAtLogon $true` when it writes `pwdLastSet = 0`, `-Server $server`; then `Set-ADUser -Identity <dn>
-Manager <dn> -Server $server` for the manager step and one `Add-ADGroupMember -Identity <group dn> -Members <dn> -Server
$server` per `AddMember` step. Groups: `New-ADGroup -Name -SamAccountName -GroupScope Global|DomainLocal|Universal
-GroupCategory Security|Distribution -Path -Description -OtherAttributes`, then `Set-ADGroup -ManagedBy`. Computers
without a join right: `New-ADComputer -Name -SamAccountName -Path -Description -Location -Enabled $true
-OtherAttributes` – without `-AccountPassword`, so the cmdlet sets a random machine password, as the plan does (2.5) –,
then `Set-ADComputer -ManagedBy`. Contacts and computers with a join right (no cmdlet for the ACE) return null →
generic export. Commands use `Module = "ActiveDirectory"`. Two value kinds are added to `PowerShellEquivalent.cs`:
`PowerShellValueKind.SecurePrompt` (`PowerShellParameter.SecurePrompt(name, prompt)` → ` -Name (Read-Host
-AsSecureString -Prompt '<prompt>')`) and `PowerShellValueKind.AttributeTable` (`PowerShellParameter.Table(name,
IReadOnlyList<AttributeChange> attributes)`, written through the exporter's existing `Entry` – hashtable keys quoted when
they contain `-`, secret attributes prompted). `PowerShellParameter.ToString` and `PowerShellCommand.ToString` print the
name / cmdlet and the kind only (they now carry personal data; contract). Every literal goes through
`PowerShellExporter.Quote`; the password never appears (`PowerShellExporterTests`).

**Global-catalog session.** `IDirectoryConnector.ConnectGlobalCatalogAsync(IDirectorySession current, CancellationToken)`
(default member in the contract: `NotSupportedException`, like `ConnectToServerAsync`) opens a new, **read-only** session
to the GC port (3268, or 3269 with `TransportSecurity.Tls`) of `current.Info.Server` with the connection options and the
in-memory credential of `current` (`LdapDirectorySession.SettingsForServer(server)` with `Options = Options with {
GlobalCatalog = true, Port = null }`), and fails like `ConnectAsync` when that DC is not a GC (refused, `ServerUnavailable`).
`LdapDirectoryConnector` implements it (B1). It is attempted only when `current.Info.IsGlobalCatalogReady` is true (a DC
that is still becoming a GC answers on 3268 with an incomplete partial replica); false or unknown → scope `Domain`. It is
the same DC as the window (rule 4, 2.8); it is used only for the uniqueness reads of 2.4 and never for a write (callers
keep it as `IDirectoryReader`, wrapped in `ReadOnlyDirectoryReader`). Forest-wide searches on it use base `""` (the whole
GC; the session sets no domain-scope control because `Info.IsGlobalCatalog` is true); a server that refuses the empty
base gets `RootDomainNamingContext` (subtree) – the lab test of 2.11 pins what Samba does.

**`SessionInfo` additions** (contract, `src/AdMng.Core/Directory/Connection.cs`, set by `LdapDirectoryConnector` in the
contract commit, settable on the fakes): `IsChannelEncrypted` (`pinned.IsEncrypted(connection)` – what
`LdapDirectorySession` already uses to refuse passwords on an unencrypted channel) and `IsGlobalCatalogReady` (`bool?`,
RootDSE `isGlobalCatalogReady`, which the connector now also reads; null when absent).

### 2.3 Core – the pattern language (B2)

Patterns are templates of the existing engine (`ITemplateEngine` documentation; `TemplateEngine` with extension tokens,
`AllowedArguments` and `ValidateArgument`), so the batch editor, the user messages and the wizard share one syntax and
one function set. There are **no Polish aliases** (`{imię}` is not a token): the canonical syntax stays, and the UI
inserts fields by their Polish labels and explains functions in Polish (2.6, 2.9). A pattern is compiled by
`CreationPatternSet.Compile` with an engine instance built per table (`new TemplateEngine(CreationPatternTokens.
Extensions(columns))`: the names of the columns that are not password-like are the allowed arguments of `$column`).
Grammar (EBNF; `ws` = optional white space, ignored around names and functions):

```
pattern   = { literal | token } ;
literal   = any character except "{" and "}" | "{{" (a "{") | "}}" (a "}") ;     (* "\" is literal outside tokens *)
token     = "{" ws head ws { "|" ws function ws } "}" ;                            (* inside a token "\" escapes the next character *)
head      = attribute | "$" special ;
attribute = ascii-letter { ascii-letter | digit | "-" } ;                          (* LDAP display name, case-insensitive *)
special   = "nr" [ ":" counter ]                                                    (* the counter, below *)
          | "column:" text                                                          (* a pasted column by header or Excel letter *)
          | "suffix"                                                                (* the UPN suffix chosen in the wizard *)
          | "mailDomain"                                                            (* the mail domain chosen in the wizard *)
          | "domain" | "parentName" | "parent" | "date" [ ":" format ] ;            (* built-in tokens of the engine *)
counter   = start [ ":" width [ ":" step ] ] ;                                      (* decimal digits only *)
function  = "lower" | "upper" | "title" | "capitalize" | "trim" | "nodiacritics" | "nospaces" | "alnum" | "sam"
          | "first:" n | "last:" n | "replace:" text ":" text | "default:" text ;
```

**Tokens.**
* `{attribute}` – the **computed value of that attribute for this row** (first value): the mapped column, the edited
  cell, or the attribute's own rule – there are no "current values" of an object that does not exist yet. An attribute
  nobody provides renders empty.
* `{$column:Name}` – the cell of a column by header (trimmed, case- and diacritics-insensitive, exact before
  diacritics-folded match) or by Excel letter (`{$column:C}`; letters are always accepted, a header equal to a letter
  wins). Every column that is not password-like is reachable, mapped or `PatternOnly`; a password-like column is not –
  neither by header nor by letter (`UnknownColumn`). The argument is the rest of the head, so colons need no escape
  (`{$column:Godz.: od}`); `|`, `}` and `{` are escaped with `\` (`{$column:Dział\|Zespół}`). An unknown column →
  `PatternError.UnknownColumn` (the engine's `AllowedArguments` error).
* `{$nr}`, `{$nr:start}`, `{$nr:start:width}`, `{$nr:start:width:step}` – the row's counter number (2.4): start
  0–999 999 999 (default 1), width 0–9 = minimum digits, zero-padded (default 0 = no padding; a number with more digits
  is written in full), step 1–1 000 000 (default 1). Every `{$nr…}` of a rule set uses **one** counter: tokens with an
  argument must agree on start and step (width may differ per token), tokens without one take that spec →
  otherwise `CounterMismatch` (the *Numeracja* panel of 2.6 rewrites all of them at once). Functions after `{$nr}` apply
  to the formatted digits.
* `{$suffix}` – the UPN suffix selected for the run; `{$mailDomain}` – the mail domain of the run
  (`BulkCreateRules.MailDomain`, default the UPN suffix); `{$domain}` – the DNS name of the window's domain;
  `{$parentName}` / `{$parent}` – the target container's name / DN (the engine computes them from `DistinguishedName =
  Child("CN", "_")` of the container); `{$date:format}` – the run's local date (`TemplateContext.Now`), formatted with
  the **invariant culture** (`{$date:d MMMM yyyy}` gives *9 November 2026*): the inserter offers only `yyyy-MM-dd` and
  `dd.MM.yyyy`, and a format with `MMM` or `ddd` gives the warning `InvariantDateNames`.
* **Forbidden** in creation patterns → `ForbiddenToken`: `{$dn}`, `{$rdn}` (the DN is the result, not an input) and
  `{$counter}` (the batch editor's position counter – the error text points to `{$nr}`).
* **Functions.** `sam` keeps the batch editor's behaviour: it cuts **each token** to 20 characters
  (`TemplateEngine.ToSamAccountName`) – right for `sAMAccountName`, wrong for addresses; a UPN or `mail` rule that uses
  `sam` gets the warning `SamInAddress`, and the built-ins and examples use `nodiacritics|nospaces|lower` there (the
  finishing of 2.2 removes what is still disallowed).

**Rules and precedence.** A profile has `AttributeRule(Attribute, Pattern, OverridesColumn = false)` entries (one per
attribute – a second → `DuplicateRule`; blocked attributes → `BlockedAttribute`, 2.2). The UPN rule is the **prefix**
pattern; the planner renders `prefix + "@" + suffix`, except when the rendered prefix already contains `@` – then it is
the whole UPN (the *UPN = adres e-mail* preset of 2.6 is the rule `{mail}`). For every row and attribute the value is:
an **edited** cell, else a non-empty **mapped column** cell, else the **rule** (a pattern without tokens is a fixed
value), else nothing; a rule with `OverridesColumn` comes before the column (an edit still wins). So a *Login* column
filled for some rows wins there and the `sAMAccountName` rule fills the rest; an edited `sAMAccountName` re-renders the
UPN and `mail` rules that reference it.

**Evaluation order.** Each rule depends on the attributes it references (`TemplateParseResult.ReferencedAttributes`);
attributes are evaluated in topological order, ties in the fixed order `cn`, `givenName`, `sn`, `initials`,
`displayName`, `sAMAccountName`, `userPrincipalName`, `mail`, then by name; a cycle (`cn` = `{displayName}`,
`displayName` = `{cn}`) → `Cycle` naming the attributes; a reference to a name that is neither an allowed attribute of
the class nor provided → `UnknownAttribute` (warning-level `PatternError`, renders empty) with a **suggestion** when the
name folds (`ColumnSynonyms.Fold`) to a column header or a synonym of the table of 2.1 (`{imie}` → *Czy chodziło o
{givenName} (kolumna „Imię”)?*). An empty token value is reported through `TemplateContext.EmptyValue`: in an
identifier (`cn`, `sAMAccountName`, UPN, `mail`) it becomes the row warning `EmptyToken` (`{0}` the attribute, `{1}` the
token's source text, never a value); an identifier that renders empty is an error. `CounterAttributes` = attributes
whose rule uses `{$nr}` directly or through references.

**Examples** (fake clock 2026-10-13; existing kd203; suffix and mail domain `contoso.com`):

| Rule | Pattern | Row | Result |
|---|---|---|---|
| `sAMAccountName` | `kd{$nr:201}` | rows 1–3, `FillGaps` | kd201, kd202, kd204 |
| UPN prefix (+ suffix) | `{sAMAccountName}` | row 1 | kd201@contoso.com |
| `mail` | `{sAMAccountName}@{$mailDomain}` | row 1 | kd201@contoso.com |
| UPN prefix | `{mail}` (*UPN = adres e-mail*) | mail jan.kowalski@firma.pl | jan.kowalski@firma.pl |
| `sAMAccountName` | `{givenName\|sam\|lower}.{sn\|sam\|lower}` | Łucja Żak | lucja.zak |
| `sAMAccountName` | `{givenName\|first:1\|sam\|lower}{sn\|sam\|lower}` | Anna Konstantynopolitańczykowa | akonstantynopolitanc (cut to 20) |
| `mail` | `{givenName\|nodiacritics\|nospaces\|lower}.{sn\|nodiacritics\|nospaces\|lower}@firma.pl` | Grzegorz Brzęczyszczykiewicz | grzegorz.brzeczyszczykiewicz@firma.pl |
| `mail` | same | Anna Konstantynopolitańczykowa | anna.konstantynopolitanczykowa@firma.pl (not cut) |
| `cn` / `displayName` | `{givenName} {sn}` | Łucja Żak (twice in the batch) | Łucja Żak, Łucja Żak (2) / Łucja Żak, Łucja Żak |
| `sAMAccountName` | `u{$nr:1:4}` | rows 1–2 | u0001, u0002 |
| `employeeNumber` | `{$nr:200:0:10}` | rows 1–3 | 200, 210, 220 |
| `homeDirectory` | `\\fs01\home$\{sAMAccountName}` | kd201 | \\fs01\home$\kd201 |
| `description` | `Konto utworzone {$date:yyyy-MM-dd} – {$column:Umowa}` | Umowa = *zlecenie* | Konto utworzone 2026-10-13 – zlecenie |
| `info` | `{{tymczasowe}} {$column:C}` | C = *praktyki* | {tymczasowe} praktyki |

**API** (`CreationPatterns.cs`, B2): `CreationPatternSet.Compile(IReadOnlyList<AttributeRule> rules, PatternScope
scope)` with `PatternScope(ObjectKind Kind, IReadOnlyList<TableColumn> Columns, IReadOnlyCollection<string>
ProvidedAttributes, ISchemaCatalog Schema)`; members `Errors` (`PatternError(PatternErrorCode Code, string Attribute, int
Position, string? Token = null, string? Suggestion = null)` – built from the code, the 1-based position in the pattern
and the token's source text; the UI text is `BulkCreate.PatternError.<code>` with `{0}` position, `{1}` token, `{2}`
suggestion; the engine's English error strings, which list arguments and every allowed column header, are never kept;
`ToString` prints code, attribute and position), `IsValid` (no error-level code), `Counter` (`CounterSpec?`),
`EvaluationOrder`, `CounterAttributes`, `DependsOn(attribute)`, `Render(attribute, PatternRow row)`, and the static
`SuggestCounter(string pattern) → string?` (a literal ending in digits, `kd201` → `kd{$nr:201}`, the quick fix of 2.6;
padding kept: `kd0201` → `kd{$nr:201:4}`). `CounterSpec(Start, Width, Step)` with `Default`, `Format(int)` and
`TryParse(string? argument, out CounterSpec, out string? error)` (the `ValidateArgument` of `$nr`). `PatternRow`
(required `Attribute` and `Column` callbacks, `Number` – the formatted number or the allocation sentinel of 2.4 –,
`UpnSuffix`, `MailDomain`, `Domain`, `ContainerDn`, `Now`) has a value-free `ToString`. Rendering is pure and
thread-safe.

### 2.4 Core – counters, uniqueness and collisions (B2)

**Scopes.** `sAMAccountName`: the domain (the SAM namespace: users, computers and groups together; no class filter);
the object name: the **RDN value** (`name`) among the container's children, whatever their class or RDN type (AD refuses
`CN=Księgowość` next to an `OU=Księgowość`); `userPrincipalName`, `mail` and `proxyAddresses`: the forest. The planner
reads the forest once (`ForestScope.ReadAsync`: one one-level search of `CN=Partitions,<config>`,
`LdapFilter.And(LdapFilter.Equal("objectClass", "crossRef"), LdapFilter.BitAnd("systemFlags", 2))`, returning
`dnsRoot` and `nCName` – the domain count and the domain DNS names of 2.2's suffix check): one domain → the domain
searches cover the forest (`UniquenessScope.ForestSingleDomain`); several domains and a forest reader (the GC session of
2.2, `BulkCreateLookups.ForestReader`) → those three attributes are searched on the GC (`Forest`); several domains
without one → `Domain`, and the computation carries the run note `CheckedInDomainOnly` (*sprawdzone tylko w domenie*).
Windows Server 2012 R2+ DCs enforce forest-wide UPN uniqueness themselves at write time (through a GC, unless
`dSHeuristics` disables it): a `CheckedInDomainOnly` row whose UPN is taken in another domain fails at its Create
(`constraintViolation`, 0x21C8) rather than creating a duplicate, and the results say so (2.7). Domain searches use
`DefaultNamingContext` (subtree) of the window's session. Every value comparison is case-insensitive
(`StringComparer.OrdinalIgnoreCase` on finished values). **Address uniqueness:** a row's `mail` and its `SMTP:` proxy
address are one address, checked against `mail` **and** `proxyAddresses` of every object
(`LdapFilter.Or(Equal("mail", a), Equal("proxyAddresses", "smtp:" + a))` – the match is case-insensitive) and against the
other rows' addresses. **Implicit UPN:** when a row's UPN suffix is a domain DNS name of the forest, its prefix is also
checked against `sAMAccountName` (in the window's domain; on the GC for another domain – `sAMAccountName` is in the
partial attribute set), and the row's `sAMAccountName` is checked against `userPrincipalName = <sam>@<window domain DNS>`
forest-wide: a clash is `ImplicitUpnClash` (`{0}` the attribute, `{1}` the owner's name) – Samba refuses it, Windows
accepts it but Kerberos then resolves `x@REALM` to the UPN owner and the existing user `x` can no longer log on as
`x@domain`; it collides like any other value (roots and `AppendNumber` below). A likely trigger is a profile with
`sAMAccountName` `kd{$nr}` and UPN prefix `{givenName}.{sn}`. **Mail domain:** a `mail` whose domain is neither a forest
domain nor a UPN suffix gets the warning `MailDomainNotInForest` (typically a private address in the sheet, 2.6), and a
mail domain ending in `.local`, `.lan`, `.internal`, `.localdomain` or `.corp` gets `NonRoutableMailDomain`.

**Deleted objects.** When `Info.RecycleBinEnabled`: the domain searches of `sAMAccountName` set `IncludeDeleted` (Show
Deleted – on Windows it returns no recycled objects, they need Show Recycled) and read `isDeleted` and `isRecycled`;
recycled hits are ignored (they can never be restored). For the object name, deleted objects are searched where they
are – one one-level search of the domain's Deleted Objects container (`<WKGUID=18E2EA80684F11D2B9AA00C04F79F805,<domain
NC>>`, `IncludeDeleted`) with `LdapFilter.And(LdapFilter.Equal("lastKnownParent", container), LdapFilter.Or(Equal(
"msDS-LastKnownRDN", v) …))` in batches of 50 (their own RDN is mangled, `name\0ADEL:guid`). A name-based value held
only by a deleted object is **not** taken (AD allows it) but gives the row warning `NameInRecycleBin` (*obiekt o tej
nazwie jest w Koszu AD – jego przywrócenie będzie wymagało zmiany nazwy*). For **counters**,
`BulkCreateRules.ReuseRecycleBinNumbers` (default false; the built-in *Konta numerowane* keeps it false) decides: false →
a number held by a deleted object counts as taken (a departed employee's number and address are not issued again);
true → free with the warning. Values in `BulkCreateInput.ReleasedValues` – the identifiers this window created and its
own undo deleted (2.7) – are never taken because of their deleted copy, so an undo followed by a re-run keeps its pins.

**Counter allocation.** Rows that need a number = included, not yet created rows where at least one
`CounterAttribute` comes from its rule (not a column or an edit). Per row the counter attributes are first rendered
with `Number` = the sentinel U+E000, the result is split at the sentinel into prefix and suffix, and both are finished
separately (2.2 – `SanitizeIdentifier` is character-wise and never cuts; length is checked afterwards on the whole
value). The **primary** counter attribute is the first of `sAMAccountName`, `cn`, `userPrincipalName`, `mail`, then by
name, among the attributes whose own pattern contains `{$nr}`; rows with the same finished (prefix, suffix) of the
primary attribute form a **sequence** (a constant prefix such as `kd` gives one sequence;
`{givenName|first:1|lower}{sn|first:1|lower}{$nr:1:3}` gives one per initials – `jk001`, `jk002`, `an001`).

1. **Taken values, one query per prefix.** For each (attribute, prefix, suffix, scope) with a finished prefix of at
   least 2 characters: one paged search (`PageSize` 1000) in the attribute's scope (domain subtree, the container one
   level for the name, the GC for the forest attributes) `LdapFilter.And(LdapFilter.StartsWith(attribute, prefix),
   suffix.Length > 0 ? LdapFilter.EndsWith(attribute, suffix) : null)` – for `proxyAddresses` the prefix is `smtp:` +
   the mail prefix; for the name the attribute is `name` – returning only that attribute (and `isDeleted`,
   `isRecycled`); plus, for the name, the same prefix on `msDS-LastKnownRDN` in Deleted Objects (above). In memory,
   every value that starts with the prefix, ends with the suffix and has only ASCII digits between them adds that
   **digit string** to the taken set (`kd0201` and `kd201` are different values; `AfterHighest` uses the largest parsed
   number). Prefixes shorter than 2 characters, and rows where the sentinel was removed or duplicated by a function
   (`sam` strips it), use **windowed equality lookups** instead: the candidate values of the next 50 grid numbers in one
   `LdapFilter.Or` of `Equal`, extended window by window. No filter is built from text (rule 3): prefixes and values go
   through `LdapFilter`.
2. **Assignment, deterministic in row order.** A sequence has a cursor: `FillGaps` starts at `Start`, `AfterHighest` at
   the first grid number above the highest **taken** number of the primary attribute (live, plus the Recycle Bin unless
   `ReuseRecycleBinNumbers`). For each row in row order: its **pinned** number (`BulkCreateInput.PinnedNumbers`, the
   number of the previous computation) is kept when it is still on the grid and free; otherwise the row takes the
   smallest grid number ≥ the cursor that is free for **every** counter attribute of the row (not taken in its scope,
   not reserved by an earlier row – edited and column values of all rows are reserved first); the cursor moves to that
   number + step (after a pinned row: to the larger of the cursor and the pin + step). Numbers therefore grow in row
   order, an edit or an exclusion never renumbers other rows (a released number is not given to pinned rows), and
   *Numeruj od nowa* (2.7) clears the pins. A counter value is **never truncated or disambiguated**: a value over the
   attribute's limit is the row error `CounterTooLong` (`{0}` the attribute, `{1}` the limit – *prefiks i numer nie
   mieszczą się w 20 znakach*); more than 100 000 candidates, or a number above 999 999 999 → `CounterExhausted`.
3. **Preview** (`IBulkCreatePlanner.PreviewCounterAsync`, for the live sample of 2.6): the query of step 1 for the
   rules' constant prefix (cached in the lookups) → `CounterPreview(Prefix, FirstFree, Highest)`; null values when the
   prefix is not constant.

**Name-based values** (every unique attribute whose value does not contain the counter): all finished values of all
rows are looked up once per attribute in batches of `PlanValidation.LookupBatchSize` (50) values per `LdapFilter.Or`;
the name instead with **one** paged one-level search of the container (`(objectClass=*)`, attribute `name`) plus the
Deleted Objects lookup above. Then, in row order, a value that is taken in AD or by an earlier row **collides**:

* `CollisionStrategy.MarkError`, every **computer** (a `cn` *PC01 (2)* is not a computer name, and `cn` and
  `sAMAccountName` must stay equal), a value from a column or an edited cell, and a counter value → error
  `DuplicateInDirectory` (`{0}` the attribute, `{1}` the owner's name) or `DuplicateRow` (`{0}` the attribute, `{1}` the
  other row's number); two rows with the same column or edited value are **both** errors.
* `AppendNumber` (default for users, contacts and groups): the disambiguator goes to the row's **roots** – unique
  attributes (`sAMAccountName`, UPN, `mail`, `cn`) whose value comes from a rule that references no other unique
  attribute. A colliding attribute that is a root is bumped itself; one that is derived (UPN or `mail` =
  `{sAMAccountName}…`) bumps the roots it depends on; when it has no root (its value comes from a column, an edit or a
  non-unique attribute only) it is an error as above. Bumped roots get `CreationNames.AppendDisambiguator` with 2, 3 …
  and the row's dependent rules are rendered again, until nothing collides or 99 is reached (error
  `DuplicateInDirectory`). The disambiguated candidates (2–6) of every collided value are fetched in one more batched
  pass; rarer deeper collisions add windows. Examples: AD holds `jan.kowalski` → the row gets `jan.kowalski2`,
  `jan.kowalski2@contoso.com` (UPN and `mail` follow); only an external contact holds `jan.kowalski@contoso.com` →
  `mail` collides, its root is `sAMAccountName`, so login and address stay equal (`jan.kowalski2`); the container holds
  *Jan Kowalski* → `cn` *Jan Kowalski (2)* (a `displayName` rule `{givenName} {sn}` is not unique and stays *Jan
  Kowalski*). Bumped values are marked `BulkValue.Adjusted` (row warning `Disambiguated`, so the operator sees it).

**Frozen values** (`BulkCreateInput.FrozenValues`, set by `PlanAsync`, 2.7): the finished values of the check
computation per row and attribute. With them the planner renders nothing, assigns no number and disambiguates nothing:
it takes those values as they are, repeats every uniqueness lookup against fresh caches, and turns any collision into
`DuplicateInDirectory` / `DuplicateRow` errors – so the plan holds exactly what the check grid showed, or the operator
sees why not.

**Possible existing accounts** (users; `BulkAccountOptions.MatchAttributes`, null = the default order): counter values
never collide, so a repeated sheet would create kd221… for people who already got kd201…. The planner therefore looks up
the row's person in the domain with the first mapped, non-empty attribute of `employeeID`, `employeeNumber`, `mail` (from
a column), `mobile`, else `givenName` + `sn` together (`LdapFilter.And` of both `Equal`s), batched like the name-based
lookups, limited to users and contacts: a match gives the warning `PossibleExistingAccount` (`{0}` the attribute, `{1}`
the owner's name; `BulkCreateRow.MatchDns`), and two rows with the same value give `DuplicateSourceRow` (`{0}` the
attribute, `{1}` the other row). An empty `MatchAttributes` switches the check off.

**Replication.** Every check is only as current as the window's DC: an object created on another DC and not yet
replicated can still produce a duplicate `sAMAccountName`, which AD does not resolve (DN conflicts are CNF-mangled,
duplicate SAM names are not). The results note says so (2.7).

**Query budget** (asserted with `FakeDirectorySession.Operations`, 2.11): 5 000 rows with constant-prefix counter
patterns for `sAMAccountName`, UPN and `mail` need one search per (attribute, prefix) plus the run reads (container,
permissions, policy, forest, option groups, RID pool, references) – at most 16; name-based patterns at most
`5 × ⌈rows / 50⌉ + 2` lookups plus the collision passes, plus `⌈rows / 50⌉` for the existing-account check. Lookups
run with at most `BulkCreateInput.MaxParallelLookups` (the batch parallelism setting) in flight, are cancellable and
report progress. `BulkCreateLookups` caches every result for the window (2.5), so a recompute after an edit only reads
values not seen before; `Refresh()` forgets them (*Sprawdź ponownie*, and always before the plan is built).

### 2.5 Core – the planner: rows, references, permissions and the change plan (B2)

**`IBulkCreatePlanner`** (`IBulkCreatePlanner.cs`, contract; `BulkCreatePlanner(PasswordGenerator passwords,
TimeProvider time)`, B2, may be split into partials `BulkCreatePlanner.*.cs`):

```csharp
Task<BulkCreateComputation> ComputeAsync(IDirectorySession session, BulkCreateInput input, BulkCreateLookups lookups,
    IProgress<int>? progress, CancellationToken cancellationToken);
Task<CounterPreview> PreviewCounterAsync(IDirectorySession session, BulkCreateInput input, BulkCreateLookups lookups,
    CancellationToken cancellationToken);
ChangePlan BuildPlan(BulkCreateComputation computation, string? sharedPassword, string title);
ChangePlan BuildRetryPlan(BulkCreateComputation computation, BatchResult accumulated, string? newSharedPassword, string title);
Task<IReadOnlyList<CreatedObject>> ReadCreatedAsync(IDirectorySession session, BatchResult result,
    CancellationToken cancellationToken);
Task<ChangePlan> PlanUndoAsync(IDirectorySession session, IReadOnlyList<CreatedObject> created, string title,
    CancellationToken cancellationToken);
```

and the pure helper `BulkCreateResults.Merge(BatchResult? accumulated, BatchResult next) → BatchResult`
(`BulkCreateResults.cs`, B2): objects keyed by `ObjectPlan.SourceRow`, the latest `ObjectResult` per row with the
`ChangeResult`s of all executions concatenated; status `Succeeded` when the latest execution succeeded, else
`PartiallySucceeded` when any step of any execution succeeded, else the latest status; plan = the first plan's header
(title, server, `CreatedAt`).

**`ComputeAsync`** reads only, all on `session` (and on `lookups.ForestReader` for the forest scope), and never throws
for a row – every finding is an issue:

1. **Run checks** (`BulkCreateComputation.Issues`): the container exists (`GetAsync(container, ["objectClass"])`, else
   `TargetNotFound`); `GetEffectivePermissionsAsync(container).CanCreate(StructuralClassOf(kind))` (known and false →
   `CannotCreateChild`, every row blocked; unknown → allowed, as the shell does); the patterns compile (each
   `PatternError` → `TemplateError` with the attribute; an error-level one blocks every row); the options are consistent
   (`Enabled` with `InitialPasswordMode.None` for users → `EnabledWithoutPassword`); **passwords need an encrypted
   channel**: a user batch with passwords or any computer batch on a session with `Info.IsChannelEncrypted == false` →
   run error `PasswordsNeedEncryptedChannel` (otherwise every row would be created and then fail its reset with
   `StrongAuthRequired`); schema not loaded → warning `SchemaUnavailable` (only the attributes of 2.2's table are
   accepted). Read once per `lookups`: the forest (2.4); the domain's `minPwdLength` and `pwdProperties`
   (`DomainPasswordPolicy`; absent → null); the **option groups** – `objectClass`, `groupType`, `objectSid`, `member`
   write permission and `msDS-PSOApplied` (a group that does not exist → `GroupNotFound` warning, the group is dropped);
   the **RID pool** for batches of security principals (users, groups, computers) of more than 100 rows, best effort:
   the DC's `rIDSetReferences` → `rIDAllocationPool` / `rIDNextRID`, and `fSMORoleOwner` of `CN=RID Manager$,CN=System`
   – fewer remaining RIDs than rows while another DC holds the RID master role → warning `RidPoolLow` (*kontroler
   poprosi o nową pulę; jeśli wzorzec RID jest niedostępny, tworzenie zatrzyma się po wyczerpaniu puli*); unreadable →
   nothing; and whether the window's DC is the **PDC emulator** (`fSMORoleOwner` of the domain object;
   `BulkCreateComputation.IsPdcEmulator`, shown in the results note). **Password policy**: `Generate` with
   `GeneratedLength` below `minPwdLength` → run error `PasswordTooShortForDomain` (`{0}` the minimum; 2.6 shows the
   minimum next to the length field); `SameForAll` – the shared password is checked when the plan is built (it is not
   in the input): shorter than `minPwdLength` → run error `PasswordTooShortForDomain`, and with `ComplexityRequired` fewer
   than three of the categories upper-case, lower-case, digit, non-alphanumeric, other Unicode letter → run error
   `PasswordNotComplex` (`BuildPlan` puts both as errors on every object, so nothing is executable and the preview
   shows why); an option group with
   `msDS-PSOApplied` → warning `GroupPasswordPolicy` (*hasło początkowe sprawdza polityka domeny; zasady haseł grupy
   „{0}” obowiązują dopiero po dodaniu do grupy – przy zmianie hasła przy logowaniu*), using the PSO's
   `msDS-MinimumPasswordLength` / `msDS-PasswordComplexityEnabled` for the shared-password check when readable (PSOs are
   readable by Domain Admins only by default).
2. **Rows** (included, not created; excluded rows → `Excluded`, created rows → `Created`, both without lookups): base
   values from the mappings (`FullName` split, `Case` applied, multi-valued attributes split with `CellText.SplitValues`,
   phones formatted when chosen, `Country` resolved, `ExpiresAt` parsed), then the edits, then the rules in evaluation
   order; counters (2.4); `Finish` on rule values; name-based uniqueness and disambiguation (2.4); the existing-account
   check (2.4); `Validate` on every value; mandatory attributes of `schema.GetMandatoryAttributes(ClassChainOf(kind))`
   minus `cn`, `name`, `objectSid`, `objectCategory`, `nTSecurityDescriptor`, `instanceType` and the RDN type (as
   `NewObjectViewModel.ValidateMandatory`), plus F3's own: `cn` for every kind, `sAMAccountName` for users, groups and
   computers → `MandatoryAttribute`; the DN `DistinguishedName.Parse(container).Child("CN", cn)` (rule 3; `cn` invalid →
   no DN, error). **Country** (`Country` target): the folded cell is looked up in `BulkCreateInput.Countries` (built by
   2.6 from the existing `Countries` table: alpha-2 code, numeric code and the names of both languages) → `c` (alpha-2),
   `co` (the name in the UI language, as ADUC writes it) and `countryCode` together; not found → warning
   `CountryNotFound`, nothing written. **Expiry** (`ExpiresAt` target, users): `dd.MM.yyyy`, `d.M.yyyy`, `yyyy-MM-dd` or
   an xlsx serial date → the end of that local day (as the batch editor's expiration editor), overriding the run option;
   anything else → error `InvalidDate`.
3. **Manager** (`Manager` target): per distinct cell, a DN (`CN=…`) is checked with `GetAsync`; a value with `@` is
   looked up as `LdapFilter.Or(Equal("userPrincipalName", v), Equal("mail", v))`, otherwise as `LdapFilter.Or(Equal(
   "sAMAccountName", v), Equal("displayName", v), Equal("cn", v))`, limited to users and contacts
   (`ObjectKind.User.ToLdapFilter()`, `ObjectKind.Contact.ToLdapFilter()`) for `manager`, and to users, contacts and
   groups for `managedBy` of groups and computers (ADUC allows a group there), batched 12 values (≤ 50 clauses) per
   search. One match → `ManagerDn`; none → warning `ManagerNotFound`; several → warning `ManagerAmbiguous` (`{0}` the
   count; `BulkCreateRow.ManagerCandidates` keeps the DNs for the cell drop-down of 2.7); a value that names another row
   of the batch (its `sAMAccountName`, UPN, `mail` or `displayName`) and nothing in AD → warning `ManagerInBatch` (the
   manager must exist when the row is written; set it later with the batch editor). The row is created without a manager
   in all three cases.
4. **Groups**: the option groups (read once, step 1) plus the row's `Groups` cells (`CellText.SplitList`; a DN checked
   with `GetAsync`, a name looked up as `LdapFilter.And(ObjectKind.Group.ToLdapFilter(), LdapFilter.Or(Equal(
   "sAMAccountName", n), Equal("cn", n)))`, batched; a cell part that is not found but whose `SplitCommas` parts each
   resolve to exactly one group → those groups with warning `GroupsSplitOnCommas`, otherwise `GroupNotFound` with the
   hint *rozdziel grupy średnikiem*); several → `GroupAmbiguous`; a group whose effective permissions are known and do
   not allow writing `member` → warning `NotWritable`. **Nesting rules** for created groups (from `groupType`): Global →
   any scope (Global only in the same domain); Universal → Universal or Domain Local; Domain Local → Domain Local of the
   same domain; a forbidden pair → row error `GroupScopeNotAllowed` (`{0}` the group) and the step is left out. A group
   that is the account's **primary group** (RID 513 Domain Users for users, 515 Domain Computers for computers, from
   `objectSid`) is left out with the info `PrimaryGroupSkipped`. Distinct by `PlanningContext.Key`.
5. **Status**: `Error` when any error issue, `Warning` when any warning, else `Ok`.

`BulkCreateComputation.Attributes` lists the check-grid columns: `cn`, `sAMAccountName`, `userPrincipalName`, `mail`,
`givenName`, `sn`, `initials`, `displayName`, then every other attribute that has a value in some row, by name.

**`BuildPlan`** is pure (no directory access, no session): one `ObjectPlan` per row with status `Ok`, `Warning` or
`Error` (error rows are kept with their issues, so the preview shows them and they cannot be included; `Excluded` and
`Created` rows are left out). `DistinguishedName` = the row's DN, `DisplayName` = `cn`, `Kind`, `ObjectClasses =
ClassChainOf(kind)`, `SourceRow = row.Number`, `Issues` = the row's issues, `PowerShell = BulkCreatePowerShell.For(…)`.
`ChangePlan`: `Title = title` (localized by the caller), `CreatedAt = time.GetUtcNow()`, `Server =
computation.Server` (the DC the values were read from – the executor refuses another, rule 4), `Sequential = false`
(the canary of 2.7 stops a systematic failure; a sequential plan would make 5 000 rows run one at a time).
Steps per kind (the order is the ADUC order; the first failed step stops the object, `PartiallySucceeded`):

* **User.** `Create`: `objectClass = [user]`, every row value except `cn`, `userAccountControl = [514]` (normal,
  disabled – AD refuses an enabled account without a password; a created UAC never contains 0x20 PASSWD_NOTREQD),
  `accountExpires` (`AdConvert.ToFileTime(ExpiresAt)`, the caller passes the end of the chosen day as the batch
  editor's expiration editor does; a row's `ExpiresAt` cell wins), `proxyAddresses = ["SMTP:" + mail]` with
  `AddPrimarySmtp`; empty values dropped. `ResetPassword` unless `None`: `Generate` → `passwords.Generate(
  GeneratedLength, tokens)` per account, tokens = `sAMAccountName`, `givenName`, `sn` and the `displayName` parts split
  on `, . - _ # tab space` (as `ChangePlanner.ForbiddenPasswordTokens`); `SameForAll` → `sharedPassword` (length and
  complexity checked as in step 1 of `ComputeAsync`), with the step warning `PasswordContainsName` (contains the
  `sAMAccountName` or a `displayName` part of 3+ characters, case-insensitive – the AD complexity rule). `Modify` (only
  when a password is set): `userAccountControl [514] → [512]` when `Enabled` (compare-and-swap on the old value),
  `pwdLastSet [] → ["0"]` when `MustChangeAtLogon`. `Modify` `manager [] → [dn]` when the row has a `ManagerDn` (a
  separate step: a manager deleted or moved since the check leaves a working account without a manager,
  `PartiallySucceeded`, retryable, instead of failing the whole add with `noSuchObject`). Then one `AddMember` per group
  (`TargetDn` = the group). A password the domain rejects therefore leaves a disabled account without groups; a group
  step that fails leaves a working account without that group – both `PartiallySucceeded`, both retried (2.7).
* **Contact.** `Create` (`objectClass = [contact]`, the values), `Modify` `manager`, then `AddMember` (distribution
  groups).
* **Group.** `Create` (`objectClass = [group]`, `sAMAccountName`, `groupType = GroupTypes.Compose(GroupScope,
  SecurityGroup)`, the values), `Modify` `managedBy`, then `AddMember` (nesting, scope rules above).
* **Computer.** `Create` (`objectClass = [computer]`, `sAMAccountName = NAME$`, `userAccountControl = [4098]`
  (`WorkstationTrustAccount | AccountDisabled`), the values; with a joiner (`BulkAccountOptions.JoinerSid`)
  `AclPlanning.ForNewObject(ComputerJoinRights.Grant(sid, name))`), then `ResetPassword` with a random password of
  `ResetComputerAccountOperation.GeneratedLength` (120) characters that is never shown, copied or exported (as the K1
  reset; `BatchReportExporter` already leaves computer passwords out), then `Modify` `userAccountControl [4098] →
  [4096]`, `Modify` `managedBy`, then `AddMember`. The K1 *Nowy komputer* dialog keeps creating 4096 without a password;
  the Windows behaviour of both is on the pilot checklist (2.11). With a joiner while the window's account is not one of
  the owners KB5020276 trusts (`PrivilegeCheck`), the run carries K1's text as warning `JoinOwnerNotTrusted` (a join that
  reuses the account fails unless the owner is allow-listed by policy).

Values never reach the plan from anywhere but the computation: `BuildPlan` adds only the option-driven attributes and
steps above. `BuildPlan` keeps no reference to `sharedPassword` beyond the `PlannedChange.Password` values; the caller
clears its copy right after the call.

**`BuildRetryPlan`** (pure): the failed, partially succeeded and cancelled objects of the accumulated result, each
without the steps that succeeded in any execution (the batch editor's `StepKeys` semantics). A `ResetPassword` step that
did not succeed gets a **new** password – `Generate` and computers: generated again (a failed reset was never applied or
handed out, so this is always safe); `SameForAll`: `newSharedPassword` when the last attempt failed with
`PasswordPolicy` (2.7 asks for it), else the step's previous password. A step that succeeded is never repeated, so an
account never gets a second password. An object whose Create failed with `AlreadyExists` or `ConstraintViolation` is not
executed again (it would fail again): it stays in the retry plan, not executable, with the error `ValueTakenSinceCheck`
(*wróć do Sprawdzenia*);
`PowerShell = null` on objects without their Create step.

**`ReadCreatedAsync`** (after each execution, memory only): `objectGUID` and `whenCreated` of every object whose Create
succeeded, in one-level searches of the container with `LdapFilter.Or` of `Equal("distinguishedName", dn)`, 50 per
search → `CreatedObject(Row, DistinguishedName, Kind, ObjectGuid, WhenCreated, MustChangeSet)`.

**`PlanUndoAsync`** builds the undo of **everything this wizard created** (all executions and retries): one `Delete`
per object, `ObjectPlan.ObjectGuid` set. Before that it re-reads every object by GUID (`<GUID=…>` base reads, batched by
`Or(Equal("objectGUID", …))` one-level in the container, then a domain subtree search for those not found there): gone
or another GUID at that DN → kept as a non-executable object with the error `UndoObjectReplaced` (renamed or moved objects are deleted at
their current DN; a different object now holding the old DN is never touched); `lastLogonTimestamp` set, `logonCount` >
0, or `pwdLastSet` ≠ 0 where must-change was set → warning `UndoAccountInUse`; children (one-level search, size limit
1, e.g. BitLocker recovery or Hyper-V objects under a joined computer) → warning `UndoHasChildren` (the Delete fails with
`NotAllowedOnNonLeaf`; a subtree delete is never planned). The executor's Delete does not check the GUID, so between the
undo preview and its execution an object could still be replaced – the window is the operator's; the note says so.
Objects deleted into the Recycle Bin keep `sAMAccountName`, UPN and `mail`, so a re-run warns `NameInRecycleBin` for
them unless they are in `ReleasedValues` (2.4).

**Records** (`BulkCreateModel.cs`, complete in the contract) – every `ToString` prints kinds, counts, row numbers and
statuses, never a value, a DN or a header (pinned by the reflection test of 2.11): `AttributeRule`, `BulkCreateRules`,
`BulkAccountOptions`, `BulkCreateInput`, `BulkValue`, `BulkCreateRow`, `DomainPasswordPolicy`, `BulkCreateComputation`,
`CounterPreview`, `CreatedObject`, `CountryValue` and the enums `CounterMode`, `CollisionStrategy`,
`InitialPasswordMode`, `PhoneFormat`, `BulkRowStatus`, `BulkValueSource`, `UniquenessScope`, `TextCase`,
`SuggestionNote` (signatures in 2.12). `BulkCreateLookups` (`BulkCreateLookups.cs`; the contract has the constructor,
`ForestReader`, `Refresh` and `ToString`, B2 the caches) is thread-safe, holds directory values in memory only, and is
owned and dropped by the wizard (2.8). New issue codes live in `BulkCreateIssueCodes` (`BulkCreateIssueCodes.cs`,
complete in the contract; translated through `Issue.<code>` like `IssueCodes`): `CounterExhausted`, `CounterTooLong`,
`Disambiguated`, `DuplicateInDirectory`, `DuplicateRow`, `ImplicitUpnClash`, `EmptyToken`, `NameInRecycleBin`,
`UpnSuffixNotInForest`, `MailDomainNotInForest`, `NonRoutableMailDomain`, `PossibleExistingAccount`,
`DuplicateSourceRow`, `ScientificNotation`, `InvalidPhone`, `LineBreakInValue`, `AllCapsName`, `TrailingDollar`,
`CountryNotFound`, `InvalidDate`, `ManagerNotFound`, `ManagerAmbiguous`, `ManagerInBatch`, `GroupNotFound`,
`GroupAmbiguous`, `GroupsSplitOnCommas`, `GroupScopeNotAllowed`, `PrimaryGroupSkipped`, `GroupPasswordPolicy`,
`EnabledWithoutPassword`, `PasswordsNeedEncryptedChannel`, `PasswordTooShortForDomain`, `PasswordNotComplex`,
`PasswordContainsName`, `CheckedInDomainOnly`, `SchemaUnavailable`, `ComputerNameNotDnsSafe`, `JoinOwnerNotTrusted`,
`RidPoolLow`, `ValueTakenSinceCheck`, `UndoObjectReplaced`, `UndoAccountInUse`, `UndoHasChildren`; plus the set
`AttributeAndArguments` of the codes whose text takes `{0}` = the attribute's display name and `{1}…` = `Arguments`
(`CounterTooLong`, `DuplicateInDirectory`, `DuplicateRow`, `ImplicitUpnClash`, `EmptyToken`, `PossibleExistingAccount`,
`DuplicateSourceRow`). The existing `IssueCodes.TooLong`, `TooShort`, `SingleValued`, `InvalidValue`,
`MandatoryAttribute`, `CannotCreateChild`, `TargetNotFound`, `UnknownAttribute`, `NotWritable` and `TemplateError` are
reused with the attribute only (their texts take `{0}` = attribute); `Duplicate`, `DuplicateInBatch` and
`InvalidCombination` are not used by F3. `DisplayNames.IssueText` gets one branch in the contract: for a code in
`BulkCreateIssueCodes.AttributeAndArguments`, `{0}` is the attribute's display name and `{1}…` are the arguments; every
other code renders as today. Run issues without an attribute have localized texts without an attribute placeholder, so
none falls back to the English `Message`.

### 2.6 Presentation/App – data, columns, naming rules, account options and profiles (B3)

**Host** (contract, complete): `BatchEditorRequest.Create` (`BulkCreateRequest(ObjectKind Kind, string? ParentDn)`)
switches the batch editor to `BatchSourceMode.Create` (`IsCreateMode`, `IsObjectsMode`; window title
`L.BulkCreate.Title.<kind>` with the container's name – *Zakładanie wielu kont / kontaktów / grup / komputerów*) and
creates `BulkCreateViewModel` (`BulkCreate`) – sub-steps `BulkCreateStep` (`Source`, `Columns`, `Rules`, `Check`, with
`Is…Step`, `NextStepCommand`, `PreviousStepCommand`, `CanGoNext`), `Kind` / `Kinds` (`Option<ObjectKind>`, texts
`BulkCreate.Kind.<kind>`: Użytkownicy, Kontakty, Grupy, Komputery), `ContainerDn` / `ContainerName` /
`PickContainerCommand` (`IDialogService.PickContainerAsync` on the window's session; the shell's target pre-filled), the
children `Source`, `Columns`, `Rules` (B3) and `Check` (B4), `BuildInput()` (null while a child is invalid) and the
`Changed` event; `InitializeAsync` calls `Rules.InitializeAsync` and `Check.InitializeAsync`; `OnSessionEnded()` calls
`Check.OnSessionEnded()`. Changing the kind asks before it replaces changed rules with the kind's default profile. In
App, `BulkCreateView.xaml` (sub-step header like the batch editor's `StepStyle`, one `ContentControl` per child with
implicit `DataTemplate`s) and the create-mode `ContentControl` in the Edit area of `BatchEditorWindow.xaml` are complete
in the contract; the child views are placeholders the owners replace.

**1. Dane – `BulkSourceViewModel`** (`BulkSourceViewModel.cs`, `SourceRowViewModel`, `SourceColumnViewModel`):
`PasteCommand` reads `Services.Clipboard.ReadTable()` on the UI thread (with a busy indicator) and parses on `Task.Run`
(`TableTextReader.ReadClipboard`); an empty table is replaced, otherwise the paste is **appended**
(`TableTextReader.Append`; *Wklej i zastąp* = `ReplaceWithPasteCommand`) – but when `CountRepeatedRows` finds that every
pasted row (or more than half of them) is already loaded, it asks *Te wiersze są już wklejone – Dołącz mimo to / Zastąp
/ Anuluj*; Ctrl+V on the source grid when no cell is in edit mode runs `PasteCommand` (KeyBinding in the view).
`LoadFileCommand`: `IFileDialogService.OpenFileAsync` with *Arkusze i pliki CSV (\*.xlsx;\*.csv;\*.txt)*, read on the
thread pool (`.xlsx` → `ReadXlsx` from the bytes, never a temp file; others → `CsvFiles.ReadTextAsync(path,
Regional.AnsiCodePage)` (UTF-16 BOM recognised) → `ReadDelimited`; files over 50 MiB refused). `HeaderMode`
(*Pierwszy wiersz zawiera nagłówki*: `BulkCreate.HeaderMode.<mode>` – *automatycznie / tak / nie* → `Reinterpret`),
*Wiersz nagłówków* (the line, offered when `TitleRows` is not empty or the operator wants another line → `Reinterpret`),
*Pierwszy wiersz danych w Excelu* (`ExcelRowOffset`, default 2 with a header and 1 without; only for display: the
check grid and the results show `number + offset - 1` as the Excel row), `AddRowCommand`, `AddColumnCommand` (header
typed by the operator), `RemoveRowsCommand` (selection), `ClearCommand`. Edits in this step, added rows and appended
pastes keep the check step's edits and pins (rows keep their numbers); only the header choices, *Wklej i zastąp* and
*Wyczyść* drop them. Rows in a `BulkObservableCollection` (editable cells, `ReplaceAll`), `Summary` in the *label: count*
style (*Wiersze: 120, kolumny: 7 – nagłówki rozpoznane*), localized `Warnings` (`BulkCreate.Warning.<code>` with row and
column). A password-like column shows `••••••••`, is read-only and copies the mask (the column descriptor carries
`IsMasked`; the App sets the column's `ClipboardContentBinding` to the mask). `Table` (`InputTable`) is the output. App:
`ClipboardService.ReadTable()` reads `Clipboard.GetText(UnicodeText)` first and asks for the raw `HTML Format`
(`ClipboardHtmlReader.ReadRaw`, ≤ `CfHtmlParser.MaxBytes`) only when `TableTextReader.NeedsHtml(text)` – Excel renders
formats on request, and the HTML of a 5 000-row range takes seconds and megabytes –, **no bitmap** (an Excel copy
carries a picture of the range, `ReadFormatted` would encode it), `ExternalException` → `ReportBusy` (HRESULT only). The
default `IClipboardService.ReadTable() => ReadFormatted()` keeps every test double working (`FakeClipboard.Formatted`).
The user doc notes that Excel copies hidden columns but not rows hidden by a filter.

**2. Kolumny – `BulkColumnsViewModel`** (`BulkColumnMappingViewModel`): one row per column (header or letter, the first
three non-empty samples – masked for password-like columns –, target, attribute, name order, *Wielkość liter*
(`BulkCreate.Case.<TextCase>`: *bez zmian / Jak W Nazwie / małe / WIELKIE*, offered for text targets),
password-like flag, the suggestion note `BulkCreate.SuggestionNote.<note>` such as *rozpoznano numery komórkowe*).
Targets (`BulkCreate.Target.<kind>`): *Pomiń*, *Atrybut* (an editable, virtualized `AttributeOption` combo:
`AttributeOption.ForKinds(schema, localizer, [kind])` minus `CreationValues.IsBlockedAttribute` – `extensionAttribute1–15`
and custom schema attributes included; each item shows the localized label with the LDAP name, *Telefon komórkowy
(mobile)*), *Tylko we wzorcach*, *Imię i nazwisko* (+ order, `BulkCreate.NameOrder.<order>`), *Menedżer (przełożony)*,
*Grupy*, *Kraj/region*, *Konto wygasa*. Auto-mapping (`AutoMapCommand`, also on load): the profile's `HeaderMappings`,
then `ColumnSynonyms.Suggest(header, kind, names, samples)` with the first 20 non-empty cells, then the LDAP name,
`Display [ldap]` or the localized display name (`TextFolding.Fold`, as `CsvImportViewModel.AutoMap`), else *Pomiń*; a
table without a header uses the profile's mappings by letter (`#C`), then `SuggestFromContent`. A second column for the
same attribute → *Pomiń* with a note. A phone column whose note is `PhonesWithoutPlus` offers *Format międzynarodowy*
(sets `Phones`). `BulkCreateCountries.Build(localizer)` fills `BulkCreateInput.Countries` from the existing
`Countries.All`: the alpha-2 and numeric codes and the `Country.XX` names of both resource languages (resource manager
with `pl` and `en`), folded, each mapped to the code, the name in the UI language and the numeric code. `MappingError`
(localized or null), `ToMappings()`.

**3. Nazwy i opcje – `BulkRulesViewModel`** (`AttributeRuleViewModel`), in sections (`Expander`s) **Nazwy**, **Konto**,
**Grupy** and a collapsed **Zaawansowane** with a one-line summary:

* *Nazwy*: the **Numeracja** panel above the rules – *Prefiks*, *Pierwszy numer*, *Liczba cyfr (zera z przodu)*, *Krok*
  – bound to the single `CounterSpec` (it rewrites every `{$nr…}` of the rules, so `CounterMismatch` cannot arise from
  the panel), and *Pierwszy wolny numer: kd261 (najwyższy istniejący: kd260)* from `PreviewCounterAsync` on entering the
  step (cached in the lookups; *bez sprawdzenia w AD* while disconnected). The rules list (attribute, pattern, live
  `Sample` of the first three included rows plus the row with the longest rendered identifier, rendered with the first
  free number, marked *(zmieniono znaki)* / *(skrócono)* from `FinishedValue.Sanitized` / `Truncated`; the localized
  pattern error with position and token, and its suggestion; `IsCounter`). An identifier rule that is a literal ending
  in digits (`kd201`) shows the quick fix *Numerować kolejno od 201? → kd{$nr:201}* (`CreationPatternSet.SuggestCounter`)
  and is never disambiguated. A rule whose attribute is also fed by a mapped column shows *Kolumna „E-mail” ma
  pierwszeństwo – reguła działa tylko dla pustych komórek* with the toggle *Reguła zastępuje kolumnę*
  (`OverridesColumn`). *Dodaj regułę*, *Usuń*; *Wstaw pole* (`InsertTokenCommand`) lists fields by their Polish labels
  (*Imię → {givenName}*, from the localized attribute names): a mapped column is inserted as its attribute (so it follows
  edits in the check grid), `{$column:…}` is offered only for *Tylko we wzorcach* and ignored columns (never for
  password-like ones); then `{$nr:…}`, `{$suffix}`, `{$mailDomain}`, `{$domain}`, `{$parentName}`, `{$date:yyyy-MM-dd}`
  and `{$date:dd.MM.yyyy}`, and the functions with their Polish descriptions (2.9). The UPN row has the prefix pattern,
  a suffix combo (`UpnFieldViewModel.ReadUpnSuffixesAsync`; the list also goes into `BulkCreateInput.UpnSuffixes`; it
  shows *z wzorca* when the prefix renders a whole address) and the preset *UPN = adres e-mail* (rule `{mail}`). Default
  suffix: the profile's suffix when it is still in the list (else a warning and the next choice), else the window's
  domain, as ADUC. *Domena adresów e-mail* (`MailDomain`, default = the UPN suffix; a warning for `.local`, `.lan`,
  `.internal`).
* *Konto* (users): `PasswordMode` (*Generuj osobne hasło dla każdego konta* / *Jedno hasło dla wszystkich kont* / *Bez
  hasła – konto wyłączone*), `GeneratedLength` (`PasswordGenerator.MinimumLength`–`MaximumLength`, default 16,
  `L.Batch.PasswordLength` check, *minimum domeny: 7* once known), `PasswordInput` / `ConfirmPasswordInput`
  (`IPasswordInput`, set by the view's `PasswordBox`es, never bound), `MustChangeAtLogon` (on), `Enabled` (on; a note
  when one shared password is combined with enabled accounts: *wszyscy będą znać hasło każdego konta do pierwszej
  zmiany*), `HasExpiry` + `ExpiryDate`; groups: `GroupScope`, `IsSecurityGroup`; computers: `Joiner` +
  `PickJoinerCommand`.
* *Grupy*: `AddGroupsCommand` → `PickObjectsAsync(session, [Group], multiSelect: true)`, `RemoveGroupCommand`.
* *Zaawansowane*: `CounterMode` (`BulkCreate.CounterMode.<mode>`: *najniższy wolny numer (uzupełnia luki)* / *kolejny
  po najwyższym istniejącym*), *Nie używaj ponownie numerów z Kosza AD* (`ReuseRecycleBinNumbers` inverted), `Collisions`
  (*dopisz numer* / *oznacz jako błąd*; computers always the latter), `SanitizeIdentifiers`, `TruncateNames`,
  `AddPrimarySmtp` (default **off**), `Phones` (default `AsIs`), *Szukaj istniejących kont tej osoby* (`MatchAttributes`
  on / off).

The rules page notes that home folders are not created. Outputs: `ToRules()`, `ToOptions()`, `ReadSharedPassword()`
(reads and clears the inputs; **UI thread only**, called by B4 at plan time; a confirmation mismatch →
`L.Batch.PasswordMismatch`), `NeedsSharedPassword` (shared mode and empty boxes – *wpisz hasło ponownie* after a Back),
`Error`. Pattern examples in resx are written with doubled braces (`{{$nr:201}}`) and always read through
`Localizer.Format` (`FormatPlaceholders_MatchBetweenLanguages` formats every value), never through `{l:Tr}`.

**Profiles** (`BulkCreateProfileStore.cs`, B3; `Settings/BulkCreateSettings.cs` + `AppSettings.BulkCreate`, complete in
the contract): a combo *Profil nazewnictwa* with the built-ins (code, never stored): users *Standardowy* (`cn` and
`displayName` `{givenName} {sn}`, `sAMAccountName` `{givenName|sam|lower}.{sn|sam|lower}`, UPN prefix
`{sAMAccountName}`, `FillGaps`) and *Konta numerowane (kd201…)* (`sAMAccountName` `kd{$nr:201}`, UPN prefix
`{sAMAccountName}`, `mail` `{sAMAccountName}@{$mailDomain}`, `cn` / `displayName` `{givenName} {sn}`, `AfterHighest`,
`ReuseRecycleBinNumbers` false); contacts, groups (`cn` from the column, `sAMAccountName` `{cn}`) and computers (`cn`
from the column, upper case; `sAMAccountName` `{cn}` – `$` is appended by `Finish`) *Standardowy* – plus the saved
profiles of the kind. **Selection** on opening: the container's default profile, else the last used profile of the kind
(`BulkCreateSettings.LastProfileIds`), else *Standardowy*. A changed profile shows *(zmieniony)* and switching away asks;
*Zapisz* on a built-in becomes *Zapisz jako…* with the name *„… (kopia)”*; *Usuń*; *Domyślny dla tego kontenera*
(portable DN of the container, `SavedView.ToPortableDn`). A profile stores: `Id`, `Name`, `Kind`, `Rules` (attribute,
pattern, `OverridesColumn`), `UpnSuffix` (the literal suffix the operator picked – a DNS name like a connection
profile's domain, allowed by rule 1), `MailDomain` (likewise), `CounterMode`, `ReuseRecycleBinNumbers`, `Collisions`,
`SanitizeIdentifiers`, `TruncateNames`, `AddPrimarySmtp`, `Phones`, `InitialMode`, `InitialLength`,
`MustChangeAtLogon`, `CreateEnabled`, `ExpiresAfterDays`, `MatchExisting`, `GroupDns` (portable) with `GroupDomain` (the
DNS name of the domain they were picked in – in a window of another domain they are dropped with a warning, never
resolved to a same-path group there), `GroupScope`, `SecurityGroup`, `HeaderMappings` and `DefaultForContainers`
(portable) – like a saved view: names, patterns, options and DNs, never a row value, a rendered value, a counter position
or a password; property names avoid *password*, *secret*, *credential* (`SerializedSettings_NeverContainPasswords`).
`HeaderMappings` (folded header or `#letter` → target, attribute, order, case) are stored only when the table had a
header (or by letter without one) and the folded header has no `@`, fewer than 7 digits and at most 64 characters, and
never for password-like headers; *Zapisz* lists the headers that will be saved. `BulkCreateSettings.MaxProfiles = 50`;
`Normalize` drops profiles without a name, rules with blocked or empty attributes, mappings that break the header rule,
clamps `InitialLength` and de-duplicates ids. The joiner is per run only.

**Operator scenario** (the acceptance test `BulkCreateEndToEndTests.OperatorScenario`, pl-PL, on
`AddBulkCreateSample`): the operator copies five columns from Excel – *Telefon komórkowy, Imię, Nazwisko, Dział,
E-mail* – with five people with diacritics and presses Ctrl+V; the columns map to `mobile`, `givenName`, `sn`,
`department` and `mail` without a click; in *Nazwy i opcje* the operator chooses *Konta numerowane*, the suffix
`contoso.com` and the group *GG_Nowi* (must-change stays on); the check shows kd208–kd212 (live kd201, kd203, kd205 and
kd207 in the Recycle Bin, `AfterHighest`), UPN `kd208@contoso.com`, the sheet's `mail` (a column wins over the profile's
mail rule, with the note of step 3) and the mobile numbers; the plan creates them with `pwdLastSet` 0 and `AddMember`;
the results list five passwords. The profile is saved as the default for *Nowi pracownicy*; a new wizard on that
container picks it up and its first free number is kd213. Regression guard: at most 10 commands and choices besides
typing the confirmation.

**Single-object dialogs:** `NewObjectViewModel.OpenBulkCreateCommand` (users, contacts, groups, computers – not OUs)
opens the batch editor with `BulkCreateRequest(Kind, ParentDn)` and closes the dialog; App: a *Wiele naraz…* button in
`NewObjectWindow.xaml` left of OK.

**UI tests and screenshots** (B3): `tests/AdMng.App.UiTests/BulkCreateInputUiTests.cs` (steps 1–3 render without
binding errors with pasted sample data, the attribute combo, the password boxes, Ctrl+V, a 5 000-row paste stays
responsive), screenshots **40** `40-bulk-create-data.png` (*Dane* with a pasted Excel range), **41**
`41-bulk-create-columns.png` (*Kolumny* with automatic mapping) and **42** `42-bulk-create-rules.png` (*Nazwy i opcje*
with the *Konta numerowane* profile, the *Numeracja* panel and live samples) in `ScreenshotTestsBulkCreateInput.cs`.

### 2.7 Presentation/App – check grid, change plan, results with passwords and entry points (B4)

**4. Sprawdzenie – `BulkCheckViewModel`** (`BulkCheckViewModel(DialogServices services, IBulkCreatePlanner planner,
Func<BulkCreateInput?> input, Func<IDirectorySession?> session, IDirectoryConnector? connector)`, `CheckRowViewModel`,
`CheckCellViewModel`): entering the step computes (`ComputeCommand` with cancel, on `Task.Run` through
`SerialCancellation`, progress bar, `Stop`). **Grid:** a WPF `DataGrid` with `EnableRowVirtualization` and
`EnableColumnVirtualization`; its columns are generated in code-behind from a Presentation column-descriptor list
(`CheckColumnDescriptor`: key, header, editable, masked – no WPF types in Presentation), cells bound by index; *Nr* (the
Excel row, 2.6), *Imię*, *Nazwisko* frozen on the left (`FrozenColumnCount`); then `Computation.Attributes`, *Menedżer*
(the RDN of the resolved DN), *Grupy* (count, tooltip names); each row has the include check box, the status glyph
(`RowSeverityToGlyphConverter`), *Uwagi* (the localized issues joined) and editable cells. The first fill and a filter
change use `ReplaceAll`; a recompute updates the existing `CheckRowViewModel` / `CheckCellViewModel`s **in place**, keyed
by row number, and never touches the cell in edit mode (scroll position, selection and the edited cell survive).
Editing a cell (commit on Enter or focus loss) stores `Edits[row][attribute]` and recomputes debounced (300 ms, a newer
edit cancels the running recompute); edited cells are marked, `Adjusted` cells show the reason in a tooltip, the cell
menu has *Przywróć wartość wyliczoną*. *Menedżer* and *Grupy* are editable too: the text is resolved again (an edit of
the hidden source cell of the `Manager` / `Groups` column), and an ambiguous manager offers its candidates in the cell's
drop-down (`ManagerCandidates`). Unchecking a row adds it to `ExcludedRows`. Filters (`BulkCheckFilter`, texts
`BulkCheck.Filter.<filter>`): *Wszystkie / OK / Ostrzeżenia / Błędy / Możliwe duplikaty / Wykluczone / Utworzone*;
*Następny błąd* (F8); *Kopiuj wiersze z błędami* (the source cells of the error rows plus *Uwagi*, TSV through `SetText`,
for sending back to HR). `Summary` in the *label: count* style (*Wiersze: 120 – OK: 108, z ostrzeżeniami: 6, z błędami:
4, wykluczone: 2*); `ScopeNote` from `Computation.Scope` (*Unikalność UPN i adresów: cały las (jedna domena)* / *cały
las (wykaz globalny na DC1)* / *sprawdzone tylko w domenie*); run issues above the grid; *Sprawdź ponownie*
(`lookups.Refresh()` + compute), *Numeruj od nowa* (clears the pins). Pins = the last computation's numbers, kept while
the counter spec and the row's sequence are unchanged. **GC session:** `InitializeAsync` reads the forest
(`ForestScope.ReadAsync`); when it has several domains, the window DC's `Info.IsGlobalCatalogReady` is true and the batch
editor got an `IDirectoryConnector`, the GC session (2.2) is connected in the background and the connect `Task` is
kept: `ComputeAsync` and `PlanAsync` await it (bounded by the connect timeout, cancellable), and when it arrives new
lookups with that `ForestReader` are built and the grid recomputes once; failure or no connector → scope `Domain`. A
connect that completes after `Dispose` or after the session ended disposes that session at once (`DisposeAsync`
fire-and-forget, the log line without values). The session is closed on `Dispose`, on `SessionChanged` and in
`OnSessionEnded` (which also cancels a running compute and drops the lookups); the `Sessions.SessionChanged`
subscription is removed in `Dispose`. Toggling the header or loading a new table drops edits, pins and created marks.

**Change plan** (`BulkCreateViewModel.Plan.cs` and the batch editor's create branches, B4): `PreviewAsync` in create
mode = `RunPlanningAsync((progress, ct) => BulkCreate.PlanAsync(progress, ct), BulkCreate.IncludedRowCount,
PlanSource.Create, ct)`. `PlanAsync`: (1) on `Task.Run`: refreshes the lookups and computes again with
`FrozenValues` = the check computation's values (2.4 – nothing is renumbered or disambiguated; a value taken since the
check becomes an error of its row, shown in the preview and in the check grid after *Wstecz*); (2) back on the UI
thread: reads the shared password **last** (`Rules.ReadSharedPassword()`; empty boxes after a Back → the localized note
*wpisz hasło ponownie* and no plan); (3) on `Task.Run`: `BuildPlan(computation, password, title)` (title
`L.BulkCreateCheck.ChangeTitle`: *Utworzenie kont (114) w jednostce „Nowi pracownicy”*); (4) clears the boxes and its
copy only after `BuildPlan` returned (a failed or cancelled compute leaves the typed password in the boxes). The rest is
the batch editor: preview with filters and include check boxes, export, PowerShell, then the **typed confirmation** for
every create plan with `BulkCheck.ConfirmCreate` instead of `Batch.ConfirmExecute` (*„{1}” – obiekty do utworzenia w
domenie {3}: {0}. Hasła początkowe będą dostępne tylko w tym oknie. Wpisz {2}, aby potwierdzić.*), and execution through
`Services.Coordinator.ExecutePlanAsync(plan, ChangeTitle, RequireSession(), …)` in two parts: the **canary** – the first
executable object as a one-object plan – and, only when all its steps succeeded, the rest as a second plan (same title,
two activities and two audit entries). A canary whose Create failed with `AlreadyExists` or `ConstraintViolation` (a
row-level value problem) hands over to the next object (at most three canaries); any other failure of a canary step
(`StrongAuthRequired`, `AccessDenied` on the reset or the UAC write, `PasswordPolicy` …) stops: the results show it with
*Wstrzymano po pierwszym koncie – pozostałe wiersze nie zostały wykonane* and the rows that did not run stay *not
created* in the check step. `CanPreview` = `BulkCreate.IsReadyToPlan` (check step reached, computation without run
errors, at least one OK or warning row, password inputs valid). `PreviewBuilder.Build` / `BuildResult` of 5 000 creates
(≈ 100 000 rows) are measured in the end-to-end test; when they miss the budget of 2.11 on the UI thread, B4 moves the
row building of `ShowPreview` / `ShowResults` into the planning `Task.Run` (lead-agreed anchor, 2.13).

**Accumulated results and passwords.** In create mode every execution of the window – the create plan, its canary and
rest, retries, a second plan after *Wstecz* – is merged into `BulkCreate.Accumulated` (`BulkCreateResults.Merge`), and
the batch editor's `Result` shows the accumulated result. `OnExecuted(result)` (only for `PlanSource.Create` and its
retries) merges, marks the rows whose Create succeeded as `Created` (with their final DN: `CreatedRows` of the next
input), runs `ReadCreatedAsync` (GUIDs, memory only) and updates `CreatedAccounts`. In create mode
`HasUnsavedGeneratedPasswords` is true while a created user account with a password was neither exported (with
passwords), nor copied by *Kopiuj tabelę z hasłami*, nor handed out per row; `GeneratesPasswords` is true when the
planned `PasswordMode` was `Generate` (captured at plan time); the `Batch.ReplacePasswords` prompt never appears in create
mode (nothing is replaced); `CanCloseAsync` and `ToolWindows.HasUnsavedGeneratedPasswords` warn as for resets. Machine
passwords of computers are never listed.

**Retry** (`RetryFailedAsync` create branch): `BuildRetryPlan(computation, Accumulated, newSharedPassword, title)` (2.5)
→ preview → execution (no canary). When a shared password was rejected (`PasswordPolicy` on a `SameForAll` reset), the
results panel shows two password boxes *Nowe hasło dla kont bez hasła* (`IPasswordInput`, set by `BulkResultsView`,
read and cleared like the rules' boxes) and *Ponów* stays disabled until they match; generated passwords are generated
again by the retry plan. An object whose value was taken since the check is not retried (*wróć do Sprawdzenia*).

**Undo** (`Undo` create branch): `PlanUndoAsync(session, created, title)` over `BulkCreate.CreatedObjects` (all
executions) → the rollback preview (`PlanSource.Rollback`) with `UndoNote` counted from that plan (*Cofnięcie usunie
obiekty (114) utworzone w tym oknie – razem z ich hasłami i członkostwem w grupach; przy włączonym Koszu AD można je
z niego przywrócić*) and the plan's issues (replaced, in use, with children). After the undo executed,
`OnUndone(result)` clears `Created` and `CreatedRows` for the deleted DNs, adds their identifiers to `ReleasedValues`
(2.4) and marks their `CreatedAccounts` *Usunięte (cofnięte)*; an undo followed by a re-run gives the same numbers.

**Results** (`CreatedAccountViewModel`, `BulkResultsView.xaml`): `CreatedAccounts`: status, *Nr* (Excel row), *Nazwa*,
*Login*, *UPN*, *E-mail*, *Hasło* (masked `••••••••`, *Pokaż* per row, *Kopiuj* → `IClipboardService.SetSecret`,
`WasHandedOut`), message – modelled on `ResetPasswordViewModel.PasswordResultViewModel`. *Kopiuj tabelę z hasłami…*
asks (`L.Batch.ExportPasswordsWarning`) and puts a TSV through `SetSecret` (cleared after 60 s): *Nr*, Nazwa, Login,
UPN, E-mail, the operator-chosen source columns (by default the phone columns and the *Tylko we wzorcach* columns – the
private address and the mobile number are what the passwords are sent to) and Hasło, written with `CsvCodec.Write(…,
'\t', protectFormulas: true)` so Excel does not evaluate cells starting with `= + - @`; *Kopiuj bez haseł* uses
`SetText`; both set the handed-out marks through `BatchEditorViewModel.MarkPasswordsHandedOut()` (internal, contract).
*Eksportuj…* is the batch editor's `ExportResultsCommand` (passwords only after the warning). A note: *Konta utworzono na
kontrolerze DC1 (emulator PDC: tak/nie); pozostałe kontrolery zobaczą je po replikacji – do tego czasu logowanie przez
inne kontrolery może się nie udać, a konto o tej samej nazwie utworzone w tym czasie na innym kontrolerze nie zostanie
wykryte*; after a partial failure, *Konto bez hasła pozostaje wyłączone*; a failed Create with `ConstraintViolation` in
a `CheckedInDomainOnly` run gets *UPN może być już używany w innej domenie lasu* (Windows' forest-wide UPN check,
0x21C8); a DC that cannot get a new RID pool fails the remaining Creates with the DC's error – shown per row, retried
later (`RidPoolLow` warned before).

**Entry points** (B4): `ShellViewModel.BulkCreate.cs` (complete in the contract): `BulkCreateCommand(string? parentDn)`
→ `ShowBatchEditorAsync(new BatchEditorRequest([]) { Create = new BulkCreateRequest(ObjectKind.User,
CreateTarget(ObjectKind.User, parentDn)) })`, can-execute `IsConnected`; palette descriptor `new.bulkCreate`
(`L.BulkCreate.Command` *Wiele kont…*, group *Nowy*; decision 1 explains the id). XAML (B4): `MainWindow.xaml` *Akcja →
Nowy* (after `new.contact`, with a separator), `ObjectListView.xaml` *Nowy* submenu (same place), `TreeMenu.NewMenu`
(an item after *Nowy kontakt* passing `container.Dn`). The tree item is enabled whenever connected; the wizard reports
`CannotCreateChild` itself for the chosen kind.

**Smoke, UI tests and screenshot** (B4): `SmokeTest.BulkCreate.cs` `RenderBulkCreateAsync` – disconnected, no
directory: opens the batch editor in create mode, loads the small synthetic TSV of
`src/AdMng.App/BulkCreate/BulkCreateSamples.cs` (as `GroupPolicySamples`; `AdMng.App` does not reference
`tests/AdMng.Testing`) through `Source`, maps, renders steps 1–3, then renders the check step and the preview from a
hand-built `BulkCreateComputation` and `ChangePlan` of the same file, without the planner and without a session; no
execution. `tests/AdMng.App.UiTests/BulkCreateWindowTests.cs` (check grid with dynamic editable columns and status
glyphs at 256 columns without binding errors, frozen columns, in-place recompute keeps the edited cell, filter, results
list with masked passwords and copy, entry points via `CommandMenu.Id`; the end-to-end paste → results test is
`[Fact(Skip = "F3 wave 2 merge")]` until the lead merges B3); screenshot **43** `43-bulk-create-check.png`
(*Sprawdzenie* with OK, warning and error rows) in `ScreenshotTestsBulkCreateCheck.cs`.

### 2.8 Data handling (rules 1–6)

* **Rule 1.** The pasted or loaded table (title rows included), the source grid, the computation, the lookup cache
  (taken names, owners, resolved managers and groups, possible existing accounts), the plans, the accumulated results,
  the created-object GUIDs and the created-accounts list live only in the batch editor window's view models and are
  dropped on `Dispose` (window close); a session change ends the GC session and drops the lookups. Nothing goes to
  settings except profiles (2.6: patterns, options, header names under the header rule, the chosen UPN suffix and mail
  domain, portable DNs with their domain name), nothing to temp files (`.xlsx` and CSV files are read into memory),
  nothing to logs: diagnostic log lines carry counts and durations only (*bulk create: 120 rows, 6 lookups, 1.2 s*),
  clipboard reads log the HRESULT only, and every F3 record (Core, Presentation and the settings types), `PatternError`,
  `PowerShellParameter` / `PowerShellCommand` and exception message is value-free. The last wizard state is not saved.
  Files are written only by the operator's export (preview, results) and the PowerShell copy goes to the clipboard on
  command.
* **Rule 2.** Generated passwords exist only in `PlannedChange.Password` of the plans and the `BatchResult`s (the
  accumulated one included), and in `CreatedAccountViewModel.Password` (internal; `ToString` without it); the shared
  password is read from the `PasswordBox`es on the UI thread at plan time (or from the retry boxes), passed as a
  parameter, copied into the plan and cleared – no record, setting or log holds it. Machine passwords of computers are
  never shown, copied or exported. Audit: the reset is recorded without a value; PowerShell: `Read-Host
  -AsSecureString`; copy: `SetSecret` only; export and *Kopiuj tabelę z hasłami* only after the warning; closing with
  unsaved generated passwords asks. Password-like columns are never mapped, never reachable from patterns, masked in
  samples and cells and copied as the mask. `Dispose` clears plans, results and created accounts (as the batch editor
  does today).
* **Rule 3.** DNs: `DistinguishedName.Parse(container).Child("CN", cn)`; filters: `LdapFilter.Equal`, `StartsWith`,
  `EndsWith`, `Or`, `And`, `BitAnd` only – pasted cells, rendered values and prefixes are never concatenated into a
  filter or DN. PowerShell literals go through `Quote`.
* **Rule 4.** Every read of `ComputeAsync`, `ReadCreatedAsync` and `PlanUndoAsync` and every write of the plans go to
  the window session's DC; `plan.Server = session.Info.Server` and the executor refuses another DC. The only other
  session is the read-only GC session to the **same DC host** (port 3268/3269, opened with the window session's in-memory
  credential through the connector, only for uniqueness lookups in a multi-domain forest, closed with the wizard) – no
  other DC is contacted (the RID master and the PDC emulator are only named, never connected). The results say that
  other DCs see the objects after replication.
* **Rule 5.** Every create, password, enable, manager and membership step and every undo goes through `BuildPlan` /
  `BuildRetryPlan` / `PlanUndoAsync` → the batch editor preview → `IChangeCoordinator` → `BatchExecutor`; F3 code never
  calls `AddAsync`, `ModifyAsync`, `DeleteAsync` or `ResetPasswordAsync`. Audit, PowerShell and change notifications come
  from the pipeline.
* **Rule 6.** Parsing, computing, recomputing and planning run on `Task.Run` with a `CancellationToken` and progress;
  the clipboard HTML is requested only when needed; lookups are batched and run at most `BatchMaxParallelism` at a time;
  all grids are virtualized (rows and columns), filled with `ReplaceAll` once and updated in place afterwards; edits are
  debounced. 5 000 rows: compute, plan and preview within the budgets of 2.11, no UI-thread work per row except the
  measured preview build.

### 2.9 Localization

Both `Strings.resx` and `Strings.pl.resx`, one block per owner between comment markers placed **after**
`<!-- P1 certificates on accounts (2.6): end -->` (the last marker today, right before `</root>`), in this order:
`F3 contract – bulk creation shared names (2.9)` (lead, filled), `F3 data, columns and naming rules (2.6)` (B3),
`F3 check, plan and results (2.7)` (B4). P1 agents fill their empty blocks in parallel; a merge conflict at the end of
the file is resolved by keeping both sides. Constants in nested classes of the partial `L`; no format string with more
than 8 placeholders; every value is a valid composite format string (pattern examples with doubled braces, 2.6). Counts
use the *label: count* style (*Wiersze: 120*, *obiekty do utworzenia: 114*), never a number before a Polish noun, so no
plural forms are needed.

| File | Class | Prefix | Contents | Owner |
|---|---|---|---|---|
| `L.BulkCreate.cs` | `L.BulkCreate` | `BulkCreate.` | command, window titles per kind, sub-steps, data source (paste, file, header, header line, Excel row, warnings), columns, rules, numbering panel, tokens and functions help, options, profiles, kinds, single-object button | contract keys (`Command`, `Title.<kind>`, `StepSource`…`StepCheck`), then B3 |
| `L.BulkCreateCheck.cs` | `L.BulkCreateCheck` | `BulkCheck.` | check grid, filters, summary, scope notes, recompute, renumber, change title, typed confirmation, canary stop, undo note, results, password copy, retry password boxes, replication note | B4 |
| contract `BulkCreateTextKeys.cs` | – | – | key families (below) | lead |

**Key families** (`BulkCreateTextKeys`, `src/AdMng.Presentation/BulkCreate/BulkCreateTextKeys.cs`, each with an
`AllKeys()` helper and a completeness test in the contract): `Issue.<BulkCreateIssueCodes>` (every constant, with a
format test that renders each through `DisplayNames.IssueText` with an attribute and its arguments),
`BulkCreate.Title.<ObjectKind>` and `BulkCreate.Kind.<ObjectKind>` (the four kinds), `BulkCreate.Target.<ColumnTargetKind>`,
`BulkCreate.HeaderMode.<HeaderMode>`, `BulkCreate.NameOrder.<NameOrder>`, `BulkCreate.Case.<TextCase>`,
`BulkCreate.SuggestionNote.<SuggestionNote>`, `BulkCreate.Warning.<TableWarningCode>`,
`BulkCreate.PatternError.<PatternErrorCode>` (`{0}` position, `{1}` token, `{2}` suggestion),
`BulkCreate.Function.<name>` (the function descriptions below), `BulkCreate.CounterMode.<CounterMode>`,
`BulkCreate.Collision.<CollisionStrategy>`, `BulkCreate.InitialMode.<InitialPasswordMode>`,
`BulkCreate.Phones.<PhoneFormat>`, `BulkCheck.Status.<BulkRowStatus>`, `BulkCheck.Source.<BulkValueSource>`,
`BulkCheck.Scope.<UniquenessScope>`, `BulkCheck.Filter.<BulkCheckFilter>` – all in the contract block, en + pl. Owners
may reword their families, not remove keys.

Function descriptions (`BulkCreate.Function.*`, shown by *Wstaw pole*):

| Function | Polish description |
|---|---|
| `lower` / `upper` | małe litery / WIELKIE LITERY |
| `title` / `capitalize` | Każde Słowo Wielką Literą / Pierwsza litera wielka |
| `trim` | bez spacji na początku i końcu |
| `nodiacritics` | bez polskich znaków (ą→a, ł→l) |
| `nospaces` / `alnum` | bez spacji / tylko litery i cyfry |
| `sam` | jak nazwa logowania (bez polskich i niedozwolonych znaków), maks. 20 znaków w każdym polu |
| `first:N` / `last:N` | pierwsze N znaków / ostatnie N znaków |
| `replace:a:b` | zamień „a” na „b” |
| `default:tekst` | „tekst”, gdy pole jest puste |

Terminology (EN / PL, from the Polish ADUC dialogs; used across the resx and the user doc):

| English | Polish |
|---|---|
| Create multiple accounts (contacts, groups, computers) / Multiple accounts… / Several at once… | Zakładanie wielu kont (kontaktów, grup, komputerów) / Wiele kont… / Wiele naraz… |
| User logon name / (pre-Windows 2000) | Nazwa logowania użytkownika / (sprzed systemu Windows 2000) |
| Full name / Display name | Pełna nazwa / Nazwa wyświetlana |
| First name / Last name / Initials | Imię / Nazwisko / Inicjały |
| E-mail / Mobile / Telephone number | Adres e-mail / Telefon komórkowy / Numer telefonu |
| Office / Job title / Department / Company / Manager | Biuro / Stanowisko / Dział / Firma / Menedżer (*przełożony* only as a header synonym and in the target *Menedżer (przełożony)*) |
| Country/region / Account expires | Kraj/region / Konto wygasa |
| User must change password at next logon | Użytkownik musi zmienić hasło przy następnym logowaniu |
| Account is disabled / Account expires: end of | Konto jest wyłączone / Konto wygasa: koniec dnia |
| Organizational unit / Container | Jednostka organizacyjna / Kontener (*w jednostce „Nowi pracownicy”*, never *w OU*) |
| Pattern / Counter / Numbering / Naming profile | Wzorzec / Licznik / Numeracja / Profil nazewnictwa |
| Check / Data / Columns / Names and options | Sprawdzenie / Dane / Kolumny / Nazwy i opcje |
| Pattern only / Ignore | Tylko we wzorcach / Pomiń |
| Paste and replace / Default for this container | Wklej i zastąp / Domyślny dla tego kontenera |
| Lowest free number (fills gaps) / Next after the highest | Najniższy wolny numer (uzupełnia luki) / Kolejny po najwyższym istniejącym |

Option labels start with a capital letter (*Generuj osobne hasło dla każdego konta*).

### 2.10 Not in this stage

A target container per row (one container per run, decision 1); passwords taken from a sheet column; handing initial
passwords to managers through U1 messages (decision 6); creating home folders, profiles or mailboxes (Exchange
`msExch*`, `mailNickname`, `targetAddress` – blocked as targets, 2.2); `inetOrgPerson`; *Hasło nigdy nie wygasa* and
other account flags at creation (the batch editor sets them afterwards); a manager who is created in the same batch
(warning `ManagerInBatch`); members of created groups from a column; copying a template account's groups and attributes
(ADUC *Kopiuj…*); sharing profiles between operators (*Kopiuj profil / Wklej profil* as JSON with patterns, options and
portable DNs – later); a random machine password in the K1 *Nowy komputer* dialog (parity with 2.5 – a K1 follow-up after
the Windows pilot); `.ods` files; DN-syntax attributes other than the manager; a candidate picker dialog for ambiguous
managers (the cell drop-down of 2.7 covers it); scheduled or repeated creation. Rows and results are never sent to the
Watchdog.

### 2.11 Test plan

**Core B1** (`tests/AdMng.Core.Tests/Batch/Creation/`): `TableTextReaderTests` on `ExcelSamples` – Polish TSV
(`Telefon komórkowy`, `Imię`, `Nazwisko`, `E-mail`, `Stanowisko`, `Dział`, `Przełożony`; diacritics ą ć ę ł ń ó ś ź ż),
a quoted multi-line address, doubled quotes, Excel's quote rule (`"Społem" PSS` keeps its quotes, an unbalanced
opening quote in row 5 does not swallow rows 6–9, a quoted cell with a tab), Excel error values in both languages
(`FormulaError`), a title row and a blank row above the header (`TitleRowsSkipped`, `Reinterpret` with another header
line), BOM, NBSP, U+202F thousands separators in `employeeNumber` and zero-width spaces, trailing empty columns and
CRLFs, LF-only line endings and a missing final newline, Alt+Enter in name cells (kept as CRLF, rejected by 2.2), an
empty middle row, ragged rows, `4,86E+11` without HTML (warning) and with CF_HTML `x:num` (recovered), `NeedsHtml`, HTML
only (Excel with `<!--StartFragment-->`, LibreOffice, Google Sheets; a `mailto:` link in a name cell stays the name),
a single-column web paste, headerless data (letters A–E, a 5-column paste), English headers, header detection edge
cases, `MaxRows` / `MaxColumns` / `MaxCellLength`, `colspan` / `rowspan`, `Append` by header and by position,
`CountRepeatedRows`, `ReadDelimited` with `;` / `,` / tab and the UTF-16 LE text of *Tekst Unicode (\*.txt)*, `ReadXlsx`
(hidden rows, other sheets; the xlsx builder of `ImportTestFiles`); `CellTextTests` (normalisation, error values,
scientific notation, `SplitFullName` both orders with titles and double-barrelled names, `NormalizePhone` incl. 11
digits starting 48, `0048` and `+48 600-100-200`, `LooksLikePolishMobile`, `ApplyCase` with hyphenated names,
`SplitValues` (`|` only) / `SplitList` / `SplitCommas`); `ColumnSynonymsTests` (every table entry, folding,
kind-dependent entries, exact match only – *komórka organizacyjna* → `department`, *Komórka* holding departments →
`department`, *Telefon* holding mobile numbers → `mobile`, private addresses → `PatternOnly`, content suggestions for a
headerless table, password-like substrings (*Hasło startowe*, *nowe hasło*, *passwd*, *temppassword*) and identifier
headers, LDAP names, no blocked attribute ever suggested); `CreationValuesTests` / `CreationNamesTests` (2.2 table per
kind, blocked attributes incl. `proxyAddresses`, `msExch*`, `lockoutTime`, `c`/`co`/`countryCode`, `SanitizeIdentifier`
never cuts, truncation never for counter values and never for addresses, `$` handling – `PC01` / `PC01$` / `PC01$$`,
users with a trailing `$` –, `.` in a computer name an error, `description` single-valued, CR/LF in single-line
attributes, `AppendDisambiguator`, never for computers); `BulkCreatePowerShellTests` and new cases in
`PowerShellExporterTests` (New-ADUser with Polish values quoted, `-AccountPassword (Read-Host -AsSecureString …)`, the
password string absent, `-Enabled`, `-ChangePasswordAtLogon`, `Set-ADUser -Manager`, hyphenated `-OtherAttributes`
keys, `Add-ADGroupMember`, group and computer forms (no `-AccountPassword` for computers), contact → generic,
`PowerShellParameter.ToString` value-free). **Directory**: `ConnectGlobalCatalogAsync` default throws
`NotSupportedException`; `SessionInfo.IsChannelEncrypted` / `IsGlobalCatalogReady` set by the connector (unit test on
the RootDSE mapping); lab `GlobalCatalogSessionLabTests` (B1, `tests/AdMng.Directory.IntegrationTests/BulkCreate/`): the
GC session of the lab DC opens, finds a seeded user's UPN with base `""` (or the documented fallback), refuses writes by
type (`IDirectoryReader`) – it proves the port and the base only, never the `Forest` scope (Samba has one domain per
forest).

**Core B2** (`tests/AdMng.Core.Tests/Batch/Creation/`, on `FakeDirectoryBuilder.AddBulkCreateSample` with the fake's SAM
rules on): `CreationPatternTests` (grammar, every function, `{{` / `}}`, `\|` / `\}` escapes, `{$column:A}` and headers
with colons and diacritics, password-like columns unreachable by header and letter, `$nr` forms and errors,
`CounterMismatch`, forbidden tokens, `DuplicateRule`, `BlockedAttribute`, cycles, evaluation order, precedence edit >
column > rule and `OverridesColumn`, a prefix with `@` as the whole UPN, `$suffix`, `$mailDomain`, `$parentName`,
`$date` on the fake clock and `InvariantDateNames`, `SamInAddress`, the did-you-mean suggestion, `PatternError` without
any argument text, `SuggestCounter`, the 2.3 examples table verbatim); `CounterAllocationTests` (kd201/kd203/kd205 live
and deleted kd207 → `FillGaps` 202, 204, 206, 208 (207 taken) and with `ReuseRecycleBinNumbers` 202, 204, 206, 207
(warned); `AfterHighest` 208, 209 … and with reuse 206, 207 (warned), 208; `ReleasedValues` keep the pins after an
undo; batch reservations; edited and column values reserved; pins kept after an edit and after an exclusion; per-prefix
sequences; width and step; counter only in UPN; a `mail` collision skips the number; `CounterTooLong` instead of
truncation; `CounterExhausted`; one prefix search per attribute; `PreviewCounterAsync`); `CollisionTests`
(`jan.kowalski` → `jan.kowalski2` with UPN and `mail` following, a contact's address bumps the login root, `cn` *(2)*, a
group *Księgowość* next to an OU *Księgowość* collides on `name`, a deleted *Jan Kowalski* of the target OU found
through `lastKnownParent` → `NameInRecycleBin`, recycled hits ignored, the implicit-UPN clash in both directions,
computers always `MarkError`, column and edited values never changed, both duplicate column rows in error, `MarkError`,
`FrozenValues` turn a new collision into an error without renumbering, `PossibleExistingAccount` by `employeeID`, by
`mail`, by `mobile` and by name, `DuplicateSourceRow` for a repeated paste, `MailDomainNotInForest`,
`NonRoutableMailDomain`, batched lookups of 50); `BulkCreatePlannerTests` (Polish data kept with diacritics in
`givenName` / `sn` / `displayName`, ASCII identifiers; manager by `sAMAccountName`, UPN, `mail`, `displayName`, ambiguous
(two *Piotr Zieliński*, candidates kept), not found, in batch, a group as `managedBy`; groups option and column, the
comma fallback, scope rules (`GroupScopeNotAllowed`), Domain Users skipped (`PrimaryGroupSkipped`); `CannotCreateChild`
via `SetPermissions`; container missing; mandatory and unknown attributes; country by Polish name, English name, alpha-2
and numeric code (`c`, `co`, `countryCode`), unknown country; per-row expiry in three formats and `InvalidDate`; UPN
suffix not in the forest, every crossRef `dnsRoot` accepted; GC reader used and scope reported; single domain →
`ForestSingleDomain`; `PasswordsNeedEncryptedChannel` on an unencrypted fake session; `GeneratedLength` below
`minPwdLength` and a shared password too short or not complex → run errors; `GroupPasswordPolicy`; `RidPoolLow` on a
fake RID set; `JoinOwnerNotTrusted`; `BuildPlan` step order and values per kind (user 514 → reset → 512 / `pwdLastSet`
→ manager → groups; computer 4098 → 120-character reset → 4096; no created UAC with 0x20), disabled and expiration
options, `Enabled` without a password → `EnabledWithoutPassword`, generated passwords unique and free of name tokens,
`PasswordContainsName`, `ObjectPlan` fields, `Server`, error rows kept / excluded rows left out; `BuildRetryPlan` leaves
out succeeded steps, regenerates a failed generated password, takes the new shared password after `PasswordPolicy`,
never repeats a succeeded reset, skips `AlreadyExists` creates with `ValueTakenSinceCheck`; `BulkCreateResults.Merge`
statuses; `PlanUndoAsync` – GUID mismatch dropped, renamed object deleted at its new DN, in-use and children warnings);
`BulkCreatePipelineTests` (the plan through the real `BatchExecutor` on the fake with SAM rules: 50 users created with
UAC 512 and `pwdLastSet` 0; one `PasswordPolicy` failure leaves that account disabled (`PartiallySucceeded`); a duplicate
`sAMAccountName` created meanwhile fails that row only; the canary order (an unencrypted session fails at the first
reset); execute → retry → undo deletes all objects of both executions; audit has Create entries and no password);
`BulkCreatePerformanceTests` (5 000 rows against a fake with 10 000 existing users: counter profile and name-based
profile, each compute < 10 s on CI and within the query budget of 2.4; `BuildPlan` < 2 s). `BulkCreateContractTests`
(contract): a reflection test that every record of `AdMng.Core.Batch.Creation` declares `ToString` and that an instance
filled with sentinel values never contains them. Lab `BulkCreateLabTests` (B2,
`tests/AdMng.Directory.IntegrationTests/BulkCreate/`, TLS session `LabFixture.ConnectAsync(LabSettings.Tls(…))`, own OU
`TestNames.Unique("F3T-…")`, random prefix `"kd" + ComputerLabTests.Suffix()[..4].ToLowerInvariant()`): 20 users with
`{prefix}{$nr:1:2}`, UPN and `mail` from it, Polish given names with diacritics, generated passwords (the lab policy
accepts them), a pre-created `{prefix}03` skipped, a lab group; verify `sAMAccountName`, UPN, `mail`, `givenName`, UAC
512, `pwdLastSet` 0 and membership; a value taken between compute and execution fails that object only (a Samba deviation
in the error code goes to the project README); one group (its scope, and a refused nesting left out by the planner), one
contact, one computer (UAC 4096 after the 4098 → reset → 4096 steps, a single `$`, `cn` = the sAM base); a `SameForAll`
password the lab policy rejects (`PasswordTests.ResetPassword_TooSimple_IsPasswordPolicy` pattern): the canary stops, the
account stays disabled without groups, the retry with a new password succeeds; Samba's checks pinned: duplicate
`sAMAccountName`, UPN uniqueness, the implicit-UPN clash, a double `$`, a second `description` value, `unicodePwd` only
over LDAPS, a PSO on a group, the Recycle Bin (deleted object found through `lastKnownParent`); undo after the retry
deletes all objects. Samba's documented deviation: permissive modify is ignored for `member`.

**Presentation B3** (pl-PL, `tests/AdMng.Presentation.Tests/BulkCreate/`, `B2Harness` with the bulk-create schema and
connector + `AddBulkCreateSample`): paste from `harness.Clipboard.Formatted` (TSV, HTML only, both), append, the
repeated-paste question (*Dołącz mimo to / Zastąp / Anuluj*), *Wklej i zastąp*, header toggle and header line, Excel row
offset, CSV file (cp1250 and UTF-16 via the files fake), xlsx, typed rows and columns, step-1 edits keep the check
edits and pins, password-like column masked, read-only and unmappable, localized warnings; auto-mapping of Polish and
English headers, content-based suggestions (*Telefon* with mobile numbers, *Komórka* with departments, a headerless
5-column paste), `#C` mappings, localized display names, manual `extensionAttribute5` and the fake's custom attribute,
`FullName` order, `Case`, *Format międzynarodowy* offer, blocked attributes not offered; the *Numeracja* panel rewriting
every `{$nr}`, the literal quick fix, the first free number, samples of three rows plus the longest, localized pattern
errors with position and suggestion, token insertion by Polish label, the column-precedence note and `OverridesColumn`,
UPN suffixes from `CN=Partitions`, the default suffix, *UPN = adres e-mail*, mail domain, kind switch, option
validation, group picker (`ScriptedDialogService.PickedObjects`); profiles save / load / delete / default for a container,
preselection order, *(zmieniony)*, *Zapisz* on a built-in, built-ins not stored, `MaxProfiles`, header mappings that
look like data never saved, `GroupDns` dropped in another domain, settings JSON free of *password* / *pwd* / *secret* and
of every sample value; *Wiele naraz…* in the single-object dialog opens the create mode with the same parent and kind;
`BulkCreatePresentationContractTests` (contract): the reflection test of every record of `AdMng.Presentation.BulkCreate`
and the F3 settings types; `CreationNamesParityTests` (contract).

**Presentation B4**: compute and statuses, edits recompute in place and stay marked, manager and groups cells editable,
ambiguous-manager candidates, exclusion, filters incl. *Możliwe duplikaty*, F8, copy of error rows, renumber, scope
notes, GC session awaited before compute and plan, opened only for a multi-domain fake with a ready GC, closed on
dispose and on session end, a late connect disposed; preview builds the plan with `FrozenValues` (shared password read
on the UI thread after the compute and cleared after `BuildPlan`; kept after a cancelled compute; *wpisz hasło ponownie*
after Back), the typed confirmation with `BulkCheck.ConfirmCreate` (`TypedConfirmAnswers`), the canary (an unencrypted
fake session stops after one account, rows not run stay not created), execution through the coordinator,
`CreatedAccounts` masked, `SetSecret` copy (`FakeClipboard`), copy-all only after confirmation and with formula
protection and the source columns, handed-out marks clear the close guard, export only after `ExportPasswordsWarning`,
close guard with unsaved generated passwords, no `ReplacePasswords` prompt on retry, retry of a password-policy failure
runs only `ResetPassword` / `Modify` / groups with a new password (no second Create, `PowerShell` cleared), the retry
password boxes for `SameForAll`, execute → retry → undo deletes every created object and `UndoNote` counts them, undo
→ re-run gives the same numbers, created rows marked after *Wstecz*, audit and `PowerShellHistory` entries; shell:
`new.bulkCreate` in the palette, tree and list menus pass the container; `BulkCreateEndToEndTests.OperatorScenario`
(2.6) and 5 000 rows end to end (paste → compute → plan → preview) with the preview build measured separately (< 3 s);
`Dispose` drops passwords; session ended; every F3 issue code renders in Polish through `IssueText` without an English
fallback.

**App (Windows CI)**: `BulkCreateInputUiTests` (B3), `BulkCreateWindowTests` (B4), `SmokeTest.RenderBulkCreateAsync`
(B4), screenshots **40–42** (B3) and **43** (B4) in classes starting `ScreenshotTests` (CI filter
`FullyQualifiedName~AdMng.App.UiTests.ScreenshotTests`), `XamlSourceTests` (keys and `CommandMenu.Id`s). UI tests that
need F3 data call `context.Directory.AddBulkCreateSample(context.Company)` themselves – `UiTestContext` does not seed it
and its schema has no bulk-create attributes.

**Pilot checklist** (Windows DC, `docs/PILOTAZ.md` §3.1, written in the integration step – what neither the fake nor
Samba can prove): a multi-domain forest (the `Forest` scope on the GC, `CheckedInDomainOnly` without a GC, Windows'
own forest-wide UPN check 0x21C8); RDN-value uniqueness across RDN types; KB5020276 (a reused computer account joined by
a non-admin) and KB5008102 (`$` rules); the computer flow 4098 → reset → 4096 and the K1 dialog's 4096 without a
password; RID pool exhaustion behaviour (`RidPoolLow`); logon through another DC before replication; a delegated OU
admin without the reset right (the canary stops).

### 2.12 Contracts (in the code since the F3 contract commit)

The public types that cross agent boundaries are committed by the lead **before wave 1** as compilable stubs with XML
doc comments (bodies `throw new NotImplementedException("F3 Bn: …")`), except the items marked *done*, *complete* or
*pass-through*; **the code is authoritative**, this table is the map. Implementers may add members and types, never
rename or remove these without a line in 2.15. Every record's value-free `ToString` is in the stubs and pinned by the
reflection tests of `BulkCreateContractTests` (Core) and `BulkCreatePresentationContractTests` (also for records the
implementers add).

| File | Types and members | Implemented by |
|---|---|---|
| `src/AdMng.Core/Batch/Creation/InputTable.cs` | `enum TableSource` (`ClipboardText`, `ClipboardHtml`, `CsvFile`, `XlsxFile`, `Typed`), `enum HeaderMode` (`Auto`, `FirstRowIsHeader`, `NoHeader`), `TableColumn(int Index, string Letter, string Header)` + `Name`, `TableRow(int Number, IReadOnlyList<string> Cells)` + `Cell(int)`, `enum TableWarningCode` (`ScientificNotation`, `RecoveredFromHtml`, `EmptyRowsSkipped`, `RaggedRows`, `DuplicateHeader`, `MergedCells`, `TooManyRows`, `TooManyColumns`, `CellTooLong`, `PasswordLikeColumn`, `OtherSheetsIgnored`, `HiddenRowsIgnored`, `FormulaError`, `TitleRowsSkipped`), `TableWarning(TableWarningCode Code, int? Row = null, int? Column = null, int Count = 1)`, `InputTable(IReadOnlyList<TableColumn> Columns, IReadOnlyList<TableRow> Rows, bool HasHeader, TableSource Source, IReadOnlyList<TableWarning> Warnings)` + `static Empty`, `Cell(int row, int column)`, `FindColumn(string)`, `NextRowNumber`, `HeaderLine`, `TitleRows` | complete |
| `…/Creation/TableTextReader.cs` | `static class TableTextReader` – `MaxRows`, `MaxColumns`, `MaxCellLength`; `ReadClipboard(string? text, byte[]? cfHtml, string? cfHtmlText, HeaderMode header)`, `NeedsHtml(string? text)`, `ReadDelimited(string text, char? delimiter, HeaderMode header, TableSource source = TableSource.CsvFile)`, `ReadHtml(string html, HeaderMode header)`, `ReadXlsx(byte[] content, HeaderMode header)`, `Append(InputTable existing, InputTable added)`, `CountRepeatedRows(InputTable existing, InputTable added)`, `Reinterpret(InputTable table, HeaderMode header, int? headerLine = null)`, `FindHeaderRow(IReadOnlyList<IReadOnlyList<string>> firstRows) → int?`, `ColumnLetter(int index)` (*done*) | B1 |
| `…/Creation/CellText.cs` | `static class CellText` – `Normalize(string)`, `IsSpreadsheetError(string)`, `IsScientificNotation(string)`, `LooksLikeEmail(string)`, `LooksLikePhone(string)`, `LooksLikePolishMobile(string)`, `IsAllCaps(string)`, `ApplyCase(string, TextCase)`, `SplitValues(string)`, `SplitList(string)`, `SplitCommas(string)`, `SplitFullName(string, NameOrder) → (string GivenName, string Surname)`, `NormalizePhone(string, PhoneFormat)` | B1 |
| `…/Creation/ColumnMapping.cs` | `enum ColumnTargetKind` (`Ignore`, `Attribute`, `PatternOnly`, `FullName`, `Manager`, `Groups`, `Country`, `ExpiresAt`), `enum NameOrder` (`GivenNameFirst`, `SurnameFirst`), `enum TextCase` (`AsIs`, `Title`, `Lower`, `Upper`), `enum SuggestionNote` (`None`, `MobileNumbersDetected`, `PhonesWithoutPlus`, `ContentBased`), `ColumnMapping(int Column, ColumnTargetKind Kind, string? Attribute = null, NameOrder Order = NameOrder.GivenNameFirst, TextCase Case = TextCase.AsIs)`, `ColumnSuggestion(ColumnTargetKind Kind, string? Attribute = null, NameOrder Order = NameOrder.GivenNameFirst, SuggestionNote Note = SuggestionNote.None)` | complete |
| `…/Creation/ColumnSynonyms.cs` | `static class ColumnSynonyms` – `Fold(string)`, `Suggest(string header, ObjectKind kind, IReadOnlyCollection<string>? attributeNames = null, IReadOnlyList<string>? samples = null) → ColumnSuggestion?`, `SuggestFromContent(IReadOnlyList<string> samples, ObjectKind kind) → ColumnSuggestion?`, `IsPasswordLike(string)`, `IsPersonalIdentifier(string)`, `Entries` (`IReadOnlyList<ColumnSynonym(string Folded, ColumnSuggestion Target, ObjectKind? OnlyFor)>`) | B1 |
| `…/Creation/SpreadsheetText.cs`, `…/Creation/HtmlTableReader.cs` | internal: `SplitTsv(string)`; `HtmlTableReader.Read(INode root)` | B1 (new) |
| `…/Creation/CreationNames.cs` | `static class CreationNames` – the six limits, `MaxSamAccountName(ObjectKind)`, `IsInvalidSamCharacter(char)`, `ClassChainOf(ObjectKind)`, `StructuralClassOf(ObjectKind)` (*done*); `IsValidSamAccountName(string, ObjectKind)`, `IsValidUpnPrefix(string)`, `IsValidComputerName(string)`, `SanitizeIdentifier(string value, ObjectKind kind, string attribute)`, `AppendDisambiguator(string attribute, string value, int number, int maxLength)` | done / B1 |
| `…/Creation/CreationValues.cs` | `FinishedValue(string Value, bool Sanitized, bool Truncated)`, `CreationFinishOptions(bool SanitizeIdentifiers, bool TruncateNames, PhoneFormat Phones, bool IsCounterValue)`, `static class CreationValues` – `Finish(ObjectKind, string attribute, string raw, CreationFinishOptions)`, `Validate(ObjectKind, string attribute, IReadOnlyList<string> values, ISchemaCatalog)`, `IsBlockedAttribute(string, ObjectKind, ISchemaCatalog)`, `IsPhoneAttribute(string)`, `IsMultiLine(string)` | pass-through / B1 |
| `…/Creation/BulkCreatePowerShell.cs` | `static PowerShellEquivalent? For(ObjectPlan objectPlan)` (contract: returns null) | B1 |
| `src/AdMng.Core/Batch/PowerShellEquivalent.cs` | `PowerShellValueKind.SecurePrompt`, `PowerShellValueKind.AttributeTable`, `PowerShellParameter.SecurePrompt(string, string)`, `PowerShellParameter.Table(string, IReadOnlyList<AttributeChange>)`, `PowerShellParameter.Attributes`, value-free `PowerShellParameter.ToString` / `PowerShellCommand.ToString` (enum values, factories and `ToString` *done*; exporter branches B1) | done / B1 |
| `src/AdMng.Core/Directory/IDirectorySession.cs`, `Connection.cs` | `IDirectoryConnector.ConnectGlobalCatalogAsync(IDirectorySession current, CancellationToken cancellationToken)` – default `NotSupportedException`; `SessionInfo.IsChannelEncrypted`, `SessionInfo.IsGlobalCatalogReady` (set by `LdapDirectoryConnector`, RootDSE `isGlobalCatalogReady` read) | done (GC impl B1 in `LdapDirectoryConnector`) |
| `src/AdMng.Core/Batch/Planning/PlanValidation.cs` | `ValidateValue(AttributeDefinition, string)` `private` → `public` inside the internal class (behaviour-neutral) | done |
| `…/Creation/CreationPatterns.cs` | `enum PatternErrorCode` (`Syntax`, `UnknownColumn`, `InvalidCounter`, `CounterMismatch`, `ForbiddenToken`, `UnknownAttribute`, `BlockedAttribute`, `DuplicateRule`, `Cycle`, `InvariantDateNames`, `SamInAddress`), `PatternError(PatternErrorCode Code, string Attribute, int Position, string? Token = null, string? Suggestion = null)` + `IsError`, `readonly record struct CounterSpec(int Start, int Width, int Step)` + `Default`, `Format(int)`, `TryParse(string?, out CounterSpec, out string?)`, `PatternScope(ObjectKind Kind, IReadOnlyList<TableColumn> Columns, IReadOnlyCollection<string> ProvidedAttributes, ISchemaCatalog Schema)`, `sealed record PatternRow` (required `Func<string, IReadOnlyList<string>> Attribute`, required `Func<string, string> Column`, `string? Number`, `string? UpnSuffix`, `string? MailDomain`, `string Domain`, `string ContainerDn`, `DateTimeOffset Now`), `sealed class CreationPatternSet` – `static Compile(IReadOnlyList<AttributeRule>, PatternScope)`, `static SuggestCounter(string)`, `Errors`, `IsValid`, `Counter`, `EvaluationOrder`, `CounterAttributes`, `DependsOn(string)`, `Render(string attribute, PatternRow row)`; `static class CreationPatternTokens` – `Number = "nr"`, `Column = "column"`, `Suffix = "suffix"`, `MailDomain = "mailDomain"`, `Forbidden`, `Extensions(IReadOnlyList<TableColumn>)` | B2 |
| `…/Creation/BulkCreateModel.cs` | enums `CounterMode` (`FillGaps`, `AfterHighest`), `CollisionStrategy` (`AppendNumber`, `MarkError`), `InitialPasswordMode` (`Generate`, `SameForAll`, `None`), `PhoneFormat` (`AsIs`, `International`), `BulkRowStatus` (`Ok`, `Warning`, `Error`, `Excluded`, `Created`), `BulkValueSource` (`Column`, `Edited`, `Pattern`, `Fixed`, `Option`, `Frozen`), `UniquenessScope` (`Domain`, `ForestSingleDomain`, `Forest`); records `AttributeRule(string Attribute, string Pattern, bool OverridesColumn = false)`; `BulkCreateRules` (`Kind` = User, `Attributes`, `UpnSuffix`, `MailDomain`, `CounterMode`, `ReuseRecycleBinNumbers` = false, `Collisions`, `SanitizeIdentifiers` = true, `TruncateNames` = true, `AddPrimarySmtp`, `Phones`); `BulkAccountOptions` (`PasswordMode` = Generate, `GeneratedLength` = 16, `MustChangeAtLogon` = true, `Enabled` = true, `DateTimeOffset? ExpiresAt`, `GroupDns`, `GroupScope` = Global, `SecurityGroup` = true, `JoinerSid`, `JoinerName`, `MatchAttributes` (null = default order, empty = off)); `CountryValue(string Code, string Name, int NumericCode)`; `BulkCreateInput` (required `ContainerDn`, `Table`, `Mappings`, `Rules`, `Options`; `Edits` (`IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>>`), `ExcludedRows`, `CreatedRows` (`IReadOnlyDictionary<int, string>`), `PinnedNumbers` (`IReadOnlyDictionary<int, int>`), `FrozenValues` (`IReadOnlyDictionary<int, IReadOnlyList<BulkValue>>?`), `ReleasedValues` (`IReadOnlyCollection<string>`), `UpnSuffixes`, `Countries` (`IReadOnlyDictionary<string, CountryValue>`, folded key), `MaxParallelLookups` = 4); `BulkValue(string Attribute, IReadOnlyList<string> Values, BulkValueSource Source)` + `Adjusted`, `Sanitized`, `Truncated`; `BulkCreateRow` (required `Number`, `Status`, `Values`; `DistinguishedName`, `int? CounterNumber`, `ManagerDn`, `ManagerCandidates`, `GroupDns`, `MatchDns`, `Issues`, `Value(string)`); `DomainPasswordPolicy(int MinimumLength, bool ComplexityRequired)`; `BulkCreateComputation` (required `Input`, `Rows`, `Attributes`, `Server`; `Scope`, `Issues`, `PasswordPolicy`, `ForestDomains`, `IsPdcEmulator`, `ComputedAt`, `HasRunErrors`, `Count(BulkRowStatus)`, `Numbers`); `CounterPreview(string? Prefix, int? FirstFree, int? Highest)`; `CreatedObject(int Row, string DistinguishedName, ObjectKind Kind, Guid? ObjectGuid, DateTimeOffset? WhenCreated, bool MustChangeSet)` | complete |
| `…/Creation/BulkCreateIssueCodes.cs` | the `const string` codes of 2.5 and the set `AttributeAndArguments` | complete |
| `…/Creation/BulkCreateLookups.cs` | `sealed class BulkCreateLookups(IDirectoryReader? forestReader = null)` – `ForestReader`, `Refresh()`, `ToString()` (*done*); caches | done / B2 |
| `…/Creation/IBulkCreatePlanner.cs`, `BulkCreatePlanner.cs` | `interface IBulkCreatePlanner` (the six members of 2.5); `sealed class BulkCreatePlanner(PasswordGenerator passwords, TimeProvider time)` | done / B2 |
| `…/Creation/BulkCreateResults.cs` | `static BatchResult Merge(BatchResult? accumulated, BatchResult next)` | B2 |
| `…/Creation/ForestScope.cs` | `static Task<ForestInfo> ReadAsync(IDirectoryReader reader, CancellationToken cancellationToken)`, `ForestInfo(int DomainCount, IReadOnlyList<string> DomainDnsNames)` | B2 |
| `src/AdMng.Presentation/Localization/DisplayNames.cs` | `IssueText`: the `BulkCreateIssueCodes.AttributeAndArguments` branch (`{0}` attribute display name, `{1}…` arguments) | done |
| `src/AdMng.Presentation/Services/IDialogService.cs` | `sealed record BulkCreateRequest(ObjectKind Kind, string? ParentDn)`; `BatchEditorRequest.Create` (`BulkCreateRequest?`) | done |
| `src/AdMng.Presentation/Services/PlatformServices.cs` | `IClipboardService.ReadTable() => ReadFormatted()` (default member) | done (App impl B3) |
| `src/AdMng.Presentation/BatchEditing/BatchEngines.cs` | optional last parameter `IBulkCreatePlanner? bulkCreate = null` (fallback `new BulkCreatePlanner(new PasswordGenerator(), TimeProvider.System)`), property `BulkCreate` | done |
| `src/AdMng.Presentation/BatchEditing/BatchEditorViewModel.cs` | `BatchSourceMode.Create`; constructor `(DialogServices, BatchEngines, BatchEditorRequest, IDirectoryConnector? connector = null)` with the create branch (creates `BulkCreate`, subscribes `BulkCreate.Changed` → `PreviewCommand.NotifyCanExecuteChanged`); `IsCreateMode`, `IsObjectsMode`, `BulkCreate` (`BulkCreateViewModel?`), `CreatedAccounts` (`BulkObservableCollection<CreatedAccountViewModel>`), `UndoNote` (stub: null), `MarkPasswordsHandedOut()` (internal; stub); `InitializeAsync` create branch (`BulkCreate.InitializeAsync()`), `OnSessionEnded` create branch (`BulkCreate.OnSessionEnded()`), `Dispose` (unsubscribes, disposes `BulkCreate`, clears `CreatedAccounts`) | done (rest of create mode B4, 2.13 anchors) |
| `src/AdMng.Presentation/Dialogs/DialogViewModelFactory.cs` | optional `IDirectoryConnector? connector` constructor parameter; `CreateBatchEditor(request) => new(services, engines, request, connector)` | done |
| `src/AdMng.Presentation/BulkCreate/BulkCreateViewModel.cs` | `enum BulkCreateStep`; `BulkCreateViewModel(DialogServices, BatchEngines, BulkCreateRequest, Func<IDirectorySession?>, IDirectoryConnector?)` with the host members of 2.6, `IncludedRowCount`, `IsReadyToPlan`, `BuildInput()`, `Changed`, `InitializeAsync()`, `OnSessionEnded()`, `Dispose` | complete |
| `src/AdMng.Presentation/BulkCreate/BulkCreateViewModel.Plan.cs` | `Task<ChangePlan> PlanAsync(IProgress<int>, CancellationToken)`, `Task<ChangePlan> PlanRetryAsync(CancellationToken)`, `Task<ChangePlan?> PlanUndoAsync(CancellationToken)`, `OnExecuted(BatchResult)`, `OnUndone(BatchResult)`, `Accumulated`, `CreatedObjects`, `PasswordModeAtPlan`, `HasUnhandedPasswords` | B4 |
| `src/AdMng.Presentation/BulkCreate/BulkSourceViewModel.cs`, `BulkColumnsViewModel.cs`, `BulkRulesViewModel.cs` | the members of 2.6 (commands stubbed, outputs `Table`, `ExcelRowOffset`, `ToMappings()`, `ToRules()`, `ToOptions()`, `ReadSharedPassword()`, `NeedsSharedPassword`, `InitializeAsync()`), row view models | B3 |
| `src/AdMng.Presentation/BulkCreate/BulkCheckViewModel.cs`, `CreatedAccountViewModel.cs` | the members of 2.7, `enum BulkCheckFilter` (`All`, `Ok`, `Warnings`, `Errors`, `PossibleDuplicates`, `Excluded`, `Created`), `CheckColumnDescriptor`, `OnSessionEnded()` | B4 |
| `src/AdMng.Presentation/BulkCreate/BulkCreateTextKeys.cs` + the contract resx block + `L.BulkCreate.cs` (contract keys) | 2.9 | done |
| `src/AdMng.Presentation/Settings/BulkCreateSettings.cs` + `AppSettings.BulkCreate` + its `Normalize` line | `BulkCreateSettings` (`MaxProfiles`, `Profiles`, `LastProfileIds`, `Normalize()`), `BulkCreateProfile` (2.6 fields), `BulkCreateRuleSetting(string Attribute, string Pattern, bool OverridesColumn)`, `BulkCreateHeaderMapping(string Header, ColumnTargetKind Target, string? Attribute, NameOrder Order, TextCase Case)` | complete |
| `src/AdMng.Presentation/Shell/ShellViewModel.BulkCreate.cs` | `BulkCreateCommand`, `BulkCreateCommandDescriptors()` | complete |
| `src/AdMng.App/BulkCreate/BulkCreateView.xaml(.cs)` and placeholders `BulkSourceView`, `BulkColumnsView`, `BulkRulesView`, `BulkCheckView`, `BulkResultsView` | host complete; placeholders replaced by their owners | done / B3 / B4 |
| `src/AdMng.App/SmokeTest.BulkCreate.cs` | `RenderBulkCreateAsync(MainWindow, IServiceProvider)` (stub), called from `RenderWindowsAsync` | B4 |

### 2.13 Work split and file ownership

**Waves.** Wave 1 (parallel worktrees from the branch with the contract commit): **B1** table input, cell text,
synonyms, value rules and names, PowerShell equivalent, GC session; **B2** pattern language, counters, uniqueness,
references, planner, retry and undo plans, lab create test. Wave 2 (parallel, from the merged wave 1): **B3** steps 1–3,
profiles, clipboard implementation, single-object button, screenshots 40–42; **B4** check step, batch-editor create
mode, canary, accumulated results, retry, undo, results, entry points, smoke test, UI tests, screenshot 43. Docs
(`docs/ZAKLADANIE-KONT.md` modelled on `docs/KOMPUTERY.md`; README *Faza 3* status and the 40–43 screenshot paragraph;
`docs/ROADMAP.md` link and status – the README anchor changes with the heading; `docs/PILOTAZ.md` §3.1 scenario and the
pilot checklist of 2.11; `docs/ARCHITECTURE.md` *Potok zmian*; the CLAUDE.md layout notes if needed) are the integration
step after wave 2 (lead), from the as-built notes.

**The contract commit (lead)** holds everything marked *done*, *complete* or *pass-through* in 2.12 and every stub, plus:
the shared test files `tests/AdMng.Testing/FakeDirectoryBuilder.BulkCreate.cs` (`AddBulkCreateSample(SampleCompany,
bool samRules = true)` → `BulkCreateSample`: OU *Nowi pracownicy* under *Pracownicy* with a child OU *Księgowość*; live
users kd201, kd203, kd205 and a deleted kd207 (`lastKnownParent` = the OU); a deleted *Jan Kowalski* from the OU; a
contact with `jan.kowalski@contoso.test` in `proxyAddresses`; two *Piotr Zieliński* (ambiguous manager) and *Ewa
Wiśniewska* (`ewa.wisniewska@contoso.test`, `employeeID` E-1001, `mobile` +48 600 100 200 – the possible existing
account); group *GG_Nowi* (Global security), a Domain Local and a Universal group, a group with `msDS-PSOApplied`;
OU *Zablokowana* where `user` cannot be created (`SetPermissions`); `uPNSuffixes` `contoso.com`, `firma.pl` and the
domain's crossRef (`systemFlags` 3) in `CN=Partitions`; `minPwdLength` 7 and `pwdProperties` 1 on the domain object; a RID
set on the DC object), the opt-in **SAM rules** of `FakeDirectorySession` (`SamRules`, off by default, switched on by
`AddBulkCreateSample`: duplicate `sAMAccountName` in the domain → 68 / 0x524 on add and modify; duplicate UPN → 19 /
0x21C8; an RDN value already used by a sibling of another RDN type → 68; an enabled user without a password (UAC
without 0x2 and 0x20, no `unicodePwd`) → 53 / 0x52D; a computer `sAMAccountName` without exactly one trailing `$` → 19;
more than one `description` value on a user, group or computer → 19; and `FakeDirectorySession.IsChannelEncrypted`
(default true) – false refuses `ResetPasswordAsync` and `unicodePwd` with `StrongAuthRequired`, and is reported in
`Info.IsChannelEncrypted`), `tests/AdMng.Testing/FakeGlobalCatalogReader.cs` (a root-base reader over several fake
domains, `Info.IsGlobalCatalog = true`), `tests/AdMng.Testing/FakeSchema.BulkCreate.cs` (`CreateBulkCreateAttributes`,
`ExtendForBulkCreate`: `extensionAttribute1–15`, `ipPhone`, `pager`, `otherMobile`, `middleName`, `msDS-PSOApplied`,
`msDS-LastKnownRDN`, `lastKnownParent` where missing, and a custom `contosoBadgeNumber` in the `user` MayContain –
**opt-in**: `FakeSchema.Create(schemaNamingContext, bulkCreate: false)`, so the attribute lists P1's W5–W7 tests and
screenshots pin do not change), `tests/AdMng.Testing/BulkCreate/ExcelSamples.cs` (hand-written synthetic TSV incl.
Excel's quote cases, error values and a title row, CF_HTML bytes with `x:num`, LibreOffice and Google Sheets HTML,
headerless and English samples, a cp1250 CSV and a UTF-16 LE text file, `Rows(int count)` generating Polish names with
diacritics, `MinimalXlsx(rows)`), `ConnectGlobalCatalogAsync` on the Presentation `FakeDirectoryConnector`
(`tests/AdMng.Presentation.Tests/TestDoubles/Infrastructure.cs`) and the UI-test connector (with a call counter and a
disposed flag), `B2Harness` passing `new BulkCreatePlanner(new PasswordGenerator(), Time)` to `BatchEngines`, its
connector to `DialogViewModelFactory` and an optional `bulkCreateSchema` constructor parameter, both
`BulkCreateContractTests` with the reflection checks, `CreationNamesParityTests`, the `BulkCreateTextKeys` completeness
and format tests, `IBulkCreatePlanner` in `ServiceRegistrationTests` and `HostTests`, the `FakeSchema.cs` lines, the
`SmokeTest.cs` call and **all three resx marker pairs** (2.9). It runs the whole Core, Presentation and UI suites once.
Tests written in wave 2 against wave-1 behaviour that is not merged yet are `[Fact(Skip = "F3 wave 1 merge")]`; B4's
end-to-end UI test waits for B3 (`"F3 wave 2 merge"`). The lead removes the skips when merging. When the contract commit
lands, the lead appends a line to P1's as-built section (p-certificate-services.md 2.14) for the F3 lines in
`ShellViewModel.Palette.cs`, which P1's 2.12 lists as "done by the contract commit; nobody else".

**New files (exclusive).** A file the contract created as a stub belongs to the named agent from then on (marked
*bodies*); the agent keeps its public signatures.

| Owner | Files |
|---|---|
| B1 | `src/AdMng.Core/Batch/Creation/{TableTextReader,CellText,ColumnSynonyms,CreationValues,BulkCreatePowerShell}.cs` (bodies), `CreationNames.cs` (B1 members), new `SpreadsheetText.cs`, `HtmlTableReader.cs`; `tests/AdMng.Core.Tests/Batch/Creation/{TableTextReaderTests,SpreadsheetTextTests,HtmlTableReaderTests,CellTextTests,ColumnSynonymsTests,CreationValuesTests,CreationNamesTests,BulkCreatePowerShellTests}.cs`; `tests/AdMng.Directory.IntegrationTests/BulkCreate/GlobalCatalogSessionLabTests.cs` |
| B2 | `src/AdMng.Core/Batch/Creation/{CreationPatterns,ForestScope,BulkCreatePlanner,BulkCreateResults}.cs` (bodies; + `BulkCreatePlanner.*.cs`, e.g. `.Counters`, `.Uniqueness`, `.References`, `.Plan`, `.Retry`, `.Undo`), `BulkCreateLookups.cs` (caches); `tests/AdMng.Core.Tests/Batch/Creation/{CreationPatternTests,CounterAllocationTests,CollisionTests,BulkCreatePlannerTests,BulkCreateRetryUndoTests,BulkCreatePipelineTests,BulkCreatePerformanceTests}.cs`; `tests/AdMng.Directory.IntegrationTests/BulkCreate/BulkCreateLabTests.cs` |
| B3 | `src/AdMng.Presentation/BulkCreate/{BulkSourceViewModel,BulkColumnsViewModel,BulkRulesViewModel,BulkCreateProfileStore,BulkCreateCountries}.cs` (+ their row view models); `L.BulkCreate.cs` (beyond the contract keys); `src/AdMng.App/BulkCreate/{BulkSourceView,BulkColumnsView,BulkRulesView}.xaml(.cs)` (bodies); `tests/AdMng.Presentation.Tests/BulkCreate/{BulkSourceTests,BulkColumnsTests,BulkRulesTests,BulkCreateProfileTests}.cs`; `tests/AdMng.App.UiTests/{BulkCreateInputUiTests,ScreenshotTestsBulkCreateInput}.cs` |
| B4 | `src/AdMng.Presentation/BulkCreate/{BulkCreateViewModel.Plan,BulkCheckViewModel,CheckRowViewModel,CreatedAccountViewModel}.cs`; `src/AdMng.Presentation/Localization/L.BulkCreateCheck.cs`; `src/AdMng.App/BulkCreate/{BulkCheckView,BulkResultsView}.xaml(.cs)` (bodies), new `src/AdMng.App/BulkCreate/BulkCreateSamples.cs`; `src/AdMng.App/SmokeTest.BulkCreate.cs` (body); `tests/AdMng.Presentation.Tests/BulkCreate/{BulkCheckTests,BulkCreateBatchEditorTests,BulkCreateEntryPointTests,BulkCreateEndToEndTests}.cs`; `tests/AdMng.App.UiTests/{BulkCreateWindowTests,ScreenshotTestsBulkCreateCheck}.cs` |

**Shared files – changes only at these anchors** (both sides kept when two owners insert at the same anchor). P1's
agents edit, at their own anchors, `DirectoryAclGrantSource.cs`, `AclObjectTypes.cs`, `FlagNames.cs`,
`NavigationTreeViewModel.cs`, `SettingsViewModel.cs`, `SettingsWindow.xaml`, `PropertyPageFactory.cs`,
`AttributeEditorPage.cs`, `PropertiesWindow.xaml`, `PropertySheetTests.cs`, `HostTests.cs` (W7: `SharedApp`, PKI
asserts), `AppHost.cs` (W7), `MainWindow.xaml` (W5/W6), `TreeMenu.cs` (W5) and their resx blocks – F3 touches the last
five only at the anchors below and none of the others (no Settings-window section: profiles are managed inside the
wizard).

| File | Owner: change at anchor |
|---|---|
| `src/AdMng.Core/Batch/PowerShellExporter.cs` | B1: the `SecurePrompt` and `AttributeTable` cases in the `switch (parameter.Kind)` of `WriteEquivalent` (and the extra arguments that method needs for `Entry`); nothing else |
| `src/AdMng.Core/Directory/Connection.cs` | contract: `IsChannelEncrypted` and `IsGlobalCatalogReady` after `IsGlobalCatalog`; nobody else |
| `src/AdMng.Directory/LdapDirectoryConnector.cs` | contract: the two `SessionInfo` initializers and `isGlobalCatalogReady` in the RootDSE read; B1: `ConnectGlobalCatalogAsync` right after `ConnectToServerAsync` |
| `src/AdMng.Presentation/Localization/DisplayNames.cs` | contract: the `AttributeAndArguments` branch at the top of `IssueText`; nobody else |
| `src/AdMng.Presentation/BatchEditing/BatchEditorViewModel.cs` | contract: the constructor's create branch, `InitializeAsync` / `OnSessionEnded` / `Dispose` create lines, `MarkPasswordsHandedOut` stub. B4: create-mode branches in `WindowTitle`, `ChangeTitle`, `PreviewAsync` (before the CSV branch), `CanPreview`, `ExecuteAsync` (no `ReplacePasswords` prompt, `BulkCheck.ConfirmCreate`, the canary split), `GeneratesPasswords`, `HasUnsavedGeneratedPasswords`, `RetryFailedAsync` (before the CSV branch), `Undo`, `ShowResults` (after `ResultSummary`; `OnExecuted` / `OnUndone`), `UndoNote` body, `MarkPasswordsHandedOut` body, `PlanSource.Create`; only if the preview budget is missed: the row building of `ShowPreview` / `ShowResults` moved into `RunPlanningAsync`'s `Task.Run` (lead-agreed, all modes); nothing else in the objects or CSV paths |
| `src/AdMng.App/Views/BatchEditorWindow.xaml(.cs)` | contract: the create-mode `ContentControl` in the Edit area and the `IsObjectsMode` visibility triggers; B4: the create-mode step labels at `<!-- F3 step labels (B4) -->`, the results panel at `<!-- F3 bulk create results (B4) -->`, the undo note at `<!-- F3 undo note (B4) -->` |
| `src/AdMng.Presentation/Dialogs/NewObjectViewModel.cs`, `src/AdMng.App/Views/NewObjectWindow.xaml` | B3: `OpenBulkCreateCommand` after `CancelCommand`; the *Wiele naraz…* button left of OK |
| `src/AdMng.App/Services/ClipboardService.cs`, `ClipboardHtmlReader.cs` | B3: `ReadTable` after `ReadFormatted`; a text-first read with the HTML read on demand next to `Read()` |
| `src/AdMng.App/Views/MainWindow.xaml`, `ObjectListView.xaml` | B4: `<MenuItem b:CommandMenu.Id="new.bulkCreate" />` (after a `<Separator />`) after `<MenuItem b:CommandMenu.Id="new.contact" />` of the *Nowy* menus – not near P1's `<!-- P1 PKI … -->` anchors |
| `src/AdMng.App/Views/TreeMenu.cs` | B4: one `NewItem`-style line after the `NewContact` line of `NewMenu` (P1 W5 adds its `case PkiNodeViewModel:` elsewhere) |
| `src/AdMng.Presentation/Shell/ShellViewModel.Palette.cs` | contract: `.. BulkCreateCommandDescriptors(),` after `.. CorrespondenceCommandDescriptors(),`; nobody else (line in P1's as-built, above) |
| `src/AdMng.Presentation/ServiceCollectionExtensions.cs` / `src/AdMng.App/AppHost.cs` | contract: `TryAddSingleton<IBulkCreatePlanner, BulkCreatePlanner>()` / `AddSingleton<IBulkCreatePlanner, BulkCreatePlanner>()` after the `ICsvImportPlanner` line; nobody else |
| `tests/AdMng.App.UiTests/HostTests.cs` | contract: `Assert.NotNull(services.GetRequiredService<IBulkCreatePlanner>());` after the `ICsvImportPlanner` assert – away from W7's `SharedApp` and PKI edits; nobody else |
| `src/AdMng.Presentation/Settings/AppSettings.cs` | contract: `BulkCreate` after `Pki`; `BulkCreate ??= new(); BulkCreate.Normalize();` after `Pki.Normalize();` |
| `src/AdMng.App/SmokeTest.cs` | contract: `await RenderBulkCreateAsync(main, services).ConfigureAwait(true);` after the `RenderPkiSecurityAsync` line |
| `tests/AdMng.Testing/FakeSchema.cs` | contract: the optional `bulkCreate` parameter of `Create`; `if (bulkCreate) { attributes.AddRange(CreateBulkCreateAttributes()); }` after the `CreatePublicKeyAttributes` line, `if (bulkCreate) { ExtendForBulkCreate(classes, schemaNamingContext); }` after `ExtendForPublicKey(…)` |
| `tests/AdMng.Testing/FakeDirectorySession.cs` | contract: `SamRules` and `IsChannelEncrypted` (properties after `Operations`), their checks at the top of `ApplyAdd`, the `sAMAccountName` / UPN check in the modify path and the encryption check of `ResetPasswordAsync`; nobody else |
| `tests/AdMng.Presentation.Tests/TestDoubles/Infrastructure.cs`, the UI-test connector, `tests/AdMng.Presentation.Tests/Dialogs/B2Harness.cs` | contract: `ConnectGlobalCatalogAsync` after `ConnectAsync`; the planner, connector and `bulkCreateSchema` lines of `B2Harness`; nobody else |
| `src/AdMng.Presentation/Resources/Strings.resx`, `Strings.pl.resx` | each owner only between its own marker pair (2.9) |

**Files nobody changes in F3:** `BatchExecutor.cs`, `RollbackPlanner.cs`, `ChangePlanner.cs`, `ChangePlan.cs` (no new
`IssueCodes` – they are in `BulkCreateIssueCodes`), `PlanValidation.cs` beyond the contract's visibility change,
`DelimitedText.cs`, `HtmlContactReader.cs`, `DocumentParts.cs` (U5's readers; B1 writes its own), `ErrorTranslator.cs`,
`DirectoryException.cs`, `CsvImportPlanner.cs`, `CsvImportViewModel.cs`, `OperationEditors.cs` (no `OperationType` – it
would enter every operation menu, smoke test and the B2 key-family test), `IDialogService` methods,
`ConnectionLifetimes.cs` (no new scoped service), `UiTestContext.cs`, `AddSampleCompany`, `L.cs`,
`Directory.Packages.props`, `AdMng.slnx`, every Watchdog and PKI file, and `CLAUDE.md` during the waves. Any other
change to a file another owner holds needs the lead's agreement and a line in 2.15; a merge conflict outside the anchors
above means a rule of this section was broken.

### 2.14 Review notes

Design review of the first draft (three reviewers: `ad`, `architecture`, `usability`), 2026-11-09. Every finding was
checked against the code (`BatchEditorViewModel`, `RollbackPlanner`, `BatchExecutor`, `DisplayNames.IssueText`,
`TemplateEngine`, `DelimitedText`, `HtmlContactReader`, `LdapDirectorySession` / `LdapDirectoryConnector`,
`FakeDirectorySession`, the test doubles, `NewObjectViewModel`, `ChangePlanner.ApplyResetComputerAccount`,
`BatchReportExporter`, `Countries`) and against AD semantics; findings taken in full are fixed in 1–2.13 without a note.
Taken in part or rejected:

* ad-2 (undo checks what it deletes) – taken, with one limit stated in 2.5: the executor's Delete does not check
  `ObjectPlan.ObjectGuid` and `BatchExecutor.cs` stays unchanged, so the GUID check happens when the undo plan is built;
  an object replaced between the undo preview and its execution is the residual risk.
* ad-3 (systematic failure) – taken as the canary; the alternative `Sequential = true` is rejected (5 000 rows would
  run one at a time).
* ad-4 (d) (PSO of an option group) – in part: a warning (`GroupPasswordPolicy`) and the PSO's limits for the
  shared-password check when readable; the step order (password before groups, as ADUC) is kept, so the stricter policy
  applies from the first password change – with must-change on, at the first logon.
* ad-6 (b) (recycled objects in the sAM search) – in part: `DirectorySearch.IncludeDeleted` sends only Show Deleted,
  which on Windows returns no recycled objects (`IncludeRecycled` adds Show Recycled), so the false warning does not
  arise there; `isRecycled` is still read and recycled hits ignored (the fake and Samba).
* ad-7 (b) (0x21C8) – in part: the results hint is tied to `ConstraintViolation` on a Create in a `CheckedInDomainOnly`
  run, because `ChangeResult` carries the error kind but not the Win32 code and `ErrorTranslator` / `DirectoryException`
  stay unchanged.
* ad-13 (computer password) – the preferred option taken (4098 → random 120-character reset → 4096); the K1 dialog keeps
  4096 without a password until the Windows pilot (2.10, pilot checklist).
* ad-14 (d) (RID pool) – best effort only: an unreadable RID set gives no warning.
* ad-16 (country) – taken through the existing ISO 3166 table `Countries` (alpha-2, numeric, both languages' names) as a
  `Country` target, not through a second table in Core.
* architecture-4 (issue arguments) – in part: `IssueText` changes only for the F3 codes in
  `BulkCreateIssueCodes.AttributeAndArguments`; existing codes render as today, and F3 no longer uses `Duplicate`,
  `DuplicateInBatch` and `InvalidCombination`.
* architecture-6 (re-computation at plan time) – taken as `FrozenValues` (the check's values) rather than a bare
  `Strict` flag, since a strict recompute alone would refuse the check's own disambiguated values.
* architecture-8 (preview build on the UI thread) – in part: the preview build is measured in the end-to-end budget and
  moved into the planning `Task.Run` only if it misses it (a lead-agreed anchor in `RunPlanningAsync`), not
  preemptively; the grid parts (DataGrid with row and column virtualization, in-place recompute) are taken.
* architecture-16 (P1 conflicts) – the P1 note is not edited in this revision: the lead adds the `ShellViewModel.
  Palette.cs` line to P1's as-built when the F3 contract commit lands (2.13); the bulk-create fake schema is opt-in, so no
  re-run of P1's suites is needed for it.
* architecture-18 – the same finding as ad-6 (a); fixed once (2.4).
* usability-3 (counter default) – taken for the built-in *Konta numerowane* (`AfterHighest`, no reuse of Recycle Bin
  numbers); name-based profiles keep `FillGaps`; the operator confirms below.
* usability-7 (phone mapping) – the ADUC tab name in the attribute combo (*– karta Telefony*) is not taken: the combo
  shows the localized label with the LDAP name, as `AttributeOption` does everywhere.
* usability-9 (live sample) – in part: sanitized and truncated values are marked per value (*(zmieniono znaki)*,
  *(skrócono)*), not per character.
* usability-12 (inline fixing) – in part: an ambiguous manager offers its candidates in the cell's drop-down instead of a
  picker dialog (no new `IDialogService` method); everything else taken.
* usability-18 (profiles) – in part: sharing profiles (*Kopiuj / Wklej profil*) moves to 2.10; preselection,
  *(zmieniony)* and *Zapisz* on a built-in are taken.
* usability-22 (acceptance test) – taken; the step screenshots are renumbered 40–43 in step order (P1 holds 36–39).
* Author's palette deviation (`new.bulkCreate` instead of decision 1's `users.bulkCreate`) – kept and stated in decision
  1; the stale sentence of 2.7 is gone.
* Author's open questions: (1) counter default – settled by usability-3 for the built-ins, confirmation asked below;
  (2) and (3) stay open below.

**Operator answers (2026-11-09):**
1. *Konta numerowane* continues after the highest existing number and never reuses numbers of deleted accounts still in
   the AD Recycle Bin (kd201, kd203, kd205 live + kd207 deleted → kd208, kd209 …) – confirmed.
2. For numbered accounts the name shown in AD lists (`cn`) is the person's name (*Jan Kowalski*) – confirmed.
3. Microsoft 365 / Exchange differs between environments: the primary `SMTP:` address in `proxyAddresses` and the
   *UPN = adres e-mail* rule are **profile options, off by default** (as designed).
4. The HR sheets identify a person by **name, surname and e-mail** (no employee number): the duplicate check
   (*Możliwe duplikaty*) uses an employee number only when such a column is mapped, otherwise the e-mail, then given name
   + surname (a warning, never an error).

### 2.15 As built

(Filled by each agent with deviations from 2.1–2.13, then by the lead at the integration step.)

**Contract commit.** Everything of 2.12 is in the code; deviations and additions:

* **Entry point gated.** `new.bulkCreate` is registered in the palette (complete descriptor), but `BulkCreateCommand`
  can execute only when `ShellViewModel.IsBulkCreateAvailable` is true – false in the contract (the steps are stubs), so the
  palette does not offer it. B4 sets it to true with the create mode of the batch editor. `ShellViewModel.BulkCreate.cs`
  also has `BulkCreateStateCommands`, added with one line `.. BulkCreateStateCommands,` to `StateCommands` in
  `ShellViewModel.Commands.cs` (not an anchor of 2.13), so the command follows the connection.
* **Child view models (not specified in 2.12).** Constructors: `BulkSourceViewModel(DialogServices)`,
  `BulkColumnsViewModel(DialogServices, BulkSourceViewModel, Func<IDirectorySession?>, ObjectKind)`,
  `BulkRulesViewModel(DialogServices, BulkColumnsViewModel, IBulkCreatePlanner, Func<IDirectorySession?>, Func<string?>
  containerDn, Func<BulkCreateInput?> input, Func<BulkCreateLookups> lookups, ObjectKind)`; `BulkCheckViewModel` as 2.7.
  The members the complete host calls are no-op stubs (*Contract: nothing*), not throwing ones: `Rules.InitializeAsync`,
  `OnEnteredAsync`, `OnKindChanged`, `OnSessionEnded`, `IsModified`; `Columns.OnKindChanged`; `Check.InitializeAsync`,
  `OnEnteredAsync`, `OnInputChanged` (an earlier step or the container changed), `OnInputReset` (table replaced or kind
  changed: drop edits, pins, exclusions, created marks), `OnSessionEnded`, `Lookups`, `Edits`, `ExcludedRows`
  (`IReadOnlySet<int>`), `PinnedNumbers`, `Computation`, `IsComputing`; every child is `IDisposable`. Step 1 raises
  `Changed` (edit, added row or column, appended paste) and `TableReplaced`. Commands and outputs (`ToMappings`,
  `ToRules`, `ToOptions`, `ReadSharedPassword`) throw `NotImplementedException("F3 Bn: …")`; `BuildInput` reaches them only
  with a non-empty table. `CheckRowViewModel` / `CheckCellViewModel` are in `CheckRowViewModel.cs` (B4),
  `CheckColumnDescriptor` has an `IsFrozen` init property, `CreatedAccountViewModel` an internal `(row, password)`
  constructor, `Mask` and `ClearPassword()`.
* **Host additions.** `BulkCreateViewModel.Title` (*Zakładanie wielu kont – Nowi pracownicy*, `L.BulkCreate.TitleInContainer`;
  the batch editor's `WindowTitle` branch stays B4's), `ObjectKind`, `HasContainer`, async `NextStepCommand` (calls
  `Rules.OnEnteredAsync` / `Check.OnEnteredAsync`), `PreviousStepCommand`; `CreatedRows` and `ReleasedValues` (read by
  `BuildInput`) are internal state of `BulkCreateViewModel.Plan.cs` (B4) with empty defaults, as are `Accumulated`,
  `CreatedObjects`, `PasswordModeAtPlan` and `HasUnhandedPasswords`; the Plan methods throw. B4 drops its state in the
  partial method `OnDisposingPlan()`. `IsReadyToPlan` does not require the shared-password boxes (the plan step says
  *wpisz hasło ponownie*). A kind change asks (`L.BulkCreate.ConfirmKindChange`) only when `Rules.IsModified`.
* **Texts.** Pattern errors take `{0}` position and `{1}` token only; the did-you-mean is `L.BulkCreate.PatternSuggestion`
  (`{0}` suggestion), so no text ends with an empty suggestion. Table warnings: `{0}` row, `{1}` column (header or
  letter), `{2}` count. `BulkCheck.Scope.Forest` takes `{0}` = the DC. Issue placeholders (XML docs of
  `BulkCreateIssueCodes`, pinned by `BulkCreateTextKeysTests.OnlyTheDocumentedCodes_HaveAPlaceholder`): the
  `AttributeAndArguments` codes as 2.5; `Disambiguated`, `NameInRecycleBin`, `ScientificNotation`, `InvalidPhone`,
  `LineBreakInValue`, `AllCapsName` are raised with the attribute and no argument (`{0}` = its display name);
  `ManagerAmbiguous` (count), `GroupNotFound`, `GroupAmbiguous`, `GroupScopeNotAllowed`, `PrimaryGroupSkipped`,
  `GroupPasswordPolicy` (group name), `PasswordTooShortForDomain` (minimum) and `JoinOwnerNotTrusted` (owner) take one
  argument; every other code has no placeholder. `BulkCreateTextKeys` also has `Kinds` and `Functions`.
* **Core additions.** `CreationValues.IsNamedBlockedAttribute` (complete, schema-independent part of
  `IsBlockedAttribute`; B1 keeps it), used by `BulkCreateSettings.Normalize`. `BulkCreateSettings.IsStorableHeader` checks
  the stored (already folded) header with its own copy of the password-like substrings, because `Normalize` runs at
  start-up and must not reach B1 stubs; B3 may switch it to `ColumnSynonyms.IsPasswordLike` with a parity test.
* **XAML.** The step-1 label *Operacje* and `ObjectsEditPanel` now bind `IsObjectsMode`; the anchors are
  `<!-- F3 step labels (B4) -->` after the CSV label of the step strip, `<!-- F3 undo note (B4) -->` at the top of
  `PreviewPanel` and `<!-- F3 bulk create results (B4) -->` at the top of `ResultsPanel`. `BulkCreateView` has its own
  sub-step style (a copy of the window's `StepStyle`) and a `Common.Browse` button for the container.
* **Fakes.** The SAM rules and the channel check are in the new partial `FakeDirectorySession.BulkCreate.cs` (Win32 codes
  as constants `Win32UserExists`, `Win32UpnNotUnique`, `Win32NameExists`, `Win32PasswordRestriction`); `FakeDirectorySession.cs`
  only calls them (top of `ApplyAdd`, the modify path, `ResetPasswordAsync`). The computer `$` and the single
  `description` rules apply on modify too; the enabled-user rule on add only (an enabled user without a password is
  refused as `PasswordPolicy`, 53 / 0x52D). `IsChannelEncrypted` is a view of `Info.IsChannelEncrypted`.
  `AddBulkCreateSample` seeds the watchdog infrastructure when missing (CN=Partitions, PSO container, `minPwdLength` 7,
  `pwdProperties` 1, `CN=RID Manager$` owned by DC01 – the RID master is the window's DC) and clears the seeding
  operations; names: kd201 *Adam Nowicki*, kd203 *Beata Zając*, kd205 *Cezary Mazur*, deleted kd207 *Dorota Krawczyk*
  (UPN and `mail` `@contoso.com`), deleted *Jan Kowalski* (`jkowalski`), the contact *Jan Kowalski (zewnętrzny)* in OU=Firma
  with `mail` / `SMTP:` `jan.kowalski@contoso.com` and `smtp:jan.kowalski@contoso.test`, the second *Piotr Zieliński*
  `pzielinski`, groups `GG_Nowi`, `DL_Drukarki`, `UG_Projekty`, `GG_Uprzywilejowani` with `PSO_Uprzywilejowani`
  (`msDS-PSOApplied` also seeded raw for the default schema), OU *Zablokowana* under *Pracownicy* (only contacts, groups
  and OUs creatable), crossRef `CN=CONTOSO`, RID set 1600–2099 with next 1650. `FakeSchema.BulkCreate.cs` also adds the
  `crossRef` and `rIDSet` classes and `uPNSuffixes`, `dnsRoot`, `nCName`, `rIDSetReferences`, `rIDAllocationPool`,
  `rIDNextRID`, `otherHomePhone`, `postalAddress`. `FakeGlobalCatalogReader` counts its searches (`SearchCount`).
* **Harness and connectors.** `B2Harness.CreateAsync(culture, regional, bulkCreateSchema)`; its `HarnessConnector`
  (GC session, error, gate, count) goes to `DialogViewModelFactory` only in a bulk-create harness, so the account review
  and *Change domain controller* tests keep a factory without a connector. The Presentation `FakeDirectoryConnector` has
  `GlobalCatalogSession`, `GlobalCatalogError`, `GlobalCatalogGate`, `GlobalCatalogConnectCount`; the UI-test connector
  `GlobalCatalogConnectCount` and `LastGlobalCatalogSession` (a `FakeDomainControllerSession`; its `IsDisposed` is the
  disposed flag). The reflection helper is `tests/AdMng.Testing/BulkCreate/BulkCreateRecordSamples.cs` (sentinel values;
  members named `Attribute`, `Code`, `Letter` get constants).
* **Tests of the contract.** `BulkCreateContractTests` and `BulkCreateFakeTests` (Core), `BulkCreatePresentationContractTests`,
  `BulkCreateTextKeysTests`, `CreationNamesParityTests` (Presentation; `IsValidUpnPrefix` parity waits for B1), a RootDSE
  unit test for an absent `isGlobalCatalogReady` in `EntryReaderAndRootDseTests.cs`; `HostTests` and
  `ServiceRegistrationTests` use the qualified `Core.Batch.Creation.IBulkCreatePlanner` (no using changed).
* **Contract review fixes** (second contract commit).
  * *B4 anchor in a lead file:* the `IsBulkCreateAvailable` line of `ShellViewModel.BulkCreate.cs` (B4: `=> true`).
    `BulkCreatePresentationContractTests.Shell_RegistersNewBulkCreate_OfferedOnlyWhenAvailableAndConnected` asserts
    `CanExecute == IsBulkCreateAvailable` while connected and false after a disconnect, so B4 does not edit the test.
  * *Shared anchor in a lead file:* the type branches of `BulkCreateRecordSamples.Value` – B1 and B2 may each add one
    (both sides kept on a merge). Pre-added: every numeric primitive, `decimal`, `Half`, `DateOnly`, `TimeOnly`, `Uri`,
    `CultureInfo`, `TimeProvider`, `Encoding`, string comparers, `object` (a sentinel), arrays, any delegate (a lambda
    returning a filled value) and every interface `FakeDirectorySession` implements (`IDirectoryReader`,
    `IDirectorySession`: a fake session with the bulk-create schema).
  * *Create mode ignores `BatchEditorRequest.Operations`:* the constructor no longer loads them, so neither
    `InitializeAsync` nor `OpenPreview` touches them (as the request's doc says).
  * *Fake channel check as `LdapDirectorySession`:* an unencrypted channel refuses `ResetPasswordAsync`, an Add with a
    non-empty `unicodePwd` or `userPassword` (`CheckChannelOnAdd`, first line of `ApplyAdd`, before the DN checks) and
    any Modify of either (`CheckChannelOnModify`, first line of `ApplyModify`); an empty `unicodePwd` list is not sent
    (`ApplyAdd` skips it, the enabled-user rule then applies). *SAM rules:* the attributes are read case-insensitively as
    `ApplyAdd` stores them (the last non-empty list wins; no dictionary copy, so keys differing only by case no longer
    throw with the rules off) and the requested classes are expanded (`inetOrgPerson` gets the user rules).
  * *`FakeGlobalCatalogReader` as one namespace:* a subtree search runs in the domain that holds the base (the longest
    naming context) and from the naming context of every domain below it, so the forest root's DN reaches the child
    domains; a one-level search adds the naming-context heads directly below the base; a base-scope search or a read of
    `""` is the forest root's RootDSE; a non-empty base in no domain is `NoSuchObject`; DN reads go to the most specific
    domain (before: the first match, i.e. the forest root).
  * *`ExcelSamples`:* `QuirksTsv` has two trailing columns empty in every row and the header (`QuirksColumnCount` 3,
    `QuirksRowCount` 4); the ragged row moved to `RaggedTsv` (5-cell header, data row 2 with 7 cells: `RaggedRowNumber`,
    `RaggedColumnCount`). New: `DuplicateHeaderTsv`, `SensitiveColumnsTsv` (*Hasło startowe*, *PESEL*), `CrOnlyTsv`,
    `MergedAndNestedHtml` (`rowspan`, a nested table, a one-row legend table before the data table), `ProtectedCsvText`
    (`'+48 …`, `'-Kadry`, `Żak-O'Neill` kept); `MinimalXlsx(…, hiddenFirstSheet, numbers)` (a hidden non-empty first
    sheet; numeric cells by reference with a custom number format, `XlsxPhoneFormat`, `XlsxPostalCodeFormat`). `Rows`
    pairs the surname form with the given name's gender (combinations repeat every 400 rows). The `LibreOfficeHtml` doc
    is corrected (the `colspan` is in data row 2; there is no title row). Invisible characters in the shared samples and
    tests are escapes (`"\u00A0"`, `"\u202F"`, `"\u200B"`, `"\uFEFF"`, `'\u3000'`; public `ExcelSamples.Bom`).
  * *Texts:* `Issue.CounterTooLong` is *{0}: prefiks i numer są za długie (limit znaków: {1}) – wartości z licznikiem
    nie są skracane.* (en to match), the *label: count* rule of 2.9.
  * *DI:* `ServiceRegistrationTests` and `HostTests` assert that `BatchEngines.BulkCreate` is the registered
    `IBulkCreatePlanner` (HostTests qualifies `Presentation.BatchEditing.BatchEngines`; no using changed).
* **Not done here.** The line for P1's as-built section (p-certificate-services.md 2.14, `ShellViewModel.Palette.cs`) is
  left to the lead: that note was not to be touched by this commit.

**B1 as built.** Bodies of `TableTextReader`, `CellText`, `ColumnSynonyms`, `CreationNames` (B1 members), `CreationValues`,
`BulkCreatePowerShell`; new `SpreadsheetText.cs`, `HtmlTableReader.cs`; the exporter cases; `ConnectGlobalCatalogAsync`.
No new record type (internal helpers are classes), so `BulkCreateRecordSamples.Value` is unchanged. Deviations and
additions:

* **Added members.** `CreationValues.Validate(kind, attribute, values, schema, PhoneFormat phones, TextCase? columnCase)`:
  `InvalidPhone` needs the run's phone format and `AllCapsName` the column's case, which the four-parameter contract
  member does not have (it is the overload with `AsIs` and *not from a column*; B2 calls the new one with `Rules.Phones`
  and, for a value from a mapped column, the mapping's `Case`). `ConnectionSettings.ForGlobalCatalog()` (Directory,
  internal, next to `ForServer`; not an anchor of 2.13 – one method, no other change). Internal helpers:
  `SpreadsheetText.HasTabInFirstLines` / `ContainsScientificCell`, the `HtmlTable` class, `CellText.TryFormatInternational`
  / `PolishNationalNumber` / `IsPolishNumberWithoutPlus` / `LooksLikeDate` / `LooksLikeNumber`, `ColumnSynonyms.IsKnownHeader`
  / `AttributeAppliesTo`, `CreationNames.IsIdentifier` / `Cut`.
* **GC session host.** Not `current.Info.Server` (2.2) but the host the window's connections are pinned to
  (`LdapConnectionFactory.Host` of its pool): `Info.Server` is the DC's `dnsHostName`, which need not resolve on the client
  – the lab connects by address and the first version failed with `HostNotFound`. It is the same DC. Pinned on Samba 4.19
  (`GlobalCatalogSessionLabTests`, run against the lab): RootDSE `isGlobalCatalogReady: TRUE`, ports 3268 / 3269, and a
  subtree search with base `""` on the GC port finds a seeded user's UPN – the fallback to `RootDomainNamingContext` is not
  needed there (kept for Windows servers that refuse it, as 2.2 says).
* **Header detection.** A candidate needs known headers for at least a third of its non-empty cells, and a frequent Polish
  given name (`GivenNameLists.Polish`) counts as data: a headerless HR sheet whose job titles and departments are header
  words (*Kierownik*, *Biuro*) stays data. `FindHeaderRow`'s index is into its argument (empty lines skipped; the reader
  passes the first ten non-empty lines). `FirstRowIsHeader` in a read is the first non-empty line.
* **Normalisation.** Every Unicode space separator becomes a space (a superset of NBSP, U+2007, U+202F), each line of a
  multi-line cell is trimmed, U+2028 / U+2029 become CRLF. The error-value list also has `#N/D` (without `!`),
  `#ROZLANIE!`, `#GETTING_DATA`, `#BLOCKED!`, `#CONNECT!`, `#FIELD!`, `#UNKNOWN!`, `#BUSY!`, `#EXTERNAL!`.
  `LooksLikePhone` is 7–15 digits and rejects dates. `NormalizePhone(International)` writes Polish fixed lines with the
  two-digit area code (`+48 22 555 01 01`) and mobiles 3-3-3; a 10-digit number without a country is left unchanged.
* **Table shape and warnings.** HTML `rowspan` is a grid: the covered cells of the rows below are empty, so the later cells
  keep their columns (the *MergedAndNested* sample); `MergedCells` is on the row of the merged cell. Warning positions:
  `Row` = `TableRow.Number`, null for a header-line cell; `RaggedRows` – the first longer row, `Column` = the header width,
  `Count` = the added columns; `DuplicateHeader` and `PasswordLikeColumn` – `Column` only; the counted ones – `Count` only.
  Leading empty lines of a headerless table are dropped silently; empty lines between the header and the data count as
  `EmptyRowsSkipped`. `ReadClipboard` uses the HTML table only when the text is empty or not tabular and the table has a
  non-empty cell; recovery needs the same shape and a read below the row limit; an unreadable HTML format is ignored.
  `ReadXlsx`: `HiddenRowsIgnored` counts what the U5 reader reports for the visible sheets (`XlsxReader` unchanged – in the
  usual one-sheet workbook the sheet's own); hidden sheets are not `OtherSheetsIgnored`.
* **Reinterpret / Append.** The line space of `Reinterpret` is `TitleRows`, the header line, the rows (rows skipped while
  reading are not counted: a re-detected header can move up a line); `NoHeader` makes title rows data; the `" (n)"` of a
  renamed duplicate header is removed when the header becomes data; cell warnings move with their cells. `Append`: a header
  of the added table that the existing one cannot use (it has none) becomes the first appended row; warnings of an added
  column that matched an existing one are dropped. `CountRepeatedRows` compares as `Append` places the cells (ordinal).
* **Synonyms.** Kind applicability (users: all but the computers' `location`; contacts: no account attributes; groups:
  `cn`, `sAMAccountName`, `mail`, `displayName`, `description`, `info`, `wWWHomePage`; computers: `cn`, `sAMAccountName`,
  `displayName`, `description`, `location`; every kind `Manager`, `Groups`, *Tylko we wzorcach*); with `attributeNames` an
  attribute target must be in it. *Komórka* without samples is `department`. `PhonesWithoutPlus` when any sample is
  `48xxxxxxxxx`; `MobileNumbersDetected` wins when the content changed the target. Identifier headers: exact folded names
  (`pesel`, `nip`, `nrdowodu`, `dataurodzenia`, `ssn`, `iban` …) and the substrings `pesel`, `dataurodzenia`, `birthdate`.
* **Names.** `SanitizeIdentifier`: a UPN or `mail` part also loses leading dots and runs of dots; a group's logon name loses
  only the forbidden characters (spaces collapsed, ends trimmed). `AppendDisambiguator`: `maxLength` limits the part
  before the first `@` (20 for a login, 64 for a `mail` local part); attributes other than the three identifiers get
  `" (n)"`; impossible limits throw; the caller never passes a computer's names.
* **Finish.** Every single-line rule value has runs of spaces collapsed and is trimmed; `cn` collapses all white space.
  The computer rules apply whatever the options say (B2 may pass a computer's column value with `SanitizeIdentifiers` and
  `TruncateNames` off): `cn` upper case; `sAMAccountName` upper case and `$` when missing, the name cut to 15 before the
  `$` – **except a `IsCounterValue` part, which gets no `$`** (a prefix finished alone would become `PC$`): B2 finishes the
  assembled computer value again or derives it from `cn` as `BuildPlan` does (`Validate` reports a missing `$` as
  `TrailingDollar`, so nothing slips through). A UPN or `mail` without `@` is sanitized as a local part. `homeDrive` upper case.
* **Validate.** Each code once per attribute; messages are value-free (the schema checks of `PlanValidation.ValidateValue`
  are re-worded without the value); an empty value list gives nothing (mandatory attributes are B2's, 2.5); other control
  characters of a multi-line attribute → `InvalidValue`; `homeDrive` accepts a lower-case letter; UPN suffix and mail
  domain are checked syntactically (the forest checks are B2's, 2.4).
* **Blocked attributes.** *No schema* = the schema lacks the structural class of the kind; then the synonym targets and the
  names of 2.2's table that the kind can have (the synonym applicability). DN syntaxes: distinguished name, DN with binary
  or string, OR name, access point; an attribute unknown to the schema is blocked. The allowed sets are cached per schema
  instance and kind (`ConditionalWeakTable`).
* **PowerShell.** `accountExpires` goes to `-OtherAttributes` as its FILETIME (not `-AccountExpirationDate`, which the script
  machine's time zone would shift); one value of an attribute with a cmdlet parameter → the parameter, several →
  `-OtherAttributes`; `-GroupScope` / `-GroupCategory` are quoted literals; a group without a readable `groupType` and any
  step `BuildPlan` does not make → null (generic lines). The exporter's call site passes the object plan and the schema
  to `WriteEquivalent` (the arguments `Entry` needs, 2.13).
* **Tests.** The exporter cases are in `BulkCreatePowerShellTests` (`PowerShellExporterTests` is not a B1 file); the
  `IsValidUpnPrefix` parity test is the new `tests/AdMng.Presentation.Tests/BulkCreate/CreationNamesUpnParityTests.cs`
  next to the contract's `CreationNamesParityTests` (left untouched); the settings unit tests of the GC session are
  `GlobalCatalogSettingsTests` in `GlobalCatalogSessionLabTests.cs`; `BulkCreatePowerShellTests.OperatorScenario_…` (table
  → synonyms → planner → exporter) is `[Fact(Skip = "F3 wave 1 merge")]`.
* **Review of B1** (adversarial review, fixes with tests):
  * *Counter values are finished in parts.* `Finish` with `IsCounterValue` works character by character: the ends of a part
    are neither trimmed nor stripped of dots (a run of spaces at an end becomes one space), the address leading-dot rule
    does not apply and a phone part is not formatted – 2.4 finishes the prefix and the suffix around the number
    separately, so `Praktykant {$nr:1:2}` gave *Praktykant01*, `kd.{$nr:201}` *kd201* and `+48 22 555 01 {$nr:10}`
    *+482255500110*. `Validate` reports white space at either end of a single-line value as `InvalidValue` (a counter rule
    with a stray space keeps it now). `SanitizeIdentifier` itself is unchanged (internal `CreationNames.Sanitize` with
    `edges: false`).
  * *Address parts lose `(` and `)`*: neither `AccountNames.IsValidUpnPrefix` nor `UserMessageRules.TryParseAddress` accepts
    them, so a sanitized UPN or `mail` rule value failed `Validate`.
  * *`x:num` on the HTML-only path*: a single column copied from Excel has no tab, so the HTML table is read – its
    scientific-notation cells now take Excel's `x:num` too (`RecoveredFromHtml`), also for a pasted `.html` file.
  * *`SplitFullName`*: a comma names the order whatever `NameOrder` says (*Kowalski, Jan*, *Nowak-Kowalska, Anna Maria* –
    surname, given names); title parts after the last comma are dropped first (*Kowalski, Jan, dr*; *Jan Kowalski, dr* has
    no comma form), each side loses its own titles. Before, a given-name-first column swapped the parts.
  * *A headerless PESEL column* (80 % valid PESEL numbers: check digit, month, day) gets no content suggestion – 11 digits
    passed `LooksLikePhone`, so it became `telephoneNumber`.

**B2 as built.** Bodies of `CreationPatterns` (`CreationPatternSet`, `CounterSpec`, the creation tokens), `ForestScope`,
`BulkCreateLookups`, `BulkCreateResults` and every partial of `BulkCreatePlanner` (`Compute`, `Counters`, `Uniqueness`,
`References`, `Plan`, `Retry`, `Undo`). No new record type (internal helpers are classes, so the contract's ToString test
does not need them), so `BulkCreateRecordSamples.Value` is unchanged. Deviations and additions:

* **Added members.** `CreationPatternSet`: `DirectReferences`, `HasRule`, `OverridesColumn`, `IsFixed`,
  `UsesCounterDirectly`, `CanRender`, `Render(attribute, row, Action<string>? emptyToken)`. `CounterSpec`: `MaxNumber`
  (999 999 999), `MaxWidth` (9), `MaxStep` (1 000 000), `GridAtOrAbove`, `IsOnGrid`. `CreationPatternTokens`:
  `NumberSentinel` (U+E000, splits a counter value into prefix and suffix) and `ReachableColumnNames`. `ForestInfo`:
  `DomainNamingContexts`, `NamingContextOf`, `IsDomain`; `ForestScope`: `DomainCrossRefFlag`, `PartitionsDn`.
  `BulkCreateLookups.QueryCount` (searches run through the lookups, for the budget tests). `BulkCreatePlanner` constants:
  `ManagerEditKey` (`manager`; `managedBy` is accepted too), `GroupsEditKey` (`memberOf`), `LookupBatchSize` 50,
  `FullListingThreshold` 250, `ManagerBatchSize` 12, `MaxCounterCandidates` 100 000, `MaxDisambiguator` 99,
  `RidPoolCheckRows` 100.
* **Patterns.** `PatternRow.Number` may be a padded number: every `{$nr…}` token re-formats it with its own width
  (`{$nr|lower}-{$nr:1:3}` → `007-007`). Evaluation is topological with fixed ties (`cn`, `givenName`, `sn`, `initials`,
  `displayName`, `sAMAccountName`, `userPrincipalName`, `mail`, then by name). Pattern errors become `TemplateError` run
  issues with the 1-based position and the token; the engine's English texts are never kept.
* **Precedence and edits.** An edit wins, then a non-empty column (unless the rule `OverridesColumn`), then the rule. Column
  cells are not normalized again (the reader did it); edits go through `CellText.Normalize`. The edit keys `c`, `co`,
  `countryCode` are read as a *Country* text, `accountExpires` as a date text (the per-row expiry is written as its
  FILETIME, end of the day).
* **Counters.** One prefix query per (namespace, prefix, suffix) when the prefix has at least two characters; a shorter or
  empty prefix uses windowed equality lookups of 50 candidates, and `AfterHighest` then falls back to `FillGaps` (no
  listing of every name). Pins are reserved first; along the row order a sequence's cursor only moves forward.
* **Uniqueness.** Deleted Objects is addressed as `CN=Deleted Objects,<NC>` (`RecycleBin.GetDeletedObjectsContainer`),
  not the WKGUID form; deleted names are one one-level listing filtered by `lastKnownParent` = the container (not batches
  of 50; `AccessDenied` there is ignored). More than 250 unknown values of one namespace are read with one paged
  `(attr=*)` listing (the existing-account lookups too): the batched lookups stay within the budget of 2.4 but are slower
  than one listing at that size. The implicit UPN (`sAM@domain` and a UPN prefix as a `sAM`) applies to users and
  `inetOrgPerson` only. The values of rows marked created are reserved (all of them, not only the operator's), so a later
  row never gets a created row's login; the other rows reserve their operator values first, excluded rows nothing. An unreadable forest gives scope
  *Domain* with the run warning `CheckedInDomainOnly`; a single-domain forest is `ForestSingleDomain` without a GC reader.
* **Issues.** `GroupScopeNotAllowed` is a warning and the nesting is left out (the row is still created).
  `GroupPasswordPolicy` carries the PSO limits in its `Arguments` (`[name, minimum, "1"/"0" complexity]`; only the name
  is a text placeholder), which `BuildPlan` uses for the shared password; `PasswordContainsName` is a warning. The owner
  name in `DuplicateInDirectory` / `PossibleExistingAccount` is the RDN value (an existing account's `displayName` when
  set); the *same person by name* match is reported on `cn`. A Create that failed with `AlreadyExists` or
  `ConstraintViolation` gets `ValueTakenSinceCheck` in the retry plan.
* **Plan.** Rows with errors get only their Create step (not executable) at `FallbackDn` `CN=#<row>,<container>` when
  they have no DN. Users: Create with UAC 514 → password → Modify UAC 514 → 512 (compare-and-swap) and `pwdLastSet` 0 →
  manager → AddMember; computers: 4098 → join right (`ModifySecurity`) → 120-character machine password → 4098 → 4096.
* **Validate.** The planner calls the four-parameter `CreationValues.Validate`; `Draft.ColumnCase` holds the mapping's case
  for column values. **At the wave-1 merge** the call in `AssembleRow` (`BulkCreatePlanner.Compute.cs`, marked with a
  comment) switches to B1's overload `Validate(_kind, draft.Attribute, draft.Values, _schema, _rules.Phones,
  draft.ColumnCase)` – needed for `InvalidPhone` and `AllCapsName`. B1's `Finish` gives a computer's counter part no `$`;
  the planner re-applies the computer rules to the joined value, so nothing changes there.
* **Tests.** The shared fixture is `tests/AdMng.Core.Tests/Batch/Creation/BulkCreateTestBed.cs` (headerless tables, so
  the B1 stubs are not reached). 17 tests that need B1's bodies are `[Fact(Skip = BulkCreateTestBed.Wave1)]` (the
  constant is `"F3 wave 1 merge"`): 3 in `CreationPatternTests`, 2 in `CounterAllocationTests`, 6 in `CollisionTests`, 5 in
  `BulkCreatePlannerTests`, 1 in `BulkCreatePerformanceTests`. With B1's branch laid over this one they all pass (339 in
  `Batch.Creation`). `BulkCreateLabTests` (Samba): 20 users `kd<run>01…` with a pre-created number skipped, attributes,
  UAC 512, `pwdLastSet` 0 and the group, then the undo; a value taken after the computation; a group, a contact and a
  computer; a shared password with the account name (canary, then the retry with a new one; skipped when the DC accepts
  it); `ImplicitUpnClash` and `NameInRecycleBin`.
* **For B4.** Call `BulkCreateLookups.Refresh()` before the plan step's final compute (2.7); created rows must be passed as
  created so their values stay reserved; computers never appear in `CreatedAccounts`; the edit keys are the attribute
  names plus `ManagerEditKey` / `GroupsEditKey`.
* **Review (B2).** The disambiguated candidates are prefetched per (root, value) of the batch, not only per collision with
  AD: a root value that `n` rows share gets the levels 2…n+5 in the one prefetch pass (rendered once, by the first such
  row), and a collision deeper than the prefetch looks up the next five levels together. Before, every repeated name
  asked again per namespace and level (200 rows of 10 names: 577 searches; now 22).
  A `description` cell is never split on `|` (SAM takes one value; the split made the row a `SingleValued` error, and a
  pipe could not be entered at all). An expiry cell is an Excel serial only with at least five whole digits: `31.12`
  (Excel's `dd.MM`, the xlsx built-in format 16) or `12.2026` was serial 31 / 12 – January 1900, an account that expired
  before it was created – and is now `InvalidDate`.
  A computation that joins a run read (`ReadOnceAsync`) another computation started on the same lookups reads again when
  that one is cancelled, instead of failing with its cancellation (an edit cancels a computation and starts the next at
  once).

**B4 as built.** Bodies of `BulkCheckViewModel`, `CheckRowViewModel` / `CheckCellViewModel`, `BulkCreateViewModel.Plan.cs`,
`CreatedAccountViewModel`, `L.BulkCreateCheck` and the B4 resx block; the batch editor's create branches at the anchors of
2.13; `IsBulkCreateAvailable => true` and the three menu entries; `BulkCheckView`, `BulkResultsView`, `BulkCreateSamples`,
`SmokeTest.RenderBulkCreateAsync`; the tests of 2.11. Deviations and additions:

* **Seams for the time before the wave-2 merge** (B3's outputs throw until then). `BulkCheckViewModel.Input` is an internal
  settable `Func<BulkCreateInput?>` (default: the host's `BuildInput`), `BulkCreateViewModel.EnterCheckStepAsync()` (internal)
  goes to *Sprawdzenie* and computes, `SharedPasswordReader` (internal, default `Rules.ReadSharedPassword`) – the Presentation
  tests drive the check, the plan and the results through the fixture `B4Wizard` (in `BulkCheckTests.cs`) with them.
  `BulkCreateViewModel.ShowSample(computation, plan)` (public) shows a finished computation in the check step and keeps the plan
  for the next Preview **only while the window has no session** – the smoke test and screenshot 43 render without a planner;
  `BulkCheckViewModel.Show(computation)` (public) updates the grid in place without computing. The Excel row of step 1 is
  wired in the toolkit's generated `partial void OnStepChanged(BulkCreateStep)` (Plan.cs: `Check.ExcelRowOffset ??= () =>
  Source.ExcelRowOffset`), because the host's constructor is the contract's; before that, *Nr* is `number + 1` with a header
  (`HeaderLine` + 1) and `number` without.
* **Batch editor.** Besides the anchors: `OnBulkCreateChanged` (contract line) also raises `WindowTitle` and `ChangeTitle`
  (the container and the kind change the title); the create branch of `ExecuteAsync` calls the new private
  `ExecuteCreateModeAsync` next to it; the create branch of `Undo` runs a local async function (the command is synchronous).
  Every execution in create mode is confirmed by typing (`BulkCheck.ConfirmCreate`, or `ConfirmCreateWithoutPasswords` for
  contacts, groups, computers and users without a password, for a new create plan; `Batch.ConfirmExecute` for a retry and the
  undo) and runs with the plan's own title (the undo's is `BulkCheck.UndoTitle`: *Cofnięcie: Utworzenie kont (5) w jednostce
  „Nowi pracownicy”*). The wizard's own planning reasons (*wpisz hasło ponownie*, retry boxes, not connected) reach the
  editor's `Error` through `BulkCreateViewModel.LastPlanError` instead of the generic *Nieoczekiwany błąd*. The preview build of
  5 000 creates stays on the UI thread: the end-to-end test builds it in well under 3 s (the whole compute, plan and preview of
  5 000 rows takes ≈ 3 s on Linux), so the lead-agreed `RunPlanningAsync` anchor is not used.
* **Change title.** `BulkCheck.ChangeTitle.<kind>` (`L.BulkCreateCheck.ChangeTitleKey`: users, contacts, groups, computers;
  `{0}` objects, `{1}` the place) with `BulkCheck.InUnit` (*jednostce „…”*), `InContainer` (*kontenerze „…”*) and `InDomain`;
  counted from the OK and warning rows and fixed when the plan is built.
* **Canary.** Up to `MaxCanaries` (3) when a canary's Create fails with `AlreadyExists` / `ConstraintViolation`; every other
  failure (incl. a partially succeeded canary) stops. The objects that did not run are added to the result as `Cancelled`
  (error rows `Skipped`) with no change – synthesized, not audited – so *Ponów nieudane* covers them; the retry runs a canary
  again when the last run stopped after its canary or a new shared password was typed (2.7 said: no canary on a retry).
* **Retry with a new shared password.** The two boxes of `BulkResultsView` (`NeedsRetryPassword`: `SameForAll` at plan time and
  a failed reset with `PasswordPolicy`) are read when *Ponów nieudane* runs – empty → `BulkCheck.RetryPasswordRequired`,
  different → `Batch.PasswordMismatch` – instead of disabling the button (`CanRetryFailed` is not an anchor); the new password
  goes to **every** password step of the retry plan (rows the canary stopped never tried theirs) and the boxes are cleared.
* **Results.** `Result` is the accumulated result after a create or retry execution and the undo's own result after an undo;
  the undo removes the deleted objects (and their passwords) from `Accumulated`; their accounts stay listed as *Usunięte
  (cofnięte)* without a password. `BulkResultsView`'s DataContext is the `BatchEditorViewModel` (its `CreatedAccounts`; the
  wizard's commands and notes through `BulkCreate`), not the wizard. Run notes (`ResultNotes`): the canary stop, the
  replication note (DC and PDC emulator of the plan's computation), *Konto bez hasła pozostaje wyłączone*, the forest UPN hint.
  The created accounts list contacts and groups too (without a password); computers never. *Kopiuj tabelę z hasłami…* asks
  with its own clipboard wording (`BulkCheck.CopyPasswordsTitle` / `CopyPasswordsWarning`) instead of the file-export warning;
  the sheet's columns of the copy are chosen in `CopyColumns` (`SheetColumnOption`; phone and pattern-only columns
  preselected, password-like headers never offered). *Kopiuj bez haseł* hands nothing out (2.7 said both mark). *Pokaż*
  counts as handed out (as *Resetuj hasło*). The close guard watches **generated** passwords only (a shared password was
  typed by the operator). `MarkPasswordsHandedOut()` marks every account with a password; it runs before the next execution
  when the results were exported with the passwords (`ShowResults` resets the export flag).
* **Check grid.** Descriptor keys `#number`, `#notes`, `#manager`, `#groups`; frozen: *Nr* plus `givenName`, `sn` (users,
  contacts) or `cn` (groups, computers); *Uwagi* right after them; masked columns are `SecretAttributes`. The include check box
  and the status glyph are fixed XAML columns before the generated ones (`FrozenColumnCount` = 2 + the frozen descriptors).
  A cell commits on Enter or leaving the cell (`UpdateSourceTrigger=Explicit`, updated in `CellEditEnding` on commit only, so
  Escape drops the text). After an execution or an undo the grid is computed again in the background (`RecomputeSoon`).
  The GC wait is `GlobalCatalogWait` (20 s on the window's `TimeProvider`); a failed connect checks in the domain only.
* **Snapshots.** `CreatedRows` and `ReleasedValues` (read by the host's `BuildInput`) are immutable snapshots replaced on
  every change, so a computation on the thread pool never sees the UI thread mark a row.
* **Tests.** Presentation: `BulkCheckTests` (23, incl. the GC-session tests with `GlobalCatalogSessionDouble` over
  `FakeGlobalCatalogReader`, and every F3 issue code in Polish), `BulkCreateBatchEditorTests` (17), `BulkCreateEntryPointTests`
  (5), `BulkCreateEndToEndTests` (2 + `OperatorScenario` `[Fact(Skip = "F3 wave 2 merge")]`, written against B3's public
  members: `Source.PasteCommand`, `Columns.Mappings`, `Rules.Profiles` / `Profile` / `UpnSuffix` / `AddGroupsCommand` /
  `SetDefaultForContainerCommand` / `FirstFreeText`). UI (Windows CI): `BulkCreateWindowTests` (6 + the end-to-end paste test
  skipped until the merge), `ScreenshotTestsBulkCreateCheck` (screenshot 43). The smoke test renders step 1, the check step and
  the preview of `BulkCreateSamples`; loading `BulkCreateSamples.Tsv` through `Source` waits for B3's loading API.
