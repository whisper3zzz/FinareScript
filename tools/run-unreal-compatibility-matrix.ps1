[CmdletBinding()]
param(
    [string]$EngineRoot = $env:FINARE_UE_ROOT,
    [string]$OutputDirectory,
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$SdkVersion = '1.0.0-rc.1',
    [ValidateRange(10, 300)]
    [int]$SmokeTimeoutSeconds = 90,
    [ValidateRange(1, 256)]
    [int]$ExpectedAutomationTests = 19
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'HostProject\FinareHost.uproject'
$projectRoot = Split-Path -Parent $projectPath

if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = @('D:\UE_5.8', 'D:\UE\_5.8') |
        Where-Object {
            Test-Path (Join-Path $_ 'Engine\Build\BatchFiles\Build.bat')
        } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'Pass -EngineRoot or set FINARE_UE_ROOT.'
}
$EngineRoot = [IO.Path]::GetFullPath($EngineRoot)

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot "artifacts\ue\matrix\$runId"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

$buildBat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\Build.bat'
$runUat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\RunUAT.bat'
$editorCmd = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
$dotnetExe = Join-Path $EngineRoot `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
$buildVersionPath = Join-Path $EngineRoot 'Engine\Build\Build.version'
$releaseScript = Join-Path $PSScriptRoot 'package-unreal-release.ps1'
$sdkPackageScript = Join-Path $PSScriptRoot 'package-finare-sdk.ps1'
$emitDemoScript = Join-Path $PSScriptRoot 'emit-unreal-demo-chunk.ps1'
foreach ($requiredPath in @(
        $projectPath,
        $buildBat,
        $runUat,
        $editorCmd,
        $dotnetExe,
        $buildVersionPath,
        $releaseScript,
        $sdkPackageScript,
        $emitDemoScript)) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Compatibility-matrix dependency is missing: $requiredPath"
    }
}

$buildVersion = Get-Content -LiteralPath $buildVersionPath -Raw | ConvertFrom-Json
$engineVersion = '{0}.{1}.{2}' -f `
    $buildVersion.MajorVersion, `
    $buildVersion.MinorVersion, `
    $buildVersion.PatchVersion
if ($engineVersion -ne '5.8.1') {
    throw "The primary compatibility gate requires UE 5.8.1; '$EngineRoot' is $engineVersion."
}

$phaseReports = [Collections.Generic.List[object]]::new()
function Invoke-MatrixPhase {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [scriptblock]$Action
    )

    $phaseId = '{0:D2}-{1}' -f `
        ($script:phaseReports.Count + 1), `
        ($Name -replace '[^0-9A-Za-z.-]', '-')
    $logPath = Join-Path $runDirectory "$phaseId.log"
    $startedAt = [DateTime]::UtcNow
    Write-Host "[$phaseId] START"
    try {
        & $Action *> $logPath
        $script:phaseReports.Add([ordered]@{
            name = $Name
            status = 'passed'
            durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
            log = $logPath
            error = $null
        })
        Write-Host "[$phaseId] PASS"
    } catch {
        $message = $_.Exception.Message
        Add-Content -LiteralPath $logPath -Value "`nERROR: $message" -Encoding utf8
        $script:phaseReports.Add([ordered]@{
            name = $Name
            status = 'failed'
            durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
            log = $logPath
            error = $message
        })
        Write-Host "[$phaseId] FAIL: $message"
        throw
    }
}

function Write-Utf8File {
    param([string]$Path, [string]$Content)

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) |
        Out-Null
    [IO.File]::WriteAllText(
        $Path,
        $Content.Replace("`r`n", "`n"),
        [Text.UTF8Encoding]::new($false))
}

$defaultEnginePath = Join-Path $projectRoot 'Config\DefaultEngine.ini'
$defaultInputPath = Join-Path $projectRoot 'Config\DefaultInput.ini'
$defaultEngineExisted = Test-Path -LiteralPath $defaultEnginePath
$defaultInputExisted = Test-Path -LiteralPath $defaultInputPath
$defaultEngineBytes = if ($defaultEngineExisted) {
    [IO.File]::ReadAllBytes($defaultEnginePath)
} else { $null }
$defaultInputBytes = if ($defaultInputExisted) {
    [IO.File]::ReadAllBytes($defaultInputPath)
} else { $null }

$sourceReleaseReport = $null
$sdkReportA = $null
$sdkReportB = $null
$sdkInstallReport = $null
$cleanReleaseReport = $null
$compatibilityScratchRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ('us-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$automationPassed = 0
$failure = $null
$startedAt = [DateTime]::UtcNow
try {
    Invoke-MatrixPhase -Name 'managed-demo-debug-chunk' -Action {
        & $emitDemoScript `
            -EngineRoot $EngineRoot `
            -Configuration Debug
        if ($LASTEXITCODE -ne 0) {
            throw "Managed demo/chunk emission failed with exit code $LASTEXITCODE."
        }
    }

    Invoke-MatrixPhase -Name 'editor-development-build' -Action {
        & $buildBat `
            FinareHostEditor `
            Win64 `
            Development `
            $projectPath `
            -WaitMutex `
            -NoHotReloadFromIDE `
            -NoXGE
        if ($LASTEXITCODE -ne 0) {
            throw "Editor Development build failed with exit code $LASTEXITCODE."
        }
    }

    $automationLog = Join-Path $runDirectory 'automation.log'
    Invoke-MatrixPhase -Name 'api-discovery-fixture' -Action {
        & (Join-Path $PSScriptRoot 'prepare-api-discovery.ps1') -EngineRoot $EngineRoot
    }
    Invoke-MatrixPhase -Name 'pure-csharp-fixture' -Action {
        & (Join-Path $PSScriptRoot 'prepare-pure-csharp.ps1') -EngineRoot $EngineRoot
    }
    Invoke-MatrixPhase -Name 'editor-automation' -Action {
        $previousErrorActionPreference = $ErrorActionPreference
        $automationExitCode = -1
        try {
            # Expected failure-containment tests emit structured diagnostics on
            # native stderr. Keep those diagnostics in the phase log without
            # letting Windows PowerShell promote them to a terminating error;
            # the process exit code and exact Automation assertions below own
            # the verdict.
            $ErrorActionPreference = 'Continue'
            & $editorCmd @(
                $projectPath,
                '-unattended',
                '-nop4',
                '-NullRHI',
                '-nosplash',
                '-NoSound',
                '-ExecCmds=Automation RunTests Finare.Unreal',
                '-TestExit=Automation Test Queue Empty',
                "-abslog=$automationLog",
                '-log')
            $automationExitCode = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($automationExitCode -ne 0) {
            throw "Editor Automation failed with exit code $automationExitCode."
        }
        $automationText = Get-Content -LiteralPath $automationLog -Raw
        $script:automationPassed = [regex]::Matches(
            $automationText,
            'Test Completed\. Result=\{Success\}[^\r\n]*Path=\{Finare\.Unreal(?:\.|\})').Count
        if ($script:automationPassed -ne $ExpectedAutomationTests) {
            throw (
                "Expected $ExpectedAutomationTests successful Finare.Unreal " +
                "tests, found $($script:automationPassed).")
        }
        if ($automationText -notmatch 'Automation Test Queue Empty') {
            throw 'Automation process exited without the queue-empty marker.'
        }
    }

    $sourceReleaseDirectory = Join-Path $runDirectory 'source-release'
    Invoke-MatrixPhase -Name 'source-development-shipping-package' -Action {
        & $releaseScript `
            -EngineRoot $EngineRoot `
            -Project $projectPath `
            -Configurations @('Development', 'Shipping') `
            -SmokeTimeoutSeconds $SmokeTimeoutSeconds `
            -OutputDirectory $sourceReleaseDirectory `
            -ExpectedEngineVersion '5.8.1'
    }
    $sourceReleaseReportPath = Join-Path $sourceReleaseDirectory 'release-gate.json'
    $sourceReleaseReport = Get-Content -LiteralPath $sourceReleaseReportPath -Raw |
        ConvertFrom-Json
    if ($sourceReleaseReport.status -ne 'passed' -or
        @($sourceReleaseReport.configurations).Count -ne 2) {
        throw 'Source Development/Shipping release report is incomplete.'
    }

    $sdkOutputA = Join-Path $runDirectory 'sdk-a'
    $sdkOutputB = Join-Path $runDirectory 'sdk-b'
    Invoke-MatrixPhase -Name 'sdk-package-first' -Action {
        & $sdkPackageScript `
            -Version $SdkVersion `
            -OutputDirectory $sdkOutputA `
            -EngineRoot $EngineRoot
    }
    Invoke-MatrixPhase -Name 'sdk-package-repeat' -Action {
        & $sdkPackageScript `
            -Version $SdkVersion `
            -OutputDirectory $sdkOutputB `
            -EngineRoot $EngineRoot
    }
    $sdkBaseName = "Finare-SDK-$SdkVersion-win-x64"
    $sdkReportAPath = Join-Path $sdkOutputA "$sdkBaseName.package-report.json"
    $sdkReportBPath = Join-Path $sdkOutputB "$sdkBaseName.package-report.json"
    $sdkReportA = Get-Content -LiteralPath $sdkReportAPath -Raw | ConvertFrom-Json
    $sdkReportB = Get-Content -LiteralPath $sdkReportBPath -Raw | ConvertFrom-Json
    if ($sdkReportA.status -ne 'passed' -or $sdkReportB.status -ne 'passed' -or
        $sdkReportA.releaseManifestSchemaVersion -ne
            'unifiedscript.product-release/v0.1' -or
        $sdkReportA.releaseStatus -ne 'releaseCandidate' -or
        [int]$sdkReportA.documentationEntryCount -lt 22 -or
        $sdkReportA.archiveSha256 -ne $sdkReportB.archiveSha256 -or
        [long]$sdkReportA.archiveBytes -ne [long]$sdkReportB.archiveBytes -or
        [int]$sdkReportA.payloadFileCount -ne [int]$sdkReportB.payloadFileCount) {
        throw 'Repeated SDK packaging was not deterministic.'
    }

    $sdkArchive = Join-Path $sdkOutputA "$sdkBaseName.zip"
    $sdkExtract = Join-Path $compatibilityScratchRoot 'sdk'
    $sdkRoot = Join-Path $sdkExtract $sdkBaseName
    $consumerArchive = Join-Path $runDirectory 'consumer.zip'
    $consumerExport = Join-Path $compatibilityScratchRoot 'consumer'
    $consumerProject = Join-Path $consumerExport `
        'HostProject\FinareHost.uproject'
    Invoke-MatrixPhase -Name 'clean-project-sdk-install' -Action {
        New-Item -ItemType Directory -Force -Path $compatibilityScratchRoot |
            Out-Null
        Expand-Archive `
            -LiteralPath $sdkArchive `
            -DestinationPath $sdkExtract `
            -Force
        & git -C $repoRoot archive `
            --format=zip `
            "--output=$consumerArchive" `
            HEAD `
            HostProject/Config `
            HostProject/Source `
            HostProject/FinareHost.uproject
        if ($LASTEXITCODE -ne 0) {
            throw "Clean HostProject export failed with exit code $LASTEXITCODE."
        }
        Expand-Archive `
            -LiteralPath $consumerArchive `
            -DestinationPath $consumerExport `
            -Force
        if (Test-Path -LiteralPath (Join-Path $consumerExport 'HostProject\Plugins')) {
            throw 'Clean HostProject export unexpectedly contains a Plugins directory.'
        }
        & (Join-Path $sdkRoot 'Tools\install-finare-sdk.ps1') `
            -Project $consumerProject `
            -SdkRoot $sdkRoot
    }
    $sdkInstallSourceReportPath = Get-ChildItem `
        -LiteralPath (Join-Path $consumerExport 'HostProject\Saved\Finare\Install') `
        -Recurse `
        -File | Where-Object Name -EQ 'install-report.json' |
        Sort-Object LastWriteTimeUtc |
        Select-Object -Last 1 -ExpandProperty FullName
    $sdkInstallReport = Get-Content -LiteralPath $sdkInstallSourceReportPath -Raw |
        ConvertFrom-Json
    if ($sdkInstallReport.status -ne 'passed') {
        throw 'The clean-project SDK installation report did not pass.'
    }
    $sdkInstallReportPath = Join-Path $runDirectory 'sdk-install-report.json'
    Copy-Item `
        -LiteralPath $sdkInstallSourceReportPath `
        -Destination $sdkInstallReportPath `
        -Force

    $installedManagedRoot = Join-Path $consumerExport `
        'HostProject\Plugins\Finare\Binaries\Managed'
    $consumerManagedRoot = Join-Path $consumerExport `
        'HostProject\Managed\Finare.Unreal.Demo'
    $demoStage = Join-Path $installedManagedRoot 'Demo'
    $consumerCsproj = Join-Path $consumerManagedRoot `
        'Finare.Unreal.Demo.csproj'
    $consumerSource = Join-Path $consumerManagedRoot 'DemoScript.cs'
    $referenceItems = Get-ChildItem `
        -LiteralPath $installedManagedRoot `
        -Filter 'Finare.*.dll' `
        -File | Sort-Object Name | ForEach-Object {
            $name = [IO.Path]::GetFileNameWithoutExtension($_.Name)
            $path = [Security.SecurityElement]::Escape($_.FullName.Replace('\', '/'))
            '    <Reference Include="{0}"><HintPath>{1}</HintPath><Private>false</Private></Reference>' -f `
                $name, $path
        }
    $consumerProjectText = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>Finare.Unreal.Demo</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Deterministic>true</Deterministic>
    <DebugType>portable</DebugType>
  </PropertyGroup>
  <ItemGroup>
$($referenceItems -join "`n")
  </ItemGroup>
</Project>
"@
    $consumerSourceText = @'
using Finare;
using Finare.Unreal;

namespace Finare.Unreal.Demo;

[ScriptAsset("Unreal/Demo")]
public sealed class DemoScript : IUnrealManagedScript
{
    private HostComponent _component = null!;

    [Expose(InstanceEditable = true, SaveGame = true, Category = "Demo")]
    public int InitialValue { get; set; } = 10;

    [Visual]
    public int ResolveInitialValue(int bonus)
    {
        int result = InitialValue + bonus;
        if (result > 100)
        {
            result = 100;
        }

        return result;
    }

    public void OnStart(UnrealScriptContext context)
    {
        _component = new HostComponent(context.Host, context.Bridge);
        _component.ManagedState = $"Started:{InitialValue}";
    }

    public void OnTick(float deltaSeconds)
    {
        _component.ManagedState = "Ticked";
    }

    public void OnStop()
    {
        _component.ManagedState = "Stopped";
    }

    private sealed class HostComponent(
        UnrealObjectHandle handle,
        IUnrealInvocationBridge bridge) : UnrealObject(handle, bridge)
    {
        public string ManagedState
        {
            set => WriteProperty(
                0x31BC2AC640227065UL,
                "/Script/FinareRuntime.FinareComponent:ManagedState",
                value);
        }
    }
}
'@
    Write-Utf8File -Path $consumerCsproj -Content $consumerProjectText
    Write-Utf8File -Path $consumerSource -Content $consumerSourceText

    $sdkCli = Join-Path $sdkRoot 'Tools\Finare.Cli\Finare.Cli.dll'
    Invoke-MatrixPhase -Name 'clean-project-managed-chunk' -Action {
        & $dotnetExe `
            $sdkCli `
            emit `
            $consumerCsproj `
            --out $demoStage `
            --configuration Release
        if ($LASTEXITCODE -ne 0) {
            throw "Installed SDK emit failed with exit code $LASTEXITCODE."
        }
        $consumerChunk = Join-Path $demoStage `
            'Finare.Unreal.Demo.uschunk'
        & $dotnetExe $sdkCli chunk-inspect $consumerChunk
        if ($LASTEXITCODE -ne 0) {
            throw "Installed SDK chunk verification failed with exit code $LASTEXITCODE."
        }
    }

    $cleanReleaseDirectory = Join-Path $runDirectory 'installed-release'
    Invoke-MatrixPhase -Name 'clean-project-development-package' -Action {
        & $releaseScript `
            -EngineRoot $EngineRoot `
            -Project $consumerProject `
            -Configurations Development `
            -SmokeTimeoutSeconds $SmokeTimeoutSeconds `
            -OutputDirectory $cleanReleaseDirectory `
            -ExpectedEngineVersion '5.8.1' `
            -SkipManagedBuild
    }
    $cleanReleaseReportPath = Join-Path $cleanReleaseDirectory 'release-gate.json'
    $cleanReleaseReport = Get-Content -LiteralPath $cleanReleaseReportPath -Raw |
        ConvertFrom-Json
    if ($cleanReleaseReport.status -ne 'passed' -or
        @($cleanReleaseReport.configurations).Count -ne 1 -or
        ![bool]$cleanReleaseReport.configurations[0].pluginLocalRuntime -or
        ![bool]$cleanReleaseReport.configurations[0].poisonedExternalDotNetRoots) {
        throw 'Clean-project packaged runtime dependency closure did not pass.'
    }
} catch {
    $failure = $_.Exception.Message
} finally {
    if ($defaultEngineExisted) {
        [IO.File]::WriteAllBytes($defaultEnginePath, $defaultEngineBytes)
    } elseif (Test-Path -LiteralPath $defaultEnginePath) {
        Remove-Item -LiteralPath $defaultEnginePath
    }
    if ($defaultInputExisted) {
        [IO.File]::WriteAllBytes($defaultInputPath, $defaultInputBytes)
    } elseif (Test-Path -LiteralPath $defaultInputPath) {
        Remove-Item -LiteralPath $defaultInputPath
    }
    if (Test-Path -LiteralPath $compatibilityScratchRoot) {
        Remove-Item `
            -LiteralPath $compatibilityScratchRoot `
            -Recurse `
            -Force `
            -ErrorAction SilentlyContinue
    }
}

$sourceRevision = (& git -C $repoRoot rev-parse HEAD).Trim()
$sourceTreeDirty = @(& git -C $repoRoot status --porcelain).Count -ne 0
$supportedRows = @()
if ($null -ne $sourceReleaseReport) {
    $supportedRows += [ordered]@{
        target = 'Editor'
        platform = 'Win64'
        configuration = 'Development'
        build = 'passed'
        automation = [ordered]@{
            passed = $automationPassed
            expected = $ExpectedAutomationTests
        }
    }
    foreach ($configuration in @($sourceReleaseReport.configurations)) {
        $supportedRows += [ordered]@{
            target = 'Game'
            platform = 'Win64'
            configuration = $configuration.configuration
            build = $configuration.build
            cook = $configuration.cook
            stage = $configuration.stage
            pak = $configuration.pak
            archive = $configuration.archive
            packagedSmoke = if ($configuration.smokeExitCode -eq 0) {
                'passed'
            } else { 'failed' }
            pluginLocalRuntime = [bool]$configuration.pluginLocalRuntime
            compiledChunk = $configuration.demoChunk
        }
    }
}
if ($null -ne $cleanReleaseReport) {
    $cleanConfiguration = $cleanReleaseReport.configurations[0]
    $supportedRows += [ordered]@{
        target = 'SDK-installed Game'
        platform = 'Win64'
        configuration = $cleanConfiguration.configuration
        build = $cleanConfiguration.build
        cook = $cleanConfiguration.cook
        stage = $cleanConfiguration.stage
        pak = $cleanConfiguration.pak
        archive = $cleanConfiguration.archive
        packagedSmoke = if ($cleanConfiguration.smokeExitCode -eq 0) {
            'passed'
        } else { 'failed' }
        pluginLocalRuntime = [bool]$cleanConfiguration.pluginLocalRuntime
        externalDotNetRootsPoisoned =
            [bool]$cleanConfiguration.poisonedExternalDotNetRoots
    }
}

$reportPath = Join-Path $runDirectory 'compatibility-matrix.json'
$report = [ordered]@{
    schemaVersion = 'unifiedscript.unreal-compatibility-matrix/v0.1'
    status = if ($null -eq $failure) { 'passed' } else { 'failed' }
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
    sourceRevision = $sourceRevision
    sourceTreeDirty = $sourceTreeDirty
    engine = [ordered]@{
        root = $EngineRoot
        version = $engineVersion
        changelist = [long]$buildVersion.Changelist
        branch = [string]$buildVersion.BranchName
    }
    supported = $supportedRows
    sdk = if ($null -eq $sdkReportA) { $null } else {
        [ordered]@{
            version = $SdkVersion
            archive = $sdkReportA.archive
            archiveSha256 = $sdkReportA.archiveSha256
            repeatArchiveSha256 = $sdkReportB.archiveSha256
            deterministic = $sdkReportA.archiveSha256 -eq $sdkReportB.archiveSha256
            payloadFileCount = [int]$sdkReportA.payloadFileCount
            installReport = $sdkInstallReportPath
            cleanProjectInstall = $sdkInstallReport.status
        }
    }
    unsupported = @(
        [ordered]@{
            target = 'WindowsServer'
            status = 'not-declared'
            reason = 'No packaged Server target and smoke lane exists yet.'
        },
        [ordered]@{
            platform = 'Linux'
            status = 'not-declared'
            reason = 'Linux CoreCLR staging, cook, and lifecycle gates are not implemented.'
        },
        [ordered]@{
            platform = 'macOS, iOS, Android, consoles'
            status = 'not-declared'
            reason = 'No platform-specific runtime closure or packaged smoke evidence exists.'
        })
    phases = $phaseReports
    outputs = [ordered]@{
        sourceRelease = if ($null -eq $sourceReleaseReport) {
            $null
        } else { $sourceReleaseReportPath }
        sdkPackageFirst = if ($null -eq $sdkReportA) { $null } else { $sdkReportAPath }
        sdkPackageRepeat = if ($null -eq $sdkReportB) { $null } else { $sdkReportBPath }
        installedRelease = if ($null -eq $cleanReleaseReport) {
            $null
        } else { $cleanReleaseReportPath }
        automationLog = $automationLog
    }
    failure = $failure
}
[IO.File]::WriteAllText(
    $reportPath,
    ($report | ConvertTo-Json -Depth 12) + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

if ($null -ne $failure) {
    throw "UE compatibility matrix failed: $failure. Report: $reportPath"
}

Write-Host "UE 5.8.1 compatibility matrix: PASS ($reportPath)"
Write-Host (
    "Rows=$($supportedRows.Count), Automation=$automationPassed/$ExpectedAutomationTests, " +
    "SDK=$($sdkReportA.archiveSha256)")
