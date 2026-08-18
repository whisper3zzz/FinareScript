# Structured Expression Editing

## Purpose

M4.7 closes the first important gap between constructing control flow and
editing it. A visual editor can now replace:

- the value of an existing simple local assignment; and
- the condition of an existing or plan-produced `while`.

Both edits consume the same closed, typed `PatchExpression` tree used by
graph-authored statement insertion. They produce ordinary C# and then rerun the
complete parse, Visual Profile, compiler, semantic-binding, identity, Graph,
and CFG pipeline. There is no editor-only expression VM or second executable
representation.

These operations were introduced in patch v0.10 and plan v0.9. The current
strict-reader versions are `unifiedscript.patch/v0.14` and
`unifiedscript.patch-plan/v0.15`, extended by the M4.8 endpoint operations in
[GENERAL_EXPRESSION_EDITING.md](GENERAL_EXPRESSION_EDITING.md).

## Replace an assignment value

`replaceAssignmentValue` targets the `HirAssignmentExpression`, not the
containing statement or the old right-hand expression:

```json
{
  "operation": "replaceAssignmentValue",
  "targetNodeId": "us_assignment",
  "expectedOperator": "=",
  "expectedTargetNodeId": "us_target_identifier",
  "expectedTargetDefinitionId": "us_local_definition",
  "expectedTargetName": "result",
  "expectedValueNodeId": "us_old_value",
  "expectedTypeId": "us_int_type",
  "newValue": {
    "expression": "binary",
    "operator": "-",
    "left": {
      "expression": "localReference",
      "sourceDefinitionId": "us_local_definition",
      "expectedName": "result",
      "expectedTypeId": "us_int_type"
    },
    "right": {
      "expression": "literal",
      "valueText": "2",
      "expectedTypeId": "us_int_type"
    },
    "expectedTypeId": "us_int_type"
  }
}
```

The operation deliberately repeats all structural endpoints. It succeeds only
when the current node is a root expression in an expression statement, its
operator is simple `=`, its target and value IDs still match, and the target
identifier still binds to the named existing local definition. The assignment,
target, old value, definition, and new value must have the same exact semantic
type ID.

The assignment statement must be CFG-reachable. Every local reference in the
new value must be in lexical scope and available at that statement on every
incoming CFG path. This is the same dominance rule used for graph-authored
local-assignment insertion.

Compound assignments, assignments nested inside another expression, parameter
or field targets, deconstruction, and effectful arbitrary expressions remain
outside the bounded profile.

## Replace a while condition

`replaceWhileCondition` targets the persistent `HirWhileStatement` and repeats
the current condition endpoint:

```json
{
  "operation": "replaceWhileCondition",
  "targetNodeId": "us_while",
  "expectedConditionNodeId": "us_old_condition",
  "expectedConditionTypeId": "us_bool_type",
  "newCondition": {
    "expression": "localReference",
    "sourceDefinitionId": "us_enabled_parameter",
    "expectedName": "enabled",
    "expectedTypeId": "us_bool_type"
  }
}
```

The current condition and the replacement must resolve to the exact declared
type ID for `System.Boolean`. The while statement must be CFG-reachable, and
every referenced parameter or local must be available at the loop header. The
loop body is not replaced or regenerated.

## Plan steps

Patch plan v0.15 exposes direct wrappers for both operations:

- `replaceAssignmentValue`; and
- `replaceWhileCondition`.

Their fields are identical to the standalone operation plus a unique `stepId`.
They can be composed with existing insertions, branch/loop-body population,
deletion, temporary extraction, reconnection, literal edits, and operator edits.
The plan remains all-or-nothing.

### Editing a while produced earlier in the same plan

A visual editor cannot know the random persistent ID of a while that has not
been inserted yet. `replaceProducedWhileCondition` therefore uses the earlier
while insertion's `stepId` as a structural handle:

```json
{
  "step": "replaceProducedWhileCondition",
  "stepId": "addStopRule",
  "targetNodeId": "us_original_parent_block",
  "sourceStepId": "insertLoop",
  "newCondition": {
    "expression": "binary",
    "operator": "&&",
    "left": {
      "expression": "localReference",
      "sourceDefinitionId": "us_enabled_parameter",
      "expectedName": "enabled",
      "expectedTypeId": "us_bool_type"
    },
    "right": {
      "expression": "binary",
      "operator": ">",
      "left": {
        "expression": "localReference",
        "sourceDefinitionId": "us_result_local",
        "expectedName": "result",
        "expectedTypeId": "us_int_type"
      },
      "right": {
        "expression": "literal",
        "valueText": "0",
        "expectedTypeId": "us_int_type"
      },
      "expectedTypeId": "us_bool_type"
    },
    "expectedTypeId": "us_bool_type"
  }
}
```

`sourceStepId` must name an earlier root, branch, or loop-body insertion whose
payload is `while`. Both steps repeat the same original parent-block
`targetNodeId`, and the new condition must declare the producer condition's
type. The coordinator resolves the actual current loop ID and current condition
ID after each intermediate reanalysis, then creates an ordinary internal
`ReplaceWhileConditionPatchOperation`.

The same producer handle may be edited more than once. Every edit resolves the
condition created by the preceding stage, so the editor never needs an
intermediate expression ID. Later `insertLoopBodyStatement` steps can continue
to use that producer's body handle.

## Identity and layout behavior

These edits preserve the identities of the structures being edited:

- assignment expression statement;
- assignment expression and target identifier;
- while statement and body block; and
- unaffected surrounding methods, blocks, statements, and expressions.

Expression descendants are reconciled rather than blindly discarded. A node
that still has an unambiguous equivalent in the replacement may retain its
persistent ID; nodes that no longer exist become sidecar tombstones. This lets
an unchanged graph leaf or operator keep its visual placement while new
expression nodes enter the layout as `unplaced`. A removed condition root or
changed literal cannot silently lend its ID to an unrelated node.

After either edit, Graph projection and CFG analysis run again. A preserved
while ID remains the same loop-header identity, while its new condition data
edges and any new value nodes are derived from the reanalyzed HIR.

## Lossless-source guard

The old assignment value or while condition is rejected if its syntax contains
comments or directives. The patcher cannot determine where such trivia belongs
inside a completely new expression tree, so discarding it would violate the
lossless source contract. The editor must first perform an explicit
comment-aware source edit or ask the user to relocate the comment.

Whitespace owned by the old expression boundary is preserved. The generated
replacement is normalized only inside that boundary.

## Checked-in sample

The sample starts with:

```csharp
while (result > 0)
{
    result = result - 1;
}
```

Run the two-step plan:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/EditLoopRules.cs \
  --identity samples/Patching/EditLoopRules.cs.usids \
  --plan samples/Patching/EditLoopRules.plan.json \
  --out artifacts/expression-edit/EditLoopRules.cs \
  --out-identity artifacts/expression-edit/EditLoopRules.cs.usids
```

The single committed result contains:

```csharp
while (enabled && (result > stopAt))
{
    result = result - 2;
}
```

The output sidecar advances from generation 1 to generation 3 because each
ordered stage is reanalyzed. The original while, body, assignment, statement,
and target IDs remain live; the removed `0`, removed `1`, and superseded old
condition root become tombstones.

## Deliberate limits

M4.7 itself is an editing primitive, not arbitrary syntax replacement. M4.8
later adds `if` conditions, declaration initializers, and return expressions;
M4.10 adds exact nested binary/unary/cast and positional invocation-argument
inputs already present in the external baseline. M4.11 publishes generated
expression roots and bounded binary/unary/cast port paths for later plan
stages. M4.12 adds conditional expression construction and canonical
condition/arm paths. The authored algebra still does not
introduce calls, member access, object construction, indexing, lambdas,
mutation inside a value expression, or effect sequencing.

Every admitted shape uses an explicit typed operation with its own endpoint and
effect preconditions. Keeping the bounded protocol explicit is what makes the
same operation safe for both text and visual front ends. See
[EXPRESSION_INPUT_PATCHING.md](EXPRESSION_INPUT_PATCHING.md).
Generated-tree addressing is specified in
[PRODUCED_EXPRESSION_HANDLES.md](PRODUCED_EXPRESSION_HANDLES.md).
