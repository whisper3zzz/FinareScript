# Async UMG gameplay example

The checked-in [AsyncUiDemoScript](../samples/Unreal/UnifiedScript.Unreal.Demo/AsyncUiDemoScript.cs)
combines generated UMG bindings, zero-parameter button delegates, async class and
mesh loading, and World-owned Actor creation. It has no gameplay-specific native
facade. The HostProject's `UnifiedScriptAsyncDemoWidget` supplies layout only;
an ordinary Widget Blueprint with the same named controls can replace it.

## Behavior

- Startup asynchronously loads `WidgetClassPath`, creates the HUD and subscribes
  to `SpawnButton` and `CancelButton` through generated `Button` wrappers.
- **Load & spawn cube** loads `StaticMeshActor` and the configured `MeshPath`,
  spawns an Actor, sets its mobility and assigns its mesh. `StatusText` displays
  readiness, loading, cancellation, success or an actionable failure.
- While loading, repeated clicks are ignored and the load button is disabled.
  Cancel stops the managed request. An already queued native completion is
  consumed by the next Tick without restarting the canceled gameplay operation.
- A failed load or invalid mesh preserves the previous successful preview. A
  successful retry replaces it. Successful resource loads are cached within the
  current generation; the native async service owns resource retention.
- Reload and Actor deletion cancel pending work, unsubscribe both buttons,
  remove the view and destroy the owned preview Actor. Retired buttons cannot
  invoke the new generation. The viewport owns the visible widget; a managed
  wrapper alone is not a GC root.

`RequestSpawn` and `CancelSpawn` are Visual methods. The async helper bodies are
ordinary source-only C#, represented as opaque calls, not editable async graphs.
The two `[Expose]` paths are configuration, not cross-generation native handles.
Reload starts a fresh view/preview rather than migrating a pending Task.

## Binding surface

[UMG.api-contract.json](../samples/Unreal/UMG.api-contract.json) selects standard
engine members: Widget creation, viewport attachment/removal, enabled state,
button events, text, StaticMeshActor mobility and mesh assignment. Its outputs
are the C# wrappers, C++ thunks, node catalog, API browser and coverage report
alongside that file. The native table is registered by the HostProject module.

`UserWidget.GetWidgetFromName` is public C++ but not a UFUNCTION. Its explicit
native-extension declaration goes through the same generator; it is not a
handwritten dispatch implementation. `StaticMeshActor.StaticMeshComponent` is
a private reflected property and deliberately uses the reflected read route.

For an adopted project, retain its scaffolded Script Class IDs/paths and use its
own generated namespace. Add the selected UMG/Engine members and the native-only
lookup declaration to its existing Contract, then use the regular Build & Reload
workflow. The native module needs `UMG`. Use a Widget Blueprint containing
`SpawnButton` and `CancelButton` (`Button`) and `StatusText` (`TextBlock`), and set
`WidgetClassPath` to that Blueprint's generated class path. No Blueprint event
forwarding Actor is needed. Mouse/input-mode policy remains with the game.

## Current runnable verification

### Launch the saved Demo

From the repository root, with the HostProject editor/game closed:

```powershell
.\tools\launch-unreal-async-demo.ps1 -EngineRoot D:\UE_5.8
```

The launcher builds the Editor host, emits the Release managed chunk and its
matching declaration, creates the Script Class and scene if absent, then opens
the scene as a standalone game. Click **Load & spawn cube** to create a visible
preview; the mouse cursor and fixed camera are supplied by a presentation-only
GameMode/PlayerController. No engine association or project input/map ini edit is
needed. `-SkipBuild` reuses already current native and managed outputs.

| Mode | Result |
| --- | --- |
| `Play` (default) | Open the saved scene with `-game` |
| `Editor` | Open the saved scene in Unreal Editor for scene/default editing |
| `Prepare` | Create missing assets and synchronize the generated class, without launching a game |
| `Verify` | Exercise the saved scene in a separate CMD/NullRHI game, including cancel, mesh spawn, chunk reload and teardown |
| `Package` | Build/cook/archive Win64 Development, exercise the packaged game, and add `StartAsyncDemo.cmd` beside its executable |

The prepared assets are `/Game/UnifiedScriptDemo/BP_AsyncUiDemo` and
`/Game/UnifiedScriptDemo/L_AsyncUiDemo`. They are locally generated and ignored by
Git, not checked-in binary fixtures. Preparation rejects conflicting class/map
identities and never rewrites an existing scene. Generated class synchronization
uses the standard materializer, preserving intentional default overrides. A
repeated preparation of unchanged inputs leaves both saved files unchanged.

Package output and logs are printed under `artifacts/ue/async-demo/<run>/`.
Distribute the entire `package/Windows` directory, not the executable alone;
launch `StartAsyncDemo.cmd` to select the Demo map without changing project-wide
defaults. Cube and Sphere are explicit native cook dependencies; custom mesh
paths must also be included in the project's cook inputs.

For source iteration, edit `AsyncUiDemoScript.cs`, close the game/editor and
rerun the launcher without `-SkipBuild`. `Editor` mode is a scene-editing entry,
not yet an integrated C#/Graph authoring session: the sample's native
Build & Reload/deployment discovery and transactional Graph surface still need
to be connected and verified. The runtime verifier reloads the deployed chunk;
it does not claim a source-change or Graph-apply test.

### Focused Automation

From the repository, build `UEUnifiedScriptHostEditor` and run
`tools/emit-unreal-demo-chunk.ps1 -EngineRoot D:\UE_5.8 -Configuration Release`.
The CMD Automation filter `UnifiedScript.Unreal.UI` runs:

- `AsyncGameplay`: materializes the actual emitted Script Class, saves and
  reloads it, then runs a real UMG viewport in a CMD/NullRHI Play World. It
  broadcasts the actual UButton delegates and checks cancellation, duplicate
  clicks, correct mesh assignment, failure/retry, reload during loading, GC and
  deletion cleanup.
- `EmptyDelegateParameters`: zero-parameter events accept Unreal's null parameter
  block, while non-empty signatures still reject missing memory.
- `GeneratedByteEnum`: out-of-range byte-wrapped enums cannot mutate native state.

When the persistent Demo class exists, the gameplay test validates its exact
identity/declaration and reuses it read-only. Test-specific paths are set on its
temporary instance before BeginPlay, not on saved defaults. Without an existing
class, it creates, saves/reloads and removes only its own test asset. The test
never modifies the persistent Demo scene. NullRHI tests do not establish visual
appearance or physical mouse interaction.

The example does not establish complete UMG/latent/delegate coverage, remote
multiplayer UI, arbitrary .NET worker-thread UObject access, or a Puerts
performance improvement.
