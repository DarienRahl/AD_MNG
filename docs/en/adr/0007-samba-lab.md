**English** · [Polski](../../adr/0007-laboratorium-samba.md)

# ADR-0007: Test lab on a Samba AD DC

- Status: Accepted
- Date: 2026-10-13

## Decision

We run integration tests of the LDAP layer on a disposable Samba AD DC (`tools/testlab`) with deterministic data
imitating a Polish company (OUs by departments and locations, Polish characters in names, locked-out accounts,
helpdesk delegation). The same script runs in CI (Ubuntu).

## Consequences

- Integration tests without access to production AD.
- Samba differs from Windows AD in details (e.g. no 415 display specifiers, no Exchange schema) – features depending
  on these differences require additional verification on a Windows Server lab before release.
