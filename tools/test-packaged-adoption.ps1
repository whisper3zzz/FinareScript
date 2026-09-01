[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SdkArchive,

    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$startedAt = [DateTime]::UtcNow
$repoRoot = Split-Path -Parent $PSScriptRoot
$archivePath = [IO.Path]::GetFullPath($SdkArchive)
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot "artifacts\adoption\$runId"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
$extractRoot = Join-Path $outputRoot 'sdk'
$fixtureRoot = Join-Path $outputRoot 'fixtures'
$reportPath = Join-Path $outputRoot 'packaged-adoption-report.json'

function Write-Utf8 {
    param([string]$Path, [string]$Text)

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) |
        Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Get-RelativeFilePath {
    param([string]$BaseDirectory, [string]$Path)

    $basePath = [IO.Path]::GetFullPath($BaseDirectory).TrimEnd('\', '/') +
        [IO.Path]::DirectorySeparatorChar
    return [Uri]::UnescapeDataString(
        ([Uri]$basePath).MakeRelativeUri([Uri][IO.Path]::GetFullPath($Path)).ToString()).
        Replace('/', '\')
}

function Get-ProductSnapshot {
    param([string]$ProjectRoot)

    $snapshot = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $ProjectRoot -Recurse -File |
            Sort-Object FullName) {
        $relative = (Get-RelativeFilePath `
            -BaseDirectory $ProjectRoot `
            -Path $file.FullName).Replace('\', '/')
        if ($relative.StartsWith('Saved/', [StringComparison]::OrdinalIgnoreCase) -or
            $relative -match '^Managed/[^/]+/(?:bin|obj)/' -or
            $relative.StartsWith(
                'Plugins/UEUnifiedScript/Binaries/Managed/Scripts/',
                [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        $snapshot[$relative] = (Get-FileHash `
            -LiteralPath $file.FullName `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return ($snapshot | ConvertTo-Json -Compress)
}

function Invoke-AdoptionCli {
    param([string]$Cli, [string[]]$Arguments)

    & $Cli @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "UnifiedScript CLI failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

function Invoke-AdoptionCliJson {
    param([string]$Cli, [string[]]$Arguments)

    $output = @(& $Cli @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "UnifiedScript CLI failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
    return (($output -join "`n") | ConvertFrom-Json)
}

function New-AdoptionFixture {
    param([string]$Name, [bool]$Native)

    $root = Join-Path $fixtureRoot $Name
    $project = Join-Path $root "$Name.uproject"
    $document = [ordered]@{
        FileVersion = 3
        EngineAssociation = '5.8'
        FixtureField = 'preserved'
        Plugins = @([ordered]@{ Name = 'Puerts'; Enabled = $true })
    }
    if ($Native) {
        $document.Modules = @([ordered]@{ Name = $Name; Type = 'Runtime' })
    }
    Write-Utf8 -Path $project -Text (($document | ConvertTo-Json -Depth 8) + "`n")
    Write-Utf8 -Path (Join-Path $root 'Config\DefaultGame.ini') -Text "keep`n"
    if ($Native) {
        Write-Utf8 `
            -Path (Join-Path $root "Source\${Name}Editor.Target.cs") `
            -Text "// packaged adoption fixture`n"
    }
    return [ordered]@{ root = $root; project = $project; native = $Native }
}

if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    throw "SDK archive does not exist: $archivePath"
}
if (Test-Path -LiteralPath $outputRoot) {
    throw "Packaged adoption output already exists: $outputRoot"
}
New-Item -ItemType Directory -Force -Path $extractRoot, $fixtureRoot | Out-Null
Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot
$sdkRoot = Get-ChildItem -LiteralPath $extractRoot -Directory |
    Select-Object -ExpandProperty FullName -First 1
if ([string]::IsNullOrWhiteSpace($sdkRoot)) {
    throw 'The SDK archive has no top-level directory.'
}
$cli = Join-Path $sdkRoot 'Tools\unifiedscript.cmd'
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) {
    throw "Packaged UnifiedScript entry point is missing: $cli"
}
$results = [Collections.Generic.List[object]]::new()
foreach ($fixture in @(
        (New-AdoptionFixture -Name 'NativeAdoption' -Native $true),
        (New-AdoptionFixture -Name 'BlueprintAdoption' -Native $false))) {
    $project = [string]$fixture.project
    $projectRoot = [string]$fixture.root
    $before = Get-ProductSnapshot -ProjectRoot $projectRoot
    $previewPath = Join-Path $projectRoot 'Saved\UnifiedScript\Adoption\preview.json'
    Invoke-AdoptionCli `
        -Cli $cli `
        -Arguments @('adopt', $project, '--sdk-root', $sdkRoot, '--preview', '--report', $previewPath)
    if ((Get-ProductSnapshot -ProjectRoot $projectRoot) -cne $before) {
        throw "Preview changed product files for $project"
    }
    $preview = Get-Content -LiteralPath $previewPath -Raw | ConvertFrom-Json
    if ($preview.status -ne 'ready' -or [int]$preview.summary.conflicts -ne 0) {
        throw "Preview did not produce a conflict-free plan for $project"
    }

    Invoke-AdoptionCli `
        -Cli $cli `
        -Arguments @('adopt', $project, '--sdk-root', $sdkRoot)
    $projectName = [IO.Path]::GetFileNameWithoutExtension($project)
    $assemblyName = "$projectName.UnifiedScript"
    foreach ($required in @(
            'Plugins\UEUnifiedScript\UEUnifiedScript.uplugin',
            'Plugins\UEUnifiedScript\Tools\UnifiedScript.Cli\UnifiedScript.Cli.exe',
            'Plugins\UEUnifiedScript\Tools\unifiedscript.cmd',
            'Plugins\UEUnifiedScript\Tools\setup-unreal-project.ps1',
            'Plugins\UEUnifiedScript\Tools\run-unifiedscript-onboarding-after-process.ps1',
            'Plugins\UEUnifiedScript\Tools\start-unifiedscript-authoring.ps1',
            'Plugins\UEUnifiedScript\Tools\Documentation\DOCUMENTATION.md',
            'Plugins\UEUnifiedScript\Tools\Documentation\UNREAL_FIRST_PROJECT.md',
            'Plugins\UEUnifiedScript\Tools\Documentation\UNREAL_GAMEPLAY_RECIPES.md',
            'Plugins\UEUnifiedScript\Authoring\Workbench\index.html',
            "Managed\$assemblyName\FirstScript.cs",
            "Managed\$assemblyName\$assemblyName.slnx",
            'Managed\UnifiedScript.ide.json',
            "$projectName.UnifiedScript.code-workspace",
            'Config\UnifiedScript.onboarding.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $projectRoot $required) -PathType Leaf)) {
            throw "Adoption output is missing: $required"
        }
    }
    [xml]$solution = Get-Content -LiteralPath `
        (Join-Path $projectRoot "Managed\$assemblyName\$assemblyName.slnx") -Raw
    if ($solution.Solution.Project.Path -ne "$assemblyName.csproj") {
        throw "Generated IDE solution does not reference the managed project for $project"
    }
    $workspace = Get-Content -LiteralPath `
        (Join-Path $projectRoot "$projectName.UnifiedScript.code-workspace") `
        -Raw | ConvertFrom-Json
    $workspaceTasks = @($workspace.tasks.tasks | ForEach-Object { [string]$_.label })
    if ('UnifiedScript: Build managed gameplay' -notin $workspaceTasks -or
        'UnifiedScript: Build and prepare Unreal' -notin $workspaceTasks -or
        'UnifiedScript: Validate project' -notin $workspaceTasks -or
        @($workspace.launch.configurations | Where-Object {
            $_.type -eq 'coreclr' -and $_.request -eq 'attach'
        }).Count -ne 1) {
        throw "Generated IDE workspace lacks build, validate, or debugger configuration for $project"
    }
    $ideManifest = Get-Content -LiteralPath `
        (Join-Path $projectRoot 'Managed\UnifiedScript.ide.json') `
        -Raw | ConvertFrom-Json
    if ($ideManifest.schemaVersion -ne 'unifiedscript.ide-workspace/v0.1' -or
        $ideManifest.typedApi.generatedBindings -ne
            "Managed/$assemblyName/Generated/Unreal.bindings.generated.cs") {
        throw "Generated IDE manifest is invalid for $project"
    }

    # FirstScript intentionally consumes generated Unreal wrappers. Their first
    # materialization belongs to Build/Onboard and is covered by the packaged
    # native acceptance; adoption alone cannot compile before reflection and
    # generated-surface publication have run.
    $afterAdoption = Get-ProductSnapshot -ProjectRoot $projectRoot

    $projectPlugin = Join-Path $projectRoot 'Plugins\UEUnifiedScript'
    $projectCli = Join-Path $projectPlugin 'Tools\unifiedscript.cmd'
    $firstProjectGuide = Invoke-AdoptionCliJson `
        -Cli $projectCli `
        -Arguments @('guide', 'first-project', '--json')
    $gameplayRecipes = Invoke-AdoptionCliJson `
        -Cli $projectCli `
        -Arguments @('guide', 'recipes', '--json')
    $installedDocumentation = Join-Path $projectPlugin 'Tools\Documentation'
    if ($firstProjectGuide.schemaVersion -ne 'unifiedscript.product-guidance/v0.1' -or
        $firstProjectGuide.path -ne
            (Join-Path $installedDocumentation 'UNREAL_FIRST_PROJECT.md') -or
        $gameplayRecipes.schemaVersion -ne 'unifiedscript.product-guidance/v0.1' -or
        $gameplayRecipes.path -ne
            (Join-Path $installedDocumentation 'UNREAL_GAMEPLAY_RECIPES.md')) {
        throw "Project-local product guidance did not resolve installed documentation for $project"
    }
    $doctorReportPath = Join-Path $projectRoot `
        'Saved\UnifiedScript\ProjectSetup\doctor-report.json'
    $doctorArguments = @('doctor', $project, '--report', $doctorReportPath)
    if (-not [string]::IsNullOrWhiteSpace($EngineRoot)) {
        $doctorArguments += @('--engine-root', [IO.Path]::GetFullPath($EngineRoot))
    }
    Invoke-AdoptionCli -Cli $projectCli -Arguments $doctorArguments
    $doctor = Get-Content -LiteralPath $doctorReportPath -Raw | ConvertFrom-Json
    $projectToolsCheck = $doctor.checks |
        Where-Object { $_.id -eq 'project-tools' } |
        Select-Object -First 1
    $ideWorkspaceCheck = $doctor.checks |
        Where-Object { $_.id -eq 'ide-workspace' } |
        Select-Object -First 1
    $ideManifestCheck = $doctor.checks |
        Where-Object { $_.id -eq 'ide-manifest' } |
        Select-Object -First 1
    if ($doctor.status -ne 'passed' -or
        $projectToolsCheck.status -ne 'passed' -or
        $ideWorkspaceCheck.status -ne 'passed' -or
        $ideManifestCheck.status -ne 'passed') {
        throw "Project-local doctor did not validate the installed tools for $project"
    }

    $diagnosticsInput = Join-Path $projectRoot 'Saved\UnifiedScript\EditorDiagnostics'
    New-Item -ItemType Directory -Force -Path $diagnosticsInput | Out-Null
    $editorDiagnostic = [ordered]@{
        schemaVersion = 'unifiedscript.editor-diagnostics/v0.2'
        retainedCount = 1
        diagnostics = @([ordered]@{
            sequence = 1
            domain = 'compile'
            stage = 'compile'
            code = 'CS0103'
            severity = 'error'
            message = 'Fixture compile diagnostic.'
            sourcePath = 'Managed/FirstScript.cs'
            line = 7
            column = 5
            member = 'us_fixture_member'
            graphNodeId = 'us_fixture_node'
            packagePath = '/Game/FirstScript'
            worldHostId = '0'
            instanceId = '0'
            generation = 0
            action = 'retry'
            evidencePath = $doctorReportPath
        })
    }
    [IO.File]::WriteAllText(
        (Join-Path $diagnosticsInput 'latest.json'),
        ($editorDiagnostic | ConvertTo-Json -Depth 8) + "`n",
        [Text.UTF8Encoding]::new($false))
    $supportBundlePath = Join-Path $projectRoot `
        'Saved\UnifiedScriptSupport\UnifiedScriptSupport.zip'
    New-Item -ItemType Directory -Force -Path `
        ([IO.Path]::GetDirectoryName($supportBundlePath)) | Out-Null
    Invoke-AdoptionCli `
        -Cli $projectCli `
        -Arguments @(
            'diagnostics-bundle',
            (Join-Path $projectRoot 'Saved\UnifiedScript'),
            '--out', $supportBundlePath)
    if (-not (Test-Path -LiteralPath $supportBundlePath -PathType Leaf) -or
        (Get-Item -LiteralPath $supportBundlePath).Length -le 0) {
        throw "Project-local support bundle export failed for $project"
    }

    $rerunReportPath = Join-Path $projectRoot `
        'Saved\UnifiedScript\ProjectSetup\adoption-rerun.json'
    Invoke-AdoptionCli `
        -Cli $projectCli `
        -Arguments @(
            'adopt', $project,
            '--sdk-root', $projectPlugin,
            '--apply',
            '--report', $rerunReportPath)
    $rerun = Get-Content -LiteralPath $rerunReportPath -Raw | ConvertFrom-Json
    if ([int]$rerun.summary.changed -ne 0 -or
        [int]$rerun.summary.conflicts -ne 0) {
        throw "Project-local adoption rerun was not a deterministic no-op for $project"
    }
    if ((Get-ProductSnapshot -ProjectRoot $projectRoot) -cne $afterAdoption) {
        throw "Project-local repeated adoption changed product files for $project"
    }

    Invoke-AdoptionCli -Cli $cli -Arguments @('adopt', $project, '--rollback')
    if ((Get-ProductSnapshot -ProjectRoot $projectRoot) -cne $before) {
        throw "Rollback did not restore the original product files for $project"
    }
    $results.Add([ordered]@{
        project = [IO.Path]::GetFileName($project)
        kind = if ([bool]$fixture.native) { 'native' } else { 'blueprintOnly' }
        status = 'passed'
        plannedChanges = [int]$preview.summary.changed
        previewFingerprint = [string]$preview.fingerprint
        projectLocalDoctor = [string]$doctor.status
        projectLocalGuidance = 'passed'
        projectLocalRerunChanges = [int]$rerun.summary.changed
        managedBuild = 'deferredToPackagedNativeOnboarding'
        supportBundle = $supportBundlePath
    })
}

$report = [ordered]@{
    schemaVersion = 'unifiedscript.packaged-adoption-acceptance/v0.1'
    status = 'passed'
    sdkArchive = $archivePath
    sdkArchiveSha256 = (Get-FileHash `
        -LiteralPath $archivePath `
        -Algorithm SHA256).Hash.ToLowerInvariant()
    sdkRoot = $sdkRoot
    summary = [ordered]@{
        total = $results.Count
        passed = $results.Count
        failed = 0
    }
    fixtures = @($results)
    durationMs = [long]([DateTime]::UtcNow - $startedAt).TotalMilliseconds
}
Write-Utf8 -Path $reportPath -Text (($report | ConvertTo-Json -Depth 10) + "`n")
Write-Host "UnifiedScript packaged adoption: PASS ($($results.Count)/$($results.Count))"
Write-Host $reportPath
