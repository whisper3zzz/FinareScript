# Async UMG gameplay example

The checked-in [AsyncUiDemoScript](../samples/Unreal/Finare.Unreal.Demo/AsyncUiDemoScript.cs)
combines generated UMG bindings, zero-parameter button delegates, async class and
mesh loading, and World-owned Actor creation. It has no gameplay-specific native
facade. The HostProject's `FinareAsyncDemoWidget` supplies layout only;
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

`RequestSpawn`, `CancelSpawn` and `PreviewHeight` are Visual methods. The height
function supplies the spawned preview's Z coordinate and is a simple C#/Graph
editing example. The async helper bodies are
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
| `Author` | Start project-backed authoring and open the saved scene plus its native C#/Graph tab; stop the owned server when the editor exits |
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

### C#/Graph iteration

```powershell
.\tools\launch-unreal-async-demo.ps1 -EngineRoot D:\UE_5.8 -Mode Author
```

The launcher starts a project-backed session at `http://127.0.0.1:4173` and opens
the native tab for `/Game/UnifiedScriptDemo/BP_AsyncUiDemo.AsyncUiScript`. Keep
the launcher running until the editor exits. Its logs are in the printed run
directory; an occupied port fails startup instead of taking over another server.
The equivalent console entry is `Finare.OpenAuthoring <ScriptAssetObjectPath>`.

Select `PreviewHeight` to edit its return literal in Graph, or use the tab's C#
view to edit the same source. Both use the normal preview/commit protocol and
publish matching source, identities, declarations, DLL/PDB and runtime chunk.
The whole Demo project participates in compilation; its other source files are
read-only in this session. Only the supported Visual profile is graph-editable.
See [project session boundaries](AUTHORING_TRANSACTIONS.md#project-backed-sessions-and-runtime-chunks).

The native Apply & Reload entry still routes through the daily-build coordinator.
External IDE saves can now be imported using **Refresh C#** without restarting
the session. The refresh reevaluates the project and preserves external edits
if compilation or publication fails. Native Build & Reload refreshes the open
authoring tab after a successful build and live component swap; unsaved C# buffers
are not overwritten. New dependency/native-host outputs still follow the existing
build/restart requirements. For a full rebuild, close the game/editor and rerun
the launcher without `-SkipBuild`.

This does not establish installed-consumer authoring, live class-shape/default
deployment, physical editor interaction or a new cooked-source-edit result.

### Source/Graph to live runtime verification

```powershell
.\tools\test-unreal-async-authoring.ps1 -EngineRoot D:\UE_5.8
# Use an external C# save followed by refresh for the first edit:
.\tools\test-unreal-async-authoring.ps1 -EngineRoot D:\UE_5.8 -SkipBuild -ExternalSourceEdit
# Exercise the native coordinator, a live PIE Actor and automatic Slate refresh:
.\tools\test-unreal-async-authoring.ps1 -EngineRoot D:\UE_5.8 -NativeDailyBuild
```

With the sample editor/game and other writers closed, this runs the actual Demo
project and a separate CMD/NullRHI game. It starts with height 100, applies a C#
edit to 120, applies a stable-ID Graph literal edit to 180, then signals the
same running game to reload the published chunk. The native verifier checks the
new Actor's height, HUD, cancellation, subscriptions and teardown. Transactional
Undo restores the two test edits and their matching chunk; concurrent external
edits are never force-restored. `-SkipBuild` requires current prepared outputs.

`-NativeDailyBuild` instead changes the source from 100 to 120 outside the
Workbench, then asks the real native daily-build coordinator to rebuild and
reload a live PIE Script Class. It checks the changed return value, component
generation, automatic Graph-tab refresh, stale-tab recovery and unsaved-buffer
protection. Its initial preparation uses ordinary onboarding so attached builds
have a compatible receipt; `-SkipBuild` reuses that preparation when current.
It does not simulate a physical toolbar click or test class-shape deployment.

The external-save/Graph path passes in
`artifacts/ue/async-authoring/20260904-184826-724/runtime.log`; the native
Build & Reload path passes in
`artifacts/ue/async-authoring/20260904-190743-946/runtime.log`. Both restore their
test edits through Undo. The latter exposed a real loaded-DLL overwrite in the
shared-host staging target; editor-attached builds now reuse that validated host.
The Demo target also seeds `.usgraph` only when missing, preserving authored
layout on later builds. Refresh failure and project-context changes are covered
by the 31 managed tests in
`artifacts/m13-5-ui/authoring-tests/project-refresh-final.trx`.

The 2026-09-04 run passed at
`artifacts/ue/async-authoring/20260904-182118-640/runtime.log`. This proves a real
behavior change in the same World process, not just loading the same chunk twice.
Managed project/transaction/semantic/debug tests are in
`artifacts/m13-5-ui/authoring-tests/project-authoring.trx` (88 passed).
The Editor build and four CMD tests also pass in
`artifacts/m13-5-ui/authoring-tests/native-authoring.log`: the three UI tests and
`Finare.Unreal.Editor.AuthoringSurface`. Supplying
`-FinareProjectSurface=<staged Demo .surface.json>` to that run adds
full-project native schema, selected-class focus and height-Graph checks.
This authored-source check is not a new cooked-build or visual-appearance result.

### Focused Automation

From the repository, build `FinareHostEditor` and run
`tools/emit-unreal-demo-chunk.ps1 -EngineRoot D:\UE_5.8 -Configuration Release`.
The CMD Automation filter `Finare.Unreal.UI` runs:

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
