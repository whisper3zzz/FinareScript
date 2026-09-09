[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:/UE_5.8',
    [string]$FixtureOutputRoot = 'artifacts/m13-3c-3-active-class-ops-cook/fixture',
    [string]$ArchiveDirectory = 'artifacts/m13-3c-3-active-class-ops-cook/package',
    [switch]$UseExistingFixture,
    [switch]$CookAndRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Join-Path $repoRoot 'HostProject'
$projectFile = Join-Path $projectRoot 'UEUnifiedScriptHost.uproject'
$editorCmd = Join-Path $EngineRoot 'Engine/Binaries/Win64/UnrealEditor-Cmd.exe'
$buildBat = Join-Path $EngineRoot 'Engine/Build/BatchFiles/Build.bat'
$runUat = Join-Path $EngineRoot 'Engine/Build/BatchFiles/RunUAT.bat'
$fixtureRoot = ([IO.Path]::GetFullPath((Join-Path $repoRoot $FixtureOutputRoot))).TrimEnd([char[]]([IO.Path]::DirectorySeparatorChar + [IO.Path]::AltDirectorySeparatorChar))
$archiveRoot = ([IO.Path]::GetFullPath((Join-Path $repoRoot $ArchiveDirectory))).TrimEnd([char[]]([IO.Path]::DirectorySeparatorChar + [IO.Path]::AltDirectorySeparatorChar))
$artifactRoot = ([IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))).TrimEnd([char[]]([IO.Path]::DirectorySeparatorChar + [IO.Path]::AltDirectorySeparatorChar))
$receiptPath = Join-Path $fixtureRoot 'active-class-ops-cook-receipt.json'
$resultPath = Join-Path $fixtureRoot 'active-class-ops-cook-result.txt'
$fixtureLog = Join-Path $fixtureRoot 'editor-active-class-ops.log'
$driverLog = Join-Path $fixtureRoot 'build-cook-run.log'
$hostBuildLog = Join-Path $fixtureRoot 'host-build-no-ubt-makefiles.log'
$cookLog = Join-Path $fixtureRoot 'packaged-build-cook.log'
$runLog = Join-Path $fixtureRoot 'packaged-active-class-ops.log'
$fixtureReady = $false
$transcriptStarted = $false
$cleanupPackages = @()
$cleanupStageFiles = @()
$cleanupContentDirectory = $null
$cleanupStageDirectory = $null
$generationError = $null

function FullPath([string]$Path) { return ([IO.Path]::GetFullPath($Path)).TrimEnd([char[]]([IO.Path]::DirectorySeparatorChar + [IO.Path]::AltDirectorySeparatorChar)) }
function Assert-UnderRoot([string]$Path, [string]$Root, [string]$Label) {
    $full = FullPath $Path; $base = FullPath $Root
    if ($full -eq $base -or
        $full.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($base + [IO.Path]::AltDirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { return $full }
    throw "$Label '$full' is outside '$base'."
}
function Delete-ExactFile([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        [IO.File]::Delete((FullPath $Path)); Write-Output "Deleted owned file: $(FullPath $Path)"
    }
}
function Delete-EmptyDirectory([string]$Path) {
    if ((Test-Path -LiteralPath $Path -PathType Container) -and (@(Get-ChildItem -LiteralPath $Path -Force).Count -eq 0)) {
        [IO.Directory]::Delete((FullPath $Path), $false)
    }
}
function Assert-ActiveClassOpsRuntimeDependencies([string]$TargetReceiptPath, [string]$FixtureId, [string]$StageDirectory) {
    if (!(Test-Path -LiteralPath $TargetReceiptPath -PathType Leaf)) { throw "Game target receipt is missing after Build.bat: $TargetReceiptPath" }
    $target = Get-Content -LiteralPath $TargetReceiptPath -Raw | ConvertFrom-Json
    $runtimeDependencies = @($target.RuntimeDependencies)
    $activeEntries = @($runtimeDependencies | Where-Object { ([string]$_.Path) -match '/Plugins/UEUnifiedScript/Binaries/Managed/ActiveClassOps/' })
    $expectedLeaves = @(
        'ActiveClassAssetOperationsFixture.uschunk',
        'ActiveClassAssetOperationsFixture.dll',
        'ActiveClassAssetOperationsFixture.asset.json')
    if ($activeEntries.Count -ne 3) { throw "Game target receipt has $($activeEntries.Count) ActiveClassOps runtime dependencies; expected exactly 3 for $FixtureId." }
    $actualPaths = @($activeEntries | ForEach-Object { [string]$_.Path })
    foreach ($leaf in $expectedLeaves) {
        $expectedRaw = "`$(ProjectDir)/Plugins/UEUnifiedScript/Binaries/Managed/ActiveClassOps/$FixtureId/$leaf"
        if (@($actualPaths | Where-Object { $_ -ceq $expectedRaw }).Count -ne 1) { throw "Game target receipt is missing the current ActiveClassOps dependency '$expectedRaw'." }
        $physical = Join-Path $StageDirectory $leaf
        if (!(Test-Path -LiteralPath $physical -PathType Leaf)) { throw "Current ActiveClassOps runtime dependency is missing on disk: $physical" }
    }
    foreach ($path in $actualPaths) {
        if ($path -notmatch "/$FixtureId/") { throw "Game target receipt contains a stale ActiveClassOps dependency: $path" }
    }
}

if (!(Test-Path -LiteralPath $editorCmd) -or !(Test-Path -LiteralPath $buildBat) -or !(Test-Path -LiteralPath $runUat)) { throw "UE tools are missing below '$EngineRoot'." }
if (!(Test-Path -LiteralPath $projectFile)) { throw "Host project is missing: $projectFile" }
Assert-UnderRoot $fixtureRoot $artifactRoot 'Fixture output root' | Out-Null
Assert-UnderRoot $archiveRoot $artifactRoot 'Archive output root' | Out-Null
if ($fixtureRoot.Equals($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or $archiveRoot.Equals($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output roots must be below artifacts.' }
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
Start-Transcript -LiteralPath $driverLog -Force | Out-Null; $transcriptStarted = $true

try {
    if (!$UseExistingFixture) {
        if (Test-Path -LiteralPath $receiptPath -PathType Leaf) { throw "An existing receipt owns retained active fixture files; use -UseExistingFixture: $receiptPath" }
        Delete-ExactFile $receiptPath; Delete-ExactFile $resultPath; Delete-ExactFile $fixtureLog
        & $editorCmd $projectFile '-unattended' '-nop4' '-NullRHI' '-nosplash' '-NoConfigChanges' `
            '-ExecCmds=Automation RunTests UnifiedScript.Unreal.ScriptClass.ActiveClassAssetOperations' `
            '-TestExit=Automation Test Queue Empty' `
            "-UnifiedScriptActiveClassOpsCookReceipt=$receiptPath" `
            "-abslog=$fixtureLog" '-log'
        if ($LASTEXITCODE -ne 0) { $generationError = "Active fixture generation failed with exit code $LASTEXITCODE. Log=$fixtureLog" }
        elseif (!(Select-String -LiteralPath $fixtureLog -Pattern 'Test Completed\. Result=\{Success\} Name=\{ActiveClassAssetOperations\}' -Quiet)) { $generationError = "Active fixture selector did not report Success: $fixtureLog" }
        elseif (!(Select-String -LiteralPath $fixtureLog -Pattern 'Automation Test Queue Empty [1-9][0-9]* tests performed\.' -Quiet)) { $generationError = "Active fixture selector did not report a nonzero queue: $fixtureLog" }
    }
    if (!(Test-Path -LiteralPath $receiptPath -PathType Leaf)) { if ($generationError) { throw $generationError }; throw "Active cook receipt is missing: $receiptPath" }
    if ($UseExistingFixture) {
        if (!(Test-Path -LiteralPath $fixtureLog -PathType Leaf) -or
            !(Select-String -LiteralPath $fixtureLog -Pattern 'Test Completed\. Result=\{Success\} Name=\{ActiveClassAssetOperations\}' -Quiet) -or
            !(Select-String -LiteralPath $fixtureLog -Pattern 'Automation Test Queue Empty [1-9][0-9]* tests performed\.' -Quiet)) {
            $generationError = "Existing active fixture lacks a named Success and nonzero Automation Test Queue Empty log: $fixtureLog"
        }
    }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    $fixtureId = [string]$receipt.fixtureId
    $packageDirectory = [string]$receipt.packageDirectory
    if ($fixtureId -cnotmatch '^[0-9A-F]{32}$' -or $fixtureId -ceq ('0' * 32) -or !$packageDirectory.Equals("/Game/M13C3ActiveClassOps_$fixtureId", [StringComparison]::Ordinal) -or
        [string]$receipt.schemaVersion -cne 'unifiedscript.active-class-ops-cook-fixture/v0.1' -or
        [string]$receipt.stageDirectory -cne "{PluginDir}/Binaries/Managed/ActiveClassOps/$fixtureId") { throw 'Active receipt identity/schema is invalid.' }
    $contentRoot = FullPath (Join-Path $projectRoot 'Content')
    $contentLeaf = $packageDirectory.Substring('/Game/'.Length)
    $fixtureDirectory = Assert-UnderRoot (Join-Path $contentRoot $contentLeaf) $contentRoot 'Active cook content directory'
    $stageRoot = FullPath (Join-Path $projectRoot 'Plugins/UEUnifiedScript/Binaries/Managed/ActiveClassOps')
    $stageDirectory = Assert-UnderRoot (Join-Path $stageRoot $fixtureId) $stageRoot 'Active managed stage directory'
    $cleanupContentDirectory = $fixtureDirectory; $cleanupStageDirectory = $stageDirectory
    $expectedBranches = @('renamed', 'duplicate')
    $branches = @($receipt.branches)
    if ($branches.Count -ne 2 -or [string]$branches[0].role -eq [string]$branches[1].role -or
        !($expectedBranches -ccontains [string]$branches[0].role) -or !($expectedBranches -ccontains [string]$branches[1].role)) { throw 'Active receipt must contain renamed and duplicate branches.' }
    $sourceRoot = Assert-UnderRoot (Join-Path $artifactRoot ("m13-3c-3-active-asset-operations/$fixtureId")) $artifactRoot 'Active fixture source root'
    $expectedSourcePaths = @{
        chunk = FullPath (Join-Path $sourceRoot 'declaration/ActiveClassAssetOperationsFixture.uschunk')
        assembly = FullPath (Join-Path $sourceRoot 'publish/ActiveClassAssetOperationsFixture.dll')
        declaration = FullPath (Join-Path $sourceRoot 'declaration/ActiveClassAssetOperationsFixture.asset.json')
    }
    if ((FullPath ([string]$receipt.chunkPath)) -cne $expectedSourcePaths.chunk -or
        (FullPath ([string]$receipt.assemblyPath)) -cne $expectedSourcePaths.assembly -or
        (FullPath ([string]$receipt.declarationPath)) -cne $expectedSourcePaths.declaration) { throw 'Active receipt program sources do not match the GUID-owned fixture output.' }
    $fixtureNamespace = 'UnifiedScript.Tests.Fixtures.ActiveClassAssetOperations'
    $expectedPackageNames = @(
        "$packageDirectory/ActiveBase_${fixtureId}_Renamed",
        "$packageDirectory/ActiveChild_$fixtureId",
        "$packageDirectory/ActiveGrandchild_$fixtureId",
        "$packageDirectory/SA_ActiveBase_${fixtureId}_Renamed",
        "$packageDirectory/ActiveBase_${fixtureId}_Duplicate",
        "$packageDirectory/ActiveBase_${fixtureId}_Duplicate_Child",
        "$packageDirectory/ActiveBase_${fixtureId}_Duplicate_Grandchild",
        "$packageDirectory/SA_ActiveBase_${fixtureId}_Duplicate")
    foreach ($branch in $branches) {
        foreach ($field in @('baseClassPath','childClassPath','grandchildClassPath','scriptAssetPath','classId','typeName','declarationAssetPath')) {
            if ([string]::IsNullOrWhiteSpace([string]$branch.$field)) { throw "Active receipt branch '$($branch.role)' is missing $field." }
        }
        if ([string]$branch.classId -cnotmatch '^us_[0-9a-f]{32}$') { throw "Active receipt branch '$($branch.role)' has invalid ClassId." }
        $expectedBaseLeaf = if ([string]$branch.role -eq 'renamed') { "ActiveBase_${fixtureId}_Renamed" } else { "ActiveBase_${fixtureId}_Duplicate" }
        $expectedChildLeaf = if ([string]$branch.role -eq 'renamed') { "ActiveChild_$fixtureId" } else { "ActiveBase_${fixtureId}_Duplicate_Child" }
        $expectedGrandchildLeaf = if ([string]$branch.role -eq 'renamed') { "ActiveGrandchild_$fixtureId" } else { "ActiveBase_${fixtureId}_Duplicate_Grandchild" }
        $expectedAssetLeaf = "SA_$expectedBaseLeaf"
        $expectedBasePath = "$packageDirectory/$expectedBaseLeaf.$($expectedBaseLeaf)_C"
        $expectedChildPath = "$packageDirectory/$expectedChildLeaf.$($expectedChildLeaf)_C"
        $expectedGrandchildPath = "$packageDirectory/$expectedGrandchildLeaf.$($expectedGrandchildLeaf)_C"
        $expectedAssetPath = "$packageDirectory/$expectedAssetLeaf.$expectedAssetLeaf"
        if ([string]$branch.baseClassPath -cne $expectedBasePath -or [string]$branch.childClassPath -cne $expectedChildPath -or
            [string]$branch.grandchildClassPath -cne $expectedGrandchildPath -or [string]$branch.scriptAssetPath -cne $expectedAssetPath -or
            [string]$branch.typeName -cne "$fixtureNamespace.$expectedBaseLeaf" -or [string]$branch.declarationAssetPath -cne "UEUnifiedScriptHost/$expectedBaseLeaf") { throw "Active receipt branch '$($branch.role)' has unexpected class/type/selector layout." }
        if ([Guid]::Parse(([string]$branch.classId).Substring(3)) -eq [Guid]::Empty) { throw "Active receipt branch '$($branch.role)' has a zero ClassId." }
    }
    if ([string]$branches[0].classId -ceq [string]$branches[1].classId) { throw 'Active renamed and duplicate branches must have distinct ClassIds.' }
    $packages = @($receipt.packages)
    if ($packages.Count -ne 8) { throw 'Active receipt must contain exactly eight packages.' }
    foreach ($package in $packages) {
        $name = [string]$package.packageName; $filename = FullPath ([string]$package.packageFilename)
        $filenameLeaf = [IO.Path]::GetFileNameWithoutExtension($filename)
        if ($name -cnotin $expectedPackageNames -or $name -cne "$packageDirectory/$filenameLeaf" -or [IO.Path]::GetExtension($filename) -cne '.uasset' -or
            [IO.Path]::GetDirectoryName($filename) -ne $fixtureDirectory -or !(Test-Path -LiteralPath $filename -PathType Leaf)) { throw "Invalid active receipt package: $filename" }
        $cleanupPackages += $filename
    }
    if (@($packages.packageName | Select-Object -Unique).Count -ne 8 -or @($expectedPackageNames | Select-Object -Unique).Count -ne 8) { throw 'Active receipt package set is not exact.' }
    $diskPackages = @(Get-ChildItem -LiteralPath $fixtureDirectory -Filter '*.uasset' -File)
    $actualDiskLeaves = (@($diskPackages.BaseName | Sort-Object) -join '|')
    $expectedDiskLeaves = (@($packages | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension([string]$_.packageFilename) } | Sort-Object) -join '|')
    if ($diskPackages.Count -ne 8 -or $actualDiskLeaves -cne $expectedDiskLeaves) { throw 'Active fixture content contains packages outside the exact eight-package receipt.' }
    foreach ($item in @(
        @{ Source = [string]$receipt.chunkPath; Leaf = 'ActiveClassAssetOperationsFixture.uschunk' },
        @{ Source = [string]$receipt.assemblyPath; Leaf = 'ActiveClassAssetOperationsFixture.dll' },
        @{ Source = [string]$receipt.declarationPath; Leaf = 'ActiveClassAssetOperationsFixture.asset.json' })) {
        $source = Assert-UnderRoot $item.Source $sourceRoot 'Active program source'
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Active program source is missing: $source" }
        $target = Assert-UnderRoot (Join-Path $stageDirectory $item.Leaf) $stageDirectory 'Active staged program'
        if (!(Test-Path -LiteralPath $target -PathType Leaf)) { throw "Active staged program is missing: $target" }
        $cleanupStageFiles += $target
    }
    $fixtureReady = $true
    Write-Output "Active cook receipt validated: $receiptPath"
    if ($generationError) { throw $generationError }
    if (!$CookAndRun) { return }

    Delete-ExactFile $resultPath; New-Item -ItemType Directory -Force -Path $archiveRoot | Out-Null
    & $buildBat 'UEUnifiedScriptHost' 'Win64' 'Development' "-Project=$projectFile" '-NoUBTMakefiles' '-WaitMutex' '-NoHotReloadFromIDE' *>&1 | Tee-Object -FilePath $hostBuildLog
    if ($LASTEXITCODE -ne 0) { throw "UEUnifiedScriptHost Build.bat failed with exit code $LASTEXITCODE. Log=$hostBuildLog" }
    Assert-ActiveClassOpsRuntimeDependencies (Join-Path $projectRoot 'Binaries/Win64/UEUnifiedScriptHost.target') $fixtureId $stageDirectory
    $cookDirectory = Assert-UnderRoot $fixtureDirectory $contentRoot 'Active CookDir'
    & $runUat BuildCookRun "-project=$projectFile" '-noP4' '-platform=Win64' '-clientconfig=Development' '-cook' '-stage' '-pak' '-archive' `
        '-map=/Engine/Maps/Entry' "-CookDir=$cookDirectory" "-archivedirectory=$archiveRoot" '-AdditionalCookerOptions=-SkipZenStore' `
        '-unattended' '-utf8output' '-NoCodeSign' '-NoXGE' '-UbtArgs=-NoUBTMakefiles' *>&1 | Tee-Object -FilePath $cookLog
    if ($LASTEXITCODE -ne 0) { throw "BuildCookRun failed with exit code $LASTEXITCODE." }
    $packageRoot = Join-Path $archiveRoot 'Windows/UEUnifiedScriptHost'
    $executables = @(Get-ChildItem -LiteralPath (Join-Path $packageRoot 'Binaries/Win64') -Filter 'UEUnifiedScriptHost.exe' -File)
    if ($executables.Count -ne 1) { throw "Expected exactly one UEUnifiedScriptHost.exe below $packageRoot." }
    $runtimeArgs = @('/Engine/Maps/Entry','-game','-unattended','-nop4','-NullRHI','-nosplash','-UnifiedScriptActiveClassOpsVerify',
        "-UnifiedScriptActiveClassOpsReceipt=`"$receiptPath`"", "-UnifiedScriptActiveClassOpsResult=`"$resultPath`"", "-abslog=`"$runLog`"", '-log')
    $process = Start-Process -FilePath $executables[0].FullName -ArgumentList $runtimeArgs -WorkingDirectory $packageRoot -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(300000)) { $process.Kill(); throw "Packaged active verifier timed out. Log=$runLog" }
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $resultPath -PathType Leaf) -or (Get-Content -LiteralPath $resultPath -TotalCount 1).Trim() -ne 'PASS') { throw "Packaged active verifier failed. Log=$runLog" }
    if (!(Select-String -LiteralPath $runLog -Pattern 'UNIFIEDSCRIPT_ACTIVE_CLASS_OPS PASS' -Quiet) -or !(Select-String -LiteralPath $runLog -Pattern 'ActiveClassOps' -Quiet)) { throw "Packaged active verifier lacks PASS/archive path evidence: $runLog" }
    Write-Output "Cooked active class operations PASS: $resultPath"
}
finally {
    if ($CookAndRun -or ($fixtureReady -and $null -ne $generationError)) {
        foreach ($file in @($cleanupPackages | Select-Object -Unique)) { Delete-ExactFile $file }
        foreach ($file in @($cleanupStageFiles | Select-Object -Unique)) { Delete-ExactFile $file }
        if ($null -ne $cleanupContentDirectory) { Delete-EmptyDirectory $cleanupContentDirectory }
        if ($null -ne $cleanupStageDirectory) { Delete-EmptyDirectory $cleanupStageDirectory }
    }
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
}
