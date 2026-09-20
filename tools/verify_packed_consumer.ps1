param(
    [Parameter(Mandatory = $true)] [string] $PackageDirectory,
    [Parameter(Mandatory = $true)] [string] $Version
)

$ErrorActionPreference = 'Stop'
$packageSource = (Resolve-Path $PackageDirectory).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "hongxian-consumers-$([Guid]::NewGuid().ToString('N'))"
$consumerPackages = Join-Path $temporaryRoot 'packages'

function Write-ConsumerProject([string] $Name, [string] $References, [string] $Program) {
    $directory = Join-Path $temporaryRoot $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    $source = [Security.SecurityElement]::Escape($packageSource)
    $References = $References.Replace('VERSION', $Version)
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <NuGetAudit>true</NuGetAudit>
    <RestoreSources>$source;https://api.nuget.org/v3/index.json</RestoreSources>
  </PropertyGroup>
  <ItemGroup>$References</ItemGroup>
</Project>
"@ | Set-Content (Join-Path $directory "$Name.csproj") -Encoding utf8
    $Program | Set-Content (Join-Path $directory 'Program.cs') -Encoding utf8
    return Join-Path $directory "$Name.csproj"
}

function Restore-And-Audit([string] $ProjectPath, [string[]] $ForbiddenPackageIds) {
    dotnet restore $ProjectPath --packages $consumerPackages --force --force-evaluate --no-cache
    if ($LASTEXITCODE -ne 0) { throw "Packed consumer restore failed: $ProjectPath" }
    $assetsPath = Join-Path (Split-Path $ProjectPath) 'obj/project.assets.json'
    $assets = Get-Content $assetsPath -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties.Name) {
        foreach ($forbidden in $ForbiddenPackageIds) {
            if ($library -like "$forbidden/*") { throw "Consumer $ProjectPath carries forbidden package $library." }
        }
    }
    if ($ForbiddenPackageIds.Count -gt 0) {
        foreach ($target in $assets.targets.PSObject.Properties) {
            foreach ($libraryEntry in $target.Value.PSObject.Properties) {
                $library = $libraryEntry.Value
                foreach ($assetKind in @('runtime', 'native', 'runtimeTargets')) {
                    $assetProperty = $library.PSObject.Properties[$assetKind]
                    if ($null -ne $assetProperty -and $null -ne $assetProperty.Value) {
                        $paths = @($assetProperty.Value.PSObject.Properties.Name)
                        if ($paths -match '(?i)(latticedb|latticedbsharp)') { throw "Consumer $ProjectPath carries forbidden LatticeDB/native runtime assets." }
                    }
                }
            }
        }
    }
    dotnet run --project $ProjectPath --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Packed consumer execution failed: $ProjectPath" }
    dotnet list $ProjectPath package --vulnerable --include-transitive
    if ($LASTEXITCODE -ne 0) { throw "Packed consumer vulnerability audit failed: $ProjectPath" }
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $core = Write-ConsumerProject 'CoreConsumer' '<PackageReference Include="Penghou.Hongxian" Version="VERSION" />' @'
using Penghou.Hongxian;
Console.WriteLine($"Core consumer loaded {typeof(ExperienceProjectionDescriptor).Assembly.GetName().Name}.");
'@
    $sqlite = Write-ConsumerProject 'SqliteConsumer' '<PackageReference Include="Penghou.Hongxian.Sqlite" Version="VERSION" />' @'
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
    if (head is null || projection?.AppliedSequence != 2 || append.ProjectionDelivery.Outcome != SessionProjectionDeliveryOutcome.Applied || audit.Health != SessionConsistencyHealth.Healthy)
        throw new InvalidOperationException("Packed Hongxian consumer did not compose, persist, project, and audit its session.");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
'@
    Restore-And-Audit $core @('Penghou.Hongxian.LatticeDb', 'LatticeDbSharp')
    Restore-And-Audit $sqlite @('Penghou.Hongxian.LatticeDb', 'LatticeDbSharp')

    $lattice = Write-ConsumerProject 'LatticeConsumer' '<PackageReference Include="Penghou.Hongxian.LatticeDb" Version="VERSION" />' @'
using System.Text.Json;
using Penghou.Hongxian;
using Penghou.Hongxian.LatticeDb;

static ExperienceProjectionDescriptor Descriptor() =>
    new(ExperienceProjectionId.New(), "latticedb", "packed-consumer", 1, "policy-1");
static ExperienceEvidenceReference Evidence() =>
    new(SessionId.New(), "packed-ledger", 1, Guid.CreateVersion7(), "hash-1");
static ExperienceEntity Entity(ExperienceProjectionDescriptor d, string key, string title) =>
    new(
        ExperienceEntityId.CreateDeterministic(d.ProjectionId, "task", key), "task",
        new ExperienceProvenance(
            d, ExperienceDerivationId.CreateDeterministic(d.ProjectionId, "packed", key)),
        new Dictionary<string, JsonElement>
        {
            ["title"] = JsonSerializer.SerializeToElement(title)
        },
        evidence: [Evidence()]);
static ExperienceRelation Relation(
    ExperienceProjectionDescriptor d, ExperienceEntity from, ExperienceEntity to, string key) =>
    new(
        ExperienceRelationId.CreateDeterministic(
            d.ProjectionId, from.Id, to.Id, "depends-on", key),
        from.Id, to.Id, "depends-on",
        new ExperienceProvenance(
            d, ExperienceDerivationId.CreateDeterministic(d.ProjectionId, "packed-relation", key)),
        evidence: [Evidence()]);

var root = Path.Combine(Path.GetTempPath(), "hongxian-packed-lattice", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var path = Path.Combine(root, "experience.lattice");
try
{
    var descriptor = Descriptor();
    var first = Entity(descriptor, "first", "durable packed lesson");
    var second = Entity(descriptor, "second", "durable packed result");
    var relation = Relation(descriptor, first, second, "first-to-second");
    IReadOnlyList<ExperienceEntityId> expectedLexical;
    ExperienceProjectionCheckpoint checkpoint;
    using (var writer = new LatticeDbExperienceProvider(new() { DatabasePath = path }))
    {
        if ((await writer.UpsertEntityAsync(first)).Outcome != ExperienceModelWriteOutcome.Applied ||
            (await writer.UpsertEntityAsync(second)).Outcome != ExperienceModelWriteOutcome.Applied ||
            (await writer.UpsertRelationAsync(relation)).Outcome != ExperienceModelWriteOutcome.Applied)
            throw new InvalidOperationException("Packed LatticeDB writes were not applied.");
        var position = new ExperienceProjectionPosition([
            new SessionEvidencePosition(
                SessionId.New(), new SessionLedgerHead("packed-ledger", 1, "hash-1"))]);
        checkpoint = await writer.AdvanceAsync(new AdvanceExperienceProjectionRequest(descriptor, position, 0));
        var lexical = await writer.SearchLexicalAsync(new(descriptor.ProjectionId, "durable"));
        var traversal = await writer.TraverseAsync(new(descriptor.ProjectionId, first.Id, maximumDepth: 1));
        expectedLexical = lexical.Items.Select(x => x.Id).ToArray();
        if (expectedLexical.Count != 2 ||
            !traversal.Items.Select(x => x.Id).SequenceEqual([relation.Id]) ||
            lexical.Checkpoint?.Position.Digest != checkpoint.Position.Digest ||
            traversal.Checkpoint?.Position.Digest != checkpoint.Position.Digest)
            throw new InvalidOperationException(
                "Packed LatticeDB lexical/traversal checkpoint was not exact.");
        await writer.DeleteProjectionAsync(descriptor);
        if ((await writer.GetEntityAsync(
                new(first.Provenance.Projection.ProjectionId, first.Id))).Items.Count != 0)
            throw new InvalidOperationException(
                "Packed LatticeDB projection deletion left an entity.");
        await writer.UpsertEntityAsync(first); await writer.UpsertEntityAsync(second); await writer.UpsertRelationAsync(relation);
        checkpoint = await writer.AdvanceAsync(new AdvanceExperienceProjectionRequest(descriptor, checkpoint.Position, 0));
        var replay = await writer.SearchLexicalAsync(new(descriptor.ProjectionId, "durable"));
        if (!replay.Items.Select(x => x.Id).SequenceEqual(expectedLexical) ||
            replay.Checkpoint?.Position.Digest != checkpoint.Position.Digest)
            throw new InvalidOperationException(
                "Packed LatticeDB delete/replay changed lexical order or checkpoint.");
    }
    using (var reader = new LatticeDbExperienceProvider(new() { DatabasePath = path, OpenMode = LatticeDbOpenMode.ReadOnly }))
    {
        var reopened = await reader.SearchLexicalAsync(new(descriptor.ProjectionId, "durable"));
        var traversed = await reader.TraverseAsync(new(descriptor.ProjectionId, first.Id, maximumDepth: 1));
        var reopenedEntity = await reader.GetEntityAsync(new(descriptor.ProjectionId, first.Id));
        if (!reopened.Items.Select(x => x.Id).SequenceEqual(expectedLexical) ||
            !traversed.Items.Select(x => x.Id).SequenceEqual([relation.Id]) ||
            reopened.Checkpoint?.Position.Digest != checkpoint.Position.Digest ||
            traversed.Checkpoint?.Position.Digest != checkpoint.Position.Digest ||
            reopenedEntity.Items.Count != 1)
            throw new InvalidOperationException(
                "Packed LatticeDB reopen changed lexical/traversal results or checkpoint.");
    }
}
finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
'@
    Restore-And-Audit $lattice @()
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        $resolvedTemporaryRoot = (Resolve-Path -LiteralPath $temporaryRoot).Path
        $resolvedTempParent = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedTemporaryRoot.StartsWith($resolvedTempParent, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to remove unexpected temporary path '$resolvedTemporaryRoot'." }
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
