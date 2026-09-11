[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PilotRoot,
    [Parameter(Mandatory)][string]$EngineRoot,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$pilotPath = [IO.Path]::GetFullPath($PilotRoot).TrimEnd('\', '/')
if ($pilotPath -eq [IO.Path]::GetFullPath('D:/Project/LyraStarterGame')) {
    throw 'Use an independent Lyra copy, not the original reference project.'
}
$projectPath = Join-Path $pilotPath 'LyraStarterGame.uproject'
$runUat = Join-Path $EngineRoot 'Engine/Build/BatchFiles/RunUAT.bat'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($inputPath in @($projectPath, $runUat)) {
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
        throw "Required input is missing: $inputPath"
    }
}
if (Test-Path -LiteralPath $outputRoot) {
    throw "Use a new package output directory: $outputRoot"
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$report = [ordered]@{
    status = 'running'
    project = $projectPath
    configuration = 'Development'
    map = '/ShooterMaps/Maps/L_Expanse'
    archiveDirectory = Join-Path $outputRoot 'Archive'
    runtimeVerified = $false
}
$reportPath = Join-Path $outputRoot 'package-report.json'
try {
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
    & $runUat BuildCookRun "-project=$projectPath" -target=LyraGame `
        -noP4 -platform=Win64 -clientconfig=Development `
        -build -cook -stage -pak -iostore -archive `
        '-map=/ShooterMaps/Maps/L_Expanse' `
        "-archivedirectory=$($report.archiveDirectory)" `
        '-AdditionalCookerOptions=-SkipZenStore' `
        -unattended -utf8output -NoCodeSign -NoXGE *> (Join-Path $outputRoot 'build-cook-stage.log')
    if ($LASTEXITCODE -ne 0) {
        throw "Lyra Build/Cook/Stage/Pak/Archive failed with exit code $LASTEXITCODE."
    }
    $windowsRoot = Join-Path $report.archiveDirectory 'Windows'
    $gameRoot = Join-Path $windowsRoot 'LyraStarterGame'
    $binariesRoot = Join-Path $gameRoot 'Binaries/Win64'
    $gameExe = @(Get-ChildItem -LiteralPath $binariesRoot -File -Filter 'LyraGame*.exe')
    if ($gameExe.Count -ne 1) {
        throw "Expected one LyraGame executable, found $($gameExe.Count)."
    }
    $pluginRoot = Join-Path $gameRoot 'Plugins/Finare'
    $managedRoot = Join-Path $pluginRoot 'Binaries/Managed'
    $chunkPath = Join-Path $managedRoot 'Scripts/LyraStarterGame.Finare.uschunk'
    $requiredFiles = @(
        $chunkPath,
        (Join-Path $managedRoot 'Finare.Unreal.dll'),
        (Join-Path $managedRoot 'Finare.Runtime.dll'),
        (Join-Path $managedRoot 'Finare.Core.dll'),
        (Join-Path $managedRoot 'Finare.Unreal.deps.json'),
        (Join-Path $pluginRoot 'Resources/Managed/Finare.Unreal.runtimeconfig.json')
    )
    foreach ($requiredPath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Packaged managed dependency is missing: $requiredPath"
        }
    }
    $runtimeRoot = Join-Path $pluginRoot 'Binaries/ThirdParty/DotNet/10.0/win-x64'
    foreach ($runtimeFile in @('hostfxr.dll', 'coreclr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
        if (-not (Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File -Filter $runtimeFile | Select-Object -First 1)) {
            throw "Packaged CoreCLR dependency is missing: $runtimeFile"
        }
    }
    $installedChunk = Join-Path $pilotPath 'Plugins/Finare/Binaries/Managed/Scripts/LyraStarterGame.Finare.uschunk'
    $report.chunkSha256 = (Get-FileHash -LiteralPath $chunkPath -Algorithm SHA256).Hash
    if ($report.chunkSha256 -ne (Get-FileHash -LiteralPath $installedChunk -Algorithm SHA256).Hash) {
        throw 'The packaged chunk differs from the installed gameplay chunk.'
    }
    $relativeExe = $gameExe[0].FullName.Substring($windowsRoot.Length + 1)
    $launcher = "@echo off`r`ncd /d `"%~dp0`"`r`nstart `"`" `"$relativeExe`" `"/ShooterMaps/Maps/L_Expanse?NumBots=0`" -log`r`n"
    [IO.File]::WriteAllText((Join-Path $windowsRoot 'StartHealthPilot.cmd'), $launcher, [Text.Encoding]::ASCII)
    $report.executable = $gameExe[0].FullName
    $report.status = 'passed'
    Write-Output "Lyra package: PASS ($reportPath); gameplay runtime validation is separate."
} catch {
    $report.status = 'failed'
    $report.error = $_.Exception.Message
    throw
} finally {
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
}
