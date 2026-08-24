# M6.1: Unreal Engine adapter

## Status

Phase A reflection/binding code is implemented. Phase B.1 adds the native
UObject lifetime foundation without claiming invocation is complete. The .NET
suite and repository invariants pass, UE 5.7.4 UHT accepts the plugin headers,
and both plugin modules compile and link against UE 5.8.1. An installed UE 5.8.1
Release build passes the lifecycle Automation Test and a live commandlet export;
the exported fixture manifest also passes managed binding generation. Phase B.2
adds tested native primitive property access and `ProcessEvent` dispatch without
claiming that the process bridge exists. Phase B.3 adds the managed invocation
adapter and executes the checked-in generated wrapper against its injected
transport. Phase B.4 adds the versioned native function table and concrete
managed in-process transport. Phase B.5 adds the native World subsystem and
retained bridge-context lifecycle.
Phase B.6 loads CoreCLR through hostfxr and acquires the process load-assembly
delegate. Phase B.7 invokes managed create/destroy entry points and owns one
managed registry per World. Phase B.8 loads a staged gameplay demo and executes
its generated wrapper against a live UObject. Phase B.9 adds component-driven
Start/Tick/Stop and collectible per-script reload generations. Phase B.10 adds
stable generated Member IDs, an ABI v2 transport, and direct native thunks for
the component fixture while preserving ABI v1 reflection fallback. Phase B.11
adds native Script Assets, Content Browser/soft-path resolution, and
configuration-aware transactional type reload. Phase B.12 generalizes native
thunk generation, Phase B.13 materializes Declaration IR state, and Phase B.14
adds the first native Slate workspace over Authoring Surface v0.1 with semantic
patch export. Phase B.15 submits those patches through the Workbench, atomically
regenerates UE authoring/runtime artifacts, and reloads matching components.
Full UE 5.7 native compilation and packaging remain open.

## Completed Phase A contract

1. A real UE 5.7/5.8-compatible host project owns an enabled two-module plugin.
2. UHT sees `UUnifiedScriptComponent` and the headless export commandlet.
3. The exporter selects named native modules and emits deterministic reflected
   class, property, function, and recursive type data.
4. `UnifiedScript.Unreal` strictly validates and normalizes schema v0.1 without
   exposing UE types to compiler projects.
5. `ue-bindings` generates deterministic typed wrappers over an opaque handle
   and invocation bridge.
6. Unsupported native shapes produce stable `USUE2xxx` diagnostics rather than
   guessed interop code.
7. A checked-in manifest and generated wrapper cover component state plus void
   and returning function calls.
8. One local helper discovers the engine and runs build, export, and generation
   without committing an absolute UE path.

## Completed Phase B.1 contract

1. Managed and native code share one unsigned 64-bit handle encoding: low bits
   are `slot + 1`, high bits are a non-zero generation, and zero is null.
2. The native registry owns no UObject. It stores `TWeakObjectPtr`, binds to one
   `UWorld`, and rejects null, foreign-world, malformed, and stale handles.
3. Registration, resolution, release, and invalidation reject access outside
   the game thread before touching UObject state.
4. Explicit release and UObject deletion both retire the slot. Reuse advances
   the generation, so an old managed handle cannot resolve to a new object.
5. A native Automation Test covers duplicate registration, cross-world access,
   release/reuse, off-thread rejection, GC deletion, and stale resolution.
6. Host TargetRules follow the selected engine version; the same project builds
   with UE 5.7 defaults or UE 5.8 defaults instead of overriding shared Editor
   settings.

## Completed Phase B.2 contract

1. Native property and function access resolves the target through the weak
   registry before reflection or memory access.
2. Full generated member paths identify both the expected target class and the
   member; wrong types and missing members fail explicitly.
3. The first owned-value transport supports `bool`, `int32`, `FString`, and
   void without exposing UObject, property, or parameter-buffer addresses.
4. Blueprint read/write/call flags, argument count, argument kind, and
   unsupported output/reference parameters are validated before `ProcessEvent`.
5. A live UE 5.8 Automation Test round-trips fixture properties, invokes both
   fixture functions, and verifies type, arity, thread, and stale-handle errors.

## Completed Phase B.3 contract

1. `NativeUnrealInvocationBridge` is the single CLR adapter implementing the
   generated wrappers' `IUnrealInvocationBridge` dependency.
2. Managed and native value/error enums share fixed numeric identities for the
   primitive slice and lifecycle failures.
3. Unsupported CLR values fail before transport; inconsistent responses and
   wrong return kinds cannot silently cross the adapter.
4. The checked-in generated fixture wrapper is compiled into tests and drives
   every generated property and function through an injected native transport.
5. Native stale-handle categories survive as a structured managed exception.

## Completed Phase B.4 contract

1. `UEUnifiedScript_GetNativeTransportV1` exposes a versioned, size-checked C ABI
   table for property reads, property writes, and function invocation.
2. The function table borrows an invocation-bridge context and never exposes a
   UObject pointer, property address, or `ProcessEvent` parameter buffer.
3. UTF-8 result slices are callback-scoped and copied synchronously by managed
   code, so no allocation crosses runtime ownership boundaries.
4. `InProcessUnrealNativeInvocationTransport` rejects ABI/table/value layout
   mismatches and preserves native invocation and object-handle failures.
5. Native Automation exercises the table against a live fixture; managed tests
   drive the generated wrapper through an unmanaged version-1 table.

## Completed Phase B.5 contract

1. Each supported `UWorld` owns one `UUnifiedScriptWorldSubsystem`, registry,
   invocation bridge, and opaque native bridge context.
2. Managed transports retain and release context references explicitly and
   reject calls after disposal.
3. World teardown detaches the context, invalidates all handles, destroys the
   bridge before the registry, and releases the host reference in that order.
4. Active handle generations are allocated process-wide so matching slot numbers
   in separate World registries cannot alias.
5. UE 5.8 Automation verifies two-World isolation and retained-context rejection
   after World destruction; the native suite passes all four adapter tests.

## Completed Phase B.6 contract

1. A process singleton discovers hostfxr through an explicit .NET root or the
   engine-bundled .NET 10 runtime.
2. The checked-in runtimeconfig initializes Microsoft.NETCore.App without a
   machine-local absolute path.
3. The host acquires a non-null `load_assembly_and_get_function_pointer` delegate
   and exposes readiness to each native World host.
4. UE 5.8 Automation confirms CoreCLR is actually loaded inside UnrealEditor.
5. Per-World isolation is reserved for managed collectible load contexts; the
   implementation does not pretend multiple CoreCLR runtimes can be unloaded.

## Completed Phase B.7 contract

1. `UnifiedScript.Unreal.dll` and its dependency metadata are staged to the
   plugin's ignored managed-binary directory after every managed build.
2. Native hostfxr code resolves managed create, destroy, and count entry points
   marked `UnmanagedCallersOnly`.
3. Each managed World retains its native transport context and owns the managed
   script-instance registry.
4. World teardown destroys the managed entry before detaching native context.
5. UE 5.8 Automation verifies two managed entries and a return to the original
   count after both test Worlds are destroyed.

## Completed Phase B.8 demo contract

1. A separate generated gameplay assembly is built and staged under the plugin's
   ignored managed-binary directory.
2. The matching World's collectible load context loads that assembly and shares
   only the managed bridge contract with the process context.
3. Demo code constructs the generated `UnifiedScriptComponent` wrapper rather
   than calling a native test shim.
4. The wrapper writes `ManagedState` and `bStartAutomatically` and returns
   `Add(20, 22) == 42` on a live UObject; the component host drives Reload.
5. `tools/run-unreal-demo.ps1` builds, runs the UE Automation slice, preserves
   generated config state, and emits a readable result plus log path.

## Completed Phase B.9 component-host contract

1. `UUnifiedScriptComponent` exposes assembly/type configuration, automatic
   Start/Tick switches, running state, active generation, and last failure.
2. `BeginPlay`, component Tick, and `EndPlay` dispatch managed `OnStart`,
   `OnTick`, and `OnStop` while the UObject handle remains valid.
3. Each script generation owns a collectible load context and dependency
   resolver; Reload swaps to newly loaded DLL bytes and unloads the old context.
4. Stop removes the managed instance before releasing its generation-checked
   UObject handle; World teardown stops any remaining instances first.
5. UE 5.8 Automation verifies automatic Start, Tick, two Reload paths, Stop,
   generation advancement, wrapper property writes, and `Add(20, 22) == 42`.

## Completed Phase B.10 generated-fast-thunk contract

1. The binding generator derives a deterministic non-zero 64-bit UTF-8 FNV-1a
   ID from every canonical member path and emits both ID and readable path.
2. Generated wrappers choose the optional fast bridge only when it is available;
   existing bridges and the complete ABI v1 function table remain compatible.
3. ABI v2 preserves the v1 table prefix and appends property read/write and
   function invoke entry points keyed by Member ID.
4. The generated `UUnifiedScriptComponent` native surface resolves its weak
   handle, then accesses C++ fields/methods without path parsing, reflected
   lookup, parameter storage, or `ProcessEvent`.
5. Managed tests prove v1 fallback and v2 selection; native Automation proves
   both tables and direct thunks; the current hosted demo reports 16 fast calls.

## Completed Phase B.11 Script Asset contract

1. `UUnifiedScriptAsset` is a schema-versioned Data Asset containing managed
   assembly/type identity plus an optional authoring source path.
2. A component prefers its Content Browser asset reference, may resolve a soft
   `ScriptAssetPath`, and retains direct assembly/type fields only as fallback.
3. Start validates the asset, expands path tokens, checks the assembly, and
   exposes the active asset identity without making the asset own instance policy.
4. Reload passes a newly resolved assembly/type into CoreCLR and preloads the
   next collectible generation before stopping the old one.
5. UE Automation proves an invalid asset/type does not advance generation or
   stop the current script, then switches to a second managed type successfully.

## Completed Phase B.12 automatic native thunk generation

1. Reflection exports native header and access metadata for supported members.
2. `ue-bindings --native-out` emits deterministic ABI v2 C++ dispatch for all
   supported public members instead of a fixture-specific switch.
3. Checked-in managed bindings and native thunks are regenerated from a live UE
   5.8 manifest and protected by golden tests.

## Completed Phase B.13 Declaration IR state materialization

1. Script Assets optionally identify emitted Declaration IR and one asset key.
2. The component materializes scalar `[Expose]` fields into a fixed-layout
   Property Bag with defaults, editor metadata, and Unreal property flags.
3. Declaration semantic IDs become descriptor GUIDs, preserving compatible
   instance overrides across rename and Reload while adding/removing fields.
4. Invalid declarations and failed managed reloads retain the old generation
   and state atomically.
5. UE 5.8 Automation proves default mapping, stable-ID migration, failed-input
   rollback, and a live managed Demo reading instance override 37 through a
   generated direct thunk (`FastCalls=16`).

## Completed Phase B.14 native Slate Authoring Surface

1. The editor module registers a native nomad tab and Tools-menu entry.
2. A strict reader consumes `unifiedscript.authoring-surface/v0.1` without
   duplicating the C#/Graph semantic pipeline in Unreal.
3. The workspace loads a file, selected Script Asset, or staged Demo and shows
   state, functions, structured regions, actions, semantic IDs, and source.
4. Typed literal/operator capabilities export `unifiedscript.patch/v0.19`
   documents with the surface compilation/configuration preconditions.
5. UE 5.8 Automation loads the real Demo surface, rejects schema drift,
   constructs the Slate widget, and verifies the shared patch contract.

## Completed Phase B.15 transactional Slate Apply

1. Slate submits typed patches to a configurable loopback Workbench endpoint
   and parses only authoring-workspace v0.2 responses.
2. The server compiles multiple source files and atomically publishes primary
   C#, `.usids`, Declaration, Surface, managed DLL, and portable PDB outputs.
3. First-time sidecar initialization retains the deterministic IDs already
   present in the loaded Surface.
4. Successful responses refresh Slate and reload matching running components;
   inactive components refresh their Declaration-backed state.
5. A real `100 -> 101 -> 100` Demo round trip advanced two Workbench
   generations, retained IDs, emitted a live UE-loadable assembly, and passed
   the existing World-host lifecycle Demo.

## Non-goals of Phase A

- no raw pointer is treated as a durable managed identity;
- no `FProperty` memory is read or written from managed code;
- no `ProcessEvent` call is issued;
- no CoreCLR host is embedded in the editor process;
- no Blueprint variable is generated as a second declaration source;
- no editor UI parses C# or Graph independently of Authoring Surface.

## Phase B acceptance gate

The full phase is complete only when the compiled lifecycle test executes, a
live fixture UObject is reached through the generated managed wrapper, a
primitive property can be read/written, a fixture UFunction can be invoked
through `ProcessEvent`, destroyed objects fail with a stable error, and all
access is rejected off the owning game thread. A one-off test API does not
satisfy the gate.
