# UnifiedScript

UnifiedScript is an experimental dual-modal gameplay authoring system. A
restricted C# profile and a structured visual graph are intended to be two
lossless projections of one program model. Roslyn and CoreCLR are the active
execution backend. The language and projection model are proven independently,
and the versioned Unreal adapter has completed its adoptable-project alpha,
M7.2 gameplay services/lifecycle productization, and M7.3 schema-generated
gameplay data plane without adding UE dependencies to the compiler core. M7.4
then completed the full BlockBreaker migration with exact inventory coverage
and no Puerts/TypeScript payload in the target project or package. M8.4 then
completed a real Player/Rifle Visual-to-C#-to-Visual round trip with semantic,
IL, execution, Script Asset, UE gameplay, and dual packaged-build equivalence.
M9-M11 complete observable hot reload, gameplay profiling, matched performance,
reliability, packaging, compatibility, and compiled-chunk delivery. M12 now
turns the proven runtime into a migration ecosystem and UnifiedScript 1.0; its
first slice is a generalized, bounded Puerts project scanner.

## Product roadmap

[docs/ROADMAP.md](docs/ROADMAP.md) is the canonical execution queue from the
current packaged BlockBreaker demo to an adoptable, production-ready Puerts
alternative. It records the verified baseline, active milestone, next vertical
slice, acceptance gates, and the longer path through runtime coverage,
visual/C# authoring, debugging, performance, release hardening, and migration.
An unqualified request to continue iteration follows its current cursor.

The M6.3 project entry point now applies the complete generated-contract path:

```powershell
powershell -ExecutionPolicy Bypass -File tools/setup-unreal-project.ps1 `
  -Project HostProject/UEUnifiedScriptHost.uproject -EngineRoot D:\UE_5.8
```

Re-run with `-ValidateOnly` to preflight Script Assets, compare deterministic
generated outputs, enforce binding coverage, and execute the configured managed
startup gate without rewriting project source. See
[Unreal project onboarding](docs/UNREAL_ONBOARDING.md).

M7.2 adds generated World services plus the World-scoped Async Service ABI v5
for Delay, async object/class loading, timeout, cancellation, reload ownership,
and teardown safety. See [Async Service ABI v5](docs/UNREAL_ASYNC_SERVICE_ABI_V5.md).
Its deletion/travel/PIE/multi-World/reload soak can be reproduced with:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/run-unreal-lifecycle-stress.ps1 -EngineRoot D:\UE_5.8
```

The v0.2 soak now adds 16 successful and 16 intentionally failed transactional
reloads, reflected Host-event rebinding, stale-debug cleanup, and zero retained
collectible contexts. See
[Transactional hot-reload reliability](docs/HOT_RELOAD_RELIABILITY.md).

M7.3 generates Shared/Gameplay fixed layouts from one versioned schema and adds
bounded telemetry plus Batch Overlap. Its live CoreCLR routing profile is:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/run-unreal-data-plane-profile.ps1 -EngineRoot D:\UE_5.8
```

M9.3 adds an opt-in 120-frame gameplay profiler. The Unreal Debug panel can
capture or stop a live trace, focus the hottest resolvable semantic node, and
save `unifiedscript.gameplay-profile/v0.3` JSON with per-node runtime-boundary
cost under
`Saved/UnifiedScript/Profiles`. See
[Gameplay and bridge profiler](docs/GAMEPLAY_PROFILER.md).

M7.4 adds a deterministic Puerts-project inventory and exact migration coverage
gate. Re-run the whole-project BlockBreaker proof with:

```powershell
powershell -ExecutionPolicy Bypass `
  -File comparison/audit-blockbreaker-migration.ps1 `
  -EngineRoot D:\UE_5.8 `
  -ComparisonRoot D:\Project\UEUnifiedScriptComparison
```

The gate covers all 74 discovered scripts, types, methods, lifecycle hooks,
imports, Puerts/Unreal APIs, resources, globals, async patterns, decorators,
delegates, and native facades. It also requires 19 byte-identical gameplay
assets, two Script Assets, 40 generated native thunks, and zero legacy targets.
See [M7.4 BlockBreaker migration](docs/milestones/M7_4_BLOCKBREAKER_MIGRATION.md).

M12.1 generalizes that project-specific proof. Scan any Puerts project into a
strict deterministic report with source ownership, modules, APIs, lifecycle,
assets, delegates, async work, native surfaces, and unsupported dynamic behavior:

```powershell
dotnet run --project src/UnifiedScript.Cli -c Release -- `
  ue-puerts-scan D:\Project\MyPuertsGame `
  --out artifacts\migration\my-game.scan.json `
  --fail-on-blockers
```

See [Puerts project migration](docs/PUERTS_MIGRATION.md).

Run the M8.4 full Visual/C# round-trip proof with:

```powershell
powershell -ExecutionPolicy Bypass `
  -File comparison/verify-blockbreaker-roundtrip.ps1 `
  -RunUnreal -Package
```

It applies one atomic project-level Visual patch to the real Player and Rifle
scripts, emits C#, applies behavior-preserving text edits, reopens equivalent
Graphs, and runs both candidates through the same UE map and independently
packaged executable. See
[BlockBreaker Visual/C# round-trip proof](docs/BLOCKBREAKER_ROUND_TRIP.md).

Create a repository-independent alpha SDK with:

```powershell
powershell -ExecutionPolicy Bypass -File tools/package-unifiedscript-sdk.ps1 `
  -Version 0.1.0-m6.3-alpha.1 -EngineRoot D:\UE_5.8
```

The deterministic ZIP contains a hash-verified installer, source plugin,
managed runtime, CLI, schemas, templates, and documentation while excluding
engine-specific native outputs. See [Unreal SDK distribution](docs/UNREAL_SDK.md).
To take a newly installed C++ project from no scripts to editor play, visual
editing, hot reload, and packaged play, follow
[First UnifiedScript in an Unreal project](docs/UNREAL_FIRST_SCRIPT.md).

Run the complete repository alpha gate with:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/run-unifiedscript-alpha-acceptance.ps1 `
  -EngineRoot D:\UE_5.8
```

It emits one machine-readable report and one concise summary covering managed
tests, repository validation, UE generation/build/Automation, and packaged
CoreCLR smoke. See [Alpha acceptance](docs/ALPHA_ACCEPTANCE.md).

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
- exact typed editing of existing assignment values and while conditions,
  including repeatable condition edits through produced-loop plan handles;
- exact typed editing of existing local initializers, `if` conditions, and
  return values, including repeatable edits through generated-statement handles;
- transaction-only typed references from later expression trees to locals
  produced earlier in the same atomic plan;
- exact nested expression-input replacement through the same `left`, `right`,
  `operand`, and positional `argN` ports emitted by Graph;
- transaction expression handles that let later stages traverse and edit trees
  authored by earlier stages without predicting generated semantic IDs;
- first-class conditional (`?:`) expressions across HIR v0.7 and Graph v0.12,
  with lazy-branch safety, typed source synthesis, canonical branch ports, and
  generated-tree handle traversal;
- explicit `&&`/`||` right-side evaluation conditions in Graph v0.12, with
  always-evaluated left effects and `UVS2003` rejection of conditional right
  effects;
- null-coalescing (`??`) across text, HIR, Graph v0.12, exact visual
  construction, and produced-expression editing, with `UVS2004` effect safety;
- null-conditional member access (`?.`) across HIR v0.7 and Graph v0.12, with
  explicit single receiver evaluation, lifted nullable results, `UVS2006`/`UVS2007`
  safety, bounded visual construction, and exact receiver editing;
- lossless text-authored `for` loops across HIR v0.7 and Graph v0.12, with
  declaration/expression initializers, optional conditions, ordered incrementors,
  prefix/postfix mutation identity, and CFG-validated continue-to-increment routing;
- dedicated bounded `for` construction through patch v0.23/plan v0.21, with
  declaration or assignment initialization, scoped for-local references,
  ordered `++`/`--`, symbolic body/local handles, and atomic CFG revalidation;
- exact editing of an existing `for` condition, declaration initializer,
  assignment-initializer value, and `++`/`--` incrementor, plus a generated-for
  condition handle, without replacing the loop or changing clause ownership;
- lossless `do ... while` across HIR v0.7 and Graph v0.12, with a distinct
  initial-body entry, post-body condition evaluation, condition-targeted
  `continue`, reducible CFG facts, bounded visual construction, and exact
  existing/produced condition editing;
- exact indexed `foreach` over strings and one-dimensional zero-based arrays,
  with evaluate-once collection capture, current-value scope, move-next
  continue routing, bounded visual construction, produced iteration-local
  handles, and exact existing/produced collection editing;
- disposal-aware synchronous `IEnumerable<T>` foreach through semantics v0.2
  and Graph v0.12, with bound enumerator protocol IDs, explicit acquisition,
  normal/return/exception cleanup, CFG ownership facts, and the same visual
  construction/editing protocol;
- lossless `switch` and bounded C# patterns across HIR v0.8 and Graph v0.13,
  with evaluate-once governing values, typed pattern variables, guarded case
  routing, editable section-body ownership, and nearest-switch `break` facts;
- region-aware control reconnection through patch v0.23 `moveStatement`, with
  exact source/destination block and sibling preconditions, persistent identity,
  cross-branch/loop/switch-section movement for ordinary statements, local-order
  and terminal-transfer guards, and regenerated-CFG validation;
- opt-in `.usids` sidecars that preserve node identity across formatting and edits;
- backend-neutral Script Asset / Declaration IR v0.1 with scalar and enum
  state, canonical defaults, engine exposure metadata, Visual signatures, and
  strict deterministic JSON;
- engine-neutral Authoring Surface v0.4 projected from Declaration IR and Graph,
  with multi-asset/function navigation, exact structured-region ownership,
  compiler-owned typed pins/control-data edges, source/debug identities, and
  typed edit capabilities;
- transactional typed data-edge dragging in the UE GraphEditor, with exact
  edge/type preconditions, bounded pure-expression C# write-back, full
  reanalysis, Surface refresh, and old-wire retention on failure;
- Graph Authoring v2 Alpha in the UE GraphEditor: a Blueprint-style right-click
  palette, structured local/binary-expression/branch/while/do/for/switch/
  break/continue/return insertion, transaction-first
  Delete and Alt-click disconnection, and automatic source/Surface/runtime
  refresh after each accepted edit;
- a docked native node Palette with token search, contract keywords,
  active-function/block/type filtering, persistent per-user Favorites, an
  accepted-action Recent list, `Ctrl+Space` focus, and Enter-to-place through
  the same Workbench transaction path;
- switch-section authoring from the Slate inspector, including typed
  constant/declaration/type/var/discard/relational/logical/not/parenthesized
  pattern text, bounded guards, ordered case/default insertion, relabeling,
  one-slot movement, and safe break-only section deletion;
- Workbench workspace v0.4 transaction history with baseline-revision and
  operation-ID conflict guards, exact identity-stable Undo/Redo snapshots,
  named multi-edit drafts, one-shot grouped publication, and shared
  browser/Slate controls;
- automatic shared-value extraction through an atomic
  `introduceTemporary` + `reconnectValue` patch plan, preserving one evaluation
  instead of copying a computed subtree into a second consumer, with a stable
  collision-resistant name derived from the complete producer semantic ID;
- reflection-driven UE API palette entries for readable properties and callable
  functions whose typed receiver and arguments are available in the active
  Visual function, emitted as structured member-access/invocation expressions
  rather than raw C# text;
- UE-reflected managed handle types as an explicit graph-safe object boundary,
  while ordinary arbitrary CLR object types remain outside the Visual profile;
- UE-side `.usgraph` load/save with atomic sidecar writes and per-function node
  position recovery across Surface regeneration and editor restarts;
- persistent state-property identity across formatting and safe renames through
  the same `.usids` reconciliation and tombstone contract;
- semantic-independent `.usgraph` layout sidecars with deterministic positions,
  sizes, collapsed state, comment regions, routing hints, and orphan recovery;
- explicit patch identity pins and tombstones for deleted semantic nodes;
- versioned HIR, semantics, and graph JSON emission through a small CLI;
- direct SDK-style `.csproj` loading through MSBuild, including evaluated
  Compile items, conditional symbols, NuGet assets, project references, and
  copy-local runtime dependencies;
- deterministic PE/portable-PDB emission plus a collectible CoreCLR execution
  host, semantic-ID invocation, strict typed return/fault JSON, and a public
  `execute` command;
- deterministic `unifiedscript.source-map/v0.1` artifacts connecting semantic
  method/node IDs to real MethodDef tokens, IL offsets, and portable-PDB spans;
- content-addressed program-image caching, collectible reload generations, and
  semantic breakpoint bind/pending/disabled plus stale-hit protocols;
- automated execution equivalence between independently text-authored code and
  C# produced by a checked-in visual semantic patch plan;
- a strict `unifiedscript.unreal-reflection/v0.4` contract with inheritance,
  interfaces, Blueprint metadata, enums, default parameters, soft references,
  callable/property/delegate shapes, latent/thread/World execution policy,
  deterministic managed-wrapper/event proxy generation, and the `ue-bindings`
  CLI command;
- an UE 5.7/5.8-compatible host project and `UEUnifiedScript` plugin with a reflected script
  component plus headless reflection-export commandlet;
- an opaque 64-bit slot/generation handle shared by managed wrappers and a
  game-thread-only, world-bound native weak UObject registry with deletion
  invalidation and stale-generation rejection;
- a placeable, Blueprintable `AUnifiedScriptActor` and managed
  `UnrealObjectScript`/`UnrealActorScript` object model with distinct
  generation-safe host and gameplay-target handles;
- versioned Unreal deployment manifests that preflight and synchronize real
  `UUnifiedScriptAsset` packages, with required target-class validation before
  CoreCLR startup and no per-host assembly/type literals;
- strict project-owned Unreal API contracts that atomically generate one
  allowlisted C# surface, Native Thunk table, coverage report, and typed visual
  node catalog, with Script Assets selecting that catalog for Slate;
- generated static-service calls, World-scoped synchronous resource retention,
  Actor spawning, and field-addressable reflected UStruct values whose same
  contract emits C# records, native codecs, and visual Make/Break nodes;
- World-safe Actor overlap events, arbitrary reflected multicast Event Entries,
  and game-thread latent `DelayAsync`, all canceled or unbound before reload,
  EndPlay, or World teardown;
- machine-local UE discovery, host build, reflection export, and managed
  binding generation through one repository script;
- xUnit coverage for validation, lowering, identity, graph projection, and
  deterministic output.

M3 bidirectional source projection is complete. M4.1 adds explicit Graph
regions, M4.2 derives and validates control flow, M4.3 consumes those facts for
bounded local assignment and safer value reconnection, and M4.4 recursively
authors nested acyclic decisions through the same HIR/Graph path. M4.5 now
projects and validates structured loops through that same path. M4.6 completes
the first bounded Graph-to-C# loop round trip by constructing and populating
those loops atomically. M4.7 preserves those structures while editing existing
assignment values and existing or plan-produced while conditions. M4.8 extends
that exact-endpoint model to local initializers, `if` conditions, and return
values, whether the enclosing statement already exists or is produced earlier
in the same atomic plan. M4.9 connects the data side of those stages by letting
later expressions consume earlier generated locals through typed plan handles.
M4.10 edits one exact nested input without rebuilding its enclosing expression,
while retaining type, effect, trivia, scope, dominance, Graph-edge, and
unaffected-spine identity proofs. M4.11 publishes the actual roots of
plan-authored expression trees and resolves later `targetPath`/`inputPort`
edits through those step handles before reusing the same M4.10 patch path.
M4.12 preserves conditional expressions directly instead of desugaring them,
projects an explicit lazy `Select` node, rejects effectful arms, and extends the
same standalone and produced-expression editing paths through `condition`,
`whenTrue`, and `whenFalse` ports.
M4.13 closes the equivalent logical-operator gap: Graph declares exactly when
an `&&`/`||` right input is evaluated, preserves the left effect chain, and
rejects right subtrees whose effects would require conditional control flow.
M4.14 applies the same explicit lazy-right contract to `??`, preserves
Roslyn-bound nullable/result types, and adds exact scalar visual construction
and produced-handle editing through the existing binary ports.
M4.15 preserves `?.` as conditional access plus conditional member binding,
connects the implicit receiver explicitly in Graph, rejects conditionally
effectful or hidden-conversion shapes, and lets visual plans author a simple
member access and edit its receiver through the same exact endpoint machinery.
M4.16 preserves text-authored `for` as a distinct ordered loop structure with
initializer/body/increment regions and continue-to-increment CFG routing.
M4.17 adds the dedicated bounded reverse path: visual plans can construct a
typed `for`, populate its body, and consume its generated declaration local
without predicting any persistent IDs. M4.18 adds exact mutation of the four
bounded existing-header endpoints while retaining loop/body/declarator/
assignment/mutation identity and CFG continue routing.
M4.19 adds `do ... while` as an independent post-test loop and completes both
directions without desugaring: text preserves at-least-once execution, while a
visual plan can construct/populate the body and edit the generated condition
through a stable producer handle.
M4.20 adds the first exact `foreach` slice without pretending that every
enumerable is an array. Strings and one-dimensional zero-based arrays share an
indexed VM contract: the collection is captured once, `continue` advances to
move-next, the current value dominates each body entry, and no disposal path is
required. M4.21 adds the synchronous enumerable strategy without desugaring it
to indexed access: semantics binds `GetEnumerator`, `MoveNext`, `Current`, and
`Dispose`, Graph owns a `loopCleanup` region, normal/break/return/exception
transfers preserve `finally` cleanup, and visual plans use the same loop and
produced-local handles.
M5.1 closes the first executable loop: validated HIR/semantics/Graph emit a
deterministic in-memory DLL and portable PDB, a collectible CoreCLR session
invokes a static synchronous Visual method by semantic ID, and only canonical
typed values or structured faults cross the session boundary. The checked-in
enumerable plan and an independently text-authored equivalent method execute to
the same result through that single backend.
M5.2 closes the executable identity loop: every synchronous Visual method maps
its stable method ID to a real MethodDef token, and executable HIR IDs carried
into Graph map to validated IL offsets and portable-PDB spans. The public
`emit` command writes the DLL, PDB, and canonical source map together.
M5.3 makes that identity reload-safe: identical requests reuse a bounded LRU
program-image cache, every load receives a distinct monotonic generation, and
semantic-node breakpoints automatically rebind, become pending on deletion,
and reject hits from stale unloaded generations.
M5.4 closes the first live debugging loop without adding a second VM: a
post-validation debug image installs statement probes into the same CoreCLR
method body, the adapter truly suspends its execution thread, and the loopback
transport supports pause/continue, semantic step-over, live stack frames, and
bounded cycle-safe locals under strict generation and pause identities.
M5.5.1 closes the first declaration-authoring gap before UE binding. C# types
marked `[ScriptAsset]` lower auto-properties, constant defaults, exposure
policy, and Visual signatures into strict `unifiedscript.script-asset/v0.1`
JSON. State identities are deterministic in batch analysis and join `.usids`
structural reconciliation when persistent editing is enabled, including safe
property renames. The prototype under `prototype/visual-editor` now loads a
real artifact generated from `samples/Authoring/DamagePolicy.cs`; its state
list, inspector, canonical declaration, AI declaration outline, and IDs are no
longer embedded mock data. M5.5.2 adds the strict typed declaration-patch
protocol and transactional Roslyn write-back. Add/update/remove operations use
asset/state Semantic IDs, complete old-value preconditions, full recompilation,
and identity pins for sidecar-backed renames. The CLI commits patched source,
revalidated Declaration IR, and an optional updated `.usids` together. The
prototype generates the same directly applicable JSON instead of a static
preview. M5.5.3 connects the behavior surface to the generated
`unifiedscript.graph/v0.13` artifact and exact source snapshot. The adapter
reduces 17 fine-grained Graph nodes to six semantic action/region cards while
recursively retaining ordinary expressions inline. Cards, source selection,
AI outline, and source selection now share real Graph IDs; state-reference
nodes additionally carry their Declaration IR IDs, so the UI never joins state
by display name. M5.5.4 closes the live authoring/debug loop. An engine-neutral
`ScriptAssetReceiver` materializes instance state on the same collectible
CoreCLR image, assignment Graph nodes expose their exact probeable HIR
`debugTargetId`, and `debug-serve` hosts the workbench plus loopback debug API
on one local origin. Run, Continue, Step Over, stack location, locals, and the
returned value now come from the suspended CoreCLR thread rather than timers.
M5.5.5 closes the remaining write side. `debug-serve` now publishes one live
authoring workspace and accepts the existing declaration/body patch protocols.
It prepares a complete candidate analysis and Graph, reloads the same CoreCLR
transport, transactionally commits source plus identity sidecar, and only then
publishes the next revision/generation. The Workbench can save declarations,
apply an AI declaration patch, or edit a promoted behavior input without source-
line operations. External file changes are rejected instead of overwritten,
and persistent IDs restore selection and breakpoints across the regenerated
artifacts. The same transaction now includes a versioned Undo/Redo journal, so
revision, exact snapshots, and operation replay guards survive Workbench and UE
editor restarts while stale source/identity/configuration state is rejected.
The finished Workbench uses dark contract/runtime surfaces around a
light semantic drafting canvas: Behavior keeps the compact flow primary, Dual
temporarily yields inspector space to flow/source comparison, Source focuses
the canonical C#, and AI Lens exposes the coordinate-free machine outline.
M5.5 is therefore complete before Unreal integration begins.

M6.0 removes the last sample-specific browser adapter before that integration.
The new `UnifiedScript.Authoring` project projects every Script Asset and Visual
function into strict `unifiedscript.authoring-surface/v0.4` JSON. The Workbench
now renders asset/function navigation, `if` and loop regions, compact actions,
state references, semantic edit controls, source selection, AI outlines, and
CoreCLR breakpoints from that Surface. `analyze --out` emits the Surface beside
Declaration IR and Graph, while `debug-serve` returns it in authoring workspace
v0.2 after every committed reload. The UI adapter contains no sample asset,
function, state, or API-call names; `MultiAssetControlFlow.cs` and automated
tests cover two assets, three functions, branches, while loops, and for loops.
UE binding began in M6.1 against this proven contract. M6.2 established the
runtime, authoring, data-plane, performance, and packaged-demo baseline; M6.3
now turns that baseline into a project-installable alpha.

M6.1 Phase A establishes the first real UE toolchain slice. The
`UnifiedScript.Unreal` boundary validates and normalizes engine reflection
without exposing `UClass` or `FProperty` to Core, Semantics, Graph, or
Authoring. The UE plugin exports selected native modules through a headless
commandlet; the CLI consumes that manifest and generates typed managed wrappers
over `IUnrealInvocationBridge`. `UUnifiedScriptComponent` is the initial host
component and reflection fixture. UHT validation passes against the discovered
UE 5.7.4 source tree. Phase B.1 now defines the lifetime foundation: managed
handles use the same 64-bit slot/generation encoding as a native weak registry,
which rejects cross-world and off-game-thread access, allocates active
generations process-wide to prevent cross-World aliasing, and listens for UObject
deletion. Both plugin modules compile against
UE 5.8.1 with version-following TargetRules. An installed UE 5.8.1 Release build
now passes the native lifecycle Automation Test and the live reflection export
smoke test; the latter exports one fixture type with three properties and two
functions and feeds the managed binding generator successfully. The earlier
5.7.4 environment still lacks a full native build and live export smoke test.
Phase B.2 adds a native primitive invocation core: the same generated member
paths now read and write fixture `bool`/`FString` properties and call `int32` or
void UFunctions through `ProcessEvent`, only after weak-handle, world, thread,
access, argument, and type validation. Phase B.3 now supplies the managed
`NativeUnrealInvocationBridge`,
which converts generated-wrapper CLR values into the same owned primitive value
and stable error categories. The checked-in generated wrapper exercises this
adapter for every fixture property and function. Phase B.4 adds a versioned
in-process native function table and a managed transport that copies synchronous
UTF-8 results before control returns. Phase B.5 adds one
`UUnifiedScriptWorldSubsystem` per supported World. It owns the registry and
bridge, exposes a retained native context, detaches it before teardown, and makes
post-teardown calls fail safely. Phase B.6 now performs a real
process-wide hostfxr bootstrap from the engine-bundled .NET runtime and acquires
the CoreCLR load-assembly delegate. The per-World managed entry point and
collectible load context are now connected in Phase B.7: each native World host
creates a managed entry that retains its transport until native context
detachment. Phase B.8 proved hosted generated-wrapper execution. Phase B.9 now
makes `UUnifiedScriptComponent` a real runtime host: `BeginPlay` automatically
registers and starts its configured managed type, component Tick dispatches
`OnTick`, `EndPlay` calls `OnStop` before handle release, and Reload swaps to a
fresh collectible script generation. Phase B.10 now gives every generated
member a stable 64-bit ID and extends the in-process transport with ABI v2.
Generated `UUnifiedScriptComponent` calls use direct native field/function
thunks; ABI v1 and string/reflection dispatch remain as compatibility fallback.
Phase B.11 adds `UUnifiedScriptAsset`, Content Browser selection, soft-path
loading, schema/config validation, and transactional Reload into a different
managed type. Phase B.12 removes the fixture-specific native switch: reflection
now exports header/native-access metadata, and `ue-bindings --native-out`
deterministically emits the ABI v2 C++ dispatcher for every supported public
member. The generated path performs direct typed access/calls without
`FProperty`, parameter buffers, or `ProcessEvent`.
Phase B.13 closes the first declaration-to-engine loop: a Script Asset can point
at emitted `.asset.json`, and the component materializes `[Expose]` state into a
fixed-layout `FInstancedPropertyBag` in Details. M8.3 completes that path with a
serialized set of explicit override IDs: untouched instances adopt new C#
defaults, compatible overrides survive field renames and Reload, and removed or
incompatible overrides fall back safely. Invalid JSON or managed reload failure
leaves the previous state, override set, and generation intact. See
[Script State override migration](docs/STATE_OVERRIDE_MIGRATION.md).
Phase B.14 adds the first native Slate authoring workspace. The Tools menu opens
an Authoring Surface browser that can load the checked-in demo, a file, or
the `AuthoringSurfacePath` of the selected Script Asset. It preserves semantic
IDs while presenting state, functions, structured regions, actions, source
spans, and source previews. Typed literal/operator capabilities export the same
`semantic-patch/v0.20` contract used by the compiler pipeline; direct patch
submission and live source transactions remain a later integration step.
Phase B.15 closes that edit loop. `Apply & Reload` posts the typed patch to the
loopback Workbench, which validates one multi-source candidate and atomically
commits C#, `.usids`, Declaration IR, Authoring Surface, managed DLL, and PDB.
Slate consumes the returned Surface and reloads matching running components;
stale IDs, external source changes, compilation failures, and artifact-write
failures leave the published artifact set unchanged.
Phase B.16 replaces the hierarchy-only center pane with Unreal's native
`SGraphEditor`. The workspace now follows the Blueprint layout: `My Script` on
the left, a real zoom/pan/marquee graph canvas in the center, and semantic
Details/source/edit controls on the right. Surface v0.4 now materializes every
Graph node, real typed pins, and compiler-owned control/data wires. Manual
occupied-data-input rewiring now uses that same transactional source-patch
contract; control flow and structural construction remain compiler-owned.
The first M8.2 experience tranche adds a searchable Palette below `My Script`.
It ranks Favorites and accepted Recent actions, filters statement nodes out
when no editable block is active, retains function-level Event Entries, and
derives API/event availability from the active function plus the generated
reflection catalog. Preferences live in per-project editor user config; they
never enter C#, `.usids`, `.usgraph`, or the Authoring Surface. See
[`docs/NODE_PALETTE.md`](docs/NODE_PALETTE.md).

The next M8.2 tranche adds source-backed Pin Authoring in native Details:
type-checked C# literals, bounded typed Reset, and safe Promote to Local all
flow through exact edge preconditions and the transactional Workbench. See
[`docs/PIN_AUTHORING.md`](docs/PIN_AUTHORING.md).

The Canvas Authoring tranche then adds native Comment Boxes, typed wire
Reroutes, per-function viewport restoration, a clickable/draggable minimap,
Focus Selection/All, and Blueprint-style `C`, `F`, and `Home` gestures. Shared
canvas structure is persisted in `.usgraph`; personal navigation state remains
in per-project editor user settings. See
[`docs/CANVAS_AUTHORING.md`](docs/CANVAS_AUTHORING.md).

The next experience tranche makes node cards compact by default and moves raw
types, effects, source, semantic identity, ports, edit capabilities, and
generated API metadata behind exact hover contracts and a collapsed **Advanced
Node Details** section. See [`docs/NODE_DETAILS.md`](docs/NODE_DETAILS.md).

The final M8.2 tranche adds server-owned transaction preflight. Slate displays
the exact C# before/after candidate prepared by the real compiler path, and an
accepted Apply carries a SHA-256 token binding that candidate to the commit.
Rejected transactions expose locatable compiler/contract diagnostics, bounded
Open Source/Refresh actions, and an explicit `unchanged`, `restored`, or
`failed` rollback receipt instead of inferring safety from a red status line.
See [`docs/AUTHORING_DIAGNOSTICS.md`](docs/AUTHORING_DIAGNOSTICS.md).

The current runtime performance tranche activates ABI v3 for fixed scalars,
enums, generation-safe object/class handles, vector/rotator, transform, UTF-8
  text, reflected structs, recursive arrays/sets/maps, and return plus `out`/`ref`. Generated wrappers
use a synchronous caller-owned frame with zero steady-state allocation for the
fixed call path. Variable-size overflow is staged in the retained World context;
the managed retry expands the arena and copies the staged result without
executing a UFunction twice. The World host discovers v3 through an append-only
v2 bootstrap suffix and retains v1/v2 only for old hosts and unsupported
reflected shapes. The current UE 5.8 lifecycle Demo reports `FastCalls=20`, all
through `CallFrameCalls=20` with `LegacyCalls=0`, and receives the reflected
`OnManagedSignal(73, Context)` delegate through ABI v4 exactly once after reload.

The first shared-data iteration adds a separate World-level data plane beside
that Binding control plane. Native code publishes a double-buffered 96-byte
Actor Transform row keyed by `WorldHostId + ObjectHandle`; managed scripts read
the current buffer through `Context.DataPlane.AcquireTransformSnapshot()` as a
borrowed, allocation-free `ReadOnlySpan`. No UObject address or Unreal container
layout is exposed, and World teardown detaches the view before freeing its
buffers. See `docs/UNREAL_SHARED_DATA_ABI_V4.md` for the frozen first schema.

Gameplay Data ABI v4 adds a Game-Thread `World Phase Scheduler` around
Actor ticking. Managed scripts reserve caller-written fixed command rows during
`ManagedTick`; native executes committed `SetTransform`, velocity, damage,
spawn, destroy, and emitter commands in `NativePostActorTick`, then publishes
their completion events at the next `NativePreActorTick`. Line, sphere, and box
queries execute through one synchronous Batch Trace boundary and return
borrowed `FHitResult`-compatible rows; sphere/box overlap queries use an
independent fixed-row batch. The rows, views, capacities, offsets, defaults, and
compatibility fingerprint are emitted from one revisioned JSON schema for both
C++ and C#. A matched 1,024-operation live CoreCLR profile keeps movement,
overlap, and high-volume queries on the data plane, attributes on generated
bindings, and spawn hybrid; the measured data-plane loops allocate zero managed
bytes. BlockBreaker now uses this path for its shot trace, impact effect, and
damage, and passes the exact-frame headless gate.
See `docs/UNREAL_GAMEPLAY_DATA_ABI_V4.md` for phase, ownership, and schema rules.

The BlockBreaker slice can also be emitted as a directly runnable Win64 demo.
`comparison/package-blockbreaker-demo.ps1` regenerates its API contract and
Script Assets, cooks and archives the project, verifies the packaged CoreCLR and
managed gameplay assembly, runs a 300-frame gameplay smoke, and writes a
double-click launcher plus delivery report. See
`docs/UNREAL_BLOCKBREAKER_DEMO.md`.

`ue-bindings --report` now emits deterministic per-member coverage as native
thunk, reflected-path fallback, rejected, or deferred type. `--fail-on-rejected`
makes that report a CI gate. If a generated member ID is absent at runtime, the
managed bridge retries the exact reflected member path only for the legacy
scalar/object/core-struct contract; `out`/`ref` and container shapes are
reported as rejected when no direct thunk exists instead of claiming an unsafe
or lossy fallback.

Graph Presentation v1 keeps `ResolveInitialValue`'s complete 14-node/14-edge
Surface intact while projecting a compact 7-node/7-wire authoring canvas. Connected identifier
and literal leaves become readable pin values such as `A = InitialValue` and
`Value = 100`; assignment titles become `Set result`, common execution labels
are suppressed, and kind/category/source/semantic-ID diagnostics live in
tooltips or Details instead of permanently occupying every card.

Run the verified UE 5.8 demo on Windows:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-unreal-demo.ps1 -EngineRoot D:\UE_5.8
```

Open the interactive Blueprint-style authoring demo in one command:

```powershell
powershell -ExecutionPolicy Bypass -File tools/launch-unreal-authoring-demo.ps1 `
  -EngineRoot D:\UE_5.8
```

It builds and stages the demo, starts the transactional host in the background,
opens the editor directly on the UnifiedScript Authoring tab, and stops the
host when the editor closes. See `docs/UNREAL_DEMO.md` for the short walkthrough.

Run the live CoreCLR-to-UObject performance gate (the Puerts verdict remains
empty unless a same-machine result is supplied):

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-unreal-benchmark.ps1 `
  -EngineRoot D:\UE_5.8
```

See `docs/UNREAL_PERFORMANCE.md` for the fixed scenario and competitive gate.
M10's representative nine-workload contract, strict matched-environment rules,
and alternating trial schedule are documented in
[`docs/PERFORMANCE_WORKLOAD_SUITE.md`](docs/PERFORMANCE_WORKLOAD_SUITE.md).
The current UE 5.8.1 run completed all 54 scheduled captures. UnifiedScript's
complete BlockBreaker p50/p95/p99 frame ratios are 0.898835/0.955283/0.959456
and its bridge-time ratio is 0.336019, while direct scalar, container, delegate,
and resource-flow workloads still lose. M10.2 therefore targets generated
shared snapshots and buffered events before M10.3 evaluates any broad claim.
That slice is now live through append-only Shared/Gameplay transport suffixes:
native modules publish schema-ID fixed rows into bounded per-World double
buffers, and managed code consumes stack-only `ReadOnlySpan<T>` views with
World/publication/layout validation. The live-CoreCLR v0.3 profile measured
1,024 direct Array+Map binding rows at 25,371.973 ns/row versus 1.270 ns/row for
the generated snapshot (ratio 0.000050), plus 0.586 ns/row for buffered events,
with zero managed allocation. Direct generated calls remain the fallback for
low-volume or unstable shapes. M10.3 now freezes the original v1.0.0 evidence,
adds suite v1.1.0 plus reviewed budgets, and routes only the representative
high-volume fixed-container/event workloads through those channels. Their
three-trial matched medians improved from losing ratios of 6.734380/3.800531 to
0.002026/0.007390, with one acquisition, zero logical boundary copies, and zero
managed allocation on every UnifiedScript trial. The complete 54-slot v1.1.0
rerun is still required before changing the broad product claim.

Build, archive, and smoke-test the self-contained Win64 Development and
Shipping packages:

```powershell
powershell -ExecutionPolicy Bypass -File tools/package-unreal-release.ps1 `
  -EngineRoot D:\UE_5.8
```

The gate verifies packaged hostfxr/CoreCLR and managed dependency closure, then
runs managed Start/Tick/Stop from each packaged executable. See
`docs/UNREAL_RELEASE.md` for the artifact and platform contract.

Run the complete declared UE 5.8.1 matrix, including Editor Automation,
Development/Shipping packages, deterministic SDK packaging, installation into
a plugin-free project export, and an installed-project package smoke:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-unreal-compatibility-matrix.ps1 `
  -EngineRoot D:\UE_5.8
```

The supported and explicitly unsupported rows are documented in
`docs/UNREAL_COMPATIBILITY_MATRIX.md`.

Start the transactional authoring host in a second terminal while the Editor is
open:

```powershell
powershell -ExecutionPolicy Bypass -File tools/run-unreal-authoring.ps1 -EngineRoot D:\UE_5.8
```

Open `Tools > UnifiedScript Authoring`, load the Demo or selected Script Asset,
change a promoted literal/operator, and choose `Apply & Reload`. For a graph
round trip, select `RewirePreview` and drag the `*` output onto
`Declare incremented` -> `Value`; the committed C# changes from `value + 1` to
`value * 2`, then Slate reloads the regenerated Surface.

For an Editor actor, create a Data Asset using `UnifiedScriptAsset`, set:

```text
ScriptAssemblyPath = {PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll
ScriptTypeName     = UnifiedScript.Unreal.Demo.DemoScript
DeclarationPath   = {PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.asset.json
DeclarationAssetPath = Unreal/Demo
AuthoringSurfacePath = {PluginDir}/Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.surface.json
```

Assign that asset to `UnifiedScriptComponent.Script Asset`. With automatic Start
and Tick enabled, PIE drives managed `OnStart`, `OnTick`, reload generations,
and `OnStop`. `Refresh Script State` makes C# defaults editable in the component
Details panel; the verified demo overrides `InitialValue` to `37`, reads it from
managed code through generated native thunks, retains it on Reload, and can
reset either one field or the whole instance to current C# defaults. The
component's direct assembly/type fields remain an advanced compatibility fallback.

The v0.2 profile accepts `[Visual]` methods containing local variables,
expressions, calls, assignments, `if/else`, `while`, `do`, `for`, bounded
indexed and synchronous disposal-aware enumerable `foreach`, `break`, `continue`,
`return`, conditional, null-coalescing, and null-conditional expressions,
casts, and `await`.
Unsupported syntax is rejected explicitly instead of being approximated.

## Architecture

```text
C# source
  -> Roslyn full-fidelity syntax tree
  -> Visual Profile validation
  -> Script Asset / Declaration IR (type-level contract)
  -> Unified HIR (Visual method bodies)
  -> Roslyn SemanticModel
  -> language-neutral bound semantics
  -> deterministic, effect-aware graph projection

Graph edit
  -> semantic patch bound to source + semantic configuration IDs
  -> typed/preconditioned Roslyn replacement
  -> full profile and semantic revalidation
  -> updated C# source (only if the whole patch succeeds)

Declaration edit
  -> typed patch bound to source + semantic configuration IDs
  -> asset/state Semantic ID and complete old-contract preconditions
  -> transactional Roslyn declaration/reference rewrite
  -> full compilation + Declaration IR postcondition validation
  -> updated C# + asset JSON + optional .usids (only as one commit)

Workbench authoring commit
  -> declaration-patch v0.2 or semantic-patch v0.28
  -> workspace v0.4 baseline revision + unique operation/group identity
  -> Begin Group -> in-memory draft edits -> atomic Commit or zero-write Cancel
  -> immutable candidate analysis + regenerated Graph
  -> same CoreCLR transport reload
  -> rollback-capable source + .usids transaction
  -> bounded Undo/Redo history + next revision/generation response or explicit conflict

Current execution path:
  -> Graph/CFG execution gate
  -> deterministic Roslyn PE + portable PDB
  -> semantic method/node IDs mapped to MethodDef tokens + IL sequence points
  -> collectible CoreCLR session
  -> content-addressed cache + reload generation + semantic breakpoint binding
  -> cooperative CoreCLR debug adapter + loopback transport
  -> Script Asset receiver + local visual debug HTTP bridge
  -> semantic-ID invocation and typed result/fault

Current Unreal adapter path:
  -> selected native UClass/UProperty/UFunction reflection
  -> strict Unreal reflection manifest v0.1
  -> deterministic C# managed wrappers
  -> handle-based invocation bridge contract
  -> world-scoped native host + retained invocation context
  -> component-driven Start/Tick/Stop lifecycle
  -> collectible per-script reload generation
  -> UE Script Asset resolution + transactional entry-type reload
  -> Declaration IR -> fixed-layout Details state + stable-ID migration
  -> stable generated Member ID -> ABI v3 call frame -> module-owned native thunk -> live UObject
  -> Authoring Surface v0.4 -> native Slate GraphEditor pins/wires + inspector
  -> typed edit capability -> semantic-patch v0.28 Workbench transaction
  -> typed callable extraction -> inferred inputs + zero/one live result
  -> identity-safe same-method range copy/paste -> fresh copied subtree IDs
  -> reversible lexical-scope collapse/expand -> stable member IDs + fresh/retired region ID
  -> atomic C#/identity/Declaration/Surface/DLL/PDB publication
  -> matching component transactional reload
  -> World Phase Scheduler -> caller-written Command Buffer -> next-frame Event Buffer
  -> synchronous line/sphere/box Batch Trace through one managed/native boundary
  -> instrumented live-World publication -> non-blocking semantic pause/step/stack/locals/watch
  -> generation-guarded worker execution -> GameThread bridge return and teardown drain
  -> bounded USDB process framing -> concurrent remote wait/Pause/Continue
  -> debug observation + profile delta -> source/graph node and runtime-boundary diagnosis

Later:
  -> authenticated remote-machine/DAP transport, async stepping, and richer expression evaluation
  -> Script Asset cook/packaging dependency discovery
  -> optional graph-canvas layout and cook/package integration
```

The HIR is the semantic contract. IL and future visual layouts are derived
artifacts; neither is used as a decompilation source.

CLI inputs inside the current working directory use normalized relative source
paths, so artifacts remain stable when the repository is checked out elsewhere.

### Execute a Visual method

M5.1 executes the same validated method body regardless of whether its C# was
authored as text or produced through visual semantic patches:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  execute samples/Execution/RuntimeRules.cs \
  --method Samples.Execution.RuntimeRules.LastNonNegative \
  --args '[[1,-2,9],4]'
```

The command returns execution-result v0.1 with `System.Int32` JSON value `9`.
An exact `--method-id us_...` may replace `--method` for overloaded entry
points.
Target exceptions are emitted as structured faults and use exit status `3`;
profile or compilation failures emit no program image. The host uses a
collectible reload context, not a security sandbox. See
[EXECUTION_RUNTIME.md](docs/EXECUTION_RUNTIME.md).

### Emit executable and source-map artifacts

M5.2 exposes the same program image without executing it:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  emit samples/Execution/RuntimeRules.cs \
  --out artifacts/runtime-image
```

The command emits `RuntimeRules.dll`, `RuntimeRules.pdb`,
`RuntimeRules.sourcemap.json`, and `RuntimeRules.asset.json`. The source map resolves stable semantic method
IDs to CLR MethodDef tokens and executable node IDs to portable-PDB-backed IL
offsets; the declaration artifact travels with the same executable program
image and compilation identities. When identity inputs are present, their reconciled `.usids` files are
written in the same transaction. `emit-files` handles a multi-source
compilation. See [SOURCE_MAP.md](docs/SOURCE_MAP.md) and
[BREAKPOINT_RELOAD.md](docs/BREAKPOINT_RELOAD.md).

### Build and verify the Unreal adapter

The host project is deliberately checked in without machine-specific engine
paths. Its TargetRules follow the selected engine's latest build settings and
include order, which maps to V6/5.7 on UE 5.7 and V7/5.8 on UE 5.8. On macOS,
the helper discovers common source and Launcher layouts; set
`UNIFIEDSCRIPT_UE_ROOT` when the engine lives elsewhere:

```bash
tools/ue-adapter.sh detect
tools/ue-adapter.sh build
tools/ue-adapter.sh export
tools/ue-adapter.sh bindings
```

`tools/ue-adapter.sh verify` runs the full sequence. Reflection and generated
bindings are written under ignored `artifacts/ue/`; the canonical contract
fixture and its deterministic output live under `samples/Unreal/`. Generate a
wrapper directly with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  ue-bindings samples/Unreal/UEUnifiedScriptRuntime.reflection.json \
  --contract samples/Unreal/UEUnifiedScriptRuntime.api-contract.json \
  --out artifacts/UEUnifiedScriptRuntime.bindings.cs \
  --native-out artifacts/UEUnifiedScriptRuntime.thunks.cpp \
  --nodes-out artifacts/UEUnifiedScriptRuntime.api-nodes.json \
  --report artifacts/ue/binding-coverage.json \
  --fail-on-rejected
```

Game modules can own their generated ABI v3 table by also setting
`--native-header` and `--native-namespace`, then registering the resulting
function table with `FUnifiedScriptGeneratedThunkRegistry` during module
startup. The BlockBreaker comparison project is the executable example.

See [UNREAL_API_CONTRACT.md](docs/UNREAL_API_CONTRACT.md) and
[UNREAL_ADAPTER.md](docs/UNREAL_ADAPTER.md) for the contract, module
boundaries, exFAT considerations, and remaining runtime phases.

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

Analyze or emit a complete SDK project with the same command shape:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/ProjectLoading/ProjectLoadingDemo.csproj \
  --out artifacts/project-loading
```

Project restore/build run by default. See [PROJECT_LOADING.md](docs/PROJECT_LOADING.md)
for configuration, target-framework, no-restore/no-build, and source-generator
boundaries.

This emits:

```text
artifacts/sample/DoorController.hir.json
artifacts/sample/DoorController.semantics.json
artifacts/sample/DoorController.graph.json
artifacts/sample/DoorController.asset.json
```

Without `--out`, the graph JSON is written to stdout.

To inspect the first authoring asset directly:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Authoring/DamagePolicy.cs \
  --out artifacts/authoring
```

See [DECLARATION_IR.md](docs/DECLARATION_IR.md) and
[VISUAL_AUTHORING_MODEL.md](docs/VISUAL_AUTHORING_MODEL.md).

Apply the checked-in state-exposure edit and emit a revalidated source/artifact
pair:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-declaration-patch samples/Authoring/DamagePolicy.cs \
  --declaration-patch samples/Authoring/MakeMaxDamageEditable.declaration.patch.json \
  --out artifacts/authoring/DamagePolicy.cs \
  --out-asset artifacts/authoring/DamagePolicy.asset.json
```

Add `--identity <input.usids> --out-identity <output.usids>` when persistent
identity is enabled; this pair is required for declaration renames.

### Analyze structured regions

Graph v0.12 assigns every node to exactly one semantic region and preserves
function, block, `if`, `then`, explicit/implicit `else`, `loop`, and `loopBody`
nesting, plus explicit `loopInitializer` and `loopIncrement` phases for `for`.
Generate the early-return example with:

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

HIR v0.7 emits explicit while/break/continue statements. Graph v0.12 emits a
`loop` region with one `loopBody` child, and derived CFG facts identify the
effective condition header, body entry, exit successor, back-edge arcs, and
break/continue nodes owned by the nearest loop. Effectful conditions route all
continues and normal fallthrough back through their first effect so the
condition is reevaluated each iteration. See
[Structured Loop Foundation](docs/LOOP_FOUNDATION.md).

### Analyze lossless for loops

M4.16 admits text-authored `for` without rewriting it into `while`. HIR v0.7
keeps a declaration initializer or ordered expression initializers, the optional
condition, ordered incrementors, and body as separate fields. Unary HIR also
records prefix versus postfix position, so `index++` cannot silently become
`++index` during a source/visual round trip.

Graph v0.12 gives every `for` loop three ordered child regions:
`loopInitializer`, `loopBody`, and `loopIncrement`. The loop node records
`evaluationOrder: initializer,condition,body,incrementors`; normal body
fallthrough and `continue` enter the increment region, while completed
incrementors return to the effective condition header. Initializer execution is
outside the cyclic component and therefore occurs exactly once.

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Loops/ForRules.cs \
  --out artifacts/for-loops
```

The sample covers a declaration initializer with postfix increment and an
effectful initializer/incrementor loop with `break` and `continue`. M4.17 also
provides a dedicated bounded visual constructor; it does not overload the
lossier `while` payload. See
[Lossless and Graph-Authored For Loops](docs/FOR_LOOPS.md).

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

`unifiedscript.patch/v0.28` binds every patch to both the source compilation ID
and semantic configuration ID emitted in current Graph schema v0.13. A `replaceLiteral`
operation also carries the expected old literal text. A stale source, changed
reference/effect configuration, missing target, changed old value, non-literal
replacement, or semantically invalid result rejects the entire patch and writes
no source.

The Roslyn replacement receives a temporary identity pin, so the edited node's
old ID is explicitly carried into the reconciled sidecar even when its
fingerprint and span length change. Deleted live entries move to tombstones;
ambiguous matches allocate new IDs and emit `UVS3101` rather than guessing.

## Apply structural graph patches

Patch v0.21 includes `changeOperator` and `reconnectArgument`. The checked-in
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

Patch v0.21 also includes bounded `insertStatement` and `deleteStatement`
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
Patch v0.21 accepts a local declaration with one literal initializer, a
CFG-checked local assignment, a typed terminal return, an `if`/`while`, or a
dedicated bounded `for` whose clauses are versioned graph-authored expressions.
A local's
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

The common standalone expression payload has seven closed variants: scalar `literal`,
`localReference`, `unary`, `binary`, `cast`, `conditional`, and bounded
`conditionalMemberAccess`. Every node carries an expected
semantic type ID. References also carry a source definition ID and expected
name; a local must both lexically and CFG-dominate the reachable insertion gap,
while a parameter must belong to the same method. The current operator subset
excludes division, remainder, decimal/string operators, mutation, calls, and
general member reads. The only authored member read is the M4.15
null-conditional simple-member payload, which is revalidated against Roslyn's
exact type before commit. A `for` header adds one clause-scoped
`forLocalReference` leaf that cannot appear outside its containing
`ForPatchStatement`.

The standalone operation deliberately creates empty `then` and `else` blocks.
It proves that source, HIR, Graph branch/data/control edges, persistent identity,
and `.usgraph` layout synchronization share one structure. New branch and
condition nodes are explicitly `unplaced`; existing placements survive.

Patch-plan v0.21 can populate those newly allocated blocks without predicting
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
C# evaluation. Patch v0.21 can connect additional compatible consumers to
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

`unifiedscript.patch-plan/v0.21` composes `replaceLiteral`, `changeOperator`,
`reconnectArgument`, typed replacement of assignment, loop, branch,
declaration-initializer, and return endpoints, their generated-statement
handle variants, `insertStatement`, `insertBranchStatement`,
`insertLoopBodyStatement`, `deleteStatement`, `introduceTemporary`, and
`reconnectValue`, plus exact nested `replaceExpressionInput` and symbolic
`replaceProducedExpressionInput`, as one ordered transaction. Plan expressions may also carry a
transaction-only `producedLocalReference` leaf that resolves an earlier local
producer by `stepId`. The
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

All independently executable target, containing-statement, parent-block,
sibling, and semantic-type preconditions are first checked against the original
plan baseline. A consumer that references a generated local has its baseline,
target method, sidecar, backward source, name, and type checked initially; its
complete endpoint/gap and dominance checks run after that local exists in the
reanalyzed in-memory source. Internal rebasing applies only to changes made by
earlier steps in that same plan. Every stage reruns parsing, Visual Profile
validation, C# compilation, semantic binding, identity reconciliation, and
Graph safety. If any later stage fails,
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
indices by persistent endpoint ID. Direct non-symbolic targets remain unique to
avoid ambiguous repeated old-value preconditions; compatible produced
endpoint/expression consumers may repeat only their producer's stable original
anchor. Reconnect handles must reference an
earlier local producer in the same method with the same graph-safe type. An `if`
insertion publishes symbolic `then` and `else` block handles only through later
`insertBranchStatement` steps; it still is not a local value handle. Branch
locals publish ordinary local handles, but normal dominance rules prevent a
branch-local value from escaping to the outer block or sibling branch. A typed
branch return terminates that symbolic path; later appends to the same path are
rejected before execution. An inserted nested `if` recursively publishes two
more paths, and all-path termination propagates up that producer tree. A
`while`, `do`, `for`, or `foreach` insertion similarly publishes one symbolic body handle through
`insertLoopBodyStatement`; nested if/loop producers compose, while
`break`/`continue` bind only to the nearest lexical loop. A declaration-form
`for` also publishes its single generated declarator as a produced-local
handle, and `foreach` publishes its iteration variable inside the body, usable
only where scope and CFG dominance allow. Local assignments are
supported as direct, produced-branch, and produced-loop-body payloads. Authored
call construction, async/task-like returns, cyclic handle references, and
cross-method handles remain outside plan v0.21; exact inputs of already-resolved
positional calls are covered by M4.10 below.

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
HIR v0.7, Graph v0.12 regions, and nearest-loop CFG facts as a text-authored
loop. See [LOOP_PATCHING.md](docs/LOOP_PATCHING.md).

### Build a for loop from a visual plan

The checked-in M4.17 sample creates a typed declaration-form `for`, uses its
generated `index` local in a nested skip guard and an outer-local assignment,
and proves that `continue` routes through the incrementor:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/ForBuildRules.cs \
  --identity samples/Patching/ForBuildRules.cs.usids \
  --plan samples/Patching/BuildFor.plan.json \
  --out artifacts/for-plan/ForBuildRules.cs \
  --out-identity artifacts/for-plan/ForBuildRules.cs.usids
```

`ForPatchStatement` explicitly separates declaration or assignment
initialization, optional condition, and ordered mutation incrementors. Its
clause-local `forLocalReference` never escapes the payload; after reanalysis,
the plan coordinator publishes the real declarator through an ordinary
`producedLocalReference` handle for body steps. See
[FOR_LOOPS.md](docs/FOR_LOOPS.md).

### Edit an existing for header

M4.18 adds four exact endpoint operations: `replaceForCondition`,
`replaceForDeclarationInitializer`, `replaceForAssignmentInitializerValue`,
and `replaceForIncrementor`. They retain the `for` form and body, pin the
surviving mutation/operand identities where applicable, reject omitted
conditions and missing initializers, and preserve declaration scope and clause
order. A `replaceProducedForCondition` plan step can edit the explicit
condition of a `for` inserted earlier in the same transaction.

The checked-in four-step sample gates a declaration-form condition with
`enabled`, advances both initializer values, and changes postfix `index++` to
prefix `++index`:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/EditForRules.cs \
  --identity samples/Patching/EditForRules.cs.usids \
  --plan samples/Patching/EditForRules.plan.json \
  --out artifacts/for-edit/EditForRules.cs \
  --out-identity artifacts/for-edit/EditForRules.cs.usids
```

The transaction is still bounded: it cannot add/remove/reorder clauses or
switch between declaration and expression initializer forms. See
[FOR_LOOPS.md](docs/FOR_LOOPS.md).

### Build and edit a do-while loop from a visual plan

M4.19 preserves `do ... while` as a post-test loop. Its generated Graph has a
one-time `Do Entry` into the body; body fallthrough and `continue` then enter
the condition, and only a true condition returns to the body. The checked-in
three-stage plan constructs the loop, edits its produced condition, and fills
its body:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/DoWhileBuildRules.cs \
  --identity samples/Patching/DoWhileBuildRules.cs.usids \
  --plan samples/Patching/BuildDoWhile.plan.json \
  --out artifacts/do-while-plan/DoWhileBuildRules.cs \
  --out-identity artifacts/do-while-plan/DoWhileBuildRules.cs.usids
```

The result contains `do { result = result - 1; } while (enabled &&
(result > 0));`. Existing loops use `replaceDoWhileCondition`; generated loops
use `replaceProducedDoWhileCondition` without predicting the new HIR IDs. See
[DO_WHILE_LOOPS.md](docs/DO_WHILE_LOOPS.md).

### Build and edit indexed or enumerable foreach loops

M4.20 preserves indexed `foreach` as its own loop instead of rewriting it to
visible `for` syntax. M4.21 adds graph-safe synchronous `IEnumerable<T>`-backed
collections and records evaluate-once capture, enumerator acquisition,
move-next/current binding, continue routing, and `disposal: finally` explicitly.

The checked-in three-stage plan creates the loop, reconnects its collection
from `primary` to `fallback`, and assigns its produced `value` local in the
generated body:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/ForEachBuildRules.cs \
  --identity samples/Patching/ForEachBuildRules.cs.usids \
  --plan samples/Patching/BuildForEach.plan.json \
  --out artifacts/foreach-plan/ForEachBuildRules.cs \
  --out-identity artifacts/foreach-plan/ForEachBuildRules.cs.usids
```

This produces `foreach (char value in fallback) { result = value; }` as
ordinary C#. The equivalent checked-in `BuildEnumerableForEach.plan.json`
produces `foreach (int value in fallback) { result = value; }` from
`IEnumerable<int>` inputs and reprojects to explicit acquisition plus three
cleanup transfers. M8.5 also accepts graph-safe pattern enumerators and
borrowed `Span<T>`/`ReadOnlySpan<T>` with `ref readonly` iteration, distinguishing
no cleanup, required cleanup, and runtime-conditional cleanup in Semantics and
Graph. Ambiguous element contracts, mutable `ref foreach`, and `await foreach`
remain rejected until the shared VM models their distinct alias/async rules. See
[FOREACH_LOOPS.md](docs/FOREACH_LOOPS.md).

### Edit assignment values and loop conditions

M4.7 adds exact-precondition expression editing without replacing its enclosing
control structure. `replaceAssignmentValue` preserves an existing simple
assignment, statement, and target identity while replacing its typed right-hand
tree. `replaceWhileCondition` preserves an existing while and body while
rebuilding its Boolean condition. Both use the same bounded literal/local/
unary/binary/cast/conditional expression algebra and rerun scope, dominance, type, Graph,
and CFG validation.

The checked-in two-step sample changes `result - 1` to `result - 2` and changes
`result > 0` to `enabled && (result > stopAt)` atomically:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/EditLoopRules.cs \
  --identity samples/Patching/EditLoopRules.cs.usids \
  --plan samples/Patching/EditLoopRules.plan.json \
  --out artifacts/expression-edit/EditLoopRules.cs \
  --out-identity artifacts/expression-edit/EditLoopRules.cs.usids
```

A `replaceProducedWhileCondition` plan step can instead reference an earlier
while-producing step by `sourceStepId`. The coordinator resolves the generated
loop and its current condition after every stage, so repeated edits and later
body population require no predicted intermediate ID. Comments or directives
inside a discarded old expression reject the edit rather than being lost. See
[STRUCTURED_EXPRESSION_EDITING.md](docs/STRUCTURED_EXPRESSION_EDITING.md).

### Edit local initializers, branch conditions, and return values

M4.8 applies the same exact-endpoint and typed-expression rules to the other
high-frequency value ports. `replaceLocalInitializer` preserves the declaration
and local definition, `replaceIfCondition` preserves the branch and both
blocks, and `replaceReturnValue` preserves the terminal statement. Scope,
dominance, exact types, reachability, lossless trivia, Graph, and CFG are all
revalidated at the real source position.

The checked-in three-step sample changes `value + 1` to `value - 2`, expands
`enabled` to `enabled && (result > stopAt)`, and changes `return result` to
`return result + stopAt` atomically:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/EditDecisionValues.cs \
  --identity samples/Patching/EditDecisionValues.cs.usids \
  --plan samples/Patching/EditDecisionValues.plan.json \
  --out artifacts/general-expression-edit/EditDecisionValues.cs \
  --out-identity artifacts/general-expression-edit/EditDecisionValues.cs.usids
```

The corresponding `replaceProducedIfCondition`,
`replaceProducedLocalInitializer`, and `replaceProducedReturnValue` steps use
an earlier insertion's `stepId`, resolve the actual current endpoint after each
reanalysis, and may edit one generated endpoint repeatedly without predicting
IDs. A generated return remains terminal for appends but its value can still be
edited in place. See
[GENERAL_EXPRESSION_EDITING.md](docs/GENERAL_EXPRESSION_EDITING.md).

### Connect expressions to locals generated earlier in a plan

M4.9 adds `producedLocalReference`, a transaction-only expression leaf whose
`sourceStepId` names an earlier `introduceTemporary` or local-literal insertion.
It may be nested inside later unary, binary, or cast trees used by root,
branch, loop-body, direct-endpoint, or generated-endpoint plan steps.

The coordinator resolves the generated declaration's real ID immediately
before the consumer stage and converts the leaf into an ordinary
`localReference`. Existing same-method, exact-name/type, lexical-scope, CFG
dominance, and graph-safety checks then run at the actual source position.
Branch locals therefore cannot escape to an outer or sibling path.

The checked-in four-stage sample inserts `offset`, uses it in a generated guard,
a generated branch return, and the original fallback return:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/ConnectGeneratedValues.cs \
  --identity samples/Patching/ConnectGeneratedValues.cs.usids \
  --plan samples/Patching/ConnectGeneratedValues.plan.json \
  --out artifacts/produced-values/ConnectGeneratedValues.cs \
  --out-identity artifacts/produced-values/ConnectGeneratedValues.cs.usids
```

Standalone patch documents reject this leaf because they have no step
namespace. See
[PRODUCED_VALUE_REFERENCES.md](docs/PRODUCED_VALUE_REFERENCES.md).

### Replace one exact nested expression input

M4.10 maps a visual data-edge edit directly to Graph's existing input-port
vocabulary. `replaceExpressionInput` targets a parent expression, names its
`left`, `right`, `operand`, or positional `argN` port, and repeats the current
child endpoint plus exact input and parent-result types. Binary, bounded unary,
cast, and resolved positional-invocation inputs are supported.

The old input must be effect-free, the replacement must come from the same
closed typed expression algebra, and every referenced local must pass the usual
scope and CFG-dominance checks. Boundary comments move with the replacement;
unsafe internal trivia rejects the edit. The containing statement, parent,
unaffected expression spine, and sibling nodes retain persistent IDs while the
removed child subtree becomes tombstones.

The checked-in plan inserts `offset` and connects it only to the inner
addition's `right` port:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/EditNestedInput.cs \
  --identity samples/Patching/EditNestedInput.cs.usids \
  --plan samples/Patching/EditNestedInput.plan.json \
  --out artifacts/nested-input/EditNestedInput.cs \
  --out-identity artifacts/nested-input/EditNestedInput.cs.usids
```

It changes `return (value + 1) * 3;` into ordinary C# containing
`int offset = 2;` and `return (value + offset) * 3;`. The generated local is
resolved at stage time and then enters the normal standalone input-replacement
path; there is no Graph-only evaluator. See
[EXPRESSION_INPUT_PATCHING.md](docs/EXPRESSION_INPUT_PATCHING.md).

### Edit an expression created by an earlier plan stage

M4.11 adds the plan-only `replaceProducedExpressionInput` step. Its
`sourceStepId` selects an earlier expression-producing stage, `targetPath`
walks that stage's authored tree using canonical `left`/`right`/`operand`
ports, and `inputPort` selects the exact child to replace. The plan never
contains generated expression IDs; the applier captures them only after each
successful reanalysis and resolves the handle in memory.

The checked-in three-stage chain first authors `value + 1`, replaces its
generated `right` input with `value * 2`, then uses that second step as a new
handle and changes its generated `right` input to `3`:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/EditGeneratedExpression.cs \
  --identity samples/Patching/EditGeneratedExpression.cs.usids \
  --plan samples/Patching/EditGeneratedExpression.plan.json \
  --out artifacts/generated-expression/EditGeneratedExpression.cs \
  --out-identity artifacts/generated-expression/EditGeneratedExpression.cs.usids
```

The result is `return value + (value * 3);`, and the sidecar advances from
generation 1 to 4. The symbolic step becomes an ordinary patch v0.23
`replaceExpressionInput` at execution time, so type, purity, dominance, trivia,
identity, tombstone, and Graph-edge behavior stay on one code path. See
[PRODUCED_EXPRESSION_HANDLES.md](docs/PRODUCED_EXPRESSION_HANDLES.md).

### Author and edit a conditional expression

M4.12 adds `condition ? whenTrue : whenFalse` as a first-class expression in
current HIR v0.8, Graph v0.13, patch v0.23, and patch-plan v0.21. Graph emits one
`conditional`/`Select` node with `condition`, `whenTrue`, and `whenFalse` data
inputs, a `value` output, and `evaluation: lazyBranches`. Text lowering keeps
the original expression shape; it does not turn the expression into a call or
an eager select primitive.

Because C# evaluates only the selected arm, assignment, await, mutation, and
effectful calls inside either arm are rejected with `UVS2002`. The condition
may carry effects because it always executes. When an arm needs effects, use an
explicit statement-level `if`/`else`, whose branch regions can sequence them.

The checked-in three-stage plan changes `return value;` into a conditional,
replaces its generated `whenFalse` input with `value + 1`, and then edits the
new binary input to `2`:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/EditConditionalExpression.cs \
  --identity samples/Patching/EditConditionalExpression.cs.usids \
  --plan samples/Patching/EditConditionalExpression.plan.json \
  --out artifacts/conditional-expression/EditConditionalExpression.cs \
  --out-identity artifacts/conditional-expression/EditConditionalExpression.cs.usids
```

The result is `return enabled ? value : (value + 2);`. Exact edits of an
existing conditional use the same canonical ports, endpoint/type preconditions,
stable-pure removal checks, identity pins, and tombstones as every other M4.10
input edit. See
[CONDITIONAL_EXPRESSIONS.md](docs/CONDITIONAL_EXPRESSIONS.md).

### Analyze short-circuit logical expressions

M4.13 makes the evaluation order of `&&` and `||` machine-readable. Their
existing binary nodes retain `left`, `right`, and `value` data ports while
Graph v0.6 introduced `evaluation: shortCircuitRight`; current Graph v0.12
retains it. `rightEvaluation` is
`whenLeftTrue` for `&&` and `whenLeftFalse` for `||`. Ordinary `&` and `|`
remain eager binary operators.

Effects on the left are safe because C# always evaluates that operand and the
projector retains its control chain. Assignment, await, mutation, or an
effectful call anywhere under the conditional right operand produces
`UVS2003`; use explicit statement-level control flow when that behavior is
required.

Generate the checked-in example with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Expressions/ShortCircuitRules.cs \
  --out artifacts/short-circuit
```

See [SHORT_CIRCUIT_EXPRESSIONS.md](docs/SHORT_CIRCUIT_EXPRESSIONS.md) for the
Graph and effect-safety contract.

### Analyze and edit null-coalescing expressions

M4.14 admits `left ?? right` without desugaring it into an eager call or a
Boolean conditional. HIR retains the ordered binary expression. Graph v0.7
introduced `rightEvaluation: whenLeftNull`; current Graph v0.12 retains it and
`resultSelection: leftNonNullOrRight`, so a visual runtime or shared VM can
preserve both evaluation and value selection directly.

The left subtree may carry effects because it always executes exactly once.
An effectful right subtree produces `UVS2004`; effect-catalogued pure calls are
allowed but remain conditionally evaluated. Roslyn supplies text-authored
nullable/result types, then `UVS2005` rejects conversions that are not explicit
in the Graph contract. Visual construction uses the existing binary patch
payload and the same exact graph-safe scalar families such as `T? ?? T` and
`T? ?? T?`, without hidden conversions.

Generate the checked-in example with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Expressions/CoalesceRules.cs \
  --out artifacts/coalesce
```

Existing `left`/`right` input edits and produced-expression handles operate on
the same coalescing tree. See
[NULL_COALESCING_EXPRESSIONS.md](docs/NULL_COALESCING_EXPRESSIONS.md).

### Analyze and edit null-conditional access

M4.15 admits `receiver?.Member` without flattening its implicit receiver into
an ordinary eager member read. Current HIR v0.7 preserves both the full conditional
access and its conditional member binding. Graph v0.8 introduced, and current
Graph v0.12 retains,
`evaluation: conditionalWhenNotNull`,
`whenNotNullEvaluation: whenReceiverNonNull`, and
`resultSelection: whenNotNullOrNull`. It also connects the receiver value to
both the conditional node's `receiver` port and the member binding's implicit
`target` port, so a shared VM can evaluate the receiver once and reuse it.

Receiver effects are valid because the receiver always executes. The selected
subtree is skipped on null, so an assignment, await, mutation, or effectful call
there produces `UVS2006` until Graph has conditional effect subregions.
`UVS2007` rejects void, graph-unsafe, or hidden-conversion result shapes.

Patch v0.21 adds a bounded `conditionalMemberAccess` payload for a simple member
identifier. It emits ordinary C#, reanalyzes it, and verifies every authored
node against Roslyn's exact pre-conversion type. Existing and generated
conditional-access roots expose `receiver` as an exact editable port under
patch-plan v0.21. The conditional `whenNotNull` data port remains read-only for
general edits until a closed call/member-binding payload can preserve its
implicit receiver safely.

Generate the checked-in example with:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/Expressions/NullConditionalRules.cs \
  --out artifacts/null-conditional
```

See [NULL_CONDITIONAL_ACCESS.md](docs/NULL_CONDITIONAL_ACCESS.md) for the HIR,
Graph, type, effect, and visual-editing contract.

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
src/UnifiedScript.Runtime/   CoreCLR invocation contracts and collectible host
src/UnifiedScript.Cli/       Analysis, patching, layout, and execution CLI
tests/UnifiedScript.Tests/   Unit and vertical-slice tests
samples/                     Input programs for manual inspection
prototype/visual-editor/     Clickable authoring-experience prototype
docs/                        Architecture and milestone plans
```

## Scope boundary

This milestone does not include:

- Unreal Engine bindings;
- a UE graph editor;
- persistent engine runtime domains, execution sandboxing, time budgets, async
  scheduling, native managed-debug traps, expression/watch evaluation,
  deeper-frame locals, async stepping, and authenticated remote-machine/DAP transport;
- semantic type binding against UE APIs;
- `.csproj`/MSBuild compilation loading;
- namespace/API allow and deny policies;
- flow-sensitive nullable state in the interchange model;
- general expression/statement synthesis and arbitrary data/control rewiring
  beyond bounded local/assignment/`if`/`while`/`do`/`for`/supported-`foreach`/
  loop-transfer/`return`
  insertion, conditional-expression synthesis, safe deletion, temporary
  introduction, compatible local consumer
  reconnection, and atomic composition of the current patch operations;
- automatic graph placement, layout merge drivers, and editor viewport/session
  state;
- pattern-only or ambiguous enumerators, conditional-disposal enumerators,
  `ref`, or async `foreach`; recursive/list/positional switch patterns, lambdas,
  full exception CFG, or
  general-purpose C# visualization; indexed and exact synchronous
  `IEnumerable<T>` loops are represented losslessly and supported by bounded
  whole-loop visual construction;
- moving/deleting loops, arbitrary loop restructuring, transfer retargeting, or
  whole-`for` clause replacement.

See [ARCHITECTURE.md](docs/ARCHITECTURE.md),
[IDENTITY_SIDECAR.md](docs/IDENTITY_SIDECAR.md),
[GRAPH_LAYOUT.md](docs/GRAPH_LAYOUT.md),
[CONTROL_FLOW_ANALYSIS.md](docs/CONTROL_FLOW_ANALYSIS.md),
[LOCAL_ASSIGNMENT_PATCHING.md](docs/LOCAL_ASSIGNMENT_PATCHING.md),
[NESTED_BRANCH_PATCHING.md](docs/NESTED_BRANCH_PATCHING.md),
[LOOP_FOUNDATION.md](docs/LOOP_FOUNDATION.md),
[FOR_LOOPS.md](docs/FOR_LOOPS.md),
[FOREACH_LOOPS.md](docs/FOREACH_LOOPS.md),
[SWITCH_PATTERNS.md](docs/SWITCH_PATTERNS.md),
[LOOP_PATCHING.md](docs/LOOP_PATCHING.md),
[CONDITIONAL_EXPRESSIONS.md](docs/CONDITIONAL_EXPRESSIONS.md),
[PATCH_PLAN.md](docs/PATCH_PLAN.md),
[EXECUTION_RUNTIME.md](docs/EXECUTION_RUNTIME.md),
[SOURCE_MAP.md](docs/SOURCE_MAP.md),
[BREAKPOINT_RELOAD.md](docs/BREAKPOINT_RELOAD.md),
[DEBUG_ADAPTER.md](docs/DEBUG_ADAPTER.md),
[NATIVE_DEBUG_WORKBENCH.md](docs/NATIVE_DEBUG_WORKBENCH.md),
[RUNTIME_DIAGNOSTICS.md](docs/RUNTIME_DIAGNOSTICS.md),
[VISUAL_AUTHORING_MODEL.md](docs/VISUAL_AUTHORING_MODEL.md),
[M1_ROSLYN_GRAPH_FOUNDATION.md](docs/milestones/M1_ROSLYN_GRAPH_FOUNDATION.md),
[M2_SEMANTIC_BINDING.md](docs/milestones/M2_SEMANTIC_BINDING.md),
[M3_BIDIRECTIONAL_PATCHING.md](docs/milestones/M3_BIDIRECTIONAL_PATCHING.md), and
[M5_CORECLR_EXECUTION.md](docs/milestones/M5_CORECLR_EXECUTION.md)
for the planned sequence.
