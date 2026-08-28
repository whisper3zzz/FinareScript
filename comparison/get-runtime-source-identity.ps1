[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('unifiedScript', 'puerts')]
    [string]$EngineId,
    [Parameter(Mandatory)]
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
$resolvedRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path

[string[]]$pathSpecs = if ($EngineId -eq 'unifiedScript') {
    @(
        'Directory.Build.props',
        'Directory.Packages.props',
        'global.json',
        'src/UnifiedScript.Core',
        'src/UnifiedScript.Runtime',
        'src/UnifiedScript.Unreal',
        'HostProject/Plugins/UEUnifiedScript/UEUnifiedScript.uplugin',
        'HostProject/Plugins/UEUnifiedScript/Source/UEUnifiedScriptRuntime'
    )
} else {
    @('Plugins/Puerts')
}

$trackedFiles = @(& git -C $resolvedRoot ls-files -- @pathSpecs)
if ($LASTEXITCODE -ne 0) {
    throw "Cannot enumerate $EngineId runtime sources under $resolvedRoot."
}
if ($trackedFiles.Count -eq 0) {
    throw "No tracked $EngineId runtime sources exist under $resolvedRoot."
}

[Array]::Sort($trackedFiles, [StringComparer]::Ordinal)
$aggregate = [Security.Cryptography.IncrementalHash]::CreateHash(
    [Security.Cryptography.HashAlgorithmName]::SHA256)
try {
    $header = [Text.Encoding]::UTF8.GetBytes(
        "unifiedscript.runtime-source-identity/v0.1`n$EngineId`n")
    $aggregate.AppendData($header)

    foreach ($relativePath in $trackedFiles) {
        $fullPath = Join-Path $resolvedRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Tracked runtime source is missing: $fullPath"
        }
        $contentHash = (Get-FileHash `
            -LiteralPath $fullPath `
            -Algorithm SHA256).Hash.ToLowerInvariant()
        $entry = [Text.Encoding]::UTF8.GetBytes(
            "$relativePath`t$contentHash`n")
        $aggregate.AppendData($entry)
    }

    $identity = [Convert]::ToHexString(
        $aggregate.GetHashAndReset()).ToLowerInvariant()
} finally {
    $aggregate.Dispose()
}

Write-Output "sha256:$identity"
