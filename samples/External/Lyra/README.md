# Lyra health HUD pilot

This sample moves the health widget's Controller/Pawn and health delegate
subscriptions into a normal Finare ActorComponent Script Class. Lyra's GAS,
damage/healing effects, widget layout, materials and animation functions remain
the production implementation. No Lyra asset is included in this repository.

Use UE 5.8.1 on Win64 and your own independent LyraStarterGame copy. The scripts
refuse the original reference at `D:/Project/LyraStarterGame`. They target the
existing `/Game/UI/Hud/W_Healthbar` used by the ShooterCore elimination experience.
These scripts require UE 5.8's Blueprint graph Python APIs.

## Install and create the class

Set `$PilotRoot`, `$SdkRoot`, `$EngineRoot` and `$SampleRoot` to absolute paths.
`$SdkRoot` is an extracted Finare SDK directory. Keep the Editor closed while
installing the SDK or running onboarding.

```powershell
& "$SdkRoot/Tools/finare.cmd" adopt "$PilotRoot/LyraStarterGame.uproject" --sdk-root "$SdkRoot"
Copy-Item "$SampleRoot/FinarePilotValidation" "$PilotRoot/Plugins/FinarePilotValidation" -Recurse
& "$PilotRoot/Plugins/Finare/Tools/setup-unreal-project.ps1" `
    -Project "$PilotRoot/LyraStarterGame.uproject" -EngineRoot $EngineRoot

$env:FINARE_LYRA_PILOT_ROOT = $PilotRoot
& "$EngineRoot/Engine/Binaries/Win64/UnrealEditor-Cmd.exe" `
    "$PilotRoot/LyraStarterGame.uproject" -unattended -nop4 -NullRHI -nosplash -NoSound `
    -run=pythonscript "-script=$SampleRoot/create_health_class.py"
```

Check `Saved/Finare/LyraPilot/class-creation.json` for `status: passed`.
The script uses the installed ActorComponent Script Class factory and retains
its new persistent ClassId when writing `HealthHud.cs`. Do not copy the first
pilot's generated classes or identity sidecars into another project.

```powershell
& "$SampleRoot/configure_health_api.ps1" -PilotRoot $PilotRoot
& "$PilotRoot/Plugins/Finare/Tools/setup-unreal-project.ps1" `
    -Project "$PilotRoot/LyraStarterGame.uproject" -EngineRoot $EngineRoot

& "$EngineRoot/Engine/Binaries/Win64/UnrealEditor-Cmd.exe" `
    "$PilotRoot/LyraStarterGame.uproject" -unattended -nop4 -NullRHI -nosplash -NoSound `
    -run=pythonscript "-script=$SampleRoot/migrate_health_widget.py"
```

The API fragment is merged into the installed project's existing Contract;
its namespace and unrelated policies are preserved. Selected Blueprint/Lyra
members use the SDK's reflection route. Unsupported API requests remain errors.
Onboarding generates the bindings, compiles/emits the managed program and
synchronizes the normal Script Class.

The migration script requires the original widget's known graph topology. It
bypasses the native health subscribe/unsubscribe nodes while preserving the
connected animation and display initialization. Widget Construct creates the
component on its owning PlayerController and calls `Initialize`. Repeated
Construct and Destruct release the old component through its own Blueprint
function, respecting UE's component destruction ownership rule. The script
saves only after compilation succeeds and refuses an already migrated widget.
Check `Saved/Finare/LyraPilot/widget-migration.json` for `status: passed`.

The script retains the Pawn handle received from the borrowed event callback
with a generation-owned `UnrealObjectReference`. This keeps the handle usable
across calls without preventing UObject destruction. Exposed Pawn and Widget
identities survive managed reload; restoring the same pair resubscribes and
refreshes current health without replaying the widget's spawn animation.

## Run the production behavior check

With the Editor closed, run:

```powershell
& "$SampleRoot/run_health_pie.ps1" -PilotRoot $PilotRoot -EngineRoot $EngineRoot
```

The runner launches the Editor hidden with `-RenderOffscreen` and the Python
check, then requires a fresh successful gameplay report, an exit code of zero,
and no Ensure, fatal error or failed GC cleanup in the engine log. The check
releases its Python component reference before ending PIE so it cannot keep
the test World alive through UE's Python reference collector.
NullRHI cannot drive this Slate/UMG PIE check. The included editor-only validation plugin does
one thing: request the normal PIE session without a visible viewport. It does
not supply health values or gameplay callbacks.

The check opens `/ShooterMaps/Maps/L_Expanse`, disables test bots, waits for
Lyra's spawn damage immunity, and uses the actual `DamageSelf`, `HealSelf` and
`DamageSelfDestruct` console routes. It compares native health, the original
numeric HUD target, all three health-material targets and managed callback
counts. The widget's `NormalizedHealth` field is an initialization cache; live
damage updates write the materials' `Health_Updated` parameters. It also
checks generation reload, Pawn replacement, explicit stop/restart and World
destruction. Results are written to `Saved/Finare/LyraPilot/health-pie.json`.
An Editor exit code alone does not prove that the script's assertions passed.
The runner uses `-NoSound`; audio behavior is outside this check. Lyra's original
respawn widget may log an absent CountdownAudio reference in that configuration.

The sample's current acceptance evidence and remaining work are tracked in
[`docs/plans/2026-09-11-lyra-pilot.md`](../../../docs/plans/2026-09-11-lyra-pilot.md).
