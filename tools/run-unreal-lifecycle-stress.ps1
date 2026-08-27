[CmdletBinding()]
param(
    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'HostProject\UEUnifiedScriptHost.uproject'
$managedProject = Join-Path $repoRoot 'samples\Unreal\UnifiedScript.Unreal.Demo\UnifiedScript.Unreal.Demo.csproj'
$artifactDirectory = Join-Path $repoRoot 'artifacts\ue'
$logPath = Join-Path $artifactDirectory 'lifecycle-stress.log'
$reportPath = Join-Path $artifactDirectory 'lifecycle-stress.json'

if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = @('D:\UE_5.8', 'D:\UE\_5.8') |
        Where-Object { Test-Path (Join-Path $_ 'Engine\Build\BatchFiles\Build.bat') } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'Pass -EngineRoot or set UNIFIEDSCRIPT_UE_ROOT.'
}

$buildBat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\Build.bat'
$editorCmd = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
$bundledDotNet = Join-Path $EngineRoot 'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $bundledDotNet) { $bundledDotNet } else { 'dotnet' }
foreach ($requiredPath in @($buildBat, $editorCmd, $projectPath, $managedProject)) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Required lifecycle-stress dependency is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
if (!$SkipBuild) {
    & $dotnetExe build $managedProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Managed lifecycle-stress build failed with exit code $LASTEXITCODE."
    }
    & $buildBat UEUnifiedScriptHostEditor Win64 Development $projectPath `
        -WaitMutex -NoHotReloadFromIDE -NoUBA -MaxParallelActions=4
    if ($LASTEXITCODE -ne 0) {
        throw "Unreal lifecycle-stress build failed with exit code $LASTEXITCODE."
    }
}

$defaultEnginePath = Join-Path $repoRoot 'HostProject\Config\DefaultEngine.ini'
$defaultInputPath = Join-Path $repoRoot 'HostProject\Config\DefaultInput.ini'
$defaultEngineExisted = Test-Path -LiteralPath $defaultEnginePath
$defaultInputExisted = Test-Path -LiteralPath $defaultInputPath
$defaultEngineBytes = if ($defaultEngineExisted) {
    [IO.File]::ReadAllBytes($defaultEnginePath)
} else { $null }
$defaultInputBytes = if ($defaultInputExisted) {
    [IO.File]::ReadAllBytes($defaultInputPath)
} else { $null }

try {
    & $editorCmd $projectPath `
        -unattended -nop4 -NullRHI -nosplash `
        '-ExecCmds=Automation RunTests UnifiedScript.Unreal.WorldHost.LifecycleStress' `
        '-TestExit=Automation Test Queue Empty' `
        "-abslog=$logPath" -log
    $editorExitCode = $LASTEXITCODE
} finally {
    if ($defaultEngineExisted) {
        [IO.File]::WriteAllBytes($defaultEnginePath, $defaultEngineBytes)
    } elseif (Test-Path -LiteralPath $defaultEnginePath) {
        Remove-Item -LiteralPath $defaultEnginePath
    }
    if ($defaultInputExisted) {
        [IO.File]::WriteAllBytes($defaultInputPath, $defaultInputBytes)
    } elseif (Test-Path -LiteralPath $defaultInputPath) {
        Remove-Item -LiteralPath $defaultInputPath
    }
}

if ($editorExitCode -ne 0) {
    throw "Unreal lifecycle-stress process failed with exit code $editorExitCode."
}
$successPattern =
    'Test Completed. Result={Success} Name={LifecycleStress} Path={UnifiedScript.Unreal.WorldHost.LifecycleStress}'
if (!(Select-String -LiteralPath $logPath -SimpleMatch $successPattern -Quiet)) {
    throw "LifecycleStress Automation did not succeed. Log: $logPath"
}

$marker = 'UNIFIEDSCRIPT_LIFECYCLE_STRESS_JSON '
$line = Select-String -LiteralPath $logPath -SimpleMatch $marker |
    Select-Object -Last 1
if ($null -eq $line) {
    throw "Lifecycle stress JSON marker is missing. Log: $logPath"
}
$result = $line.Line.Substring($line.Line.IndexOf($marker) + $marker.Length) |
    ConvertFrom-Json

$matrixPass =
    $result.schemaVersion -eq 'unifiedscript.unreal-lifecycle-stress/v0.1' -and
    [int]$result.travelCycles -ge 8 -and
    [int]$result.pieCycles -ge 8 -and
    [int]$result.concurrentWorlds -ge 4 -and
    [int]$result.collidingRequestsPerWorld -ge 8 -and
    [int]$result.generationRollovers -ge 16
$zeroLeakPass =
    [int]$result.staleCompletions -eq 0 -and
    [int]$result.pendingRequestsDelta -eq 0 -and
    [int]$result.managedWorldsDelta -eq 0 -and
    [int]$result.nativeContextsDelta -eq 0 -and
    [int]$result.ownedResourcesDelta -eq 0 -and
    [int]$result.retiredLoadContextsAlive -eq 0

$report = [ordered]@{
    schemaVersion = 'unifiedscript.unreal-lifecycle-stress-report/v0.1'
    status = if ($matrixPass -and $zeroLeakPass) { 'passed' } else { 'failed' }
    engineRoot = [IO.Path]::GetFullPath($EngineRoot)
    test = 'UnifiedScript.Unreal.WorldHost.LifecycleStress'
    result = $result
    gates = [ordered]@{
        completeMatrix = $matrixPass
        zeroLeak = $zeroLeakPass
    }
    logPath = [IO.Path]::GetFullPath($logPath)
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8

if (!$matrixPass -or !$zeroLeakPass) {
    throw "One or more lifecycle stress gates failed. Report: $reportPath"
}

Write-Host "Lifecycle stress: PASS ($reportPath)"
Write-Host (
    "Travel=$($result.travelCycles), PIE=$($result.pieCycles), " +
    "Worlds=$($result.concurrentWorlds), Generations=$($result.generationRollovers), " +
    "RetiredALC=$($result.retiredLoadContextsAlive)")
