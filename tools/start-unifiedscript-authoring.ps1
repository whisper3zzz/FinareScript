[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Project,

    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,

    [string]$Script,

    [string]$ApiBrowserPath,

    [ValidateRange(1024, 65535)]
    [int]$Port = 4173,

    [switch]$Background,

    [string]$LogDirectory
)

$ErrorActionPreference = 'Stop'
$projectPath = [IO.Path]::GetFullPath($Project)
$projectRoot = Split-Path -Parent $projectPath
$sdkRoot = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $projectRoot 'Config\UnifiedScript.onboarding.json'
$workbenchRoot = if (Test-Path -LiteralPath (Join-Path $sdkRoot 'prototype\visual-editor')) {
    Join-Path $sdkRoot 'prototype\visual-editor'
} else {
    Join-Path $sdkRoot 'Authoring\Workbench'
}

function Resolve-EngineAssociation {
    param([string]$Association)

    $key = 'HKCU:\Software\Epic Games\Unreal Engine\Builds'
    if (Test-Path $key) {
        $value = (Get-ItemProperty -Path $key).PSObject.Properties |
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

function Resolve-ConfigPath {
    param([string]$Path, [string]$BaseDirectory)

    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $BaseDirectory $Path))
}

foreach ($required in @($projectPath, $configPath, $workbenchRoot)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required authoring input is missing: $required"
    }
}
$projectJson = Get-Content -LiteralPath $projectPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = Resolve-EngineAssociation -Association ([string]$projectJson.EngineAssociation)
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'Unreal Engine could not be resolved. Pass -EngineRoot or register EngineAssociation.'
}
$dotnet = Join-Path ([IO.Path]::GetFullPath($EngineRoot)) `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
$cliAssembly = if (Test-Path -LiteralPath `
    (Join-Path $sdkRoot 'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll')) {
    Join-Path $sdkRoot 'src\UnifiedScript.Cli\bin\Release\net10.0\UnifiedScript.Cli.dll'
} else {
    Join-Path $sdkRoot 'Tools\UnifiedScript.Cli\UnifiedScript.Cli.dll'
}
foreach ($required in @($dotnet, $cliAssembly)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required authoring tool is missing: $required"
    }
}

$configDirectory = Split-Path -Parent $configPath
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$managedProject = Resolve-ConfigPath `
    -Path ([string]@($config.managed.projects)[0]) `
    -BaseDirectory $configDirectory
$managedOutput = Resolve-ConfigPath `
    -Path ([string]$config.bindings.managedOutput) `
    -BaseDirectory $configDirectory
$nodeCatalogOutput = Resolve-ConfigPath `
    -Path ([string]$config.bindings.nodeCatalogOutput) `
    -BaseDirectory $configDirectory
$apiBrowser = if ([string]::IsNullOrWhiteSpace($config.bindings.browserOutput)) {
    $nodeCatalogName = [IO.Path]::GetFileNameWithoutExtension($nodeCatalogOutput)
    $browserName = ($nodeCatalogName -replace '\.api-nodes$', '') +
        '.api-browser.json'
    Join-Path (Split-Path -Parent $nodeCatalogOutput) $browserName
} else {
    Resolve-ConfigPath `
        -Path ([string]$config.bindings.browserOutput) `
        -BaseDirectory $configDirectory
}
if (-not [string]::IsNullOrWhiteSpace($ApiBrowserPath)) {
    $apiBrowser = [IO.Path]::GetFullPath($ApiBrowserPath)
}
$managedStage = Resolve-ConfigPath `
    -Path ([string]$config.managed.stageDirectory) `
    -BaseDirectory $configDirectory
if ([string]::IsNullOrWhiteSpace($Script)) {
    $managedDirectory = Split-Path -Parent $managedProject
    $primarySource = Get-ChildItem -LiteralPath $managedDirectory -File -Filter '*.cs' |
        Where-Object { $_.DirectoryName -notmatch '[\\/](Generated|obj|bin)([\\/]|$)' } |
        Select-Object -First 1
    if ($null -eq $primarySource) {
        throw "No primary managed script was found beside $managedProject"
    }
    $scriptPath = $primarySource.FullName
} elseif ([IO.Path]::IsPathRooted($Script)) {
    $scriptPath = [IO.Path]::GetFullPath($Script)
} else {
    $managedDirectory = Split-Path -Parent $managedProject
    $scriptCandidates = [Collections.Generic.List[string]]::new()
    $scriptCandidates.Add([IO.Path]::GetFullPath((Join-Path $projectRoot $Script)))
    $scriptCandidates.Add([IO.Path]::GetFullPath((Join-Path $managedDirectory $Script)))
    if ([IO.Path]::GetExtension($Script) -ne '.cs') {
        $scriptCandidates.Add(
            [IO.Path]::GetFullPath((Join-Path $managedDirectory "$Script.cs")))
    }
    $scriptPath = $scriptCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        $scriptPath = $scriptCandidates[0]
    }
}
foreach ($required in @($managedProject, $managedOutput, $apiBrowser, $scriptPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required managed authoring input is missing: $required"
    }
}

$matchingChunks = @($config.managed.chunks | Where-Object {
    (Resolve-ConfigPath -Path ([string]$_.project) -BaseDirectory $configDirectory) -eq $managedProject
})
if ($matchingChunks.Count -ne 1) {
    throw 'Authoring requires exactly one configured managed chunk for the selected project; use the normal Build workflow first.'
}
$chunkPath = Resolve-ConfigPath -Path ([string]$matchingChunks[0].stagePath) -BaseDirectory $configDirectory
$scriptStage = Split-Path -Parent $chunkPath
$identityPath = $scriptPath + '.usids'
$assetPath = [IO.Path]::ChangeExtension($chunkPath, '.asset.json')
$surfacePath = [IO.Path]::ChangeExtension($chunkPath, '.surface.json')
$layoutPath = [IO.Path]::ChangeExtension($chunkPath, '.usgraph')
$assemblyPath = [IO.Path]::ChangeExtension($chunkPath, '.dll')
$pdbPath = [IO.Path]::ChangeExtension($chunkPath, '.pdb')
# Separate histories for source-oriented sessions that publish the same project.
$historyPath = Join-Path $scriptStage "$([IO.Path]::GetFileName($scriptPath)).authoring-history.json"
$coreAssembly = Join-Path $managedStage 'UnifiedScript.Core.dll'
$unrealAssembly = Join-Path $managedStage 'UnifiedScript.Unreal.dll'
foreach ($required in @($coreAssembly, $unrealAssembly)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Installed managed runtime is missing: $required"
    }
}

# MSBuild evaluation supplies all Compile items, references and language settings.
# Do not rebuild/stage shared native-host dependencies while the Editor owns them.
# A missing or changed dependency is handled by the normal Build & Reload workflow.
$configuration = if ([string]::IsNullOrWhiteSpace($config.managed.configuration)) { 'Release' } else { [string]$config.managed.configuration }

$arguments = [Collections.Generic.List[string]]::new()
foreach ($argument in @(
    $cliAssembly,
    'debug-serve',
    $scriptPath,
    '--project', $managedProject,
    '--configuration', $configuration,
    '--no-restore',
    '--no-build',
    '--publish-initial',
    '--api-browser', $apiBrowser,
    '--root', $workbenchRoot,
    '--port', [string]$Port,
    '--identity', $identityPath,
    '--asset-out', $assetPath,
    '--surface-out', $surfacePath,
    '--layout-out', $layoutPath,
    '--managed-out', $assemblyPath,
    '--chunk-out', $chunkPath,
    '--pdb-out', $pdbPath,
    '--history', $historyPath)) {
    $arguments.Add([string]$argument)
}
Write-Host "UnifiedScript authoring server: http://127.0.0.1:$Port/"
if ($Background) {
    if ([string]::IsNullOrWhiteSpace($LogDirectory)) { throw '-Background requires -LogDirectory; the caller owns the returned process lifetime.' }
    New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
    $quotedArguments = @($arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' })
    Start-Process -FilePath $dotnet -ArgumentList $quotedArguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $LogDirectory 'authoring.stdout.log') `
        -RedirectStandardError (Join-Path $LogDirectory 'authoring.stderr.log')
    return
}
Write-Host 'Keep this terminal open; use Apply & Reload in the UE authoring tab.'
& $dotnet @arguments
exit $LASTEXITCODE
