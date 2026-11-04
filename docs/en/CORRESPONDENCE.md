**English** · [Polski](../KORESPONDENCJA.md)

# Correspondence (Outlook) in AD Manager (stage U5)

The **Correspondence** window is the *Korespondencja WPiA* program (`MailerWPiA.exe`) built into AD Manager: the same
message sent to many recipients through the **classic Outlook** installed on the operator's computer. By default every
recipient gets a **separate message**, so the addressees do not see each other; copies land in the *Sent Items* of your
mailbox, and sending goes from your account and with your signature. All features of the program were kept (files with
addresses, `{{…}}` fields, an editor with pasting from Word, signatures, attachments, preview, test, drafts, the "already
sent" journal) and new ones were added: **recipients straight from AD** with fields from the directory (also the
salutations from U1d), custom fields, templates, grouping by address, voting buttons with reading of the responses, and
**queries to mailbox owners** – the *Mailboxes* tab finds the person a shared mailbox is assigned to and asks them
whether the mailbox is still needed. Technical design (in English):
[dev/u5-outlook-correspondence.md](../dev/u5-outlook-correspondence.md).

Rules that apply everywhere:

- **Only Outlook sends, and only after your click.** AD Manager prepares each message and hands it to Outlook; nothing is
  sent in the background or automatically.
- **Nothing from the directory goes to disk.** The recipient list, field values from AD, the preview, the sending
  report, the mailbox matches and the responses exist only in the memory of the window. On disk there are only the
  settings, templates, the last draft and the "already sent" journal, which keeps **hashes** of addresses, not the
  addresses (chapter 11). A file is created otherwise only when the operator saves it themselves (*Export CSV…*,
  *Save report…*, *Save as .html…*).
- **The directory is read-only.** The window changes nothing in AD (a message is not a directory change). It reads the
  recipients and mailboxes through the session of its connection window – with its domain controller and account.
- **No passwords.** Outlook sends with its own profile and its own sign-in; AD Manager neither sees nor stores any mail
  password.

---

## 1. Requirements

- **Windows and classic Outlook** (Microsoft 365 Apps or Office 2016 or later, 32- or 64-bit) with a mail profile on the
  same computer as AD Manager. **The "new Outlook" and Outlook on the web have no automation** and will not work – the
  window then shows *Outlook: classic Outlook is not installed*.
- **The same privilege level.** AD Manager and Outlook must run the same way: both normally or both with *Run as
  administrator*. Windows does not let a program run as administrator connect to an Outlook run normally (the message
  *AD Manager runs as administrator and Outlook does not…*). The simplest is to run AD Manager without elevation. If it
  has to run as administrator, close Outlook first – AD Manager will start its own, which will also run as administrator
  until it is closed.
- **Outlook's protection against programs** (*Trust Center → Programmatic Access*). When the antivirus program is
  inactive or out of date, Outlook asks at the first send whether to allow the program to send messages on your behalf.
  This is its safeguard – allow it for the duration of the sending. A refusal ends with the message *Outlook refused to
  let a program send the message*. In a company the behavior of this prompt is set by an Outlook Group Policy.
- **Accounts in the profile.** *Send as* offers the accounts of the Outlook profile; another account has to be added to
  the profile first. *On behalf of* (a shared mailbox) requires the *Send on behalf* (or *Send as*) permission to that
  mailbox in Exchange.
- **A connection to AD** is needed only for recipients from the directory and for the *Mailboxes* tab. Files, the
  clipboard and sending also work after the connection window is disconnected.
- **The preview** in the window uses Microsoft Edge WebView2 Runtime (like the message preview of U1c); without it the
  HTML source is shown, and *Open in the browser* always works.

## 2. Opening the window

| From | What opens |
|---|---|
| *Tools → Correspondence (Outlook)…* (command palette: `tools.correspondence`) | one window per connection window, with the last message draft |
| *Action → Send correspondence…*, the list's context menu (`object.correspondence`) | a new window with the selected users and contacts as recipients from AD; a selected group gives its members |
| *Action → Ask mailbox owners…*, the list's context menu (`object.mailboxOwners`) | a new window on the *Mailboxes* tab, with the selected accounts as the scope |
| the *Mailbox owners…* button on the *Recipients* tab | shows the *Mailboxes* tab in the current window |

The window belongs to the connection window: it has its colored bar, and the title *Correspondence – contoso.test* gives
the domain. The tabs correspond to the steps of the work: *Mailboxes* (only when needed), **1 · Recipients**,
**2 · Message**, **3 · Preview**, **4 · Sending**, **5 · Responses**. The status bar shows the connection to Outlook
(*Outlook: connected (2 accounts)*) and the last message; *Help* shows a short guide.

On opening, the window connects to Outlook in the background: it attaches to a running Outlook or starts it (three
attempts). A missing Outlook is not an error of the window – it is visible only on the status bar, and *Connect to
Outlook* on the *Message* tab tries again. Closing the window during sending asks whether to interrupt it; on closing,
the last draft and the options are saved.

An Outlook that AD Manager had to start runs without a window until AD Manager is closed, also after the
*Correspondence* window is closed – otherwise it would close right after sending, and the last messages would stay in the
*Outbox*. An Outlook you started yourself is not closed by AD Manager.

## 3. Step 1 – Recipients

### Recipient sources

| Button | What it does |
|---|---|
| *Load CSV from Downloads* | the newest file matching the pattern `*kontakty*.csv` in the *Downloads* folder (also a redirected one, e.g. to OneDrive; includes the files `prawo-pl_kontakty_*.csv`) |
| *From a file (CSV, Excel, Word…)* | one or many files: CSV, TSV, TXT, XLSX/XLSM/XLTX, DOCX/DOTX/DOCM, ODT/ODS, RTF, HTM/HTML (up to 50 MB each) |
| *Paste from the clipboard* | a table copied from Excel or any text with addresses (from Word, a message, a web page); recognizes the notation "Jan Kowalski &lt;jan@x.pl&gt;" and the columns *Nazwa* + *E-mail* |
| *Add manually…* | a window to type or paste addresses (any text – the addresses are extracted from it) |
| dragging files onto the list | like *From a file* |
| *From the directory: Selected objects* | users, contacts and groups (their members) selected in the connection window |
| *Choose users…* | an object picker: users and contacts |
| *Group members…* | members of the chosen group, also nested ones and accounts for which it is the primary group (e.g. *Domain Users*; users and contacts, up to 10,000) |
| *Mailbox owners…* | the *Mailboxes* tab (chapter 8) |

Repeated addresses are removed – also against the recipients loaded earlier. The status bar summarizes every load, e.g.
*Read the file: 120 new, 3 duplicates, 2 invalid addresses*.

**How files and tables are read:**

- The table header is one of the first 8 non-empty rows – the first one under which the address column really contains
  addresses; in text the separator is `;`, a tab or `,` (in this order). A title above the table (e.g. a merged cell
  "List of e-mail addresses of the units" or a table caption in Word) and an introductory sentence ("Hello, the e-mail
  addresses below:") are not taken for the header; when no row fits, the whole is read as plain text.
- **The address column:** the header *E-mail*, *Email*, *Mail*, *Adres e-mail* or *E-mail address*, then *Poczta*, and
  last any header containing "mail" – the first such column that really contains addresses wins (*Inne e-maile* loses to
  *E-mail*). A cell with several addresses ("a@x.pl; b@y.pl") gives the first valid one.
- **The name column:** *Nazwa*, *Name*, *Instytucja*, *Urząd*, *Firma*, *Odbiorca*, *Adresat*, *Imię i nazwisko*,
  *Pełna nazwa* (also as part of a header); when there is none, and there are columns *Imię* and *Nazwisko* (*First
  name*, *Last name*), the name is "Imię Nazwisko".
- **Every column becomes a field** `{{Header}}` (chapter 4). An empty header gives *Kolumna N*, a repeated one –
  *Header (2)*.
- Rows without a valid address are counted as invalid. Text without a table: first the pairs "Name &lt;address&gt;" and
  "Name (address)", then single addresses.
- **Excel:** every sheet has its own header, and sheets with an address column are merged; cells go to columns by their
  address (A1, C1 …), so empty cells do not shift the columns. Sheets and rows that Excel does not show (hidden by hand,
  filtered out, in a collapsed group) are skipped and counted – the status bar says e.g. *hidden sheets skipped: 1,
  hidden rows skipped: 15*; in ODS files hidden sheets and rows are for now read. Dates and times go into the fields as
  dates, not as the numbers Excel stores them as – in Polish notation, regardless of the cell format ("15.03.2026",
  "15.03.2026 10:00", "10:00"); numbers with a format such as a postal code (`00-000`) or PESEL (Polish national ID
  number) (`00000000000`) – as Excel shows them ("95-020", a PESEL with a leading zero). Other numbers have the stored
  value. **Word, OpenDocument, HTML:** tables keep their columns, `mailto:` links give "text &lt;address&gt;", and text
  outside tables is read as plain text.
- Encoding of text files: a BOM, UTF-8, and otherwise the Windows code page (on Polish Windows – Windows-1250, as in CSV
  from Polish Excel).
- Addresses are checked more strictly than in Korespondencja WPiA (no "..", no dots or hyphens at the edges, with length
  limits), and junk addresses from web pages and documents (`example.*`, `*.sentry.io`, `*.w3.org`, `schemas.*`) are
  skipped. An address with an apostrophe or the character `&`, `#` or `=` before the "@" (e.g. `o'brien@…`,
  `Jan.O’Neil@…`, `biuro&sekretariat@…`) is rejected as a whole – it is never cut down to its tail (`brien@…`,
  `sekretariat@…`), which would be somebody else's address.
- A damaged file, a file over 50 MB or a suspicious archive (a so-called zip bomb – XLSX, DOCX and ODF are ZIP archives)
  is rejected with a message; the content of the file never reaches the message or the log.

### Recipients from AD

A recipient from the directory has the name from `displayName` (when it is missing – from the object name), the address
from `mail`, and when it is missing – the primary SMTP address from `proxyAddresses`; the *Source* column shows "AD". An
object without an address is on the list as invalid, with a description in the *Status* column. The values are read live
through the session of the connection window.

**Fields of recipients from the directory** (the section under the list) determine which columns – and so which message
fields – every recipient from AD gets. A field is a *Field name* and an *Expression* in the syntax of **messages to
users (U1)** – with single curly braces, with all attributes, functions and calculated fields of U1 and U1d:

| Field name (default) | Expression | Sample value |
|---|---|---|
| Imię | `{givenName}` | Łukasz |
| Nazwisko | `{sn}` | Wiśniewski |
| Nazwa wyświetlana | `{displayName}` | dr Łukasz Wiśniewski |
| Stanowisko | `{title}` | Kierownik |
| Dział | `{department}` | Księgowość |
| Login | `{sAMAccountName}` | lukasz.wisniewski |
| Zwrot osobowy | `{$salutation}` | Szanowny Panie Doktorze |
| Tytuł | `{$title}` | dr |
| Pan/Pani | `{$honorific}` | Pan |
| Pana/Panią | `{$honorific:biernik}` | Pana |
| Panu/Pani | `{$honorific:celownik}` | Panu |
| Szanowny/a | `{$byGender:Szanowny/Szanowna}` | Szanowny |

*Add field* appends your own, e.g. *Ostatnie logowanie* = `{$lastLogon:d MMMM yyyy}`, *Konto utworzone* =
`{$created:d MMMM yyyy}`, *Hasło wygasa* = `{$passwordExpires:d MMMM yyyy}`, *Jednostka* = `{$parentName}` or *Login
wielkimi* = `{sAMAccountName|upper}`. *Restore default* returns to the table above. Expressions with confidential
attributes (passwords, anything with `password`/`pwd`) are rejected, as in U1. Salutations use the settings *Settings →
E-mail messages → Salutations* (the table of titles, the list of first names, the behavior for an unknown gender). Only
the field definitions (name and expression) are saved, never the values.

### The recipient list

- Columns: *Send* (a check box – uncheck those you want to skip), *E-mail*, *Name*, *Source*, *Status*, *Sent before*,
  and after them the field columns (horizontal scrolling).
- The counter above the list, e.g. *Recipients: 120 • to send: 95 • skipped: 20 • invalid: 5*.
- The search box filters the list by address, name, source and fields (regardless of Polish characters).
- *Check all*, *Uncheck all*, *Remove selected*, *Clear the list* (with confirmation) and *Export CSV…* – a file in the
  chosen place with the columns *Name*, *E-mail*, *Source*, *Status* and the fields, with protection against Excel
  formulas.

### Do not send

Your own list of addresses to skip: *From a file…* (from any supported file), *Paste* (from the clipboard or typed) and
*Clear*. The list lives only in the memory of the window – as in Korespondencja WPiA it is empty after closing. The
counter next to it shows both reasons for skipping, e.g. *Skipped: 12 of your own + 340 from the journal*.

### The "already sent" journal

Every address to which a message was **sent** goes into the journal (a test to yourself and drafts – no). With *Skip
addresses already sent to* on (the default), recipients whose address is in the journal are skipped, and the *Sent
before* column shows the date and the subject as typed, e.g. *28.10.2026 · Weryfikacja skrzynki pocztowej: {{Skrzynka}}*.
To send again to the same people (e.g. a reminder), switch *Skip addresses already sent to* off.

**Rows from the *Mailboxes* tab are counted separately for each mailbox.** A mailbox owner's row is "already sent" only
if a message **about this mailbox** was sent to that person: the journal stores the hash of the owner's address together
with the mailbox address. A person to whom three mailboxes are assigned therefore gets three messages (three journal
entries), and when a fourth mailbox is added later, the message about it will not be skipped. An earlier message from
another run (e.g. an invitation from a file) does not skip the question about a mailbox – and vice versa. With one
message per owner (grouping) an entry is written for every mailbox on the list, and with sending in BCC – for every row
with its own mailbox.

*Journal of sent addresses…* shows the number of entries, the oldest and the newest entry and the retention period (365
days) and lets you delete entries older than this period or clear the whole journal. The journal **does not know the
addresses** – it keeps only their hashes (chapter 11), so it cannot be read to see whom something was sent to; for that
use Outlook's *Sent Items* and the sending report. The journal is shared by all AD Manager windows of this Windows user
and belongs to them: after moving the profile to another computer or signing in with another account the key cannot be
read – the window creates a new journal and keeps the previous one as `journal-<date>.old` and says so. The backup is
subject to the same retention period (older entries are removed from it, an empty backup disappears), and clearing the
journal removes the backups too.

## 4. Fields in the subject and body

In the subject, body, list item and custom fields, fields are written in double curly braces:

| Field | Value |
|---|---|
| `{{zwrot}}` | the salutation from the *Salutation* field on the *Message* tab, e.g. "Szanowni Państwo" – the same for everyone |
| `{{nazwa}}` | the recipient's name, e.g. "Urząd Gminy Andrespol"; when the recipient has no name (just an address) – the salutation, so the greeting is not empty |
| `{{email}}` | the recipient's address |
| `{{lista}}`, `{{liczba}}` | with grouping by address: the list of the recipient's rows and their number (chapter 5) |
| `{{podpis}}` | the Outlook signature chosen on the *Message* tab – exactly at this place of the body, without an empty line before it (chapter 5); without this field the signature goes under the body |
| `{{Any column}}` | the value of a column of the loaded table, e.g. `{{Miejscowość}}`, `{{Kod pocztowy}}`, `{{Telefon}}`, fields from AD (`{{Imię}}`, `{{Pana/Panią}}`, `{{Zwrot osobowy}}`) and mailbox fields |
| `{{Custom field}}` | the value of a custom field (below) |
| `{{Field\|text}}` | "text" when the field is empty, e.g. `{{Ostatnie logowanie\|brak danych}}` |

- The field name is written like a column header – with spaces and Polish characters. Letter case, spaces, "-", "_" and
  Polish characters do not matter: `{{Kod pocztowy}}`, `{{kodpocztowy}}` and `{{KOD-POCZTOWY}}` are the same field.
- *Insert field* (next to the subject and on the editor bar) suggests all available fields and inserts the chosen one at
  the cursor. Columns that repeat the built-in fields (*E-mail*, *Name*) are not suggested a second time.
- Built-in fields are checked first, then custom fields, and columns last – a custom field with the name of a column wins.
- In the body values are encoded as text (the characters `<` or `&` from the file will not change the look of the
  message), new lines in a cell give new lines in the body; in the subject control characters are replaced by spaces.
- A field split by formatting (e.g. Word made half of the name bold: `{{Miej`**`scowość`**`}}`) is joined before sending –
  within one paragraph.
- The *Preview* tab warns about a field name that is not in the data (it gives the list of available ones) and about a
  field that is empty for some recipients (*The field {{Miejscowość}} is empty for 12 of 95 recipients*). An unknown
  field gives an empty text in the message.

A typical beginning of a letter:

```text
{{zwrot}},
w nawiązaniu do działalności {{nazwa}} w {{Miejscowość}}…
```

**Custom fields** (the *Message* tab): a name and a value, the same for all recipients, e.g. *Telefon działu* = "22 555
01 00". The value may contain other fields (`{{nazwa}}`, columns), also other custom fields – e.g. *Link do systemu
zgłoszeń* = `https://{{System zgłoszeń}}`, so changing one field changes both places. A field that refers back to itself
through others is marked with an error (*the value refers back to itself*). Custom fields are saved in templates and in
the last draft – instead of the `{$input:…}` fields from messages to users.

**Date fields** (*Add date*): the date is chosen in a calendar, and the format – from a list or by typing your own (a
.NET date format: `d` – day, `MMMM` – month in words, `yyyy` – year, text in apostrophes). The default format `d MMMM
yyyy'r.'` gives "15 listopada 2026r." (the month in the genitive case, no space before "r."); next to it you can see how
the date will come out in the message. The list also has, among others, "15 listopada 2026 r.", "15.11.2026" and
"niedziela, 15 listopada 2026". A date field without a date blocks sending, and a date in the past is a preview warning.

**Two field syntaxes.** The Correspondence message uses `{{Field}}` – like Korespondencja WPiA, because columns such as
"Kod pocztowy" are not attribute names. The expressions of AD fields (chapters 3 and 8) have the syntax of messages to
users with single braces (`{givenName}`, `{$salutation}`), and in the body they are used by the field name: `{{Imię}}`,
`{{Zwrot osobowy}}`.

## 5. Step 2 – Message

### Sender

- **Send as** – an account from your Outlook profile. After connecting to Outlook the field shows the remembered
  account, and when there is none or it is missing from the profile – the profile's default account (the account of the
  current mailbox, and failing that the first one), so with *Outlook: connected* it is never empty. Only your choice is
  remembered: an account that is missing from the profile will come back when the profile has it again.
- **On behalf of (shared mailbox)** – optionally the address of the mailbox on whose behalf the messages go (requires
  the *Send on behalf* right). When Outlook does not accept this setting, the message ends with an error – it never
  goes out silently from another sender.
- *Connect to Outlook* and *Check Outlook* (chapter 13).

### Subject, salutation and body

- **Subject** – plain text with fields (*Insert field* next to it).
- **Salutation – field {{zwrot}}** – by default "Szanowni Państwo".
- **Font, Size, Line spacing** – the base style of the message: the font, size and spacing between lines of text that
  has no formatting of its own. By default Calibri 11 pt with line spacing 1.4; the built-in mailbox templates have
  Aptos 12 pt with single line spacing (like a new message in Outlook from Microsoft 365). Formatting entered in the
  body (e.g. text pasted from Word with its own font) takes precedence. The style is saved in the template and in the
  last draft.
- **Body** – an editor with a white background showing the text in the font and size of the message (as in Outlook),
  with Polish spell checking. The bar: bold (Ctrl+B), italic (Ctrl+I), underline (Ctrl+U), a bulleted and a numbered
  list, *Insert link…* (first select text within one paragraph; an address `https://…`, `mailto:`, `tel:` or a field,
  e.g. `{{Link}}`; a bare domain gets `https://`, an e-mail address – `mailto:`; an empty field removes the link),
  *Clear the formatting of the selection*, *Paste without formatting* (Ctrl+Shift+V) and the list of fields from
  *Insert field*.
- **Pasting from Word, Outlook or a browser (Ctrl+V)** keeps formatting, tables, lists and images. The content is
  cleaned the same way as in messages to users (U1c): Office styles written into the text, conditional comments and VML
  drawings removed, Word lists turned into lists, Safe Links links expanded. An image from the clipboard (e.g. a
  screenshot) is pasted as an image. Remarks on the pasting appear above the editor.
- **Images are embedded in the body** (`cid:`), not as attachments: PNG, JPEG or GIF checked by content and cleaned of
  metadata (EXIF with GPS location), up to 50 images and 10 MB per image, 25 MB per message in total. Images from the
  internet are never downloaded – paste the image together with the content.

### Signature

The *Signature* list shows the signatures from your Outlook (files from `%APPDATA%\Microsoft\Signatures`), with *(no
signature)* at the start; *Refresh* reads them again. The signature is appended under the body, with images embedded in
the message. Its styles are written into the text (web mail, e.g. Gmail, ignores `<style>` blocks), and images from the
internet and elements that mail will not show are removed – the window lists such problems under the list. `{{…}}`
fields in the signature are not filled in.

**A signature in a chosen place – the `{{podpis}}` field.** When the body contains `{{podpis}}`, the signature goes
exactly there and not under the body – without an empty line before it, e.g. right under "Z poważaniem" (like a
signature inserted in Outlook). It is best to write the field in a separate paragraph; in the middle of a paragraph the
paragraph is split, because a signature consists of separate paragraphs. A body with the `{{podpis}}` field and no
signature chosen has nothing at this place – the preview warns about it. The `{{podpis}}` field is always the signature,
even when the data has a column with this name.

### Attachments

*Add…* or dragging files onto the list; *Remove* and *Clear*. **Attachments go with EVERY message of the run** and stay
on the list until the window is closed (the list is not remembered). The heading shows the number and size of the files,
e.g. *Attachments (2, 1.4 MB)*, and stands out when the list is not empty. Above 15 MB of attachments the window warns,
and above 20 MB per message (images + attachments) the preview warns too. Remember: 2 MB × 300 messages = 600 MB in
*Sent Items* – with large files it is better to send a link.

### Sending

- **How to send:** *a separate message to each recipient* (recommended – the addressees do not see each other) or *one
  message, recipients in Bcc* – the *To* field holds your address and the recipients are in the hidden copy, at most
  *Recipients in Bcc per message* (1–500, default 500 – the Exchange Online limit). A Bcc message is one for everyone,
  so the column fields are empty in it (`{{nazwa}}` gives the salutation); such a message more often lands in spam.
- **CC (every message)** and **BCC (every message)** – addresses separated by `;` (also by a comma or a space), added
  in the copy and the hidden copy to every sent message and every draft, e.g. a copy to a department mailbox or an
  archive. They do not go into the test message or the "already sent" journal; an address typed in CC is not repeated in
  BCC. An entry that is not an address, and more than 50 addresses together, block sending. In Bcc mode the addresses
  from the BCC field are added to every batch, which is smaller by that many (the limit of 500 recipients per message).
  The fields are remembered like *On behalf of*.
- **Pause between messages (s)** – 0–300, default 3 s (chapter 7).
- **Request a read receipt.**
- **Deliver at a set time** – a day and an hour (e.g. `8:00`, `08:00` or `8`); at least a minute from now and at most a
  year ahead. Outlook hands the messages over at once, and they will be delivered at that time – AD Manager can be
  closed. With an Exchange or Microsoft 365 account the message waits on the server; with IMAP/POP accounts it waits in
  the *Outbox*, so Outlook must then be running.
- **Voting buttons (Outlook)** and *Answers to choose from, separated by semicolons* (default "Tak, nadal
  potrzebna;Nie, można usunąć"). The buttons are seen by recipients in Outlook and in Outlook on the web in an Exchange
  organization; other mail programs show a plain message, and the recipient simply replies. The votes are read by the
  *Responses* tab.

### Custom fields and grouping

- **Own fields** – chapter 4; *Add field*, *Add date* (a date field with a calendar and a format), *Remove*. A field
  with an error (e.g. an empty name or a date field without a date) blocks sending.
- **One message per address – rows as {{lista}}, their number as {{liczba}}.** Rows with the same address give one
  message. `{{lista}}` becomes a bulleted list: one bullet per row, according to the pattern *One item of {{lista}}*
  (e.g. `{{Skrzynka}} ({{Adres skrzynki}})`); `{{liczba}}` is the number of rows, and the other fields come from the
  first row. Write `{{lista}}` in a separate paragraph – the whole paragraph will be replaced by the list. In the
  subject `{{lista}}` gives bullets separated by commas. Without grouping every message has a list with one bullet.
- **The list item** is plain text, as in its field: the characters `<` or `&` go into the message literally, so the
  pattern `{{Skrzynka}} <{{Adres skrzynki}}>` will show the address in angle brackets. Without grouping, a column or a
  custom field named *Lista* or *Liczba* is an ordinary field and wins over the built-in field – *Insert field* then
  suggests exactly it.

### Templates

- **Template** – at the start of the list two built-in templates of the mailbox query (chapter 8): *Weryfikacja
  skrzynki pocztowej (wiadomość na każdą skrzynkę)* and *Weryfikacja skrzynek pocztowych (jedna wiadomość na osobę)*,
  then your own. Choosing a template with a non-empty body asks whether to replace it – unless the page holds a built-in
  template as it was loaded (subject, body, list item, font, voting and custom fields unchanged; then there is nothing
  to lose). The date chosen in the *Termin* field (and the values of other custom fields, e.g. *System zgłoszeń*) stays
  when switching to the other built-in template; another template would replace it, so the window asks first – likewise
  after any other change of a built-in template.
- **Template name** + *Save* saves the current message; *Delete template*; *New message* (after confirmation clears the
  subject, body and custom fields).
- A template contains the subject, the body with images, custom fields (with dates and date field formats), the list
  item, the salutation, voting buttons, grouping, and the font, size and line spacing – it contains no recipients,
  attachments or sender. *Save* under a new name creates a copy of a built-in template. A template saved in an earlier
  version has no font – after loading, the font, size and line spacing set in the window stay.
- **Save as the built-in template** (since 0.9.1) – choose a built-in template, change it (subject, body with images,
  custom fields, list item, voting, font) and save: your version replaces the built-in template on the list (*built-in,
  changed*) and on the *Mailboxes* page (*Add the owners as recipients*). The template keeps its name and grouping, and
  the date in the *Termin* field is still counted from the day of loading (today + 14 days). **Restore default**
  removes your version. Versions of built-in templates are not included in the export.
- *Export…* / *Import…* – a JSON file with the templates and their images (up to 20 MB) for a team; the import adds
  copies and checks the body and images again.
- **The last draft** (subject, body, images, custom fields, list item, salutation, voting, grouping, font) is saved when
  the window is closed and before every sending, and at the next opening it comes back as an empty message.

## 6. Step 3 – Preview

The preview shows the message exactly as the chosen recipient will see it: a card with the fields *From*, *To*,
*Subject* and *Attachments*, and under it the body with merged fields, images and the signature.

- **Show as seen by:** a choice of recipient (the first 500 messages). When one address gets several messages (a
  message per mailbox, several rows with the same address without grouping), the mailbox is shown next to the address
  (*Jan Kowalski <jan.kowalski@contoso.test> – rekrutacja@contoso.test*), and without a mailbox – the message number
  (*(2)*); each of them can be chosen, and the choice stays after a refresh. **Width:** desktop (700 px), phone (380 px)
  or full – as in the mailbox. The preview refreshes when you enter the tab and after every change of the choice;
  *Refresh* does it on demand. *Preview from Outlook*, *Open in the browser* and *Test to myself* use the chosen message.
- Under the bar: the number of embedded images and the fields used, and then the **warnings** – unknown fields (with the
  list of available ones), fields empty for some recipients, things that break in mail (scripts, images linked from the
  internet or stored as `data:`, absolute positioning, background images, `<style>` blocks, elements wider than 700 px,
  a link whose text shows a different domain than the target address – a typical sign of phishing) and a message that is
  too large. Without warnings you see *Looks good…*.
- Warnings about the message settings:
  - **a date in the past** in a date field used in the subject or body (*The field {{Termin}} has a date in the past:
    …*) – typically with a template saved long ago; choose a new date on the *Message* tab;
  - **`{{podpis}}` without a signature** – the body has the `{{podpis}}` field, and *(no signature)* is chosen on the
    *Signature* list;
  - **a row field in the subject with grouping** – with *One message per address* a subject with a column field (e.g.
    `{{Skrzynka}}`) gets the value only from the first row of the group; use the "a message per mailbox" template or a
    subject without this field.
- **Preview from Outlook** builds the message in Outlook (as for sending), reads the finished body with images from it
  and deletes the draft – it lands in *Deleted Items*. This is a "one to one" proof: you see the HTML the recipient will
  get. Outlook usually changes only the notation of quotation marks and dashes into entities (`&#8222;`, `&#8212;`).
- **Open in the browser** – the most faithful preview with a browser engine (Edge, Chrome), close to what someone
  reading mail in Gmail, Interia or WP will see. AD Manager serves the message **straight from memory** at the address
  `http://127.0.0.1:<port>/<random token>/`, available only from this computer, for 15 minutes (at most 20 previews at
  once) – nothing is written to disk.
- **Save as .html…** – you choose a folder; `podglad_wiadomosci.html` is created with the images next to it, e.g. for
  approval by a supervisor. This is an operator export: the file contains the data of the chosen recipient.

## 7. Step 4 – Sending

**Summary** (*Refresh*): the number of messages with skipped and invalid addresses, the sender, *on behalf of*, CC and
BCC of every message, how to send, the subject, the signature, attachments, delayed delivery, voting and the estimated
time.

| Button | What it does |
|---|---|
| *Test to myself* | one message to your address (the chosen account, and without a choice – the current Outlook mailbox; when there is none either, the window asks for an address), with the subject "[TEST] …" and the fields of the message chosen in the preview (e.g. about the second mailbox of the same person; without a choice – the first); it does not go into the journal |
| *Create drafts* | saves the messages in Outlook's *Drafts* – nothing is sent or written to the journal; if sent later by hand from Outlook, they will not go into the journal either |
| *Send* | sending after confirmation |
| *Stop* | interrupts after the current message |
| *Retry failed* | repeats the sending only to recipients with an error |
| *Save report…* | a text report (date, subject, the result for each recipient) in the chosen file |
| *Journal of sent addresses…* | as on the *Recipients* tab |

**The confirmation** lists the number of messages, the sender, *on behalf of*, CC and BCC, how to send, attachments,
skipped and invalid addresses and the delivery time, warns about a pause below 2 s and over 50 messages, and reminds you
that sending cannot be undone. Above 20 messages you must type their number.

**During sending** the bar shows the progress and the time to the end (*Sending 12 of 95 · about 4 min left*), the
counters *sent*, *failed* and *skipped*, and the *Who it went to* list – the result of each message; the *Status*
column of the recipient list shows *sent*, *draft*, *test sent* or *error – …*. The window responds all the time: the
conversation with Outlook runs on a separate thread, so *Stop*, scrolling and the tabs work normally.

- **Every message is first saved and only then sent.** When sending fails, the message stays in *Drafts* (the status says
  "saved in Drafts") – nothing is lost, and the report says at which step the error was. When the error occurs before
  saving, the unfinished message is discarded.
- An address that Outlook does not recognize ends the message with an error instead of sending.
- **Images** (from the body, pasted, from the template and from the signature) are attached to the message before its
  body, each with its own identifier (`cid:`), and after saving AD Manager checks in the saved message that every image
  the body points to is embedded in it. When Outlook lost this link, the body is set once more; when that does not help
  either, the message **is not sent without the images** – it stays in *Drafts* with the error "Outlook did not embed
  the images of the message" (since 0.9.1; earlier the images could arrive as a red X or a plain attachment).
- **After 5 errors in a row sending stops by itself** – this usually means Outlook was closed or the connection failed;
  after fixing it use *Retry failed*. Errors after which further sending makes no sense (Outlook closed, refusal of
  Outlook's protection, different privilege levels, a *Send as* account that is not in the profile – every next message
  would go from it) stop it immediately, with the message *■ stopped: …*.
- The AD Manager session log gets one entry per sending ("Correspondence (Outlook)", the number of successful ones out
  of all) – without addresses and without the subject.

### Microsoft 365 limits and large mailings (200–500 messages)

- Exchange Online accepts about **30 messages per minute**, **10,000 recipients per day** and **500 recipients in one
  message** (applies to Bcc mode; every Bcc addressee counts toward the daily limit). Exchange in a company may have
  other limits – ask the mail administrator.
- Leave a pause of 3 s – that is about 20 messages per minute, below the limit. One message takes Outlook about 1–3 s,
  so 300 messages take about 15–17 minutes; the bar shows the time to the end.
- Do not close Outlook and do not put the computer to sleep during sending.
- An attachment is added to **every** message (chapter 5).
- **The recipient list is not saved** (unlike in Korespondencja WPiA, chapter 11). After an interruption – also after
  closing the window – load the same file (or add the same recipients from AD) again: the addresses to which messages
  were already sent will be skipped thanks to the journal. So keep the source file until the sending is finished.

## 8. Mailboxes – a query to mailbox owners

In many companies the account of a shared mailbox (e.g. *Sekretariat IT*) has in the *First name* and *Last name* fields
(`givenName`, `sn`) the data of the person the mailbox is assigned to. The *Mailboxes* tab reads these data, finds
**that person's own account** and adds them to the recipients with the mailbox and owner fields – and the built-in
templates ask whether the mailbox is still needed.

### Where and which mailboxes

- **Where to look for mailboxes:** *Selected objects* (only after *Ask mailbox owners…*), *The whole domain* or *An
  organizational unit* (*Choose a unit…*, with the subtree).
- **Mailbox type** (by `msExchRecipientTypeDetails`): shared mailboxes (the default); shared together with rooms and
  equipment; all mailboxes; all user accounts in the scope. Remote mailboxes of a hybrid environment (Exchange Online)
  count toward their type.
- **Additional LDAP filter** – e.g. `(department=IT)`, combined with the mailbox filter by AND; the syntax is checked
  before searching.
- **A directory without the Exchange schema** (no `msExchRecipientTypeDetails` attribute): the window says so and offers
  only all user accounts in the scope – narrow them with an organizational unit of the mailboxes or an additional
  filter, e.g. `(description=Skrzynka*)`.

### Where the owner's name comes from

**The owner's name is read from:** the mailbox's `givenName` + `sn` (the default; one of the attributes may contain both
words), the display name, the description or a given attribute (*Attribute*; confidential attributes are rejected).
From the text, titles are removed ("dr", "prof.", "mgr inż." – the same table as in salutations), remarks in
parentheses and a prefix before ":" or " - " ("Skrzynka: Jan Kowalski" → Jan Kowalski). The last word is the surname,
the earlier ones are first names; a single word is a surname only.

### Where to look for people

- **Look for the owners in:** the whole domain (the default) or a chosen unit (*Choose a unit…*; *Whole domain* restores
  the domain). People are user accounts with a first name, a surname or a display name.
- AD Manager reads the people **once** (several attributes, in pages) and matches them in memory, because the domain
  controller compares with Polish characters ("Lukasz" ≠ "Łukasz"), and a query for each surname separately would not
  get around that. Limits: 20,000 mailboxes and 300,000 people – above that the window asks to narrow the scope.
- **Only an enabled account can be the owner** (the default): people with a disabled account stay candidates, but are
  never chosen automatically.
- The mailbox itself and other mailbox accounts (shared, rooms, equipment) are never candidates.

*Find owners* starts the search (progress: *Mailboxes: 120 · persons: 4,210*), *Stop* interrupts it.

### How AD Manager finds the owner

Every candidate gets a match level – the best one that fits:

| Level | Example (mailbox → person) | Points |
|---|---|---|
| exact | Jan Kowalski → Jan Kowalski (regardless of letter case) | 50 |
| without Polish characters | Lukasz Wisniewski → Łukasz Wiśniewski | 45 |
| swapped order | first name "Wójcik", surname "Małgorzata" → Małgorzata Wójcik | 35 |
| display name | Anna Nowak → a person with the name "dr Anna Nowak", "Nowak, Anna" or "Anna Nowak (IT)" | 30 |
| partial | a double-barreled surname (Anna Kowalska → Anna Kowalska-Nowak – the same first name), one of several first names, the surname alone | 15 |

To the points are added: **delegation** +40 (the mailbox points to the person in `msExchDelegateListLink` or
`publicDelegates` – confirmation that the person really uses the mailbox), enabled account +10, the person has an
address +5. The best level that occurred counts: one candidate at it is a match (exact or without Polish characters) or
a probable match (the other levels – to be checked); several candidates – with *Only enabled accounts become owners*
the only enabled account wins, and otherwise the result is ambiguous. A single candidate who is a delegate of the
mailbox always wins (at least at the *partial* level). With a probable match the person is chosen automatically (to be
checked) – except for the *partial* level without delegation: when only part of the surname, one of several first names
or just the surname matches, it may be someone else with a similar surname, so you have to choose the person from the
list yourself.

| Result | Meaning | What to do |
|---|---|---|
| matched | one person, exactly or without Polish characters | nothing |
| matched through delegation | the only candidate who is a delegate | nothing |
| probable | one person at a lower level (swapped order, display name or partial – with a partial match the person is not chosen automatically) | check, choose another if needed; with a partial match choose the person from the list |
| ambiguous | several people at the same level | choose a candidate from the list |
| not found | nobody fits | choose a person, type an address or exclude the mailbox |
| no name | the mailbox has no person's data | choose a person or change the source of the name |
| owner disabled | the chosen person has a disabled account | choose another person or exclude the mailbox |
| owner without an address | the person has neither `mail` nor an SMTP address | type an address or choose another person |
| chosen by hand, excluded | the operator's decision | – |

### Review and manual choice

The list columns: *Send*, *Mailbox*, *Mailbox address*, *Name in the mailbox*, *Match* (color: green – ready, yellow –
to be checked, red – no owner), *Owner* (a list of candidates, e.g. "Jan Kowalski – IT, jan.kowalski@…", delegates
marked), *Owner's address*, *Department*, *Actions*, *Location* and *Why* (level, delegation, disabled account). The
filter above the list shows all rows, ready, to review, not found or excluded, and the summary – e.g. *Matched: 90 · to
review: 12 · not found: 8 · excluded: 2*.

- *Send* is checked only where there is somebody to write to – with a chosen owner with an address. Rows without a name,
  not found, ambiguous or partially matched (nobody chosen), with a disabled owner (when only an enabled account can be
  the owner) or without an address have it unchecked. Choosing a candidate, *Choose the owner…* and *Type the owner's
  address…* check it themselves (a person without an address – uncheck it), unless you changed it by hand earlier: then
  it stays as you set it.
- Choosing another candidate from the list in the *Owner* column changes the row to chosen by hand.
- *Choose the owner…* – any user from the object picker.
- *Type the owner's address…* – an address typed by hand (checked). The address of one of the candidates chooses that
  person, for a person without an address the typed address fills it in, and an address of someone other than the chosen
  person removes them from the row – their name and salutation will not go to a foreign address (the person's fields
  stay empty, the preview says so).
- *Exclude* / *Include again* – the mailbox will not go to the recipients.

### Mailbox and owner fields

The *Fields of mailboxes and owners* section works like the fields of recipients from the directory (chapter 3), with an
additional choice: *field of the mailbox* or *field of the owner*. By default:

| Field | Expression | Whose |
|---|---|---|
| Skrzynka | `{displayName}` | mailbox |
| Adres skrzynki | `{mail}` | mailbox |
| Opis skrzynki | `{description}` | mailbox |
| Jednostka | `{$parentName}` | mailbox |
| Ostatnie logowanie | `{$lastLogon:d MMMM yyyy}` | mailbox |
| Utworzona | `{$created:d MMMM yyyy}` | mailbox |
| Login skrzynki | `{sAMAccountName}` | mailbox |
| Imię, Nazwisko, Nazwa wyświetlana, Stanowisko, Dział | `{givenName}`, `{sn}`, `{displayName}`, `{title}`, `{department}` | owner |
| Zwrot osobowy, Pan/Pani, Pana/Panią, Panu/Pani, Szanowny/a | `{$salutation}`, `{$honorific}`, `{$honorific:biernik}`, `{$honorific:celownik}`, `{$byGender:Szanowny/Szanowna}` | owner |

The mailbox's *Ostatnie logowanie* is a sign-in to the mailbox's **account** – the account of a shared mailbox is
usually disabled, and people open the mailbox with their own account, so the field is often empty and does not say
whether the mailbox is used (mailbox activity is known only to Exchange). When using it in a message, give a text for
a missing value, e.g. `{{Ostatnie logowanie|brak danych}}`.

### Add the owners as recipients

*Add the owners as recipients* moves the checked rows with an owner's address to the *Recipients* tab: the recipient's
address is the owner's address, the name – their display name, the fields – the mailbox and owner fields, the source –
"Skrzynki". Earlier rows with the source "Skrzynki" are replaced. By default **every mailbox is a separate message**: a
person with three mailboxes gets three messages, each with the name of its mailbox in the subject. After checking *One
message per owner (all their mailboxes)* grouping by address is switched on and that person gets one message with a
list of the three mailboxes.

When the body of the message is empty (with a subject typed – after a question) or the *Message* tab has a built-in
template as it was loaded (apart from the values of custom fields, e.g. the date in the *Termin* field), the built-in
template matching this choice is loaded: *Weryfikacja skrzynki pocztowej (wiadomość na każdą skrzynkę)* or *Weryfikacja
skrzynek pocztowych (jedna wiadomość na osobę)*. Adding again with the other choice swaps one built-in template for the
other and keeps the values of custom fields (the date from the *Termin* field, *System zgłoszeń*). A message that has
already been written or changed (even only the font, list item, voting or custom fields – e.g. a saved copy of a
template with voting) is not replaced by AD Manager.

**Built-in templates** reproduce the message the IT team had been sending from Outlook – the same text, spacing, colors
and tables. The font is Aptos 12 pt with single line spacing, without voting buttons (the answer is a plain reply to the
message):

- **Subject:** `Weryfikacja skrzynki pocztowej: {{Skrzynka}}`, and with one message per person *Weryfikacja skrzynek
  pocztowych*.
- **Body:** a request to confirm whether the mailbox named in the subject (or the mailboxes from the `{{lista}}` list,
  list item `{{Skrzynka}} ({{Adres skrzynki}})`) is still needed, and if it is to stay – to indicate in the ticketing
  system the person responsible and the people with access; the reply deadline `{{Termin}}` is in bold. At the end
  "Z poważaniem", the team name and **the Outlook signature in place of `{{podpis}}`** – choose it on the *Signature*
  list (chapter 5).
- **Termin** – a date field set to today + 14 days, in the format "15 listopada 2026r.". Before sending check the date
  on the *Message* tab – a date in the past is a preview warning.
- **System zgłoszeń** – a custom field with the address of the ticketing system, by default `hd.uni.lodz.pl`: the link
  text in the body. The link address is taken from the field *Link do systemu zgłoszeń* = `https://{{System zgłoszeń}}`,
  so with another ticketing system you change only one field (an address with another path or `http://` – type the
  whole of it in *Link do systemu zgłoszeń*).

A built-in template can be saved under another name (with your own ticketing system, deadline and font) and changed
further.

## 9. Step 5 – Responses

The tab reads the votes from Outlook's *Sent Items* – nothing is saved on this computer.

1. *Sent since* (by default 90 days back) and *Find in Sent Items* – the list of runs from the *Correspondence* window,
   e.g. *28.10.2026 · Weryfikacja skrzynek pocztowych · 95 msg.*
2. *Mailing* and *Show responses* – a table: *Recipient*, *Mailboxes* (which mailboxes the response concerns),
   *Response* (the button text or "no response") and *Answered*; a summary, e.g. *Responses: 40 of 95 · Yes: 30 ·
   No: 10*.
3. *Export CSV…* – the table to a file (with protection against formulas).

How it works: every message sent from the *Correspondence* window carries hidden properties in Outlook – the run
identifier and the addresses of the mailboxes it concerns. The votes are counted by the sender's Outlook on the sent
item, when the replies reach their *Inbox* (by default right on arrival). The *Sent Items* of the profile's default
mailbox are searched. Whoever replied with a plain message instead of clicking the button has "no response" – check the
*Inbox*. In Bcc mode your own address from the *To* field is not counted.

## 10. Messages to users (U1) through Outlook

Messages to users from templates (*Send message…*, U1) have a third way of sending: *Settings → E-mail messages → How
messages are sent → Through Outlook* (next to *Directly via SMTP* and *Through AdMng Watchdog*; *Automatic* still picks
Watchdog or SMTP, never Outlook). The *Outlook* section of the settings:

- **Send from the account** – the SMTP address of an account from the Outlook profile (empty – the default account);
  *Get the accounts from Outlook* suggests the accounts of the profile.
- **Send on behalf of the sender address above** – the sender address from the settings goes into the *On behalf of*
  field (the account needs the *Send on behalf* permission to that mailbox).

Templates, the footer, the U1 limitations (no attachments, the allowed-domains list of links), preview, test, report and
"Retry failed" work unchanged; the sending window shows *Sending through Outlook: …* with the account. The requirements
from chapter 1 apply (classic Outlook, the same privilege level, Outlook's protection), and the result in the report is
*Sent through Outlook.* or an error description from chapter 13.

## 11. What is saved to disk and what is not

| What | Where | Content |
|---|---|---|
| window settings | `%APPDATA%\AdMng\settings.json`, section `correspondence` | subject, salutation, account, *on behalf of*, signature name, pause, how to send, read receipt, skipping sent, grouping, voting, delivery hour, preview width, the pattern of the file from *Downloads* (`downloadsPattern`), the confirmation threshold (`confirmationThreshold`, 20), the journal retention period (`journalRetentionDays`, 365 days); definitions of AD fields and mailbox fields (name + expression); the options of *Mailboxes* (type, LDAP filter, source of the name, attribute, the place of people as a portable DN `{domain}`) |
| templates and the last draft | `settings.json` and images in `%APPDATA%\AdMng\message-images` (shared with the U1 templates) | operator content: subject, body with fields, images, custom fields (date fields with the date and format), list item, font, size and line spacing, options |
| the "already sent" journal | `%APPDATA%\AdMng\correspondence\journal.txt` | one row per sent address: the **address hash** (HMAC-SHA-256 with a key, the first 16 bytes; for rows from *Mailboxes* – the hash of the owner's address together with the mailbox address, and when the mailbox has no address, with its DN), the send time, the subject as typed (without the fields filled in) |
| the journal key | `…\correspondence\journal.key` | 32 random bytes encrypted with DPAPI for the current Windows user |
| the journal lock | `…\correspondence\journal.lock` | an empty file – several windows and runs of AD Manager write the journal one after another |
| backup of an old journal | `…\correspondence\journal-<date>.old` | a journal set aside when its key could not be read (chapter 13): address hashes, times and subjects as in the journal; the same retention period applies to it (older entries are removed, an empty backup disappears), and clearing the journal removes the backups too |
| images for Outlook | `%TEMP%\AdMng-Outlook-<process>-<random>` | images of the body and signature (Outlook accepts images only from a file); removed right after the message is saved, leftovers after a crash – at the next start |
| exports on demand | a place chosen by the operator | the recipient list (CSV), the report, the `.html` preview with images, responses (CSV), templates (JSON) |

**Not saved:** the recipient list and its fields, values from AD, the *Do not send* list, attachments, the preview, the
sending results (without *Save report…*), the rows and matches of *Mailboxes*, and responses. Diagnostic logs contain no
addresses, names, subjects or bodies; the session log – only the entry about sending with the number of messages. The
messages themselves are in the Outlook mailbox (*Sent Items*, *Drafts*, and after *Preview from Outlook* – *Deleted
Items*), like any mail sent from Outlook.

**Why the recipient list does not save itself.** Korespondencja WPiA saved `odbiorcy.csv` with statuses every 25
messages so that an interrupted sending could be finished. AD Manager does not save directory data to disk (rule 1):
recipients from AD and their fields are directory data, and files with addresses are personal data. Finishing is
ensured by the journal: load the file again, and the addresses to which messages were already sent will be skipped. The
journal itself contains neither addresses nor mailbox names – even with the key one can only check whether a message
to a **known** address (about a known mailbox) was already sent, not read a list of addresses. This is the only
exception of stage U5 to the rule "no directory data on disk" ([ADR-0003](adr/0003-no-local-data.md)).

## 12. Differences from Korespondencja WPiA

- **One window in AD Manager** instead of a separate program – for every connection window. The settings are in AD
  Manager's `settings.json`; the files `%APPDATA%\MailerWPiA` (`ustawienia.txt`, `tresc.xamlpkg`, `wyslane.csv`) are not
  read. The `wyslane.csv` journal contains addresses in plain text and is not carried over – if you need to skip
  addresses sent by the old program, load it through *Do not send → From a file…*.
- **New:** recipients from AD (selection, picker, group members) with fields from the directory and salutations, custom
  fields, `{{Field|text}}`, templates with export and import, grouping by address (`{{lista}}`, `{{liczba}}`), voting
  buttons and the *Responses* tab, the *Mailboxes* tab, the journal retention period, confirmation by typing the number
  above 20 messages, a configurable number of Bcc recipients (1–500).
- **The journal** keeps address hashes instead of addresses; *Journal of sent addresses…* shows statistics instead of
  opening a CSV file with addresses. The recipient list is not saved every 25 messages (chapter 11).
- **Files:** XLSX is read by cell addresses (the old program shifted columns after an empty cell), every sheet with its
  own header; DOCX and ODT tables keep their columns; from a cell with several addresses the first valid one is taken;
  the columns *Imię* + *Nazwisko* give the name; stricter address checking; size limits and protection against zip
  bombs.
- **Body and signature:** the body HTML is built from a limited, safe set of tags – scripts and dangerous links will not
  pass; signature styles are written into the text, and images are embedded through `cid:`; images are checked and
  cleaned of metadata, with limits. Fields split by formatting are joined only within a paragraph, and `&nbsp;` becomes a
  space.
- **More careful sending:** an address not recognized by Outlook, a failed setting of *on behalf of*, of the recipient
  type (BCC) or of the sender account ends the message with an error – the old program in such cases sent on (e.g.
  from the default account). A message unfinished before saving is discarded.
- **Browser preview** straight from memory (no temporary file); *Preview from Outlook* unchanged.
- **Diagnostics:** *Check Outlook* instead of `MailerWPiA.exe --diag`, with additional information about the journal, the
  *Downloads* folder and the signatures folder; a readable message for different privilege levels. There are no
  `--selftest` and `--shot` parameters.

## 13. When something does not work

**Check Outlook** (the *Message* tab) shows an environment report: the user, the computer and the bitness of the
process, the Outlook version, the profile, the accounts, the current mailbox, signatures, the result of an attempt to
create a message (without sending), the journal file and the number of entries, the *Downloads* folder and the
signatures folder. *Copy* moves the report to a ticket (it contains the addresses of your accounts).

| Message (code) | What to do |
|---|---|
| *Classic Outlook works on Windows only.* (`outlook.notSupported`) | – (applies only to tests and builds on Linux) |
| *Classic Outlook was not found…* (`outlook.notInstalled`) | install classic Outlook (Microsoft 365 Apps); the "new Outlook" and Outlook on the web have no automation |
| *AD Manager runs as administrator and Outlook does not…* (`outlook.elevated`) | run AD Manager without *Run as administrator* or close Outlook before connecting |
| *Outlook is busy (an open dialog?)…* (`outlook.busy`) | close Outlook's dialogs (e.g. the profile choice, a password, a reminder) and try again |
| *Outlook does not respond…* (`outlook.notResponding`) | check whether Outlook is waiting for a password or a profile choice; set a default profile |
| *Outlook refused to let a program send the message…* (`outlook.denied`) | allow it in the Outlook window; check the antivirus program (an up-to-date one switches the prompts off) or the *Programmatic Access* Group Policy |
| *The connection to Outlook was lost…* (`outlook.disconnected`) | start Outlook, *Connect to Outlook*, then *Retry failed* |
| *Outlook did not recognize the recipient.* (`outlook.recipient`) | correct the address (a typo, a nonexistent address in the organization) |
| *An attachment file was not found.* (`outlook.attachment`) | the file was moved or deleted after it was added – add it again |
| *The chosen account is not in the Outlook profile.* (`outlook.account`) | sending stops at the first message – choose an account from the *Send as* list or add it to the profile |
| *The message is too large for the mailbox or the server.* (`outlook.tooLarge`) | reduce attachments or images, send a link |
| *Cancelled.* (`outlook.cancelled`) | the sending was stopped – *Retry failed* will finish it |
| *Unknown Outlook error.* (`outlook.error`, with the code `0x…`) | give the code and the step from the report in the ticket; the text of Outlook's message is not saved, because it may contain addresses |

Other situations:

- *The key of the "already sent" journal could not be read…* – another Windows user or a moved profile: the window
  created a new journal and kept the old one (`journal-<date>.old`, for the journal's retention period). Skips from the
  previous journal do not work – before sending again use *Do not send* or the report.
- *The "already sent" journal cannot be used…* – sent addresses will not be skipped; check the permissions to the folder
  `%APPDATA%\AdMng\correspondence`.
- *Load CSV from Downloads* finds nothing – the file must match the pattern `*kontakty*.csv`; the *Downloads* folder is
  shown by *Check Outlook*.
- Messages wait in the *Outbox* – Outlook is working offline or is waiting for send/receive.
- The preview shows the HTML source instead of the message – the WebView2 runtime is missing; use *Open in the browser*.
- The signature list is empty – Outlook did not save signatures in `%APPDATA%\Microsoft\Signatures` (signatures stored
  only in the cloud are not available).

## 14. Pilot checklist

This cannot be checked in CI (no Outlook and Exchange). To be confirmed on a real classic Outlook:

- [ ] Microsoft 365 Apps in the 32- and 64-bit versions; Exchange Online and Exchange in a company.
- [ ] Attaching to a running Outlook and starting Outlook when it is not running.
- [ ] Profile accounts and *Send as* from another account.
- [ ] *On behalf of* a shared mailbox (the message, *Sent Items*, replies).
- [ ] Embedded images (body and signature) visible in the body in Outlook, Outlook on the web and Gmail.
- [ ] Attachments.
- [ ] Delivery at a set time (Exchange and possibly IMAP).
- [ ] Read receipt.
- [ ] Voting buttons and reading the votes on the *Responses* tab (the sending date filter in Outlook).
- [ ] Outlook's protection prompt on a computer without an up-to-date antivirus (consent and refusal).
- [ ] AD Manager run as administrator with a normal Outlook – the message `outlook.elevated`.
- [ ] Only the "new Outlook" – the message `outlook.notInstalled`.
- [ ] 300 messages with a 3 s pause: time, the window responds, *Stop*, no rejections because of limits.
- [ ] Real `msExchRecipientTypeDetails` values in a hybrid environment (remote mailboxes) and the delegations
  `msExchDelegateListLink`.
- [ ] Messages to users (U1) through Outlook.
- [ ] The editor: pasting from the real Word and Outlook clipboard, the dark theme, consistency of the body after saving a template.
- [ ] Files from Polish Excel (CSV in Windows-1250, XLSX), the *Downloads* folder redirected to OneDrive.
- [ ] *Open in the browser* in Edge and Chrome; *Save as .html* opened on another computer.
- [ ] The journal with two AD Manager windows sending at the same time.
- [ ] The "no directory data on disk" test after sending and after querying mailbox owners (apart from the hash journal).
