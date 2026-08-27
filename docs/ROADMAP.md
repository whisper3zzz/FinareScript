# UnifiedScript product roadmap

This document is the canonical execution queue for UnifiedScript. It records
both the evidence-backed foundation and the ordered path from the current
BlockBreaker package to a production alternative to Puerts. Milestone documents
explain individual contracts; this file decides what is worked on next.

## Product north star

UnifiedScript should provide a practical Unreal gameplay scripting product with:

- one semantic program that can be edited as restricted C# or as a Blueprint-like
  visual graph without maintaining two implementations;
- generated, type-safe Unreal bindings instead of project-specific handwritten
  facades;
- World-safe lifetime, reload, event, async, and packaged-build behavior;
- fast scalar calls plus shared snapshots and batched command/query paths for
  frame-critical gameplay;
- reproducible evidence that compares complete workloads with Puerts rather than
  relying on one favorable API microbenchmark.

The goal is not source-level JavaScript compatibility. The product wins by
combining stronger authoring, safer contracts, and lower end-to-end gameplay
cost.

## How this roadmap is executed

1. **Follow the cursor.** Work starts from the first unchecked slice in the
   active milestone below. An older unchecked research item does not preempt the
   cursor unless it blocks that slice.
2. **Finish vertical slices.** A slice includes implementation, generated
   artifacts, proportional tests, user-facing documentation, and reproducible
   evidence. A compile alone is not completion.
3. **Keep the baseline green.** Every product slice must preserve deterministic
   .NET tests, repository validation, the relevant UE build/Automation gates,
   and any affected packaged or performance gate.
4. **Update this file with the code.** Check off only verified work, record a
   short evidence reference, and advance the cursor to one next slice.
5. **Control scope changes.** Crash, data-loss, ABI, and active-engine
   compatibility failures may interrupt the queue. Other ideas go into the
   later milestone that owns them.

When the user asks to "continue" or "continue iterating" without choosing a
different target, it means: implement the first unchecked slice under the
current cursor, validate it, and advance this document.

## Current execution cursor

Last refreshed: **2026-08-27**

- Active milestone: **M8 — Visual/C# Authoring Beta**
- Next slice: **M8.1 — Structured graph editing completeness**
- Primary engine gate: **Unreal Engine 5.8.1 installed build at `D:\UE_5.8`**
- Product proof: **self-contained Win64 BlockBreaker demo**

## Verified baseline

The forward plan starts from this locally reproduced baseline. Results remain
machine-local unless an artifact explicitly says otherwise.

| Gate | Verified result |
| --- | --- |
| Main source | `fe42c97` (`feat: complete M7.3 schema data plane`), plus this M7.4 change set |
| Comparison source | `489ce9b` (`feat: complete full UnifiedScript migration`) |
| Managed tests | 455 / 455 passed |
| Repository validator | 48 / 48 passed |
| UE 5.8.1 native build | passed |
| `UnifiedScript.Unreal` Automation | 13 / 13 passed at the M7.3 onboarding baseline; the migrated BlockBreaker Editor gate also passed |
| BlockBreaker workload | 10,000 frames, three trials, one shot, one hit, two command completions per trial |
| Gameplay boundary | 120,031 fast/CallFrame calls, zero legacy calls |
| Matched local median | UnifiedScript 4,927.191 ms; Puerts 4,759.930 ms; ratio 1.035139; functional parity only, performance claim deferred to M10 |
| Full migration audit | 74 / 74 inventory items, 19 / 19 shared assets, 40 / 40 generated thunks, zero legacy targets |
| Deliverable smoke | 300 frames, one shot, one hit, two command completions, 3,631 fast/CallFrame calls, zero legacy calls |
| Deliverable report | `artifacts/demo/blockbreaker-20260827-165719/demo-delivery.json` |

## Release ladder

| Level | Meaning | State |
| --- | --- | --- |
| R0 foundation | Deterministic language, graph, patch, CoreCLR, and UE adapter contracts | Complete |
| R1 demonstrable alpha | A packaged sample runs meaningful gameplay without Puerts | Complete |
| R2 adoptable alpha | A second UE project can install, generate, run, and package without repository-specific glue | Complete |
| R3 product beta | Practical gameplay API coverage, complete visual/code loop, debugging, and migration workflow | Active — M7 |
| R4 release candidate | Broad performance, soak, packaging, upgrade, and CI evidence | Queued — M10 to M11 |
| R5 1.0 | Versioned SDK and plugin distribution with supported compatibility and migration policy | Queued — M12 |

## Evidence-backed foundation

The completed milestones below are retained as the historical acceptance
record. Their remaining language-completeness items are routed into the forward
product milestones instead of silently competing with the active cursor.

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
- [ ] flow-sensitive nullability state (scheduled in M8.5)
- [x] `.csproj`/MSBuild and NuGet reference loading
- [x] configurable API allow/deny catalog
- [x] semantic diagnostics for unsupported APIs

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
  (scheduled in M8.1)
- [ ] pattern-only/conditional-disposal, ref, and async `foreach` variants
  (scheduled in M8.5)
- [x] lossless text-authored `switch` and supported-pattern HIR/Graph/CFG
  projection with editable section-body ownership
- [x] deterministic shared-value temporary naming from the complete producer
  semantic identity

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
- [ ] native managed-debug traps, deeper-frame locals, expression evaluation,
  and remote transport framing (scheduled in M9.1-M9.3)
- [x] async invocation and cancellation/time-budget contract (delivered in M7.2)

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
  (scheduled in M8.1)
- [x] self-contained Win64 cook and packaging strategy

## M6.2 — Production Unreal interop and release gates

Outcome: generated gameplay bindings cover practical UE values through a
measured call path and survive packaged Win64 builds.

Status: complete for the production interop and packaged-demo baseline. Graph
Authoring Beta and native debugging are deliberately owned by M8 and M9.

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
- [x] deterministic UE binding coverage gate and safe reflected-path fallback
- [x] arbitrary reflected delegate signatures, ABI v4 event proxies, attributed
  lifecycle binding, and Graph/C# Event Entry round-trip
- [x] reproducible C++/UnifiedScript benchmark, Puerts comparison gate, and regression thresholds
- [x] append-compatible generated scalar ABI lane that passes the matched Puerts latency gate
- [x] per-World pre/managed/post phase scheduler on UE tick delegates
- [x] fixed-row command buffer with next-frame double-buffered completion events
- [x] single-boundary line, sphere, and box Batch Trace with World-scoped handles
- [x] BlockBreaker shot migration from trace/damage/emitter facade calls to the gameplay data plane
- [x] Win64 Development/Shipping cook, dependency closure, and packaged smoke test
- [x] self-contained 300-frame deliverable BlockBreaker smoke and hashed delivery report

## M6.3 — Adoptable project alpha

Outcome: a developer can add UnifiedScript to another UE 5.8 project, generate
its contracts, run scripts, and package the project without knowing this
repository's internal build sequence.

Status: **complete**.

### M6.3.1 — One-command project onboarding

- [x] accept an engine root or resolved `EngineAssociation`, a `.uproject`,
  selected modules/types, and an output policy as explicit inputs
- [x] discover and validate the plugin, .NET SDK/runtime, Unreal build tools,
  project modules, and writable generated locations before changing files
- [x] run reflection export, contract-driven managed wrapper and
  native thunk generation, deployment-manifest sync, managed build, and native
  validation through one orchestrated command
- [x] support idempotent `-ValidateOnly` and ordinary apply modes with stable,
  actionable failure categories
- [x] prove the same command against both `HostProject` and BlockBreaker with no
  machine-specific absolute path committed to source

Acceptance: start from a clean copy of either consumer, invoke one documented
command, then pass contract validation, native build, managed startup, and a
second no-diff validation run.

Evidence (2026-08-26): `tools/setup-unreal-project.ps1` passed Apply for both
UE 5.8.1 consumers and a following ValidateOnly run. HostProject passed two UE
builds, 68/68 native contract members, one Script Asset, deterministic C#/C++/
node/coverage outputs, and 10/10 Automation tests. BlockBreaker passed two UE
builds, 43/43 native members, two Script Assets, and deterministic outputs.

### M6.3.2 — Distributable SDK and plugin bundle

- [x] assemble the source plugin, managed runtime, CLI/tooling, schemas,
  templates, and version metadata into one deterministic SDK archive
- [x] separate engine-independent managed payloads from engine-version-specific
  native build output
- [x] verify hashes, required files, ABI/schema compatibility, and accidental
  absolute paths before publishing an archive
- [x] install the archive into a clean project copy and remove any dependency
  on the UnifiedScript repository checkout

Acceptance: the installed SDK passes M6.3.1 and packages a runnable Win64 build
from an isolated directory.

Evidence (2026-08-26): `tools/package-unifiedscript-sdk.ps1` produced the same
SHA-256 on two consecutive `0.1.0-m6.3-alpha.1` builds. Its manifest covers all
payload files plus UE 5.8/.NET 10, ABI v2/v3/v4, and schema compatibility while
the plugin payload contains no native `Binaries` or `Intermediate` output. A
Git-exported BlockBreaker copy with its plugin excluded installed solely from
the SDK archive, passed packaged-layout onboarding (38 reflected types, 43/43
native members, two Script Assets, deterministic outputs), built/cooked/staged/
paked, and passed the runnable 300-frame gate with one shot, one hit, two
gameplay commands, 3,627 fast/call-frame calls, and zero legacy calls.

### M6.3.3 — Project-agnostic sample and templates

- [x] convert `HostProject` into the minimal onboarding fixture while keeping
  BlockBreaker as the realistic gameplay fixture
- [x] provide Script Asset, C# type, project-contract, deployment-manifest, and
  build-target templates with placeholders instead of sample-specific names
- [x] ensure generated code contains no BlockBreaker or fixture behavior in the
  runtime/plugin implementation
- [x] add a first-script walkthrough that ends in editor play and packaged play

Acceptance: a new script type can be created, visually opened, hot reloaded,
and packaged without copying files from either sample by hand.

Evidence (2026-08-26): the SDK-installed `new-unifiedscript-project.ps1`
created a clean `HostScaffold.UnifiedScript.FirstScript` project, project-owned
contract/deployment/onboarding files, one Script Asset, and all Graph/HIR/
semantics/Declaration IR/Surface outputs in an isolated Git export. Packaged-
layout onboarding reflected two project types and generated 16/16 native
members with zero fallback or rejected members. The project-agnostic authoring
launcher initialized stable identities and served a ready workspace containing
that script, while the native Slate surface retained its tested Apply & Reload
path. A clean UE 5.8.1 `BuildCookRun` then built, cooked, paked, staged, and
archived the generated managed assembly and authoring artifacts. The plugin
implementation contains no BlockBreaker, Demo, or DamagePolicy fixture behavior;
the packaged-smoke harness now belongs to the HostProject fixture. The final SDK
archive contains 148 hashed files and reproduced SHA-256
`a2f28a31baa2954e5d27e7fc55771a00cdea5e0443bd4787606bbcfe0ba6dd03`
on two consecutive builds. Main-source regression passed 441/441 managed tests,
41/41 repository invariants, two UE editor builds, deterministic regeneration,
asset deployment, and 10/10 Automation tests.

### M6.3.4 — Alpha acceptance and failure UX

- [x] add a single alpha acceptance runner that aggregates clean generation,
  managed tests, validator, UE build, Automation, package smoke, and reports
- [x] print the failed phase, relevant log, generated artifact, and safe retry
  command instead of requiring manual log archaeology
- [x] publish a versioned machine-readable acceptance report and a concise
  human summary

Acceptance: one report proves or rejects the complete R2 claim. Completing this
slice advances the cursor to M7.

Evidence (2026-08-26): `tools/run-unifiedscript-alpha-acceptance.ps1` first
proved its failure contract with `USACC1001`, a persisted failed JSON/Markdown
pair, the owning phase, and a non-destructive retry. Its final complete UE
5.8.1 run then passed in 135,150 ms and wrote
`artifacts/acceptance/20260826-161229/alpha-acceptance.json` using schema
`unifiedscript.alpha-acceptance/v0.1`. The single report records 441/441 managed
tests, 42/42 repository invariants, 68/68 native contract members, 10/10 Automation tests,
two successful editor builds and deterministic regeneration, followed by a
Development Build/Cook/Stage/Pak/Archive containing 251 files and a packaged
CoreCLR Start/Tick/Stop smoke with exit code zero. Every phase owns a separate
log, output list, duration, exit code, and directly executable safe retry.

## M7 — General gameplay API and runtime completeness

Outcome: generated contracts cover ordinary Unreal gameplay code broadly
enough that BlockBreaker and a second gameplay slice need no handwritten
project facade for supported engine operations.

### M7.1 — Binding coverage matrix

- [x] cover inheritance, interfaces, overloads, default parameters, static
  members, Blueprint metadata, nested structs, enums, object/class/soft handles,
  containers, return values, and `out`/`ref` combinations with golden generation
- [x] generate explicit allow/deny policy and stable diagnostics for unsafe or
  unsupported APIs
- [x] classify every selected reflected member as direct thunk, CallFrame,
  data-plane operation, safe reflection fallback, or rejected-with-reason
- [x] fail CI when selected project coverage regresses or handwritten generated
  dispatch reappears

Evidence (2026-08-26): reflection/API Contract/coverage advanced to v0.3/v0.2/v0.2
with a checked-in matrix for inheritance, interfaces, overloads, defaults,
Blueprint metadata, nested types, enums, soft handles, containers, and
return/ref/out shapes. Exact C#, C++, visual-node, and coverage goldens compile
and compare in the 444/444 managed suite. The live contract classifies 73
members as 22 direct thunks, 49 CallFrame members, two safe delegate fallbacks,
and zero rejected members; all 71 native paths are generated. Stable policy and
`USUE5101`-`USUE5106` baseline diagnostics plus the repository handwritten-
dispatch scan passed 42/42 invariants. UE 5.8.1 completed a clean 3,768-action
Editor build, and the final one-command onboarding report
`artifacts/onboarding/m7-1-20260826.json` passed deterministic regeneration,
both native builds, deployment, and 10/10 Automation in 55,664 ms. That run
also found and fixed source-engine-relative Slate API catalog resolution.

### M7.2 — Services, async, and lifecycle

- [x] add generated World/static services for timers, input, component lookup,
  spawn/destroy, resource and async asset loading, collision queries, and common
  gameplay events
- [x] define task/latent completion, cancellation, timeout, World teardown, and
  hot-reload ownership as one versioned contract
- [x] stress object deletion, level travel, PIE restart, multiple Worlds, and
  generation rollover without stale callbacks or leaked managed contexts

Evidence (2026-08-27): Async Service ABI v5 adds a World-owned fixed request,
completion, borrowed-view, and function-table contract for Delay, async object/
class load, per-request cancellation, game-time timeout, generation cancellation,
and teardown rejection. `UnrealObjectScript` now binds latent work to one
instance/generation scope and cancels it before EndPlay or collectible reload.
The generated product contract expanded from 73 to 98 members: World services
now include input polling, component lookup, spawn/destroy, time, resource load,
visibility trace, and the selected 17-field `FHitResult` shape. Coverage reports
24 direct thunks, 72 CallFrame members, two safe fallbacks, 96 native paths, and
zero rejected members. The 448/448 managed suite and 44/44 validator passed;
UE 5.8.1 completed both native builds, deterministic regeneration, deployment,
CoreCLR startup, and 12/12 Automation in 110,938 ms in
`artifacts/onboarding/m7-2-lifecycle-20260827.json`. The dedicated
`UnifiedScript.Unreal.WorldHost.LifecycleStress` gate then completed eight
non-seamless travel handoffs, eight PIE create/stop cycles, four simultaneous
Worlds reusing eight request IDs each, and sixteen managed generation rollovers.
It found and fixed generation-owned async resource handles surviving reload;
the final `artifacts/ue/lifecycle-stress.json` reports zero stale completions,
pending-request, managed-World, native-context, owned-resource, and retired-ALC
leaks. The standalone lifecycle report passes as an independently reproducible
focused gate. M7.2 is complete and the cursor advances to
M7.3 schema-generated gameplay data-plane layouts.

### M7.3 — Schema-generated gameplay data plane

- [x] generate borrowed snapshot, fixed command, completion event, and batched
  query layouts from versioned schemas instead of handwritten row definitions
- [x] add bounded capacity, overflow telemetry, ordering, borrow lifetime, and
  forward-compatible schema checks
- [x] extend the current Transform/Trace/impact/damage slice to representative
  movement, overlap, attribute, spawn, and query workloads only where profiling
  proves per-call binding is the wrong boundary

Evidence (2026-08-27): one strict v0.1 JSON layout schema at compatible revision
2 now deterministically emits the C++ and C# snapshot, command, completion,
trace, overlap, view, enum, limit, offset, default, telemetry, and fingerprint
contracts. Exact checked-in-output tests and
`generate-unreal-data-plane.ps1 -Check` reject drift. The World runtime bounds
all four lanes, reports cumulative
overflow and execution/order counters, preserves borrowed view/handle lifetime,
and accepts size-guarded forward-compatible transport suffixes. Batch Overlap
adds sphere/box requests without exposing `FOverlapResult`. The matched live
CoreCLR profile ran 1,024 operations across three median samples: movement row
submission measured 0.200x scalar-binding cost, Batch Trace 0.0076x matched
per-call trace cost, and fixed-row loops allocated zero managed bytes. Therefore
movement/overlap/query use the data plane, generic attributes remain generated
bindings, and spawn stays hybrid. The 452/452 managed suite, 47/47 repository
invariants, UE 5.8.1 build, focused Gameplay Data/Workload Profile Automations,
and the reproducible profile report passed. The final one-command onboarding
report `artifacts/onboarding/m7-3-20260827.json` then passed deterministic
regeneration, both native builds, deployment, CoreCLR startup, and 13/13
Automation tests in 73,660 ms. SDK `0.1.0-m7.3-alpha.1` packaging also passed
with the data-plane schema in its hashed manifest. M7.3 is complete and the
cursor advances to M7.4 full BlockBreaker runtime migration.

### M7.4 — Full BlockBreaker runtime migration

- [x] inventory all Puerts scripts, Unreal APIs, delegates, resources, globals,
  lifecycle hooks, and remaining facade behavior
- [x] migrate the complete playable logic through generated contracts or the
  generic data plane; keep sample-only gameplay outside the plugin
- [x] remove the Puerts plugin from the UnifiedScript project and prove editor,
  packaged, functional, and asset-parity gates

Acceptance: BlockBreaker is a whole-project migration proof rather than a
single API or shot-path benchmark, and the coverage report contains no
unexplained manual bridge.

Evidence (2026-08-27): `ue-puerts-inventory` now scans the canonical Puerts
source and target native headers into deterministic, fingerprinted inventory,
coverage, and report schemas. BlockBreaker covers all 74 discovered items: four
scripts, three script types, 13 methods, six lifecycle hooks, four imports, two
Puerts APIs, 29 Unreal APIs, three resources, three global patterns, four async
patterns, two decorators, zero delegates, and one explained native facade.
`ReportManagedTick` remains verification-only for the exact-frame acceptance
counter and process exit; gameplay policy does not cross it. Camera/gun mount,
shot counters, hit counters, and status now use generated bindings or direct
generated property access.

The target comparison commit `489ce9b` removes 154 tracked Puerts/TypeScript
files (about 55 MB) and all forbidden legacy roots while preserving 19/19
non-map gameplay assets byte-for-byte. The project contains exactly two Script
Assets and its nine-type, 40-member contract emits 40/40 native thunks with no
fallback or rejection. The 300-frame Editor gate passed with one shot, one hit,
two commands, 3,631 fast/CallFrame calls, and zero legacy calls. The Development
package `artifacts/demo/blockbreaker-20260827-165719` passed the same gameplay
gate across 249 files and 1,030,599,735 bytes, with zero Puerts/JavaScript/
TypeScript package files. The exact 10,000-frame, three-trial comparison also
kept functional parity and zero legacy calls, but its local median was
4,927.191 ms versus Puerts at 4,759.930 ms (ratio 1.035139); this is not a
performance win, and M10 retains the at-least-5%-faster release gate. The
455/455 managed suite and 48/48 repository invariants pass. M7 is complete and
the cursor advances to M8.1.

## M8 — Visual/C# Authoring Beta

Outcome: developers can implement and maintain practical gameplay from either
C# or the UE graph editor, with each view remaining a lossless projection of
the same semantic program.

### M8.1 — Structured graph editing completeness

- [ ] add `switch` and supported patterns, deterministic temporary naming, and
  the remaining high-value structured control operations
- [ ] support safe control-flow reconnection through region-aware transforms;
  reject irreducible or semantically ambiguous wiring with actionable feedback
- [ ] make create, delete, reconnect, extract, collapse, copy/paste, undo/redo,
  and multi-step edits transactional and identity-stable

Progress (2026-08-27): HIR v0.8 and Graph v0.13 now preserve classic labels,
declaration/type/var/discard/relational/logical patterns, guards, typed pattern
variables, switch sections, nearest-breakable CFG facts, and actionable
unsupported-pattern diagnostics. Section regions target their exact editable
HIR body block and exclude case labels from statement order. The UE palette now
authors `do/while`, bounded `for`, `break`, and `continue`; switch-section
`break` is accepted while an unscoped `continue` is rejected. Automatic shared
values use the complete producer semantic ID instead of a six-character prefix.
The 459/459 managed suite, UE 5.8 editor build, and focused native Authoring
Surface Automation pass. Visual switch-section construction/relabel/reorder,
region-aware control reconnection, and the full edit transaction/undo contract
remain open, so M8.1 is not yet complete.

### M8.2 — Blueprint-grade Unreal experience

- [ ] deliver context-sensitive palette search, favorites/recent nodes, pin
  promotion and defaults, comments, reroute nodes, zoom/minimap, selection, and
  keyboard workflows consistent with UE expectations
- [ ] show concise author-facing node content by default while retaining exact
  type, effect, source, semantic ID, and generated API information on demand
- [ ] provide compiler/contract diagnostics, quick fixes, source navigation,
  diff preview, and failed-transaction rollback in the editor

### M8.3 — Asset, state, event, and API authoring

- [ ] create and edit Script Assets, exposed state, functions, lifecycle hooks,
  reflected events, and API nodes without editing generated JSON
- [ ] drive palette categories, tooltips, defaults, latent markers, thread/World
  restrictions, and deprecation from generated reflection metadata
- [ ] preserve instance overrides and compatible semantic identities across
  rename, reload, source edits, and graph edits

### M8.4 — Whole-game round-trip proof

- [ ] author representative BlockBreaker behavior visually, inspect the emitted
  C#, edit the same logic as C#, and reopen an equivalent graph
- [ ] run semantic, IL, gameplay-result, asset, and packaged-build equivalence
  gates after both directions
- [ ] add conflict and merge tests for external edits, stale surfaces, and
  simultaneous declaration/body changes

### M8.5 — Language-profile closure

- [ ] implement flow-sensitive nullability and the scheduled `foreach` variants
  needed by real gameplay scripts
- [ ] publish the supported Visual C# profile, opaque-code escape boundary, and
  diagnostics for code that cannot be represented visually

Acceptance: Player and Rifle logic can be changed from either surface and hot
reloaded into the same running scene without a second Blueprint behavior source.

## M9 — Debugging, reload, and observability Beta

Outcome: UnifiedScript failures can be diagnosed inside Unreal at the source,
graph, World, generation, and bridge boundaries.

### M9.1 — Native editor debug integration

- [ ] bind semantic breakpoints to current managed generations from the Slate
  graph and C# source
- [ ] implement pause/resume, step in/over/out, call stacks, deeper-frame locals,
  watches, bounded expression evaluation, and breakpoint migration on reload
- [ ] map exceptions and native bridge failures to source span, graph node,
  UObject handle, World, and script generation

### M9.2 — Hot-reload reliability

- [ ] make compile, artifact publication, instance state migration, event
  rebinding, generation swap, rollback, and old-context collection observable
  phases of one transaction
- [ ] soak repeated success/failure reload cycles and detect retained AssemblyLoadContexts,
  UObject handles, delegates, latent work, and stale debug sessions

### M9.3 — Gameplay and bridge profiler

- [ ] attribute time and count to scripts, semantic nodes, World phases, direct
  thunks, CallFrame calls, reflection fallback, batch queries, commands, events,
  allocations, and buffer pressure
- [ ] expose frame timeline and aggregate views in Unreal and emit a portable
  machine-readable trace for regression comparison

Acceptance: a scripted gameplay defect and a performance regression can both be
located to a source/graph node and runtime boundary without custom logging.

## M10 — Performance architecture and honest Puerts comparison

Outcome: UnifiedScript is faster on representative end-to-end gameplay, not
only on a selected scalar call.

### M10.1 — Workload suite

- [ ] version workloads for scalar/property calls, struct/container transfer,
  delegate storms, per-frame Actor logic, mass entities, batch queries,
  resource/spawn flows, hot reload, and the complete BlockBreaker project
- [ ] capture median, p95/p99 frame time, bridge time, calls, bytes copied,
  allocations, GC, memory high-water mark, startup, reload, and package size
- [ ] alternate engines and trials, freeze inputs/assets/build configuration,
  record machine fingerprints, and reject unmatched comparisons

### M10.2 — Shared data and batching optimization

- [ ] use profiler evidence to promote only high-volume stable data into
  versioned shared snapshots, command/event buffers, or batch APIs
- [ ] preserve World ownership, generation checks, bounds, deterministic phase
  visibility, and fallback semantics; managed code never retains raw UE pointers
- [ ] make schemas generated, extensible, measurable, and debuggable rather than
  accumulating one-off shared-memory structs

### M10.3 — Performance budgets

- [ ] keep zero steady-state managed allocation on generated fixed-value fast
  paths and published data-plane loops
- [ ] prevent regressions in the existing ABI and BlockBreaker gates, with
  explicit reviewed threshold changes rather than silently moving targets
- [ ] require the weighted matched gameplay suite to beat Puerts by at least 5%
  before making the broad product claim; publish neutral or losing results too

Acceptance: the performance report explains where UnifiedScript wins or loses,
and the broad claim is derived from the versioned suite rather than one API.

## M11 — Production hardening and release candidate

Outcome: the same versioned product survives real project scale, upgrades,
long sessions, and supported build configurations.

### M11.1 — Reliability and scale

- [ ] run long PIE, travel, multi-World, dedicated-server, reload, GC, object
  churn, delegate, command-buffer saturation, and fault-injection soaks
- [ ] enforce bounded memory/queues, deterministic shutdown, failure containment,
  and useful crash breadcrumbs
- [ ] test large contracts and script sets for generation time, editor startup,
  incremental rebuild, and asset churn

### M11.2 — Compatibility and packaging matrix

- [ ] keep UE 5.8.1 as the primary gate and validate each additionally supported
  engine/platform/configuration explicitly
- [ ] gate Editor, Development, Shipping, cook, stage, pak, archive, installed
  SDK, and clean-machine runtime dependency closure
- [ ] add Linux dedicated server and other platforms only when their CoreCLR,
  ABI, cook, and lifecycle gates are real; do not imply support from compilation

### M11.3 — Versioning, upgrades, and CI

- [ ] publish compatibility policy and migrations for schemas, artifacts,
  bindings, Script Assets, ABI tables, saved graph layout, and generated code
- [ ] add clean CI lanes for managed tests, deterministic generation, UE build,
  Automation, package smoke, workload performance, and SDK installation
- [ ] support diagnostics bundles and reproducible reports without collecting
  project source or sensitive content by default

Acceptance: an RC archive installs and upgrades in clean project copies, passes
the declared matrix, and survives the soak budget with no unresolved blocker.

## M12 — Migration ecosystem and UnifiedScript 1.0

Outcome: teams can evaluate, migrate, ship, and maintain UnifiedScript without
requiring repository authors to operate their project.

### M12.1 — Puerts migration workflow

- [ ] scan a Puerts project into an inventory of modules, APIs, lifecycle hooks,
  assets, delegates, async patterns, and unsupported dynamic behavior
- [ ] generate a migration plan, API coverage report, stubs/contracts, and
  side-by-side verification harness; do not promise blind one-click conversion
- [ ] support incremental coexistence during migration while preventing both
  runtimes from owning the same gameplay lifecycle accidentally

### M12.2 — SDK, documentation, and samples

- [ ] publish versioned plugin/SDK archives, release notes, upgrade guides,
  onboarding, authoring, API, debugging, performance, packaging, and migration docs
- [ ] ship minimal, gameplay, networking/server, data-plane, and visual/code
  round-trip samples with automated acceptance reports
- [ ] provide project templates and an API/diagnostic browser generated from the
  same contracts consumed by the compiler and editor

### M12.3 — Beyond-Puerts differentiators

- [ ] make semantic graph/C# diffs reviewable and mergeable without serialized
  Blueprint graph noise
- [ ] let AI tools propose typed semantic patch plans that pass the same compiler
  and transaction gates as human graph edits
- [ ] integrate graph/source debugging and profiling so authoring, runtime cost,
  and Unreal API boundaries are visible in one place
- [ ] keep the generated data-plane architecture open to new workloads without
  weakening safety or ABI compatibility

Acceptance: the 1.0 release has an installable SDK, supported compatibility
matrix, end-to-end migration guide, complete samples, published workload report,
and no critical acceptance failure.

## Global release gates

Every active milestone inherits these gates when relevant:

- deterministic artifacts: a no-change rerun produces no generated diff;
- semantic safety: graph and C# edits compile to the same intended program and
  stale edits fail atomically;
- runtime safety: no raw UObject pointer crosses as durable managed identity;
- lifecycle safety: World teardown, reload, delegates, async work, and buffers
  cannot target a retired generation;
- packaging: supported builds contain every managed/native dependency and do
  not depend on a source checkout;
- performance honesty: only matched, reproducible artifacts support comparative
  claims;
- documentation: commands and reported guarantees match the validated product.

## Explicit non-goals

- reproducing JavaScript semantics or every Puerts dynamic pattern;
- treating Blueprint assets as a second authoritative implementation;
- exposing UE memory or pointers to managed code for benchmark convenience;
- allowing arbitrary visual control flow that cannot round-trip safely;
- claiming engine or platform compatibility that has not passed its build,
  lifecycle, cook/package, and smoke gates;
- optimizing every reflected API into shared memory without workload evidence.
