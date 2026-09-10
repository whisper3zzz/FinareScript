# M7.3 Schema-generated Gameplay Data Plane

Status: **complete**.

## Delivered slice

- one strict, versioned JSON schema for Shared and Gameplay Data layouts;
- deterministic `ue-data-plane` C++/C# generation with computed alignment,
  offsets, sizes, defaults, compatibility ID, revision, and fingerprint;
- exact checked-in-output and unknown-field/size-drift tests plus a standalone
  `-Check` regeneration gate;
- generated Transform snapshot, command, completion event, trace, overlap,
  borrowed-view, and telemetry layouts;
- bounded snapshot/command/event/trace/overlap capacity and cumulative overflow
  counters;
- monotonic command ordering, executed totals, publication generation/frame,
  and generated maximum telemetry;
- append-only 104/136/176-byte Gameplay transport discovery with compatible
  revision and size-present suffix validation;
- fixed-row Batch Sphere/Box Overlap with World-scoped Actor/component results
  and explicit borrowed-handle release;
- matched live-CoreCLR profiling that chooses the boundary by measured workload
  rather than moving every reflected API into shared memory.

## Routing decision

The checked-in profile uses one UE 5.8.1 World, the same managed generation,
1,024 operations, three samples, and medians. The 2026-08-27 accepted run
reported:

| Workload | Binding | Data plane | Ratio |
| --- | ---: | ---: | ---: |
| movement submit vs scalar binding | 331.934 ns | 66.504 ns | 0.200x |
| line query | 53,225.781 ns | 403.027 ns | 0.0076x |

The fixed-row movement/query loops allocated zero managed bytes. The product
route is therefore:

- movement, overlap, high-volume query: data plane;
- generic attributes: generated type-safe binding, because Unreal exposes no
  universal gameplay-attribute semantics worth inventing in the plugin;
- spawn: hybrid, with class/resource discovery on bindings and repeated spawn
  execution available as command rows.

Reproduce the profile and machine-readable gate with:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/run-unreal-data-plane-profile.ps1 -EngineRoot D:\UE_5.8
```

## Acceptance evidence

- managed suite: 452/452 passed;
- repository validator: 47/47 invariants passed;
- UE 5.8.1 editor build: passed;
- `Finare.Unreal.GameplayData.CommandEventAndBatchTrace`: passed with
  command/event/trace/overlap bounds and telemetry;
- `Finare.Unreal.Performance.DataPlaneWorkloadProfile`: passed;
- profile report: `artifacts/ue/data-plane-profile.json`, status `passed`.
- full onboarding: `artifacts/onboarding/m7-3-20260827.json`, 13/13 Automation
  tests passed with deterministic regeneration, both UE builds, deployment, and
  CoreCLR startup in 73,660 ms.
- SDK packaging: `0.1.0-m7.3-alpha.1` passed and includes the versioned
  data-plane schema in its hashed manifest.

M7.3 closes the generic data-plane foundation. The RoadMap cursor advances to
M7.4, where the complete BlockBreaker runtime is inventoried and migrated with
no unexplained manual bridge.

## M10.2 extension

Schema revision 4 keeps this foundation append-only and adds generic generated
snapshot/event channels. Shared discovery extends from 80 to 104 bytes and
Gameplay discovery from 176 to 200 bytes; old prefixes remain fixed. Native
modules publish any schema-ID trivially-copyable row set through the World
subsystem, while managed code receives stack-only checked `ReadOnlySpan<T>`
views. The v0.3 profile adds matched Array+Map binding, fixed-container snapshot,
and buffered-event workloads without changing the frozen M7.3 evidence above.
