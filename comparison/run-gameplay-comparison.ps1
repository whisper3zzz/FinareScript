[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',
    [ValidateRange(1, 1000000)]
    [int]$Frames = 10000,
    [ValidateRange(0, 1000000)]
    [int]$WarmupFrames = 300,
    [ValidateRange(1, 9)]
    [int]$Trials = 3,
    [ValidateRange(1, 3)]
    [int]$SuiteTrial = 1,
    [string]$PackageMetricsPath,
    [switch]$WriteSuiteCaptures,
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
$migrationAudit = Join-Path $PSScriptRoot 'audit-blockbreaker-migration.ps1'
$captureWriter = Join-Path $PSScriptRoot 'write-workload-capture.ps1'
$runtimeIdentityScript = Join-Path $PSScriptRoot `
    'get-runtime-source-identity.ps1'
$suiteManifestPath = Join-Path $repoRoot 'benchmarks\unreal\workload-suite.v1.1.0.json'
$unifiedMap = '/Game/BlockBreaker/Maps/TutorialMap?game=/Script/BlockBreaker.UnifiedScriptBlockBreakerGameMode'
$totalGameplayFrames = $WarmupFrames + $Frames
# Boot CSV capture contributes one startup row before gameplay. The runtime stays
# alive for one tail tick so the remaining rows contain every warmup/measured frame.
$csvCaptureFrames = $totalGameplayFrames + 1
if ([string]::IsNullOrWhiteSpace($PackageMetricsPath)) {
    $PackageMetricsPath = Join-Path $repoRoot `
        'artifacts\comparison\package-size\latest.json'
}

foreach ($required in @(
    $buildBat, $editorCmd, $puertsRoot, $unifiedRoot, $puertsProject,
    $unifiedProject, $unifiedRunner, $migrationAudit, $captureWriter,
    $runtimeIdentityScript,
    $suiteManifestPath, $PackageMetricsPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required gameplay comparison path does not exist: $required"
    }
}
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

$packageMetrics = Get-Content -LiteralPath $PackageMetricsPath -Raw |
    ConvertFrom-Json
$puertsCommit = (& git -C $puertsRoot rev-parse HEAD).Trim()
$unifiedCommit = (& git -C $unifiedRoot rev-parse HEAD).Trim()
if ($packageMetrics.schemaVersion -ne
        'unifiedscript.blockbreaker-package-size/v0.1' -or
    $packageMetrics.engineVersion -ne '5.8.1' -or
    $packageMetrics.platform -ne 'Win64' -or
    $packageMetrics.configuration -ne 'Development' -or
    $packageMetrics.map -ne $map -or
    $packageMetrics.puerts.commit -ne $puertsCommit -or
    $packageMetrics.unifiedScript.commit -ne $unifiedCommit -or
    [bool]$packageMetrics.puerts.trackedWorkingTreeDirty -or
    [bool]$packageMetrics.unifiedScript.trackedWorkingTreeDirty -or
    [uint64]$packageMetrics.puerts.packageSizeBytes -eq 0 -or
    [uint64]$packageMetrics.unifiedScript.packageSizeBytes -eq 0) {
    throw (
        'Package metrics do not match the current UE 5.8.1 Development ' +
        "BlockBreaker inputs: $PackageMetricsPath")
}
$puertsPackageSizeBytes = [uint64]$packageMetrics.puerts.packageSizeBytes
$unifiedPackageSizeBytes = [uint64]$packageMetrics.unifiedScript.packageSizeBytes

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

$puertsRuntimeSourceIdentity = Get-RuntimeSourceIdentity 'puerts' $puertsRoot
$unifiedRuntimeSourceIdentity = Get-RuntimeSourceIdentity `
    'unifiedScript' $repoRoot

$suiteManifest = Get-Content -LiteralPath $suiteManifestPath -Raw |
    ConvertFrom-Json
$suiteWorkload = @($suiteManifest.workloads) |
    Where-Object id -eq 'blockbreaker-complete' |
    Select-Object -First 1
if ($null -eq $suiteWorkload) {
    throw "The performance suite does not contain 'blockbreaker-complete'."
}
if ($WriteSuiteCaptures -and
    ($Frames -ne [int]$suiteWorkload.measuredIterations -or
        $WarmupFrames -ne [int]$suiteWorkload.warmupIterations)) {
    throw (
        'Suite captures require the frozen blockbreaker-complete recipe: ' +
        "$($suiteWorkload.warmupIterations) warmup and " +
        "$($suiteWorkload.measuredIterations) measured frames.")
}
& $migrationAudit -EngineRoot $EngineRoot -ComparisonRoot $ComparisonRoot `
    -ReportPath (Join-Path $artifactRoot 'migration-audit.json')
if ($LASTEXITCODE -ne 0) {
    throw 'BlockBreaker whole-project migration audit failed before comparison.'
}

function Get-Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-Percentile([double[]]$Values, [double]$Percentile) {
    if ($Values.Count -eq 0) {
        throw 'Cannot calculate a percentile for an empty sample.'
    }
    $sorted = @($Values | Sort-Object)
    $index = [Math]::Max(
        0,
        [Math]::Min(
            $sorted.Count - 1,
            [int][Math]::Ceiling($Percentile * $sorted.Count) - 1))
    return [double]$sorted[$index]
}

function Get-UnrealCsvMetrics(
    [string]$OutputText,
    [int]$MeasuredFrames,
    [int]$ExpectedRows) {
    $pathMatches = [regex]::Matches(
        $OutputText,
        'Capture Ended\. Writing CSV to file\s*:\s*(?<path>[^\r\n]+?\.csv)')
    if ($pathMatches.Count -eq 0) {
        throw 'Unreal CSV profiler did not publish a completed CSV path.'
    }
    $csvPath = $pathMatches[$pathMatches.Count - 1].Groups['path'].Value.Trim()
    if (-not (Test-Path -LiteralPath $csvPath)) {
        throw "Unreal CSV profiler output does not exist: $csvPath"
    }

    Add-Type -AssemblyName Microsoft.VisualBasic
    $parser = [Microsoft.VisualBasic.FileIO.TextFieldParser]::new($csvPath)
    $parser.SetDelimiters(',')
    $parser.HasFieldsEnclosedInQuotes = $true
    try {
        $headers = $parser.ReadFields()
        $frameTimeIndex = [Array]::IndexOf($headers, 'FrameTime')
        $physicalUsedIndex = [Array]::IndexOf($headers, 'PhysicalUsedMB')
        $gcCountIndex = [Array]::IndexOf($headers, 'GC/Count')
        $gcPauseIndex = [Array]::IndexOf(
            $headers,
            'Exclusive/GameThread/GarbageCollection')
        if ($frameTimeIndex -lt 0 -or $physicalUsedIndex -lt 0 -or
            $gcCountIndex -lt 0 -or $gcPauseIndex -lt 0) {
            throw "Unreal CSV is missing required Basic columns: $csvPath"
        }

        $frameTimes = [System.Collections.Generic.List[double]]::new()
        $physicalUsedMegabytes = [System.Collections.Generic.List[double]]::new()
        $gcCounts = [System.Collections.Generic.List[double]]::new()
        $gcPauses = [System.Collections.Generic.List[double]]::new()
        $culture = [System.Globalization.CultureInfo]::InvariantCulture
        $style = [System.Globalization.NumberStyles]::Float
        while (-not $parser.EndOfData) {
            $fields = $parser.ReadFields()
            if ($null -eq $fields -or $fields.Count -le $frameTimeIndex) {
                continue
            }
            if ($fields[0] -eq 'EVENTS' -or $fields[0].StartsWith('[')) {
                break
            }
            [double]$frameTime = 0
            if (-not [double]::TryParse(
                    $fields[$frameTimeIndex], $style, $culture, [ref]$frameTime)) {
                continue
            }
            [double]$physicalUsed = 0
            [double]$gcCount = 0
            [double]$gcPause = 0
            [void][double]::TryParse(
                $fields[$physicalUsedIndex], $style, $culture, [ref]$physicalUsed)
            [void][double]::TryParse(
                $fields[$gcCountIndex], $style, $culture, [ref]$gcCount)
            [void][double]::TryParse(
                $fields[$gcPauseIndex], $style, $culture, [ref]$gcPause)
            $frameTimes.Add($frameTime)
            $physicalUsedMegabytes.Add($physicalUsed)
            $gcCounts.Add($gcCount)
            $gcPauses.Add($gcPause)
        }
    } finally {
        $parser.Dispose()
    }

    if ($frameTimes.Count -ne $ExpectedRows -or
        $frameTimes.Count -lt $MeasuredFrames + 1) {
        throw (
            "Unreal CSV row count mismatch: expected $ExpectedRows, " +
            "observed $($frameTimes.Count), path=$csvPath")
    }
    $measuredOffset = $frameTimes.Count - $MeasuredFrames
    $measuredFrameTimes = @(
        $frameTimes.GetRange($measuredOffset, $MeasuredFrames).ToArray())
    $measuredGcCount = 0.0
    $measuredGcPause = 0.0
    for ($index = $measuredOffset; $index -lt $frameTimes.Count; ++$index) {
        $measuredGcCount += $gcCounts[$index]
        $measuredGcPause += $gcPauses[$index]
    }
    return [pscustomobject]@{
        Path = $csvPath
        RowCount = $frameTimes.Count
        FrameTimeMedianMilliseconds = Get-Median $measuredFrameTimes
        FrameTimeP95Milliseconds = Get-Percentile $measuredFrameTimes 0.95
        FrameTimeP99Milliseconds = Get-Percentile $measuredFrameTimes 0.99
        MemoryHighWaterBytes = [uint64](
            ($physicalUsedMegabytes | Measure-Object -Maximum).Maximum * 1MB)
        StartupMilliseconds = [double]$frameTimes[0]
        UnrealGcCollectionCount = $measuredGcCount
        UnrealGcPauseMilliseconds = $measuredGcPause
    }
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
            WarmupFrames = $WarmupFrames
            Trials = 1
            SuiteTrial = $trial
            PackageMetricsPath = $PackageMetricsPath
            SkipBuild = $true
        }
        if ($WriteSuiteCaptures) {
            $innerArguments.WriteSuiteCaptures = $true
        }
        if (($trial % 2) -eq 1) {
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
    $puertsFrameMedian = Get-Median @($trialResults | ForEach-Object {
        [double]$_.puerts.frameTimeMedianMilliseconds })
    $unifiedFrameMedian = Get-Median @($trialResults | ForEach-Object {
        [double]$_.unifiedScript.frameTimeMedianMilliseconds })
    $puertsBridge = Get-Median @($trialResults | ForEach-Object {
        [double]$_.puerts.bridgeTimeMilliseconds })
    $unifiedBridge = Get-Median @($trialResults | ForEach-Object {
        [double]$_.unifiedScript.bridgeTimeMilliseconds })
    $result = [ordered]@{
        schemaVersion = 'unifiedscript.blockbreaker-gameplay-comparison/v0.4'
        generatedAtUtc = [DateTime]::UtcNow.ToString('O')
        scenario = $first.scenario
        frames = $Frames
        warmupFrames = $WarmupFrames
        trialCount = $Trials
        aggregation = 'median'
        engineRoot = $EngineRoot
        workload = $first.workload
        puerts = [ordered]@{
            commit = $first.puerts.commit
            runtimeSourceIdentity = $first.puerts.runtimeSourceIdentity
            elapsedMilliseconds = $puertsElapsed
            framesPerSecond = $Frames * 1000.0 / $puertsElapsed
            scriptMovementFrames = $Frames
            scriptMovementElapsedMilliseconds = $puertsScriptElapsed
            shots = 1
            hits = 1
            frameTimeMedianMilliseconds = $puertsFrameMedian
            frameTimeP95Milliseconds = Get-Median @($trialResults | ForEach-Object {
                [double]$_.puerts.frameTimeP95Milliseconds })
            frameTimeP99Milliseconds = Get-Median @($trialResults | ForEach-Object {
                [double]$_.puerts.frameTimeP99Milliseconds })
            bridgeTimeMilliseconds = $puertsBridge
            calls = [uint64](Get-Median @($trialResults | ForEach-Object {
                [double]$_.puerts.calls }))
            bytesCopied = [uint64](Get-Median @($trialResults | ForEach-Object {
                [double]$_.puerts.bytesCopied }))
            allocatedBytes = [uint64](Get-Median @($trialResults | ForEach-Object {
                [double]$_.puerts.allocatedBytes }))
            gcCollectionCount = [double](Get-Median @($trialResults | ForEach-Object {
                [double]$_.puerts.gcCollectionCount }))
            gcPauseMilliseconds = Get-Median @($trialResults | ForEach-Object {
                [double]$_.puerts.gcPauseMilliseconds })
            memoryHighWaterBytes = [uint64](Get-Median @($trialResults | ForEach-Object {
                [double]$_.puerts.memoryHighWaterBytes }))
            startupMilliseconds = Get-Median @($trialResults | ForEach-Object {
                [double]$_.puerts.startupMilliseconds })
            reloadMilliseconds = Get-Median @($trialResults | ForEach-Object {
                [double]$_.puerts.reloadMilliseconds })
            packageSizeBytes = $puertsPackageSizeBytes
        }
        unifiedScript = [ordered]@{
            commit = $first.unifiedScript.commit
            runtimeSourceIdentity = $first.unifiedScript.runtimeSourceIdentity
            elapsedMilliseconds = $unifiedElapsed
            framesPerSecond = $Frames * 1000.0 / $unifiedElapsed
            shots = 1
            hits = 1
            fastCalls = [uint64]$first.unifiedScript.fastCalls
            callFrameCalls = [uint64]$first.unifiedScript.callFrameCalls
            legacyCalls = 0
            frameTimeMedianMilliseconds = $unifiedFrameMedian
            frameTimeP95Milliseconds = Get-Median @($trialResults | ForEach-Object {
                [double]$_.unifiedScript.frameTimeP95Milliseconds })
            frameTimeP99Milliseconds = Get-Median @($trialResults | ForEach-Object {
                [double]$_.unifiedScript.frameTimeP99Milliseconds })
            bridgeTimeMilliseconds = $unifiedBridge
            calls = [uint64](Get-Median @($trialResults | ForEach-Object {
                [double]$_.unifiedScript.calls }))
            bytesCopied = [uint64](Get-Median @($trialResults | ForEach-Object {
                [double]$_.unifiedScript.bytesCopied }))
            allocatedBytes = [uint64](Get-Median @($trialResults | ForEach-Object {
                [double]$_.unifiedScript.allocatedBytes }))
            gcCollectionCount = [double](Get-Median @($trialResults | ForEach-Object {
                [double]$_.unifiedScript.gcCollectionCount }))
            gcPauseMilliseconds = Get-Median @($trialResults | ForEach-Object {
                [double]$_.unifiedScript.gcPauseMilliseconds })
            memoryHighWaterBytes = [uint64](Get-Median @($trialResults | ForEach-Object {
                [double]$_.unifiedScript.memoryHighWaterBytes }))
            startupMilliseconds = Get-Median @($trialResults | ForEach-Object {
                [double]$_.unifiedScript.startupMilliseconds })
            reloadMilliseconds = Get-Median @($trialResults | ForEach-Object {
                [double]$_.unifiedScript.reloadMilliseconds })
            packageSizeBytes = $unifiedPackageSizeBytes
        }
        ratio = [ordered]@{
            unifiedScriptToPuertsElapsed = $unifiedElapsed / $puertsElapsed
            unifiedScriptToPuertsFrameMedian = $unifiedFrameMedian / $puertsFrameMedian
            unifiedScriptToPuertsBridge = $unifiedBridge / $puertsBridge
            unifiedScriptToPuertsPackageSize =
                [double]$unifiedPackageSizeBytes / [double]$puertsPackageSizeBytes
            allocatedBytes = $null
            lowerIsBetter = $true
        }
        trials = @($trialResults | ForEach-Object -Begin { $index = 0 } -Process {
            ++$index
            [ordered]@{
                trial = $index
                executionOrder = [string]$_.executionOrder
                puertsElapsedMilliseconds = [double]$_.puerts.elapsedMilliseconds
                puertsScriptElapsedMilliseconds =
                    [double]$_.puerts.scriptMovementElapsedMilliseconds
                unifiedScriptElapsedMilliseconds =
                    [double]$_.unifiedScript.elapsedMilliseconds
                unifiedScriptToPuertsElapsed =
                    [double]$_.unifiedScript.elapsedMilliseconds /
                    [double]$_.puerts.elapsedMilliseconds
                unifiedScriptToPuertsFrameMedian =
                    [double]$_.unifiedScript.frameTimeMedianMilliseconds /
                    [double]$_.puerts.frameTimeMedianMilliseconds
                unifiedScriptToPuertsBridge =
                    [double]$_.unifiedScript.bridgeTimeMilliseconds /
                    [double]$_.puerts.bridgeTimeMilliseconds
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
    $runtimeArguments = @(
        $puertsProject, $map, '-game', '-nullrhi', '-unattended', '-nosound',
        '-nosplash', '-stdout', '-FullStdOutLogOutput', '-benchmark', '-fps=60',
        "-BlockBreakerBenchmarkFrames=$Frames",
        "-BlockBreakerBenchmarkWarmupFrames=$WarmupFrames",
        '-BlockBreakerBenchmarkReload',
        '-BlockBreakerBenchmarkCsvTailFrames=1',
        "-csvCaptureFrames=$csvCaptureFrames",
        '-csvCategories=Basic', '-csvCompression=0', '-csv.BlockOnCaptureEnd=1')
    $output = @(& $editorCmd @runtimeArguments 2>&1)
    [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
}

function Invoke-UnifiedScriptWorkload {
    if ($SkipBuild) {
        $runtimeArguments = @(
            $unifiedProject, $unifiedMap, '-game', '-nullrhi', '-unattended',
            '-nosound', '-nosplash', '-stdout', '-FullStdOutLogOutput',
            '-benchmark', '-fps=60', "-BlockBreakerBenchmarkFrames=$Frames",
            "-BlockBreakerBenchmarkWarmupFrames=$WarmupFrames",
            '-BlockBreakerBenchmarkReload',
            '-BlockBreakerBenchmarkCsvTailFrames=1',
            "-csvCaptureFrames=$csvCaptureFrames",
            '-csvCategories=Basic', '-csvCompression=0', '-csv.BlockOnCaptureEnd=1')
        $output = @(& $editorCmd @runtimeArguments 2>&1)
    } else {
        $output = @(& $unifiedRunner `
            -EngineRoot $EngineRoot `
            -UnifiedScriptRoot $repoRoot `
            -Headless `
            -GameplayBenchmarkFrames $Frames `
            -GameplayBenchmarkWarmupFrames $WarmupFrames `
            -GameplayBenchmarkReload `
            -CsvCaptureFrames $csvCaptureFrames 2>&1)
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
    'Puerts BlockBreaker benchmark: Frames=(\d+), WarmupFrames=(\d+), TotalFrames=(\d+), ElapsedMs=([0-9.]+), FPS=([0-9.]+), BridgeMs=([0-9.]+), Calls=(\d+), Bytes=(\d+)')
$puertsScriptMatch = [regex]::Match(
    $puertsText,
    'Puerts BlockBreaker script workload: Frames=(\d+), ElapsedMs=([0-9.]+), Shots=(\d+), Hits=(\d+), Allocated=(\d+), GC=(\d+), GCPauseMs=([0-9.]+)')
$puertsReloadMatch = [regex]::Match(
    $puertsText,
    'Puerts BlockBreaker reload: Success=(\d+), ElapsedMs=([0-9.]+)')
$unifiedMatch = [regex]::Match(
    $unifiedText,
    'UnifiedScript BlockBreaker benchmark: Frames=(\d+), WarmupFrames=(\d+), TotalFrames=(\d+), ElapsedMs=([0-9.]+), FPS=([0-9.]+)')
$unifiedRuntimeMatch = [regex]::Match(
    $unifiedText,
    'UnifiedScript BlockBreaker runtime metrics: Frames=(\d+), BridgeMs=([0-9.]+), Calls=(\d+), Bytes=(\d+), Allocated=(\d+), GC=(\d+), GCPauseMs=([0-9.]+)')
$unifiedReloadMatch = [regex]::Match(
    $unifiedText,
    'UnifiedScript BlockBreaker reload: Success=(\d+), ElapsedMs=([0-9.]+)')
$metricsMatch = [regex]::Match(
    $unifiedText,
    'ManagedTicks=(\d+), Shots=(\d+), Hits=(\d+), Status=([^,\r\n]+), FastCalls=(\d+), CallFrameCalls=(\d+), LegacyCalls=(\d+)')

if ($puertsExitCode -ne 0 -or -not $puertsMatch.Success -or
    -not $puertsScriptMatch.Success -or
    -not $puertsReloadMatch.Success -or
    [int]$puertsReloadMatch.Groups[1].Value -ne 1 -or
    [int]$puertsScriptMatch.Groups[1].Value -ne $Frames -or
    [int]$puertsScriptMatch.Groups[3].Value -ne 1 -or
    [int]$puertsScriptMatch.Groups[4].Value -ne 1 -or
    [int]$puertsMatch.Groups[1].Value -ne $Frames -or
    [int]$puertsMatch.Groups[2].Value -ne $WarmupFrames -or
    [int]$puertsMatch.Groups[3].Value -ne $totalGameplayFrames) {
    throw "Puerts did not complete the exact $Frames-frame workload. Log: $puertsLog"
}
if ($unifiedExitCode -ne 0 -or -not $unifiedMatch.Success -or
    [int]$unifiedMatch.Groups[1].Value -ne $Frames -or -not $metricsMatch.Success -or
    [int]$unifiedMatch.Groups[2].Value -ne $WarmupFrames -or
    [int]$unifiedMatch.Groups[3].Value -ne $totalGameplayFrames -or
    -not $unifiedRuntimeMatch.Success -or
    -not $unifiedReloadMatch.Success -or
    [int]$unifiedReloadMatch.Groups[1].Value -ne 1 -or
    [int]$unifiedRuntimeMatch.Groups[1].Value -ne $Frames -or
    [int]$metricsMatch.Groups[1].Value -lt $totalGameplayFrames -or
    [int]$metricsMatch.Groups[1].Value -gt ($totalGameplayFrames + 1) -or
    [int]$metricsMatch.Groups[2].Value -ne 1 -or
    [int]$metricsMatch.Groups[3].Value -ne 1 -or
    [uint64]$metricsMatch.Groups[7].Value -ne 0) {
    throw "UnifiedScript did not complete the exact $Frames-frame fast-path workload. Log: $unifiedLog"
}

$puertsCsv = Get-UnrealCsvMetrics $puertsText $Frames $csvCaptureFrames
$unifiedCsv = Get-UnrealCsvMetrics $unifiedText $Frames $csvCaptureFrames
$puertsElapsed = [double]$puertsMatch.Groups[4].Value
$unifiedElapsed = [double]$unifiedMatch.Groups[4].Value
$result = [ordered]@{
    schemaVersion = 'unifiedscript.blockbreaker-gameplay-comparison/v0.4'
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    scenario = 'BlockBreaker-TutorialMap'
    frames = $Frames
    warmupFrames = $WarmupFrames
    trialCount = 1
    aggregation = 'single'
    executionOrder = if ($UnifiedFirst) { 'UnifiedScript->Puerts' } else { 'Puerts->UnifiedScript' }
    engineRoot = $EngineRoot
    workload = [ordered]@{
        fixedTimestepFps = 60
        nullRhi = $true
        csvCaptureRows = $csvCaptureFrames
        csvMeasuredWindow = 'last measured frames after boot row and gameplay warmup'
        movementAndLookEveryFrame = $true
        scriptedShotAtMidpoint = $true
        sharedAssetCount = $sharedAssetPaths.Count
        sharedAssetsByteIdentical = $true
        puertsTutorialMapSha256 = $puertsMapHash
        unifiedScriptTutorialMapSha256 = $unifiedMapHash
        mapMigration = 'DefaultGameModeMigration'
    }
    puerts = [ordered]@{
        commit = $puertsCommit
        runtimeSourceIdentity = $puertsRuntimeSourceIdentity
        elapsedMilliseconds = $puertsElapsed
        framesPerSecond = [double]$puertsMatch.Groups[5].Value
        scriptMovementFrames = [int]$puertsScriptMatch.Groups[1].Value
        scriptMovementElapsedMilliseconds = [double]$puertsScriptMatch.Groups[2].Value
        shots = [int]$puertsScriptMatch.Groups[3].Value
        hits = [int]$puertsScriptMatch.Groups[4].Value
        frameTimeMedianMilliseconds = $puertsCsv.FrameTimeMedianMilliseconds
        frameTimeP95Milliseconds = $puertsCsv.FrameTimeP95Milliseconds
        frameTimeP99Milliseconds = $puertsCsv.FrameTimeP99Milliseconds
        bridgeTimeMilliseconds = [double]$puertsMatch.Groups[6].Value
        calls = [uint64]$puertsMatch.Groups[7].Value
        bytesCopied = [uint64]$puertsMatch.Groups[8].Value
        allocatedBytes = [uint64]$puertsScriptMatch.Groups[5].Value
        gcCollectionCount = [double]$puertsScriptMatch.Groups[6].Value
        gcPauseMilliseconds = [double]$puertsScriptMatch.Groups[7].Value
        memoryHighWaterBytes = [uint64]$puertsCsv.MemoryHighWaterBytes
        startupMilliseconds = [double]$puertsCsv.StartupMilliseconds
        reloadMilliseconds = [double]$puertsReloadMatch.Groups[2].Value
        packageSizeBytes = $puertsPackageSizeBytes
        diagnostics = [ordered]@{
            csvPath = $puertsCsv.Path
            csvRows = $puertsCsv.RowCount
            unrealGcCollectionCount = $puertsCsv.UnrealGcCollectionCount
            unrealGcPauseMilliseconds = $puertsCsv.UnrealGcPauseMilliseconds
            allocationSemantic = 'V8 used-heap growth'
            bridgeSemantic = 'five native ProcessEvent callback envelopes per frame'
            reloadSemantic = 'synchronous Puerts module cache reexecution'
            packageMetricsPath = $PackageMetricsPath
        }
    }
    unifiedScript = [ordered]@{
        commit = $unifiedCommit
        runtimeSourceIdentity = $unifiedRuntimeSourceIdentity
        elapsedMilliseconds = $unifiedElapsed
        framesPerSecond = [double]$unifiedMatch.Groups[5].Value
        shots = [int]$metricsMatch.Groups[2].Value
        hits = [int]$metricsMatch.Groups[3].Value
        fastCalls = [uint64]$metricsMatch.Groups[5].Value
        callFrameCalls = [uint64]$metricsMatch.Groups[6].Value
        legacyCalls = [uint64]$metricsMatch.Groups[7].Value
        frameTimeMedianMilliseconds = $unifiedCsv.FrameTimeMedianMilliseconds
        frameTimeP95Milliseconds = $unifiedCsv.FrameTimeP95Milliseconds
        frameTimeP99Milliseconds = $unifiedCsv.FrameTimeP99Milliseconds
        bridgeTimeMilliseconds = [double]$unifiedRuntimeMatch.Groups[2].Value
        calls = [uint64]$unifiedRuntimeMatch.Groups[3].Value
        bytesCopied = [uint64]$unifiedRuntimeMatch.Groups[4].Value
        allocatedBytes = [uint64]$unifiedRuntimeMatch.Groups[5].Value
        gcCollectionCount = [double]$unifiedRuntimeMatch.Groups[6].Value
        gcPauseMilliseconds = [double]$unifiedRuntimeMatch.Groups[7].Value
        memoryHighWaterBytes = [uint64]$unifiedCsv.MemoryHighWaterBytes
        startupMilliseconds = [double]$unifiedCsv.StartupMilliseconds
        reloadMilliseconds = [double]$unifiedReloadMatch.Groups[2].Value
        packageSizeBytes = $unifiedPackageSizeBytes
        diagnostics = [ordered]@{
            csvPath = $unifiedCsv.Path
            csvRows = $unifiedCsv.RowCount
            unrealGcCollectionCount = $unifiedCsv.UnrealGcCollectionCount
            unrealGcPauseMilliseconds = $unifiedCsv.UnrealGcPauseMilliseconds
            allocationSemantic = 'CLR cumulative allocation'
            bridgeSemantic = 'one managed Actor OnTick callback envelope per frame'
            reloadSemantic = 'full ScriptComponent generation reload'
            packageMetricsPath = $PackageMetricsPath
        }
    }
    ratio = [ordered]@{
        unifiedScriptToPuertsElapsed = $unifiedElapsed / $puertsElapsed
        unifiedScriptToPuertsFrameMedian =
            $unifiedCsv.FrameTimeMedianMilliseconds /
            $puertsCsv.FrameTimeMedianMilliseconds
        unifiedScriptToPuertsBridge =
            [double]$unifiedRuntimeMatch.Groups[2].Value /
            [double]$puertsMatch.Groups[6].Value
        unifiedScriptToPuertsPackageSize =
            [double]$unifiedPackageSizeBytes / [double]$puertsPackageSizeBytes
        allocatedBytes = $null
        lowerIsBetter = $true
    }
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultPath -Encoding utf8

if ($WriteSuiteCaptures) {
    function Get-PluginVersion([string]$DescriptorPath) {
        $descriptor = Get-Content -LiteralPath $DescriptorPath -Raw |
            ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace([string]$descriptor.VersionName)) {
            throw "Runtime plugin has no VersionName: $DescriptorPath"
        }
        return [string]$descriptor.VersionName
    }

    $suiteDirectory = Join-Path $repoRoot 'artifacts\comparison\suite\v1.1.0'
    foreach ($runtime in @(
            [pscustomobject]@{
                Id = 'puerts'
                Result = $result.puerts
                Root = $puertsRoot
                Descriptor = Join-Path $puertsRoot 'Plugins\Puerts\Puerts.uplugin'
            },
            [pscustomobject]@{
                Id = 'unifiedScript'
                Result = $result.unifiedScript
                Root = $unifiedRoot
                Descriptor = Join-Path $unifiedRoot `
                    'Plugins\UEUnifiedScript\UEUnifiedScript.uplugin'
            })) {
        $metricsPath = Join-Path $artifactRoot `
            "$($runtime.Id)-blockbreaker-suite-metrics.json"
        $rawPath = Join-Path $suiteDirectory `
            "$($runtime.Id)\raw\blockbreaker-complete-trial-$SuiteTrial.json"
        $capturePath = Join-Path $suiteDirectory (
            "$($runtime.Id)\captures\blockbreaker-complete-trial-$SuiteTrial.json")
        $metrics = [ordered]@{
            values = [ordered]@{
                frameTimeMedianMilliseconds =
                    [double]$runtime.Result.frameTimeMedianMilliseconds
                frameTimeP95Milliseconds =
                    [double]$runtime.Result.frameTimeP95Milliseconds
                frameTimeP99Milliseconds =
                    [double]$runtime.Result.frameTimeP99Milliseconds
                bridgeTimeMilliseconds =
                    [double]$runtime.Result.bridgeTimeMilliseconds
                calls = [uint64]$runtime.Result.calls
                bytesCopied = [uint64]$runtime.Result.bytesCopied
                allocatedBytes = [uint64]$runtime.Result.allocatedBytes
                gcCollectionCount = [double]$runtime.Result.gcCollectionCount
                gcPauseMilliseconds =
                    [double]$runtime.Result.gcPauseMilliseconds
                memoryHighWaterBytes = [uint64]$runtime.Result.memoryHighWaterBytes
                startupMilliseconds = [double]$runtime.Result.startupMilliseconds
                reloadMilliseconds = [double]$runtime.Result.reloadMilliseconds
                packageSizeBytes = [uint64]$runtime.Result.packageSizeBytes
            }
        }
        $metrics | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath $metricsPath -Encoding utf8
        & $captureWriter `
            -EngineId $runtime.Id `
            -RuntimeVersion (Get-PluginVersion $runtime.Descriptor) `
            -RuntimeCommit $runtime.Result.runtimeSourceIdentity `
            -WorkloadId 'blockbreaker-complete' `
            -Trial $SuiteTrial `
            -MetricsPath $metricsPath `
            -RawOutputPath $rawPath `
            -CaptureOutputPath $capturePath `
            -EngineRoot $EngineRoot `
            -ManifestPath $suiteManifestPath
        if ($LASTEXITCODE -ne 0) {
            throw "$($runtime.Id) BlockBreaker suite capture failed."
        }
    }
}

Write-Output "Puerts:       $puertsElapsed ms ($($result.puerts.framesPerSecond) FPS)"
Write-Output "UnifiedScript: $unifiedElapsed ms ($($result.unifiedScript.framesPerSecond) FPS)"
Write-Output "Elapsed ratio (UnifiedScript/Puerts): $($result.ratio.unifiedScriptToPuertsElapsed)"
Write-Output "Result: $resultPath"
