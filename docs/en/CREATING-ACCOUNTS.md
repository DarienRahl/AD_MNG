**English** · [Polski](../ZAKLADANIE-KONT.md)

# Creating multiple accounts in AD Manager (Phase 3)

This document describes the **Create multiple accounts** wizard: user accounts – and also contacts, groups and computers
– created in bulk from a sheet pasted from Excel, LibreOffice Calc or Google Sheets, or from a CSV or `.xlsx` file, with
names built by rules, for example sequentially numbered `kd201`, `kd202`… with the UPN `kd201@contoso.com`. Technical
design: [docs/dev/phase3-bulk-create.md](../dev/phase3-bulk-create.md).

Rules that apply throughout the wizard:

- **Sheet data only in memory.** The pasted table, the computed values, the check result, the created accounts and their
  passwords exist only in the wizard window and disappear when it is closed. A file is created only when the operator
  chooses an export. Naming profiles store rules and options – never sheet data or passwords.
- **Every account is created through a change plan:** preview, PowerShell equivalent, confirmation by typing the number
  of objects, session log, *Retry failed* and *Undo* – the wizard is part of the bulk editor.
- **One domain controller per window.** All reads and writes go to the window's controller. In a forest with several
  domains the wizard additionally opens a read-only session to the global catalog of **the same** controller – solely to
  check the uniqueness of UPNs and e-mail addresses across the whole forest.
- **Passwords only in memory.** Initial passwords never reach the session log, the PowerShell history, the diagnostic
  logs or the profiles. They reach the clipboard only through the secure copy (cleared after a minute), and a file only
  in an export the operator explicitly asks for.

## 1. What the wizard is for

A typical case: HR sends a sheet with new employees (mobile phone, given name, surname, department, e-mail address). The
operator selects the range in Excel, copies it, pastes it into the wizard and gets accounts in one organizational unit –
with a logon name, UPN, e-mail address, display name, initial password, a forced password change and group memberships.
Before anything is written, the wizard shows every row with the computed values and notes: names already taken, a
possibly existing account of the same person, values that are too long, a manager that was not found…

The wizard creates four kinds of objects (the *Object kind* box): **Users**, **Contacts**, **Groups** and **Computers**
– with the same mechanism and with the columns and options that fit the kind. All objects of one run go to **one**
container (*Target container*).

## 2. How to open the wizard

- command palette (Ctrl+K): **Multiple accounts…** (command `new.bulkCreate`, group *New*);
- *Action → New → Multiple accounts…*;
- the context menu of an organizational unit or a container in the tree: *New → Multiple accounts…* – the objects are
  created in the clicked container;
- the context menu of the object list: *New → Multiple accounts…*;
- the **Several at once…** button in the *New Object* window (the commands *New User*, *New Contact*, *New Group*, *New
  Computer*) – closes the single-object window and opens the wizard for the same kind and container.

The wizard opens in a bulk editor window, e.g. *Create multiple accounts – Nowi pracownicy* (for the other kinds:
*Create multiple contacts*, *groups*, *computers*). The container is filled in right away: the container in which *New*
was clicked, and from the palette and the *Action* menu the container of the current tab (when the tab shows the domain
or search results – the domain's default container for new objects). The *Browse...* button next to the *Target
container* box changes it. The command is available whenever the window is connected; whether the window's account may
create objects of the chosen kind in that container is checked by the wizard itself in the *Check* step.

The wizard has four steps (shown as *Data and rules* in the step bar of the bulk editor), followed by the usual steps of
the bulk editor:

```
 Data and rules                                                      │ bulk editor
 1 Data ─── 2 Columns ─── 3 Names and options ─── 4 Check ───────────┼─ Preview ─── Execution ─── Results
```

You move between the wizard steps with the *Next* and *Back* buttons; in the *Check* step the *Preview* button builds
the change plan.

## 3. Step 1 – Data

**Pasting.** Select a range in Excel – with or without a header row – and press **Ctrl+V** in the wizard or click
*Paste*. A range from LibreOffice Calc and Google Sheets works the same way (tab-separated text), and so does a table
from Word or a web page (the HTML clipboard format – read only when the text does not look like a table or contains
numbers in scientific notation). Each further paste **appends** rows; *Paste and replace* replaces the whole table. When
all (or more than half) of the pasted rows are already in the table, the wizard asks: *These rows are already pasted …
Append them anyway, replace the table or cancel?* (*Append anyway*, *Replace*, *Cancel*). When appending, the columns
are matched by their headers (an unknown header creates a new column), and in a table without headers by their order.

**File.** *Load file…* accepts `.xlsx`, `.csv` and `.txt` up to 50 MB and replaces the loaded data (the wizard asks
first). From an `.xlsx` file the first visible, non-empty sheet is read – the other non-empty sheets and hidden rows are
skipped with a warning. Numbers arrive with the workbook's format, so a phone number stored as a number is not
shortened. CSV and text: the separator `;`, `,` or tab (detected from the first row), the encoding UTF-16 (Excel:
*Unicode Text (\*.txt)*), UTF-8 with or without a BOM, and otherwise the Windows ANSI code page of the workstation
(cp1250 on Polish Windows). Files are read into memory, without temporary files.

**Typing.** The table cells can be corrected in place. In an empty wizard, *Add row* starts a table with the columns
*Given name, Surname, E-mail, Mobile, Department*, which the next step maps by itself. *Add column* adds a column with
the header typed in the *Header of the new column* box; *Remove selected rows* and *Clear* remove data.

**Headers.** *First row contains headers*: *Automatically* (default), *Yes* or *No*. In automatic mode the header is the
row with known column names (chapter 4), also below a sheet title (e.g. *New employees – November 2026*) – the rows
above the header are skipped with a warning, and *Header row* lets you point to another row of the range. A table
without headers has columns named by letters (`A`, `B`, `C`…). *First data row in Excel* (default 2 with a header, 1
without) is used only for display: the *Row* column in the *Check* step and in the results shows the row number of the
sheet, so corrections are easy to find in the source file. The summary below the table reads *Rows: 120, columns: 7 –
headers recognized*.

**What the wizard does with the cells.** It removes the BOM and invisible characters (e.g. zero-width spaces), replaces
non-breaking spaces with ordinary ones, collapses repeated spaces and trims spaces at the start and the end. Empty
columns and rows at the end of the range disappear, empty rows in the middle are skipped. Cells with a tab or a line
break (Alt+Enter) are read as Excel wrote them, and quotation marks in ordinary text (e.g. the cell `"Projekt A" –
zespół`) remain ordinary characters. Warnings give the row and column number – never the value:

| Situation | What the wizard does |
|---|---|
| an Excel error value (`#N/A`, `#VALUE!`, `#NAME?`, in Polish Excel `#N/D!`, `#ARG!`, `#NAZWA?` …) | the cell stays empty, a warning |
| a number in scientific notation (`4.86E+11` – Excel shortened a 12-digit number in a column with the *General* format) | a warning; when the clipboard also holds the HTML format with the full value, the wizard reads it (*the full number was read from the HTML format of the clipboard*) |
| merged cells (HTML table) | split, the extra cells empty |
| a repeated header | both columns stay, the second one with the suffix *(2)* |
| rows longer than the header | columns without a header at the end of the table |
| a column that looks like passwords (*Password*, *Initial password*, *PIN*, Polish *Hasło* …) | it is never used: the cells are hidden (`••••••••`), read-only and copied as the mask |
| more than 10,000 rows, 256 columns or 65,536 characters in a cell | the first 10,000 rows are loaded, the extra columns are skipped, the cell is shortened |

> Excel also copies **hidden columns**, but **not rows hidden by a filter**. Before copying, check that the sheet's
> filter does not hide people who are supposed to get accounts.

*Next* is available when a container is chosen and the table has at least one row.

## 4. Step 2 – Columns

Here every column of the sheet has a row: *Column* (the header or the letter), *Samples* (the first values from the
sheet), *Use as*, *Attribute*, *Name order*, *Case* and *Notes*. The uses:

| Use as | Meaning |
|---|---|
| *Ignore* | the column is not used |
| *Attribute* | the value goes to the chosen attribute – any writable attribute of the class according to the schema, including `extensionAttribute1–15` and custom attributes; the list shows the display name (in the UI language) with the LDAP name, e.g. *Mobile [mobile]* |
| *Pattern only* | the column goes to no attribute, but it can be used in a pattern (`{$column:…}`, chapter 5.2) – e.g. a private e-mail address to which the operator will send the password |
| *Full name* | one column split into the given name (`givenName`) and the surname (`sn`), in the order *First name Last name* or *Last name First name*; titles (*dr hab.*) are skipped, and a comma means "surname, given name" (*Kowalski, Jan*); users and contacts |
| *Manager* | `manager` (for groups and computers `managedBy`), identified by the logon name, UPN, e-mail address, display name or DN |
| *Groups* | the groups of this row, separated by semicolons (also `\|` or on separate lines of the cell), by name or DN |
| *Country/region* | `c`, `co` and `countryCode` at once – from the Polish or English country name or from a code (`PL`, `616`); users and contacts |
| *Account expires* | the account expiration date of this row (`dd.MM.yyyy`, `yyyy-MM-dd` or a date from an `.xlsx` file; US-style `MM/dd/yyyy` is not accepted) – the end of that day; users only |

Two columns cannot have the same use. The attribute list does not offer attributes the wizard does not write: system and
read-only attributes, binary and confidential ones, `userAccountControl`, `member`, DN-valued attributes (except the
manager), `proxyAddresses` (only through the option of chapter 5.4), `c` / `co` / `countryCode` (only through
*Country/region*) and the attributes of Exchange and Skype for Business.

**Column recognition.** When a table is loaded (and after *Recognize again*) the wizard maps the columns by itself:
first by the mappings remembered in the profile (chapter 5.1), then by Polish and English headers – regardless of case,
diacritics and punctuation (*Tel. kom.*, *E-mail address*) – by LDAP names and the attribute display names of the UI
language, and finally by the content. Both header languages are recognized whatever the UI language, so a Polish sheet
works in the English UI and vice versa. A header must match as a whole: *Komórka organizacyjna* (organizational unit) is
the department, not a phone. The most common headers:

| Mapping | English headers | Polish headers |
|---|---|---|
| `givenName` | First name, Given name, Forename | Imię, Imiona |
| `sn` | Last name, Surname, Family name | Nazwisko |
| *Full name* | Full name, Employee name, for users and contacts also Name | Imię i nazwisko, Pracownik, Osoba; *Nazwisko i imię* – surname first |
| `sAMAccountName` | Login, Username, User ID, Logon name, Account | Login, Nazwa użytkownika, Nazwa logowania, Konto |
| `userPrincipalName` | UPN, User principal name | UPN, Login UPN |
| `mail` | E-mail, Mail, E-mail address, Work e-mail | E-mail, Adres e-mail, Adres mailowy, Poczta, E-mail służbowy |
| *Pattern only* | Private e-mail, Personal e-mail, Home e-mail | E-mail prywatny, Mail prywatny, Adres prywatny |
| `mobile` | Mobile, Mobile phone, Mobile number, Cell phone, Cell | Telefon komórkowy, Komórkowy, Tel. kom., Nr komórki, GSM |
| `telephoneNumber` | Phone, Telephone, Office phone, Business phone, Work phone, Extension | Telefon, Tel., Nr telefonu, Telefon służbowy, Wewnętrzny |
| `title` | Title, Job title, Position | Stanowisko, Funkcja |
| `department` | Department, Dept | Dział, Departament, Wydział, Komórka organizacyjna |
| `company` | Company, Employer, Organization | Firma, Spółka, Pracodawca |
| `physicalDeliveryOfficeName` | Office, Room | Biuro, Pokój, Siedziba, Miejsce pracy |
| `employeeID` / `employeeNumber` | Employee ID / Employee number, Personnel number, Staff number | ID pracownika / Numer pracownika, Nr ewidencyjny, Nr kadrowy |
| *Manager* | Manager, Supervisor, Reports to | Menedżer, Przełożony, Kierownik, Szef |
| *Account expires* | End date, Contract end, Expires, Expiration date | Umowa do, Data końca umowy, Konto wygasa, Wygasa |
| *Country/region* | Country, Country/region | Kraj, Państwo |
| `streetAddress` / `l` / `postalCode` | Street, Address / City, Town / Postal code, ZIP, Postcode | Ulica, Adres / Miasto, Miejscowość / Kod pocztowy |
| *Groups* | Groups, Group, Member of | Grupy, Grupa, Członkostwo |

The content matters too: a column headed *Telefon* (also *Tel.*, *Nr telefonu*) in which at least 80 % of the values are
Polish mobile numbers goes to `mobile` (note *mobile numbers detected*); the content does not switch English headers
such as *Phone*, which stay `telephoneNumber`. *Komórka* (Polish for both a mobile phone and an organizational unit) is
the mobile phone only when it contains numbers, and otherwise the department. In a table without headers, columns with
e-mail addresses and phone numbers are recognized by their content (*recognized by the content*). Password columns are
never mapped (*probably passwords – the column is not used*), and personal identifiers (PESEL, NIP, ID card number, date
of birth, SSN, IBAN) only by hand (*personal identifier – skipped (map it only if really needed)*). A second column with
the same use gets *Ignore* with the note *another column already has this target – skipped*.

**Case** (the uses *Attribute* and *Full name*): *Unchanged*, *Like A Name* (every word and every part of a
double-barrelled surname capitalized: *NOWAK-KOWALSKA* → *Nowak-Kowalska*), *lower case*, *UPPER CASE*. Apart from this
choice and the phone format, the wizard does not change the values from the sheet; a given name or surname written in
capitals gives a warning with the hint *Like A Name*.

**Phones.** When a phone column contains numbers without a plus (`48600100200`), the note reads *numbers without +48 –
the international format adds it*, and the *International format* button switches on the *Phone numbers* option of the
*Advanced* section (chapter 5.7) for all phones.

## 5. Step 3 – Names and options

At the top of the step is the *Naming profile*, and below it the sections **Names**, **Account**, **Groups** and the
collapsed **Advanced** section with a one-line summary.

### 5.1 Naming profile

Built-in profiles (they cannot be overwritten or deleted – a changed one is saved under a new name):

| Kind | Profile | Rules |
|---|---|---|
| Users | *Standard* | `cn` and `displayName`: `{givenName} {sn}`; logon name: `{givenName\|sam\|lower}.{sn\|sam\|lower}` (*Łucja Żak* → `lucja.zak`); UPN prefix: `{sAMAccountName}`; counter: *Lowest free number (fills gaps)* |
| Users | *Numbered accounts (kd201…)* | logon name: `kd{$nr:201}`; UPN prefix: `{sAMAccountName}`; `mail`: `{sAMAccountName}@{$mailDomain}`; `cn` and `displayName`: `{givenName} {sn}` (AD lists show the name, not the number); counter: *Next after the highest*, without numbers from the AD Recycle Bin |
| Contacts | *Standard* | `cn` and `displayName`: `{givenName} {sn}` |
| Groups, Computers | *Standard* | `cn` from a column (e.g. *Group name*, *Computer name*); logon name: `{cn}` (a computer gets `$` at the end) |

**Which profile is selected.** When the wizard opens: the container's default profile (*Default for this container*),
otherwise the profile of this kind used last, and finally *Standard*. Changed rules or options are marked *(changed)*;
when you switch the profile or the *Object kind*, the wizard asks whether to discard the changes.

**Saving.** *Save* saves the changes of a saved profile. A built-in profile is saved through *Save as…* (the suggested
name has the suffix *(copy)*): the panel has the *Profile name* box, the *Default for this container* option and a note
on which column mappings the profile will remember (*The profile also remembers the mapping of the columns: …*). With a
saved profile, *Default for this container* makes it the default or stops it being one; *Delete* deletes a saved profile
(created objects do not change). Up to 50 profiles can be saved; profiles are shared by all windows of the program.

**What a profile contains:** the rules (attribute, pattern, *Rule replaces the column*), the chosen UPN suffix and
e-mail domain, the account options and the advanced options, the groups (with the domain name – in a window of another
domain they are skipped with the warning *The profile's groups (count: …) were chosen in the domain … and are not used
here*), the column mappings by header and the containers for which the profile is the default. A header is remembered
only when it does not look like data (no `@`, fewer than 7 digits) or like a password. A profile **contains no** value
from the sheet, computed name, counter number or password. The user or group that may join the computers (chapter 5.5)
applies to the current run only.

### 5.2 Rules and patterns

The *Names* section has a list of rules with the columns *Attribute*, *Pattern* and *Sample*. Fixed rows: users – `cn`,
`displayName`, `sAMAccountName`, `userPrincipalName`, `mail`, `description`; contacts – `cn`, `displayName`, `mail`,
`description`; groups – `cn`, `sAMAccountName`, `mail`, `description`; computers – `cn`, `sAMAccountName`,
`description`. *Add rule* adds a rule for any attribute of the class, *Remove rule* removes it. An empty pattern is
described by the hint *no rule – the value comes from column "…"* or *no rule – left empty*. When `cn` (or the logon
name – except for contacts) has neither a rule nor a column, the step reports an error.

**Where a value comes from.** For each row and attribute: from a cell corrected in the *Check* step, otherwise from the
non-empty cell of the mapped column, and only then from the rule. When an attribute has both a column and a rule, the
rule shows *Column "E-mail" takes precedence – the rule applies to empty cells only*; the *Rule replaces the column*
option reverses the order. A pattern without fields is a constant value (e.g. `description` = `Temporary account`).

**Syntax.** A pattern is text with fields in curly braces – the same syntax as in the templates of the bulk editor and
of messages to users. The field names and functions are the same in every UI language:

| Field | Value |
|---|---|
| `{givenName}`, `{sn}`, `{sAMAccountName}`, `{mail}` … | the computed value of the attribute in this row (from the column, a correction or the rule); fields have LDAP names |
| `{$column:Department}`, `{$column:C}` | the cell of the column with this header or letter |
| `{$nr:201}`, `{$nr:1:4}`, `{$nr:200:0:10}` | the counter (chapter 5.3) |
| `{$suffix}` | the chosen UPN suffix |
| `{$mailDomain}` | the *E-mail domain* |
| `{$domain}` | the DNS name of the window's domain |
| `{$parentName}` | the name of the target container |
| `{$date:yyyy-MM-dd}`, `{$date:dd.MM.yyyy}` | today's date |

After the field name you can add functions separated by `|`, e.g. `{givenName|nodiacritics|lower}`:

| Function | Effect |
|---|---|
| `lower` / `upper` | lower case / UPPER CASE |
| `title` / `capitalize` | Every Word Capitalized / First letter capitalized |
| `trim` | without spaces at the start and the end |
| `nodiacritics` | without diacritics (ą→a, ł→l) |
| `nospaces` / `alnum` | without spaces / letters and digits only |
| `sam` | like a logon name (no diacritics or disallowed characters), at most 20 characters per field |
| `first:N` / `last:N` | the first N characters / the last N characters |
| `replace:a:b` | replace "a" with "b" |
| `default:text` | "text" when the field is empty |

*Insert field* inserts at the cursor the fields described by the attribute display names, the columns, the special
fields (*Next number*, *UPN suffix*, *E-mail domain*, *Domain (DNS name)*, *Container name*, *Today's date
(YYYY-MM-DD)*, *Today's date (DD.MM.YYYY)*) and the functions with a description. A column mapped to an attribute is
inserted as that attribute (e.g. `{department}`), so it takes the corrections of the *Check* step into account;
`{$column:…}` is offered for *Pattern only* and ignored columns. Columns that look like passwords are not available in
patterns either.

Writing rules: `{{` and `}}` give a curly brace in the text; inside a field `\` precedes the character `|`, `{` or `}`
(`{$column:Department\|Team}`). The bulk editor fields `{$dn}`, `{$rdn}` and `{$counter}` do not work when objects are
created (the counter is `{$nr}`). In addresses (UPN, `mail`) use `nodiacritics|nospaces|lower` instead of `sam` – `sam`
cuts every field to 20 characters, while the wizard never shortens addresses. A date format with month or day names
(`MMMM`, `ddd`) gives English names whatever the UI language – the wizard warns and suggests a numeric format.

**Examples** (UPN suffix and e-mail domain `contoso.com`, no `kd…` accounts in the domain yet, date 2026-11-10):

| Rule | Pattern | Row | Result |
|---|---|---|---|
| logon name | `kd{$nr:201}` | rows 1–3 | kd201, kd202, kd203 |
| UPN prefix | `{sAMAccountName}` | row 1 | kd201@contoso.com |
| `mail` | `{sAMAccountName}@{$mailDomain}` | row 1 | kd201@contoso.com |
| UPN prefix (*UPN = e-mail address*) | `{mail}` | `mail` = lucja.zak@contoso.com | lucja.zak@contoso.com |
| logon name | `{givenName\|sam\|lower}.{sn\|sam\|lower}` | Łucja Żak | lucja.zak |
| logon name | `{givenName\|first:1\|sam\|lower}{sn\|sam\|lower}` | Anna Konstantynopolitańczykowa | akonstantynopolitanc (shortened to 20 characters) |
| `mail` | `{givenName\|nodiacritics\|nospaces\|lower}.{sn\|nodiacritics\|nospaces\|lower}@{$mailDomain}` | Anna Konstantynopolitańczykowa | anna.konstantynopolitanczykowa@contoso.com (the address is not shortened) |
| `cn` | `{givenName} {sn}` | Łucja Żak twice in the sheet | Łucja Żak, Łucja Żak (2) |
| logon name | `u{$nr:1:4}` | rows 1–2 | u0001, u0002 |
| `employeeNumber` | `{$nr:200:0:10}` | rows 1–3 | 200, 210, 220 |
| `homeDirectory` | `\\fs01\home$\{sAMAccountName}` | kd201 | `\\fs01\home$\kd201` (the folder is not created) |
| `description` | `Created {$date:yyyy-MM-dd} – {$column:Contract}` | *Contract* = contractor | Created 2026-11-10 – contractor |
| `info` | `{{temporary}} {$column:C}` | C = internship | {temporary} internship |

**Live sample.** The *Sample* column shows the result for the first three rows (and *longest: …* when a longer value
comes further down), with the first free counter number; *(characters changed)* and *(shortened)* mark values corrected
by the options of the *Advanced* section. A pattern error gives the position in the text and the field, sometimes with a
hint (*Did you mean {givenName}?*). The sample does not take the corrections of the *Check* step or taken names into
account – only step 4 does that.

### 5.3 Numbering

When a rule uses the counter `{$nr…}`, a **Numbering** panel appears above the rules: *Prefix*, *First number*, *Digits
(leading zeros)* and *Step*. All `{$nr…}` fields of the rules use one counter, so the panel changes all of them at once.
The syntax `{$nr:start[:digits[:step]]}`: start 0–999,999,999 (default 1), digits 0–9 (padding with leading zeros; 0 –
no padding), step 1–1,000,000 (default 1). `kd{$nr:201}` gives kd201, kd202…, `kd{$nr:1:4}` – kd0001, kd0002…

**First free number.** When you enter the step and after each counter change, the panel queries the directory and shows
e.g. *First free number: kd208 (highest existing: kd207)*; without a connection – *First number: kd201 (not checked in
AD)*.

**How the number is assigned** (*Advanced → Counter*):

- *Next after the highest* (the *Numbered accounts* profile): numbering starts after the highest number that a logon
  name with this prefix already has **in the domain** – also counting deleted accounts in the AD Recycle Bin when *Do
  not reuse numbers of accounts in the AD Recycle Bin* is checked (the default). Example: kd201, kd203 and kd205 exist,
  and kd207 is in the AD Recycle Bin → kd208, kd209… The number of an employee who left is never handed out again.
- *Lowest free number (fills gaps)*: from the *First number* upwards, skipping taken ones (in the same example: kd202,
  kd204, kd206, kd208).

In both modes the number must be free for **every** attribute that uses it: the logon name in the domain, the UPN and
the e-mail address in the whole forest (the address also among the `proxyAddresses` of other objects; the scope of the
check – chapter 6), and free of the numbers taken by earlier rows of the sheet. Numbers grow in row order. An assigned
number stays with its row: a correction or the exclusion of another row does not renumber the rest, and *Renumber* in
the *Check* step assigns the numbers again. A value with a counter is never shortened or completed with a number: when
the prefix with the number exceeds the limit (e.g. 20 characters of a logon name), the row has an error.

**Quick fix.** When an identifier rule is constant text ending in digits (e.g. `kd201` – every account would get the
same name), the wizard suggests *Number them from 201? → kd{$nr:201}* with the *Number them* button.

### 5.4 UPN, e-mail address, Microsoft 365 and Exchange

The `userPrincipalName` rule is a **prefix**: the wizard appends `@` and the *UPN suffix* chosen from the list (the DNS
names of the forest's domains and the additional UPN suffixes). The default suffix is the one stored in the profile if
the forest knows it (otherwise the warning *The profile's UPN suffix … is not in this forest – … is used.*), and in all
other cases the window's domain – as in ADUC. When the prefix pattern already yields a whole address (e.g. `{mail}`),
*from the pattern* is shown instead of the suffix. The *UPN = e-mail address* button sets the rule `{mail}`.

The *E-mail domain* (`{$mailDomain}`) equals the UPN suffix by default and changes with it until the operator chooses
another one. A domain ending in `.local`, `.lan` or `.internal` (and in the *Check* step also `.localdomain` and
`.corp`) does not receive mail from the internet – the wizard warns. An address from the sheet in a domain that is
neither a domain of the forest nor a UPN suffix gets a warning *(a private address?)* – such a column is better mapped
as *Pattern only*.

**Microsoft 365 / Exchange.** Environments differ, so both settings are profile options and are off by default:

- *Add the primary SMTP address to proxyAddresses (Microsoft 365 / Exchange)* (the *Advanced* section) – the new account
  gets `proxyAddresses` = `SMTP:` + `mail`;
- *UPN = e-mail address* – when in your environment the UPN logon name has to equal the e-mail address.

The wizard **does not create mailboxes**: the attributes of Exchange (`mailNickname`, `targetAddress`,
`legacyExchangeDN`, `homeMDB`, `msExch…`) and Skype for Business (`msRTCSIP-…`) cannot be set with a column or a rule –
written directly they would produce incomplete recipients. Mailboxes are created with the Exchange or Microsoft 365
tools.

### 5.5 Account

**Users:**

- *Initial password*: *Generate a separate password for every account* (the default; *Password length* from 12 to 256
  characters, default 16, next to it *domain minimum: …*), *One password for all accounts* (the *Password* and *Confirm
  password* boxes) or *No password – the account is disabled*. Generated passwords are random, different for every
  account, and contain neither the logon name nor a part of the given name or surname.
- *User must change password at next logon* – checked by default.
- *Account is enabled* – checked by default; not available without a password (AD does not accept an enabled account
  without a password).
- *Account expires: end of* with a date – for all accounts; the *Account expires* column takes precedence in its row.

The password must meet the domain's policy: a generated password length below the domain minimum is a step error (*The
domain requires longer passwords …*), and a common password that is too short or not complex blocks the plan when it is
built. A common password that contains the logon name or a part of the display name gives a warning (the domain may
reject it), and with enabled accounts the note *Everybody who knows this password can sign in to every account until its
first password change.* The common password boxes are cleared after each plan is built – after *Back* you have to type
it again. When a chosen group has its own password settings (PSO), the wizard warns: the initial password is checked by
the domain policy, and the group's settings apply only when the password is changed.

Passwords are set **only over an encrypted connection** (Kerberos with encryption, LDAPS or StartTLS). Without one, the
*Check* step reports a problem that concerns every row (*Passwords can only be set over an encrypted connection …*) –
for user accounts with a password and for any creation of computers.

**Contacts** have no account and no password.

**Groups:** *Group scope* (*Domain local*, *Global* – the default, *Universal*) and *Security group (otherwise
distribution)*.

**Computers:** *User or group that can join the computers to the domain* (*Browse...*; with nothing chosen – *(not
chosen – Domain Admins)*). The computer account is created disabled, gets a random 120-character password that nobody
sees (it is not in the results, the clipboard or the export), and only then is it enabled. When someone else is to join
the computers and the window's account is not one of the owners trusted by the KB5020276 hardening, the wizard warns as
the *New Computer* window does (see [COMPUTERS.md](COMPUTERS.md#preparing-an-account-new--computer)). A computer name
has 1 to 15 letters, digits and hyphens (not digits only, no hyphen at the start or the end) and is stored in capitals;
a dot is an error, and `_` a warning (a character not allowed in DNS names).

### 5.6 Groups

*Add groups…* chooses the groups every created object will join (*Every created object is added to these groups.*); the
*Groups* column adds groups to individual rows. The account's primary group (*Domain Users*, for computers *Domain
Computers*) is skipped. A group that is not found or is ambiguous gives a warning; names separated by commas are split
where possible, with a request to use semicolons. Nesting the created group in a way the group scopes do not allow, and
a group to which the window's account cannot add members, give a warning (that membership will be skipped or will fail).

### 5.7 Advanced

| Option | Default | Effect |
|---|---|---|
| *Counter* | per profile | chapter 5.3 |
| *Do not reuse numbers of accounts in the AD Recycle Bin* | checked | the number of an account in the AD Recycle Bin counts as taken; unchecked, it may be reused (with a warning) |
| *When a name is taken* | *Append a number* | *Append a number*: a taken `jan.kowalski` → `jan.kowalski2` (the UPN and address from a `{sAMAccountName}…` rule follow it), *Jan Kowalski* taken in the container → *Jan Kowalski (2)*; *Mark as an error* – the row has an error. Computers – always an error. Values from columns, corrections and values with a counter are never changed – their collision is a row error |
| *Fix disallowed characters in identifiers (ą→a, spaces)* | checked | applies to values from rules: the logon name, the UPN prefix and the part of the address before `@` |
| *Shorten names that are too long (character limit: logon name 20, cn 64)* | checked | UPNs and e-mail addresses are never shortened – too long is an error |
| *Add the primary SMTP address to proxyAddresses (Microsoft 365 / Exchange)* | off | chapter 5.4 |
| *Phone numbers* | *Unchanged* | *International format (+48 600 100 200)*: removes spaces, hyphens, dots and parentheses, turns `00` into `+`, a 9-digit number and `48` with 9 digits get `+48`; mobile numbers in groups of 3-3-3, landlines with a two-digit area code; a number that cannot be written this way is left unchanged with a warning |
| *Look for existing accounts of the same person* | checked | chapter 6 |

Uniqueness: the logon name – in the domain (together with the names of computers and groups); the UPN and the e-mail
address – in the whole forest; the object name (`cn`) – in the target container, regardless of the object type (a group
*Księgowość* collides with an organizational unit *Księgowość*). A name taken only by an object in the AD Recycle Bin
does not block the row but gives a warning: restoring that object will require a rename.

## 6. Step 4 – Check

When you enter the step, the wizard computes all rows (*Checking rows: 40 of 120*, the *Stop* button). It reads the
directory through the window's controller – taken names, counter numbers, managers, groups, possibly existing accounts,
the domain's password policy, permissions on the container – and writes nothing.

**Table.** Columns: *Create* (a check box), *Status*, *Row* (the row of the sheet), the given name and surname (for
groups and computers – the name) pinned on the left, *Notes*, then the computed attributes (`cn`, logon name, UPN,
e-mail address, given name, surname and every attribute that has a value in some row) and *Manager* and *Groups*.
*Double-click a cell to correct it (Enter saves), clear the check box of a row to leave it out.* Summary: *Rows: 120 –
OK: 108, with warnings: 6, with errors: 4, excluded: 2*; below it the scope of the check: *Uniqueness of UPNs and
addresses: the whole forest (one domain)*, *the whole forest (global catalog on DC01)* or *checked in the domain only*.

| Status | Meaning |
|---|---|
| *OK* | the row will be created |
| *Warning* | the row will be created; read the notes |
| *Error* | the row will not be created until you correct it (or clear its check box) |
| *Excluded* | the row's check box is cleared |
| *Created* | the object has already been created in this window |

**What blocks.**

- A **row error** blocks only that row: a value taken in AD or by another row of the sheet that cannot be changed (*…
  the value is already used by "…"*, *… row … has the same value*); a logon name that collides with the logon name of
  another account in the form `name@domain` (Kerberos would resolve it to the other account); a missing required
  attribute (`cn`, logon name); disallowed characters, a value that is too long or a line break in a single-line
  attribute (Alt+Enter in Excel); a counter that is too long or exhausted; an unsupported date in the *Account expires*
  column; a `$` at the end of a user name; an invalid computer name.
- **Problems that concern every row** (above the table) block the whole run: the container does not exist; *You cannot
  create this type of object in this container.*; an error in a pattern; enabled accounts without a password; passwords
  without an encrypted connection; generated passwords shorter than the domain minimum. A common password that is too
  short or not complex is reported when the plan is built – the preview then shows every object with an error.
- **Warnings** do not block: a value changed because it was taken (*a number was appended*); an object with this name in
  the AD Recycle Bin; a manager that was not found, is ambiguous or is created in the same batch (the account is created
  without a manager – when several accounts match you can choose one in the cell: *Several accounts match – choose the
  manager*); a group that was not found; an address or UPN suffix outside the forest; a given name in capitals; an
  unknown country; a phone number in scientific notation or one that cannot be written in the international format; few
  free RIDs on the controller; uniqueness checked in the domain only.

**Possibly existing accounts.** The counter never collides, so a sheet pasted a second time would give the same people
new accounts (kd221… next to kd201…). That is why, when creating users, the wizard looks for the same person among the
domain's users and contacts: by the first value from the sheet (or the corrected one) of the list `employeeID`,
`employeeNumber`, e-mail address, `mobile`, and without them – by the given name and surname together. A match is the
warning *Possibly an existing account of this person: "…" (the same …)*, and two rows of the same person – *Row …
describes the same person …*; the *Possible duplicates* filter shows both. The *Look for existing accounts of the same
person* option switches this off.

**Correcting.** A corrected cell has the source *edited* and takes precedence over the column and the rule; after a
moment the rows are computed again, and the table stays where it was (the scroll position, the selection and the edited
cell do not change). Correcting the logon name also changes the UPN and the address of the rules that use it. The cell
tooltip gives the source of the value (*Source: from the column*, *from the pattern*, *edited* …) and what the wizard
changed (*Changed so that the value is unique.*, *Characters that are not allowed were removed or replaced.*, *Shortened
to the allowed length.*); the cell menu has *Restore the computed value*. *Manager* and *Groups* can be corrected too.

**Tools.** *Filter*: *All*, *OK*, *Warnings*, *Errors*, *Possible duplicates*, *Excluded*, *Created*. *Next error* (F8).
*Copy rows with errors* – the sheet's cells of the rows with errors together with the notes, ready to paste into Excel
and send back for correction. *Check again* – reads the directory again (names taken meanwhile, managers, groups).
*Renumber* – forgets the numbers shown and assigns them again.

After going back to an earlier step and changing the data, the rules or the container, the rows are computed again the
next time you enter the step; cell corrections, exclusions and assigned numbers stay. They are lost only after a change
of the headers, *Paste and replace*, *Clear*, loading a new file or changing the object kind.

*Preview* is available when there are no problems that concern every row and at least one row has the status *OK* or
*Warning*.

## 7. Change plan preview and execution

*Preview* computes the rows once more – with the values of the *Check* step frozen: nothing is renumbered or changed,
and a value that someone took since the check becomes a row error (it shows in the preview and, after *Back*, in the
*Check* step). Then the wizard builds the change plan, e.g. *Create accounts (3) in the organizational unit "Nowi
pracownicy"*; the common password is read exactly now, and its boxes are cleared.

**What the plan contains.** The order of steps as in ADUC:

| Kind | Steps of one object |
|---|---|
| user | creating the account (disabled) with the values → initial password → enabling the account and forcing a password change → manager → membership in each group |
| contact | creation → manager → groups |
| group | creation with the scope and type → `managedBy` → groups |
| computer | creation (disabled) → the right to join the domain → random password → enabling → `managedBy` → groups |

A password rejected by the domain therefore leaves the account disabled and without groups, and a failed addition to a
group leaves a working account without that group; both cases show in the results and can be retried. The preview is the
usual bulk editor preview: objects with steps (the "before" value is empty), filters, check boxes, *Export preview*
(never with passwords) and *Copy as PowerShell* – `New-ADUser … -AccountPassword (Read-Host -AsSecureString -Prompt
'…')`, `Set-ADUser -Manager …`, `Add-ADGroupMember …`; the script contains no password.

**Execute.** The confirmation window gives the title of the change, the number of objects to create and the domain,
reminds you that *The initial passwords will be available in this window only*, and requires typing the number of
objects. The execution runs in the background (activity center) with progress and a *Stop* button.

**One account first.** The wizard first creates one object; only when all its steps succeed does it run the rest (the
activity center and the session log then have two entries). A systematic error – no encryption, no right to reset the
password or to write `userAccountControl`, a password rejected by the policy – stops the execution after one account:
*Stopped after the first account – the other rows were not run. Fix the cause shown below, then use Retry failed.* If
the first object was not created because its value is taken (a problem of that row only), the next one takes over the
attempt – at most three.

## 8. Results and passwords

Above the usual bulk editor report (filter, summary, the status and message of each object, *Export results*, *Retry
failed*, *Undo*) is the **Created objects** list: *Row*, *Name*, *Logon name*, *UPN*, *E-mail*, *Initial password*
(masked, with the *Show* and *Copy* buttons), *Handed out*, *Status* (*Created*, *Created, not every step succeeded*,
*Deleted (undone)*) and *Message*. The list covers all executions of the window; contacts and groups are on it without a
password, computers are not on it. *The initial passwords exist only in this window: hand them out (Copy, Copy table
with passwords or Export) before you close it.*

How to hand out the passwords:

- *Copy* next to an account – the password goes to the clipboard through the secure path (*Password copied – the
  clipboard is cleared after a minute.*). *Show* reveals the password – this also counts as handing it out.
- *Copy table with passwords…* – after a warning (*The table contains the initial passwords in plain text. Paste it only
  where the passwords stay protected (for example an encrypted message) …*) copies a table for Excel: *Row*, the name,
  the logon name, UPN, e-mail address, the chosen sheet columns and the password. *Sheet columns in the copy:* by
  default selects the phone columns and the *Pattern only* columns – usually the mobile number and the private address
  through which the passwords are passed on; columns that look like passwords are not offered. Cells that start with
  `=`, `+`, `-` or `@` are protected against formulas. The clipboard is cleared after a minute.
- *Copy without passwords* – the same table without passwords (e.g. for a ticket); the passwords are then not marked as
  handed out.
- *Export results* – a report to a file chosen by the operator. Passwords go into it only after the confirmation
  *Include passwords?* (*The results contain the new passwords. Include them in the file in plain text?*), otherwise
  they are masked. Protect a file with passwords like the passwords themselves and delete it once they are handed out.

Closing the window while a generated password has not been copied, shown or exported requires a confirmation (*The
generated passwords have not been exported or copied …*). The operator knows a common password, so with one the window
does not ask.

Notes above the list: the controller on which the accounts were created and whether it is the PDC emulator – the other
controllers see the accounts after replication, and until then a logon through another controller may fail; *An account
without a password stays disabled.* after a failed password set; *The UPN may already be used in another domain of the
forest.* when uniqueness was checked in the domain only and the controller refused the creation.

## 9. Retrying and undoing

**Retry failed** plans only the steps that did not succeed in any execution: an account is never created twice, and an
account that got a password does not get a second one. A failed password set gets a new generated password. When the
domain rejected the common password, the results show *New password for the accounts without a password* (*New
password*, *Confirm password*) – the new password goes to every account that does not have one yet. An object whose
value was taken since the check is not retried (*A value was taken since the check – the object is not retried; go back
to Check.*). After a stop after the first account, a retry again starts with one object. You can also go back (*Back*)
to the *Check* step, correct the rows and build a new plan – created rows have the status *Created* and do not enter the
plan, and the results of all executions are on one list.

**Undo** deletes **everything this window created** – from all executions, retries and pasted tables. The undo preview
(e.g. *Undo: Create accounts (3) in the organizational unit "Nowi pracownicy"*) says: *The undo deletes the objects
created in this window (3) – with their passwords and group memberships; with the AD Recycle Bin enabled they can be
restored from it.* Before the plan is built, each object is read again by its `objectGUID`: an object that was moved or
renamed is deleted where it is now, and an object that no longer exists or whose name another object holds is skipped
(*The created object is gone or another object holds its name – the undo leaves it.*). Warnings: the account was used
since it was created (a logon or a password change), the object has child objects (deleting it will fail). The undo is
confirmed by typing the number of objects.

After the undo the accounts have the status *Deleted (undone)* and no longer have passwords, the rows return to their
state before creation, and their names and numbers are free again in this window – a new execution gives the same
numbers. In another window (or later), deleted accounts that are in the AD Recycle Bin still hold their numbers and
names (the counter skips them, the names give a warning). The undo works only in the open wizard window – once it is
closed, the program does not remember what was created.

## 10. Example: accounts kd201, kd202… from an HR spreadsheet

The domain `contoso.test` has the additional UPN suffix `contoso.com`; the new accounts are to go to the organizational
unit *Nowi pracownicy* (under *Pracownicy*). There are no `kd…` accounts in the domain yet. HR sent this sheet (English
headers such as *Mobile*, *First name*, *Last name*, *Department*, *E-mail* would be recognized the same way):

| | A | B | C | D | E |
|---|---|---|---|---|---|
| 1 | Telefon komórkowy | Imię | Nazwisko | Dział | E-mail |
| 2 | +48 600 100 201 | Łucja | Żak | Księgowość | lucja.zak@contoso.com |
| 3 | +48 600 100 202 | Bartłomiej | Śliwiński | Kadry | bartlomiej.sliwinski@contoso.com |
| 4 | +48 600 100 203 | Zofia | Kamińska | IT | zofia.kaminska@contoso.com |

1. In the tree, right-click *Nowi pracownicy* → *New → Multiple accounts…*. The window *Create multiple accounts – Nowi
   pracownicy* has *Object kind*: *Users* and the container *Nowi pracownicy*.
2. **Data:** in Excel select `A1:E4`, Ctrl+C, in the wizard Ctrl+V. Summary: *Rows: 3, columns: 5 – headers recognized*.
   *Next*.
3. **Columns:** the wizard mapped by itself *Telefon komórkowy* → `mobile`, *Imię* → `givenName`, *Nazwisko* → `sn`,
   *Dział* → `department`, *E-mail* → `mail`. *Next*.
4. **Names and options:** *Naming profile* → *Numbered accounts (kd201…)*, *UPN suffix* → `contoso.com` (the *E-mail
   domain* changes with it). The *Numbering* panel: prefix `kd`, first number 201, *First free number: kd201*. The
   `mail` rule shows *Column "E-mail" takes precedence – the rule applies to empty cells only* – the addresses from the
   sheet win. *Account*: *Generate a separate password for every account*, *User must change password at next logon* and
   *Account is enabled* stay checked. *Groups → Add groups…* → the group of new employees, e.g. `GG_Nowi`. *Next*.
5. **Check** (selected columns):

   | *Row* | Given name | Surname | `cn` | `sAMAccountName` | `userPrincipalName` | `mail` | *Status* |
   |---|---|---|---|---|---|---|---|
   | 2 | Łucja | Żak | Łucja Żak | kd201 | kd201@contoso.com | lucja.zak@contoso.com | OK |
   | 3 | Bartłomiej | Śliwiński | Bartłomiej Śliwiński | kd202 | kd202@contoso.com | bartlomiej.sliwinski@contoso.com | OK |
   | 4 | Zofia | Kamińska | Zofia Kamińska | kd203 | kd203@contoso.com | zofia.kaminska@contoso.com | OK |

   *Rows: 3 – OK: 3, with warnings: 0, with errors: 0, excluded: 0*, *Uniqueness of UPNs and addresses: the whole forest
   (one domain)*.
6. *Preview* → the plan *Create accounts (3) in the organizational unit "Nowi pracownicy"* → *Execute*, type `3`. kd201
   is created first, then kd202 and kd203.
7. **Results:** three accounts with masked passwords. *Copy table with passwords…* (the *Telefon komórkowy* column
   selected in *Sheet columns in the copy:*) → paste the table where the passwords stay protected and hand them out to
   the employees.
8. Finally, *Back* to the *Names and options* step and *Save as…*, e.g. "Nowi pracownicy – kd", with *Default for this
   container* checked. The next wizard opened on *Nowi pracownicy* selects this profile by itself, maps the columns by
   the remembered headers and starts from *First free number: kd204 (highest existing: kd203)*.

Variants:

- **The domain already has kd… accounts:** if kd201, kd203 and kd205 exist and kd207 is in the AD Recycle Bin, numbering
  starts at kd208.
- **The sheet has private addresses:** map the *E-mail* column as *Pattern only* – `mail` is then created by the
  profile's rule (`kd201@contoso.com`), and the private address is selected by default in *Copy table with passwords…*,
  so that the password can be sent to it.
- **The work address is to be the login:** *UPN = e-mail address* – UPN = `lucja.zak@contoso.com`, while the logon name
  (pre-Windows 2000) stays `kd201`.

## 11. Data and privacy

- The pasted table (including skipped title rows), the computed values, the check result, the names, managers and groups
  read from the directory, the plans, the results, the identifiers of the created objects and the passwords exist **only
  in the window's memory** and disappear when it is closed. The wizard does not save its state – a closed window has to
  be started from scratch.
- `.xlsx` and CSV files are read into memory, without temporary files. Diagnostic logs contain only counts and times
  (e.g. the number of rows and queries), never values; reading the clipboard logs at most an error code.
- Only the **naming profiles** in the program's settings go to disk: profile names, patterns, options, the chosen UPN
  suffix and e-mail domain (DNS names), groups and containers as DNs with the domain name, and column headers (without
  addresses, numbers and passwords). Never values from the sheet, computed names, counter numbers or passwords.
- Files are created only at the operator's command: *Export preview* (without passwords) and *Export results* (passwords
  only after confirmation). *Copy as PowerShell* goes to the clipboard.
- Passwords: generated ones exist only in the plan and the results of the window; the common one is read from the boxes
  when the plan is built and cleared immediately. The session log records the password set without the value, the
  PowerShell equivalent prompts for the password (`Read-Host -AsSecureString`). Passwords are copied through the secure
  clipboard (cleared after a minute). The passwords of computer accounts are never shown anywhere.
- Rows and results do not reach the AdMng Watchdog service (Watchdog sees only the changes in the directory, like any
  other).

## 12. Limitations and pilot

Limitations of this version:

- **One container per run.** The objects of one sheet go to one container; there is no container column.
- **Excel:** copies hidden columns, but not rows hidden by a filter. **Leading zeros that Excel removed from numbers**
  (`00123` typed in a *General* column is `123`) cannot be detected – format such columns as text before entering the
  data. The wizard only detects numbers in scientific notation; it recovers the full value from the HTML clipboard
  format or from an `.xlsx` file. It does not read `.ods` files (from Calc – paste, CSV or `.xlsx`).
- **Limits:** 10,000 rows, 256 columns, 65,536 characters per cell, files up to 50 MB, 50 saved profiles.
- **Passwords from the sheet are not supported** – columns that look like passwords are hidden and skipped. The wizard
  also does not send passwords to managers or employees – the operator hands them out (chapter 8).
- **Not in this version:** mailboxes, home folders and profiles (only the attributes `homeDirectory`, `homeDrive`,
  `profilePath`), *Password never expires* and other account flags at creation (the bulk editor sets them afterwards), a
  manager created in the same sheet (set it afterwards with the bulk editor), members of the created groups from a
  column, copying a template account (*Copy…* in ADUC), DN-valued attributes other than the manager, `inetOrgPerson`
  objects, sharing profiles with other operators, scheduled creation.
- **Counter:** a prefix shorter than 2 characters is checked number by number and, in the *Next after the highest* mode,
  counts from the *First number* (as when filling gaps). The number always depends on what the window's controller sees:
  an account created at the same moment on another controller and not yet replicated is not detected – AD itself does
  not resolve duplicate logon names.
- **AD Recycle Bin:** deleted objects are visible only to an account that can read the domain's `CN=Deleted Objects`
  container; a wizard opened with another account does not find them (without a message) – it does not warn about names
  from the AD Recycle Bin and does not skip their numbers.
- **Forest with several domains:** UPNs and addresses are checked across the whole forest only when the window's
  controller is a ready global catalog; otherwise *checked in the domain only* – controllers from Windows Server 2012 R2
  reject a duplicate UPN at write time anyway, unless this was switched off in `dSHeuristics` (the result: *The UPN may
  already be used in another domain of the forest.*). The password settings of groups (PSO) can usually be read only by
  a domain administrator – without that, a common password is checked against the domain policy only.
- **The sample in step 3** computes the values locally (without the corrections of step 4 and without taken names). A
  10-digit number without a country code is not changed by *International format*. `description` always has one value
  (`|` does not split the cell). In the *Account expires* column a number is an Excel date only when it has at least
  five digits (`31.12` is a date error, not 31 January 1900).
- **Undo** works only in the open window. Between the undo preview and its execution the wizard does not check the
  objects again – an object replaced in the meantime would be deleted under the same name; run the undo right after the
  preview.

**To be confirmed in the pilot on Windows Server AD** – the fake directory of the tests and the Samba lab (one domain in
the forest) cannot settle this; scenario and results: [PILOT.md, chapter 3.1](PILOT.md#31-everyday-work-phases-13):

- a forest with several domains: checking UPNs and addresses through the global catalog (*the whole forest (global
  catalog on …)*), *checked in the domain only* without a global catalog, and the forest-wide UPN uniqueness check that
  Windows itself performs at write time;
- uniqueness of the object name in a container for different object types (`CN=` next to an `OU=` of the same name);
- computer accounts: KB5020276 (reuse of a prepared account by someone outside the administrators) and KB5008102 (the
  rules for the `$` character), the "disabled → random password → enabled" sequence and a comparison with the *New
  Computer* window, which creates the account enabled without a password;
- behaviour when the RID pool runs out (the low-pool warning, errors of the following accounts, retry);
- logging on with a new account through another controller before replication;
- an OU administrator with delegated permissions but without the right to reset passwords – the execution stops after
  the first account;
- pasting from real Excel (Polish version, numbers in scientific notation and their recovery from the HTML format), from
  LibreOffice Calc and from Google Sheets.
