#!/usr/bin/env python3
"""Validate repository structure without duplicating the .NET test suite."""

from __future__ import annotations

import hashlib
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]

PROJECTS = [
    ROOT / "src" / "UnifiedScript.Core" / "UnifiedScript.Core.csproj",
    ROOT / "src" / "UnifiedScript.Semantics" / "UnifiedScript.Semantics.csproj",
    ROOT / "src" / "UnifiedScript.CSharp" / "UnifiedScript.CSharp.csproj",
    ROOT / "src" / "UnifiedScript.Graph" / "UnifiedScript.Graph.csproj",
    ROOT / "src" / "UnifiedScript.Runtime" / "UnifiedScript.Runtime.csproj",
    ROOT / "src" / "UnifiedScript.Cli" / "UnifiedScript.Cli.csproj",
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
    ROOT / "docs" / "ROADMAP.md",
    ROOT / "docs" / "milestones" / "M1_ROSLYN_GRAPH_FOUNDATION.md",
    ROOT / "docs" / "milestones" / "M2_SEMANTIC_BINDING.md",
    ROOT / "docs" / "milestones" / "M3_BIDIRECTIONAL_PATCHING.md",
    ROOT / "docs" / "milestones" / "M4_STRUCTURED_CONTROL_FLOW.md",
    ROOT / "docs" / "milestones" / "M5_CORECLR_EXECUTION.md",
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
        patch_document["schemaVersion"] == "unifiedscript.patch/v0.19"
        and operator_patch["schemaVersion"] == "unifiedscript.patch/v0.19"
        and insert_patch["schemaVersion"] == "unifiedscript.patch/v0.19"
        and temporary_patch["schemaVersion"] == "unifiedscript.patch/v0.19"
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
        reconnect_patch["schemaVersion"] == "unifiedscript.patch/v0.19"
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
        assignment_patch["schemaVersion"] == "unifiedscript.patch/v0.19"
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
        plan_document["schemaVersion"] == "unifiedscript.patch-plan/v0.20"
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
        composed_document["schemaVersion"] == "unifiedscript.patch-plan/v0.20"
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
        guard_document["schemaVersion"] == "unifiedscript.patch/v0.19"
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
        guard_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.20"
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
        guard_return_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.20"
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
        nested_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.20"
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
        loop_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.20"
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
        do_while_build_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.20"
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
        == "unifiedscript.patch-plan/v0.20"
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
        == "unifiedscript.patch-plan/v0.20"
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
        for_build_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.20"
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
        for_edit_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.20"
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
        == "unifiedscript.patch-plan/v0.20"
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
        == "unifiedscript.patch-plan/v0.20"
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
        == "unifiedscript.patch-plan/v0.20"
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
        == "unifiedscript.patch-plan/v0.20"
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
        == "unifiedscript.patch-plan/v0.20"
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
        == "unifiedscript.patch-plan/v0.20"
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
        == "unifiedscript.graph/v0.12"
        and golden_manifest["artifacts"]["hir"]["schemaVersion"]
        == "unifiedscript.hir/v0.7"
        and "if ((damage > threshold) && !disabled)" in region_source
        and "return true;" in region_source
        and "return false;" in region_source,
        "HIR v0.7/Graph v0.12 Golden manifest or early-return region sample is inconsistent",
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
