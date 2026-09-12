[CmdletBinding()]
param([string]$EngineRoot = 'D:/UE_5.8')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo 'artifacts/api-discovery'
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

# A real native export supplies the signatures; no handwritten member list or thunk table.
$manifest = Join-Path $output 'reflection.json'
$legacyManifest = Join-Path $output 'legacy-reflection.json'
$rootType = '/Script/FinareRuntime.FinareApiDiscoveryComponent'
$configPath = Join-Path $repo 'HostProject/Config/DefaultEngine.ini'
$configBytes = [IO.File]::ReadAllBytes($configPath)
try {
foreach ($mode in @('legacy', 'public')) {
    $destination = if ($mode -eq 'legacy') { $legacyManifest } else { $manifest }
    $exportArgs = @($project, '-run=FinareExport', "-Output=$destination",
        '-Modules=FinareRuntime', "-Classes=$rootType", '-unattended', '-nop4', '-nosplash', '-NullRHI', '-NoConfigChanges',
        "-abslog=$(Join-Path $output "$mode-export.log")")
    if ($mode -eq 'public') {
        $exportArgs += @('-IncludePublicReflected', "-DiscoveryTypes=$rootType")
    }
    Invoke-Checked $editor $exportArgs
}
} finally {
    [IO.File]::WriteAllBytes($configPath, $configBytes)
}
$legacyType = (Get-Content $legacyManifest -Raw | ConvertFrom-Json).types |
    Where-Object path -eq $rootType
$publicType = (Get-Content $manifest -Raw | ConvertFrom-Json).types |
    Where-Object path -eq $rootType
if (@($legacyType.functions | Where-Object name -eq 'DiscoveryAdd').Count -ne 0 -or
    @($publicType.functions | Where-Object name -eq 'DiscoveryAdd').Count -ne 1 -or
    @($publicType.properties | Where-Object name -eq 'DiscoveryResult').Count -ne 1 -or
    @($publicType.functions | Where-Object name -eq 'DiscoveryPrivate').Count -ne 0 -or
    @($publicType.properties | Where-Object name -eq 'DiscoveryPrivateValue').Count -ne 0) {
    throw 'Public reflected export or legacy visibility compatibility failed.'
}

$artifacts = Join-Path $output 'build'
Invoke-Checked $dotnet @('build', (Join-Path $repo 'src/Finare.Cli/Finare.Cli.csproj'),
    '--artifacts-path', $artifacts, '-p:FinareSkipManagedHostStaging=true', '--verbosity', 'minimal')
$cli = Join-Path $artifacts 'bin/Finare.Cli/debug/Finare.Cli.dll'
$bindings = Join-Path $output 'Bindings.g.cs'
$fixture = Join-Path $repo 'tests/Finare.Tests/Fixtures/ApiDiscovery/DiscoveryScript.cs'
$unreal = Join-Path $artifacts 'bin/Finare.Unreal/debug/Finare.Unreal.dll'
$fixtureProject = Join-Path $output 'Finare.ApiDiscovery.csproj'
$escapedFixture = [Security.SecurityElement]::Escape($fixture)
$escapedUnreal = [Security.SecurityElement]::Escape($unreal)
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <ItemGroup>
    <Compile Include="Bindings.g.cs" Condition="Exists('Bindings.g.cs')" />
    <Compile Include="$escapedFixture" />
    <Reference Include="Finare.Unreal"><HintPath>$escapedUnreal</HintPath><Private>false</Private></Reference>
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $fixtureProject -Encoding utf8
Invoke-Checked $dotnet @($cli, 'ue-bindings', $manifest, '--project', $fixtureProject,
    '--namespace', 'Finare.ApiDiscovery.Generated', '--configuration', 'Debug', '--dotnet', $dotnet,
    '--out', $bindings, '--report', (Join-Path $output 'coverage.json'), '--fail-on-rejected')
Invoke-Checked $dotnet @('build', $fixtureProject, '--output',
    (Join-Path $repo 'HostProject/Saved/Finare/ApiDiscovery'), '--verbosity', 'minimal')
Write-Output 'Prepared generated C# fixture. Run Finare.Unreal.ApiDiscovery CMD Automation.'
