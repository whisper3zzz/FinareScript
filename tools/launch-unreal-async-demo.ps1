[CmdletBinding()]
param(
    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,
    [ValidateSet('Play', 'Editor', 'Prepare', 'Verify', 'Package')]
    [string]$Mode = 'Play',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'HostProject\UEUnifiedScriptHost.uproject'
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = @('D:\UE_5.8', 'D:\UE\_5.8') |
        Where-Object { Test-Path (Join-Path $_ 'Engine\Build\BatchFiles\Build.bat') } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) { throw 'Pass -EngineRoot or set UNIFIEDSCRIPT_UE_ROOT.' }
$engine = [IO.Path]::GetFullPath($EngineRoot)
$build = Join-Path $engine 'Engine\Build\BatchFiles\Build.bat'
$editor = Join-Path $engine 'Engine\Binaries\Win64\UnrealEditor.exe'
$cmdEditor = Join-Path $engine 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
foreach ($required in @($project, $build, $editor, $cmdEditor)) {
    if (!(Test-Path -LiteralPath $required)) { throw "Missing Demo dependency: $required" }
}
$running = @(Get-CimInstance Win32_Process -Filter "Name = 'UnrealEditor.exe' OR Name = 'UnrealEditor-Cmd.exe'" |
    Where-Object { $_.CommandLine -like '*UEUnifiedScriptHost.uproject*' })
if ($running.Count -gt 0) {
    throw 'Close the HostProject game/editor first so preparation cannot conflict with unsaved assets or loaded managed assemblies.'
}
$logs = Join-Path $repoRoot ("artifacts\ue\async-demo\" + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Force -Path $logs | Out-Null
if (!$SkipBuild) {
    & $build UEUnifiedScriptHostEditor Win64 Development "-Project=$project" -WaitMutex -NoHotReloadFromIDE
    if ($LASTEXITCODE -ne 0) { throw "Demo native build failed: $LASTEXITCODE" }
    & (Join-Path $PSScriptRoot 'emit-unreal-demo-chunk.ps1') -EngineRoot $engine -Configuration Release
}
$prepareLog = Join-Path $logs 'prepare.log'
& $cmdEditor $project -run=UnifiedScriptPrepareAsyncDemo -unattended -nop4 -nosplash -NullRHI "-abslog=$prepareLog"
if ($LASTEXITCODE -ne 0 -or !(Select-String -LiteralPath $prepareLog -Pattern 'UNIFIEDSCRIPT_ASYNC_UI_PREPARED' -Quiet)) {
    throw "Demo preparation failed. See $prepareLog"
}
$map = '/Game/UnifiedScriptDemo/L_AsyncUiDemo'
if ($Mode -eq 'Prepare') { Write-Output "Prepared $map. Existing scene edits were preserved."; return }
if ($Mode -eq 'Package') {
    $archive = Join-Path $logs 'package'
    $uat = Join-Path $engine 'Engine\Build\BatchFiles\RunUAT.bat'
    & $uat BuildCookRun "-project=$project" -noP4 -platform=Win64 -clientconfig=Development `
        -build -cook -stage -pak -archive "-map=$map" "-archivedirectory=$archive" `
        -AdditionalCookerOptions=-SkipZenStore -unattended -utf8output -NoCodeSign -NoXGE
    if ($LASTEXITCODE -ne 0) { throw "Demo packaging failed: $LASTEXITCODE" }
    $packageRoot = Join-Path $archive 'Windows'
    $game = Join-Path $packageRoot 'UEUnifiedScriptHost.exe'
    $runLog = Join-Path $logs 'packaged-runtime.log'
    if (!(Test-Path -LiteralPath $game)) { throw "Packaged executable missing: $game" }
    $verification = Start-Process -FilePath $game -ArgumentList @(
        $map, '-UnifiedScriptAsyncUiVerify', '-unattended', '-nosplash', '-NullRHI',
        ('-abslog="' + $runLog + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($verification.ExitCode -ne 0 -or
        !(Select-String -LiteralPath $runLog -Pattern 'UNIFIEDSCRIPT_ASYNC_UI_VERIFY PASS' -Quiet)) {
        throw "Packaged Demo verification failed. See $runLog"
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'StartAsyncDemo.cmd') -Destination $packageRoot
    Write-Output "Packaged Demo verified. Launch $(Join-Path $packageRoot 'StartAsyncDemo.cmd')"
    return
}
if ($Mode -eq 'Verify') {
    $runLog = Join-Path $logs 'runtime.log'
    & $cmdEditor $project $map -game -UnifiedScriptAsyncUiVerify -unattended -nop4 -nosplash -NullRHI "-abslog=$runLog"
    if ($LASTEXITCODE -ne 0 -or !(Select-String -LiteralPath $runLog -Pattern 'UNIFIEDSCRIPT_ASYNC_UI_VERIFY PASS' -Quiet)) {
        throw "Demo runtime verification failed. See $runLog"
    }
    Write-Output "Demo runtime verified. $runLog"
    return
}
# These modes explicitly request a visible interactive game/editor window.
$arguments = @(('"' + $project + '"'), $map, '-windowed', '-ResX=1280', '-ResY=720', '-nosplash')
if ($Mode -eq 'Play') { $arguments += '-game' }
$process = Start-Process -FilePath $editor -ArgumentList $arguments -PassThru
Write-Output "Opened Async UI Demo ($Mode), process $($process.Id)."
