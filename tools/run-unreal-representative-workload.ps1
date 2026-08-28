[CmdletBinding()]
param(
    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,
    [Parameter(Mandatory)]
    [ValidateSet(
        'struct-container-transfer',
        'delegate-storm',
        'per-frame-actor-logic',
        'mass-entities')]
    [string]$WorkloadId,
    [string]$OutputPath,
    [string]$SuiteMetricsPath,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'HostProject\UEUnifiedScriptHost.uproject'
$managedProject = Join-Path $repoRoot `
    'samples\Unreal\UnifiedScript.Unreal.Demo\UnifiedScript.Unreal.Demo.csproj'
$manifestPath = Join-Path $repoRoot 'benchmarks\unreal\workload-suite.v0.1.json'
$artifactRoot = Join-Path $repoRoot 'artifacts\ue\representative'
$logPath = Join-Path $artifactRoot "$WorkloadId.log"
$rawResultPath = Join-Path $artifactRoot "$WorkloadId-raw.json"
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $artifactRoot "$WorkloadId.json"
}

if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = @('D:\UE_5.8', 'D:\UE\_5.8') |
        Where-Object {
            Test-Path (Join-Path $_ 'Engine\Build\BatchFiles\Build.bat')
        } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'Pass -EngineRoot or set UNIFIEDSCRIPT_UE_ROOT.'
}

$buildBat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\Build.bat'
$editorCmd = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
$dotnetExe = Join-Path $EngineRoot `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
foreach ($required in @(
        $projectPath, $managedProject, $manifestPath,
        $buildBat, $editorCmd, $dotnetExe)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required representative workload input is missing: $required"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$workload = @($manifest.workloads) |
    Where-Object id -eq $WorkloadId |
    Select-Object -First 1
if ($null -eq $workload) {
    throw "The workload suite does not contain '$WorkloadId'."
}
$contracts = @{
    'struct-container-transfer' = [pscustomobject]@{
        Calls = 49152; Bytes = 3276800; Checksum = 2424832
    }
    'delegate-storm' = [pscustomobject]@{
        Calls = 200000; Bytes = 1700000; Checksum = 5000350000
    }
    'per-frame-actor-logic' = [pscustomobject]@{
        Calls = 20000; Bytes = 440000; Checksum = 100015000
    }
    'mass-entities' = [pscustomobject]@{
        Calls = 1001000; Bytes = 72004000; Checksum = 1501500000
    }
}
$contract = $contracts[$WorkloadId]
$isFrameWorkload = $WorkloadId -in @(
    'per-frame-actor-logic',
    'mass-entities')

New-Item -ItemType Directory -Force -Path `
    $artifactRoot, (Split-Path -Parent $OutputPath) | Out-Null
Remove-Item -LiteralPath $rawResultPath, $logPath `
    -Force -ErrorAction SilentlyContinue
if (-not $SkipBuild) {
    & $dotnetExe build $managedProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Managed representative workload build failed: $LASTEXITCODE."
    }
    & $buildBat UEUnifiedScriptHostEditor Win64 Development $projectPath `
        -WaitMutex -NoHotReloadFromIDE -NoUBA -MaxParallelActions=4
    if ($LASTEXITCODE -ne 0) {
        throw "Native representative workload build failed: $LASTEXITCODE."
    }
}

$defaultEnginePath = Join-Path $repoRoot 'HostProject\Config\DefaultEngine.ini'
$defaultInputPath = Join-Path $repoRoot 'HostProject\Config\DefaultInput.ini'
$defaultEngineBytes = if (Test-Path -LiteralPath $defaultEnginePath) {
    [IO.File]::ReadAllBytes($defaultEnginePath)
} else { $null }
$defaultInputBytes = if (Test-Path -LiteralPath $defaultInputPath) {
    [IO.File]::ReadAllBytes($defaultInputPath)
} else { $null }
try {
    if ($isFrameWorkload) {
        & $editorCmd $projectPath `
            -unattended -nop4 -NullRHI -nosplash `
            -UseFixedTimeStep -FixedSeed `
            -UnifiedScriptFrameWorkload `
            "-UnifiedScriptRepresentativeWorkload=$WorkloadId" `
            "-UnifiedScriptRepresentativeResult=$rawResultPath" `
            "-abslog=$logPath" -log
    } else {
        & $editorCmd $projectPath `
            -unattended -nop4 -NullRHI -nosplash `
            "-UnifiedScriptRepresentativeWorkload=$WorkloadId" `
            '-ExecCmds=Automation RunTests UnifiedScript.Unreal.Performance.RepresentativeWorkload' `
            '-TestExit=Automation Test Queue Empty' `
            "-abslog=$logPath" -log
    }
    $editorExitCode = $LASTEXITCODE
} finally {
    if ($null -ne $defaultEngineBytes) {
        [IO.File]::WriteAllBytes($defaultEnginePath, $defaultEngineBytes)
    }
    if ($null -ne $defaultInputBytes) {
        [IO.File]::WriteAllBytes($defaultInputPath, $defaultInputBytes)
    }
}
if ($editorExitCode -ne 0) {
    throw "UnifiedScript '$WorkloadId' process failed: $editorExitCode."
}

$result = if ($isFrameWorkload) {
    if (-not (Test-Path -LiteralPath $rawResultPath -PathType Leaf)) {
        throw "UnifiedScript '$WorkloadId' raw result is missing. Log: $logPath"
    }
    Get-Content -LiteralPath $rawResultPath -Raw | ConvertFrom-Json
} else {
    $successPattern =
        'Test Completed. Result={Success} Name={RepresentativeWorkload} Path={UnifiedScript.Unreal.Performance.RepresentativeWorkload}'
    if (-not (Select-String -LiteralPath $logPath `
            -SimpleMatch $successPattern -Quiet)) {
        throw "UnifiedScript '$WorkloadId' Automation failed. Log: $logPath"
    }
    $marker = 'UNIFIEDSCRIPT_REPRESENTATIVE_WORKLOAD_JSON '
    $line = Select-String -LiteralPath $logPath -SimpleMatch $marker |
        Select-Object -Last 1
    if ($null -eq $line) {
        throw "UnifiedScript '$WorkloadId' result marker is missing."
    }
    $line.Line.Substring(
        $line.Line.IndexOf($marker) + $marker.Length) | ConvertFrom-Json
}
$frameMetricsValid = -not $isFrameWorkload -or (
    [double]$result.frameTimeMedianMilliseconds -gt 0 -and
    [double]$result.frameTimeP95Milliseconds -ge
        [double]$result.frameTimeMedianMilliseconds -and
    [double]$result.frameTimeP99Milliseconds -ge
        [double]$result.frameTimeP95Milliseconds)
if ($result.schemaVersion -ne 'unifiedscript.representative-workload/v0.1' -or
    $result.workloadId -ne $WorkloadId -or
    [int]$result.warmupIterations -ne [int]$workload.warmupIterations -or
    [int]$result.iterations -ne [int]$workload.measuredIterations -or
    [double]$result.bridgeTimeMilliseconds -le 0 -or
    [int64]$result.calls -ne $contract.Calls -or
    [int64]$result.bytesCopied -ne $contract.Bytes -or
    [int64]$result.allocatedBytes -lt 0 -or
    [int]$result.gcCollectionCount -lt 0 -or
    [double]$result.gcPauseMilliseconds -lt 0 -or
    [uint64]$result.memoryHighWaterBytes -le 0 -or
    [int64]$result.checksum -ne $contract.Checksum -or
    -not $frameMetricsValid) {
    throw "UnifiedScript '$WorkloadId' evidence violates its frozen contract."
}

$report = [ordered]@{
    schemaVersion = 'unifiedscript.representative-workload-report/v0.1'
    engineId = 'unifiedScript'
    status = 'passed'
    workloadId = $WorkloadId
    result = $result
    logPath = [IO.Path]::GetFullPath($logPath)
}
$report | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $OutputPath -Encoding utf8

if (-not [string]::IsNullOrWhiteSpace($SuiteMetricsPath)) {
    $metrics = [ordered]@{
        values = [ordered]@{
            frameTimeMedianMilliseconds = if ($isFrameWorkload) {
                [double]$result.frameTimeMedianMilliseconds
            } else { $null }
            frameTimeP95Milliseconds = if ($isFrameWorkload) {
                [double]$result.frameTimeP95Milliseconds
            } else { $null }
            frameTimeP99Milliseconds = if ($isFrameWorkload) {
                [double]$result.frameTimeP99Milliseconds
            } else { $null }
            bridgeTimeMilliseconds = [double]$result.bridgeTimeMilliseconds
            calls = [int64]$result.calls
            bytesCopied = [int64]$result.bytesCopied
            allocatedBytes = [int64]$result.allocatedBytes
            gcCollectionCount = [int]$result.gcCollectionCount
            gcPauseMilliseconds = [double]$result.gcPauseMilliseconds
            memoryHighWaterBytes = [uint64]$result.memoryHighWaterBytes
            startupMilliseconds = if ($isFrameWorkload) {
                [double]$result.startupMilliseconds
            } else { $null }
            reloadMilliseconds = $null
            packageSizeBytes = $null
        }
    }
    $metricsDirectory = Split-Path -Parent `
        ([IO.Path]::GetFullPath($SuiteMetricsPath))
    if (-not [string]::IsNullOrWhiteSpace($metricsDirectory)) {
        New-Item -ItemType Directory -Force -Path $metricsDirectory | Out-Null
    }
    $metrics | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $SuiteMetricsPath -Encoding utf8
}

Write-Output (
    "UnifiedScript '$WorkloadId': $($result.bridgeTimeMilliseconds) ms, " +
    "$($result.calls) calls, $($result.bytesCopied) logical bytes")
Write-Output "Result: $OutputPath"
