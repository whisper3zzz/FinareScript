# Produced Value References

## Purpose

M4.9 lets a later patch-plan expression consume a local variable created by an
earlier step without predicting that declaration's random persistent ID. This
closes the first transaction-time data-flow gap between structural graph
construction and typed expression authoring.

The feature belongs to `unifiedscript.patch-plan/v0.14`. The standalone patch
protocol is `unifiedscript.patch/v0.13`: a standalone patch has no earlier
step namespace, so it explicitly rejects transaction-only produced references.

No second expression interpreter is introduced. Before each stage executes,
the coordinator resolves a symbolic reference to the generated declaration's
real `SemanticId` and rewrites it into an ordinary `LocalReferencePatchExpression`.
The existing source patcher then performs the same Roslyn binding, lexical
scope, CFG dominance, exact type, Graph, and CFG checks used by every other
local reference.

## Protocol shape

A produced reference is one leaf in the existing closed expression algebra:

```json
{
  "expression": "producedLocalReference",
  "sourceStepId": "insertOffset",
  "expectedName": "offset",
  "expectedTypeId": "us_int_type"
}
```

It differs from an ordinary local reference only in how it addresses the
definition:

| Expression | Address | When the definition exists |
| --- | --- | --- |
| `localReference` | `sourceDefinitionId` | In the external source baseline |
| `producedLocalReference` | `sourceStepId` | After an earlier plan stage succeeds |

The expected name and type are not hints. They are optimistic semantic
preconditions checked against both the producer payload and the actual
reanalyzed declaration.

## Local producers

The following earlier steps publish a local-value handle under their `stepId`:

- `introduceTemporary`;
- root `insertStatement` with `localLiteralDeclaration`;
- `insertBranchStatement` with `localLiteralDeclaration`; and
- `insertLoopBodyStatement` with `localLiteralDeclaration`.

An `if`, `while`, assignment, return, break, or continue insertion is not a
local producer. A future or unknown `sourceStepId` is rejected while validating
the plan JSON, before source analysis or execution starts.

The producer must declare exactly the same `expectedName` and `expectedTypeId`
as every reference that consumes it. Type inference across plan steps is
deliberately not used; the visual editor must serialize the type already proven
by its graph model.

## Consumer positions

`producedLocalReference` may appear at any depth inside the existing
literal/local/unary/binary/cast/conditional algebra carried by a patch-plan step. Supported
consumer roots include:

- direct `replaceAssignmentValue`;
- direct or produced `replaceWhileCondition`;
- direct or produced `replaceIfCondition`;
- direct or produced `replaceLocalInitializer`;
- direct or produced `replaceReturnValue`;
- root `insertStatement` payloads for local assignment, `if`, `while`, or
  value return;
- generated `insertBranchStatement` payloads; and
- generated `insertLoopBodyStatement` payloads.

For example, a generated local can feed a later generated guard:

```json
{
  "step": "insertStatement",
  "stepId": "insertGuard",
  "targetNodeId": "us_method_body",
  "expectedPreviousSiblingId": null,
  "expectedNextSiblingId": "us_fallback_return",
  "newStatement": {
    "statement": "if",
    "condition": {
      "expression": "binary",
      "operator": ">",
      "left": {
        "expression": "producedLocalReference",
        "sourceStepId": "insertOffset",
        "expectedName": "offset",
        "expectedTypeId": "us_int_type"
      },
      "right": {
        "expression": "literal",
        "valueText": "0",
        "expectedTypeId": "us_int_type"
      },
      "expectedTypeId": "us_bool_type"
    }
  }
}
```

The same source may be referenced more than once and from more than one later
step. Each occurrence is independently checked at its actual C# position.

## Validation phases

### Plan protocol validation

While reading plan v0.14, the validator walks every expression recursively and
requires each produced reference to:

1. name a non-empty earlier `stepId`;
2. select a step that actually produces a local;
3. repeat the producer's exact declared name; and
4. repeat the producer's exact semantic type ID.

For ordinary payload-shape validation, the validator temporarily substitutes a
non-executable placeholder local ID. This only allows the existing standalone
operation validator to verify expression depth, operators, operands, payload
types, and statement shape. The placeholder never reaches C# generation.

### Initial source validation

Before stage one executes, the C# plan coordinator verifies that the consumer
and every referenced local producer target the same method. Cross-method and
cross-document value handles are rejected before any in-memory stage runs.

The source compilation baseline, semantic configuration baseline, target kind,
target document, and persistent sidecar requirement are also checked normally.

A consumer containing a produced reference cannot be fully applied to the
unchanged source during this phase because its declaration does not exist yet.
Its complete endpoint, gap, source-availability, and semantic checks therefore
run at its actual stage against the reanalyzed in-memory source. A failure still
rolls back the complete external transaction.

### Execution-time materialization

After a local-producing stage succeeds, the coordinator finds the one generated
`HirVariableDeclarationStatement` and records its real persistent ID under the
producer's `stepId`.

Immediately before a consumer stage, every produced reference is rewritten to:

```json
{
  "expression": "localReference",
  "sourceDefinitionId": "us_actual_generated_definition",
  "expectedName": "offset",
  "expectedTypeId": "us_int_type"
}
```

The resulting operation is an ordinary patch v0.13 operation. The standalone
source patcher never needs to understand a plan handle.

## Scope and CFG dominance

Resolving a handle proves identity, not availability. The normal local-reference
rules still require the generated declaration to:

- belong to the same method;
- have the expected name and exact graph-safe scalar type;
- be lexically visible at the consumer position;
- precede the consumer where required by C# scope; and
- dominate every CFG predecessor reaching the consumer.

Consequently:

- a root-block local may feed later root statements and nested branches/loops;
- a branch local may feed later descendants in that same branch;
- a branch local cannot feed its sibling branch or escape to the outer block;
- a loop-body local cannot feed the loop header or code outside its lexical
  body; and
- a generated local cannot reference itself from its own initializer.

These are semantic rejections, not best-effort graph rewrites. The plan returns
no patched request or analysis when any occurrence is unavailable.

## Identity, Graph, and rollback

The producer declaration receives its normal generated persistent ID. Every
materialized reference becomes an ordinary C# identifier and receives its own
expression ID while binding back to the declaration through
`referencedSymbolId` in bound semantics and Graph data edges.

Existing statements and graph nodes retain their identity pins. New declaration,
condition, branch, return, and identifier nodes enter graph layout as
`unplaced`; surviving original nodes keep their placements. Removed expression
endpoints become sidecar tombstones according to the normal reconciliation
rules.

Every stage still runs parse, Visual Profile, compilation, binding, identity,
Graph projection, and CFG validation. The CLI writes the final source/sidecar
pair only after all stages succeed.

## Checked-in CLI sample

[ConnectGeneratedValues.cs](../samples/Patching/ConnectGeneratedValues.cs)
starts with one fallback return. Its four-stage plan:

1. inserts `int offset = 2;`;
2. consumes `insertOffset` inside a generated if condition;
3. consumes it again inside a generated branch return; and
4. consumes it inside a direct edit of the original fallback return.

Run it with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/ConnectGeneratedValues.cs \
  --identity samples/Patching/ConnectGeneratedValues.cs.usids \
  --plan samples/Patching/ConnectGeneratedValues.plan.json \
  --out artifacts/produced-values/ConnectGeneratedValues.cs \
  --out-identity artifacts/produced-values/ConnectGeneratedValues.cs.usids
```

The final ordinary C# is:

```csharp
int offset = 2;
if (enabled && (offset > stopAt))
{
    return value + offset;
}
else
{
}
return value - offset;
```

The sidecar advances from generation 1 to generation 5. The superseded
fallback value becomes one tombstone; its enclosing return and all original
method/block/parameter structures remain live.

## Deliberate limits

The symbolic leaf addresses local definitions only. It does not represent an
arbitrary generated expression result, field, property, invocation output,
branch merge, loop-carried value, or cross-method value. It cannot point to the
same step or a future step, and it does not weaken C# lexical scope or CFG
dominance.

The authored expression algebra remains bounded to scalar literal, local,
unary, binary, and cast nodes. Calls, member/indexer access, assignment as an
expression, conditional access, lambdas, and `await` are still outside this
data-flow authoring slice.

M4.10 builds on this handle resolution through `replaceExpressionInput`: a
produced local can feed one exact nested Graph port without rebuilding its
entire enclosing endpoint. The edit retains explicit old-port IDs, type/effect
and evaluation-order proofs, lossless trivia handling, and stable identity for
the unaffected expression spine. See
[EXPRESSION_INPUT_PATCHING.md](EXPRESSION_INPUT_PATCHING.md).

M4.11 now publishes a separate transaction handle for the root of every
plan-authored expression. Later `replaceProducedExpressionInput` steps traverse
that authored tree with canonical port paths, without changing the local-leaf
contract described here. See
[PRODUCED_EXPRESSION_HANDLES.md](PRODUCED_EXPRESSION_HANDLES.md).
