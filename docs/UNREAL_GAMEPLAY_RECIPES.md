# Unreal gameplay task recipes

These recipes start after the [first project golden path](UNREAL_FIRST_PROJECT.md)
passes. They describe the supported product loop for ordinary gameplay work,
not a second build system. Replace the example namespace and generated type
names with the exact names shown by the project's **Unreal API Browser**.

## One loop for every recipe

1. Open the target Script Asset and the **Unreal API Browser**.
2. Search for the required type, function, property, delegate, or World service.
3. If it is absent, add the exact reflected type/member to the project Contract,
   or let the configured development surface propose it.
4. Run **Build & Reload**. Use **Build, Restart & Continue** only when the shared
   receipt reports `restartRequired`.
5. Use the generated C# wrapper or the matching typed node. Never call an
   internal thunk, cache a raw UObject address, or run a generator by hand.
6. Verify the result in Play, then run project validation before packaging.

Examples use this generated namespace:

```csharp
using UnifiedScript;
using UnifiedScript.Unreal;
using MyGame.UnifiedScript.Generated;
```

The API Browser is authoritative for member paths, parameter direction,
World-context policy, thread policy, deprecation, and native/fallback coverage.

## Actor behavior

Use `UnrealActorScript` when the Script Asset targets an Actor. It gives the
generation-safe target and host handles, World-owned async lifetime, Tick, and
Actor overlap callbacks:

```csharp
[ScriptAsset("MyGame/PatrolActor")]
public sealed class PatrolActorScript : UnrealActorScript
{
    [Expose(InstanceEditable = true, Category = "Patrol")]
    public float Speed { get; set; }

    public PatrolActorScript()
    {
        Speed = 300.0f;
    }

    protected override void OnBeginPlay()
    {
        if (!Context.HasDistinctTarget)
        {
            throw new InvalidOperationException("PatrolActor requires an Actor target.");
        }
    }

    protected override void OnTick(float deltaSeconds)
    {
        // Wrap TargetHandle with the exact generated Actor type, then invoke
        // only members present in the API Browser.
    }

    protected override void OnActorBeginOverlap(UnrealObjectHandle otherActor)
    {
        _ = otherActor.IsWellFormed;
    }

    protected override void OnEndPlay()
    {
    }
}
```

Script state defaults may use either a property initializer or direct constant
assignments in the explicit parameterless constructor. Constructor assignments
are applied in order and override the initializer. Keep them unconditional and
top-level; branches, loops, compound assignments, runtime values, local
functions, and lambdas are intentionally rejected so Blueprint CDO and packaged
defaults remain deterministic.

Attach the Script Asset to a `UnifiedScriptActor` or to a
`UnifiedScriptComponent` whose target is the owning Actor. Verify BeginPlay,
Tick, overlap, EndPlay, level travel, and one Build & Reload. `TargetHandle` is
the gameplay Actor; `HostHandle` is the component that owns the generation.

## Component behavior

Use a normal `IUnrealManagedScript` when a Blueprint or native Component is the
target and you want explicit lifecycle control. Wrap `context.Target` once and
discard the wrapper on `OnStop`:

```csharp
[ScriptAsset("MyGame/HealthComponent")]
public sealed class HealthComponentScript : IUnrealManagedScript
{
    private HealthComponent _component = null!;

    public void OnStart(UnrealScriptContext context)
    {
        _component = new HealthComponent(context.Target, context.Bridge);
        _component.SetCurrentHealth(100.0f);
    }

    public void OnTick(float deltaSeconds)
    {
        _ = deltaSeconds;
    }

    public void OnStop()
    {
        _component = null!;
    }
}
```

`HealthComponent` and `SetCurrentHealth` are project examples; select the real
wrapper/member in the API Browser. Verify the component on two Actor instances
so state ownership is not accidentally static or World-global.

## Blueprint event

For Blueprint-to-managed events, expose a typed multicast delegate in the
Blueprint, include its generated class and delegate in reflection/Contract, and
copy the exact `memberPath` from the API Browser into `UnrealEvent`:

```csharp
[UnrealEvent(
    typeof(DamageSignalComponent),
    "/Game/Gameplay/BP_DamageSignal.BP_DamageSignal_C:OnDamage",
    Source = UnrealEventSource.Target)]
private void OnDamage(float amount, UnrealObjectHandle instigator)
{
    if (instigator.IsWellFormed)
    {
        ApplyDamage(amount);
    }
}
```

For managed-to-Blueprint calls, expose a callable function or implementable
event on that Blueprint class and invoke its generated wrapper method. Do not
fill in a plausible path when discovery is ambiguous: the build must produce
the exact Blueprint-generated-class identity first. Verify subscribe, fire,
unsubscribe on reload, Actor deletion, and level travel; a retired generation
must never receive the event.

## Enhanced Input and native interface

Bind Enhanced Input with the exact `UInputAction` object path produced by the
project. The attribute also fixes the trigger phase, value shape, and consume
policy in Declaration IR, so changing any of them is a reviewed contract change:

```csharp
[UnrealInputAction(
    "/Game/Input/IA_Move.IA_Move",
    TriggerEvent = UnrealInputTriggerEvent.Triggered,
    ValueType = UnrealInputValueType.Axis2D,
    ConsumeInput = true)]
private void OnMove(UnrealVector value)
{
    ApplyMovement(value);
}
```

Implement a native Unreal interface by naming its exact `/Script/Module.Type`
path and reflected function. The method signature must match the reviewed API
Contract exactly; native callers continue to use Unreal's normal `Execute_`
entry point:

```csharp
[UnrealInterfaceFunction(
    "/Script/MyGame.Damageable",
    "ApplyManagedDamage")]
public int ApplyManagedDamage(int amount)
{
    return ApplyDamage(amount);
}
```

Build & Reload must leave one input binding and one delegate subscription per
running Actor. Verify input and interface return values before and after a
compatible reload, then delete the Actor or travel to another level and confirm
that the retired generation receives no callback.

## Asset load

Use the generated World service for synchronous startup loads whose hitch is
explicitly acceptable:

```csharp
private static UnrealObjectHandle LoadIcon(
    UnrealScriptContext context,
    string objectPath)
{
    var world = new UnifiedScriptWorldLibrary(context.Target, context.Bridge);
    UnrealObjectHandle icon = world.LoadObjectByPath(context.Target, objectPath);
    if (icon.IsNull)
    {
        throw new InvalidOperationException($"Asset was not found: {objectPath}");
    }
    return icon;
}
```

Use an Unreal object path such as
`/Game/UI/T_StatusIcon.T_StatusIcon`, not a disk path. For gameplay-time loads,
prefer `LoadObjectAsync` or `LoadClassAsync` from the async recipe. Loaded
resources are retained by the World host and released with its lifecycle; do
not store raw pointers or assume they survive travel.

## Spawn Actor

Load a class through the World service and pass the resulting typed class
handle to `SpawnActor`:

```csharp
private static UnrealObjectHandle SpawnPickup(UnrealScriptContext context)
{
    var world = new UnifiedScriptWorldLibrary(context.Target, context.Bridge);
    UnrealClassHandle actorClass = world.LoadClassByPath(
        context.Target,
        "/Game/Gameplay/BP_Pickup.BP_Pickup_C");
    if (actorClass.IsNull)
    {
        throw new InvalidOperationException("Pickup class could not be loaded.");
    }

    UnrealObjectHandle actor = world.SpawnActor(
        context.Target,
        actorClass,
        UnrealTransform.Identity);
    if (actor.IsNull)
    {
        throw new InvalidOperationException("Pickup could not be spawned.");
    }
    return actor;
}
```

Use `DestroyActor` through the same generated service when the script owns the
spawned instance. Verify spawn failure, explicit destroy, World teardown, and
reload; never carry the handle into another World.

## Trace and FHitResult

Use the generated synchronous service for an occasional trace and inspect the
field-addressable `HitResult` value:

```csharp
private static UnrealObjectHandle TraceForward(
    UnrealScriptContext context,
    UnrealVector start,
    UnrealVector end)
{
    var world = new UnifiedScriptWorldLibrary(context.Target, context.Bridge);
    bool hit = world.LineTraceSingle(
        context.Target,
        start,
        end,
        context.Target,
        out HitResult result,
        out UnrealObjectHandle hitActor);

    return hit && result.bBlockingHit ? hitActor : default;
}
```

For many traces per frame, use `context.DataPlane.BeginTraceBatch(count)` and
execute once; do not loop over the bridge. Read results before disposing the
batch and copy only data that must outlive it. Verify miss, blocking hit,
ignored Actor, and batch-capacity behavior.

## UI bridge

Keep widget construction and animation in UMG/Blueprint, and expose a small
typed Blueprint bridge to managed gameplay. For example, a
`BP_ManagedHudBridge` Actor can own the widget and expose `SetStatusText` plus an
`OnActionPressed` delegate. After Build & Reload, use its generated wrapper:

```csharp
private ManagedHudBridge _hud = null!;

private void BindHud(UnrealScriptContext context)
{
    _hud = new ManagedHudBridge(context.Target, context.Bridge);
    _hud.SetStatusText("Ready");
}

[UnrealEvent(
    typeof(ManagedHudBridge),
    "/Game/UI/BP_ManagedHudBridge.BP_ManagedHudBridge_C:OnActionPressed",
    Source = UnrealEventSource.Target)]
private void OnActionPressed(int actionId)
{
    _hud.SetStatusText($"Action {actionId}");
}
```

This keeps Slate/UMG ownership native while gameplay policy remains C#/Graph.
If the project deliberately exposes `CreateWidget` and viewport members in its
Contract, their generated wrappers are also valid; their World/thread policy in
the API Browser still applies. Verify viewport creation, input event delivery,
reload unsubscription, and map teardown.

## Async work

Derive from `UnrealObjectScript` or `UnrealActorScript` to use the World-scoped
Async Service. Capture `Lifetime`, handle cancellation, and update Unreal only
after returning to the managed Tick pump:

```csharp
private async Task LoadHudAsync()
{
    CancellationToken lifetime = Lifetime;
    try
    {
        await DelayAsync(0.1f, timeoutSeconds: 2.0f, lifetime);
        UnrealObjectHandle hudClass = await LoadClassAsync(
            "/Game/UI/WBP_Hud.WBP_Hud_C",
            timeoutSeconds: 5.0f,
            cancellationToken: lifetime);

        if (!lifetime.IsCancellationRequested && hudClass.IsWellFormed)
        {
            // Continue through a generated game-thread-safe wrapper.
        }
    }
    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
    {
        // Reload, deletion, travel, and PIE stop cancel this generation.
    }
}
```

Start the task from `OnBeginPlay`/`OnInitialized` and intentionally discard it
only when all exceptions are handled. Do not use `Task.Run` for UObject work.
Verify timeout, manual cancellation, reload, deletion, travel, and PIE stop.

## Packaging

Before packaging, save the map and run the project-local validation gate. It
rechecks deterministic generation, Contract coverage, Script Assets, managed
startup, and chunk freshness without rewriting source:

```powershell
$projectRoot = Split-Path -Parent $project
$projectTools = Join-Path $projectRoot 'Plugins\UEUnifiedScript\Tools'
powershell -ExecutionPolicy Bypass `
  -File "$projectTools\setup-unreal-project.ps1" `
  -Project $project `
  -EngineRoot $engine `
  -ValidateOnly
```

Then use the ordinary Unreal pipeline; UnifiedScript stages the managed runtime
and compiled chunks through the plugin:

```powershell
& "$engine\Engine\Build\BatchFiles\RunUAT.bat" BuildCookRun `
  "-project=$project" `
  -noP4 -platform=Win64 -clientconfig=Development `
  -build -cook -stage -pak -archive `
  '-map=/Game/Maps/Main' `
  '-AdditionalCookerOptions=-SkipZenStore' `
  '-archivedirectory=D:\Builds\MyGame-UnifiedScript'
```

Launch the archived executable and verify the same Actor/Component lifecycle,
asset paths, events, UI, and async cancellation used in Editor Play. A green
compile without a packaged runtime smoke is not packaging evidence. See
[Win64 release packaging](UNREAL_RELEASE.md) for Shipping, Automation, and
compiled-chunk details.

## When a recipe is blocked

- Missing type/member: use the API Browser and the development-surface proposal;
  review the resulting Contract change.
- `fallback` coverage: decide whether the frequency is acceptable; hot gameplay
  paths should move to generated native coverage or the data plane.
- `rejected` coverage: the diagnostic owns the fix; do not bypass it with raw
  reflection or pointer interop.
- `restartRequired`: use **Build, Restart & Continue** before Play.
- Runtime fault: inspect `LastScriptError`, the shared diagnostics page, and the
  privacy-safe diagnostics bundle described in
  [Authoring diagnostics](AUTHORING_DIAGNOSTICS.md).
