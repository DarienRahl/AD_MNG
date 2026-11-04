**English** · [Polski](../../adr/0002-system-directoryservices-protocols.md)

# ADR-0002: AD access through System.DirectoryServices.Protocols

- Status: Accepted
- Date: 2026-10-13

## Context

Available APIs: ADSI (`System.DirectoryServices`, used by ADUC), `System.DirectoryServices.AccountManagement`, the
ActiveDirectory PowerShell module (ADWS, port 9389), and direct LDAP through `System.DirectoryServices.Protocols`
(S.DS.P).

## Decision

All communication with AD goes through S.DS.P (LDAP v3): Negotiate (Kerberos) with signing and encryption, or LDAPS,
asynchronous requests, LDAP controls (paging, VLV + sorting, ASQ, Show Deleted/Recycled, Permissive Modify,
Tree Delete, SD Flags, Change Notification).

## Rationale

- ADSI caches objects (property cache), works synchronously and has COM overhead – one source of ADUC's "sluggishness".
- S.DS.P gives full control over *which* attributes are fetched (only the columns visible on screen) and allows
  results to be streamed in pages – the first rows appear after a few dozen ms.
- AccountManagement is slow and limited to a few classes; ADWS requires a service on the DC and gives no control over
  LDAP.
- S.DS.P also works on Linux, which enables integration tests against a Samba AD controller in CI.

Prototype on the lab (Samba 4.19 AD DC): bind 16 ms, first page of 100 users 65 ms,
428 objects in 174 ms, VLV/ASQ/allowedAttributesEffective work, 20 parallel queries on a single connection.

## Consequences

- Our own implementation of what ADSI does "for free" (type conversions according to the schema, ranged retrieval,
  error mapping).
- No dependency on RSAT on the administrator's workstation.
