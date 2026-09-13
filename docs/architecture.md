# Architecture

## Responsibility

Hongxian models continuity across evolving human and automated work. A session
is an evidence-stream and consistency boundary; its event ledger is the
authoritative record of accepted assertions and its projections are disposable
read models.

The accepted target architecture is recorded in the
[architecture decision records](decisions/README.md). In particular, a ledger
is authoritative for the integrity and ordering of accepted evidence, not proof
that every source assertion is factually correct.

```text
application policy
        |
        v
Penghou.Hongxian contracts
        |
        +--- immutable event port ---> Penghou.Siming
        |
        +--- projection port --------> replaceable experience/read providers
        |
        +--- optional adapters ------> workflow or execution systems
```

The core accepts application-defined event types and opaque cross-system
references. Built-in event names support common session behavior, but callers
may define their own vocabulary without changing the kernel.

## Authoritative and derived state

- Siming event ledgers are authoritative, ordered, append-only evidence.
- SQLite and future LatticeDB, Neo4j, or remote projections are derived state
  and can be rebuilt from intact verified evidence.
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

## Cross-authority integration

Marang may adapt typed supervised-work, workflow run/epoch, node/generation,
checkpoint, and intervention concepts onto Hongxian's opaque references and
application-defined events. Hongxian supplies session continuity, evidence,
leases, and reconciliation; it does not define or execute those concepts.

The evidence outbox and cross-store operation contracts are an idempotent,
forward-only saga boundary. They cannot transactionally commit Zhinu's
separate database. A Zhinu adapter must persist or acknowledge the external
handle/receipt independently, preserve Zhinu as authoritative for workflow
state and fencing, and reconcile incomplete delivery forward. A Hongxian lease
cannot override a Zhinu fencing decision, and the integration must not claim a
distributed transaction.

Reusable indexed correlation queries and verified as-of/re-entry projections
remain planned portability work; until those APIs exist, consumers should use
bounded event pages and rebuildable projections rather than scan the ledger as
an implicit query contract.

The portable boundary describes evidence, projection position, capabilities,
and bounded recall—not a generic graph database API. Provider-specific query
languages and administration remain on provider-specific surfaces.

## Failure model

A successful append followed by projection failure is projection lag, not a
failed append. Callers must not retry the append as though nothing committed.
Recovery and reconciliation are forward-only: they add evidence and verified
receipts instead of rolling back immutable session or external execution
history.
