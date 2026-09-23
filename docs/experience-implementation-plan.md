# Hongxian experience implementation plan

## Status

Planned on **2026-09-13** from the accepted architecture decision records.
Phases 0 through 5 are complete. This document converts the target architecture
into safe implementation phases. The [roadmap](roadmap.md) remains the progress
summary; this plan carries the working detail and phase gates.

## Outcome

Hongxian will preserve evidence in independently verifiable Siming session
streams, build disposable experience projections through replaceable providers,
and expose bounded evidence-bound recall to Fuwen, Baize, Guyabano, and other
consumers.

The minimum useful vertical slice is:

```text
verified session evidence
        -> provider-neutral experience records
        -> LatticeDB graph + lexical index
        -> bounded recall result with evidence references
        -> recall receipt appended to the session ledger
```

Vector search, generated summaries, adaptive model routing, Neo4j, and remote
providers follow this slice. None is required to prove the boundary.

## Existing foundation to preserve

| Existing capability | How the experience work uses it |
| --- | --- |
| `ISessionEventStore` | Source of ordered session evidence |
| `SessionEvent` envelope v2 | Starting point for evidence semantics |
| `SessionLedgerHead` and `ExpectedHead` | Optimistic concurrency and reproducible positions |
| `ReadVerifiedHistoryAsync` | Verified input to a projector |
| `ISessionProjectionStore` | Existing session timeline/current-state projection; it remains separate |
| Projection delivery status | Existing pattern for committed-but-lagging diagnostics |
| Evidence outbox and cross-store receipts | Forward-only delivery from independent authorities |
| `SessionPayloadSchema` and upcasters | Payload evolution without rewriting history |
| Provider conformance fixture | Pattern for a portable experience-provider suite |

The current SQLite projection is not migrated into an experience graph and is
not removed. Timeline/current-state queries and experience recall solve
different problems and may coexist.

## Phase 0 — Baseline and authority inventory

Working classification and Phase 1 contract questions are recorded in the
[evidence authority inventory](evidence-authority-inventory.md).

**Status: complete (2026-09-13).**

### Deliverables

- Restore from explicit local and NuGet sources, then record a clean build,
  test, formatting, and package baseline.
- Classify every persisted field/table and public store contract as:
  authoritative evidence, rebuildable derived state, operational coordination,
  or externally authoritative reference.
- Trace append, projection delivery, rebuild, outbox, and reconciliation paths.
- Record gaps between envelope v2 and ADR-0001 without changing the public API.
- Decide which existing SQLite schemas are retained unchanged during the first
  experience slice.

### Gate

An inventory document and characterization tests explain what can be deleted
and rebuilt, what must be backed up, and which authority owns every durable
value. The existing suite passes from a clean restore.

### Safe checkpoint

Documentation and characterization tests only. This is the first batch to
implement.

### Result

- Authority inventory and evidence-semantics draft completed.
- Existing append, projection, outbox, and reconciliation paths traced.
- Existing SQLite schemas retained unchanged for the first experience slice.
- Added explicit lag characterization for a projection whose applied head
  trails the committed ledger head.
- Clean explicit-source restore, Release build, 76 tests, formatting
  verification, and package creation passed with zero build warnings/errors.

The repeatable restore command for a checkout using the local Siming package is:

```powershell
dotnet restore Penghou.Hongxian.slnx `
  --source https://api.nuget.org/v3/index.json `
  --source C:\Users\Laszlos\source\repos\Penghou.Siming\artifacts `
  -p:NuGetAudit=false -p:TreatWarningsAsErrors=false
```

CI and consumers should continue using NuGet.org after the required Siming
version is published; the local source is a development override, not a package
contract.

## Phase 1 — Evidence semantics and envelope v3

**Status: complete (2026-09-13).**

### Deliverables

- Add bounded, extensible evidence semantics without replacing
  application-defined event and payload kinds. Prefer validated opaque value
  types with well-known values over closed enums.
- Distinguish assertion, observation, measurement, decision, receipt, and
  diagnostic evidence where that distinction affects interpretation.
- Preserve source attribution, claimed occurrence time, ledger commit time,
  schema identity, retention, correlation, causation, and external references.
- Define capture-time verification/disposition precisely. Later corroboration,
  contradiction, or verification is appended as new evidence referencing the
  original event; an immutable event is never relabelled in place.
- Keep optional confidence domain-qualified. A bare numeric similarity or model
  probability must not masquerade as factual confidence.
- Introduce envelope v3 only if these semantics cannot be expressed safely as a
  versioned payload profile. Continue reading v1/v2 and never reinterpret their
  hashes.
- Add typed validation failures and centralized bounds for all new fields.

### Tests

- Canonical typed and `JsonElement` round trips.
- v1/v2 compatibility and v3 read/write vectors if v3 is introduced.
- Idempotent replay and conflicting reuse.
- Expected-head conflict under concurrent evidence appends.
- Invalid kind, status, confidence qualifier, reference, and size boundaries.
- Later verification appends without mutation of original evidence.

### Gate

Persisted evidence is independently understandable without knowing an original
CLR type, and its semantics do not claim more certainty than its source proves.

### Result

- Envelope v3 adds an immutable `SessionEvidenceDescriptor` containing bounded,
  extensible `Nature`, `Basis`, and capture-time `Disposition` identifier tokens.
- Well-known values cover assertions, observations, measurements, decisions,
  receipts, diagnostics, participant claims, authority receipts, direct
  observations, derivations, and assessment outcomes without closing the
  vocabulary to applications.
- New writes always persist explicit evidence semantics. Callers that omit the
  descriptor receive `unspecified` nature/basis and `unassessed` disposition.
- Existing v1/v2 events retain `null` semantics and are never reinterpreted.
  Legacy idempotent replay succeeds only when the new caller also leaves
  semantics unspecified; it cannot retroactively attach a descriptor.
- Catalog and cross-store outbox delivery is classified as an authority receipt.
- Later support, contradiction, or supersession is represented by a new linked
  event. The original event remains unchanged.
- Confidence was deliberately left out of core until two domains establish a
  portable qualified meaning.
- Release build, all 82 tests, formatting verification, and package creation
  pass.

## Phase 2 — Projection position and verified projector substrate

### Progress

Completed:

- Projection identities use UUIDv7-backed value objects; descriptors bind the
  provider, projector, schema version, and policy version without leaking a
  storage schema.
- A bounded canonical position records each independently ordered session's
  ledger identity, verified sequence, and head hash. Its versioned SHA-256
  digest uses length-prefixed UTF-8 fields and has an order-independent golden
  vector.
- Provider-neutral checkpoint rules enforce optimistic concurrency,
  monotonicity, idempotent replay, stable ledger identity, same-sequence hash
  consistency, and version-exhaustion handling with typed failures.
- A concurrency-safe in-memory checkpoint store exercises the contract.
- The provider-neutral projector accepts only complete verified histories,
  validates sequence and hash-link continuity, and rejects unsupported envelope
  schemas before provider writes begin.
- Projection effects have stable identities and distinguish new writes,
  content-equivalent replays, and conflicting reuse of an existing identity.
- Projection writes and checkpoint commits may be non-atomic. Typed failures
  expose the intended position and whether provider state may have changed;
  retry replays effect identities safely and heals the checkpoint.
- Complete rebuild explicitly resets the checkpoint and deletes disposable
  provider state before replay. A failed or interrupted reset is recovered by
  retrying the whole rebuild.
- Tests cover multi-session replay, source growth, gaps and broken links,
  unsupported schemas, checkpoint failure, commit-then-fail writes, partial
  rebuild reset, effect conflicts, and delete/rebuild equivalence.

### Deliverables

- Add stable projection/provider identity and projector schema/policy versions.
- Model a single-stream position with session ID, ledger identity, sequence,
  and verified head hash.
- Model a multi-stream projection position as a bounded, canonical set of
  single-stream heads plus a deterministic position digest. Do not invent a
  misleading global sequence across independent ledgers.
- Define checkpoint persistence, monotonic advancement, idempotent replay,
  cancellation, and crash recovery.
- Reject gaps, same-sequence/different-hash histories, unsupported schemas, and
  projector-version mismatches with typed results.
- Add a small in-memory reference implementation used by provider conformance
  tests before binding the contract to LatticeDB.
- Reuse the evidence outbox/reconciliation pattern when a projection update and
  its checkpoint cannot commit atomically.

### Tests

- Delete and rebuild from verified history.
- Resume after interruption without duplicate entities or relations.
- Concurrent stream growth while projecting an earlier verified head.
- Multi-session position canonicalization and order independence.
- Corruption, gap, stale checkpoint, and provider-version rejection.

### Gate

A disposable in-memory projection can be rebuilt reproducibly from one or more
verified session heads, and its checkpoint says exactly what evidence it covers.

Gate passed. The implementation deliberately stops short of defining portable
experience entities, relations, or query behavior; those belong to Phase 3.

## Phase 3 — Portable experience model and provider SPI

### Progress

Completed:

- Portable entities and directed relations carry rebuild-stable UUIDv8
  identities, application-defined kinds, canonical bounded JSON properties,
  optional temporal validity, projector/derivation provenance, and at least one
  immutable session-ledger evidence reference.
- Deterministic identity derivation uses a published version marker and
  length-prefixed UTF-8 hashing. Golden and delimiter-boundary tests protect the
  persistence contract.
- Evidence-reference identity is derived from its complete ledger location,
  not merely the event GUID. Property and evidence ordering is canonical and
  duplicate evidence identities are rejected.
- Projection writes and recall reads are separate ports. Providers declare
  exact lookup, traversal, lexical, vector, hybrid, `AsOf`, checkpoint, and
  remote capabilities rather than silently approximating unsupported behavior.
- Recall completion, freshness, degradation, and truncation are orthogonal;
  unsupported operations return no fallback records and identify the missing
  capability explicitly.
- An in-memory provider proves replay-safe writes, conflict detection, bounded
  traversal and lexical lookup. A transport-shaped fake remote provider proves
  the contracts do not require local files, shared transactions, or a native
  query language.

### Deliverables

- Define stable experience entity, relation, and evidence-reference identities.
- Keep the portable model small: identity, application-defined kind, bounded
  properties, provenance, derivation identity, temporal validity where known,
  and source evidence references.
- Separate projection writes from recall reads so read-only remote providers and
  independently operated projectors remain possible.
- Define capabilities for exact lookup, graph traversal, lexical search, vector
  search, hybrid ranking, `AsOf`, and checkpoint consistency.
- Define typed unsupported-capability and degraded/freshness results. A provider
  must not silently substitute different query semantics.
- Keep SQL, Cypher, LatticeDB handles, embedding vector types, and transport
  clients out of `Penghou.Hongxian`.
- Publish a reusable conformance test base only after the internal suite proves
  useful to at least two implementations.

### Gate

The in-memory reference provider implements the portable contract, and a fake
remote provider proves that the contract does not assume local files,
transactions, or provider-native query syntax.

Gate passed. The deliberately small search requests are provider-contract
probes, not the complete evidence-bound recall policy planned for Phase 5.

## Phase 4 — First provider: LatticeDB graph and lexical recall

### Progress

Started:

- Added the optional `Penghou.Hongxian.LatticeDb` package boundary on published
  `LatticeDbSharp` 0.2.0; core and SQLite consumers do not acquire that native
  dependency.
- Added explicit file/memory location, create/open-existing/read-only modes,
  WAL durability, locking, provider-owned disposal, and typed native-boundary
  diagnostics.
- Documented and implemented a versioned provider-private graph schema with
  projection-scoped portable identities and complete portable JSON payloads.
- Implemented replay-safe entity/relation writes, exact lookup, bounded
  traversal, projection-isolated BM25 lexical search, checkpoint persistence,
  projection deletion, and delete/replay equivalence.
- Added native Windows tests and a CI matrix for Windows x64, Linux x64, and
  macOS ARM64 provider conformance.
- Extended the isolated packed-consumer audit to execute the LatticeDB package
  (memory-provider entity/relation writes, exact lookup, lexical search,
  traversal, checkpoint, delete/replay equivalence) and to prove the core and
  SQLite consumer remains free of LatticeDB assets via transitive package and
  package-cache checks. Verified locally on Windows with all 123 tests passing;
  the three-platform CI matrix now packs and runs the same audit on every
  supported platform.
- Added packed-consumer coverage to the LatticeDB CI matrix job so provider
  conformance plus packed execution run on Windows, Linux, and macOS.
- Reassessed the provider-conformance extraction (2026-09-23): the portable
  writer/reader cases now live in an internal shared suite
  (`ExperienceProviderConformanceCases`) exercised against three shapes
  (in-memory reference, LatticeDB memory, transport-shaped fake remote) by
  `ExperienceProviderConformanceTests`. Session-kernel conformance still has a
  single implementation, so a public `Penghou.Hongxian.Testing` package remains
  deferred until a second session provider or a Neo4j/remote consumer needs it.
  Two behaviors are documented as provider-specific and excluded from the
  shared suite: missing-relation-endpoint handling (conflict in LatticeDB,
  accepted by the reference provider) and provider-mismatch signaling
  (conflict result versus `ArgumentException`).
- Confirmed the gate from a clean GitHub run (2026-09-23, CI run 35874188863):
  provider conformance plus packed LatticeDB execution and core/SQLite
  isolation passed on Windows x64, Linux x64, and macOS ARM64, and the
  build-test-pack job passed with all 128 tests.

Gate passed (2026-09-23). Deleting the LatticeDB database and replaying the
same verified position produces equivalent portable records and queries, as
proven by the delete/replay conformance cases and the packed-consumer audit.

### Deliverables

- Add an optional provider project, provisionally
  `Penghou.Hongxian.LatticeDb`, referencing `LatticeDbSharp` and core Hongxian.
- Map portable entities, relations, evidence references, and checkpoints to a
  documented LatticeDB schema.
- Implement exact lookup, bounded relation traversal, and lexical/BM25 search
  before adding embeddings.
- Make database location, ownership, open mode, durability, and disposal
  explicit in provider options.
- Surface native-runtime/platform failures as typed provider diagnostics.
- Support projection deletion and complete rebuild without touching Siming
  ledgers or existing SQLite operational state.
- Add an isolated packed-consumer test so LatticeDB native assets do not leak
  into consumers of `Penghou.Hongxian` or `Penghou.Hongxian.Sqlite`.

### Gate

Windows and CI-supported platforms pass provider conformance and packed-consumer
tests. Deleting the LatticeDB database and replaying the same verified position
produces equivalent portable records and queries.

## Phase 5 — Bounded evidence-bound recall

### Progress

Started (2026-09-23):

- Added portable recall contracts in `Penghou.Hongxian`: retrieval policy,
  kind-scoped request with `AsOf`, item/byte/token budgets, evidence-strength
  and freshness requirements, provider-ranked results with checkpoints,
  canonical query fingerprints, and compact recall receipts.
- Added a portable executor that enforces budgets as hard caps, surfaces
  typed unsupported results without fallback, and keeps provider ranking with
  record-id tie-breaking and deterministic replay.
- Added receipt persistence helpers (`experience-recall-recorded` event,
  `penghou.recall-receipt` schema v1, fingerprint-derived idempotency) plus a
  typed receipt reader for audits.
- The suite passes 143 tests, including executor, fingerprint, receipt, and
  ledger round-trip coverage across in-memory, LatticeDB, and fake-remote
  shapes.
- The standalone example appends session evidence, projects it into LatticeDB
  through a sample-local event-to-experience mapping, recalls a bounded
  result, appends a recall receipt to the same session ledger, rebuilds the
  projection with equivalent results, and prints the fingerprint, evidence,
  policy, truncation, and receipt behind its decision.

Gate passed (2026-09-23, CI run 35883135145). The sample appends evidence,
projects it into LatticeDB, recalls a bounded result, appends a recall
receipt, rebuilds the projection with equivalent results, and prints the
fingerprint, evidence, policy, truncation, and receipt behind its decision.
This is the first package candidate useful to Guyabano and Fuwen.

### Deliverables

- Add a recall request with scope, `AsOf` position/checkpoint, item and byte
  budgets, optional token budget, freshness, evidence-strength requirements,
  bounded filters, and retrieval-policy identity/version.
- Add recall results carrying provider/projection identity, covered checkpoint,
  source evidence identities, derivation versions, score semantics, freshness,
  truncation, and diagnostics.
- Specify stable ordering and tie-breaking for portable result sets.
- Add a canonical query fingerprint independent of CLR serialization details.
- Add a compact recall receipt payload/profile containing the query fingerprint,
  selected evidence, checkpoint, policy version, and actual supplied-context
  digest.
- Provide helpers for appending receipts, but leave the consequential planning
  or routing decision to the consumer.

### Gate

A sample can append evidence, project it into LatticeDB, recall a bounded result,
append a recall receipt, rebuild the projection, and explain exactly what
evidence influenced the sample decision.

This is the first package candidate useful to Guyabano and Fuwen.

## Phase 6 — Derived summaries and embeddings

### Deliverables

- Add optional derivation contracts for summaries and embeddings, using a
  host-supplied generator rather than a required model SDK.
- Persist evidence inputs, generator/provider/model/version, policy version,
  creation time, content digest, dimensions where applicable, and invalidation
  rules.
- Propagate sensitivity, retention, authorization scope, and redaction policy to
  every derivation.
- Support re-derivation without rewriting evidence or invalidating older recall
  receipts.
- Extend capabilities and conformance tests for vector and hybrid retrieval.

### Gate

Changing an embedding or summarization provider requires rebuilding only
derived data. Existing evidence and recall receipts remain verifiable.

## Phase 7 — Ecosystem integration

### Fuwen

- Query experience during planning or replanning through the portable recall
  API.
- Bind recall to the project/resource and relevant plan, repository, model, and
  policy revisions.
- Append the recall receipt and resulting plan-revision decision evidence.
- Keep the compiled plan immutable and Zhinu execution deterministic.

### Baize

- Record bounded invocation outcomes and exact context-selection evidence.
- Expose contextual statistics first; do not enable self-reinforcing adaptive
  routing until representative evidence and bias evaluation exist.
- Keep provider choice and invocation execution in Baize.

### Guyabano

- Replace direct memory/graph prompting with bounded recall incrementally.
- Preserve current behavior behind a feature switch during dogfooding.
- Show recall provenance and projection lag in diagnostics without requiring a
  user to inspect databases.
- Verify that projection failure degrades context quality but does not corrupt
  or block authoritative workflow/session state.

### Gate

One realistic Guyabano run can be audited from user request through recalled
experience, Fuwen plan, Zhinu execution, Baize calls, artifacts, incidents, and
recovery evidence without any provider-specific type crossing application
boundaries.

## Phase 8 — Second provider and stability review

### Deliverables

- Implement a second real provider only when a consumer needs it. Neo4j is a
  likely graph-oriented candidate; a remote provider is a useful deployment
  test but carries a larger security and consistency surface.
- Validate authentication/configuration ownership, tenant isolation, timeouts,
  cancellation, idempotent retry, freshness, disclosure classification, and
  degraded behavior for remote providers.
- Compare provider capability negotiation and native escape hatches against the
  portable contract; revise the contract before leaving preview if reality
  disproves it.
- Benchmark representative multi-session and multimedia workloads before adding
  write-behind or changing storage recommendations.

### Gate

Two materially different providers and two materially different consumers use
the core contracts without storage-specific leakage. Only then consider API
stability and package graduation.

## Package boundaries

The intended dependency direction is:

```text
Penghou.Hongxian
    evidence + projection/recall contracts
        ^
        +-- Penghou.Hongxian.Sqlite
        |      current Siming/SQLite local composition
        |
        +-- Penghou.Hongxian.LatticeDb (provisional)
        |      disposable local experience provider
        |
        +-- future Neo4j or remote provider
```

Do not split the current SQLite package merely to make the diagram symmetrical.
A future `Penghou.Hongxian.Siming` package is justified only if consumers need a
Siming evidence store without SQLite operational composition, or another
projection provider needs that dependency without taking the existing package.

## Cross-cutting security and operational requirements

- Evidence integrity does not authenticate its source or prove semantic truth.
- Projection and recall inputs are bounded before allocation or provider calls.
- Secrets, unrestricted prompts, and large artifacts remain in owning systems;
  Hongxian stores bounded evidence, references, digests, or protected payloads.
- Derived content cannot weaken the source evidence's disclosure classification.
- Remote providers must not be selectable by event payloads or grant authority
  through metadata.
- Logs are diagnostics; typed health, lag, corruption, truncation, and
  reconciliation results are the operator surface.
- Projection failures and recovery are appended as evidence when operationally
  meaningful; they never roll back the session ledger or Zhinu state.

## Explicitly deferred

- Neo4j and remote provider implementation before the LatticeDB slice proves
  the portable contracts.
- Generic database, graph-query-language, or storage-transaction abstractions.
- Automatic adaptive model routing.
- Cross-session global ordering.
- Treating summaries, embeddings, similarity, or participant claims as truth.
- Replacing Cangjie memory, Hetu code facts, or externally owned artifact stores.
- Removing the existing SQLite timeline projection before parity and rebuild
  evidence exists.

## Immediate Batch 1

**Completed 2026-09-13.** Phase 0 stopped at its safe checkpoint:

1. [x] fix or document the clean-restore source selection used by local validation;
2. [x] add the durable-state authority inventory;
3. [x] add characterization tests around v1/v2 envelope reads, verified projection
   rebuild, outbox delivery, and projection lag;
4. [x] draft the evidence-semantics contract table for Phase 1;
5. [x] update the roadmap with measured findings before changing public APIs.

This batch should be small enough to review independently and should not yet add
LatticeDB, embeddings, or new packages.
