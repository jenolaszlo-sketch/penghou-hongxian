# Penghou.Hongxian

[![CI](https://github.com/jenolaszlo-sketch/penghou-hongxian/actions/workflows/ci.yml/badge.svg)](https://github.com/jenolaszlo-sketch/penghou-hongxian/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Penghou.Hongxian)](https://www.nuget.org/packages/Penghou.Hongxian)
[![License](https://img.shields.io/github/license/jenolaszlo-sketch/penghou-hongxian)](LICENSE)

Penghou.Hongxian is a durable session kernel for work that unfolds across
people, models, tools, workflow runs, retries, and process restarts.

A workflow engine knows what should execute. An artifact store knows what was
produced. A memory system knows what has been retained. Hongxian supplies the
missing temporal dimension: **what belongs together, what happened, what
changed, and how the work recovered**.

It is designed for long-running human/AI applications that need more than a
transient chat transcript but do not want a session framework to become another
workflow engine.

## Why Hongxian is different

- **A session outlives a workflow run.** One evolving effort can contain many
  executions, restarts, revisions, decisions, and external systems without
  treating any one provider's run ID as the identity of the work.
- **History stays truthful.** Events, failures, unusual conditions, recovery
  attempts, and verified receipts are append-only. Successful recovery adds new
  evidence; it never erases the failure that made recovery necessary.
- **Authority is explicit.** The ledger is authoritative history. SQLite
  projections are disposable read models that can be rebuilt. External systems
  remain authoritative for their own state.
- **Retries are safe.** Idempotency identities make ambiguous appends
  repeatable, while conflicting reuse is rejected instead of silently producing
  contradictory history.
- **Cross-store work does not pretend to be one transaction.** Cross-store
  participant receipts and forward reconciliation describe what actually
  committed across independent stores and what still needs attention.
- **The core is provider-neutral.** Workflow activities, model calls, tools,
  artifacts, and revisions are correlated through opaque references rather than
  dependencies on a particular orchestration or AI stack.
- **Local sessions are real operational boundaries.** Each session can own an
  independently verifiable SQLite ledger, making retention, export, inspection,
  and deletion boundaries understandable.
- **Sensitive payload handling is deliberate.** Callers classify payloads and
  choose to retain content, retain only its digest, or omit it before immutable
  evidence is written.

The result is a session that can answer not only “what is the current state?”
but also “how did we get here, what evidence supports it, and what happened when
something went wrong?”

## How the pieces fit together

```text
                         application policy
                                │
                                ▼
                    ┌───────────────────────┐
                    │  Penghou.Hongxian     │
                    │  session continuity   │
                    │  correlation/recovery │
                    └───────────┬───────────┘
                                │
              ┌─────────────────┼─────────────────┐
              ▼                 ▼                 ▼
       immutable evidence  rebuildable state  opaque references
       Penghou.Siming      SQLite projections external systems
                                               and artifacts
```

Hongxian deliberately composes focused components rather than absorbing their
responsibilities:

| Component | Responsibility | Relationship to Hongxian |
| --- | --- | --- |
| [Penghou.Hongxian](https://github.com/jenolaszlo-sketch/penghou-hongxian) | Session identity and lifecycle, immutable event envelopes, external-operation correlation, decisions, incidents, recovery evidence, projections, leases, and reconciliation contracts | Owns temporal continuity and cross-system correlation |
| [Penghou.Siming](https://github.com/jenolaszlo-sketch/penghou-siming) | Canonical payload hashing, append-only hash chains, checkpoints, verification, and transactional SQLite ledger persistence | Provides Hongxian's authoritative, tamper-evident session history |
| [Penghou.Zhinu](https://github.com/jenolaszlo-sketch/penghou-zhinu) | Durable workflow execution, steps, retries, fencing, signals, cancellation, and selective restart | Remains authoritative for workflow state; Hongxian correlates its runs and receipts |
| [Penghou.Cangjie](https://github.com/jenolaszlo-sketch/penghou-cangjie) | Revisioned memory, stable logical concepts, and context snapshots | Owns retained knowledge; Hongxian records when and why it was selected or changed |
| [Penghou.Hetu](https://github.com/jenolaszlo-sketch/penghou-hetu) | Code-graph publication, repository identity, dependency knowledge, and impact analysis | Owns code facts; Hongxian correlates the exact publication used by a session action |
| [Penghou.Baize](https://github.com/jenolaszlo-sketch/penghou-baize) | Provider-neutral model execution, structured outputs, tools, streaming, usage, and provenance | Owns model invocation; Hongxian connects invocations to actors, context, revisions, and outcomes |
| [Guyabano](https://github.com/jenolaszlo-sketch/guyabano) | Auditable AI code generation, workspace staging, validation, promotion, and selective regeneration | The first dogfood application and source of Hongxian's proven session requirements |

This split keeps each authority independently useful. Hongxian does not copy a
workflow database, turn every event into memory, store large artifacts, inspect
a code graph, or invoke a model. It preserves the identities, causation,
correlation, revisions, and receipts needed to understand how those systems
participated in one session.

## Packages

| Package | What it provides |
| --- | --- |
| `Penghou.Hongxian` | Provider-neutral session, event, projection, decision, incident, recovery, lease, and cross-store operation contracts |
| `Penghou.Hongxian.Sqlite` | Per-session Siming ledgers, transactional operational catalogs, decision leases, lifecycle and cross-store participant receipts, operation state, and rebuildable projections |

The core contracts do not require a workflow engine. Applications can attach
any external execution system using a provider-qualified operation reference.

## A small example

```csharp
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;

await using var sessions = new SimingSessionEventStore("sessions");
var sessionId = SessionId.New();

var created = await sessions.AppendAsync(new SessionEventRequest(
    sessionId,
    Actor: "user:laszlo",
    EventType: SessionEventTypes.SessionCreated,
    OccurredAt: DateTimeOffset.UtcNow,
    IdempotencyKey: $"session:{sessionId}:created"));

await sessions.AppendAsync(new SessionEventRequest(
    sessionId,
    Actor: "worker:planner",
    EventType: SessionEventTypes.ExecutionStarted,
    OccurredAt: DateTimeOffset.UtcNow,
    CausationId: created.EventId,
    CrossSystemRefs: new Dictionary<string, string>
    {
        ["provider"] = "my-workflow-engine",
        ["operation"] = "planning/42"
    },
    IdempotencyKey: "planning/42:started"));

var page = await sessions.ReadPageAsync(
    new SessionEventPageRequest(sessionId, Limit: 100));
var verifiedHead = await sessions.VerifyChainAsync(sessionId);
```

The same session can later attach another execution, record a failure, append a
recovery plan and verified receipt, rebuild its projection, and prove the
ordered ledger without rewriting the earlier history.

## Architectural boundaries

Hongxian records facts and coordination evidence. Applications still decide
what those facts mean and what should happen next.

Hongxian does not:

- schedule or restart workflow steps;
- choose a domain recovery action;
- authenticate actors or authorize commands;
- store source code, model transcripts, or large artifacts;
- interpret code graphs or memory;
- roll back independent external systems;
- claim that a projection is authoritative history.

Actor identity and occurrence time are host-supplied claims. The ledger's
commit time is the authoritative audit clock. If an append commits but a
projection update fails, the result is projection lag—not permission to append
the event again as though nothing happened.

## Status and direction

Hongxian is experimental and targets .NET 10 for its first preview. Its initial
contracts were extracted from working Guyabano session and recovery paths, and
the next step is package-backed Guyabano integration. Planned work includes
bounded query and lifecycle APIs, participant collaboration, optional execution
adapters, and validation through a substantially different media-generation
consumer.

See the [architecture](docs/architecture.md) for authority and failure
semantics, and the [roadmap](docs/roadmap.md) for current milestones and
stability criteria.

## Build

```powershell
dotnet build Penghou.Hongxian.slnx --configuration Release
dotnet test Penghou.Hongxian.slnx --configuration Release --no-build
dotnet run --project samples/Penghou.Hongxian.Example
```

## Security model

Hash chaining makes changes to an existing ledger detectable, but an actor with
full storage access could replace the entire ledger. Trusted checkpoints or
external anchoring are required to detect rollback or wholesale replacement.
Hongxian preserves actor claims and provenance; it does not authenticate them.

Applications should keep secrets and unrestricted model transcripts out of the
session ledger, store large content in its authoritative system, and record
bounded references and digests instead.

## License

MIT
