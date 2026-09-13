# ADR-0001: Siming evidence streams are authoritative

- Status: Accepted
- Date: 2026-09-13

## Context

Hongxian began as a durable-session kernel. Its useful long-term role is
broader: preserve the temporal evidence from which experience can be projected
and recalled across people, models, tools, plans, executions, and artifacts.

A separate mutable session aggregate would create another authority that could
disagree with the append-only event history. Calling every recorded statement
"truth" would also overstate what cryptographic integrity proves. A valid
ledger proves ordering and integrity; it does not prove that a participant's
claim was correct.

## Decision

Each session is an append-only Siming evidence stream and a consistency
boundary. The stream is authoritative for the evidence that identified sources
asserted and Hongxian accepted. Session lifecycle and current state are derived
views over that evidence, not a second mutable source of truth.

Every durable evidence envelope must make the following independently
understandable and evolvable:

- evidence identity and session identity;
- source/participant attribution;
- occurrence time claimed by the source and commit time assigned by the ledger;
- correlation, causation, and bounded external references;
- envelope and payload schema versions;
- measurement or assertion kind, verification status, and optional confidence
  where the domain can define it meaningfully;
- payload-retention treatment and integrity identity.

State-dependent appends use expected-head optimistic concurrency. Idempotency
protects ambiguous retries but does not replace concurrency control.

Work spanning Zhinu, artifact stores, or other authorities uses durable outbox
delivery, idempotent receipts, and forward reconciliation. It does not pretend
that the stores share a distributed transaction. Failures, gaps, late results,
and successful recovery remain append-only evidence.

## Consequences

- A session can outlive any workflow run, process, UI, or projection database.
- Current state, timelines, relations, summaries, and experience graphs must be
  rebuildable from verified evidence and explicit external checkpoints.
- Projection loss is recoverable; authoritative evidence loss is not.
- Closing or archiving a session does not rewrite its history.
- Retention must distinguish payload treatment from ledger lifecycle. Retained
  ledgers remain verifiable after checkpointing or archival.
- Consumers must describe recorded statements as evidence or assertions unless
  an owning authority independently verifies their semantics.

## Non-goals

- Hongxian does not make participant claims factually true.
- Hongxian does not replay or replace Zhinu's authoritative workflow state.
- Hongxian does not roll back independently committed external work.
- Hongxian does not own artifact bytes, model transcripts, encryption keys, or
  application authorization policy.

