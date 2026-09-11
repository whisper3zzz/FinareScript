param([Parameter(Mandatory = $true)][string]$PilotRoot)
$ErrorActionPreference = 'Stop'
$pilotPath = (Resolve-Path -LiteralPath $PilotRoot).Path
if ($pilotPath.TrimEnd('\', '/') -ieq 'D:\Project\LyraStarterGame') {
    throw 'The original Lyra reference is read-only. Use an independent checkout.'
}
$contractPath = Join-Path $pilotPath 'Contracts/LyraStarterGame.unreal-api-contract.json'
$configPath = Join-Path $pilotPath 'Config/Finare.onboarding.json'
$contract = Get-Content -LiteralPath $contractPath -Raw | ConvertFrom-Json
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$health = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'health-api-contract.json') -Raw | ConvertFrom-Json
$healthPaths = @($health.types.path)
$contract.schemaVersion = $health.schemaVersion
$contract.types = @($contract.types | Where-Object { $_.path -notin $healthPaths }) + @($health.types)
# Retain starter namespace/output settings and unrelated per-member policies.
$rules = @($contract.policy.rules | Where-Object {
    if ($null -eq $_) { return $false }
    $memberPath = $_.memberPath
    if ([string]::IsNullOrEmpty($memberPath)) { return $true }
    -not @($healthPaths | Where-Object { $memberPath.StartsWith($_ + ':', [StringComparison]::Ordinal) }).Count
}) + @($health.policy.rules)
$contract | Add-Member -NotePropertyName policy -NotePropertyValue ([ordered]@{
    defaultAction = $contract.policy.defaultAction
    rules = $rules
}) -Force
if (-not $contract.policy.defaultAction) { $contract.policy.defaultAction = 'allow' }
$config.reflection.modules = @($config.reflection.modules + @('Engine', 'FinareRuntime', 'LyraGame', 'UMG') | Select-Object -Unique)
$config.reflection.classes = @($config.reflection.classes + @(
    '/Script/Engine.Controller', '/Script/LyraGame.LyraHealthComponent', '/Script/UMG.UserWidget') | Select-Object -Unique)
$config.reflection.blueprintAssets = @($config.reflection.blueprintAssets + @('/Game/UI/Hud/W_Healthbar') | Select-Object -Unique)
$config.bindings.requireCompleteNativeCoverage = $false
$contract | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $contractPath -Encoding utf8
$config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configPath -Encoding utf8
Write-Output 'Selected the Lyra health/HUD APIs; run the installed setup-unreal-project.ps1 to generate and deploy them.'
