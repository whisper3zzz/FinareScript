# Recursive Nested Branch Patching

## Purpose

M4.4 makes multi-level acyclic decisions authorable from the visual side while
preserving one source of truth. A nested branch is not an editor-only compound
node and does not introduce a second control-flow runtime. It is an ordinary
typed `IfPatchStatement` inserted into a previously generated C# `then` or
`else` Block, followed by the same full C# → HIR → semantics → Graph → CFG
pipeline used by every other edit.

Patch-plan schema `unifiedscript.patch-plan/v0.8` extends the existing symbolic
branch-handle model recursively. No new step discriminator is required.

## Recursive producer model

An initial `insertStatement` with an `if` payload publishes two internal
handles:

```text
insertEnabledBranch
├── then
└── else
```

An `insertBranchStatement` may consume either handle and append another `if`.
That step's own `stepId` then becomes a new producer:

```text
insertEnabledBranch
├── then
│   └── insertCriticalBranch
│       ├── then
│       └── else
└── else
```

The JSON shape stays bounded and explicit:

```json
{
  "step": "insertBranchStatement",
  "stepId": "insertCriticalBranch",
  "targetNodeId": "us_original_method_body",
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

`sourceStepId` must name an earlier if-producing insertion. Backward-only
references make the producer graph acyclic by construction. Every descendant
repeats the same initial `targetNodeId`; that ID is the stable source document,
method, sidecar, and optimistic-concurrency anchor, not a generated Block ID.

## Ordered execution and rebasing

For every producer stage, the coordinator:

1. resolves the current generated parent Block from the source producer and
   selected branch;
2. appends a normal `InsertStatementPatchOperation` after its current last
   statement;
3. runs complete profile, compiler, semantic, HIR, Graph, and CFG validation;
4. reconciles and adopts the new identity sidecar;
5. proves the inserted statement is an HIR `if` with explicit `Then` and
   `Else` Blocks; and
6. records the actual persistent Block IDs under the new producer `stepId`.

The editor never predicts the ID of the outer `if`, nested `if`, or any of
their Blocks. A failed stage returns no intermediate request or analysis, and
the CLI commits neither output file.

Each symbolic path has its own append order. A later step may continue the
outer sibling branch, one nested leaf, or a deeper descendant without
disturbing the other paths.

## Terminal-path propagation

A direct `return` marks only its selected `(sourceStepId, branch)` path as
terminal. Later appends to that path are invalid; the sibling remains writable.

Nested termination requires one additional proof. If both child branches of a
nested `if` terminate, execution cannot fall through that `if`. Its parent path
therefore becomes terminal only when the nested `if` is still the parent's
last appended statement:

```text
parent path
└── nested if            ← last statement
    ├── then → return
    └── else → return

result: parent path is terminal
```

If another statement was appended after the nested `if`, later terminating its
second child would make that following statement unreachable:

```text
parent path
├── nested if
│   ├── then → return
│   └── else → return    ← completing this pair is invalid
└── later statement      ← would become unreachable
```

Plan v0.8 tracks the last statement on every symbolic path and rejects this
retroactive-unreachability shape before executing the first stage. The rule
propagates recursively: terminal leaves can close their parent, which can close
its parent, through arbitrary acyclic depth.

This tree check complements, rather than replaces, the runtime CFG. Each stage
still re-derives reachability, dominance, merge, exit, and reducibility facts
from the actual current HIR.

## Identity and layout stability

Editing an inner Block changes source spans for every enclosing syntax node,
but it does not semantically replace those ancestors. The identity pin boundary
therefore distinguishes two relations:

- syntax contained by the replaced Block belongs to the local replacement and
  uses operation-specific pins;
- unaffected syntax outside or containing that Block receives global pins.

The second category includes enclosing `if` statements and parent Blocks. This
ancestor rule prevents a nested stage from reallocating an earlier producer's
ID and leaving false tombstones. It also keeps symbolic handles resolvable in
later stages.

After a successful recursive plan:

- every pre-existing identity remains live;
- every earlier produced branch keeps its reconciled identity;
- only genuinely new nodes receive new IDs;
- no incidental tombstones are produced;
- existing Graph layout remains `placed`; and
- newly authored Graph nodes remain explicitly `unplaced`.

## Type, scope, and CFG safety

Nested conditions reuse the closed graph-authored expression algebra. They must
bind to `System.Boolean`, remain pure under the current effect model, and use
only parameters or locals available at the exact generated insertion gap.

Leaf assignments retain every M4.3 guard:

- existing same-method local target;
- persistent target ID, expected name, and exact graph-safe type;
- lexically visible definition;
- CFG-reachable insertion gap;
- target and right-hand-side local definitions dominating every gap anchor;
  and
- pure, bounded, exactly typed right-hand side.

Nested terminal returns retain exact method-return-type validation and the
non-async/task-like limit.

## CLI sample

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/NestedDecisionRules.cs \
  --identity samples/Patching/NestedDecisionRules.cs.usids \
  --plan samples/Patching/BuildNestedDecision.plan.json \
  --out artifacts/nested/NestedDecisionRules.cs \
  --out-identity artifacts/nested/NestedDecisionRules.cs.usids
```

The five-stage checked-in plan produces:

```csharp
int result = 0;
if (enabled)
{
    if (critical)
    {
        result = 2;
    }
    else
    {
        result = 1;
    }
}
else
{
    result = 0;
}
return result;
```

The verified output sidecar advances from generation 1 to generation 6, has 27
live entries, and has zero tombstones.

## Current limits

Branch steps append; they do not insert at an arbitrary position inside a
generated Block, move an existing statement, delete a branch, or rewrite an
existing `if`. Nested conditions cannot refer to a newly generated local by a
symbolic value handle because patch expressions still carry persistent source
definition IDs. The producer tree cannot cross methods or documents.

Calls, member writes, compound assignments, `switch`, conditional expressions,
exception flow, async state-machine flow, and arbitrary Graph edge rewiring
remain outside this branch slice. Bounded `while` construction is layered on
the same recursive producer model in M4.6. Independent root-block insertions
are not members of the symbolic branch tree; terminal propagation applies to
generated branch paths, while every executed stage still relies on compiler
and CFG validation for its actual source state.
