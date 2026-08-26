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
completion events on the next managed frame. The runner prepares both runtimes
once, performs only runtime work inside measured trials, and alternates which
runtime executes first to reduce build heat and ordering bias. The latest local
three-trial result reports 120,027 generated ABI v3 calls, zero legacy calls,
and a median of 3,497.508 ms for UnifiedScript versus 3,901.037 ms for Puerts
(`UnifiedScript/Puerts = 0.896559`). Every trial completed exactly one shot and
one hit, and UnifiedScript observed both command events. This remains a
machine-local UE 5.8.1 result rather than portable cross-machine evidence.
