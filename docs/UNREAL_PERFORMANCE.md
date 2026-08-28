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
latency ceiling in `benchmarks/unreal/performance-thresholds.json`. Result v0.3
also records bridge-equivalent time, runtime GC collections/pause, process
memory high-water mark, and managed component startup time for M10.1.

Run the reproducible local gate:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-unreal-benchmark.ps1 `
  -EngineRoot D:\UE_5.8
```

The report is written to `artifacts/ue/interop-benchmark.json`. The reported
latency is the median of five samples. A Puerts result must use
`unifiedscript.unreal-benchmark/v0.2` or v0.3 during the suite migration, the
same `generated-int32-add` scenario,
machine fingerprint, UE engine version, platform, build configuration, warm-up,
iteration count, and sample count, with a positive `puertsNsPerCall`. Supply it with
`-PuertsResult <file> -RequirePuerts`. The competitive gate only passes when
UnifiedScript is at least 5% faster. Without that file the report deliberately
leaves the Puerts result and competitive verdict empty; no performance claim
should be made from an unmatched or cross-machine number.

Pass `-SuiteMetricsPath <file>` to emit the scalar workload's complete M10.1
metric map. Normalize that map with
`comparison/write-workload-capture.ps1`; the capture path rejects a trial whose
manifest slot, runtime identity, or machine/build evidence does not match.

## Matched microbenchmark status

Run both engines and produce the matched comparison artifact with:

```powershell
powershell -ExecutionPolicy Bypass -File comparison/run-performance-comparison.ps1 `
  -EngineRoot D:\UE_5.8
```

The latest local `artifacts/comparison/performance/comparison.json` uses the
same machine, UE 5.8.1 Development Editor configuration, 4,096 warm-up calls,
and five 100,000-call samples. The current formal three-trial suite median is
168.016 ns per UnifiedScript call versus 138.69699 ns per Puerts call, a losing
ratio of 1.211389. UnifiedScript still records zero managed allocation in the
measured loop. This result applies only to the generated
`int32 Add(int32, int32)` boundary; it is published as a loss and the artifacts
are not treated as portable cross-machine evidence.

## BlockBreaker gameplay workload

The product workload runs the same BlockBreaker TutorialMap for 10,000 fixed
frames in both runtimes, drives movement/look every frame, and fires one
midpoint shot. Run the paired, three-trial median comparison with:

```powershell
powershell -ExecutionPolicy Bypass -File comparison/run-gameplay-comparison.ps1 `
  -EngineRoot D:\UE_5.8 -Frames 10000 -WarmupFrames 300 -Trials 3 `
  -WriteSuiteCaptures
```

Run `comparison/measure-blockbreaker-package-size.ps1` first; the gameplay
runner rejects a missing, stale, or commit-mismatched package report. The
current UnifiedScript player routes that shot through one
Batch Trace plus an emitter/damage command reservation and consumes both
completion events on the next managed frame. The runner prepares both runtimes
once, performs only runtime work inside measured trials, alternates which
runtime executes first to reduce build heat and ordering bias, and now runs the
full 74-item migration audit before measurement. The latest local three-trial
result reports zero legacy calls and p50/p95/p99 frame medians of
0.5824/1.0596/1.4033 ms for UnifiedScript versus
0.64795/1.1092/1.4626 ms for Puerts. The ratios are
0.898835/0.955283/0.959456. Bridge time is 175.4974 ms versus 522.2836 ms
(ratio 0.336019), and every trial completed exactly one shot and one hit. All
13 M10.1 metrics are present, but allocation and reload retain runtime-specific
collector semantics. The full 54-slot suite is published; its mixed workload
results remain separate from the future M10.3 weighted claim.
