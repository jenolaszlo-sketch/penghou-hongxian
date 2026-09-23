param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,
    [Parameter(Mandatory = $true)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$packageSource = (Resolve-Path $PackageDirectory).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "hongxian-consumer-$([Guid]::NewGuid().ToString('N'))"
$coreDir = Join-Path $temporaryRoot 'core'
$latticeDir = Join-Path $temporaryRoot 'lattice'
$corePackages = Join-Path $coreDir 'packages'
$latticePackages = Join-Path $latticeDir 'packages'
$coreProjectPath = Join-Path $coreDir 'PackedConsumer.csproj'
$coreProgramPath = Join-Path $coreDir 'Program.cs'
$latticeProjectPath = Join-Path $latticeDir 'PackedLatticeConsumer.csproj'
$latticeProgramPath = Join-Path $latticeDir 'Program.cs'
try {
    New-Item -ItemType Directory -Path $coreDir | Out-Null
    New-Item -ItemType Directory -Path $latticeDir | Out-Null
    $escapedSource = [Security.SecurityElement]::Escape($packageSource)
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <NuGetAudit>true</NuGetAudit>
    <RestoreSources>$escapedSource;https://api.nuget.org/v3/index.json</RestoreSources>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Penghou.Hongxian" Version="$Version" />
    <PackageReference Include="Penghou.Hongxian.Sqlite" Version="$Version" />
  </ItemGroup>
</Project>
"@ | Set-Content -Path $coreProjectPath -Encoding utf8

    @'
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "hongxian-packed-consumer", Guid.NewGuid().ToString("N"));
try
{
    await using var hongxian = new HongxianSqliteStoreSet(new HongxianSqliteOptions
    {
        RootPath = root,
        Pooling = false,
        MaximumCachedLedgers = 2
    });
    var session = await hongxian.SessionStore.CreateAsync("package-test", "resource/1");
    await hongxian.CatalogEvidence.DispatchPendingAsync();
    var append = await hongxian.EventDeliveryStore.AppendWithDeliveryAsync(
        new SessionEventRequest(
            session.Id,
            SessionParticipantAttribution.System("packed-consumer", "package-test"),
            SessionEventTypes.ExecutionStarted,
            DateTimeOffset.UtcNow,
            IdempotencyKey: $"session:{session.Id}:started"));
    var head = await hongxian.EventStore.VerifyChainAsync(session.Id);
    var projection = await hongxian.ProjectionStore.GetAsync(session.Id);
    var audit = await hongxian.ConsistencyAudit.InspectAsync(session.Id);
    if (head is null ||
        projection?.AppliedSequence != 2 ||
        append.ProjectionDelivery.Outcome != SessionProjectionDeliveryOutcome.Applied ||
        audit.Health != SessionConsistencyHealth.Healthy)
        throw new InvalidOperationException("Packed Hongxian consumer did not compose, persist, project, and audit its session.");
}
finally
{
    if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}
'@ | Set-Content -Path $coreProgramPath -Encoding utf8

    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <NuGetAudit>true</NuGetAudit>
    <RestoreSources>$escapedSource;https://api.nuget.org/v3/index.json</RestoreSources>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Penghou.Hongxian" Version="$Version" />
    <PackageReference Include="Penghou.Hongxian.LatticeDb" Version="$Version" />
  </ItemGroup>
</Project>
"@ | Set-Content -Path $latticeProjectPath -Encoding utf8

    @'
using System.Text.Json;
using Penghou.Hongxian;
using Penghou.Hongxian.LatticeDb;

using var provider = new LatticeDbExperienceProvider(new LatticeDbExperienceProviderOptions
{
    Location = LatticeDbLocationKind.Memory,
    Lock = false
});
var descriptor = new ExperienceProjectionDescriptor(
    ExperienceProjectionId.New(), "latticedb", "packed-consumer", 1, "policy-1");

static ExperienceEvidenceReference Evidence() =>
    new(SessionId.New(), "ledger", 1, Guid.CreateVersion7(), "hash");

static ExperienceEntity Entity(ExperienceProjectionDescriptor projection, string stableKey, string title) =>
    new(
        ExperienceEntityId.CreateDeterministic(projection.ProjectionId, "task", stableKey),
        "task",
        new ExperienceProvenance(
            projection,
            ExperienceDerivationId.CreateDeterministic(projection.ProjectionId, "packed", stableKey)),
        new Dictionary<string, JsonElement>
        {
            ["title"] = JsonSerializer.SerializeToElement(title)
        },
        evidence: [Evidence()]);

var first = Entity(descriptor, "task:first", "first packed durable lesson");
var second = Entity(descriptor, "task:second", "second packed durable lesson");
var relation = new ExperienceRelation(
    ExperienceRelationId.CreateDeterministic(
        descriptor.ProjectionId, first.Id, second.Id, "depends-on", "edge:packed"),
    first.Id,
    second.Id,
    "depends-on",
    new ExperienceProvenance(
        descriptor,
        ExperienceDerivationId.CreateDeterministic(descriptor.ProjectionId, "packed-relation", "edge:packed")),
    evidence: [Evidence()]);

if ((await provider.UpsertEntityAsync(first)).Outcome != ExperienceModelWriteOutcome.Applied)
    throw new InvalidOperationException("Packed LatticeDB consumer did not persist the first entity.");
if ((await provider.UpsertEntityAsync(second)).Outcome != ExperienceModelWriteOutcome.Applied)
    throw new InvalidOperationException("Packed LatticeDB consumer did not persist the second entity.");
if ((await provider.UpsertRelationAsync(relation)).Outcome != ExperienceModelWriteOutcome.Applied)
    throw new InvalidOperationException("Packed LatticeDB consumer did not persist the relation.");
if ((await provider.UpsertEntityAsync(first)).Outcome != ExperienceModelWriteOutcome.AlreadyPresent)
    throw new InvalidOperationException("Packed LatticeDB consumer replay was not idempotent.");

var exact = await provider.GetEntityAsync(
    new ExperienceEntityLookupRequest(descriptor.ProjectionId, first.Id));
if (exact.Items.Count != 1 || exact.Items[0].Id != first.Id)
    throw new InvalidOperationException("Packed LatticeDB consumer exact lookup failed.");

var lexical = await provider.SearchLexicalAsync(
    new ExperienceLexicalSearchRequest(descriptor.ProjectionId, "packed durable"));
if (lexical.Items.Count != 2)
    throw new InvalidOperationException("Packed LatticeDB consumer lexical search failed.");

var traversal = await provider.TraverseAsync(
    new ExperienceRelationTraversalRequest(descriptor.ProjectionId, first.Id));
if (traversal.Items.Count != 1 || traversal.Items[0].Id != relation.Id)
    throw new InvalidOperationException("Packed LatticeDB consumer traversal failed.");

var position = new ExperienceProjectionPosition([
    new SessionEvidencePosition(SessionId.New(), new SessionLedgerHead("ledger", 1, "hash"))
]);
var checkpoint = await provider.AdvanceAsync(
    new AdvanceExperienceProjectionRequest(descriptor, position, 0));
if ((await provider.GetAsync(descriptor.ProjectionId))?.Version != checkpoint.Version)
    throw new InvalidOperationException("Packed LatticeDB consumer checkpoint was not durable.");

await provider.DeleteProjectionAsync(descriptor);
if ((await provider.GetEntityAsync(
    new ExperienceEntityLookupRequest(descriptor.ProjectionId, first.Id))).Items.Count != 0)
    throw new InvalidOperationException("Packed LatticeDB consumer delete did not clear the projection.");

if ((await provider.UpsertEntityAsync(first)).Outcome != ExperienceModelWriteOutcome.Applied)
    throw new InvalidOperationException("Packed LatticeDB consumer replay after delete failed.");
if ((await provider.UpsertEntityAsync(second)).Outcome != ExperienceModelWriteOutcome.Applied)
    throw new InvalidOperationException("Packed LatticeDB consumer replay after delete failed.");
if ((await provider.UpsertRelationAsync(relation)).Outcome != ExperienceModelWriteOutcome.Applied)
    throw new InvalidOperationException("Packed LatticeDB consumer replay after delete failed.");
var replayed = await provider.SearchLexicalAsync(
    new ExperienceLexicalSearchRequest(descriptor.ProjectionId, "packed durable"));
if (replayed.Items.Count != 2)
    throw new InvalidOperationException("Packed LatticeDB consumer delete/replay was not equivalent.");
'@ | Set-Content -Path $latticeProgramPath -Encoding utf8

    # Use isolated package folders so a locally cached, unpublished dependency
    # cannot make the packed-consumer checks pass when public restore would fail.
    dotnet restore $coreProjectPath --packages $corePackages --force --force-evaluate --no-cache
    if ($LASTEXITCODE -ne 0) { throw "Packed core consumer restore failed." }
    dotnet run --project $coreProjectPath --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Packed core consumer execution failed." }

    # Core and SQLite consumers must remain free of LatticeDB native assets.
    $coreTransitive = dotnet list $coreProjectPath package --include-transitive --no-restore 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Packed core consumer package audit failed." }
    if ($coreTransitive -match '(?i)lattice') {
        throw "Core/SQLite consumer unexpectedly depends on LatticeDB assets: $coreTransitive"
    }
    $leakedLatticeFiles = Get-ChildItem -Path $corePackages -Recurse -Filter '*attice*' -ErrorAction SilentlyContinue |
        Select-Object -First 5
    if ($null -ne $leakedLatticeFiles) {
        $names = ($leakedLatticeFiles | ForEach-Object { $_.FullName }) -join '; '
        throw "Core/SQLite consumer package cache contains LatticeDB assets: $names"
    }
    dotnet list $coreProjectPath package --vulnerable --include-transitive --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Packed core consumer vulnerability audit failed." }

    dotnet restore $latticeProjectPath --packages $latticePackages --force --force-evaluate --no-cache
    if ($LASTEXITCODE -ne 0) { throw "Packed LatticeDB consumer restore failed." }
    dotnet run --project $latticeProjectPath --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Packed LatticeDB consumer execution failed." }

    # Prove the isolation check is not vacuous: the LatticeDB consumer must flow
    # the native dependency that the core consumer must not acquire.
    $latticeTransitive = dotnet list $latticeProjectPath package --include-transitive --no-restore 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Packed LatticeDB consumer package audit failed." }
    if ($latticeTransitive -notmatch '(?i)LatticeDbSharp') {
        throw "Packed LatticeDB consumer unexpectedly lacks its native dependency: $latticeTransitive"
    }
    dotnet list $latticeProjectPath package --vulnerable --include-transitive --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Packed LatticeDB consumer vulnerability audit failed." }
}
finally {
    if (Test-Path $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
