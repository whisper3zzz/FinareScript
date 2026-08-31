[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Project,

    [Parameter(Mandatory)]
    [string]$EngineRoot,

    [Parameter(Mandatory)]
    [ValidateRange(1, 2147483647)]
    [int]$WaitForProcessId,

    [string]$Report
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$setupScript = Join-Path $PSScriptRoot 'setup-unreal-project.ps1'
$projectPath = [IO.Path]::GetFullPath($Project)
$enginePath = [IO.Path]::GetFullPath($EngineRoot)
if (-not (Test-Path -LiteralPath $setupScript -PathType Leaf)) {
    throw "Project-local onboarding script is missing: $setupScript"
}
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Unreal project is missing: $projectPath"
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
exit $LASTEXITCODE
