[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',
    [ValidateRange(1, 1000000)]
    [int]$Frames = 10000,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$puertsRoot = Join-Path $ComparisonRoot 'BlockBreaker-Puerts'
$unifiedRoot = Join-Path $ComparisonRoot 'BlockBreaker-UnifiedScript'
$buildBat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\Build.bat'
$editorCmd = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
$map = '/Game/BlockBreaker/Maps/TutorialMap'
$artifactRoot = Join-Path $repoRoot 'artifacts\comparison\gameplay'
$puertsLog = Join-Path $artifactRoot 'puerts-blockbreaker.log'
$unifiedLog = Join-Path $artifactRoot 'unifiedscript-blockbreaker.log'
$resultPath = Join-Path $artifactRoot 'comparison.json'

foreach ($required in @($buildBat, $editorCmd, $puertsRoot, $unifiedRoot)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required gameplay comparison path does not exist: $required"
    }
}
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

$puertsProject = Join-Path $puertsRoot 'BlockBreaker.uproject'
if (-not $SkipBuild) {
    & $buildBat BlockBreakerEditor Win64 Development "-Project=$puertsProject" `
        -WaitMutex -NoHotReloadFromIDE -NoUBA -MaxParallelActions=4
    if ($LASTEXITCODE -ne 0) {
        throw "Puerts gameplay build failed with exit code $LASTEXITCODE."
    }
}

$puertsOutput = @(& $editorCmd $puertsProject $map -game -nullrhi -unattended `
    -nosound -nosplash -stdout -FullStdOutLogOutput -benchmark -fps=60 `
    "-BlockBreakerBenchmarkFrames=$Frames" 2>&1)
$puertsExitCode = $LASTEXITCODE
$puertsOutput | Set-Content -LiteralPath $puertsLog -Encoding utf8
$puertsText = $puertsOutput -join [Environment]::NewLine

$unifiedRunner = Join-Path $unifiedRoot 'Tools\run_unifiedscript_demo.ps1'
$unifiedOutput = @(& $unifiedRunner `
    -EngineRoot $EngineRoot `
    -UnifiedScriptRoot $repoRoot `
    -Headless `
    -GameplayBenchmarkFrames $Frames 2>&1)
$unifiedExitCode = $LASTEXITCODE
$unifiedOutput | Set-Content -LiteralPath $unifiedLog -Encoding utf8
$unifiedText = $unifiedOutput -join [Environment]::NewLine

$puertsMatch = [regex]::Match(
    $puertsText,
    'Puerts BlockBreaker benchmark: Frames=(\d+), ElapsedMs=([0-9.]+), FPS=([0-9.]+)')
$puertsScriptMatch = [regex]::Match(
    $puertsText,
    'Puerts BlockBreaker script workload: Frames=(\d+), ElapsedMs=([0-9.]+)')
$unifiedMatch = [regex]::Match(
    $unifiedText,
    'UnifiedScript BlockBreaker benchmark: Frames=(\d+), ElapsedMs=([0-9.]+), FPS=([0-9.]+)')
$metricsMatch = [regex]::Match(
    $unifiedText,
    'ManagedTicks=(\d+), Shots=(\d+), Hits=(\d+), Status=([^,\r\n]+), FastCalls=(\d+), CallFrameCalls=(\d+), LegacyCalls=(\d+)')

if ($puertsExitCode -ne 0 -or -not $puertsMatch.Success -or
    -not $puertsScriptMatch.Success -or
    [int]$puertsScriptMatch.Groups[1].Value -ne $Frames -or
    [int]$puertsMatch.Groups[1].Value -ne $Frames) {
    throw "Puerts did not complete the exact $Frames-frame workload. Log: $puertsLog"
}
if ($unifiedExitCode -ne 0 -or -not $unifiedMatch.Success -or
    [int]$unifiedMatch.Groups[1].Value -ne $Frames -or -not $metricsMatch.Success -or
    [int]$metricsMatch.Groups[1].Value -ne $Frames -or
    [uint64]$metricsMatch.Groups[7].Value -ne 0) {
    throw "UnifiedScript did not complete the exact $Frames-frame fast-path workload. Log: $unifiedLog"
}

$puertsElapsed = [double]$puertsMatch.Groups[2].Value
$unifiedElapsed = [double]$unifiedMatch.Groups[2].Value
$result = [ordered]@{
    schemaVersion = 'unifiedscript.blockbreaker-gameplay-comparison/v0.1'
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    scenario = 'BlockBreaker-TutorialMap'
    frames = $Frames
    engineRoot = $EngineRoot
    workload = [ordered]@{
        fixedTimestepFps = 60
        nullRhi = $true
        movementAndLookEveryFrame = $true
        initialShot = $false
    }
    puerts = [ordered]@{
        commit = (& git -C $puertsRoot rev-parse HEAD).Trim()
        elapsedMilliseconds = $puertsElapsed
        framesPerSecond = [double]$puertsMatch.Groups[3].Value
        scriptMovementFrames = [int]$puertsScriptMatch.Groups[1].Value
        scriptMovementElapsedMilliseconds = [double]$puertsScriptMatch.Groups[2].Value
    }
    unifiedScript = [ordered]@{
        commit = (& git -C $unifiedRoot rev-parse HEAD).Trim()
        elapsedMilliseconds = $unifiedElapsed
        framesPerSecond = [double]$unifiedMatch.Groups[3].Value
        shots = [int]$metricsMatch.Groups[2].Value
        hits = [int]$metricsMatch.Groups[3].Value
        fastCalls = [uint64]$metricsMatch.Groups[5].Value
        callFrameCalls = [uint64]$metricsMatch.Groups[6].Value
        legacyCalls = [uint64]$metricsMatch.Groups[7].Value
    }
    ratio = [ordered]@{
        unifiedScriptToPuertsElapsed = $unifiedElapsed / $puertsElapsed
        lowerIsBetter = $true
    }
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8

Write-Output "Puerts:       $puertsElapsed ms ($($result.puerts.framesPerSecond) FPS)"
Write-Output "UnifiedScript: $unifiedElapsed ms ($($result.unifiedScript.framesPerSecond) FPS)"
Write-Output "Elapsed ratio (UnifiedScript/Puerts): $($result.ratio.unifiedScriptToPuertsElapsed)"
Write-Output "Result: $resultPath"
