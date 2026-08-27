# Script Asset and Declaration IR

## Purpose

`unifiedscript.script-asset/v0.1` is the engine-neutral declaration contract for
UnifiedScript-authored assets. It closes the type-level gap left by method-body
HIR and Graph: script state, defaults, exposure policy, and Visual function
signatures are now compiler artifacts rather than Blueprint-owned data.

Roslyn is the first producer, the visual editor and AI outline are consumers,
and the future Unreal adapter will map the same exposure metadata to generated
host properties. None of those consumers owns a second declaration model.

## C# source contract

An explicit Script Asset is a class or struct marked with a relative asset
path:

```csharp
[ScriptAsset("Combat/DamagePolicy")]
public sealed class DamagePolicy
{
    [Expose(InstanceEditable = true, Category = "Damage")]
    public int BaseDamage { get; set; } = 20;

    [Expose(SaveGame = true)]
    public int MaxDamage { get; set; } = 999;

    [Visual]
    public float ApplyDamage(int incomingDamage, bool critical)
    {
        // One method body, projected through the existing HIR/Graph path.
    }
}
```

For an explicit `[ScriptAsset]`, each directly declared non-static auto-property
with `get` plus `set` or `init` is script state. `[Expose]` is optional and adds
engine-neutral policy; it does not transfer declaration ownership to an engine.
A type containing a `[Visual]` method still receives an implicit asset record
with a qualified-name-derived path, preserving compatibility with existing
samples. An `[Expose]` member also opts its containing type into the declaration
pipeline.

Declaration IR v0.1 accepts booleans, signed/unsigned integers, finite
floating-point values, decimal, string, and enum state. Every state requires an
explicit compile-time constant default. Enum defaults serialize by member name,
not by underlying integer, so the artifact remains readable and rejects unnamed
values.

## Document model

Each document carries:

- exact schema version;
- source-compilation and semantic-configuration preconditions;
- deterministically ordered Script Assets;
- stable asset/state/function/parameter Semantic IDs;
- source spans for assets and state declarations;
- display and metadata type names;
- typed JSON defaults;
- `instanceEditable`, `saveGame`, `replicated`, and optional `category` policy;
- Visual function signatures linked to existing HIR method and parameter IDs.

`ScriptAssetJson` rejects unknown JSON members, wrong schema versions, empty
IDs, duplicate asset paths, duplicate state names/IDs, invalid source spans,
blank categories, and defaults whose JSON kind conflicts with the declared
state kind. Serialization normalizes asset, state, and function order before
writing, so editor and CI artifacts do not depend on source-document order.

## Declaration patch v0.2

`unifiedscript.declaration-patch/v0.2` is the typed edit contract for state and
UE Event Entry declarations. The reader migrates legacy v0.1 state-only patches
to v0.2. Every document carries the exact source-compilation and semantic-
configuration IDs from the Declaration IR artifact on which the edit was
authored. It supports four bounded operations:

- `addState` targets one Script Asset ID and supplies name, C# type spelling,
  state kind, typed default, and exposure policy;
- `updateState` targets one state ID, supplies the complete old declaration as
  a precondition, and supplies a complete replacement contract;
- `removeState` targets one state ID and supplies the same complete old-value
  precondition.
- `addUnrealEventHandler` targets one Script Asset ID and supplies the handler
  name, generated source-wrapper type, canonical UE delegate path, Host/Target
  source selection, optional source member, and ordered typed parameters.

The JSON reader rejects unknown fields, empty targets, duplicate update/remove
targets, no-op updates, blank categories, and defaults incompatible with their
declared kind. A patch never uses display order or canvas coordinates as an
identity or precondition.

`CSharpDeclarationPatchApplier` analyzes the original compilation, validates
all operations, annotates the exact Roslyn declarations, renames bound property
references when necessary, applies every addition/update/removal in memory, and
then runs the complete declaration/HIR/semantic analysis again. Source is
returned only if compilation and every postcondition succeed. A late failure
therefore rolls back the entire patch.

For Event Entries, the postcondition reprojects the generated method and proves
its `[UnrealEvent]` canonical path, source type, source selection, and parameter
types. Generated endpoint member names are not persisted as identity.

State renames require an input `.usids` sidecar. The applier installs an exact
identity pin on the rewritten property before reanalysis, so references receive
the new identifier while Declaration IR retains the original state Semantic ID.
Removals become normal sidecar tombstones; additions receive identities during
the same reconciliation pass.

Graph projection receives the lowering result's Roslyn-symbol-to-declaration
map. Identifier nodes that read Script Asset state retain their normal
`referencedSymbolId` and additionally expose the corresponding
`declarationId`. Visual and AI consumers can therefore join executable state
reads to their type/default/exposure contract by Semantic ID rather than by
mutable display name. Locals and parameters deliberately have no
`declarationId`.

## Diagnostics

The Roslyn declaration lowering boundary emits stable codes:

| Code | Meaning |
| --- | --- |
| `UAD1001` | Asset path is empty, absolute, or contains invalid segments. |
| `UAD1002` | State is not a supported instance auto-property. |
| `UAD1003` | Default is absent, non-constant, non-finite, or not JSON-representable. |
| `UAD1004` | State type is outside the scalar/enum v0.1 boundary. |
| `UAD1005` | Two source types claim the same asset path. |
| `UAD1006` | Exposure metadata contains a blank category. |
| `UAD2001` | Patch source-compilation precondition is stale. |
| `UAD2002` | Patch semantic-configuration precondition is stale. |
| `UAD2003` | Target asset/state or its exact source syntax is missing. |
| `UAD2004` | A state no longer matches its complete old-value precondition. |
| `UAD2005` | Replacement name, type syntax, or enum member is invalid. |
| `UAD2006` | A rename was requested without persistent identity. |
| `UAD2007` | Reanalysis did not satisfy an operation postcondition. |

Any declaration error fails the same `CSharpAnalysisResult` transaction used by
HIR and semantic binding. No partial asset document is emitted.

## Persistent identity

Without a sidecar, state IDs are deterministic symbol identities and survive
formatting, comments, and source-document reordering. With a `.usids` sidecar,
state properties join the existing structural reconciliation pass. Their
type/owner shape, neighboring token context, parent evidence, and anchor are
used to preserve the same random Semantic ID across safe renames; ambiguous
same-shape matches allocate a new ID rather than guessing. State entries use
`kind: "state"` and participate in the same tombstone rules as executable HIR
nodes.

Unreal uses the resulting state ID as the Property Bag descriptor GUID and
stores instance override membership separately from the values. This lets an
untouched instance inherit a changed declaration default while a compatible
override survives a declaration/graph/source rename and CoreCLR Reload. See
[STATE_OVERRIDE_MIGRATION.md](STATE_OVERRIDE_MIGRATION.md).

## CLI and prototype

`analyze` and `analyze-files` write `<base>.asset.json` next to HIR, semantics,
and Graph artifacts when `--out` is supplied:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Authoring/DamagePolicy.cs \
  --out artifacts/authoring
```

`emit` and `emit-files` also write the same asset document next to the DLL,
portable PDB, and source map. `CSharpProgramImage` and a loaded
`CoreClrExecutionSession` retain the validated document, so an editor or future
UE host does not need to reconstruct declarations from IL or maintain a second
runtime-side model.

Apply the checked-in declaration patch and atomically write the patched source
plus its revalidated Declaration IR:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-declaration-patch samples/Authoring/DamagePolicy.cs \
  --declaration-patch samples/Authoring/MakeMaxDamageEditable.declaration.patch.json \
  --out artifacts/authoring/DamagePolicy.cs \
  --out-asset artifacts/authoring/DamagePolicy.asset.json
```

When persistent identity is enabled, `--identity <input.usids>` and
`--out-identity <output.usids>` are a required pair. Source, asset, and optional
sidecar outputs are staged and committed as one filesystem transaction; the
command never patches the source in place.

The checked-in visual prototype consumes a byte-for-byte-equivalent generated
artifact at `prototype/visual-editor/data/damage-policy.asset.json`. The state
list, inspector, canonical declaration preview, AI declaration outline, and
Semantic ID display therefore share real compiler data. Its AI preview now
builds the strict patch document above from the loaded artifact, including
source/configuration IDs, target state ID, full old contract, and typed
replacement. The preview JSON can be given directly to the CLI. Applying it is
also a direct Workbench action: `debug-serve` exposes the same declaration
applier at `/api/authoring/declaration-patch`, prepares a complete candidate
analysis, writes source plus reconciled sidecar transactionally, and returns
the regenerated Declaration IR/Graph with the next CoreCLR generation.
Structure cards consume that live Graph. The host also materializes a
`ScriptAssetReceiver` from the asset ID and typed state-ID overrides, then
drives the existing CoreCLR loopback debugger. Declaration write-back and
execution therefore share one committed source/identity baseline.
