# U5 – Correspondence through Outlook ("Korespondencja") and mailbox owner queries – design note

Status: binding for the U5 implementation tracks (section 13). Product documentation: `docs/KORESPONDENCJA.md`
(Polish). Roadmap: README "Ścieżka U", item U5.

## 0. What and why

The operator asked to bring the standalone program **Korespondencja WPiA** (`MailerWPiA.exe`, .NET Framework 4.8 +
classic Outlook COM, ~4 200 lines) into AD Manager, with **all** its functions available from the snap-in and sending
through **Outlook**, and to add **mailbox owner queries**: mailbox accounts whose `givenName`/`sn` hold the name of
the person the mailbox is assigned to are matched to that person's own AD account, and the person gets a customizable
message ("is this mailbox still needed?") – sent exactly the way the program sends.

Three parts:

| Part | Content |
|---|---|
| **U5a – Outlook engine** | New library `AdMng.Outlook` (net10.0, Windows at run time): classic Outlook through COM late binding on a dedicated STA thread – connect (attach or start), accounts and profile, send / save as draft, inline images by content id, attachments, send-as account, on behalf of, read receipt, deferred delivery, voting buttons, preview built by Outlook, diagnostics ("Sprawdź Outlooka"), reading voting responses. Also an **Outlook transport for the existing U1 messages** (`UserMessageTransportKind.Outlook`). |
| **U5b – Korespondencja window** | The whole MailerWPiA workflow as a per-connection AD Manager window: recipients from files (CSV/TSV/TXT, XLSX/XLSM/XLTX, DOCX/DOTX/DOCM, ODT/ODS, RTF, HTML), the clipboard, typed text, the newest contacts file in *Pobrane*, drag and drop – and, new, **from AD** (selected objects, picked users, group members); every column is a `{{field}}`; built-in `{{zwrot}}`, `{{nazwa}}`, `{{email}}`; "Nie wysyłać" list; journal "już wysłane"; WYSIWYG editor (bold, italic, underline, lists, links, clear formatting, paste plain, paste from Word/Outlook with images); Outlook signatures with images; attachments; send-as account, on behalf of; deferred delivery; read receipt; one message each or BCC; delay; test to self; drafts; send; stop; retry failed; stop after 5 errors in a row; ETA; preview as the recipient sees it (700 / 380 px / full), "Podgląd z Outlooka", open in the browser, save as .html, mail compatibility warnings; report. New on top: custom fields, saved templates, grouping by address, AD fields as U1 template expressions. |
| **U5c – Mailbox owner queries** | A *Skrzynki* tab of the same window: choose mailbox accounts (scope + type + filter), parse the owner's name from them, find the owner's account (diacritics, swapped order, titles, compound surnames, delegate links as confirmation), review (unique / ambiguous / not found / disabled / no address – pick a candidate, pick any user, type an address, exclude), then add the owners as recipients – one message per mailbox or one per owner listing all their mailboxes – with fields of both the mailbox and the owner. A built-in template asks whether the mailbox is still needed, optionally with Outlook voting buttons; the *Odpowiedzi* tab reads the votes back from *Elementy wysłane*. |

### 0.1 Decisions

| # | Decision | Why |
|---|---|---|
| D1 | Field syntax of Korespondencja stays MailerWPiA's `{{Pole}}` (any column name, spaces and diacritics allowed), plus `{{Pole|tekst gdy puste}}`. U1 messages keep `{attr}`/`{$token}`. | The operator's existing templates and habits; column names like "Kod pocztowy" are not LDAP names. |
| D2 | Values of AD rows are produced by **U1 template expressions** per field (`{givenName}`, `{$salutation}`, `{$honorific:biernik}`, `{$lastLogon:d MMMM yyyy}` …) evaluated with `UserMessageFields`, then used as plain column values. | Reuses every attribute, function, date and salutation of U1/U1d; the operator chooses the variables. |
| D3 | Outlook is reached through `dynamic` late binding (no PIA), all calls on one STA thread per session with a COM message filter. The code that touches the object model is written against `dynamic`, so the tests drive it with plain CLR fakes on Linux. | No Office package to ship; testable without Outlook (CI has none). |
| D4 | Rule 1: the journal "już wysłane" stores only **keyed hashes** of addresses (HMAC-SHA-256 with a per-user random key protected by DPAPI), the time and the subject as typed (fields not filled in). The recipient list is **not** saved automatically (MailerWPiA's `odbiorcy.csv`); after an interruption the operator re-imports the file and the journal skips what was sent. Body drafts and templates (operator content) are kept in the settings. | AD Manager never writes directory values to disk (CLAUDE.md rule 1, amended for the journal in section 2). |
| D5 | Previews never touch the disk: WebView2 from memory (as U1c), "Otwórz w przeglądarce" through a one-shot loopback HTTP server bound to 127.0.0.1 with a random token. "Zapisz jako .html" is an operator export (allowed). Images handed to Outlook (`Attachments.Add` needs a path) are written to a private temp folder and deleted right after the item is saved – they are operator content, never directory data. | Rule 1. |
| D6 | The editor is a WPF `RichTextBox` with an HTML round trip limited to the sanitized subset (section 9.2); pasted Office HTML goes through the U1c importer (sanitizer, Word lists, Office temp images) and is inserted as a fragment. | Same experience as MailerWPiA, everything else in the app is WPF; works without WebView2. |
| D7 | Korespondencja sends through Outlook only (as the original). U1 messages gain Outlook as a third transport choice. Attachments, BCC, deferred delivery, voting and signatures exist in Korespondencja only; U1 keeps its "no attachments" rule. | Scope; U1's footer/condition model stays intact. |
| D8 | No automatic rate limiter beyond MailerWPiA's delay (default 3 s) and the M365 hints; BCC is split into chunks of at most 500 recipients (configurable 1–500). | The original behaviour; 500 recipients per message is the Exchange Online limit. |
| D9 | Owner matching reads the persons of the chosen base once into memory (paged search, few attributes) and matches locally with diacritic folding. | The DC compares accent-sensitively (`Lukasz` ≠ `Łukasz`); per-name LDAP queries cannot fold. |
| D10 | Nothing is written to the directory by U5 (rule 5 does not apply). | Messages are not directory changes. |

### 0.2 Deliberate differences from MailerWPiA

- No `%APPDATA%\MailerWPiA` files: settings live in AD Manager's `settings.json`, the journal in its own folder as hashes,
  the recipient list is not auto-saved (D4). "Otwórz dziennik" shows statistics and per-row "wysłano wcześniej" marks
  instead of opening a CSV with addresses.
- `.xlsx` reading honours cell references (MailerWPiA shifted columns after empty cells) and reads each sheet with its own
  header; `.docx`/`.odt` tables keep their columns (MailerWPiA flattened them); an e-mail cell with several addresses
  takes the first valid one; a header pair "Imię" + "Nazwisko" makes the name.
- Signatures are CSS-inlined and sanitized (webmail drops `<style>`), images embedded by content id.
- HTML of the body is produced from the editor's sanitized subset; scripts and dangerous URLs cannot get in.
- Inline images are limited (section 3.6) and validated (PNG/JPEG/GIF, metadata stripped) as in U1c.

## 1. Rules (CLAUDE.md) applied

1. **No local directory data:** recipients, AD values, previews, plans, reports and mailbox matches exist in memory only.
   On disk: settings (`CorrespondenceSettings`: options, templates, last draft, field definitions, mailbox options –
   names, LDAP filters and portable DNs only), template images (`message-images`, operator content), the journal
   (keyed hashes of addresses – for mailbox owner queries, of the address together with the mailbox's address or DN;
   section 7.6), Outlook temp images (deleted at once, D5), and files the operator exports (recipient CSV, report,
   preview `.html`, responses CSV). Logs never contain addresses, names, subjects or bodies.
2. **No passwords:** none are involved (Outlook uses its own profile). `ToString()` of records with addresses returns
   type names / counts only.
3. **Escaping:** all filters with `LdapFilter`; the operator's extra mailbox filter is validated with
   `LdapFilter.Validate` and wrapped with `LdapFilter.And`; DNs with `DistinguishedName`.
4. **One DC:** every read uses the window's session (`RequireSession()`).
5. **Writes:** none.
6. **UI never blocks:** directory reads stream with cancellation; every Outlook call runs on the session's STA thread
   and is awaited; imports parse on the thread pool; the recipients grid virtualizes.

## 2. CLAUDE.md amendment (rule 1, allowed on disk)

Add to "Allowed on disk" of rule 1: *"the Korespondencja journal (U5): keyed hashes of recipient addresses (for mailbox
owner queries: of the address together with the mailbox's address or DN) – HMAC-SHA-256 with a per-user key protected
by DPAPI – send times and the subjects as typed – no address, name or other directory value"*. Layout table rows:
`src/AdMng.Outlook` and `tests/AdMng.Outlook.Tests`.

## 3. Core: `src/AdMng.Core/Correspondence` (pure, net10.0)

Namespaces `AdMng.Core.Correspondence.*`. Everything is pure and deterministic; file bytes come in as arguments.

### 3.1 Addresses and contact import – `Correspondence/Import`

```csharp
namespace AdMng.Core.Correspondence.Import;

/// One recipient found in a file or text: address (normalized, lower case), name (may be empty) and the cells of its
/// table row by column name (empty for free text).
public sealed record ImportedContact(string Email, string Name, IReadOnlyDictionary<string, string> Fields)
{ public override string ToString() => nameof(ImportedContact); }

public enum ContactSourceFormat { Text, Delimited, Excel, Word, OpenDocument, Rtf, Html }

public sealed record ContactImportResult(
    IReadOnlyList<ImportedContact> Contacts,   // de-duplicated by Email, file order
    IReadOnlyList<string> Columns,             // column names in table order (empty for free text)
    int InvalidAddresses,                      // table rows whose e-mail cell held no valid address
    int Duplicates,                            // repeated addresses dropped
    ContactSourceFormat Format)
{ public static ContactImportResult Empty { get; } ...; public override string ToString() => $"{Contacts.Count} contacts"; }

public static class EmailAddressText
{
    public static bool IsValid(string? address);          // MailerWPiA's pattern + TLD 2–63 + not junk (example.*, sentry.io, schemas.*, w3.org)
    public static string Normalize(string? address);      // trims "mailto:", "?…", punctuation and quotes; lower-invariant
    public static IReadOnlyList<string> Extract(string? text); // every address in order, normalized, valid, de-duplicated
    public static string CleanName(string? name);         // whitespace collapse, "Kontakt: X" → "X", trims bullets/dashes/quotes, "" if it is an address, max 120
    public static string? FirstValid(string? cell);       // first valid address of a cell such as "a@x.pl; b@y.pl"
}

public static class ContactExtractor
{
    public static ContactImportResult FromText(string? text);                         // table first, then "Name <addr>", then bare addresses
    public static ContactImportResult FromRows(IReadOnlyList<IReadOnlyList<string>> rows, ContactSourceFormat format); // a sheet/table
}

public static class ContactFileReader
{
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public static IReadOnlyList<string> Extensions { get; }   // .csv .tsv .txt .xlsx .xlsm .xltx .docx .dotx .docm .odt .ods .rtf .htm .html
    public static bool IsSupported(string fileName);
    /// Reads one file. Text files: BOM / strict UTF-8 / the ANSI code page (Windows-1250 on Polish Windows).
    public static ContactImportResult Read(string fileName, ReadOnlySpan<byte> content, int ansiCodePage);
}

public static class TextDecoding { public static string Decode(ReadOnlySpan<byte> bytes, int ansiCodePage); } // registers CodePagesEncodingProvider
```

Table detection (`FromRows` and the delimited text path, delimiters `;`, tab, `,` in this order, header within the
first 8 rows):

- **E-mail column:** a header equal (folded, see 3.2 `FieldKey`) to `email`, `e-mail`, `mail`, `adres e-mail`,
  `adres email`, `poczta`, `e-mail address`; otherwise the first header *containing* `e-mail`/`email`/`mail`
  (MailerWPiA's rule). "Inne e-maile" therefore loses to "E-mail".
- **Name column:** first header equal to or containing `nazwa`, `name`, `instytucj`, `urząd`/`urzad`, `firma`,
  `odbiorca`, `adresat`, `imię i nazwisko`/`imie i nazwisko`, `pełna nazwa`; else, when both `imię`/`imie`/`first name`
  and `nazwisko`/`surname`/`last name` exist, Name = "Imię Nazwisko".
- Every column becomes a field under its header (trimmed; empty → `Kolumna N`; a repeated header → `Header (2)`).
- The e-mail cell takes `FirstValid`; rows without one count as `InvalidAddresses`.
- Free text: `Name <addr>` / `Name (addr)` pairs (MailerWPiA's `Named` pattern), then bare addresses.

Formats:

| Format | Rule |
|---|---|
| CSV/TSV/TXT | decode, `FromText` |
| XLSX/XLSM/XLTX | `System.IO.Compression` + `XmlReader`: `xl/workbook.xml` + rels give sheet order; `sharedStrings.xml` (all `<t>` of an `<si>`, rich runs joined); cells placed by their `r` reference (A1 → column), inline strings, `t="str"`/numbers as text, booleans `TRUE/FALSE`. Each sheet is a table on its own; results of sheets that have an e-mail column are concatenated (de-duplicated). |
| DOCX/DOTX/DOCM | `word/document.xml`: `w:p` → line, `w:tab` → tab, `w:br` → line; `w:tbl` rows → cells joined by tab (nested tables flattened); then `FromText`. |
| ODT/ODS | `content.xml`: `text:p`/`text:h` → line, `table:table-row` → cells joined by tab with `table:number-columns-repeated` (capped at 256), `text:s`/`text:tab`/`text:line-break`. |
| RTF | own small reader: groups, `\par`/`\line` → newline, `\tab`/`\cell` → tab, `\row` → newline, `\'hh` via `\ansicpgN` or the ANSI code page, `\uN?` Unicode, skip `{\*…}`, `\fonttbl`, `\colortbl`, `\stylesheet`, `\info`, `\pict`. |
| HTML | AngleSharp: every `<table>` → rows (`th`/`td` text); `mailto:` links → "text <addr>"; remaining body text → `FromText`. |

Size: files above `MaxFileBytes` are refused (`InvalidDataException`); XML readers use `DtdProcessing.Prohibit`, no
external resolution; zip entries above 200 MiB uncompressed or a compression ratio above 100 are refused (zip bomb).

### 3.2 Fields – `Correspondence/Fields`

```csharp
namespace AdMng.Core.Correspondence.Fields;

public static class FieldKey { public static string Of(string? name); public static bool Same(string? a, string? b); }
  // fold: trim, collapse whitespace, lower-invariant, diacritics removed, '-', '_' and spaces removed ("Kod pocztowy" == "kodpocztowy")

public static class BuiltInFields
{
    public const string Salutation = "zwrot";     // the window's "Zwrot grzecznościowy" (default "Szanowni Państwo")
    public const string Name = "nazwa";           // row name, else the salutation (MailerWPiA)
    public const string Email = "email";
    public const string List = "lista";           // grouped messages (section 3.4)
    public const string Count = "liczba";         // grouped messages: number of rows
    public const string Signature = "podpis";     // the Outlook signature where the body places it (section 7.7); "" elsewhere
    public const string DefaultSalutation = "Szanowni Państwo";
    public static IReadOnlyList<string> All { get; }            // zwrot, nazwa, email, lista, liczba, podpis
    public static bool IsBuiltInColumn(string column);         // nazwa/name/email/e-mail/mail/adres e-mail/zwrot – hidden from "Wstaw pole" as duplicates
}

public sealed record FieldToken(string Field, string? Fallback, int Start, int Length);

public enum FieldEncoding { Text, Html, Header }   // Html: HtmlEncode + line breaks → <br>; Header: control chars → space

public static class FieldTemplate
{
    public const int MaxFieldNameLength = 80;
    public static IReadOnlyList<FieldToken> Parse(string? template);        // {{ Pole }} or {{ Pole | tekst }}; no nesting; "{{" without "}}" is literal
    public static IReadOnlyList<string> UsedFields(string? template);       // distinct by FieldKey, first spelling
    public static string RepairHtml(string? html);                          // joins tokens split by tags/&nbsp; (MailerWPiA RepairTokens), never inside attribute values
    /// lookup returns null for an unknown field, "" for a known empty one.
    public static string Merge(string? template, Func<string, string?> lookup, FieldEncoding encoding,
        ICollection<string>? unknown = null, ICollection<string>? empty = null);
}

public enum CustomFieldKind { Text, Date }

/// Text: Value may use {{other fields}}, other custom fields included (a loop renders "" there, at most
/// RecipientFieldSet.MaxCustomDepth = 8 nested; the window flags a loop). Date: Value is "yyyy-MM-dd" (invariant), Format a .NET
/// custom date format (null/blank → CustomFieldFormats.DefaultDate), rendered with the Polish culture; an empty or
/// unparsable date renders "".
public sealed record CustomFieldDefinition(string Name, string Value, CustomFieldKind Kind = CustomFieldKind.Text, string? Format = null);

public static class CustomFieldFormats
{
    public const string DefaultDate = "d MMMM yyyy'r.'";            // 15 listopada 2026r. (genitive month after a day)
    public static IReadOnlyList<string> DateChoices { get; }        // DefaultDate, "d MMMM yyyy 'r.'", "d MMMM yyyy", "dd.MM.yyyy", "dd.MM.yyyy 'r.'", "dddd, d MMMM yyyy", "yyyy-MM-dd"
    public static string FormatDate(DateOnly date, string? format); // pl-PL; blank, too long (> 64) or invalid (time parts, unclosed quote) → DefaultDate
    public static string DateText(CustomFieldDefinition field);     // the formatted date, "" without one
}

/// Field values of one recipient (or group): built-ins, custom fields, then columns (FieldKey matching).
public sealed class RecipientFieldSet
{
    public RecipientFieldSet(string email, string name, IReadOnlyDictionary<string, string> columns, string salutation,
        IReadOnlyList<CustomFieldDefinition> customFields, IReadOnlyCollection<string> knownColumns,
        string? list = null, int? count = null);
    public string? Lookup(string field);   // null = unknown field
}
```

Lookup order: `zwrot` → salutation (blank → default); `nazwa`/`name` → row name, else the salutation; `email`/`mail`/
`adres e-mail` → address; `podpis` → "" (the renderer places the signature in the body, section 7.7; a column or custom
field of that name never wins); `lista`/`liczba` → group values (null when not grouped); custom fields (a text field's
value merged with the same lookup minus custom fields, text encoding – encoding is applied once at the outer merge; a
date field's date in its format); row columns; a column known to the list but empty in this row → ""; else null
(unknown → warning, renders empty). `{{Pole|tekst}}` renders the fallback when the value is empty or unknown.

### 3.3 Composition – `Correspondence/Composition`

```csharp
namespace AdMng.Core.Correspondence.Composition;

public static class CorrespondenceLimits
{
    public const int MaxImagesPerMessage = 50;
    public const int MaxImageBytes = 10 * 1024 * 1024;                 // per image after metadata stripping
    public const long MaxImagesBytesPerMessage = 25L * 1024 * 1024;
    public const long AttachmentWarningBytes = 15L * 1024 * 1024;      // MailerWPiA's warning
    public const long MessageWarningBytes = 20L * 1024 * 1024;         // images + attachments of one message
    public const int MaxBccPerMessage = 500;
    public const int MaxSubjectLength = 255;
    public const int MaxRecipients = 100_000;
}

public sealed record CorrespondenceSignature(string Name, string BodyHtml, IReadOnlyList<MessageImage> Images)
{ public override string ToString() => $"{nameof(CorrespondenceSignature)} {Name}"; }

public static class OutlookSignatureParser
{
    /// Parses an Outlook signature (.htm): charset from <meta> else ANSI, CSS of <style> inlined, conditional comments,
    /// VML, o:p and <link> removed, sanitized as a fragment (U1c sanitizer); relative images ("Name_files/image001.png")
    /// read through readRelative and embedded by content id. Unreadable / invalid images are dropped with a problem.
    public static SignatureParseResult Parse(string name, ReadOnlySpan<byte> html, Func<string, byte[]?> readRelative, int ansiCodePage);
}
public sealed record SignatureParseResult(CorrespondenceSignature Signature, IReadOnlyList<string> Problems);

/// The style of the wrapping div: today's values are the default.
public sealed record MessageBaseStyle(string FontFamily, double FontSizePoints, string LineHeight)
{
    public static MessageBaseStyle Default { get; } = new("Calibri,'Segoe UI',Arial,sans-serif", 11, "1.4");
    public string Css { get; }   // "font-family:…;font-size:…pt;color:#000000;line-height:…" – only values the sanitizer allows
                                 // (family list, 6–72 pt, normal/number/length), else the default's part
    public static MessageBaseStyle Of(string? fontFamily, double? fontSizePoints, string? lineHeight);   // stored values, missing parts default
}

public static class CorrespondenceDocument
{
    public const string BodyFont = "Calibri,'Segoe UI',Arial,sans-serif";
    public const double BodyPoints = 11;
    /// <html><head><meta charset=utf-8></head><body><div style="{style.Css}">body (+ "<br>" + signature)</div></body></html>
    /// – MailerWPiA's BuildMessage without a <style> block; style null → MessageBaseStyle.Default. A body that places the
    /// signature itself ({{podpis}}) is built without one.
    public static string Build(string bodyHtml, CorrespondenceSignature? signature, MessageBaseStyle? style = null);
    public static string BodyOf(string document);   // inner content of <body> (for previews)
}

public enum MailLintKind { Script, RemoteImage, DataImage, AbsolutePosition, BackgroundImage, TooWide, StyleBlock, LinkTextMismatch, LargeMessage }
public sealed record MailLintFinding(MailLintKind Kind, string? Detail);   // Detail: px for TooWide, host for mismatch, bytes for LargeMessage
public static class MailCompatibilityLint { public static IReadOnlyList<MailLintFinding> Check(string? html, long messageBytes = 0); }

/// The draft as edited: subject, body fragment (sanitized subset, cid images), images by content id, signature,
/// custom fields, list item template (groups) and the salutation.
public sealed record CorrespondenceDraft
{
    public string Subject { get; init; } = "";
    public string BodyHtml { get; init; } = "";
    public IReadOnlyDictionary<string, MessageImage> Images { get; init; }   // body images by content id
    public CorrespondenceSignature? Signature { get; init; }
    public IReadOnlyList<CustomFieldDefinition> CustomFields { get; init; } = [];
    public string ListItemTemplate { get; init; } = "";   // plain text with {{fields}}, as typed in its text box
    public string Salutation { get; init; } = BuiltInFields.DefaultSalutation;
    public bool GroupByAddress { get; init; }
    public MessageBaseStyle? BaseStyle { get; init; }   // null → MessageBaseStyle.Default
}

public sealed record CorrespondenceUnit(            // one message: one row, or several rows of one address (group)
    string Email, string Name, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);

public sealed record RenderedCorrespondence(
    string Subject, string HtmlDocument, IReadOnlyList<MessageImage> Images,   // only images referenced by the document
    IReadOnlyList<string> UnknownFields, IReadOnlyList<string> EmptyFields)
{ public override string ToString() => nameof(RenderedCorrespondence); }

public static class CorrespondenceRenderer
{
    /// Merges subject (Header) and body (Html) for one unit; groups render {{lista}} as <ul><li> of the list item template
    /// per row – plain text: its own text HTML-encoded (FieldTemplate.TextToHtml), the values merged with Html – and
    /// {{liczba}} as the row count; the first row gives the other fields. The body is repaired
    /// (FieldTemplate.RepairHtml), merged, wrapped by CorrespondenceDocument.Build (with the draft's BaseStyle) and
    /// re-sanitized as a document. {{podpis}} in the body is the signature's HTML (raw, like the list; its paragraph split
    /// by ListPlacement, which moves every block field – list and signature – out of its paragraph), and the signature is
    /// then not appended again (no <br>); without a signature, in the subject, list items and custom values it is "" and
    /// never reported as unknown or empty.
    public static RenderedCorrespondence Render(CorrespondenceDraft draft, CorrespondenceUnit unit, IReadOnlyCollection<string> knownColumns);
    public static string PlainText(string htmlDocument);    // HtmlToText.Convert (for logs-free previews, tests)
}
```

### 3.4 Grouping

`RecipientGrouping.Group(IEnumerable<(string Email, string Name, IReadOnlyDictionary<string,string> Fields)>, bool byAddress)`
returns `CorrespondenceUnit`s in first-appearance order; without grouping one unit per row. Mailbox rows (section 6.4)
use it with the owner's address.

### 3.5 Journal – `Correspondence/Journal`

```csharp
namespace AdMng.Core.Correspondence.Journal;

public sealed record JournalEntry(string AddressHash, DateTimeOffset SentAt, string Subject)
{ public override string ToString() => nameof(JournalEntry); }

public static class JournalHashing
{
    public const int KeyBytes = 32;
    /// Base64url of the first 16 bytes of HMAC-SHA-256(key, UTF-8(EmailAddressText.Normalize(address))).
    public static string Hash(ReadOnlySpan<byte> key, string address);
    /// Scoped (a mailbox owner row, section 7.6): HMAC of normalize(address) + "\n" + scope (trimmed, lower-invariant: the
    /// mailbox address or DN, never cut); a null or blank scope is the unscoped hash. An address with a line break is
    /// refused, so no scoped input equals an unscoped one or another scope's.
    public static string Hash(ReadOnlySpan<byte> key, string address, string? scope);
}

public static class JournalFile
{
    public const string Header = "AdMng.CorrespondenceJournal\t1";
    public const int MaxSubjectLength = 200;
    public static IReadOnlyList<JournalEntry> Read(TextReader reader);           // skips malformed lines
    public static string Line(JournalEntry entry);                               // hash \t yyyy-MM-ddTHH:mm:ssZ \t subject (tabs/newlines → spaces)
}
```

### 3.6 Images

`MessageImage.Create` gains an optional `int maxImageBytes = MessageImageLimits.MaxImageBytes` (U1 callers unchanged);
Korespondencja passes `CorrespondenceLimits.MaxImageBytes`. Per-message limits of section 3.3 are checked by the
renderer (finding `LargeMessage` / an error before sending).

### 3.7 Mailboxes – `Correspondence/Mailboxes`

```csharp
namespace AdMng.Core.Correspondence.Mailboxes;

public enum MailboxType { None, User, Linked, Shared, Room, Equipment, RemoteUser, RemoteShared, RemoteRoom, RemoteEquipment, Other }

public static class MailboxTypes
{
    public const string RecipientTypeDetails = "msExchRecipientTypeDetails";
    public const string DelegateListLink = "msExchDelegateListLink";
    public const string PublicDelegates = "publicDelegates";
    // 1 User, 2 Linked, 4 Shared, 16 Room, 32 Equipment, 2147483648 RemoteUser, 8589934592 RemoteRoom,
    // 17179869184 RemoteEquipment, 34359738368 RemoteShared; other values → Other; missing → None
    public static MailboxType FromValue(long? recipientTypeDetails);
    public static IReadOnlyList<long> ValuesOf(MailboxType type);
    public static bool IsMailbox(MailboxType type);          // not None
    public static bool IsFunctional(MailboxType type);       // Shared, Room, Equipment and their Remote twins
}

public enum MailboxSelection { Shared, SharedRoomsAndEquipment, AllMailboxes, AllAccounts }
  // AllAccounts: every user account of the scope (no Exchange attributes needed – for directories without the schema)

public enum OwnerNameSource { GivenNameAndSurname, DisplayName, Description, Attribute }

public sealed record OwnerName(string GivenName, string Surname, string Text)   // Text: "Given Surname" as found (titles removed)
{ public bool IsEmpty; public override string ToString() => nameof(OwnerName); }

public static class OwnerNameParser
{
    /// GivenNameAndSurname: givenName + sn of the mailbox (one of them may hold both words – then split);
    /// DisplayName/Description/Attribute: the text without titles (TitleParser.WithoutTitles with TitleTable.Default),
    /// without "(…)" remarks and after a ':' or ' - ' prefix ("Skrzynka: Jan Kowalski"); 2+ words → given = all but the
    /// last, surname = last; a single word → surname only.
    public static OwnerName? Parse(DirectoryObject mailbox, OwnerNameSource source, string? attribute);
}

public sealed record PersonCandidate(string DistinguishedName, string GivenName, string Surname, string DisplayName,
    string? Address, bool IsDisabled, MailboxType Type, string? Department, string? Title, string? Login)
{ public override string ToString() => nameof(PersonCandidate); }

public static class PersonNameKey { public static string Fold(string? name); }   // diacritics removed, lower, hyphens/spaces collapsed

public sealed class PersonIndex
{
    public PersonIndex(IEnumerable<PersonCandidate> persons);   // indexes folded given, surname, both orders, display name, surname parts
    public int Count { get; }
    public IReadOnlyList<PersonCandidate> Lookup(OwnerName name);  // union of all levels, distinct
}

public enum OwnerMatchLevel { Exact, Folded, Swapped, DisplayName, Partial }
public enum OwnerMatchStatus { Matched, MatchedByDelegate, Probable, Ambiguous, NotFound, NoName, OwnerDisabled, OwnerWithoutAddress }

public sealed record OwnerCandidateMatch(PersonCandidate Person, OwnerMatchLevel Level, bool IsDelegate, int Score);
public sealed record OwnerMatch(OwnerMatchStatus Status, OwnerCandidateMatch? Best, IReadOnlyList<OwnerCandidateMatch> Candidates)
{ public override string ToString() => $"{Status} ({Candidates.Count})"; }

public sealed record MailboxEntry(string DistinguishedName, string Name, string? Address, MailboxType Type,
    IReadOnlyList<string> DelegateDns, bool IsDisabled)
{ public override string ToString() => nameof(MailboxEntry); }

public sealed record OwnerMatchOptions { public bool ExcludeMailboxAccounts { get; init; } = true; public bool PreferEnabled { get; init; } = true; }

public static class MailboxOwnerMatcher
{
    public const int MaxCandidates = 20;
    public static OwnerMatch Match(MailboxEntry mailbox, OwnerName? name, PersonIndex persons, OwnerMatchOptions options);
}
```

Matching:

1. `name` null/empty → `NoName`.
2. Candidates: `persons.Lookup(name)` minus the mailbox itself (same DN) and, with `ExcludeMailboxAccounts`, persons
   whose `Type` is functional (shared/room/equipment).
3. Level per candidate (best that applies): **Exact** – given and surname equal ignoring case; **Folded** – equal after
   `PersonNameKey.Fold`; **Swapped** – given↔surname (folded); **DisplayName** – the folded display name without titles
   equals "given surname" or "surname given"; **Partial** – the folded surname equals one part of a compound surname
   (`Kowalska-Nowak`) on either side, or the given names share the first given name and surnames are equal.
4. Score = level weight (Exact 50, Folded 45, Swapped 35, DisplayName 30, Partial 15) + delegate 40 (`DelegateDns`
   contains the candidate DN) + enabled 10 + has address 5.
5. Status: no candidate → `NotFound`. Exactly one candidate is a delegate and it is at least `Partial` → `MatchedByDelegate`.
   Otherwise take the highest level present: one candidate at that level → `Matched` when the level is Exact/Folded
   (`Probable` for Swapped/DisplayName/Partial); several → prefer enabled ones when `PreferEnabled` (one enabled left →
   that one at the same rule), else `Ambiguous` (`Best` null). A chosen best that is disabled → `OwnerDisabled`; without
   address → `OwnerWithoutAddress`. Candidates sorted by score desc, then display name (culture-insensitive ordinal of
   the folded name), capped at `MaxCandidates`.
6. The page (6.4) chooses `Best` automatically for `Matched`, `MatchedByDelegate`, `Probable` and
   `OwnerWithoutAddress`, and for `OwnerDisabled` only when disabled owners are allowed – never a best at the `Partial`
   level without a delegate link (only a part of the name agrees: maybe another person of a similar name), which the
   operator confirms in the row. `Swapped` and `DisplayName` bests are chosen.

### 3.8 U1 fields addition

`UserMessageFields` gains `{$created[:format]}` (= `AccountStatus.Created`, date token like `lastLogon`) so mailbox fields
can show when a mailbox was created. Added to `Tokens`, `Evaluate`, `Examples` and the U1 field list.

### 3.9 Outlook contracts – `Correspondence/Outlook`

```csharp
namespace AdMng.Core.Correspondence.Outlook;

public interface IOutlookClient            // application singleton; constructor never touches COM
{
    /// Whether classic Outlook is registered (ProgID "Outlook.Application") – no start, no attach. Off Windows: NotSupported.
    OutlookAvailability Availability { get; }
    /// Attaches to the running Outlook or starts it (3 attempts, 700 ms apart), loads profile and accounts.
    Task<OutlookConnectResult> ConnectAsync(CancellationToken cancellationToken);
}
public enum OutlookAvailability { NotSupported, NotInstalled, Installed }

public interface IOutlookSession : IAsyncDisposable     // owns one STA thread; dispose releases every COM object
{
    OutlookProfile Profile { get; }
    bool IsConnected { get; }
    Task<OutlookItemResult> SendAsync(OutlookMessage message, OutlookSendMode mode, CancellationToken cancellationToken);
    Task<OutlookRenderedMessage> RenderAsync(OutlookMessage message, CancellationToken cancellationToken);   // builds, saves, reads, deletes the draft
    Task<OutlookDiagnostics> DiagnoseAsync(bool createTestItem, CancellationToken cancellationToken);
    Task<IReadOnlyList<OutlookCampaign>> FindCampaignsAsync(DateTimeOffset since, CancellationToken cancellationToken);
    Task<IReadOnlyList<OutlookVoteResponse>> ReadResponsesAsync(string campaignId, CancellationToken cancellationToken);
}

public sealed record OutlookAccount(string DisplayName, string SmtpAddress) { /* ToString: "Display <smtp>" or Display */ }
public sealed record OutlookProfile(string Version, string ProfileName, IReadOnlyList<OutlookAccount> Accounts, string? CurrentUserAddress)
{ public override string ToString() => nameof(OutlookProfile); }

public enum OutlookFailureKind { NotSupported, NotInstalled, Elevated, Busy, NotResponding, SecurityDenied, Disconnected,
    RecipientUnresolved, AttachmentMissing, InvalidAccount, ItemTooLarge, Cancelled, Unknown }
public sealed record OutlookFailure(OutlookFailureKind Kind, string Code, string? Step, int? HResult)
{   // Code: "outlook.<kind>" camel case, e.g. outlook.notInstalled; Step: a stable step id (section 4.3), never a value
    public bool StopsRun { get; }   // NotInstalled, Elevated, SecurityDenied, Disconnected, NotSupported,
                                    // InvalidAccount (every message of a run is sent from the same account)
    public bool IsTransient { get; }  // Busy, NotResponding
}
public sealed record OutlookConnectResult(IOutlookSession? Session, OutlookFailure? Failure);

public sealed record OutlookAttachment(string Path, string? DisplayName = null) { public override string ToString() => nameof(OutlookAttachment); }
public enum OutlookImportance { Low = 0, Normal = 1, High = 2 }
public enum OutlookSendMode { Send, Draft }

public sealed record OutlookMessage
{
    public required IReadOnlyList<string> To { get; init; }
    public IReadOnlyList<string> Cc { get; init; } = [];
    public IReadOnlyList<string> Bcc { get; init; } = [];
    public IReadOnlyList<string> ReplyTo { get; init; } = [];
    public required string Subject { get; init; }
    public required string HtmlBody { get; init; }                        // whole document, cid: images
    public IReadOnlyList<MessageImage> InlineImages { get; init; } = [];
    public IReadOnlyList<OutlookAttachment> Attachments { get; init; } = [];
    public string? SendUsingAccount { get; init; }                        // SMTP address of a profile account; null = default
    public string? SentOnBehalfOf { get; init; }
    public bool ReadReceipt { get; init; }
    public DateTimeOffset? DeferredDelivery { get; init; }
    public string? VotingOptions { get; init; }                           // "Tak;Nie" – semicolon separated (see below)
    public OutlookImportance Importance { get; init; } = OutlookImportance.Normal;
    public string? CampaignId { get; init; }                              // stored as a named property for the Odpowiedzi tab
    public IReadOnlyList<string> CampaignKeys { get; init; } = [];        // e.g. mailbox addresses of this message
    public IReadOnlyDictionary<string, string> InternetHeaders { get; init; } // U1: Auto-Submitted, X-AdMng-Message (best effort)
    public override string ToString() => nameof(OutlookMessage);
}

public sealed record OutlookItemResult(bool Succeeded, bool SavedAsDraft, OutlookFailure? Failure)
{ public static OutlookItemResult Sent { get; } ...; public static OutlookItemResult Draft { get; } ...; }
public sealed record OutlookRenderedMessage(string Html, IReadOnlyDictionary<string, OutlookRenderedImage> Images, int Attachments);
public sealed record OutlookRenderedImage(string ContentId, string MediaType, byte[] Data);
public sealed record OutlookDiagnostics(string Version, string ProfileName, IReadOnlyList<OutlookAccount> Accounts,
    string? CurrentUserAddress, IReadOnlyList<string> Signatures, bool ProcessIs64Bit, bool? TestItemCreated, OutlookFailure? TestItemFailure);
public sealed record OutlookCampaign(string Id, string Subject, DateTimeOffset FirstSent, DateTimeOffset LastSent, int Messages);
public sealed record OutlookVoteResponse(string Recipient, string? Response, DateTimeOffset? RespondedAt, DateTimeOffset SentAt,
    string Subject, IReadOnlyList<string> Keys) { public override string ToString() => nameof(OutlookVoteResponse); }

// VotingOptions: Outlook splits MailItem.VotingOptions on the Windows list separator (sList, ',' with US formats),
// not on ';' – the engine splits AD Manager's ';' options and joins them with the user's separator (GetLocaleInfoEx
// LOCALE_SLIST). A delimiter inside an option becomes a look-alike so each option stays one button: a comma U+201A
// '‚' while ',' is the delimiter, a no-break space for a blank delimiter, a space for any other; votes read back for
// the Odpowiedzi tab get their comma again (U+201A → ',') before they are matched to the options.

public interface IOutlookSignatureSource       // %APPDATA%\Microsoft\Signatures (file system, no COM)
{
    string? Folder { get; }
    IReadOnlyList<string> List();               // names (file name without .htm/.html), ordinal order
    Task<SignatureParseResult?> ReadAsync(string name, int ansiCodePage, CancellationToken cancellationToken);
}
```

## 4. `src/AdMng.Outlook` (net10.0, Windows at run time)

Project like `AdMng.Remote`: `TargetFramework net10.0`, references Core, `AllowUnsafeBlocks` only if `LibraryImport` is
used, `InternalsVisibleTo AdMng.Outlook.Tests`. Header comment: *"Windows only at run time (every Windows call is
guarded); compiles everywhere. Persists nothing; never logs addresses, subjects or bodies."*

### 4.1 Types

| Type | Purpose |
|---|---|
| `OutlookClient : IOutlookClient` | `Availability` (cached `Type.GetTypeFromProgID("Outlook.Application")` on Windows); `ConnectAsync`: new `StaWorker`, on it `IOutlookApplicationProvider.Attach()` (oleaut32 `GetActiveObject` via `CLSIDFromProgID`) else `Activator.CreateInstance`; 3 attempts 700 ms apart; profile + accounts; returns `OutlookSession`. Constructor takes an optional `IOutlookApplicationProvider` (tests) and `TimeProvider`. |
| `IOutlookApplicationProvider` (internal) | `object Attach()` / `object Start()` – the only place that creates COM objects; tests pass CLR fakes. |
| `StaWorker` (internal) | One background thread, `ApartmentState.STA` on Windows (MTA elsewhere for tests), a `BlockingCollection<WorkItem>` loop, `CoRegisterMessageFilter` with `ComRetryFilter` (RETRYLATER → retry after 150 ms for up to 60 s, then cancel), `RunAsync<T>(Func<T>, CancellationToken)`; `DisposeAsync` revokes the filter and ends the thread. |
| `OutlookSession : IOutlookSession` | Holds the `dynamic` application; every method marshals to the worker; `Release(object?)` = `Marshal.ReleaseComObject` only when `Marshal.IsComObject`. |
| `OutlookMailBuilder` (internal) | The port of MailerWPiA `Send`/`RenderViaOutlook` (4.2). |
| `OutlookErrors` (internal, pure) | HRESULT / exception → `OutlookFailure` (4.3). |
| `OutlookCampaigns` (internal) | Named properties, Sent Items search, vote reading (4.4). |
| `OutlookSignatureFiles : IOutlookSignatureSource` | File listing and reading of `%APPDATA%\Microsoft\Signatures`, `Name_files` images (relative paths only, no `..`, no rooted paths, inside the folder), 2 MiB per file; parsing with Core `OutlookSignatureParser`. |
| `OutlookUserMessageTransport : IUserMessageTransport` | U1 adapter (section 5). |
| `OutlookTempFiles` (internal) | Private folder `%TEMP%\AdMng-Outlook-{pid}-{random}`, created per item, deleted after the item is saved (and stale `AdMng-Outlook-*` of dead processes at the next start). |

### 4.2 Building an item (port of MailerWPiA, same order)

Steps (step ids in brackets, used in `OutlookFailure.Step`): `CreateItem(0)` [create] → `BodyFormat = 2` →
`SendUsingAccount` – matched by SMTP address among `Session.Accounts`; property set, else `InvokeMember` with
`BindingFlags.PutRefDispProperty` [account] → `SentOnBehalfOfName` [onBehalf] → `Recipients.Add` + `Type` (1 To, 2 Cc,
3 Bcc) + `Resolve` each, `ResolveAll` [recipients]; any unresolved recipient after `ResolveAll` fails with
`RecipientUnresolved` (no send) → `ReplyRecipients.Add` [replyTo] → `Subject` [subject] → `HTMLBody` [body] →
`ReadReceiptRequested` [receipt] → `DeferredDeliveryTime` (local time of the PC) [defer] → `VotingOptions` [voting] →
`Importance` → named properties `AdMngCampaign` / `AdMngKeys` (PS_PUBLIC_STRINGS `{00020329-0000-0000-C000-000000000046}`)
and internet headers (PS_INTERNET_HEADERS `{00020386-0000-0000-C000-000000000046}`) via `PropertyAccessor.SetProperty`,
best effort [properties] → inline images: write to the temp folder, `Attachments.Add(path, 1, 0, fileName)`, set
`PR_ATTACH_CONTENT_ID` (0x3712001F) = `ContentId`, `PR_ATTACHMENT_HIDDEN` (0x7FFE000B) = true [images] → attachments:
`File.Exists` else `AttachmentMissing`, `Attachments.Add(path)` [attachments] → `Save()` [save] → when images: set
content id, `PR_ATTACH_MIME_TAG` (0x370E001F), hidden again on the saved attachments (matched by file name), `Save()`
[inline] → unless Draft: `Send()` [send]. `finally`: release every COM object, delete the temp folder. A failure after
`Save()` leaves the draft in *Wersje robocze* (`SavedAsDraft = true`), as MailerWPiA.

`RenderAsync`: the same build without send, then `HTMLBody`, attachments with a content id are saved to the temp
folder, read into memory (`OutlookRenderedImage`) and deleted, `cid:` kept in the HTML, `Delete()` the draft
(MailerWPiA behaviour: it lands in *Elementy usunięte*).

`DiagnoseAsync`: version, profile, accounts, current user address, signature names (from `IOutlookSignatureSource` if
given), process bitness, optionally `CreateItem` + `Subject` + `HTMLBody` + `Close(1)` (olDiscard).

### 4.3 Error classification (`OutlookErrors.Classify(Exception, string step)`)

| Condition | Kind | Code |
|---|---|---|
| not Windows | NotSupported | `outlook.notSupported` |
| ProgID missing | NotInstalled | `outlook.notInstalled` |
| `0x80080005` CO_E_SERVER_EXEC_FAILURE, `0x800401E3` MK_E_UNAVAILABLE after start attempt with elevation detected (`Environment.IsPrivilegedProcess`) | Elevated | `outlook.elevated` |
| `0x8001010A` RPC_E_SERVERCALL_RETRYLATER, `0x80010001` RPC_E_CALL_REJECTED | Busy | `outlook.busy` |
| `0x800706BE`, `0x80010105` RPC_E_SERVERFAULT | NotResponding | `outlook.notResponding` |
| `0x80004004` E_ABORT on send / recipients (object model guard refused) | SecurityDenied | `outlook.denied` |
| `0x80010108` RPC_E_DISCONNECTED, `0x800706BA` RPC_S_SERVER_UNAVAILABLE, `0x800706BF` | Disconnected | `outlook.disconnected` |
| unresolved recipient after `ResolveAll` | RecipientUnresolved | `outlook.recipient` |
| missing attachment file | AttachmentMissing | `outlook.attachment` |
| account not found among profile accounts | InvalidAccount | `outlook.account` |
| `0x80040600`-ish size errors / `ItemTooLarge` text-free detection by HRESULT `0x8004060C` (MAPI_E_NOT_ENOUGH_DISK) / `0x80040116` | ItemTooLarge | `outlook.tooLarge` |
| `OperationCanceledException` | Cancelled | `outlook.cancelled` |
| anything else | Unknown | `outlook.error` (HResult kept for the report) |

Exception messages are never kept (they may contain addresses); only the HRESULT and the step.

### 4.4 Campaigns and votes

`CampaignId` = 32 hex chars generated per send run; `AdMngKeys` = keys joined by `\n` (max 4 000 chars). Search:
`Session.GetDefaultFolder(5)` (olFolderSentMail) `.Items.Restrict("@SQL=\"…/AdMngCampaign\" IS NOT NULL AND
\"urn:schemas:httpmail:date\" >= '<since>'")`, grouped by id (subject of the first item, first/last `SentOn`, count).
`ReadResponsesAsync`: items with that id; per recipient `AutoResponse` (vote text, empty when none), `TrackingStatus`
(`olTrackingReplied` = 3 … ) and `TrackingStatusTime` (`4501-01-01` = none → null); keys from `AdMngKeys`.

### 4.5 Tests (`tests/AdMng.Outlook.Tests`, net10.0, runs on Linux and Windows)

A CLR fake object model in the test project (`FakeOutlook.Application`, `NameSpace`, `Accounts` with 1-based
indexer, `MailItem`, `Recipients`, `Recipient`, `Attachments`, `Attachment`, `PropertyAccessor`, `Folder`, `Items`
with `Restrict`/`Sort`, `UserProperties` not needed) records every call; tests drive `OutlookSession` through
`IOutlookApplicationProvider` and assert the step order of 4.2, images by content id and hidden, draft on failure
after save, unresolved recipient refusal, account selection, error classification table, temp folder deleted,
render images read back, campaign search filter text and vote parsing, `StaWorker` serialization and cancellation,
`OperatingSystem.IsWindows()` false → `NotSupported`.

`tests/AdMng.Testing/FakeOutlookClient.cs` (public): `FakeOutlookClient : IOutlookClient` + `FakeOutlookSession`
(scripted results per recipient address, recorded `OutlookMessage`s with mode, `ConnectFailure`, `Profile`,
`RenderResult`, `Campaigns`, `Responses`, `DiagnoseResult`, `BeforeSend` hook) for Presentation and UI tests.

## 5. U1 Outlook transport

| Change | Where |
|---|---|
| `UserMessageTransportKind.Outlook` | Core `Messaging/Transport.cs` |
| `UserMessageTransportChoice.Outlook` (+ resx `UserMessages.Transport.Outlook`) | Presentation `Settings/UserMessageSettings.cs` |
| `UserMessageSettings.OutlookAccount` (SMTP address, optional), `OutlookOnBehalfOfSender` (bool: put `FromAddress` into "w imieniu") | same |
| `UserMessageTransportSelector(..., IOutlookClient? outlook = null)`: choice Outlook → `OutlookUserMessageTransport`; problems `OutlookNotSupported`, `OutlookNotInstalled` (+ `All`, resx `UserMessages.Problem.*`); Automatic unchanged | `UserMessageTransports.cs` |
| Composer `TransportText`: `L.UserMessages.ViaOutlook` "Wysyłka przez Outlooka: {0}" ({0} = account or "konto domyślne profilu") | composer + RichHtml partial (`Kind` switch) |
| Capabilities: `UserMessageTransportCapabilities.Outlook` = InlineImages, 50 images, 10 MiB / image, 25 MiB total, MaxHtmlLength 2 000 000 | Core |
| Delivery codes `outlook.sent` (Accepted) and the failure codes of 4.3 → `UserMessageText.KnownCodes` + resx `UserMessages.Code.*` | Presentation |
| Settings tab: transport choice Outlook shows the account box (+ "Pobierz konta z Outlooka" button using `IOutlookClient`) and the on-behalf check | `UserMessageSettingsViewModel` + XAML |

`OutlookUserMessageTransport` (in AdMng.Outlook): `Kind = Outlook`; `Description` = "Outlook (account or default)";
`GetStatusAsync` maps `Availability` (Installed → Available with `Capabilities.Outlook`); `ConnectAsync` → session
(failure → `UserMessageTransportException` with `TransportFailed`); `SendAsync(OutgoingUserMessage)` →
`OutlookMessage` (To/Cc, ReplyTo, subject, `HtmlBody`, inline images, account, on behalf = From when configured,
internet headers `Auto-Submitted: auto-generated`, `X-Auto-Response-Suppress: All`, `X-AdMng-Message:
user-notification`) → delivery: Sent → `Accepted` code `outlook.sent`; failure → `Transient` (Busy, NotResponding),
`TransportFailed` (StopsRun kinds), else `Rejected` with the failure code. Dispose ends the session.

## 6. Presentation: `src/AdMng.Presentation/Correspondence`

### 6.1 Settings – `Settings/CorrespondenceSettings.cs` (`AppSettings.Correspondence`)

```csharp
public sealed class CorrespondenceSettings
{
    public const int MaxTemplates = 100, MaxDelaySeconds = 300, MaxJournalRetentionDays = 3650;
    public string Subject { get; set; } = "";
    public string Salutation { get; set; } = "Szanowni Państwo";
    public string OnBehalfOf { get; set; } = "";
    public string AccountAddress { get; set; } = "";              // the chosen Outlook account (SMTP), applied after connect
    public string SignatureName { get; set; } = "";
    public double DelaySeconds { get; set; } = 3;                 // 0–300
    public CorrespondenceSendMode SendMode { get; set; } = CorrespondenceSendMode.Individual;   // Individual, Bcc
    public int BccChunkSize { get; set; } = 500;                  // 1–500
    public bool ReadReceipt { get; set; }
    public bool SkipAlreadySent { get; set; } = true;
    public bool GroupByAddress { get; set; }
    public bool VotingEnabled { get; set; }
    public string VotingOptions { get; set; } = "Tak, nadal potrzebna;Nie, można usunąć";
    public string DeferTime { get; set; } = "08:00";
    public CorrespondencePreviewWidth PreviewWidth { get; set; } = CorrespondencePreviewWidth.Desktop;   // Desktop 700, Phone 380, Full
    public string DownloadsPattern { get; set; } = "*kontakty*.csv";
    public int ConfirmationThreshold { get; set; } = 20;          // typed confirmation above
    public int JournalRetentionDays { get; set; } = 365;
    public CorrespondenceTemplate? LastDraft { get; set; }        // autosaved on close and before each send
    public List<CorrespondenceTemplate> Templates { get; set; } = [];
    public List<DirectoryFieldDefinition> DirectoryFields { get; set; } = [];    // empty → DirectoryFieldDefinition.Defaults
    public MailboxOwnerSettings Mailboxes { get; set; } = new();
    public void Normalize();
}
public enum CorrespondenceSendMode { Individual, Bcc }
public enum CorrespondencePreviewWidth { Desktop, Phone, Full }

public sealed class CorrespondenceTemplate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";                     // sanitized subset with cid images
    public List<MessageTemplateImage> Images { get; set; } = [];   // bytes in MessageImageStore
    public List<CustomFieldDefinition> CustomFields { get; set; } = [];
    public string ListItemTemplate { get; set; } = "";
    public string? Salutation { get; set; }
    public bool? VotingEnabled { get; set; }
    public string? VotingOptions { get; set; }
    public bool? GroupByAddress { get; set; }
    public string? FontFamily { get; set; }     // CSS family list; null = the window's (its last values, section 7.7)
    public double? FontSize { get; set; }       // points
    public string? LineHeight { get; set; }     // "normal" or a number such as "1.4"
}
// Normalize keeps a custom field's Kind and Format (a date that does not parse keeps Value "" – the window flags it; an
// unknown kind is text) and the font values the sanitizer allows (else null). Export files stay version 1 (additive).

public sealed record DirectoryFieldDefinition(string Name, string Expression, DirectoryFieldTarget Target = DirectoryFieldTarget.Person);
public enum DirectoryFieldTarget { Person, Mailbox }   // Person: the recipient (AD rows) or the owner (mailbox rows)

public sealed class MailboxOwnerSettings
{
    public MailboxSelection Selection { get; set; } = MailboxSelection.Shared;
    public string ExtraFilter { get; set; } = "";                 // validated LDAP filter, ANDed
    public OwnerNameSource NameSource { get; set; } = OwnerNameSource.GivenNameAndSurname;
    public string NameAttribute { get; set; } = "";
    public string PersonBase { get; set; } = "";                  // portable DN ({domain}); empty = domain
    public bool EnabledOwnersOnly { get; set; } = true;           // disabled persons stay candidates but never auto-match (OwnerDisabled)
    public bool GroupByOwner { get; set; }                         // off: one message per mailbox (the built-in names it in the subject)
    public List<DirectoryFieldDefinition> Fields { get; set; } = []; // empty → Defaults for mailbox rows
}
```

`DirectoryFieldDefinition.Defaults` (person): Imię `{givenName}`, Nazwisko `{sn}`, Nazwa wyświetlana `{displayName}`,
Stanowisko `{title}`, Dział `{department}`, Login `{sAMAccountName}`, Zwrot osobowy `{$salutation}`, Tytuł `{$title}`,
Pan/Pani `{$honorific}`, Pana/Panią `{$honorific:biernik}`, Panu/Pani `{$honorific:celownik}`, Szanowny/a
`{$byGender:Szanowny/Szanowna}`. `MailboxDefaults` adds (target Mailbox): Skrzynka `{displayName}`, Adres skrzynki
`{mail}`, Opis skrzynki `{description}`, Jednostka `{$parentName}`, Ostatnie logowanie `{$lastLogon:d MMMM yyyy}`,
Utworzona `{$created:d MMMM yyyy}`, Login skrzynki `{sAMAccountName}`, and person fields for the owner (target Person:
Imię, Nazwisko, Nazwa wyświetlana, Stanowisko, Dział, Zwrot osobowy, Pan/Pani, Pana/Panią, Panu/Pani, Szanowny/a).
Expressions are checked with `UserMessageFields.Engine.Parse` and `UserMessageRules.IsForbiddenAttribute` (no secrets).

**Built-in templates** (not stored, `CorrespondenceTemplateLibrary.BuiltIns`, both `IsBuiltIn`): the mailbox query as
the operator's team sends it today – section 7.7. `mailbox-still-needed` "Weryfikacja skrzynki pocztowej (wiadomość na
każdą skrzynkę)", subject `Weryfikacja skrzynki pocztowej: {{Skrzynka}}`, one message per mailbox;
`mailbox-still-needed-grouped` "Weryfikacja skrzynek pocztowych (jedna wiadomość na osobę)", subject `Weryfikacja
skrzynek pocztowych`, the mailboxes in `{{lista}}`, grouping on. Both: the body of the operator's example message
(embedded resources `Correspondence/Templates/*.html`, read once), custom field Termin (date, today + 14 days,
`CustomFieldFormats.DefaultDate`), list item template "{{Skrzynka}} ({{Adres skrzynki}})", no salutation, no voting
buttons, Aptos 12 pt with line height normal.

### 6.2 Services and registration

| Type | Lifetime | Notes |
|---|---|---|
| `CorrespondenceTemplateLibrary(ISettingsStore, MessageImageStore?, TimeProvider?)` | singleton | built-ins (their deadline from the clock; default `TimeProvider.System`) + saved; save/delete/export (`{"format":"AdMng.CorrespondenceTemplates","version":1,...}` with images base64, 20 MiB cap)/import (new ids); `LastDraft` save/load; `StyleOf(template)`; `Changed` event |
| `CorrespondenceJournal(IAppPaths, ICorrespondenceKeyProtector, TimeProvider)` | singleton | `<SettingsDirectory>/correspondence/journal.txt` + `journal.key` (protected 32 bytes); `LoadAsync`, `Find(address, scope = null) → JournalEntry?`, `AppendAsync(address, subject, ct)` and `AppendAsync(address, subject, scope, ct)` (line written and flushed at once, FileShare.ReadWrite; scope: a mailbox owner row's mailbox key, section 7.6), `PruneAsync(retention)`, `ClearAsync`, `Count`, `Oldest`, `Newest`; prune at load with the settings retention; thread-safe; corrupted key → new key, old journal renamed `journal-<date>.old` (and reported); the backups follow the retention (every prune drops their older entries, an emptied backup is deleted, one of another format once last written before the cutoff) and `ClearAsync` deletes them before the journal |
| `ICorrespondenceKeyProtector` | singleton (App: DPAPI CurrentUser + entropy "AdMng.CorrespondenceJournal") | `byte[] Protect(byte[])`, `byte[] Unprotect(byte[])`; tests: XOR fake |
| `ICorrespondencePlatform` | singleton (App) | `string? DownloadsFolder` (SHGetKnownFolderPath), `int AnsiCodePage` via `IRegionalSettings` |
| `IBrowserPreview` | singleton (App) | `Task OpenAsync(HtmlPreviewContent content, string title)` (loopback, section 9.3) |
| `ICorrespondenceDialogs` | scoped (App `DialogService` partial) | `Task<IReadOnlyList<string>> OpenFilesAsync(string title, IReadOnlyList<FileFilter> filters)`; `Task<string?> PromptTextAsync(string title, string message, string initial, bool multiline)`; `Task ShowReportAsync(string title, string text)` (read-only, copy button); `Task<string?> PickFolderAsync(string title)` |
| `ICorrespondenceLauncher` | scoped (App `DialogService` partial) | `Task ShowCorrespondenceAsync(CorrespondenceRequest request)` |
| `CorrespondenceServices` | scoped | parameter object: Outlook client, signature source, journal, templates, image store, dialogs, platform, browser preview, CSV codec, audit log, time, delay function, `UserMessageFields` engine |

`CorrespondenceRequest(IReadOnlyList<string> DistinguishedNames, CorrespondenceStart Start = Recipients)`;
`CorrespondenceStart { Recipients, MailboxOwners }`. `AddAdMngCorrespondence()` (Presentation, called from
`AddAdMngPresentation`) TryAdds the Presentation singletons, `CorrespondenceServices` scoped and
`CorrespondenceViewModelFactory` scoped (`Create(CorrespondenceRequest)`). New scoped/shared types go into
`ConnectionLifetimes.cs`.

### 6.3 Shell commands

| Id | Title key | Where | Action |
|---|---|---|---|
| `tools.correspondence` | `Correspondence.Command` "Korespondencja (Outlook)…" | Tools menu, palette | singleton window per connection (key `tools:correspondence`), empty request |
| `object.correspondence` | `Correspondence.ObjectCommand` "Wyślij korespondencję…" | Action menu, list context menu, palette | new window with the DNs of selected users/contacts/inetOrgPersons (groups: their members, section 6.5) |
| `object.mailboxOwners` | `Correspondence.MailboxOwnersCommand` "Zapytaj właścicieli skrzynek…" | Action menu, list context menu, palette | new window, `Start = MailboxOwners`, scope = the selected user objects |

Declared in a `ShellViewModel.Correspondence.cs` partial (`CorrespondenceCommandDescriptors()`, `CorrespondenceStateCommands`);
hidden when `ICorrespondenceLauncher` is not registered.

### 6.4 View models

All in `AdMng.Presentation.Correspondence`; `CorrespondenceViewModel : WindowViewModelBase` owns page view models
(each a `ObservableObject` with its own file and tests). XAML binds through the page properties.

**`CorrespondenceViewModel`** (`CorrespondenceViewModel.cs`)
- `WindowTitle` ("Korespondencja – {domain}"), `SelectedTab` (`CorrespondenceTab { Mailboxes, Recipients, Message, Preview, Send, Responses }`), `IsMailboxesTabVisible` (true when started with MailboxOwners or after `ShowMailboxesCommand`), `StatusText`, `Error`, `IsBusy`.
- Pages: `Outlook` (`OutlookConnectionViewModel`), `Mailboxes` (`MailboxOwnersPageViewModel`), `Recipients` (`RecipientsPageViewModel`), `Message` (`MessagePageViewModel`), `Preview` (`PreviewPageViewModel`), `Send` (`SendPageViewModel`), `Responses` (`ResponsesPageViewModel`).
- Commands: `ShowMailboxesCommand`, `HelpCommand` (report dialog with the Polish help text), `CloseCommand`.
- `InitializeAsync`: load settings into pages, journal, signatures, template draft; apply the request (DNs → recipients from AD, or mailbox scope = selection + switch to Mailboxes); connect Outlook in the background (no modal error; status only).
- `CanCloseAsync`: while sending asks to stop; saves `LastDraft` and options.
- `OnSessionEnded`: AD actions disabled (`Recipients.CanUseDirectory=false`), Outlook sending continues to work.

**`OutlookConnectionViewModel`**: `State` (`OutlookState { Unknown, Connecting, Connected, NotInstalled, Failed, NotSupported }`), `StateText` ("Outlook: połączono (2 konta)" …), `Accounts` (`ObservableCollection<OutlookAccount>`), `SelectedAccount` (after connecting: the account of the settings when the profile has it, else the profile's default – the current user's, else the first – so never empty while connected; that default is not saved as the operator's choice), `OnBehalfOf`, `Session` (internal), `ConnectCommand`, `CheckCommand` ("Sprawdź Outlooka" → diagnostics report incl. journal folder path, Downloads folder, signatures folder), `EnsureConnectedAsync()`; failures show the localized code text (`Correspondence.Outlook.{code}`).

**`RecipientsPageViewModel`**
- Collection `Items` (`BulkObservableCollection<CorrespondenceRecipientViewModel>`), `View` (filtered by `SearchText` over email, name, source and fields), `SelectedItems` (set by the view), `Columns` (`ObservableCollection<string>`: field columns in import order – the view builds grid columns), `CountsText` ("Odbiorcy: 120 • do wysłania: 95 • pominięci: 20 • błędne: 5"), `ExclusionsText` ("Pominięcia: 12 własnych + 340 z dziennika").
- Row `CorrespondenceRecipientViewModel`: `Include`, `Email`, `Name`, `Source`, `Status` + `StatusKind` (`None, Sent, Draft, Test, Failed, Skipped`), `PreviouslySentText` (journal: "28.10.2026 · Temat…"), `IsValid`, `IsExcluded`, `IsAlreadySent`, `Fields` (`IReadOnlyDictionary`), indexer `this[string column]`, `DistinguishedName?` (AD rows), `MailboxKeys` (mailbox rows).
- Commands: `ImportFromDownloadsCommand` (newest `DownloadsPattern` file in Downloads), `ImportFilesCommand` (multi-select, filters of `ContactFileReader.Extensions`), `PasteCommand` (clipboard `Text`, else CF_HTML text via `HtmlToText`), `AddManualCommand` (multiline prompt), `AddFromSelectionCommand`, `PickUsersCommand` (object picker, users/contacts), `AddGroupMembersCommand` (pick a group → members, nested via `InChain`, users/contacts only), `IncludeAllCommand`, `IncludeNoneCommand`, `RemoveSelectedCommand`, `ExportCommand` (CSV: Nazwa;E-mail;Źródło;Status + columns, protectFormulas), `ClearCommand` (confirm), `ExclusionsFromFileCommand`, `ExclusionsFromClipboardCommand` (clipboard, else prompt), `ClearExclusionsCommand`, `ShowJournalCommand` (report: count, oldest, newest, retention; buttons via follow-up confirms: prune older than retention, clear all), `SkipAlreadySent` (bool).
- Methods: `Task AddFilesAsync(IReadOnlyList<string> paths)` (drag and drop), `AddRows(IEnumerable<ImportedContact>, string source)` → (added, duplicates); `SendList()` → included, valid, not excluded, not already sent (when skipping); `Units(bool groupByAddress)` → `CorrespondenceUnit`s; `KnownColumns`.
- AD rows: `DirectoryRowBuilder` (internal, testable) reads objects by DN (`DirectoryLookup`) with attributes = U1 `AccountAttributesFor` of all field expressions, evaluates every `DirectoryFieldDefinition` (target Person) with `UserMessageFields.Render` (`UserMessageFieldData` with the object as account, `AccountStatus.Evaluate`, salutation options from `UserMessageSettings.Salutations`), Name = displayName (else RDN), Email = `mail` else primary `SMTP:` proxy, source "AD"; objects without address → counted as invalid with a status; groups: members via `InChain` search (users/contacts), max 10 000.
- `DirectoryFields` (`ObservableCollection<DirectoryFieldViewModel>`: `Name`, `Expression`, `Target`, `Error`), `AddDirectoryFieldCommand`, `RemoveDirectoryFieldCommand`, `ResetDirectoryFieldsCommand`; saved to settings.

**`MailboxOwnersPageViewModel`**
- Scope: `ScopeKind` (`MailboxScopeKind { Selection, Domain, OrganizationalUnit }`), `ScopeText`, `BrowseScopeCommand` (container picker), `SelectionChoices`/`Selection` (`MailboxSelection`), `IsExchangeSchemaPresent` (schema has `msExchRecipientTypeDetails`; without it only `AllAccounts` is offered and a hint says so), `ExtraFilter` + `ExtraFilterError`, `NameSourceChoices`/`NameSource`, `NameAttribute`, `PersonBaseText`, `BrowsePersonBaseCommand`, `ClearPersonBaseCommand`, `EnabledOwnersOnly`, `GroupByOwner`.
- Run: `FindOwnersCommand` (reads mailboxes: `And(QuickFilters.Users, type filter, extra filter)` under the scope, attributes = mailbox needs + mailbox field expressions; then persons of the person base: `And(QuickFilters.Users, Or(Present(givenName), Present(sn), Present(displayName)))`, attributes = person needs + owner field expressions; builds `PersonIndex`; matches; streams rows), `CancelCommand`, `ProgressText` ("Skrzynki: 120 · osoby: 4 210"), limits: 20 000 mailboxes, 300 000 persons (beyond → error asking to narrow the base).
- Rows `MailboxOwnerRowViewModel`: `Include` (ticked only while the row has an owner to write to – a valid owner address, from the automatic owner, a chosen candidate or user, or a typed address – and following it until the operator ticks or clears it), `MailboxName`, `MailboxAddress`, `MailboxLocation`, `MailboxTypeText`, `OwnerNameText` (as parsed), `Status` (`OwnerMatchStatus` + `Manual`, `Excluded` via `RowState`), `StatusText`, `StatusKind` (Ok/Warning/Error for colour), `Candidates` (`CandidateOption`: `Text` "Jan Kowalski – IT, jan.kowalski@…", `IsDelegate`, `Level`), `SelectedCandidate` (two-way; the matcher's best when chosen automatically, 3.7 step 6; selecting sets Manual when it differs from it), `OwnerDisplayName`, `OwnerAddress`, `OwnerDepartment`, `DetailText` (why: level, delegate, disabled), `HasCandidates`, `NeedsReview`.
- `Filter` (`MailboxRowFilter { All, Ready, NeedsReview, NotFound, Excluded }`), `SummaryText` ("Dopasowane: 90 · do sprawdzenia: 12 · nieznalezione: 8 · wykluczone: 2").
- Row commands (parameter = row): `PickOwnerCommand` (object picker users → reads the person and sets Manual), `TypeOwnerAddressCommand` (prompt, validates with `EmailAddressText`; the address of a candidate chooses that person, a chosen person without an address keeps the typed one as theirs, and a chosen person whose own address differs is dropped – their name, salutation and fields never go to another address), `ExcludeCommand`, `RestoreCommand`.
- `MailboxFields` (`ObservableCollection<DirectoryFieldViewModel>`, mailbox + owner definitions), add/remove/reset.
- `AddToRecipientsCommand`: rows with an owner address and `Include` → recipient rows (Email = owner address, Name = owner display name, Fields = mailbox fields + owner fields, `MailboxKeys` = mailbox address or DN – the row's `JournalScope`, section 7.6 –, Source "Skrzynki"); replaces earlier "Skrzynki" rows; loads the built-in that fits `GroupByOwner` (off by default → `mailbox-still-needed`, on → `mailbox-still-needed-grouped`) when the message body is empty or still the other built-in as loaded (`MessagePageViewModel.UseMailboxTemplateAsync`; "as loaded" is `HeldBuiltIn`: subject, body, list item template, voting, font and the custom fields' names and kinds equal the built-in's – grouping aside –, so a font, list item, voting or field change, or a saved copy with voting, makes it the operator's message, which is never replaced; no question then, the custom fields' values such as the chosen deadline kept; the fitting built-in held as loaded stays), then sets `GroupByAddress = GroupByOwner`; switches to Recipients.

**`MessagePageViewModel`** (implements `IRichEditorHost`, section 9.2)
- `Subject`, `Salutation`, `BodyHtml` (two-way with the editor), `BodyImages` (`IReadOnlyDictionary<string, MessageImage>`; the editor registers images through the host), `IsBodyEmpty`.
- `FieldChoices` (`IReadOnlyList<string>` of `{{field}}` texts: built-ins without list/count unless grouping, custom fields, columns minus built-in duplicates), `InsertFieldRequested` handled by the view (the editor inserts at the caret; the subject box inserts at its caret).
- `Signatures` (`IReadOnlyList<SignatureOption>` with "(bez podpisu)" first), `SelectedSignature`, `SignatureProblemsText`, `RefreshSignaturesCommand`.
- `Attachments` (`ObservableCollection<AttachmentViewModel>`: `Name`, `Path`, `SizeText`, `Exists`), `SelectedAttachments`, `AttachmentsHeaderText` ("Załączniki (2, 1,4 MB)"), `HasAttachments` (header turns red), `AddAttachmentsCommand`, `RemoveAttachmentsCommand`, `ClearAttachmentsCommand`, `AddAttachmentFilesAsync(paths)` (drop); warning above `AttachmentWarningBytes`.
- `SendModeChoices`/`SendMode`, `BccChunkSize`, `DelaySeconds`, `ReadReceipt`, `DeliverLater`, `DeliverDate` (`DateTime?`, default tomorrow), `DeliverTime` ("08:00"), `DeliverError`, `TryGetDeferredDelivery(out DateTimeOffset?)` (MailerWPiA rules: future > now+1 min, ≤ 365 days, `H:mm`/`HH:mm`/`H`).
- `UseVoting`, `VotingOptions`.
- `CustomFields` (`ObservableCollection<CustomFieldViewModel>`: `Name`, `Value`, `Error`; date fields: `Kind`, `IsDate`, `Date` (`DateTime?`), `Format`, `Sample` (the formatted date or ""), static `FormatChoices` = `CustomFieldFormats.DateChoices`; a date field without a date is an error), `AddCustomFieldCommand`, `AddDateFieldCommand`, `RemoveCustomFieldCommand`; `GroupByAddress`, `ListItemTemplate`.
- Base style: `FontFamily` (CSS family list), `FontSize` (points), `LineHeight` ("normal" or a number) with `FontFamilyChoices`, `FontSizeChoices`, `LineHeightChoices` (`Option` items, the current value always among them; "normal" → "Pojedyncza"), `BaseStyle`, `ApplyBaseStyle(style)` (templates and the last draft; a template without font values keeps the window's); a value the message cannot use goes back to the previous one.
- Templates: `Templates` (`IReadOnlyList<TemplateOption>`: built-ins then saved), `SelectedTemplate` (loading asks when the page holds a message the template would change – subject, body, list item template or custom fields – unless it holds a built-in as it was loaded (`HeldBuiltIn`); loading a built-in over a built-in keeps the values of the custom fields of the same name and kind (the deadline chosen), while any other template over a built-in whose values were changed asks first), `TemplateName`, `SaveTemplateCommand`, `DeleteTemplateCommand`, `ExportTemplatesCommand`, `ImportTemplatesCommand`, `NewMessageCommand` (confirm, clears subject/body/fields).
- `Draft()` → `CorrespondenceDraft` (with the loaded signature and the base style); `ValidateForSend()` → localized errors (empty subject/body, missing attachments, deferred delivery, custom field errors, images over limits).
- `PasteFormattedAsync` (Ctrl+V through the editor host): `HtmlPasteWorkflow` (U1c) with `Engine = null` → `FieldTemplate.RepairHtml` → `RichPaste(fragment, images)`; problems shown in `PasteProblemsText`.

**`PreviewPageViewModel`**
- `PreviewRecipients` (first 500 units: "Name <email>"; an address with several messages – one per mailbox, rows of one address without grouping – adds the mailbox, "… – rekrutacja@contoso.test", else the number, "… (2)"), `SelectedRecipient` (kept per message, not per address, when the preview refreshes: the unit holding the chosen first row, else the same address and mailbox, else the same address – `PreviewPageViewModel.IndexOfSame`; "Podgląd z Outlooka", "Otwórz w przeglądarce" and the test to self use that message), `WidthChoices`/`Width`, `Preview` (`HtmlPreviewContent`: the card of MailerWPiA – Od/Do/Temat/Załączniki header, body, width), `InfoText` ("obrazków osadzonych: 3 • pola: zwrot, Miejscowość"), `Warnings` (`IReadOnlyList<string>`: unknown fields with the available list, the setup – a date field the message uses dated before today, `{{podpis}}` in the body without a chosen signature, a column field in the subject of messages grouped by address (it gets the group's first row) –, empty fields "Pole {{X}} jest puste u 12 z 95 odbiorców" (`{{podpis}}` never), lint findings, size), `HasWarnings`, `IsFromOutlook`, `OkText` ("Wygląda dobrze – …").
- `RefreshCommand` (also on tab entry and recipient/width change; debounced 200 ms), `PreviewViaOutlookCommand` (session `RenderAsync`, images from memory), `OpenInBrowserCommand` (`IBrowserPreview`), `SaveHtmlCommand` (folder + file name: `podglad_wiadomosci.html` and image files next to it, cid → file names).

**`SendPageViewModel`**
- `SummaryText` (MailerWPiA summary: count with skipped/invalid, sender, on behalf, mode, subject, signature, attachments, deferred, voting, estimated time), `RefreshSummaryCommand`.
- `SendTestCommand` (to the selected account's address, else `Profile.CurrentUserAddress`, else prompt; subject prefix "[TEST] "; the message chosen on the preview – its fields and rows, matched as `SelectedRecipient` is kept – or the first unit), `CreateDraftsCommand`, `SendCommand` (confirmation as MailerWPiA's text; typed confirmation of the count above `ConfirmationThreshold`; warning when delay < 2 s with more than 50 messages), `StopCommand`, `RetryFailedCommand`, `ExportReportCommand` (text report: date, subject, lines), `IsSending`, `ProgressValue`, `ProgressMaximum`, `ProgressText` ("Wysyłam 12 z 95 · zostało ok. 4 min"), `SentCount`, `FailedCount`, `SkippedCount`, `Results` (`ObservableCollection<SendResultLine>`: `Text`, `IsSuccess`).
- Engine `CorrespondenceSender` (internal, testable): per unit build `OutlookMessage` (rendered subject/html/images; attachments; account; on behalf; receipt; deferred; voting; campaign id per run + keys); Individual → one message per unit, To = unit address; Bcc → chunks of `BccChunkSize`, To = own address (account / current user), Bcc = addresses, subject/body merged with an empty row (fields of the first row are **not** used; unknown/empty warnings apply); stop on cancellation; 5 failures in a row → stop with the MailerWPiA message; `StopsRun` failure (an account missing from the profile among them) → stop at once; delay between messages with cancellation; ETA = left × (delay + average seconds per message); after each success in Send mode: row status "wysłano" + `Journal.AppendAsync(address, subject as typed)`; Draft mode: "wersja robocza", no journal; test: "test wysłany", no journal; failure: "błąd – {code text}" (+ "zapisana w Wersjach roboczych" when `SavedAsDraft`).
- One session-log entry per run (`AuditEntry`: action `SendCorrespondence`, server = "Outlook", OldValue = subject template? **no** – `L` text "Korespondencja (Outlook)", NewValue "ok/total", success flag) – no addresses, no subject.

**`ResponsesPageViewModel`**
- `Since` (default 90 days back), `Campaigns` (`OutlookCampaign` options: "28.10.2026 · Temat · 95 wiad."), `SelectedCampaign`, `LoadCampaignsCommand`, `LoadResponsesCommand`, `Rows` (`ResponseRowViewModel`: `Recipient`, `Keys` (mailboxes), `Response` (vote text or "brak odpowiedzi"), `RespondedAtText`, `Kind` Yes/No/Other/None by matching the first/second voting option), `SummaryText` ("Odpowiedzi: 40 z 95 · Tak: 30 · Nie: 10"), `ExportCommand` (CSV, protectFormulas).

### 6.5 Localization

Key prefix `Correspondence.*` with nested `L.Correspondence*` classes in `Localization/L.Correspondence*.cs` (one file
per page: `L.Correspondence.cs`, `L.CorrespondenceRecipients.cs`, `L.CorrespondenceMailboxes.cs`,
`L.CorrespondenceMessage.cs`, `L.CorrespondencePreview.cs`, `L.CorrespondenceSend.cs`,
`L.CorrespondenceResponses.cs`, `L.CorrespondenceOutlook.cs`). Dynamic families: `Correspondence.Outlook.Code.{code}`,
`Correspondence.Outlook.State.{state}`, `Correspondence.Mailboxes.Status.{status}`, `.Selection.{value}`,
`.NameSource.{value}`, `.Filter.{value}`, `Correspondence.Lint.{kind}`, `Correspondence.SendMode.{value}`,
`Correspondence.Width.{value}`, `Correspondence.Status.{kind}`, `Correspondence.Format.{format}`;
`CorrespondenceText.AllDynamicKeys()` feeds a localization test. In both resx files the entries of each track live
between markers `<!-- U5 <track>: begin -->` / `<!-- U5 <track>: end -->` created by the scaffold (section 13), so the
tracks merge without conflicts.

## 7. Behaviour details

### 7.1 Recipients

- Import adds rows, de-duplicates by address against existing rows (MailerWPiA), keeps the column order of first
  appearance, reports "Wczytano plik: 120 nowych, 3 powtórzone, 2 błędne adresy" in the status.
- Files are read with `File.ReadAllBytesAsync` (≤ 50 MiB) on the thread pool; `ContactFileReader.Read` with the ANSI
  code page from `IRegionalSettings`.
- Downloads: newest file matching `DownloadsPattern` (default `*kontakty*.csv`; MailerWPiA also matched
  `prawo-pl_kontakty_*.csv`, which the default covers).
- Exclusions ("Nie wysyłać") are in memory only (as MailerWPiA); count shown.
- Already sent: when `SkipAlreadySent`, rows whose address is in the journal are skipped and show
  `PreviouslySentText` – a mailbox owner row only when its address was sent for its own mailbox (section 7.6).
- Filter box: email/name/source/fields contains (folded).
- Grid shows Wyślij, E-mail, Nazwa, Źródło, Status, Wcześniej, then the field columns (horizontal scroll).

### 7.2 Message

- The editor (section 9.2) produces `BodyHtml`; the subject is plain text with `{{fields}}`.
- Signature: loaded when selected (parse on the thread pool), problems shown; images embedded. It goes after the body
  (after a line break), or where the body has `{{podpis}}` (section 7.7).
- Attachments: added to **every** message of the run (MailerWPiA warning text kept); list not persisted.
- Custom fields are text ("Dodaj pole") or dates ("Dodaj datę": a date picker, an editable format with
  `CustomFieldFormats.DateChoices`, the sample as messages write it).
- "Czcionka", "Rozmiar", "Interlinia": the base style of the message (the wrapping div); the editor shows the body in it
  (`RichMessageEditor.BaseFontFamily`/`BaseFontSize`, bound by the window).
- Last draft (subject, body, images, custom fields with their kind and format, list item template, voting, grouping,
  salutation, base style) and the options are saved on close and before sending; template images go to
  `MessageImageStore`.

### 7.3 Preview

As MailerWPiA: the card (max-width 700/380 px or full, grey background, header Od/Do/Temat/Załączniki); warnings:
unknown fields (with "Dostępne: …"), the setup (a past date field, `{{podpis}}` without a signature, a row field in a
grouped subject), empty fields per field over the send list, lint, message size. The preview HTML and images stay in
memory.

### 7.4 Sending

As section 6.4. The Outlook session is created on demand (`EnsureConnectedAsync`) and kept for the window's life
(disposed on close). Sending runs on the thread pool; Outlook calls go to the session's STA thread; the UI stays
responsive (progress through the dispatcher).

### 7.5 Responses

Requires a connected session. Campaign ids are only in Outlook items (nothing local). Rows map votes to keys (mailbox
addresses) so the operator sees which mailbox each answer concerns.

### 7.6 Journal

File `correspondence/journal.txt` next to `settings.json`: header line, then one line per sent address
(`JournalFile.Line`). Key: `correspondence/journal.key` (DPAPI-protected 32 random bytes, CurrentUser, entropy
"AdMng.CorrespondenceJournal"). `Find` hashes the normalized address with the key and looks it up in a dictionary
(latest entry wins). Retention pruning rewrites the file atomically (temp + move) and prunes the `journal-<date>.old`
backups the same way; clearing the journal deletes the backups first. The journal never stores the address.

**Per mailbox.** A mailbox owner row (it has a mailbox key: the mailbox address, else its DN) is "already sent" only
for that mailbox: its `CorrespondenceRecipientViewModel.JournalScope` is the key, and the journal hashes the owner's
address together with it (`JournalHashing.Hash(key, address, scope)`). Every reader of a row's mark uses the row's own
scope – the marks and the "Wcześniej" text of the recipients page, the send list and the summary, the marks set after
a send, the rows of a retry. A send records **pairs**: each row of the message with its own scope
(`CorrespondenceSendJob.JournalTargets`), never the addresses of a BCC chunk crossed with its keys; rows without a
mailbox key stay unscoped. So one person with three mailboxes gets three messages (three entries) and a later run for a
fourth mailbox is not skipped. A line does not say whether it is scoped; unscoped entries of older journals keep
answering for unscoped rows only (never for a mailbox row: an invitation sent to the owner once does not skip the
mailbox query).

### 7.7 Template after the operator's example (1:1)

The built-in mailbox templates (section 6.1) reproduce the message the operator's team sends today to ask whether a
mailbox is still needed – the same text, spacing, colours and structure. The two variables are the deadline and the
mailbox name in the subject; the rest is the example message, kept as embedded resources
(`src/AdMng.Presentation/Correspondence/Templates/mailbox-still-needed.html` and `…-grouped.html`, LF line ends
pinned in `.gitattributes`) that must not be "improved": its tables, cell borders and backgrounds, paddings and
paragraph margins are what makes it 1:1. Neither the people of the example, nor the mailbox names, the sender's address
or the signature block are part of it: the signature comes from the operator's Outlook signature files at run time.

- **One message per mailbox** (`mailbox-still-needed`): the body says the mailbox is "wskazana w temacie tej
  wiadomości", the subject is `Weryfikacja skrzynki pocztowej: {{Skrzynka}}`. That is why "Jedna wiadomość na
  właściciela" is off by default; when it is on, `mailbox-still-needed-grouped` lists the owner's mailboxes in
  `{{lista}}` ("{{Skrzynka}} ({{Adres skrzynki}})"). The preview warns when grouping is on and the subject uses a row
  field such as `{{Skrzynka}}` (the subject gets the group's first row).
- **Date fields.** `{{Termin}}` is a custom field of kind Date (`CustomFieldKind.Date`): its value is `yyyy-MM-dd`, it
  renders in the Polish culture with its format – by default `d MMMM yyyy'r.'`, "15 listopada 2026r." exactly as the
  example (genitive month, no space before "r.") – and the built-ins set it to today + 14 days (the library's clock).
  The window picks the date in a calendar, offers the usual formats in an editable box and shows the sample; a date
  field without a date blocks sending, a past date is a preview warning.
- **`{{podpis}}`.** The example ends with "Z poważaniem", the team's name and the signature right after it, without the
  blank line the renderer puts before an appended signature. `{{podpis}}` places the chosen signature there (its
  paragraph split away – no empty paragraph is left) and the signature is then not appended; without a signature it
  renders nothing (a preview warning, never an empty field). Signature images still go with the message.
- **Base style.** The example is Aptos 12 pt with single line spacing, while earlier messages were Calibri 11 pt, 1.4:
  `MessageBaseStyle` on the draft and the template sets the wrapping div (a template without font values – saved
  before they existed – keeps the window's, i.e. its last values); the window keeps the last values in the last draft
  and shows "Czcionka", "Rozmiar", "Interlinia"; the editor shows the body in that font.
- **No voting buttons** (the example has none); the answers come as replies.
- **The help desk is a field.** The example sends people to `hd.uni.lodz.pl`; the operator wanted to keep it but be able
  to change it. The link is `<a href="{{Link do systemu zgłoszeń}}">…{{System zgłoszeń}}…</a>` with the custom fields
  *System zgłoszeń* = `hd.uni.lodz.pl` (the text) and *Link do systemu zgłoszeń* = `https://{{System zgłoszeń}}` (a
  custom field may use other custom fields, section 3.2), so another help desk is one field. An `href` that starts with a
  field is what the template sanitizer (`AllowFieldUrls`) and the editor (`LinkUri`) accept; .NET cannot even hold
  `https://{{…}}` as a `Uri`. After the merge the document sanitizer checks the real address again. With the defaults the
  message is the example, byte for byte in the link.
- **Journal per mailbox** (section 7.6): one owner of three mailboxes gets three messages, each recorded for its mailbox.

With `{{Termin}}` = "15 listopada 2026r.", the signature in place of `{{podpis}}` and the wrapping style
`font-family:Aptos,sans-serif;font-size:12pt;color:#000000;line-height:normal`, Chromium renders the body pixel-identical
to the example (checked when the template was taken over). `GoldenTemplateRenderTests` (Presentation) renders the
built-in through the message page and the renderer for a synthetic mailbox row and compares the body with the embedded
HTML semantically – element tree, attributes, text runs and the CSS value of every property – and checks the base style
of the wrapper; the library tests pin the bytes of the embedded files. Until the message sanitizer allows Outlook's
`mso-margin-top-alt`/`-bottom-alt` hints (inert in browsers, used by classic Outlook's Word engine) they are the only
declarations the test lets go missing. The editor's side – keeping the look when the operator edits the template – is
section 9.2.

## 8. AD sources and rule 4

All AD reads go through the window's session. Row building and mailbox matching run on the thread pool with the
window's cancellation (`SerialCancellation`), and stream progress to the UI. Field expressions referencing forbidden
attributes are refused (error on the definition). Optional Exchange attributes are only requested when the schema has
them (`session.Schema.FindAttribute`).

## 9. App: `src/AdMng.App/Correspondence`

### 9.1 Windows and registration

- `CorrespondenceWindow.xaml(.cs)` – a `TabControl` (`CorrespondenceTabs`) with tabs `MailboxesTab` (visible on
  `IsMailboxesTabVisible`), `RecipientsTab` ("1 · Odbiorcy"), `MessageTab` ("2 · Wiadomość"), `PreviewTab`
  ("3 · Podgląd"), `SendTab` ("4 · Wysyłka"), `ResponsesTab` ("5 · Odpowiedzi"); a status bar with `OutlookStateText`
  and `StatusText`; named elements per section 6.4 for the UI tests (e.g. `RecipientsGrid`, `ImportFilesButton`,
  `PasteRecipientsButton`, `SearchBox`, `MessageEditor`, `SubjectBox`, `SignatureBox`, `AttachmentsList`,
  `PreviewView`, `PreviewWarningsList`, `SendButton`, `ResultsList`, `MailboxesGrid`, `FindOwnersButton`,
  `ResponsesGrid`). Size 1320×900 (min 1000×680), fits 1366×768 by `WorkArea` clamp.
- Recipients grid: `ListView`+`GridView` with fixed columns and dynamic field columns rebuilt on `Columns` change
  (BatchEditor pattern), row indexer bindings `[{column}]`, virtualization on; drop target for files.
- `CorrespondenceAppRegistration.AddAdMngCorrespondenceApp()`: `IOutlookClient` → `OutlookClient` (singleton),
  `IOutlookSignatureSource` → `OutlookSignatureFiles`, `IBrowserPreview` → `LoopbackBrowserPreview`,
  `ICorrespondenceKeyProtector` → `DpapiKeyProtector`, `ICorrespondencePlatform` → `WindowsCorrespondencePlatform`,
  `ICorrespondenceDialogs` + `ICorrespondenceLauncher` → `DialogService` (scoped), `AddWindow<CorrespondenceViewModel,
  CorrespondenceWindow>()`, `AddWindow<TextPromptViewModel, TextPromptWindow>()`, `AddWindow<ReportViewModel,
  ReportWindow>()`; `ToolWindows.ShowCorrespondence()` (singleton key `tools:correspondence`).
- `HostTests` lists: shared – `IOutlookClient`, `IOutlookSignatureSource`, `IBrowserPreview`,
  `ICorrespondenceKeyProtector`, `ICorrespondencePlatform`; per connection – `ICorrespondenceDialogs`,
  `ICorrespondenceLauncher`.
- `UiTestContext` replaces `IOutlookClient` with `FakeOutlookClient` and `IOutlookSignatureSource` with a fake
  (UseFake pattern). `SmokeTest` builds its host with the same replacements (it must never start Outlook) and renders
  the window with every tab.

### 9.2 Editor – `Controls/RichMessageEditor.xaml(.cs)` + `Correspondence/FlowDocumentHtml.cs`

- Toolbar: B, I, U, bullets, numbering, link (prompt), clear formatting, paste plain, field combo + "Wstaw pole".
- `RichTextBox` (`MessageEditorBox`), `AcceptsTab`, spell check (pl-PL when available), shown in the base font of the
  message (below; Calibri 11 pt by default).
- Dependency properties: `Html` (two-way, debounced 300 ms from the document), `Host` (`IRichEditorHost`),
  `FieldChoices`, `BaseFontFamily` (CSS family list, default `Calibri,'Segoe UI',Arial,sans-serif` =
  `MessageBaseStyle.Default.FontFamily`) and `BaseFontSize` (points, default 11) – the base style of the message (the
  window binds them to the message page's font controls).
- `IRichEditorHost` (Presentation): `Task<RichPaste?> PasteFormattedAsync()` (U1c workflow over the clipboard),
  `MessageImage? AddImage(byte[] data, string? fileName)` (validation, error text through `ReportEditorError`),
  `IReadOnlyDictionary<string, MessageImage> Images`, `void ReportEditorError(string text)`.
- Ctrl+V / paste command: `DataObject.Pasting` cancelled; CF_HTML → `Host.PasteFormattedAsync()` → fragment → blocks
  inserted at the caret; bitmap only → `AddImage(png)` → image; text only → plain text. "Wklej bez formatowania" =
  text.
- `FlowDocumentHtml.ToHtml(FlowDocument, Func<Image, string?> contentIdOf)`: MailerWPiA's `FromDocument` (paragraph
  margin 0 0 8pt, alignment, lists `ol`/`ul`, tables with borders, runs: bold, italic, underline/strike, colour (not
  black), background (not white), font size, font family, hyperlinks, line breaks, images
  `<img src="cid:…" width height style="max-width:100%">`), output re-sanitized by the U1c sanitizer (fragment,
  `InlineImageIds`). The document's font is the base font: a run in its first family (a name starting with it) and
  within 0.6 pt of its size is written without a font of its own, so the base style of the message applies.
- `FlowDocumentHtml.ToDocument(string html, IReadOnlyDictionary<string, MessageImage> images)`: the inverse for the
  sanitized subset (`p`, `div`, `br`, `b/strong`, `i/em`, `u`, `s/strike`, `span` with the styles above, `font`
  colour/face/size, `a`, `ul/ol/li` (nested), `table/tr/td/th` (colspan/rowspan), `h1–h6` (bold, sizes), `img cid:` →
  `InlineUIContainer(Image)` tagged with the content id, `hr`); anything else → text. Parsing with AngleSharp (already
  a Core dependency, referenced through Core). Blocks get MailerWPiA's spacing and tables its grid – what a paste gets.
- **The body keeps its layout** (`ToDocument(html, images, keepLayout: true)`, how the editor loads its own body): a
  template written outside the editor – the built-in mailbox template is an e-mail layout of borderless tables, boxes
  with a coloured left border, Word's paragraph spacing – stays as its HTML writes it, also after an edit:
  - Margins of `p`, `h1–h6`, `ul/ol`, `li` and tables → `Block.Margin` / `ListItem.Margin`. A `div` is not kept as an
    element: its margins go to the blocks inside it as a browser stacks them (its top margin collapses with the first
    block's, its bottom margin with the last block's – the larger wins –, left and right add up), its inherited CSS the
    editor cannot show (`line-height` …) to each of them; a single paragraph's margins go to its list item. Sides the
    HTML leaves out get the message's spacing (8 pt below a paragraph, a list, a grid table); a block without any
    margin or kept CSS is read as by a paste.
  - Tables: per-side cell borders (`border`, `border-top…`, the width/style/colour longhands; as a browser reads them)
    → `TableCell.BorderThickness` (device-independent pixels, 1 pt = 4/3 px) and one `BorderBrush` per cell (WPF has
    one; the colour of the widest visible side, the left one on a tie); cell padding (`padding` in any unit, else the
    table's `cellpadding`) → `TableCell.Padding`; cell backgrounds (white too); `cellspacing` → `Table.CellSpacing`;
    nested tables. `border="0"` is an e-mail layout: no grid and no margin or padding of the editor's own (a browser's
    1 px padding without `cellpadding`; without a margin in its CSS the table shows a margin of 0 – the default margin
    of a WPF `Table` is auto, a line above and below it – and none is written back, `HtmlLayout.ImplicitMargin`). Any
    other table without cell borders keeps the grid (1 px #BFBFBF, 5 px, `cellpadding="5" cellspacing="0"`,
    `border-collapse:collapse` written as shown). WPF's defaults are not a browser's (a `Table`'s margin is auto – a
    line above and below it –, so are a `Paragraph`'s and a `List`'s through the theme's styles; `CellSpacing` is 2 px):
    the editor sets the margin of every table and list it reads, the cell spacing and each cell's padding and border
    itself, and a paragraph without a margin of its own takes the editor's style (8 pt below it,
    `RichMessageEditor.xaml`).
  - A link's own `color` and `text-decoration` stay on the hyperlink (its text without a colour takes it; no underline of
    the runs), as Outlook writes `<a style="color:blue;text-decoration:underline"><b><span style="color:#0056B3">`.
  - The CSS the editor cannot show (`mso-margin-top-alt`, `mso-margin-bottom-alt`, `line-height`, `text-indent`,
    `max-width`, `width`, `border-collapse`, `border-spacing`, `table-layout`, `vertical-align` …) and the attributes of
    tables (`border cellpadding cellspacing width align bgcolor`) and cells (`width height align valign nowrap`) are
    kept as they were.
  - Everything is kept in the element's `Tag` (`FlowDocumentHtml.HtmlLayout`: the margin as CSS per side, padding and
    border declarations with the values applied, the kept declarations and attributes) and written back by `ToHtml`
    while the editor has not changed the property (margin, padding, border); a changed one is written from the WPF
    value (an indent with Ctrl+T, a paragraph pasted from the editor itself). WPF copies the `Tag` with the other local
    values when Enter splits a paragraph or a list item (`TextContainer.SetValues` copies every local value but the
    read-only ones), so the new one keeps the look of the one it came from.
  - Limits: a `div` with a border, padding or background of its own is still dissolved (its margins and inherited CSS
    kept, the box lost); a cell has one border colour in the editor (the HTML keeps every side's while the border is
    not changed); margins in `%` or `auto` show as 0 (written back as they were); the omitted top margin of a list
    gets the message's 0 instead of a browser's 1em (the same look when the block before it has a bottom margin of at
    least 1em).
- **Base font** (`BaseFontFamily`, `BaseFontSize`): the document shows the first family of the list
  (`FlowDocumentHtml.EditorFontFamily`: a generic family as Windows shows it – `sans-serif` → Arial –; WPF falls back to
  Arial itself when the font is not installed) at that size; text in it is written without a font of its own; "Wyczyść
  formatowanie" goes back to it. A base font change is no edit.
- **Dirty logic:** loading a body (also when the host or the base font changes) computes the document's own HTML; a
  flush (after the typing pause, on focus loss, before every button) writes `Html` only when the editor's text changed
  and its HTML differs from that – a template loaded and not edited stays byte for byte the HTML it came with.
- Sanitizer policy version 2 (U1c note 4.3) keeps Word's `mso-margin-top-alt` / `mso-margin-bottom-alt` (one length or
  `auto`): browsers ignore them, classic Outlook uses them for the space before and after a paragraph.
- Round trip tests (UI tests, Windows): ToDocument(ToHtml(doc)) is stable for every construct (also read with the
  layout kept); the built-in mailbox template keeps its look through the editor, compared semantically
  (`HtmlLayoutFacts`: stacked margins, per-side borders, paddings, backgrounds, table attributes, list, link, bold,
  colours); an edit of one word changes only that word; a loaded template not edited is not rewritten; Enter gives the
  new paragraph the margin and the stashed layout of the old one (WPF ignores `EnterParagraphBreak` without the keyboard
  focus, which an off-screen window may not get: the test then splits with `TextPointer.InsertParagraphBreak`, the
  same split).

### 9.3 Loopback browser preview – `Services/LoopbackBrowserPreview.cs`

`TcpListener` on `127.0.0.1:0`; per `OpenAsync` a random 128-bit token path `/{token}/` serving the preview document
(`Content-Type: text/html; charset=utf-8`, the CSP of `HtmlPreviewView` with `img-src` pointing to the same origin,
`Cache-Control: no-store`) and `/{token}/cid/{id}` images; any other path or method → 404; only GET; requests over
8 KiB of headers dropped; a document lives 15 minutes or until the app exits; then `Process.Start(url,
UseShellExecute)`. Nothing written to disk.

### 9.4 Other App services

- `DpapiKeyProtector` (`ProtectedData`, CurrentUser, entropy) – package `System.Security.Cryptography.ProtectedData`
  added centrally (App only); off Windows not used.
- `WindowsCorrespondencePlatform` – `SHGetKnownFolderPath(FOLDERID_Downloads)` with the MailerWPiA fallbacks.
- `DialogService.Correspondence.cs` – `OpenFilesAsync` (`OpenFileDialog Multiselect=true`), `PromptTextAsync`
  (`TextPromptWindow`), `ShowReportAsync` (`ReportWindow`: read-only monospace text, "Kopiuj" and "Zamknij"),
  `PickFolderAsync` (`OpenFolderDialog`), `ShowCorrespondenceAsync`.
- Main window: Tools menu item `tools.correspondence` after `tools.accountReview`; Action menu and list context menu
  `object.correspondence` after `object.sendMessage`, `object.mailboxOwners` after it.

### 9.5 Screenshots

`ScreenshotTestsCorrespondence` (new class): **26** `26-correspondence-recipients.png` (imported CSV with field
columns, statuses), **27** `27-correspondence-message.png` (editor with formatting and a field, signature, attachments,
options), **28** `28-correspondence-mailbox-owners.png` (Skrzynki tab after matching: matched, ambiguous with
candidates, not found, delegate-confirmed). Polish UI, light theme, fake Outlook.

## 10. Sample data

`tests/AdMng.Testing/FakeDirectoryBuilder.Mailboxes.cs` – `AddMailboxOwnerSample(SampleCompany company)` (call after
`AddSampleCompany`; with `AddSalutationSamples` for the ambiguous names) creates `OU=Skrzynki,OU=Firma` and sets
`Session.Info = Info with { HasExchangeSchema = true }`; `FakeSchema.Mailboxes.cs` adds `msExchRecipientTypeDetails`
(Large integer), `msExchDelegateListLink` and `publicDelegates` (DN, multi-valued) and allows them on users.
Mailboxes (all disabled user objects, `msExchRecipientTypeDetails` 4 unless stated, mail `<name>@contoso.test`):

| Mailbox (displayName / mail) | givenName / sn | Expected |
|---|---|---|
| Sekretariat IT / sekretariat.it | Jan / Kowalski | Matched (Jan Kowalski) |
| Rekrutacja / rekrutacja | Lukasz / Wisniewski | Matched, Folded (Łukasz Wiśniewski) |
| Księgowość – faktury / faktury | Wójcik / Małgorzata | Probable, Swapped |
| Biuro projektu / biuro.projektu | Anna / Nowak | Matched (Anna Nowak, salutation sample, displayName "dr Anna Nowak") |
| Archiwum / archiwum | Piotr / Wiśniewski | Matched (Piotr Wiśniewski) – Łukasz Wiśniewski is not a candidate (different given name) |
| Konferencja 2026 / konferencja2026 | Sasza / Kim | Matched (Sasza Kim) |
| Pomoc techniczna / pomoc | (empty) / (empty) | NoName (with the DisplayName source: "Pomoc techniczna" → NotFound) |
| Wydarzenia / wydarzenia | Tomasz / Wójcik + `msExchDelegateListLink` = Tomasz Wójcik | MatchedByDelegate |
| Sala 101 / sala101 (Room, 16) | Paweł / Lewandowski | Matched (rooms included only with SharedRoomsAndEquipment) |
| Stara skrzynka / stara | Zbigniew / Kaźmierczak | OwnerDisabled |
| Projekt UE / projekt.ue | Karolina / Malinowska | NotFound |
| Dział Kadr / kadry | Ewa / Zielińska | Matched (displayName with titles in the salutation sample) |
| Laboratorium / lab | Jan / Nowak – two persons "Jan Nowak" added (one in IT, one in Sprzedaż) | Ambiguous |
| Wnioski / wnioski | Agnieszka / Żółć (person has proxy address only) | Matched, owner address = primary SMTP proxy |

Returned record `MailboxOwnerSample` with the DNs and the extra persons. Polish names with diacritics are intentional.

## 11. Tests

- Core (`tests/AdMng.Core.Tests/Correspondence/…`): addresses, extractor (MailerWPiA cases + new rules), every file
  format with generated in-memory files (zip built in the test: XLSX with gaps, two sheets, shared strings with runs;
  DOCX with a table; ODS with repeated columns; RTF with `\'b3` cp1250 and `\u322?`; HTML with a table and mailto),
  decoding (BOM, UTF-8, cp1250), zip-bomb and size limits; fields (parse, fallback, repair split tokens, merge
  encodings, lookup order, custom fields, unknown/empty); grouping; renderer (signature, list, images referenced only,
  sanitized output); signature parser (charset, VML/conditional comments, inlined CSS, relative images, `..` refused);
  lint; journal hashing and file; mailbox types, name parser, person index, matcher (every row of section 10 in pure
  form plus tie-breaks); `{$created}` field.
- Outlook (`tests/AdMng.Outlook.Tests`): section 4.5.
- Presentation (`tests/AdMng.Presentation.Tests/Correspondence/…`): template library and import/export; journal
  (append, find, prune, clear, key loss, concurrent append); recipients (import each source with the fake dialogs,
  exclusions, skip sent, counts, filter, AD rows with fields and salutations, group members, invalid AD rows, export);
  mailbox owners (sample of section 10 end to end, filters, manual pick/type/exclude, add to recipients with grouping,
  no Exchange schema → AllAccounts only, extra filter validation, limits, cancellation); message (validation, deferred
  delivery parsing, custom fields, templates, paste through the U1c workflow); preview (card, warnings, Outlook render
  through the fake); send (individual, BCC chunks, drafts, test, 5 errors in a row, stop, StopsRun, delay with
  ManualTimeProvider, journal entries only for Send, retry failed, report, session log without content); responses;
  U1 Outlook transport selection, composer text and settings; localization (`CorrespondenceText.AllDynamicKeys`);
  `ConnectionLifetimes`.
- App UI tests (Windows): window opens from each command; tabs render without binding errors in pl and en; editor
  round trip and formatting buttons; paste of an Outlook fixture through the real clipboard into the editor; recipients
  paste and grid columns; preview fallback; send through the fake; mailbox owners run on the sample; responses; report
  window; prompt window; loopback preview serves the document and 404s other paths; `HostTests` lists; screenshots
  26–28; smoke test.

## 12. Pilot (cannot be verified in CI)

Real classic Outlook (Microsoft 365 Apps, 32- and 64-bit), Exchange Online and on-premises: attach to a running
Outlook and start one; accounts and send-as; on behalf of a shared mailbox; inline images (signature and body) shown
inline in Outlook, OWA and Gmail; attachments; deferred delivery; read receipt; voting buttons and reading votes back;
object model guard prompt on a machine without current antivirus; elevated AD Manager vs non-elevated Outlook
(`outlook.elevated`); "new Outlook" only (`outlook.notInstalled`); 300 messages with 3 s delay; real
`msExchRecipientTypeDetails` values in a hybrid directory; U1 messages through Outlook.

## 13. Work split

1. **Scaffold** (one step, sequential): this note; projects `src/AdMng.Outlook` and `tests/AdMng.Outlook.Tests` in
   `AdMng.slnx`; every public type of sections 3, 4.1 (public ones), 6 and 9 with its members and XML docs, bodies
   `throw new NotImplementedException()` where logic is needed, records complete; settings classes complete;
   `AddAdMngCorrespondence()` registrations; `L.Correspondence*` files with the keys known so far; resx markers for
   the tracks (`U5 core`, `U5 outlook`, `U5 shell`, `U5 recipients`, `U5 mailboxes`, `U5 message`, `U5 send`,
   `U5 app`) in both files, in this order, at the end; `MessageImage.Create` overload; `UserMessageTransportKind.Outlook`
   / choice / capabilities stubs; build at 0 warnings, existing tests green.
2. **Wave 1 (parallel, worktrees):** T1 Core import/fields/composition/journal + tests; T2 Core mailboxes + `$created`
   + sample/schema + tests; T3 AdMng.Outlook + U1 transport adapter + Outlook tests + `FakeOutlookClient`; T7a App
   services without view models (editor control + converter, loopback preview, DPAPI, platform, prompt/report windows,
   dialogs partial) + UI tests for them.
3. **Wave 2 (parallel, worktrees):** T4 shell (window VM, Outlook connection, settings, template library, journal
   service, launcher, commands, U1 Outlook integration) + tests; T5 recipients + mailbox owners pages + tests; T6
   message, preview, send, responses pages + tests; T7b App window XAML, menus, registration, SmokeTest, UI tests,
   screenshots; T8 docs (`docs/KORESPONDENCJA.md` in Polish, README, ROADMAP, ARCHITECTURE, CLAUDE.md).
4. **Integration:** merge, build, all Linux tests twice, Windows CI, adversarial review, fixes, release 0.7.0.

File ownership per track = the files of its sections; shared files (resx, `AppHost.cs`, `MainWindow.xaml`,
`ObjectListView.xaml`, `SmokeTest.cs`, `HostTests.cs`, `ConnectionLifetimes.cs`, `UiTestContext.cs`) are edited only by
the track named in their section, inside the scaffold markers where markers exist.

## 14. As built (waves 1 and 2a)

Waves 1 (T1 core, T2 mailboxes, T3 outlook, T7a app services) and 2a (T4 shell, T7b app window) are merged at
`b9d4416`. Public names and signatures of the scaffold were kept; the tracks only added members. This section lists what
differs from sections 1–13 or goes beyond them.

### 14.1 Core – import, fields, composition, journal (T1)

- **Added members:** `EmailAddressText.MaxNameLength`; `TextDecoding.FallbackCodePage` (1250 when the ANSI code page is
  0, unknown or UTF-8); `ContactExtractor.HeaderRows`, `UnnamedColumnFormat`; `FieldTemplate.MaxFallbackLength` (200),
  `Encode`; `CorrespondenceDocument.WrapperStyle`; `CorrespondenceRenderer.ListStart`;
  `RenderedCorrespondence.ImageBytes`, `ExceedsImageLimits` (the per-message limits of 3.6); `SignatureProblemCodes`
  ("code" or "code: detail"); `MailCompatibilityLint.MaxWidthPixels`; `JournalHashing.HashBytes`, `IsHash`.
  Review fixes: `ContactFileReader.ReadAddresses` – every address of a file (any column, a second address of a cell,
  hidden sheets too), which "Nie wysyłać z pliku" takes instead of the contacts (one per table row), as MailerWPiA's
  whole-text pass did; `ContactImportResult.HiddenSheets`, `HiddenRows`; `SpreadsheetLimits.MaxCharacters`;
  `HtmlImportRequest.ImageLimits` (`HtmlImageLimits`, default `UserMessages` = U1c's 1 MiB / 10 images / 2 MiB) and
  `HtmlPasteWorkflow(…, imageLimits)`, which the message page's paste sets to `CorrespondenceLimits.PastedImages` (10 MiB
  per image, 50 images, 25 MiB) – a pasted Outlook message with a 3 MB picture was refused before.
- **Table detection (3.1):** the header is one of the first 8 *non-blank* rows; a row holding a valid address is never a
  header; delimited text needs 2+ header cells. Every candidate row is tried: it is taken when its e-mail column gives at
  least one address, a row with 2+ non-empty cells before a one-cell title or caption ("Wykaz adresów e-mail …" merged
  over the columns, a Word caption row). Delimited text also refuses a lead-in sentence (last cell of 2+ words ending
  with `:`) and a "header" whose e-mail column gives no more addresses than the lines that hold an address but end
  before that column (prose split at commas, "Dzień dobry, adresy e-mail poniżej" over single addresses); in an accepted
  delimited table such a short line is free text, never lost (a row that has the e-mail cell but no address in it stays
  invalid – other columns are no recipients). When no candidate is taken the rows are free text – a sheet or document
  table as well as text (the invalid rows of an e-mail column without any address still count); a workbook keeps the
  tables of the sheets that have one, takes the other e-mail-headed sheets as free text and, when no sheet has a table,
  reads every cell as free text. E-mail column tiers: the exact names, then `poczta`, then a header containing `mail` –
  the first column of a tier with at least one valid address wins. The "contains name" rule skips
  first-name, surname, middle-name ("Middle Name", "Additional Name", "Drugie imię") and login headers (so "First name" +
  "Last name" still give the Imię+Nazwisko rule); a row whose name cell is empty (or only an address) takes Imię+Nazwisko
  when both columns exist. A column is named by its header on one line: line breaks (Excel's Alt+Enter, a multi-line
  quoted CSV header), tabs and other control characters become one space, since a `{{field}}` cannot hold a line break.
- **Free text:** contacts in file order; a later name fills an earlier empty one; addresses never start inside a word
  (`Łucja.Żak@x.pl` does not yield `ak@x.pl`), also not after an apostrophe or another RFC 5322 `atext` character inside
  a word (`o'brien@x.ie`, `biuro&sekretariat@x.pl` are refused whole, not cut to `brien@x.ie`, `sekretariat@x.pl`; a
  quote or bullet before an address is fine); `<mailto:…>` accepted. Text with a table reads only the lines before the
  header as free text (other columns such as "Inne e-maile" are not imported as recipients); when no delimiter gives a
  table with contacts, the whole text is free text. Address validity is stricter than MailerWPiA (no `..`, no dot at the
  ends of the local part, no hyphen at a label edge, length limits); junk list wider (`example.*` and subdomains,
  `*.sentry.io`, `*.w3.org`).
- **Documents:** DOCX, ODT and HTML tables go through `FromRows` (not tab-joined text); `gridSpan`/`colspan` add empty
  cells, a `vMerge` continuation repeats the cell above; `mailto:` links become "text <addr>" in every document format;
  deleted text, field codes, fallbacks and annotations are skipped. XLSX/ODS rows above a sheet's header are free text;
  with no e-mail column in any sheet every cell is free text. What Excel hides is left out of an XLSX import – sheets in
  the state `hidden`/`veryHidden`, rows marked `hidden` (by hand, a filter, a collapsed group) – and counted
  (`ContactImportResult.HiddenSheets`/`HiddenRows`, "pominięte ukryte arkusze: 1, pominięte ukryte wiersze: 15" in the
  status). XLSX numbers keep the stored value (3.1) except what `xl/styles.xml` formats as a date or time – built-in
  14–22 or a custom code with day, month, year, hour or second parts, 1900 or 1904 system – which becomes "15.03.2026",
  "15.03.2026 10:00" or "10:00" (Polish order whatever the code, rounded to the second; elapsed `[h]` stays a number), and
  a whole number in a code of digit placeholders and literals (`00\-000`, `00000000000`), which becomes "95-020" or keeps
  the leading zero of a PESEL (`XlsxNumberFormats`). RTF: a font's `\fcharset` wins over `\ansicpg`.
- **Limits:** 256 columns; 4 million cells and 2 million shared strings per workbook; 65 536 number formats and 65 536
  cell formats read from `xl/styles.xml` (a format code over 255 characters, longer than Excel allows, is ignored and its
  cells keep the stored value); zip entries ≤ 200 MiB, ≤ 20 000
  entries and ≤ 512 MiB in total; the ratio check (> 100) only for entries over 1 MiB; inflated bytes are counted (a
  forged size is caught). The text of a package's cells and paragraphs ≤ 50 Mi characters
  (`SpreadsheetLimits.MaxCharacters`: a shared string, a repeated ODF cell or a merged Word cell counted each time it
  appears, ODF `text:s` spaces as expanded; the shared strings of a workbook have their own 50 Mi) – a few kilobytes
  cannot expand to gigabytes. Unknown extension → read as text; unsupported compression, too much text or too deep HTML
  → `InvalidDataException` (English message, never file content).
- **Fields:** `FieldToken.Field`/`Fallback` are HTML-decoded and `&nbsp;` becomes a space. An empty value with a
  fallback is not reported as empty; an unknown field is always reported. HTML encoding encodes only `& < > " '` and
  turns CR/LF into `<br>`. `RepairHtml` never joins across block elements, `<br>`, comments or script/style, keeps the
  tags inside a token (moved, not deleted) and turns `&nbsp;` into a space. Without group values (`list`/`count` null)
  `lista`/`liczba` fall through to custom fields and columns. Inside a custom field's value `{{lista}}` is the list as text
  (`RecipientFieldSet(…, listText)`, the subject's ", "-joined items) – the value is text, so the HTML list showed as
  escaped markup in the body and as tags in the subject; the renderer builds the list when a used custom field needs it.
- **Renderer:** every unit is a group (a single row renders `{{lista}}` with one item, `{{liczba}}` = 1) – except that
  without grouping by address (`CorrespondenceDraft.GroupByAddress`, set from the message page) a column or custom field
  named "Lista"/"Liczba" wins over the built-in (`CorrespondenceRenderer.BuiltInApplies`), as "Wstaw pole" offers the
  column then and the built-ins only with grouping; such a column is encoded like any value and its paragraph is not
  split; the preview's empty-field count follows the same rule; the list is
  rendered only when used, and the paragraph holding `{{lista}}` is split around the `<ul>`; in the subject `{{lista}}` is
  the items joined by ", "; list items take `{{nazwa}}` from the unit; the list item template is plain text, as its text
  box suggests (its own text HTML-encoded, `FieldTemplate.TextToHtml`, so a typed `{{Skrzynka}} <{{Adres}}>` keeps the
  address; template import keeps it as text instead of sanitizing it as HTML); the signature is appended without merging
  fields; only images whose content id is referenced are kept; the sanitizer empties `<head>`, so the final document has
  no `<meta charset>` (previews add their own).
- **Signature parser:** decoding BOM → meta charset → strict UTF-8 → ANSI; web images removed with a problem, `data:`
  images embedded; removed `<link>`/`<meta>` are not problems. **Lint** works on the parsed document (pt converted to px,
  `max-width` ignored), one finding per kind except one `LinkTextMismatch` per host. **Grouping** compares normalized
  addresses; rows without an address are never grouped. **Journal file:** `Hash` throws for a key that is not 32 bytes;
  another format version or lines over 4 096 characters are skipped.

### 14.2 Core – mailboxes, `{$created}`, sample data (T2)

- **Matching rules refined (3.7):** *Partial* needs the same first given name also for compound surnames (otherwise "Anna
  Kowalska-Nowak" would be a candidate for "Jan Nowak", contradicting the Archiwum row of section 10); an owner name with
  a surname only is *Partial* against every person with that surname or compound part; *Swapped* needs both parts; the
  *DisplayName* level also drops "(…)" remarks and `Fold` treats commas as separators ("Kowalski, Jan", "Jan Kowalski
  (IT)"); *Exact* compares trimmed, whitespace-collapsed NFC strings. Candidates are ordered by score, folded display
  name, then DN, capped at 20 with the chosen one always kept. `OwnerMatch.Best` is also set for `OwnerDisabled` and
  `OwnerWithoutAddress`. All 14 rows of section 10 hold as written.
- **Parser:** `givenName`/`sn` are also cleaned of titles and remarks; a single word keeps its attribute's meaning; text
  sources split on commas, trim quotes, take the part after the last `:` and then after the last spaced dash (hyphen,
  en or em dash), and are cut at 256 characters; the Attribute source refuses secret and invalid attribute names.
- **Added members:** `MailboxTypes.Of`, `TypesOf`, `Filter` (null for `AllAccounts`), `HasExchangeAttributes`;
  `OwnerNameParser.MaxTextLength`, `FromNames`, `ParseText`, `AttributesFor`, `IsUsableAttribute`;
  `PersonCandidate.AttributesFor`/`From` (address = `mail`, else the valid primary SMTP proxy; display name falls back to
  the RDN); `MailboxEntry.AttributesFor`/`From` (delegates = `msExchDelegateListLink` ∪ `publicDelegates`);
  `MailboxOwnerMatcher.DelegateScore`, `EnabledScore`, `AddressScore`, `LevelOf`, `WeightOf`; `UserMessageFields.Created`.
- **Testing:** `FakeSchema.MailboxAttributes`, `CreateWithoutMailboxes()` (`Create` now goes through a private
  `CreateCore`); `FakeDirectoryBuilder.SharedMailboxType`, `RoomMailboxType`, `MailboxOwnerExpectation`,
  `MailboxOwnerSample` (`Expected`, `Mailboxes`, `Persons`, `ExpectedFor`). `AddMailboxOwnerSample` adds the salutation
  samples itself when missing; the second Jan Nowak is `jan.nowak2`; Agnieszka Żółć is in `OU=Sprzedaż`; the delegate
  attributes are plain multi-valued DN attributes in the fake. Measured once (Debug): 20 000 mailboxes against 300 000
  persons match in about 140 ms; building the index takes 3–8 s.

### 14.3 AdMng.Outlook and the U1 adapter (T3)

- **Contracts:** `OutlookException(OutlookFailure)` (no inner exception, no original message, `HResult` of the failure)
  for `RenderAsync`, `DiagnoseAsync`, `FindCampaignsAsync` and `ReadResponsesAsync`, which have no failure slot;
  `IOutlookUserMessageTransportFactory` in Core `Messaging/Transport.cs`. `OutlookClient(TimeProvider? time = null,
  IOutlookSignatureSource? signatures = null)` (signatures feed diagnostics), `OutlookClient.Attempts`,
  `AttemptDelay`; `OutlookUserMessageTransport.NotInstalledReason`, `Headers`; public `OutlookUserMessageTransportFactory`.
  Internal: `OutlookSession(StaWorker, application, profile, OutlookSessionOptions)`, `ComScope`, `OutlookConstants`,
  `MapiProperties`, `OutlookSteps`, `ComRetryPolicy`.
- **Worker:** an item cancelled while queued never runs; once running it completes (no half-built drafts); calls from
  the worker run inline; `DisposeAsync` lets queued items run and joins the thread (≤ 30 s); a failed filter registration
  never stops the queue. Session disposal releases the application on the worker and never quits Outlook. A
  `Disconnected` failure sets `IsConnected` false.
- **Stricter than MailerWPiA:** failing to set `SentOnBehalfOfName` or `Recipient.Type`, or both ways of setting
  `SendUsingAccount`, fails the item (no silent send from the wrong sender or a BCC left as To); `ReadReceipt`,
  `BodyFormat`, `Importance`, `Resolve` and `PropertyAccessor` writes stay best effort. An `Unknown` error of
  `ResolveAll` is ignored and each recipient's `Resolved` decides. A failure before `Save` closes the item with
  `Close(olDiscard)`.
- **Render** follows MailerWPiA's `RenderViaOutlook` (account, To only, no unresolved refusal, existing attachments only);
  a failure deletes the draft; an image that cannot be read back is left out unless Outlook is lost or busy.
- **An Outlook the client started** (no running one to attach to) runs without a window, and Outlook started by
  automation exits as soon as its last reference is released – while `Send()` only queues the item in the Outbox. The
  client therefore keeps one reference of its own on a keep-alive STA thread until it is disposed with the app
  (`IAsyncDisposable`, `IDisposable`), renewed when Outlook had to be started again; an Outlook the user runs is never
  held (MailerWPiA kept its application object for the program's life). Sessions come and go meanwhile.
- **Voting options** are joined with the Windows list separator (3.9, `OutlookVotingOptions`).
- **U1 mapping (section 5):** Busy/NotResponding are Transient only before the item was saved; after `Save` (a draft
  left in *Wersje robocze* – after a failed `Send()` it may even have gone out) the failure is Rejected with its code,
  never retried, since a retry would build and send a second copy. The `StopsRun` kinds are TransportFailed –
  InvalidAccount among them: every message of a U1 run is sent from the account of the settings.
- **Classification additions (4.3):** `E_ABORT` at the replyTo step = SecurityDenied; `MAPI_E_TOO_BIG` 0x80040305 =
  ItemTooLarge; `REGDB_E_CLASSNOTREG` 0x80040154 and `CO_E_CLASSSTRING` 0x800401F3 = NotInstalled; FileNotFound /
  DirectoryNotFound at the attachments step = AttachmentMissing; `PlatformNotSupportedException` = NotSupported; without
  elevation `CO_E_SERVER_EXEC_FAILURE`/`MK_E_UNAVAILABLE` at connect are Unknown. `TargetInvocationException` and
  single-inner `AggregateException` are unwrapped.
- **Campaigns (4.4):** the DASL date literal is UTC `yyyy-MM-dd HH:mm` (invariant), `SentOn >= since` re-checked in
  code (the literal is a pilot item); groups newest first; campaign ids must be 1–64 characters of `[A-Za-z0-9-]` (not
  written otherwise; `ReadResponsesAsync` throws – no DASL injection); recipient address = `Address`, else
  `PR_SMTP_ADDRESS`, else `ExchangeUser`, else `Name`; in a BCC campaign the sender's own To row is left out.
  **Correction:** `olTrackingReplied` is 7, not 3 (3 is `olTrackingNotRead`); the code does not depend on it – a vote is
  present when `AutoResponse` is not empty, and `RespondedAt` is `TrackingStatusTime` only then. Internet headers with an
  invalid name are dropped, control characters in values become spaces.
- **Temp files and signatures:** per-item folder `%TEMP%\AdMng-Outlook-{pid}-{16 hex}` (0700 on Unix), stale folders of
  dead processes removed once per client; signature references must be relative, without `:`, UNC, scheme, `.`/`..`
  segments (also URL-encoded), resolve inside the folder (symbolic links included), ≤ 2 MiB.
- **Testing:** `FakeOutlookClient` (availability, `ConnectFailure`, Polish `Profile`, per-recipient `Script`,
  `DefaultResult`, `Items`, `RenderResult`/`RenderFailure`, `DiagnoseResult`, `Signatures`, `Campaigns`, `Responses`,
  `ReadFailure`, the hooks `BeforeSend`, `BeforeRender` and `BeforeRead`, `Sessions`, `Connections`),
  `FakeOutlookSession`, `FakeOutlookItem`, `FakeOutlookUserMessageTransportFactory`. Not testable on Linux:
  `ComOutlookApplicationProvider`, `MessageFilterRegistration`, `ComRetryFilter`, real RCW release.

### 14.4 App services (T7a)

- **`LoopbackPreviewServer`** is a public Presentation type (Linux-testable): listener started at the first `Publish`,
  `http://127.0.0.1:{port}/{token}/` (128-bit token), `cid:` sources rewritten to `/{token}/cid/{id}`, CSP like
  `HtmlPreviewView` plus `base-uri`/`form-action`/`frame-ancestors 'none'`, `no-store`, `nosniff`, `no-referrer`. Beyond
  9.3: only GET with `Host` equal to `127.0.0.1:port` (DNS-rebinding guard); at most 20 documents (oldest dropped); a
  request not complete within 10 s is dropped. `LoopbackBrowserPreview` refuses an empty preview and logs a failed
  browser start by exception type only.
- **`DpapiKeyProtector`** (package `System.Security.Cryptography.ProtectedData` 10.0.13, App only);
  **`WindowsCorrespondencePlatform`** returns null when no Downloads folder exists (fallbacks `%USERPROFILE%\Downloads`,
  then Documents); `PickFolderAsync` reuses the GPO folder picker; `ShowCorrespondenceAsync` with no DNs and the
  Recipients start uses the singleton `ToolWindows.ShowCorrespondence`.
- **Editor:** `RichMessageEditor.FlushHtml()`, `InsertField(string)`, `PastePlainCommand`, `HtmlDelay`; `Html` is pushed
  300 ms after typing, on focus loss, on Unloaded or on `FlushHtml` – a body that was only loaded is never pushed back
  normalized. Paste: CF_HTML → `Host.PasteFormattedAsync`; a bitmap alone → `AddImage`; content copied from the editor in
  this process is pasted natively; Ctrl+Shift+V pastes plain text. The link prompt opens `TextPromptWindow` directly
  (no `IRichEditorHost` member; no connection strip): http(s), mailto, tel and `{{field}}` targets, a bare domain gets
  `https://`, an address `mailto:`, empty removes the link. The editing surface is white with black Calibri 11 pt in
  every theme, pl-PL spell check in try/catch.
- **`FlowDocumentHtml`:** lists keep type, start and nesting; several paragraphs in one list item become `div`s; cells
  keep background, colspan and rowspan; a run of spaces becomes a space plus `&nbsp;`; images carry
  `max-width:100%;height:auto` and alt text; headings and `th` are read as bold larger runs and written back as `p` +
  `span` (as MailerWPiA); blockquote indentation is not kept. Added: `FontFamilyName`, `BodyPoints`, `DefaultFontSize`,
  `HorizontalRuleTag`, `LinkBrush`, `NewDocument`, `CreateImage`, `ContentIdOf`, `IsHorizontalRule`.
- `TextPromptWindow` and `ReportWindow` gained an `Initialization` property; 24 `Correspondence.App.*` keys.

### 14.5 Shell (T4)

- **`OutlookFailureCodes`** (Core): one constant per `OutlookFailureKind`, `Sent`, `Failures`, `All`, `Of(kind)`,
  `IsKnown`; AdMng.Outlook's codes come from it. `MessageTemplateImage.IsValidUpTo(long)`.
- **Journal:** every file operation of every instance and process holds **`journal.lock`** (empty file opened with
  `FileShare.None`, an exclusive `flock` on Linux; 15 s timeout, retries every 5–25 ms) – .NET's `FileMode.Append` seeks
  instead of `O_APPEND`, so concurrent writers overwrote each other. Appends still use `FileShare.ReadWrite` and flush at
  once. The new key is protected before an old journal is moved aside (a host without a protector never moves it); an
  unreadable key also renames a journal that holds only its header. Added: `LockFileName`, `LockTimeout`, `LockPath`,
  `IsLoaded`; `AppendAsync` throws `InvalidOperationException` when the journal is not loaded. The section 2 amendment
  of CLAUDE.md should also name `journal.lock`.
- **Templates:** the built-in template has `<p>{{lista}}</p>` in a paragraph of its own; export v1 checks base64 images by
  hash and re-sanitizes HTML; added `FromDraft`, `Copy`; `LoadLastDraft()` returns a copy.
  `MessageImageStore.CollectGarbage(AppSettings)` (and `ReferencedImages`) keeps the images of U1 templates,
  Korespondencja templates and the last draft; `LoadAsync(…, maxImageBytes)`.
- **`OutlookConnectionViewModel`:** a connection starts outside the lock; "Sprawdź Outlooka" reports a diagnostics
  failure (`DiagFailed`); accounts are written out explicitly; one account gives "Outlook: połączono" without a count.
  Added: `Failure`, `FailureText`, `IsChecking`, `IsConnected`, `IsAvailable`, `AccountAddress`, `OwnAddress`, `Profile`,
  `ConnectInBackground()`, `ReportFailure(OutlookFailure, IOutlookSession?)`, `RefreshTexts()`. `EnsureConnectedAsync`
  returns null when Outlook cannot be used (the state says why); `ReportFailure` after `Disconnected`/`NotResponding`/
  `Elevated` makes the next use reconnect – reported by every page with the session it used, so a late failure of a
  session the window has replaced since (a held run, a preview, a read of the responses) changes nothing. After
  connecting, "Wyślij jako" shows the account of the settings, else the profile's default account (6.4).
- **`CorrespondenceViewModel`:** opening loads the last draft (images first through `Message.AddImage`) only into an
  empty message page; the end of the session also cancels a running owner query. Added (internal): `Initialization`,
  `SaveStateAsync`, `CurrentDraft`. `CorrespondenceText.OutlookStateKey`, `OutlookCodeKey`, `FailureText`.
- **U1 transport (section 5):** `UserMessageTransportSelector` gained an optional 6th parameter
  `IOutlookUserMessageTransportFactory?`, `OutlookTransports`, `SelectOutlook(UserMessageSettings)`;
  `UserMessageSettingsViewModel` gained `IsOutlookTransport`, `OutlookAccounts`, `LoadOutlookAccountsCommand`,
  `IsLoadingOutlookAccounts`, `OutlookAccountsText`; keys `UserMessages.Outlook.*` (`L.UserMessagesOutlook`). A client
  that throws while loading accounts is an unknown failure (HRESULT only).

### 14.6 App window (T7b)

- `CorrespondenceWindow` binds every member of 6.4 and 9.1 (checked by hand against the scaffold view models); field
  columns are rebuilt from `Recipients.Columns` with escaped indexer paths; the window is 1320×900 clamped to the work
  area. **Scope radios** (selected objects – shown only for a MailboxOwners request –, domain, OU) through
  `ChoiceConverter`; **`KeepChoiceConverter`** never writes a cleared candidate selection back when the virtualized
  mailbox grid recycles a row. Pending editor text is written to `BodyHtml` before every button, on Ctrl+Tab, on a click
  on a tab header and when the window closes (before the last draft is saved).
- **Registration:** `OutlookClient` with the registered signature source; `OutlookUserMessageTransportFactory` as a
  singleton over the same client (without it the U1 choice Outlook reports `OutlookNotSupported`). `OfflineOutlook`
  (reports NotInstalled, never starts Outlook) serves the smoke test, which also renders the prompt and report windows
  and the Outlook section of the settings. `UiTestContext` uses `FakeOutlookClient`, `FakeOutlookSignatureSource` (one
  Polish signature) and `FakeOutlookUserMessageTransportFactory`. Settings: a result line bound to
  `OutlookAccountsText`. Keys `Correspondence.App.Mailboxes.ScopeDomain`, `ScopeOu`, `ScopeSelection`.

### 14.7 Open issues after wave 2a

- The UI tests, smoke test, screenshots 26–28, `FlowDocumentHtml` writing, editor paste and every COM path run only on
  Windows (CI or pilot); the UI tests assume documented page behaviour of wave 2b (scope = Selection for a MailboxOwners
  request, `RecipientsAdded` switching tabs, Send marking rows, pasted TSV columns, signatures loaded at start).
- Recipients page (T5): decide whether the file's e-mail and name columns stay in `Columns` (the grid would show them
  next to the fixed E-mail and Nazwa columns); localize `SignatureProblemCodes`, `MailLintKind`, `ContactSourceFormat`
  and `InvalidDataException` of `ContactFileReader`.
- Message/send pages (T6): `AddImage` idempotent; BCC units with `Rows = []`; `ValidateForSend` with
  `ExceedsImageLimits` and `MailCompatibilityLint.Check(html, ImageBytes + attachment bytes)`; previews take
  `CorrespondenceDocument.BodyOf` and add their own charset; check `Journal.IsLoaded` before appending; `CanCloseAsync`
  while sending is not unit-tested yet.
- The rendered-preview switch of U1 (`UserMessages.RenderedPreview`) is not applied to the Korespondencja preview.
- A test reading an Outlook signature with relative images through `OutlookSignatureFiles` + the T1 parser is still
  missing.
- The hints `Correspondence.App.Recipients.DirectoryFieldsHint` and `Correspondence.App.Mailboxes.FieldsHint` show the
  example expressions with double braces (`{{givenName}}`); field expressions use the U1 syntax with single braces.
