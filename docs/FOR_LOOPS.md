# Lossless For Loops

M4.16 admits text-authored C# `for` loops to the shared source/HIR/Graph
contract. A `for` remains a first-class structure; it is never rewritten into a
`while`, because that would lose clause ownership and would route `continue`
past the incrementors.

The current strict-reader contracts are:

- HIR `unifiedscript.hir/v0.5`;
- Graph `unifiedscript.graph/v0.9`;
- standalone patch `unifiedscript.patch/v0.14`;
- patch plan `unifiedscript.patch-plan/v0.15`.

Patch schemas do not change in this slice because whole-loop construction and
clause restructuring remain read-only.

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

## Source and visual editing boundary

M4.16 is text-authored and visually inspectable. Existing exact operations can
still edit ordinary supported expression nodes nested inside admitted clauses
when their normal type, endpoint, effect, trivia, identity, scope, and dominance
checks pass. Existing non-transfer insertions into an explicit body also
continue through the ordinary block and CFG safety path. Graph-authored
`break`/`continue` insertion into a `for` remains unavailable until the loop
patch model can express its increment-phase target explicitly.

The patch algebra does not yet create or restructure a whole `for`. In
particular, it does not add, remove, reorder, or change the kind of initializer
or incrementor clauses, and it does not offer a whole-condition endpoint
operation analogous to `replaceWhileCondition`.

Reusing `WhilePatchStatement` would be unsafe: that payload has only a
condition and body, so it cannot express one-time initialization, incrementor
order, declaration scope, or the distinct continue target. A future visual
constructor must introduce a dedicated versioned `ForPatchStatement` and prove
all those properties before source emission.

## Checked-in sample

Generate the sample artifacts with:

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

## Deliberate limits

- Whole-`for` visual construction and clause restructuring are not exposed.
- A `for`-declaration local is semantically bound and visible in Graph, but is
  not yet an authored `localReference` source in the bounded patch algebra.
- Graph-authored `break`/`continue` insertion currently recognizes `while`
  ancestry only; text-authored transfers in `for` are preserved and validated.
- `await foreach`, synchronous `foreach`, and enumerator disposal semantics
  require a separate contract.
- `do` requires a separate post-condition region and is not represented as
  either `while` or `for`.
- Declaration modifiers, ref locals, deconstruction, and unsupported expression
  forms remain outside the Visual Profile.
- Exception edges, SSA, control dependence, and automatic graph placement
  remain derived or future work.
