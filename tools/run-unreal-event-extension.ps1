# Prerequisites: the UE SDK10 Release Demo/chunk and Host Editor/Host builds already exist.
# UseExistingFixture reuses the retained passing Editor fixture; omit it only to regenerate one.
[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:/UE_5.8',
    [string]$FixtureOutputRoot = 'artifacts/m13-3c-2-runtime/fixture-output',
    [string]$ArchiveDirectory = 'artifacts/m13-3c-2-runtime/package',
    [switch]$UseExistingFixture,
    [switch]$CookAndRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Join-Path $repoRoot 'HostProject'
$projectFile = Join-Path $projectRoot 'UEUnifiedScriptHost.uproject'
$editorCmd = Join-Path $EngineRoot 'Engine/Binaries/Win64/UnrealEditor-Cmd.exe'
$runUat = Join-Path $EngineRoot 'Engine/Build/BatchFiles/RunUAT.bat'
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $FixtureOutputRoot))
$archiveRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $ArchiveDirectory))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$driverLog = Join-Path $fixtureRoot 'build-cook-run.log'
$fixtureLog = Join-Path $fixtureRoot 'editor-fixture-generation.log'
$receiptPath = Join-Path $fixtureRoot 'event-extension-receipt.json'
$resultPath = Join-Path $fixtureRoot 'event-extension-result.txt'
$runLog = Join-Path $fixtureRoot 'packaged-event-extension.log'
$cookLog = Join-Path $fixtureRoot 'packaged-build-cook.log'
$transcriptStarted = $false
$cleanupPackageFilenames = @()
$cleanupFixtureDirectory = $null

function Assert-UnderRoot([string]$Path, [string]$Root, [string]$Label) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if ($fullPath -ne $fullRoot -and
        !$fullPath.StartsWith($fullRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        !$fullPath.StartsWith($fullRoot + [IO.Path]::AltDirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label '$fullPath' is outside '$fullRoot'."
    }
    return $fullPath
}

function Delete-ExactFile([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        [IO.File]::Delete([IO.Path]::GetFullPath($Path))
    }
}

if (!(Test-Path -LiteralPath $editorCmd) -or !(Test-Path -LiteralPath $runUat)) {
    throw "UE 5.8 Editor/UAT tools are missing below '$EngineRoot'."
}
if (!(Test-Path -LiteralPath $projectFile)) {
    throw "Host project is missing: $projectFile"
}
Assert-UnderRoot $fixtureRoot $artifactRoot 'Fixture output root' | Out-Null
Assert-UnderRoot $archiveRoot $artifactRoot 'Archive directory' | Out-Null
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $driverLog) | Out-Null
Start-Transcript -LiteralPath $driverLog -Force | Out-Null
$transcriptStarted = $true

try {
    if (!$UseExistingFixture) {
        Delete-ExactFile $receiptPath
        Delete-ExactFile $fixtureLog
        Delete-ExactFile $resultPath
        & $editorCmd $projectFile `
            '-unattended' `
            '-nop4' `
            '-NullRHI' `
            '-nosplash' `
            '-NoConfigChanges' `
            '-ExecCmds=Automation RunTests UnifiedScript.Unreal.ScriptClass.BlueprintEventExtensionCookFixture' `
            '-TestExit=Automation Test Queue Empty' `
            "-UnifiedScriptEventExtensionFixtureOutput=$fixtureRoot" `
            "-abslog=$fixtureLog" `
            '-log'
        if ($LASTEXITCODE -ne 0) {
            throw "Event extension fixture generation failed with exit code $LASTEXITCODE. Log=$fixtureLog"
        }
        if (!(Select-String -LiteralPath $fixtureLog -Pattern 'Test Completed\. Result=\{Success\} Name=\{BlueprintEventExtensionCookFixture\}' -Quiet)) {
            throw "Fixture generation log does not contain the required Success marker: $fixtureLog"
        }
        if (!(Select-String -LiteralPath $fixtureLog -Pattern 'Automation Test Queue Empty [1-9][0-9]* tests performed\.' -Quiet)) {
            throw "Fixture generation log does not contain the required Automation Test Queue Empty marker: $fixtureLog"
        }
    }

    if (!(Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw "Event extension fixture receipt is missing: $receiptPath"
    }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ([string]$receipt.schemaVersion -ne 'unifiedscript.event-extension-fixture/v0.1') {
        throw "Unexpected event extension fixture schemaVersion: $($receipt.schemaVersion)"
    }
    $packageDirectory = [string]$receipt.packageDirectory
    if ($packageDirectory -notmatch '^/Game/UnifiedScriptEventExtensionFixture_[0-9A-Fa-f]{32}$') {
        throw "Receipt packageDirectory is not a dedicated GUID fixture directory: $packageDirectory"
    }
    if ($null -eq $receipt.packages -or @($receipt.packages).Count -ne 6) {
        throw 'Event extension receipt must contain exactly six packages.'
    }

    $contentRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'Content')).TrimEnd('\', '/')
    $fixtureDirectoryName = $packageDirectory.Substring('/Game/'.Length)
    $physicalFixtureDirectory = [IO.Path]::GetFullPath((Join-Path $contentRoot $fixtureDirectoryName))
    Assert-UnderRoot $physicalFixtureDirectory $contentRoot 'Cook fixture directory' | Out-Null
    if (!(Test-Path -LiteralPath $physicalFixtureDirectory -PathType Container)) {
        throw "Receipt fixture directory is missing: $physicalFixtureDirectory"
    }
    $cleanupFixtureDirectory = $physicalFixtureDirectory
    $packageNames = @()
    foreach ($package in @($receipt.packages)) {
        $packageName = [string]$package.packageName
        $packageFilename = [IO.Path]::GetFullPath([string]$package.packageFilename)
        if ([string]::IsNullOrWhiteSpace($packageName) -or
            [IO.Path]::GetExtension($packageFilename) -ne '.uasset' -or
            !(Test-Path -LiteralPath $packageFilename -PathType Leaf) -or
            [IO.Path]::GetDirectoryName($packageFilename) -ne $physicalFixtureDirectory -or
            $packageName -ne "$packageDirectory/$([IO.Path]::GetFileNameWithoutExtension($packageFilename))") {
            throw "Receipt package is not an exact file in its GUID fixture directory: $packageFilename"
        }
        $packageNames += $packageName
        $cleanupPackageFilenames += $packageFilename
    }
    if (@($packageNames | Select-Object -Unique).Count -ne 6) {
        throw 'Event extension receipt contains duplicate package names.'
    }

    $classPaths = @(
        [string]$receipt.nativeActorClassPath,
        [string]$receipt.ordinaryParentActorClassPath,
        [string]$receipt.intermediateActorClassPath,
        [string]$receipt.componentClassPath,
        [string]$receipt.componentChildWithParentClassPath,
        [string]$receipt.componentChildWithoutParentClassPath
    )
    foreach ($classPath in $classPaths) {
        if ([string]::IsNullOrWhiteSpace($classPath) -or
            !($packageNames | Where-Object { $classPath.StartsWith($_ + '.', [StringComparison]::Ordinal) })) {
            throw "Receipt class path is outside its six owned packages: $classPath"
        }
    }
    Write-Output "Fixture receipt validated: $receiptPath"
    Write-Output "Fixture directory validated: $physicalFixtureDirectory"

    if (!$CookAndRun) {
        return
    }

    New-Item -ItemType Directory -Force -Path $archiveRoot | Out-Null
    & $runUat BuildCookRun `
        "-project=$projectFile" `
        '-noP4' `
        '-platform=Win64' `
        '-clientconfig=Development' `
        '-build' `
        '-cook' `
        '-stage' `
        '-pak' `
        '-archive' `
        '-map=/Engine/Maps/Entry' `
        "-CookDir=$physicalFixtureDirectory" `
        "-archivedirectory=$archiveRoot" `
        '-AdditionalCookerOptions=-SkipZenStore' `
        '-unattended' `
        '-utf8output' `
        '-NoCodeSign' `
        '-NoXGE' *>&1 | Tee-Object -FilePath $cookLog
    if ($LASTEXITCODE -ne 0) {
        throw "Event extension BuildCookRun failed with exit code $LASTEXITCODE. Log=$cookLog"
    }

    $packageRoot = Join-Path $archiveRoot 'Windows/UEUnifiedScriptHost'
    $executable = Get-ChildItem -LiteralPath (Join-Path $packageRoot 'Binaries/Win64') -Filter 'UEUnifiedScriptHost*.exe' -File |
        Select-Object -First 1
    if ($null -eq $executable) {
        throw "Packaged Host executable is missing below $packageRoot"
    }
    Delete-ExactFile $resultPath
    $runtimeArgs = @(
        '-game',
        '-unattended',
        '-nop4',
        '-NullRHI',
        '-nosplash',
        '-UnifiedScriptEventExtensionVerify',
        "-UnifiedScriptEventExtensionReceipt=`"$receiptPath`"",
        "-UnifiedScriptEventExtensionResult=`"$resultPath`"",
        "-abslog=`"$runLog`"",
        '-log'
    )
    $runtimeProcess = Start-Process -FilePath $executable.FullName `
        -ArgumentList $runtimeArgs `
        -WorkingDirectory $packageRoot `
        -WindowStyle Hidden `
        -PassThru
    if (!$runtimeProcess.WaitForExit(300000)) {
        $runtimeProcess.Kill()
        throw "Packaged event extension verifier timed out after 300 seconds. Log=$runLog"
    }
    if ($runtimeProcess.ExitCode -ne 0) {
        throw "Packaged event extension verifier failed with exit code $($runtimeProcess.ExitCode). Log=$runLog"
    }
    if (!(Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw "Packaged event extension verifier did not write its result: $resultPath"
    }
    $resultFirstLine = (Get-Content -LiteralPath $resultPath -TotalCount 1).Trim()
    if ($resultFirstLine -ne 'PASS') {
        throw "Packaged event extension verifier did not report PASS: $resultPath"
    }
    Write-Output "Packaged event extension PASS: $resultPath"
}
finally {
    if ($CookAndRun) {
        foreach ($packageFilename in @($cleanupPackageFilenames | Select-Object -Unique)) {
            Delete-ExactFile $packageFilename
        }
        if ($null -ne $cleanupFixtureDirectory -and
            (Test-Path -LiteralPath $cleanupFixtureDirectory -PathType Container) -and
            @(Get-ChildItem -LiteralPath $cleanupFixtureDirectory -Force).Count -eq 0) {
            [IO.Directory]::Delete([IO.Path]::GetFullPath($cleanupFixtureDirectory), $false)
        }
    }
    if ($transcriptStarted) {
        Stop-Transcript | Out-Null
    }
}
