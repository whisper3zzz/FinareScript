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

$demoDirectory = Join-Path $repositoryRoot "samples\Unreal\UnifiedScript.Unreal.Demo"
$demoProject = Join-Path $demoDirectory "UnifiedScript.Unreal.Demo.csproj"
$demoSource = Join-Path $demoDirectory "DemoScript.cs"
$alternateSource = Join-Path $demoDirectory "AlternateDemoScript.cs"
$bindingsSource = Join-Path $repositoryRoot "samples\Unreal\UEUnifiedScriptRuntime.bindings.cs"
$workbenchRoot = Join-Path $repositoryRoot "prototype\visual-editor"
$cliProject = Join-Path $repositoryRoot "src\UnifiedScript.Cli\UnifiedScript.Cli.csproj"
$coreAssembly = Join-Path $repositoryRoot "src\UnifiedScript.Core\bin\Debug\net10.0\UnifiedScript.Core.dll"
$unrealAssembly = Join-Path $repositoryRoot "src\UnifiedScript.Unreal\bin\Debug\net10.0\UnifiedScript.Unreal.dll"
$managedDirectory = Join-Path $repositoryRoot "HostProject\Plugins\UEUnifiedScript\Binaries\Managed\Demo"
$artifactDirectory = Join-Path $repositoryRoot "artifacts\ue\authoring"

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $managedDirectory | Out-Null

Write-Host "[1/2] Building and staging the baseline Unreal managed demo..."
& $dotnetPath build $demoProject --nologo
if ($LASTEXITCODE -ne 0) {
    throw "The baseline Unreal managed demo build failed with exit code $LASTEXITCODE."
}

Write-Host "[2/2] Starting the transactional authoring host at http://127.0.0.1:$Port/"
Write-Host "Keep this window open, then use Apply & Reload in the UE UnifiedScript Authoring tab."
& $dotnetPath run --project $cliProject -- `
    debug-serve $demoSource `
    --source $alternateSource `
    --source $bindingsSource `
    --root $workbenchRoot `
    --port $Port `
    --assembly "UnifiedScript.Unreal.Demo" `
    --reference $coreAssembly `
    --reference $unrealAssembly `
    --identity (Join-Path $demoDirectory "DemoScript.cs.usids") `
    --asset-out (Join-Path $demoDirectory "UnifiedScript.Unreal.Demo.asset.json") `
    --surface-out (Join-Path $demoDirectory "UnifiedScript.Unreal.Demo.surface.json") `
    --managed-out (Join-Path $managedDirectory "UnifiedScript.Unreal.Demo.dll") `
    --pdb-out (Join-Path $managedDirectory "UnifiedScript.Unreal.Demo.pdb") `
    --history (Join-Path $artifactDirectory "UnifiedScript.Unreal.Demo.authoring-history.json")
exit $LASTEXITCODE
