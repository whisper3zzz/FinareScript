# Continuous integration lanes

UnifiedScript uses seven independent release-candidate lanes. The local entry
point and GitHub Actions call the same command:

```powershell
tools/run-ci-lane.ps1 -Lane <lane> -RequireCleanTree
```

For a task-oriented view, [Engineering Harness](HARNESS.md) adds source status,
change-to-gate planning, and unified evidence without replacing these providers.
The managed job first runs `python -B tools/harness.py run --profile quick --require-clean --output ci-quick`;
its behavioral tests cover the orchestrator's failure/staleness/ownership rules.
Its reports are uploaded alongside the managed lane. Existing seven-lane and
release-readiness acceptance remains intact.

Every invocation performs preflight before creating outputs, requires a
resolvable Git revision, optionally rejects any tracked or untracked working
tree change, runs into an empty output directory, and writes
`unifiedscript.ci-lane/v0.1` even when preflight or a child command fails. The
report records the revision, dirty flag, phase status, duration, exit code, and
relative artifact paths. Detailed child output remains in per-phase logs.

## Lane matrix

| Lane | Runner | Owned gate |
| --- | --- | --- |
| `managed-tests` | GitHub-hosted Windows | Debug `dotnet test` plus repository validator; Debug matches the ProjectLoader default configuration |
| `deterministic-generation` | GitHub-hosted Windows | Build CLI once; generate bindings, native thunks, node catalog, coverage, and both data-plane layouts twice; compare both runs and checked-in files |
| `ue-build` | dedicated UE runner | Managed Release build plus `UEUnifiedScriptHostEditor` Win64 Development build |
| `unreal-automation` | dedicated UE runner | Validate-only onboarding, deterministic generation, UE build, deployment validation, managed startup, and all `UnifiedScript.Unreal` Automation tests |
| `package-smoke` | dedicated UE runner | Development Build/Cook/Stage/Pak/Archive, plugin-local CoreCLR closure, and packaged managed gameplay smoke |
| `workload-performance` | dedicated comparison runner | Complete 54-slot matched UnifiedScript/Puerts suite and reviewed performance budgets |
| `sdk-installation` | dedicated UE runner | Exact UE 5.8.1 compatibility matrix, deterministic SDK archive, clean project installation, package, poisoned external .NET roots, and runtime smoke |

The first two lanes run for pull requests, `main` pushes, schedules, and manual
runs. UE lanes do not run untrusted pull-request code on a persistent machine;
they run for `main`, scheduled, and manually dispatched workflows. Each matrix
entry gets a fresh checkout and uploads its own report directory, so one lane
cannot satisfy another lane with an earlier job's binaries.

Only JSON/Markdown/text logs and TRX evidence are uploaded. Cooked content,
archives, SDK ZIPs, native binaries, and other large executable payloads stay on
the runner; their release reports retain the required sizes and hashes.

The managed job also runs the release-readiness command with `-PlanOnly`. This
does not claim product acceptance; it fails quickly when the release manifest
adds, removes, duplicates, or renames a required gate without updating the
versioned provider contract. A strict 1.0 verdict still requires the complete
clean-checkout command documented in `RELEASE_READINESS.md`.

## Hosted runner requirements

The two lightweight jobs install the exact SDK from `global.json` (`10.0.201`)
on `windows-latest`. Heavy jobs require a self-hosted runner carrying all labels
below:

```text
self-hosted, Windows, X64, ue-5.8.1
```

The runner must expose:

- `UNIFIEDSCRIPT_UE_ROOT`: an exact UE 5.8.1 installed build with bundled .NET
  10 under `Engine/Binaries/ThirdParty/DotNet/10.0/win-x64`;
- `UNIFIEDSCRIPT_COMPARISON_ROOT`: the prepared comparison root containing
  `BlockBreaker-Puerts` and `BlockBreaker-UnifiedScript`; required by the
  workload lane;
- Git, Windows PowerShell, and Python on `PATH`;
- enough disk for parallel UE intermediates and packaged archives.

The workflow uses the Node 24 generations of checkout, .NET setup, and artifact
upload. A self-hosted Actions runner must therefore be version `2.327.1` or
newer. Keep only one heavy job per physical checkout volume if Unreal or the
comparison fixtures cannot safely build concurrently.

## Local focused runs

Lightweight lanes can use a system .NET 10 SDK or the UE-bundled SDK:

```powershell
tools/run-ci-lane.ps1 `
  -Lane managed-tests `
  -EngineRoot D:\UE_5.8 `
  -OutputDirectory artifacts\ci\managed-tests

tools/run-ci-lane.ps1 `
  -Lane deterministic-generation `
  -EngineRoot D:\UE_5.8 `
  -OutputDirectory artifacts\ci\deterministic-generation
```

A dedicated Unreal runner uses the same shape:

```powershell
tools/run-ci-lane.ps1 `
  -Lane unreal-automation `
  -EngineRoot D:\UE_5.8 `
  -OutputDirectory artifacts\ci\unreal-automation `
  -RequireCleanTree
```

Use a new output directory for each retry. The runner intentionally refuses a
non-empty output root so stale reports cannot turn a failed retry green.

## Failure ownership

- `preflight` means the checkout, toolchain, engine, comparison root, or output
  isolation is invalid. Repair the runner before diagnosing product code.
- A named child phase owns its `.log` and declared child report. Start from that
  report instead of rerunning the full matrix.
- Generation drift is a product failure even when both generated files compile.
  Regenerate the whole contract-owned C#/C++/node/coverage set.
- Managed tests intentionally build Debug because ProjectLoader's default
  evaluation resolves Debug project references. Release compilation remains an
  independent requirement in generation, Unreal build, package, and SDK lanes.
- Performance only passes through the versioned matched workload budget report;
  a microbenchmark, resumable partial suite, or unmatched machine result cannot
  substitute for the lane.
- SDK installation is intentionally independent of source-tree plugin state and
  must remain green in a project initially containing no UnifiedScript plugin.
