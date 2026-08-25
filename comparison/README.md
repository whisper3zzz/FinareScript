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
It is not a performance verdict. Fair performance work must add identical
in-map workloads, warmup, repeated samples, allocation/GC counters, and
statistical output before making a faster-than-Puerts claim.
