[CmdletBinding()]
param(
    [string]$EngineRoot,
    [string]$Schema,
    [string]$ManagedOutput,
    [string]$NativeOutput,
    [string]$CatalogOutput,
    [switch]$Check,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceCliProject = Join-Path $repoRoot 'src\UnifiedScript.Cli\UnifiedScript.Cli.csproj'
$sourceCliAssembly = Join-Path $repoRoot `
    'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll'
$packagedCliAssembly = Join-Path $repoRoot `
    'Tools\UnifiedScript.Cli\UnifiedScript.Cli.dll'
$isSourceLayout = Test-Path -LiteralPath $sourceCliProject -PathType Leaf
$pathBase = if ($isSourceLayout) { $repoRoot } else { (Get-Location).Path }

function Resolve-RepositoryPath {
    param([string]$Value, [string]$DefaultRelativePath)
    $candidate = if ([string]::IsNullOrWhiteSpace($Value)) {
        if ([IO.Path]::IsPathRooted($DefaultRelativePath)) {
            $DefaultRelativePath
        } else {
            Join-Path $pathBase $DefaultRelativePath
        }
    } elseif ([IO.Path]::IsPathRooted($Value)) {
        $Value
    } else {
        Join-Path $pathBase $Value
    }
    return [IO.Path]::GetFullPath($candidate)
}

if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = @($env:UNREAL_ENGINE_ROOT, 'D:\UE_5.8', 'D:\UE\_5.8') |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Where-Object {
            Test-Path -LiteralPath (Join-Path $_ `
                'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe')
        } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'A UE 5.8 EngineRoot with bundled .NET 10 is required.'
}
$dotnet = Join-Path ([IO.Path]::GetFullPath($EngineRoot)) `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "UE bundled .NET 10 was not found: $dotnet"
}

$defaultSchema = if ($isSourceLayout) {
    'samples\Unreal\BlockBreaker.data-plane-extension.json'
} else {
    Join-Path $repoRoot 'Schemas\unreal-data-plane-extension.v0.1.example.json'
}
$defaultManagedOutput = if ($isSourceLayout) {
    'samples\Unreal\UnifiedScript.Unreal.Demo\BlockBreakerDataPlaneExtension.g.cs'
} else {
    'Generated\BlockBreakerDataPlaneExtension.g.cs'
}
$defaultNativeOutput = if ($isSourceLayout) {
    'HostProject\Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Private\Tests\Generated\UnifiedScriptBlockBreakerDataPlaneExtension.gen.h'
} else {
    'Generated\UnifiedScriptBlockBreakerDataPlaneExtension.gen.h'
}
$defaultCatalogOutput = if ($isSourceLayout) {
    'samples\Unreal\BlockBreaker.data-plane-extension.catalog.json'
} else {
    'Generated\BlockBreaker.data-plane-extension.catalog.json'
}
$resolvedSchema = Resolve-RepositoryPath $Schema $defaultSchema
$resolvedManagedOutput = Resolve-RepositoryPath $ManagedOutput $defaultManagedOutput
$resolvedNativeOutput = Resolve-RepositoryPath $NativeOutput $defaultNativeOutput
$resolvedCatalogOutput = Resolve-RepositoryPath $CatalogOutput $defaultCatalogOutput
if (-not (Test-Path -LiteralPath $resolvedSchema -PathType Leaf)) {
    throw "Data-plane extension schema was not found: $resolvedSchema"
}

$cliAssembly = if ($isSourceLayout) { $sourceCliAssembly } else { $packagedCliAssembly }
if ($isSourceLayout -and -not $SkipBuild) {
    & $dotnet build $sourceCliProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "UnifiedScript CLI build failed with exit code $LASTEXITCODE."
    }
}
if (-not (Test-Path -LiteralPath $cliAssembly -PathType Leaf)) {
    throw "UnifiedScript CLI assembly was not found: $cliAssembly"
}

$temporaryRoot = $null
try {
    if ($Check) {
        $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) `
            ("UnifiedScriptDataPlaneExtension-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
        $generatedManaged = Join-Path $temporaryRoot 'extension.g.cs'
        $generatedNative = Join-Path $temporaryRoot 'extension.gen.h'
        $generatedCatalog = Join-Path $temporaryRoot 'extension.catalog.json'
    } else {
        foreach ($output in @(
                $resolvedManagedOutput,
                $resolvedNativeOutput,
                $resolvedCatalogOutput)) {
            $parent = Split-Path -Parent $output
            if (-not [string]::IsNullOrWhiteSpace($parent)) {
                New-Item -ItemType Directory -Force -Path $parent | Out-Null
            }
        }
        $generatedManaged = $resolvedManagedOutput
        $generatedNative = $resolvedNativeOutput
        $generatedCatalog = $resolvedCatalogOutput
    }

    & $dotnet $cliAssembly ue-data-plane-extension $resolvedSchema `
        --managed-out $generatedManaged `
        --native-out $generatedNative `
        --catalog-out $generatedCatalog
    if ($LASTEXITCODE -ne 0) {
        throw "Data-plane extension generation failed with exit code $LASTEXITCODE."
    }

    if ($Check) {
        foreach ($pair in @(
            @($resolvedManagedOutput, $generatedManaged),
            @($resolvedNativeOutput, $generatedNative),
            @($resolvedCatalogOutput, $generatedCatalog))) {
            if (-not (Test-Path -LiteralPath $pair[0] -PathType Leaf)) {
                throw "Checked-in generated extension artifact is missing: $($pair[0])"
            }
            if ((Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $pair[1] -Algorithm SHA256).Hash) {
                throw "Generated data-plane extension artifact is stale: $($pair[0])"
            }
        }
        Write-Host 'UnifiedScript data-plane extension: deterministic and current.'
    } else {
        Write-Host 'UnifiedScript data-plane extension regenerated.'
    }
} finally {
    if ($null -ne $temporaryRoot -and
        (Test-Path -LiteralPath $temporaryRoot -PathType Container)) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        $systemTemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolvedTemporaryRoot.StartsWith(
                $systemTemporaryRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected temporary path: $resolvedTemporaryRoot"
        }
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
