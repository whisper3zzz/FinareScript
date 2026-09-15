[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:/UE_5.8',
    [switch]$SkipBuild,
    [switch]$SkipPrepare,
    [string[]]$Tests = @(
        'Finare.Unreal.ScriptClass.ParentChange.OrdinaryActorComposition',
        'Finare.Unreal.ScriptClass.ParentChange.OrdinaryActorMultiAssetReload',
        'Finare.Unreal.ScriptClass.Composition.PublicationRecovery',
        'Finare.Unreal.ScriptClass.Composition.PureCSharpWorkflow'
    )
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'HostProject/FinareHost.uproject'
$dotnet = Join-Path $EngineRoot 'Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe'
$output = Join-Path $repoRoot 'artifacts/class-composition'
New-Item -ItemType Directory -Force -Path $output | Out-Null
if (!$SkipPrepare) {
    & (Join-Path $PSScriptRoot 'prepare-class-composition.ps1') -DotNet $dotnet
    if ($LASTEXITCODE -ne 0) { throw 'Composition fixture preparation failed.' }
}
if (!$SkipBuild) {
    & (Join-Path $EngineRoot 'Engine/Build/BatchFiles/Build.bat') FinareHostEditor Win64 Development "-Project=$project" -WaitMutex -NoHotReloadFromIDE -MaxParallelActions=4
    if ($LASTEXITCODE -ne 0) { throw 'FinareHostEditor compilation failed.' }
}
$logPath = Join-Path $output 'editor.log'
if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath }
$snapshots = @{}
foreach ($relative in @('HostProject/Config/DefaultEngine.ini', 'HostProject/Config/DefaultInput.ini')) {
    $path = Join-Path $repoRoot $relative
    $snapshots[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
try {
    & (Join-Path $EngineRoot 'Engine/Binaries/Win64/UnrealEditor-Cmd.exe') $project `
        '-unattended' '-nop4' '-NullRHI' '-nosplash' '-NoConfigChanges' `
        "-ExecCmds=Automation RunTests $($Tests -join '+')" `
        '-TestExit=Automation Test Queue Empty' "-abslog=$logPath" '-log'
    $editorExitCode = $LASTEXITCODE
} finally {
    foreach ($path in $snapshots.Keys) {
        if ($null -ne $snapshots[$path]) { [IO.File]::WriteAllBytes($path, $snapshots[$path]) }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
}
if (!(Test-Path -LiteralPath $logPath)) { throw "Editor did not create its log: $logPath" }
$log = Get-Content -LiteralPath $logPath -Raw
foreach ($test in $Tests) {
    $success = 'Test Completed\. Result=\{Success\}[^\r\n]*Path=\{' + [regex]::Escape($test) + '\}'
    if ($log -notmatch $success) { throw "Composition test did not pass: $test. See $logPath" }
}
if ($editorExitCode -ne 0 -or $log -match 'Test Completed\. Result=\{Fail\}|Ensure condition failed:|Fatal error:') {
    throw "Editor verification reported a failure. See $logPath"
}
Write-Output "Passed $($Tests.Count) class composition tests. Log: $logPath"
