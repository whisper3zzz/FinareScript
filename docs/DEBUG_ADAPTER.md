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
- continue, step-in, step-over, and step-out;
- stack-frame and per-frame local snapshot reads;
- bounded read-only evaluation over detached local snapshots;
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

CoreClrDebugEvaluation evaluation = debug.Evaluate(
    loaded.Generation.GenerationId,
    pause.PauseId,
    stack[0].FrameId,
    "damage.Amounts[0]");

debug.StepInto(loaded.Generation.GenerationId, pause.PauseId);
```

## Visual workbench loopback bridge

`unifiedscript debug-serve` is the development-only editor host. It serves the
static workbench and a same-origin loopback JSON API from `127.0.0.1`:

- `GET /api/debug/health` reports the loaded generation plus source and
  semantic-configuration IDs;
- `POST /api/debug/start` installs statement breakpoints, materializes an
  optional `ScriptAssetReceiver`, starts CoreCLR, and immediately returns a
  `running` session;
- `POST /api/debug/wait` waits for the next pause or completion without blocking
  pause requests from another client request;
- `POST /api/debug/pause` requests suspension at the next managed probe;
- `POST /api/debug/continue`, `/step-in`, `/step-over`, and `/step-out` resume
  the exact pause and immediately return the `running` session;
- `POST /api/debug/evaluate` reads a bounded expression from the exact paused
  frame snapshot.
- `POST /api/debug/publish-live-world` validates the current compiler
  identities and transactionally writes an instrumented PE/PDB for a live
  Unreal component. It is rejected while a loopback execution or authoring
  draft is active.

Debug session responses use `unifiedscript.debug-session/v0.2`. Every wait,
pause, resume, and evaluation request carries the current generation and
execution identity; resume and evaluation additionally carry the pause
identity, and evaluation carries the selected frame identity. The browser can
therefore keep a wait request outstanding while still issuing Pause, and stale
commands cannot affect a newer run. Start may additionally carry the Surface's
expected source-compilation and semantic-configuration IDs; native Slate uses
both guards so a stale editor document is rejected before execution begins.
A wait poll that sees neither pause nor completion within 15 seconds returns
the unchanged `running` session, allowing browser and Slate clients to renew
the poll without converting a long-lived method into an error.

The same loopback process owns the authoring transaction boundary:

- `GET /api/authoring/workspace` returns source, Declaration IR, Graph, revision,
  bounded transaction history, and the loaded CoreCLR generation;
- `POST /api/authoring/declaration-patch` applies typed state operations;
- `POST /api/authoring/semantic-patch` applies body edits by Semantic ID;
- `POST /api/authoring/semantic-patch-plan` applies dependent edits atomically;
- `POST /api/authoring/undo` and `/redo` restore exact accepted snapshots and
  publish a new revision/generation.

Authoring and debug control share one gate, so source cannot reload while an
execution is active. A successful candidate reloads the same transport and
commits canonical source plus `.usids`; external disk changes and stale patch
preconditions fail without replacing the current generation. The versioned
history journal restores revision, Undo/Redo stacks, and operation replay guards
after `debug-serve` restarts, but rejects stale source/identity/configuration
fingerprints and reports that rejection to clients.
Updated clients send baseline revision, unique operation ID, and author-facing
label headers so stale tabs and ambiguous retries fail before publication. See
[AUTHORING_TRANSACTIONS.md](AUTHORING_TRANSACTIONS.md) for the v0.3 contract.

For Unreal authoring, `debug-serve` also accepts repeatable `--source` inputs
and optional `--asset-out`, `--surface-out`, `--managed-out`, `--pdb-out`, and
`--history` targets. These outputs join source and identity in the same
rollback-capable file transaction, allowing a successful Slate request to
publish the exact artifacts consumed by UE before component reload begins.

The bridge never interprets HIR. It adapts HTTP requests directly to
`ICoreClrDebugTransport`, caps request bodies, serves only a path-confined
content root, and exposes the generation identities that the workbench checks
against its loaded artifacts. It is loopback development plumbing, not the
later remote or UE process transport. The native Slate client now consumes
this exact loopback contract; see
[NATIVE_DEBUG_WORKBENCH.md](NATIVE_DEBUG_WORKBENCH.md).

## Live Unreal World transport

The Unreal host reuses the probe/snapshot semantics above rather than adding a
second debugger model. Slate publishes an instrumented image, reloads the
selected `UUnifiedScriptComponent`, and sends
`unifiedscript.unreal-debug-config/v0.1` with the exact runtime generation,
program generation, and semantic breakpoint node IDs. The managed host starts
each debug `OnTick` on a named background thread. A probe hit publishes the
same immutable `unifiedscript.debug-session/v0.2` state and blocks only that
thread on its resume signal.

Unreal API calls cannot run on this worker. `FUnifiedScriptNativeTransport`
therefore queues worker-originated generated calls in an MPSC queue, retains
their World context, and waits on a pooled event. The World subsystem pumps the
queue on the GameThread before managed phase work. Detach, reload, component
stop, and World shutdown first reject new worker dispatch, wake pending calls,
abort suspended probes, and drain the worker before unloading its collectible
context.

State reads use a bounded retry because `running` can become a larger `paused`
snapshot between the ABI length query and copy. This prevents a legitimate
concurrent pause from being misreported as an empty session.

## Suspension and stepping semantics

A hit publishes its immutable pause snapshot and then blocks the actual thread
executing the emitted Visual method on a resume signal. The completion task
therefore remains incomplete until a continue or step command releases that
pause. This is not a synthetic notification layered over already completed
execution.

`Pause` is cooperative: it stops at the next statement probe. `StepOver`
records the current program-stack depth, releases execution, ignores probes in
deeper called Visual methods, and stops at the next probe at the same or a
shallower depth. An enabled breakpoint in a deeper call still wins and pauses,
which matches normal debugger expectations. `StepInto` stops at the next probe
at any depth. `StepOut` stops at the next probe in a shallower Visual frame and
rejects a request from the outermost frame instead of silently behaving like
continue.

Reload is rejected with `UDB1004` while an execution is running or suspended.
After completion, reload produces a new generation. Commands carrying the old
generation fail with `UDB1002`; commands carrying an old execution or pause fail
with `UDB1005` or `UDB1006`.

## Stack and graph-safe locals

Stack frames are captured from the live CoreCLR stack and immediately reduced
to strings, MethodDef tokens, IL offsets, and optional original semantic source
spans. No `StackFrame`, `MethodBase`, `Type`, or source-defined object reference
escapes the pause.

Every active instrumented Visual frame retains the most recent parameters and
locals that Roslyn proved definitely assigned at its latest probe. These
snapshots are correlated to the live CoreCLR stack by assembly and MethodDef
identity. Each frame also carries its Visual method ID, latest semantic node
ID, and original source span when available. Values are copied into a bounded
tree:

- primitive, string, enum, and native-integer values use canonical displays;
- arrays are capped at 32 children per level;
- objects expose public instance fields only, never property getters;
- depth is capped at three levels;
- reference cycles become explicit `<cycle>` leaves;
- inaccessible values become `<unavailable>` without retaining the object.

Watch evaluation traverses only these detached snapshots. Its grammar is a
local or parameter name followed by public-field access and constant,
non-negative array indices, for example `damage.Amounts[0]`. Expressions are
limited to 256 characters and 16 traversal segments. It cannot call methods or
properties, perform operators, mutate state, or retain source-defined objects.
Invalid or unavailable expressions fail with `UDB1009`.

## Deliberate limits

M5.4 supports synchronous Visual methods and statement-node breakpoints;
M5.5.4 also supplies declared Script Asset receivers for instance methods.
Expression-node breakpoint requests remain `Pending` with an explicit backend
message instead of pretending that a nearby statement is exact. The current
slice does not provide:

- arbitrary code execution, property or method calls, operators, mutation, or
  conditional/log breakpoints;
- instruction-level stepping;
- async state-machine suspension;
- forced preemptive suspension between probes;
- serialized remote/DAP framing;
- async state-machine suspension inside a live UE World call;
- a native ICorDebug/netcoredbg trap backend.

The cooperative backend is intended for trusted development code. As with the
normal execution host, collectible loading is a reload mechanism, not a CPU,
memory, filesystem, network, or process security sandbox.
