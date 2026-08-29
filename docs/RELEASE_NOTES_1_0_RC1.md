# UnifiedScript 1.0 RC1 release notes

Version: `1.0.0-rc.1`
Channel: release candidate
Verified target: Unreal Engine 5.8.1, Win64, .NET 10

## What is ready

- A source-distributed Unreal plugin, managed runtime, CLI, schemas, templates,
  Workbench, and documentation are packaged into one deterministic SDK archive.
- C# and the native Slate graph are lossless projections of the supported
  authoring model, with transactional semantic edits, Undo/Redo, compile and
  reload rollback, live debugging, and node-level gameplay profiling.
- Unreal reflection generates type-safe C++, C#, visual-node, coverage, World
  service, async, shared snapshot, command/event, Trace, Overlap, and gameplay
  data-plane contracts.
- Script Assets load deterministic `.uschunk` programs through a World-scoped
  CoreCLR host with generation-safe object handles, async ownership, hot reload,
  diagnostics, and failure containment.
- BlockBreaker is fully migrated without Puerts payload in the UnifiedScript
  target and has matched packaged gameplay and performance evidence.
- General Puerts projects can be scanned, planned, migrated incrementally under
  a single-runtime lifecycle ownership policy, and verified with exact
  side-by-side observable evidence.

## Compatibility and packaging

The declared package rows are Editor Development, Game Development, Game
Shipping, and SDK-installed Game Development on Win64 with UE 5.8.1. Packaged
games stage their plugin-local CoreCLR closure and are tested with external
.NET roots deliberately poisoned. Other platforms and UE versions are not
claimed by this release candidate.

The SDK is source-distributed for the native plugin. A consumer's selected UE
5.8 engine builds the native modules; engine-version-specific `Binaries` and
`Intermediate` directories are excluded from the archive. Every shipped file
is size/hash listed and installation is transactional.

## Upgrade notes

- Run the packaged `upgrade-plan` before replacing an existing SDK.
- Reflection v0.1-v0.3 and API Contract v0.1 have deterministic direct rewrite
  routes to their current revisions.
- Regenerate derived bindings/surfaces together from their authoritative
  contracts. Rebuild `.uschunk` and matched ABI outputs; do not patch either in
  place.
- Stop a live runtime before changing a Puerts/UnifiedScript lifecycle owner.

See [Versioning and upgrades](VERSIONING_AND_UPGRADES.md) for the exact action
model and [UnifiedScript documentation](DOCUMENTATION.md) for the complete
release-owned guide set.

## Known limitations

- Packaged-runtime support is currently declared only for UE 5.8.1 on Win64.
- Puerts coexistence generation does not rewrite project SpawnActor, asset, or
  gameplay selection routes and does not itself prove behavior parity.
- Visual editing intentionally targets the documented restricted C# profile;
  source-only methods remain explicit opaque boundaries.
- Networking/server sample coverage and the generated API/diagnostic browser
  remain M12.2 follow-up slices and are not claimed by RC1 yet.
