[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Finare.BuildGraph.psm1') -Force
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'setup-unreal-project.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$stages = @{}
foreach ($command in $ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Invoke-DependencyAwareOnboardingPhase'
}, $true)) {
    $arguments = @{}
    for ($i=1; $i -lt $command.CommandElements.Count; $i++) {
        if ($command.CommandElements[$i] -is [Management.Automation.Language.CommandParameterAst]) {
            $arguments[$command.CommandElements[$i].ParameterName] = $command.CommandElements[$i+1].Extent.Text
        }
    }
    if ($arguments.Name) { $stages[$arguments.Name.Trim("'", '"')] = $arguments }
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('Finare-ApiLibrary-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $project = Join-Path $root 'Game.uproject'; $config = Join-Path $root 'config.json'
    $source = Join-Path $root 'Gameplay.cs'; $cpp = Join-Path $root 'Api.cpp'
    foreach ($path in @($project, $config, $source, $cpp)) { [IO.File]::WriteAllText($path, 'initial') }
    $resolved = @{ modules=@('Engine'); classes=@(); blueprintAssets=@(); bindingNamespace='Game.Generated';
        managedConfiguration='Debug'; failOnRejected=$true; requireCompleteNativeCoverage=$false;
        reflection=(Join-Path $root 'reflection.json'); nativeExtensions=$null;
        managedOutput=(Join-Path $root 'Bindings.cs'); managedProjects=@((Join-Path $root 'Game.csproj'));
        editorTarget='GameEditor'; platform='Win64'; configuration='Development' }
    $cliAssembly=Join-Path $root 'Cli.dll'; [IO.File]::WriteAllText($cliAssembly,'tool')
    $script:ResolvedProjectPath=$project
    $managedSourceInputs=@($source); $managedRuntimeInputs=@($cliAssembly)
    $nativeSourceInputs=@($cpp); $nativeConfigurationInputs=@($config)
    $names=@('reflection-export','binding-generation','managed-gameplay-build','native-final-build')
    $state=Join-Path $root 'state.json'
    for ($revision=0; $revision -lt 2; $revision++) {
        if ($revision -eq 1) { [IO.File]::WriteAllText($source,'actor.SetActorTickEnabled(true);') }
        $context=New-FinareBuildGraphContext -StatePath $state -Project $project -Config $config -EngineVersion '5.8.1'
        foreach ($name in $names) {
            # Evaluate the production stage's real inputs, not a duplicate test graph.
            $values=& ([scriptblock]::Create($stages[$name].Values))
            $inputs=& ([scriptblock]::Create($stages[$name].PathInputs))
            $output = switch ($name) {
                'reflection-export' { $resolved.reflection }
                'binding-generation' { $resolved.managedOutput }
                default { Join-Path $root ($name + '.output') }
            }
            $decision=Get-FinareBuildGraphStageDecision -Context $context -Name $name -Values $values -PathInputs $inputs -OutputPaths @($output)
            if ($revision -eq 1 -and $decision.ShouldRun -ne ($name -eq 'managed-gameplay-build')) {
                throw "Adding an available API call invalidated the wrong stage: $name ($($decision.Reason))"
            }
            if ($decision.ShouldRun) { [IO.File]::WriteAllText($output, "$name-$revision") }
            Complete-FinareBuildGraphStage -Context $context -Decision $decision -Executed $decision.ShouldRun | Out-Null
        }
        Save-FinareBuildGraphState -Context $context
    }
    Write-Output 'Available API edit invalidates managed compilation only: PASS'
} finally {
    $resolvedRoot=[IO.Path]::GetFullPath($root)
    if ($resolvedRoot.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
