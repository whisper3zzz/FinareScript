# Large-project scale gate

M11.1 adds one reproducible UE 5.8.1 gate for the product paths that grow with
a real project: reflected API Contracts, generated bindings, C# script sets,
compiled chunks, editor startup, and Script Asset churn. The gate is synthetic
so its size and single-input mutations stay deterministic, but it uses the
normal `ue-bindings`, `emit`, `.uschunk`, and Unreal deployment commandlet
paths rather than mock serializers.

## Default workload

`tools/run-unreal-scale-gate.ps1` creates and verifies:

- 256 reflected Unreal types with 16 properties each, for 4,096 accepted
  Contract members, 4,096 generated CallFrame bindings, and 4,096 generated
  node properties;
- three independent Contract generations whose C#, C++, node-catalog, and
  coverage outputs must be byte-for-byte deterministic;
- a one-member Tooltip edit whose managed Blueprint metadata and node catalog
  change while native thunks and coverage classification remain stable;
- 256 real C# source files, each containing a `[ScriptAsset]`, `[Expose]`,
  `[Visual]`, and `IUnrealManagedScript` implementation;
- a cold Release emit and one-source default-value edit; the compilation ID,
  managed assembly, and `.uschunk` must all change;
- cold-process Unreal validation, creation of 256 `.uasset` files, and a
  one-entry incremental manifest that updates exactly one asset while the
  total remains 256.

The runner snapshots `DefaultEngine.ini` and `DefaultInput.ini`, uses a unique
`/Game/UnifiedScriptScale_<timestamp>` package root, and removes only that
resolved directory in `finally`. The report fails if cleanup is incomplete.

The script-set fixture exposed a real pre-gate defect: Roslyn's fully-qualified
display format rendered every property as only its member name, so `Value` in
different Script Assets shared one semantic reference ID. Roslyn declaration
IDs now scope these references to their complete containing type. A two-source
emitter regression test protects both unique declaration references and unique
HIR node identities.

## Running the gate

Use the installed UE 5.8.1 toolchain:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/run-unreal-scale-gate.ps1 `
  -EngineRoot D:\UE_5.8
```

The defaults enforce these machine-local budgets:

| Phase | Budget |
| --- | ---: |
| Contract generation and single-member rebuild | 15 s each |
| Cold and single-script Release emit | 60 s each |
| Editor startup plus 256-entry validation | 30 s |
| Initial asset creation and one-entry churn | 120 s each |

The timings include process startup. They are regression budgets for this host,
not a claim that another machine will reproduce the same absolute values.

## Verified UE 5.8.1 result

The 2026-08-28 full run under
`artifacts/ue/scale/20260828_163657/scale-gate.json` passed every
`unifiedscript.unreal-scale-gate/v0.1` gate:

| Measurement | Result |
| --- | ---: |
| Contract size | 256 types / 4,096 members |
| Deterministic generation median | 1,141.3522 ms |
| Single-member Contract rebuild | 965.5414 ms |
| Script set | 256 assets |
| Cold Release emit | 5,398.7625 ms |
| Single-script Release emit | 5,123.4274 ms |
| Editor startup plus validation | 12,023.0525 ms |
| Initial 256-asset sync | 12,539.5622 ms |
| One-asset churn | 12,443.4847 ms |

The generated managed assembly was 78,848 bytes and the Release chunk was
141,249 bytes. Contract generation rejected no members and used no reflection
fallback. The Unreal phase created 256 assets, updated exactly one on the
incremental manifest, retained 256 files, and left no scale content directory.
