[CmdletBinding()]
param(
    [switch]$Child,
    [string]$Root,
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'Finare.BuildGraph.psm1'
Import-Module -Name $modulePath -Force

function Assert-CrossShellBuildGraph {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

if ($Child) {
    if ([string]::IsNullOrWhiteSpace($Root) -or
        [string]::IsNullOrWhiteSpace($Output)) {
        throw 'The cross-shell child requires Root and Output.'
    }

    $state = Join-Path $Root 'state.json'
    $project = Join-Path $Root 'Game.uproject'
    $config = Join-Path $Root 'Finare.onboarding.json'
    $inputs = Join-Path $Root 'Inputs'
    $source = Join-Path $inputs 'EditorValidator.cpp'
    $sourceWithUnderscore = Join-Path $inputs 'EditorValidator_Blueprints.cpp'
    $punctuated = Join-Path $inputs 'A_[x]!,_.gen.cpp'
    $valueInputs = [ordered]@{
        'z-key' = 'z'
        'A_key' = 'A'
        '!' = 'punctuation'
    }
    $pathInputs = [ordered]@{
        'z-source' = @($source, $inputs)
        'A-source' = @($punctuated, $sourceWithUnderscore)
    }

    $context = New-FinareBuildGraphContext `
        -StatePath $state `
        -Project $project `
        -Config $config `
        -EngineVersion '5.8.1'
    $first = Get-FinareBuildGraphStageDecision `
        -Context $context `
        -Name 'A-dependency' `
        -Values ([ordered]@{ marker = 'first' }) `
        -PathInputs $pathInputs
    Complete-FinareBuildGraphStage `
        -Context $context `
        -Decision $first `
        -Executed $true | Out-Null
    $second = Get-FinareBuildGraphStageDecision `
        -Context $context `
        -Name 'z-dependency' `
        -Values ([ordered]@{ marker = 'second' }) `
        -PathInputs $pathInputs
    Complete-FinareBuildGraphStage `
        -Context $context `
        -Decision $second `
        -Executed $true | Out-Null
    $decision = Get-FinareBuildGraphStageDecision `
        -Context $context `
        -Name 'consumer' `
        -Values $valueInputs `
        -PathInputs $pathInputs `
        -Dependencies @('z-dependency', 'A-dependency')

    $result = [ordered]@{
        inputFingerprint = $decision.InputFingerprint
        directFingerprint = $decision.DirectFingerprint
        namedInputs = $decision.Inputs
        dependencies = $decision.Dependencies
    }
    [IO.File]::WriteAllText(
        $Output,
        ($result | ConvertTo-Json -Depth 20 -Compress),
        [Text.UTF8Encoding]::new($false))
    exit 0
}

$root = Join-Path ([IO.Path]::GetTempPath()) `
    ("Finare-BuildGraph-CrossShell-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $root | Out-Null
try {
    $inputs = Join-Path $root 'Inputs'
    New-Item -ItemType Directory -Force -Path $inputs | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $root 'Game.uproject'), '{}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $root 'Finare.onboarding.json'), '{}',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $inputs 'EditorValidator.cpp'), 'editor',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $inputs 'EditorValidator_Blueprints.cpp'), 'blueprints',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $inputs 'A_[x]!,_.gen.cpp'), 'punctuation',
        [Text.UTF8Encoding]::new($false))

    $legacyShell = (Get-Command powershell.exe -ErrorAction Stop).Source
    $modernShell = (Get-Command pwsh.exe -ErrorAction Stop).Source
    $legacyOutput = Join-Path $root 'powershell5.json'
    $modernOutput = Join-Path $root 'powershell7.json'
    & $legacyShell -NoProfile -NonInteractive -ExecutionPolicy Bypass `
        -File $PSCommandPath -Child -Root $root -Output $legacyOutput
    Assert-CrossShellBuildGraph ($LASTEXITCODE -eq 0) `
        'Windows PowerShell 5 child fingerprint failed.'
    & $modernShell -NoProfile -NonInteractive -ExecutionPolicy Bypass `
        -File $PSCommandPath -Child -Root $root -Output $modernOutput
    Assert-CrossShellBuildGraph ($LASTEXITCODE -eq 0) `
        'PowerShell 7 child fingerprint failed.'

    $legacy = Get-Content -LiteralPath $legacyOutput -Raw | ConvertFrom-Json
    $modern = Get-Content -LiteralPath $modernOutput -Raw | ConvertFrom-Json
    Assert-CrossShellBuildGraph `
        ($legacy.inputFingerprint -eq $modern.inputFingerprint) `
        'PS5 and PS7 input fingerprints differ for identical files.'
    Assert-CrossShellBuildGraph `
        ($legacy.directFingerprint -eq $modern.directFingerprint) `
        'PS5 and PS7 direct fingerprints differ for identical files.'
    Assert-CrossShellBuildGraph `
        ($legacy.namedInputs.'A-source' -eq $modern.namedInputs.'A-source') `
        'PS5 and PS7 named path fingerprints differ.'
    Assert-CrossShellBuildGraph `
        ($legacy.dependencies.'A-dependency' -eq $modern.dependencies.'A-dependency') `
        'PS5 and PS7 dependency fingerprints differ.'

    [IO.File]::WriteAllText(
        (Join-Path $inputs 'EditorValidator.cpp'), 'editor-byte-changed',
        [Text.UTF8Encoding]::new($false))
    $changedOutput = Join-Path $root 'changed.json'
    & $modernShell -NoProfile -NonInteractive -ExecutionPolicy Bypass `
        -File $PSCommandPath -Child -Root $root -Output $changedOutput
    Assert-CrossShellBuildGraph ($LASTEXITCODE -eq 0) `
        'Changed-byte fingerprint child failed.'
    $changed = Get-Content -LiteralPath $changedOutput -Raw | ConvertFrom-Json
    Assert-CrossShellBuildGraph `
        ($changed.inputFingerprint -ne $modern.inputFingerprint) `
        'Changing a tracked byte did not invalidate the fingerprint.'
    Write-Host 'Finare cross-shell BuildGraph fingerprint: PASS'
} finally {
    if (Test-Path -LiteralPath $root) {
        $tempDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        $resolvedRoot = [IO.Path]::GetFullPath($root)
        $tempPrefix = $tempDirectory.TrimEnd('\', '/') +
            [IO.Path]::DirectorySeparatorChar
        $rootName = [IO.Path]::GetFileName($resolvedRoot.TrimEnd('\', '/'))
        if (-not $resolvedRoot.StartsWith(
                $tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not $rootName.StartsWith(
                'Finare-BuildGraph-CrossShell-', [StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected cross-shell test path '$resolvedRoot'."
        }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
