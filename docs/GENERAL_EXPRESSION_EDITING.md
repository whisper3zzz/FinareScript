# General Expression Endpoint Editing

## Purpose

M4.8 generalizes the exact-endpoint replacement model proven by assignment
values and `while` conditions. A visual editor can now replace three more
common expression ports without deleting and recreating their enclosing
statements:

- the condition of an existing `if`;
- the initializer of an existing local declaration; and
- the value of an existing non-void return.

The standalone protocol is `unifiedscript.patch/v0.25`; the transaction
protocol is `unifiedscript.patch-plan/v0.21`. All replacements consume the
same closed `PatchExpression` algebra used by statement construction and by
M4.7 expression editing: literal, existing local/parameter reference, unary,
binary, cast, conditional, and bounded conditional member access.

The replacement is emitted as ordinary C#, then passed through the complete
parser, Visual Profile validator, compiler, semantic binder, persistent
identity reconciler, Graph projector, and CFG analyzer. Text and visual edits
therefore share one semantic pipeline and one future execution VM; this feature
does not introduce an editor-only expression interpreter.

## Existing endpoint operations

### Replace an `if` condition

`replaceIfCondition` targets the persistent `HirIfStatement` and repeats the
current condition endpoint and type:

```json
{
  "operation": "replaceIfCondition",
  "targetNodeId": "us_if",
  "expectedConditionNodeId": "us_old_condition",
  "expectedConditionTypeId": "us_bool_type",
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
      "expression": "literal",
      "valueText": "true",
      "expectedTypeId": "us_bool_type"
    },
    "expectedTypeId": "us_bool_type"
  }
}
```

The current endpoint and the replacement must have the exact graph-safe
`System.Boolean` type ID. The `if` must belong to a CFG-reachable statement,
and every definition referenced by the replacement must be lexically visible
and available on every incoming CFG path at that statement. The `then` and
`else` blocks are never regenerated.

### Replace a local initializer

`replaceLocalInitializer` targets the persistent local-declaration statement:

```json
{
  "operation": "replaceLocalInitializer",
  "targetNodeId": "us_local_declaration",
  "expectedName": "result",
  "expectedInitializerNodeId": "us_old_initializer",
  "expectedTypeId": "us_int_type",
  "newInitializer": {
    "expression": "binary",
    "operator": "-",
    "left": {
      "expression": "localReference",
      "sourceDefinitionId": "us_value_parameter",
      "expectedName": "value",
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

The declaration must still have the expected name, initializer endpoint, and
exact graph-safe declared type. References are checked at the declaration
position, so the local being declared cannot reference itself and a definition
available only on a sibling or later path cannot be connected to the
initializer. The declaration statement itself remains the same semantic
definition.

### Replace a return value

`replaceReturnValue` targets a value-return statement and repeats the old value
endpoint plus the containing method's return type:

```json
{
  "operation": "replaceReturnValue",
  "targetNodeId": "us_return",
  "expectedValueNodeId": "us_old_return_value",
  "expectedReturnTypeId": "us_int_type",
  "newValue": {
    "expression": "localReference",
    "sourceDefinitionId": "us_result_local",
    "expectedName": "result",
    "expectedTypeId": "us_int_type"
  }
}
```

The statement must still be a reachable value return in a synchronous,
non-void, non-task-like method. Its bound method return type, old value type,
and new expression type must be the same exact graph-safe type. Bare `return;`,
`async` methods, and task-like return contracts are rejected in this bounded
slice.

## Shared safety contract

All three operations use optimistic structural preconditions in addition to
the document's source-compilation and semantic-configuration baselines. A
request fails atomically if the statement kind, endpoint ID, name, return type,
or semantic type has changed since the editor produced the patch.

`PatchExpression` is deliberately closed. It can compose literals, references
to already existing parameters or locals, supported unary/binary operators,
and explicit casts. Each node declares its expected semantic result type. The
patcher constructs syntax at the exact statement position and asks Roslyn to
bind it there; it does not trust the JSON type labels as proof.

For every referenced definition, the patcher verifies:

- it belongs to the same method;
- its persistent ID, name, and exact semantic type still match;
- it is lexically in scope at the endpoint; and
- it dominates every CFG predecessor that reaches the endpoint.

The containing statement must be reachable. The rebuilt expression must remain
inside the Visual Profile and compile without errors. Graph safety, branch or
return facts, and CFG reducibility are recalculated after each stage.

## Patch-plan steps

Patch plan v0.20 carries direct wrappers named `replaceIfCondition`,
`replaceLocalInitializer`, and `replaceReturnValue`. Their payloads are the
standalone fields plus a unique `stepId`. Like other direct replacements, they
target IDs that exist in the external baseline and are not repeatable against
the same old endpoint in one plan.

When the enclosing statement is created earlier in the same plan, its random
persistent statement and expression IDs cannot be known by the visual editor.
M4.8 therefore adds three producer-handle steps:

- `replaceProducedIfCondition`;
- `replaceProducedLocalInitializer`; and
- `replaceProducedReturnValue`.

Each symbolic edit carries the original producer anchor in `targetNodeId`, an
earlier `sourceStepId`, and only the new expression payload. For example:

```json
{
  "step": "replaceProducedIfCondition",
  "stepId": "refineGeneratedGuard",
  "targetNodeId": "us_original_parent_block",
  "sourceStepId": "insertGuard",
  "newCondition": {
    "expression": "literal",
    "valueText": "false",
    "expectedTypeId": "us_bool_type"
  }
}
```

Before execution, the plan validator proves that the source step is an earlier
compatible producer in the same original method and under the same original
anchor:

- an `if` insertion produces an editable condition;
- a `localLiteralDeclaration` insertion produces an editable initializer; and
- a value-bearing `return` insertion produces an editable return value.

Those producers can be root `insertStatement` steps or statements inserted
through a generated branch or loop-body handle. A bare return is not a value
producer. `introduceTemporary` does not currently publish an initializer-edit
handle; it continues to publish only its existing reconnectable local handle.

After every stage, the coordinator reanalyzes the current in-memory source and
records the actual generated IDs. A produced-endpoint edit resolves the current
statement and current expression endpoint, constructs the corresponding normal
patch operation, and runs the same source patcher. The same producer handle can
be edited repeatedly: each stage resolves the expression produced by the
previous stage, so no intermediate ID needs to escape the transaction.

A return terminates its generated path for later statement appends, but its own
value endpoint remains editable. This is not an append after a terminal
statement; it is a replacement inside that terminal statement.

## Identity and layout

Endpoint editing pins the structures that the user did not ask to recreate:

- the local declaration and its symbol definition;
- the `if`, its `then` block, and its optional `else` block;
- the return statement;
- the containing method and block; and
- unaffected sibling statements and expression nodes.

Expression descendants are reconciled by structural, shape, and context
evidence. An unambiguous equivalent may keep its persistent ID; removed nodes
become tombstones; new nodes receive new IDs. The enclosing statement ID is
guaranteed to remain live.

Graph layout synchronization therefore keeps existing structural nodes placed,
marks genuinely new expression nodes as `unplaced`, and does not create false
orphans for the declaration, branch, blocks, or return being edited.

## Lossless-source guard

An endpoint replacement discards the old expression syntax. If that syntax
contains a comment or directive, the operation is rejected because the patcher
cannot infer a lossless destination for the trivia. Boundary whitespace is
preserved, while only the newly generated expression interior is normalized.

This rule applies equally to local initializers, `if` conditions, return values,
assignment values, and `while` conditions.

## Checked-in CLI sample

The sample begins with:

```csharp
int result = value + 1;
if (enabled)
{
    return result;
}
```

Run its three-step direct plan:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/EditDecisionValues.cs \
  --identity samples/Patching/EditDecisionValues.cs.usids \
  --plan samples/Patching/EditDecisionValues.plan.json \
  --out artifacts/general-expression-edit/EditDecisionValues.cs \
  --out-identity artifacts/general-expression-edit/EditDecisionValues.cs.usids
```

The output contains:

```csharp
int result = value - 2;
if (enabled && (result > stopAt))
{
    return result + stopAt;
}
```

The declaration, `if`, branch block, and return identities stay live. The
sidecar advances once per internal stage, from generation 1 to generation 4,
and the final source is committed only after every stage succeeds.

## Exact nested input follow-up

M4.10 now complements these complete-endpoint operations with
`replaceExpressionInput`. It edits binary `left`/`right`, bounded unary or cast
`operand`, conditional selector/arm ports, null-conditional `receiver`, and
resolved positional invocation `argN` ports without rebuilding
the complete endpoint. It preserves the unaffected expression spine and uses
the same type, scope, dominance, effect, trivia, Graph, and identity pipeline.
See [EXPRESSION_INPUT_PATCHING.md](EXPRESSION_INPUT_PATCHING.md).

## Deliberate limits

An ordinary `localReference` still addresses only definitions present in the
external source baseline. Plan v0.20 adds a transaction-only
`producedLocalReference` leaf for locals created by earlier steps; it resolves
to a normal definition ID before source patching and retains all scope and CFG
dominance rules. See
[PRODUCED_VALUE_REFERENCES.md](PRODUCED_VALUE_REFERENCES.md). M4.11 also
publishes transaction-local roots for authored expression trees; later stages
can traverse those roots through bounded structural ports and reuse the normal
input-edit operation. See
[PRODUCED_EXPRESSION_HANDLES.md](PRODUCED_EXPRESSION_HANDLES.md).

General calls, ordinary member/indexer access, assignment expressions,
increment/decrement, lambdas, `await`, and other effectful or evaluation-order
sensitive forms remain outside the authored expression algebra. M4.15 admits
the bounded `receiver?.Member` form while keeping its selected branch read-only.
Void/task-like return editing, declaration type or name changes, branch
restructuring, moving statements, and automatic graph placement are also out
of scope.

Generated expression roots publish symbolic node/port handles for bounded
binary/unary/cast/conditional paths. Member receivers, invocation targets,
assignment/mutation, and await inputs remain outside exact input replacement.
Text-authored `for` has an explicit lossless HIR/Graph design, and M4.17 adds a
bounded whole-loop constructor. Exact restructuring/replacement of clauses on
an already existing `for` remains outside authored patch payloads.
Indexed and synchronous enumerable `foreach` now share one lossless HIR/Graph
design and exact collection endpoint; unsupported pattern/ref/async enumerator forms and `switch` remain outside this
expression-editing slice. See [FOREACH_LOOPS.md](FOREACH_LOOPS.md).
