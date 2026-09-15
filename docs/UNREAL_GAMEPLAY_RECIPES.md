# Unreal gameplay task recipes

These recipes start after the [first project golden path](UNREAL_FIRST_PROJECT.md)
passes. They describe the supported product loop for ordinary gameplay work,
not a second build system. Replace the example namespace and generated type
names with the exact names shown by the project's **Unreal API Browser**.

## One loop for every recipe

1. Open the target Script Asset and the **Unreal API Browser**.
2. Search for the required type, function, property, delegate, or World service.
3. If it is absent, check the configured reflection modules and API coverage
   diagnostic. Setup generates the supported module API library; there is no
   project member Contract to maintain. A missing signature needs metadata or
   transport support, not a new per-component binding record.
4. Run **Build & Reload**. Use **Build, Restart & Continue** only when the shared
   receipt reports `restartRequired`.
5. Use the generated C# wrapper or the matching typed node. Never call an
   internal thunk, cache a raw UObject address, or run a generator by hand.
6. Verify the result in Play, then run project validation before packaging.

Examples use this generated namespace:

```csharp
using Finare;
using Finare.Unreal;
using MyGame.Finare.Generated;
```

The API Browser is authoritative for member paths, parameter direction,
World-context policy, thread policy, deprecation, and native/fallback coverage.

## Actor behavior

For runtime component creation, see the complete
[C# example](../tests/Finare.Tests/Fixtures/NativeComponentCreation/ComponentScripts.cs)
and [reproduction guide](../tests/Finare.Tests/Fixtures/NativeComponentCreation/README.md).
It uses the generated Actor's `AddComponentByClass`, sets properties before
`FinishAddComponent` when deferred, and calls generated attachment/destruction
methods. Release components owned by the behavior when it ends so a reload
does not duplicate them. Native constructor/default-subobject support is a
separate lifecycle requirement; these runtime calls do not implement it.

Use `UnrealActorScript` when the Script Asset targets an Actor. It gives the
generation-safe target and host handles, World-owned async lifetime, Tick, and
Actor overlap callbacks:

```csharp
[ScriptAsset("MyGame/PatrolActor")]
[UnrealScriptClass(UnrealScriptClassKind.Actor,
    GeneratedClassPath = "/Game/PatrolActor.PatrolActor_C")]
public sealed partial class PatrolActorScript : UnrealActorScript
{
    [Expose(InstanceEditable = true, Category = "Patrol")]
    public partial float Speed { get; set; }

    public PatrolActorScript()
    {
        Speed = 300.0f;
    }

    protected override void OnBeginPlay()
    {
        // HostHandle and TargetHandle both identify this generated Actor.
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

The reviewed Blueprint-visible common state shapes can be authored directly in
the same class:

```csharp
[Expose] public partial UnrealName StateTag { get; set; }
[Expose] public partial UnrealText DisplayText { get; set; }
[Expose] public partial UnrealObject? LinkedObject { get; set; }
[Expose] public partial UnrealClassHandle LinkedClass { get; set; }
[Expose] public partial UnrealVector SpawnOffset { get; set; }

public MyActorScript()
{
    StateTag = new UnrealName("Demo");
    DisplayText = new UnrealText("Ready");
    LinkedObject = default;
    LinkedClass = default;
    SpawnOffset = new UnrealVector(100.0, 0.0, 0.0);
}
```

These become native `FName`, `FText`, `UObject`, `UClass`, and `FVector`
properties on the generated Blueprint class and use the same state Semantic ID
for CDO reconciliation. Generated partial accessors read and write the native
property immediately, including after a native or Blueprint edit. Source
defaults for object references and class handles must remain null: a nonzero handle is valid only for its current
World registry generation. Assign durable object/class references through the
normal Blueprint CDO/instance property surface or resolve assets at runtime.

For Script Classes, use partial `[Expose]` properties and the analyzer
already included by the project template. A reflected container getter returns
a value snapshot; assign the edited value back to replace the native container.
For example, `var scores = Scores.ToArray(); scores[0]++; Scores = scores;`.
Object references inside the snapshot still identify their original native
objects. Access through the script fails after its generation stops. Non-partial
reflected state is a compilation error. A `[ScriptAsset]` owner also projects
instance properties without `[Expose]`; those properties must be partial too.
Use ordinary fields for private managed values that should not become UE state.

Generated Unreal enum wrappers, `UnrealRotator` and `UnrealTransform` also have
typed state/default contracts. For example, the
[CommonStateGameplayDemoScript](../samples/Unreal/Finare.Unreal.Demo/CommonStateGameplayDemoScript.cs)
exposes configuration used by ordinary generated Actor calls:

```csharp
[Expose(InstanceEditable = true, SaveGame = true)]
public partial ETickingGroup TickGroup { get; set; } = ETickingGroup.TG_DuringPhysics;

[Expose(InstanceEditable = true, SaveGame = true)]
public partial UnrealRotator Facing { get; set; } = new(0.0, 45.0, 0.0);

[Expose(InstanceEditable = true, SaveGame = true)]
public partial UnrealTransform Placement { get; set; } = new(
    UnrealQuaternion.Identity,
    new UnrealVector(120.0, 0.0, 40.0),
    UnrealVector.One);
```

Import `Finare.Unreal.Generated` for the selected runtime bindings'
`ETickingGroup` wrapper. BeginPlay applies the configured tick group and
Transform; each Tick changes `Facing` and applies it to the Actor. The native
Script Class fields and managed properties exchange the same types. This sample
configures Actor tick properties; it does not establish managed component tick
ordering relative to every Unreal tick group.

Use `UnrealTransform.Identity` for an identity default. C# `default` and `new()`
produce a zero quaternion and are rejected for Transform state. Explicit
quaternions must be normalized, and all components must be finite constants.
Named constructor arguments may be reordered. Generated enum defaults may use
an attributed named member or `new EnumWrapper(constantLong)`; native
materialization requires a declared UEnum value, and Blueprint enum fields also
require a value in their byte storage range. Ordinary CLR enums retain the
existing member-name contract.

For persistent asset configuration, declare soft-reference state instead of
storing a runtime object handle:

```csharp
[Expose(InstanceEditable = true, SaveGame = true)]
public partial UnrealSoftObjectHandle Mesh { get; set; } =
    new("/Engine/BasicShapes/Cube.Cube");

[Expose(InstanceEditable = true, SaveGame = true)]
public partial UnrealSoftClassHandle ActorClass { get; set; } =
    new("/Script/Engine.StaticMeshActor");
```

These fields appear as native soft-object and soft-class properties. Their
defaults and overrides store paths across reload; creating or reading the
script does not load the assets. `default`, `new()` and an empty path are null
references. Use absolute Unreal object paths, including the object name after
the dot. A valid path can refer to an asset that is currently missing, so handle
failure when explicitly loading it.

In an asynchronous helper on an `UnrealObjectScript`, use
`await LoadObjectAsync(Mesh)` and `await LoadClassAsync(ActorClass)`. These typed
overloads use the existing World-owned async service. The result is a runtime
handle for generated native calls; it is not a replacement for the persistent
soft-reference field. Keep cancellation, failed-load recovery and spawned Actor
cleanup in the same lifecycle scope as other asynchronous gameplay work.

[SoftReferenceGameplayDemoScript](../samples/Unreal/Finare.Unreal.Demo/SoftReferenceGameplayDemoScript.cs)
uses editable mesh and Actor-class references to load assets and create a
preview Actor through generated native APIs.

Editable collection configuration uses the existing read-only collection
interfaces. The property remains assignable so gameplay can publish a new
collection value:

```csharp
[Expose(InstanceEditable = true, SaveGame = true)]
public partial IReadOnlyList<UnrealSoftObjectHandle> Meshes { get; set; } =
    new List<UnrealSoftObjectHandle>
    {
        new("/Engine/BasicShapes/Cube.Cube"),
        new("/Engine/BasicShapes/Sphere.Sphere"),
    };

[Expose(InstanceEditable = true)]
public partial IReadOnlySet<UnrealName> EnabledChoices { get; set; } =
    new HashSet<UnrealName> { new("Cube"), new("Sphere") };

[Expose(InstanceEditable = true)]
public partial IReadOnlyDictionary<UnrealName, int> ChoiceIndices { get; set; } =
    new Dictionary<UnrealName, int>
    {
        { new("Cube"), 0 },
        { new("Sphere"), 1 },
    };
```

These become native Array, Set and Map properties. Use explicit empty
initializers for empty configuration; a null collection is not a native empty
container. Elements use the reviewed common state types, with native hashing
required for Set elements and Map keys. Direct nested containers are rejected.
Soft references inside a collection keep persistent paths and load only when
gameplay explicitly requests them. Check that the selected name is enabled,
resolve its index through `ChoiceIndices`, and check the index against
`Meshes.Count` before loading that path. Snapshot the selected path before
awaiting the same async loader used by the scalar soft-reference example.
The [collection-state example](../samples/Unreal/Finare.Unreal.Demo/CollectionStateGameplayDemoScript.cs)
combines that selection with generated Actor APIs. `SelectSpherePreview`
publishes a new array, set and map configuration before loading its selected
mesh. The prior preview remains alive until the replacement is ready, and the
script releases its owned preview on shutdown or generation replacement.

Attach the Script Asset to a `FinareActor` or to a
`FinareComponent` whose target is the owning Actor. Verify BeginPlay,
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

## Blueprint-callable common values and outputs

A normal public `[Visual]` method becomes a generated typed Blueprint function.
The reviewed common function matrix includes `UnrealName`, `UnrealText`,
`UnrealClassHandle`, `UnrealRotator`, and `UnrealTransform`; C# `ref` becomes an
input/output pin and `out` becomes an output-only pin:

```csharp
[ScriptAsset("MyGame/GameplayLibrary")]
[UnrealScriptClass(UnrealScriptClassKind.BlueprintFunctionLibrary)]
public static class GameplayLibrary
{
    [Visual]
    public static UnrealClassHandle ResolveSpawnContract(
        UnrealName profile,
        ref UnrealRotator rotation,
        out UnrealText displayName,
        UnrealTransform origin)
    {
        // Resolve values through generated APIs as needed.
        rotation = default;
        displayName = default;
        return default;
    }
}
```

Building creates an ordinary Blueprint Function Library node. The return
pin is followed by the `ref`/`out` result pins in source order, while stable
parameter names keep that contract intact even if Unreal internally reorders
output properties. The same directions support reviewed `IReadOnlyList<T>`,
`IReadOnlySet<T>`, `IReadOnlyDictionary<TKey, TValue>`, `UnrealSoftObjectHandle`
and `UnrealSoftClassHandle` values. The [Demo library](../samples/Unreal/Finare.Unreal.Demo/DemoScript.cs)
contains exchange functions for all five families; connect their outputs to
ordinary downstream Blueprint nodes.

Component-backed functions support compatible instance reload. For a static
library, edit its body and run **Build & Reload** to update the existing Worlds
that have already called it. Selected-asset reload uses the same route. Each
World owns its loaded code generation; static CLR fields reset with a successful
replacement. Keep persistent gameplay state on Script Class instances.

Reload validates every loaded World copy before publishing a replacement.
Changing an existing callable signature or failing to load the candidate keeps
the current code callable and reports the failure. Adding a callable is
compatible. A World that has not used the library stays unloaded until its first
call. Callers continue to use the generated Blueprint nodes.

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

### Keeping an event object after its callback

Object handles supplied by native events are borrowed for that callback. If a
script needs to use one later, retain it through its generation's `Bridge`:

```csharp
private UnrealObjectReference? _observedActor;

private void RememberActor(UnrealObjectHandle actor)
{
    UnrealObjectReference? next = actor.IsWellFormed
        ? UnrealObjectReference.Retain(Bridge, actor)
        : null;
    _observedActor?.Dispose();
    _observedActor = next;
}

protected override void OnEndPlay()
{
    _observedActor?.Dispose();
    _observedActor = null;
}
```

Use `_observedActor.Handle` with the generated wrapper. The lease keeps the
registry entry registered; it does not keep the UObject alive. UObject deletion
or World teardown still makes native access fail with an invalid/stale handle.
Retain and release on the game thread. Each generation owns its leases and
releases any remaining ones at stop/reload; early `Dispose` releases only that
lease. A disposed lease or generation rejects further `Handle` access. Older
native transports without the optional reference extension report
`NotSupportedException` when retaining.

Leases and private fields do not persist across reload. Store state that must
survive generation replacement in the normal Script Class's exposed native
properties, then restore subscriptions and transient wrappers from that state.

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
private static UnrealObject LoadIcon(
    UnrealScriptContext context,
    string objectPath)
{
    var world = new FinareWorldLibrary(context.Target, context.Bridge);
    var receiver = UnrealObject.Wrap<UnrealObject>(context.Target, context.Bridge)!;
    UnrealObject? icon = world.LoadObjectByPath(receiver, objectPath);
    if (icon is null)
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
private static Actor SpawnPickup(UnrealScriptContext context)
{
    var world = new FinareWorldLibrary(context.Target, context.Bridge);
    var receiver = UnrealObject.Wrap<UnrealObject>(context.Target, context.Bridge)!;
    UnrealClassHandle actorClass = world.LoadClassByPath(
        receiver,
        "/Game/Gameplay/BP_Pickup.BP_Pickup_C");
    if (actorClass.IsNull)
    {
        throw new InvalidOperationException("Pickup class could not be loaded.");
    }

    Actor? actor = world.SpawnActor(
        receiver,
        actorClass,
        UnrealTransform.Identity);
    if (actor is null)
    {
        throw new InvalidOperationException("Pickup could not be spawned.");
    }
    return actor;
}
```

Use `DestroyActor` through the same generated service when the script owns the
spawned instance. Verify spawn failure, explicit destroy, World teardown, and
reload; never carry the handle into another World.

## Create an ordinary UObject

Use `CreateObject` for an ordinary UObject class, with a World-associated Outer:

```csharp
private static UnrealObject CreateData(UnrealScriptContext context, string classPath)
{
    var world = new FinareWorldLibrary(context.Host, context.Bridge);
    var receiver = UnrealObject.Wrap<UnrealObject>(context.Host, context.Bridge)!;
    UnrealClassHandle type = world.LoadClassByPath(receiver, classPath);
    return world.CreateObject(receiver, type, receiver)
        ?? throw new InvalidOperationException("Object creation or script activation failed.");
}
```

For a Finare Object Script Class, this also activates its managed behavior;
plain native classes are constructed without attaching a script. Keep a newly
created object in a native reflected object property when it must survive GC.
A C# view, handle lease, or Outer alone does not root the object. A missing Outer
defaults to the World; cross-World Outers and incompatible native class families
are rejected. Use `SpawnActor` for Actors and the appropriate UE component or
widget factory when their construction requires it.

## Trace and FHitResult

Use the generated synchronous service for an occasional trace and inspect the
field-addressable `HitResult` value:

```csharp
private static Actor? TraceForward(
    UnrealScriptContext context,
    UnrealVector start,
    UnrealVector end)
{
    var world = new FinareWorldLibrary(context.Target, context.Bridge);
    var receiver = UnrealObject.Wrap<UnrealObject>(context.Target, context.Bridge)!;
    bool hit = world.LineTraceSingle(
        receiver,
        start,
        end,
        receiver.IsA<Actor>() ? receiver.As<Actor>() : null,
        out HitResult result,
        out Actor? hitActor);

    return hit && result.bBlockingHit ? hitActor : default;
}
```

For many traces per frame, use `context.DataPlane.BeginTraceBatch(count)` and
execute once; do not loop over the bridge. Read results before disposing the
batch and copy only data that must outlive it. Verify miss, blocking hit,
ignored Actor, and batch-capacity behavior.

## UI bridge

Use generated UMG wrappers directly for ordinary widget creation, named-control
lookup, button events, enabled state and text. The
[async UI gameplay example](UNREAL_ASYNC_UI_DEMO.md) combines them with asset
loading and Actor creation, including cancel/retry and generation cleanup.
UMG/Blueprint can own the layout without owning or forwarding the gameplay.

A small typed Blueprint bridge is an optional integration choice when an
existing project's view already exposes one. For example, a
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

Both approaches keep Slate/UMG ownership native while gameplay policy remains
C#/Graph. The API Browser's World/thread policy still applies. For a view created
after an async load, subscribe through its generated event endpoint after creation
and dispose the subscription on stop; an attributed source member must already
exist when startup binding runs. Zero-argument events such as `Button.OnClicked`
are supported. Verify viewport creation, event delivery, reload unsubscription
and map teardown; viewport ownership, not a C# handle, keeps the view alive.

## .NET libraries and reload

Gameplay is a .NET class library hosted by CoreCLR. The shipped project template
targets `net10.0`; the reviewed Unreal distribution is UE 5.8.1 on Win64. Choose
managed libraries compatible with that target and their own platform requirements.
This runtime does not provide Node/npm APIs or a NativeAOT/iOS/console execution
path. See the current [release scope](UNREAL_RELEASE.md).

Add `ProjectReference` or `PackageReference` entries to the gameplay project.
The [MSBuild project loader](PROJECT_LOADING.md) reads both compilation references
and copy-local managed runtime dependencies. For package dependencies in an
SDK-style class library, explicitly request runtime copies:

```xml
<PropertyGroup>
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>
```

This copies transitive package runtime assemblies to the build output, as
documented by [Microsoft](https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#copylocallockfileassemblies).
The shipped gameplay template builds directly into the plugin's managed script
directory. If a custom project stages its output elsewhere, its runtime DLLs
must accompany the staged chunk; copying only the main assembly or `.uschunk`
does not deploy those dependencies. Reference assemblies from `ref/` are for
compilation, not runtime deployment.

Run the normal **Build & Reload** after changing a dependency. A chunk records
managed dependency identities and content digests; the World loader verifies
resolved runtime assemblies against that manifest. Replacing a dependency DLL
without rebuilding the corresponding chunk can fail verification. The shared
Finare contract/runtime assemblies belong to the host and are not private
copies for each script generation. Follow `restartRequired` when those change.

Each generation has a collectible assembly load context. Cancel owned work and
dispose timers, subscriptions and callback leases when the generation stops.
An external event handler, task or cached delegate that still references an old
script can keep that generation alive. Persistent state belongs in the supported
Script Class state contract, not a dependency's process-global cache.

Library compilation does not expand the editable Graph language. Keep code that
uses unsupported visual syntax in ordinary C# helper bodies. Project source
generators are replayed by the Finare compilation host; standalone diagnostic
analyzers, generator AdditionalFiles and analyzer-config inputs remain outside
that path. See the [project-loading restrictions](PROJECT_LOADING.md) before
adopting a package that generates required source. Native dependencies and P/Invoke additionally need a matching
OS/architecture, deployment and lifetime strategy; the managed dependency loader
does not establish general native-package support.

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
only when all exceptions are handled. Keep UObject access on the supported
World invocation route. Verify timeout, manual cancellation, reload, deletion,
travel, and PIE stop.

`Task.Delay`, network/file awaits and arbitrary library tasks are not the
World-scoped Async Service. Do not assume that their continuations resume on the
UE game thread or that reload cancels them. Pass the generation's cancellation
token where the API accepts one, observe exceptions, and return results through
the script's Tick/lifecycle path before updating Unreal. Pure managed background
work can use `Task.Run`; never block the game thread waiting for unfinished work
that may need the World invocation queue.

Supported worker-originated generated calls are a specific transport route: the
World host executes them on the game thread. The
[callback worker example](../samples/Unreal/Finare.Unreal.Demo/CallbackGameplayDemoScript.cs)
starts two workers, performs generated UE calls with nested callbacks, and only
reads their completion from Tick after `IsCompleted` is true. This is not a
general UObject thread-safety or synchronization-context guarantee. Register and
dispose native callback leases on the game thread, retain them for the required
native lifetime, and follow the
[callback contract](UNREAL_API_CONTRACT.md#reflected-callback-arguments) for
generation ownership, reentry, errors and supported signatures.

## Replicated state and RPC

Use a generated Actor or ActorComponent Script Class for the network examples.
The Actor must be replicated and have the intended client ownership;
materializing replicated state or RPC declarations enables replication on the
generated default object. A generated component must itself replicate, and its
Actor must separately enable replication. This does not assign a player owner
automatically. Runtime-enabled Actors are supported: a client's received UE role
identifies its replica even when the Actor archetype's `bReplicates` is false.

```csharp
[Expose(Replicated = true)]
public partial int Score { get; set; } = 0;

[UnrealRepNotify(nameof(Score))]
public void ScoreChanged(int previous)
{
    UpdateScoreDisplay(previous, Score);
}

[UnrealRpc(UnrealRpcTarget.Server, Reliable = true)]
public void ServerAdd(int amount)
{
    if (amount > 0 && amount <= 10)
    {
        Score += amount;
    }
}

[Visual]
public void RequestAdd(int amount)
{
    SendRpc(nameof(ServerAdd), amount);
}
```

`UpdateScoreDisplay` above is the game's own helper. `SendRpc(ServerAdd, amount)`
is the delegate-typed C# alternative; the `nameof` form fits the current visual
call boundary and validates the exact declared signature before dispatch.
Calling `ServerAdd(amount)` normally in C# is a local call, **not** a network send.
The attribute declares the receive body; there is no hidden source rewrite.

`Client` and `Multicast` use the same attribute/send API. UE supplies actual
callspace, actor channels, delivery and connection ownership. An owning client
may send a Server RPC; clients cannot originate Client/Multicast sends. A Server
RPC still needs game-specific payload/permission checks in its body. `HasAuthority`
queries the owning Actor rather than caching a role across travel/reload.

Writing a replicated property without authority is rejected by its generated
setter and the native bridge. Getters read the current native value directly,
including state received from the server. An initial RepNotify can
arrive before the managed host starts: its owned previous-value snapshot is queued
and delivered once after automatic activation. Pre-start changes coalesce while
retaining the first old value; stop/failed activation discards the queue. This is
not a promise that managed RepNotify precedes managed BeginPlay.

The [checked-in example](../samples/Unreal/Finare.Unreal.Demo/NetworkingDemoScript.cs)
combines replicated/owner-only state, RepNotify, Server/Client/Multicast RPCs and
reload. After running `tools/emit-unreal-demo-chunk.ps1 -Configuration Release`,
`Finare.Unreal.Networking.ConnectedScriptClass` consumes the emitted
declarations, materializes the class, reloads its saved package and uses a server
plus two connected clients under CMD/NullRHI. Ownership rejection and repeated replication are checked; role
flags or manual state copies are not the network evidence.

The [component example](../samples/Unreal/Finare.Unreal.Demo/NetworkingComponentDemoScript.cs)
uses a dynamically created, registered component on an ordinary replicated Actor.
`Finare.Unreal.Networking.ConnectedComponent` runs it through the same
saved-class/server/two-client workflow. `HostHandle` identifies the component;
`TargetHandle` and `HasAuthority` refer to its owning Actor. RPC object parameters
are resolved into each receiving World's object handles, not sent as raw managed
handle numbers. Disabling component replication explicitly rejects sends.

Set `Expose.ReplicationCondition` to `None`, `InitialOnly`, `OwnerOnly`, `SkipOwner`
or `InitialOrOwner`. The component example verifies the initial and later
audiences for all five, plus string previous-value notifications and client
reload. Its RPCs exercise zero, two, three and four inputs, including float,
bool, Vector, string and Actor references; the Actor example exercises one input.

The component's `RequestCommonValues` also sends a long integer beyond double's
exact range, a generated typed enum, Rotator and Transform to the server and back
to its owner. Long state remains exact after client reload. The Rotator check
uses UE's compressed-short wire result (including angle wrapping), not the
uncompressed source value; local bridge transfer itself retains binary64 values.

RPC declarations accept 0-4 common inputs. The path transport used by `SendRpc`
now implements bool, the supported integer widths, float/double, string/name/text,
typed enums, object/class handles and Vector/Rotator/Transform. This does not
make every transport kind a supported Blueprint pin or Script Class property:
the declaration/default matrix is still separately bounded. Return/ref/out parameters,
collections, Soft References and complex structures are not supported RPC payloads
yet. The connected samples prove the listed mixed payloads, not every accepted
signature or combination. Push-model runtime, travel and cooked network execution
still require dedicated verification. Do not infer packaged WindowsServer or
full networking parity from these examples.

## Packaging

Before packaging, save the map and run the project-local validation gate. It
rechecks deterministic generation, Contract coverage, Script Assets, managed
startup, and chunk freshness without rewriting source:

```powershell
$projectRoot = Split-Path -Parent $project
$projectTools = Join-Path $projectRoot 'Plugins\Finare\Tools'
powershell -ExecutionPolicy Bypass `
  -File "$projectTools\setup-unreal-project.ps1" `
  -Project $project `
  -EngineRoot $engine `
  -ValidateOnly
```

Then use the ordinary Unreal pipeline; Finare stages the managed runtime
and compiled chunks through the plugin:

```powershell
& "$engine\Engine\Build\BatchFiles\RunUAT.bat" BuildCookRun `
  "-project=$project" `
  -noP4 -platform=Win64 -clientconfig=Development `
  -build -cook -stage -pak -archive `
  '-map=/Game/Maps/Main' `
  '-AdditionalCookerOptions=-SkipZenStore' `
  '-archivedirectory=D:\Builds\MyGame-Finare'
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
