[CmdletBinding()]
param(
    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,

    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',

    [string]$OutputDirectory,

    [ValidateRange(10, 300)]
    [int]$SmokeTimeoutSeconds = 90,

    [switch]$PlanOnly,

    [switch]$AllowDirtySource,

    [switch]$AllowDirtyComparison
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseManifestPath = Join-Path $repoRoot 'release\unifiedscript.release.json'
$startedAtUtc = [DateTime]::UtcNow
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' +
    [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot "artifacts\release-readiness\$runId"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
$reportPath = Join-Path $runDirectory 'release-readiness.json'
$summaryPath = Join-Path $runDirectory 'release-readiness.md'
$powershell = Join-Path $env:SystemRoot `
    'System32\WindowsPowerShell\v1.0\powershell.exe'
$python = (Get-Command python -ErrorAction Stop).Source
$phaseRecords = [Collections.Generic.List[object]]::new()
$gateRecords = [Collections.Generic.List[object]]::new()
$failure = $null
$status = 'failed'

$gateProviders = [ordered]@{
    'managed-tests' = 'managed-tests'
    'repository-validator' = 'repository-validator'
    'editor-development-build' = 'compatibility-matrix'
    'editor-automation' = 'compatibility-matrix'
    'source-development-shipping-package' = 'compatibility-matrix'
    'deterministic-sdk-package' = 'compatibility-matrix'
    'sample-matrix' = 'sample-matrix'
    'project-template-and-api-browser' = 'project-templates'
    'semantic-diff-merge' = 'managed-tests'
    'ai-semantic-proposals' = 'managed-tests'
    'integrated-runtime-insights' = 'managed-tests'
    'generated-data-plane-extensions' = 'managed-tests+deterministic-generation+compatibility-matrix'
    'clean-project-sdk-install' = 'compatibility-matrix'
    'clean-project-development-package' = 'compatibility-matrix'
    'matched-performance-workload' = 'workload-performance'
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) |
        Out-Null
    [IO.File]::WriteAllText(
        $Path,
        $Content.Replace("`r`n", "`n"),
        [Text.UTF8Encoding]::new($false))
}

function Require-ReleaseCondition {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-GitState {
    param([Parameter(Mandatory)][string]$Root)

    $revision = (& git -C $Root rev-parse HEAD 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($revision)) {
        throw "Git revision is unavailable for '$Root'."
    }
    $entries = @(& git -C $Root status --porcelain=v1 --untracked-files=all 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Git status is unavailable for '$Root'."
    }
    return [ordered]@{
        root = [IO.Path]::GetFullPath($Root)
        revision = $revision
        dirty = $entries.Count -ne 0
        entries = $entries
    }
}

function Test-GitStatePreserved {
    param(
        [Parameter(Mandatory)]$Before,
        [Parameter(Mandatory)]$After
    )

    return $Before.revision -eq $After.revision -and
        (@($Before.entries) -join "`n") -ceq (@($After.entries) -join "`n")
}

function Get-RelativeArtifactPath {
    param([Parameter(Mandatory)][string]$Path)

    $basePath = [IO.Path]::GetFullPath($runDirectory).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    $baseUri = [Uri]$basePath
    $pathUri = [Uri][IO.Path]::GetFullPath($Path)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString())
}

function Invoke-ReleasePhase {
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
    Write-Host "[release/$Name]"
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
    $record = [ordered]@{
        name = $Name
        status = if ($exitCode -eq 0) { 'passed' } else { 'failed' }
        exitCode = $exitCode
        durationMs = [long]([DateTime]::UtcNow - $phaseStartedAt).TotalMilliseconds
        log = Get-RelativeArtifactPath $logPath
        artifacts = @($Artifacts | ForEach-Object {
            Get-RelativeArtifactPath $_
        })
    }
    $phaseRecords.Add($record)
    if ($exitCode -ne 0) {
        throw "Release phase '$Name' failed with exit code $exitCode."
    }
}

function Read-StrictJson {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$SchemaVersion
    )

    Require-ReleaseCondition `
        -Condition (Test-Path -LiteralPath $Path -PathType Leaf) `
        -Message "Release evidence is missing: $Path"
    $document = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Require-ReleaseCondition `
        -Condition ($document.schemaVersion -eq $SchemaVersion) `
        -Message "Evidence '$Path' has schema '$($document.schemaVersion)', expected '$SchemaVersion'."
    return $document
}

function Get-ManagedTestGroup {
    param(
        [Parameter(Mandatory)]$Results,
        [Parameter(Mandatory)][string]$Token
    )

    $matching = @($Results | Where-Object {
        [string]$_.testName -like "*$Token*"
    })
    return [ordered]@{
        token = $Token
        total = $matching.Count
        passed = @($matching | Where-Object outcome -eq 'Passed').Count
        failed = @($matching | Where-Object outcome -ne 'Passed').Count
    }
}

function Add-GateEvidence {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][bool]$Passed,
        [Parameter(Mandatory)][string[]]$Assertions,
        [string[]]$Artifacts = @()
    )

    $gateRecords.Add([ordered]@{
        id = $Id
        status = if ($Passed) { 'passed' } else { 'failed' }
        provider = [string]$gateProviders[$Id]
        assertions = @($Assertions)
        artifacts = @($Artifacts | ForEach-Object {
            Get-RelativeArtifactPath $_
        })
    })
}

function Test-PackagedRows {
    param([Parameter(Mandatory)][object[]]$Rows)

    if ($Rows.Count -eq 0) { return $false }
    foreach ($row in $Rows) {
        if ($row.build -ne 'passed' -or
            $row.cook -ne 'passed' -or
            $row.stage -ne 'passed' -or
            $row.pak -ne 'passed' -or
            $row.archive -ne 'passed' -or
            $row.packagedSmoke -ne 'passed' -or
            -not [bool]$row.pluginLocalRuntime) {
            return $false
        }
    }
    return $true
}

New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
$sourceBefore = $null
$sourceAfter = $null
$comparisonBefore = @()
$comparisonAfter = @()
$releaseManifest = $null

try {
    $releaseManifest = Read-StrictJson `
        -Path $releaseManifestPath `
        -SchemaVersion 'unifiedscript.product-release/v0.1'
    Require-ReleaseCondition `
        -Condition ($releaseManifest.productVersion -eq '1.0.0-rc.1' -and
            $releaseManifest.channel -eq 'rc' -and
            $releaseManifest.status -eq 'releaseCandidate') `
        -Message 'Release readiness only accepts the declared UnifiedScript 1.0 RC1 manifest.'
    $requiredGates = @($releaseManifest.acceptance.requiredGates |
        ForEach-Object { [string]$_ })
    $providerGates = @($gateProviders.Keys | ForEach-Object { [string]$_ })
    $gateDifference = @(Compare-Object $requiredGates $providerGates)
    Require-ReleaseCondition `
        -Condition ($requiredGates.Count -eq $providerGates.Count -and
            @($requiredGates | Select-Object -Unique).Count -eq $requiredGates.Count -and
            $gateDifference.Count -eq 0) `
        -Message 'Every release requiredGate must have exactly one release-readiness provider.'
    Require-ReleaseCondition `
        -Condition (@($releaseManifest.documentation).Count -ge 1 -and
            @($releaseManifest.knownLimitations).Count -ge 1) `
        -Message 'Release documentation and known limitations must be explicit.'
    foreach ($document in @($releaseManifest.documentation)) {
        $relativePath = ([string]$document.path).Replace('/', '\')
        Require-ReleaseCondition `
            -Condition (-not [IO.Path]::IsPathRooted($relativePath) -and
                $relativePath.Split('\') -notcontains '..' -and
                (Test-Path -LiteralPath (Join-Path $repoRoot $relativePath) -PathType Leaf)) `
            -Message "Release document is missing or unsafe: $($document.path)"
    }

    $sourceBefore = Get-GitState -Root $repoRoot
    if ($sourceBefore.dirty -and -not $AllowDirtySource) {
        throw 'Release readiness requires a clean source checkout. Use -AllowDirtySource only for diagnostics.'
    }

    if (-not $PlanOnly) {
        if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
            $EngineRoot = @('D:\UE_5.8', 'D:\UE\_5.8') |
                Where-Object {
                    Test-Path -LiteralPath (Join-Path $_ `
                        'Engine\Build\Build.version') -PathType Leaf
                } | Select-Object -First 1
        }
        Require-ReleaseCondition `
            -Condition (-not [string]::IsNullOrWhiteSpace($EngineRoot)) `
            -Message 'Pass -EngineRoot or set UNIFIEDSCRIPT_UE_ROOT.'
        $EngineRoot = [IO.Path]::GetFullPath($EngineRoot)
    }

    if ($PlanOnly) {
        foreach ($gateId in $requiredGates) {
            $gateRecords.Add([ordered]@{
                id = $gateId
                status = 'planned'
                provider = [string]$gateProviders[$gateId]
                assertions = @()
                artifacts = @()
            })
        }
        $status = 'planned'
    } else {
        $buildVersionPath = Join-Path $EngineRoot 'Engine\Build\Build.version'
        $buildVersion = Get-Content -LiteralPath $buildVersionPath -Raw |
            ConvertFrom-Json
        $engineVersion = '{0}.{1}.{2}' -f $buildVersion.MajorVersion,
            $buildVersion.MinorVersion, $buildVersion.PatchVersion
        Require-ReleaseCondition `
            -Condition ($engineVersion -eq
                [string]$releaseManifest.compatibility.verifiedEngineVersion) `
            -Message "Engine '$engineVersion' does not match the release manifest."
        $dotnet = Join-Path $EngineRoot `
            'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
        Require-ReleaseCondition `
            -Condition (Test-Path -LiteralPath $dotnet -PathType Leaf) `
            -Message "Engine-bundled .NET 10 is missing: $dotnet"

        $ComparisonRoot = [IO.Path]::GetFullPath($ComparisonRoot)
        foreach ($name in @(
                'BlockBreaker-UnifiedScript',
                'BlockBreakerSource',
                'BlockBreaker-Puerts')) {
            $comparisonPath = Join-Path $ComparisonRoot $name
            Require-ReleaseCondition `
                -Condition (Test-Path -LiteralPath $comparisonPath -PathType Container) `
                -Message "Comparison repository is missing: $comparisonPath"
            $comparisonBefore += Get-GitState -Root $comparisonPath
        }
        if (-not $AllowDirtyComparison -and
            @($comparisonBefore | Where-Object dirty).Count -ne 0) {
            throw 'Release readiness requires clean comparison repositories.'
        }

        $managedDirectory = Join-Path $runDirectory 'managed'
        $managedTrx = Join-Path $managedDirectory 'managed-tests.trx'
        New-Item -ItemType Directory -Force -Path $managedDirectory | Out-Null
        Invoke-ReleasePhase -Name 'managed-tests' -FilePath $dotnet -Arguments @(
            'test', (Join-Path $repoRoot 'UnifiedScript.slnx'),
            '-c', 'Debug', '--nologo',
            '--logger', 'trx;LogFileName=managed-tests.trx',
            '--results-directory', $managedDirectory) -Artifacts @($managedTrx)

        $validatorReport = Join-Path $managedDirectory 'repository-validator.log'
        Invoke-ReleasePhase -Name 'repository-validator' -FilePath $python -Arguments @(
            (Join-Path $repoRoot 'tools\validate_project.py')) -Artifacts @($validatorReport)
        Copy-Item -LiteralPath (Join-Path $runDirectory '02-repository-validator.log') `
            -Destination $validatorReport -Force

        $generationDirectory = Join-Path $runDirectory 'deterministic-generation'
        $generationReportPath = Join-Path $generationDirectory `
            'deterministic-generation.json'
        Invoke-ReleasePhase -Name 'deterministic-generation' `
            -FilePath $powershell -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repoRoot 'tools\run-deterministic-generation-gate.ps1'),
                '-DotNet', $dotnet,
                '-OutputDirectory', $generationDirectory) `
            -Artifacts @($generationReportPath)

        $matrixDirectory = Join-Path $runDirectory 'compatibility-matrix'
        $matrixReportPath = Join-Path $matrixDirectory 'compatibility-matrix.json'
        Invoke-ReleasePhase -Name 'compatibility-matrix' `
            -FilePath $powershell -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repoRoot 'tools\run-unreal-compatibility-matrix.ps1'),
                '-EngineRoot', $EngineRoot,
                '-OutputDirectory', $matrixDirectory,
                '-SdkVersion', [string]$releaseManifest.productVersion,
                '-SmokeTimeoutSeconds', $SmokeTimeoutSeconds,
                '-ExpectedAutomationTests',
                [string]$releaseManifest.acceptance.unrealAutomationTests) `
            -Artifacts @($matrixReportPath)

        $sampleDirectory = Join-Path $runDirectory 'sample-matrix'
        $sampleReportPath = Join-Path $sampleDirectory 'sample-matrix.json'
        $sampleArguments = @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            (Join-Path $repoRoot 'tools\run-unifiedscript-sample-matrix.ps1'),
            '-EngineRoot', $EngineRoot,
            '-ComparisonRoot', $ComparisonRoot,
            '-OutputDirectory', $sampleDirectory)
        if ($AllowDirtySource) { $sampleArguments += '-AllowDirtySource' }
        if ($AllowDirtyComparison) { $sampleArguments += '-AllowDirtyComparison' }
        Invoke-ReleasePhase -Name 'sample-matrix' `
            -FilePath $powershell -Arguments $sampleArguments `
            -Artifacts @($sampleReportPath)

        $templateDirectory = Join-Path $runDirectory 'project-templates'
        $templateReportPath = Join-Path $templateDirectory `
            'project-template-acceptance.json'
        Invoke-ReleasePhase -Name 'project-templates' `
            -FilePath $powershell -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repoRoot 'tools\test-unifiedscript-project-templates.ps1'),
                '-EngineRoot', $EngineRoot,
                '-OutputDirectory', $templateDirectory) `
            -Artifacts @($templateReportPath)

        $performanceDirectory = Join-Path $runDirectory 'workload-performance'
        $performanceReportPath = Join-Path $performanceDirectory 'ci-lane.json'
        Invoke-ReleasePhase -Name 'workload-performance' `
            -FilePath $powershell -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                (Join-Path $repoRoot 'tools\run-ci-lane.ps1'),
                '-Lane', 'workload-performance',
                '-EngineRoot', $EngineRoot,
                '-ComparisonRoot', $ComparisonRoot,
                '-OutputDirectory', $performanceDirectory) `
            -Artifacts @($performanceReportPath)

        [xml]$trx = Get-Content -LiteralPath $managedTrx -Raw
        $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
        $testResults = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
        $managedPassed = [int]$counters.passed -eq
            [int]$releaseManifest.acceptance.managedTests -and
            [int]$counters.failed -eq 0
        $semanticTests = Get-ManagedTestGroup $testResults 'SemanticGraphReviewTests'
        $aiTests = Get-ManagedTestGroup $testResults 'AiSemanticPatchProposalTests'
        $insightTests = Get-ManagedTestGroup $testResults 'UnrealAuthoringInsightTests'
        $extensionTests = Get-ManagedTestGroup `
            $testResults 'UnrealDataPlaneExtensionGeneratorTests'

        $validatorText = Get-Content -LiteralPath $validatorReport -Raw
        $validatorPassed = $validatorText -match (
            'Validated {0} repository invariants\.' -f
            [int]$releaseManifest.acceptance.repositoryInvariants)
        $generation = Read-StrictJson $generationReportPath `
            'unifiedscript.deterministic-generation-gate/v0.1'
        $generationPassed = $generation.status -eq 'passed' -and
            [int]$generation.artifactCount -eq 9 -and
            @($generation.artifacts | Where-Object {
                -not $_.matchesSecondRun -or -not $_.matchesCheckedIn
            }).Count -eq 0
        $matrix = Read-StrictJson $matrixReportPath `
            'unifiedscript.unreal-compatibility-matrix/v0.1'
        $matrixRevisionMatches = $matrix.sourceRevision -eq $sourceBefore.revision
        $editorRows = @($matrix.supported | Where-Object target -eq 'Editor')
        $gameRows = @($matrix.supported | Where-Object target -eq 'Game')
        $sdkRows = @($matrix.supported | Where-Object target -eq 'SDK-installed Game')
        $automationPassed = $editorRows.Count -eq 1 -and
            [int]$editorRows[0].automation.passed -eq
                [int]$releaseManifest.acceptance.unrealAutomationTests -and
            [int]$editorRows[0].automation.expected -eq
                [int]$releaseManifest.acceptance.unrealAutomationTests
        $sourcePackagesPassed = $gameRows.Count -eq 2 -and
            ((@($gameRows.configuration | Sort-Object) -join ',') -eq
                'Development,Shipping') -and (Test-PackagedRows $gameRows)
        $sdkPackagePassed = $matrix.sdk.deterministic -and
            $matrix.sdk.archiveSha256 -eq $matrix.sdk.repeatArchiveSha256
        $cleanInstallPassed = $matrix.sdk.cleanProjectInstall -eq 'passed'
        $cleanPackagePassed = $sdkRows.Count -eq 1 -and
            (Test-PackagedRows $sdkRows) -and
            [bool]$sdkRows[0].externalDotNetRootsPoisoned
        $automationText = Get-Content -LiteralPath `
            ([string]$matrix.outputs.automationLog) -Raw
        $extensionAutomationPassed = $automationText -match
            'Result=\{Success\}.*Path=\{UnifiedScript\.Unreal\.DataPlane\.GeneratedExtensionRegistry\}'

        $sampleMatrix = Read-StrictJson $sampleReportPath `
            'unifiedscript.sample-matrix-report/v0.1'
        $samplePassed = $sampleMatrix.status -eq 'passed' -and
            $sampleMatrix.productVersion -eq $releaseManifest.productVersion -and
            [int]$sampleMatrix.summary.total -eq 5 -and
            [int]$sampleMatrix.summary.passed -eq 5 -and
            [bool]$sampleMatrix.source.preserved -and
            [bool]$sampleMatrix.comparisons.preserved
        $templates = Read-StrictJson $templateReportPath `
            'unifiedscript.project-template-acceptance/v0.1'
        $templatesPassed = $templates.status -eq 'passed' -and
            $templates.productVersion -eq $releaseManifest.productVersion -and
            [int]$templates.summary.total -eq 3 -and
            [int]$templates.summary.passed -eq 3
        $performance = Read-StrictJson $performanceReportPath `
            'unifiedscript.ci-lane/v0.1'
        $performanceBudgetPath = Join-Path $performanceDirectory `
            'performance\budget-report.json'
        $performanceBudget = Read-StrictJson $performanceBudgetPath `
            'unifiedscript.performance-budget-report/v0.1'
        $performancePassed = $performance.status -eq 'passed' -and
            $performance.sourceRevision -eq $sourceBefore.revision -and
            (-not [bool]$performance.sourceDirty) -and
            $performanceBudget.status -eq 'qualified'

        Add-GateEvidence 'managed-tests' $managedPassed @(
            "$($counters.passed)/$($releaseManifest.acceptance.managedTests) managed tests passed") @($managedTrx)
        Add-GateEvidence 'repository-validator' $validatorPassed @(
            "$($releaseManifest.acceptance.repositoryInvariants) repository invariants passed") @($validatorReport)
        Add-GateEvidence 'editor-development-build' (
            $matrix.status -eq 'passed' -and $matrixRevisionMatches -and
            $editorRows.Count -eq 1 -and $editorRows[0].build -eq 'passed') @(
                'UE 5.8.1 Editor Win64 Development build passed on the release revision') @($matrixReportPath)
        Add-GateEvidence 'editor-automation' $automationPassed @(
            "$($releaseManifest.acceptance.unrealAutomationTests) UE Automation tests passed") @($matrixReportPath)
        Add-GateEvidence 'source-development-shipping-package' $sourcePackagesPassed @(
            'Source-tree Development and Shipping build/cook/stage/pak/archive/smoke passed') @($matrixReportPath)
        Add-GateEvidence 'deterministic-sdk-package' $sdkPackagePassed @(
            "Repeated SDK SHA-256 is $($matrix.sdk.archiveSha256)") @($matrixReportPath)
        Add-GateEvidence 'sample-matrix' $samplePassed @(
            'All five versioned sample categories passed and preserved source state') @($sampleReportPath)
        Add-GateEvidence 'project-template-and-api-browser' (
            $templatesPassed -and $validatorPassed) @(
                'Three project templates passed',
                'Repository invariant proves the Contract-owned API browser') @($templateReportPath, $validatorReport)
        Add-GateEvidence 'semantic-diff-merge' (
            $semanticTests.total -gt 0 -and $semanticTests.failed -eq 0 -and
            $validatorPassed) @(
                "$($semanticTests.passed) semantic review tests passed",
                'Repository invariant proves release wiring') @($managedTrx, $validatorReport)
        Add-GateEvidence 'ai-semantic-proposals' (
            $aiTests.total -gt 0 -and $aiTests.failed -eq 0 -and
            $validatorPassed) @(
                "$($aiTests.passed) typed AI proposal tests passed",
                'Repository invariant proves release wiring') @($managedTrx, $validatorReport)
        Add-GateEvidence 'integrated-runtime-insights' (
            $insightTests.total -gt 0 -and $insightTests.failed -eq 0 -and
            $automationPassed -and $validatorPassed) @(
                "$($insightTests.passed) runtime insight tests passed",
                'Live UE Automation and release wiring passed') @($managedTrx, $matrixReportPath, $validatorReport)
        Add-GateEvidence 'generated-data-plane-extensions' (
            $extensionTests.total -gt 0 -and $extensionTests.failed -eq 0 -and
            $generationPassed -and $extensionAutomationPassed -and
            $validatorPassed) @(
                "$($extensionTests.passed) managed extension tests passed",
                'Nine generated artifacts are deterministic and current',
                'GeneratedExtensionRegistry UE Automation passed') @($managedTrx, $generationReportPath, $matrixReportPath)
        Add-GateEvidence 'clean-project-sdk-install' $cleanInstallPassed @(
            'SDK installed into a plugin-free project export') @($matrixReportPath)
        Add-GateEvidence 'clean-project-development-package' $cleanPackagePassed @(
            'SDK-installed Development package passed with poisoned external .NET roots') @($matrixReportPath)
        Add-GateEvidence 'matched-performance-workload' $performancePassed @(
            'Nine-workload matched performance budget is qualified') @($performanceReportPath, $performanceBudgetPath)

        $sourceAfter = Get-GitState -Root $repoRoot
        foreach ($before in $comparisonBefore) {
            $comparisonAfter += Get-GitState -Root ([string]$before.root)
        }
        $sourcePreserved = Test-GitStatePreserved $sourceBefore $sourceAfter
        $comparisonsPreserved = $comparisonBefore.Count -eq $comparisonAfter.Count
        for ($index = 0; $index -lt $comparisonBefore.Count; $index++) {
            $comparisonsPreserved = $comparisonsPreserved -and
                (Test-GitStatePreserved `
                    $comparisonBefore[$index] $comparisonAfter[$index])
        }
        Require-ReleaseCondition `
            -Condition $sourcePreserved `
            -Message 'Release readiness changed the source checkout.'
        Require-ReleaseCondition `
            -Condition $comparisonsPreserved `
            -Message 'Release readiness changed a comparison checkout.'
        $failedGates = @($gateRecords | Where-Object status -ne 'passed')
        Require-ReleaseCondition `
            -Condition ($gateRecords.Count -eq $requiredGates.Count -and
                $failedGates.Count -eq 0) `
            -Message "Release required gates failed: $($failedGates.id -join ', ')"
        $status = if ($sourceBefore.dirty -or
            @($comparisonBefore | Where-Object dirty).Count -ne 0) {
            'passed-local-diagnostics'
        } else {
            'passed'
        }
    }
} catch {
    $failure = [ordered]@{
        message = $_.Exception.Message
        phase = if ($phaseRecords.Count -eq 0) {
            'preflight'
        } else {
            [string]$phaseRecords[-1].name
        }
    }
} finally {
    $completedAtUtc = [DateTime]::UtcNow
    $report = [ordered]@{
        schemaVersion = 'unifiedscript.release-readiness/v0.1'
        status = $status
        productVersion = if ($null -eq $releaseManifest) {
            $null
        } else {
            [string]$releaseManifest.productVersion
        }
        startedAtUtc = $startedAtUtc.ToString('O')
        completedAtUtc = $completedAtUtc.ToString('O')
        durationMs = [long]($completedAtUtc - $startedAtUtc).TotalMilliseconds
        source = [ordered]@{
            before = $sourceBefore
            after = $sourceAfter
            preserved = if ($null -eq $sourceAfter) { $null } else {
                Test-GitStatePreserved $sourceBefore $sourceAfter
            }
        }
        comparisons = [ordered]@{
            before = $comparisonBefore
            after = $comparisonAfter
        }
        engineRoot = $EngineRoot
        releaseManifest = $releaseManifestPath
        planOnly = [bool]$PlanOnly
        phases = @($phaseRecords)
        requiredGates = @($gateRecords)
        summary = [ordered]@{
            total = $gateRecords.Count
            passed = @($gateRecords | Where-Object status -eq 'passed').Count
            planned = @($gateRecords | Where-Object status -eq 'planned').Count
            failed = @($gateRecords | Where-Object status -eq 'failed').Count
        }
        failure = $failure
    }
    Write-Utf8File $reportPath (($report | ConvertTo-Json -Depth 14) + "`n")

    $summaryLines = [Collections.Generic.List[string]]::new()
    $summaryLines.Add('# UnifiedScript 1.0 release readiness')
    $summaryLines.Add('')
    $summaryLines.Add("- Status: **$($status.ToUpperInvariant())**")
    $summaryLines.Add(('- Product: `{0}`' -f $report.productVersion))
    $summaryLines.Add(('- Source revision: `{0}`' -f $sourceBefore.revision))
    $summaryLines.Add(('- Report: `{0}`' -f $reportPath))
    $summaryLines.Add('')
    $summaryLines.Add('| Required gate | Provider | Status |')
    $summaryLines.Add('| --- | --- | --- |')
    foreach ($gate in $gateRecords) {
        $summaryLines.Add("| $($gate.id) | $($gate.provider) | $($gate.status) |")
    }
    if ($null -ne $failure) {
        $summaryLines.Add('')
        $summaryLines.Add('## Failure')
        $summaryLines.Add('')
        $summaryLines.Add(('- Phase: `{0}`' -f $failure.phase))
        $summaryLines.Add("- Message: $($failure.message)")
    }
    Write-Utf8File $summaryPath (($summaryLines -join "`n") + "`n")
}

if ($null -ne $failure) {
    Write-Error "UnifiedScript release readiness failed: $($failure.message) Report=$reportPath"
    exit 1
}
Write-Host "UnifiedScript release readiness: $($status.ToUpperInvariant())"
Write-Host "RequiredGates=$($gateRecords.Count)"
Write-Host "Report: $reportPath"
Write-Host "Summary: $summaryPath"
