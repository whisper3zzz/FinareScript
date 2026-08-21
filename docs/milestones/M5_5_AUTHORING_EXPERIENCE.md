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
- coordinate-free AI outline plus declaration/body semantic patch preview
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

The next M5.5 slice connects the prototype's behavior cards to its generated
Graph artifact. After that, live debug transport replaces the current pause and
locals fixture.

## Deliberate limits

The first slice does not bind Unreal reflection, cook assets, or generate
UObjects. Exposure metadata remains engine-neutral until M6 consumes it.
Arbitrary class declarations, constructors, events/delegates, inheritance,
generic state, object ownership, replication mechanics, and editor layout
collaboration remain later bounded additions.
