# First UnifiedScript in an Unreal project

This walkthrough starts from an existing C++ or Blueprint-only UE 5.8 project
and ends with the same script running in editor play, opening in the native
visual authoring surface, hot reloading, and shipping in a Win64 package. It
does not copy source or generated files from HostProject or BlockBreaker, and it
does not require hand-editing `.uproject`, Contract, deployment, or onboarding
JSON.

## 1. Adopt the existing project

Extract the SDK and run the packaged CLI. `adopt` is Apply by default:

```powershell
$sdk = 'D:\SDK\UnifiedScript-SDK-1.0.0-rc.1-win-x64'
$project = 'D:\Project\Game\Game.uproject'

& "$sdk\Tools\unifiedscript.cmd" `
  adopt $project --sdk-root $sdk
```

Use `--preview --report <path>` to inspect every create/update/unchanged/conflict
decision without changing the project. Apply then performs one file transaction:

- merges the enabled `UEUnifiedScript` entry while preserving other `.uproject`
  fields;
- installs the SDK source plugin, managed runtime closure, project-local CLI,
  templates, setup script, and deferred onboarding helper;
- creates the managed project and `FirstScript.cs` from the selected template;
- creates the project-owned onboarding, Contract, and deployment inputs;
- for a Blueprint-only project, creates a minimal Runtime module and matching
  Game/Editor targets and merges the module into the `.uproject`;
- records exact before/after hashes and backups under
  `Saved/UnifiedScript/Adoption`.

Those native host files are generated product scaffolding; you do not need to
write or maintain C++ to run the first script. Existing native projects keep
their own module and targets.

A second identical invocation reports `unchanged` and creates no product-file
diff. Existing unowned or post-adoption edited files are conflicts and are never
silently overwritten. To restore the latest applied transaction, first preserve
any changed files and run:

```powershell
& "$sdk\Tools\unifiedscript.cmd" `
  adopt $project --rollback
```

Rollback restores only the adoption-owned project surface. Derived Unreal
outputs already produced under `Binaries`, `Intermediate`, or `Saved` are normal
build artifacts and may remain after rollback.

Run `doctor` at any point to diagnose the project/plugin/runtime/configuration,
engine resolution, adopted payload drift, and whether the first generated
binding coverage baseline exists. After adoption, the project-local entry point
is preferred because it proves the installed project is self-contained:

```powershell
$projectTools = 'D:\Project\Game\Plugins\UEUnifiedScript\Tools'
& "$projectTools\unifiedscript.cmd" `
  doctor $project --engine-root D:\UE_5.8
```

## 2. Build bindings and the first Script Asset

Adoption intentionally finishes before native compilation so it remains
previewable and rollback-safe. If the source plugin has not been compiled and
the project cannot open in the Editor yet, run its installed onboarding action
once to compile the plugin, export reflection, generate bindings, compile the
managed project, build the project Game target, and materialize the Script
Asset:

```powershell
powershell -ExecutionPolicy Bypass `
  -File "$projectTools\setup-unreal-project.ps1" `
  -Project $project `
  -EngineRoot D:\UE_5.8
```

Once the plugin loads, open **Tools > UnifiedScript Project Setup**. The panel
uses the same project-local CLI and configuration model:

1. **Preview** shows the transactional adoption plan without writes;
2. **Repair** applies only owned, conflict-free project changes;
3. **Run Doctor** checks runtime, generation, engine, ownership, and cook state;
4. **Build, Restart & Continue** saves dirty packages, closes the Editor, and
   starts onboarding only after the loaded native plugin DLL has been released;
   the helper then reopens the project.

The last action deliberately exits the Editor. Replacing or rebuilding a loaded
native plugin in place is unsafe on Windows; the deferred helper waits for the
exact Editor process and then runs the installed setup script.

For the first attachable behavior, choose **Create UnifiedScript Class** from
the Tools menu or Content Browser and select Actor or ActorComponent. Creation
writes the C# lifecycle source, exact Script Asset sidecar, Blueprint proxy, and
deployment entry, then opens Project Setup. Run **Build, Restart & Continue**
once. When the project reopens, choose **Locate Generated Class**, drag the Actor
into a level or add the Component to an Actor Blueprint, and press Play. The
receipt is marked `ready` only after the full project-local dependency graph
succeeds; a failed build returns to the same panel with the report path and exit
code.

The adopted project owns:

- `Source/Game/Game.cpp`, its module rules, and Game/Editor targets when the
  original project was Blueprint-only;
- `Managed/Game.UnifiedScript/FirstScript.cs` and its SDK-style project;
- `Config/UnifiedScript.onboarding.json`;
- `Contracts/Game.unreal-api-contract.json`;
- `Deployment/Game.unifiedscript-deployment.json`;
- generated Declaration IR, Graph, Authoring Surface, bindings, native thunks,
  source API requests, development Contract/report, API nodes, API/diagnostic
  browser, coverage, and
  `/Game/UnifiedScript/FirstScript/SA_FirstScript`.

Onboarding success means the managed project, reflection export, complete native
Contract, final Editor target, Game target receipt, and Script Asset have passed
together. This closes the same native dependency graph that normal packaging
uses; generated thunk source is preserved by later no-change Repair. The older
`install-unifiedscript-sdk.ps1` and `new-unifiedscript-project.ps1` entry points
remain available for compatibility and advanced diagnosis; new projects should
use `adopt` so installation and scaffolding share one ownership transaction.

## 3. Editor play

Open the project in UE 5.8, then:

1. open or create a gameplay map;
2. place a `UnifiedScriptActor`;
3. assign `SA_FirstScript` to its `Script Asset` property;
4. start PIE and select the actor/component;
5. confirm `bScriptRunning` is true and `LastScriptError` is empty.

The generated `FirstScript` exposes `StartValue`, a Visual
`AddClamped(value, delta)` function, and ordinary source calls to
`Actor.GetActorLocation` and `Pawn.GetController`. The source uses a normal
`using Game.UnifiedScript.Generated;` import; an alias or fully qualified name is
no longer required. During the first Setup run, a deterministic source type-hint
artifact requests `Pawn` even though it is absent from the seed reflection
selection. UE resolves it uniquely, exports that one additional Engine class,
then turns both calls into source dependency requests and a safe development
Contract overlay before the managed project needs to compile. The same effective
members then appear in the generated native thunk, C# wrapper, visual node
catalog, API browser, and coverage report; the reviewed seed Contract is not
hand-edited.
This still contains no fixture gameplay: it is the smallest lifecycle,
source-to-Unreal API, and C#/Visual round-trip starting point.

## 4. Visual open and hot reload

Start the project-specific transactional authoring host in a second terminal:

```powershell
powershell -ExecutionPolicy Bypass `
  -File "$sdk\Tools\start-unifiedscript-authoring.ps1" `
  -Project $project `
  -EngineRoot D:\UE_5.8
```

Keep that terminal open. In the editor, select `SA_FirstScript` in the Content
Browser, choose **Tools > UnifiedScript Authoring**, and click
**Selected Script Asset**. `AddClamped` opens as the same typed graph represented
by its restricted C# source. Edit a supported node/default/connection and use
**Apply & Reload**. The server commits the semantic patch, emits the next
managed generation plus Declaration/Surface artifacts, and the editor reloads
components matched to that Script Asset without restarting the process. Before
the first component changes generation, every matching running and
stopped/editor instance passes the same non-mutating configuration, ownership,
artifact, declaration, and stable-ID state preflight. If one instance requires
a restart, the entire batch remains on its previous generation/state and the
reload receipt identifies `batchPreflight` plus the required action. After that
barrier, every running candidate loads, starts, and rebinds while its stopped
previous generation remains recoverable. The editor publishes pointers only
after all candidates prepare; a late candidate failure aborts the full batch,
restarts retained generations, and restores native state and overrides. The
receipt records `batchPrepare`, `batchCommit`, `batchAbort`, and
`batchFinalize` for this two-phase swap.

For ordinary IDE source edits, use **Tools > UnifiedScript Project Setup >
Build & Reload**. It evaluates the persistent Build/Play graph without closing
the Editor, rebuilds only invalidated managed/chunk outputs, and then routes all
loaded Script Class assets through the same preflight/prepare/commit/finalize
coordinator. Editor-owned deployment and startup checks are deferred against
their last successful state. If a native thunk, reflection surface, deployment
schema, or baseline must change, the receipt names the blocking stage and asks
for **Build, Restart & Continue**; no component is partially reloaded.

You do not need to visit Project Setup before every run. A successful Graph
**Apply & Reload** queues this same daily action, and the normal Play button
uses it as a gate. The first Play request waits outside PIE; compatible managed
changes build and reload before the exact request resumes automatically. If the
change requires native/reflection/schema work, Play stays stopped and the same
diagnostic receipt points to **Build, Restart & Continue**. Repeated Graph,
manual, or Play requests while a build is active are coalesced rather than
starting competing generators.

## 5. Packaged play

Save the map containing the actor and package with the ordinary Unreal
Automation Tool. Replace the map path with the saved project map:

```powershell
& D:\UE_5.8\Engine\Build\BatchFiles\RunUAT.bat BuildCookRun `
  "-project=$project" `
  -noP4 -platform=Win64 -clientconfig=Development `
  -build -cook -stage -pak -archive `
  '-map=/Game/Maps/Main' `
  '-AdditionalCookerOptions=-SkipZenStore' `
  '-archivedirectory=D:\Builds\Game-UnifiedScript' `
  -unattended -utf8output -NoCodeSign -NoXGE
```

The plugin stages all files under `Binaries/Managed`, the matching UE-bundled
CoreCLR runtime, the generated Script Asset, and the project native thunk table.
Launch the archived executable and inspect the same actor behavior. No SDK or
UnifiedScript repository path is consulted at runtime. The clean Blueprint-only
acceptance also invokes the command-line-only smoke hook carried by its generated
native host. It passes the exact generated Actor and ActorComponent class paths,
loads both classes from the packaged asset store, and requires both to complete
`BeginPlay`, `Tick`, and `EndPlay` from the compiled chunk before the process can
exit successfully:

```powershell
& $packagedExe `
  -UnifiedScriptScriptClassSmoke `
  '-UnifiedScriptActorClass=/Game/MyScripts/MyActor.MyActor_C' `
  '-UnifiedScriptComponentClass=/Game/MyScripts/MyComponent.MyComponent_C' `
  "-UnifiedScriptScriptClassSmokeResult=$resultPath" `
  -unattended -nullrhi -nosplash
```

The smoke additionally requires two live managed instances and lifecycle claims,
compiled-chunk execution, the plugin-local CoreCLR runtime, and zero retained
instances, claims, or Worlds after teardown. The normal player launch does not
activate this hook. Existing native projects can use their own equivalent game
smoke; Blueprint-only projects receive the generic hook with the generated host.

## Template inventory

The versioned `unifiedscript.project-template-catalog/v0.1` declares `minimal`,
`visual-gameplay`, and `data-plane` starting points. The raw reusable templates
live under `Templates/UnrealProject` in the SDK and `templates/UnrealProject` in
source. They cover onboarding, API Contract, deployment/Script Asset identity,
C# project/type, game/editor targets, module rules, and the `.uproject` plugin
fragment. The scaffold tool is the supported way to render them; the files
remain readable so teams can review and customize their policy before
automation. See
[Project templates and generated API browser](PROJECT_TEMPLATES_AND_API_BROWSER.md)
for exact capabilities and non-claims.
