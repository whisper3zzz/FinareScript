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
    ROOT / "src" / "UnifiedScript.Cli" / "UnifiedScript.Cli.csproj",
    ROOT / "tests" / "UnifiedScript.Tests" / "UnifiedScript.Tests.csproj",
]

DOCUMENTS = [
    ROOT / "README.md",
    ROOT / "CONTRIBUTING.md",
    ROOT / "docs" / "ARCHITECTURE.md",
    ROOT / "docs" / "IDENTITY_SIDECAR.md",
    ROOT / "docs" / "GRAPH_LAYOUT.md",
    ROOT / "docs" / "PATCH_PLAN.md",
    ROOT / "docs" / "ROADMAP.md",
    ROOT / "docs" / "milestones" / "M1_ROSLYN_GRAPH_FOUNDATION.md",
    ROOT / "docs" / "milestones" / "M2_SEMANTIC_BINDING.md",
    ROOT / "docs" / "milestones" / "M3_BIDIRECTIONAL_PATCHING.md",
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
    layout = json.loads(layout_path.read_text(encoding="utf-8"))
    patch_operation = patch_document["operations"][0]
    operator_operation = operator_patch["operations"][0]
    insert_operation = insert_patch["operations"][0]
    temporary_operation = temporary_patch["operations"][0]
    reconnect_operation = reconnect_patch["operations"][0]
    identity_ids = {entry["id"] for entry in patch_identity["entries"]}
    reconnect_identity_ids = {entry["id"] for entry in reconnect_identity["entries"]}
    source_digest = hashlib.sha256(patch_source.encode("utf-8")).hexdigest()
    reconnect_source_digest = hashlib.sha256(reconnect_source.encode("utf-8")).hexdigest()
    plan_source_digest = hashlib.sha256(plan_source.encode("utf-8")).hexdigest()
    composed_source_digest = hashlib.sha256(composed_source.encode("utf-8")).hexdigest()
    guard_source_digest = hashlib.sha256(guard_source.encode("utf-8")).hexdigest()
    require(
        patch_document["schemaVersion"] == "unifiedscript.patch/v0.7"
        and operator_patch["schemaVersion"] == "unifiedscript.patch/v0.7"
        and insert_patch["schemaVersion"] == "unifiedscript.patch/v0.7"
        and temporary_patch["schemaVersion"] == "unifiedscript.patch/v0.7"
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
        reconnect_patch["schemaVersion"] == "unifiedscript.patch/v0.7"
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
    plan_steps = plan_document["steps"]
    plan_identity_ids = {entry["id"] for entry in plan_identity["entries"]}
    plan_step_ids = {step["stepId"] for step in plan_steps}
    introduction_step = plan_steps[0]
    reconnect_steps = plan_steps[1:]
    require(
        plan_document["schemaVersion"] == "unifiedscript.patch-plan/v0.5"
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
        composed_document["schemaVersion"] == "unifiedscript.patch-plan/v0.5"
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
        guard_document["schemaVersion"] == "unifiedscript.patch/v0.7"
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
        guard_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.5"
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
        guard_return_plan["schemaVersion"] == "unifiedscript.patch-plan/v0.5"
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
    require(
        any((ROOT / "tests" / "UnifiedScript.Tests").glob("*Tests.cs")),
        "No tests found",
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
