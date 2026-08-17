# M4: Structured Control Flow Expansion

## Goal

Make practical gameplay control flow explicit, verifiable, and editable in the
shared Graph/HIR model before introducing a UE editor or a second execution
path.

Status: in progress. M4.1 explicit regions, M4.2 derived CFG analysis, and
M4.3 CFG-checked local assignment/data reconnection are implemented. Nested
branch construction and broader structural HIR operations are the next slice.

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

## M4.3 implemented vertical slice

- `unifiedscript.patch/v0.8` local-assignment statement payload
- `unifiedscript.patch-plan/v0.6` direct and symbolic-branch assignment steps
- ordinary normalized C# assignment emission and full HIR/Graph reprojection
- existing same-method local targets with persistent-ID, name, and exact-type
  preconditions
- bounded, closed, pure graph-authored right-hand-side expressions
- a patch-time Graph safety index derived from the current HIR and CFG
- insertion-gap reachability checks, including terminal nested branch regions
- combined lexical-scope and CFG-dominance checks for assignment targets and
  local references used by authored expressions
- CFG-backed value-reconnection dominance checks
- rejection of reconnect targets inside an assignment's writable left-hand
  side
- atomic plan composition and identity-sidecar reconciliation
- checked-in `IncrementResult.patch.json` CLI sample

## M4.3 acceptance criteria

1. Patch v0.8 JSON round-trips a typed `localAssignment` payload.
2. Plan v0.6 accepts that payload as a direct statement insertion and as a
   generated `then`/`else` branch append.
3. An assignment targets only an existing local declaration in the same
   method, identified by persistent semantic ID.
4. The target's expected name and exact graph-safe semantic type must still
   match the current program.
5. The insertion gap must be reachable in the derived function CFG.
6. The target declaration must be lexically visible and CFG-dominate every
   control anchor for the insertion gap.
7. Every local referenced by the right-hand side must satisfy the same lexical
   and CFG availability rules.
8. The right-hand side remains inside the closed, pure, bounded scalar
   expression algebra and must exactly match the target type.
9. Assignments at reachable branch ends work, while insertion after a direct or
   nested all-paths-return region is rejected.
10. Successful insertion emits ordinary C#, rebinds to an HIR assignment, and
    projects the corresponding Graph assignment/control facts.
11. Value reconnection rejects unreachable consumers and any target inside an
    assignment's writable left-hand side.
12. A plan can populate both generated branches with assignments to a valid
    outer local without predicting generated block IDs.
13. Any failed stage returns no partial source or identity sidecar.
14. The checked-in assignment sample, full solution, test suite, and project
    validator pass.

## Deliberate limits

M4.3 consumes derived CFG facts at patch time but does not serialize them or
materialize source/Graph merge nodes. Assignment support is intentionally
limited to inserting a simple assignment to an existing local; it does not edit
an existing assignment, synthesize declarations with arbitrary initializers,
write members/indexers, or support compound assignments, increment/decrement,
calls, or effectful right-hand sides. Nested-branch synthesis, loops, switch,
conditional expressions, exception edges, short-circuit expression flow,
async state-machine transitions, control dependence, SSA, and automatic node
placement remain outside the current slice. Region kinds for loops remain
absent until their HIR and control semantics are defined.

## Next slice

M4.4 should add nested branch construction and the minimum structural HIR
operations needed to author useful multi-level gameplay decisions. It should
reuse M4.3's insertion reachability and dominance boundary, define bounded
symbolic handles for nested regions, and preserve atomic source/identity/layout
reconciliation. Loop HIR and regions should follow only after nested acyclic
control flow is editable end to end.
