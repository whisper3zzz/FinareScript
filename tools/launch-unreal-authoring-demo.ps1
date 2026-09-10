[CmdletBinding()]
param(
    [string]$EngineRoot = $env:FINARE_UE_ROOT,
    [ValidateRange(1024, 65535)]
    [int]$Port = 4173,
    [switch]$SkipNativeBuild,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'HostProject\FinareHost.uproject'
$authoringScript = Join-Path $PSScriptRoot 'run-unreal-authoring.ps1'
$artifactDirectory = Join-Path $repoRoot 'artifacts\ue\authoring'

if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    $EngineRoot = @('D:\UE_5.8', 'D:\UE\_5.8') |
        Where-Object { Test-Path (Join-Path $_ 'Engine\Build\BatchFiles\Build.bat') } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($EngineRoot)) {
    throw 'Pass -EngineRoot or set FINARE_UE_ROOT.'
}
$buildBat = Join-Path $EngineRoot 'Engine\Build\BatchFiles\Build.bat'
$editor = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor.exe'
foreach ($requiredPath in @($buildBat, $editor, $authoringScript, $projectPath)) {
    if (!(Test-Path -LiteralPath $requiredPath)) {
        throw "Required demo dependency is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
$defaultEnginePath = Join-Path $repoRoot 'HostProject\Config\DefaultEngine.ini'
$defaultInputPath = Join-Path $repoRoot 'HostProject\Config\DefaultInput.ini'
$defaultEngineBytes = if (Test-Path -LiteralPath $defaultEnginePath) {
    [IO.File]::ReadAllBytes($defaultEnginePath)
} else { $null }
$defaultInputBytes = if (Test-Path -LiteralPath $defaultInputPath) {
    [IO.File]::ReadAllBytes($defaultInputPath)
} else { $null }

$server = $null
try {
    if (!$SkipNativeBuild) {
        Write-Host '[1/3] Building the UE 5.8 editor host...'
        & $buildBat FinareHostEditor Win64 Development $projectPath `
            -WaitMutex -NoHotReloadFromIDE -NoUBA -MaxParallelActions=4
        if ($LASTEXITCODE -ne 0) {
            throw "Unreal editor build failed with exit code $LASTEXITCODE."
        }
    }

    Write-Host "[2/3] Starting the transactional authoring host on port $Port..."
    $server = Start-Process `
        -FilePath 'powershell.exe' `
        -ArgumentList @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $authoringScript,
            '-EngineRoot', $EngineRoot,
            '-Port', $Port) `
        -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $artifactDirectory 'server.stdout.log') `
        -RedirectStandardError (Join-Path $artifactDirectory 'server.stderr.log') `
        -PassThru

    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline -and !$server.HasExited) {
        $client = [Net.Sockets.TcpClient]::new()
        try {
            $ready = $client.ConnectAsync('127.0.0.1', $Port).Wait(250) -and $client.Connected
        } catch {
            $ready = $false
        } finally {
            $client.Dispose()
        }
        if ($ready) { break }
        Start-Sleep -Milliseconds 250
    }
    if (!$ready) {
        throw "The authoring host did not become ready. See $artifactDirectory."
    }
    if ($ValidateOnly) {
        Write-Host 'Authoring demo launcher validation succeeded.'
        return
    }

    Write-Host '[3/3] Opening Unreal Editor and the Finare Authoring tab...'
    Write-Host 'Close the editor to stop the background authoring host.'
    $editorProcess = Start-Process `
        -FilePath $editor `
        -ArgumentList @($projectPath, '-ExecCmds=Finare.OpenAuthoring') `
        -PassThru
    $editorProcess.WaitForExit()
} finally {
    if ($null -ne $server -and !$server.HasExited) {
        Stop-Process -Id $server.Id
        $server.WaitForExit()
    }
    if ($null -ne $defaultEngineBytes) {
        [IO.File]::WriteAllBytes($defaultEnginePath, $defaultEngineBytes)
    }
    if ($null -ne $defaultInputBytes) {
        [IO.File]::WriteAllBytes($defaultInputPath, $defaultInputBytes)
    }
}
