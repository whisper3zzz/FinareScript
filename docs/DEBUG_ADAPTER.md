# CoreCLR Debug Adapter

## Purpose

M5.4 turns semantic breakpoint identity into a real suspended CoreCLR
execution. It deliberately does not introduce a HIR interpreter or a second
script VM. The debug image is still a Roslyn-emitted .NET assembly and the
method still runs on CoreCLR; debug-only calls inserted after Visual Profile,
semantic, Graph, and CFG validation cooperate with an adapter in the shared
runtime assembly.

The cooperative backend is the first implementation of a replaceable adapter
boundary. A future ICorDebug, `netcoredbg`, or UE process backend can implement
the same transport operations without changing semantic breakpoint IDs,
generation guards, stack/value models, or editor consumers.

## Compile and execution path

```text
original C#
  -> Visual Profile + compiler + semantic + Graph/CFG gates
  -> stable HIR and source map
  -> debug-only statement probes added to an emission syntax tree
  -> portable-PDB CoreCLR image
  -> collectible AssemblyLoadContext
  -> CoreClrDebugAdapter
       -> breakpoint / pause / step decision
       -> real execution-thread suspension
       -> detached stack and local snapshots
```

Instrumentation happens only after the original source has passed every normal
gate. The HIR, bound semantics, source-map identity, and identity sidecars all
come from that original source. Probe calls therefore cannot become graph
nodes, alter persistent semantic IDs, or make an otherwise rejected program
executable.

Normal non-debug emission remains a single PE/PDB emission. Only a debug image
performs the extra instrumented emission.

## Adapter and loopback transport

`CoreClrDebugAdapter` owns one reload domain and exposes:

- transactional debug-image reload with a new monotonic generation;
- persistent semantic breakpoint set/remove and rebind;
- asynchronous execution start and completion;
- wait-for-pause events;
- cooperative pause at the next probe;
- continue and step-over;
- stack-frame and per-frame local snapshot reads;
- generation, execution, and pause identity validation.

`ICoreClrDebugTransport` is the editor-facing boundary.
`CoreClrDebugLoopbackTransport` delegates it in process and is the integration
path used by the visual editor before process or UE transport framing exists.
Every stateful command carries a generation, execution, or pause identity, so a
consumer cannot accidentally continue a superseded image or inspect a resumed
pause.

Typical use:

```csharp
using var debug = new CoreClrDebugLoopbackTransport();
CoreClrDebugReloadResult loaded = debug.Reload(request);
debug.SetBreakpoint(new SemanticBreakpoint(breakpointId, statementNodeId));

SemanticId methodId = debug.ResolveVisualMethodId("Game.Rules.Run");
CoreClrDebugExecution execution = debug.Start(
    new ExecutionInvocation(methodId, [new ExecutionArgument("41")]));

CoreClrDebugPause pause = await debug.WaitForPauseAsync(execution.ExecutionId);
IReadOnlyList<CoreClrDebugStackFrame> stack = debug.GetStackTrace(
    loaded.Generation.GenerationId,
    pause.PauseId);
IReadOnlyList<CoreClrDebugValue> locals = debug.GetLocals(
    loaded.Generation.GenerationId,
    pause.PauseId,
    stack[0].FrameId);

debug.StepOver(loaded.Generation.GenerationId, pause.PauseId);
```

## Visual workbench loopback bridge

`unifiedscript debug-serve` is the development-only editor host. It serves the
static workbench and a same-origin loopback JSON API from `127.0.0.1`:

- `GET /api/debug/health` reports the loaded generation plus source and
  semantic-configuration IDs;
- `POST /api/debug/start` installs statement breakpoints, materializes an
  optional `ScriptAssetReceiver`, starts CoreCLR, and returns the first pause;
- `POST /api/debug/step-over` resumes the exact pause and returns the next pause
  or completion;
- `POST /api/debug/continue` resumes and returns the next pause or completion.

The same loopback process owns the authoring transaction boundary:

- `GET /api/authoring/workspace` returns source, Declaration IR, Graph, revision,
  and the loaded CoreCLR generation;
- `POST /api/authoring/declaration-patch` applies typed state operations;
- `POST /api/authoring/semantic-patch` applies body edits by Semantic ID.

Authoring and debug control share one gate, so source cannot reload while an
execution is active. A successful candidate reloads the same transport and
commits canonical source plus `.usids`; external disk changes and stale patch
preconditions fail without replacing the current generation.

The bridge never interprets HIR. It adapts HTTP requests directly to
`ICoreClrDebugTransport`, caps request bodies, serves only a path-confined
content root, and exposes the generation identities that the workbench checks
against its loaded artifacts. It is loopback development plumbing, not the
later remote or UE process transport.

## Suspension and step-over semantics

A hit publishes its immutable pause snapshot and then blocks the actual thread
executing the emitted Visual method on a resume signal. The completion task
therefore remains incomplete until `Continue` or `StepOver` releases that
pause. This is not a synthetic notification layered over already completed
execution.

`Pause` is cooperative: it stops at the next statement probe. `StepOver`
records the current program-stack depth, releases execution, ignores probes in
deeper called Visual methods, and stops at the next probe at the same or a
shallower depth. An enabled breakpoint in a deeper call still wins and pauses,
which matches normal debugger expectations.

Reload is rejected with `UDB1004` while an execution is running or suspended.
After completion, reload produces a new generation. Commands carrying the old
generation fail with `UDB1002`; commands carrying an old execution or pause fail
with `UDB1005` or `UDB1006`.

## Stack and graph-safe locals

Stack frames are captured from the live CoreCLR stack and immediately reduced
to strings, MethodDef tokens, IL offsets, and optional original semantic source
spans. No `StackFrame`, `MethodBase`, `Type`, or source-defined object reference
escapes the pause.

The top instrumented frame exposes parameters and locals that Roslyn proves
definitely assigned on entry to the probed statement. Values are copied into a
bounded tree:

- primitive, string, enum, and native-integer values use canonical displays;
- arrays are capped at 32 children per level;
- objects expose public instance fields only, never property getters;
- depth is capped at three levels;
- reference cycles become explicit `<cycle>` leaves;
- inaccessible values become `<unavailable>` without retaining the object.

Deeper frames are reported but do not yet expose locals because cooperative
statement probes do not retain past frame state. The snapshot model already
supports per-frame lookup, so a native managed-debug backend can fill those
frames later without changing consumers.

## Deliberate limits

M5.4 supports synchronous Visual methods and statement-node breakpoints;
M5.5.4 also supplies declared Script Asset receivers for instance methods.
Expression-node breakpoint requests remain `Pending` with an explicit backend
message instead of pretending that a nearby statement is exact. The current
slice does not provide:

- arbitrary expression evaluation, watches, mutation, or conditional/log
  breakpoints;
- deeper-frame locals;
- instruction-level stepping, step-in, or step-out;
- async state-machine suspension;
- forced preemptive suspension between probes;
- serialized remote/DAP framing or UE editor UI;
- a native ICorDebug/netcoredbg trap backend.

The cooperative backend is intended for trusted development code. As with the
normal execution host, collectible loading is a reload mechanism, not a CPU,
memory, filesystem, network, or process security sandbox.
