# Unreal project onboarding

`unifiedscript adopt <Game.uproject>` is the M13.1A first-use entry point. It
transactionally enables and installs the plugin/runtime and creates the first
managed workspace plus project-owned onboarding, Contract, and deployment
inputs. `--preview` makes no project changes, identical Apply is a no-op, and
`--rollback` restores exact retained backups only when doing so cannot overwrite
later user edits. `unifiedscript doctor <Game.uproject>` diagnoses the resulting
ownership, runtime, engine, generation, and cook-readiness state.

`tools/setup-unreal-project.ps1` is the build/onboarding stage used after
adoption. It turns the managed build, Unreal build, reflection export, binding
generation, Script Asset synchronization, determinism, and Automation steps
into one versioned operation driven by the adopted configuration. The same
script runs from a source checkout, an extracted [distributable SDK](UNREAL_SDK.md),
or the tools installed inside an adopted plugin. It reports `sdkLayout:
repository`, `sdkLayout: packaged`, or `sdkLayout: embedded`; embedded mode uses
the project-local CLI/runtime without copying the payload onto itself.

## Adopt and diagnose

From an extracted SDK:

```powershell
& "$sdk\Tools\unifiedscript.cmd" `
  adopt D:\Project\Game\Game.uproject --sdk-root $sdk --preview

& "$sdk\Tools\unifiedscript.cmd" `
  adopt D:\Project\Game\Game.uproject --sdk-root $sdk

& "$sdk\Tools\unifiedscript.cmd" `
  doctor D:\Project\Game\Game.uproject --engine-root D:\UE_5.8
```

The preview document uses
`unifiedscript.unreal-project-adoption-plan/v0.1`. Every file is classified as
`create`, `update`, `unchanged`, or `conflict` with current/desired SHA-256.
Apply accepts only conflict-free plans and rechecks every current hash before
the first commit. Its
`unifiedscript.unreal-project-adoption-transaction/v0.1` state retains backups,
the previous transaction link, and the exact product-owned paths required for
safe upgrades and rollback. Project policy and managed source become
user-editable after creation; future adoption never guesses through a changed
file.

For a Blueprint-only project, the same transaction also adds the smallest
project-owned native host required by a source plugin: `Source/<Game>/<Game>.cpp`,
its module rules, and matching Game/Editor targets. The `.uproject` Runtime
module entry and all four source files are rollback-owned product scaffolding;
the developer does not author C++ to reach the first script. Existing native
projects keep their own module and targets. Generated plugin thunk sources are
tracked separately as mutable product output, so a later no-change Repair keeps
the current generated table instead of replacing it with the SDK seed.

The doctor report uses `unifiedscript.unreal-project-doctor/v0.1`. Missing
first-generation coverage is a warning after adoption; a missing/disabled
plugin, runtime closure, managed project, invalid onboarding schema, corrupt
transaction state, or changed product-owned plugin/runtime is a failure.

Adoption also installs these project-local product tools below
`Plugins/UEUnifiedScript/Tools`:

- `unifiedscript.cmd` and `UnifiedScript.Cli` for Preview, Repair, and Doctor;
- `setup-unreal-project.ps1` for native/managed onboarding;
- `run-unifiedscript-onboarding-after-process.ps1` for a build that must wait
  until the current Editor releases the plugin DLL;
- the versioned project templates used by deterministic Repair.

After the plugin can load, **Tools > UnifiedScript Project Setup** exposes the
same operations in Slate. Preview, Repair, and Doctor stream their child-process
output without blocking the game thread. **Save, Exit & Build** saves dirty
packages, launches the deferred helper with the current Editor process ID, and
requests a normal Editor exit. The helper then invokes the project-local setup
script and writes its report under `Saved/UnifiedScript/ProjectSetup`.

## Apply

From the UnifiedScript repository, extracted SDK, or adopted project's plugin
tools directory:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/setup-unreal-project.ps1 `
  -Project D:\Project\Game\Game.uproject `
  -EngineRoot D:\UE_5.8
```

`-Config` defaults to `Config/UnifiedScript.onboarding.json` beside the selected
project. `-EngineRoot` may be omitted when `UNIFIEDSCRIPT_UE_ROOT` is set or the
project's `EngineAssociation` resolves through the registered Unreal builds.
`-Modules` and `-Classes` can override the checked-in reflection selection for
an explicit local run.

Apply performs these phases in order:

1. preflight every input, tool, target, plugin module, output parent, contract,
   deployment manifest, and managed project before product files are changed;
2. build the UnifiedScript CLI/runtime and stage the managed host into the
   selected project's plugin;
3. bootstrap native bindings from the last reflection artifact when present;
4. build the editor target, export current reflection, and generate the C#
   wrappers, native thunks, visual API catalog, and coverage report atomically;
5. build project managed assemblies and rebuild the editor target against the
   final native thunks;
6. build the configured project Game target and require its target receipt, so
   ordinary BuildCookRun cannot silently fall back to a generic game that omits
   the source plugin;
7. synchronize the deployment manifest into deterministic Script Assets;
8. regenerate all four contract outputs in `Saved` and compare SHA-256 hashes;
9. optionally run the configured UE Automation filter and require its minimum
   successful test count.

The initial native build intentionally uses the last known reflection artifact.
This lets a project compile an append-compatible thunk-table change before the
new exporter can be loaded. The final native build always consumes reflection
from the current editor binary.

## Validate only

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/setup-unreal-project.ps1 `
  -Project D:\Project\Game\Game.uproject `
  -ValidateOnly
```

`-ValidateOnly` requires one successful Apply baseline. It does not overwrite
reflection, bindings, native thunks, node catalogs, coverage, or Script Assets.
That baseline must match the current managed assemblies and compiled chunks. If
a shared UnifiedScript assembly changed after the last Apply run, run ordinary
Apply onboarding once to rebuild, recompile, and deploy the chunks before using
`-ValidateOnly` again; otherwise the runtime correctly rejects the stale chunk
dependency content identity.
When it runs from this repository, it first builds the current Release CLI for
validation only, so deterministic output checks cannot accidentally use a stale
tool binary. The rebuilt CLI is not staged into the Unreal plugin.
It validates the deployment manifest through the editor commandlet, regenerates
contract outputs under `Saved` for byte comparison, verifies configured native
coverage, and runs the optional managed-startup Automation gate. Logs and the
machine-readable report are diagnostic artifacts under
`Saved/UnifiedScript/Onboarding` rather than project source.

## Configuration contract

The strict `unifiedscript.unreal-project-onboarding/v0.1` document contains no
machine-specific absolute paths. Every file path is resolved relative to the
configuration file.

```json
{
  "schemaVersion": "unifiedscript.unreal-project-onboarding/v0.1",
  "editorTarget": "GameEditor",
  "gameTarget": "Game",
  "platform": "Win64",
  "configuration": "Development",
  "pluginPath": "../Plugins/UEUnifiedScript",
  "reflection": {
    "modules": ["Game", "Engine", "UEUnifiedScriptRuntime"],
    "classes": ["/Script/Game.Player", "/Script/Engine.Actor"],
    "output": "../Generated/Game.reflection.json"
  },
  "bindings": {
    "contract": "../Contracts/Game.unreal-api-contract.json",
    "managedOutput": "../Managed/Generated/Game.bindings.generated.cs",
    "nativeOutput": "../Source/Game/Generated/Game.native-thunks.generated.cpp",
    "nodeCatalogOutput": "../Generated/Game.api-nodes.json",
    "coverageOutput": "../Generated/Game.binding-coverage.json",
    "failOnRejected": true,
    "requireCompleteNativeCoverage": true
  },
  "deployment": {
    "manifest": "../Deployment/Game.unifiedscript-deployment.json",
    "enabled": true
  },
  "managed": {
    "configuration": "Release",
    "stageDirectory": "../Plugins/UEUnifiedScript/Binaries/Managed",
    "projects": ["../Managed/Game.UnifiedScript.csproj"],
    "chunks": [
      {
        "project": "../Managed/Game.UnifiedScript.csproj",
        "stagePath": "../Plugins/UEUnifiedScript/Binaries/Managed/Game.UnifiedScript.uschunk"
      }
    ]
  },
  "validation": {
    "automationFilter": "Game.UnifiedScript",
    "minimumAutomationTests": 1
  }
}
```

`reflection.modules` controls what Unreal may inspect. `reflection.classes`
narrows broad modules such as Engine to an exact allowlist. The separate API
Contract remains the shipped member allowlist; the onboarding command consumes
it to produce C++, C#, coverage, and visual nodes from the same selection.

`requireCompleteNativeCoverage` requires every selected member to have a native
thunk and rejects reflection fallback, rejected members, and deferred types.
Projects may set it to false only when their reviewed product policy permits a
safe reflected fallback.

`editorTarget` loads reflection/export and editor Automation; `gameTarget`
closes the native runtime and cook dependency graph. Adoption renders both from
the project name for a Blueprint-only consumer. A legacy native edge case may
still use `UnrealGame`, but only with a source plugin descriptor that keeps
`Installed=false` so UAT is allowed to synthesize its temporary target.

The optional `managed.chunks` list compiles an already-built managed project
into a deterministic `.uschunk`, verifies the container, and atomically replaces
the declared file below `managed.stageDirectory` before UE Automation starts.
This keeps the chunk dependency manifest synchronized with any shared managed
assemblies rebuilt during onboarding.

## Reports and failures

Every run writes `unifiedscript.unreal-project-onboarding-report/v0.1`. It
records mode, engine/project/config resolution, ordered phase durations,
outputs, and one stable error code. The main families are:

- `USONB10xx`: configuration schema or unknown property;
- `USONB11xx`: engine, SDK compatibility, plugin, target, input, or destination preflight;
- `USONB12xx`: missing Apply baseline required by ValidateOnly;
- `USONB20xx`: generation or deterministic-output mismatch;
- `USONB21xx`/`USONB23xx`: managed or native build failure;
- `USONB22xx`: native bootstrap or reflection export failure;
- `USONB24xx`: Script Asset deployment failure;
- `USONB25xx`: binding coverage failure;
- `USONB26xx`: managed-startup Automation failure.

The report and the failed phase's log are the retry boundary; no manual ordering
knowledge should be needed.

## Checked consumers

`HostProject/Config/UnifiedScript.onboarding.json` is the minimal runtime and
Automation fixture. The BlockBreaker UnifiedScript project carries the same
schema for its nine-type, 40-member gameplay contract. Both configurations pass
Apply and a following deterministic validation against UE 5.8.1.

The M13.1A-2 clean Blueprint-only acceptance additionally proves packaged-SDK
Adopt, project-local Apply/Validate, Editor and Game builds, Script Asset sync,
Automation, BuildCookRun, no-change Repair, Doctor, and exact adoption rollback.
The archived Win64 product contains the managed gameplay assembly, compiled
chunk, UnifiedScript runtime assemblies, UE-bundled CoreCLR host, and cooked
Script Asset with no Puerts or project-authored JavaScript payload. Adoption
rollback restores its owned source/configuration surface; normal Unreal-derived
`Binaries`, `Intermediate`, and `Saved` outputs remain ordinary build artifacts.
