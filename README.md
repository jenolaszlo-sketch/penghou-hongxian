# Penghou.Hongxian

[![CI](https://github.com/jenolaszlo-sketch/penghou-hongxian/actions/workflows/ci.yml/badge.svg)](https://github.com/jenolaszlo-sketch/penghou-hongxian/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Penghou.Hongxian)](https://www.nuget.org/packages/Penghou.Hongxian)
[![License](https://img.shields.io/github/license/jenolaszlo-sketch/penghou-hongxian)](LICENSE)

Penghou.Hongxian is a durable session kernel for long-running human and AI
work. It keeps identity, immutable activity, decisions, incidents, recovery
evidence, and current projections connected across retries, process loss, and
multiple external execution systems.

Hongxian provides the temporal dimension that an artifact store or workflow
engine intentionally does not own. It records what belongs together and what
happened over time; it does not schedule workflows or define application
policy.

## Status

The reusable kernel has been extracted from Guyabano after several rounds of
real code-generation dogfooding. The API remains experimental and the first
package will be a preview. The current slice includes provider-neutral event,
incident, recovery, projection, catalog, decision-lease, lifecycle-receipt, and
cross-store reconciliation contracts plus per-session Siming/SQLite
persistence. Guyabano integration waits for the published package rather than
using a sibling project reference.

## Packages

| Package | Responsibility |
| --- | --- |
| `Penghou.Hongxian` | Session identity, external-operation correlation, immutable events, incidents, recovery and participant receipts, projections, leases, and reconciliation contracts |
| `Penghou.Hongxian.Sqlite` | Per-session Siming ledgers, transactional operational catalogs, decision leases, cross-store operation state, and rebuildable projections |

Planned query/lifecycle APIs, optional adapters, Guyabano integration, and
second-consumer validation are tracked in the [roadmap](docs/roadmap.md).

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

The first preview targets .NET 10 to preserve the proven Guyabano behavior.
Broader target-framework support is a pre-stability roadmap item.

Run the standalone example with:

```powershell
dotnet run --project samples/Penghou.Hongxian.Example
```

See the [architecture](docs/architecture.md) for authority, projection, and
failure semantics.

## Publishing

Publishing follows the same trusted-publishing flow as Penghou.Baize. Configure
the NuGet.org trusted-publishing policies for both package IDs against:

```text
Repository owner: jenolaszlo-sketch
Repository:       penghou-hongxian
Workflow:         publish.yml
Environment:      (none)
```

Add the repository secret `NUGET_USER` containing the NuGet.org account name.
No long-lived NuGet API key is stored in GitHub; `NuGet/login@v1` exchanges the
workflow's OIDC identity for a temporary key.

To publish the version in `Directory.Build.props`, run **Publish to NuGet** from
GitHub Actions. For a release with an explicit version, push a matching `v*`
tag, for example `v0.1.0-preview.1`; the tag version overrides the project
version. The workflow packs and audits both packages, publishes `.nupkg` and
`.snupkg` files, and safely skips an already-published version.

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
