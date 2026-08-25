[CmdletBinding()]
param(
    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,
    [string]$PuertsResult,
    [switch]$RequirePuerts,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'HostProject\UEUnifiedScriptHost.uproject'
$managedProject = Join-Path $repoRoot 'samples\Unreal\UnifiedScript.Unreal.Demo\UnifiedScript.Unreal.Demo.csproj'
$thresholdPath = Join-Path $repoRoot 'benchmarks\unreal\performance-thresholds.json'
$artifactDirectory = Join-Path $repoRoot 'artifacts\ue'
$logPath = Join-Path $artifactDirectory 'managed-interop-benchmark.log'
$reportPath = Join-Path $artifactDirectory 'interop-benchmark.json'

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
foreach ($requiredPath in @($buildBat, $editorCmd, $dotnetExe, $thresholdPath)) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Required benchmark dependency is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$defaultEnginePath = Join-Path $repoRoot 'HostProject\Config\DefaultEngine.ini'
$defaultInputPath = Join-Path $repoRoot 'HostProject\Config\DefaultInput.ini'
$defaultEngineBytes = if (Test-Path -LiteralPath $defaultEnginePath) {
    [IO.File]::ReadAllBytes($defaultEnginePath)
} else { $null }
$defaultInputBytes = if (Test-Path -LiteralPath $defaultInputPath) {
    [IO.File]::ReadAllBytes($defaultInputPath)
} else { $null }

try {
    if (!$SkipBuild) {
        & $dotnetExe build $managedProject -c Release --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Managed benchmark build failed with exit code $LASTEXITCODE."
        }
        & $buildBat UEUnifiedScriptHostEditor Win64 Development $projectPath `
            -WaitMutex -NoHotReloadFromIDE -NoUBA -MaxParallelActions=4
        if ($LASTEXITCODE -ne 0) {
            throw "Unreal benchmark build failed with exit code $LASTEXITCODE."
        }
    }

    & $editorCmd $projectPath `
        -unattended -nop4 -NullRHI -nosplash `
        '-ExecCmds=Automation RunTests UnifiedScript.Unreal.Performance.ManagedInterop' `
        '-TestExit=Automation Test Queue Empty' `
        "-abslog=$logPath" -log
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
    throw "Unreal benchmark process failed with exit code $editorExitCode."
}
$successPattern =
    'Test Completed. Result={Success} Name={ManagedInterop} Path={UnifiedScript.Unreal.Performance.ManagedInterop}'
if (!(Select-String -LiteralPath $logPath -SimpleMatch $successPattern -Quiet)) {
    throw "Managed interop benchmark Automation did not succeed. Log: $logPath"
}
$marker = 'UNIFIEDSCRIPT_BENCHMARK_JSON '
$line = Select-String -LiteralPath $logPath -SimpleMatch $marker |
    Select-Object -Last 1
if ($null -eq $line) {
    throw "Benchmark JSON marker is missing. Log: $logPath"
}
$nativeResult = ($line.Line.Substring($line.Line.IndexOf($marker) + $marker.Length) |
    ConvertFrom-Json)
$thresholds = Get-Content -Raw -LiteralPath $thresholdPath | ConvertFrom-Json
if ($thresholds.schemaVersion -ne 'unifiedscript.unreal-benchmark-thresholds/v0.2' -or
    $nativeResult.schemaVersion -ne 'unifiedscript.unreal-benchmark/v0.2' -or
    $nativeResult.scenario -ne $thresholds.scenario -or
    [int]$nativeResult.warmupIterations -lt [int]$thresholds.minimumWarmupIterations -or
    [int]$nativeResult.iterations -lt [int]$thresholds.minimumIterations -or
    [int]$nativeResult.sampleCount -lt [int]$thresholds.minimumSampleCount) {
    throw 'The benchmark result does not match the checked-in schema/scenario.'
}

$cpuName = (Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty Name)
$machineDescriptor = [string]::Join('|', @(
    [Environment]::MachineName,
    [Environment]::OSVersion.VersionString,
    $cpuName))
$hasher = [Security.Cryptography.SHA256]::Create()
try {
    $machineHash = $hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($machineDescriptor))
} finally {
    $hasher.Dispose()
}
$machineFingerprint = -join ($machineHash | ForEach-Object { $_.ToString('x2') })
$machineFingerprint = $machineFingerprint.Substring(0, 16)

$puertsNsPerCall = $null
if (![string]::IsNullOrWhiteSpace($PuertsResult)) {
    $puerts = Get-Content -Raw -LiteralPath $PuertsResult | ConvertFrom-Json
    if ($puerts.schemaVersion -ne 'unifiedscript.unreal-benchmark/v0.2' -or
        $puerts.scenario -ne $nativeResult.scenario -or
        $puerts.configuration -ne $nativeResult.configuration -or
        $puerts.platform -ne $nativeResult.platform -or
        [int]$puerts.engineMajor -ne [int]$nativeResult.engineMajor -or
        [int]$puerts.engineMinor -ne [int]$nativeResult.engineMinor -or
        [int]$puerts.enginePatch -ne [int]$nativeResult.enginePatch -or
        [int]$puerts.warmupIterations -ne [int]$nativeResult.warmupIterations -or
        [int]$puerts.iterations -ne [int]$nativeResult.iterations -or
        [int]$puerts.sampleCount -ne [int]$nativeResult.sampleCount -or
        $puerts.machineFingerprint -ne $machineFingerprint -or
        $null -eq $puerts.puertsNsPerCall -or
        [double]$puerts.puertsNsPerCall -le 0) {
        throw 'The Puerts result must use the same v0.2 machine, engine, configuration, workload, and contain puertsNsPerCall.'
    }
    $puertsNsPerCall = [double]$puerts.puertsNsPerCall
}

$absolutePass =
    [double]$nativeResult.unifiedScriptNsPerCall -le [double]$thresholds.maxUnifiedScriptNsPerCall
$allocationPass =
    [long]$nativeResult.unifiedScriptAllocatedBytes -le [long]$thresholds.maxUnifiedScriptAllocatedBytes
$competitiveRatio = if ($null -ne $puertsNsPerCall) {
    [double]$nativeResult.unifiedScriptNsPerCall / $puertsNsPerCall
} else { $null }
$competitivePass = if ($null -ne $competitiveRatio) {
    $competitiveRatio -le [double]$thresholds.maxUnifiedScriptToPuertsRatio
} else { !$RequirePuerts }

$report = [ordered]@{
    schemaVersion = 'unifiedscript.unreal-benchmark-report/v0.2'
    scenario = $nativeResult.scenario
    configuration = $nativeResult.configuration
    platform = $nativeResult.platform
    engineMajor = [int]$nativeResult.engineMajor
    engineMinor = [int]$nativeResult.engineMinor
    enginePatch = [int]$nativeResult.enginePatch
    machineFingerprint = $machineFingerprint
    warmupIterations = [int]$nativeResult.warmupIterations
    iterations = [int]$nativeResult.iterations
    sampleCount = [int]$nativeResult.sampleCount
    cppDirectNsPerCall = [double]$nativeResult.cppDirectNsPerCall
    unifiedScriptNsPerCall = [double]$nativeResult.unifiedScriptNsPerCall
    unifiedScriptAllocatedBytes = [long]$nativeResult.unifiedScriptAllocatedBytes
    puertsNsPerCall = $puertsNsPerCall
    unifiedScriptToPuertsRatio = $competitiveRatio
    gates = [ordered]@{
        matchedWorkload = $true
        absoluteLatency = $absolutePass
        zeroManagedAllocation = $allocationPass
        fasterThanPuerts = if ($null -ne $puertsNsPerCall) { $competitivePass } else { $null }
    }
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Host "UnifiedScript: $($nativeResult.unifiedScriptNsPerCall) ns/call, $($nativeResult.unifiedScriptAllocatedBytes) B"
Write-Host "C++ direct lower bound: $($nativeResult.cppDirectNsPerCall) ns/call"
if ($null -ne $puertsNsPerCall) {
    Write-Host "Puerts: $puertsNsPerCall ns/call; ratio: $competitiveRatio"
} else {
    Write-Host 'Puerts competitive gate: not run (pass -PuertsResult, and optionally -RequirePuerts).'
}
Write-Host "Report: $reportPath"

if (!$absolutePass -or !$allocationPass -or !$competitivePass) {
    throw 'One or more Unreal interop performance gates failed.'
}
