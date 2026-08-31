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
$dotnet = if (-not [string]::IsNullOrWhiteSpace($EngineRoot) -and
        (Test-Path -LiteralPath (Join-Path ([IO.Path]::GetFullPath($EngineRoot)) `
            'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe') -PathType Leaf)) {
    Join-Path ([IO.Path]::GetFullPath($EngineRoot)) `
        'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
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
    foreach ($required in @(
            'Plugins\UEUnifiedScript\UEUnifiedScript.uplugin',
            "Managed\$([IO.Path]::GetFileNameWithoutExtension($project)).UnifiedScript\FirstScript.cs",
            'Config\UnifiedScript.onboarding.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $projectRoot $required) -PathType Leaf)) {
            throw "Adoption output is missing: $required"
        }
    }

    $projectName = [IO.Path]::GetFileNameWithoutExtension($project)
    $managedProject = Join-Path $projectRoot `
        "Managed\$projectName.UnifiedScript\$projectName.UnifiedScript.csproj"
    & $dotnet build $managedProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Adopted managed project failed to build with exit code ${LASTEXITCODE}: $managedProject"
    }
    $stagedAssembly = Join-Path $projectRoot `
        "Plugins\UEUnifiedScript\Binaries\Managed\Scripts\$projectName.UnifiedScript.dll"
    if (-not (Test-Path -LiteralPath $stagedAssembly -PathType Leaf)) {
        throw "Adopted managed build did not stage its assembly: $stagedAssembly"
    }
    $afterBuild = Get-ProductSnapshot -ProjectRoot $projectRoot

    $doctorArguments = @('doctor', $project)
    if (-not [string]::IsNullOrWhiteSpace($EngineRoot)) {
        $doctorArguments += @('--engine-root', [IO.Path]::GetFullPath($EngineRoot))
    }
    Invoke-AdoptionCli -Cli $cli -Arguments $doctorArguments
    Invoke-AdoptionCli `
        -Cli $cli `
        -Arguments @('adopt', $project, '--sdk-root', $sdkRoot)
    if ((Get-ProductSnapshot -ProjectRoot $projectRoot) -cne $afterBuild) {
        throw "Repeated adoption changed product files for $project"
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
