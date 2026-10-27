# U1c / U1d – formatted messages pasted from Outlook (inline images) and title-aware salutations – design note

Roadmap items **U1c** (a ready message from Outlook pasted into a template: fonts, sizes, colours, links, tables, lists
and images come over) and **U1d** (salutations that know the recipient's title and gender as template fields), both
extensions of U1 (README, "Ścieżka U"; base design: [u1-user-messages.md](u1-user-messages.md), whose sections are cited
as "U1 §n"). This note is binding for the two implementation tracks of section 22; where it and U1 disagree, this note
wins for the parts it covers.

## 0. Decisions, binding rules, non-goals

**Decided with the user (not reopened):**

* **A1** – Pasting a formatted message ("HTML Format" / CF_HTML from Outlook; also Word and browsers) into the HTML body of
  a template, *sanitized but faithful*: Outlook's `<style>` class rules (`MsoNormal` …) inlined into `style` attributes,
  conditional comments, `o:`/`v:`/`w:` elements and `mso-*` properties removed, allow-listed tags, attributes and CSS
  properties kept, links kept (http/https/mailto; the allowed-link-domain rule of U1 §5 stays – pasted links to other
  domains are problems with an "allow this domain" action), placeholders such as `{givenName}` that Outlook split with
  inline tags repaired.
* **A2** – Images pasted with the text are **embedded**: read once at paste time into memory, validated (PNG/JPEG/GIF by
  magic bytes, size and count limits) and sent as inline images (`multipart/related`, `Content-ID`, `cid:` references) –
  never as downloadable attachments. Remote http(s) images are never fetched (removed with a warning).
* **A3** – A rendered preview of the HTML in a safe renderer (scripts and navigation disabled) – decision: WebView2 with
  a fallback to the source view (section 10).
* **A4** – The Watchdog route accepts inline images (contract change with a capability flag in
  `GET /api/v1/user-messages/status`); with an older service AD Manager offers direct SMTP or sending without images and
  never drops images silently; the service keeps images only in the outbox payload, deleted once sent; docs and the
  ADR-0008 note updated.
* **B1** – Salutations as template fields: the title from `personalTitle` (prof., dr hab., dr, mgr inż., ks. …), when empty
  recognized from the display name ("prof. dr hab. Jan Kowalski"); Pan/Pani from the given name by Polish rules with
  exception lists, overridable (an attribute chosen in the settings and/or a per-template default); fields for the title,
  the full salutation and the short Pan/Pani form; a title table in the settings (token → rank, PL masculine / feminine
  vocative, EN form) with defaults; unknown gender → a configurable neutral fallback plus a warning; the preview marks
  guessed values; field names in the style of the existing `{$…}` fields.

**CLAUDE.md rules as they apply:**

* **Rule 1.** Templates – text, HTML *and their images* – are operator settings (like saved views), not directory data:
  they may be stored in the settings folder (section 11). Rendered messages, the plan and every preview exist only in
  memory; the preview renderer must not persist them (section 10). No directory attribute is ever an image source
  (`thumbnailPhoto` / `jpegPhoto` are not fields; images are static template assets, identical for every recipient). The
  title table and the given-name list are vocabulary typed by the operator; nothing read from the directory is written to
  disk – the preview's per-message gender override is memory-only and there is deliberately no "remember this name"
  action that would copy a directory value into the settings. Diagnostic logs: counts and codes only (no HTML, no image
  bytes, no file paths of clipboard images – they contain the Windows user name).
* **Rule 2.** No credentials involved; unchanged.
* **Rule 3.** No new filters or DNs; U1d only adds attribute names to the existing batched reads (U1 §4).
* **Rule 6.** CF_HTML parsing, import, sanitizing, image reads, image-store IO and WebView2 start-up are asynchronous,
  off the UI thread where the API allows it, and cancellable.
* **DI scopes.** The clipboard reader is part of `IClipboardService` (Scoped). New per-connection services are added to
  `ConnectionLifetimes.cs`; application-wide ones (the image store – shared like `ISettingsStore`; the WebView2
  environment host) are singletons without per-connection state.
* **Watchdog (ADR-0008/0009 exception).** Images exist only inside the outbox payload of a user message and are deleted
  with it (section 9.5); never in the audit, the delivery-log metadata or logs.
* Warnings are errors; every user-visible string in `Strings.resx` **and** `Strings.pl.resx`; xUnit v3 `Assert`, fakes
  over mocks; Polish test data with diacritics.

**Non-goals:** WYSIWYG editing inside AD Manager (formatting is done in Outlook and pasted again; the HTML source stays
editable), attachments of any kind, fetching remote content, SVG / WebP / TIFF / BMP / EMF images in messages, RTF
parsing, per-recipient images, images in the plain-text part, automatic learning of genders, changes to the texts of the
built-in templates (they keep "Dzień dobry {givenName}," – operators opt in to salutations in their own templates).

---

# Part A – U1c: formatted HTML with inline images

## 1. Pieces

| Piece | Project | Purpose |
|---|---|---|
| `CfHtmlParser`, `CfHtmlDocument` | Core (`Messaging/Html/`) | CF_HTML header + offsets → full HTML, fragment range, `SourceURL` (section 2). |
| `HtmlPolicy` | Core | The allow-lists (tags, attributes, CSS properties and values, URL schemes) and limits; `HtmlPolicy.Version`. |
| `CssParser`, `CssInliner` | Core | Minimal CSS (declarations, rules, the selector subset of section 4.3); cascade into `style` attributes. |
| `MessageHtmlSanitizer` | Core | `Sanitize` (repairs, reports changes) and `Validate` (security findings only); fragment and document mode. **The one component AD Manager and the Watchdog validator share.** |
| `PastedHtmlImporter` | Core | Office/browser pre-processing (comments, VML, `o:p`, Word lists, `mso-hide`, highlight, Safe Links, images → `cid:`), then `Sanitize`, then placeholder repair and the text suggestion. |
| `PlaceholderRepair`, `WordListConverter`, `SafeLinks`, `HtmlToText`, `LinkDomainSuggestions` | Core | Import steps and helpers (sections 3, 5, 6). |
| `MessageImage`, `ImageFormats`, `ImageMetadataStripper`, `MessageImageLimits`, `ClipboardImagePolicy` | Core (`Messaging/Images/`) | The inline image model, magic bytes and dimensions, EXIF/text-chunk stripping, limits, the file-URI rule (section 7). |
| `UserMessageMimeBody` | AdMng.Mail | The one MIME body builder used by `UserMimeMessageBuilder` **and** the Watchdog's `MimeMessageFactory.CreateUserMessage` (section 8). |
| Contract v2 (`UserMessageImageDto`, capabilities, limits) | Watchdog.Contracts | Section 9. |
| `UserMessageRequestValidator`, `UserMessageService`, storage migration 0007 | Watchdog.Service | Section 9. |
| `MessageImageStore` | Presentation (`UserMessages/`) | Content-addressed image files of saved templates in the settings folder (section 11). |
| `HtmlPasteWorkflow`, composer partial `UserMessageComposerViewModel.RichHtml.cs`, item partial `UserMessageItems.RichHtml.cs`, `TransportFit` | Presentation | Paste → import → apply; images of the working template in memory; the transport check of section 9.6. |
| `IClipboardService.ReadFormatted()` + `ClipboardHtmlReader` | Presentation contract / App | Raw CF_HTML bytes, PNG / DIB bitmaps, Unicode text (section 2). |
| `HtmlPreviewView` + `WebView2Host` | App | The rendered preview and its fallback (section 10). |

## 2. Clipboard formats and CF_HTML

**What is read** (App, `ClipboardHtmlReader`, on the UI thread – the clipboard is STA – then everything else off it):

| Format | Source | Use |
|---|---|---|
| `HTML Format` (CF_HTML) | Outlook (classic, Word engine), Word, Edge/Chrome/Firefox, new Outlook | The content. Read as **raw bytes** through Win32 (`OpenClipboard` with 5 retries × 50 ms, `GetClipboardData(RegisterClipboardFormat("HTML Format"))`, `GlobalLock`/`GlobalSize`), never through WPF's string conversion (offsets are UTF-8 byte offsets and older code paths decode with the ANSI code page – Polish letters would break). If the raw read fails, the WPF string is passed instead and parsed in *text mode* (offsets ignored, markers used). Larger than 8 MiB → refused with a message. |
| `PNG` | Snipping Tool, browsers, Office when an image alone is copied | A pasted image without HTML (inserted as `<p><img></p>` at the caret of the HTML source), or the bitmap of a single remote `<img>` (below). |
| `CF_DIBV5` / `CF_DIB` | screenshots, image editors | Converted to PNG in memory in the App (`BitmapSource` → `PngBitmapEncoder`), then as `PNG`. |
| `UnicodeText` | everything | No HTML: offered as text (into the text body, or wrapped by `UserMessageRules.TextToHtml`). |
| `Rich Text Format` | Office | Ignored (no RTF parser). |

Presentation contract (`AdMng.Presentation.Services`): `ClipboardContent? IClipboardService.ReadFormatted()` returning
`ClipboardContent(byte[]? CfHtml, string? CfHtmlText, byte[]? Png, string? Text)`. `ScopelessClipboard` and the test
fakes return null / their canned content.

**CF_HTML parsing** (`CfHtmlParser.Parse(ReadOnlySpan<byte>)`, Core, pure):

1. Header lines `Key:Value` (CR LF, LF or CR) until the first `<` or the `StartHTML` offset. Keys (case-insensitive):
   `Version` (required: `0.9` or `1.0`), `StartHTML`, `EndHTML` (both may be `-1` = no context, version 1.0),
   `StartFragment`, `EndFragment` (required), `StartSelection`/`EndSelection` (ignored), `SourceURL` (optional, kept only
   for resolving relative links – never fetched, never stored).
2. Offsets are decimal byte offsets from the start of the buffer and must satisfy
   `0 ≤ StartHTML ≤ StartFragment ≤ EndFragment ≤ EndHTML ≤ length` (a trailing NUL and the rounding of `GlobalSize` are
   cut at `EndHTML`). Invalid or inconsistent offsets → problem `ClipboardOffsets` (info) and the markers are used.
3. Decoding: strict UTF-8 of `[StartHTML, EndHTML)`; invalid sequences → U+FFFD and problem `ClipboardEncoding`
   (warning – the source wrote a non-UTF-8 clipboard). A leading BOM is skipped.
4. The fragment: the `<!--StartFragment-->` / `<!--EndFragment-->` comments when present (Outlook, Word, Chromium); else
   sentinel comments `<!--admng-fragment-start-->` / `-end-->` inserted at the fragment offsets when both lie on a tag
   boundary; else the whole `<body>`. The importer extracts the fragment as a DOM range (`CloneContents`) of the parsed
   *full* document, so `<head>` styles stay available to the inliner.
5. Text mode (`CfHtmlParser.FromText(string)`): the header is skipped, step 4 without offsets.

Result: `CfHtmlDocument(string Html, Uri? SourceUrl, IReadOnlyList<HtmlImportProblem> Problems)` with the fragment
markers guaranteed to be present in `Html`.

**A real Outlook clipboard (abridged, offsets illustrative; the shape of the test fixtures, section 21):**

```text
Version:0.9
StartHTML:0000000105
EndHTML:0000002893
StartFragment:0000002107
EndFragment:0000002853
<html xmlns:v="urn:schemas-microsoft-com:vml" xmlns:o="urn:schemas-microsoft-com:office:office" xmlns:w="urn:schemas-microsoft-com:office:word" xmlns="http://www.w3.org/TR/REC-html40">
<head><meta http-equiv=Content-Type content="text/html; charset=utf-8"><meta name=Generator content="Microsoft Word 15 (filtered medium)">
<!--[if !mso]><style>v\:* {behavior:url(#default#VML);} o\:* {behavior:url(#default#VML);}</style><![endif]-->
<style><!--
@font-face {font-family:Aptos;}
p.MsoNormal, li.MsoNormal, div.MsoNormal {margin:0cm; font-size:12.0pt; font-family:"Aptos",sans-serif; mso-ligatures:standardcontextual;}
a:link, span.MsoHyperlink {mso-style-priority:99; color:#467886; text-decoration:underline;}
p.MsoListParagraph {mso-style-priority:34; margin-top:0cm; margin-right:0cm; margin-bottom:0cm; margin-left:36.0pt; font-size:12.0pt; font-family:"Aptos",sans-serif;}
@page WordSection1 {size:612.0pt 792.0pt; margin:70.85pt 70.85pt 70.85pt 70.85pt;}
div.WordSection1 {page:WordSection1;}
@list l0:level1 {mso-level-number-format:bullet; mso-level-text:\F0B7; font-family:Symbol;}
--></style></head>
<body lang=PL link="#467886" vlink="#96607D" style='word-wrap:break-word'>
<!--StartFragment--><p class=MsoNormal>Szanowni Państwo,<o:p></o:p></p>
<p class=MsoNormal>hasło do konta <b><span style='color:#C00000'>{</span></b><b><span class=SpellE><span style='color:#C00000'>sAMAccountName</span></span></b><b><span style='color:#C00000'>}</span></b> wygaśnie <span lang=EN-US>{$passwordExpires:d MMMM yyyy}</span>.<o:p></o:p></p>
<p class=MsoListParagraph style='text-indent:-18.0pt;mso-list:l0 level1 lfo1'><![if !supportLists]><span style='font-family:Symbol'><span style='mso-list:Ignore'>·<span style='font:7.0pt "Times New Roman"'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp; </span></span></span><![endif]>Zmień hasło na stronie <a href="https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fhaslo.contoso.pl%2F&amp;data=05%7C02%7C&amp;reserved=0">haslo.contoso.pl</a>.<o:p></o:p></p>
<p class=MsoNormal><img width=120 height=40 style='width:1.25in;height:.4166in' id="Obraz_x0020_1" src="file:///C:/Users/JKOWAL~1/AppData/Local/Temp/msohtmlclip1/01/clip_image001.png" alt="Logo – Dział IT"><o:p></o:p></p>
<!--EndFragment--></body></html>
```

Expected import result (line breaks added here; the serializer writes a newline after block end tags only):

```html
<p style="margin:0cm;font-size:12pt;font-family:'Aptos',sans-serif">Szanowni Państwo,</p>
<p style="margin:0cm;font-size:12pt;font-family:'Aptos',sans-serif">hasło do konta <b><span style="color:#C00000">{sAMAccountName}</span></b> wygaśnie {$passwordExpires:d MMMM yyyy}.</p>
<ul style="margin-top:0;margin-bottom:0"><li style="font-size:12pt;font-family:'Aptos',sans-serif">Zmień hasło na stronie <a href="https://haslo.contoso.pl/" style="color:#467886;text-decoration:underline">haslo.contoso.pl</a>.</li></ul>
<p style="margin:0cm;font-size:12pt;font-family:'Aptos',sans-serif"><img src="cid:img-3f2a9c0d1e4b5a67@admng" alt="Logo – Dział IT" width="120" height="40" style="width:1.25in;height:0.4166in"></p>
```

## 3. Import pipeline (`PastedHtmlImporter.Import`)

Input: a `CfHtmlDocument`, an `IPastedImageSource` (reads `file:` URIs under the rule of section 7.1; `data:` URIs are
decoded in Core), the optional clipboard bitmap, the allowed link domains and the template engine (for placeholder
repair). Output: `HtmlImportResult(string Html, IReadOnlyList<MessageImage> Images, IReadOnlyList<HtmlImportProblem>
Problems, string SuggestedText)`. Steps, in this order:

1. **Parse** the full HTML with the HTML5 parser (AngleSharp, section 4.1); collect every `<style>` element that is not
   inside a comment (Outlook's `<!--[if gte mso 10]><style>…` blocks are comments and are ignored) and parse its rules.
2. **Fragment** = DOM range between the markers (section 2 step 4), cloned with its partial ancestors. Inherited
   declarations of `body` (its `style`, plus `body {…}` / `div.WordSection1 {…}` rules) are applied to a wrapper `div`
   only when they set an allow-listed inherited property (`font-*`, `color`, `line-height`, `text-align`).
3. **Comments.** Downlevel-revealed markers `<![if …]>` / `<![endif]>` (parsed as bogus comments) are dropped and their
   content kept – this keeps the `<![if !vml]><img …><![endif]>` fallback images. `[if !supportLists]` regions are used
   by step 5, then removed. Every other comment, including `<!--[if gte mso 9]><xml>…</xml><![endif]-->`, is removed.
4. **Office elements.** By prefix of the element name: `o:p` → unwrapped (an empty `<p><o:p>&nbsp;</o:p></p>` stays an
   empty paragraph `<p>&nbsp;</p>` – it is an intentional blank line); `v:*`, `m:*`, `xml` → removed with content (each has
   an `<img>` fallback); every other prefixed element (`st1:*`, `w:*` smart tags, content controls) → unwrapped.
   `mso-highlight:<colour>` → `background-color:<colour>`; `background:<colour>` shorthand with only a colour →
   `background-color`. (Hidden elements – `mso-hide:all`, `display:none` – are removed by the sanitizer, section 4.3.)
5. **Word lists** (`WordListConverter`): consecutive sibling paragraphs carrying `mso-list:lN levelM lfoK` become real
   lists. The marker (the content of the `[if !supportLists]` region, or a leading `<span style="mso-list:Ignore">`) is
   removed and classifies the list: `·` `•` `-` `–` `o` `§` `Ø` `ü` (Symbol/Wingdings glyphs) → `ul` (`disc`, `circle` for
   `o`, `square` for `§`); `1.` `1)` → `ol` decimal (with `start`); `a.` `a)` → `lower-alpha`; `A.` → `upper-alpha`;
   `i.` `ii.` → `lower-roman`; `I.` → `upper-roman`. Levels nest (`level2` inside the last `li` of `level1`). The `li`
   keeps the paragraph's allowed text properties; `margin-left`, `text-indent` and `mso-list` are dropped; the list gets
   `margin-top:0;margin-bottom:0` when the paragraphs had no vertical margins. Real `<ul>/<ol>` from Outlook's own editor
   (`<li class=MsoListParagraph …>`) only lose the Office properties.
6. **Sanitize** (`MessageHtmlSanitizer.Sanitize`, fragment mode, section 4) – inliner, allow-lists, URLs.
7. **Links** (section 6) and **images** (section 7) – resolved during the sanitizer's URL pass through the import hooks.
8. **Cleanup**: spans without attributes, `font` without attributes and anchors without `href` (bookmarks
   `_Hlk…`, `_MailOriginal`, `mso-bookmark` spans) are unwrapped; adjacent sibling inline elements with the same name and
   identical attributes are merged; empty inline elements are removed.
9. **Placeholder repair** (section 5).
10. **Text suggestion**: `HtmlToText.Convert(html)` – blocks → lines, `<br>` → line break, `li` → "- " / "1. ", table
    cells → tab-separated, `<a>` → "text (url)" unless the text is the URL, `<img>` → `[alt]` (nothing without alt), tokens
    stay tokens.

The importer never throws on content: everything it cannot keep becomes an `HtmlImportProblem(Kind, Severity, Detail)`
(kinds: `RemovedElement`, `RemovedAttribute`, `RemovedStyle`, `RemoteImageRemoved`, `ImageRejected`, `ImageUnreadable`,
`LinkRemoved`, `LinkOutsideAllowedDomains`, `LinkUnwrapped`, `LinkTextMismatch`, `PlaceholderRepaired`,
`ClipboardOffsets`, `ClipboardEncoding`, `ContentTruncated`); the `Detail` is a tag / property / host / field name, never
text of the message. Problems are localized as `UserMessages.Rich.Problem.{Kind}`; repeated ones are counted.

## 4. The sanitizer – a Core component shared with the Watchdog

### 4.1 Dependency

**AngleSharp** (MIT, fully managed, no native code, runs on Linux for the Watchdog) – the HTML5 (WHATWG) parsing
algorithm, so the tree the sanitizer sees is the tree a browser (WebView2, new Outlook, OWA) builds; that removes the
mutation-XSS class of bugs a regex or a non-conforming parser (HtmlAgilityPack) leaves open. Added to `AdMng.Core.csproj`
(Core stays free of LDAP libraries; this is a pure parser) with the exact current stable 1.x version in
`Directory.Packages.props` (new `ItemGroup Label="Messages (U1c)"`). **Not** used: `HtmlSanitizer` (Ganss.Xss) – generic
policy, no Office pre-processing, and it drags in `AngleSharp.Css` (pre-1.0); `AngleSharp.Css` itself – CSS is handled by
the small `CssParser` below, which only needs declarations, simple rules and a selector subset, and whose output we fully
control. The existing regex check `UserMessageRules.FindForbiddenHtml` stays as a cheap first gate (and for older clients).

### 4.2 API

```csharp
public enum HtmlSanitizeMode { Fragment, Document }

public sealed record HtmlSanitizeOptions
{
    public HtmlSanitizeMode Mode { get; init; }
    /// <summary>Content ids of images that may be referenced as cid: (others → finding / removed).</summary>
    public IReadOnlySet<string> InlineImageIds { get; init; } = new HashSet<string>();
    /// <summary>Template mode: href/src may start with a field ({…}); checked again after rendering.</summary>
    public bool AllowFieldUrls { get; init; }
    public IHtmlImportHooks? Import { get; init; }     // only the importer: images, Safe Links, relative URLs
}

public static class MessageHtmlSanitizer
{
    public static HtmlSanitizeResult Sanitize(string html, HtmlSanitizeOptions options);   // Html, Changes, ImageIds, Links
    public static IReadOnlyList<HtmlFinding> Validate(string html, HtmlSanitizeOptions options); // security findings only
}
```

`Changes` are `HtmlChange(Kind, IsSecurity, Detail)`; `Validate` returns the security subset of what `Sanitize` would
change (active content, event handlers, disallowed URL schemes, remote resources, `cid:` without a matching image,
fields in an image source, CSS that hides or overlays content). Guarantees, enforced by tests: **idempotent**
(`Sanitize(Sanitize(x).Html).Html == Sanitize(x).Html`), **stable** (parse → serialize → parse yields the same tree),
`FindForbiddenHtml(Sanitize(x).Html)` is empty, and text and template tokens are never altered except by the documented
transformations.

**Where it runs:** at paste (fragment, with import hooks); in `CheckTemplate` on each language's HTML (fragment,
`AllowFieldUrls`: findings → `HtmlNotAllowed` errors as today; cosmetic changes are applied when the template is saved
and listed as a note); in the planner on every rendered message document (document mode, `Validate` → message error
`HtmlNotAllowed` – defense in depth, cannot happen for a sanitized template because field values are HTML-encoded); in
the Watchdog validator (document mode: security findings → 400 `forbiddenHtml`; then `Sanitize` and the sanitized
document is what the service sends – section 9.3).

### 4.3 Policy (`HtmlPolicy`, version 1)

**Elements kept:** `a abbr b bdi bdo big blockquote br caption center cite code col colgroup dd del dfn div dl dt em
font h1 h2 h3 h4 h5 h6 hr i img ins kbd li mark ol p pre q s samp small span strike strong sub sup table tbody td tfoot th
thead tr tt u ul wbr`; document mode additionally `html`, `head` (emptied) and `body`.
**Removed with content:** `script style`(after inlining) `iframe frame frameset object embed applet form input button
select textarea option link meta base title svg math template noscript audio video source track canvas picture`.
**Unwrapped** (content kept): every other element (`section`, `article`, `figure`, `label`, unknown names, …).

**Attributes** (anything else, including `class`, `id`, `name`, `target`, `data-*`, `on*`, is dropped):

| Element | Allowed |
|---|---|
| all kept | `style` (filtered), `title`, `lang`, `dir` |
| `p div h1–h6 blockquote caption` | `align` |
| `a` | `href` |
| `img` | `src` (`cid:` only), `alt`, `width`, `height`, `border`, `align`, `hspace`, `vspace` |
| `table` | `border cellpadding cellspacing width align bgcolor` |
| `td th` | `colspan rowspan width height align valign bgcolor nowrap`, `th`: `scope` |
| `tr tbody thead tfoot` | `align valign bgcolor` |
| `col colgroup` | `span width` |
| `ol ul li` | `ol`: `start type reversed`; `ul`: `type`; `li`: `value type` |
| `font` | `face size color` |
| `html` (document) | `lang` |

Numeric attributes: digits (and `%` for widths), ≤ 5 digits; colours: `#rgb`, `#rrggbb` or a CSS colour name.

**CSS** (style attributes after inlining; a declaration whose property or value is not allowed is dropped):

| Allowed properties | Value rules |
|---|---|
| `color background-color` | colour: `#hex`, `rgb()/rgba()` with numbers, CSS names; `windowtext` → `#000000`, `window` → `#FFFFFF`; `auto` dropped |
| `font font-family font-size font-style font-weight font-variant line-height letter-spacing word-spacing text-transform text-indent text-align vertical-align white-space direction` | keywords, lengths (`px pt em rem % cm mm in`, ≥ 0 except `text-indent` and `letter-spacing`), family names as identifiers or strings (re-serialized with single quotes) |
| `text-decoration text-decoration-line text-decoration-style text-decoration-color` | keywords, colour |
| `margin padding` and their `-top/-right/-bottom/-left` | lengths ≥ 0 (**no negative margins** – they can pull content over the fixed footer) |
| `border` and every `border-*` longhand, `border-collapse border-spacing border-radius` | lengths ≥ 0, styles, colours |
| `width height min-width max-width min-height` | lengths ≥ 0, `auto` |
| `list-style-type list-style-position table-layout caption-side empty-cells word-wrap overflow-wrap word-break` | keywords |

Dropped always: `mso-*` and every vendor-prefixed property, `position`, `z-index`, `display`, `visibility`, `opacity`, `transform`, `filter`, `clip`, `clip-path`, `overflow*`, `max-height`, `float`,
`content`, `behavior`, `-moz-binding`, `cursor`, `pointer-events`, `animation*`, `transition*`, `page*`, `background`
(after the colour conversion of section 3) and `background-image`. Any value containing `url(`, `expression(`, `\`, `/*`,
`@`, `<`, `javascript:` or a control character is dropped; `var()`, `calc()`, `attr()` dropped; `!important` stripped.

**Hidden content:** an element whose cascaded declarations (inline style and rules, evaluated *before* the filtering
below) contain `display:none` or `mso-hide:all` is removed with its content – hidden text is neither shown in the preview
nor sent.

**Inliner** (`CssInliner`): rules from `<style>` elements (and in fragment mode the import step 2 rules; the CDO/CDC
tokens `<!--` / `-->` that Outlook wraps its style sheet in are ignored at the top level, as in CSS); supported
selectors: `type`, `.class`, `type.class`, `#id`, selector lists, and `a:link` (as `a`); rules containing any other
selector (combinators, attribute selectors, other pseudo-classes, `@media`, `@font-face`, `@page`, `@list`) are skipped.
Cascade per element: matching rules by specificity, then source order; the element's own `style` wins over normal rule
declarations, an `!important` rule declaration wins over a normal inline one. Then: allow-list filtering, shorthand
`font:` kept only when fully allowed, and **inherited dedupe** – an inherited property (`color`, `font-*`, `line-height`,
`letter-spacing`, `text-align`, `text-transform`, `white-space`, `direction`) whose value equals the value the nearest
ancestor sets is not written. Declarations are serialized in a fixed order (`property:value;` without spaces, no trailing
`;`) so the output is deterministic.

**URLs:** `a@href` – `http`, `https`, `mailto`, `tel` (as `IsLinkAllowed`); `#anchor`, relative URLs without
`SourceURL`, `ftp`, `file`, `javascript`, `vbscript`, `data`, `cid`, `blob`, `ms-*`, `onenote:` → the anchor is
unwrapped (text kept, change `LinkRemoved`). `img@src` – `cid:` of a known image only (import hooks first turn `file:`
and `data:` into `cid:`); anything else → the `img` is removed (`RemoteImageRemoved` for http(s), else `ImageRejected`).
`AllowFieldUrls`: a value starting with `{` is kept in `href` (validated after rendering) and is a finding in `src`.

**Serialization:** AngleSharp's compact formatter with a newline after block-level end tags and `<br>`; attribute values
double-quoted; text escaped minimally (`&amp; &lt; &gt; &nbsp;`), so Polish letters stay literal UTF-8.

**Limits:** the HTML of one language ≤ `MessageHtmlLimits.MaxHtmlLength` = **400 000** characters after sanitizing
(Outlook HTML is verbose after inlining; U1b's 100 000 is too small) – longer → template error `HtmlTooLong`; the
Watchdog reports its own value (section 9.1).

## 5. Placeholder repair

Outlook/Word split tokens when the spell checker (`span.SpellE`, `span.GramE`), a language change (`span lang=EN-US`),
bookmarks or formatting touched part of them. After step 8, within one block element (`p div li td th h1–h6`), never
across `<br>` or a block boundary:

1. Concatenate the text of the block's inline descendants in document order with a map back to the text nodes.
2. Find candidate tokens `{…}` in the concatenation: `{` + a valid token per `UserMessageFields.Engine.Parse` (an
   attribute name or `$`-token with its argument and functions, no line breaks) + `}`. Inside a candidate, U+00A0 becomes
   a space and soft hyphens / zero-width characters are removed (Word inserts them).
3. A candidate spread over several text nodes is moved into the text node holding the longest piece (its formatting
   wins); the other pieces are removed from their nodes; elements left empty are removed. One problem
   `PlaceholderRepaired` per repaired token (detail: the token).
4. `href` values: `%7B…%7D` sequences that decode to a valid token are decoded back (`https://portal.contoso.pl/%7BsAMAccountName%7D`
   → `…/{sAMAccountName}`); Outlook's auto-link of a typed URL with a field produces exactly this.
5. Text that only looks like braces (`{ to jest nawias }`, invalid token) is left alone; the template check reports real
   syntax errors as before.

## 6. Links

* **Safe Links**: an `href` on a host ending in `.safelinks.protection.outlook.com` with a `url` query parameter is
  replaced by the decoded `url` value when that is itself an allowed-scheme URL (problem `LinkUnwrapped`, info). Other
  rewriting services are not unwrapped (they show up as their own domain).
* **Relative URLs** are resolved against `SourceURL` when it is http(s); otherwise the link is removed.
* **Allowed domains**: the U1 rule stays (settings domains + AD DNS domain + sender's domain, subdomains included; the
  planner still checks every rendered message, U1 §4). At paste and on every template check, links outside it are
  listed as `LinkOutsideAllowedDomains` (warning at paste, error in the plan as before) with two actions in the window:
  **"Zezwól na domenę …"** – adds a domain to `UserMessages.AllowedLinkDomains` through `ISettingsStore.UpdateAsync`
  (the operator picks the host or one of its parent domains; `LinkDomainSuggestions` never offers a single label and, for
  two-letter country TLDs with a second level of ≤ 3 letters – `com.pl`, `gov.pl`, `edu.pl`, `co.uk` – nothing shorter
  than three labels) – and **"Usuń łącze"** (unwraps the anchor in the HTML of the edited language).
* **Text/target mismatch**: an anchor whose visible text is itself a URL or host (`haslo.contoso.pl`) with a different
  host than its `href` → `LinkTextMismatch` (warning at paste; template-level warning issue in the plan) – the classic
  phishing shape, worth a look even in our own templates.
* `UserMessageRules.FindLinks` / `IsLinkAllowed` treat `cid:` image sources as non-links (today they would become
  `LinkNotAllowed`).

## 7. Inline images

### 7.1 Sources at paste time

| Source | Rule |
|---|---|
| `file:///…/msohtmlclip1/NN/clip_imageNNN.png\|jpg\|gif` (Outlook, Word) | `ClipboardImagePolicy`: a `file:` URI with a **local drive path** (no UNC `\\server\share`, no `\\?\` / `\\.\` device paths, no `file://host/…`) whose final path (8.3 names expanded, links resolved, reparse points rejected) lies under the user's temp folder. Read once, asynchronously, at most `MaxImageBytes + 1` bytes. Anything else is **not opened** (a UNC path would leak NTLM credentials to a foreign server) → `ImageUnreadable`. Only the basename (`clip_image001.png`) is kept as the MIME file name; the path (it contains the Windows user name) is never stored or logged. |
| `data:image/png\|jpeg\|gif;base64,…` (browsers) | Decoded in Core; base64 longer than the limit allows is rejected before decoding; any other media type (notably `image/svg+xml`) → `ImageRejected`. |
| `http(s)://…`, `blob:`, `cid:` (other apps), relative | **Never fetched** → removed with `RemoteImageRemoved` / `ImageUnreadable`. Exception: when the HTML has exactly one such `<img>` and the clipboard also carries a bitmap (`PNG`/DIB – "Copy image" in a browser), the bitmap is used for it. |
| `PNG` / DIB without HTML | The image alone (section 2). |

`IPastedImageSource` lives in Presentation (`PastedImageFileSource`, file IO with the Core policy), so the policy is unit
tested on Linux with a temp-folder fake; the App only supplies the clipboard bytes.

### 7.2 Validation and normalization (`ImageFormats`, `ImageMetadataStripper`)

* Type by **magic bytes only** (the extension and a declared media type must agree or the image is rejected):
  PNG `89 50 4E 47 0D 0A 1A 0A`, JPEG `FF D8 FF`, GIF `GIF87a` / `GIF89a`.
* Dimensions read from the headers without decoding (PNG `IHDR`, GIF logical screen, JPEG first `SOFn`): each side
  1–4096 px and ≤ 16 777 216 px in total (decompression bombs in the recipients' clients).
* Structure walked to its end (PNG chunks with CRC, JPEG segments to `EOI`, GIF blocks to the trailer); a truncated or
  malformed file is rejected.
* **Metadata stripped** by lossless surgery, never by re-encoding: PNG `tEXt zTXt iTXt eXIf tIME` chunks; JPEG `APP1`
  (Exif/XMP), `APP13` (IPTC) and `COM` segments (`APP0` JFIF, `APP2` ICC and `APP14` Adobe stay); GIF comment
  extensions. Photos pasted from a phone lose GPS position and author (RODO).

### 7.3 Model and limits

```csharp
public sealed record MessageImage
{
    public required string ContentId { get; init; }   // "img-" + first 16 hex digits of SHA-256 + "@admng"
    public required string MediaType { get; init; }   // image/png | image/jpeg | image/gif
    public required byte[] Data { get; init; }        // validated and stripped
    public required string Sha256 { get; init; }      // lower-case hex of Data
    public string FileName { get; init; } = "";       // [A-Za-z0-9._-]{1,64}, else "image{n}.{ext}"
    public int Width { get; init; }
    public int Height { get; init; }
    public override string ToString() => $"MessageImage({ContentId}, {MediaType}, {Data.Length} B)";
}
```

The `Content-ID` is derived from the content, so the same image always has the same id, a template references it as
`<img src="cid:img-3f2a9c0d1e4b5a67@admng">`, and the Watchdog can verify the id against the bytes.
`MessageImage.Create(bytes, fileName)` is the only factory (validates, strips, hashes).

| Limit (`MessageImageLimits`, Core; the Watchdog reports its values, section 9.1) | Value |
|---|---|
| Images per message | 10 |
| Bytes per image (after stripping) | 1 MiB |
| Bytes of all images of one message | 2 MiB |
| Side / pixels | 4096 px / 16.7 Mpx |
| Images per template (both languages) | 20 |
| Bytes of all images of one template | 4 MiB |

### 7.4 Templates and the planner

* `MessageTemplate.Images: IReadOnlyList<MessageTemplateImage>` – metadata only (`ContentId`, `MediaType`, `Sha256`,
  `Length`, `Width`, `Height`, `FileName`); both language texts reference a subset by `cid:`. The bytes come from the
  image store (section 11) or, for an unsaved working template, from memory.
* `CheckTemplate`: every `cid:` in either language's HTML has a template image (else `ImageMissing`, error); a field in
  an image `src` → `HtmlNotAllowed`; limits → `ImagesTooLarge`; images referenced by neither language are dropped when
  the template is saved.
* `UserMessagePlanRequest.Images: IReadOnlyDictionary<string, MessageImage>` (by content id, loaded by the composer
  before planning, off the UI thread) and `OmitImages: bool`.
* Per message, after rendering the HTML document: `Validate` (document mode, the referenced ids) → `HtmlNotAllowed`;
  the referenced images in order of first reference → `OutgoingUserMessage.InlineImages` (an image missing from the
  dictionary → `ImageMissing` error; per-message limits → `ImagesTooLarge` error); HTML length → `HtmlTooLong`.
* `OmitImages` ("send without images", section 9.6): every `<img src="cid:…">` is replaced by
  `<span>[alt]</span>` (nothing without alt) before validation, `InlineImages` stays empty and every message gets the
  info issue `ImagesOmitted` – the preview shows exactly the HTML that will be sent.
* Text body empty while an HTML body exists → the text part is `HtmlToText.Convert(rendered HTML)`.
* New issue codes live in `HtmlIssueCodes` (Core, A's file): `ImageMissing`, `ImagesTooLarge`, `ImagesOmitted` (info),
  `HtmlTooLong`, `LinkTextMismatch` (warning), `HtmlNormalized` (info, template level). `MessageIssueCodes` is not
  edited; texts `UserMessages.Issue.{code}`.

## 8. MIME (both senders)

`OutgoingUserMessage` gains `IReadOnlyList<MessageImage> InlineImages` (default empty) with the doc comment: *images
referenced by `cid:` from `HtmlBody`, sent as inline parts of `multipart/related`; there is still no attachment field.*
`UserMessageMimeBody.Create(string text, string? html, IReadOnlyList<MessageImage> images)` (AdMng.Mail, MimeKit
`BodyBuilder` with `LinkedResources`, never `Attachments`) builds the body for **both** `UserMimeMessageBuilder.Build`
(direct SMTP) and `MimeMessageFactory.CreateUserMessage` (Watchdog – `AdMng.Watchdog.Service.csproj` gains a project
reference to AdMng.Mail; no cycle: Mail → Core, Contracts). Without images the structure is unchanged
(`multipart/alternative` of U1 §5). With images:

```text
multipart/alternative
├── text/plain; charset=utf-8                         (transfer encoding chosen by MimeKit)
└── multipart/related; type="text/html"
    ├── text/html; charset=utf-8                      (<img src="cid:img-…@admng">)
    ├── image/png; name="clip_image001.png"
    │     Content-ID: <img-3f2a9c0d1e4b5a67@admng>
    │     Content-Disposition: inline; filename="clip_image001.png"
    │     Content-Transfer-Encoding: base64
    └── image/jpeg; …                                 (in order of first reference)
```

Rules: only images referenced by the HTML are added (an unreferenced related part is shown as an attachment by some
clients); every reference has its part; headers as in U1 §5 (`Auto-Submitted`, `X-Auto-Response-Suppress`,
`X-AdMng-Message: user-notification`, `Message-ID`); the image parts carry no other headers (no `Content-Location`, no
dates). A server that announces `SIZE` smaller than the message, or answers 552 / 5.3.4, is classified as a permanent
rejection (`smtp.552.5.3.4`) with the new hint `DeliveryHints.MessageTooLarge = "smtp.messageTooLarge"` – in both
classifiers (AdMng.Mail `SmtpFailureClassifier`, the service's `SmtpErrorClassifier`), text
`Watchdog.Hint.smtp.messageTooLarge` in both `Strings` resx files.

## 9. AdMng Watchdog: contract v2, capability, storage

### 9.1 Contract (`Api/UserMessageDtos.cs`)

```csharp
public sealed record UserMessageImageDto
{
    public required string ContentId { get; init; }   // "img-<16 hex>@admng", must match SHA-256 of Data
    public required string MediaType { get; init; }   // image/png | image/jpeg | image/gif
    public required byte[] Data { get; init; }        // base64 in JSON
    public string? FileName { get; init; }
    public override string ToString() => $"UserMessageImageDto({ContentId})";
}
// UserMessageRequest gains:
public IReadOnlyList<UserMessageImageDto> InlineImages { get; init; } = [];

// UserMessagingStatusDto gains (all optional for older clients; an older service simply does not send them):
public IReadOnlyList<string> Capabilities { get; init; } = [];          // UserMessagingCapabilities.InlineImages = "inlineImages"
public int MaxInlineImages { get; init; }                              // 0 = no images
public long MaxInlineImageBytes { get; init; }
public long MaxInlineImagesTotalBytes { get; init; }
public int MaxHtmlLength { get; init; } = 100_000;                     // the value of U1b when absent
public long StoredImageBytes { get; init; }                            // section 9.5
public long MaxStoredImageBytes { get; init; }
```

The request stays `[JsonUnmappedMemberHandling(Disallow)]`: `attachments`, `bcc` … are still 400 `invalidJson`, and an
**older service rejects `inlineImages` the same way** – which is why AD Manager never sends the field without the
capability. `UserMessageLimits.MaxHtmlLength` becomes 400 000 (equal to `MessageHtmlLimits.MaxHtmlLength`; a test in
Watchdog.Core.Tests asserts the equality) and gains `MaxInlineImages`, `MaxInlineImageBytes`,
`MaxInlineImagesTotalBytes` (the Core values). New issue codes: `invalidImage`, `missingImage` (a `cid:` without an
image), `unreferencedImage` (an image the HTML does not reference), `imageIdMismatch`, `tooLarge`,
`imagesDisabled`; 429 `imageStorageFull` (section 9.5).

The capability `inlineImages` is reported only when the service supports images **and** `userMessages.allowInlineImages`
is on (new setting, default **on** – an administrator may forbid images; config audit rating Medium). Clients without
images are unaffected; an older AD Manager keeps working against the new service.

### 9.2 Request size

`ApiLimits.MaxUserMessageBodyBytes` = **6 MiB** for `POST /api/v1/user-messages` only (2 MiB of images ≈ 2.7 MiB base64,
400 000 HTML characters JSON-escaped, the text body, headroom); every other route keeps 1 MB. The endpoint raises
`IHttpMaxRequestBodySizeFeature.MaxRequestBodySize` before reading (Kestrel; HTTP.sys while the Windows hosting code
exists) and `ApiResults.ReadJsonAsync` takes the limit as a parameter (default unchanged). The client's 45 s timeout
stays (a 6 MiB upload on a LAN is well below it).

### 9.3 Validation (`UserMessageRequestValidator`, never trusting the client)

In addition to U1 §10: images present → `allowInlineImages` on (else `imagesDisabled`) and an HTML body present;
count and sizes ≤ the limits; every image: `MessageImage.Create` succeeds (magic bytes = `MediaType`, dimensions,
structure – `invalidImage`) and `ContentId` equals the id derived from the SHA-256 of the received bytes
(`imageIdMismatch`); `MessageHtmlSanitizer.Validate(html, Document, ids)` empty – a `cid:` without an image is reported as
`missingImage`, every other finding as `forbiddenHtml`; every image is referenced (`unreferencedImage`). The service then
sends
`MessageHtmlSanitizer.Sanitize(html, Document, ids).Html` and the stripped image bytes – cosmetic policy differences
between AD Manager and service versions therefore never fail a run, security differences always do.

### 9.4 Outbox payload

`RenderedEmail` (Watchdog.Core `Model/`, frozen model – amended like U1b's `UserMessage`) gains
`IReadOnlyList<RenderedInlineImage> InlineImages` (`ContentId`, `MediaType`, `FileName`, `Data`) – populated only for
`DeliveryKind.UserMessage`. Images live **only in the outbox payload** of that message (Deflate(JSON), base64 inside):
not in `user_messages` (the audit row's request hash covers them), not in the delivery-log metadata, not in logs
(log line 2610 gains the image count only). `SmtpEmailTransport.SendUserMessageAsync` passes them to
`UserMessageMimeBody`. Sent → payload NULL (images gone); Dead → payload kept for `OutboxDeadDays` like every message.

### 9.5 Bounded storage

Payload copies are not deduplicated (user decision: only in the payload), so the stored image volume is capped:
migration **0007** adds `outbox.image_bytes INTEGER NOT NULL DEFAULT 0`, written on insert (sum of image lengths) and
set to 0 whenever the payload is set NULL or purged. `userMessages.maxStoredImageMb` (default 256, range 16–4096,
validated) bounds `SUM(image_bytes)` over user messages; a submission with images that would exceed it → 429
`imageStorageFull` with `Retry-After: 3600` (messages without images are unaffected). The status DTO reports both
values so AD Manager can warn before a run. `InMemoryWatchdogStore` and the store contract tests mirror this.

### 9.6 AD Manager with an older (or image-less) service

`UserMessageTransportStatus` gains `UserMessageTransportCapabilities Capabilities` (`InlineImages`, `MaxInlineImages`,
`MaxInlineImageBytes`, `MaxInlineImagesTotalBytes`, `MaxHtmlLength`); direct SMTP reports the Core limits,
`WatchdogUserMessageTransport` maps the status DTO (missing fields → no images, HTML 100 000). Before sending (and
before the test message) `TransportFit.Check(plan, capabilities)` compares the sendable messages with the chosen
transport. When something does not fit, the preview shows a blocking bar – **never a silent switch or drop**:

| Reason | Offered (only what is possible) |
|---|---|
| Images, service without `inlineImages` (older version or switched off) | **Wyślij bezpośrednio przez SMTP** (when direct SMTP is configured – for this run only, the setting is unchanged) · **Wyślij bez obrazów** (re-plan with `OmitImages`; preview updated) · Anuluj |
| Images over the service's limits / `imageStorageFull` | the same choices |
| HTML longer than the service's `MaxHtmlLength` | **Wyślij bezpośrednio przez SMTP** · Anuluj |

The choice is remembered for the run (report header and the session-log line say "bez obrazów" / "bezpośrednio przez
SMTP"). If the service still answers 400 `invalidJson` on `inlineImages` or `imagesDisabled` (downgraded between status
and send), the transport returns `TransportFailed` code `watchdog.imagesNotSupported` – the run stops and the rest is
"not sent" (never retried without images automatically). `imageStorageFull` → `TransportFailed` `watchdog.imageStorage`.
`FakeWatchdogClient` gains the capability switch and the limits.

### 9.7 Documentation

`docs/WATCHDOG.md` 5.8 (images: what the service checks, limits, `allowInlineImages`, `maxStoredImageMb`, the 6 MiB
route limit) and 7 (outbox row: "treść wiadomości, także obrazy osadzone, usuwana zaraz po wysłaniu"); ADR-0008 gains
"Uzupełnienie: obrazy w treści wiadomości do użytkowników (U1c)" (images only in the payload, `image_bytes` and the cap,
never in audit or logs); ADR-0003 gains a short "Uzupełnienie" that message templates – text and their images – are
operator settings (U1a/U1c), not directory data. CLAUDE.md is not edited by the tracks: rule 1 already covers the
outbox content of user messages; naming templates in its allow-list is a one-line decision for the maintainers.

## 10. Preview renderer

**Decision: WebView2** (Microsoft Edge Chromium, NuGet `Microsoft.Web.WebView2`, exact version in
`Directory.Packages.props`, referenced by AdMng.App only; Evergreen runtime, no fixed runtime shipped) **with a fallback
to the HTML source view**. Justification: the rendering engine of new Outlook and OWA, so tables, fonts and spacing
look as most recipients will see them; scripts can be switched off and every request intercepted; maintained and
security-patched with Edge. Rejected: WPF `WebBrowser` (MSHTML/IE – unsupported engine, scripting on by default), an
HTML → `FlowDocument` converter (a second renderer with its own fidelity gaps and maintenance), CefSharp (≈ 150 MB
redistributable, own patch cycle). The WebView2 runtime is part of Windows 11 and current Windows 10, but not guaranteed
on Windows Server 2016–2022 jump hosts – hence the fallback.

**Hardening (binding):**

* One `CoreWebView2Environment` per process (`WebView2Host`, singleton, created lazily and asynchronously on first
  use) with the user data folder `%TEMP%\AdMng-WebView2-{pid}-{random}`; deleted when the application exits (after the
  browser process has ended) and stale `AdMng-WebView2-*` folders are deleted at the next start. Controllers are created
  with `IsInPrivateModeEnabled = true` (no profile, cache or history written). `AdditionalBrowserArguments`:
  `--disable-background-networking --disable-component-update --disable-sync --no-pings --disable-breakpad
  --disable-crash-reporter`. Residual risk, documented: a renderer crash dump could hold the previewed message in that
  temp folder until the next start deletes it.
* `CoreWebView2Settings`: `IsScriptEnabled=false`, `IsWebMessageEnabled=false`, `AreHostObjectsAllowed=false`,
  `AreDevToolsEnabled=false`, `AreDefaultContextMenusEnabled=false`, `AreDefaultScriptDialogsEnabled=false`,
  `IsStatusBarEnabled=false`, `AreBrowserAcceleratorKeysEnabled=false`, `IsPasswordAutosaveEnabled=false`,
  `IsGeneralAutofillEnabled=false`, `IsBuiltInErrorPageEnabled=false`, `IsSwipeNavigationEnabled=false`; zoom allowed.
* **Content from memory only:** the control navigates to `https://preview.admng.invalid/message`; a
  `WebResourceRequested` filter on `*` answers that URL with the preview document and
  `https://preview.admng.invalid/cid/{contentId}` with the image bytes (from memory), and **every other request with
  403** – remote images, fonts and tracking pixels never load, matching what is sent. The preview copy (never the sent
  HTML) has its `cid:` references rewritten to those URLs and a CSP
  `default-src 'none'; img-src https://preview.admng.invalid; style-src 'unsafe-inline'` (also sent as a response header).
  `NavigateToString` is not used (2 MB limit, `about:blank` origin).
* `NavigationStarting` cancels everything except the preview URL (clicked links do nothing; the problems list shows the
  link targets); `NewWindowRequested`, `DownloadStarting`, `PermissionRequested` → denied/handled.
* **Fallback:** `CoreWebView2Environment.GetAvailableBrowserVersionString` fails, initialization fails, or the new
  setting `UserMessages.RenderedPreview` is off (default on; for environments that forbid an embedded browser) → the view
  shows the HTML source (read-only, monospace) with a one-line note and a link to the Microsoft WebView2 download page
  (opened in the default browser only when clicked). The UI tests force the fallback with
  `ADMNG_UI_NO_WEBVIEW2=1`.
* The package must keep `dotnet build AdMng.slnx` working on Linux (`EnableWindowsTargeting`); verified by track A's first
  App commit.

**Where:** compose stage – "Podgląd formatowania" of the edited language's template HTML (fields shown as written,
images from the working template); preview stage – the selected message, tabs **Widok** (rendered) · **Tekst** ·
**Źródło HTML**.

## 11. Templates with images: storage, size, export/import

* **Metadata** in `settings.json` (`MessageTemplate.Images`, section 7.4) – small, so the copy-on-write settings store
  (which re-serializes everything on each update) stays fast.
* **Bytes** in a content-addressed folder next to the settings: `<SettingsDirectory>\message-images\{sha256}.{png|jpg|gif}`
  (`MessageImageStore`, Presentation, singleton). Writes are atomic (temp file + move), skipped when the file exists and
  its hash matches; reads verify the SHA-256 (mismatch or missing → `ImageMissing` for that template, with "remove
  image" in the editor). It is operator content (logos, instruction screenshots), never directory data (section 0).
* **Lifetime:** saving a template writes its images first, then the settings; deleting/saving/importing runs a
  garbage collection afterwards (files referenced by no template in `ISettingsStore.Current` **and** older than ten
  minutes are deleted – another window may be between writing its images and its settings); the
  composer loads a template's image bytes into memory when the template is selected, so another window deleting the
  template never breaks an open composer. Limits: 4 MiB per template (section 7.3); the library warns above 64 MiB in the
  folder.
* **Export/import** (`UserMessageTemplateLibrary`): format version **2** – `{ "format": "AdMng.MessageTemplates",
  "version": 2, "templates": [...], "images": [{ "sha256", "mediaType", "fileName", "data" (base64) }] }` (images once,
  shared by templates). Export only writes images referenced by the exported templates; a file above 20 MiB is refused.
  Import accepts versions 1 and 2; every image goes through `MessageImage.Create` (magic, limits, stripping, hash =
  declared `sha256`); a template whose image fails keeps its text, loses that `<img>` and the import summary says so.
  Imported templates still get new ids.

## 12. UI (track A)

* **Compose stage** – the "HTML" expander becomes a section with a toolbar: **Wklej sformatowaną treść** (replaces the
  HTML of the edited language; asks first when it is not empty), **Wklej obraz** (bitmap at the caret), **Podgląd
  formatowania / Źródło** (toggle), **Wygeneruj tekst z HTML** (fills the text body; asks when it is not empty – an empty
  text body is filled automatically after a paste). Below: the image strip (thumbnail, name, size, dimensions,
  **Usuń** – removes the image and its `<img>` elements) and the paste result list (problem, count, actions **Zezwól na
  domenę …**, **Usuń łącze**). Paste runs as a cancellable background step with a status line.
* **Preview stage** – the details `TabControl` gets **Widok** first; the transport bar of section 9.6 sits above the
  send button.
* Keyboard: Ctrl+Shift+V = paste formatted; the buttons have automation names for the UI tests.

## 13. Security checklist (A)

UNC/file reads restricted (7.1) · no network fetches anywhere (import, preview, send) · SVG and every non-raster
format excluded · magic bytes, structure and dimension checks · EXIF/XMP/IPTC stripped · HTML5 parser + allow-lists +
idempotence/stability tests (mXSS) · CSS hiding/overlay properties and negative margins dropped (the fixed footer stays
visible and last) · links: scheme allow-list, Safe Links unwrapped, domain allow-list unchanged, text/target mismatch
warned · fields never inside `img src` · the Watchdog re-validates everything and rejects security findings ·
idempotency hash covers images · images only in the outbox payload, bounded by `maxStoredImageMb` · no HTML, image
bytes or clipboard paths in any log.

---

# Part B – U1d: title-aware salutations

## 14. Fields

All are extension tokens of `UserMessageFields` (camelCase, `$` prefix like `{$passwordExpiresInDays}`), evaluated in the
**message language** (U1 §2) for the account; the `manager` twins evaluate the same for the account's manager (useful
in manager mode, U1 §3).

| Field | Argument | Value (PL / EN) |
|---|---|---|
| `{$title}` | none \| `highest` | The recognized title chain in canonical spelling: `prof. dr hab.`, `dr n. med.`, `mgr inż.`; with a suffix title `dr hab., prof. UW`; `highest` → only the highest-ranked title (`prof.`). Language-independent. Empty when none. |
| `{$titledName}` | none | `prof. dr hab. Jan Kowalski`; with a suffix title `dr hab. Anna Nowak, prof. UW`; without a title `Jan Kowalski`. |
| `{$salutation}` | none \| `noTitle` \| `vocative` \| `vocativeNoTitle` | Full salutation: `Szanowny Panie Profesorze`, `Szanowna Pani Doktor`, `Szanowny Panie`, `Szanowna Pani`, `Szanowny Księże` / `Dear Professor Kowalski`, `Dear Dr Nowak`, `Dear Mr Wiśniewski`, `Dear Ms Kowalska`. `noTitle` ignores the title; `vocative` drops the adjective / "Dear" (`Panie Profesorze`, `Professor Kowalski`). |
| `{$honorific}` | none \| case: `nom gen dat acc ins loc voc` (Polish aliases `mianownik dopełniacz celownik biernik narzędnik miejscownik wołacz`, diacritics-insensitive) | The short form, capitalized as customary in letters: `Pan`/`Pani`, `Pana`/`Pani`, `Panu`/`Pani`, `Pana`/`Panią`, `Panem`/`Panią`, `Panu`/`Pani`, `Panie`/`Pani`; EN `Mr`/`Ms` (case ignored). |
| `{$byGender:masc/fem[/neutral]}` | required | Free text chosen by gender: `{$byGender:zmienił/zmieniła}`, `{$byGender:his/her/their}`. `/` separates (the parts cannot contain `/`; the engine's backslash escape is not seen by extensions); two parts → neutral is `masc/fem` joined. |
| `{$managerTitle}`, `{$managerTitledName}`, `{$managerSalutation}`, `{$managerHonorific}`, `{$managerByGender:…}` | as above | The same for the manager (empty without a manager). |

Engine changes (Core `Templates/`, track B): `TemplateExtensionToken` gains `AllowedArguments` (case- and
diacritics-insensitive list; another argument is a template syntax error with the allowed values in the message) and
`ReportsEmpty` (default true; **false** for `$title` and `$managerTitle` – an empty title is normal and must not
produce the U1 "empty field" warning for most recipients). New function
`capitalize` (upper-cases the first letter only: `{$title|capitalize}` → `Prof. dr hab.` at the start of a line).
`UserMessageFields.Examples` gains `{$salutation}`, `{$salutation:noTitle}`, `{$title}`, `{$titledName}`,
`{$honorific}`, `{$honorific:dopełniacz}`, `{$byGender:zmienił/zmieniła}`, `{$managerSalutation}`.

The salutation fields render as plain text (HTML-encoded in the HTML body, `HeaderSafe` in the subject) – no markup.

## 15. Title detection (`TitleParser`, `TitleTable`)

**Sources** – the first one that yields a title wins (an attribute holding only an honorific word yields none, so the
display name is still parsed); the source is recorded for the preview:

1. The **title attribute** of the settings (default `personalTitle`; empty = none). Never `title` – that is the job
   title. Honorific words found there (`Pan Pani Mr Mr. Mrs Mrs. Ms Ms. Miss`) are not titles: they set the gender with
   source *attribute* (section 16) and are removed from the title. As built, a fully covered part after the last comma
   is a suffix as in the display name (`dr hab., prof. UW` → `{$titledName}` "dr hab. Ewa Zielińska, prof. UW").
2. The **display name** (setting *Rozpoznawaj tytuł z nazwy wyświetlanej*, default on): a title prefix
   (`prof. dr hab. Jan Kowalski`, `dr Kowalski Jan`) and/or a suffix after the last comma (`…, prof. UW`,
   `…, dr hab.`). Accepted only when the rest still contains the `givenName` or the `sn` (case- and
   diacritics-insensitive) – so a display name that merely starts with "Dr" is not cut. Source *display name* (info
   issue `TitleFromDisplayName`).

**Tokenization:** split on white space and commas; a word with inner dots is split after each dot
(`dr.hab.inż.` → `dr.` `hab.` `inż.`); comparison is case-insensitive, diacritics-insensitive and ignores a trailing dot
(`Dr.` = `dr`, `inz` = `inż.`).

**Coverage** (what `{$title}` shows): from the start of the source (and of the suffix), the longest table spelling
(canonical or variant, itself tokenized) matching the next words is taken, repeatedly; output uses the canonical
spelling. Two absorbed patterns: the qualifier `n.` + up to two following words of which the last ends in a dot
(`dr n. med.`, `dr hab. n. o zdr.`) and, after a row with `prof.` in its title (`prof.`, `ks. prof.`, as built also
`prof. uczelni`, `prof. nadzw.`), a university abbreviation (2–8 letters, starts upper-case, at
least two upper-case letters: `UW`, `UJ`, `AGH`, `PWr`, `SGGW`; as built never a degree such as `MBA`, `PhD`, `DSc`,
`LLM`, `MSc` – those stay unrecognized words); as built also one lower-case abbreviation of a discipline (at least two
letters and a dot) directly after a row of the table (`dr med.`, `dr hab. med.`, `dr inż. arch.`, `mgr farm.`,
`lic. piel.`) – all kept as written in `{$title}`, without effect on the salutation. Words of the title attribute that are not covered → warning `TitleUnrecognized` (detail: the words);
`{$title}` then shows the covered part only.

**Selection** (what the salutation uses): among the table rows whose spelling occurs as a contiguous word sequence in
the covered words, the highest rank wins; if that row has no form for the determined gender (or language), the next
rank is tried, finally the plain form. Ties: the earlier row of the table.

**Title table** (settings; `null` = the built-in defaults below, so later improvements reach operators who never edited
it; *Przywróć domyślne* resets). Columns: canonical title, variants, rank (0–1000), PL masculine vocative, PL feminine
vocative, EN form (empty = no title-specific form). Defaults:

| Title | Variants | Rank | PL masc. vocative | PL fem. vocative | EN |
|---|---|---|---|---|---|
| `ks. prof.` | – | 120 | Księże Profesorze | – | Father |
| `ks. dr hab.` | – | 118 | Księże Profesorze | – | Father |
| `ks. dr` | – | 115 | Księże Doktorze | – | Father |
| `prof.` | `prof`, `profesor`, `professor`, `prof. zw.` | 100 | Panie Profesorze | Pani Profesor | Professor |
| `prof. uczelni` | `prof. ucz.` | 101 | Panie Profesorze | Pani Profesor | Professor |
| `prof. nadzw.` | `prof. nzw.`, `prof. ndzw.` | 101 | Panie Profesorze | Pani Profesor | Professor |
| `dr hab.` | `dr. hab.`, `doktor habilitowany` | 90 | Panie Profesorze | Pani Profesor | Dr |
| `ks.` | `ks`, `ksiądz` | 80 | Księże | – | Father |
| `dr` | `dr.`, `doktor`, `doctor` | 70 | Panie Doktorze | Pani Doktor | Dr |
| `lek.` | `lek. med.`, `lekarz` | 60 | Panie Doktorze | Pani Doktor | Dr |
| `lek. dent.` | `lek. stom.` | 61 | Panie Doktorze | Pani Doktor | Dr |
| `lek. wet.` | – | 61 | Panie Doktorze | Pani Doktor | Dr |
| `mgr` | `mgr.`, `magister` | 20 | – | – | – |
| `inż.` | `inż`, `inz.`, `inz`, `inżynier` | 20 | – | – | – |
| `lic.` | `lic`, `licencjat` | 10 | – | – | – |

Notes on the defaults: variants are other spellings of the **same** title (`{$title}` prints the canonical one); a
different title with the same forms has its own row – *profesor uczelni* / *profesor nadzwyczajny* are positions
(usually of a *dr hab.*), not the title of professor, and a *lekarz dentysta* / *weterynarii* is no physician – ranked
just above the row its spelling contains, so `{$title:highest}` keeps it (as built). A *doktor habilitowany* is
customarily addressed "Panie Profesorze / Pani Profesor" in Polish correspondence (changeable); *magister / inżynier / licencjat* are shown in `{$title}` and `{$titledName}` but give
the plain "Szanowny Panie / Szanowna Pani" (titular "Panie Inżynierze" is archaic); military and other titles are added
by the operator (e.g. `płk` – Panie Pułkowniku / Pani Pułkownik / Colonel). Limits: ≤ 100 rows, spellings and forms
≤ 100 characters, header-safe.

The default table and the honorific/neutral forms are **language data addressed by the message language**, not UI
strings: they live in Core (`Messaging/Salutations/SalutationData.cs`) with both languages in every row; UI labels
(settings, preview) are in `Strings*.resx`.

## 16. Gender (`GenderGuesser`)

`Gender { Unknown, Masculine, Feminine }` with a source and a "guessed" flag. Order:

1. **Override** for this run (preview, section 19) – certain.
2. **Gender attribute** of the settings (e.g. `extensionAttribute5`; empty = none; must pass
   `UserMessageRules.IsForbiddenAttribute` = false and be a valid attribute name), values compared case- and
   diacritics-insensitively against two lists in the settings – defaults masculine `M`, `male`, `mężczyzna`, `Pan`,
   `Mr`; feminine `K`, `F`, `W`, `female`, `kobieta`, `Pani`, `Ms`, `Mrs`. Or an honorific word in the title attribute
   (section 15). Certain. Another value → ignored with info `GenderValueUnknown`.
3. **Operator's given-name list** (settings, typed by the operator: name → gender) – certain for that name.
4. **First given name** (`givenName` up to the first space or hyphen – "Jan Maria" → "Jan", "Anna-Maria" → "Anna";
   initials such as "J." → no signal), normalized lower-case without diacritics:
   * in the built-in **unisex** list → no name signal;
   * in the built-in **masculine-ending-in-a** list → masculine (guessed, *exception*);
   * in the built-in **feminine-not-ending-in-a** list → feminine (guessed, *exception*);
   * ends in `a` → feminine (guessed, *rule*), otherwise masculine (guessed, *rule*) – **Polish messages only** (as
     built): recipients of English messages mostly have names the Polish rule does not fit (Jennifer, Karen, Ilya), so
     there a name outside the lists gives no signal of its own (the surname may decide; else the neutral "Dear {name}"
     and `GenderUnknown`) – but the rule result still vetoes a disagreeing surname (step 5), so "Laura Nowakowski" or
     "Paweł Gąska" is a conflict, never "Dear Mr Nowakowski" / "Dear Ms Gąska".
5. **Surname** (`sn`, first part of a hyphenated one): `-ska -cka -dzka` → feminine, `-ski -cki -dzki` → masculine.
   With no name signal it decides (guessed, *surname*); against a *rule* result (not an exception; in English messages
   too) → **conflict** → unknown (warning `GenderConflict`); agreeing → keeps the name result (English: the surname
   result).
6. **Title for one gender only** (as built): no signal from the names (no conflict) and the highest-ranked title with a
   Polish form has a form for one gender only (`ks.`, `ks. dr` – priests) → that gender (guessed, source of the title,
   detail: the title).
7. Nothing → unknown (warning `GenderUnknown`).

**Built-in lists** (Core, `GivenNameLists`, minimum content – extend, never shrink):

* masculine ending in -a: barnaba, bonawentura, dyzma, jarema, kosma, kuba, zawisza, juda, jona, sawa, nikita, mykyta,
  mykola (mykoła), danila, gawrila, kuzma, foma, ilja, ilia, illia, misza, grisza, kola, wania, kostia, petia, fedia,
  tolia, wolodia, wasia, dima, luca, joshua, ezra, elia, mustafa, hamza, osama, isa, musa;
* feminine not ending in -a: beatrycze, noemi, miriam, ingrid, nel, abigail, rut, ruth, karmen, carmen, inez, ines,
  mercedes, nikol, nicole, dagmar, astrid, sigrid, agnes, rachel, sarah, hannah, deborah, esther, judith, lilian, vivien,
  michelle, emily, amy, lily, lili, ann, anne, mary, marie, sophie, chloe, zoe, julie, claire, jeanne, louise, elizabeth,
  catherine, kathleen, margaret, irene, doris, alice, charlotte, isabel, heike, elke, silke, ute, gudrun, karin, birgit,
  ellen, helen, kathrin, jasmin, lubow, liubov; as built also common English, German, French and Spanish names (jennifer,
  karen, susan, megan, sabine, yvonne, christine, kerstin, iris …) and, masculine ending in -a, the English
  transliterations of Russian names (ilya, misha, grisha, kostya, vanya, pasha …), mattia, luka;
* unisex / ambiguous: sasza, andrea, nicola, nikola, kim, alex, robin, jean, dominique, toni, kai, noa, maxime, akira
  (as built also sasha, zhenya).

The **per-template default** (`MessageTemplate.Salutation.UnknownGender`: *z ustawień* (default) / neutral / masculine
/ feminine) and the settings default (`SalutationSettings.UnknownGender`, default neutral) decide what an unknown gender
renders as; a template may also switch titles off (`UseTitles`, default true) and override the neutral texts.

## 17. Composition and fallbacks (`SalutationResolver`)

* **PL full** = adjective + vocative: masculine `Szanowny` + (title row's masc. vocative, else `Panie`); feminine
  `Szanowna` + (fem. vocative, else `Pani`).
* **EN full** = `Dear` + (title row's EN form, else `Mr` / `Ms` by gender) + ` ` + `sn`. With an EN title form the
  gender is **not needed** ("Dear Professor Kowalski", "Dear Dr Nowak", "Dear Father Kowalski"). `sn` empty → neutral
  (warning `SurnameMissing`).
* **Unknown gender** (and the template/settings default is neutral): the **neutral text** of the language – defaults PL
  `Szanowna Pani / Szanowny Panie`, EN `Dear {name}` (`{name}` = `givenName sn`, else the display name without its
  title; with no name at all the fixed last resort `Szanowni Państwo` / `Dear Sir or Madam`) – plus the warning
  `GenderUnknown`. Vocative variants: PL `Pani / Panie`, EN `{name}`. As built: without given name and surname a title
  prefix or suffix is cut from the display name for `{name}` ("dr Jan Kowalski" → "Dear Jan Kowalski"; never used as
  the person's title), and a finding names the account by its login or RDN when it has no name at all. Honorific: the joined forms (`Pan/Pani`,
  `Pana/Pani`, `Panu/Pani`, `Pana/Panią`, `Panem/Panią`, `Panu/Pani`, `Panie/Pani`; EN `Mr/Ms`); `byGender`: the third
  part, else `masc/fem`.
* **Unknown gender with a title** (as built): with the default Polish neutral text and a title row having both Polish
  forms, the neutral text keeps the title – `Szanowna Pani Profesor / Szanowny Panie Profesorze` (vocative `Pani
  Profesor / Panie Profesorze`); an operator's own neutral text is used as it is.
* A warning is raised only when the template actually uses a field that needed the missing information (an EN titled
  salutation never warns about gender; a template without salutation fields never warns).

## 18. Settings and template options

`UserMessageSettings.Salutations: SalutationSettings` (Presentation `Settings/`, Core types for the rows):
`TitleAttribute` (`personalTitle`), `TitleFromDisplayName` (true), `GenderAttribute` (null), `MasculineValues`,
`FeminineValues`, `GivenNames` (`List<GivenNameGender>`, ≤ 500), `Titles` (`List<TitleRule>?`, null = defaults),
`NeutralPolish`, `NeutralEnglish`, `UnknownGender` (`Neutral`). `Normalize()` trims, deduplicates, clamps, drops
invalid attribute names and forbidden attributes (`IsForbiddenAttribute`). `MessageTemplate.Salutation:
TemplateSalutationOptions?` (`UseTitles`, `UnknownGender?`, `NeutralPolish?`, `NeutralEnglish?`), normalized on load and
import. The planner receives a Core `SalutationOptions` built from both (`UserMessageSender.Salutations`, filled by
`UserMessageSenders.From`), and `AccountAttributesFor` / `ManagerAttributesFor` add the title and gender attributes when
the template uses a salutation-family token.

Settings window, tab *Wiadomości e-mail*, new section **Zwroty grzecznościowe**: title attribute, *rozpoznawaj tytuł z
nazwy wyświetlanej*, gender attribute with its two value lists, neutral texts PL/EN, *gdy płeć nieznana*, the given-name
list (grid: imię, płeć), the title table (grid with *Przywróć domyślne*), and a **Sprawdź** box (type a title, display
name, given name and surname → the title, gender with its source and the PL/EN salutations; pure, no directory
access). Composer: a small **Zwroty** group under the recipient mode (*używaj tytułów*, *gdy płeć nieznana*).

## 19. Preview marking

* `PlannedUserMessage.Derived: IReadOnlyList<DerivedFieldValue>` – `(Field, Value, Source, IsGuess, Detail)` for
  `title`, `gender`, `salutation` (and the manager twins when used); sources `Attribute`, `TitleAttribute`,
  `DisplayName`, `OperatorList`, `NameException`, `NameRule`, `Surname`, `Override`, `Fallback`.
* Issues (`SalutationIssueCodes`, Core, B's file; texts `UserMessages.Issue.{code}`): `GenderGuessed` (info, detail:
  source + name), `TitleFromDisplayName` (info), `GenderUnknown`, `GenderConflict`, `GenderDefaulted` (an unknown
  gender rendered with the default gender of the settings / the template), `TitleUnrecognized`, `SurnameMissing`
  (warnings), `GenderValueUnknown` (info).
* Preview list: new column **Zwrot** – the rendered salutation with `≈` (guessed, tooltip with the source) or `⚠`
  (neutral fallback / conflict). Details pane: **Pola wyliczone** – title (source), gender (source, "zgadnięte"),
  salutation – and a **Płeć** selector *automatycznie / Pan / Pani / neutralnie* that re-plans only that message
  (`UserMessagePlanner.Replan(request with GenderOverrides, index)`; the composer keeps the accounts of the last read in
  memory while the window shows them; the override never reaches the settings). New filter **Zgadnięte zwroty**.
* The rendered body is not marked – the preview shows exactly what is sent.

## 20. Grammar examples

Template (PL):

```text
{$salutation},
uprzejmie informujemy, że hasło do {$honorific:dopełniacz} konta {sAMAccountName} wygaśnie {$passwordExpires}
(za {$passwordExpiresInDays} dni). Prosimy, aby {$honorific} {$byGender:zmienił/zmieniła} je przed tym terminem.
```

| Account (personalTitle / displayName / givenName sn) | PL result | `{$salutation}` EN |
|---|---|---|
| `prof. dr hab.` / – / Jan Kowalski | **Szanowny Panie Profesorze,** … do **Pana** konta … aby **Pan zmienił** … | Dear Professor Kowalski |
| – / `dr Anna Nowak` / Anna Nowak | **Szanowna Pani Doktor,** … do **Pani** konta … aby **Pani zmieniła** … (title ≈ from the display name) | Dear Dr Nowak |
| `mgr inż.` / – / Piotr Wiśniewski | **Szanowny Panie,** … **Pana** … **Pan zmienił** … | Dear Mr Wiśniewski |
| – / `dr hab. Ewa Zielińska, prof. UW` / Ewa Zielińska | **Szanowna Pani Profesor,** … | Dear Professor Zielińska |
| `ks. dr` / – / Tomasz Wójcik | **Szanowny Księże Doktorze,** … (`{$honorific}` stays *Pan/Pani* – it is not title-aware; templates for clergy word the rest themselves) | Dear Father Wójcik |
| – / – / Kuba Mazur | **Szanowny Panie,** … (≈ exception list) | Dear Mr Mazur |
| – / – / Sasza Kamińska | **Szanowna Pani,** … (≈ unisex name, surname -ska) | Dear Ms Kamińska |
| – / – / Sasza Kim | **Szanowna Pani / Szanowny Panie,** … do **Pana/Pani** konta … aby **Pan/Pani zmienił/zmieniła** … ⚠ | Dear Sasza Kim ⚠ |
| `Pani` (honorific) / – / Nikola Lewandowski | **Szanowna Pani,** … (attribute wins over the surname) | Dear Ms Lewandowski |

Other cases: `{$honorific:celownik}` → *Panu / Pani* ("przypominamy Panu"), `{$honorific:biernik}` → *Pana / Panią*
("prosimy Panią"), `{$honorific:narzędnik}` → *Panem / Panią* ("kontakt z Panem"); `{$titledName}` → *prof. dr hab. Jan
Kowalski*; `{$title|capitalize}` → *Prof. dr hab.*

### 20a. As built (track B)

Details settled while implementing (code: `src/AdMng.Core/Messaging/Salutations/`, Presentation
`Settings/SalutationSettings.cs`, `UserMessages/*Salutation*`):

* `TemplateExtensionToken` also gained `ValidateArgument` (an English error or null): `{$byGender}` needs two or three
  parts. `{$byGender}` does not report an empty value either (`{$byGender:/a}` – an empty part is the operator's choice).
* Words of the display name equal to the given name or the surname are never a title or an absorbed university
  (`prof. NOWAK Jan`), and a suffix after the last comma counts only when every word of it is covered. A title of the
  display name made of short all-caps words without a dot (`Jan Kowalski, KS` – a unit code such as *księgowość*, `DR`,
  `LEK`) is no title unless the whole display name is written in capitals (`JAN KOWALSKI, KS`); `DR HAB.` still is one.
* Findings: `GenderGuessed` carries the deciding word (first given name or surname) – the source is shown by the
  derived fields; an unknown gender rendered with the default gender of the settings / the template is the warning
  `GenderDefaulted` (the gender is still unknown – section 17), not `GenderUnknown`; a neutral override is the
  operator's choice (no finding, no ⚠).
* `PlannedUserMessage.Derived`: `title` and `gender` (with the `manager` prefix for the manager) for every person whose
  fields the template uses, `salutation` only when a `{$salutation}` field of that person is used (manager mode shows
  the manager's salutation in the column); `DerivedFieldValue.Issue` names the finding behind a value. The salutation
  value is the variant the template rendered (the fullest one when several: `Full`, `noTitle`, `vocative`,
  `vocativeNoTitle`), marked by the worst of the variants rendered. The title and the gender carry a guess, a fallback
  or a finding only when a rendered field needed them (`{$titledName}` alone never marks the gender, `{$byGender}`
  alone never marks a display-name title). Without `{$salutation}` the *Zwrot* column shows only the mark (`≈` / `⚠`)
  of a gender that `{$honorific}` / `{$byGender}` used. The *Zgadnięte zwroty* filter lists messages with a guessed or
  fallback value or a value with a finding.
* A template with *używaj tytułów* off renders `{$title}` empty and `{$titledName}` without the title.
* The composer's *Zwroty* options read and write `MessageTemplate.Salutation` of the edited template and are refreshed
  when another template is selected (an `OnPropertyChanged` override in the partial class – no extra hook line in the
  shared file); a gender change in the preview is posted to the dispatcher and replaces that item only.
* *Pola wyliczone* has one gender selector per person whose fields the template uses: *Płeć* (the account,
  `UserMessagePlanRequest.GenderOverrides`) and *Płeć przełożonego* (the manager fields such as
  `{$managerSalutation}`, `ManagerGenderOverrides`) – both keyed by the account DN of the message, so a choice re-plans
  that message only.
* Settings: an emptied title attribute is stored as `""` (null would bring the default back); the neutral texts are
  stored only when they differ from the defaults.
* `{title}` (the job title, attribute `title`) and `{$title}` (the academic title) differ by one character: the
  composer's *Wstaw pole* list carries `{title}` and labels both (*stanowisko* / *tytuł naukowy*). No template finding for the mix – the template bar is an error bar, and the mandatory preview of
  every message shows which one was used.
* The screenshot test class is `ScreenshotTestsSalutations`, so the CI step's filter
  `FullyQualifiedName~AdMng.App.UiTests.ScreenshotTests` takes it without a workflow change.

---

# Part C – tests and implementation tracks

## 21. Tests

Every new test class in a **new file** (no edits to existing test files except where noted), xUnit v3 `Assert`, fakes,
Polish data with diacritics.

**Core (track A)** – `tests/AdMng.Core.Tests/Messaging/Html/`, fixtures in `Fixtures/` (embedded resources):
* Fixtures transcribed from real captures (structure verbatim, text replaced by Polish sample text with ą ć ę ł ń ó ś ź
  ż): classic Outlook 365 (`MsoNormal`, `SpellE` splits, Word bullet and numbered lists two levels deep, a bordered
  `MsoTableGrid` table, highlighted text, Safe Links, two `msohtmlclip` images, VML fallback), Word 365, new Outlook
  (remote/`blob:` images), Edge/Chrome (data: URI image, relative links + `SourceURL`), Firefox; malicious samples
  (script, `onerror`, `javascript:` and entity-obfuscated schemes, `<svg>`, CSS `expression()`/`url()`, `position:fixed`
  overlay, negative margin over the footer, `display:none` text, mXSS shapes, UNC `file://` image). A helper
  `CfHtmlFixture.Build(html)` writes headers with correct byte offsets; raw-byte fixtures cover wrong offsets, `-1`,
  missing markers, invalid UTF-8, BOM, trailing NUL.
* `CfHtmlParserTests`, `CssParserTests`, `CssInlinerTests` (specificity, order, inline vs `!important`, unsupported
  selectors skipped, inherited dedupe, deterministic order), `MessageHtmlSanitizerTests` (every allow-list row, both
  modes, `Validate` = security subset, idempotence and stability over all fixtures, `FindForbiddenHtml` empty),
  `PastedHtmlImporterTests` (expected output of every fixture, problems list), `WordListConverterTests`,
  `PlaceholderRepairTests` (splits by `SpellE`, `lang`, bold; `%7B…%7D` in href; nbsp/soft hyphen inside; false
  candidates untouched), `SafeLinksTests`, `HtmlToTextTests`, `LinkDomainSuggestionsTests` (`com.pl`, `gov.pl`, `co.uk`),
  `ImageFormatsTests` (magic/extension mismatch, dimensions, truncation, bombs), `ImageMetadataStripperTests`
  (EXIF/XMP/IPTC/tEXt removed, pixels byte-identical), `ClipboardImagePolicyTests` (temp folder, 8.3 names, UNC, device
  paths, junctions), `MessageImageTests` (content id, ToString), `UserMessageImagesPlannerTests` (references, missing,
  limits, `OmitImages`, text from HTML, `HtmlTooLong`, `cid:` not a link, post-render `Validate`).

**Core (track B)** – `tests/AdMng.Core.Tests/Messaging/Salutations/`: `TitleParserTests` (personalTitle variants
`prof. dr hab.`, `Prof. Dr.`, `dr.hab.inż.`, `dr n. med.`, `lek. med.`, `ks. prof.`, `mgr inż.`, `Pan`, `Mr`, unknown
`MBA`; display names with prefix, suffix `, prof. UW`, surname-first, "Dr" not cut without name), `GenderGuesserTests`
(Anna, Jan, Kuba, Barnaba, Beatrycze, Noemi, Mykoła, Emily, "Jan Maria", "Anna-Maria", "ŁUCJA", Sasza + Kamińska, Sasza
Kim, Nikola + Lewandowski, conflicts, attribute values, operator list), `SalutationResolverTests` (table selection, combos
with `ks.`, missing gender forms, EN without gender, neutral texts, `{name}` last resort, per-template options),
`SalutationFieldsTests` (every field and argument in PL and EN, all seven cases, `byGender` with two and three parts, manager twins,
`capitalize`, `AllowedArguments` errors, no empty-field warning for `{$title}`), `UserMessageSalutationPlannerTests`
(attributes read, derived values, issues, `Replan`, manager mode).

**Mail (track A)** – `InlineImageMimeTests` (structure of section 8, ids, inline disposition, no attachments, order,
unchanged structure without images, 552 classification); `MailpitInlineImageTests` (with `ADMNG_TEST_SMTP_*`): a Polish
message with a PNG and a JPEG through direct SMTP – Mailpit's API shows two `Inline` parts with the `ContentID`s of the
HTML, `Attachments` empty, part bytes equal (SHA-256).

**Watchdog (track A)** – Core.Tests: contract round trip of `inlineImages` and the new status fields, an older-client
status without them, limit equality. Service.Tests: API (images accepted; each new issue code; `imagesDisabled`;
`attachments` still `invalidJson`; 6 MiB route vs 1 MB elsewhere; idempotency with images; `imageStorageFull` with
`Retry-After`), `MimeMessageFactory` = `UserMimeMessageBuilder` body structure for the same input, migration 0007, store
(`image_bytes` set/cleared on send, dead, purge; contract tests in `AdMng.Watchdog.Testing` for both stores), outbox job
with an SMTP sink receiving the related parts. IntegrationTests (lab: Samba + Mailpit): `UserMessageInlineImageLabTests`
– the same Polish message with images through the service; Mailpit check as above; payload NULL and `image_bytes` 0
after sending.

**Presentation** – A: `HtmlPasteWorkflowTests` (fake clipboard with the Core fixtures and a temp-folder image source;
replace/confirm; images pooled and deduplicated; problems and both actions – the domain lands in the settings),
`MessageImageStoreTests` (atomic write, hash check, GC, concurrent delete), `TemplateLibraryImagesTests` (export v2,
import v1/v2, bad image, size cap), `TransportFitTests` + `WatchdogUserMessageTransportImagesTests` (old status → bar
choices; SMTP for this run; without images re-plans; `invalidJson` race → `watchdog.imagesNotSupported`),
`RichHtmlLocalizationTests` (every new key and code in en + pl). B: `SalutationSettingsTests` (normalize, defaults,
reset), `SalutationSettingsViewModelTests` (grids, check box), `UserMessageSalutationComposerTests` (column, marks,
filter, override re-plans one message and leaves the settings untouched, manager mode), `SalutationLocalizationTests`.
B's directory data: a new `tests/AdMng.Testing/SalutationSampleData.cs` (`AddSalutationSamples()` – users in their own
OU, so `AddSampleCompany()` counts do not change).

**UI (Windows CI only)** – A: `UserMessagesRichHtmlWindowTests` (puts a CF_HTML fixture as raw bytes and a PNG under
`%TEMP%\msohtmlclip1\01\` on the clipboard, pastes, checks the image strip and the problems list, the preview tab renders
with WebView2 and – with `ADMNG_UI_NO_WEBVIEW2=1` – falls back to the source view, no binding errors), screenshot
`22-user-messages-rich-html.png` (new test file), `HostTests` lifetimes, `SmokeTest` opens the preview tab. B:
`UserMessagesSalutationWindowTests` (column, details panel, override, settings section) and screenshot
`25-user-messages-salutations.png` (new test file; 22 and 23 belong to track K – see 22.4).

## 22. Implementation tracks

Two independent tracks on branches cut from the commit of this note; either may merge first. Each keeps 0 warnings,
runs its test projects twice before merging, and commits in small steps.

### 22.1 Track A – U1c (rich HTML, inline images, Watchdog v2, preview)

Owns (new files unless marked *edit*):

* Core: `src/AdMng.Core/AdMng.Core.csproj` (*edit*: AngleSharp), `Messaging/Html/*`, `Messaging/Images/*`,
  `Messaging/UserMessageRules.cs` (*edit*: `cid:`), `Messaging/Transport.cs` (*edit*: capabilities).
* Mail: `UserMessageMimeBody.cs`, `UserMimeMessageBuilder.cs` (*edit*), `SmtpFailureClassifier.cs` (*edit*).
* Watchdog: `Contracts/Api/UserMessageDtos.cs`, `Contracts/Api/DeliveryTestResult.cs` (*edit*: `DeliveryHints`),
  `Contracts/Configuration/UserMessagingSettings.cs` (*edit*),
  Watchdog.Core config validation and config-audit rating (*edit*), `Core/Model/OutboxMessage.cs` (*edit*:
  `RenderedEmail.InlineImages`), `Service/AdMng.Watchdog.Service.csproj` (*edit*: reference AdMng.Mail),
  `Service/UserMessages/*`, `Service/Api/UserMessageEndpoints.cs`, `Service/Api/ApiResults.cs`,
  `Service/Hosting/ApiLimits.cs`, `Service/Notifications/{MimeMessageFactory,SmtpEmailTransport,SmtpErrorClassifier}.cs`,
  `Service/Storage/Migrations/Migration0007UserMessageImages.cs`, `Service/Storage/{SqliteUserMessageStore,OutboxRows,
  SqliteOutboxStore,WatchdogStorageJsonContext}.cs` (*edit*), `tests/AdMng.Watchdog.Testing/*` (*edit*: in-memory store,
  contract tests, `FakeWatchdogClient`).
* Presentation: `Services/PlatformServices.cs` (*edit*: `ReadFormatted`), `UserMessages/UserMessageTransports.cs`
  (*edit*), `UserMessages/UserMessageText.cs` (*edit*: `KnownCodes`), `UserMessages/MessageImageStore.cs`,
  `UserMessages/HtmlPasteWorkflow.cs`, `UserMessages/TransportFit.cs`, `UserMessages/UserMessageComposerViewModel.RichHtml.cs`, `UserMessages/UserMessageItems.RichHtml.cs`,
  `Localization/L.UserMessagesRich.cs`, the Presentation DI registration (*edit*: image store).
* App: `AdMng.App.csproj` (*edit*: WebView2), `Services/ClipboardService.cs` (*edit*), `Services/ClipboardHtmlReader.cs`,
  `Services/WebView2Host.cs`, `Controls/HtmlPreviewView.xaml(.cs)`, `AppHost.cs` (*edit*), `SmokeTest.cs` (*edit*),
  `Connections/ConnectionWindowManager.cs` (*edit*: `ScopelessClipboard`).
* `Directory.Packages.props` (*edit*: AngleSharp, Microsoft.Web.WebView2).
* Docs: `docs/WATCHDOG.md`, `docs/adr/0008-…`, `docs/adr/0003-…`, `docs/ARCHITECTURE.md` (dependencies),
  `docs/dev/u1-user-messages.md` §§ 1, 5, 7, 10 (one-line pointers to this note).
* Tests: every A test file of section 21; `tests/AdMng.App.UiTests/HostTests.cs` (*edit*).

### 22.2 Track B – U1d (salutations)

Owns:

* Core: `Messaging/Salutations/*` (`TitleTable.cs`, `TitleParser.cs`, `GenderGuesser.cs`, `GivenNameLists.cs`,
  `SalutationData.cs`, `SalutationResolver.cs`, `SalutationIssueCodes.cs`), `Messaging/UserMessageFields.cs` (*edit*),
  `Templates/TemplateEngine.cs` and `Templates/ITemplateEngine.cs` (*edit*: `AllowedArguments`, `ReportsEmpty`,
  `capitalize`).
* Presentation: `Settings/SalutationSettings.cs`, `UserMessages/SalutationSettingsViewModel.cs`,
  `UserMessages/UserMessageSettingsViewModel.cs` (*edit*), `UserMessages/UserMessageServices.cs` (*edit*:
  `UserMessageSenders.From`), `UserMessages/UserMessageComposerViewModel.Salutations.cs`,
  `UserMessages/UserMessageItems.Salutations.cs`, `Localization/L.Salutations.cs`.
* App: `Views/SettingsWindow.xaml` (*edit*: the section).
* Docs: `docs/dev/u1-user-messages.md` § 2 (fields line pointing here).
* Tests: every B test file of section 21; `tests/AdMng.Testing/SalutationSampleData.cs`.

### 22.3 Shared files and how each track edits them

Both tracks touch the files below **only at the stated places**, so a merge has no overlapping hunks (keep at least
three unchanged lines between the two tracks' insertions); bulk code goes into the tracks' own files and partial classes.

| File | Track A | Track B |
|---|---|---|
| `src/AdMng.Core/Messaging/MessageModel.cs` | `MessageTemplate.Images` **after `English`**; `OutgoingUserMessage.InlineImages` after `HtmlBody` | `MessageTemplate.Salutation` **after `Recipients`** (last property) |
| `src/AdMng.Core/Messaging/UserMessagePlanner.cs` | `UserMessagePlanRequest.Images`/`OmitImages` **after `Inputs`**; the HTML block of `CheckTemplate`; in `PlanOne` after the `htmlBody` rendering and inside the `new OutgoingUserMessage` initializer (`InlineImages = …`) | `UserMessageSender.Salutations` after `TimeZone`; `BaseAccountAttributes`/`AccountAttributesFor`/`ManagerAttributesFor`; in `PlanOne` the `UserMessageFieldData` initializer (after `Domain = request.Domain,`) and the final `new PlannedUserMessage` initializer (after `Message = message,`); `PlannedUserMessage.Derived` after `Message`; `UserMessagePlanRequest.GenderOverrides` after `Schema`; a public `Replan` delegating to `PlanOne` placed directly after `Plan` |
| `UserMessageComposerViewModel.cs` | hook lines only: request initializer in `PlanCoreAsync` (after `Inputs = inputs,`), the `TransportFit` call before `RunAsync` and before the test send | hook lines only: the salutation options passed to the `AccountAttributesFor` / `ManagerAttributesFor` calls at the top of `PlanCoreAsync`, the `Matches` switch (`Guessed` case before `_ => true`), keeping the last read accounts after `ShowPlan(plan)` |
| `UserMessageItems.cs` | – (partial file) | `UserMessagePreviewFilter.Guessed` appended to the enum |
| `src/AdMng.App/Views/UserMessagesWindow.xaml` | the compose HTML `Expander`; the details `TabControl`; the transport bar above the send button | a new `GridViewColumn` after the Language column; the *Pola wyliczone* panel between the subject grid and the issues text; the *Zwroty* group after the recipient-mode `WrapPanel` |
| `src/AdMng.Presentation/Settings/UserMessageSettings.cs` | `RenderedPreview` after `Transport`; in `Normalize()` the template initializer line `Images = …` **after `English = …`** | `Salutations` after `Templates`; in `Normalize()` `Salutations ??= new(); Salutations.Normalize();` after `Smtp.Normalize();` and `Salutation = …` **after `ConditionDays = …`** |
| `UserMessageTemplateLibrary.cs` | export/import v2 (methods `ExportJson`, `ImportAsync` body, `TemplatesFile`); import initializer line after `English = …` | import initializer line after `ConditionDays = …` |
| `Strings.resx` + `Strings.pl.resx` | one block of `UserMessages.Rich.*`, its `UserMessages.Issue.*` and `UserMessages.Code.*` keys, inserted **directly after the `UserMessages.Html` entry**; `Watchdog.Hint.smtp.messageTooLarge` directly after `Watchdog.Hint.smtp.basicAuthDisabled` | one block of `UserMessages.Salutation.*`, `UserMessages.Filter.Guessed` and its `UserMessages.Issue.*` keys, inserted **directly after the `UserMessages.SettingsTab` entry** |
| `README.md` (U1 section) | one bullet "U1c" after the *Wysyłka przez AdMng Watchdog (U1b)* bullet | one bullet "U1d" after the *Szablony* bullet |
| this note | Part A sections | Part B sections |

Not touched by either track (left to the integration step): the README status lines (roadmap diagram, the path table
row, the "Ścieżka U" heading), `docs/ROADMAP.md`, release notes and versions, CLAUDE.md.

### 22.4 Integration step (after both merges)

Screenshot numbers: track K (`docs/dev/k-computers-and-gpo.md`) owns `22-computers.png` and `23-group-policy.png`;
U1d uses `25-user-messages-salutations.png`; U1c renumbers its `22-user-messages-rich-html.png` to the next free one
(24) and the README / design-note references with it.

Rebase the second track on the first, build the solution with 0 warnings, run Core, Presentation, Mail, Watchdog Core
and Service tests twice, the Mailpit and lab tests, the Windows UI job (both screenshots); then update the README
status lines and `docs/ROADMAP.md` ("U1c, U1d: 🟡 zaimplementowane, czekają na pilotaż"), and add a joint test that a
pasted Outlook template with `{$salutation}` split by `SpellE` spans is repaired and renders "Szanowny Panie
Profesorze" with an inline logo through both transports.

### 22.5 Suggested commit sequence

* **A:** (1) CF_HTML parser, image model/formats/stripper/policy; (2) CSS parser, inliner, sanitizer, AngleSharp;
  (3) importer, Word lists, placeholders, Safe Links, text; (4) planner/rules integration; (5) MIME body + Mailpit test;
  (6) Watchdog contract, validator, storage 0007, service, client fakes, lab test; (7) Presentation store, library v2,
  transport fit, paste workflow, resx; (8) App clipboard reader, WebView2 preview with fallback, window, DI, UI tests,
  screenshot; (9) docs and ADRs.
* **B:** (1) title table and parser, gender guesser and lists; (2) resolver, fields, engine changes; (3) planner
  integration and `Replan`; (4) settings, view models, composer marks/override/filter, resx, sample data; (5) settings
  section and window column/panel, UI tests, screenshot; (6) docs.
