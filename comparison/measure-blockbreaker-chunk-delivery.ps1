[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',
    [ValidateRange(30, 10000)]
    [int]$Frames = 100,
    [ValidateRange(0, 10000)]
    [int]$WarmupFrames = 30,
    [ValidateRange(1, 9)]
    [int]$Trials = 3,
    [ValidateRange(30, 300)]
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Join-Path $ComparisonRoot 'BlockBreaker-UnifiedScript'
$projectPath = Join-Path $projectRoot 'BlockBreaker.uproject'
$editorCmd = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
$managedRoot = Join-Path $projectRoot 'Managed'
$chunkPath = Join-Path $managedRoot 'BlockBreaker.UnifiedScript.uschunk'
$assemblyPath = Join-Path $managedRoot 'BlockBreaker.UnifiedScript.dll'
$depsPath = Join-Path $managedRoot 'BlockBreaker.UnifiedScript.deps.json'
$map = '/Game/BlockBreaker/Maps/TutorialMap?game=/Script/BlockBreaker.UnifiedScriptBlockBreakerGameMode'
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifactRoot = Join-Path $repoRoot "artifacts\comparison\chunk-delivery\$runId"
$reportPath = Join-Path $artifactRoot 'chunk-delivery.json'

foreach ($required in @($editorCmd, $projectPath, $chunkPath, $assemblyPath, $depsPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required chunk-delivery input does not exist: $required"
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

function Get-ArtifactSet([string[]]$Paths) {
    $files = @($Paths | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object {
        Get-Item -LiteralPath $_
    })
    return [ordered]@{
        fileCount = $files.Count
        bytes = [long](($files | Measure-Object Length -Sum).Sum)
        files = @($files | ForEach-Object {
            [ordered]@{
                path = $_.FullName
                bytes = [long]$_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
    }
}

function Invoke-DeliveryTrial([string]$Mode, [int]$Trial, [int]$ExecutionOrdinal) {
    $stem = '{0:D2}-{1:D2}-{2}' -f $ExecutionOrdinal, $Trial, $Mode
    $logPath = Join-Path $artifactRoot "$stem.log"
    $stdoutPath = Join-Path $artifactRoot "$stem.stdout.log"
    $stderrPath = Join-Path $artifactRoot "$stem.stderr.log"
    $arguments = @(
        $projectPath, $map, '-game', '-nullrhi', '-unattended', '-nosound',
        '-nosplash', '-stdout', '-FullStdOutLogOutput', '-benchmark', '-fps=60',
        "-BlockBreakerBenchmarkFrames=$Frames",
        '-BlockBreakerBenchmarkReload',
        "-abslog=$logPath")
    if ($WarmupFrames -gt 0) {
        $arguments += "-BlockBreakerBenchmarkWarmupFrames=$WarmupFrames"
    }
    if ($Mode -eq 'loose') {
        $arguments += '-UnifiedScriptDisableCompiledChunks'
    }

    Write-Host "[trial $Trial/$Trials, ordinal $ExecutionOrdinal] $Mode"
    $process = Start-Process -FilePath $editorCmd -ArgumentList $arguments `
        -WorkingDirectory $projectRoot -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath `
        -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id
        $process.WaitForExit()
        throw "$Mode trial $Trial timed out after $TimeoutSeconds seconds."
    }
    $process.Refresh()
    $outputText = @(
        if (Test-Path -LiteralPath $logPath) {
            Get-Content -LiteralPath $logPath -Raw
        }
        if (Test-Path -LiteralPath $stdoutPath) {
            Get-Content -LiteralPath $stdoutPath -Raw
        }
        if (Test-Path -LiteralPath $stderrPath) {
            Get-Content -LiteralPath $stderrPath -Raw
        }
    ) -join [Environment]::NewLine

    $playerStarts = [regex]::Matches(
        $outputText,
        "UnifiedScript started 'BlockBreaker\.UnifiedScript\.BlockBreakerPlayerScript' from '([^']+)' \(asset='[^']+', startupMs=([0-9.]+)\)")
    $gunStarts = [regex]::Matches(
        $outputText,
        "UnifiedScript started 'BlockBreaker\.UnifiedScript\.BlockBreakerRifleScript' from '([^']+)' \(asset='[^']+', startupMs=([0-9.]+)\)")
    $reload = [regex]::Match(
        $outputText,
        'UnifiedScript BlockBreaker reload: Success=1, ElapsedMs=([0-9.]+), Program=([^\r\n]+)')
    $reloadTransactionMatch = [regex]::Match(
        $outputText,
        'UnifiedScript BlockBreaker reload transaction: (\{[^\r\n]+\})')
    $summary = [regex]::Match(
        $outputText,
        'ManagedTicks=(\d+), Shots=(\d+), Hits=(\d+), Status=([^,\r\n]+), FastCalls=(\d+), CallFrameCalls=(\d+), LegacyCalls=(\d+).+PlayerProgram=([^,\r\n]+), GunProgram=([^,\r\n]+)')
    if ([int]$process.ExitCode -ne 0 -or $playerStarts.Count -lt 1 -or
        $gunStarts.Count -lt 1 -or -not $reload.Success -or
        -not $reloadTransactionMatch.Success -or -not $summary.Success) {
        throw "$Mode trial $Trial did not publish a complete startup/reload result. Log: $logPath"
    }
    $reloadTransaction = $reloadTransactionMatch.Groups[1].Value | ConvertFrom-Json
    if ($reloadTransaction.state -ne 'committed') {
        throw "$Mode trial $Trial did not commit its reload transaction. Log: $logPath"
    }
    $generationLoad = @($reloadTransaction.phases |
        Where-Object name -eq 'generationLoad' | Select-Object -First 1)
    $candidateStart = @($reloadTransaction.phases |
        Where-Object name -eq 'candidateStart' | Select-Object -First 1)
    $stateMigration = @($reloadTransaction.phases |
        Where-Object name -eq 'stateMigration' | Select-Object -First 1)
    if ($generationLoad.Count -ne 1 -or $candidateStart.Count -ne 1 -or
        $stateMigration.Count -ne 1) {
        throw "$Mode trial $Trial reload transaction is missing measured phases. Log: $logPath"
    }

    $expectedExtension = if ($Mode -eq 'chunk') { '.uschunk' } else { '.dll' }
    $programPaths = @(
        $playerStarts[0].Groups[1].Value.Trim(),
        $gunStarts[0].Groups[1].Value.Trim(),
        $reload.Groups[2].Value.Trim(),
        $summary.Groups[8].Value.Trim(),
        $summary.Groups[9].Value.Trim())
    if (@($programPaths | Where-Object {
            -not $_.EndsWith($expectedExtension, [StringComparison]::OrdinalIgnoreCase)
        }).Count -ne 0) {
        throw "$Mode trial $Trial executed an unexpected program path: $($programPaths -join ', ')"
    }
    if ([int]$summary.Groups[1].Value -lt ($Frames + $WarmupFrames) -or
        [int]$summary.Groups[2].Value -ne 1 -or
        [int]$summary.Groups[3].Value -ne 1 -or
        $summary.Groups[4].Value.Trim() -ne 'Stopped' -or
        [uint64]$summary.Groups[5].Value -eq 0 -or
        [uint64]$summary.Groups[6].Value -eq 0 -or
        [uint64]$summary.Groups[7].Value -ne 0 -or
        -not $outputText.Contains('UnifiedScript BlockBreaker gameplay data completed: Commands=2')) {
        throw "$Mode trial $Trial failed the equivalent gameplay gate. Log: $logPath"
    }

    $playerStartup = [double]$playerStarts[0].Groups[2].Value
    $gunStartup = [double]$gunStarts[0].Groups[2].Value
    return [pscustomobject][ordered]@{
        mode = $Mode
        trial = $Trial
        executionOrdinal = $ExecutionOrdinal
        playerStartupMilliseconds = $playerStartup
        gunStartupMilliseconds = $gunStartup
        totalStartupMilliseconds = $playerStartup + $gunStartup
        reloadMilliseconds = [double]$reload.Groups[1].Value
        reloadGenerationLoadMilliseconds =
            [double]$generationLoad[0].durationMicroseconds / 1000.0
        reloadCandidateStartMilliseconds =
            [double]$candidateStart[0].durationMicroseconds / 1000.0
        reloadStateMigrationMilliseconds =
            [double]$stateMigration[0].durationMicroseconds / 1000.0
        managedTicks = [int]$summary.Groups[1].Value
        fastCalls = [uint64]$summary.Groups[5].Value
        callFrameCalls = [uint64]$summary.Groups[6].Value
        legacyCalls = [uint64]$summary.Groups[7].Value
        playerProgram = $summary.Groups[8].Value.Trim()
        gunProgram = $summary.Groups[9].Value.Trim()
        log = $logPath
    }
}

$results = [System.Collections.Generic.List[object]]::new()
$ordinal = 0
for ($trial = 1; $trial -le $Trials; ++$trial) {
    $order = if (($trial % 2) -eq 1) { @('chunk', 'loose') } else { @('loose', 'chunk') }
    foreach ($mode in $order) {
        ++$ordinal
        $results.Add((Invoke-DeliveryTrial $mode $trial $ordinal))
    }
}

$chunkTrials = @($results | Where-Object mode -eq 'chunk')
$looseTrials = @($results | Where-Object mode -eq 'loose')
$chunkStartup = Get-Median @($chunkTrials | ForEach-Object totalStartupMilliseconds)
$looseStartup = Get-Median @($looseTrials | ForEach-Object totalStartupMilliseconds)
$chunkReload = Get-Median @($chunkTrials | ForEach-Object reloadMilliseconds)
$looseReload = Get-Median @($looseTrials | ForEach-Object reloadMilliseconds)
$chunkGenerationLoad = Get-Median @(
    $chunkTrials | ForEach-Object reloadGenerationLoadMilliseconds)
$looseGenerationLoad = Get-Median @(
    $looseTrials | ForEach-Object reloadGenerationLoadMilliseconds)
$chunkArtifacts = Get-ArtifactSet @($chunkPath)
$looseRuntimeArtifacts = Get-ArtifactSet @($assemblyPath, $depsPath)
$looseMigrationArtifacts = Get-ArtifactSet @(
    $assemblyPath,
    $depsPath,
    (Join-Path $managedRoot 'BlockBreaker.UnifiedScript.pdb'),
    (Join-Path $managedRoot 'BlockBreaker.UnifiedScript.asset.json'),
    (Join-Path $managedRoot 'BlockBreaker.UnifiedScript.sourcemap.json'))

$report = [ordered]@{
    schemaVersion = 'unifiedscript.blockbreaker-chunk-delivery/v0.1'
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    engineVersion = '5.8.1'
    engineRoot = $EngineRoot
    sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    comparisonCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
    frames = $Frames
    warmupFrames = $WarmupFrames
    trialsPerMode = $Trials
    schedule = @($results | ForEach-Object {
        [ordered]@{
            executionOrdinal = $_.executionOrdinal
            trial = $_.trial
            mode = $_.mode
        }
    })
    metrics = [ordered]@{
        chunk = [ordered]@{
            startupMedianMilliseconds = $chunkStartup
            reloadMedianMilliseconds = $chunkReload
            reloadGenerationLoadMedianMilliseconds = $chunkGenerationLoad
        }
        loose = [ordered]@{
            startupMedianMilliseconds = $looseStartup
            reloadMedianMilliseconds = $looseReload
            reloadGenerationLoadMedianMilliseconds = $looseGenerationLoad
        }
        ratios = [ordered]@{
            chunkToLooseStartup = $chunkStartup / $looseStartup
            chunkToLooseReload = $chunkReload / $looseReload
            chunkToLooseReloadGenerationLoad =
                $chunkGenerationLoad / $looseGenerationLoad
        }
    }
    deliveryArtifacts = [ordered]@{
        chunkProgram = $chunkArtifacts
        looseRuntimeProgram = $looseRuntimeArtifacts
        looseMigrationBundle = $looseMigrationArtifacts
        chunkToLooseRuntimeBytes = [double]$chunkArtifacts.bytes / [double]$looseRuntimeArtifacts.bytes
    }
    trials = @($results)
    equivalentGameplayPassed = $true
}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
$latestPath = Join-Path (Split-Path -Parent $artifactRoot) 'latest.json'
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $latestPath -Encoding utf8

Write-Output (
    'BlockBreaker chunk delivery: PASS ' +
    "(startup ratio=$([Math]::Round($report.metrics.ratios.chunkToLooseStartup, 6)), " +
    "reload ratio=$([Math]::Round($report.metrics.ratios.chunkToLooseReload, 6)), " +
    "chunk files=$($chunkArtifacts.fileCount), loose runtime files=$($looseRuntimeArtifacts.fileCount))")
Write-Output $reportPath
