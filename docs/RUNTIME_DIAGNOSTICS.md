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
World, instance, generation, and target-handle identity and exposes:

- **Pause World Script**: stop entering managed `OnTick` at the next frame
  boundary without blocking the Unreal Game Thread;
- **Step Frame**: allow exactly one managed World tick and remain paused;
- **Continue World**: resume automatic managed ticks.

This frame-boundary control is intentionally distinct from the loopback
debugger's semantic Step In/Over/Out. It is safe for the current synchronous UE
bridge, but it does not preserve an in-progress CLR stack or stop midway through
a managed call. True semantic statement suspension inside a live UE World still
requires the next M9.1 transport/backend slice.

## Evidence

Managed tests validate ordinary and native-bridge records, exact 64-bit
identity encoding, source capture, and the zero-allocation generated fast path.
UE Automation drives a real hosted component through pause, one-frame step,
continue, transactional reload failure, managed JSON export, native parsing,
and World/generation/handle retention. Authoring Surface Automation verifies
that one-based runtime PDB coordinates resolve back to the exact semantic
method and `DebugTargetId`.
