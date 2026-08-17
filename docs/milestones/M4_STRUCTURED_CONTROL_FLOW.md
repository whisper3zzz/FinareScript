# M4: Structured Control Flow Expansion

## Goal

Make practical gameplay control flow explicit, verifiable, and editable in the
shared Graph/HIR model before introducing a UE editor or a second execution
path.

Status: in progress. M4.1 explicit regions are implemented; CFG analysis and
loop regions are the next slices.

## M4.1 implemented vertical slice

- `unifiedscript.graph/v0.3` schema
- explicit `GraphRegionKind` values for `function`, `block`, `if`, `then`, and
  `else`
- one root function region and one method-body block region per Graph function
- ordered region parent/child relationships
- exactly one direct region owner for every Graph node
- source HIR owner and source span on every region
- explicit vs implicit `else` preservation
- stable region IDs derived from persistent HIR IDs and structural roles
- region-aware projection for expressions, effects, branches, early returns,
  and nested lexical blocks
- deterministic Graph JSON normalization and region validation
- Graph-layout compatibility without mixing semantic and comment regions
- early-return sample and Golden Graph v0.3 coverage

## M4.1 acceptance criteria

1. A method projects a `function` root with exactly one body `block` child.
2. Every node appears in exactly one region's `nodeIds`.
3. An `if` directly owns its condition and branch node.
4. Each `if` owns ordered `then` and `else` children.
5. A missing C# `else` becomes an empty implicit region; an explicit empty
   `else` remains non-implicit.
6. Early-return nodes belong to the selected branch while the outer fallback
   remains in the surrounding block.
7. Nested lexical blocks form child block regions.
8. Invalid ownership, hierarchy, branch order, or implicit markers fail JSON
   validation.
9. Identical input produces byte-identical region-aware Graph JSON.
10. Existing semantic node layout survives region projection unchanged.
11. The CLI emits and consumes Graph v0.3 for layout initialization/sync.
12. The full solution, project validator, Golden fixture, and CLI regressions
    pass.

## Deliberate limits

M4.1 does not infer a general CFG, materialize merge nodes, compute
reachability/dominance/post-dominance, or validate reducibility. It does not add
assignments to the patch algebra, nested-branch synthesis, loops, switch,
conditional expressions, or automatic node placement. Region kinds for loops
remain absent until their HIR and control semantics are defined.

## Next slice

M4.2 will construct a deterministic per-function CFG index from control edges
and regions. It will expose entry, successor, predecessor, reachability,
dominance, immediate dominator, post-dominance, and structured merge facts,
then reject region/edge combinations that are irreducible for visual round-trip.
Those executable invariants will become the safety basis for assignment and
nested-control-flow patch operations before `while`/`break`/`continue` are
added.
