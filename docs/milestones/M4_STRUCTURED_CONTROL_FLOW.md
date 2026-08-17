# M4: Structured Control Flow Expansion

## Goal

Make practical gameplay control flow explicit, verifiable, and editable in the
shared Graph/HIR model before introducing a UE editor or a second execution
path.

Status: in progress. M4.1 explicit regions and M4.2 derived CFG analysis are
implemented; bounded assignment/data rewiring is the next slice.

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

## M4.2 implemented vertical slice

- derived `GraphControlFlowAnalysis` and per-function CFG model
- control-node filtering that keeps pure data expressions out of the CFG
- validated real control arcs plus stable synthetic function exit/arcs
- deterministic predecessor and successor indexes
- entry reachability and explicit unreachable control-node reporting
- dominator sets and immediate dominators
- post-dominator sets and immediate post-dominators
- structured branch then/else entry and nearest real merge facts
- branch-region function-exit classification for early returns
- deterministic SCC discovery and single-entry reducibility checks
- diagnostic `Analyze` path plus rejecting `AnalyzeAndValidate` path
- automatic CFG/reducibility validation at the HIR-to-Graph projection boundary
- coverage for sequential flow, normal branch merges, implicit-else early
  return, both-branch return, unreachable statements, nested branches,
  reducible cycles, and irreducible two-entry cycles

## M4.2 acceptance criteria

1. Every projected function has one real entry and one stable synthetic exit.
2. Returns and otherwise unconnected control outputs receive deterministic
   synthetic exit arcs without changing Graph JSON.
3. Pure expression nodes do not appear in predecessor/successor indexes.
4. Unreachable control nodes remain visible but carry no fabricated dominance
   relations.
5. Sequential immediate dominators/post-dominators match execution order.
6. Both sides of a falling-through branch identify the first shared real merge.
7. A one-sided early return has no real merge and distinguishes the exiting
   branch from the implicit fallthrough branch.
8. A both-return branch reports two regional exits and no real merge.
9. Nested branch analysis is deterministic.
10. A single-entry cyclic SCC whose header dominates the component is
    reducible.
11. A two-entry cyclic SCC is reported by `Analyze` and rejected by
    `AnalyzeAndValidate`.
12. Every normal HIR Graph projection passes reducibility validation before it
    is returned to CLI/library consumers.

## Deliberate limits

M4.2 derives but does not serialize CFG facts, materialize source/Graph merge
nodes, or rewrite M3's lexical patch-dominance checks. It does not add
assignments to the patch algebra, nested-branch synthesis, loops, switch,
conditional expressions, exception edges, short-circuit expression flow,
async state-machine transitions, control dependence, SSA, or automatic node
placement. Region kinds for loops remain absent until their HIR and control
semantics are defined.

## Next slice

M4.3 should use Graph reachability/dominance and the existing type/effect model
to add bounded local assignment plus compatible data rewiring. Assignment
targets must be existing same-method locals, must be reachable at the insertion
point, and must dominate later consumers where required. Only after those
checks are executable should nested branch construction and loop HIR/regions
extend the control-flow surface.
