[CmdletBinding()]
param(
    [string]$EngineRoot = $env:FINARE_UE_ROOT,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repoRoot `
    'templates\UnrealProject\finare.project-templates.json'
$scaffoldScript = Join-Path $PSScriptRoot 'new-finare-project.ps1'
$pluginDescriptor = Join-Path $repoRoot `
    'HostProject\Plugins\Finare\Finare.uplugin'
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot "artifacts\project-templates\$runId"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
$reportPath = Join-Path $outputRoot 'project-template-acceptance.json'
$startedAt = [DateTime]::UtcNow

function Write-Utf8 {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Text)

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) |
        Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $scaffoldScript -PathType Leaf) -or
    -not (Test-Path -LiteralPath $pluginDescriptor -PathType Leaf)) {
    throw 'Project template catalog, scaffold command, or plugin descriptor is missing.'
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'Pass -EngineRoot or set FINARE_UE_ROOT.'
}
$dotnet = Join-Path ([IO.Path]::GetFullPath($EngineRoot)) `
    'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Engine-bundled .NET 10 is missing: $dotnet"
}

$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
if ($catalog.schemaVersion -ne 'unifiedscript.project-template-catalog/v0.1' -or
    @($catalog.templates).Count -lt 2) {
    throw 'Project template catalog is invalid.'
}
$catalogSha256 = (Get-FileHash -LiteralPath $catalogPath -Algorithm SHA256).Hash.
    ToLowerInvariant()
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

& $dotnet build (Join-Path $repoRoot `
    'src\Finare.Cli\Finare.Cli.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Finare CLI build failed with exit code $LASTEXITCODE."
}
$runtimeFiles = @(
    Join-Path $repoRoot 'src\Finare.Core\bin\Release\net10.0\Finare.Core.dll'
    Join-Path $repoRoot 'src\Finare.Unreal\bin\Release\net10.0\Finare.Unreal.dll'
)
foreach ($runtimeFile in $runtimeFiles) {
    if (-not (Test-Path -LiteralPath $runtimeFile -PathType Leaf)) {
        throw "Managed runtime output is missing: $runtimeFile"
    }
}

$results = [Collections.Generic.List[object]]::new()
foreach ($template in @($catalog.templates)) {
    $templateId = [string]$template.id
    $projectIdentifierSegments = @(($templateId -split '[^A-Za-z0-9]') |
        Where-Object { $_ } |
        ForEach-Object {
            $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1)
        })
    $projectIdentifier = 'Template' + ($projectIdentifierSegments -join '')
    $projectRoot = Join-Path $outputRoot $templateId
    $projectPath = Join-Path $projectRoot "$projectIdentifier.uproject"
    $pluginRoot = Join-Path $projectRoot 'Plugins\Finare'
    $managedRuntime = Join-Path $pluginRoot 'Binaries\Managed'
    $editorTarget = "${projectIdentifier}Editor"
    New-Item -ItemType Directory -Force -Path $managedRuntime | Out-Null
    Copy-Item -LiteralPath $pluginDescriptor -Destination `
        (Join-Path $pluginRoot 'Finare.uplugin') -Force
    foreach ($runtimeFile in $runtimeFiles) {
        Copy-Item -LiteralPath $runtimeFile -Destination $managedRuntime -Force
    }
    Write-Utf8 -Path $projectPath -Text (@{
        FileVersion = 3
        EngineAssociation = ''
        Modules = @(@{ Name = $projectIdentifier; Type = 'Runtime' })
        Plugins = @(@{ Name = 'Finare'; Enabled = $true })
    } | ConvertTo-Json -Depth 6)
    Write-Utf8 `
        -Path (Join-Path $projectRoot "Source\$editorTarget.Target.cs") `
        -Text @"
using UnrealBuildTool;
public class ${editorTarget}Target : TargetRules
{
    public ${editorTarget}Target(TargetInfo Target) : base(Target)
    {
        Type = TargetType.Editor;
        ExtraModuleNames.Add("$projectIdentifier");
    }
}
"@
    $logPath = Join-Path $projectRoot 'scaffold.log'
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & powershell.exe `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $scaffoldScript `
            -Project $projectPath `
            -EngineRoot $EngineRoot `
            -ProjectName $projectIdentifier `
            -ProjectModule $projectIdentifier `
            -EditorTarget $editorTarget `
            -ScriptName 'StarterScript' `
            -Template $templateId `
            -SkipOnboarding 2>&1 |
            Tee-Object -FilePath $logPath |
            ForEach-Object { Write-Host $_ }
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "Project template '$templateId' failed scaffold with exit code $exitCode."
    }
    $scaffoldReportPath = Get-ChildItem `
        -LiteralPath (Join-Path $projectRoot 'Saved\Finare\Scaffold') `
        -File -Recurse -Filter 'scaffold-report.json' |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -ExpandProperty FullName -First 1
    if ([string]::IsNullOrWhiteSpace($scaffoldReportPath)) {
        throw "Project template '$templateId' did not write a scaffold report."
    }
    $scaffoldReport = Get-Content -LiteralPath $scaffoldReportPath -Raw |
        ConvertFrom-Json
    $sourcePath = [string]$scaffoldReport.outputs.managedSource
    $solutionPath = [string]$scaffoldReport.outputs.managedSolution
    $workspacePath = [string]$scaffoldReport.outputs.ideWorkspace
    $ideManifestPath = [string]$scaffoldReport.outputs.ideManifest
    foreach ($required in @(
            $sourcePath,
            $solutionPath,
            $workspacePath,
            $ideManifestPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Project template '$templateId' output is missing: $required"
        }
    }
    $source = Get-Content -LiteralPath $sourcePath -Raw
    [xml]$solution = Get-Content -LiteralPath $solutionPath -Raw
    $workspace = Get-Content -LiteralPath $workspacePath -Raw | ConvertFrom-Json
    $ideManifest = Get-Content -LiteralPath $ideManifestPath -Raw |
        ConvertFrom-Json
    $ideTaskLabels = @($workspace.tasks.tasks | ForEach-Object { [string]$_.label })
    if ($scaffoldReport.schemaVersion -ne 'unifiedscript.project-scaffold-report/v0.2' -or
        $scaffoldReport.status -ne 'passed' -or
        $scaffoldReport.template.id -ne $templateId -or
        $scaffoldReport.templateCatalog.sha256 -ne $catalogSha256 -or
        $source -notmatch '\[ScriptAsset\(' -or
        ($templateId -eq 'minimal' -and $source -match '\[Visual\]') -or
        ($templateId -ne 'minimal' -and $source -notmatch '\[Visual\]') -or
        $solution.Solution.Project.Path -ne "$projectIdentifier.Finare.csproj" -or
        $ideManifest.schemaVersion -ne 'unifiedscript.ide-workspace/v0.1' -or
        'Finare: Build managed gameplay' -notin $ideTaskLabels -or
        @($workspace.launch.configurations | Where-Object {
            $_.type -eq 'coreclr' -and $_.request -eq 'attach'
        }).Count -ne 1) {
        throw "Project template '$templateId' emitted inconsistent evidence."
    }
    $results.Add([ordered]@{
        id = $templateId
        status = 'passed'
        capabilities = @($template.capabilities)
        sourceSha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.
            ToLowerInvariant()
        visualFunctionCount = ([regex]::Matches($source, '\[Visual\]')).Count
        scaffoldReport = $scaffoldReportPath
        log = $logPath
    })
}

$report = [ordered]@{
    schemaVersion = 'unifiedscript.project-template-acceptance/v0.1'
    status = 'passed'
    productVersion = [string]$catalog.productVersion
    catalog = [ordered]@{
        path = $catalogPath
        schemaVersion = [string]$catalog.schemaVersion
        sha256 = $catalogSha256
    }
    summary = [ordered]@{
        total = @($catalog.templates).Count
        passed = $results.Count
        failed = 0
    }
    templates = @($results)
    durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
}
Write-Utf8 -Path $reportPath -Text (($report | ConvertTo-Json -Depth 10) + "`n")
Write-Host "Finare project templates: PASS ($($results.Count)/$(@($catalog.templates).Count))"
Write-Host "Report: $reportPath"
