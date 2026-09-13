# ADR-0003: Recall is bounded, evidence-bound, and reproducible

- Status: Accepted
- Date: 2026-09-13

## Context

Experience becomes valuable when Fuwen can use prior outcomes while planning
and Baize can use relevant context when selecting or invoking models. If recall
is an unbounded opaque search, later audits cannot explain why a plan or routing
decision was made, and replay may silently use newer knowledge.

Derived summaries and embeddings improve retrieval but can be stale, lossy, or
model-dependent. They must not replace the evidence supporting them.

## Decision

Recall is a bounded query over a named projection at a declared point in time.
Portable recall requests will support, as applicable:

- session/project/resource scope and an `AsOf` evidence head or checkpoint;
- maximum item, byte, and optional token budgets;
- required evidence/verification strength and freshness;
- bounded filters for participant, event kind, correlation, causation, external
  references, and relevant repository, plan, workflow, model, or policy
  revisions;
- a versioned retrieval/ranking policy.

Recall results will carry source evidence identities, projection identity and
checkpoint, derivation/policy versions, rank or score semantics, and truncation
or unsupported-capability diagnostics. Summaries, embeddings, and learned
relations remain derived data linked to their evidence and derivation inputs.

When recall influences planning, routing, or another consequential action, the
consumer records a compact recall receipt: query fingerprint, selected evidence
identities, projection/checkpoint, retrieval policy version, and the bounded
context or context digest actually supplied. Hongxian preserves the receipt;
Fuwen or Baize remains responsible for the decision.

## Consequences

- A later audit can identify the experience visible to a decision without
  claiming deterministic model output.
- Re-execution can deliberately choose historical (`AsOf`) or current recall
  instead of accidentally mixing them.
- Embedding and summarization providers can change without rewriting evidence.
- Access classification, redaction, and payload-retention constraints propagate
  to derived summaries, embeddings, and recall results.
- Exact ranking reproducibility may require retaining provider/model and policy
  revisions; when a provider cannot guarantee it, the result states that limit.

## Non-goals

- Recall does not make Hongxian a planner, model router, or autonomous agent.
- Recall does not expose chain-of-thought.
- Hongxian does not prescribe one embedding model, graph store, tokenizer, or
  ranking algorithm.
- A similarity score is not treated as confidence that evidence is correct.

