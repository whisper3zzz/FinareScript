[CmdletBinding()]
param(
    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,
    [ValidateSet('Development', 'Shipping')]
    [string[]]$Configurations = @('Development', 'Shipping'),
    [ValidateRange(10, 300)]
    [int]$SmokeTimeoutSeconds = 60,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'HostProject\UEUnifiedScriptHost.uproject'
$managedProject = Join-Path $repoRoot 'samples\Unreal\UnifiedScript.Unreal.Demo\UnifiedScript.Unreal.Demo.csproj'

if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = @('D:\UE_5.8', 'D:\UE\_5.8') |
        Where-Object { Test-Path (Join-Path $_ 'Engine\Build\BatchFiles\RunUAT.bat') } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'Pass -EngineRoot or set UNIFIEDSCRIPT_UE_ROOT.'
}

$runUat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\RunUAT.bat'
$dotnetExe = Join-Path $EngineRoot 'Engine\Binaries\ThirdParty\DotNet\10.0\win-x64\dotnet.exe'
foreach ($requiredPath in @($runUat, $dotnetExe, $projectPath, $managedProject)) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Required release dependency is missing: $requiredPath"
    }
}

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot "artifacts\ue\release\$runId"
} elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}
New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null

$defaultEnginePath = Join-Path $repoRoot 'HostProject\Config\DefaultEngine.ini'
$defaultInputPath = Join-Path $repoRoot 'HostProject\Config\DefaultInput.ini'
$defaultEngineExisted = Test-Path -LiteralPath $defaultEnginePath
$defaultInputExisted = Test-Path -LiteralPath $defaultInputPath
$defaultEngineBytes = if ($defaultEngineExisted) {
    [IO.File]::ReadAllBytes($defaultEnginePath)
} else { $null }
$defaultInputBytes = if ($defaultInputExisted) {
    [IO.File]::ReadAllBytes($defaultInputPath)
} else { $null }

$configurationReports = @()
try {
    Write-Host '[managed] Building and staging the Release demo assemblies...'
    & $dotnetExe build $managedProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Managed release build failed with exit code $LASTEXITCODE."
    }
    & (Join-Path $PSScriptRoot 'emit-unreal-demo-chunk.ps1') `
        -EngineRoot $EngineRoot `
        -Configuration Release `
        -SkipManagedBuild
    if ($LASTEXITCODE -ne 0) {
        throw "Managed release chunk emission failed with exit code $LASTEXITCODE."
    }

    foreach ($configuration in $Configurations) {
        $archiveDirectory = Join-Path $runDirectory $configuration
        Write-Host "[$configuration] Build/Cook/Stage/Pak/Archive..."
        & $runUat BuildCookRun `
            "-project=$projectPath" `
            -noP4 -platform=Win64 "-clientconfig=$configuration" `
            -build -cook -stage -pak -archive `
            "-archivedirectory=$archiveDirectory" `
            '-AdditionalCookerOptions=-SkipZenStore' `
            -unattended -utf8output -NoCodeSign -NoXGE
        if ($LASTEXITCODE -ne 0) {
            throw "$configuration package failed with exit code $LASTEXITCODE."
        }

        $packageRoot = Join-Path $archiveDirectory 'Windows\UEUnifiedScriptHost'
        $executableItem = Get-ChildItem `
            -LiteralPath (Join-Path $packageRoot 'Binaries\Win64') `
            -Filter 'UEUnifiedScriptHost*.exe' -File | Select-Object -First 1
        if ($null -eq $executableItem) {
            throw "$configuration package does not contain its internal game executable."
        }
        $executable = $executableItem.FullName
        $managedAssembly = Join-Path $packageRoot 'Plugins\UEUnifiedScript\Binaries\Managed\UnifiedScript.Unreal.dll'
        $demoAssembly = Join-Path $packageRoot 'Plugins\UEUnifiedScript\Binaries\Managed\Demo\UnifiedScript.Unreal.Demo.dll'
        $demoChunk = Join-Path $packageRoot 'Plugins\UEUnifiedScript\Binaries\Managed\Demo\UnifiedScript.Unreal.Demo.uschunk'
        $runtimeConfig = Join-Path $packageRoot 'Plugins\UEUnifiedScript\Resources\Managed\UnifiedScript.Unreal.runtimeconfig.json'
        foreach ($requiredPackageFile in @($executable, $managedAssembly, $demoAssembly, $demoChunk, $runtimeConfig)) {
            if (!(Test-Path -LiteralPath $requiredPackageFile)) {
                throw "$configuration package is incomplete: $requiredPackageFile"
            }
        }
        $hostFxr = Get-ChildItem `
            -LiteralPath (Join-Path $packageRoot 'Plugins\UEUnifiedScript\Binaries\ThirdParty\DotNet\10.0\win-x64\host\fxr') `
            -Filter 'hostfxr.dll' -Recurse -File | Select-Object -First 1
        $coreClr = Get-ChildItem `
            -LiteralPath (Join-Path $packageRoot 'Plugins\UEUnifiedScript\Binaries\ThirdParty\DotNet\10.0\win-x64\shared\Microsoft.NETCore.App') `
            -Filter 'coreclr.dll' -Recurse -File | Select-Object -First 1
        if ($null -eq $hostFxr -or $null -eq $coreClr) {
            throw "$configuration package does not contain the self-contained CoreCLR runtime."
        }

        $smokeResult = Join-Path $runDirectory "$configuration-smoke.txt"
        $smokeLog = Join-Path $runDirectory "$configuration-smoke.log"
        Write-Host "[$configuration] Running packaged CoreCLR/ABI v3 smoke..."
        $process = Start-Process `
            -FilePath $executable `
            -ArgumentList @(
                '-UnifiedScriptSmoke',
                "-UnifiedScriptSmokeResult=`"$smokeResult`"",
                '-unattended',
                '-nullrhi',
                '-nosplash',
                "-abslog=`"$smokeLog`"",
                '-log') `
            -WindowStyle Hidden `
            -PassThru
        if (!$process.WaitForExit($SmokeTimeoutSeconds * 1000)) {
            Stop-Process -Id $process.Id
            $process.WaitForExit()
            throw "$configuration packaged smoke timed out after $SmokeTimeoutSeconds seconds."
        }
        if (!(Test-Path -LiteralPath $smokeResult)) {
            throw "$configuration packaged smoke did not write a result: $smokeResult"
        }
        $smokeText = Get-Content -Raw -LiteralPath $smokeResult
        if (!$smokeText.StartsWith("PASS`n") -and !$smokeText.StartsWith("PASS`r`n")) {
            throw "$configuration packaged smoke failed: $smokeText"
        }
        $chunkSmoke = [regex]::Match(
            $smokeText,
            'program=([^;\r\n]+\.uschunk); startupMs=([0-9.]+)')
        if (!$chunkSmoke.Success -or [double]$chunkSmoke.Groups[2].Value -le 0) {
            throw "$configuration packaged smoke did not prove compiled-chunk startup: $smokeText"
        }

        $packageFiles = Get-ChildItem -LiteralPath $archiveDirectory -Recurse -File
        $configurationReports += [ordered]@{
            configuration = $configuration
            archiveDirectory = $archiveDirectory
            executable = $executable
            executableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $executable).Hash
            packageFileCount = $packageFiles.Count
            packageBytes = [long](($packageFiles | Measure-Object Length -Sum).Sum)
            hostFxr = $hostFxr.FullName
            coreClr = $coreClr.FullName
            managedAssembly = $managedAssembly
            demoAssembly = $demoAssembly
            demoChunk = $demoChunk
            compiledChunkStartupMilliseconds = [double]$chunkSmoke.Groups[2].Value
            smokeResult = $smokeText.Trim()
            smokeExitCode = $process.ExitCode
            smokeLog = if (Test-Path -LiteralPath $smokeLog) { $smokeLog } else { $null }
        }
        Write-Host "[$configuration] PASS"
    }
} finally {
    if ($defaultEngineExisted) {
        [IO.File]::WriteAllBytes($defaultEnginePath, $defaultEngineBytes)
    } elseif (Test-Path -LiteralPath $defaultEnginePath) {
        Remove-Item -LiteralPath $defaultEnginePath
    }
    if ($defaultInputExisted) {
        [IO.File]::WriteAllBytes($defaultInputPath, $defaultInputBytes)
    } elseif (Test-Path -LiteralPath $defaultInputPath) {
        Remove-Item -LiteralPath $defaultInputPath
    }
}

$reportPath = Join-Path $runDirectory 'release-gate.json'
[ordered]@{
    schemaVersion = 'unifiedscript.unreal-release-gate/v0.1'
    engineRoot = $EngineRoot
    project = $projectPath
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    configurations = $configurationReports
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8

Write-Host "Release gate passed: $reportPath"
