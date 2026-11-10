**English** · [Polski](../../adr/0003-brak-lokalnych-danych.md)

# ADR-0003: No local copies of directory data

- Status: Accepted; partially amended by [ADR-0008](0008-watchdog-data-in-separate-folder.md) (only the AdMng
  Watchdog service)
- Date: 2026-10-13

## Context

User requirement: the application must not collect data locally – data is to be fetched from AD at the moment of
querying. Some tools (e.g. AD Explorer – snapshots, reporting tools with a local database) synchronize the directory
into a local database, which creates a copy of personal data and a risk of leakage.

## Decision

1. All object data is read from the DC on demand and kept in memory only, and only while it is displayed.
2. Only the following may be on disk: UI settings, connection profiles **without passwords**, defined views (name, DN,
   LDAP filter, columns) and diagnostic logs **without attribute values**.
3. Export (CSV, PowerShell script, session log) happens only at the operator's explicit command.
4. Schema metadata is loaded into memory at connection time and discarded after disconnecting.
5. Passwords (logon and reset ones) are never stored or logged.

## Consequences

- Fluidity is achieved with "live" techniques: fetching only the needed attributes, streaming paging,
  VLV for huge containers, parallel queries, refreshing a single object after a change, change notifications.
- Reports (e.g. inactive accounts) are LDAP queries executed on the fly, not an analysis of a local database.
- Tests and code review make sure that no logging contains attribute values.

## Addendum: user message templates (U1a, U1c)

User message templates – subject, text, HTML (also pasted from Outlook) and **their images** (logo, screenshots
from instructions) – are **operator settings**, like saved views, and not directory data: they may be stored in the
settings folder (`settings.json`, images in `message-images\{sha256}.png|jpg|gif` next to it) and in the template export
file that the operator saves themselves. Condition: nothing that comes from the directory ends up in a template or in
the settings – fields such as `{givenName}` are filled in only in memory when planning, no attribute (e.g.
`thumbnailPhoto`) is a source of an image, and the gender selection in the preview (U1d) affects only the current
sending. Generated messages, the preview (also in WebView2 – content and images served from memory, the browser's
temporary folder removed on close) and the report exist only in memory.

## Addendum: the "already sent" journal of Correspondence (U5)

The *Correspondence* window (sending through Outlook, [CORRESPONDENCE.md](../CORRESPONDENCE.md)) skips addresses that
have already been sent to, also after sending was interrupted and the program restarted. For this reason the settings
folder (`correspondence\journal.txt`) may hold a sending journal – but **without addresses**: each line is a keyed hash
(HMAC-SHA-256 with a random per-user key, first 16 bytes), the send time and the subject as typed by the operator
(without filled-in fields). The hash covers the recipient's address, and for mailbox query rows (the *Mailboxes* tab) –
the owner's address **together with the mailbox's address**, or, when the mailbox has no address, with its DN
(normalized address, a newline character and the mailbox's address or DN in lowercase): such a row is "already sent"
only with respect to that mailbox. The key (`journal.key`) is encrypted with DPAPI for the current Windows user,
`journal.lock` is an empty lock file, and when the key cannot be read (another computer or account) or the journal has
an unknown format, the previous journal is kept as `journal-<date>.old` – with the same hashes and within the same
retention period. No address, name, or mailbox address or DN ends up in these files as plain text. The journal does
not allow reading back a list of addresses or mailboxes, only checking whether a known address has already been sent
to (for mailbox rows: with respect to a known mailbox – a known address and mailbox pair). The recipient list, field
values from AD, the preview, the report and the matches of mailbox owners exist only in memory; Correspondence
templates and the last message draft are operator settings, like the U1 templates.
