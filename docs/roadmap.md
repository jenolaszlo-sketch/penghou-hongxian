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

- The provider-neutral core and SQLite/Siming packages exist in the public
  repository and build as `0.1.0-preview.1` candidates.
- Immutable events, recovery evidence, current-state projections, transactional
  catalog state, decision leases, lifecycle outbox receipts, cross-store
  operation receipts, and forward reconciliation are extracted.
- Provider-qualified external-operation identity prevents collisions between
  execution systems without introducing workflow-engine types.
- The suite passes 39 tests; a standalone example and an isolated packed
  consumer both persist, project, and verify a session event.
- Guyabano integration is intentionally waiting for the first Hongxian package.
  No sibling-repository project reference should be committed as a workaround.
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
- [ ] Add provider conformance tests for session event and projection stores.
- [ ] Define a provider-neutral consistency-audit result covering ledger head,
  projection cursor, catalog version, incomplete operations, and receipts.

## Milestone 2 — Operational catalog and coordination

- [x] Extract the concurrency-safe session catalog behind provider-neutral
  interfaces.
- [x] Model provider-qualified external-operation references instead of bare
  workflow GUIDs.
- [x] Extract renewable decision leases and optimistic concurrency.
- [x] Extract durable lifecycle outbox receipts.
- [x] Extract durable cross-store operation receipts and reconciliation status.
- [x] Keep application-authored recovery explanations outside the kernel.
- [ ] Add conformance tests for catalog, lease, lifecycle receipt, and
  cross-store operation providers.
- [ ] Define bounded handle/cache behavior for providers that keep one ledger
  or database handle per session.

## Milestone 3 — Query and lifecycle surface

These reusable APIs moved from Guyabano's interactive-session backlog. UI and
application policy remain with consumers.

- [ ] Add bounded query APIs for session catalog lookup, paged timeline,
  projection delivery status, pending inputs, pending decisions, active
  incidents, and incomplete operations.
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
- [ ] Decide whether session branching belongs in the generic kernel only after
  selective rerun and a second consumer establish useful semantics.

## Milestone 4 — Package and Guyabano integration

- [ ] Decide whether to multi-target .NET 8 before the first public preview.
- [ ] Publish `Penghou.Hongxian` and `Penghou.Hongxian.Sqlite`
  `0.1.0-preview.1` after CI and packed-consumer validation pass on GitHub.
- [ ] Replace Guyabano's internal session projects with package references.
- [ ] Keep Guyabano event vocabulary, workspace policy, product recovery
  handlers, and Penghou-provider adapters in Guyabano.
- [ ] Add a Guyabano mapping layer for its Zhinu workflow IDs, workspace
  revisions, event vocabulary, and domain recovery explanations.
- [ ] Re-run the complete Guyabano suite and realistic recovery dogfood flow.
- [ ] Prove restart and projection reconstruction after process loss.
- [ ] Remove Guyabano's temporary duplicate kernel implementation only after
  package-backed parity is proven.

## Milestone 5 — Participant collaboration surface

Implement this after the first package-backed Guyabano integration establishes
which collaboration concepts are genuinely reusable. The invariant is:

> Hongxian records collaboration. Applications interpret collaboration.
> Workflow engines control execution.

Initial scope:

- [ ] Represent human, model, workflow-activity, tool, system, and external
  participant attribution through provider-neutral, host-supplied identity
  claims. Hongxian preserves attribution but does not authenticate it.
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
- [ ] Evaluate signed publications, trust policy, visibility/scoping,
  collaboration-specific retention, federation, and export/import formats.
- [ ] Keep proposals and decision candidates distinct from authoritative
  Hongxian decision-coordination records and leases.
- [ ] Keep session collaboration distinct from Cangjie memory: a publication
  records that something was contributed; deliberate promotion determines what
  becomes retained context.

## Milestone 6 — Optional execution adapters

- [ ] Extract a provider-neutral external-event delivery cursor from Guyabano's
  current Zhinu-to-Siming mirror implementation.
- [ ] Consider `Penghou.Hongxian.Zhinu` only if it can map authoritative Zhinu
  receipts/events without importing Guyabano statuses or policy.
- [ ] Keep workflow execution and sequencing optional to Hongxian.
- [ ] Do not introduce a Siming-specific adapter package unless it materially
  improves provider replacement beyond the existing core ledger port.

## Milestone 7 — Second-consumer validation

- [ ] Build a Baize media-generation/batching profile using opaque external
  operation and artifact references.
- [ ] Resume partial batches without regenerating acknowledged outputs.
- [ ] Record variants, selection, retries, partial success, and media lineage
  without adding media-specific types or bytes to Hongxian.
- [ ] Confirm that application-defined event payloads and artifact references
  require no core API changes.

## Milestone 8 — Package quality and stability

- [x] Add CI build, format, test, pack, and isolated-consumer verification.
- [x] Add trusted-publishing workflow consistent with the Penghou ecosystem.
- [ ] Add complete public API documentation before leaving preview.
- [ ] Add package compatibility validation against the previous release after
  the first preview exists.
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
- Should participant attribution be stored entirely as an immutable publication
  snapshot, or combine a stable subject reference with a historical snapshot?
- Which publication kinds need generic projection support beyond application
  queries, without making Hongxian interpret their domain meaning?
