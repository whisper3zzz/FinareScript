[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',
    [ValidateSet('Development', 'Shipping')]
    [string]$Configuration = 'Development',
    [string]$OutputDirectory,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runUat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\RunUAT.bat'
$dotnetExe = Join-Path $EngineRoot `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
$cliProject = Join-Path $repoRoot `
    'src\UnifiedScript.Cli\UnifiedScript.Cli.csproj'
$cliAssembly = Join-Path $repoRoot `
    'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll'
$artifactRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot 'artifacts\comparison\package-size'
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runRoot = Join-Path $artifactRoot $runId
$map = '/Game/BlockBreaker/Maps/TutorialMap'
$runtimeDefinitions = @(
    [pscustomobject]@{
        Id = 'puerts'
        Root = Join-Path $ComparisonRoot 'BlockBreaker-Puerts'
        ExtraCookDirectory = 'Content\Blueprints\TypeScript'
        PluginIntermediate = 'Plugins\Puerts\Intermediate'
    },
    [pscustomobject]@{
        Id = 'unifiedScript'
        Root = Join-Path $ComparisonRoot 'BlockBreaker-UnifiedScript'
        ExtraCookDirectory = 'Content\UnifiedScript'
        PluginIntermediate = 'Plugins\UEUnifiedScript\Intermediate'
    }
)

foreach ($requiredPath in @($runUat, $dotnetExe, $cliProject)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Package-size dependency does not exist: $requiredPath"
    }
}
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

function Build-UnifiedScriptManagedProgram {
    param([Parameter(Mandatory)][string]$ProjectRoot)

    $managedProject = Join-Path $ProjectRoot `
        'Managed\BlockBreaker.UnifiedScript.csproj'
    $managedRoot = Split-Path -Parent $managedProject
    $managedOutputs = @(
        (Join-Path $managedRoot 'BlockBreaker.UnifiedScript.dll'),
        (Join-Path $managedRoot 'BlockBreaker.UnifiedScript.deps.json'),
        (Join-Path $managedRoot 'BlockBreaker.UnifiedScript.uschunk'))
    if (-not (Test-Path -LiteralPath $managedProject -PathType Leaf)) {
        throw "UnifiedScript managed project does not exist: $managedProject"
    }

    Write-Output '[package-size] unifiedScript managed program build...'
    & $dotnetExe build $cliProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "UnifiedScript CLI build failed with exit code $LASTEXITCODE."
    }
    & $dotnetExe build $managedProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "BlockBreaker managed build failed with exit code $LASTEXITCODE."
    }
    & $dotnetExe $cliAssembly emit $managedProject `
        --out $managedRoot `
        --configuration Release `
        --no-restore `
        --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "BlockBreaker chunk emission failed with exit code $LASTEXITCODE."
    }
    foreach ($managedOutput in $managedOutputs) {
        if (-not (Test-Path -LiteralPath $managedOutput -PathType Leaf)) {
            throw "BlockBreaker managed package output is missing: $managedOutput"
        }
    }
}

function Get-ArchivedFiles {
    param([Parameter(Mandatory)][string]$ArchiveDirectory)

    $resolvedArchive = [IO.Path]::GetFullPath($ArchiveDirectory)
    $enumerationRoot = if ($env:OS -eq 'Windows_NT') {
        if ($resolvedArchive.StartsWith('\\')) {
            '\\?\UNC\' + $resolvedArchive.TrimStart('\')
        } else {
            '\\?\' + $resolvedArchive
        }
    } else {
        $resolvedArchive
    }

    for ($attempt = 1; $attempt -le 3; ++$attempt) {
        try {
            return @(
                [IO.Directory]::EnumerateFiles(
                    $enumerationRoot,
                    '*',
                    [IO.SearchOption]::AllDirectories) |
                    ForEach-Object { [IO.FileInfo]::new($_) })
        } catch [IO.DirectoryNotFoundException] {
            if ($attempt -eq 3) {
                throw
            }
            Start-Sleep -Milliseconds 200
        }
    }
}

$measurements = [ordered]@{}
foreach ($runtime in $runtimeDefinitions) {
    $projectPath = Join-Path $runtime.Root 'BlockBreaker.uproject'
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "BlockBreaker project does not exist: $projectPath"
    }
    if ($runtime.Id -eq 'unifiedScript') {
        Build-UnifiedScriptManagedProgram -ProjectRoot $runtime.Root
    }
    $trackedStatus = @(& git -C $runtime.Root status --porcelain --untracked-files=no)
    if ($trackedStatus.Count -ne 0) {
        throw "$($runtime.Id) has tracked working-tree changes; commit them before packaging."
    }

    $archiveDirectory = Join-Path $runRoot $runtime.Id
    $cookDirectories = @(
        (Join-Path $runtime.Root 'Content\BlockBreaker'),
        (Join-Path $runtime.Root $runtime.ExtraCookDirectory)
    ) -join '+'
    $uatArguments = @(
        'BuildCookRun',
        "-project=$projectPath",
        '-noP4', '-platform=Win64', "-clientconfig=$Configuration",
        '-cook', '-stage', '-pak', '-archive',
        "-archivedirectory=$archiveDirectory",
        "-map=$map",
        "-CookDir=$cookDirectories",
        '-AdditionalCookerOptions=-SkipZenStore',
        '-unattended', '-utf8output', '-NoCodeSign', '-NoXGE',
        '-nocompileeditor', '-clean'
    )
    if (-not $SkipBuild) {
        $uatArguments += '-build'
        $runtimeRoot = [IO.Path]::GetFullPath($runtime.Root).TrimEnd('\', '/') +
            [IO.Path]::DirectorySeparatorChar
        $pluginIntermediate = [IO.Path]::GetFullPath((Join-Path `
            $runtime.Root $runtime.PluginIntermediate))
        if (-not $pluginIntermediate.StartsWith(
                $runtimeRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe plugin Intermediate path: $pluginIntermediate"
        }
        if (Test-Path -LiteralPath $pluginIntermediate -PathType Container) {
            Remove-Item -LiteralPath $pluginIntermediate -Recurse -Force
        }
    }
    $orderLogSnapshots = @(
        'Build\Windows\FileOpenOrder\CookerOpenOrder.log',
        'Build\Windows\FileOpenOrder\EditorOpenOrder.log' |
            ForEach-Object {
                $path = Join-Path $runtime.Root $_
                [pscustomobject]@{
                    Path = $path
                    Existed = Test-Path -LiteralPath $path -PathType Leaf
                    Bytes = if (Test-Path -LiteralPath $path -PathType Leaf) {
                        [IO.File]::ReadAllBytes($path)
                    } else { $null }
                }
            })
    try {
        Write-Output "[package-size] $($runtime.Id) Build/Cook/Stage/Pak/Archive..."
        & $runUat @uatArguments
        if ($LASTEXITCODE -ne 0) {
            throw "$($runtime.Id) packaging failed with exit code $LASTEXITCODE."
        }
    } finally {
        foreach ($snapshot in $orderLogSnapshots) {
            if ($snapshot.Existed) {
                [IO.File]::WriteAllBytes($snapshot.Path, $snapshot.Bytes)
            } elseif (Test-Path -LiteralPath $snapshot.Path -PathType Leaf) {
                Remove-Item -LiteralPath $snapshot.Path -Force
            }
        }
    }

    $files = @(Get-ArchivedFiles -ArchiveDirectory $archiveDirectory)
    if ($files.Count -eq 0) {
        throw "$($runtime.Id) package archive is empty: $archiveDirectory"
    }
    $bootstrap = $files | Where-Object Name -eq 'BlockBreaker.exe' |
        Select-Object -First 1
    if ($null -eq $bootstrap) {
        throw "$($runtime.Id) package is missing BlockBreaker.exe."
    }
    $manifestFiles = @($files | Where-Object Name -like 'Manifest_*.txt')
    $manifestText = @($manifestFiles | ForEach-Object {
        [IO.File]::ReadAllText($_.FullName)
    }) -join [Environment]::NewLine
    if ($runtime.Id -eq 'puerts') {
        if ($manifestText -notmatch '(?i)(^|[/\\])Puerts([/\\]|\.)' -or
            $manifestText -notmatch '(?i)(^|[/\\])TypeScript([/\\]|\.)') {
            throw 'Puerts package does not contain its runtime/script payload.'
        }
    } else {
        if ($manifestText -match '(?i)(^|[/\\])(Puerts|JavaScript|TypeScript)([/\\]|\.)') {
            throw 'UnifiedScript package contains a forbidden Puerts/JavaScript/TypeScript payload.'
        }
        if (-not ($files | Where-Object Name -eq 'coreclr.dll')) {
            throw 'UnifiedScript package does not contain coreclr.dll.'
        }
    }

    $measurements[$runtime.Id] = [ordered]@{
        commit = (& git -C $runtime.Root rev-parse HEAD).Trim()
        trackedWorkingTreeDirty = $false
        archiveDirectory = $archiveDirectory
        fileCount = $files.Count
        packageSizeBytes = [uint64](($files | Measure-Object Length -Sum).Sum)
        bootstrapExecutable = $bootstrap.FullName
        bootstrapSha256 = (Get-FileHash -LiteralPath $bootstrap.FullName `
            -Algorithm SHA256).Hash
    }
}

$report = [ordered]@{
    schemaVersion = 'unifiedscript.blockbreaker-package-size/v0.1'
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    engineRoot = $EngineRoot
    engineVersion = '5.8.1'
    platform = 'Win64'
    configuration = $Configuration
    map = $map
    measurement = 'sum of every archived delivery file after matched BuildCookRun'
    puerts = $measurements.puerts
    unifiedScript = $measurements.unifiedScript
    ratio = [ordered]@{
        unifiedScriptToPuertsPackageSize =
            [double]$measurements.unifiedScript.packageSizeBytes /
            [double]$measurements.puerts.packageSizeBytes
        lowerIsBetter = $true
    }
}
$reportPath = Join-Path $runRoot 'package-size.json'
$latestPath = Join-Path $artifactRoot 'latest.json'
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $latestPath -Encoding utf8

Write-Output "Puerts package:       $($report.puerts.packageSizeBytes) bytes"
Write-Output "UnifiedScript package: $($report.unifiedScript.packageSizeBytes) bytes"
Write-Output "Package ratio (UnifiedScript/Puerts): $($report.ratio.unifiedScriptToPuertsPackageSize)"
Write-Output "Report: $reportPath"
