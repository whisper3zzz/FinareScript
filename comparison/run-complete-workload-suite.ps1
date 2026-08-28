[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',
    [ValidateSet(
        'scalar-property-calls',
        'struct-container-transfer',
        'delegate-storm',
        'per-frame-actor-logic',
        'mass-entities',
        'batch-queries',
        'resource-spawn-flow',
        'hot-reload',
        'blockbreaker-complete')]
    [string[]]$WorkloadId,
    [ValidateRange(1, 3)]
    [int[]]$Trial = @(1, 2, 3),
    [string]$PackageMetricsPath,
    [switch]$Force,
    [switch]$PlanOnly,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot `
    'benchmarks\unreal\workload-suite.v0.1.json'
$suiteRoot = Join-Path $repoRoot 'artifacts\comparison\suite'
$scalarRunner = Join-Path $PSScriptRoot 'run-performance-comparison.ps1'
$representativeRunner = Join-Path $PSScriptRoot `
    'run-representative-workload-comparison.ps1'
$gameplayRunner = Join-Path $PSScriptRoot 'run-gameplay-comparison.ps1'
$identityScript = Join-Path $PSScriptRoot 'get-runtime-source-identity.ps1'
$dotnetExe = Join-Path $EngineRoot `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
$cliProject = Join-Path $repoRoot `
    'src\UnifiedScript.Cli\UnifiedScript.Cli.csproj'
$cliDll = Join-Path $repoRoot `
    'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll'
$engineBuildPath = Join-Path $EngineRoot 'Engine\Build\Build.version'
$puertsRoot = Join-Path $ComparisonRoot 'BlockBreaker-Puerts'
if ([string]::IsNullOrWhiteSpace($PackageMetricsPath)) {
    $PackageMetricsPath = Join-Path $repoRoot `
        'artifacts\comparison\package-size\latest.json'
}

foreach ($required in @(
        $manifestPath, $scalarRunner, $representativeRunner, $gameplayRunner,
        $identityScript, $dotnetExe, $cliProject, $engineBuildPath,
        (Join-Path $puertsRoot 'Plugins\Puerts\Puerts.uplugin'))) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Complete-suite dependency is missing: $required"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$workloads = @($manifest.workloads)
$selectedWorkloadIds = if ($null -eq $WorkloadId -or $WorkloadId.Count -eq 0) {
    @($workloads.id)
} else {
    @($WorkloadId)
}
$selectedTrials = @($Trial | Sort-Object -Unique)

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

$runtimeIdentity = @{
    unifiedScript = Get-RuntimeIdentity 'unifiedScript' $repoRoot
    puerts = Get-RuntimeIdentity 'puerts' $puertsRoot
}
$cpu = Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1
$operatingSystem = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop |
    Select-Object -First 1
$gpu = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
    Select-Object -First 1
$currentEnvironment = [pscustomobject]@{
    OperatingSystem = [string]::Join(' ', @(
        $operatingSystem.Caption,
        $operatingSystem.Version,
        "build-$($operatingSystem.BuildNumber)"))
    CpuModel = [string]$cpu.Name
    LogicalProcessorCount = [int]$cpu.NumberOfLogicalProcessors
    PhysicalMemoryBytes = [int64]$operatingSystem.TotalVisibleMemorySize * 1024
    GpuModel = if ($null -ne $gpu -and
        -not [string]::IsNullOrWhiteSpace([string]$gpu.Name)) {
        [string]$gpu.Name
    } else { 'unavailable-gpu' }
    EngineBuildId = 'sha256:' + (
        Get-FileHash -LiteralPath $engineBuildPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
}

function Get-ExpectedOrdinal(
    [string]$EngineId,
    [string]$Id,
    [int]$SuiteTrial) {
    $workloadIndex = [Array]::FindIndex(
        [object[]]$workloads,
        [Predicate[object]] { param($candidate) $candidate.id -eq $Id })
    if ($workloadIndex -lt 0) {
        throw "Unknown workload '$Id'."
    }
    $pairBase = ($SuiteTrial - 1) * $workloads.Count * 2 + $workloadIndex * 2
    $unifiedFirst = ($SuiteTrial % 2) -eq 1
    return $pairBase + $(if (
            ($unifiedFirst -and $EngineId -eq 'unifiedScript') -or
            (-not $unifiedFirst -and $EngineId -eq 'puerts')) { 1 } else { 2 })
}

function Get-CapturePath(
    [string]$EngineId,
    [string]$Id,
    [int]$SuiteTrial) {
    $captureDirectory = Join-Path $suiteRoot "$EngineId\captures"
    if ($Id -eq 'blockbreaker-complete') {
        return Join-Path $captureDirectory "$Id-trial-$SuiteTrial.json"
    }
    $ordinal = Get-ExpectedOrdinal $EngineId $Id $SuiteTrial
    return Join-Path $captureDirectory (
        '{0:D4}-{1}-trial-{2}.json' -f $ordinal, $Id, $SuiteTrial)
}

function Test-CurrentCapture(
    [string]$EngineId,
    [string]$Id,
    [int]$SuiteTrial) {
    $path = Get-CapturePath $EngineId $Id $SuiteTrial
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $false
    }
    try {
        $capture = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    } catch {
        return $false
    }
    $environment = $capture.environment
    return $capture.schemaVersion -eq
            'unifiedscript.performance-trial-capture/v0.1' -and
        $capture.suiteId -eq $manifest.suiteId -and
        $capture.suiteVersion -eq $manifest.suiteVersion -and
        $capture.engineId -eq $EngineId -and
        $capture.runtimeCommit -eq $runtimeIdentity[$EngineId] -and
        $capture.trial.workloadId -eq $Id -and
        [int]$capture.trial.trial -eq $SuiteTrial -and
        [int]$capture.trial.executionOrdinal -eq
            (Get-ExpectedOrdinal $EngineId $Id $SuiteTrial) -and
        $capture.trial.status -eq 'passed' -and
        $environment.operatingSystem -eq $currentEnvironment.OperatingSystem -and
        $environment.cpuModel -eq $currentEnvironment.CpuModel -and
        [int]$environment.logicalProcessorCount -eq
            $currentEnvironment.LogicalProcessorCount -and
        [int64]$environment.physicalMemoryBytes -eq
            $currentEnvironment.PhysicalMemoryBytes -and
        $environment.gpuModel -eq $currentEnvironment.GpuModel -and
        $environment.engineBuildId -eq $currentEnvironment.EngineBuildId -and
        $environment.engineVersion -eq $manifest.build.engineVersion -and
        $environment.platform -eq $manifest.build.platform -and
        $environment.configuration -eq $manifest.build.configuration -and
        $environment.rhi -eq $manifest.build.rhi -and
        $environment.buildFingerprint -eq $manifest.build.buildFingerprint
}

function Test-CurrentPair([string]$Id, [int]$SuiteTrial) {
    return (Test-CurrentCapture 'unifiedScript' $Id $SuiteTrial) -and
        (Test-CurrentCapture 'puerts' $Id $SuiteTrial)
}

$pending = @()
foreach ($suiteTrial in $selectedTrials) {
    foreach ($workload in $workloads) {
        if ($workload.id -notin $selectedWorkloadIds) {
            continue
        }
        $isCurrent = Test-CurrentPair $workload.id $suiteTrial
        $state = if ($Force) { 'FORCE' } elseif ($isCurrent) { 'CURRENT' } else { 'RUN' }
        Write-Output ('[{0}] trial={1} workload={2}' -f
            $state, $suiteTrial, $workload.id)
        if ($Force -or -not $isCurrent) {
            $pending += [pscustomobject]@{
                WorkloadId = [string]$workload.id
                Trial = [int]$suiteTrial
            }
        }
    }
}

Write-Output (
    "Suite plan: selected=$($selectedWorkloadIds.Count * $selectedTrials.Count), " +
    "pending=$($pending.Count), " +
    "unifiedIdentity=$($runtimeIdentity.unifiedScript), " +
    "puertsIdentity=$($runtimeIdentity.puerts)")
if ($PlanOnly) {
    return
}

$interopTargetsPrepared = [bool]$SkipBuild
$gameplayTargetsPrepared = [bool]$SkipBuild
foreach ($item in $pending) {
    $arguments = @{
        EngineRoot = $EngineRoot
        ComparisonRoot = $ComparisonRoot
        SuiteTrial = $item.Trial
    }
    if ($item.WorkloadId -eq 'scalar-property-calls') {
        if ($interopTargetsPrepared) {
            $arguments.SkipBuild = $true
        }
        & $scalarRunner @arguments
        $interopTargetsPrepared = $true
    } elseif ($item.WorkloadId -eq 'blockbreaker-complete') {
        if (-not (Test-Path -LiteralPath $PackageMetricsPath -PathType Leaf)) {
            throw "BlockBreaker package metrics are missing: $PackageMetricsPath"
        }
        $arguments.Trials = 1
        $arguments.WriteSuiteCaptures = $true
        $arguments.PackageMetricsPath = $PackageMetricsPath
        if (($item.Trial % 2) -eq 1) {
            $arguments.UnifiedFirst = $true
        }
        if ($gameplayTargetsPrepared) {
            $arguments.SkipBuild = $true
        }
        & $gameplayRunner @arguments
        $gameplayTargetsPrepared = $true
    } else {
        $arguments.WorkloadId = $item.WorkloadId
        if ($interopTargetsPrepared) {
            $arguments.SkipBuild = $true
        }
        & $representativeRunner @arguments
        $interopTargetsPrepared = $true
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Suite pair failed: trial=$($item.Trial), workload=$($item.WorkloadId)."
    }
    if (-not (Test-CurrentPair $item.WorkloadId $item.Trial)) {
        throw (
            "Suite pair did not publish current matched captures: " +
            "trial=$($item.Trial), workload=$($item.WorkloadId).")
    }
}

$missing = @()
for ($suiteTrial = 1; $suiteTrial -le [int]$manifest.trialCount; ++$suiteTrial) {
    foreach ($workload in $workloads) {
        if (-not (Test-CurrentPair $workload.id $suiteTrial)) {
            $missing += "$($workload.id)/$suiteTrial"
        }
    }
}
if ($missing.Count -gt 0) {
    Write-Output (
        "Suite remains resumable with $($missing.Count) pair(s) missing or stale: " +
        ($missing -join ', '))
    return
}

& $dotnetExe build $cliProject -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw "UnifiedScript CLI build failed: $LASTEXITCODE."
}
$unifiedResult = Join-Path $suiteRoot 'unifiedscript.json'
$puertsResult = Join-Path $suiteRoot 'puerts.json'
$comparisonResult = Join-Path $suiteRoot 'comparison.json'
foreach ($engine in @(
        [pscustomobject]@{ Id = 'unifiedScript'; Output = $unifiedResult },
        [pscustomobject]@{ Id = 'puerts'; Output = $puertsResult })) {
    & $dotnetExe $cliDll ue-workload-assemble `
        --manifest $manifestPath `
        --captures (Join-Path $suiteRoot "$($engine.Id)\captures") `
        --engine $engine.Id `
        --out $engine.Output
    if ($LASTEXITCODE -ne 0) {
        throw "$($engine.Id) workload assembly failed: $LASTEXITCODE."
    }
}
& $dotnetExe $cliDll ue-workload-compare `
    --manifest $manifestPath `
    --unified $unifiedResult `
    --puerts $puertsResult `
    --out $comparisonResult
if ($LASTEXITCODE -ne 0) {
    throw "Matched workload comparison failed: $LASTEXITCODE."
}
Write-Output "Complete 54-slot workload suite: PASS ($comparisonResult)"
