[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0-m6.3-alpha.1',

    [string]$OutputDirectory,

    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,

    [string]$DotNet,

    [ValidateSet('Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginRoot = Join-Path $repoRoot 'HostProject\Plugins\UEUnifiedScript'
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot 'artifacts\sdk'
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
$archiveBaseName = "UnifiedScript-SDK-$Version-win-x64"
$archivePath = Join-Path $outputRoot "$archiveBaseName.zip"
$reportPath = Join-Path $outputRoot "$archiveBaseName.package-report.json"
$stagingParent = Join-Path $outputRoot ".staging-$([Guid]::NewGuid().ToString('N'))"
$sdkRoot = Join-Path $stagingParent $archiveBaseName
$startedAt = [DateTime]::UtcNow

function Throw-SdkError {
    param([string]$Code, [string]$Message)

    throw [InvalidOperationException]::new("[$Code] $Message")
}

function Resolve-EngineAssociation {
    param([string]$Association)

    $customBuildsKey = 'HKCU:\Software\Epic Games\Unreal Engine\Builds'
    if (Test-Path $customBuildsKey) {
        $customBuilds = Get-ItemProperty -Path $customBuildsKey
        $value = $customBuilds.PSObject.Properties |
            Where-Object Name -EQ $Association |
            Select-Object -ExpandProperty Value -First 1
        if (-not [string]::IsNullOrWhiteSpace($value)) { return [string]$value }
    }
    foreach ($root in @(
        'HKLM:\SOFTWARE\EpicGames\Unreal Engine',
        'HKLM:\SOFTWARE\WOW6432Node\EpicGames\Unreal Engine')) {
        $versionKey = Join-Path $root $Association
        if (Test-Path $versionKey) {
            $installed = (Get-ItemProperty -Path $versionKey).InstalledDirectory
            if (-not [string]::IsNullOrWhiteSpace($installed)) { return [string]$installed }
        }
    }
    return $null
}

function Get-RelativeFilePath {
    param([string]$BaseDirectory, [string]$Path)

    $basePath = [IO.Path]::GetFullPath($BaseDirectory).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    $baseUri = [Uri]$basePath
    $pathUri = [Uri][IO.Path]::GetFullPath($Path)
    return [Uri]::UnescapeDataString(
        $baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

function Invoke-CheckedCommand {
    param([string]$FilePath, [object[]]$Arguments, [string]$Label)

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        Throw-SdkError 'USSDK2001' "$Label failed with exit code $LASTEXITCODE."
    }
}

function Copy-PayloadTree {
    param([string]$Source, [string]$Destination)

    if (-not (Test-Path -LiteralPath $Source)) {
        Throw-SdkError 'USSDK1001' "Required SDK source is missing: $Source"
    }
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File) {
        $relative = Get-RelativeFilePath -BaseDirectory $Source -Path $file.FullName
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) |
            Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

function Assert-NoMachinePathLeak {
    param([string]$Root)

    $needles = @(
        [IO.Path]::GetFullPath($repoRoot),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    $textExtensions = @(
        '.cs', '.cpp', '.h', '.json', '.md', '.ps1', '.props', '.targets',
        '.csproj', '.uplugin', '.txt')
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File) {
        if ($file.Extension.ToLowerInvariant() -notin $textExtensions) { continue }
        $text = [IO.File]::ReadAllText($file.FullName)
        foreach ($needle in $needles) {
            if ($text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $relative = Get-RelativeFilePath -BaseDirectory $Root -Path $file.FullName
                Throw-SdkError `
                    'USSDK1201' `
                    "Machine-specific absolute path leaked into SDK payload '$relative'."
            }
        }
    }
}

function Write-DeterministicArchive {
    param([string]$SourceRoot, [string]$Destination, [string]$EntryRoot)

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Force
    }
    $stream = [IO.File]::Open(
        $Destination,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            $files = @(Get-ChildItem -LiteralPath $SourceRoot -Recurse -File |
                Sort-Object { Get-RelativeFilePath -BaseDirectory $SourceRoot -Path $_.FullName })
            foreach ($file in $files) {
                $relative = (Get-RelativeFilePath `
                    -BaseDirectory $SourceRoot `
                    -Path $file.FullName).Replace('\', '/')
                $entry = $archive.CreateEntry(
                    "$EntryRoot/$relative",
                    [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(
                    2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $entryStream = $entry.Open()
                try {
                    $input = [IO.File]::OpenRead($file.FullName)
                    try { $input.CopyTo($entryStream) } finally { $input.Dispose() }
                } finally {
                    $entryStream.Dispose()
                }
            }
        } finally {
            $archive.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $sdkRoot | Out-Null

    $resolvedDotNet = $DotNet
    if ([string]::IsNullOrWhiteSpace($resolvedDotNet)) {
        $resolvedEngineRoot = $EngineRoot
        if ([string]::IsNullOrWhiteSpace($resolvedEngineRoot)) {
            $resolvedEngineRoot = Resolve-EngineAssociation -Association '5.8'
        }
        if (-not [string]::IsNullOrWhiteSpace($resolvedEngineRoot)) {
            $engineDotNet = Join-Path $resolvedEngineRoot `
                'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
            if (Test-Path -LiteralPath $engineDotNet -PathType Leaf) {
                $resolvedDotNet = $engineDotNet
            }
        }
        if ([string]::IsNullOrWhiteSpace($resolvedDotNet)) {
            $resolvedDotNet = 'dotnet'
        }
    }

    foreach ($required in @(
        (Join-Path $pluginRoot 'UEUnifiedScript.uplugin'),
        (Join-Path $pluginRoot 'Source'),
        (Join-Path $pluginRoot 'Resources'),
        (Join-Path $repoRoot 'src\UnifiedScript.Cli\UnifiedScript.Cli.csproj'),
        (Join-Path $repoRoot 'src\UnifiedScript.Unreal\UnifiedScript.Unreal.csproj'),
        (Join-Path $repoRoot 'tools\setup-unreal-project.ps1'))) {
        if (-not (Test-Path -LiteralPath $required)) {
            Throw-SdkError 'USSDK1001' "Required SDK source is missing: $required"
        }
    }

    $cliPublish = Join-Path $stagingParent 'cli-publish'
    $managedRuntime = Join-Path $sdkRoot 'Managed\Runtime'
    New-Item -ItemType Directory -Force -Path $cliPublish, $managedRuntime | Out-Null
    Write-Host '[managed] Publishing framework-dependent CLI and runtime payloads...'
    Invoke-CheckedCommand `
        -FilePath $resolvedDotNet `
        -Arguments @(
            'publish',
            (Join-Path $repoRoot 'src\UnifiedScript.Cli\UnifiedScript.Cli.csproj'),
            '-c', $Configuration,
            '--nologo',
            '--no-self-contained',
            '-o', $cliPublish,
            "-p:UnifiedScriptManagedStageDir=$managedRuntime") `
        -Label 'Managed SDK publish'
    Copy-PayloadTree `
        -Source $cliPublish `
        -Destination (Join-Path $sdkRoot 'Tools\UnifiedScript.Cli')

    Write-Host '[plugin] Copying engine-independent source plugin...'
    $packagedPlugin = Join-Path $sdkRoot 'Plugins\UEUnifiedScript'
    Copy-PayloadTree `
        -Source (Join-Path $pluginRoot 'Source') `
        -Destination (Join-Path $packagedPlugin 'Source')
    Copy-PayloadTree `
        -Source (Join-Path $pluginRoot 'Resources') `
        -Destination (Join-Path $packagedPlugin 'Resources')
    $pluginDescriptor = Get-Content `
        -LiteralPath (Join-Path $pluginRoot 'UEUnifiedScript.uplugin') `
        -Raw | ConvertFrom-Json
    $pluginDescriptor.VersionName = $Version
    $pluginDescriptor.Installed = $true
    [IO.File]::WriteAllText(
        (Join-Path $packagedPlugin 'UEUnifiedScript.uplugin'),
        ($pluginDescriptor | ConvertTo-Json -Depth 10) + "`n",
        [Text.UTF8Encoding]::new($false))

    Write-Host '[tools] Copying onboarding, installer, schemas, templates, and documentation...'
    $toolDirectory = Join-Path $sdkRoot 'Tools'
    New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
    foreach ($tool in @(
        'setup-unreal-project.ps1',
        'install-unifiedscript-sdk.ps1',
        'new-unifiedscript-project.ps1',
        'start-unifiedscript-authoring.ps1')) {
        $source = Join-Path $repoRoot "tools\$tool"
        if (-not (Test-Path -LiteralPath $source)) {
            Throw-SdkError 'USSDK1001' "Required SDK tool is missing: $source"
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $toolDirectory $tool) -Force
    }
    $schemaDirectory = Join-Path $sdkRoot 'Schemas'
    $templateDirectory = Join-Path $sdkRoot 'Templates\HostProject'
    New-Item -ItemType Directory -Force -Path $schemaDirectory, $templateDirectory |
        Out-Null
    $schemaSources = [ordered]@{
        'unreal-reflection.v0.2.example.json' = 'samples\Unreal\UEUnifiedScriptRuntime.reflection.json'
        'unreal-api-contract.v0.1.example.json' = 'samples\Unreal\UEUnifiedScriptRuntime.api-contract.json'
        'unreal-api-nodes.v0.1.example.json' = 'samples\Unreal\UEUnifiedScriptRuntime.api-nodes.json'
        'unreal-binding-coverage.v0.1.example.json' = 'samples\Unreal\UEUnifiedScriptRuntime.binding-coverage.json'
        'unreal-script-deployment.v0.1.example.json' = 'samples\Unreal\UnifiedScript.Unreal.Demo.deployment.json'
    }
    foreach ($entry in $schemaSources.GetEnumerator()) {
        Copy-Item `
            -LiteralPath (Join-Path $repoRoot $entry.Value) `
            -Destination (Join-Path $schemaDirectory $entry.Key) `
            -Force
    }
    Copy-Item `
        -LiteralPath (Join-Path $repoRoot 'HostProject\Config\UnifiedScript.onboarding.json') `
        -Destination (Join-Path $templateDirectory 'UnifiedScript.onboarding.json') `
        -Force
    Copy-Item `
        -LiteralPath (Join-Path $repoRoot 'samples\Unreal\UnifiedScript.Unreal.Demo.deployment.json') `
        -Destination (Join-Path $templateDirectory 'UnifiedScript.deployment.json') `
        -Force
    Copy-Item `
        -LiteralPath (Join-Path $repoRoot 'samples\Unreal\UEUnifiedScriptRuntime.api-contract.json') `
        -Destination (Join-Path $templateDirectory 'UnifiedScript.api-contract.json') `
        -Force
    Copy-PayloadTree `
        -Source (Join-Path $repoRoot 'templates\UnrealProject') `
        -Destination (Join-Path $sdkRoot 'Templates\UnrealProject')
    Copy-PayloadTree `
        -Source (Join-Path $repoRoot 'prototype\visual-editor') `
        -Destination (Join-Path $sdkRoot 'Authoring\Workbench')
    $documentationDirectory = Join-Path $sdkRoot 'Documentation'
    New-Item -ItemType Directory -Force -Path $documentationDirectory | Out-Null
    foreach ($document in @(
        'UNREAL_SDK.md',
        'UNREAL_FIRST_SCRIPT.md',
        'UNREAL_ONBOARDING.md',
        'UNREAL_API_CONTRACT.md',
        'UNREAL_DEPLOYMENT.md')) {
        $source = Join-Path $repoRoot "docs\$document"
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination $documentationDirectory -Force
        }
    }

    foreach ($requiredPayload in @(
        'Tools\setup-unreal-project.ps1',
        'Tools\install-unifiedscript-sdk.ps1',
        'Tools\new-unifiedscript-project.ps1',
        'Tools\start-unifiedscript-authoring.ps1',
        'Tools\UnifiedScript.Cli\UnifiedScript.Cli.dll',
        'Tools\UnifiedScript.Cli\UnifiedScript.Cli.runtimeconfig.json',
        'Managed\Runtime\UnifiedScript.Core.dll',
        'Managed\Runtime\UnifiedScript.Unreal.dll',
        'Managed\Runtime\UnifiedScript.Unreal.deps.json',
        'Plugins\UEUnifiedScript\UEUnifiedScript.uplugin',
        'Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime',
        'Plugins\UEUnifiedScript\Source\UEUnifiedScriptEditor',
        'Plugins\UEUnifiedScript\Resources\Managed\UnifiedScript.Unreal.runtimeconfig.json',
        'Templates\UnrealProject\Managed\FirstScript.cs.template',
        'Templates\UnrealProject\Config\UnifiedScript.onboarding.json.template',
        'Authoring\Workbench\index.html')) {
        $candidate = Join-Path $sdkRoot $requiredPayload
        if (-not (Test-Path -LiteralPath $candidate)) {
            Throw-SdkError 'USSDK1101' "SDK payload is incomplete: $requiredPayload"
        }
    }
    if (Test-Path -LiteralPath (Join-Path $packagedPlugin 'Binaries')) {
        Throw-SdkError 'USSDK1102' 'Engine-version-specific plugin Binaries leaked into the SDK.'
    }
    if (Test-Path -LiteralPath (Join-Path $packagedPlugin 'Intermediate')) {
        Throw-SdkError 'USSDK1102' 'Plugin Intermediate output leaked into the SDK.'
    }
    Assert-NoMachinePathLeak -Root $sdkRoot

    $payloadFiles = @(
        Get-ChildItem -LiteralPath $sdkRoot -Recurse -File |
            ForEach-Object {
                [ordered]@{
                    path = (Get-RelativeFilePath `
                        -BaseDirectory $sdkRoot `
                        -Path $_.FullName).Replace('\', '/')
                    size = [long]$_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            } | Sort-Object path)
    $sourceRevision = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { $sourceRevision = 'unknown' }
    $sourceTreeDirty = @(& git -C $repoRoot status --porcelain --untracked-files=no).Count -ne 0
    $manifest = [ordered]@{
        schemaVersion = 'unifiedscript.sdk-manifest/v0.1'
        sdkVersion = $Version
        targetPlatform = 'win-x64'
        sourceRevision = $sourceRevision
        sourceTreeDirty = $sourceTreeDirty
        compatibility = [ordered]@{
            engineVersions = @('5.8')
            managedTargetFramework = 'net10.0'
            abi = [ordered]@{
                bootstrap = 2
                fastMember = 2
                callFrame = 3
                event = 4
                sharedData = 4
                gameplayData = 4
            }
            schemas = [ordered]@{
                onboarding = 'unifiedscript.unreal-project-onboarding/v0.1'
                reflection = 'unifiedscript.unreal-reflection/v0.2'
                apiContract = 'unifiedscript.unreal-api-contract/v0.1'
                apiNodeCatalog = 'unifiedscript.unreal-api-node-catalog/v0.1'
                bindingCoverage = 'unifiedscript.unreal-binding-coverage/v0.1'
                deployment = 'unifiedscript.unreal-script-deployment/v0.1'
            }
        }
        payload = [ordered]@{
            fileCount = $payloadFiles.Count
            files = $payloadFiles
        }
    }
    $manifestPath = Join-Path $sdkRoot 'sdk-manifest.json'
    [IO.File]::WriteAllText(
        $manifestPath,
        ($manifest | ConvertTo-Json -Depth 10) + "`n",
        [Text.UTF8Encoding]::new($false))

    Write-Host '[archive] Writing deterministic SDK archive...'
    Write-DeterministicArchive `
        -SourceRoot $sdkRoot `
        -Destination $archivePath `
        -EntryRoot $archiveBaseName
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $report = [ordered]@{
        schemaVersion = 'unifiedscript.sdk-package-report/v0.1'
        status = 'passed'
        sdkVersion = $Version
        sourceRevision = $sourceRevision
        archive = $archivePath
        archiveSha256 = $archiveHash
        payloadFileCount = $payloadFiles.Count
        archiveBytes = [long](Get-Item -LiteralPath $archivePath).Length
        durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
    }
    [IO.File]::WriteAllText(
        $reportPath,
        ($report | ConvertTo-Json -Depth 6) + "`n",
        [Text.UTF8Encoding]::new($false))
    Write-Host "UnifiedScript SDK package: PASS ($archiveHash)"
    Write-Host $archivePath
} finally {
    if (Test-Path -LiteralPath $stagingParent) {
        $resolvedStage = [IO.Path]::GetFullPath($stagingParent)
        $resolvedOutput = [IO.Path]::GetFullPath($outputRoot).TrimEnd('\', '/') +
            [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedStage.StartsWith($resolvedOutput, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove staging directory outside SDK output: $resolvedStage"
        }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
