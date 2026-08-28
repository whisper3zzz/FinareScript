[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',
    [Parameter(Mandatory)]
    [ValidateSet(
        'struct-container-transfer',
        'delegate-storm',
        'batch-queries',
        'per-frame-actor-logic',
        'mass-entities')]
    [string]$WorkloadId,
    [ValidateRange(1, 3)]
    [int]$SuiteTrial = 1,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$puertsRoot = Join-Path $ComparisonRoot 'BlockBreaker-Puerts'
$puertsRunner = Join-Path $puertsRoot `
    'Tools\run_puerts_representative_workload.ps1'
$unifiedRunner = if ($WorkloadId -eq 'batch-queries') {
    Join-Path $repoRoot 'tools\run-unreal-data-plane-profile.ps1'
} else {
    Join-Path $repoRoot 'tools\run-unreal-representative-workload.ps1'
}
$captureWriter = Join-Path $PSScriptRoot 'write-workload-capture.ps1'
$identityScript = Join-Path $PSScriptRoot 'get-runtime-source-identity.ps1'
$manifestPath = Join-Path $repoRoot `
    'benchmarks\unreal\workload-suite.v0.1.json'
$suiteRoot = Join-Path $repoRoot 'artifacts\comparison\suite'
$metricsRoot = Join-Path $repoRoot `
    'artifacts\comparison\representative-workloads'

foreach ($required in @(
        $puertsRunner, $unifiedRunner, $captureWriter,
        $identityScript, $manifestPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Representative comparison dependency is missing: $required"
    }
}
New-Item -ItemType Directory -Force -Path $metricsRoot | Out-Null

function Get-PluginVersion([string]$DescriptorPath) {
    $descriptor = Get-Content -LiteralPath $DescriptorPath -Raw |
        ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$descriptor.VersionName)) {
        throw "Runtime plugin has no VersionName: $DescriptorPath"
    }
    return [string]$descriptor.VersionName
}

function Get-RuntimeIdentity([string]$EngineId, [string]$Root) {
    $output = @(& $identityScript `
        -EngineId $EngineId `
        -RepositoryRoot $Root)
    if ($LASTEXITCODE -ne 0 -or $output.Count -ne 1 -or
        -not $output[0].StartsWith('sha256:')) {
        throw "Cannot resolve $EngineId runtime identity under $Root."
    }
    return $output[0]
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$workloads = @($manifest.workloads)
$workloadIndex = [Array]::FindIndex(
    [object[]]$workloads,
    [Predicate[object]] { param($candidate) $candidate.id -eq $WorkloadId })
if ($workloadIndex -lt 0) {
    throw "The workload suite does not contain '$WorkloadId'."
}
$unifiedFirst = ($SuiteTrial % 2) -eq 1
$pairBase = ($SuiteTrial - 1) * $workloads.Count * 2 + $workloadIndex * 2
$unifiedOrdinal = $pairBase + $(if ($unifiedFirst) { 1 } else { 2 })
$puertsOrdinal = $pairBase + $(if ($unifiedFirst) { 2 } else { 1 })
$unifiedMetrics = Join-Path $metricsRoot `
    "unifiedscript-$WorkloadId-trial-$SuiteTrial.json"
$puertsMetrics = Join-Path $metricsRoot `
    "puerts-$WorkloadId-trial-$SuiteTrial.json"
$puertsReport = Join-Path $metricsRoot `
    "puerts-$WorkloadId-trial-$SuiteTrial-report.json"

function Invoke-UnifiedScript {
    $arguments = @{
        EngineRoot = $EngineRoot
        SuiteMetricsPath = $unifiedMetrics
    }
    if ($WorkloadId -ne 'batch-queries') {
        $arguments.WorkloadId = $WorkloadId
    }
    if ($SkipBuild) {
        $arguments.SkipBuild = $true
    }
    & $unifiedRunner @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "UnifiedScript '$WorkloadId' measurement failed."
    }
}

function Invoke-Puerts {
    $arguments = @{
        EngineRoot = $EngineRoot
        WorkloadId = $WorkloadId
        OutputPath = $puertsReport
        SuiteMetricsPath = $puertsMetrics
    }
    if ($SkipBuild) {
        $arguments.SkipBuild = $true
    }
    & $puertsRunner @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Puerts '$WorkloadId' measurement failed."
    }
}

if ($unifiedFirst) {
    Invoke-UnifiedScript
    Invoke-Puerts
} else {
    Invoke-Puerts
    Invoke-UnifiedScript
}

foreach ($runtime in @(
        [pscustomobject]@{
            Id = 'unifiedScript'
            Root = $repoRoot
            Ordinal = $unifiedOrdinal
            Metrics = $unifiedMetrics
            Descriptor = Join-Path $repoRoot `
                'HostProject\Plugins\UEUnifiedScript\UEUnifiedScript.uplugin'
        },
        [pscustomobject]@{
            Id = 'puerts'
            Root = $puertsRoot
            Ordinal = $puertsOrdinal
            Metrics = $puertsMetrics
            Descriptor = Join-Path $puertsRoot 'Plugins\Puerts\Puerts.uplugin'
        })) {
    $rawPath = Join-Path $suiteRoot `
        "$($runtime.Id)\raw\$WorkloadId-trial-$SuiteTrial.json"
    $capturePath = Join-Path $suiteRoot (
        "$($runtime.Id)\captures\{0:D4}-$WorkloadId-trial-$SuiteTrial.json" -f
        $runtime.Ordinal)
    & $captureWriter `
        -EngineId $runtime.Id `
        -RuntimeVersion (Get-PluginVersion $runtime.Descriptor) `
        -RuntimeCommit (Get-RuntimeIdentity $runtime.Id $runtime.Root) `
        -WorkloadId $WorkloadId `
        -Trial $SuiteTrial `
        -MetricsPath $runtime.Metrics `
        -RawOutputPath $rawPath `
        -CaptureOutputPath $capturePath `
        -EngineRoot $EngineRoot `
        -ManifestPath $manifestPath
    if ($LASTEXITCODE -ne 0) {
        throw "$($runtime.Id) '$WorkloadId' capture normalization failed."
    }
}

Write-Output (
    "Representative pair complete: workload=$WorkloadId, trial=$SuiteTrial, " +
    "order=$(if ($unifiedFirst) { 'UnifiedScript->Puerts' } else { 'Puerts->UnifiedScript' })")
