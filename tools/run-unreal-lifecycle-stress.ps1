[CmdletBinding()]
param(
    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,
    [switch]$SkipBuild,
    [switch]$DedicatedServer,
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$ReportName = 'lifecycle-stress',
    [ValidateRange(1, 256)]
    [int]$TravelCycles = 8,
    [ValidateRange(1, 256)]
    [int]$PieCycles = 8,
    [ValidateRange(2, 32)]
    [int]$ConcurrentWorlds = 4,
    [ValidateRange(1, 1024)]
    [int]$RequestsPerWorld = 8,
    [ValidateRange(1, 512)]
    [int]$GenerationRollovers = 16,
    [ValidateRange(1, 128)]
    [int]$ObjectChurnCycles = 2,
    [ValidateRange(1, 4096)]
    [int]$ObjectsPerChurnCycle = 128,
    [ValidateRange(1, 64)]
    [int]$CommandSaturationCycles = 1,
    [ValidateRange(4097, 65536)]
    [int]$CommandsPerSaturationCycle = 5120,
    [ValidateRange(64, 4096)]
    [int]$MemoryGrowthBudgetMB = 512
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'HostProject\UEUnifiedScriptHost.uproject'
$managedProject = Join-Path $repoRoot 'samples\Unreal\UnifiedScript.Unreal.Demo\UnifiedScript.Unreal.Demo.csproj'
$artifactDirectory = Join-Path $repoRoot 'artifacts\ue'
$logPath = Join-Path $artifactDirectory "$ReportName.log"
$reportPath = Join-Path $artifactDirectory "$ReportName.json"

if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = @('D:\UE_5.8', 'D:\UE\_5.8') |
        Where-Object { Test-Path (Join-Path $_ 'Engine\Build\BatchFiles\Build.bat') } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'Pass -EngineRoot or set UNIFIEDSCRIPT_UE_ROOT.'
}

$buildBat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\Build.bat'
$editorCmd = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
$bundledDotNet = Join-Path $EngineRoot 'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $bundledDotNet) { $bundledDotNet } else { 'dotnet' }
foreach ($requiredPath in @($buildBat, $editorCmd, $projectPath, $managedProject)) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Required lifecycle-stress dependency is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
if (!$SkipBuild) {
    & $dotnetExe build $managedProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Managed lifecycle-stress build failed with exit code $LASTEXITCODE."
    }
    & $buildBat UEUnifiedScriptHostEditor Win64 Development $projectPath `
        -WaitMutex -NoHotReloadFromIDE -NoUBA -MaxParallelActions=4
    if ($LASTEXITCODE -ne 0) {
        throw "Unreal lifecycle-stress build failed with exit code $LASTEXITCODE."
    }
}

$defaultEnginePath = Join-Path $repoRoot 'HostProject\Config\DefaultEngine.ini'
$defaultInputPath = Join-Path $repoRoot 'HostProject\Config\DefaultInput.ini'
$defaultEngineExisted = Test-Path -LiteralPath $defaultEnginePath
$defaultInputExisted = Test-Path -LiteralPath $defaultInputPath
$defaultEngineBytes = if ($defaultEngineExisted) {
    [IO.File]::ReadAllBytes($defaultEnginePath)
} else { $null }
$defaultInputBytes = if ($defaultInputExisted) {
    [IO.File]::ReadAllBytes($defaultInputPath)
} else { $null }

try {
    $editorArguments = @(
        $projectPath,
        '-unattended', '-nop4', '-NullRHI', '-nosplash', '-NoSound',
        '-ExecCmds=Automation RunTests UnifiedScript.Unreal.WorldHost.LifecycleStress',
        '-TestExit=Automation Test Queue Empty',
        "-abslog=$logPath", '-log',
        "-UnifiedScriptSoakTravelCycles=$TravelCycles",
        "-UnifiedScriptSoakPieCycles=$PieCycles",
        "-UnifiedScriptSoakConcurrentWorlds=$ConcurrentWorlds",
        "-UnifiedScriptSoakRequestsPerWorld=$RequestsPerWorld",
        "-UnifiedScriptSoakGenerationRollovers=$GenerationRollovers",
        "-UnifiedScriptSoakObjectChurnCycles=$ObjectChurnCycles",
        "-UnifiedScriptSoakObjectsPerChurnCycle=$ObjectsPerChurnCycle",
        "-UnifiedScriptSoakCommandSaturationCycles=$CommandSaturationCycles",
        "-UnifiedScriptSoakCommandsPerSaturationCycle=$CommandsPerSaturationCycle",
        "-UnifiedScriptSoakMemoryGrowthBudgetMB=$MemoryGrowthBudgetMB")
    if ($DedicatedServer) {
        $editorArguments += '-server'
    }
    & $editorCmd @editorArguments
    $editorExitCode = $LASTEXITCODE
} finally {
    if ($defaultEngineExisted) {
        [IO.File]::WriteAllBytes($defaultEnginePath, $defaultEngineBytes)
    } elseif (Test-Path -LiteralPath $defaultEnginePath) {
        Remove-Item -LiteralPath $defaultEnginePath
    }
    if ($defaultInputExisted) {
        [IO.File]::WriteAllBytes($defaultInputPath, $defaultInputBytes)
    } elseif (Test-Path -LiteralPath $defaultInputPath) {
        Remove-Item -LiteralPath $defaultInputPath
    }
}

if ($editorExitCode -ne 0) {
    throw "Unreal lifecycle-stress process failed with exit code $editorExitCode."
}
$successPattern =
    'Test Completed. Result={Success} Name={LifecycleStress} Path={UnifiedScript.Unreal.WorldHost.LifecycleStress}'
if (!(Select-String -LiteralPath $logPath -SimpleMatch $successPattern -Quiet)) {
    throw "LifecycleStress Automation did not succeed. Log: $logPath"
}

$marker = 'UNIFIEDSCRIPT_LIFECYCLE_STRESS_JSON '
$line = Select-String -LiteralPath $logPath -SimpleMatch $marker |
    Select-Object -Last 1
if ($null -eq $line) {
    throw "Lifecycle stress JSON marker is missing. Log: $logPath"
}
$result = $line.Line.Substring($line.Line.IndexOf($marker) + $marker.Length) |
    ConvertFrom-Json

$matrixPass =
    $result.schemaVersion -eq 'unifiedscript.unreal-lifecycle-stress/v0.4' -and
    [bool]$result.dedicatedServer -eq [bool]$DedicatedServer -and
    [int]$result.travelCycles -ge $TravelCycles -and
    [int]$result.pieCycles -ge $PieCycles -and
    [int]$result.concurrentWorlds -ge $ConcurrentWorlds -and
    [int]$result.collidingRequestsPerWorld -ge $RequestsPerWorld -and
    [int]$result.generationRollovers -ge $GenerationRollovers -and
    [int]$result.failedReloadCycles -ge $GenerationRollovers -and
    [int]$result.objectChurnCycles -ge $ObjectChurnCycles -and
    [int]$result.objectsPerChurnCycle -ge $ObjectsPerChurnCycle -and
    [int]$result.objectRegistrations -ge ($ObjectChurnCycles * $ObjectsPerChurnCycle) -and
    [int]$result.garbageCollectionCycles -ge ($ObjectChurnCycles + 1) -and
    [int]$result.commandSaturationCycles -ge $CommandSaturationCycles -and
    [int]$result.commandsPerSaturationCycle -ge $CommandsPerSaturationCycle
$capacityPass =
    [uint64]$result.commandOverflowCount -eq 1 -and
    [uint64]$result.eventOverflowCount -eq (
        [uint64]($CommandsPerSaturationCycle - 4096) *
        [uint64]$CommandSaturationCycles) -and
    [uint64]$result.traceOverflowCount -eq 1 -and
    [uint64]$result.overlapOverflowCount -eq 1 -and
    [uint64]$result.asyncCapacityRejections -eq 1 -and
    [uint64]$result.asyncPayloadRejections -eq 1 -and
    [uint32]$result.maximumAsyncInFlightRequests -eq 4096 -and
    [uint32]$result.maximumAsyncPayloadBytes -eq 1048576
$memoryPass =
    [uint64]$result.memoryGrowthBytes -le [uint64]$result.memoryGrowthBudgetBytes -and
    [uint64]$result.memoryGrowthBudgetBytes -eq (
        [uint64]$MemoryGrowthBudgetMB * 1024L * 1024L)
$shutdownPass =
    [int]$result.shutdownSequencesVerified -gt 0 -and
    [int]$result.shutdownSequenceFailures -eq 0 -and
    [int]$result.breadcrumbCapacity -eq 64 -and
    [int]$result.breadcrumbCount -eq 64 -and
    [uint64]$result.breadcrumbDroppedCount -gt 0
$zeroLeakPass =
    [int]$result.staleCompletions -eq 0 -and
    [int]$result.pendingRequestsDelta -eq 0 -and
    [int]$result.managedWorldsDelta -eq 0 -and
    [int]$result.nativeContextsDelta -eq 0 -and
    [int]$result.pendingWorkerDispatchDelta -eq 0 -and
    [int]$result.rejectedWorkerDispatchDelta -eq 0 -and
    [int]$result.registeredObjectsDelta -eq 0 -and
    [int]$result.eventSubscriptionsDelta -eq 0 -and
    [int]$result.ownedResourcesDelta -eq 0 -and
    [int]$result.staleDebugSessions -eq 0 -and
    [int]$result.retiredLoadContextsAlive -eq 0

$report = [ordered]@{
    schemaVersion = 'unifiedscript.unreal-lifecycle-stress-report/v0.3'
    status = if (
        $matrixPass -and
        $capacityPass -and
        $memoryPass -and
        $shutdownPass -and
        $zeroLeakPass) {
        'passed'
    } else { 'failed' }
    engineRoot = [IO.Path]::GetFullPath($EngineRoot)
    test = 'UnifiedScript.Unreal.WorldHost.LifecycleStress'
    result = $result
    gates = [ordered]@{
        completeMatrix = $matrixPass
        boundedCapacity = $capacityPass
        memoryBudget = $memoryPass
        deterministicShutdown = $shutdownPass
        zeroLeak = $zeroLeakPass
    }
    logPath = [IO.Path]::GetFullPath($logPath)
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8

if (
    !$matrixPass -or
    !$capacityPass -or
    !$memoryPass -or
    !$shutdownPass -or
    !$zeroLeakPass) {
    throw "One or more lifecycle stress gates failed. Report: $reportPath"
}

Write-Host "Lifecycle stress: PASS ($reportPath)"
Write-Host (
    "Travel=$($result.travelCycles), PIE=$($result.pieCycles), " +
    "Worlds=$($result.concurrentWorlds), Generations=$($result.generationRollovers), " +
    "FailedReloads=$($result.failedReloadCycles), Objects=$($result.objectRegistrations), " +
    "Commands=$([uint64]$result.publishedSaturationEvents), " +
    "Shutdowns=$([int]$result.shutdownSequencesVerified), " +
    "BreadcrumbDrops=$([uint64]$result.breadcrumbDroppedCount), " +
    "MemoryGrowth=$([uint64]$result.memoryGrowthBytes), " +
    "RetiredALC=$($result.retiredLoadContextsAlive), Server=$([bool]$result.dedicatedServer)")
