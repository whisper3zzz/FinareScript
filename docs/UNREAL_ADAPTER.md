# Unreal adapter

M6.1 integrates the proven language/runtime/authoring pipeline with Unreal
Engine through adapters. It does not move UE reflection types into the core and
does not introduce a second graph or VM.

## Phase A vertical slice

The first slice has four explicit boundaries:

```text
UE 5.7 native reflection
  -> UnifiedScriptExport commandlet
  -> unifiedscript.unreal-reflection/v0.1
  -> UnifiedScript.Unreal binding generator
  -> typed C# wrappers over IUnrealInvocationBridge
```

The checked-in host is `HostProject/UEUnifiedScriptHost.uproject`. Its plugin
contains:

- `UEUnifiedScriptRuntime`: `UUnifiedScriptComponent` and the eventual native
  runtime/lifetime bridge;
- `UEUnifiedScriptEditor`: reflection export and future Slate/editor tooling.

The .NET `UnifiedScript.Unreal` project contains no UE dependency. It owns the
wire contract, validation, normalization, managed wrapper generation, and the
handle-only bridge API. This makes the extractor and generator testable without
launching an editor and prevents engine upgrades from contaminating HIR,
Semantics, Graph, or Authoring.

## Reflection manifest v0.1

The manifest records only portable identity and callable shape:

- exact engine major/minor/patch and branch;
- explicitly selected native module names;
- native class path, module, base path, and traits;
- Blueprint-visible declared properties with access policy;
- Blueprint-callable/pure declared functions with ordered parameters;
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

`UnrealObject` rejects a zero handle and a null bridge. This is only the managed
side of the ownership contract: Phase B must add a native weak-handle table,
serial/generation validation, game-thread checks, and destruction notification
before any real UObject access is allowed.

## Commands

The helper keeps machine-local paths and all generated output outside Git:

```bash
tools/ue-adapter.sh detect
tools/ue-adapter.sh build
tools/ue-adapter.sh export
tools/ue-adapter.sh bindings
tools/ue-adapter.sh verify
```

Set an explicit engine when discovery is ambiguous:

```bash
UNIFIEDSCRIPT_UE_ROOT=/path/to/UnrealEngine tools/ue-adapter.sh verify
```

The build target is `UEUnifiedScriptHostEditor Mac Development`. Export invokes
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

## Remaining M6.1 phases

Phase A proves schema and toolchain connectivity. It does not claim that UE can
yet execute gameplay C#.

Phase B adds native invocation and lifetime safety:

- weak object handles with serial validation and explicit invalidation;
- property marshalling and `ProcessEvent` invocation;
- game-thread/world ownership and structured native errors;
- generated wrapper integration tests against real fixture objects.

Phase C adds the shared runtime domain:

- one CoreCLR domain per `UWorld` policy;
- component-to-script asset loading and hot reload;
- C# declaration state materialized directly on the component/Details surface;
- no duplicate Blueprint variable declarations.

Phase D adds the editor and distribution surface:

- Slate frontend consuming Authoring Surface v0.1 and the same patch APIs;
- semantic selection/debug identities retained across UE reloads;
- cook staging, managed dependency closure, platform runtime selection, and
  packaging verification.
