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
  boundary call;
- sphere and box overlap queries use a second fixed-row batch without exposing
  `FOverlapResult` or engine container memory;
- generated gameplay modules publish stable fixed-container snapshots and
  queue typed event rows through generic schema-ID channels instead of adding
  another handwritten bridge facade.

The Binding control plane remains authoritative for resource discovery,
arbitrary reflected functions, properties, delegates, and uncommon signatures.
Gameplay Data is an optimized execution lane, not a replacement type system.

## World phase scheduler

Each `UUnifiedScriptWorldSubsystem` owns one
`FUnifiedScriptGameplayDataPlane` and binds only to its own `UWorld`:

```text
OnWorldPreActorTick
  -> NativePreActorTick
  -> publish Transform snapshot, prior command events, and queued generated events
  -> ManagedTick: scripts may reserve/commit commands and execute trace/overlap batches

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

All operations currently require the Unreal Game Thread. Command, trace, and
overlap reservation, commit, and execution require `ManagedTick` and reject the
wrong phase before exposing writable memory or executing side effects.
Read-only phase inspection, telemetry, and event acquisition remain available
while the context and World are alive.

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

## Generated snapshot and event channels

Schema revision 4 appends one generic 64-byte `GeneratedDataViewV1` contract.
Native gameplay modules call the World subsystem with a schema ID, trivially
copyable row size, and typed `TConstArrayView<T>`:

- `PublishGeneratedSnapshotRows` copies into the inactive per-schema buffer and
  publishes immediately;
- `QueueGeneratedEventRows` appends to a bounded pending buffer; `BeginFrame`
  flips it into visibility and publishes an empty buffer on the following frame
  when no new events arrive.

Both paths are double-buffered, World-owned, limited to 256 channels, 65,536
rows per channel, and 16 MiB per published channel. A schema ID fixes its row
size for the lifetime of that World host. A 64-bit non-zero publication ID,
cumulative dropped-row count, schema ID, World host ID, row count/size, and
borrow-kind flags make each view inspectable without exposing Unreal allocator
or container layouts. Teardown clears every channel before the object registry
is invalidated.

Managed code uses
`AcquireGeneratedSnapshot<T>(schemaId)` or
`AcquireGeneratedEvents<T>(schemaId)`. Both return a stack-only
`UnrealGeneratedDataSnapshot<T>` backed by `ReadOnlySpan<T>`. The row must be
`unmanaged`; ABI version, schema, row size, World, publication, capacity, byte
bound, pointer, and snapshot/event flag are checked before the span is created.
The span must not survive the next native publish. Direct generated binding is
the fallback for low-volume, mutable, or unsupported shapes.

The checked-in revision-4 examples are a 104-byte
`FixedValueCollectionSnapshotV1` and a 48-byte
`BufferedGameplayEventV1`. They exercise the Array/Map and delegate/event
families that lost in the M10.1 matched suite, while the channel implementation
remains independent of either row.

Project workloads can replace raw schema-ID calls with a generated extension
descriptor. The extension generator freezes the extension key, core ABI and
compatibility requirement, schema fingerprint, row layouts, channel kind,
publication phase, and tighter per-channel row/byte limits into matched C++ and
C# outputs. The World-owned registry rejects collisions and incompatible
publication before the existing generic ABI is reached, then clears all
registration state during World shutdown. See
[Generated data-plane extensions](DATA_PLANE_EXTENSIONS.md).

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

## Batch Overlap

Schema revision 2 adds up to 65,536 fixed 160-byte overlap requests. A request
selects `Sphere` or `Box`, center, extent/rotation, collision channel, and zero
to four ignored World-scoped Actors. One boundary call returns 48-byte rows
containing request sequence, status, `HasOverlap`, and the first valid Actor and
component handles. A valid query with no overlap is a successful row without
the flag.

Only one overlap batch may be active. Its request span ends at execute/cancel;
result rows and registered hit handles remain borrowed until the next overlap
batch or World shutdown. Trace and overlap reservations are independent, so a
script may run both during one managed phase.

## Bounds, order, and telemetry

The versioned schema fixes logical maximums at 65,536 commands, 4,096 events,
65,536 traces, 65,536 overlaps, 65,536 generated snapshot/event rows, 256
generated channels, and 16 MiB per generated channel. Reservations exceeding a
limit fail before memory exposure and increment their cumulative overflow
counter. Event production beyond its bound drops only the excess row and
increments the event overflow counter.

The first 120 bytes of the telemetry row remain the revision-1 prefix. Revision
2 appends overlap counters/capacity and rounds the row to 144 bytes without
moving any earlier field. Revision 3 appends cumulative count/nanosecond pairs
for `NativePreActorTick`, `ManagedTick`, and `NativePostActorTick`, producing a
192-byte row while preserving both earlier prefixes. It also reports
committed/executed command totals, executed trace/overlap totals, last
committed/executed sequence IDs, last event publication frame, current phase,
and every advertised maximum. Sequence IDs are monotonic within a World and
commands execute in reservation/row order. Profiler consumers calculate
per-frame phase cost from cumulative deltas, so reading telemetry does not
reset shared World counters.

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
independent `FUnifiedScriptGameplayDataTransportV4` table. The original 104-byte
prefix remains readable, revision 1 telemetry/capacity extends it to 136 bytes,
schema revision 2 appends overlap discovery at 176 bytes, and revision 4 appends
generated event discovery at 200 bytes. The Shared Data table similarly grows
from 80 to 104 bytes for generated snapshots. Managed code
requires the stable compatibility ID, accepts a revision at least as new as its
schema, validates each present size-guarded suffix, and ignores unknown future
bytes. Telemetry getters receive the caller's destination size and copy only the
compatible prefix, so a newer native telemetry suffix cannot overwrite an older
managed structure.

| Schema | ID | Entry/view size |
| --- | --- | ---: |
| command | `0x5553434D44423401` | 224 / 72 |
| event | `0x555345564E543401` | 88 / 64 |
| trace request | `0x5553545245513401` | 184 / 64 |
| trace result | `0x5553545245533401` | 168 / 64 |
| overlap request | `0x55534F5652455101` | 160 / 64 |
| overlap result | `0x55534F5652455301` | 48 / 64 |
| fixed-value collection snapshot | `0x5553464958434F01` | 104 / 64 |
| buffered gameplay event | `0x5553425546455601` | 48 / 64 |

The checked-in `samples/Unreal/UEUnifiedScriptRuntime.data-plane.json` is the
single source for these rows, views, enums, capacities, offsets, defaults,
schema revision, compatibility ID, and fingerprint. `ue-data-plane` emits the
C++ and C# layouts deterministically; native `static_assert`s, managed
`Marshal.SizeOf`, exact-output tests, and
`tools/generate-unreal-data-plane.ps1 -Check` reject drift. Extensions use new
schema IDs and the separate `ue-data-plane-extension` contract rather than
mutating a frozen row or transport table.

## Managed example

```csharp
UnrealWorldDataPlane data = Context.DataPlane;

using UnrealTraceBatch traces = data.BeginTraceBatch(1);
traces.Requests[0] = UnrealTraceRequestV4.Line(start, end, 3);
ReadOnlySpan<UnrealTraceResultV4> hits = traces.Execute();

using UnrealOverlapBatch overlaps = data.BeginOverlapBatch(1);
overlaps.Requests[0] = UnrealOverlapRequestV1.Sphere(center, 100.0, 3);
ReadOnlySpan<UnrealOverlapResultV1> nearby = overlaps.Execute();

UnrealGeneratedDataSnapshot<UnrealFixedValueCollectionSnapshotV1> attributes =
    data.AcquireGeneratedSnapshot<UnrealFixedValueCollectionSnapshotV1>(
        UnrealSharedDataAbiV4.FixedValueCollectionSchemaId);
UnrealGeneratedDataSnapshot<UnrealBufferedGameplayEventV1> gameplayEvents =
    data.AcquireGeneratedEvents<UnrealBufferedGameplayEventV1>(
        UnrealGameplayDataAbiV4.BufferedGameplayEventSchemaId);

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

The matched live-CoreCLR profile (`tools/run-unreal-data-plane-profile.ps1`)
measures 1,024 operations across three median samples in the same World. On the
2026-08-28 UE 5.8.1 run, movement submission was 0.186x scalar binding, Batch
Trace was 0.010x matched per-call trace, and a generated fixed-container
snapshot was 0.000050x matched direct Array+Map binding (1.270 versus
25,371.973 ns/row). Generated event iteration measured 0.586 ns/row. All
published data-plane loops reported zero managed allocation. This evidence
routes stable high-volume containers and events through generated channels;
ordinary attributes and uncommon calls remain type-safe bindings, and spawn
remains hybrid (binding for discovery, command row for repeated execution).

The BlockBreaker comparison demo uses this architecture for its shot trace,
impact emitter, and damage while keeping resource/class discovery on the
generated World service binding.
