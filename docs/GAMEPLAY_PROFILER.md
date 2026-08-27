# Gameplay and bridge profiler

Status: M9.3 attribution slice, schema
`unifiedscript.gameplay-profile/v0.2`.

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
- stable semantic method/node IDs and their hit count/time;
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
4. inspect the aggregate summary and latest frame;
5. choose **Focus Hot Node** to jump to the hottest node that still resolves in
   the active authoring surface;
6. choose **Save JSON** to persist the exact portable trace under
   `Saved/UnifiedScript/Profiles`.

The summary separates script time from World scheduler time and displays the
direct, CallFrame, reflection, batch, command/event, allocation, and pressure
totals. A node is focusable only when its semantic method/node identity exists
in the current Script Asset, so a stale capture cannot jump to an unrelated
graph element.

## Portable trace contract

`unifiedscript.gameplay-profile/v0.2` contains World/script identities, capture
time, ring capacity, dropped-frame count, an ordered `frames` array, and one
`aggregate` object. Frame and aggregate boundary names are stable identifiers:

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
semantic probe sharing, direct/CallFrame/reflection routes, allocation, and
buffer pressure. The live UE gate runs two adjacent World Ticks through every
attribution family, so the second Tick also consumes the first Tick's published
command event:

```powershell
& D:\UE_5.8\Engine\Binaries\Win64\UnrealEditor-Cmd.exe `
  HostProject\UEUnifiedScriptHost.uproject `
  -ExecCmds="Automation RunTests UnifiedScript.Unreal.Profiler.FullAttribution;Quit" `
  -Unattended -NullRHI -NoSound -NoSplash
```

The native workbench parser rejects non-v0.2, incomplete, negative, or
unbounded payloads before Slate displays them.
