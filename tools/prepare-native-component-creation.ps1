[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:/UE_5.8',
    [string]$ReflectionPath,
    [string]$CliPath
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo 'artifacts/native-component-creation'
$project = Join-Path $repo 'HostProject/FinareHost.uproject'
$dotnet = Join-Path $EngineRoot 'Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe'
New-Item -ItemType Directory -Force -Path $output | Out-Null
function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
}
if (!$CliPath) {
    $cliBuild = Join-Path $output 'cli-build'
    Invoke-Checked $dotnet @('build', (Join-Path $repo 'src/Finare.Cli/Finare.Cli.csproj'),
        '--artifacts-path', $cliBuild, '-p:FinareSkipManagedHostStaging=true', '--verbosity', 'minimal')
    $CliPath = Join-Path $cliBuild 'bin/Finare.Cli/debug/Finare.Cli.dll'
}
if (!$ReflectionPath) {
    $ReflectionPath = Join-Path $output 'reflection.json'
    $snapshots = @{}
    foreach ($relative in @('HostProject/Config/DefaultEngine.ini', 'HostProject/Config/DefaultInput.ini')) {
        $path = Join-Path $repo $relative
        $snapshots[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
    }
    try {
        Invoke-Checked (Join-Path $EngineRoot 'Engine/Binaries/Win64/UnrealEditor-Cmd.exe') @(
            $project, '-run=FinareExport', "-Output=$ReflectionPath", '-Modules=Engine,FinareRuntime',
            '-DiscoveryModules=Engine,FinareRuntime', '-IncludePublicReflected', '-unattended', '-nop4',
            '-nosplash', '-NullRHI', '-NoConfigChanges', "-abslog=$(Join-Path $output 'export.log')")
    } finally {
        foreach ($path in $snapshots.Keys) {
            if ($null -ne $snapshots[$path]) { [IO.File]::WriteAllBytes($path, $snapshots[$path]) }
            elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        }
    }
}
Invoke-Checked $dotnet @($CliPath, 'ue-bindings', $ReflectionPath, '--api-library',
    '--namespace', 'Finare.NativeComponentCreation.Generated', '--out', (Join-Path $output 'Bindings.g.cs'),
    '--native-out', (Join-Path $output 'Thunks.cpp'), '--report', (Join-Path $output 'coverage.json'), '--fail-on-rejected')
$fixture = Join-Path $repo 'tests/Finare.Tests/Fixtures/NativeComponentCreation/NativeComponentCreation.csproj'
$build = Join-Path $output 'build'
# The same module library compiles before and after the gameplay source exists.
Invoke-Checked $dotnet @('build', $fixture, '--artifacts-path', $build, '-p:IncludeGameplay=false', '--verbosity', 'minimal')
Invoke-Checked $dotnet @('build', $fixture, '--artifacts-path', $build, '--verbosity', 'minimal')
$previousArtifacts = $env:ArtifactsPath
try {
    $env:ArtifactsPath = $build
    Invoke-Checked $dotnet @($CliPath, 'emit', $fixture, '--out',
        (Join-Path $repo 'HostProject/Saved/Finare/NativeComponentCreation'),
        '--configuration', 'Debug', '--no-restore', '--no-build')
} finally { $env:ArtifactsPath = $previousArtifacts }
Write-Output 'Prepared native component creation through the generated module API library.'
