[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Project,

    [Parameter(Mandatory)]
    [string]$EngineRoot,

    [Parameter(Mandatory)]
    [ValidateRange(1, 2147483647)]
    [int]$WaitForProcessId,

    [string]$Report,

    [string]$FirstPlayRequest,

    [switch]$RelaunchEditor
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$setupScript = Join-Path $PSScriptRoot 'setup-unreal-project.ps1'
$projectPath = [IO.Path]::GetFullPath($Project)
$enginePath = [IO.Path]::GetFullPath($EngineRoot)
$projectRoot = Split-Path -Parent $projectPath
if (-not (Test-Path -LiteralPath $setupScript -PathType Leaf)) {
    throw "Project-local onboarding script is missing: $setupScript"
}
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Unreal project is missing: $projectPath"
}

function Write-FirstPlayResult {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][int]$ExitCode,
        [string]$OnboardingReport
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or
        -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    $fullPath = [IO.Path]::GetFullPath($Path)
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot `
        'Saved\UnifiedScript\ProjectSetup')).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith(
            $allowedRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "First-Play request escapes the project setup directory: $fullPath"
    }

    $request = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    if ($request.schemaVersion -ne 'unifiedscript.first-play-request/v0.1' -or
        [string]::IsNullOrWhiteSpace([string]$request.requestId) -or
        [string]::IsNullOrWhiteSpace([string]$request.blueprintAssetPath)) {
        throw "First-Play request is invalid: $fullPath"
    }
    $request.status = if ($ExitCode -eq 0) { 'ready' } else { 'failed' }
    $request | Add-Member -NotePropertyName exitCode `
        -NotePropertyValue $ExitCode -Force
    $request | Add-Member -NotePropertyName completedAtUtc `
        -NotePropertyValue ([DateTime]::UtcNow.ToString('O')) -Force
    if (-not [string]::IsNullOrWhiteSpace($OnboardingReport)) {
        $request | Add-Member -NotePropertyName onboardingReport `
            -NotePropertyValue ([IO.Path]::GetFullPath($OnboardingReport)) -Force
    }

    $temporaryPath = "$fullPath.tmp"
    [IO.File]::WriteAllText(
        $temporaryPath,
        (($request | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $fullPath -Force
    return $true
}

$process = Get-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue
if ($null -ne $process) {
    Write-Host "Waiting for Unreal Editor process $WaitForProcessId to exit..."
    $process.WaitForExit()
}

$arguments = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $setupScript,
    '-Project', $projectPath,
    '-EngineRoot', $enginePath)
if (-not [string]::IsNullOrWhiteSpace($Report)) {
    $arguments += @('-Report', [IO.Path]::GetFullPath($Report))
}
& powershell.exe @arguments
$setupExitCode = $LASTEXITCODE
if ($null -eq $setupExitCode) {
    $setupExitCode = 1
}

$hasFirstPlayRequest = $false
if (-not [string]::IsNullOrWhiteSpace($FirstPlayRequest)) {
    $hasFirstPlayRequest = Write-FirstPlayResult `
        -Path $FirstPlayRequest `
        -ExitCode $setupExitCode `
        -OnboardingReport $Report
}

if ($RelaunchEditor) {
    $editorExecutable = Join-Path $enginePath `
        'Engine\Binaries\Win64\UnrealEditor.exe'
    if (-not (Test-Path -LiteralPath $editorExecutable -PathType Leaf)) {
        throw "Unreal Editor is missing for first-Play relaunch: $editorExecutable"
    }
    $relaunchArguments = @("`"$projectPath`"")
    if ($hasFirstPlayRequest) {
        $relaunchArguments += '-UnifiedScriptFirstPlay'
    }
    $relaunchProcess = Start-Process `
        -FilePath $editorExecutable `
        -ArgumentList ($relaunchArguments -join ' ') `
        -WorkingDirectory $projectRoot `
        -PassThru
    Write-Host "Relaunched Unreal Editor process $($relaunchProcess.Id)."
}

exit $setupExitCode
