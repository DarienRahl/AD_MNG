# U1 – E-mail messages to users ("Wiadomości e-mail do użytkowników") – design note

Roadmap item U1 (README, "Ścieżka U"), part **U1a**: the AD Manager side with direct SMTP sending. Part **U1b** adds
the second transport, "through AdMng Watchdog" (service API endpoint + client), behind the interface described in
section 7 – its design is section 10. Binding rules (CLAUDE.md): rendered messages, recipient lists and reports exist **only in memory** (rule 1 –
on disk only templates and settings, plus the CSV report when the operator exports it); the SMTP password of the direct
mode lives only in memory for the session and never reaches the settings, a log or `ToString()` (rule 2); filters and
DNs through `LdapFilter`/`DistinguishedName` (rule 3); the plan reads only from the window's pinned DC (rule 4); nothing
is written to the directory (rule 5 does not apply – a message is not a directory change); every directory call and
every send is async with cancellation (rule 6).

Extensions: **U1c** (a formatted message pasted from Outlook, inline images, rendered preview, Watchdog contract v2) and
**U1d** (title-aware salutations) – [u1c-u1d-rich-html-and-salutations.md](u1c-u1d-rich-html-and-salutations.md).
**U5** adds a third transport, *through Outlook* (section 7), and the field `{$created}` (section 2) –
[u5-outlook-correspondence.md](u5-outlook-correspondence.md).

## 1. Pieces

| Piece | Project | Purpose |
|---|---|---|
| `TemplateEngine` (extension tokens) | Core (`Templates/`) | The batch editor's engine gains *extension tokens* (`$name` / `$name:argument`) evaluated by the caller and a value encoder applied to every token value (HTML encoding, header-safe text). The batch editor keeps the default instance without extensions. |
| `UserMessageFields` | Core (`Messaging/`) | The message flavour of the engine: `{$passwordExpires:d MMMM yyyy}`, `{$passwordExpiresInDays}`, `{$accountExpires:…}`, `{$accountExpiresInDays}`, `{$lastLogon:…}`, `{$lockedSince:…}`, `{$created:…}` (U5), `{$manager:displayName}`, `{$input:termin}` + all batch-editor tokens and functions. Dates in the message language (pl-PL / en-US) and the message time zone (Europe/Warsaw). Values come from `AccountStatus` (U2). |
| `MessageTemplate`, `BuiltInMessageTemplates` | Core | Template = name, PL and EN text (subject, text body, optional HTML body), send condition with a day parameter, recipient mode. Built-in texts in `Messaging/Resources/MessageTemplates(.pl).resx`. |
| `UserMessageRules` | Core | Security: forbidden fields (secrets, anything with "password"/"pwd", inputs named like a password), link allow-list, HTML restrictions, header sanitizing, subject length. |
| `UserMessagePlanner` | Core | Pure: accounts (+ managers) already read → one `PlannedUserMessage` per account with status, issues and the rendered `OutgoingUserMessage`. |
| `UserMessageDispatcher`, `MessageRateLimiter` | Core | Sends planned messages through an `IUserMessageTransport`: rate limit, retries of transient failures (SMTP 4xx, timeouts, connection loss), stop on authentication/TLS failures, cancellation, progress, per-recipient result. |
| `UserMessageReportExporter` | Core | CSV of the report (on demand). |
| `IUserMessageTransport`, `IUserMessageConnection`, `IDirectSmtpTransportFactory` | Core | Transport contract (section 7). |
| `SmtpUserMessageTransport` | **AdMng.Mail** (new, net10.0, MailKitLite) | Direct SMTP: TLS modes, certificate check (system trust / pinned SHA-256), authentication (none, current Windows account via SASL GSSAPI/NTLM, user name + password), MIME (UTF-8, RFC 2047, `Auto-Submitted`), classification of failures with the Watchdog's hint codes (`DeliveryHints`), connection test. |
| `UserMessageSettings` | Presentation (`Settings/`) | Persisted configuration: sender, reply-to, CC, rate limit, confirmation threshold, allowed link domains, default language, time zone, SMTP connection (never a password), custom templates. |
| `UserMessageTemplateLibrary` | Presentation (`UserMessages/`) | Built-ins + operator templates; save, delete, export/import (JSON, text only – like saved views). |
| `UserMessageTransportSelector`, `WatchdogUserMessageTransport` (U1b, section 10), `SmtpCredentialCache` | Presentation | Chooses the transport (section 7); the session-only SMTP password. |
| `UserMessageComposerViewModel` + `UserMessagesWindow` | Presentation / App | The window: compose → preview → send → report. |
| `UserMessageSettingsViewModel` | Presentation | The "Wiadomości e-mail" tab of the settings window with *Testuj połączenie* and *Wyślij wiadomość testową do mnie*. |
| `IUserMessageLauncher` (U2 hook) | App (`DialogService`) | Opens the window: property sheet (Account tab), list context menu / palette `object.sendMessage`, account review lists (template matching the check). |

U1c adds the HTML sanitizer and importer (`Messaging/Html`, AngleSharp), inline images (`Messaging/Images`), the shared
MIME body (`AdMng.Mail/UserMessageMimeBody`), the image store, paste workflow and transport fit (Presentation) and the
clipboard reader and WebView2 preview (App) – [u1c-u1d-rich-html-and-salutations.md](u1c-u1d-rich-html-and-salutations.md)
section 1.

## 2. Templates and fields

* A template has a **Polish and an English text** (subject, text body, optional HTML body). The recipient's
  `preferredLanguage` chooses (`pl*` → Polish, `en*` → English, anything else → default language from the settings,
  Polish by default). An empty text of the chosen language falls back to the other language.
* HTML body: when the template has none, it is generated from the text body (paragraphs, line breaks, every value HTML
  encoded). In an operator's HTML body every token value is HTML encoded (`TemplateContext.Encode`); the literal HTML is
  the operator's and is restricted (no `<script>`, `<iframe>`, `<object>`, `<embed>`, `<form>`, `<input>`, `<link>`,
  `<meta>`, `<base>`, no `on…=` handlers, no `javascript:`/`data:`/`vbscript:`/`file:` URLs).
* **Fixed footer** (resx, per language) is appended to text and HTML: "automatic message of the IT department – we never
  ask for your password". Templates cannot remove it.
* The message time zone is the setting `UserMessages.TimeZone` (default `Europe/Warsaw`, not shown in the UI).
* Fields: every attribute (`{givenName}`, `{sAMAccountName|lower}` …) and function of the batch editor, plus the
  extension tokens of section 1. `{$passwordExpires}` without a format uses `d MMMM yyyy`; dates are the local date of the
  instant in the message time zone, formatted with the message culture (pl-PL genitive month names: "5 listopada 2026").
  `{$passwordExpiresInDays}` = `AccountStatus.PasswordDaysLeft` (calendar days, 0 = today). Password expiry always from
  `msDS-UserPasswordExpiryTimeComputed` (FGPP); "never expires" and "must change" are separate states – the fields are
  empty then and the condition decides who is skipped. `{$created[:format]}` (U5) is the account's `whenCreated`
  (`AccountStatus.Created`), a date token like `{$lastLogon}`; empty (and reported as an empty field) when not readable.
* **Salutations (U1d):** `{$salutation}`, `{$title}`, `{$titledName}`, `{$honorific:case}`, `{$byGender:masc/fem}` and
  their `manager…` twins (title from `personalTitle` or the display name, gender from the attribute or the names, title
  table and given names in the settings) – [u1c-u1d-rich-html-and-salutations.md](u1c-u1d-rich-html-and-salutations.md)
  Part B.
* Inputs `{$input:name}` are typed by the operator in the window (one value for all recipients). An empty input is a
  plan error.
* **No password field:** a template that references a secret attribute (`SecretAttributes.IsSecret`), any attribute whose
  name contains `password`/`pwd`, or an input whose name looks like a password (`hasło`, `haslo`, `password`, `pass`,
  `pwd`, `secret`, `sekret`) is rejected. There is no token that yields a password.
* Built-in templates (ids): `password-expiring` (condition: expires within N days, default 14), `password-expired`,
  `account-expiring` (N = 30, user + manager), `account-expired` (user + manager), `account-locked`, `account-unlocked`,
  `must-change-password`, `inactive-disable` (N = 90, `{$input:termin}`, user + manager), `custom` (free text, starts
  empty). Built-ins cannot be changed or deleted – *Zapisz jako szablon…* stores a copy.
* Library: operator templates are stored in `settings.json` (`UserMessages.Templates`) – name, texts, condition and mode;
  export/import as JSON (`{ "templates": [...] }`), import adds copies with new ids. No directory data.

## 3. Recipients and conditions

* **Address of an account:** `mail`, else the primary `SMTP:` address of `proxyAddresses` (info note in the preview).
* **Recipient mode** (template default, changeable in the window): user; manager (`manager` → its `mail` / primary
  SMTP); user and manager (user in To, manager in CC; one of them alone when the other has no address); user, else the
  manager. Always **one message per account** – never a shared To/CC list of several users. Helpdesk CC from the
  settings is added to every message.
* **Condition** (re-evaluated at plan time from the fresh read): none, password expires within N days, password expired,
  account expires within N days, account expired, locked, not locked, must change password, inactive for N days. Not
  met → *skipped* with the reason (e.g. the password was changed after the list was loaded).

## 4. Pipeline

1. **Plan** – fresh read of the accounts from the window's DC (batches of 100 DNs, `AccountStatus.Attributes` + name,
   `displayName givenName sn sAMAccountName userPrincipalName mail proxyAddresses manager preferredLanguage` + the
   attributes the template references), then their managers; the domain policy (`maxPwdAge`) for the estimate when the
   constructed value is missing. Every message is rendered.
2. **Validation** – per message: errors (no address, invalid address, condition not met → skipped, empty subject,
   subject longer than 250 characters, link outside the allow-list, unresolved template), warnings (account disabled,
   empty field in the text, manager disabled / missing in a manager mode, the same address for several accounts,
   address from `proxyAddresses`). Template errors (syntax, forbidden field, empty input, disallowed HTML) block the plan.
3. **Preview** – every message (To, CC, Reply-To, subject, text, HTML source), filter (all / ready / warnings / errors /
   skipped), *exclude* per message, *send a test message to me* (the selected message, subject prefixed `[TEST]`, to the
   test address from the settings, To only – no CC).
4. **Send** – confirmation; above the threshold (settings, default 10) by typing the number of messages. Background
   task with progress (sent / total, accepted, failed, retrying), *Przerwij*. Rate limit: messages are spaced
   `60 s / rate` (default 30 per minute – Exchange Online) and a daily recipient budget (default 10 000, counted in memory
   for the application session). Transient failures (4xx, timeout, connection lost) are retried up to 3 times after 10 s
   and 30 s on a new connection; authentication / TLS / configuration failures stop the run (the rest is *not sent*);
   permanent 5xx rejections fail only that message.
5. **Report** – status per message: accepted by the server (with the time), rejected (SMTP code, enhanced code, hint –
   the Polish texts of the Watchdog channel tests), not sent (cancelled / stopped / excluded / skipped). *Ponów nieudane*
   plans the failed and not-sent ones again (fresh read). *Eksportuj CSV…* on demand. Session log: one entry per run
   ("Wysłanie wiadomości do użytkowników", template name, `accepted/total`, transport) – never addresses or content.

## 5. MIME and security

UTF-8 text (`text/plain` + `text/html`, `multipart/alternative`), subject and display names RFC 2047 encoded by MimeKit,
`Message-ID <guid@local-domain>`, `Date`, `Auto-Submitted: auto-generated`, `X-Auto-Response-Suppress: All`,
`X-AdMng-Message: user-notification`. CR/LF and control characters are removed from the subject, display names and every
value that ends in a header (`UserMessageRules.HeaderSafe`), addresses are parsed strictly (one mailbox, local part and
domain). **No attachments** – the transport contract has no attachment field. Link allow-list: the configured domains
plus the AD DNS domain and the sender's domain; a host matches a domain or its subdomain; mailto: links are allowed.
No protocol logger is attached to the SMTP client (it would log content).

U1c: the HTML part becomes `multipart/related` with inline images (`Content-ID`, `cid:` references, inline disposition –
still never an attachment), the HTML is checked by the shared sanitizer and may be up to 400 000 characters –
[u1c-u1d-rich-html-and-salutations.md](u1c-u1d-rich-html-and-salutations.md) sections 4, 7 and 8.

## 6. Direct SMTP (AdMng.Mail)

* TLS: **STARTTLS required** (default; the server must offer it, else the connection fails with the hint "server does
  not offer STARTTLS"), implicit TLS (465), STARTTLS when offered, none – the last two only after the operator confirmed
  a warning in the settings (stored as an explicit flag). TLS 1.2+ (`SslProtocols.Tls12 | Tls13`).
* Certificate: system trust (default) or pinned SHA-256 of the server certificate (64 hex characters; same format and
  comparison as the Watchdog). There is no "accept any".
* Authentication: none (relay); current Windows account – SASL `GSSAPI` (Kerberos) when offered, else `NTLM` via
  `SaslMechanismNtlmNative`, both with the default network credentials; user name + password – the password is asked in
  the window when needed and kept in `SmtpCredentialCache` (memory, for the session, per host + user name, `ToString()`
  without it). **XOAUTH2 (Exchange Online) is not done in U1a:** the repo's MSAL code is the Watchdog's confidential
  client (client secret in the service's secret store); a delegated interactive flow in the WPF app would need its own
  app registration and token cache and does not fit cleanly – Exchange Online goes through the Watchdog (U1b).
* *Testuj połączenie*: connect, TLS, EHLO capabilities, authentication – result with stage, code and hint.
* Failures are classified like the Watchdog's `SmtpErrorClassifier` (same codes `smtp.<status>[.<enhanced>]`, `auth`,
  `tls`, `timeout`, `socket.*`, `io`, and the same hint keys incl. 5.7.139 / 5.7.30 / 5.7.68).

## 7. Transport contract (for U1b)

```csharp
public interface IUserMessageTransport
{
    UserMessageTransportKind Kind { get; }                          // DirectSmtp | Watchdog | Outlook (U5)
    string Description { get; }                                     // "smtp.contoso.com:587" / "AdMng Watchdog (https://…)"
    Task<UserMessageTransportStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<IUserMessageConnection> ConnectAsync(CancellationToken cancellationToken);
}

public interface IUserMessageConnection : IAsyncDisposable
{
    Task<UserMessageDelivery> SendAsync(OutgoingUserMessage message, CancellationToken cancellationToken);
}
```

`OutgoingUserMessage` = id (GUID), From, To (1–2 addresses), CC, Reply-To, subject, text, HTML, language – everything
already rendered and checked; `UserMessageDelivery` = outcome (`Accepted`, `Rejected`, `Transient`,
`AuthenticationFailed`, `TransportFailed`), SMTP status, code, hint. The dispatcher (rate limit, retry, report) is
transport-independent.

**Selection** (`UserMessageTransportSelector`): setting *Transport* = automatic (default) / direct SMTP / Watchdog.
Automatic picks the Watchdog transport when the Watchdog connection is configured **and** `GetStatusAsync` reports it
available, else direct SMTP. U1a registered `WatchdogUserMessageTransport` as a stub; U1b implements it (section 10):
status from `GET /api/v1/user-messages/status`, send through `POST /api/v1/user-messages` with the rendered message (the
service applies its own sender, queue, retries and log). An unavailable Watchdog is shown with its reason
(`UserMessages.Problem.Watchdog{NotConfigured,Disabled,Forbidden,Unreachable,NotSupported}`).

U1c: `OutgoingUserMessage.InlineImages` and `UserMessageTransportStatus.Capabilities` (images, their limits, the HTML
length, the room left in the service's outbox); the window checks the planned messages against them before sending
(`TransportFit`) and never switches transport or drops images silently –
[u1c-u1d-rich-html-and-salutations.md](u1c-u1d-rich-html-and-salutations.md) sections 7.3 and 9.6.

U5: a third choice, *through Outlook* (`UserMessageTransportKind.Outlook`, `UserMessageTransportChoice.Outlook`): the
operator's classic Outlook sends the rendered message (`OutlookUserMessageTransport` in `AdMng.Outlook`, created by
`IOutlookUserMessageTransportFactory`), from the account in `UserMessageSettings.OutlookAccount` (empty – the profile's
default account) and, with `OutlookOnBehalfOfSender`, on behalf of the configured sender. Automatic never picks it.
Problems `OutlookNotSupported` / `OutlookNotInstalled`; delivery codes `outlook.sent` and the `outlook.*` failure codes;
capabilities `UserMessageTransportCapabilities.Outlook` (inline images) –
[u5-outlook-correspondence.md section 5](u5-outlook-correspondence.md#5-u1-outlook-transport).

## 8. UI

`UserMessagesWindow` (modeless, one per launch): top – transport line and the recipients count; three stages in one
window: **Wiadomość** (template, language tab PL/EN for the subject / text / HTML editor, condition with N, recipient
mode, inputs, recipients list with *Dodaj…* / *Usuń*, library menu: save as, delete, export, import), **Podgląd**
(filter, list with include check boxes, details of the selected message, *Wyślij wiadomość testową do mnie*,
*Wyślij*), **Wysyłka / raport** (progress, *Przerwij*, report list, *Ponów nieudane*, *Eksportuj CSV…*). The SMTP
password box appears only when the authentication needs it and no password is cached. Closing the window while sending
asks to stop.

Settings window: a new tab *Wiadomości e-mail* (sender, reply-to, CC, test address, language, rate, daily limit,
threshold, allowed domains, transport choice, SMTP host/port/TLS/certificate/authentication/user name, test buttons).

## 9. Tests

* Core: extension tokens and encoders; fields (Polish diacritics, `d MMMM yyyy` in pl and en, Europe/Warsaw across the DST
  change, days left, manager, inputs); rules (forbidden fields and inputs, links, HTML, header injection, subject);
  planner (languages, conditions with N, recipient modes, proxy address fallback, duplicates, disabled, empty field,
  footer, encoding); dispatcher with a fake transport and a manual clock (spacing, daily budget, 4xx retry on a new
  connection, permanent rejection, stop on auth failure, cancel, progress); report CSV.
* Mail: MIME (UTF-8 subject with diacritics, RFC 2047, headers, CR/LF injection), classifier; the transport against the
  in-process SMTP servers of AdMng.Watchdog.Testing (`SmtpSink`: STARTTLS with the pinned test certificate, implicit TLS,
  AUTH; `ScriptedSmtpServer`: 550 / 451 per recipient, 535 5.7.139, credentials never sent unencrypted, no GSSAPI/NTLM
  offered); **Mailpit** integration (`ADMNG_TEST_SMTP_HOST`/`_PORT`, `ADMNG_TEST_MAILPIT_API`; skipped without them):
  Polish subject and body arrive with the automatic headers, a recipient rejection (chaos API), a 4xx retried, rate limit
  spacing; the Mailpit certificate is learnt by a first test and then pinned.
* Presentation: composer over the fake DC with the sample company (plan, preview, exclude, test message, typed
  confirmation, send, report, retry failed, CSV, session log without content, session end), settings tab, library
  export/import, transport selection, password only in memory, launcher mapping from the review checks.
* App: UI test (window opens from the list context menu command, stages) and screenshot `18-user-messages.png`; smoke test.

## 10. U1b – sending through AdMng Watchdog

Binding rules for the service side: CLAUDE.md rules 1–2 with the ADR-0008/0009 exceptions – the service may keep the
rendered message in its outbox payload until it is sent (ADR-0008, "Uzupełnienie: wiadomości do użytkowników (U1b)"),
everything else stays value-free.

U1c (contract v2): `inlineImages` in the request, the `inlineImages` capability and limits in the status, HTML up to
400 000 characters, a 6 MiB body limit for this route only, `userMessages.allowInlineImages` / `maxStoredImageMb`,
migration 0007 (`outbox.image_bytes`) – [u1c-u1d-rich-html-and-salutations.md](u1c-u1d-rich-html-and-salutations.md)
section 9; ADR-0008 "Uzupełnienie: obrazy w treści wiadomości do użytkowników (U1c)". The limits below are U1b's.

**Contract** (`AdMng.Watchdog.Contracts`, `Api/UserMessageDtos.cs`, routes `WatchdogRoutes.UserMessages` /
`UserMessagesStatus`):

* `POST /api/v1/user-messages` (Admin **and** Viewer – user decision; `DeniedSids` wins; CSRF header) with
  `UserMessageRequest` = `Id` (idempotency key = Message-ID local part), `From?`, `To` (1–2), `Cc`, `ReplyTo?`, `Subject`,
  `TextBody`, `HtmlBody?`, `Language` ("pl"/"en"). No attachment field; the type has
  `[JsonUnmappedMemberHandling(Disallow)]`, so `attachments`, `bcc` … are 400 `invalidJson`. Answer `UserMessageResult`
  = `State` (`Delivered` / `Rejected` / `Queued` / `Failed`), `Code`, `SmtpStatus`, `Hint` (`DeliveryHints`),
  `RejectedCopies`, `Duplicate`, `OutboxId`; 200, or 202 for `Queued`. Errors: 400 validation issues
  (`UserMessageIssueCodes`), 409 `user-messages-unavailable` (`userMessagesDisabled` / `smtpNotConfigured`), 409
  `idempotency-conflict`, 429 `rate-limited` with `Retry-After`.
* `GET /api/v1/user-messages/status` (Admin and Viewer) → `UserMessagingStatusDto`: `Enabled`, `SmtpConfigured`,
  `Available`, `Channel` ("host:port"), `Sender` (`smtp.from` address), `AllowedSenders`, the limits and the messages
  submitted in the last hour / day.
* Configuration `WatchdogConfiguration.UserMessages` (`UserMessagingSettings`): `Enabled` (default **off**),
  `MaxMessagesPerHour` 300, `MaxMessagesPerDay` 2000 (sliding windows, all callers), `AllowedSenders` (addresses or
  `@domain`); `Retention.UserMessageAuditDays` 365. Validation: ranges, sender patterns, warning `smtpDisabledButUsed`;
  the config audit rates `userMessages.enabled` / `allowedSenders` High. `DeliveryKind.UserMessage` is the new outbox kind.

**Service** (`src/AdMng.Watchdog.Service/UserMessages`, `Api/UserMessageEndpoints.cs`):

1. Available only with `userMessages.enabled` and an enabled SMTP channel with host and a valid `smtp.from` (else 409).
2. `UserMessageRequestValidator` – never trusting the client: strict single mailboxes (`UserMessageRules.TryParseAddress`),
   CR/LF / control / U+2028–2029 in the subject, addresses or display names → **rejected** (`headerInjection`, not
   repaired), `UserMessageLimits` (To 1–2, To + CC ≤ 10, subject ≤ 250, text ≤ 50 000, HTML ≤ 100 000 characters),
   `UserMessageRules.FindForbiddenHtml`, language pl/en, From / Reply-To only `smtp.from`, `smtp.replyTo`, an
   `allowedSenders` address or domain (Reply-To may also equal the request's From) → `senderNotAllowed`. The envelope
   sender (MAIL FROM) is always `smtp.from`; a missing From / Reply-To takes the channel's. The service cannot check
   AD Manager's link allow-list (it does not know it).
3. One write transaction (`SqliteUserMessageStore`, table `user_messages`, migration 0006): an earlier row with the id →
   *Duplicate* (same caller SID/account and same SHA-256 of the request) or *Conflict* (409); the sliding hour/day counts →
   429 (Retry-After = until the oldest message of the window leaves it); else the outbox message (kind `UserMessage`,
   channel `email`, the addresses only in `RenderedEmail.UserMessage` inside the payload; subject and recipients columns
   empty, `recipient_count` = To + CC) and the audit row (who, when, recipient count, SHA-256 of the sorted lower-case
   addresses, SHA-256 of the request, outbox id). Log line 2610 with message id, caller and recipient count only.
4. The request then waits up to `ApiLimits.UserMessageWait` (20 s) for the first SMTP attempt, reported by
   `SmtpEmailTransport` through the in-memory `UserMessageDeliveryNotifier` (rate-limit deferrals are not attempts):
   Delivered → `Delivered` (+ rejected CC addresses, memory only); Permanent → `Rejected`; Transient → `Queued` (the
   outbox retries per `RetryPolicy`); ChannelBroken / AuthenticationFailed → `Failed`. No attempt in time → the state of
   the outbox row (Sent → Delivered, Dead → Rejected for a 5xx code else Failed, otherwise Queued). A repeated id answers
   the state of the first submission (waiting again while it is still queued).
5. Sending (`SmtpEmailTransport.SendUserMessageAsync`): one connection, one SMTP transaction for To + CC with MIME from
   `MimeMessageFactory.CreateUserMessage` (same headers as AD Manager's direct transport: `Message-ID <id@local-domain>`,
   `Auto-Submitted`, `X-Auto-Response-Suppress`, `X-AdMng-Message: user-notification`; no severity/rule headers);
   accepted when a To recipient is accepted, else classified by the To rejection; shared email rate limiter and
   credentials (Basic / OAuth2) of the channel.
6. `OutboxSenderJob`: user messages are leased and sent after the other due messages of their channel (alerts first);
   switched off while queued → given up `userMessages.disabled` (a withdrawn code, no channel alarm); a permanent
   rejection does not touch the channel health; ChannelBroken / AuthenticationFailed → Dead at once (AD Manager was told
   the run failed – no hourly retries for a day). Sent → payload NULL; Dead → payload kept for `OutboxDeadDays`
   (existing retention); audit rows purged after `UserMessageAuditDays`.

**Client** (`IWatchdogClient.GetUserMessagingStatusAsync`, `SendUserMessageAsync` – 45 s timeout);
`WatchdogClientException` gained `ProblemType` and `RetryAfter`. `FakeWatchdogClient` mirrors the service (default:
SMTP configured, messages switched off; `UserMessageHandler`, `UserMessages`).

**AD Manager** (`WatchdogUserMessageTransport`): status reasons `notConfigured` / `disabled` / `forbidden` /
`unreachable` / `notSupported` (404 or another API version); `ConnectAsync` checks the status (disabled →
TransportFailed `watchdog.disabled`); each send maps the answer – `Queued` counts as **accepted** with code
`watchdog.queued` (report text "Queued in AdMng Watchdog …"; the service finishes it and must never get it twice), 409
switched off / 429 limit / 400 `senderNotAllowed` → TransportFailed (the run stops, the rest is "not sent"), other 400 /
409 conflict → Rejected, 401/403 → AuthenticationFailed, certificate → TransportFailed `tls`, unreachable / timeout / 5xx
→ Transient (the dispatcher retries with the **same id**, so a lost answer never duplicates a message). The Watchdog
tab's *Channels and SMTP* sub-tab has the card *Messages to users (from AD Manager)* (switch, limits, allowed senders).

**Tests:** contract JSON (round trip, unknown members rejected, defaults), configuration validation, outbox job
(ordering, withdrawal, health), store (idempotency, sliding limit, purge), service API in process (roles Admin / Viewer /
none / denied, every validation rule, attachments, idempotency, limits, switch, SMTP sink with envelope sender and
headers, permanent rejection, rejected copy, queued), HTTPS client, Presentation transport mapping and composer over
`FakeWatchdogClient`, the channels card; the lab end-to-end test sends one message through Mailpit.
