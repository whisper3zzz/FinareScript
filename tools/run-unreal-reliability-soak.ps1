[CmdletBinding()]
param(
    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,
    [switch]$SkipBuild,
    [ValidateRange(1, 256)]
    [int]$TravelCycles = 32,
    [ValidateRange(1, 256)]
    [int]$PieCycles = 32,
    [ValidateRange(2, 32)]
    [int]$ConcurrentWorlds = 8,
    [ValidateRange(1, 1024)]
    [int]$RequestsPerWorld = 128,
    [ValidateRange(1, 512)]
    [int]$GenerationRollovers = 64,
    [ValidateRange(1, 128)]
    [int]$ObjectChurnCycles = 16,
    [ValidateRange(1, 4096)]
    [int]$ObjectsPerChurnCycle = 256,
    [ValidateRange(1, 64)]
    [int]$CommandSaturationCycles = 8,
    [ValidateRange(4097, 65536)]
    [int]$CommandsPerSaturationCycle = 8192,
    [ValidateRange(64, 4096)]
    [int]$MemoryGrowthBudgetMB = 256
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$laneScript = Join-Path $PSScriptRoot 'run-unreal-lifecycle-stress.ps1'
$artifactRoot = Join-Path $repoRoot 'artifacts\ue'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDirectory = Join-Path $artifactRoot "reliability-soak\$stamp"
$editorName = "reliability-soak-$stamp-editor"
$serverName = "reliability-soak-$stamp-server"

if (-not (Test-Path -LiteralPath $laneScript)) {
    throw "Lifecycle stress runner is missing: $laneScript"
}
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

$common = @{
    EngineRoot = $EngineRoot
    TravelCycles = $TravelCycles
    PieCycles = $PieCycles
    ConcurrentWorlds = $ConcurrentWorlds
    RequestsPerWorld = $RequestsPerWorld
    GenerationRollovers = $GenerationRollovers
    ObjectChurnCycles = $ObjectChurnCycles
    ObjectsPerChurnCycle = $ObjectsPerChurnCycle
    CommandSaturationCycles = $CommandSaturationCycles
    CommandsPerSaturationCycle = $CommandsPerSaturationCycle
    MemoryGrowthBudgetMB = $MemoryGrowthBudgetMB
}

$editorArguments = @{} + $common
$editorArguments.ReportName = $editorName
if ($SkipBuild) {
    $editorArguments.SkipBuild = $true
}
& $laneScript @editorArguments

$serverArguments = @{} + $common
$serverArguments.ReportName = $serverName
$serverArguments.SkipBuild = $true
$serverArguments.DedicatedServer = $true
& $laneScript @serverArguments

$editorReportPath = Join-Path $artifactRoot "$editorName.json"
$serverReportPath = Join-Path $artifactRoot "$serverName.json"
$editorReport = Get-Content -LiteralPath $editorReportPath -Raw | ConvertFrom-Json
$serverReport = Get-Content -LiteralPath $serverReportPath -Raw | ConvertFrom-Json
$passed =
    $editorReport.status -eq 'passed' -and
    $serverReport.status -eq 'passed' -and
    -not [bool]$editorReport.result.dedicatedServer -and
    [bool]$serverReport.result.dedicatedServer

$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
$report = [ordered]@{
    schemaVersion = 'unifiedscript.unreal-reliability-soak/v0.1'
    status = if ($passed) { 'passed' } else { 'failed' }
    createdUtc = [DateTime]::UtcNow.ToString('O')
    sourceCommit = $sourceCommit
    engineRoot = $editorReport.engineRoot
    lanes = @(
        [ordered]@{
            id = 'editor'
            dedicatedServer = $false
            reportPath = [IO.Path]::GetFullPath($editorReportPath)
            result = $editorReport.result
            gates = $editorReport.gates
        },
        [ordered]@{
            id = 'dedicated-server'
            dedicatedServer = $true
            reportPath = [IO.Path]::GetFullPath($serverReportPath)
            result = $serverReport.result
            gates = $serverReport.gates
        })
    totals = [ordered]@{
        travelCycles = [int]$editorReport.result.travelCycles +
            [int]$serverReport.result.travelCycles
        pieCycles = [int]$editorReport.result.pieCycles +
            [int]$serverReport.result.pieCycles
        generationRollovers = [int]$editorReport.result.generationRollovers +
            [int]$serverReport.result.generationRollovers
        failedReloadCycles = [int]$editorReport.result.failedReloadCycles +
            [int]$serverReport.result.failedReloadCycles
        objectRegistrations = [int]$editorReport.result.objectRegistrations +
            [int]$serverReport.result.objectRegistrations
        garbageCollectionCycles = [int]$editorReport.result.garbageCollectionCycles +
            [int]$serverReport.result.garbageCollectionCycles
        executedCommands = (
            [uint64]$editorReport.result.commandsPerSaturationCycle *
            [uint64]$editorReport.result.commandSaturationCycles) + (
            [uint64]$serverReport.result.commandsPerSaturationCycle *
            [uint64]$serverReport.result.commandSaturationCycles)
        droppedEvents = [uint64]$editorReport.result.eventOverflowCount +
            [uint64]$serverReport.result.eventOverflowCount
        asyncCapacityRejections =
            [uint64]$editorReport.result.asyncCapacityRejections +
            [uint64]$serverReport.result.asyncCapacityRejections
        asyncPayloadRejections =
            [uint64]$editorReport.result.asyncPayloadRejections +
            [uint64]$serverReport.result.asyncPayloadRejections
    }
}
$reportPath = Join-Path $runDirectory 'reliability-soak.json'
$report | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $reportPath -Encoding utf8

if (-not $passed) {
    throw "One or more reliability soak lanes failed. Report: $reportPath"
}

Write-Host "UnifiedScript reliability soak: PASS ($reportPath)"
Write-Host (
    "Travel=$($report.totals.travelCycles), PIE=$($report.totals.pieCycles), " +
    "Reloads=$($report.totals.generationRollovers), " +
    "FailedReloads=$($report.totals.failedReloadCycles), " +
    "Objects=$($report.totals.objectRegistrations), " +
    "Commands=$($report.totals.executedCommands), " +
    "DroppedEvents=$($report.totals.droppedEvents)")
