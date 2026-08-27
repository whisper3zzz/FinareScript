# Exact Expression Input Patching

## Purpose

M4.10 adds an exact Graph-input edit that replaces one nested expression
subtree without rebuilding its enclosing assignment, condition, initializer,
return value, or invocation. The standalone and transaction protocols are
`unifiedscript.patch/v0.24` and `unifiedscript.patch-plan/v0.21`.

The operation uses the same input-port vocabulary already emitted by the Graph
projector. It therefore models a visual connection change directly instead of
introducing a second syntax path or a second evaluator.

## Operation

```json
{
  "operation": "replaceExpressionInput",
  "targetNodeId": "us_parent_expression",
  "inputPort": "right",
  "expectedInputNodeId": "us_old_child_expression",
  "expectedInputTypeId": "us_int_type",
  "expectedTargetTypeId": "us_int_type",
  "newInput": {
    "expression": "localReference",
    "sourceDefinitionId": "us_offset_definition",
    "expectedName": "offset",
    "expectedTypeId": "us_int_type"
  }
}
```

The plan form adds `stepId` and uses `step: "replaceExpressionInput"`. Its
`newInput` may contain a transaction-only `producedLocalReference` just like
the other expression-bearing plan steps.

## Port contract

The first slice accepts these canonical Graph data-input names:

| Target HIR/Graph expression | Accepted port | Current endpoint |
| --- | --- | --- |
| binary | `left` | left operand |
| binary | `right` | right operand |
| bounded pure unary | `operand` | unary operand |
| cast | `operand` | cast operand |
| conditional | `condition` | Boolean selector |
| conditional | `whenTrue` | selected-when-true arm |
| conditional | `whenFalse` | selected-when-false arm |
| conditional access | `receiver` | always-evaluated receiver |
| resolved positional invocation | `arg0`, `arg1`, ... | corresponding argument |

`argN` is zero-based and canonical. Spellings such as `arg01`, negative
indices, missing indices, and out-of-range arguments are rejected. Ordinary
member receivers, conditional `whenNotNull` bindings, invocation targets,
assignment targets/values, await operands, and mutation operands are
deliberately excluded. M4.15 exposes only the receiver owned by a first-class
conditional-access node.

The parent expression is `targetNodeId`; the old child connected to the chosen
port is `expectedInputNodeId`. Both IDs are required. This makes a stale visual
edge fail even when the parent node itself still exists.

## Type and shape proof

The patch carries two exact semantic types:

- `expectedInputTypeId` is the current port type and the required type of
  `newInput`;
- `expectedTargetTypeId` is the current result type of the parent expression.

The source patcher verifies the bound type of both the parent and old child.
For an invocation it additionally resolves the invoked method, requires a
non-expanded positional argument list whose count equals the parameter count,
and checks the selected parameter type. The replacement is built through the
closed graph-authored expression algebra and must produce exactly the input
type. Finally, the entire source is compiled and rebound before the patch can
succeed.

No implicit numeric conversion, overload guess, name-only connection, or
string substitution is accepted at the patch boundary.

## Scope, dominance, and effects

`newInput` uses the existing
literal/local/unary/binary/cast/conditional/conditional-member-access algebra. Every
ordinary or produced local reference therefore reuses the normal checks:

- same method;
- persistent source-definition ID;
- exact expected name and type;
- lexical visibility;
- reachable containing statement;
- CFG dominance of the consumer position;
- graph-safe scalar expression construction.

The old input subtree must satisfy the existing stable-pure proof. State/member
reads, assignment, await, increment/decrement, unknown or effectful calls,
division/remainder, short-circuit evaluation, and unstable types reject with
`UVS3011`. This prevents a visual data-edge edit from silently deleting or
reordering observable C# evaluation or removing a potentially throwing
subtree.

The authored replacement algebra is itself pure and bounded to 16 nested
nodes. Parentheses are added around a composite replacement when necessary so
the new subtree remains one input regardless of C# operator precedence.

## Trivia and localized source edits

The implementation locates the selected input and its containing statement by
their analyzed Roslyn spans. Boundary trivia belonging to the old input is
transferred to the replacement, so this remains lossless:

```csharp
return value + /* keep input note */ 1;
```

Replacing `right` with `2` produces:

```csharp
return value + /* keep input note */ 2;
```

Comments or directives embedded inside a larger discarded subtree are rejected
when they cannot be transferred without changing their structural meaning.
The API remains non-mutating on every rejection.

## Persistent identity and Graph synchronization

Only the old input subtree is unpinned. The patch explicitly pins:

- the containing statement;
- the targeted parent expression;
- every surviving ancestor on the expression spine;
- every unchanged sibling expression;
- surrounding structures already carried by the input sidecar.

IDs from the removed subtree become tombstones. New authored expression nodes
receive new IDs. The parent node and unaffected spine keep their old IDs, so a
visual layout can retain its nodes while only the selected incoming edge and
new subtree require synchronization.

After reanalysis, the ordinary Graph projector emits the new data edge using
the same port named by the patch. No Graph-only executable representation is
created.

## Atomic plan composition

The checked-in sample starts with:

```csharp
return (value + 1) * 3;
```

Its two plan stages are:

1. insert `int offset = 2;` before the return;
2. replace the inner addition's `right` input with a
   `producedLocalReference` to `offset`.

Run it with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/EditNestedInput.cs \
  --identity samples/Patching/EditNestedInput.cs.usids \
  --plan samples/Patching/EditNestedInput.plan.json \
  --out artifacts/nested-input/EditNestedInput.cs \
  --out-identity artifacts/nested-input/EditNestedInput.cs.usids
```

The committed result is:

```csharp
int offset = 2;
return (value + offset) * 3;
```

The coordinator first resolves the generated declaration's actual persistent
ID, rewrites the transaction-only leaf to an ordinary local reference, and then
runs the normal standalone `replaceExpressionInput` operation. The sidecar
advances from generation 1 to 3 and records the superseded literal as one
tombstone. If port, endpoint, type, scope, dominance, effect, syntax, compile,
Graph, or identity validation fails at the second stage, the first stage is not
returned or written.

## Deliberate limits

M4.10 addresses inputs on expressions already present in the external
baseline. M4.11 adds the plan-only `replaceProducedExpressionInput` step for
trees created by earlier stages. It uses `sourceStepId`, a canonical
`targetPath`, and `inputPort`, then resolves to the same ordinary operation
described here. See
[PRODUCED_EXPRESSION_HANDLES.md](PRODUCED_EXPRESSION_HANDLES.md).

One patch document still permits only one operation per target node, and direct
plan steps keep non-structural targets unique. A produced-expression consumer
may repeat its producer's original anchor because it resolves a transaction
handle rather than replaying an external old-value precondition. Multiple ports
on the same existing baseline parent must still be edited against freshly
analyzed sequential baselines.

Ordinary member/indexer receivers, conditional-access `whenNotNull` bindings,
invocation targets, assignments, mutation, await, lambdas, object construction,
and arbitrary call construction remain outside the editable input set.
Produced-expression paths are intentionally limited to binary `left`/`right`,
unary/cast `operand`, conditional `condition`/`whenTrue`/`whenFalse`, and
conditional-member-access `receiver` shapes
constructible by the current patch-expression algebra; broadening that algebra
or admitting effectful rewrites remains future work. See
[CONDITIONAL_EXPRESSIONS.md](CONDITIONAL_EXPRESSIONS.md) for lazy-arm safety.
