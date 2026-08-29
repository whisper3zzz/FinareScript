# UnifiedScript 1.0 RC1 release notes

## Typed AI semantic proposals

AI tools can now package Patch Plan v0.21 as a strict, content-identified
proposal, preview it through the same Roslyn and stable-ID semantic diff used by
human edits, and commit only the exact proposal bound to the accepted preview
token. The CLI, loopback host, browser Workbench, and native Slate review path
share the versioned envelope; no model runtime is bundled or trusted as a
commit authority.

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
- One versioned sample catalog and aggregate acceptance runner cover minimal
  onboarding, packaged BlockBreaker gameplay, dedicated-server World lifecycle,
  gameplay data-plane routes, and Visual/C# round-trip equivalence.
- Three versioned project templates cover minimal lifecycle, structured Visual
  gameplay policy, and World data-plane starting points. The public scaffold
  records their exact capabilities and non-claims.
- The Workbench provides a searchable API/diagnostic browser generated from the
  same Contract, node catalog, and coverage input as managed/native bindings.
- Native Slate and browser authoring previews expose versioned stable-ID
  semantic Graph diffs beside exact C# source. The CLI can diff Graph artifacts
  and three-way merge disjoint Visual-function changes without Blueprint
  serialization or layout noise, while conflicting same-function edits remain
  explicit and non-writing.

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
- The networking/server sample proves dedicated-server World host lifecycle; it
  does not yet claim client/server gameplay replication or a packaged
  `WindowsServer` target.
- Project templates are reviewed starting points; they do not generate maps,
  replicated authority policy, project-native classes, or game-specific assets.
