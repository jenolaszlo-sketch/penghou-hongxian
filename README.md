# Penghou.Hongxian

Penghou.Hongxian is a durable session kernel for long-running human and AI
work. It keeps identity, immutable activity, decisions, incidents, recovery
evidence, and current projections connected across retries, process loss, and
multiple external execution systems.

Hongxian provides the temporal dimension that an artifact store or workflow
engine intentionally does not own. It records what belongs together and what
happened over time; it does not schedule workflows or define application
policy.

## Status

The project is being extracted from Guyabano after several rounds of real code
generation dogfooding. The API is experimental and the first package will be a
preview. The initial slice contains provider-neutral event, incident, recovery,
and projection contracts plus per-session Siming/SQLite persistence.

## Packages

| Package | Responsibility |
| --- | --- |
| `Penghou.Hongxian` | Session identity, immutable event envelopes, incidents, recovery receipts, and projections |
| `Penghou.Hongxian.Sqlite` | Per-session Siming ledgers and rebuildable SQLite projections |

Planned adapters and catalog functionality are tracked in the
[roadmap](docs/roadmap.md).

## Boundaries

Hongxian owns continuity and correlation. Applications retain domain policy,
artifact meaning, recovery handlers, authorization, and large payloads.

- [Penghou.Siming](https://github.com/jenolaszlo-sketch/penghou-siming) owns the
  cryptographic ledger format and append-only SQLite persistence.
- Workflow engines such as Penghou.Zhinu remain optional external systems.
- Hongxian records verified recovery receipts but does not choose or schedule
  domain recovery actions.
- Actor identity is a host-supplied claim. Hongxian preserves it but does not
  authenticate it.

## Build

```powershell
dotnet build Penghou.Hongxian.slnx --configuration Release
dotnet test Penghou.Hongxian.slnx --configuration Release --no-build
```

The current extraction targets .NET 10 to preserve the proven Guyabano
implementation. Broader target-framework support is a pre-stability roadmap
item.

Run the standalone example with:

```powershell
dotnet run --project samples/Penghou.Hongxian.Example
```

See the [architecture](docs/architecture.md) for authority, projection, and
failure semantics.

## Security model

Session events are persisted in independent Siming ledgers and can be verified
as ordered, tamper-evident histories. Rebuildable projections are deliberately
not authoritative. Hongxian cannot prevent an actor with full storage access
from replacing an entire ledger; trusted checkpoints or external anchoring are
required to detect rollback or wholesale replacement.

Payload retention is explicit. Applications should store bounded references,
identities, and digests instead of secrets or unrestricted model transcripts.

## License

MIT
