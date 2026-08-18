# Graph Regions

## Purpose

`unifiedscript.graph/v0.3` introduced explicit structured regions to the
semantic Graph, v0.4 added structured `while` loops, and v0.9 added explicit
`for` initializer/increment phases. Current `unifiedscript.graph/v0.12` retains
the distinct `do ... while` entry and indexed-foreach collection phases, then
adds disposal ownership for enumerable `foreach`.
Nodes and control/data edges still describe computation; regions
describe lexical ownership and structured control-flow nesting without
inferring it from screen coordinates or reverse-engineering edge shapes.

The v0.3 branch hierarchy remains compatible in v0.4. Loop structure now uses
the same direct-ownership and parent/child rules instead of being inferred from
cyclic edge geometry.

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

The current schema defines eleven kinds:

| Kind | Meaning | Parent |
| --- | --- | --- |
| `function` | One visual method | none |
| `block` | Method body or nested lexical block | structured enclosing region |
| `if` | Branch condition and branch control node | block/branch region |
| `then` | True branch body | corresponding `if` |
| `else` | False branch body or implicit false path | corresponding `if` |
| `loop` | While/do/for/foreach decision node and directly owned control | enclosing structured region |
| `loopInitializer` | Ordered for initializers | corresponding `loop` |
| `loopCollection` | Evaluate-once foreach collection and capture | corresponding `loop` |
| `loopBody` | While/do/for/foreach body | corresponding `loop` |
| `loopIncrement` | Ordered for incrementors | corresponding `loop` |
| `loopCleanup` | Enumerator disposal for normal and pending transfers | corresponding `loop` |

An `if` region directly owns the condition expression nodes and its `branch`
node. Its `childRegionIds` must contain exactly the `then` region followed by the
`else` region. Nodes inside each body belong directly to that branch region;
nested `if` or lexical blocks appear as child regions instead of flattening
their nodes into the parent.

The projector always emits an `else` region because the Graph branch has an
explicit false control port. If C# omitted the `else` block, the region is empty
and `isImplicit` is `true`. Omitted for initializer/increment clauses likewise
produce empty implicit phase regions. No other region kind may be implicit.

A `loop` region directly owns its condition expression and `loop` decision
node. A while has exactly one `loopBody` child sourced from the HIR body block.
A do-while has the same child-region count plus one generated `loopEntry` node
owned directly by `loop`; that node enters the body once before the condition.
A for has ordered `loopInitializer`, `loopBody`, and `loopIncrement` children.
A foreach always starts with ordered `loopCollection` and `loopBody` children.
Its collection phase owns the source expression and evaluate-once capture; its
body starts with the source-backed iteration-variable binding. An enumerator
strategy appends `loopCleanup`, owned by the same loop, containing distinct
normal/break, return, and exception disposal nodes. Neither `loopCollection`
nor `loopCleanup` is implicit.
A break or continue nested under an `if` remains directly owned by that lexical
`then`/`else` region; the derived CFG separately records which nearest loop owns
its transfer. See [LOOP_FOUNDATION.md](LOOP_FOUNDATION.md).

## Direct node ownership

Every Graph node belongs to exactly one direct region through `nodeIds`:

- the entry node belongs to the function region;
- method-body statements and their expressions belong to the body block;
- branch conditions and branch nodes belong to the `if` region;
- branch-body statements and expressions belong to `then` or `else`;
- loop conditions and decisions belong to `loop`; initializer, body, and
  increment nodes belong to their corresponding phase or nested child regions;
- foreach collection/capture nodes belong to `loopCollection`, while its
  iteration binding and statements belong to `loopBody`;
- enumerable acquisition remains in `loopCollection`; generated disposal nodes
  belong to `loopCleanup`;
- nodes inside a nested structured construct belong to its child region.

Region node order is projection/evaluation order and is therefore preserved.
The top-level `nodes`, `edges`, and `regions` arrays are normalized by semantic
ID for byte-deterministic JSON; `nodeIds` and `childRegionIds` retain their
semantic order.

`GraphJson` rejects missing or duplicate node ownership, unknown node/child
references, inconsistent parent links, unreachable/cyclic regions, secondary
function roots, invalid implicit regions, and malformed `if` or loop children.
Older Graph schema documents, including v0.3, must be regenerated; no migration
guesses a loop hierarchy from cyclic edges.

## Control flow and layout boundaries

Regions do not replace control edges and do not serialize CFG facts. M4.2 now
computes and validates reachability, dominance, post-dominance, merge nodes, and
reducibility from the explicit hierarchy plus control edges. The result remains
a derived in-memory index so edits cannot leave stale quadratic analysis data
inside Graph JSON. See [CONTROL_FLOW_ANALYSIS.md](CONTROL_FLOW_ANALYSIS.md).

Semantic regions are also separate from `.usgraph` comment regions. A semantic
region affects program structure; a comment region is only visual canvas state.
`GraphLayoutSynchronizer` validates Graph v0.12 but continues to preserve layout
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
