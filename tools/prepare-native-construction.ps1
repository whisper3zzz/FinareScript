[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:/UE_5.8',
    [string]$ReflectionPath,
    [string]$CliPath,
    [switch]$SkipManagedBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo 'artifacts/native-construction'
$dotnet = Join-Path $EngineRoot 'Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe'
function Invoke-Checked([string[]]$Arguments) {
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Managed construction preparation failed ($LASTEXITCODE)." }
}
if (!$CliPath) {
    Invoke-Checked @('build', (Join-Path $repo 'src/Finare.Cli/Finare.Cli.csproj'),
        '--artifacts-path', (Join-Path $output 'cli-build'), '-p:FinareSkipManagedHostStaging=true', '-v:q')
    $CliPath = Join-Path $output 'cli-build/bin/Finare.Cli/debug/Finare.Cli.dll'
}
if (!$ReflectionPath) {
    # Reuse the existing complete module discovery/export workflow.
    & (Join-Path $PSScriptRoot 'prepare-native-component-creation.ps1') -EngineRoot $EngineRoot -CliPath $CliPath
    $ReflectionPath = Join-Path $repo 'artifacts/native-component-creation/reflection.json'
}
if (!$SkipManagedBuild) {
    Invoke-Checked @('build', (Join-Path $repo 'src/Finare.Unreal/Finare.Unreal.csproj'),
        '--artifacts-path', (Join-Path $output 'build'), '-v:q')
}
Invoke-Checked @($CliPath, 'ue-bindings', $ReflectionPath, '--api-library',
    '--namespace', 'Finare.NativeConstruction.Generated', '--out', (Join-Path $output 'Bindings.g.cs'),
    '--native-out', (Join-Path $output 'Thunks.cpp'), '--report', (Join-Path $output 'coverage.json'), '--fail-on-rejected')
$fixture = Join-Path $repo 'tests/Finare.Tests/Fixtures/NativeConstruction/NativeConstruction.csproj'
$build = Join-Path $output 'fixture-build'
Invoke-Checked @('build', $fixture, '--artifacts-path', $build, '-v:q')
$previousArtifacts = $env:ArtifactsPath
try {
    $env:ArtifactsPath = $build
    $emitOutput = & $dotnet $CliPath emit $fixture --out (Join-Path $repo 'HostProject/Saved/Finare/NativeConstruction') `
        --configuration Debug --no-restore --no-build 2>&1
    $emitExit = $LASTEXITCODE
    $emitOutput | Write-Output
    if ($emitExit -ne 0 -or ($emitOutput -join "`n") -match 'Emission failed') {
        throw 'Native construction emission failed; existing outputs cannot be used as new evidence.'
    }
} finally { $env:ArtifactsPath = $previousArtifacts }
if (!(Test-Path -LiteralPath (Join-Path $repo 'HostProject/Saved/Finare/NativeConstruction/NativeConstruction.uschunk'))) {
    throw 'Native construction program was not emitted.'
}
