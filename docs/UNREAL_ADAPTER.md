# Unreal adapter

M6.1 integrates the proven language/runtime/authoring pipeline with Unreal
Engine through adapters. It does not move UE reflection types into the core and
does not introduce a second graph or VM.

## Phase A vertical slice

The first slice has four explicit boundaries:

```text
UE 5.7/5.8 native reflection
  -> UnifiedScriptExport commandlet
  -> unifiedscript.unreal-reflection/v0.5
  -> UnifiedScript.Unreal binding generator
  -> typed C# wrappers over IUnrealInvocationBridge
```

The checked-in host is `HostProject/UEUnifiedScriptHost.uproject`. Its plugin
contains:

- `UEUnifiedScriptRuntime`: `UUnifiedScriptComponent` and the eventual native
  runtime/lifetime bridge;
- `UEUnifiedScriptEditor`: reflection export and native Slate authoring tooling.

The .NET `UnifiedScript.Unreal` project contains no UE dependency. It owns the
wire contract, validation, normalization, managed wrapper generation, and the
handle-only bridge API. This makes the extractor and generator testable without
launching an editor and prevents engine upgrades from contaminating HIR,
Semantics, Graph, or Authoring.

## Reflection manifest v0.4

The manifest records only portable identity and callable shape:

- exact engine major/minor/patch and branch;
- explicitly selected native module names;
- native class path, module, base path, and traits;
- Blueprint-visible declared properties with access policy;
- Blueprint-callable/pure declared functions with ordered parameters;
- Blueprint-assignable multicast delegates with canonical paths, traits, and
  ordered parameter types;
- stable display/category/tooltip/deprecation metadata plus latent, thread, and
  World-context execution policy used by generated authoring surfaces;
- recursive value types for primitives, UObject/class handles, structs, enums,
  arrays, sets, and maps.

It deliberately excludes absolute engine paths, timestamps, memory addresses,
display-localized labels, and reflection iteration order. Modules, types,
properties, and functions normalize by ordinal identity. Function parameter
order is retained because it is semantic.

Unknown JSON members, duplicate identities, malformed `/Script/...` paths,
inconsistent container types, blank modules, and `void` values outside return
positions are rejected. An unsupported native property is represented
explicitly as `unsupported`; the binding generator emits a stable diagnostic
and omits that member instead of guessing a marshal layout.

## Managed bindings

`ue-bindings` produces one sealed partial wrapper per reflected class. A wrapper
contains the native class path and exact member paths, but never stores a raw
`UObject*`. It receives:

- `UnrealObjectHandle`: an opaque native-owned handle;
- `IUnrealInvocationBridge`: the only property/function crossing point.

Binding v0.1 generates primitives, strings/names/text, object handles, and
recursive list/set/map shapes. Native structs and enums, static functions,
and output/reference parameters remain explicit deferred diagnostics until the
marshalling and class-default-object rules exist.

`UnrealObject` rejects malformed handles and a null bridge. Handles are unsigned
64-bit values rather than addresses: the low 32 bits encode `slot + 1`, the high
32 bits encode a non-zero generation, and zero is null.

## Phase B.1 UObject lifetime boundary

`FUnifiedScriptObjectRegistry` is the native authority for those handles. Each
registry is bound to one `UWorld`, stores only `TWeakObjectPtr<UObject>`, and
requires the game thread for registration, resolution, release, and bulk
invalidation. Duplicate registration returns the existing handle. A released or
deleted object retires its slot; reuse receives a new process-wide generation.
Active handles therefore cannot alias either a later UObject or an object in a
different World registry.

The registry is a `FUObjectArray::FUObjectDeleteListener`; deletion notification
removes the object identity immediately rather than relying only on lazy weak
pointer checks. Stable result categories distinguish off-thread access,
unavailable worlds, null objects, world mismatch, malformed handles, and stale
generations. No `FProperty` access or `ProcessEvent` invocation has been added in
this slice.

The native Automation Test covers registration identity, world isolation,
release/reuse, off-thread rejection, GC deletion, and stale lookup. It compiles,
links, and passes under an installed UE 5.8.1 Release build. The same host also
runs `UnifiedScriptExport` successfully, producing one fixture type with three
properties and two functions; that live manifest passes the managed binding
generator. A separate source-engine checkout had corrupt local DLL outputs, but
that was an engine-build artifact rather than a UE 5.8 compatibility boundary.

## Phase B.2 primitive invocation core

`FUnifiedScriptInvocationBridge` resolves every target through
`FUnifiedScriptObjectRegistry` before member lookup. It accepts the exact
`/Script/Module.Type:Member` identities emitted into generated wrappers, checks
the target class and Blueprint access flags, and owns all transported values.
No property address, parameter buffer, or UObject pointer escapes the call.

The first marshalling slice supports `bool`, `int32`, `FString`, and void. It
reads and writes the fixture properties and invokes `Add` and `RequestReload`
through `ProcessEvent`. Unsupported types, output/reference parameters, wrong
argument counts, wrong value kinds, non-callable functions, cross-thread use,
and stale handles fail before invocation with stable categories. The installed
UE 5.8.1 Automation run passes the lifetime, primitive invocation, and native
transport ABI tests.

## Phase B.3 managed invocation adapter

`NativeUnrealInvocationBridge` implements `IUnrealInvocationBridge` and converts
generated-wrapper CLR `bool`, `int`, and `string` values into the same owned
value kinds used by the native core. Native invocation and handle categories are
preserved in `UnrealInvocationException`; the numeric values are fixed on both
sides of the boundary.

The checked-in generated `UnifiedScriptComponent` wrapper is compiled into the
.NET test suite. It reads and writes all fixture properties, calls `Add` and
`RequestReload`, and preserves a simulated native stale-handle failure through
an injected `IUnrealNativeInvocationTransport`. This proves generated-wrapper
and CLR-side semantics without pretending that the process boundary exists.

## Phase B.4 versioned in-process transport

`UEUnifiedScript_GetNativeTransportV1` exports a size-checked native function
table for property reads, property writes, and function calls. Responses use a
synchronous callback whose UTF-8 slices are valid only for the callback, so the
managed side copies them immediately and neither runtime frees memory allocated
by the other.

`InProcessUnrealNativeInvocationTransport` validates the ABI version, table
size, value size, and required entry points before accepting calls. Native
Automation covers the exported table against a live fixture UObject; managed
tests drive the checked-in generated wrapper through an unmanaged function table
and verify ABI-layout rejection and stable stale-handle propagation. The table
also exposes context retain/release operations; the managed transport implements
`IDisposable` and serializes calls against disposal.

## Phase B.5 world host and bridge-context lifetime

`UUnifiedScriptWorldSubsystem` is instantiated once for each supported Game,
PIE, or Editor World. It exclusively owns that World's weak-object registry and
invocation bridge. Its opaque `FUnifiedScriptNativeBridgeContext` starts with one
host reference; each managed transport retains another reference during
construction and releases it on disposal.

World teardown first detaches the context, waiting for a current synchronous call
to leave. It then invalidates every UObject handle, destroys the bridge before
the registry, and releases the host reference. A managed reference may keep the
small detached context object alive, but subsequent calls return
`TransportUnavailable` and cannot reach freed UE state.

The UE 5.8 Automation suite creates two Worlds, verifies distinct hosts and
contexts, proves handles cannot alias across contexts, retains one context across
World destruction, and verifies safe rejection afterward. Phases B.6-B.9 build
the process host, managed World entry, and hosted C# demo on this lifetime base.

## Phase B.6 process CoreCLR bootstrap

`FUnifiedScriptCoreClrHost` discovers .NET through
`UNIFIEDSCRIPT_DOTNET_ROOT`, `DOTNET_ROOT`, or the engine-bundled .NET 10 runtime.
It dynamically loads hostfxr, initializes the checked-in runtimeconfig, and
acquires `load_assembly_and_get_function_pointer`. CoreCLR is process-wide;
World isolation will be provided by managed collectible load contexts rather
than by attempting to start one CLR per World.

The UE 5.8 lifecycle test confirms the real engine process loaded hostfxr 10.0.7
and received a non-null load-assembly delegate. Managed assembly staging,
entry-point invocation, and per-World unload are completed in Phase B.7.

## Phase B.7 managed World entry and collectible domain

The `UnifiedScript.Unreal` build stages its managed host DLL and dependency
manifest under the plugin's ignored `Binaries/Managed` directory. The native
process host resolves three `UnmanagedCallersOnly` methods: create World, destroy
World, and query active World count.

Each managed World entry constructs `InProcessUnrealNativeInvocationTransport`,
retains the native context, and owns the script-instance registry. Native
teardown invokes the managed destroy entry first, stopping and unloading every
script generation and releasing the transport before native detach and registry
destruction. UE 5.8 Automation verifies two entries are created and their count
returns to baseline as both Worlds are destroyed.

## Phase B.8 hosted managed demo

`UnifiedScript.Unreal.Demo.dll` is staged beside the managed host. Its
`DemoScript` constructs the checked-in generated `UnifiedScriptComponent`
wrapper and proves live property access plus `Add(20, 22) == 42`.

## Phase B.9 component-driven lifecycle and reload

`UUnifiedScriptComponent` now exposes `ScriptAssemblyPath`, `ScriptTypeName`,
automatic start/tick switches, running state, active generation, and last error.
`BeginPlay` registers the component and dispatches managed `OnStart`; Tick calls
`OnTick`; `EndPlay` calls `OnStop` before releasing the UObject handle.

Each script instance loads into its own collectible generation context. Reload
preloads the next generation, stops the old instance, starts the new instance,
then unloads the old context. Assembly and PDB bytes are loaded from shared-read
streams so Windows builds can replace staged files while an older generation is
still alive. UE 5.8 Automation verifies automatic Start, Tick, two reload
generations, Stop, handle release ordering, and zero remaining script instances.

Run the complete build-and-demo path on Windows with:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-unreal-demo.ps1 -EngineRoot D:\UE_5.8
```

The same demo can be attached to an Editor actor by creating a
`UnifiedScriptAsset` Data Asset and configuring:

```text
ScriptAssemblyPath = {PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll
ScriptTypeName     = UnifiedScript.Unreal.Demo.DemoScript
```

Assign the Data Asset to `UnifiedScriptComponent.Script Asset`. The component's
direct fields remain available under advanced settings for compatibility.

## Phase B.10 generated native fast thunks

The binding generator now hashes each canonical member path with stable UTF-8
FNV-1a and emits the 64-bit ID beside its readable path. `UnrealObject` selects
`IUnrealFastInvocationBridge` when available and otherwise calls the original
string-path bridge, so old injected bridges and ABI v1 hosts remain compatible.

`UEUnifiedScript_GetNativeTransportV2` preserves the complete ABI v1 prefix and
appends read/write/invoke entry points keyed by Member ID. The first generated
native thunk table covers the full reflected `UUnifiedScriptComponent` surface.
It still resolves the world-scoped weak handle first, then accesses fields or
calls C++ methods directly without path decoding, `FProperty` lookup,
`FStructOnScope`, or `ProcessEvent`. Native and managed tests cover both ABI
versions. At this milestone the live UE demo reported `FastCalls=16`, proving
the hosted generated wrapper selected stable-ID v2 thunks during
Start/Tick/Reload/Stop; the later ABI v3 activation splits that same counter by
call-frame and legacy route.

This established an executable component host with a measured route counter.
Automatic thunk generation and a reproducible matched Puerts comparison gate
now build on that boundary; the faster-than-Puerts verdict is taken only from
the same machine, engine, configuration, warmup, and sampled workload, never
from the C++ lower bound.

## Phase B.11 Script Asset resolution and transactional reload

`UUnifiedScriptAsset` is a native UE Data Asset with schema version, managed
assembly path, managed type name, and optional authoring source path. It can be
created through the ordinary Content Browser Data Asset flow and selected on a
`UUnifiedScriptComponent`. Components may alternatively load the same asset from
`ScriptAssetPath`; direct assembly/type fields remain an advanced compatibility
fallback.

Start validates the asset schema and entry configuration, expands project/plugin
path tokens, checks the DLL, and records the active asset identity. Reload now
passes a freshly resolved assembly and type through the native CoreCLR entry
point. Managed code preloads the candidate collectible generation before
stopping the current one; a missing type or invalid asset therefore leaves the
previous script running. The UE demo starts from a transient Script Asset,
rejects an invalid asset reload without advancing generation, then switches to
`AlternateDemoScript` and completes Tick/Stop successfully.

The remaining asset work is cook/package dependency discovery.

## Phase B.12 automatic native thunk generation

The reflection manifest now carries each native class header and the native
public/protected/private access of exported properties and functions. The same
`ue-bindings` command accepts `--native-out` and deterministically emits a C++
translation unit keyed by the exact Member IDs used in managed wrappers.

Type references also carry the defining Unreal header. Generated static
services can therefore decode concrete object/class parameters such as
`UParticleSystem*` and `TSubclassOf<UDamageType>` without a hand-maintained
include list or an unsafe generic UObject cast.

The generated dispatcher now covers all scalar widths plus `FString`, `FName`,
`FText`, reflected enums, `FVector`, `FRotator`, and `FTransform`. It casts the
already handle-validated UObject once and then accesses the native field or
invokes the C++ method directly. It performs no member-path parsing,
`FProperty` lookup, parameter-buffer allocation, or `ProcessEvent`. Read-only
properties, bad target types, enum type mismatches, argument-count errors, and
value-kind mismatches retain the existing stable bridge failures. Unsupported
or non-public native shapes produce deterministic `USUE300x` diagnostics
rather than silently falling back to unsafe direct access.

`ue-bindings --report <path>` combines policy, managed, and native generator
diagnostics into deterministic `unifiedscript.unreal-binding-coverage/v0.2`
JSON. Every selected member is classified as `directThunk`, `callFrame`,
`dataPlaneOperation`, `reflectionFallback`, or `rejected`. The contract carries
minimum per-route counts and a maximum rejected count; falling below that
baseline fails generation with stable `USUE51xx` diagnostics.
`--fail-on-rejected` exits with code 65, so projects can make incomplete API
coverage a build gate. Runtime member-ID
misses select the exact ABI v1 reflected path. Its runtime codec now covers
bool, all supported integer widths, float/double, string/name/text, object/class
handles, typed enums and Vector/Rotator/Transform. Reflected property access
also carries typed soft-object/soft-class paths. The common-value coverage test
keeps property/function fallback classification aligned with these families.
The report rejects missing direct
thunks for containers or `out`/`ref` functions because the legacy value contract
cannot preserve those shapes.

The checked-in runtime thunk file is generated from the checked-in live UE 5.8
manifest, including `UUnifiedScriptInteropFixture`, and a golden test prevents
it from drifting. UE Automation executes generated int64, enum, FVector, and
FTransform property/function thunks against that fixture. ABI v1/v2 remain
available for existing hosts. Without changing the frozen v1 table or value
struct layout, common values use the following encoding:

| Kind | Encoding |
| --- | --- |
| Int8/UInt8/Int16/UInt16/Int32 | `Int32Value`, with narrow-width range validation |
| UInt32 / Float32 | Unsigned integer / IEEE-754 bit pattern in `Int32Value` |
| Int64/UInt64, Object/Class, Float64 | Exactly eight little-endian payload bytes |
| Vector / Rotator | Three binary64 cells: XYZ / Pitch-Yaw-Roll |
| Transform | Ten binary64 cells: quaternion XYZW, translation XYZ, scale XYZ |
| Enum | Signed 64-bit value, unsigned 64-bit stable type ID, optional UTF-8 path |
| SoftObject / SoftClass | UTF-8 asset path with distinct owned-value kinds 27 / 28; empty path is null |
| Array / Set / Map | Owned kinds 22 / 23 / 24 carry a V3 root cell and its copied arena |

The appended soft kinds apply to reflected property access. Existing generated
soft-reference calls retain their published path-string encoding and ABI v3
descriptors; the new state path does not change that protocol.

Enum paths are limited to 4096 bytes, cannot contain a null byte, and must agree
with the type ID when present. Generated results can carry an ID without a path:
typed managed enum consumers validate the expected identity, while an untyped
`UnrealEnumValue` consumer requiring a path fails instead of inventing one. Boxed
typed enum arguments retain their runtime enum metadata. Payload sizes and
integer ranges are checked before dispatching writes. The callback owns neither
slice and must copy it synchronously. Update managed and native SDK
parts together for newly supported kinds; old hosts do not gain these codecs
automatically. Collection property access reuses the V3 value codec while keeping
the frozen table and value layouts. Root kind and canonical container type ID,
child cells, arena bounds and payload ownership are validated at this boundary.
The payload contains the 48-byte V3 root followed by its arena; offsets are
relative to the arena, and the combined payload is limited to 64 MiB. Set/Map
keys use native identity rules, including Name case and null-name normalization
and case-insensitive soft asset paths with case-sensitive subobject paths.
Generated member calls continue to use their existing ABI v3/v4 path.

This transport support does not expand the generated Script Class property/default
matrix to every type. The connected example proves long state and enum/Rotator/
Transform RPC; broader Script Class state declarations remain a separate gap.
UE still controls network serialization: FRotator RPC axes use its compressed-short
format and wrap angles, whereas local bridge transfer preserves the original
binary64 cells. Do not confuse engine wire quantization with bridge conversion.

ABI v3 is frozen as an independent caller-owned synchronous call-frame contract
in `UNREAL_ABI_V3.md`. Managed and native layouts are locked at 48-byte values,
72-byte frames, a frozen 64-byte table prefix, and a current 72-byte export
whose optional suffix carries the generated `int32(int32, int32)` scalar lane. The live export now
advertises fixed scalars, enums, object/class handles, vector/rotator, transform,
UTF-8 text, reflected structs, recursive arrays/sets/maps, and return plus `out`/`ref`; generated
calls use a strongly typed span/stack frame whose fixed path has zero
steady-state managed allocation. Variable-size overflow is retained by the
World context and copied on an automatic expanded-arena retry without a second
UFunction dispatch. The append-only v2 bootstrap exposes the optional v3 table
to existing World entry points; v2 remains for old hosts and unsupported
reflection shapes.
Object/class values continue to be generation handles only; no raw `UObject*`
crosses the managed boundary.

The first shared-data plane is specified separately in
`UNREAL_SHARED_DATA_ABI_V4.md`. It publishes a double-buffered per-World Actor
Transform snapshot and exposes it to managed code as a borrowed
`ReadOnlySpan<UnrealTransformSnapshotEntryV4>`. This is a high-volume data path,
not a replacement for ABI v3 calls: UObject side effects, resource operations,
and reflected functions remain on the type-safe Binding control plane.

`UNREAL_GAMEPLAY_DATA_ABI_V4.md` adds the first side-effect and query layer to
that World data plane. `OnWorldPreActorTick` publishes snapshots/events and
opens `ManagedTick`; `OnWorldPostActorTick` closes it and executes committed
commands in deterministic reservation order. Managed code writes fixed command
or trace rows directly into native-owned storage. Line/sphere/box trace results
are returned by one synchronous call, while command completions are published
through a double-buffered event view on the next frame. The append-only v2
bootstrap advertises this independent table only when every size, capability,
and entry point is compatible.

## Phase B.13 Declaration IR state materialization

`UUnifiedScriptAsset` now carries an optional declaration JSON path and exact
asset key. `UUnifiedScriptComponent.RefreshScriptState` parses
`unifiedscript.script-asset/v0.1`, maps the reviewed scalar/enum plus `FName`,
`FText`, `UObject`, `UClass`, soft-object/soft-class paths, `FVector`, `FRotator`
and `FTransform` state matrix, plus reviewed Array/Set/Map configuration, into a
fixed-layout
`FInstancedPropertyBag`, and applies C# defaults plus Details metadata and
Edit/SaveGame/Net flags. The C# declaration remains authoritative; Blueprint
does not own duplicate field declarations.

Each state semantic ID becomes the Property Bag descriptor GUID. M8.3 now
serializes the exact override GUID set separately from the value bag. A
candidate layout inherits current declaration defaults and migrates only
explicit, type-compatible overrides by ID across renames, additions, removals,
Start, and Reload. Untouched values therefore follow source/graph default edits;
incompatible overrides are dropped instead of being numerically coerced into a
new contract. Parsing and managed generation swaps are transactional: a bad
document or failed managed entry keeps the prior running generation, state bag,
and override set. See [STATE_OVERRIDE_MIGRATION.md](STATE_OVERRIDE_MIGRATION.md).

The same Declaration IR default may be authored as a property initializer or
as a direct constant assignment in the explicit parameterless C# constructor.
The latter is the default Script Class scaffold form. Constructor order is
canonical, but only unconditional top-level simple assignments are projected;
non-deterministic constructor shapes fail before native class mutation. This
keeps generated CDO defaults, managed state hydration, compatible override
migration, and packaged reload on one typed declaration contract.

The compiled declaration payload uses v1.4 for collection state,
v1.3 for soft-reference state and v1.2
for generated Unreal enum wrappers, Rotator and Transform state. Older shapes
retain v1.1 encoding, and v1.0 scalar
decoding remains supported. The optional Unreal type descriptor identifies each
reflected enum or struct exactly and retains ordered collection type arguments.
The generated class projects the common shapes to native
`FNameProperty`, `FTextProperty`, `FObjectProperty`, `FClassProperty`, and
`FStructProperty` fields; the three core struct descriptors must resolve to
their exact `/Script/CoreUObject.Vector`, `.Rotator` or `.Transform` identity.
Generated enum state becomes a K2 enum byte property; its default must be a
declared UEnum value in byte range. Ordinary CLR enum state retains its legacy
member-name string representation. Object/class source defaults must be null because
nonzero managed handles belong to one native registry generation, while normal
Blueprint CDO edits may still store real reflected object/class references.
Managed instance startup and compatible reload hydrate/flush the same
`UnrealName`, `UnrealText`, `UnrealObjectHandle`, `UnrealClassHandle`, and
`UnrealVector`, `UnrealRotator`, `UnrealTransform` and generated enum state
without a facade field. Transform defaults retain quaternion, translation and
scale in Declaration IR and the binary payload. The Blueprint adapter translates
that value into Unreal's specialized Transform default text, shared with CDO
readback so untouched defaults and explicit overrides compare consistently.

Soft-reference defaults persist Unreal asset paths and materialize as
`FSoftObjectProperty` or `FSoftClassProperty`, with the corresponding Property
Bag value type. The ordinary managed state synchronizer reads and writes these
through the reflection property bridge. Its owned values distinguish soft
objects from soft classes; they do not become untyped strings or generation
handles. The frozen native table and value layouts are unchanged, and existing
value-kind numbers are preserved. Update managed and native parts together to
use these appended kinds.

Declaration validation, CDO reconciliation, hydration, flush and reload only
store and compare paths. They do not load referenced packages. Null paths use
the empty string in the declaration and `None` for Blueprint default import;
raw stored paths determine whether a value follows its source default or is an
explicit override. Actual asset loading uses the existing World-owned async
service, with the same cancellation and generation-lifetime rules.

The checked-in UE 5.8 demo stages its emitted declaration beside the managed
assembly, obtains the declared value 10 from `DemoScript()`, overrides
`InitialValue` to 37, reads 37 in `OnStart`, retains
it across Reload, and currently reports `DeclarationState=37`, `FastCalls=20`,
`CallFrameCalls=20`, `LegacyCalls=0`, and `ReflectedEvent=73`.

## Phase B.14 native Slate Authoring Surface

The editor module now registers a `UnifiedScript Authoring` nomad tab and a
Tools-menu entry. Its native Slate workspace consumes only the strict
`unifiedscript.authoring-surface/v0.4` artifact; it does not parse C# or recreate
Graph semantics inside Unreal. A surface can be opened directly, loaded from
the selected `UUnifiedScriptAsset.AuthoringSurfacePath`, reset to the staged
Demo, and automatically refreshed when its file changes.

The workspace presents Script Asset state and behavior as a semantic hierarchy:
function regions retain structured ownership of promoted actions, while the
inspector exposes kind/category, exact semantic ID, source span, source preview,
and source navigation. Literal and operator edit capabilities accept a candidate
value and export a preconditioned `unifiedscript.patch/v0.28` document to the
clipboard. This preserves one compiler-owned edit contract across browser and
Slate frontends.

The UE 5.8 editor Automation test loads the real Demo surface (one asset, one
state, four visual functions, 45 lossless actions, and 43 edges), rejects an
invalid schema transactionally, verifies the typed Event Entry payload pins,
builds semantic and declaration patches, verifies tab registration, and
constructs the real widget. Later phases add the live Workbench transaction and
persisted visual layout.

## Phase B.15 transactional Slate Apply and component reload

Each promoted literal/operator editor now exposes `Apply & Reload`. The Slate
client posts the exact `unifiedscript.patch/v0.28` payload to the configured
loopback Workbench URL and accepts only a strict
`unifiedscript.authoring-workspace/v0.5` response. The returned Surface replaces
the view without UE-side C# or Graph reconstruction; protocol diagnostics such
as stale compilation IDs and external-file conflicts are shown in the tab.

The same response now carries bounded transaction history. Slate sends an exact
baseline revision, unique operation ID, and author-facing label for each edit,
then enables Undo/Redo from server history rather than maintaining a second UE
edit stack. Navigation republishes source, identities, artifacts, and a fresh
CoreCLR generation before the canvas and matching components refresh. Its
status also distinguishes empty, restored, persisted, disabled, and rejected
history journals so a stale restart never looks like a valid empty stack.

`debug-serve` now supports repeatable `--source` plus transactional
`--asset-out`, `--surface-out`, `--layout-out`, `--managed-out`, `--pdb-out`, and `--history`
targets. A
successful mutation validates the complete multi-source candidate and publishes
C#, primary `.usids`, synchronized `.usgraph`, Declaration IR, Authoring Surface, uninstrumented managed
DLL, portable PDB, and the prospective `authoring-history/v0.1` journal through
one rollback-capable file transaction. Initial
sidecar creation retains existing deterministic Semantic IDs, so a checked-in
Surface remains a valid first-edit baseline.

After the response, Slate finds components associated with the selected Script
Asset or loaded Surface. Running instances call their existing transactional
`ReloadScript`; inactive instances refresh Declaration state. The real UE 5.8
Demo was exercised through a `100 -> 101 -> 100` Workbench round trip, retained
its stable IDs, emitted a loadable DLL, and then passed the live World-host Demo
with `DeclarationState=37`, `FastCalls=20`, `CallFrameCalls=20`,
`LegacyCalls=0`, and `ReflectedEvent=73`.

## Phase B.16 Blueprint-style GraphEditor workspace

The authoring tab now uses Unreal's native `SGraphEditor` rather than a custom
tree pretending to be a graph. Its Blueprint-style workbench keeps `My Script`
navigation on the left, a zoomable/pannable/marquee-selectable behavior canvas
in the center, and semantic Details/source/edit controls on the right. Function
selection rebuilds a transient `UEdGraph` from the current Surface; graph-node
selection drives the same inspector and double-click opens the exact C# span.

Custom editor-only nodes preserve the Surface action and region identities,
source order, scope depth, category colors, summaries, and source locations.
They are a read model: moving a card is transient, deletion/duplication is
disabled, and Surface v0.1 does not invent pins or execution wires that the
compiler has not emitted. A later Surface edge/port contract can therefore add
real Blueprint-like connections without creating a second semantic authority
inside Unreal.

## Phase B.17 Surface v0.3 native pins and wires

Surface v0.3 now promotes the complete Graph IR projection instead of only
control-bearing cards. Every node carries direction/kind/type-aware ports and
every compiler-owned control/data edge retains its exact semantic ID and
endpoints. Strict C# and UE readers reject dangling pins, wrong directions,
kind/type mismatches, duplicate edges, and multiply driven data inputs.

The Slate adapter allocates real `UEdGraphPin` objects, uses native Blueprint
execution-pin widgets, colors known data pins by compiler type, links every
Surface edge, and lays the graph out by dependency depth. Literal/operator
capabilities still use the existing transactional C# patch, rebuild, Surface
refresh, and live component reload loop. Manual rewiring is intentionally
disabled until a graph-rewire patch contract can preserve source correctness.

## Phase B.18 lossless Graph Presentation v1

The compiler still publishes the complete Graph IR projection, but the Slate
canvas no longer renders every identifier and literal as a full debug card.
Connected value leaves are folded into their consumer pins (`A = result`,
`Value = 100`), common execution-pin labels are hidden, unused data outputs are
suppressed, and assignment actions use author-facing titles such as
`Set result`. `ResolveInitialValue` therefore presents 7 nodes and 7 wires while
that function's strict Surface remains 14 nodes and 14 edges.

Kind/category, source location, semantic IDs, exact IR pin names, and type IDs
remain available in Details and tooltips. This is deliberately a read-only
projection policy: semantic selection, source navigation, literal/operator
patch targets, workbench transactions, and live component reload still resolve
against the unmodified Surface identities.

## Phase B.19 transactional data-edge authoring

Slate can now drag a compatible visible data output onto an occupied data input.
The schema does not mutate the transient `UEdGraph` immediately. It serializes
`reconnectDataEdge` with the exact compiler edge/endpoints/type from Surface
v0.2, submits it through the existing Workbench endpoint, and rebuilds from the
returned Surface only after the source/DLL/PDB transaction commits. A server,
precondition, type, effect, scope, or compilation failure therefore leaves the
old wire visible and reports the shared diagnostic.

The C# reverse path deliberately starts with bounded copy semantics: literals,
parameters/locals, and supported pure unary/binary/cast/conditional trees are
projected into the target through the existing typed expression patcher. It
rejects state reads and effects because copying those expressions could change
evaluation count or ordering. General shared-value/control rewiring will use
explicit temporaries and structural patch plans rather than silently changing
runtime semantics.

The staged Demo includes `RewirePreview(value, doubleValue)` as a positive
round-trip path. Select it in `My Script`, then drag the `*` data output onto
`Declare incremented` -> `Value`. The Workbench changes `value + 1` to
`value * 2`, rebuilds the managed artifacts, and refreshes the graph from the
new compiler-owned Surface.

## Phase B.20 managed UObject and Actor object model

`AUnifiedScriptActor` is now a placeable, Blueprintable native identity with a
default `UUnifiedScriptComponent` and Blueprint-visible managed state. Managed
gameplay code can derive from `UnrealObjectScript` or `UnrealActorScript`; the
latter maps the component lifecycle onto `OnBeginPlay`, `OnTick`, and
`OnEndPlay` overrides.

The runtime passes two independent generation-safe handles into each managed
context: `Host` identifies the component that owns the collectible generation,
while `Target` identifies the UObject whose behavior is implemented. Actor
components therefore target their owning Actor. Both handles remain World-bound,
are released after managed EndPlay, and are recreated without aliasing during a
new instance. Reload cancels and tears down the old managed lifetime before the
next generation receives BeginPlay.

The live UE 5.8 Automation gate spawns `AUnifiedScriptActor`, starts
`ActorDemoScript`, writes a Blueprint-visible Actor property, ticks through a
generated native thunk, reloads into generation two, and verifies managed
EndPlay executes before both handles are retired.

## Phase B.21 World-safe events and latent generation lifetime

The Actor host binds native `OnActorBeginOverlap` and `OnActorEndOverlap`
dynamic multicast delegates only while its managed instance is running. A
dedicated native-to-managed entry point dispatches a typed event to the active
collectible generation on the game thread. Event source and argument values use
the same World-bound object registry; registry entries are reference-counted so
temporary callback arguments cannot retire a handle still owned by another
script. Stop unbinds both delegates before managed EndPlay and handle release.

`UnrealObjectScript.DelayAsync` is a game-thread latent primitive advanced by
the component Tick rather than a thread-pool timer. Its continuation therefore
resumes inside the managed World call. Reload, EndPlay, failed BeginPlay, and
World teardown synchronously cancel every pending delay before the generation
is unloaded. `ActorDemoScript` proves latent resumption plus Begin/End overlap,
temporary argument release, reload cancellation, and post-EndPlay unbinding in
the live Automation gate.

This phase introduced the lifetime rules with common Actor overlap signatures.
The next phase applies the same World/generation ownership to arbitrary
reflected multicast delegates.

## Phase B.22 arbitrary reflected delegates and visual Event Entries

Reflection manifest v0.2 exports Blueprint-assignable delegates with canonical
paths and ordered value shapes. Types tagged `UnifiedScriptBindingInternal` are
excluded, so the transient native dispatch proxy never appears as a user API.
The binding generator emits a strong handler delegate, a typed subscribe method,
and an `UnrealEventEndpointAttribute` keyed by the canonical path.

The independent ABI v4 reverse-event table carries borrowed ABI v3 values in a
fixed 64-byte frame. Native code creates a transient proxy UObject whose runtime
UFunction clones the reflected delegate signature, binds it on the game thread,
and dispatches through the retained World context. A per-generation managed
bridge owns every subscription and releases it before ALC unload, rollback,
EndPlay, or World teardown. `[UnrealEvent]` methods are discovered after
`OnStart`; endpoint resolution uses the canonical path rather than a generated
C# member name.

Authoring Surface projection recognizes the attributed method as a red
`unrealEventEntry` node with a control continuation and typed payload outputs.
The GraphEditor context menu is populated from the same reflection manifest;
choosing `Unreal Events > <Type> > Event <Delegate>` submits declaration-patch
v0.2, writes the strongly typed C# handler, recompiles, and reloads the Surface.
The checked Demo receives `OnManagedSignal(73, Context)` before and after reload
without duplicate subscriptions.

Managed exceptions from Create/Start/Tick/event/reload/teardown boundaries are
stored per World and returned to native as UTF-8, so `LastScriptError` contains
the operation and managed stack instead of only a status code. The live UE 5.8
gate proves a failed `MissingScript` reload reports `ReloadScript` plus the
`TypeLoadException` while preserving the prior generation.

## Phase B.23 static services, World resources, and reflected structs

Blueprint function libraries now generate as `UnrealStaticService` wrappers.
Their managed instance retains the ordinary World context handle, while the
native thunk invokes the static C++ function directly and never casts that
context to the library class. `UUnifiedScriptWorldLibrary` supplies typed
object/class loading, Actor spawn and destroy, component lookup, input polling,
World time, and visibility tracing. Synchronously loaded objects are retained by
the World subsystem until teardown, so a managed handle cannot become stale
during Unreal GC while gameplay still owns the resource.

Async Service ABI v5 appends a separate World-owned service table for Delay and
asynchronous object/class loading. Requests carry script instance and collectible
generation identity; timeout, explicit cancellation, reload, EndPlay, and World
teardown remove native work before the managed load context is released. See
`UNREAL_ASYNC_SERVICE_ABI_V5.md` for fixed layouts and completion semantics.
Successful load handles are now recorded in a generation ownership ledger, so
reload and teardown release both the object-registry reference and native strong
reference instead of retaining every historical load. The lifecycle stress gate
also observes live/detached bridge contexts, native pending/completion/resource
counts, managed World entries, and retired collectible load contexts.

ABI v3 `Struct` cells carry an arena-backed ordered field-cell block keyed by
the canonical struct TypeId. The contract generator emits a readonly C# record
with stable field indices, matching native encode/decode functions, and visual
Make/Break Struct nodes. The exporter includes public native struct fields,
recursively discovers referenced structs, and normalizes FVector-derived
network serialization types to the FVector ABI.

The BlockBreaker product slice keeps resource/class loading, Actor spawn, and
`StaticMeshComponent.SetStaticMesh` on the generated Binding control plane.
Its per-shot collision query now uses one Gameplay Data Batch Trace, while
impact emission and damage are committed together through the command buffer;
the next-frame event view confirms both completions. `FHitResult`-compatible
trace rows cross no reflected struct boundary on this hot path. The Gun host no
longer contains resource, trace, cached-hit, effect, or damage facade functions.
The complete migration inventory covers 74/74 source and native items and the
target contains no Puerts/TypeScript payload. The matched three-trial
10,000-frame gate prepares both runtimes once, alternates execution order, and
currently reports a local median of 4,927.191 ms for UnifiedScript versus
4,759.930 ms for Puerts (ratio 1.035139). This is functional evidence, not a
performance claim; the accepted performance workload remains an M10 gate.

The performance gate builds managed code in Release, warms both runtimes with
4,096 calls, measures five 100,000-call samples, and uses the median. A
competitive Puerts result must match machine fingerprint, engine, platform,
configuration, warm-up, iteration count, and sample count. The checked gate
requires zero managed allocation and a UnifiedScript/Puerts ratio no greater
than 0.95; without a matched Puerts result the competitive verdict remains
empty. The generated scalar ABI v3 lane avoids generic call-frame construction
for the measured fixed signature while preserving the complete marshaller for
other shapes.

## Phase B.24 structured switch authoring

The native GraphEditor now exposes `Switch on Integer` beside the existing
structured flow templates. It emits the current Semantic Patch protocol over a real typed
function parameter and begins with explicit `case 0` and `default` break-only
sections. The Switch inspector inserts ordered case/default sections; the Case
inspector relabels, moves, or safely deletes the exact semantic section. Delete
key routing recognizes case actions and submits the same section transaction
instead of treating labels as ordinary statements.

## Phase B.25 callable extraction

The native GraphEditor toolbar now accepts one consecutive statement selection,
an explicit method name, and emits Semantic Patch v0.27 `extractCallable` with
the exact containing method, parent block, ordered statement range, and outer
sibling gaps. The UE client does not guess parameters or rewrite C#. The shared
Roslyn applier derives graph-safe inputs and at most one live result, creates a
private `[Visual]` helper, preserves moved semantic identities, recompiles every
projected Visual method, validates CFGs, and commits through the existing
Workbench transaction and reload boundary.

## Phase B.26 named grouped transactions

Workspace v0.4 adds a server-owned draft above the normal Workbench commit
path. Slate exposes **Begin Group**, a live staged-operation count, **Commit**,
and **Cancel Group**. Each graph/declaration request carries the active group ID
and a unique stage ID. Accepted stages refresh the compiler-owned Surface only;
they do not write source, reload CoreCLR, or reload matching components. Commit
publishes the final draft through one source/identity/artifact/history/runtime
transaction and creates one named Undo entry. Cancel restores the committed
Surface without touching the runtime generation.

## Phase B.27 context Palette and node discovery

The Blueprint workspace now docks a searchable Palette below **My Script**.
Its deterministic model tokenizes title, category, tooltip, action ID, and
generated contract keywords, then ranks exact/relevant matches before
Favorites, recently accepted placements, category, and title. `Ctrl+Space`
focuses search, Enter places the first result, double-click and the row `+`
button use the same action path as the native graph context menu.

The Palette is context-derived rather than a static catalog. Built-in actions
appear only when their required semantic types exist in the active function;
Break/Continue are limited to the selected loop/switch ancestry; reflected
properties/functions require a compatible receiver and parameters; Event Entry
actions remain legal at function scope while statement actions require a real
source block. Favorites and the bounded eight-entry Recent list persist in
`GEditorPerProjectIni`. A placement reaches Recent only after the Workbench
accepts its transaction, and preferences never alter source, identities,
Surface, Graph, or `.usgraph`. See [NODE_PALETTE.md](NODE_PALETTE.md).

## Phase B.28 typed Pin Authoring

Selecting a graph node now exposes every connected data input in **PIN
AUTHORING**. **Set Value** emits Semantic Patch v0.28 `setDataInputValue` with
the exact old edge, endpoints, ports, semantic type, and a closed typed literal
expression. **Reset** uses the same operation with a bounded typed default.
The Roslyn patcher rejects stale edges and literal/runtime-type mismatches before
the normal Workbench transaction can publish source or artifacts.

**Promote to Local** targets the expression feeding the pin and reuses
`introduceTemporary` with exact containing-statement, block, sibling, name, and
type facts. Stable pure expressions inside explicit local initializers and
returns become a typed declaration immediately before the statement; unsafe
effect/order or name conflicts reject without writes. The graph remains
compiler-owned until the accepted response reloads the next Surface. See
[PIN_AUTHORING.md](PIN_AUTHORING.md).

The bounded native pattern parser emits the shared JSON algebra for constant,
declaration, type, var, discard, relational, logical, negated, and
parenthesized patterns, plus literal or bool-parameter guards. It consumes
Authoring Surface v0.4 `sectionId` and `switchSectionId` properties and includes
old label and sibling identities in every request. Source text and visual wires
remain compiler-owned until the Workbench accepts, rebuilds, and publishes the
next Surface generation.

## Phase B.29 managed Script Class lifecycle and Blueprint events

Declaration IR and the compiled chunk now carry stable Actor/ActorComponent
`OnBeginPlay`, `OnTick`, and `OnEndPlay` lifecycle bindings. Reviewed
`[UnrealBlueprintEvent]` declarations carry a stable semantic function ID and
`blueprintEvent.superMode = explicit`; ordinary body edits therefore preserve
the generated event identity and its Blueprint references.

The Script Class factory scaffolds the Actor and ActorComponent lifecycle
overrides, while the materializer persists both lifecycle and event bindings on
the product GeneratedClass. Generated managed functions share one generic
native thunk. Their transient registrations are rebuilt during bind/load and
instance initialization, and prepared explicitly before Blueprint recompile,
so package reload does not depend on a handwritten facade or per-function C++
member.

A child Blueprint override remains ordinary Kismet bytecode and replaces the
managed default unless it contains `Call Parent Function`. On that explicit
parent route, the dispatcher uses `Stack.CurrentNativeFunction` and decodes the
Blueprint VM argument stream with `Stack.Step` into a reflected parameter
container before invoking the same World-owned managed instance used by direct
`ProcessEvent`. The focused live-child test, the 29/29 CMD Automation suite,
and the packaged Actor/Component Golden Path pass; evidence is under
`artifacts/m13-3b3a/`.

## Phase B.30 managed gameplay contracts

`[UnrealInputAction]` binds a managed method to one exact `UInputAction` object
path plus a trigger phase, value shape, and consume policy. Production code
should use an exact project asset path such as
`/Game/Input/IA_Move.IA_Move`; `/Script/...` fixture paths are reserved for
native contract tests. `[UnrealEvent]` continues to identify delegates by their
canonical owner/member path and host or target source. `[UnrealInterfaceFunction]`
identifies one exact native `UInterface` path and function name; its generated
native thunk invokes the interface `Execute_` path instead of assuming a concrete
receiver implementation.

The product GeneratedClass persists input, delegate, and interface binding
records beside lifecycle and function metadata. Its World-owned component binds
input and delegates only after the managed generation starts, routes interface
calls to that same per-World instance, and tears every binding down before the
generation unloads. Compatible reload therefore replaces rather than stacks
subscriptions. Native interface graphs do not retain
`FKismetUserDeclaredFunctionMetadata`, so ValidateOnly idempotency recognizes an
existing graph only when the persistent binding identity and exact reflected ABI
also match; incompatible user-owned functions still reject before mutation.

The .NET suite passes 678/678, repository validation passes 130/130,
deterministic generation passes 9/9, UE 5.8.1 Editor and Game builds, and the
29/29 CMD Automation suite passes. The full Cook/Package Golden Path proves
packaged input, exactly-once delegate delivery, native-to-managed interface
return dispatch, compatible reload without duplicate bindings, and deterministic
teardown. Evidence is under `artifacts/m13-3b3b/`.

## Phase B.31 common function values and multi-result directions

Ordinary public `[Visual]` methods can now expose `FName`, `FText`, `UClass`,
`FRotator`, and `FTransform` as Blueprint-visible parameters or return values.
C# `ref` and `out` lower to explicit input-output and output directions in HIR
and Declaration IR, then materialize as the corresponding generated UFunction
pins. Persistent bindings retain both parameter directions and stable source
names because Unreal may reorder output properties when linking a UFunction.

Managed ABI v3 receives inputs in source order and returns one ordered result
sequence: the non-void return value first, followed by each `ref`/`out` value in
source order. The generic native thunk resolves reflected properties by stable
name and writes results through either the direct `ProcessEvent` out-parameter
records or the Blueprint VM addresses captured while stepping the stack. This
keeps direct calls, child Blueprint `Call Parent Function`, package reload, and
World-owned static-library dispatch on one implementation without per-function
C++ facades.

The .NET suite passes 691/691, repository validation passes 132/132,
deterministic generation passes 9/9, UE 5.8.1 Editor/Game builds pass, and the
complete 29/29 CMD Automation suite plus a focused live static dispatch pass.
The 345,567 ms Cook/Archive/packaged Golden Path is recorded under
`artifacts/m13-3c1c/golden-path-final/`.

## Container and Soft Reference Blueprint VM checkpoint

Generated functions carry reviewed `IReadOnlyList<T>`, `IReadOnlySet<T>` and
`IReadOnlyDictionary<TKey, TValue>` values, plus soft-object and soft-class
handles, through input, return, `ref` and `out`. A saved ordinary Blueprint
caller now exercises all five families through compiled bytecode, including
downstream use of an output and forwarding it through the caller's own output.
Native `ProcessEvent` calls remain covered independently.

A nested VM call must retain the output addresses captured while stepping its
arguments. The enclosing frame's `OutParms` can contain the same source name
with a different destination; those records only supply destinations not already
captured for the current call. Output decoding remains transactional before
caller storage is updated.

Nested containers and known unhashable Blueprint Set/Map keys fail during
source lowering with a shape-specific diagnostic. Native reflected type
validation remains authoritative for other leaf types.

Static library cache slots are World-owned and retain current, prepared and
previous code generations during reload. The daily and selected-asset actions
prepare all loaded World copies of a library before committing any, restore the
previous generation on abort, and retire it on finalize. Existing callable
signatures must remain compatible; cold Worlds stay unloaded. Optional managed
control exports preserve ordinary dispatch with an older host while reporting
the reload feature as unavailable. No invocation ABI layout changes are needed.

The dedicated `UnifiedScript.Unreal.ScriptClass.StaticLibraryReload` CMD test
uses separately compiled v1/v2/incompatible DLLs and ordinary Blueprint callers
in two existing Worlds. It covers changed results, commit/abort restoration,
rejected candidates, cold-World exclusion, daily-action target deduplication
and World-host cleanup. Evidence is under
`artifacts/m13-3c-1c-b-static-library-reload`.

The same five exchange families also pass ordinary Blueprint caller bytecode
in a Win64 Development cooked HostProject. The packaged verifier checks exact
property shapes, unchanged input, ref replacement, out/return/downstream values,
World cleanup, and chunk/hostfxr paths inside the executing archive. This proves
soft-reference value transport; the fixture paths do not request asset loading.
Evidence is under `artifacts/m13-3c-1c-b-function-closure`.

After building the Host Editor and emitting the Demo chunk with the UE-bundled
.NET 10 SDK in Release, run the focused fixture/Cook driver from the repo root:

```powershell
pwsh -File tools/run-unreal-function-closure.ps1 -EngineRoot D:/UE_5.8 -CookAndRun
```

Omit `-CookAndRun` to retain the passing fixture for inspection, then use
`-UseExistingFixture -CookAndRun` to package that exact fixture. The driver
validates its receipt, checks UAT and packaged execution, and removes its own
temporary Content package after the Cook/run attempt.

## Explicit inherited-event parent calls

`[UnrealBlueprintOverride(declaringClassPath, functionName)]` binds a public
instance method to an existing inherited Blueprint event. The declaring path
must name the class that actually declares that event; synchronization verifies
the ancestry and reflected signature. `[UnrealBlueprintEvent]` continues to
declare a new managed event. See the
[Declaration contract](DECLARATION_IR.md#inherited-blueprint-event-declarations).

The managed override chooses whether to call its parent. For a void method with
only input parameters, use `CallParent(nameof(ManagedMethod), inputs...)`.
For a return value or `ref`/`out` parameters, use `CallParentWithOutputs` and
read its ordered results. Pure `out` parameters need no input placeholder:

```csharp
[UnrealBlueprintOverride(
    "/Script/UEUnifiedScriptRuntime.UnifiedScriptEventExtensionFixture",
    "EvaluateInherited")]
public int EvaluateInherited(int input, ref int accumulator, out int output)
{
    UnrealOutputInvocationResult parent = CallParentWithOutputs(
        nameof(EvaluateInherited), input, accumulator);
    accumulator = (int)parent.GetOutput(0);
    output = (int)parent.GetOutput(1);
    return (int)parent.ReturnValue;
}
```

This example uses the runtime test fixture; project code must use its own
reflected event path and signature. `GetOutput(index)` supports the current
Visual C# subset without an indexer or explicit generic member invocation.
`CallParent<TResult>` is also available to source-only helper code for an
input-only return contract. The
[Demo source](../samples/Unreal/UnifiedScript.Unreal.Demo/BlueprintParentInvocationDemoScript.cs)
includes the container form using `IReadOnlyList<int>`.

Parent resolution starts above the active Script Class in the actual UE class
chain. An ordinary Blueprint parent keeps its own implementation. The host
component selects the receiver, including ActorComponent scripts whose target
handle is their Actor. General managed-to-managed parent inheritance remains
outside this checkpoint and is rejected.

The optional parent bridge requires ABI v3 Parent Calls and Correlated Results
capabilities. Result-buffer retries retain the completed parent result; each
explicit helper call executes the parent once. Existing frame sizes and
transport-table pointers are unchanged. Calls require an active generation and
are rejected during managed initialization, destruction and after retirement.
Managed state is flushed before entering the parent and hydrated afterward.

Startup obtains the override contract from the Script Asset's exact generated
class, including when its receiver is an ordinary Blueprint child. Persistent
bindings and actual UFunction properties define the expected routes, ordered
parameter directions and nested codec shapes. The loaded DLL must match before
its constructor or lifecycle code runs; reload candidates retain the same
immutable expectation. A mismatch reports the route and requires stopping the
instance, synchronizing the Script Class and restarting it. Failed activation
uses the existing state, handle and ownership rollback.

This uses the optional named `CreateScriptWithBlueprintContract` managed export
without changing transport-table layouts. Component startup requires that export
even when the expected override set is empty, so stale classes cannot silently
activate new override methods. The legacy creation export remains available for
non-override types. If the validated export is absent, stage the updated managed
runtime before starting a Script Component.

Synchronization validates the complete persisted override contract and compares
it with the incoming Declaration before building a change plan. Active owners
are matched across Worlds by the Script Asset's exact generated class. A changed
route, managed name or signature requires stopping every affected instance;
same-contract synchronization remains allowed. The guard also applies to
validation-only requests. Case-only changes in managed names or raw route strings
are contract changes and are persisted after stopping the affected instances.

Component lifecycle-event ordering and the cooked extension fixture remain open in the
[active checkpoint](plans/2026-09-07-blueprint-event-extension.md). The existing
container/Soft Reference cooked result above predates this extension.

## Commands

The helper keeps machine-local paths and all generated output outside Git:

```bash
tools/ue-adapter.sh detect
tools/ue-adapter.sh build
tools/ue-adapter.sh export
tools/ue-adapter.sh bindings
tools/ue-adapter.sh verify
```

Direct CLI generation of both managed and native sides:

```bash
unifiedscript ue-bindings reflection.json \
  --contract game.unreal-api-contract.json \
  --out bindings.cs \
  --native-out native-thunks.generated.cpp \
  --nodes-out api-nodes.json \
  --report binding-coverage.json \
  --fail-on-rejected
```

For project-owned ABI v3 fast bindings, pass `--native-header` and a globally
unique `--native-namespace`, compile the emitted C++ in that game module, and
register its `FUnifiedScriptGeneratedThunkTable` for the module lifetime.

Set an explicit engine when discovery is ambiguous:

```bash
UNIFIEDSCRIPT_UE_ROOT=/path/to/UnrealEngine tools/ue-adapter.sh verify
```

The TargetRules use the selected engine's `Latest` build settings and include
order, mapping to V6/5.7 for UE 5.7 and V7/5.8 for UE 5.8. The helper build
target is `UEUnifiedScriptHostEditor Mac Development`. Export invokes
the editor headlessly with `-run=UnifiedScriptExport` and defaults to the
`UEUnifiedScriptRuntime` module. Runtime artifacts are written to
`artifacts/ue/`.

## External exFAT engine

The currently discovered engine is UE 5.7.4 arm64 under an external exFAT
volume. The repository and host project stay on APFS. Only engine binaries,
headers, and engine intermediate state remain on the external disk.

The first UBT build may be large because a source engine without compatible
intermediate state must rebuild editor dependencies. exFAT also creates `._`
metadata files and has poor small-file behavior. These files are not part of
the plugin and the exporter never records their paths. If iteration becomes
I/O-bound, moving the engine or at least its build/intermediate data to APFS is
the correct environment optimization; changing compiler architecture to hide
the disk issue is not.

The helper disables UBA and caps local compilation at four actions. On the
current 16 GiB machine, UE's default of ten concurrent unity compilations
causes heavy compression and swap and is slower overall. This is a local build
policy, not a runtime limitation.

## Current production boundary

M6.1 now executes gameplay C# through a World-owned CoreCLR host, generated
member thunks, and ABI v3. Script Assets materialize declaration state, managed
generations reload transactionally, and the Slate authoring surface commits
typed C# edits through the Workbench. Win64 Development and Shipping builds
stage a plugin-local .NET runtime plus managed dependency closure and pass a
packaged managed Start/Tick/Stop smoke. See `UNREAL_RELEASE.md`.

The generated allocation-free scalar lane now covers zero to four arguments
for the supported int, float, bool, object-handle, and FVector families, with
ABI v3 CallFrame fallback for broader shapes. The generic Blueprint function
lane also carries the reviewed common UE value types and ordered `ref`/`out`
results. The remaining production work is container and Soft Reference function
shapes, native debug traps and deeper evaluation, arbitrary control-wire
authoring outside structured operations, additional shared/gameplay data
schemas, and platform-specific CoreCLR packaging beyond Win64.
