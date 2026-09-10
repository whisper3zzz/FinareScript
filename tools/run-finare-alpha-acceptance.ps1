[CmdletBinding()]
param(
    [string]$EngineRoot = $env:FINARE_UE_ROOT,

    [string]$Project,

    [ValidateSet('Development', 'Shipping')]
    [string]$PackageConfiguration = 'Development',

    [string]$OutputDirectory,

    [switch]$SkipPackageSmoke
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$startedAtUtc = [DateTime]::UtcNow
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$projectPath = if ([string]::IsNullOrWhiteSpace($Project)) {
    Join-Path $repoRoot 'HostProject\FinareHost.uproject'
} else {
    [IO.Path]::GetFullPath($Project)
}
$projectDirectory = Split-Path -Parent $projectPath
$configSnapshots = @(
    'DefaultEngine.ini',
    'DefaultInput.ini' |
        ForEach-Object {
            $path = Join-Path $projectDirectory "Config\$_"
            $existed = Test-Path -LiteralPath $path -PathType Leaf
            [ordered]@{
                path = $path
                existed = $existed
                bytes = if ($existed) { [IO.File]::ReadAllBytes($path) } else { $null }
            }
        })
$runDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot "artifacts\acceptance\$runId"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
$reportPath = Join-Path $runDirectory 'alpha-acceptance.json'
$summaryPath = Join-Path $runDirectory 'alpha-acceptance.md'
$onboardingReportPath = Join-Path $runDirectory 'onboarding-report.json'
$releaseDirectory = Join-Path $runDirectory 'package'
$releaseReportPath = Join-Path $releaseDirectory 'release-gate.json'
$managedTrxPath = Join-Path $runDirectory 'managed-tests.trx'
$projectLoadingFixture = Join-Path $repoRoot `
    'samples\ProjectLoading\ProjectLoadingDemo.csproj'
$script:PhaseRecords = [Collections.Generic.List[object]]::new()
$script:CurrentPhase = 'preflight'
$script:PhaseCounter = 0
$script:Failure = $null
$script:Metrics = [ordered]@{}

function Resolve-EngineAssociation {
    param([string]$Association)

    $customBuildsKey = 'HKCU:\Software\Epic Games\Unreal Engine\Builds'
    if (Test-Path $customBuildsKey) {
        $value = (Get-ItemProperty -Path $customBuildsKey).PSObject.Properties |
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
            if (-not [string]::IsNullOrWhiteSpace($installed)) {
                return [string]$installed
            }
        }
    }
    return $null
}

function Restore-AcceptanceConfigs {
    foreach ($snapshot in $configSnapshots) {
        if ($snapshot.existed) {
            [IO.File]::WriteAllBytes($snapshot.path, $snapshot.bytes)
        } elseif (Test-Path -LiteralPath $snapshot.path) {
            Remove-Item -LiteralPath $snapshot.path -Force
        }
    }
}

function Invoke-AcceptancePhase {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Retry,
        [string[]]$Artifacts = @()
    )

    $script:CurrentPhase = $Name
    $script:PhaseCounter++
    $slug = $Name.ToLowerInvariant() -replace '[^a-z0-9]+', '-'
    $logPath = Join-Path $runDirectory ('{0:D2}-{1}.log' -f $script:PhaseCounter, $slug)
    $phaseStartedAt = [DateTime]::UtcNow
    Write-Host "[$Name]"
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
    if ($exitCode -ne 0) {
        $script:PhaseRecords.Add([ordered]@{
            name = $Name
            status = 'failed'
            durationMs = $durationMs
            exitCode = $exitCode
            log = $logPath
            artifacts = @($Artifacts)
            retry = $Retry
        })
        $script:Failure = [ordered]@{
            code = 'USACC2001'
            phase = $Name
            message = "$Name exited with code $exitCode."
            log = $logPath
            artifacts = @($Artifacts)
            retry = $Retry
        }
        throw [InvalidOperationException]::new($script:Failure.message)
    }
    $script:PhaseRecords.Add([ordered]@{
        name = $Name
        status = 'passed'
        durationMs = $durationMs
        exitCode = $exitCode
        log = $logPath
        artifacts = @($Artifacts)
        retry = $Retry
    })
}

function Write-AcceptanceOutputs {
    param([Parameter(Mandatory)][string]$Status)

    $completedAtUtc = [DateTime]::UtcNow
    $revision = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
    $dirty = @(& git -C $repoRoot status --porcelain 2>$null).Count -ne 0
    $document = [ordered]@{
        schemaVersion = 'unifiedscript.alpha-acceptance/v0.1'
        status = $Status
        startedAtUtc = $startedAtUtc.ToString('O')
        completedAtUtc = $completedAtUtc.ToString('O')
        durationMs = [long]($completedAtUtc - $startedAtUtc).TotalMilliseconds
        sourceRevision = $revision
        sourceDirty = $dirty
        engineRoot = $EngineRoot
        project = $projectPath
        packageConfiguration = if ($SkipPackageSmoke) { $null } else { $PackageConfiguration }
        currentPhase = $script:CurrentPhase
        phases = @($script:PhaseRecords)
        metrics = $script:Metrics
        outputs = [ordered]@{
            runDirectory = $runDirectory
            summary = $summaryPath
            managedTests = $managedTrxPath
            onboarding = $onboardingReportPath
            releaseGate = if ($SkipPackageSmoke) { $null } else { $releaseReportPath }
        }
        error = $script:Failure
    }
    $json = $document | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText(
        $reportPath,
        $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# Finare alpha acceptance')
    $lines.Add('')
    $lines.Add("- Status: **$($Status.ToUpperInvariant())**")
    $lines.Add(('- Project: `{0}`' -f $projectPath))
    $lines.Add(('- Engine: `{0}`' -f $EngineRoot))
    $lines.Add(('- Report: `{0}`' -f $reportPath))
    $lines.Add('')
    $lines.Add('| Phase | Status | Duration | Log |')
    $lines.Add('| --- | --- | ---: | --- |')
    foreach ($phase in $script:PhaseRecords) {
        $lines.Add(('| {0} | {1} | {2} ms | `{3}` |' -f
            $phase.name, $phase.status, $phase.durationMs, $phase.log))
    }
    if ($script:Metrics.Count -gt 0) {
        $lines.Add('')
        $lines.Add('## Metrics')
        $lines.Add('')
        foreach ($key in $script:Metrics.Keys) {
            $lines.Add("- ${key}: $($script:Metrics[$key])")
        }
    }
    if ($null -ne $script:Failure) {
        $lines.Add('')
        $lines.Add('## Failure and safe retry')
        $lines.Add('')
        $lines.Add(('- Code: `{0}`' -f $script:Failure.code))
        $lines.Add(('- Phase: `{0}`' -f $script:Failure.phase))
        $lines.Add("- Message: $($script:Failure.message)")
        $lines.Add(('- Relevant log: `{0}`' -f $script:Failure.log))
        $lines.Add(('- Retry: `{0}`' -f $script:Failure.retry))
    }
    [IO.File]::WriteAllText(
        $summaryPath,
        ($lines -join [Environment]::NewLine) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

try {
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Unreal project is missing: $projectPath"
    }
    $projectJson = Get-Content -LiteralPath $projectPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
        $EngineRoot = Resolve-EngineAssociation -Association ([string]$projectJson.EngineAssociation)
    }
    if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
        throw 'Unreal Engine could not be resolved. Pass -EngineRoot or register EngineAssociation.'
    }
    $EngineRoot = [IO.Path]::GetFullPath($EngineRoot)
    $dotnet = Join-Path $EngineRoot 'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
    $python = (Get-Command python -ErrorAction Stop).Source
    $powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
    $setup = Join-Path $repoRoot 'tools\setup-unreal-project.ps1'
    $release = Join-Path $repoRoot 'tools\package-unreal-release.ps1'
    $validator = Join-Path $repoRoot 'tools\validate_project.py'
    $solution = Join-Path $repoRoot 'Finare.slnx'
    foreach ($required in @(
            $dotnet,
            $python,
            $powershell,
            $setup,
            $release,
            $validator,
            $solution,
            $projectLoadingFixture)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "Required alpha acceptance input is missing: $required"
        }
    }
    if (-not $SkipPackageSmoke -and
        $projectPath -ne (Join-Path $repoRoot 'HostProject\FinareHost.uproject')) {
        throw 'Package smoke currently owns the HostProject fixture. Use HostProject or -SkipPackageSmoke.'
    }
    $script:PhaseRecords.Add([ordered]@{
        name = 'preflight'
        status = 'passed'
        durationMs = [long]([DateTime]::UtcNow - $startedAtUtc).TotalMilliseconds
        exitCode = 0
        log = $null
        artifacts = @()
        retry = "powershell -File `"$PSCommandPath`" -EngineRoot `"$EngineRoot`""
    })

    # CSharpProjectLoader tests intentionally evaluate the sample project's
    # default Debug configuration without building it. Materialize that exact
    # project-reference closure so a clean checkout behaves like a warm tree.
    Invoke-AcceptancePhase `
        -Name 'managed-debug-fixture' `
        -FilePath $dotnet `
        -Arguments @(
            'build', $projectLoadingFixture, '-c', 'Debug', '--nologo') `
        -Retry "& `"$dotnet`" build `"$projectLoadingFixture`" -c Debug"

    Invoke-AcceptancePhase `
        -Name 'managed-tests' `
        -FilePath $dotnet `
        -Arguments @(
            'test', $solution, '-c', 'Release',
            '--logger', 'trx;LogFileName=managed-tests.trx',
            '--results-directory', $runDirectory) `
        -Retry "& `"$dotnet`" test `"$solution`" -c Release" `
        -Artifacts @($managedTrxPath)

    [xml]$trx = Get-Content -LiteralPath $managedTrxPath -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $script:Metrics.managedTestsTotal = [int]$counters.total
    $script:Metrics.managedTestsPassed = [int]$counters.passed
    $script:Metrics.managedTestsFailed = [int]$counters.failed

    $validatorLog = Join-Path $runDirectory (
        '{0:D2}-repository-validator.log' -f ($script:PhaseCounter + 1))
    Invoke-AcceptancePhase `
        -Name 'repository-validator' `
        -FilePath $python `
        -Arguments @($validator) `
        -Retry "& `"$python`" `"$validator`"" `
        -Artifacts @($validatorLog)
    $validatorText = Get-Content -LiteralPath $validatorLog -Raw
    $validatorMatch = [regex]::Match(
        $validatorText,
        'Validated\s+(?<count>\d+)\s+repository invariants\.')
    if (-not $validatorMatch.Success) {
        throw "Repository validator passed but its invariant count was not reported. Log=$validatorLog"
    }
    $script:Metrics.repositoryInvariants = [int]$validatorMatch.Groups['count'].Value

    Invoke-AcceptancePhase `
        -Name 'unreal-onboarding' `
        -FilePath $powershell `
        -Arguments @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $setup,
            '-Project', $projectPath, '-EngineRoot', $EngineRoot,
            '-Report', $onboardingReportPath) `
        -Retry (
            "powershell -ExecutionPolicy Bypass -File `"$setup`" " +
            "-Project `"$projectPath`" -EngineRoot `"$EngineRoot`" " +
            "-Report `"$onboardingReportPath`"") `
        -Artifacts @($onboardingReportPath)

    $onboarding = Get-Content -LiteralPath $onboardingReportPath -Raw | ConvertFrom-Json
    if ($onboarding.status -ne 'passed') {
        throw "Onboarding child report is not passed: $onboardingReportPath"
    }
    $script:Metrics.automationTestsPassed = [int]$onboarding.outputs.automation.passed
    $script:Metrics.automationTestsFailed = [int]$onboarding.outputs.automation.failed
    $coverage = Get-Content -LiteralPath $onboarding.outputs.bindingCoverage -Raw |
        ConvertFrom-Json
    $script:Metrics.nativeContractMembers = [int]$coverage.summary.memberCount
    $script:Metrics.nativeThunkMembers = [int]$coverage.summary.nativeThunkCount

    if (-not $SkipPackageSmoke) {
        Invoke-AcceptancePhase `
            -Name 'package-smoke' `
            -FilePath $powershell `
            -Arguments @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $release,
                '-EngineRoot', $EngineRoot,
                '-Configurations', $PackageConfiguration,
                '-OutputDirectory', $releaseDirectory) `
            -Retry (
                "powershell -ExecutionPolicy Bypass -File `"$release`" " +
                "-EngineRoot `"$EngineRoot`" -Configurations $PackageConfiguration " +
                "-OutputDirectory `"$releaseDirectory`"") `
            -Artifacts @($releaseReportPath)
        $releaseGate = Get-Content -LiteralPath $releaseReportPath -Raw |
            ConvertFrom-Json
        $configurationReport = @($releaseGate.configurations)[0]
        $script:Metrics.packageConfiguration = [string]$configurationReport.configuration
        $script:Metrics.packageFiles = [int]$configurationReport.packageFileCount
        $script:Metrics.packageBytes = [long]$configurationReport.packageBytes
        $script:Metrics.packageSmokeExitCode = [int]$configurationReport.smokeExitCode
        $script:Metrics.packageSmoke = ([string]$configurationReport.smokeResult).Split("`n")[0]
    }

    Restore-AcceptanceConfigs
    Write-AcceptanceOutputs -Status 'passed'
    Write-Host 'Finare alpha acceptance: PASS'
    Write-Host "Report: $reportPath"
    Write-Host "Summary: $summaryPath"
} catch {
    Restore-AcceptanceConfigs
    if ($null -eq $script:Failure) {
        $safeRetry = (
            "powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" " +
            "-Project `"$projectPath`"")
        $script:Failure = [ordered]@{
            code = 'USACC1001'
            phase = $script:CurrentPhase
            message = $_.Exception.Message
            log = $null
            artifacts = @()
            retry = $safeRetry
        }
        $script:PhaseRecords.Add([ordered]@{
            name = $script:CurrentPhase
            status = 'failed'
            durationMs = [long]([DateTime]::UtcNow - $startedAtUtc).TotalMilliseconds
            exitCode = -1
            log = $null
            artifacts = @()
            retry = $safeRetry
        })
    }
    Write-AcceptanceOutputs -Status 'failed'
    Write-Error (
        "Finare alpha acceptance failed [$($script:Failure.code)] " +
        "during '$($script:Failure.phase)': $($script:Failure.message) " +
        "Log=$($script:Failure.log) Retry=$($script:Failure.retry) Report=$reportPath")
    exit 1
}
