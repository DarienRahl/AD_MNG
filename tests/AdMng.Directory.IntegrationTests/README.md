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

- Tests write **only** below `OU=AdMngIntegration,<domain>`. The assembly fixture creates it (removing leftovers
  of an aborted run first) and removes it with a tree delete at the end. Seeded data is only read; the access
  denied test writes the current value back so that even a wrongly permitted write changes nothing.
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

## Samba deviations seen in the lab (AD behaves as documented)

- The permissive modify control is ignored for linked attributes (`member`): adding an existing member returns
  entryAlreadyExists, deleting a missing one unwillingToPerform.
- Abandoning a paged search (page size 0 + cookie) does not free the server side result set; Samba keeps at
  most 10 per connection and evicts the least recently used one.
- RootDSE lists no `supportedExtension`, although "Who am I?" works.
