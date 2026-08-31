[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Project,

    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,

    [string]$Config,

    [string[]]$Modules,

    [string[]]$Classes,

    [string]$Report,

    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:PhaseRecords = [Collections.Generic.List[object]]::new()
$script:CurrentPhase = 'bootstrap'
$script:ResolvedEngineRoot = $null
$script:ResolvedProjectPath = $null
$script:ResolvedConfigPath = $null
$script:ReportPath = $null
$script:RunDirectory = $null
$script:StartedAtUtc = [DateTime]::UtcNow
$script:AutomationSuccessCount = 0
$script:AutomationFailureCount = 0

function Throw-OnboardingError {
    param(
        [Parameter(Mandatory)]
        [string]$Code,

        [Parameter(Mandatory)]
        [string]$Message
    )

    $exception = [InvalidOperationException]::new($Message)
    $exception.Data['UnifiedScriptCode'] = $Code
    throw $exception
}

function Get-OnboardingErrorCode {
    param([Exception]$Exception)

    if ($null -ne $Exception -and
        $null -ne $Exception.Data -and
        $Exception.Data.Contains('UnifiedScriptCode')) {
        return [string]$Exception.Data['UnifiedScriptCode']
    }
    return 'USONB9999'
}

function Invoke-OnboardingPhase {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Action
    )

    $script:CurrentPhase = $Name
    $startedAt = [DateTime]::UtcNow
    Write-Host "[$Name]"
    try {
        & $Action
        $script:PhaseRecords.Add([ordered]@{
            name = $Name
            status = 'passed'
            durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
        })
    } catch {
        $script:PhaseRecords.Add([ordered]@{
            name = $Name
            status = 'failed'
            durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
            code = Get-OnboardingErrorCode -Exception $_.Exception
            message = $_.Exception.Message
        })
        throw
    }
}

function Assert-AllowedProperties {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string[]]$Allowed,

        [Parameter(Mandatory)]
        [string]$Context
    )

    foreach ($property in $Value.PSObject.Properties.Name) {
        if ($property -notin $Allowed) {
            Throw-OnboardingError `
                -Code 'USONB1002' `
                -Message "$Context contains unknown property '$property'."
        }
    }
}

function Resolve-FullPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$BaseDirectory
    )

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    return [IO.Path]::GetFullPath((Join-Path $BaseDirectory $Path))
}

function Get-RelativeFilePath {
    param(
        [Parameter(Mandatory)][string]$BaseDirectory,
        [Parameter(Mandatory)][string]$Path
    )

    $basePath = [IO.Path]::GetFullPath($BaseDirectory).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    $baseUri = [Uri]$basePath
    $pathUri = [Uri][IO.Path]::GetFullPath($Path)
    return [Uri]::UnescapeDataString(
        $baseUri.MakeRelativeUri($pathUri).ToString()).Replace(
            '/',
            [IO.Path]::DirectorySeparatorChar)
}

function Assert-RequiredPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Label,

        [string]$Code = 'USONB1101'
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        Throw-OnboardingError -Code $Code -Message "$Label does not exist: $Path"
    }
}

function Get-NearestExistingDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $candidate = if ([IO.Path]::HasExtension($Path)) {
        Split-Path -Parent $Path
    } else {
        $Path
    }
    while (-not [string]::IsNullOrWhiteSpace($candidate) -and
        -not (Test-Path -LiteralPath $candidate -PathType Container)) {
        $parent = Split-Path -Parent $candidate
        if ($parent -eq $candidate) { break }
        $candidate = $parent
    }
    return $candidate
}

function Assert-WritableDestination {
    param([Parameter(Mandatory)][string]$Path)

    $directory = Get-NearestExistingDirectory -Path $Path
    if ([string]::IsNullOrWhiteSpace($directory)) {
        Throw-OnboardingError `
            -Code 'USONB1106' `
            -Message "No existing parent directory is available for output: $Path"
    }
    $item = Get-Item -LiteralPath $directory
    if ($item.Attributes -band [IO.FileAttributes]::ReadOnly) {
        Throw-OnboardingError `
            -Code 'USONB1107' `
            -Message "Output parent is read-only: $directory"
    }
}

function Resolve-EngineFromAssociation {
    param([string]$Association)

    if ([string]::IsNullOrWhiteSpace($Association)) {
        return $null
    }

    $customBuildsKey = 'HKCU:\Software\Epic Games\Unreal Engine\Builds'
    if (Test-Path $customBuildsKey) {
        $customBuilds = Get-ItemProperty -Path $customBuildsKey
        $customValue = $customBuilds.PSObject.Properties |
            Where-Object Name -EQ $Association |
            Select-Object -ExpandProperty Value -First 1
        if (-not [string]::IsNullOrWhiteSpace($customValue)) {
            return [string]$customValue
        }
    }

    foreach ($root in @('HKLM:\SOFTWARE\EpicGames\Unreal Engine',
                         'HKLM:\SOFTWARE\WOW6432Node\EpicGames\Unreal Engine')) {
        $versionKey = Join-Path $root $Association
        if (Test-Path $versionKey) {
            $installedDirectory = (Get-ItemProperty -Path $versionKey).InstalledDirectory
            if (-not [string]::IsNullOrWhiteSpace($installedDirectory)) {
                return [string]$installedDirectory
            }
        }
    }
    return $null
}

function Resolve-UnrealEngineRoot {
    param(
        [string]$RequestedRoot,
        [string]$Association
    )

    $resolved = $RequestedRoot
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        $resolved = Resolve-EngineFromAssociation -Association $Association
    }
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        Throw-OnboardingError `
            -Code 'USONB1102' `
            -Message (
                "Unreal Engine could not be resolved. Pass -EngineRoot, set " +
                "UNIFIEDSCRIPT_UE_ROOT, or register EngineAssociation '$Association'.")
    }
    $resolved = [IO.Path]::GetFullPath($resolved)
    Assert-RequiredPath `
        -Path (Join-Path $resolved 'Engine\Build\BatchFiles\Build.bat') `
        -Label 'Unreal Build.bat' `
        -Code 'USONB1103'
    Assert-RequiredPath `
        -Path (Join-Path $resolved 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe') `
        -Label 'UnrealEditor-Cmd.exe' `
        -Code 'USONB1103'
    Assert-RequiredPath `
        -Path (Join-Path $resolved 'Engine\Build\Build.version') `
        -Label 'Unreal Build.version' `
        -Code 'USONB1103'
    return $resolved
}

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [object[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$LogPath,

        [Parameter(Mandatory)]
        [string]$FailureCode,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    $logDirectory = Split-Path -Parent $LogPath
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    # Windows PowerShell 5.1 wraps native stderr records as non-terminating
    # NativeCommandError objects. Some Unreal negative-path Automation tests
    # intentionally write expected failures to stderr, so use the process exit
    # code as the command contract and keep the complete merged stream in logs.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $FilePath @Arguments 2>&1 |
            Tee-Object -FilePath $LogPath |
            ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        $tail = if (Test-Path -LiteralPath $LogPath) {
            (Get-Content -LiteralPath $LogPath -Tail 30) -join [Environment]::NewLine
        } else {
            '<log unavailable>'
        }
        Throw-OnboardingError `
            -Code $FailureCode `
            -Message "$FailureMessage ExitCode=$exitCode. Log=$LogPath`n$tail"
    }
}

function Invoke-BindingGeneration {
    param(
        [Parameter(Mandatory)][string]$DotNet,
        [Parameter(Mandatory)][string]$CliAssembly,
        [Parameter(Mandatory)][string]$ReflectionManifest,
        [Parameter(Mandatory)][string]$Contract,
        [Parameter(Mandatory)][string]$ManagedOutput,
        [Parameter(Mandatory)][string]$NativeOutput,
        [Parameter(Mandatory)][string]$NodeCatalogOutput,
        [Parameter(Mandatory)][string]$BrowserOutput,
        [Parameter(Mandatory)][string]$CoverageOutput,
        [Parameter(Mandatory)][bool]$FailOnRejected,
        [Parameter(Mandatory)][string]$LogPath
    )

    foreach ($output in @(
        $ManagedOutput,
        $NativeOutput,
        $NodeCatalogOutput,
        $BrowserOutput,
        $CoverageOutput)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
    }
    $arguments = @(
        $CliAssembly,
        'ue-bindings',
        $ReflectionManifest,
        '--contract', $Contract,
        '--out', $ManagedOutput,
        '--native-out', $NativeOutput,
        '--nodes-out', $NodeCatalogOutput,
        '--browser-out', $BrowserOutput,
        '--report', $CoverageOutput
    )
    if ($FailOnRejected) {
        $arguments += '--fail-on-rejected'
    }
    Invoke-LoggedCommand `
        -FilePath $DotNet `
        -Arguments $arguments `
        -LogPath $LogPath `
        -FailureCode 'USONB2002' `
        -FailureMessage 'Unreal binding generation failed.'
}

function Assert-GeneratedFileMatches {
    param(
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][string]$Actual
    )

    Assert-RequiredPath -Path $Expected -Label 'Checked-in generated artifact' -Code 'USONB2003'
    Assert-RequiredPath -Path $Actual -Label 'Validation generated artifact' -Code 'USONB2003'
    $expectedHash = (Get-FileHash -LiteralPath $Expected -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $Actual -Algorithm SHA256).Hash
    if ($expectedHash -ne $actualHash) {
        Throw-OnboardingError `
            -Code 'USONB2004' `
            -Message "Generated artifact is stale: $Expected"
    }
}

function Get-FileSnapshot {
    param([Parameter(Mandatory)][string[]]$Paths)

    return @($Paths | ForEach-Object {
        [pscustomobject]@{
            Path = $_
            Existed = Test-Path -LiteralPath $_
            Bytes = if (Test-Path -LiteralPath $_) {
                [Convert]::ToBase64String([IO.File]::ReadAllBytes($_))
            } else {
                $null
            }
        }
    })
}

function Restore-FileSnapshot {
    param([Parameter(Mandatory)][object[]]$Snapshots)

    foreach ($snapshot in $Snapshots) {
        if ($snapshot.Existed) {
            [IO.File]::WriteAllBytes(
                $snapshot.Path,
                [Convert]::FromBase64String($snapshot.Bytes))
        } elseif (Test-Path -LiteralPath $snapshot.Path) {
            Remove-Item -LiteralPath $snapshot.Path
        }
    }
}

function Write-OnboardingReport {
    param(
        [Parameter(Mandatory)][string]$Status,
        [string]$ErrorCode,
        [string]$ErrorMessage,
        [object]$Context
    )

    if ([string]::IsNullOrWhiteSpace($script:ReportPath)) {
        return
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $script:ReportPath) |
        Out-Null
    $reportDocument = [ordered]@{
        schemaVersion = 'unifiedscript.unreal-project-onboarding-report/v0.1'
        status = $Status
        mode = if ($ValidateOnly) { 'validateOnly' } else { 'apply' }
        startedAtUtc = $script:StartedAtUtc.ToString('O')
        completedAtUtc = [DateTime]::UtcNow.ToString('O')
        durationMs = [long]([DateTime]::UtcNow - $script:StartedAtUtc).TotalMilliseconds
        project = $script:ResolvedProjectPath
        config = $script:ResolvedConfigPath
        engineRoot = $script:ResolvedEngineRoot
        currentPhase = $script:CurrentPhase
        phases = @($script:PhaseRecords)
        error = if ($Status -eq 'passed') {
            $null
        } else {
            [ordered]@{ code = $ErrorCode; message = $ErrorMessage }
        }
        outputs = $Context
    }
    $json = $reportDocument | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText(
        $script:ReportPath,
        $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

try {
    $script:ResolvedProjectPath = [IO.Path]::GetFullPath($Project)
    $projectDirectory = Split-Path -Parent $script:ResolvedProjectPath
    $runId = Get-Date -Format 'yyyyMMdd-HHmmss'
    $script:RunDirectory = Join-Path $projectDirectory "Saved\UnifiedScript\Onboarding\$runId"
    $script:ReportPath = if ([string]::IsNullOrWhiteSpace($Report)) {
        Join-Path $script:RunDirectory 'onboarding-report.json'
    } else {
        Resolve-FullPath -Path $Report -BaseDirectory (Get-Location).Path
    }
    New-Item -ItemType Directory -Force -Path $script:RunDirectory | Out-Null

    $sdkRoot = Split-Path -Parent $PSScriptRoot
    $repositoryCliProject = Join-Path $sdkRoot 'src\UnifiedScript.Cli\UnifiedScript.Cli.csproj'
    $repositoryCliAssembly =
        Join-Path $sdkRoot 'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll'
    $packagedCliAssembly = Join-Path $sdkRoot 'Tools\UnifiedScript.Cli\UnifiedScript.Cli.dll'
    $packagedManagedRuntime = Join-Path $sdkRoot 'Managed\Runtime'
    $packagedSdkManifest = Join-Path $sdkRoot 'sdk-manifest.json'
    $embeddedManagedRuntime = Join-Path $sdkRoot 'Binaries\Managed'
    $embeddedPluginDescriptor = Join-Path $sdkRoot 'UEUnifiedScript.uplugin'
    if (Test-Path -LiteralPath $repositoryCliProject -PathType Leaf) {
        $sdkLayout = 'repository'
        $cliProject = $repositoryCliProject
        $cliAssembly = $repositoryCliAssembly
    } elseif ((Test-Path -LiteralPath $packagedCliAssembly -PathType Leaf) -and
        (Test-Path -LiteralPath $packagedManagedRuntime -PathType Container)) {
        $sdkLayout = 'packaged'
        $cliProject = $null
        $cliAssembly = $packagedCliAssembly
    } elseif ((Test-Path -LiteralPath $packagedCliAssembly -PathType Leaf) -and
        (Test-Path -LiteralPath $embeddedManagedRuntime -PathType Container) -and
        (Test-Path -LiteralPath $embeddedPluginDescriptor -PathType Leaf)) {
        $sdkLayout = 'embedded'
        $cliProject = $null
        $cliAssembly = $packagedCliAssembly
        $packagedManagedRuntime = $embeddedManagedRuntime
    } else {
        Throw-OnboardingError `
            -Code 'USONB1101' `
            -Message (
                "UnifiedScript SDK layout is incomplete under '$sdkRoot'. Expected " +
                'a repository, packaged SDK, or adopted project-local CLI/runtime payload.')
    }

    $resolved = [ordered]@{}
    Invoke-OnboardingPhase -Name 'preflight' -Action {
        Assert-RequiredPath `
            -Path $script:ResolvedProjectPath `
            -Label 'Unreal project' `
            -Code 'USONB1101'
        if ([IO.Path]::GetExtension($script:ResolvedProjectPath) -ne '.uproject') {
            Throw-OnboardingError `
                -Code 'USONB1101' `
                -Message "Project must be a .uproject file: $($script:ResolvedProjectPath)"
        }

        $projectJson = Get-Content -LiteralPath $script:ResolvedProjectPath -Raw |
            ConvertFrom-Json
        $association = [string]$projectJson.EngineAssociation
        $script:ResolvedEngineRoot = Resolve-UnrealEngineRoot `
            -RequestedRoot $EngineRoot `
            -Association $association

        $script:ResolvedConfigPath = if ([string]::IsNullOrWhiteSpace($Config)) {
            Join-Path $projectDirectory 'Config\UnifiedScript.onboarding.json'
        } else {
            Resolve-FullPath -Path $Config -BaseDirectory (Get-Location).Path
        }
        Assert-RequiredPath `
            -Path $script:ResolvedConfigPath `
            -Label 'UnifiedScript onboarding config' `
            -Code 'USONB1001'
        $configDirectory = Split-Path -Parent $script:ResolvedConfigPath
        $configJson = Get-Content -LiteralPath $script:ResolvedConfigPath -Raw |
            ConvertFrom-Json
        Assert-AllowedProperties `
            -Value $configJson `
            -Allowed @(
                'schemaVersion', 'editorTarget', 'platform', 'configuration',
                'pluginPath', 'reflection', 'bindings', 'deployment', 'managed',
                'validation') `
            -Context 'Onboarding config'
        if ($configJson.schemaVersion -ne 'unifiedscript.unreal-project-onboarding/v0.1') {
            Throw-OnboardingError `
                -Code 'USONB1001' `
                -Message "Unsupported onboarding schema '$($configJson.schemaVersion)'."
        }
        foreach ($requiredProperty in @(
            'editorTarget', 'reflection', 'bindings', 'deployment', 'managed')) {
            if ($null -eq $configJson.$requiredProperty) {
                Throw-OnboardingError `
                    -Code 'USONB1001' `
                    -Message "Onboarding config requires '$requiredProperty'."
            }
        }
        Assert-AllowedProperties `
            -Value $configJson.reflection `
            -Allowed @('modules', 'classes', 'output') `
            -Context 'reflection'
        Assert-AllowedProperties `
            -Value $configJson.bindings `
            -Allowed @(
                'contract', 'managedOutput', 'nativeOutput', 'nodeCatalogOutput',
                'browserOutput', 'coverageOutput', 'failOnRejected',
                'requireCompleteNativeCoverage') `
            -Context 'bindings'
        Assert-AllowedProperties `
            -Value $configJson.deployment `
            -Allowed @('manifest', 'enabled') `
            -Context 'deployment'
        Assert-AllowedProperties `
            -Value $configJson.managed `
            -Allowed @('configuration', 'stageDirectory', 'projects', 'chunks') `
            -Context 'managed'
        foreach ($chunk in @($configJson.managed.chunks)) {
            Assert-AllowedProperties `
                -Value $chunk `
                -Allowed @('project', 'stagePath') `
                -Context 'managed.chunks entry'
        }
        if ($null -ne $configJson.validation) {
            Assert-AllowedProperties `
                -Value $configJson.validation `
                -Allowed @('automationFilter', 'minimumAutomationTests') `
                -Context 'validation'
        }

        $resolved.editorTarget = [string]$configJson.editorTarget
        $resolved.platform = if ([string]::IsNullOrWhiteSpace($configJson.platform)) {
            'Win64'
        } else { [string]$configJson.platform }
        $resolved.configuration = if ([string]::IsNullOrWhiteSpace($configJson.configuration)) {
            'Development'
        } else { [string]$configJson.configuration }
        $resolved.plugin = Resolve-FullPath `
            -Path $(if ([string]::IsNullOrWhiteSpace($configJson.pluginPath)) {
                '..\Plugins\UEUnifiedScript'
            } else { [string]$configJson.pluginPath }) `
            -BaseDirectory $configDirectory
        $resolved.modules = @(
            if ($null -ne $Modules -and $Modules.Count -gt 0) {
                $Modules
            } else {
                @($configJson.reflection.modules)
            }
        )
        $resolved.classes = @(
            if ($null -ne $Classes -and $Classes.Count -gt 0) {
                $Classes
            } else {
                @($configJson.reflection.classes)
            }
        )
        $resolved.reflection = Resolve-FullPath `
            -Path ([string]$configJson.reflection.output) `
            -BaseDirectory $configDirectory
        $resolved.contract = Resolve-FullPath `
            -Path ([string]$configJson.bindings.contract) `
            -BaseDirectory $configDirectory
        $resolved.managedOutput = Resolve-FullPath `
            -Path ([string]$configJson.bindings.managedOutput) `
            -BaseDirectory $configDirectory
        $resolved.nativeOutput = Resolve-FullPath `
            -Path ([string]$configJson.bindings.nativeOutput) `
            -BaseDirectory $configDirectory
        $resolved.nodeCatalogOutput = Resolve-FullPath `
            -Path ([string]$configJson.bindings.nodeCatalogOutput) `
            -BaseDirectory $configDirectory
        $resolved.browserOutput = if (
            [string]::IsNullOrWhiteSpace($configJson.bindings.browserOutput)) {
            $nodeCatalogDirectory = Split-Path -Parent $resolved.nodeCatalogOutput
            $nodeCatalogName = [IO.Path]::GetFileNameWithoutExtension(
                $resolved.nodeCatalogOutput)
            $browserName = ($nodeCatalogName -replace '\.api-nodes$', '') +
                '.api-browser.json'
            Join-Path $nodeCatalogDirectory $browserName
        } else {
            Resolve-FullPath `
                -Path ([string]$configJson.bindings.browserOutput) `
                -BaseDirectory $configDirectory
        }
        $resolved.coverageOutput = Resolve-FullPath `
            -Path ([string]$configJson.bindings.coverageOutput) `
            -BaseDirectory $configDirectory
        $resolved.failOnRejected = $configJson.bindings.failOnRejected -ne $false
        $resolved.requireCompleteNativeCoverage =
            $configJson.bindings.requireCompleteNativeCoverage -eq $true
        $resolved.deploymentEnabled = $configJson.deployment.enabled -ne $false
        $resolved.deployment = Resolve-FullPath `
            -Path ([string]$configJson.deployment.manifest) `
            -BaseDirectory $configDirectory
        $resolved.managedConfiguration = if (
            [string]::IsNullOrWhiteSpace($configJson.managed.configuration)) {
            'Release'
        } else { [string]$configJson.managed.configuration }
        $resolved.managedStage = Resolve-FullPath `
            -Path $(if ([string]::IsNullOrWhiteSpace($configJson.managed.stageDirectory)) {
                '..\Plugins\UEUnifiedScript\Binaries\Managed'
            } else { [string]$configJson.managed.stageDirectory }) `
            -BaseDirectory $configDirectory
        $resolved.managedProjects = @(
            @($configJson.managed.projects) | ForEach-Object {
                Resolve-FullPath -Path ([string]$_) -BaseDirectory $configDirectory
            }
        )
        $resolved.managedChunks = @(
            @($configJson.managed.chunks) | ForEach-Object {
                [pscustomobject]@{
                    project = Resolve-FullPath `
                        -Path ([string]$_.project) `
                        -BaseDirectory $configDirectory
                    stagePath = Resolve-FullPath `
                        -Path ([string]$_.stagePath) `
                        -BaseDirectory $configDirectory
                }
            }
        )
        $resolved.automationFilter = if ($null -eq $configJson.validation) {
            $null
        } else { [string]$configJson.validation.automationFilter }
        $resolved.minimumAutomationTests = if (
            $null -eq $configJson.validation -or
            $null -eq $configJson.validation.minimumAutomationTests) {
            0
        } else { [int]$configJson.validation.minimumAutomationTests }

        if ([string]::IsNullOrWhiteSpace($resolved.editorTarget)) {
            Throw-OnboardingError -Code 'USONB1001' -Message 'editorTarget is required.'
        }
        if ($resolved.modules.Count -eq 0 -or
            @($resolved.modules | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
            Throw-OnboardingError `
                -Code 'USONB1001' `
                -Message 'reflection.modules must contain non-empty module names.'
        }
        foreach ($classPath in $resolved.classes) {
            if (-not ([string]$classPath).StartsWith('/Script/')) {
                Throw-OnboardingError `
                    -Code 'USONB1001' `
                    -Message "Reflection class path must start with /Script/: $classPath"
            }
        }

        Assert-RequiredPath `
            -Path (Join-Path $resolved.plugin 'UEUnifiedScript.uplugin') `
            -Label 'UEUnifiedScript plugin descriptor' `
            -Code 'USONB1104'
        Assert-RequiredPath `
            -Path (Join-Path $resolved.plugin 'Source\UEUnifiedScriptRuntime') `
            -Label 'UEUnifiedScript runtime module' `
            -Code 'USONB1104'
        Assert-RequiredPath `
            -Path (Join-Path $resolved.plugin 'Source\UEUnifiedScriptEditor') `
            -Label 'UEUnifiedScript editor module' `
            -Code 'USONB1104'
        $enabledPlugin = @($projectJson.Plugins) |
            Where-Object { $_.Name -eq 'UEUnifiedScript' -and $_.Enabled -eq $true }
        if ($enabledPlugin.Count -eq 0) {
            Throw-OnboardingError `
                -Code 'USONB1105' `
                -Message 'The .uproject must explicitly enable the UEUnifiedScript plugin.'
        }
        $targetFile = Get-ChildItem `
            -LiteralPath (Join-Path $projectDirectory 'Source') `
            -Recurse `
            -Filter "$($resolved.editorTarget).Target.cs" `
            -File `
            -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -eq $targetFile -and $resolved.editorTarget -ne 'UnrealEditor') {
            Throw-OnboardingError `
                -Code 'USONB1108' `
                -Message "Editor target source was not found: $($resolved.editorTarget).Target.cs"
        }

        $requiredInputs = @($resolved.contract, $resolved.deployment) +
            $resolved.managedProjects + @($resolved.managedChunks.project)
        if ($sdkLayout -eq 'repository') {
            $requiredInputs = @($cliProject) + $requiredInputs
        } elseif ($sdkLayout -eq 'packaged') {
            $requiredInputs = @(
                $cliAssembly,
                $packagedManagedRuntime,
                $packagedSdkManifest) + $requiredInputs
        } else {
            $requiredInputs = @($cliAssembly, $packagedManagedRuntime) + $requiredInputs
        }
        foreach ($required in $requiredInputs) {
            Assert-RequiredPath -Path $required -Label 'Onboarding input'
        }
        foreach ($destination in @(
            $resolved.reflection,
            $resolved.managedOutput,
            $resolved.nativeOutput,
            $resolved.nodeCatalogOutput,
            $resolved.browserOutput,
            $resolved.coverageOutput,
            $resolved.managedStage)) {
            Assert-WritableDestination -Path $destination
        }
        $managedStageBoundary = [IO.Path]::GetFullPath(
            $resolved.managedStage).TrimEnd('\', '/') +
            [IO.Path]::DirectorySeparatorChar
        foreach ($chunk in $resolved.managedChunks) {
            if ($chunk.project -notin $resolved.managedProjects) {
                Throw-OnboardingError `
                    -Code 'USONB1001' `
                    -Message "Managed chunk project is not present in managed.projects: $($chunk.project)"
            }
            $chunkStagePath = [IO.Path]::GetFullPath($chunk.stagePath)
            if (-not $chunkStagePath.StartsWith(
                    $managedStageBoundary,
                    [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetExtension($chunkStagePath) -ne '.uschunk') {
                Throw-OnboardingError `
                    -Code 'USONB1001' `
                    -Message (
                        "Managed chunk stagePath must be a .uschunk below " +
                        "managed.stageDirectory: $chunkStagePath")
            }
            Assert-WritableDestination -Path $chunkStagePath
        }

        $engineVersion = Get-Content `
            -LiteralPath (Join-Path $script:ResolvedEngineRoot 'Engine\Build\Build.version') `
            -Raw | ConvertFrom-Json
        $resolved.engineVersion =
            "$($engineVersion.MajorVersion).$($engineVersion.MinorVersion).$($engineVersion.PatchVersion)"
        if ($sdkLayout -eq 'packaged') {
            $sdkManifest = Get-Content -LiteralPath $packagedSdkManifest -Raw |
                ConvertFrom-Json
            if ($sdkManifest.schemaVersion -ne 'unifiedscript.sdk-manifest/v0.1') {
                Throw-OnboardingError `
                    -Code 'USONB1110' `
                    -Message "Unsupported SDK manifest '$($sdkManifest.schemaVersion)'."
            }
            $engineFamily = "$($engineVersion.MajorVersion).$($engineVersion.MinorVersion)"
            if ($engineFamily -notin @($sdkManifest.compatibility.engineVersions)) {
                Throw-OnboardingError `
                    -Code 'USONB1110' `
                    -Message (
                        "SDK '$($sdkManifest.sdkVersion)' does not support Unreal " +
                        "$($resolved.engineVersion). Supported families: " +
                        "$(@($sdkManifest.compatibility.engineVersions) -join ', ').")
            }
            if ($sdkManifest.compatibility.managedTargetFramework -ne 'net10.0' -or
                [int]$sdkManifest.compatibility.abi.callFrame -ne 3 -or
                [int]$sdkManifest.compatibility.abi.event -ne 4 -or
                [int]$sdkManifest.compatibility.abi.sharedData -ne 4 -or
                [int]$sdkManifest.compatibility.abi.gameplayData -ne 4) {
                Throw-OnboardingError `
                    -Code 'USONB1110' `
                    -Message 'The packaged SDK managed runtime or ABI contract is incompatible.'
            }
        } elseif ($sdkLayout -eq 'embedded') {
            $engineFamily = "$($engineVersion.MajorVersion).$($engineVersion.MinorVersion)"
            if ($engineFamily -ne '5.8') {
                Throw-OnboardingError `
                    -Code 'USONB1110' `
                    -Message (
                        "The adopted project-local SDK supports Unreal 5.8, not " +
                        "$($resolved.engineVersion).")
            }
        }
        $resolved.dotnet = Join-Path $script:ResolvedEngineRoot `
            'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
        $resolved.build = Join-Path $script:ResolvedEngineRoot `
            'Engine\Build\BatchFiles\Build.bat'
        $resolved.editorCmd = Join-Path $script:ResolvedEngineRoot `
            'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
        Assert-RequiredPath -Path $resolved.dotnet -Label 'Engine-bundled .NET 10' -Code 'USONB1109'

        if ($ValidateOnly) {
            foreach ($required in @(
                $cliAssembly,
                $resolved.reflection,
                $resolved.managedOutput,
                $resolved.nativeOutput,
                $resolved.nodeCatalogOutput,
                $resolved.browserOutput,
                $resolved.coverageOutput)) {
                Assert-RequiredPath `
                    -Path $required `
                    -Label 'ValidateOnly prerequisite' `
                    -Code 'USONB1201'
            }
        }
    }

    if ($ValidateOnly -and $sdkLayout -eq 'repository') {
        Invoke-OnboardingPhase -Name 'managed-toolchain-validation' -Action {
            Invoke-LoggedCommand `
                -FilePath $resolved.dotnet `
                -Arguments @(
                    'build', $cliProject,
                    '-c', $resolved.managedConfiguration,
                    '--nologo') `
                -LogPath (Join-Path $script:RunDirectory 'managed-toolchain-validation.log') `
                -FailureCode 'USONB2101' `
                -FailureMessage 'Current repository CLI build failed before validation.'
            Assert-RequiredPath `
                -Path $cliAssembly `
                -Label 'Current repository CLI assembly' `
                -Code 'USONB2101'
        }
    }

    if (-not $ValidateOnly) {
        Invoke-OnboardingPhase -Name 'managed-toolchain' -Action {
            New-Item -ItemType Directory -Force -Path $resolved.managedStage | Out-Null
            if ($sdkLayout -eq 'repository') {
                $log = Join-Path $script:RunDirectory 'managed-toolchain.log'
                Invoke-LoggedCommand `
                    -FilePath $resolved.dotnet `
                    -Arguments @(
                        'build', $cliProject,
                        '-c', $resolved.managedConfiguration,
                        '--nologo',
                        "-p:UnifiedScriptManagedStageDir=$($resolved.managedStage)") `
                    -LogPath $log `
                    -FailureCode 'USONB2101' `
                    -FailureMessage 'UnifiedScript managed toolchain build failed.'
            } elseif ($sdkLayout -eq 'packaged') {
                foreach ($runtimeFile in Get-ChildItem `
                    -LiteralPath $packagedManagedRuntime -Recurse -File) {
                    $relativePath = Get-RelativeFilePath `
                        -BaseDirectory $packagedManagedRuntime `
                        -Path $runtimeFile.FullName
                    $destination = Join-Path $resolved.managedStage $relativePath
                    New-Item -ItemType Directory -Force `
                        -Path (Split-Path -Parent $destination) | Out-Null
                    Copy-Item -LiteralPath $runtimeFile.FullName -Destination $destination -Force
                }
                Write-Host (
                    "Installed packaged managed runtime into '$($resolved.managedStage)'.")
            } else {
                Write-Host (
                    "Using adopted project-local managed runtime in '$packagedManagedRuntime'.")
            }
            Assert-RequiredPath -Path $cliAssembly -Label 'UnifiedScript CLI assembly' -Code 'USONB2101'
        }

        if (Test-Path -LiteralPath $resolved.reflection) {
            Invoke-OnboardingPhase -Name 'bootstrap-bindings' -Action {
                Invoke-BindingGeneration `
                    -DotNet $resolved.dotnet `
                    -CliAssembly $cliAssembly `
                    -ReflectionManifest $resolved.reflection `
                    -Contract $resolved.contract `
                    -ManagedOutput $resolved.managedOutput `
                    -NativeOutput $resolved.nativeOutput `
                    -NodeCatalogOutput $resolved.nodeCatalogOutput `
                    -BrowserOutput $resolved.browserOutput `
                    -CoverageOutput $resolved.coverageOutput `
                    -FailOnRejected $resolved.failOnRejected `
                    -LogPath (Join-Path $script:RunDirectory 'bootstrap-bindings.log')
            }
        }

        Invoke-OnboardingPhase -Name 'native-bootstrap-build' -Action {
            Invoke-LoggedCommand `
                -FilePath $resolved.build `
                -Arguments @(
                    $resolved.editorTarget,
                    $resolved.platform,
                    $resolved.configuration,
                    "-Project=$($script:ResolvedProjectPath)",
                    '-WaitMutex',
                    '-NoHotReloadFromIDE') `
                -LogPath (Join-Path $script:RunDirectory 'native-bootstrap-build.log') `
                -FailureCode 'USONB2201' `
                -FailureMessage 'Initial Unreal editor build failed.'
        }

        Invoke-OnboardingPhase -Name 'reflection-export' -Action {
            New-Item -ItemType Directory -Force -Path `
                (Split-Path -Parent $resolved.reflection) | Out-Null
            $arguments = @(
                $script:ResolvedProjectPath,
                '-run=UnifiedScriptExport',
                "-Output=$($resolved.reflection)",
                "-Modules=$($resolved.modules -join ',')",
                '-unattended',
                '-nop4',
                '-nosplash',
                '-NullRHI'
            )
            if ($resolved.classes.Count -gt 0) {
                $arguments += "-Classes=$($resolved.classes -join ',')"
            }
            Invoke-LoggedCommand `
                -FilePath $resolved.editorCmd `
                -Arguments $arguments `
                -LogPath (Join-Path $script:RunDirectory 'reflection-export.log') `
                -FailureCode 'USONB2202' `
                -FailureMessage 'Unreal reflection export failed.'
            Assert-RequiredPath `
                -Path $resolved.reflection `
                -Label 'Reflection manifest' `
                -Code 'USONB2202'
        }

        Invoke-OnboardingPhase -Name 'binding-generation' -Action {
            Invoke-BindingGeneration `
                -DotNet $resolved.dotnet `
                -CliAssembly $cliAssembly `
                -ReflectionManifest $resolved.reflection `
                -Contract $resolved.contract `
                -ManagedOutput $resolved.managedOutput `
                -NativeOutput $resolved.nativeOutput `
                -NodeCatalogOutput $resolved.nodeCatalogOutput `
                -BrowserOutput $resolved.browserOutput `
                -CoverageOutput $resolved.coverageOutput `
                -FailOnRejected $resolved.failOnRejected `
                -LogPath (Join-Path $script:RunDirectory 'binding-generation.log')
        }

        Invoke-OnboardingPhase -Name 'managed-gameplay-build' -Action {
            foreach ($managedProject in $resolved.managedProjects) {
                $safeName = [IO.Path]::GetFileNameWithoutExtension($managedProject)
                Invoke-LoggedCommand `
                    -FilePath $resolved.dotnet `
                    -Arguments @(
                        'build', $managedProject,
                        '-c', $resolved.managedConfiguration,
                        '--nologo',
                        "-p:UnifiedScriptManagedStageDir=$($resolved.managedStage)") `
                    -LogPath (Join-Path $script:RunDirectory "managed-$safeName.log") `
                    -FailureCode 'USONB2301' `
                    -FailureMessage "Managed gameplay build failed: $managedProject"
            }
        }

        if ($resolved.managedChunks.Count -gt 0) {
            Invoke-OnboardingPhase -Name 'compiled-chunks' -Action {
                for ($index = 0; $index -lt $resolved.managedChunks.Count; ++$index) {
                    $chunk = $resolved.managedChunks[$index]
                    $safeName = [IO.Path]::GetFileNameWithoutExtension($chunk.stagePath)
                    $outputDirectory = Join-Path `
                        $script:RunDirectory `
                        ("compiled-chunks\{0:D2}-{1}" -f ($index + 1), $safeName)
                    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
                    Invoke-LoggedCommand `
                        -FilePath $resolved.dotnet `
                        -Arguments @(
                            $cliAssembly,
                            'emit', $chunk.project,
                            '--out', $outputDirectory,
                            '--configuration', $resolved.managedConfiguration,
                            '--no-restore',
                            '--no-build') `
                        -LogPath (Join-Path $script:RunDirectory "chunk-emit-$safeName.log") `
                        -FailureCode 'USONB2303' `
                        -FailureMessage "Managed chunk emission failed: $($chunk.project)"
                    $compiledChunk = Join-Path `
                        $outputDirectory `
                        ([IO.Path]::GetFileName($chunk.stagePath))
                    Assert-RequiredPath `
                        -Path $compiledChunk `
                        -Label 'Compiled managed chunk' `
                        -Code 'USONB2303'
                    Invoke-LoggedCommand `
                        -FilePath $resolved.dotnet `
                        -Arguments @($cliAssembly, 'chunk-inspect', $compiledChunk) `
                        -LogPath (Join-Path $script:RunDirectory "chunk-inspect-$safeName.log") `
                        -FailureCode 'USONB2303' `
                        -FailureMessage "Managed chunk verification failed: $compiledChunk"

                    $stageDirectory = Split-Path -Parent $chunk.stagePath
                    New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
                    $temporaryStagePath = Join-Path `
                        $stageDirectory `
                        (".{0}.{1}.tmp" -f
                            ([IO.Path]::GetFileName($chunk.stagePath)),
                            [Guid]::NewGuid().ToString('N'))
                    try {
                        Copy-Item `
                            -LiteralPath $compiledChunk `
                            -Destination $temporaryStagePath `
                            -Force
                        Move-Item `
                            -LiteralPath $temporaryStagePath `
                            -Destination $chunk.stagePath `
                            -Force
                    } finally {
                        if (Test-Path -LiteralPath $temporaryStagePath) {
                            Remove-Item -LiteralPath $temporaryStagePath -Force
                        }
                    }
                }
            }
        }

        Invoke-OnboardingPhase -Name 'native-final-build' -Action {
            Invoke-LoggedCommand `
                -FilePath $resolved.build `
                -Arguments @(
                    $resolved.editorTarget,
                    $resolved.platform,
                    $resolved.configuration,
                    "-Project=$($script:ResolvedProjectPath)",
                    '-WaitMutex',
                    '-NoHotReloadFromIDE') `
                -LogPath (Join-Path $script:RunDirectory 'native-final-build.log') `
                -FailureCode 'USONB2302' `
                -FailureMessage 'Final Unreal editor build failed.'
        }
    }

    Invoke-OnboardingPhase -Name 'deployment' -Action {
        if (-not $resolved.deploymentEnabled) {
            Write-Host 'Deployment synchronization is disabled by project policy.'
            return
        }
        $arguments = @(
            $script:ResolvedProjectPath,
            '-run=UnifiedScriptSyncAssets',
            "-Input=$($resolved.deployment)",
            '-unattended',
            '-nop4',
            '-nosplash',
            '-NullRHI'
        )
        if ($ValidateOnly) {
            $arguments += '-ValidateOnly'
        }
        $log = Join-Path $script:RunDirectory 'deployment.log'
        Invoke-LoggedCommand `
            -FilePath $resolved.editorCmd `
            -Arguments $arguments `
            -LogPath $log `
            -FailureCode 'USONB2401' `
            -FailureMessage 'Script Asset deployment failed.'
        $deploymentText = Get-Content -LiteralPath $log -Raw
        $expectedText = if ($ValidateOnly) {
            'UnifiedScript deployment validated:'
        } else {
            'UnifiedScript deployment synchronized:'
        }
        if (-not $deploymentText.Contains($expectedText)) {
            Throw-OnboardingError `
                -Code 'USONB2402' `
                -Message "Deployment command did not publish '$expectedText'. Log=$log"
        }
    }

    Invoke-OnboardingPhase -Name 'determinism' -Action {
        $tempDirectory = Join-Path $script:RunDirectory 'generated-validation'
        $tempManaged = Join-Path $tempDirectory 'bindings.cs'
        $tempNative = Join-Path $tempDirectory 'native-thunks.cpp'
        $tempNodes = Join-Path $tempDirectory 'api-nodes.json'
        $tempBrowser = Join-Path $tempDirectory 'api-browser.json'
        $tempCoverage = Join-Path $tempDirectory 'binding-coverage.json'
        Invoke-BindingGeneration `
            -DotNet $resolved.dotnet `
            -CliAssembly $cliAssembly `
            -ReflectionManifest $resolved.reflection `
            -Contract $resolved.contract `
            -ManagedOutput $tempManaged `
            -NativeOutput $tempNative `
            -NodeCatalogOutput $tempNodes `
            -BrowserOutput $tempBrowser `
            -CoverageOutput $tempCoverage `
            -FailOnRejected $resolved.failOnRejected `
            -LogPath (Join-Path $script:RunDirectory 'determinism.log')
        Assert-GeneratedFileMatches -Expected $resolved.managedOutput -Actual $tempManaged
        Assert-GeneratedFileMatches -Expected $resolved.nativeOutput -Actual $tempNative
        Assert-GeneratedFileMatches -Expected $resolved.nodeCatalogOutput -Actual $tempNodes
        Assert-GeneratedFileMatches -Expected $resolved.browserOutput -Actual $tempBrowser
        Assert-GeneratedFileMatches -Expected $resolved.coverageOutput -Actual $tempCoverage

        $coverage = Get-Content -LiteralPath $resolved.coverageOutput -Raw |
            ConvertFrom-Json
        if ($resolved.requireCompleteNativeCoverage -and
            (($coverage.summary.nativeThunkCount +
               $coverage.summary.reflectionFallbackCount +
               $coverage.summary.dataPlaneOperationCount) -ne $coverage.summary.memberCount -or
             $coverage.summary.rejectedCount -ne 0 -or
             $coverage.summary.deferredTypeCount -ne 0 -or
             $coverage.gate.passed -ne $true)) {
            Throw-OnboardingError `
                -Code 'USONB2501' `
                -Message "Generated native coverage is incomplete: $($resolved.coverageOutput)"
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($resolved.automationFilter)) {
        Invoke-OnboardingPhase -Name 'managed-startup' -Action {
            $defaultConfigPaths = @(
                (Join-Path $projectDirectory 'Config\DefaultEngine.ini'),
                (Join-Path $projectDirectory 'Config\DefaultInput.ini'))
            $snapshots = Get-FileSnapshot -Paths $defaultConfigPaths
            $automationLog = Join-Path $script:RunDirectory 'automation.log'
            $automationStdout = Join-Path $script:RunDirectory 'automation-stdout.log'
            try {
                Invoke-LoggedCommand `
                    -FilePath $resolved.editorCmd `
                    -Arguments @(
                        $script:ResolvedProjectPath,
                        '-unattended',
                        '-nop4',
                        '-NullRHI',
                        '-nosplash',
                        "-ExecCmds=Automation RunTests $($resolved.automationFilter)",
                        '-TestExit=Automation Test Queue Empty',
                        "-abslog=$automationLog",
                        '-log') `
                    -LogPath $automationStdout `
                    -FailureCode 'USONB2601' `
                    -FailureMessage 'Managed startup Automation failed.'
            } finally {
                Restore-FileSnapshot -Snapshots $snapshots
            }
            $automationText = @(
                if (Test-Path -LiteralPath $automationLog) {
                    Get-Content -LiteralPath $automationLog -Raw
                }
                if (Test-Path -LiteralPath $automationStdout) {
                    Get-Content -LiteralPath $automationStdout -Raw
                }) -join [Environment]::NewLine
            $successCount = [regex]::Matches(
                $automationText,
                'Test Completed\. Result=\{Success\}').Count
            $failureCount = [regex]::Matches(
                $automationText,
                'Test Completed\. Result=\{(Fail|Failed|Error)\}').Count
            $script:AutomationSuccessCount = $successCount
            $script:AutomationFailureCount = $failureCount
            if ($failureCount -ne 0 -or
                $successCount -lt $resolved.minimumAutomationTests) {
                Throw-OnboardingError `
                    -Code 'USONB2602' `
                    -Message (
                        "Automation acceptance failed: Success=$successCount, " +
                        "Failure=$failureCount, Required=$($resolved.minimumAutomationTests). " +
                        "Log=$automationLog")
            }
        }
    }

    $outputContext = [ordered]@{
        sdkLayout = $sdkLayout
        sdkRoot = $sdkRoot
        engineVersion = $resolved.engineVersion
        editorTarget = $resolved.editorTarget
        reflectionManifest = $resolved.reflection
        managedBindings = $resolved.managedOutput
        nativeThunks = $resolved.nativeOutput
        apiNodeCatalog = $resolved.nodeCatalogOutput
        apiBrowser = $resolved.browserOutput
        bindingCoverage = $resolved.coverageOutput
        deploymentManifest = $resolved.deployment
        managedStageDirectory = $resolved.managedStage
        compiledChunks = @($resolved.managedChunks.stagePath)
        runDirectory = $script:RunDirectory
        automation = [ordered]@{
            filter = $resolved.automationFilter
            required = $resolved.minimumAutomationTests
            passed = $script:AutomationSuccessCount
            failed = $script:AutomationFailureCount
        }
    }
    Write-OnboardingReport -Status 'passed' -Context $outputContext
    Write-Host "UnifiedScript project onboarding: PASS ($($script:ReportPath))"
} catch {
    $code = Get-OnboardingErrorCode -Exception $_.Exception
    Write-OnboardingReport `
        -Status 'failed' `
        -ErrorCode $code `
        -ErrorMessage $_.Exception.Message `
        -Context ([ordered]@{ runDirectory = $script:RunDirectory })
    Write-Error "UnifiedScript project onboarding failed [$code] during '$($script:CurrentPhase)': $($_.Exception.Message)"
    exit 1
}
