# Unreal project onboarding

`tools/setup-unreal-project.ps1` is the M6.3 project-facing entry point. It
turns the previously separate managed build, Unreal build, reflection export,
binding generation, Script Asset synchronization, determinism, and Automation
steps into one versioned operation driven by a project-owned configuration.
The same script runs from either a source checkout or an extracted
[distributable SDK](UNREAL_SDK.md); it reports `sdkLayout: repository` or
`sdkLayout: packaged` and uses the SDK's verified managed payload in packaged
mode.

## Apply

From the UnifiedScript SDK/repository root:

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
6. synchronize the deployment manifest into deterministic Script Assets;
7. regenerate all four contract outputs in `Saved` and compare SHA-256 hashes;
8. optionally run the configured UE Automation filter and require its minimum
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
    "projects": ["../Managed/Game.UnifiedScript.csproj"]
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
