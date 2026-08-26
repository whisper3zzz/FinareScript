[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Project,

    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,

    [string]$ProjectName,

    [string]$ProjectModule,

    [string]$EditorTarget,

    [ValidatePattern('^[A-Za-z_][A-Za-z0-9_]*$')]
    [string]$ScriptName = 'FirstScript',

    [string]$ManagedNamespace,

    [switch]$Force,

    [switch]$SkipOnboarding
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$startedAt = [DateTime]::UtcNow
$projectPath = [IO.Path]::GetFullPath($Project)
$projectRoot = Split-Path -Parent $projectPath
$sdkRoot = Split-Path -Parent $PSScriptRoot
$templateRoot = if (Test-Path -LiteralPath (Join-Path $sdkRoot 'templates\UnrealProject')) {
    Join-Path $sdkRoot 'templates\UnrealProject'
} else {
    Join-Path $sdkRoot 'Templates\UnrealProject'
}
$setupScript = Join-Path $PSScriptRoot 'setup-unreal-project.ps1'
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportPath = Join-Path $projectRoot "Saved\UnifiedScript\Scaffold\$runId\scaffold-report.json"
$createdFiles = [Collections.Generic.List[string]]::new()

function Throw-ScaffoldError {
    param([string]$Code, [string]$Message)

    $exception = [InvalidOperationException]::new($Message)
    $exception.Data['UnifiedScriptCode'] = $Code
    throw $exception
}

function Resolve-EngineAssociation {
    param([string]$Association)

    $customBuildsKey = 'HKCU:\Software\Epic Games\Unreal Engine\Builds'
    if (Test-Path $customBuildsKey) {
        $customBuilds = Get-ItemProperty -Path $customBuildsKey
        $value = $customBuilds.PSObject.Properties |
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
            if (-not [string]::IsNullOrWhiteSpace($installed)) { return [string]$installed }
        }
    }
    return $null
}

function Render-Template {
    param([string]$TemplatePath, [string]$Destination, [hashtable]$Replacements)

    $text = [IO.File]::ReadAllText($TemplatePath)
    foreach ($entry in $Replacements.GetEnumerator()) {
        $text = $text.Replace("__$($entry.Key)__", [string]$entry.Value)
    }
    if ($text -match '__[A-Z0-9_]+__') {
        Throw-ScaffoldError `
            'USNEW1102' `
            "Template has unresolved placeholders: $TemplatePath"
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) |
        Out-Null
    [IO.File]::WriteAllText(
        $Destination,
        $text,
        [Text.UTF8Encoding]::new($false))
    $script:createdFiles.Add($Destination)
}

function Write-ScaffoldReport {
    param([string]$Status, [string]$Code, [string]$Message, [object]$Outputs)

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $reportPath) |
        Out-Null
    $document = [ordered]@{
        schemaVersion = 'unifiedscript.project-scaffold-report/v0.1'
        status = $Status
        project = $projectPath
        sdkRoot = $sdkRoot
        createdFiles = @($script:createdFiles)
        outputs = $Outputs
        error = if ($Status -eq 'passed') { $null } else {
            [ordered]@{ code = $Code; message = $Message }
        }
        durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
    }
    [IO.File]::WriteAllText(
        $reportPath,
        ($document | ConvertTo-Json -Depth 8) + "`n",
        [Text.UTF8Encoding]::new($false))
}

try {
    foreach ($required in @($projectPath, $templateRoot, $setupScript)) {
        if (-not (Test-Path -LiteralPath $required)) {
            Throw-ScaffoldError 'USNEW1001' "Required scaffold input is missing: $required"
        }
    }
    if ([IO.Path]::GetExtension($projectPath) -ne '.uproject') {
        Throw-ScaffoldError 'USNEW1001' "Project must be a .uproject file: $projectPath"
    }
    $projectJson = Get-Content -LiteralPath $projectPath -Raw | ConvertFrom-Json
    $enabledPlugin = @($projectJson.Plugins) |
        Where-Object { $_.Name -eq 'UEUnifiedScript' -and $_.Enabled -eq $true }
    if ($enabledPlugin.Count -eq 0 -or
        -not (Test-Path -LiteralPath (Join-Path $projectRoot 'Plugins\UEUnifiedScript\UEUnifiedScript.uplugin'))) {
        Throw-ScaffoldError `
            'USNEW1002' `
            'Install and explicitly enable the UEUnifiedScript plugin before scaffolding.'
    }

    if ([string]::IsNullOrWhiteSpace($ProjectName)) {
        $ProjectName = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    }
    if ($ProjectName -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        Throw-ScaffoldError `
            'USNEW1003' `
            "ProjectName must be a C#/path-safe identifier: $ProjectName"
    }
    if ([string]::IsNullOrWhiteSpace($ProjectModule)) {
        $ProjectModule = [string]@($projectJson.Modules)[0].Name
    }
    if ($ProjectModule -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        Throw-ScaffoldError 'USNEW1003' "ProjectModule is invalid: $ProjectModule"
    }
    if ([string]::IsNullOrWhiteSpace($EditorTarget)) {
        $editorTargetFile = Get-ChildItem `
            -LiteralPath (Join-Path $projectRoot 'Source') `
            -Recurse -File -Filter '*Editor.Target.cs' |
            Select-Object -First 1
        if ($null -eq $editorTargetFile) {
            Throw-ScaffoldError 'USNEW1004' 'No Editor.Target.cs was found in the project.'
        }
        $EditorTarget = $editorTargetFile.Name.Substring(
            0,
            $editorTargetFile.Name.Length - '.Target.cs'.Length)
    }
    if ($EditorTarget -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        Throw-ScaffoldError 'USNEW1003' "EditorTarget is invalid: $EditorTarget"
    }
    if ([string]::IsNullOrWhiteSpace($ManagedNamespace)) {
        $ManagedNamespace = "$ProjectName.UnifiedScript"
    }
    if ($ManagedNamespace -notmatch '^[A-Za-z_][A-Za-z0-9_.]*$') {
        Throw-ScaffoldError 'USNEW1003' "ManagedNamespace is invalid: $ManagedNamespace"
    }
    $assemblyName = "$ProjectName.UnifiedScript"

    $resolvedEngineRoot = $EngineRoot
    if ([string]::IsNullOrWhiteSpace($resolvedEngineRoot)) {
        $resolvedEngineRoot = Resolve-EngineAssociation `
            -Association ([string]$projectJson.EngineAssociation)
    }
    if ([string]::IsNullOrWhiteSpace($resolvedEngineRoot)) {
        Throw-ScaffoldError `
            'USNEW1005' `
            'Unreal Engine could not be resolved. Pass -EngineRoot or register EngineAssociation.'
    }
    $resolvedEngineRoot = [IO.Path]::GetFullPath($resolvedEngineRoot)
    $dotnet = Join-Path $resolvedEngineRoot `
        'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) {
        Throw-ScaffoldError 'USNEW1005' "Engine-bundled .NET 10 is missing: $dotnet"
    }
    $cliAssembly = if (Test-Path -LiteralPath `
        (Join-Path $sdkRoot 'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll')) {
        Join-Path $sdkRoot 'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll'
    } else {
        Join-Path $sdkRoot 'Tools\UnifiedScript.Cli\UnifiedScript.Cli.dll'
    }
    if (-not (Test-Path -LiteralPath $cliAssembly)) {
        Throw-ScaffoldError 'USNEW1006' "UnifiedScript CLI is missing: $cliAssembly"
    }

    $managedRoot = Join-Path $projectRoot "Managed\$assemblyName"
    $outputs = [ordered]@{
        config = Join-Path $projectRoot 'Config\UnifiedScript.onboarding.json'
        contract = Join-Path $projectRoot "Contracts\$ProjectName.unreal-api-contract.json"
        deployment = Join-Path $projectRoot "Deployment\$ProjectName.unifiedscript-deployment.json"
        managedProject = Join-Path $managedRoot "$assemblyName.csproj"
        managedSource = Join-Path $managedRoot "$ScriptName.cs"
        managedStage = Join-Path $projectRoot 'Plugins\UEUnifiedScript\Binaries\Managed\Scripts'
    }
    foreach ($destination in @(
        $outputs.config,
        $outputs.contract,
        $outputs.deployment,
        $outputs.managedProject,
        $outputs.managedSource)) {
        if ((Test-Path -LiteralPath $destination) -and -not $Force) {
            Throw-ScaffoldError `
                'USNEW1101' `
                "Scaffold output already exists; use -Force to replace it: $destination"
        }
    }
    $replacements = @{
        PROJECT_NAME = $ProjectName
        PROJECT_IDENTIFIER = $ProjectName
        PROJECT_MODULE = $ProjectModule
        EDITOR_TARGET = $EditorTarget
        SCRIPT_NAME = $ScriptName
        MANAGED_NAMESPACE = $ManagedNamespace
        ASSEMBLY_NAME = $assemblyName
    }
    Render-Template `
        -TemplatePath (Join-Path $templateRoot 'Config\UnifiedScript.onboarding.json.template') `
        -Destination $outputs.config `
        -Replacements $replacements
    Render-Template `
        -TemplatePath (Join-Path $templateRoot 'Contracts\Project.unreal-api-contract.json.template') `
        -Destination $outputs.contract `
        -Replacements $replacements
    Render-Template `
        -TemplatePath (Join-Path $templateRoot 'Deployment\Project.unifiedscript-deployment.json.template') `
        -Destination $outputs.deployment `
        -Replacements $replacements
    Render-Template `
        -TemplatePath (Join-Path $templateRoot 'Managed\Project.UnifiedScript.csproj.template') `
        -Destination $outputs.managedProject `
        -Replacements $replacements
    Render-Template `
        -TemplatePath (Join-Path $templateRoot 'Managed\FirstScript.cs.template') `
        -Destination $outputs.managedSource `
        -Replacements $replacements

    Write-Host '[authoring] Generating the first Declaration IR and Authoring Surface...'
    New-Item -ItemType Directory -Force -Path $outputs.managedStage | Out-Null
    & $dotnet $cliAssembly analyze $outputs.managedProject `
        --out $outputs.managedStage `
        --configuration Release `
        --framework net10.0
    if ($LASTEXITCODE -ne 0) {
        Throw-ScaffoldError `
            'USNEW2001' `
            "Initial authoring artifact generation failed with exit code $LASTEXITCODE."
    }
    foreach ($artifactExtension in @('asset.json', 'surface.json')) {
        $artifact = Join-Path $outputs.managedStage "$assemblyName.$artifactExtension"
        if (-not (Test-Path -LiteralPath $artifact)) {
            Throw-ScaffoldError 'USNEW2001' "Initial authoring artifact is missing: $artifact"
        }
    }

    if (-not $SkipOnboarding) {
        Write-Host '[onboarding] Building bindings, Script Asset, and project integration...'
        $windowsPowerShell = (Get-Command powershell.exe -ErrorAction Stop).Source
        & $windowsPowerShell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $setupScript `
            -Project $projectPath `
            -EngineRoot $resolvedEngineRoot `
            -Config $outputs.config
        if ($LASTEXITCODE -ne 0) {
            Throw-ScaffoldError `
                'USNEW2002' `
                "Project onboarding failed with exit code $LASTEXITCODE."
        }
    }

    Write-ScaffoldReport `
        -Status 'passed' `
        -Code $null `
        -Message $null `
        -Outputs $outputs
    Write-Host "UnifiedScript first project scaffold: PASS ($reportPath)"
} catch {
    $code = if ($_.Exception.Data.Contains('UnifiedScriptCode')) {
        [string]$_.Exception.Data['UnifiedScriptCode']
    } else { 'USNEW9999' }
    Write-ScaffoldReport `
        -Status 'failed' `
        -Code $code `
        -Message $_.Exception.Message `
        -Outputs $null
    Write-Error "UnifiedScript project scaffold failed [$code]: $($_.Exception.Message)"
    exit 1
}
