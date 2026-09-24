# Ecosystem integration guide

How hosts connect Fuwen, Baize, Zhinu, and Guyabano through Hongxian
without creating package dependencies in the wrong direction.

## Rules

- Hongxian records evidence; applications decide what it means.
- Fuwen, Baize, and Zhinu take no Hongxian package reference. Adapters live
  in the host application, which references both sides.
- Guyabano references Hongxian NuGet packages, never sibling project
  references.
- External systems stay authoritative for their own state. Hongxian keeps
  opaque references and receipts, never copies of their databases.

## Fuwen hosts: planning evidence and recall

Fuwen owns immutable plan revisions; Hongxian correlates the evidence around
them. A host adapter:

1. Opens one session per planning effort with `HongxianSqliteStoreSet` (or a
   `SimingSessionEventStore` for event-only use).
2. Records plan proposals, replan requests, approvals, and activations as
   application-defined events carrying an
   `ExternalOperationReference("fuwen", planRevisionId)` plus bounded
   `CrossSystemRefs` for repository, model, and policy revisions.
3. Recalls experience through `ExperienceBoundedRecall.ExecuteAsync` with an
   explicit `ExperienceRetrievalPolicy`, then appends the receipt with
   `ExperienceRecallReceipts.AppendRecallReceiptAsync` so a later audit can
   reproduce the fingerprint, the selected evidence, and the supplied-context
   digest.
4. Uses `ExpectedHead` conditional appends for state-dependent decisions
   (for example, activating a generation only if the ledger head is still
   the approved one).

The compiled plan stays immutable and Zhinu execution stays deterministic;
Hongxian never validates a plan or authorizes a transition.

## Baize hosts: invocation outcomes and advisory reads

Baize owns provider choice and invocation; Hongxian preserves what happened:

1. Append one bounded event per consequential invocation with a
   provider-qualified `ExternalOperationReference("baize", invocationId)`,
   normalized usage/cost/latency/outcome payloads, and digests instead of
   prompts, transcripts, or credentials.
2. Read advisory context with bounded recall and keep the query fingerprint
   next to the routing inputs. Reproduce the inputs later from the receipt,
   not by re-running the query against newer data.
3. Keep historical signals advisory: they never override current capability,
   availability, budget, or authorization checks, and similarity scores are
   never presented as factual confidence.

The Baize package itself takes no Hongxian reference; the host owns the
mapping and the routing decision.

## Guyabano mapping: package-backed sessions

1. Reference `Penghou.Hongxian` and `Penghou.Hongxian.Sqlite` NuGet packages.
2. Map Zhinu run IDs, workspace revisions, and Guyabano event vocabulary to
   `ExternalOperationReference` values and application-defined events in a
   Guyabano-owned mapping layer. Workspace policy, recovery handlers, and
   product explanations stay in Guyabano.
3. Deliver cross-store evidence through the durable outbox plus forward
   reconciliation; never claim a distributed transaction with Zhinu.
4. Prove package-backed parity (full suite plus a realistic recovery
   dogfood flow with restart and projection rebuild) before removing the
   temporary duplicate kernel.

## Preview.3 contents for integrators

Preview.3 adds the LatticeDB experience provider, bounded recall with
canonical fingerprints and receipts, and summary/embedding derivation
contracts. Compatibility against preview.2 records one intentional break:
the `SessionEventRequest` constructor and `Deconstruct` gained the optional
`Evidence` parameter for envelope v3 semantics. Everything else is additive.
