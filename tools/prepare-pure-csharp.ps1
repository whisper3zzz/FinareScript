[CmdletBinding()]
param([string]$EngineRoot = 'D:/UE_5.8')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo 'artifacts/pure-csharp'
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

function Render-Template([string]$RelativePath) {
    (Get-Content -LiteralPath (Join-Path $repo "templates/UnrealProject/$RelativePath") -Raw).
        Replace('__PROJECT_NAME__', 'PureCSharp').
        Replace('__SCRIPT_NAME__', 'FirstScript').
        Replace('__MANAGED_NAMESPACE__', 'Finare.PureCSharp')
}

# Exercise the shipped default template without a Contract, request list, overlay, or lock.
$source = Render-Template 'Managed/FirstScript.cs.template'
$sourcePath = Join-Path $output 'FirstScript.cs'
$source | Set-Content -LiteralPath $sourcePath -Encoding utf8
if ($source.Contains('[Visual]')) {
    throw 'The default C# fixture must not depend on Visual methods.'
}

$artifacts = Join-Path $repo 'artifacts/api-discovery/build'
Invoke-Checked $dotnet @('build', (Join-Path $repo 'src/Finare.Cli/Finare.Cli.csproj'),
    '--artifacts-path', $artifacts, '-p:FinareSkipManagedHostStaging=true', '--verbosity', 'minimal')
$cli = Join-Path $artifacts 'bin/Finare.Cli/debug/Finare.Cli.dll'
# Like an adopted SDK project, compile against the runtime actually loaded by UE.
# Chunk dependency validation must not mix isolated tool-build DLLs with the staged host.
$unreal = [Security.SecurityElement]::Escape((Join-Path $repo 'HostProject/Plugins/Finare/Binaries/Managed/Finare.Unreal.dll'))
$core = [Security.SecurityElement]::Escape((Join-Path $repo 'HostProject/Plugins/Finare/Binaries/Managed/Finare.Core.dll'))
$analyzer = [Security.SecurityElement]::Escape((Join-Path $repo 'HostProject/Plugins/Finare/Binaries/Managed/Finare.Unreal.Generators.dll'))
$fixtureProject = Join-Path $output 'Finare.PureCSharp.csproj'
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><LangVersion>preview</LangVersion><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <ItemGroup>
    <Compile Include="FirstScript.cs" />
    <Compile Include="Bindings.g.cs" Condition="Exists('Bindings.g.cs')" />
    <Reference Include="Finare.Unreal"><HintPath>$unreal</HintPath><Private>false</Private></Reference>
    <Reference Include="Finare.Core"><HintPath>$core</HintPath><Private>false</Private></Reference>
    <Analyzer Include="$analyzer" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $fixtureProject -Encoding utf8
$hints = Join-Path $output 'type-hints.json'
Invoke-Checked $dotnet @($cli, 'ue-reflection-hints', '--project', $fixtureProject,
    '--namespace', 'Finare.PureCSharp.Generated', '--out', $hints, '--configuration', 'Debug', '--dotnet', $dotnet)
$typeNames = @((Get-Content $hints -Raw | ConvertFrom-Json).hints | ForEach-Object { $_.managedTypeName })
$manifest = Join-Path $output 'reflection.json'
$configPaths = @('HostProject/Config/DefaultEngine.ini', 'HostProject/Config/DefaultInput.ini')
$snapshots = @{}
foreach ($relative in $configPaths) {
    $path = Join-Path $repo $relative
    $snapshots[$path] = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}
try {
    Invoke-Checked $editor @($project, '-run=FinareExport', "-Output=$manifest",
        '-Modules=Engine,FinareRuntime', '-Classes=/Script/FinareRuntime.FinareComponent',
        "-TypeNames=$($typeNames -join ',')", '-IncludePublicReflected',
        '-unattended', '-nop4', '-nosplash', '-NullRHI', '-NoConfigChanges',
        "-abslog=$(Join-Path $output 'export.log')")
} finally {
    foreach ($path in $snapshots.Keys) {
        if ($null -ne $snapshots[$path]) { [IO.File]::WriteAllBytes($path, $snapshots[$path]) }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
}
Invoke-Checked $dotnet @($cli, 'ue-bindings', $manifest, '--project', $fixtureProject,
    '--namespace', 'Finare.PureCSharp.Generated', '--configuration', 'Debug', '--dotnet', $dotnet,
    '--out', (Join-Path $output 'Bindings.g.cs'), '--report', (Join-Path $output 'coverage.json'), '--fail-on-rejected')

try {
    foreach ($version in @('v1', 'v2')) {
        if ($version -eq 'v2') {
            $source.Replace('AddClamped(StartValue, 1)', 'AddClamped(StartValue, 2)') |
                Set-Content -LiteralPath $sourcePath -Encoding utf8
        }
        $destination = Join-Path $repo "HostProject/Saved/Finare/PureCSharp/$version"
        Invoke-Checked $dotnet @('build', $fixtureProject, '--verbosity', 'minimal')
        Invoke-Checked $dotnet @($cli, 'emit', $fixtureProject, '--out', $destination,
            '--configuration', 'Debug', '--no-restore', '--no-build')
    }
} finally {
    $source | Set-Content -LiteralPath $sourcePath -Encoding utf8
}
Write-Output 'Prepared source-discovered C# and a body-only revision. Run Finare.Unreal.PureCSharp CMD Automation.'
