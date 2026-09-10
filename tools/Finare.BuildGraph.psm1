Set-StrictMode -Version Latest

$script:BuildGraphSchemaVersion =
    'unifiedscript.unreal-build-graph-state/v0.1'

function Get-FinareBuildGraphTextFingerprint {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $algorithm.ComputeHash($bytes)
    } finally {
        $algorithm.Dispose()
    }
    return ([BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant())
}

function Get-FinareBuildGraphNormalizedPath {
    param([Parameter(Mandatory)][string]$Path)

    return [IO.Path]::GetFullPath($Path).Replace('\', '/').ToLowerInvariant()
}

function Get-FinareBuildGraphRelativePath {
    param(
        [Parameter(Mandatory)][string]$BaseDirectory,
        [Parameter(Mandatory)][string]$Path
    )

    $basePath = [IO.Path]::GetFullPath($BaseDirectory).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    $baseUri = [Uri]$basePath
    $pathUri = [Uri][IO.Path]::GetFullPath($Path)
    return [Uri]::UnescapeDataString(
        $baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

function Get-FinareBuildGraphPathFingerprint {
    param(
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [AllowNull()]
        [string[]]$Paths
    )

    $records = [Collections.Generic.List[string]]::new()
    $missing = [Collections.Generic.List[string]]::new()
    $normalizedPaths = @(
        $Paths |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { [IO.Path]::GetFullPath($_) } |
            Sort-Object -Unique
    )
    foreach ($path in $normalizedPaths) {
        $identity = Get-FinareBuildGraphNormalizedPath -Path $path
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            $records.Add("file|$identity|$hash")
            continue
        }
        if (Test-Path -LiteralPath $path -PathType Container) {
            $files = @(Get-ChildItem -LiteralPath $path -Recurse -File |
                Sort-Object FullName)
            if ($files.Count -eq 0) {
                $records.Add("directory|$identity|empty")
                continue
            }
            foreach ($file in $files) {
                $relative = (Get-FinareBuildGraphRelativePath `
                    -BaseDirectory $path `
                    -Path $file.FullName).Replace('\', '/').ToLowerInvariant()
                $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).
                    Hash.ToLowerInvariant()
                $records.Add("directory-file|$identity|$relative|$hash")
            }
            continue
        }
        $records.Add("missing|$identity")
        $missing.Add($path)
    }
    if ($records.Count -eq 0) {
        $records.Add('empty')
    }
    return [pscustomobject]@{
        Fingerprint = Get-FinareBuildGraphTextFingerprint `
            -Text ($records -join "`n")
        Missing = @($missing)
        Count = $records.Count
    }
}

function Get-FinareBuildGraphPropertyValue {
    param(
        [AllowNull()][object]$Object,
        [Parameter(Mandatory)][string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }
    if ($Object -is [Collections.IDictionary]) {
        if ($Object.Contains($Name)) {
            return $Object[$Name]
        }
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Get-FinareBuildGraphNamedFingerprint {
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Values,
        [Parameter(Mandatory)][Collections.IDictionary]$PathInputs
    )

    $inputs = [ordered]@{}
    foreach ($key in @($Values.Keys | Sort-Object)) {
        $value = $Values[$key]
        $inputs[[string]$key] = if ($null -eq $value) {
            'null'
        } else {
            ConvertTo-Json -InputObject $value -Depth 20 -Compress
        }
    }
    foreach ($key in @($PathInputs.Keys | Sort-Object)) {
        $paths = @($PathInputs[$key])
        $fingerprint = Get-FinareBuildGraphPathFingerprint -Paths $paths
        $inputs[[string]$key] = $fingerprint.Fingerprint
    }
    $records = @($inputs.Keys | ForEach-Object { "$_=$($inputs[$_])" })
    return [pscustomobject]@{
        Inputs = $inputs
        Fingerprint = Get-FinareBuildGraphTextFingerprint `
            -Text ($records -join "`n")
    }
}

function New-FinareBuildGraphContext {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StatePath,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Config,
        [Parameter(Mandatory)][string]$EngineVersion
    )

    $resolvedStatePath = [IO.Path]::GetFullPath($StatePath)
    $resolvedProject = [IO.Path]::GetFullPath($Project)
    $resolvedConfig = [IO.Path]::GetFullPath($Config)
    $previousStages = @{}
    $cacheStatus = 'missing'
    if (Test-Path -LiteralPath $resolvedStatePath -PathType Leaf) {
        try {
            $state = Get-Content -LiteralPath $resolvedStatePath -Raw |
                ConvertFrom-Json
            if ($state.schemaVersion -ne $script:BuildGraphSchemaVersion) {
                $cacheStatus = 'schema-mismatch'
            } elseif ([IO.Path]::GetFullPath([string]$state.project) -ne
                    $resolvedProject -or
                [IO.Path]::GetFullPath([string]$state.config) -ne
                    $resolvedConfig -or
                [string]$state.engineVersion -ne $EngineVersion) {
                $cacheStatus = 'identity-mismatch'
            } else {
                foreach ($stage in @($state.stages)) {
                    if (-not [string]::IsNullOrWhiteSpace([string]$stage.name)) {
                        $previousStages[[string]$stage.name] = $stage
                    }
                }
                $cacheStatus = 'loaded'
            }
        } catch {
            $cacheStatus = 'invalid'
        }
    }
    return [pscustomobject]@{
        SchemaVersion = $script:BuildGraphSchemaVersion
        StatePath = $resolvedStatePath
        Project = $resolvedProject
        Config = $resolvedConfig
        EngineVersion = $EngineVersion
        CacheStatus = $cacheStatus
        PreviousStages = $previousStages
        CurrentStages = [ordered]@{}
        Decisions = [Collections.Generic.List[object]]::new()
    }
}

function Get-FinareBuildGraphStageDecision {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$Name,
        [Collections.IDictionary]$Values = [ordered]@{},
        [Collections.IDictionary]$PathInputs = [ordered]@{},
        [string[]]$Dependencies = @(),
        [string[]]$OutputPaths = @()
    )

    if ($Context.CurrentStages.Contains($Name)) {
        throw "Build graph stage '$Name' was evaluated more than once."
    }
    $named = Get-FinareBuildGraphNamedFingerprint `
        -Values $Values `
        -PathInputs $PathInputs
    $dependencyFingerprints = [ordered]@{}
    foreach ($dependency in $Dependencies) {
        if (-not $Context.CurrentStages.Contains($dependency)) {
            throw "Build graph stage '$Name' requires unevaluated dependency '$dependency'."
        }
        $dependencyFingerprints[$dependency] =
            $Context.CurrentStages[$dependency].dependencyFingerprint
    }
    $dependencyRecords = @($dependencyFingerprints.Keys |
        ForEach-Object { "$_=$($dependencyFingerprints[$_])" })
    $inputFingerprint = Get-FinareBuildGraphTextFingerprint -Text (
        "direct=$($named.Fingerprint)`n" + ($dependencyRecords -join "`n"))
    $outputSnapshot = Get-FinareBuildGraphPathFingerprint -Paths $OutputPaths
    $previous = if ($Context.PreviousStages.ContainsKey($Name)) {
        $Context.PreviousStages[$Name]
    } else { $null }
    $changedInputs = [Collections.Generic.List[string]]::new()
    $changedDependencies = [Collections.Generic.List[string]]::new()
    $reasons = [Collections.Generic.List[string]]::new()
    if ($null -eq $previous) {
        $reasons.Add('no prior successful state')
    } else {
        $previousInputs = Get-FinareBuildGraphPropertyValue `
            -Object $previous `
            -Name 'inputs'
        foreach ($key in $named.Inputs.Keys) {
            $before = Get-FinareBuildGraphPropertyValue `
                -Object $previousInputs `
                -Name ([string]$key)
            if ([string]$before -ne [string]$named.Inputs[$key]) {
                $changedInputs.Add([string]$key)
            }
        }
        $previousDependencies = Get-FinareBuildGraphPropertyValue `
            -Object $previous `
            -Name 'dependencies'
        foreach ($key in $dependencyFingerprints.Keys) {
            $before = Get-FinareBuildGraphPropertyValue `
                -Object $previousDependencies `
                -Name ([string]$key)
            if ([string]$before -ne [string]$dependencyFingerprints[$key]) {
                $changedDependencies.Add([string]$key)
            }
        }
        if ($changedInputs.Count -gt 0) {
            $reasons.Add("inputs changed: $($changedInputs -join ', ')")
        }
        if ($changedDependencies.Count -gt 0) {
            $reasons.Add(
                "dependency outputs changed: $($changedDependencies -join ', ')")
        }
        if ([string]$previous.outputFingerprint -ne
                [string]$outputSnapshot.Fingerprint) {
            $reasons.Add('tracked outputs are missing or modified')
        }
        if ([string]$previous.inputFingerprint -ne $inputFingerprint -and
            $changedInputs.Count -eq 0 -and
            $changedDependencies.Count -eq 0) {
            $reasons.Add('stage input fingerprint changed')
        }
    }
    $shouldRun = $reasons.Count -gt 0
    if (-not $shouldRun) {
        $reasons.Add('all inputs, dependency outputs, and tracked outputs match')
    }
    return [pscustomobject]@{
        Name = $Name
        ShouldRun = $shouldRun
        Reason = $reasons -join '; '
        ChangedInputs = @($changedInputs)
        ChangedDependencies = @($changedDependencies)
        Inputs = $named.Inputs
        DirectFingerprint = $named.Fingerprint
        Dependencies = $dependencyFingerprints
        InputFingerprint = $inputFingerprint
        OutputFingerprint = $outputSnapshot.Fingerprint
        OutputPaths = @($OutputPaths |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { [IO.Path]::GetFullPath($_) })
        MissingOutputs = @($outputSnapshot.Missing)
    }
}

function Complete-FinareBuildGraphStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][object]$Decision,
        [Parameter(Mandatory)][bool]$Executed,
        [long]$DurationMs = 0
    )

    $outputSnapshot = Get-FinareBuildGraphPathFingerprint `
        -Paths $Decision.OutputPaths
    if ($Executed -and $Decision.OutputPaths.Count -gt 0 -and
        $outputSnapshot.Missing.Count -gt 0) {
        throw (
            "Build graph stage '$($Decision.Name)' did not publish tracked outputs: " +
            ($outputSnapshot.Missing -join ', '))
    }
    $dependencyFingerprint = if ($Decision.OutputPaths.Count -gt 0) {
        $outputSnapshot.Fingerprint
    } else {
        $Decision.InputFingerprint
    }
    $stage = [ordered]@{
        name = $Decision.Name
        inputFingerprint = $Decision.InputFingerprint
        directFingerprint = $Decision.DirectFingerprint
        dependencyFingerprint = $dependencyFingerprint
        outputFingerprint = $outputSnapshot.Fingerprint
        inputs = $Decision.Inputs
        dependencies = $Decision.Dependencies
        outputs = @($Decision.OutputPaths)
    }
    $Context.CurrentStages[$Decision.Name] = $stage
    $Context.Decisions.Add([ordered]@{
        name = $Decision.Name
        status = if ($Executed) { 'executed' } else { 'skipped' }
        reason = $Decision.Reason
        durationMs = $DurationMs
        changedInputs = @($Decision.ChangedInputs)
        changedDependencies = @($Decision.ChangedDependencies)
        inputFingerprint = $Decision.InputFingerprint
        outputFingerprint = $outputSnapshot.Fingerprint
    })
    return [pscustomobject]$stage
}

function Defer-FinareBuildGraphStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][object]$Decision,
        [Parameter(Mandatory)][string]$Reason
    )

    if (-not $Context.PreviousStages.ContainsKey($Decision.Name)) {
        throw "Build graph stage '$($Decision.Name)' cannot be deferred without prior successful state."
    }
    $previous = $Context.PreviousStages[$Decision.Name]
    $Context.CurrentStages[$Decision.Name] = $previous
    $Context.Decisions.Add([ordered]@{
        name = $Decision.Name
        status = 'deferred'
        reason = $Reason
        durationMs = 0
        changedInputs = @($Decision.ChangedInputs)
        changedDependencies = @($Decision.ChangedDependencies)
        inputFingerprint = $Decision.InputFingerprint
        outputFingerprint = $Decision.OutputFingerprint
    })
    return $previous
}

function Save-FinareBuildGraphState {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Context)

    $state = [ordered]@{
        schemaVersion = $Context.SchemaVersion
        project = $Context.Project
        config = $Context.Config
        engineVersion = $Context.EngineVersion
        completedAtUtc = [DateTime]::UtcNow.ToString('O')
        stages = @($Context.CurrentStages.Values)
    }
    $directory = Split-Path -Parent $Context.StatePath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporary = Join-Path $directory (
        ".{0}.{1}.tmp" -f [IO.Path]::GetFileName($Context.StatePath),
        [Guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText(
            $temporary,
            ($state | ConvertTo-Json -Depth 20) + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $Context.StatePath -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Get-FinareBuildGraphReport {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Context)

    $decisions = @($Context.Decisions)
    return [ordered]@{
        schemaVersion = $Context.SchemaVersion
        statePath = $Context.StatePath
        cacheStatus = $Context.CacheStatus
        executed = @($decisions | Where-Object { $_.status -eq 'executed' }).Count
        skipped = @($decisions | Where-Object { $_.status -eq 'skipped' }).Count
        deferred = @($decisions | Where-Object { $_.status -eq 'deferred' }).Count
        stages = $decisions
    }
}

Export-ModuleMember -Function @(
    'New-FinareBuildGraphContext',
    'Get-FinareBuildGraphStageDecision',
    'Complete-FinareBuildGraphStage',
    'Defer-FinareBuildGraphStage',
    'Save-FinareBuildGraphState',
    'Get-FinareBuildGraphReport'
)
