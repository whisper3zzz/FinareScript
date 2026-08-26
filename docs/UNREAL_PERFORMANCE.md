# Unreal interop performance gate

`tools/run-unreal-benchmark.ps1` runs a live UE 5.8 Development Editor
Automation test against the staged CoreCLR demo. The fixed scenario performs
4,096 warm-up calls followed by five samples of 100,000 generated C# wrapper calls to
`UUnifiedScriptComponent.Add(int32, int32)`. The reported UnifiedScript time
therefore includes the managed wrapper, ABI v3 call frame, native transport,
generation-safe World handle resolution, and generated C++ thunk.

The same process also records a direct C++ loop. That number is an optimized
lower bound, not a substitute for a competing runtime measurement. The gate
requires zero managed bytes in the measured loop and checks the absolute
latency ceiling in `benchmarks/unreal/performance-thresholds.json`.

Run the reproducible local gate:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-unreal-benchmark.ps1 `
  -EngineRoot D:\UE_5.8
```

The report is written to `artifacts/ue/interop-benchmark.json`. The reported
latency is the median of five samples. A Puerts result must use
`unifiedscript.unreal-benchmark/v0.2`, the same `generated-int32-add` scenario,
machine fingerprint, UE engine version, platform, build configuration, warm-up,
iteration count, and sample count, with a positive `puertsNsPerCall`. Supply it with
`-PuertsResult <file> -RequirePuerts`. The competitive gate only passes when
UnifiedScript is at least 5% faster. Without that file the report deliberately
leaves the Puerts result and competitive verdict empty; no performance claim
should be made from an unmatched or cross-machine number.

## Matched microbenchmark status

Run both engines and produce the matched comparison artifact with:

```powershell
powershell -ExecutionPolicy Bypass -File comparison/run-performance-comparison.ps1 `
  -EngineRoot D:\UE_5.8
```

The latest local `artifacts/comparison/performance/comparison.json` uses the
same machine, UE 5.8.1 Development Editor configuration, 4,096 warm-up calls,
and five 100,000-call samples. Its current median is 128.113 ns per
UnifiedScript call versus 143.811 ns per Puerts call, with zero managed
allocation and all matched-workload gates passing. This claim applies only to
the generated `int32 Add(int32, int32)` boundary; artifacts are regenerated
locally and are not treated as portable cross-machine evidence.

## BlockBreaker gameplay workload

The product workload runs the same BlockBreaker TutorialMap for 10,000 fixed
frames in both runtimes, drives movement/look every frame, and fires one
midpoint shot. Run the paired, three-trial median comparison with:

```powershell
powershell -ExecutionPolicy Bypass -File comparison/run-gameplay-comparison.ps1 `
  -EngineRoot D:\UE_5.8 -Frames 10000 -Trials 3
```

The current working-tree UnifiedScript player routes that shot through one
Batch Trace plus an emitter/damage command reservation and consumes both
completion events on the next managed frame. Its latest standalone 10,000-frame
smoke reports 120,027 generated ABI v3 calls and zero legacy calls. However,
`artifacts/comparison/gameplay/comparison.json` was generated before this
data-plane migration and still records 120,030 calls. It remains a valid record
of the earlier facade-free binding baseline, but it is not evidence for the
current data-plane workload. Regenerate the paired artifact before publishing a
new UnifiedScript-versus-Puerts full-gameplay result.
