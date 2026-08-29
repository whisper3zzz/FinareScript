# Gameplay and bridge profiler

Status: M9.3 attribution slice, schema
`unifiedscript.gameplay-profile/v0.3`.

## Purpose

The gameplay profiler attributes one managed script Tick across the same
boundaries that determine real frame cost. It is opt-in and scoped to one live
`UUnifiedScriptComponent`; normal gameplay keeps the counters disabled.

Each captured frame records:

- script type, generation, World frame, delta time, total time, and managed
  allocation;
- `NativePreActorTick`, `ManagedTick`, and `NativePostActorTick` count/time
  deltas from the World scheduler;
- direct generated thunks, ABI v3 CallFrame calls, canonical reflection
  fallback, command submission, event acquisition, Batch Trace, and Batch
  Overlap count/time;
- stable semantic method/node IDs, hit count/time, and the bridge/data boundary
  cost accumulated while each node is active;
- command, event, trace, and overlap overflow deltas.

The component owns a 120-frame ring by default. When full, the oldest frame is
replaced and `droppedFrameCount` increases. Export never returns an unbounded
history.

## Unreal workflow

Select a running component in the UnifiedScript Debug panel, then:

1. choose **Start Profile**;
2. reproduce the gameplay frame or interval;
3. choose **Capture Profile** for a non-destructive snapshot, or **Stop
   Profile** to retain the final snapshot;
4. inspect the aggregate summary, latest frame, and ranked **Semantic
   Hotspots** with graph/source locations and dominant Unreal boundaries;
5. select any hotspot, or choose **Focus Hot Node**, to jump to the matching
   node in the active authoring surface;
6. choose **Save JSON** to persist the exact portable trace under
   `Saved/UnifiedScript/Profiles`.

The summary separates script time from World scheduler time and displays the
direct, CallFrame, reflection, batch, command/event, allocation, and pressure
totals. A node is focusable only when its semantic method/node identity exists
in the current Script Asset, so a stale capture cannot jump to an unrelated
graph element.

## Portable trace contract

`unifiedscript.gameplay-profile/v0.3` contains World/script identities, capture
time, ring capacity, dropped-frame count, an ordered `frames` array, and one
`aggregate` object. Frame and aggregate nodes each contain their own bounded
`boundaries` array, allowing a bridge or data-plane regression to be attributed
to the source/graph node that owned the call. Boundary names are stable
identifiers:

```text
World:    NativePreActorTick, ManagedTick, NativePostActorTick
Bridge:   directThunk, callFrame, reflectionFallback
Data:     commands, events, batchTrace, batchOverlap
Pressure: commandOverflowCount, eventOverflowCount,
          traceOverflowCount, overlapOverflowCount
```

Times are integer nanoseconds and counters are integer deltas. A consumer must
use `schemaVersion`, not UI text, for compatibility. Missing World telemetry is
represented by an empty `worldPhases` array and an `Unknown` current phase; it
is not silently interpreted as zero-cost work.

The JSON is suitable for checked-in regression fixtures or later comparison
tooling. M10 owns statistical baselines, machine fingerprints, percentile
budgets, and matched Puerts reports; a single trace is diagnostic evidence, not
a broad performance claim.

The data-plane workload profile also exercises the generated BlockBreaker
extension with 1,024 target snapshots and 1,024 impact events. Its report
exports both row counts and the generated schema fingerprint, so an apparently
fast run cannot pass with a stale native/managed descriptor pair. See
[Generated data-plane extensions](DATA_PLANE_EXTENSIONS.md).

The browser Workbench can import the same JSON and ask the loopback host to
join it with the current Surface and generated API route catalog. See
[INTEGRATED_RUNTIME_INSIGHTS.md](INTEGRATED_RUNTIME_INSIGHTS.md). The catalog
join describes which generated APIs use a route; it is not fabricated
per-member timing.

## Defect and regression diagnosis

`ue-profile-diagnose` joins three existing evidence sources without adding
custom gameplay logs:

1. a baseline `gameplay-profile/v0.3` trace;
2. a current trace with per-node boundary costs;
3. one detached debugger local observation plus the compiler/Authoring Surface
   node-to-source locations.

The debugger observation is captured from the real pause, frame, and locals,
so its method ID, node ID, source span, expected value, and actual value remain
correlated. The analyzer chooses that node's dominant runtime boundary for a
gameplay defect. It then compares per-frame costs for every resolvable
node/boundary pair, including residual `managedCode`, and reports candidates
that cross both a ratio and absolute-nanosecond threshold. Output uses
`unifiedscript.gameplay-diagnosis/v0.1` and only sets `acceptanceSatisfied` when
both kinds are located. Each CLI input is capped at 16 MiB in addition to the
trace's frame, node, and boundary collection limits.

The checked-in fixture is reproducible with:

```powershell
dotnet run --project src/UnifiedScript.Cli -- `
  ue-profile-diagnose `
  --baseline samples/Unreal/Diagnostics/GameplayDiagnosis.baseline.json `
  --current samples/Unreal/Diagnostics/GameplayDiagnosis.current.json `
  --evidence samples/Unreal/Diagnostics/GameplayDiagnosis.evidence.json `
  --out samples/Unreal/Diagnostics/GameplayDiagnosis.report.json
```

Its report locates the incorrect `damage = 0` observation at the exact Return
source/graph node and `reflectionFallback` boundary. It separately locates a
5.00x Batch Trace regression at the exact `traceBudget` declaration node and
`batchTrace` boundary. The managed acceptance test obtains the defect evidence
from a real suspended CoreCLR execution rather than constructing a log record.

## Instrumenting semantic nodes

Generated or authored managed code emits `CoreClrDebugProbe.Enter(methodId)`
and `CoreClrDebugProbe.Hit(methodId, nodeId, ...)` with stable `us_...`
identities. The live semantic debugger and profiler share these probes, so the
same source/graph node can explain both a paused value and its runtime cost.

When a breakpoint pauses a debug worker, the open node interval includes the
pause duration. Such a capture is useful for defect correlation but must not be
used as a performance baseline. Capture performance with semantic breakpoints
disabled.

## Reproducible gates

The managed tests prove ring bounds, aggregate math, World telemetry deltas,
semantic probe sharing, per-node direct/CallFrame/reflection/data routes,
allocation, buffer pressure, live-local defect correlation, and normalized
regression comparison. The live UE gate runs two adjacent World Ticks through
every attribution family, so the second Tick also consumes the first Tick's
published command event:

```powershell
& D:\UE_5.8\Engine\Binaries\Win64\UnrealEditor-Cmd.exe `
  HostProject\UEUnifiedScriptHost.uproject `
  -ExecCmds="Automation RunTests UnifiedScript.Unreal.Profiler.FullAttribution;Quit" `
  -Unattended -NullRHI -NoSound -NoSplash
```

The native workbench parser rejects non-v0.3, incomplete, negative, or
unbounded payloads before Slate displays them.
