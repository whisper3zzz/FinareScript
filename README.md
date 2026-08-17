# UnifiedScript

UnifiedScript is an experimental dual-modal gameplay authoring system. A
restricted C# profile and a structured visual graph are intended to be two
lossless projections of one program model. The first execution backend will use
Roslyn and CoreCLR; Unreal Engine integration is intentionally deferred until
the language and projection model are proven independently.

## Current vertical slice

The repository currently implements:

- deterministic semantic IDs shared as compact JSON strings across compiler artifacts;
- source ranges and structured diagnostics;
- a Roslyn validator for Visual C# profile v0.2;
- C# syntax lowering into a polymorphic high-level IR (HIR);
- a Roslyn compilation host and language-neutral bound semantic model;
- deterministic multi-document compilation requests and cross-file binding;
- resolved types, declaration references, overloads, and conservative call effects;
- a versioned, configurable method-effect catalog;
- deterministic, effect-aware HIR-to-graph projection with control/data edges
  and explicit function/block/branch regions;
- derived CFG analysis with synthetic exits, reachability, dominance,
  post-dominance, structured merge facts, and reducibility validation;
- explicit `while`, `break`, and `continue` HIR, Graph regions, nearest-loop
  transfers, condition re-evaluation, and CFG header/back-edge facts;
- a versioned semantic patch protocol with source/configuration conflict guards;
- transactional graph-to-C# literal, operator, argument-connection, and bounded
  statement rewriting;
- CFG-checked local-assignment insertion with typed, effect-bounded values;
- use/definition indexing and safe extraction of stable expressions into
  single-evaluation C# temporaries;
- combined lexical/CFG dominance analysis and exactly typed reconnection from
  an existing local definition to a stable consumer expression;
- atomic multi-stage patch plans composing every bounded patch operation, with
  symbolic references to newly introduced locals and full reanalysis per stage;
- recursive symbolic `then`/`else` block handles with ordered local, assignment,
  nested-branch, and typed early-return population, tree-aware terminal-path
  guards, and late-failure rollback;
- bounded typed `while` synthesis with symbolic loop-body handles, recursive
  branch/loop composition, and nearest-loop `break`/`continue` validation;
- opt-in `.usids` sidecars that preserve node identity across formatting and edits;
- semantic-independent `.usgraph` layout sidecars with deterministic positions,
  sizes, collapsed state, comment regions, routing hints, and orphan recovery;
- explicit patch identity pins and tombstones for deleted semantic nodes;
- versioned HIR, semantics, and graph JSON emission through a small CLI;
- xUnit coverage for validation, lowering, identity, graph projection, and
  deterministic output.

M3 bidirectional source projection is complete. M4.1 adds explicit Graph
regions, M4.2 derives and validates control flow, M4.3 consumes those facts for
bounded local assignment and safer value reconnection, and M4.4 recursively
authors nested acyclic decisions through the same HIR/Graph path. M4.5 now
projects and validates structured loops through that same path. M4.6 completes
the first bounded Graph-to-C# loop round trip by constructing and populating
those loops atomically.

The v0.2 profile accepts `[Visual]` methods containing local variables,
expressions, calls, assignments, `if/else`, `while`, `break`, `continue`,
`return`, casts, and `await`.
Unsupported syntax is rejected explicitly instead of being approximated.

## Architecture

```text
C# source
  -> Roslyn full-fidelity syntax tree
  -> Visual Profile validation
  -> Unified HIR
  -> Roslyn SemanticModel
  -> language-neutral bound semantics
  -> deterministic, effect-aware graph projection

Graph edit
  -> semantic patch bound to source + semantic configuration IDs
  -> typed/preconditioned Roslyn replacement
  -> full profile and semantic revalidation
  -> updated C# source (only if the whole patch succeeds)

Later:
  -> semantic MIR
  -> Roslyn IL/CoreCLR backend
  -> shared debugger/source map
  -> Unreal Engine adapter
```

The HIR is the semantic contract. IL and future visual layouts are derived
artifacts; neither is used as a decompilation source.

CLI inputs inside the current working directory use normalized relative source
paths, so artifacts remain stable when the repository is checked out elsewhere.

## Requirements

- .NET SDK 10.0.201 or a compatible feature-band roll-forward

## Build and test

```bash
dotnet restore UnifiedScript.slnx
dotnet build UnifiedScript.slnx --no-restore
dotnet test UnifiedScript.slnx --no-build
python3 tools/validate_project.py
```

## Analyze the sample

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/DoorController.cs \
  --out artifacts/sample
```

This emits:

```text
artifacts/sample/DoorController.hir.json
artifacts/sample/DoorController.semantics.json
artifacts/sample/DoorController.graph.json
```

Without `--out`, the graph JSON is written to stdout.

### Analyze structured regions

Graph v0.4 assigns every node to exactly one semantic region and preserves
function, block, `if`, `then`, explicit/implicit `else`, `loop`, and `loopBody`
nesting. Generate the early-return example with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Regions/EarlyReturn.cs \
  --out artifacts/regions
```

The projected hierarchy is `function → body block → if → then/else`. The
`return true` nodes belong to `then`; because the source omits an explicit
`else`, the empty false-path region is marked `isImplicit`. The outer
`return false` remains in the body block. See
[Graph Regions](docs/GRAPH_REGIONS.md) for the schema and invariants.

The projector also derives a non-persisted CFG index and rejects irreducible
control flow before returning the Graph. The index exposes synthetic function
exit arcs, predecessors/successors, reachable nodes, dominance,
post-dominance, branch merges, per-branch function exits, and cyclic SCC entry
facts. See [Control Flow Analysis](docs/CONTROL_FLOW_ANALYSIS.md).

### Analyze structured loops

The loop sample exercises local assignment, a nearest-loop `break`, a
nearest-loop `continue`, and normal body back edges while retaining persistent
node identity:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Loops/Countdown.cs \
  --identity samples/Loops/Countdown.cs.usids \
  --out artifacts/loops
```

HIR v0.2 emits explicit while/break/continue statements. Graph v0.4 emits a
`loop` region with one `loopBody` child, and derived CFG facts identify the
effective condition header, body entry, exit successor, back-edge arcs, and
break/continue nodes owned by the nearest loop. Effectful conditions route all
continues and normal fallthrough back through their first effect so the
condition is reevaluated each iteration. See
[Structured Loop Foundation](docs/LOOP_FOUNDATION.md).

## Analyze multiple source files

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze-files \
  samples/MultiFile/GameplayApi.cs \
  samples/MultiFile/DamageScript.cs \
  --assembly UnifiedScript.MultiFileSample \
  --effects samples/MultiFile/effects.json \
  --out artifacts/multi-file
```

`analyze-files` sorts logical source paths before parsing, so changing command
line input order does not change HIR, semantic IDs, or Graph JSON. `--reference`
may be repeated to add explicit metadata assemblies; their contents contribute
to the semantic configuration ID.

Effect catalogs use `unifiedscript.effects/v0.1`. Rules match assembly,
containing type, and method metadata names. `*` is supported as a whole-field
wildcard. User rules override the bootstrap `System.Math`/`System.MathF` rules;
unmatched calls remain conservatively read/write effectful.

## Initialize persistent identities

Create the source-controlled identity sidecar once:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  identity-init \
  samples/Patching/DamageRules.cs \
  --out artifacts/identity/DamageRules.cs.usids
```

`unifiedscript.identity/v0.1` stores random 128-bit semantic IDs, structural and
shape fingerprints, token-context evidence, anchors, generations, and
tombstones. Supplying it with `--identity` makes analysis reconcile the current
HIR against the stored evidence. Unchanged sources reproduce byte-identical
sidecars; whitespace and comment edits preserve IDs even though the source
digest and generation advance.

The checked-in sample sidecar is
`samples/Patching/DamageRules.cs.usids`. `analyze` and `analyze-files` accept
repeatable `--identity`; when `--out` is present, updated sidecars are emitted
beside the other analysis artifacts without overwriting the input files.

## Apply a visual literal patch

The patching sample changes the graph literal `1.0f` to `100.0f`, deliberately
changing its source width while retaining the same persistent node ID:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-patch \
  samples/Patching/DamageRules.cs \
  --identity samples/Patching/DamageRules.cs.usids \
  --patch samples/Patching/ScaleDamage.patch.json \
  --out artifacts/patched/DamageRules.cs \
  --out-identity artifacts/patched/DamageRules.cs.usids
```

`unifiedscript.patch/v0.9` binds every patch to both the source compilation ID
and semantic configuration ID emitted in Graph schema v0.3. A `replaceLiteral`
operation also carries the expected old literal text. A stale source, changed
reference/effect configuration, missing target, changed old value, non-literal
replacement, or semantically invalid result rejects the entire patch and writes
no source.

The Roslyn replacement receives a temporary identity pin, so the edited node's
old ID is explicitly carried into the reconciled sidecar even when its
fingerprint and span length change. Deleted live entries move to tombstones;
ambiguous matches allocate new IDs and emit `UVS3101` rather than guessing.

## Apply structural graph patches

Patch v0.9 includes `changeOperator` and `reconnectArgument`. The checked-in
operator sample changes the binary node from multiplication to division while
requiring the expected result, left-port, and right-port type IDs:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-patch \
  samples/Patching/DamageRules.cs \
  --identity samples/Patching/DamageRules.cs.usids \
  --patch samples/Patching/DivideDamage.patch.json \
  --out artifacts/operator/DamageRules.cs \
  --out-identity artifacts/operator/DamageRules.cs.usids
```

Operator changes are restricted to the same result-preserving family, such as
arithmetic-to-arithmetic or comparison-to-comparison. Both unary and binary
operators are supported. The edited operator node receives an identity pin.

`reconnectArgument` safely exchanges two existing positional arguments of one
resolved invocation. Its patch records both indices, both expected endpoint
IDs, and both expected parameter type IDs. The types must match exactly and
both argument subtrees must be effect-free; otherwise changing their C#
left-to-right evaluation order is rejected. The invocation and both moved
argument roots retain their persistent IDs, so Graph edges move without
inventing duplicate semantic nodes.

Overlapping operations—for example changing an operator inside an invocation
while also reconnecting that invocation in the same patch—are rejected and
must be submitted as sequential patches with a fresh baseline. With
`--identity`, `--out-identity` is required and source plus updated sidecar are
written as one rollback-capable output transaction. The in-memory patcher
supports multi-document requests; the CLI commit boundary remains
single-document.

## Apply a statement insertion

Patch v0.8 also includes bounded `insertStatement` and `deleteStatement`
operations. The insertion sample adds a graph-authored local declaration before
the existing return statement:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-patch \
  samples/Patching/DamageRules.cs \
  --identity samples/Patching/DamageRules.cs.usids \
  --patch samples/Patching/InsertScale.patch.json \
  --out artifacts/inserted/DamageRules.cs \
  --out-identity artifacts/inserted/DamageRules.cs.usids
```

An insertion targets a parent block and records the exact adjacent sibling IDs.
Patch v0.8 accepts a local declaration with one literal initializer, a
CFG-checked local assignment, a typed terminal return, or an `if` whose
condition is a versioned graph-authored expression. A local's
semantic type must already be present and graph-safe in the bound compilation,
and its name must not collide with any parameter or local in the method. The
existing block, sibling statements, and their expressions are explicitly
pinned; newly authored syntax receives new persistent IDs.

The sample intentionally reports compiler warning `CS0219` because the new
local is not connected to a consumer yet; warnings do not bypass the full
semantic reanalysis or make the transaction partial.

### Insert a CFG-checked local assignment

The M4.3 sample inserts `result = result + 1;` between an existing local
declaration and return:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-patch \
  samples/Patching/AssignmentRules.cs \
  --identity samples/Patching/AssignmentRules.cs.usids \
  --patch samples/Patching/IncrementResult.patch.json \
  --out artifacts/assignment/AssignmentRules.cs \
  --out-identity artifacts/assignment/AssignmentRules.cs.usids
```

`localAssignment` identifies an existing same-method local by persistent ID,
expected name, and exact graph-safe type. Its value reuses the closed typed
expression payload. The insertion gap must be CFG-reachable; the target and
every local used by the value must be in lexical scope and dominate every CFG
anchor reaching that gap. This permits assignments at a reachable branch end,
but rejects sibling-branch locals and paths terminated by returns. The emitted
assignment is ordinary C#, HIR, and Graph—not an editor-only node. See
[LOCAL_ASSIGNMENT_PATCHING.md](docs/LOCAL_ASSIGNMENT_PATCHING.md).

### Insert a typed branch skeleton

The structured-control-flow sample builds `(damage > threshold) && !disabled`
from typed expression nodes and inserts an `if` before the existing return:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-patch \
  samples/Patching/GuardRules.cs \
  --identity samples/Patching/GuardRules.cs.usids \
  --patch samples/Patching/InsertGuard.patch.json \
  --out artifacts/guard/GuardRules.cs \
  --out-identity artifacts/guard/GuardRules.cs.usids
```

The emitted source contains explicit blocks on both paths:

```csharp
if ((damage > threshold) && (!disabled))
{
}
else
{
}
```

The expression payload has five closed variants: scalar `literal`,
`localReference`, `unary`, `binary`, and `cast`. Every node carries an expected
semantic type ID. References also carry a source definition ID and expected
name; a local must both lexically and CFG-dominate the reachable insertion gap,
while a parameter must belong to the same method. The current operator subset
excludes division, remainder, decimal/string operations, mutation, calls,
member reads, and other expressions whose evaluation may throw, allocate,
mutate, or read state.

The standalone operation deliberately creates empty `then` and `else` blocks.
It proves that source, HIR, Graph branch/data/control edges, persistent identity,
and `.usgraph` layout synchronization share one structure. New branch and
condition nodes are explicitly `unplaced`; existing placements survive.

Patch-plan v0.7 can populate those newly allocated blocks without predicting
their random IDs. The checked-in plan inserts the same guard, appends two typed
locals to `then`, and appends one typed local to `else`:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/GuardRules.cs \
  --identity samples/Patching/GuardRules.cs.usids \
  --plan samples/Patching/PopulateGuard.plan.json \
  --out artifacts/guard-plan/GuardRules.cs \
  --out-identity artifacts/guard-plan/GuardRules.cs.usids
```

Each `insertBranchStatement` names the earlier `if` step through `sourceStepId`
and selects `then` or `else`. The coordinator resolves the actual block ID after
the `if` stage, appends branch statements in plan order, and reuses the normal
typed insertion checks. A branch payload is deliberately limited to a local
declaration with a literal initializer, a CFG-checked assignment to an existing
outer local, another typed `if`, or a typed terminal `return`.

### Build nested decisions recursively

An `if` inserted through `insertBranchStatement` publishes its own `stepId` as
the next branch producer. Later steps can therefore address its generated
`then` and `else` blocks without knowing any intermediate random IDs:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/NestedDecisionRules.cs \
  --identity samples/Patching/NestedDecisionRules.cs.usids \
  --plan samples/Patching/BuildNestedDecision.plan.json \
  --out artifacts/nested/NestedDecisionRules.cs \
  --out-identity artifacts/nested/NestedDecisionRules.cs.usids
```

The five-stage sample emits:

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

Branch handles form an acyclic producer tree. If both children of a nested
`if` become terminal, terminal state propagates to the parent path only when
that nested `if` is still its final statement. The validator rejects a plan
that would retroactively make already appended parent statements unreachable.
Every stage reanalyzes CFG and reconciles identity; ancestor `if` and Block IDs
remain pinned, while newly authored nodes remain unplaced. See
[NESTED_BRANCH_PATCHING.md](docs/NESTED_BRANCH_PATCHING.md).

### Insert a typed early return

The M3 closure sample inserts the guard, returns `true` from its generated
`then` block, and changes the surviving outer fallback to `false` in one atomic
plan:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/GuardRules.cs \
  --identity samples/Patching/GuardRules.cs.usids \
  --plan samples/Patching/ReturnGuard.plan.json \
  --out artifacts/return-guard/GuardRules.cs \
  --out-identity artifacts/return-guard/GuardRules.cs.usids
```

```csharp
if ((damage > threshold) && (!disabled))
{
    return true;
}
else
{
}
return false;
```

A value return must declare the current non-async method's exact semantic return
type and carry a closed expression of that type. A `void` return carries no
expression. Returns can be appended only at the end of an explicit block, and
the protocol rejects later steps in the same produced branch as unreachable.
Async/task-like return insertion remains outside this bounded slice.

A deletion targets one statement and records its parent block plus both current
sibling IDs. This slice permits only an unused local with no initializer or a
literal initializer, or an effect-free resolved static call whose argument
subtrees are also effect-free. Referenced locals, effectful calls, assignments,
awaits, mutations, returns, branches, and blocks are rejected. Surviving
statements retain their IDs and comments; every ID in the deleted subtree moves
to the sidecar tombstones. Sibling, type, name, reference, and effect checks are
optimistic-concurrency preconditions: any failure rejects the whole patch.

## Introduce a single-evaluation temporary

`introduceTemporary` extracts one existing stable expression into a typed local
immediately before its containing statement. The original expression moves to
the initializer and keeps its persistent ID; a new identifier replaces the old
position:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-patch \
  samples/Patching/DamageRules.cs \
  --identity samples/Patching/DamageRules.cs.usids \
  --patch samples/Patching/IntroduceScaledDamage.patch.json \
  --out artifacts/temporary/DamageRules.cs \
  --out-identity artifacts/temporary/DamageRules.cs.usids
```

The sample produces:

```csharp
float scaledDamage = damage * 1.0f;
return scaledDamage;
```

The operation records the target expression, containing statement, parent
block, exact adjacent siblings, temporary name, and expected semantic type. The
current slice accepts return values and local initializers in explicit blocks.
The complete containing expression must consist only of literals, parameter or
local references, supported operators/casts, and resolved pure static calls
with equally stable arguments. Member/state reads, assignments, mutation,
awaits, unknown/effectful calls, branches, and expression statements are
rejected rather than reordered. Short-circuit operators, division/remainder,
decimal operators, and string operators are also excluded in this slice because
the current builtin-effect model cannot yet prove that speculative or reordered
evaluation is non-throwing and allocation-free.

Every pre-existing expression in the changed statement receives an identity
pin. The moved expression, its descendants, the containing statement, sibling
statements, and parent block therefore retain their IDs. Only the new local
declaration and replacement identifier allocate new IDs. This is the first
lowering primitive needed to represent a shared Graph value without duplicating
C# evaluation. Patch v0.8 can now connect additional compatible consumers to
that local definition.

## Reconnect a typed consumer

`reconnectValue` replaces one existing stable consumer expression with a C#
identifier that refers to an existing local declaration. The checked-in sample
turns the second expression below into a use of the already evaluated value:

```csharp
float sharedDamage = damage * 2.0f;
return bonus + 1.0f;
```

Run it with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-patch \
  samples/Patching/SharedValueRules.cs \
  --identity samples/Patching/SharedValueRules.cs.usids \
  --patch samples/Patching/ReconnectSharedDamage.patch.json \
  --out artifacts/reconnected/SharedValueRules.cs \
  --out-identity artifacts/reconnected/SharedValueRules.cs.usids
```

The result is:

```csharp
float sharedDamage = damage * 2.0f;
return bonus + sharedDamage;
```

The patch records the source declaration, target expression, containing
statement, parent block, exact adjacent siblings, and both semantic type IDs.
The source local must be in the same method and must both lexically and
CFG-dominate the reachable consumer statement: a root-block local may feed a
later nested branch, but a branch-local value cannot escape that branch or feed
a sibling branch. The
source name must also be unambiguous in the method, and source and target types
must be the same graph-safe type.

Removing the old consumer subtree is allowed only when it is stable and
effect-free under the same conservative rules used by temporary extraction.
State/member reads, mutation, assignment, await, unknown or effectful calls,
short-circuit evaluation, division/remainder, and string/decimal operators are
rejected. Comments or directives inside the removed target are rejected as
well, because silently discarding them would make the projection lossy.

The containing statement, its surviving expressions, siblings, parent block,
and source declaration retain their persistent IDs. IDs belonging to the
removed target subtree become tombstones, while the replacement identifier
receives a new ID and binds back to `sourceDefinitionId`. Reanalyzing the output
projects that binding into Graph as `referencedSymbolId`. Multiple consumers
can be connected with sequential patches, each generated against the freshly
analyzed source baseline, or atomically with the patch-plan protocol below.

## Apply atomic semantic patch plans

`unifiedscript.patch-plan/v0.8` composes `replaceLiteral`, `changeOperator`,
`reconnectArgument`, `insertStatement`, `insertBranchStatement`,
`insertLoopBodyStatement`, `deleteStatement`, `introduceTemporary`, and
`reconnectValue` as one ordered transaction. The
shared-value sample starts with:

```csharp
return damage * 2.0f + damage * 3.0f + 1.0f;
```

Run the plan with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/SharedExpressionPlan.cs \
  --identity samples/Patching/SharedExpressionPlan.cs.usids \
  --plan samples/Patching/ShareDamage.plan.json \
  --out artifacts/planned/SharedExpressionPlan.cs \
  --out-identity artifacts/planned/SharedExpressionPlan.cs.usids
```

The result is committed once:

```csharp
float sharedDamage = damage * 2.0f;
return sharedDamage + sharedDamage + sharedDamage;
```

Each plan step has a unique `stepId`. Both `introduceTemporary` and a
local-literal `insertStatement` publish a symbolic handle; later
`reconnectValue` steps use `sourceStepId` to reference that handle. The
coordinator resolves the actual random declaration ID after the producer stage,
rebuilds the in-memory request with the newly reconciled sidecar, derives a
fresh internal source baseline and structural context, and then executes the
next validated step. This is why the editor does not need to predict an
intermediate ID or submit several external transactions.

All target, containing-statement, parent-block, sibling, and semantic-type
preconditions are first checked against the original plan baseline. Internal
rebasing applies only to changes made by earlier steps in that same plan. Every
stage reruns parsing, Visual Profile validation, C# compilation, semantic
binding, identity reconciliation, and Graph safety. If any later stage fails,
the API returns no patched request and the CLI writes neither source nor
sidecar. A persistent `.usids` input is mandatory because span-derived IDs
cannot identify later targets after the first insertion changes source ranges.

The composed sample proves a four-stage visual gesture: insert `scale`, connect
the return expression to that new local, change multiplication to division, and
delete an obsolete declaration:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/ComposedPlan.cs \
  --identity samples/Patching/ComposedPlan.cs.usids \
  --plan samples/Patching/ComposeScale.plan.json \
  --out artifacts/composed/ComposedPlan.cs \
  --out-identity artifacts/composed/ComposedPlan.cs.usids
```

The single committed result is:

```csharp
float scale = 2.0f;
return damage / scale;
```

Before execution, every independently executable step is checked against the
original source. During execution, statement siblings and insertion gaps are
rebased only over earlier stages in the same plan. Multiple insertions into one
original gap retain plan order, and argument reconnection relocates its current
indices by persistent endpoint ID. Non-insertion targets remain unique to avoid
ambiguous repeated old-value preconditions. Reconnect handles must reference an
earlier local producer in the same method with the same graph-safe type. An `if`
insertion publishes symbolic `then` and `else` block handles only through later
`insertBranchStatement` steps; it still is not a local value handle. Branch
locals publish ordinary local handles, but normal dominance rules prevent a
branch-local value from escaping to the outer block or sibling branch. A typed
branch return terminates that symbolic path; later appends to the same path are
rejected before execution. An inserted nested `if` recursively publishes two
more paths, and all-path termination propagates up that producer tree. A
`while` insertion similarly publishes one symbolic body handle through
`insertLoopBodyStatement`; nested if/while producers compose, while
`break`/`continue` bind only to the nearest lexical loop. Local assignments are
supported as direct, produced-branch, and produced-loop-body payloads. Calls,
async/task-like returns, cyclic handle references, and cross-method handles
remain outside v0.8.

### Build a loop from a visual plan

The checked-in M4.6 sample starts with a local and its return, then atomically
inserts a typed while, adds a stop branch and `break`, decrements the local, and
adds `continue`:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/LoopBuildRules.cs \
  --identity samples/Patching/LoopBuildRules.cs.usids \
  --plan samples/Patching/BuildLoop.plan.json \
  --out artifacts/loop-plan/LoopBuildRules.cs \
  --out-identity artifacts/loop-plan/LoopBuildRules.cs.usids
```

The editor supplies only the original method-body anchor and backward symbolic
producer references. The coordinator resolves the generated while/body/branch
IDs after each reanalysis. The final ordinary C# loop reprojects into the same
HIR v0.2, Graph v0.4 regions, and nearest-loop CFG facts as a text-authored
loop. See [LOOP_PATCHING.md](docs/LOOP_PATCHING.md).

## Persist and synchronize graph layout

`unifiedscript.graph-layout/v0.1` keeps visual canvas state separate from C#,
HIR, bound semantics, and persistent `.usids` identity. Initialize a layout from
an emitted Graph document:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  layout-init artifacts/sample/DoorController.graph.json \
  --out artifacts/sample/DoorController.usgraph
```

Every projected node begins in explicit `unplaced` state. An editor can then
record finite positions and positive sizes, collapsed state, comment regions,
and edge routing control points. After a semantic edit emits an updated Graph,
synchronize the existing visual state into it:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  layout-sync artifacts/updated/DoorController.graph.json \
  --layout artifacts/sample/DoorController.usgraph \
  --out artifacts/updated/DoorController.usgraph
```

Surviving node IDs retain their layouts, new IDs become `unplaced`, and missing
nodes move to explicit `missingNode` or `missingFunction` orphans. A matching
orphan is restored if its semantic ID reappears. Comment membership and routing
hints are pruned only when their nodes or edges no longer exist. Layout syncing
cannot mutate semantic Graph JSON, HIR, source, or `.usids` because those
artifacts are not writable inputs to the synchronizer. See
[GRAPH_LAYOUT.md](docs/GRAPH_LAYOUT.md) and the checked-in
`samples/Layout/DamageRules.usgraph` example.

## Repository layout

```text
src/UnifiedScript.Core/      Semantic IDs, diagnostics, HIR, [Visual]
src/UnifiedScript.Semantics/ Language-neutral types, symbols, effects, bindings
src/UnifiedScript.CSharp/    Roslyn profile validation and lowering
src/UnifiedScript.Graph/     Graph projection, regions, CFG analysis, layout
src/UnifiedScript.Cli/       Analysis command-line interface
tests/UnifiedScript.Tests/   Unit and vertical-slice tests
samples/                     Input programs for manual inspection
docs/                        Architecture and milestone plans
```

## Scope boundary

This milestone does not include:

- Unreal Engine bindings;
- a UE graph editor;
- IL emission or CoreCLR hosting inside another process;
- semantic type binding against UE APIs;
- `.csproj`/MSBuild compilation loading;
- namespace/API allow and deny policies;
- flow-sensitive nullable state in the interchange model;
- general expression/statement synthesis and arbitrary data/control rewiring
  beyond bounded local/assignment/`if`/`while`/loop-transfer/`return`
  insertion, safe deletion, temporary introduction, compatible local consumer
  reconnection, and atomic composition of the current patch operations;
- automatic graph placement, layout merge drivers, and editor viewport/session
  state;
- `do`/`for`/`foreach`, switch, lambdas, exceptions, or general-purpose C#
  visualization;
- arbitrary loop restructuring, transfer retargeting, or existing-loop editing.

See [ARCHITECTURE.md](docs/ARCHITECTURE.md),
[IDENTITY_SIDECAR.md](docs/IDENTITY_SIDECAR.md),
[GRAPH_LAYOUT.md](docs/GRAPH_LAYOUT.md),
[CONTROL_FLOW_ANALYSIS.md](docs/CONTROL_FLOW_ANALYSIS.md),
[LOCAL_ASSIGNMENT_PATCHING.md](docs/LOCAL_ASSIGNMENT_PATCHING.md),
[NESTED_BRANCH_PATCHING.md](docs/NESTED_BRANCH_PATCHING.md),
[LOOP_FOUNDATION.md](docs/LOOP_FOUNDATION.md),
[LOOP_PATCHING.md](docs/LOOP_PATCHING.md),
[PATCH_PLAN.md](docs/PATCH_PLAN.md),
[M1_ROSLYN_GRAPH_FOUNDATION.md](docs/milestones/M1_ROSLYN_GRAPH_FOUNDATION.md),
[M2_SEMANTIC_BINDING.md](docs/milestones/M2_SEMANTIC_BINDING.md), and
[M3_BIDIRECTIONAL_PATCHING.md](docs/milestones/M3_BIDIRECTIONAL_PATCHING.md)
for the planned sequence.
