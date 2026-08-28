[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ChunkPath,

    [Parameter(Mandatory)]
    [string]$Crossgen2Path,

    [string[]]$ReferencePath = @(),

    [ValidateRange(3, 15)]
    [int]$Trials = 9,

    [string]$OutputDirectory,

    [string]$EngineRoot = 'D:\UE_5.8',

    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path `
    $EngineRoot `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
$runtimeRoot = Join-Path `
    $EngineRoot `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\shared\Microsoft.NETCore.App'
$sourceCliProject = Join-Path $repoRoot 'src\UnifiedScript.Cli\UnifiedScript.Cli.csproj'
$sourceCliAssembly = Join-Path `
    $repoRoot `
    "src\UnifiedScript.Cli\bin\$Configuration\net10.0\UnifiedScript.Cli.dll"
$packagedCliAssembly = Join-Path `
    $PSScriptRoot `
    'UnifiedScript.Cli\UnifiedScript.Cli.dll'
$packagedLayout = -not (Test-Path -LiteralPath $sourceCliProject) -and
    (Test-Path -LiteralPath $packagedCliAssembly)
$cliAssembly = if ($packagedLayout) {
    $packagedCliAssembly
} else {
    $sourceCliAssembly
}
$processProfiler = Join-Path $PSScriptRoot 'measure-compiled-chunk-load.ps1'
$resolvedChunk = [IO.Path]::GetFullPath($ChunkPath)
$resolvedCrossgen2 = [IO.Path]::GetFullPath($Crossgen2Path)
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path `
        $repoRoot `
        "artifacts\chunk-profile\readytorun-$((Get-Date).ToString('yyyyMMdd-HHmmss'))"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}

foreach ($required in @(
    $dotnet,
    $runtimeRoot,
    $processProfiler,
    $resolvedChunk,
    $resolvedCrossgen2)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required ReadyToRun experiment input does not exist: $required"
    }
}
if (-not $packagedLayout -and -not (Test-Path -LiteralPath $sourceCliProject)) {
    throw "UnifiedScript source CLI project does not exist: $sourceCliProject"
}
if (Test-Path -LiteralPath $outputRoot) {
    if (@(Get-ChildItem -LiteralPath $outputRoot -Force).Count -ne 0) {
        throw "ReadyToRun experiment output directory must be empty: $outputRoot"
    }
} else {
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
}

$resolvedReferences = @($ReferencePath | ForEach-Object {
    $resolved = [IO.Path]::GetFullPath($_)
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw "ReadyToRun experiment reference does not exist: $resolved"
    }
    $resolved
} | Sort-Object -Unique)

if (-not $SkipBuild -and -not $packagedLayout) {
    & $dotnet build $sourceCliProject -c $Configuration --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "UnifiedScript CLI build failed with exit code $LASTEXITCODE."
    }
} elseif ($packagedLayout) {
    Write-Host '[managed] Using the packaged UnifiedScript CLI.'
}
if (-not (Test-Path -LiteralPath $cliAssembly)) {
    throw "ReadyToRun experiment CLI does not exist: $cliAssembly"
}

& $dotnet $cliAssembly chunk-inspect $resolvedChunk | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Input chunk validation failed with exit code $LASTEXITCODE."
}

function Get-ManagedImageInfo([string]$Path) {
    Add-Type -AssemblyName System.Reflection.Metadata
    $stream = [IO.File]::OpenRead($Path)
    $reader = $null
    try {
        $reader = [Reflection.PortableExecutable.PEReader]::new($stream)
        if (-not $reader.HasMetadata -or $null -eq $reader.PEHeaders.CorHeader) {
            throw "Managed image contains no CLR metadata: $Path"
        }
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($reader)
        if (-not $metadata.IsAssembly) {
            throw "Managed image is not an assembly: $Path"
        }
        $assembly = $metadata.GetAssemblyDefinition()
        $module = $metadata.GetModuleDefinition()
        return [ordered]@{
            name = $metadata.GetString($assembly.Name)
            version = $assembly.Version.ToString()
            moduleVersionId = $metadata.GetGuid($module.Mvid).ToString('D')
            byteLength = [long]$stream.Length
            machine = $reader.PEHeaders.CoffHeader.Machine.ToString()
            managedNativeHeaderBytes = [long]$reader.PEHeaders.CorHeader.ManagedNativeHeaderDirectory.Size
            sha256 = 'sha256:' +
                (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    } finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        $stream.Dispose()
    }
}

$chunkBytes = [IO.File]::ReadAllBytes($resolvedChunk)
$sectionCount = [BitConverter]::ToUInt32($chunkBytes, 20)
if ($sectionCount -ne 5) {
    throw 'ReadyToRun experiment requires a Release chunk with exactly five runtime sections.'
}
$sectionNames = @{
    1 = 'runtime.bin'
    2 = 'program.il.dll'
    3 = 'declarations.bin'
    4 = 'bindings.bin'
    5 = 'dependencies.bin'
}
$sectionPaths = @{}
for ($index = 0; $index -lt $sectionCount; ++$index) {
    $entryOffset = 96 + ($index * 64)
    $kind = [int][BitConverter]::ToUInt32($chunkBytes, $entryOffset)
    $offset = [long][BitConverter]::ToUInt64($chunkBytes, $entryOffset + 8)
    $length = [long][BitConverter]::ToUInt64($chunkBytes, $entryOffset + 16)
    if (-not $sectionNames.ContainsKey($kind) -or
        $offset -lt 0 -or
        $length -le 0 -or
        $length -gt [int]::MaxValue -or
        $offset + $length -gt $chunkBytes.LongLength) {
        throw "ReadyToRun experiment found an invalid runtime section '$kind'."
    }
    $payload = [byte[]]::new([int]$length)
    [Array]::Copy($chunkBytes, [int]$offset, $payload, 0, [int]$length)
    $path = Join-Path $outputRoot $sectionNames[$kind]
    [IO.File]::WriteAllBytes($path, $payload)
    $sectionPaths[$kind] = $path
}
if ($sectionPaths.Count -ne 5) {
    throw 'ReadyToRun experiment could not extract all runtime sections.'
}

$runtimeDirectory = Get-ChildItem -LiteralPath $runtimeRoot -Directory |
    Sort-Object { [Version]$_.Name } -Descending |
    Select-Object -First 1
if ($null -eq $runtimeDirectory) {
    throw "No Microsoft.NETCore.App runtime exists under '$runtimeRoot'."
}
$crossgenVersion = Split-Path `
    (Split-Path -Parent $resolvedCrossgen2) `
    -Parent | Split-Path -Leaf
if ($crossgenVersion -ne $runtimeDirectory.Name) {
    throw "Crossgen2 '$crossgenVersion' must match runtime '$($runtimeDirectory.Name)'."
}

$ilAssembly = $sectionPaths[2]
$readyToRunAssembly = Join-Path $outputRoot 'program.r2r.dll'
$crossgenArguments = @(
    $ilAssembly,
    "--out:$readyToRunAssembly",
    '--targetarch:x64',
    '--targetos:windows',
    '--optimize-time')
$crossgenReferences = @(
    Get-ChildItem -LiteralPath $runtimeDirectory.FullName -Filter '*.dll' -File |
        ForEach-Object FullName)
$crossgenReferences += $resolvedReferences
foreach ($reference in @($crossgenReferences | Sort-Object -Unique)) {
    $crossgenArguments += "--reference:$reference"
}
& $resolvedCrossgen2 @crossgenArguments
if ($LASTEXITCODE -ne 0) {
    throw "Crossgen2 failed with exit code $LASTEXITCODE."
}

$ilImage = Get-ManagedImageInfo $ilAssembly
$readyToRunImage = Get-ManagedImageInfo $readyToRunAssembly
$identityMatches =
    $ilImage.name -eq $readyToRunImage.name -and
    $ilImage.version -eq $readyToRunImage.version -and
    $ilImage.moduleVersionId -eq $readyToRunImage.moduleVersionId
if (-not $identityMatches -or
    $ilImage.managedNativeHeaderBytes -ne 0 -or
    $readyToRunImage.managedNativeHeaderBytes -le 0 -or
    $readyToRunImage.machine -ne 'Amd64') {
    throw 'Crossgen2 output does not preserve the exact IL identity as an AMD64 ReadyToRun image.'
}

$readyToRunChunk = Join-Path $outputRoot 'program.r2r.uschunk'
& $dotnet $cliAssembly chunk-pack `
    --out $readyToRunChunk `
    --runtime-manifest $sectionPaths[1] `
    --assembly $readyToRunAssembly `
    --declaration $sectionPaths[3] `
    --bindings $sectionPaths[4] `
    --dependencies $sectionPaths[5]
if ($LASTEXITCODE -ne 0) {
    throw "ReadyToRun chunk pack failed with exit code $LASTEXITCODE."
}

$ilProfileDirectory = Join-Path $outputRoot 'il-processes'
$readyToRunProfileDirectory = Join-Path $outputRoot 'readytorun-processes'
& $processProfiler `
    -ChunkPath $resolvedChunk `
    -ReferencePath $resolvedReferences `
    -Trials $Trials `
    -OutputDirectory $ilProfileDirectory `
    -EngineRoot $EngineRoot `
    -Configuration $Configuration `
    -SkipBuild
if ($LASTEXITCODE -ne 0) {
    throw "IL process profiling failed with exit code $LASTEXITCODE."
}
& $processProfiler `
    -ChunkPath $readyToRunChunk `
    -ReferencePath $resolvedReferences `
    -Trials $Trials `
    -OutputDirectory $readyToRunProfileDirectory `
    -EngineRoot $EngineRoot `
    -Configuration $Configuration `
    -SkipBuild
if ($LASTEXITCODE -ne 0) {
    throw "ReadyToRun process profiling failed with exit code $LASTEXITCODE."
}

$ilProfile = Get-Content `
    -LiteralPath (Join-Path $ilProfileDirectory 'process-profile.json') `
    -Raw | ConvertFrom-Json
$readyToRunProfile = Get-Content `
    -LiteralPath (Join-Path $readyToRunProfileDirectory 'process-profile.json') `
    -Raw | ConvertFrom-Json
function Get-Phase([object]$Profile, [string]$Name) {
    $phase = @($Profile.medians.phases | Where-Object phase -EQ $Name)
    if ($phase.Count -ne 1) {
        throw "ReadyToRun experiment profile is missing phase '$Name'."
    }
    return $phase[0]
}
function Get-Ratio([long]$Numerator, [long]$Denominator) {
    if ($Denominator -le 0) {
        throw 'ReadyToRun experiment ratio denominator must be positive.'
    }
    return [Math]::Round($Numerator / [double]$Denominator, 6)
}

$ilJit = Get-Phase $ilProfile 'entryMethodJitPreparation'
$readyToRunJit = Get-Phase $readyToRunProfile 'entryMethodJitPreparation'
$loadRatio = Get-Ratio `
    $readyToRunProfile.medians.totalLoadNanoseconds `
    $ilProfile.medians.totalLoadNanoseconds
$profileRatio = Get-Ratio `
    $readyToRunProfile.medians.totalProfileNanoseconds `
    $ilProfile.medians.totalProfileNanoseconds
$jitRatio = Get-Ratio `
    $readyToRunJit.durationNanoseconds `
    $ilJit.durationNanoseconds
$sizeRatio = Get-Ratio $readyToRunProfile.byteLength $ilProfile.byteLength
$promote = $loadRatio -le 0.95 -and $jitRatio -le 0.95 -and $sizeRatio -le 1.5
$reasons = [Collections.Generic.List[string]]::new()
if ($loadRatio -gt 0.95) {
    $reasons.Add('cold-load improvement is below the 5 percent materiality threshold')
}
if ($jitRatio -gt 0.95) {
    $reasons.Add('entry-method preparation improvement is below the 5 percent threshold')
}
if ($sizeRatio -gt 1.5) {
    $reasons.Add('chunk size exceeds the 1.5x delivery budget')
}

$report = [ordered]@{
    schemaVersion = 'unifiedscript.readytorun-experiment/v0.1'
    experimentVersion = '1.0'
    loadMode = 'collectible-memory-stream'
    target = [ordered]@{
        os = 'windows'
        architecture = 'x64'
        dotnetRuntimeVersion = $runtimeDirectory.Name
        crossgen2Version = $crossgenVersion
    }
    processTrialsPerVariant = $Trials
    identity = [ordered]@{
        assemblyName = $ilImage.name
        assemblyVersion = $ilImage.version
        moduleVersionId = $ilImage.moduleVersionId
        exactMatch = $identityMatches
    }
    il = [ordered]@{
        chunkDigest = $ilProfile.contentDigest
        chunkBytes = [long]$ilProfile.byteLength
        assemblyDigest = $ilImage.sha256
        assemblyBytes = [long]$ilImage.byteLength
        machine = $ilImage.machine
        managedNativeHeaderBytes = [long]$ilImage.managedNativeHeaderBytes
        medianLoadNanoseconds = [long]$ilProfile.medians.totalLoadNanoseconds
        medianProfileNanoseconds = [long]$ilProfile.medians.totalProfileNanoseconds
        medianJitPreparationNanoseconds = [long]$ilJit.durationNanoseconds
        medianCopyBytes = [long]$ilProfile.medians.totalCopyBytes
    }
    readyToRun = [ordered]@{
        chunkDigest = $readyToRunProfile.contentDigest
        chunkBytes = [long]$readyToRunProfile.byteLength
        assemblyDigest = $readyToRunImage.sha256
        assemblyBytes = [long]$readyToRunImage.byteLength
        machine = $readyToRunImage.machine
        managedNativeHeaderBytes = [long]$readyToRunImage.managedNativeHeaderBytes
        medianLoadNanoseconds = [long]$readyToRunProfile.medians.totalLoadNanoseconds
        medianProfileNanoseconds = [long]$readyToRunProfile.medians.totalProfileNanoseconds
        medianJitPreparationNanoseconds = [long]$readyToRunJit.durationNanoseconds
        medianCopyBytes = [long]$readyToRunProfile.medians.totalCopyBytes
    }
    ratios = [ordered]@{
        readyToRunToIlColdLoad = $loadRatio
        readyToRunToIlTotalProfile = $profileRatio
        readyToRunToIlJitPreparation = $jitRatio
        readyToRunToIlChunkBytes = $sizeRatio
    }
    decision = [ordered]@{
        promoteReadyToRun = $promote
        maximumLoadRatio = 0.95
        maximumJitPreparationRatio = 0.95
        maximumChunkSizeRatio = 1.5
        reasons = @($reasons)
    }
}
$reportPath = Join-Path $outputRoot 'readytorun-experiment.json'
[IO.File]::WriteAllText(
    $reportPath,
    ($report | ConvertTo-Json -Depth 10) + "`n",
    [Text.UTF8Encoding]::new($false))

Write-Host (
    "ReadyToRun experiment: PASS (promote=$promote; loadRatio=$loadRatio; " +
    "jitRatio=$jitRatio; sizeRatio=$sizeRatio)")
Write-Output $reportPath
