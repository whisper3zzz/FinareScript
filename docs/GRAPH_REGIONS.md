# Graph Regions

## Purpose

`unifiedscript.graph/v0.3` adds explicit structured regions to the semantic
Graph. Nodes and control/data edges still describe computation; regions describe
lexical ownership and structured control-flow nesting without inferring it from
screen coordinates or reverse-engineering edge shapes.

This is the first M4 contract. It deliberately projects existing HIR constructs
before adding loops or new patch operations, so future CFG and reducibility
analysis can depend on a stable hierarchy.

## Function contract

Every `GraphFunction` contains:

- `rootRegionId`, identifying its unique `function` region;
- `regions`, the complete region set for that function;
- the existing deterministic `nodes` and `edges` collections.

The root region has no parent, uses the function ID as its `sourceNodeId`, and
has exactly one `block` child for the method body. Each non-root region appears
exactly once in its declared parent's ordered `childRegionIds`.

Each region records:

```json
{
  "id": "us_...",
  "kind": "then",
  "parentRegionId": "us_...",
  "sourceNodeId": "us_...",
  "span": {
    "filePath": "samples/Regions/EarlyReturn.cs",
    "start": { "line": 11, "column": 9 },
    "end": { "line": 14, "column": 10 }
  },
  "isImplicit": false,
  "nodeIds": ["us_..."],
  "childRegionIds": []
}
```

`sourceNodeId` points to the HIR construct that owns the region. A region keeps
its own source span because empty branches contain no Graph node from which an
editor could recover that location.

## Region kinds

The v0.3 schema defines five kinds:

| Kind | Meaning | Parent |
| --- | --- | --- |
| `function` | One visual method | none |
| `block` | Method body or nested lexical block | structured enclosing region |
| `if` | Branch condition and branch control node | block/branch region |
| `then` | True branch body | corresponding `if` |
| `else` | False branch body or implicit false path | corresponding `if` |

An `if` region directly owns the condition expression nodes and its `branch`
node. Its `childRegionIds` must contain exactly the `then` region followed by the
`else` region. Nodes inside each body belong directly to that branch region;
nested `if` or lexical blocks appear as child regions instead of flattening
their nodes into the parent.

The projector always emits an `else` region because the Graph branch has an
explicit false control port. If C# omitted the `else` block, the region is empty
and `isImplicit` is `true`. Only an `else` region may be implicit. This preserves
the difference between an absent `else` and an explicitly authored empty block.

## Direct node ownership

Every Graph node belongs to exactly one direct region through `nodeIds`:

- the entry node belongs to the function region;
- method-body statements and their expressions belong to the body block;
- branch conditions and branch nodes belong to the `if` region;
- branch-body statements and expressions belong to `then` or `else`;
- nodes inside a nested structured construct belong to its child region.

Region node order is projection/evaluation order and is therefore preserved.
The top-level `nodes`, `edges`, and `regions` arrays are normalized by semantic
ID for byte-deterministic JSON; `nodeIds` and `childRegionIds` retain their
semantic order.

`GraphJson` rejects missing or duplicate node ownership, unknown node/child
references, inconsistent parent links, unreachable/cyclic regions, secondary
function roots, invalid implicit regions, and malformed `if` children. Older
Graph schema documents must be regenerated; no v0.2 migration guesses a region
hierarchy.

## Control flow and layout boundaries

Regions do not replace control edges and do not yet claim CFG facts. In
particular, v0.3 does not serialize reachability, dominance, post-dominance,
merge blocks, or reducibility. Those will be computed and validated in M4.2
from the explicit hierarchy plus control edges.

Semantic regions are also separate from `.usgraph` comment regions. A semantic
region affects program structure; a comment region is only visual canvas state.
`GraphLayoutSynchronizer` validates Graph v0.3 but continues to preserve layout
solely by function/node/edge IDs, so adding region metadata does not move nodes
or change placement.

## Sample

Generate a region-aware Graph for the early-return sample:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Regions/EarlyReturn.cs \
  --out artifacts/regions
```

The result contains the following hierarchy:

```text
function ShouldApply
└── block method-body
    ├── if
    │   ├── then
    │   │   └── return true
    │   └── else (implicit)
    └── return false
```

The fallback return remains a direct member of the method-body region. The
implicit false region is empty because its control edge continues to that
fallback outside the `if`.
