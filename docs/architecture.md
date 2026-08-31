# Architecture

## Responsibility

Hongxian models continuity across evolving human and automated work. A session
is the correlation boundary; its event ledger is authoritative history and its
projections are disposable read models.

```text
application policy
        |
        v
Penghou.Hongxian contracts
        |
        +--- immutable event port ---> Penghou.Siming
        |
        +--- projection port --------> SQLite read models
        |
        +--- optional adapters ------> workflow or execution systems
```

The core accepts application-defined event types and opaque cross-system
references. Built-in event names support common session behavior, but callers
may define their own vocabulary without changing the kernel.

## Authoritative and derived state

- Siming event ledgers are authoritative, ordered, append-only evidence.
- SQLite projections are derived state and can be rebuilt from an intact
  ledger.
- `OccurredAt` is a caller claim; ledger `CommittedAt` is the authoritative
  audit clock.
- Idempotency keys make ambiguous append retries safe within one session.
- Recovery appends attempts and verified receipts. It never erases the failed
  action that made recovery necessary.

## Provider boundary

Hongxian does not require a workflow engine. External executions are correlated
through IDs and references supplied by the application. An optional adapter may
mirror external history, but the external system remains authoritative for its
own execution state.

Large artifacts and domain objects remain in their owning stores. Hongxian
should retain stable identity, content digest, media type, location, producer,
and lineage references rather than payload bytes.

## Failure model

A successful append followed by projection failure is projection lag, not a
failed append. Callers must not retry the append as though nothing committed.
Recovery and reconciliation are forward-only: they add evidence and verified
receipts instead of rolling back immutable session or external execution
history.
