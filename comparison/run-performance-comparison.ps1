[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',
    [switch]$RequireWin,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$puertsRoot = Join-Path $ComparisonRoot 'BlockBreaker-Puerts'
$puertsRunner = Join-Path $puertsRoot 'Tools\run_puerts_benchmark.ps1'
$unifiedScriptRunner = Join-Path $repoRoot 'tools\run-unreal-benchmark.ps1'
$artifactDirectory = Join-Path $repoRoot 'artifacts\comparison\performance'
$puertsResult = Join-Path $artifactDirectory 'puerts.json'
$finalResult = Join-Path $artifactDirectory 'comparison.json'
$unifiedScriptResult = Join-Path $repoRoot 'artifacts\ue\interop-benchmark.json'
$puertsSuiteMetrics = Join-Path $artifactDirectory 'puerts-suite-metrics.json'
$unifiedScriptSuiteMetrics = Join-Path $artifactDirectory 'unifiedscript-suite-metrics.json'

foreach ($requiredPath in @($puertsRunner, $unifiedScriptRunner)) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Performance comparison dependency is missing: $requiredPath"
    }
}
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null

$puertsArguments = @{
    EngineRoot = $EngineRoot
    OutputPath = $puertsResult
    SuiteMetricsPath = $puertsSuiteMetrics
}
$unifiedScriptArguments = @{
    EngineRoot = $EngineRoot
    PuertsResult = $puertsResult
    RequirePuerts = $true
    SuiteMetricsPath = $unifiedScriptSuiteMetrics
}
if (-not $RequireWin) {
    $unifiedScriptArguments.AllowLosingComparison = $true
}
if ($SkipBuild) {
    $puertsArguments.SkipBuild = $true
    $unifiedScriptArguments.SkipBuild = $true
}

& $puertsRunner @puertsArguments
if ($LASTEXITCODE -ne 0) {
    throw "Puerts performance benchmark failed with exit code $LASTEXITCODE."
}
& $unifiedScriptRunner @unifiedScriptArguments
if ($LASTEXITCODE -ne 0) {
    throw "UnifiedScript performance benchmark failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $unifiedScriptResult -Destination $finalResult -Force
$report = Get-Content -Raw -LiteralPath $finalResult | ConvertFrom-Json
Write-Host (
    'Performance comparison passed: UnifiedScript={0} ns/call, Puerts={1} ns/call, ratio={2:N3}' -f
    $report.unifiedScriptNsPerCall,
    $report.puertsNsPerCall,
    $report.unifiedScriptToPuertsRatio)
Write-Host "Report: $finalResult"
Write-Host "M10.1 UnifiedScript scalar metrics: $unifiedScriptSuiteMetrics"
Write-Host "M10.1 Puerts scalar metrics: $puertsSuiteMetrics"
