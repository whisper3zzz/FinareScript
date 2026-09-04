[CmdletBinding()]
param(
    [string]$EngineRoot = 'D:\UE_5.8',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'HostProject\UEUnifiedScriptHost.uproject'
$sourcePath = Join-Path $repoRoot 'samples\Unreal\UnifiedScript.Unreal.Demo\AsyncUiDemoScript.cs'
$logs = Join-Path $repoRoot ('artifacts\ue\async-authoring\' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Force -Path $logs | Out-Null
# This test exercises the checked-in sample, not arbitrary user-authored gameplay.
$originalSource = [IO.File]::ReadAllText($sourcePath)
if ([regex]::Matches($originalSource, 'return 100\.0f;').Count -ne 1) {
    throw 'The authoring example requires its single original PreviewHeight return value (100.0f). No source was changed.'
}
& (Join-Path $PSScriptRoot 'launch-unreal-async-demo.ps1') -EngineRoot $EngineRoot -Mode Prepare -SkipBuild:$SkipBuild

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$url = "http://127.0.0.1:$port"
$server = & (Join-Path $PSScriptRoot 'start-unifiedscript-authoring.ps1') `
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
        'X-UnifiedScript-Baseline-Revision' = [string]$script:workspace.revision
        'X-UnifiedScript-Transaction-Id' = [guid]::NewGuid().ToString('N')
    }
    $json = $Patch | ConvertTo-Json -Depth 30 -Compress
    $body = [Text.Encoding]::UTF8.GetBytes($json)
    $preview = Invoke-RestMethod "$url/api/authoring/preview/$Kind" -Method Post `
        -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 45
    $headers['X-UnifiedScript-Preview-Token'] = $preview.previewToken
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
    $game = Start-Process -FilePath (Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe') `
        -ArgumentList @(('"' + $project + '"'), '/Game/UnifiedScriptDemo/L_AsyncUiDemo',
            '-game', '-UnifiedScriptAsyncUiVerify', '-UnifiedScriptAsyncUiHeight=100',
            '-UnifiedScriptAsyncUiReloadedHeight=180', ('-UnifiedScriptAsyncUiReloadSignal="' + $signal + '"'),
            '-unattended', '-nop4', '-nosplash', '-NullRHI', ('-abslog="' + $runtimeLog + '"')) `
        -WindowStyle Hidden -PassThru
    Wait-For { (Test-Path -LiteralPath $runtimeLog) -and
        (Select-String -LiteralPath $runtimeLog -Pattern 'UNIFIEDSCRIPT_ASYNC_UI_READY_FOR_EDIT' -Quiet) } 'the initial live preview'

    $digest = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($digest.ComputeHash([Text.Encoding]::UTF8.GetBytes($workspace.sourceText))).Replace('-', '').ToLowerInvariant() }
    finally { $digest.Dispose() }
    Send-Edit 'source-text' @{
        schemaVersion = 'unifiedscript.csharp-source-text-patch/v0.1'
        sourcePath = $workspace.sourcePath
        baselineContentDigest = 'sha256:' + $hash
        sourceText = $workspace.sourceText.Replace('return 100.0f;', 'return 120.0f;')
    }
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
    if ($game.ExitCode -ne 0 -or !(Select-String -LiteralPath $runtimeLog -Pattern 'UNIFIEDSCRIPT_ASYNC_UI_VERIFY PASS' -Quiet)) {
        throw "Authored runtime verification failed. See $runtimeLog"
    }
    Write-Host 'Verified: C# edit -> Graph edit -> chunk publication -> live World reload -> changed preview height and teardown.'
} finally {
    if ($null -ne $game) {
        if (!$game.HasExited) { Stop-Process -Id $game.Id -Force }
        $game.Dispose()
    }
    try {
        # Reuse transactional Undo; never force a filesystem restore over a concurrent edit.
        while ($edits -gt 0 -and !$server.HasExited) {
            $workspace = Invoke-RestMethod "$url/api/authoring/undo" -Method Post -ContentType 'application/json' -Body '{}' -TimeoutSec 45 -Headers @{
                'X-UnifiedScript-Baseline-Revision' = [string]$workspace.revision
                'X-UnifiedScript-Transaction-Id' = [guid]::NewGuid().ToString('N')
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
