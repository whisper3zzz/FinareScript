# Representative performance workload suite

M10 measures UnifiedScript and Puerts with one versioned end-to-end contract.
The checked-in manifest is
`benchmarks/unreal/workload-suite.v0.1.json`; changing a recipe, asset set,
iteration count, build configuration, or required metric is a suite-version
change and produces a different SHA-256 manifest digest.

The first suite contains exactly one workload for each representative family:

1. scalar/property calls;
2. struct/container transfer;
3. delegate storms;
4. per-frame Actor logic;
5. mass entities;
6. batch queries;
7. resource/load/spawn/destroy flows;
8. collectible hot reload;
9. the complete 10,000-frame BlockBreaker TutorialMap migration.

The manifest pins UE 5.8.1, Windows Editor, Development Editor, NullRHI, a
60 Hz fixed timestep, warm-up/measured iterations, recipe and asset digests,
and a build fingerprint. It also assigns weights for the later M10.3 broad
performance gate. M10.1 does not reinterpret those weights as a claim.

## Evidence schemas

- `unifiedscript.performance-workload-suite/v0.1` describes frozen work.
- `unifiedscript.performance-engine-result/v0.1` carries one runtime's trials.
- `unifiedscript.performance-comparison/v0.1` is emitted only for matched
  UnifiedScript and Puerts inputs.

Every trial has all metric keys. A value is either a finite non-negative
measurement or explicit JSON `null` when that metric is not applicable to the
workload. The manifest's `requiredMetrics` may never be null. Across the suite,
the required sets cover frame median/p95/p99, bridge time, calls, bytes copied,
allocation, runtime GC collection count/pause time, memory high-water mark,
startup, reload, and package size. Frame percentiles must be monotonic.

This distinction prevents a scalar-call benchmark from inventing a frame time,
while requiring the complete BlockBreaker workload to publish every M10.1
metric.

`allocatedBytes` currently has different collector semantics: UnifiedScript
uses `GC.GetAllocatedBytesForCurrentThread()` around the measured interval,
while the Puerts/V8 collector reports the non-negative change in V8 used heap.
Both raw values remain useful diagnostics, but they are not equivalent
allocation measurements. The comparison therefore publishes both medians with
a null ratio and excludes this metric from performance claims until both
runtimes expose the same cumulative-allocation method. GC count and pause time
are observed directly on both runtimes and remain comparable.

The complete BlockBreaker reload values also have different scopes. Puerts
measures synchronous module-cache reexecution through `forceReload`; its full
source/HMR path is not used because it is not shutdown-safe in this UE 5.8.1
fixture. UnifiedScript measures a full ScriptComponent generation reload. Both
values prove the collectors execute, but their ratio is not a reload-performance
claim until one matched lifecycle recipe is available.

## Matched execution

The schedule is deterministic and contains 54 slots: nine workloads times
three trials times two runtimes. Odd trials run UnifiedScript then Puerts for
each workload; even trials reverse the order. Each engine result records the
global execution ordinal. The comparison rejects a missing/duplicate/failed
trial or any ordinal that does not match this alternating schedule.

Each result includes the operating system, CPU, logical processor count,
physical memory, GPU, engine version/build ID, platform, configuration, RHI,
and build fingerprint. The machine fingerprint is derived from the normalized
machine descriptor. Both results must independently validate and their complete
environment records must then be identical. Runtime version/commit are kept
outside that shared record because the compared runtimes are intentionally
different.

Runtime-specific collectors first write a small metrics document containing
the complete `values` map. `comparison/write-workload-capture.ps1` adds the
actual OS/CPU/memory/GPU and engine build identity, derives the slot ordinal
from the manifest, and sends the raw v0.1 measurement through the CLI. The CLI
copies recipe/asset/build facts from the manifest instead of trusting a runner
to repeat them and emits one independently verifiable
`unifiedscript.performance-trial-capture/v0.1` file.

`comparison/run-performance-comparison.ps1 -SuiteTrial <1..3>` runs the matched
scalar pair and immediately writes both normalized captures. Its default mode
publishes neutral or losing evidence while leaving `gates.fasterThanPuerts`
truthful; pass `-RequireWin` when the narrow scalar threshold must fail the run.
Odd trials measure UnifiedScript before Puerts, then reuse the saved
UnifiedScript measurement to merge the comparison report without a third engine
run; even trials measure Puerts before UnifiedScript.
Tracked dirty runtime sources are included in `runtimeCommit` as a stable
worktree fingerprint rather than being mislabeled as a clean commit.

Run or resume the entire manifest with one command:

```powershell
.\comparison\run-complete-workload-suite.ps1
```

The orchestrator checks both captures against the current product-source
identity, machine/engine descriptors, exact trial, and scheduled ordinal before
skipping a pair. Stale or missing pairs are rerun in manifest order; the first
interop pair builds both targets and later pairs reuse them, while the complete
BlockBreaker path independently synchronizes and builds the current plugin. Use
`-PlanOnly` to inspect the 27-pair plan, or narrow a resumable pass with
`-WorkloadId <id> -Trial <1..3>`. Once all pairs are current, the command
assembles both 27-capture results and publishes the matched comparison.

After all 27 captures for one runtime exist, assemble them with:

```powershell
dotnet src/UnifiedScript.Cli/bin/Release/net10.0/UnifiedScript.Cli.dll `
  ue-workload-assemble `
  --manifest benchmarks/unreal/workload-suite.v0.1.json `
  --captures artifacts/comparison/suite/unifiedscript/captures `
  --engine unifiedScript `
  --out artifacts/comparison/suite/unifiedscript.json
```

Assembly rejects mixed commits or environments as well as incomplete,
duplicate, failed, or misordered captures. This makes every process invocation
resumable without weakening the final matched-result gate.

Compare two complete engine results with:

```powershell
dotnet run --project src/UnifiedScript.Cli -- ue-workload-compare `
  --manifest benchmarks/unreal/workload-suite.v0.1.json `
  --unified artifacts/comparison/suite/unifiedscript.json `
  --puerts artifacts/comparison/suite/puerts.json `
  --out artifacts/comparison/suite/comparison.json
```

The command publishes ratios even when UnifiedScript is neutral or slower. It
does not fail a losing performance ratio; M10.3 owns reviewed budgets and the
weighted at-least-5% product gate. It does fail malformed or unmatched evidence
with data error exit code 65.

## Current integration boundary

The current local UE 5.8.1 run completed the full contract: nine workloads,
three trials, two runtimes, and all 54 execution ordinals. Both 27-trial engine
results use machine fingerprint
`sha256:fc848657...2c96512`, engine build fingerprint
`sha256:d35d3d5f...f7a2997`, and the frozen runtime source identities
`sha256:73607b...a7a76b` (UnifiedScript) and
`sha256:4e5f66...76cf0d` (Puerts). The final comparison reports matched
environment, matched workloads, and alternating schedule.

Representative three-trial medians are:

| Workload | Comparison metric | UnifiedScript | Puerts | U/P ratio |
| --- | --- | ---: | ---: | ---: |
| Scalar/property, 100,000 calls | bridge ms | 16.8016 | 13.869699 | 1.211389 |
| Struct/container, 16,384 calls | bridge ms | 448.6599 | 66.622298 | 6.734380 |
| Four-argument delegate, 100,000 dispatches | bridge ms | 220.2320 | 57.947699 | 3.800531 |
| Per-frame Actor, 10,000 frames | frame p50 ms | 0.0060 | 0.0175 | 0.342857 |
| 1,000 entities x 1,000 frames | frame p50 ms | 0.2286 | 0.65995 | 0.346390 |
| Batch Trace, 1,024 queries | bridge ms | 0.440400 | 1.331002 | 0.330878 |
| Resource load/spawn/destroy, 256 rounds | bridge ms | 9.6211 | 5.857199 | 1.642611 |
| Hot reload, 100 generations | reload ms | 450.698502 | 77.618301 | 5.806601* |
| Complete BlockBreaker, 10,000 frames | frame p50 ms | 0.5824 | 0.64795 | 0.898835 |

`*` Hot reload is retained as a diagnostic, not a latency claim: collectible
CLR assembly generations and Puerts module-cache reexecution do not implement
the same lifecycle mechanism.

Complete BlockBreaker also records p95/p99 ratios of 0.955283/0.959456 and a
bridge-time ratio of 0.336019. Its generated data plane uses 10,000 calls and
40,000 logical copied bytes versus Puerts at 50,000 and 200,000. UnifiedScript
records zero runtime GC collections in the measured window, while Puerts records
eight. Memory high-water marks are effectively equal (ratio 0.998727).
UnifiedScript startup is 5.13% slower and its commit-matched Development package
is 1,032,716,821 bytes versus 1,009,698,201 bytes (ratio 1.022798).

Allocation medians remain published without a ratio because the CLR and V8
collectors have different semantics. These mixed results close M10.1 while
keeping the M10.3 weighted-product claim unset. The largest actionable direct
boundary losses are struct/container and delegate traffic, so M10.2 starts with
generated shared container snapshots and buffered event ingress while retaining
the direct generated ABI as a safe fallback.

M10.2 follow-up evidence is deliberately separate from the frozen v1.0.0 suite.
The live-CoreCLR data-plane profile v0.3 runs 1,024 rows with three median
samples in one UE 5.8.1 World. Matched direct Array+Map binding measured
25,371.973 ns/row; the schema-generated fixed-container snapshot measured
1.270 ns/row (ratio 0.000050), and generated event-buffer iteration measured
0.586 ns/row. The two borrowed-view loops allocated zero managed bytes. These
numbers validate the new routing mechanism; they do not rewrite the historical
M10.1 captures or establish the future M10.3 weighted product verdict.
