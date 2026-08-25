# Roadmap

## M0 — Repository foundation

Outcome: deterministic .NET solution with documented boundaries and automated
verification.

- [x] .NET 10 solution and central package versions
- [x] Core, Semantics, CSharp, Graph, CLI, and Tests projects
- [x] repository validator
- [x] architecture and contribution rules

## M1 — Roslyn-to-graph vertical slice

Outcome: a supported `[Visual]` method emits deterministic HIR and graph JSON,
while unsupported syntax fails with actionable diagnostics.

- [x] deterministic semantic ID primitive
- [x] source spans and diagnostics
- [x] profile v0.2 validator (`while` foundation included)
- [x] declaration/statement/expression HIR
- [x] syntax-to-HIR lowering
- [x] graph schema and projection
- [x] CLI emission
- [x] unit tests and sample
- [x] persistent identity sidecar design
- [x] golden JSON schema fixture

## M2 — Semantic model and graph-safe types

Outcome: Roslyn symbols bind into a language-independent type/effect model.

- [x] primitive and nominal type descriptors
- [x] nullable type annotations
- [x] method overload resolution capture
- [x] graph-safe signature validation
- [x] pure/impure/latent effect descriptors
- [x] deterministic bound symbol references
- [x] deterministic multi-document compilation requests
- [x] cross-file type and overload binding
- [x] versioned method-effect catalog overrides
- [x] semantic configuration identity for references and effect rules
- [ ] flow-sensitive nullability state
- [ ] `.csproj`/MSBuild and NuGet reference loading
- [ ] configurable API allow/deny catalog
- [ ] semantic diagnostics for unsupported APIs

## M3 — Bidirectional source projection

Outcome: graph patches produce localized, format-preserving C# edits.

Status: complete. Broader control/data-flow editing continues under M4.

- [x] versioned semantic patch protocol
- [x] source compilation and semantic configuration preconditions
- [x] localized literal replacement by graph/HIR `SemanticId`
- [x] atomic rejection of stale, invalid, or semantically unsafe patches
- [x] Roslyn syntax construction with trivia/comment preservation
- [x] source-to-graph-to-source literal round-trip tests
- [x] persistent semantic ID sidecar
- [x] structural/shape/context reconciliation with ambiguity diagnostics
- [x] tombstones for deleted semantic nodes
- [x] explicit identity remapping for graph-authored replacements
- [x] unary/binary operator changes with semantic type preconditions
- [x] same-invocation argument reconnection with type/effect guards
- [x] overlapping structural-operation conflict detection
- [x] local-literal statement insertion with parent/sibling/type/name guards
- [x] safe statement deletion with reference/effect guards and tombstones
- [x] exact sibling preconditions for concurrent structural edits
- [x] HIR use/definition and expression-location indexes
- [x] stable pure-expression extraction into a single-evaluation temporary
- [x] structured block dominance for local value availability
- [x] typed existing-local-to-consumer reconnection with effect guards and tombstones
- [x] atomic temporary-introduction and multi-consumer patch plans
- [x] symbolic plan handles for newly allocated local declaration IDs
- [x] in-memory stage rebasing with late-failure rollback
- [x] atomic plan composition of all bounded patch operations
- [x] insertion-produced local handles and structural gap/endpoint rebasing
- [x] versioned scalar graph-authored expression payload
- [x] first typed `if` synthesis with explicit empty branch blocks
- [x] symbolic branch-block handles and ordered local-literal branch-body population
- [x] typed branch return payloads and terminal-step/unreachable guards
- [x] unaffected-node identity pinning for nested staged insertions
- [x] graph layout sidecar with deterministic JSON, orphan recovery, and CLI sync

## M4 — Structured control flow expansion

Outcome: practical gameplay methods are fully visualizable.

- [x] Graph v0.3 regions for functions, blocks, `if`, `then`, and `else`
- [x] exact direct node ownership and explicit/implicit branch preservation
- [x] CFG entry/exit, reachability, dominance, and post-dominance indexes
- [x] structured branch merge/exit facts and SCC reducibility validation
- [x] CFG-checked bounded local assignment and compatible local-value rewiring
- [x] recursive nested branch construction and terminal-path propagation
- [x] explicit `while`, `break`, and `continue` HIR and C# lowering
- [x] Graph v0.4 `loop`/`loopBody` regions and nearest-loop transfer roles
- [x] loop header/body/exit/back-edge CFG facts and target validation
- [x] bounded Graph-to-C# while synthesis and symbolic body population
- [x] nearest-loop break/continue construction with atomic rollback
- [x] exact typed replacement of existing assignment values and while conditions
- [x] repeatable symbolic condition editing for plan-produced while loops
- [x] exact typed replacement of existing local initializers, if conditions,
  and return values
- [x] repeatable symbolic endpoint editing for plan-produced locals, branches,
  and returns
- [x] typed produced-local references inside later plan expression trees
- [x] same-method, scope, and CFG-dominance validation for generated values
- [x] exact nested binary/unary/cast/invocation-argument Graph input replacement
- [x] old-endpoint/type/effect/trivia checks with unaffected-spine identity retention
- [x] symbolic roots and canonical port paths for expressions authored by
  earlier plan stages
- [x] chainable produced-expression input edits lowered to the ordinary patch
  path with atomic rollback
- [x] first-class conditional-expression HIR/Graph/source round trip with
  lazy-branch effect safety and canonical generated-tree ports
- [x] explicit `&&`/`||` short-circuit Graph evaluation with left-effect
  sequencing and effect-safe conditional right operands
- [x] null-coalescing text/HIR/Graph/patch round trip with nullable result
  typing, lazy-right metadata, and produced-expression editing
- [x] first-class null-conditional access/member binding with single receiver
  evaluation, lifted result/effect safety, bounded visual construction, and
  exact receiver/produced-handle editing
- [x] lossless text-authored `for` HIR/Graph projection with explicit
  initializer/body/increment regions, postfix identity, and correct continue
  routing through incrementors
- [x] dedicated bounded `ForPatchStatement` construction with declaration
  scope, clause ordering, and continue-target proofs
- [x] exact existing-for condition, declaration-initializer,
  assignment-initializer-value, and mutation-incrementor editing, including a
  generated-for condition handle
- [x] lossless `do` loop projection with explicit initial-body entry and
  post-body condition/continue CFG routing
- [x] bounded `do` construction, symbolic body population, and exact
  existing/produced condition editing
- [x] indexed `foreach` over strings and one-dimensional zero-based arrays,
  with collection capture, current-value binding, CFG routing, bounded
  construction, produced-local handles, and exact collection editing
- [x] synchronous graph-safe `IEnumerable<T>` foreach with bound enumerator
  protocol, explicit acquisition/current/disposal Graph nodes, cleanup-region
  ownership, normal/return/exception CFG facts, and visual round trip
- [x] exact typed data-edge reconnection for stable pure expression sources,
  with stale-edge/type/effect/scope/dominance rejection and transactional C#
  reanalysis
- [x] typed data-edge reconnection and typed-default disconnection
- [x] automatic temporary extraction for shared computed values
- [x] structured local/branch/loop/return creation and statement deletion from UE
- [ ] arbitrary control-wire rewiring beyond structured region operations
- [ ] pattern-only/conditional-disposal, ref, and async `foreach` variants
- [ ] `switch` and supported patterns
- [ ] deterministic temporary naming

## M5 — CoreCLR execution backend

Outcome: text and graph versions compile to and execute the same IL.

- [x] Roslyn IL-emission host
- [x] deterministic in-memory assembly and portable PDB emission
- [x] reloadable collectible development assembly context
- [x] backend-neutral invocation and execution-result v0.1 contract
- [x] text-authored versus visual-plan-authored execution equivalence proof
- [x] deterministic method/node-to-MethodDef/IL/PDB source map v0.1
- [x] PE/PDB/source-map `emit` and `emit-files` CLI artifact commands
- [x] content-addressed bounded program-image cache
- [x] monotonic collectible reload generations with transactional failure
- [x] shared semantic breakpoint bind/migrate/hit identity protocols
- [x] cooperative CoreCLR statement traps with real pause/resume
- [x] live CLR stack, bounded graph-safe top-frame locals, and step-over
- [x] generation-safe in-process loopback debug transport
- native managed-debug traps, deeper-frame locals, expression evaluation, and
  remote transport framing
- async invocation and cancellation/time-budget contract

## M5.5 — Script asset and authoring experience

Outcome: state and behavior are text/visual/AI-authorable without Blueprint-only
declarations or one-node-per-expression graphs.

- [x] backend-neutral Script Asset and Declaration IR v0.1
- [x] stable state/property identity, type, default, and exposure metadata
- [x] declaration semantic patching and transactional C# round trip
- [x] compact structure regions with inline ordinary expressions
- [x] actual Declaration/Graph artifacts connected to the editor prototype
- [x] coordinate-free AI outline and typed declaration patch preview
- [x] live CoreCLR pause/locals/step-over painted on real semantic statement cards
- [x] sample source workflow with zero required Blueprint variables
- [x] transactional Workbench declaration/body submission and source write-back
- [x] CoreCLR hot reload with semantic selection and breakpoint migration
- [x] external-file conflict rejection and persistent rename identity

## M6.0 — Generic authoring surface

Outcome: every authoring client consumes one versioned, engine-neutral Surface
instead of interpreting Graph or embedding sample behavior.

- [x] dedicated `UnifiedScript.Authoring` project boundary
- [x] deterministic Authoring Surface v0.1 JSON and strict invariants
- [x] Surface v0.3 complete nodes, typed ports, control/data edges, real block
  source IDs, ordered statement IDs, and available parameter type identities
- [x] Declaration IR + Graph projection for multiple assets and functions
- [x] structured if/loop region hierarchy with compact inline expressions
- [x] source spans, state-reference IDs, and debug-target IDs on actions
- [x] schema-driven literal and operator edit capabilities
- [x] CLI Surface artifact emission and authoring workspace v0.2
- [x] Surface-driven Workbench navigation, cards, inspector, AI lens, and debug
- [x] multi-asset/if/while/for sample, tests, and browser verification
- [x] zero sample-specific behavior names in the UI adapter

## M6.1 — Unreal Engine adapter

Outcome: the proven language pipeline becomes usable from UE.

- [x] UE 5.7/5.8-compatible host project and runtime/editor plugin module boundary
- [x] strict deterministic Unreal reflection manifest v0.1
- [x] selected-module `UClass`/property/function extraction commandlet
- [x] C# binding source generator and public `ue-bindings` CLI
- [x] handle-only managed invocation contract
- [x] initial generated-script host component and reflection fixture
- [x] local engine discovery/build/export/binding verification script
- [x] full UE 5.8 C++ compile and live commandlet export smoke test
- [x] native fixture primitive property/function dispatch with stable failures
- [x] managed `IUnrealInvocationBridge` adapter over the native dispatch contract
- [x] versioned in-process native transport for the managed adapter
- [x] generation-checked weak UObject handle table and deletion invalidation
- [x] live native lifecycle Automation test on installed UE 5.8.1
- [x] world-scoped native host and retained bridge-context ownership
- [x] process-wide hostfxr/CoreCLR bootstrap and load-assembly delegate
- [x] managed World entry and collectible per-script generation contexts
- [x] hosted demo assembly loading and generated-wrapper execution on a live UObject
- [x] component-driven auto-registration, Start/Tick/Stop, and real DLL reload
- [x] stable generated Member IDs, ABI v2 transport, and component native fast thunks
- [x] `UUnifiedScriptAsset`, Content Browser/soft-path resolution, and transactional type reload
- [x] automatic native thunk generation for arbitrary public reflected types
- [x] script declaration materialization on the host component
- [x] Slate integration consuming Authoring Surface v0.3, with native
  GraphEditor pins/wires, semantic selection, source preview, auto reload, and
  typed semantic-patch export
- [x] Graph Presentation v1 with compact value leaves, author-facing pin labels,
  dedicated assignment titles, hidden debug chrome, and lossless Surface access
- [x] transactional Slate Apply through Workbench with regenerated
  Declaration/Surface/DLL/PDB artifacts and matching component reload
- [x] transactional occupied-data-input drag rewiring with server-owned commit,
  Surface refresh, and failure rollback
- [x] Graph Authoring v2 Alpha node palette and transaction-first create/delete
- [x] typed-default wire disconnection without mutating compiler-owned links first
- [x] automatic shared-value temporary plan through the Workbench plan endpoint
- [x] reflection-driven readable-property and callable UE API node insertion
  with structured C# output
- [x] per-function `.usgraph` position persistence and atomic sidecar save
- [ ] arbitrary control-wire authoring beyond structured block operations
- [x] self-contained Win64 cook and packaging strategy

## M6.2 — Production Unreal interop and release gates

Outcome: generated gameplay bindings cover practical UE values through a
measured call path and survive packaged Win64 builds.

- [x] restore and freeze the UE 5.8 editor, World demo, .NET, and validator baseline
- [x] freeze independent ABI v3 value/call-frame/table layouts and ownership rules
- [x] generated managed shapes for all scalar widths, FName/FText, enum and
  generation-safe object/class handles
- [x] generated and live-tested native scalar, enum, FVector, FRotator, and
  FTransform direct thunks
- [x] activate ABI v3 with a strongly typed zero-allocation steady-state path
- [x] arrays, sets, maps, variable-size returns, and return plus `out`/`ref`
- [x] placeable Actor host and managed UObject/Actor lifecycle object model
- [x] World-safe Actor overlap delegates/events and latent generation cancellation
- [ ] arbitrary reflected delegate signatures and event proxy generation
- [x] reproducible C++/UnifiedScript benchmark, Puerts comparison gate, and regression thresholds
- [x] Win64 Development/Shipping cook, dependency closure, and packaged smoke test
- [ ] Graph Authoring Beta plus native debug traps, deeper locals, and evaluation
