**English** · [Polski](../../adr/0004-jeden-potok-zmian.md)

# ADR-0004: One change pipeline for single and bulk edits

- Status: Accepted
- Date: 2026-10-13

## Context

Bulk editing is a key requirement. ADUC allows editing multiple objects only for a few fields, with no preview, no
report and no way to roll back. Commercial tools (ManageEngine ADManager Plus, Adaxes) use the model
"select → configure → preview → execute → report".

## Decision

Every change – including a change of a single field in a properties window – goes through the same pipeline:

```
BatchOperation[]  →  IChangePlanner  →  ChangePlan (diff: old → new value, validation)
                  →  preview / confirmation  →  IBatchExecutor  →  BatchResult  →  session log
                  →  (optionally) IRollbackPlanner → rollback plan
```

- The planner reads the **current** values from the DC right before the preview, skips changes with no effect, validates
  against the schema and the operator's effective permissions (`allowedAttributesEffective`), checks uniqueness
  (sAMAccountName, UPN, mail).
- The executor applies optimistic concurrency (delete of the old + add of the new value in one atomic modify), so a
  change made by someone else after the preview is not overwritten.
- Per-object results, retrying of transient errors, cancellation, CSV export, a PowerShell equivalent, rollback.

## Consequences

- One implementation of validation, audit and error handling; single edits get the preview and rollback "for free".
- Slightly higher cost of simple changes (read before write) – negligible for a single object.
