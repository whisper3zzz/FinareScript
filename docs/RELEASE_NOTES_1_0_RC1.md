# UnifiedScript 1.0 RC1 release notes

## Typed AI semantic proposals

AI tools can now package Patch Plan v0.21 as a strict, content-identified
proposal, preview it through the same Roslyn and stable-ID semantic diff used by
human edits, and commit only the exact proposal bound to the accepted preview
token. The CLI, loopback host, browser Workbench, and native Slate review path
share the versioned envelope; no model runtime is bundled or trusted as a
commit authority.

Runtime Insight now joins a bounded UE gameplay profile with the current
Authoring Surface and generated API route catalog. Browser and native Slate
show ranked semantic hotspots, exact C# spans, residual managed cost, and
DirectThunk/CallFrame/data-plane/reflection attribution in the same authoring
workflow. Route catalog examples remain explicitly distinct from measured
per-member calls.

Project-owned high-volume workloads can now generate matched C++ rows, C# rows,
typed snapshot/event descriptors, and an authoring catalog without changing
the frozen Gameplay/Shared Data ABI v4. A per-World native registry enforces
extension identity, fingerprint, kind, publication phase, row/byte limits, and
lifecycle reset before using the existing zero-copy channels. BlockBreaker now
proves the route with target snapshots and impact events in the live CoreCLR
profile.

Version: `1.0.0-rc.1`
Channel: release candidate
Verified target: Unreal Engine 5.8.1, Win64, .NET 10

## Release acceptance

One release-readiness command now binds every manifest-declared gate to an
exact provider and produces a single `unifiedscript.release-readiness/v0.1`
verdict from one clean Git revision. It includes the complete matched
UnifiedScript/Puerts workload budget; microbenchmarks and stale child reports
cannot substitute for the release performance gate. Dirty-source modes are
diagnostic-only and cannot emit the strict `passed` verdict.

The frozen clean-revision run at `5be7dde` passed all 15 required gates. It
includes 619 managed tests, 92 repository invariants, 19 UE Automation tests,
four declared compatibility/package rows, five executable sample categories,
three project templates, and all 54 scheduled UnifiedScript/Puerts captures.
The authoritative local evidence is
`artifacts/release-readiness/m12-final-5be7dde/release-readiness.json`.

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
- The same Workbenches expose graph/source runtime hotspots and Unreal boundary
  cost through the versioned, read-only Runtime Insight contract; stale
  semantic nodes remain visible without unsafe navigation.
- Generated data-plane extensions let projects add bounded typed snapshot and
  event workloads while preserving the core ABI v4 compatibility identity.

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
- The qualified weighted performance result is not a claim that every path is
  faster. In the frozen suite, scalar bridge calls were 1.087774x Puerts and the
  resource/spawn flow was 1.239108x Puerts. The dedicated reload workload was
  9.108265x Puerts and is excluded from the broad weighted claim because the
  CoreCLR generation replacement and Puerts module-cache mechanisms differ.
- BlockBreaker gameplay medians favored UnifiedScript (frame ratio 0.937981,
  bridge ratio 0.415071), while its diagnostic reload field was 62.205199 ms
  versus 0.131797 ms for Puerts. Reload latency and experience remain explicit
  post-RC1 optimization work.
