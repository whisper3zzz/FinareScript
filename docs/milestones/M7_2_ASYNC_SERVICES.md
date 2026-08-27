# M7.2 Services, Async, and Lifecycle

Status: **complete**.

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
- generation-owned async resource handles released on reload, EndPlay, failed
  start, and World teardown.
- process-wide native context/service/outstanding-work diagnostics plus managed
  retired collectible-context diagnostics.

## Acceptance evidence

- managed suite: 448/448 passed;
- repository validator: 44/44 invariants passed;
- product coverage: 98 members, 96 native paths, zero rejected;
- UE 5.8.1: bootstrap and final editor builds passed;
- Automation: 12/12 passed, including
  `UnifiedScript.Unreal.AsyncService.DelayLoadCancelAndLifecycle`;
- onboarding: `artifacts/onboarding/m7-2-lifecycle-20260827.json`, passed in
  110,938 ms with deterministic regeneration, deployment, CoreCLR startup, and
  all 12 Automation tests.

## Completed lifecycle stress matrix

`UnifiedScript.Unreal.WorldHost.LifecycleStress` exercises real lifecycle
transitions rather than only single-operation correctness:

1. deletes a managed Actor target while a class load and long timer are owned;
2. performs eight non-seamless travel-style old/new World handoffs with active
   timer, completion, resource, and retained-context state;
3. repeats eight PIE World create/stop/restart cycles;
4. runs four Worlds concurrently with the same owner/generation and eight
   colliding request IDs in every World;
5. rolls a live managed Actor through sixteen collectible generations.

The machine-readable report at `artifacts/ue/lifecycle-stress.json` validates
the fixed v0.1 matrix and records zero stale completions, pending-request delta,
managed-World delta, native-context delta, owned-resource delta, and live
retired collectible contexts. Reproduce it with:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/run-unreal-lifecycle-stress.ps1 -EngineRoot D:\UE_5.8
```

Completion of this matrix advances the RoadMap cursor to M7.3.
