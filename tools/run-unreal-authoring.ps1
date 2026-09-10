param(
    [string]$EngineRoot = "D:\UE_5.8",
    [ValidateRange(1024, 65535)]
    [int]$Port = 4173
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnetPath = Join-Path $EngineRoot "Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnetPath)) {
    throw "The UE bundled .NET runtime was not found: $dotnetPath"
}

$demoDirectory = Join-Path $repositoryRoot "samples\Unreal\Finare.Unreal.Demo"
$demoProject = Join-Path $demoDirectory "Finare.Unreal.Demo.csproj"
$demoSource = Join-Path $demoDirectory "DemoScript.cs"
$alternateSource = Join-Path $demoDirectory "AlternateDemoScript.cs"
$bindingsSource = Join-Path $repositoryRoot "samples\Unreal\FinareRuntime.bindings.cs"
$workbenchRoot = Join-Path $repositoryRoot "prototype\visual-editor"
$cliProject = Join-Path $repositoryRoot "src\Finare.Cli\Finare.Cli.csproj"
$coreAssembly = Join-Path $repositoryRoot "src\Finare.Core\bin\Debug\net10.0\Finare.Core.dll"
$unrealAssembly = Join-Path $repositoryRoot "src\Finare.Unreal\bin\Debug\net10.0\Finare.Unreal.dll"
$managedDirectory = Join-Path $repositoryRoot "HostProject\Plugins\Finare\Binaries\Managed\Demo"
$artifactDirectory = Join-Path $repositoryRoot "artifacts\ue\authoring"

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $managedDirectory | Out-Null

Write-Host "[1/2] Building and staging the baseline Unreal managed demo..."
& $dotnetPath build $demoProject --nologo
if ($LASTEXITCODE -ne 0) {
    throw "The baseline Unreal managed demo build failed with exit code $LASTEXITCODE."
}

Write-Host "[2/2] Starting the transactional authoring host at http://127.0.0.1:$Port/"
Write-Host "Keep this window open, then use Apply & Reload in the Finare Authoring tab."
& $dotnetPath run --project $cliProject -- `
    debug-serve $demoSource `
    --source $alternateSource `
    --source $bindingsSource `
    --root $workbenchRoot `
    --port $Port `
    --assembly "Finare.Unreal.Demo" `
    --reference $coreAssembly `
    --reference $unrealAssembly `
    --identity (Join-Path $demoDirectory "DemoScript.cs.usids") `
    --asset-out (Join-Path $demoDirectory "Finare.Unreal.Demo.asset.json") `
    --surface-out (Join-Path $demoDirectory "Finare.Unreal.Demo.surface.json") `
    --layout-out (Join-Path $demoDirectory "Finare.Unreal.Demo.usgraph") `
    --managed-out (Join-Path $managedDirectory "Finare.Unreal.Demo.dll") `
    --pdb-out (Join-Path $managedDirectory "Finare.Unreal.Demo.pdb") `
    --history (Join-Path $artifactDirectory "Finare.Unreal.Demo.authoring-history.json")
exit $LASTEXITCODE
