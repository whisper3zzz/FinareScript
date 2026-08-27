[CmdletBinding()]
param(
    [string]$EngineRoot,
    [string]$Schema,
    [string]$ManagedOutput,
    [string]$NativeOutput,
    [switch]$Check,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-RepositoryPath {
    param(
        [string]$Value,
        [string]$DefaultRelativePath
    )
    $candidate = if ([string]::IsNullOrWhiteSpace($Value)) {
        Join-Path $repoRoot $DefaultRelativePath
    } elseif ([IO.Path]::IsPathRooted($Value)) {
        $Value
    } else {
        Join-Path $repoRoot $Value
    }
    return [IO.Path]::GetFullPath($candidate)
}

if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $engineCandidates = @(
        $env:UNREAL_ENGINE_ROOT,
        'D:\UE_5.8',
        'D:\UE\_5.8'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $EngineRoot = $engineCandidates |
        Where-Object {
            Test-Path -LiteralPath (Join-Path $_ `
                'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe')
        } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'A UE 5.8 EngineRoot with bundled .NET 10 is required.'
}

$resolvedEngineRoot = [IO.Path]::GetFullPath($EngineRoot)
$dotnet = Join-Path $resolvedEngineRoot `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "UE bundled .NET 10 was not found: $dotnet"
}

$resolvedSchema = Resolve-RepositoryPath `
    -Value $Schema `
    -DefaultRelativePath 'samples\Unreal\UEUnifiedScriptRuntime.data-plane.json'
$resolvedManagedOutput = Resolve-RepositoryPath `
    -Value $ManagedOutput `
    -DefaultRelativePath 'src\UnifiedScript.Unreal\UnrealDataPlaneLayouts.g.cs'
$resolvedNativeOutput = Resolve-RepositoryPath `
    -Value $NativeOutput `
    -DefaultRelativePath (
        'HostProject\Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\' +
        'Public\UnifiedScriptDataPlaneLayouts.gen.h')
if (-not (Test-Path -LiteralPath $resolvedSchema -PathType Leaf)) {
    throw "Data-plane layout schema was not found: $resolvedSchema"
}

$cliProject = Join-Path $repoRoot 'src\UnifiedScript.Cli\UnifiedScript.Cli.csproj'
$cliAssembly = Join-Path $repoRoot `
    'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll'
if (-not $SkipBuild) {
    & $dotnet build $cliProject -c Release --nologo
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
            ("UnifiedScriptDataPlane-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
        $generationManagedOutput = Join-Path $temporaryRoot 'UnrealDataPlaneLayouts.g.cs'
        $generationNativeOutput = Join-Path $temporaryRoot 'UnifiedScriptDataPlaneLayouts.gen.h'
    } else {
        $generationManagedOutput = $resolvedManagedOutput
        $generationNativeOutput = $resolvedNativeOutput
    }

    & $dotnet $cliAssembly ue-data-plane $resolvedSchema `
        --managed-out $generationManagedOutput `
        --native-out $generationNativeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Data-plane layout generation failed with exit code $LASTEXITCODE."
    }

    if ($Check) {
        foreach ($pair in @(
            @($resolvedManagedOutput, $generationManagedOutput),
            @($resolvedNativeOutput, $generationNativeOutput))) {
            if (-not (Test-Path -LiteralPath $pair[0] -PathType Leaf)) {
                throw "Checked-in generated data-plane artifact is missing: $($pair[0])"
            }
            $expected = (Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash
            $actual = (Get-FileHash -LiteralPath $pair[1] -Algorithm SHA256).Hash
            if ($expected -ne $actual) {
                throw "Generated data-plane artifact is stale: $($pair[0])"
            }
        }
        Write-Host 'UnifiedScript data-plane layouts: deterministic and current.'
    } else {
        Write-Host 'UnifiedScript data-plane layouts regenerated.'
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
