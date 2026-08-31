# Penghou.Hongxian roadmap

## Goal

Extract the proven generic session mechanics from Guyabano without importing
code-generation policy, workspace layout, or dependencies on Zhinu, Hetu,
Cangjie, or Baize.

## Milestone 0 — Repository and boundary

- [x] Create the public package and solution structure.
- [x] Record ownership boundaries and security claims.
- [x] Preserve the first proven event, recovery, and projection tests.
- [x] Add architecture documentation and a minimal independent sample.
- [ ] Add packed-consumer validation.

## Milestone 1 — Durable event and projection kernel

- [x] Extract session identity and immutable event envelopes.
- [x] Extract incidents, recovery plans, attempts, and verified receipts.
- [x] Extract current-state and timeline projection contracts.
- [x] Compose one independent Siming SQLite ledger per session.
- [x] Keep SQLite current-state projections rebuildable from the ledger.
- [x] Remove remaining assumptions that an external operation is a workflow or
  that a revision is a source-code workspace.
- [ ] Add provider conformance tests for session event and projection stores.

## Milestone 2 — Operational catalog and coordination

- [x] Extract the concurrency-safe session catalog behind provider-neutral
  interfaces.
- [x] Model provider-qualified external operation references instead of bare
  workflow GUIDs.
- [x] Extract renewable decision leases and optimistic concurrency.
- [x] Extract durable cross-store operation receipts and reconciliation status.
- [x] Keep application-authored recovery explanations outside the kernel.

## Milestone 3 — Application integration

- [ ] Replace Guyabano's internal session projects with Hongxian references.
- [ ] Keep Guyabano event vocabulary, workspace policy, and Penghou adapters in
  Guyabano.
- [ ] Re-run the complete Guyabano suite and realistic recovery dogfood flow.
- [ ] Prove restart and projection reconstruction after process loss.

## Milestone 4 — Second-consumer validation

- [ ] Build a Baize media-generation/batching profile using opaque external
  operation and artifact references.
- [ ] Resume partial batches without regenerating acknowledged outputs.
- [ ] Record variants, selection, retries, partial success, and media lineage
  without adding media-specific types to Hongxian.

## Milestone 5 — Preview package readiness

- [ ] Add CI build, format, test, pack, and isolated-consumer verification.
- [ ] Add trusted-publishing workflow consistent with the Penghou ecosystem.
- [ ] Decide whether to multi-target .NET 8 before the first public preview.
- [ ] Publish `0.1.0-preview.1` only after Guyabano and the independent sample
  consume packed artifacts successfully.

## Stability criteria

Do not graduate from preview until:

- at least two substantially different applications use the kernel;
- public contracts contain no Guyabano or provider-specific types;
- replacing SQLite or the external-execution adapter does not change session
  policy;
- process-loss, idempotency, concurrency, projection rebuild, and tamper
  detection semantics are documented and tested;
- the package has completed at least one compatibility review using a prior
  release as the package-validation baseline.
