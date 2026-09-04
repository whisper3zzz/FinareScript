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
$emissionOutput = & $dotnetExe run `
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
$emissionOutput | Write-Output
if ($LASTEXITCODE -ne 0) {
    throw "UnifiedScript chunk emission failed with exit code $LASTEXITCODE."
}

$reconciledIdentities = @(
    $emissionOutput | Where-Object { [IO.Path]::GetExtension($_) -eq '.usids' } |
        ForEach-Object { Get-Item -LiteralPath $_ }
)
$managedDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $managedProject))
$identityUpdates = @{}
foreach ($sidecar in $reconciledIdentities) {
    $logicalSource = [string](Get-Content -LiteralPath $sidecar.FullName -Raw | ConvertFrom-Json).sourcePath
    $candidates = @(
        [IO.Path]::GetFullPath((Join-Path $repoRoot $logicalSource)),
        [IO.Path]::GetFullPath((Join-Path $managedDirectory $logicalSource))
    ) | Sort-Object -Unique | Where-Object {
        $_.StartsWith($managedDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $_ -PathType Leaf)
    }
    if (@($candidates).Count -ne 1) {
        throw "Cannot resolve emitted identity to exactly one Demo source: $logicalSource"
    }
    $identityUpdates.Add($sidecar.FullName, ([string]@($candidates)[0] + '.usids'))
}
foreach ($sidecarPath in $identityUpdates.Keys) {
    Copy-Item -LiteralPath $sidecarPath -Destination $identityUpdates[$sidecarPath] -Force
}

$chunkName = 'UnifiedScript.Unreal.Demo.uschunk'
$compiledChunk = Join-Path $outputDirectory $chunkName
$stagedChunk = Join-Path $stageDirectory $chunkName
if (!(Test-Path -LiteralPath $compiledChunk)) {
    throw "Chunk compiler did not produce '$compiledChunk'."
}
New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
Copy-Item -LiteralPath $compiledChunk -Destination $stagedChunk -Force
$declarationName = 'UnifiedScript.Unreal.Demo.asset.json'
$compiledDeclaration = Join-Path $outputDirectory $declarationName
if (!(Test-Path -LiteralPath $compiledDeclaration)) {
    throw "Chunk compiler did not produce '$compiledDeclaration'."
}
# Keep editor materialization on the declaration emitted with this chunk, not
# the older checked-in authoring fixture copied by the managed project build.
Copy-Item -LiteralPath $compiledDeclaration -Destination (Join-Path $stageDirectory $declarationName) -Force

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
