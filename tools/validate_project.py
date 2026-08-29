#!/usr/bin/env python3
"""Validate repository structure without duplicating the .NET test suite."""

from __future__ import annotations

import hashlib
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]

PROJECTS = [
    ROOT / "src" / "UnifiedScript.Core" / "UnifiedScript.Core.csproj",
    ROOT / "src" / "UnifiedScript.Semantics" / "UnifiedScript.Semantics.csproj",
    ROOT / "src" / "UnifiedScript.CSharp" / "UnifiedScript.CSharp.csproj",
    ROOT / "src" / "UnifiedScript.Graph" / "UnifiedScript.Graph.csproj",
    ROOT / "src" / "UnifiedScript.Authoring" / "UnifiedScript.Authoring.csproj",
    ROOT / "src" / "UnifiedScript.Runtime" / "UnifiedScript.Runtime.csproj",
    ROOT / "src" / "UnifiedScript.Unreal" / "UnifiedScript.Unreal.csproj",
    ROOT / "src" / "UnifiedScript.Cli" / "UnifiedScript.Cli.csproj",
    ROOT
    / "samples"
    / "Unreal"
    / "UnifiedScript.Unreal.Demo"
    / "UnifiedScript.Unreal.Demo.csproj",
    ROOT / "samples" / "ProjectLoading" / "ProjectLoadingDemo.csproj",
    ROOT / "tests" / "UnifiedScript.Tests" / "UnifiedScript.Tests.csproj",
]

DOCUMENTS = [
    ROOT / "README.md",
    ROOT / "CONTRIBUTING.md",
    ROOT / "docs" / "ARCHITECTURE.md",
    ROOT / "docs" / "IDENTITY_SIDECAR.md",
    ROOT / "docs" / "GRAPH_LAYOUT.md",
    ROOT / "docs" / "GRAPH_REGIONS.md",
    ROOT / "docs" / "CONTROL_FLOW_ANALYSIS.md",
    ROOT / "docs" / "LOCAL_ASSIGNMENT_PATCHING.md",
    ROOT / "docs" / "NESTED_BRANCH_PATCHING.md",
    ROOT / "docs" / "LOOP_FOUNDATION.md",
    ROOT / "docs" / "LOOP_PATCHING.md",
    ROOT / "docs" / "FOR_LOOPS.md",
    ROOT / "docs" / "DO_WHILE_LOOPS.md",
    ROOT / "docs" / "FOREACH_LOOPS.md",
    ROOT / "docs" / "STRUCTURED_EXPRESSION_EDITING.md",
    ROOT / "docs" / "GENERAL_EXPRESSION_EDITING.md",
    ROOT / "docs" / "PRODUCED_VALUE_REFERENCES.md",
    ROOT / "docs" / "EXPRESSION_INPUT_PATCHING.md",
    ROOT / "docs" / "PRODUCED_EXPRESSION_HANDLES.md",
    ROOT / "docs" / "CONDITIONAL_EXPRESSIONS.md",
    ROOT / "docs" / "SHORT_CIRCUIT_EXPRESSIONS.md",
    ROOT / "docs" / "NULL_COALESCING_EXPRESSIONS.md",
    ROOT / "docs" / "NULL_CONDITIONAL_ACCESS.md",
    ROOT / "docs" / "PATCH_PLAN.md",
    ROOT / "docs" / "EXECUTION_RUNTIME.md",
    ROOT / "docs" / "SOURCE_MAP.md",
    ROOT / "docs" / "BREAKPOINT_RELOAD.md",
    ROOT / "docs" / "NATIVE_DEBUG_WORKBENCH.md",
    ROOT / "docs" / "REMOTE_DEBUG_FRAMING.md",
    ROOT / "docs" / "RUNTIME_DIAGNOSTICS.md",
    ROOT / "docs" / "HOT_RELOAD_RELIABILITY.md",
    ROOT / "docs" / "RELIABILITY_SOAK.md",
    ROOT / "docs" / "LARGE_PROJECT_SCALE.md",
    ROOT / "docs" / "GAMEPLAY_PROFILER.md",
    ROOT / "docs" / "PERFORMANCE_WORKLOAD_SUITE.md",
    ROOT / "docs" / "COMPILED_CHUNKS.md",
    ROOT / "docs" / "PROJECT_LOADING.md",
    ROOT / "docs" / "DECLARATION_IR.md",
    ROOT / "docs" / "STATE_OVERRIDE_MIGRATION.md",
    ROOT / "docs" / "BLOCKBREAKER_ROUND_TRIP.md",
    ROOT / "docs" / "NULLABILITY_FLOW.md",
    ROOT / "docs" / "VISUAL_AUTHORING_MODEL.md",
    ROOT / "docs" / "AUTHORING_SURFACE.md",
    ROOT / "docs" / "NODE_PALETTE.md",
    ROOT / "docs" / "PIN_AUTHORING.md",
    ROOT / "docs" / "CANVAS_AUTHORING.md",
    ROOT / "docs" / "NODE_DETAILS.md",
    ROOT / "docs" / "AUTHORING_DIAGNOSTICS.md",
    ROOT / "docs" / "UNREAL_ADAPTER.md",
    ROOT / "docs" / "UNREAL_DEPLOYMENT.md",
    ROOT / "docs" / "UNREAL_ONBOARDING.md",
    ROOT / "docs" / "UNREAL_SDK.md",
    ROOT / "docs" / "DOCUMENTATION.md",
    ROOT / "docs" / "RELEASE_NOTES_1_0_RC1.md",
    ROOT / "docs" / "UNREAL_RELEASE.md",
    ROOT / "docs" / "UNREAL_COMPATIBILITY_MATRIX.md",
    ROOT / "docs" / "VERSIONING_AND_UPGRADES.md",
    ROOT / "docs" / "CI.md",
    ROOT / "docs" / "DIAGNOSTICS_BUNDLES.md",
    ROOT / "docs" / "UNREAL_FIRST_SCRIPT.md",
    ROOT / "docs" / "ALPHA_ACCEPTANCE.md",
    ROOT / "docs" / "UNREAL_API_CONTRACT.md",
    ROOT / "docs" / "ROADMAP.md",
    ROOT / "docs" / "milestones" / "M1_ROSLYN_GRAPH_FOUNDATION.md",
    ROOT / "docs" / "milestones" / "M2_SEMANTIC_BINDING.md",
    ROOT / "docs" / "milestones" / "M3_BIDIRECTIONAL_PATCHING.md",
    ROOT / "docs" / "milestones" / "M4_STRUCTURED_CONTROL_FLOW.md",
    ROOT / "docs" / "milestones" / "M5_CORECLR_EXECUTION.md",
    ROOT / "docs" / "milestones" / "M5_5_AUTHORING_EXPERIENCE.md",
    ROOT / "docs" / "milestones" / "M6_0_GENERIC_AUTHORING.md",
    ROOT / "docs" / "milestones" / "M6_1_UNREAL_ADAPTER.md",
    ROOT / "docs" / "milestones" / "M7_4_BLOCKBREAKER_MIGRATION.md",
]


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def main() -> int:
    checks: list[str] = []

    global_json = ROOT / "global.json"
    with global_json.open("r", encoding="utf-8") as handle:
        sdk_config = json.load(handle)
    require(sdk_config["sdk"]["version"].startswith("10."), "The project must target .NET 10")
    checks.append("global.json selects .NET 10")

    solution = ROOT / "UnifiedScript.slnx"
    solution_tree = ET.parse(solution)
    solution_projects = {
        (ROOT / element.attrib["Path"]).resolve()
        for element in solution_tree.findall(".//Project")
    }
    require(
        solution_projects == {project.resolve() for project in PROJECTS},
        "Solution project set differs from the expected architecture",
    )
    checks.append("solution contains the expected project boundaries")

    for project in PROJECTS:
        require(project.is_file(), f"Missing project: {project}")
        ET.parse(project)
    checks.append("all project files are present and valid XML")

    for document in DOCUMENTS:
        require(document.is_file(), f"Missing document: {document}")
    checks.append("architecture and milestone documents exist")

    compiled_chunk_source = (
        ROOT / "src" / "UnifiedScript.Core" / "CompiledChunk.cs"
    ).read_text(encoding="utf-8")
    compiled_chunk_tests = (
        ROOT / "tests" / "UnifiedScript.Tests" / "CompiledChunkTests.cs"
    ).read_text(encoding="utf-8")
    cli_source = (
        ROOT / "src" / "UnifiedScript.Cli" / "Program.cs"
    ).read_text(encoding="utf-8")
    compiled_chunk_document = (
        ROOT / "docs" / "COMPILED_CHUNKS.md"
    ).read_text(encoding="utf-8")
    roadmap_document = (
        ROOT / "docs" / "ROADMAP.md"
    ).read_text(encoding="utf-8")
    compiled_program_source = (
        ROOT / "src" / "UnifiedScript.Runtime" / "CompiledProgramChunk.cs"
    ).read_text(encoding="utf-8")
    compiled_program_host = (
        ROOT / "src" / "UnifiedScript.Runtime" / "CoreClrCompiledChunkHost.cs"
    ).read_text(encoding="utf-8")
    managed_dependency_cache = (
        ROOT / "src" / "UnifiedScript.Runtime"
        / "UnifiedScriptManagedDependencyCache.cs"
    ).read_text(encoding="utf-8")
    compiled_chunk_profiler = (
        ROOT / "src" / "UnifiedScript.Runtime" / "CompiledChunkLoadProfiler.cs"
    ).read_text(encoding="utf-8")
    compiled_chunk_process_profiler = (
        ROOT / "tools" / "measure-compiled-chunk-load.ps1"
    ).read_text(encoding="utf-8")
    readytorun_profiler = (
        ROOT / "tools" / "measure-readytorun-chunk.ps1"
    ).read_text(encoding="utf-8")
    generation_prewarm_source = (
        ROOT / "src" / "UnifiedScript.Runtime" / "CoreClrGenerationPrewarm.cs"
    ).read_text(encoding="utf-8")
    generation_prewarm_profiler = (
        ROOT / "tools" / "measure-generation-prewarm.ps1"
    ).read_text(encoding="utf-8")
    generation_prewarm_probe = (
        ROOT / "benchmarks" / "unreal" / "generation-prewarm-probe.cs"
    ).read_text(encoding="utf-8")
    generation_prewarm_tests = (
        ROOT / "tests" / "UnifiedScript.Tests" / "CoreClrGenerationPrewarmTests.cs"
    ).read_text(encoding="utf-8")
    program_loader_prewarm_source = (
        ROOT / "src" / "UnifiedScript.Runtime" / "CoreClrProgramLoaderPrewarm.cs"
    ).read_text(encoding="utf-8")
    program_loader_prewarm_tests = (
        ROOT / "tests" / "UnifiedScript.Tests"
        / "CoreClrProgramLoaderPrewarmTests.cs"
    ).read_text(encoding="utf-8")
    compiled_program_tests = (
        ROOT / "tests" / "UnifiedScript.Tests" / "CompiledProgramChunkTests.cs"
    ).read_text(encoding="utf-8")
    unreal_world_entry = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealWorldManagedEntryPoint.cs"
    ).read_text(encoding="utf-8")
    unreal_chunk_reader = (
        ROOT / "HostProject" / "Plugins" / "UEUnifiedScript" / "Source"
        / "UEUnifiedScriptRuntime" / "Private" / "UnifiedScriptCompiledChunk.cpp"
    ).read_text(encoding="utf-8")
    unreal_component = (
        ROOT / "HostProject" / "Plugins" / "UEUnifiedScript" / "Source"
        / "UEUnifiedScriptRuntime" / "Private" / "UnifiedScriptComponent.cpp"
    ).read_text(encoding="utf-8")
    unreal_asset = (
        ROOT / "HostProject" / "Plugins" / "UEUnifiedScript" / "Source"
        / "UEUnifiedScriptRuntime" / "Public" / "UnifiedScriptAsset.h"
    ).read_text(encoding="utf-8")
    unreal_chunk_stage = (
        ROOT / "tools" / "emit-unreal-demo-chunk.ps1"
    ).read_text(encoding="utf-8")
    chunk_deployment_commandlet = (
        ROOT / "HostProject" / "Plugins" / "UEUnifiedScript" / "Source"
        / "UEUnifiedScriptEditor" / "Private"
        / "UnifiedScriptSyncChunksCommandlet.cpp"
    ).read_text(encoding="utf-8")
    chunk_deployment_sample = (
        ROOT / "samples" / "Unreal"
        / "UnifiedScript.Unreal.Demo.chunk-deployment.json"
    ).read_text(encoding="utf-8")
    chunk_delivery_measurement = (
        ROOT / "comparison" / "measure-blockbreaker-chunk-delivery.ps1"
    ).read_text(encoding="utf-8")
    blockbreaker_package = (
        ROOT / "comparison" / "package-blockbreaker-demo.ps1"
    ).read_text(encoding="utf-8")
    release_package = (
        ROOT / "tools" / "package-unreal-release.ps1"
    ).read_text(encoding="utf-8")
    project_loader = (
        ROOT / "src" / "UnifiedScript.CSharp" / "CSharpProjectLoader.cs"
    ).read_text(encoding="utf-8")
    require(
        '"USCHUNK\\0"u8' in compiled_chunk_source
        and "FormatMajorVersion = 1" in compiled_chunk_source
        and "ComputeContentDigest" in compiled_chunk_source
        and "FixedTimeEquals" in compiled_chunk_source
        and "UnifiedScriptProgramChunk" in compiled_chunk_source
        and 'args[0] == "chunk-pack"' in cli_source
        and 'args[0] == "chunk-inspect"' in cli_source
        and "Write_IsDeterministicAndCanonicalAcrossInputOrder"
        in compiled_chunk_tests
        and "Read_RejectsCorruptedPayloadBeforeItCanBeLoaded"
        in compiled_chunk_tests
        and "UnifiedScriptRuntimeDeclarationBinary" in compiled_program_source
        and "CSharpProgramChunkCompiler" in compiled_program_source
        and "CoreClrCompiledChunkHost" in compiled_program_host
        and "CoreClrHost_LoadsAssemblyDirectlyFromVerifiedChunk" in compiled_program_tests
        and '".uschunk"' in unreal_world_entry
        and "FUnifiedScriptCompiledChunk::LoadSection" in unreal_chunk_reader
        and "MaterializeChunkFileWithOverrides" in unreal_component
        and "CompiledChunkPath" in unreal_asset
        and "UnifiedScript.Unreal.Demo.uschunk" in unreal_chunk_stage
        and "CSharpProjectReferenceKind.Assembly" in project_loader
        and "IncludePortablePdb: !string.Equals" in cli_source
        and "unifiedscript.unreal-chunk-deployment/v0.1" in chunk_deployment_commandlet
        and "ValidateChunk" in chunk_deployment_commandlet
        and "UPackage::SavePackage" in chunk_deployment_commandlet
        and "CompiledChunkPath" in chunk_deployment_commandlet
        and "chunkPath" in chunk_deployment_sample
        and "UnifiedScriptDisableCompiledChunks" in unreal_component
        and "unifiedscript.blockbreaker-chunk-delivery/v0.2"
        in chunk_delivery_measurement
        and "BlockBreakerBenchmarkReload" in chunk_delivery_measurement
        and "compiledChunkPrograms" in blockbreaker_package
        and "compiledChunkStartupMilliseconds" in release_package
        and "does **not** introduce another interpreter" in compiled_chunk_document,
        "Compiled chunk compiler, hosts, strict readers, staging, tests, or contract are disconnected",
    )
    checks.append(
        "compiled chunks are deterministic, integrity-checked, and loaded end to end"
    )
    require(
        "UnifiedScriptChunkReadProfile" in compiled_chunk_source
        and "ReadProfiled(Stream source)" in compiled_chunk_source
        and "WholeFileCopyCount" in compiled_chunk_source
        and "SectionDigestStopwatchTicks" in compiled_chunk_source
        and "CoreClrCompiledChunkLoadProfile" in compiled_program_host
        and "ManagedAssemblyMaterializationStopwatchTicks"
        in compiled_program_host
        and "RuntimeHelpers.PrepareMethod" in compiled_program_host
        and "unifiedscript.compiled-chunk-load-profile/v0.1"
        in compiled_chunk_profiler
        and all(
            phase in compiled_chunk_profiler
            for phase in {
                '"streamRead"',
                '"wholeFileDigest"',
                '"sectionDigests"',
                '"declarationDecode"',
                '"assemblyLoad"',
                '"entryMethodJitPreparation"',
            }
        )
        and 'args[0] == "chunk-profile"' in cli_source
        and "unifiedscript.compiled-chunk-process-profile/v0.1"
        in compiled_chunk_process_profiler
        and "Get-Int64Median" in compiled_chunk_process_profiler
        and "processFirstLoad" in compiled_chunk_process_profiler
        and "output directory must be empty" in compiled_chunk_process_profiler
        and "ProfiledStreamReadOwnsOneWholeFileCopyAndDigest"
        in compiled_chunk_tests
        and "ChunkProfilerMeasuresCopiesDecodeLoadAndEntryMethodJitPreparation"
        in compiled_program_tests
        and "M11.4 phased load profiling" in compiled_chunk_document
        and "one exact-length whole-file" in compiled_chunk_document
        and "does not flush the operating-system file cache"
        in compiled_chunk_document,
        "M11.4 chunk copy, digest, decode, CoreCLR load, or JIT profiling is incomplete",
    )
    checks.append(
        "M11.4 profiles chunk I/O, copies, verification, decode, CoreCLR load, and JIT preparation"
    )
    require(
        "DefaultMaximumEntryCount = 128" in managed_dependency_cache
        and "DefaultMaximumByteCount = 256L * 1024 * 1024"
        in managed_dependency_cache
        and "FileShare.Read" in managed_dependency_cache
        and "PEReader" in managed_dependency_cache
        and "ModuleVersionId" in managed_dependency_cache
        and "sharedAssemblyDigests" in managed_dependency_cache
        and "leastRecentlyUsed" in managed_dependency_cache
        and "DependencyCacheHitCount" in compiled_program_host
        and "DependencySnapshotCopyBytes" in compiled_program_host
        and "LoadFromStream(stream)" in compiled_program_host
        and "DependencyCacheHitCount" in compiled_chunk_profiler
        and "dependencyCacheHitCount" in compiled_chunk_process_profiler
        and "UnifiedScriptManagedDependencyCache.Shared" in unreal_world_entry
        and "LoadAssemblySnapshot" in unreal_world_entry
        and "CoreClrHost_ReusesVerifiedDependencySnapshotsAcrossCollectibleContexts"
        in compiled_program_tests
        and "DependencyCache_UsesImmutableContentThenRejectsChangedDiskEvidence"
        in compiled_program_tests
        and "DependencyCache_IsSingleFlightAndBounded" in compiled_program_tests
        and "verified dependency snapshot cache" in compiled_chunk_document,
        "M11.4 verified dependency cache, immutable ALC snapshots, metrics, tests, or documentation are incomplete",
    )
    checks.append(
        "M11.4 atomically caches bounded verified dependency snapshots across World generations"
    )
    require(
        "unifiedscript.readytorun-experiment/v0.1" in readytorun_profiler
        and "Crossgen2 output does not preserve the exact IL identity"
        in readytorun_profiler
        and "managedNativeHeaderBytes" in readytorun_profiler
        and "collectible-memory-stream" in readytorun_profiler
        and "maximumLoadRatio = 0.95" in readytorun_profiler
        and "maximumJitPreparationRatio = 0.95" in readytorun_profiler
        and "maximumChunkSizeRatio = 1.5" in readytorun_profiler
        and "promoteReadyToRun = $promote" in readytorun_profiler
        and "M11.4 ReadyToRun decision" in compiled_chunk_document
        and "R2R failed all three promotion gates" in compiled_chunk_document,
        "M11.4 exact-identity ReadyToRun experiment, promotion budgets, SDK tool, or decision record is incomplete",
    )
    checks.append(
        "M11.4 rejects ReadyToRun when exact-identity cold load, JIT, or size budgets fail"
    )
    require(
        "DefaultMaximumMethodCount = 64" in generation_prewarm_source
        and "DefaultTimeBudgetMilliseconds = 8" in generation_prewarm_source
        and "MaximumSupportedMethodCount = 1024" in generation_prewarm_source
        and "MaximumSupportedTimeBudgetMilliseconds = 100"
        in generation_prewarm_source
        and "Task.Run(Run)" in generation_prewarm_source
        and "ObserveFirstTick" in generation_prewarm_source
        and "RuntimeHelpers.PrepareMethod" in generation_prewarm_source
        and "Prewarm.Cancel()" in unreal_world_entry
        and '"backgroundPrewarm"' in unreal_world_entry
        and "UNIFIEDSCRIPT_DISABLE_BACKGROUND_PREWARM" in unreal_world_entry
        and 'args[0] == "chunk-first-tick-profile"' in cli_source
        and "unifiedscript.generation-prewarm-trial/v0.1" in cli_source
        and "unifiedscript.generation-prewarm-experiment/v0.1"
        in generation_prewarm_profiler
        and "$maximumFirstTickRatio = 0.80" in generation_prewarm_profiler
        and "$maximumScheduleNanoseconds = 2000000" in generation_prewarm_profiler
        and "$maximumTeardownNanoseconds = 2000000" in generation_prewarm_profiler
        and "@('disabled', 'background')" in generation_prewarm_profiler
        and "MethodImplOptions.NoInlining" in generation_prewarm_probe
        and "Cancellation_IsNonBlockingAndGenerationWorkFinishesCooperatively"
        in generation_prewarm_tests
        and "CandidateCancellationBeforeActivation_DoesNotScheduleWork"
        in generation_prewarm_tests
        and "FirstTickProfileCommand_UsesTheGenerationPrewarmOperation"
        in compiled_program_tests
        and "generation-owned background prewarm" in compiled_chunk_document,
        "M11.4 background prewarm bounds, ownership, cancellation, first-Tick evidence, or documentation are incomplete",
    )
    checks.append(
        "M11.4 moves bounded cancellable JIT prewarm outside activation and first Tick"
    )
    require(
        "DefaultMaximumDependencyCount = 32" in program_loader_prewarm_source
        and "DefaultMaximumDependencyBytes = 64L * 1024 * 1024"
        in program_loader_prewarm_source
        and "DefaultTimeBudgetMilliseconds = 50"
        in program_loader_prewarm_source
        and "HardMaximumDependencyCount = 256" in program_loader_prewarm_source
        and "HardMaximumDependencyBytes = 512L * 1024 * 1024"
        in program_loader_prewarm_source
        and "HardMaximumTimeBudgetMilliseconds = 500"
        in program_loader_prewarm_source
        and "Task.Run(ExecuteAsync)" in program_loader_prewarm_source
        and "StartDependencyPreparation" in program_loader_prewarm_source
        and "StartCodecPreparation" in program_loader_prewarm_source
        and "WarmBinaryCodec" in program_loader_prewarm_source
        and "dependencyCache.Preload" in program_loader_prewarm_source
        and "ReadOwnedContent" in compiled_chunk_source
        and "ReadCore(content, profile: null, content)" in compiled_chunk_source
        and "ProgramLoaderPrewarm.Start()" in unreal_world_entry
        and "current.Prewarm.Start()" in unreal_world_entry
        and "UNIFIEDSCRIPT_DISABLE_PROGRAM_LOADER_PREWARM" in unreal_world_entry
        and "ProgramLoaderPrewarm = ProgramLoaderPrewarm.GetSnapshot()"
        in unreal_world_entry
        and "Prewarm_CanPrepareDependenciesBeforeCodecWorkIsReleased"
        in program_loader_prewarm_tests
        and "Options_EnforceHardBoundsAndZeroDisables"
        in program_loader_prewarm_tests
        and "MaximumStartupRatio = 1.0" in chunk_delivery_measurement
        and "programLoaderPrewarm.codecPrepared" in chunk_delivery_measurement
        and "programLoaderPrewarm.failureCount" in chunk_delivery_measurement
        and "if (-not $report.acceptance.passed)" in chunk_delivery_measurement
        and 'TEXT("programLoaderPrewarm")' in unreal_component
        and "UnifiedScript startup phases:" in unreal_component
        and "process loader prewarm and cold-start closure"
        in compiled_chunk_document
        and "0.970394" in compiled_chunk_document
        and "M11.4 is complete and the cursor advances to M12.1"
        in roadmap_document,
        "M11.4 process loader prewarm, one-copy reader, hard gate, evidence, or cursor closure is incomplete",
    )
    checks.append(
        "M11.4 closes matched chunk cold start with bounded loader prewarm and a hard gate"
    )

    deployment_sample_path = (
        ROOT / "samples" / "Unreal" / "UnifiedScript.Unreal.Demo.deployment.json"
    )
    deployment_commandlet = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "UnifiedScriptSyncAssetsCommandlet.cpp"
    ).read_text(encoding="utf-8")
    runtime_asset_header = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptAsset.h"
    ).read_text(encoding="utf-8")
    runtime_component_source = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UnifiedScriptComponent.cpp"
    ).read_text(encoding="utf-8")
    with deployment_sample_path.open("r", encoding="utf-8") as handle:
        deployment_sample = json.load(handle)
    deployment_scripts = deployment_sample["scripts"]
    require(
        deployment_sample["schemaVersion"]
        == "unifiedscript.unreal-script-deployment/v0.1"
        and len(deployment_scripts) == 1
        and deployment_scripts[0]["assetPath"].startswith("/Game/")
        and deployment_scripts[0]["requiredTargetClass"].startswith("/Script/")
        and deployment_scripts[0]["apiNodeCatalogPath"].endswith(".api-nodes.json")
        and "ValidateOnly" in deployment_commandlet
        and "ValidateKnownFields" in deployment_commandlet
        and "UPackage::SavePackage" in deployment_commandlet
        and "RequiredTargetClass" in runtime_asset_header
        and "ApiNodeCatalogPath" in runtime_asset_header
        and "ValidateTarget" in runtime_component_source,
        "Unreal deployment manifest, asset sync, or target compatibility gate is missing",
    )
    checks.append("UE deployment manifests materialize target-safe Script Assets")

    api_contract_path = (
        ROOT / "samples" / "Unreal" / "UEUnifiedScriptRuntime.api-contract.json"
    )
    api_nodes_path = (
        ROOT / "samples" / "Unreal" / "UEUnifiedScriptRuntime.api-nodes.json"
    )
    with api_contract_path.open("r", encoding="utf-8") as handle:
        api_contract = json.load(handle)
    with api_nodes_path.open("r", encoding="utf-8") as handle:
        api_nodes = json.load(handle)
    contract_generator = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealApiContract.cs"
    ).read_text(encoding="utf-8")
    cli_source = (ROOT / "src" / "UnifiedScript.Cli" / "Program.cs").read_text(
        encoding="utf-8"
    )
    reflection_exporter = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "UnifiedScriptReflectionExporter.cpp"
    ).read_text(encoding="utf-8")
    authoring_widget = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "Widgets"
        / "SUnifiedScriptAuthoringSurface.cpp"
    ).read_text(encoding="utf-8")
    contract_member_names = {
        member
        for type_selector in api_contract["types"]
        for member in type_selector["members"]
    }
    node_member_names = {
        member["path"].rsplit(":", 1)[1]
        for node_type in api_nodes["types"]
        for member_group in ("properties", "functions", "delegates")
        for member in node_type[member_group]
    }
    node_descriptors = [
        node
        for node_type in api_nodes["types"]
        for node in node_type["nodes"]
    ]
    require(
        api_contract["schemaVersion"]
        == "unifiedscript.unreal-api-contract/v0.2"
        and api_nodes["schemaVersion"]
        == "unifiedscript.unreal-api-node-catalog/v0.2"
        and api_nodes["contractName"] == api_contract["contractName"]
        and api_nodes["managedNamespace"] == api_contract["managedNamespace"]
        and contract_member_names == node_member_names
        and all(
            {
                "latent",
                "threadRequirement",
                "worldRequirement",
                "callableWithoutWorldContext",
            }
            <= node.keys()
            for node in node_descriptors
        )
        and any(
            pin.get("defaultValue") == "1"
            for node in node_descriptors
            for pin in node["pins"]
        )
        and "UnrealApiContractProjector" in contract_generator
        and "UnrealApiNodeCatalogGenerator" in contract_generator
        and "--contract" in cli_source
        and "--nodes-out" in cli_source
        and "WriteTransactionally(outputs)" in cli_source
        and "ClassPathSet" in reflection_exporter
        and "ResolveApiNodeCatalogPath" in authoring_widget
        and "UEUnifiedScriptRuntime.api-nodes.json" in authoring_widget,
        "Project API Contract generation or Script Asset-selected visual catalog is missing",
    )
    checks.append("one Unreal API Contract drives C++, C#, coverage, and visual nodes")

    onboarding_script_path = ROOT / "tools" / "setup-unreal-project.ps1"
    onboarding_config_path = (
        ROOT / "HostProject" / "Config" / "UnifiedScript.onboarding.json"
    )
    onboarding_coverage_path = (
        ROOT / "samples" / "Unreal" / "UEUnifiedScriptRuntime.binding-coverage.json"
    )
    onboarding_script = onboarding_script_path.read_text(encoding="utf-8")
    with onboarding_config_path.open("r", encoding="utf-8") as handle:
        onboarding_config = json.load(handle)
    with onboarding_coverage_path.open("r", encoding="utf-8") as handle:
        onboarding_coverage = json.load(handle)
    onboarding_paths = [
        onboarding_config["pluginPath"],
        onboarding_config["reflection"]["output"],
        onboarding_config["bindings"]["contract"],
        onboarding_config["bindings"]["managedOutput"],
        onboarding_config["bindings"]["nativeOutput"],
        onboarding_config["bindings"]["nodeCatalogOutput"],
        onboarding_config["bindings"]["coverageOutput"],
        onboarding_config["deployment"]["manifest"],
        onboarding_config["managed"]["stageDirectory"],
        *onboarding_config["managed"]["projects"],
    ]
    coverage_summary = onboarding_coverage["summary"]
    require(
        onboarding_script_path.is_file()
        and onboarding_config["schemaVersion"]
        == "unifiedscript.unreal-project-onboarding/v0.1"
        and onboarding_config["editorTarget"] == "UEUnifiedScriptHostEditor"
        and onboarding_config["bindings"]["requireCompleteNativeCoverage"] is True
        and all(not Path(path).is_absolute() and ":" not in path for path in onboarding_paths)
        and coverage_summary["memberCount"]
        == coverage_summary["nativeThunkCount"]
        + coverage_summary["reflectionFallbackCount"]
        + coverage_summary["dataPlaneOperationCount"]
        and coverage_summary["memberCount"]
        == len(onboarding_coverage["entries"])
        and onboarding_coverage["gate"]["passed"] is True
        and coverage_summary["rejectedCount"] == 0
        and coverage_summary["deferredTypeCount"] == 0
        and "EngineAssociation" in onboarding_script
        and "ValidateOnly" in onboarding_script
        and "managed-toolchain-validation" in onboarding_script
        and "Current repository CLI build failed before validation" in onboarding_script
        and "unifiedscript.unreal-project-onboarding-report/v0.1" in onboarding_script
        and "Assert-GeneratedFileMatches" in onboarding_script
        and "UnifiedScriptSyncAssets" in onboarding_script
        and "Automation RunTests" in onboarding_script,
        "One-command Unreal onboarding, determinism, or native coverage gate is missing",
    )
    checks.append("one command onboards and deterministically validates an Unreal project")

    sdk_package_path = ROOT / "tools" / "package-unifiedscript-sdk.ps1"
    sdk_install_path = ROOT / "tools" / "install-unifiedscript-sdk.ps1"
    sdk_distribution_path = ROOT / "docs" / "UNREAL_SDK.md"
    sdk_package = sdk_package_path.read_text(encoding="utf-8")
    sdk_install = sdk_install_path.read_text(encoding="utf-8")
    sdk_distribution = sdk_distribution_path.read_text(encoding="utf-8")
    require(
        sdk_package_path.is_file()
        and sdk_install_path.is_file()
        and "unifiedscript.sdk-manifest/v0.1" in sdk_package
        and "Write-DeterministicArchive" in sdk_package
        and "LastWriteTime" in sdk_package
        and "GetTempPath" in sdk_package
        and '"us-sdk-$(' in sdk_package
        and "outside the SDK temp boundary" in sdk_package
        and "Managed\\Runtime" in sdk_package
        and "Tools\\UnifiedScript.Cli" in sdk_package
        and "measure-compiled-chunk-load.ps1" in sdk_package
        and "measure-readytorun-chunk.ps1" in sdk_package
        and "measure-generation-prewarm.ps1" in sdk_package
        and "generation-prewarm-probe.cs" in sdk_package
        and "release\\unifiedscript.release.json" in sdk_package
        and "$releaseDocumentation" in sdk_package
        and "Engine-version-specific plugin Binaries leaked" in sdk_package
        and "Assert-NoMachinePathLeak" in sdk_package
        and "unifiedscript.sdk-manifest/v0.1" in sdk_install
        and "Get-FileHash" in sdk_install
        and "Unmanifested SDK payload file" in sdk_install
        and "UEUnifiedScript.backup-" in sdk_install
        and "sdkLayout = 'packaged'" in onboarding_script
        and "packagedSdkManifest" in onboarding_script
        and "sdkLayout: packaged" in sdk_distribution,
        "Deterministic SDK packaging, compatibility, or verified installation is missing",
    )
    checks.append("SDK archive is deterministic, hashed, compatible, and installable")

    product_release_path = ROOT / "release" / "unifiedscript.release.json"
    product_documentation_path = ROOT / "docs" / "DOCUMENTATION.md"
    product_release_notes_path = ROOT / "docs" / "RELEASE_NOTES_1_0_RC1.md"
    product_plugin_path = (
        ROOT / "HostProject" / "Plugins" / "UEUnifiedScript"
        / "UEUnifiedScript.uplugin"
    )
    compatibility_matrix_runner_path = (
        ROOT / "tools" / "run-unreal-compatibility-matrix.ps1"
    )
    for path in [
        product_release_path,
        product_documentation_path,
        product_release_notes_path,
        product_plugin_path,
        compatibility_matrix_runner_path,
    ]:
        require(path.is_file(), f"Missing M12.2 release-surface artifact: {path}")
    with product_release_path.open("r", encoding="utf-8") as handle:
        product_release = json.load(handle)
    with product_plugin_path.open("r", encoding="utf-8") as handle:
        product_plugin = json.load(handle)
    product_documentation = product_documentation_path.read_text(encoding="utf-8")
    product_release_notes = product_release_notes_path.read_text(encoding="utf-8")
    compatibility_matrix_runner = compatibility_matrix_runner_path.read_text(
        encoding="utf-8"
    )
    release_documents = product_release["documentation"]
    release_document_ids = [document["id"] for document in release_documents]
    release_document_paths = [document["path"] for document in release_documents]
    release_document_categories = {
        document["category"] for document in release_documents
    }
    required_release_categories = {
        "index",
        "sdk",
        "onboarding",
        "authoring",
        "api",
        "debugging",
        "performance",
        "packaging",
        "upgrades",
        "migration",
        "support",
        "samples",
        "releaseNotes",
    }
    require(
        product_release["schemaVersion"]
        == "unifiedscript.product-release/v0.1"
        and product_release["productVersion"] == "1.0.0-rc.1"
        and product_release["channel"] == "rc"
        and product_release["status"] == "releaseCandidate"
        and product_release["compatibility"]
        == {
            "platform": "win-x64",
            "engineFamily": "5.8",
            "verifiedEngineVersion": "5.8.1",
            "managedTargetFramework": "net10.0",
        }
        and product_release["artifacts"]["sdkArchive"]
        == "UnifiedScript-SDK-1.0.0-rc.1-win-x64.zip"
        and product_release["artifacts"]["pluginVersion"] == 1000001
        and product_plugin["Version"] == 1000001
        and product_plugin["VersionName"] == "1.0.0-rc.1"
        and len(release_documents) == 23
        and len(set(release_document_ids)) == len(release_document_ids)
        and len(set(release_document_paths)) == len(release_document_paths)
        and required_release_categories <= release_document_categories
        and all(
            path.startswith("docs/")
            and (ROOT / path).is_file()
            and (ROOT / path).resolve().parent == (ROOT / "docs").resolve()
            for path in release_document_paths
        )
        and product_release["releaseNotes"] in release_document_paths
        and product_release["acceptance"]["managedTests"] == 600
        and product_release["acceptance"]["repositoryInvariants"] == 86
        and product_release["acceptance"]["unrealAutomationTests"] == 18
        and "release-manifest.json" in sdk_package
        and "$releaseDocumentation" in sdk_package
        and "documentationEntryCount" in sdk_package
        and "does not match release manifest" in sdk_package
        and "1.0.0-rc.1" in compatibility_matrix_runner
        and "$ExpectedAutomationTests = 18" in compatibility_matrix_runner
        and "$automationExitCode = $LASTEXITCODE" in compatibility_matrix_runner
        and "$ErrorActionPreference = 'Continue'" in compatibility_matrix_runner
        and "releaseStatus -ne 'releaseCandidate'" in compatibility_matrix_runner
        and "Start here" in product_documentation
        and "Authoring and API" in product_documentation
        and "Debugging and profiling" in product_documentation
        and "Packaging, upgrades, and migration" in product_documentation
        and "Other platforms and UE versions are not"
        in product_release_notes
        and "- [x] publish versioned plugin/SDK archives" in roadmap_document
        and "- Next slice: **M12.2 — automated sample matrix" in roadmap_document
        and "M12.2 release-surface evidence" in roadmap_document
        and "sourceTreeDirty=false" in roadmap_document,
        "M12.2 product release manifest, versioned SDK, documentation catalog, or compatibility gate is incomplete",
    )
    checks.append(
        "M12.2 binds the 1.0 RC1 plugin, SDK, release notes, and complete documentation catalog"
    )

    sample_catalog_path = ROOT / "samples" / "Unreal" / "unifiedscript.samples.json"
    sample_matrix_runner_path = ROOT / "tools" / "run-unifiedscript-sample-matrix.ps1"
    sample_document_path = ROOT / "docs" / "UNREAL_SAMPLES.md"
    for path in [sample_catalog_path, sample_matrix_runner_path, sample_document_path]:
        require(path.is_file(), f"Missing M12.2 sample-matrix artifact: {path}")
    with sample_catalog_path.open("r", encoding="utf-8") as handle:
        sample_catalog = json.load(handle)
    sample_matrix_runner = sample_matrix_runner_path.read_text(encoding="utf-8")
    sample_document = sample_document_path.read_text(encoding="utf-8")
    sample_categories = [sample["category"] for sample in sample_catalog["samples"]]
    sample_ids = [sample["id"] for sample in sample_catalog["samples"]]
    sample_runners = {
        sample["execution"]["runner"] for sample in sample_catalog["samples"]
    }
    required_sample_categories = {
        "minimal",
        "gameplay",
        "networking-server",
        "data-plane",
        "visual-code",
    }
    require(
        sample_catalog["schemaVersion"] == "unifiedscript.sample-catalog/v0.1"
        and sample_catalog["productVersion"] == product_release["productVersion"]
        and set(sample_catalog["categories"]) == required_sample_categories
        and len(sample_catalog["samples"]) == 5
        and len(set(sample_categories)) == 5
        and len(set(sample_ids)) == 5
        and set(sample_categories) == required_sample_categories
        and all(sample["sources"] for sample in sample_catalog["samples"])
        and all(sample["assertions"] for sample in sample_catalog["samples"])
        and all(sample["guarantees"] for sample in sample_catalog["samples"])
        and all(sample["nonClaims"] for sample in sample_catalog["samples"])
        and all(
            sample["execution"]["reportSchema"].startswith("unifiedscript.")
            for sample in sample_catalog["samples"]
        )
        and "unifiedscript.sample-matrix-report/v0.1" in sample_matrix_runner
        and "Test-SampleAssertion" in sample_matrix_runner
        and "Get-GitState" in sample_matrix_runner
        and sample_runners
        == {
            "tools/run-unifiedscript-alpha-acceptance.ps1",
            "comparison/package-blockbreaker-demo.ps1",
            "tools/run-unreal-lifecycle-stress.ps1",
            "tools/run-unreal-data-plane-profile.ps1",
            "comparison/verify-blockbreaker-roundtrip.ps1",
        }
        and "client/server gameplay replication" in sample_document
        and "complete acceptance corpus" in sample_document
        and "docs/UNREAL_SAMPLES.md" in release_document_paths
        and "sample-matrix" in product_release["acceptance"]["requiredGates"],
        "M12.2 five-category sample catalog, declarative assertions, aggregate runner, or support boundary is incomplete",
    )
    checks.append(
        "M12.2 executes five versioned Unreal sample categories through one evidence matrix"
    )

    compatibility_policy_path = (
        ROOT / "schemas" / "unifiedscript.compatibility-policy.v0.1.json"
    )
    compatibility_source_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnifiedScriptCompatibility.cs"
    )
    compatibility_tests_path = (
        ROOT / "tests" / "UnifiedScript.Tests" / "UnifiedScriptCompatibilityTests.cs"
    )
    compatibility_guide_path = ROOT / "docs" / "VERSIONING_AND_UPGRADES.md"
    with compatibility_policy_path.open("r", encoding="utf-8") as handle:
        compatibility_policy = json.load(handle)
    compatibility_families = {
        family["id"]: family for family in compatibility_policy["families"]
    }
    compatibility_source = compatibility_source_path.read_text(encoding="utf-8")
    compatibility_tests = compatibility_tests_path.read_text(encoding="utf-8")
    compatibility_guide = compatibility_guide_path.read_text(encoding="utf-8")
    require(
        compatibility_policy["schemaVersion"]
        == "unifiedscript.compatibility-policy/v0.1"
        and len(compatibility_families) == 16
        and compatibility_families["unreal-reflection"]["currentVersion"]
        == "unifiedscript.unreal-reflection/v0.4"
        and compatibility_families["unreal-api-contract"]["currentVersion"]
        == "unifiedscript.unreal-api-contract/v0.2"
        and compatibility_families["compiled-program-chunk"]["upgradeStrategy"]
        == "rebuild"
        and compatibility_families["generated-bindings"]["upgradeStrategy"]
        == "regenerate"
        and compatibility_families["unreal-abi-set"]["components"]
        == {
            "bootstrap": 2,
            "callFrame": 3,
            "event": 4,
            "fastMember": 2,
            "gameplayData": 4,
            "sharedData": 4,
        }
        and 'args[0] is "upgrade-plan" or "upgrade-apply"' in cli_source
        and "UnifiedScriptUpgradeDisposition" in compatibility_source
        and "RewriteMigrationsReadLegacyAndEmitCanonicalCurrentDocuments"
        in compatibility_tests
        and "UpgradeApplyIsAtomicWhenPlanRequiresRegenerationOrIsBlocked"
        in compatibility_tests
        and "JSON remains the reviewable authoring" in compatibility_guide
        and "compatibility-policy.v0.1.json" in sdk_package,
        "Versioned compatibility policy, deterministic migration, SDK payload, or guide is missing",
    )
    checks.append("versioned artifacts plan deterministic rewrite, regeneration, and rebuild upgrades")

    ci_workflow_path = ROOT / ".github" / "workflows" / "ci.yml"
    ci_lane_path = ROOT / "tools" / "run-ci-lane.ps1"
    deterministic_gate_path = (
        ROOT / "tools" / "run-deterministic-generation-gate.ps1"
    )
    ci_guide_path = ROOT / "docs" / "CI.md"
    ci_workflow = ci_workflow_path.read_text(encoding="utf-8")
    ci_lane = ci_lane_path.read_text(encoding="utf-8")
    deterministic_gate = deterministic_gate_path.read_text(encoding="utf-8")
    ci_guide = ci_guide_path.read_text(encoding="utf-8")
    ci_lanes = {
        "managed-tests",
        "deterministic-generation",
        "ue-build",
        "unreal-automation",
        "package-smoke",
        "workload-performance",
        "sdk-installation",
    }
    require(
        ci_workflow_path.is_file()
        and ci_lane_path.is_file()
        and deterministic_gate_path.is_file()
        and all(f"'{lane}'" in ci_lane for lane in ci_lanes)
        and all(f"- {lane}" in ci_workflow for lane in ci_lanes - {
            "managed-tests", "deterministic-generation"
        })
        and "-Lane managed-tests" in ci_workflow
        and "-Lane deterministic-generation" in ci_workflow
        and "-RequireCleanTree" in ci_workflow
        and "runs-on: [self-hosted, Windows, X64, ue-5.8.1]" in ci_workflow
        and "github.event_name != 'pull_request'" in ci_workflow
        and "actions/checkout@v7" in ci_workflow
        and "actions/setup-dotnet@v6" in ci_workflow
        and "actions/setup-python@v7" in ci_workflow
        and "actions/upload-artifact@v7" in ci_workflow
        and "unifiedscript.ci-lane/v0.1" in ci_lane
        and "'-c', 'Debug'" in ci_lane
        and "sourceDirty" in ci_lane
        and "CI lane output directory must be empty" in ci_lane
        and "run-complete-workload-suite.ps1" in ci_lane
        and "-RequireComplete" in ci_lane
        and "status -ne 'qualified'" in ci_lane
        and "run-unreal-compatibility-matrix.ps1" in ci_lane
        and "[switch]$RequireComplete" in (
            ROOT / "comparison" / "run-complete-workload-suite.ps1"
        ).read_text(encoding="utf-8")
        and "**/*.json" in ci_workflow
        and "**/*.zip" not in ci_workflow
        and "unifiedscript.deterministic-generation-gate/v0.1"
        in deterministic_gate
        and "matchesSecondRun" in deterministic_gate
        and "matchesCheckedIn" in deterministic_gate
        and "seven independent release-candidate lanes" in ci_guide
        and "2.327.1" in ci_guide,
        "Clean CI lane matrix, deterministic gate, workflow isolation, or runner guide is missing",
    )
    checks.append("seven clean CI lanes isolate managed, generated, UE, package, performance, and SDK gates")

    diagnostics_policy_path = (
        ROOT / "schemas" / "unifiedscript.diagnostics-policy.v0.1.json"
    )
    diagnostics_source_path = (
        ROOT
        / "src"
        / "UnifiedScript.Unreal"
        / "UnifiedScriptDiagnosticsBundle.cs"
    )
    diagnostics_tests_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnifiedScriptDiagnosticsBundleTests.cs"
    )
    diagnostics_guide_path = ROOT / "docs" / "DIAGNOSTICS_BUNDLES.md"
    cli_project_path = ROOT / "src" / "UnifiedScript.Cli" / "UnifiedScript.Cli.csproj"
    with diagnostics_policy_path.open("r", encoding="utf-8") as handle:
        diagnostics_policy = json.load(handle)
    diagnostics_source = diagnostics_source_path.read_text(encoding="utf-8")
    diagnostics_tests = diagnostics_tests_path.read_text(encoding="utf-8")
    diagnostics_guide = diagnostics_guide_path.read_text(encoding="utf-8")
    cli_project = cli_project_path.read_text(encoding="utf-8")
    require(
        diagnostics_policy["schemaVersion"]
        == "unifiedscript.diagnostics-policy/v0.1"
        and len(diagnostics_policy["allowedSchemas"]) == 31
        and len(set(diagnostics_policy["allowedSchemas"])) == 31
        and diagnostics_policy["maximumReportCount"] == 256
        and diagnostics_policy["maximumReportBytes"] == 16 * 1024 * 1024
        and diagnostics_policy["maximumBundleBytes"] == 64 * 1024 * 1024
        and "unifiedscript.ci-lane/v0.1"
        in diagnostics_policy["allowedSchemas"]
        and "unifiedscript.unreal-release-gate/v0.2"
        in diagnostics_policy["allowedSchemas"]
        and "unifiedscript.compiled-chunk-load-profile/v0.1"
        in diagnostics_policy["allowedSchemas"]
        and "unifiedscript.compiled-chunk-process-profile/v0.1"
        in diagnostics_policy["allowedSchemas"]
        and "unifiedscript.readytorun-experiment/v0.1"
        in diagnostics_policy["allowedSchemas"]
        and "unifiedscript.generation-prewarm-trial/v0.1"
        in diagnostics_policy["allowedSchemas"]
        and "unifiedscript.generation-prewarm-experiment/v0.1"
        in diagnostics_policy["allowedSchemas"]
        and all(
            source_schema not in diagnostics_policy["allowedSchemas"]
            for source_schema in {
                "unifiedscript.hir/v0.9",
                "unifiedscript.authoring-surface/v0.1",
                "unifiedscript.unreal-reflection/v0.4",
            }
        )
        and {"schemaVersion", "status", "phase", "metric", "sha256"}.issubset(
            set(diagnostics_policy["safeStringPropertyNames"])
        )
        and {"source", "sourceText", "token", "message", "stackTrace"}.issubset(
            set(diagnostics_policy["sensitivePropertyNames"])
        )
        and 'args[0] == "diagnostics-bundle"' in cli_source
        and "unifiedscript.diagnostics-policy.json" in cli_project
        and "unifiedscript.diagnostics-bundle/v0.1" in diagnostics_source
        and '"safe-default"' in diagnostics_source
        and "EnumerateFilesWithoutFollowingReparsePoints" in diagnostics_source
        and "TryReadBounded" in diagnostics_source
        and '"<redacted-string>"' in diagnostics_source
        and "ArchiveTimestamp" in diagnostics_source
        and 'reports/{envelopes.Count + 1:D4}-report.json' in diagnostics_source
        and "SafeDefaultBundleIsDeterministicAllowlistedBoundedAndRedacted"
        in diagnostics_tests
        and "ordinary but proprietary" in diagnostics_tests
        and "CliUsesBundledPolicyAndWritesTheSameArchiveTwice" in diagnostics_tests
        and "diagnostics-policy.v0.1.json" in sdk_package
        and "docs/DIAGNOSTICS_BUNDLES.md" in release_document_paths
        and "redacted by default" in diagnostics_guide
        and "v0.1 has no flag" in diagnostics_guide,
        "Privacy-safe diagnostics policy, bounded collector, deterministic archive, SDK payload, or guide is missing",
    )
    checks.append(
        "diagnostics bundles are allowlisted, bounded, deterministic, and string-redacted by default"
    )

    scaffold_path = ROOT / "tools" / "new-unifiedscript-project.ps1"
    authoring_start_path = ROOT / "tools" / "start-unifiedscript-authoring.ps1"
    first_script_guide_path = ROOT / "docs" / "UNREAL_FIRST_SCRIPT.md"
    template_root = ROOT / "templates" / "UnrealProject"
    required_templates = [
        template_root / "Config" / "UnifiedScript.onboarding.json.template",
        template_root / "Contracts" / "Project.unreal-api-contract.json.template",
        template_root
        / "Deployment"
        / "Project.unifiedscript-deployment.json.template",
        template_root / "Managed" / "Project.UnifiedScript.csproj.template",
        template_root / "Managed" / "FirstScript.cs.template",
        template_root / "Source" / "Project.Build.cs.template",
        template_root / "Source" / "Project.Target.cs.template",
        template_root / "Source" / "ProjectEditor.Target.cs.template",
        template_root / "Project.plugin-fragment.json.template",
    ]
    scaffold = scaffold_path.read_text(encoding="utf-8")
    authoring_start = authoring_start_path.read_text(encoding="utf-8")
    first_script_guide = first_script_guide_path.read_text(encoding="utf-8")
    first_script_template = required_templates[4].read_text(encoding="utf-8")
    plugin_source_root = (
        ROOT / "HostProject" / "Plugins" / "UEUnifiedScript" / "Source"
    )
    plugin_implementation_files = [
        path
        for path in plugin_source_root.rglob("*")
        if path.suffix in {".cpp", ".h", ".cs"}
        and "Tests" not in path.parts
    ]
    plugin_implementation = "\n".join(
        path.read_text(encoding="utf-8") for path in plugin_implementation_files
    )
    runtime_module = (
        plugin_source_root
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UEUnifiedScriptRuntimeModule.cpp"
    ).read_text(encoding="utf-8")
    host_module = (
        ROOT
        / "HostProject"
        / "Source"
        / "UEUnifiedScriptHost"
        / "Private"
        / "UEUnifiedScriptHostModule.cpp"
    ).read_text(encoding="utf-8")
    editor_module = (
        plugin_source_root
        / "UEUnifiedScriptEditor"
        / "Private"
        / "UEUnifiedScriptEditorModule.cpp"
    ).read_text(encoding="utf-8")
    require(
        scaffold_path.is_file()
        and authoring_start_path.is_file()
        and all(path.is_file() for path in required_templates)
        and "unifiedscript.project-scaffold-report/v0.1" in scaffold
        and "setup-unreal-project.ps1" in scaffold
        and "identity-init" in authoring_start
        and "debug-serve" in authoring_start
        and "Authoring\\Workbench" in authoring_start
        and "using System;" in first_script_template
        and '[ScriptAsset("__PROJECT_NAME__/__SCRIPT_NAME__")]'
        in first_script_template
        and "[Visual]" in first_script_template
        and "editor play" in first_script_guide.lower()
        and "packaged play" in first_script_guide.lower()
        and not any(
            fixture_name in plugin_implementation
            for fixture_name in (
                "BlockBreaker",
                "UnifiedScript.Unreal.Demo",
                "DamagePolicy",
                "DemoSurfacePath",
                "HandleLoadDemo",
            )
        )
        and "UnifiedScriptSmoke" not in runtime_module
        and "UnifiedScriptSmoke" in host_module
        and ".InitialSurfacePath(" not in editor_module,
        "Project-agnostic first-script templates or fixture isolation is missing",
    )
    checks.append("generic first-script scaffold isolates fixtures from the plugin")

    alpha_acceptance_path = (
        ROOT / "tools" / "run-unifiedscript-alpha-acceptance.ps1"
    )
    alpha_acceptance_doc_path = ROOT / "docs" / "ALPHA_ACCEPTANCE.md"
    release_gate_path = ROOT / "tools" / "package-unreal-release.ps1"
    alpha_acceptance = alpha_acceptance_path.read_text(encoding="utf-8")
    alpha_acceptance_doc = alpha_acceptance_doc_path.read_text(encoding="utf-8")
    release_gate = release_gate_path.read_text(encoding="utf-8")
    require(
        alpha_acceptance_path.is_file()
        and alpha_acceptance_doc_path.is_file()
        and "unifiedscript.alpha-acceptance/v0.1" in alpha_acceptance
        and "managed-debug-fixture" in alpha_acceptance
        and "ProjectLoadingDemo.csproj" in alpha_acceptance
        and "managed-tests" in alpha_acceptance
        and "repository-validator" in alpha_acceptance
        and "unreal-onboarding" in alpha_acceptance
        and "package-smoke" in alpha_acceptance
        and "managed-tests.trx" in alpha_acceptance
        and "onboarding-report.json" in alpha_acceptance
        and "release-gate.json" in alpha_acceptance
        and "USACC2001" in alpha_acceptance
        and "retry =" in alpha_acceptance
        and "alpha-acceptance.md" in alpha_acceptance
        and "SkipPackageSmoke" in alpha_acceptance
        and "OutputDirectory" in release_gate
        and "automation = [ordered]@{" in onboarding_script
        and "Failure still produces both top-level reports"
        in alpha_acceptance_doc,
        "Single-command alpha acceptance reporting or failure UX is missing",
    )
    checks.append("one alpha gate reports generation, tests, UE, Automation, and package smoke")

    authoring_source = ROOT / "samples" / "Authoring" / "DamagePolicy.cs"
    authoring_artifact = (
        ROOT / "prototype" / "visual-editor" / "data" / "damage-policy.asset.json"
    )
    authoring_graph = (
        ROOT / "prototype" / "visual-editor" / "data" / "damage-policy.graph.json"
    )
    authoring_surface = (
        ROOT / "prototype" / "visual-editor" / "data" / "damage-policy.surface.json"
    )
    authoring_source_snapshot = (
        ROOT / "prototype" / "visual-editor" / "data" / "damage-policy.source.cs"
    )
    require(authoring_source.is_file(), "Missing Script Asset authoring sample")
    require(authoring_artifact.is_file(), "Missing visual-editor Declaration IR artifact")
    require(authoring_graph.is_file(), "Missing visual-editor Graph artifact")
    require(authoring_surface.is_file(), "Missing visual-editor Authoring Surface artifact")
    require(authoring_source_snapshot.is_file(), "Missing visual-editor source snapshot")
    with authoring_artifact.open("r", encoding="utf-8") as handle:
        declaration_document = json.load(handle)
    require(
        declaration_document["schemaVersion"] == "unifiedscript.script-asset/v0.1"
        and len(declaration_document["assets"]) == 1
        and declaration_document["assets"][0]["assetPath"] == "Combat/DamagePolicy"
        and len(declaration_document["assets"][0]["states"]) == 3
        and len(declaration_document["assets"][0]["functions"]) == 1,
        "Authoring Declaration IR sample is inconsistent",
    )
    checks.append("Script Asset sample and Declaration IR artifact are consistent")

    with authoring_graph.open("r", encoding="utf-8") as handle:
        graph_document = json.load(handle)
    graph_function = graph_document["functions"][0]
    graph_nodes = graph_function["nodes"]
    graph_node_ids = {node["id"] for node in graph_nodes}
    graph_regions = graph_function["regions"]
    declaration_ids = {
        state["name"]: state["id"]
        for state in declaration_document["assets"][0]["states"]
    }
    state_references = {
        node["properties"]["name"]: node["properties"].get("declarationId")
        for node in graph_nodes
        if node["kind"] == "identifier"
        and node["properties"].get("name") in declaration_ids
    }
    kind_counts = {
        kind: sum(node["kind"] == kind for node in graph_nodes)
        for kind in ("entry", "variable", "branch", "assignment", "invocation", "return")
    }
    assignment_debug_targets = {
        node["properties"].get("debugTargetId")
        for node in graph_nodes
        if node["kind"] == "assignment"
    }
    require(
        graph_document["schemaVersion"] == "unifiedscript.graph/v0.13"
        and graph_document["sourceCompilationId"]
        == declaration_document["sourceCompilationId"]
        and graph_document["semanticConfigurationId"]
        == declaration_document["semanticConfigurationId"]
        and len(graph_document["functions"]) == 1
        and graph_function["id"]
        == declaration_document["assets"][0]["functions"][0]["id"]
        and graph_function["rootRegionId"]
        in {region["id"] for region in graph_regions}
        and len(graph_node_ids) == len(graph_nodes)
        and all(
            edge["fromNode"] in graph_node_ids and edge["toNode"] in graph_node_ids
            for edge in graph_function["edges"]
        )
        and kind_counts
        == {
            "entry": 1,
            "variable": 1,
            "branch": 1,
            "assignment": 2,
            "invocation": 1,
            "return": 1,
        }
        and state_references == declaration_ids
        and len(assignment_debug_targets) == 2
        and None not in assignment_debug_targets
        and assignment_debug_targets.isdisjoint(graph_node_ids)
        and sum(region["kind"] == "if" for region in graph_regions) == 1
        and sum(
            region["kind"] == "else"
            and region["isImplicit"] is True
            and region["nodeIds"] == []
            for region in graph_regions
        )
        == 1,
        "Visual-editor Graph artifact is inconsistent with Declaration IR",
    )
    require(
        authoring_source_snapshot.read_bytes() == authoring_source.read_bytes(),
        "Visual-editor source snapshot differs from its authoring source",
    )
    checks.append("visual prototype Graph, Declaration IR, and source identities agree")

    with authoring_surface.open("r", encoding="utf-8") as handle:
        surface_document = json.load(handle)
    surface_asset = surface_document["assets"][0]
    surface_function = surface_asset["functions"][0]
    surface_actions = surface_function["actions"]
    surface_action_ids = {action["id"] for action in surface_actions}
    graph_node_properties = {
        node["id"]: node["properties"] for node in graph_function["nodes"]
    }
    surface_edges = surface_function["edges"]
    surface_ports = {
        (action["id"], port["direction"], port["name"]): port
        for action in surface_actions
        for port in action["ports"]
    }
    surface_regions = surface_function["regions"]
    surface_region_ids = {region["id"] for region in surface_regions}
    owned_action_ids = [
        action_id
        for region in surface_regions
        for action_id in region["directActionIds"]
    ]
    edit_operations = {
        edit["operation"]
        for action in surface_actions
        for edit in action["editCapabilities"]
    }
    require(
        surface_document["schemaVersion"]
        == "unifiedscript.authoring-surface/v0.4"
        and surface_document["sourceCompilationId"]
        == declaration_document["sourceCompilationId"]
        and surface_document["semanticConfigurationId"]
        == declaration_document["semanticConfigurationId"]
        and surface_asset["id"] == declaration_document["assets"][0]["id"]
        and surface_function["id"] == graph_function["id"]
        and surface_function["rootRegionId"] in surface_region_ids
        and len(surface_action_ids) == len(surface_actions)
        and surface_action_ids == graph_node_ids
        and all(
            action["properties"] == graph_node_properties[action["id"]]
            for action in surface_actions
        )
        and len(owned_action_ids) == len(surface_actions)
        and set(owned_action_ids) == surface_action_ids
        and all(
            child_id in surface_region_ids
            for region in surface_regions
            for child_id in region["childRegionIds"]
        )
        and len(surface_edges) == len(graph_function["edges"])
        and all(
            (edge["fromNode"], "output", edge["fromPort"]) in surface_ports
            and (edge["toNode"], "input", edge["toPort"]) in surface_ports
            for edge in surface_edges
        )
        and {"replaceLiteral", "changeOperator"}.issubset(edit_operations),
        "Authoring Surface is inconsistent with Declaration IR and Graph",
    )
    checks.append(
        "Authoring Surface owns every action, Graph property, and typed edit capability"
    )

    declaration_patch_path = (
        ROOT
        / "samples"
        / "Authoring"
        / "MakeMaxDamageEditable.declaration.patch.json"
    )
    require(declaration_patch_path.is_file(), "Missing declaration patch sample")
    with declaration_patch_path.open("r", encoding="utf-8") as handle:
        declaration_patch = json.load(handle)
    max_damage = next(
        state
        for state in declaration_document["assets"][0]["states"]
        if state["name"] == "MaxDamage"
    )
    declaration_operation = declaration_patch["operations"][0]
    require(
        declaration_patch["schemaVersion"]
        == "unifiedscript.declaration-patch/v0.2"
        and declaration_patch["sourceCompilationId"]
        == declaration_document["sourceCompilationId"]
        and declaration_patch["semanticConfigurationId"]
        == declaration_document["semanticConfigurationId"]
        and len(declaration_patch["operations"]) == 1
        and declaration_operation["operation"] == "updateState"
        and declaration_operation["targetStateId"] == max_damage["id"]
        and declaration_operation["expected"]["defaultValue"] == 999
        and declaration_operation["replacement"]["exposure"]["instanceEditable"]
        is True,
        "Declaration patch sample does not match its generated baseline artifact",
    )
    checks.append("Declaration patch sample targets the generated authoring baseline")

    require((ROOT / "samples" / "DoorController.cs").is_file(), "Missing vertical-slice sample")
    runtime_source_path = ROOT / "samples" / "Execution" / "RuntimeRules.cs"
    require(runtime_source_path.is_file(), "Missing CoreCLR execution sample")
    runtime_source = runtime_source_path.read_text(encoding="utf-8")
    cli_source = (ROOT / "src" / "UnifiedScript.Cli" / "Program.cs").read_text(
        encoding="utf-8"
    )
    require(
        "LastNonNegative(IEnumerable<int> values, int seed)" in runtime_source
        and "return Math.Abs(value);" in runtime_source
        and 'args[0] == "execute"' in cli_source
        and 'args[0] is "emit" or "emit-files"' in cli_source
        and "ExecutionJson.Serialize(result)" in cli_source,
        "CoreCLR execution sample or CLI command is inconsistent",
    )
    require(
        "ScriptAssetJson.Serialize(result.ScriptAssets)" in cli_source
        and "ScriptAssetJson.Serialize(result.Image.ScriptAssets)" in cli_source,
        "CLI analysis/emission does not publish Declaration IR",
    )
    project_loader_source = (
        ROOT / "src" / "UnifiedScript.CSharp" / "CSharpProjectLoader.cs"
    ).read_text(encoding="utf-8")
    compilation_request_source = (
        ROOT / "src" / "UnifiedScript.CSharp" / "CSharpCompilationRequest.cs"
    ).read_text(encoding="utf-8")
    project_loader_test = (
        ROOT / "tests" / "UnifiedScript.Tests" / "CSharpProjectLoaderTests.cs"
    ).read_text(encoding="utf-8")
    project_loading_sample = (
        ROOT / "samples" / "ProjectLoading" / "ProjectLoadingDemo.cs"
    ).read_text(encoding="utf-8")
    require(
        "-target:ResolveReferences" in project_loader_source
        and '"Compile"' in project_loader_source
        and '"ReferencePath"' in project_loader_source
        and '"ReferenceCopyLocalPaths"' in project_loader_source
        and "CSharpProjectReferenceKind.Package" in project_loader_source
        and "RuntimeReferencePaths" in compilation_request_source
        and "LoadAnalysisRequest" in cli_source
        and "--no-restore" in cli_source
        and "LoaderUsesMsBuildSourcesConstantsNuGetAndProjectReferences"
        in project_loader_test
        and "UNIFIEDSCRIPT_PROJECT_DEMO" in project_loading_sample,
        "MSBuild project, NuGet, project-reference, or CLI loading coverage is missing",
    )
    checks.append("MSBuild project loading carries sources, settings, NuGet, and runtime references")
    prototype_source = (
        ROOT / "prototype" / "visual-editor" / "app.js"
    ).read_text(encoding="utf-8")
    authoring_projector_source = (
        ROOT
        / "src"
        / "UnifiedScript.Authoring"
        / "AuthoringSurfaceProjector.cs"
    ).read_text(encoding="utf-8")
    authoring_model_source = (
        ROOT / "src" / "UnifiedScript.Authoring" / "AuthoringSurfaceModel.cs"
    ).read_text(encoding="utf-8")
    require(
        'args[0] == "apply-declaration-patch"' in cli_source
        and "new CSharpDeclarationPatchApplier().Apply" in cli_source
        and "ScriptAssetJson.Serialize(outputAnalysis.ScriptAssets)" in cli_source
        and "unifiedscript.declaration-patch/v0.2" in prototype_source
        and "buildDeclarationPatch" in prototype_source,
        "Declaration patch CLI or prototype integration is missing",
    )
    checks.append("Declaration patch CLI and visual preview are connected")
    require(
        "loadWorkspaceArtifacts" in prototype_source
        and "validateWorkspaceArtifacts" in prototype_source
        and "renderBehaviorGraph" in prototype_source
        and "data-semantic-id" in prototype_source
        and "data-debug-semantic-id" in prototype_source
        and "unifiedscript.authoring-surface/v0.4" in prototype_source
        and "unifiedscript.authoring-surface/v0.4" in authoring_model_source
        and "CollectEditCapabilities" in authoring_projector_source
        and "DamagePolicy" not in prototype_source
        and "Math.Clamp" not in prototype_source,
        "Visual prototype is not driven by the generic Authoring Surface",
    )
    checks.append("visual cards and inspectors consume generic Surface identities")
    debug_server_source = (
        ROOT / "src" / "UnifiedScript.Cli" / "CoreClrDebugHttpServer.cs"
    ).read_text(encoding="utf-8")
    require(
        'args[0] == "debug-serve"' in cli_source
        and "CoreClrDebugHttpServer" in cli_source
        and '"/api/debug/start"' in debug_server_source
        and '"/api/debug/wait"' in debug_server_source
        and '"/api/debug/pause"' in debug_server_source
        and '"/api/debug/step-in"' in debug_server_source
        and '"/api/debug/step-over"' in debug_server_source
        and '"/api/debug/step-out"' in debug_server_source
        and '"/api/debug/continue"' in debug_server_source
        and '"/api/debug/evaluate"' in debug_server_source
        and "ScriptAssetReceiver" in debug_server_source
        and "debugTimers" not in prototype_source
        and "CoreCLR debug experience simulation" not in prototype_source
        and "unifiedscript.debug-session/v0.2" in prototype_source
        and "unifiedscript.debug-evaluation/v0.1" in prototype_source
        and "debugFramePicker" in prototype_source
        and "evaluateDebugWatch" in prototype_source,
        "Live CoreCLR visual debug bridge is not connected end to end",
    )
    checks.append("visual pause, full step controls, per-frame locals, and watch use the live CoreCLR bridge")
    authoring_workspace_source = (
        ROOT / "src" / "UnifiedScript.CSharp" / "CSharpAuthoringWorkspace.cs"
    ).read_text(encoding="utf-8")
    require(
        '"/api/authoring/workspace"' in debug_server_source
        and '"/api/authoring/declaration-patch"' in debug_server_source
        and '"/api/authoring/semantic-patch"' in debug_server_source
        and '"/api/authoring/undo"' in debug_server_source
        and '"/api/authoring/redo"' in debug_server_source
        and '"/api/authoring/group/begin"' in debug_server_source
        and '"/api/authoring/group/commit"' in debug_server_source
        and '"/api/authoring/group/cancel"' in debug_server_source
        and "WriteTransactionally" in debug_server_source
        and "EnsureFilesUnchanged" in debug_server_source
        and "CSharpAuthoringMutation" in authoring_workspace_source
        and "CSharpAuthoringHistoryMutation" in authoring_workspace_source
        and "CreateDraft" in authoring_workspace_source
        and "PrepareDraftCommit" in authoring_workspace_source
        and "NavigateHistoryAsync" in debug_server_source
        and "AttachReconciledSidecars" in authoring_workspace_source
        and "saveDeclarationChanges" in prototype_source
        and "saveSemanticPatch" in prototype_source
        and "navigateAuthoringHistory" in prototype_source
        and "toggleAuthoringGroup" in prototype_source
        and "X-UnifiedScript-Group-Id" in prototype_source
        and "X-UnifiedScript-Baseline-Revision" in prototype_source
        and "X-UnifiedScript-Transaction-Id" in prototype_source
        and "unifiedscript.authoring-workspace/v0.5" in prototype_source
        and "AuthoringSurfaceProjector" in debug_server_source
        and "unifiedscript.patch/v0.28" in prototype_source,
        "Visual authoring changes do not commit through the transactional host",
    )
    checks.append("declaration and behavior patches write source and reload one CoreCLR generation")
    authoring_history_source = (
        ROOT / "src" / "UnifiedScript.CSharp" / "CSharpAuthoringHistory.cs"
    )
    require(
        authoring_history_source.is_file()
        and "unifiedscript.authoring-history/v0.1"
        in authoring_history_source.read_text(encoding="utf-8")
        and "CSharpAuthoringWorkspace.Restore" in debug_server_source
        and "PreviewCommit" in debug_server_source
        and "HistoryPath" in debug_server_source
        and 'case "--history"' in cli_source
        and "persistenceState" in prototype_source,
        "Restart-safe transactional authoring history is not connected end to end",
    )
    checks.append("authoring Undo/Redo history is restart-safe, stale-checked, and transactional")
    multi_asset_authoring_source = (
        ROOT / "samples" / "Authoring" / "MultiAssetControlFlow.cs"
    )
    require(
        multi_asset_authoring_source.is_file()
        and multi_asset_authoring_source.read_text(encoding="utf-8").count("[ScriptAsset(")
        == 2
        and "while (value > 0)" in multi_asset_authoring_source.read_text(encoding="utf-8")
        and "for (int index = 0; index < count; index++)"
        in multi_asset_authoring_source.read_text(encoding="utf-8"),
        "Missing generic multi-asset/if/while/for authoring sample",
    )
    checks.append("multi-asset authoring sample covers if, while, and for regions")
    unreal_manifest_path = (
        ROOT / "samples" / "Unreal" / "UEUnifiedScriptRuntime.reflection.json"
    )
    unreal_bindings_path = (
        ROOT / "samples" / "Unreal" / "UEUnifiedScriptRuntime.bindings.cs"
    )
    unreal_native_thunk_generator_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealNativeThunkGenerator.cs"
    )
    unreal_generated_thunks_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "Generated"
        / "UEUnifiedScriptRuntime.native-thunks.generated.cpp"
    )
    unreal_native_thunk_test_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealNativeThunkGeneratorTests.cs"
    )
    unreal_plugin_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "UEUnifiedScript.uplugin"
    )
    unreal_component_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptComponent.h"
    )
    unreal_delegate_proxy_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptDelegateProxy.h"
    )
    unreal_actor_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptActor.h"
    )
    unreal_script_asset_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptAsset.h"
    )
    unreal_script_asset_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UnifiedScriptAsset.cpp"
    )
    unreal_script_asset_test_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "Tests"
        / "UnifiedScriptAssetTests.cpp"
    )
    unreal_declaration_materializer_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptDeclarationMaterializer.h"
    )
    unreal_declaration_materializer_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UnifiedScriptDeclarationMaterializer.cpp"
    )
    unreal_declaration_materializer_test_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "Tests"
        / "UnifiedScriptDeclarationMaterializerTests.cpp"
    )
    unreal_exporter_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "UnifiedScriptReflectionExporter.cpp"
    )
    unreal_registry_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptObjectRegistry.h"
    )
    unreal_registry_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UnifiedScriptObjectRegistry.cpp"
    )
    unreal_registry_test_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "Tests"
        / "UnifiedScriptObjectRegistryTests.cpp"
    )
    unreal_invocation_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptInvocationBridge.h"
    )
    unreal_invocation_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UnifiedScriptInvocationBridge.cpp"
    )
    unreal_invocation_test_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "Tests"
        / "UnifiedScriptInvocationBridgeTests.cpp"
    )
    unreal_native_transport_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptNativeTransport.h"
    )
    unreal_native_transport_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UnifiedScriptNativeTransport.cpp"
    )
    unreal_native_transport_test_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "Tests"
        / "UnifiedScriptNativeTransportTests.cpp"
    )
    unreal_gameplay_data_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptGameplayDataPlane.h"
    )
    unreal_gameplay_data_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UnifiedScriptGameplayDataPlane.cpp"
    )
    unreal_shared_data_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptSharedDataPlane.h"
    )
    unreal_shared_data_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UnifiedScriptSharedDataPlane.cpp"
    )
    unreal_data_plane_schema_path = (
        ROOT / "samples" / "Unreal" / "UEUnifiedScriptRuntime.data-plane.json"
    )
    unreal_data_plane_generator_path = (
        ROOT
        / "src"
        / "UnifiedScript.Unreal"
        / "UnrealDataPlaneLayoutGenerator.cs"
    )
    unreal_data_plane_managed_layout_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealDataPlaneLayouts.g.cs"
    )
    unreal_data_plane_native_layout_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptDataPlaneLayouts.gen.h"
    )
    unreal_data_plane_generator_test_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealDataPlaneLayoutGeneratorTests.cs"
    )
    unreal_data_plane_generator_helper_path = (
        ROOT / "tools" / "generate-unreal-data-plane.ps1"
    )
    unreal_data_plane_profile_demo_path = (
        ROOT
        / "samples"
        / "Unreal"
        / "UnifiedScript.Unreal.Demo"
        / "DataPlaneWorkloadProfileScript.cs"
    )
    unreal_data_plane_profile_helper_path = (
        ROOT / "tools" / "run-unreal-data-plane-profile.ps1"
    )
    unreal_async_service_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptAsyncService.h"
    )
    unreal_async_service_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UnifiedScriptAsyncService.cpp"
    )
    unreal_world_host_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptWorldSubsystem.h"
    )
    unreal_world_host_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UnifiedScriptWorldSubsystem.cpp"
    )
    unreal_world_host_test_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "Tests"
        / "UnifiedScriptWorldSubsystemTests.cpp"
    )
    unreal_lifecycle_stress_test_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "Tests"
        / "UnifiedScriptLifecycleStressTests.cpp"
    )
    unreal_runtime_breadcrumb_test_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "Tests"
        / "UnifiedScriptRuntimeBreadcrumbTests.cpp"
    )
    unreal_coreclr_host_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptCoreClrHost.h"
    )
    unreal_coreclr_host_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UnifiedScriptCoreClrHost.cpp"
    )
    unreal_coreclr_runtime_config_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Resources"
        / "Managed"
        / "UnifiedScript.Unreal.runtimeconfig.json"
    )
    unreal_runtime_build_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "UEUnifiedScriptRuntime.Build.cs"
    )
    unreal_runtime_module_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UEUnifiedScriptRuntimeModule.cpp"
    )
    unreal_managed_bridge_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealManagedBridge.cs"
    )
    unreal_native_adapter_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "NativeUnrealInvocationBridge.cs"
    )
    unreal_native_adapter_test_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "NativeUnrealInvocationBridgeTests.cs"
    )
    unreal_binding_coverage_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealBindingCoverage.cs"
    )
    unreal_binding_coverage_test_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealBindingCoverageTests.cs"
    )
    unreal_in_process_transport_path = (
        ROOT
        / "src"
        / "UnifiedScript.Unreal"
        / "InProcessUnrealNativeInvocationTransport.cs"
    )
    unreal_native_abi_v3_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealNativeAbiV3.cs"
    )
    unreal_native_event_abi_v4_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealNativeEventAbiV4.cs"
    )
    unreal_gameplay_data_abi_v4_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealGameplayDataAbiV4.cs"
    )
    unreal_shared_data_abi_v4_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealSharedDataAbiV4.cs"
    )
    unreal_gameplay_data_abi_v4_test_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealGameplayDataAbiV4Tests.cs"
    )
    unreal_shared_data_abi_v4_test_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealSharedDataAbiV4Tests.cs"
    )
    unreal_async_service_abi_v5_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealAsyncServiceAbiV5.cs"
    )
    unreal_async_service_abi_v5_test_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealAsyncServiceAbiV5Tests.cs"
    )
    unreal_async_service_doc_path = (
        ROOT / "docs" / "UNREAL_ASYNC_SERVICE_ABI_V5.md"
    )
    unreal_gameplay_data_doc_path = (
        ROOT / "docs" / "UNREAL_GAMEPLAY_DATA_ABI_V4.md"
    )
    unreal_generation_invocation_bridge_path = (
        ROOT
        / "src"
        / "UnifiedScript.Unreal"
        / "UnrealGenerationInvocationBridge.cs"
    )
    unreal_event_binder_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealEventBinder.cs"
    )
    unreal_event_binder_test_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealEventBinderTests.cs"
    )
    unreal_native_event_abi_v4_test_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealNativeEventAbiV4Tests.cs"
    )
    unreal_native_call_frame_v3_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealNativeCallFrameV3.cs"
    )
    unreal_in_process_transport_test_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "InProcessUnrealNativeInvocationTransportTests.cs"
    )
    unreal_world_managed_entry_path = (
        ROOT
        / "src"
        / "UnifiedScript.Unreal"
        / "UnrealWorldManagedEntryPoint.cs"
    )
    unreal_runtime_diagnostic_managed_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealRuntimeDiagnostic.cs"
    )
    unreal_script_object_model_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealScriptObjectModel.cs"
    )
    unreal_demo_project_path = (
        ROOT
        / "samples"
        / "Unreal"
        / "UnifiedScript.Unreal.Demo"
        / "UnifiedScript.Unreal.Demo.csproj"
    )
    unreal_demo_source_path = (
        ROOT
        / "samples"
        / "Unreal"
        / "UnifiedScript.Unreal.Demo"
        / "DemoScript.cs"
    )
    unreal_benchmark_demo_source_path = (
        ROOT
        / "samples"
        / "Unreal"
        / "UnifiedScript.Unreal.Demo"
        / "ManagedInteropBenchmarkScript.cs"
    )
    unreal_benchmark_helper_path = ROOT / "tools" / "run-unreal-benchmark.ps1"
    unreal_lifecycle_stress_helper_path = (
        ROOT / "tools" / "run-unreal-lifecycle-stress.ps1"
    )
    unreal_reliability_soak_helper_path = (
        ROOT / "tools" / "run-unreal-reliability-soak.ps1"
    )
    unreal_benchmark_thresholds_path = (
        ROOT / "benchmarks" / "unreal" / "performance-thresholds.json"
    )
    unreal_performance_doc_path = ROOT / "docs" / "UNREAL_PERFORMANCE.md"
    unreal_actor_demo_source_path = (
        ROOT
        / "samples"
        / "Unreal"
        / "UnifiedScript.Unreal.Demo"
        / "ActorDemoScript.cs"
    )
    unreal_lifecycle_stress_demo_source_path = (
        ROOT
        / "samples"
        / "Unreal"
        / "UnifiedScript.Unreal.Demo"
        / "LifecycleStressScript.cs"
    )
    unreal_demo_declaration_path = (
        ROOT
        / "samples"
        / "Unreal"
        / "UnifiedScript.Unreal.Demo"
        / "UnifiedScript.Unreal.Demo.asset.json"
    )
    unreal_demo_surface_path = (
        ROOT
        / "samples"
        / "Unreal"
        / "UnifiedScript.Unreal.Demo"
        / "UnifiedScript.Unreal.Demo.surface.json"
    )
    unreal_authoring_surface_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Public"
        / "UnifiedScriptAuthoringSurface.h"
    )
    unreal_authoring_surface_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "UnifiedScriptAuthoringSurface.cpp"
    )
    unreal_authoring_workbench_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Public"
        / "UnifiedScriptAuthoringWorkbench.h"
    )
    unreal_authoring_workbench_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "UnifiedScriptAuthoringWorkbench.cpp"
    )
    unreal_debug_workbench_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Public"
        / "UnifiedScriptDebugWorkbench.h"
    )
    unreal_debug_workbench_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "UnifiedScriptDebugWorkbench.cpp"
    )
    unreal_debug_panel_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "Widgets"
        / "SUnifiedScriptDebugPanel.cpp"
    )
    unreal_runtime_diagnostic_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Public"
        / "UnifiedScriptRuntimeDiagnostic.h"
    )
    unreal_runtime_diagnostic_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "UnifiedScriptRuntimeDiagnostic.cpp"
    )
    unreal_authoring_widget_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "Widgets"
        / "SUnifiedScriptAuthoringSurface.cpp"
    )
    unreal_ed_graph_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Public"
        / "UnifiedScriptEdGraph.h"
    )
    unreal_ed_graph_source_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "UnifiedScriptEdGraph.cpp"
    )
    unreal_graph_layout_store_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "Graph"
        / "UnifiedScriptGraphLayoutStore.cpp"
    )
    unreal_graph_presentation_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "Graph"
        / "UnifiedScriptGraphPresentation.cpp"
    )
    unreal_node_palette_header_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "Graph"
        / "UnifiedScriptNodePalette.h"
    )
    unreal_node_palette_source_path = unreal_node_palette_header_path.with_suffix(".cpp")
    unreal_authoring_test_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "Tests"
        / "UnifiedScriptAuthoringSurfaceTests.cpp"
    )
    unreal_editor_module_path = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptEditor"
        / "Private"
        / "UEUnifiedScriptEditorModule.cpp"
    )
    unreal_alternate_demo_source_path = (
        ROOT
        / "samples"
        / "Unreal"
        / "UnifiedScript.Unreal.Demo"
        / "AlternateDemoScript.cs"
    )
    unreal_test_project_path = (
        ROOT / "tests" / "UnifiedScript.Tests" / "UnifiedScript.Tests.csproj"
    )
    unreal_game_target_path = (
        ROOT / "HostProject" / "Source" / "UEUnifiedScriptHost.Target.cs"
    )
    unreal_editor_target_path = (
        ROOT / "HostProject" / "Source" / "UEUnifiedScriptHostEditor.Target.cs"
    )
    unreal_helper_path = ROOT / "tools" / "ue-adapter.sh"
    unreal_demo_helper_path = ROOT / "tools" / "run-unreal-demo.ps1"
    unreal_authoring_helper_path = ROOT / "tools" / "run-unreal-authoring.ps1"
    unreal_release_helper_path = ROOT / "tools" / "package-unreal-release.ps1"
    unreal_release_doc_path = ROOT / "docs" / "UNREAL_RELEASE.md"
    unreal_compatibility_helper_path = (
        ROOT / "tools" / "run-unreal-compatibility-matrix.ps1"
    )
    unreal_compatibility_doc_path = (
        ROOT / "docs" / "UNREAL_COMPATIBILITY_MATRIX.md"
    )
    helper_is_executable = sys.platform == "win32" or (
        unreal_helper_path.stat().st_mode & 0o111
    ) != 0
    require(
        unreal_manifest_path.is_file()
        and unreal_bindings_path.is_file()
        and unreal_native_thunk_generator_path.is_file()
        and unreal_generated_thunks_path.is_file()
        and unreal_native_thunk_test_path.is_file()
        and unreal_plugin_path.is_file()
        and unreal_component_path.is_file()
        and unreal_delegate_proxy_path.is_file()
        and unreal_actor_path.is_file()
        and unreal_script_asset_header_path.is_file()
        and unreal_script_asset_source_path.is_file()
        and unreal_script_asset_test_path.is_file()
        and unreal_declaration_materializer_header_path.is_file()
        and unreal_declaration_materializer_source_path.is_file()
        and unreal_declaration_materializer_test_path.is_file()
        and unreal_exporter_path.is_file()
        and unreal_registry_header_path.is_file()
        and unreal_registry_source_path.is_file()
        and unreal_registry_test_path.is_file()
        and unreal_invocation_header_path.is_file()
        and unreal_invocation_source_path.is_file()
        and unreal_invocation_test_path.is_file()
        and unreal_native_transport_header_path.is_file()
        and unreal_native_transport_source_path.is_file()
        and unreal_native_transport_test_path.is_file()
        and unreal_gameplay_data_header_path.is_file()
        and unreal_gameplay_data_source_path.is_file()
        and unreal_shared_data_header_path.is_file()
        and unreal_shared_data_source_path.is_file()
        and unreal_data_plane_schema_path.is_file()
        and unreal_data_plane_generator_path.is_file()
        and unreal_data_plane_managed_layout_path.is_file()
        and unreal_data_plane_native_layout_path.is_file()
        and unreal_data_plane_generator_test_path.is_file()
        and unreal_data_plane_generator_helper_path.is_file()
        and unreal_data_plane_profile_demo_path.is_file()
        and unreal_data_plane_profile_helper_path.is_file()
        and unreal_async_service_header_path.is_file()
        and unreal_async_service_source_path.is_file()
        and unreal_world_host_header_path.is_file()
        and unreal_world_host_source_path.is_file()
        and unreal_world_host_test_path.is_file()
        and unreal_lifecycle_stress_test_path.is_file()
        and unreal_runtime_breadcrumb_test_path.is_file()
        and unreal_compatibility_helper_path.is_file()
        and unreal_compatibility_doc_path.is_file()
        and unreal_coreclr_host_header_path.is_file()
        and unreal_coreclr_host_source_path.is_file()
        and unreal_coreclr_runtime_config_path.is_file()
        and unreal_runtime_build_path.is_file()
        and unreal_runtime_module_path.is_file()
        and unreal_managed_bridge_path.is_file()
        and unreal_native_adapter_path.is_file()
        and unreal_native_adapter_test_path.is_file()
        and unreal_binding_coverage_path.is_file()
        and unreal_binding_coverage_test_path.is_file()
        and unreal_in_process_transport_path.is_file()
        and unreal_native_abi_v3_path.is_file()
        and unreal_native_event_abi_v4_path.is_file()
        and unreal_gameplay_data_abi_v4_path.is_file()
        and unreal_gameplay_data_abi_v4_test_path.is_file()
        and unreal_shared_data_abi_v4_path.is_file()
        and unreal_shared_data_abi_v4_test_path.is_file()
        and unreal_gameplay_data_doc_path.is_file()
        and unreal_async_service_abi_v5_path.is_file()
        and unreal_async_service_abi_v5_test_path.is_file()
        and unreal_async_service_doc_path.is_file()
        and unreal_generation_invocation_bridge_path.is_file()
        and unreal_event_binder_path.is_file()
        and unreal_event_binder_test_path.is_file()
        and unreal_native_event_abi_v4_test_path.is_file()
        and unreal_native_call_frame_v3_path.is_file()
        and unreal_in_process_transport_test_path.is_file()
        and unreal_world_managed_entry_path.is_file()
        and unreal_runtime_diagnostic_managed_path.is_file()
        and unreal_script_object_model_path.is_file()
        and unreal_demo_project_path.is_file()
        and unreal_demo_source_path.is_file()
        and unreal_benchmark_demo_source_path.is_file()
        and unreal_benchmark_helper_path.is_file()
        and unreal_lifecycle_stress_helper_path.is_file()
        and unreal_benchmark_thresholds_path.is_file()
        and unreal_performance_doc_path.is_file()
        and unreal_actor_demo_source_path.is_file()
        and unreal_lifecycle_stress_demo_source_path.is_file()
        and unreal_demo_declaration_path.is_file()
        and unreal_demo_surface_path.is_file()
        and unreal_authoring_surface_header_path.is_file()
        and unreal_authoring_surface_source_path.is_file()
        and unreal_authoring_workbench_header_path.is_file()
        and unreal_authoring_workbench_source_path.is_file()
        and unreal_debug_workbench_header_path.is_file()
        and unreal_debug_workbench_source_path.is_file()
        and unreal_debug_panel_path.is_file()
        and unreal_runtime_diagnostic_header_path.is_file()
        and unreal_runtime_diagnostic_source_path.is_file()
        and unreal_authoring_widget_path.is_file()
        and unreal_ed_graph_header_path.is_file()
        and unreal_ed_graph_source_path.is_file()
        and unreal_graph_layout_store_path.is_file()
        and unreal_graph_presentation_path.is_file()
        and unreal_node_palette_header_path.is_file()
        and unreal_node_palette_source_path.is_file()
        and unreal_authoring_test_path.is_file()
        and unreal_editor_module_path.is_file()
        and unreal_alternate_demo_source_path.is_file()
        and unreal_test_project_path.is_file()
        and unreal_game_target_path.is_file()
        and unreal_editor_target_path.is_file()
        and unreal_helper_path.is_file()
        and unreal_demo_helper_path.is_file()
        and unreal_authoring_helper_path.is_file()
        and unreal_release_helper_path.is_file()
        and unreal_release_doc_path.is_file()
        and helper_is_executable,
        "Missing Unreal adapter contract, plugin, fixture, or executable helper",
    )
    with unreal_manifest_path.open("r", encoding="utf-8") as handle:
        unreal_manifest = json.load(handle)
    with unreal_plugin_path.open("r", encoding="utf-8") as handle:
        unreal_plugin = json.load(handle)
    unreal_component = unreal_component_path.read_text(encoding="utf-8")
    unreal_delegate_proxy = unreal_delegate_proxy_path.read_text(encoding="utf-8")
    unreal_actor = unreal_actor_path.read_text(encoding="utf-8")
    unreal_script_asset_header = unreal_script_asset_header_path.read_text(
        encoding="utf-8"
    )
    unreal_script_asset_source = unreal_script_asset_source_path.read_text(
        encoding="utf-8"
    )
    unreal_script_asset_test = unreal_script_asset_test_path.read_text(encoding="utf-8")
    unreal_declaration_materializer_header = (
        unreal_declaration_materializer_header_path.read_text(encoding="utf-8")
    )
    unreal_declaration_materializer_source = (
        unreal_declaration_materializer_source_path.read_text(encoding="utf-8")
    )
    unreal_declaration_materializer_test = (
        unreal_declaration_materializer_test_path.read_text(encoding="utf-8")
    )
    managed_reload_domain_test = (
        ROOT / "tests" / "UnifiedScript.Tests" / "CoreClrReloadDomainTests.cs"
    ).read_text(encoding="utf-8")
    unreal_exporter = unreal_exporter_path.read_text(encoding="utf-8")
    unreal_bindings = unreal_bindings_path.read_text(encoding="utf-8")
    unreal_native_thunk_generator = unreal_native_thunk_generator_path.read_text(
        encoding="utf-8"
    )
    unreal_generated_thunks = unreal_generated_thunks_path.read_text(encoding="utf-8")
    unreal_native_thunk_test = unreal_native_thunk_test_path.read_text(encoding="utf-8")
    unreal_registry_header = unreal_registry_header_path.read_text(encoding="utf-8")
    unreal_registry_source = unreal_registry_source_path.read_text(encoding="utf-8")
    unreal_registry_test = unreal_registry_test_path.read_text(encoding="utf-8")
    unreal_invocation_header = unreal_invocation_header_path.read_text(encoding="utf-8")
    unreal_invocation_source = unreal_invocation_source_path.read_text(encoding="utf-8")
    unreal_invocation_test = unreal_invocation_test_path.read_text(encoding="utf-8")
    unreal_native_transport_header = unreal_native_transport_header_path.read_text(
        encoding="utf-8"
    )
    unreal_native_transport_source = unreal_native_transport_source_path.read_text(
        encoding="utf-8"
    )
    unreal_native_transport_test = unreal_native_transport_test_path.read_text(
        encoding="utf-8"
    )
    unreal_gameplay_data_header = unreal_gameplay_data_header_path.read_text(
        encoding="utf-8"
    )
    unreal_gameplay_data_source = unreal_gameplay_data_source_path.read_text(
        encoding="utf-8"
    )
    unreal_shared_data_header = unreal_shared_data_header_path.read_text(
        encoding="utf-8"
    )
    unreal_shared_data_source = unreal_shared_data_source_path.read_text(
        encoding="utf-8"
    )
    with unreal_data_plane_schema_path.open("r", encoding="utf-8") as handle:
        unreal_data_plane_schema = json.load(handle)
    unreal_data_plane_generator = unreal_data_plane_generator_path.read_text(
        encoding="utf-8"
    )
    unreal_data_plane_managed_layout = (
        unreal_data_plane_managed_layout_path.read_text(encoding="utf-8")
    )
    unreal_data_plane_native_layout = (
        unreal_data_plane_native_layout_path.read_text(encoding="utf-8")
    )
    unreal_data_plane_generator_test = (
        unreal_data_plane_generator_test_path.read_text(encoding="utf-8")
    )
    unreal_data_plane_generator_helper = (
        unreal_data_plane_generator_helper_path.read_text(encoding="utf-8")
    )
    unreal_data_plane_profile_demo = unreal_data_plane_profile_demo_path.read_text(
        encoding="utf-8"
    )
    unreal_data_plane_profile_helper = (
        unreal_data_plane_profile_helper_path.read_text(encoding="utf-8")
    )
    unreal_world_host_header = unreal_world_host_header_path.read_text(encoding="utf-8")
    unreal_world_host_source = unreal_world_host_source_path.read_text(encoding="utf-8")
    unreal_world_host_test = unreal_world_host_test_path.read_text(encoding="utf-8")
    unreal_lifecycle_stress_test = unreal_lifecycle_stress_test_path.read_text(
        encoding="utf-8"
    )
    unreal_runtime_breadcrumb_test = unreal_runtime_breadcrumb_test_path.read_text(
        encoding="utf-8"
    )
    unreal_coreclr_host_header = unreal_coreclr_host_header_path.read_text(
        encoding="utf-8"
    )
    unreal_coreclr_host_source = unreal_coreclr_host_source_path.read_text(
        encoding="utf-8"
    )
    unreal_runtime_build = unreal_runtime_build_path.read_text(encoding="utf-8")
    unreal_runtime_module = unreal_runtime_module_path.read_text(encoding="utf-8")
    with unreal_coreclr_runtime_config_path.open("r", encoding="utf-8") as handle:
        unreal_coreclr_runtime_config = json.load(handle)
    unreal_managed_bridge = unreal_managed_bridge_path.read_text(encoding="utf-8")
    unreal_native_adapter = unreal_native_adapter_path.read_text(encoding="utf-8")
    unreal_native_adapter_test = unreal_native_adapter_test_path.read_text(encoding="utf-8")
    unreal_binding_coverage = unreal_binding_coverage_path.read_text(encoding="utf-8")
    unreal_binding_coverage_test = unreal_binding_coverage_test_path.read_text(
        encoding="utf-8"
    )
    unreal_runtime_source_root = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
    )
    handwritten_dispatch_files = []
    for source_path in [
        *unreal_runtime_source_root.rglob("*.cpp"),
        *unreal_runtime_source_root.rglob("*.h"),
    ]:
        relative_parts = source_path.relative_to(unreal_runtime_source_root).parts
        if "Generated" in relative_parts or "Tests" in relative_parts:
            continue
        source_text = source_path.read_text(encoding="utf-8")
        if re.search(r"case\s+0x[0-9A-Fa-f]{16}(?:ull|UL)", source_text) or re.search(
            r"switch\s*\(\s*MemberId\s*\)", source_text
        ):
            handwritten_dispatch_files.append(str(source_path))
    binding_matrix_fixture_root = (
        ROOT / "tests" / "UnifiedScript.Tests" / "Fixtures"
    )
    binding_matrix_golden_files = [
        binding_matrix_fixture_root / "UnrealBindingMatrix.reflection.json",
        binding_matrix_fixture_root / "UnrealBindingMatrix.api-contract.json",
        binding_matrix_fixture_root / "UnrealBindingMatrix.bindings.golden.cs",
        binding_matrix_fixture_root / "UnrealBindingMatrix.thunks.golden.cpp",
        binding_matrix_fixture_root / "UnrealBindingMatrix.nodes.golden.json",
        binding_matrix_fixture_root / "UnrealBindingMatrix.coverage.golden.json",
    ]
    unreal_in_process_transport = unreal_in_process_transport_path.read_text(
        encoding="utf-8"
    )
    unreal_native_abi_v3 = unreal_native_abi_v3_path.read_text(encoding="utf-8")
    unreal_native_event_abi_v4 = unreal_native_event_abi_v4_path.read_text(
        encoding="utf-8"
    )
    unreal_gameplay_data_abi_v4 = unreal_gameplay_data_abi_v4_path.read_text(
        encoding="utf-8"
    )
    unreal_shared_data_abi_v4 = unreal_shared_data_abi_v4_path.read_text(
        encoding="utf-8"
    )
    unreal_gameplay_data_abi_v4_test = (
        unreal_gameplay_data_abi_v4_test_path.read_text(encoding="utf-8")
    )
    unreal_shared_data_abi_v4_test = (
        unreal_shared_data_abi_v4_test_path.read_text(encoding="utf-8")
    )
    unreal_gameplay_data_doc = unreal_gameplay_data_doc_path.read_text(
        encoding="utf-8"
    )
    unreal_async_service_header = unreal_async_service_header_path.read_text(
        encoding="utf-8"
    )
    unreal_async_service_source = unreal_async_service_source_path.read_text(
        encoding="utf-8"
    )
    unreal_async_service_abi_v5 = unreal_async_service_abi_v5_path.read_text(
        encoding="utf-8"
    )
    unreal_async_service_abi_v5_test = (
        unreal_async_service_abi_v5_test_path.read_text(encoding="utf-8")
    )
    unreal_async_service_doc = unreal_async_service_doc_path.read_text(
        encoding="utf-8"
    )
    unreal_generation_invocation_bridge = (
        unreal_generation_invocation_bridge_path.read_text(encoding="utf-8")
    )
    unreal_event_binder = unreal_event_binder_path.read_text(encoding="utf-8")
    unreal_event_binder_test = unreal_event_binder_test_path.read_text(
        encoding="utf-8"
    )
    unreal_native_event_abi_v4_test = (
        unreal_native_event_abi_v4_test_path.read_text(encoding="utf-8")
    )
    unreal_native_call_frame_v3 = unreal_native_call_frame_v3_path.read_text(
        encoding="utf-8"
    )
    unreal_in_process_transport_test = unreal_in_process_transport_test_path.read_text(
        encoding="utf-8"
    )
    unreal_world_managed_entry = unreal_world_managed_entry_path.read_text(
        encoding="utf-8"
    )
    unreal_runtime_diagnostic_managed = (
        unreal_runtime_diagnostic_managed_path.read_text(encoding="utf-8")
    )
    unreal_script_object_model = unreal_script_object_model_path.read_text(
        encoding="utf-8"
    )
    unreal_demo_project = unreal_demo_project_path.read_text(encoding="utf-8")
    unreal_demo_source = unreal_demo_source_path.read_text(encoding="utf-8")
    unreal_benchmark_demo_source = unreal_benchmark_demo_source_path.read_text(
        encoding="utf-8"
    )
    unreal_benchmark_helper = unreal_benchmark_helper_path.read_text(
        encoding="utf-8"
    )
    unreal_lifecycle_stress_helper = unreal_lifecycle_stress_helper_path.read_text(
        encoding="utf-8"
    )
    unreal_reliability_soak_helper = unreal_reliability_soak_helper_path.read_text(
        encoding="utf-8"
    )
    with unreal_benchmark_thresholds_path.open("r", encoding="utf-8") as handle:
        unreal_benchmark_thresholds = json.load(handle)
    unreal_performance_doc = unreal_performance_doc_path.read_text(encoding="utf-8")
    unreal_actor_demo_source = unreal_actor_demo_source_path.read_text(encoding="utf-8")
    unreal_lifecycle_stress_demo_source = (
        unreal_lifecycle_stress_demo_source_path.read_text(encoding="utf-8")
    )
    with unreal_demo_declaration_path.open("r", encoding="utf-8") as handle:
        unreal_demo_declaration = json.load(handle)
    with unreal_demo_surface_path.open("r", encoding="utf-8") as handle:
        unreal_demo_surface = json.load(handle)
    unreal_authoring_surface_header = unreal_authoring_surface_header_path.read_text(
        encoding="utf-8"
    )
    unreal_authoring_surface_source = unreal_authoring_surface_source_path.read_text(
        encoding="utf-8"
    )
    unreal_authoring_workbench_header = unreal_authoring_workbench_header_path.read_text(
        encoding="utf-8"
    )
    unreal_authoring_workbench_source = unreal_authoring_workbench_source_path.read_text(
        encoding="utf-8"
    )
    unreal_debug_workbench_header = unreal_debug_workbench_header_path.read_text(
        encoding="utf-8"
    )
    unreal_debug_workbench_source = unreal_debug_workbench_source_path.read_text(
        encoding="utf-8"
    )
    unreal_debug_panel = unreal_debug_panel_path.read_text(encoding="utf-8")
    unreal_runtime_diagnostic_header = unreal_runtime_diagnostic_header_path.read_text(
        encoding="utf-8"
    )
    unreal_runtime_diagnostic_source = unreal_runtime_diagnostic_source_path.read_text(
        encoding="utf-8"
    )
    unreal_authoring_widget = unreal_authoring_widget_path.read_text(encoding="utf-8")
    unreal_ed_graph_header = unreal_ed_graph_header_path.read_text(encoding="utf-8")
    unreal_ed_graph_source = unreal_ed_graph_source_path.read_text(encoding="utf-8")
    unreal_graph_layout_store = unreal_graph_layout_store_path.read_text(
        encoding="utf-8"
    )
    unreal_graph_presentation = unreal_graph_presentation_path.read_text(
        encoding="utf-8"
    )
    unreal_node_palette_header = unreal_node_palette_header_path.read_text(
        encoding="utf-8"
    )
    unreal_node_palette_source = unreal_node_palette_source_path.read_text(
        encoding="utf-8"
    )
    unreal_authoring_test = unreal_authoring_test_path.read_text(encoding="utf-8")
    unreal_editor_module = unreal_editor_module_path.read_text(encoding="utf-8")
    unreal_alternate_demo_source = unreal_alternate_demo_source_path.read_text(
        encoding="utf-8"
    )
    unreal_demo_helper = unreal_demo_helper_path.read_text(encoding="utf-8")
    unreal_authoring_helper = unreal_authoring_helper_path.read_text(encoding="utf-8")
    unreal_release_helper = unreal_release_helper_path.read_text(encoding="utf-8")
    unreal_release_doc = unreal_release_doc_path.read_text(encoding="utf-8")
    unreal_compatibility_helper = unreal_compatibility_helper_path.read_text(
        encoding="utf-8"
    )
    unreal_compatibility_doc = unreal_compatibility_doc_path.read_text(
        encoding="utf-8"
    )
    unreal_test_project = unreal_test_project_path.read_text(encoding="utf-8")
    unreal_game_target = unreal_game_target_path.read_text(encoding="utf-8")
    unreal_editor_target = unreal_editor_target_path.read_text(encoding="utf-8")
    unreal_component_manifest = next(
        item
        for item in unreal_manifest["types"]
        if item["path"]
        == "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent"
    )
    unreal_interop_manifest = next(
        item
        for item in unreal_manifest["types"]
        if item["path"]
        == "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture"
    )
    unreal_actor_manifest = next(
        item
        for item in unreal_manifest["types"]
        if item["path"] == "/Script/UEUnifiedScriptRuntime.UnifiedScriptActor"
    )
    require(
        unreal_manifest["schemaVersion"]
        == "unifiedscript.unreal-reflection/v0.4"
        and unreal_manifest["modules"] == ["Engine", "UEUnifiedScriptRuntime"]
        and {item["path"] for item in unreal_manifest["types"]}
        == {
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptActor",
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptAsset",
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent",
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropContract",
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture",
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptWorldLibrary",
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptWorldSubsystem",
            "/Script/Engine.ActorInstanceHandle",
            "/Script/Engine.HitResult",
            "/Script/UEUnifiedScriptRuntime.EUnifiedScriptInteropMode",
        }
        and unreal_component_manifest["headerPath"] == "UnifiedScriptComponent.h"
        and all(
            item["access"]["nativeAccess"] == "public"
            for item in unreal_component_manifest["properties"]
        )
        and all(
            item["traits"]["nativeAccess"] == "public"
            for item in unreal_component_manifest["functions"]
        )
        and {item["name"] for item in unreal_component_manifest["properties"]}
        == {
            "ActiveGeneration",
            "ActiveDeclarationAssetPath",
            "ActiveScriptAssetPath",
            "LastGameplayProfileTrace",
            "LastReloadTransaction",
            "LastScriptError",
            "LastStateMigration",
            "ManagedState",
            "MaterializedStateCount",
            "MaterializedStateOverrideCount",
            "ScriptAssemblyPath",
            "ScriptAssetPath",
            "ScriptTypeName",
            "bScriptRunning",
            "bGameplayProfilerEnabled",
            "bStartAutomatically",
            "bTickAutomatically",
        }
        and {item["name"] for item in unreal_component_manifest["functions"]}
        == {
            "Add",
            "AddBenchmarkVector",
            "ApplyBenchmarkActorInput",
            "BroadcastBenchmarkSignal",
            "BroadcastManagedSignal",
            "EchoBenchmarkArray",
            "EchoBenchmarkMap",
            "GetScriptStateInt32",
            "GetScriptTarget",
            "IsScriptStateOverridden",
            "RefreshScriptState",
            "ReloadScript",
            "RequestReload",
            "ResetAllScriptStateOverrides",
            "ResetBenchmarkActorState",
            "ResetScriptStateOverride",
            "SetScriptStateInt32",
            "StartScript",
            "StopScript",
        }
        and unreal_actor_manifest["headerPath"] == "UnifiedScriptActor.h"
        and {item["name"] for item in unreal_actor_manifest["properties"]}
        == {"ManagedActorState", "ManagedTickCount", "ScriptComponent"}
        and {item["name"] for item in unreal_actor_manifest["functions"]}
        == {
            "GetUnifiedScriptComponent",
            "IncrementManagedTickCount",
            "ReloadManagedBehavior",
            "SetManagedActorState",
            "StartManagedBehavior",
            "StopManagedBehavior",
        }
        and unreal_interop_manifest["headerPath"]
        == "UnifiedScriptInteropFixture.h"
        and {item["name"] for item in unreal_interop_manifest["properties"]}
        == {
            "ByteValue",
            "DoubleValue",
            "FloatValue",
            "Int32Value",
            "Int64Value",
            "IntArrayValue",
            "IntMapValue",
            "IntSetValue",
            "Mode",
            "NameValue",
            "ObjectValue",
            "ClassValue",
            "RotatorValue",
            "SoftClassValue",
            "SoftObjectValue",
            "StringValue",
            "TextValue",
            "TransformValue",
            "VectorValue",
        }
        and {item["name"] for item in unreal_interop_manifest["functions"]}
        == {
            "AddInt64",
            "AddWithDefault",
            "AddVector",
            "BroadcastInteropChanged",
            "CountSet",
            "EchoArray",
            "EchoClass",
            "EchoMap",
            "EchoObject",
            "EchoSet",
            "EchoTransform",
            "FixedArity0",
            "FixedArity1",
            "FixedArity2",
            "FixedArity3",
            "FixedArity4",
            "MakeArray",
            "MutateValues",
            "StringLength",
            "SumArray",
            "SumMap",
        }
        and any(
            item["path"]
            == "/Script/UEUnifiedScriptRuntime.UnifiedScriptAsset"
            for item in unreal_manifest["types"]
        )
        and any(
            item["path"]
            == "/Script/UEUnifiedScriptRuntime.UnifiedScriptWorldSubsystem"
            for item in unreal_manifest["types"]
        )
        and not any(
            item["path"]
            == "/Script/UEUnifiedScriptRuntime.UnifiedScriptDelegateProxy"
            for item in unreal_manifest["types"]
        )
        and {
            item["path"]
            for reflected_type in unreal_manifest["types"]
            for item in reflected_type["delegates"]
        }
        == {
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnBenchmarkSignal",
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnManagedSignal",
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:OnInteropChanged",
        }
        and all(
            item["traits"]["kind"] == "multicast"
            and item["traits"]["blueprintAssignable"] is True
            for reflected_type in unreal_manifest["types"]
            for item in reflected_type["delegates"]
        )
        and {
            item["path"]: [
                parameter["type"]["kind"] for parameter in item["parameters"]
            ]
            for reflected_type in unreal_manifest["types"]
            for item in reflected_type["delegates"]
        }
        == {
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnBenchmarkSignal": [
                "int32",
                "float32",
                "boolean",
                "object",
            ],
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnManagedSignal": [
                "int32",
                "object",
            ],
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:OnInteropChanged": [
                "int32",
                "object",
            ],
        }
        and {module["Name"] for module in unreal_plugin["Modules"]}
        == {"UEUnifiedScriptRuntime", "UEUnifiedScriptEditor"}
        and "UUnifiedScriptComponent : public UActorComponent" in unreal_component
        and "unifiedscript.unreal-reflection/v0.4" in unreal_exporter
        and "UnifiedScriptBindingInternal" in unreal_exporter
        and "UnifiedScriptBindingInternal" in unreal_delegate_proxy
        and "TObjectIterator<UClass>" in unreal_exporter
        and "IUnrealInvocationBridge" in unreal_bindings
        and "UnrealEventEndpointAttribute" in unreal_bindings
        and "SubscribeOnManagedSignal" in unreal_bindings
        and "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:Add"
        in unreal_bindings
        and 'args[0] == "ue-bindings"' in cli_source,
        "Unreal reflection fixture, plugin, exporter, bindings, or CLI is inconsistent",
    )
    checks.append("UE reflection export and managed binding vertical slice is connected")
    require(
        "FUnifiedScriptObjectRegistry" in unreal_registry_header
        and "EUnifiedScriptObjectHandleError" in unreal_registry_header
        and "FUObjectArray::FUObjectDeleteListener" in unreal_registry_source
        and "TWeakObjectPtr<UObject>" in unreal_registry_source
        and "IsInGameThread()" in unreal_registry_source
        and "AllocateGeneration" in unreal_registry_source
        and "std::atomic<uint32>" in unreal_registry_source
        and "ReferenceCount" in unreal_registry_source
        and "First release preserves the duplicate owner" in unreal_registry_test
        and "ObjectRegistry.Lifecycle" in unreal_registry_test
        and "CollectGarbage" in unreal_registry_test
        and "record struct UnrealObjectHandle(ulong Value)" in unreal_managed_bridge
        and "slotIndex + 1UL" in unreal_managed_bridge
        and "BuildSettingsVersion.Latest" in unreal_game_target
        and "EngineIncludeOrderVersion.Latest" in unreal_game_target
        and "BuildSettingsVersion.Latest" in unreal_editor_target
        and "EngineIncludeOrderVersion.Latest" in unreal_editor_target,
        "Unreal weak-handle lifecycle boundary or UE 5.7/5.8 target compatibility is missing",
    )
    checks.append("UE weak handles are generation-checked, world-bound, and deletion-aware")
    require(
        "FUnifiedScriptInvocationBridge" in unreal_invocation_header
        and "EUnifiedScriptInvocationError" in unreal_invocation_header
        and "ObjectHandleFailure" in unreal_invocation_header
        and "Registry.ResolveObject" in unreal_invocation_source
        and "FStructOnScope" in unreal_invocation_source
        and "ProcessEvent" in unreal_invocation_source
        and "CPF_BlueprintVisible" in unreal_invocation_source
        and "CPF_BlueprintReadOnly" in unreal_invocation_source
        and "InvocationBridge.Primitives" in unreal_invocation_test
        and "ValueTypeMismatch" in unreal_invocation_test
        and "StaleHandle" in unreal_invocation_test,
        "Unreal primitive property/function invocation boundary is missing",
    )
    checks.append("UE primitive invocation resolves handles before property access or ProcessEvent")
    require(
        "NativeUnrealInvocationBridge" in unreal_native_adapter
        and "IUnrealFastInvocationBridge" in unreal_native_adapter
        and "IUnrealNativeInvocationTransport" in unreal_native_adapter
        and "UnrealInvocationException" in unreal_native_adapter
        and "StaleHandle = 6" in unreal_native_adapter
        and "UnsupportedParameter = 13" in unreal_native_adapter
        and "GeneratedComponent" in unreal_native_adapter_test
        and "CheckedInGeneratedWrapperUsesManagedNativeAdapter"
        in unreal_native_adapter_test
        and "NativeFailurePreservesInvocationAndHandleCategories"
        in unreal_native_adapter_test
        and "<Compile Include=\"../../samples/Unreal/UEUnifiedScriptRuntime.bindings.cs\""
        in unreal_test_project,
        "Managed Unreal invocation adapter or generated-wrapper integration is missing",
    )
    checks.append("generated UE wrapper uses the managed native invocation adapter contract")
    require(
        "FUnifiedScriptNativeTransportV1" in unreal_native_transport_header
        and "FUnifiedScriptNativeResultCallbackV1" in unreal_native_transport_header
        and "UEUnifiedScript_GetNativeTransportV1" in unreal_native_transport_header
        and "AbiVersion" in unreal_native_transport_header
        and "TableSize" in unreal_native_transport_header
        and "ValueSize" in unreal_native_transport_header
        and "RetainContext" in unreal_native_transport_header
        and "ReleaseContext" in unreal_native_transport_header
        and "UEUnifiedScript_GetNativeTransportV1" in unreal_native_transport_source
        and "NativeTransport.Abi" in unreal_native_transport_test
        and "InProcessUnrealNativeInvocationTransport"
        in unreal_in_process_transport
        and "UnrealNativeResultCallbackV1" in unreal_in_process_transport
        and "IDisposable" in unreal_in_process_transport
        and "retainContext(_context)" in unreal_in_process_transport
        and "_releaseContext(_context)" in unreal_in_process_transport
        and "GeneratedWrapperCrossesVersionedFunctionTable"
        in unreal_in_process_transport_test
        and "ConstructorRejectsAbiLayoutMismatch"
        in unreal_in_process_transport_test,
        "Versioned Unreal native transport ABI or managed process adapter is missing",
    )
    checks.append("versioned UE native transport copies synchronous UTF-8 ABI results")
    require(
        "UnrealMemberId" in unreal_managed_bridge
        and "14695981039346656037UL" in unreal_managed_bridge
        and "0xB324FB910D9CED26UL" in unreal_bindings
        and "FUnifiedScriptNativeTransportV2" in unreal_native_transport_header
        and "UEUnifiedScript_GetNativeTransportV2" in unreal_native_transport_source
        and "ReadPropertyById" in unreal_native_transport_header
        and "WritePropertyById" in unreal_native_transport_header
        and "InvokeById" in unreal_native_transport_header
        and "ReadPropertyById" in unreal_invocation_source
        and "WritePropertyById" in unreal_invocation_source
        and "InvokeById" in unreal_invocation_source
        and "GeneratedWrapperPrefersStableMemberIdsWhenAbiV2IsAvailable"
        in unreal_native_adapter_test
        and "AbiV2GeneratedWrapperUsesAppendedMemberIdFunctions"
        in unreal_in_process_transport_test
        and "Generated Add invocation bypasses ProcessEvent"
        in unreal_native_transport_test
        and "Generated managed wrapper selects native member-ID thunks"
        in unreal_world_host_test
        and "class UnrealNativeThunkGenerator" in unreal_native_thunk_generator
        and "--native-out" in cli_source
        and "TypedTarget->ManagedState" in unreal_generated_thunks
        and "TypedTarget->Add(Argument0, Argument1)" in unreal_generated_thunks
        and "ProcessEvent" not in unreal_generated_thunks
        and "FindFProperty" not in unreal_generated_thunks
        and "CheckedInNativeFixtureMatchesGeneratorOutput" in unreal_native_thunk_test,
        "Generated Unreal Member IDs or ABI v2 native fast-thunk path is missing",
    )
    checks.append("generated UE members use automatic ABI v2 direct native thunks with ABI v1 fallback")
    require(
        "FUnifiedScriptNativeTransportV3" in unreal_native_transport_header
        and "FUnifiedScriptNativeCallFrameV3" in unreal_native_transport_header
        and "CallFrameTransport" in unreal_native_transport_header
        and "UEUnifiedScript_GetNativeTransportV3" in unreal_native_transport_source
        and "UnrealNativeTransportTableV3" in unreal_native_abi_v3
        and "TransportTableSize = 64" in unreal_native_abi_v3
        and "IUnrealNativeCallFrameTransportV3" in unreal_native_call_frame_v3
        and "IUnrealNativeCallFrameTransportV3" in unreal_in_process_transport
        and "AbiV2BootstrapDiscoversV3CallFrameAndKeepsLegacyFallback"
        in unreal_in_process_transport_test
        and "CallFrameInvocationCount" in unreal_native_adapter
        and "LegacyInvocationCount" in unreal_native_adapter
        and "Fixed generated calls cross the live ABI v3 call frame"
        in unreal_world_host_test
        and "GetInvocationMetrics" in unreal_world_managed_entry
        and "GetManagedInvocationMetrics" in unreal_coreclr_host_source,
        "Unreal ABI v3 call-frame activation or World metrics are missing",
    )
    checks.append("ABI v3 fixed calls use the live World call frame with observable v2 fallback")
    require(
        "FUnifiedScriptNativeEventFrameV4" in unreal_native_transport_header
        and "FUnifiedScriptNativeEventTransportV4" in unreal_native_transport_header
        and "UEUnifiedScript_GetNativeEventTransportV4" in unreal_native_transport_source
        and "sizeof(FUnifiedScriptNativeEventFrameV4) == 64"
        in unreal_native_transport_header
        and "UnrealNativeEventFrameV4" in unreal_native_event_abi_v4
        and "FrameSize = 64" in unreal_native_event_abi_v4
        and "TransportTableSize = 48" in unreal_native_event_abi_v4
        and "Marshal.SizeOf<UnrealNativeEventFrameV4>" in unreal_native_event_abi_v4_test
        and "Arbitrary reflected event subscription succeeds"
        in unreal_native_transport_test
        and "UnrealGenerationInvocationBridge" in unreal_generation_invocation_bridge
        and "BindAttributedEvents" in unreal_event_binder
        and "BinderResolvesCanonicalGeneratedEndpointAndStrongHandler"
        in unreal_event_binder_test
        and "UnrealEventEndpointAttribute" in unreal_bindings
        and "Attributed Event Entry receives a reflected UE multicast delegate"
        in unreal_world_host_test
        and "Reload rebinds one Event Entry and preserves compatible state"
        in unreal_world_host_test
        and "UNIFIEDSCRIPT_MANAGED_DIAGNOSTIC_PATH" in unreal_world_managed_entry
        and "BoundaryErrors" in unreal_world_managed_entry
        and "public static unsafe int GetLastError" in unreal_world_managed_entry
        and "DescribeManagedFailure" in unreal_coreclr_host_source
        and "GetLastManagedErrorEntryPoint" in unreal_coreclr_host_source
        and "Failed Script Asset reload exposes the managed boundary and type"
        in unreal_world_host_test,
        "Unreal ABI v4 event transport, attributed binding, or diagnostics are missing",
    )
    checks.append("ABI v4 reverse events are generated, World-scoped, reload-safe, and diagnosable")
    require(
        unreal_data_plane_schema["schemaVersion"]
        == "unifiedscript.unreal-data-plane-layout/v0.1"
        and unreal_data_plane_schema["revision"] == 4
        and unreal_data_plane_schema["compatibilityId"] == "0x555344504C415901"
        and unreal_data_plane_schema["limits"]
        == {
            "transformSnapshots": 65536,
            "generatedSnapshotEntries": 65536,
            "generatedEventEntries": 65536,
            "generatedChannels": 256,
            "generatedChannelBytes": 16777216,
            "commands": 65536,
            "events": 4096,
            "traceRequests": 65536,
            "overlapRequests": 65536,
        }
        and {
            item["role"] for item in unreal_data_plane_schema["structs"]
        }
        == {
            "snapshot-row",
            "borrowed-view",
            "telemetry",
            "command-row",
            "completion-event",
            "event-row",
            "query-request",
            "query-result",
        }
        and "class UnrealDataPlaneLayoutGenerator" in unreal_data_plane_generator
        and "JsonUnmappedMemberHandling.Disallow" in unreal_data_plane_generator
        and "ValidateLayouts" in unreal_data_plane_generator
        and 'args[0] == "ue-data-plane"' in cli_source
        and "schema-fingerprint=0x" in cli_source
        and "UnrealDataPlaneLayoutV1" in unreal_data_plane_managed_layout
        and "FUnifiedScriptCommandV4" in unreal_data_plane_native_layout
        and "static_assert(sizeof(FUnifiedScriptCommandV4) == 224)"
        in unreal_data_plane_native_layout
        and "UnifiedScriptDataPlaneLayouts.gen.h" in unreal_shared_data_header
        and "struct alignas(8) FUnifiedScriptCommandV4"
        not in unreal_gameplay_data_header
        and "CheckedInManagedAndNativeLayoutsAreDeterministicSchemaOutputs"
        in unreal_data_plane_generator_test
        and "Generated data-plane artifact is stale"
        in unreal_data_plane_generator_helper,
        "Versioned data-plane schema, deterministic generator, or drift gate is missing",
    )
    checks.append("one versioned schema generates native and managed data-plane layouts")
    require(
        "FUnifiedScriptGameplayDataTransportV4" in unreal_gameplay_data_header
        and "sizeof(FUnifiedScriptCommandV4) == 224" in unreal_data_plane_native_layout
        and "sizeof(FUnifiedScriptTraceResultV4) == 168"
        in unreal_data_plane_native_layout
        and "sizeof(FUnifiedScriptOverlapRequestV1) == 160"
        in unreal_data_plane_native_layout
        and "sizeof(FUnifiedScriptOverlapResultV1) == 48"
        in unreal_data_plane_native_layout
        and "ExecuteCommittedCommands" in unreal_gameplay_data_source
        and "LineTraceSingleByChannel" in unreal_gameplay_data_source
        and "SweepSingleByChannel" in unreal_gameplay_data_source
        and "OverlapMultiByChannel" in unreal_gameplay_data_source
        and "GameplayDataTransport" in unreal_native_transport_header
        and "UEUnifiedScript_GetGameplayDataTransportV4"
        in unreal_native_transport_source
        and "OnWorldPreActorTick" in unreal_world_host_source
        and "OnWorldPostActorTick" in unreal_world_host_source
        and "UnrealCommandBatch" in unreal_gameplay_data_abi_v4
        and "UnrealGameplayEventSnapshot" in unreal_gameplay_data_abi_v4
        and "UnrealTraceBatch" in unreal_gameplay_data_abi_v4
        and "UnrealOverlapBatch" in unreal_gameplay_data_abi_v4
        and "LayoutMatchesNativeGameplayDataContract"
        in unreal_gameplay_data_abi_v4_test
        and "CommandEventAndBatchTrace" in unreal_native_transport_test
        and "Gameplay Data ABI v4" in unreal_demo_source
        and "World phase scheduler" in unreal_gameplay_data_doc,
        "World phase, command/event buffer, or Batch Trace contract is missing",
    )
    checks.append("UE gameplay data plane batches World commands, events, traces, and overlaps")
    require(
        "MaximumTransformSnapshotCount" in unreal_shared_data_source
        and "DroppedSnapshotEntryCount" in unreal_shared_data_source
        and "CommandOverflowCount" in unreal_gameplay_data_source
        and "DroppedEventCount" in unreal_gameplay_data_source
        and "TraceOverflowCount" in unreal_gameplay_data_source
        and "OverlapOverflowCount" in unreal_gameplay_data_source
        and "LastCommittedSequenceId" in unreal_gameplay_data_source
        and "LastExecutedSequenceId" in unreal_gameplay_data_source
        and "GetSharedDataTelemetry" in unreal_native_transport_source
        and "GetGameplayDataTelemetry" in unreal_native_transport_source
        and "OutTelemetrySize" in unreal_native_transport_source
        and "Telemetry prefix copy does not overwrite caller memory"
        in unreal_native_transport_test
        and "LayoutCompatibilityId" in unreal_in_process_transport
        and "GetSharedDataTelemetry()" in unreal_shared_data_abi_v4
        and "GetGameplayDataTelemetry()" in unreal_shared_data_abi_v4
        and "ValidatesSchemaGeneratedCapacityAndOrderingTelemetry"
        in unreal_gameplay_data_abi_v4_test
        and "Command reservations reject schema capacity overflow"
        in unreal_native_transport_test
        and "Published Transform count is bounded" in unreal_native_transport_test,
        "Data-plane capacity, overflow, ordering, or borrow telemetry gate is missing",
    )
    checks.append("UE data-plane bounds and borrow/order telemetry are runtime-enforced")
    require(
        "DataPlaneWorkloadProfileScript" in unreal_data_plane_profile_demo
        and "MeasureMovementCommands" in unreal_data_plane_profile_demo
        and "MeasureBatchTrace" in unreal_data_plane_profile_demo
        and "MeasureAttributeBinding" in unreal_data_plane_profile_demo
        and "MeasureBindingContainers" in unreal_data_plane_profile_demo
        and "MeasureGeneratedSnapshot" in unreal_data_plane_profile_demo
        and "MeasureGeneratedEvents" in unreal_data_plane_profile_demo
        and "DataPlaneAllocatedBytes" in unreal_data_plane_profile_demo
        and "BatchTraceGcPauseMilliseconds" in unreal_data_plane_profile_demo
        and "DataPlaneWorkloadProfile" in unreal_world_host_test
        and "UNIFIEDSCRIPT_DATA_PLANE_PROFILE_JSON" in unreal_world_host_test
        and "unifiedscript.unreal-data-plane-profile-report/v0.3"
        in unreal_data_plane_profile_helper
        and "commandSubmitToScalarBinding" in unreal_data_plane_profile_helper
        and "batchTraceToPerCallBinding" in unreal_data_plane_profile_helper
        and "generatedSnapshotToContainerBinding"
        in unreal_data_plane_profile_helper
        and "generatedChannelsZeroManagedAllocation"
        in unreal_data_plane_profile_helper
        and "SuiteMetricsPath" in unreal_data_plane_profile_helper
        and "batchTraceBytes" in unreal_data_plane_profile_helper
        and "evidenceBackedRouting" in unreal_data_plane_profile_helper,
        "Matched CoreCLR data-plane workload profile or routing gate is missing",
    )
    checks.append("UE data-plane routing is backed by matched live-CoreCLR profiling")
    require(
        any(
            item["name"] == "GeneratedDataViewV1"
            and item["expectedSize"] == 64
            for item in unreal_data_plane_schema["structs"]
        )
        and any(
            item["name"] == "FixedValueCollectionSnapshotV1"
            and item["expectedSize"] == 104
            for item in unreal_data_plane_schema["structs"]
        )
        and any(
            item["name"] == "BufferedGameplayEventV1"
            and item["expectedSize"] == 48
            for item in unreal_data_plane_schema["structs"]
        )
        and "PublishGeneratedSnapshot" in unreal_shared_data_header
        and "AcquireGeneratedSnapshot" in unreal_shared_data_source
        and "QueueGeneratedEvents" in unreal_gameplay_data_header
        and "PublishPendingGeneratedEvents" in unreal_gameplay_data_source
        and "PublishGeneratedSnapshotRows" in unreal_world_host_header
        and "QueueGeneratedEventRows" in unreal_world_host_header
        and "GeneratedSnapshotChannels" in unreal_native_transport_source
        and "GeneratedEventChannels" in unreal_native_transport_source
        and "ChannelsExtendedTransportTableSize = 104"
        in unreal_shared_data_abi_v4
        and "ChannelsExtendedTransportTableSize = 200"
        in unreal_gameplay_data_abi_v4
        and "AcquireGeneratedSnapshot<T>" in unreal_shared_data_abi_v4
        and "AcquireGeneratedEvents<T>" in unreal_shared_data_abi_v4
        and "UnrealGeneratedDataSnapshot<T>" in unreal_shared_data_abi_v4
        and "ReadsGeneratedSnapshotAndBufferedEventsWithoutCopying"
        in unreal_gameplay_data_abi_v4_test
        and "Generated event visibility is one-frame and overflow is observable"
        in unreal_native_transport_test
        and "Generated snapshot publication rejects the schema entry bound"
        in unreal_native_transport_test
        and "Generated snapshot and event channels" in unreal_gameplay_data_doc,
        "M10.2 generated snapshot/event channels or their lifecycle gates are missing",
    )
    checks.append(
        "generated snapshot and event channels are schema-owned, bounded, World-safe, and zero-copy"
    )
    require(
        "FUnifiedScriptAsyncTransportV5" in unreal_async_service_header
        and "GenerationOwnership" in unreal_async_service_header
        and "RequestAsyncLoad" in unreal_async_service_source
        and "CancelOwner" in unreal_async_service_source
        and "AsyncTransport" in unreal_native_transport_header
        and "UEUnifiedScript_GetAsyncTransportV5" in unreal_native_transport_source
        and "UnrealAsyncScope" in unreal_async_service_abi_v5
        and "LoadObjectAsync" in unreal_async_service_abi_v5
        and "ScopeCompletesDelayAndAssetLoadThenCancelsItsGeneration"
        in unreal_async_service_abi_v5_test
        and "AsyncService.DelayLoadCancelAndLifecycle"
        in unreal_native_transport_test
        and "generation-owned" in unreal_async_service_doc,
        "ABI v5 async services, cancellation, or generation lifecycle gate is missing",
    )
    checks.append("UE async services are World-scoped, timeout-aware, and generation-owned")
    require(
        "WorldHost.LifecycleStress" in unreal_lifecycle_stress_test
        and 'TEXT("UnifiedScriptSoakTravelCycles="), 8' in unreal_lifecycle_stress_test
        and 'TEXT("UnifiedScriptSoakPieCycles="), 8' in unreal_lifecycle_stress_test
        and 'TEXT("UnifiedScriptSoakConcurrentWorlds="), 4'
        in unreal_lifecycle_stress_test
        and 'TEXT("UnifiedScriptSoakGenerationRollovers="), 16'
        in unreal_lifecycle_stress_test
        and "GetGlobalOwnedResourceCount" in unreal_async_service_header
        and "ReleaseOwnerResources" in unreal_async_service_source
        and "GetLiveContextCount" in unreal_native_transport_header
        and "CollectRetiredLoadContexts" in unreal_world_managed_entry
        and "CollectRetiredManagedLoadContexts" in unreal_coreclr_host_source
        and "LifecycleStressScript" in unreal_lifecycle_stress_demo_source
        and "unifiedscript.unreal-lifecycle-stress-report/v0.3"
        in unreal_lifecycle_stress_helper
        and "retiredLoadContextsAlive" in unreal_lifecycle_stress_helper
        and "zeroLeak" in unreal_lifecycle_stress_helper,
        "M7.2 lifecycle stress matrix or zero-leak diagnostics are missing",
    )
    checks.append("UE lifecycle stress covers deletion, travel, PIE, multi-World, and reload")
    require(
        unreal_benchmark_thresholds["schemaVersion"]
        == "unifiedscript.unreal-benchmark-thresholds/v0.2"
        and unreal_benchmark_thresholds["scenario"] == "generated-int32-add"
        and unreal_benchmark_thresholds["minimumWarmupIterations"] >= 4096
        and unreal_benchmark_thresholds["minimumIterations"] >= 100000
        and unreal_benchmark_thresholds["minimumSampleCount"] >= 5
        and unreal_benchmark_thresholds["maxUnifiedScriptAllocatedBytes"] == 0
        and unreal_benchmark_thresholds["maxUnifiedScriptToPuertsRatio"] <= 0.95
        and "SampleCount = 5" in unreal_benchmark_demo_source
        and "Benchmark:v3" in unreal_benchmark_demo_source
        and "GC.GetTotalPauseDuration" in unreal_benchmark_demo_source
        and "Array.Sort(samples)" in unreal_benchmark_demo_source
        and "unifiedscript.unreal-benchmark/v0.3" in unreal_world_host_test
        and "memoryHighWaterBytes" in unreal_world_host_test
        and "startupMilliseconds" in unreal_world_host_test
        and "NativeSamples.Sort()" in unreal_world_host_test
        and "machineFingerprint" in unreal_benchmark_helper
        and "$puerts.configuration -ne $nativeResult.configuration"
        in unreal_benchmark_helper
        and "$puerts.engineMinor -ne" in unreal_benchmark_helper
        and "$puerts.sampleCount -ne" in unreal_benchmark_helper
        and "SuiteMetricsPath" in unreal_benchmark_helper
        and "gcPauseMilliseconds" in unreal_benchmark_helper
        and "-RequirePuerts" in unreal_performance_doc
        and "no performance claim" in unreal_performance_doc,
        "Reproducible Unreal/Puerts performance comparison gate is incomplete",
    )
    checks.append("UE performance gate uses median samples and matched Puerts environments")
    require(
        "unifiedscript.unreal-binding-coverage/v0.2" in unreal_binding_coverage
        and "DirectThunk" in unreal_binding_coverage
        and "CallFrame" in unreal_binding_coverage
        and "DataPlaneOperation" in unreal_binding_coverage
        and "ReflectionFallback" in unreal_binding_coverage
        and "USUE4001" in unreal_binding_coverage
        and "IsMissingGeneratedMember" in unreal_native_adapter
        and "MissingGeneratedMembersFallBackToReflectedPaths"
        in unreal_native_adapter_test
        and "FastErrorsOtherThanMissingMemberDoNotFallBack"
        in unreal_native_adapter_test
        and "CoverageSeparatesNativeFallbackAndRejectedMembers"
        in unreal_binding_coverage_test
        and "M71MatrixMatchesAllGoldenArtifactsAndCompiles"
        in unreal_binding_coverage_test
        and all(path.is_file() for path in binding_matrix_golden_files)
        and not handwritten_dispatch_files
        and "--report" in cli_source
        and "--fail-on-rejected" in cli_source,
        "Unreal binding coverage gate or safe reflected-path fallback is missing",
    )
    checks.append("M7.1 binding matrix and no-handwritten-dispatch regression gate are complete")
    blockbreaker_inventory_path = (
        ROOT / "comparison" / "blockbreaker-puerts-inventory.json"
    )
    blockbreaker_coverage_path = (
        ROOT / "comparison" / "blockbreaker-migration-coverage.json"
    )
    blockbreaker_report_path = (
        ROOT / "comparison" / "blockbreaker-migration-report.json"
    )
    blockbreaker_audit_path = (
        ROOT / "comparison" / "audit-blockbreaker-migration.ps1"
    )
    blockbreaker_gameplay_runner_path = (
        ROOT / "comparison" / "run-gameplay-comparison.ps1"
    )
    blockbreaker_smoke_runner_path = (
        ROOT / "comparison" / "run-smoke-comparison.ps1"
    )
    blockbreaker_package_runner_path = (
        ROOT / "comparison" / "package-blockbreaker-demo.ps1"
    )
    puerts_inventory_builder_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealPuertsMigrationInventory.cs"
    )
    roadmap_path = ROOT / "docs" / "ROADMAP.md"
    for path in [
        blockbreaker_inventory_path,
        blockbreaker_coverage_path,
        blockbreaker_report_path,
        blockbreaker_audit_path,
        blockbreaker_gameplay_runner_path,
        blockbreaker_smoke_runner_path,
        blockbreaker_package_runner_path,
        puerts_inventory_builder_path,
        roadmap_path,
    ]:
        require(path.is_file(), f"Missing M7.4 migration artifact: {path}")
    with blockbreaker_inventory_path.open("r", encoding="utf-8") as handle:
        blockbreaker_inventory = json.load(handle)
    with blockbreaker_coverage_path.open("r", encoding="utf-8") as handle:
        blockbreaker_coverage = json.load(handle)
    with blockbreaker_report_path.open("r", encoding="utf-8-sig") as handle:
        blockbreaker_report = json.load(handle)
    blockbreaker_audit = blockbreaker_audit_path.read_text(encoding="utf-8")
    blockbreaker_gameplay_runner = blockbreaker_gameplay_runner_path.read_text(
        encoding="utf-8"
    )
    blockbreaker_smoke_runner = blockbreaker_smoke_runner_path.read_text(
        encoding="utf-8"
    )
    blockbreaker_package_runner = blockbreaker_package_runner_path.read_text(
        encoding="utf-8"
    )
    puerts_inventory_builder = puerts_inventory_builder_path.read_text(
        encoding="utf-8"
    )
    roadmap = roadmap_path.read_text(encoding="utf-8")
    facade_coverage = [
        entry
        for entry in blockbreaker_coverage["entries"]
        if entry["kind"] == "nativeFacade"
    ]
    expected_inventory_summary = {
        "scriptCount": 4,
        "scriptTypeCount": 3,
        "scriptMethodCount": 13,
        "lifecycleHookCount": 6,
        "importCount": 4,
        "puertsApiCount": 2,
        "unrealApiCount": 29,
        "delegateCount": 0,
        "resourceCount": 3,
        "globalPatternCount": 3,
        "asyncPatternCount": 4,
        "decoratorCount": 2,
        "nativeFacadeCount": 1,
        "itemCount": 74,
    }
    require(
        blockbreaker_inventory["schemaVersion"]
        == "unifiedscript.puerts-project-inventory/v0.1"
        and blockbreaker_inventory["summary"] == expected_inventory_summary
        and len(blockbreaker_inventory["items"]) == 74
        and blockbreaker_coverage["schemaVersion"]
        == "unifiedscript.puerts-migration-coverage/v0.1"
        and blockbreaker_coverage["inventoryFingerprint"]
        == blockbreaker_inventory["sourceFingerprint"]
        and len(blockbreaker_coverage["entries"]) == 74
        and blockbreaker_report["schemaVersion"]
        == "unifiedscript.puerts-migration-report/v0.1"
        and blockbreaker_report["inventoryFingerprint"]
        == blockbreaker_inventory["sourceFingerprint"]
        and blockbreaker_report["fingerprintMatches"]
        and blockbreaker_report["passed"]
        and blockbreaker_report["inventoryItemCount"] == 74
        and blockbreaker_report["coveredItemCount"] == 74
        and blockbreaker_report["uncoveredItemCount"] == 0
        and blockbreaker_report["staleEntryCount"] == 0
        and len(facade_coverage) == 1
        and facade_coverage[0]["route"] == "verification-only"
        and facade_coverage[0]["notes"].strip()
        and "UnrealPuertsMigrationInventoryBuilder" in puerts_inventory_builder
        and "JsonUnmappedMemberHandling.Disallow" in puerts_inventory_builder
        and "requires an explicit boundary explanation" in puerts_inventory_builder
        and 'args[0] == "ue-puerts-inventory"' in cli_source
        and "--fail-on-uncovered" in cli_source
        and "Plugins\\Puerts" in blockbreaker_audit
        and "shared assets changed during migration" in blockbreaker_audit
        and "generated binding coverage is incomplete" in blockbreaker_audit
        and "scriptAssets.Count -ne 2" in blockbreaker_audit
        and "audit-blockbreaker-migration.ps1" in blockbreaker_gameplay_runner
        and "audit-blockbreaker-migration.ps1" in blockbreaker_smoke_runner
        and "audit-blockbreaker-migration.ps1" in blockbreaker_package_runner
        and "puertsOrTypeScriptPackageFiles" in blockbreaker_package_runner
        and "### M7.4 — Full BlockBreaker runtime migration" in roadmap
        and "- [x] inventory all Puerts scripts" in roadmap
        and "### M8.3 — Asset, state, event, and API authoring" in roadmap,
        "M7.4 whole-project Puerts inventory, coverage, or migration gates are incomplete",
    )
    checks.append("M7.4 BlockBreaker migration has exact coverage and zero legacy payload gates")
    puerts_project_scanner_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealPuertsProjectScanner.cs"
    )
    puerts_project_scanner_test_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealPuertsProjectScannerTests.cs"
    )
    puerts_migration_document_path = ROOT / "docs" / "PUERTS_MIGRATION.md"
    for path in [
        puerts_project_scanner_path,
        puerts_project_scanner_test_path,
        puerts_migration_document_path,
    ]:
        require(path.is_file(), f"Missing M12.1 Puerts project scanner artifact: {path}")
    puerts_project_scanner = puerts_project_scanner_path.read_text(encoding="utf-8")
    puerts_project_scanner_test = puerts_project_scanner_test_path.read_text(
        encoding="utf-8"
    )
    puerts_migration_document = puerts_migration_document_path.read_text(
        encoding="utf-8"
    )
    require(
        "unifiedscript.puerts-project-scan/v0.1" in puerts_project_scanner
        and "DefaultSourceScriptRoots" in puerts_project_scanner
        and "DefaultDeployedScriptRoots" in puerts_project_scanner
        and "MaximumFileCount = 50_000" in puerts_project_scanner
        and '"module"' in puerts_project_scanner
        and '"unrealApi"' in puerts_project_scanner
        and '"puertsApi"' in puerts_project_scanner
        and '"lifecycleHook"' in puerts_project_scanner
        and '"assetReference"' in puerts_project_scanner
        and '"delegatePattern"' in puerts_project_scanner
        and '"asyncPattern"' in puerts_project_scanner
        and '"unsupportedDynamicBehavior"' in puerts_project_scanner
        and '"scanLimit"' in puerts_project_scanner
        and 'args[0] == "ue-puerts-scan"' in cli_source
        and "--script-root" in cli_source
        and "--native-root" in cli_source
        and "--fail-on-blockers" in cli_source
        and "ScansCustomRootsAndClassifiesMigrationRisksDeterministically"
        in puerts_project_scanner_test
        and "AutoDiscoveryPrefersAuthoredSourcesOverDeployedJavaScript"
        in puerts_project_scanner_test
        and "ReportsIncompleteBoundedScanInsteadOfSilentlyDroppingSources"
        in puerts_project_scanner_test
        and "CliWritesScanAndCanFailAutomationOnBlockers"
        in puerts_project_scanner_test
        and "blind source translation" in puerts_migration_document
        and "exactly four authored TypeScript files" in puerts_migration_document
        and "- [x] scan a Puerts project into an inventory" in roadmap
        and "- [x] support incremental coexistence during migration" in roadmap,
        "M12.1 generalized Puerts source ownership, risk scan, CLI, tests, or documentation is incomplete",
    )
    checks.append(
        "M12.1 scans generalized Puerts projects with bounded ownership and dynamic-risk evidence"
    )
    puerts_migration_planner_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealPuertsMigrationPlanner.cs"
    )
    puerts_verification_harness_path = (
        ROOT
        / "src"
        / "UnifiedScript.Unreal"
        / "UnrealPuertsVerificationHarness.cs"
    )
    puerts_migration_planner_test_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealPuertsMigrationPlannerTests.cs"
    )
    for path in [
        puerts_migration_planner_path,
        puerts_verification_harness_path,
        puerts_migration_planner_test_path,
    ]:
        require(path.is_file(), f"Missing M12.1 Puerts migration package artifact: {path}")
    puerts_migration_planner = puerts_migration_planner_path.read_text(
        encoding="utf-8"
    )
    puerts_verification_harness = puerts_verification_harness_path.read_text(
        encoding="utf-8"
    )
    puerts_migration_planner_test = puerts_migration_planner_test_path.read_text(
        encoding="utf-8"
    )
    require(
        "unifiedscript.puerts-migration-plan/v0.1" in puerts_migration_planner
        and "unifiedscript.puerts-api-coverage/v0.1" in puerts_migration_planner
        and "unifiedscript.puerts-contract-requests/v0.1" in puerts_migration_planner
        and "unifiedscript.puerts-verification-harness/v0.1"
        in puerts_migration_planner
        and "unifiedscript.puerts-migration-package/v0.1"
        in puerts_migration_planner
        and 'CreateFile("migration-plan.json"' in puerts_migration_planner
        and 'CreateFile("api-coverage.json"' in puerts_migration_planner
        and 'CreateFile("contract-requests.json"' in puerts_migration_planner
        and 'CreateFile("MigrationStubs.cs"' in puerts_migration_planner
        and '"covered-adapter"' in puerts_migration_planner
        and '"covered-route"' in puerts_migration_planner
        and '"data-plane"' in puerts_migration_planner
        and '"world-service"' in puerts_migration_planner
        and '"resource-loader"' in puerts_migration_planner
        and "exactly-one-runtime-owns-each-world-lifecycle"
        in puerts_migration_planner
        and "PlanBlocked" in puerts_migration_planner
        and "unifiedscript.puerts-verification-report/v0.1"
        in puerts_verification_harness
        and '"blocked-plan"' in puerts_verification_harness
        and '"duplicate-case"' in puerts_verification_harness
        and '"observable-mismatch"' in puerts_verification_harness
        and 'args[0] == "ue-puerts-plan"' in cli_source
        and 'args[0] == "ue-puerts-verify"' in cli_source
        and "--fail-on-mismatch" in cli_source
        and "GeneratesDeterministicPlanCoverageRequestsStubsAndHarness"
        in puerts_migration_planner_test
        and "VerificationRequiresMatchedPassedEvidenceAndExactObservables"
        in puerts_migration_planner_test
        and "CliWritesPackageAndRunsSideBySideVerification"
        in puerts_migration_planner_test
        and "BlockedScanStillWritesDiagnosablePackageBeforeFailingGate"
        in puerts_migration_planner_test
        and "ten deterministic files" in puerts_migration_document
        and "Both evidence templates intentionally fail this gate"
        in puerts_migration_document
        and "- [x] generate a migration plan, API coverage report"
        in roadmap
        and "22 of 38" in roadmap,
        "M12.1 migration plan, coverage, Contract requests, stubs, verification harness, CLI, tests, or documentation is incomplete",
    )
    checks.append(
        "M12.1 emits deterministic Puerts migration packages and strict side-by-side evidence gates"
    )
    puerts_coexistence_path = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealPuertsCoexistence.cs"
    )
    puerts_coexistence_test_path = (
        ROOT / "tests" / "UnifiedScript.Tests" / "UnrealPuertsCoexistenceTests.cs"
    )
    runtime_world_header_path = (
        ROOT / "HostProject" / "Plugins" / "UEUnifiedScript" / "Source"
        / "UEUnifiedScriptRuntime" / "Public" / "UnifiedScriptWorldSubsystem.h"
    )
    runtime_world_library_header_path = (
        ROOT / "HostProject" / "Plugins" / "UEUnifiedScript" / "Source"
        / "UEUnifiedScriptRuntime" / "Public" / "UnifiedScriptWorldLibrary.h"
    )
    runtime_component_header_path = (
        ROOT / "HostProject" / "Plugins" / "UEUnifiedScript" / "Source"
        / "UEUnifiedScriptRuntime" / "Public" / "UnifiedScriptComponent.h"
    )
    runtime_ownership_test_path = (
        ROOT / "HostProject" / "Plugins" / "UEUnifiedScript" / "Source"
        / "UEUnifiedScriptRuntime" / "Private" / "Tests"
        / "UnifiedScriptRuntimeOwnershipTests.cpp"
    )
    for path in [
        puerts_coexistence_path,
        puerts_coexistence_test_path,
        runtime_world_header_path,
        runtime_world_library_header_path,
        runtime_component_header_path,
        runtime_ownership_test_path,
    ]:
        require(path.is_file(), f"Missing M12.1 coexistence artifact: {path}")
    puerts_coexistence = puerts_coexistence_path.read_text(encoding="utf-8")
    puerts_coexistence_test = puerts_coexistence_test_path.read_text(encoding="utf-8")
    runtime_world_header = runtime_world_header_path.read_text(encoding="utf-8")
    runtime_world_library_header = runtime_world_library_header_path.read_text(
        encoding="utf-8"
    )
    runtime_component_header = runtime_component_header_path.read_text(encoding="utf-8")
    runtime_ownership_test = runtime_ownership_test_path.read_text(encoding="utf-8")
    require(
        "unifiedscript.puerts-coexistence/v0.1" in puerts_coexistence
        and "exactly-one-runtime-owns-each-world-lifecycle" in puerts_coexistence
        and 'CreateFile("coexistence-manifest.json"' in puerts_coexistence
        and 'CreateFile("PuertsOwnership.generated.ts"' in puerts_coexistence
        and "claimPuertsLifecycle" in puerts_coexistence
        and "releasePuertsLifecycle" in puerts_coexistence
        and 'args[0] == "ue-puerts-coexist"' in cli_source
        and "--unified" in cli_source
        and "CliWritesPromotedRoutesAndBlockedPlanStillFailsAfterOutput"
        in puerts_coexistence_test
        and "EUnifiedScriptRuntimeOwner" in runtime_world_header
        and "ClaimGameplayLifecycle" in runtime_world_header
        and "ReleaseGameplayLifecycle" in runtime_world_header
        and "BuildGameplayLifecycleOwnershipReport" in runtime_world_header
        and "ClaimGameplayLifecycle" in runtime_world_library_header
        and "ReleaseGameplayLifecycle" in runtime_world_library_header
        and "LifecycleOwnershipKey" in runtime_component_header
        and "Reload cannot change lifecycle ownership" in runtime_component_source
        and "UnifiedScript.Unreal.Coexistence.RuntimeOwnership" in runtime_ownership_test
        and "For each Puerts-owned concrete script instance" in puerts_migration_document
        and "manifest and generated guard do not rewrite" in puerts_migration_document
        and "- [x] support incremental coexistence during migration" in roadmap
        and "- Next slice: **M12.2" in roadmap,
        "M12.1 coexistence manifest, shared World arbiter, tests, or lifecycle documentation is incomplete",
    )
    checks.append(
        "M12.1 arbitrates incremental Puerts and UnifiedScript lifecycle ownership per World"
    )
    roundtrip_manifest_path = (
        ROOT / "comparison" / "blockbreaker-roundtrip.json"
    )
    roundtrip_patch_path = (
        ROOT / "comparison" / "blockbreaker-roundtrip.patch.json"
    )
    roundtrip_requirement_paths = [
        ROOT / "comparison" / "blockbreaker-player-roundtrip.requirements.json",
        ROOT / "comparison" / "blockbreaker-rifle-roundtrip.requirements.json",
    ]
    roundtrip_runner_path = (
        ROOT / "comparison" / "verify-blockbreaker-roundtrip.ps1"
    )
    roundtrip_runtime_path = (
        ROOT / "src" / "UnifiedScript.Runtime" / "RoundTripEquivalence.cs"
    )
    roundtrip_test_path = (
        ROOT / "tests" / "UnifiedScript.Tests" / "RoundTripEquivalenceTests.cs"
    )
    authoring_http_test_path = (
        ROOT / "tests" / "UnifiedScript.Tests" / "CoreClrAuthoringHttpServerTests.cs"
    )
    roundtrip_doc_path = ROOT / "docs" / "BLOCKBREAKER_ROUND_TRIP.md"
    comparison_readme_path = ROOT / "comparison" / "README.md"
    for path in [
        roundtrip_manifest_path,
        roundtrip_patch_path,
        *roundtrip_requirement_paths,
        roundtrip_runner_path,
        roundtrip_runtime_path,
        roundtrip_test_path,
        authoring_http_test_path,
        roundtrip_doc_path,
        comparison_readme_path,
    ]:
        require(path.is_file(), f"Missing M8.4 round-trip artifact: {path}")
    with roundtrip_manifest_path.open("r", encoding="utf-8") as handle:
        roundtrip_manifest = json.load(handle)
    with roundtrip_patch_path.open("r", encoding="utf-8") as handle:
        roundtrip_patch = json.load(handle)
    roundtrip_requirements = []
    for path in roundtrip_requirement_paths:
        with path.open("r", encoding="utf-8") as handle:
            roundtrip_requirements.append(json.load(handle))
    roundtrip_runner = roundtrip_runner_path.read_text(encoding="utf-8")
    roundtrip_runtime = roundtrip_runtime_path.read_text(encoding="utf-8")
    roundtrip_test = roundtrip_test_path.read_text(encoding="utf-8")
    authoring_http_test = authoring_http_test_path.read_text(encoding="utf-8")
    roundtrip_doc = roundtrip_doc_path.read_text(encoding="utf-8")
    comparison_readme = comparison_readme_path.read_text(encoding="utf-8")
    roundtrip_methods = {
        script["method"] for script in roundtrip_manifest["scripts"]
    }
    requirement_methods = {
        method["qualifiedName"]
        for document in roundtrip_requirements
        for method in document["methods"]
    }
    require(
        roundtrip_manifest["schemaVersion"]
        == "unifiedscript.blockbreaker-round-trip/v0.1"
        and len(roundtrip_manifest["baselineCommit"]) == 40
        and roundtrip_manifest["projectPath"].endswith(".csproj")
        and {script["id"] for script in roundtrip_manifest["scripts"]}
        == {"player", "rifle"}
        and roundtrip_methods == requirement_methods
        and all(
            document["schemaVersion"]
            == "unifiedscript.round-trip-requirements/v0.1"
            and len(document["methods"]) == 1
            and len(document["methods"][0]["cases"]) >= 3
            for document in roundtrip_requirements
        )
        and roundtrip_patch["schemaVersion"] == "unifiedscript.patch/v0.28"
        and len(roundtrip_patch["operations"]) == 2
        and all(
            operation["operation"] == "replaceLiteral"
            for operation in roundtrip_patch["operations"]
        )
        and 'args[0] == "verify-roundtrip"' in cli_source
        and '"apply-project-patch" or "apply-project-plan"' in cli_source
        and "RoundTripEquivalenceVerifier" in roundtrip_runtime
        and "CreateSemanticDigest" in roundtrip_runtime
        and "CreateIlDigest" in roundtrip_runtime
        and "EquivalentVisualAndCodeProgramsMatchIdentitySemanticsIlAssetsAndResults"
        in roundtrip_test
        and "apply-project-patch" in roundtrip_runner
        and "verify-roundtrip" in roundtrip_runner
        and "gameplayEquivalent" in roundtrip_runner
        and "scriptAssetsEquivalent" in roundtrip_runner
        and "packagedBuildEquivalent" in roundtrip_runner
        and "stage-maximum-stale" in authoring_http_test
        and "group-external-conflict" in authoring_http_test
        and '"UAW1005"' in authoring_http_test
        and 'Assert.Equal("UAW1002", error.Error.Code)' in authoring_http_test
        and "BLOCKBREAKER_ROUND_TRIP.md" in comparison_readme
        and "verify-blockbreaker-roundtrip.ps1" in roundtrip_doc
        and "- [x] author representative BlockBreaker behavior visually" in roadmap
        and "- [x] run semantic, IL, gameplay-result, asset, and packaged-build equivalence"
        in roadmap
        and "- [x] add conflict and merge tests for external edits" in roadmap,
        "M8.4 whole-game Visual/C# round-trip proof or conflict gates are incomplete",
    )
    checks.append("M8.4 BlockBreaker Visual/C# round trip passes full equivalence and conflict gates")
    semantic_model_path = (
        ROOT / "src" / "UnifiedScript.Semantics" / "SemanticModel.cs"
    )
    semantic_binder_path = (
        ROOT / "src" / "UnifiedScript.CSharp" / "RoslynSemanticBinder.cs"
    )
    graph_projector_path = (
        ROOT / "src" / "UnifiedScript.Graph" / "HirGraphProjector.cs"
    )
    graph_json_path = ROOT / "src" / "UnifiedScript.Graph" / "GraphJson.cs"
    semantic_pipeline_test_path = (
        ROOT / "tests" / "UnifiedScript.Tests" / "CSharpSemanticPipelineTests.cs"
    )
    graph_json_test_path = (
        ROOT / "tests" / "UnifiedScript.Tests" / "GraphJsonTests.cs"
    )
    nullability_doc_path = ROOT / "docs" / "NULLABILITY_FLOW.md"
    for path in [
        semantic_model_path,
        semantic_binder_path,
        graph_projector_path,
        graph_json_path,
        semantic_pipeline_test_path,
        graph_json_test_path,
        nullability_doc_path,
    ]:
        require(path.is_file(), f"Missing M8.5 null-flow artifact: {path}")
    semantic_model_source = semantic_model_path.read_text(encoding="utf-8")
    semantic_binder_source = semantic_binder_path.read_text(encoding="utf-8")
    graph_projector_source = graph_projector_path.read_text(encoding="utf-8")
    graph_json_source = graph_json_path.read_text(encoding="utf-8")
    semantic_pipeline_test = semantic_pipeline_test_path.read_text(encoding="utf-8")
    graph_json_test = graph_json_test_path.read_text(encoding="utf-8")
    nullability_doc = nullability_doc_path.read_text(encoding="utf-8")
    require(
        'CurrentSchemaVersion = "unifiedscript.semantics/v0.6"'
        in semantic_model_source
        and "UnifiedNullFlowState" in semantic_model_source
        and "NullFlowState" in semantic_model_source
        and "typeInfo.ConvertedNullability.FlowState" in semantic_binder_source
        and "GetInitialNullFlowState" in semantic_binder_source
        and 'properties["typeNullability"]' in graph_projector_source
        and 'properties["nullFlowState"]' in graph_projector_source
        and "ValidateNullabilityProperties" in graph_json_source
        and "AnalyzeAndProject_PublishesFlowSensitiveNullStateAtEveryExpressionUse"
        in semantic_pipeline_test
        and "Serialize_RejectsInvalidNullFlowMetadata" in graph_json_test
        and "unifiedscript.semantics/v0.6" in nullability_doc
        and "unifiedscript.bound-method/v0.4" in roundtrip_runtime
        and "- [x] flow-sensitive nullability state" in roadmap,
        "M8.5 flow-sensitive nullability semantic, Graph, or documentation contract is incomplete",
    )
    checks.append("M8.5 nullability follows Roslyn flow state through Semantics and Graph")
    hir_nodes_path = ROOT / "src" / "UnifiedScript.Core" / "HirNodes.cs"
    profile_validator_path = (
        ROOT / "src" / "UnifiedScript.CSharp" / "VisualProfileValidator.cs"
    )
    source_patch_path = (
        ROOT / "src" / "UnifiedScript.CSharp" / "CSharpSourcePatchApplier.cs"
    )
    source_patch_test_path = (
        ROOT / "tests" / "UnifiedScript.Tests" / "CSharpSourcePatchApplierTests.cs"
    )
    profile_test_path = (
        ROOT / "tests" / "UnifiedScript.Tests" / "VisualProfileValidatorTests.cs"
    )
    foreach_doc_path = ROOT / "docs" / "FOREACH_LOOPS.md"
    for path in [
        hir_nodes_path,
        profile_validator_path,
        source_patch_path,
        source_patch_test_path,
        profile_test_path,
        foreach_doc_path,
    ]:
        require(path.is_file(), f"Missing M8.5 foreach artifact: {path}")
    hir_nodes_source = hir_nodes_path.read_text(encoding="utf-8")
    profile_validator_source = profile_validator_path.read_text(encoding="utf-8")
    source_patch_source = source_patch_path.read_text(encoding="utf-8")
    source_patch_test = source_patch_test_path.read_text(encoding="utf-8")
    profile_test = profile_test_path.read_text(encoding="utf-8")
    foreach_doc = foreach_doc_path.read_text(encoding="utf-8")
    require(
        'CurrentSchemaVersion = "unifiedscript.hir/v0.9"'
        in (ROOT / "src" / "UnifiedScript.Core" / "HirDocument.cs").read_text(encoding="utf-8")
        and "HirForEachIterationKind" in hir_nodes_source
        and "RefReadOnly" in hir_nodes_source
        and "BorrowedSequence" in semantic_model_source
        and "ForEachDisposalKind" in semantic_model_source
        and "TryGetBorrowedSequenceElementType" in semantic_binder_source
        and "GetForEachDisposalKind" in semantic_binder_source
        and '"conditionalFinally"' in graph_projector_source
        and '"borrowedReadOnly"' in graph_projector_source
        and "isConditionalDisposalForEach" in graph_json_source
        and "UnifiedTypeKind.BorrowedSequence" in source_patch_source
        and "AnalyzeAndProject_PreservesBorrowedRefReadonlySpanWithoutCleanup"
        in semantic_pipeline_test
        and "AnalyzeAndProject_PreservesPatternEnumeratorConditionalDisposal"
        in semantic_pipeline_test
        and "Apply_ReplacesBorrowedRefReadonlyForEachCollectionWithoutLosingAliasMode"
        in source_patch_test
        and "Validate_AllowsRefReadonlyForEachButRejectsMutableRefAndAwaitVariants"
        in profile_test
        and "disposal: conditionalFinally" in foreach_doc
        and "ref readonly" in foreach_doc,
        "M8.5 borrowed/ref-readonly or conditional-disposal foreach contract is incomplete",
    )
    checks.append("M8.5 foreach preserves borrowed refs and exact conditional disposal")
    graph_cfg_source = (
        ROOT / "src" / "UnifiedScript.Graph" / "GraphControlFlowAnalyzer.cs"
    ).read_text(encoding="utf-8")
    require(
        "AsyncEnumerable" in semantic_model_source
        and "AsyncEnumerator" in semantic_model_source
        and "TryGetAsyncEnumerableElementType" in semantic_binder_source
        and '"asyncEnumerator"' in graph_projector_source
        and '"moveNextAsync"' in graph_projector_source
        and '"asyncEnumeratorDispose"' in graph_projector_source
        and '"disposeAsyncMethodId"' in graph_projector_source
        and "isAsyncEnumeratorForEach" in graph_json_source
        and '"asyncEnumeratorDispose"' in graph_cfg_source
        and "UnifiedTypeKind.AsyncEnumerable" in source_patch_source
        and "Apply_ReplacesAsyncForEachCollectionAndPreservesAwaitProtocol"
        in source_patch_test
        and "AnalyzeAndProject_PreservesAsyncEnumeratorAndAwaitedCleanup"
        in semantic_pipeline_test
        and "await foreach" in foreach_doc
        and "DisposeAsync" in foreach_doc,
        "M8.5 async foreach semantic, Graph, CFG, edit, test, or documentation contract is incomplete",
    )
    checks.append("M8.5 await foreach preserves async move-next and disposal cleanup")
    require(
        "UUnifiedScriptWorldSubsystem final" in unreal_world_host_header
        and "public UWorldSubsystem" in unreal_world_host_header
        and "FUnifiedScriptNativeBridgeContext" in unreal_world_host_header
        and "NativeContext->Detach()" in unreal_world_host_source
        and "ObjectRegistry->InvalidateAll" in unreal_world_host_source
        and "InvocationBridge.Reset()" in unreal_world_host_source
        and "ObjectRegistry.Reset()" in unreal_world_host_source
        and "WorldHost.Lifecycle" in unreal_world_host_test
        and "Active handles are unique across world registries"
        in unreal_world_host_test
        and "Retained context rejects calls after world teardown"
        in unreal_world_host_test
        and "DisposeReleasesContextAndRejectsFurtherCalls"
        in unreal_in_process_transport_test,
        "World-scoped Unreal host or retained bridge-context lifecycle is missing",
    )
    checks.append("UE World hosts detach retained bridge contexts before native teardown")
    require(
        "FUnifiedScriptCoreClrHost" in unreal_coreclr_host_header
        and "GetLoadAssemblyAndGetFunctionPointer" in unreal_coreclr_host_header
        and "UNIFIEDSCRIPT_DOTNET_ROOT" in unreal_coreclr_host_source
        and "Binaries/ThirdParty/DotNet/10.0" in unreal_coreclr_host_source
        and "hostfxr_initialize_for_runtime_config" in unreal_coreclr_host_source
        and "hostfxr_get_runtime_delegate" in unreal_coreclr_host_source
        and "LoadAssemblyAndGetFunctionPointerDelegateType = 5"
        in unreal_coreclr_host_source
        and "CoreClrHost.Initialize()" in unreal_world_host_source
        and "Process CoreCLR host is ready" in unreal_world_host_test
        and "CoreCLR load-assembly delegate exists" in unreal_world_host_test
        and unreal_coreclr_runtime_config["runtimeOptions"]["tfm"] == "net10.0"
        and unreal_coreclr_runtime_config["runtimeOptions"]["framework"]["name"]
        == "Microsoft.NETCore.App",
        "Process-wide hostfxr/CoreCLR bootstrap contract is missing",
    )
    checks.append("UE loads CoreCLR through hostfxr before managed World entry points")
    require(
        'RuntimeDependencies.Add("$(PluginDir)/Binaries/Managed/..."'
        in unreal_runtime_build
        and 'RuntimeDependencies.Add("$(PluginDir)/Resources/Managed/..."'
        in unreal_runtime_build
        and 'StageBundledDotNetDirectory("host/fxr")' in unreal_runtime_build
        and 'StageBundledDotNetDirectory("shared/Microsoft.NETCore.App")'
        in unreal_runtime_build
        and "Binaries/ThirdParty/DotNet/10.0" in unreal_coreclr_host_source
        and "UnifiedScriptSmoke" in host_module
        and "UnifiedScriptSmokeResult=" in host_module
        and "Managed Start/Tick/Stop succeeded" in host_module
        and "BuildCookRun" in unreal_release_helper
        and "Development', 'Shipping" in unreal_release_helper
        and "-SkipZenStore" in unreal_release_helper
        and "hostfxr.dll" in unreal_release_helper
        and "hostpolicy.dll" in unreal_release_helper
        and "coreclr.dll" in unreal_release_helper
        and "System.Private.CoreLib.dll" in unreal_release_helper
        and "poisonedExternalDotNetRoots" in unreal_release_helper
        and "ExpectedEngineVersion = '5.8.1'" in unreal_release_helper
        and "unifiedscript.unreal-release-gate/v0.2" in unreal_release_helper
        and "self-contained" in unreal_release_doc,
        "Win64 self-contained staging, packaged smoke, or release gate is missing",
    )
    checks.append("Win64 packages stage CoreCLR and run managed lifecycle smoke in Development/Shipping")
    unreal_managed_project = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnifiedScript.Unreal.csproj"
    ).read_text(encoding="utf-8")
    require(
        "unifiedscript.unreal-compatibility-matrix/v0.1"
        in unreal_compatibility_helper
        and "editor-development-build" in unreal_compatibility_helper
        and "editor-automation" in unreal_compatibility_helper
        and "source-development-shipping-package" in unreal_compatibility_helper
        and "clean-project-sdk-install" in unreal_compatibility_helper
        and "clean-project-managed-chunk" in unreal_compatibility_helper
        and "clean-project-development-package" in unreal_compatibility_helper
        and "@('Development', 'Shipping')" in unreal_compatibility_helper
        and "package-unifiedscript-sdk.ps1" in unreal_compatibility_helper
        and "archiveSha256 -ne" in unreal_compatibility_helper
        and "git -C $repoRoot archive" in unreal_compatibility_helper
        and "DOTNET_ROOT" in unreal_release_helper
        and "UNIFIEDSCRIPT_DOTNET_ROOT" in unreal_release_helper
        and "not-declared" in unreal_compatibility_helper
        and "four declared UE 5.8.1 Win64 rows" in unreal_compatibility_doc
        and "171 payload files" in unreal_compatibility_doc
        and re.search(
            r"-c Release `\n\s+--no-build `\n\s+-- `",
            unreal_chunk_stage,
        )
        and 'SkipUnchangedFiles="true"' not in unreal_managed_project
        and 'SkipUnchangedFiles="true"' not in unreal_demo_project,
        "M11.2 exact-engine, package, SDK-consumer, or staging-determinism matrix is incomplete",
    )
    checks.append(
        "M11.2 declares only four proven UE 5.8.1 Win64 build/package/runtime rows"
    )
    require(
        "UnrealWorldManagedEntryPoint" in unreal_world_managed_entry
        and "UnmanagedCallersOnly" in unreal_world_managed_entry
        and "ConcurrentDictionary<ulong, WorldEntry>" in unreal_world_managed_entry
        and "InProcessUnrealNativeInvocationTransport" in unreal_world_managed_entry
        and "AssemblyLoadContext" in unreal_world_managed_entry
        and "isCollectible: true" in unreal_world_managed_entry
        and "Worlds.TryAdd" in unreal_world_managed_entry
        and "Worlds.TryRemove" in unreal_world_managed_entry,
        "Managed World entry point or collectible load context is missing",
    )
    unreal_project = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnifiedScript.Unreal.csproj"
    ).read_text(encoding="utf-8")
    require(
        "StageUnrealManagedHost" in unreal_project
        and "Binaries/Managed" in unreal_project
        and "UnifiedScript.Unreal.dll" in unreal_coreclr_host_source
        and "CreateWorldEntryPoint" in unreal_coreclr_host_source
        and "DestroyWorldEntryPoint" in unreal_coreclr_host_source
        and "GetWorldCountEntryPoint" in unreal_coreclr_host_source
        and "CreateManagedWorld" in unreal_world_host_source
        and "DestroyManagedWorld" in unreal_world_host_source
        and "Two collectible managed World entries were created"
        in unreal_world_host_test
        and "Second World teardown restores the managed entry count"
        in unreal_world_host_test,
        "Managed World staging or native create/destroy lifecycle is missing",
    )
    checks.append("UE creates and destroys one collectible managed entry per World")
    require(
        "IUnrealManagedScript" in unreal_world_managed_entry
        and "OnStart" in unreal_world_managed_entry
        and "OnTick" in unreal_world_managed_entry
        and "OnStop" in unreal_world_managed_entry
        and "ScriptAssemblyLoadContext" in unreal_world_managed_entry
        and "ManagedScriptInstance" in unreal_world_managed_entry
        and "ReloadScript" in unreal_world_managed_entry
        and "UnifiedScriptComponent(context.Host, context.Bridge)"
        in unreal_demo_source
        and '_component.Add(20, 22)' in unreal_demo_source
        and "StageUnrealManagedDemo" in unreal_demo_project
        and "CreateScriptEntryPoint" in unreal_coreclr_host_source
        and "ReloadScriptEntryPoint" in unreal_coreclr_host_source
        and "ScriptAssemblyPath" in unreal_component
        and "ScriptTypeName" in unreal_component
        and "StartScript" in unreal_component
        and "BeginPlay" in unreal_component
        and "TickComponent" in unreal_component
        and "BeginPlay starts the configured managed script"
        in unreal_world_host_test
        and "Queued reload advances the collectible generation"
        in unreal_world_host_test
        and "Managed OnStop executes before handle release"
        in unreal_world_host_test
        and "UnifiedScript demo completed:" in unreal_world_host_test
        and "WorldHost.Lifecycle" in unreal_demo_helper,
        "Component-driven managed lifecycle or collectible reload is missing",
    )
    checks.append("UE component drives managed Start, Tick, reload, and Stop on a live UObject")
    require(
        "AUnifiedScriptActor : public AActor" in unreal_actor
        and "UUnifiedScriptComponent" in unreal_actor
        and "class UnrealObjectScript : IUnrealManagedScript"
        in unreal_script_object_model
        and "class UnrealActorScript : UnrealObjectScript"
        in unreal_script_object_model
        and "CancellationToken Lifetime" in unreal_script_object_model
        and "UnrealObjectHandle Host" in unreal_world_managed_entry
        and "UnrealObjectHandle Target" in unreal_world_managed_entry
        and "ActorDemoScript : UnrealActorScript" in unreal_actor_demo_source
        and "Context.HasDistinctTarget" in unreal_actor_demo_source
        and "IUnrealManagedEventSink" in unreal_script_object_model
        and "DelayAsync" in unreal_script_object_model
        and "UnrealAsyncScope" in unreal_script_object_model
        and "_asyncScope!.Dispose()" in unreal_script_object_model
        and "DispatchEventEntryPoint" in unreal_coreclr_host_source
        and "HandleActorBeginOverlap" in unreal_component
        and "World-safe BeginOverlap reaches managed Actor behavior"
        in unreal_world_host_test
        and "Managed latent delay resumes on the game-thread Tick"
        in unreal_world_host_test
        and "Managed Actor EndPlay runs before target handle release"
        in unreal_world_host_test,
        "Managed UObject/Actor object model or live Actor lifecycle gate is missing",
    )
    checks.append("UE Actor host maps Blueprint identity to managed object lifecycle")
    require(
        "UUnifiedScriptAsset : public UDataAsset" in unreal_script_asset_header
        and "SchemaVersion = 1" in unreal_script_asset_header
        and "ResolveConfiguration" in unreal_script_asset_source
        and "unsupported schema version" in unreal_script_asset_source
        and "ScriptAsset.Configuration" in unreal_script_asset_test
        and "TObjectPtr<UUnifiedScriptAsset> ScriptAsset" in unreal_component
        and "ResolveScriptConfiguration" in unreal_component
        and "ActiveScriptAssetPath" in unreal_component
        and "ManagedState" in unreal_component
        and "FSoftObjectPath" in (
            ROOT
            / "HostProject"
            / "Plugins"
            / "UEUnifiedScript"
            / "Source"
            / "UEUnifiedScriptRuntime"
            / "Private"
            / "UnifiedScriptComponent.cpp"
        ).read_text(encoding="utf-8")
        and "ReadUtf8(assemblyPathData, assemblyPathLength)"
        in unreal_world_managed_entry
        and "nextAssemblyPath" in unreal_world_managed_entry
        and "Invalid Script Asset reload is rejected transactionally"
        in unreal_world_host_test
        and "Queued reload transaction changes the managed entry type"
        in unreal_world_host_test
        and "AlternateDemoScript" in unreal_alternate_demo_source,
        "Script Asset resolution or configuration-aware reload contract is missing",
    )
    checks.append("UE Script Assets resolve managed entry points with transactional type reload")
    require(
        "FInstancedPropertyBag ScriptState" in unreal_component
        and "FixedLayout" in unreal_component
        and "RefreshScriptState" in unreal_component
        and "GetScriptStateInt32" in unreal_component
        and "ScriptStateOverrideIds" in unreal_component
        and "ResetScriptStateOverride" in unreal_component
        and "ResetAllScriptStateOverrides" in unreal_component
        and "MaterializedStateOverrideCount" in unreal_component
        and "MaterializeJson" in unreal_declaration_materializer_header
        and "FUnifiedScriptStateMigrationReport"
        in unreal_declaration_materializer_header
        and "MigrateToNewBagInstanceWithOverrides"
        in unreal_declaration_materializer_source
        and "ParseSemanticId" in unreal_declaration_materializer_source
        and "UnifiedScript.Unreal.Declaration.Materialization"
        in unreal_declaration_materializer_test
        and "Stable semantic ID preserves override across rename"
        in unreal_declaration_materializer_test
        and "Untouched instance adopts the new C# default"
        in unreal_declaration_materializer_test
        and "Incompatible override falls back to the new default"
        in unreal_declaration_materializer_test
        and "Reload_PreservesInstanceOverrideIdentityAcrossGraphAndSourceEdits"
        in managed_reload_domain_test
        and unreal_demo_declaration["schemaVersion"]
        == "unifiedscript.script-asset/v0.1"
        and unreal_demo_declaration["assets"][0]["assetPath"] == "Unreal/Demo"
        and unreal_demo_declaration["assets"][0]["states"][0]["name"]
        == "InitialValue"
        and "[ScriptAsset(\"Unreal/Demo\")]" in unreal_demo_source
        and "GetScriptStateInt32" in unreal_demo_source
        and "DeclarationState=37" in unreal_world_host_test
        and "Reset restores the current declaration default"
        in unreal_world_host_test
        and "Dropped override is observable after asset replacement"
        in unreal_world_host_test
        and "- [x] preserve instance overrides and compatible semantic identities"
        in roadmap,
        "Declaration IR state materialization or live component integration is missing",
    )
    checks.append("UE component materializes Declaration IR state with stable-ID migration")
    unreal_demo_surface_asset = unreal_demo_surface["assets"][0]
    unreal_demo_surface_functions = {
        function["name"]: function
        for function in unreal_demo_surface_asset["functions"]
    }
    unreal_demo_event_actions = [
        action
        for function in unreal_demo_surface_asset["functions"]
        for action in function["actions"]
        if action["graphKind"] == "unrealEventEntry"
    ]
    unreal_demo_edit_capabilities = [
        edit
        for function in unreal_demo_surface_asset["functions"]
        for action in function["actions"]
        for edit in action["editCapabilities"]
    ]
    require(
        unreal_demo_surface["schemaVersion"]
        == "unifiedscript.authoring-surface/v0.4"
        and len(unreal_demo_surface["assets"]) == 1
        and len(unreal_demo_surface_asset["states"]) == 1
        and len(unreal_demo_surface_asset["functions"]) == 7
        and sum(
            len(function["actions"])
            for function in unreal_demo_surface_asset["functions"]
        ) == 70
        and sum(
            len(function["edges"])
            for function in unreal_demo_surface_asset["functions"]
        ) == 68
        and all(
            isinstance(action["properties"], dict)
            for function in unreal_demo_surface_asset["functions"]
            for action in function["actions"]
        )
        and set(unreal_demo_surface_functions)
        == {
            "OnManagedSignal",
            "NormalizeTickDelta",
            "ResolveInitialValue",
            "ResolveTickDelta",
            "RewirePreview",
            "UEApiPreview",
            "UEDefaultPreview",
        }
        and len(unreal_demo_event_actions) == 1
        and unreal_demo_event_actions[0]["title"] == "Event OnManagedSignal"
        and "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:OnManagedSignal"
        in unreal_demo_event_actions[0]["summary"]
        and [
            port["typeDisplayName"]
            for port in unreal_demo_event_actions[0]["ports"]
            if port["kind"] == "data"
        ]
        == ["int", "UnrealObjectHandle"]
        and len({edit["targetNodeId"] for edit in unreal_demo_edit_capabilities}) == 13
        and "FUnifiedScriptAuthoringSurfaceReader" in unreal_authoring_surface_header
        and "ParseJson" in unreal_authoring_surface_source
        and "BuildSemanticPatch" in unreal_authoring_surface_source
        and "BuildDataEdgeReconnectionPatch" in unreal_authoring_surface_source
        and "BuildDataEdgeDisconnectionPatch" in unreal_authoring_surface_source
        and "BuildSharedDataEdgePatchPlan" in unreal_authoring_surface_source
        and "BuildBasicNodeInsertionPatch" in unreal_authoring_surface_source
        and "BuildSwitchSectionInsertionPatch" in unreal_authoring_surface_source
        and "BuildSwitchSectionReplacementPatch" in unreal_authoring_surface_source
        and "BuildSwitchSectionMovePatch" in unreal_authoring_surface_source
        and "BuildSwitchSectionDeletionPatch" in unreal_authoring_surface_source
        and "FSwitchPatternTextParser" in unreal_authoring_surface_source
        and "BuildMemberInvocationInsertionPatch" in unreal_authoring_surface_source
        and "BuildUnrealEventHandlerInsertionPatch" in unreal_authoring_surface_source
        and "BuildStatementDeletionPatch" in unreal_authoring_surface_source
        and "BuildStatementDuplicationPatch" in unreal_authoring_surface_source
        and 'TEXT("duplicateStatement")' in unreal_authoring_surface_source
        and "BuildStatementRangeDuplicationPatch" in unreal_authoring_surface_source
        and 'TEXT("duplicateStatements")' in unreal_authoring_surface_source
        and "BuildCallableExtractionPatch" in unreal_authoring_surface_source
        and 'TEXT("extractCallable")' in unreal_authoring_surface_source
        and "BuildStatementCollapsePatch" in unreal_authoring_surface_source
        and "BuildBlockExpansionPatch" in unreal_authoring_surface_source
        and 'TEXT("collapseStatements")' in unreal_authoring_surface_source
        and 'TEXT("expandBlock")' in unreal_authoring_surface_source
        and "reconnectDataEdge" in unreal_authoring_surface_source
        and "BuildControlEdgeReconnectionPatch" in unreal_authoring_surface_source
        and "moveStatement" in unreal_authoring_surface_source
        and "HandleControlRewireRequested" in unreal_authoring_widget
        and "HandleAddSwitchSection" in unreal_authoring_widget
        and "HandleReplaceSwitchSection" in unreal_authoring_widget
        and "HandleMoveSwitchSection" in unreal_authoring_widget
        and "HandleDeleteSwitchSection" in unreal_authoring_widget
        and "HandleCopySelectedStatement" in unreal_authoring_widget
        and "HandlePasteStatement" in unreal_authoring_widget
        and "FGenericCommands::Get().Copy" in unreal_authoring_widget
        and "FGenericCommands::Get().Paste" in unreal_authoring_widget
        and "FGenericCommands::Get().Undo" in unreal_authoring_widget
        and "FGenericCommands::Get().Redo" in unreal_authoring_widget
        and "HandleExtractSelectedStatements" in unreal_authoring_widget
        and "HandleCollapseSelectedStatements" in unreal_authoring_widget
        and "HandleExpandBlockRegion" in unreal_authoring_widget
        and 'TEXT("flow.switch")' in unreal_authoring_widget
        and "unifiedscript.patch/v0.28" in unreal_authoring_surface_source
        and "SUnifiedScriptAuthoringSurface" in unreal_authoring_widget
        and "HandleDataRewireRequested" in unreal_authoring_widget
        and "SubmitSemanticPatch" in unreal_authoring_widget
        and "BuildSemanticPatchPlanUrl" in unreal_authoring_widget
        and "RebuildNodePalette" in unreal_authoring_widget
        and "EventAction.bEvent = true" in unreal_authoring_widget
        and "/api/authoring/declaration-patch" in unreal_authoring_workbench_source
        and "PersistCurrentLayout" in unreal_authoring_widget
        and "FUnifiedScriptGraphPresentation::ShouldInlineAction"
        in unreal_graph_presentation
        and "Value = 100" in unreal_authoring_test
        and "CountVisibleActions(Function)" in unreal_authoring_test
        and "ResolveAuthoringSurfacePath" in unreal_authoring_widget
        and "Copy Patch" in unreal_authoring_widget
        and "RegisterNomadTabSpawner" in unreal_editor_module
        and "LevelEditor.MainMenu.Tools" in unreal_editor_module
        and "UnifiedScript.Unreal.Editor.AuthoringSurface" in unreal_authoring_test
        and "Old compiler-owned wire remains until the server commits"
        in unreal_authoring_test
        and "Multi-selection can build a bounded callable extraction"
        in unreal_authoring_test
        and "Statement duplication preserves both source and destination gaps"
        in unreal_authoring_test
        and "Range copy preserves ordered source and distinct destination facts"
        in unreal_authoring_test
        and "Scope collapse carries ordered members and outer sibling gaps"
        in unreal_authoring_test
        and "Scope expansion carries block, children, parent, and sibling facts"
        in unreal_authoring_test
        and "AuthoringSurfacePath" in unreal_script_asset_header,
        "Native Slate Authoring Surface or transactional data-edge patch is missing",
    )
    checks.append(
        "UE Slate losslessly compacts Surface v0.4 into author-facing nodes and pins"
    )
    require(
        "FUnifiedScriptNodePaletteModel" in unreal_node_palette_header
        and "ScoreMatch" in unreal_node_palette_source
        and "MaxRecentActions" in unreal_node_palette_source
        and "SSearchBox" in unreal_authoring_widget
        and "Ctrl+Space" in unreal_authoring_widget
        and "HandleToggleNodePaletteFavorite" in unreal_authoring_widget
        and "RecordNodePaletteUsage" in unreal_authoring_widget
        and "GEditorPerProjectIni" in unreal_authoring_widget
        and "No active block hides statement actions but retains function events"
        in unreal_authoring_test,
        "Blueprint-style context Palette search, favorites, or recents are incomplete",
    )
    checks.append(
        "UE Slate Palette is searchable, context-filtered, keyboard-driven, and preference-aware"
    )
    semantic_patch_model = (
        ROOT / "src" / "UnifiedScript.Core" / "SemanticPatch.cs"
    ).read_text(encoding="utf-8")
    semantic_patch_json = (
        ROOT / "src" / "UnifiedScript.Core" / "SemanticPatchJson.cs"
    ).read_text(encoding="utf-8")
    source_patch_applier = (
        ROOT / "src" / "UnifiedScript.CSharp" / "CSharpSourcePatchApplier.cs"
    ).read_text(encoding="utf-8")
    require(
        "SetDataInputValuePatchOperation" in semantic_patch_model
        and "unifiedscript.patch/v0.28" in semantic_patch_model
        and "ValidateSetDataInputValue" in semantic_patch_json
        and "PlanDataInputReplacement" in source_patch_applier
        and "BuildDataInputValuePatch" in unreal_authoring_surface_source
        and "BuildDataInputPromotionPatch" in unreal_authoring_surface_source
        and 'TEXT("setDataInputValue")' in unreal_authoring_surface_source
        and "HandleApplyDataInputValue" in unreal_authoring_widget
        and "HandleResetDataInputValue" in unreal_authoring_widget
        and "HandlePromoteDataInput" in unreal_authoring_widget
        and "Pin Details can build a typed literal replacement" in unreal_authoring_test
        and "Pin Details can promote its source expression to a typed local" in unreal_authoring_test,
        "Source-backed typed Pin Defaults or Promote to Local are incomplete",
    )
    checks.append(
        "UE Slate Pin Authoring writes typed values and promoted locals through source transactions"
    )
    require(
        "UUnifiedScriptEdGraphCommentNode" in unreal_ed_graph_header
        and "UUnifiedScriptEdGraphRerouteNode" in unreal_ed_graph_header
        and "OnPinConnectionDoubleCicked" in unreal_ed_graph_source
        and "Following->PointIndex" in unreal_ed_graph_source
        and 'SetArrayField(TEXT("commentRegions")' in unreal_graph_layout_store
        and 'SetArrayField(TEXT("routingHints")' in unreal_graph_layout_store
        and "invalid or duplicate semantic node" in unreal_graph_layout_store
        and "SUnifiedScriptCanvasMinimap" in unreal_authoring_widget
        and "PersistCanvasView" in unreal_authoring_widget
        and "RestoreCanvasView" in unreal_authoring_widget
        and "HandleFocusSelection" in unreal_authoring_widget
        and "CanvasConfigSection" in unreal_authoring_widget
        and "EKeys::C" in unreal_authoring_widget
        and "Deleting the final layout-only reroute reconnects the semantic wire"
        in unreal_authoring_test
        and "Native canvas layout reload succeeds" in unreal_authoring_test
        and "Double-clicking a wire forwards an exact reroute request"
        in unreal_authoring_test,
        "Native Comment/Reroute authoring, minimap, or viewport persistence is incomplete",
    )
    checks.append(
        "UE Slate Canvas Authoring persists shared layout and personal viewport navigation"
    )
    require(
        "GetNodeSubtitle" in unreal_graph_presentation
        and "GetNodeTooltip" in unreal_graph_presentation
        and "CompactNodeText" in unreal_graph_presentation
        and "AdvancedTooltip" in unreal_ed_graph_header
        and "SExpandableArea" in unreal_authoring_widget
        and "BuildAdvancedNodeDetails" in unreal_authoring_widget
        and "COMPILER / GENERATED API METADATA" in unreal_authoring_widget
        and "SOURCE EDIT CAPABILITIES" in unreal_authoring_widget
        and "Default Event Entry content hides the generated API path"
        in unreal_authoring_test
        and "On-demand Event Entry tooltip retains effects, API path, and exact types"
        in unreal_authoring_test
        and "Member read tooltip retains generated symbol and type identity"
        in unreal_authoring_test,
        "Compact semantic cards or on-demand exact Advanced Node Details are incomplete",
    )
    checks.append(
        "UE Slate keeps author-facing cards compact and exact compiler/API facts on demand"
    )
    native_debug_doc = (
        ROOT / "docs" / "NATIVE_DEBUG_WORKBENCH.md"
    ).read_text(encoding="utf-8")
    require(
        "unifiedscript.debug-session/v0.2" in unreal_debug_workbench_source
        and "unifiedscript.debug-evaluation/v0.1" in unreal_debug_workbench_source
        and "BuildStartRequest" in unreal_debug_workbench_header
        and "expectedSourceCompilationId" in unreal_debug_workbench_source
        and "expectedSemanticConfigurationId" in unreal_debug_workbench_source
        and "/api/debug/wait" in unreal_debug_workbench_source
        and "/api/debug/pause" in unreal_debug_workbench_source
        and "/api/debug/step-in" in unreal_debug_workbench_source
        and "/api/debug/step-over" in unreal_debug_workbench_source
        and "/api/debug/step-out" in unreal_debug_workbench_source
        and "/api/debug/evaluate" in unreal_debug_workbench_source
        and "SUnifiedScriptDebugPanel" in unreal_authoring_widget
        and "HandleBreakpointToggleRequested" in unreal_authoring_widget
        and "HandleDebugNodeFocused" in unreal_authoring_widget
        and "CALL STACK" in unreal_debug_panel
        and "LOCALS / PARAMETERS" in unreal_debug_panel
        and "READ-ONLY WATCH" in unreal_debug_panel
        and "DispatchWait" in unreal_debug_panel
        and "DebugTargetId" in unreal_ed_graph_header
        and "DebugBreakpointState" in unreal_ed_graph_header
        and "Native debugger parses a paused v0.2 session" in unreal_authoring_test
        and "Slate can toggle a graph/source semantic breakpoint" in unreal_authoring_test
        and "Attach Semantic" in native_debug_doc
        and "live World semantic completion" in roadmap,
        "Native Slate debugger protocol, controls, semantic navigation, or safety boundary is incomplete",
    )
    checks.append(
        "UE Slate consumes guarded debug v0.2 controls, stack, per-frame locals, and watch"
    )
    runtime_diagnostic_doc = (
        ROOT / "docs" / "RUNTIME_DIAGNOSTICS.md"
    ).read_text(encoding="utf-8")
    runtime_diagnostic_test = (
        ROOT / "tests" / "UnifiedScript.Tests" / "UnrealRuntimeDiagnosticTests.cs"
    ).read_text(encoding="utf-8")
    require(
        "unifiedscript.runtime-diagnostic/v0.1" in unreal_runtime_diagnostic_managed
        and "SourceFrames" in unreal_runtime_diagnostic_managed
        and "UnrealInvocationException" in unreal_runtime_diagnostic_managed
        and "MemberPath" in unreal_runtime_diagnostic_managed
        and "runtime-diagnostic/v0.1" in unreal_runtime_diagnostic_source
        and "TryParseBoundaryError" in unreal_runtime_diagnostic_source
        and "GetRuntimeDebugContext" in runtime_component_source
        and "PauseAtDebugFrameBoundary" in runtime_component_source
        and "StepOneDebugFrame" in runtime_component_source
        and "ContinueFromDebugFrameBoundary" in runtime_component_source
        and "LIVE UE WORLD" in unreal_debug_panel
        and "Pause World Script" in unreal_debug_panel
        and "Focus Node" in unreal_debug_panel
        and "ResolveRuntimeDiagnosticNode" in unreal_debug_workbench_source
        and "Runtime PDB coordinates resolve to the exact semantic graph node"
        in unreal_authoring_test
        and "World frame step enters managed OnTick once" in unreal_world_host_test
        and "Managed boundary failure is parsed as runtime-diagnostic/v0.1"
        in unreal_world_host_test
        and "GeneratedFixedCallFramePathHasZeroSteadyStateManagedAllocations"
        in unreal_native_adapter_test
        and "NativeBridgeFailureCarriesExactInvocationSiteAndStableJson"
        in runtime_diagnostic_test
        and "$(AssemblyName).pdb" in unreal_demo_project
        and "Live bridge failure retains the generated member path"
        in unreal_world_host_test
        and "World frame step enters managed OnTick when automatic tick is disabled"
        in unreal_world_host_test
        and "frame-boundary control" in runtime_diagnostic_doc
        and "- [x] map exceptions and native bridge failures" in roadmap,
        "Live UE World context, frame-boundary control, or structured runtime failure mapping is incomplete",
    )
    checks.append(
        "UE Slate correlates live World failures to generation, handles, source, and graph"
    )
    unreal_world_debug_session = (
        ROOT / "src" / "UnifiedScript.Unreal" / "UnrealWorldDebugSession.cs"
    ).read_text(encoding="utf-8")
    unreal_world_debug_session_test = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealWorldDebugSessionTests.cs"
    ).read_text(encoding="utf-8")
    authoring_http_test = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "CoreClrAuthoringHttpServerTests.cs"
    ).read_text(encoding="utf-8")
    semantic_debug_fixture_path = (
        ROOT
        / "samples"
        / "Unreal"
        / "UnifiedScript.Unreal.Demo"
        / "RuntimeSemanticDebugDemoScript.cs"
    )
    semantic_debug_fixture = semantic_debug_fixture_path.read_text(encoding="utf-8")
    demo_identity_path = (
        ROOT
        / "samples"
        / "Unreal"
        / "UnifiedScript.Unreal.Demo"
        / "DemoScript.cs.usids"
    )
    with demo_identity_path.open("r", encoding="utf-8") as handle:
        demo_identity = json.load(handle)
    demo_source_digest = hashlib.sha256(unreal_demo_source_path.read_bytes()).hexdigest()
    require(
        '"/api/debug/publish-live-world"' in debug_server_source
        and "CSharpDebugInstrumentation" in debug_server_source
        and "unifiedscript.live-world-debug-publish/v0.1" in debug_server_source
        and "new Thread" in unreal_world_debug_session
        and "Volatile.Read(ref completed)" in unreal_world_debug_session
        and "checked(++nextFrameId)" in unreal_world_debug_session
        and "LiveWorldProbe_PreservesWorkerStackAndStepsWithoutBlockingCaller"
        in unreal_world_debug_session_test
        and "typeof(global::UnifiedScript.Runtime.CoreClrDebugProbe).Assembly"
        in unreal_world_managed_entry
        and "WrittenBytes > RequiredBytes" in unreal_coreclr_host_source
        and "PumpGameThreadCalls" in unreal_native_transport_source
        and "BeginManagedScriptDebugTick" in runtime_component_source
        and "HandleAttachSemanticWorld" in unreal_debug_panel
        and "EvaluateLiveSemanticWatch" in unreal_debug_panel
        and "SegmentCount > 16" in unreal_debug_panel
        and "Native debugger parses the instrumented live World publication"
        in unreal_authoring_test
        and "FUnifiedScriptWorldSemanticDebugTest" in unreal_world_host_test
        and "Worker-originated generated bridge calls return through the Game Thread"
        in unreal_world_host_test
        and "Reloaded generation reaches the migrated semantic breakpoint"
        in unreal_world_host_test
        and "stale-live-world-source" in authoring_http_test
        and "UnifiedScript.Runtime" in authoring_http_test
        and "RuntimeSemanticDebugDemoScript" in semantic_debug_fixture
        and "CoreClrDebugProbe.Hit" in semantic_debug_fixture
        and demo_identity["sourcePath"]
        == "samples/Unreal/UnifiedScript.Unreal.Demo/DemoScript.cs"
        and demo_identity["sourceDigest"] == f"sha256:{demo_source_digest}"
        and "- [x] bind semantic breakpoints to current managed generations"
        in roadmap
        and "M9.1 is\ncomplete" in roadmap,
        "Live UE World semantic suspension, bridge return, or generation binding is incomplete",
    )
    checks.append(
        "UE Slate controls live World semantic stacks without blocking the GameThread"
    )
    hot_reload_reliability_doc = (
        ROOT / "docs" / "HOT_RELOAD_RELIABILITY.md"
    ).read_text(encoding="utf-8")
    require(
        "unifiedscript.reload-transaction/v0.1" in debug_server_source
        and '"compile"' in debug_server_source
        and '"artifactPublication"' in debug_server_source
        and '"workbenchGenerationSwap"' in debug_server_source
        and "oldGenerationStop" in unreal_world_managed_entry
        and "candidateStart" in unreal_world_managed_entry
        and "eventRebinding" in unreal_world_managed_entry
        and "generationSwap" in unreal_world_managed_entry
        and "oldContextRetirement" in unreal_world_managed_entry
        and "stateMigration" in runtime_component_source
        and "stateRollback" in runtime_component_source
        and "oldContextCollection" in runtime_component_source
        and "retiredLoadContextsAlive" in runtime_component_source
        and "worldFromGeneration" in runtime_component_source
        and "unifiedscript.unreal-lifecycle-stress/v0.4"
        in unreal_lifecycle_stress_test
        and "FailedReloadCycles" in unreal_lifecycle_stress_test
        and "GetEventSubscriptionCount" in unreal_lifecycle_stress_test
        and "staleDebugSessions" in unreal_lifecycle_stress_test
        and "failedReloadCycles" in unreal_lifecycle_stress_helper
        and "unifiedscript.unreal-lifecycle-stress-report/v0.3"
        in unreal_lifecycle_stress_helper
        and "eventSubscriptionsDelta" in unreal_lifecycle_stress_helper
        and "staleDebugSessions" in unreal_lifecycle_stress_helper
        and "ParseReloadTransaction" in unreal_authoring_workbench_source
        and "ObservedTransaction.ToDisplayString()" in unreal_authoring_widget
        and "ObservedTransaction.ToDisplayString()" in unreal_debug_panel
        and "16 successful reloads" in hot_reload_reliability_doc
        and "16 intentionally failed reloads" in hot_reload_reliability_doc
        and "- [x] make compile, artifact publication" in roadmap
        and "M9.2 is\ncomplete" in roadmap,
        "Observable hot-reload phases, rollback, leak soak, or Slate diagnostics are incomplete",
    )
    checks.append(
        "UE hot reload is one observable rollback-safe transaction with zero-leak soak"
    )
    reliability_soak_doc = (ROOT / "docs" / "RELIABILITY_SOAK.md").read_text(
        encoding="utf-8"
    )
    coreclr_debug_coordinator = (
        ROOT / "src" / "UnifiedScript.Runtime" / "CoreClrDebugCoordinator.cs"
    ).read_text(encoding="utf-8")
    require(
        unreal_reliability_soak_helper_path.is_file()
        and "unifiedscript.unreal-reliability-soak/v0.2"
        in unreal_reliability_soak_helper
        and "DedicatedServer" in unreal_lifecycle_stress_helper
        and "-server" in unreal_lifecycle_stress_helper
        and "boundedCapacity" in unreal_lifecycle_stress_helper
        and "memoryBudget" in unreal_lifecycle_stress_helper
        and "ServerContext" in unreal_lifecycle_stress_test
        and "ObjectChurnCycles" in unreal_lifecycle_stress_test
        and "CommandSaturationCycles" in unreal_lifecycle_stress_test
        and "MaximumInFlightRequestCount = 4096" in unreal_async_service_header
        and "MaximumPayloadByteCount = 1024 * 1024" in unreal_async_service_header
        and "GCapacityRejectionCount" in unreal_async_service_source
        and "MaximumPendingWorkerDispatchCount = 1024"
        in unreal_native_transport_header
        and "GRejectedWorkerDispatchCount" in unreal_native_transport_source
        and "Channel.CreateBounded<CoreClrDebugPause>" in coreclr_debug_coordinator
        and "dedicated-server **process mode**" in reliability_soak_doc
        and "131,072 commands" in reliability_soak_doc
        and "- [x] run long PIE, travel, multi-World, dedicated-server"
        in roadmap,
        "M11.1 two-lane reliability soak or bounded runtime queues are incomplete",
    )
    checks.append(
        "M11.1 soaks editor and server-mode Worlds against explicit memory and queue bounds"
    )
    require(
        "FUnifiedScriptRuntimeBreadcrumbSnapshot"
        in unreal_runtime_diagnostic_header
        and "MaximumEntryCount = 64" in unreal_runtime_diagnostic_header
        and "MaximumCrashContextEntryCount = 8"
        in unreal_runtime_diagnostic_header
        and "unifiedscript.runtime-breadcrumbs/v0.1"
        in unreal_runtime_diagnostic_source
        and "GBreadcrumbs.RemoveAt(0" in unreal_runtime_diagnostic_source
        and "FGenericCrashContext::SetGameData"
        in unreal_runtime_diagnostic_source
        and "RecordDiagnostic(LastRuntimeDiagnostic)" in runtime_component_source
        and "WorldShutdown.WorkerDispatchSuspended" in unreal_world_host_source
        and "WorldShutdown.ManagedWorldDestroyed" in unreal_world_host_source
        and "WorldShutdown.NativeContextDetached" in unreal_world_host_source
        and "WorldShutdown.ServicesStopped" in unreal_world_host_source
        and "WorldShutdown.HandlesInvalidated" in unreal_world_host_source
        and "WorldShutdown.Complete" in unreal_world_host_source
        and "healthy sibling continues managed Tick" in unreal_world_host_test
        and "HasDeterministicShutdownSequence" in unreal_lifecycle_stress_test
        and "shutdownSequenceFailures" in unreal_lifecycle_stress_test
        and "MaximumEntryCount + 6" in unreal_runtime_breadcrumb_test
        and "CrashContext" in unreal_runtime_breadcrumb_test
        and "deterministicShutdown" in unreal_lifecycle_stress_helper
        and "last 64 detached managed" in runtime_diagnostic_doc
        and "All 152 World shutdowns" in reliability_soak_doc
        and "- [x] enforce bounded memory/queues, deterministic shutdown"
        in roadmap,
        "M11.1 deterministic shutdown, failure isolation, or crash breadcrumbs are incomplete",
    )
    checks.append(
        "M11.1 contains script faults and preserves bounded crash-ready shutdown breadcrumbs"
    )
    unreal_scale_runner = (
        ROOT / "tools" / "run-unreal-scale-gate.ps1"
    ).read_text(encoding="utf-8")
    large_project_scale_doc = (
        ROOT / "docs" / "LARGE_PROJECT_SCALE.md"
    ).read_text(encoding="utf-8")
    csharp_semantic_ids = (
        ROOT / "src" / "UnifiedScript.CSharp" / "CSharpSemanticIds.cs"
    ).read_text(encoding="utf-8")
    csharp_program_emitter_tests = (
        ROOT / "tests" / "UnifiedScript.Tests" / "CSharpProgramEmitterTests.cs"
    ).read_text(encoding="utf-8")
    require(
        "unifiedscript.unreal-scale-gate/v0.1" in unreal_scale_runner
        and "[int]$TypeCount = 256" in unreal_scale_runner
        and "[int]$MembersPerType = 16" in unreal_scale_runner
        and "[int]$ScriptCount = 256" in unreal_scale_runner
        and "$contractDeterministic" in unreal_scale_runner
        and "$contractManagedChanged" in unreal_scale_runner
        and "$contractNativeStable" in unreal_scale_runner
        and "$scriptChunkChanged" in unreal_scale_runner
        and "Scripts=1, Created=0, Updated=1" in unreal_scale_runner
        and "$cleanupPass" in unreal_scale_runner
        and "DocumentationCommentId.CreateDeclarationId" in csharp_semantic_ids
        and "Emit_SupportsMultipleScriptAssetsWithIdenticalMethodShapes"
        in csharp_program_emitter_tests
        and "256 reflected Unreal types" in large_project_scale_doc
        and "4,096 accepted" in large_project_scale_doc
        and "updated exactly one" in large_project_scale_doc
        and "- [x] test large contracts and script sets" in roadmap
        and "- Active milestone: **M12" in roadmap,
        "M11.1 large Contract, script-set, incremental, or asset-churn scale gate is incomplete",
    )
    checks.append(
        "M11.1 gates 4096 Contract members and 256 compiled scripts through localized UE churn"
    )
    gameplay_profiler_doc = (
        ROOT / "docs" / "GAMEPLAY_PROFILER.md"
    ).read_text(encoding="utf-8")
    gameplay_profiler_source = (
        ROOT
        / "src"
        / "UnifiedScript.Unreal"
        / "UnrealGameplayProfiler.cs"
    ).read_text(encoding="utf-8")
    gameplay_profiler_fixture = (
        ROOT
        / "samples"
        / "Unreal"
        / "UnifiedScript.Unreal.Demo"
        / "GameplayProfilerDemoScript.cs"
    ).read_text(encoding="utf-8")
    gameplay_profiler_native_test = (
        ROOT
        / "HostProject"
        / "Plugins"
        / "UEUnifiedScript"
        / "Source"
        / "UEUnifiedScriptRuntime"
        / "Private"
        / "Tests"
        / "UnifiedScriptGameplayProfilerTests.cpp"
    ).read_text(encoding="utf-8")
    require(
        "unifiedscript.gameplay-profile/v0.3" in gameplay_profiler_source
        and "DefaultFrameCapacity = 120" in gameplay_profiler_source
        and "BuildWorldPhases" in gameplay_profiler_source
        and "BuildPressure" in gameplay_profiler_source
        and 'new("reflectionFallback"' in gameplay_profiler_source
        and "probeObserver?.Hit" in unreal_world_debug_session
        and "NativePreActorTickNanoseconds" in unreal_gameplay_data_source
        and "NativePostActorTickNanoseconds" in unreal_gameplay_data_source
        and "unifiedscript.gameplay-profile/v0.3"
        in unreal_debug_workbench_source
        and '"Focus Hot Node"' in unreal_debug_panel
        and '"Save JSON"' in unreal_debug_panel
        and "GameplayProfilerDemoScript" in gameplay_profiler_fixture
        and "BeginTraceBatch" in gameplay_profiler_fixture
        and "BeginOverlapBatch" in gameplay_profiler_fixture
        and "UnifiedScript.Unreal.Profiler.FullAttribution"
        in gameplay_profiler_native_test
        and "Semantic source and graph nodes share the gameplay timeline"
        in gameplay_profiler_native_test
        and "Saved/UnifiedScript/Profiles" in gameplay_profiler_doc
        and "performance baseline" in gameplay_profiler_doc
        and "- [x] attribute time and count to scripts" in roadmap
        and "M9.3 is\ncomplete" in roadmap,
        "Gameplay profiler attribution, bounded trace, Slate workflow, or live World proof is incomplete",
    )
    checks.append(
        "UE gameplay profiler attributes World, semantic, bridge, data, allocation, and pressure cost"
    )
    remote_debug_frame_source = (
        ROOT
        / "src"
        / "UnifiedScript.Runtime"
        / "CoreClrDebugFrameProtocol.cs"
    ).read_text(encoding="utf-8")
    remote_debug_transport_source = (
        ROOT
        / "src"
        / "UnifiedScript.Runtime"
        / "CoreClrDebugRemoteTransport.cs"
    ).read_text(encoding="utf-8")
    remote_debug_test = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "CoreClrDebugRemoteTransportTests.cs"
    ).read_text(encoding="utf-8")
    gameplay_diagnosis_source = (
        ROOT
        / "src"
        / "UnifiedScript.Unreal"
        / "UnrealGameplayDiagnosis.cs"
    ).read_text(encoding="utf-8")
    gameplay_diagnosis_test = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealGameplayDiagnosisTests.cs"
    ).read_text(encoding="utf-8")
    remote_debug_doc = (
        ROOT / "docs" / "REMOTE_DEBUG_FRAMING.md"
    ).read_text(encoding="utf-8")
    gameplay_diagnosis_report_path = (
        ROOT
        / "samples"
        / "Unreal"
        / "Diagnostics"
        / "GameplayDiagnosis.report.json"
    )
    with gameplay_diagnosis_report_path.open("r", encoding="utf-8") as handle:
        gameplay_diagnosis_report = json.load(handle)
    require(
        "unifiedscript.debug-frame/v0.1" in remote_debug_frame_source
        and 'Magic = 0x42445355' in remote_debug_frame_source
        and "MaximumPayloadBytes = 1024 * 1024" in remote_debug_frame_source
        and "CoreClrDebugRemoteTransport" in remote_debug_transport_source
        and "maximumConnections = 32" in remote_debug_transport_source
        and "LongPollConnectionDoesNotBlockIndependentPauseAndContinueRequests"
        in remote_debug_test
        and 'args[0] == "debug-frame-serve"' in cli_source
        and "unifiedscript.debug-frame-server/v0.1" in cli_source
        and "24-byte little-endian header" in remote_debug_doc
        and "unifiedscript.gameplay-diagnosis/v0.1" in gameplay_diagnosis_source
        and "GetBoundaryCosts" in gameplay_diagnosis_source
        and "LiveDebugValueAndProfileDeltaLocateDefectAndRegressionWithoutLogs"
        in gameplay_diagnosis_test
        and 'args[0] == "ue-profile-diagnose"' in cli_source
        and gameplay_diagnosis_report["schemaVersion"]
        == "unifiedscript.gameplay-diagnosis/v0.1"
        and gameplay_diagnosis_report["acceptanceSatisfied"] is True
        and {
            finding["kind"] for finding in gameplay_diagnosis_report["findings"]
        }
        == {"gameplayDefect", "performanceRegression"}
        and all(
            finding["nodeId"].startswith("us_")
            and finding["sourceSpan"]["filePath"].endswith(
                "GameplayDiagnosisFixture.cs"
            )
            and finding["runtimeBoundary"]
            in {"reflectionFallback", "batchTrace"}
            for finding in gameplay_diagnosis_report["findings"]
        )
        and "- [x] add bounded versioned remote debug framing" in roadmap,
        "Bounded process debug framing or source/node/boundary diagnosis proof is incomplete",
    )
    checks.append(
        "remote debug control and gameplay diagnosis are bounded, concurrent, and reproducible"
    )
    performance_suite_source = (
        ROOT
        / "src"
        / "UnifiedScript.Unreal"
        / "UnrealPerformanceWorkloadSuite.cs"
    ).read_text(encoding="utf-8")
    performance_suite_test = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealPerformanceWorkloadSuiteTests.cs"
    ).read_text(encoding="utf-8")
    performance_budget_source = (
        ROOT
        / "src"
        / "UnifiedScript.Unreal"
        / "UnrealPerformanceBudgets.cs"
    ).read_text(encoding="utf-8")
    performance_budget_test = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "UnrealPerformanceBudgetTests.cs"
    ).read_text(encoding="utf-8")
    representative_workload_demo = (
        ROOT
        / "samples"
        / "Unreal"
        / "UnifiedScript.Unreal.Demo"
        / "RepresentativeWorkloadBenchmarkScript.cs"
    ).read_text(encoding="utf-8")
    performance_suite_doc = (
        ROOT / "docs" / "PERFORMANCE_WORKLOAD_SUITE.md"
    ).read_text(encoding="utf-8")
    performance_capture_writer = (
        ROOT / "comparison" / "write-workload-capture.ps1"
    ).read_text(encoding="utf-8")
    performance_pair_runner = (
        ROOT / "comparison" / "run-performance-comparison.ps1"
    ).read_text(encoding="utf-8")
    performance_runtime_identity = (
        ROOT / "comparison" / "get-runtime-source-identity.ps1"
    ).read_text(encoding="utf-8")
    performance_complete_runner = (
        ROOT / "comparison" / "run-complete-workload-suite.ps1"
    ).read_text(encoding="utf-8")
    performance_suite_manifest_path = (
        ROOT / "benchmarks" / "unreal" / "workload-suite.v0.1.json"
    )
    with performance_suite_manifest_path.open("r", encoding="utf-8") as handle:
        performance_suite_manifest = json.load(handle)
    performance_current_manifest_path = (
        ROOT / "benchmarks" / "unreal" / "workload-suite.v1.1.0.json"
    )
    performance_budget_manifest_path = (
        ROOT / "benchmarks" / "unreal" / "performance-budgets.v1.1.0.json"
    )
    with performance_current_manifest_path.open("r", encoding="utf-8") as handle:
        performance_current_manifest = json.load(handle)
    with performance_budget_manifest_path.open("r", encoding="utf-8") as handle:
        performance_budget_manifest = json.load(handle)
    expected_workload_categories = {
        "scalarPropertyCalls",
        "structContainerTransfer",
        "delegateStorm",
        "perFrameActorLogic",
        "massEntities",
        "batchQueries",
        "resourceSpawnFlow",
        "hotReload",
        "blockBreaker",
    }
    expected_performance_metrics = {
        "frameTimeMedianMilliseconds",
        "frameTimeP95Milliseconds",
        "frameTimeP99Milliseconds",
        "bridgeTimeMilliseconds",
        "calls",
        "bytesCopied",
        "allocatedBytes",
        "gcCollectionCount",
        "gcPauseMilliseconds",
        "memoryHighWaterBytes",
        "startupMilliseconds",
        "reloadMilliseconds",
        "packageSizeBytes",
    }
    suite_workloads = performance_suite_manifest["workloads"]
    require(
        performance_suite_manifest["schemaVersion"]
        == "unifiedscript.performance-workload-suite/v0.1"
        and performance_suite_manifest["trialCount"] == 3
        and performance_suite_manifest["build"]["engineVersion"] == "5.8.1"
        and performance_suite_manifest["build"]["rhi"] == "NullRHI"
        and len(suite_workloads) == 9
        and {workload["category"] for workload in suite_workloads}
        == expected_workload_categories
        and {
            metric
            for workload in suite_workloads
            for metric in workload["requiredMetrics"]
        }
        == expected_performance_metrics
        and abs(sum(workload["weight"] for workload in suite_workloads) - 1.0)
        < 0.000001
        and all(
            re.fullmatch(r"sha256:[0-9a-f]{64}", workload["inputDigest"])
            and re.fullmatch(r"sha256:[0-9a-f]{64}", workload["assetDigest"])
            for workload in suite_workloads
        )
        and "unifiedscript.performance-engine-result/v0.1"
        in performance_suite_source
        and "unifiedscript.performance-raw-measurement/v0.1"
        in performance_suite_source
        and "unifiedscript.performance-trial-capture/v0.1"
        in performance_suite_source
        and "CreateAlternatingSchedule" in performance_suite_source
        and "CreateCapture" in performance_suite_source
        and "AssembleResult" in performance_suite_source
        and "ComputeMachineFingerprint" in performance_suite_source
        and "environment differs" in performance_suite_source
        and "CheckedInManifestCoversM10WorkloadsMetricsAndAlternatingOrder"
        in performance_suite_test
        and "ComparisonRejectsAValidButDifferentMachineFingerprint"
        in performance_suite_test
        and "RawMeasurementIsNormalizedAgainstManifestAndScheduledSlot"
        in performance_suite_test
        and "IndependentTrialCapturesAssembleOnlyOneRuntimeAndEnvironment"
        in performance_suite_test
        and 'args[0] == "ue-workload-capture"' in cli_source
        and 'args[0] == "ue-workload-assemble"' in cli_source
        and 'args[0] == "ue-workload-compare"' in cli_source
        and "Get-CimInstance Win32_Processor" in performance_capture_writer
        and "Engine\\Build\\Build.version" in performance_capture_writer
        and "executionOrdinal" in performance_capture_writer
        and "SuiteTrial" in performance_pair_runner
        and "Get-RuntimeSourceIdentity" in performance_pair_runner
        and "unifiedscript.runtime-source-identity/v0.1"
        in performance_runtime_identity
        and "Get-FileHash" in performance_runtime_identity
        and "Get-PluginVersion" in performance_pair_runner
        and "ReportOnly" in performance_pair_runner
        and "if ($unifiedFirst)" in performance_pair_runner
        and "unifiedscript\\captures" in performance_pair_runner
        and "puerts\\captures" in performance_pair_runner
        and "AllowLosingComparison" in performance_pair_runner
        and "Test-CurrentPair" in performance_complete_runner
        and "run-representative-workload-comparison.ps1"
        in performance_complete_runner
        and "run-gameplay-comparison.ps1" in performance_complete_runner
        and "ue-workload-assemble" in performance_complete_runner
        and "ue-workload-compare" in performance_complete_runner
        and "GetAllocatedBytesForCurrentThread" in performance_suite_doc
        and "V8 used heap" in performance_suite_doc
        and "null ratio" in performance_suite_doc
        and "54 slots" in performance_suite_doc
        and "all 27 captures" in performance_suite_doc
        and "all 54 execution ordinals" in performance_suite_doc
        and "mixed results close M10.1" in performance_suite_doc
        and "- Active milestone: **M12" in roadmap
        and "- [x] use profiler evidence to promote only high-volume stable data"
        in roadmap
        and "- [x] preserve World ownership, generation checks, bounds"
        in roadmap
        and "- [x] make schemas generated, extensible, measurable, and debuggable"
        in roadmap
        and "- [x] capture median, p95/p99 frame time" in roadmap
        and "- [x] alternate engines and trials" in roadmap,
        "M10.1 workload manifest, metrics, alternating schedule, or strict comparison is incomplete",
    )
    checks.append(
        "representative performance suite versions nine workloads and rejects unmatched evidence"
    )
    current_workloads = {
        workload["id"]: workload
        for workload in performance_current_manifest["workloads"]
    }
    frozen_workloads = {
        workload["id"]: workload
        for workload in performance_suite_manifest["workloads"]
    }
    require(
        performance_suite_manifest["suiteVersion"] == "1.0.0"
        and performance_current_manifest["suiteVersion"] == "1.1.0"
        and set(current_workloads) == set(frozen_workloads)
        and current_workloads["struct-container-transfer"]["version"] == "1.1.0"
        and current_workloads["delegate-storm"]["version"] == "1.1.0"
        and current_workloads["delegate-storm"]["measuredIterations"] == 65536
        and all(
            current_workloads[workload_id]["inputDigest"]
            == frozen_workloads[workload_id]["inputDigest"]
            for workload_id in current_workloads
            if workload_id
            not in {"struct-container-transfer", "delegate-storm"}
        )
        and performance_budget_manifest["schemaVersion"]
        == "unifiedscript.performance-budgets/v0.1"
        and performance_budget_manifest["suiteVersion"] == "1.1.0"
        and performance_budget_manifest["referenceSuiteVersion"] == "1.0.0"
        and performance_budget_manifest["aggregation"]
        == "weighted-arithmetic-normalized"
        and performance_budget_manifest["maximumWeightedRatio"] <= 0.95
        and len(performance_budget_manifest["weightedMetrics"]) == 8
        and {
            item["workloadId"]
            for item in performance_budget_manifest["excludedWorkloads"]
        }
        == {"hot-reload"}
        and all(
            gate["rationale"] and gate["maximum"] >= 0
            for gate in performance_budget_manifest["regressionGates"]
        )
        and any(
            gate["id"] == "generated-container-allocation"
            and gate["maximum"] == 0
            for gate in performance_budget_manifest["regressionGates"]
        )
        and any(
            gate["id"] == "generated-event-allocation"
            and gate["maximum"] == 0
            for gate in performance_budget_manifest["regressionGates"]
        )
        and "WeightedArithmeticNormalized" in performance_budget_source
        and "BroadClaimQualified" in performance_budget_source
        and "RegressionGatesPassed" in performance_budget_source
        and "cover the complete suite" in performance_budget_source
        and "WeightedVerdictPublishesWinsAndLossesWithoutMovingThresholds"
        in performance_budget_test
        and "CheckedInM10Point3SuitePreservesFrozenWorkloadsAndReviewsBudgets"
        in performance_budget_test
        and 'args[0] == "ue-workload-budget"' in cli_source
        and "ue-workload-budget" in performance_complete_runner
        and "performance-budgets.v1.1.0.json" in performance_complete_runner
        and "artifacts\\comparison\\suite\\v1.1.0"
        in performance_complete_runner
        and "AcquireGeneratedSnapshot" in representative_workload_demo
        and "AcquireGeneratedEvents" in representative_workload_demo
        and "allocatedBytes -ne 0" in (
            ROOT / "tools" / "run-unreal-representative-workload.ps1"
        ).read_text(encoding="utf-8")
        and "- [x] keep zero steady-state managed allocation" in roadmap
        and "- [x] prevent regressions in the existing ABI" in roadmap
        and "- [x] require the weighted matched gameplay suite" in roadmap
        and "weighted result is 0.555769" in roadmap,
        "M10.3 versioned generated workloads, reviewed budgets, or honest verdict are incomplete",
    )
    checks.append(
        "M10.3 generated workloads preserve v1.0 evidence and publish reviewed weighted verdicts"
    )
    authoring_diagnostics_doc = (
        ROOT / "docs" / "AUTHORING_DIAGNOSTICS.md"
    ).read_text(encoding="utf-8")
    require(
        "unifiedscript.authoring-preview/v0.1" in debug_server_source
        and "X-UnifiedScript-Preview-Token" in debug_server_source
        and "TakePreparedPreview" in debug_server_source
        and "preparedPreviews" in debug_server_source
        and "CreateRollbackResponse" in debug_server_source
        and "AuthoringQuickFixResponse" in debug_server_source
        and "BuildPreviewUrl" in unreal_authoring_workbench_source
        and "ParsePreviewResponse" in unreal_authoring_workbench_source
        and "FUnifiedScriptAuthoringErrorResult" in unreal_authoring_workbench_header
        and "ShowDiffPreview" in unreal_authoring_widget
        and "ROLLBACK CONFIRMED" in unreal_authoring_widget
        and "HandleWorkspaceRefreshResponse" in unreal_authoring_widget
        and "Workbench parses exact before/after preview" in unreal_authoring_test
        and "Failure explicitly confirms unchanged rollback" in unreal_authoring_test
        and "mismatched preview token" in authoring_diagnostics_doc
        and "- [x] provide compiler/contract diagnostics" in roadmap
        and "### M8.3 — Asset, state, event, and API authoring" in roadmap,
        "Native prepared Diff, structured diagnostics, safe fixes, or rollback receipt is incomplete",
    )
    checks.append(
        "UE Slate binds exact server previews to commits and exposes diagnostic rollback UX"
    )
    identity_reconciler_source = (
        ROOT / "src" / "UnifiedScript.CSharp" / "CSharpIdentityReconciler.cs"
    ).read_text(encoding="utf-8")
    require(
        "FUnifiedScriptAuthoringWorkbench" in unreal_authoring_workbench_header
        and "unifiedscript.authoring-workspace/v0.5"
        in unreal_authoring_workbench_source
        and "/api/authoring/semantic-patch" in unreal_authoring_workbench_source
        and "/api/authoring/undo" in unreal_authoring_workbench_source
        and "/api/authoring/redo" in unreal_authoring_workbench_source
        and "/api/authoring/group/begin" in unreal_authoring_workbench_source
        and "/api/authoring/group/commit" in unreal_authoring_workbench_source
        and "/api/authoring/group/cancel" in unreal_authoring_workbench_source
        and "FHttpModule::Get().CreateRequest" in unreal_authoring_widget
        and "X-UnifiedScript-Baseline-Revision" in unreal_authoring_widget
        and "X-UnifiedScript-Transaction-Id" in unreal_authoring_widget
        and "X-UnifiedScript-Group-Id" in unreal_authoring_widget
        and "HandleUndoAuthoringTransaction" in unreal_authoring_widget
        and "HandleRedoAuthoringTransaction" in unreal_authoring_widget
        and "HandleBeginAuthoringGroup" in unreal_authoring_widget
        and "HandleCommitAuthoringGroup" in unreal_authoring_widget
        and "HandleCancelAuthoringGroup" in unreal_authoring_widget
        and "Apply & Reload" in unreal_authoring_widget
        and "ReloadMatchingComponents" in unreal_authoring_widget
        and "Component->ReloadScriptWithTransaction(" in unreal_authoring_widget
        and "AuthoringServerUrl" in unreal_script_asset_header
        and "AuthoringArtifactOutputs" in debug_server_source
        and "persistedSources" in debug_server_source
        and "ScriptAssetJson.Serialize(scriptAssets)" in debug_server_source
        and "AuthoringSurfaceJson.Serialize(surface)" in debug_server_source
        and "emitter.Emit(snapshot.Request)" in debug_server_source
        and 'case "--source"' in cli_source
        and 'case "--managed-out"' in cli_source
        and 'case "--history"' in cli_source
        and "isInitialBaseline" in identity_reconciler_source
        and "run-unreal-authoring.ps1" in (
            ROOT / "README.md"
        ).read_text(encoding="utf-8")
        and "--surface-out" in unreal_authoring_helper
        and "--managed-out" in unreal_authoring_helper
        and "--history" in unreal_authoring_helper,
        "Transactional Slate Workbench apply or managed artifact publication is missing",
    )
    checks.append("UE Slate applies patches transactionally and reloads matching components")
    visual_profile_model = (
        ROOT / "src" / "UnifiedScript.CSharp" / "VisualProfileModel.cs"
    ).read_text(encoding="utf-8")
    visual_profile_json = (
        ROOT / "src" / "UnifiedScript.CSharp" / "VisualProfileJson.cs"
    ).read_text(encoding="utf-8")
    visual_profile_inspector = (
        ROOT / "src" / "UnifiedScript.CSharp" / "VisualProfileInspector.cs"
    ).read_text(encoding="utf-8")
    visual_profile_test = (
        ROOT / "tests" / "UnifiedScript.Tests" / "VisualProfileInspectorTests.cs"
    ).read_text(encoding="utf-8")
    visual_profile_doc = (
        ROOT / "docs" / "VISUAL_CSHARP_PROFILE.md"
    ).read_text(encoding="utf-8")
    semantic_diagnostic_codes = (
        ROOT / "src" / "UnifiedScript.CSharp" / "SemanticDiagnosticCodes.cs"
    ).read_text(encoding="utf-8")
    authoring_surface_json = (
        ROOT / "src" / "UnifiedScript.Authoring" / "AuthoringSurfaceJson.cs"
    ).read_text(encoding="utf-8")
    require(
        "unifiedscript.visual-csharp-profile/v0.3" in visual_profile_model
        and "unifiedscript.visual-csharp-profile-report/v0.1" in visual_profile_model
        and "OpaqueCodeBoundaryContract" in visual_profile_model
        and "NormalizeAndValidate" in visual_profile_json
        and "VisualMethodRepresentation.OpaqueCode" in visual_profile_inspector
        and "OpaqueCodeBoundaryNotSupported = \"UVS2009\"" in semantic_diagnostic_codes
        and 'properties["calleeBodyRepresentation"]' in graph_projector_source
        and 'properties["authoringBoundary"]' in graph_projector_source
        and "ValidateInvocationBoundaryProperties" in graph_json_source
        and "ValidateInvocationBoundary" in authoring_surface_json
        and "PipelineProjectsOpaqueHelperAsOneSourceOnlyCallNode" in visual_profile_test
        and ".profile.json" in cli_source
        and "VisualProfileReport Profile" in debug_server_source
        and "validateVisualProfile" in prototype_source
        and "OPAQUE CODE BOUNDARY" in prototype_source
        and "FUnifiedScriptVisualProfileResult" in unreal_authoring_workbench_header
        and "source-only" in unreal_authoring_widget
        and "Source-only C#" in unreal_graph_presentation
        and 'TEXT("authoringBoundary")' in unreal_graph_presentation
        and "Source-only call tooltip retains exact non-editable contract"
        in unreal_authoring_test
        and "unifiedscript.authoring-workspace/v0.5" in unreal_authoring_workbench_source
        and "calleeBodyRepresentation = opaqueCode" in visual_profile_doc
        and "- [x] publish the supported Visual C# profile" in roadmap,
        "M8.5 Visual C# profile, opaque boundary, diagnostic, or authoring projection is incomplete",
    )
    checks.append("M8.5 publishes one versioned Visual C# and source-only opaque boundary")
    require(
        (ROOT / "samples" / "MultiFile" / "GameplayApi.cs").is_file()
        and (ROOT / "samples" / "MultiFile" / "DamageScript.cs").is_file()
        and (ROOT / "samples" / "MultiFile" / "effects.json").is_file(),
        "Missing multi-file semantic sample",
    )
    patch_source_path = ROOT / "samples" / "Patching" / "DamageRules.cs"
    patch_document_path = ROOT / "samples" / "Patching" / "ScaleDamage.patch.json"
    operator_patch_path = ROOT / "samples" / "Patching" / "DivideDamage.patch.json"
    insert_patch_path = ROOT / "samples" / "Patching" / "InsertScale.patch.json"
    temporary_patch_path = ROOT / "samples" / "Patching" / "IntroduceScaledDamage.patch.json"
    patch_identity_path = ROOT / "samples" / "Patching" / "DamageRules.cs.usids"
    reconnect_source_path = ROOT / "samples" / "Patching" / "SharedValueRules.cs"
    reconnect_patch_path = ROOT / "samples" / "Patching" / "ReconnectSharedDamage.patch.json"
    reconnect_identity_path = ROOT / "samples" / "Patching" / "SharedValueRules.cs.usids"
    assignment_source_path = ROOT / "samples" / "Patching" / "AssignmentRules.cs"
    assignment_patch_path = ROOT / "samples" / "Patching" / "IncrementResult.patch.json"
    assignment_identity_path = ROOT / "samples" / "Patching" / "AssignmentRules.cs.usids"
    plan_source_path = ROOT / "samples" / "Patching" / "SharedExpressionPlan.cs"
    plan_document_path = ROOT / "samples" / "Patching" / "ShareDamage.plan.json"
    plan_identity_path = ROOT / "samples" / "Patching" / "SharedExpressionPlan.cs.usids"
    composed_source_path = ROOT / "samples" / "Patching" / "ComposedPlan.cs"
    composed_document_path = ROOT / "samples" / "Patching" / "ComposeScale.plan.json"
    composed_identity_path = ROOT / "samples" / "Patching" / "ComposedPlan.cs.usids"
    guard_source_path = ROOT / "samples" / "Patching" / "GuardRules.cs"
    guard_document_path = ROOT / "samples" / "Patching" / "InsertGuard.patch.json"
    guard_plan_path = ROOT / "samples" / "Patching" / "PopulateGuard.plan.json"
    guard_return_plan_path = ROOT / "samples" / "Patching" / "ReturnGuard.plan.json"
    guard_identity_path = ROOT / "samples" / "Patching" / "GuardRules.cs.usids"
    nested_source_path = ROOT / "samples" / "Patching" / "NestedDecisionRules.cs"
    nested_plan_path = ROOT / "samples" / "Patching" / "BuildNestedDecision.plan.json"
    nested_identity_path = ROOT / "samples" / "Patching" / "NestedDecisionRules.cs.usids"
    loop_plan_source_path = ROOT / "samples" / "Patching" / "LoopBuildRules.cs"
    loop_plan_path = ROOT / "samples" / "Patching" / "BuildLoop.plan.json"
    loop_plan_identity_path = ROOT / "samples" / "Patching" / "LoopBuildRules.cs.usids"
    for_build_source_path = ROOT / "samples" / "Patching" / "ForBuildRules.cs"
    for_build_plan_path = ROOT / "samples" / "Patching" / "BuildFor.plan.json"
    for_build_identity_path = ROOT / "samples" / "Patching" / "ForBuildRules.cs.usids"
    do_while_build_source_path = ROOT / "samples" / "Patching" / "DoWhileBuildRules.cs"
    do_while_build_plan_path = ROOT / "samples" / "Patching" / "BuildDoWhile.plan.json"
    do_while_build_identity_path = (
        ROOT / "samples" / "Patching" / "DoWhileBuildRules.cs.usids"
    )
    for_each_build_source_path = ROOT / "samples" / "Patching" / "ForEachBuildRules.cs"
    for_each_build_plan_path = ROOT / "samples" / "Patching" / "BuildForEach.plan.json"
    for_each_build_identity_path = (
        ROOT / "samples" / "Patching" / "ForEachBuildRules.cs.usids"
    )
    enumerable_for_each_build_source_path = (
        ROOT / "samples" / "Patching" / "EnumerableForEachBuildRules.cs"
    )
    enumerable_for_each_build_plan_path = (
        ROOT / "samples" / "Patching" / "BuildEnumerableForEach.plan.json"
    )
    enumerable_for_each_build_identity_path = (
        ROOT / "samples" / "Patching" / "EnumerableForEachBuildRules.cs.usids"
    )
    for_edit_source_path = ROOT / "samples" / "Patching" / "EditForRules.cs"
    for_edit_plan_path = ROOT / "samples" / "Patching" / "EditForRules.plan.json"
    for_edit_identity_path = ROOT / "samples" / "Patching" / "EditForRules.cs.usids"
    expression_edit_source_path = ROOT / "samples" / "Patching" / "EditLoopRules.cs"
    expression_edit_plan_path = (
        ROOT / "samples" / "Patching" / "EditLoopRules.plan.json"
    )
    expression_edit_identity_path = (
        ROOT / "samples" / "Patching" / "EditLoopRules.cs.usids"
    )
    general_expression_source_path = (
        ROOT / "samples" / "Patching" / "EditDecisionValues.cs"
    )
    general_expression_plan_path = (
        ROOT / "samples" / "Patching" / "EditDecisionValues.plan.json"
    )
    general_expression_identity_path = (
        ROOT / "samples" / "Patching" / "EditDecisionValues.cs.usids"
    )
    produced_value_source_path = (
        ROOT / "samples" / "Patching" / "ConnectGeneratedValues.cs"
    )
    produced_value_plan_path = (
        ROOT / "samples" / "Patching" / "ConnectGeneratedValues.plan.json"
    )
    produced_value_identity_path = (
        ROOT / "samples" / "Patching" / "ConnectGeneratedValues.cs.usids"
    )
    nested_input_source_path = (
        ROOT / "samples" / "Patching" / "EditNestedInput.cs"
    )
    nested_input_plan_path = (
        ROOT / "samples" / "Patching" / "EditNestedInput.plan.json"
    )
    nested_input_identity_path = (
        ROOT / "samples" / "Patching" / "EditNestedInput.cs.usids"
    )
    generated_expression_source_path = (
        ROOT / "samples" / "Patching" / "EditGeneratedExpression.cs"
    )
    generated_expression_plan_path = (
        ROOT / "samples" / "Patching" / "EditGeneratedExpression.plan.json"
    )
    generated_expression_identity_path = (
        ROOT / "samples" / "Patching" / "EditGeneratedExpression.cs.usids"
    )
    conditional_expression_source_path = (
        ROOT / "samples" / "Patching" / "EditConditionalExpression.cs"
    )
    conditional_expression_plan_path = (
        ROOT / "samples" / "Patching" / "EditConditionalExpression.plan.json"
    )
    conditional_expression_identity_path = (
        ROOT / "samples" / "Patching" / "EditConditionalExpression.cs.usids"
    )
    short_circuit_source_path = (
        ROOT / "samples" / "Expressions" / "ShortCircuitRules.cs"
    )
    coalesce_source_path = ROOT / "samples" / "Expressions" / "CoalesceRules.cs"
    null_conditional_source_path = (
        ROOT / "samples" / "Expressions" / "NullConditionalRules.cs"
    )
    region_source_path = ROOT / "samples" / "Regions" / "EarlyReturn.cs"
    loop_source_path = ROOT / "samples" / "Loops" / "Countdown.cs"
    loop_identity_path = ROOT / "samples" / "Loops" / "Countdown.cs.usids"
    for_source_path = ROOT / "samples" / "Loops" / "ForRules.cs"
    do_while_source_path = ROOT / "samples" / "Loops" / "DoWhileRules.cs"
    for_each_source_path = ROOT / "samples" / "Loops" / "ForEachRules.cs"
    enumerable_for_each_source_path = (
        ROOT / "samples" / "Loops" / "EnumerableForEachRules.cs"
    )
    golden_manifest_path = (
        ROOT
        / "tests"
        / "UnifiedScript.Tests"
        / "Fixtures"
        / "GoldenVisual.golden.json"
    )
    layout_path = ROOT / "samples" / "Layout" / "DamageRules.usgraph"
    require(
        patch_source_path.is_file()
        and patch_document_path.is_file()
        and operator_patch_path.is_file()
        and insert_patch_path.is_file()
        and temporary_patch_path.is_file()
        and patch_identity_path.is_file(),
        "Missing bidirectional patch sample",
    )
    require(
        reconnect_source_path.is_file()
        and reconnect_patch_path.is_file()
        and reconnect_identity_path.is_file(),
        "Missing typed value-reconnection sample",
    )
    require(
        assignment_source_path.is_file()
        and assignment_patch_path.is_file()
        and assignment_identity_path.is_file(),
        "Missing CFG-checked local-assignment sample",
    )
    require(
        plan_source_path.is_file()
        and plan_document_path.is_file()
        and plan_identity_path.is_file(),
        "Missing atomic semantic patch-plan sample",
    )
    require(
        composed_source_path.is_file()
        and composed_document_path.is_file()
        and composed_identity_path.is_file(),
        "Missing composed semantic patch-plan sample",
    )
    require(
        guard_source_path.is_file()
        and guard_document_path.is_file()
        and guard_plan_path.is_file()
        and guard_return_plan_path.is_file()
        and guard_identity_path.is_file(),
        "Missing typed branch-insertion or branch-population sample",
    )
    require(
        nested_source_path.is_file()
        and nested_plan_path.is_file()
        and nested_identity_path.is_file(),
        "Missing recursive nested-branch plan sample",
    )
    require(
        loop_plan_source_path.is_file()
        and loop_plan_path.is_file()
        and loop_plan_identity_path.is_file(),
        "Missing graph-authored loop plan sample",
    )
    require(
        for_build_source_path.is_file()
        and for_build_plan_path.is_file()
        and for_build_identity_path.is_file(),
        "Missing graph-authored for-loop plan sample",
    )
    require(
        do_while_build_source_path.is_file()
        and do_while_build_plan_path.is_file()
        and do_while_build_identity_path.is_file(),
        "Missing graph-authored do-while plan sample",
    )
    require(
        for_each_build_source_path.is_file()
        and for_each_build_plan_path.is_file()
        and for_each_build_identity_path.is_file(),
        "Missing graph-authored foreach plan sample",
    )
    require(
        enumerable_for_each_build_source_path.is_file()
        and enumerable_for_each_build_plan_path.is_file()
        and enumerable_for_each_build_identity_path.is_file(),
        "Missing graph-authored enumerable foreach plan sample",
    )
    require(
        for_edit_source_path.is_file()
        and for_edit_plan_path.is_file()
        and for_edit_identity_path.is_file(),
        "Missing exact existing-for header edit sample",
    )
    require(
        expression_edit_source_path.is_file()
        and expression_edit_plan_path.is_file()
        and expression_edit_identity_path.is_file(),
        "Missing structured expression-edit plan sample",
    )
    require(
        general_expression_source_path.is_file()
        and general_expression_plan_path.is_file()
        and general_expression_identity_path.is_file(),
        "Missing generalized expression-endpoint edit sample",
    )
    require(
        produced_value_source_path.is_file()
        and produced_value_plan_path.is_file()
        and produced_value_identity_path.is_file(),
        "Missing produced-value expression reference sample",
    )
    require(
        nested_input_source_path.is_file()
        and nested_input_plan_path.is_file()
        and nested_input_identity_path.is_file(),
        "Missing nested expression-input edit sample",
    )
    require(
        generated_expression_source_path.is_file()
        and generated_expression_plan_path.is_file()
        and generated_expression_identity_path.is_file(),
        "Missing produced-expression handle sample",
    )
    require(
        conditional_expression_source_path.is_file()
        and conditional_expression_plan_path.is_file()
        and conditional_expression_identity_path.is_file(),
        "Missing conditional-expression round-trip sample",
    )
    require(short_circuit_source_path.is_file(), "Missing short-circuit sample")
    require(coalesce_source_path.is_file(), "Missing null-coalescing sample")
    require(
        null_conditional_source_path.is_file(),
        "Missing null-conditional access sample",
    )
    require(region_source_path.is_file(), "Missing structured Graph-region sample")
    require(
        loop_source_path.is_file() and loop_identity_path.is_file(),
        "Missing structured while-loop sample or identity sidecar",
    )
    require(for_source_path.is_file(), "Missing lossless for-loop sample")
    require(do_while_source_path.is_file(), "Missing lossless do-while sample")
    require(for_each_source_path.is_file(), "Missing indexed foreach sample")
    require(
        enumerable_for_each_source_path.is_file(),
        "Missing disposal-aware enumerable foreach sample",
    )
    require(golden_manifest_path.is_file(), "Missing Golden artifact manifest")
    require(layout_path.is_file(), "Missing graph layout sidecar sample")
    patch_source = patch_source_path.read_text(encoding="utf-8")
    patch_document = json.loads(patch_document_path.read_text(encoding="utf-8"))
    operator_patch = json.loads(operator_patch_path.read_text(encoding="utf-8"))
    insert_patch = json.loads(insert_patch_path.read_text(encoding="utf-8"))
    temporary_patch = json.loads(temporary_patch_path.read_text(encoding="utf-8"))
    patch_identity = json.loads(patch_identity_path.read_text(encoding="utf-8"))
    reconnect_source = reconnect_source_path.read_text(encoding="utf-8")
    reconnect_patch = json.loads(reconnect_patch_path.read_text(encoding="utf-8"))
    reconnect_identity = json.loads(reconnect_identity_path.read_text(encoding="utf-8"))
    assignment_source = assignment_source_path.read_text(encoding="utf-8")
    assignment_patch = json.loads(assignment_patch_path.read_text(encoding="utf-8"))
    assignment_identity = json.loads(assignment_identity_path.read_text(encoding="utf-8"))
    plan_source = plan_source_path.read_text(encoding="utf-8")
    plan_document = json.loads(plan_document_path.read_text(encoding="utf-8"))
    plan_identity = json.loads(plan_identity_path.read_text(encoding="utf-8"))
    composed_source = composed_source_path.read_text(encoding="utf-8")
    composed_document = json.loads(composed_document_path.read_text(encoding="utf-8"))
    composed_identity = json.loads(composed_identity_path.read_text(encoding="utf-8"))
    guard_source = guard_source_path.read_text(encoding="utf-8")
    guard_document = json.loads(guard_document_path.read_text(encoding="utf-8"))
    guard_plan = json.loads(guard_plan_path.read_text(encoding="utf-8"))
    guard_return_plan = json.loads(guard_return_plan_path.read_text(encoding="utf-8"))
    guard_identity = json.loads(guard_identity_path.read_text(encoding="utf-8"))
    nested_source = nested_source_path.read_text(encoding="utf-8")
    nested_plan = json.loads(nested_plan_path.read_text(encoding="utf-8"))
    nested_identity = json.loads(nested_identity_path.read_text(encoding="utf-8"))
    loop_plan_source = loop_plan_source_path.read_text(encoding="utf-8")
    loop_plan = json.loads(loop_plan_path.read_text(encoding="utf-8"))
    loop_plan_identity = json.loads(
        loop_plan_identity_path.read_text(encoding="utf-8")
    )
    for_build_source = for_build_source_path.read_text(encoding="utf-8")
    for_build_plan = json.loads(for_build_plan_path.read_text(encoding="utf-8"))
    for_build_identity = json.loads(
        for_build_identity_path.read_text(encoding="utf-8")
    )
    do_while_build_source = do_while_build_source_path.read_text(encoding="utf-8")
    do_while_build_plan = json.loads(
        do_while_build_plan_path.read_text(encoding="utf-8")
    )
    do_while_build_identity = json.loads(
        do_while_build_identity_path.read_text(encoding="utf-8")
    )
    for_each_build_source = for_each_build_source_path.read_text(encoding="utf-8")
    for_each_build_plan = json.loads(
        for_each_build_plan_path.read_text(encoding="utf-8")
    )
    for_each_build_identity = json.loads(
        for_each_build_identity_path.read_text(encoding="utf-8")
    )
    enumerable_for_each_build_source = (
        enumerable_for_each_build_source_path.read_text(encoding="utf-8")
    )
    enumerable_for_each_build_plan = json.loads(
        enumerable_for_each_build_plan_path.read_text(encoding="utf-8")
    )
    enumerable_for_each_build_identity = json.loads(
        enumerable_for_each_build_identity_path.read_text(encoding="utf-8")
    )
    for_edit_source = for_edit_source_path.read_text(encoding="utf-8")
    for_edit_plan = json.loads(for_edit_plan_path.read_text(encoding="utf-8"))
    for_edit_identity = json.loads(
        for_edit_identity_path.read_text(encoding="utf-8")
    )
    expression_edit_source = expression_edit_source_path.read_text(encoding="utf-8")
    expression_edit_plan = json.loads(
        expression_edit_plan_path.read_text(encoding="utf-8")
    )
    expression_edit_identity = json.loads(
        expression_edit_identity_path.read_text(encoding="utf-8")
    )
    general_expression_source = general_expression_source_path.read_text(
        encoding="utf-8"
    )
    general_expression_plan = json.loads(
        general_expression_plan_path.read_text(encoding="utf-8")
    )
    general_expression_identity = json.loads(
        general_expression_identity_path.read_text(encoding="utf-8")
    )
    produced_value_source = produced_value_source_path.read_text(encoding="utf-8")
    produced_value_plan = json.loads(
        produced_value_plan_path.read_text(encoding="utf-8")
    )
    produced_value_identity = json.loads(
        produced_value_identity_path.read_text(encoding="utf-8")
    )
    nested_input_source = nested_input_source_path.read_text(encoding="utf-8")
    nested_input_plan = json.loads(
        nested_input_plan_path.read_text(encoding="utf-8")
    )
    nested_input_identity = json.loads(
        nested_input_identity_path.read_text(encoding="utf-8")
    )
    generated_expression_source = generated_expression_source_path.read_text(
        encoding="utf-8"
    )
    generated_expression_plan = json.loads(
        generated_expression_plan_path.read_text(encoding="utf-8")
    )
    generated_expression_identity = json.loads(
        generated_expression_identity_path.read_text(encoding="utf-8")
    )
    conditional_expression_source = conditional_expression_source_path.read_text(
        encoding="utf-8"
    )
    conditional_expression_plan = json.loads(
        conditional_expression_plan_path.read_text(encoding="utf-8")
    )
    conditional_expression_identity = json.loads(
        conditional_expression_identity_path.read_text(encoding="utf-8")
    )
    short_circuit_source = short_circuit_source_path.read_text(encoding="utf-8")
    coalesce_source = coalesce_source_path.read_text(encoding="utf-8")
    null_conditional_source = null_conditional_source_path.read_text(encoding="utf-8")
    region_source = region_source_path.read_text(encoding="utf-8")
    loop_source = loop_source_path.read_text(encoding="utf-8")
    loop_identity = json.loads(loop_identity_path.read_text(encoding="utf-8"))
    for_source = for_source_path.read_text(encoding="utf-8")
    do_while_source = do_while_source_path.read_text(encoding="utf-8")
    for_each_source = for_each_source_path.read_text(encoding="utf-8")
    enumerable_for_each_source = enumerable_for_each_source_path.read_text(
        encoding="utf-8"
    )
    golden_manifest = json.loads(golden_manifest_path.read_text(encoding="utf-8"))
    layout = json.loads(layout_path.read_text(encoding="utf-8"))
    patch_operation = patch_document["operations"][0]
    operator_operation = operator_patch["operations"][0]
    insert_operation = insert_patch["operations"][0]
    temporary_operation = temporary_patch["operations"][0]
    reconnect_operation = reconnect_patch["operations"][0]
    assignment_operation = assignment_patch["operations"][0]
    identity_ids = {entry["id"] for entry in patch_identity["entries"]}
    reconnect_identity_ids = {entry["id"] for entry in reconnect_identity["entries"]}
    source_digest = hashlib.sha256(patch_source.encode("utf-8")).hexdigest()
    reconnect_source_digest = hashlib.sha256(reconnect_source.encode("utf-8")).hexdigest()
    assignment_source_digest = hashlib.sha256(assignment_source.encode("utf-8")).hexdigest()
    plan_source_digest = hashlib.sha256(plan_source.encode("utf-8")).hexdigest()
    composed_source_digest = hashlib.sha256(composed_source.encode("utf-8")).hexdigest()
    guard_source_digest = hashlib.sha256(guard_source.encode("utf-8")).hexdigest()
    nested_source_digest = hashlib.sha256(nested_source.encode("utf-8")).hexdigest()
    loop_plan_source_digest = hashlib.sha256(
        loop_plan_source.encode("utf-8")
    ).hexdigest()
    for_build_source_digest = hashlib.sha256(
        for_build_source.encode("utf-8")
    ).hexdigest()
    do_while_build_source_digest = hashlib.sha256(
        do_while_build_source.encode("utf-8")
    ).hexdigest()
    for_each_build_source_digest = hashlib.sha256(
        for_each_build_source.encode("utf-8")
    ).hexdigest()
    enumerable_for_each_build_source_digest = hashlib.sha256(
        enumerable_for_each_build_source.encode("utf-8")
    ).hexdigest()
    for_edit_source_digest = hashlib.sha256(
        for_edit_source.encode("utf-8")
    ).hexdigest()
    expression_edit_source_digest = hashlib.sha256(
        expression_edit_source.encode("utf-8")
    ).hexdigest()
    general_expression_source_digest = hashlib.sha256(
        general_expression_source.encode("utf-8")
    ).hexdigest()
    produced_value_source_digest = hashlib.sha256(
        produced_value_source.encode("utf-8")
    ).hexdigest()
    nested_input_source_digest = hashlib.sha256(
        nested_input_source.encode("utf-8")
    ).hexdigest()
    generated_expression_source_digest = hashlib.sha256(
        generated_expression_source.encode("utf-8")
    ).hexdigest()
    conditional_expression_source_digest = hashlib.sha256(
        conditional_expression_source.encode("utf-8")
    ).hexdigest()
    loop_source_digest = hashlib.sha256(loop_source.encode("utf-8")).hexdigest()
    require(
        patch_document["schemaVersion"] == "unifiedscript.patch/v0.28"
        and operator_patch["schemaVersion"] == "unifiedscript.patch/v0.28"
        and insert_patch["schemaVersion"] == "unifiedscript.patch/v0.28"
        and temporary_patch["schemaVersion"] == "unifiedscript.patch/v0.28"
        and operator_patch["sourceCompilationId"]
        == patch_document["sourceCompilationId"]
        and insert_patch["sourceCompilationId"]
        == patch_document["sourceCompilationId"]
        and temporary_patch["sourceCompilationId"]
        == patch_document["sourceCompilationId"]
        and operator_patch["semanticConfigurationId"]
        == patch_document["semanticConfigurationId"]
        and insert_patch["semanticConfigurationId"]
        == patch_document["semanticConfigurationId"]
        and temporary_patch["semanticConfigurationId"]
        == patch_document["semanticConfigurationId"]
        and patch_identity["sourcePath"] == "samples/Patching/DamageRules.cs"
        and patch_identity["sourceDigest"] == f"sha256:{source_digest}"
        and patch_operation["operation"] == "replaceLiteral"
        and operator_operation["operation"] == "changeOperator"
        and insert_operation["operation"] == "insertStatement"
        and temporary_operation["operation"] == "introduceTemporary"
        and patch_operation["targetNodeId"] in identity_ids
        and operator_operation["targetNodeId"] in identity_ids
        and insert_operation["targetNodeId"] in identity_ids
        and insert_operation["expectedNextSiblingId"] in identity_ids
        and temporary_operation["targetNodeId"] in identity_ids
        and temporary_operation["expectedContainingStatementId"] in identity_ids
        and temporary_operation["expectedParentBlockId"] in identity_ids
        and patch_operation["expectedValueText"] in patch_source
        and operator_operation["expectedOperator"] in patch_source
        and operator_operation["expectedOperator"] != operator_operation["newOperator"],
        "Bidirectional patch sample source, sidecar, and patch are inconsistent",
    )
    inserted_statement = insert_operation["newStatement"]
    require(
        inserted_statement["statement"] == "localLiteralDeclaration"
        and inserted_statement["name"] == "scale"
        and inserted_statement["typeName"] == "float"
        and inserted_statement["valueText"] == "2.0f"
        and inserted_statement["expectedTypeId"].startswith("us_"),
        "Statement insertion sample does not contain the expected typed payload",
    )
    require(
        temporary_operation["name"] == "scaledDamage"
        and temporary_operation["typeName"] == "float"
        and temporary_operation["expectedTypeId"]
        == inserted_statement["expectedTypeId"],
        "Temporary-introduction sample does not contain the expected typed payload",
    )
    require(
        reconnect_patch["schemaVersion"] == "unifiedscript.patch/v0.28"
        and reconnect_identity["sourcePath"]
        == "samples/Patching/SharedValueRules.cs"
        and reconnect_identity["sourceDigest"]
        == f"sha256:{reconnect_source_digest}"
        and reconnect_operation["operation"] == "reconnectValue"
        and reconnect_operation["targetNodeId"] in reconnect_identity_ids
        and reconnect_operation["sourceDefinitionId"] in reconnect_identity_ids
        and reconnect_operation["expectedContainingStatementId"]
        in reconnect_identity_ids
        and reconnect_operation["expectedParentBlockId"] in reconnect_identity_ids
        and reconnect_operation["expectedPreviousSiblingId"]
        == reconnect_operation["sourceDefinitionId"]
        and reconnect_operation["expectedNextSiblingId"] is None
        and reconnect_operation["expectedTargetTypeId"]
        == reconnect_operation["expectedSourceTypeId"]
        and reconnect_operation["expectedTargetTypeId"].startswith("us_")
        and "float sharedDamage = damage * 2.0f;" in reconnect_source
        and "return bonus + 1.0f;" in reconnect_source,
        "Typed value-reconnection source, sidecar, and patch are inconsistent",
    )
    assignment_identity_ids = {entry["id"] for entry in assignment_identity["entries"]}
    assignment_statement = assignment_operation["newStatement"]
    assignment_value = assignment_statement["value"]
    require(
        assignment_patch["schemaVersion"] == "unifiedscript.patch/v0.28"
        and assignment_identity["sourcePath"]
        == "samples/Patching/AssignmentRules.cs"
        and assignment_identity["sourceDigest"]
        == f"sha256:{assignment_source_digest}"
        and assignment_operation["operation"] == "insertStatement"
        and assignment_operation["targetNodeId"] in assignment_identity_ids
        and assignment_operation["expectedPreviousSiblingId"]
        in assignment_identity_ids
        and assignment_operation["expectedNextSiblingId"] in assignment_identity_ids
        and assignment_statement["statement"] == "localAssignment"
        and assignment_statement["targetDefinitionId"]
        == assignment_operation["expectedPreviousSiblingId"]
        and assignment_statement["targetDefinitionId"] in assignment_identity_ids
        and assignment_statement["expectedName"] == "result"
        and assignment_value["expression"] == "binary"
        and assignment_value["operator"] == "+"
        and assignment_value["left"]["sourceDefinitionId"]
        == assignment_statement["targetDefinitionId"]
        and assignment_value["expectedTypeId"]
        == assignment_statement["expectedTypeId"]
        and "int result = seed;" in assignment_source
        and "return result;" in assignment_source,
        "CFG-checked local-assignment source, sidecar, and patch are inconsistent",
    )
    plan_steps = plan_document["steps"]
    plan_identity_ids = {entry["id"] for entry in plan_identity["entries"]}
    plan_step_ids = {step["stepId"] for step in plan_steps}
    introduction_step = plan_steps[0]
    reconnect_steps = plan_steps[1:]
    require(
        plan_document["schemaVersion"] == "unifiedscript.patch-plan/v0.21"
        and plan_identity["sourcePath"]
        == "samples/Patching/SharedExpressionPlan.cs"
        and plan_identity["sourceDigest"] == f"sha256:{plan_source_digest}"
        and len(plan_steps) == 3
        and len(plan_step_ids) == len(plan_steps)
        and introduction_step["step"] == "introduceTemporary"
        and introduction_step["stepId"] == "sharedDamage"
        and introduction_step["targetNodeId"] in plan_identity_ids
        and introduction_step["expectedContainingStatementId"] in plan_identity_ids
        and introduction_step["expectedParentBlockId"] in plan_identity_ids
        and all(step["step"] == "reconnectValue" for step in reconnect_steps)
        and all(step["targetNodeId"] in plan_identity_ids for step in reconnect_steps)
        and all(step["sourceStepId"] == introduction_step["stepId"] for step in reconnect_steps)
        and all(
            step["expectedContainingStatementId"]
            == introduction_step["expectedContainingStatementId"]
            for step in reconnect_steps
        )
        and all(
            step["expectedParentBlockId"]
            == introduction_step["expectedParentBlockId"]
            for step in reconnect_steps
        )
        and all(
            step["expectedTypeId"] == introduction_step["expectedTypeId"]
            for step in reconnect_steps
        )
        and "return damage * 2.0f + damage * 3.0f + 1.0f;" in plan_source,
        "Atomic semantic patch-plan source, sidecar, and steps are inconsistent",
    )
    composed_steps = composed_document["steps"]
    composed_identity_ids = {entry["id"] for entry in composed_identity["entries"]}
    composed_insert, composed_reconnect, composed_operator, composed_delete = composed_steps
    require(
        composed_document["schemaVersion"] == "unifiedscript.patch-plan/v0.21"
        and composed_identity["sourcePath"] == "samples/Patching/ComposedPlan.cs"
        and composed_identity["sourceDigest"] == f"sha256:{composed_source_digest}"
        and [step["step"] for step in composed_steps]
        == ["insertStatement", "reconnectValue", "changeOperator", "deleteStatement"]
        and len({step["stepId"] for step in composed_steps}) == len(composed_steps)
        and composed_insert["targetNodeId"] in composed_identity_ids
        and composed_insert["expectedNextSiblingId"] in composed_identity_ids
        and composed_insert["newStatement"]["statement"] == "localLiteralDeclaration"
        and composed_insert["newStatement"]["name"] == "scale"
        and composed_reconnect["targetNodeId"] in composed_identity_ids
        and composed_reconnect["sourceStepId"] == composed_insert["stepId"]
        and composed_reconnect["expectedTypeId"]
        == composed_insert["newStatement"]["expectedTypeId"]
        and composed_operator["targetNodeId"] in composed_identity_ids
        and composed_operator["expectedOperator"] == "*"
        and composed_operator["newOperator"] == "/"
        and composed_delete["targetNodeId"] in composed_identity_ids
        and composed_delete["expectedParentBlockId"] == composed_insert["targetNodeId"]
        and "float obsolete = 1.0f;" in composed_source
        and "return damage * 2.0f;" in composed_source,
        "Composed semantic patch-plan source, sidecar, and steps are inconsistent",
    )
    guard_operation = guard_document["operations"][0]
    guard_statement = guard_operation["newStatement"]
    guard_condition = guard_statement["condition"]
    guard_identity_ids = {entry["id"] for entry in guard_identity["entries"]}
    guard_comparison = guard_condition["left"]
    guard_negation = guard_condition["right"]
    require(
        guard_document["schemaVersion"] == "unifiedscript.patch/v0.28"
        and guard_identity["sourcePath"] == "samples/Patching/GuardRules.cs"
        and guard_identity["sourceDigest"] == f"sha256:{guard_source_digest}"
        and guard_operation["operation"] == "insertStatement"
        and guard_operation["targetNodeId"] in guard_identity_ids
        and guard_operation["expectedNextSiblingId"] in guard_identity_ids
        and guard_statement["statement"] == "if"
        and guard_condition["expression"] == "binary"
        and guard_condition["operator"] == "&&"
        and guard_comparison["expression"] == "binary"
        and guard_comparison["operator"] == ">"
        and guard_negation["expression"] == "unary"
        and guard_negation["operator"] == "!"
        and guard_comparison["left"]["sourceDefinitionId"] in guard_identity_ids
        and guard_comparison["right"]["sourceDefinitionId"] in guard_identity_ids
        and guard_negation["operand"]["sourceDefinitionId"] in guard_identity_ids
        and "int damage" in guard_source
        and "int threshold" in guard_source
        and "bool disabled" in guard_source,
        "Typed branch source, sidecar, and patch are inconsistent",
    )
    guard_plan_steps = guard_plan["steps"]
    guard_plan_source, *guard_branch_steps = guard_plan_steps
    require(
        guard_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.21"
        and guard_plan["sourceCompilationId"] == guard_document["sourceCompilationId"]
        and guard_plan["semanticConfigurationId"]
        == guard_document["semanticConfigurationId"]
        and guard_plan_source["step"] == "insertStatement"
        and guard_plan_source["stepId"] == "insertGuard"
        and guard_plan_source["targetNodeId"] == guard_operation["targetNodeId"]
        and guard_plan_source["newStatement"] == guard_statement
        and len(guard_branch_steps) == 3
        and all(step["step"] == "insertBranchStatement" for step in guard_branch_steps)
        and all(step["sourceStepId"] == guard_plan_source["stepId"] for step in guard_branch_steps)
        and all(step["targetNodeId"] == guard_plan_source["targetNodeId"] for step in guard_branch_steps)
        and [step["branch"] for step in guard_branch_steps] == ["then", "then", "else"]
        and all(
            step["newStatement"]["statement"] == "localLiteralDeclaration"
            for step in guard_branch_steps
        ),
        "Atomic branch-population plan is inconsistent with the branch sample",
    )
    guard_return_source, guard_return_step, guard_fallback_step = guard_return_plan["steps"]
    require(
        guard_return_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.21"
        and guard_return_plan["sourceCompilationId"] == guard_document["sourceCompilationId"]
        and guard_return_plan["semanticConfigurationId"]
        == guard_document["semanticConfigurationId"]
        and guard_return_source["newStatement"] == guard_statement
        and guard_return_step["step"] == "insertBranchStatement"
        and guard_return_step["sourceStepId"] == guard_return_source["stepId"]
        and guard_return_step["branch"] == "then"
        and guard_return_step["newStatement"]["statement"] == "return"
        and guard_return_step["newStatement"]["expression"]["valueText"] == "true"
        and guard_return_step["newStatement"]["expression"]["expectedTypeId"]
        == guard_return_step["newStatement"]["expectedReturnTypeId"]
        and guard_fallback_step["step"] == "replaceLiteral"
        and guard_fallback_step["targetNodeId"] in guard_identity_ids
        and guard_fallback_step["expectedValueText"] == "true"
        and guard_fallback_step["newValueText"] == "false",
        "Typed early-return patch plan is inconsistent with the branch sample",
    )
    nested_identity_ids = {entry["id"] for entry in nested_identity["entries"]}
    nested_steps = nested_plan["steps"]
    nested_outer, nested_inner, nested_then, nested_else, nested_outer_else = nested_steps
    require(
        nested_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.21"
        and nested_identity["sourcePath"]
        == "samples/Patching/NestedDecisionRules.cs"
        and nested_identity["sourceDigest"] == f"sha256:{nested_source_digest}"
        and nested_identity["tombstones"] == []
        and len(nested_steps) == 5
        and len({step["stepId"] for step in nested_steps}) == len(nested_steps)
        and nested_outer["step"] == "insertStatement"
        and nested_outer["targetNodeId"] in nested_identity_ids
        and nested_outer["expectedPreviousSiblingId"] in nested_identity_ids
        and nested_outer["expectedNextSiblingId"] in nested_identity_ids
        and nested_outer["newStatement"]["statement"] == "if"
        and nested_outer["newStatement"]["condition"]["expectedName"] == "enabled"
        and nested_inner["step"] == "insertBranchStatement"
        and nested_inner["sourceStepId"] == nested_outer["stepId"]
        and nested_inner["branch"] == "then"
        and nested_inner["newStatement"]["statement"] == "if"
        and nested_inner["newStatement"]["condition"]["expectedName"] == "critical"
        and nested_then["sourceStepId"] == nested_inner["stepId"]
        and nested_else["sourceStepId"] == nested_inner["stepId"]
        and [nested_then["branch"], nested_else["branch"]] == ["then", "else"]
        and nested_outer_else["sourceStepId"] == nested_outer["stepId"]
        and nested_outer_else["branch"] == "else"
        and all(
            step["newStatement"]["statement"] == "localAssignment"
            for step in (nested_then, nested_else, nested_outer_else)
        )
        and all(
            step["newStatement"]["targetDefinitionId"] in nested_identity_ids
            for step in (nested_then, nested_else, nested_outer_else)
        )
        and "bool enabled" in nested_source
        and "bool critical" in nested_source
        and "int result = 0;" in nested_source,
        "Recursive nested-branch plan source, sidecar, and handles are inconsistent",
    )
    loop_plan_identity_ids = {
        entry["id"] for entry in loop_plan_identity["entries"]
    }
    loop_plan_steps = loop_plan["steps"]
    loop_producer, loop_guard, loop_break, loop_assignment, loop_continue = (
        loop_plan_steps
    )
    require(
        loop_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.21"
        and loop_plan_identity["sourcePath"]
        == "samples/Patching/LoopBuildRules.cs"
        and loop_plan_identity["sourceDigest"]
        == f"sha256:{loop_plan_source_digest}"
        and loop_plan_identity["generation"] == 1
        and loop_plan_identity["tombstones"] == []
        and len(loop_plan_steps) == 5
        and len({step["stepId"] for step in loop_plan_steps})
        == len(loop_plan_steps)
        and loop_producer["step"] == "insertStatement"
        and loop_producer["targetNodeId"] in loop_plan_identity_ids
        and loop_producer["expectedPreviousSiblingId"] in loop_plan_identity_ids
        and loop_producer["expectedNextSiblingId"] in loop_plan_identity_ids
        and loop_producer["newStatement"]["statement"] == "while"
        and loop_producer["newStatement"]["condition"]["operator"] == "&&"
        and loop_guard["step"] == "insertLoopBodyStatement"
        and loop_guard["sourceStepId"] == loop_producer["stepId"]
        and loop_guard["newStatement"]["statement"] == "if"
        and loop_break["step"] == "insertBranchStatement"
        and loop_break["sourceStepId"] == loop_guard["stepId"]
        and loop_break["branch"] == "then"
        and loop_break["newStatement"]["statement"] == "break"
        and loop_assignment["step"] == "insertLoopBodyStatement"
        and loop_assignment["sourceStepId"] == loop_producer["stepId"]
        and loop_assignment["newStatement"]["statement"] == "localAssignment"
        and loop_assignment["newStatement"]["targetDefinitionId"]
        in loop_plan_identity_ids
        and loop_continue["step"] == "insertLoopBodyStatement"
        and loop_continue["sourceStepId"] == loop_producer["stepId"]
        and loop_continue["newStatement"]["statement"] == "continue"
        and "int result = value;" in loop_plan_source
        and "return result;" in loop_plan_source,
        "Graph-authored loop plan source, sidecar, and symbolic handles are inconsistent",
    )
    do_while_identity_ids = {
        entry["id"] for entry in do_while_build_identity["entries"]
    }
    do_producer, do_condition_edit, do_assignment = do_while_build_plan["steps"]
    require(
        do_while_build_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.21"
        and do_while_build_identity["sourcePath"]
        == "samples/Patching/DoWhileBuildRules.cs"
        and do_while_build_identity["sourceDigest"]
        == f"sha256:{do_while_build_source_digest}"
        and do_while_build_identity["generation"] == 1
        and do_while_build_identity["tombstones"] == []
        and len(do_while_build_plan["steps"]) == 3
        and len({step["stepId"] for step in do_while_build_plan["steps"]}) == 3
        and do_producer["step"] == "insertStatement"
        and do_producer["targetNodeId"] in do_while_identity_ids
        and do_producer["expectedPreviousSiblingId"] in do_while_identity_ids
        and do_producer["expectedNextSiblingId"] in do_while_identity_ids
        and do_producer["newStatement"]["statement"] == "doWhile"
        and do_condition_edit["step"] == "replaceProducedDoWhileCondition"
        and do_condition_edit["sourceStepId"] == do_producer["stepId"]
        and do_condition_edit["newCondition"]["operator"] == "&&"
        and do_assignment["step"] == "insertLoopBodyStatement"
        and do_assignment["sourceStepId"] == do_producer["stepId"]
        and do_assignment["newStatement"]["statement"] == "localAssignment"
        and do_assignment["newStatement"]["targetDefinitionId"]
        in do_while_identity_ids
        and "int result = value;" in do_while_build_source
        and "return result;" in do_while_build_source,
        "Graph-authored do-while source, sidecar, and plan are inconsistent",
    )
    for_each_identity_ids = {
        entry["id"] for entry in for_each_build_identity["entries"]
    }
    for_each_producer, for_each_collection_edit, for_each_assignment = (
        for_each_build_plan["steps"]
    )
    for_each_statement = for_each_producer["newStatement"]
    produced_iteration_reference = for_each_assignment["newStatement"]["value"]
    require(
        for_each_build_plan["schemaVersion"]
        == "unifiedscript.patch-plan/v0.21"
        and for_each_build_identity["sourcePath"]
        == "samples/Patching/ForEachBuildRules.cs"
        and for_each_build_identity["sourceDigest"]
        == f"sha256:{for_each_build_source_digest}"
        and for_each_build_identity["generation"] == 1
        and for_each_build_identity["tombstones"] == []
        and len(for_each_build_plan["steps"]) == 3
        and len({step["stepId"] for step in for_each_build_plan["steps"]}) == 3
        and for_each_producer["step"] == "insertStatement"
        and for_each_producer["targetNodeId"] in for_each_identity_ids
        and for_each_producer["expectedPreviousSiblingId"] in for_each_identity_ids
        and for_each_producer["expectedNextSiblingId"] in for_each_identity_ids
        and for_each_statement["statement"] == "forEach"
        and for_each_statement["typeName"] == "char"
        and for_each_statement["variableName"] == "value"
        and for_each_statement["collection"]["expectedName"] == "primary"
        and for_each_statement["collection"]["sourceDefinitionId"]
        in for_each_identity_ids
        and for_each_collection_edit["step"]
        == "replaceProducedForEachCollection"
        and for_each_collection_edit["sourceStepId"]
        == for_each_producer["stepId"]
        and for_each_collection_edit["newCollection"]["expectedName"]
        == "fallback"
        and for_each_collection_edit["newCollection"]["sourceDefinitionId"]
        in for_each_identity_ids
        and for_each_assignment["step"] == "insertLoopBodyStatement"
        and for_each_assignment["sourceStepId"] == for_each_producer["stepId"]
        and for_each_assignment["newStatement"]["statement"] == "localAssignment"
        and for_each_assignment["newStatement"]["targetDefinitionId"]
        in for_each_identity_ids
        and produced_iteration_reference["expression"] == "producedLocalReference"
        and produced_iteration_reference["sourceStepId"]
        == for_each_producer["stepId"]
        and produced_iteration_reference["expectedName"] == "value"
        and produced_iteration_reference["expectedTypeId"]
        == for_each_statement["expectedElementTypeId"]
        and "char result = seed;" in for_each_build_source
        and "return result;" in for_each_build_source,
        "Graph-authored foreach source, sidecar, and plan are inconsistent",
    )
    enumerable_identity_ids = {
        entry["id"] for entry in enumerable_for_each_build_identity["entries"]
    }
    (
        enumerable_producer,
        enumerable_collection_edit,
        enumerable_assignment,
    ) = enumerable_for_each_build_plan["steps"]
    enumerable_statement = enumerable_producer["newStatement"]
    enumerable_iteration_reference = enumerable_assignment["newStatement"]["value"]
    require(
        enumerable_for_each_build_plan["schemaVersion"]
        == "unifiedscript.patch-plan/v0.21"
        and enumerable_for_each_build_identity["sourcePath"]
        == "samples/Patching/EnumerableForEachBuildRules.cs"
        and enumerable_for_each_build_identity["sourceDigest"]
        == f"sha256:{enumerable_for_each_build_source_digest}"
        and enumerable_for_each_build_identity["generation"] == 1
        and enumerable_for_each_build_identity["tombstones"] == []
        and len(enumerable_for_each_build_plan["steps"]) == 3
        and len(
            {step["stepId"] for step in enumerable_for_each_build_plan["steps"]}
        )
        == 3
        and enumerable_producer["step"] == "insertStatement"
        and enumerable_producer["targetNodeId"] in enumerable_identity_ids
        and enumerable_producer["expectedPreviousSiblingId"]
        in enumerable_identity_ids
        and enumerable_producer["expectedNextSiblingId"] in enumerable_identity_ids
        and enumerable_statement["statement"] == "forEach"
        and enumerable_statement["typeName"] == "int"
        and enumerable_statement["variableName"] == "value"
        and enumerable_statement["collection"]["expectedName"] == "primary"
        and enumerable_statement["collection"]["sourceDefinitionId"]
        in enumerable_identity_ids
        and enumerable_collection_edit["step"]
        == "replaceProducedForEachCollection"
        and enumerable_collection_edit["sourceStepId"]
        == enumerable_producer["stepId"]
        and enumerable_collection_edit["newCollection"]["expectedName"]
        == "fallback"
        and enumerable_collection_edit["newCollection"]["sourceDefinitionId"]
        in enumerable_identity_ids
        and enumerable_assignment["step"] == "insertLoopBodyStatement"
        and enumerable_assignment["sourceStepId"] == enumerable_producer["stepId"]
        and enumerable_assignment["newStatement"]["statement"]
        == "localAssignment"
        and enumerable_assignment["newStatement"]["targetDefinitionId"]
        in enumerable_identity_ids
        and enumerable_iteration_reference["expression"]
        == "producedLocalReference"
        and enumerable_iteration_reference["sourceStepId"]
        == enumerable_producer["stepId"]
        and enumerable_iteration_reference["expectedName"] == "value"
        and enumerable_iteration_reference["expectedTypeId"]
        == enumerable_statement["expectedElementTypeId"]
        and "IEnumerable<int> primary" in enumerable_for_each_build_source
        and "int result = seed;" in enumerable_for_each_build_source,
        "Enumerable foreach source, sidecar, and plan are inconsistent",
    )
    for_build_identity_ids = {
        entry["id"] for entry in for_build_identity["entries"]
    }
    for_producer, for_guard, for_continue, for_assignment = for_build_plan["steps"]
    for_statement = for_producer["newStatement"]
    for_variable = for_statement["declaration"]["variables"][0]
    for_condition = for_statement["condition"]
    for_increment = for_statement["incrementors"][0]
    produced_index_references = [
        for_guard["newStatement"]["condition"]["left"],
        for_assignment["newStatement"]["value"]["right"],
    ]
    require(
        for_build_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.21"
        and for_build_identity["sourcePath"]
        == "samples/Patching/ForBuildRules.cs"
        and for_build_identity["sourceDigest"]
        == f"sha256:{for_build_source_digest}"
        and for_build_identity["generation"] == 1
        and for_build_identity["tombstones"] == []
        and len(for_build_plan["steps"]) == 4
        and len({step["stepId"] for step in for_build_plan["steps"]}) == 4
        and for_producer["step"] == "insertStatement"
        and for_producer["targetNodeId"] in for_build_identity_ids
        and for_producer["expectedPreviousSiblingId"] in for_build_identity_ids
        and for_producer["expectedNextSiblingId"] in for_build_identity_ids
        and for_statement["statement"] == "for"
        and for_statement["initializers"] == []
        and for_statement["declaration"]["typeName"] == "int"
        and for_variable["name"] == "index"
        and for_variable["initializer"]["valueText"] == "0"
        and for_condition["operator"] == "<"
        and for_condition["left"]["expression"] == "forLocalReference"
        and for_condition["left"]["variableIndex"] == 0
        and for_condition["right"]["sourceDefinitionId"]
        in for_build_identity_ids
        and for_increment["operand"]["expression"] == "forLocalReference"
        and for_increment["operator"] == "++"
        and for_increment["isPostfix"] is True
        and for_guard["step"] == "insertLoopBodyStatement"
        and for_guard["sourceStepId"] == for_producer["stepId"]
        and for_continue["step"] == "insertBranchStatement"
        and for_continue["sourceStepId"] == for_guard["stepId"]
        and for_continue["newStatement"]["statement"] == "continue"
        and for_assignment["step"] == "insertLoopBodyStatement"
        and for_assignment["sourceStepId"] == for_producer["stepId"]
        and for_assignment["newStatement"]["statement"] == "localAssignment"
        and for_assignment["newStatement"]["targetDefinitionId"]
        in for_build_identity_ids
        and all(
            reference["expression"] == "producedLocalReference"
            and reference["sourceStepId"] == for_producer["stepId"]
            and reference["expectedName"] == "index"
            and reference["expectedTypeId"] == for_variable["expectedTypeId"]
            for reference in produced_index_references
        )
        and "int total = 0;" in for_build_source
        and "return total;" in for_build_source,
        "Graph-authored for-loop source, sidecar, and plan are inconsistent",
    )
    for_edit_identity_ids = {entry["id"] for entry in for_edit_identity["entries"]}
    (
        for_condition_edit,
        for_declaration_edit,
        for_incrementor_edit,
        for_assignment_edit,
    ) = for_edit_plan["steps"]
    require(
        for_edit_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.21"
        and for_edit_identity["sourcePath"] == "samples/Patching/EditForRules.cs"
        and for_edit_identity["sourceDigest"] == f"sha256:{for_edit_source_digest}"
        and for_edit_identity["generation"] == 1
        and for_edit_identity["tombstones"] == []
        and [step["step"] for step in for_edit_plan["steps"]]
        == [
            "replaceForCondition",
            "replaceForDeclarationInitializer",
            "replaceForIncrementor",
            "replaceForAssignmentInitializerValue",
        ]
        and len({step["stepId"] for step in for_edit_plan["steps"]}) == 4
        and all(step["targetNodeId"] in for_edit_identity_ids for step in for_edit_plan["steps"])
        and for_condition_edit["expectedConditionNodeId"] in for_edit_identity_ids
        and for_condition_edit["newCondition"]["operator"] == "&&"
        and for_condition_edit["newCondition"]["left"]["expectedName"] == "enabled"
        and for_condition_edit["newCondition"]["right"]["left"]["expectedName"] == "index"
        and for_declaration_edit["expectedForStatementId"]
        == for_condition_edit["targetNodeId"]
        and for_declaration_edit["expectedInitializerNodeId"] in for_edit_identity_ids
        and for_declaration_edit["newInitializer"]["valueText"] == "1"
        and for_incrementor_edit["expectedForStatementId"]
        == for_condition_edit["targetNodeId"]
        and for_incrementor_edit["incrementorIndex"] == 0
        and for_incrementor_edit["expectedOperator"] == "++"
        and for_incrementor_edit["expectedIsPostfix"] is True
        and for_incrementor_edit["newOperator"] == "++"
        and for_incrementor_edit["newIsPostfix"] is False
        and for_incrementor_edit["newOperand"]["expectedName"] == "index"
        and for_assignment_edit["initializerIndex"] == 0
        and for_assignment_edit["expectedOperator"] == "="
        and for_assignment_edit["expectedTargetDefinitionId"] in for_edit_identity_ids
        and for_assignment_edit["expectedTargetName"] == "cursor"
        and for_assignment_edit["newValue"]["valueText"] == "1"
        and "for (int index = 0; index < count; index++)" in for_edit_source
        and "for (cursor = 0; cursor < count; cursor++)" in for_edit_source,
        "Exact existing-for header source, sidecar, and plan are inconsistent",
    )
    expression_edit_identity_ids = {
        entry["id"] for entry in expression_edit_identity["entries"]
    }
    assignment_edit, condition_edit = expression_edit_plan["steps"]
    condition_payload = condition_edit["newCondition"]
    require(
        expression_edit_plan["schemaVersion"]
        == "unifiedscript.patch-plan/v0.21"
        and expression_edit_identity["sourcePath"]
        == "samples/Patching/EditLoopRules.cs"
        and expression_edit_identity["sourceDigest"]
        == f"sha256:{expression_edit_source_digest}"
        and expression_edit_identity["generation"] == 1
        and expression_edit_identity["tombstones"] == []
        and [step["step"] for step in expression_edit_plan["steps"]]
        == ["replaceAssignmentValue", "replaceWhileCondition"]
        and len({step["stepId"] for step in expression_edit_plan["steps"]}) == 2
        and assignment_edit["targetNodeId"] in expression_edit_identity_ids
        and assignment_edit["expectedOperator"] == "="
        and assignment_edit["expectedTargetNodeId"] in expression_edit_identity_ids
        and assignment_edit["expectedTargetDefinitionId"]
        in expression_edit_identity_ids
        and assignment_edit["expectedTargetName"] == "result"
        and assignment_edit["expectedValueNodeId"] in expression_edit_identity_ids
        and assignment_edit["newValue"]["expression"] == "binary"
        and assignment_edit["newValue"]["operator"] == "-"
        and assignment_edit["newValue"]["right"]["valueText"] == "2"
        and condition_edit["targetNodeId"] in expression_edit_identity_ids
        and condition_edit["expectedConditionNodeId"] in expression_edit_identity_ids
        and condition_payload["expression"] == "binary"
        and condition_payload["operator"] == "&&"
        and condition_payload["left"]["expectedName"] == "enabled"
        and condition_payload["right"]["operator"] == ">"
        and condition_payload["right"]["left"]["expectedName"] == "result"
        and condition_payload["right"]["right"]["expectedName"] == "stopAt"
        and "while (result > 0)" in expression_edit_source
        and "result = result - 1;" in expression_edit_source,
        "Structured expression-edit source, sidecar, and plan are inconsistent",
    )
    general_expression_identity_ids = {
        entry["id"] for entry in general_expression_identity["entries"]
    }
    initializer_edit, if_edit, return_edit = general_expression_plan["steps"]
    require(
        general_expression_plan["schemaVersion"]
        == "unifiedscript.patch-plan/v0.21"
        and general_expression_identity["sourcePath"]
        == "samples/Patching/EditDecisionValues.cs"
        and general_expression_identity["sourceDigest"]
        == f"sha256:{general_expression_source_digest}"
        and general_expression_identity["generation"] == 1
        and general_expression_identity["tombstones"] == []
        and [step["step"] for step in general_expression_plan["steps"]]
        == ["replaceLocalInitializer", "replaceIfCondition", "replaceReturnValue"]
        and len({step["stepId"] for step in general_expression_plan["steps"]}) == 3
        and initializer_edit["targetNodeId"] in general_expression_identity_ids
        and initializer_edit["expectedInitializerNodeId"]
        in general_expression_identity_ids
        and initializer_edit["expectedName"] == "result"
        and initializer_edit["newInitializer"]["operator"] == "-"
        and if_edit["targetNodeId"] in general_expression_identity_ids
        and if_edit["expectedConditionNodeId"] in general_expression_identity_ids
        and if_edit["newCondition"]["operator"] == "&&"
        and if_edit["newCondition"]["right"]["operator"] == ">"
        and return_edit["targetNodeId"] in general_expression_identity_ids
        and return_edit["expectedValueNodeId"] in general_expression_identity_ids
        and return_edit["newValue"]["operator"] == "+"
        and initializer_edit["expectedTypeId"]
        == return_edit["expectedReturnTypeId"]
        and if_edit["expectedConditionTypeId"]
        == if_edit["newCondition"]["expectedTypeId"]
        and "int result = value + 1;" in general_expression_source
        and "if (enabled)" in general_expression_source
        and "return result;" in general_expression_source,
        "Generalized expression-endpoint source, sidecar, and plan are inconsistent",
    )
    produced_value_identity_ids = {
        entry["id"] for entry in produced_value_identity["entries"]
    }
    value_producer, guard_consumer, branch_consumer, fallback_consumer = (
        produced_value_plan["steps"]
    )
    guard_reference = guard_consumer["newStatement"]["condition"]["right"]["left"]
    branch_reference = branch_consumer["newStatement"]["expression"]["right"]
    fallback_reference = fallback_consumer["newValue"]["right"]
    produced_references = [guard_reference, branch_reference, fallback_reference]
    require(
        produced_value_plan["schemaVersion"]
        == "unifiedscript.patch-plan/v0.21"
        and produced_value_identity["sourcePath"]
        == "samples/Patching/ConnectGeneratedValues.cs"
        and produced_value_identity["sourceDigest"]
        == f"sha256:{produced_value_source_digest}"
        and produced_value_identity["generation"] == 1
        and produced_value_identity["tombstones"] == []
        and [step["step"] for step in produced_value_plan["steps"]]
        == [
            "insertStatement",
            "insertStatement",
            "insertBranchStatement",
            "replaceReturnValue",
        ]
        and len({step["stepId"] for step in produced_value_plan["steps"]}) == 4
        and value_producer["targetNodeId"] in produced_value_identity_ids
        and value_producer["expectedNextSiblingId"] in produced_value_identity_ids
        and value_producer["newStatement"]["statement"]
        == "localLiteralDeclaration"
        and value_producer["newStatement"]["name"] == "offset",
        "Produced-value expression reference sample has an invalid envelope",
    )
    require(
        guard_consumer["targetNodeId"] == value_producer["targetNodeId"]
        and guard_consumer["newStatement"]["statement"] == "if"
        and branch_consumer["sourceStepId"] == guard_consumer["stepId"]
        and branch_consumer["branch"] == "then"
        and branch_consumer["newStatement"]["statement"] == "return"
        and fallback_consumer["targetNodeId"] in produced_value_identity_ids
        and fallback_consumer["expectedValueNodeId"] in produced_value_identity_ids
        and all(
            reference["expression"] == "producedLocalReference"
            and reference["sourceStepId"] == value_producer["stepId"]
            and reference["expectedName"] == "offset"
            and reference["expectedTypeId"]
            == value_producer["newStatement"]["expectedTypeId"]
            for reference in produced_references
        )
        and "return value;" in produced_value_source,
        "Produced-value expression reference source, sidecar, and plan are inconsistent",
    )
    nested_input_identity_ids = {
        entry["id"] for entry in nested_input_identity["entries"]
    }
    nested_input_producer, nested_input_edit = nested_input_plan["steps"]
    require(
        nested_input_plan["schemaVersion"]
        == "unifiedscript.patch-plan/v0.21"
        and nested_input_identity["sourcePath"]
        == "samples/Patching/EditNestedInput.cs"
        and nested_input_identity["sourceDigest"]
        == f"sha256:{nested_input_source_digest}"
        and nested_input_identity["generation"] == 1
        and nested_input_identity["tombstones"] == []
        and [step["step"] for step in nested_input_plan["steps"]]
        == ["insertStatement", "replaceExpressionInput"]
        and len({step["stepId"] for step in nested_input_plan["steps"]}) == 2
        and nested_input_producer["targetNodeId"] in nested_input_identity_ids
        and nested_input_producer["expectedNextSiblingId"]
        in nested_input_identity_ids
        and nested_input_producer["newStatement"]["statement"]
        == "localLiteralDeclaration"
        and nested_input_producer["newStatement"]["name"] == "offset"
        and nested_input_edit["targetNodeId"] in nested_input_identity_ids
        and nested_input_edit["inputPort"] == "right"
        and nested_input_edit["expectedInputNodeId"] in nested_input_identity_ids
        and nested_input_edit["expectedInputTypeId"]
        == nested_input_edit["expectedTargetTypeId"]
        == nested_input_producer["newStatement"]["expectedTypeId"]
        and nested_input_edit["newInput"]["expression"]
        == "producedLocalReference"
        and nested_input_edit["newInput"]["sourceStepId"]
        == nested_input_producer["stepId"]
        and nested_input_edit["newInput"]["expectedName"] == "offset"
        and "return (value + 1) * 3;" in nested_input_source,
        "Nested expression-input source, sidecar, and plan are inconsistent",
    )
    generated_expression_identity_ids = {
        entry["id"] for entry in generated_expression_identity["entries"]
    }
    generated_root, generated_right, generated_nested_right = (
        generated_expression_plan["steps"]
    )
    generated_handles = [generated_right, generated_nested_right]
    require(
        generated_expression_plan["schemaVersion"]
        == "unifiedscript.patch-plan/v0.21"
        and generated_expression_identity["sourcePath"]
        == "samples/Patching/EditGeneratedExpression.cs"
        and generated_expression_identity["sourceDigest"]
        == f"sha256:{generated_expression_source_digest}"
        and generated_expression_identity["generation"] == 1
        and generated_expression_identity["tombstones"] == []
        and [step["step"] for step in generated_expression_plan["steps"]]
        == [
            "replaceReturnValue",
            "replaceProducedExpressionInput",
            "replaceProducedExpressionInput",
        ]
        and len(
            {step["stepId"] for step in generated_expression_plan["steps"]}
        )
        == 3
        and generated_root["targetNodeId"] in generated_expression_identity_ids
        and generated_root["expectedValueNodeId"]
        in generated_expression_identity_ids
        and generated_root["newValue"]["operator"] == "+"
        and generated_root["newValue"]["left"]["sourceDefinitionId"]
        in generated_expression_identity_ids
        and all(
            handle["targetNodeId"] == generated_root["targetNodeId"]
            and handle["targetPath"] == []
            and handle["inputPort"] == "right"
            and "expectedInputNodeId" not in handle
            for handle in generated_handles
        )
        and generated_right["sourceStepId"] == generated_root["stepId"]
        and generated_nested_right["sourceStepId"] == generated_right["stepId"]
        and generated_right["newInput"]["operator"] == "*"
        and generated_nested_right["newInput"]["valueText"] == "3"
        and "return value;" in generated_expression_source,
        "Produced-expression handle source, sidecar, and chained plan are inconsistent",
    )
    conditional_expression_identity_ids = {
        entry["id"] for entry in conditional_expression_identity["entries"]
    }
    conditional_root, conditional_fallback, conditional_offset = (
        conditional_expression_plan["steps"]
    )
    conditional_payload = conditional_root["newValue"]
    require(
        conditional_expression_plan["schemaVersion"]
        == "unifiedscript.patch-plan/v0.21"
        and conditional_expression_identity["sourcePath"]
        == "samples/Patching/EditConditionalExpression.cs"
        and conditional_expression_identity["sourceDigest"]
        == f"sha256:{conditional_expression_source_digest}"
        and conditional_expression_identity["generation"] == 1
        and conditional_expression_identity["tombstones"] == []
        and [step["step"] for step in conditional_expression_plan["steps"]]
        == [
            "replaceReturnValue",
            "replaceProducedExpressionInput",
            "replaceProducedExpressionInput",
        ]
        and len(
            {step["stepId"] for step in conditional_expression_plan["steps"]}
        )
        == 3
        and conditional_root["targetNodeId"] in conditional_expression_identity_ids
        and conditional_root["expectedValueNodeId"]
        in conditional_expression_identity_ids
        and conditional_payload["expression"] == "conditional"
        and conditional_payload["condition"]["expectedName"] == "enabled"
        and conditional_payload["condition"]["sourceDefinitionId"]
        in conditional_expression_identity_ids
        and conditional_payload["whenTrue"]["expectedName"] == "value"
        and conditional_payload["whenFalse"]["expectedName"] == "fallback"
        and conditional_payload["whenTrue"]["expectedTypeId"]
        == conditional_payload["whenFalse"]["expectedTypeId"]
        == conditional_payload["expectedTypeId"]
        and conditional_fallback["sourceStepId"] == conditional_root["stepId"]
        and conditional_fallback["targetPath"] == []
        and conditional_fallback["inputPort"] == "whenFalse"
        and conditional_fallback["newInput"]["operator"] == "+"
        and conditional_offset["sourceStepId"] == conditional_fallback["stepId"]
        and conditional_offset["inputPort"] == "right"
        and conditional_offset["newInput"]["valueText"] == "2"
        and "return value;" in conditional_expression_source,
        "Conditional-expression source, sidecar, and chained plan are inconsistent",
    )
    require(
        any((ROOT / "tests" / "UnifiedScript.Tests").glob("*Tests.cs")),
        "No tests found",
    )
    require(
        golden_manifest["artifacts"]["graph"]["schemaVersion"]
        == "unifiedscript.graph/v0.13"
        and golden_manifest["artifacts"]["hir"]["schemaVersion"]
        == "unifiedscript.hir/v0.9"
        and "if ((damage > threshold) && !disabled)" in region_source
        and "return true;" in region_source
        and "return false;" in region_source,
        "Current HIR/Graph Golden manifest or early-return region sample is inconsistent",
    )
    require(
        "return enabled && ready;" in short_circuit_source
        and "return cached || ready;" in short_circuit_source
        and "return Poll() && enabled;" in short_circuit_source,
        "Short-circuit sample does not cover both operators and left effects",
    )
    require(
        "return value ?? fallback;" in coalesce_source
        and "return Poll() ?? fallback;" in coalesce_source
        and "return value ?? Math.Abs(fallback);" in coalesce_source,
        "Null-coalescing sample does not cover nullable values and effect boundaries",
    )
    require(
        "return value?.Length;" in null_conditional_source
        and "return Observe(value)?.Length;" in null_conditional_source
        and "return (primary ?? fallback)?.Length;" in null_conditional_source,
        "Null-conditional sample does not cover receiver, effects, and composition",
    )
    require(
        "while (value > 0)" in loop_source
        and "break;" in loop_source
        and "continue;" in loop_source
        and "value = value - 1;" in loop_source,
        "Structured while-loop sample does not cover loop transfers and assignment",
    )
    require(
        "for (int index = 0; index < count; index++)" in for_source
        and "for (Reset(); state < limit; Advance())" in for_source
        and "break;" in for_source
        and "continue;" in for_source,
        "Lossless for-loop sample does not cover declaration/effect phases and transfers",
    )
    require(
        "do\n        {" in do_while_source
        and "while (enabled && value > 0);" in do_while_source
        and "break;" in do_while_source
        and "continue;" in do_while_source
        and "value = value - 1;" in do_while_source,
        "Lossless do-while sample does not cover post-test order and loop transfers",
    )
    require(
        "foreach (int value in values)" in for_each_source
        and "break;" in for_each_source
        and "continue;" in for_each_source
        and "total = total + value;" in for_each_source,
        "Indexed foreach sample does not cover binding, transfers, and assignment",
    )
    require(
        "IEnumerable<int> values" in enumerable_for_each_source
        and "foreach (int value in values)" in enumerable_for_each_source
        and "return total;" in enumerable_for_each_source
        and "break;" in enumerable_for_each_source
        and "continue;" in enumerable_for_each_source,
        "Enumerable foreach sample does not cover disposal-relevant transfers",
    )
    loop_kinds = {entry["kind"] for entry in loop_identity["entries"]}
    require(
        loop_identity["sourcePath"] == "samples/Loops/Countdown.cs"
        and loop_identity["sourceDigest"] == f"sha256:{loop_source_digest}"
        and loop_identity["generation"] == 1
        and loop_identity["tombstones"] == []
        and {"while", "break", "continue", "assignment"}.issubset(loop_kinds),
        "Structured while-loop identity sidecar is inconsistent",
    )
    layout_functions = layout["functions"]
    layout_nodes = [
        node
        for function in layout_functions
        for node in function["nodes"]
    ]
    require(
        layout["schemaVersion"] == "unifiedscript.graph-layout/v0.1"
        and len(layout_functions) == 1
        and layout_nodes
        and all(node["nodeId"].startswith("us_") for node in layout_nodes)
        and all(node["placement"] == "placed" for node in layout_nodes)
        and all(
            node["size"]["width"] > 0 and node["size"]["height"] > 0
            for node in layout_nodes
        )
        and layout_functions[0]["commentRegions"]
        and layout_functions[0]["routingHints"]
        and layout["orphanedNodes"] == [],
        "Graph layout sample does not exercise the complete v0.1 canvas state",
    )
    checks.append("sample and test sources exist")

    for check in checks:
        print(f"[ok] {check}")
    print(f"Validated {len(checks)} repository invariants.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (AssertionError, KeyError, OSError, ValueError, ET.ParseError) as exc:
        print(f"[error] {exc}", file=sys.stderr)
        raise SystemExit(1) from exc
