[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',
    [ValidateSet('Development', 'Shipping')]
    [string]$Configuration = 'Development',
    [ValidateRange(60, 10000)]
    [int]$SmokeFrames = 300,
    [ValidateRange(30, 300)]
    [int]$SmokeTimeoutSeconds = 120,
    [switch]$SkipPrepare
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Join-Path $ComparisonRoot 'BlockBreaker-UnifiedScript'
$projectPath = Join-Path $projectRoot 'BlockBreaker.uproject'
$prepareScript = Join-Path $projectRoot 'Tools\run_unifiedscript_demo.ps1'
$runUat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\RunUAT.bat'
$gameAssembly = Join-Path $projectRoot 'Managed\BlockBreaker.UnifiedScript.dll'
$gameDeps = Join-Path $projectRoot 'Managed\BlockBreaker.UnifiedScript.deps.json'
$gameChunk = Join-Path $projectRoot 'Managed\BlockBreaker.UnifiedScript.uschunk'
$migrationAudit = Join-Path $PSScriptRoot 'audit-blockbreaker-migration.ps1'
$map = '/Game/BlockBreaker/Maps/TutorialMap'
$gameMode = '/Script/BlockBreaker.UnifiedScriptBlockBreakerGameMode'

foreach ($required in @($projectPath, $prepareScript, $runUat, $migrationAudit)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required BlockBreaker demo dependency is missing: $required"
    }
}

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDirectory = Join-Path $repoRoot "artifacts\demo\blockbreaker-$runId"
$archiveDirectory = Join-Path $runDirectory 'archive'
$prepareLog = Join-Path $runDirectory 'prepare.log'
$smokeLog = Join-Path $runDirectory 'packaged-smoke.log'
$smokeStdout = Join-Path $runDirectory 'packaged-smoke.stdout.log'
$smokeStderr = Join-Path $runDirectory 'packaged-smoke.stderr.log'
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$migrationReport = Join-Path $runDirectory 'migration-audit.json'
& $migrationAudit -EngineRoot $EngineRoot -ComparisonRoot $ComparisonRoot `
    -ReportPath $migrationReport
if ($LASTEXITCODE -ne 0) {
    throw 'BlockBreaker whole-project migration audit failed before packaging.'
}

if (-not $SkipPrepare) {
    Write-Output '[prepare] Synchronizing plugin, bindings, Script Assets, and managed gameplay...'
    $prepareOutput = @(& $prepareScript `
        -EngineRoot $EngineRoot `
        -UnifiedScriptRoot $repoRoot `
        -GameplayBenchmarkFrames $SmokeFrames `
        -Headless 2>&1)
    $prepareExitCode = $LASTEXITCODE
    $prepareOutput | Set-Content -LiteralPath $prepareLog -Encoding utf8
    $prepareText = $prepareOutput -join [Environment]::NewLine
    if ($prepareExitCode -ne 0 -or
        -not $prepareText.Contains('UnifiedScript gameplay data + ABI v3 gate: PASS')) {
        $prepareOutput | Select-Object -Last 200 | Write-Output
        throw 'BlockBreaker demo preparation or editor smoke failed.'
    }
}

foreach ($required in @($gameAssembly, $gameDeps, $gameChunk)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Prepared managed gameplay output is missing: $required"
    }
}

$runtimeCookDirectories = @(
    (Join-Path $projectRoot 'Content\UnifiedScript'),
    (Join-Path $projectRoot 'Content\BlockBreaker\Meshes'),
    (Join-Path $projectRoot 'Content\BlockBreaker\ParticleSystems')
) -join '+'

Write-Output "[package] Build/Cook/Stage/Pak/Archive $Configuration demo..."
& $runUat BuildCookRun `
    "-project=$projectPath" `
    -noP4 -platform=Win64 "-clientconfig=$Configuration" `
    -build -cook -stage -pak -archive `
    "-archivedirectory=$archiveDirectory" `
    "-map=$map" `
    "-CookDir=$runtimeCookDirectories" `
    '-AdditionalCookerOptions=-SkipZenStore' `
    -unattended -utf8output -NoCodeSign -NoXGE
if ($LASTEXITCODE -ne 0) {
    throw "BlockBreaker $Configuration package failed with exit code $LASTEXITCODE."
}

$packagedGameAssembly = Get-ChildItem -LiteralPath $archiveDirectory -Recurse -File `
    -Filter 'BlockBreaker.UnifiedScript.dll' | Select-Object -First 1
if ($null -eq $packagedGameAssembly -or
    $packagedGameAssembly.Directory.Name -ne 'Managed') {
    throw 'The packaged demo does not contain Managed/BlockBreaker.UnifiedScript.dll.'
}
$packagedGameChunk = Get-ChildItem -LiteralPath $archiveDirectory -Recurse -File `
    -Filter 'BlockBreaker.UnifiedScript.uschunk' | Select-Object -First 1
if ($null -eq $packagedGameChunk -or
    $packagedGameChunk.Directory.FullName -ne $packagedGameAssembly.Directory.FullName) {
    throw 'The packaged demo does not contain its compiled BlockBreaker program chunk.'
}
$packageRoot = $packagedGameAssembly.Directory.Parent.FullName
$deliveryRoot = Split-Path -Parent $packageRoot
$bootstrapExecutable = Get-Item -LiteralPath (Join-Path $deliveryRoot 'BlockBreaker.exe') `
    -ErrorAction SilentlyContinue
if ($null -eq $bootstrapExecutable) {
    throw "The packaged demo bootstrap executable is missing under $deliveryRoot."
}
$binaryDirectory = Join-Path $packageRoot 'Binaries\Win64'
$executable = Get-ChildItem -LiteralPath $binaryDirectory -File `
    -Filter 'BlockBreaker*.exe' | Where-Object {
        $_.Name -notmatch 'CrashReportClient|UnrealTraceServer'
    } | Select-Object -First 1
if ($null -eq $executable) {
    throw "The packaged demo executable is missing under $binaryDirectory."
}

$pluginManaged = Join-Path $packageRoot `
    'Plugins\UEUnifiedScript\Binaries\Managed\UnifiedScript.Unreal.dll'
$pluginCore = Join-Path $packageRoot `
    'Plugins\UEUnifiedScript\Binaries\Managed\UnifiedScript.Core.dll'
$runtimeConfig = Join-Path $packageRoot `
    'Plugins\UEUnifiedScript\Resources\Managed\UnifiedScript.Unreal.runtimeconfig.json'
foreach ($required in @($pluginManaged, $pluginCore, $runtimeConfig)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "The packaged UnifiedScript runtime is incomplete: $required"
    }
}
$hostFxr = Get-ChildItem -LiteralPath (Join-Path $packageRoot `
    'Plugins\UEUnifiedScript\Binaries\ThirdParty\DotNet\10.0\win-x64\host\fxr') `
    -Recurse -File -Filter 'hostfxr.dll' | Select-Object -First 1
$coreClr = Get-ChildItem -LiteralPath (Join-Path $packageRoot `
    'Plugins\UEUnifiedScript\Binaries\ThirdParty\DotNet\10.0\win-x64\shared\Microsoft.NETCore.App') `
    -Recurse -File -Filter 'coreclr.dll' | Select-Object -First 1
if ($null -eq $hostFxr -or $null -eq $coreClr) {
    throw 'The packaged demo does not contain its self-contained CoreCLR runtime.'
}

$launcherPath = Join-Path $deliveryRoot 'Launch-BlockBreaker-UnifiedScript.cmd'
$launcherText = @"
@echo off
cd /d "%~dp0"
start "UnifiedScript BlockBreaker" "%~dp0BlockBreaker.exe" "$map`?game=$gameMode"
"@
[IO.File]::WriteAllText($launcherPath, $launcherText, [Text.UTF8Encoding]::new($false))

$readmePath = Join-Path $deliveryRoot 'README-UnifiedScript-Demo.txt'
$readmeText = @"
UnifiedScript BlockBreaker Demo
===============================

Launch: double-click Launch-BlockBreaker-UnifiedScript.cmd

Controls:
  W/A/S/D     Move
  Mouse       Look
  Left Mouse  Shoot

This package demonstrates:
  - World-owned CoreCLR hosting and managed Actor lifecycle
  - generated type-safe Unreal C# bindings
  - Script Asset deployment without a developer checkout
  - one verified compiled chunk for both managed gameplay entry points
  - zero-copy World data discovery
  - Batch Trace plus Command/Event Buffer damage and impact effects
  - zero legacy facade calls in the automated gameplay gate

Source commit: $(& git -C $repoRoot rev-parse HEAD)
Configuration: $Configuration
"@
[IO.File]::WriteAllText($readmePath, $readmeText, [Text.UTF8Encoding]::new($false))

Write-Output "[smoke] Running packaged $SmokeFrames-frame gameplay gate..."
$smokeArguments = @(
    "$map`?game=$gameMode",
    '-game',
    '-nullrhi',
    '-unattended',
    '-nosound',
    '-nosplash',
    '-benchmark',
    '-fps=60',
    '-stdout',
    '-FullStdOutLogOutput',
    "-BlockBreakerBenchmarkFrames=$SmokeFrames",
    "-abslog=$smokeLog"
)
$process = Start-Process `
    -FilePath $executable.FullName `
    -ArgumentList $smokeArguments `
    -WorkingDirectory $packageRoot `
    -WindowStyle Hidden `
    -RedirectStandardOutput $smokeStdout `
    -RedirectStandardError $smokeStderr `
    -PassThru
if (-not $process.WaitForExit($SmokeTimeoutSeconds * 1000)) {
    Stop-Process -Id $process.Id
    $process.WaitForExit()
    throw "Packaged BlockBreaker smoke timed out after $SmokeTimeoutSeconds seconds."
}
$process.Refresh()
$smokeExitCode = [int]$process.ExitCode
if (-not (Test-Path -LiteralPath $smokeLog)) {
    throw "Packaged BlockBreaker did not write its smoke log: $smokeLog"
}
$smokeText = @(
    Get-Content -LiteralPath $smokeLog -Raw
    if (Test-Path -LiteralPath $smokeStdout) {
        Get-Content -LiteralPath $smokeStdout -Raw
    }
    if (Test-Path -LiteralPath $smokeStderr) {
        Get-Content -LiteralPath $smokeStderr -Raw
    }
) -join [Environment]::NewLine
$summary = [regex]::Match(
    $smokeText,
    'ManagedTicks=(\d+), Shots=(\d+), Hits=(\d+), Status=([^,\r\n]+), FastCalls=(\d+), CallFrameCalls=(\d+), LegacyCalls=(\d+)')
$gateFailures = @()
if ($smokeExitCode -ne 0) { $gateFailures += "exitCode=$smokeExitCode" }
if (-not $summary.Success) {
    $gateFailures += 'summary=missing'
} else {
    if ([int]$summary.Groups[1].Value -ne $SmokeFrames) { $gateFailures += 'frames' }
    if ([int]$summary.Groups[2].Value -ne 1) { $gateFailures += 'shots' }
    if ([int]$summary.Groups[3].Value -ne 1) { $gateFailures += 'hits' }
    if ($summary.Groups[4].Value.Trim() -ne 'Stopped') { $gateFailures += 'status' }
    if ([uint64]$summary.Groups[5].Value -eq 0) { $gateFailures += 'fastCalls' }
    if ([uint64]$summary.Groups[6].Value -eq 0) { $gateFailures += 'callFrameCalls' }
    if ([uint64]$summary.Groups[7].Value -ne 0) { $gateFailures += 'legacyCalls' }
}
if (-not $smokeText.Contains(
        'UnifiedScript BlockBreaker gameplay data completed: Commands=2')) {
    $gateFailures += 'gameplayCommands'
}
$playerProgram = [regex]::Match(
    $smokeText,
    "UnifiedScript started 'BlockBreaker\.UnifiedScript\.BlockBreakerPlayerScript' from '([^']+BlockBreaker\.UnifiedScript\.uschunk)'")
$gunProgram = [regex]::Match(
    $smokeText,
    "UnifiedScript started 'BlockBreaker\.UnifiedScript\.BlockBreakerRifleScript' from '([^']+BlockBreaker\.UnifiedScript\.uschunk)'")
if (-not $playerProgram.Success -or -not $gunProgram.Success) {
    $gateFailures += 'compiledChunkPrograms'
}
if ($gateFailures.Count -ne 0) {
    throw (
        "Packaged BlockBreaker gameplay gate failed ($($gateFailures -join ', ')). " +
        "Log: $smokeLog")
}

$packageFiles = @(Get-ChildItem -LiteralPath $deliveryRoot -Recurse -File)
$legacyPackageFiles = @($packageFiles | Where-Object {
    $relative = $_.FullName.Substring($deliveryRoot.Length + 1)
    $relative -match '(^|\\)(Puerts|JavaScript|TypeScript)(\\|$)'
})
if ($legacyPackageFiles.Count -ne 0) {
    throw (
        'Packaged BlockBreaker still contains Puerts/TypeScript payloads: ' +
        (($legacyPackageFiles | ForEach-Object FullName) -join ', '))
}
$comparisonStatus = @(& git -C $projectRoot status --porcelain)
$reportPath = Join-Path $runDirectory 'demo-delivery.json'
[ordered]@{
    schemaVersion = 'unifiedscript.blockbreaker-demo-delivery/v0.2'
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    engineRoot = $EngineRoot
    engineVersion = '5.8.1'
    configuration = $Configuration
    sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    comparisonCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
    comparisonWorkingTreeDirty = $comparisonStatus.Count -ne 0
    deliveryRoot = $deliveryRoot
    packageRoot = $packageRoot
    bootstrapExecutable = $bootstrapExecutable.FullName
    executable = $executable.FullName
    executableSha256 = (Get-FileHash -LiteralPath $executable.FullName -Algorithm SHA256).Hash
    packageFileCount = $packageFiles.Count
    packageBytes = [long](($packageFiles | Measure-Object Length -Sum).Sum)
    puertsOrTypeScriptPackageFiles = $legacyPackageFiles.Count
    migrationAudit = $migrationReport
    migrationAuditSha256 = (Get-FileHash -LiteralPath $migrationReport `
        -Algorithm SHA256).Hash
    launcher = $launcherPath
    readme = $readmePath
    managedGameplayAssembly = $packagedGameAssembly.FullName
    managedGameplayChunk = $packagedGameChunk.FullName
    managedGameplayChunkSha256 = (Get-FileHash -LiteralPath $packagedGameChunk.FullName `
        -Algorithm SHA256).Hash
    managedRuntimeAssembly = $pluginManaged
    hostFxr = $hostFxr.FullName
    coreClr = $coreClr.FullName
    smoke = [ordered]@{
        frames = [int]$summary.Groups[1].Value
        shots = [int]$summary.Groups[2].Value
        hits = [int]$summary.Groups[3].Value
        fastCalls = [uint64]$summary.Groups[5].Value
        callFrameCalls = [uint64]$summary.Groups[6].Value
        legacyCalls = [uint64]$summary.Groups[7].Value
        gameplayCommands = 2
        playerProgram = $playerProgram.Groups[1].Value
        gunProgram = $gunProgram.Groups[1].Value
        exitCode = $smokeExitCode
        log = $smokeLog
        stdout = $smokeStdout
        stderr = $smokeStderr
    }
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Output "BlockBreaker deliverable demo: PASS"
Write-Output "Delivery: $deliveryRoot"
Write-Output "Launcher: $launcherPath"
Write-Output "Report: $reportPath"
