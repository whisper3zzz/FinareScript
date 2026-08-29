# Unreal sample matrix

UnifiedScript 1.0 RC1 has one versioned catalog for the five product sample
categories. The catalog is
`samples/Unreal/unifiedscript.samples.json`; the source-tree acceptance entry
point is `tools/run-unifiedscript-sample-matrix.ps1`.

The matrix is deliberately stricter than checking whether sample files exist.
Every entry names its source, executable runner, versioned child-report schema,
machine-checked assertions, guarantees, and non-claims. The aggregate runner
archives every child report and log, hashes the evidence, and writes
`unifiedscript.sample-matrix-report/v0.1`.

## Sample coverage

| Category | Sample | Executable proof |
| --- | --- | --- |
| Minimal | `minimal-first-script` | public onboarding, managed/repository gates, 18 UE Automation tests, and Development package smoke |
| Gameplay | `blockbreaker-gameplay` | migrated Character and Gun scripts complete a 300-frame packaged BlockBreaker smoke with zero legacy calls |
| Networking/server | `dedicated-server-lifecycle` | the real World host lifecycle/reload/capacity/shutdown workload passes under Unreal `-server` |
| Data plane | `gameplay-data-plane` | live CoreCLR/UE measurements cover command buffering, batch trace, generated snapshots, and generated events |
| Visual/code | `blockbreaker-visual-code` | visual and C# edits converge through semantic, IL, execution, Script Asset, UE gameplay, and packaged-build gates |

The current networking/server sample proves dedicated-server host behavior. It
does **not** claim client/server gameplay replication or a packaged
`WindowsServer` target. Those require separate targets, replicated gameplay,
and networked multi-process evidence.

## Run the complete matrix

Use clean, isolated worktrees. The BlockBreaker comparison root must contain
clean `BlockBreaker-UnifiedScript`, `BlockBreakerSource`, and
`BlockBreaker-Puerts` repositories.

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-unifiedscript-sample-matrix.ps1 `
  -EngineRoot $env:UNIFIEDSCRIPT_UE_ROOT `
  -ComparisonRoot $env:UNIFIEDSCRIPT_COMPARISON_ROOT
```

By default the runner rejects dirty source or comparison repositories. The
`-AllowDirtySource` and `-AllowDirtyComparison` switches exist only for local
diagnostics; reports still record the complete before/after Git state and fail
if a runner changes it.

The default output is a unique directory below `artifacts/samples`. Its two
top-level artifacts are:

- `sample-matrix.json`: the versioned machine-readable verdict;
- `sample-matrix.md`: a compact human-readable result table.

Each sample subdirectory contains `runner.log` and the immutable
`evidence.json` copy used by the aggregate verdict. A complete release result
must report five passed categories, preserve all four source repositories, and
contain no failed declarative assertion.

Runners that materialize untracked generated outputs declare every removable
path in the catalog. The matrix rejects an undeclared path and only deletes a
declared file below the clean comparison worktree; tracked changes remain the
responsibility of the child runner's own transactional restore.

## Run a focused sample

For investigation, select one or more IDs without weakening their report
contract:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-unifiedscript-sample-matrix.ps1 `
  -EngineRoot $env:UNIFIEDSCRIPT_UE_ROOT `
  -ComparisonRoot $env:UNIFIEDSCRIPT_COMPARISON_ROOT `
  -SampleIds dedicated-server-lifecycle,gameplay-data-plane
```

A focused run sets `completeCatalog=false`; it is useful diagnostic evidence,
not the five-category release acceptance result.

## Source and SDK boundary

The SDK carries the project-agnostic first-script template and the runtime/API
surface needed by host samples. The complete acceptance corpus also depends on
the UnifiedScript source tree and the separately versioned BlockBreaker
comparison workspace. This prevents the SDK from silently republishing the
third-party sample project and keeps comparison revisions explicit in every
report.
