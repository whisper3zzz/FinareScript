# Bounded Graph-Authored Loop Patching

## Purpose

M4.6 closes the first bounded loop round trip for `while`; M4.17 extends the
same symbolic protocol to a dedicated `for` constructor, and M4.19 adds the
post-test `do ... while` form. M4.20 adds indexed `foreach`, and M4.21 extends
the same constructor to disposal-aware synchronous enumerable loops. A visual edit can
create ordinary C#, obtain a symbolic handle for its generated body, and
populate that body with the same typed statements used elsewhere. The result
is re-lowered into the current HIR v0.7 and projected into the current Graph
v0.12/CFG loop model; there is no editor-only loop node or second runtime.

This is structured construction, not arbitrary cyclic edge rewiring. The
patch describes a supported structured loop and ordered body statements. The compiler owns the
actual loop/body IDs, collection phases, control edges, back edges, and nearest-loop transfer
targets.

## Protocol

Current patch schema `unifiedscript.patch/v0.28` carries six loop statement payloads:

- `while`, carrying one closed, typed `PatchExpression` condition;
- `doWhile`, carrying one closed, typed post-body condition;
- `for`, carrying declaration or assignment initialization, an optional
  condition, and ordered prefix/postfix `++`/`--` incrementors;
- `forEach`, carrying an explicit iteration variable and a supported string,
  one-dimensional zero-based array, or graph-safe `IEnumerable<T>` collection
  reference;
- `break`, carrying no target ID; and
- `continue`, carrying no target ID.

The absence of a transfer target is intentional. C# and HIR bind each transfer
to its nearest lexical loop, and the derived CFG verifies that target. A visual
client cannot forge an edge to an outer or unrelated loop.

Current patch-plan schema `unifiedscript.patch-plan/v0.21` carries
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
`while`, `doWhile`, `for`, or `forEach`. The consumer repeats the producer's original `targetNodeId`;
it never predicts the generated HIR loop or Block ID.

An `insertLoopBodyStatement` may itself insert another loop or `if`.
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

A generated while or do-while condition uses the same bounded expression algebra as a
generated if condition or assignment value: literal, local reference, unary,
binary, and cast expressions. It must:

- declare the exact semantic ID of `System.Boolean`;
- resolve to that type after Roslyn binding;
- contain only graph-safe types and supported operators;
- be pure under the current effect catalog;
- reference definitions visible at the exact root insertion gap; and
- remain inside the expression nesting-depth limit.

The generated body currently accepts local-literal declarations, assignments
to existing locals, nested `if`, nested `while`/`do`/`for`/`foreach`, typed return, break, and
continue. Every stage retains the statement-specific name, type, scope,
dominance, effect, and reachability checks.

`break` and `continue` are accepted only at the end of an explicit reachable
Block with a discoverable nearest enclosing `HirWhileStatement`,
`HirDoWhileStatement`, `HirForStatement`, or `HirForEachStatement`. This applies equally to direct library patches and symbolic
plan steps. A transfer outside a loop is rejected; a statement appended after
a transfer is rejected as unreachable. A `for` continue is independently
verified to target its increment phase.

## Symbolic rebasing and identity

After a successful loop-producing stage, the plan coordinator finds the actual
inserted `HirWhileStatement`, `HirDoWhileStatement`, `HirForStatement`, or
`HirForEachStatement`, records its persistent loop
and body IDs, and advances the request with the reconciled identity sidecar. A
later body step resolves that current body, appends after its current final
statement, and invokes a normal `InsertStatementPatchOperation`.

Replacing the generated body changes the spans of its enclosing loop and
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

Termination does not propagate through the surrounding loop to its parent
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

Foreach specializes that shape with an ordered `loopCollection` evaluate-once
phase, a move-next header/continue target, and a current-value body entry. The
enumerator strategy adds bound acquisition and a `loopCleanup` region whose
normal, return, and exception disposal nodes preserve pending transfers. Its
generated iteration local is available through the same produced-local
mechanism but remains scoped to the loop body.

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

The bounded `for` constructor and its four-stage CLI sample are documented in
[FOR_LOOPS.md](FOR_LOOPS.md). It reuses the same body handle while additionally
publishing the single generated for-local to later body expressions.
The indexed and enumerable foreach constructors, collection endpoint, cleanup
contract, and three-stage samples
are documented in [FOREACH_LOOPS.md](FOREACH_LOOPS.md).

## Current limits

Loop-body steps append; they cannot move an existing statement into or out of
a loop, select an arbitrary generated-body gap, delete a generated loop, or
retarget a transfer. M4.7 can edit an existing condition or repeatedly edit a
condition through a produced-while handle; see
[STRUCTURED_EXPRESSION_EDITING.md](STRUCTURED_EXPRESSION_EDITING.md). Produced
locals can feed later expressions only where ordinary scope and CFG dominance
hold; generated for-locals therefore stay inside their loop. The implementation
does not prove loop termination or constant-fold conditions.

M4.19 applies the same body-handle and exact-condition model to post-test
`do ... while` while preserving its distinct initial-body entry and
continue-to-condition CFG. See [DO_WHILE_LOOPS.md](DO_WHILE_LOOPS.md).

Pattern-only/conditional-disposal enumerators, ref/async `foreach`, existing-`for` clause restructuring,
arbitrary cyclic Graph rewiring, general compound-assignment/mutation statements, effectful authored
expression trees, exception flow, short-circuit expression CFG expansion,
async state-machine flow, and automatic node placement remain later slices.
