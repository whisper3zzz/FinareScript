# Unreal interop performance gate

`tools/run-unreal-benchmark.ps1` runs a live UE 5.8 Development Editor
Automation test against the staged CoreCLR demo. The fixed scenario performs
4,096 warm-up calls followed by five samples of 100,000 generated C# wrapper calls to
`UFinareComponent.Add(int32, int32)`. The reported Finare time
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
Finare is at least 5% faster. Without that file the report deliberately
leaves the Puerts result and competitive verdict empty; no performance claim
should be made from an unmatched or cross-machine number.

## Generated channel optimization proof

`tools/run-unreal-data-plane-profile.ps1` now emits
`unifiedscript.unreal-data-plane-profile-report/v0.3`. In addition to command
and query batches, it publishes 1,024 schema-generated fixed-container rows and
1,024 buffered gameplay-event rows before the live CoreCLR workload starts.
The managed script compares the snapshot against direct generated Array+Map
binding, consumes both channels through borrowed `ReadOnlySpan<T>` views, and
requires zero managed allocation.

The 2026-08-28 UE 5.8.1 run measured direct container binding at 25,371.973
ns/row, generated snapshot iteration at 1.270 ns/row (ratio 0.000050), and
generated event iteration at 0.586 ns/row. This is a route-specific M10.2 result:
stable, high-volume containers and events can use generated channels, while
low-volume or changing contracts stay on direct generated binding. It does not
replace the frozen matched Puerts suite or satisfy M10.3 by itself.

M10.3 converts that route-level proof into suite v1.1.0 without rewriting the
checked-in v1.0.0 manifest. The fixed-container recipe keeps 16,384 rows; the
event recipe uses the declared 65,536-entry channel bound on both runtimes.
Three alternating trials produced 0.1118 ms versus 64.0493 ms for containers
(ratio 0.001746), and 0.2813 ms versus 36.2080 ms for events (ratio 0.007769).
Every Finare trial reports one acquisition, zero logical boundary-copy
bytes, and zero managed allocation. The complete 54-slot suite qualifies at a
weighted ratio of 0.555769 versus the 0.95 maximum, with every absolute
regression gate passing.

The reviewed policy lives in
`benchmarks/unreal/performance-budgets.v1.1.0.json`. It selects one primary
metric per comparable workload, excludes hot reload with an explicit mechanism
reason, computes a normalized weighted arithmetic ratio, and requires a result
at or below 0.95 for the broad claim. Absolute ABI, data-channel, BlockBreaker
tail, bridge, and package ceilings are version-controlled regression gates.
Neutral and losing results are written with `status: not-qualified`; they are
not suppressed or converted into a passing claim.

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
147.766 ns per Finare call versus 143.339 ns per Puerts call, a losing
ratio of 1.030885. Finare still records zero managed allocation in the
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
current Finare player routes that shot through one
Batch Trace plus an emitter/damage command reservation and consumes both
completion events on the next managed frame. The runner prepares both runtimes
once, performs only runtime work inside measured trials, alternates which
runtime executes first to reduce build heat and ordering bias, and now runs the
full 74-item migration audit before measurement. The latest local three-trial
result reports zero legacy calls and p50/p95/p99 frame medians of
0.4340/0.8549/1.2585 ms for Finare versus
0.4740/0.9416/1.3735 ms for Puerts. The ratios are
0.915612/0.907923/0.916272. Bridge time is 122.1483 ms versus 328.9069 ms
(ratio 0.371377), and every trial completed exactly one shot and one hit. All
13 M10.1 metrics are present, but allocation and reload retain runtime-specific
collector semantics. Together with the other matched workloads and refreshed
package sizes, this is the qualified M10.3 broad-suite verdict.
