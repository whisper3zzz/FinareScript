#!/usr/bin/env python3
"""Repository task/evidence orchestrator. Python standard library only."""

from __future__ import annotations

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
from fnmatch import fnmatchcase
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import time
import uuid

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
MANIFEST = "harness/manifest.json"
REPORT_SCHEMA = "unifiedscript.harness-report/v0.1"


class HarnessError(Exception):
    pass


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def read_json(path):
    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise HarnessError(f"Duplicate JSON key: {key}")
            result[key] = value
        return result

    return json.loads(Path(path).read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def contained(root, relative):
    """Resolve links as well as '..'; never allow an input/output to escape."""
    root = Path(root).resolve()
    path = (root / relative).resolve()
    if not path.is_relative_to(root) or path == root:
        raise HarnessError(f"Path must remain below {root}: {relative}")
    return path


def require_keys(value, required, optional=()):
    if not isinstance(value, dict) or set(value) - set(required) - set(optional) or set(required) - set(value):
        raise HarnessError(f"Invalid fields; expected {sorted(required)}, optional {sorted(optional)}")


def strings(value):
    return isinstance(value, list) and all(isinstance(item, str) and item for item in value)


def expand_gates(manifest, requested):
    result, active = [], set()

    def visit(name):
        if name not in manifest["gates"]:
            raise HarnessError(f"Unknown gate: {name}")
        if name in active:
            raise HarnessError(f"Gate dependency cycle at {name}")
        if name in result:
            return
        active.add(name)
        for dependency in manifest["gates"][name]["dependsOn"]:
            visit(dependency)
        active.remove(name)
        result.append(name)

    for name in requested:
        visit(name)
    return result


def load_manifest(root):
    manifest = read_json(contained(root, MANIFEST))
    require_keys(manifest, ("schemaVersion", "documents", "gates", "profiles", "routes"))
    if manifest["schemaVersion"] != "unifiedscript.harness-manifest/v0.1":
        raise HarnessError("Unsupported Harness manifest schema")
    if not strings(manifest["documents"]):
        raise HarnessError("documents must be a list of paths")
    for document in manifest["documents"]:
        if not contained(root, document).is_file():
            raise HarnessError(f"Missing document: {document}")
    if not isinstance(manifest["gates"], dict) or not manifest["gates"]:
        raise HarnessError("No gates declared")
    for name, gate in manifest["gates"].items():
        if not re.fullmatch(r"[a-z][a-z0-9-]*", name):
            raise HarnessError(f"Invalid gate ID: {name}")
        require_keys(gate, ("description", "driver", "script", "args", "dependsOn", "requires",
                            "timeoutSeconds", "mayRegenerateSource"), ("report",))
        if gate["driver"] not in ("python", "ci-lane", "powershell"):
            raise HarnessError(f"Unknown driver for {name}")
        if not isinstance(gate["description"], str) or not gate["description"].strip():
            raise HarnessError(f"Missing description for {name}")
        if not isinstance(gate["script"], str) or not contained(root, gate["script"]).is_file():
            raise HarnessError(f"Missing gate script for {name}")
        for field in ("args", "dependsOn", "requires"):
            if not strings(gate[field]):
                raise HarnessError(f"{name}.{field} must be a string list")
        if set(gate["requires"]) - {"engine", "dotnet", "comparison"}:
            raise HarnessError(f"Unknown tool requirement for {name}")
        if type(gate["timeoutSeconds"]) is not int or gate["timeoutSeconds"] <= 0:
            raise HarnessError(f"Invalid timeout for {name}")
        if type(gate["mayRegenerateSource"]) is not bool:
            raise HarnessError(f"Missing source-write policy for {name}")
        if gate["driver"] != "python" and "report" not in gate:
            raise HarnessError(f"Provider report contract missing for {name}")
        if "report" in gate:
            report = gate["report"]
            require_keys(report, ("path", "schemaVersion", "checks"))
            contained(root, report["path"])
            if not isinstance(report["schemaVersion"], str) or not isinstance(report["checks"], dict):
                raise HarnessError(f"Invalid report contract for {name}")
            if report["checks"].get("status") != "passed":
                raise HarnessError(f"Report must explicitly require passed status for {name}")
    expand_gates(manifest, list(manifest["gates"]))
    if not isinstance(manifest["profiles"], dict) or not {"quick", "precommit"} <= set(manifest["profiles"]):
        raise HarnessError("Quick and conservative precommit profiles are required")
    for name, gates in manifest["profiles"].items():
        if name == "auto" or not strings(gates) or not gates:
            raise HarnessError(f"Invalid profile: {name}")
        expand_gates(manifest, gates)
    if not isinstance(manifest["routes"], list):
        raise HarnessError("routes must be an array")
    for route in manifest["routes"]:
        require_keys(route, ("patterns", "profile", "reason"))
        if not strings(route["patterns"]) or not route["patterns"] or route["profile"] not in manifest["profiles"]:
            raise HarnessError("Invalid change route")
    return manifest


def git(root, *arguments):
    result = subprocess.run(["git", "-C", str(root), *arguments], capture_output=True)
    if result.returncode:
        raise HarnessError(f"Git {' '.join(arguments)} failed: {result.stderr.decode('utf-8', 'replace').strip()}")
    return result.stdout


def changed_entries(raw):
    fields = raw.decode("utf-8", "surrogateescape").split("\0")
    entries, index = [], 0
    while index < len(fields) and fields[index]:
        entry = {"status": fields[index][:2], "path": fields[index][3:]}
        index += 1
        if "R" in entry["status"] or "C" in entry["status"]:
            entry["originalPath"] = fields[index]
            index += 1
        entries.append(entry)
    return entries


def file_digest(path):
    path = Path(path)
    if not path.exists():
        return None
    if not path.is_file():
        raise HarnessError(f"Expected a file, not directory: {path}")
    hasher = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest()


def source_state(root):
    revision = git(root, "rev-parse", "HEAD").decode().strip()
    entries = changed_entries(git(root, "status", "--porcelain=v1", "-z", "--untracked-files=all"))
    # Include index separately: staging a different blob without changing the
    # working file is a different source state as well.
    payload = {
        "revision": revision, "entries": entries,
        "worktree": digest(git(root, "diff", "--binary", "--no-ext-diff", "--no-textconv", "HEAD", "--")),
        "index": digest(git(root, "diff", "--cached", "--binary", "--no-ext-diff", "--no-textconv", "--")),
        "untracked": {entry["path"]: file_digest(contained(root, entry["path"]))
                      for entry in entries if entry["status"] == "??"},
    }
    return {"revision": revision, "dirty": bool(entries), "entries": entries,
            "fingerprint": digest(json.dumps(payload, sort_keys=True).encode())}


def load_local(root):
    path = root / "harness.local.json"
    local = read_json(path) if path.exists() else {}
    require_keys(local, (), ("engineRoot", "protectedPaths"))
    if "engineRoot" in local and not isinstance(local["engineRoot"], str):
        raise HarnessError("engineRoot must be a path string")
    if not strings(local.get("protectedPaths", [])):
        raise HarnessError("protectedPaths must be a list of exact file paths")
    if "protectedPaths" in local:
        local["protectedPaths"] = [contained(root, path).relative_to(root.resolve()).as_posix()
                                   for path in local["protectedPaths"]]
    return local


def make_plan(root, manifest, profile="auto", paths=None, base_ref=None):
    if paths is None:
        entries = changed_entries(git(root, "status", "--porcelain=v1", "-z", "--untracked-files=all"))
        paths = [entry[key] for entry in entries for key in ("path", "originalPath") if key in entry]
        if base_ref:
            # Resolve first so a ref resembling an option cannot become a Git option.
            commit = git(root, "rev-parse", "--verify", "--end-of-options", base_ref + "^{commit}").decode().strip()
            paths += git(root, "diff", "--name-only", "--no-renames", "-z", commit, "HEAD", "--").decode().split("\0")
    normalized = []
    for path in paths:
        if path:
            normalized.append(contained(root, path).relative_to(root.resolve()).as_posix())
    reasons, requested, unknown = [], [], []
    if profile != "auto":
        if profile not in manifest["profiles"]:
            raise HarnessError(f"Unknown profile: {profile}")
        requested.extend(manifest["profiles"][profile])
    else:
        requested.extend(manifest["profiles"]["quick"])
        for path in sorted(set(normalized)):
            matches = [route for route in manifest["routes"]
                       if any(fnmatchcase(path, pattern) for pattern in route["patterns"])]
            if not matches:
                unknown.append(path)
                requested.extend(manifest["profiles"]["precommit"])
            for match in matches:
                requested.extend(manifest["profiles"][match["profile"]])
                reasons.append({"path": path, "profile": match["profile"], "reason": match["reason"]})
    return {"profile": profile, "paths": sorted(set(normalized)), "reasons": reasons,
            "unmappedPaths": unknown, "gates": expand_gates(manifest, requested)}


def gate_command(root, name, gate, output, engine_root=None, comparison_root=None, require_clean=False):
    script = str(contained(root, gate["script"]))
    if gate["driver"] == "python":
        return [sys.executable, "-B", script, *gate["args"]]
    command = [shutil.which("powershell.exe") or "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script]
    if gate["driver"] == "ci-lane":
        command += ["-Lane", name]
        if require_clean:
            command.append("-RequireCleanTree")
    command += ["-OutputDirectory", str(output)]
    if engine_root:
        command += ["-EngineRoot", str(engine_root)]
    if "comparison" in gate["requires"] and comparison_root:
        command += ["-ComparisonRoot", str(comparison_root)]
    return command + gate["args"]


def preflight(root, manifest, plan, source, local, engine_root, comparison_root, require_clean):
    if require_clean and source["dirty"]:
        raise HarnessError("A clean checkout is required; user files will not be reverted")
    gates = [manifest["gates"][name] for name in plan["gates"]]
    if any(gate["driver"] != "python" for gate in gates) and not shutil.which("powershell.exe"):
        raise HarnessError("Windows PowerShell is required for these existing providers")
    requires = {requirement for gate in gates for requirement in gate["requires"]}
    tools = {"python": sys.executable, "pythonVersion": sys.version.split()[0]}
    if "engine" in requires:
        if not engine_root:
            raise HarnessError("This profile requires --engine-root or FINARE_UE_ROOT")
        version = read_json(Path(engine_root) / "Engine/Build/Build.version")
        if not isinstance(version, dict) or [version.get(key) for key in ("MajorVersion", "MinorVersion", "PatchVersion")] != [5, 8, 1]:
            raise HarnessError("Current Unreal provider contract requires UE 5.8.1")
        for relative in ("Engine/Binaries/Win64/UnrealEditor-Cmd.exe", "Engine/Build/BatchFiles/Build.bat"):
            if not (Path(engine_root) / relative).is_file():
                raise HarnessError(f"Missing engine tool: {relative}")
        tools["engineVersion"] = "5.8.1"
    if "dotnet" in requires:
        bundled = Path(engine_root or ".") / "Engine/Binaries/ThirdParty/DotNet/10.0/win-x64/dotnet.exe"
        dotnet = str(bundled) if engine_root and bundled.is_file() else shutil.which("dotnet")
        if not dotnet:
            raise HarnessError("A .NET 10 SDK is required")
        result = subprocess.run([dotnet, "--list-sdks"], capture_output=True, text=True, timeout=30)
        if result.returncode or not any(line.startswith("10.") for line in result.stdout.splitlines()):
            raise HarnessError("The selected dotnet does not expose a .NET 10 SDK")
        tools.update(dotnet=dotnet, dotnetSdks=result.stdout.strip())
    if "comparison" in requires:
        if not comparison_root or not all((Path(comparison_root) / name).is_dir()
                                         for name in ("BlockBreaker-Puerts", "BlockBreaker-Finare")):
            raise HarnessError("Explicit comparison root with both BlockBreaker checkouts is required")
    protected = set(local.get("protectedPaths", []))
    dirty_paths = {entry[key] for entry in source["entries"] for key in ("path", "originalPath") if key in entry}
    if protected & dirty_paths and any(gate["mayRegenerateSource"] for gate in gates):
        raise HarnessError("Source-regenerating gates conflict with dirty protected files; use a clean checkout: "
                           + ", ".join(sorted(protected & dirty_paths)))
    return tools


@contextmanager
def checkout_lock(root):
    path = contained(root, ".cache/finare-harness.lock")
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        stream = path.open("x", encoding="utf-8")
    except FileExistsError as exc:
        raise HarnessError(f"Another run or stale lock exists: {path}; inspect its PID before removing") from exc
    try:
        with stream:
            json.dump({"pid": os.getpid(), "startedAtUtc": utc_now()}, stream)
        yield
    finally:
        path.unlink()


def stop_process_tree(process):
    if os.name == "nt":
        subprocess.run(["taskkill.exe", "/PID", str(process.pid), "/T", "/F"],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                       creationflags=subprocess.CREATE_NO_WINDOW, timeout=30)
    else:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    process.wait(timeout=30)


def child_environment(command):
    environment = {**os.environ, "PYTHONDONTWRITEBYTECODE": "1"}
    if os.name == "nt" and Path(command[0]).name.lower() == "powershell.exe":
        # Python does not perform pwsh's compatibility cleanup when launching
        # Windows PowerShell. Inherited PS7 module roots can shadow the 5.1
        # Utility module and make Get-FileHash/New-TemporaryFile disappear.
        # Reset only this child's search path; never alter the user's process
        # or machine environment. All providers use standard Windows modules.
        for key in list(environment):
            if key.lower() == "psmodulepath":
                del environment[key]
    return environment


def run_process(command, cwd, log_path, timeout):
    options = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {"start_new_session": True}
    with log_path.open("wb") as log:
        process = subprocess.Popen(command, cwd=cwd, stdout=log, stderr=subprocess.STDOUT,
                                   env=child_environment(command), **options)
        started = time.monotonic()
        next_heartbeat = 30
        try:
            while process.poll() is None:
                elapsed = time.monotonic() - started
                if elapsed >= timeout:
                    stop_process_tree(process)
                    return process.returncode, "timed-out"
                if elapsed >= next_heartbeat:
                    print(f"[running {int(elapsed)}s] {log_path}", file=sys.stderr, flush=True)
                    next_heartbeat += 30
                time.sleep(0.1)
        except KeyboardInterrupt:
            stop_process_tree(process)
            return process.returncode, "interrupted"
    return process.returncode, "passed" if process.returncode == 0 else "failed"


def verify_provider(gate, output, source):
    if "report" not in gate:
        return None
    contract = gate["report"]
    path = contained(output, contract["path"])
    document = read_json(path)
    if not isinstance(document, dict) or document.get("schemaVersion") != contract["schemaVersion"]:
        raise HarnessError(f"Wrong provider schema: {path}")
    for field, expected in contract["checks"].items():
        actual = document
        for segment in field.split("."):
            actual = actual.get(segment) if isinstance(actual, dict) else None
        if type(actual) is not type(expected) or actual != expected:
            raise HarnessError(f"Provider {path.name}: {field} must be {expected!r}, got {actual!r}")
    if gate["driver"] == "ci-lane":
        if document.get("sourceRevision") != source["revision"] or document.get("sourceDirty") is not source["dirty"]:
            raise HarnessError("Provider evidence belongs to a different Git source state")
        phases = document.get("phases")
        if not isinstance(phases, list) or not phases or any(
                not isinstance(phase, dict) or phase.get("status") != "passed"
                or type(phase.get("exitCode")) is not int or phase["exitCode"] != 0 for phase in phases):
            raise HarnessError("Provider has missing or failed phases")
        for phase in phases:
            for relative in [phase.get("log", ""), *phase.get("artifacts", [])]:
                if not contained(output, relative).is_file():
                    raise HarnessError(f"Missing declared provider evidence: {relative}")
    return {"path": str(path), "sha256": file_digest(path), "schemaVersion": contract["schemaVersion"]}


def write_report(output, report):
    # Every directory is exclusive to one run; atomic replacement also permits
    # inspecting an honest 'running' report while a long provider is active.
    temporary = output / "report.tmp"
    temporary.write_text(json.dumps(report, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")
    temporary.replace(output / "report.json")
    lines = ["# Harness run", "", f"Status: {report['status']}", f"Profile: {report['profile']}",
             f"Source: {report.get('sourceBefore', {}).get('revision', 'unavailable')}", "",
             "| Gate | Status | Exit | Duration ms | Log |", "| --- | --- | --- | --- | --- |"]
    for gate in report["gates"]:
        lines.append(f"| {gate['id']} | {gate['status']} | {gate.get('exitCode', '')} | {gate.get('durationMs', '')} | {gate.get('log', '')} |")
    if report.get("failure"):
        lines += ["", "Failure: " + report["failure"]]
    lines += ["", "Only the listed gates ran. This is not release acceptance."]
    (output / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def execute(root, manifest, plan, local, engine_root=None, comparison_root=None,
            output=None, require_clean=False, timeout_override=None):
    run_root = contained(root, "artifacts/harness/runs")
    output = contained(run_root, output or (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex[:8]))
    if output.exists():
        raise HarnessError(f"Output directory must not already exist: {output}")
    with checkout_lock(root):
        output.mkdir(parents=True, exist_ok=False)
        started = time.monotonic()
        report = {"schemaVersion": REPORT_SCHEMA, "status": "running", "profile": plan["profile"],
                  "startedAtUtc": utc_now(), "selection": plan,
                  "engineRoot": engine_root, "comparisonRoot": comparison_root,
                  "localConfigSha256": file_digest(root / "harness.local.json"),
                  "gates": [{"id": name, "status": "not-run"} for name in plan["gates"]], "failure": None}
        protected_before = {}
        try:
            source = source_state(root)
            report["sourceBefore"] = source
            protected_before = {path: file_digest(contained(root, path)) for path in local.get("protectedPaths", [])}
            report["tools"] = preflight(root, manifest, plan, source, local, engine_root, comparison_root, require_clean)
            write_report(output, report)
            for record in report["gates"]:
                name = record["id"]
                gate = manifest["gates"][name]
                gate_output = contained(output, name)
                command = gate_command(root, name, gate, gate_output, engine_root, comparison_root, require_clean)
                record.update(status="running", command=command, startedAtUtc=utc_now(), log=name + ".log")
                write_report(output, report)
                print(f"[start] {name}", file=sys.stderr, flush=True)
                gate_started = time.monotonic()
                try:
                    code, status = run_process(command, root, output / record["log"], timeout_override or gate["timeoutSeconds"])
                    record.update(exitCode=code, status=status)
                    if status != "passed":
                        raise HarnessError(f"{name}: {status}; see {output / record['log']}")
                    record["provider"] = verify_provider(gate, gate_output, source)
                except Exception as exc:
                    # Fail closed even if a malformed provider hits an
                    # unexpected decoder error; never leave a passed gate.
                    if record["status"] in ("running", "passed"):
                        record["status"] = "failed"
                    record["failure"] = str(exc)
                    raise
                finally:
                    record["durationMs"] = round((time.monotonic() - gate_started) * 1000)
                print(f"[passed] {name}", file=sys.stderr, flush=True)
            report["status"] = "passed"
        except KeyboardInterrupt:
            report.update(status="interrupted", failure="Interrupted before gate completion")
        except Exception as exc:
            report.update(status="failed", failure=str(exc))
        finally:
            try:
                report["sourceAfter"] = source_state(root)
                report["protectedChanges"] = [path for path, before in protected_before.items()
                                                if before != file_digest(contained(root, path))]
                report["sourceChangedDuringRun"] = report.get("sourceBefore", {}).get("fingerprint") != report["sourceAfter"]["fingerprint"]
                report["localConfigChangedDuringRun"] = report["localConfigSha256"] != file_digest(root / "harness.local.json")
                if report["protectedChanges"] or report["sourceChangedDuringRun"] or report["localConfigChangedDuringRun"]:
                    report["status"] = "failed"
                    report["failure"] = (report["failure"] or "") + " Source/protected files/local config changed during the run; review, then rerun. No files were reverted."
            except Exception as exc:
                report.update(status="failed", failure=f"Could not verify final source state: {exc}")
            for record in report["gates"]:
                if record["status"] == "running":
                    record["status"] = "interrupted"
            report.update(completedAtUtc=utc_now(), durationMs=round((time.monotonic() - started) * 1000))
            write_report(output, report)
    return report, output


def repository_status(root):
    source = source_state(root)
    roadmap = (root / "docs/ROADMAP.md").read_text(encoding="utf-8")
    cursor = [line for line in roadmap.splitlines() if line.startswith(("- Active milestone:", "- Current tranche:"))]
    plans = []
    for path in sorted((root / "docs/plans").glob("*.md")):
        status = re.search(r"^Status: (planned|in-progress|blocked|complete)\s*$", path.read_text(encoding="utf-8"), re.MULTILINE)
        if status and status[1] != "complete":
            plans.append({"path": path.relative_to(root).as_posix(), "status": status[1]})
    reports = sorted((root / "artifacts/harness/runs").glob("*/report.json"), key=lambda path: path.stat().st_mtime_ns, reverse=True)
    latest = None
    if reports:
        path = reports[0]
        try:
            report = read_json(path)
            if not isinstance(report, dict) or report.get("schemaVersion") != REPORT_SCHEMA:
                raise HarnessError("Unsupported report schema")
            matches = (report.get("sourceBefore", {}).get("fingerprint") == source["fingerprint"]
                       and not report.get("sourceChangedDuringRun", True)
                       and report.get("localConfigSha256") == file_digest(root / "harness.local.json"))
            latest = {"path": str(path), "status": report["status"], "profile": report["profile"],
                      "freshness": "matches-source" if matches else "stale",
                      "gates": [{"id": gate["id"], "status": gate["status"]} for gate in report["gates"]]}
        except (HarnessError, OSError, ValueError, KeyError) as exc:
            latest = {"path": str(path), "status": "invalid", "failure": str(exc)}
    return {"source": source, "roadmapCursor": cursor, "taskPlans": plans, "latestRun": latest,
            "note": "Source match is not a fresh run or a toolchain/external-project verification."}


def print_result(action, result, as_json):
    if as_json:
        print(json.dumps(result, indent=2, ensure_ascii=True))
        return
    if action == "status":
        source = result["source"]
        print(f"HEAD {source['revision'][:12]} | {'dirty' if source['dirty'] else 'clean'} | {len(source['entries'])} changed paths")
        print("\n".join(result["roadmapCursor"]))
        for plan in result["taskPlans"]:
            print(f"Task plan: {plan['status']} | {plan['path']}")
        latest = result["latestRun"]
        if latest:
            print(f"Latest Harness: {latest['status']} | {latest.get('profile', '?')} | {latest.get('freshness', 'invalid')}")
            print(latest["path"])
            for gate in latest.get("gates", []):
                print(f"  {gate['id']}: {gate['status']}")
        else:
            print("No Harness report yet. Historical Roadmap evidence was not replayed.")
        print(result["note"])
    elif action == "plan":
        print(f"Profile: {result['profile']} | scoped paths: {len(result['paths'])}")
        for reason in result["reasons"]:
            print(f"  {reason['path']} -> {reason['profile']}: {reason['reason']}")
        if result["unmappedPaths"]:
            print("Unmapped paths (precommit fallback; manually review UE/package risk): " + ", ".join(result["unmappedPaths"]))
        for name in result["gates"]:
            print(f"{name} | requires: {', '.join(result['requirements'][name]) or 'Python/Git only'}")
            print("  & " + " ".join("'" + arg.replace("'", "''") + "'" for arg in result["commands"][name]))
        print("Advisory coverage only: no gates executed. Use relevant focused tests or choose a profile when its full coverage is needed.")
    else:
        print(f"Harness {result['profile']}: {result['status']}")
        print(result["report"])
        if result.get("failure"):
            print(result["failure"])


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("status", "plan", "run"))
    parser.add_argument("--profile")
    selection = parser.add_mutually_exclusive_group()
    selection.add_argument("--paths", nargs="+")
    selection.add_argument("--base-ref")
    parser.add_argument("--engine-root")
    parser.add_argument("--comparison-root")
    parser.add_argument("--output", help="New run directory name or absolute path below artifacts/harness/runs")
    parser.add_argument("--require-clean", action="store_true")
    parser.add_argument("--timeout-seconds", type=int)
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    try:
        manifest = load_manifest(ROOT)
        local = load_local(ROOT)
        engine = args.engine_root or os.environ.get("FINARE_UE_ROOT") or local.get("engineRoot")
        comparison = args.comparison_root or os.environ.get("FINARE_COMPARISON_ROOT")
        engine = str(Path(engine).resolve()) if engine else None
        comparison = str(Path(comparison).resolve()) if comparison else None
        if args.timeout_seconds is not None and args.timeout_seconds <= 0:
            raise HarnessError("Timeout must be positive")
        if args.action != "plan" and (args.paths is not None or args.base_ref):
            raise HarnessError("--paths/--base-ref are plan-only; run requires an explicit profile")
        if args.action == "status":
            result = repository_status(ROOT)
        else:
            profile = args.profile or ("auto" if args.action == "plan" else "quick")
            if args.action == "run" and profile == "auto":
                raise HarnessError("Review plan, then select an explicit run profile")
            plan = make_plan(ROOT, manifest, profile, args.paths, args.base_ref)
            if args.action == "plan":
                plan["commands"] = {name: gate_command(ROOT, name, manifest["gates"][name],
                                                      ROOT / "artifacts/harness/runs/<new-run>" / name,
                                                      engine, comparison, args.require_clean) for name in plan["gates"]}
                plan["requirements"] = {name: manifest["gates"][name]["requires"] for name in plan["gates"]}
                result = plan
            else:
                result, output = execute(ROOT, manifest, plan, local, engine, comparison,
                                         args.output, args.require_clean, args.timeout_seconds)
                result = {"status": result["status"], "profile": profile, "report": str(output / "report.json"),
                          "failure": result["failure"]}
        print_result(args.action, result, args.json)
        return 1 if args.action == "run" and result["status"] != "passed" else 0
    except (HarnessError, OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError) as exc:
        print(json.dumps({"status": "error", "failure": str(exc)}), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
