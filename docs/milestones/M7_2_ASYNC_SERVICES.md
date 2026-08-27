# M7.2 Services, Async, and Lifecycle

Status: **services and async contract complete; lifecycle stress remains**.

## Delivered slice

- Async Service ABI v5 with fixed native/managed request, completion, view, and
  append-only transport layouts.
- World-owned Delay and asynchronous object/class loading.
- per-request cancellation, game-time timeout, script-generation cancellation,
  retained-context teardown rejection, and no raw UObject pointer crossing.
- `UnrealObjectScript` helpers for Delay, next Tick, object load, and class load.
- generated World services for input, component lookup, World time,
  spawn/destroy, synchronous resource loading, and visibility trace.
- selected `FHitResult` field schema with generated C++, C#, and visual
  Make/Break nodes.

## Acceptance evidence

- managed suite: 448/448 passed;
- repository validator: 43/43 invariants passed;
- product coverage: 98 members, 96 native paths, zero rejected;
- UE 5.8.1: bootstrap and final editor builds passed;
- Automation: 11/11 passed, including
  `UnifiedScript.Unreal.AsyncService.DelayLoadCancelAndLifecycle`;
- onboarding: `artifacts/onboarding/m7-2-async-20260827.json`, passed in
  100,111 ms with deterministic regeneration and deployment.

## Remaining M7.2 stress slice

The next increment must exercise real lifecycle transitions rather than only
single-operation correctness:

1. delete request targets while loads and timers are pending;
2. travel between levels while the old World owns active requests;
3. repeat PIE create/stop/restart cycles;
4. run multiple Worlds with colliding instance/request patterns;
5. roll generations repeatedly while asserting zero stale completions, native
   pending requests, managed script entries, retained bridge contexts, and
   collectible load contexts.

Completion of that matrix advances the RoadMap cursor to M7.3.
