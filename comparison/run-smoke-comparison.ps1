[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$puertsRoot = Join-Path $ComparisonRoot 'BlockBreaker-Puerts'
$unifiedRoot = Join-Path $ComparisonRoot 'BlockBreaker-UnifiedScript'
$buildBat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\Build.bat'
$editorCmd = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$resultRoot = Join-Path $repoRoot "artifacts\comparison\$timestamp"
$migrationAudit = Join-Path $PSScriptRoot 'audit-blockbreaker-migration.ps1'

foreach ($required in @(
    $buildBat, $editorCmd, $puertsRoot, $unifiedRoot, $migrationAudit)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required comparison path does not exist: $required"
    }
}

New-Item -ItemType Directory -Force -Path $resultRoot | Out-Null
& $migrationAudit -EngineRoot $EngineRoot -ComparisonRoot $ComparisonRoot `
    -ReportPath (Join-Path $resultRoot 'migration-audit.json')
if ($LASTEXITCODE -ne 0) {
    throw 'BlockBreaker whole-project migration audit failed before smoke comparison.'
}
$puertsBuildLog = Join-Path $resultRoot 'puerts-build.log'
$puertsRuntimeLog = Join-Path $resultRoot 'puerts-runtime.log'
$unifiedLog = Join-Path $resultRoot 'unifiedscript.log'

Push-Location $puertsRoot
try {
    & $buildBat BlockBreakerEditor Win64 Development `
        "-Project=$(Join-Path $puertsRoot 'BlockBreaker.uproject')" `
        -WaitMutex -NoHotReloadFromIDE *> $puertsBuildLog
    $puertsBuildExitCode = $LASTEXITCODE
    if ($puertsBuildExitCode -eq 0) {
        & $editorCmd (Join-Path $puertsRoot 'BlockBreaker.uproject') `
            '/Game/BlockBreaker/Maps/TutorialMap' -game -nullrhi -unattended `
            -nosound -nosplash -stdout -FullStdOutLogOutput `
            -benchmark -fps=60 -seconds=5 *> $puertsRuntimeLog
        $puertsRuntimeExitCode = $LASTEXITCODE
    }
    else {
        $puertsRuntimeExitCode = -1
    }
}
finally {
    Pop-Location
}

$unifiedRunner = Join-Path $unifiedRoot 'Tools\run_unifiedscript_demo.ps1'
& $unifiedRunner -EngineRoot $EngineRoot -UnifiedScriptRoot $repoRoot -Headless *> $unifiedLog
$unifiedExitCode = $LASTEXITCODE

$puertsRuntimeText = if (Test-Path -LiteralPath $puertsRuntimeLog) {
    Get-Content -LiteralPath $puertsRuntimeLog -Raw
} else { '' }
$unifiedText = Get-Content -LiteralPath $unifiedLog -Raw
$unifiedMatch = [regex]::Match(
    $unifiedText,
    'ManagedTicks=(\d+), Shots=(\d+), Hits=(\d+), Status=([^,\r\n]+), FastCalls=(\d+), CallFrameCalls=(\d+), LegacyCalls=(\d+)')

$summary = [ordered]@{
    schema = 'unifiedscript.puerts-smoke-comparison/v0.1'
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    engineRoot = $EngineRoot
    performanceComparable = $false
    performanceNote = 'Functional smoke only; matched workload timing is not implemented yet.'
    puerts = [ordered]@{
        commit = (& git -C $puertsRoot rev-parse HEAD).Trim()
        buildExitCode = $puertsBuildExitCode
        runtimeExitCode = $puertsRuntimeExitCode
        normalModeStarted = $puertsRuntimeText.Contains('PuertsModule: Normal Mode started!')
        playerBound = $puertsRuntimeText.Contains('Bind module [TS_Player]')
        rifleBound = $puertsRuntimeText.Contains('Bind module [TS_Rifle]')
        mapLoaded = $puertsRuntimeText.Contains('Load map complete /Game/BlockBreaker/Maps/TutorialMap')
        requestedTicks = 300
    }
    unifiedScript = [ordered]@{
        commit = (& git -C $unifiedRoot rev-parse HEAD).Trim()
        runtimeExitCode = $unifiedExitCode
        gameModeLoaded = $unifiedText.Contains("Game class is 'UnifiedScriptBlockBreakerGameMode'")
        scriptStarted = $unifiedText.Contains("UnifiedScript started 'BlockBreaker.UnifiedScript.BlockBreakerPlayerScript'")
        managedTicks = if ($unifiedMatch.Success) { [int]$unifiedMatch.Groups[1].Value } else { 0 }
        shots = if ($unifiedMatch.Success) { [int]$unifiedMatch.Groups[2].Value } else { 0 }
        hits = if ($unifiedMatch.Success) { [int]$unifiedMatch.Groups[3].Value } else { 0 }
        finalStatus = if ($unifiedMatch.Success) { $unifiedMatch.Groups[4].Value.Trim() } else { '' }
        fastCalls = if ($unifiedMatch.Success) { [uint64]$unifiedMatch.Groups[5].Value } else { 0 }
        callFrameCalls = if ($unifiedMatch.Success) { [uint64]$unifiedMatch.Groups[6].Value } else { 0 }
        legacyCalls = if ($unifiedMatch.Success) { [uint64]$unifiedMatch.Groups[7].Value } else { 0 }
    }
}

$summaryPath = Join-Path $resultRoot 'summary.json'
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
Copy-Item -LiteralPath $summaryPath `
    -Destination (Join-Path $repoRoot 'artifacts\comparison\latest.json') -Force

$puertsPassed =
    $summary.puerts.buildExitCode -eq 0 -and
    $summary.puerts.runtimeExitCode -eq 0 -and
    $summary.puerts.normalModeStarted -and
    $summary.puerts.playerBound -and
    $summary.puerts.rifleBound -and
    $summary.puerts.mapLoaded
$unifiedPassed =
    $summary.unifiedScript.runtimeExitCode -eq 0 -and
    $summary.unifiedScript.gameModeLoaded -and
    $summary.unifiedScript.scriptStarted -and
    $summary.unifiedScript.managedTicks -eq 300 -and
    $summary.unifiedScript.shots -eq 1 -and
    $summary.unifiedScript.hits -eq 1 -and
    $summary.unifiedScript.finalStatus -eq 'Stopped' -and
    $summary.unifiedScript.fastCalls -gt 0 -and
    $summary.unifiedScript.callFrameCalls -gt 0 -and
    $summary.unifiedScript.legacyCalls -eq 0

Write-Output "Puerts smoke: $puertsPassed"
Write-Output "UnifiedScript smoke: $unifiedPassed"
Write-Output "Report: $summaryPath"
if (-not ($puertsPassed -and $unifiedPassed)) {
    exit 1
}
