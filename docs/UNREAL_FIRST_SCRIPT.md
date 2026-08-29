# First UnifiedScript in an Unreal project

This walkthrough starts from an existing C++ UE 5.8 project and ends with the
same script running in editor play, opening in the native visual authoring
surface, hot reloading, and shipping in a Win64 package. It does not copy source
or generated files from HostProject or BlockBreaker.

## 1. Enable and install the plugin

Add this entry to the `.uproject` `Plugins` array before installation:

```json
{
  "Name": "UEUnifiedScript",
  "Enabled": true
}
```

Extract the SDK and install it:

```powershell
$sdk = 'D:\SDK\UnifiedScript-SDK-1.0.0-rc.1-win-x64'
$project = 'D:\Project\Game\Game.uproject'

powershell -ExecutionPolicy Bypass `
  -File "$sdk\Tools\install-unifiedscript-sdk.ps1" `
  -SdkRoot $sdk `
  -Project $project
```

The source plugin and framework-dependent managed host now belong to the game
project. Native binaries are intentionally built by that project's selected
engine.

## 2. Create the first script and integrate the project

Run the SDK scaffold command:

```powershell
powershell -ExecutionPolicy Bypass `
  -File "$sdk\Tools\new-unifiedscript-project.ps1" `
  -Project $project `
  -EngineRoot D:\UE_5.8 `
  -ScriptName FirstScript
```

The command discovers the project module and editor target, then renders
project-owned files from placeholder templates:

- `Managed/Game.UnifiedScript/FirstScript.cs` and its SDK-style project;
- `Config/UnifiedScript.onboarding.json`;
- `Contracts/Game.unreal-api-contract.json`;
- `Deployment/Game.unifiedscript-deployment.json`;
- generated Declaration IR, Graph, Authoring Surface, bindings, native thunks,
  API nodes, coverage, and `/Game/UnifiedScript/FirstScript/SA_FirstScript`.

It immediately executes normal onboarding, so success means the managed
project, reflection export, complete native contract, final editor target, and
Script Asset have already passed together. Existing scaffold files are never
overwritten unless `-Force` is explicit.

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
UnifiedScript repository path is consulted at runtime.

## Template inventory

The raw reusable templates live under `Templates/UnrealProject` in the SDK and
`templates/UnrealProject` in source. They cover onboarding, API Contract,
deployment/Script Asset identity, C# project/type, game/editor targets, module
rules, and the `.uproject` plugin fragment. The scaffold tool is the supported
way to render them; the files remain readable so teams can review and customize
their policy before automation.
