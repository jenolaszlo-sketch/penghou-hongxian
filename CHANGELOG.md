# Changelog

## Unreleased

- Executable Fuwen/Baize host integration proofs and a parked-Neo4j decision.

## 0.1.0-preview.3 (candidate, unreleased)

- New `Penghou.Hongxian.LatticeDb` provider: disposable graph with exact
  lookup, bounded traversal, projection-scoped BM25 recall, checkpoints,
  and delete/replay equivalence.
- Portable bounded recall: retrieval policies, item/byte/token budgets,
  canonical query fingerprints, and ledger-persisted recall receipts.
- Summary/embedding derivation contracts with host-supplied generators,
  portable cosine vector and RRF hybrid recall on the in-memory reference.
- Compatibility against preview.2 records one intentional break: the
  `SessionEventRequest` constructor and `Deconstruct` gained the optional
  envelope v3 evidence parameter. Everything else is additive.

## 0.1.0-preview.2 (published)

- Provider-neutral session kernel with per-session Siming ledgers, SQLite
  projections, catalog, decision leases, lifecycle outbox receipts,
  cross-store operation receipts, and forward reconciliation.
- Integrity and neutrality hardening: expected-head conditional appends,
  fencing leases, versioned migrations, payload upcasters, participant
  attribution, and the consistency audit.

## 0.1.0-preview.1 (published)

- Initial packaging and extracted session behavior.
