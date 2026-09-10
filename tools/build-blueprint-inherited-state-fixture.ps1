[CmdletBinding()]
param(
    [string]$DotNet = 'D:/UE_5.8/Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe',
    [string]$OutputRoot = 'artifacts/m13-3c-3-inherited-runtime/fixture'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tests/Finare.Tests/Fixtures/BlueprintInheritedState/BlueprintInheritedStateFixture.csproj'
$source = Join-Path $repoRoot 'tests/Finare.Tests/Fixtures/BlueprintInheritedState/BlueprintInheritedStateFixture.cs'
$identityPath = Join-Path $repoRoot 'tests/Finare.Tests/Fixtures/BlueprintInheritedState/BlueprintInheritedStateFixture.cs.usids'
$cliAssembly = Join-Path $repoRoot 'src/Finare.Cli/bin/Release/net10.0/Finare.Cli.dll'
$outputRootPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd('\', '/')
$declarationOutput = Join-Path $outputRootPath 'declaration'
$publishOutput = Join-Path $outputRootPath 'publish'
$generatedOutput = Join-Path $outputRootPath 'generated'
$generatedSource = Join-Path $generatedOutput 'BlueprintInheritedStatePaths.g.cs'
$identitySeedPath = Join-Path $generatedOutput 'BlueprintInheritedStateFixture.usids'
$configPath = Join-Path $outputRootPath 'BlueprintInheritedStateFixture.config.json'
$assemblyName = 'BlueprintInheritedStateFixture'
$hostDirectory = [IO.Path]::GetFullPath((Join-Path $repoRoot 'HostProject/Plugins/Finare/Binaries/Managed'))
$fixtureDll = Join-Path $repoRoot "tests/Finare.Tests/Fixtures/BlueprintInheritedState/bin/Release/net10.0/$assemblyName.dll"

if (!(Test-Path -LiteralPath $DotNet)) { throw "Bundled .NET SDK 10 was not found at '$DotNet'." }
foreach ($required in @($project, $source, $cliAssembly)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required inherited-state fixture input is missing: $required"
    }
}
foreach ($hostName in @(
        'Finare.Core.dll',
        'Finare.CSharp.dll',
        'Finare.Graph.dll',
        'Finare.Runtime.dll',
        'Finare.Semantics.dll',
        'Finare.Unreal.dll')) {
    $hostPath = Join-Path $hostDirectory $hostName
    if (!(Test-Path -LiteralPath $hostPath -PathType Leaf)) {
        throw "Deployed managed host dependency is missing: $hostPath"
    }
}
if ($outputRootPath -eq $artifactRoot -or
    !$outputRootPath.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot '$outputRootPath' must remain below '$artifactRoot'."
}

New-Item -ItemType Directory -Force -Path $outputRootPath, $declarationOutput, $publishOutput, $generatedOutput | Out-Null

$fixtureId = [Guid]::NewGuid().ToString('N').ToUpperInvariant()
$packageDirectory = "/Game/UnifiedScriptInheritedState_$fixtureId"
if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    $existing = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if (![string]::IsNullOrWhiteSpace([string]$existing.fixtureId)) {
        $fixtureId = [string]$existing.fixtureId
    }
    if (![string]::IsNullOrWhiteSpace([string]$existing.packageDirectory)) {
        $packageDirectory = [string]$existing.packageDirectory
    }
}
if ($fixtureId -cnotmatch '^[0-9A-F]{32}$' -or
    !$packageDirectory.Equals("/Game/UnifiedScriptInheritedState_$fixtureId", [StringComparison]::Ordinal)) {
    throw "Fixture config identity is invalid: fixtureId='$fixtureId', packageDirectory='$packageDirectory'."
}
$actorClassPath = "$packageDirectory/InheritedStateBase.InheritedStateBase_C"

@"
namespace Finare.Tests.Fixtures.BlueprintInheritedState;

public static class BlueprintInheritedStatePaths
{
    public const string ActorClassPath = "$actorClassPath";
}
"@ | Set-Content -LiteralPath $generatedSource -Encoding UTF8

$propertyName = 'BlueprintInheritedStateGeneratedPath'
$hostPropertyName = 'BlueprintInheritedStateHostDirectory'
$oldProperty = [Environment]::GetEnvironmentVariable($propertyName, 'Process')
$oldHostProperty = [Environment]::GetEnvironmentVariable($hostPropertyName, 'Process')
try {
    [Environment]::SetEnvironmentVariable($propertyName, $generatedSource, 'Process')
    [Environment]::SetEnvironmentVariable($hostPropertyName, $hostDirectory, 'Process')
    & $DotNet build $project --configuration Release --nologo "-p:$propertyName=$generatedSource" "-p:$hostPropertyName=$hostDirectory" -p:FinareSkipManagedHostStaging=true
    if ($LASTEXITCODE -ne 0) { throw "Inherited-state fixture build failed with exit code $LASTEXITCODE." }

    if (!(Test-Path -LiteralPath $identityPath -PathType Leaf)) {
        $firstEmit = & $DotNet $cliAssembly emit $project --out $declarationOutput --configuration Release --no-restore --no-build
        $firstEmit | Write-Output
        if ($LASTEXITCODE -ne 0) { throw "Initial inherited-state declaration emission failed with exit code $LASTEXITCODE." }
        if (!(Test-Path -LiteralPath $fixtureDll -PathType Leaf)) { throw "Fixture build did not produce '$fixtureDll'." }
        & $DotNet $cliAssembly identity-init $source --out $identitySeedPath `
            --assembly $assemblyName `
            --reference (Join-Path $hostDirectory 'Finare.Unreal.dll') `
            --reference (Join-Path $hostDirectory 'Finare.Core.dll') `
            --reference $fixtureDll
        if ($LASTEXITCODE -ne 0) { throw "Inherited-state identity initialization failed with exit code $LASTEXITCODE." }
        if (!(Test-Path -LiteralPath $identitySeedPath -PathType Leaf)) { throw "Identity initialization did not produce '$identitySeedPath'." }
        Copy-Item -LiteralPath $identitySeedPath -Destination $identityPath -Force
    }

    & $DotNet $cliAssembly emit $project --out $declarationOutput --identity $identityPath --configuration Release --no-restore --no-build
    if ($LASTEXITCODE -ne 0) { throw "Stable inherited-state declaration emission failed with exit code $LASTEXITCODE." }

    # Keep the checked-in sidecar synchronized with the official emitter's
    # current source digest while preserving the stable IDs it returned.
    $emittedIdentityCandidates = @(
        Get-ChildItem -LiteralPath $declarationOutput -Filter '*.usids' -File |
            Where-Object {
                try {
                    $emitted = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
                    [string]$emitted.sourcePath -eq 'tests/Finare.Tests/Fixtures/BlueprintInheritedState/BlueprintInheritedStateFixture.cs'
                } catch {
                    $false
                }
            }
    )
    if ($emittedIdentityCandidates.Count -ne 1) {
        throw "Expected exactly one emitted identity sidecar for '$source', found $($emittedIdentityCandidates.Count)."
    }
    Copy-Item -LiteralPath $emittedIdentityCandidates[0].FullName -Destination $identityPath -Force

    & $DotNet publish $project --configuration Release --no-restore --output $publishOutput "-p:$propertyName=$generatedSource" "-p:$hostPropertyName=$hostDirectory" -p:FinareSkipManagedHostStaging=true
    if ($LASTEXITCODE -ne 0) { throw "Inherited-state fixture publish failed with exit code $LASTEXITCODE." }
}
finally {
    [Environment]::SetEnvironmentVariable($propertyName, $oldProperty, 'Process')
    [Environment]::SetEnvironmentVariable($hostPropertyName, $oldHostProperty, 'Process')
}

$declarationPath = Join-Path $declarationOutput "$assemblyName.asset.json"
$chunkPath = Join-Path $declarationOutput "$assemblyName.uschunk"
$assemblyPath = Join-Path $publishOutput "$assemblyName.dll"
foreach ($required in @($declarationPath, $chunkPath, $assemblyPath, $identityPath)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Inherited-state fixture output is missing: $required"
    }
}
$document = Get-Content -LiteralPath $declarationPath -Raw | ConvertFrom-Json
$assets = @(@($document.assets) | Where-Object { [string]$_.qualifiedName -eq 'Finare.Tests.Fixtures.BlueprintInheritedState.BlueprintInheritedStateFixture' })
if ($assets.Count -ne 1) { throw "Expected exactly one inherited-state ScriptAsset with the fixture qualified name in '$declarationPath', found $($assets.Count)." }
$asset = $assets[0]


$config = [ordered]@{
    schemaVersion = 'unifiedscript.blueprint-inherited-state-fixture/v0.1'
    fixtureId = $fixtureId
    packageDirectory = $packageDirectory
    actorClassPath = $actorClassPath
    scriptClassId = [string]$asset.id
    scriptTypeName = [string]$asset.qualifiedName
    declarationAssetPath = [string]$asset.assetPath
    declarationPath = [IO.Path]::GetFullPath($declarationPath)
    chunkPath = [IO.Path]::GetFullPath($chunkPath)
    assemblyPath = [IO.Path]::GetFullPath($assemblyPath)
    generatedSourcePath = [IO.Path]::GetFullPath($generatedSource)
}
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding UTF8

Write-Output "BlueprintInheritedStateFixture config: $configPath"
Write-Output "BlueprintInheritedStateFixture declaration: $declarationPath"
Write-Output "BlueprintInheritedStateFixture chunk: $chunkPath"
Write-Output "BlueprintInheritedStateFixture assembly: $assemblyPath"
Write-Output "BlueprintInheritedStateFixture class: $actorClassPath"
