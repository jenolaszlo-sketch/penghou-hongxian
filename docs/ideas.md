# Hongxian ideas to revisit

These suggestions from the September 2026 external review are not committed
roadmap work. Revisit them when a concrete consumer needs them.

- **Standalone `hongxian` CLI.** The existing typed query, audit, and delivery
  APIs are the reusable surface. A CLI could help operators later, once its
  authentication, deployment, and provider scope are clear.
- **Fluent recall request builder.** The bounded request constructor already
  validates inputs. Add a builder only if real call sites show repeated,
  hard-to-read construction.
- **Generic cross-store transaction coordinator.** Current evidence outboxes,
  participant receipts, and forward reconciliation cover partial success
  without claiming a distributed transaction. A generic coordinator needs a
  concrete case that these mechanisms cannot express.
- **GlobalAgentMemory facade.** Cross-session views are already planned as
  projections over independently verified session heads. Consider a named
  facade only after project scoping, access control, and retention semantics
  are proven by a consumer.
- **File-change-driven fact invalidation policy.** The roadmap already calls
  for source-linked invalidation of derived summaries and embeddings. Watching
  repository files and deciding which facts are stale belongs with a host or
  application-specific projector until a portable evidence rule emerges.
- **Dedicated vector embedding provider interface.** Host-supplied derivation
  generators and portable vector/hybrid recall already exist. Add another
  interface only if LatticeDB vector indexing or a second consumer exposes a
  missing contract.
