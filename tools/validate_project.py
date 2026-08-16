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
    require(
        patch_source_path.is_file()
        and patch_document_path.is_file()
        and operator_patch_path.is_file()
        and insert_patch_path.is_file()
        and temporary_patch_path.is_file()
        and patch_identity_path.is_file(),
        "Missing bidirectional patch sample",
    )
    patch_source = patch_source_path.read_text(encoding="utf-8")
    patch_document = json.loads(patch_document_path.read_text(encoding="utf-8"))
    operator_patch = json.loads(operator_patch_path.read_text(encoding="utf-8"))
    insert_patch = json.loads(insert_patch_path.read_text(encoding="utf-8"))
    temporary_patch = json.loads(temporary_patch_path.read_text(encoding="utf-8"))
    patch_identity = json.loads(patch_identity_path.read_text(encoding="utf-8"))
    patch_operation = patch_document["operations"][0]
    operator_operation = operator_patch["operations"][0]
    insert_operation = insert_patch["operations"][0]
    temporary_operation = temporary_patch["operations"][0]
    identity_ids = {entry["id"] for entry in patch_identity["entries"]}
    source_digest = hashlib.sha256(patch_source.encode("utf-8")).hexdigest()
    require(
        patch_document["schemaVersion"] == "unifiedscript.patch/v0.4"
        and operator_patch["schemaVersion"] == "unifiedscript.patch/v0.4"
        and insert_patch["schemaVersion"] == "unifiedscript.patch/v0.4"
        and temporary_patch["schemaVersion"] == "unifiedscript.patch/v0.4"
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
        any((ROOT / "tests" / "UnifiedScript.Tests").glob("*Tests.cs")),
        "No tests found",
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
