# Interactive Unreal demo

## Launch

Close any editor instance already using the host project, then run:

```powershell
powershell -ExecutionPolicy Bypass -File tools/launch-unreal-authoring-demo.ps1 `
  -EngineRoot D:\UE_5.8
```

The launcher builds the UE host, builds and stages the managed demo, starts the
transactional authoring service on `127.0.0.1:4173`, and opens the
`UnifiedScript Authoring` tab automatically. It snapshots and restores the
project's local engine/input configuration. Closing Unreal Editor also stops
the hidden authoring service.

## What to try

1. Select `ResolveInitialValue` in **My Script**. The center canvas shows the
   same typed control/data structure used by the compiler, presented as compact
   Blueprint-style cards.
2. Select `OnManagedSignal`. Its red Event Entry exposes `value: int` and
   `signalContext: UnrealObjectHandle` payload pins while retaining the exact UE
   delegate path in Details.
3. Right-click the graph and choose **Unreal Events >
   UnifiedScriptComponent > Event OnManagedSignal**. The Workbench adds a new
   strongly typed `[UnrealEvent]` C# handler through declaration-patch v0.2,
   recompiles, and reloads the compiler-owned Surface.
4. Select a literal or supported operator. Its semantic identity, type, source
   span, and legal edit capability appear in **Details**.
5. Change an editable value and choose **Apply & Reload**. The Workbench checks
   the current source fingerprints, compiles one candidate, and atomically
    publishes C#, identity sidecars, Declaration IR, Authoring Surface, DLL, and
    PDB. Native reload then preflights every matching running and stopped/editor
    component before changing any instance; one restart-required state migration
    blocks the entire batch. Every compatible running candidate then prepares
    while retaining the stopped previous generation. The batch publishes only
    after all candidates prepare; a later `OnStart` or event-rebinding failure
    aborts earlier candidates and restores their generation, state, and
    overrides before old contexts can retire.
6. Drag a typed data wire onto an occupied input to preview transactional
   rewiring. Invalid types and stale source are rejected without partially
   changing the graph or source.
7. Use the source/debug controls to navigate the real C# span and inspect the
   active CoreCLR statement/locals when a script is running.

The checked-in headless smoke test remains available through
`tools/run-unreal-demo.ps1`. A successful run proves component
Start/Tick/Reload/Stop, declaration state migration, generated wrapper calls,
the reflected `OnManagedSignal` Event Entry exactly once after reload, and
`LegacyCalls=0` on UE 5.8. The managed Demo also validates that Gameplay Data
ABI v4 is available in `ManagedTick`, acquires the current event view, commits a
zero-row command reservation, and executes a zero-distance line Batch Trace.
These are lifecycle/discovery probes; the full command/event gameplay workload
is exercised by the BlockBreaker comparison.

## Diagnostics

- Authoring server output: `artifacts/ue/authoring/server.stdout.log`
- Authoring server failures: `artifacts/ue/authoring/server.stderr.log`
- Headless lifecycle log: `artifacts/ue/managed-demo.log`
- Interop benchmark report: `artifacts/ue/interop-benchmark.json`
- Win64 package and smoke reports: `artifacts/ue/release/<timestamp>/release-gate.json`

The status bar reports source conflicts, semantic/type rejection, compile
failure, artifact publication failure, and reload failure separately. A failed
transaction leaves the previously published source and running generation
unchanged. Runtime failures are also returned through the managed/native
diagnostic boundary: `UUnifiedScriptComponent.LastScriptError` contains the
operation, managed exception type/message, and stack rather than only a numeric
status.
