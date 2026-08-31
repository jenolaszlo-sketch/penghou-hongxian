# Penghou.Hongxian roadmap

## Goal

Provide a reusable durable-session kernel for long-running human and automated
work without importing application policy, workspace layout, artifact meaning,
or dependencies on a particular execution engine.

This is the source of truth for reusable session identity, lifecycle, event,
projection, decision, recovery, reconciliation, and persistence work. Guyabano
tracks only its application profile and package integration.

## Current state

Last reviewed: **2026-08-31**

- `Penghou.Hongxian` and `Penghou.Hongxian.Sqlite` `0.1.0-preview.1` are
  published on NuGet.
- Immutable events, recovery evidence, current-state projections, transactional
  catalog state, decision leases, lifecycle outbox receipts, cross-store
  operation receipts, and forward reconciliation are extracted.
- Provider-qualified external-operation identity prevents collisions between
  execution systems without introducing workflow-engine types.
- The suite passes 39 tests; a standalone example and an isolated packed
  consumer both persist, project, and verify a session event.
- A pre-integration review found no vulnerable direct or transitive packages
  and all 39 tests pass. It also identified integrity, fencing, provider-neutral
  contract, schema-evolution, and usability work to complete in preview 2 before
  Guyabano replaces its internal kernel.
- A provider-neutral participant collaboration surface is accepted as
  post-integration work. It will reuse Hongxian's event, reference, projection,
  and idempotency foundations rather than introduce a parallel message store.

## Ownership boundary

Hongxian owns continuity and correlation: session identity and lifecycle,
opaque revision lineage, external-operation references, decision coordination,
incidents and recovery records, projections, reconciliation contracts, audit
queries, and operational-catalog abstractions.

Hongxian does not own workflow scheduling, application recovery policy,
workspace mutation, generated-file semantics, model requests, code graphs,
memory promotion, artifact bytes, actor authentication, or authorization.
Siming remains authoritative for cryptographic ledger format and verification.

## Milestone 0 — Repository and boundary

- [x] Create the public package and solution structure.
- [x] Record ownership boundaries and security claims.
- [x] Preserve the first proven event, recovery, and projection tests.
- [x] Add architecture documentation and a minimal independent sample.
- [x] Add packed-consumer validation.

## Milestone 1 — Durable event and projection kernel

- [x] Extract session identity and immutable event envelopes.
- [x] Extract incidents, recovery plans, attempts, and verified receipts.
- [x] Extract current-state and timeline projection contracts.
- [x] Compose one independent Siming SQLite ledger per session.
- [x] Keep SQLite current-state projections rebuildable from the ledger.
- [x] Separate committed ledger state from rebuildable projection delivery and
  expose lag diagnostics.
- [x] Treat ledger commit time as authoritative while retaining bounded
  caller-supplied occurrence-time claims.
- [x] Remove assumptions that an external operation is a workflow or that a
  revision is a source-code workspace.

## Milestone 2 — Operational catalog and coordination

- [x] Extract the concurrency-safe session catalog behind provider-neutral
  interfaces.
- [x] Model provider-qualified external-operation references instead of bare
  workflow GUIDs.
- [x] Extract renewable decision leases and optimistic concurrency.
- [x] Extract durable lifecycle outbox receipts.
- [x] Extract durable cross-store operation receipts and reconciliation status.
- [x] Keep application-authored recovery explanations outside the kernel.
- [ ] Define bounded handle/cache behavior for providers that keep one ledger
  or database handle per session.

## Milestone 3 — Preview 2 integrity, neutrality, and usability hardening

Complete this breaking-change batch before Guyabano consumes Hongxian. Preview
1 proved packaging and the extracted behavior; preview 2 should establish the
contract that applications integrate against.

Integrity and concurrency:

- [ ] Make projection rebuild consume verified ledger history or a verified
  head contract. Validate chain continuity as well as sequence continuity, and
  never allow rebuild/application state to replace an authoritative committed
  head hash at the same sequence.
- [ ] Turn decision leases into a genuine fencing contract: expose fencing
  token and expiry, signal lease loss immediately, allow ownership assertion at
  the protected commit, and test renewal failure and stale-holder rejection.
- [ ] Add a transactional evidence outbox for cross-store operation creation,
  participant receipts, and transitions, plus a reusable idempotent dispatcher
  into the session ledger. Operational SQLite rows must not be the only audit
  evidence.
- [ ] Add versioned SQLite migrations with an explicit schema version,
  serialized upgrade, unsupported-newer-schema rejection, reopen/upgrade tests,
  and documented backup responsibility.
- [ ] Validate default/empty value-type IDs, enum values, timestamps, monotonic
  transition time, and bounded strings at every public persistence boundary.
  Use `TimeProvider` consistently for library-authored audit and cache times.
- [ ] Define a provider-neutral consistency-audit result covering verified
  ledger head, projection cursor, catalog version, incomplete operations,
  evidence outboxes, leases, and receipts.

Provider-neutral contract cleanup:

- [ ] Replace GUID-only external operation identity with a bounded opaque
  identity while retaining provider/system qualification and ordinal identity
  semantics.
- [ ] Remove Guyabano-shaped operation phases such as `RevisionCommitted` and
  `Published`. Keep only generic lifecycle/health semantics or make application
  phases opaque and validated.
- [ ] Replace `RefreshPreview`, `AbandonCandidate`, `SafeRevision`, and other
  code-generation recovery vocabulary with application-defined action and
  resource references.
- [ ] Move revision-promotion commit policy out of the generic kernel and into a
  Guyabano profile or adapter. Hongxian should expose reusable transactional
  receipt/outbox mechanics rather than one application's mutation.
- [ ] Separate recovery recording from application recovery execution. A helper
  may wrap an application-supplied handler, but the core must not choose,
  schedule, or claim authority over the action.
- [ ] Replace hard-coded application-style reconciliation instructions with
  structured health, incomplete participant, and suggested-action-code data;
  applications own user-facing explanations.

Consumer usability:

- [ ] Replace the free-form actor string with a compact immutable participant
  attribution reference containing a small kind, provider, stable opaque
  subject, and optional display-name snapshot. These remain host-supplied
  claims; metadata or capability claims must never grant authorization.
- [ ] Add canonical `AppendAsync<T>` and `JsonElement` payload APIs plus typed
  read helpers. Keep retention and idempotency semantics independent of the
  original CLR type and document the identity behavior of omitted payloads.
- [ ] Add typed public exceptions/results for idempotency conflict, stale
  version, lost lease, projection gap/lag, corruption, and unsupported schema;
  consumers must not parse exception messages.
- [ ] Add `TryParse`, JSON converters, and formatting support for public IDs and
  external references.
- [ ] Add a small SQLite composition root/options model that consistently opens
  catalog, per-session ledgers, projections, leases, operations, and evidence
  dispatchers without hiding provider interfaces.
- [ ] Expose projection-delivery outcome or diagnostics from append so callers
  can observe committed-but-lagging state without relying on trace output.
- [ ] Expose typed, queryable health results for ledger verification,
  projection lag, evidence-outbox delivery, incomplete operations, failed
  participants, lease ownership/loss, schema compatibility, and required
  reconciliation. Logs and traces remain diagnostics, not the operator API.
- [ ] Centralize bounded input limits for actors, event/application kinds,
  external identities, references, metadata, reasons, and receipts across both
  ledger and operational stores.

Package and contract quality:

- [ ] Add provider conformance suites for event, projection, catalog, lease,
  lifecycle/outbox, and cross-store operation implementations.
- [ ] Multi-target .NET 8 if UUIDv7 compatibility can be supplied without
  weakening ordering or identity semantics.
- [ ] Add public API analyzer shipped/unshipped baselines and enable package
  compatibility validation against preview 1.

## Milestone 4 — Query, lifecycle, and portability surface

These reusable APIs moved from Guyabano's interactive-session backlog. UI and
application policy remain with consumers.

- [ ] Add bounded query APIs for session catalog lookup, paged timeline,
  projection delivery status, pending inputs, pending decisions, active
  incidents, and incomplete operations.
- [ ] Add immutable named checkpoints that bind a session ledger sequence and
  verified head hash to application-defined kind/name, actor, causation, and
  bounded external resource identities, revisions, and digests. Hongxian
  records checkpoints but never restores external state.
- [ ] Add bounded as-of projection at a verified ledger sequence or named
  checkpoint. Begin with deterministic streaming replay; introduce cached
  snapshots only after measured interactive workloads justify them.
- [ ] Add an optional indexed `SessionRelation` with source, target,
  application-defined kind, actor, time, and causation. Relations support
  discovery but do not imply lifecycle propagation, inherited authorization,
  cascading deletion, ownership, or acyclic parent/child semantics.
- [ ] Define generic session metadata and lifecycle operations for name, rename,
  archive, unarchive, and resume discovery with optimistic concurrency.
- [ ] Define durable input-request lifecycle events and projections for request,
  response, cancellation, timeout, and closure. Delivery to a workflow signal
  remains an optional adapter concern.
- [ ] Review operator-state vocabulary and distinguish healthy, warning,
  awaiting-input/decision, reconciliation-required, and corrupt conditions
  without embedding application-specific severity policy.
- [ ] Document payload retention, ledger deletion, backup, checkpoint anchoring,
  and projection-rebuild responsibilities.
- [ ] After schema/version contracts stabilize, add verified single-session
  export/import containing the authoritative ledger, versioned manifest,
  catalog metadata, relations, checkpoints, and bounded external references.
  Projections are rebuildable and need not be authoritative export content;
  import verifies history, rejects ambiguous ID collisions, and never silently
  merges two ledgers.
- [ ] Decide whether session branching belongs in the generic kernel only after
  selective rerun and a second consumer establish useful semantics.

## Milestone 5 — Package and Guyabano integration

- [x] Publish `Penghou.Hongxian` and `Penghou.Hongxian.Sqlite`
  `0.1.0-preview.1` after CI and packed-consumer validation pass on GitHub.
- [ ] Publish preview 2 only after the Milestone 3 integrity and contract gates
  pass locally, in CI, and through the isolated packed consumer.
- [ ] Replace Guyabano's internal session projects with package references.
- [ ] Keep Guyabano event vocabulary, workspace policy, product recovery
  handlers, and Penghou-provider adapters in Guyabano.
- [ ] Add a Guyabano mapping layer for its Zhinu workflow IDs, workspace
  revisions, event vocabulary, and domain recovery explanations.
- [ ] Re-run the complete Guyabano suite and realistic recovery dogfood flow.
- [ ] Prove restart and projection reconstruction after process loss.
- [ ] Remove Guyabano's temporary duplicate kernel implementation only after
  package-backed parity is proven.

## Milestone 6 — Participant collaboration surface

Implement this after the first package-backed Guyabano integration establishes
which collaboration concepts are genuinely reusable. The invariant is:

> Hongxian records collaboration. Applications interpret collaboration.
> Workflow engines control execution.

Initial scope:

- [ ] Reuse the preview 2 participant attribution reference for human, model,
  workflow-activity, tool, system, and external publications. Collaboration
  must not introduce a second actor directory or identity format.
- [ ] Add immutable, application-defined session publications as typed payloads
  over the existing session event and Siming append path—not a second ledger,
  ordering model, or transaction boundary.
- [ ] Support bounded subject, text/structured content, opaque external
  references, provenance references, `RespondsTo`, and `Supersedes`.
- [ ] Enforce explicit UTF-8 byte, JSON depth, subject, reference-count,
  relation-count, and metadata limits at append time.
- [ ] Reuse canonical event idempotency: identical operation identity and
  content return the original publication; conflicting reuse is rejected.
- [ ] Require publication relationships to target the same session. Represent
  cross-session or external relationships with opaque references until
  federation has concrete semantics.
- [ ] Add bounded cursor queries by participant, application-defined kind,
  subject, relationship, external reference, and ledger sequence/checkpoint.
- [ ] Add a rebuildable effective-head projection for supersession while
  retaining complete history. Supersession means a newer contribution, not an
  accepted fact, and concurrent publications may leave multiple heads.
- [ ] Validate observations, questions, proposals, review findings, and evidence
  in Guyabano without adding Guyabano or provider types to Hongxian.
- [ ] Prove that publishing an entry cannot assign work, approve or reject a
  decision, mutate workflow state, invoke recovery, or grant authority to
  another participant.

Later, driven by concrete consumers:

- [ ] Add `Supports` and `Contradicts`, graph traversal, participant activity
  projections, richer provenance queries, and collaboration checkpoints.
- [ ] Evaluate signed publications, trust policy, visibility/scoping, and
  federation only when concrete consumers require them.
- [ ] Keep proposals and decision candidates distinct from authoritative
  Hongxian decision-coordination records and leases.
- [ ] Keep session collaboration distinct from Cangjie memory: a publication
  records that something was contributed; deliberate promotion determines what
  becomes retained context.

## Milestone 7 — Optional execution adapters

- [ ] Extract a provider-neutral external-event delivery cursor from Guyabano's
  current Zhinu-to-Siming mirror implementation.
- [ ] Consider `Penghou.Hongxian.Zhinu` only if it can map authoritative Zhinu
  receipts/events without importing Guyabano statuses or policy.
- [ ] Keep workflow execution and sequencing optional to Hongxian.
- [ ] Do not introduce a Siming-specific adapter package unless it materially
  improves provider replacement beyond the existing core ledger port.

## Milestone 8 — Second-consumer validation

- [ ] Build a Baize media-generation/batching profile using opaque external
  operation and artifact references.
- [ ] Resume partial batches without regenerating acknowledged outputs.
- [ ] Record variants, selection, retries, partial success, and media lineage
  without adding media-specific types or bytes to Hongxian.
- [ ] Confirm that application-defined event payloads and artifact references
  require no core API changes.

## Milestone 9 — Package quality and stability

- [x] Add CI build, format, test, pack, and isolated-consumer verification.
- [x] Add trusted-publishing workflow consistent with the Penghou ecosystem.
- [ ] Add complete public API documentation before leaving preview.
- [ ] Maintain package compatibility validation against the previous release;
  intentional preview breaks require explicit baselines and release notes.
- [ ] Add a changelog and release checklist.

Do not graduate from preview until:

- at least two substantially different applications use the kernel;
- public contracts contain no Guyabano or provider-specific types;
- replacing SQLite or an external-execution adapter does not change session
  policy;
- process-loss, idempotency, concurrency, projection rebuild, retention, and
  tamper-detection semantics are documented and tested;
- the public query/lifecycle surface has survived real Guyabano use;
- package compatibility has been checked against at least one prior release.

## Open design decisions

- Can one session span multiple logical application contexts or resources, or
  should callers correlate multiple sessions?
- What generic metadata is safe to place in the operational catalog rather than
  immutable event history?
- Which retention guarantees can Hongxian express without claiming authority
  over Siming ledgers, application artifacts, or backups?
- Is a trusted checkpoint reference sufficient for the core, with signature and
  anchoring policy left to hosts?
- What branching semantics remain useful outside code-generation workflows?
- Which publication kinds need generic projection support beyond application
  queries, without making Hongxian interpret their domain meaning?
