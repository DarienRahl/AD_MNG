# AdMng.Directory tests

Two kinds of tests live in this project:

- `Unit/` - no server needed (option validation, user-name parsing, ranged attribute names, schema parsing,
  error translation). Always run.
- `Integration/` - run against a real directory (the Samba lab from `tools/testlab`, or a lab AD) and are
  **skipped** unless `ADMNG_TEST_LDAP_SERVER` is set.

```bash
export ADMNG_TEST_LDAP_SERVER=127.0.0.1
export ADMNG_TEST_LDAP_USER='Administrator@contoso.test' ADMNG_TEST_LDAP_PASSWORD='P@ssw0rd!2026'
dotnet test tests/AdMng.Directory.IntegrationTests
```

Optional overrides (defaults match `tools/testlab`): `ADMNG_TEST_LDAP_HELPDESK_USER`,
`ADMNG_TEST_LDAP_HELPDESK_PASSWORD`, `ADMNG_TEST_LDAP_READER_USER`, `ADMNG_TEST_LDAP_READER_PASSWORD`,
`ADMNG_TEST_LDAP_BASE`, `ADMNG_TEST_LDAP_DOMAIN`, `ADMNG_TEST_LDAP_NETBIOS`.

## Rules

- Tests write **only** below this run's own OU, `OU=AdMngIntegration-<UTC time>-<random>,<domain>`
  (`LabSettings.TestOu`), so concurrent runs on the shared lab (worktrees, CI jobs) never collide. Its description
  names the test process (`[run <host> pid <id> started <time>]`, `Infrastructure/LabRuns.cs`). The assembly fixture
  creates it and removes it at the end. Before that it removes the OUs of runs that are over: the owning process on
  this host ended (an aborted run), or – owner on another host, or the fixed `OU=AdMngIntegration` of earlier
  versions – older than 2 hours; OUs of runs still in progress are left alone. A leftover that cannot be removed is
  reported as a diagnostic message and does not stop the run. Seeded data is only read; the access denied test writes
  the current value back so that even a wrongly permitted write changes nothing.
- Test trees are deleted **client-side** (`Infrastructure/LabCleanup.cs`, also used by
  `AdMng.Watchdog.IntegrationTests`): groups lose their members in one modify each, then every object is deleted on its
  own, deepest first, with a 10-minute request timeout. Samba runs one write transaction at a time (~0.1 s per deleted
  object on the lab) and ends a request after 300 s, so a server-side tree delete of a run OU (two 1600-member groups
  and their 3200 contacts, ~5 minutes) failed with timeLimitExceeded, left the objects behind and blocked every other
  suite meanwhile. A busy lab (timeout, LDAP busy) or a stopped one makes the fixture fail with an explanation.
  Diagnostic messages (removed leftovers, cleanup time): `dotnet test ... -- xUnit.DiagnosticMessages=true`.
- Reads/writes use simple bind over plain LDAP (`AllowInsecureSimpleBind`, lab only); password resets use LDAPS
  with `AllowUntrustedCertificate` (the lab certificate is self-signed).
- Latency assertions live in the non-parallel `Timing` collection.

## Platform notes (Linux / macOS, OpenLDAP based S.DS.P)

- **Untrusted certificates**: `VerifyServerCertificate` is not available. The connector sets OpenLDAP's global
  `LDAP_OPT_X_TLS_REQUIRE_CERT` to `never` only while creating the one connection, restores it and gives the
  handle a new TLS context (`OpenLdapTls`). `LDAPTLS_REQCERT=never` would also work but only when set before
  libldap initializes, and for the whole process.
- **Negotiate with explicit credentials** is not supported by S.DS.P on Linux - use `SimpleBind` over TLS, or
  `CurrentUser` with a Kerberos ticket (`kinit`).
- **One request per connection at a time**: concurrent requests on one handle lose search result codes and
  response controls (paging cookie). The pool serializes requests per connection and opens more connections.
- **Searches use the blocking `SendRequest` on a worker thread**: the asynchronous S.DS.P path reads one LDAP
  message per poll (up to 100 ms apart), which would make large pages take minutes.
- **The blocking call ignores its timeout while the server is silent**: S.DS.P passes a 2 x 32 bit
  `struct timeval` to `ldap_result`, which reads 2 x 64 bit on 64-bit Linux/macOS, so it waits until data
  arrives. `LdapAsync` enforces the request timeout itself (plus 1 s), the pool drops a connection that let a
  request time out, and requests queued on it move to another connection. The abandoned worker thread stays
  blocked until the socket delivers data or fails (`PoolTests` stall the lab through `TcpProxy.StallExisting`).
- **Change notifications** run on S.DS.P's single, process-wide partial-results thread. It polls one subscription
  at a time while holding the lock that `Abort` needs, sleeps 250 ms between polls (at most ~4 deliveries per
  second), and because of the same `timeval` bug a poll of a quiet subscription can block until that container
  changes. Disposing a subscription therefore shuts its socket down first (`OpenLdapTls.TryShutdownSocket`, which
  wakes the poll) and aborts on a separate thread; with several quiet subscriptions open, delivery to the others
  can still be delayed.

## Samba deviations seen in the lab (AD behaves as documented)

- The permissive modify control is ignored for linked attributes (`member`): adding an existing member returns
  entryAlreadyExists, deleting a missing one unwillingToPerform.
- Abandoning a paged search (page size 0 + cookie) does not free the server side result set; Samba keeps at
  most 10 per connection and evicts the least recently used one.
- RootDSE lists no `supportedExtension`, although "Who am I?" works.
