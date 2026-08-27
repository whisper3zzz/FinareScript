# Lossless and Graph-Authored For Loops

M4.16 admits text-authored C# `for` loops to the shared source/HIR/Graph
contract. M4.17 closes the bounded reverse path with a dedicated
`ForPatchStatement`; M4.18 adds exact editing of supported existing header
endpoints. A `for` remains a first-class structure in both directions; it is
never rewritten into a `while`, because that would lose clause ownership and
would route `continue` past the incrementors.

The current strict-reader contracts are:

- HIR `unifiedscript.hir/v0.9`;
- Graph `unifiedscript.graph/v0.13`;
- standalone patch `unifiedscript.patch/v0.28`;
- patch plan `unifiedscript.patch-plan/v0.21`.

Patch v0.20 carries construction plus the four existing-header endpoint
operations. Plan v0.20 carries their direct wrappers, symbolic loop-body and
generated-for-local use, and the generated-for condition handle.

## HIR structure

HIR v0.7 adds `HirForStatement` with five execution-relevant components:

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

HIR v0.7 also adds `IsPostfix` to `HirUnaryExpression`. This distinguishes:

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

Graph v0.12 continues to use one `loop` decision node. A `while` retains its one
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

## Exact existing-header editing

M4.18 exposes four role-aware operations instead of treating the complete
header as one replaceable expression:

| Operation | Target | Replacement | Preserved structure |
|---|---|---|---|
| `replaceForCondition` | `HirForStatement` with an explicit condition | exact Boolean expression | loop and body |
| `replaceForDeclarationInitializer` | initialized `HirForVariableDeclarator` | exact same-typed expression | loop, declaration group, declarator and name |
| `replaceForAssignmentInitializerValue` | indexed root simple assignment | exact same-typed right side | loop, initializer slot, assignment and target |
| `replaceForIncrementor` | indexed root `++`/`--` unary | local operand, operator and prefix/postfix position | loop, incrementor slot and mutation identity |

Every payload repeats the owning loop ID. Indexed initializer and incrementor
forms also repeat their zero-based slot, current operator, endpoints, binding,
name, and exact semantic type. This prevents a stale visual edit from landing
on a different clause after source changes. Declaration initialization rejects
an absent initializer; condition replacement rejects an implicit-true omitted
condition.

Condition and incrementor expressions may reference the same loop's declared
local. That scope is admitted only for the exact owning header; it does not make
the local visible before its declaration or outside the loop. Declaration
initializer editing deliberately uses the ordinary pre-loop scope and therefore
cannot self-reference the new declarator. Assignment initializers remain
limited to simple assignments to an existing bound local.

Incrementor replacement admits only a local reference of the exact arithmetic
or enum type and `++`/`--`. It can change operator, prefix/postfix position, or
the writable local. When the operand binding is unchanged, both the mutation
and operand IDs are pinned; changing the operand tombstones the old identifier
while retaining the mutation ID. Full reanalysis still proves C# writability,
binding, Graph effects, and the continue-to-increment CFG target.

`replaceProducedForCondition` references an earlier `ForPatchStatement`
insertion by `sourceStepId`. It is published only when the generated loop has
an explicit condition. At execution time the coordinator resolves the actual
post-reanalysis loop and condition IDs, resolves any generated-local references,
and emits an ordinary `replaceForCondition` stage.

The patch algebra still does not restructure an existing whole `for`: it cannot
add/remove/reorder clauses, switch initializer kind, replace arbitrary
initializer/incrementor expression forms, or turn an omitted condition into an
explicit one. Nested supported expression nodes remain independently editable
through the general exact-input operation.

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

Run the exact existing-header sample with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/EditForRules.cs \
  --identity samples/Patching/EditForRules.cs.usids \
  --plan samples/Patching/EditForRules.plan.json \
  --out artifacts/for-edit/EditForRules.cs \
  --out-identity artifacts/for-edit/EditForRules.cs.usids
```

The four-stage plan changes the source to
`for (int index = 1; enabled && (index < count); ++index)` and advances the
assignment-form initializer to `cursor = 1`, while retaining both loop bodies
and the ordered initializer/increment regions.

## Deliberate limits

- Existing-`for` whole-clause replacement/restructuring is not exposed; only
  the four exact M4.18 endpoints above are mutable.
- Visual declaration construction is limited to exactly one initialized local;
  text-authored multiple declarators remain losslessly readable.
- Visual expression initializers are exact assignments to available locals;
  arbitrary calls and mutations are not admitted in that phase.
- Visual incrementors are ordered local `++`/`--` only; general calls,
  assignments, and compound expressions remain text-authored.
- A generated for-local may be consumed only where ordinary declaration scope
  and CFG dominance make it available, normally inside that loop's body.
- Indexed and disposal-aware synchronous enumerable `foreach` have their
  M4.20/M4.21 contract; pattern-only/ref/async variants remain later work. See
  [FOREACH_LOOPS.md](FOREACH_LOOPS.md).
- `do` uses its separate post-condition control shape and remains neither
  `while` nor `for`; see [DO_WHILE_LOOPS.md](DO_WHILE_LOOPS.md).
- Declaration modifiers, ref locals, deconstruction, and unsupported expression
  forms remain outside the Visual Profile.
- Exception edges, SSA, control dependence, and automatic graph placement
  remain derived or future work.
