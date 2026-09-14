# LatticeDB experience-provider schema

## Status

Phase 4 schema contract, version 1. This is a provider-private physical mapping
of Hongxian's portable experience model. It is disposable and rebuildable from
verified Siming evidence; it is never an evidence authority.

## Identity and ownership

- LatticeDB node and edge identifiers are storage-local implementation details.
- Portable entity, relation, and projection identifiers remain authoritative
  within the derived model.
- Every lookup key is namespaced by projection as
  `<projection-id>/<portable-record-id>`.
- A provider instance owns and disposes databases it opens. Database location,
  creation behavior, access mode, WAL, and file locking are explicit options.
- Checkpoints live in the same LatticeDB database as projection records so a
  provider can commit a model write atomically. They are still derived state.

## Labels and edge type

| Record | LatticeDB shape | Label/type |
| --- | --- | --- |
| entity | node | `HxEntity` plus projection-scoped `HxEntity<projection-id-N>` |
| relation | directed edge | `HxRelation` |
| checkpoint | node | `HxCheckpoint` |

Provider-owned names use the `hx_` prefix. Version 1 uses:

| Property | Applies to | Meaning |
| --- | --- | --- |
| `hx_key` | all | projection-scoped stable lookup key |
| `hx_id` | entity/relation | portable UUID in canonical `D` form |
| `hx_projection` | all | portable projection UUID |
| `hx_kind` | entity/relation | application-defined portable kind |
| `hx_payload` | all | complete canonical portable record JSON |
| `hx_text` | entity | bounded deterministic lexical-search text |
| `hx_version` | checkpoint | optimistic checkpoint version |

Equality indexes are created for `hx_key` on entity and checkpoint nodes, and
for `hx_key` on relation edges. Each projection-scoped entity label has a
full-text index on `hx_text`; this makes lexical isolation exact even when one
database carries several projections. Queries never depend on a provider scan
when an indexed lookup exists.

## Portable payloads

`hx_payload` contains the complete portable entity, relation, or checkpoint.
Its canonical UTF-8 JSON form is the equality value used for replay detection:

- missing key: apply the new record;
- same key and byte-equivalent canonical payload: `AlreadyPresent`;
- same key and different canonical payload: `Conflict`.

Duplicating selected fields as native properties is an index optimization, not
a second source of truth. Reads reconstruct the portable result from the
payload and validate its key and projection before returning it.

`hx_text` is derived deterministically from string-valued portable properties,
ordered by ordinal property name. It is bounded by the portable property-set
limit and can be regenerated without changing entity identity.

## Query semantics

- Exact lookup is scoped to one projection and portable entity ID.
- Traversal follows only `HxRelation` edges whose stored portable relation
  belongs to the requested projection. Depth and result counts are bounded by
  the portable request.
- Lexical search uses LatticeDB full-text/BM25 ranking and applies the portable
  item and byte budgets before returning records.
- Version 1 does not provide historical `AsOf` reads. Such a request returns an
  explicit unsupported-capability result; it never silently reads current data.

## Deletion and rebuild

Deleting a projection removes its relation edges, entity nodes, and checkpoint
node. It does not access Siming ledgers, Hongxian's SQLite operational state, or
another projection. Replaying the same verified evidence and projector version
must recreate equivalent portable records and query results at the same
checkpoint.

## Evolution

Physical schema changes require a new provider schema version and an explicit
rebuild or migration decision. Because this store is derived, delete-and-replay
is the default upgrade path. Historical Siming evidence is never rewritten to
match a provider schema.
