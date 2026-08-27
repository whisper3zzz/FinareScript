# Authoring Surface v0.3

`unifiedscript.authoring-surface/v0.3` is the versioned, engine-neutral contract
between the compiler pipeline and an authoring UI. It prevents a browser, a UE
editor panel, or an AI client from re-implementing Graph interpretation and
from accumulating sample-specific adapters.

## Inputs and output

`AuthoringSurfaceProjector` consumes two artifacts from the same successful
analysis:

- Script Asset / Declaration IR v0.1 for asset paths, states, exposure policy,
  Visual signatures, and declaration identities;
- Graph v0.12 for expression dataflow, direct region ownership, structured
  control flow, source spans, and debug-target identities.

It emits one deterministic document containing all Script Assets and their
Visual functions. The Surface is a projection; it does not become another
program representation and it never executes code. HIR remains the executable
body contract, Graph remains the exact control/data projection, and C# remains
the canonical text.

## Shape

An `AuthoringSurfaceDocument` owns:

- compilation and semantic-configuration preconditions;
- `AuthoringAssetSurface` records with state declarations and functions;
- `AuthoringFunctionSurface` records with signature, root region, regions,
  complete Graph nodes, and compiler-owned edges;
- `AuthoringRegionSurface` records preserving function/block/if/then/else/loop
  hierarchy, direct action ownership, the real source block/node ID, exact
  direct-statement order, implicit regions, and an optional anchor;
- typed function-parameter records that publish a semantic type ID when the
  parameter participates in the projected graph;
- `AuthoringActionSurface` records for every Graph node with a compact title and
  expression summary, source span, Graph kind, category, region, optional
  probeable debug target, declaration references, edit capabilities, and ports;
- `AuthoringPortSurface` records with direction, control/data kind, and known
  semantic type identity/display name;
- `AuthoringEdgeSurface` records preserving every Graph control and data edge;
- typed `AuthoringEditCapability` records that translate directly to an
  existing Semantic Patch operation.

Every action is owned by exactly one direct region. Every child region names a
matching parent. Every edit has a stable capability ID and a real expression
target ID. `AuthoringSurfaceJson` rejects dangling, duplicate, multiply owned,
or incompletely typed records before a UI sees them.

## Full graph projection and presentation compaction

The projector publishes every Graph node and edge. Entry, executable steps,
decisions, loops, calls, transfers, values, identifiers, literals, and
operators therefore retain their exact compiler-owned endpoints. A UI can show
the complete Blueprint-style graph or compact pure dataflow into an owning
action summary without losing the underlying pins and wires.

If and loop anchors become region headers. Their child regions become named
lanes such as `TRUE`, `FALSE`, `INITIALIZE`, `BODY`, `INCREMENT`, and `CLEANUP`.
Nested regions remain nested; the UI does not infer this hierarchy from edges
or screen coordinates.

Every edge must resolve an output port and an input port with the same
control/data kind. Known data types must agree, and a data input may have only
one owner. The strict reader rejects dangling endpoints, wrong directions,
kind/type mismatches, and duplicate data inputs before rendering.

## Semantic edit capabilities

v0.3 retains two inline editor capabilities:

- `replaceLiteral` includes the target expression ID, current/expected text,
  and source location;
- `changeOperator` includes the target expression ID, current operator,
  compatible options, result type ID, left/operand type ID, and optional right
  operand type ID.

The Workbench serializes those fields as Semantic Patch v0.21. The ordinary
Roslyn patch path still enforces operator families, type preconditions, source
identity, semantic configuration, localized syntax replacement, complete
reanalysis, and transactional CoreCLR reload. The Surface does not weaken or
duplicate those checks.

v0.21 also defines `reconnectDataEdge` for one exact existing data input. The
operation carries the old edge ID, old and new endpoints, target port, and
semantic type as preconditions. Its first implementation copies only a stable
pure scalar expression through the existing safe expression-edit pipeline;
state reads, effects, incompatible types, stale edges, self-cycles, and scope or
dominance violations are rejected before source commit. `disconnectDataEdge`
replaces one exact connected input with the bounded scalar default for its
declared type instead of leaving invalid C# behind.

Surface v0.3 publishes enough structural preconditions for a client to author
whole statements without treating screen coordinates as semantics. The UE
client inserts locals, `if`, `while`, `do/while`, bounded `for`, nearest-region
`break`/`continue`, and typed `return` statements against a real source block
and exact sibling gap; Delete carries the same parent/sibling preconditions.
Switch-section regions expose their HIR body block as `sourceNodeId`, while case
labels remain visible actions outside the editable statement order. Reusing a
computed output emits one atomic patch-plan that first introduces a local
temporary and then reconnects the other expression to the produced local. The
temporary name includes the complete producer semantic-ID payload, so repeated
planning is byte-stable and distinct producers do not collide through a short
prefix. The host exposes that transaction at
`POST /api/authoring/semantic-patch-plan`.

## HTTP and CLI delivery

`unifiedscript analyze ... --out <directory>` now writes
`<name>.surface.json` beside HIR, semantic, Graph, and Declaration artifacts.
The local authoring host publishes workspace v0.2, which contains Declaration
IR, Graph, Surface, canonical source, revision, and CoreCLR generation from one
committed snapshot.

The Workbench consumes the Surface for asset/function navigation, cards,
regions, inspector editors, state-reference labels, source selection, AI
outline, and semantic breakpoints. Its checked-in fallback includes the same
Surface JSON. No sample class, function, state, or API call is encoded in
`app.js`.

## Unreal Engine adapter

The UE adapter consumes this contract rather than parsing C#, rebuilding Graph
regions, or owning Blueprint declarations. Its transient `UEdGraph` allocates
real execution/data pins and links directly from Surface v0.3. Dragging a
compatible output onto an occupied data input submits a direct rewire or an
automatic shared-value patch plan to the Workbench. Alt-click submits a typed
default disconnection. Right-click creates structured statement nodes, and
Delete removes only statement nodes with exact block/sibling preconditions. UE
keeps compiler-owned nodes and wires unchanged until the server commits;
success regenerates C#, Declaration IR, Graph, Surface, DLL, and PDB, then
reloads matching components. Rejection leaves the canvas unchanged.

The node menu reads the loaded Script Asset's contract-generated API node
catalog, falling back to the checked-in reflection fixture for the repository
demo. Readable properties and callable instance methods appear when the active Visual function
exposes a matching typed receiver and parameters. Selecting one emits a
structured `memberAccess` or `memberInvocation` initializer, so normal
compilation and semantic validation remain the final authority. The built-in
math entry can likewise create a typed binary-expression local from compatible
function parameters. Screen positions load from and save to the adjacent
`.usgraph` sidecar independently of program semantics.

Reflection v0.2 delegates appear under `Unreal Events`. Selecting one is a
declaration-level operation rather than a statement insertion: the client posts
`addUnrealEventHandler` through declaration-patch v0.2, and Roslyn adds a
strongly typed `[Visual] [UnrealEvent(...)]` method to the active Script Asset.
The next compiler projection renders that method as a red `unrealEventEntry`
with one control continuation and typed delegate-payload outputs. Canonical UE
event path and source type are preserved in HIR metadata; generated C# endpoint
names are deliberately not used as semantic identity.

`FUnifiedScriptGraphPresentation` is a read-only display policy above that
contract. It keeps every Surface record addressable while folding connected
identifier/literal leaves into consumer labels, hiding unused data outputs,
renaming IR pins to author-facing labels, and deciding which complex actions
need a short summary. Full kind/category/source/type/semantic-ID diagnostics
remain available through tooltips and Details. Presentation compaction never
changes IDs, source spans, edit targets, ports, edges, or patch payloads.

For a concrete UE round trip, the checked-in Demo's `RewirePreview` function
contains two pure parameter expressions. Dragging the `*` output to the
`Declare incremented` value input now produces one shared local and connects
both declarations to it. `UEApiPreview` supplies a typed
`UnifiedScriptComponent` receiver plus `left`/`right` parameters, so the
reflection palette can insert another generated-wrapper `Add` call, read a
reflected component property, or create a typed integer Add Expression and
publish the result immediately through the same compile/reload transaction.
`OnManagedSignal` demonstrates the reverse direction: its Event Entry receives
an `int` plus generation-safe object handle through native ABI v4, updates C#
state, and rebinds exactly once after reload.
