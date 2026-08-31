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
| [Penghou.Baize](https://github.com/jenolaszlo-sketch/penghou-baize) | Provider-neutral model execution, structured outputs, tools, streaming, usage, and provenance | Owns model invocation; Hongxian connects invocations to participants, context, revisions, and outcomes |
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
using System.Text.Json;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;

await using var sessions = new SimingSessionEventStore("sessions");
var sessionId = SessionId.New();

var created = await sessions.AppendAsync(new SessionEventRequest(
    sessionId,
    Participant: SessionParticipantAttribution.Human("laszlo", "example"),
    EventType: SessionEventTypes.SessionCreated,
    OccurredAt: DateTimeOffset.UtcNow,
    IdempotencyKey: $"session:{sessionId}:created"));

await sessions.AppendAsync(new SessionEventRequest(
    sessionId,
    Participant: SessionParticipantAttribution.Agent("planner", "example"),
    EventType: SessionEventTypes.ExecutionStarted,
    OccurredAt: DateTimeOffset.UtcNow,
    CausationId: created.EventId,
    CrossSystemRefs: new Dictionary<string, string>
    {
        ["provider"] = "my-workflow-engine",
        ["operation"] = "planning/42"
    },
    IdempotencyKey: "planning/42:started"));

var message = await sessions.AppendAsync(
    new SessionEventRequest(
        sessionId,
        Participant: SessionParticipantAttribution.Human("laszlo", "example"),
        EventType: SessionEventTypes.UserMessage,
        OccurredAt: DateTimeOffset.UtcNow,
        PayloadSchema: new SessionPayloadSchema("example.user-message", 1)),
    new { text = "Please add authentication" });

var payload = message.ReadPayload<JsonElement>();

var page = await sessions.ReadPageAsync(
    new SessionEventPageRequest(sessionId, Limit: 100));
var verifiedHead = await sessions.VerifyChainAsync(sessionId);
```

The same session can later attach another execution, record a failure, append a
recovery plan and verified receipt, rebuild its projection, and prove the
ordered ledger without rewriting the earlier history.

For applications using the standard local provider, `HongxianSqliteStoreSet`
opens the complete, consistently configured surface from one root:

```csharp
await using var hongxian = new HongxianSqliteStoreSet(
    new HongxianSqliteOptions { RootPath = "hongxian-data" });

var session = await hongxian.SessionStore.CreateAsync("project", "workspace/1");
await hongxian.CatalogEvidence.DispatchPendingAsync();

var audit = await hongxian.ConsistencyAudit.InspectAsync(session.Id);
```

The store set exposes both concrete SQLite implementations and the
provider-neutral event, projection, catalog, lease, operation, and inspection
interfaces. It is composition convenience, not a service locator or a new
authority boundary.

Callers that need immediate delivery visibility can use
`ISessionEventDeliveryStore.AppendWithDeliveryAsync`. Its result separates the
authoritative ledger commit from `Applied`, `Lagging`, or `NotConfigured`
projection delivery. The consistency audit later correlates the verified
ledger head, projection cursor, catalog version, incomplete operations,
participant failures, evidence outboxes, and the best-known decision lease.
The audit is intentionally a non-atomic diagnostic snapshot and never claims a
distributed transaction.

Typed and `JsonElement` appends first become a JSON tree, then the SQLite
provider uses Siming's canonical JSON contract. Object-property order,
insignificant whitespace, and equivalent JSON number spelling therefore do not
change payload identity. `DigestOnly` records the canonical JSON digest without
content; `Omit` records neither content nor a digest, so omitted values cannot
participate in content-based idempotency. Application payload schemas are
versioned separately from the Hongxian envelope and SQLite schema. Registered
upcasters transform payloads only while reading or projecting and never rewrite
immutable ledger history.

## Architectural boundaries

Hongxian records facts and coordination evidence. Applications still decide
what those facts mean and what should happen next.

Participant attribution is a structured, immutable host claim: kind, provider,
stable opaque subject, and an optional display-name snapshot. It makes events
usefully attributable across humans, agents, models, tools, and systems without
pretending to authenticate them. Public IDs and external-operation references
round-trip as stable JSON strings and support `Parse`, `TryParse`, and span
formatting. Portable bounds are exposed through `SessionContractLimits` and
enforced before event, catalog, recovery, or cross-store persistence.

Hongxian does not:

- schedule or restart workflow steps;
- choose a domain recovery action;
- authenticate participants or authorize commands;
- store source code, model transcripts, or large artifacts;
- interpret code graphs or memory;
- roll back independent external systems;
- claim that a projection is authoritative history.

Participant attribution and occurrence time are host-supplied claims. The ledger's
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

Hash chaining makes changes to an existing ledger detectable, but an attacker with
full storage access could replace the entire ledger. Trusted checkpoints or
external anchoring are required to detect rollback or wholesale replacement.
Hongxian preserves participant claims and provenance; it does not authenticate them.

Applications should keep secrets and unrestricted model transcripts out of the
session ledger, store large content in its authoritative system, and record
bounded references and digests instead.

SQLite schemas are versioned independently for the catalog, projections, and
cross-store operation store. Upgrades are serialized and transactional, and a
library version refuses to open a component written by a newer schema. The host
is responsible for backing up authoritative catalog, operation, outbox, and
ledger files before upgrading. Projections are rebuildable; the other stores
are not. Migration from the first preview discards old decision-lease rows
because their GUID tokens cannot provide the fencing guarantee of the current
monotonic token contract.

## License

MIT
