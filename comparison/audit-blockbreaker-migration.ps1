[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $ComparisonRoot 'BlockBreakerSource'
$targetRoot = Join-Path $ComparisonRoot 'BlockBreaker-UnifiedScript'
$dotnet = Join-Path $EngineRoot `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
$cliProject = Join-Path $repoRoot 'src\UnifiedScript.Cli\UnifiedScript.Cli.csproj'
$inventoryPath = Join-Path $PSScriptRoot 'blockbreaker-puerts-inventory.json'
$coveragePath = Join-Path $PSScriptRoot 'blockbreaker-migration-coverage.json'
$coverageReportPath = Join-Path $PSScriptRoot 'blockbreaker-migration-report.json'

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $repoRoot 'artifacts\migration\blockbreaker-migration.json'
}
$ReportPath = [IO.Path]::GetFullPath($ReportPath)

foreach ($required in @(
    $sourceRoot,
    $targetRoot,
    $dotnet,
    $cliProject,
    $coveragePath,
    (Join-Path $sourceRoot 'TypeScript'),
    (Join-Path $targetRoot 'BlockBreaker.uproject'),
    (Join-Path $targetRoot 'Contracts\BlockBreaker.unreal-api-contract.json'),
    (Join-Path $targetRoot 'Deployment\BlockBreaker.unifiedscript-deployment.json')
)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required BlockBreaker migration input is missing: $required"
    }
}

& $dotnet run --project $cliProject -c Release -- `
    ue-puerts-inventory $sourceRoot `
    --native-project $targetRoot `
    --project-name BlockBreaker-UnifiedScript `
    --out $inventoryPath `
    --coverage $coveragePath `
    --report $coverageReportPath `
    --fail-on-uncovered
if ($LASTEXITCODE -ne 0) {
    throw "BlockBreaker Puerts inventory coverage failed with exit code $LASTEXITCODE."
}

$inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
$coverage = Get-Content -LiteralPath $coverageReportPath -Raw | ConvertFrom-Json
if (-not $coverage.passed -or
    $coverage.inventoryItemCount -ne $coverage.coveredItemCount -or
    $coverage.uncoveredItemCount -ne 0 -or
    $coverage.staleEntryCount -ne 0) {
    throw "BlockBreaker Puerts migration coverage is incomplete: $coverageReportPath"
}

$forbiddenTargets = @(
    'Plugins\Puerts',
    'TypeScript',
    'Typing',
    'Content\JavaScript',
    'Content\Blueprints\TypeScript',
    'Config\DefaultPuerts.ini',
    'tsconfig.json'
)
$remainingLegacyTargets = @(
    foreach ($relative in $forbiddenTargets) {
        if (Test-Path -LiteralPath (Join-Path $targetRoot $relative)) {
            $relative.Replace('\', '/')
        }
    }
)
if ($remainingLegacyTargets.Count -ne 0) {
    throw (
        'UnifiedScript BlockBreaker still contains Puerts/TypeScript targets: ' +
        ($remainingLegacyTargets -join ', '))
}

$uproject = Get-Content -LiteralPath (Join-Path $targetRoot 'BlockBreaker.uproject') `
    -Raw | ConvertFrom-Json
$enabledPlugins = @($uproject.Plugins | Where-Object Enabled | ForEach-Object Name)
if ('UEUnifiedScript' -notin $enabledPlugins -or 'Puerts' -in $enabledPlugins) {
    throw "BlockBreaker.uproject does not select only the UnifiedScript runtime path."
}
$buildRulesText = Get-Content -LiteralPath `
    (Join-Path $targetRoot 'Source\BlockBreaker\BlockBreaker.Build.cs') -Raw
if ($buildRulesText -match '(?i)puerts|javascript|typescript') {
    throw "BlockBreaker native module still references Puerts or TypeScript."
}

$sourceAssetRoot = Join-Path $sourceRoot 'Content\BlockBreaker'
$targetAssetRoot = Join-Path $targetRoot 'Content\BlockBreaker'
$excludedMap = 'Maps/TutorialMap.umap'
$sourceAssets = @(
    Get-ChildItem -LiteralPath $sourceAssetRoot -Recurse -File |
        ForEach-Object {
            $_.FullName.Substring($sourceAssetRoot.Length + 1).Replace('\', '/')
        } |
        Where-Object { $_ -ne $excludedMap } |
        Sort-Object
)
$targetAssets = @(
    Get-ChildItem -LiteralPath $targetAssetRoot -Recurse -File |
        ForEach-Object {
            $_.FullName.Substring($targetAssetRoot.Length + 1).Replace('\', '/')
        } |
        Where-Object { $_ -ne $excludedMap } |
        Sort-Object
)
$assetSetDifference = @(Compare-Object $sourceAssets $targetAssets)
if ($assetSetDifference.Count -ne 0) {
    throw "BlockBreaker non-map asset sets differ: $($assetSetDifference | Out-String)"
}
$assetMismatches = @(
    foreach ($relative in $sourceAssets) {
        $sourceHash = (Get-FileHash -LiteralPath `
            (Join-Path $sourceAssetRoot $relative) -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath `
            (Join-Path $targetAssetRoot $relative) -Algorithm SHA256).Hash
        if ($sourceHash -ne $targetHash) {
            $relative
        }
    }
)
if ($assetMismatches.Count -ne 0) {
    throw "BlockBreaker shared assets changed during migration: $($assetMismatches -join ', ')"
}

$deployment = Get-Content -LiteralPath `
    (Join-Path $targetRoot 'Deployment\BlockBreaker.unifiedscript-deployment.json') `
    -Raw | ConvertFrom-Json
if (@($deployment.scripts).Count -ne 2) {
    throw "BlockBreaker migration must deploy exactly the Player and Rifle Script Assets."
}
$scriptAssets = @(
    Get-ChildItem -LiteralPath (Join-Path $targetRoot 'Content\UnifiedScript\BlockBreaker') `
        -File -Filter '*.uasset' | Sort-Object Name
)
if ($scriptAssets.Count -ne 2) {
    throw "BlockBreaker migration must materialize exactly two UnifiedScript assets."
}

$bindingCoveragePath = Join-Path $targetRoot `
    'Generated\BlockBreaker.binding-coverage.json'
$bindingCoverage = Get-Content -LiteralPath $bindingCoveragePath -Raw | ConvertFrom-Json
if ($bindingCoverage.summary.nativeThunkCount -ne $bindingCoverage.summary.memberCount -or
    $bindingCoverage.summary.reflectionFallbackCount -ne 0 -or
    $bindingCoverage.summary.rejectedCount -ne 0 -or
    $bindingCoverage.summary.deferredTypeCount -ne 0) {
    throw "BlockBreaker generated binding coverage is incomplete: $bindingCoveragePath"
}

$reportDirectory = Split-Path -Parent $ReportPath
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
[ordered]@{
    schemaVersion = 'unifiedscript.blockbreaker-migration-audit/v0.1'
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    sourceProject = $sourceRoot
    targetProject = $targetRoot
    inventoryFingerprint = $inventory.sourceFingerprint
    inventoryItems = [int]$inventory.summary.itemCount
    coveredItems = [int]$coverage.coveredItemCount
    nativeFacadeCount = [int]$inventory.summary.nativeFacadeCount
    nativeFacades = @(
        $inventory.items | Where-Object kind -eq 'nativeFacade' | ForEach-Object key)
    forbiddenLegacyTargets = $forbiddenTargets
    remainingLegacyTargets = $remainingLegacyTargets
    enabledPlugins = $enabledPlugins
    sharedAssetCount = $sourceAssets.Count
    sharedAssetsByteIdentical = $true
    scriptAssetCount = $scriptAssets.Count
    generatedBindingMembers = [int]$bindingCoverage.summary.memberCount
    generatedNativeThunks = [int]$bindingCoverage.summary.nativeThunkCount
    reflectionFallbackCount = [int]$bindingCoverage.summary.reflectionFallbackCount
    rejectedBindingCount = [int]$bindingCoverage.summary.rejectedCount
    passed = $true
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ReportPath -Encoding utf8

Write-Output (
    'BlockBreaker full migration audit: PASS ' +
    "(inventory=$($inventory.summary.itemCount), assets=$($sourceAssets.Count), " +
    "nativeFacades=$($inventory.summary.nativeFacadeCount), legacyTargets=0)")
Write-Output "Report: $ReportPath"
