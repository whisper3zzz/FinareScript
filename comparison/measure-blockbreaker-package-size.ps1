[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',
    [ValidateSet('Development', 'Shipping')]
    [string]$Configuration = 'Development',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runUat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\RunUAT.bat'
$artifactRoot = Join-Path $repoRoot 'artifacts\comparison\package-size'
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runRoot = Join-Path $artifactRoot $runId
$map = '/Game/BlockBreaker/Maps/TutorialMap'
$runtimeDefinitions = @(
    [pscustomobject]@{
        Id = 'puerts'
        Root = Join-Path $ComparisonRoot 'BlockBreaker-Puerts'
        ExtraCookDirectory = 'Content\Blueprints\TypeScript'
    },
    [pscustomobject]@{
        Id = 'unifiedScript'
        Root = Join-Path $ComparisonRoot 'BlockBreaker-UnifiedScript'
        ExtraCookDirectory = 'Content\UnifiedScript'
    }
)

if (-not (Test-Path -LiteralPath $runUat)) {
    throw "RunUAT does not exist: $runUat"
}
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

$measurements = [ordered]@{}
foreach ($runtime in $runtimeDefinitions) {
    $projectPath = Join-Path $runtime.Root 'BlockBreaker.uproject'
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "BlockBreaker project does not exist: $projectPath"
    }
    $trackedStatus = @(& git -C $runtime.Root status --porcelain --untracked-files=no)
    if ($trackedStatus.Count -ne 0) {
        throw "$($runtime.Id) has tracked working-tree changes; commit them before packaging."
    }

    $archiveDirectory = Join-Path $runRoot $runtime.Id
    $cookDirectories = @(
        (Join-Path $runtime.Root 'Content\BlockBreaker'),
        (Join-Path $runtime.Root $runtime.ExtraCookDirectory)
    ) -join '+'
    $uatArguments = @(
        'BuildCookRun',
        "-project=$projectPath",
        '-noP4', '-platform=Win64', "-clientconfig=$Configuration",
        '-cook', '-stage', '-pak', '-archive',
        "-archivedirectory=$archiveDirectory",
        "-map=$map",
        "-CookDir=$cookDirectories",
        '-AdditionalCookerOptions=-SkipZenStore',
        '-unattended', '-utf8output', '-NoCodeSign', '-NoXGE',
        '-nocompileeditor'
    )
    if (-not $SkipBuild) {
        $uatArguments += '-build'
    }
    Write-Output "[package-size] $($runtime.Id) Build/Cook/Stage/Pak/Archive..."
    & $runUat @uatArguments
    if ($LASTEXITCODE -ne 0) {
        throw "$($runtime.Id) packaging failed with exit code $LASTEXITCODE."
    }

    $files = @(Get-ChildItem -LiteralPath $archiveDirectory -Recurse -File)
    if ($files.Count -eq 0) {
        throw "$($runtime.Id) package archive is empty: $archiveDirectory"
    }
    $bootstrap = $files | Where-Object Name -eq 'BlockBreaker.exe' |
        Select-Object -First 1
    if ($null -eq $bootstrap) {
        throw "$($runtime.Id) package is missing BlockBreaker.exe."
    }
    $manifestFiles = @($files | Where-Object Name -like 'Manifest_*.txt')
    $manifestText = @($manifestFiles | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw
    }) -join [Environment]::NewLine
    if ($runtime.Id -eq 'puerts') {
        if ($manifestText -notmatch '(?i)(^|[/\\])Puerts([/\\]|\.)' -or
            $manifestText -notmatch '(?i)(^|[/\\])TypeScript([/\\]|\.)') {
            throw 'Puerts package does not contain its runtime/script payload.'
        }
    } else {
        if ($manifestText -match '(?i)(^|[/\\])(Puerts|JavaScript|TypeScript)([/\\]|\.)') {
            throw 'UnifiedScript package contains a forbidden Puerts/JavaScript/TypeScript payload.'
        }
        if (-not ($files | Where-Object Name -eq 'coreclr.dll')) {
            throw 'UnifiedScript package does not contain coreclr.dll.'
        }
    }

    $measurements[$runtime.Id] = [ordered]@{
        commit = (& git -C $runtime.Root rev-parse HEAD).Trim()
        trackedWorkingTreeDirty = $false
        archiveDirectory = $archiveDirectory
        fileCount = $files.Count
        packageSizeBytes = [uint64](($files | Measure-Object Length -Sum).Sum)
        bootstrapExecutable = $bootstrap.FullName
        bootstrapSha256 = (Get-FileHash -LiteralPath $bootstrap.FullName `
            -Algorithm SHA256).Hash
    }
}

$report = [ordered]@{
    schemaVersion = 'unifiedscript.blockbreaker-package-size/v0.1'
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    engineRoot = $EngineRoot
    engineVersion = '5.8.1'
    platform = 'Win64'
    configuration = $Configuration
    map = $map
    measurement = 'sum of every archived delivery file after matched BuildCookRun'
    puerts = $measurements.puerts
    unifiedScript = $measurements.unifiedScript
    ratio = [ordered]@{
        unifiedScriptToPuertsPackageSize =
            [double]$measurements.unifiedScript.packageSizeBytes /
            [double]$measurements.puerts.packageSizeBytes
        lowerIsBetter = $true
    }
}
$reportPath = Join-Path $runRoot 'package-size.json'
$latestPath = Join-Path $artifactRoot 'latest.json'
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $latestPath -Encoding utf8

Write-Output "Puerts package:       $($report.puerts.packageSizeBytes) bytes"
Write-Output "UnifiedScript package: $($report.unifiedScript.packageSizeBytes) bytes"
Write-Output "Package ratio (UnifiedScript/Puerts): $($report.ratio.unifiedScriptToPuertsPackageSize)"
Write-Output "Report: $reportPath"
