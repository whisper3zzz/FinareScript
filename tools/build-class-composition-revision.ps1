[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Config,
    [Parameter(Mandatory)][string]$Report,
    [string]$DotNet = 'D:/UE_5.8/Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe'
)

# Integration-test build command: compile the saved C# source and publish only
# after emit and stable-class validation succeed. The real editor queue reads
# this receipt and decides whether its live component batch may reload.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $repoRoot 'src/Finare.Cli/bin/Release/net10.0/Finare.Cli.dll'
$oldDotNetRoot = $env:DOTNET_ROOT
$env:DOTNET_ROOT = Split-Path -Parent $DotNet
$exitCode = 0
$receipt = [ordered]@{
    schemaVersion = 'unifiedscript.unreal-project-onboarding-report/v0.1'
    mode = 'dailyEditor'
    status = 'failed'
    outputs = @{ buildGraph = @{ executed = 0; skipped = 0; deferred = 0 } }
}
$preimages = @{}
$publicationStarted = $false
try {
    $fixture = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
    if ($fixture.schemaVersion -ne 'finare.class-composition-fixture/v1') { throw 'Invalid composition fixture configuration.' }
    $declarationRoot = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($Config))) 'saved-revision'
    & $DotNet build $fixture.watchProjectPath --configuration Release --nologo --verbosity minimal -p:FinareSkipManagedHostStaging=true
    if ($LASTEXITCODE -ne 0) { throw 'Saved C# revision failed to compile.' }
    $receipt.outputs.buildGraph.executed++
    & $DotNet $cli emit $fixture.watchProjectPath --out $declarationRoot --identity $fixture.identityPath --configuration Release --no-restore --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Saved C# revision failed declaration emission.' }
    $receipt.outputs.buildGraph.executed++
    $declarationPath = Join-Path $declarationRoot 'ClassCompositionFixture.asset.json'
    $document = Get-Content -LiteralPath $declarationPath -Raw | ConvertFrom-Json
    foreach ($script in $fixture.scripts) {
        $matches = @($document.assets | Where-Object { $_.id -ceq $script.scriptClassId })
        if ($matches.Count -ne 1 -or $matches[0].qualifiedName -cne $script.scriptTypeName -or
            $matches[0].classBinding.generatedClassPath -cne $script.generatedClassPath -or
            $matches[0].classBinding.kind -cne $script.kind) {
            throw "Saved revision changed the class contract for $($script.scriptTypeName)."
        }
    }
    foreach ($path in @($fixture.liveAssemblyPath, $fixture.liveDeclarationPath)) {
        $preimages[$path] = [IO.File]::ReadAllBytes($path)
    }
    $publicationStarted = $true
    Copy-Item -LiteralPath (Join-Path $declarationRoot 'ClassCompositionFixture.dll') -Destination $fixture.liveAssemblyPath -Force
    Copy-Item -LiteralPath $declarationPath -Destination $fixture.liveDeclarationPath -Force
    $receipt.outputs.buildGraph.executed++
    $receipt.status = 'passed'
} catch {
    if ($publicationStarted) {
        foreach ($path in $preimages.Keys) { [IO.File]::WriteAllBytes($path, $preimages[$path]) }
    }
    $receipt.error = @{ code = 'compositionBuildFailed'; message = $_.Exception.Message }
    $exitCode = 1
    Write-Output $_.Exception.Message
} finally {
    $env:DOTNET_ROOT = $oldDotNetRoot
    $receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Report -Encoding utf8
}
exit $exitCode
