[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('unifiedScript', 'puerts')]
    [string]$EngineId,
    [Parameter(Mandatory)]
    [string]$RuntimeVersion,
    [Parameter(Mandatory)]
    [string]$RuntimeCommit,
    [Parameter(Mandatory)]
    [string]$WorkloadId,
    [Parameter(Mandatory)]
    [ValidateRange(1, 9)]
    [int]$Trial,
    [Parameter(Mandatory)]
    [string]$MetricsPath,
    [Parameter(Mandatory)]
    [string]$RawOutputPath,
    [Parameter(Mandatory)]
    [string]$CaptureOutputPath,
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $repoRoot 'benchmarks\unreal\workload-suite.v1.1.0.json'
}
$dotnetExe = Join-Path `
    $EngineRoot 'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
$cliDll = Join-Path `
    $repoRoot 'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll'
$engineBuildPath = Join-Path $EngineRoot 'Engine\Build\Build.version'
foreach ($requiredPath in @(
        $ManifestPath,
        $MetricsPath,
        $dotnetExe,
        $cliDll,
        $engineBuildPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required workload capture input does not exist: $requiredPath"
    }
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 'unifiedscript.performance-workload-suite/v0.1') {
    throw "Unsupported workload manifest: $ManifestPath"
}
$workloads = @($manifest.workloads)
$workloadIndex = [Array]::FindIndex(
    [object[]]$workloads,
    [Predicate[object]] { param($candidate) $candidate.id -eq $WorkloadId })
if ($workloadIndex -lt 0) {
    throw "Unknown workload '$WorkloadId'."
}
if ($Trial -gt [int]$manifest.trialCount) {
    throw "Trial $Trial exceeds manifest trialCount $($manifest.trialCount)."
}
$pairBase = ($Trial - 1) * $workloads.Count * 2 + $workloadIndex * 2
$unifiedFirst = ($Trial % 2) -eq 1
$executionOrdinal = $pairBase + $(if (
        ($unifiedFirst -and $EngineId -eq 'unifiedScript') -or
        (-not $unifiedFirst -and $EngineId -eq 'puerts')) { 1 } else { 2 })

$metrics = Get-Content -LiteralPath $MetricsPath -Raw | ConvertFrom-Json
if ($null -eq $metrics.values) {
    throw "Metrics input must contain one 'values' object: $MetricsPath"
}
$cpu = Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1
$operatingSystem = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop |
    Select-Object -First 1
$gpu = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
    Select-Object -First 1
$osDescriptor = [string]::Join(' ', @(
    $operatingSystem.Caption,
    $operatingSystem.Version,
    "build-$($operatingSystem.BuildNumber)"))
$gpuDescriptor = if ($null -ne $gpu -and
    -not [string]::IsNullOrWhiteSpace([string]$gpu.Name)) {
    [string]$gpu.Name
} else { 'unavailable-gpu' }
$engineBuildId = 'sha256:' + (
    Get-FileHash -LiteralPath $engineBuildPath -Algorithm SHA256).Hash.ToLowerInvariant()

$measurement = [ordered]@{
    schemaVersion = 'unifiedscript.performance-raw-measurement/v0.1'
    engineId = $EngineId
    runtimeVersion = $RuntimeVersion
    runtimeCommit = $RuntimeCommit
    operatingSystem = $osDescriptor
    cpuModel = [string]$cpu.Name
    logicalProcessorCount = [int]$cpu.NumberOfLogicalProcessors
    physicalMemoryBytes = [int64]$operatingSystem.TotalVisibleMemorySize * 1024
    gpuModel = $gpuDescriptor
    engineBuildId = $engineBuildId
    workloadId = $WorkloadId
    trial = $Trial
    executionOrdinal = $executionOrdinal
    status = 'passed'
    metrics = $metrics
}

foreach ($outputPath in @($RawOutputPath, $CaptureOutputPath)) {
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($outputPath))
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
}
$measurement | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $RawOutputPath -Encoding utf8

& $dotnetExe $cliDll ue-workload-capture `
    --manifest $ManifestPath `
    --measurement $RawOutputPath `
    --out $CaptureOutputPath
if ($LASTEXITCODE -ne 0) {
    throw "Workload capture normalization failed with exit code $LASTEXITCODE."
}

Write-Output (
    "Captured $EngineId/$WorkloadId trial $Trial at execution ordinal " +
    "${executionOrdinal}: $CaptureOutputPath")
