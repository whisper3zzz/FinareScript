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
  -ComparisonRoot D:\Comparison\UEUnifiedScript
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

## Frozen RC1 acceptance

The strict acceptance run completed on 2026-08-29 from clean source revision
`5be7ddeb767cf2fb67444d5ed93236a40e409764`. Its authoritative report is
`artifacts/release-readiness/m12-final-5be7dde/release-readiness.json`:

- status `passed`, 15/15 required gates, no failed or planned gates;
- 619/619 managed tests, 92/92 repository invariants, and 19/19 UE Automation
  tests;
- Editor Development, source Game Development/Shipping, and SDK-installed Game
  Development rows passed on UE 5.8.1 Win64;
- five sample categories and three project templates passed;
- repeated SDK archives were byte-identical with SHA-256
  `2fdde4bed0f32b525c11d03c838002553ed611e83beabd17363cfb3d3929c7cd`;
- the complete 54-slot, nine-workload comparison qualified at weighted ratio
  0.563208 against the 0.95 maximum;
- packaged BlockBreaker measured 1,033,732,983 bytes versus Puerts
  1,009,756,057 bytes, ratio 1.023745 against the 1.05 maximum.

The source checkout and the frozen UnifiedScript, source, and Puerts comparison
checkouts were clean before and after the run. The Windows PowerShell 5.1 lane
uses long-path-safe package enumeration and snapshot cleanup, and restores the
comparison checkout on both success and failure. This evidence accepts the
local `1.0.0-rc.1` candidate; it does not claim remote publication or support
outside UE 5.8.1 Win64.
