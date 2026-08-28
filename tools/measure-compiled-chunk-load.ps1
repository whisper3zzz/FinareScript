[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ChunkPath,

    [string[]]$ReferencePath = @(),

    [ValidateRange(3, 15)]
    [int]$Trials = 5,

    [string]$OutputDirectory,

    [string]$EngineRoot = 'D:\UE_5.8',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipBuild,

    [switch]$NoJitPrepare
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path `
    $EngineRoot `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
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
$resolvedChunk = [IO.Path]::GetFullPath($ChunkPath)
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path `
        $repoRoot `
        "artifacts\chunk-profile\process-$((Get-Date).ToString('yyyyMMdd-HHmmss'))"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}

foreach ($required in @($dotnet, $resolvedChunk)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required compiled-chunk profile input does not exist: $required"
    }
}
if (-not $packagedLayout -and -not (Test-Path -LiteralPath $sourceCliProject)) {
    throw "UnifiedScript source CLI project does not exist: $sourceCliProject"
}
$resolvedReferences = @($ReferencePath | ForEach-Object {
    $resolved = [IO.Path]::GetFullPath($_)
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw "Compiled-chunk profile reference does not exist: $resolved"
    }
    $resolved
} | Sort-Object -Unique)

if (Test-Path -LiteralPath $outputRoot) {
    if (@(Get-ChildItem -LiteralPath $outputRoot -Force).Count -ne 0) {
        throw "Compiled-chunk profile output directory must be empty: $outputRoot"
    }
} else {
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
}

if (-not $SkipBuild -and -not $packagedLayout) {
    & $dotnet build $sourceCliProject -c $Configuration --nologo --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw "UnifiedScript CLI build failed with exit code $LASTEXITCODE."
    }
} elseif ($packagedLayout) {
    Write-Host '[managed] Using the packaged UnifiedScript CLI.'
}
if (-not (Test-Path -LiteralPath $cliAssembly)) {
    throw "Compiled-chunk profiler CLI does not exist: $cliAssembly"
}

function Get-Int64Median([long[]]$Values) {
    if ($Values.Count -eq 0) {
        throw 'A compiled-chunk profile median requires at least one value.'
    }
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 1) {
        return [long]$ordered[$middle]
    }
    return [long](([long]$ordered[$middle - 1] + [long]$ordered[$middle]) / 2)
}

$measurements = [Collections.Generic.List[object]]::new()
$expectedDigest = $null
$expectedFormat = $null
$expectedByteLength = $null
for ($trial = 1; $trial -le $Trials; ++$trial) {
    $trialName = 'trial-{0:D2}.json' -f $trial
    $trialPath = Join-Path $outputRoot $trialName
    $arguments = @(
        $cliAssembly,
        'chunk-profile',
        $resolvedChunk,
        '--out',
        $trialPath,
        '--trials',
        '1')
    foreach ($reference in $resolvedReferences) {
        $arguments += @('--reference', $reference)
    }
    if ($NoJitPrepare) {
        $arguments += '--no-jit-prepare'
    }

    Write-Host "[process $trial/$Trials] $trialName"
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Compiled-chunk profile trial $trial failed with exit code $LASTEXITCODE."
    }
    $profile = Get-Content -LiteralPath $trialPath -Raw | ConvertFrom-Json
    if ($profile.schemaVersion -ne 'unifiedscript.compiled-chunk-load-profile/v0.1' -or
        [int]$profile.trialCount -ne 1 -or
        -not [bool]$profile.trials[0].processFirstLoad -or
        [bool]$profile.entryMethodJitPreparation -eq [bool]$NoJitPrepare) {
        throw "Compiled-chunk profile trial $trial returned an incompatible report."
    }
    if ($null -eq $expectedDigest) {
        $expectedDigest = [string]$profile.contentDigest
        $expectedFormat = [string]$profile.chunkFormatVersion
        $expectedByteLength = [int]$profile.byteLength
    } elseif ($profile.contentDigest -ne $expectedDigest -or
        $profile.chunkFormatVersion -ne $expectedFormat -or
        [int]$profile.byteLength -ne $expectedByteLength) {
        throw "Compiled chunk changed between isolated process trials."
    }

    $measurement = $profile.trials[0]
    $measurements.Add([pscustomobject][ordered]@{
        trial = $trial
        report = $trialName
        totalLoadNanoseconds = [long]$measurement.totalLoadNanoseconds
        totalProfileNanoseconds = [long]$measurement.totalProfileNanoseconds
        currentThreadAllocatedBytes = [long]$measurement.currentThreadAllocatedBytes
        totalCopyBytes = [long]$measurement.totalCopyBytes
        phases = @($measurement.phases)
    })
}

$phaseNames = @($measurements[0].phases | ForEach-Object { [string]$_.phase })
$medianPhases = @($phaseNames | ForEach-Object {
    $phaseName = $_
    $values = @($measurements | ForEach-Object {
        @($_.phases | Where-Object phase -EQ $phaseName | Select-Object -First 1)[0]
    })
    [ordered]@{
        phase = $phaseName
        durationNanoseconds = Get-Int64Median @(
            $values | ForEach-Object { [long]$_.durationNanoseconds })
        bytesRead = Get-Int64Median @(
            $values | ForEach-Object { [long]$_.bytesRead })
        bytesCopied = Get-Int64Median @(
            $values | ForEach-Object { [long]$_.bytesCopied })
        workItems = Get-Int64Median @(
            $values | ForEach-Object { [long]$_.workItems })
    }
})

$report = [ordered]@{
    schemaVersion = 'unifiedscript.compiled-chunk-process-profile/v0.1'
    profilerVersion = '1.0'
    chunkFormatVersion = $expectedFormat
    contentDigest = $expectedDigest
    byteLength = $expectedByteLength
    processTrialCount = $Trials
    entryMethodJitPreparation = -not [bool]$NoJitPrepare
    dotnetSdkVersion = (& $dotnet --version).Trim()
    cliSha256 = 'sha256:' + (
        Get-FileHash -Algorithm SHA256 -LiteralPath $cliAssembly
    ).Hash.ToLowerInvariant()
    medians = [ordered]@{
        totalLoadNanoseconds = Get-Int64Median @(
            $measurements | ForEach-Object { [long]$_.totalLoadNanoseconds })
        totalProfileNanoseconds = Get-Int64Median @(
            $measurements | ForEach-Object { [long]$_.totalProfileNanoseconds })
        currentThreadAllocatedBytes = Get-Int64Median @(
            $measurements | ForEach-Object { [long]$_.currentThreadAllocatedBytes })
        totalCopyBytes = Get-Int64Median @(
            $measurements | ForEach-Object { [long]$_.totalCopyBytes })
        phases = $medianPhases
    }
    trials = @($measurements)
}
$reportPath = Join-Path $outputRoot 'process-profile.json'
$temporaryPath = "$reportPath.tmp-$([Guid]::NewGuid().ToString('N'))"
try {
    $json = $report | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($temporaryPath, $json + "`n", [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $reportPath
} finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

Write-Output (
    'Compiled chunk process profile: PASS ' +
    "(medianLoadNs=$($report.medians.totalLoadNanoseconds), " +
    "medianProfileNs=$($report.medians.totalProfileNanoseconds), " +
    "copyBytes=$($report.medians.totalCopyBytes))")
Write-Output $reportPath
