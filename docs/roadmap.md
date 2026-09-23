# Penghou.Hongxian roadmap

## Goal

Provide the temporal evidence and experience layer for long-running human and
automated work. Hongxian preserves what happened in verifiable session streams,
projects disposable experience views, and offers bounded evidence-bound recall
without importing application policy or becoming a workflow, planning, model,
artifact, or database engine.

This is the source of truth for reusable session identity, lifecycle, event,
projection, decision, recovery, reconciliation, and persistence work. Guyabano
tracks only its application profile and package integration.

## Current state

Last reviewed: **2026-09-14**

- `Penghou.Hongxian` and `Penghou.Hongxian.Sqlite` `0.1.0-preview.2` are
  published on NuGet.
- Preview 2's breaking consumer contract, SQLite composition,
  projection-delivery diagnostics, consistency audit, in-repository provider
  conformance reference suite, and package compatibility validation passed the
  release gates.
- Immutable events, recovery evidence, current-state projections, transactional
  catalog state, decision leases, lifecycle outbox receipts, cross-store
  operation receipts, and forward reconciliation are extracted.
- Provider-qualified external-operation identity prevents collisions between
  execution systems without introducing workflow-engine types.
- The current experience checkpoint passes 128 tests, including a shared
  internal experience-provider conformance suite run against in-memory,
  LatticeDB, and fake-remote shapes; a standalone example and an isolated packed
  consumer both persist, project, and verify a session event.
- A pre-integration review found no vulnerable direct or transitive packages
  and all tests pass. Preview 2 now records in-repository, interface-driven
  provider contract conformance and public API compatibility against preview 1.
  Multi-targeting .NET 8 is explicitly deferred: the package relies on
  `Guid.CreateVersion7`,
  and replacing it with a custom UUIDv7 implementation would add ordering and
  identity risk without a demonstrated preview-2 consumer requirement.
- A provider-neutral participant collaboration surface is accepted as
  post-integration work. It will reuse Hongxian's event, reference, projection,
  and idempotency foundations rather than introduce a parallel message store.
- The experience-graph pivot is accepted. Siming session streams remain the
  authority for recorded evidence; experience graphs, lexical/vector indexes,
  summaries, timelines, and current state are disposable projections.
- LatticeDB is the planned first experience projection provider, not a core
  storage identity. Neo4j, another graph store, or a remote provider must be
  addable without changing evidence envelopes or portable recall consumers.
- Experience implementation Phase 0 is complete: the durable-state authority
  inventory and evidence-semantics draft are recorded, projection-lag
  characterization was extended, and clean restore/build/format/pack plus all
  76 tests pass.
- Experience implementation Phase 1 is complete: envelope v3 persists explicit
  bounded evidence nature, capture basis, and capture-time disposition; v1/v2
  remain readable without reinterpretation; authority outbox events are marked
  as receipts; build, format, pack, and all 82 tests pass.
- Experience implementation Phases 2 and 3 are complete: verified projector
  positions/checkpoints, replay-safe execution, the portable evidence-bound
  entity/relation model, provider capabilities, in-memory reference provider,
  and remote-shaped contract fixture are implemented.
- Experience implementation Phase 4 is in progress: the optional
  `Penghou.Hongxian.LatticeDb` provider implements exact lookup, bounded
  traversal, projection-scoped BM25 recall, checkpoints, typed diagnostics,
  and delete/replay equivalence. Packed-consumer LatticeDB execution and
  core/SQLite isolation are implemented and verified locally; confirmation of
  the three-platform CI matrix from a clean GitHub run remains open.

## Accepted architectural direction

The decision records are the authority for the target boundary:

- [ADR-0001](decisions/0001-siming-evidence-streams-are-authoritative.md):
  sessions are append-only Siming evidence streams and consistency boundaries;
  lifecycle and current state are projections.
- [ADR-0002](decisions/0002-provider-neutral-experience-projections.md):
  Hongxian owns logical experience projection and recall contracts; LatticeDB,
  Neo4j, and remote implementations remain replaceable providers with explicit
  capabilities.
- [ADR-0003](decisions/0003-evidence-bound-recall.md): recall is bounded,
  provenance-carrying, versioned, and reproducible enough to explain the
  context supplied to Fuwen, Baize, or another consumer.

The intended stack is:

```text
sources (people, Baize, Fuwen, Zhinu, tools, artifact systems)
                              |
                              v
                 Siming session evidence streams
                              |
                       verified projectors
                              |
          LatticeDB / Neo4j / remote experience provider
                              |
                       bounded recall API
                              |
                planning, routing, and user context
```

Cryptographic integrity proves the order and integrity of accepted assertions;
it does not prove that every assertion is factually correct. Source,
observation and commit time, schema, verification status, measurement kind, and
optional domain confidence remain explicit.

## Delivery order for the experience layer

Detailed deliverables, tests, and safe checkpoints are maintained in the
[experience implementation plan](experience-implementation-plan.md).

1. Freeze the versioned evidence envelope and assertion/source semantics.
2. Confirm sessions as expected-head Siming streams with derived lifecycle.
3. Define verified projector checkpoints, replay, and cross-ledger references.
4. Define portable experience projection and provider-capability contracts.
5. Implement the first LatticeDB graph plus lexical-search provider.
6. Add bounded `AsOf` recall with evidence references and recall receipts.
7. Add optional embeddings and summaries as provenance-linked derived data.
8. Integrate recall into Fuwen planning and record the exact supplied context.
9. Feed contextual outcome statistics to Baize; defer adaptive routing until
   enough representative evidence exists to evaluate bias and usefulness.

Each stage must update this roadmap when completed. Guyabano integration may
exercise an earlier stage, but must not freeze provider-specific types into the
portable contracts.

## Ownership boundary

Hongxian owns temporal continuity and experience semantics: session evidence
identity and lifecycle, opaque revision lineage, external-operation references,
decision coordination, incidents and recovery records, verified projection
positions, bounded recall contracts, reconciliation, audit queries, and
operational-catalog abstractions.

Hongxian does not own workflow scheduling, planning policy, application
recovery policy, workspace mutation, generated-file semantics, model requests
or routing, code facts, memory promotion, artifact bytes, database query
languages, participant authentication, or authorization. Siming remains
authoritative for cryptographic ledger format and verification.

For adaptive workflows, Fuwen owns immutable plan revisions and comparison;
Zhinu owns workflow instances, execution generations, fencing, activation, and
artifact reuse decisions. Hongxian records opaque references to those facts and
the causal evidence around them. It never decides whether a plan or artifact is
valid and never becomes the cutover authority.

## Marang Gate 0.5 audit

Last reviewed: **2026-09-07**

The current preview-2 contracts are usable for Marang's durable session and
correlation slice. Marang can use the existing `SessionId`, immutable participant
attribution, event IDs with causation/correlation, provider-qualified opaque
external-operation references, append-only verified events, `ExpectedHead`
conditional appends, idempotency keys, rebuildable projections, decision and
recovery evidence, session decision leases, and the evidence outbox plus
forward-reconciliation contracts.

Marang remains responsible for typed `SupervisedWork`, workflow run/epoch,
structural node/generation, supervisor checkpoint, and intervention semantics.
Its adapter should map those identities to Hongxian's opaque references and
application-defined events. Hongxian must not become an execution provider,
choose intervention policy, or treat attribution metadata as authorization.

Hongxian's outbox and `CrossStoreOperation` contracts coordinate evidence across
independent stores; they do not transactionally commit Zhinu's separate
database. Any Zhinu-to-Hongxian integration must preserve Zhinu as the
authority for workflow state and fencing, use idempotent saga/outbox delivery,
and reconcile forward after partial success. It must never claim a distributed
transaction or allow a Hongxian lease to override Zhinu fencing.

The only reusable follow-ups identified by this audit are the existing Milestone
4 bounded indexed correlation/as-of/re-entry query work and the conditional
Milestone 7 cross-authority evidence seam. A Marang adapter should consume the
current generic outbox/operation contracts first; add an upstream seam only if
Zhinu receipts/events cannot be represented without provider-specific leakage.

## Adaptive workflow-evolution evidence

Hongxian should make an adaptive workflow understandable after the original UI,
process, or chat context is gone. Application-defined events and opaque external
references should be sufficient to preserve a timeline such as:

```text
plan revision proposed -> replan requested -> transition preview evaluated
-> approved/rejected -> old generation quiesced/superseded
-> new generation activated -> artifacts reused/revalidated/invalidated
```

Roadmap requirements:

- [ ] Define a bounded provider-neutral reference profile for logical workflow
  instance, Fuwen plan revision/execution fingerprint, Zhinu run, execution
  generation, structural/runtime node, attempt, and transition operation.
- [ ] Record who requested replanning, why, the evidence that caused it, the
  candidate revision, policy/approval decision, and authoritative Zhinu
  transition receipt without storing chain-of-thought.
- [ ] Record artifact dispositions (`produced`, `reused`, `revalidated`,
  `invalidated`, `superseded`, `ignored`) as evidence referencing both original
  producer and current consumer; never rewrite provenance.
- [ ] Preserve pause, quiescence, resume, supersession, late completion,
  ignored-for-progression, transition failure, and recovery evidence with
  correlation/causation and idempotent delivery.
- [ ] Add projection/query support for current referenced plan/generation,
  replan history, pending transition approval, transition outcome, and unusual
  late completions after Milestone 4's generic bounded query surface exists.
- [ ] Use the existing evidence outbox and forward reconciliation for Zhinu
  delivery. Hongxian records only committed authority results and must not claim
  a distributed transaction with Zhinu.
- [ ] Prove projection rebuild from a verified ledger preserves the same
  generation/transition timeline and never attempts to reconstruct Zhinu's
  authoritative workflow state.

Branching remains a correlation concern here, not a Hongxian lifecycle model.
Different plan-lineage branches may be referenced, but choosing or activating a
branch belongs to Fuwen/host policy and Zhinu.

## Non-goals

These boundaries are deliberate. Hongxian may preserve evidence about an
external capability without becoming authoritative for that capability.

- Hongxian is not a workflow engine or a replay log for one. Zhinu remains
  authoritative for workflow state, fencing, steps, retries, signals, results,
  and selective restart. Mirrored Zhinu events are diagnostic and audit
  evidence; they cannot reconstruct, supersede, or repair Zhinu state.
- Hongxian does not execute sibling branches, evaluate candidates, or select a
  winner. Zhinu may execute candidates with `FanOutAsync`; an application makes
  the selection; Hongxian records the resulting decision and its evidence.
- A Hongxian lease cannot authorize workflow work that Zhinu fencing rejects.
  Hongxian leases coordinate session-level decisions and execution that is not
  owned by Zhinu. Operations spanning both boundaries must satisfy each
  authority at its own protected commit.
- Hongxian is not an artifact or large-payload store. Events carry bounded
  evidence and opaque references to externally owned content.
- Hongxian is not an encryption or key-management system. A future protection
  contract may retain an opaque encrypted payload, but hosts own encryption,
  keys, rotation, access control, and disclosure policy.
- Append-only does not mean retain forever. Hosts own backup, archive, export,
  anchoring, and deletion policy; Hongxian must make those operations explicit
  and preserve verifiability where history is retained.

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
- [x] Define bounded handle/cache behavior for providers that keep one ledger
  or database handle per session.

## Milestone 3 — Preview 2 integrity, neutrality, and usability hardening

Complete this breaking-change batch before Guyabano consumes Hongxian. Preview
1 proved packaging and the extracted behavior; preview 2 should establish the
contract that applications integrate against.

Integrity and concurrency:

- [x] Add optional conditional event append against an expected authoritative
  ledger head (ledger identity, sequence, and hash), distinct from idempotency
  and operational-catalog versions. State-dependent decisions can reject stale
  observations while independent messages and diagnostics remain concurrently
  appendable. Require Siming to enforce the condition atomically inside the
  append transaction; do not implement a read-then-append check in Hongxian.
- [x] Make projection rebuild consume verified ledger history or a verified
  head contract. Validate chain continuity as well as sequence continuity, and
  never allow rebuild/application state to replace an authoritative committed
  head hash at the same sequence.
- [x] Turn decision leases into a genuine fencing contract: expose fencing
  token and expiry, signal lease loss immediately, allow ownership assertion at
  the protected commit, and test renewal failure and stale-holder rejection.
  Document that Zhinu fencing remains authoritative for Zhinu-owned workflow
  commits and cannot be overridden by a Hongxian lease.
- [x] Add a transactional evidence outbox for cross-store operation creation,
  participant receipts, and transitions, plus a reusable idempotent dispatcher
  into the session ledger. Operational SQLite rows must not be the only audit
  evidence.
- [x] Add versioned SQLite migrations with an explicit schema version,
  serialized upgrade, unsupported-newer-schema rejection, reopen/upgrade tests,
  and documented backup responsibility.
- [x] Define event-envelope and application-payload schema versions separately
  from the SQLite storage schema. Add a provider-neutral upcaster registry for
  projection replay and typed reads, allow applications to register payload
  upcasters, reject unsupported versions with typed results, and never rewrite
  immutable historical ledger entries during migration.
- [x] Validate default/empty value-type IDs, enum values, timestamps, monotonic
  transition time, and bounded strings at every public persistence boundary.
  Use `TimeProvider` consistently for library-authored audit and cache times.
- [x] Define a provider-neutral consistency-audit result covering verified
  ledger head, projection cursor, catalog version, incomplete operations,
  evidence outboxes, leases, and receipts.

Provider-neutral contract cleanup:

- [x] Replace GUID-only external operation identity with a bounded opaque
  identity while retaining provider/system qualification and ordinal identity
  semantics.
- [x] Remove Guyabano-shaped operation phases such as `RevisionCommitted` and
  `Published`. Keep only generic lifecycle/health semantics or make application
  phases opaque and validated.
- [x] Replace `RefreshPreview`, `AbandonCandidate`, `SafeRevision`, and other
  code-generation recovery vocabulary with application-defined action and
  resource references.
- [x] Move revision-promotion commit policy out of the generic kernel and into a
  Guyabano profile or adapter. Hongxian should expose reusable transactional
  receipt/outbox mechanics rather than one application's mutation.
- [x] Separate recovery recording from application recovery execution. A helper
  may wrap an application-supplied handler, but the core must not choose,
  schedule, or claim authority over the action.
- [x] Replace hard-coded application-style reconciliation instructions with
  structured health, incomplete participant, and suggested-action-code data;
  applications own user-facing explanations.

Consumer usability:

- [x] Replace the free-form actor string with a compact immutable participant
  attribution reference containing a small kind, provider, stable opaque
  subject, and optional display-name snapshot. These remain host-supplied
  claims; metadata or capability claims must never grant authorization.
- [x] Add canonical `AppendAsync<T>` and `JsonElement` payload APIs plus typed
  read helpers. Keep retention and idempotency semantics independent of the
  original CLR type and document the identity behavior of omitted payloads.
- [x] Add typed public exceptions/results for idempotency conflict, stale
  version, lost lease, projection gap/lag, corruption, and unsupported schema;
  consumers must not parse exception messages.
- [x] Add `TryParse`, JSON converters, and formatting support for public IDs and
  external references.
- [x] Add a small SQLite composition root/options model that consistently opens
  catalog, per-session ledgers, projections, leases, operations, and evidence
  dispatchers without hiding provider interfaces.
- [x] Expose projection-delivery outcome or diagnostics from append so callers
  can observe committed-but-lagging state without relying on trace output.
- [x] Expose typed, queryable health results for ledger verification,
  projection lag, evidence-outbox delivery, incomplete operations, failed
  participants, lease ownership/loss, schema compatibility, and required
  reconciliation. Logs and traces remain diagnostics, not the operator API.
- [x] Centralize bounded input limits for participants, event/application kinds,
  external identities, references, metadata, reasons, and receipts across both
  ledger and operational stores.

Package and contract quality:

- [x] Add an in-repository, interface-driven provider conformance reference
  suite for event, projection, catalog, lease, lifecycle/outbox, cross-store
  operation, and inspection contracts. An exported
  `Penghou.Hongxian.Testing` package remains a future opportunity if another
  provider makes that distribution useful.
- [x] Defer .NET 8 multi-targeting for preview 2. `Guid.CreateVersion7` is
  available in the supported net10 target; a net8 fallback would require a
  second UUIDv7 implementation and could weaken ordering or identity semantics.
- [x] Add public API analyzer shipped/unshipped baselines and enable package
  compatibility validation against preview 1. Intentional preview-2 contract
  breaks are recorded in package-validation suppression files.

## Milestone 4 — Experience projection, recall, query, and portability

These reusable APIs moved from Guyabano's interactive-session backlog. UI and
application policy remain with consumers.

Evidence and projection contract:

- [x] Inventory current event, catalog, projection, and operation fields and
  classify each as authoritative evidence, rebuildable derived state, or
  ephemeral coordination. Do not remove the current SQLite implementation until
  parity and verified rebuild tests exist.
- [x] Freeze a versioned provider-neutral evidence envelope vocabulary for
  assertion/measurement kind, source, observed and committed time, verification
  status without breaking application-defined payloads. Deliberately keep
  confidence in qualified application payloads until two domains prove a
  portable core meaning.
- [x] Define projector identity and version, verified source head, projection
  checkpoint, replay idempotency, gap/corruption behavior, and safe rebuild from
  one or more session ledgers.
  - [x] Define bounded canonical multi-ledger positions, stable projector
    descriptors, and monotonic idempotent checkpoint transition rules with a
    concurrency-safe in-memory reference store.
  - [x] Add verified projector execution, interruption recovery, gap rejection,
    and delete/rebuild equivalence before closing the parent item.
- [ ] Define bounded cross-ledger evidence references and discovery. A project
  experience view may span sessions, but no projection may silently merge or
  rewrite their authoritative histories.
  - [x] Define canonical, bounded immutable references to session, ledger,
    sequence, event identity, and event hash.
  - [ ] Add bounded discovery by evidence reference without scanning or merging
    authoritative ledgers.
- [x] Define provider-neutral logical projection and recall ports. Keep storage
  schemas, SQL, Cypher, LatticeDB types, embedding types, and transport clients
  out of `Penghou.Hongxian` public contracts.
- [x] Define an explicit provider capability descriptor for graph traversal,
  lexical, vector, hybrid ranking, `AsOf`, checkpoint consistency, and remote
  operation. Missing capability returns a typed result or uses a declared,
  deterministic fallback.
- [x] Add an internal provider conformance suite covering checkpoint monotonicity,
  idempotent replay, evidence provenance, deletion/rebuild, bounds, truncation,
  unsupported capabilities, and projection-lag reporting.

First provider and recall slice:

- [ ] Implement LatticeDB as the first disposable experience provider, starting
    with evidence-backed entities/relations and lexical search. Keep the package
    boundary provisional until dependency and deployment needs justify a split.
  - [x] Add the optional provider project, explicit lifecycle/open options,
    versioned physical schema, replay-safe writes, exact lookup, bounded
    traversal, projection-scoped BM25 search, typed diagnostics, and native
    provider tests.
  - [ ] Prove the packed package and native assets in an isolated consumer on
    every supported CI platform; verify core/SQLite packages remain isolated.
- [ ] Prove a projection can be deleted, rebuilt from verified Siming streams,
    and produce equivalent portable results at the same checkpoint.
  - [x] Prove provider-level delete/replay equivalence for portable entity,
    relation, lexical, and traversal results.
  - [ ] Complete end-to-end verified-history projector equivalence once an
    application-neutral event-to-experience mapping fixture is defined.
- [ ] Add bounded recall requests with scope, `AsOf`, item/byte/token budgets,
  evidence-strength and freshness requirements, revision filters, and a
  versioned retrieval policy.
- [ ] Return evidence identities, projection/provider identity and checkpoint,
  derivation/policy versions, score semantics, truncation, freshness, and
  unsupported-capability diagnostics.
- [ ] Define a compact recall receipt containing query fingerprint, selected
  evidence, checkpoint, policy version, and supplied-context digest so a
  consequential consumer can append what influenced it.
- [ ] Add summaries and embeddings only as rebuildable derivations with source
  evidence, model/provider/version, creation time, access classification, and
  invalidation rules. Similarity must not be presented as factual confidence.

Portable session and operator queries:

- [ ] Add bounded, projection-backed, indexed query APIs for session catalog
  lookup, paged timeline, projection delivery status, pending inputs, pending
  decisions, active incidents, and incomplete operations. Support reusable
  envelope filters such as event type, participant, committed/occurrence time,
  correlation, causation, external reference, and external execution identity
  without scanning the authoritative ledger for routine queries or demand-driven
  re-entry context.
- [ ] Add immutable named checkpoints that bind a session ledger sequence and
  verified head hash to application-defined kind/name, participant, causation, and
  bounded external resource identities, revisions, and digests. Hongxian
  records checkpoints but never restores external state.
- [ ] Add bounded as-of projection at a verified ledger sequence or named
  checkpoint for demand-driven re-entry context. Begin with deterministic
  streaming replay; introduce cached snapshots only after measured interactive
  workloads justify them.
- [ ] Add an optional indexed `SessionRelation` with source, target,
  application-defined kind, participant, time, and causation. Relations support
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
- [ ] Define lifecycle, verification, and anchoring as independent dimensions:
  for example active/closed/archived, unverified/verified/corrupt, and
  uncheckpointed/checkpointed/externally anchored. Document payload retention,
  ledger deletion, backup, checkpoint anchoring, archive verification, and
  projection-rebuild responsibilities without collapsing them into one status
  enum.
- [ ] After a concrete threat model and consumer require it, define a
  host-supplied payload-protection extension that can retain an opaque encrypted
  envelope with bounded algorithm/version, key reference, protection
  parameters, and ciphertext digest. Define canonical identity semantics, but
  leave encryption, keys, rotation, and authorization to the host. Do not add a
  `RetainEncrypted` value that implies protection without this contract.
- [ ] After schema/version contracts stabilize, add verified single-session
  export/import containing the authoritative ledger, versioned manifest,
  catalog metadata, relations, checkpoints, and bounded external references.
  Projections are rebuildable and need not be authoritative export content;
  import verifies history, rejects ambiguous ID collisions, and never silently
  merges two ledgers.
- [ ] Decide whether session branching belongs in the generic kernel only after
  selective rerun and a second consumer establish useful semantics.

Provider evolution gates:

- [ ] Validate a second provider shape before declaring the portable interface
  stable. A thin in-memory reference provider is sufficient for conformance;
  Neo4j or a remote provider should be implemented only when a real consumer
  needs its deployment or traversal characteristics.
- [ ] For remote providers, define authentication/configuration ownership,
  timeout and cancellation, retry/idempotency, consistency/freshness reporting,
  disclosure classification, tenant isolation, and safe degraded behavior.
- [ ] Permit provider-native administration and advanced queries on
  provider-specific APIs, while keeping Fuwen, Baize, and ordinary Hongxian
  consumers on the portable evidence/recall surface.

### Historical execution and retrospective projections

Hongxian is episodic execution evidence, not a truth engine. It can prove that
an attributed claim or receipt was committed and verify its history; only an
authoritative source receipt establishes the execution fact it describes.
Model assertions remain assertions even when they appear in a successful run.

- [ ] Define a portable historical-execution graph profile spanning immutable
  plan revisions, workflow generations, activities/attempts, actors,
  model/provider/profile identities, tools, artifacts, validation, retries,
  corrections, context additions, mutations, checkpoints, compensation,
  timing, usage/cost, and outcomes.
- [ ] Add a deterministic mechanical-retrospective projector comparing the
  original plan, activated revisions, actual execution, mutations, retries,
  substitutions, validation failures, recoveries, unresolved issues, and final
  outcome. Its outputs are rebuildable views, not new evidence authority.
- [ ] Add contextual aggregates by task class, model/version, provider, role,
  prompt/profile, tool requirements, and reasoning mode. Expose sample size,
  observation window, recency, and supporting evidence.
- [ ] Keep model reputation and usage guidance as versioned advisory
  projections. Add recency decay and model/profile boundaries before routing
  consumes them; controlled exploration and feedback-loop evaluation are later
  policy work.
- [ ] Add bounded similar-run and workflow-mutation-pattern retrieval only
  after the mechanical retrospective supplies stable comparable features.
- [ ] Permit optional reasoned retrospectives to append attributed candidate
  lessons linked to their mechanical inputs. Normal successful runs require no
  model call, and no candidate becomes long-term knowledge automatically.
- [ ] Define retention and compression tiers for derived experience while
  preserving immutable source evidence and every published recall receipt.

Non-goals for this slice are a global model score, automatic routing, mandatory
LLM retrospectives, workflow interpretation inside Hongxian, and silent
promotion of an observation into truth or reusable knowledge.

## Milestone 5 — Package and Guyabano integration

- [x] Publish `Penghou.Hongxian` and `Penghou.Hongxian.Sqlite`
  `0.1.0-preview.1` after CI and packed-consumer validation pass on GitHub.
- [x] Publish preview 2 after the Milestone 3 integrity and contract gates pass
  locally, in CI, and through the isolated packed consumer.
- [ ] Replace Guyabano's internal session projects with package references.
- [ ] Keep Guyabano event vocabulary, workspace policy, product recovery
  handlers, and Penghou-provider adapters in Guyabano.
- [ ] Add a Guyabano mapping layer for its Zhinu workflow IDs, workspace
  revisions, event vocabulary, and domain recovery explanations.
- [ ] Re-run the complete Guyabano suite and realistic recovery dogfood flow.
- [ ] Prove restart and projection reconstruction after process loss.
- [ ] Remove Guyabano's temporary duplicate kernel implementation only after
  package-backed parity is proven.
- [ ] After the portable recall slice is stable, let Guyabano query experience
  through Hongxian rather than LatticeDB-specific APIs and append a recall
  receipt whenever retrieved experience influences planning or recovery.
- [ ] Treat Hongxian experience as advisory context. Fuwen owns plan revisions,
  Zhinu owns deterministic execution, and Guyabano owns application policy.

## Milestone 6 — Participant collaboration surface

Implement this after the first package-backed Guyabano integration establishes
which collaboration concepts are genuinely reusable. The invariant is:

> Hongxian records collaboration. Applications interpret collaboration.
> Workflow engines control execution.

Initial scope:

- [ ] Reuse the preview 2 participant attribution reference for human, model,
  workflow-activity, tool, system, and external publications. Collaboration
  must not introduce a second participant directory or identity format.
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

## Milestone 7 — Optional external-capability adapters

Hongxian remains complete with zero optional adapters. It owns session
continuity, immutable evidence, and opaque correlation; an external system owns
the derived state it publishes. Adding or removing an adapter must not change
ordinary session semantics.

### Session-resource adapter design gate

- [x] Confirm by source dependency audit that `Penghou.Hongxian` and
  `Penghou.Hongxian.Sqlite` have no Hetu, parser, graph-store, or Roslyn runtime
  dependency. `Microsoft.CodeAnalysis.PublicApiAnalyzers` is build-only with
  `PrivateAssets=all`; the packed-consumer gate must prove it does not flow to
  consumers. The remaining risk is future transitive package composition, not
  a current code-memory dependency in Hongxian core.
- [ ] First test whether the existing evidence outbox, cross-store operation,
  participant-health, and opaque external-reference contracts can support
  resource synchronization from application composition. Introduce
  `Penghou.Hongxian.Hosting` only when repeated consumers prove that adapter
  lifecycle and DI composition are reusable package responsibilities.
- [ ] If a reusable seam is required, keep it resource-neutral: an adapter may
  receive an opaque resource identity and immutable revision and return bounded
  provider, external identity/revision, digest, and synchronization evidence.
  Do not add code, repository, parser, graph, workspace, Git, or Hetu types to
  Hongxian core contracts.
- [ ] Define synchronization triggers and ordering before freezing an API:
  committed revision, outbox delivery, explicit refresh, superseding revision,
  concurrent synchronization, late completion, cancellation, and stale-result
  handling must be deterministic and idempotent.
- [ ] Treat zero registered adapters as the ordinary absence of a capability,
  not as a `NotConfigured` synchronization result and not as degraded session
  health. A host may explicitly require a capability for one application or
  resource, but that is host admission policy rather than a global Hongxian
  requirement.
- [ ] Reuse forward reconciliation for partial cross-store success. An adapter
  failure may produce immutable incident/participant evidence and mark the
  derived state lagging, but it must not roll back, rewrite, or invalidate the
  authoritative session revision.
- [ ] Keep adapter registration host-authorized. Session payloads and opaque
  resource identifiers must never select implementations, grant filesystem or
  network authority, or supply unbounded metadata. Apply existing bounds,
  redaction, and sensitive-path/credential rules to adapter inputs and results.
- [ ] Add isolated packed-consumer tests proving Hongxian, Hongxian.Sqlite, and
  any generic hosting package have no transitive `Penghou.Hetu*`, Roslyn,
  ANTLR, or LadybugDB dependency. Add a zero-adapter recovery test.
- [ ] Consider `Penghou.Hongxian.Hetu` only after this boundary is proven. It
  may depend on Hongxian hosting and Hetu, correlate an exact session resource
  revision with an exact Hetu publication, and expose capability/freshness
  evidence without exporting Hetu domain types through Hongxian contracts.
- [ ] Prove the optional Hetu path with R1 -> H1 and R2 -> H2 correlation,
  failure plus forward reconciliation, and removal of the package/registration
  while the same generic session lifecycle continues to work.

This boundary is a prerequisite for the planned Marang/Hetu shared-workspace
and structural-catch-up experiment, but it is not the implementation of that
experiment. Workspace composition, graph deltas, code-specific synchronization
policy, and MCP presentation remain Hetu and Marang responsibilities.

Do not implement dynamic assembly discovery, MEF, runtime NuGet loading,
generic `ICodeMemory`/`ICodeGraph`/`IGraph` abstractions, copied graph content,
or parser-specific contracts as part of this milestone. NuGet references plus
explicit host registration are the extension model until real demand proves a
need for more.

### Workflow execution adapters

- [ ] If the generic evidence-outbox and cross-store-operation contracts cannot
  consume Zhinu receipts/events without provider-specific leakage, define a
  provider-neutral cross-authority evidence seam with an idempotent delivery
  cursor and forward reconciliation. Guyabano's current Zhinu-to-Siming mirror
  is the reference integration; this seam must not claim an atomic Zhinu plus
  Hongxian transaction.
- [ ] Consider `Penghou.Hongxian.Zhinu` only if it can map authoritative Zhinu
  receipts/events without importing Guyabano statuses or policy.
- [ ] Document that Zhinu's persisted run, step, result, restart, signal, and
  fencing state is authoritative. Hongxian mirrors selected diagnostics and
  evidence for correlation; it does not treat the Zhinu event stream as an
  event-sourced replay contract or reconcile it as a peer source of truth.
- [ ] Add a worked `Zhinu.FanOutAsync` to Hongxian decision-evidence example:
  reference every candidate and authoritative Zhinu result, then record the
  application-selected winner, rejected candidates, evaluator, rationale,
  scores, and bounded evidence/artifact references. Hongxian records the
  decision but neither executes nor selects the branches.
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
- [ ] Benchmark per-session SQLite append and projection behavior under a
  representative parallel media batch or large fan-out before introducing
  write-behind complexity or recommending another provider. Treat measured
  contention, not hypothetical scale, as the trigger for optimization.

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
- Which minimal opaque transition receipt fields are required for useful
  workflow-evolution queries without copying Fuwen or Zhinu state models?
- Which publication kinds need generic projection support beyond application
  queries, without making Hongxian interpret their domain meaning?
- What is the smallest useful portable experience model: evidence entities and
  typed relations only, or a small set of derived observation/outcome concepts?
- Should a project-wide projector consume a catalog of independently verified
  session heads, or should cross-session composition be a separate projection
  layer?
- Which score semantics can be portable across lexical, vector, graph, and
  hybrid providers without inventing misleading normalized confidence?
- What minimum capabilities must every experience provider implement, and which
  remain optional with explicit degradation?
- When does splitting `Penghou.Hongxian.Sqlite` into evidence composition and
  projection-provider packages materially improve deployment or dependency
  clarity?
