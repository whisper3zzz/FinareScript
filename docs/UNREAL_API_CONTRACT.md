# Available Unreal API library

Setup generates supported declarations for configured UE modules before gameplay
uses them. IDE completion includes APIs that have never appeared in the project's
C# source. No project Contract, member allowlist, request overlay or binding lock
is required.

## Build and edit flow

1. Export public reflected types and members from `reflection.modules`, plus
   configured classes and Blueprint assets. Public members do not require
   BlueprintCallable or BlueprintReadWrite.
2. Project metadata through managed/native transport capabilities. Generate C#,
   native dispatch where necessary, optional Graph nodes, API browser and coverage
   together. Unsupported signatures are excluded from the available library; raw
   reflection retains their metadata.
3. Compile gameplay against this library. Adding or removing an available call
   does not change the library or native dispatch.
4. With the Editor open, saving `.cs`, `.csproj`, `.props` or `.targets` inside a
   configured managed project directory queues Build & Reload. Saves are
   debounced; generated files and build outputs are ignored. Play still waits
   for the build gate. Compilation/reload failure preserves the running
   generation and reports the error.

Reflection/library generation is cached on engine, module and native inputs.
Changing reflected C++ declarations, module configuration or generated native
layout still requires a native build and may require restarting the Editor.
External linked source files outside configured project directories are not
watched; use Build & Reload for those edits.

```powershell
finare ue-bindings Generated/Game.reflection.json --api-library `
  --namespace Game.Finare.Generated `
  --out Managed/Generated/Game.bindings.generated.cs `
  --native-out Source/Game/Generated/Game.thunks.cpp `
  --nodes-out Generated/Game.api-nodes.json `
  --browser-out Generated/Game.api-browser.json `
  --report Generated/Game.binding-coverage.json --fail-on-rejected
```

Setup runs this internally. `managed.projects` selects gameplay projects;
`bindings.namespace` names their available API namespace. See
[project onboarding](UNREAL_ONBOARDING.md).

## Dispatch and non-reflected C++

Supported reflected scalars, objects, enums, core math values, public value
structs, common containers and supported input callbacks use the generic
reflection bridge, including ordered `ref/out` results. Struct values use a
deterministic order of supported public fields. Fixed arrays, private/protected,
deprecated and editor-only fields are excluded; writing the remaining fields
preserves excluded native state. Interface fields, interface arrays/Map values,
and collections of custom structs within a struct use this same recursive
codec. Direct nested UE containers still follow UHT restrictions; an intermediate
USTRUCT supplies a supported layout. Interface Set elements/Map keys are not
UHT-supported declarations.

Normal getters return values. Generated `PropertyNative` companions provide
live array, set, map and struct operations. For example,
`actor.TagsNative.Add(new UnrealName("Selected"))` updates one native element;
`fixture.GroupNative.Items.ElementFields(0).Field<int>("Value").Value = 7`
updates one struct field. A view stores a World/object/generation and a property
path, resolves storage on each operation, and rejects an expired owner or index.
Array element views refer to the current index, not a durable element identity.
Set elements and Map keys are values to preserve native hash invariants. Map
values currently have get/set operations, not persistent address aliases.
Script Class code can use `NativeArray<T>(nameof(Property))`, `NativeSet<T>`,
`NativeMap<K,V>` and `NativeStruct` for its own reflected state. Authority and
read-only checks apply to live writes as well as normal setters.

`WithSpan`/`ReadSpan` borrow the exact allocation of numeric UE fields/arrays
and declared ordinary C++ `std::vector<T>`/`TArray<T>` fields. Supported element
layouts are `byte`, `int`, `uint`, `long`, `float` and `double`, with native
kind/size checks and a 64 MiB bound. The synchronous callback cannot await or
retain a safe Span; UE/native reentry that could invalidate storage is rejected.
Call again after resize to acquire the new address. Exceptions return through
the managed boundary and release the borrow; writes made before an exception
remain in native storage. Reference-bearing USTRUCTs, bool packing and arbitrary
C# struct reinterpretation are not supported memory layouts. This avoids the
bulk data copy, not all per-call allocation or dispatch overhead.

ABI v3 validates output
descriptors before gameplay executes and retains correlated results during buffer
growth, so retries do not invoke the function twice. World handles and
transactional reload rules remain unchanged.

Signatures requiring generated native transport receive dispatch code during the
initial library build. UHT cannot discover non-reflected C++: optional
`bindings.nativeExtensions` / `--native-extensions` supplies actual signatures,
headers and ownership. Supported declarations in that file join the library.
It is not a project member selection list. Owned and borrowed native objects
retain their existing disposal and generation lifetime rules.

Ordinary C++ declarations may specify one `baseTypePath` per native type. The
generated C# class inherits that wrapper and shares its ownership lease; generated
`static_cast` edges adjust native base pointers. Unknown/cyclic declarations
are rejected. This exposes one declared base chain, not arbitrary multiple-base
casts, RTTI downcasts or ownership transfer.

Input `std::function<R(A...)>` declarations generate typed
`UnrealCallback<Handler>` arguments. C++ may retain or copy the callable; it
holds a weak reference to the generation-owned endpoint. Disposing the callback,
closing the generation or destroying the World retires it; subsequent calls
return the default result without entering C#. Ordinary C++ callbacks are
synchronous and game-thread-only. Signatures support up to 16 fixed numeric,
bool, Vector/Rotator inputs and fixed/void results; ref/out, strings, enums,
arbitrary structs and callback returns require additional codecs. The native
type spelling must agree exactly with the declared callback signature.

The library covers supported signatures in configured modules, not every C++ API
in UE. An absent API needs metadata or transport support; writing a C# call alone
cannot make an unsupported ABI executable.

## Internal selection and verification

The in-memory projector remains named `UnrealApiContract`; it is an internal
model. Low-level `ue-bindings --project` and `ue-reflection-hints` remain useful
for source analysis and focused fixtures. Setup uses `--api-library` independently
of gameplay source. Removed project Contract/configuration modes remain rejected.

`tools/test-api-library.ps1 -EngineRoot <UE root>` runs the complete focused gate:
preparation, Editor/Game native builds and the required CMD Automation tests. It
restores the checkout's focused native fixture and rebuilds its binaries afterward.
The gate runs in the `unreal-automation` CI lane; the managed lane also tests that
C# edits invalidate managed compilation without invalidating binding generation.

`tools/prepare-api-library.ps1` exports Engine and FinareRuntime, compiles the
library before gameplay, then emits two C# revisions with unchanged generated
bindings/native output. `Finare.Unreal.ApiLibrary.NewCallReload` loads both in one
UE process and checks previously unused scalar, struct, container-field and
`ref/out` calls, including a static Kismet function called through the current
World's handle.
`Finare.Unreal.ApiDiscovery.GenericOutputs` covers descriptor rejection and
single-execution retries. `Finare.Unreal.ProjectSetup.SourceSave` exercises real
directory notifications and save debouncing.

`Finare.Unreal.InteropParity.Managed` executes generated C# bindings against UE
and ordinary C++ objects, including original-buffer mutation, struct/interface
collections, pointer adjustment, retained callbacks and generation cleanup.
`Finare.Unreal.InteropParity.MemoryIdentity` checks that the borrowed address
equals `TArray::GetData()`. Both are included in the API-library CI lane; see the
[runnable example](../tests/Finare.Tests/Fixtures/InteropParity/README.md).
Current acceptance is UE 5.8.2 Win64 Editor/CMD; these additions do not renew
cooked, other-platform or full Puerts-parity acceptance.

`Finare.Unreal.Components.GeneratedCreation` uses the same full-module export to
compile C# before invoking UE component creation, deferred registration,
attachment and destruction. The native test observes lifecycle and reload
results without constructing the tested components. Preparation and this test
are included in `tools/test-api-library.ps1` and its existing CI lane.

`Finare.Unreal.Construction.CharacterDefaults` covers the separate native
constructor path. `OnConstruct(UnrealConstructionContext)` calls the real UE
`CreateDefaultSubobject` and `SetupAttachment` operations; the same generated
wrappers supply native types and property access. Reflected partial script
properties point to the constructing receiver. No managed World, BeginPlay,
async scope or gameplay generation is started, and all constructor views expire
on return or failure. `async void OnConstruct` is rejected before entry.

The Character fixture verifies the first CDO, saved/edited camera defaults,
independent instances and gameplay reload. It currently requires game-thread
class loading and loaded concrete subobject classes. Any program revision
containing a constructor requires stopping and rebuilding the Script Class CDO;
active publication is rejected before mutation. Native constructor calls must
obey normal UE constructor restrictions. Async loading, live constructor
replacement and cooked delivery remain outside this tranche's acceptance.
See the [construction example](../tests/Finare.Tests/Fixtures/NativeConstruction/README.md).
