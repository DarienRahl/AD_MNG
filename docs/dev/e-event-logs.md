# Track E – event log reader and analyses (design)

Product description (Polish): README, *Ścieżka E – Dzienniki zdarzeń: czytnik i analiza*. This note is the
implementation design of the first version: **E1** (the reader) and, from **E2**, the account lockout source and the
failed-logon analysis. Research behind it: event IDs, codes, access methods, comparable tools – summarized in 3–5.

## 1. Decisions (operator, 2026-11)

- **Transport: RPC (MS-EVEN6) now, WinRM later.** `System.Diagnostics.Eventing.Reader.EventLogSession` with
  `SessionAuthentication.Kerberos` and the connection window's credential (`SessionLogons.Of(session)`; the password
  only as an in-memory `SecureString`, rule 2), by FQDN, never NTLM. WinRM (aggregations on the target) is a later
  stage; the contracts below do not depend on the transport.
- **No reason prompt** for searches about a person; exports (CSV, HTML) say that the file contains personal data.
- **Scope:** reader (sources, channels, query builder, merged timeline, details, coverage and access diagnostics,
  `.evtx` files, saved queries, export) + **lockout source** (with the K1 pivot to the source computer) + **failed
  logons / brute force / password spraying**. Kerberos/RC4, NTLM, LDAP signing, logon history, account changes, GPO
  model vs reality, DC health and audit integrity are E2–E4 later.
- **Separate from AdMng Watchdog** (no service needed); with a connected Watchdog the lockout view links to its history.

## 2. Rules (CLAUDE.md) applied

1. Records, coverage and analysis results live **only in memory** while shown – no cache, index or temp file;
   diagnostic logs carry no event content (`ToString()` of every record type is value-free). Saved queries are
   settings: name, channel, IDs, levels, providers, data conditions and XPath as the operator typed them, host lists –
   never values read from a log. Export only to a file the operator picks.
2. Credentials as in K1 (`SessionLogons`), Kerberos only; nothing logged.
3. **XPath is built, never concatenated**: names restricted to `[0-9A-Za-z.\-_:{}$ ]`, values one XPath literal
   (single-quoted, double-quoted with an apostrophe, rejected with both quotes or a control character) – the rule of
   the Watchdog's `SecurityEventQueryBuilder`, whose literal function moves to Core and is shared. Raw XPath (expert
   mode) is passed only after a structural check (one expression, balanced brackets, only `*`, `System`, `EventData`,
   `UserData`, `Data`, `@Name`, `@SystemTime`, comparison operators, `and`/`or`, `timediff()`, `band()`); the target's
   own validation decides the rest and its error is shown.
4. LDAP lookups (SID → object, lockout state) go through the window's DC; the per-DC lockout state uses the existing
   `DomainControllerReader` (short-lived sessions, one DC each). Event sessions are separate read-only connections to
   the chosen hosts.
5. Nothing is written; the K1 pivot uses read-only remote calls.
6. Reads run on a dedicated thread per host, records are streamed in batches (`IAsyncEnumerable`), at most 4 hosts at
   once, cancellation calls `EventLogSession.CancelCurrentOperations()`; lists virtualize; at most
   `EventLogLimits.MaxRecordsInMemory` (50 000) records are held, aggregates keep counting past it.

## 3. Projects and contracts

| Where | What |
|---|---|
| `AdMng.Core/EventLogs` | `EventLogRecord`, `EventLogQuery`, `EventLogCoverage`, `IEventLogConnector`/`IEventLogReader`, `EventLogAccessException`, `EventLogLimits` (scaffold, frozen); `EventXmlParser` (event XML → record), `EventQueryXml` (query → structured query XML), `XPathLiteral`, `EventCodes` (NTSTATUS logon codes, Kerberos result codes, encryption and logon types – keys only, texts in Presentation), `InsertionStringDecoder` (moved from Watchdog.Core, which then uses it), `EventLogMatcher` (applies a query to records in memory – fake connector, file post-filters) |
| `AdMng.Core/EventLogs/Analysis` | `LockoutInvestigation` (query plan + pure analysis), `FailedLogonAnalysis` (query plan + pure analysis), `AuditEvidence` (which required events were seen against the coverage) |
| `AdMng.EventLogs` (new, net10.0, Windows at run time, App only – like `AdMng.Remote`) | `WindowsEventLogConnector`: `EventLogSession`/`EventLogReader` with the structured query, `ToXml()` → `EventXmlParser`, batches, coverage from `GetLogInformation` + `EventLogConfiguration`, message rendering on demand (`FormatDescription`), `.evtx` files (`PathType.FilePath`, forward only), error classification to `EventLogAccessError`; nothing logged |
| `AdMng.Remote` | `IRemoteComputerSession.GetScheduledTasksAsync` (CIM `MSFT_ScheduledTask`, `root/Microsoft/Windows/TaskScheduler`: path, name, principal user, state) for the lockout pivot |
| `tests/AdMng.Testing/EventLogs` | `FakeEventLogConnector` (hosts → channels → records, coverage, access failures per host, query applied by `EventLogMatcher`), Polish sample records |
| `AdMng.Presentation/EventLogs` | reader document, lockout and failed-logon documents, texts, settings (saved queries), entry points |
| `AdMng.App/EventLogs` | views; DI registration of the connector |

### 3.1 Record and query (scaffold)

`EventLogRecord { Host, Computer, Channel, Provider?, EventId, Version, Level?, Task?, Opcode?, Keywords, RecordId,
TimeCreated (UTC), ActivityId?, UserSid?, Data (name → value, document order, unnamed `Data` as `Data1`…, `%%` decoded,
`-` and empty omitted), IsAuditSuccess, IsAuditFailure }`. `Host` is where it was read (DC FQDN, computer FQDN or the
file name); `Computer` is `System/Computer`.

`EventLogQuery { Channel, From?, To?, EventIds, Levels, Providers, Outcome (Success/Failure/null), Data (each
`EventDataCondition(Name, Values)`: values OR-ed, conditions AND-ed), RawXPath?, NewestFirst (default true),
MaxRecords? }`. A file reader ignores `Channel` (the file is the log) and reads forward (`NewestFirst` is applied
after reading, bounded by `MaxRecords`).

### 3.2 Connector (scaffold)

`IEventLogConnector { bool IsSupported; Task<IEventLogReader> OpenComputerAsync(string host, IDirectorySession
session, CancellationToken); IEventLogReader OpenFiles(IReadOnlyList<string> paths) }`;
`IEventLogReader : IAsyncDisposable { string Host; Task<IReadOnlyList<string>> GetChannelsAsync(ct);
Task<EventLogCoverage> GetCoverageAsync(string channel, ct); IAsyncEnumerable<EventLogRecord> ReadAsync(EventLogQuery,
ct); Task<string?> FormatMessageAsync(EventLogRecord, ct) }`. Errors: `EventLogAccessException(EventLogAccessError
{ NotSupported, Unreachable, RpcUnavailable, AccessDenied, ChannelNotFound, InvalidQuery, FileUnreadable, Failed })` –
the message is value-free; the UI explains each kind (firewall group *Remote Event Log Management*, group *Event Log
Readers*, channel access).

`EventLogCoverage { Host, Channel, Oldest?, Newest?, RecordCount?, FileSizeBytes?, MaximumSizeBytes?, Mode (Circular,
AutoBackup, Retain, Unknown), IsEnabled, IsFull }` – the reader shows "DC01 sięga tylko 6 godzin wstecz" when the query
starts before `Oldest`.

## 4. Analyses

### 4.1 Lockout source (`LockoutInvestigation`)

**Input:** the account (sAMAccountName, SID, UPN, DN), per-DC state (`badPwdCount`, `badPasswordTime`, `lockoutTime`,
`lastLogon`, read through `DomainControllerReader`; the PDC emulator marked), the originating DC of `lockoutTime`
(`msDS-ReplAttributeMetaData`), the lockout policy (domain policy or the user's resultant PSO: threshold, observation
window, duration), the time window (default: 24 h before the last lockout or now), the records read from the plan,
the coverage of every DC.

**Query plan** (`Queries(account, from, to)`): on **every DC** `Security` 4740 (TargetUserName = account) and 4771,
4776, 4625 (TargetUserName = account, failures); the PDC emulator and the originating DC first. Data values compare
case-sensitively in the event log: the plan uses the sAMAccountName as stored in AD (with its lower-case, upper-case and
capitalized forms, `Jan.Kowalski`) and the UI also matches case-insensitively in memory. **Step 2**
(`FollowUpQueries`, after step 1 was read): on every DC whose read completed, the unfiltered audit probes of §4.3 and
the failures of *every* account from the `ProxyCheckSources` sources with most of the account's failures (by the
workstation names and addresses as logged, `ProxyCheckRecords` per query) – so that "many accounts failing from it" can
mark a proxy. "Locked now" is judged at the current time (`Now`), never at the window end. A PDC emulator's 4776 copy of
another DC's NTLM failure (same account, workstation and status within 5 s) is counted once. When the run dropped its
oldest records (memory limit) the report says so (`RecordsDropped`) and concludes nothing from what is missing before
the oldest kept record.

**Report:** lockouts (time, DC, caller computer from 4740 `TargetDomainName`), **sources** ranked by failures – caller
computer, IP (4771 `IpAddress`, `::ffff:` stripped), workstation (4776 `Workstation`, 4625 `WorkstationName`/`IpAddress`)
– each with count, first/last time, DCs, failure codes; a timeline; per-DC state; **notes**: empty caller (the client
did not say), a source that looks like a proxy (Exchange/ADFS/NPS/RD Gateway/IIS by a configurable name pattern and
by many accounts failing from it), bad passwords that do not count (the last two of the history – stated as a general
hint), coverage gaps (a DC whose log does not reach the window), **audit evidence** (§4.3). The source computer can be
opened in K1 (*Zdalne akcje*) and checked: **sessions** of the account, **services** and **scheduled tasks** running
as the account (pivot).

### 4.2 Failed logons (`FailedLogonAnalysis`)

**Query plan:** all DCs, `Security` 4625, 4771 (failure), 4776 (failure), 4768 with result 0x6, and 4624/4768
successes for the "success after failures" check; window default 24 h (1 h – 7 days).

**Findings** (severity, source, accounts, counts, first/last, codes): **brute force** (≥ `BruteForceThreshold` failures
for one account from one source), **password spraying** (≥ `SprayAccounts` distinct accounts from one source within
`SprayWindow`, each with ≤ `SprayMaxPerAccount` failures), **account enumeration** (≥ `EnumerationThreshold` unknown-user
failures – `0xC0000064`, Kerberos `0x6` – from one source), **success after failures** (a success for the same account
from the same source after ≥ `SuccessAfterFailures` failures). Summaries: top accounts, top sources, counts per code.
Caveats in the texts: NAT/VPN/proxies merge users, the 4776 workstation is client-supplied. A spray counts accounts with
up to `max(SprayMaxPerAccount, BruteForceThreshold − 1)` failures (no count falls between spraying and brute force); a
span that starts before the previous one ended continues it (one finding per source and period, linear time). The PDC
emulator's 4776 copies of another DC's NTLM failures (`PdcEmulators`) are counted once; 4768 `0x12` (revoked: disabled,
expired, locked – no password checked) is counted but is not a password attempt.

### 4.3 Audit evidence (`AuditEvidence`)

Each analysis declares the events it needs and the audit subcategory behind them (4740 – *User Account Management*
success; 4771 – *Kerberos Authentication Service* failure; 4776 – *Credential Validation* failure; 4625 – *Logon*
failure; 4624 – *Logon* success). After a read, per DC: **seen** (at least one record), **not seen while the log covers
the window** (the subcategory is probably not audited – the UI offers *Pokaż w modelu* for the DC and *Edytuj
ustawienia zabezpieczeń* of the GPO that wins on the Domain Controllers OU), **unknown** (coverage missing). Never stated
as certain. *Not seen* needs a read that would have seen any account's record (`AuditRead`): a read filtered to accounts
(the lockout plan, the success step of the failed logons) proves an event audited, never not audited, so the analyses
run unfiltered probes (`AuditEvidence.Probes`: the event ID with its outcome in the window, one record at most) for the
requirements not seen on a DC; hosts whose read failed or dropped records stay unknown. A log covers the window
(`EventLogCoverage.Covers(from, to)`) when it is enabled, starts at or before `from` and did not stop recording before
`to` (full and set not to overwrite – note `LogFullNotOverwriting`).

## 5. Presentation

- **Dzienniki zdarzeń** (one tab per connection window; *Narzędzia → Dzienniki zdarzeń…*, palette `events.open`):
  sources (all DCs, chosen computers, a computer from its *Stan* page or context menu, `.evtx` files), channel (common
  list + the host's channels), time (presets 1 h / 24 h / 7 dni / własny), IDs, levels, providers, outcome, account /
  SID / IP / workstation (mapped to data conditions per channel), raw XPath, run with per-host progress and cancel,
  merged virtualized timeline (time, host, channel, ID, level, provider, short summary), details (fields with Polish
  explanations of codes and logon types, SIDs and accounts linked to AD objects, message on demand), coverage and
  access per host, saved queries, export CSV/HTML with the personal-data note.
- **Skąd blokada konta – <konto>** (user context menu, account property page, U2 review, palette `events.lockout`):
  the report of 4.1 in the K6 diagnostics layout, *Sprawdź komputer źródłowy* (pivot), refresh.
- **Nieudane logowania** (palette `events.failedLogons`, *Narzędzia*): window, thresholds, findings of 4.2, drill-down
  to records.

## 6. Testing

Core: parser (Polish fixtures, invalid characters, UserData, unnamed data), query XML (escaping, chunking, raw XPath
check), matcher, analyses (scenarios with Polish accounts and computers), codes. Windows project: error
classification and query wiring testable without Windows; real reads only on Windows (CI smoke uses the fake).
Presentation: documents against `FakeEventLogConnector` + `FakeDirectorySession`. App: views, SmokeTest, UI tests.

## 7. As built

(Filled in when the stage is done.)
