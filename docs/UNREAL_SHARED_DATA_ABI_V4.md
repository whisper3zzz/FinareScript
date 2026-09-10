# Unreal Shared Data ABI v4

Status: first active data-plane contract. It complements the synchronous call
ABI v3 and reverse-event ABI v4; it does not replace either contract.

## Purpose

The Shared Data ABI removes repeated managed/native calls for frame-coherent,
high-volume gameplay state. Native code owns two preallocated buffers per
`UWorld`; managed code acquires the published buffer as a borrowed
`ReadOnlySpan<T>` without copying it.

The first schema publishes Actor transforms. Resource loading and arbitrary
UFunction calls remain on the existing Binding control plane. Common gameplay
side effects and collision queries now also have an independent fixed-row
contract described by `UNREAL_GAMEPLAY_DATA_ABI_V4.md`.

All Shared and Gameplay rows now come from the same checked-in versioned layout
schema. Its revision may advance while the compatibility ID and frozen row IDs
remain stable; consumers accept an equal-or-newer compatible revision and
validate only size-present append-only suffixes. Telemetry getters also receive
the caller buffer size and copy only its compatible prefix.

## Lifetime and threading

- One `FFinareSharedDataPlane` belongs to one World subsystem.
- Native publishes the inactive buffer and then flips the active buffer.
- A view is valid only until the next World snapshot publication.
- Managed exposes the view as a `ref struct`, so it cannot be retained on the
  heap or across `await`.
- Acquisition currently runs on the Unreal Game Thread.
- World teardown detaches the transport before freeing buffers and invalidating
  snapshot-owned object handles.
- publication is capped at 65,536 rows; excess Actors increment a cumulative
  dropped-row counter rather than growing the cross-language contract without
  bound.

No `UObject*`, `FTransform`, `TArray`, allocator, or engine-private layout
crosses the boundary.

## Transform row

`FFinareTransformSnapshotEntryV4` is a fixed 96-byte row:

| Offset | Field | Shape |
| ---: | --- | --- |
| 0 | World host ID | `uint64` |
| 8 | generation-safe Actor handle | `uint64` |
| 16 | translation | 3 x `double` |
| 40 | quaternion rotation | 4 x `double` |
| 72 | scale | 3 x `double` |

The stable schema ID is `0x55535452414E5334`. Each row combines the existing
slot/generation object handle with its World host ID, preventing cross-World
aliasing without changing the call ABI's 64-bit handle.

## Snapshot view

The 56-byte view carries:

- ABI and view sizes;
- schema and World identities;
- monotonically increasing publication generation;
- borrowed entry pointer;
- count, capacity, and entry size;
- `BorrowedUntilNextPublish` lifetime flag.

Managed validates every field before constructing the span. An incompatible
schema, layout, World ID, count, pointer, or lifetime flag fails before memory
is exposed.

## Discovery

The append-only ABI v2 bootstrap now exposes a pointer to the independent
56-byte `FFinareSharedDataTransportV4` table after the call-frame and
event table pointers. Older managed hosts ignore the suffix. New hosts validate
the table and call `AcquireTransformSnapshot` once per gameplay phase.

## Managed API

```csharp
UnrealTransformSnapshot snapshot = Context.DataPlane.AcquireTransformSnapshot();
foreach (ref readonly UnrealTransformSnapshotEntryV4 actor in snapshot.Entries)
{
    UnrealTransform transform = actor.Transform;
}
```

This API performs no row copy and no steady-state allocation. Callers must not
retain the borrowed view past the current managed tick.

`GetSharedDataTelemetry()` exposes the schema revision/compatibility ID, World
ID, current publication generation, current/total published counts, cumulative
dropped rows, and the generated maximum. Runtime validation rejects impossible
counts or a telemetry generation older than the current borrowed view.

## Compatible extensions

The Gameplay Data ABI follows this rule by using independent command, event,
trace, and overlap schema IDs rather than changing the frozen Transform row.
Future velocity, gameplay-attribute, or tag snapshots must do the same and are
added only after representative profiling proves that per-call bindings are the
wrong boundary.
