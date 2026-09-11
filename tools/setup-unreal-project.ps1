[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Project,

    [string]$EngineRoot = $env:FINARE_UE_ROOT,

    [string]$Config,

    [string[]]$Modules,

    [string[]]$Classes,

    [string]$Report,

    [switch]$ValidateOnly,

    [switch]$EditorAttached
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$moduleSearchPaths = @(
    $env:PSModulePath,
    [Environment]::GetEnvironmentVariable('PSModulePath', 'Machine'),
    [Environment]::GetEnvironmentVariable('PSModulePath', 'User')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$env:PSModulePath = ($moduleSearchPaths -join ';')
Import-Module Microsoft.PowerShell.Utility -ErrorAction Stop
if ($null -eq (Get-Command Get-FileHash -ErrorAction SilentlyContinue)) {
    function global:Get-FileHash {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][string]$LiteralPath,
            [ValidateSet('SHA256')][string]$Algorithm = 'SHA256'
        )

        $stream = [IO.File]::OpenRead($LiteralPath)
        try {
            $sha256 = [Security.Cryptography.SHA256]::Create()
            try {
                $hash = [BitConverter]::ToString(
                    $sha256.ComputeHash($stream)).Replace('-', '')
            } finally {
                $sha256.Dispose()
            }
        } finally {
            $stream.Dispose()
        }
        [pscustomobject]@{
            Algorithm = $Algorithm
            Hash = $hash
            Path = $LiteralPath
        }
    }
}
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
$script:AutomationCriticalFailureCount = 0
$script:BuildGraphContext = $null
$script:BuildGraphReport = $null
$script:RestartRequiredStage = $null
$script:RestartRequiredReason = $null

function Get-FinareAutomationEvidence([string]$Text) {
    # UE writes the same completion to its engine log and redirected stdout.
    # Count test identities once; a later success must not erase an earlier fail.
    $passed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $failed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($result in [regex]::Matches($Text,
            'Test Completed\. Result=\{(?<result>[^}]+)\} Name=\{[^}]*\} Path=\{(?<path>[^}]+)\}')) {
        if ($result.Groups['result'].Value -eq 'Success') {
            [void]$passed.Add($result.Groups['path'].Value)
        } else {
            [void]$failed.Add($result.Groups['path'].Value)
        }
    }
    [pscustomobject]@{
        passed = $passed.Count
        failed = $failed.Count
        criticalFailures = [regex]::Matches($Text,
            '(?im)Ensure condition failed:|Fatal error:|Assertion failed:|Old World.*not cleaned up').Count
    }
}

function Throw-OnboardingError {
    param(
        [Parameter(Mandatory)]
        [string]$Code,

        [Parameter(Mandatory)]
        [string]$Message
    )

    $exception = [InvalidOperationException]::new($Message)
    $exception.Data['FinareCode'] = $Code
    throw $exception
}

function Get-OnboardingErrorCode {
    param([Exception]$Exception)

    if ($null -ne $Exception -and
        $null -ne $Exception.Data -and
        $Exception.Data.Contains('FinareCode')) {
        return [string]$Exception.Data['FinareCode']
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

function Invoke-DependencyAwareOnboardingPhase {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Collections.IDictionary]$Values = [ordered]@{},
        [Collections.IDictionary]$PathInputs = [ordered]@{},
        [string[]]$Dependencies = @(),
        [string[]]$OutputPaths = @(),
        [ValidateSet('run', 'restart', 'defer')]
        [string]$EditorAttachedPolicy = 'run',
        [string[]]$EditorAttachedRestartInputs = @(),
        [Parameter(Mandatory)][scriptblock]$Action
    )

    if ($null -eq $script:BuildGraphContext) {
        Invoke-OnboardingPhase -Name $Name -Action $Action
        return
    }

    $decision = Get-FinareBuildGraphStageDecision `
        -Context $script:BuildGraphContext `
        -Name $Name `
        -Values $Values `
        -PathInputs $PathInputs `
        -Dependencies $Dependencies `
        -OutputPaths $OutputPaths
    $script:CurrentPhase = $Name
    if (-not $decision.ShouldRun) {
        Write-Host "[$Name] SKIP - $($decision.Reason)"
        Complete-FinareBuildGraphStage `
            -Context $script:BuildGraphContext `
            -Decision $decision `
            -Executed $false | Out-Null
        $script:PhaseRecords.Add([ordered]@{
            name = $Name
            status = 'skipped'
            durationMs = 0
            reason = $decision.Reason
            buildGraph = [ordered]@{
                changedInputs = @($decision.ChangedInputs)
                changedDependencies = @($decision.ChangedDependencies)
            }
        })
        return
    }

    if ($EditorAttached -and $EditorAttachedPolicy -ne 'run') {
        $restartInputs = @($decision.ChangedInputs | Where-Object {
            $_ -in $EditorAttachedRestartInputs
        })
        $requiresRestart = $EditorAttachedPolicy -eq 'restart' -or
            $restartInputs.Count -gt 0 -or
            -not $script:BuildGraphContext.PreviousStages.ContainsKey($Name)
        if ($requiresRestart) {
            $script:CurrentPhase = $Name
            $script:RestartRequiredStage = $Name
            $script:RestartRequiredReason = if ($restartInputs.Count -gt 0) {
                "live Editor inputs changed: $($restartInputs -join ', ')"
            } elseif (-not $script:BuildGraphContext.PreviousStages.ContainsKey($Name)) {
                'no prior successful stage is available for live reuse'
            } else {
                $decision.Reason
            }
            $script:PhaseRecords.Add([ordered]@{
                name = $Name
                status = 'restartRequired'
                durationMs = 0
                reason = $script:RestartRequiredReason
                buildGraph = [ordered]@{
                    changedInputs = @($decision.ChangedInputs)
                    changedDependencies = @($decision.ChangedDependencies)
                }
            })
            Throw-OnboardingError `
                -Code 'USONB3001' `
                -Message (
                    "Daily Editor build requires restart before '$Name': " +
                    $script:RestartRequiredReason)
        }

        $deferredReason =
            "deferred while the Editor is attached; prior published outputs remain active"
        Write-Host "[$Name] DEFER - $deferredReason"
        Defer-FinareBuildGraphStage `
            -Context $script:BuildGraphContext `
            -Decision $decision `
            -Reason $deferredReason | Out-Null
        $script:PhaseRecords.Add([ordered]@{
            name = $Name
            status = 'deferred'
            durationMs = 0
            reason = $deferredReason
            buildGraph = [ordered]@{
                changedInputs = @($decision.ChangedInputs)
                changedDependencies = @($decision.ChangedDependencies)
            }
        })
        return
    }

    $startedAt = [DateTime]::UtcNow
    Write-Host "[$Name] RUN - $($decision.Reason)"
    try {
        & $Action
        $duration = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
        Complete-FinareBuildGraphStage `
            -Context $script:BuildGraphContext `
            -Decision $decision `
            -Executed $true `
            -DurationMs $duration | Out-Null
        $script:PhaseRecords.Add([ordered]@{
            name = $Name
            status = 'passed'
            durationMs = $duration
            reason = $decision.Reason
            buildGraph = [ordered]@{
                changedInputs = @($decision.ChangedInputs)
                changedDependencies = @($decision.ChangedDependencies)
            }
        })
    } catch {
        $script:PhaseRecords.Add([ordered]@{
            name = $Name
            status = 'failed'
            durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
            code = Get-OnboardingErrorCode -Exception $_.Exception
            message = $_.Exception.Message
            reason = $decision.Reason
        })
        throw
    }
}

function Get-OnboardingSourceFiles {
    param(
        [Parameter(Mandatory)][string[]]$Roots,
        [Parameter(Mandatory)][string[]]$Extensions,
        [string[]]$ExcludedDirectories = @(
            '.git', '.vs', 'bin', 'obj', 'Binaries', 'DerivedDataCache',
            'Intermediate', 'Saved')
    )

    $files = [Collections.Generic.List[string]]::new()
    foreach ($root in $Roots) {
        if ([string]::IsNullOrWhiteSpace($root)) {
            continue
        }
        if (Test-Path -LiteralPath $root -PathType Leaf) {
            $files.Add([IO.Path]::GetFullPath($root))
            continue
        }
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            continue
        }
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
            $relative = Get-RelativeFilePath `
                -BaseDirectory $root `
                -Path $file.FullName
            $segments = $relative -split '[\\/]'
            if (@($segments | Where-Object { $_ -in $ExcludedDirectories }).Count -gt 0) {
                continue
            }
            if ($Extensions.Count -gt 0 -and
                [IO.Path]::GetExtension($file.Name) -notin $Extensions) {
                continue
            }
            $files.Add($file.FullName)
        }
    }
    return @($files | Sort-Object -Unique)
}

function Get-OnboardingDeploymentOutputs {
    param(
        [Parameter(Mandatory)][string]$Manifest,
        [Parameter(Mandatory)][string]$ProjectDirectory
    )

    if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
        return @()
    }
    $document = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
    $outputs = [Collections.Generic.List[string]]::new()
    foreach ($entry in @($document.scripts)) {
        $assetPath = [string]$entry.assetPath
        if ($assetPath.StartsWith('/Game/', [StringComparison]::OrdinalIgnoreCase)) {
            $relative = $assetPath.Substring('/Game/'.Length).Replace('/', '\')
            $outputs.Add((Join-Path $ProjectDirectory "Content\$relative.uasset"))
            continue
        }
        $match = [regex]::Match($assetPath, '^/([^/]+)/(.+)$')
        if ($match.Success) {
            $pluginName = $match.Groups[1].Value
            $relative = $match.Groups[2].Value.Replace('/', '\')
            $outputs.Add((Join-Path $ProjectDirectory `
                "Plugins\$pluginName\Content\$relative.uasset"))
        }
    }
    return @($outputs | Sort-Object -Unique)
}

function Get-OnboardingDeploymentManagedOutputs {
    param(
        [Parameter(Mandatory)][string]$Manifest,
        [Parameter(Mandatory)][string]$ProjectDirectory,
        [Parameter(Mandatory)][string]$PluginDirectory
    )

    if (-not (Test-Path -LiteralPath $Manifest -PathType Leaf)) {
        return @()
    }
    $document = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
    $outputs = [Collections.Generic.List[string]]::new()
    foreach ($entry in @($document.scripts)) {
        $assemblyPath = [string]$entry.assemblyPath
        if ([string]::IsNullOrWhiteSpace($assemblyPath)) {
            continue
        }
        $resolvedPath = $assemblyPath.
            Replace('{ProjectDir}', $ProjectDirectory).
            Replace('{PluginDir}', $PluginDirectory).
            Replace('/', '\')
        if (-not [IO.Path]::IsPathRooted($resolvedPath)) {
            $resolvedPath = Join-Path $ProjectDirectory $resolvedPath
        }
        $outputs.Add([IO.Path]::GetFullPath($resolvedPath))
    }
    return @($outputs | Sort-Object -Unique)
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
                "FINARE_UE_ROOT, or register EngineAssociation '$Association'.")
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

function Invoke-DevelopmentSurfaceExpansion {
    param(
        [Parameter(Mandatory)][string]$DotNet,
        [Parameter(Mandatory)][string]$CliAssembly,
        [Parameter(Mandatory)][string]$ReflectionManifest,
        [Parameter(Mandatory)][string]$BaseContract,
        [Parameter(Mandatory)][string[]]$ManagedProjects,
        [Parameter(Mandatory)][string]$ManagedConfiguration,
        [Parameter(Mandatory)][string]$RequestsOutput,
        [Parameter(Mandatory)][string]$OverlayOutput,
        [Parameter(Mandatory)][string]$ReportOutput,
        [string]$SurfaceLockOutput,
        [string]$VerifySurfaceLock,
        [Parameter(Mandatory)][bool]$FailOnOpenDecisions,
        [Parameter(Mandatory)][string]$LogPath
    )

    foreach ($output in @(
            $RequestsOutput, $OverlayOutput, $ReportOutput, $SurfaceLockOutput) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
    }
    $arguments = @(
        $CliAssembly,
        'ue-surface-expand',
        $ReflectionManifest
    )
    foreach ($managedProject in $ManagedProjects) {
        $arguments += @('--project', $managedProject)
    }
    $arguments += @(
        '--base', $BaseContract,
        '--requests-out', $RequestsOutput,
        '--overlay-out', $OverlayOutput,
        '--report', $ReportOutput,
        '--configuration', $ManagedConfiguration,
        '--dotnet', $DotNet
    )
    if (-not [string]::IsNullOrWhiteSpace($SurfaceLockOutput)) {
        $arguments += @('--lock-out', $SurfaceLockOutput)
    }
    if (-not [string]::IsNullOrWhiteSpace($VerifySurfaceLock)) {
        $arguments += @('--verify-lock', $VerifySurfaceLock)
    }
    if ($FailOnOpenDecisions) {
        $arguments += '--fail-on-open-decisions'
    }
    Invoke-LoggedCommand `
        -FilePath $DotNet `
        -Arguments $arguments `
        -LogPath $LogPath `
        -FailureCode 'USONB2005' `
        -FailureMessage 'Automatic Unreal API development-surface expansion failed.'
}

function Invoke-ReflectionHintScan {
    param(
        [Parameter(Mandatory)][string]$DotNet,
        [Parameter(Mandatory)][string]$CliAssembly,
        [Parameter(Mandatory)][string]$BaseContract,
        [Parameter(Mandatory)][string[]]$ManagedProjects,
        [Parameter(Mandatory)][string]$ManagedConfiguration,
        [Parameter(Mandatory)][string]$Output,
        [Parameter(Mandatory)][string]$LogPath
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Output) | Out-Null
    $arguments = @($CliAssembly, 'ue-reflection-hints')
    foreach ($managedProject in $ManagedProjects) {
        $arguments += @('--project', $managedProject)
    }
    $arguments += @(
        '--base', $BaseContract,
        '--out', $Output,
        '--configuration', $ManagedConfiguration,
        '--dotnet', $DotNet
    )
    Invoke-LoggedCommand `
        -FilePath $DotNet `
        -Arguments $arguments `
        -LogPath $LogPath `
        -FailureCode 'USONB2006' `
        -FailureMessage 'Managed source reflection-hint scan failed.'
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
        mode = if ($EditorAttached) {
            'dailyEditor'
        } elseif ($ValidateOnly) {
            'validateOnly'
        } else {
            'apply'
        }
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
    $script:RunDirectory = Join-Path $projectDirectory "Saved\Finare\Onboarding\$runId"
    $script:ReportPath = if ([string]::IsNullOrWhiteSpace($Report)) {
        Join-Path $script:RunDirectory 'onboarding-report.json'
    } else {
        Resolve-FullPath -Path $Report -BaseDirectory (Get-Location).Path
    }
    New-Item -ItemType Directory -Force -Path $script:RunDirectory | Out-Null

    $buildGraphModulePath = Join-Path $PSScriptRoot 'Finare.BuildGraph.psm1'
    Assert-RequiredPath `
        -Path $buildGraphModulePath `
        -Label 'Finare dependency-aware Build/Play graph' `
        -Code 'USONB1101'
    Import-Module -Name $buildGraphModulePath -Force

    $sdkRoot = Split-Path -Parent $PSScriptRoot
    $repositoryCliProject = Join-Path $sdkRoot 'src\Finare.Cli\Finare.Cli.csproj'
    $repositoryCliAssembly =
        Join-Path $sdkRoot 'src\Finare.Cli\bin\Release\net10.0\Finare.Cli.dll'
    $packagedCliAssembly = Join-Path $sdkRoot 'Tools\Finare.Cli\Finare.Cli.dll'
    $packagedManagedRuntime = Join-Path $sdkRoot 'Managed\Runtime'
    $packagedSdkManifest = Join-Path $sdkRoot 'sdk-manifest.json'
    $embeddedManagedRuntime = Join-Path $sdkRoot 'Binaries\Managed'
    $embeddedPluginDescriptor = Join-Path $sdkRoot 'Finare.uplugin'
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
                "Finare SDK layout is incomplete under '$sdkRoot'. Expected " +
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
            Join-Path $projectDirectory 'Config\Finare.onboarding.json'
        } else {
            Resolve-FullPath -Path $Config -BaseDirectory (Get-Location).Path
        }
        Assert-RequiredPath `
            -Path $script:ResolvedConfigPath `
            -Label 'Finare onboarding config' `
            -Code 'USONB1001'
        $configDirectory = Split-Path -Parent $script:ResolvedConfigPath
        $configJson = Get-Content -LiteralPath $script:ResolvedConfigPath -Raw |
            ConvertFrom-Json
        Assert-AllowedProperties `
            -Value $configJson `
            -Allowed @(
                'schemaVersion', 'editorTarget', 'gameTarget', 'platform', 'configuration',
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
            -Allowed @('modules', 'classes', 'blueprintAssets', 'output') `
            -Context 'reflection'
        Assert-AllowedProperties `
            -Value $configJson.bindings `
             -Allowed @(
                 'contract', 'managedOutput', 'nativeOutput', 'nodeCatalogOutput',
                 'browserOutput', 'coverageOutput', 'failOnRejected',
                 'requireCompleteNativeCoverage', 'developmentSurface') `
             -Context 'bindings'
        if ($null -ne $configJson.bindings.developmentSurface) {
            Assert-AllowedProperties `
                -Value $configJson.bindings.developmentSurface `
                -Allowed @(
                    'enabled', 'typeHintsOutput', 'requestsOutput', 'contractOutput',
                    'reportOutput', 'lockOutput', 'lockMode',
                    'failOnOpenDecisions') `
                -Context 'bindings.developmentSurface'
        }
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
        $resolved.gameTarget = if ([string]::IsNullOrWhiteSpace($configJson.gameTarget)) {
            'UnrealGame'
        } else { [string]$configJson.gameTarget }
        $resolved.platform = if ([string]::IsNullOrWhiteSpace($configJson.platform)) {
            'Win64'
        } else { [string]$configJson.platform }
        $resolved.configuration = if ([string]::IsNullOrWhiteSpace($configJson.configuration)) {
            'Development'
        } else { [string]$configJson.configuration }
        $resolved.plugin = Resolve-FullPath `
            -Path $(if ([string]::IsNullOrWhiteSpace($configJson.pluginPath)) {
                '..\Plugins\Finare'
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
        $resolved.blueprintAssets = @($configJson.reflection.blueprintAssets)
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
        $resolved.developmentSurfaceEnabled =
            $null -ne $configJson.bindings.developmentSurface -and
            $configJson.bindings.developmentSurface.enabled -ne $false
        $resolved.developmentSurfaceFailOnOpen =
            $resolved.developmentSurfaceEnabled -and
            $configJson.bindings.developmentSurface.failOnOpenDecisions -ne $false
        if ($resolved.developmentSurfaceEnabled) {
            foreach ($requiredProperty in @(
                'requestsOutput', 'contractOutput', 'reportOutput')) {
                if ([string]::IsNullOrWhiteSpace(
                        $configJson.bindings.developmentSurface.$requiredProperty)) {
                    Throw-OnboardingError `
                        -Code 'USONB1001' `
                        -Message (
                            "bindings.developmentSurface requires '$requiredProperty' when enabled.")
                }
            }
            $resolved.developmentSurfaceRequests = Resolve-FullPath `
                -Path ([string]$configJson.bindings.developmentSurface.requestsOutput) `
                -BaseDirectory $configDirectory
            $resolved.developmentSurfaceTypeHints = if ([string]::IsNullOrWhiteSpace(
                    $configJson.bindings.developmentSurface.typeHintsOutput)) {
                $requestFileName = [IO.Path]::GetFileName(
                    $resolved.developmentSurfaceRequests)
                $hintFileName = if ($requestFileName.EndsWith(
                        '.api-requests.json', [StringComparison]::OrdinalIgnoreCase)) {
                    $requestFileName.Substring(
                        0,
                        $requestFileName.Length - '.api-requests.json'.Length) +
                        '.api-type-hints.json'
                } else {
                    $requestFileName + '.type-hints.json'
                }
                Join-Path `
                    (Split-Path -Parent $resolved.developmentSurfaceRequests) `
                    $hintFileName
            } else {
                Resolve-FullPath `
                    -Path ([string]$configJson.bindings.developmentSurface.typeHintsOutput) `
                    -BaseDirectory $configDirectory
            }
            $resolved.developmentSurfaceContract = Resolve-FullPath `
                -Path ([string]$configJson.bindings.developmentSurface.contractOutput) `
                -BaseDirectory $configDirectory
            $resolved.developmentSurfaceReport = Resolve-FullPath `
                -Path ([string]$configJson.bindings.developmentSurface.reportOutput) `
                -BaseDirectory $configDirectory
            $resolved.developmentSurfaceLock = if ([string]::IsNullOrWhiteSpace(
                    $configJson.bindings.developmentSurface.lockOutput)) {
                $reportFileName = [IO.Path]::GetFileName(
                    $resolved.developmentSurfaceReport)
                $lockFileName = if ($reportFileName.EndsWith(
                        '.development-surface.json', [StringComparison]::OrdinalIgnoreCase)) {
                    $reportFileName.Substring(
                        0,
                        $reportFileName.Length - '.development-surface.json'.Length) +
                        '.generated-surface.lock.json'
                } else {
                    $reportFileName + '.lock.json'
                }
                Join-Path `
                    (Split-Path -Parent $resolved.developmentSurfaceReport) `
                    $lockFileName
            } else {
                Resolve-FullPath `
                    -Path ([string]$configJson.bindings.developmentSurface.lockOutput) `
                    -BaseDirectory $configDirectory
            }
            $resolved.developmentSurfaceLockMode = if ([string]::IsNullOrWhiteSpace(
                    $configJson.bindings.developmentSurface.lockMode)) {
                'update'
            } else {
                ([string]$configJson.bindings.developmentSurface.lockMode).ToLowerInvariant()
            }
            if ($resolved.developmentSurfaceLockMode -notin @('update', 'frozen')) {
                Throw-OnboardingError `
                    -Code 'USONB1001' `
                    -Message "bindings.developmentSurface.lockMode must be 'update' or 'frozen'."
            }
        } else {
            $resolved.developmentSurfaceTypeHints = $null
            $resolved.developmentSurfaceRequests = $null
            $resolved.developmentSurfaceContract = $null
            $resolved.developmentSurfaceReport = $null
            $resolved.developmentSurfaceLock = $null
            $resolved.developmentSurfaceLockMode = $null
        }
        $resolved.effectiveContract = if (
            $resolved.developmentSurfaceEnabled -and
            (Test-Path -LiteralPath $resolved.developmentSurfaceContract -PathType Leaf)) {
            $resolved.developmentSurfaceContract
        } else {
            $resolved.contract
        }
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
                '..\Plugins\Finare\Binaries\Managed'
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
        if ([string]::IsNullOrWhiteSpace($resolved.gameTarget)) {
            Throw-OnboardingError -Code 'USONB1001' -Message 'gameTarget is required.'
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
            -Path (Join-Path $resolved.plugin 'Finare.uplugin') `
            -Label 'Finare plugin descriptor' `
            -Code 'USONB1104'
        Assert-RequiredPath `
            -Path (Join-Path $resolved.plugin 'Source\FinareRuntime') `
            -Label 'Finare runtime module' `
            -Code 'USONB1104'
        Assert-RequiredPath `
            -Path (Join-Path $resolved.plugin 'Source\FinareEditor') `
            -Label 'Finare editor module' `
            -Code 'USONB1104'
        $enabledPlugin = @($projectJson.Plugins) |
            Where-Object { $_.Name -eq 'Finare' -and $_.Enabled -eq $true }
        if ($enabledPlugin.Count -eq 0) {
            Throw-OnboardingError `
                -Code 'USONB1105' `
                -Message 'The .uproject must explicitly enable the Finare plugin.'
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
        $gameTargetFile = Get-ChildItem `
            -LiteralPath (Join-Path $projectDirectory 'Source') `
            -Recurse `
            -Filter "$($resolved.gameTarget).Target.cs" `
            -File `
            -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -eq $gameTargetFile -and $resolved.gameTarget -ne 'UnrealGame') {
            Throw-OnboardingError `
                -Code 'USONB1108' `
                -Message "Game target source was not found: $($resolved.gameTarget).Target.cs"
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
        if ($resolved.developmentSurfaceEnabled -and
            $resolved.developmentSurfaceLockMode -eq 'frozen') {
            Assert-RequiredPath `
                -Path $resolved.developmentSurfaceLock `
                -Label 'Frozen generated surface lock'
        }
        foreach ($destination in @(
             $resolved.reflection,
             $resolved.managedOutput,
             $resolved.nativeOutput,
             $resolved.nodeCatalogOutput,
             $resolved.browserOutput,
             $resolved.coverageOutput,
             $resolved.managedStage,
             $resolved.developmentSurfaceTypeHints,
             $resolved.developmentSurfaceRequests,
             $resolved.developmentSurfaceContract,
             $resolved.developmentSurfaceReport,
             $resolved.developmentSurfaceLock) |
             Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
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
             $validatePrerequisites = @(
                 $cliAssembly,
                 $resolved.reflection,
                 $resolved.managedOutput,
                 $resolved.nativeOutput,
                 $resolved.nodeCatalogOutput,
                 $resolved.browserOutput,
                 $resolved.coverageOutput)
             foreach ($chunk in $resolved.managedChunks) {
                 $validatePrerequisites += @(
                     $chunk.stagePath,
                     [IO.Path]::ChangeExtension($chunk.stagePath, '.asset.json'),
                     [IO.Path]::ChangeExtension($chunk.stagePath, '.surface.json'))
             }
             if ($resolved.developmentSurfaceEnabled) {
                 $validatePrerequisites += @(
                     $resolved.developmentSurfaceTypeHints,
                     $resolved.developmentSurfaceRequests,
                     $resolved.developmentSurfaceContract,
                     $resolved.developmentSurfaceReport,
                     $resolved.developmentSurfaceLock)
             }
             foreach ($required in $validatePrerequisites) {
                 Assert-RequiredPath `
                    -Path $required `
                    -Label 'ValidateOnly prerequisite' `
                    -Code 'USONB1201'
            }
        }
    }

    if (-not $ValidateOnly) {
        $buildGraphStatePath = Join-Path $projectDirectory `
            'Saved\Finare\BuildGraph\onboarding-state.json'
        $script:BuildGraphContext = New-FinareBuildGraphContext `
            -StatePath $buildGraphStatePath `
            -Project $script:ResolvedProjectPath `
            -Config $script:ResolvedConfigPath `
            -EngineVersion $resolved.engineVersion
        Write-Host (
            "[build-graph] Cache=$($script:BuildGraphContext.CacheStatus); " +
            "State=$buildGraphStatePath")
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
        $toolchainInputs = if ($sdkLayout -eq 'repository') {
            @(
                Get-OnboardingSourceFiles `
                    -Roots @((Join-Path $sdkRoot 'src')) `
                    -Extensions @('.cs', '.csproj', '.props', '.targets')
            ) + @(
                (Join-Path $sdkRoot 'global.json'),
                (Join-Path $sdkRoot 'Directory.Build.props'))
        } elseif ($sdkLayout -eq 'packaged') {
            @($cliAssembly, $packagedManagedRuntime, $packagedSdkManifest)
        } else {
            @($cliAssembly) + @(
                Get-ChildItem `
                    -LiteralPath $packagedManagedRuntime `
                    -File `
                    -ErrorAction SilentlyContinue | Where-Object {
                        $_.Extension -ne '.uschunk'
                    } | ForEach-Object { $_.FullName }
            )
        }
        $toolchainOutputs = @(
            $cliAssembly,
            (Join-Path $resolved.managedStage 'Finare.Core.dll'),
            (Join-Path $resolved.managedStage 'Finare.Unreal.dll'),
            (Join-Path $resolved.managedStage 'Finare.Unreal.deps.json'))
        Invoke-DependencyAwareOnboardingPhase `
            -Name 'managed-toolchain' `
            -Values ([ordered]@{
                sdkLayout = $sdkLayout
                configuration = $resolved.managedConfiguration
            }) `
            -PathInputs ([ordered]@{ toolchain = $toolchainInputs }) `
            -OutputPaths $toolchainOutputs `
            -EditorAttachedPolicy 'restart' `
            -Action {
            New-Item -ItemType Directory -Force -Path $resolved.managedStage | Out-Null
            if ($sdkLayout -eq 'repository') {
                $log = Join-Path $script:RunDirectory 'managed-toolchain.log'
                Invoke-LoggedCommand `
                    -FilePath $resolved.dotnet `
                    -Arguments @(
                        'build', $cliProject,
                        '-c', $resolved.managedConfiguration,
                        '--nologo',
                        "-p:FinareManagedStageDir=$($resolved.managedStage)") `
                    -LogPath $log `
                    -FailureCode 'USONB2101' `
                    -FailureMessage 'Finare managed toolchain build failed.'
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
            Assert-RequiredPath -Path $cliAssembly -Label 'Finare CLI assembly' -Code 'USONB2101'
        }

        $managedSourceRoots = @($resolved.managedProjects | ForEach-Object {
            Split-Path -Parent $_
        })
        $managedSourceInputs = @(Get-OnboardingSourceFiles `
            -Roots $managedSourceRoots `
            -Extensions @('.cs', '.csproj', '.json', '.props', '.targets', '.usgraph') |
            Where-Object {
                [IO.Path]::GetFullPath($_) -ne
                    [IO.Path]::GetFullPath($resolved.managedOutput)
            })
        $nativeSourceInputs = Get-OnboardingSourceFiles `
            -Roots @(
                (Join-Path $resolved.plugin 'Source'),
                (Join-Path $projectDirectory 'Source')) `
            -Extensions @('.Build.cs', '.Target.cs', '.cpp', '.cs', '.h', '.hpp', '.inl')
        $nativeBootstrapInputs = @($nativeSourceInputs | Where-Object {
            [IO.Path]::GetFullPath($_) -ne [IO.Path]::GetFullPath($resolved.nativeOutput)
        })
        $managedRuntimeInputs = @(Get-ChildItem `
            -LiteralPath $resolved.managedStage `
            -File `
            -ErrorAction SilentlyContinue | Where-Object {
                $_.Extension -ne '.uschunk'
            } | ForEach-Object { $_.FullName })
        $nativeConfigurationInputs = @(
            $script:ResolvedProjectPath,
            $script:ResolvedConfigPath,
            (Join-Path $resolved.plugin 'Finare.uplugin'),
            (Join-Path $script:ResolvedEngineRoot 'Engine\Build\Build.version'))
        $bindingOutputs = @(
            $resolved.managedOutput,
            $resolved.nativeOutput,
            $resolved.nodeCatalogOutput,
            $resolved.browserOutput,
            $resolved.coverageOutput)
        $managedBuildOutputs = Get-OnboardingDeploymentManagedOutputs `
            -Manifest $resolved.deployment `
            -ProjectDirectory $projectDirectory `
            -PluginDirectory $resolved.plugin
        if ($managedBuildOutputs.Count -eq 0) {
            $managedBuildOutputs = @($resolved.managedProjects | ForEach-Object {
                Join-Path (Split-Path -Parent $_) `
                    "bin\$($resolved.managedConfiguration)"
            })
        }
        $managedChunkOutputs = @($resolved.managedChunks | ForEach-Object {
            $_.stagePath
            [IO.Path]::ChangeExtension($_.stagePath, '.asset.json')
            [IO.Path]::ChangeExtension($_.stagePath, '.surface.json')
        })
        $editorTargetReceipt = Join-Path $projectDirectory `
            "Binaries\$($resolved.platform)\$($resolved.editorTarget).target"
        $gameTargetReceipt = Join-Path $projectDirectory `
            "Binaries\$($resolved.platform)\$($resolved.gameTarget).target"
        $deploymentOutputs = if ($resolved.deploymentEnabled) {
            Get-OnboardingDeploymentOutputs `
                -Manifest $resolved.deployment `
                -ProjectDirectory $projectDirectory
        } else { @() }

        if ((Test-Path -LiteralPath $resolved.reflection) -and
            -not (Test-Path -LiteralPath $resolved.nativeOutput)) {
            Invoke-OnboardingPhase -Name 'bootstrap-bindings' -Action {
                Invoke-BindingGeneration `
                 -DotNet $resolved.dotnet `
                 -CliAssembly $cliAssembly `
                 -ReflectionManifest $resolved.reflection `
                 -Contract $resolved.effectiveContract `
                    -ManagedOutput $resolved.managedOutput `
                    -NativeOutput $resolved.nativeOutput `
                    -NodeCatalogOutput $resolved.nodeCatalogOutput `
                    -BrowserOutput $resolved.browserOutput `
                    -CoverageOutput $resolved.coverageOutput `
                    -FailOnRejected $resolved.failOnRejected `
                    -LogPath (Join-Path $script:RunDirectory 'bootstrap-bindings.log')
            }
        } elseif (Test-Path -LiteralPath $resolved.reflection) {
            Write-Host '[bootstrap-bindings] SKIP - generated native bootstrap source exists'
            $script:PhaseRecords.Add([ordered]@{
                name = 'bootstrap-bindings'
                status = 'skipped'
                durationMs = 0
                reason = 'generated native bootstrap source exists'
            })
        }

        Invoke-DependencyAwareOnboardingPhase `
            -Name 'native-bootstrap-build' `
            -Values ([ordered]@{
                target = $resolved.editorTarget
                platform = $resolved.platform
                configuration = $resolved.configuration
            }) `
            -PathInputs ([ordered]@{
                nativeSources = $nativeBootstrapInputs
                nativeConfiguration = $nativeConfigurationInputs
            }) `
            -EditorAttachedPolicy 'restart' `
            -Action {
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

        if ($resolved.developmentSurfaceEnabled) {
            Invoke-DependencyAwareOnboardingPhase `
                -Name 'source-reflection-hints' `
                -Values ([ordered]@{
                    configuration = $resolved.managedConfiguration
                }) `
                -PathInputs ([ordered]@{
                    managedSources = $managedSourceInputs
                    baseContract = @($resolved.contract)
                    generator = @($cliAssembly)
                }) `
                -OutputPaths @($resolved.developmentSurfaceTypeHints) `
                -Action {
                Invoke-ReflectionHintScan `
                    -DotNet $resolved.dotnet `
                    -CliAssembly $cliAssembly `
                    -BaseContract $resolved.contract `
                    -ManagedProjects $resolved.managedProjects `
                    -ManagedConfiguration $resolved.managedConfiguration `
                    -Output $resolved.developmentSurfaceTypeHints `
                    -LogPath (Join-Path $script:RunDirectory 'source-reflection-hints.log')
            }
        }

        $reflectionDependencies = @('managed-toolchain', 'native-bootstrap-build')
        if ($resolved.developmentSurfaceEnabled) {
            $reflectionDependencies += 'source-reflection-hints'
        }
        Invoke-DependencyAwareOnboardingPhase `
            -Name 'reflection-export' `
            -Values ([ordered]@{
                modules = @($resolved.modules)
                classes = @($resolved.classes)
                blueprintAssets = @($resolved.blueprintAssets)
            }) `
            -PathInputs ([ordered]@{
                project = @($script:ResolvedProjectPath)
                sourceTypeHints = @($resolved.developmentSurfaceTypeHints)
            }) `
            -Dependencies $reflectionDependencies `
            -OutputPaths @($resolved.reflection) `
            -EditorAttachedPolicy 'restart' `
            -Action {
            New-Item -ItemType Directory -Force -Path `
                (Split-Path -Parent $resolved.reflection) | Out-Null
            $arguments = @(
                $script:ResolvedProjectPath,
                '-run=FinareExport',
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
            if ($resolved.developmentSurfaceEnabled) {
                $hintDocument = Get-Content `
                    -LiteralPath $resolved.developmentSurfaceTypeHints `
                    -Raw | ConvertFrom-Json
                if ($hintDocument.schemaVersion -ne
                        'unifiedscript.unreal-api-source-type-hints/v0.1') {
                    Throw-OnboardingError `
                        -Code 'USONB2006' `
                        -Message 'Managed source reflection hints have an unsupported schema.'
                }
                $typeNames = @($hintDocument.hints | ForEach-Object {
                    [string]$_.managedTypeName
                })
                if ($typeNames.Count -gt 0) {
                    $arguments += "-TypeNames=$($typeNames -join ',')"
                }
            }
            if ($resolved.blueprintAssets.Count -gt 0) {
                $arguments += "-BlueprintAssets=$($resolved.blueprintAssets -join ',')"
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

        if ($resolved.developmentSurfaceEnabled) {
            Invoke-DependencyAwareOnboardingPhase `
                -Name 'development-surface' `
                -Values ([ordered]@{
                    configuration = $resolved.managedConfiguration
                    lockMode = $resolved.developmentSurfaceLockMode
                    failOnOpenDecisions = $resolved.developmentSurfaceFailOnOpen
                }) `
                -PathInputs ([ordered]@{
                    reflection = @($resolved.reflection)
                    baseContract = @($resolved.contract)
                    managedSources = $managedSourceInputs
                    typeHints = @($resolved.developmentSurfaceTypeHints)
                    generator = @($cliAssembly)
                }) `
                -Dependencies @('reflection-export', 'source-reflection-hints') `
                -OutputPaths @(
                    $resolved.developmentSurfaceRequests,
                    $resolved.developmentSurfaceContract,
                    $resolved.developmentSurfaceReport,
                    $resolved.developmentSurfaceLock) `
                -Action {
                Invoke-DevelopmentSurfaceExpansion `
                    -DotNet $resolved.dotnet `
                    -CliAssembly $cliAssembly `
                    -ReflectionManifest $resolved.reflection `
                    -BaseContract $resolved.contract `
                    -ManagedProjects $resolved.managedProjects `
                    -ManagedConfiguration $resolved.managedConfiguration `
                    -RequestsOutput $resolved.developmentSurfaceRequests `
                    -OverlayOutput $resolved.developmentSurfaceContract `
                    -ReportOutput $resolved.developmentSurfaceReport `
                    -SurfaceLockOutput $resolved.developmentSurfaceLock `
                    -VerifySurfaceLock $(if (
                            $resolved.developmentSurfaceLockMode -eq 'frozen') {
                        $resolved.developmentSurfaceLock
                    } else { $null }) `
                    -FailOnOpenDecisions $resolved.developmentSurfaceFailOnOpen `
                    -LogPath (Join-Path $script:RunDirectory 'development-surface.log')
                Assert-RequiredPath `
                    -Path $resolved.developmentSurfaceContract `
                    -Label 'Automatic Unreal API development Contract' `
                    -Code 'USONB2005'
                $resolved.effectiveContract = $resolved.developmentSurfaceContract
            }
        }

        $bindingDependencies = @('reflection-export')
        if ($resolved.developmentSurfaceEnabled) {
            $bindingDependencies += 'development-surface'
        }
        Invoke-DependencyAwareOnboardingPhase `
            -Name 'binding-generation' `
            -Values ([ordered]@{
                failOnRejected = $resolved.failOnRejected
                requireCompleteNativeCoverage = $resolved.requireCompleteNativeCoverage
            }) `
            -PathInputs ([ordered]@{
                reflection = @($resolved.reflection)
                contract = @($resolved.effectiveContract)
                generator = @($cliAssembly)
            }) `
            -Dependencies $bindingDependencies `
            -OutputPaths $bindingOutputs `
            -Action {
            Invoke-BindingGeneration `
                -DotNet $resolved.dotnet `
                -CliAssembly $cliAssembly `
                -ReflectionManifest $resolved.reflection `
                 -Contract $resolved.effectiveContract `
                -ManagedOutput $resolved.managedOutput `
                -NativeOutput $resolved.nativeOutput `
                -NodeCatalogOutput $resolved.nodeCatalogOutput `
                -BrowserOutput $resolved.browserOutput `
                -CoverageOutput $resolved.coverageOutput `
                -FailOnRejected $resolved.failOnRejected `
                -LogPath (Join-Path $script:RunDirectory 'binding-generation.log')
        }

        Invoke-DependencyAwareOnboardingPhase `
            -Name 'managed-gameplay-build' `
            -Values ([ordered]@{
                configuration = $resolved.managedConfiguration
                projects = @($resolved.managedProjects)
            }) `
            -PathInputs ([ordered]@{
                managedSources = $managedSourceInputs
                generatedBindings = @($resolved.managedOutput)
                managedRuntime = $managedRuntimeInputs
            }) `
            -Dependencies @('managed-toolchain', 'binding-generation') `
            -OutputPaths $managedBuildOutputs `
            -Action {
            foreach ($managedProject in $resolved.managedProjects) {
                $safeName = [IO.Path]::GetFileNameWithoutExtension($managedProject)
                # The shared host was validated above; never copy over assemblies
                # loaded by UE while rebuilding only gameplay with the Editor open.
                Invoke-LoggedCommand `
                    -FilePath $resolved.dotnet `
                    -Arguments @(
                        'build', $managedProject,
                        '-c', $resolved.managedConfiguration,
                        '--nologo',
                        "-p:FinareSkipManagedHostStaging=$($EditorAttached.IsPresent)",
                        "-p:FinareManagedStageDir=$($resolved.managedStage)") `
                    -LogPath (Join-Path $script:RunDirectory "managed-$safeName.log") `
                    -FailureCode 'USONB2301' `
                    -FailureMessage "Managed gameplay build failed: $managedProject"
            }
        }

        if ($resolved.managedChunks.Count -gt 0) {
            Invoke-DependencyAwareOnboardingPhase `
                -Name 'compiled-chunks' `
                -Values ([ordered]@{
                    configuration = $resolved.managedConfiguration
                    projects = @($resolved.managedChunks.project)
                }) `
                -PathInputs ([ordered]@{
                    managedBuild = $managedBuildOutputs
                    generator = @($cliAssembly)
                }) `
                -Dependencies @('managed-gameplay-build') `
                -OutputPaths $managedChunkOutputs `
                -Action {
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
                    $compiledDeclaration = [IO.Path]::ChangeExtension(
                        $compiledChunk,
                        '.asset.json')
                    Assert-RequiredPath `
                        -Path $compiledDeclaration `
                        -Label 'Compiled managed Declaration IR' `
                        -Code 'USONB2303'
                    $compiledSurface = [IO.Path]::ChangeExtension(
                        $compiledChunk,
                        '.surface.json')
                    Assert-RequiredPath `
                        -Path $compiledSurface `
                        -Label 'Compiled managed authoring surface' `
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

                    # The Editor materializer deliberately consumes the readable
                    # Declaration IR sidecar while runtime execution consumes the
                    # verified chunk. Publish both products from the same emit so
                    # a dynamically created Script Class cannot observe the stale
                    # source-tree JSON staged by an earlier MSBuild target.
                    $declarationStagePath = [IO.Path]::ChangeExtension(
                        $chunk.stagePath,
                        '.asset.json')
                    $temporaryDeclarationStagePath = Join-Path `
                        $stageDirectory `
                        (".{0}.{1}.tmp" -f
                            ([IO.Path]::GetFileName($declarationStagePath)),
                            [Guid]::NewGuid().ToString('N'))
                    try {
                        Copy-Item `
                            -LiteralPath $compiledDeclaration `
                            -Destination $temporaryDeclarationStagePath `
                            -Force
                        Move-Item `
                            -LiteralPath $temporaryDeclarationStagePath `
                            -Destination $declarationStagePath `
                            -Force
                    } finally {
                        if (Test-Path -LiteralPath $temporaryDeclarationStagePath) {
                            Remove-Item `
                                -LiteralPath $temporaryDeclarationStagePath `
                                -Force
                        }
                    }

                    $surfaceStagePath = [IO.Path]::ChangeExtension(
                        $chunk.stagePath,
                        '.surface.json')
                    $temporarySurfaceStagePath = Join-Path `
                        $stageDirectory `
                        (".{0}.{1}.tmp" -f
                            ([IO.Path]::GetFileName($surfaceStagePath)),
                            [Guid]::NewGuid().ToString('N'))
                    try {
                        Copy-Item `
                            -LiteralPath $compiledSurface `
                            -Destination $temporarySurfaceStagePath `
                            -Force
                        Move-Item `
                            -LiteralPath $temporarySurfaceStagePath `
                            -Destination $surfaceStagePath `
                            -Force
                    } finally {
                        if (Test-Path -LiteralPath $temporarySurfaceStagePath) {
                            Remove-Item `
                                -LiteralPath $temporarySurfaceStagePath `
                                -Force
                        }
                    }
                }
            }
        }

        Invoke-DependencyAwareOnboardingPhase `
            -Name 'native-final-build' `
            -Values ([ordered]@{
                target = $resolved.editorTarget
                platform = $resolved.platform
                configuration = $resolved.configuration
            }) `
            -PathInputs ([ordered]@{
                nativeSources = $nativeSourceInputs
                nativeConfiguration = $nativeConfigurationInputs
            }) `
            -Dependencies @('native-bootstrap-build', 'binding-generation') `
            -OutputPaths @($editorTargetReceipt) `
            -EditorAttachedPolicy 'restart' `
            -Action {
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

        $nativeGameOutputs = if ($resolved.gameTarget -eq 'UnrealGame') {
            @()
        } else { @($gameTargetReceipt) }
        Invoke-DependencyAwareOnboardingPhase `
            -Name 'native-game-build' `
            -Values ([ordered]@{
                target = $resolved.gameTarget
                platform = $resolved.platform
                configuration = $resolved.configuration
            }) `
            -PathInputs ([ordered]@{
                nativeSources = $nativeSourceInputs
                nativeConfiguration = $nativeConfigurationInputs
            }) `
            -Dependencies @('native-final-build') `
            -OutputPaths $nativeGameOutputs `
            -EditorAttachedPolicy 'restart' `
            -Action {
            if ($resolved.gameTarget -eq 'UnrealGame') {
                $pluginDescriptor = Get-Content `
                    -LiteralPath (Join-Path $resolved.plugin 'Finare.uplugin') `
                    -Raw | ConvertFrom-Json
                if ($pluginDescriptor.Installed -eq $true) {
                    Throw-OnboardingError `
                        -Code 'USONB2304' `
                        -Message (
                            'Blueprint-only packaging requires the source plugin descriptor ' +
                            'to keep Installed=false so UAT can generate a temporary target.')
                }
                Write-Host (
                    'Blueprint-only game linking is deferred to UAT BuildCookRun temporary-target generation.')
                return
            }
            Invoke-LoggedCommand `
                -FilePath $resolved.build `
                -Arguments @(
                    $resolved.gameTarget,
                    $resolved.platform,
                    $resolved.configuration,
                    "-Project=$($script:ResolvedProjectPath)",
                    '-WaitMutex',
                    '-NoHotReloadFromIDE') `
                -LogPath (Join-Path $script:RunDirectory 'native-game-build.log') `
                -FailureCode 'USONB2304' `
                -FailureMessage 'Final Unreal game target build failed.'
            Assert-RequiredPath `
                -Path (Join-Path $projectDirectory `
                    "Binaries\$($resolved.platform)\$($resolved.gameTarget).target") `
                -Label 'Unreal game target receipt' `
                -Code 'USONB2304'
         }
     }

    if ($ValidateOnly -and $resolved.developmentSurfaceEnabled) {
        Invoke-OnboardingPhase -Name 'development-surface-validation' -Action {
            $surfaceValidationDirectory = Join-Path `
                $script:RunDirectory `
                'development-surface-validation'
            $tempTypeHints = Join-Path $surfaceValidationDirectory 'api-type-hints.json'
            $tempRequests = Join-Path $surfaceValidationDirectory 'api-requests.json'
            $tempContract = Join-Path $surfaceValidationDirectory 'development-contract.json'
            $tempReport = Join-Path $surfaceValidationDirectory 'development-surface.json'
            $tempLock = Join-Path $surfaceValidationDirectory 'generated-surface.lock.json'
            Invoke-ReflectionHintScan `
                -DotNet $resolved.dotnet `
                -CliAssembly $cliAssembly `
                -BaseContract $resolved.contract `
                -ManagedProjects $resolved.managedProjects `
                -ManagedConfiguration $resolved.managedConfiguration `
                -Output $tempTypeHints `
                -LogPath (Join-Path $script:RunDirectory `
                    'source-reflection-hints-validation.log')
            Invoke-DevelopmentSurfaceExpansion `
                -DotNet $resolved.dotnet `
                -CliAssembly $cliAssembly `
                -ReflectionManifest $resolved.reflection `
                -BaseContract $resolved.contract `
                -ManagedProjects $resolved.managedProjects `
                -ManagedConfiguration $resolved.managedConfiguration `
                -RequestsOutput $tempRequests `
                -OverlayOutput $tempContract `
                -ReportOutput $tempReport `
                -SurfaceLockOutput $tempLock `
                -VerifySurfaceLock $resolved.developmentSurfaceLock `
                -FailOnOpenDecisions $resolved.developmentSurfaceFailOnOpen `
                -LogPath (Join-Path $script:RunDirectory 'development-surface-validation.log')
            Assert-GeneratedFileMatches `
                -Expected $resolved.developmentSurfaceTypeHints `
                -Actual $tempTypeHints
            Assert-GeneratedFileMatches `
                -Expected $resolved.developmentSurfaceRequests `
                -Actual $tempRequests
            Assert-GeneratedFileMatches `
                -Expected $resolved.developmentSurfaceContract `
                -Actual $tempContract
            Assert-GeneratedFileMatches `
                -Expected $resolved.developmentSurfaceReport `
                -Actual $tempReport
            Assert-GeneratedFileMatches `
                -Expected $resolved.developmentSurfaceLock `
                -Actual $tempLock
            $resolved.effectiveContract = $resolved.developmentSurfaceContract
        }
    }

    $deploymentDependencies = @()
    if ($null -ne $script:BuildGraphContext) {
        $deploymentDependencies = @(
            'managed-gameplay-build',
            'native-final-build',
            'native-game-build')
        if ($resolved.managedChunks.Count -gt 0) {
            $deploymentDependencies += 'compiled-chunks'
        }
    }
    Invoke-DependencyAwareOnboardingPhase `
        -Name 'deployment' `
        -Values ([ordered]@{
            enabled = $resolved.deploymentEnabled
            validateOnly = [bool]$ValidateOnly
        }) `
        -PathInputs ([ordered]@{
            manifest = @($resolved.deployment)
            managedStage = @($resolved.managedStage)
        }) `
        -Dependencies $deploymentDependencies `
        -OutputPaths $(if ($null -ne $script:BuildGraphContext) {
            @($deploymentOutputs)
        } else { @() }) `
        -EditorAttachedPolicy 'defer' `
        -EditorAttachedRestartInputs @('enabled', 'validateOnly', 'manifest') `
        -Action {
        if (-not $resolved.deploymentEnabled) {
            Write-Host 'Deployment synchronization is disabled by project policy.'
            return
        }
        $arguments = @(
            $script:ResolvedProjectPath,
            '-run=FinareSyncAssets',
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
            'Finare deployment validated:'
        } else {
            'Finare deployment synchronized:'
        }
        if (-not $deploymentText.Contains($expectedText)) {
            Throw-OnboardingError `
                -Code 'USONB2402' `
                -Message "Deployment command did not publish '$expectedText'. Log=$log"
        }

        $classArguments = @(
            $script:ResolvedProjectPath,
            '-run=FinareSyncClasses',
            "-Input=$($resolved.deployment)",
            '-unattended',
            '-nop4',
            '-nosplash',
            '-NullRHI'
        )
        if ($ValidateOnly) {
            $classArguments += '-ValidateOnly'
        }
        $classLog = Join-Path $script:RunDirectory 'script-class-materialization.log'
        Invoke-LoggedCommand `
            -FilePath $resolved.editorCmd `
            -Arguments $classArguments `
            -LogPath $classLog `
            -FailureCode 'USONB2403' `
            -FailureMessage 'Script Class Blueprint materialization failed.'
        $classText = Get-Content -LiteralPath $classLog -Raw
        $expectedClassText = if ($ValidateOnly) {
            'Finare class proxies validated:'
        } else {
            'Finare class proxies synchronized:'
        }
        if (-not $classText.Contains($expectedClassText)) {
            Throw-OnboardingError `
                -Code 'USONB2404' `
                -Message (
                    "Script Class command did not publish '$expectedClassText'. " +
                    "Log=$classLog")
        }
    }

    $determinismDependencies = if ($null -ne $script:BuildGraphContext) {
        @('binding-generation')
    } else { @() }
    Invoke-DependencyAwareOnboardingPhase `
        -Name 'determinism' `
        -Values ([ordered]@{
            failOnRejected = $resolved.failOnRejected
            requireCompleteNativeCoverage = $resolved.requireCompleteNativeCoverage
        }) `
        -PathInputs ([ordered]@{
            reflection = @($resolved.reflection)
            contract = @($resolved.effectiveContract)
            generatedBindings = @(
                $resolved.managedOutput,
                $resolved.nativeOutput,
                $resolved.nodeCatalogOutput,
                $resolved.browserOutput,
                $resolved.coverageOutput)
            generator = @($cliAssembly)
        }) `
        -Dependencies $determinismDependencies `
        -Action {
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
             -Contract $resolved.effectiveContract `
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
        $startupDependencies = if ($null -ne $script:BuildGraphContext) {
            @('deployment', 'determinism', 'native-game-build')
        } else { @() }
        Invoke-DependencyAwareOnboardingPhase `
            -Name 'managed-startup' `
            -Values ([ordered]@{
                filter = $resolved.automationFilter
                minimumTests = $resolved.minimumAutomationTests
            }) `
            -PathInputs ([ordered]@{
                project = @($script:ResolvedProjectPath)
                editorReceipt = @($editorTargetReceipt)
                deployment = @($deploymentOutputs)
                chunks = @($resolved.managedChunks.stagePath)
                declarations = @($resolved.managedChunks | ForEach-Object {
                    [IO.Path]::ChangeExtension($_.stagePath, '.asset.json')
                })
                surfaces = @($resolved.managedChunks | ForEach-Object {
                    [IO.Path]::ChangeExtension($_.stagePath, '.surface.json')
                })
            }) `
            -Dependencies $startupDependencies `
            -EditorAttachedPolicy 'defer' `
            -Action {
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
            $automationEvidence = Get-FinareAutomationEvidence $automationText
            $successCount = $automationEvidence.passed
            $failureCount = $automationEvidence.failed
            $script:AutomationSuccessCount = $successCount
            $script:AutomationFailureCount = $failureCount
            $criticalFailureCount = $automationEvidence.criticalFailures
            $script:AutomationCriticalFailureCount = $criticalFailureCount
            if ($failureCount -ne 0 -or
                $criticalFailureCount -ne 0 -or
                $successCount -lt $resolved.minimumAutomationTests) {
                Throw-OnboardingError `
                    -Code 'USONB2602' `
                    -Message (
                        "Automation acceptance failed: Success=$successCount, " +
                        "Failure=$failureCount, Critical=$criticalFailureCount, Required=$($resolved.minimumAutomationTests). " +
                        "Log=$automationLog")
            }
        }
    }

    if ($null -ne $script:BuildGraphContext) {
        Save-FinareBuildGraphState -Context $script:BuildGraphContext
        $script:BuildGraphReport = Get-FinareBuildGraphReport `
            -Context $script:BuildGraphContext
    }

    $outputContext = [ordered]@{
        sdkLayout = $sdkLayout
        sdkRoot = $sdkRoot
        engineVersion = $resolved.engineVersion
        editorTarget = $resolved.editorTarget
        gameTarget = $resolved.gameTarget
        reflectionManifest = $resolved.reflection
        managedBindings = $resolved.managedOutput
        nativeThunks = $resolved.nativeOutput
        apiNodeCatalog = $resolved.nodeCatalogOutput
        apiBrowser = $resolved.browserOutput
         bindingCoverage = $resolved.coverageOutput
        developmentSurface = if ($resolved.developmentSurfaceEnabled) {
            [ordered]@{
                baseContract = $resolved.contract
                typeHints = $resolved.developmentSurfaceTypeHints
                requests = $resolved.developmentSurfaceRequests
                effectiveContract = $resolved.developmentSurfaceContract
                report = $resolved.developmentSurfaceReport
                lock = $resolved.developmentSurfaceLock
                lockMode = $resolved.developmentSurfaceLockMode
            }
        } else { $null }
        deploymentManifest = $resolved.deployment
        managedStageDirectory = $resolved.managedStage
        compiledChunks = @($resolved.managedChunks.stagePath)
        compiledDeclarations = @($resolved.managedChunks | ForEach-Object {
            [IO.Path]::ChangeExtension($_.stagePath, '.asset.json')
        })
        compiledSurfaces = @($resolved.managedChunks | ForEach-Object {
            [IO.Path]::ChangeExtension($_.stagePath, '.surface.json')
        })
        buildGraph = $script:BuildGraphReport
        runDirectory = $script:RunDirectory
        automation = [ordered]@{
            filter = $resolved.automationFilter
            required = $resolved.minimumAutomationTests
            passed = $script:AutomationSuccessCount
            failed = $script:AutomationFailureCount
            criticalFailures = $script:AutomationCriticalFailureCount
        }
    }
    Write-OnboardingReport -Status 'passed' -Context $outputContext
    Write-Host "Finare project onboarding: PASS ($($script:ReportPath))"
} catch {
    $code = Get-OnboardingErrorCode -Exception $_.Exception
    if ($code -eq 'USONB3001') {
        if ($null -ne $script:BuildGraphContext) {
            $script:BuildGraphReport = Get-FinareBuildGraphReport `
                -Context $script:BuildGraphContext
        }
        Write-OnboardingReport `
            -Status 'restartRequired' `
            -ErrorCode $code `
            -ErrorMessage $_.Exception.Message `
            -Context ([ordered]@{
                runDirectory = $script:RunDirectory
                buildGraph = $script:BuildGraphReport
                requiredAction = 'restartEditor'
                restartRequiredStage = $script:RestartRequiredStage
                restartReason = $script:RestartRequiredReason
            })
        Write-Warning (
            "Finare daily Editor build requires restart during " +
            "'$($script:RestartRequiredStage)': $($script:RestartRequiredReason)")
        exit 2
    }
    Write-OnboardingReport `
        -Status 'failed' `
        -ErrorCode $code `
        -ErrorMessage $_.Exception.Message `
        -Context ([ordered]@{ runDirectory = $script:RunDirectory })
    Write-Error "Finare project onboarding failed [$code] during '$($script:CurrentPhase)': $($_.Exception.Message)"
    exit 1
}
