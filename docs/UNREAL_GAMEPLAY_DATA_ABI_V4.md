# Unreal Gameplay Data ABI v4

Status: active World-level command, event, and collision-query contract. It is
independent from call-frame ABI v3, reverse-event ABI v4, and the Transform
Shared Data ABI v4.

## Purpose

Gameplay Data ABI v4 moves frequent homogeneous work off repeated reflected or
generated function calls:

- managed code writes fixed command rows directly into native-owned memory;
- native code executes committed commands once per World phase;
- completion events are published in a borrowed double buffer on the next
  managed phase;
- line, sphere, and box traces are submitted and returned in one synchronous
  boundary call.

The Binding control plane remains authoritative for resource discovery,
arbitrary reflected functions, properties, delegates, and uncommon signatures.
Gameplay Data is an optimized execution lane, not a replacement type system.

## World phase scheduler

Each `UUnifiedScriptWorldSubsystem` owns one
`FUnifiedScriptGameplayDataPlane` and binds only to its own `UWorld`:

```text
OnWorldPreActorTick
  -> NativePreActorTick
  -> publish Transform snapshot and prior command events
  -> ManagedTick: scripts may reserve/commit commands and execute trace batches

OnWorldPostActorTick
  -> NativePostActorTick
  -> execute committed commands in reservation order
  -> Idle
```

The subsystem opens an initial managed phase during World initialization so
BeginPlay scripts can use the same API before the first Actor tick. Teardown
removes both delegates, detaches the bridge context so retained transports fail
safely, shuts down the Shared and Gameplay Data planes and releases their
buffer-owned handles, and finally invalidates the World registry.

All operations currently require the Unreal Game Thread. Command and trace
reservation, commit, and execution require `ManagedTick` and reject the wrong
phase before exposing writable memory or executing side effects. Read-only
phase inspection and event acquisition remain available while the context and
World are alive.

## Command buffer

`FUnifiedScriptCommandV4` is a fixed 224-byte tagged row. The active command
types are:

| Command | Primary operands | Completion kind |
| --- | --- | --- |
| `SetActorTransform` | Actor, transform, sweep/teleport flags | `CommandCompleted` |
| `SetActorVelocity` | Actor, linear velocity | `CommandCompleted` |
| `ApplyDamage` | Actor, amount, optional instigator/causer/type | `DamageApplied` |
| `SpawnActor` | Actor class and transform | `ActorSpawned` |
| `DestroyActor` | Actor | `ActorDestroyed` |
| `SpawnEmitter` | particle system and transform | `EffectSpawned` |

`SpawnEmitter` is a successful visual no-op when UE reports that the process
can never render or the World is a dedicated server. In a render-capable World,
failure to create the particle component is reported as `ExecutionFailed`.

`BeginCommands(capacity)` returns a stack-only `UnrealCommandBatch` over
native-owned rows. Only one uncommitted reservation may exist at a time, so a
later reservation cannot reallocate storage behind a live `Span`. `Commit(n)`
assigns stable sequence IDs and publishes exactly the first `n` rows for
PostActorTick execution. Disposing an uncommitted batch commits zero rows and
releases the reservation.

Commands committed by multiple scripts execute in reservation order, and rows
inside a reservation execute in array order. A failed row does not prevent
later rows from running; each row receives its own status event.

## Event buffer

`FUnifiedScriptGameplayEventV4` is an 88-byte completion row keyed by the
command sequence ID. Events produced in `NativePostActorTick` become visible at
the next `NativePreActorTick`. The 64-byte view includes World/frame identity,
entry count and size, plus the cumulative dropped-event count.

The event span and returned Actor/component/effect handles are borrowed. They
must not be retained past the current managed tick. Native keeps referenced
objects registered while their event buffer is published and releases those
registrations when that buffer is recycled. Persistent object ownership still
belongs to the ordinary generated Binding path.

The current event capacity is 4,096 rows per World frame. Overflow increments
`DroppedEventCount` and immediately releases any handle owned by the dropped
row.

## Batch Trace

One trace batch contains up to 65,536 fixed 184-byte requests. Every request
chooses `Line`, `Sphere`, or `Box`, a collision channel, start/end positions,
shape extents, box rotation, and zero to four World-scoped ignored Actors.

`Execute()` performs every query synchronously on the Game Thread and returns
a borrowed span of 168-byte result rows. Each result preserves the request
sequence ID, status, blocking/start-penetrating flags, Actor/component handles,
face/item/element indices, distance/time, location, impact point, normal, and
impact normal. A miss is a successful row without `BlockingHit`.

Only one trace batch may be active. Its request span is invalid after execute
or cancel; its result span and hit handles are valid until the next trace batch
begins or the World shuts down.

## World-scoped handles and validation

Every command operand, ignored Actor, and returned hit object uses the existing
64-bit slot/generation handle plus a 64-bit `WorldHostId`. Native rejects:

- a non-null handle from another World;
- a stale generation or deleted UObject;
- an object whose Unreal type does not satisfy the command;
- an invalid collision channel, shape, extent, or ignore count;
- a batch token from another reservation or phase.

No `UObject*`, `FHitResult`, `FTransform`, `TArray`, allocator, or engine-private
layout crosses CoreCLR.

## Discovery and frozen layouts

The append-only `FUnifiedScriptNativeTransportV2` suffix points to an
independent 104-byte `FUnifiedScriptGameplayDataTransportV4` table. Managed code
accepts it only when ABI/table/entry sizes, capabilities, and all function
pointers match.

| Schema | ID | Entry/view size |
| --- | --- | ---: |
| command | `0x5553434D44423401` | 224 / 72 |
| event | `0x555345564E543401` | 88 / 64 |
| trace request | `0x5553545245513401` | 184 / 64 |
| trace result | `0x5553545245533401` | 168 / 64 |

Native `static_assert`s and managed `Marshal.SizeOf` tests lock these layouts.
Extensions must use new schema IDs or append a size-checked table suffix rather
than mutating a frozen row.

## Managed example

```csharp
UnrealWorldDataPlane data = Context.DataPlane;

using UnrealTraceBatch traces = data.BeginTraceBatch(1);
traces.Requests[0] = UnrealTraceRequestV4.Line(start, end, 3);
ReadOnlySpan<UnrealTraceResultV4> hits = traces.Execute();

if (hits[0].IsSuccess && hits[0].BlockingHit)
{
    using UnrealCommandBatch commands = data.BeginCommands(1);
    commands.Commands[0] = UnrealCommandV4.ApplyDamage(
        hits[0].HitActor,
        damage,
        damageCauser: data.Scope(gunHandle));
    commands.Commit();
}
```

The BlockBreaker comparison demo uses this exact architecture for its shot
trace, impact emitter, and damage while keeping load/spawn discovery on the
generated World service binding.
