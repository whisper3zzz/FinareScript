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

Run the complete migrated gameplay flow with three exact 10,000-frame trials
and median aggregation:

```powershell
.\comparison\run-gameplay-comparison.ps1
```

The gameplay runner first synchronizes the BlockBreaker deployment manifest
into target-class-checked Player and Rifle `UUnifiedScriptAsset` packages. Both
scripts must start through those assets, all 19 non-map business assets must be
byte-identical, the midpoint shot must hit exactly once, and UnifiedScript must
report zero legacy calls. Per-trial logs and the aggregate report are written
under `artifacts/comparison/gameplay`.

Run the matched generated `int32(int32, int32)` performance gate with:

```powershell
.\comparison\run-performance-comparison.ps1
```

Both sides use UE 5.8.1 Development Editor, 4,096 warmup calls, five samples of
100,000 calls, and the median. UnifiedScript managed code is built in Release;
the report requires zero managed allocation and a UnifiedScript/Puerts latency
ratio no greater than 0.95. Results are written to the ignored
`artifacts/comparison/performance/comparison.json`. The generated scalar ABI v3
lane handles this fixed signature without constructing a generic call frame;
other signatures continue to use the complete ABI v3 marshaller.
