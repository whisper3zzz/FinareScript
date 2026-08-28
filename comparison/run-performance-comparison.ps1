[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',
    [ValidateRange(1, 3)]
    [int]$SuiteTrial = 1,
    [switch]$RequireWin,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$puertsRoot = Join-Path $ComparisonRoot 'BlockBreaker-Puerts'
$puertsRunner = Join-Path $puertsRoot 'Tools\run_puerts_benchmark.ps1'
$unifiedScriptRunner = Join-Path $repoRoot 'tools\run-unreal-benchmark.ps1'
$captureWriter = Join-Path $repoRoot 'comparison\write-workload-capture.ps1'
$runtimeIdentityScript = Join-Path $repoRoot `
    'comparison\get-runtime-source-identity.ps1'
$manifestPath = Join-Path $repoRoot 'benchmarks\unreal\workload-suite.v1.1.0.json'
$artifactDirectory = Join-Path $repoRoot 'artifacts\comparison\performance'
$suiteDirectory = Join-Path $repoRoot 'artifacts\comparison\suite\v1.1.0'
$puertsResult = Join-Path $artifactDirectory 'puerts.json'
$finalResult = Join-Path $artifactDirectory 'comparison.json'
$unifiedScriptResult = Join-Path $repoRoot 'artifacts\ue\interop-benchmark.json'
$puertsSuiteMetrics = Join-Path $artifactDirectory 'puerts-suite-metrics.json'
$unifiedScriptSuiteMetrics = Join-Path $artifactDirectory 'unifiedscript-suite-metrics.json'

foreach ($requiredPath in @(
        $puertsRunner,
        $unifiedScriptRunner,
        $captureWriter,
        $runtimeIdentityScript,
        $manifestPath)) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Performance comparison dependency is missing: $requiredPath"
    }
}
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

function Get-RuntimeSourceIdentity(
    [string]$EngineId,
    [string]$Root) {
    $output = @(& $runtimeIdentityScript `
        -EngineId $EngineId `
        -RepositoryRoot $Root)
    if ($LASTEXITCODE -ne 0 -or $output.Count -ne 1 -or
        -not $output[0].StartsWith('sha256:')) {
        throw "Cannot resolve $EngineId runtime source identity under $Root."
    }
    return $output[0]
}

function Get-PluginVersion([string]$DescriptorPath) {
    if (-not (Test-Path -LiteralPath $DescriptorPath -PathType Leaf)) {
        throw "Runtime plugin descriptor is missing: $DescriptorPath"
    }
    $descriptor = Get-Content -LiteralPath $DescriptorPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$descriptor.VersionName)) {
        throw "Runtime plugin descriptor has no VersionName: $DescriptorPath"
    }
    return [string]$descriptor.VersionName
}

$puertsArguments = @{
    EngineRoot = $EngineRoot
    OutputPath = $puertsResult
    SuiteMetricsPath = $puertsSuiteMetrics
}
$unifiedScriptArguments = @{
    EngineRoot = $EngineRoot
    SuiteMetricsPath = $unifiedScriptSuiteMetrics
}
if ($SkipBuild) {
    $puertsArguments.SkipBuild = $true
    $unifiedScriptArguments.SkipBuild = $true
}

function Invoke-PuertsMeasurement {
    & $puertsRunner @puertsArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Puerts performance benchmark failed with exit code $LASTEXITCODE."
    }
}

function Invoke-UnifiedScriptMeasurement([bool]$CompareExistingMeasurement) {
    $arguments = @{} + $unifiedScriptArguments
    if ($CompareExistingMeasurement) {
        $arguments.PuertsResult = $puertsResult
        $arguments.RequirePuerts = $true
        $arguments.ReportOnly = $true
        if (-not $RequireWin) {
            $arguments.AllowLosingComparison = $true
        }
    }
    & $unifiedScriptRunner @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "UnifiedScript performance benchmark failed with exit code $LASTEXITCODE."
    }
}

$unifiedFirst = ($SuiteTrial % 2) -eq 1
if ($unifiedFirst) {
    Invoke-UnifiedScriptMeasurement $false
    Invoke-PuertsMeasurement
    Invoke-UnifiedScriptMeasurement $true
} else {
    Invoke-PuertsMeasurement
    $unifiedScriptArguments.PuertsResult = $puertsResult
    $unifiedScriptArguments.RequirePuerts = $true
    if (-not $RequireWin) {
        $unifiedScriptArguments.AllowLosingComparison = $true
    }
    Invoke-UnifiedScriptMeasurement $false
}

Copy-Item -LiteralPath $unifiedScriptResult -Destination $finalResult -Force
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$workloadId = 'scalar-property-calls'
$workloadIndex = [Array]::FindIndex(
    [object[]]@($manifest.workloads),
    [Predicate[object]] { param($candidate) $candidate.id -eq $workloadId })
if ($workloadIndex -lt 0) {
    throw "The performance suite does not contain '$workloadId'."
}
$pairBase = ($SuiteTrial - 1) * @($manifest.workloads).Count * 2 +
    $workloadIndex * 2
$unifiedScriptOrdinal = $pairBase + $(if ($unifiedFirst) { 1 } else { 2 })
$puertsOrdinal = $pairBase + $(if ($unifiedFirst) { 2 } else { 1 })
$unifiedScriptRaw = Join-Path $suiteDirectory `
    "unifiedscript\raw\$workloadId-trial-$SuiteTrial.json"
$puertsRaw = Join-Path $suiteDirectory `
    "puerts\raw\$workloadId-trial-$SuiteTrial.json"
$unifiedScriptCapture = Join-Path $suiteDirectory (
    'unifiedscript\captures\{0:D4}-{1}-trial-{2}.json' -f
    $unifiedScriptOrdinal,
    $workloadId,
    $SuiteTrial)
$puertsCapture = Join-Path $suiteDirectory (
    'puerts\captures\{0:D4}-{1}-trial-{2}.json' -f
    $puertsOrdinal,
    $workloadId,
    $SuiteTrial)

& $captureWriter `
    -EngineId unifiedScript `
    -RuntimeVersion (Get-PluginVersion (
        Join-Path $repoRoot 'HostProject\Plugins\UEUnifiedScript\UEUnifiedScript.uplugin')) `
    -RuntimeCommit (Get-RuntimeSourceIdentity 'unifiedScript' $repoRoot) `
    -WorkloadId $workloadId `
    -Trial $SuiteTrial `
    -MetricsPath $unifiedScriptSuiteMetrics `
    -RawOutputPath $unifiedScriptRaw `
    -CaptureOutputPath $unifiedScriptCapture `
    -EngineRoot $EngineRoot `
    -ManifestPath $manifestPath
& $captureWriter `
    -EngineId puerts `
    -RuntimeVersion (Get-PluginVersion (
        Join-Path $puertsRoot 'Plugins\Puerts\Puerts.uplugin')) `
    -RuntimeCommit (Get-RuntimeSourceIdentity 'puerts' $puertsRoot) `
    -WorkloadId $workloadId `
    -Trial $SuiteTrial `
    -MetricsPath $puertsSuiteMetrics `
    -RawOutputPath $puertsRaw `
    -CaptureOutputPath $puertsCapture `
    -EngineRoot $EngineRoot `
    -ManifestPath $manifestPath

$report = Get-Content -Raw -LiteralPath $finalResult | ConvertFrom-Json
Write-Host (
    'Performance comparison completed: UnifiedScript={0} ns/call, Puerts={1} ns/call, ratio={2:N3}' -f
    $report.unifiedScriptNsPerCall,
    $report.puertsNsPerCall,
    $report.unifiedScriptToPuertsRatio)
Write-Host "Report: $finalResult"
Write-Host "M10.1 UnifiedScript scalar metrics: $unifiedScriptSuiteMetrics"
Write-Host "M10.1 Puerts scalar metrics: $puertsSuiteMetrics"
Write-Host "M10.1 UnifiedScript trial capture: $unifiedScriptCapture"
Write-Host "M10.1 Puerts trial capture: $puertsCapture"
