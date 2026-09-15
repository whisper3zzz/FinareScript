[CmdletBinding()]
param([Parameter(Mandatory)][string]$EngineRoot, [string]$AutomationLog)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo 'artifacts/api-library'
$project = Join-Path $repo 'HostProject/FinareHost.uproject'
$build = Join-Path $EngineRoot 'Engine/Build/BatchFiles/Build.bat'
$editor = Join-Path $EngineRoot 'Engine/Binaries/Win64/UnrealEditor-Cmd.exe'
$dotnet = Join-Path $EngineRoot 'Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe'
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
New-Item -ItemType Directory -Force -Path $output | Out-Null

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
}
function Build-Target([string]$Target) {
    Invoke-Checked $build @($Target, 'Win64', 'Development', "-Project=$project",
        '-WaitMutex', '-NoHotReloadFromIDE', '-MaxParallelActions=4')
}

# Export with the current exporter and stage exactly the host assemblies against
# which both gameplay revisions will compile.
Invoke-Checked $dotnet @('build', (Join-Path $repo 'src/Finare.Unreal/Finare.Unreal.csproj'),
    '--verbosity', 'minimal')
Build-Target 'FinareHostEditor'
& (Join-Path $PSScriptRoot 'prepare-api-library.ps1') -EngineRoot $EngineRoot
& (Join-Path $PSScriptRoot 'prepare-native-component-creation.ps1') -EngineRoot $EngineRoot `
    -ReflectionPath (Join-Path $output 'full-reflection.json') `
    -CliPath (Join-Path $output 'build/bin/Finare.Cli/debug/Finare.Cli.dll')

$native = Join-Path $repo 'HostProject/Plugins/Finare/Source/FinareRuntime/Private/Generated/FinareRuntime.native-thunks.generated.cpp'
$snapshots = @{}
foreach ($path in @($native, (Join-Path $repo 'HostProject/Config/DefaultEngine.ini'),
        (Join-Path $repo 'HostProject/Config/DefaultInput.ini'))) {
    $snapshots[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
$nativeChanged = $false
try {
    Copy-Item -LiteralPath (Join-Path $output 'FullThunks.cpp') -Destination $native -Force
    $nativeChanged = $true
    Build-Target 'FinareHostEditor'
    Build-Target 'FinareHost'
    $tests = @('Finare.Unreal.ApiDiscovery.ExportScope', 'Finare.Unreal.ApiDiscovery.GenericOutputs',
        'Finare.Unreal.ApiLibrary.NewCallReload', 'Finare.Unreal.ProjectSetup.SourceSave',
        'Finare.Unreal.Components.GeneratedCreation')
    $logPath = if ($AutomationLog) { [IO.Path]::GetFullPath($AutomationLog) } else { Join-Path $output 'automation.log' }
    if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath }
    Invoke-Checked $editor @($project, "-ExecCmds=Automation RunTests $($tests -join '+')",
        '-TestExit=Automation Test Queue Empty', '-unattended', '-nop4', '-nosplash',
        '-NullRHI', '-NoConfigChanges', "-abslog=$logPath")
    $log = [IO.File]::ReadAllText($logPath)
    foreach ($test in $tests) {
        if ($log -notmatch ('Test Completed\. Result=\{Success\}.*Path=\{' + [regex]::Escape($test) + '\}')) {
            throw "Automation did not report success for $test. See $logPath"
        }
    }
    if ($log -match 'Test Completed\. Result=\{Fail') { throw "Automation failed. See $logPath" }
    if ($log -match 'Fatal error:|Assertion failed:|Ensure condition failed:') { throw "Engine failure in $logPath" }
} finally {
    foreach ($path in $snapshots.Keys) {
        if ($null -ne $snapshots[$path]) { [IO.File]::WriteAllBytes($path, $snapshots[$path]) }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    # The checkout contains a focused generated fixture. Restore its binaries too
    # so later Automation never runs a library different from the source on disk.
    if ($nativeChanged) {
        Build-Target 'FinareHostEditor'
        Build-Target 'FinareHost'
    }
}
Write-Host 'API library: PASS (available declarations, native Editor/Game builds, same-process reload, ref/out, save notifications).'
