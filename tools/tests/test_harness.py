#!/usr/bin/env python3
"""Behavior tests use isolated temporary repositories, never the user's index."""

from __future__ import annotations

import copy
import importlib.util
import json
import os
import shutil
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("harness", REPO / "tools/harness.py")
harness = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(harness)


def fixture_gate(script="gate.py", **overrides):
    gate = {"description": "fixture", "driver": "python", "script": script, "args": [],
            "dependsOn": [], "requires": [], "timeoutSeconds": 10, "mayRegenerateSource": False}
    return gate | overrides


class HarnessTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="unifiedscript-harness-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.write(".gitignore", "artifacts/\n.cache/\nharness.local.json\n")
        self.write("gate.py", "print('fixture passed')\n")
        self.write("tracked.txt", "baseline\n")
        self.write("docs/ROADMAP.md", "- Active milestone: **fixture**\n- Current tranche: **one**\n")
        self.manifest = {
            "schemaVersion": "unifiedscript.harness-manifest/v0.1",
            "documents": ["docs/ROADMAP.md"], "gates": {"one": fixture_gate()},
            "profiles": {"quick": ["one"], "precommit": ["one"]},
            "routes": [{"patterns": ["docs/*"], "profile": "quick", "reason": "docs"}],
        }
        self.write_manifest()
        self.git("init", "--quiet")
        self.git("config", "user.name", "Harness Fixture")
        self.git("config", "user.email", "harness@example.invalid")
        self.git("config", "core.autocrlf", "false")
        self.git("add", ".")
        self.git("commit", "--quiet", "-m", "fixture")

    def write(self, name, text):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def write_manifest(self):
        self.write(harness.MANIFEST, json.dumps(self.manifest))

    def git(self, *args):
        return harness.git(self.root, *args)

    def run_fixture(self, **options):
        plan = harness.make_plan(self.root, self.manifest, "quick")
        return harness.execute(self.root, self.manifest, plan, options.pop("local", {}), **options)

    def test_real_manifest_and_documents_are_valid(self):
        manifest = harness.load_manifest(REPO)
        self.assertEqual(8, len(manifest["gates"]))
        self.assertNotIn("script-class-golden-path", harness.expand_gates(manifest, manifest["profiles"]["quick"]))

    def test_project_agent_roles_pin_architect_and_executor_models(self):
        config = (REPO / ".codex/config.toml").read_text(encoding="utf-8")
        executor = (REPO / ".codex/agents/executor.toml").read_text(encoding="utf-8")
        self.assertIn('model = "gpt-6-astra"', config)
        self.assertIn('model_reasoning_effort = "xhigh"', config)
        self.assertIn('default_subagent_model = "gpt-5.6-luna"', config)
        self.assertIn('default_subagent_reasoning_effort = "xhigh"', config)
        self.assertIn('max_concurrent_threads_per_session = 1', config)
        self.assertIn('name = "executor"', executor)
        self.assertIn('model = "gpt-5.6-luna"', executor)
        self.assertIn('model_reasoning_effort = "xhigh"', executor)
        plan = harness.make_plan(
            REPO,
            harness.load_manifest(REPO),
            paths=[".codex/config.toml", ".codex/agents/executor.toml"],
        )
        self.assertEqual([], plan["unmappedPaths"])
        self.assertEqual(["harness-tests", "repository"], plan["gates"])

    def test_load_manifest_rejects_duplicate_json_keys(self):
        self.write(harness.MANIFEST, '{"gates": {}, "gates": {}}')
        with self.assertRaisesRegex(harness.HarnessError, "Duplicate"):
            harness.load_manifest(self.root)

    def test_load_manifest_rejects_schema_unknown_fields_and_driver(self):
        for mutate in (
            lambda value: value.update(schemaVersion="future"),
            lambda value: value.update(typo=True),
            lambda value: value["gates"]["one"].update(driver="shell"),
            lambda value: value["gates"]["one"].update(timeoutSeconds=0),
            lambda value: value["gates"]["one"].update(mayRegenerateSource="false"),
            lambda value: value["gates"]["one"].update(requires=["magic"]),
        ):
            with self.subTest(mutate=mutate):
                candidate = copy.deepcopy(self.manifest)
                mutate(candidate)
                self.write(harness.MANIFEST, json.dumps(candidate))
                with self.assertRaises(harness.HarnessError):
                    harness.load_manifest(self.root)

    def test_missing_document_script_dependency_and_report_are_rejected(self):
        candidates = [copy.deepcopy(self.manifest) for _ in range(4)]
        candidates[0]["documents"] = ["missing.md"]
        candidates[1]["gates"]["one"]["script"] = "missing.py"
        candidates[2]["gates"]["one"]["dependsOn"] = ["missing"]
        candidates[3]["gates"]["one"]["driver"] = "powershell"
        for candidate in candidates:
            self.write(harness.MANIFEST, json.dumps(candidate))
            with self.assertRaises(harness.HarnessError):
                harness.load_manifest(self.root)

    def test_dependency_order_deduplication_and_cycle_rejection(self):
        self.manifest["gates"].update(two=fixture_gate(dependsOn=["one"]), three=fixture_gate(dependsOn=["one", "two"]))
        self.assertEqual(["one", "two", "three"], harness.expand_gates(self.manifest, ["three", "two"]))
        self.manifest["gates"]["one"]["dependsOn"] = ["three"]
        with self.assertRaisesRegex(harness.HarnessError, "cycle"):
            harness.expand_gates(self.manifest, ["one"])

    def test_plan_explicit_paths_does_not_include_unrelated_user_changes(self):
        self.write("unrelated.cpp", "user file")
        before = self.git("status", "--porcelain=v1", "-z")
        plan = harness.make_plan(self.root, self.manifest, paths=["docs/ROADMAP.md"])
        self.assertEqual(["docs/ROADMAP.md"], plan["paths"])
        self.assertEqual([], plan["unmappedPaths"])
        self.assertEqual(before, self.git("status", "--porcelain=v1", "-z"))
        self.assertFalse((self.root / "artifacts").exists())

    def test_plan_uses_staged_unstaged_untracked_and_rename_paths(self):
        self.git("mv", "tracked.txt", "renamed.txt")
        self.write("docs/ROADMAP.md", "changed")
        self.write("new.txt", "untracked")
        paths = harness.make_plan(self.root, self.manifest)["paths"]
        self.assertEqual(["docs/ROADMAP.md", "new.txt", "renamed.txt", "tracked.txt"], paths)

    def test_plan_uses_base_ref_committed_diff(self):
        self.write("committed.txt", "change")
        self.git("add", "committed.txt")
        self.git("commit", "--quiet", "-m", "second")
        self.assertEqual(["committed.txt"], harness.make_plan(self.root, self.manifest, base_ref="HEAD~1")["paths"])

    def test_unknown_paths_require_conservative_profile(self):
        self.manifest["gates"]["two"] = fixture_gate(dependsOn=["one"])
        self.manifest["profiles"]["precommit"] = ["two"]
        plan = harness.make_plan(self.root, self.manifest, paths=["new-domain/file.txt"])
        self.assertEqual(["one", "two"], plan["gates"])
        self.assertEqual(["new-domain/file.txt"], plan["unmappedPaths"])

    def test_blueprint_change_selects_packaged_gate(self):
        manifest = harness.load_manifest(REPO)
        plan = harness.make_plan(REPO, manifest, paths=["src/UnifiedScript.Core/ScriptAssetModel.cs"])
        self.assertIn("script-class-golden-path", plan["gates"])
        self.assertNotIn("workload-performance", plan["gates"])

    def test_source_fingerprint_tracks_contents_and_index_without_exposing_contents(self):
        self.write("new.txt", "private-first")
        first = harness.source_state(self.root)
        self.write("new.txt", "private-second")
        second = harness.source_state(self.root)
        self.assertNotEqual(first["fingerprint"], second["fingerprint"])
        self.assertNotIn("private-", json.dumps(second))
        self.write("tracked.txt", "working change")
        third = harness.source_state(self.root)
        self.git("add", "tracked.txt")
        self.assertNotEqual(third["fingerprint"], harness.source_state(self.root)["fingerprint"])

    def test_success_has_fresh_source_logs_report_and_no_git_mutation(self):
        before = harness.source_state(self.root)
        report, output = self.run_fixture()
        self.assertEqual("passed", report["status"])
        self.assertEqual(before, report["sourceAfter"])
        self.assertEqual("fixture passed", (output / "one.log").read_text().strip())
        self.assertTrue((output / "summary.md").is_file())
        self.assertEqual(harness.REPORT_SCHEMA, harness.read_json(output / "report.json")["schemaVersion"])
        self.assertEqual("matches-source", harness.repository_status(self.root)["latestRun"]["freshness"])

    def test_status_marks_same_head_different_content_stale(self):
        self.write("tracked.txt", "dirty before")
        self.run_fixture()
        self.write("tracked.txt", "dirty after")
        latest = harness.repository_status(self.root)["latestRun"]
        self.assertEqual("stale", latest["freshness"])

    def test_status_marks_local_configuration_changes_stale(self):
        self.run_fixture()
        self.write("harness.local.json", '{"protectedPaths": ["tracked.txt"]}')
        self.assertEqual("stale", harness.repository_status(self.root)["latestRun"]["freshness"])

    def test_latest_report_uses_write_time_not_user_output_name(self):
        self.run_fixture(output="zzz-old")
        _, output = self.run_fixture(output="aaa-new")
        self.assertEqual(str(output / "report.json"), harness.repository_status(self.root)["latestRun"]["path"])

    def test_missing_latest_report_does_not_claim_tests_passed(self):
        status = harness.repository_status(self.root)
        self.assertIsNone(status["latestRun"])
        self.assertEqual(2, len(status["roadmapCursor"]))
        self.assertFalse((self.root / "artifacts").exists())

    def test_status_lists_active_task_plans_without_copying_product_cursor(self):
        self.write("docs/plans/active.md", "# task\n\nStatus: in-progress\n")
        self.write("docs/plans/done.md", "Status: complete\n")
        plans = harness.repository_status(self.root)["taskPlans"]
        self.assertEqual([{"path": "docs/plans/active.md", "status": "in-progress"}], plans)

    def test_exit_failure_stops_downstream_and_records_failure(self):
        self.write("gate.py", "raise SystemExit(7)\n")
        self.manifest["gates"]["two"] = fixture_gate(dependsOn=["one"])
        self.manifest["profiles"]["quick"] = ["two"]
        report, _ = self.run_fixture()
        self.assertEqual("failed", report["status"])
        self.assertEqual(7, report["gates"][0]["exitCode"])
        self.assertEqual("not-run", report["gates"][1]["status"])

    def test_zero_exit_without_provider_evidence_is_failure(self):
        self.manifest["gates"]["one"]["report"] = {
            "path": "proof.json", "schemaVersion": "fixture/v1", "checks": {"status": "passed"}}
        report, _ = self.run_fixture()
        self.assertEqual("failed", report["status"])
        self.assertEqual("failed", report["gates"][0]["status"])
        self.assertEqual(0, report["gates"][0]["exitCode"])

    def test_provider_schema_status_boolean_and_malformed_evidence(self):
        gate = fixture_gate(report={"path": "proof.json", "schemaVersion": "fixture/v1",
                                    "checks": {"status": "passed", "cook.skipped": False}})
        output = self.root / "artifacts/proof"
        output.mkdir(parents=True)
        documents = [
            [],
            {"schemaVersion": "wrong", "status": "passed", "cook": {"skipped": False}},
            {"schemaVersion": "fixture/v1", "status": "failed", "cook": {"skipped": False}},
            {"schemaVersion": "fixture/v1", "status": "passed", "cook": {"skipped": True}},
            {"schemaVersion": "fixture/v1", "status": "passed", "cook": {"skipped": 0}},
        ]
        for document in documents:
            (output / "proof.json").write_text(json.dumps(document))
            with self.assertRaises(harness.HarnessError):
                harness.verify_provider(gate, output, {})
        self.write("artifacts/proof/proof.json", "not json")
        with self.assertRaises(ValueError):
            harness.verify_provider(gate, output, {})
        self.write("artifacts/proof/proof.json", json.dumps({"schemaVersion": "fixture/v1", "status": "passed", "cook": {"skipped": False}}))
        result = harness.verify_provider(gate, output, {})
        self.assertEqual(64, len(result["sha256"]))

    def test_ci_provider_requires_matching_source_and_real_phase_artifacts(self):
        source = harness.source_state(self.root)
        output = self.root / "artifacts/ci"
        output.mkdir(parents=True)
        gate = fixture_gate(driver="ci-lane", report={"path": "ci-lane.json", "schemaVersion": "fixture/v1", "checks": {"status": "passed"}})
        document = {"schemaVersion": "fixture/v1", "status": "passed", "sourceRevision": "wrong", "sourceDirty": False,
                    "phases": [{"status": "passed", "exitCode": 0, "log": "phase.log", "artifacts": []}]}
        for update in ({}, {"sourceRevision": source["revision"]}, {"phases": []}):
            document.update(update)
            self.write("artifacts/ci/ci-lane.json", json.dumps(document))
            with self.assertRaises(harness.HarnessError):
                harness.verify_provider(gate, output, source)

    def test_timeout_records_failure_and_preserves_source(self):
        self.write("gate.py", "import time\ntime.sleep(30)\n")
        report, _ = self.run_fixture(timeout_override=1)
        self.assertEqual("timed-out", report["gates"][0]["status"])
        self.assertEqual("failed", report["status"])
        self.assertFalse(report["sourceChangedDuringRun"])
        self.assertLess(report["durationMs"], 10000)

    def test_interrupt_records_no_false_pass_and_releases_lock(self):
        with patch.object(harness, "run_process", return_value=(-1, "interrupted")):
            report, _ = self.run_fixture()
        self.assertEqual("interrupted", report["gates"][0]["status"])
        self.assertNotEqual("passed", report["status"])
        self.assertFalse((self.root / ".cache/unifiedscript-harness.lock").exists())

    def test_timeout_terminates_the_owned_descendant(self):
        marker = self.root / "artifacts/descendant.txt"
        marker.parent.mkdir(parents=True)
        ready = marker.with_suffix(".ready")
        probe = marker.with_suffix(".probe")
        # Probe after cleanup returns: a fixed child sleep incorrectly counts
        # work performed while Windows taskkill is still running as an orphan.
        child = (
            "import time\nfrom pathlib import Path\n"
            f"Path({str(ready)!r}).write_text('ready')\n"
            "deadline = time.monotonic() + 30\n"
            f"while not Path({str(probe)!r}).exists() and time.monotonic() < deadline:\n"
            "    time.sleep(0.02)\n"
            f"if Path({str(probe)!r}).exists(): Path({str(marker)!r}).write_text('orphan')\n"
        )
        parent = f"import subprocess, sys, time; subprocess.Popen([sys.executable, '-c', {child!r}]); time.sleep(30)"
        self.write("gate.py", parent)
        report, _ = self.run_fixture(timeout_override=3)
        self.assertEqual("timed-out", report["gates"][0]["status"])
        self.assertTrue(ready.exists(), "The descendant must start for this test to exercise cleanup")
        probe.touch()
        time.sleep(1.5)
        self.assertFalse(marker.exists(), "The timed-out provider left its child running")

    def test_process_launch_error_is_reported(self):
        with patch.object(harness, "run_process", side_effect=OSError("launch failed")):
            report, _ = self.run_fixture()
        self.assertEqual("failed", report["gates"][0]["status"])
        self.assertIn("launch failed", report["failure"])

    def test_unexpected_provider_decoder_error_is_failure_not_running_or_passed(self):
        with patch.object(harness, "verify_provider", side_effect=TypeError("malformed provider field")):
            report, output = self.run_fixture()
        self.assertEqual("failed", report["status"])
        self.assertEqual("failed", report["gates"][0]["status"])
        self.assertEqual("failed", harness.read_json(output / "report.json")["status"])

    def test_preflight_failure_has_report_and_runs_nothing(self):
        self.write("tracked.txt", "user work")
        report, output = self.run_fixture(require_clean=True)
        self.assertEqual("failed", report["status"])
        self.assertEqual("not-run", report["gates"][0]["status"])
        self.assertFalse((output / "one.log").exists())

    def test_missing_engine_is_preflight_failure_not_skipped_success(self):
        self.manifest["gates"]["one"]["requires"] = ["engine"]
        report, _ = self.run_fixture()
        self.assertEqual("failed", report["status"])
        self.assertIn("engine-root", report["failure"])

    def test_dirty_protected_files_block_source_regenerators_before_launch(self):
        self.write("tracked.txt", "user work")
        self.manifest["gates"]["one"]["mayRegenerateSource"] = True
        report, _ = self.run_fixture(local={"protectedPaths": ["tracked.txt"]})
        self.assertEqual("not-run", report["gates"][0]["status"])
        self.assertIn("dirty protected", report["failure"])
        self.assertEqual("user work", (self.root / "tracked.txt").read_text())

    def test_source_and_protected_changes_invalidate_success_without_reverting(self):
        self.write("gate.py", "from pathlib import Path\nPath('tracked.txt').write_text('changed by provider')\n")
        report, _ = self.run_fixture(local={"protectedPaths": ["tracked.txt"]})
        self.assertEqual("failed", report["status"])
        self.assertTrue(report["sourceChangedDuringRun"])
        self.assertEqual(["tracked.txt"], report["protectedChanges"])
        self.assertEqual("changed by provider", (self.root / "tracked.txt").read_text())

    def test_local_policy_change_during_run_invalidates_evidence(self):
        self.write("gate.py", "from pathlib import Path\nPath('harness.local.json').write_text('{}')\n")
        report, _ = self.run_fixture()
        self.assertEqual("failed", report["status"])
        self.assertTrue(report["localConfigChangedDuringRun"])

    def test_symlink_output_escape_is_rejected(self):
        link = self.root / "artifacts/harness/runs/escape"
        link.parent.mkdir(parents=True)
        try:
            os.symlink(self.root.parent, link, target_is_directory=True)
        except OSError:
            self.skipTest("Symlink creation requires privileges on this host")
        with self.assertRaises(harness.HarnessError):
            self.run_fixture(output="escape/run")

    def test_output_cannot_escape_or_reuse_existing_evidence(self):
        for output in ("../../escape", str(self.root), str(self.root.parent)):
            with self.assertRaises(harness.HarnessError):
                self.run_fixture(output=output)
        _, output = self.run_fixture(output="fixed")
        before = (output / "report.json").read_bytes()
        with self.assertRaisesRegex(harness.HarnessError, "already exist"):
            self.run_fixture(output="fixed")
        self.assertEqual(before, (output / "report.json").read_bytes())

    def test_checkout_lock_is_exclusive_and_not_stolen(self):
        with harness.checkout_lock(self.root):
            with self.assertRaisesRegex(harness.HarnessError, "lock exists"):
                self.run_fixture()
            self.assertTrue((self.root / ".cache/unifiedscript-harness.lock").exists())
        self.assertFalse((self.root / ".cache/unifiedscript-harness.lock").exists())

    def test_unsafe_paths_and_local_configuration_are_rejected(self):
        with self.assertRaises(harness.HarnessError):
            harness.make_plan(self.root, self.manifest, paths=["../elsewhere"])
        self.write("harness.local.json", '{"protectedPaths": ["../user.txt"]}')
        with self.assertRaises(harness.HarnessError):
            harness.load_local(self.root)

    def test_command_uses_argument_array_and_reuses_existing_lane(self):
        gate = fixture_gate(driver="ci-lane")
        command = harness.gate_command(self.root, "managed-tests", gate, self.root / "artifacts/space path",
                                       engine_root="D:/UE Engine", require_clean=True)
        self.assertIn("D:/UE Engine", command)
        self.assertIn("-RequireCleanTree", command)
        self.assertIn("-Lane", command)
        self.assertNotIn("-Command", command)

    @unittest.skipUnless(os.name == "nt" and shutil.which("powershell.exe"), "Windows PowerShell provider")
    def test_windows_powershell_uses_its_own_modules_when_hosted_from_python(self):
        command = [shutil.which("powershell.exe"), "-NoProfile", "-Command",
                   "$ErrorActionPreference='Stop'; Get-Command Get-FileHash,New-TemporaryFile | Select-Object -ExpandProperty Name"]
        before = dict(os.environ)
        with patch.dict(os.environ, {"PSModulePath": str(self.root / "invalid-module-root")}):
            code, status = harness.run_process(command, self.root, self.root / "modules.log", 15)
        self.assertEqual((0, "passed"), (code, status))
        self.assertIn("Get-FileHash", (self.root / "modules.log").read_text())
        self.assertEqual(before, dict(os.environ))

    def test_cli_status_plan_and_invalid_auto_run(self):
        with patch.object(harness, "ROOT", self.root), patch("builtins.print"):
            self.assertEqual(0, harness.main(["status", "--json"]))
            self.assertEqual(0, harness.main(["plan", "--paths", "docs/ROADMAP.md", "--json"]))
            self.assertEqual(2, harness.main(["run", "--profile", "auto"]))
            self.assertEqual(2, harness.main(["run", "--paths", "docs/ROADMAP.md"]))
            self.assertEqual(2, harness.main(["run", "--timeout-seconds", "0"]))
        self.assertFalse((self.root / "artifacts").exists())

    def test_plan_output_keeps_profiles_advisory_and_allows_focused_tests(self):
        plan = harness.make_plan(self.root, self.manifest, paths=["docs/ROADMAP.md"])
        plan["requirements"] = {"one": []}
        plan["commands"] = {"one": [sys.executable, "gate.py"]}
        with patch("builtins.print") as printer:
            harness.print_result("plan", plan, False)
        output = "\n".join(str(call.args[0]) for call in printer.call_args_list)
        self.assertIn("Advisory coverage only", output)
        self.assertIn("focused tests", output)
        self.assertNotIn("Select an explicit profile to run", output)

    def test_cli_success_and_failure_exit_codes(self):
        with patch.object(harness, "ROOT", self.root), patch("builtins.print"):
            self.assertEqual(0, harness.main(["run", "--profile", "quick", "--json"]))
            self.write("gate.py", "raise SystemExit(1)\n")
            self.assertEqual(1, harness.main(["run", "--json"]))


if __name__ == "__main__":
    unittest.main(verbosity=2)
