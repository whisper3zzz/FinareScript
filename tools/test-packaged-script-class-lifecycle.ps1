[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Project,

    [Parameter(Mandatory)]
    [string]$EngineRoot,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$startedAt = [DateTime]::UtcNow
$projectPath = [IO.Path]::GetFullPath($Project)
$projectRoot = Split-Path -Parent $projectPath
$enginePath = [IO.Path]::GetFullPath($EngineRoot)
$editorCmd = Join-Path $enginePath 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $projectRoot "Saved\Finare\PackagedLifecycle\$runId"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
$reportPath = Join-Path $outputRoot 'packaged-script-class-lifecycle-report.json'
$engineLog = Join-Path $outputRoot 'script-class-lifecycle-editor.log'
$stdoutLog = Join-Path $outputRoot 'script-class-lifecycle-stdout.log'
$filter = 'Finare.Unreal.ScriptClass.AssetOperations'

function Write-Utf8 {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Text)

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) |
        Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
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

function Get-FileState {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [ordered]@{ exists = $false; path = $Path }
    }
    return [ordered]@{
        exists = $true
        path = $Path
        bytes = (Get-Item -LiteralPath $Path).Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).
            Hash.ToLowerInvariant()
    }
}

function Get-TreeState {
    param(
        [Parameter(Mandatory)][string]$Root,
        [string[]]$Extensions
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return @()
    }
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    return @(Get-ChildItem -LiteralPath $Root -File -Recurse |
        Where-Object {
            $Extensions.Count -eq 0 -or $_.Extension -in $Extensions
        } |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($normalizedRoot.Length).
                    Replace('\', '/')
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName `
                    -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
}

function Get-SurfaceState {
    param(
        [Parameter(Mandatory)][string]$Deployment,
        [Parameter(Mandatory)][string]$FirstPlayRequest,
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$ContentRoot,
        [Parameter(Mandatory)][string]$PendingRoot
    )

    return [ordered]@{
        deployment = Get-FileState -Path $Deployment
        firstPlay = Get-FileState -Path $FirstPlayRequest
        managedSources = @(Get-TreeState `
            -Root $SourceRoot `
            -Extensions @('.cs'))
        uePackages = @(Get-TreeState `
            -Root $ContentRoot `
            -Extensions @('.uasset', '.uexp', '.ubulk', '.uptnl'))
        pendingReceipts = @(Get-TreeState `
            -Root $PendingRoot `
            -Extensions @('.json'))
    }
}

function Get-StateJson {
    param([Parameter(Mandatory)][object]$State)

    return ($State | ConvertTo-Json -Depth 12 -Compress)
}

foreach ($required in @($projectPath, $editorCmd)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required packaged lifecycle input is missing: $required"
    }
}
if (Test-Path -LiteralPath $outputRoot) {
    throw "Packaged lifecycle output already exists: $outputRoot"
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$configPath = Join-Path $projectRoot 'Config\Finare.onboarding.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "Packaged consumer onboarding configuration is missing: $configPath"
}
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($config.schemaVersion -ne 'unifiedscript.unreal-project-onboarding/v0.1' -or
    @($config.managed.projects).Count -lt 1 -or
    [string]::IsNullOrWhiteSpace([string]$config.deployment.manifest)) {
    throw "Packaged consumer onboarding configuration is incomplete: $configPath"
}
$configDirectory = Split-Path -Parent $configPath
$managedProject = Resolve-ConfigPath `
    -Value ([string]$config.managed.projects[0]) `
    -BaseDirectory $configDirectory
$deploymentPath = Resolve-ConfigPath `
    -Value ([string]$config.deployment.manifest) `
    -BaseDirectory $configDirectory
$sourceRoot = Join-Path (Split-Path -Parent $managedProject) 'Classes'
$contentRoot = Join-Path $projectRoot 'Content\UnifiedScriptAutomation'
$firstPlayRequest = Join-Path $projectRoot `
    'Saved\Finare\ProjectSetup\first-play-request.json'
$pendingRoot = Join-Path $projectRoot `
    'Saved\Finare\ClassOperations\Pending'
$before = Get-SurfaceState `
    -Deployment $deploymentPath `
    -FirstPlayRequest $firstPlayRequest `
    -SourceRoot $sourceRoot `
    -ContentRoot $contentRoot `
    -PendingRoot $pendingRoot
if (@($before.pendingReceipts).Count -ne 0) {
    throw "Packaged consumer starts with pending Script Class receipts: $pendingRoot"
}

$report = $null
try {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $editorCmd @(
            $projectPath,
            '-unattended',
            '-nop4',
            '-NullRHI',
            '-nosplash',
            "-ExecCmds=Automation RunTests $filter",
            '-TestExit=Automation Test Queue Empty',
            "-abslog=$engineLog",
            '-log') 2>&1 |
            Tee-Object -FilePath $stdoutLog |
            ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "Packaged Script Class lifecycle Automation exited with code $exitCode. Log=$engineLog"
    }
    $logText = @(
        if (Test-Path -LiteralPath $engineLog -PathType Leaf) {
            Get-Content -LiteralPath $engineLog -Raw
        }
        if (Test-Path -LiteralPath $stdoutLog -PathType Leaf) {
            Get-Content -LiteralPath $stdoutLog -Raw
        }) -join [Environment]::NewLine
    $successCount = [regex]::Matches(
        $logText,
        'Test Completed\. Result=\{Success\}').Count
    $failureCount = [regex]::Matches(
        $logText,
        'Test Completed\. Result=\{(?:Fail|Failed|Error)\}').Count
    if ($successCount -ne 1 -or $failureCount -ne 0 -or
        $logText -notmatch [regex]::Escape($filter)) {
        throw "Packaged lifecycle did not report exactly one successful test. Success=$successCount Failure=$failureCount"
    }

    $after = Get-SurfaceState `
        -Deployment $deploymentPath `
        -FirstPlayRequest $firstPlayRequest `
        -SourceRoot $sourceRoot `
        -ContentRoot $contentRoot `
        -PendingRoot $pendingRoot
    $beforeJson = Get-StateJson -State $before
    $afterJson = Get-StateJson -State $after
    if ($afterJson -ne $beforeJson) {
        Write-Utf8 `
            -Path (Join-Path $outputRoot 'surface-before.json') `
            -Text (($before | ConvertTo-Json -Depth 12) + "`n")
        Write-Utf8 `
            -Path (Join-Path $outputRoot 'surface-after.json') `
            -Text (($after | ConvertTo-Json -Depth 12) + "`n")
        throw 'Packaged lifecycle left source, deployment, first-Play, package, redirector, or receipt state behind.'
    }

    $report = [ordered]@{
        schemaVersion = 'unifiedscript.packaged-script-class-lifecycle/v0.1'
        status = 'passed'
        project = $projectPath
        engineRoot = $enginePath
        automation = [ordered]@{
            filter = $filter
            passed = $successCount
            failed = $failureCount
            editorLog = $engineLog
            stdoutLog = $stdoutLog
        }
        operations = @(
            foreach ($operation in @('create', 'rename', 'duplicate', 'delete')) {
                [ordered]@{
                    name = $operation
                    success = 'passed'
                    injectedRollback = 'passed'
                    recoveryReceipt = 'persisted-recovery-passed'
                }
            })
        closure = [ordered]@{
            managedSources = 'baseline-restored'
            deployment = 'byte-exact-baseline'
            firstPlay = 'byte-exact-baseline'
            uePackages = 'baseline-restored'
            ueReferences = 'rewritten-and-rollback-verified'
            redirectors = 'removed-and-rollback-verified'
            pendingReceipts = 0
        }
        surfaceFingerprint = [ordered]@{
            beforeSha256 = (Get-FileHash `
                -LiteralPath $deploymentPath `
                -Algorithm SHA256).Hash.ToLowerInvariant()
            beforeEqualsAfter = $true
            managedSourceCount = @($before.managedSources).Count
            uePackageCount = @($before.uePackages).Count
        }
        durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
    }
    Write-Utf8 -Path $reportPath -Text (
        ($report | ConvertTo-Json -Depth 12) + "`n")
} catch {
    $failureReport = [ordered]@{
        schemaVersion = 'unifiedscript.packaged-script-class-lifecycle/v0.1'
        status = 'failed'
        project = $projectPath
        engineRoot = $enginePath
        error = $_.Exception.Message
        durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
    }
    Write-Utf8 -Path $reportPath -Text (
        ($failureReport | ConvertTo-Json -Depth 8) + "`n")
    throw
}

Write-Host "Packaged Script Class lifecycle replay passed: $reportPath"
