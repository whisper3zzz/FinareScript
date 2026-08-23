# Authoring Surface v0.1

`unifiedscript.authoring-surface/v0.1` is the versioned, engine-neutral contract
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
- `AuthoringFunctionSurface` records with signature, root region, regions, and
  promoted actions;
- `AuthoringRegionSurface` records preserving function/block/if/then/else/loop
  hierarchy, direct action ownership, implicit regions, and an optional anchor;
- `AuthoringActionSurface` records with a compact title and expression summary,
  source span, Graph kind, category, region, optional probeable debug target, declaration
  references, and edit capabilities;
- typed `AuthoringEditCapability` records that translate directly to an
  existing Semantic Patch operation.

Every action is owned by exactly one direct region. Every child region names a
matching parent. Every edit has a stable capability ID and a real expression
target ID. `AuthoringSurfaceJson` rejects dangling, duplicate, multiply owned,
or incompletely typed records before a UI sees them.

## Promotion and expression compaction

The projector promotes Graph entry nodes and nodes with control ports. This
captures executable steps, decisions, loops, calls with effects, transfers, and
returns without making every identifier, literal, or binary operator a card.
Pure dataflow is formatted recursively into the owning action summary. The
visual surface therefore remains a structured behavior document rather than a
one-node-per-source-token diagram.

If and loop anchors become region headers. Their child regions become named
lanes such as `TRUE`, `FALSE`, `INITIALIZE`, `BODY`, `INCREMENT`, and `CLEANUP`.
Nested regions remain nested; the UI does not infer this hierarchy from edges
or screen coordinates.

## Semantic edit capabilities

v0.1 publishes two bounded editor capabilities:

- `replaceLiteral` includes the target expression ID, current/expected text,
  and source location;
- `changeOperator` includes the target expression ID, current operator,
  compatible options, result type ID, left/operand type ID, and optional right
  operand type ID.

The Workbench serializes those fields as Semantic Patch v0.19. The ordinary
Roslyn patch path still enforces operator families, type preconditions, source
identity, semantic configuration, localized syntax replacement, complete
reanalysis, and transactional CoreCLR reload. The Surface does not weaken or
duplicate those checks.

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

## Boundary for Unreal Engine

A future UE adapter should consume this contract rather than parse C#, rebuild
Graph regions, or own Blueprint declarations. UE-specific code may translate
reflection types, host the shared runtime domain, paint Slate widgets, and
materialize UObject state. It must preserve the Surface/Declaration/HIR
Semantic IDs and submit the same declaration or semantic patches. This keeps
text, visual, AI, CoreCLR development execution, and eventual UE execution on
one semantic path and one VM per target runtime.
