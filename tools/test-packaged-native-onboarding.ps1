[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SdkArchive,

    [Parameter(Mandatory)]
    [string]$EngineRoot,

    [string]$OutputDirectory,

    [switch]$Resume
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$startedAt = [DateTime]::UtcNow
$repoRoot = Split-Path -Parent $PSScriptRoot
$archivePath = [IO.Path]::GetFullPath($SdkArchive)
$enginePath = [IO.Path]::GetFullPath($EngineRoot)
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot "artifacts\adoption-native\$runId"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
$extractRoot = Join-Path $outputRoot 'sdk'
$projectName = 'AdoptedBlueprintGame'
$projectRoot = Join-Path $outputRoot 'consumer'
$project = Join-Path $projectRoot "$projectName.uproject"
$packageArchive = Join-Path $outputRoot 'package'
$reportPath = Join-Path $outputRoot 'packaged-native-onboarding-report.json'
$runUat = Join-Path $enginePath 'Engine\Build\BatchFiles\RunUAT.bat'
$originalProjectSha256 = $null

function Write-Utf8 {
    param([string]$Path, [string]$Text)

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) |
        Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][object[]]$Arguments,
        [Parameter(Mandatory)][string]$LogPath,
        [Parameter(Mandatory)][string]$FailureMessage
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $LogPath) |
        Out-Null
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $FilePath @Arguments 2>&1 |
            Tee-Object -FilePath $LogPath |
            ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        $tail = if (Test-Path -LiteralPath $LogPath -PathType Leaf) {
            (Get-Content -LiteralPath $LogPath -Tail 40) -join [Environment]::NewLine
        } else {
            '<log unavailable>'
        }
        throw "$FailureMessage ExitCode=$exitCode. Log=$LogPath`n$tail"
    }
}

function Assert-OnboardingReport {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Mode,
        [Parameter(Mandatory)][string[]]$RequiredPhases
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Onboarding report is missing: $Path"
    }
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($report.schemaVersion -ne 'unifiedscript.unreal-project-onboarding-report/v0.1' -or
        $report.status -ne 'passed' -or
        $report.mode -ne $Mode -or
        $report.outputs.sdkLayout -ne 'embedded') {
        throw "Onboarding report did not prove $Mode embedded success: $Path"
    }
    $phaseByName = @{}
    foreach ($phase in $report.phases) {
        $phaseByName[[string]$phase.name] = [string]$phase.status
    }
    foreach ($requiredPhase in $RequiredPhases) {
        if ($phaseByName[$requiredPhase] -ne 'passed') {
            throw "Onboarding phase '$requiredPhase' did not pass in $Path"
        }
    }
    if ($report.outputs.automation.filter -ne 'UnifiedScript.Unreal.ProjectSetup' -or
        [int]$report.outputs.automation.required -ne 1 -or
        [int]$report.outputs.automation.passed -lt 1 -or
        [int]$report.outputs.automation.failed -ne 0) {
        throw "Project Setup Automation was not proven by $Path"
    }
    return $report
}

function Assert-File {
    param([string]$Path, [string]$Label)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label is missing: $Path"
    }
    return [ordered]@{
        path = $Path
        bytes = (Get-Item -LiteralPath $Path).Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).
            Hash.ToLowerInvariant()
    }
}

foreach ($required in @(
        $archivePath,
        $runUat,
        (Join-Path $enginePath 'Engine\Build\Build.version'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required native onboarding input is missing: $required"
    }
}
if ((Test-Path -LiteralPath $outputRoot) -and -not $Resume) {
    throw "Native onboarding output already exists: $outputRoot"
}
if (-not (Test-Path -LiteralPath $outputRoot) -and $Resume) {
    throw "Native onboarding output cannot be resumed because it does not exist: $outputRoot"
}
if (-not $Resume) {
    New-Item -ItemType Directory -Force -Path $extractRoot, $projectRoot |
        Out-Null
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot
}
$sdkRoot = Get-ChildItem -LiteralPath $extractRoot -Directory |
    Select-Object -ExpandProperty FullName -First 1
if ([string]::IsNullOrWhiteSpace($sdkRoot)) {
    throw 'The SDK archive has no top-level directory.'
}
$sdkCli = Join-Path $sdkRoot 'Tools\unifiedscript.cmd'

$adoptionReportPath = Join-Path $projectRoot `
    'Saved\UnifiedScript\ProjectSetup\adoption.json'
if (-not $Resume) {
    $projectDocument = [ordered]@{
        FileVersion = 3
        EngineAssociation = '5.8'
        Category = 'Games'
        Description = 'UnifiedScript clean Blueprint-only native onboarding fixture.'
    }
    Write-Utf8 -Path $project -Text (
        ($projectDocument | ConvertTo-Json -Depth 8) + "`n")
    Write-Utf8 `
        -Path (Join-Path $projectRoot 'Config\DefaultEngine.ini') `
        -Text @"
[/Script/EngineSettings.GameMapsSettings]
EditorStartupMap=/Engine/Maps/Entry
GameDefaultMap=/Engine/Maps/Entry
"@
    $originalProjectSha256 = (Get-FileHash `
        -LiteralPath $project `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    Invoke-LoggedCommand `
        -FilePath $sdkCli `
        -Arguments @(
            'adopt', $project,
            '--sdk-root', $sdkRoot,
            '--apply',
            '--report', $adoptionReportPath) `
        -LogPath (Join-Path $outputRoot 'adoption.log') `
        -FailureMessage 'Packaged project adoption failed.'
}
if ([string]::IsNullOrWhiteSpace($originalProjectSha256)) {
    $transaction = Get-Content `
        -LiteralPath (Join-Path $projectRoot `
            'Saved\UnifiedScript\Adoption\current.json') `
        -Raw | ConvertFrom-Json
    $transactionPath = Join-Path $projectRoot (
        "Saved\UnifiedScript\Adoption\transactions\$($transaction.transactionId)\transaction.json")
    $transactionDocument = Get-Content -LiteralPath $transactionPath -Raw |
        ConvertFrom-Json
    $projectEntry = $transactionDocument.files |
        Where-Object { $_.kind -eq 'project-merge' } |
        Select-Object -First 1
    $originalProjectSha256 = [string]$projectEntry.previousSha256
}

$projectTools = Join-Path $projectRoot 'Plugins\UEUnifiedScript\Tools'
$localSetup = Join-Path $projectTools 'setup-unreal-project.ps1'
$localCli = Join-Path $projectTools 'unifiedscript.cmd'
foreach ($required in @($localSetup, $localCli)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Adoption did not install its project-local entry point: $required"
    }
}

$applyReportPath = Join-Path $projectRoot `
    'Saved\UnifiedScript\ProjectSetup\native-onboarding.json'
if (-not $Resume -or
    -not (Test-Path -LiteralPath $applyReportPath -PathType Leaf)) {
    Invoke-LoggedCommand `
        -FilePath 'powershell.exe' `
        -Arguments @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $localSetup,
            '-Project', $project,
            '-EngineRoot', $enginePath,
            '-Report', $applyReportPath) `
        -LogPath (Join-Path $outputRoot 'native-onboarding.log') `
        -FailureMessage 'Project-local native onboarding failed.'
}

$applyReport = Assert-OnboardingReport `
    -Path $applyReportPath `
    -Mode 'apply' `
    -RequiredPhases @(
        'preflight',
        'managed-toolchain',
        'native-bootstrap-build',
        'reflection-export',
        'development-surface',
        'binding-generation',
        'managed-gameplay-build',
        'compiled-chunks',
        'native-final-build',
        'native-game-build',
        'deployment',
        'determinism',
        'managed-startup')

$assemblyName = "$projectName.UnifiedScript"
$generatedEvidence = [ordered]@{
    reflection = Assert-File `
        -Path (Join-Path $projectRoot "Generated\$projectName.reflection.json") `
        -Label 'Reflection manifest'
    bindings = Assert-File `
        -Path (Join-Path $projectRoot `
            "Managed\$assemblyName\Generated\Unreal.bindings.generated.cs") `
        -Label 'Managed bindings'
    nativeThunks = Assert-File `
        -Path (Join-Path $projectRoot `
            'Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Private\Generated\UEUnifiedScriptRuntime.native-thunks.generated.cpp') `
        -Label 'Native thunk table'
    coverage = Assert-File `
        -Path (Join-Path $projectRoot `
            "Generated\$projectName.binding-coverage.json") `
        -Label 'Binding coverage'
    apiRequests = Assert-File `
        -Path (Join-Path $projectRoot `
            "Generated\$projectName.api-requests.json") `
        -Label 'Source API dependency requests'
    developmentContract = Assert-File `
        -Path (Join-Path $projectRoot `
            "Generated\$projectName.development-api-contract.json") `
        -Label 'Automatic development API Contract'
    developmentSurface = Assert-File `
        -Path (Join-Path $projectRoot `
            "Generated\$projectName.development-surface.json") `
        -Label 'Automatic development API surface report'
    managedAssembly = Assert-File `
        -Path (Join-Path $projectRoot `
            "Plugins\UEUnifiedScript\Binaries\Managed\Scripts\$assemblyName.dll") `
        -Label 'Managed gameplay assembly'
    managedChunk = Assert-File `
        -Path (Join-Path $projectRoot `
            "Plugins\UEUnifiedScript\Binaries\Managed\$assemblyName.uschunk") `
        -Label 'Managed gameplay chunk'
    scriptAsset = Assert-File `
        -Path (Join-Path $projectRoot `
            'Content\UnifiedScript\FirstScript\SA_FirstScript.uasset') `
        -Label 'Generated Script Asset'
    editorPlugin = Assert-File `
        -Path (Join-Path $projectRoot `
            'Plugins\UEUnifiedScript\Binaries\Win64\UnrealEditor-UEUnifiedScriptRuntime.dll') `
        -Label 'Built Editor plugin'
    gameReceipt = Assert-File `
        -Path (Join-Path $projectRoot `
            "Binaries\Win64\$projectName.target") `
        -Label 'Built game target receipt'
}
$surfaceReport = Get-Content `
    -LiteralPath $generatedEvidence.developmentSurface.path `
    -Raw | ConvertFrom-Json
$surfaceRequests = Get-Content `
    -LiteralPath $generatedEvidence.apiRequests.path `
    -Raw | ConvertFrom-Json
$surfaceContract = Get-Content `
    -LiteralPath $generatedEvidence.developmentContract.path `
    -Raw | ConvertFrom-Json
if ($surfaceReport.schemaVersion -ne
        'unifiedscript.unreal-api-development-surface/v0.1' -or
    $surfaceReport.applied -ne $true -or
    [int]$surfaceReport.summary.selectedCount -lt 1 -or
    [int]$surfaceReport.summary.openDecisionCount -ne 0 -or
    @($surfaceRequests.requests | Where-Object {
        $_.suggestedTypePath -eq '/Script/Engine.Actor' -and
        $_.memberName -eq 'GetActorLocation'
    }).Count -ne 1 -or
    @($surfaceContract.types | Where-Object {
        $_.path -eq '/Script/Engine.Actor' -and
        'K2_GetActorLocation' -in @($_.members)
    }).Count -ne 1) {
    throw 'Automatic Actor.GetActorLocation development surface acceptance failed.'
}
$managedBindingText = Get-Content -LiteralPath $generatedEvidence.bindings.path -Raw
if ($managedBindingText -notmatch 'class Actor' -or
    $managedBindingText -notmatch 'GetActorLocation') {
    throw 'Generated managed binding does not contain the source-requested Actor API.'
}

$validateReportPath = Join-Path $projectRoot `
    'Saved\UnifiedScript\ProjectSetup\native-validate.json'
if (-not $Resume -or
    -not (Test-Path -LiteralPath $validateReportPath -PathType Leaf)) {
    Invoke-LoggedCommand `
        -FilePath 'powershell.exe' `
        -Arguments @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $localSetup,
            '-Project', $project,
            '-EngineRoot', $enginePath,
            '-ValidateOnly',
            '-Report', $validateReportPath) `
        -LogPath (Join-Path $outputRoot 'native-validate.log') `
        -FailureMessage 'Project-local deterministic validation failed.'
}
$validateReport = Assert-OnboardingReport `
    -Path $validateReportPath `
    -Mode 'validateOnly' `
    -RequiredPhases @(
        'preflight',
        'development-surface-validation',
        'deployment',
        'determinism',
        'managed-startup')

$cookLog = Join-Path $outputRoot 'build-cook-run.log'
Invoke-LoggedCommand `
    -FilePath $runUat `
    -Arguments @(
        'BuildCookRun',
        "-project=$project",
        '-noP4',
        '-platform=Win64',
        '-clientconfig=Development',
        '-build',
        '-cook',
        '-stage',
        '-pak',
        '-archive',
        '-map=/Engine/Maps/Entry',
        "-CookDir=$(Join-Path $projectRoot 'Content\UnifiedScript')",
        "-archivedirectory=$packageArchive",
        '-AdditionalCookerOptions=-SkipZenStore',
        '-unattended',
        '-utf8output',
        '-NoCodeSign',
        '-NoXGE') `
    -LogPath $cookLog `
    -FailureMessage 'Adopted project BuildCookRun failed.'

$packageRoot = Join-Path $packageArchive 'Windows'
$projectPackageRoot = Join-Path $packageRoot $projectName
$packageEvidence = [ordered]@{
    executable = Assert-File `
        -Path (Join-Path $packageRoot "$projectName.exe") `
        -Label 'Packaged game executable'
    core = Assert-File `
        -Path (Join-Path $projectPackageRoot `
            'Plugins\UEUnifiedScript\Binaries\Managed\UnifiedScript.Core.dll') `
        -Label 'Packaged managed core'
    unreal = Assert-File `
        -Path (Join-Path $projectPackageRoot `
            'Plugins\UEUnifiedScript\Binaries\Managed\UnifiedScript.Unreal.dll') `
        -Label 'Packaged managed Unreal host'
    gameplay = Assert-File `
        -Path (Join-Path $projectPackageRoot `
            "Plugins\UEUnifiedScript\Binaries\Managed\Scripts\$assemblyName.dll") `
        -Label 'Packaged gameplay assembly'
    chunk = Assert-File `
        -Path (Join-Path $projectPackageRoot `
            "Plugins\UEUnifiedScript\Binaries\Managed\$assemblyName.uschunk") `
        -Label 'Packaged gameplay chunk'
}
$hostFxr = Get-ChildItem `
    -LiteralPath (Join-Path $projectPackageRoot `
        'Plugins\UEUnifiedScript\Binaries\ThirdParty\DotNet\10.0\win-x64\host\fxr') `
    -Filter hostfxr.dll -File -Recurse |
    Select-Object -First 1
if ($null -eq $hostFxr) {
    throw 'Packaged CoreCLR hostfxr.dll is missing.'
}
$cookedAsset = Get-ChildItem `
    -LiteralPath (Join-Path $projectRoot 'Saved\Cooked') `
    -Filter SA_FirstScript.uasset -File -Recurse |
    Select-Object -First 1
if ($null -eq $cookedAsset) {
    throw 'The generated FirstScript asset was not cooked.'
}
$forbiddenPayload = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse |
    Where-Object {
        $_.FullName -match '(?i)puerts' -or
        $_.Extension -match '(?i)^\.(?:js|ts)$'
    })
if ($forbiddenPayload.Count -ne 0) {
    throw (
        'Packaged output contains forbidden Puerts/JavaScript payload: ' +
        (($forbiddenPayload | Select-Object -ExpandProperty FullName) -join ', '))
}

$doctorReportPath = Join-Path $projectRoot `
    'Saved\UnifiedScript\ProjectSetup\native-doctor.json'
Invoke-LoggedCommand `
    -FilePath $localCli `
    -Arguments @(
        'doctor', $project,
        '--engine-root', $enginePath,
        '--report', $doctorReportPath) `
    -LogPath (Join-Path $outputRoot 'native-doctor.log') `
    -FailureMessage 'Project-local Doctor failed after Setup and Cook.'
$doctorReport = Get-Content -LiteralPath $doctorReportPath -Raw |
    ConvertFrom-Json
if ($doctorReport.status -ne 'passed') {
    throw "Project-local Doctor did not pass after Setup and Cook: $doctorReportPath"
}

$repairReportPath = Join-Path $projectRoot `
    'Saved\UnifiedScript\ProjectSetup\native-repair.json'
Invoke-LoggedCommand `
    -FilePath $localCli `
    -Arguments @(
        'adopt', $project,
        '--sdk-root', (Join-Path $projectRoot 'Plugins\UEUnifiedScript'),
        '--apply',
        '--report', $repairReportPath) `
    -LogPath (Join-Path $outputRoot 'native-repair.log') `
    -FailureMessage 'Project-local no-change Repair failed after Setup and Cook.'
$repairReport = Get-Content -LiteralPath $repairReportPath -Raw |
    ConvertFrom-Json
$nativeHostRepair = @($repairReport.files |
    Where-Object { $_.kind -eq 'native-host' })
if ([int]$repairReport.summary.changed -ne 0 -or
    [int]$repairReport.summary.conflicts -ne 0 -or
    $nativeHostRepair.Count -ne 4 -or
    @($nativeHostRepair | Where-Object { $_.action -ne 'unchanged' }).Count -ne 0) {
    throw "Project-local Repair was not a complete native-host no-op: $repairReportPath"
}

Invoke-LoggedCommand `
    -FilePath $sdkCli `
    -Arguments @('adopt', $project, '--rollback') `
    -LogPath (Join-Path $outputRoot 'native-rollback.log') `
    -FailureMessage 'Packaged SDK rollback failed after native acceptance.'
$restoredProjectSha256 = (Get-FileHash `
    -LiteralPath $project `
    -Algorithm SHA256).Hash.ToLowerInvariant()
if ($restoredProjectSha256 -ne $originalProjectSha256 -or
    (Test-Path -LiteralPath (Join-Path $projectRoot `
        'Plugins\UEUnifiedScript\UEUnifiedScript.uplugin')) -or
    (Test-Path -LiteralPath (Join-Path $projectRoot `
        'Plugins\UEUnifiedScript\Source')) -or
    (Test-Path -LiteralPath (Join-Path $projectRoot `
        'Plugins\UEUnifiedScript\Tools')) -or
    (Test-Path -LiteralPath (Join-Path $projectRoot "Source\$projectName.Target.cs")) -or
    (Test-Path -LiteralPath (Join-Path $projectRoot "Source\$projectName"))) {
    throw 'Rollback did not restore the original Blueprint-only project surface.'
}

$report = [ordered]@{
    schemaVersion = 'unifiedscript.packaged-native-onboarding-acceptance/v0.1'
    status = 'passed'
    sdkArchive = $archivePath
    sdkArchiveSha256 = (Get-FileHash `
        -LiteralPath $archivePath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    engineRoot = $enginePath
    project = $project
    adoptionReport = $adoptionReportPath
    onboarding = [ordered]@{
        applyReport = $applyReportPath
        applyDurationMs = [long]$applyReport.durationMs
        validateReport = $validateReportPath
        validateDurationMs = [long]$validateReport.durationMs
        automationPassed = [int]$applyReport.outputs.automation.passed
        generated = $generatedEvidence
    }
    package = [ordered]@{
        archiveRoot = $packageRoot
        projectRoot = $projectPackageRoot
        files = $packageEvidence
        hostFxr = $hostFxr.FullName
        cookedScriptAsset = $cookedAsset.FullName
        forbiddenPuertsOrJavaScriptFiles = $forbiddenPayload.Count
    }
    repair = [ordered]@{
        doctorReport = $doctorReportPath
        doctorStatus = [string]$doctorReport.status
        repairReport = $repairReportPath
        changed = [int]$repairReport.summary.changed
        conflicts = [int]$repairReport.summary.conflicts
        nativeHostFiles = $nativeHostRepair.Count
        rollback = 'passed'
        restoredProjectSha256 = $restoredProjectSha256
    }
    durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
}
Write-Utf8 -Path $reportPath -Text (($report | ConvertTo-Json -Depth 12) + "`n")
Write-Host 'UnifiedScript packaged native onboarding: PASS (Apply/Validate/Cook/Repair/Rollback)'
Write-Host $reportPath
