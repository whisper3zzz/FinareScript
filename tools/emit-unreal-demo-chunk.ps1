[CmdletBinding()]
param(
    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipManagedBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$managedProject = Join-Path `
    $repoRoot `
    'samples\Unreal\UnifiedScript.Unreal.Demo\UnifiedScript.Unreal.Demo.csproj'
$cliProject = Join-Path $repoRoot 'src\UnifiedScript.Cli\UnifiedScript.Cli.csproj'
$identityPath = Join-Path `
    $repoRoot `
    'samples\Unreal\UnifiedScript.Unreal.Demo\DemoScript.cs.usids'
$cliAssembly = Join-Path `
    $repoRoot `
    'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll'
$stageDirectory = Join-Path `
    $repoRoot `
    'HostProject\Plugins\UEUnifiedScript\Binaries\Managed\Demo'
$outputDirectory = Join-Path `
    $repoRoot `
    "artifacts\ue\compiled-chunks\$($Configuration.ToLowerInvariant())"

if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = @('D:\UE_5.8', 'D:\UE\_5.8') |
        Where-Object {
            Test-Path (Join-Path `
                $_ `
                'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe')
        } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'Pass -EngineRoot or set UNIFIEDSCRIPT_UE_ROOT.'
}

$dotnetExe = Join-Path `
    $EngineRoot `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
foreach ($requiredPath in @($dotnetExe, $managedProject, $cliProject, $identityPath)) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Required chunk compiler input is missing: $requiredPath"
    }
}

if (!$SkipManagedBuild) {
    & $dotnetExe build $cliProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "UnifiedScript CLI build failed with exit code $LASTEXITCODE."
    }
    & $dotnetExe build $managedProject -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Managed demo build failed with exit code $LASTEXITCODE."
    }
    $configuredHostAssembly = Join-Path `
        $repoRoot `
        "src\UnifiedScript.Unreal\bin\$Configuration\net10.0\UnifiedScript.Unreal.dll"
    $stagedHostAssembly = Join-Path `
        $repoRoot `
        'HostProject\Plugins\UEUnifiedScript\Binaries\Managed\UnifiedScript.Unreal.dll'
    if (!(Test-Path -LiteralPath $configuredHostAssembly) -or
        !(Test-Path -LiteralPath $stagedHostAssembly) -or
        (Get-FileHash -Algorithm SHA256 -LiteralPath $configuredHostAssembly).Hash -ne
            (Get-FileHash -Algorithm SHA256 -LiteralPath $stagedHostAssembly).Hash) {
        throw "Managed $Configuration host staging does not match its compiled dependency."
    }
} elseif (!(Test-Path -LiteralPath $cliAssembly)) {
    throw "-SkipManagedBuild requires an existing Release CLI assembly: $cliAssembly"
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
& $dotnetExe run `
    --project $cliProject `
    -c Release `
    --no-build `
    -- `
    emit $managedProject `
    --out $outputDirectory `
    --identity $identityPath `
    --configuration $Configuration `
    --no-restore `
    --no-build
if ($LASTEXITCODE -ne 0) {
    throw "UnifiedScript chunk emission failed with exit code $LASTEXITCODE."
}

$reconciledIdentities = @(
    Get-ChildItem -LiteralPath $outputDirectory -Filter '*.usids' -File
)
if ($reconciledIdentities.Count -ne 1) {
    throw "Chunk emission must produce exactly one reconciled Demo identity sidecar."
}
Copy-Item -LiteralPath $reconciledIdentities[0].FullName -Destination $identityPath -Force

$chunkName = 'UnifiedScript.Unreal.Demo.uschunk'
$compiledChunk = Join-Path $outputDirectory $chunkName
$stagedChunk = Join-Path $stageDirectory $chunkName
if (!(Test-Path -LiteralPath $compiledChunk)) {
    throw "Chunk compiler did not produce '$compiledChunk'."
}
New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
Copy-Item -LiteralPath $compiledChunk -Destination $stagedChunk -Force

& $dotnetExe run `
    --project $cliProject `
    -c Release `
    --no-build `
    -- `
    chunk-inspect $stagedChunk
if ($LASTEXITCODE -ne 0) {
    throw "Staged chunk verification failed with exit code $LASTEXITCODE."
}

Write-Output $stagedChunk
