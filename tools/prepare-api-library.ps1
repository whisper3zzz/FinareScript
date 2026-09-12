[CmdletBinding()]
param([string]$EngineRoot = 'D:/UE_5.8')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo 'artifacts/api-library'
$project = Join-Path $repo 'HostProject/FinareHost.uproject'
$editor = Join-Path $EngineRoot 'Engine/Binaries/Win64/UnrealEditor-Cmd.exe'
$dotnet = Join-Path $EngineRoot 'Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe'
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
New-Item -ItemType Directory -Force -Path $output | Out-Null
function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
}
$build = Join-Path $output 'build'
Invoke-Checked $dotnet @('build', (Join-Path $repo 'src/Finare.Cli/Finare.Cli.csproj'),
    '--artifacts-path', $build, '-p:FinareSkipManagedHostStaging=true', '--verbosity', 'minimal')
$cli = Join-Path $build 'bin/Finare.Cli/debug/Finare.Cli.dll'
$manifest = Join-Path $output 'full-reflection.json'
$snapshots = @{}
foreach ($relative in @('HostProject/Config/DefaultEngine.ini', 'HostProject/Config/DefaultInput.ini')) {
    $path = Join-Path $repo $relative
    $snapshots[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
try {
    Invoke-Checked $editor @($project, '-run=FinareExport', "-Output=$manifest",
        '-Modules=Engine,FinareRuntime', '-DiscoveryModules=Engine,FinareRuntime', '-IncludePublicReflected',
        '-unattended', '-nop4', '-nosplash', '-NullRHI', '-NoConfigChanges', "-abslog=$(Join-Path $output 'export.log')")
} finally {
    foreach ($path in $snapshots.Keys) {
        if ($null -ne $snapshots[$path]) { [IO.File]::WriteAllBytes($path, $snapshots[$path]) }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
}
$bindings = Join-Path $output 'FullBindings.g.cs'
$native = Join-Path $output 'FullThunks.cpp'
Invoke-Checked $dotnet @($cli, 'ue-bindings', $manifest, '--api-library',
    '--namespace', 'Finare.ApiLibrary.Generated', '--out', $bindings, '--native-out', $native,
    '--report', (Join-Path $output 'full-coverage.json'), '--fail-on-rejected')
# Compile the available library before any gameplay source is part of the project.
$unreal = [Security.SecurityElement]::Escape((Join-Path $repo 'HostProject/Plugins/Finare/Binaries/Managed/Finare.Unreal.dll'))
$core = [Security.SecurityElement]::Escape((Join-Path $repo 'HostProject/Plugins/Finare/Binaries/Managed/Finare.Core.dll'))
$fixtureProject = Join-Path $output 'Finare.ApiLibrary.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><EnableDefaultItems>false</EnableDefaultItems></PropertyGroup>
  <ItemGroup>
    <Compile Include="FullBindings.g.cs" />
    <Compile Include="FirstScript.cs" Condition="'`$(IncludeGameplay)' != 'false'" />
    <Reference Include="Finare.Unreal"><HintPath>$unreal</HintPath><Private>false</Private></Reference>
    <Reference Include="Finare.Core"><HintPath>$core</HintPath><Private>false</Private></Reference>
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $fixtureProject -Encoding utf8
Invoke-Checked $dotnet @('build', $fixtureProject, '-p:IncludeGameplay=false', '--verbosity', 'minimal')
$source = @'
using Finare;
using Finare.Unreal;
using Finare.ApiLibrary.Generated;
namespace Finare.ApiLibrary;
[ScriptAsset("ApiLibrary/FirstScript")]
public sealed class FirstScript : UnrealActorComponentScript
{
    protected override void OnBeginPlay()
    {
        var host = new FinareApiDiscoveryComponent(HostHandle, Bridge);
        host.DiscoveryResult = host.DiscoveryAdd(20, 22);
    }
}
'@
$revision = @'
int total = host.DiscoveryOffset(host.DiscoveryResult);
        new KismetMathLibrary(HostHandle, Bridge).BreakVector(new UnrealVector(1, 2, 3), out double x, out double y, out double z);
        if (x != 1 || y != 2 || z != 3) throw new System.InvalidOperationException("Static ref/out dispatch failed.");
        if (!host.DiscoveryOutputs(2, ref total, out string label)) throw new System.InvalidOperationException();
        host.DiscoveryResult = total;
        new FinareComponent(HostHandle, Bridge).ManagedState = label;
        var data = host.DiscoveryData with { Value = total, Label = label, Numbers = new[] { 1, 2 } };
        host.DiscoveryData = data;
        if (!host.DiscoveryEchoData(data, out var echoed)) throw new System.InvalidOperationException();
        host.DiscoveryResult = echoed.Value;
'@
$before = Get-FileHash -LiteralPath @($bindings, $native)
foreach ($version in @('v1', 'v2')) {
    $body = if ($version -eq 'v1') { $source } else {
        $source.Replace('host.DiscoveryResult = host.DiscoveryAdd(20, 22);', $revision)
    }
    $body | Set-Content -LiteralPath (Join-Path $output 'FirstScript.cs') -Encoding utf8
    Invoke-Checked $dotnet @('build', $fixtureProject, '-p:IncludeGameplay=true', '--verbosity', 'minimal')
    Invoke-Checked $dotnet @($cli, 'emit', $fixtureProject,
        '--out', (Join-Path $repo "HostProject/Saved/Finare/ApiLibrary/$version"),
        '--configuration', 'Debug', '--no-restore', '--no-build')
}
$after = Get-FileHash -LiteralPath @($bindings, $native)
if (Compare-Object ($before.Hash) ($after.Hash)) { throw 'Gameplay edits changed the available binding library.' }
Write-Output 'Library compiled before gameplay; two emitted revisions share unchanged bindings/native output. Run Finare.Unreal.ApiLibrary CMD Automation.'
