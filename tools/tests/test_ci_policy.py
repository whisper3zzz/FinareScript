from __future__ import annotations

import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from ci_policy import LANES, evaluate, make_plan, select_lanes, validate_child, validate_lane, validate_trx

REVISION = "a" * 40


class CiCoverageTests(unittest.TestCase):
    def test_documentation_does_not_claim_native_acceptance(self):
        self.assertEqual(select_lanes(["README.md", "docs/ROADMAP.md"]), list(LANES[:2]))

    def test_runtime_authoring_and_native_changes_require_editor_coverage(self):
        for path in ("src/Finare.Runtime/CoreClrDebugProbe.cs", "src/Finare.Cli/AuthoringSession.cs",
                     "HostProject/Plugins/Finare/Source/FinareRuntime/Private/Tests/Case.cpp"):
            with self.subTest(path=path):
                self.assertTrue({"ue-build", "unreal-automation"} <= set(select_lanes([path])))

    def test_build_delivery_and_unknown_changes_cannot_fall_through(self):
        for path in ("src/Finare.Runtime/Finare.Runtime.csproj", "src/Finare.Runtime/Loader.cs",
                     "src/Finare.Unreal/Bridge.cs", "tools/setup-unreal-project.ps1",
                     "release/manifest.json", "new-build-system/config.json"):
            with self.subTest(path=path):
                self.assertTrue({"package-smoke", "sdk-installation"} <= set(select_lanes([path])))
        self.assertEqual(select_lanes(["docs/x.md"], full=True), list(LANES))

    def test_fork_keeps_native_requirement_without_running_untrusted_code(self):
        event = {"repository": {"full_name": "owner/repo"}, "pull_request": {
            "head": {"repo": {"full_name": "contributor/repo"}}}}
        plan = make_plan("pull_request", event, REVISION, ["src/Finare.Unreal/Bridge.cs"])
        self.assertFalse(plan["trustedNativeExecution"])
        self.assertIn("unreal-automation", plan["requiredLanes"])
        event["pull_request"]["head"]["repo"]["full_name"] = "owner/repo"
        self.assertTrue(make_plan("pull_request", event, REVISION, [])['trustedNativeExecution'])

    def test_main_manual_and_scheduled_require_full_matrix(self):
        for name, event in (("push", {"ref": "refs/heads/main"}), ("schedule", {}), ("workflow_dispatch", {})):
            self.assertEqual(make_plan(name, event, REVISION, ["README.md"])["requiredLanes"], list(LANES))


def trx(total=1, executed=1, passed=1, result="Passed"):
    return f'''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
      <Results>{''.join(f'<UnitTestResult outcome="{result}" />' for _ in range(total))}</Results>
      <ResultSummary outcome="Completed"><Counters total="{total}" executed="{executed}"
        passed="{passed}" failed="{total - passed}" /></ResultSummary></TestRun>'''


class CiEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def write(self, path, content):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content if isinstance(content, str) else json.dumps(content), encoding="utf-8")
        return target

    def managed_report(self):
        self.write("managed-tests.trx", trx())
        self.write("01-tests.log", "tests passed")
        self.write("02-structure.log", "contracts passed")
        return {"schemaVersion": "unifiedscript.ci-lane/v0.1", "lane": "managed-tests",
                "sourceRevision": REVISION, "sourceDirty": False, "status": "passed", "failure": None,
                "phases": [{"name": "managed-tests", "status": "passed", "exitCode": 0,
                            "log": "01-tests.log", "artifacts": ["managed-tests.trx"]},
                           {"name": "repository-validator", "status": "passed", "exitCode": 0,
                            "log": "02-structure.log", "artifacts": []}]}

    def test_real_test_results_are_required_even_with_success_exit(self):
        for content in (trx(0, 0, 0), trx(1, 0, 0, "NotExecuted"), trx(1, 1, 0, "Failed"),
                        trx(1, 1, 1, "NotExecuted"), "<TestRun />"):
            with self.subTest(content=content), self.assertRaises(ValueError):
                validate_trx(self.write("test.trx", content))
        validate_trx(self.write("test.trx", trx()))

    def test_dirty_stale_wrong_or_incomplete_reports_are_rejected(self):
        valid = self.managed_report()
        for mutation in ({"sourceDirty": True}, {"sourceRevision": "b" * 40}, {"status": "failed"},
                         {"lane": "ue-build"}, {"phases": []}, {"failure": {"message": "failed"}}):
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                validate_lane(self.write("ci-lane.json", valid | mutation), "managed-tests", REVISION)
        validate_lane(self.write("ci-lane.json", valid), "managed-tests", REVISION)

    def test_missing_evidence_cannot_be_replaced_with_an_unrelated_artifact(self):
        valid = self.managed_report()
        (self.root / "managed-tests.trx").unlink()
        self.write("unrelated.json", {"status": "passed"})
        valid["phases"][0]["artifacts"] = ["unrelated.json"]
        with self.assertRaisesRegex(ValueError, "missing/empty"):
            validate_lane(self.write("ci-lane.json", valid), "managed-tests", REVISION)

    def test_artifact_paths_cannot_escape_report_directory(self):
        valid = self.managed_report()
        valid["phases"][0]["artifacts"] = ["../outside.json"]
        with self.assertRaisesRegex(ValueError, "escapes"):
            validate_lane(self.write("ci-lane.json", valid), "managed-tests", REVISION)

    def test_aggregate_rejects_skipped_cancelled_or_missing_required_jobs(self):
        self.write("ci-lane.json", self.managed_report())
        plan = {"schemaVersion": "finare.ci-plan/v1", "sourceRevision": REVISION,
                "scope": "change-selected", "requiredLanes": ["managed-tests"]}
        for status in ("skipped", "cancelled", "failure", None):
            result = evaluate(plan, self.root, {"managed-tests": status})
            self.assertEqual(result["status"], "failed")
        result = evaluate(plan, self.root, {"managed-tests": "success"})
        self.assertEqual(result["status"], "passed")
        self.assertEqual(result["lanes"][2]["status"], "not-required")
        self.write("duplicate/ci-lane.json", self.managed_report())
        self.assertEqual(evaluate(plan, self.root, {"managed-tests": "success"})["status"], "failed")

    def test_onboarding_success_requires_executed_tests_and_engine_failure_scan(self):
        valid = {"schemaVersion": "unifiedscript.unreal-project-onboarding-report/v0.1", "status": "passed",
                 "outputs": {"automation": {"required": 2, "passed": 2, "failed": 0, "criticalFailures": 0}}}
        validate_child(self.write("onboarding.json", valid))
        for mutation in ({"passed": 0}, {"failed": 1}, {"criticalFailures": 1}, {"criticalFailures": None}):
            value = copy.deepcopy(valid)
            value["outputs"]["automation"].update(mutation)
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                validate_child(self.write("onboarding.json", value))

    def test_generation_and_package_success_need_actual_scope(self):
        for report in (
            {"schemaVersion": "unifiedscript.deterministic-generation-gate/v0.1", "status": "passed", "artifacts": []},
            {"schemaVersion": "unifiedscript.unreal-release-gate/v0.2", "status": "passed", "configurations": []},
            {"schemaVersion": "unifiedscript.unreal-compatibility-matrix/v0.1", "status": "passed", "supported": []},
        ):
            with self.subTest(report=report), self.assertRaises(ValueError):
                validate_child(self.write("child.json", report))

    def test_child_revision_must_match_even_when_the_outer_lane_is_current(self):
        child = {"schemaVersion": "unifiedscript.deterministic-generation-gate/v0.1", "status": "passed",
                 "sourceRevision": "b" * 40, "artifactCount": 1,
                 "artifacts": [{"matchesSecondRun": True, "matchesCheckedIn": True}]}
        with self.assertRaisesRegex(ValueError, "different revision"):
            validate_child(self.write("generation.json", child), REVISION)
        child["sourceRevision"] = REVISION
        child["sourceTreeDirty"] = True
        with self.assertRaisesRegex(ValueError, "dirty"):
            validate_child(self.write("generation.json", child), REVISION)


if __name__ == "__main__":
    unittest.main()
