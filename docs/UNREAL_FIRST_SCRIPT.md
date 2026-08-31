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
4. **Save, Exit & Build** saves dirty packages, closes the Editor, and starts
   onboarding only after the loaded native plugin DLL has been released.

The last action deliberately exits the Editor. Replacing or rebuilding a loaded
native plugin in place is unsafe on Windows; the deferred helper waits for the
exact Editor process and then runs the installed setup script.

The adopted project owns:

- `Source/Game/Game.cpp`, its module rules, and Game/Editor targets when the
  original project was Blueprint-only;
- `Managed/Game.UnifiedScript/FirstScript.cs` and its SDK-style project;
- `Config/UnifiedScript.onboarding.json`;
- `Contracts/Game.unreal-api-contract.json`;
- `Deployment/Game.unifiedscript-deployment.json`;
- generated Declaration IR, Graph, Authoring Surface, bindings, native thunks,
  API nodes, API/diagnostic browser, coverage, and
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

The generated `FirstScript` exposes `StartValue` and a Visual
`AddClamped(value, delta)` function. This deliberately contains no fixture
gameplay: it is the smallest lifecycle and C#/Visual round-trip starting point.

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
components matched to that Script Asset without restarting the process.

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
acceptance runs this BuildCookRun path and checks the executable, managed DLLs,
compiled chunk, CoreCLR host, cooked Script Asset, and absence of Puerts or
project-authored JavaScript payloads.

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
