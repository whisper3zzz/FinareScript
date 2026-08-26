[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',
    [ValidateRange(1, 1000000)]
    [int]$Frames = 10000,
    [ValidateRange(1, 9)]
    [int]$Trials = 3,
    [switch]$SkipBuild,
    [switch]$UnifiedFirst
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$puertsRoot = Join-Path $ComparisonRoot 'BlockBreaker-Puerts'
$unifiedRoot = Join-Path $ComparisonRoot 'BlockBreaker-UnifiedScript'
$buildBat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\Build.bat'
$editorCmd = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
$map = '/Game/BlockBreaker/Maps/TutorialMap'
$mapRelativePath = 'Content\BlockBreaker\Maps\TutorialMap.umap'
$artifactRoot = Join-Path $repoRoot 'artifacts\comparison\gameplay'
$puertsLog = Join-Path $artifactRoot 'puerts-blockbreaker.log'
$unifiedLog = Join-Path $artifactRoot 'unifiedscript-blockbreaker.log'
$resultPath = Join-Path $artifactRoot 'comparison.json'
$puertsProject = Join-Path $puertsRoot 'BlockBreaker.uproject'
$unifiedProject = Join-Path $unifiedRoot 'BlockBreaker.uproject'
$unifiedRunner = Join-Path $unifiedRoot 'Tools\run_unifiedscript_demo.ps1'
$unifiedMap = '/Game/BlockBreaker/Maps/TutorialMap?game=/Script/BlockBreaker.UnifiedScriptBlockBreakerGameMode'

foreach ($required in @(
    $buildBat, $editorCmd, $puertsRoot, $unifiedRoot, $puertsProject,
    $unifiedProject, $unifiedRunner)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required gameplay comparison path does not exist: $required"
    }
}
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

function Get-Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

if ($Trials -gt 1) {
    if (-not $SkipBuild) {
        Write-Output 'Preparing both BlockBreaker runtimes before measured trials...'
        & $buildBat BlockBreakerEditor Win64 Development "-Project=$puertsProject" `
            -WaitMutex -NoHotReloadFromIDE -NoUBA -MaxParallelActions=4
        if ($LASTEXITCODE -ne 0) {
            throw "Puerts gameplay build failed with exit code $LASTEXITCODE."
        }
        $prepareOutput = @(& $unifiedRunner `
            -EngineRoot $EngineRoot `
            -UnifiedScriptRoot $repoRoot `
            -Headless 2>&1)
        $prepareExitCode = $LASTEXITCODE
        $prepareText = $prepareOutput -join [Environment]::NewLine
        if ($prepareExitCode -ne 0 -or
            -not $prepareText.Contains('UnifiedScript gameplay data + ABI v3 gate: PASS')) {
            $prepareOutput | Write-Output
            throw 'UnifiedScript gameplay preparation/smoke failed.'
        }
    }

    $trialResults = @()
    for ($trial = 1; $trial -le $Trials; ++$trial) {
        $innerArguments = @{
            EngineRoot = $EngineRoot
            ComparisonRoot = $ComparisonRoot
            Frames = $Frames
            Trials = 1
            SkipBuild = $true
        }
        if (($trial % 2) -eq 0) {
            $innerArguments.UnifiedFirst = $true
        }
        Write-Output "Running BlockBreaker gameplay trial $trial/$Trials..."
        & $PSCommandPath @innerArguments
        if ($LASTEXITCODE -ne 0) {
            exit $LASTEXITCODE
        }
        $trialResults += Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        Copy-Item -LiteralPath $puertsLog `
            -Destination (Join-Path $artifactRoot "puerts-blockbreaker-trial-$trial.log") -Force
        Copy-Item -LiteralPath $unifiedLog `
            -Destination (Join-Path $artifactRoot "unifiedscript-blockbreaker-trial-$trial.log") -Force
    }

    $first = $trialResults[0]
    $puertsElapsed = Get-Median @($trialResults | ForEach-Object {
        [double]$_.puerts.elapsedMilliseconds })
    $puertsScriptElapsed = Get-Median @($trialResults | ForEach-Object {
        [double]$_.puerts.scriptMovementElapsedMilliseconds })
    $unifiedElapsed = Get-Median @($trialResults | ForEach-Object {
        [double]$_.unifiedScript.elapsedMilliseconds })
    $result = [ordered]@{
        schemaVersion = 'unifiedscript.blockbreaker-gameplay-comparison/v0.3'
        generatedAtUtc = [DateTime]::UtcNow.ToString('O')
        scenario = $first.scenario
        frames = $Frames
        trialCount = $Trials
        aggregation = 'median'
        engineRoot = $EngineRoot
        workload = $first.workload
        puerts = [ordered]@{
            commit = $first.puerts.commit
            elapsedMilliseconds = $puertsElapsed
            framesPerSecond = $Frames * 1000.0 / $puertsElapsed
            scriptMovementFrames = $Frames
            scriptMovementElapsedMilliseconds = $puertsScriptElapsed
            shots = 1
            hits = 1
        }
        unifiedScript = [ordered]@{
            commit = $first.unifiedScript.commit
            elapsedMilliseconds = $unifiedElapsed
            framesPerSecond = $Frames * 1000.0 / $unifiedElapsed
            shots = 1
            hits = 1
            fastCalls = [uint64]$first.unifiedScript.fastCalls
            callFrameCalls = [uint64]$first.unifiedScript.callFrameCalls
            legacyCalls = 0
        }
        ratio = [ordered]@{
            unifiedScriptToPuertsElapsed = $unifiedElapsed / $puertsElapsed
            lowerIsBetter = $true
        }
        trials = @($trialResults | ForEach-Object -Begin { $index = 0 } -Process {
            ++$index
            [ordered]@{
                trial = $index
                puertsElapsedMilliseconds = [double]$_.puerts.elapsedMilliseconds
                puertsScriptElapsedMilliseconds =
                    [double]$_.puerts.scriptMovementElapsedMilliseconds
                unifiedScriptElapsedMilliseconds =
                    [double]$_.unifiedScript.elapsedMilliseconds
                unifiedScriptToPuertsElapsed =
                    [double]$_.unifiedScript.elapsedMilliseconds /
                    [double]$_.puerts.elapsedMilliseconds
            }
        })
    }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Output "Puerts median:        $puertsElapsed ms ($($result.puerts.framesPerSecond) FPS)"
    Write-Output "UnifiedScript median: $unifiedElapsed ms ($($result.unifiedScript.framesPerSecond) FPS)"
    Write-Output "Median elapsed ratio (UnifiedScript/Puerts): $($result.ratio.unifiedScriptToPuertsElapsed)"
    Write-Output "Result: $resultPath"
    exit 0
}

$puertsMapHash = (Get-FileHash -LiteralPath (Join-Path $puertsRoot $mapRelativePath) `
    -Algorithm SHA256).Hash
$unifiedMapHash = (Get-FileHash -LiteralPath (Join-Path $unifiedRoot $mapRelativePath) `
    -Algorithm SHA256).Hash
$puertsAssetRoot = Join-Path $puertsRoot 'Content\BlockBreaker'
$unifiedAssetRoot = Join-Path $unifiedRoot 'Content\BlockBreaker'
$sharedAssetPaths = @(Get-ChildItem -LiteralPath $puertsAssetRoot -Recurse -File |
    Where-Object { $_.FullName -ne (Join-Path $puertsRoot $mapRelativePath) } |
    ForEach-Object { $_.FullName.Substring($puertsAssetRoot.Length + 1) } |
    Sort-Object)
$unifiedSharedAssetPaths = @(Get-ChildItem -LiteralPath $unifiedAssetRoot -Recurse -File |
    Where-Object { $_.FullName -ne (Join-Path $unifiedRoot $mapRelativePath) } |
    ForEach-Object { $_.FullName.Substring($unifiedAssetRoot.Length + 1) } |
    Sort-Object)
$assetPathDifference = @(Compare-Object $sharedAssetPaths $unifiedSharedAssetPaths)
if ($assetPathDifference.Count -ne 0) {
    throw "BlockBreaker shared asset sets differ: $($assetPathDifference | Out-String)"
}
foreach ($relativeAssetPath in $sharedAssetPaths) {
    $puertsAssetPath = Join-Path $puertsAssetRoot $relativeAssetPath
    $unifiedAssetPath = Join-Path $unifiedAssetRoot $relativeAssetPath
    if (-not (Test-Path -LiteralPath $unifiedAssetPath) -or
        (Get-FileHash -LiteralPath $puertsAssetPath -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $unifiedAssetPath -Algorithm SHA256).Hash) {
        throw "BlockBreaker shared asset mismatch: $relativeAssetPath"
    }
}

if (-not $SkipBuild) {
    & $buildBat BlockBreakerEditor Win64 Development "-Project=$puertsProject" `
        -WaitMutex -NoHotReloadFromIDE -NoUBA -MaxParallelActions=4
    if ($LASTEXITCODE -ne 0) {
        throw "Puerts gameplay build failed with exit code $LASTEXITCODE."
    }
}

function Invoke-PuertsWorkload {
    $output = @(& $editorCmd $puertsProject $map -game -nullrhi -unattended `
        -nosound -nosplash -stdout -FullStdOutLogOutput -benchmark -fps=60 `
        "-BlockBreakerBenchmarkFrames=$Frames" 2>&1)
    [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
}

function Invoke-UnifiedScriptWorkload {
    if ($SkipBuild) {
        $output = @(& $editorCmd $unifiedProject $unifiedMap -game -nullrhi `
            -unattended -nosound -nosplash -stdout -FullStdOutLogOutput `
            -benchmark -fps=60 "-BlockBreakerBenchmarkFrames=$Frames" 2>&1)
    } else {
        $output = @(& $unifiedRunner `
            -EngineRoot $EngineRoot `
            -UnifiedScriptRoot $repoRoot `
            -Headless `
            -GameplayBenchmarkFrames $Frames 2>&1)
    }
    [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
}

if ($UnifiedFirst) {
    $unifiedRun = Invoke-UnifiedScriptWorkload
    $puertsRun = Invoke-PuertsWorkload
} else {
    $puertsRun = Invoke-PuertsWorkload
    $unifiedRun = Invoke-UnifiedScriptWorkload
}
$puertsOutput = @($puertsRun.Output)
$puertsExitCode = $puertsRun.ExitCode
$puertsOutput | Set-Content -LiteralPath $puertsLog -Encoding utf8
$puertsText = $puertsOutput -join [Environment]::NewLine
$unifiedOutput = @($unifiedRun.Output)
$unifiedExitCode = $unifiedRun.ExitCode
$unifiedOutput | Set-Content -LiteralPath $unifiedLog -Encoding utf8
$unifiedText = $unifiedOutput -join [Environment]::NewLine

$puertsMatch = [regex]::Match(
    $puertsText,
    'Puerts BlockBreaker benchmark: Frames=(\d+), ElapsedMs=([0-9.]+), FPS=([0-9.]+)')
$puertsScriptMatch = [regex]::Match(
    $puertsText,
    'Puerts BlockBreaker script workload: Frames=(\d+), ElapsedMs=([0-9.]+), Shots=(\d+), Hits=(\d+)')
$unifiedMatch = [regex]::Match(
    $unifiedText,
    'UnifiedScript BlockBreaker benchmark: Frames=(\d+), ElapsedMs=([0-9.]+), FPS=([0-9.]+)')
$metricsMatch = [regex]::Match(
    $unifiedText,
    'ManagedTicks=(\d+), Shots=(\d+), Hits=(\d+), Status=([^,\r\n]+), FastCalls=(\d+), CallFrameCalls=(\d+), LegacyCalls=(\d+)')

if ($puertsExitCode -ne 0 -or -not $puertsMatch.Success -or
    -not $puertsScriptMatch.Success -or
    [int]$puertsScriptMatch.Groups[1].Value -ne $Frames -or
    [int]$puertsScriptMatch.Groups[3].Value -ne 1 -or
    [int]$puertsScriptMatch.Groups[4].Value -ne 1 -or
    [int]$puertsMatch.Groups[1].Value -ne $Frames) {
    throw "Puerts did not complete the exact $Frames-frame workload. Log: $puertsLog"
}
if ($unifiedExitCode -ne 0 -or -not $unifiedMatch.Success -or
    [int]$unifiedMatch.Groups[1].Value -ne $Frames -or -not $metricsMatch.Success -or
    [int]$metricsMatch.Groups[1].Value -ne $Frames -or
    [int]$metricsMatch.Groups[2].Value -ne 1 -or
    [int]$metricsMatch.Groups[3].Value -ne 1 -or
    [uint64]$metricsMatch.Groups[7].Value -ne 0) {
    throw "UnifiedScript did not complete the exact $Frames-frame fast-path workload. Log: $unifiedLog"
}

$puertsElapsed = [double]$puertsMatch.Groups[2].Value
$unifiedElapsed = [double]$unifiedMatch.Groups[2].Value
$result = [ordered]@{
    schemaVersion = 'unifiedscript.blockbreaker-gameplay-comparison/v0.3'
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    scenario = 'BlockBreaker-TutorialMap'
    frames = $Frames
    trialCount = 1
    aggregation = 'single'
    engineRoot = $EngineRoot
    workload = [ordered]@{
        fixedTimestepFps = 60
        nullRhi = $true
        movementAndLookEveryFrame = $true
        scriptedShotAtMidpoint = $true
        sharedAssetCount = $sharedAssetPaths.Count
        sharedAssetsByteIdentical = $true
        puertsTutorialMapSha256 = $puertsMapHash
        unifiedScriptTutorialMapSha256 = $unifiedMapHash
        mapMigration = 'DefaultGameModeMigration'
    }
    puerts = [ordered]@{
        commit = (& git -C $puertsRoot rev-parse HEAD).Trim()
        elapsedMilliseconds = $puertsElapsed
        framesPerSecond = [double]$puertsMatch.Groups[3].Value
        scriptMovementFrames = [int]$puertsScriptMatch.Groups[1].Value
        scriptMovementElapsedMilliseconds = [double]$puertsScriptMatch.Groups[2].Value
        shots = [int]$puertsScriptMatch.Groups[3].Value
        hits = [int]$puertsScriptMatch.Groups[4].Value
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
