[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SdkArchive,

    [Parameter(Mandatory)]
    [string]$EngineRoot,

    [string]$OutputDirectory,

    [switch]$Resume,

    [switch]$IncludeScriptClassFirstPlay,

    [switch]$IncludeScriptClassLifecycleReplay
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
$baselinePath = Join-Path $outputRoot 'blueprint-only-baseline.json'
$runUat = Join-Path $enginePath 'Engine\Build\BatchFiles\RunUAT.bat'
$scriptClassGoldenPath = Join-Path $repoRoot 'tools\test-script-class-golden-path.ps1'
$scriptClassLifecycleReplay = Join-Path $repoRoot `
    'tools\test-packaged-script-class-lifecycle.ps1'
$originalProjectSha256 = $null
$originalDefaultEngineSha256 = $null
$originalUserSentinelSha256 = $null
$userSentinelPath = Join-Path $projectRoot 'Content\UserOwned\keep.txt'
$scriptClassFirstPlayReport = $null
$scriptClassLifecycleReport = $null

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
        [int]$report.outputs.automation.required -ne 2 -or
        [int]$report.outputs.automation.passed -lt 2 -or
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

[/Script/AndroidFileServerEditor.AndroidFileServerRuntimeSettings]
bEnablePlugin=False
bAllowNetworkConnection=False
SecurityToken=00000000000000000000000000000000
bIncludeInShipping=False
bAllowExternalStartInShipping=False
bCompileAFSProject=False
bUseCompression=False
bLogFiles=False
bReportStats=False
ConnectionType=USBOnly
bUseManualIPAddress=False
ManualIPAddress=
"@
    Write-Utf8 `
        -Path $userSentinelPath `
        -Text "user-owned acceptance sentinel`n"
    $originalProjectSha256 = (Get-FileHash `
        -LiteralPath $project `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $originalDefaultEngineSha256 = (Get-FileHash `
        -LiteralPath (Join-Path $projectRoot 'Config\DefaultEngine.ini') `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $originalUserSentinelSha256 = (Get-FileHash `
        -LiteralPath $userSentinelPath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    $baselineDocument = [ordered]@{
        schemaVersion = 'unifiedscript.blueprint-only-baseline/v0.1'
        projectSha256 = $originalProjectSha256
        defaultEngineSha256 = $originalDefaultEngineSha256
        userSentinelSha256 = $originalUserSentinelSha256
    }
    Write-Utf8 `
        -Path $baselinePath `
        -Text (($baselineDocument | ConvertTo-Json -Depth 4) + "`n")
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
if ($Resume) {
    if (-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) {
        throw "Native onboarding baseline is missing and cannot be resumed: $baselinePath"
    }
    $baseline = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
    if ($baseline.schemaVersion -ne 'unifiedscript.blueprint-only-baseline/v0.1') {
        throw "Native onboarding baseline schema is unsupported: $baselinePath"
    }
    $originalProjectSha256 = [string]$baseline.projectSha256
    $originalDefaultEngineSha256 = [string]$baseline.defaultEngineSha256
    $originalUserSentinelSha256 = [string]$baseline.userSentinelSha256
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
    apiTypeHints = Assert-File `
        -Path (Join-Path $projectRoot `
            "Generated\$projectName.api-type-hints.json") `
        -Label 'Source reflection type hints'
    developmentContract = Assert-File `
        -Path (Join-Path $projectRoot `
            "Generated\$projectName.development-api-contract.json") `
        -Label 'Automatic development API Contract'
    surfaceLock = Assert-File `
        -Path (Join-Path $projectRoot `
            "Generated\$projectName.generated-surface.lock.json") `
        -Label 'Generated API surface lock'
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
    firstScriptSource = Assert-File `
        -Path (Join-Path $projectRoot "Managed\$assemblyName\FirstScript.cs") `
        -Label 'Managed first-script source'
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
$sourceTypeHints = Get-Content `
    -LiteralPath $generatedEvidence.apiTypeHints.path `
    -Raw | ConvertFrom-Json
$surfaceContract = Get-Content `
    -LiteralPath $generatedEvidence.developmentContract.path `
    -Raw | ConvertFrom-Json
$surfaceLock = Get-Content `
    -LiteralPath $generatedEvidence.surfaceLock.path `
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
    @($surfaceRequests.requests | Where-Object {
        $_.suggestedTypePath -eq '/Script/Engine.Pawn' -and
        $_.memberName -eq 'GetController'
    }).Count -ne 1 -or
    @($surfaceContract.types | Where-Object {
        $_.path -eq '/Script/Engine.Actor' -and
        'K2_GetActorLocation' -in @($_.members)
    }).Count -ne 1 -or
    @($surfaceContract.types | Where-Object {
        $_.path -eq '/Script/Engine.Pawn' -and
        'GetController' -in @($_.members)
    }).Count -ne 1) {
    throw 'Automatic Actor/Pawn development surface acceptance failed.'
}
if ($sourceTypeHints.schemaVersion -ne
        'unifiedscript.unreal-api-source-type-hints/v0.1' -or
    @($sourceTypeHints.hints | Where-Object {
        $_.managedTypeName -eq 'Pawn'
    }).Count -ne 1) {
    throw 'Managed source did not produce the bounded Pawn reflection hint.'
}
if ($surfaceLock.schemaVersion -ne
        'unifiedscript.unreal-api-generated-surface-lock/v0.1' -or
    [string]::IsNullOrWhiteSpace($surfaceLock.fingerprint) -or
    @($surfaceLock.types | Where-Object {
        $_.typePath -eq '/Script/Engine.Actor'
    }).Count -ne 1 -or
    @($surfaceLock.types | Where-Object {
        $_.typePath -eq '/Script/Engine.Pawn'
    }).Count -ne 1) {
    throw 'Generated API surface lock did not freeze the exact Actor/Pawn surface.'
}
$managedBindingText = Get-Content -LiteralPath $generatedEvidence.bindings.path -Raw
if ($managedBindingText -notmatch 'class Actor' -or
    $managedBindingText -notmatch 'GetActorLocation' -or
    $managedBindingText -notmatch 'class Pawn' -or
    $managedBindingText -notmatch 'GetController') {
    throw 'Generated managed binding does not contain the source-requested Actor/Pawn API.'
}
$firstScriptText = Get-Content -LiteralPath $generatedEvidence.firstScriptSource.path -Raw
$generatedNamespacePattern = [regex]::Escape("$assemblyName.Generated")
if ($firstScriptText -notmatch "using\s+$generatedNamespacePattern\s*;" -or
    $firstScriptText -match 'using\s+UnrealApi\s*=' -or
    $firstScriptText -notmatch 'new\s+Actor\s*\(' -or
    $firstScriptText -notmatch 'new\s+Pawn\s*\(') {
    throw 'First-script source did not exercise the ordinary generated-namespace import.'
}

if ($IncludeScriptClassFirstPlay) {
    $scriptClassOutput = Join-Path $outputRoot 'script-class-first-play'
    Invoke-LoggedCommand `
        -FilePath 'powershell.exe' `
        -Arguments @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $scriptClassGoldenPath,
            '-Project', $project,
            '-EngineRoot', $enginePath,
            '-OutputDirectory', $scriptClassOutput) `
        -LogPath (Join-Path $outputRoot 'script-class-first-play.log') `
        -FailureMessage 'Packaged Script Class first-Play workflow failed.'
    $scriptClassReportPath = Join-Path $scriptClassOutput `
        'script-class-golden-path-report.json'
    $scriptClassFirstPlayReport = Get-Content `
        -LiteralPath $scriptClassReportPath -Raw | ConvertFrom-Json
    if ($scriptClassFirstPlayReport.schemaVersion -ne
            'unifiedscript.script-class-golden-path/v0.2' -or
        $scriptClassFirstPlayReport.status -ne 'passed' -or
        $scriptClassFirstPlayReport.firstPlay.status -ne 'ready' -or
        [bool]$scriptClassFirstPlayReport.cook.skipped -or
        @($scriptClassFirstPlayReport.cook.cookedAssets).Count -ne 4 -or
        $scriptClassFirstPlayReport.packagedSmoke.status -ne 'passed' -or
        [int]$scriptClassFirstPlayReport.packagedSmoke.managedScriptsAtPeak -ne 2 -or
        [int]$scriptClassFirstPlayReport.packagedSmoke.lifecycleClaimsAtPeak -ne 2 -or
        $scriptClassFirstPlayReport.onboarding.postSmokeGeneratedDiff -ne 'none') {
        throw "Packaged Script Class report did not prove first Play, Cook, packaged execution, and no-change rerun: $scriptClassReportPath"
    }
}

if ($IncludeScriptClassLifecycleReplay) {
    $scriptClassLifecycleOutput = Join-Path $outputRoot `
        'script-class-lifecycle-replay'
    Invoke-LoggedCommand `
        -FilePath 'powershell.exe' `
        -Arguments @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $scriptClassLifecycleReplay,
            '-Project', $project,
            '-EngineRoot', $enginePath,
            '-OutputDirectory', $scriptClassLifecycleOutput) `
        -LogPath (Join-Path $outputRoot `
            'script-class-lifecycle-replay.log') `
        -FailureMessage 'Packaged Script Class lifecycle replay failed.'
    $scriptClassLifecycleReportPath = Join-Path `
        $scriptClassLifecycleOutput `
        'packaged-script-class-lifecycle-report.json'
    $scriptClassLifecycleReport = Get-Content `
        -LiteralPath $scriptClassLifecycleReportPath -Raw | ConvertFrom-Json
    $lifecycleOperations = @($scriptClassLifecycleReport.operations)
    if ($scriptClassLifecycleReport.schemaVersion -ne
            'unifiedscript.packaged-script-class-lifecycle/v0.1' -or
        $scriptClassLifecycleReport.status -ne 'passed' -or
        $lifecycleOperations.Count -ne 4 -or
        @($lifecycleOperations | Where-Object {
            $_.success -ne 'passed' -or
            $_.injectedRollback -ne 'passed' -or
            $_.recoveryReceipt -ne 'persisted-recovery-passed'
        }).Count -ne 0 -or
        [int]$scriptClassLifecycleReport.closure.pendingReceipts -ne 0 -or
        $scriptClassLifecycleReport.closure.managedSources -ne
            'baseline-restored' -or
        $scriptClassLifecycleReport.closure.deployment -ne
            'byte-exact-baseline' -or
        $scriptClassLifecycleReport.closure.uePackages -ne
            'baseline-restored' -or
        $scriptClassLifecycleReport.closure.ueReferences -ne
            'rewritten-and-rollback-verified' -or
        $scriptClassLifecycleReport.closure.redirectors -ne
            'removed-and-rollback-verified') {
        throw "Packaged Script Class lifecycle report is incomplete: $scriptClassLifecycleReportPath"
    }
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
$restoredDefaultEngineSha256 = (Get-FileHash `
    -LiteralPath (Join-Path $projectRoot 'Config\DefaultEngine.ini') `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$restoredUserSentinelSha256 = (Get-FileHash `
    -LiteralPath $userSentinelPath `
    -Algorithm SHA256).Hash.ToLowerInvariant()
$rollbackViolations = @()
if ($restoredProjectSha256 -ne $originalProjectSha256) {
    $rollbackViolations += 'project-hash'
}
if ($restoredDefaultEngineSha256 -ne $originalDefaultEngineSha256) {
    $rollbackViolations += 'default-engine-hash'
}
if ($restoredUserSentinelSha256 -ne $originalUserSentinelSha256) {
    $rollbackViolations += 'user-sentinel-hash'
}
foreach ($unexpectedPath in @(
        'Plugins\UEUnifiedScript\UEUnifiedScript.uplugin',
        'Plugins\UEUnifiedScript\Source',
        'Plugins\UEUnifiedScript\Tools',
        "Source\$projectName.Target.cs",
        "Source\$projectName")) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot $unexpectedPath)) {
        $rollbackViolations += $unexpectedPath
    }
}
if ($rollbackViolations.Count -ne 0) {
    throw (
        'Rollback did not restore the original Blueprint-only project surface: ' +
        ($rollbackViolations -join ', '))
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
    scriptClassFirstPlay = if ($null -eq $scriptClassFirstPlayReport) {
        [ordered]@{ included = $false }
    } else {
        $scriptClassFirstPlayReport
    }
    scriptClassLifecycleReplay = if ($null -eq $scriptClassLifecycleReport) {
        [ordered]@{ included = $false }
    } else {
        $scriptClassLifecycleReport
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
        restoredDefaultEngineSha256 = $restoredDefaultEngineSha256
        restoredUserSentinelSha256 = $restoredUserSentinelSha256
        userOwnedFilesPreserved = $true
    }
    durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
}
Write-Utf8 -Path $reportPath -Text (($report | ConvertTo-Json -Depth 12) + "`n")
Write-Host 'UnifiedScript packaged native onboarding: PASS (Apply/Validate/Cook/Repair/Rollback)'
Write-Host $reportPath
