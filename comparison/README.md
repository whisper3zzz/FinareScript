# Puerts / UnifiedScript comparison

The comparison uses the same BlockBreaker map, art, input mapping, Unreal Engine
5.8.1 installation, and 300-tick headless smoke window. The two worktrees live
under `D:\\Project\\UEUnifiedScriptComparison`:

- `BlockBreaker-Puerts`: original TypeScript gameplay on Puerts/V8.
- `BlockBreaker-UnifiedScript`: migrated C# gameplay on UnifiedScript/CoreCLR.

Pinned repositories and commits are recorded in `baselines.json`. Run both
functional gates and produce an ignored JSON report with:

The external BlockBreaker checkout and its historical reports still use the old
UnifiedScript paths and identifiers. The Finare rename does not rewrite those
external inputs or claim that their recorded measurements were rerun; migrate
that checkout separately before comparing new Finare measurements.

```powershell
.\\comparison\\run-smoke-comparison.ps1
```

The current gate proves engine compatibility, map loading, script startup,
managed lifecycle, 300 ticks, the UnifiedScript shot/hit path, and generated
ABI v3 dispatch with nonzero fast calls and zero legacy calls. Its coverage
report also requires every exported BlockBreaker member to have a native thunk.

Before either smoke or gameplay measurement, the runners execute the complete
migration audit. It can also be run directly:

```powershell
.\comparison\audit-blockbreaker-migration.ps1
```

The audit discovers 74 Puerts-project behaviors and requires exact coverage,
19 byte-identical non-map gameplay assets, exactly two UnifiedScript Script
Assets, 40 generated native thunks, and no Puerts/TypeScript legacy target.

First produce the matched Win64 Development delivery-size evidence. The report
is commit-bound and reused by the editor workload instead of mixing packaging
into a timed trial:

```powershell
.\comparison\measure-blockbreaker-package-size.ps1
```

Then run the complete migrated gameplay flow with 300 warm-up frames, three
exact 10,000-frame trials, median aggregation, and normalized M10.1 captures:

```powershell
.\comparison\run-gameplay-comparison.ps1 -WriteSuiteCaptures
```

The gameplay runner first builds both runtimes and synchronizes the BlockBreaker
deployment manifest into target-class-checked Player and Rifle
`UUnifiedScriptAsset` packages. Both
scripts must start through those assets, all 19 non-map business assets must be
byte-identical, the midpoint shot must hit exactly once, and UnifiedScript must
report zero legacy calls. Per-trial logs and the aggregate report are written
under `artifacts/comparison/gameplay`. Compilation, generation, packaging, and
warm-up are outside the measured window. Odd trials run UnifiedScript first and
even trials run Puerts first. Standardized complete-game captures occupy
ordinals 17/18, 35/36, and 53/54 in the 54-slot representative suite.

The migrated shot is no longer a native Gun-facade shortcut. Resource/class
loading and Actor spawn remain on the generated Binding control plane. The hot
shot path uses one Gameplay Data Batch Trace, commits emitter and damage rows in
one command reservation, and consumes their completion events on the next
managed frame. The current formal three-trial medians are 0.5824/1.0596/1.4033
ms p50/p95/p99 for UnifiedScript and 0.64795/1.1092/1.4626 ms for Puerts,
ratios of 0.898835/0.955283/0.959456. UnifiedScript bridge time is 175.4974 ms
versus 522.2836 ms (ratio 0.336019), using 10,000 calls and 40,000 logical bytes
instead of 50,000 and 200,000. Every run requires one shot/hit and zero legacy
calls. The matched packages are 1,032,716,821 and 1,009,698,201 bytes
respectively, making the current UnifiedScript delivery 2.28% larger.

All 13 complete-workload metrics are present. Allocation remains diagnostic
only because CLR cumulative allocation and V8 used-heap growth are different
collectors. Reload also remains diagnostic: Puerts measures synchronous module
cache reexecution, while UnifiedScript measures a full ScriptComponent
generation reload. Bridge values similarly name their envelope: five native
ProcessEvent callbacks per Puerts frame versus one managed Actor `OnTick`
callback per UnifiedScript frame. The complete-game result is a real M10.1
workload win. The full M10.1 matrix is now published and mixed; M10.3 still owns
the weighted broad-product claim.

Run the matched generated `int32(int32, int32)` performance gate with:

```powershell
.\comparison\run-performance-comparison.ps1
```

Both sides use UE 5.8.1 Development Editor, 4,096 warmup calls, five samples of
100,000 calls, and the median. UnifiedScript managed code is built in Release;
the report requires zero managed allocation and a UnifiedScript/Puerts latency
ratio no greater than 0.95. Results are written to the ignored
`artifacts/comparison/performance/comparison.json`. The generated scalar ABI v3
lane handles this fixed signature without constructing a generic call frame.
The allocation-free fixed lane also covers supported zero-to-four-argument
int, float, bool, object-handle, and FVector families; broader signatures use
the complete ABI v3 CallFrame marshaller.

Run one matched representative pair and write its scheduled normalized
captures with:

```powershell
.\comparison\run-representative-workload-comparison.ps1 `
  -WorkloadId per-frame-actor-logic -SuiteTrial 1

.\comparison\run-representative-workload-comparison.ps1 `
  -WorkloadId mass-entities -SuiteTrial 1 -SkipBuild

.\comparison\run-representative-workload-comparison.ps1 `
  -WorkloadId resource-spawn-flow -SuiteTrial 1 -SkipBuild

.\comparison\run-representative-workload-comparison.ps1 `
  -WorkloadId hot-reload -SuiteTrial 1 -SkipBuild
```

The same runner currently accepts `struct-container-transfer`,
`delegate-storm`, and `batch-queries`. The resource workload executes 256
matched class-load, Actor-spawn, and Actor-destroy rounds. Odd trials execute
UnifiedScript first; even trials reverse the engine order. `-SkipBuild` is only
valid after both worktrees have been rebuilt from the current sources.
The hot-reload pair performs 100 activation/retirement/collection cycles and
publishes reload, allocation, GC, memory, and startup diagnostics; it does not
treat collectible CLR generations and Puerts module reexecution as identical
latency semantics.

Plan or resume the complete 54-slot suite with:

```powershell
.\comparison\run-complete-workload-suite.ps1 -PlanOnly
.\comparison\run-complete-workload-suite.ps1
```

The runner skips only capture pairs whose runtime source, machine, engine build,
trial, and execution ordinal still match. It automatically assembles the two
27-capture engine results and publishes `artifacts/comparison/suite/comparison.json`
after the final pair succeeds.

The current UE 5.8.1 evidence contains all 54 scheduled captures under one
machine/build fingerprint. Complete BlockBreaker frame p50/p95/p99 ratios are
0.898835/0.955283/0.959456 and its bridge-time ratio is 0.336019. The same report
retains losing scalar (1.211389), struct/container (6.734380), delegate
(3.800531), and resource-flow (1.642611) ratios rather than filtering them out.
Reload and allocation remain mechanism-qualified diagnostics as described in
the suite contract.

## Visual/C# whole-game round trip

M8.4 edits the actual migrated Player and Rifle gameplay from one pinned Visual
baseline, inspects the emitted C#, makes behavior-preserving C# edits, and
reopens equivalent Graphs. Run the managed proof with:

```powershell
.\comparison\verify-blockbreaker-roundtrip.ps1
```

Add the two UE gameplay candidates with `-RunUnreal`, or run the complete dual
Build/Cook/Stage/Pak/Archive and packaged-smoke proof with
`-RunUnreal -Package`. The report independently gates stable method identity,
bound semantics, exact IL, execution cases, Script Assets, gameplay results,
and package results. See
[BlockBreaker Visual/C# round-trip proof](../docs/BLOCKBREAKER_ROUND_TRIP.md).
