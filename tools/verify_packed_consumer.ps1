param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,
    [Parameter(Mandatory = $true)]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$packageSource = (Resolve-Path $PackageDirectory).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "hongxian-consumer-$([Guid]::NewGuid().ToString('N'))"
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
    var projections = new SqliteSessionProjectionStore(Path.Combine(root, "catalog.db"), pooling: false);
    await using var events = new SimingSessionEventStore(
        Path.Combine(root, "sessions"),
        projectionStore: projections,
        maximumCachedLedgers: 2);
    var sessionId = SessionId.New();
    await events.AppendAsync(new SessionEventRequest(
        sessionId,
        SessionParticipantAttribution.System("packed-consumer", "package-test"),
        SessionEventTypes.SessionCreated,
        DateTimeOffset.UtcNow,
        IdempotencyKey: $"session:{sessionId}:created"));
    var head = await events.VerifyChainAsync(sessionId);
    var projection = await projections.GetAsync(sessionId);
    if (head is null || projection?.AppliedSequence != 1)
        throw new InvalidOperationException("Packed Hongxian consumer did not persist and project its event.");
}
finally
{
    if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}
'@ | Set-Content -Path $programPath -Encoding utf8

    dotnet restore $projectPath --force --no-cache
    if ($LASTEXITCODE -ne 0) { throw "Packed consumer restore failed." }
    dotnet run --project $projectPath --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Packed consumer execution failed." }
    dotnet list $projectPath package --vulnerable --include-transitive
    if ($LASTEXITCODE -ne 0) { throw "Packed consumer vulnerability audit failed." }
}
finally {
    if (Test-Path $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
