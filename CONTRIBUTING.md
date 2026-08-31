# Contributing

Issues and design discussions are welcome while the API is in preview. Please
keep proposals inside Hongxian's responsibility boundary: durable session
identity, correlation, decisions, incidents, recovery evidence, projections,
and reconciliation contracts.

Before submitting a change, run:

```powershell
dotnet format Penghou.Hongxian.slnx
dotnet test Penghou.Hongxian.slnx --configuration Release
```

Public API changes should include tests and a roadmap or architecture update
when they alter ownership boundaries or persistence semantics.
