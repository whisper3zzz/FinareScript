[CmdletBinding()]
param(
    [string]$Project = (Join-Path $PSScriptRoot '..\HostProject\UEUnifiedScriptHost.uproject'),

    [Parameter(Mandatory)]
    [string]$EngineRoot,

    [string]$OutputDirectory,

    [switch]$SkipCook
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$startedAt = [DateTime]::UtcNow
$projectPath = [IO.Path]::GetFullPath($Project)
$projectRoot = Split-Path -Parent $projectPath
$enginePath = [IO.Path]::GetFullPath($EngineRoot)
$editorCmd = Join-Path $enginePath 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
$runUat = Join-Path $enginePath 'Engine\Build\BatchFiles\RunUAT.bat'
$setupScript = Join-Path $PSScriptRoot 'setup-unreal-project.ps1'
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $projectRoot "Saved\UnifiedScript\ScriptClassGoldenPath\$runId"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}

function Resolve-ConfigPath {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$BaseDirectory
    )

    if ([IO.Path]::IsPathRooted($Value)) {
        return [IO.Path]::GetFullPath($Value)
    }
    return [IO.Path]::GetFullPath((Join-Path $BaseDirectory $Value))
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Label
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label escapes its expected root. Path=$fullPath Root=$fullRoot"
    }
    return $fullPath
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
            (Get-Content -LiteralPath $LogPath -Tail 60) -join [Environment]::NewLine
        } else {
            '<log unavailable>'
        }
        throw "$FailureMessage ExitCode=$exitCode. Log=$LogPath`n$tail"
    }
}

function Invoke-AutomationStage {
    param(
        [Parameter(Mandatory)][string]$Filter,
        [Parameter(Mandatory)][string]$Stage
    )

    $engineLog = Join-Path $outputRoot "$Stage-editor.log"
    $stdoutLog = Join-Path $outputRoot "$Stage-stdout.log"
    Invoke-LoggedCommand `
        -FilePath $editorCmd `
        -Arguments @(
            $projectPath,
            '-unattended',
            '-nop4',
            '-NullRHI',
            '-nosplash',
            "-ExecCmds=Automation RunTests $Filter",
            '-TestExit=Automation Test Queue Empty',
            "-abslog=$engineLog",
            '-log') `
        -LogPath $stdoutLog `
        -FailureMessage "Script Class $Stage Automation failed."
    $text = @(
        if (Test-Path -LiteralPath $engineLog) {
            Get-Content -LiteralPath $engineLog -Raw
        }
        if (Test-Path -LiteralPath $stdoutLog) {
            Get-Content -LiteralPath $stdoutLog -Raw
        }) -join [Environment]::NewLine
    $successCount = [regex]::Matches(
        $text,
        'Test Completed\. Result=\{Success\}').Count
    $failureCount = [regex]::Matches(
        $text,
        'Test Completed\. Result=\{(Fail|Failed|Error)\}').Count
    if ($successCount -lt 1 -or $failureCount -ne 0) {
        throw "Script Class $Stage did not report one successful Automation test. Success=$successCount Failure=$failureCount"
    }
}

function Get-OutputFingerprints {
    param([Parameter(Mandatory)][object]$OnboardingReport)

    $paths = @(
        [string]$OnboardingReport.outputs.reflectionManifest,
        [string]$OnboardingReport.outputs.managedBindings,
        [string]$OnboardingReport.outputs.nativeThunks,
        [string]$OnboardingReport.outputs.apiNodeCatalog,
        [string]$OnboardingReport.outputs.apiBrowser,
        [string]$OnboardingReport.outputs.bindingCoverage,
        [string]$OnboardingReport.outputs.developmentSurface.typeHints,
        [string]$OnboardingReport.outputs.developmentSurface.requests,
        [string]$OnboardingReport.outputs.developmentSurface.effectiveContract,
        [string]$OnboardingReport.outputs.developmentSurface.report,
        [string]$OnboardingReport.outputs.developmentSurface.lock) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique
    $fingerprints = [ordered]@{}
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Onboarding output is missing before no-change validation: $path"
        }
        $fingerprints[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).
            Hash.ToLowerInvariant()
    }
    return $fingerprints
}

function Assert-FingerprintsUnchanged {
    param([Parameter(Mandatory)][Collections.IDictionary]$Before)

    foreach ($entry in $Before.GetEnumerator()) {
        $current = (Get-FileHash -LiteralPath $entry.Key -Algorithm SHA256).
            Hash.ToLowerInvariant()
        if ($current -ne $entry.Value) {
            throw "ValidateOnly changed deterministic output '$($entry.Key)'. Before=$($entry.Value) After=$current"
        }
    }
}

function Remove-FixtureFiles {
    param([Parameter(Mandatory)][string[]]$Paths)

    foreach ($path in $Paths) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
}

foreach ($required in @($projectPath, $editorCmd, $runUat, $setupScript)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required Script Class golden-path input is missing: $required"
    }
}
if (Test-Path -LiteralPath $outputRoot) {
    throw "Script Class golden-path output already exists: $outputRoot"
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$configPath = Join-Path $projectRoot 'Config\UnifiedScript.onboarding.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$configDirectory = Split-Path -Parent $configPath
$managedProject = Resolve-ConfigPath `
    -Value ([string]$config.managed.projects[0]) `
    -BaseDirectory $configDirectory
$managedRoot = Split-Path -Parent $managedProject
$deploymentPath = Resolve-ConfigPath `
    -Value ([string]$config.deployment.manifest) `
    -BaseDirectory $configDirectory
$sourceRoot = Assert-ChildPath `
    -Path (Join-Path $managedRoot 'Classes') `
    -Root $managedRoot `
    -Label 'Managed fixture source root'
$contentRoot = Assert-ChildPath `
    -Path (Join-Path $projectRoot 'Content\UnifiedScriptAutomation') `
    -Root (Join-Path $projectRoot 'Content') `
    -Label 'Script Class fixture content root'
$receiptRoot = Assert-ChildPath `
    -Path (Join-Path $projectRoot 'Saved\UnifiedScript\ScriptClassGoldenPath') `
    -Root (Join-Path $projectRoot 'Saved') `
    -Label 'Script Class fixture receipt root'
$fixtureNames = @('M13C0PersistentActor', 'M13C0PersistentComponent')
$fixtureFiles = [Collections.Generic.List[string]]::new()
foreach ($name in $fixtureNames) {
    $fixtureFiles.Add((Assert-ChildPath `
        -Path (Join-Path $sourceRoot "$name.cs") `
        -Root $managedRoot `
        -Label 'Managed fixture source'))
    foreach ($assetName in @($name, "SA_$name")) {
        foreach ($extension in @('.uasset', '.uexp', '.ubulk')) {
            $fixtureFiles.Add((Assert-ChildPath `
                -Path (Join-Path $contentRoot "$assetName$extension") `
                -Root (Join-Path $projectRoot 'Content') `
                -Label 'Script Class fixture package'))
        }
    }
}
$receiptFiles = @(
    (Join-Path $receiptRoot 'create.pid'),
    (Join-Path $receiptRoot 'reload.pid'))
foreach ($path in $fixtureFiles) {
    if (Test-Path -LiteralPath $path) {
        throw "A reserved Script Class golden-path fixture already exists: $path"
    }
}

$deploymentBytes = [IO.File]::ReadAllBytes($deploymentPath)
$onboardingStarted = $false
$failure = $null
$createProcessId = 0
$reloadProcessId = 0
$applyReportPath = Join-Path $outputRoot 'onboarding-apply.json'
$validateReportPath = Join-Path $outputRoot 'onboarding-validate.json'
$cleanupReportPath = Join-Path $outputRoot 'onboarding-cleanup.json'
$packageArchive = Join-Path $outputRoot 'package'
$cookedAssets = @()

try {
    Remove-FixtureFiles -Paths $receiptFiles
    Invoke-AutomationStage `
        -Filter 'UnifiedScript.Integration.ScriptClass.CreatePersistent' `
        -Stage 'create'
    foreach ($required in @(
            (Join-Path $sourceRoot 'M13C0PersistentActor.cs'),
            (Join-Path $sourceRoot 'M13C0PersistentComponent.cs'),
            (Join-Path $contentRoot 'M13C0PersistentActor.uasset'),
            (Join-Path $contentRoot 'SA_M13C0PersistentActor.uasset'),
            (Join-Path $contentRoot 'M13C0PersistentComponent.uasset'),
            (Join-Path $contentRoot 'SA_M13C0PersistentComponent.uasset'),
            (Join-Path $receiptRoot 'create.pid'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Persistent Script Class creation output is missing: $required"
        }
    }
    $createProcessId = [int](Get-Content -LiteralPath (
        Join-Path $receiptRoot 'create.pid') -Raw)

    $onboardingStarted = $true
    Invoke-LoggedCommand `
        -FilePath 'powershell.exe' `
        -Arguments @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $setupScript,
            '-Project', $projectPath,
            '-EngineRoot', $enginePath,
            '-Report', $applyReportPath) `
        -LogPath (Join-Path $outputRoot 'onboarding-apply.log') `
        -FailureMessage 'Script Class Build/Onboard failed.'
    $applyReport = Get-Content -LiteralPath $applyReportPath -Raw |
        ConvertFrom-Json
    if ($applyReport.status -ne 'passed') {
        throw "Script Class Build/Onboard report did not pass: $applyReportPath"
    }
    $fingerprints = Get-OutputFingerprints -OnboardingReport $applyReport

    Invoke-AutomationStage `
        -Filter 'UnifiedScript.Integration.ScriptClass.ReloadLifecycle' `
        -Stage 'reload-lifecycle'
    $reloadProcessId = [int](Get-Content -LiteralPath (
        Join-Path $receiptRoot 'reload.pid') -Raw)
    if ($createProcessId -le 0 -or $reloadProcessId -le 0 -or
        $createProcessId -eq $reloadProcessId) {
        throw "Script Class reload was not proven in a second Editor process. CreatePid=$createProcessId ReloadPid=$reloadProcessId"
    }

    Invoke-LoggedCommand `
        -FilePath 'powershell.exe' `
        -Arguments @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $setupScript,
            '-Project', $projectPath,
            '-EngineRoot', $enginePath,
            '-ValidateOnly',
            '-Report', $validateReportPath) `
        -LogPath (Join-Path $outputRoot 'onboarding-validate.log') `
        -FailureMessage 'Script Class no-change validation failed.'
    $validateReport = Get-Content -LiteralPath $validateReportPath -Raw |
        ConvertFrom-Json
    if ($validateReport.status -ne 'passed' -or
        $validateReport.mode -ne 'validateOnly') {
        throw "Script Class ValidateOnly report did not pass: $validateReportPath"
    }
    Assert-FingerprintsUnchanged -Before $fingerprints

    if (-not $SkipCook) {
        Invoke-LoggedCommand `
            -FilePath $runUat `
            -Arguments @(
                'BuildCookRun',
                "-project=$projectPath",
                '-noP4',
                '-platform=Win64',
                '-clientconfig=Development',
                '-build',
                '-cook',
                '-stage',
                '-pak',
                '-archive',
                '-map=/Engine/Maps/Entry',
                "-CookDir=$contentRoot",
                "-archivedirectory=$packageArchive",
                '-AdditionalCookerOptions=-SkipZenStore',
                '-unattended',
                '-utf8output',
                '-NoCodeSign',
                '-NoXGE') `
            -LogPath (Join-Path $outputRoot 'build-cook-run.log') `
            -FailureMessage 'Script Class BuildCookRun failed.'
        foreach ($assetName in @(
                'M13C0PersistentActor',
                'SA_M13C0PersistentActor',
                'M13C0PersistentComponent',
                'SA_M13C0PersistentComponent')) {
            $cooked = Get-ChildItem `
                -LiteralPath (Join-Path $projectRoot 'Saved\Cooked') `
                -Filter "$assetName.uasset" `
                -File `
                -Recurse |
                Select-Object -First 1
            if ($null -eq $cooked) {
                throw "Cooked Script Class asset is missing: $assetName.uasset"
            }
            $cookedAssets += $cooked.FullName
        }
    }

    $report = [ordered]@{
        schemaVersion = 'unifiedscript.script-class-golden-path/v0.1'
        status = 'passed'
        project = $projectPath
        engineRoot = $enginePath
        createProcessId = $createProcessId
        reloadProcessId = $reloadProcessId
        onboarding = [ordered]@{
            applyReport = $applyReportPath
            validateReport = $validateReportPath
            deterministicOutputs = $fingerprints.Count
        }
        lifecycle = [ordered]@{
            actor = 'BeginPlay/Tick/EndPlay passed'
            actorComponent = 'BeginPlay/Tick/EndPlay passed'
            worldTeardown = 'passed'
        }
        cook = [ordered]@{
            skipped = [bool]$SkipCook
            packageRoot = if ($SkipCook) { $null } else {
                Join-Path $packageArchive 'Windows'
            }
            cookedAssets = $cookedAssets
        }
        durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
    }
    [IO.File]::WriteAllText(
        (Join-Path $outputRoot 'script-class-golden-path-report.json'),
        (($report | ConvertTo-Json -Depth 12) + "`n"),
        [Text.UTF8Encoding]::new($false))
} catch {
    $failure = $_
} finally {
    [IO.File]::WriteAllBytes($deploymentPath, $deploymentBytes)
    Remove-FixtureFiles -Paths (@($fixtureFiles) + $receiptFiles)
    if ($onboardingStarted) {
        try {
            Invoke-LoggedCommand `
                -FilePath 'powershell.exe' `
                -Arguments @(
                    '-NoProfile',
                    '-ExecutionPolicy', 'Bypass',
                    '-File', $setupScript,
                    '-Project', $projectPath,
                    '-EngineRoot', $enginePath,
                    '-Report', $cleanupReportPath) `
                -LogPath (Join-Path $outputRoot 'onboarding-cleanup.log') `
                -FailureMessage 'Script Class golden-path cleanup regeneration failed.'
        } catch {
            if ($null -eq $failure) {
                $failure = $_
            } else {
                Write-Warning $_.Exception.Message
            }
        }
    }
}

if ($null -ne $failure) {
    throw $failure
}
Write-Host 'UnifiedScript Script Class golden path: PASS'
Write-Host (Join-Path $outputRoot 'script-class-golden-path-report.json')
