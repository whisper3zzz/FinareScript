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

The live UnifiedScript scalar and Batch Trace benchmarks now emit complete
metric maps and have passed the raw-measurement/capture path for their trial-1
slots. The first local samples recorded 100,000 scalar calls in 14.4818 ms with
zero measured allocation/GC, and 1,024 queries through two batch-boundary calls
in 0.3950 ms with 360,448 transferred bytes and zero measured allocation/GC.
These are machine-local collector proofs, not a suite comparison.

The pre-existing BlockBreaker three-trial runner remains valid narrow evidence
but predates the complete metric contract. The next M10.1 slice adds collectors
for the remaining workloads and adapts the Puerts and complete-game runners.
Until both runtimes complete 27 trials under the 54-slot schedule, no broad performance result is published.
