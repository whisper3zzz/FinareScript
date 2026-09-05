# Script Asset and Declaration IR

## Purpose

`unifiedscript.script-asset/v0.1` is the engine-neutral declaration contract for
UnifiedScript-authored assets. It closes the type-level gap left by method-body
HIR and Graph: script state, defaults, exposure policy, and Visual function
signatures are now compiler artifacts rather than Blueprint-owned data.

Roslyn is the first producer, the visual editor and AI outline are consumers,
and the future Unreal adapter will map the same exposure metadata to generated
host properties. None of those consumers owns a second declaration model.

## Container and Soft Reference function declarations

Blueprint-callable `IReadOnlyList<T>`, `IReadOnlySet<T>` and
`IReadOnlyDictionary<TKey, TValue>` carry `array`, `set` and `map` value-type
descriptors. Their ordered `typeArguments` retain the CLR metadata name and
optional nested Unreal enum/struct descriptor of each component. Containers have
an empty `unrealTypePath`, native family name (`TArray`/`TSet`/`TMap`) and no struct
`fields`. Strict readers reject inconsistent kinds, arity or metadata names.
Existing enum/struct documents remain unchanged when no type arguments exist.

`UnrealSoftObjectHandle` and `UnrealSoftClassHandle` remain built-in path values,
projected as soft object/class pins without loading their referenced asset.
Input, return, `ref` and `out` use the same ABI v3 cell/arena transport. Container
descriptors are bounded by the call arena, and all outputs decode into temporary
storage before publication to the caller. Generated native struct-field paths
use reserved `$element`, `$key` and `$value` segments inside containers.

K2 does not permit direct nested container pins; sets and map keys must support
Unreal hashing. Such unsupported signatures fail during class preflight. The
DemoLibrary `ExchangeArray`, `ExchangeSet`, `ExchangeMap`, `ExchangeSoftObject`
and `ExchangeSoftClass` examples exercise all directions through generated
functions. See the live roadmap for the remaining validation scope; package
reload within Editor Automation does not itself prove cooked execution.

## Replication and network function declarations

State exposure optionally carries `replicationCondition`: `none` (the omitted
default), `initialOnly`, `ownerOnly`, `skipOwner`, or `initialOrOwner`. A non-default
condition requires `replicated: true`. These are portable audience policies,
not serialized Unreal flag values. State patches preserve the condition.

A function can carry `rpc: { "target": "server", "reliable": true }`, with
`server`, `client` or `multicast` direction, or `repNotifyProperty: "Score"`.
RPC/RepNotify functions must be public instance `void` methods with input-only
parameters and one binding owner; event/input/delegate/interface bindings cannot
be combined with them. RepNotify names one existing replicated state and accepts
zero arguments or exactly that state's previous-value type. Each state has at
most one notification handler. Invalid source contracts report `UAD1011`;
invalid JSON is rejected before publication.

The Unreal adapter requires an Actor/ActorComponent Script Class and rejects
RPC payloads outside its current 0-4 common-value input surface. Network flags
and stable notification identities persist in the generated class, including
the native previous-value RepNotify signature that ordinary Blueprint compilation
does not retain. This extends declaration metadata without changing the existing
native call-frame ABI. Older consumers must be upgraded with the SDK before
using network declarations; an older reader is not a networking fallback.

See [networked gameplay](UNREAL_GAMEPLAY_RECIPES.md#replicated-state-and-rpc)
for explicit send semantics and current runtime evidence.

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
floating-point values, decimal, string, enum, `UnrealName`, `UnrealText`,
`UnrealObjectHandle`, `UnrealClassHandle`, `UnrealSoftObjectHandle`,
`UnrealSoftClassHandle`, and the reviewed `UnrealVector`,
`UnrealRotator` and `UnrealTransform` struct state. Collection state reuses the
function container descriptors as described below. Every state requires an
explicit deterministic default. The value
may come from the property initializer or from a direct, unconditional simple
assignment in the type's explicit parameterless constructor; an
expression-bodied constructor is also accepted. Constructor assignments follow
source execution order, so the last direct assignment overrides earlier
assignments and the property initializer. Branches, loops, compound writes,
runtime expressions, and writes deferred into local functions or lambdas are
rejected with `UAD1003` rather than guessed.

Common Unreal defaults use a bounded source form: `UnrealName` and `UnrealText`
accept an empty/default value or one constant string; `UnrealVector` accepts
default/`Zero` or three finite constant components. Object and class handles
accept only their zero/null form because their nonzero values identify one
native registry generation and are not durable asset references. Ordinary CLR
enum defaults serialize by member name and reject unnamed values. Generated
Unreal enum wrappers instead use a decimal string and an exact reflected enum
descriptor, as described below. Struct state carries its exact Unreal type path,
native name, and field schema in `unrealType`; collection state instead carries
its container descriptor and typed arguments. Other scalar and reference state
cannot claim that extra identity.

## Document model

Each document carries:

- exact schema version;
- source-compilation and semantic-configuration preconditions;
- deterministically ordered Script Assets;
- stable asset/state/function/parameter Semantic IDs;
- source spans for assets and state declarations;
- display and metadata type names;
- typed JSON defaults;
- exact reflected type identity for generated Unreal enum and reviewed struct state;
- `instanceEditable`, `saveGame`, `replicated`, and optional `category` policy;
- Visual function signatures linked to existing HIR method and parameter IDs;
- explicit `Input`, `Output`, or `InputOutput` direction on every function
  parameter. Ordinary C# parameters map to `Input`, `out` maps to `Output`, and
  `ref` maps to `InputOutput`; `in` is outside the reviewed Blueprint matrix.

`ScriptAssetJson` rejects unknown JSON members, wrong schema versions, empty
IDs, duplicate asset paths, duplicate state names/IDs, invalid source spans,
blank categories, and defaults whose JSON kind conflicts with the declared
state kind. Serialization normalizes asset, state, and function order before
writing, so editor and CI artifacts do not depend on source-document order.
Input-action, delegate, and interface contracts remain input-only. Function
directions are part of canonical Declaration IR JSON and persistent generated
class bindings; they do not add function signatures to the state-focused runtime
declaration binary.

The managed function ABI v3 consumes input and input-output values in source
parameter order, then returns one ordered result sequence: a non-void return
value first, followed by every input-output and output parameter in source
order. Consumers must bind by stable parameter name because Unreal may reorder
output properties while constructing a UFunction.

The compiled declaration payload retains major version 1. Binary v1.1 introduced
the optional reflected type descriptor plus typed
`FName`, `FText`, and `FVector` defaults. The decoder continues to accept v1.0
scalar documents; v1.1 readers validate nesting, field order, exact FVector
identity, finite components, and the null-only object/class default contract
before native state is mutated.

### Common value state and binary v1.2

The M13.5 common-state batch extends state to generated Unreal enum wrappers,
`UnrealRotator` and `UnrealTransform`. It keeps the existing state IDs and the
distinction between a declaration default and a user override. The Common State
Actor test verifies native defaults, source updates, compatible overrides,
managed Tick, reload and World cleanup using the emitted Demo chunk.

Generated enum state carries `unrealType.kind = enum` with its exact reflected
enum path and native name. Its JSON default is an invariant decimal string,
preserving the wrapper's signed 64-bit value without a JSON floating-point
conversion. Ordinary CLR enum state retains its existing member-name string
and has no reflected enum descriptor. These representations must not be confused
or materialized as an arbitrary integer or editable string for a reflected enum.

Generated named enum constants expose their numeric value through
`UnrealEnumConstantAttribute` metadata.
The compiler can therefore lower a named default from a referenced bindings DLL
without executing its getter or requiring the generated source. The owning enum
identity remains authoritative; a numeric constant does not imply that a value
belongs to another UEnum.

Rotator defaults contain exactly `pitch`, `yaw` and `roll`. Transform defaults
contain `rotation` (`x`, `y`, `z`, `w`), `translation` (`x`, `y`, `z`) and
`scale3D` (`x`, `y`, `z`). Components are finite constants; a Transform requires
a valid normalized quaternion. `UnrealTransform.Identity` has unit rotation and
scale, whereas C# `default(UnrealTransform)` has a zero quaternion and is not a
substitute for Identity. Invalid defaults fail before native state changes.
Named constructor arguments bind to their parameter names, regardless of source
argument order.

The binary declaration extension uses major 1, minor 2 for these new forms,
preserving existing value-kind numbers and decoding of minor 0 and 1 payloads.
Legacy state shapes retain their existing encoding where possible. Both the
runtime Property Bag and generated Blueprint class must resolve the same exact
type and default; reload must retain compatible explicit overrides. Reflected
defaults must name a declared UEnum value. Blueprint enum fields use K2 byte
storage and reject defaults outside 0 through 255; the runtime Property Bag
retains the reflected underlying type for enum classes. This does not widen
Blueprint's native enum storage.

The default chunk entry list includes declared Script Asset types as well as HIR
types. A gameplay class with only lifecycle overrides does not need a dummy
`Visual` method to load. An explicitly supplied entry-type list remains
authoritative.

### Asset-reference state and binary v1.3

`UnrealSoftObjectHandle` and `UnrealSoftClassHandle` state use distinct
`softObject` and `softClass` kinds. Their defaults are persistent path strings,
not World registry handles. `default`, `new()` and an empty-path constructor
produce the empty JSON string. A nonempty default may use a constant path in
the initializer or the same supported unconditional constructor assignment.

Paths use absolute Unreal object syntax, such as
`/Game/Props/Crate.Crate` or `/Script/Engine.StaticMeshActor`. Plugin mount paths
and valid soft-object subobject suffixes are accepted. Relative paths, operating
system paths, export-text class prefixes, malformed delimiters and illegal
characters are rejected before state mutation. The engine-neutral declaration
layer validates syntax without looking up mount points, resolving an object or
loading its package. The package prefix and asset name each fit Unreal's
1023-character name limit. A syntactically valid missing asset remains a valid
stored reference; an explicit load reports whether it can be used.

The binary extension uses major 1, minor 3 when soft-reference state is present.
It appends state kinds and default kinds without renumbering prior values;
older payloads keep their existing version and encoding. Soft references do
not masquerade as arbitrary struct state and carry no synthetic struct
descriptor. Object and class references remain distinct through JSON, binary,
native properties, managed hydration and patch preconditions.

The Unreal adapter materializes native soft-object/soft-class properties and
Property Bag entries. It compares stored paths when reconciling defaults and
overrides; Blueprint's `None` import text represents the empty reference.
Materialization and state synchronization do not eagerly load the referenced
asset. Compatible explicit overrides retain their semantic identity across
source-default changes and reload.

### Collection state and binary v1.4

Collection state uses the same `array`, `set` and `map` value-type descriptors
as function parameters. The public declarations are `IReadOnlyList<T>`,
`IReadOnlySet<T>` and `IReadOnlyDictionary<TKey, TValue>`; `typeArguments` retain
the exact element, key and value identities. A native container is a value with
an explicit empty form, so a null/default interface reference is not an empty
collection default. Direct nested containers remain outside the Blueprint
property contract, and set elements/map keys must support native hashing.

Defaults accept parameterless `List<T>`, `HashSet<T>` and
`Dictionary<TKey, TValue>` construction with fixed collection initializers.
List collection expressions may contain fixed elements. Arbitrary constructors,
custom comparers and spread elements are not evaluated as declaration defaults.
The reviewed leaves are bool, byte, int, long, float, double, string, Name/Text,
generated Unreal enums, object/class handles, the three common transform value
types and soft-object/soft-class handles. This does not extend collection state
to every scalar type accepted by the engine-neutral declaration model.

Array and Set defaults are JSON arrays. A Map default is an array of objects
with exactly `key` and `value` members, allowing keys other than strings without
coercing their identity into a JSON object member name. Each entry uses its
declared leaf's existing default representation, including invariant enum
strings, reviewed struct objects and persistent soft-reference paths. Set
elements and Map keys must be unique under the native property's equality
rules. Array order is significant; Set and Map iteration order does not define
whether a default or override changed.

Collection overrides retain a whole property value. During compatible default
updates on the synchronized generated class, an instance collection that differs
from its previous CDO is preserved as a whole; one that matches the previous CDO
inherits the new default. Unreal's per-entry Map delta must not silently merge
new defaults into an overridden configuration. This follows the Property Bag's
existing override unit, the stable state ID.

Integer leaves remain JSON numbers. Readers retain the number token until
integer range validation and conversion, so adjacent `long` keys above 2^53
remain distinct. Fractional and out-of-range tokens are rejected; converting
through `double` or accepting a quoted number would change the contract.

The binary extension uses major 1, minor 4 for collection state. It appends
state kinds Array/Set/Map (12/13/14) and default kinds Array/Set/Map (14/15/16),
preserving existing numeric kinds and earlier payload versions. Collection
defaults retain their typed children; readers validate the container descriptor
and every child before publishing native state. An invalid late element must
not leave an earlier element written to the active bag or instance.
The reused value-type descriptor kinds keep their existing values: Enum 0,
Struct 1, Array 2, Set 3 and Map 4. Descriptor presence is encoded separately.

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

Common struct defaults remain JSON objects in the Authoring Surface and its
old-value preconditions. The C# applier renders Rotator/Transform constructors
from those objects. A generated Unreal enum's decimal-string default becomes
`new EnumWrapper(valueL)`; a CLR enum member remains `EnumType.Member`.

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
| `UAD1003` | Default is absent, non-constant, non-finite, not JSON-representable, or written through a non-deterministic constructor shape. |
| `UAD1004` | State type is outside the reviewed scalar/enum/common-Unreal property matrix. |
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
nodes. Editing only a constructor default changes the typed default value but
does not replace that state identity.

Unreal uses the resulting state ID as the Property Bag descriptor GUID and
stores instance override membership separately from the values. This lets an
untouched instance inherit a changed declaration default while a compatible
override survives a declaration/graph/source rename and CoreCLR Reload. See
[STATE_OVERRIDE_MIGRATION.md](STATE_OVERRIDE_MIGRATION.md).

For Unreal targets the reviewed common types materialize as real `FNameProperty`,
`FTextProperty`, `FObjectProperty`, `FClassProperty`, and `FStructProperty`
(`FVector`) fields on the generated Blueprint class. The same declaration
default initializes the native Property Bag and generated CDO, while managed
startup/reload hydrates and flushes the corresponding strongly typed C# value.

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
