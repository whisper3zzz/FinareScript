[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectPath,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [string]$EngineRoot = 'D:/UE_5.8',
    [string]$DotNet,
    [string]$HostDirectory,
    [string]$CliAssembly,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = [IO.Path]::GetFullPath($ProjectPath)
$outputRootPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$projectDirectory = [IO.Path]::GetDirectoryName($projectPath)
$classesDirectory = Join-Path $projectDirectory 'Classes'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd('\', '/')
$declarationOutput = Join-Path $outputRootPath 'declaration'
$publishOutput = Join-Path $outputRootPath 'publish'
$logStarted = $false

if ([string]::IsNullOrWhiteSpace($DotNet)) {
    $engineRootFull = [IO.Path]::GetFullPath($EngineRoot).TrimEnd('\', '/')
    if ([IO.Path]::GetFileName($engineRootFull).Equals('Engine', [StringComparison]::OrdinalIgnoreCase)) {
        $DotNet = Join-Path $engineRootFull 'Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe'
    } else {
        $DotNet = Join-Path $engineRootFull 'Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe'
    }
}
if ([string]::IsNullOrWhiteSpace($HostDirectory)) { $HostDirectory = Join-Path $repoRoot 'HostProject/Plugins/UEUnifiedScript/Binaries/Managed' }
if ([string]::IsNullOrWhiteSpace($CliAssembly)) { $CliAssembly = Join-Path $repoRoot 'src/UnifiedScript.Cli/bin/Release/net10.0/UnifiedScript.Cli.dll' }
$DotNet = [IO.Path]::GetFullPath($DotNet)
$HostDirectory = [IO.Path]::GetFullPath($HostDirectory)
$CliAssembly = [IO.Path]::GetFullPath($CliAssembly)

if ($outputRootPath -eq $artifactRoot -or !$outputRootPath.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "OutputRoot must remain below artifacts: $outputRootPath" }
if (!(Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw "Fixture project is missing: $projectPath" }
if (!(Test-Path -LiteralPath $DotNet -PathType Leaf)) { throw "Bundled dotnet is missing: $DotNet" }
if (!(Test-Path -LiteralPath $CliAssembly -PathType Leaf)) { throw "UnifiedScript CLI is missing: $CliAssembly" }
foreach ($name in @('UnifiedScript.Core.dll','UnifiedScript.CSharp.dll','UnifiedScript.Graph.dll','UnifiedScript.Runtime.dll','UnifiedScript.Semantics.dll','UnifiedScript.Unreal.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $HostDirectory $name) -PathType Leaf)) { throw "Deployed managed host dependency is missing: $(Join-Path $HostDirectory $name)" }
}

if ($LogPath) {
    $LogPath = [IO.Path]::GetFullPath($LogPath)
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($LogPath)) | Out-Null
    Start-Transcript -LiteralPath $LogPath -Force | Out-Null
    $logStarted = $true
}

function Normalize-ComparablePath([string]$Path) {
    return ([IO.Path]::GetFullPath($Path)).Replace('\', '/').TrimEnd('/')
}

function ConvertTo-ExtendedPath([string]$Path) {
    if ($Path.StartsWith('\\?\', [StringComparison]::OrdinalIgnoreCase)) {
        return $Path
    }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.StartsWith('\\')) {
        return '\\?\UNC\' + $full.Substring(2)
    }
    return '\\?\' + $full
}

function Get-SourceSidecarMatch([string]$SidecarPath, [string]$SourcePath) {
    $sidecar = [IO.File]::ReadAllText((ConvertTo-ExtendedPath $SidecarPath)) | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$sidecar.sourcePath)) { return $false }
    $raw = ([string]$sidecar.sourcePath).Replace('\', '/')
    $sourceFull = Normalize-ComparablePath $SourcePath
    $rawResolvedCandidates = if ([IO.Path]::IsPathRooted($raw)) {
        @(Normalize-ComparablePath $raw)
    } else {
        @(
            (Normalize-ComparablePath (Join-Path $projectDirectory $raw)),
            (Normalize-ComparablePath (Join-Path $repoRoot $raw))
        )
    }
    if ($rawResolvedCandidates -contains $sourceFull) { return $true }
    $relative = $sourceFull.Substring((Normalize-ComparablePath $projectDirectory).Length).TrimStart('/')
    return $raw.TrimStart('/') -eq $relative
}

function Sync-EmittedSidecars([System.IO.FileInfo[]]$Sources, [System.IO.FileInfo[]]$EmittedSidecars) {
    $used = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($source in $Sources) {
        $matches = @($EmittedSidecars | Where-Object {
            if (!$used.Contains($_.FullName)) { Get-SourceSidecarMatch $_.FullName $source.FullName } else { $false }
        })
        if ($matches.Count -ne 1) {
            throw "Official emit did not produce exactly one identity sidecar whose sourcePath matches '$($source.FullName)' (found $($matches.Count))."
        }
        [IO.File]::Copy(
            (ConvertTo-ExtendedPath $matches[0].FullName),
            (ConvertTo-ExtendedPath ($source.FullName + '.usids')),
            $true)
        [void]$used.Add($matches[0].FullName)
    }
    if ($used.Count -ne $EmittedSidecars.Count) {
        throw "Official emit produced $($EmittedSidecars.Count) sidecars but only $($used.Count) matched Classes sources."
    }
}

try {
    New-Item -ItemType Directory -Force -Path $declarationOutput, $publishOutput | Out-Null
    $propertyName = 'ActiveClassAssetOperationsHostDirectory'
    $oldProperty = [Environment]::GetEnvironmentVariable($propertyName, 'Process')
    try {
        [Environment]::SetEnvironmentVariable($propertyName, $HostDirectory, 'Process')
        & $DotNet build $projectPath --configuration Release --nologo "-p:$propertyName=$HostDirectory" -p:UnifiedScriptSkipManagedHostStaging=true
        if ($LASTEXITCODE -ne 0) { throw "Active class asset operations fixture build failed with exit code $LASTEXITCODE." }
        $sourceCandidates = @(Get-ChildItem -LiteralPath $classesDirectory -Filter '*.cs' -File -Recurse)
        if ($sourceCandidates.Count -eq 0) { throw "No factory-generated active source files were found under '$classesDirectory'." }
        $existingSidecars = @(Get-ChildItem -LiteralPath $classesDirectory -Filter '*.cs.usids' -File -Recurse)
        $missingSidecars = @($sourceCandidates | Where-Object { !(Test-Path -LiteralPath ($_.FullName + '.usids') -PathType Leaf) })
        if ($existingSidecars.Count -ne 0 -and $missingSidecars.Count -ne 0) {
            throw "Stable identity sidecars are incomplete; refusing to reinitialize $($missingSidecars.Count) source(s)."
        }
        Get-ChildItem -LiteralPath $declarationOutput -Filter '*.usids' -File -ErrorAction SilentlyContinue | ForEach-Object { [IO.File]::Delete((ConvertTo-ExtendedPath $_.FullName)) }
        if ($existingSidecars.Count -eq 0) {
            foreach ($source in $sourceCandidates) {
                $sourceIdentityPath = "$($source.FullName).usids"
                & $DotNet $CliAssembly identity-init $source.FullName --project $projectPath `
                    --out $sourceIdentityPath --configuration Release --no-restore --no-build --dotnet $DotNet
                if ($LASTEXITCODE -ne 0) {
                    throw "Initial active class identity initialization failed for '$($source.FullName)' with exit code $LASTEXITCODE."
                }
                if (!(Test-Path -LiteralPath $sourceIdentityPath -PathType Leaf)) {
                    throw "Identity initialization did not produce '$sourceIdentityPath'."
                }
            }
        }
        foreach ($source in $sourceCandidates) {
            if (!(Test-Path -LiteralPath ($source.FullName + '.usids') -PathType Leaf)) {
                throw "The active source identity sidecar is missing: $($source.FullName).usids"
            }
        }
        & $DotNet $CliAssembly emit $projectPath --out $declarationOutput --configuration Release --no-restore --no-build
        if ($LASTEXITCODE -ne 0) { throw "Active class declaration emission failed with exit code $LASTEXITCODE." }
        $emittedSidecars = @(Get-ChildItem -LiteralPath $declarationOutput -Filter '*.usids' -File)
        Sync-EmittedSidecars $sourceCandidates $emittedSidecars
        & $DotNet publish $projectPath --configuration Release --no-restore --output $publishOutput "-p:$propertyName=$HostDirectory" -p:UnifiedScriptSkipManagedHostStaging=true
        if ($LASTEXITCODE -ne 0) { throw "Active class asset operations fixture publish failed with exit code $LASTEXITCODE." }
    }
    finally {
        [Environment]::SetEnvironmentVariable($propertyName, $oldProperty, 'Process')
    }
    $assemblyName = 'ActiveClassAssetOperationsFixture'
    $assetPath = Join-Path $declarationOutput "$assemblyName.asset.json"
    $chunkPath = Join-Path $declarationOutput "$assemblyName.uschunk"
    $assemblyPath = Join-Path $publishOutput "$assemblyName.dll"
    foreach ($source in $sourceCandidates) {
        $sourceIdentityPath = "$($source.FullName).usids"
        if (!(Test-Path -LiteralPath $sourceIdentityPath -PathType Leaf)) { throw "Fixture output is missing: $sourceIdentityPath" }
    }
    foreach ($path in @($assetPath, $chunkPath, $assemblyPath)) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Fixture output is missing: $path" } }
    Write-Output "Active class asset operations fixture declaration: $assetPath"
    Write-Output "Active class asset operations fixture chunk: $chunkPath"
    Write-Output "Active class asset operations fixture assembly: $assemblyPath"
}
finally {
    if ($logStarted) { Stop-Transcript | Out-Null }
}
