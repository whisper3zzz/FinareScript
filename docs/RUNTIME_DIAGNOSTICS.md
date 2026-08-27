# UE World runtime diagnostics

`unifiedscript.runtime-diagnostic/v0.1` is the detached failure contract emitted
when a managed script call leaves the real Unreal World boundary with an
exception. It replaces an opaque exception string with enough identity to
answer: which World, script instance, generation, UObject handles, source
location, graph node, and bridge operation failed?

## Contract

Every record contains a monotonic process-local sequence, UTC timestamp,
failure kind, boundary operation, bounded message/stack, World host ID, script
instance ID, collectible generation, assembly/type, and host/target handles.
World, instance, handle, and Member IDs are JSON strings so a browser or other
double-based JSON consumer cannot truncate a native `uint64`.

`managedException` represents script or lifecycle failures.
`nativeBridgeFailure` additionally carries the exact
`UnrealInvocationError`, `UnrealObjectHandleError`, invocation target, generated
Member ID when available, and canonical member path. This information is added
only when constructing a failed invocation exception; successful generated
ABI v3/v4 paths do not allocate diagnostic state.

Portable-PDB locations are emitted as a primary `source` plus at most 32
`sourceFrames`. The primary location prefers the active script type. Slate
walks all frames until one belongs to an Authoring Surface action, converts the
PDB's one-based line/column to the Surface's zero-based span, and resolves the
smallest containing action. The resulting `DebugTargetId` is used for graph
highlighting and **Focus Node** navigation.

Messages are limited to 4,096 characters and stacks to 32,768 characters. The
record contains no source text, locals, managed object references, or raw UE
pointers. Native consumers validate the schema and copy every value before the
managed boundary error is cleared.

## Live World controls

The Slate debugger automatically discovers the best matching PIE/Game
`UUnifiedScriptComponent` for the selected managed type. It displays exact
World, instance, generation, and target-handle identity. **Attach Semantic**
publishes and reloads an instrumented image, then binds graph/source semantic
breakpoints to that exact live runtime generation. The normal debugger controls
now operate on the live `OnTick` CLR stack:

- **Pause**: cooperatively stop at the next semantic statement probe;
- **Continue / Step In / Step Over / Step Out**: resume the exact pause ID and
  preserve nested Visual frame semantics;
- **Call Stack / Locals / Watch**: inspect detached bounded snapshots for any
  active Visual frame without retaining a managed object or UObject pointer.

The managed call runs on a dedicated debugger worker. Worker-originated Unreal
calls are queued back to the GameThread, so a suspended CLR stack never blocks
the engine thread. Detach, reload, stop, and World teardown abort and drain the
worker before collectible-context disposal.

Reload additionally clears coarse frame-boundary pause/step state and records
debug drain, generation swap, rollback, event rebinding, and collectible-context
cleanup as one `unifiedscript.reload-transaction/v0.1` receipt. The authoring
and debug panels show the same structured transaction. See
[Transactional hot-reload reliability](HOT_RELOAD_RELIABILITY.md).

The frame-boundary control fallback remains available when semantic attach is not
active:

- **Pause World Script**: stop entering managed `OnTick` at the next frame
  boundary without blocking the Unreal Game Thread;
- **Step Frame**: allow exactly one managed World tick and remain paused;
- **Continue World**: resume automatic managed ticks.

These three controls deliberately do not preserve an in-progress CLR stack;
they remain useful for coarse gameplay stepping and diagnostics.

## Evidence

Managed tests validate ordinary and native-bridge records, exact 64-bit
identity encoding, source capture, and the zero-allocation generated fast path.
UE Automation drives a real hosted component through pause, one-frame step,
continue, transactional reload failure, managed JSON export, native parsing,
and World/generation/handle retention. Authoring Surface Automation verifies
that one-based runtime PDB coordinates resolve back to the exact semantic
method and `DebugTargetId`. The semantic World Automation additionally proves
statement breakpoint suspension, Step In/Out, deeper-frame locals, unique
frame identities, worker-to-GameThread generated bridge calls, generation-two
breakpoint rebinding, and detach while suspended.
