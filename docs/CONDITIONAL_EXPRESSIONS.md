# Conditional Expressions

M4.12 adds C# conditional expressions (`condition ? whenTrue : whenFalse`) as a
lossless vertical slice across text, HIR, bound semantics, Graph, source
patching, persistent identity, and transaction expression handles. The feature
uses the existing Roslyn/CoreCLR language path; it does not introduce a Graph
evaluator or desugar the expression into an eager function call.

The current contracts are:

- HIR `unifiedscript.hir/v0.3`;
- Graph `unifiedscript.graph/v0.5`;
- standalone patch `unifiedscript.patch/v0.13`;
- patch plan `unifiedscript.patch-plan/v0.14`.

## HIR and semantic contract

`HirConditionalExpression` owns three ordered expression children:

1. `Condition`;
2. `WhenTrue`;
3. `WhenFalse`.

Lowering preserves the C# syntax directly. It does not rewrite the expression
into an `if`, duplicate it into two returns, or encode it as an invocation.
Traversal, semantic binding, structural identity reconciliation, expression
location lookup, and JSON serialization all recognize the same node.

Roslyn remains authoritative for the result type and ordinary C# conversion
rules of a text-authored expression. The condition must bind as Boolean under
C# rules. Both arms are bound independently, and the conditional node carries
Roslyn's resolved result type.

## Lazy branch safety

C# evaluates the condition first and evaluates exactly one arm. A normal data
graph could accidentally evaluate both arm subgraphs before a select node, so
Graph projection admits conditional arms only when they contain no observable
effects in the current effect model. Assignment, `await`, increment/decrement,
and calls bound with non-`None` effects produce diagnostic `UVS2002`.

The condition may contain effects because it always executes. Its projected
effect chain is sequenced before the containing statement. The two arms must
remain effect-free so representing their values as data dependencies cannot
change observable execution.

When a branch needs mutation, latency, or an effectful call, author an explicit
`if`/`else` statement instead. That form has control regions and can sequence
each branch independently.

## Graph contract

Graph v0.5 projects one node with:

- `kind: "conditional"`;
- label `Select`;
- data inputs `condition`, `whenTrue`, and `whenFalse`;
- data output `value`;
- property `evaluation: "lazyBranches"`.

Every child projects through the ordinary expression path, and one data edge
connects its value to the matching canonical port. The property is an explicit
consumer contract: a future visual runtime or editor must preserve lazy arm
selection even though the currently admitted arms are effect-free.

## Graph-authored patch expression

Patch v0.13 extends the closed expression algebra with this payload:

```json
{
  "expression": "conditional",
  "condition": {
    "expression": "localReference",
    "sourceDefinitionId": "us_enabled_parameter",
    "expectedName": "enabled",
    "expectedTypeId": "us_boolean_type"
  },
  "whenTrue": {
    "expression": "localReference",
    "sourceDefinitionId": "us_value_parameter",
    "expectedName": "value",
    "expectedTypeId": "us_integer_type"
  },
  "whenFalse": {
    "expression": "literal",
    "valueText": "0",
    "expectedTypeId": "us_integer_type"
  },
  "expectedTypeId": "us_integer_type"
}
```

Static JSON validation requires all three children and requires both arm
payloads to declare the conditional result type. Source application then
requires the condition's semantic type to be exactly `System.Boolean` and both
arm types to exactly equal the declared result type. These exact patch-time
rules intentionally avoid silently applying target-typed or user-defined
conversions while editing a graph-authored tree.

Roslyn syntax construction adds parentheses around composite children when
needed, then reruns profile validation, C# compilation, semantic binding,
identity reconciliation, and Graph projection before returning any source.

## Exact input editing and generated handles

`replaceExpressionInput` accepts the same canonical ports emitted by Graph:

- `condition`;
- `whenTrue`;
- `whenFalse`.

The operation repeats the current child ID, child type, and conditional result
type. It reuses the ordinary stable-pure removal, trivia, reachability, local
scope/dominance, and persistent-identity checks. The conditional node,
containing statement, unaffected arms, and surrounding expression spine retain
their IDs; only the removed subtree becomes tombstones.

Patch-plan v0.14 also allows these ports in `targetPath` and `inputPort` for
`replaceProducedExpressionInput`. A stage may author a conditional, a later
stage may replace `whenFalse`, and a third stage may use the second stage's
published expression root to edit a nested binary input. No generated ID is
serialized in the plan.

## Checked-in round trip

The sample begins with `return value;`, authors a conditional, replaces its
generated false arm with `value + 1`, and then changes the generated offset to
`2`:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/EditConditionalExpression.cs \
  --identity samples/Patching/EditConditionalExpression.cs.usids \
  --plan samples/Patching/EditConditionalExpression.plan.json \
  --out artifacts/conditional-expression/EditConditionalExpression.cs \
  --out-identity artifacts/conditional-expression/EditConditionalExpression.cs.usids
```

The committed result is ordinary C#:

```csharp
return enabled ? value : (value + 2);
```

Reanalysis emits a `conditional` Graph node, preserves the return identity,
advances the sidecar from generation 1 to 4, and records the replaced generated
subtrees as ordinary tombstones.

## Deliberate limits

- Effectful arms are rejected; use statement-level `if`/`else` for them.
- Graph-authored arms require exact result types rather than implicit numeric,
  reference, nullable, tuple, or user-defined conversions.
- The closed authored algebra still excludes new call/member construction,
  assignment, and `await`.
- Null-conditional access (`?.`), null coalescing (`??`), switch expressions,
  pattern arms, and conditional `ref` expressions remain outside this slice.
- Short-circuit expression control flow and exception edges are not yet
  materialized as standalone CFG regions.
