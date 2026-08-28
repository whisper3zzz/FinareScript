[CmdletBinding()]
param(
    [string]$DotNet,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot 'artifacts\ci\deterministic-generation'
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
if ([string]::IsNullOrWhiteSpace($DotNet)) {
    $DotNet = (Get-Command dotnet -ErrorAction Stop).Source
}
$cliProject = Join-Path $repoRoot 'src\UnifiedScript.Cli\UnifiedScript.Cli.csproj'
$cliAssembly = Join-Path $repoRoot 'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll'
$reflection = Join-Path $repoRoot 'samples\Unreal\UEUnifiedScriptRuntime.reflection.json'
$contract = Join-Path $repoRoot 'samples\Unreal\UEUnifiedScriptRuntime.api-contract.json'
$dataPlane = Join-Path $repoRoot 'samples\Unreal\UEUnifiedScriptRuntime.data-plane.json'
$expected = [ordered]@{
    'bindings.cs' = Join-Path $repoRoot 'samples\Unreal\UEUnifiedScriptRuntime.bindings.cs'
    'native-thunks.cpp' = Join-Path $repoRoot (
        'HostProject\Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\' +
        'Private\Generated\UEUnifiedScriptRuntime.native-thunks.generated.cpp')
    'api-nodes.json' = Join-Path $repoRoot 'samples\Unreal\UEUnifiedScriptRuntime.api-nodes.json'
    'binding-coverage.json' = Join-Path $repoRoot 'samples\Unreal\UEUnifiedScriptRuntime.binding-coverage.json'
    'data-plane.g.cs' = Join-Path $repoRoot 'src\UnifiedScript.Unreal\UnrealDataPlaneLayouts.g.cs'
    'data-plane.gen.h' = Join-Path $repoRoot (
        'HostProject\Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\' +
        'Public\UnifiedScriptDataPlaneLayouts.gen.h')
}
foreach ($required in @(
        $DotNet, $cliProject, $reflection, $contract, $dataPlane) +
        @($expected.Values)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Deterministic-generation dependency is missing: $required"
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][object[]]$Arguments,
        [Parameter(Mandatory)][string]$Label
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

function Invoke-Generation {
    param([Parameter(Mandatory)][string]$Destination)

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Invoke-Checked -FilePath $DotNet -Label 'Unreal binding generation' -Arguments @(
        $cliAssembly,
        'ue-bindings', $reflection,
        '--contract', $contract,
        '--out', (Join-Path $Destination 'bindings.cs'),
        '--native-out', (Join-Path $Destination 'native-thunks.cpp'),
        '--nodes-out', (Join-Path $Destination 'api-nodes.json'),
        '--report', (Join-Path $Destination 'binding-coverage.json'),
        '--fail-on-rejected')
    Invoke-Checked -FilePath $DotNet -Label 'Data-plane generation' -Arguments @(
        $cliAssembly,
        'ue-data-plane', $dataPlane,
        '--managed-out', (Join-Path $Destination 'data-plane.g.cs'),
        '--native-out', (Join-Path $Destination 'data-plane.gen.h'))
}

if (Test-Path -LiteralPath $runDirectory) {
    $existing = @(Get-ChildItem -LiteralPath $runDirectory -Force)
    if ($existing.Count -gt 0) {
        throw "Deterministic-generation output directory must be empty: $runDirectory"
    }
}
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$first = Join-Path $runDirectory 'first'
$second = Join-Path $runDirectory 'second'
Invoke-Checked -FilePath $DotNet -Label 'Release CLI build' -Arguments @(
    'build', $cliProject, '-c', 'Release', '--nologo')
Invoke-Generation -Destination $first
Invoke-Generation -Destination $second

$artifacts = @()
foreach ($entry in $expected.GetEnumerator()) {
    $firstPath = Join-Path $first $entry.Key
    $secondPath = Join-Path $second $entry.Key
    $firstHash = (Get-FileHash -LiteralPath $firstPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $secondHash = (Get-FileHash -LiteralPath $secondPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($firstHash -ne $secondHash) {
        throw "Generated artifact is nondeterministic: $($entry.Key)"
    }
    if ($firstHash -ne $expectedHash) {
        throw "Checked-in generated artifact is stale: $($entry.Value)"
    }
    $artifacts += [ordered]@{
        path = $entry.Key
        sha256 = $firstHash
        matchesSecondRun = $true
        matchesCheckedIn = $true
    }
}

$report = [ordered]@{
    schemaVersion = 'unifiedscript.deterministic-generation-gate/v0.1'
    status = 'passed'
    artifactCount = $artifacts.Count
    artifacts = $artifacts
}
$reportPath = Join-Path $runDirectory 'deterministic-generation.json'
[IO.File]::WriteAllText(
    $reportPath,
    ($report | ConvertTo-Json -Depth 6) + "`n",
    [Text.UTF8Encoding]::new($false))
Write-Host "UnifiedScript deterministic generation: PASS ($($artifacts.Count)/$($artifacts.Count))"
Write-Host "Report: $reportPath"
