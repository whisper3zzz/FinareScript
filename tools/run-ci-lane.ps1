[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'managed-tests',
        'deterministic-generation',
        'ue-build',
        'unreal-automation',
        'package-smoke',
        'workload-performance',
        'sdk-installation')]
    [string]$Lane,
    [string]$EngineRoot = $env:FINARE_UE_ROOT,
    [string]$ComparisonRoot = $env:FINARE_COMPARISON_ROOT,
    [string]$OutputDirectory,
    [switch]$RequireCleanTree
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot "artifacts\ci\$Lane"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
$reportPath = Join-Path $runDirectory 'ci-lane.json'
$startedAt = [DateTime]::UtcNow
$sourceRevision = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
$sourceStatus = @(& git -C $repoRoot status --porcelain --untracked-files=all 2>$null)
$sourceDirty = $sourceStatus.Count -ne 0
$phaseRecords = [Collections.Generic.List[object]]::new()
$failure = $null
$status = 'failed'

function Get-RelativeArtifactPath([string]$Path) {
    $basePath = [IO.Path]::GetFullPath($runDirectory).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    $baseUri = [Uri]$basePath
    $pathUri = [Uri][IO.Path]::GetFullPath($Path)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString())
}

function Invoke-CiPhase {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][object[]]$Arguments,
        [string[]]$Artifacts = @()
    )

    $phaseNumber = $phaseRecords.Count + 1
    $slug = $Name.ToLowerInvariant() -replace '[^a-z0-9]+', '-'
    $logPath = Join-Path $runDirectory ('{0:D2}-{1}.log' -f $phaseNumber, $slug)
    $phaseStartedAt = [DateTime]::UtcNow
    Write-Host "[$Lane/$Name]"
    $previousPreference = $ErrorActionPreference
    $exitCode = -1
    try {
        $ErrorActionPreference = 'Continue'
        & $FilePath @Arguments 2>&1 |
            Tee-Object -FilePath $logPath |
            ForEach-Object { Write-Host ([string]$_) }
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    $durationMs = [long]([DateTime]::UtcNow - $phaseStartedAt).TotalMilliseconds
    $record = [ordered]@{
        name = $Name
        status = if ($exitCode -eq 0) { 'passed' } else { 'failed' }
        exitCode = $exitCode
        durationMs = $durationMs
        log = Get-RelativeArtifactPath $logPath
        artifacts = @($Artifacts | ForEach-Object { Get-RelativeArtifactPath $_ })
    }
    $phaseRecords.Add($record)
    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode."
    }
}

function Resolve-DotNet {
    if (-not [string]::IsNullOrWhiteSpace($EngineRoot)) {
        $engineDotNet = Join-Path $EngineRoot `
            'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
        if (Test-Path -LiteralPath $engineDotNet -PathType Leaf) {
            $engineDotNetDirectory = Split-Path -Parent $engineDotNet
            $env:DOTNET_ROOT = $engineDotNetDirectory
            $env:PATH = "$engineDotNetDirectory;$env:PATH"
            return $engineDotNet
        }
    }
    return (Get-Command dotnet -ErrorAction Stop).Source
}

function Require-UnrealEngine {
    if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
        throw "CI lane '$Lane' requires -EngineRoot or FINARE_UE_ROOT."
    }
    $script:EngineRoot = [IO.Path]::GetFullPath($EngineRoot)
    $buildVersionPath = Join-Path $script:EngineRoot 'Engine\Build\Build.version'
    if (-not (Test-Path -LiteralPath $buildVersionPath -PathType Leaf)) {
        throw "Unreal Build.version is missing: $buildVersionPath"
    }
    $buildVersion = Get-Content -LiteralPath $buildVersionPath -Raw | ConvertFrom-Json
    $version = '{0}.{1}.{2}' -f $buildVersion.MajorVersion,
        $buildVersion.MinorVersion, $buildVersion.PatchVersion
    if ($version -ne '5.8.1') {
        throw "CI Unreal lanes require UE 5.8.1; '$script:EngineRoot' is $version."
    }
}

function Invoke-GitPathQuery {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$FailureMessage
    )

    # Native stderr is promoted to the PowerShell error stream. Git can write
    # harmless line-ending warnings there while still returning valid paths,
    # so keep stderr isolated and make the native exit code authoritative.
    $previousPreference = $ErrorActionPreference
    $exitCode = -1
    try {
        $ErrorActionPreference = 'Continue'
        $paths = @(& git -C $Root @Arguments 2>$null)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) {
        throw $FailureMessage
    }
    return @($paths | ForEach-Object { [string]$_ })
}

function New-CleanGitCheckoutSnapshot {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$SnapshotRoot
    )

    $resolvedRoot = [IO.Path]::GetFullPath($Root)
    $entries = @(Invoke-GitPathQuery -Root $resolvedRoot -Arguments @(
            'status', '--porcelain=v1', '--untracked-files=all') `
            -FailureMessage "Cannot inspect comparison checkout: $resolvedRoot")
    if ($entries.Count -ne 0) {
        throw (
            'The workload-performance lane requires a clean Finare ' +
            "comparison checkout before it can preserve it: $resolvedRoot")
    }

    $resolvedSnapshot = [IO.Path]::GetFullPath($SnapshotRoot)
    $runPrefix = [IO.Path]::GetFullPath($runDirectory).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedSnapshot.StartsWith(
            $runPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe comparison snapshot path: $resolvedSnapshot"
    }
    New-Item -ItemType Directory -Force -Path $resolvedSnapshot | Out-Null
    $snapshotPrefix = $resolvedSnapshot.TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    & git -C $resolvedRoot checkout-index --all "--prefix=$snapshotPrefix"
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot snapshot comparison checkout: $resolvedRoot"
    }

    return [pscustomobject]@{
        Root = $resolvedRoot
        SnapshotRoot = $resolvedSnapshot
    }
}

function Restore-CleanGitCheckoutSnapshot {
    param([Parameter(Mandatory)]$Snapshot)

    $resolvedRoot = [IO.Path]::GetFullPath([string]$Snapshot.Root)
    $rootPrefix = $resolvedRoot.TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    $resolvedSnapshot = [IO.Path]::GetFullPath([string]$Snapshot.SnapshotRoot)
    $runPrefix = [IO.Path]::GetFullPath($runDirectory).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedSnapshot.StartsWith(
            $runPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe comparison snapshot restore path: $resolvedSnapshot"
    }

    $workingTreePaths = @(Invoke-GitPathQuery -Root $resolvedRoot -Arguments @(
            'diff', '--name-only') -FailureMessage (
            "Cannot inspect comparison working-tree changes: $resolvedRoot"))
    $indexPaths = @(Invoke-GitPathQuery -Root $resolvedRoot -Arguments @(
            'diff', '--cached', '--name-only') -FailureMessage (
            "Cannot inspect comparison index changes: $resolvedRoot"))
    $trackedPaths = @($workingTreePaths; $indexPaths) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
    foreach ($relativePath in $trackedPaths) {
        $nativeRelative = [string]$relativePath -replace '/', '\'
        $source = [IO.Path]::GetFullPath((Join-Path `
            $resolvedSnapshot $nativeRelative))
        $destination = [IO.Path]::GetFullPath((Join-Path `
            $resolvedRoot $nativeRelative))
        if (-not $destination.StartsWith(
                $rootPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe comparison restore target: $destination"
        }
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            New-Item -ItemType Directory -Force -Path (
                Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination -Force
        } elseif (Test-Path -LiteralPath $destination -PathType Leaf) {
            Remove-Item -LiteralPath $destination -Force
        }
    }

    $untrackedPaths = @(Invoke-GitPathQuery -Root $resolvedRoot -Arguments @(
            'ls-files', '--others', '--exclude-standard') -FailureMessage (
            "Cannot inspect comparison untracked files: $resolvedRoot"))
    foreach ($relativePath in $untrackedPaths) {
        $nativeRelative = [string]$relativePath -replace '/', '\'
        $target = [IO.Path]::GetFullPath((Join-Path $resolvedRoot $nativeRelative))
        if (-not $target.StartsWith(
                $rootPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe comparison cleanup target: $target"
        }
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            Remove-Item -LiteralPath $target -Force
        }
    }

    $remaining = @(Invoke-GitPathQuery -Root $resolvedRoot -Arguments @(
            'status', '--porcelain=v1', '--untracked-files=all') `
            -FailureMessage "Cannot verify comparison checkout: $resolvedRoot")
    if ($remaining.Count -ne 0) {
        throw "Comparison checkout restoration failed: $resolvedRoot"
    }
    $snapshotDeletePath = $resolvedSnapshot
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT -and
        -not $snapshotDeletePath.StartsWith('\\?\')) {
        $snapshotDeletePath = if ($snapshotDeletePath.StartsWith('\\')) {
            '\\?\UNC\' + $snapshotDeletePath.TrimStart('\')
        } else {
            '\\?\' + $snapshotDeletePath
        }
    }
    [IO.Directory]::Delete($snapshotDeletePath, $true)
}

try {
    if ([string]::IsNullOrWhiteSpace($sourceRevision)) {
        throw 'CI lanes require a Git checkout with a resolvable HEAD.'
    }
    if ($RequireCleanTree -and $sourceDirty) {
        throw "CI lane '$Lane' requires a clean checkout. Dirty entries: $($sourceStatus.Count)."
    }
    if (Test-Path -LiteralPath $runDirectory) {
        $existing = @(Get-ChildItem -LiteralPath $runDirectory -Force)
        if ($existing.Count -gt 0) {
            throw "CI lane output directory must be empty: $runDirectory"
        }
    }
    New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
    $powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
    $dotnet = Resolve-DotNet
    $project = Join-Path $repoRoot 'HostProject\FinareHost.uproject'

    switch ($Lane) {
        'managed-tests' {
            $trx = Join-Path $runDirectory 'managed-tests.trx'
            Invoke-CiPhase -Name 'managed-tests' -FilePath $dotnet -Arguments @(
                'test', (Join-Path $repoRoot 'Finare.slnx'),
                '-c', 'Debug', '--nologo',
                '--logger', 'trx;LogFileName=managed-tests.trx',
                '--results-directory', $runDirectory) -Artifacts @($trx)
            $python = (Get-Command python -ErrorAction Stop).Source
            Invoke-CiPhase -Name 'repository-validator' -FilePath $python -Arguments @(
                (Join-Path $repoRoot 'tools\validate_project.py'))
            Invoke-CiPhase -Name 'api-library-build-graph' -FilePath $powershell -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repoRoot 'tools\test-api-library-build-graph.ps1'))
        }
        'deterministic-generation' {
            $generationReport = Join-Path $runDirectory 'generation\deterministic-generation.json'
            Invoke-CiPhase -Name 'deterministic-generation' -FilePath $powershell -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repoRoot 'tools\run-deterministic-generation-gate.ps1'),
                '-DotNet', $dotnet,
                '-OutputDirectory', (Join-Path $runDirectory 'generation')) `
                -Artifacts @($generationReport)
        }
        'ue-build' {
            Require-UnrealEngine
            $dotnet = Resolve-DotNet
            Invoke-CiPhase -Name 'managed-release-build' -FilePath $dotnet -Arguments @(
                'build',
                (Join-Path $repoRoot 'samples\Unreal\Finare.Unreal.Demo\Finare.Unreal.Demo.csproj'),
                '-c', 'Release', '--nologo')
            Invoke-CiPhase -Name 'ue-editor-build' -FilePath (
                Join-Path $EngineRoot 'Engine\Build\BatchFiles\Build.bat') -Arguments @(
                'FinareHostEditor', 'Win64', 'Development', $project,
                '-WaitMutex', '-NoHotReloadFromIDE', '-NoUBA', '-MaxParallelActions=4')
        }
        'unreal-automation' {
            Require-UnrealEngine
            $apiLibraryLog = Join-Path $runDirectory 'api-library-automation.log'
            Invoke-CiPhase -Name 'api-library-new-call-reload' -FilePath $powershell -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repoRoot 'tools\test-api-library.ps1'),
                '-EngineRoot', $EngineRoot, '-AutomationLog', $apiLibraryLog) -Artifacts @($apiLibraryLog)
            $onboardingReport = Join-Path $runDirectory 'onboarding-report.json'
            Invoke-CiPhase -Name 'unreal-automation' -FilePath $powershell -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repoRoot 'tools\setup-unreal-project.ps1'),
                '-Project', $project,
                '-EngineRoot', $EngineRoot,
                '-Report', $onboardingReport,
                '-ValidateOnly') -Artifacts @($onboardingReport)
        }
        'package-smoke' {
            Require-UnrealEngine
            $releaseDirectory = Join-Path $runDirectory 'release'
            $releaseReport = Join-Path $releaseDirectory 'release-gate.json'
            Invoke-CiPhase -Name 'development-package-smoke' -FilePath $powershell -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repoRoot 'tools\package-unreal-release.ps1'),
                '-EngineRoot', $EngineRoot,
                '-Configurations', 'Development',
                '-OutputDirectory', $releaseDirectory) -Artifacts @($releaseReport)
        }
        'workload-performance' {
            Require-UnrealEngine
            if ([string]::IsNullOrWhiteSpace($ComparisonRoot)) {
                throw "CI lane '$Lane' requires -ComparisonRoot or FINARE_COMPARISON_ROOT."
            }
            $resolvedComparisonRoot = [IO.Path]::GetFullPath($ComparisonRoot)
            $comparisonSnapshot = New-CleanGitCheckoutSnapshot `
                -Root (Join-Path $resolvedComparisonRoot 'BlockBreaker-Finare') `
                -SnapshotRoot (Join-Path $runDirectory 'comparison-checkout-snapshot')
            try {
                $packageSizeDirectory = Join-Path $runDirectory 'package-size'
                $packageMetricsPath = Join-Path $packageSizeDirectory 'latest.json'
                Invoke-CiPhase -Name 'matched-package-size' -FilePath $powershell -Arguments @(
                    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                    (Join-Path $repoRoot 'comparison\measure-blockbreaker-package-size.ps1'),
                    '-EngineRoot', $EngineRoot,
                    '-ComparisonRoot', $resolvedComparisonRoot,
                    '-OutputDirectory', $packageSizeDirectory) `
                    -Artifacts @($packageMetricsPath)
                Invoke-CiPhase -Name 'matched-workload-performance' -FilePath $powershell -Arguments @(
                    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                    (Join-Path $repoRoot 'comparison\run-complete-workload-suite.ps1'),
                    '-EngineRoot', $EngineRoot,
                    '-ComparisonRoot', $resolvedComparisonRoot,
                    '-PackageMetricsPath', $packageMetricsPath,
                    '-Force',
                    '-RequireComplete')
                $suiteRoot = Join-Path $repoRoot 'artifacts\comparison\suite\v1.1.0'
                $performanceDirectory = Join-Path $runDirectory 'performance'
                New-Item -ItemType Directory -Force -Path $performanceDirectory | Out-Null
                $performanceArtifacts = @()
                foreach ($name in @(
                        'finare.json',
                        'puerts.json',
                        'comparison.json',
                        'budget-report.json')) {
                    $source = Join-Path $suiteRoot $name
                    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                        throw "Complete workload suite did not produce $name."
                    }
                    $destination = Join-Path $performanceDirectory $name
                    Copy-Item -LiteralPath $source -Destination $destination -Force
                    $performanceArtifacts += Get-RelativeArtifactPath $destination
                }
                $budget = Get-Content -LiteralPath (
                    Join-Path $performanceDirectory 'budget-report.json') -Raw | ConvertFrom-Json
                if ($budget.schemaVersion -ne 'unifiedscript.performance-budget-report/v0.1' -or
                    $budget.status -ne 'qualified') {
                    throw "Complete workload budget did not qualify."
                }
                $phaseRecords[-1].artifacts = $performanceArtifacts
            } finally {
                Restore-CleanGitCheckoutSnapshot -Snapshot $comparisonSnapshot
            }
        }
        'sdk-installation' {
            Require-UnrealEngine
            $matrixDirectory = Join-Path $runDirectory 'compatibility-matrix'
            $matrixReport = Join-Path $matrixDirectory 'compatibility-matrix.json'
            Invoke-CiPhase -Name 'clean-sdk-installation' -FilePath $powershell -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repoRoot 'tools\run-unreal-compatibility-matrix.ps1'),
                '-EngineRoot', $EngineRoot,
                '-OutputDirectory', $matrixDirectory,
                '-SdkVersion', '0.1.0-m11.3-ci') -Artifacts @($matrixReport)
            $matrix = Get-Content -LiteralPath $matrixReport -Raw | ConvertFrom-Json
            if ($matrix.status -ne 'passed') {
                throw 'Clean SDK installation matrix did not pass.'
            }
        }
    }
    $status = 'passed'
} catch {
    $failure = [ordered]@{
        message = $_.Exception.Message
        phase = if ($phaseRecords.Count -eq 0) { 'preflight' } else { $phaseRecords[-1].name }
    }
} finally {
    New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
    $completedAt = [DateTime]::UtcNow
    $report = [ordered]@{
        schemaVersion = 'unifiedscript.ci-lane/v0.1'
        lane = $Lane
        status = $status
        sourceRevision = $sourceRevision
        sourceDirty = $sourceDirty
        startedAtUtc = $startedAt.ToString('O')
        completedAtUtc = $completedAt.ToString('O')
        durationMs = [long]($completedAt - $startedAt).TotalMilliseconds
        phases = @($phaseRecords)
        failure = $failure
    }
    [IO.File]::WriteAllText(
        $reportPath,
        ($report | ConvertTo-Json -Depth 8) + "`n",
        [Text.UTF8Encoding]::new($false))
}

# Exit codes are necessary but do not prove that tests ran or that a child
# report accepted the requested scope. Local dirty runs remain labelled dirty;
# the workflow's aggregate verifier never accepts them as CI evidence.
if ($status -eq 'passed') {
    try {
        $python = (Get-Command python -ErrorAction Stop).Source
        $evidenceArguments = @('-B', (Join-Path $repoRoot 'tools\ci_policy.py'),
            'verify-lane', '--report', $reportPath, '--lane', $Lane,
            '--revision', $sourceRevision)
        if (-not $RequireCleanTree) { $evidenceArguments += '--allow-dirty' }
        & $python @evidenceArguments
        $evidenceExitCode = $LASTEXITCODE
        $changedAfterRun = $RequireCleanTree -and @(
            & git -C $repoRoot status --porcelain --untracked-files=all).Count -ne 0
        if ($changedAfterRun) { throw 'Source tree changed during the CI lane.' }
        if ($evidenceExitCode -ne 0) { throw 'Child reports did not establish the required passing coverage.' }
    } catch {
        $status = 'failed'
        $failure = [ordered]@{
            phase = 'evidence-validation'
            message = $_.Exception.Message
        }
        $report.status = $status
        $report.failure = $failure
        [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 8) + "`n",
            [Text.UTF8Encoding]::new($false))
    }
}

if ($status -ne 'passed') {
    Write-Error "Finare CI lane '$Lane' failed: $($failure.message) Report=$reportPath"
    exit 1
}
Write-Host "Finare CI lane '$Lane': PASS"
Write-Host "Report: $reportPath"
