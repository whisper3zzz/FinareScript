#!/usr/bin/env python3
"""Select CI coverage and reject incomplete evidence for the selected revision."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

LANES = (
    "managed-tests", "deterministic-generation", "ue-build", "unreal-automation",
    "package-smoke", "workload-performance", "sdk-installation",
)
PHASES = {
    "managed-tests": ("managed-tests", "repository-validator", "api-library-build-graph"),
    "deterministic-generation": ("deterministic-generation",),
    "ue-build": ("managed-release-build", "ue-editor-build"),
    "unreal-automation": ("api-library-new-call-reload", "unreal-automation"),
    "package-smoke": ("development-package-smoke",),
    "workload-performance": ("matched-package-size", "matched-workload-performance"),
    "sdk-installation": ("clean-sdk-installation",),
}


def select_lanes(paths: list[str], full: bool = False) -> list[str]:
    if full:
        return list(LANES)
    required = set(LANES[:2])
    for raw in paths:
        path = raw.replace("\\", "/").lower()
        # Pure prose cannot change execution. Unknown executable/configuration
        # paths conservatively receive full coverage instead of falling through.
        if path.endswith(".md") or path in (".gitignore", ".gitattributes"):
            continue
        if path.startswith("tests/"):
            continue
        if path.startswith(("src/", "hostproject/", "samples/", "schemas/")):
            required.update(("ue-build", "unreal-automation"))
            if path.startswith(("src/finare.runtime/", "src/finare.unreal/", "src/finare.csharp/",
                                "src/finare.compilation/", "src/finare.programmodel/", "src/finare.core/",
                                "hostproject/plugins/finare/source/finareruntime/")) or path.endswith(
                    (".csproj", ".props", ".targets", ".build.cs", ".uplugin", ".uproject")):
                required.update(("package-smoke", "sdk-installation"))
        else:
            required.update(LANES)
    return [lane for lane in LANES if lane in required]


def git(*args: str) -> str:
    return subprocess.check_output(["git", *args], text=True, encoding="utf-8").strip()


def make_plan(event_name: str, event: dict, revision: str, paths: list[str]) -> dict:
    full = event_name in ("schedule", "workflow_dispatch") or (
        event_name == "push" and event.get("ref") == "refs/heads/main")
    trusted = event_name != "pull_request" or (
        event.get("pull_request", {}).get("head", {}).get("repo", {}).get("full_name")
        == event.get("repository", {}).get("full_name")
        and bool(event.get("repository", {}).get("full_name")))
    required = select_lanes(paths, full)
    return {
        "schemaVersion": "finare.ci-plan/v1", "sourceRevision": revision,
        "event": event_name, "scope": "full-matrix" if full else "change-selected",
        "requiredLanes": required, "changedPaths": paths, "trustedNativeExecution": trusted,
    }


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def read_json(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    require(isinstance(value, dict), f"{path}: expected JSON object")
    return value


def contained_file(root: Path, relative: str) -> Path:
    require(isinstance(relative, str) and bool(relative), "missing artifact path")
    candidate = (root / relative).resolve()
    require(candidate.is_relative_to(root.resolve()), "artifact escapes report directory")
    require(candidate.is_file() and candidate.stat().st_size > 0, f"missing/empty evidence: {relative}")
    return candidate


def validate_trx(path: Path) -> None:
    root = ET.parse(path).getroot()
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    summary = root.find("t:ResultSummary", ns)
    require(summary is not None, "TRX has no result summary")
    require(summary.get("outcome") in ("Completed", "Passed"), "TRX summary is not successful")
    counters = summary.find("t:Counters", ns)
    require(counters is not None, "TRX has no counters")
    total = int(counters.get("total", "0"))
    require(total > 0, "TRX executed zero tests")
    for name in ("executed", "passed"):
        require(int(counters.get(name, "0")) == total, f"TRX {name} does not match total")
    for name in ("failed", "error", "timeout", "aborted", "notExecuted", "disconnected", "inconclusive"):
        require(int(counters.get(name, "0")) == 0, f"TRX contains {name} tests")
    results = root.findall("t:Results/t:UnitTestResult", ns)
    require(len(results) == total, "TRX result count does not match counters")
    require(all(r.get("outcome") == "Passed" for r in results), "TRX contains a non-passing result")


def validate_child(path: Path, revision: str | None = None, allow_dirty: bool = False) -> None:
    if path.suffix.lower() == ".trx":
        validate_trx(path)
        return
    if path.suffix.lower() != ".json":
        return
    report = read_json(path)
    if revision is not None and "sourceRevision" in report:
        require(report["sourceRevision"] == revision, "child report belongs to a different revision")
    if not allow_dirty:
        for key in ("sourceDirty", "sourceTreeDirty"):
            if key in report:
                require(report[key] is False, "child report used a dirty source tree")
    schema = report.get("schemaVersion", "")
    # Matched workload measurements are data, while budget-report is the verdict.
    if schema == "unifiedscript.performance-budget-report/v0.1":
        require(report.get("status") == "qualified", "performance budget is not qualified")
    elif schema == "unifiedscript.deterministic-generation-gate/v0.1":
        rows = report.get("artifacts", [])
        require(report.get("status") == "passed" and bool(rows), "generation has no passing artifacts")
        require(report.get("artifactCount") == len(rows), "generation artifact count disagrees")
        available = {"bindings.cs", "native-thunks.cpp", "api-nodes.json", "binding-coverage.json"}
        require(all(r.get("matchesSecondRun") is True and
                    (r.get("matchesCheckedIn") is True or
                     r.get("path") in available and r.get("matchesCheckedIn") is None) for r in rows),
                "generation drift detected")
    elif "onboarding" in schema:
        require(report.get("status") == "passed", "onboarding did not pass")
        counts = report.get("outputs", {}).get("automation", {})
        required = counts.get("required", 0)
        require(isinstance(required, int) and required > 0, "onboarding has no required Automation coverage")
        require(counts.get("passed", 0) >= required and counts.get("failed") == 0,
                "onboarding Automation coverage failed or is missing")
        require(counts.get("criticalFailures") == 0, "engine failure scan is missing or found a critical failure")
    elif schema == "unifiedscript.unreal-release-gate/v0.2":
        require(report.get("status") == "passed", "package gate did not pass")
        rows = report.get("configurations", [])
        require(bool(rows), "package gate has no configurations")
        for row in rows:
            require(all(row.get(phase) == "passed" for phase in ("build", "cook", "stage", "pak", "archive")),
                    "package stage is incomplete")
            require(row.get("smokeExitCode") == 0 and bool(row.get("smokeResult")), "packaged gameplay smoke is missing")
            require(row.get("pluginLocalRuntime") is True and row.get("poisonedExternalDotNetRoots") is True,
                    "package did not verify its own runtime")
    elif schema == "unifiedscript.unreal-compatibility-matrix/v0.1":
        require(report.get("status") == "passed" and bool(report.get("supported")), "SDK matrix is incomplete")
        require(report.get("sdk", {}).get("deterministic") is True, "SDK archive is not deterministic")
        rows = report["supported"]
        require({"Editor", "Game", "SDK-installed Game"} <= {r.get("target") for r in rows}, "SDK matrix is missing a target")
        for row in rows:
            require(row.get("build") == "passed", "SDK target did not build")
            if row.get("target") == "Editor":
                counts = row.get("automation", {})
                require(counts.get("expected", 0) > 0 and counts.get("passed", 0) >= counts["expected"], "SDK Editor tests are missing")
            else:
                require(row.get("packagedSmoke") == "passed" and row.get("pluginLocalRuntime") is True,
                        "SDK packaged gameplay did not pass")
    elif "status" in report:
        require(report["status"] in ("passed", "qualified"), f"child report did not pass: {path.name}")


def validate_lane(path: Path, lane: str, revision: str, allow_dirty: bool = False) -> None:
    report = read_json(path)
    require(report.get("schemaVersion") == "unifiedscript.ci-lane/v0.1", "wrong CI report schema")
    require(report.get("lane") == lane and lane in PHASES, "wrong CI lane")
    require(report.get("sourceRevision") == revision, "CI report belongs to a different revision")
    require(allow_dirty or report.get("sourceDirty") is False, "CI report does not prove a clean checkout")
    require(report.get("status") == "passed" and report.get("failure") is None, "CI lane did not pass")
    phases = report.get("phases", [])
    require([p.get("name") for p in phases] == list(PHASES[lane]), "CI phases are missing, duplicated or unexpected")
    for phase in phases:
        require(phase.get("status") == "passed" and phase.get("exitCode") == 0, "CI phase failed")
        contained_file(path.parent, phase.get("log"))
        artifacts = phase.get("artifacts", [])
        if phase["name"] not in ("repository-validator", "managed-release-build", "ue-editor-build", "api-library-build-graph"):
            require(bool(artifacts), f"{phase['name']} has no test evidence")
        for artifact in artifacts:
            validate_child(contained_file(path.parent, artifact), revision, allow_dirty)
    expected = {
        "managed-tests": "managed-tests.trx",
        "deterministic-generation": "generation/deterministic-generation.json",
        "unreal-automation": "onboarding-report.json",
        "package-smoke": "release/release-gate.json",
        "workload-performance": "performance/budget-report.json",
        "sdk-installation": "compatibility-matrix/compatibility-matrix.json",
    }.get(lane)
    if expected:
        child = contained_file(path.parent, expected)
        schemas = {
            "deterministic-generation": "unifiedscript.deterministic-generation-gate/v0.1",
            "unreal-automation": "unifiedscript.unreal-project-onboarding-report/v0.1",
            "package-smoke": "unifiedscript.unreal-release-gate/v0.2",
            "workload-performance": "unifiedscript.performance-budget-report/v0.1",
            "sdk-installation": "unifiedscript.unreal-compatibility-matrix/v0.1",
        }
        if lane in schemas:
            require(read_json(child).get("schemaVersion") == schemas[lane], "wrong child report schema")
        validate_child(child, revision, allow_dirty)


def evaluate(plan: dict, root: Path, jobs: dict) -> dict:
    require(plan.get("schemaVersion") == "finare.ci-plan/v1", "missing/invalid CI plan")
    required = plan.get("requiredLanes", [])
    require(bool(required) and len(set(required)) == len(required) and set(required) <= set(LANES), "invalid required lanes")
    revision = plan.get("sourceRevision")
    require(isinstance(revision, str) and len(revision) == 40, "invalid source revision")
    rows = []
    for lane in LANES:
        if lane not in required:
            rows.append({"lane": lane, "status": "not-required"})
            continue
        job = lane if lane in LANES[:2] else "unreal"
        paths = [p for p in root.rglob("ci-lane.json") if read_json(p).get("lane") == lane]
        try:
            require(jobs.get(job) == "success", f"job result is {jobs.get(job, 'missing')}")
            require(len(paths) == 1, "expected exactly one report from this workflow run")
            validate_lane(paths[0], lane, revision)
            rows.append({"lane": lane, "status": "passed"})
        except (ValueError, OSError, ET.ParseError) as error:
            rows.append({"lane": lane, "status": "failed", "reason": str(error)})
    return {"schemaVersion": "finare.ci-verdict/v1", "sourceRevision": revision,
            "scope": plan["scope"], "status": "passed" if all(r["status"] != "failed" for r in rows) else "failed",
            "lanes": rows}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("plan")
    p.add_argument("--output", type=Path, required=True)
    p = sub.add_parser("verify-lane")
    p.add_argument("--report", type=Path, required=True)
    p.add_argument("--lane", choices=LANES, required=True)
    p.add_argument("--revision", required=True)
    p.add_argument("--allow-dirty", action="store_true", help="local diagnostics only; aggregate CI always requires clean")
    p = sub.add_parser("verify")
    p.add_argument("--plan", type=Path, required=True)
    p.add_argument("--reports", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.command == "verify-lane":
            validate_lane(args.report, args.lane, args.revision, args.allow_dirty)
            return 0
        if args.command == "plan":
            event_name = os.environ["GITHUB_EVENT_NAME"]
            event = read_json(Path(os.environ["GITHUB_EVENT_PATH"]))
            base = event.get("pull_request", {}).get("base", {}).get("sha") if event_name == "pull_request" else event.get("before")
            paths = git("diff", "--name-only", base, "HEAD").splitlines() if base and set(base) != {"0"} else git("ls-files").splitlines()
            result = make_plan(event_name, event, git("rev-parse", "HEAD"), paths)
            native = [lane for lane in result["requiredLanes"] if lane not in LANES[:2]]
            if output := os.environ.get("GITHUB_OUTPUT"):
                with open(output, "a", encoding="utf-8") as stream:
                    stream.write(f"native={json.dumps(native)}\ntrusted={str(result['trustedNativeExecution']).lower()}\n")
        else:
            jobs = {name: value["result"] for name, value in json.loads(os.environ.get("CI_JOB_RESULTS", "{}")).items()}
            plan = read_json(args.plan)
            require(jobs.get("plan") == "success", "coverage planning did not succeed")
            require(plan.get("sourceRevision") == git("rev-parse", "HEAD"), "coverage plan is stale for this checkout")
            result = evaluate(plan, args.reports, jobs)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(result, indent=2))
        if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(summary, "a", encoding="utf-8") as stream:
                stream.write(f"\nCI scope: **{result['scope']}**, revision `{result['sourceRevision']}`\n\n")
                for row in result.get("lanes", []):
                    stream.write(f"- {row['lane']}: **{row['status']}** {row.get('reason', '')}\n")
        return int(result.get("status") == "failed")
    except (ValueError, OSError, KeyError, TypeError, ET.ParseError, subprocess.CalledProcessError) as error:
        print(f"CI evidence rejected: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
