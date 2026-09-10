[CmdletBinding()]
param(
    [string]$EngineRoot = $env:FINARE_UE_ROOT,
    [ValidateRange(32, 1024)]
    [int]$TypeCount = 256,
    [ValidateRange(4, 64)]
    [int]$MembersPerType = 16,
    [ValidateRange(16, 1024)]
    [int]$ScriptCount = 256,
    [ValidateRange(1, 9)]
    [int]$ContractTrials = 3,
    [ValidateRange(1000, 120000)]
    [int]$ContractGenerationBudgetMs = 15000,
    [ValidateRange(1000, 180000)]
    [int]$ScriptEmitBudgetMs = 60000,
    [ValidateRange(1000, 120000)]
    [int]$EditorStartupBudgetMs = 30000,
    [ValidateRange(1000, 300000)]
    [int]$AssetSyncBudgetMs = 120000,
    [switch]$SkipCliBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'HostProject\FinareHost.uproject'
$cliProject = Join-Path $repoRoot 'src\Finare.Cli\Finare.Cli.csproj'
$unrealProjectReference = Join-Path `
    $repoRoot `
    'src\Finare.Unreal\Finare.Unreal.csproj'
$artifactRoot = Join-Path $repoRoot 'artifacts\ue\scale'
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$runDirectory = Join-Path $artifactRoot $stamp
$contractDirectory = Join-Path $runDirectory 'contract'
$scriptProjectDirectory = Join-Path $runDirectory 'scripts'
$scriptOutputDirectory = Join-Path $runDirectory 'script-output'
$scalePackageSegment = "FinareScale_$stamp"
$scaleContentDirectory = Join-Path `
    $repoRoot `
    "HostProject\Content\$scalePackageSegment"
$reportPath = Join-Path $runDirectory 'scale-gate.json'

if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = @('D:\UE_5.8', 'D:\UE\_5.8') |
        Where-Object {
            Test-Path (Join-Path `
                $_ `
                'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe')
        } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'Pass -EngineRoot or set FINARE_UE_ROOT.'
}

$dotnetExe = Join-Path `
    $EngineRoot `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
$unrealEditorCmd = Join-Path `
    $EngineRoot `
    'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
foreach ($requiredPath in @(
        $dotnetExe,
        $unrealEditorCmd,
        $projectPath,
        $cliProject,
        $unrealProjectReference)) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Required scale-gate input is missing: $requiredPath"
    }
}
if (Test-Path -LiteralPath $scaleContentDirectory) {
    throw "Unique scale content directory already exists: $scaleContentDirectory"
}

New-Item -ItemType Directory -Force -Path @(
    $runDirectory,
    $contractDirectory,
    $scriptProjectDirectory,
    $scriptOutputDirectory) | Out-Null
$utf8NoBom = [Text.UTF8Encoding]::new($false)

function Write-Utf8Text {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Value
    )
    $parent = Split-Path -Parent $Path
    if (![string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Value, $utf8NoBom)
}

function Write-Json {
    param(
        [Parameter(Mandatory)]$Value,
        [Parameter(Mandatory)][string]$Path
    )
    Write-Utf8Text `
        -Path $Path `
        -Value (($Value | ConvertTo-Json -Depth 32) + [Environment]::NewLine)
}

function Invoke-Measured {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$LogPath
    )
    $parent = Split-Path -Parent $LogPath
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & $FilePath @Arguments *> $LogPath
    $exitCode = $LASTEXITCODE
    $timer.Stop()
    if ($exitCode -ne 0) {
        $tail = Get-Content -LiteralPath $LogPath -Tail 80 -ErrorAction SilentlyContinue
        throw "Command failed with exit code $exitCode. Log: $LogPath`n$($tail -join [Environment]::NewLine)"
    }
    return [Math]::Round($timer.Elapsed.TotalMilliseconds, 4)
}

function Get-FileDigest {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-Median {
    param([Parameter(Mandatory)][double[]]$Values)
    $sorted = @($Values | Sort-Object)
    $middle = [Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function New-ContractOutputPaths {
    param([Parameter(Mandatory)][string]$Directory)
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    return [ordered]@{
        managed = Join-Path $Directory 'Scale.bindings.cs'
        native = Join-Path $Directory 'Scale.thunks.cpp'
        nodes = Join-Path $Directory 'Scale.nodes.json'
        coverage = Join-Path $Directory 'Scale.coverage.json'
    }
}

function Invoke-ContractGeneration {
    param(
        [Parameter(Mandatory)][string]$ReflectionPath,
        [Parameter(Mandatory)][string]$ContractPath,
        [Parameter(Mandatory)]$Outputs,
        [Parameter(Mandatory)][string]$LogPath
    )
    return Invoke-Measured `
        -FilePath $dotnetExe `
        -Arguments @(
            'run',
            '--project', $cliProject,
            '-c', 'Release',
            '--no-build',
            '--',
            'ue-bindings', $ReflectionPath,
            '--out', $Outputs.managed,
            '--contract', $ContractPath,
            '--native-out', $Outputs.native,
            '--nodes-out', $Outputs.nodes,
            '--report', $Outputs.coverage,
            '--fail-on-rejected') `
        -LogPath $LogPath
}

function Invoke-UnrealAssetSync {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$Name,
        [switch]$ValidateOnly
    )
    $engineLog = Join-Path $runDirectory "$Name.engine.log"
    $commandLog = Join-Path $runDirectory "$Name.command.log"
    $arguments = @(
        $projectPath,
        '-unattended',
        '-nop4',
        '-nullrhi',
        '-nosplash',
        '-NoSound',
        '-run=FinareSyncAssets',
        "-Input=$ManifestPath",
        "-abslog=$engineLog")
    if ($ValidateOnly) {
        $arguments += '-ValidateOnly'
    }
    $milliseconds = Invoke-Measured `
        -FilePath $unrealEditorCmd `
        -Arguments $arguments `
        -LogPath $commandLog
    return [ordered]@{
        milliseconds = $milliseconds
        engineLog = $engineLog
        commandLog = $commandLog
    }
}

$defaultConfigPaths = @(
    (Join-Path $repoRoot 'HostProject\Config\DefaultEngine.ini'),
    (Join-Path $repoRoot 'HostProject\Config\DefaultInput.ini'))
$configSnapshots = @{}
foreach ($path in $defaultConfigPaths) {
    $configSnapshots[$path] = if (Test-Path -LiteralPath $path) {
        [IO.File]::ReadAllBytes($path)
    } else {
        $null
    }
}

$cleanupPass = $false
try {
    if (!$SkipCliBuild) {
        & $dotnetExe build $cliProject -c Release --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Finare CLI build failed with exit code $LASTEXITCODE."
        }
    }

    $types = @()
    $contractTypes = @()
    for ($typeIndex = 0; $typeIndex -lt $TypeCount; ++$typeIndex) {
        $typeName = 'ScaleType{0:D4}' -f $typeIndex
        $typePath = "/Script/FinareScale.$typeName"
        $properties = @()
        $memberNames = @()
        for ($memberIndex = 0; $memberIndex -lt $MembersPerType; ++$memberIndex) {
            $memberName = 'Value{0:D2}' -f $memberIndex
            $memberNames += $memberName
            $properties += [ordered]@{
                path = "$typePath`:$memberName"
                name = $memberName
                type = [ordered]@{
                    kind = 'int32'
                    nativeName = 'int32'
                }
                access = [ordered]@{
                    readable = $true
                    writable = $true
                    editorVisible = $true
                    nativeAccess = 'public'
                }
                metadata = [ordered]@{
                    displayName = "$typeName $memberName"
                    category = 'Finare|Scale'
                    tooltip = "Synthetic scale member $typeIndex/$memberIndex."
                    deprecated = $false
                }
            }
        }
        $types += [ordered]@{
            path = $typePath
            name = $typeName
            nativeName = "U$typeName"
            module = 'FinareScale'
            headerPath = "Scale/$typeName.h"
            kind = 'class'
            baseTypePath = '/Script/CoreUObject.Object'
            traits = [ordered]@{
                abstract = $false
                blueprintType = $true
                interface = $false
            }
            metadata = [ordered]@{
                displayName = $typeName
                tooltip = "Synthetic large-contract type $typeIndex."
                deprecated = $false
            }
            interfacePaths = @()
            enumValues = @()
            properties = $properties
            delegates = @()
            functions = @()
        }
        $contractTypes += [ordered]@{
            path = $typePath
            members = $memberNames
        }
    }

    $reflection = [ordered]@{
        schemaVersion = 'unifiedscript.unreal-reflection/v0.5'
        engine = [ordered]@{
            major = 5
            minor = 8
            patch = 1
            branch = '//UE5/Release-5.8'
        }
        modules = @('CoreUObject', 'FinareScale')
        types = $types
    }
    $expectedMemberCount = $TypeCount * $MembersPerType
    $contract = [ordered]@{
        schemaVersion = 'unifiedscript.unreal-api-contract/v0.2'
        contractName = 'Finare.Scale.Gate'
        managedNamespace = 'Finare.Unreal.Scale.Generated'
        nativeHeader = 'ScaleGeneratedThunks.h'
        nativeNamespace = 'UE::Finare::Scale::Generated'
        types = $contractTypes
        policy = [ordered]@{
            defaultAction = 'allow'
            rules = @()
        }
        coverageBaseline = [ordered]@{
            minimumMemberCount = $expectedMemberCount
            minimumDirectThunkCount = 0
            minimumCallFrameCount = $expectedMemberCount
            minimumDataPlaneOperationCount = 0
            minimumReflectionFallbackCount = 0
            maximumRejectedCount = 0
        }
    }
    $reflectionPath = Join-Path $contractDirectory 'Scale.reflection.json'
    $contractPath = Join-Path $contractDirectory 'Scale.api-contract.json'
    Write-Json -Value $reflection -Path $reflectionPath
    Write-Json -Value $contract -Path $contractPath

    $contractSamples = @()
    $contractTrialOutputs = @()
    for ($trial = 0; $trial -lt $ContractTrials; ++$trial) {
        $trialDirectory = Join-Path $contractDirectory ("trial-{0:D2}" -f $trial)
        $outputs = New-ContractOutputPaths -Directory $trialDirectory
        $contractTrialOutputs += $outputs
        $contractSamples += Invoke-ContractGeneration `
            -ReflectionPath $reflectionPath `
            -ContractPath $contractPath `
            -Outputs $outputs `
            -LogPath (Join-Path $trialDirectory 'generation.log')
    }

    $contractOutputNames = @('managed', 'native', 'nodes', 'coverage')
    $baselineContractDigests = [ordered]@{}
    $contractDeterministic = $true
    foreach ($name in $contractOutputNames) {
        $baselineContractDigests[$name] = Get-FileDigest `
            -Path $contractTrialOutputs[0][$name]
        for ($trial = 1; $trial -lt $ContractTrials; ++$trial) {
            if ((Get-FileDigest -Path $contractTrialOutputs[$trial][$name]) -ne
                $baselineContractDigests[$name]) {
                $contractDeterministic = $false
            }
        }
    }

    $coverage = Get-Content `
        -LiteralPath $contractTrialOutputs[0].coverage `
        -Raw | ConvertFrom-Json
    $nodes = Get-Content `
        -LiteralPath $contractTrialOutputs[0].nodes `
        -Raw | ConvertFrom-Json
    $nodePropertyCount = 0
    foreach ($nodeType in $nodes.types) {
        $nodePropertyCount += @($nodeType.properties).Count
    }

    $reflection.types[-1].properties[-1].metadata.tooltip =
        'Synthetic single-member incremental contract edit.'
    Write-Json -Value $reflection -Path $reflectionPath
    $incrementalContractOutputs = New-ContractOutputPaths `
        -Directory (Join-Path $contractDirectory 'incremental')
    $incrementalContractMilliseconds = Invoke-ContractGeneration `
        -ReflectionPath $reflectionPath `
        -ContractPath $contractPath `
        -Outputs $incrementalContractOutputs `
        -LogPath (Join-Path $contractDirectory 'incremental\generation.log')
    $contractManagedChanged =
        (Get-FileDigest -Path $incrementalContractOutputs.managed) -ne
            $baselineContractDigests.managed
    $contractNativeStable =
        (Get-FileDigest -Path $incrementalContractOutputs.native) -eq
            $baselineContractDigests.native
    $contractCoverageStable =
        (Get-FileDigest -Path $incrementalContractOutputs.coverage) -eq
            $baselineContractDigests.coverage
    $contractNodeChanged =
        (Get-FileDigest -Path $incrementalContractOutputs.nodes) -ne
            $baselineContractDigests.nodes

    $projectFile = Join-Path $scriptProjectDirectory 'Finare.ScaleScripts.csproj'
    $escapedReference = [Security.SecurityElement]::Escape(
        [IO.Path]::GetFullPath($unrealProjectReference))
    $projectXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>Finare.ScaleScripts</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$escapedReference" />
  </ItemGroup>
</Project>
"@
    Write-Utf8Text -Path $projectFile -Value $projectXml

    $scriptPaths = @()
    for ($scriptIndex = 0; $scriptIndex -lt $ScriptCount; ++$scriptIndex) {
        $className = 'ScaleScript{0:D4}' -f $scriptIndex
        $scriptPath = Join-Path $scriptProjectDirectory "$className.cs"
        $scriptSource = @"
using Finare;
using Finare.Unreal;

namespace Finare.Scale;

[ScriptAsset("Scale/$className")]
public sealed class $className : IUnrealManagedScript
{
    [Expose(InstanceEditable = true, Category = "Scale")]
    public int Value { get; set; } = $scriptIndex;

    [Visual]
    public int Compute(int input)
    {
        int result = input + Value;
        return result;
    }

    public void OnStart(UnrealScriptContext context) => _ = context;

    public void OnTick(float deltaSeconds) => _ = deltaSeconds;

    public void OnStop()
    {
    }
}
"@
        Write-Utf8Text -Path $scriptPath -Value $scriptSource
        $scriptPaths += $scriptPath
    }

    & $dotnetExe restore $projectFile --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Scale script restore failed with exit code $LASTEXITCODE."
    }
    $scriptColdEmitMilliseconds = Invoke-Measured `
        -FilePath $dotnetExe `
        -Arguments @(
            'run',
            '--project', $cliProject,
            '-c', 'Release',
            '--no-build',
            '--',
            'emit', $projectFile,
            '--out', $scriptOutputDirectory,
            '--configuration', 'Release',
            '--no-restore') `
        -LogPath (Join-Path $runDirectory 'script-cold-emit.log')

    $scriptBaseName = 'Finare.ScaleScripts'
    $scriptAssemblyPath = Join-Path $scriptOutputDirectory "$scriptBaseName.dll"
    $scriptChunkPath = Join-Path $scriptOutputDirectory "$scriptBaseName.uschunk"
    $scriptAssetPath = Join-Path $scriptOutputDirectory "$scriptBaseName.asset.json"
    $initialScriptAssemblyDigest = Get-FileDigest -Path $scriptAssemblyPath
    $initialScriptChunkDigest = Get-FileDigest -Path $scriptChunkPath
    $initialScriptAssets = Get-Content -LiteralPath $scriptAssetPath -Raw |
        ConvertFrom-Json
    $initialCompilationId = [string]$initialScriptAssets.sourceCompilationId

    $lastScriptPath = $scriptPaths[-1]
    $lastScriptSource = Get-Content -LiteralPath $lastScriptPath -Raw
    $oldDefault = '= {0};' -f ($ScriptCount - 1)
    $newDefault = '= {0};' -f $ScriptCount
    if (!$lastScriptSource.Contains($oldDefault)) {
        throw "Cannot locate the single-script incremental edit in '$lastScriptPath'."
    }
    Write-Utf8Text `
        -Path $lastScriptPath `
        -Value $lastScriptSource.Replace($oldDefault, $newDefault)
    $scriptIncrementalEmitMilliseconds = Invoke-Measured `
        -FilePath $dotnetExe `
        -Arguments @(
            'run',
            '--project', $cliProject,
            '-c', 'Release',
            '--no-build',
            '--',
            'emit', $projectFile,
            '--out', $scriptOutputDirectory,
            '--configuration', 'Release',
            '--no-restore') `
        -LogPath (Join-Path $runDirectory 'script-incremental-emit.log')

    $incrementalScriptAssets = Get-Content -LiteralPath $scriptAssetPath -Raw |
        ConvertFrom-Json
    $scriptAssemblyChanged =
        (Get-FileDigest -Path $scriptAssemblyPath) -ne $initialScriptAssemblyDigest
    $scriptChunkChanged =
        (Get-FileDigest -Path $scriptChunkPath) -ne $initialScriptChunkDigest
    $scriptCompilationChanged =
        [string]$incrementalScriptAssets.sourceCompilationId -ne $initialCompilationId

    $deploymentEntries = @()
    for ($scriptIndex = 0; $scriptIndex -lt $ScriptCount; ++$scriptIndex) {
        $className = 'ScaleScript{0:D4}' -f $scriptIndex
        $deploymentEntries += [ordered]@{
            assetPath = "/Game/$scalePackageSegment/$className"
            assemblyPath = [IO.Path]::GetFullPath($scriptChunkPath)
            typeName = "Finare.Scale.$className"
            requiredTargetClass =
                '/Script/FinareRuntime.FinareComponent'
            sourcePath = [IO.Path]::GetFullPath($scriptPaths[$scriptIndex])
        }
    }
    $deployment = [ordered]@{
        schemaVersion = 'unifiedscript.unreal-script-deployment/v0.1'
        scripts = $deploymentEntries
    }
    $deploymentPath = Join-Path $runDirectory 'scale.deployment.json'
    Write-Json -Value $deployment -Path $deploymentPath

    $editorValidation = Invoke-UnrealAssetSync `
        -ManifestPath $deploymentPath `
        -Name 'editor-startup-validation' `
        -ValidateOnly
    $validationMarker =
        "Finare deployment validated: Scripts=$ScriptCount"
    if (!(Select-String `
            -LiteralPath $editorValidation.engineLog `
            -SimpleMatch $validationMarker `
            -Quiet)) {
        throw "Scale deployment validation marker is missing: $($editorValidation.engineLog)"
    }

    $initialAssetSync = Invoke-UnrealAssetSync `
        -ManifestPath $deploymentPath `
        -Name 'asset-sync-initial'
    $initialSyncMarker =
        "Finare deployment synchronized: Scripts=$ScriptCount, Created=$ScriptCount, Updated=0"
    if (!(Select-String `
            -LiteralPath $initialAssetSync.engineLog `
            -SimpleMatch $initialSyncMarker `
            -Quiet)) {
        throw "Initial scale asset sync marker is missing: $($initialAssetSync.engineLog)"
    }
    $initialAssetFileCount = @(
        Get-ChildItem -LiteralPath $scaleContentDirectory -Filter '*.uasset' -File
    ).Count

    $changedDeploymentEntry = [ordered]@{}
    foreach ($property in $deploymentEntries[-1].GetEnumerator()) {
        $changedDeploymentEntry[$property.Key] = $property.Value
    }
    $changedDeploymentEntry.authoringServerUrl = 'http://127.0.0.1:4174'
    $churnDeployment = [ordered]@{
        schemaVersion = 'unifiedscript.unreal-script-deployment/v0.1'
        scripts = @($changedDeploymentEntry)
    }
    $deploymentChurnPath = Join-Path $runDirectory 'scale.deployment.churn.json'
    Write-Json -Value $churnDeployment -Path $deploymentChurnPath
    $incrementalAssetSync = Invoke-UnrealAssetSync `
        -ManifestPath $deploymentChurnPath `
        -Name 'asset-sync-single-entry-churn'
    $incrementalSyncMarker =
        'Finare deployment synchronized: Scripts=1, Created=0, Updated=1'
    if (!(Select-String `
            -LiteralPath $incrementalAssetSync.engineLog `
            -SimpleMatch $incrementalSyncMarker `
            -Quiet)) {
        throw "Incremental scale asset sync marker is missing: $($incrementalAssetSync.engineLog)"
    }
    $incrementalAssetFileCount = @(
        Get-ChildItem -LiteralPath $scaleContentDirectory -Filter '*.uasset' -File
    ).Count
}
finally {
    foreach ($path in $defaultConfigPaths) {
        if ($null -ne $configSnapshots[$path]) {
            [IO.File]::WriteAllBytes($path, $configSnapshots[$path])
        } elseif (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path
        }
    }
    if (Test-Path -LiteralPath $scaleContentDirectory) {
        $resolvedContentRoot = [IO.Path]::GetFullPath(
            (Join-Path $repoRoot 'HostProject\Content'))
        $resolvedScaleDirectory = [IO.Path]::GetFullPath($scaleContentDirectory)
        $requiredPrefix = $resolvedContentRoot.TrimEnd(
            [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (!$resolvedScaleDirectory.StartsWith(
                $requiredPrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolvedScaleDirectory) -ne $scalePackageSegment) {
            throw "Refusing to clean an unexpected scale content path: $resolvedScaleDirectory"
        }
        Remove-Item -LiteralPath $resolvedScaleDirectory -Recurse -Force
    }
    $cleanupPass = !(Test-Path -LiteralPath $scaleContentDirectory)
}

$contractCoveragePass =
    [int]$coverage.summary.typeCount -eq $TypeCount -and
    [int]$coverage.summary.memberCount -eq $expectedMemberCount -and
    [int]$coverage.summary.callFrameCount -eq $expectedMemberCount -and
    [int]$coverage.summary.reflectionFallbackCount -eq 0 -and
    [int]$coverage.summary.rejectedCount -eq 0 -and
    [int]$coverage.summary.deferredTypeCount -eq 0 -and
    [bool]$coverage.gate.passed -and
    $nodePropertyCount -eq $expectedMemberCount
$contractGenerationMedianMilliseconds = Get-Median -Values $contractSamples
$contractBudgetPass =
    $contractGenerationMedianMilliseconds -le $ContractGenerationBudgetMs -and
    $incrementalContractMilliseconds -le $ContractGenerationBudgetMs
$contractIncrementalPass =
    $contractManagedChanged -and
    $contractNativeStable -and
    $contractCoverageStable -and
    $contractNodeChanged
$scriptSetPass =
    @($initialScriptAssets.assets).Count -eq $ScriptCount -and
    @($incrementalScriptAssets.assets).Count -eq $ScriptCount -and
    $scriptAssemblyChanged -and
    $scriptChunkChanged -and
    $scriptCompilationChanged
$scriptBudgetPass =
    $scriptColdEmitMilliseconds -le $ScriptEmitBudgetMs -and
    $scriptIncrementalEmitMilliseconds -le $ScriptEmitBudgetMs
$editorStartupPass =
    $editorValidation.milliseconds -le $EditorStartupBudgetMs
$assetChurnPass =
    $initialAssetFileCount -eq $ScriptCount -and
    $incrementalAssetFileCount -eq $ScriptCount -and
    $initialAssetSync.milliseconds -le $AssetSyncBudgetMs -and
    $incrementalAssetSync.milliseconds -le $AssetSyncBudgetMs
$passed =
    $contractCoveragePass -and
    $contractDeterministic -and
    $contractBudgetPass -and
    $contractIncrementalPass -and
    $scriptSetPass -and
    $scriptBudgetPass -and
    $editorStartupPass -and
    $assetChurnPass -and
    $cleanupPass

$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
$sourceDirty = -not [string]::IsNullOrWhiteSpace(
    ((& git -C $repoRoot status --short) -join [Environment]::NewLine))
$report = [ordered]@{
    schemaVersion = 'unifiedscript.unreal-scale-gate/v0.1'
    status = if ($passed) { 'passed' } else { 'failed' }
    createdUtc = [DateTime]::UtcNow.ToString('O')
    sourceCommit = $sourceCommit
    sourceDirty = $sourceDirty
    engineRoot = [IO.Path]::GetFullPath($EngineRoot)
    configuration = [ordered]@{
        typeCount = $TypeCount
        membersPerType = $MembersPerType
        expectedMemberCount = $expectedMemberCount
        scriptCount = $ScriptCount
        contractTrials = $ContractTrials
    }
    thresholds = [ordered]@{
        contractGenerationMilliseconds = $ContractGenerationBudgetMs
        scriptEmitMilliseconds = $ScriptEmitBudgetMs
        editorStartupMilliseconds = $EditorStartupBudgetMs
        assetSyncMilliseconds = $AssetSyncBudgetMs
    }
    contract = [ordered]@{
        typeCount = [int]$coverage.summary.typeCount
        memberCount = [int]$coverage.summary.memberCount
        nodePropertyCount = $nodePropertyCount
        directThunkCount = [int]$coverage.summary.directThunkCount
        callFrameCount = [int]$coverage.summary.callFrameCount
        reflectionFallbackCount = [int]$coverage.summary.reflectionFallbackCount
        rejectedCount = [int]$coverage.summary.rejectedCount
        generationSamplesMilliseconds = @($contractSamples)
        generationMedianMilliseconds = $contractGenerationMedianMilliseconds
        singleMemberRebuildMilliseconds = $incrementalContractMilliseconds
        deterministic = $contractDeterministic
        changedManagedBindings = $contractManagedChanged
        stableNativeThunks = $contractNativeStable
        stableCoverage = $contractCoverageStable
        changedNodeCatalog = $contractNodeChanged
        managedBytes = (Get-Item -LiteralPath $contractTrialOutputs[0].managed).Length
        nativeBytes = (Get-Item -LiteralPath $contractTrialOutputs[0].native).Length
        nodeCatalogBytes = (Get-Item -LiteralPath $contractTrialOutputs[0].nodes).Length
        coverageBytes = (Get-Item -LiteralPath $contractTrialOutputs[0].coverage).Length
    }
    scripts = [ordered]@{
        assetCount = @($incrementalScriptAssets.assets).Count
        coldEmitMilliseconds = $scriptColdEmitMilliseconds
        singleScriptEmitMilliseconds = $scriptIncrementalEmitMilliseconds
        sourceCompilationChanged = $scriptCompilationChanged
        assemblyChanged = $scriptAssemblyChanged
        chunkChanged = $scriptChunkChanged
        assemblyBytes = (Get-Item -LiteralPath $scriptAssemblyPath).Length
        chunkBytes = (Get-Item -LiteralPath $scriptChunkPath).Length
        identitySidecarCount = @(
            Get-ChildItem -LiteralPath $scriptOutputDirectory -Filter '*.usids' -File
        ).Count
    }
    unreal = [ordered]@{
        editorStartupAndValidationMilliseconds = $editorValidation.milliseconds
        initialAssetSyncMilliseconds = $initialAssetSync.milliseconds
        singleEntryAssetChurnMilliseconds = $incrementalAssetSync.milliseconds
        initialCreatedCount = $ScriptCount
        churnUpdatedCount = 1
        assetFileCount = $incrementalAssetFileCount
        cleanupPassed = $cleanupPass
        validationLog = [IO.Path]::GetFullPath($editorValidation.engineLog)
        initialSyncLog = [IO.Path]::GetFullPath($initialAssetSync.engineLog)
        churnSyncLog = [IO.Path]::GetFullPath($incrementalAssetSync.engineLog)
    }
    gates = [ordered]@{
        contractCoverage = $contractCoveragePass
        contractDeterminism = $contractDeterministic
        contractTimeBudget = $contractBudgetPass
        localizedContractRebuild = $contractIncrementalPass
        scriptSet = $scriptSetPass
        scriptTimeBudget = $scriptBudgetPass
        editorStartupBudget = $editorStartupPass
        assetChurnBudget = $assetChurnPass
        cleanup = $cleanupPass
    }
}
Write-Json -Value $report -Path $reportPath

if (!$passed) {
    throw "One or more Unreal scale gates failed. Report: $reportPath"
}

Write-Host "Finare Unreal scale gate: PASS ($reportPath)"
Write-Host (
    "Contract=$expectedMemberCount members/$TypeCount types, " +
    "GenerationMedian=$contractGenerationMedianMilliseconds ms, " +
    "ContractIncremental=$incrementalContractMilliseconds ms, " +
    "Scripts=$ScriptCount, ColdEmit=$scriptColdEmitMilliseconds ms, " +
    "ScriptIncremental=$scriptIncrementalEmitMilliseconds ms, " +
    "EditorStartup=$($editorValidation.milliseconds) ms, " +
    "AssetCreate=$($initialAssetSync.milliseconds) ms, " +
    "AssetChurn=$($incrementalAssetSync.milliseconds) ms")
