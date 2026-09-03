param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,
    [Parameter(Mandatory = $true)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$packageSource = (Resolve-Path $PackageDirectory).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "hongxian-consumer-$([Guid]::NewGuid().ToString('N'))"
$consumerPackages = Join-Path $temporaryRoot 'packages'
$projectPath = Join-Path $temporaryRoot 'PackedConsumer.csproj'
$programPath = Join-Path $temporaryRoot 'Program.cs'
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
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
"@ | Set-Content -Path $projectPath -Encoding utf8

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
'@ | Set-Content -Path $programPath -Encoding utf8

    # Use an isolated package folder so a locally cached, unpublished dependency
    # cannot make the packed-consumer check pass when public restore would fail.
    dotnet restore $projectPath --packages $consumerPackages --force --force-evaluate --no-cache
    if ($LASTEXITCODE -ne 0) { throw "Packed consumer restore failed." }
    dotnet run --project $projectPath --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Packed consumer execution failed." }
    dotnet list $projectPath package --vulnerable --include-transitive
    if ($LASTEXITCODE -ne 0) { throw "Packed consumer vulnerability audit failed." }
}
finally {
    if (Test-Path $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
