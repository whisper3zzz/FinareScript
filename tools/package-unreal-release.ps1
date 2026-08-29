[CmdletBinding()]
param(
    [string]$EngineRoot = $env:UNIFIEDSCRIPT_UE_ROOT,
    [string]$Project,
    [ValidateSet('Development', 'Shipping')]
    [string[]]$Configurations = @('Development', 'Shipping'),
    [ValidateRange(10, 300)]
    [int]$SmokeTimeoutSeconds = 60,
    [string]$OutputDirectory,
    [string]$ExpectedEngineVersion = '5.8.1',
    [switch]$SkipManagedBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = if ([string]::IsNullOrWhiteSpace($Project)) {
    Join-Path $repoRoot 'HostProject\UEUnifiedScriptHost.uproject'
} else {
    [IO.Path]::GetFullPath($Project)
}
$managedProjectPath = Join-Path $repoRoot `
    'samples\Unreal\UnifiedScript.Unreal.Demo\UnifiedScript.Unreal.Demo.csproj'
$projectName = [IO.Path]::GetFileNameWithoutExtension($projectPath)
$projectRoot = Split-Path -Parent $projectPath

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
$buildVersionPath = Join-Path $EngineRoot 'Engine\Build\Build.version'
$requiredPaths = @($runUat, $dotnetExe, $projectPath, $buildVersionPath)
if (!$SkipManagedBuild) {
    $requiredPaths += $managedProjectPath
}
foreach ($requiredPath in $requiredPaths) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Required release dependency is missing: $requiredPath"
    }
}
$buildVersion = Get-Content -LiteralPath $buildVersionPath -Raw | ConvertFrom-Json
$engineVersion = '{0}.{1}.{2}' -f `
    $buildVersion.MajorVersion, `
    $buildVersion.MinorVersion, `
    $buildVersion.PatchVersion
if (![string]::IsNullOrWhiteSpace($ExpectedEngineVersion) -and
    $engineVersion -ne $ExpectedEngineVersion) {
    throw "Release gate requires Unreal Engine $ExpectedEngineVersion, but '$EngineRoot' is $engineVersion."
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

function Find-ReleaseFileWithRetry {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Filter,
        [ValidateRange(1, 30)][int]$TimeoutSeconds = 15
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path -LiteralPath $Directory -PathType Container) {
            $candidate = Get-ChildItem `
                -LiteralPath $Directory `
                -Filter $Filter `
                -Recurse `
                -File `
                -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($null -ne $candidate) {
                return $candidate
            }
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    return $null
}

$defaultEnginePath = Join-Path $projectRoot 'Config\DefaultEngine.ini'
$defaultInputPath = Join-Path $projectRoot 'Config\DefaultInput.ini'
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
    if (!$SkipManagedBuild) {
        Write-Host '[managed] Building and staging the Release demo chunk...'
        & (Join-Path $PSScriptRoot 'emit-unreal-demo-chunk.ps1') `
            -EngineRoot $EngineRoot `
            -Configuration Release
        if ($LASTEXITCODE -ne 0) {
            throw "Managed release chunk emission failed with exit code $LASTEXITCODE."
        }
    } else {
        Write-Host '[managed] Using the caller-staged managed runtime and demo chunk.'
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

        $packageRoot = Join-Path $archiveDirectory "Windows\$projectName"
        $executableItem = Get-ChildItem `
            -LiteralPath (Join-Path $packageRoot 'Binaries\Win64') `
            -Filter "$projectName*.exe" -File | Select-Object -First 1
        if ($null -eq $executableItem) {
            throw "$configuration package does not contain its internal game executable."
        }
        $executable = $executableItem.FullName
        $managedAssembly = Join-Path $packageRoot 'Plugins\UEUnifiedScript\Binaries\Managed\UnifiedScript.Unreal.dll'
        $demoAssembly = Join-Path $packageRoot 'Plugins\UEUnifiedScript\Binaries\Managed\Demo\UnifiedScript.Unreal.Demo.dll'
        $demoChunk = Join-Path $packageRoot 'Plugins\UEUnifiedScript\Binaries\Managed\Demo\UnifiedScript.Unreal.Demo.uschunk'
        $runtimeConfig = Join-Path $packageRoot 'Plugins\UEUnifiedScript\Resources\Managed\UnifiedScript.Unreal.runtimeconfig.json'
        $managedRoot = Join-Path $packageRoot 'Plugins\UEUnifiedScript\Binaries\Managed'
        $runtimeRoot = Join-Path $packageRoot `
            'Plugins\UEUnifiedScript\Binaries\ThirdParty\DotNet\10.0\win-x64'
        $requiredManagedFiles = @(
            (Join-Path $managedRoot 'UnifiedScript.Core.dll'),
            (Join-Path $managedRoot 'UnifiedScript.Runtime.dll'),
            (Join-Path $managedRoot 'UnifiedScript.Unreal.dll'),
            (Join-Path $managedRoot 'UnifiedScript.Unreal.deps.json'))
        foreach ($requiredPackageFile in @(
                $executable,
                $demoAssembly,
                $demoChunk,
                $runtimeConfig) + $requiredManagedFiles) {
            if (!(Test-Path -LiteralPath $requiredPackageFile)) {
                throw "$configuration package is incomplete: $requiredPackageFile"
            }
        }
        $hostFxrDirectory = Join-Path $runtimeRoot 'host\fxr'
        $sharedRuntimeDirectory = Join-Path $runtimeRoot `
            'shared\Microsoft.NETCore.App'
        $hostFxr = Find-ReleaseFileWithRetry `
            -Directory $hostFxrDirectory `
            -Filter 'hostfxr.dll'
        $coreClr = Find-ReleaseFileWithRetry `
            -Directory $sharedRuntimeDirectory `
            -Filter 'coreclr.dll'
        $hostPolicy = Find-ReleaseFileWithRetry `
            -Directory $sharedRuntimeDirectory `
            -Filter 'hostpolicy.dll'
        $privateCoreLib = Find-ReleaseFileWithRetry `
            -Directory $sharedRuntimeDirectory `
            -Filter 'System.Private.CoreLib.dll'
        $systemRuntime = Find-ReleaseFileWithRetry `
            -Directory $sharedRuntimeDirectory `
            -Filter 'System.Runtime.dll'
        if ($null -eq $hostFxr -or $null -eq $coreClr -or
            $null -eq $hostPolicy -or $null -eq $privateCoreLib -or
            $null -eq $systemRuntime) {
            throw "$configuration package does not contain the self-contained CoreCLR runtime."
        }
        $pakFiles = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.pak' -Recurse -File)
        $utocFiles = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.utoc' -Recurse -File)
        $ucasFiles = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.ucas' -Recurse -File)
        if ($pakFiles.Count -eq 0 -or $utocFiles.Count -eq 0 -or $ucasFiles.Count -eq 0) {
            throw "$configuration package is missing Pak/IoStore outputs."
        }

        $smokeResult = Join-Path $runDirectory "$configuration-smoke.txt"
        $smokeLog = Join-Path $runDirectory "$configuration-smoke.log"
        Write-Host "[$configuration] Running packaged CoreCLR/ABI v3 smoke..."
        $previousDotNetRoot = $env:DOTNET_ROOT
        $previousUnifiedScriptDotNetRoot = $env:UNIFIEDSCRIPT_DOTNET_ROOT
        try {
            $env:DOTNET_ROOT = Join-Path $runDirectory 'intentionally-missing-dotnet-root'
            $env:UNIFIEDSCRIPT_DOTNET_ROOT = Join-Path `
                $runDirectory `
                'intentionally-missing-unifiedscript-dotnet-root'
            $process = Start-Process `
                -FilePath $executable `
                -WorkingDirectory (Split-Path -Parent $executable) `
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
        } finally {
            $env:DOTNET_ROOT = $previousDotNetRoot
            $env:UNIFIEDSCRIPT_DOTNET_ROOT = $previousUnifiedScriptDotNetRoot
        }
        if ($process.ExitCode -ne 0) {
            throw "$configuration packaged smoke exited with code $($process.ExitCode)."
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
        if ($smokeText -notmatch
            'hostfxr=.*UEUnifiedScript[/\\]Binaries[/\\]ThirdParty[/\\]DotNet[/\\]10\.0') {
            throw "$configuration packaged smoke did not use the plugin-local CoreCLR runtime: $smokeText"
        }

        $packageFiles = Get-ChildItem -LiteralPath $archiveDirectory -Recurse -File
        $runtimeFiles = @(Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File)
        $configurationReports += [ordered]@{
            configuration = $configuration
            targetType = 'Game'
            targetPlatform = 'Win64'
            build = 'passed'
            cook = 'passed'
            stage = 'passed'
            pak = 'passed'
            archive = 'passed'
            archiveDirectory = $archiveDirectory
            executable = $executable
            executableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $executable).Hash
            packageFileCount = $packageFiles.Count
            packageBytes = [long](($packageFiles | Measure-Object Length -Sum).Sum)
            hostFxr = $hostFxr.FullName
            hostPolicy = $hostPolicy.FullName
            coreClr = $coreClr.FullName
            privateCoreLib = $privateCoreLib.FullName
            systemRuntime = $systemRuntime.FullName
            runtimeFileCount = $runtimeFiles.Count
            runtimeBytes = [long](($runtimeFiles | Measure-Object Length -Sum).Sum)
            managedAssembly = $managedAssembly
            demoAssembly = $demoAssembly
            demoChunk = $demoChunk
            compiledChunkStartupMilliseconds = [double]$chunkSmoke.Groups[2].Value
            pluginLocalRuntime = $true
            poisonedExternalDotNetRoots = $true
            pakFileCount = $pakFiles.Count
            utocFileCount = $utocFiles.Count
            ucasFileCount = $ucasFiles.Count
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
    schemaVersion = 'unifiedscript.unreal-release-gate/v0.2'
    status = 'passed'
    engine = [ordered]@{
        root = [IO.Path]::GetFullPath($EngineRoot)
        version = $engineVersion
        changelist = [long]$buildVersion.Changelist
        branch = [string]$buildVersion.BranchName
    }
    project = $projectPath
    projectName = $projectName
    managedStage = if ($SkipManagedBuild) { 'caller' } else { 'source' }
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    configurations = $configurationReports
} | ConvertTo-Json -Depth 8 | ForEach-Object {
    [IO.File]::WriteAllText(
        $reportPath,
        $_ + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

Write-Host "Release gate passed: $reportPath"
