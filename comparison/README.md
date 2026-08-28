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
managed frame. The latest local three-trial median is 4,599.308 ms for
UnifiedScript and 4,957.016 ms for Puerts, a ratio of 0.927838. Median frame time
is 0.4222 ms versus 0.44745 ms (ratio 0.943569). The individual elapsed ratios
are 0.962756, 0.921757, and 0.884789, so the raw variance remains visible rather
than being hidden behind the median. Every run requires one shot/hit and zero
legacy calls. The matched packages are 1,030,446,530 and 1,009,484,249 bytes
respectively, making the current UnifiedScript delivery 2.08% larger.

All 13 complete-workload metrics are present. Allocation remains diagnostic
only because CLR cumulative allocation and V8 used-heap growth are different
collectors. Reload also remains diagnostic: Puerts measures synchronous module
cache reexecution, while UnifiedScript measures a full ScriptComponent
generation reload. Bridge values similarly name their envelope: five native
ProcessEvent callbacks per Puerts frame versus one managed Actor `OnTick`
callback per UnifiedScript frame. The complete-game result is a real M10.1
workload win, but it is not yet a broad-suite claim.

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
