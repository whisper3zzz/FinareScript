# UnifiedScript 1.0 release readiness

`tools/run-unifiedscript-release-readiness.ps1` is the final local acceptance
boundary for `1.0.0-rc.1`. It reads `release/unifiedscript.release.json`, requires
an exact provider for every declared `requiredGate`, executes all providers from
one Git revision, and emits one machine-readable verdict.

## Strict run

Run from a clean UnifiedScript checkout and clean comparison repositories:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/run-unifiedscript-release-readiness.ps1 `
  -EngineRoot D:\UE_5.8 `
  -ComparisonRoot D:\Project\UEUnifiedScriptComparison
```

The command owns these evidence families:

- all managed tests and repository invariants;
- deterministic Contract and data-plane generation;
- UE 5.8.1 Editor build and complete `UnifiedScript.Unreal` Automation;
- source Development and Shipping packages plus packaged smoke;
- two byte-identical SDK archives, clean SDK installation, and a clean-project
  Development package with external .NET roots poisoned;
- all five executable sample categories and all three project templates;
- the complete matched UnifiedScript/Puerts workload budget;
- explicit test and runtime evidence for semantic diff/merge, typed AI
  proposals, Runtime Insight, and generated data-plane extensions.

Success writes `release-readiness.json` with schema
`unifiedscript.release-readiness/v0.1`. Every required gate records its provider,
assertions, and artifact paths. A gate that is absent from the release manifest,
duplicated in the manifest, or missing a provider fails before expensive work.

## Plan and diagnostics

Use `-PlanOnly` to validate the manifest-to-provider contract without running
builds. It emits `status=planned`, never a release verdict.

`-AllowDirtySource` and `-AllowDirtyComparison` exist for investigation only.
A successful diagnostic run emits `passed-local-diagnostics`, which is not
promotion evidence. A strict release verdict is exactly `status=passed`, with
clean source/comparison states preserved and all evidence bound to one revision.

Each phase owns a log and child report below a unique output directory. Retry
into a new directory so old artifacts cannot satisfy a failed run.
