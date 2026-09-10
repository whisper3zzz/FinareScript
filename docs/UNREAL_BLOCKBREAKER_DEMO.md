# Deliverable BlockBreaker Demo

The BlockBreaker product slice can be prepared, cooked, archived, and verified
as a self-contained Win64 demo. The package contains the Unreal game, the
generated BlockBreaker managed assembly, synchronized Script Assets, the
Finare managed runtime, and the engine-bundled CoreCLR runtime.

## Build the deliverable

Close any BlockBreaker editor instance, then run:

```powershell
powershell -ExecutionPolicy Bypass `
  -File comparison/package-blockbreaker-demo.ps1 `
  -EngineRoot D:\UE_5.8 `
  -ComparisonRoot D:\Project\UEUnifiedScriptComparison
```

The command performs the complete product path:

1. copy the current workspace plugin into the Finare BlockBreaker project;
2. regenerate the nine-type, 40-member project API contract, C# wrappers, C++ thunks,
   binding coverage, and visual API-node catalog;
3. synchronize the Player and Rifle `UFinareAsset` packages and validate
   their compiled-chunk deployment transaction;
4. build the managed gameplay assembly, Release `.uschunk`, and native game;
5. cook, stage, pak, and archive the Win64 demo;
6. verify the packaged CoreCLR, managed runtime, gameplay chunk, launcher,
   and absence of Puerts/JavaScript/TypeScript package files;
7. run a 300-frame packaged gameplay gate with one shot, one hit, two completion
   events, nonzero ABI v3 calls, and zero legacy calls.

Outputs are written under `artifacts/demo/blockbreaker-<timestamp>`. Distribute
the complete `archive/Windows` directory; its sibling `Engine` and
`BlockBreaker` directories are both part of the Unreal package. The
`demo-delivery.json` report records hashes, package size, source identities, and
smoke metrics. At the root of `archive/Windows`, double-click
`Launch-BlockBreaker-Finare.cmd` to play.

## Controls

- `W/A/S/D`: move
- mouse: look
- left mouse: shoot

## What the demo proves

- World-owned CoreCLR startup and managed Actor lifecycle;
- generated, type-safe Unreal API bindings selected by a project contract;
- one verified Release chunk owns both Player and Rifle managed programs in the
  cooked package, with loose assembly/JSON retained only as a migration switch;
- exact 74/74 migration-inventory coverage with no unexplained gameplay bridge;
- Script Asset deployment without a source checkout beside the executable;
- resource/class loading and Actor spawn through the Binding control plane;
- one-boundary Batch Trace and command-buffered impact/damage side effects;
- next-frame command completion events and `LegacyCalls=0`.
- zero Puerts/TypeScript legacy roots in the project and packaged artifact.

The latest verified Development delivery is
`artifacts/demo/blockbreaker-20260828-152605`: 300 frames, one shot, one hit,
two command completions, 3,631 fast/CallFrame calls, and zero legacy calls. Its
report records both packaged Script Asset starts from
`BlockBreaker.Finare.uschunk`.

The package is the user-facing functional proof. Puerts comparison reports stay
separate because performance claims require matched workloads and environments;
they are not a prerequisite for launching the demo.
