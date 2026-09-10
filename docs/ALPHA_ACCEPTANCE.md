# Finare alpha acceptance

`tools/run-finare-alpha-acceptance.ps1` is the single repository gate
for the M6.3 adoptable-project claim. It runs the managed suite, repository
invariants, generated-contract onboarding, UE build and Automation, and a real
Win64 packaged lifecycle smoke in one command:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/run-finare-alpha-acceptance.ps1 `
  -EngineRoot D:\UE_5.8
```

The default package configuration is `Development`. Use
`-PackageConfiguration Shipping` for the shipping variant. A focused source/
editor diagnosis may pass `-SkipPackageSmoke`, but that result does not prove
the complete alpha acceptance boundary.

## Phases

1. `preflight` resolves the project, UE 5.8 engine, bundled .NET 10, Python,
   tools, and writable result directory before invoking a build;
2. `managed-tests` restores/builds the solution, runs all tests, and writes a
   TRX result;
3. `repository-validator` checks the versioned source, schema, plugin, SDK,
   template, authoring, and runtime invariants;
4. `unreal-onboarding` regenerates the reflected contract path, builds the UE
   editor twice, verifies deterministic outputs and native coverage, syncs
   Script Assets, and runs the configured Automation filter;
5. `package-smoke` builds, cooks, stages, paks, archives, dependency-checks,
   and launches the packaged game through the CoreCLR Start/Tick/Stop harness.

## Reports and retry UX

Every run writes to `artifacts/acceptance/<timestamp>` by default:

```text
alpha-acceptance.json
alpha-acceptance.md
managed-tests.trx
01-managed-tests.log
02-repository-validator.log
03-unreal-onboarding.log
04-package-smoke.log
onboarding-report.json
package/release-gate.json
```

`alpha-acceptance.json` uses
`unifiedscript.alpha-acceptance/v0.1`. It records source/engine/project identity,
every phase status, duration, exit code, log, produced artifact and safe retry
command, plus test, Automation, native-coverage, package, and smoke metrics.
The Markdown sibling is the concise human summary.

Failure still produces both top-level reports. The terminal error starts with a
stable `USACC` code and names the failed phase, relevant log, generated
artifact, retry command, and report. Successful earlier phases are retained;
the retry command reruns only the owning child gate without deleting source or
local project configuration.
