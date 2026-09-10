[CmdletBinding()]
param(
    [string]$DotNet = 'D:/UE_5.8/Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe',
    [string]$OutputRoot = 'artifacts/m13-3c-2-event-reload/fixture'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tests/Finare.Tests/Fixtures/BlueprintEventReload/BlueprintEventReloadFixture.csproj'
$identityPath = Join-Path $repoRoot 'tests/Finare.Tests/Fixtures/BlueprintEventReload/BlueprintEventReloadFixture.cs.usids'
$cliProject = Join-Path $repoRoot 'src/Finare.Cli/Finare.Cli.csproj'
$cliAssembly = Join-Path $repoRoot 'src/Finare.Cli/bin/Release/net10.0/Finare.Cli.dll'
$outputRootPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$declarationOutput = Join-Path $outputRootPath 'declaration'
$identitySeedPath = Join-Path $outputRootPath 'generated/BlueprintEventReloadFixture.usids'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd('\', '/')
if ($outputRootPath -ne $artifactRoot -and
    !$outputRootPath.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
    !$outputRootPath.StartsWith($artifactRoot + [IO.Path]::AltDirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot '$outputRootPath' must remain below '$artifactRoot'."
}

if (!(Test-Path -LiteralPath $DotNet)) { throw "Bundled .NET SDK 10 was not found at '$DotNet'." }
foreach ($required in @($project, $cliProject, $cliAssembly)) {
    if (!(Test-Path -LiteralPath $required)) { throw "Required fixture input is missing: $required" }
}
New-Item -ItemType Directory -Force -Path $outputRootPath, $declarationOutput | Out-Null

& $DotNet build $project --configuration Release --nologo `
    -p:BlueprintEventReloadVariant=V1 -p:FinareSkipManagedHostStaging=true
if ($LASTEXITCODE -ne 0) { throw "Blueprint event reload v1 build failed with exit code $LASTEXITCODE." }

if (!(Test-Path -LiteralPath $identityPath -PathType Leaf)) {
    & $DotNet $cliAssembly emit $project --out $declarationOutput --configuration Release --no-restore --no-build
    if ($LASTEXITCODE -ne 0) { throw "Baseline declaration emission failed with exit code $LASTEXITCODE." }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $identitySeedPath) | Out-Null
    & $DotNet $cliAssembly identity-init `
        (Join-Path $repoRoot 'tests/Finare.Tests/Fixtures/BlueprintEventReload/BlueprintEventReloadFixture.cs') `
        --out $identitySeedPath
    if ($LASTEXITCODE -ne 0) { throw "Generated identity initialization failed with exit code $LASTEXITCODE." }
    Copy-Item -LiteralPath $identitySeedPath -Destination $identityPath -Force
}

& $DotNet $cliAssembly emit $project --out $declarationOutput --identity $identityPath --configuration Release --no-restore --no-build
if ($LASTEXITCODE -ne 0) { throw "Baseline declaration emission with stable identity failed with exit code $LASTEXITCODE." }

foreach ($variant in @(
        @{ Name = 'v1'; Define = 'V1' },
        @{ Name = 'v2'; Define = 'V2' },
        @{ Name = 'incompatible'; Define = 'INCOMPATIBLE' })) {
    $output = Join-Path $outputRootPath $variant.Name
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    & $DotNet publish $project --configuration Release --no-restore --output $output `
        "-p:BlueprintEventReloadVariant=$($variant.Define)" `
        -p:FinareSkipManagedHostStaging=true
    if ($LASTEXITCODE -ne 0) { throw "Fixture '$($variant.Name)' publish failed with exit code $LASTEXITCODE." }
    if (!(Test-Path -LiteralPath (Join-Path $output 'BlueprintEventReloadFixture.dll'))) {
        throw "Fixture '$($variant.Name)' did not produce BlueprintEventReloadFixture.dll."
    }
}

$active = Join-Path $outputRootPath 'active'
New-Item -ItemType Directory -Force -Path $active | Out-Null
Copy-Item -Path (Join-Path (Join-Path $outputRootPath 'v1') '*') -Destination $active -Recurse -Force
$declaration = Join-Path $declarationOutput 'BlueprintEventReloadFixture.asset.json'
if (!(Test-Path -LiteralPath $declaration)) { throw "Baseline declaration is missing: $declaration" }
Write-Output "BlueprintEventReloadFixture.dll active path: $(Join-Path $active 'BlueprintEventReloadFixture.dll')"
Write-Output "BlueprintEventReloadFixture declaration: $declaration"
Write-Output "BlueprintEventReloadFixture identity: $identityPath"
