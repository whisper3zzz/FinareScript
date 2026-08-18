# Bounded Graph-Authored Loop Patching

## Purpose

M4.6 closes the first bounded loop round trip. A visual edit can now create an
ordinary C# `while`, obtain a symbolic handle for its generated body, and
populate that body with the same typed statements used elsewhere. The result
is re-lowered into the current HIR v0.5 and projected into the current Graph
v0.9/CFG loop model; there is no editor-only loop node or second runtime.

This is structured construction, not arbitrary cyclic edge rewiring. The
patch describes a `while` and ordered body statements. The compiler owns the
actual loop/body IDs, control edges, back edges, and nearest-loop transfer
targets.

## Protocol

Current patch schema `unifiedscript.patch/v0.14` carries three loop statement payloads:

- `while`, carrying one closed, typed `PatchExpression` condition;
- `break`, carrying no target ID; and
- `continue`, carrying no target ID.

The absence of a transfer target is intentional. C# and HIR bind each transfer
to its nearest lexical loop, and the derived CFG verifies that target. A visual
client cannot forge an edge to an outer or unrelated loop.

Current patch-plan schema `unifiedscript.patch-plan/v0.15` carries
`insertLoopBodyStatement`:

```json
{
  "step": "insertLoopBodyStatement",
  "stepId": "decrementResult",
  "targetNodeId": "us_original_method_body",
  "sourceStepId": "insertLoop",
  "newStatement": {
    "statement": "localAssignment",
    "targetDefinitionId": "us_existing_result_local",
    "expectedName": "result",
    "expectedTypeId": "us_int_type",
    "value": {
      "expression": "literal",
      "valueText": "0",
      "expectedTypeId": "us_int_type"
    }
  }
}
```

`sourceStepId` must refer backward to an earlier insertion whose payload is a
`while`. The consumer repeats the producer's original `targetNodeId`; it never
predicts the generated HIR loop or Block ID.

An `insertLoopBodyStatement` may itself insert another `while` or `if`.
Therefore loop-body and branch handles compose recursively:

```text
root block
└── insertLoop
    └── body
        ├── insertStopGuard
        │   ├── then → break
        │   └── else
        ├── decrementResult
        └── continue
```

## Typed condition and statement boundary

A generated while condition uses the same bounded expression algebra as a
generated if condition or assignment value: literal, local reference, unary,
binary, and cast expressions. It must:

- declare the exact semantic ID of `System.Boolean`;
- resolve to that type after Roslyn binding;
- contain only graph-safe types and supported operators;
- be pure under the current effect catalog;
- reference definitions visible at the exact root insertion gap; and
- remain inside the expression nesting-depth limit.

The generated body currently accepts local-literal declarations, assignments
to existing locals, nested `if`, nested `while`, typed return, break, and
continue. Every stage retains the statement-specific name, type, scope,
dominance, effect, and reachability checks.

`break` and `continue` are accepted only at the end of an explicit reachable
Block with a discoverable nearest enclosing `HirWhileStatement`. This applies
equally to direct library patches and symbolic plan steps. A transfer outside
a loop is rejected; a statement appended after a transfer is rejected as
unreachable.

## Symbolic rebasing and identity

After a successful while-producing stage, the plan coordinator finds the
actual inserted `HirWhileStatement`, records its persistent loop and body IDs,
and advances the request with the reconciled identity sidecar. A later body
step resolves that current body, appends after its current final statement,
and invokes a normal `InsertStatementPatchOperation`.

Replacing the generated body changes the spans of its enclosing while and
method ancestors. Global ancestor identity pins preserve those nodes, while
operation-specific pins preserve the body and existing statements. This keeps
the loop handle valid across all later stages and avoids incidental
tombstones.

The entire plan is still externally atomic. Every stage reparses, validates
the Visual Profile, compiles, binds, lowers, projects, validates CFG
reducibility, and reconciles identity. A late type, scope, reachability, or CFG
failure returns no patched request, analysis, source file, or sidecar.

## Terminal paths and loop boundaries

Within one symbolic body path, direct `return`, `break`, and `continue` are
terminal. If both branches of a generated nested `if` terminate and that `if`
is the body's final statement, the containing body path becomes terminal as
well. Later appends to that body are rejected before execution.

Termination does not propagate through the surrounding `while` to its parent
path: the condition may be false on entry, so code after the loop remains
reachable. Likewise, a terminal inner-loop body does not terminate its outer
loop body. This boundary is what makes nested loop construction compositional
without pretending to solve loop termination.

## Graph and CFG result

Successful output is processed by the M4.5 loop contract:

- one source-backed `loop` node and `loop` region;
- one `loopBody` child region;
- typed condition data flow;
- normal fallthrough and continue back edges to the effective header;
- break and false-condition flow to the same loop exit;
- nearest-loop break/continue ownership; and
- reducible cyclic-component validation.

The layout synchronizer preserves every pre-existing placed node. Newly
authored loop, condition, body, branch, assignment, and transfer nodes are
explicitly `unplaced`, ready for an editor-owned placement pass. Derived CFG
facts remain transient rather than being duplicated in `.usgraph`.

## CLI sample

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/LoopBuildRules.cs \
  --identity samples/Patching/LoopBuildRules.cs.usids \
  --plan samples/Patching/BuildLoop.plan.json \
  --out artifacts/loop-plan/LoopBuildRules.cs \
  --out-identity artifacts/loop-plan/LoopBuildRules.cs.usids
```

The five-stage plan produces:

```csharp
int result = value;
while (enabled && (result > 0))
{
    if (result == stopAt)
    {
        break;
    }
    else
    {
    }
    result = result - 1;
    continue;
}
return result;
```

The output is normal C#, so text edits, HIR/Graph projection, semantic
analysis, identity reconciliation, and eventual execution continue to use the
same program.

## Current limits

Loop-body steps append; they cannot move an existing statement into or out of
a loop, select an arbitrary generated-body gap, delete a generated loop, or
retarget a transfer. M4.7 can edit an existing condition or repeatedly edit a
condition through a produced-while handle; see
[STRUCTURED_EXPRESSION_EDITING.md](STRUCTURED_EXPRESSION_EDITING.md). A patch
expression cannot yet reference a newly inserted local through a symbolic value handle. The
implementation does not prove loop termination or constant-fold conditions.

`do`, `foreach`, whole-`for` construction/restructuring, arbitrary cyclic Graph
rewiring, compound assignment, increment/decrement payloads, effectful authored expressions, exception flow,
short-circuit expression CFG expansion, async state-machine flow, and automatic
node placement remain later slices.
