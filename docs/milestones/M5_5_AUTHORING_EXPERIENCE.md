# M5.5 Script Asset and Authoring Experience

## Outcome

State declarations and behavior bodies are text/visual/AI-authorable through
one semantic model, with no required Blueprint-only variables and without
turning source lines into a spaghetti graph.

## Why this precedes more runtime work

M5.1 through M5.4 prove one CoreCLR execution/debug backend. The largest
remaining product risk is no longer whether code can run; it is whether the
authoring model actually removes the Blueprint and AI bottlenecks. Async
execution, native managed-debug traps, and UE binding should not harden an asset
model that still omits state declarations.

## Planned vertical slice

- backend-neutral Script Asset and Declaration IR
- stable semantic identities for type-level state and function signatures
- supported type/default/exposure binding from C# properties or fields
- canonical Declaration IR JSON and strict validation
- typed declaration semantic patches with source/configuration preconditions
- transactional Roslyn application and identity-sidecar reconciliation
- actual Graph/Declaration artifacts consumed by the visual editor
- declaration list, inspector, and Structure/Split/Code synchronization
- coordinate-free AI outline plus declaration/body semantic patch submission
- existing CoreCLR debug pause painted onto real visual statement cards
- a sample asset with zero Blueprint-only declarations

## M5.5.1 completed — Declaration IR foundation

The first connected slice is now implemented:

1. `[ScriptAsset]` defines an engine-neutral asset path in canonical C#.
2. Script-asset auto-properties lower to state declarations; `[Expose]` carries
   instance-editable, save-game, replicated, and category policy.
3. Scalar and enum types plus explicit constant defaults are bound through
   Roslyn and emitted as typed JSON values.
4. Script Assets, state, Visual functions, and parameters share existing
   Semantic IDs and semantic-configuration/source-compilation preconditions.
5. Strict `unifiedscript.script-asset/v0.1` JSON rejects unknown fields,
   duplicates, invalid spans/paths, and default/type mismatches, then emits a
   deterministic order.
6. State properties join optional `.usids` reconciliation, preserving identity
   across formatting and safe renames while retaining ambiguity/tombstone rules.
7. `analyze`/`analyze-files --out` emit `.asset.json` next to HIR, semantics,
   and Graph artifacts; `emit`/`emit-files` retain and write the same document
   with the executable program image.
8. `samples/Authoring/DamagePolicy.cs` owns all gameplay declarations in text;
   no Blueprint-only variable is required.
9. The visual prototype loads the generated Declaration IR and uses it for the
   state list, inspector, canonical text, AI declaration outline, and IDs. It
   visibly falls back only when the artifact cannot be fetched.
10. The full 347-test suite, zero-warning build, formatting check, repository
    validator, exact analyze/emit artifact comparison, and browser interaction
    regression pass.

## M5.5.2 completed — Declaration semantic patch and write-back

The second connected slice is now implemented:

1. Strict `unifiedscript.declaration-patch/v0.1` JSON carries source and
   semantic-configuration preconditions.
2. Typed `addState`, `updateState`, and `removeState` operations address asset
   or state Semantic IDs rather than visual coordinates.
3. Update/remove operations include the complete old state contract, rejecting
   concurrent name, type, default, or exposure changes.
4. Roslyn applies declaration changes in memory and renames all bound property
   references across the compilation.
5. Validated C# identifier/type/default/exposure rendering supports every
   scalar and enum boundary already accepted by Declaration IR v0.1.
6. State renames require `.usids` and use an exact identity pin, retaining the
   original Semantic ID after source and reference renaming.
7. The entire patched compilation is reanalyzed; compile, declaration,
   semantic, identity, or postcondition failure returns no patched request.
8. `apply-declaration-patch` atomically writes patched source, revalidated
   `.asset.json`, and an optional updated `.usids` sidecar.
9. `MakeMaxDamageEditable.declaration.patch.json` is a checked-in executable
   sample against the generated authoring artifact.
10. The prototype's AI view generates that real typed protocol dynamically,
    including baseline IDs and full expected/replacement contracts.
11. Desktop and 760 px browser checks pass with scroll-contained JSON and no
    console errors.
12. The full 360-test suite, zero-warning build, formatting check, repository
    validator, and CLI source/asset transaction pass.

## M5.5.3 completed — Real Graph compact visual projection

The third connected slice is now implemented:

1. The prototype loads checked-in Graph v0.12, Declaration IR v0.1, and exact
   C# source artifacts produced from the same compilation.
2. A strict browser-side gate verifies source/configuration/function identity,
   unique nodes/regions, edge endpoints, region ownership, and state-reference
   declaration identity before rendering.
3. The DamagePolicy Graph's 17 fine-grained nodes project to six promoted
   semantic cards: entry, local declaration, decision region, branch mutation,
   call-owning assignment, and return.
4. Identifier, literal, binary, member-access, and invocation nodes remain
   recursively reconstructable inline expressions instead of becoming a
   Blueprint-style wire/card explosion.
5. The `if` card retains the real branch node and region IDs, including its
   explicit `then` region and implicit empty `else` region.
6. Every promoted card exposes its exact Graph Semantic ID and source line.
   Selecting the clamp assignment highlights line 27 of the real source.
7. The assignment card keeps its Graph expression ID, and its generated
   `debugTargetId` identifies the enclosing probeable HIR statement. The nested
   `Math.Clamp` invocation keeps a third inline expression ID for tracing.
8. Graph identifiers bound to Script Asset state now carry a
   `declarationId`, joining behavior references to Declaration IR without name
   matching.
9. Inspector state renames update canonical source, inline behavior references,
   and the coordinate-free AI outline while retaining identity and visibly
   entering `PATCH PENDING`.
10. The repository validator checks the three checked-in artifacts together,
    including compact statement counts and all state declaration links.
11. Browser regression verifies the six real IDs, three Declaration IR links,
    exact source selection, cross-view rename, AI outline, and assignment-level
    debug pause.
12. The full 361-test suite, zero-warning build, formatting check, and ten
    repository invariants pass.

## M5.5.4 completed — Live CoreCLR visual debugging

The fourth connected slice is now implemented:

1. `ScriptAssetReceiver` is an engine-neutral instance invocation contract
   carrying the asset ID and typed state-ID/JSON overrides.
2. The collectible CoreCLR host constructs a validated Script Asset receiver,
   applies overrides through its declared state properties, and invokes its
   synchronous instance Visual method without introducing another VM.
3. Missing, foreign, duplicate, or static-method receivers fail with stable
   `UVR1008` invocation diagnostics.
4. Top-level effect Graph nodes now carry `debugTargetId`, explicitly joining
   an assignment expression card to its probeable enclosing HIR statement.
5. `debug-serve` compiles the real DamagePolicy debug image once and serves the
   workbench plus `/api/debug/health`, `start`, `continue`, and `step-over` from
   one loopback origin.
6. Every debug request and response retains generation, source-compilation,
   semantic-configuration, method, execution, pause, statement, and breakpoint
   identities.
7. The workbench rejects a debug generation whose source/configuration IDs do
   not match its loaded Graph and Declaration artifacts.
8. Run executes `DamagePolicy.ApplyDamage(12, true)` against the current
   Declaration state values and pauses the real CoreCLR thread at line 27.
9. The pause strip renders the real top frame, IL offset, generation, and live
   locals: `incomingDamage=12`, `critical=true`, and `scaled=480`.
10. Step Over resumes that thread and pauses on the real return statement at
    line 28; Continue completes with the canonical CoreCLR result `480`.
11. Timed execution animation and hard-coded locals/return fixtures have been
    removed; an offline bridge produces an explicit error rather than a fake
    debug session.
12. Runtime, graph-identity, CLI, repository, and browser regressions cover the
    complete receiver → pause → locals → step → result path.
13. The full 363-test suite, zero-warning build, formatting check, and eleven
    repository invariants pass.

## M5.5.5 completed — Transactional authoring host and hot reload

The final M5.5 slice closes the write-back loop:

1. `CSharpAuthoringWorkspace` owns one immutable committed snapshot containing
   its request, analysis, Script Asset document, HIR-derived Graph, and revision.
2. Declaration Patch v0.1 and Semantic Patch v0.19 produce candidate snapshots;
   the current workspace is unchanged until an explicit revision-checked commit.
3. The first reconciliation result from an empty identity sidecar is attached to
   the in-memory baseline before any edit, so the first rename cannot drift IDs.
4. `GET /api/authoring/workspace` returns source, Declaration IR, Graph, revision,
   and the exact CoreCLR generation from one same-origin response.
5. `POST /api/authoring/declaration-patch` accepts add/update/remove operations
   directly from the Workbench instead of requiring a manual CLI handoff.
6. `POST /api/authoring/semantic-patch` accepts the existing body-edit protocol;
   the first UI operation edits the Clamp literal by Semantic ID rather than by
   source line or visual coordinate.
7. A successful mutation reloads the same `CoreClrDebugLoopbackTransport`, writes
   source plus `.usids` through one rollback-capable file transaction, then
   publishes the next workspace revision and CLR generation.
8. Reload failure leaves files untouched; file-commit failure reloads the prior
   CoreCLR request. A patch failure never mutates the committed snapshot.
9. Before writing, the host compares source and sidecar bytes with its committed
   baseline. External editor changes return `UAW1002` instead of being overwritten.
10. The Workbench loads the live workspace before checked-in fallback artifacts,
    exposes `SAVE PATCH`/Ctrl+S, and renders explicit pending, rejected, external-
    conflict, synchronized-revision, and reloaded-generation states.
11. State creation, update, rename, exposure changes, and removal share one
    Declaration Patch transaction. AI preview can apply that same document to
    source and CoreCLR directly.
12. Selection and active breakpoint intent are captured by Semantic ID before
    reload and restored against the regenerated Declaration IR and Graph.
13. Browser regression proves a persistent state rename, transactional default
    update, literal body edit, generation-4 breakpoint hit, and changed CoreCLR
    return value (`480` before Clamp, `500` after it).
14. Conflict regression proves an external source edit is rejected while the
    Workbench remains dirty, and restart regression proves the renamed state ID
    plus behavior edit survive through the generated sidecar and source.
15. The full 369-test suite, zero-warning build, formatting check, twelve
    repository invariants, artifact reproduction, and browser interaction gate
    pass.

M5.5 is complete. Native traps, async execution, remote framing, and the UE
object/lifetime adapter remain later runtime/M6 work; they do not alter the
authoring contracts proven here.

## Deliberate limits

The first slice does not bind Unreal reflection, cook assets, or generate
UObjects. Exposure metadata remains engine-neutral until M6 consumes it.
Arbitrary class declarations, constructors, events/delegates, inheritance,
generic state, object ownership, replication mechanics, and editor layout
collaboration remain later bounded additions.
