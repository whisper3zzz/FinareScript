# Short-Circuit Expressions

M4.13 makes C# conditional logical operators (`&&` and `||`) explicit across
text, HIR, bound effects, and Graph. Both source and visual representations
continue to use the same Roslyn/CoreCLR language path; Graph does not introduce
an eager Boolean helper or a second evaluator.

The current contracts are:

- HIR `unifiedscript.hir/v0.7`;
- Graph `unifiedscript.graph/v0.11`;
- standalone patch `unifiedscript.patch/v0.18`;
- patch plan `unifiedscript.patch-plan/v0.19`.

Only Graph changes in this slice. HIR and the patch protocols already preserve
`&&` and `||` as binary operators with canonical `left` and `right` children.

## Evaluation contract

C# always evaluates the left operand first. It evaluates the right operand of
`&&` only when the left value is `true`, and the right operand of `||` only
when the left value is `false`. Graph v0.6 introduced that rule directly on the
existing binary node; current Graph v0.11 retains the same contract:

| Operator | `evaluation` | `rightEvaluation` |
| --- | --- | --- |
| `&&` | `shortCircuitRight` | `whenLeftTrue` |
| `||` | `shortCircuitRight` | `whenLeftFalse` |

The node retains data inputs `left` and `right` plus data output `value`. A
visual editor, interpreter, or future shared VM must treat the right data edge
as conditional evaluation according to these properties. It must not compute
both inputs eagerly and then apply an ordinary Boolean function.

Non-conditional `&` and `|` remain ordinary eager binary operators. They do
not receive either lazy-evaluation property.

## Effect safety

The left subtree may contain effects because C# always evaluates it. Its effect
nodes remain in the containing statement's control chain before the consumer.

The current Graph has no conditional effect subgraph for a binary data input,
so the complete right subtree must be effect-free. Assignment, `await`,
increment/decrement, or a call bound with effects other than `None` produces
semantic diagnostic `UVS2003` before Graph projection. The check covers nested
right-side expressions, not only the immediate child.

For example, this is accepted because `Poll()` is always evaluated:

```csharp
return Poll() && enabled;
```

This is rejected because `Poll()` would run conditionally:

```csharp
return enabled && Poll();
```

When the right side needs mutation, latency, or an effectful call, express the
operation as statement-level `if`/`else`. Its Graph regions can carry an
independent control chain without losing C# evaluation behavior.

Effect-free calls are permitted when the bound effect catalog explicitly
classifies them as `None`. Regardless of effects, a consumer must still honor
`rightEvaluation`; purity makes speculative execution observationally safe in
the present model but does not erase the language contract.

## HIR, identity, and editing

HIR continues to emit one `HirBinaryExpression` with operator `&&` or `||` and
ordered left/right children. Traversal, semantic binding, persistent identity,
source location, serialization, and structural fingerprints already share that
shape.

Patch v0.18 and patch-plan v0.19 likewise keep logical operators in the closed
typed binary-expression algebra. Existing `replaceExpressionInput` and
`replaceProducedExpressionInput` operations use `left` and `right`, matching
the Graph ports. Source reconstruction emits ordinary C#, reruns semantic
analysis, and applies `UVS2003`; therefore a visual edit cannot introduce an
effectful conditional right operand through a patch.

No patch-schema bump is required because the authored syntax shape and ports
do not change. Graph v0.6 was required because consumers gained a new mandatory
evaluation meaning for these operators; v0.8 retains that contract.

## Checked-in sample

Generate the short-circuit sample with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Expressions/ShortCircuitRules.cs \
  --out artifacts/short-circuit
```

`All` and `Any` demonstrate the two right-side conditions. `PollThenCheck`
demonstrates that an effectful left call remains in the ordinary control chain.
The resulting Graph uses v0.6 and the properties in the table above.

## Deliberate limits

- Conditional effects on the right are rejected instead of being represented
  by a dedicated lazy subregion.
- Null coalescing (`??`) is covered by the subsequent Graph v0.7 contract, and
  null-conditional access (`?.`) by the subsequent Graph v0.8 contract.
- Flow-sensitive nullable state, exception edges, and speculative-allocation
  effects are not modeled yet.
- `&&`/`||` remain binary HIR nodes rather than standalone CFG regions; their
  conditional evaluation is an explicit Graph-node contract.
