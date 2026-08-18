# Null-Coalescing Expressions

M4.14 adds C# null coalescing (`left ?? right`) to the explicit lazy-expression
contract shared by source, HIR, bound semantics, Graph, and graph-authored
editing. It uses the existing Roslyn/CoreCLR language path and does not lower
the operation into a Boolean test, conditional call, or editor-only evaluator.

The current contracts are:

- HIR `unifiedscript.hir/v0.6`;
- Graph `unifiedscript.graph/v0.10`;
- standalone patch `unifiedscript.patch/v0.17`;
- patch plan `unifiedscript.patch-plan/v0.18`.

At the M4.14 boundary only Graph changed version: HIR, persistent identity, and
both patch protocols already represented `??` losslessly as an ordered binary
expression with canonical `left` and `right` children. M4.15 subsequently
advanced the current strict-reader versions shown above for null-conditional
access.

## Evaluation and result contract

C# evaluates `left` exactly once. If it is non-null, that value supplies the
result and `right` is skipped. Otherwise C# evaluates `right` and uses its
value. Graph v0.7 introduced all three decisions; current Graph v0.10 retains
them on the existing binary node:

```json
{
  "kind": "binary",
  "properties": {
    "operator": "??",
    "evaluation": "shortCircuitRight",
    "rightEvaluation": "whenLeftNull",
    "resultSelection": "leftNonNullOrRight"
  }
}
```

The node retains data inputs `left` and `right` plus data output `value`. A
visual runtime or shared VM must evaluate the left edge first, test its null
state, and follow only the selected value path. It must not evaluate both
inputs eagerly. For nullable value types, selecting the non-null left value
means selecting the contained `T`, not the `Nullable<T>` wrapper, whenever the
bound result type is non-nullable.

Each expression node still carries its bound `typeId` and `resolvedType`.
Therefore the left, right, and result types remain visible without introducing
a second coalescing-specific type system.

## Roslyn type authority

For text-authored C#, Roslyn remains authoritative for C# legality and the
candidate operand/result types. The Visual Profile then admits only shapes
whose execution requires nullable unwrapping or identity conversion. Typical
admitted forms include:

```csharp
int result = optionalNumber ?? fallback;
string name = optionalName ?? fallbackName;
```

The left node retains its nullable type, while the right node and coalescing
node retain their Roslyn-bound types. All three types must be graph-safe, the
right and result type IDs must match exactly, and the left/result metadata names
must differ by at most the nullable suffix. A legal C# expression that needs an
implicit numeric, reference, or user-defined coalescing conversion produces
`UVS2005` instead of leaving that conversion implicit for the shared VM.

M4.14 does not add flow-sensitive nullable state; it serializes the
declared/bound nullability already present in semantic profile v0.1.

## Graph-authored type boundary

Patch v0.17 reuses `BinaryPatchExpression` with operator `??`. No new payload
or port is required. To avoid hiding conversions from the future shared VM,
visual construction is deliberately narrower than all legal C# forms:

- `left` must declare a nullable graph-safe scalar type;
- `right` must exactly equal the declared result type;
- the left and result types must have the same kind and metadata name after
  removing only the nullable suffix;
- implicit numeric, reference, tuple, dynamic, and user-defined conversions are
  not synthesized;
- the existing bounded scalar exclusions, including `decimal`, still apply.

This matches the text-side execution boundary and admits exact families such
as `T? ?? T` and `T? ?? T?` for the current
Boolean, integer, floating-point, and enum graph-authored subset. Roslyn then
reanalyzes the emitted ordinary C# before any patch result is returned.

Existing `replaceExpressionInput` operations edit `left` or `right` using exact
endpoint and type preconditions. Patch-plan v0.18 can author a coalescing binary
tree, publish its actual root under a step handle, and use
`replaceProducedExpressionInput` on either canonical port without predicting a
generated semantic ID. Persistent identity, trivia safety, tombstones, scope,
dominance, and atomic rollback remain on the existing binary-expression path.

## Effect safety

The left subtree may contain effects because C# always evaluates it exactly
once. Its projected effect chain is sequenced before the containing statement.

The current Graph does not have a conditional effect subgraph for a lazy data
input, so the complete right subtree must be effect-free. Assignment, `await`,
increment/decrement, or a call bound with effects other than `None` produces
semantic diagnostic `UVS2004` before Graph projection:

```csharp
return optional ?? Poll(); // rejected when Poll has effects
```

An effect-catalogued pure call is allowed:

```csharp
return optional ?? Math.Abs(fallback);
```

Purity prevents observable speculative effects in the current model, but a
consumer must still honor `rightEvaluation: whenLeftNull`. When conditional
effects are required, author explicit statement-level control flow.

## Checked-in sample

Generate the sample with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Expressions/CoalesceRules.cs \
  --out artifacts/coalesce
```

The sample covers nullable value and reference types, an effectful
always-evaluated left call, and a catalogued-pure right call. Every coalescing
node emits the Graph v0.10 lazy-right metadata introduced in v0.7.

## Deliberate limits

- Flow-sensitive nullable-state facts are not serialized yet.
- Visual construction does not synthesize implicit or user-defined coalescing
  conversions.
- Conditional right-side effects are rejected instead of materialized as a
  dedicated lazy subregion.
- Null-conditional access (`?.`), null-coalescing assignment (`??=`), `throw`
  expressions, and unconstrained generic coalescing remain outside this slice.
- Coalescing evaluation is explicit on the Graph node but is not duplicated as
  a standalone CFG region.
