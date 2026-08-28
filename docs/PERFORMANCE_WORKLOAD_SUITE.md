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

The live scalar, struct/container, four-argument delegate, per-frame Actor,
1,000-entity, Batch Trace, resource/spawn, and hot-reload benchmarks now emit
complete metric maps and have passed the raw-measurement/capture path on both runtimes.
All three matched
scalar trials followed the alternating schedule. Their median bridge times for
100,000 calls were 14.3025 ms for UnifiedScript and 13.8434 ms for Puerts, a
losing UnifiedScript ratio of 1.0332 that remains published without moving the
future product gate. The Batch Trace trial-1 collector recorded 1,024 queries
through two UnifiedScript boundary calls in 0.3950 ms with 360,448 transferred
bytes and zero measured allocation/GC.

The frame driver uses an actual UE ticker and keeps the result-publication tick
outside the measured window. Its first machine-local 10,000-frame Actor proof
recorded 0.0048/0.0090/0.0143 ms p50/p95/p99 and 55.8038 ms total for
UnifiedScript, versus 0.0152/0.0237/0.0388 ms and 165.4386 ms for Puerts. The
1,000-entity by 1,000-frame proof issued 1,001,000 boundary calls and transferred
72,004,000 logical bytes; UnifiedScript recorded 222.7079 ms with zero managed
allocation/GC, while Puerts recorded 727.1043 ms and 41 V8 collections. These
are collector proofs, not a complete suite comparison.

The resource/spawn collector calls the product `UnifiedScriptWorldLibrary`
generated binding rather than a gameplay-specific facade. Each measured round
loads `/Script/Engine.Actor`, spawns it with an identity transform, observes the
returned handle, and destroys it. Trial 1 completed all 256 rounds and 768
boundary calls in 6.7588 ms for UnifiedScript and 13.702597 ms for Puerts, with
the same 40,192-byte logical payload and checksum. The captures occupy schedule
ordinals 13/14.

The hot-reload collector executes 2 warm-ups followed by 100 complete runtime
generation lifecycles. UnifiedScript loads and activates a collectible assembly
generation, stops and retires the previous generation, then forces reclamation;
Puerts reparses/reexecutes the gameplay module, retires its previous exports,
and synchronously asks V8 to collect. Trial 1 recorded 1,549.0686 ms,
20,404,944 allocated bytes, 1,212 CLR collection-count increments, and
47.070 ms GC pause for UnifiedScript. Puerts recorded 67.245603 ms, 19,744 bytes,
200 V8 GC callbacks, and 46.427611 ms GC pause. These values remain diagnostic:
collectible assembly reload and JavaScript module-cache reexecution are not the
same mechanism, so the suite does not publish bridge/call/byte ratios for this
workload. The captures occupy ordinals 15/16.

The BlockBreaker runner now consumes a separately produced, commit-matched
Win64 Development package-size report, captures exact UE CSV frame/memory/GC
windows after 300 warm-up frames, records both runtime envelopes, and publishes
all 13 required metrics. Its three local captures occupy ordinals 17/18, 35/36,
and 53/54. The 10,000-frame medians were 4,599.308 ms for UnifiedScript and
4,957.016 ms for Puerts (ratio 0.927838); frame-time medians were 0.4222 ms and
0.44745 ms (ratio 0.943569). Per-trial elapsed ratios of 0.962756, 0.921757, and
0.884789 expose run variance. The matched UnifiedScript package is 2.08% larger.
This is complete-game evidence, not a broad suite result.

All nine collectors are now executable. The next M10.1 slice recaptures all
nine workloads from one frozen runtime source identity. Until both runtimes
complete 27 trials under the 54-slot schedule, no broad performance result is published.
