# Prerequisites: the UE SDK10 Release Demo/chunk and Host Editor build already exist.
# UseExistingFixture reuses the retained passing Editor fixture; omit it only to regenerate one.
[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:/UE_5.8',
    [string]$FixtureOutputRoot = 'artifacts/m13-3c-1c-b-function-closure/fixture-output',
    [string]$ArchiveDirectory = 'artifacts/m13-3c-1c-b-function-closure/package',
    [switch]$UseExistingFixture,
    [switch]$CookAndRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Join-Path $repoRoot 'HostProject'
$projectFile = Join-Path $projectRoot 'FinareHost.uproject'
$editorCmd = Join-Path $EngineRoot 'Engine/Binaries/Win64/UnrealEditor-Cmd.exe'
$runUat = Join-Path $EngineRoot 'Engine/Build/BatchFiles/RunUAT.bat'
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $FixtureOutputRoot))
$archiveRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $ArchiveDirectory))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$fixtureLog = Join-Path $fixtureRoot 'editor-fixture-generation.log'
$receiptPath = Join-Path $fixtureRoot 'function-closure-receipt.json'
$resultPath = Join-Path $fixtureRoot 'function-closure-result.txt'

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

if (!(Test-Path -LiteralPath $editorCmd) -or !(Test-Path -LiteralPath $runUat)) {
    throw "UE 5.8 Editor/UAT tools are missing below '$EngineRoot'."
}
if (!(Test-Path -LiteralPath $projectFile)) {
    throw "Host project is missing: $projectFile"
}
Assert-UnderRoot $fixtureRoot $artifactRoot 'Fixture output root' | Out-Null
Assert-UnderRoot $archiveRoot $artifactRoot 'Archive directory' | Out-Null
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null

if (!$UseExistingFixture) {
    foreach ($stalePath in @($receiptPath, $fixtureLog, $resultPath)) {
        if (Test-Path -LiteralPath $stalePath) {
            [IO.File]::Delete($stalePath)
        }
    }

    & $editorCmd $projectFile `
        '-unattended' `
        '-nop4' `
        '-NullRHI' `
        '-nosplash' `
        '-NoConfigChanges' `
        '-ExecCmds=Automation RunTests Finare.Unreal.ScriptClass.BlueprintManagedStaticFunctionDispatch' `
        '-TestExit=Automation Test Queue Empty' `
        "-FinareFunctionFixtureOutput=$fixtureRoot" `
        "-abslog=$fixtureLog" `
        '-log'
    $fixtureExit = $LASTEXITCODE
    if ($fixtureExit -ne 0) {
        throw "Function closure fixture generation failed with exit code $fixtureExit. Log=$fixtureLog"
    }
    if (!(Select-String -LiteralPath $fixtureLog -Pattern 'Test Completed\. Result=\{Success\} Name=\{BlueprintManagedStaticFunctionDispatch\}' -Quiet)) {
        throw "Fixture generation log does not contain the required Success marker: $fixtureLog"
    }
}
if (!(Test-Path -LiteralPath $receiptPath)) {
    throw "Fixture receipt is missing: $receiptPath"
}

$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
$expectedSchemaVersion = 'unifiedscript.function-closure-fixture/v0.1'
if ([string]$receipt.schemaVersion -ne $expectedSchemaVersion) {
    throw "Fixture receipt schemaVersion is not '$expectedSchemaVersion': $receiptPath"
}
$contentRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'Content')).TrimEnd('\', '/')
$packageFilename = [IO.Path]::GetFullPath([string]$receipt.packageFilename)
$packageDirectory = [IO.Path]::GetDirectoryName($packageFilename)
$relativeFixtureDirectory = [IO.Path]::GetRelativePath($contentRoot, $packageDirectory)
if ($relativeFixtureDirectory -notmatch '^FinareFunctionFixture_[0-9A-Fa-f]{32}$' -or
    [IO.Path]::GetExtension($packageFilename) -ne '.uasset' -or
    !(Test-Path -LiteralPath $packageFilename)) {
    throw "Receipt package is not a retained dedicated fixture under HostProject/Content: $packageFilename"
}
$packageName = [string]$receipt.packageName
if ($relativeFixtureDirectory -eq '.' -or
    [string]::IsNullOrWhiteSpace($packageName) -or
    $packageName -ne "/Game/$relativeFixtureDirectory/$([IO.Path]::GetFileNameWithoutExtension($packageFilename))" -or
    !([string]$receipt.libraryClassPath).StartsWith($packageName + '.', [StringComparison]::Ordinal) -or
    !([string]$receipt.callerClassPath).StartsWith($packageName + '.', [StringComparison]::Ordinal)) {
    throw 'Receipt class paths do not belong to its exact fixture package.'
}

Write-Output "Fixture receipt: $receiptPath"
Write-Output "Fixture package: $packageFilename"
if (!$CookAndRun) {
    exit 0
}

New-Item -ItemType Directory -Force -Path $archiveRoot | Out-Null
$runLog = Join-Path $fixtureRoot 'packaged-function-closure.log'
try {
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
        "-CookDir=$packageDirectory" `
        "-archivedirectory=$archiveRoot" `
        '-AdditionalCookerOptions=-SkipZenStore' `
        '-unattended' `
        '-utf8output' `
        '-NoCodeSign' `
        '-NoXGE'
    if ($LASTEXITCODE -ne 0) {
        throw "Function closure BuildCookRun failed with exit code $LASTEXITCODE. Archive=$archiveRoot"
    }

    $packageRoot = Join-Path $archiveRoot 'Windows/FinareHost'
    $executable = Get-ChildItem -LiteralPath (Join-Path $packageRoot 'Binaries/Win64') -Filter 'FinareHost*.exe' -File |
        Select-Object -First 1
    if ($null -eq $executable) {
        throw "Packaged Host executable is missing below $packageRoot"
    }

    if (Test-Path -LiteralPath $resultPath) {
        [IO.File]::Delete($resultPath)
    }
    $runtimeArgs = @(
        '-unattended',
        '-nop4',
        '-NullRHI',
        '-nosplash',
        '-FinareFunctionClosureVerify',
        "-FinareFunctionClosureReceipt=`"$receiptPath`"",
        "-FinareFunctionClosureResult=`"$resultPath`"",
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
        throw "Packaged function closure verifier timed out after 300 seconds. Log=$runLog"
    }
    $runtimeExit = $runtimeProcess.ExitCode
    if ($runtimeExit -ne 0) {
        throw "Packaged function closure verifier failed with exit code $runtimeExit. Log=$runLog"
    }
    if (!(Test-Path -LiteralPath $resultPath)) {
        throw "Packaged function closure verifier did not write its result file: $resultPath"
    }
    $resultFirstLine = Get-Content -LiteralPath $resultPath -TotalCount 1
    if ($resultFirstLine -ne 'PASS') {
        throw "Packaged function closure verifier did not report PASS: $resultPath"
    }
    Write-Output "Packaged function closure PASS: $resultPath"
}
finally {
    if (Test-Path -LiteralPath $packageFilename) {
        Remove-Item -LiteralPath $packageFilename -Force
    }
    if ((Test-Path -LiteralPath $packageDirectory) -and
        @(Get-ChildItem -LiteralPath $packageDirectory -Force).Count -eq 0) {
        Remove-Item -LiteralPath $packageDirectory -Force
    }
}
