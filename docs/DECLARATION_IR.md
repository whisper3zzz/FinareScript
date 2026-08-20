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

The checked-in visual prototype consumes a byte-for-byte-equivalent generated
artifact at `prototype/visual-editor/data/damage-policy.asset.json`. The state
list, inspector, canonical declaration preview, AI declaration outline, and
Semantic ID display therefore share real compiler data. Browser edits are
still local UI state; typed declaration patches and transactional Roslyn
write-back are the next M5.5 slice. Structure cards and the debug animation are
also still fixtures until the real Graph/debug transports are connected.
