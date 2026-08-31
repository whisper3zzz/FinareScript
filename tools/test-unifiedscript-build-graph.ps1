[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'UnifiedScript.BuildGraph.psm1'
Import-Module -Name $modulePath -Force

function Assert-BuildGraph {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Complete-TestStage {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][object]$Decision,
        [Parameter(Mandatory)][string]$Output,
        [Parameter(Mandatory)][string]$Content
    )

    if ($Decision.ShouldRun) {
        [IO.File]::WriteAllText(
            $Output,
            $Content,
            [Text.UTF8Encoding]::new($false))
    }
    Complete-UnifiedScriptBuildGraphStage `
        -Context $Context `
        -Decision $Decision `
        -Executed $Decision.ShouldRun | Out-Null
}

function Invoke-TestMatrixGraph {
    param(
        [Parameter(Mandatory)][string]$State,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Config,
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$ApiShape,
        [Parameter(Mandatory)][string]$OutputDirectory
    )

    $context = New-UnifiedScriptBuildGraphContext `
        -StatePath $State `
        -Project $Project `
        -Config $Config `
        -EngineVersion '5.8.1'
    $semanticOutput = Join-Path $OutputDirectory 'semantic.json'
    $reflectionOutput = Join-Path $OutputDirectory 'reflection.json'
    $bindingOutput = Join-Path $OutputDirectory 'bindings.cs'
    $managedOutput = Join-Path $OutputDirectory 'gameplay.dll'
    $nativeOutput = Join-Path $OutputDirectory 'native.dll'
    $chunkOutput = Join-Path $OutputDirectory 'gameplay.uschunk'
    $deploymentOutput = Join-Path $OutputDirectory 'script-asset.uasset'

    $semantic = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $context `
        -Name 'semantic' `
        -PathInputs ([ordered]@{ managedSource = @($Source) }) `
        -OutputPaths @($semanticOutput)
    Complete-TestStage $context $semantic $semanticOutput 'public-shape-v1'

    $reflection = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $context `
        -Name 'reflection' `
        -PathInputs ([ordered]@{ apiShape = @($ApiShape) }) `
        -OutputPaths @($reflectionOutput)
    Complete-TestStage `
        $context $reflection $reflectionOutput (Get-Content $ApiShape -Raw)

    $binding = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $context `
        -Name 'binding' `
        -PathInputs ([ordered]@{ reflection = @($reflectionOutput) }) `
        -Dependencies @('reflection') `
        -OutputPaths @($bindingOutput)
    Complete-TestStage $context $binding $bindingOutput `
        ("binding:" + (Get-Content $reflectionOutput -Raw))

    $managed = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $context `
        -Name 'managed-compile' `
        -PathInputs ([ordered]@{
            source = @($Source)
            binding = @($bindingOutput)
        }) `
        -Dependencies @('semantic', 'binding') `
        -OutputPaths @($managedOutput)
    Complete-TestStage $context $managed $managedOutput `
        ((Get-Content $Source -Raw) + (Get-Content $bindingOutput -Raw))

    $native = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $context `
        -Name 'native-compile' `
        -PathInputs ([ordered]@{ binding = @($bindingOutput) }) `
        -Dependencies @('binding') `
        -OutputPaths @($nativeOutput)
    Complete-TestStage $context $native $nativeOutput `
        ("native:" + (Get-Content $bindingOutput -Raw))

    $chunk = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $context `
        -Name 'chunk' `
        -PathInputs ([ordered]@{ managed = @($managedOutput) }) `
        -Dependencies @('managed-compile') `
        -OutputPaths @($chunkOutput)
    Complete-TestStage $context $chunk $chunkOutput `
        ("chunk:" + (Get-Content $managedOutput -Raw))

    $deployment = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $context `
        -Name 'deployment' `
        -PathInputs ([ordered]@{
            chunk = @($chunkOutput)
            native = @($nativeOutput)
        }) `
        -Dependencies @('chunk', 'native-compile') `
        -OutputPaths @($deploymentOutput)
    Complete-TestStage $context $deployment $deploymentOutput `
        ((Get-Content $chunkOutput -Raw) + (Get-Content $nativeOutput -Raw))
    return $context
}

function Assert-TestMatrixStatuses {
    param(
        [Parameter(Mandatory)][object]$Context,
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][string[]]$Executed,
        [AllowEmptyCollection()]
        [Parameter(Mandatory)][string[]]$Skipped,
        [Parameter(Mandatory)][string]$Scenario
    )

    $actualExecuted = @($Context.Decisions |
        Where-Object status -eq 'executed' |
        ForEach-Object name |
        Sort-Object)
    $actualSkipped = @($Context.Decisions |
        Where-Object status -eq 'skipped' |
        ForEach-Object name |
        Sort-Object)
    Assert-BuildGraph `
        (($actualExecuted -join '|') -eq (($Executed | Sort-Object) -join '|')) `
        "$Scenario executed the wrong transitive stage set: $($actualExecuted -join ', ')"
    Assert-BuildGraph `
        (($actualSkipped -join '|') -eq (($Skipped | Sort-Object) -join '|')) `
        "$Scenario skipped the wrong transitive stage set: $($actualSkipped -join ', ')"
}

$root = Join-Path ([IO.Path]::GetTempPath()) `
    ("UnifiedScript-BuildGraph-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $root | Out-Null
try {
    $project = Join-Path $root 'Game.uproject'
    $config = Join-Path $root 'UnifiedScript.onboarding.json'
    $source = Join-Path $root 'Gameplay.cs'
    $semantic = Join-Path $root 'reflection.json'
    $consumer = Join-Path $root 'bindings.cs'
    $state = Join-Path $root 'Saved\BuildGraph\state.json'
    [IO.File]::WriteAllText($project, '{}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($config, '{}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($source, 'body-v1', [Text.UTF8Encoding]::new($false))

    $first = New-UnifiedScriptBuildGraphContext `
        -StatePath $state `
        -Project $project `
        -Config $config `
        -EngineVersion '5.8.1'
    $compileBarrier = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $first `
        -Name 'compile-barrier' `
        -Values ([ordered]@{ target = 'Editor' }) `
        -OutputPaths @('')
    Assert-BuildGraph $compileBarrier.ShouldRun `
        'First output-free compile barrier must execute.'
    Complete-UnifiedScriptBuildGraphStage `
        -Context $first `
        -Decision $compileBarrier `
        -Executed $true | Out-Null
    $firstSemantic = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $first `
        -Name 'semantic' `
        -Values ([ordered]@{ classes = @() }) `
        -PathInputs ([ordered]@{ source = @($source) }) `
        -OutputPaths @($semantic)
    Assert-BuildGraph $firstSemantic.ShouldRun 'First semantic stage must execute.'
    Complete-TestStage $first $firstSemantic $semantic 'shape-v1'
    $firstConsumer = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $first `
        -Name 'consumer' `
        -PathInputs ([ordered]@{ semantic = @($semantic) }) `
        -Dependencies @('semantic') `
        -OutputPaths @($consumer)
    Assert-BuildGraph $firstConsumer.ShouldRun 'First consumer stage must execute.'
    Complete-TestStage $first $firstConsumer $consumer 'consumer-v1'
    Save-UnifiedScriptBuildGraphState -Context $first

    $unchanged = New-UnifiedScriptBuildGraphContext `
        -StatePath $state `
        -Project $project `
        -Config $config `
        -EngineVersion '5.8.1'
    $unchangedSemantic = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $unchanged `
        -Name 'semantic' `
        -Values ([ordered]@{ classes = @() }) `
        -PathInputs ([ordered]@{ source = @($source) }) `
        -OutputPaths @($semantic)
    Assert-BuildGraph (-not $unchangedSemantic.ShouldRun) `
        'No-change semantic stage must skip.'
    Complete-TestStage $unchanged $unchangedSemantic $semantic 'shape-v1'
    $unchangedConsumer = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $unchanged `
        -Name 'consumer' `
        -PathInputs ([ordered]@{ semantic = @($semantic) }) `
        -Dependencies @('semantic') `
        -OutputPaths @($consumer)
    Assert-BuildGraph (-not $unchangedConsumer.ShouldRun) `
        'No-change consumer stage must skip.'
    Complete-TestStage $unchanged $unchangedConsumer $consumer 'consumer-v1'

    [IO.File]::WriteAllText($source, 'body-v2', [Text.UTF8Encoding]::new($false))
    $bodyEdit = New-UnifiedScriptBuildGraphContext `
        -StatePath $state `
        -Project $project `
        -Config $config `
        -EngineVersion '5.8.1'
    $bodySemantic = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $bodyEdit `
        -Name 'semantic' `
        -Values ([ordered]@{ classes = @() }) `
        -PathInputs ([ordered]@{ source = @($source) }) `
        -OutputPaths @($semantic)
    Assert-BuildGraph $bodySemantic.ShouldRun 'Source edit must execute semantic stage.'
    Complete-TestStage $bodyEdit $bodySemantic $semantic 'shape-v1'
    $bodyConsumer = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $bodyEdit `
        -Name 'consumer' `
        -PathInputs ([ordered]@{ semantic = @($semantic) }) `
        -Dependencies @('semantic') `
        -OutputPaths @($consumer)
    Assert-BuildGraph (-not $bodyConsumer.ShouldRun) `
        'Byte-identical semantic output must not invalidate its consumer.'
    Complete-TestStage $bodyEdit $bodyConsumer $consumer 'consumer-v1'

    [IO.File]::WriteAllText($consumer, 'drifted', [Text.UTF8Encoding]::new($false))
    $drift = New-UnifiedScriptBuildGraphContext `
        -StatePath $state `
        -Project $project `
        -Config $config `
        -EngineVersion '5.8.1'
    $driftSemantic = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $drift `
        -Name 'semantic' `
        -Values ([ordered]@{ classes = @() }) `
        -PathInputs ([ordered]@{ source = @($source) }) `
        -OutputPaths @($semantic)
    Complete-TestStage $drift $driftSemantic $semantic 'shape-v1'
    $driftConsumer = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $drift `
        -Name 'consumer' `
        -PathInputs ([ordered]@{ semantic = @($semantic) }) `
        -Dependencies @('semantic') `
        -OutputPaths @($consumer)
    Assert-BuildGraph $driftConsumer.ShouldRun 'Output drift must execute its owner stage.'
    Assert-BuildGraph ($driftConsumer.Reason.Contains('tracked outputs')) `
        'Output drift must be explainable in the decision reason.'

    $report = Get-UnifiedScriptBuildGraphReport -Context $unchanged
    Assert-BuildGraph ($report.executed -eq 0 -and $report.skipped -eq 2) `
        'No-change report must count two skipped stages and zero executions.'

    $deferred = New-UnifiedScriptBuildGraphContext `
        -StatePath $state `
        -Project $project `
        -Config $config `
        -EngineVersion '5.8.1'
    $deferredSemantic = Get-UnifiedScriptBuildGraphStageDecision `
        -Context $deferred `
        -Name 'semantic' `
        -Values ([ordered]@{ classes = @() }) `
        -PathInputs ([ordered]@{ source = @($source) }) `
        -OutputPaths @($semantic)
    Assert-BuildGraph $deferredSemantic.ShouldRun `
        'Changed live input must request the Editor-unsafe stage.'
    Defer-UnifiedScriptBuildGraphStage `
        -Context $deferred `
        -Decision $deferredSemantic `
        -Reason 'Editor owns the published output' | Out-Null
    $deferredReport = Get-UnifiedScriptBuildGraphReport -Context $deferred
    Assert-BuildGraph (
        $deferredReport.deferred -eq 1 -and
        $deferredReport.executed -eq 0 -and
        $deferredReport.skipped -eq 0) `
        'Editor-attached report must count a deferred stage separately.'
    Assert-BuildGraph (
        $deferred.CurrentStages['semantic'].inputFingerprint -eq
        $deferred.PreviousStages['semantic'].inputFingerprint) `
        'Deferral must retain the last successful stage state instead of accepting new inputs.'

    $matrixRoot = Join-Path $root 'matrix'
    New-Item -ItemType Directory -Force -Path $matrixRoot | Out-Null
    $matrixSource = Join-Path $matrixRoot 'Gameplay.cs'
    $matrixApi = Join-Path $matrixRoot 'ApiShape.json'
    $matrixState = Join-Path $matrixRoot 'state.json'
    $matrixOutputs = Join-Path $matrixRoot 'outputs'
    New-Item -ItemType Directory -Force -Path $matrixOutputs | Out-Null
    [IO.File]::WriteAllText(
        $matrixSource, 'body-v1', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $matrixApi, 'api-v1', [Text.UTF8Encoding]::new($false))
    $matrixBaseline = Invoke-TestMatrixGraph `
        $matrixState $project $config $matrixSource $matrixApi $matrixOutputs
    Assert-TestMatrixStatuses `
        $matrixBaseline `
        @('semantic', 'reflection', 'binding', 'managed-compile',
            'native-compile', 'chunk', 'deployment') `
        @() `
        'Cold build'
    Save-UnifiedScriptBuildGraphState -Context $matrixBaseline

    [IO.File]::WriteAllText(
        $matrixSource, 'body-v2', [Text.UTF8Encoding]::new($false))
    $matrixBodyEdit = Invoke-TestMatrixGraph `
        $matrixState $project $config $matrixSource $matrixApi $matrixOutputs
    Assert-TestMatrixStatuses `
        $matrixBodyEdit `
        @('semantic', 'managed-compile', 'chunk', 'deployment') `
        @('reflection', 'binding', 'native-compile') `
        'Managed body edit'
    Save-UnifiedScriptBuildGraphState -Context $matrixBodyEdit

    [IO.File]::WriteAllText(
        $matrixApi, 'api-v2', [Text.UTF8Encoding]::new($false))
    $matrixApiEdit = Invoke-TestMatrixGraph `
        $matrixState $project $config $matrixSource $matrixApi $matrixOutputs
    Assert-TestMatrixStatuses `
        $matrixApiEdit `
        @('reflection', 'binding', 'managed-compile', 'native-compile',
            'chunk', 'deployment') `
        @('semantic') `
        'API shape edit'
    Write-Host 'UnifiedScript dependency-aware Build/Play graph: PASS'
} finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
