[CmdletBinding()]
param(
    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,
    [switch]$SkipManagedBuild,
    [switch]$SkipNativeBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'HostProject\UEUnifiedScriptHost.uproject'
$managedProject = Join-Path $repoRoot 'samples\Unreal\UnifiedScript.Unreal.Demo\UnifiedScript.Unreal.Demo.csproj'

if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = @('D:\UE_5.8', 'D:\UE\_5.8') |
        Where-Object { Test-Path (Join-Path $_ 'Engine\Build\BatchFiles\Build.bat') } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'Pass -EngineRoot or set UNIFIEDSCRIPT_UE_ROOT to an installed Unreal Engine root.'
}

$buildBat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\Build.bat'
$editorCmd = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
$bundledDotNet = Join-Path $EngineRoot 'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
if (!(Test-Path $buildBat) -or !(Test-Path $editorCmd)) {
    throw "'$EngineRoot' is not a usable installed Unreal Engine root."
}
$dotnetExe = if (Test-Path $bundledDotNet) { $bundledDotNet } else { 'dotnet' }

if (!$SkipManagedBuild) {
    Write-Host '[1/4] Building and staging the managed demo...'
    & $dotnetExe build $managedProject --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Managed demo build failed with exit code $LASTEXITCODE."
    }
}

Write-Host '[2/4] Compiling and verifying the deterministic demo chunk...'
& (Join-Path $PSScriptRoot 'emit-unreal-demo-chunk.ps1') `
    -EngineRoot $EngineRoot `
    -Configuration Debug `
    -SkipManagedBuild
if ($LASTEXITCODE -ne 0) {
    throw "Managed demo chunk emission failed with exit code $LASTEXITCODE."
}

if (!$SkipNativeBuild) {
    Write-Host '[3/4] Building the Unreal host...'
    & $buildBat UEUnifiedScriptHostEditor Win64 Development $projectPath -WaitMutex -NoHotReloadFromIDE
    if ($LASTEXITCODE -ne 0) {
        throw "Unreal host build failed with exit code $LASTEXITCODE."
    }
}

$artifactDirectory = Join-Path $repoRoot 'artifacts\ue'
New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$demoLog = Join-Path $artifactDirectory 'managed-demo.log'
$defaultEnginePath = Join-Path $repoRoot 'HostProject\Config\DefaultEngine.ini'
$defaultInputPath = Join-Path $repoRoot 'HostProject\Config\DefaultInput.ini'
$defaultEngineBytes = if (Test-Path $defaultEnginePath) {
    [IO.File]::ReadAllBytes($defaultEnginePath)
} else {
    $null
}
$defaultInputBytes = if (Test-Path $defaultInputPath) {
    [IO.File]::ReadAllBytes($defaultInputPath)
} else {
    $null
}

Write-Host '[4/4] Running .uschunk -> managed C# -> generated wrapper -> live UObject demo...'
try {
    $editorArguments = @(
        $projectPath,
        '-unattended',
        '-nop4',
        '-NullRHI',
        '-nosplash',
        '-ExecCmds=Automation RunTests UnifiedScript.Unreal.WorldHost.Lifecycle',
        '-TestExit=Automation Test Queue Empty',
        "-abslog=$demoLog",
        '-log'
    )
    & $editorCmd @editorArguments
    $editorExitCode = $LASTEXITCODE
} finally {
    if ($null -ne $defaultEngineBytes) {
        [IO.File]::WriteAllBytes($defaultEnginePath, $defaultEngineBytes)
    } elseif (Test-Path $defaultEnginePath) {
        Remove-Item -LiteralPath $defaultEnginePath
    }
    if ($null -ne $defaultInputBytes) {
        [IO.File]::WriteAllBytes($defaultInputPath, $defaultInputBytes)
    } elseif (Test-Path $defaultInputPath) {
        Remove-Item -LiteralPath $defaultInputPath
    }
}

$testPattern =
    'Test Completed. Result={Success} Name={Lifecycle} Path={UnifiedScript.Unreal.WorldHost.Lifecycle}'
$testSucceeded = Select-String -Path $demoLog -SimpleMatch $testPattern -Quiet
$demoResult = Select-String -Path $demoLog -SimpleMatch 'UnifiedScript demo completed:' |
    Select-Object -Last 1
if ($editorExitCode -ne 0 -or !$testSucceeded -or $null -eq $demoResult) {
    Write-Host "Demo log: $demoLog"
    throw "Managed Unreal demo failed (editor exit code $editorExitCode)."
}

Write-Host ''
Write-Host 'Demo succeeded.' -ForegroundColor Green
Write-Host ($demoResult.Line -replace '^.*UnifiedScript demo completed:', 'Result:')
Write-Host "Log: $demoLog"
