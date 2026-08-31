# Transactional hot-reload reliability

`unifiedscript.reload-transaction/v0.1` is the observable receipt for a
UnifiedScript authoring or live-World reload. It turns compile, publication,
runtime migration, and cleanup into one ordered transaction instead of a set
of unrelated log lines.

## Transaction boundary

The authoring server starts the transaction and records `compile`,
`artifactPublication`, and `workbenchGenerationSwap`. When a matching live
World component exists, the same JSON receipt crosses the native boundary and
is extended with:

- `batchPreflight` on every matching running or stopped/editor instance before
  any instance crosses into runtime mutation;
- `debugDrain` and `configuration` before any runtime generation changes;
- `artifactValidation` and `stateMigration` while the old generation remains
  recoverable;
- managed `generationLoad`, `oldGenerationStop`, `candidateStart`,
  `eventRebinding`, `oldGenerationRetention`, `generationSwap`, and
  `oldContextRetirement` phases;
- native `batchPrepare`, `stateCommit`, `batchCommit`, `batchAbort`,
  `batchFinalize`, and `oldContextCollection` phases around the generation
swap.

The ordinary-source path starts the same receipt from Project Setup. Its
`dailyBuild` source records `dependencyAwareBuild` and `artifactPublication`
after `setup-unreal-project.ps1 -EditorAttached` succeeds. Editor-attached mode
can execute managed toolchain/gameplay/chunk and deterministic generation
stages, but it never attempts an invalidated native/reflection stage under a
loaded Editor. Those changes return `restartRequired` with the exact stage and
`restartEditor`; compatible output instead enters the same all-asset batch
coordinator below. Deferred deployment/startup stages retain their last
successful BuildGraph state and are not falsely accepted as newly completed.

Every phase carries a state, non-negative duration, and bounded detail. The
top-level transaction retains the Workbench `fromGeneration`,
`candidateGeneration`, and `activeGeneration`. A live World merge adds the
corresponding `worldFromGeneration`, `worldCandidateGeneration`, and
`worldActiveGeneration` so the two generation domains are never conflated.

The final `resources` snapshot exposes registered UObject handles, event
subscriptions, pending and queued work, generation-owned async resources,
semantic-debug activity, and `retiredLoadContextsAlive`. Slate parses this
contract rather than scraping log text: successful authoring and semantic
attach status show the transaction summary, while the diagnostics panel lists
each failed or rolled-back phase and its duration.

The Authoring Surface first collects every component that resolves to the exact
Script Asset (or, for legacy entry, the exact Surface path), sorts that set by
object path, and runs a non-mutating preflight. Configuration, lifecycle
ownership, artifact presence for live instances, declaration materialization,
and stable-ID override preservation are checked while every active generation,
property bag, debug session, and component receipt remains untouched. A single
`failed` or `restartRequired` candidate blocks the whole batch; the receipt
reports `batchPreflight`, and no compatible sibling is allowed to advance early.
Stopped/editor instances participate in the same barrier but a successful
commit refreshes declaration state without starting managed code.

After preflight, every running instance enters managed prepare before any
instance publishes a generation pointer. Prepare stops but retains the previous
generation, loads the candidate, applies migrated native state for candidate
`OnStart`, and binds candidate events. Stopped/editor instances prepare the
same state/declaration transaction without starting managed code. A failure in
any prepare aborts every earlier candidate, restarts each retained previous
generation, rebinds its events, and restores the exact native state bag,
override IDs, active asset/configuration identity, and lifecycle settings.

Only a completely prepared batch enters commit. Commit swaps managed and native
generation/state pointers without script callbacks or old-context retirement.
A commit-boundary failure aborts both already committed and merely prepared
members back to the retained generation. After every member commits, finalize
retires the retained old contexts and collects them. Finalize is cleanup rather
than a publication decision: a cleanup failure keeps the already committed
candidate active and reports the retained resource in the transaction receipt.

## Rollback guarantees

A compile failure leaves the published artifact and active generation
unchanged. An artifact publication failure restores the previous files. A
candidate-load failure keeps the old generation active. If failure happens
after the old script stops, the previous generation is restarted, its events
are rebound, and the captured instance-state bag is restored.

Reload also clears frame-boundary pause/step state and drains an active semantic
debug worker before retiring a generation. Old-context collection is forced
after both successful and failed runtime reload attempts and its remaining
collectible AssemblyLoadContext count is recorded in the receipt. This GC is a
reload reliability gate, not a per-frame operation.

## Reproducible soak

Run the focused UE 5.8.1 gate with:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/run-unreal-lifecycle-stress.ps1 -EngineRoot D:\UE_5.8
```

The `unifiedscript.unreal-lifecycle-stress/v0.4` payload defaults to eight travel
cycles, eight PIE cycles, four concurrent Worlds, 16 successful reloads, and
16 intentionally failed reloads. Each failed attempt proves that the old
generation, migrated state, timer, and one reflected Host event subscription
remain active. Each successful attempt broadcasts the same event and proves
that only the current generation receives it.

The same test accepts bounded command-line scale inputs. The M11.1 two-process
runner in `tools/run-unreal-reliability-soak.ps1` executes the larger matrix in
both editor and dedicated-server process modes and adds UObject/GC churn,
async/command/event/trace/overlap saturation, memory budgets, and worker-queue
shutdown invariants. Every World destruction must also emit the complete
ordered shutdown breadcrumb sequence into the bounded process ring. See
`docs/RELIABILITY_SOAK.md`.

The gate accepts the report only when registered-object, delegate, pending-work,
managed-World, native-context, worker-dispatch, owned-resource, and
stale-debug-session deltas are zero and `retiredLoadContextsAlive` is zero. It
also requires exact capacity rejection telemetry and final physical-memory
growth within the selected budget. The deterministic-shutdown gate requires at
least one verified sequence, zero sequence failures, a 64-entry breadcrumb
bound, and counted oldest-entry eviction under pressure. The intentionally
missing type diagnostics during the failed half of the soak are expected test
evidence.

## Current boundary

The deterministic preflight plus two-phase prepare/commit/abort coordinator
closes mixed-generation publication for one Workbench reload batch, including a
late candidate `OnStart` or event-rebinding failure. Finalize cleanup is
observable but intentionally does not roll back an already published complete
batch. M13.2D-3 still needs to route the ordinary source/Graph Build and Play
loop through this same batch receipt automatically. Per-node gameplay timing,
bridge-call attribution, remote debug framing, and a frame timeline remain
owned by M9.3.
