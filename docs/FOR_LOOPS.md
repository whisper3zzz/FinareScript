# Lossless and Graph-Authored For Loops

M4.16 admits text-authored C# `for` loops to the shared source/HIR/Graph
contract. M4.17 closes the bounded reverse path with a dedicated
`ForPatchStatement`. A `for` remains a first-class structure in both
directions; it is never rewritten into a `while`, because that would lose
clause ownership and would route `continue` past the incrementors.

The current strict-reader contracts are:

- HIR `unifiedscript.hir/v0.5`;
- Graph `unifiedscript.graph/v0.9`;
- standalone patch `unifiedscript.patch/v0.15`;
- patch plan `unifiedscript.patch-plan/v0.16`.

Patch v0.15 adds the dedicated statement and for-local expression payloads;
plan v0.16 adds symbolic loop-body and generated-for-local use without changing
the existing plan-step discriminators.

## HIR structure

HIR v0.5 adds `HirForStatement` with five execution-relevant components:

```text
HirForStatement
├── Declaration?                 // int i = 0, step = 1
│   └── HirForVariableDeclaration
│       ├── HirForVariableDeclarator(i, initializer: 0)
│       └── HirForVariableDeclarator(step, initializer: 1)
├── Initializers[]               // or: Reset(), index = 0
├── Condition?                   // omitted means true
├── Incrementors[]               // i++, Advance(), ...
└── Body
```

C# grammar permits either one variable-declaration group or an ordered list of
initializer expressions. HIR preserves that choice instead of flattening a
declaration into an expression. Multiple declarators and multiple expression
clauses retain source order. A missing condition remains `null`, representing
the C# implicit-true rule without inventing a synthetic literal and semantic
ID.

`HirForVariableDeclarator` is a declaration identity. Roslyn binding maps every
use in the condition, incrementors, and body back to that ID. The declaration
group separately retains its source type spelling, including `var`, while the
semantic side table retains the resolved type.

HIR v0.5 also adds `IsPostfix` to `HirUnaryExpression`. This distinguishes:

```csharp
++index;
index++;
```

Both use operator `++`, but their value semantics differ when embedded in a
larger expression. Preserving position prevents a source/visual round trip from
silently changing which value the expression produces. Graph emits
`position: prefix` or `position: postfix` on every unary node. Mutation unary
nodes also expose `execIn`/`execOut` and participate in the effect chain.

All new nodes participate in HIR traversal, JSON polymorphism, Roslyn syntax
correlation, semantic binding, patch indexing, structural fingerprints,
persistent identity reconciliation, and identity rewriting.

## Graph regions

Graph v0.9 continues to use one `loop` decision node. A `while` retains its one
`loopBody` child. A `for` owns exactly three ordered child regions:

```text
loop
├── loopInitializer
├── loopBody
└── loopIncrement
```

The initializer and increment regions always exist so consumers see one stable
shape. When the corresponding source clause is absent, the region is empty and
`isImplicit: true`. An omitted condition is represented on the loop node by
`conditionEvaluation: implicitTrue` and the condition data port is absent.

The loop node publishes the execution contract:

```json
{
  "kind": "loop",
  "label": "For",
  "properties": {
    "loopForm": "for",
    "initializerKind": "declaration",
    "conditionEvaluation": "explicit",
    "incrementorCount": "1",
    "continueTarget": "incrementors",
    "evaluationOrder": "initializer,condition,body,incrementors"
  }
}
```

The shared VM must execute the phases in this order:

```text
initializer (once)
    ↓
condition ── false ──> exit
    │ true
    ↓
body
    ↓ fallthrough or continue
incrementors
    └───────────────> condition
```

`break` bypasses the incrementors and joins the loop exit. `return` remains
terminal. An initializer is outside the cyclic component, even when it contains
effects, so it cannot execute again on a back edge.

## CFG contract

`GraphControlFlowLoop` now distinguishes:

- `HeaderEntryNodeId`: the loop node or first condition effect;
- `ContinueTargetNodeId`: the first increment effect/evaluation for `for`, or
  the header when there are no incrementors;
- optional initializer and increment region IDs;
- body entry, exit, back-edge arcs, breaks, and continues.

For an effectful loop such as:

```csharp
for (Reset(); Check(); Advance())
{
    continue;
}
```

the validated control sequence is:

```text
Reset → Check → loop.body → continue → Advance → Check
```

The `Advance → Check` arc is a back edge. `Reset → Check` is not, because the
initializer is not part of the cycle. CFG validation rejects a continue edge
that targets `Check` directly while an increment phase exists.

Condition effects remain valid and are reevaluated on every iteration. Ordered
initializer and incrementor effects are connected using the same effect-chain
logic as ordinary expression statements.

## Bounded visual construction

`ForPatchStatement` owns four explicit parts:

- either one declaration group or an ordered list of assignment initializers;
- an optional Boolean condition, where omission preserves C# implicit true;
- ordered `++`/`--` incrementors with prefix/postfix position; and
- the explicit empty body created by the normal structured-insertion path.

The bounded declaration constructor deliberately accepts exactly one variable
with an exact graph-safe type and required initializer. Clause-local references
use `forLocalReference(variableIndex, expectedName, expectedTypeId)`, which is
valid only inside the same `for` payload. Expression initialization instead
accepts ordered simple assignments to existing, unshadowed same-method locals
that lexically and CFG-dominate the insertion gap. Declaration and expression
initializer forms are mutually exclusive.

Mutation incrementors accept only writable local identifiers, exact arithmetic
or enum types, and `++`/`--`. This makes mutation and its effect ordering
explicit without admitting arbitrary effectful authored expression trees. The
complete generated statement is emitted as ordinary C#, reparsed, rebound,
reprojected, and checked against the same HIR/Graph/CFG contract as text input.

When a plan inserts a declaration-form `for`, its step publishes two compatible
transaction handles:

- a loop-body handle consumed by `insertLoopBodyStatement`; and
- one produced-local handle for the single generated declarator.

Later body expressions use `producedLocalReference` and are resolved to the
actual post-reanalysis declarator ID. The ordinary lexical and CFG gates prevent
that local from escaping the loop body. `break` and `continue` now discover the
nearest `HirWhileStatement` or `HirForStatement`; Graph/CFG validation proves
that a generated `for` continue enters the increment phase rather than jumping
directly to the condition.

Existing exact operations can still edit supported expression nodes nested
inside text- or graph-authored clauses when their normal type, endpoint, effect,
trivia, identity, scope, and dominance checks pass.

The patch algebra does not yet restructure an existing whole `for`. It cannot
add, remove, reorder, or change the kind of existing initializer/incrementor
clauses, and it does not offer a whole-condition endpoint operation analogous
to `replaceWhileCondition`.

Reusing `WhilePatchStatement` remains invalid: that payload has only a
condition and body, so it cannot express one-time initialization, incrementor
order, declaration scope, or the distinct continue target.

## Checked-in sample

Analyze the lossless text-authored sample with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Loops/ForRules.cs \
  --out artifacts/for-loops
```

The sample covers:

- a declaration initializer and a postfix increment;
- a loop-local referenced from condition, body, and incrementor;
- normal body fallthrough and `continue` through the increment phase;
- effectful expression initialization and increment;
- nearest-loop `break`;
- initializer execution outside the cyclic CFG component.

Run the bounded visual-construction sample with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/ForBuildRules.cs \
  --identity samples/Patching/ForBuildRules.cs.usids \
  --plan samples/Patching/BuildFor.plan.json \
  --out artifacts/for-plan/ForBuildRules.cs \
  --out-identity artifacts/for-plan/ForBuildRules.cs.usids
```

The four-stage plan creates `for (int index = 0; index < count; index++)`,
inserts an `index == 2` guard and `continue`, then accumulates `index` into an
outer `total`. It demonstrates both symbolic handles and verifies that the
continue target remains the generated incrementor.

## Deliberate limits

- Existing-`for` whole-clause replacement/restructuring is not exposed.
- Visual declaration construction is limited to exactly one initialized local;
  text-authored multiple declarators remain losslessly readable.
- Visual expression initializers are exact assignments to available locals;
  arbitrary calls and mutations are not admitted in that phase.
- Visual incrementors are ordered local `++`/`--` only; general calls,
  assignments, and compound expressions remain text-authored.
- A generated for-local may be consumed only where ordinary declaration scope
  and CFG dominance make it available, normally inside that loop's body.
- `await foreach`, synchronous `foreach`, and enumerator disposal semantics
  require a separate contract.
- `do` requires a separate post-condition region and is not represented as
  either `while` or `for`.
- Declaration modifiers, ref locals, deconstruction, and unsupported expression
  forms remain outside the Visual Profile.
- Exception edges, SSA, control dependence, and automatic graph placement
  remain derived or future work.
