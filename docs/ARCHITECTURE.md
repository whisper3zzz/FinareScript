# UnifiedScript Architecture

## Purpose

UnifiedScript provides one gameplay language with two authoring projections:
restricted C# text and a structured visual graph. Both projections must preserve
semantic identity and ultimately execute the same compiled method body.

The language system was developed outside Unreal Engine first. This keeps
Roslyn, HIR, graph projection, and deterministic serialization fast to build
and test. M6.1 now integrates UE as a consumer of those contracts rather than
their owner.

## Non-negotiable decisions

### C# is the text surface

The project uses actual C# syntax and Roslyn. It does not inherit JavaScript
runtime semantics and does not create a new parser in the first phase.

### Visual C# is an explicit profile

Only methods marked `[Visual]` promise lossless visual projection. Unsupported
syntax is diagnosed before lowering. Unmarked C# methods are source-only and
their graph-safe calls project as one non-editable opaque node under the
versioned contract in
[`VISUAL_CSHARP_PROFILE.md`](VISUAL_CSHARP_PROFILE.md).

### Declaration IR and HIR are the shared semantic contracts

Declaration IR owns the asset path, type-level state, defaults, exposure policy,
and Visual signatures. HIR owns executable Visual method bodies. The graph is
projected from HIR, not from IL. Future graph edits will apply
semantic patches to HIR and then construct localized Roslyn syntax changes.

Bound semantics are stored as deterministic side tables keyed by HIR
`SemanticId`. This preserves a language-neutral HIR while attaching resolved
types, declaration references, exact methods, and effects without leaking
Roslyn symbols into downstream modules.

### IDs flow through every layer

Every declaration, statement, expression, graph node, future MIR operation, and
future bytecode range carries a `SemanticId`. Profile v0.2 derives deterministic
bootstrap IDs from source identity and syntax locations. When a `.usids`
sidecar is supplied, parameters, blocks, statements, and expressions receive
persistent random IDs reconciled from structural evidence. Type and method IDs
remain deterministic symbol identities. The file contract and reconciliation
rules are specified in
[IDENTITY_SIDECAR.md](IDENTITY_SIDECAR.md).

## Current pipeline

```mermaid
flowchart LR
    Source["C# source"] --> Roslyn["Roslyn syntax tree"]
    Roslyn --> Validator["Visual Profile Validator"]
    Validator --> HIR["Unified HIR"]
    Sidecar[".usids sidecar"] --> Reconcile["Identity Reconciler"]
    HIR --> Reconcile
    Reconcile --> StableHIR["Identity-stable HIR"]
    Roslyn --> Declaration["Script Asset / Declaration IR"]
    Reconcile --> Declaration
    Declaration --> AssetJson["Declaration JSON"]
    StableHIR --> Binder["Roslyn Semantic Binder"]
    Binder --> Bound["Bound semantic side tables"]
    StableHIR --> Projector["Effect-aware Graph Projector"]
    Bound --> Projector
    Projector --> Graph["Graph JSON"]
    Declaration --> Authoring["Authoring Surface Projector"]
    Graph --> Authoring
    Authoring --> Surface["Authoring Surface v0.4 pins + edges"]
    Surface --> Presentation["Lossless Graph Presentation policy"]
    Presentation --> Workbench["Browser / native UE GraphEditor"]
    Projector --> EmitGate["Execution Graph/CFG gate"]
    EmitGate --> Emitter["Deterministic PE + portable PDB"]
    Emitter --> SourceMap["Semantic ID -> MethodDef + IL/PDB source map"]
    Emitter --> Runtime["Collectible CoreCLR session"]
    SourceMap --> Runtime
    Runtime --> Reload["Image cache + reload generation"]
    SourceMap --> Breakpoints["Semantic breakpoint binder"]
    Reload --> Breakpoints
    Hit["Generation + token + IL hit"] --> Breakpoints
    Breakpoints --> Resolution["Semantic breakpoint/node resolution"]
    Invocation["Semantic method ID + JSON arguments"] --> Runtime
    Runtime --> Result["Typed return or structured fault"]
    Graph --> LayoutSync["Layout Synchronizer"]
    Layout[".usgraph sidecar"] --> LayoutSync
    LayoutSync --> UpdatedLayout["Updated .usgraph"]
    Graph --> Patch["Semantic Patch"]
    Patch --> Rewriter["Transactional Roslyn Rewriter"]
    Rewriter --> Source
```

## Project boundaries

### UnifiedScript.Core

- `VisualAttribute`
- `ScriptAssetAttribute` and `ExposeAttribute`
- stable identity
- source positions
- diagnostics
- HIR declarations, statements, and expressions
- HIR traversal and the language-neutral semantic patch contract
- identity sidecar schema, deterministic JSON, and random ID allocation
- engine-neutral Script Asset / Declaration IR schema and strict JSON
- common JSON conventions

This project has no Roslyn, graph UI, CLR hosting, or UE dependency.

### UnifiedScript.CSharp

- Roslyn parsing
- detection of `[Visual]` methods
- Visual Profile validation
- syntax-to-HIR lowering
- source range and deterministic ID assignment
- deterministic multi-document `CSharpCompilation` hosting
- Roslyn symbol/type/method translation into bound semantics
- transactional semantic-patch validation and localized source rewriting
- HIR structural fingerprinting, context reconciliation, pins, and tombstones
- Roslyn type/property/default/exposure lowering into Declaration IR
- persistent state-property reconciliation through the identity sidecar
- deterministic program-image and portable-PDB emission after Graph/CFG gates
- Declaration IR retained in the executable program image
- PE/PDB-derived semantic method/node source-map generation and strict JSON
- reconciled identity sidecars retained in the executable program image

It may depend on Core, Semantics, Graph, and Roslyn, but not on Runtime or UE.

### UnifiedScript.Semantics

- primitive and nominal type descriptors
- nullability annotations
- exact method descriptors and parameter types
- graph-safe signature classification
- pure/impure/latent effect flags
- HIR-node bindings for declarations, references, types, and calls
- versioned deterministic semantic JSON

It depends only on Core. It must never expose `ITypeSymbol`, `IMethodSymbol`,
CoreCLR hosting objects, or UE reflection objects. Roslyn and future UE adapters
translate into this contract.

### UnifiedScript.Graph

- graph document schema
- data and control ports
- deterministic HIR projection
- graph JSON serialization
- semantic-independent `.usgraph` layout schema and deterministic JSON
- node layout synchronization, explicit unplaced state, and orphan recovery

It depends on Core and Semantics. This lets later frontends render a typed,
effect-aware graph without hosting Roslyn.

### UnifiedScript.Runtime

- backend-neutral semantic-ID invocation and typed result/fault contracts
- strict execution-result JSON
- exact JSON-to-graph-safe-value conversion
- emitted-method resolution without leaking reflection objects
- collectible CoreCLR assembly loading and dependency resolution
- explicit reload-session lifetime and unloading
- immutable semantic-ID/IL/PDB source-map exposure without reflection leakage
- bounded content-addressed program-image cache and monotonic reload generations
- semantic breakpoint binding/migration and stale-generation hit rejection
- cooperative CoreCLR suspension, step-over, stack/value snapshots, and
  generation-safe loopback debug transport

It depends on Core, Semantics, and the C# emission boundary. It does not parse
source, project Graph, apply patches, or depend on UE. A collectible load
context is a reload mechanism, not a security sandbox.

### UnifiedScript.Unreal

- strict Unreal reflection manifest v0.1 model and canonical JSON
- engine version and selected-module identity without absolute engine paths
- recursive native type references for primitives, objects, containers,
  structs, and enums
- deterministic managed wrapper generation and stable unsupported-shape
  diagnostics
- handle-only `IUnrealInvocationBridge` boundary with no managed ownership
  claim over a UObject
- unsigned 64-bit slot/generation handle encoding shared with the native plugin

It depends only on Core. It does not reference UE assemblies or headers, scan a
local engine tree, load CoreCLR into UE, or reinterpret Declaration IR and
Authoring Surface. The native plugin translates `UClass`, `FProperty`, and
`UFunction` into this contract. The native runtime plugin now implements the
world-bound, game-thread-only weak handle registry and deletion invalidation.
Its first invocation slice resolves the same generated member paths and supports
validated `bool`, `int32`, `FString`, and void property/function dispatch through
`ProcessEvent`. `NativeUnrealInvocationBridge` now carries matching owned values
and stable failures into `IUnrealInvocationBridge`. A versioned native function
table and `InProcessUnrealNativeInvocationTransport` now provide the concrete
synchronous process boundary without sharing allocator ownership.
`UUnifiedScriptWorldSubsystem` now owns one registry and bridge per supported
World. Its ref-counted context detaches before handle invalidation and native
destruction, so retained managed transports fail safely after World teardown.
A process-wide `FUnifiedScriptCoreClrHost` now loads hostfxr from the selected or
engine-bundled .NET root, initializes the checked-in runtimeconfig, and acquires
the CoreCLR load-assembly delegate. Native World initialization calls an
`UnmanagedCallersOnly` managed entry point, which retains the transport and owns
the World's managed-script registry until World teardown. Each running component
script owns a collectible generation context: Start loads its configured assembly
and type, Tick dispatches the managed lifecycle, Reload creates a fresh context
before unloading the previous generation, and Stop runs before UObject handle
release. The demo drives all four operations through a generated wrapper against
a live UObject. Generated wrapper members now carry deterministic 64-bit FNV-1a
IDs. ABI v2 routes the component fixture through direct native fields and C++
methods without member-path parsing, reflected property lookup, parameter-buffer
construction, or `ProcessEvent`; ABI v1 remains a compatible reflection fallback.
Reflection now exports header and native-access metadata. The managed binding
command emits a deterministic companion C++ translation unit whose Member-ID
switches perform direct typed access for supported public members across any
selected reflected class. The checked-in runtime dispatcher is generated from
the same manifest as its managed wrapper. ABI v3 covers fixed scalars, text,
enums, handles, structs, containers, variable-size results, and return plus
`out`/`ref`; ABI v4 event proxies bind arbitrary reflected delegates. A separate
Transform Shared Data ABI publishes borrowed World snapshots, while Gameplay
Data ABI v4 schedules managed/native phases and carries fixed command, event,
and Batch Trace rows without exposing Unreal allocator or UObject layouts.

`UUnifiedScriptAsset` now owns the managed assembly/type identity as a native
UE Data Asset. A component can hold a Content Browser reference, load a soft
`ScriptAssetPath`, or use legacy direct fields. Start resolves and validates one
configuration snapshot. Reload resolves a new snapshot and loads its assembly
and type before stopping the current generation, so invalid asset edits leave
the previous script running. Cook-time dependency discovery remains open.

### UnifiedScript.Authoring

- deterministic multi-asset and multi-function authoring hierarchy
- complete Graph-node projection with UI-selectable expression compaction
- direction/kind/type-aware ports and compiler-owned control/data edges
- exact function/block/if/loop region ownership
- declaration references, source spans, and debug-target identities
- schema-driven literal/operator Semantic Patch capabilities
- strict Authoring Surface v0.4 JSON with endpoint/type invariants

It depends only on Core and Graph. It does not parse source, host Roslyn or
CoreCLR, render a browser/Slate UI, apply patches, or depend on UE. A frontend
consumes this projection instead of becoming a second Graph interpreter.

### UnifiedScript.Cli

- filesystem input/output
- diagnostics rendering
- HIR, bound semantics, and graph artifact emission
- Script Asset / Declaration IR artifact emission
- Authoring Surface artifact emission
- deterministic PE, portable-PDB, and semantic source-map artifact emission
- qualified Visual-method resolution and CoreCLR execution

The CLI is a test harness and integration boundary, not the final editor.

## Execution semantics

M5.1 emits executable code only after the same Visual Profile, compiler,
semantic, Graph, and derived-CFG gates used by analysis and patching. A static
synchronous `[Visual]` method is invoked by its existing semantic ID; JSON is
only the transport representation for bounded graph-safe arguments and results.
Returned values and target faults never expose `Assembly`, `Type`,
`MethodInfo`, exception, or source-defined object references outside the
collectible session. See [EXECUTION_RUNTIME.md](EXECUTION_RUNTIME.md).

## Executable identity

M5.2 reads the emitted PE and portable PDB to create
`unifiedscript.source-map/v0.1`. A method semantic ID resolves to its actual CLR
MethodDef metadata token. Each executable statement/expression semantic ID then
resolves to one or more IL offsets, and every offset is validated against the
same method's visible portable-PDB sequence points. The map is deterministic,
versioned, exposed by the collectible session, and generated equally for text
source and visual-patch-produced source. It is the location substrate for a
later breakpoint/stepping controller, not itself a debugger. See
[SOURCE_MAP.md](SOURCE_MAP.md).

## Reload and debugger protocol boundary

M5.3 gives each successful load a domain-scoped monotonic generation identity
and a `sha256:` program-image content address. A bounded LRU cache skips repeated
Roslyn emission for an identical request, while every reload still creates a
fresh collectible lifetime. Semantic breakpoint requests bind through the
source map, migrate to new IL/PDB locations when their persistent node survives,
remain pending when it is absent, and reject token/offset hits from stale
generations. Failed reloads preserve the previous executable session and
bindings transactionally. This is the transport-neutral boundary for a later
CLR/UE trap and stepping adapter. See
[BREAKPOINT_RELOAD.md](BREAKPOINT_RELOAD.md).

## Live debug adapter boundary

M5.4 emits a separate debug image after the original program has passed the
same profile, compiler, semantic, Graph, and CFG gates. Statement probes call a
shared-runtime sink while the actual method continues to execute as CoreCLR IL;
there is no HIR interpreter and no second VM. `CoreClrDebugAdapter` blocks the
real execution thread on a pause, implements continue/cooperative pause and
call-depth-aware step-over, and detaches stack frames plus bounded cycle-safe
locals from the collectible context. `ICoreClrDebugTransport` separates these
operations from `CoreClrDebugLoopbackTransport`, allowing a later native or UE
backend to replace probe mechanics without changing semantic IDs and stale
generation rules. See [DEBUG_ADAPTER.md](DEBUG_ADAPTER.md).

M5.5.4 exposes that boundary to the authoring prototype through a loopback-only
`debug-serve` host. Graph effect nodes carry `debugTargetId` when their visible
expression identity differs from the enclosing HIR statement probe. A
`ScriptAssetReceiver` constructs declared instance state from Declaration IR
IDs before invoking the same emitted CoreCLR method. The HTTP bridge adds no VM
and no alternate execution semantics.

M5.5.5 adds the write side without moving authoring policy into HTTP. A
`CSharpAuthoringWorkspace` prepares immutable candidate snapshots through the
existing Declaration Patch and Semantic Patch appliers. The loopback host
serializes authoring with debug control, refuses writes during a live execution,
checks the physical source and sidecar against its committed bytes, reloads the
same CoreCLR transport, and commits source plus reconciled identity sidecar as a
rollback-capable file transaction. Only then does it expose the next workspace
revision and program generation. HTTP is therefore an adapter over the compiler
transaction, not a second editor model or VM.

## Profile v0.2

Accepted statement forms:

- block
- one local declaration per statement
- expression statement
- `if/else`
- `while`
- lossless `do ... while`
- lossless text-authored `for`
- exact indexed `foreach` over `string` or a one-dimensional zero-based array
- synchronous disposal-aware `foreach` backed by one graph-safe
  `IEnumerable<T>` element contract
- synchronous graph-safe pattern `foreach`, including no-cleanup and
  runtime-conditional cleanup protocols
- borrowed `Span<T>`/`ReadOnlySpan<T>` iteration with explicit `ref readonly`
  value semantics
- `break` and `continue` inside an enclosing `while`, `do`, `for`, or supported
  `foreach`
- `return`

Accepted expression forms:

- literals and identifiers
- `this`
- member access
- invocation
- binary and prefix unary operators, plus postfix `++`/`--`
- assignment
- cast
- `await`

Explicitly rejected forms include dynamic binding, unsafe code, pointers, goto,
yield, local functions, lambdas, exception control flow, locks, LINQ query
syntax, stack allocation, ref/out/in parameters and arguments, generic visual
methods, ambiguous enumerator element contracts, mutable ref/async `foreach`,
switch, and all other syntax
not listed above.

## Graph semantics

The current graph schema separates control edges from data edges. Statement
nodes participate in control flow; expression nodes form data dependencies.
Pure expression statements use an explicit `Evaluate` control node. Effectful
calls, assignments, and awaits instead expose their own control ports, so nested
side effects are sequenced before their data consumers.

Graph schema v0.12 separates structured ownership from connectivity. Every
function has a `function` root and method-body `block`; an `if` region owns its
condition/branch nodes plus ordered `then` and `else` children. Every Graph node
has exactly one direct region owner. An omitted C# `else` remains an empty
`isImplicit` region, while an explicitly empty `else` is non-implicit. Nested
lexical blocks and branches form child regions instead of being reconstructed
from edge geometry. The complete contract is documented in
[GRAPH_REGIONS.md](GRAPH_REGIONS.md).

A `while` owns one `loop` region with one `loopBody` child. Its Graph decision
node exposes `body` and `exit` control ports. Derived loop facts associate
`break` and `continue` with the nearest loop without moving those statements
out of their lexical regions. Normal body fallthrough and continue edges return
to the effective condition header; if the condition contains effects, that
header is the first effect rather than the final decision node. See
[LOOP_FOUNDATION.md](LOOP_FOUNDATION.md).

A `do ... while` also owns one `loopBody`, but Graph v0.12 adds a generated
`loopEntry` control node for the one-time initial body entry. Body fallthrough
and `continue` target the first condition effect or the decision node; a true
condition returns to the body header and a false condition exits. This topology
proves at-least-once execution without overloading one decision node with two
semantically different entry ports. See [DO_WHILE_LOOPS.md](DO_WHILE_LOOPS.md).

A `for` remains a distinct HIR statement rather than being desugared to
`while`. HIR v0.7 separately preserves its declaration or expression
initializers, optional condition, ordered incrementors, and body. Graph v0.12
owns ordered `loopInitializer`, `loopBody`, and `loopIncrement` child regions.
Initializer exits enter the effective condition header once; body fallthrough
and `continue` enter the increment phase; completed incrementors return to the
condition header. Prefix/postfix unary position is explicit, and `++`/`--`
nodes carry control ports as mutation effects. Derived loop facts publish a
separate continue target so CFG validation does not incorrectly route a `for`
continue like a `while` continue. See [FOR_LOOPS.md](FOR_LOOPS.md).

A supported `foreach` owns ordered `loopCollection` and `loopBody` children.
Indexed string/array loops stop there and record `collectionStrategy: indexed`
plus `disposal: none`. Synchronous `IEnumerable<T>` loops add `loopCleanup`:
the collection phase captures once and acquires the bound enumerator, move-next
and current binding guard every body entry, and three disposal nodes preserve
normal/break, return, and exceptional transfers with `finally` semantics.
Pattern-only, conditional-disposal, ref, and async variants are rejected rather
than approximated. See [FOREACH_LOOPS.md](FOREACH_LOOPS.md).

Regions are not a CFG cache. Control edges remain authoritative for execution;
M4.2 now derives synthetic exits, predecessor/successor indexes, reachability,
dominance, post-dominance, structured merges, and SCC reducibility without
persisting those recomputable facts in Graph JSON. `HirGraphProjector` validates
the derived CFG before returning a Graph. See
[CONTROL_FLOW_ANALYSIS.md](CONTROL_FLOW_ANALYSIS.md). Semantic regions remain
independent from layout-only `.usgraph` comment regions.

The first semantic slice now classifies Roslyn builtin operators and
`System.Math`/`System.MathF` calls as pure. Unknown calls default to
`ReadsState | WritesState`; task-like or async calls additionally become
`Async | Latent`. Effectful calls, assignments, and awaits receive explicit
control ports and are sequenced in C# evaluation order. Versioned effect catalog
rules can override method effects without introducing Roslyn dependencies into
the semantic contract.

`CSharpCompilationRequest` canonicalizes source order, rejects duplicate logical
paths, fingerprints explicit metadata references, and carries the effect
catalog. Multi-document HIR compilation identity depends on assembly name,
logical paths, and source digests. Bound semantics additionally carry a
configuration ID so the same source analyzed with different references or
effect rules cannot be mistaken for the same semantic result.

Single- and multi-document HIR compilation IDs now both include assembly name,
logical source paths, and SHA-256 source digests. This content-sensitive ID is
the optimistic-concurrency baseline for graph-to-source patches; path-only
compilation identity is not safe for editing.

## Semantic patch transaction

Graph schema v0.13 carries both the source compilation ID and semantic
configuration ID. A `unifiedscript.patch/v0.28` document repeats those IDs and
contains typed operations keyed by graph/HIR `SemanticId`. Implemented
operations are `replaceLiteral`, `changeOperator`, `reconnectArgument`,
`replaceExpressionInput`,
`replaceAssignmentValue`, `replaceWhileCondition`, `replaceDoWhileCondition`,
`replaceForCondition`, `replaceForEachCollection`,
`replaceForDeclarationInitializer`, `replaceForAssignmentInitializerValue`,
`replaceForIncrementor`, `replaceIfCondition`,
`replaceLocalInitializer`, `replaceReturnValue`, `insertStatement`,
`deleteStatement`, `introduceTemporary`, and `reconnectValue`.
`insertStatement` accepts a local-literal declaration, a
CFG-checked assignment to an existing local, an `if`, `while`, `doWhile`,
bounded `for`, or supported indexed/enumerable `forEach` with closed typed inputs and explicit
blocks, a nearest-loop
`break`/`continue`, or a typed terminal `return`. Structural operations carry old
operator/endpoint, semantic type,
containing statement, parent block, and exact sibling preconditions rather than
relying only on the compilation baseline.

`unifiedscript.patch-plan/v0.21` is an ordered transaction above individual
patches. It composes every current bounded operation: literal and operator
changes, argument reconnection, structured assignment/while/do/for/foreach/branch/local/
return expression edits, exact nested expression-input replacement, statement
insertion/deletion, temporary introduction, value
reconnection, symbolic branch-body insertion, symbolic loop-body insertion,
and symbolic replacement of expression endpoints on compatible statements
produced earlier in the same plan, including explicit conditions on generated
while, do-while, for, and foreach loops.
`introduceTemporary` and local-literal root, branch, or loop-body insertion
steps publish symbolic local handles. Later `reconnectValue` steps can consume
an entire value port, while transaction-only `producedLocalReference` leaves
can use the same handle inside nested authored expression trees. The
coordinator resolves both forms to an ordinary local definition ID instead of
guessing a random ID that does not exist in the initial HIR. The complete plan still
carries one external source/configuration baseline and records every target's
original value, endpoint, statement, block, sibling, and type preconditions.
Every expression-bearing stage also publishes its actual post-reanalysis root
under the stage `stepId`. A later `replaceProducedExpressionInput` validates a
`targetPath` against the producer's authored tree, follows that path from the
captured HIR root, and lowers to the existing standalone
`replaceExpressionInput` operation with real current endpoint IDs. The
consumer's replacement root is published again, so handles chain without a
second VM, graph evaluator, or generated-ID prediction scheme.
Current HIR v0.7 preserves a conditional expression as one node with condition,
true, and false children. Graph v0.12 projects that node as a `conditional`/`Select`
with canonical `condition`, `whenTrue`, and `whenFalse` inputs and an explicit
`evaluation: lazyBranches` contract. Semantic binding rejects effectful arms so
the data projection cannot eagerly execute mutation, latency, or effectful
calls that C# would evaluate only on the selected path. Patch v0.20 and plan
v0.19 use the same conditional shape in their closed expression algebra and
allow exact or produced-handle edits through those Graph ports.
The same HIR already preserves `&&` and `||` as ordered binary nodes. Graph
v0.10 retains their `shortCircuitRight` contract, with
`whenLeftTrue`/`whenLeftFalse` selecting the operator-specific condition.
Always-evaluated left effects stay in the statement control chain. Semantic
binding rejects effectful right subtrees with `UVS2003` until Graph has a
conditional effect-subgraph contract. The full boundary is specified in
[SHORT_CIRCUIT_EXPRESSIONS.md](SHORT_CIRCUIT_EXPRESSIONS.md).
Null coalescing also remains an ordered binary HIR node. Graph v0.12 marks its
right input `whenLeftNull` and its result selection `leftNonNullOrRight`.
Roslyn remains authoritative for candidate text-authored operand/result types;
`UVS2005` then rejects implicit or user-defined conversions absent from the
Graph contract. Graph-authored construction accepts the same exact nullable
scalar families. Effectful right subtrees produce `UVS2004`; left effects
remain in the statement control chain. See
[NULL_COALESCING_EXPRESSIONS.md](NULL_COALESCING_EXPRESSIONS.md).
Null-conditional access is a distinct HIR node owning an always-evaluated
receiver and a conditional `whenNotNull` subtree. A conditional member binding
keeps the receiver implicit in HIR while Graph v0.12 makes both relationships
explicit: the receiver feeds the parent `receiver` input and the binding's
`target` input. The parent records `conditionalWhenNotNull`,
`whenReceiverNonNull`, and `whenNotNullOrNull`, so a VM can evaluate once,
skip the selected subtree on null, and lift the result. `UVS2006` rejects
effects in the selected subtree and `UVS2007` rejects void or hidden-conversion
type shapes. Patch v0.20 can author a bounded simple conditional member access;
plan v0.20 can replace its `receiver` through an actual produced root. See
[NULL_CONDITIONAL_ACCESS.md](NULL_CONDITIONAL_ACCESS.md).

Semantics v0.3 also records Roslyn's flow-sensitive null state per expression.
Graph nodes expose `typeNullability` separately from `nullFlowState`, and the
same properties reach every Authoring Surface client. A guard can therefore
narrow a nullable value to `notNull` at one use while a later assignment returns
it to `maybeNull`, without relying on source-text heuristics. See
[NULLABILITY_FLOW.md](NULLABILITY_FLOW.md).
An `if` insertion publishes internal `then`/`else` block handles for later
`insertBranchStatement` steps without masquerading as a reconnectable local
definition. The branch steps repeat the producer's original parent block as
their stable initial anchor; the coordinator records the generated branch IDs
after the `if` stage and turns each append into a normal bounded insertion.
Local-literal, existing-local assignment, nested `if`/loop, nearest-loop
transfer, and typed-return payloads are accepted in a produced branch. A nested
`if` publishes another pair of branch handles, forming an acyclic producer
tree. A return terminates
one path; when both child paths terminate, termination propagates to the parent
only if the nested `if` is still the parent's final statement. This rejects
both direct and retroactive unreachability before execution while keeping
unrelated sibling paths independently writable.
A `while`, `do`, `for`, or `foreach` insertion publishes one generated body handle for later
`insertLoopBodyStatement` steps. A declaration-form `for` additionally
publishes its single generated declarator as a produced-local handle; normal
lexical/CFG availability prevents use outside that loop. A `foreach` similarly
publishes its iteration variable, but only inside the generated body. Indexed
and enumerable strategies use the same construction handle; semantic
reanalysis chooses the exact VM strategy from the collection type. Loop bodies accept the
same bounded statements, including nested `if`/loop producers and nearest-loop
transfers. Terminal state stops at the loop boundary because a loop condition
may be false on entry; execution and CFG validation still determine the actual
current flow. The `for` constructor separately preserves initializer form,
optional condition, ordered prefix/postfix mutations, declaration scope, and
continue routing through the increment phase.
The complete transaction and internal rebasing contract is specified in
[PATCH_PLAN.md](PATCH_PLAN.md).
The endpoint replacement and generated-statement handle contract is specified
in [GENERAL_EXPRESSION_EDITING.md](GENERAL_EXPRESSION_EDITING.md).
The generated-local data-flow contract is specified in
[PRODUCED_VALUE_REFERENCES.md](PRODUCED_VALUE_REFERENCES.md).
The exact Graph-input edit and unaffected-spine identity contract is specified
in [EXPRESSION_INPUT_PATCHING.md](EXPRESSION_INPUT_PATCHING.md).
The lazy conditional-expression projection and round-trip contract is
specified in [CONDITIONAL_EXPRESSIONS.md](CONDITIONAL_EXPRESSIONS.md).

The C# patch boundary performs these steps without mutating files:

1. analyze the current source request;
2. verify source and semantic-configuration baselines;
3. resolve every target and connection endpoint ID to the expected HIR kind;
4. verify operation-specific value, operator, endpoint, type, sibling,
   symbol-use/definition, name, and effect preconditions;
5. build localized Roslyn syntax replacements while preserving trivia;
6. reject overlapping source ranges and apply every operation in memory;
7. rerun profile validation, compilation, semantic binding, and graph safety;
8. return patched documents only if the complete transaction succeeds.

The CLI currently writes a successful single-document result and updated
sidecar through same-directory temporary files with backup-based rollback.
Failed patches produce neither output. A Roslyn annotation maps each explicit
replacement to an identity pin in the proposed source, allowing `1` to become
`100` without changing that node's persistent ID. Operator roots and swapped
invocation arguments use the same pin mechanism. Statement insertion pins the
existing block and direct siblings while leaving the new declaration and
literal unpinned, so they receive fresh persistent IDs. Safe deletion pins the
block and surviving siblings while the deleted subtree becomes tombstones.
When an insertion replaces a nested block, unaffected live nodes elsewhere in
the same document are also pinned. Nodes that contain the replaced block are
pinned as unaffected ancestors rather than incorrectly treated as part of the
replacement subtree. This preserves every enclosing `if` and Block ID during
recursive branch population and prevents newly authored same-shape syntax from
taking an exterior or ancestor node's persistent ID during staged execution.

Temporary introduction uses a HIR use/definition index and an expression
location index. It moves a stable expression into a new local declaration
immediately before its current return or local-declaration statement, then
replaces the old occurrence with an identifier. The complete containing value
expression must be effect-free and referentially stable, so the transformation
cannot cross state reads, mutations, awaits, throwing/effectful calls, or
unknown evaluation. Every pre-existing expression in the changed statement is
explicitly pinned; only the new declaration and identifier receive new IDs.
Short-circuit, division/remainder, decimal, and string operators remain outside
this proof until builtin operator effects include conditional evaluation,
allocation, and throw behavior.

M4.3 adds a `GraphPatchSafetyIndex` inside the C# patch boundary. It projects
the same analyzed HIR and bound semantics through Graph v0.12, derives the M4.2
CFG, and resolves statement entries or region fallthrough sources as insertion
anchors. A non-empty block whose paths terminate has no fallthrough anchor.
Local assignment targets and graph-authored local references must satisfy both
C# lexical dominance and Graph reachability/dominance; the Graph does not
replace language scope. See
[LOCAL_ASSIGNMENT_PATCHING.md](LOCAL_ASSIGNMENT_PATCHING.md).

Typed value reconnection uses the same expression-stability predicate plus the
lexical and CFG indexes. The source must be an existing local in the same
method, its declaration statement must strictly precede and dominate the
reachable consumer statement, its name must not be shadowed, and its bound
graph-safe type must exactly equal the consumer type. Replacing a consumer
never moves or duplicates evaluation: it removes one proven-stable subtree and
inserts a local identifier at the same syntax position. The writable target
subtree of an assignment is never a value consumer and cannot be reconnected.
Comments or directives inside the removed subtree make the edit unsafe.
Surviving syntax is pinned; removed IDs are tombstoned; the new identifier
receives a new ID and must rebind to the requested source declaration during
full reanalysis.

Argument reconnection is deliberately bounded to exchanging two pure, exactly
type-compatible positional arguments of one resolved invocation. The
implemented temporary-introduction primitive preserves C# evaluation count and
order for one extracted value, and `reconnectValue` can connect later,
dominated consumers to that definition. Statement editing is bounded to local
declarations with a literal initializer, simple assignments to existing
locals, typed non-async returns at the end of explicit blocks, and deletion of
unused literal locals or pure static-call statements. Assignment and return
values reuse the closed graph-authored scalar expression subset and must
exactly match the bound target type. Call insertion, declaration renaming,
existing-assignment retargeting, and general cross-expression/data-port
rewiring remain future patch-algebra work.

The C# patch-plan coordinator validates every independently executable step in
isolation against the initial HIR before changing its in-memory request;
symbolic reconnections receive equivalent initial statement, sibling, method,
and type checks, while dependent branch insertions validate their original
anchor, producer method, selector, payload shape, and recursive terminal-path
state. It then executes one localized patch at a time, adopts the stage's
updated sidecar, removes transient pins, resolves any produced local or branch
handle, and constructs the next internal patch with the current compilation
baseline. Statement deletion uses current siblings; repeated root-block
insertion rebases the original gap while retaining plan order; argument
reconnection relocates current indices from persistent endpoint IDs; branch
insertion appends to the currently resolved produced block and may register
another pair of produced blocks. Each stage performs complete semantic
reanalysis. A failure discards every intermediate request; only the
final request and sidecar cross the CLI's rollback-capable transaction. Plans
therefore require persistent identity on target documents. Repeated target IDs
are allowed only for ordered insertions into one block; other operations retain
one original old-value/precondition set per target.

## Visual layout boundary

`unifiedscript.graph-layout/v0.1` is a non-semantic sidecar keyed by function,
node, and edge `SemanticId`. It stores node placement and collapsed state,
comment regions, and routing hints but deliberately excludes source compilation
IDs, source spans, types, effects, and editable program properties. The complete
contract is specified in [GRAPH_LAYOUT.md](GRAPH_LAYOUT.md).

Layout synchronization is a one-way reconciliation against a current Graph
document. Surviving nodes keep their canvas state, new nodes become explicitly
unplaced, and missing nodes become reasoned orphans. Comment membership and
routing hints are restricted to current nodes and edges. The synchronizer has
no dependency on the C# project, receives no HIR or identity sidecar, and
returns no semantic artifact; canvas changes therefore cannot silently become
program changes.

Visual method signatures currently accept void, booleans, integer and floating
primitives, strings, enums, nullable forms of supported value types, and
task-like wrappers with safe results. Arbitrary classes, interfaces, delegates,
arrays, type parameters, and user structs are rejected with `UVS2001` when used
as visual parameters or return values. They may still appear internally as
receivers until a richer graph-safe object-handle policy is defined.

## Error strategy

- Syntax errors stop HIR emission.
- Visual Profile errors stop HIR emission.
- Unsupported syntax always has a stable `UVS` diagnostic code.
- Compiler or semantic errors stop bound semantics and graph emission.
- Graph-unsafe visual signatures produce stable `UVS2001` diagnostics.
- The CLI does not emit partial artifacts after an error.
- Lowering throws only when validator and lowerer support lists disagree; that is
  an implementation defect covered by tests, not a user-facing failure mode.

## Testing strategy

- identity determinism tests;
- one test per profile diagnostic family;
- C#-to-HIR structural tests;
- HIR-to-graph control/data tests;
- deterministic serialization tests;
- golden digests for versioned HIR, semantics, and graph fixtures;
- source/graph/source round-trip tests for values, operators, argument edges,
  bounded statement edits, temporary introduction, and typed local-to-consumer
  reconnection;
- atomic plan tests for overlapping multi-consumer edits and late-stage rollback;
- graph-layout schema, deterministic serialization, orphan restoration, and
  semantic-patch synchronization tests;
- later, semantic equivalence tests between graph-edited and text-edited IL.

## Current UE boundary

M6.1 and M6.2 now provide the reflection/API contract, generated C# wrappers and
C++ thunks, generation-checked weak UObject handles, versioned ABI v3/v4
transports, and one native CoreCLR host scoped to each supported World. The
component and Actor hosts cover generated gameplay assembly loading,
Start/Tick/Stop, reflected events, latent generation cancellation, collectible
reload, and Declaration IR state materialization with stable-ID migration.

For persistent Script Classes, the Editor's post-deployment class materializer
also projects reviewed Declaration IR state onto the exact generated Blueprint.
`bool`, `byte`, `int`, `long`, `float`, `double`, and non-null `string` become
ordinary compiled `FProperty` fields; the semantic state ID becomes the
Blueprint variable GUID, while generated-owner/type/previous-default metadata
supports stable rename, stale-field removal, and preservation of intentional
Blueprint class-default overrides. A complete preflight rejects user-owned name
or GUID collisions, unsupported types, and unsafe overridden type changes before
the real Blueprint is touched. Managed callbacks hydrate and flush those fields
through strongly typed accessors cached by weak script-type identity and the
existing invocation bridge, preserving the fast path without retaining a
collectible hot-reload generation. ActorComponent classes use the component host
handle; Actor/Object classes use the target handle.

Flat collection state preserves whole-value overrides across Blueprint
reinstancing. For loaded ordinary Blueprint descendants, each CDO and live
instance is compared with its immediate archetype. Overrides keep an owned
value snapshot; followers copy their archetype's final value after overrides
are restored in inheritance order. Stable state IDs resolve renamed properties,
the engine replacement map fixes object references, and Set/Map values are
rehashed after restoration. Both validation-only and apply reject incompatible
type changes when a descendant owns an override. This does not automatically
save every dependent Blueprint or cover templates other than derived CDOs.

Before adding a state field, synchronization checks loaded descendants' own
user Blueprint variables for name collisions. Validation-only and apply reject
the conflict before changing the original hierarchy, preserving the user's
variable name, GUID and value instead of allowing the compiler to rename it.
New fields use Unreal's normal default propagation; the instance snapshot path
remains responsible for existing collection overrides and their followers.

Reviewed public non-event `[Visual]` instance methods also project onto that
exact Blueprint as stable `USF_<semantic-id>` UFunctions. Their managed name is
display/binding metadata. Declaration-patch renames preserve the sidecar's
function identity through HIR, binding, Graph and execution, while stale
generated graphs can be removed independently of user graphs. A product-owned
Blueprint GeneratedClass stores the binding table; a dedicated Kismet compiler
keeps that carrier across initial compile and recompile for every proxy parent
kind, including types without a construction script. After compile or load, one
generic native thunk serves every managed function rather than generating a
handwritten C++ thunk per function. The GeneratedClass persists the semantic
binding table, rebuilds its transient native function map during bind, load,
default-object load, and instance initialization, and prepares those functions
for safe Blueprint recompilation before the old generated class is replaced.

The first dispatch ABI remains frozen as one 16-byte tagged scalar carrying
`bool`, `byte`, `int`, `long`, `float`, or `double`. ABI v2 keeps its
zero-to-four argument contract but reuses the published 48-byte ABI v3 value
cell and 72-byte borrowed-arena call frame for `String`, `ObjectHandle`, and
`FVector`. Instance calls resolve their Script Component and World-scoped
instance ID; static BlueprintFunctionLibrary calls resolve their Script Asset
and a World-owned collectible library context. Both paths invoke strongly typed
expression delegates compiled once per generation. No JSON, `MethodInfo.Invoke`,
or handwritten native thunk appears on the call path. `UnrealObjectScript`
functions hydrate/flush reflected state at the call boundary, while World
teardown disposes every static library context. Public inherited `[Visual]`
instance declarations share the same path and retain their base semantic IDs.
Generated enum wrappers carry the exact reflected `/Script/...` path and raw
numeric value. Generated UStruct records carry exact native field names and
recursive field order. Roslyn lowers both shapes into Declaration IR, which is
embedded in the compiled chunk and staged atomically with the readable
`.asset.json`. The materializer resolves exact `UEnum`/`UScriptStruct` pins and
persists the recursive runtime type and field-path binding on the product
GeneratedClass. `PostLoadDefaultObject` restores the generic dispatch pointer
after package load. ABI v2 then marshals enums and nested reflected structures,
including `FHitResult` and vector-derived fields, from the persisted binding
without JSON, per-call reflection, or handwritten per-function native thunks.

Roslyn also lowers stable Actor and ActorComponent lifecycle hooks
(`OnBeginPlay`, `OnTick`, and `OnEndPlay`) plus reviewed
`[UnrealBlueprintEvent]` methods with an explicit `Super` policy. The
materializer persists their lifecycle/event bindings and marks managed event
functions with `FUNC_Event | FUNC_BlueprintEvent`. A child Blueprint override
replaces the managed default unless its ordinary Kismet graph contains an
explicit `Call Parent Function`. That parent call stays in the child ubergraph;
the generic dispatcher resolves the invoked base function through
`Stack.CurrentNativeFunction`, decodes Blueprint bytecode arguments with
`Stack.Step` into an `FStructOnScope`, and then uses the same ABI and per-World
managed instance as direct `ProcessEvent` dispatch. Compatible reload and World
teardown therefore preserve one ownership model for lifecycle, direct calls,
and Blueprint-event calls.

The control plane handles resources, uncommon signatures, properties,
functions, delegates, and complex values. The data plane publishes borrowed
Transform snapshots and runs a per-World pre/managed/post scheduler: managed
scripts reserve fixed command rows, native executes them after Actor tick,
completion events publish on the next frame, and line/sphere/box traces execute
through one synchronous batch boundary. World-scoped handles and explicit
borrow lifetimes prevent CoreCLR from owning Unreal memory.

The editor consumes the compiler-owned Authoring Surface in a native Slate
GraphEditor. It supports Blueprint-style presentation, API/event node palettes,
typed creation and deletion, structured insertion, occupied-input data rewiring,
undo/redo transactions, and persisted per-function graph layout. The Workbench
atomically republishes source, identity, Declaration, Surface, DLL, and PDB
before matching component instances enter transactional reload. Win64
Development and Shipping cook/package gates stage the managed dependency
closure and pass packaged lifecycle smoke.

The remaining boundary work is:

- arbitrary control-wire authoring beyond structured block operations;
- Enhanced Input, delegate, interface, and remaining event-shape authoring
  contracts on the per-World managed instance;
- native debug traps, deeper locals, expression evaluation, and remote framing;
- additional shared-memory gameplay snapshot and batch schemas;
- platform-specific CoreCLR packaging beyond Win64.

None of these concerns may redefine HIR semantics implicitly.

## Current semantic-host boundary

The M2 host compiles one or more source documents directly or loads an
SDK-style `.csproj` through the selected .NET SDK's MSBuild. The project adapter
consumes evaluated Compile, ReferencePath, and ReferenceCopyLocalPaths items;
language version, target-framework constants, nullable/unsafe/overflow options,
NuGet assets, and built project-reference outputs enter the same deterministic
compilation request. Downstream Semantics and Graph modules continue to consume
the compilation-neutral contracts. Source-generator/analyzer replay and UE
symbol-assembly import remain outside this boundary.
