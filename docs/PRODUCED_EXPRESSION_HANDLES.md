# Produced expression handles

M4.11 lets a later patch-plan stage edit an expression tree authored by an
earlier stage without predicting any of the tree's persistent `SemanticId`
values. The feature is plan-only and belongs to
`unifiedscript.patch-plan/v0.21`. Standalone patches use
`unifiedscript.patch/v0.22` because they have no earlier stage namespace.

This closes an important text/visual round-trip gap. An AI or graph editor can
first replace a return value, condition, initializer, assignment value, or
inserted statement expression, then address an exact port within the generated
tree using only authored structure.

## Handle model

Every plan step with a `PatchExpression` payload publishes the root of the
expression that actually exists after that stage succeeds. The handle name is
the producer's existing `stepId`; no second identifier namespace is needed.

A consumer uses `replaceProducedExpressionInput`:

```json
{
  "step": "replaceProducedExpressionInput",
  "stepId": "replaceGeneratedRight",
  "targetNodeId": "us_original_return_statement",
  "sourceStepId": "replaceReturn",
  "targetPath": [],
  "inputPort": "right",
  "expectedInputTypeId": "us_system_int32",
  "expectedTargetTypeId": "us_system_int32",
  "newInput": {
    "expression": "literal",
    "valueText": "3",
    "expectedTypeId": "us_system_int32"
  }
}
```

The fields mean:

- `targetNodeId` is an original structural anchor shared with the producer. It
  proves that the handle is not being transplanted into another edit context.
- `sourceStepId` names an earlier expression-producing step.
- `targetPath` walks from that step's authored expression root to the generated
  parent expression to edit.
- `inputPort` selects the child of that parent which will be replaced.
- `expectedInputTypeId` and `expectedTargetTypeId` are exact semantic type
  preconditions.
- `newInput` is the same closed typed expression algebra used by ordinary patch
  operations and may itself contain `producedLocalReference` leaves.

## Canonical path language

`targetPath` is an array of existing Graph input-port names, not a source span,
syntax index, JSON pointer, or generated node ID:

- `left` and `right` traverse a `BinaryPatchExpression`;
- `operand` traverses a `UnaryPatchExpression` or `CastPatchExpression`;
- `condition`, `whenTrue`, and `whenFalse` traverse a
  `ConditionalPatchExpression`;
- `receiver` traverses a `ConditionalMemberAccessPatchExpression`;
- an empty path selects the produced root itself.

After the path selects the generated parent, `inputPort` uses the same names.
The current authored `PatchExpression` algebra has no invocation constructor,
so produced paths cannot currently contain `argN`. Ordinary
`replaceExpressionInput` still supports resolved positional invocation ports
on expressions already present in the source baseline.

For example, if an earlier stage authors:

```text
enabled && (value > 0)
```

then `targetPath: ["right"]` selects `value > 0`, and
`inputPort: "right"` selects only its `0` input.

## Two-phase resolution

The plan is checked twice.

At JSON validation time, the reader records the exact resolved
`PatchExpression` tree published by each earlier step. It walks `targetPath`,
checks the selected authored kind and port, compares both declared types, and
validates `newInput` through the ordinary standalone expression-input schema.
Forward references, unknown producers, mismatched original anchors, invalid
paths, invalid ports, and type drift are rejected before source execution.

At stage execution time, the C# plan applier reanalyzes the current source,
looks up the real expression-root ID captured after `sourceStepId`, follows the
same path through current HIR, and reads the real current child ID. It then
constructs an ordinary `ReplaceExpressionInputPatchOperation` and sends it
through the M4.10 source patcher.

Consequently the symbolic feature does not introduce a second edit engine. It
inherits the existing checks for:

- exact parent and child semantic types;
- stable, effect-free removal of the old subtree;
- lexical scope, same-method ownership, reachability, and CFG dominance;
- local name and definition identity;
- precedence-safe expression construction;
- boundary-trivia transfer and unsafe embedded-trivia rejection;
- persistent identity pins on the statement, parent, unaffected spine, and
  siblings;
- ordinary tombstones for removed nodes;
- full profile, semantic, HIR, and Graph regeneration after each stage.

## Expression publication

The applier publishes an actual root after successful expression-bearing
stages, including:

- direct or symbolic assignment-value replacements;
- direct or symbolic while/if-condition replacements;
- direct or symbolic local-initializer replacements;
- direct or symbolic return-value replacements;
- local-assignment, `if`, `while`, and value-return statement insertions;
- ordinary `replaceExpressionInput` steps;
- `replaceProducedExpressionInput` itself.

The last item makes handles chainable. If step B replaces an input created by
step A, step C can name B and edit inside B's newly inserted subtree. A consumer
therefore always traverses the authored tree of its immediate producer, which
keeps paths short and deterministic.

Local literal declarations do not publish an expression handle because their
initializer is currently a scalar `valueText`, not a `PatchExpression`; they
continue to publish their existing produced-local definition and initializer
handles.

## Atomicity and identity

All stages still form one transaction. Intermediate source, semantic models,
sidecars, and captured expression roots remain in memory. If a later symbolic
path cannot be resolved, a referenced local is unavailable, or the ordinary
input edit rejects the operation, the caller receives diagnostics with no
patched request or analysis. Earlier stage output is not committed.

Generated expression IDs are observed only after each successful reanalysis.
They never appear in the plan payload. Once observed, they are used internally
to find the current HIR node at the next stage. The final sidecar remains the
sole persistent identity authority.

## Checked-in chain

`samples/Patching/EditGeneratedExpression.plan.json` starts with:

```csharp
return value;
```

It performs three stages:

1. `replaceReturn` authors `value + 1`.
2. `replaceGeneratedRight` uses the first step's root handle to replace `1`
   with `value * 2`.
3. `replaceNestedGeneratedRight` uses the second step's newly published root
   handle to replace `2` with `3`.

The result is:

```csharp
return value + (value * 3);
```

Run it with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/EditGeneratedExpression.cs \
  --identity samples/Patching/EditGeneratedExpression.cs.usids \
  --plan samples/Patching/EditGeneratedExpression.plan.json \
  --out artifacts/generated-expression/EditGeneratedExpression.cs \
  --out-identity artifacts/generated-expression/EditGeneratedExpression.cs.usids
```

The checked-in sidecar begins at generation 1; the three-stage result reaches
generation 4.

## Deliberate limits

The handle identifies one expression root plus a bounded structural path. It
does not expose arbitrary descendant searches, wildcard paths, parent steps,
source offsets, graph-layout coordinates, or cross-method references. Paths
cannot enter member targets, invocations, assignments, or awaits because those
shapes are not constructible by the current `PatchExpression` algebra.

This is intentionally narrower than general graph rewiring. It provides a
deterministic, AI-friendly address for trees the plan itself authored while
preserving one HIR, one semantic model, one Graph projection, and one eventual
CoreCLR execution path.
