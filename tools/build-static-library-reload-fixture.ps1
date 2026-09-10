[CmdletBinding()]
param(
    [string]$DotNet = 'D:/UE_5.8/Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe',
    [string]$OutputRoot = 'artifacts/m13-3c-1c-b-static-library-reload/fixture'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tests/Finare.Tests/Fixtures/StaticLibraryReload/StaticLibraryReloadFixture.csproj'
$outputRootPath = Join-Path $repoRoot $OutputRoot
if (!(Test-Path -LiteralPath $DotNet)) {
    throw "Bundled .NET SDK 10 was not found at '$DotNet'."
}

New-Item -ItemType Directory -Force -Path $outputRootPath | Out-Null
foreach ($variant in @(
        @{ Name = 'v1'; Define = 'V1' },
        @{ Name = 'v2'; Define = 'V2' },
        @{ Name = 'incompatible'; Define = 'INCOMPATIBLE' })) {
    $output = Join-Path $outputRootPath $variant.Name
    $defines = $variant.Define
    & $DotNet publish $project --configuration Release --output $output `
        "-p:StaticLibraryReloadVariant=$defines" -p:FinareSkipManagedHostStaging=true
    if ($LASTEXITCODE -ne 0) {
        throw "Static library fixture '$($variant.Name)' failed with exit code $LASTEXITCODE."
    }
}

$active = Join-Path $outputRootPath 'active'
New-Item -ItemType Directory -Force -Path $active | Out-Null
Copy-Item -Path (Join-Path $outputRootPath 'v1/*') -Destination $active -Recurse -Force
Write-Output (Join-Path $active 'StaticLibraryReloadFixture.dll')
