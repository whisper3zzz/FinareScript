[CmdletBinding()]
param(
    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,
    [string]$SuiteMetricsPath,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'HostProject\UEUnifiedScriptHost.uproject'
$managedProject = Join-Path $repoRoot 'samples\Unreal\UnifiedScript.Unreal.Demo\UnifiedScript.Unreal.Demo.csproj'
$artifactDirectory = Join-Path $repoRoot 'artifacts\ue'
$logPath = Join-Path $artifactDirectory 'data-plane-profile.log'
$reportPath = Join-Path $artifactDirectory 'data-plane-profile.json'

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
$dotnetExe = Join-Path $EngineRoot 'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
foreach ($requiredPath in @(
        $buildBat,
        $editorCmd,
        $dotnetExe,
        $projectPath,
        $managedProject)) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Required data-plane profile dependency is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
if (!$SkipBuild) {
    & $dotnetExe build $managedProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Managed data-plane profile build failed with exit code $LASTEXITCODE."
    }
    & $buildBat UEUnifiedScriptHostEditor Win64 Development $projectPath `
        -WaitMutex -NoHotReloadFromIDE -NoUBA -MaxParallelActions=4
    if ($LASTEXITCODE -ne 0) {
        throw "Unreal data-plane profile build failed with exit code $LASTEXITCODE."
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
    $previousPreference = $ErrorActionPreference
    try {
        # Automation can intentionally write diagnostics to native stderr.
        # Windows PowerShell must use the process exit code and report gates as
        # the verdict instead of promoting those lines to terminating errors.
        $ErrorActionPreference = 'Continue'
        & $editorCmd $projectPath `
            -unattended -nop4 -NullRHI -nosplash `
            '-ExecCmds=Automation RunTests UnifiedScript.Unreal.Performance.DataPlaneWorkloadProfile' `
            '-TestExit=Automation Test Queue Empty' `
            "-abslog=$logPath" -log
        $editorExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
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
    throw "Unreal data-plane profile process failed with exit code $editorExitCode."
}
$successPattern =
    'Test Completed. Result={Success} Name={DataPlaneWorkloadProfile} Path={UnifiedScript.Unreal.Performance.DataPlaneWorkloadProfile}'
if (!(Select-String -LiteralPath $logPath -SimpleMatch $successPattern -Quiet)) {
    throw "DataPlaneWorkloadProfile Automation did not succeed. Log: $logPath"
}

$marker = 'UNIFIEDSCRIPT_DATA_PLANE_PROFILE_JSON '
$line = Select-String -LiteralPath $logPath -SimpleMatch $marker |
    Select-Object -Last 1
if ($null -eq $line) {
    throw "Data-plane profile JSON marker is missing. Log: $logPath"
}
$result = $line.Line.Substring($line.Line.IndexOf($marker) + $marker.Length) |
    ConvertFrom-Json
$movementRatio = [double]$result.commandSubmitNs / [double]$result.bindingScalarNs
$queryRatio = [double]$result.batchTraceNs / [double]$result.bindingTraceNs
$containerRatio = [double]$result.generatedSnapshotNs / [double]$result.bindingContainerNs
$schemaPass =
    $result.schemaVersion -eq 'unifiedscript.unreal-data-plane-profile/v0.3' -and
    [int]$result.iterations -ge 1024 -and
    [int]$result.sampleCount -ge 3
$measurementPass =
    [double]$result.bindingScalarNs -gt 0 -and
    [double]$result.commandSubmitNs -gt 0 -and
    [double]$result.bindingTraceNs -gt 0 -and
    [double]$result.batchTraceNs -gt 0 -and
    [double]$result.attributeReadNs -gt 0 -and
    [double]$result.bindingContainerNs -gt 0 -and
    [double]$result.generatedSnapshotNs -gt 0 -and
    [double]$result.generatedEventNs -gt 0 -and
    $movementRatio -lt 1.0 -and
    $queryRatio -lt 1.0 -and
    $containerRatio -lt 1.0 -and
    [long]$result.dataPlaneAllocatedBytes -eq 0 -and
    [long]$result.generatedDataAllocatedBytes -eq 0 -and
    [int]$result.generatedSnapshotEntries -eq [int]$result.iterations -and
    [int]$result.generatedEventEntries -eq [int]$result.iterations -and
    [int]$result.extensionSnapshotEntries -eq [int]$result.iterations -and
    [int]$result.extensionEventEntries -eq [int]$result.iterations -and
    $result.extensionFingerprint -eq '0xDE4BA9E28216D610' -and
    [long]$result.batchTraceAllocatedBytes -eq 0 -and
    [long]$result.batchTraceBytes -gt 0 -and
    [int]$result.batchTraceGcCollectionCount -ge 0 -and
    [double]$result.batchTraceGcPauseMilliseconds -ge 0 -and
    [uint64]$result.memoryHighWaterBytes -gt 0
$routingPass =
    $result.movement -eq 'data-plane' -and
    $result.overlap -eq 'data-plane' -and
    $result.attribute -eq 'binding' -and
    $result.spawn -eq 'hybrid' -and
    $result.query -eq 'data-plane' -and
    $result.container -eq 'generated-snapshot' -and
    $result.delegate -eq 'generated-events'

$report = [ordered]@{
    schemaVersion = 'unifiedscript.unreal-data-plane-profile-report/v0.3'
    status = if ($schemaPass -and $measurementPass -and $routingPass) {
        'passed'
    } else { 'failed' }
    engineRoot = [IO.Path]::GetFullPath($EngineRoot)
    test = 'UnifiedScript.Unreal.Performance.DataPlaneWorkloadProfile'
    result = $result
    ratios = [ordered]@{
        commandSubmitToScalarBinding = $movementRatio
        batchTraceToPerCallBinding = $queryRatio
        generatedSnapshotToContainerBinding = $containerRatio
    }
    gates = [ordered]@{
        matchedSchema = $schemaPass
        measuredAdvantage = $measurementPass
        zeroManagedAllocation = [long]$result.dataPlaneAllocatedBytes -eq 0
        generatedChannelsZeroManagedAllocation =
            [long]$result.generatedDataAllocatedBytes -eq 0
        generatedExtensionCompatible =
            [int]$result.extensionSnapshotEntries -eq [int]$result.iterations -and
            [int]$result.extensionEventEntries -eq [int]$result.iterations -and
            $result.extensionFingerprint -eq '0xDE4BA9E28216D610'
        evidenceBackedRouting = $routingPass
    }
    logPath = [IO.Path]::GetFullPath($logPath)
}
$report | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $reportPath -Encoding utf8

if (![string]::IsNullOrWhiteSpace($SuiteMetricsPath)) {
    $suiteMetrics = [ordered]@{
        values = [ordered]@{
            frameTimeMedianMilliseconds = $null
            frameTimeP95Milliseconds = $null
            frameTimeP99Milliseconds = $null
            bridgeTimeMilliseconds =
                [double]$result.batchTraceNs * [int]$result.iterations / 1000000.0
            calls = 2
            bytesCopied = [int64]$result.batchTraceBytes
            allocatedBytes = [int64]$result.batchTraceAllocatedBytes
            gcCollectionCount = [int]$result.batchTraceGcCollectionCount
            gcPauseMilliseconds =
                [double]$result.batchTraceGcPauseMilliseconds
            memoryHighWaterBytes = [uint64]$result.memoryHighWaterBytes
            startupMilliseconds = $null
            reloadMilliseconds = $null
            packageSizeBytes = $null
        }
    }
    $suiteMetricsDirectory = Split-Path -Parent `
        ([IO.Path]::GetFullPath($SuiteMetricsPath))
    if (-not [string]::IsNullOrWhiteSpace($suiteMetricsDirectory)) {
        New-Item -ItemType Directory -Force -Path $suiteMetricsDirectory | Out-Null
    }
    $suiteMetrics | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $SuiteMetricsPath -Encoding utf8
}

if (!$schemaPass -or !$measurementPass -or !$routingPass) {
    throw "One or more data-plane profile gates failed. Report: $reportPath"
}

Write-Host "Data-plane workload profile: PASS ($reportPath)"
Write-Host (
    "Movement ratio=$([Math]::Round($movementRatio, 3)); " +
    "Trace ratio=$([Math]::Round($queryRatio, 3)); " +
    "Container ratio=$([Math]::Round($containerRatio, 3)); " +
    "managed allocation=$($result.dataPlaneAllocatedBytes) B")
