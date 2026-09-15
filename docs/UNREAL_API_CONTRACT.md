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
preserves excluded native state. Nested containers and struct fields containing
collections of custom structs are excluded from this layout. This is value
marshalling, not a shared-memory struct view.

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

`Finare.Unreal.Components.GeneratedCreation` uses the same full-module export to
compile C# before invoking UE component creation, deferred registration,
attachment and destruction. The native test observes lifecycle and reload
results without constructing the tested components. Preparation and this test
are included in `tools/test-api-library.ps1` and its existing CI lane.
