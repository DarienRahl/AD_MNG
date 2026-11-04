**English** · [Polski](../../adr/0005-sesja-przypieta-do-dc.md)

# ADR-0005: Session pinned to a single domain controller

- Status: Accepted
- Date: 2026-10-13

## Context

AD replicates changes with a delay. If a write goes to DC1 and the refresh to DC2, the operator sees stale data and
thinks the change did not succeed (a common problem with tools that use a random DC).

## Decision

After logon a single DC is chosen (the DC locator with site awareness, or a manual choice). All reads and writes of the
session – including bulk operations – go to that DC through a pool of several connections. The DC name is visible in
the status bar; the operator can change the DC ("Change Domain Controller..." as in ADUC).

## Consequences

- A consistent "save → refresh" picture.
- When a DC fails, the session reports an error and offers to reconnect (without silently switching to another DC).
- "Change Domain Controller..." (Phase 1, October 2026) does not switch the existing session: it opens a new session
  on the chosen DC with the current credentials and replaces the old one only after verification (same domain,
  different DC). Windows opened earlier stay on the previous session and do not write – the "one session = one DC" rule
  applies without exceptions ([dev/phase1-changedc.md](../../dev/phase1-changedc.md)).
