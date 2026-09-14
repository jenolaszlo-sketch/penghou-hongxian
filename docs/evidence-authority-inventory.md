# Evidence authority inventory

## Status

Phase 0 working inventory, reviewed **2026-09-13**. This classifies the current
preview-2 implementation before experience APIs change public contracts.

## Classification rules

- **Authoritative evidence** is append-only history whose loss cannot be repaired
  from another Hongxian store.
- **Derived state** is disposable and must be reproducible from a declared,
  verified evidence position.
- **Operational coordination** protects live work or reliable delivery. It may
  be mutable, but is not historical truth and may require backup until all
  consequential transitions are durably evidenced.
- **External authority reference** identifies state owned elsewhere. Hongxian
  preserves the reference and receipt but does not become authoritative for the
  referenced state.

## Current stores

| Store or table | Classification | Rebuildable today? | Required treatment |
| --- | --- | --- | --- |
| Per-session Siming ledger | Authoritative evidence | No | Verify, back up, checkpoint/archive; never roll back after another store fails |
| `session_projections` | Derived state | Yes, from `VerifiedSessionHistory` | Safe to delete and rebuild after verifying the source head |
| `session_projection_delivery` | Operational diagnostic cursor | Partly, by comparing projection and ledger | May be reset/recomputed; failures remain useful diagnostic evidence |
| `sessions` catalog row | Operational routing and discovery | Not fully by a supported API | Back up; keep until a ledger-driven catalog rebuild contract exists |
| `session_external_operations` | Operational lookup index plus external references | Evidence is emitted, but no supported rebuild exists | Back up; external system remains authoritative for operation state |
| `session_decision_leases` | Ephemeral operational coordination | No and not required after expiry/restart policy | Never use as historical evidence; Zhinu fencing still wins for Zhinu work |
| `session_decision_fence_counters` | Durable coordination safety state | No | Back up; loss could permit token reuse and stale-owner ambiguity |
| `session_lifecycle_receipts` | Transactional evidence outbox | Not until delivered; delivery state is operational | Back up and reconcile forward; do not delete an undelivered receipt |
| `cross_store_operations` | Durable saga coordination | Not fully | Back up; state summarizes append-only transitions but is not the session ledger |
| `cross_store_participant_receipts` | Durable external-authority receipts | Evidence is emitted, but no supported rebuild exists | Back up; preserve provider-owned before/after identities and result hash |
| `cross_store_operation_transitions` | Append-only operational transition history | Evidence is emitted, but no supported rebuild exists | Back up; never rewrite failed or superseded transitions |
| `cross_store_evidence_outbox` | Transactional evidence outbox | Not until delivered | Back up and dispatch idempotently; reconcile partial success forward |
| `hongxian_schema_versions` | Operational compatibility metadata | Recreated only with its owning database | Back up with non-derived databases; projections may recreate it during rebuild |

## Current contract fields

### `SessionEvent` and Siming ledger entry

| Field | Authority and interpretation |
| --- | --- |
| `Sequence`, `PreviousHash`, `Hash`, ledger identity | Authoritative integrity and ordering assigned by Siming |
| `CommittedAt` | Authoritative ledger commit clock; not necessarily real-world occurrence time |
| `EventId`, `SessionId` | Durable evidence and stream identities |
| `Participant` | Immutable source attribution claim supplied by the host; not authentication |
| `EventType` | Bounded application or well-known classification, not proof of payload meaning |
| `OccurredAt` | Source/host claim subject to future-skew validation |
| `CausationId`, `CorrelationId` | Evidence relationships; absence does not imply absence of causation |
| `CrossSystemRefs` | Bounded external-authority references; referenced state is owned elsewhere |
| `Payload`, `PayloadJson`, `PayloadDigest` | Evidence content or identity according to retention contract |
| `PayloadSchema` | Application-owned interpretation/version contract |
| `PayloadSensitivity`, `PayloadRetention` | Capture-time disclosure and retention decisions |
| `IdempotencyKey` | Retry identity within the session; not an ordering or concurrency token |

### Catalog and coordination records

- `Session.ContextId`, `ResourceId`, `CreatedAt`, and `Version` are operational
  catalog state. `SessionCreated` evidence carries the initial values, but no
  public verified catalog-rebuild path currently exists.
- `Session.CurrentRevision` is an operational cached reference to an externally
  meaningful resource revision. `RevisionAccepted` evidence preserves the
  transition, but Hongxian does not validate or own the referenced content.
  The method name `UpdateRevisionAsync` remains boundary debt to revisit after
  an application-neutral lifecycle/query design is proven.
- `ExternalOperationReference` is an opaque external-authority identity.
- Decision leases and fencing counters authorize only the Hongxian coordination
  boundary. Their acquisition, expiration, and release are mirrored through a
  transactional outbox as evidence.
- Cross-store operation current state is a summary of its transition and
  participant records. It coordinates forward recovery and does not create an
  atomic transaction across authorities.

## Existing append and projection path

```text
direct event append
    -> Siming commit (authoritative)
    -> best-effort timeline projection apply
    -> Applied or Lagging delivery result

catalog/operation mutation
    -> local SQLite transaction + evidence outbox record
    -> idempotent dispatcher append to Siming
    -> mark outbox delivery
    -> forward reconciliation after partial success
```

The experience projector must consume verified ledger history or a bounded
verified page contract. It must not subscribe only to successful timeline
projection delivery, because that would make one derived view the source of
another.

## Gaps to close before experience projection

1. The envelope identifies event and payload kinds but does not explicitly state
   whether content is an assertion, observation, measurement, decision, receipt,
   or diagnostic.
2. Source attribution exists, but verification/corroboration is not modelled as
   a first-class append-only relationship.
3. `SessionLedgerHead` is sufficient for one stream; a project-wide experience
   projection needs a canonical set of independently verified stream heads, not
   a fabricated global sequence.
4. `ISessionProjectionStore` models one timeline/current-state projection. It
   has no projector identity, schema/policy version, provider capabilities, or
   multi-stream checkpoint.
5. Catalog and cross-store data emit evidence through outboxes, but Hongxian has
   no supported rebuild proving those mutable stores can be discarded.
6. Projection failure diagnostics are typed, but consequential experience
   projection gaps also need append-only incident/recovery evidence and an
   operator-friendly reconciliation path.
7. No portable recall contract currently binds results to evidence identities,
   provider/checkpoint, retrieval policy, bounds, or the context actually used.

## Phase 1 evidence-semantics decision

Implemented by envelope v3 on **2026-09-13**.

| Concept | Proposed shape | Rule |
| --- | --- | --- |
| Evidence nature | Bounded opaque value with well-known `assertion`, `observation`, `measurement`, `decision`, `receipt`, and `diagnostic` values | Extensible by applications; unknown valid values survive round trip |
| Capture basis | Bounded opaque value such as `participant-claim`, `authority-receipt`, `direct-observation`, or `derived` | Describes why it was recorded, not whether it is universally true |
| Verification at capture | Optional bounded disposition plus verifier/source reference | Immutable snapshot of capture-time knowledge only |
| Later verification | New evidence referencing the original event | Never mutate or relabel an existing envelope |
| Confidence | Optional value plus required domain/scale qualifier | Similarity and model probability are not factual confidence |
| Derivation | Policy/model/provider/version and source evidence references | Required for summaries, embeddings, inferred relations, and aggregates |
| External authority | Provider-qualified opaque reference and optional immutable revision/digest | Hongxian records the receipt; the external system owns the state |

### Resolved questions

- Evidence nature is envelope metadata in v3 so generic projectors can classify
  evidence without understanding application payloads.
- Capture disposition is immutable. Later evaluation is a new causally linked
  event and never a mutation of the original.
- The first implementation links one evaluation through the existing event
  causation identity. Bounded multi-evidence/checkpoint evaluation remains a
  projection-model decision for Phase 2/3.
- Confidence remains outside core until a second domain establishes compatible,
  qualified semantics.

## Backup and deletion summary

- Back up the per-session Siming ledgers, catalog, operation database, pending
  outboxes, and fencing counters.
- The current projection database is disposable only after its source ledger
  verifies and rebuild has been tested.
- A future LatticeDB/Neo4j/remote experience projection is always disposable by
  contract, even if retaining it is operationally convenient.
- Do not promise that deleting one session ledger is harmless merely because a
  project-wide projection still contains derived records from it.
