[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0-rc.1',

    [string]$OutputDirectory,

    [string]$EngineRoot = $env:FINARE_UE_ROOT,

    [string]$DotNet,

    [ValidateSet('Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$pluginRoot = Join-Path $repoRoot 'HostProject\Plugins\Finare'
$releaseManifestSource = Join-Path $repoRoot 'release\finare.release.json'
if (-not (Test-Path -LiteralPath $releaseManifestSource -PathType Leaf)) {
    throw "[USSDK1001] Required release manifest is missing: $releaseManifestSource"
}
$releaseManifest = Get-Content -LiteralPath $releaseManifestSource -Raw |
    ConvertFrom-Json
if ($releaseManifest.schemaVersion -ne 'unifiedscript.product-release/v0.1') {
    throw "[USSDK1002] Unsupported product release manifest '$($releaseManifest.schemaVersion)'."
}
if ($releaseManifest.productVersion -ne $Version) {
    throw (
        "[USSDK1002] SDK version '$Version' does not match release manifest " +
        "version '$($releaseManifest.productVersion)'.")
}
if ($releaseManifest.status -ne 'releaseCandidate' -or
    $releaseManifest.channel -ne 'rc' -or
    $releaseManifest.compatibility.platform -ne 'win-x64' -or
    $releaseManifest.compatibility.engineFamily -ne '5.8' -or
    $releaseManifest.compatibility.verifiedEngineVersion -ne '5.8.1' -or
    $releaseManifest.compatibility.managedTargetFramework -ne 'net10.0') {
    throw '[USSDK1002] Product release status or compatibility declaration is incomplete.'
}
$expectedArchiveName = "Finare-SDK-$Version-win-x64.zip"
if ($releaseManifest.artifacts.sdkArchive -ne $expectedArchiveName -or
    $releaseManifest.artifacts.pluginVersionName -ne $Version) {
    throw '[USSDK1002] Product release artifact names do not match the requested SDK version.'
}
$requiredDocumentationCategories = @(
    'index', 'sdk', 'onboarding', 'authoring', 'api', 'debugging',
    'performance', 'packaging', 'upgrades', 'migration', 'support',
    'releaseNotes')
$releaseDocumentation = @($releaseManifest.documentation)
if ($releaseDocumentation.Count -eq 0) {
    throw '[USSDK1002] Product release documentation catalog is empty.'
}
$documentIds = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
$documentNames = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$documentationRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'docs')).TrimEnd(
    '\', '/') + [IO.Path]::DirectorySeparatorChar
foreach ($document in $releaseDocumentation) {
    if ([string]::IsNullOrWhiteSpace($document.id) -or
        [string]::IsNullOrWhiteSpace($document.title) -or
        [string]::IsNullOrWhiteSpace($document.category) -or
        [string]::IsNullOrWhiteSpace($document.path) -or
        -not $documentIds.Add([string]$document.id)) {
        throw '[USSDK1002] Product release documentation IDs and fields must be unique and non-empty.'
    }
    $normalizedDocumentPath = ([string]$document.path).Replace('\', '/')
    if ($normalizedDocumentPath -notmatch '^docs/[0-9A-Za-z_.-]+\.md$') {
        throw "[USSDK1002] Unsafe release documentation path '$normalizedDocumentPath'."
    }
    $sourceDocument = [IO.Path]::GetFullPath((Join-Path $repoRoot $normalizedDocumentPath))
    $documentName = [IO.Path]::GetFileName($sourceDocument)
    if (-not $sourceDocument.StartsWith(
            $documentationRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $sourceDocument -PathType Leaf) -or
        -not $documentNames.Add($documentName)) {
        throw "[USSDK1002] Missing, duplicate, or out-of-root release document '$normalizedDocumentPath'."
    }
}
foreach ($category in $requiredDocumentationCategories) {
    if ($category -notin @($releaseDocumentation.category)) {
        throw "[USSDK1002] Release documentation category '$category' is missing."
    }
}
if ($releaseManifest.releaseNotes -notin @($releaseDocumentation.path)) {
    throw '[USSDK1002] Release notes must be part of the published documentation catalog.'
}
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot 'artifacts\sdk'
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
$archiveBaseName = "Finare-SDK-$Version-win-x64"
$archivePath = Join-Path $outputRoot "$archiveBaseName.zip"
$reportPath = Join-Path $outputRoot "$archiveBaseName.package-report.json"
$stagingRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stagingParent = Join-Path `
    $stagingRoot `
    "us-sdk-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
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
        (Join-Path $pluginRoot 'Finare.uplugin'),
        (Join-Path $pluginRoot 'Source'),
        (Join-Path $pluginRoot 'Resources'),
        (Join-Path $repoRoot 'src\Finare.Cli\Finare.Cli.csproj'),
        (Join-Path $repoRoot 'src\Finare.Unreal\Finare.Unreal.csproj'),
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
            (Join-Path $repoRoot 'src\Finare.Cli\Finare.Cli.csproj'),
            '-c', $Configuration,
            '--nologo',
            '--no-self-contained',
            '-o', $cliPublish,
            "-p:FinareManagedStageDir=$managedRuntime") `
        -Label 'Managed SDK publish'
    Copy-PayloadTree `
        -Source $cliPublish `
        -Destination (Join-Path $sdkRoot 'Tools\Finare.Cli')

    Write-Host '[plugin] Copying engine-independent source plugin...'
    $packagedPlugin = Join-Path $sdkRoot 'Plugins\Finare'
    Copy-PayloadTree `
        -Source (Join-Path $pluginRoot 'Source') `
        -Destination (Join-Path $packagedPlugin 'Source')
    Copy-PayloadTree `
        -Source (Join-Path $pluginRoot 'Resources') `
        -Destination (Join-Path $packagedPlugin 'Resources')
    $pluginDescriptor = Get-Content `
        -LiteralPath (Join-Path $pluginRoot 'Finare.uplugin') `
        -Raw | ConvertFrom-Json
    if ([int]$pluginDescriptor.Version -ne
            [int]$releaseManifest.artifacts.pluginVersion -or
        $pluginDescriptor.VersionName -ne
            [string]$releaseManifest.artifacts.pluginVersionName) {
        Throw-SdkError `
            'USSDK1002' `
            'Source plugin descriptor does not match the product release manifest.'
    }
    $pluginDescriptor.VersionName = $Version
    # This archive ships source, not precompiled engine binaries. Keeping the
    # plugin project-local lets the adopted native host targets compile and
    # link the runtime module during normal BuildCookRun packaging.
    $pluginDescriptor.Installed = $false
    [IO.File]::WriteAllText(
        (Join-Path $packagedPlugin 'Finare.uplugin'),
        ($pluginDescriptor | ConvertTo-Json -Depth 10) + "`n",
        [Text.UTF8Encoding]::new($false))

    Write-Host '[tools] Copying onboarding, installer, schemas, templates, and documentation...'
    $toolDirectory = Join-Path $sdkRoot 'Tools'
    New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
    foreach ($tool in @(
        'finare.cmd',
        'run-finare-onboarding-after-process.ps1',
        'setup-unreal-project.ps1',
        'Finare.BuildGraph.psm1',
        'install-finare-sdk.ps1',
        'new-finare-project.ps1',
        'start-finare-authoring.ps1',
        'generate-unreal-data-plane-extension.ps1',
        'measure-compiled-chunk-load.ps1',
        'measure-readytorun-chunk.ps1',
        'measure-generation-prewarm.ps1')) {
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
        'compatibility-policy.v0.1.json' = 'schemas\finare.compatibility-policy.v0.1.json'
        'diagnostics-policy.v0.1.json' = 'schemas\finare.diagnostics-policy.v0.1.json'
        'unreal-reflection.v0.4.example.json' = 'samples\Unreal\FinareRuntime.reflection.json'
        'unreal-native-extensions.v0.3.example.json' = 'samples\Unreal\FinareRuntime.native-extensions.json'
        'unreal-api-nodes.v0.2.example.json' = 'samples\Unreal\FinareRuntime.api-nodes.json'
        'unreal-binding-coverage.v0.2.example.json' = 'samples\Unreal\FinareRuntime.binding-coverage.json'
        'unreal-data-plane-layout.v0.1.example.json' = 'samples\Unreal\FinareRuntime.data-plane.json'
        'unreal-data-plane-extension.v0.1.example.json' = 'samples\Unreal\BlockBreaker.data-plane-extension.json'
        'unreal-data-plane-extension-catalog.v0.1.example.json' = 'samples\Unreal\BlockBreaker.data-plane-extension.catalog.json'
        'unreal-script-deployment.v0.1.example.json' = 'samples\Unreal\Finare.Unreal.Demo.deployment.json'
    }
    foreach ($entry in $schemaSources.GetEnumerator()) {
        Copy-Item `
            -LiteralPath (Join-Path $repoRoot $entry.Value) `
            -Destination (Join-Path $schemaDirectory $entry.Key) `
            -Force
    }
    $benchmarkDirectory = Join-Path $sdkRoot 'benchmarks\unreal'
    New-Item -ItemType Directory -Force -Path $benchmarkDirectory | Out-Null
    Copy-Item `
        -LiteralPath (Join-Path $repoRoot 'benchmarks\unreal\generation-prewarm-probe.cs') `
        -Destination (Join-Path $benchmarkDirectory 'generation-prewarm-probe.cs') `
        -Force
    Copy-Item `
        -LiteralPath (Join-Path $repoRoot 'benchmarks\unreal\usability-budgets.v0.1.json') `
        -Destination (Join-Path $benchmarkDirectory 'usability-budgets.v0.1.json') `
        -Force
    Copy-Item `
        -LiteralPath (Join-Path $repoRoot 'HostProject\Config\Finare.onboarding.json') `
        -Destination (Join-Path $templateDirectory 'Finare.onboarding.json') `
        -Force
    Copy-Item `
        -LiteralPath (Join-Path $repoRoot 'samples\Unreal\Finare.Unreal.Demo.deployment.json') `
        -Destination (Join-Path $templateDirectory 'Finare.deployment.json') `
        -Force
    Copy-Item `
        -LiteralPath (Join-Path $repoRoot 'samples\Unreal\FinareRuntime.native-extensions.json') `
        -Destination (Join-Path $templateDirectory 'Finare.native-extensions.json') `
        -Force
    Copy-PayloadTree `
        -Source (Join-Path $repoRoot 'templates\UnrealProject') `
        -Destination (Join-Path $sdkRoot 'Templates\UnrealProject')
    Copy-PayloadTree `
        -Source (Join-Path $repoRoot 'prototype\visual-editor') `
        -Destination (Join-Path $sdkRoot 'Authoring\Workbench')
    $documentationDirectory = Join-Path $sdkRoot 'Documentation'
    New-Item -ItemType Directory -Force -Path $documentationDirectory | Out-Null
    foreach ($document in $releaseDocumentation) {
        $source = Join-Path $repoRoot ([string]$document.path)
        Copy-Item -LiteralPath $source -Destination $documentationDirectory -Force
    }
    Copy-Item `
        -LiteralPath $releaseManifestSource `
        -Destination (Join-Path $sdkRoot 'release-manifest.json') `
        -Force

    foreach ($requiredPayload in @(
        'Tools\finare.cmd',
        'Tools\run-finare-onboarding-after-process.ps1',
        'Tools\setup-unreal-project.ps1',
        'Tools\Finare.BuildGraph.psm1',
        'Tools\install-finare-sdk.ps1',
        'Tools\new-finare-project.ps1',
        'Tools\start-finare-authoring.ps1',
        'Tools\generate-unreal-data-plane-extension.ps1',
        'Tools\measure-compiled-chunk-load.ps1',
        'Tools\measure-readytorun-chunk.ps1',
        'Tools\measure-generation-prewarm.ps1',
        'Tools\Finare.Cli\Finare.Cli.dll',
        'Tools\Finare.Cli\Finare.Cli.runtimeconfig.json',
        'Schemas\compatibility-policy.v0.1.json',
        'Schemas\diagnostics-policy.v0.1.json',
        'benchmarks\unreal\generation-prewarm-probe.cs',
        'benchmarks\unreal\usability-budgets.v0.1.json',
        'Managed\Runtime\Finare.Core.dll',
        'Managed\Runtime\Finare.Unreal.dll',
        'Managed\Runtime\Finare.Unreal.Generators.dll',
        'Managed\Runtime\Finare.Unreal.deps.json',
        'release-manifest.json',
        'Documentation\DOCUMENTATION.md',
        'Documentation\RELEASE_NOTES_1_0_RC1.md',
        'Documentation\AUTHORING_SURFACE.md',
        'Documentation\AUTHORING_DIAGNOSTICS.md',
        'Documentation\UNREAL_ONBOARDING.md',
        'Documentation\UNREAL_FIRST_PROJECT.md',
        'Documentation\UNREAL_GAMEPLAY_RECIPES.md',
        'Documentation\UNREAL_USABILITY.md',
        'Documentation\DIAGNOSTICS_BUNDLES.md',
        'Documentation\RUNTIME_DIAGNOSTICS.md',
        'Documentation\PERFORMANCE_WORKLOAD_SUITE.md',
        'Documentation\UNREAL_RELEASE.md',
        'Documentation\PUERTS_MIGRATION.md',
        'Plugins\Finare\Finare.uplugin',
        'Plugins\Finare\Source\FinareRuntime',
        'Plugins\Finare\Source\FinareEditor',
        'Plugins\Finare\Resources\Managed\Finare.Unreal.runtimeconfig.json',
        'Templates\UnrealProject\finare.project-templates.json',
        'Templates\UnrealProject\Managed\FirstScript.cs.template',
        'Templates\UnrealProject\Managed\VisualGameplayScript.cs.template',
        'Templates\UnrealProject\Managed\DataPlaneScript.cs.template',
        'Templates\UnrealProject\Managed\Project.Finare.slnx.template',
        'Templates\UnrealProject\IDE\Project.Finare.code-workspace.template',
        'Templates\UnrealProject\IDE\Finare.ide.json.template',
        'Templates\UnrealProject\Config\Finare.onboarding.json.template',
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
                compatibilityPolicy = 'unifiedscript.compatibility-policy/v0.1'
                diagnosticsPolicy = 'unifiedscript.diagnostics-policy/v0.1'
                onboarding = 'unifiedscript.unreal-project-onboarding/v0.1'
                reflection = 'unifiedscript.unreal-reflection/v0.5'
                nativeExtensions = 'unifiedscript.unreal-native-extensions/v0.3'
                apiNodeCatalog = 'unifiedscript.unreal-api-node-catalog/v0.2'
                bindingCoverage = 'unifiedscript.unreal-binding-coverage/v0.2'
                dataPlaneLayout = 'unifiedscript.unreal-data-plane-layout/v0.1'
                dataPlaneExtension = 'unifiedscript.unreal-data-plane-extension/v0.1'
                dataPlaneExtensionCatalog = 'unifiedscript.unreal-data-plane-extension-catalog/v0.1'
                deployment = 'unifiedscript.unreal-script-deployment/v0.1'
            }
        }
        release = [ordered]@{
            schemaVersion = [string]$releaseManifest.schemaVersion
            channel = [string]$releaseManifest.channel
            status = [string]$releaseManifest.status
            verifiedEngineVersion = [string]$releaseManifest.compatibility.verifiedEngineVersion
            releaseNotes = [string]$releaseManifest.releaseNotes
            documentationEntryCount = $releaseDocumentation.Count
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
        releaseManifestSchemaVersion = [string]$releaseManifest.schemaVersion
        releaseStatus = [string]$releaseManifest.status
        documentationEntryCount = $releaseDocumentation.Count
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
    Write-Host "Finare SDK package: PASS ($archiveHash)"
    Write-Host $archivePath
} finally {
    if (Test-Path -LiteralPath $stagingParent) {
        $resolvedStage = [IO.Path]::GetFullPath($stagingParent)
        $resolvedStagingRoot = [IO.Path]::GetFullPath($stagingRoot).TrimEnd('\', '/') +
            [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedStage.StartsWith(
                $resolvedStagingRoot,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Split-Path -Leaf $resolvedStage).StartsWith(
                'us-sdk-',
                [StringComparison]::Ordinal)) {
            throw "Refusing to remove staging directory outside the SDK temp boundary: $resolvedStage"
        }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
