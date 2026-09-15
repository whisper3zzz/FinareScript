[CmdletBinding()]
param(
    [string]$DotNet = 'D:/UE_5.8/Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe',
    [string]$OutputRoot = 'artifacts/class-composition'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd('\', '/')
if (!$output.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Composition outputs must remain in a subdirectory of repository artifacts.'
}
$project = Join-Path $repoRoot 'tests/Finare.Tests/Fixtures/ClassComposition/ClassCompositionFixture.csproj'
$source = Join-Path (Split-Path -Parent $project) 'ClassCompositionFixture.cs'
$hostDirectory = Join-Path $repoRoot 'HostProject/Plugins/Finare/Binaries/Managed'
$cli = Join-Path $repoRoot 'src/Finare.Cli/bin/Release/net10.0/Finare.Cli.dll'
$generated = Join-Path $output 'generated'
$configPath = Join-Path $output 'fixture.json'
$identityPath = Join-Path $generated 'ClassCompositionFixture.usids'
$builtDll = Join-Path (Split-Path -Parent $project) 'bin/Release/net10.0/ClassCompositionFixture.dll'
foreach ($required in @($DotNet, $cli, (Join-Path $hostDirectory 'Finare.Unreal.dll'), (Join-Path $hostDirectory 'Finare.Core.dll'))) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing fixture dependency: $required" }
}
New-Item -ItemType Directory -Force -Path $generated | Out-Null

$fixtureId = [Guid]::NewGuid().ToString('N').ToUpperInvariant()
if (Test-Path -LiteralPath $configPath) {
    $previous = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if ([string]$previous.fixtureId -cmatch '^[0-9A-F]{32}$') { $fixtureId = [string]$previous.fixtureId }
    else { throw 'The existing composition fixture identity is invalid.' }
}
$packageDirectory = "/Game/FinareComposition_$fixtureId"
$actorClassPath = "$packageDirectory/CompositionActor.CompositionActor_C"
$healthClassPath = "$packageDirectory/CompositionHealth.CompositionHealth_C"
$moverClassPath = "$packageDirectory/CompositionMover.CompositionMover_C"
@"
namespace Finare.Tests.Fixtures.ClassComposition;
public static class CompositionPaths
{
    public const string ActorClassPath = "$actorClassPath";
    public const string HealthClassPath = "$healthClassPath";
    public const string MoverClassPath = "$moverClassPath";
}
"@ | Set-Content -LiteralPath (Join-Path $generated 'CompositionPaths.g.cs') -Encoding utf8

function Invoke-Checked([string[]]$Arguments) {
    & $DotNet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Fixture command failed with exit code $LASTEXITCODE`: $($Arguments -join ' ')" }
}
$oldGenerated = $env:CompositionGeneratedDirectory
$oldHostDirectory = $env:CompositionHostDirectory
$oldDotNetRoot = $env:DOTNET_ROOT
$env:CompositionGeneratedDirectory = $generated
$env:CompositionHostDirectory = $hostDirectory
$env:DOTNET_ROOT = Split-Path -Parent $DotNet
$variants = [ordered]@{}
$scripts = @()
try {
    # The native carrier round-trip regression consumes this independent
    # declaration-only fixture. Generate it here instead of relying on an old
    # ignored artifact from an earlier local test run.
    Invoke-Checked @('test', (Join-Path $repoRoot 'tests/Finare.Tests/Finare.Tests.csproj'),
        '--configuration', 'Release', '--nologo', '--verbosity', 'minimal',
        '--filter', 'FullyQualifiedName~ScriptClassBindingTests.EmitsClassBindingJsonAndChunkFixtureForNativeConsumer',
        '-p:FinareSkipManagedHostStaging=true')
    # Later revisions are compiled from actual source saves by the Editor test.
    foreach ($revision in @('v1')) {
        $revisionRoot = Join-Path $output $revision
        $declarationRoot = Join-Path $revisionRoot 'declaration'
        New-Item -ItemType Directory -Force -Path $declarationRoot | Out-Null
        $failMover = if ($revision -eq 'bad') { 'true' } else { 'false' }
        @"
namespace Finare.Tests.Fixtures.ClassComposition;
public static class CompositionRevision
{
    public static string Label => "$revision";
    public static bool FailMover => $failMover;
}
"@ | Set-Content -LiteralPath (Join-Path $generated 'CompositionRevision.cs') -Encoding utf8

        Invoke-Checked @('build', $project, '--configuration', 'Release', '--nologo', '--verbosity', 'minimal', '-p:FinareSkipManagedHostStaging=true')
        if (!(Test-Path -LiteralPath $identityPath)) {
            Invoke-Checked @($cli, 'identity-init', $source, '--out', $identityPath,
                '--assembly', 'ClassCompositionFixture', '--reference', (Join-Path $hostDirectory 'Finare.Unreal.dll'),
                '--reference', (Join-Path $hostDirectory 'Finare.Core.dll'), '--reference', $builtDll)
        }
        Invoke-Checked @($cli, 'emit', $project, '--out', $declarationRoot, '--identity', $identityPath,
            '--configuration', 'Release', '--no-restore', '--no-build')
        $assemblyPath = Join-Path $revisionRoot 'ClassCompositionFixture.dll'
        Copy-Item -LiteralPath $builtDll -Destination $assemblyPath -Force
        $declarationPath = Join-Path $declarationRoot 'ClassCompositionFixture.asset.json'
        $document = Get-Content -LiteralPath $declarationPath -Raw | ConvertFrom-Json
        $selected = @($document.assets | Where-Object { $_.qualifiedName -like 'Finare.Tests.Fixtures.ClassComposition.Composition*' })
        if ($selected.Count -ne 3) { throw "Expected three real C# Script Assets for $revision, found $($selected.Count)." }
        $currentScripts = @($selected | ForEach-Object {
            [ordered]@{
                scriptClassId = [string]$_.id
                scriptTypeName = [string]$_.qualifiedName
                declarationAssetPath = [string]$_.assetPath
                generatedClassPath = [string]$_.classBinding.generatedClassPath
                kind = [string]$_.classBinding.kind
            }
        } | Sort-Object { $_.scriptTypeName })
        if ($scripts.Count -eq 0) { $scripts = $currentScripts }
        elseif (($scripts | ConvertTo-Json -Depth 6 -Compress) -cne ($currentScripts | ConvertTo-Json -Depth 6 -Compress)) {
            throw "Script identity or class binding changed in revision $revision."
        }
        $variants[$revision] = [ordered]@{
            assemblyPath = $assemblyPath
            declarationPath = $declarationPath
            chunkPath = Join-Path $declarationRoot 'ClassCompositionFixture.uschunk'
        }
    }
} finally {
    $env:CompositionGeneratedDirectory = $oldGenerated
    $env:CompositionHostDirectory = $oldHostDirectory
    $env:DOTNET_ROOT = $oldDotNetRoot
}

$liveDirectory = Join-Path $output 'live'
New-Item -ItemType Directory -Force -Path $liveDirectory | Out-Null
$liveAssemblyPath = Join-Path $liveDirectory 'ClassCompositionFixture.dll'
$liveDeclarationPath = Join-Path $liveDirectory 'ClassCompositionFixture.asset.json'
Copy-Item -LiteralPath $variants.v1.assemblyPath -Destination $liveAssemblyPath -Force
Copy-Item -LiteralPath $variants.v1.declarationPath -Destination $liveDeclarationPath -Force
$workspace = Join-Path $output 'workspace'
New-Item -ItemType Directory -Force -Path $workspace | Out-Null
$watchProject = Join-Path $workspace 'ClassCompositionFixture.csproj'
$watchSource = Join-Path $workspace 'CompositionRevision.cs'
$watchConfig = Join-Path $output 'watcher.json'
$sourceXml = [Security.SecurityElement]::Escape($source)
$bindingsXml = [Security.SecurityElement]::Escape((Join-Path $repoRoot 'samples/Unreal/FinareRuntime.bindings.cs'))
$pathsXml = [Security.SecurityElement]::Escape((Join-Path $generated 'CompositionPaths.g.cs'))
$hostXml = [Security.SecurityElement]::Escape($hostDirectory)
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AssemblyName>ClassCompositionFixture</AssemblyName>
    <RootNamespace>Finare.Tests.Fixtures.ClassComposition</RootNamespace>
    <EnableDefaultItems>false</EnableDefaultItems><AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$sourceXml" />
    <Compile Include="$bindingsXml" />
    <Compile Include="$pathsXml" />
    <Compile Include="CompositionRevision.cs" />
    <Reference Include="Finare.Core"><HintPath>$hostXml/Finare.Core.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Finare.Unreal"><HintPath>$hostXml/Finare.Unreal.dll</HintPath><Private>false</Private></Reference>
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $watchProject -Encoding utf8
@'
namespace Finare.Tests.Fixtures.ClassComposition;
public static class CompositionRevision
{
    public static string Label => "v1";
    public static bool FailMover => false;
}
'@ | Set-Content -LiteralPath $watchSource -Encoding utf8
@{ managed = @{ projects = @($watchProject) } } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $watchConfig -Encoding utf8
[ordered]@{
    schemaVersion = 'finare.class-composition-fixture/v1'
    fixtureId = $fixtureId
    packageDirectory = $packageDirectory
    actorClassPath = $actorClassPath
    healthClassPath = $healthClassPath
    moverClassPath = $moverClassPath
    sourcePath = $source
    projectPath = $project
    liveAssemblyPath = $liveAssemblyPath
    liveDeclarationPath = $liveDeclarationPath
    identityPath = $identityPath
    watchProjectPath = $watchProject
    watchSourcePath = $watchSource
    watcherConfigPath = $watchConfig
    revisionBuildScript = Join-Path $repoRoot 'tools/build-class-composition-revision.ps1'
    dailyBuildReportPath = Join-Path $output 'daily-build-report.json'
    scripts = $scripts
    variants = $variants
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding utf8
Write-Output "Class composition fixture: $configPath"
