**English** · [Polski](../../adr/0008-watchdog-dane-w-osobnym-folderze.md)

# ADR-0008: Watchdog stores data in a separate folder (an exception to ADR-0003)

- Status: Accepted
- Date: 2026-10-14 (amended the same day after the operational, security and implementation reviews)
- Amends: [ADR-0003](0003-no-local-data.md) (only for the AdMng Watchdog service) and rule 2 in
  `CLAUDE.md` (only the Watchdog channel secrets)
- **Amended by [ADR-0009](0009-watchdog-on-linux.md)** (2026-10-20; in force since release 0.3.0, in which the
  service runs on Linux only): the conditions of this decision still apply, but we implement them differently – the data
  folder `/var/lib/admng-watchdog` (0700, checked at every start) instead of NTFS ACLs, a second short-lived place
  for OpenWEC output files (`/var/lib/openwec/out`), a GPO settings snapshot without secrets, logon records for 48
  hours, event retention of 1095 days with the `MinEventsDays` threshold, secret keys protected by a systemd credential
  (TPM2 + host key or host key) instead of DPAPI-NG, the service account keytab as a new exception to rule 2,
  logs in journald. Details: ADR-0009, section "Amendment to ADR-0008". The description below concerns the Windows
  service of release 0.2.0.
- **Supplemented (2026-10-26, U1b):** the service's outbox also carries user messages submitted by AD Manager, and the
  service audits them without content – section "Addendum: user messages (U1b)" at the end.

## Context

[ADR-0003](0003-no-local-data.md) forbids storing local copies of directory data: AD Manager reads
everything from the DC on demand and keeps data in memory only while it is displayed.

The user ordered a watchdog that reports changes in AD around the clock (new accounts and computers, changes of
account parameters, lockouts, approaching expiry dates, permission changes), with history, configurable rules, SMTP
delivery, a daily report and Teams / webhook channels. Such a feature cannot work without state:

- an "old → new" change can be computed only if the previous value is stored somewhere – AD keeps only the current
  state (and `whenChanged`, replication metadata and DirSync do not provide previous values);
- the progress marker (`highestCommittedUSN` for each DC), the DC identifier (`invocationId`), the notification queue
  with retries, the deduplication of account expiry warnings and the daily report must survive a restart;
- attribution of the actor from the DC security logs often arrives later than the detection of the change, so events
  must wait for completion;
- the operator wants to browse the event history, not just receive notifications;
- the passwords and addresses of notification channels (SMTP, Teams, webhook) must survive a restart of a service that
  runs without an operator.

The user deliberately changed the decision: "Let's change the decision about collecting data after all, let there be a
separate folder for it", and agreed to a separate service account running 24/7.

Options considered:

1. **No state** (only comparison with the previous in-memory read) – loss of history and detection after every
   restart, no "before" values after a crash, no retrying of deliveries. Rejected.
2. **Storing in the AD_MNG application on administrators' workstations** – personal data on many workstations, no
   24/7 operation. Rejected; contradicts ADR-0003 with no benefit.
3. **An external SIEM only** – requires a system the customer may not have, and provides no tab in AD_MNG.
   It remains possible as a webhook channel.
4. **A separate service with its own data folder on a server** – chosen.

## Decision

We relax ADR-0003 **only** for the AdMng Watchdog service host (the project `src/AdMng.Watchdog.Service` and the
store implementations it runs). The libraries `AdMng.Watchdog.Client`, `AdMng.Watchdog.Contracts`,
`AdMng.Watchdog.Core` (outside the service host) and AD_MNG (Presentation, App) remain under the full ADR-0003 ban.
The service may store directory data in its dedicated data folder under the following conditions:

1. **One place.** Directory data is written only to the service data folder (by default
   `%ProgramData%\AdMng\Watchdog\data`, configurable): the SQLite database `watchdog.db` (with WAL files), backups in
   `backup\`, the key ring in `keys\`. Nowhere else: not in temporary files (SQLite runs with
   `temp_store = MEMORY`, so sorts and copies do not end up in the `%TEMP%` of the gMSA profile), the registry, crash
   dumps (WER is disabled for the service), the Windows event log, the console or diagnostic logs – these still
   contain no directory values or exception contents.
2. **Only what is needed.** The snapshot contains only the watched attributes (the list is in the specification;
   the administrator can add further attributes only one at a time, after checking them in the schema – never `*`,
   confidential, secret or constructed attributes), group memberships and identifiers (GUID, SID, DN); events contain
   "before → after" changes, attribution and notification state; DC logs – only the normalized fields needed for
   matching (attribute values only for watched, non-secret attributes; out-of-scope records are discarded).
3. **Never directory secrets.** Passwords and their hashes, LAPS, BitLocker, TPM, gMSA passwords, LSA secrets
   (`currentValue`, `priorValue`), trust attributes and all attributes marked in the schema as confidential
   (`SecretAttributes.IsSecret`) are neither read nor stored; a change of such an attribute is noted as
   "changed (value hidden)", and the values in 5136 events are masked before storing.
4. **Retention and deletion.** Each kind of data has a retention period with a default value (events 365 days,
   events from DC logs 30 days, snapshots of deleted objects 180 days, backups 7 days, pre-migration copies – the
   newest + 30 days) and a database size limit; the content of a sent notification is deleted right after
   sending (the metadata remains). Retention is enforced daily, and deleted records are overwritten
   (`secure_delete`).
5. **Access.** The data folder has inheritance disabled and permissions only for SYSTEM, local
   Administrators and the service account (gMSA); the service **enforces** this at every start (with broader
   permissions it does not start). Startup settings (roles, authentication) are in a separate folder `config\`, which
   the service account cannot modify. Data is exposed only through the service API over HTTPS (TLS 1.2/1.3) with
   **Kerberos-only** authentication (in both server modes), for members of configured AD groups; the Viewer role gets
   data at the "standard" level, and reading individual events is logged. The service server and its account are
   administered as Tier 0 resources.
6. **Channel secrets** (SMTP password, OAuth secret, Teams Workflows and webhook addresses, signing keys) are encrypted
   (ASP.NET Core Data Protection, keys protected with DPAPI-NG for the gMSA SID), **bound to the destination** (server,
   account, tenant – changing the destination requires re-entering the secret), stored in a single transaction with
   the configuration and its audit, and never returned by the API. This is the only exception to rule 2 in
   `CLAUDE.md`.
7. **Outbound data** (email, Teams, webhook) is a copy outside the folder – therefore each channel has a level of
   detail (minimal / standard / full), and secrets are never sent.
8. **AD_MNG unchanged.** The WPF application still stores nothing locally: the Watchdog tab fetches data from the
   service on demand and keeps it in memory; on disk it stores only the service address, the layout and named filters
   (like saved views). CSV export of events happens only at the operator's explicit command (as in ADR-0003).
9. **Protection against silencing.** Every configuration change is audited and reported as an event (also to existing
   recipients); disabling critical detections through the API requires local consent in the startup settings.

`CLAUDE.md` (rules 1 and 2), ADR-0003 (status) and `docs/WATCHDOG.md` describe this exception; code reviews check
that directory data does not leave the service folder or the explicitly configured channels.

## Consequences

- Watchdog can show history and "before → after" values, restores its state after a restart and does not lose
  notifications.
- The data folder contains employees' personal data: the customer must include it in the record of processing
  activities (GDPR), the backup policy (backups have the same access restrictions and should be encrypted;
  BitLocker on the volume is recommended) and the server decommissioning procedure.
- Restoring the service requires the database, the `keys\` folder and the same gMSA account; after the gMSA is
  recreated, the channel secrets must be entered again.
- The service code has tests ensuring that diagnostic logs contain no attribute values or addresses (also with
  injected LDAP, SMTP and parsing errors), that API responses contain no secrets, that a stored secret
  will not be sent to a changed address, and that after records are deleted they are not in the database file.
- Whoever has administrator rights on the service server or can run code as the gMSA has access to all the data and can
  disable monitoring – hence the Tier 0 requirement and external monitoring (`/healthz`, the Application log,
  heartbeat).
- The scope of the exception is narrow: any other part of the solution that wanted to store directory data requires a
  separate decision.

## Addendum: user messages (U1b)

Since U1b AD Manager can send messages to users (templates with account data, README, track U) through the service
(`POST /api/v1/user-messages`, docs/WATCHDOG.md chapter 5.8). The conditions of this decision apply unchanged, and the
new data fits within them as follows:

- **The outbox** (`outbox`, point 4) stores the ready message (subject, text, HTML, the *From*, *To*, *CC*,
  *Reply-To* addresses) only in the content of the queued message (the `payload` column, like the content of alerts),
  which is deleted right after sending; failed ones – after `OutboxDeadDays` (until manual retry), like any other
  message. The metadata in the delivery log contains neither the subject nor addresses (only the kind, state, number of
  recipients and codes).
- **The audit** (`user_messages`, schema 6): message identifier, time, the operator's SID and account, number of
  recipients, SHA-256 hash of the sorted addresses and SHA-256 hash of the request (idempotency check) – never an
  address, subject or content; retention `retention.userMessageAuditDays` (365 days by default).
- **Diagnostic logs** – one line per message: identifier, operator account, number of recipients; no addresses,
  hashes or content.
- The feature is disabled by default (`userMessages.enabled`), and the AD Manager side remains under the full ADR-0003
  ban: messages and the report exist in the WPF process only in memory.

## Addendum: images in the content of user messages (U1c)

Since U1c a message to a user may have images embedded in the content (a logo, a screenshot from instructions – pasted
in AD Manager from Outlook, docs/dev/u1c-u1d-rich-html-and-salutations.md chapter 9, docs/WATCHDOG.md chapter 5.8).
The conditions of this decision and of the U1b addendum apply unchanged; the images fit within them as follows:

- **Only in the content of the queued message.** The images (PNG, JPEG, GIF – checked by the service, with metadata
  stripped) are part of the message's `payload` column in `outbox` and disappear with it: right after sending, and
  failed ones after `OutboxDeadDays`. There is no separate table or deduplication of images between messages; they are
  not in the audit (`user_messages` – the content hash for the idempotency check covers the images but does not store
  them), in the delivery log or in diagnostic logs (only numbers and codes).
- **Limited volume** (schema 7): the column `outbox.image_bytes` records the size of the images of a message that has
  not been sent yet or has been abandoned (0 after sending); the total is limited by `userMessages.maxStoredImageMb`
  (default 256, 16–4096) – above it the service answers 429 `imageStorageFull` (`Retry-After` 1 h) for messages with
  images, without holding back messages without images or alerts. Only the route `POST /api/v1/user-messages` accepts a
  request body of up to 6 MiB.
- **Can be disabled:** `userMessages.allowInlineImages` (enabled by default; a change of "medium" severity in the
  configuration audit) – when disabled: messages with images are rejected (`imagesDisabled`), and the status does not
  report the `inlineImages` capability.
- Images never come from the directory (`thumbnailPhoto`/`jpegPhoto` are not template fields) – they are fixed elements
  of the operator's template, the same for every recipient. The AD Manager side remains under the full ADR-0003 ban
  (see the addendum to ADR-0003 about templates).
