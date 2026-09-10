[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [switch]$SkipBuild,
    [switch]$ExternalSourceEdit,
    [switch]$NativeDailyBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'HostProject\FinareHost.uproject'
$sourcePath = Join-Path $repoRoot 'samples\Unreal\Finare.Unreal.Demo\AsyncUiDemoScript.cs'
$logs = Join-Path $repoRoot ('artifacts\ue\async-authoring\' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Force -Path $logs | Out-Null
# This test exercises the checked-in sample, not arbitrary user-authored gameplay.
$originalSource = [IO.File]::ReadAllText($sourcePath)
if ([regex]::Matches($originalSource, 'return 100\.0f;').Count -ne 1) {
    throw 'The authoring example requires its single original PreviewHeight return value (100.0f). No source was changed.'
}
$preparedBuild = $SkipBuild.IsPresent
if ($NativeDailyBuild -and !$SkipBuild) {
    # The attached build requires the ordinary setup receipt, not only UBT outputs.
    & (Join-Path $PSScriptRoot 'setup-unreal-project.ps1') -Project $project -EngineRoot $EngineRoot
    $preparedBuild = $true
}
& (Join-Path $PSScriptRoot 'launch-unreal-async-demo.ps1') -EngineRoot $EngineRoot -Mode Prepare -SkipBuild:$preparedBuild

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$url = "http://127.0.0.1:$port"
$server = & (Join-Path $PSScriptRoot 'start-finare-authoring.ps1') `
    -Project $project -EngineRoot $EngineRoot -Script $sourcePath `
    -ApiBrowserPath (Join-Path $repoRoot 'samples\Unreal\UMG.api-browser.json') `
    -Port $port -Background -LogDirectory $logs
$game = $null
$edits = 0
$workspace = $null

function Wait-For {
    param([scriptblock]$Condition, [string]$Description, [int]$Seconds = 90)
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while (!( & $Condition )) {
        if ($server.HasExited) { throw "Authoring process exited. See $logs" }
        if ($null -ne $game -and $game.HasExited) { throw "Game exited while waiting for $Description. See $logs" }
        if ([DateTime]::UtcNow -gt $deadline) { throw "Timed out waiting for $Description. See $logs" }
        Start-Sleep -Milliseconds 200
    }
}

function Send-Edit {
    param([string]$Kind, [hashtable]$Patch)
    $headers = @{
        'X-Finare-Baseline-Revision' = [string]$script:workspace.revision
        'X-Finare-Transaction-Id' = [guid]::NewGuid().ToString('N')
    }
    $json = $Patch | ConvertTo-Json -Depth 30 -Compress
    $body = [Text.Encoding]::UTF8.GetBytes($json)
    $preview = Invoke-RestMethod "$url/api/authoring/preview/$Kind" -Method Post `
        -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 45
    $headers['X-Finare-Preview-Token'] = $preview.previewToken
    $script:workspace = Invoke-RestMethod "$url/api/authoring/$Kind" -Method Post `
        -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 45
    $script:edits++
}

try {
    Wait-For {
        try { $script:workspace = Invoke-RestMethod "$url/api/authoring/workspace" -TimeoutSec 2 } catch { return $false }
        return $script:workspace.sourcePath -eq 'AsyncUiDemoScript.cs'
    } 'project authoring'
    if ($workspace.sourceText -cne $originalSource) { throw 'Source changed during startup; no test edit was applied.' }
    $runtimeLog = Join-Path $logs 'runtime.log'
    $signal = Join-Path $logs 'reload.ready'
    if ($NativeDailyBuild) {
        $game = Start-Process -FilePath (Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe') `
            -ArgumentList @(('"' + $project + '"'),
                '-ExecCmds="Automation RunTests Finare.Unreal.Editor.ProjectRefresh"',
                '-TestExit="Automation Test Queue Empty"', ('-FinareAuthoringTestUrl=' + $url),
                ('-FinareAuthoringTestSignal="' + $signal + '"'),
                '-unattended', '-nop4', '-nosplash', '-NullRHI', ('-abslog="' + $runtimeLog + '"')) `
            -WindowStyle Hidden -PassThru
        Wait-For { (Test-Path -LiteralPath $runtimeLog) -and
            (Select-String -LiteralPath $runtimeLog -Pattern 'FINARE_PROJECT_REFRESH_READY_FOR_EDIT' -Quiet) } 'native authoring and the initial managed Actor'
        if ([IO.File]::ReadAllText($sourcePath) -cne $workspace.sourceText) { throw 'Source changed before the simulated IDE save.' }
        [IO.File]::WriteAllText($sourcePath, $workspace.sourceText.Replace('return 100.0f;', 'return 120.0f;'), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($signal, 'Build the external edit through the native coordinator.')
        try { Wait-For { $game.HasExited } 'native Build & Reload and automatic Slate refresh' 180 }
        finally {
            $workspace = Invoke-RestMethod "$url/api/authoring/workspace" -TimeoutSec 10
            if ($workspace.sourceText -match 'return 120\.0f;') { $edits++ }
        }
        if ($game.ExitCode -ne 0 -or !(Select-String -LiteralPath $runtimeLog -Pattern 'FINARE_PROJECT_REFRESH PASS' -Quiet)) {
            throw "Native Build & Reload verification failed. See $runtimeLog"
        }
        Write-Host 'Verified: external C# save -> native Build & Reload -> changed live Actor -> automatic Slate refresh; stale-tab recovery and dirty-buffer preservation.'
    } else {
    $game = Start-Process -FilePath (Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe') `
        -ArgumentList @(('"' + $project + '"'), '/Game/UnifiedScriptDemo/L_AsyncUiDemo',
            '-game', '-FinareAsyncUiVerify', '-FinareAsyncUiHeight=100',
            '-FinareAsyncUiReloadedHeight=180', ('-FinareAsyncUiReloadSignal="' + $signal + '"'),
            '-unattended', '-nop4', '-nosplash', '-NullRHI', ('-abslog="' + $runtimeLog + '"')) `
        -WindowStyle Hidden -PassThru
    Wait-For { (Test-Path -LiteralPath $runtimeLog) -and
        (Select-String -LiteralPath $runtimeLog -Pattern 'FINARE_ASYNC_UI_READY_FOR_EDIT' -Quiet) } 'the initial live preview'

    $digest = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($digest.ComputeHash([Text.Encoding]::UTF8.GetBytes($workspace.sourceText))).Replace('-', '').ToLowerInvariant() }
    finally { $digest.Dispose() }
    $sourceEdit = @{
        schemaVersion = 'unifiedscript.csharp-source-text-patch/v0.1'
        sourcePath = $workspace.sourcePath
        baselineContentDigest = 'sha256:' + $hash
        sourceText = $workspace.sourceText.Replace('return 100.0f;', 'return 120.0f;')
    }
    if ($ExternalSourceEdit) {
        if ([IO.File]::ReadAllText($sourcePath) -cne $workspace.sourceText) { throw 'Source changed before the simulated IDE save.' }
        [IO.File]::WriteAllText($sourcePath, $sourceEdit.sourceText, [Text.UTF8Encoding]::new($false))
        $workspace = Invoke-RestMethod "$url/api/authoring/refresh" -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 45 -Headers @{
            'X-Finare-Baseline-Revision' = [string]$workspace.revision
            'X-Finare-Transaction-Id' = [guid]::NewGuid().ToString('N')
        }
        $edits++
    } else { Send-Edit 'source-text' $sourceEdit }
    $function = @($workspace.surface.assets | Where-Object assetPath -EQ 'Unreal/AsyncUiDemo').functions |
        Where-Object name -EQ 'PreviewHeight'
    $literal = @($workspace.graph.functions | Where-Object id -EQ $function.id).nodes |
        Where-Object { $_.kind -eq 'literal' -and $_.properties.value -eq '120.0f' }
    if (@($literal).Count -ne 1) { throw 'Expected one editable PreviewHeight literal in the full project graph.' }
    $literalId = $literal.id
    Send-Edit 'semantic-patch' @{
        schemaVersion = 'unifiedscript.patch/v0.28'
        sourceCompilationId = $workspace.scriptAssets.sourceCompilationId
        semanticConfigurationId = $workspace.scriptAssets.semanticConfigurationId
        operations = @([ordered]@{ operation = 'replaceLiteral'; targetNodeId = $literalId; expectedValueText = '120.0f'; newValueText = '180.0f' })
    }
    $updatedLiteral = @($workspace.graph.functions | Where-Object id -EQ $function.id).nodes |
        Where-Object id -EQ $literalId
    if ($updatedLiteral.properties.value -ne '180.0f' -or $workspace.sourceText -notmatch 'return 180\.0f;') {
        throw 'Graph apply did not retain the semantic identity and update C#.'
    }
    # The running game reloads only after both normal authoring transactions publish.
    [IO.File]::WriteAllText($signal, [string]$workspace.scriptAssets.sourceCompilationId)
    Wait-For { $game.HasExited } 'the authored runtime reload' 60
    if ($game.ExitCode -ne 0 -or !(Select-String -LiteralPath $runtimeLog -Pattern 'FINARE_ASYNC_UI_VERIFY PASS' -Quiet)) {
        throw "Authored runtime verification failed. See $runtimeLog"
    }
    Write-Host "Verified: C# edit (external=$ExternalSourceEdit) -> Graph edit -> chunk publication -> live World reload -> changed preview height and teardown."
    }
} finally {
    if ($null -ne $game) {
        if (!$game.HasExited) { Stop-Process -Id $game.Id -Force }
        $game.Dispose()
    }
    try {
        # Reuse transactional Undo; never force a filesystem restore over a concurrent edit.
        while ($edits -gt 0 -and !$server.HasExited) {
            $workspace = Invoke-RestMethod "$url/api/authoring/undo" -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 45 -Headers @{
                'X-Finare-Baseline-Revision' = [string]$workspace.revision
                'X-Finare-Transaction-Id' = [guid]::NewGuid().ToString('N')
            }
            $edits--
        }
        if ($edits -gt 0 -or [IO.File]::ReadAllText($sourcePath) -cne $originalSource) {
            throw "The test could not undo its source edits safely. Inspect $sourcePath and $logs; no forced restore was attempted."
        }
    } finally {
        if (!$server.HasExited) { Stop-Process -Id $server.Id -Force }
        $server.Dispose()
    }
}
Write-Output "Source edits undone and matching runtime chunk restored. Evidence: $logs"
