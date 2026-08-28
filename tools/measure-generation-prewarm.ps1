[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$CliAssembly,
    [string]$ProbeSource,
    [string]$OutputDirectory,
    [ValidateRange(5, 50)]
    [int]$Trials = 15,
    [ValidateRange(0, 100)]
    [int]$FrameGapMilliseconds = 16
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($CliAssembly)) {
    $packagedCli = Join-Path $repoRoot 'Tools\UnifiedScript.Cli\UnifiedScript.Cli.dll'
    $CliAssembly = if (Test-Path -LiteralPath $packagedCli -PathType Leaf) {
        $packagedCli
    } else {
        Join-Path $repoRoot 'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll'
    }
}
if ([string]::IsNullOrWhiteSpace($ProbeSource)) {
    $ProbeSource = Join-Path $repoRoot 'benchmarks\unreal\generation-prewarm-probe.cs'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\chunk-profile\m11.4-background-prewarm'
}

$dotnet = Join-Path $EngineRoot 'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
$resolvedCli = [IO.Path]::GetFullPath($CliAssembly)
$resolvedProbe = [IO.Path]::GetFullPath($ProbeSource)
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($required in @($dotnet, $resolvedCli, $resolvedProbe)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required generation-prewarm input does not exist: $required"
    }
}

$cliDirectory = Split-Path -Parent $resolvedCli
$references = @(
    (Join-Path $cliDirectory 'UnifiedScript.Core.dll'),
    (Join-Path $cliDirectory 'UnifiedScript.Runtime.dll'),
    (Join-Path $cliDirectory 'UnifiedScript.Unreal.dll')
)
foreach ($reference in $references) {
    if (!(Test-Path -LiteralPath $reference -PathType Leaf)) {
        throw "Packaged CLI dependency does not exist: $reference"
    }
}

$probeDirectory = Join-Path $resolvedOutput 'probe'
$trialDirectory = Join-Path $resolvedOutput 'trials'
[IO.Directory]::CreateDirectory($probeDirectory) | Out-Null
[IO.Directory]::CreateDirectory($trialDirectory) | Out-Null

$emitArguments = @(
    $resolvedCli,
    'emit',
    $resolvedProbe,
    '--out',
    $probeDirectory,
    '--assembly',
    'UnifiedScript.GenerationPrewarmProbe'
)
foreach ($reference in $references) {
    $emitArguments += @('--reference', $reference)
}
& $dotnet @emitArguments | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Generation-prewarm probe emission failed with exit code $LASTEXITCODE."
}

$chunkPath = Join-Path $probeDirectory 'generation-prewarm-probe.uschunk'
if (!(Test-Path -LiteralPath $chunkPath -PathType Leaf)) {
    throw "Generation-prewarm probe chunk was not emitted: $chunkPath"
}

function Get-Median([double[]]$Values) {
    if ($Values.Count -eq 0) {
        throw 'A generation-prewarm median requires at least one value.'
    }
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 1) {
        return [double]$ordered[$middle]
    }
    return ([double]$ordered[$middle - 1] + [double]$ordered[$middle]) / 2.0
}

$measurements = [System.Collections.Generic.List[object]]::new()
for ($trial = 1; $trial -le $Trials; ++$trial) {
    $variants = if (($trial % 2) -eq 1) {
        @('disabled', 'background')
    } else {
        @('background', 'disabled')
    }
    foreach ($variant in $variants) {
        $trialPath = Join-Path $trialDirectory ('trial-{0:D2}-{1}.json' -f $trial, $variant)
        $profileArguments = @(
            $resolvedCli,
            'chunk-first-tick-profile',
            $chunkPath,
            '--type',
            'UnifiedScript.Benchmarks.GenerationPrewarmProbe',
            '--out',
            $trialPath,
            '--frame-gap-ms',
            $FrameGapMilliseconds
        )
        foreach ($reference in $references) {
            $profileArguments += @('--reference', $reference)
        }
        if ($variant -eq 'disabled') {
            $profileArguments += '--disable-prewarm'
        }
        & $dotnet @profileArguments | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Generation-prewarm $variant trial $trial failed with exit code $LASTEXITCODE."
        }

        $profile = Get-Content -LiteralPath $trialPath -Raw | ConvertFrom-Json
        if ($profile.schemaVersion -ne 'unifiedscript.generation-prewarm-trial/v0.1' -or
            $profile.variant -ne $variant -or
            [int]$profile.frameGapMilliseconds -ne $FrameGapMilliseconds) {
            throw "Generation-prewarm $variant trial $trial produced an invalid profile."
        }
        $measurements.Add([ordered]@{
            trial = $trial
            variant = $variant
            firstTickNanoseconds = [long]$profile.firstTickNanoseconds
            firstTickAllocatedBytes = [long]$profile.firstTickAllocatedBytes
            scheduleNanoseconds = [long]$profile.scheduleNanoseconds
            teardownNanoseconds = [long]$profile.teardownNanoseconds
            prewarmStateAtFirstTick = [string]$profile.prewarmAtFirstTick.state
            preparedBeforeFirstTickCount = [int]$profile.prewarmAtFirstTick.preparedBeforeFirstTickCount
            completedBeforeFirstTick = [bool]$profile.prewarmAtFirstTick.completedBeforeFirstTick
        })
    }
}

$disabled = @($measurements | Where-Object variant -eq 'disabled')
$background = @($measurements | Where-Object variant -eq 'background')
$disabledFirstTick = Get-Median @($disabled | ForEach-Object { [double]$_.firstTickNanoseconds })
$backgroundFirstTick = Get-Median @($background | ForEach-Object { [double]$_.firstTickNanoseconds })
$backgroundSchedule = Get-Median @($background | ForEach-Object { [double]$_.scheduleNanoseconds })
$backgroundTeardown = Get-Median @($background | ForEach-Object { [double]$_.teardownNanoseconds })
$firstTickRatio = $backgroundFirstTick / $disabledFirstTick
$completedCount = @($background | Where-Object completedBeforeFirstTick).Count
$completionRate = $completedCount / [double]$Trials

$maximumFirstTickRatio = 0.80
$maximumScheduleNanoseconds = 2000000
$maximumTeardownNanoseconds = 2000000
$requiredCompletionRate = 1.0
$passed = $firstTickRatio -le $maximumFirstTickRatio -and
    $backgroundSchedule -le $maximumScheduleNanoseconds -and
    $backgroundTeardown -le $maximumTeardownNanoseconds -and
    $completionRate -ge $requiredCompletionRate

$firstProfile = Get-Content -LiteralPath (
    Join-Path $trialDirectory 'trial-01-disabled.json') -Raw | ConvertFrom-Json
$report = [ordered]@{
    schemaVersion = 'unifiedscript.generation-prewarm-experiment/v0.1'
    runtimeVersion = [string]$firstProfile.runtimeVersion
    chunkFormatVersion = [string]$firstProfile.chunkFormatVersion
    contentDigest = [string]$firstProfile.contentDigest
    trialCountPerVariant = $Trials
    frameGapMilliseconds = $FrameGapMilliseconds
    measurements = @($measurements)
    medians = [ordered]@{
        disabledFirstTickNanoseconds = [long][Math]::Round($disabledFirstTick)
        backgroundFirstTickNanoseconds = [long][Math]::Round($backgroundFirstTick)
        backgroundScheduleNanoseconds = [long][Math]::Round($backgroundSchedule)
        backgroundTeardownNanoseconds = [long][Math]::Round($backgroundTeardown)
        backgroundToDisabledFirstTickRatio = $firstTickRatio
        completedBeforeFirstTickCount = $completedCount
        completedBeforeFirstTickRate = $completionRate
    }
    decision = [ordered]@{
        passed = $passed
        maximumFirstTickRatio = $maximumFirstTickRatio
        maximumScheduleNanoseconds = $maximumScheduleNanoseconds
        maximumTeardownNanoseconds = $maximumTeardownNanoseconds
        requiredCompletionRate = $requiredCompletionRate
    }
}

$reportPath = Join-Path $resolvedOutput 'generation-prewarm-experiment.json'
$reportJson = $report | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText($reportPath, $reportJson + [Environment]::NewLine)
Write-Output $reportPath
$summary = (
    'firstTickRatio={0:F6}; disabledMedianNs={1}; backgroundMedianNs={2}; ' +
    'scheduleMedianNs={3}; teardownMedianNs={4}; completionRate={5:F3}; passed={6}') -f
    $firstTickRatio,
    [long][Math]::Round($disabledFirstTick),
    [long][Math]::Round($backgroundFirstTick),
    [long][Math]::Round($backgroundSchedule),
    [long][Math]::Round($backgroundTeardown),
    $completionRate,
    $passed
Write-Output $summary
if (!$passed) {
    throw 'Generation-owned background prewarm failed its first-Tick or critical-path budgets.'
}
