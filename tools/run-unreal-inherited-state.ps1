# Prerequisites: the HostEditor target is built and the fixture builder can
# reference the deployed Release managed host.  CookAndRun performs the full
# retained fixture -> UAT archive -> packaged verifier path.
[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:/UE_5.8',
    [string]$FixtureOutputRoot = 'artifacts/m13-3c-3-inherited-cook/fixture',
    [string]$ArchiveDirectory = 'artifacts/m13-3c-3-inherited-cook/package',
    [switch]$UseExistingFixture,
    [switch]$CookAndRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Join-Path $repoRoot 'HostProject'
$projectFile = Join-Path $projectRoot 'FinareHost.uproject'
$editorCmd = Join-Path $EngineRoot 'Engine/Binaries/Win64/UnrealEditor-Cmd.exe'
$runUat = Join-Path $EngineRoot 'Engine/Build/BatchFiles/RunUAT.bat'
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $FixtureOutputRoot)).TrimEnd('\', '/')
$archiveRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $ArchiveDirectory)).TrimEnd('\', '/')
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')).TrimEnd('\', '/')
$configPath = Join-Path $fixtureRoot 'BlueprintInheritedStateFixture.config.json'
$receiptPath = Join-Path $fixtureRoot 'inherited-state-cook-receipt.json'
$resultPath = Join-Path $fixtureRoot 'inherited-state-cook-result.txt'
$fixtureLog = Join-Path $fixtureRoot 'editor-fixture-generation.log'
$driverLog = Join-Path $fixtureRoot 'build-cook-run.log'
$cookLog = Join-Path $fixtureRoot 'packaged-build-cook.log'
$runLog = Join-Path $fixtureRoot 'packaged-inherited-state.log'
$stageRootBase = Join-Path $projectRoot 'Plugins/Finare/Binaries/Managed/InheritedState'
$cleanupPackages = @()
$cleanupStageFiles = @()
$cleanupContentDirectory = $null
$cleanupStageDirectory = $null
$fixtureReady = $false
$transcriptStarted = $false

function FullPath([string]$Path) { return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/') }

function Assert-UnderRoot([string]$Path, [string]$Root, [string]$Label) {
    $fullPath = FullPath $Path
    $fullRoot = FullPath $Root
    if ($fullPath -ne $fullRoot -and
        !$fullPath.StartsWith($fullRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        !$fullPath.StartsWith($fullRoot + [IO.Path]::AltDirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label '$fullPath' is outside '$fullRoot'."
    }
    return $fullPath
}

function Delete-ExactFile([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $fullPath = FullPath $Path
        [IO.File]::Delete($fullPath)
        Write-Output "Deleted owned file: $fullPath"
    }
}

function Delete-EmptyDirectory([string]$Path) {
    if ((Test-Path -LiteralPath $Path -PathType Container) -and
        (@(Get-ChildItem -LiteralPath $Path -Force).Count -eq 0)) {
        [IO.Directory]::Delete((FullPath $Path), $false)
    }
}

if (!(Test-Path -LiteralPath $editorCmd) -or !(Test-Path -LiteralPath $runUat)) {
    throw "UE 5.8 Editor/UAT tools are missing below '$EngineRoot'."
}
if (!(Test-Path -LiteralPath $projectFile)) { throw "Host project is missing: $projectFile" }
Assert-UnderRoot $fixtureRoot $artifactRoot 'Fixture output root' | Out-Null
Assert-UnderRoot $archiveRoot $artifactRoot 'Archive directory' | Out-Null
if ($fixtureRoot.Equals($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $archiveRoot.Equals($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Fixture and archive outputs must be below the artifacts directory, not the directory itself.'
}
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
Start-Transcript -LiteralPath $driverLog -Force | Out-Null
$transcriptStarted = $true

try {
    if (!$UseExistingFixture) {
        Delete-ExactFile $receiptPath
        Delete-ExactFile $resultPath
        Delete-ExactFile $fixtureLog
        $bundledDotNet = Join-Path $EngineRoot 'Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe'
        if (!(Test-Path -LiteralPath $bundledDotNet)) { throw "Bundled SDK10 dotnet is missing: $bundledDotNet" }
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tools/build-blueprint-inherited-state-fixture.ps1') `
            -DotNet $bundledDotNet -OutputRoot $FixtureOutputRoot *>&1 | Tee-Object -FilePath (Join-Path $fixtureRoot 'fixture-build.log')
        if ($LASTEXITCODE -ne 0) { throw "Inherited-state fixture build failed with exit code $LASTEXITCODE." }
    }
    if (!(Test-Path -LiteralPath $configPath -PathType Leaf)) { throw "Inherited-state fixture config is missing: $configPath" }
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $fixtureId = [string]$config.fixtureId
    $packageDirectory = [string]$config.packageDirectory
    if ($fixtureId -cnotmatch '^[0-9A-F]{32}$' -or
        !$packageDirectory.Equals("/Game/UnifiedScriptInheritedState_$fixtureId", [StringComparison]::Ordinal)) {
        throw "Inherited-state config has invalid GUID/package identity."
    }
    $contentRoot = FullPath (Join-Path $projectRoot 'Content')
    $fixtureDirectory = Assert-UnderRoot (Join-Path $contentRoot ("UnifiedScriptInheritedState_$fixtureId")) $contentRoot 'Cook content directory'
    $stageDirectory = Assert-UnderRoot (Join-Path $stageRootBase $fixtureId) $stageRootBase 'Cook managed stage directory'
    $cleanupContentDirectory = $fixtureDirectory
    $cleanupStageDirectory = $stageDirectory
    New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
    $stageFiles = @(
        @{ Source = [string]$config.chunkPath; Leaf = 'BlueprintInheritedStateFixture.uschunk' },
        @{ Source = [string]$config.assemblyPath; Leaf = 'BlueprintInheritedStateFixture.dll' },
        @{ Source = [string]$config.declarationPath; Leaf = 'BlueprintInheritedStateFixture.asset.json' }
    )
    foreach ($item in $stageFiles) {
        $source = FullPath $item.Source
        Assert-UnderRoot $source $fixtureRoot 'Fixture staged source' | Out-Null
        $target = Assert-UnderRoot (Join-Path $stageDirectory $item.Leaf) $stageDirectory 'Cook managed stage file'
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Fixture stage source is missing: $source" }
        if ($UseExistingFixture) {
            if (!(Test-Path -LiteralPath $target -PathType Leaf)) { throw "Existing fixture stage file is missing: $target" }
        }
        else {
            Delete-ExactFile $target
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
        $cleanupStageFiles += $target
    }
    if (!$UseExistingFixture) {
        & $editorCmd $projectFile '-unattended' '-nop4' '-NullRHI' '-nosplash' '-NoConfigChanges' `
            '-ExecCmds=Automation RunTests Finare.Unreal.ScriptClass.InheritedActorStateRuntime' `
            '-TestExit=Automation Test Queue Empty' `
            "-FinareInheritedStateFixture=$configPath" `
            "-FinareInheritedStateCookReceipt=$receiptPath" `
            "-abslog=$fixtureLog" '-log'
        if ($LASTEXITCODE -ne 0) { throw "Inherited-state fixture generation failed with exit code $LASTEXITCODE. Log=$fixtureLog" }
        if (!(Select-String -LiteralPath $fixtureLog -Pattern 'Test Completed\. Result=\{Success\} Name=\{InheritedActorStateRuntime\}' -Quiet)) {
            throw "Fixture generation did not report selector Success: $fixtureLog"
        }
        if (!(Select-String -LiteralPath $fixtureLog -Pattern 'Automation Test Queue Empty [1-9][0-9]* tests performed\.' -Quiet)) {
            throw "Fixture generation did not report a nonzero Automation queue: $fixtureLog"
        }
    }
    if (!(Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw "Cook receipt is missing: $receiptPath" }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ([string]$receipt.schemaVersion -ne 'unifiedscript.inherited-state-cook-fixture/v0.1' -or
        [string]$receipt.fixtureId -cne $fixtureId -or
        [string]$receipt.packageDirectory -cne $packageDirectory -or
        [string]$receipt.stageDirectory -cne "{PluginDir}/Binaries/Managed/InheritedState/$fixtureId") {
        throw 'Cook receipt schema or identity does not match the generated config.'
    }
    $expectedClasses = @(
        "$packageDirectory/InheritedStateBase.InheritedStateBase_C",
        "$packageDirectory/InheritedStateChild.InheritedStateChild_C",
        "$packageDirectory/InheritedStateGrandchild.InheritedStateGrandchild_C"
    )
    if ([string]$receipt.baseClassPath -cne $expectedClasses[0] -or
        [string]$receipt.childClassPath -cne $expectedClasses[1] -or
        [string]$receipt.grandchildClassPath -cne $expectedClasses[2] -or
        [string]$receipt.scriptAssetPath -cne "$packageDirectory/InheritedStateScriptAsset.InheritedStateScriptAsset") {
        throw 'Cook receipt class or ScriptAsset paths do not match the fixed fixture layout.'
    }
    $packages = @($receipt.packages)
    if ($packages.Count -ne 4) { throw 'Cook receipt must contain exactly four packages.' }
    $packageNames = @()
    $expectedPackageLeaves = @('InheritedStateBase', 'InheritedStateChild', 'InheritedStateGrandchild', 'InheritedStateScriptAsset')
    foreach ($package in $packages) {
        $name = [string]$package.packageName
        $filename = FullPath ([string]$package.packageFilename)
        $leaf = [IO.Path]::GetFileNameWithoutExtension($filename)
        if ([string]::IsNullOrWhiteSpace($name) -or
            [IO.Path]::GetExtension($filename) -ne '.uasset' -or
            [IO.Path]::GetDirectoryName($filename) -ne $fixtureDirectory -or
            $leaf -cnotin $expectedPackageLeaves -or
            $name -cne "$packageDirectory/$leaf" -or
            !(Test-Path -LiteralPath $filename -PathType Leaf)) {
            throw "Cook receipt contains an invalid package path: $filename"
        }
        $packageNames += $name
        $cleanupPackages += $filename
    }
    if (@($packageNames | Select-Object -Unique).Count -ne 4) { throw 'Cook receipt contains duplicate package names.' }
    $fixtureReady = $true
    $sourceByLeaf = @{
        'BlueprintInheritedStateFixture.uschunk' = FullPath ([string]$config.chunkPath)
        'BlueprintInheritedStateFixture.dll' = FullPath ([string]$config.assemblyPath)
        'BlueprintInheritedStateFixture.asset.json' = FullPath ([string]$config.declarationPath)
    }
    foreach ($item in $stageFiles) {
        if ($sourceByLeaf[$item.Leaf] -ne (FullPath ([string]$item.Source))) { throw "Fixture source mapping changed unexpectedly for $($item.Leaf)." }
    }
    Write-Output "Cook receipt validated: $receiptPath"
    if (!$CookAndRun) { return }

    Delete-ExactFile $resultPath
    New-Item -ItemType Directory -Force -Path $archiveRoot | Out-Null
    & $runUat BuildCookRun `
        "-project=$projectFile" '-noP4' '-platform=Win64' '-clientconfig=Development' '-build' '-cook' '-stage' '-pak' '-archive' `
        '-map=/Engine/Maps/Entry' "-CookDir=$fixtureDirectory" "-archivedirectory=$archiveRoot" '-AdditionalCookerOptions=-SkipZenStore' `
        '-unattended' '-utf8output' '-NoCodeSign' '-NoXGE' '-UbtArgs=-NoUBTMakefiles' *>&1 | Tee-Object -FilePath (Join-Path $fixtureRoot 'packaged-build-cook.log')
    if ($LASTEXITCODE -ne 0) { throw "BuildCookRun failed with exit code $LASTEXITCODE." }
    $packageRoot = Join-Path $archiveRoot 'Windows/FinareHost'
    $executables = @(Get-ChildItem -LiteralPath (Join-Path $packageRoot 'Binaries/Win64') -Filter 'FinareHost.exe' -File)
    if ($executables.Count -ne 1) { throw "Expected exactly one FinareHost.exe below $packageRoot, found $($executables.Count)." }
    $executable = $executables[0]
    $runtimeArgs = @(
        '-game', '-unattended', '-nop4', '-NullRHI', '-nosplash', '-FinareInheritedStateVerify',
        "-FinareInheritedStateReceipt=`"$receiptPath`"",
        "-FinareInheritedStateResult=`"$resultPath`"",
        "-abslog=`"$runLog`"", '-log'
    )
    $process = Start-Process -FilePath $executable.FullName -ArgumentList $runtimeArgs -WorkingDirectory $packageRoot -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(300000)) { $process.Kill(); throw "Packaged verifier timed out. Log=$runLog" }
    if ($process.ExitCode -ne 0) { throw "Packaged verifier failed with exit code $($process.ExitCode). Log=$runLog" }
    if (!(Test-Path -LiteralPath $resultPath -PathType Leaf) -or (Get-Content -LiteralPath $resultPath -TotalCount 1).Trim() -ne 'PASS') {
        throw "Packaged verifier did not report PASS: $resultPath"
    }
    if (!(Select-String -LiteralPath $runLog -Pattern 'FINARE_INHERITED_STATE PASS' -Quiet) -or
        !(Select-String -LiteralPath $runLog -Pattern 'Plugins/Finare/Binaries/Managed/InheritedState' -Quiet)) {
        throw "Packaged verifier log lacks the required PASS/path evidence: $runLog"
    }
    Write-Output "Cooked inherited-state PASS: $resultPath"
}
finally {
    if ($CookAndRun -or !$fixtureReady) {
        if ($CookAndRun) {
            foreach ($file in @($cleanupPackages | Select-Object -Unique)) { Delete-ExactFile $file }
        }
        if (!$UseExistingFixture -or $CookAndRun) {
            foreach ($file in @($cleanupStageFiles | Select-Object -Unique)) { Delete-ExactFile $file }
        }
        if ($CookAndRun) {
            if ($null -ne $cleanupContentDirectory) { Delete-EmptyDirectory $cleanupContentDirectory }
            if ($null -ne $cleanupStageDirectory) { Delete-EmptyDirectory $cleanupStageDirectory }
        }
    }
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
}
