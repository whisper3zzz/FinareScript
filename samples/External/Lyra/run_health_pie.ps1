[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PilotRoot,
    [Parameter(Mandatory)][string]$EngineRoot,
    [string]$ReportDirectory
)

$ErrorActionPreference = 'Stop'
$pilotPath = [IO.Path]::GetFullPath($PilotRoot).TrimEnd('\', '/')
if ($pilotPath -eq [IO.Path]::GetFullPath('D:/Project/LyraStarterGame')) {
    throw 'Use an independent Lyra copy, not the original reference project.'
}
$projectPath = Join-Path $pilotPath 'LyraStarterGame.uproject'
$editorPath = Join-Path $EngineRoot 'Engine/Binaries/Win64/UnrealEditor.exe'
$scriptPath = Join-Path $PSScriptRoot 'verify_health_pie.py'
foreach ($requiredPath in @($projectPath, $editorPath, $scriptPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required input is missing: $requiredPath"
    }
}
if (-not $ReportDirectory) {
    $ReportDirectory = Join-Path $pilotPath ('Saved/Finare/LyraPilot/Runs/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$reportRoot = [IO.Path]::GetFullPath($ReportDirectory)
if (Test-Path -LiteralPath $reportRoot) {
    throw "Use a new report directory: $reportRoot"
}
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$resultPath = Join-Path $pilotPath 'Saved/Finare/LyraPilot/health-pie.json'
$oldPilotRoot = $env:FINARE_LYRA_PILOT_ROOT
try {
    $env:FINARE_LYRA_PILOT_ROOT = $pilotPath
    $startedAt = [DateTime]::UtcNow
    $editorArguments = @(
        ('"' + $projectPath + '"'),
        '-unattended', '-nop4', '-RenderOffscreen', '-nosplash', '-NoSound',
        ('-ExecutePythonScript="' + $scriptPath + '"'),
        ('-abslog="' + (Join-Path $reportRoot 'engine.log') + '"'),
        '-log'
    )
    $editorProcess = Start-Process -FilePath $editorPath -ArgumentList $editorArguments `
        -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $reportRoot 'stdout.log') `
        -RedirectStandardError (Join-Path $reportRoot 'stderr.log')
    Write-Output "Health PIE started: PID $($editorProcess.Id)"
    if (-not $editorProcess.WaitForExit(600000)) {
        Stop-Process -Id $editorProcess.Id
        throw 'Health PIE exceeded the ten minute process timeout.'
    }
    if (-not (Test-Path -LiteralPath $resultPath) -or
        (Get-Item -LiteralPath $resultPath).LastWriteTimeUtc -lt $startedAt) {
        throw "Editor produced no fresh gameplay report (exit $($editorProcess.ExitCode))."
    }
    Copy-Item -LiteralPath $resultPath -Destination (Join-Path $reportRoot 'health-pie.json')
    $report = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($editorProcess.ExitCode -ne 0 -or $report.status -ne 'passed') {
        throw "Health PIE failed (exit $($editorProcess.ExitCode)): $($report.error)"
    }
    $engineFailures = @(Select-String -LiteralPath (Join-Path $reportRoot 'engine.log') `
        -Pattern 'Ensure condition failed:', 'Fatal error:', 'not cleaned up by GC!')
    if ($engineFailures.Count -gt 0) {
        throw "Health PIE reported engine failure despite its gameplay result: $($engineFailures[0].Line)"
    }
    Write-Output "Health PIE: PASS ($reportRoot)"
} finally {
    $env:FINARE_LYRA_PILOT_ROOT = $oldPilotRoot
}
