# Semantic Patch Plan Protocol

## Purpose

`unifiedscript.patch-plan/v0.12` represents one visual-editor gesture as an
ordered, atomic sequence of the same bounded semantic operations supported by
`unifiedscript.patch/v0.12`. It exists because a useful graph edit often needs
IDs created by an earlier edit or changes several overlapping source regions.
Submitting those stages as unrelated filesystem transactions would expose
partial programs and force the editor to predict random persistent IDs.

The plan coordinator never bypasses the normal patch applier. Each stage becomes
a one-operation semantic patch, runs the complete C# profile and semantic
pipeline, and advances only an in-memory request. Only the final source and
identity sidecar are eligible for a CLI filesystem transaction.

## Envelope

```json
{
  "schemaVersion": "unifiedscript.patch-plan/v0.12",
  "sourceCompilationId": "us_...",
  "semanticConfigurationId": "us_...",
  "steps": []
}
```

The source compilation and semantic configuration IDs are the external
optimistic-concurrency baseline. Every step has a unique, non-empty `stepId`
and a persistent `targetNodeId`.

Version v0.12 supports these step discriminators:

| `step` | Purpose | Structural result |
| --- | --- | --- |
| `replaceLiteral` | Replace one literal with a complete literal token | Target ID survives |
| `changeOperator` | Change a unary/binary operator inside its result-preserving family | Operator node ID survives |
| `reconnectArgument` | Exchange two pure, exactly typed positional arguments | Invocation and endpoint IDs survive |
| `replaceExpressionInput` | Replace one exact binary/unary/cast/invocation-argument input | Parent and unaffected expression-spine IDs survive |
| `replaceAssignmentValue` | Replace one root simple-local-assignment value with a closed typed expression | Statement, assignment, and target IDs survive |
| `replaceWhileCondition` | Replace an existing while condition with a closed typed Boolean expression | While and body IDs survive |
| `replaceProducedWhileCondition` | Resolve an earlier while producer and replace its current condition | Consumes a loop handle without predicting generated IDs |
| `replaceIfCondition` | Replace an existing if condition with a closed typed Boolean expression | If and branch-block IDs survive |
| `replaceProducedIfCondition` | Resolve an earlier if producer and replace its current condition | Consumes a branch producer without predicting generated IDs |
| `replaceLocalInitializer` | Replace an existing local declaration initializer | Declaration and local-definition ID survive |
| `replaceProducedLocalInitializer` | Resolve an earlier local-literal producer and replace its current initializer | Consumes a local producer without predicting generated IDs |
| `replaceReturnValue` | Replace an existing synchronous value-return expression | Return statement ID survives |
| `replaceProducedReturnValue` | Resolve an earlier value-return producer and replace its current value | Consumes a terminal-statement producer without predicting generated IDs |
| `insertStatement` | Insert a local-literal declaration, local assignment, typed `if`/`while`, loop transfer, or terminal `return` | Local payload produces a handle; `if` produces branch handles; `while` produces a body handle |
| `insertBranchStatement` | Append a bounded statement to a produced branch | Consumes a branch handle; a declaration produces a value handle; an `if` or `while` produces structural handles |
| `insertLoopBodyStatement` | Append a bounded statement to a produced while body | Consumes a loop-body handle; a declaration, `if`, or nested `while` can publish more handles |
| `deleteStatement` | Delete one proven-safe unused/effect-free statement | Removed subtree becomes tombstones |
| `introduceTemporary` | Move one stable expression into a single-evaluation local | Produces a local handle |
| `reconnectValue` | Replace a stable consumer with a produced local reference | Consumes a local handle |

Each step repeats the same old-value, type, endpoint, parent, and sibling
preconditions as its standalone patch operation. The plan protocol does not
weaken operation safety.

## Symbolic local handles

`introduceTemporary` and a local-literal root, branch, or loop-body insertion
publish their `stepId` as a symbolic local handle. A later `reconnectValue`
supplies that value as `sourceStepId`:

```json
{
  "step": "reconnectValue",
  "stepId": "connectScale",
  "targetNodeId": "us_...",
  "sourceStepId": "insertScale",
  "expectedContainingStatementId": "us_...",
  "expectedParentBlockId": "us_...",
  "expectedPreviousSiblingId": "us_...",
  "expectedNextSiblingId": null,
  "expectedTypeId": "us_..."
}
```

The source step must occur earlier, produce the same semantic type, and target
the same method. After its stage succeeds, the coordinator reconciles the new
sidecar, finds exactly one produced declaration, and records its actual random
`SemanticId`. The reconnect stage then uses that ID in a normal
`ReconnectValuePatchOperation`.

### Produced locals inside expression trees

Plan v0.12 also exposes the same local handle as a transaction-only expression
leaf. Unlike `reconnectValue`, which replaces a whole stable consumer with one
identifier, `producedLocalReference` can participate at any depth inside a
later literal/local/unary/binary/cast expression:

```json
{
  "expression": "producedLocalReference",
  "sourceStepId": "insertScale",
  "expectedName": "scale",
  "expectedTypeId": "us_float_type"
}
```

The source must be an earlier local-literal insertion or
`introduceTemporary`, and its exact name and semantic type must match. Before
the consumer stage, the coordinator substitutes the producer's actual current
declaration ID and creates an ordinary `localReference`. The source patcher
then enforces same-method membership, lexical scope, CFG dominance, name, and
type at the real consumer position. A root local can feed later nested
statements; a branch local cannot escape or feed its sibling branch.

The transaction-only leaf is accepted in insertion payloads and direct or
produced assignment/while/if/initializer/return endpoint replacements. A
standalone patch rejects it because no plan step namespace exists. See
[PRODUCED_VALUE_REFERENCES.md](PRODUCED_VALUE_REFERENCES.md).

### Exact nested expression inputs

`replaceExpressionInput` addresses an existing parent expression and one of the
same data ports emitted by Graph: binary `left`/`right`, bounded unary or cast
`operand`, or resolved positional invocation `argN`. It carries the old child
endpoint, exact child type, exact parent-result type, and a typed `newInput`.

The plan form may consume a `producedLocalReference` in `newInput`. The
coordinator resolves that leaf before creating the ordinary standalone
operation, so generated-local scope and dominance are checked at the actual
stage position. The old child must be effect-free; boundary trivia is retained;
the parent, statement, unaffected ancestors, and sibling expressions receive
identity pins. See
[EXPRESSION_INPUT_PATCHING.md](EXPRESSION_INPUT_PATCHING.md).

## Symbolic branch-block handles

An `insertStatement` whose payload is `if` publishes two internal block handles
under its `stepId`. A later `insertBranchStatement` identifies that producer,
selects one branch, and supplies a bounded statement payload:

```json
{
  "step": "insertBranchStatement",
  "stepId": "insertHighDamageBonus",
  "targetNodeId": "us_original_parent_block",
  "sourceStepId": "insertGuard",
  "branch": "then",
  "newStatement": {
    "statement": "localLiteralDeclaration",
    "name": "highDamageBonus",
    "typeName": "int",
    "valueText": "2",
    "expectedTypeId": "us_int_type"
  }
}
```

`sourceStepId` must name an earlier root, branch, or loop-body insertion with an
`if` payload.
`targetNodeId` intentionally repeats that source step's original parent-block
target: it is the stable initial-baseline anchor used for target-document,
method, sidecar, and duplicate-target validation. It is not the generated
branch block ID. The coordinator captures the real `Then.Id` and `Else.Id`
after the producer stage and uses the selected ID in an ordinary internal
`InsertStatementPatchOperation`.

Branch statements append in plan order. Version v0.12 accepts
`localLiteralDeclaration`, `localAssignment`, `if`, `while`, `break`,
`continue`, or `return` here. An
assignment may target only an existing same-method local that lexically and
CFG-dominates the generated branch gap. A local declaration publishes its own
`stepId` as a normal local handle. A later
`reconnectValue` may consume it only where the existing structural-dominance
and CFG-dominance rules make it available; a branch local therefore cannot
escape its branch or feed its sibling branch.

### Recursive branch producers

When a branch insertion carries an `if`, its own `stepId` becomes another
branch producer. Descendants keep repeating the same original parent-block
`targetNodeId`; this remains an external baseline anchor, while each
`sourceStepId` selects the actual generated parent at its level:

```json
{
  "step": "insertBranchStatement",
  "stepId": "insertCriticalBranch",
  "targetNodeId": "us_original_parent_block",
  "sourceStepId": "insertEnabledBranch",
  "branch": "then",
  "newStatement": {
    "statement": "if",
    "condition": {
      "expression": "localReference",
      "sourceDefinitionId": "us_critical_parameter",
      "expectedName": "critical",
      "expectedTypeId": "us_bool_type"
    }
  }
}
```

The next step may use `"sourceStepId": "insertCriticalBranch"` and select its
`then` or `else`. Producer references must point backward, so the resulting
handle graph is an acyclic tree rooted at an initial `insertStatement`. Every
producer must remain in the same method and use the same stable original
target. The coordinator reanalyzes after each stage, resolves the newly
allocated `Then.Id` and `Else.Id`, and registers them only after the inserted
HIR node is proven to be an `if` with two explicit blocks.

## Symbolic loop-body handles

An insertion whose payload is `while` publishes one internal body handle under
its `stepId`. A later `insertLoopBodyStatement` names that producer and carries
a bounded statement payload:

```json
{
  "step": "insertLoopBodyStatement",
  "stepId": "continueLoop",
  "targetNodeId": "us_original_parent_block",
  "sourceStepId": "insertLoop",
  "newStatement": {
    "statement": "continue"
  }
}
```

The producer must occur earlier, target the same original parent Block and
method, and contain a `while` payload. After the producer stage, the coordinator
proves the inserted HIR node is a `HirWhileStatement`, records its actual
persistent loop and body IDs, and resolves later appends against the current
body. A body insertion containing `if` or another `while` publishes recursive
branch or loop handles under its own `stepId`.

`break` and `continue` do not carry a target ID. The source applier requires a
reachable insertion gap and discovers the nearest enclosing HIR loop; the
reprojected CFG independently verifies that transfer target. This prevents a
plan from manufacturing unstructured jumps or cross-loop edges.

## Symbolic produced expression-endpoint handles

`replaceProducedWhileCondition` edits the condition of a while created by an
earlier insertion in the same plan:

```json
{
  "step": "replaceProducedWhileCondition",
  "stepId": "tightenLoop",
  "targetNodeId": "us_original_parent_block",
  "sourceStepId": "insertLoop",
  "newCondition": {
    "expression": "localReference",
    "sourceDefinitionId": "us_enabled_parameter",
    "expectedName": "enabled",
    "expectedTypeId": "us_bool_type"
  }
}
```

The producer must be earlier, contain a `while` payload, use the same original
parent-block target, and declare the same condition type. During execution the
coordinator finds the producer's actual persistent while ID, reads its current
condition ID, and creates a normal `replaceWhileCondition` operation. Multiple
steps may reference the same producer; each resolves the condition produced by
the previous stage. Body insertions remain valid because the while and body
identities are preserved.

Plan v0.12 applies the same rule to `replaceProducedIfCondition`,
`replaceProducedLocalInitializer`, and `replaceProducedReturnValue`. Their
source step must be an earlier compatible root, branch, or loop-body insertion:
an `if`, a `localLiteralDeclaration`, or a value-bearing `return`, respectively.
The symbolic step repeats the producer's original stable target and declares a
replacement of the producer's exact type. Bare returns do not publish value
handles, and `introduceTemporary` does not publish an initializer-edit handle.

The coordinator records the generated if ID and condition ID, local declaration
ID and initializer ID, or return ID and value ID after its producer stage. A
later endpoint edit resolves those current IDs and runs the ordinary direct
operation. Produced endpoint handles are repeatable; every edit reads the
endpoint created by the preceding stage. Although a return terminates a
symbolic path for later appends, editing the value inside that existing return
remains valid.

Direct `replaceAssignmentValue`, `replaceWhileCondition`,
`replaceIfCondition`, `replaceLocalInitializer`, and `replaceReturnValue` steps
use exactly the standalone patch v0.12 payload plus `stepId`. They are validated
against the original plan baseline and are intentionally single-target, because
their explicit old endpoints describe the original rather than an intermediate
stage. See
[STRUCTURED_EXPRESSION_EDITING.md](STRUCTURED_EXPRESSION_EDITING.md) and
[GENERAL_EXPRESSION_EDITING.md](GENERAL_EXPRESSION_EDITING.md).

## Typed terminal returns

A `return` payload contains an optional expression and the expected method
return type:

```json
{
  "statement": "return",
  "expression": {
    "expression": "literal",
    "valueText": "true",
    "expectedTypeId": "us_bool_type"
  },
  "expectedReturnTypeId": "us_bool_type"
}
```

For a non-`void` method, the expression is required and its type ID must exactly
equal both `expectedReturnTypeId` and the currently bound method return type. A
`void` method requires a matching `System.Void` type ID and no expression.
Async and task-like methods are rejected until the protocol distinguishes the
declared task type from the value returned inside an async body.

A return may be inserted only at the end of an explicit block. No insertion may
follow an existing return. Because every symbolic body step appends, the
v0.12 JSON validator records each `(sourceStepId, branch)` path and rejects every
later append after a direct return. The other branch remains independently
writable.

Terminal state also propagates through nested producers. When both branches of
a nested `if` terminate, its parent path terminates if and only if that nested
`if` is still the final statement on the parent path. If another statement was
already appended after it, accepting the second child termination would make
that later statement unreachable; v0.12 rejects the plan before executing any
stage. This propagation is recursive across arbitrary acyclic nesting depth.

`break` and `continue` are also terminal within their selected branch or loop
body path. Terminal propagation stops at a `while` boundary: the condition may
be false before the first iteration, so a terminal body does not make the
parent path terminal. The same rule lets an outer loop body continue after a
nested loop whose own body terminates.

## Two-phase safety model

### Initial validation

Before executing the first stage, the coordinator:

1. analyzes the unchanged request;
2. checks the plan's source and configuration baselines;
3. resolves every original target to its required method and HIR category;
4. requires a persistent `.usids` sidecar on every target document;
5. applies every independently executable step to the original request in
   memory and rejects the plan if any standalone precondition fails;
6. validates symbolic reconnect statement, block, sibling, method, and type
   context against the original HIR; and
7. validates each branch or loop-body insertion's original parent block,
   source-step order, producer method, sidecar, selector where applicable, and
   bounded payload shape.

This prevents an earlier stage from making a stale external precondition appear
valid. Initial validation results are discarded; they never advance identity
generation or source.

### Ordered execution

For each step, the coordinator reanalyzes the current in-memory request,
constructs a fresh one-operation patch against that stage's compilation ID,
executes the normal patch applier, adopts the newly reconciled sidecar, removes
transient identity pins, and proceeds. A failed stage returns no patched request
or analysis from any earlier successful stage.

## Internal rebasing rules

Internal rebasing recognizes only structural changes produced by earlier steps
in the same validated plan:

- `introduceTemporary` and `reconnectValue` recompute the target expression's
  current containing statement, parent block, and adjacent siblings;
- `deleteStatement` recomputes the target statement's current parent and
  siblings;
- `insertStatement` anchors its original gap. If both original siblings remain,
  it inserts immediately before the original next sibling after prior planned
  insertions. A start/end/empty-block gap similarly retains plan order;
- `insertBranchStatement` resolves the actual block ID captured from its earlier
  `if` producer and appends after the current last statement in the selected
  branch;
- `insertLoopBodyStatement` resolves the actual body ID captured from its
  earlier `while` producer and appends after the current last body statement;
- `replaceProducedWhileCondition` resolves the actual loop ID and current
  condition ID captured through its earlier while producer on every stage;
- `reconnectArgument` locates both endpoint roots by persistent ID and derives
  their current positional indices;
- direct literal, operator, assignment-value, and existing-while-condition
  steps reuse their original endpoint/type preconditions, so those direct
  targets cannot be repeated in one plan.

Insertion steps and produced-while-condition steps may repeat their original
target block. This supports several ordered statements in one gap and repeated
condition edits through a dynamic producer handle without giving direct
literal/operator/endpoint edits ambiguous original-versus-intermediate
preconditions.

Any insertion step carrying an `if` or `while` payload executes through the
same staged transaction and rebasing. It publishes structural handles, but does
not publish a local handle. A `reconnectValue` whose `sourceStepId` names that
step is rejected during JSON validation rather than failing after source
mutation.

If an earlier step deletes a later target or removes every anchor needed to
reconstruct an insertion gap, execution fails atomically. Internal rebasing is
not a mechanism for accepting unrelated external source changes.

## CLI example

The checked-in composed sample inserts `scale`, reconnects a consumer to that
new declaration, changes `*` to `/`, and deletes `obsolete`:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/ComposedPlan.cs \
  --identity samples/Patching/ComposedPlan.cs.usids \
  --plan samples/Patching/ComposeScale.plan.json \
  --out artifacts/composed/ComposedPlan.cs \
  --out-identity artifacts/composed/ComposedPlan.cs.usids
```

The CLI requires non-in-place source and sidecar outputs and commits both with
the same backup-based transaction used by standalone patches. The input source
remains unchanged.

The branch-population sample demonstrates the symbolic block path:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/GuardRules.cs \
  --identity samples/Patching/GuardRules.cs.usids \
  --plan samples/Patching/PopulateGuard.plan.json \
  --out artifacts/guard-plan/GuardRules.cs \
  --out-identity artifacts/guard-plan/GuardRules.cs.usids
```

It resolves one generated `then` block twice, preserving the two insertion
steps' order, and resolves the generated `else` block once. The editor supplies
no generated statement or block IDs.

The typed early-return sample composes a same-valued new branch literal with a
later edit to the original fallback literal. It also proves that nested-block
insertion pins unaffected live nodes outside the replaced subtree, preventing
same-shape identities from migrating to newly authored syntax:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/GuardRules.cs \
  --identity samples/Patching/GuardRules.cs.usids \
  --plan samples/Patching/ReturnGuard.plan.json \
  --out artifacts/return-guard/GuardRules.cs \
  --out-identity artifacts/return-guard/GuardRules.cs.usids
```

The recursive sample creates an outer `enabled` decision, inserts a nested
`critical` decision into its generated `then`, and populates all three leaf
paths with assignments:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/NestedDecisionRules.cs \
  --identity samples/Patching/NestedDecisionRules.cs.usids \
  --plan samples/Patching/BuildNestedDecision.plan.json \
  --out artifacts/nested/NestedDecisionRules.cs \
  --out-identity artifacts/nested/NestedDecisionRules.cs.usids
```

The five internal stages finish with no tombstones: original identities remain
live, enclosing branch/block ancestors retain their IDs, and the editor never
supplies either generated `if` or Block ID.

The loop-construction sample creates a while, populates a nested stop branch,
and appends assignment and transfer statements without predicting generated
loop, body, or branch IDs:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/LoopBuildRules.cs \
  --identity samples/Patching/LoopBuildRules.cs.usids \
  --plan samples/Patching/BuildLoop.plan.json \
  --out artifacts/loop-plan/LoopBuildRules.cs \
  --out-identity artifacts/loop-plan/LoopBuildRules.cs.usids
```

The structured expression-edit sample uses the direct assignment and existing
while wrappers in one transaction:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/EditLoopRules.cs \
  --identity samples/Patching/EditLoopRules.cs.usids \
  --plan samples/Patching/EditLoopRules.plan.json \
  --out artifacts/expression-edit/EditLoopRules.cs \
  --out-identity artifacts/expression-edit/EditLoopRules.cs.usids
```

## Deliberate limits

Plan v0.12 composes all operations currently defined by patch v0.12 and resolves
recursive symbolic `then`/`else` and loop-body handles for bounded local,
assignment, nested-if/while, loop-transfer, and typed-return population. It
also resolves repeatable expression-endpoint edits on produced while, if,
local-literal declaration, and value-return statements. It cannot insert
calls, member writes, or arbitrary statement payloads through a structural
handle. It has no cross-method or cross-document symbolic handle. Generated
locals may be consumed inside later typed expressions, but arbitrary generated
expression results still do not publish value handles. Existing-expression
replacement supports complete assignment-value, while-condition, if-condition,
local-initializer, and return-value endpoints plus exact binary, bounded unary,
cast, and positional-invocation argument inputs already present in the external
baseline. Generated arbitrary expression nodes do not yet publish handles for
later input edits. Multi-document in-memory analysis remains available
below the CLI, while the CLI commit boundary is still one source/identity pair.

There is no automatic reader migration for older patch-plan schemas; producers
should reanalyze and emit the current schema and baselines. The original
temporary-plus-reconnect and earlier composed step shapes remain representable
in v0.12. Older plan documents must be regenerated because the reader accepts
only the current schema.
