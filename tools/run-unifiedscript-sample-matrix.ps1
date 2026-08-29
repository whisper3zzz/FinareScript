[CmdletBinding()]
param(
    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,

    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',

    [string]$OutputDirectory,

    [string[]]$SampleIds,

    [switch]$AllowDirtySource,

    [switch]$AllowDirtyComparison
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repoRoot 'samples\Unreal\unifiedscript.samples.json'
$releasePath = Join-Path $repoRoot 'release\unifiedscript.release.json'
$startedAtUtc = [DateTime]::UtcNow
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot "artifacts\samples\$runId"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
$reportPath = Join-Path $runDirectory 'sample-matrix.json'
$summaryPath = Join-Path $runDirectory 'sample-matrix.md'
$powershell = Join-Path $env:SystemRoot `
    'System32\WindowsPowerShell\v1.0\powershell.exe'

function Get-GitState {
    param([Parameter(Mandatory)][string]$Root)

    $revision = (& git -C $Root rev-parse HEAD 2>$null).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Git revision is unavailable for '$Root'."
    }
    $status = @(& git -C $Root status --porcelain=v1 --untracked-files=all 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Git status is unavailable for '$Root'."
    }
    & git -C $Root diff --quiet --
    $trackedChanges = $LASTEXITCODE -ne 0
    & git -C $Root diff --cached --quiet --
    $stagedChanges = $LASTEXITCODE -ne 0
    $untracked = @($status | Where-Object {
        $_.StartsWith('?? ', [StringComparison]::Ordinal)
    })
    return [ordered]@{
        root = [IO.Path]::GetFullPath($Root)
        revision = $revision
        dirty = $trackedChanges -or $stagedChanges -or $untracked.Count -ne 0
        porcelainDirty = $status.Count -ne 0
        trackedChanges = $trackedChanges
        stagedChanges = $stagedChanges
        untracked = $untracked
        status = @($status)
    }
}

function Test-GitStatePreserved {
    param(
        [Parameter(Mandatory)]$Before,
        [Parameter(Mandatory)]$After
    )

    if ($Before.revision -ne $After.revision) {
        return $false
    }
    if (-not $Before.dirty) {
        # Git can report CRLF/stat-only dirt after UE tooling rewrites a file
        # even when the clean-filtered blobs are byte-equivalent to HEAD.
        return -not $After.dirty
    }
    return (@($Before.status) -join "`n") -ceq
        (@($After.status) -join "`n")
}

function Expand-SampleToken {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)]$Tokens
    )

    $expanded = $Value
    foreach ($entry in $Tokens.GetEnumerator()) {
        $expanded = $expanded.Replace("{$($entry.Key)}", [string]$entry.Value)
    }
    if ($expanded -match '\{[A-Za-z][A-Za-z0-9]*\}') {
        throw "Unresolved sample token in '$expanded'."
    }
    return $expanded.Replace('/', [IO.Path]::DirectorySeparatorChar)
}

function Get-JsonPathValue {
    param(
        [Parameter(Mandatory)]$Document,
        [Parameter(Mandatory)][string]$Path
    )

    $value = $Document
    foreach ($segment in $Path.Split('.')) {
        $property = $value.PSObject.Properties[$segment]
        if ($null -eq $property) {
            throw "Evidence path '$Path' is missing segment '$segment'."
        }
        $value = $property.Value
    }
    return $value
}

function Test-SampleAssertion {
    param(
        [Parameter(Mandatory)]$Document,
        [Parameter(Mandatory)]$Assertion
    )

    $actual = Get-JsonPathValue -Document $Document -Path $Assertion.path
    $passed = switch ([string]$Assertion.operator) {
        'equals' { $actual -eq $Assertion.expected }
        'minimum' { [decimal]$actual -ge [decimal]$Assertion.expected }
        default { throw "Unsupported sample assertion operator '$($Assertion.operator)'." }
    }
    return [ordered]@{
        path = [string]$Assertion.path
        operator = [string]$Assertion.operator
        expected = $Assertion.expected
        actual = $actual
        passed = [bool]$passed
    }
}

function Invoke-SampleProcess {
    param(
        [Parameter(Mandatory)][string]$Script,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$LogPath
    )

    $processArguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Script) + $Arguments
    $previousPreference = $ErrorActionPreference
    try {
        # UE Automation intentionally emits selected diagnostics on stderr. Keep
        # process exit code and evidence assertions as the verdict on Windows.
        $ErrorActionPreference = 'Continue'
        $output = @(& $powershell @processArguments 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    $output | Set-Content -LiteralPath $LogPath -Encoding utf8
    $output | ForEach-Object { Write-Host ([string]$_) }
    return [ordered]@{
        exitCode = [int]$exitCode
        output = @($output | ForEach-Object { [string]$_ })
    }
}

function Remove-DeclaredUntrackedOutputs {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string[]]$RelativePaths
    )

    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    $declared = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($relativePath in $RelativePaths) {
        $normalized = $relativePath.Replace('\', '/')
        if ([IO.Path]::IsPathRooted($relativePath) -or
            $normalized.Split('/') -contains '..' -or
            -not $declared.Add($normalized)) {
            throw "Unsafe or duplicate cleanup path '$relativePath'."
        }
    }

    $state = Get-GitState -Root $Root
    $untrackedPaths = @($state.untracked | ForEach-Object { $_.Substring(3) })
    $unexpected = @($untrackedPaths | Where-Object {
        $_.Replace('\', '/') -notin $declared
    })
    if ($unexpected.Count -ne 0) {
        throw (
            "Sample created undeclared untracked outputs below '$Root': " +
            ($unexpected -join ', '))
    }
    foreach ($relativePath in $untrackedPaths) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $Root $relativePath))
        if (-not $candidate.StartsWith(
                $rootPath,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "Declared cleanup output is not a safe file: $candidate"
        }
        Remove-Item -LiteralPath $candidate -Force
    }
}

foreach ($required in @($catalogPath, $releasePath, $powershell)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required sample-matrix input is missing: $required"
    }
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = @('D:\UE_5.8', 'D:\UE\_5.8') |
        Where-Object {
            Test-Path (Join-Path $_ 'Engine\Build\BatchFiles\Build.bat')
        } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'Pass -EngineRoot or set UNIFIEDSCRIPT_UE_ROOT.'
}
$EngineRoot = [IO.Path]::GetFullPath($EngineRoot)
$ComparisonRoot = [IO.Path]::GetFullPath($ComparisonRoot)
if (-not (Test-Path -LiteralPath (
            Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe') `
        -PathType Leaf)) {
    throw "UnrealEditor-Cmd.exe is missing below '$EngineRoot'."
}

$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$release = Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json
$requiredCategories = @(
    'minimal', 'gameplay', 'networking-server', 'data-plane', 'visual-code')
$catalogCategories = @($catalog.categories | ForEach-Object { [string]$_ })
$samples = @($catalog.samples)
$sampleIdsInCatalog = @($samples | ForEach-Object { [string]$_.id })
$sampleCategories = @($samples | ForEach-Object { [string]$_.category })
if ($catalog.schemaVersion -ne 'unifiedscript.sample-catalog/v0.1' -or
    $catalog.productVersion -ne $release.productVersion -or
    $samples.Count -ne 5 -or
    @($sampleIdsInCatalog | Select-Object -Unique).Count -ne 5 -or
    @($sampleCategories | Select-Object -Unique).Count -ne 5 -or
    (Compare-Object $requiredCategories $catalogCategories).Count -ne 0 -or
    (Compare-Object $requiredCategories $sampleCategories).Count -ne 0) {
    throw 'The sample catalog is incomplete, duplicated, or version-skewed.'
}

if ($SampleIds.Count -gt 0) {
    $unknownIds = @($SampleIds | Where-Object { $_ -notin $sampleIdsInCatalog })
    if ($unknownIds.Count -gt 0) {
        throw "Unknown sample IDs: $($unknownIds -join ', ')"
    }
    $samples = @($samples | Where-Object { $_.id -in $SampleIds })
}

$needsComparison = @($samples | Where-Object {
    @($_.sources | Where-Object scope -eq 'comparison').Count -gt 0 -or
    @($_.execution.arguments) -contains '{ComparisonRoot}'
}).Count -gt 0
$comparisonRepositories = @()
if ($needsComparison) {
    foreach ($name in @(
            'BlockBreaker-UnifiedScript', 'BlockBreakerSource', 'BlockBreaker-Puerts')) {
        $root = Join-Path $ComparisonRoot $name
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            throw "Required comparison repository is missing: $root"
        }
        $comparisonRepositories += $root
    }
}

foreach ($sample in $samples) {
    $runner = Join-Path $repoRoot ([string]$sample.execution.runner)
    if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
        throw "Sample '$($sample.id)' runner is missing: $runner"
    }
    foreach ($source in @($sample.sources)) {
        if ([IO.Path]::IsPathRooted([string]$source.path) -or
            ([string]$source.path).Replace('\', '/').Split('/') -contains '..') {
            throw "Sample '$($sample.id)' has an unsafe source path '$($source.path)'."
        }
        $sourceRoot = switch ([string]$source.scope) {
            'repository' { $repoRoot }
            'comparison' { $ComparisonRoot }
            default { throw "Sample '$($sample.id)' has unsupported source scope '$($source.scope)'." }
        }
        $sourcePath = Join-Path $sourceRoot ([string]$source.path)
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Sample '$($sample.id)' source is missing: $sourcePath"
        }
    }
    if (@($sample.assertions).Count -eq 0 -or
        [string]::IsNullOrWhiteSpace($sample.execution.reportSchema)) {
        throw "Sample '$($sample.id)' does not define strict report assertions."
    }
}

$sourceBefore = Get-GitState -Root $repoRoot
if ($sourceBefore.dirty -and -not $AllowDirtySource) {
    throw 'Sample matrix requires a clean UnifiedScript source tree. Use -AllowDirtySource only for local diagnostics.'
}
$comparisonBefore = @($comparisonRepositories | ForEach-Object { Get-GitState $_ })
if (-not $AllowDirtyComparison -and
    @($comparisonBefore | Where-Object dirty).Count -ne 0) {
    throw 'Sample matrix requires clean comparison repositories. Use isolated worktrees or -AllowDirtyComparison for local diagnostics.'
}

New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$catalogHash = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sampleRecords = [Collections.Generic.List[object]]::new()
$hostBuildReady = $false

foreach ($sample in $samples) {
    $sampleStartedAt = [DateTime]::UtcNow
    $sampleOutput = Join-Path $runDirectory ([string]$sample.id)
    $logPath = Join-Path $sampleOutput 'runner.log'
    $archivedEvidencePath = Join-Path $sampleOutput 'evidence.json'
    New-Item -ItemType Directory -Force -Path $sampleOutput | Out-Null
    $reportName = "sample-matrix-$runId-$($sample.id)"
    $candidateReportPath = Join-Path $sampleOutput 'evidence-source.json'
    $tokens = [ordered]@{
        RepoRoot = $repoRoot
        EngineRoot = $EngineRoot
        ComparisonRoot = $ComparisonRoot
        SampleOutput = $sampleOutput
        ReportName = $reportName
        ReportPath = $candidateReportPath
    }
    $runner = Join-Path $repoRoot ([string]$sample.execution.runner)
    $arguments = @($sample.execution.arguments | ForEach-Object {
        Expand-SampleToken -Value ([string]$_) -Tokens $tokens
    })
    if ([bool]$sample.execution.reuseHostBuild -and $hostBuildReady) {
        $arguments += '-SkipBuild'
    }

    Write-Host "[sample:$($sample.id)] $($sample.title)"
    try {
        $process = Invoke-SampleProcess `
            -Script $runner `
            -Arguments $arguments `
            -LogPath $logPath
        if ($process.exitCode -ne 0) {
            throw "Runner exited with code $($process.exitCode)."
        }

        if (-not [string]::IsNullOrWhiteSpace($sample.execution.reportMarker)) {
            $marker = [string]$sample.execution.reportMarker
            $reportLine = @($process.output | Where-Object {
                $_.StartsWith($marker, [StringComparison]::Ordinal)
            }) | Select-Object -Last 1
            if ($null -eq $reportLine) {
                throw "Runner output did not publish report marker '$marker'."
            }
            $evidencePath = $reportLine.Substring($marker.Length).Trim()
        } else {
            $evidencePath = Expand-SampleToken `
                -Value ([string]$sample.execution.reportPath) `
                -Tokens $tokens
        }
        $evidencePath = [IO.Path]::GetFullPath($evidencePath)
        if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
            throw "Runner evidence is missing: $evidencePath"
        }
        $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
        if ($evidence.schemaVersion -ne $sample.execution.reportSchema) {
            throw (
                "Evidence schema '$($evidence.schemaVersion)' does not match " +
                "'$($sample.execution.reportSchema)'.")
        }
        $assertionResults = @($sample.assertions | ForEach-Object {
            Test-SampleAssertion -Document $evidence -Assertion $_
        })
        $failedAssertions = @($assertionResults | Where-Object { -not $_.passed })
        if ($failedAssertions.Count -ne 0) {
            throw (
                'Evidence assertions failed: ' +
                (($failedAssertions | ForEach-Object {
                    "$($_.path) actual=$($_.actual) expected=$($_.expected)"
                }) -join '; '))
        }
        if (@($sample.execution.cleanupUntracked).Count -gt 0) {
            $cleanupRoot = Join-Path $ComparisonRoot `
                ([string]$sample.execution.cleanupRoot)
            Remove-DeclaredUntrackedOutputs `
                -Root $cleanupRoot `
                -RelativePaths @($sample.execution.cleanupUntracked)
        }
        Copy-Item -LiteralPath $evidencePath -Destination $archivedEvidencePath -Force
        $sampleRecords.Add([ordered]@{
            id = [string]$sample.id
            category = [string]$sample.category
            title = [string]$sample.title
            status = 'passed'
            durationMs = [long]([DateTime]::UtcNow - $sampleStartedAt).TotalMilliseconds
            runner = [string]$sample.execution.runner
            runnerLog = $logPath
            evidence = [ordered]@{
                originalPath = $evidencePath
                archivedPath = $archivedEvidencePath
                schemaVersion = [string]$evidence.schemaVersion
                sha256 = (Get-FileHash -LiteralPath $archivedEvidencePath `
                    -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            assertions = $assertionResults
            guarantees = @($sample.guarantees)
            nonClaims = @($sample.nonClaims)
            error = $null
        })
        if ([bool]$sample.execution.producesReusableHostBuild) {
            $hostBuildReady = $true
        }
    } catch {
        $sampleRecords.Add([ordered]@{
            id = [string]$sample.id
            category = [string]$sample.category
            title = [string]$sample.title
            status = 'failed'
            durationMs = [long]([DateTime]::UtcNow - $sampleStartedAt).TotalMilliseconds
            runner = [string]$sample.execution.runner
            runnerLog = $logPath
            evidence = $null
            assertions = @()
            guarantees = @($sample.guarantees)
            nonClaims = @($sample.nonClaims)
            error = $_.Exception.Message
        })
        Write-Warning "Sample '$($sample.id)' failed: $($_.Exception.Message)"
    }
}

$sourceAfter = Get-GitState -Root $repoRoot
$comparisonAfter = @($comparisonRepositories | ForEach-Object { Get-GitState $_ })
$sourcePreserved = Test-GitStatePreserved -Before $sourceBefore -After $sourceAfter
$comparisonPreserved = $true
for ($index = 0; $index -lt $comparisonBefore.Count; $index++) {
    if (-not (Test-GitStatePreserved `
            -Before $comparisonBefore[$index] `
            -After $comparisonAfter[$index])) {
        $comparisonPreserved = $false
    }
}
$failedSamples = @($sampleRecords | Where-Object status -ne 'passed')
$completedAtUtc = [DateTime]::UtcNow
$passed = $failedSamples.Count -eq 0 -and $sourcePreserved -and $comparisonPreserved
$document = [ordered]@{
    schemaVersion = 'unifiedscript.sample-matrix-report/v0.1'
    status = if ($passed) { 'passed' } else { 'failed' }
    productVersion = [string]$catalog.productVersion
    catalog = [ordered]@{
        path = $catalogPath
        schemaVersion = [string]$catalog.schemaVersion
        sha256 = $catalogHash
    }
    startedAtUtc = $startedAtUtc.ToString('O')
    completedAtUtc = $completedAtUtc.ToString('O')
    durationMs = [long]($completedAtUtc - $startedAtUtc).TotalMilliseconds
    engineRoot = $EngineRoot
    comparisonRoot = if ($needsComparison) { $ComparisonRoot } else { $null }
    requestedSampleIds = @($samples | ForEach-Object { [string]$_.id })
    completeCatalog = $samples.Count -eq 5
    source = [ordered]@{
        before = $sourceBefore
        after = $sourceAfter
        preserved = $sourcePreserved
    }
    comparisons = [ordered]@{
        before = $comparisonBefore
        after = $comparisonAfter
        preserved = $comparisonPreserved
    }
    samples = @($sampleRecords)
    summary = [ordered]@{
        total = $sampleRecords.Count
        passed = @($sampleRecords | Where-Object status -eq 'passed').Count
        failed = $failedSamples.Count
    }
}
$document | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $reportPath -Encoding utf8

$summaryLines = [Collections.Generic.List[string]]::new()
$summaryLines.Add('# UnifiedScript sample matrix')
$summaryLines.Add('')
$summaryLines.Add("- Status: **$($document.status.ToUpperInvariant())**")
$summaryLines.Add(('- Product: `{0}`' -f $document.productVersion))
$summaryLines.Add(('- Catalog: `{0}`' -f $catalogPath))
$summaryLines.Add(('- Report: `{0}`' -f $reportPath))
$summaryLines.Add(('- Source preserved: `{0}`' -f $sourcePreserved))
$summaryLines.Add(('- Comparison repositories preserved: `{0}`' -f
    $comparisonPreserved))
$summaryLines.Add('')
$summaryLines.Add('| Category | Sample | Status | Duration | Evidence |')
$summaryLines.Add('| --- | --- | --- | ---: | --- |')
foreach ($sample in $sampleRecords) {
    $evidenceSummary = if ($null -ne $sample.evidence) {
        [string]$sample.evidence.archivedPath
    } else { [string]$sample.error }
    $summaryLines.Add(('| {0} | {1} | {2} | {3} ms | `{4}` |' -f
        $sample.category, $sample.id, $sample.status, $sample.durationMs,
        $evidenceSummary))
}
$summaryLines | Set-Content -LiteralPath $summaryPath -Encoding utf8

if (-not $passed) {
    Write-Error (
        "UnifiedScript sample matrix failed. Samples=$($failedSamples.Count), " +
        "SourcePreserved=$sourcePreserved, ComparisonPreserved=$comparisonPreserved, " +
        "Report=$reportPath")
    exit 1
}
Write-Host "UnifiedScript sample matrix: PASS ($($sampleRecords.Count)/$($sampleRecords.Count))"
Write-Host "Report: $reportPath"
Write-Host "Summary: $summaryPath"
