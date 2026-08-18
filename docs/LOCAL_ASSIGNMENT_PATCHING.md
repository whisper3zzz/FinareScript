# Local Assignment Patching

## Purpose

M4.3 adds the first state-changing statement that can be authored from the
visual side without introducing a second execution path. A
`localAssignment` payload is still an `insertStatement` operation: it targets
one existing C# block and one sibling-guarded insertion gap, then emits a normal
C# simple assignment that lowers back into the existing HIR and Graph
`assignment` node.

The patch boundary uses both C# lexical scope and the M4.2 Graph CFG. Source
syntax proves that a local name is legally in scope; CFG reachability and
dominance prove that the definition executes on every path reaching the edit.
Neither approximation is accepted alone.

## Protocol

Current patch schema `unifiedscript.patch/v0.16` includes the
`localAssignment` statement discriminator:

```json
{
  "operation": "insertStatement",
  "targetNodeId": "us_parent_block",
  "expectedPreviousSiblingId": "us_local_declaration",
  "expectedNextSiblingId": "us_return",
  "newStatement": {
    "statement": "localAssignment",
    "targetDefinitionId": "us_local_declaration",
    "expectedName": "result",
    "expectedTypeId": "us_int_type",
    "value": {
      "expression": "binary",
      "operator": "+",
      "left": {
        "expression": "localReference",
        "sourceDefinitionId": "us_local_declaration",
        "expectedName": "result",
        "expectedTypeId": "us_int_type"
      },
      "right": {
        "expression": "literal",
        "valueText": "1",
        "expectedTypeId": "us_int_type"
      },
      "expectedTypeId": "us_int_type"
    }
  }
}
```

`targetDefinitionId` identifies the existing writable local. `expectedName`
and `expectedTypeId` are optimistic-concurrency guards: renaming the local or
changing its type invalidates the patch even if the persistent ID survives.
The value's declared type must exactly equal the target type.

Only simple `=` assignment is synthesized. Compound assignment, increment and
decrement, member/property writes, indexers, deconstruction, tuple targets,
`ref` writes, and cross-method targets remain outside this slice.

## CFG insertion proof

`GraphPatchSafetyIndex` projects the already analyzed HIR and bound semantics
to Graph v0.9, derives the validated CFG, and maps a source insertion gap to
control-flow anchors:

- before an existing statement, the anchors are that statement's first
  control nodes, including any effect chain before its statement node;
- at the end of a non-empty block, the anchors are reachable non-return
  control nodes whose edges fall through out of the block-region subtree;
- in a newly created empty `then` or `else`, the reachable branch node is the
  path anchor;
- a non-empty block with only terminal return paths has no fallthrough anchor
  and rejects assignment insertion as unreachable;
- an empty nested lexical block inherits the nearest enclosing continuation.

An assignment is accepted only if the gap has at least one reachable anchor.
The target declaration must lexically dominate the gap and its Graph control
node must dominate every resolved anchor. This permits useful shapes such as:

```csharp
int result = 0;
if (ready)
{
    int branchValue = 2;
    result = branchValue;
}
return result;
```

It rejects:

- assigning to a local declared in a sibling or exited branch;
- appending after an explicit return;
- appending after an `if` whose two branches return;
- appending to a branch whose nested control paths all terminate;
- consuming an unreachable local or expression as if it were available.

The same Graph proof now reinforces `localReference` nodes inside all
graph-authored expressions and `reconnectValue` consumers. The earlier lexical
block check remains as a C# scoping guard; M4.3 does not replace language scope
with Graph topology.

## Type and effect boundaries

The target must be an existing same-method local with a bound graph-safe type.
The right-hand side uses the existing closed expression algebra:

- scalar literals;
- parameter or dominating-local references;
- bounded unary and binary operators;
- explicit scalar casts.

Calls, member/state reads, allocation, await, mutation, assignments nested in
the value, and unknown effects cannot be authored by this payload. Therefore
the only new effect is the explicit write represented by the assignment node;
evaluation count and C# left-to-right ordering remain evident in the emitted
source and Graph control chain.

The patch undergoes full Visual Profile validation, C# compilation, semantic
binding, Graph projection, CFG/reducibility validation, and identity
reconciliation before a result is returned.

## Value reconnection hardening

M4.3 upgrades `reconnectValue` from lexical-only dominance to combined lexical
and CFG dominance. An unreachable consumer carries no dominance facts and is
rejected. Reconnection also refuses every expression located inside the
writable target subtree of an existing assignment; changing
`targetValue = 3` into `sourceValue = 3` is a write retargeting operation, not a
data-input reconnection.

Stable right-hand-side consumers remain reconnectable when their source local
dominates the containing statement and their exact graph-safe types match.
Effectful or state-reading consumers, comment-bearing removed subtrees, and
conditional-evaluation shapes remain rejected.

## Identity and transactions

Existing block, sibling, declaration, and surviving expression identities are
pinned across the Roslyn replacement. The new expression statement,
assignment node, target identifier, and value nodes receive fresh persistent
IDs and immediately project through the ordinary HIR/Graph path. No special VM
node or editor-only assignment representation exists.

Patch-plan schema `unifiedscript.patch-plan/v0.17` carries the same payload in a
direct `insertStatement` step. `insertBranchStatement` may also place an
assignment into a generated `then` or `else` block, allowing one atomic plan to
create a branch and write different values to an existing outer local on each
path. `insertLoopBodyStatement` applies the identical rules at a generated
while-body gap. Every stage reanalyzes and re-derives CFG facts; a late failure
discards all earlier in-memory stages.

## CLI sample

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-patch \
  samples/Patching/AssignmentRules.cs \
  --identity samples/Patching/AssignmentRules.cs.usids \
  --patch samples/Patching/IncrementResult.patch.json \
  --out artifacts/assignment/AssignmentRules.cs \
  --out-identity artifacts/assignment/AssignmentRules.cs.usids
```

The checked-in patch inserts:

```csharp
result = result + 1;
```

The input source and identity sidecar remain unchanged; only the requested
output pair is committed.

## Current limits

M4.7 now edits the right-hand value of an existing root simple-local assignment
through `replaceAssignmentValue`; see
[STRUCTURED_EXPRESSION_EDITING.md](STRUCTURED_EXPRESSION_EDITING.md). It still
does not retarget the assignment, edit compound or nested assignments,
reconnect arbitrary Graph data ports, move control nodes, synthesize calls,
introduce phi/SSA values, or prove definite assignment independently from the
C# compiler. Exception edges, short-circuit CFG, and async suspension states
also remain outside the current slice.
