[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [string]$ComparisonRoot = 'D:\Project\UEUnifiedScriptComparison',
    [string]$ReportPath,
    [switch]$RunUnreal,
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$targetRoot = Join-Path $ComparisonRoot 'BlockBreaker-UnifiedScript'
$manifestPath = Join-Path $PSScriptRoot 'blockbreaker-roundtrip.json'
$dotnet = Join-Path $EngineRoot `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
$cliProject = Join-Path $repoRoot 'src\UnifiedScript.Cli\UnifiedScript.Cli.csproj'
$cliDll = Join-Path $repoRoot 'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll'
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $repoRoot "artifacts\roundtrip\blockbreaker-$runId"
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $runRoot 'roundtrip-report.json'
}
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
if ($Package -and -not $RunUnreal) {
    throw '-Package requires -RunUnreal.'
}

foreach ($required in @($dotnet, $cliProject, $manifestPath, $targetRoot)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required BlockBreaker round-trip input is missing: $required"
    }
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 'unifiedscript.blockbreaker-round-trip/v0.1' -or
    @($manifest.scripts).Count -ne 2) {
    throw "Unsupported or incomplete BlockBreaker round-trip manifest: $manifestPath"
}
$projectPath = Join-Path $targetRoot $manifest.projectPath
$patchPath = Join-Path $repoRoot $manifest.visualPatchPath
foreach ($required in @($projectPath, $patchPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required BlockBreaker round-trip artifact is missing: $required"
    }
}

$trackedPaths = @(
    foreach ($script in $manifest.scripts) {
        $script.sourcePath
        $script.identityPath
    }
)
& git -C $targetRoot diff --quiet -- $trackedPaths
if ($LASTEXITCODE -ne 0) {
    throw 'BlockBreaker round-trip source or identity files have unstaged changes.'
}
& git -C $targetRoot diff --cached --quiet -- $trackedPaths
if ($LASTEXITCODE -ne 0) {
    throw 'BlockBreaker round-trip source or identity files have staged changes.'
}
$baselineCommit = (& git -C $targetRoot rev-parse "$($manifest.baselineCommit)^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or $baselineCommit -ne $manifest.baselineCommit) {
    throw "BlockBreaker Visual baseline commit is unavailable: $($manifest.baselineCommit)"
}
$targetCommit = (& git -C $targetRoot rev-parse HEAD).Trim()

New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$baselineArchive = Join-Path $runRoot 'baseline.zip'
$baselineRoot = Join-Path $runRoot 'baseline'
$backupRoot = Join-Path $runRoot 'current-backup'
$baselineArtifacts = Join-Path $runRoot 'baseline-artifacts'
$visualRoot = Join-Path $runRoot 'visual'
$codeRoot = Join-Path $runRoot 'code'
$appliedPatchPath = Join-Path $runRoot 'materialized-roundtrip.patch.json'
New-Item -ItemType Directory -Force -Path $backupRoot, $codeRoot | Out-Null

& git -C $targetRoot archive --format=zip "--output=$baselineArchive" `
    $baselineCommit -- $trackedPaths
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to export the pinned BlockBreaker Visual baseline.'
}
Expand-Archive -LiteralPath $baselineArchive -DestinationPath $baselineRoot

foreach ($relative in $trackedPaths) {
    $current = Join-Path $targetRoot $relative
    $backup = Join-Path $backupRoot $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup) | Out-Null
    Copy-Item -LiteralPath $current -Destination $backup -Force
}

& $dotnet build $cliProject -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'UnifiedScript CLI Release build failed before BlockBreaker round-trip.'
}
& $dotnet build $projectPath -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'Current BlockBreaker managed project failed before round-trip.'
}

$baselineInstalled = $false
try {
    foreach ($relative in $trackedPaths) {
        Copy-Item -LiteralPath (Join-Path $baselineRoot $relative) `
            -Destination (Join-Path $targetRoot $relative) -Force
    }
    $baselineInstalled = $true

    $analyzeArguments = @(
        'analyze', $projectPath,
        '--configuration', 'Release',
        '--no-restore', '--no-build',
        '--out', $baselineArtifacts
    )
    foreach ($script in $manifest.scripts) {
        $analyzeArguments += @('--identity', (Join-Path $targetRoot $script.identityPath))
    }
    & $dotnet $cliDll @analyzeArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Pinned BlockBreaker Visual baseline analysis failed.'
    }

    # Compilation and semantic configuration IDs intentionally include current
    # reference fingerprints. Rebind only those baseline guards after the pinned
    # sources have been analyzed; operation node IDs and old values remain fixed.
    $baselineGraphPath = Join-Path $baselineArtifacts `
        'BlockBreaker.UnifiedScript.graph.json'
    $baselineGraph = Get-Content -LiteralPath $baselineGraphPath -Raw | ConvertFrom-Json
    $materializedPatch = Get-Content -LiteralPath $patchPath -Raw | ConvertFrom-Json
    $materializedPatch.sourceCompilationId = $baselineGraph.sourceCompilationId
    $materializedPatch.semanticConfigurationId = $baselineGraph.semanticConfigurationId
    $materializedPatch | ConvertTo-Json -Depth 20 | Set-Content `
        -LiteralPath $appliedPatchPath -Encoding utf8

    $applyArguments = @(
        'apply-project-patch', $projectPath,
        '--patch', $appliedPatchPath,
        '--out', $visualRoot,
        '--configuration', 'Release',
        '--no-restore', '--no-build'
    )
    foreach ($script in $manifest.scripts) {
        $applyArguments += @('--identity', (Join-Path $targetRoot $script.identityPath))
    }
    & $dotnet $cliDll @applyArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'BlockBreaker Visual project patch failed.'
    }
}
finally {
    if ($baselineInstalled) {
        foreach ($relative in $trackedPaths) {
            Copy-Item -LiteralPath (Join-Path $backupRoot $relative) `
                -Destination (Join-Path $targetRoot $relative) -Force
        }
    }
}

function Invoke-ExactCodeEdit([string]$Path, [object[]]$Edits) {
    $text = [IO.File]::ReadAllText($Path)
    foreach ($edit in $Edits) {
        $count = ([regex]::Matches($text, [regex]::Escape([string]$edit.oldText))).Count
        if ($count -ne 1) {
            throw "Expected exactly one C# edit match in ${Path}: $($edit.oldText)"
        }
        $text = $text.Replace([string]$edit.oldText, [string]$edit.newText)
    }
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$managedRoot = Split-Path -Parent $projectPath
$managedAssembly = Join-Path $managedRoot 'BlockBreaker.UnifiedScript.dll'
$runtimeRoot = Join-Path $targetRoot 'Plugins\UEUnifiedScript\Binaries\Managed'
$references = @(
    $managedAssembly,
    (Join-Path $runtimeRoot 'UnifiedScript.Core.dll'),
    (Join-Path $runtimeRoot 'UnifiedScript.Unreal.dll')
)
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference)) {
        throw "BlockBreaker round-trip reference is missing: $reference"
    }
}

$scriptReports = @()
$codeIdentityPaths = @{}
foreach ($script in $manifest.scripts) {
    $sourceName = Split-Path -Leaf $script.sourcePath
    $identityName = Split-Path -Leaf $script.identityPath
    $visualSource = Join-Path $visualRoot $sourceName
    $visualIdentity = Join-Path $visualRoot $identityName
    $codeSource = Join-Path $codeRoot $sourceName
    Copy-Item -LiteralPath $visualSource -Destination $codeSource -Force
    Invoke-ExactCodeEdit -Path $codeSource -Edits @($script.codeEdits)

    $visualAnalysis = Join-Path $runRoot "$($script.id)-visual-analysis"
    $codeAnalysis = Join-Path $runRoot "$($script.id)-code-analysis"
    foreach ($analysis in @(
        [ordered]@{ Source = $visualSource; Identity = $visualIdentity; Output = $visualAnalysis },
        [ordered]@{ Source = $codeSource; Identity = $visualIdentity; Output = $codeAnalysis }
    )) {
        $arguments = @(
            'analyze', (Split-Path -Leaf $analysis.Source),
            '--identity', $analysis.Identity,
            '--out', $analysis.Output,
            '--assembly', $script.assemblyName
        )
        foreach ($reference in $references) {
            $arguments += @('--reference', $reference)
        }
        Push-Location (Split-Path -Parent $analysis.Source)
        try {
            & $dotnet $cliDll @arguments
            if ($LASTEXITCODE -ne 0) {
                throw "BlockBreaker $($script.id) source could not reopen as a graph."
            }
        }
        finally {
            Pop-Location
        }
    }

    $codeIdentity = Join-Path $codeAnalysis $identityName
    $codeIdentityPaths[[string]$script.id] = $codeIdentity
    $equivalencePath = Join-Path $runRoot "$($script.id)-equivalence.json"
    $verifyArguments = @(
        'verify-roundtrip', $visualSource, $codeSource,
        '--requirements', (Join-Path $repoRoot $script.requirementsPath),
        '--report', $equivalencePath,
        '--visual-identity', $visualIdentity,
        '--code-identity', $codeIdentity,
        '--assembly', $script.assemblyName
    )
    foreach ($reference in $references) {
        $verifyArguments += @('--reference', $reference)
    }
    & $dotnet $cliDll @verifyArguments
    if ($LASTEXITCODE -ne 0) {
        throw "BlockBreaker $($script.id) semantic/IL/execution equivalence failed."
    }

    $equivalence = Get-Content -LiteralPath $equivalencePath -Raw | ConvertFrom-Json
    $visualGraphPath = Join-Path $visualAnalysis "$([IO.Path]::GetFileNameWithoutExtension($sourceName)).graph.json"
    $codeGraphPath = Join-Path $codeAnalysis "$([IO.Path]::GetFileNameWithoutExtension($sourceName)).graph.json"
    $visualGraph = Get-Content -LiteralPath $visualGraphPath -Raw | ConvertFrom-Json
    $codeGraph = Get-Content -LiteralPath $codeGraphPath -Raw | ConvertFrom-Json
    $visualFunction = @($visualGraph.functions | Where-Object qualifiedName -eq $script.method)
    $codeFunction = @($codeGraph.functions | Where-Object qualifiedName -eq $script.method)
    if ($visualFunction.Count -ne 1 -or $codeFunction.Count -ne 1 -or
        $visualFunction[0].id -ne $codeFunction[0].id -or
        -not ([IO.File]::ReadAllText($visualSource).Contains([string]$script.visualEvidence))) {
        throw "BlockBreaker $($script.id) did not reopen with the same method identity."
    }
    $scriptReports += [ordered]@{
        id = [string]$script.id
        method = [string]$script.method
        methodId = [string]$visualFunction[0].id
        visualSource = $visualSource
        codeSource = $codeSource
        visualSourceSha256 = (Get-FileHash -LiteralPath $visualSource -Algorithm SHA256).Hash.ToLowerInvariant()
        codeSourceSha256 = (Get-FileHash -LiteralPath $codeSource -Algorithm SHA256).Hash.ToLowerInvariant()
        visualGraphNodeCount = @($visualFunction[0].nodes).Count
        codeGraphNodeCount = @($codeFunction[0].nodes).Count
        semanticEquivalent = [bool]$equivalence.methods[0].semanticEquivalent
        ilEquivalent = [bool]$equivalence.methods[0].ilEquivalent
        executionCaseCount = @($equivalence.methods[0].executions).Count
        executionEquivalent = @($equivalence.methods[0].executions | Where-Object { -not $_.equivalent }).Count -eq 0
        assetEquivalent = [bool]$equivalence.assetEquivalent
        equivalenceReport = $equivalencePath
    }
}

$unrealReports = @()
$gameplayEquivalent = -not $RunUnreal
$scriptAssetsEquivalent = -not $RunUnreal
$packagedBuildEquivalent = -not $Package
if ($RunUnreal) {
    $prepareScript = Join-Path $targetRoot 'Tools\run_unifiedscript_demo.ps1'
    $auditScript = Join-Path $PSScriptRoot 'audit-blockbreaker-migration.ps1'
    $packageScript = Join-Path $PSScriptRoot 'package-blockbreaker-demo.ps1'
    $editorCmd = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
    $uproject = Join-Path $targetRoot 'BlockBreaker.uproject'
    $generatedBindings = Join-Path $targetRoot `
        'Managed\Generated\BlockBreaker.bindings.generated.cs'
    $map = '/Game/BlockBreaker/Maps/TutorialMap?game=/Script/BlockBreaker.UnifiedScriptBlockBreakerGameMode'
    foreach ($required in @(
        $prepareScript, $auditScript, $editorCmd, $uproject, $generatedBindings)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "Required Unreal round-trip gate input is missing: $required"
        }
    }
    if ($Package -and -not (Test-Path -LiteralPath $packageScript)) {
        throw "BlockBreaker package runner is missing: $packageScript"
    }

    $targetArchive = Join-Path $runRoot 'target-tracked.zip'
    $targetBackup = Join-Path $runRoot 'target-tracked'
    $initialUntracked = @(
        & git -C $targetRoot status --porcelain=v1 --untracked-files=all |
            Where-Object { $_.StartsWith('?? ', [StringComparison]::Ordinal) } |
            ForEach-Object { $_.Substring(3).Replace('\', '/') }
    )
    $declaredUntrackedOutputs = @(
        'Managed/BlockBreaker.UnifiedScript.asset.json',
        'Managed/BlockBreaker.UnifiedScript.sourcemap.json',
        'Managed/BlockBreaker.UnifiedScript.uschunk',
        'Plugins/UEUnifiedScript/Source/UEUnifiedScriptRuntime/Private/Tests/UnifiedScriptRuntimeBreadcrumbTests.cpp',
        'Plugins/UEUnifiedScript/Source/UEUnifiedScriptRuntime/Private/Tests/UnifiedScriptRuntimeOwnershipTests.cpp'
    )
    & git -C $targetRoot archive --format=zip "--output=$targetArchive" HEAD
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to back up the tracked BlockBreaker target before Unreal gates.'
    }
    Expand-Archive -LiteralPath $targetArchive -DestinationPath $targetBackup

    function New-CandidateProject(
        [string]$CandidateId,
        [string]$PlayerSource,
        [string]$RifleSource) {
        $candidateRoot = Join-Path $runRoot "unreal-$CandidateId"
        $candidateOutput = Join-Path $candidateRoot 'out'
        New-Item -ItemType Directory -Force -Path $candidateRoot, $candidateOutput | Out-Null
        $candidateProject = Join-Path $candidateRoot 'BlockBreaker.RoundTrip.csproj'
        $escapedPlayer = [Security.SecurityElement]::Escape($PlayerSource)
        $escapedRifle = [Security.SecurityElement]::Escape($RifleSource)
        $escapedBindings = [Security.SecurityElement]::Escape($generatedBindings)
        $escapedCore = [Security.SecurityElement]::Escape(
            (Join-Path $runtimeRoot 'UnifiedScript.Core.dll'))
        $escapedUnreal = [Security.SecurityElement]::Escape(
            (Join-Path $runtimeRoot 'UnifiedScript.Unreal.dll'))
        $projectText = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>BlockBreaker.UnifiedScript</AssemblyName>
    <RootNamespace>BlockBreaker.UnifiedScript</RootNamespace>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <GenerateDependencyFile>true</GenerateDependencyFile>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$escapedPlayer" Link="BlockBreakerPlayerScript.cs" />
    <Compile Include="$escapedRifle" Link="BlockBreakerGunScripts.cs" />
    <Compile Include="$escapedBindings" Link="Generated/BlockBreaker.bindings.generated.cs" />
    <Reference Include="UnifiedScript.Core">
      <HintPath>$escapedCore</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnifiedScript.Unreal">
      <HintPath>$escapedUnreal</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
"@
        [IO.File]::WriteAllText(
            $candidateProject,
            $projectText,
            [Text.UTF8Encoding]::new($false))
        $buildLog = Join-Path $candidateRoot 'build.log'
        $buildOutput = @(& $dotnet build $candidateProject -c Release --nologo `
            --output $candidateOutput 2>&1)
        $buildExitCode = $LASTEXITCODE
        $buildOutput | Set-Content -LiteralPath $buildLog -Encoding utf8
        if ($buildExitCode -ne 0) {
            $buildOutput | Select-Object -Last 200 | Write-Host
            throw "BlockBreaker $CandidateId candidate assembly failed to build."
        }
        return [ordered]@{
            Id = $CandidateId
            Root = $candidateRoot
            Output = $candidateOutput
            Project = $candidateProject
        }
    }

    function Invoke-CandidateGameplay([object]$Candidate) {
        foreach ($file in @(
            'BlockBreaker.UnifiedScript.dll',
            'BlockBreaker.UnifiedScript.pdb',
            'BlockBreaker.UnifiedScript.deps.json')) {
            $candidateFile = Join-Path $Candidate.Output $file
            if (-not (Test-Path -LiteralPath $candidateFile)) {
                throw "BlockBreaker $($Candidate.Id) candidate output is missing: $candidateFile"
            }
            Copy-Item -LiteralPath $candidateFile `
                -Destination (Join-Path $managedRoot $file) -Force
        }

        $runtimeLog = Join-Path $Candidate.Root 'gameplay.log'
        $stdoutLog = Join-Path $Candidate.Root 'gameplay.stdout.log'
        $runtimeArguments = @(
            $uproject, $map, '-game', '-nullrhi', '-unattended', '-nosound', '-nosplash',
            '-stdout', '-FullStdOutLogOutput', '-benchmark', '-fps=60',
            '-BlockBreakerBenchmarkFrames=300', "-abslog=$runtimeLog"
        )
        $runtimeOutput = @(& $editorCmd @runtimeArguments 2>&1)
        $runtimeExitCode = $LASTEXITCODE
        $runtimeOutput | Set-Content -LiteralPath $stdoutLog -Encoding utf8
        $runtimeText = @(
            $runtimeOutput -join [Environment]::NewLine
            if (Test-Path -LiteralPath $runtimeLog) {
                Get-Content -LiteralPath $runtimeLog -Raw
            }
        ) -join [Environment]::NewLine
        $summary = [regex]::Match(
            $runtimeText,
            'ManagedTicks=(\d+), Shots=(\d+), Hits=(\d+), Status=([^,\r\n]+), FastCalls=(\d+), CallFrameCalls=(\d+), LegacyCalls=(\d+)')
        if ($runtimeExitCode -ne 0 -or -not $summary.Success -or
            [int]$summary.Groups[1].Value -ne 300 -or
            [int]$summary.Groups[2].Value -ne 1 -or
            [int]$summary.Groups[3].Value -ne 1 -or
            $summary.Groups[4].Value.Trim() -ne 'Stopped' -or
            [uint64]$summary.Groups[5].Value -eq 0 -or
            [uint64]$summary.Groups[6].Value -eq 0 -or
            [uint64]$summary.Groups[7].Value -ne 0 -or
            -not $runtimeText.Contains(
                'UnifiedScript BlockBreaker gameplay data completed: Commands=2')) {
            $runtimeOutput | Select-Object -Last 200 | Write-Output
            throw "BlockBreaker $($Candidate.Id) Unreal gameplay equivalence gate failed."
        }
        if (-not $runtimeText.Contains(
                'PlayerAsset=/Game/UnifiedScript/BlockBreaker/SA_BlockBreakerPlayer.SA_BlockBreakerPlayer') -or
            -not $runtimeText.Contains(
                'GunAsset=/Game/UnifiedScript/BlockBreaker/SA_BlockBreakerRifle.SA_BlockBreakerRifle')) {
            throw "BlockBreaker $($Candidate.Id) did not execute through both Script Assets."
        }
        return [ordered]@{
            frames = [int]$summary.Groups[1].Value
            shots = [int]$summary.Groups[2].Value
            hits = [int]$summary.Groups[3].Value
            status = $summary.Groups[4].Value.Trim()
            fastCalls = [uint64]$summary.Groups[5].Value
            callFrameCalls = [uint64]$summary.Groups[6].Value
            legacyCalls = [uint64]$summary.Groups[7].Value
            gameplayCommands = 2
            exitCode = $runtimeExitCode
            log = $runtimeLog
            stdout = $stdoutLog
        }
    }

    function Invoke-CandidatePackage([object]$Candidate) {
        $packageLog = Join-Path $Candidate.Root 'package.log'
        $packageOutput = @(& powershell -ExecutionPolicy Bypass -File $packageScript `
            -EngineRoot $EngineRoot -ComparisonRoot $ComparisonRoot `
            -Configuration Development -SmokeFrames 300 -SkipPrepare 2>&1)
        $packageExitCode = $LASTEXITCODE
        $packageOutput | Set-Content -LiteralPath $packageLog -Encoding utf8
        if ($packageExitCode -ne 0) {
            $packageOutput | Select-Object -Last 200 | Write-Output
            throw "BlockBreaker $($Candidate.Id) packaged build gate failed."
        }
        $reportLine = $packageOutput | Where-Object {
            [string]$_ -like 'Report: *'
        } | Select-Object -Last 1
        if ($null -eq $reportLine) {
            throw "BlockBreaker $($Candidate.Id) package did not publish a delivery report."
        }
        $deliveryReportPath = ([string]$reportLine).Substring('Report: '.Length).Trim()
        $delivery = Get-Content -LiteralPath $deliveryReportPath -Raw | ConvertFrom-Json
        if ($delivery.schemaVersion -ne 'unifiedscript.blockbreaker-demo-delivery/v0.2' -or
            $delivery.smoke.frames -ne 300 -or $delivery.smoke.shots -ne 1 -or
            $delivery.smoke.hits -ne 1 -or $delivery.smoke.legacyCalls -ne 0 -or
            $delivery.smoke.gameplayCommands -ne 2 -or $delivery.smoke.exitCode -ne 0 -or
            $delivery.puertsOrTypeScriptPackageFiles -ne 0) {
            throw "BlockBreaker $($Candidate.Id) delivery report failed equivalence checks."
        }
        return [ordered]@{
            report = $deliveryReportPath
            packageFileCount = [int]$delivery.packageFileCount
            packageBytes = [long]$delivery.packageBytes
            frames = [int]$delivery.smoke.frames
            shots = [int]$delivery.smoke.shots
            hits = [int]$delivery.smoke.hits
            fastCalls = [uint64]$delivery.smoke.fastCalls
            callFrameCalls = [uint64]$delivery.smoke.callFrameCalls
            legacyCalls = [uint64]$delivery.smoke.legacyCalls
            gameplayCommands = [int]$delivery.smoke.gameplayCommands
            exitCode = [int]$delivery.smoke.exitCode
        }
    }

    try {
        $prepareLog = Join-Path $runRoot 'unreal-prepare.log'
        $prepareOutput = @(& $prepareScript -EngineRoot $EngineRoot `
            -UnifiedScriptRoot $repoRoot -Headless -GameplayBenchmarkFrames 300 2>&1)
        $prepareExitCode = $LASTEXITCODE
        $prepareOutput | Set-Content -LiteralPath $prepareLog -Encoding utf8
        if ($prepareExitCode -ne 0 -or
            -not (($prepareOutput -join [Environment]::NewLine).Contains(
                'UnifiedScript gameplay data + ABI v3 gate: PASS'))) {
            $prepareOutput | Select-Object -Last 200 | Write-Output
            throw 'BlockBreaker Unreal preparation failed before round-trip candidates.'
        }
        $migrationAuditPath = Join-Path $runRoot 'migration-audit.json'
        & $auditScript -EngineRoot $EngineRoot -ComparisonRoot $ComparisonRoot `
            -ReportPath $migrationAuditPath
        if ($LASTEXITCODE -ne 0) {
            throw 'BlockBreaker migration and shared-asset audit failed before round-trip candidates.'
        }
        $migrationAudit = Get-Content -LiteralPath $migrationAuditPath -Raw | ConvertFrom-Json
        if (-not $migrationAudit.passed -or
            -not $migrationAudit.sharedAssetsByteIdentical -or
            $migrationAudit.scriptAssetCount -ne 2) {
            throw 'BlockBreaker migration audit did not prove the expected Script/shared assets.'
        }

        $player = $manifest.scripts | Where-Object id -eq 'player'
        $rifle = $manifest.scripts | Where-Object id -eq 'rifle'
        $candidates = @(
            (New-CandidateProject -CandidateId 'visual' `
                -PlayerSource (Join-Path $visualRoot (Split-Path -Leaf $player.sourcePath)) `
                -RifleSource (Join-Path $visualRoot (Split-Path -Leaf $rifle.sourcePath))),
            (New-CandidateProject -CandidateId 'code' `
                -PlayerSource (Join-Path $codeRoot (Split-Path -Leaf $player.sourcePath)) `
                -RifleSource (Join-Path $codeRoot (Split-Path -Leaf $rifle.sourcePath)))
        )
        $scriptAssetPaths = @(
            (Join-Path $targetRoot 'Content\UnifiedScript\BlockBreaker\SA_BlockBreakerPlayer.uasset'),
            (Join-Path $targetRoot 'Content\UnifiedScript\BlockBreaker\SA_BlockBreakerRifle.uasset')
        )
        foreach ($assetPath in $scriptAssetPaths) {
            if (-not (Test-Path -LiteralPath $assetPath)) {
                throw "Synchronized BlockBreaker Script Asset is missing: $assetPath"
            }
        }
        foreach ($candidate in $candidates) {
            $gameplay = Invoke-CandidateGameplay -Candidate $candidate
            $assetHashes = [ordered]@{}
            foreach ($assetPath in $scriptAssetPaths) {
                $assetHashes[(Split-Path -Leaf $assetPath)] =
                    (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            $packageResult = if ($Package) {
                Invoke-CandidatePackage -Candidate $candidate
            } else {
                $null
            }
            $unrealReports += [ordered]@{
                id = [string]$candidate.Id
                gameplay = $gameplay
                scriptAssetHashes = $assetHashes
                package = $packageResult
            }
        }

        $visualUnreal = $unrealReports | Where-Object id -eq 'visual'
        $codeUnreal = $unrealReports | Where-Object id -eq 'code'
        $gameplayEquivalent = @(
            'frames', 'shots', 'hits', 'status', 'fastCalls', 'callFrameCalls',
            'legacyCalls', 'gameplayCommands', 'exitCode'
        ) | ForEach-Object {
            $visualUnreal.gameplay.$_ -eq $codeUnreal.gameplay.$_
        } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count
        $gameplayEquivalent = $gameplayEquivalent -eq 0
        $scriptAssetsEquivalent =
            ($visualUnreal.scriptAssetHashes | ConvertTo-Json -Compress) -eq
            ($codeUnreal.scriptAssetHashes | ConvertTo-Json -Compress)
        if ($Package) {
            $packagedBuildEquivalent = @(
                'frames', 'shots', 'hits', 'fastCalls', 'callFrameCalls',
                'legacyCalls', 'gameplayCommands', 'exitCode'
            ) | ForEach-Object {
                $visualUnreal.package.$_ -eq $codeUnreal.package.$_
            } | Where-Object { -not $_ } | Measure-Object | Select-Object -ExpandProperty Count
            $packagedBuildEquivalent = $packagedBuildEquivalent -eq 0
        }
        if (-not $gameplayEquivalent -or -not $scriptAssetsEquivalent -or
            ($Package -and -not $packagedBuildEquivalent)) {
            throw 'BlockBreaker Visual/C# Unreal or package results are not equivalent.'
        }
    }
    finally {
        $changedTracked = @(& git -C $targetRoot diff --name-only --)
        foreach ($relative in $changedTracked) {
            $backup = Join-Path $targetBackup $relative
            if (-not (Test-Path -LiteralPath $backup)) {
                throw "Cannot restore tracked BlockBreaker path from backup: $relative"
            }
            $destination = Join-Path $targetRoot $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $backup -Destination $destination -Force
        }
        & git -C $targetRoot diff --quiet --
        if ($LASTEXITCODE -ne 0) {
            throw 'Tracked BlockBreaker files were not fully restored after Unreal gates.'
        }
        # Copying a plugin through a CRLF worktree can leave stat-only changes
        # even when the clean-filtered blobs exactly match HEAD. Refresh those
        # entries only after proving there is no substantive tracked diff.
        & git -C $targetRoot add -u
        & git -C $targetRoot diff --cached --quiet --
        if ($LASTEXITCODE -ne 0) {
            throw 'BlockBreaker target restoration unexpectedly staged content.'
        }
        $finalUntracked = @(
            & git -C $targetRoot status --porcelain=v1 --untracked-files=all |
                Where-Object { $_.StartsWith('?? ', [StringComparison]::Ordinal) } |
                ForEach-Object { $_.Substring(3).Replace('\', '/') }
        )
        $newUntracked = @($finalUntracked | Where-Object { $_ -notin $initialUntracked })
        foreach ($relative in $newUntracked) {
            if ($relative -notin $declaredUntrackedOutputs) {
                throw "Unreal gate created an unexpected untracked path: $relative"
            }
            $createdPath = Join-Path $targetRoot $relative
            if (-not (Test-Path -LiteralPath $createdPath -PathType Leaf)) {
                throw "Unreal gate created a non-file path that cannot be safely removed: $relative"
            }
            Remove-Item -LiteralPath $createdPath -Force
        }
    }
}

$reportDirectory = Split-Path -Parent $ReportPath
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
$report = [ordered]@{
    schemaVersion = 'unifiedscript.blockbreaker-round-trip-report/v0.1'
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    baselineCommit = $baselineCommit
    targetCommit = $targetCommit
    patchTemplateSha256 = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash.ToLowerInvariant()
    appliedPatchSha256 = (Get-FileHash -LiteralPath $appliedPatchPath -Algorithm SHA256).Hash.ToLowerInvariant()
    scripts = $scriptReports
    unreal = $unrealReports
    visualProjectPatchApplied = $true
    codeGraphsReopened = $true
    semanticEquivalent = @($scriptReports | Where-Object { -not $_.semanticEquivalent }).Count -eq 0
    ilEquivalent = @($scriptReports | Where-Object { -not $_.ilEquivalent }).Count -eq 0
    executionEquivalent = @($scriptReports | Where-Object { -not $_.executionEquivalent }).Count -eq 0
    assetDeclarationsEquivalent = @($scriptReports | Where-Object { -not $_.assetEquivalent }).Count -eq 0
    unrealRan = [bool]$RunUnreal
    packageRan = [bool]$Package
    gameplayEquivalent = [bool]$gameplayEquivalent
    scriptAssetsEquivalent = [bool]$scriptAssetsEquivalent
    packagedBuildEquivalent = [bool]$packagedBuildEquivalent
    passed = @($scriptReports | Where-Object {
        -not $_.semanticEquivalent -or -not $_.ilEquivalent -or
        -not $_.executionEquivalent -or -not $_.assetEquivalent
    }).Count -eq 0 -and $gameplayEquivalent -and
        $scriptAssetsEquivalent -and $packagedBuildEquivalent
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding utf8
if (-not $report.passed) {
    throw "BlockBreaker managed round-trip proof failed: $ReportPath"
}
Write-Output "BlockBreaker Visual -> C# -> Visual round-trip: PASS"
Write-Output "Report: $ReportPath"
