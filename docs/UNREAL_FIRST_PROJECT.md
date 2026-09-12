# First project golden path

This is the shortest supported path from an extracted Finare SDK to a
running C# Script Class in an existing Unreal project. The default `minimal`
template uses ordinary C#; Graph editing is optional. It uses only packaged or
project-local entry points: no repository checkout, direct generator command,
or hand-edited `.uproject`, deployment, or onboarding JSON is needed.

For the detailed ownership and recovery model, continue with
[First Finare in an Unreal project](UNREAL_FIRST_SCRIPT.md). After the
first Play succeeds, use the [gameplay task recipes](UNREAL_GAMEPLAY_RECIPES.md).

## Before you start

Use Unreal Engine 5.8.1 on Win64 and an extracted Finare SDK. In
PowerShell, set three paths for this session:

```powershell
$sdk = 'D:\SDK\Finare-1.0.0-rc.1'
$project = 'D:\Games\MyGame\MyGame.uproject'
$engine = 'D:\UE_5.8'
```

The SDK can locate this page without knowing its installation layout:

```powershell
& "$sdk\Tools\finare.cmd" guide first-project --sdk-root $sdk
```

## 1. Preview, adopt, and diagnose

Preview shows the complete owned file transaction without changing the game:

```powershell
& "$sdk\Tools\finare.cmd" adopt $project --sdk-root $sdk --preview
```

Resolve every reported `conflict`; Apply never guesses through an unowned or
post-adoption edited file. Then run the same command without `--preview` and
diagnose the installed surface:

```powershell
& "$sdk\Tools\finare.cmd" adopt $project --sdk-root $sdk

$projectRoot = Split-Path -Parent $project
$projectTools = Join-Path $projectRoot 'Plugins\Finare\Tools'
& "$projectTools\finare.cmd" doctor $project --engine-root $engine
```

Success means adoption reports `applied` and Doctor has no error-severity
check. A warning about first-generation binding coverage is expected until the
next step. Repeating Apply must report `unchanged` and produce no project-file
diff.

## 2. Build the first Script Class

Run the one project-local setup entry. It discovers UE APIs referenced by your
C# source and owns reflection, binding selection,
managed/native bindings, Script Asset synchronization, and the initial build:

```powershell
powershell -ExecutionPolicy Bypass `
  -File "$projectTools\setup-unreal-project.ps1" `
  -Project $project `
  -EngineRoot $engine
```

Do not run the internal generators separately. A successful receipt is written
under `Saved/Finare/ProjectSetup`, and Doctor should now report the
generated surface and cook baseline as ready.

You do not need to maintain a list of allowed functions. The default build reads
C# source and UE reflection directly; it creates no Contract, request list,
selection overlay, or binding lock. For example,
its references to `Actor.GetActorLocation()` and `Pawn.GetController()` drive
binding generation. The generated wrappers provide C# type checking and IDE
completion for that generated surface. This is not yet a pre-generated IDE
catalog of every UE API; unsupported signatures still produce diagnostics.
Removing a call removes its unused binding on the next build. Failed selection
leaves the previous generated outputs intact.

## 3. Place it and Play

Open the project and perform these Editor actions:

1. Open `/Game/UnifiedScript/FirstScript/SA_FirstScript`.
2. Place a `FinareActor` in a map, or add a `FinareComponent` to
   an existing Actor Blueprint.
3. Assign `SA_FirstScript` to the host's **Script Asset** property.
4. Save the map and press Play.
5. Inspect the host: `bScriptRunning` is true and `LastScriptError` is empty.

The default script increments its exposed `StartValue` in `OnBeginPlay`. That state
change, a running host, and an empty error field are the first-Play acceptance
receipt; opening a source file or seeing generated output alone is not.

## 4. Edit C# and reload

Open the generated solution or VS Code workspace in your IDE. In
`Managed/<assembly>/FirstScript.cs`, change
`AddClamped(StartValue, 1)` to `AddClamped(StartValue, 2)` and save.
Use **Build & Reload** in the Editor's Finare project setup panel.
The running instance now adds two to its preserved state. On a fresh Play it
starts from the configured initial state.

The helper uses `Math.Clamp` and has no `[Visual]` attribute. Ordinary C# bodies
go through Roslyn and do not need to fit the Graph language profile. The Graph
authoring service and semantic Diff acceptance are not required for this loop.
Use the Editor action to publish changes; an IDE compile alone does not reload
the running instances.

A compatible body-only change keeps the Editor open and publishes every live
instance together. If the receipt says `restartRequired`, use **Build, Restart
& Continue**; Play remains blocked until the required native/reflection/schema
stage completes. Do not work around that receipt with a partial generator run.

To opt into Graph editing, adopt with `--template visual-gameplay` or mark
eligible methods with `[Visual]`, then follow the
[authoring workflow](UNREAL_FIRST_SCRIPT.md). Its Graph profile restrictions
apply to those visual methods.

## 5. Know the recovery buttons

- Adoption conflict: rerun Preview and inspect its exact path and hash.
- Broken installed surface: run `doctor`, then adoption Apply for a no-change
  repair or `adopt $project --rollback` to restore the latest owned transaction.
- C# build failure: read the Build & Reload diagnostics, fix the source in the
  IDE, and retry. A failed build does not publish a new runtime generation.
- Optional Graph rejection: open **Diagnostics**. A stale Diff requires a new
  preview; a rejected authoring transaction does not change its source or graph.
- Native/schema change: use **Build, Restart & Continue**, not hot reload.

## Done

The golden path is complete when all of these are true:

- Apply is repeatable and Doctor is error-free;
- initial setup has a successful project-local receipt;
- the placed host runs `SA_FirstScript` in Play;
- one ordinary C# edit reaches the running host through Build & Reload;
- no internal generator ordering or repository path was required.

Next, open the recipe index directly from the adopted project:

```powershell
& "$projectTools\finare.cmd" guide recipes
```

For repeatable CMD measurement of this path and the compatible edit/reload
loop, use the [versioned usability budget gate](UNREAL_USABILITY.md).
