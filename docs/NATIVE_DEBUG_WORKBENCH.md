# Native Slate debug Workbench

## What this slice provides

The Unreal editor's UnifiedScript tab now consumes the same versioned CoreCLR
debug contract as the browser Workbench. It is a real native Slate client, not
a visual simulation: Run starts the current Visual method in `debug-serve`, the
execution thread suspends on cooperative probes, and every control carries the
exact generation, execution, pause, and selected-frame identity required by
`unifiedscript.debug-session/v0.2`.

The graph and source inspector share one semantic breakpoint set. Debuggable
graph cards expose a breakpoint dot; the Advanced Node Details panel exposes
the same operation beside the exact C# source span. Breakpoints are stored by
`DebugTargetId`, so rebuilding the transient graph or switching functions does
not turn layout node identity into debugger identity. Start also sends the
loaded source-compilation and semantic-configuration IDs, and the server
rejects a stale Slate Surface before creating an execution.

## Workflow

1. Start the authoring host with `dotnet run --project src/UnifiedScript.Cli --
   debug-serve ...` and use the same Surface/artifact outputs as the Unreal
   Script Asset.
2. Open **UnifiedScript Blueprint**, load the Script Asset or Surface, and
   verify the Workbench URL.
3. Toggle breakpoint dots on graph cards or use **Add Semantic Breakpoint** in
   Advanced Node Details. Bound targets are red; pending targets are amber.
4. Select a Visual function and choose **Run**. A run without breakpoints may
   be stopped with **Pause** at the next managed safe point.
5. Use **Continue**, **Step In**, **Step Over**, and **Step Out**. Selecting a
   call-stack frame navigates to its semantic method/node and highlights the
   matching graph card.
6. Inspect the detached locals for any active Visual frame. A watch accepts a
   local/parameter root followed only by public-field access and constant array
   indices, such as `damage.Amounts[0]`.

The wait operation is long-polled independently from commands. A 15-second
poll expiry returns the same `running` identity and is renewed by clients;
Pause therefore remains responsive while a wait is outstanding and long-lived
gameplay methods do not become false failures.

## Live UE World target

The same panel now discovers a matching `UUnifiedScriptComponent` already
hosted inside a PIE or Game World and displays the exact World host, script
instance, collectible generation, and target UObject handle. **Attach Semantic**
performs one guarded operation chain:

1. `debug-serve` validates the loaded source and semantic-configuration IDs;
2. `/api/debug/publish-live-world` emits an instrumented PE and portable PDB to
   the configured `--managed-out` and `--pdb-out` targets without changing
   source, identity, Declaration IR, or Surface artifacts;
3. Slate reloads the component from that image and binds its current semantic
   breakpoint set to the new runtime generation;
4. managed `OnTick` runs on a dedicated debugger worker, so a statement trap
   preserves the real CLR stack without blocking Unreal's Game Thread.

**Pause**, **Continue**, **Step In**, **Step Over**, and **Step Out** then
control that live World stack through the same `debug-session/v0.2` model used
by loopback execution. Stack selection, deeper-frame locals, semantic graph
highlighting, and detached watch paths all operate on the live component.
Generated UObject bridge calls made by the worker enter a bounded MPSC command
queue; the World GameThread pumps the call and wakes the worker, preserving the
engine's thread-affinity contract. **Detach Semantic** aborts and drains a
suspended worker before reload, stop, or World teardown.

The older **Pause World Script**, **Step Frame**, and **Continue World** controls
remain available as frame-boundary gameplay controls when semantic attach is
not active. They do not claim statement-level suspension.

Managed exceptions and native bridge failures cross the host boundary as
`unifiedscript.runtime-diagnostic/v0.1`. The panel shows the failure kind,
operation, exception type, source, bridge/handle category, and member path.
Portable-PDB frames are resolved against Authoring Surface spans; **Focus
Node** navigates to and highlights the exact `DebugTargetId` when one matches.
See [RUNTIME_DIAGNOSTICS.md](RUNTIME_DIAGNOSTICS.md).

The live gameplay profiler now exposes a ranked Semantic Hotspots list in this
same panel. Every row joins total/hit cost and residual managed time with the
dominant direct-thunk, CallFrame, reflection, or data-plane boundary, then
resolves the same method/node IDs to a graph action and C# span. Selecting a row
uses the debugger's normal semantic navigation; stale rows remain visible but
cannot focus an unrelated node. The portable browser join and route-catalog
boundary are documented in
[INTEGRATED_RUNTIME_INSIGHTS.md](INTEGRATED_RUNTIME_INSIGHTS.md).

## Safety and current boundary

- The client never accepts a response from another source compilation,
  semantic configuration, generation, execution, pause, or frame.
- Locals and watch results are detached bounded trees. No managed object,
  reflection object, or raw Unreal pointer is retained by Slate.
- Watch evaluation cannot invoke methods or properties, perform operators, or
  mutate the suspended program.
- Breakpoint changes are accepted only between executions; the active binding
  snapshot reports `bound`, `pending`, or `disabled` for the current
  generation.
- Live watch grammar is limited to one local/parameter root plus at most 16
  public-field or constant array-index segments and 256 characters. It only
  traverses detached values already present in the selected pause.
- Method arguments currently use deterministic type-based demo values, and
  instance methods use the declared Script Asset receiver with default state.

Live synchronous World calls now preserve their in-progress CLR stack on a
dedicated worker while the GameThread remains responsive. Remaining debugger
limits are async state-machine suspension, forced preemptive stops between
cooperative probes, conditional/log breakpoints, remote-machine authentication,
and DAP adaptation. The versioned bounded process framing itself is documented
in [REMOTE_DEBUG_FRAMING.md](REMOTE_DEBUG_FRAMING.md).
