# Puerts / UnifiedScript comparison

The comparison uses the same BlockBreaker map, art, input mapping, Unreal Engine
5.8.1 installation, and 300-tick headless smoke window. The two worktrees live
under `D:\\Project\\UEUnifiedScriptComparison`:

- `BlockBreaker-Puerts`: original TypeScript gameplay on Puerts/V8.
- `BlockBreaker-UnifiedScript`: migrated C# gameplay on UnifiedScript/CoreCLR.

Pinned repositories and commits are recorded in `baselines.json`. Run both
functional gates and produce an ignored JSON report with:

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

Run the complete migrated gameplay flow with three exact 10,000-frame trials
and median aggregation:

```powershell
.\comparison\run-gameplay-comparison.ps1
```

The gameplay runner first builds both runtimes and synchronizes the BlockBreaker
deployment manifest into target-class-checked Player and Rifle
`UUnifiedScriptAsset` packages. Both
scripts must start through those assets, all 19 non-map business assets must be
byte-identical, the midpoint shot must hit exactly once, and UnifiedScript must
report zero legacy calls. Per-trial logs and the aggregate report are written
under `artifacts/comparison/gameplay`. Compilation and generation are outside
the measured trials, and even-numbered trials run UnifiedScript first to reduce
thermal and fixed-order bias.

The migrated shot is no longer a native Gun-facade shortcut. Resource/class
loading and Actor spawn remain on the generated Binding control plane. The hot
shot path uses one Gameplay Data Batch Trace, commits emitter and damage rows in
one command reservation, and consumes their completion events on the next
managed frame. The latest local three-trial median is 4,927.191 ms for
UnifiedScript and 4,759.930 ms for Puerts, a ratio of 1.035139. Each result also
requires exactly one shot/hit, both command events, 120,031 fast/CallFrame calls,
and zero legacy calls. This result proves functional parity but is not a
performance win; the at-least-5%-faster workload gate remains an M10 target.

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
