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

Last refreshed: **2026-08-28**

- Active milestone: **M10 — Performance architecture and honest Puerts comparison**
- Next slice: **M10.2 — generated shared container snapshots and buffered events**
- Primary engine gate: **Unreal Engine 5.8.1 installed build at `D:\UE_5.8`**
- Product proof: **self-contained Win64 BlockBreaker demo**

## Verified baseline

The forward plan starts from this locally reproduced baseline. Results remain
machine-local unless an artifact explicitly says otherwise.

| Gate | Verified result |
| --- | --- |
| Runtime source baseline | `9f9f026` (`feat: complete representative workload collectors`) |
| UnifiedScript comparison source | `e0c7110` (`feat: sync current unifiedscript runtime into demo`) |
| Puerts comparison source | `5612b59` (`feat: benchmark puerts reload generations`) |
| Managed tests | 542 / 542 passed |
| Repository validator | 66 / 66 passed |
| UE 5.8.1 native build | passed |
| `UnifiedScript.Unreal` Automation | 16 / 16 passed, including workload collectors, gameplay-profiler attribution, Authoring Surface, live World semantic pause/step/reload/detach, and repeated success/failure hot-reload leak coverage |
| Representative workload suite | 54 / 54 scheduled captures; nine workloads, three trials, two runtimes, matched machine/build identity, alternating runtime order |
| BlockBreaker workload | 10,000 frames, three trials, one shot, one hit, two command completions per trial |
| Gameplay boundary | generated ABI v3 fast/CallFrame path, zero legacy calls |
| Matched BlockBreaker frame median | UnifiedScript 0.5824 ms; Puerts 0.64795 ms; ratio 0.898835; p95/p99 ratios 0.955283 / 0.959456 |
| Matched BlockBreaker bridge median | UnifiedScript 175.4974 ms; Puerts 522.2836 ms; ratio 0.336019; calls and logical bytes are both 20% of Puerts |
| Matched package size | UnifiedScript 1,032,716,821 bytes; Puerts 1,009,698,201 bytes; ratio 1.022798 |
| Full migration audit | 74 / 74 inventory items, 19 / 19 shared assets, 40 / 40 generated thunks, zero legacy targets |
| Deliverable smoke | 300 frames, one shot, one hit, two command completions, 3,631 fast/CallFrame calls, zero legacy calls |
| Deliverable report | `artifacts/demo/blockbreaker-20260827-165719/demo-delivery.json` |
| Visual/C# round trip | semantic, MethodDef IL, execution cases, Script Assets, two live UE candidates, and two packaged candidates equivalent |
| Round-trip report | `artifacts/roundtrip/blockbreaker-20260828-021802-6305602d/roundtrip-report.json` |

## Release ladder

| Level | Meaning | State |
| --- | --- | --- |
| R0 foundation | Deterministic language, graph, patch, CoreCLR, and UE adapter contracts | Complete |
| R1 demonstrable alpha | A packaged sample runs meaningful gameplay without Puerts | Complete |
| R2 adoptable alpha | A second UE project can install, generate, run, and package without repository-specific glue | Complete |
| R3 product beta | Practical gameplay API coverage, complete visual/code loop, debugging, and migration workflow | Active — M8 |
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
- [x] flow-sensitive nullability state
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
- [x] live CLR stack, bounded graph-safe per-frame locals, step in/over/out, and
  read-only snapshot evaluation
- [x] generation-safe in-process loopback debug transport
- [x] live UE World cooperative semantic traps and GameThread-safe bridge return
- [x] bounded versioned remote debug transport framing (delivered in M9.3)
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

- [x] add `switch` and supported patterns, deterministic temporary naming, and
  the remaining high-value structured control operations
- [x] support safe control-flow reconnection through region-aware transforms;
  reject irreducible or semantically ambiguous wiring with actionable feedback
- [x] make create, delete, reconnect, extract, collapse, copy/paste, and
  undo/redo transactional and identity-stable
- [x] group several author-selected edits into one named atomic transaction

Progress (2026-08-27): HIR v0.8 and Graph v0.13 now preserve classic labels,
declaration/type/var/discard/relational/logical patterns, guards, typed pattern
variables, switch sections, nearest-breakable CFG facts, and actionable
unsupported-pattern diagnostics. Section regions target their exact editable
HIR body block and exclude case labels from statement order. The UE palette now
authors `do/while`, bounded `for`, `break`, and `continue`; switch-section
`break` is accepted while an unscoped `continue` is rejected. Automatic shared
values use the complete producer semantic ID instead of a six-character prefix.
The control-reconnection path maps a single-path control-pin drag to Semantic
Patch v0.22 `moveStatement` instead of mutating raw edges. It supports same-block
reordering plus ordinary variable/expression statement movement across explicit
function, then/else, loop, and switch-section bodies. Exact source/destination
block and sibling preconditions reject stale surfaces; method-wide `.usids`
pinning preserves nested identity and comments; local ordering, terminal,
function-boundary, compilation, and reducible-CFG gates reject unsafe moves with
actionable diagnostics. Entire branch/loop/switch statements and
return/break/continue remain owned by explicit structured transforms. The Slate
client keeps the old compiler-owned wire until the Workbench commits. Semantic
Patch v0.23 now adds typed `switch` construction plus preconditioned section
insert/relabel/reorder/delete operations across constant, declaration, type,
var, discard, relational, logical, negated, and parenthesized patterns. Direct
statement insertion/deletion/reordering also accepts a switch-section body.
Authoring Surface v0.4 carries normalized Graph properties, including the exact
semantic switch-section identity, so Slate can consume these operations without
parsing presentation text. The Slate palette now creates a typed switch, and
its inspector converts
bounded pattern text into semantic labels, inserts Case/Default sections,
relabels whole sections, moves them by exact sibling gaps, and routes both its
button and Delete-key paths through safe section deletion. Workspace v0.3 now
places accepted declaration, semantic, plan, Undo, and Redo operations behind
one baseline-revision and idempotent-operation gate. A bounded 100-entry history
restores exact stabilized source/identity snapshots, republishes every artifact,
and advances both workspace revision and CoreCLR generation. Browser and Slate
consume the same server history and never maintain a competing edit stack.
Semantic Patch v0.24 now adds a bounded identity-safe copy/paste transaction.
The Slate GraphEditor maps Copy/Paste to a compiler-owned statement identity
and an exact same-block destination gap; the C# applier pins the original
method subtree, gives the copy fresh IDs, preserves comments, generates a
conflict-free name for copied locals, recompiles, and validates the projected
CFG. Ctrl/Cmd+Z/Y use the same Workbench history as the toolbar. User-grouped
history, collapse/expand transforms, and broader multi-selection or cross-region
clipboard semantics remain open, so M8.1 is not yet complete.

Semantic Patch v0.25 now adds reversible lexical scope transforms. Collapse
Scope consumes two or more consecutive selected statement nodes from one
compiler-owned block and creates a real nested C# block; member/comment IDs are
pinned while the wrapper receives a fresh region ID. The tree inspector exposes
Expand Scope for that nested BLOCK and restores its ordered members while
tombstoning only the wrapper. Full compilation and CFG validation reject scope
escape or name-collision changes. This closes the bounded region
collapse/expand slice, but extracted callable subgraphs, user-grouped history,
and wider clipboard semantics remain before M8.1 completion.

Authoring History v0.1 now persists the 100-entry server-owned Undo/Redo graph,
monotonic workspace revision, transaction labels, exact multi-source/identity
snapshots, and operation replay guard across Workbench and UE editor restarts.
The prospective journal is written atomically with C#, `.usids`, Declaration
IR, Surface, DLL, and PDB. Restore validates source/identity/configuration and
snapshot/chain digests; stale or corrupt journals remain untouched, start a
fresh revision-1 history, and surface a rejected state in browser and Slate.
Historical snapshots reanalyze lazily on navigation. This closes persisted
history; user-defined grouping, callable extraction, and wider clipboard
semantics still keep M8.1 open. The 486/486 managed suite, UE 5.8 editor build,
focused native Authoring Surface Automation, and 49/49 repository invariants
pass.

Semantic Patch v0.26 now closes same-method range clipboard semantics. Slate
copies one consecutive multi-selection and can paste it after an ordinary
statement in another explicit structured region. The transaction carries exact
source outer gaps and a distinct destination gap; originals keep their IDs,
copies receive fresh IDs, direct copied locals and their bound references are
renamed together, and external local/loop/pattern references must dominate the
destination. Cross-function transport remains owned by callable extraction,
where parameter, result, visibility, and effect contracts can be explicit.
User-defined grouping and callable extraction are now the remaining M8.1 slice.
The 489/489 managed suite, UE 5.8 editor build, focused native Authoring Surface
Automation, and 49/49 repository invariants pass.

Semantic Patch v0.27 now closes bounded callable extraction. Slate sends an
ordered same-block selection, exact outer gaps, containing method identity, and
the requested name. Roslyn infers up to 16 graph-safe inputs and zero or one
live result, creates a private static/non-static `[Visual]` method matching its
caller, and keeps moved identities stable while assigning fresh identities to
the new declaration, parameters, body, and call site. Caller-visible extracted
locals retain their original identity at the call site. Async/await, escaping
return/break/continue, directives, name conflicts, unsafe types, and multiple
live outputs reject before any write. User-defined transaction grouping is now
the only remaining M8.1 slice. The 495/495 managed suite, UE 5.8 editor build,
focused native Authoring Surface Automation, and 49/49 repository invariants
pass.

Workspace v0.4 now closes user-defined grouping and completes M8.1. Browser and
Slate can begin one named server-owned draft, stage successive declaration,
semantic, and patch-plan edits against regenerated draft Surfaces, then commit
the final snapshot through exactly one source/identity/artifact/history/CoreCLR
publication or cancel it with no writes. Stages use unique replay IDs but create
no public history or runtime generations; commit creates one named Undo entry.
Nested/mismatched groups, stale draft revisions, duplicate stages, empty commit,
ordinary writes, history navigation, and debug start during a draft are rejected.
The cursor advances to M8.2 after the 497/497 managed suite, UE 5.8 editor build,
focused native Authoring Surface Automation, and 49/49 repository invariants
pass.

### M8.2 — Blueprint-grade Unreal experience

- [x] deliver context-sensitive palette search, favorites/recent nodes, pin
  promotion and defaults, comments, reroute nodes, zoom/minimap, selection, and
  keyboard workflows consistent with UE expectations
  - [x] context-derived token search, persistent Favorites, accepted Recent
    actions, and `Ctrl+Space` / Enter / double-click placement
  - [x] source-backed typed Pin Default/Reset and safe Promote to Local Details
    editors
  - [x] comment/reroute authoring, minimap, viewport state, and common
    Blueprint keyboard parity
- [x] show concise author-facing node content by default while retaining exact
  type, effect, source, semantic ID, and generated API information on demand
- [x] provide compiler/contract diagnostics, quick fixes, source navigation,
  diff preview, and failed-transaction rollback in the editor

The first M8.2 tranche replaces the right-click-only discovery path with a
docked native Palette below **My Script**. Its action set is rebuilt from the
active function, bound semantic types, selected source block, loop/switch
ancestry, and generated UE reflection catalog. Search covers titles,
categories, tooltips, stable action IDs, and explicit contract keywords.
Favorites and the bounded eight-entry Recent list are stored in per-project user
config; Recent changes only after an accepted Workbench response. Function-level
Event Entries remain visible without an editable block, while statement actions
do not. The focused UE 5.8 Authoring Surface Automation and 50/50 repository
invariants pass. Later M8.2 tranches completed pin defaults/promotion, canvas
organization, compact/advanced node information, and the final
diagnostics/Diff/rollback safety loop recorded below.

The second M8.2 tranche adds source-backed Pin Authoring. Every connected data
input in native Details can replace its exact compiler-owned edge with a typed
C# literal, reset through a bounded semantic default, or promote the feeding
stable pure expression to a conflict-free local declaration. Semantic Patch
v0.28 carries the edge/endpoints/ports/type plus the closed authored expression;
the Roslyn path validates literal runtime type, source freshness, effect order,
scope, and identity before the ordinary Workbench transaction can publish.
Promote reuses `introduceTemporary` and is deliberately limited to explicit
local initializers and returns where evaluation order is proven unchanged. See
[`PIN_AUTHORING.md`](PIN_AUTHORING.md). The 501/501 managed suite, UE 5.8 Editor
build, focused native Authoring Surface Automation, and 51/51 repository
invariants pass.

The third M8.2 tranche completes native Canvas Authoring without creating a
second executable graph. Unreal Comment Boxes and typed wire Reroutes persist
as `.usgraph` v0.1 comment regions and ordered routing hints; reroute deletion
reconnects the semantic edge and compacts later point indices. A live
bottom-right minimap renders nodes, comments, routed segments, and the visible
viewport, and click/drag navigation centers the GraphEditor. View location,
zoom, and minimap visibility are stored per project/user, keyed by Surface path
and function ID, so personal navigation never causes sidecar conflicts. `C`,
`F`, and `Home` provide comment, focus-selection, and focus-all parity. See
[`CANVAS_AUTHORING.md`](CANVAS_AUTHORING.md). The 501/501 managed suite, UE 5.8
Editor build, focused native Authoring Surface Automation, and 52/52 repository
invariants pass.

The fourth M8.2 tranche separates authoring content from compiler diagnostics.
Cards now use bounded author-facing subtitles: Event Entries show their C#
handler signature instead of the canonical `/Script/...` path, and member reads
show their receiver without repeating the member name. Hover retains the exact
kind, raw effects, graph-safety flag, resolved type/Type ID, port contract,
generated method/member/event identity, source span, and semantic ID. Selecting
an action keeps ordinary authoring controls visible and puts the complete sorted
contract, declaration references, edit capabilities, and source preview inside
an initially collapsed **Advanced Node Details** area. See
[`NODE_DETAILS.md`](NODE_DETAILS.md). The 501/501 managed suite, UE 5.8 Editor
build, focused native Authoring Surface Automation, and 53/53 repository
invariants pass. M8.2 now advances to compiler diagnostics, safe quick fixes,
diff preview, source navigation, and explicit failed-transaction rollback UX.

The fifth M8.2 tranche closes that safety loop. New preview endpoints run the
same declaration/semantic/plan/history/group candidate preparation as commit
without writes and return exact per-source before/after text. Slate requires an
author confirmation and resubmits a SHA-256 preview token; the server rejects a
candidate mismatch before publication. Structured compiler/contract
diagnostics now retain code, severity, semantic identity, and source span in a
native panel with bounded Open Source and read-only Refresh Surface actions.
Every authoring-protocol failure also carries an explicit `unchanged`,
`restored`, or `failed` receipt with the active revision/generation, so the UI
never infers rollback from HTTP status. Group stages preview only the next
draft, while group commit previews the accumulated published delta. See
[`AUTHORING_DIAGNOSTICS.md`](AUTHORING_DIAGNOSTICS.md). The 502/502 managed
suite, UE 5.8 Editor build, focused native Authoring Surface Automation, and
54/54 repository invariants pass. M8.2 is complete and the cursor advances to
M8.3 asset, state, event, and API authoring.

### M8.3 — Asset, state, event, and API authoring

- [x] create and edit Script Assets, exposed state, functions, lifecycle hooks,
  reflected events, and API nodes without editing generated JSON
- [x] drive palette categories, tooltips, defaults, latent markers, thread/World
  restrictions, and deprecation from generated reflection metadata
- [x] preserve instance overrides and compatible semantic identities across
  rename, reload, source edits, and graph edits

The first M8.3 tranche added native create/update/remove workflows for Script
Assets, exposed state, Visual functions, lifecycle hooks, reflected Event
Entries, and API actions through previewed source transactions. The second
tranche upgrades reflection to v0.4 and the API node catalog to v0.2: native
Blueprint metadata now deterministically drives title, category, tooltip,
defaults, deprecation, latent, thread, and World-context policy in Slate. The
checked-in Demo proves a real reflected default through `AddWithDefault`; latent
and current void-statement actions remain visible with exact unavailable
reasons instead of being silently dropped. The final tranche separates
declaration defaults from a serialized set of explicit instance override IDs.
Unreal now inherits edited defaults for untouched fields, preserves compatible
overrides through state renames, removes stale or incompatible overrides, and
restores the prior bag/override set on failed Reload. A managed execution test
reuses the same state-ID override across a declaration graph rename, body graph
edit, external C# rename, and four CoreCLR generations. The 507/507 managed
suite, 54/54 repository invariants, UE 5.8.1 Editor build, and full 13/13 native
Automation suite pass. M8.3 is complete and the cursor advances to M8.4
whole-game round-trip proof. See
[`STATE_OVERRIDE_MIGRATION.md`](STATE_OVERRIDE_MIGRATION.md).

### M8.4 — Whole-game round-trip proof

- [x] author representative BlockBreaker behavior visually, inspect the emitted
  C#, edit the same logic as C#, and reopen an equivalent graph
- [x] run semantic, IL, gameplay-result, asset, and packaged-build equivalence
  gates after both directions
- [x] add conflict and merge tests for external edits, stale surfaces, and
  simultaneous declaration/body changes

Evidence (2026-08-28): the pinned comparison baseline `b1d3de3` exposes real
Player `ShouldFire` and Rifle `ResolveDamage` Visual methods. One full-project
patch changes their stable Graph literals, emits both C# sources, and reconciles
their identity sidecars. Ordinary C# local renames and equivalent lexical
float spellings then reopen as equivalent Graphs at comparison commit
`e62a46e`. `verify-roundtrip` independently passes stable method identity,
bound-semantic digest, exact MethodDef IL, boundary execution cases, and Script
Asset declaration equivalence. The dual-candidate UE 5.8.1 run passes 300 frames,
one shot, one hit, two gameplay command completions, 3,631 fast/CallFrame calls,
zero legacy calls, and byte-identical Player/Rifle Script Assets on both sides.
Both candidates independently Build/Cook/Stage/Pak/Archive 249 files
(1,030,617,655 bytes), contain zero Puerts/TypeScript files, and pass the same
packaged smoke. Workbench regression now rejects an in-group stale Surface with
`UAW1005`, atomically merges declaration and method-body stages, and rejects an
external post-preview edit with `UAW1002` without partially publishing source,
identity, history, artifacts, or runtime generation. The 509/509 managed suite
and 55/55 repository invariants pass. M8.4 is complete and the cursor advances
to M8.5. See [`BLOCKBREAKER_ROUND_TRIP.md`](BLOCKBREAKER_ROUND_TRIP.md).

### M8.5 — Language-profile closure

- [x] implement flow-sensitive nullability and the scheduled `foreach` variants
  needed by real gameplay scripts
- [x] publish the supported Visual C# profile, opaque-code escape boundary, and
  diagnostics for code that cannot be represented visually

Progress (2026-08-28): semantics v0.6 carries Roslyn's exact per-expression
`NullFlowState` independently from type nullability. Graph and Authoring Surface
actions publish validated `typeNullability`/`nullFlowState` properties, and the
round-trip semantic digest includes the flow result. Regression proves a
nullable parameter is `notNull` after an early-return guard and returns to
`maybeNull` after assignment.

HIR v0.9 and semantics v0.6 now also preserve `value` versus `ref readonly`
iteration, scoped `Span<T>`/`ReadOnlySpan<T>` borrowed sequences, graph-safe
pattern enumerators, and exact `none`/`required`/`conditional` disposal. Graph
publishes copied versus borrowed-read-only value semantics and creates cleanup
regions only when C# actually owns disposal. Exact collection editing preserves
the alias mode. `await foreach` over graph-safe `IAsyncEnumerable<T>` now
preserves the exact async enumerator protocol, awaited move-next, and required
or conditional `DisposeAsync` cleanup through Semantics, Graph, strict JSON,
derived CFG, and exact collection replacement. Mutable `ref` iteration and
borrowed async iteration are deliberate profile exclusions rather than implicit
gaps.

Visual C# profile v0.3 and report v0.1 now classify `[Visual]` bodies as
bidirectionally graph-editable and unmarked helpers as source-only opaque code.
Opaque calls project as one validated node, require graph-safe value signatures,
and reject unsupported boundaries with `UVS2009`. CLI analysis publishes the
profile even when visual lowering fails; authoring workspace v0.5, the browser
prototype, and native Slate Workbench validate and display the same boundary.
The 522/522 managed suite and 59/59 repository invariants pass for this slice.
See
[`NULLABILITY_FLOW.md`](NULLABILITY_FLOW.md) and
[`FOREACH_LOOPS.md`](FOREACH_LOOPS.md), and
[`VISUAL_CSHARP_PROFILE.md`](VISUAL_CSHARP_PROFILE.md).

Acceptance: Player and Rifle logic can be changed from either surface and hot
reloaded into the same running scene without a second Blueprint behavior source.

## M9 — Debugging, reload, and observability Beta

Outcome: UnifiedScript failures can be diagnosed inside Unreal at the source,
graph, World, generation, and bridge boundaries.

### M9.1 — Native editor debug integration

- [x] bind semantic breakpoints to current managed generations from the Slate
  graph and C# source
- [x] implement pause/resume, step in/over/out, call stacks, deeper-frame locals,
  watches, bounded expression evaluation, and breakpoint migration on reload
- [x] map exceptions and native bridge failures to source span, graph node,
  UObject handle, World, and script generation

Progress (2026-08-28): the cooperative CoreCLR control plane and browser
workbench now use debug-session v0.2 with non-blocking start/wait, cooperative
Pause, Step In/Over/Out, semantic method/node stack identities, detached locals
for every active Visual frame, and bounded read-only watch evaluation. Native
Slate now consumes the same guarded protocol: graph-card and source-inspector
breakpoints survive graph rebuilds, binding state is generation-specific, all
run controls are native, stack selection navigates semantic graph nodes, and
per-frame locals/watch values remain detached. UE 5.8.1 Editor build, targeted
Authoring Surface Automation, 522/522 managed tests, and 60/60 repository
invariants passed that slice. Semantic statement controls still drive the
loopback generation; attaching those probes to live World execution remains
unfinished, so this milestone is not yet marked complete.

Progress (2026-08-28, live World diagnostics): the Slate debugger now discovers
the matching hosted PIE/Game component and exposes exact World host, script
instance, collectible generation, and UObject handles. Pause/Step Frame/
Continue operate at a non-blocking managed tick boundary. Managed lifecycle
exceptions and native bridge failures cross the host as bounded
`runtime-diagnostic/v0.1` records with exact bridge/handle categories, member
identity, portable-PDB frames, and zero `uint64` JSON precision loss. Native
Runtime parses and retains the record; Slate maps all source frames to the
smallest matching Authoring Surface span, highlights the `DebugTargetId`, and
navigates through Focus Node. Managed 524/524, UE WorldHost lifecycle/stress,
native Authoring Surface mapping, UE 5.8.1 build, and repository 61/61 gates
cover the slice. Live semantic statement suspension still requires a transport
that can preserve the CLR stack without blocking Unreal's Game Thread.

Progress (2026-08-28, live World semantic completion): `debug-serve` now
publishes a generation-guarded instrumented PE/PDB through
`/api/debug/publish-live-world`. Slate reloads the selected PIE/Game component,
binds its graph/source `DebugTargetId` set to the new runtime generation, and
uses the same `debug-session/v0.2` pause, stack, per-frame locals, and control
model as loopback execution. `OnTick` runs on a named debugger worker; statement
traps preserve the real CLR stack while worker-originated generated UObject
calls enter an MPSC queue that the World GameThread pumps. Reload, stop, detach,
and World teardown abort and drain the worker before collectible-context
disposal. Native state reads retry a concurrent running-to-paused size change,
and the script ALC shares the probe runtime with instrumented images.

The checked-in Demo now contains real nested `[Visual]` tick work plus a stable
identity sidecar. Managed HTTP coverage proves stale-generation rejection and
instrumented PE/PDB publication without source mutation. UE World Automation
proves exact statement breakpoints, non-blocking GameThread start, Step In/Out,
two-frame locals, pause-unique frame IDs, generated worker-to-GameThread bridge
calls, generation-two breakpoint rebinding, and detach while suspended. M9.1 is
complete; async state-machine traps remain later work, while process framing
was deferred explicitly to and completed in M9.3.

### M9.2 — Hot-reload reliability

- [x] make compile, artifact publication, instance state migration, event
  rebinding, generation swap, rollback, and old-context collection observable
  phases of one transaction
- [x] soak repeated success/failure reload cycles and detect retained AssemblyLoadContexts,
  UObject handles, delegates, latent work, and stale debug sessions

Evidence (2026-08-28): authoring compile/publication and live World
debug-drain/configuration/state migration/generation load-stop-start/event
rebind/swap/state commit/context retirement and collection now extend one
`unifiedscript.reload-transaction/v0.1` receipt. Compile and publication
failures retain or restore prior artifacts; runtime failures preserve or restart
the old generation and restore the captured state bag. Slate success status and
failure diagnostics consume the same structured phase list. The v0.2 UE
lifecycle soak executes 16 successful and 16 intentionally failed reloads while
holding state, one reflected Host delegate, timers, and the active generation;
it finishes with zero registered-object, delegate, pending-work, native-context,
owned-resource, stale-debug-session, and retired-AssemblyLoadContext deltas.
Managed tests pass 526/526, repository validation passes 63/63, the UE 5.8.1
Editor build passes, and `UnifiedScript.Unreal` Automation passes 14/14. M9.2 is
complete; the cursor advances to M9.3.

### M9.3 — Gameplay and bridge profiler

- [x] attribute time and count to scripts, semantic nodes, World phases, direct
  thunks, CallFrame calls, reflection fallback, batch queries, commands, events,
  allocations, and buffer pressure
- [x] expose frame timeline and aggregate views in Unreal and emit a portable
  machine-readable trace for regression comparison
- [x] add bounded versioned remote debug framing, then reproduce one gameplay
  defect and one performance regression and locate both to the responsible
  source/graph node and runtime boundary

Evidence (2026-08-28): the opt-in managed profiler now owns a bounded 120-frame
ring and emits `unifiedscript.gameplay-profile/v0.3`. Each frame and aggregate
separates script duration/allocation, stable semantic method/node timing, all
three World scheduler phases, direct thunk, CallFrame, reflection fallback,
Command/Event, Batch Trace/Overlap, and buffer-pressure deltas. Gameplay Data
schema revision 3 appends cumulative phase count/nanosecond telemetry without
moving the revision-1/2 prefixes. The native parser rejects incompatible or
unbounded captures; Slate shows the aggregate/latest-frame summary, focuses the
hottest node that resolves in the active graph, and saves exact JSON under
`Saved/UnifiedScript/Profiles`. Each semantic node also retains the bridge and
data-plane counters accumulated while it is active. One live World workload
exercises every attribution family.

The process control plane uses `unifiedscript.debug-frame/v0.1`: a fixed
24-byte `USDB` header, 1 MiB pre-allocation payload cap, bounded compilation and
invocation collections, correlated request IDs, stable `UDBF...` failures, and
one concurrent loopback TCP connection per operation. `debug-frame-serve`
publishes its actual endpoint and loaded generation. A real TCP gate holds a
wait-for-pause request open while independent Pause and Continue operations
complete.

The reproducible `ue-profile-diagnose` fixture joins a detached live-debug local
observation, compiler source spans, and baseline/current per-node profiles. It
locates the wrong `damage` value at the exact Return source/graph node and
`reflectionFallback` boundary, and separately locates a 5.00x Batch Trace
regression at the exact `traceBudget` declaration node and `batchTrace`
boundary. The report is `unifiedscript.gameplay-diagnosis/v0.1` and contains no
custom gameplay logging. Managed tests pass 534/534, repository validation
passes 65/65, the UE 5.8.1 Editor build passes, and `UnifiedScript.Unreal`
Automation passes 15/15. M9.3 is
complete; the cursor advances to M10.1.

Acceptance: a scripted gameplay defect and a performance regression can both be
located to a source/graph node and runtime boundary without custom logging.

## M10 — Performance architecture and honest Puerts comparison

Outcome: UnifiedScript is faster on representative end-to-end gameplay, not
only on a selected scalar call.

### M10.1 — Workload suite

- [x] version workloads for scalar/property calls, struct/container transfer,
  delegate storms, per-frame Actor logic, mass entities, batch queries,
  resource/spawn flows, hot reload, and the complete BlockBreaker project
- [x] capture median, p95/p99 frame time, bridge time, calls, bytes copied,
  allocations, GC, memory high-water mark, startup, reload, and package size
- [x] alternate engines and trials, freeze inputs/assets/build configuration,
  record machine fingerprints, and reject unmatched comparisons

Completed (2026-08-28): suite v1.0.0 produced all 54 scheduled captures under
one UE 5.8.1 machine/build fingerprint and the frozen runtime identities
`sha256:73607b...a7a76b` (UnifiedScript) and
`sha256:4e5f66...76cf0d` (Puerts). Each runtime assembled exactly 27 trials; the
comparison verified all nine workload definitions, three-trial medians, and the
odd/even alternating order. The checked-in schema rejects missing, duplicate,
stale, dirty-mislabeled, mismatched-environment, or misordered evidence.

The formal result is deliberately mixed. UnifiedScript wins the per-frame Actor
(p50 ratio 0.342857), 1,000-entity (0.346390), Batch Trace (bridge ratio
0.330878), and complete BlockBreaker workload (p50/p95/p99 ratios
0.898835/0.955283/0.959456). In BlockBreaker, generated batching reduces bridge
time to 33.60%, calls to 20%, and logical copied bytes to 20% of Puerts, with
zero measured runtime GC collections in the window. It loses scalar calls
(1.211389), struct/container transfer (6.734380), four-argument delegate
dispatch (3.800531), and resource load/spawn/destroy (1.642611). UnifiedScript's
matched package is 2.28% larger and startup is 5.13% slower in the complete
workload. Allocation values stay ratio-free because CLR cumulative thread
allocation and V8 used-heap growth are different collectors. Reload remains a
mechanism-qualified diagnostic because collectible assembly generation reload
and Puerts module-cache reexecution are not equivalent.

This closes M10.1 without making the M10.3 broad-product claim. The measured
losses make generated container snapshots and buffered delegate/event ingress
the first M10.2 optimization slice; direct generated calls remain the safe
fallback while data-plane schemas preserve World/generation ownership.

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
