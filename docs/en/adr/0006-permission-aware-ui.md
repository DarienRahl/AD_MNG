**English** · [Polski](../../adr/0006-ui-swiadome-uprawnien.md)

# ADR-0006: A permission-aware interface

- Status: Accepted
- Date: 2026-10-13

## Context

The application is used with credentials of different permission levels (helpdesk with delegation on selected OUs,
administrators). ADUC shows all fields as editable and reports "Access denied" only on save.

## Decision

When an object is opened, the constructed attributes `allowedAttributesEffective`,
`allowedChildClassesEffective` and `sDRightsEffective` are fetched. Fields the operator cannot change are read-only
(with a hint explaining why), and unavailable actions (e.g. creating objects in an OU) are disabled. The bulk change
planner rejects disallowed changes before execution. The final decision is always made by the server.

## Consequences

- One additional base-scope read per opened object (a few ms).
- Extended rights (e.g. password reset) are not visible in these attributes – for them the UI lets the operator try and
  explains the denial clearly.
