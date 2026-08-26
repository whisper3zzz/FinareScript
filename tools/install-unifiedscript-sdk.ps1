[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Project,

    [string]$SdkRoot,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$resolvedSdkRoot = if ([string]::IsNullOrWhiteSpace($SdkRoot)) {
    Split-Path -Parent $PSScriptRoot
} elseif ([IO.Path]::IsPathRooted($SdkRoot)) {
    [IO.Path]::GetFullPath($SdkRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $SdkRoot))
}
$projectPath = [IO.Path]::GetFullPath($Project)
$projectRoot = Split-Path -Parent $projectPath
$manifestPath = Join-Path $resolvedSdkRoot 'sdk-manifest.json'
$sourcePlugin = Join-Path $resolvedSdkRoot 'Plugins\UEUnifiedScript'
$sourceRuntime = Join-Path $resolvedSdkRoot 'Managed\Runtime'
$targetPlugin = Join-Path $projectRoot 'Plugins\UEUnifiedScript'
$installId = Get-Date -Format 'yyyyMMdd-HHmmss'
$temporaryPlugin = Join-Path $projectRoot "Plugins\.UEUnifiedScript.install-$([Guid]::NewGuid().ToString('N'))"
$reportPath = Join-Path $projectRoot "Saved\UnifiedScript\Install\$installId\install-report.json"
$backupPlugin = $null
$startedAt = [DateTime]::UtcNow

function Throw-SdkInstallError {
    param([string]$Code, [string]$Message)

    throw [InvalidOperationException]::new("[$Code] $Message")
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

function Copy-PayloadTree {
    param([string]$Source, [string]$Destination)

    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File) {
        $relative = Get-RelativeFilePath -BaseDirectory $Source -Path $file.FullName
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) |
            Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

function Write-InstallReport {
    param([string]$Status, [string]$ErrorMessage)

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $reportPath) |
        Out-Null
    $document = [ordered]@{
        schemaVersion = 'unifiedscript.sdk-install-report/v0.1'
        status = $Status
        sdkRoot = $resolvedSdkRoot
        sdkVersion = if ($null -eq $script:SdkManifest) {
            $null
        } else { $script:SdkManifest.sdkVersion }
        project = $projectPath
        plugin = $targetPlugin
        previousPluginBackup = $backupPlugin
        error = $ErrorMessage
        durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
    }
    [IO.File]::WriteAllText(
        $reportPath,
        ($document | ConvertTo-Json -Depth 8) + "`n",
        [Text.UTF8Encoding]::new($false))
}

$script:SdkManifest = $null
try {
    foreach ($required in @($projectPath, $manifestPath, $sourcePlugin, $sourceRuntime)) {
        if (-not (Test-Path -LiteralPath $required)) {
            Throw-SdkInstallError 'USSDK3001' "Required installation input is missing: $required"
        }
    }
    if ([IO.Path]::GetExtension($projectPath) -ne '.uproject') {
        Throw-SdkInstallError 'USSDK3001' "Project must be a .uproject file: $projectPath"
    }

    $projectJson = Get-Content -LiteralPath $projectPath -Raw | ConvertFrom-Json
    $enabledPlugin = @($projectJson.Plugins) |
        Where-Object { $_.Name -eq 'UEUnifiedScript' -and $_.Enabled -eq $true }
    if ($enabledPlugin.Count -eq 0) {
        Throw-SdkInstallError `
            'USSDK3002' `
            'The .uproject must explicitly enable the UEUnifiedScript plugin before installation.'
    }

    $script:SdkManifest = Get-Content -LiteralPath $manifestPath -Raw |
        ConvertFrom-Json
    if ($script:SdkManifest.schemaVersion -ne 'unifiedscript.sdk-manifest/v0.1') {
        Throw-SdkInstallError `
            'USSDK3101' `
            "Unsupported SDK manifest '$($script:SdkManifest.schemaVersion)'."
    }
    if ($script:SdkManifest.targetPlatform -ne 'win-x64' -or
        $script:SdkManifest.compatibility.managedTargetFramework -ne 'net10.0' -or
        [int]$script:SdkManifest.compatibility.abi.callFrame -ne 3 -or
        [int]$script:SdkManifest.compatibility.abi.event -ne 4 -or
        [int]$script:SdkManifest.compatibility.abi.sharedData -ne 4 -or
        [int]$script:SdkManifest.compatibility.abi.gameplayData -ne 4) {
        Throw-SdkInstallError 'USSDK3102' 'SDK platform, managed runtime, or ABI is incompatible.'
    }

    $longestPluginRelativePath = Get-ChildItem -LiteralPath $sourcePlugin -Recurse -File |
        ForEach-Object {
            Get-RelativeFilePath -BaseDirectory $sourcePlugin -Path $_.FullName
        } | Sort-Object Length -Descending | Select-Object -First 1
    $longestInstalledPath = Join-Path $targetPlugin $longestPluginRelativePath
    if ($longestInstalledPath.Length -ge 260) {
        Throw-SdkInstallError `
            'USSDK3003' `
            (
                "The project path is too deep for Windows PowerShell 5.1 file operations " +
                "($($longestInstalledPath.Length) characters). Move/extract the project to a " +
                'shorter root and retry; no plugin files were changed.')
    }

    Write-Host '[verify] Validating every SDK payload hash...'
    $manifestFiles = @($script:SdkManifest.payload.files)
    if ([int]$script:SdkManifest.payload.fileCount -ne $manifestFiles.Count) {
        Throw-SdkInstallError 'USSDK3103' 'SDK manifest file count is inconsistent.'
    }
    $listedPaths = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifestFiles) {
        $relative = ([string]$entry.path).Replace('/', '\')
        if ([string]::IsNullOrWhiteSpace($relative) -or
            [IO.Path]::IsPathRooted($relative) -or
            $relative.Split('\') -contains '..') {
            Throw-SdkInstallError 'USSDK3103' "Unsafe SDK manifest path: $($entry.path)"
        }
        if (-not $listedPaths.Add($relative)) {
            Throw-SdkInstallError 'USSDK3103' "Duplicate SDK manifest path: $($entry.path)"
        }
        $payloadPath = [IO.Path]::GetFullPath((Join-Path $resolvedSdkRoot $relative))
        $rootPrefix = [IO.Path]::GetFullPath($resolvedSdkRoot).TrimEnd('\', '/') +
            [IO.Path]::DirectorySeparatorChar
        if (-not $payloadPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            Throw-SdkInstallError 'USSDK3103' "SDK manifest path escapes its root: $($entry.path)"
        }
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
            Throw-SdkInstallError 'USSDK3104' "SDK payload file is missing: $($entry.path)"
        }
        $file = Get-Item -LiteralPath $payloadPath
        $hash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
        if ([long]$entry.size -ne [long]$file.Length -or
            $hash -ne [string]$entry.sha256) {
            Throw-SdkInstallError 'USSDK3104' "SDK payload hash mismatch: $($entry.path)"
        }
    }
    $actualFiles = @(
        Get-ChildItem -LiteralPath $resolvedSdkRoot -Recurse -File |
            Where-Object { $_.FullName -ne $manifestPath })
    foreach ($file in $actualFiles) {
        $relative = Get-RelativeFilePath `
            -BaseDirectory $resolvedSdkRoot `
            -Path $file.FullName
        if (-not $listedPaths.Contains($relative)) {
            Throw-SdkInstallError 'USSDK3105' "Unmanifested SDK payload file: $relative"
        }
    }

    if (Test-Path -LiteralPath $targetPlugin) {
        if (-not $Force) {
            Throw-SdkInstallError `
                'USSDK3201' `
                "Target plugin already exists; re-run with -Force to preserve and replace it: $targetPlugin"
        }
        $backupPlugin = Join-Path $projectRoot "Plugins\UEUnifiedScript.backup-$installId"
        if (Test-Path -LiteralPath $backupPlugin) {
            Throw-SdkInstallError 'USSDK3201' "Plugin backup path already exists: $backupPlugin"
        }
    }

    Write-Host '[install] Staging source plugin and managed runtime...'
    New-Item -ItemType Directory -Force -Path $temporaryPlugin | Out-Null
    Copy-PayloadTree -Source $sourcePlugin -Destination $temporaryPlugin
    Copy-PayloadTree `
        -Source $sourceRuntime `
        -Destination (Join-Path $temporaryPlugin 'Binaries\Managed')
    foreach ($requiredInstalledFile in @(
        'UEUnifiedScript.uplugin',
        'Binaries\Managed\UnifiedScript.Core.dll',
        'Binaries\Managed\UnifiedScript.Unreal.dll',
        'Binaries\Managed\UnifiedScript.Unreal.deps.json',
        'Resources\Managed\UnifiedScript.Unreal.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $temporaryPlugin $requiredInstalledFile))) {
            Throw-SdkInstallError 'USSDK3202' "Installed plugin payload is incomplete: $requiredInstalledFile"
        }
    }
    if ($null -ne $backupPlugin) {
        Move-Item -LiteralPath $targetPlugin -Destination $backupPlugin
    }
    Move-Item -LiteralPath $temporaryPlugin -Destination $targetPlugin

    Write-InstallReport -Status 'passed' -ErrorMessage $null
    Write-Host "UnifiedScript SDK install: PASS ($targetPlugin)"
    Write-Host $reportPath
} catch {
    if (Test-Path -LiteralPath $temporaryPlugin) {
        Remove-Item -LiteralPath $temporaryPlugin -Recurse -Force
    }
    Write-InstallReport -Status 'failed' -ErrorMessage $_.Exception.Message
    Write-Error "UnifiedScript SDK install failed: $($_.Exception.Message)"
    exit 1
}
